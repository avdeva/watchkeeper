// GPIO: listens to Axia devices (Livewire Routing Protocol, TCP port 93) and runs vMix functions or web requests when a
// pin turns on or off.
//
// Each switched-on device has a connection of its own that reconnects by itself (2 s, doubling to 30 s). After connecting
// the device reports the state of every port; that first report only sets the known state and runs nothing. Afterwards
// every change of a pin is matched against the triggers. Actions run one after another on a worker thread, so a slow
// vMix never delays reading the device.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Watchkeeper
{
    public class GpioPortState
    {
        public string Signal = "";
        public int Port;
        /// <summary>One character per pin: 1 = on (low, contact closed), 0 = off, ? = not known.</summary>
        public string Pins = "";
    }

    public class GpioDeviceStatus
    {
        public string Id = "", Name = "", Host = "";
        /// <summary>off | connecting | connected | error</summary>
        public string State = "off";
        public string Text = "";
        public double Since;
        public List<GpioPortState> Ports = new List<GpioPortState>();
    }

    public class GpioEvent
    {
        public double Time;
        public string Device = "";
        public string Signal = "";
        public int Port, Pin;
        public string Edge = "";
        public string Result = "";
    }

    public class GpioStatus
    {
        public bool Enabled;
        public List<GpioDeviceStatus> Devices = new List<GpioDeviceStatus>();
        public List<GpioEvent> Events = new List<GpioEvent>();
    }

    public class Gpio
    {
        static readonly Regex Line = new Regex(@"^(GPI|GPO)\s+(\d+)\s+(\S+)", RegexOptions.IgnoreCase);

        readonly Func<Settings> settings;
        readonly Logger log;
        readonly IAlertSink alerts;
        readonly object lk = new object();
        readonly Dictionary<string, Link> links = new Dictionary<string, Link>();
        readonly Dictionary<string, char[]> pins = new Dictionary<string, char[]>();        // device|signal|port -> pins
        readonly Dictionary<string, double> lastFired = new Dictionary<string, double>();
        readonly List<GpioEvent> events = new List<GpioEvent>();
        readonly Dictionary<string, Queue<KeyValuePair<GpioTrigger, List<GpioAction>>>> queues = new Dictionary<string, Queue<KeyValuePair<GpioTrigger, List<GpioAction>>>>();
        readonly HashSet<string> draining = new HashSet<string>();
        volatile bool stopping;
        /// <summary>Tests: run actions on the reading thread.</summary>
        public bool Inline;

        public Gpio(Func<Settings> settings, Logger log, IAlertSink alerts)
        {
            this.settings = settings;
            this.log = log;
            this.alerts = alerts;
        }

        public void Start() { }

        public void Stop()
        {
            stopping = true;
            lock (lk) { foreach (Link l in links.Values) l.Stop(); links.Clear(); }
        }

        /// <summary>Start, stop or restart connections to match the settings; alert on devices unreachable too long.</summary>
        public void Tick()
        {
            Settings cfg = settings();
            GpioSettings g = cfg.Gpio;
            Dictionary<string, AxiaDevice> wanted = new Dictionary<string, AxiaDevice>();
            if (g.Enabled) foreach (AxiaDevice d in g.Devices) if (d.Enabled && d.Host.Length > 0) wanted[d.Id] = d;
            lock (lk)
            {
                foreach (string id in new List<string>(links.Keys))
                {
                    AxiaDevice d;
                    if (!wanted.TryGetValue(id, out d) || links[id].Signature != Sig(d))
                    {
                        links[id].Stop();
                        links.Remove(id);
                        alerts.Recover("gpio:device:" + id, "GPIO device no longer used", "The device is no longer used.");
                    }
                }
                foreach (AxiaDevice d in wanted.Values)
                {
                    if (links.ContainsKey(d.Id)) continue;
                    Link l = new Link(this, d);
                    links[d.Id] = l;
                    l.Start();
                }
                double now = Util.Now();
                foreach (Link l in links.Values)
                {
                    if (l.State != "connected" && !l.Alerted && now - l.DownSince >= g.DisconnectAlertSeconds)
                    {
                        l.Alerted = true;
                        string msg = l.Device.Name + " (" + l.Device.Host + ":" + l.Device.Port + ") has not been reachable for " + (int)(now - l.DownSince) + "s: " + l.Text + ". Its triggers do not run.";
                        log.Error(msg);
                        alerts.Raise("gpio", "gpio:device:" + l.Device.Id, "error", "GPIO device " + l.Device.Name + " is not reachable", msg);
                    }
                }
            }
        }

        static string Sig(AxiaDevice d) { return d.Host + "|" + d.Port + "|" + d.Password; }

        // ---------------------------------------------------------------- pins
        void Forget(string deviceId)
        {
            lock (lk)
            {
                List<string> keys = new List<string>(pins.Keys);
                foreach (string k in keys) if (k.StartsWith(deviceId + "|")) pins.Remove(k);
            }
        }

        /// <summary>A line from a device. sync: part of the state report after connecting (sets the state, runs nothing).</summary>
        public void OnLine(AxiaDevice device, string line, bool sync)
        {
            Match m = Line.Match(line.Trim());
            if (!m.Success) return;
            string signal = m.Groups[1].Value.ToLowerInvariant();
            int port = int.Parse(m.Groups[2].Value, Util.Inv);
            string states = m.Groups[3].Value;
            List<int> on = new List<int>(), off = new List<int>();
            string key = device.Id + "|" + signal + "|" + port;
            lock (lk)
            {
                char[] mem;
                if (!pins.TryGetValue(key, out mem)) { mem = "?????".ToCharArray(); pins[key] = mem; }
                for (int i = 0; i < Math.Min(5, states.Length); i++)
                {
                    char c = char.ToLowerInvariant(states[i]);
                    if (c != 'l' && c != 'h') continue;
                    char now = c == 'l' ? '1' : '0';
                    char before = mem[i];
                    mem[i] = now;
                    if (sync || before == now) continue;
                    if (before == '?') continue;          // first report of this pin: only its state is learned
                    if (now == '1') on.Add(i + 1); else off.Add(i + 1);
                }
            }
            foreach (int p in on) Fire(device, signal, port, p, true);
            foreach (int p in off) Fire(device, signal, port, p, false);
        }

        void Fire(AxiaDevice device, string signal, int port, int pin, bool low)
        {
            Settings cfg = settings();
            GpioEvent e = new GpioEvent();
            e.Time = Util.Now();
            e.Device = device.Name;
            e.Signal = signal;
            e.Port = port;
            e.Pin = pin;
            e.Edge = low ? "low (closed)" : "high (open)";
            List<string> fired = new List<string>();
            foreach (GpioTrigger t in cfg.Gpio.Triggers)
            {
                if (!t.Enabled || t.DeviceId != device.Id || t.Signal != signal || t.Port != port || t.Pin != pin) continue;
                bool active = t.Invert ? !low : low;
                List<GpioAction> actions = active ? t.WhenOn : t.WhenOff;
                string rk = t.Id + "|" + active;
                lock (lk)
                {
                    double last;
                    if (t.IgnoreRepeatMs > 0 && lastFired.TryGetValue(rk, out last) && (e.Time - last) * 1000 < t.IgnoreRepeatMs)
                    {
                        fired.Add(t.Name + " (repeat ignored)");
                        continue;
                    }
                    lastFired[rk] = e.Time;
                }
                if (actions.Count == 0) continue;
                fired.Add(t.Name + (active ? " on" : " off"));
                Queue(t, actions);
            }
            e.Result = fired.Count == 0 ? "no trigger" : string.Join(", ", fired.ToArray());
            log.Info("GPIO " + device.Name + " " + signal.ToUpperInvariant() + " " + port + " pin " + pin + " " + e.Edge + ": " + e.Result + ".");
            lock (lk) { events.Insert(0, e); if (events.Count > 100) events.RemoveAt(events.Count - 1); }
        }

        /// <summary>Each trigger has its own queue: its actions run in order, and a trigger that waits (an action with a
        /// delay, a slow vMix) never holds up another trigger.</summary>
        void Queue(GpioTrigger t, List<GpioAction> actions)
        {
            KeyValuePair<GpioTrigger, List<GpioAction>> job = new KeyValuePair<GpioTrigger, List<GpioAction>>(t, actions);
            if (Inline) { RunJob(job); return; }
            bool start;
            lock (lk)
            {
                Queue<KeyValuePair<GpioTrigger, List<GpioAction>>> q;
                if (!queues.TryGetValue(t.Id, out q)) { q = new Queue<KeyValuePair<GpioTrigger, List<GpioAction>>>(); queues[t.Id] = q; }
                q.Enqueue(job);
                start = !draining.Contains(t.Id);
                if (start) draining.Add(t.Id);
            }
            if (!start) return;
            string id = t.Id;
            Thread th = new Thread(delegate()
            {
                while (!stopping)
                {
                    KeyValuePair<GpioTrigger, List<GpioAction>> next;
                    lock (lk)
                    {
                        Queue<KeyValuePair<GpioTrigger, List<GpioAction>>> q = queues[id];
                        if (q.Count == 0) { draining.Remove(id); return; }
                        next = q.Dequeue();
                    }
                    try { RunJob(next); } catch (Exception ex) { log.Error("GPIO action: " + ex.Message); }
                }
                lock (lk) draining.Remove(id);
            });
            th.IsBackground = true;
            th.Name = "gpio-" + t.Name;
            th.Start();
        }

        void RunJob(KeyValuePair<GpioTrigger, List<GpioAction>> job)
        {
            Settings cfg = settings();
            List<string> failed = new List<string>();
            foreach (GpioAction a in job.Value)
            {
                if (a.DelayMs > 0) Thread.Sleep(a.DelayMs);
                string err = Run(cfg.Gpio, a);
                string what = Describe(cfg.Gpio, a);
                if (err == null) log.Info("GPIO trigger '" + job.Key.Name + "': " + what + " - done.");
                else { log.Warn("GPIO trigger '" + job.Key.Name + "': " + what + " - " + err); failed.Add(what + ": " + err); }
            }
            string key = "gpio:trigger:" + job.Key.Id;
            if (failed.Count > 0) alerts.Raise("gpio", key, "warning", "GPIO trigger '" + job.Key.Name + "' failed", string.Join("\n", failed.ToArray()));
            else alerts.Recover(key, "GPIO trigger '" + job.Key.Name + "' works again", "Its actions ran without problems.");
        }

        // ---------------------------------------------------------------- actions
        public static VmixTarget TargetFor(GpioSettings g, GpioAction a)
        {
            if (a.VmixId.Length > 0) return g.Vmix.Find(delegate(VmixTarget x) { return x.Id == a.VmixId; });
            return g.Vmix.Count > 0 ? g.Vmix[0] : null;
        }

        public static string VmixUrl(VmixTarget t, GpioAction a)
        {
            StringBuilder sb = new StringBuilder("http://" + t.Host + ":" + t.Port + "/api/?Function=" + Uri.EscapeDataString(a.Function));
            if (a.Input.Length > 0) sb.Append("&Input=").Append(Uri.EscapeDataString(a.Input));
            if (a.Value.Length > 0) sb.Append("&Value=").Append(Uri.EscapeDataString(a.Value));
            foreach (string part in a.Extra.Split('&'))
            {
                string p = part.Trim();
                if (p.Length == 0) continue;
                int eq = p.IndexOf('=');
                if (eq <= 0) sb.Append('&').Append(Uri.EscapeDataString(p));
                else sb.Append('&').Append(Uri.EscapeDataString(p.Substring(0, eq).Trim())).Append('=').Append(Uri.EscapeDataString(p.Substring(eq + 1)));
            }
            return sb.ToString();
        }

        public static string Describe(GpioSettings g, GpioAction a)
        {
            if (a.Type == "http") return a.Method + " " + a.Url;
            VmixTarget t = TargetFor(g, a);
            return "vMix" + (t != null && g.Vmix.Count > 1 ? " " + t.Name : "") + " " + a.Function + (a.Input.Length > 0 ? " Input=" + a.Input : "") + (a.Value.Length > 0 ? " Value=" + a.Value : "");
        }

        /// <summary>Run one action now. Null when it worked, otherwise what went wrong.</summary>
        public static string Run(GpioSettings g, GpioAction a)
        {
            if (a.Type == "http")
            {
                HttpResult r = Http.Send(a.Method, a.Url, a.Method == "GET" ? null : a.Body, a.ContentType, null, null, null, 5000);
                if (!r.Answered) return "no answer: " + r.Error;
                if (r.Status >= 400) return "answered HTTP " + r.Status;
                return null;
            }
            VmixTarget t = TargetFor(g, a);
            if (t == null) return "no vMix is set up";
            string problem;
            string pw = t.User.Length > 0 ? Secrets.TryReveal(t.Password, out problem) : "";
            HttpResult h = Http.Send("GET", VmixUrl(t, a), null, null, null, t.User.Length > 0 ? t.User : null, pw, 3000);
            if (!h.Answered) return "no answer from vMix at " + t.Host + ":" + t.Port + " (" + h.Error + "). Is vMix running with Settings > Web Controller on?";
            if (h.Status == 401) return "vMix asks for a login: set the Web Controller user name and password";
            if (h.Status >= 400) return "vMix answered HTTP " + h.Status + ": " + Util.OneLine(h.Body, 150) + " (check the function name and input)";
            return null;
        }

        /// <summary>Manager: run a trigger's actions as if the pin turned on (or off).</summary>
        public static List<string> Simulate(GpioSettings g, GpioTrigger t, bool on)
        {
            List<string> o = new List<string>();
            List<GpioAction> list = on ? t.WhenOn : t.WhenOff;
            if (list.Count == 0) o.Add("No actions for " + (on ? "on" : "off") + ".");
            foreach (GpioAction a in list)
            {
                if (a.DelayMs > 0) Thread.Sleep(Math.Min(a.DelayMs, 5000));
                string err = Run(g, a);
                o.Add(Describe(g, a) + ": " + (err == null ? "done" : err));
            }
            return o;
        }

        public GpioStatus Snapshot()
        {
            Settings cfg = settings();
            GpioStatus s = new GpioStatus();
            s.Enabled = cfg.Gpio.Enabled;
            lock (lk)
            {
                foreach (AxiaDevice d in cfg.Gpio.Devices)
                {
                    GpioDeviceStatus ds = new GpioDeviceStatus();
                    ds.Id = d.Id;
                    ds.Name = d.Name;
                    ds.Host = d.Host;
                    Link l;
                    if (links.TryGetValue(d.Id, out l)) { ds.State = l.State; ds.Text = l.Text; ds.Since = l.Since; }
                    else { ds.State = "off"; ds.Text = !cfg.Gpio.Enabled ? "GPIO is switched off" : !d.Enabled ? "Switched off" : "No address"; }
                    foreach (KeyValuePair<string, char[]> kv in pins)
                    {
                        string[] parts = kv.Key.Split('|');
                        if (parts[0] != d.Id) continue;
                        GpioPortState ps = new GpioPortState();
                        ps.Signal = parts[1];
                        ps.Port = int.Parse(parts[2], Util.Inv);
                        ps.Pins = new string(kv.Value);
                        ds.Ports.Add(ps);
                    }
                    ds.Ports.Sort(delegate(GpioPortState a, GpioPortState b) { int c = string.CompareOrdinal(a.Signal, b.Signal); return c != 0 ? c : a.Port.CompareTo(b.Port); });
                    s.Devices.Add(ds);
                }
                foreach (GpioEvent e in events) { GpioEvent c = new GpioEvent(); Json.FillFrom(c, Json.ToJson(e)); s.Events.Add(c); }
            }
            return s;
        }

        // ---------------------------------------------------------------- one device connection
        class Link
        {
            readonly Gpio owner;
            public readonly AxiaDevice Device;
            public readonly string Signature;
            readonly ManualResetEvent stopped = new ManualResetEvent(false);
            TcpClient client;
            public volatile string State = "connecting";
            public volatile string Text = "Connecting";
            public double Since = Util.Now(), DownSince = Util.Now();
            public bool Alerted;

            public Link(Gpio owner, AxiaDevice d)
            {
                this.owner = owner;
                Device = new AxiaDevice();
                Json.FillFrom(Device, Json.ToJson(d));
                Signature = Sig(d);
            }

            public void Start()
            {
                Thread t = new Thread(Run);
                t.IsBackground = true;
                t.Name = "lwrp-" + Device.Host;
                t.Start();
            }

            public void Stop()
            {
                stopped.Set();
                try { if (client != null) client.Close(); } catch (Exception) { }
            }

            void Set(string state, string text)
            {
                if (state != State) Since = Util.Now();
                if (state != "connected" && State == "connected") DownSince = Util.Now();
                State = state;
                Text = text;
            }

            void Run()
            {
                int backoff = 2000;
                while (!stopped.WaitOne(0))
                {
                    try { Session(); backoff = 2000; }
                    catch (Exception ex)
                    {
                        if (stopped.WaitOne(0)) break;
                        bool wasUp = State == "connected";
                        Set("error", ex.Message);
                        if (wasUp) owner.log.Warn("GPIO " + Device.Name + " (" + Device.Host + "): " + ex.Message);
                    }
                    finally { try { if (client != null) client.Close(); } catch (Exception) { } }
                    if (stopped.WaitOne(backoff)) break;
                    backoff = Math.Min(30000, backoff * 2);
                }
                State = "off";
                Text = "Stopped";
            }

            static void Send(Stream s, string line)
            {
                byte[] b = Encoding.ASCII.GetBytes(line + "\r\n");
                s.Write(b, 0, b.Length);
                s.Flush();
            }

            void Session()
            {
                Set("connecting", "Connecting to " + Device.Host + ":" + Device.Port);
                client = new TcpClient();
                IAsyncResult ar = client.BeginConnect(Device.Host, Device.Port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(5000)) throw new IOException("no connection to " + Device.Host + ":" + Device.Port + " within 5s");
                try { client.EndConnect(ar); }
                catch (SocketException ex) { throw new IOException("cannot connect to " + Device.Host + ":" + Device.Port + ": " + ex.Message); }
                NetworkStream s = client.GetStream();
                string pw = "";
                if (Device.Password.Length > 0)
                {
                    string problem;
                    pw = Secrets.TryReveal(Device.Password, out problem);
                    if (problem != null) throw new IOException("the device password cannot be read: " + problem);
                    Send(s, "LOGIN " + pw);
                }
                owner.Forget(Device.Id);                  // states are learned again; nothing runs for what changed while away
                Send(s, "VER");
                Send(s, "ADD GPI");
                Send(s, "ADD GPO");
                Send(s, "GPI");                           // ask for the current state of every port
                Send(s, "GPO");
                Set("connected", "Connected to " + Device.Host + ":" + Device.Port);
                owner.log.Info("GPIO " + Device.Name + ": connected to " + Device.Host + ":" + Device.Port + ".");
                if (Alerted)
                {
                    Alerted = false;
                    owner.alerts.Recover("gpio:device:" + Device.Id, "GPIO device " + Device.Name + " is reachable again", Device.Name + " (" + Device.Host + ") is connected again.");
                }
                double lastHeard = Util.Now(), lastPing = Util.Now();
                bool inBlock = false;
                List<byte> buf = new List<byte>();
                byte[] chunk = new byte[4096];
                while (!stopped.WaitOne(0))
                {
                    if (!client.Client.Poll(500000, SelectMode.SelectRead))
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
                        if (chunk[i] != '\n') { if (buf.Count < 8192) buf.Add(chunk[i]); continue; }
                        string line = Encoding.ASCII.GetString(buf.ToArray()).Trim();
                        buf.Clear();
                        if (line.Length == 0) continue;
                        string up = line.ToUpperInvariant();
                        if (up == "BEGIN") { inBlock = true; continue; }
                        if (up == "END") { inBlock = false; continue; }
                        if (up.StartsWith("ERROR")) { owner.log.Warn("GPIO " + Device.Name + ": the device says " + line); continue; }
                        owner.OnLine(Device, line, inBlock);
                    }
                }
            }
        }
    }
}
