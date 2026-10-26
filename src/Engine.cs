// The engine: everything the service does. Loads the configuration file, keeps watching it,
// schedules folder policies and scripts, supervises programs, runs the vertical converter,
// sends alerts and answers the manager window over the control pipe.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace Watchkeeper
{
    public class EngineOptions
    {
        public Func<List<Adapter>> AdapterDetector;
        public ILauncher Launcher;
        public IProcessTable Table;
        public ISessions Sessions;
        public IServiceControl Services;
        public bool StartControl = true;
        public string Mode = "console";        // service | console
        public string PipeName;                // default: derived from the data folder
    }

    public class EngineStatus
    {
        public string Version = "";
        public string Mode = "";
        public int Pid;
        public double StartedAt;
        public string Machine = "";
        public string Priority = "";
        public string DataDir = "";
        public string ConfigPath = "";
        public string ConfigHash = "";
        public double ConfigLoadedAt;
        public List<string> ConfigProblems = new List<string>();
        /// <summary>Why nothing runs (the settings folder is not protected, or only the backup copy could be read), or empty.</summary>
        public string SafeMode = "";
        public bool DryRun;
        public int ConsoleSession = -1;
        public string ConsoleUser = "";
        public List<JobStatus> Jobs = new List<JobStatus>();
        public List<ProgramStatus> Programs = new List<ProgramStatus>();
        public ConverterStatus Vertical = new ConverterStatus();
        public GpioStatus Gpio = new GpioStatus();
        public StudioStatus Studios = new StudioStatus();
        public List<AlertRecord> Alerts = new List<AlertRecord>();
    }

    public class PreviewResult
    {
        public TaskResult Result = new TaskResult();
        public List<PlanItem> Plan = new List<PlanItem>();
        public List<string> Lines = new List<string>();
    }

    public class Engine
    {
        public const string Version = "3.2.2";

        public readonly string DataDir;
        public readonly LogHub Hub;
        public readonly ConfigStore Config;
        public readonly AdapterCache Adapters;
        public readonly ILauncher Launcher;
        public readonly IProcessTable Table;
        public readonly ISessions Sessions;
        public readonly AlertManager Alerts;
        public readonly Converter Vertical;
        public readonly JobManager Jobs;
        public readonly Supervisor Programs;
        public readonly FileTriggers Triggers;
        public readonly GpioService Gpio;
        public readonly StudioManager Studios;
        public readonly ControlServer Control;
        public readonly string Mode;
        readonly SourceLogger log;
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        SecureResult secured;
        string secureError;
        double startedAt;
        Thread watcher;
        string appliedPriority = "";

        public Engine(string dataDir, EngineOptions o)
        {
            if (o == null) o = new EngineOptions();
            DataDir = Path.GetFullPath(dataDir);
            Mode = o.Mode;
            // The settings folder decides what runs as Local System: owned by Administrators, only Administrators and
            // Local System may open it. Files another account planted there are set aside, never loaded.
            try { secured = DataDirSecurity.Secure(DataDir); }
            catch (Exception ex) { secureError = ex.Message; Directory.CreateDirectory(DataDir); }
            Directory.CreateDirectory(StateDir);
            Hub = new LogHub(5000);
            Hub.AttachFile(Path.Combine(DataDir, "watchkeeper.log"), 5000000, 9);
            log = Hub.Logger("service");
            Config = new ConfigStore(Path.Combine(DataDir, "config.json"), Hub.Logger("config"));
            Config.Guard = delegate { return DataDirSecurity.Verify(DataDir); };
            Adapters = o.AdapterDetector == null ? new AdapterCache() : new AdapterCache(300, o.AdapterDetector);
            Launcher = o.Launcher ?? new SystemLauncher();
            Table = o.Table ?? new SystemProcessTable();
            Sessions = o.Sessions ?? new SystemSessions();

            Alerts = new AlertManager(Config.Get, Hub);
            Vertical = new Converter(Config, Hub, Adapters, Launcher, Sessions);
            Vertical.Alert = Alerts.Raise;
            Jobs = new JobManager(BuildSpecs, Hub, Path.Combine(StateDir, "jobs.json"));
            Jobs.GlobalDryRun = delegate { return Config.Get().General.DryRun; };
            Jobs.Finished = OnJobFinished;
            Programs = new Supervisor(Config.Get, Launcher, Table, Sessions, o.Services ?? new SystemServiceControl(), Hub);
            Programs.Alert = Alerts.Raise;
            Triggers = new FileTriggers(Config.Get, Jobs, Hub, StateDir);
            Triggers.Alert = Alerts.Raise;
            Gpio = new GpioService(Config.Get, Hub);
            Gpio.Alert = Alerts.Raise;
            Studios = new StudioManager(Config.Get, Programs, Gpio, Hub, StateDir);
            Studios.Alert = Alerts.Raise;
            Studios.GlobalDryRun = delegate { return Config.Get().General.DryRun; };
            Programs.InStudio = Studios.InStudio;
            GpioService.StudioSwitch = Studios.SwitchFromGpio;
            if (o.StartControl)
            {
                Control = new ControlServer(o.PipeName ?? ControlChannel.PipeName(DataDir), Handle, Hub.Logger("service"));
                Control.Squatted = delegate(string msg) { Alerts.Raise("config_problem", "service:pipe", "The manager cannot reach the service", msg); };
            }
            Config.Changed += OnConfigChanged;
        }

        public string StateDir { get { return Path.Combine(DataDir, "state"); } }

        public static string DefaultDataDir()
        {
            string pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrEmpty(pd)) pd = Path.GetTempPath();
            return Path.Combine(pd, "Watchkeeper");
        }

        // ------------------------------------------------------------------ jobs
        List<JobSpec> BuildSpecs()
        {
            AppConfig cfg = Config.Get();
            GeneralCfg general = cfg.General;
            List<JobSpec> o = new List<JobSpec>();
            foreach (FolderPolicy f in cfg.Folders)
            {
                FolderPolicy policy = f;
                JobSpec s = new JobSpec();
                s.Key = "folder:" + f.Id;
                s.Kind = "folder";
                s.Id = f.Id;
                s.Name = f.Name;
                s.Source = "folders";
                s.Enabled = f.Enabled && general.Modules.Folders;
                s.Schedule = f.Schedule;
                s.Run = delegate(RunContext ctx)
                {
                    return FolderRunner.Run(policy, general, ctx.Log, ctx.DryRun, null, 0, delegate { return ctx.Cancelled; });
                };
                o.Add(s);
            }
            foreach (ScriptJob sc in cfg.Scripts)
            {
                ScriptJob script = sc;
                JobSpec s = new JobSpec();
                s.Key = "script:" + sc.Id;
                s.Kind = "script";
                s.Id = sc.Id;
                s.Name = sc.Name;
                s.Source = "scripts";
                s.Enabled = sc.Enabled && general.Modules.Scripts;
                s.Schedule = sc.Trigger == "schedule" ? sc.Schedule : null;
                s.AtStartup = sc.Trigger == "startup";
                s.Run = delegate(RunContext ctx) { return ScriptRunner.Run(script, ctx, Launcher); };
                o.Add(s);
            }
            return o;
        }

        readonly HashSet<string> failedJobs = new HashSet<string>();

        void OnJobFinished(JobSpec spec, TaskResult result, RunContext ctx)
        {
            if (result.Ok)
            {
                bool was;
                lock (failedJobs) was = failedJobs.Remove(spec.Key);
                if (was) Alerts.Raise("recovered", spec.Key, spec.Name + " works again",
                                      spec.Name + " (" + (spec.Kind == "folder" ? "folder policy" : "script") + ") ran without problems again: " + result.Text);
                return;
            }
            if (ctx.Trigger == "manual") return;
            lock (failedJobs) failedJobs.Add(spec.Key);
            StringBuilder sb = new StringBuilder();
            sb.Append(spec.Name).Append(" (").Append(spec.Kind == "folder" ? "folder policy" : "script").Append(") failed: ")
              .Append(result.Text).Append("\n\nTrigger: ").Append(ctx.Trigger);
            if (ctx.File.Length > 0) sb.Append(" (").Append(ctx.File).Append(")");
            List<string> lines = Jobs.Lines(spec.Key);
            bool output = Config.Get().Alerts.IncludeScriptOutput;
            List<string> bad = lines.FindAll(delegate(string l) { return (l.Contains("ERROR") || l.Contains("WARN")) && (output || !l.StartsWith("  ")); });
            if (bad.Count > 0)
            {
                sb.Append("\n\nProblems:\n");
                for (int i = Math.Max(0, bad.Count - 15); i < bad.Count; i++) sb.Append(bad[i]).Append('\n');
            }
            Alerts.Raise("task_failure", spec.Key, spec.Name + " failed", sb.ToString());
        }

        // --------------------------------------------------------------- lifecycle
        public void Start()
        {
            startedAt = Util.Now();
            AlertManager.EnableModernTls();
            if (secured != null) foreach (string a in secured.Actions) log.Warn(a);
            if (secureError != null) log.Error("Could not protect the settings folder " + DataDir + ": " + secureError);
            Config.Load(true);
            ConvertSecrets();
            ApplyPriority();
            AppConfig cfg = Config.Get();
            log.Info("Watchkeeper " + Version + " started (" + Mode + ", PID " + Process.GetCurrentProcess().Id +
                     "). Data folder: " + DataDir + ". Configuration " + Config.LoadedHash + " with " + cfg.Folders.Count +
                     " folder(s), " + cfg.Programs.Count + " program(s), " + cfg.Scripts.Count + " script(s)." +
                     (cfg.General.DryRun ? " DRY-RUN MODE: nothing is changed." : ""));
            Alerts.Start();
            if (Mode == "service")
            {
                foreach (string d in LaunchSafety.HardenServiceEnvironment())
                    log.Warn("Left out of the search path (PATH) for programs run as Local System: " + d);
                string why = PathTrust.Check(System.Reflection.Assembly.GetExecutingAssembly().Location);
                if (why != null)
                {
                    log.Error("The service program is not protected: " + why);
                    Alerts.Raise("config_problem", "service:untrusted", "The Watchkeeper program file is not protected", why);
                }
            }
            Jobs.Start();
            Studios.Load();
            Programs.Start();
            Triggers.Start();
            Gpio.Start();
            Studios.Start();
            ApplyVertical();
            watcher = new Thread(WatchConfig);
            watcher.Name = "config-watch";
            watcher.IsBackground = true;
            watcher.Start();
            if (Control != null) Control.Start();

            List<string> problems = Config.Problems;
            Alerts.Raise("service_start", "service", "Service started",
                         "The Watchkeeper service started.\n\nEnabled: " + Count(cfg.Folders.FindAll(delegate(FolderPolicy f) { return f.Enabled; }).Count, "folder policy", "folder policies") +
                         ", " + Count(cfg.Programs.FindAll(delegate(ProgramJob p) { return p.Enabled; }).Count, "program", "programs") +
                         ", " + Count(cfg.Scripts.FindAll(delegate(ScriptJob s) { return s.Enabled; }).Count, "script", "scripts") +
                         (cfg.Vertical.Enabled ? ", vertical conversion" : "") + "." +
                         (cfg.General.DryRun ? "\nDry-run mode is on: nothing is changed." : ""));
            if (problems.Count > 0) RaiseConfigProblems(problems);
            if (secured != null && secured.SetAside.Count > 0)
                Alerts.Raise("config_problem", "config:untrusted", "Files in the settings folder were set aside",
                             "These files were created or changed by an account that is not an administrator, so they were renamed to *.untrusted " +
                             "and are not loaded:\n- " + string.Join("\n- ", secured.SetAside.ToArray()) +
                             "\n\nCheck who could write to " + DataDir + ". The settings folder is now restricted to Administrators and Local System.");
        }

        static string Count(int n, string one, string many) { return n + " " + (n == 1 ? one : many); }

        void RaiseConfigProblems(List<string> problems)
        {
            Alerts.Raise("config_problem", "config", "Configuration problems",
                         "The configuration file " + Config.Path + " has problems:\n- " + string.Join("\n- ", problems.ToArray()));
        }

        void WatchConfig()
        {
            while (!stop.WaitOne(2000))
            {
                try
                {
                    List<string> problems = Config.CheckForChanges();
                    if (problems != null && problems.Count > 0) RaiseConfigProblems(problems);
                }
                catch (Exception ex) { log.Error("Configuration watch failed: " + ex.Message); }
            }
        }

        public void Shutdown() { Shutdown(false); }

        /// <summary>fast: Windows is shutting down and allows only a few seconds.</summary>
        public void Shutdown(bool fast)
        {
            log.Info("Watchkeeper stopping" + (fast ? " (Windows is shutting down)" : "") + ". Supervised programs are left running.");
            stop.Set();
            if (Control != null) Control.Stop();
            Triggers.Stop();
            Studios.Stop();
            Gpio.Stop();
            Programs.Stop();
            Jobs.Stop();
            Vertical.Stop(true, fast ? 2000 : 8000);
            Alerts.Stop(fast ? 500 : 3000);
            log.Info("Watchkeeper stopped.");
        }

        /// <summary>The service stores every password, token and secret address with its own key: converted when the file
        /// holds one in plain text (typed in, deployed) or with this computer's key (saved while the service was stopped).</summary>
        void ConvertSecrets()
        {
            if (Mode != "service" || !Util.IsWindows) return;
            try
            {
                int n = Config.ConvertSecrets(Secrets.ProtectService);
                if (n > 0) log.Info(n + " password(s) or token(s) in the configuration re-encrypted with the service's own key.");
            }
            catch (Exception ex) { log.Warn("Could not re-encrypt the passwords in the configuration: " + ex.Message); }
        }

        void OnConfigChanged(string section)
        {
            if (section == "*") ConvertSecrets();
            ApplyVertical();
            ApplyPriority();
        }

        public void ApplyVertical()
        {
            AppConfig c = Config.Get();
            Util.MediaAccount = c.General.FfmpegAccount;
            if (c.Vertical.Enabled && c.General.Modules.Vertical) Vertical.Start();
            else Vertical.Stop();
        }

        void ApplyPriority()
        {
            string want = Config.Get().General.ServicePriority;
            if (want == appliedPriority) return;
            try
            {
                using (Process me = Process.GetCurrentProcess()) me.PriorityClass = Procs.ToClass(want);
                appliedPriority = want;
                log.Info("Process priority: " + want.Replace('_', ' ') + ".");
            }
            catch (Exception ex) { log.Warn("Cannot set process priority to " + want + ": " + ex.Message); }
        }

        // ------------------------------------------------------------------ status
        public EngineStatus Status()
        {
            EngineStatus s = new EngineStatus();
            AppConfig cfg = Config.Get();
            s.Version = Version;
            s.Mode = Mode;
            using (Process me = Process.GetCurrentProcess())
            {
                s.Pid = me.Id;
                try { s.Priority = me.PriorityClass.ToString(); } catch (Exception) { s.Priority = ""; }
            }
            s.StartedAt = startedAt;
            s.Machine = Environment.MachineName;
            s.DataDir = DataDir;
            s.ConfigPath = Config.Path;
            s.ConfigHash = Config.LoadedHash;
            s.ConfigLoadedAt = Config.LoadedAt;
            s.ConfigProblems = Config.Problems;
            s.SafeMode = Config.SafeReason ?? "";
            s.DryRun = cfg.General.DryRun;
            string user;
            s.ConsoleSession = Sessions.ConsoleUserSession(out user);
            s.ConsoleUser = user ?? "";
            s.Jobs = Jobs.Snapshot();
            s.Programs = Programs.Snapshot();
            s.Vertical = Vertical.Status();
            s.Gpio = Gpio.Status();
            s.Studios = Studios.Status();
            s.Alerts = Alerts.Recent();
            if (s.Alerts.Count > 30) s.Alerts.RemoveRange(30, s.Alerts.Count - 30);
            return s;
        }

        public PreviewResult Preview(FolderPolicy f, int limit)
        {
            Cfg.NormalizeFolder(f, new List<string>());
            PreviewResult p = new PreviewResult();
            LogHub quiet = new LogHub(10);
            SourceLogger l = quiet.Logger("preview").WithCapture(p.Lines);
            p.Result = FolderRunner.Run(f, Config.Get().General, l, true, p.Plan, limit, null);
            return p;
        }

        /// <summary>"keep:ref" from the manager: the saved value of that entry (secrets are not sent back and forth).</summary>
        static string Kept(string value, string saved, string reference)
        {
            return Secrets.Resolve(value, delegate(string r) { return r == reference ? saved : null; });
        }

        // ------------------------------------------------------------- control pipe
        static JObject Ok(string key, object value)
        {
            JObject o = new JObject();
            o.Set("ok", true);
            if (key != null) o.Set(key, Json.ToJson(value));
            return o;
        }

        public JObject Handle(JObject req)
        {
            string cmd = req.Str("cmd", "");
            string msg;
            switch (cmd)
            {
                case "ping":
                    {
                        JObject o = Ok(null, null);
                        o.Set("version", Version);
                        o.Set("mode", Mode);
                        using (Process me = Process.GetCurrentProcess()) o.Set("pid", (double)me.Id);
                        return o;
                    }
                case "status":
                    return Ok("status", Status());
                case "reload":
                    {
                        List<string> problems = Config.Reload();
                        return Ok("problems", problems);
                    }
                case "job.run":
                    if (!Jobs.RunNow(req.Str("key", ""), req.Bool("dry", false), out msg)) throw new InvalidOperationException(msg);
                    return Ok("message", msg);
                case "job.abort":
                    return Ok("stopped", Jobs.Abort(req.Str("key", "")));
                case "job.lines":
                    return Ok("lines", Jobs.Lines(req.Str("key", "")));
                case "folder.preview":
                    {
                        FolderPolicy f = req.As<FolderPolicy>("folder");
                        if (f == null) throw new InvalidOperationException("no folder policy in the request");
                        return Ok("preview", Preview(f, (int)req.Num("limit", 2000)));
                    }
                case "program.restart":
                    if (!Programs.RequestRestart(req.Str("id", ""))) throw new InvalidOperationException("unknown program, or it has not been checked yet");
                    return Ok(null, null);
                case "vertical.abort":
                    Vertical.AbortCurrent();
                    return Ok(null, null);
                case "vertical.retry":
                    return Ok("count", Vertical.RetryFailed());
                case "gpu.detect":
                    return Ok("adapters", Adapters.Get(req.Bool("refresh", false)));
                case "gpu.test":
                    {
                        AppConfig cfg = Config.Get();
                        GeneralCfg g = cfg.General;          // the saved ffmpeg and ffprobe only, never paths sent in a request
                        VerticalCfg v = req.As<VerticalCfg>("vertical") ?? cfg.Vertical;
                        return Ok("result", Converter.RunEncoderTest(g, v, Adapters.Get(), 120000, Launcher, Sessions));
                    }
                case "logs":
                    return Ok("entries", Hub.Since((long)req.Num("after", 0), null, null, (int)req.Num("limit", 3000)));
                case "secret.protect":
                    {
                        string v = req.Str("value", "");
                        // the service's own key in the service; this computer's key in --console (converted when the service loads it)
                        return Ok("value", Mode == "service" ? Secrets.ProtectService(v) : Secrets.Protect(v));
                    }
                case "alerts.test":
                    {
                        AlertDestination d = req.As<AlertDestination>("destination");
                        if (d == null) throw new InvalidOperationException("no destination in the request");
                        AlertDestination saved = Config.Get().Alerts.Destinations.Find(delegate(AlertDestination x) { return x.Id == d.Id; }) ?? new AlertDestination();
                        d.SmtpPassword = Kept(d.SmtpPassword, saved.SmtpPassword, d.Id);
                        d.JiraToken = Kept(d.JiraToken, saved.JiraToken, d.Id);
                        d.Secret = Kept(d.Secret, saved.Secret, d.Id);
                        d.Url = Kept(d.Url, saved.Url, d.Id);
                        d.Headers = Kept(d.Headers, saved.Headers, d.Id);
                        d.ProxyUrl = Kept(d.ProxyUrl, saved.ProxyUrl, d.Id);
                        AlertSettings.NormalizeDestination(d);
                        return Ok("result", Alerts.Test(d, req.Str("mode", "send")));
                    }
                case "ffmpeg.check":
                    {
                        GeneralCfg g = Config.Get().General;          // the saved paths only
                        JObject o = Ok(null, null);
                        o.Set("ffmpeg", Gpu.FfmpegVersion(g.FfmpegPath));
                        o.Set("ffprobe", Gpu.FfmpegVersion(g.FfprobePath));
                        EncoderList enc = Gpu.ListEncoders(g.FfmpegPath);
                        o.Set("encoders", Json.ToJson(enc));
                        return o;
                    }
                case "gpio.run":
                    {
                        GpioTrigger t = req.As<GpioTrigger>("trigger");
                        GpioCfg savedGpio = Config.Get().Gpio;
                        GpioCfg g = req.As<GpioCfg>("gpio") ?? savedGpio;
                        if (t == null) throw new InvalidOperationException("no trigger in the request");
                        foreach (VmixTarget v in g.Vmix)
                        {
                            VmixTarget sv = savedGpio.Vmix.Find(delegate(VmixTarget x) { return x.Id == v.Id; });
                            v.Password = Kept(v.Password, sv != null ? sv.Password : "", v.Id);
                        }
                        GpioTrigger st = savedGpio.Triggers.Find(delegate(GpioTrigger x) { return x.Id == t.Id; });
                        for (int i = 0; i < t.OnActive.Count; i++)
                            t.OnActive[i].Password = Kept(t.OnActive[i].Password, st != null && i < st.OnActive.Count ? st.OnActive[i].Password : "", t.Id + "/on/" + i);
                        for (int i = 0; i < t.OnInactive.Count; i++)
                            t.OnInactive[i].Password = Kept(t.OnInactive[i].Password, st != null && i < st.OnInactive.Count ? st.OnInactive[i].Password : "", t.Id + "/off/" + i);
                        Cfg.NormalizeGpio(g);
                        GpioCfg one = new GpioCfg();
                        one.Triggers.Add(t);
                        Cfg.NormalizeGpio(one);
                        t = one.Triggers[0];
                        List<GpioAction> actions = req.Str("edge", "on") == "off" ? t.OnInactive : t.OnActive;
                        List<ActionResult> results = GpioService.RunActions(actions, g.Vmix, null);
                        foreach (ActionResult r in results) log.Info("Run now (" + t.Name + "): " + r.Text);
                        return Ok("results", results);
                    }
                case "gpio.simulate":
                    {
                        GpioEvent e = Gpio.Simulate(req.Str("device", ""), req.Str("signal", "gpi"), (int)req.Num("port", 1), (int)req.Num("pin", 1),
                                                    req.Bool("on", true), req.Str("command", ""));
                        return Ok("event", e);
                    }
                case "gpio.vmix":
                    {
                        VmixTarget t = req.As<VmixTarget>("vmix");
                        if (t == null) throw new InvalidOperationException("no vMix in the request");
                        VmixTarget sv = Config.Get().Gpio.Vmix.Find(delegate(VmixTarget x) { return x.Id == t.Id; });
                        t.Password = Kept(t.Password, sv != null ? sv.Password : "", t.Id);
                        GpioCfg one = new GpioCfg();
                        one.Vmix.Add(t);
                        Cfg.NormalizeGpio(one);
                        return Ok("info", Vmix.State(one.Vmix[0]));
                    }
                case "gpio.probe":
                    {
                        AxiaDevice d = req.As<AxiaDevice>("device");
                        if (d == null) throw new InvalidOperationException("no device in the request");
                        AxiaDevice sd = Config.Get().Gpio.Devices.Find(delegate(AxiaDevice x) { return x.Id == d.Id; });
                        d.Password = Kept(d.Password, sd != null ? sd.Password : "", d.Id);
                        GpioCfg one = new GpioCfg();
                        one.Devices.Add(d);
                        Cfg.NormalizeGpio(one);
                        return Ok("lines", GpioService.Probe(one.Devices[0], (int)req.Num("listen", 3000)));
                    }
                case "studios.switch":
                    {
                        ActionResult r = Studios.RequestSwitch(req.Str("studio", ""), "the manager", true);
                        if (r.Ok) log.Info("Studio switch asked for in the manager: " + r.Text);
                        JObject o = Ok(null, null);
                        o.Set("ok", r.Ok);
                        o.Set("text", r.Text);
                        return o;
                    }
                case "studios.test":
                    {
                        PathfinderCfg p = req.As<PathfinderCfg>("pathfinder");
                        if (p == null) throw new InvalidOperationException("no Pathfinder settings in the request");
                        PathfinderCfg saved = Config.Get().Studios.Pathfinder;
                        p.Password = Kept(p.Password, saved.Password, "pathfinder");
                        StudiosCfg one = new StudiosCfg();
                        one.Pathfinder = p;
                        StudioSettings.Normalize(one);
                        return Ok("trace", StudioManager.TestPathfinder(one.Pathfinder));
                    }
                case "health.test":
                    {
                        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                        string user = req.Str("user", "");
                        ProgramJob sp = Config.Get().Programs.Find(delegate(ProgramJob x) { return x.Id == req.Str("id", ""); });
                        string pass = user.Length > 0 ? Secrets.Unprotect(Kept(req.Str("password", ""), sp != null ? sp.HealthPassword : "", req.Str("id", ""))) : "";
                        string problem = Net.Probe(req.Str("url", ""), 10000, user, pass, req.Bool("allow_plain_login", false));
                        JObject o = Ok(null, null);
                        o.Set("problem", problem ?? "");
                        o.Set("ms", sw.Elapsed.TotalMilliseconds);
                        return o;
                    }
                case "path.check":
                    {
                        string p = Util.NormPath(req.Str("path", ""));
                        JObject o = Ok(null, null);
                        bool dir = false, file = false;
                        try { dir = Directory.Exists(p); file = File.Exists(p); } catch (Exception) { }
                        o.Set("dir", dir);
                        o.Set("file", file);
                        return o;
                    }
            }
            throw new InvalidOperationException("unknown command '" + cmd + "'");
        }
    }
}
