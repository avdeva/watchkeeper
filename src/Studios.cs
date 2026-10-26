// Studios: one server can run the programs of different studios (vMix, VVD, ... each with its own settings).
// Pathfinder decides which studio a server runs, and Watchkeeper reports back how the server is doing:
//
//   status out  - Pathfinder Core PRO memory slots over its SapV2 protocol (TCP 9600, or 9602 with TLS):
//                 a heartbeat counter, the status (ok | degraded | switching | error) and the active studio;
//               - and/or GPIO pins on an Axia device over LWRP (typically the IP-Audio driver on this server):
//                 a heartbeat pin that changes every interval, an "OK" pin and one pin per studio (low = on).
//   commands in - a Pathfinder memory slot holding the studio this server should run (read every interval;
//                 the connection is made by Watchkeeper, nothing listens for it), and/or GPIO triggers with a
//                 "switch studio" action.
//
// A switch closes the programs of the old studio (asked to close, then ended after a time-out), then starts
// the programs of the new one at once. Watchkeeper never switches on its own: Pathfinder decides.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Watchkeeper
{
    // ------------------------------------------------------------------ settings
    public class StudiosCfg
    {
        public List<StudioDef> Studios = new List<StudioDef>();
        /// <summary>The studio (id) this server runs the very first time; afterwards the last active studio is kept
        /// across restarts until Pathfinder (or an operator) chooses another. Empty = none.</summary>
        public string StartStudio = "";
        /// <summary>How long programs of the old studio get to close before they are ended.</summary>
        public int CloseTimeoutSeconds = 20;
        /// <summary>Switches asked for by Pathfinder or GPIO are at least this far apart (protects against flapping).</summary>
        public int MinSecondsBetweenSwitches = 30;
        public PathfinderCfg Pathfinder = new PathfinderCfg();
        public GpioStatusCfg GpioStatus = new GpioStatusCfg();
    }

    public class StudioDef
    {
        public string Id = "";
        public string Name = "Studio";
        /// <summary>What Pathfinder (memory slot) or a GPIO command message uses for this studio: letters, digits, _ and -.</summary>
        public string Code = "";
        public string Notes = "";
    }

    public class PathfinderCfg
    {
        public bool Enabled = false;
        public string Host = "";
        public int Port = 9600;
        /// <summary>TLS (Pathfinder Core PRO: port 9602, with its certificate set up).</summary>
        public bool Tls = false;
        /// <summary>SHA-256 fingerprint of Pathfinder's certificate, accepted although it is not trusted by Windows.</summary>
        public string TrustFingerprint = "";
        public string User = "";
        public string Password = "";
        /// <summary>Without TLS the login crosses the network readable: only when this is ticked (or Pathfinder is this computer).</summary>
        public bool AllowPlainLogin = false;
        public int IntervalSeconds = 2;
        /// <summary>Memory slot names (empty = not used). Watchkeeper writes the first three and reads the last.</summary>
        public string HeartbeatSlot = "";
        public string StatusSlot = "";
        public string StudioSlot = "";
        public string CommandSlot = "";
    }

    public class GpioStatusCfg
    {
        public bool Enabled = false;
        /// <summary>The Axia device (GPIO tab) the pins are set on; usually the IP-Audio driver on this server (127.0.0.1).</summary>
        public string DeviceId = "";
        /// <summary>gpi: set the device's GPI port (the IP-Audio driver's virtual inputs, seen by Pathfinder) | gpo.</summary>
        public string Signal = "gpi";
        public int Port = 1;
        public int IntervalSeconds = 2;
        /// <summary>Pins 1-5, 0 = not used. Low = on.</summary>
        public int HeartbeatPin = 1;
        public int OkPin = 2;
        public List<StudioPin> StudioPins = new List<StudioPin>();
    }

    public class StudioPin
    {
        public string StudioId = "";
        public int Pin = 0;
    }

    // -------------------------------------------------------------------- status
    public class StudioStatus
    {
        public bool On;
        public string Active = "";            // studio id, "" = none
        public string ActiveName = "";
        public string ActiveCode = "";
        public double ActiveSince;
        public string State = "off";          // off | ok | degraded | switching | error
        public string StateText = "";
        public string Switching = "";         // target studio name while a switch runs
        public string LastSwitch = "";        // one line about the last switch
        public double LastSwitchAt;
        public string Desired = "";           // what Pathfinder's command slot holds now
        public string PathfinderState = "off"; // off | connecting | connected | error
        public string PathfinderText = "";
        public double PathfinderSince;
        public string GpioState = "off";      // off | ok | error
        public string GpioText = "";
        public long Heartbeat;
    }

    // ------------------------------------------------------------- normalise / check
    public static class StudioSettings
    {
        static readonly Regex CodeRx = new Regex("^[A-Za-z0-9_-]{1,32}$");
        static readonly Regex SlotRx = new Regex("^[A-Za-z0-9_.-]{1,64}$");
        public const string None = "none";

        static string S(string v) { return v == null ? "" : v.Trim(); }
        static int I(int v, int lo, int hi) { return Math.Max(lo, Math.Min(hi, v)); }

        public static void Normalize(StudiosCfg s)
        {
            s.Studios = s.Studios ?? new List<StudioDef>();
            List<string> seen = new List<string>();
            for (int i = 0; i < s.Studios.Count; i++)
            {
                StudioDef d = s.Studios[i] ?? new StudioDef();
                s.Studios[i] = d;
                d.Id = Cfg.UniqueId(d.Id, seen);
                d.Name = S(d.Name).Length > 0 ? S(d.Name) : "Studio";
                d.Code = S(d.Code);
                d.Notes = S(d.Notes);
            }
            s.StartStudio = S(s.StartStudio);
            s.CloseTimeoutSeconds = I(s.CloseTimeoutSeconds, 1, 600);
            s.MinSecondsBetweenSwitches = I(s.MinSecondsBetweenSwitches, 0, 3600);
            PathfinderCfg p = s.Pathfinder = s.Pathfinder ?? new PathfinderCfg();
            p.Host = S(p.Host);
            if (p.Port == 0) p.Port = p.Tls ? 9602 : 9600;
            p.Port = I(p.Port, 1, 65535);
            p.TrustFingerprint = SmtpSender.Clean(p.TrustFingerprint);
            p.User = S(p.User);
            p.Password = p.Password ?? "";
            p.IntervalSeconds = I(p.IntervalSeconds, 1, 60);
            p.HeartbeatSlot = S(p.HeartbeatSlot);
            p.StatusSlot = S(p.StatusSlot);
            p.StudioSlot = S(p.StudioSlot);
            p.CommandSlot = S(p.CommandSlot);
            GpioStatusCfg g = s.GpioStatus = s.GpioStatus ?? new GpioStatusCfg();
            g.DeviceId = S(g.DeviceId);
            g.Signal = g.Signal == "gpo" ? "gpo" : "gpi";
            g.Port = I(g.Port, 1, 256);
            g.IntervalSeconds = I(g.IntervalSeconds, 1, 60);
            g.HeartbeatPin = I(g.HeartbeatPin, 0, 5);
            g.OkPin = I(g.OkPin, 0, 5);
            List<StudioPin> pins = new List<StudioPin>();
            if (g.StudioPins != null)
                foreach (StudioPin sp in g.StudioPins)
                    if (sp != null && S(sp.StudioId).Length > 0) { sp.StudioId = S(sp.StudioId); sp.Pin = I(sp.Pin, 0, 5); pins.Add(sp); }
            g.StudioPins = pins;
        }

        public static List<string> Validate(AppConfig c)
        {
            List<string> e = new List<string>();
            StudiosCfg s = c.Studios;
            List<string> codes = new List<string>(), names = new List<string>();
            for (int i = 0; i < s.Studios.Count; i++)
            {
                StudioDef d = s.Studios[i];
                string l = "studio " + (i + 1) + " (" + d.Name + ")";
                if (!CodeRx.IsMatch(d.Code)) e.Add(l + ": the code must be 1 to 32 letters, digits, _ or - (it is what Pathfinder sends)");
                else if (string.Equals(d.Code, None, StringComparison.OrdinalIgnoreCase)) e.Add(l + ": 'none' is reserved for no studio");
                else if (codes.Contains(d.Code.ToUpperInvariant())) e.Add(l + ": another studio has the code " + d.Code);
                if (names.Contains(d.Name.ToUpperInvariant())) e.Add(l + ": another studio has the same name");
                codes.Add(d.Code.ToUpperInvariant());
                names.Add(d.Name.ToUpperInvariant());
            }
            if (s.StartStudio.Length > 0 && Find(s, s.StartStudio) == null) e.Add("studios: the studio to start with no longer exists");
            foreach (ProgramJob p in c.Programs)
                foreach (string sid in p.Studios)
                    if (!s.Studios.Exists(delegate(StudioDef d) { return d.Id == sid; }))
                        e.Add("watchdog entry " + p.Name + " belongs to a studio that no longer exists; it does not run until that is corrected");
            foreach (GpioTrigger t in c.Gpio.Triggers)
            {
                List<GpioAction> all = new List<GpioAction>(t.OnActive);
                all.AddRange(t.OnInactive);
                foreach (GpioAction a in all)
                    if (a.Type == "studio" && a.Studio.Length > 0 && !string.Equals(a.Studio, None, StringComparison.OrdinalIgnoreCase) &&
                        !s.Studios.Exists(delegate(StudioDef d) { return d.Id == a.Studio; }))
                        e.Add("GPIO trigger " + t.Name + ": its 'switch studio' action uses a studio that no longer exists");
            }

            PathfinderCfg p0 = s.Pathfinder;
            if (p0.Enabled)
            {
                const string l = "Pathfinder";
                if (p0.Host.Length == 0) e.Add(l + ": the address of Pathfinder Core PRO is required");
                if (p0.User.Length == 0) e.Add(l + ": a Pathfinder user name is required (SapV2 needs a login)");
                if (HasSpace(p0.User) || HasSpace(Secrets.Peek(p0.Password)))
                    e.Add(l + ": the user name and password cannot contain spaces (the SapV2 login separates them with spaces)");
                if (!p0.Tls && !p0.AllowPlainLogin && !Net.IsLoopback(p0.Host))
                    e.Add(l + ": without TLS the Pathfinder login crosses the network readable. Use TLS (port 9602), or switch on 'Send the login unencrypted'");
                foreach (string slot in new[] { p0.HeartbeatSlot, p0.StatusSlot, p0.StudioSlot, p0.CommandSlot })
                    if (slot.Length > 0 && !SlotRx.IsMatch(slot)) e.Add(l + ": memory slot name '" + slot + "' may only contain letters, digits, _ . and -");
                if (p0.HeartbeatSlot.Length + p0.StatusSlot.Length + p0.StudioSlot.Length + p0.CommandSlot.Length == 0)
                    e.Add(l + ": name at least one memory slot");
            }

            GpioStatusCfg g = s.GpioStatus;
            if (g.Enabled)
            {
                const string l = "GPIO status";
                if (!c.General.Modules.Gpio) e.Add(l + ": the GPIO part is off (Service & settings); the pins are set through it");
                AxiaDevice dev = c.Gpio.Devices.Find(delegate(AxiaDevice d) { return d.Id == g.DeviceId; });
                if (dev == null) e.Add(l + ": choose the Axia device the pins are set on (add it on the GPIO page)");
                else if (!dev.Enabled) e.Add(l + ": the Axia device " + dev.Name + " is switched off");
                if (!c.Gpio.Enabled && c.General.Modules.Gpio) e.Add(l + ": GPIO is switched off on the GPIO page ('Listen to the Axia devices')");
                List<int> used = new List<int>();
                Action<int, string> use = delegate(int pin, string what)
                {
                    if (pin == 0) return;
                    if (used.Contains(pin)) e.Add(l + ": pin " + pin + " is used twice (" + what + ")");
                    used.Add(pin);
                };
                use(g.HeartbeatPin, "heartbeat");
                use(g.OkPin, "OK");
                foreach (StudioPin sp in g.StudioPins)
                {
                    StudioDef d = s.Studios.Find(delegate(StudioDef x) { return x.Id == sp.StudioId; });
                    if (d == null) { if (sp.Pin > 0) e.Add(l + ": a pin is set for a studio that no longer exists"); continue; }
                    use(sp.Pin, d.Name);
                }
                if (used.Count == 0) e.Add(l + ": choose at least one pin");
            }
            return e;
        }

        static bool HasSpace(string v)
        {
            foreach (char ch in v ?? "") if (char.IsWhiteSpace(ch) || char.IsControl(ch)) return true;
            return false;
        }

        /// <summary>The studio meant by an id, code or name (case does not matter); null when none matches.</summary>
        public static StudioDef Find(StudiosCfg s, string text)
        {
            string t = (text ?? "").Trim();
            if (t.Length == 0) return null;
            foreach (StudioDef d in s.Studios) if (d.Id == t) return d;
            foreach (StudioDef d in s.Studios) if (string.Equals(d.Code, t, StringComparison.OrdinalIgnoreCase)) return d;
            foreach (StudioDef d in s.Studios) if (string.Equals(d.Name, t, StringComparison.OrdinalIgnoreCase)) return d;
            return null;
        }

        public static bool IsNone(string text)
        {
            string t = (text ?? "").Trim();
            return string.Equals(t, None, StringComparison.OrdinalIgnoreCase) || string.Equals(t, "standby", StringComparison.OrdinalIgnoreCase);
        }

        public static string NameOf(StudiosCfg s, string id)
        {
            if (string.IsNullOrEmpty(id)) return "no studio";
            StudioDef d = s.Studios.Find(delegate(StudioDef x) { return x.Id == id; });
            return d != null ? d.Name : "a studio that no longer exists";
        }

        public static string CodeOf(StudiosCfg s, string id)
        {
            if (string.IsNullOrEmpty(id)) return None;
            StudioDef d = s.Studios.Find(delegate(StudioDef x) { return x.Id == id; });
            return d != null ? d.Code : None;
        }
    }

    // --------------------------------------------------------------------- manager
    public class StudioManager
    {
        /// <summary>The state the watchdog reads (replaced as a whole, read without locks).</summary>
        sealed class Phase
        {
            public readonly string Active;     // "" = none
            public readonly bool Switching;
            public readonly string From, To;
            public Phase(string active, bool switching, string from, string to) { Active = active; Switching = switching; From = from; To = to; }
        }

        class Saved
        {
            public string Active = "";
            public double Since;
        }

        class Request
        {
            public string Target;          // studio id or "" (none)
            public string Source;
            public bool Manual;
        }

        readonly Func<AppConfig> config;
        readonly Supervisor programs;
        readonly GpioService gpio;
        readonly SourceLogger log;
        readonly string statePath;
        readonly object lk = new object();
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        volatile Phase phase = new Phase("", false, "", "");
        double activeSince;
        Request pending;
        double lastSwitchAt = -1e9;
        string lastSwitch = "";
        string lastSwitchError = "";
        string desired = "";
        string lastDesiredProblem = "";
        Thread worker, reporter;
        PathfinderLink link;
        bool loaded;
        // GPIO status output
        bool beat;
        double nextGpio;
        string gpioState = "off", gpioText = "";
        long heartbeat;

        /// <summary>(kind, key, title, message) - see AlertManager.</summary>
        public Action<string, string, string, string> Alert;
        public Func<bool> GlobalDryRun = delegate { return false; };
        public int TickMs = 250;

        public StudioManager(Func<AppConfig> config, Supervisor programs, GpioService gpio, LogHub hub, string stateDir)
        {
            this.config = config;
            this.programs = programs;
            this.gpio = gpio;
            this.log = hub.Logger("studios");
            this.statePath = Path.Combine(stateDir, "studio.json");
        }

        bool On(AppConfig c) { return c.General.Modules.Studios; }

        // ---------------------------------------------------------------- state
        /// <summary>Read the active studio saved by the last run (or the studio to start with).</summary>
        public void Load()
        {
            AppConfig c = config();
            string active = "";
            double since = Util.Now();
            bool fromFile = false;
            try
            {
                if (File.Exists(statePath))
                {
                    Saved s = new Saved();
                    Json.FillFrom(s, Json.Parse(File.ReadAllText(statePath)));
                    if (s != null) { active = s.Active ?? ""; since = s.Since > 0 ? s.Since : since; fromFile = true; }
                }
            }
            catch (Exception ex) { log.Warn("Cannot read the active studio (" + statePath + "): " + ex.Message); }
            if (!fromFile) active = c.Studios.StartStudio;
            if (active.Length > 0 && !c.Studios.Studios.Exists(delegate(StudioDef d) { return d.Id == active; }))
            {
                log.Warn("The active studio saved last time no longer exists; running no studio until Pathfinder or an operator chooses one.");
                active = "";
            }
            lock (lk)
            {
                phase = new Phase(active, false, "", "");
                activeSince = since;
                loaded = true;
            }
            if (On(c)) log.Info("Active studio: " + StudioSettings.NameOf(c.Studios, active) + (fromFile ? "" : " (the studio to start with)") + ".");
        }

        void SaveActive(string active, double since)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(statePath));
                Saved s = new Saved();
                s.Active = active;
                s.Since = since;
                Util.WriteAtomic(statePath, Json.Serialize(Json.ToJson(s), true));
            }
            catch (Exception ex) { log.Warn("Cannot save the active studio: " + ex.Message); }
        }

        static bool Has(ProgramJob p, string studio) { return studio.Length > 0 && p.Studios.Contains(studio); }

        /// <summary>For the watchdog: may this entry run now? Entries without studios always may. While a switch runs only
        /// entries that belong to both the old and the new studio run (the old studio's programs are being closed, the
        /// new studio's wait until they are gone, so a new entry never mistakes an old program for its own).</summary>
        public bool InStudio(ProgramJob p)
        {
            if (p.Studios.Count == 0) return true;
            AppConfig c = config();
            if (!On(c)) return true;
            Phase ph = phase;
            if (ph.Switching) return Has(p, ph.From) && Has(p, ph.To);
            return Has(p, ph.Active);
        }

        public string Active { get { return phase.Active; } }

        // ------------------------------------------------------------- requests
        /// <summary>Ask to run a studio (id, code or name; "none" for no studio). Manual requests (the manager) are not held
        /// back by the minimum time between switches.</summary>
        public ActionResult RequestSwitch(string target, string source, bool manual)
        {
            ActionResult r = new ActionResult();
            AppConfig c = config();
            if (!On(c)) { r.Text = "Switch studio - the Studios part is off on this server"; return r; }
            string id;
            string name;
            if (StudioSettings.IsNone(target)) { id = ""; name = "no studio"; }
            else
            {
                StudioDef d = StudioSettings.Find(c.Studios, target);
                if (d == null) { r.Text = "Switch studio - there is no studio '" + target + "'"; return r; }
                id = d.Id;
                name = d.Name;
            }
            lock (lk)
            {
                Phase ph = phase;
                if (!ph.Switching && ph.Active == id && pending == null)
                {
                    r.Ok = true;
                    r.Text = "Switch studio - " + name + " is already active";
                    return r;
                }
                Request q = new Request();
                q.Target = id;
                q.Source = source;
                q.Manual = manual;
                pending = q;
            }
            wake.Set();
            r.Ok = true;
            r.Text = "Switch studio - switching to " + name + " (asked by " + source + ")";
            return r;
        }

        /// <summary>For GPIO actions.</summary>
        public ActionResult SwitchFromGpio(string target, string source) { return RequestSwitch(target, source, false); }

        /// <summary>What Pathfinder's command slot holds (read every interval). Empty: no instruction.</summary>
        public void OnDesired(string value)
        {
            string v = (value ?? "").Trim();
            AppConfig c = config();
            lock (lk) desired = v;
            if (v.Length == 0 || !On(c)) return;
            string id;
            if (StudioSettings.IsNone(v)) id = "";
            else
            {
                StudioDef d = StudioSettings.Find(c.Studios, v);
                if (d == null)
                {
                    if (lastDesiredProblem != v)
                    {
                        lastDesiredProblem = v;
                        string msg = "Pathfinder asks for studio '" + v + "', which is not set up on this server (Studios page). Nothing was changed.";
                        log.Error(msg);
                        RaiseAlert("studio_problem", "studio:command", "Pathfinder asks for an unknown studio", msg);
                    }
                    return;
                }
                id = d.Id;
            }
            lastDesiredProblem = "";
            lock (lk)
            {
                Phase ph = phase;
                if (ph.Switching ? ph.To == id : ph.Active == id) return;
                if (pending != null && pending.Target == id) return;
                Request q = new Request();
                q.Target = id;
                q.Source = "Pathfinder";
                pending = q;
            }
            wake.Set();
        }

        void RaiseAlert(string kind, string key, string title, string message)
        {
            Action<string, string, string, string> a = Alert;
            if (a == null) return;
            try { a(kind, key, title, message); } catch (Exception) { }
        }

        // ------------------------------------------------------------- lifecycle
        public void Start()
        {
            if (!loaded) Load();
            if (worker != null) return;
            stop.Reset();
            worker = new Thread(Work);
            worker.Name = "studios";
            worker.IsBackground = true;
            worker.Start();
            reporter = new Thread(Report);
            reporter.Name = "studios-status";
            reporter.IsBackground = true;
            reporter.Start();
        }

        public void Stop()
        {
            stop.Set();
            wake.Set();
            PathfinderLink l;
            lock (lk) { l = link; link = null; }
            if (l != null) l.Stop();
        }

        void Work()
        {
            while (!stop.WaitOne(0))
            {
                wake.WaitOne(1000);
                if (stop.WaitOne(0)) break;
                Request q;
                AppConfig c = config();
                lock (lk)
                {
                    q = pending;
                    if (q == null) continue;
                    double wait = lastSwitchAt + c.Studios.MinSecondsBetweenSwitches - Util.Now();
                    if (!q.Manual && wait > 0) continue;        // tried again every second until the time is up
                    pending = null;
                }
                try { Switch(q, c); }
                catch (Exception ex) { log.Error("Studio switch failed: " + ex.Message); }
            }
        }

        /// <summary>Close the programs of the old studio, then make the new studio active and start its programs.</summary>
        void Switch(Request q, AppConfig c)
        {
            if (!On(c)) return;
            Phase before = phase;
            if (before.Active == q.Target) return;
            string fromName = StudioSettings.NameOf(c.Studios, before.Active), toName = StudioSettings.NameOf(c.Studios, q.Target);
            if (GlobalDryRun())
            {
                log.Info("[dry-run] Would switch from " + fromName + " to " + toName + " (asked by " + q.Source + ").");
                lock (lk) lastSwitchAt = Util.Now();
                return;
            }
            log.Warn("Switching from " + fromName + " to " + toName + " (asked by " + q.Source + ").");
            lock (lk)
            {
                phase = new Phase(before.Active, true, before.Active, q.Target);
                lastSwitchAt = Util.Now();
            }
            List<string> closed = new List<string>(), problems = new List<string>(), starting = new List<string>();
            try
            {
                foreach (ProgramJob p in c.Programs)
                {
                    if (!p.Enabled || !c.General.Modules.Programs) continue;
                    if (!(Has(p, before.Active) && !Has(p, q.Target))) continue;
                    string err = programs.CloseForSwitch(p, c.Studios.CloseTimeoutSeconds, delegate { return stop.WaitOne(0); });
                    if (err != null) problems.Add(err);
                    else closed.Add(p.Name);
                }
            }
            finally
            {
                double now = Util.Now();
                lock (lk)
                {
                    phase = new Phase(q.Target, false, "", "");
                    activeSince = now;
                }
                SaveActive(q.Target, now);
            }
            foreach (ProgramJob p in c.Programs)
            {
                if (!p.Enabled || !c.General.Modules.Programs) continue;
                if (Has(p, q.Target) && !Has(p, before.Active)) { programs.StartSoon(p.Id); starting.Add(p.Name); }
            }
            StringBuilder sb = new StringBuilder();
            sb.Append("This server now runs ").Append(toName).Append(" (before: ").Append(fromName).Append("), asked by ").Append(q.Source).Append('.');
            if (closed.Count > 0) sb.Append("\nClosed: ").Append(string.Join(", ", closed.ToArray())).Append('.');
            if (starting.Count > 0) sb.Append("\nStarting: ").Append(string.Join(", ", starting.ToArray())).Append('.');
            if (problems.Count > 0) sb.Append("\nNot closed: ").Append(string.Join("; ", problems.ToArray())).Append('.');
            string text = sb.ToString();
            lock (lk)
            {
                lastSwitch = toName + " at " + DateTime.Now.ToString("HH:mm:ss", Util.Inv) + ", asked by " + q.Source +
                             (problems.Count > 0 ? " - " + problems.Count + " program(s) did not close" : "");
                lastSwitchError = problems.Count > 0 ? string.Join("; ", problems.ToArray()) : "";
            }
            if (problems.Count > 0)
            {
                log.Error(text.Replace("\n", " "));
                RaiseAlert("studio_problem", "studio:switch", "Studio switch to " + toName + ": programs did not close", text);
            }
            else
            {
                log.Warn(text.Replace("\n", " "));
                RaiseAlert("studio_switch", "studio:switch", "Now running " + toName, text);
            }
        }

        // ---------------------------------------------------------------- status
        /// <summary>ok | degraded | switching | error, and why.</summary>
        public string ComputeState(AppConfig c, out string text)
        {
            Phase ph = phase;
            if (ph.Switching)
            {
                text = "Switching to " + StudioSettings.NameOf(c.Studios, ph.To);
                return "switching";
            }
            string err;
            lock (lk) err = lastSwitchError;
            if (err.Length > 0) { text = "The last switch left programs running: " + err; return "error"; }
            if (!c.General.Modules.Programs) { text = "The watchdog part is off"; return "ok"; }
            List<string> bad = new List<string>();
            foreach (ProgramStatus s in programs.Snapshot())
            {
                if (!s.Enabled || !s.InStudio) continue;
                if (s.State != "running") bad.Add(s.Name + ": " + (s.StateText.Length > 0 ? s.StateText : s.State));
                else if (s.Health == "failing") bad.Add(s.Name + ": not answering");
            }
            if (bad.Count > 0) { text = string.Join("; ", bad.ToArray()); return "degraded"; }
            text = ph.Active.Length == 0 ? "No studio is active; the programs that belong to no studio run" : "Everything in " + StudioSettings.NameOf(c.Studios, ph.Active) + " runs";
            return "ok";
        }

        public StudioStatus Status()
        {
            AppConfig c = config();
            StudioStatus s = new StudioStatus();
            s.On = On(c);
            Phase ph = phase;
            s.Active = ph.Active;
            s.ActiveName = StudioSettings.NameOf(c.Studios, ph.Active);
            s.ActiveCode = StudioSettings.CodeOf(c.Studios, ph.Active);
            if (!s.On) { s.State = "off"; s.StateText = "The Studios part is off"; return s; }
            string text;
            s.State = ComputeState(c, out text);
            s.StateText = text;
            if (ph.Switching) s.Switching = StudioSettings.NameOf(c.Studios, ph.To);
            PathfinderLink l;
            lock (lk)
            {
                s.ActiveSince = activeSince;
                s.LastSwitch = lastSwitch;
                s.LastSwitchAt = lastSwitchAt > 0 ? lastSwitchAt : 0;
                s.Desired = desired;
                s.GpioState = gpioState;
                s.GpioText = gpioText;
                s.Heartbeat = heartbeat;
                l = link;
            }
            if (l != null) l.Describe(s);
            else { s.PathfinderState = "off"; s.PathfinderText = c.Studios.Pathfinder.Enabled ? "Starting" : "Not used"; }
            return s;
        }

        // ------------------------------------------------------- status reporting
        void Report()
        {
            while (!stop.WaitOne(TickMs))
            {
                try { ReportOnce(); }
                catch (Exception ex) { log.Error("Studio status: " + ex.Message); }
            }
        }

        /// <summary>Keep the Pathfinder link matching the settings and set the GPIO status pins when due.</summary>
        public void ReportOnce()
        {
            AppConfig c = config();
            bool on = On(c);
            // Pathfinder link
            PathfinderCfg pc = c.Studios.Pathfinder;
            bool wantLink = on && pc.Enabled && pc.Host.Length > 0;
            string sig = wantLink ? PathfinderLink.Signature(pc) : "";
            PathfinderLink old = null, start = null;
            lock (lk)
            {
                if (link != null && link.Sig != sig) { old = link; link = null; }
                if (wantLink && link == null) { link = new PathfinderLink(pc, sig, this, log); start = link; }
            }
            if (old != null) old.Stop();
            if (start != null) start.Start();

            // GPIO pins
            GpioStatusCfg g = c.Studios.GpioStatus;
            if (!on || !g.Enabled || gpio == null)
            {
                lock (lk) { gpioState = "off"; gpioText = g.Enabled ? "The Studios part is off" : "Not used"; }
                return;
            }
            double now = Util.Now();
            if (now < nextGpio) return;
            nextGpio = now + g.IntervalSeconds;
            beat = !beat;
            string line = GpioLine(c, beat);
            string err = gpio.SendLine(g.DeviceId, line);
            lock (lk)
            {
                if (err == null) { gpioState = "ok"; gpioText = "Last sent: " + line; }
                else
                {
                    if (gpioState != "error") log.Warn("GPIO status not sent: " + err);
                    gpioState = "error";
                    gpioText = err;
                }
            }
        }

        /// <summary>The LWRP line that sets the status pins ("GPI 1 lhxxx"): low = on. Pins not used are left alone (x).</summary>
        public string GpioLine(AppConfig c, bool beatLow)
        {
            GpioStatusCfg g = c.Studios.GpioStatus;
            char[] pins = { 'x', 'x', 'x', 'x', 'x' };
            string text;
            string state = ComputeState(c, out text);
            Phase ph = phase;
            if (g.HeartbeatPin > 0) pins[g.HeartbeatPin - 1] = beatLow ? 'l' : 'h';
            if (g.OkPin > 0) pins[g.OkPin - 1] = state == "ok" ? 'l' : 'h';
            foreach (StudioPin sp in g.StudioPins)
                if (sp.Pin > 0) pins[sp.Pin - 1] = !ph.Switching && ph.Active == sp.StudioId ? 'l' : 'h';
            return (g.Signal == "gpo" ? "GPO " : "GPI ") + g.Port + " " + new string(pins);
        }

        /// <summary>For the Pathfinder link: the values to write (heartbeat counter, status, studio code).</summary>
        internal void Values(out long beatCount, out string status, out string studio)
        {
            AppConfig c = config();
            string text;
            status = ComputeState(c, out text);
            Phase ph = phase;
            studio = ph.Switching ? "switching" : StudioSettings.CodeOf(c.Studios, ph.Active);
            lock (lk) beatCount = ++heartbeat;
        }

        /// <summary>Connect with these (unsaved) settings, log in and read the memory slots: the whole exchange.</summary>
        public static string TestPathfinder(PathfinderCfg p)
        {
            NetTrace t = new NetTrace();
            try { PathfinderLink.Test(p, t); }
            catch (Exception ex) { t.Add("Failed: " + ex.Message); }
            return t.ToString();
        }
    }

    // ------------------------------------------------------------ Pathfinder SapV2
    /// <summary>A connection to Pathfinder Core PRO's SapV2 protocol: "Login user password", then
    /// "set MemorySlots#0.MemorySlot#Name SlotValue="value"" and "get MemorySlots#0.MemorySlot#Name SlotValue"
    /// (answered with "indi MemorySlots#0.MemorySlot#Name SlotValue="value""). Lines end with CR LF.</summary>
    public class PathfinderLink
    {
        readonly PathfinderCfg cfg;
        public readonly string Sig;
        readonly StudioManager owner;
        readonly SourceLogger log;
        readonly ManualResetEvent stopped = new ManualResetEvent(false);
        readonly object lk = new object();
        Thread thread;
        TcpClient client;
        string state = "connecting", text = "Connecting";
        double since = Util.Now();
        bool wasConnected;

        public PathfinderLink(PathfinderCfg cfg, string sig, StudioManager owner, SourceLogger log)
        {
            this.cfg = Json.Clone(cfg);
            Sig = sig;
            this.owner = owner;
            this.log = log;
        }

        public static string Signature(PathfinderCfg p)
        {
            return Json.Serialize(Json.ToJson(p), false);
        }

        public void Start()
        {
            thread = new Thread(Run);
            thread.Name = "pathfinder";
            thread.IsBackground = true;
            thread.Start();
        }

        public void Stop()
        {
            stopped.Set();
            try { if (client != null) client.Close(); } catch (Exception) { }
        }

        public void Describe(StudioStatus s)
        {
            lock (lk)
            {
                s.PathfinderState = state;
                s.PathfinderText = text;
                s.PathfinderSince = since;
            }
        }

        void SetState(string st, string t)
        {
            lock (lk)
            {
                if (st != state) since = Util.Now();
                state = st;
                text = t;
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
                    if (wasConnected) log.Warn("Pathfinder (" + cfg.Host + "): " + msg);
                    else if (state != "error" || text != msg) log.Warn("Pathfinder (" + cfg.Host + "): " + msg);
                    wasConnected = false;
                    SetState("error", msg);
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

        /// <summary>Connect (TLS when set), check the certificate, and log in. Throws NetFailure.</summary>
        static Stream Open(PathfinderCfg p, NetTrace t, out TcpClient client)
        {
            string plain = Net.PlainLoginProblem(new Uri("http://" + (p.Host.Contains(":") ? "[" + p.Host + "]" : p.Host) + "/"), p.User, null, p.AllowPlainLogin || p.Tls);
            if (plain != null)
                throw new NetFailure("login", "The Pathfinder login is not sent to " + p.Host + " without TLS.",
                                     "Use TLS (port 9602), or switch on 'Send the login unencrypted'.", null);
            client = Net.Connect(p.Host, p.Port, 5000, t);
            try { return Login(p, t, client); }
            catch (Exception)
            {
                try { client.Close(); } catch (Exception) { }
                throw;
            }
        }

        static Stream Login(PathfinderCfg p, NetTrace t, TcpClient client)
        {
            try { client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true); } catch (Exception) { }
            Stream s = client.GetStream();
            if (p.Tls)
            {
                string problem = "";
                SslStream ssl = new SslStream(s, false, delegate(object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors errors)
                {
                    string subject = cert != null ? cert.Subject : "(none)";
                    if (Net.IsRevoked(chain)) { problem = "Pathfinder's certificate (" + subject + ") has been revoked."; return false; }
                    if (Net.CertificateVerdict(errors, chain) == SslPolicyErrors.None) { if (t != null) t.Add("Certificate OK: " + subject); return true; }
                    string fp = SmtpSender.Fingerprint(cert), want = SmtpSender.Clean(p.TrustFingerprint);
                    if (want.Length > 0 && want == fp) { if (t != null) t.Add("Certificate accepted by its fingerprint: " + subject); return true; }
                    problem = "Pathfinder's certificate (" + subject + ") is not trusted by this computer" +
                              (fp.Length > 0 ? "; to trust exactly this certificate, enter its fingerprint " + fp : "") + ".";
                    if (t != null) t.Add(problem);
                    return false;
                });
                try { ssl.AuthenticateAsClient(p.Host, null, SslProtocols.Tls12, true); }
                catch (Exception ex)
                {
                    throw new NetFailure("TLS", problem.Length > 0 ? problem : "The TLS handshake failed: " + ex.Message,
                                         "Pathfinder Core PRO offers TLS on port 9602 once its certificate is set up.", ex);
                }
                s = ssl;
            }
            s.ReadTimeout = 5000;
            s.WriteTimeout = 5000;
            Write(s, "Login " + p.User + " " + Secrets.Unprotect(p.Password), "Login " + p.User + " ********", t);
            double until = Util.Now() + 5;
            LineReader r = new LineReader(s);
            while (Util.Now() < until)
            {
                string line = r.Next(500);
                if (line == null) continue;
                if (t != null) t.Add("S: " + Net.OneLine(line, 300));
                string low = line.ToLowerInvariant();
                if (low.Contains("login successful")) return new ReaderStream(s, r);
                if (low.Contains("login failed"))
                    throw new NetFailure("login", "Pathfinder refused the login.", "Check the user name and password (a Pathfinder Core PRO user).", null);
            }
            throw new NetFailure("login", "Pathfinder did not answer the login within 5 seconds.", "Check the address and port (SapV2: 9600, or 9602 with TLS).", null);
        }

        static void Write(Stream s, string line, string shown, NetTrace t)
        {
            byte[] b = Encoding.UTF8.GetBytes(line + "\r\n");
            s.Write(b, 0, b.Length);
            s.Flush();
            if (t != null) t.Add("C: " + (shown ?? line));
        }

        public static string SetLine(string slot, string value)
        {
            return "set MemorySlots#0.MemorySlot#" + slot + " SlotValue=\"" + (value ?? "").Replace("\"", "'") + "\"";
        }

        public static string GetLine(string slot) { return "get MemorySlots#0.MemorySlot#" + slot + " SlotValue"; }

        static readonly Regex Indi = new Regex("^indi\\s+MemorySlots#0\\.MemorySlot#(\\S+)\\s+(.*)$", RegexOptions.IgnoreCase);
        static readonly Regex Value = new Regex("SlotValue=(\"([^\"]*)\"|(\\S*))", RegexOptions.IgnoreCase);

        /// <summary>The SlotValue of the named slot in an "indi" line, or null when the line is about something else.</summary>
        public static string ParseSlotValue(string line, string slot)
        {
            Match m = Indi.Match((line ?? "").Trim());
            if (!m.Success || !string.Equals(m.Groups[1].Value, slot, StringComparison.OrdinalIgnoreCase)) return null;
            Match v = Value.Match(m.Groups[2].Value);
            if (!v.Success) return null;
            string val = v.Groups[2].Success ? v.Groups[2].Value : v.Groups[3].Value;
            return val.Replace("%BeginEncap%", "").Replace("%EndEncap%", "").Trim();
        }

        void Session()
        {
            SetState("connecting", "Connecting to " + cfg.Host + ":" + cfg.Port);
            ReaderStream s = (ReaderStream)Open(cfg, null, out client);
            SetState("connected", "Connected to " + cfg.Host + ":" + cfg.Port + (cfg.Tls ? " (TLS)" : ""));
            log.Info("Pathfinder: connected to " + cfg.Host + ":" + cfg.Port + (cfg.Tls ? " with TLS" : "") + " as " + cfg.User + ".");
            wasConnected = true;
            string lastStatus = null, lastStudio = null;
            double lastFull = 0, lastAnswer = Util.Now();
            bool asked = false;
            while (!stopped.WaitOne(0))
            {
                long n;
                string status, studio;
                owner.Values(out n, out status, out studio);
                double now = Util.Now();
                bool full = now - lastFull >= 30;
                if (cfg.HeartbeatSlot.Length > 0) Write(s.Inner, SetLine(cfg.HeartbeatSlot, (n % 1000000).ToString(Util.Inv)), null, null);
                if (cfg.StatusSlot.Length > 0 && (full || status != lastStatus)) Write(s.Inner, SetLine(cfg.StatusSlot, status), null, null);
                if (cfg.StudioSlot.Length > 0 && (full || studio != lastStudio)) Write(s.Inner, SetLine(cfg.StudioSlot, studio), null, null);
                if (full) lastFull = now;
                lastStatus = status;
                lastStudio = studio;
                if (cfg.CommandSlot.Length > 0) { Write(s.Inner, GetLine(cfg.CommandSlot), null, null); asked = true; }
                // read whatever arrives until the next round
                double until = Util.Now() + cfg.IntervalSeconds;
                while (Util.Now() < until && !stopped.WaitOne(0))
                {
                    string line = s.Reader.Next(200);
                    if (line == null) continue;
                    lastAnswer = Util.Now();
                    if (cfg.CommandSlot.Length > 0)
                    {
                        string v = ParseSlotValue(line, cfg.CommandSlot);
                        if (v != null) owner.OnDesired(v);
                    }
                    if (line.ToLowerInvariant().Contains("error")) log.Debug("Pathfinder: " + Net.OneLine(line, 300));
                }
                if (asked && Util.Now() - lastAnswer > Math.Max(15, cfg.IntervalSeconds * 5))
                    throw new IOException("Pathfinder has not answered for " + (int)(Util.Now() - lastAnswer) + " seconds; reconnecting");
            }
        }

        public static void Test(PathfinderCfg p, NetTrace t)
        {
            TcpClient c;
            ReaderStream s = (ReaderStream)Open(p, t, out c);
            try
            {
                t.Add("Logged in.");
                foreach (string slot in new[] { p.HeartbeatSlot, p.StatusSlot, p.StudioSlot, p.CommandSlot })
                {
                    if (slot.Length == 0) continue;
                    Write(s.Inner, GetLine(slot), null, t);
                    double until = Util.Now() + 2;
                    bool found = false;
                    while (Util.Now() < until && !found)
                    {
                        string line = s.Reader.Next(200);
                        if (line == null) continue;
                        t.Add("S: " + Net.OneLine(line, 300));
                        if (ParseSlotValue(line, slot) != null) found = true;
                    }
                    if (!found) t.Add("No value came back for " + slot + ": check that this memory slot exists in Pathfinder (Memory Slots page).");
                }
            }
            finally { c.Close(); }
        }

        // ----------------------------------------------------------- line reading
        /// <summary>Reads CR LF lines from a stream with a time-out per wait, without losing data between calls.</summary>
        internal class LineReader
        {
            readonly Stream s;
            readonly List<byte> buf = new List<byte>();
            readonly byte[] chunk = new byte[4096];
            readonly Queue<string> lines = new Queue<string>();
            IAsyncResult pendingRead;

            public LineReader(Stream s) { this.s = s; }

            /// <summary>The next line, or null when none arrived within ms. Throws when the connection closed.</summary>
            public string Next(int ms)
            {
                if (lines.Count > 0) return lines.Dequeue();
                if (pendingRead == null) pendingRead = s.BeginRead(chunk, 0, chunk.Length, null, null);
                if (!pendingRead.AsyncWaitHandle.WaitOne(ms)) return null;
                int n = s.EndRead(pendingRead);
                pendingRead = null;
                if (n <= 0) throw new IOException("Pathfinder closed the connection");
                for (int i = 0; i < n; i++)
                {
                    byte b = chunk[i];
                    if (b == '\n')
                    {
                        string line = Encoding.UTF8.GetString(buf.ToArray()).Trim('\r', ' ', '\t');
                        buf.Clear();
                        if (line.Length > 0) lines.Enqueue(line);
                    }
                    else if (buf.Count < 65536) buf.Add(b);
                }
                return lines.Count > 0 ? lines.Dequeue() : null;
            }
        }

        /// <summary>The logged-in stream together with its reader (lines already read stay with it).</summary>
        internal class ReaderStream : Stream
        {
            public readonly Stream Inner;
            public readonly LineReader Reader;
            public ReaderStream(Stream inner, LineReader reader) { Inner = inner; Reader = reader; }
            public override bool CanRead { get { return false; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return true; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override void Flush() { Inner.Flush(); }
            public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { Inner.Write(buffer, offset, count); }
        }
    }
}
