// The engine: loads the settings, runs the parts, notices changes to the settings file, writes the status file the
// manager shows, and carries out the manager's commands. The service and the console mode both run one engine.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Watchkeeper
{
    public class CommandResult
    {
        public string Id = "";
        public double Time;
        public bool Ok;
        public string Text = "";
        public List<string> Lines = new List<string>();
    }

    public class EngineStatus
    {
        public string Version = "";
        public int Pid;
        public double Started;
        public double Updated;
        public string Server = "";
        /// <summary>file | new | restored | imported | backup (config.json unreadable, a backup is in use) | defaults</summary>
        public string SettingsSource = "";
        public string SettingsError = "";
        public List<string> SettingsNotes = new List<string>();
        public List<string> Problems = new List<string>();
        public List<ProgramStatus> Watchdog = new List<ProgramStatus>();
        public List<FolderTaskStatus> Folders = new List<FolderTaskStatus>();
        public VerticalStatus Vertical = new VerticalStatus();
        public GpioStatus Gpio = new GpioStatus();
        public AlertStatus Alerts = new AlertStatus();
        public List<LogEntry> Log = new List<LogEntry>();
        public List<CommandResult> Commands = new List<CommandResult>();
    }

    public class Engine
    {
        public const string Version = "4.0.0";

        public static string DefaultDataDir()
        {
            if (!Util.IsWindows) return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Watchkeeper");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Watchkeeper");
        }

        public readonly string DataDir;
        public readonly LogHub Hub;
        public readonly SettingsFile File;
        readonly Logger log;
        volatile Settings current = new Settings();
        public readonly Watchdog Watchdog;
        public readonly FolderTasks Folders;
        public readonly Vertical Vertical;
        public readonly Gpio Gpio;
        public readonly Alerts Alerts;
        readonly IProcesses procs;
        Thread loop;
        readonly ManualResetEvent stopping = new ManualResetEvent(false);
        double started;
        string source = "", settingsError = "";
        double settingsFailedAt, settingsAlertedAt;
        List<string> notes = new List<string>();
        List<string> problems = new List<string>();
        readonly List<CommandResult> commands = new List<CommandResult>();
        public int TickMs = 2000;
        /// <summary>Tests: no worker threads for the checks, folder tasks and vertical conversion; the test calls Step.</summary>
        public bool Manual;

        public Engine(string dataDir, IProcesses procs, IServices services)
        {
            DataDir = dataDir;
            Directory.CreateDirectory(dataDir);
            Hub = new LogHub(Path.Combine(dataDir, "logs"));
            log = Hub.For("service");
            File = new SettingsFile(dataDir, Hub.For("settings"));
            this.procs = procs;
            Alerts = new Alerts(Get, Hub.For("alerts"));
            Watchdog = new Watchdog(Get, procs, services, Hub.For("watchdog"), Alerts);
            Folders = new FolderTasks(Get, Hub.For("folders"), Alerts, new StateStore(Path.Combine(dataDir, "state.json")));
            Vertical = new Vertical(Get, procs, Hub.For("vertical"), Alerts);
            Gpio = new Gpio(Get, Hub.For("gpio"), Alerts);
        }

        public Settings Get() { return current; }

        public string CommandDir { get { return Path.Combine(DataDir, "commands"); } }
        public string StatusPath { get { return Path.Combine(DataDir, "status.json"); } }

        // ---------------------------------------------------------------- settings
        /// <summary>Load (or reload) the settings. A file that cannot be read keeps what is running (or, at the first
        /// start, the newest backup) and raises an alert; the file itself is left for a person to fix.</summary>
        public void LoadSettings(bool first)
        {
            LoadResult r;
            try { r = File.Load(); }
            catch (Exception ex) { r = new LoadResult(); r.Error = "the settings cannot be loaded: " + ex.Message; }
            if (r.Settings == null)
            {
                bool same = r.Error == settingsError;
                settingsError = r.Error;
                settingsFailedAt = Util.Now();
                if (same && !first && Util.Now() - settingsAlertedAt < 1800) return;     // the same problem: reminded every 30 minutes at most
                settingsAlertedAt = Util.Now();
                log.Error(r.Error + (first ? "" : " The settings in use stay as they are."));
                if (first)
                {
                    Backup b = File.NewestReadable();
                    if (b != null)
                    {
                        current = SettingsFile.Parse(Util.ReadAllTextRetry(b.File), null);
                        source = "backup";
                        log.Warn("Using the newest backup (" + Path.GetFileName(b.File) + ") until config.json is fixed.");
                    }
                    else { current = new Settings(); source = "defaults"; log.Warn("Nothing is switched on until config.json is fixed."); }
                }
                Alerts.Raise("service", "service:settings", "error", "Settings file cannot be read", r.Error +
                             ". Watchkeeper keeps running with " + (source == "backup" ? "the newest backup" : source == "defaults" ? "nothing switched on" : "the settings it had") +
                             ". Fix the file, or restore a backup in the manager (Settings page).");
                return;
            }
            if (settingsError.Length > 0) Alerts.Recover("service:settings", "Settings file can be read again", "config.json can be read again.");
            settingsError = "";
            source = r.Source;
            notes = r.Notes;
            foreach (string n in r.Notes) log.Warn("Settings: " + n + ".");
            Settings s = r.Settings;
            if (SettingsFile.HasPlainSecrets(s))
            {
                try { File.Save(s, "encrypted-passwords"); log.Info("Passwords typed into config.json were encrypted."); }
                catch (Exception ex) { log.Warn("Passwords in config.json could not be encrypted: " + ex.Message); }
            }
            List<string> pr = new List<string>();
            foreach (Problem p in Validate.All(s))
            {
                pr.Add((p.Level == "error" ? "" : "warning: ") + p);
                if (p.Level == "error") log.Error("Settings: " + p + "."); else log.Warn("Settings: " + p + ".");
            }
            problems = pr;
            Hub.Debug = s.General.DetailedLog;
            current = s;
            if (!first) log.Info("Settings reloaded.");
        }

        // ---------------------------------------------------------------- run
        public void Start()
        {
            started = Util.Now();
            log.Info("Watchkeeper " + Version + " starting (data folder " + DataDir + ").");
            LoadSettings(true);
            Alerts.Start();
            if (!Manual) { Folders.Start(); Vertical.Start(); }
            Gpio.Start();
            if (!Manual)
            {
                loop = new Thread(Loop);
                loop.IsBackground = true;
                loop.Name = "engine";
                loop.Start();
            }
            log.Info("Started: " + Summary(current) + ".");
        }

        public static string Summary(Settings s)
        {
            int w = s.Watchdog.FindAll(delegate(ProgramEntry p) { return p.Enabled; }).Count;
            int f = s.Folders.FindAll(delegate(FolderTask t) { return t.Enabled; }).Count;
            int a = s.Alerts.Destinations.FindAll(delegate(AlertDestination d) { return d.Enabled; }).Count;
            return "watchdog " + s.Watchdog.Count + " (" + w + " on), folder tasks " + s.Folders.Count + " (" + f + " on), vertical conversion " +
                   (s.Vertical.Enabled ? "on" : "off") + ", GPIO " + (s.Gpio.Enabled ? "on" : "off") + " (" + s.Gpio.Triggers.Count + " triggers), alert destinations " +
                   s.Alerts.Destinations.Count + " (" + a + " on)";
        }

        public void Stop()
        {
            log.Info("Watchkeeper stopping.");
            stopping.Set();
            if (loop != null) loop.Join(10000);
            Vertical.Stop();
            Folders.Stop();
            Gpio.Stop();
            Alerts.Stop();
            WriteStatus(true);
            log.Info("Stopped.");
        }

        void Loop()
        {
            while (!stopping.WaitOne(0))
            {
                Step(DateTime.Now);
                stopping.WaitOne(TickMs);
            }
        }

        /// <summary>One round: settings changes, the watchdog, folder schedules, GPIO connections, commands, status.</summary>
        public void Step(DateTime now)
        {
            try
            {
                // read again when the file changes, and every minute while it could not be read (it may have been locked)
                if (File.ChangedOnDisk() || settingsError.Length > 0 && Util.Now() - settingsFailedAt >= 60) LoadSettings(false);
            }
            catch (Exception ex) { log.Error("Settings: " + ex.Message); }
            try { Watchdog.Tick(now); } catch (Exception ex) { log.Error("Watchdog: " + ex.Message); }
            try { Folders.Tick(now); } catch (Exception ex) { log.Error("Folder tasks: " + ex.Message); }
            try { Gpio.Tick(); } catch (Exception ex) { log.Error("GPIO: " + ex.Message); }
            try { RunCommands(); } catch (Exception ex) { log.Error("Commands: " + ex.Message); }
            WriteStatus(false);
        }

        // ---------------------------------------------------------------- status
        public EngineStatus Status()
        {
            EngineStatus s = new EngineStatus();
            s.Version = Version;
            s.Pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            s.Started = started;
            s.Updated = Util.Now();
            s.Server = Alerts.Server(current);
            s.SettingsSource = source;
            s.SettingsError = settingsError;
            s.SettingsNotes = new List<string>(notes);
            s.Problems = new List<string>(problems);
            s.Watchdog = Watchdog.Snapshot();
            s.Folders = Folders.Snapshot();
            s.Vertical = Vertical.Snapshot();
            s.Gpio = Gpio.Snapshot();
            s.Alerts = Alerts.Snapshot();
            s.Log = Hub.Since(0, 300);
            lock (commands) s.Commands = new List<CommandResult>(commands);
            return s;
        }

        void WriteStatus(bool stoppedNow)
        {
            try
            {
                EngineStatus s = Status();
                if (stoppedNow) s.Updated = 0;
                Util.WriteAtomic(StatusPath, Json.Serialize(Json.ToJson(s), false));
            }
            catch (Exception ex) { log.Debug("Status file: " + ex.Message); }
        }

        public static EngineStatus ReadStatus(string dataDir)
        {
            string p = Path.Combine(dataDir, "status.json");
            if (!System.IO.File.Exists(p)) return null;
            try
            {
                EngineStatus s = new EngineStatus();
                Json.FillFrom(s, Json.Parse(Util.ReadAllTextRetry(p)));
                return s;
            }
            catch (Exception) { return null; }
        }

        // ---------------------------------------------------------------- commands from the manager
        /// <summary>Manager side: drop a command for the service. Returns its id; the result appears in the status.</summary>
        public static string SendCommand(string dataDir, string action, string target)
        {
            string dir = Path.Combine(dataDir, "commands");
            Directory.CreateDirectory(dir);
            string id = Util.NewId() + Util.NewId();
            JObject o = new JObject();
            o.Set("id", id);
            o.Set("action", action);
            o.Set("target", target ?? "");
            Util.WriteAtomic(Path.Combine(dir, id + ".json"), Json.Serialize(o, false));
            return id;
        }

        void RunCommands()
        {
            if (!Directory.Exists(CommandDir)) return;
            foreach (string f in Directory.GetFiles(CommandDir, "*.json"))
            {
                JObject o = null;
                try { o = Json.Parse(System.IO.File.ReadAllText(f)) as JObject; } catch (Exception) { }
                try { System.IO.File.Delete(f); } catch (Exception) { }
                if (o == null) continue;
                CommandResult r = new CommandResult();
                r.Id = o.Str("id", "");
                r.Time = Util.Now();
                string action = o.Str("action", ""), target = o.Str("target", "");
                try { Execute(action, target, r); }
                catch (Exception ex) { r.Ok = false; r.Text = ex.Message; }
                log.Info("Command from the manager: " + action + (target.Length > 0 ? " " + target : "") + " - " + r.Text);
                lock (commands) { commands.Insert(0, r); if (commands.Count > 20) commands.RemoveAt(commands.Count - 1); }
            }
        }

        void Execute(string action, string target, CommandResult r)
        {
            switch (action)
            {
                case "restart_program":
                    r.Ok = Watchdog.RequestRestart(target);
                    r.Text = r.Ok ? "restart requested" : "no such watchdog entry (is it switched on?)";
                    break;
                case "run_folder_task":
                    r.Ok = Folders.RunNow(target);
                    r.Text = r.Ok ? "queued" : "no such folder task";
                    break;
                case "retry_vertical":
                    Vertical.RetryFailed();
                    r.Ok = true;
                    r.Text = "failed files are tried again";
                    break;
                case "reload":
                    LoadSettings(false);
                    r.Ok = settingsError.Length == 0;
                    r.Text = r.Ok ? "settings reloaded" : settingsError;
                    break;
                case "selftest":
                    r.Lines = SelfTest.InService(this, procs);
                    r.Ok = !r.Lines.Exists(delegate(string l) { return l.StartsWith("FAIL"); });
                    r.Text = r.Ok ? "all checks passed" : "some checks failed";
                    break;
                default:
                    r.Text = "unknown command " + action;
                    break;
            }
        }
    }
}
