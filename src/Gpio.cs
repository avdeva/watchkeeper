// Axia GPIO to vMix: the service keeps a Livewire Routing Protocol (LWRP) connection to each
// Axia device (GPIO node, xNode, console engine, IP-Audio driver; TCP port 93), subscribes to
// its GPI and GPO ports, and turns pin changes into vMix Web API calls or any HTTP request.
//
// LWRP lines used:  ADD GPI / ADD GPO (subscribe; the device answers with the current state of
// every port between BEGIN and END), then "GPI <port> <pins>" / "GPO <port> <pins>" on every
// change. <pins> has one letter per pin: l/L = low = active (closed), h/H = high, x = unchanged.
// "GPO <port> CMD:"text"" carries a custom command message. VER is sent as a keep-alive.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Watchkeeper
{
    // ------------------------------------------------------------------ settings
    public class GpioCfg
    {
        /// <summary>Connect to the devices and run the triggers.</summary>
        public bool Enabled = false;
        public List<AxiaDevice> Devices = new List<AxiaDevice>();
        public List<VmixTarget> Vmix = new List<VmixTarget>();
        public List<GpioTrigger> Triggers = new List<GpioTrigger>();
        /// <summary>Alert when a device has been unreachable this long.</summary>
        public int DisconnectAlertSeconds = 60;
    }

    public class AxiaDevice
    {
        public string Id = "";
        public string Name = "Axia GPIO";
        public bool Enabled = true;
        public string Host = "";
        public int Port = 93;
        /// <summary>Send LOGIN after connecting (only some devices need it to report GPIO).</summary>
        public bool Login = false;
        /// <summary>LWRP password, stored encrypted like the SMTP password. Empty = LOGIN without password.</summary>
        public string Password = "";
        public string Notes = "";
    }

    public class VmixTarget
    {
        public string Id = "";
        public string Name = "vMix (this server)";
        public string Host = "127.0.0.1";
        public int Port = 8088;
        public string User = "";
        public string Password = "";
        /// <summary>Send the login over plain http to another computer (the vMix Web Controller offers http only).</summary>
        public bool AllowPlainLogin = false;
        public int TimeoutMs = 3000;
    }

    public class GpioAction
    {
        public string Type = "vmix";            // vmix | http
        public string VmixId = "";              // empty = the first vMix
        public string Function = "Cut";
        public string Input = "";
        public string Value = "";
        public string Duration = "";
        public string SelectedName = "";
        public string Mix = "";
        public string Extra = "";               // more parameters: Name=Value&Name2=Value2
        public string Method = "GET";
        public string Url = "";
        public string Body = "";
        public string ContentType = "application/json";
        /// <summary>HTTP basic login for the address, if it needs one. The password is stored encrypted.</summary>
        public string User = "";
        public string Password = "";
        /// <summary>Send the login over plain http to another computer.</summary>
        public bool AllowPlainLogin = false;
        public int DelayMs = 0;                 // wait before this action
    }

    public class GpioTrigger
    {
        public string Id = "";
        public string Name = "Trigger";
        public bool Enabled = true;
        public string DeviceId = "";
        public string Signal = "gpi";           // gpi (input pins) | gpo (output pins) | cmd (custom command message)
        public int Port = 1;
        public int Pin = 1;                     // 1-5
        /// <summary>Normally a pin is "on" when it is low (contact closed). Inverted: on when high.</summary>
        public bool Invert = false;
        public string Command = "";             // cmd: the text after CMD:
        public int IgnoreRepeatMs = 0;          // ignore the same change again within this time
        public List<GpioAction> OnActive = new List<GpioAction>();
        public List<GpioAction> OnInactive = new List<GpioAction>();
        public string Notes = "";
    }

    // -------------------------------------------------------------------- status
    public class GpioPortState
    {
        public string DeviceId = "";
        public string Signal = "";              // gpi | gpo
        public int Port;
        public string Pins = "";                // one character per pin: 1 = on (low), 0 = off, ? = unknown
        public double Changed;
    }

    public class GpioDeviceStatus
    {
        public string Id = "";
        public string Name = "";
        public string Host = "";
        public string State = "off";            // off | connecting | connected | error
        public string Text = "";
        public double Since;
        public double LastLine;
        public string Version = "";
        public List<GpioPortState> Ports = new List<GpioPortState>();
    }

    public class GpioEvent
    {
        public long Seq;
        public double Time;
        public string DeviceId = "";
        public string Device = "";
        public string Signal = "";
        public int Port;
        public int Pin;
        public string Edge = "";                // on | off | cmd
        public string Text = "";                // command text
        public bool Simulated;
        public List<string> Triggers = new List<string>();
        public string Result = "";
    }

    public class GpioStatus
    {
        public bool Enabled;
        public List<GpioDeviceStatus> Devices = new List<GpioDeviceStatus>();
        public List<GpioEvent> Events = new List<GpioEvent>();
    }

    public class ActionResult
    {
        public bool Ok;
        public string Text = "";
        public double Ms;
    }

    public class VmixInput
    {
        public int Number;
        public string Title = "";
        public string Key = "";
        public string Type = "";
    }

    public class VmixInfo
    {
        public bool Ok;
        public string Error = "";
        public string Version = "";
        public string Edition = "";
        public string Active = "";
        public string Preview = "";
        public List<VmixInput> Inputs = new List<VmixInput>();
    }

    // ---------------------------------------------------------------- vMix calls
    public static class Vmix
    {
        static string Esc(string s) { return Uri.EscapeDataString(s ?? ""); }

        public static string Url(VmixTarget t, GpioAction a)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("http://").Append(t.Host.Trim()).Append(':').Append(t.Port).Append("/api/?Function=").Append(Esc(a.Function.Trim()));
            if (a.Input.Trim().Length > 0) sb.Append("&Input=").Append(Esc(a.Input.Trim()));
            if (a.Value.Length > 0) sb.Append("&Value=").Append(Esc(a.Value));
            if (a.Duration.Trim().Length > 0) sb.Append("&Duration=").Append(Esc(a.Duration.Trim()));
            if (a.SelectedName.Trim().Length > 0) sb.Append("&SelectedName=").Append(Esc(a.SelectedName.Trim()));
            if (a.Mix.Trim().Length > 0) sb.Append("&Mix=").Append(Esc(a.Mix.Trim()));
            foreach (string part in (a.Extra ?? "").Split('&'))
            {
                string p = part.Trim();
                if (p.Length == 0) continue;
                int eq = p.IndexOf('=');
                if (eq <= 0) sb.Append('&').Append(Esc(p));
                else sb.Append('&').Append(Esc(p.Substring(0, eq).Trim())).Append('=').Append(Esc(p.Substring(eq + 1)));
            }
            return sb.ToString();
        }

        static HttpRequestSpec Request(VmixTarget t, string url)
        {
            HttpRequestSpec s = new HttpRequestSpec();
            s.Url = url;
            s.Proxy = "none";
            s.User = t.User;
            s.Password = t.User.Length > 0 ? Secrets.Unprotect(t.Password) : "";
            s.AllowPlainLogin = t.AllowPlainLogin;
            s.TimeoutMs = Math.Max(500, t.TimeoutMs);
            s.MaxBody = 500;
            return s;
        }

        public static ActionResult Call(VmixTarget t, GpioAction a)
        {
            ActionResult r = new ActionResult();
            HttpResult h = Net.Send(Request(t, Url(t, a)), null);
            r.Ms = h.Seconds * 1000;
            string what = "vMix " + t.Name + ": " + a.Function + (a.Input.Trim().Length > 0 ? " Input=" + a.Input.Trim() : "") +
                          (a.Value.Length > 0 ? " Value=" + a.Value : "");
            if (!h.Answered)
            {
                r.Text = what + " - no answer: " + h.Error + " Is vMix running with Settings > Web Controller switched on (port " + t.Port + ")?";
                return r;
            }
            if (h.Status == 401) { r.Text = what + " - vMix asks for a login: set the user name and password of the Web Controller."; return r; }
            if (!h.Success)
            {
                r.Text = what + " - vMix answered HTTP " + h.Status + (h.Body.Length > 0 ? ": " + Net.OneLine(h.Body, 200) : "") +
                         " (check the function name and its input).";
                return r;
            }
            r.Ok = true;
            r.Text = what + " - done (" + r.Ms.ToString("0", Util.Inv) + " ms)";
            return r;
        }

        static readonly Regex Attr = new Regex("(\\w+)=\"([^\"]*)\"");

        static string Tag(string xml, string name)
        {
            Match m = Regex.Match(xml, "<" + name + ">([^<]*)</" + name + ">", RegexOptions.IgnoreCase);
            return m.Success ? Decode(m.Groups[1].Value) : "";
        }

        static string Decode(string s)
        {
            return s.Replace("&quot;", "\"").Replace("&apos;", "'").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");
        }

        /// <summary>Parse the XML vMix returns from /api.</summary>
        public static VmixInfo Parse(string xml)
        {
            VmixInfo info = new VmixInfo();
            info.Version = Tag(xml, "version");
            info.Edition = Tag(xml, "edition");
            info.Active = Tag(xml, "active");
            info.Preview = Tag(xml, "preview");
            int start = xml.IndexOf("<inputs>", StringComparison.OrdinalIgnoreCase);
            int end = xml.IndexOf("</inputs>", StringComparison.OrdinalIgnoreCase);
            string inputs = start >= 0 && end > start ? xml.Substring(start, end - start) : "";
            foreach (Match m in Regex.Matches(inputs, "<input\\s([^>]*)>", RegexOptions.IgnoreCase))
            {
                VmixInput i = new VmixInput();
                foreach (Match a in Attr.Matches(m.Groups[1].Value))
                {
                    string k = a.Groups[1].Value.ToLowerInvariant(), v = Decode(a.Groups[2].Value);
                    if (k == "number") int.TryParse(v, out i.Number);
                    else if (k == "title") i.Title = v;
                    else if (k == "key") i.Key = v;
                    else if (k == "type") i.Type = v;
                }
                if (i.Number > 0) info.Inputs.Add(i);
            }
            info.Ok = info.Version.Length > 0 || info.Inputs.Count > 0;
            if (!info.Ok) info.Error = "the answer does not look like vMix (no version or inputs)";
            return info;
        }

        public static VmixInfo State(VmixTarget t)
        {
            HttpRequestSpec s = Request(t, "http://" + t.Host.Trim() + ":" + t.Port + "/api");
            s.MaxBody = 2000000;
            HttpResult h = Net.Send(s, null);
            if (!h.Answered)
            {
                VmixInfo bad = new VmixInfo();
                bad.Error = "no answer from " + t.Host + ":" + t.Port + ": " + h.Error + " Is vMix running with Settings > Web Controller switched on?";
                return bad;
            }
            if (h.Status == 401)
            {
                VmixInfo bad = new VmixInfo();
                bad.Error = "vMix asks for a login: set the Web Controller user name and password.";
                return bad;
            }
            if (!h.Success)
            {
                VmixInfo bad = new VmixInfo();
                bad.Error = "HTTP " + h.Status + " from " + t.Host + ":" + t.Port;
                return bad;
            }
            return Parse(h.Body);
        }

        /// <summary>Common vMix functions and what their parameters mean (shown in the manager).</summary>
        public static readonly string[][] Functions =
        {
            new[] { "Cut", "Cut to Input (or to the preview when Input is empty)." },
            new[] { "Fade", "Fade to Input (or the preview). Duration in ms." },
            new[] { "Merge", "Merge to Input (or the preview). Duration in ms." },
            new[] { "Stinger1", "Stinger 1 transition to Input (or the preview)." },
            new[] { "Stinger2", "Stinger 2 transition to Input (or the preview)." },
            new[] { "CutDirect", "Cut straight to Input without touching the preview." },
            new[] { "PreviewInput", "Put Input in preview." },
            new[] { "ActiveInput", "Put Input on air (program)." },
            new[] { "OverlayInput1In", "Overlay 1 on with Input. Also OverlayInput2In ... OverlayInput4In." },
            new[] { "OverlayInput1Out", "Overlay 1 off. Also OverlayInput2Out ... OverlayInput4Out." },
            new[] { "OverlayInput1", "Overlay 1 toggle with Input." },
            new[] { "OverlayInput2In", "Overlay 2 on with Input." },
            new[] { "OverlayInput2Out", "Overlay 2 off." },
            new[] { "OverlayInput3In", "Overlay 3 on with Input." },
            new[] { "OverlayInput3Out", "Overlay 3 off." },
            new[] { "OverlayInput4In", "Overlay 4 on with Input." },
            new[] { "OverlayInput4Out", "Overlay 4 off." },
            new[] { "FadeToBlack", "Fade to black (toggle)." },
            new[] { "Play", "Play Input." },
            new[] { "Pause", "Pause Input." },
            new[] { "PlayPause", "Play or pause Input." },
            new[] { "Restart", "Restart Input from the beginning." },
            new[] { "AudioOn", "Audio of Input on." },
            new[] { "AudioOff", "Audio of Input off." },
            new[] { "AudioOnOff", "Audio of Input on/off toggle." },
            new[] { "AudioBusOn", "Send Input to a bus: Value = bus (M, A, B, C, D, E, F, G)." },
            new[] { "AudioBusOff", "Remove Input from a bus: Value = bus (M, A, ... G)." },
            new[] { "SetVolume", "Volume of Input: Value 0-100." },
            new[] { "SetVolumeFade", "Fade volume of Input: Value = volume,milliseconds (e.g. 0,2000)." },
            new[] { "MasterAudioON", "Master audio on." },
            new[] { "MasterAudioOFF", "Master audio off." },
            new[] { "BusXAudioOn", "Bus audio on: Value = bus letter (A ... G)." },
            new[] { "BusXAudioOff", "Bus audio off: Value = bus letter (A ... G)." },
            new[] { "StartRecording", "Start recording." },
            new[] { "StopRecording", "Stop recording." },
            new[] { "StartStopRecording", "Recording on/off toggle." },
            new[] { "StartStreaming", "Start streaming. Value = stream number (0-2), empty = all." },
            new[] { "StopStreaming", "Stop streaming. Value = stream number, empty = all." },
            new[] { "StartMultiCorder", "Start MultiCorder." },
            new[] { "StopMultiCorder", "Stop MultiCorder." },
            new[] { "StartExternal", "Start external output." },
            new[] { "StopExternal", "Stop external output." },
            new[] { "SetText", "Text of a title Input: SelectedName = field (e.g. Headline.Text), Value = text." },
            new[] { "SetImage", "Image of a title Input: SelectedName = field, Value = file path or URL." },
            new[] { "SelectIndex", "Select item Value (1 = first) in a list Input." },
            new[] { "NextItem", "Next item in a list Input." },
            new[] { "PreviousItem", "Previous item in a list Input." },
            new[] { "ScriptStart", "Start the vMix script named in Value." },
            new[] { "ScriptStop", "Stop the vMix script named in Value." },
            new[] { "SnapshotInput", "Save a snapshot of Input." },
            new[] { "ReplayMarkIn", "Replay: mark in." },
            new[] { "ReplayMarkOut", "Replay: mark out." },
            new[] { "ReplayPlay", "Replay: play." },
            new[] { "KeyPress", "Press the vMix shortcut key in Value." },
        };

        public static string Hint(string function)
        {
            foreach (string[] f in Functions) if (string.Equals(f[0], (function ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) return f[1];
            return "Any vMix function works; see the vMix shortcut function reference for its parameters.";
        }
    }

    // ------------------------------------------------------------------- engine
    public class GpioService
    {
        readonly Func<AppConfig> config;
        readonly SourceLogger log;
        readonly object lk = new object();
        readonly Dictionary<string, DeviceLink> links = new Dictionary<string, DeviceLink>();
        readonly Dictionary<string, PortMem> ports = new Dictionary<string, PortMem>();
        readonly Dictionary<string, double> lastFire = new Dictionary<string, double>();
        readonly List<GpioEvent> events = new List<GpioEvent>();
        readonly Queue<Job> queue = new Queue<Job>();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        Thread manager, worker;
        long seq;

        /// <summary>(kind, key, title, message) - see AlertManager.</summary>
        public Action<string, string, string, string> Alert;
        public int ReconcileMs = 1000;

        class PortMem
        {
            public char[] Active = { '?', '?', '?', '?', '?' };
            public double Changed;
        }

        class Job
        {
            public GpioEvent Event;
            public GpioTrigger Trigger;
            public List<GpioAction> Actions;
            public List<VmixTarget> Vmix;
        }

        public GpioService(Func<AppConfig> config, LogHub hub)
        {
            this.config = config;
            this.log = hub.Logger("gpio");
        }

        bool Wanted(AppConfig c) { return c.Gpio.Enabled && c.General.Modules.Gpio; }

        public void Start()
        {
            if (manager != null) return;
            stop.Reset();
            manager = new Thread(Manage);
            manager.Name = "gpio";
            manager.IsBackground = true;
            manager.Start();
            worker = new Thread(Work);
            worker.Name = "gpio-actions";
            worker.IsBackground = true;
            worker.Start();
        }

        public void Stop()
        {
            stop.Set();
            wake.Set();
            lock (lk)
            {
                foreach (DeviceLink l in links.Values) l.Stop();
                links.Clear();
            }
        }

        void RaiseAlert(string key, string title, string message) { RaiseAlert("gpio_problem", key, title, message); }

        void RaiseAlert(string kind, string key, string title, string message)
        {
            Action<string, string, string, string> a = Alert;
            if (a == null) return;
            try { a(kind, key, title, message); } catch (Exception) { }
        }

        readonly HashSet<string> failedTriggers = new HashSet<string>();

        // -------------------------------------------------------- device links
        void Manage()
        {
            while (!stop.WaitOne(ReconcileMs))
            {
                try { Reconcile(); }
                catch (Exception ex) { log.Error("GPIO: " + ex.Message); }
            }
        }

        /// <summary>Start, stop or replace device connections so they match the configuration.</summary>
        public void Reconcile()
        {
            AppConfig cfg = config();
            bool on = Wanted(cfg);
            Dictionary<string, AxiaDevice> want = new Dictionary<string, AxiaDevice>();
            if (on) foreach (AxiaDevice d in cfg.Gpio.Devices) if (d.Enabled && d.Host.Length > 0) want[d.Id] = d;
            List<DeviceLink> stopNow = new List<DeviceLink>();
            lock (lk)
            {
                foreach (KeyValuePair<string, DeviceLink> kv in new List<KeyValuePair<string, DeviceLink>>(links))
                {
                    AxiaDevice d;
                    if (!want.TryGetValue(kv.Key, out d) || kv.Value.Signature != Signature(d))
                    {
                        stopNow.Add(kv.Value);
                        links.Remove(kv.Key);
                    }
                }
                foreach (AxiaDevice d in want.Values)
                {
                    if (links.ContainsKey(d.Id)) continue;
                    DeviceLink l = new DeviceLink(this, d, Signature(d));
                    links[d.Id] = l;
                    l.Start();
                }
            }
            foreach (DeviceLink l in stopNow) l.Stop();

            // unreachable for too long -> one alert per outage
            double now = Util.Now();
            lock (lk)
            {
                foreach (DeviceLink l in links.Values)
                {
                    if (l.State == "connected")
                    {
                        if (l.Alerted)
                            RaiseAlert("recovered", "gpio:" + l.Device.Id, "Axia device " + l.Device.Name + " reachable again",
                                       "The Axia device " + l.Device.Name + " (" + l.Device.Host + ":" + l.Device.Port + ") is connected again.");
                        l.Alerted = false;
                        continue;
                    }
                    if (!l.Alerted && now - l.DownSince >= Math.Max(10, cfg.Gpio.DisconnectAlertSeconds))
                    {
                        l.Alerted = true;
                        RaiseAlert("gpio:" + l.Device.Id, "Axia device " + l.Device.Name + " unreachable",
                                   "The Axia device " + l.Device.Name + " (" + l.Device.Host + ":" + l.Device.Port + ") has not been reachable for " +
                                   (int)(now - l.DownSince) + " seconds: " + l.Text + "\n\nGPIO triggers from it do nothing until it is back.");
                    }
                }
            }
        }

        static string Signature(AxiaDevice d)
        {
            return d.Host + "|" + d.Port + "|" + d.Login + "|" + d.Password + "|" + d.Name;
        }

        class DeviceLink
        {
            readonly GpioService owner;
            public readonly AxiaDevice Device;
            public readonly string Signature;
            readonly ManualResetEvent stopped = new ManualResetEvent(false);
            Thread thread;
            TcpClient client;
            public string State = "connecting";
            public string Text = "Connecting";
            public double Since = Util.Now();
            public double DownSince = Util.Now();
            public double LastLine;
            public string Version = "";
            public bool Alerted;

            public DeviceLink(GpioService owner, AxiaDevice d, string sig)
            {
                this.owner = owner;
                Device = d;
                Signature = sig;
            }

            public void Start()
            {
                thread = new Thread(Run);
                thread.Name = "lwrp-" + Device.Host;
                thread.IsBackground = true;
                thread.Start();
            }

            public void Stop()
            {
                stopped.Set();
                try { if (client != null) client.Close(); } catch (Exception) { }
            }

            void SetState(string state, string text)
            {
                lock (owner.lk)
                {
                    if (state != State) Since = Util.Now();
                    if (state != "connected" && State == "connected") DownSince = Util.Now();
                    State = state;
                    Text = text;
                }
            }

            void Run()
            {
                int backoff = 2000;
                while (!stopped.WaitOne(0))
                {
                    try
                    {
                        Session();
                        backoff = 2000;
                    }
                    catch (Exception ex)
                    {
                        if (stopped.WaitOne(0)) break;
                        string msg = ex is NetFailure ? ex.Message : "connection lost: " + ex.Message;
                        bool wasUp = State == "connected";
                        SetState("error", msg);
                        if (wasUp) owner.log.Warn(Device.Name + " (" + Device.Host + "): " + msg);
                        else owner.log.Debug(Device.Name + " (" + Device.Host + "): " + msg);
                    }
                    finally
                    {
                        try { if (client != null) client.Close(); } catch (Exception) { }
                        client = null;
                    }
                    if (stopped.WaitOne(backoff)) break;
                    backoff = Math.Min(30000, backoff * 2);
                }
                SetState("off", "Stopped");
            }

            void Send(Stream s, string line)
            {
                byte[] b = Encoding.ASCII.GetBytes(line + "\r\n");
                s.Write(b, 0, b.Length);
                s.Flush();
            }

            void Session()
            {
                SetState("connecting", "Connecting to " + Device.Host + ":" + Device.Port);
                client = Net.Connect(Device.Host, Device.Port, 5000, null);
                NetworkStream s = client.GetStream();
                try { client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true); } catch (Exception) { }
                if (Device.Login)
                {
                    string pw = Secrets.Unprotect(Device.Password);
                    Send(s, pw.Length > 0 ? "LOGIN " + pw : "LOGIN");
                }
                Send(s, "VER");
                Send(s, "ADD GPI");
                Send(s, "ADD GPO");
                SetState("connected", "Connected to " + Device.Host + ":" + Device.Port);
                owner.log.Info(Device.Name + ": connected to " + Device.Host + ":" + Device.Port + ", listening to GPI and GPO.");
                owner.ForgetSync(Device.Id);
                double lastHeard = Util.Now(), lastPing = Util.Now(), connectedAt = Util.Now();
                bool inBlock = false;
                List<byte> buf = new List<byte>();
                byte[] chunk = new byte[4096];
                while (!stopped.WaitOne(0))
                {
                    // wait for data without read time-outs (a timed-out read can leave a socket unusable)
                    if (!client.Client.Poll(1000000, SelectMode.SelectRead))
                    {
                        double now = Util.Now();
                        if (now - lastHeard > 300) throw new IOException("nothing heard for 5 minutes; reconnecting");
                        if (now - lastPing >= 30) { Send(s, "VER"); lastPing = now; }
                        continue;
                    }
                    int n = s.Read(chunk, 0, chunk.Length);
                    if (n == 0) throw new IOException("the device closed the connection");
                    lastHeard = Util.Now();
                    for (int i = 0; i < n; i++)
                    {
                        byte c = chunk[i];
                        if (c == '\n')
                        {
                            bool overlong = buf.Count >= 8192;
                            string line = Encoding.UTF8.GetString(buf.ToArray()).Trim('\r', ' ', '\t');
                            buf.Clear();
                            if (overlong)
                            {
                                // not a line an Axia device sends: ignored rather than read cut short
                                owner.log.Warn(Device.Name + ": ignored a line longer than 8 KB from " + Device.Host + ".");
                                continue;
                            }
                            if (line.Length == 0) continue;
                            lock (owner.lk) LastLine = Util.Now();
                            string up = line.ToUpperInvariant();
                            if (up == "BEGIN") { inBlock = true; continue; }
                            if (up == "END") { inBlock = false; continue; }
                            if (up.StartsWith("VER ")) { lock (owner.lk) Version = line.Substring(4).Trim(); continue; }
                            if (up.StartsWith("ERROR")) { owner.log.Warn(Device.Name + ": device says " + line); continue; }
                            // the state dump right after subscribing (in BEGIN/END, or the first 2 s) only sets the state
                            owner.OnLine(Device, line, inBlock || Util.Now() - connectedAt < 2.0, false);
                        }
                        else if (buf.Count < 8192) buf.Add(c);
                    }
                }
            }
        }

        void ForgetSync(string deviceId)
        {
            // after a reconnect the first state of each port is taken as it is (no triggers)
            lock (lk)
            {
                foreach (string k in new List<string>(ports.Keys))
                    if (k.StartsWith(deviceId + "|")) ports.Remove(k);
            }
        }

        // ------------------------------------------------------------- parsing
        static readonly Regex Line = new Regex("^(GPI|GPO)\\s+(\\d+)\\s+(.+)$", RegexOptions.IgnoreCase);

        /// <summary>Handle one line from a device. sync = part of the state block after subscribing (no triggers).</summary>
        public void OnLine(AxiaDevice device, string line, bool sync, bool simulated)
        {
            Match m = Line.Match(line.Trim());
            if (!m.Success) return;
            string signal = m.Groups[1].Value.ToLowerInvariant();
            int port = int.Parse(m.Groups[2].Value, Util.Inv);
            string rest = m.Groups[3].Value.Trim();
            if (rest.StartsWith("CMD:", StringComparison.OrdinalIgnoreCase))
            {
                string text = rest.Substring(4).Trim().Trim('"');
                if (!sync) OnCommand(device, port, text, simulated);
                return;
            }
            string pins = rest.Split(' ')[0];
            List<int> changedOn = new List<int>(), changedOff = new List<int>();
            string key = device.Id + "|" + signal + "|" + port;
            lock (lk)
            {
                PortMem mem;
                bool known = ports.TryGetValue(key, out mem);
                if (!known) { mem = new PortMem(); ports[key] = mem; }
                for (int i = 0; i < Math.Min(5, pins.Length); i++)
                {
                    char c = pins[i];
                    if (c == 'x' || c == 'X') continue;
                    if ("lLhH".IndexOf(c) < 0) continue;
                    char now = (c == 'l' || c == 'L') ? '1' : '0';
                    char before = mem.Active[i];
                    mem.Active[i] = now;
                    if (sync || (!known && !simulated && now == '0')) continue;     // first sight of a port: only an "on" counts
                    if (before == now) continue;
                    if (before == '?' && now == '0') continue;
                    if (now == '1') changedOn.Add(i + 1); else changedOff.Add(i + 1);
                    mem.Changed = Util.Now();
                }
            }
            foreach (int pin in changedOn) Fire(device, signal, port, pin, "on", "", simulated);
            foreach (int pin in changedOff) Fire(device, signal, port, pin, "off", "", simulated);
        }

        void OnCommand(AxiaDevice device, int port, string text, bool simulated)
        {
            Fire(device, "cmd", port, 0, "cmd", text, simulated);
        }

        GpioEvent Remember(GpioEvent e)
        {
            lock (lk)
            {
                e.Seq = ++seq;
                events.Add(e);
                while (events.Count > 200) events.RemoveAt(0);
            }
            return e;
        }

        void Fire(AxiaDevice device, string signal, int port, int pin, string edge, string text, bool simulated)
        {
            AppConfig cfg = config();
            GpioEvent e = new GpioEvent();
            e.Time = Util.Now();
            e.DeviceId = device.Id;
            e.Device = device.Name;
            e.Signal = signal;
            e.Port = port;
            e.Pin = pin;
            e.Edge = edge;
            e.Text = text;
            e.Simulated = simulated;
            string where = Describe(e);
            List<Job> jobs = new List<Job>();
            double now = Util.Now();
            if (Wanted(cfg) || simulated)
            {
                foreach (GpioTrigger t in cfg.Gpio.Triggers)
                {
                    if (!t.Enabled || t.DeviceId != device.Id || t.Signal != signal) continue;
                    List<GpioAction> actions;
                    if (signal == "cmd")
                    {
                        if (t.Port != 0 && t.Port != port) continue;
                        if (!string.Equals(t.Command.Trim(), text.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                        actions = t.OnActive;
                    }
                    else
                    {
                        if (t.Port != port || t.Pin != pin) continue;
                        bool logicalOn = (edge == "on") != t.Invert;
                        actions = logicalOn ? t.OnActive : t.OnInactive;
                    }
                    string fk = t.Id + "|" + edge;
                    lock (lk)
                    {
                        double last;
                        if (t.IgnoreRepeatMs > 0 && lastFire.TryGetValue(fk, out last) && (now - last) * 1000 < t.IgnoreRepeatMs) continue;
                        lastFire[fk] = now;
                    }
                    e.Triggers.Add(t.Name);
                    if (actions.Count == 0) continue;
                    Job j = new Job();
                    j.Event = e;
                    j.Trigger = t;
                    j.Actions = actions;
                    j.Vmix = cfg.Gpio.Vmix;
                    jobs.Add(j);
                }
            }
            e.Result = e.Triggers.Count == 0 ? "no trigger" : jobs.Count == 0 ? "no actions for this change" : "running";
            Remember(e);
            if (e.Triggers.Count > 0 || simulated) log.Info(where + (simulated ? " (simulated)" : "") + ": " +
                                                           (e.Triggers.Count > 0 ? "trigger " + string.Join(", ", e.Triggers.ToArray()) : "no trigger") + ".");
            else log.Debug(where + ": no trigger.");
            if (jobs.Count == 0) return;
            lock (lk) foreach (Job j in jobs) queue.Enqueue(j);
            wake.Set();
        }

        public static string Describe(GpioEvent e)
        {
            if (e.Signal == "cmd") return e.Device + " GPO " + e.Port + " command \"" + e.Text + "\"";
            return e.Device + " " + e.Signal.ToUpperInvariant() + " " + e.Port + " pin " + e.Pin + " " + e.Edge;
        }

        // -------------------------------------------------------------- actions
        void Work()
        {
            while (!stop.WaitOne(0))
            {
                wake.WaitOne(1000);
                while (true)
                {
                    Job j;
                    lock (lk)
                    {
                        if (queue.Count == 0) break;
                        j = queue.Dequeue();
                    }
                    List<ActionResult> results = RunActions(j.Actions, j.Vmix, stop);
                    List<string> bad = new List<string>();
                    foreach (ActionResult r in results)
                    {
                        if (r.Ok) log.Info(j.Trigger.Name + ": " + r.Text);
                        else { log.Warn(j.Trigger.Name + ": " + r.Text); bad.Add(r.Text); }
                    }
                    lock (lk)
                    {
                        string res = bad.Count == 0 ? results.Count + " action(s) done" : bad.Count + " of " + results.Count + " failed: " + bad[0];
                        j.Event.Result = j.Event.Result == "running" ? res : j.Event.Result + "; " + res;
                    }
                    if (bad.Count > 0)
                    {
                        lock (lk) failedTriggers.Add(j.Trigger.Id);
                        RaiseAlert("gpio-trigger:" + j.Trigger.Id, "GPIO trigger " + j.Trigger.Name + " failed",
                                   Describe(j.Event) + " ran trigger " + j.Trigger.Name + ", but:\n- " + string.Join("\n- ", bad.ToArray()));
                    }
                    else
                    {
                        bool was;
                        lock (lk) was = failedTriggers.Remove(j.Trigger.Id);
                        if (was) RaiseAlert("recovered", "gpio-trigger:" + j.Trigger.Id, "GPIO trigger " + j.Trigger.Name + " works again",
                                            Describe(j.Event) + " ran trigger " + j.Trigger.Name + " and every action worked.");
                    }
                }
            }
        }

        /// <summary>Run actions in order (waiting before each as set). Used by the service and by "run now".</summary>
        public static List<ActionResult> RunActions(List<GpioAction> actions, List<VmixTarget> vmix, WaitHandle cancel)
        {
            List<ActionResult> o = new List<ActionResult>();
            foreach (GpioAction a in actions)
            {
                if (a.DelayMs > 0)
                {
                    if (cancel != null) { if (cancel.WaitOne(Math.Min(a.DelayMs, 60000))) break; }
                    else Thread.Sleep(Math.Min(a.DelayMs, 60000));
                }
                o.Add(RunAction(a, vmix));
            }
            return o;
        }

        public static ActionResult RunAction(GpioAction a, List<VmixTarget> vmix)
        {
            try
            {
                if (a.Type == "http")
                {
                    HttpRequestSpec s = new HttpRequestSpec();
                    s.Method = a.Method;
                    s.Url = a.Url;
                    s.Body = a.Body;
                    s.ContentType = a.ContentType;
                    s.User = a.User ?? "";
                    s.Password = s.User.Length > 0 ? Secrets.Unprotect(a.Password) : "";
                    s.AllowPlainLogin = a.AllowPlainLogin;
                    s.Proxy = "none";
                    s.TimeoutMs = 5000;
                    s.MaxBody = 300;
                    HttpResult h = Net.Send(s, null);
                    ActionResult r = new ActionResult();
                    r.Ms = h.Seconds * 1000;
                    string what = "HTTP " + a.Method + " " + a.Url;
                    if (!h.Answered) r.Text = what + " - " + h.Error;
                    else if (!h.Success) r.Text = what + " - answered HTTP " + h.Status + (h.Body.Length > 0 ? ": " + Net.OneLine(h.Body, 200) : "");
                    else { r.Ok = true; r.Text = what + " - HTTP " + h.Status + " (" + r.Ms.ToString("0", Util.Inv) + " ms)"; }
                    return r;
                }
                VmixTarget t = null;
                foreach (VmixTarget v in vmix) if (v.Id == a.VmixId) t = v;
                if (t == null && vmix.Count > 0 && a.VmixId.Length == 0) t = vmix[0];
                if (t == null)
                {
                    ActionResult r = new ActionResult();
                    r.Text = "vMix " + a.Function + " - no vMix set up for this action (vMix tab)";
                    return r;
                }
                return Vmix.Call(t, a);
            }
            catch (Exception ex)
            {
                ActionResult r = new ActionResult();
                r.Text = ex.Message;
                return r;
            }
        }

        // -------------------------------------------------------- manager calls
        /// <summary>Act as if a device reported this change (manager "simulate" button, tests).</summary>
        public GpioEvent Simulate(string deviceId, string signal, int port, int pin, bool on, string command)
        {
            AppConfig cfg = config();
            AxiaDevice d = cfg.Gpio.Devices.Find(delegate(AxiaDevice x) { return x.Id == deviceId; });
            if (d == null) throw new InvalidOperationException("unknown device; save the GPIO settings first");
            long before;
            lock (lk) before = seq;
            if (signal == "cmd") OnCommand(d, port, command ?? "", true);
            else
            {
                char[] p = { 'x', 'x', 'x', 'x', 'x' };
                if (pin >= 1 && pin <= 5) p[pin - 1] = on ? 'L' : 'H';
                string key = d.Id + "|" + signal + "|" + port;
                lock (lk)
                {
                    PortMem mem;
                    if (!ports.TryGetValue(key, out mem)) { mem = new PortMem(); ports[key] = mem; }
                    if (pin >= 1 && pin <= 5 && mem.Active[pin - 1] == '?') mem.Active[pin - 1] = on ? '0' : '1';
                }
                OnLine(d, signal.ToUpperInvariant() + " " + port + " " + new string(p), false, true);
            }
            lock (lk) return events.Count > 0 && events[events.Count - 1].Seq > before ? events[events.Count - 1] : null;
        }

        /// <summary>Connect to a device with unsaved settings and report everything it says for a few seconds.</summary>
        public static List<string> Probe(AxiaDevice d, int listenMs)
        {
            NetTrace t = new NetTrace();
            try
            {
                using (TcpClient c = Net.Connect(d.Host.Trim(), d.Port, 5000, t))
                {
                    NetworkStream s = c.GetStream();
                    List<string> send = new List<string>();
                    if (d.Login)
                    {
                        string pw = Secrets.Unprotect(d.Password);
                        send.Add(pw.Length > 0 ? "LOGIN " + pw : "LOGIN");
                    }
                    send.Add("VER");
                    send.Add("ADD GPI");
                    send.Add("ADD GPO");
                    foreach (string line in send)
                    {
                        byte[] b = Encoding.ASCII.GetBytes(line + "\r\n");
                        s.Write(b, 0, b.Length);
                        t.Add("C: " + (line.StartsWith("LOGIN ") ? "LOGIN (password)" : line));
                    }
                    Stopwatch sw = Stopwatch.StartNew();
                    StringBuilder buf = new StringBuilder();
                    byte[] chunk = new byte[4096];
                    int lines = 0;
                    while (sw.ElapsedMilliseconds < listenMs && lines < 300)
                    {
                        if (!c.Client.Poll(250000, SelectMode.SelectRead)) continue;
                        int n = s.Read(chunk, 0, chunk.Length);
                        if (n == 0) { t.Add("The device closed the connection."); break; }
                        buf.Append(Encoding.UTF8.GetString(chunk, 0, n));
                        if (buf.Length > 65536) { t.Add("The device sent more than 64 KB without a line break; stopped listening."); break; }
                        string all = buf.ToString();
                        int nl;
                        while ((nl = all.IndexOf('\n')) >= 0)
                        {
                            string line = all.Substring(0, nl).Trim('\r', ' ');
                            all = all.Substring(nl + 1);
                            if (line.Length > 0) { t.Add("S: " + line); lines++; }
                        }
                        buf = new StringBuilder(all);
                    }
                    if (lines == 0) t.Add("The device said nothing in " + listenMs / 1000 + " s. Check that this is an Axia device with LWRP on port " + d.Port +
                                          " (GPIO node, xNode, engine or IP-Audio driver), and whether it needs LOGIN.");
                    else t.Add(lines + " line(s) received. GPI/GPO lines show the ports; l/L = pin on (low), h/H = off.");
                }
            }
            catch (Exception ex) { t.Add("FAILED: " + ex.Message); }
            return t.Lines;
        }

        public GpioStatus Status()
        {
            AppConfig cfg = config();
            GpioStatus st = new GpioStatus();
            st.Enabled = Wanted(cfg);
            lock (lk)
            {
                foreach (AxiaDevice d in cfg.Gpio.Devices)
                {
                    GpioDeviceStatus ds = new GpioDeviceStatus();
                    ds.Id = d.Id;
                    ds.Name = d.Name;
                    ds.Host = d.Host + ":" + d.Port;
                    DeviceLink l;
                    if (links.TryGetValue(d.Id, out l))
                    {
                        ds.State = l.State;
                        ds.Text = l.Text;
                        ds.Since = l.Since;
                        ds.LastLine = l.LastLine;
                        ds.Version = l.Version;
                    }
                    else
                    {
                        ds.State = "off";
                        ds.Text = !st.Enabled ? "GPIO is switched off" : !d.Enabled ? "Switched off" : d.Host.Length == 0 ? "No address" : "Starting";
                    }
                    foreach (KeyValuePair<string, PortMem> kv in ports)
                    {
                        string[] k = kv.Key.Split('|');
                        if (k.Length != 3 || k[0] != d.Id) continue;
                        GpioPortState ps = new GpioPortState();
                        ps.DeviceId = d.Id;
                        ps.Signal = k[1];
                        ps.Port = int.Parse(k[2], Util.Inv);
                        ps.Pins = new string(kv.Value.Active);
                        ps.Changed = kv.Value.Changed;
                        ds.Ports.Add(ps);
                    }
                    ds.Ports.Sort(delegate(GpioPortState a, GpioPortState b)
                    {
                        int c = string.CompareOrdinal(a.Signal, b.Signal);
                        return c != 0 ? c : a.Port.CompareTo(b.Port);
                    });
                    st.Devices.Add(ds);
                }
                for (int i = events.Count - 1; i >= 0 && st.Events.Count < 60; i--) st.Events.Add(Json.Clone(events[i]));
            }
            return st;
        }
    }
}
