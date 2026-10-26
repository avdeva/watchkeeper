// Configuration: one JSON file holds every setting (folder policies, programs, scripts,
// vertical conversion, alerts). Plain classes with defaults, normalisation (clamping),
// validation and an atomic file store that the service watches for changes.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Watchkeeper
{
    public class ScheduleCfg
    {
        public string Mode = "daily";          // manual | interval | daily | weekly
        public string Time = "09:30";
        public double IntervalMinutes = 60;
        public string Day = "sunday";          // weekly only
        /// <summary>Daily/weekly: run at service start if the last scheduled run was missed.</summary>
        public bool CatchUp = true;

        public ScheduleCfg() { }
        public ScheduleCfg(string mode, string time, double minutes)
        {
            Mode = mode; Time = time; IntervalMinutes = minutes;
        }
    }

    public class GeneralCfg
    {
        public string FfmpegPath = @"C:\Program Files\ffmpeg\bin\ffmpeg.exe";
        public string FfprobePath = @"C:\Program Files\ffmpeg\bin\ffprobe.exe";
        /// <summary>Account ffmpeg and ffprobe run as when the service starts them: local_service (LOCAL SERVICE without
        /// privileges; recordings must be readable by that account, e.g. through Users) | restricted_system (Local System
        /// with privileges removed and Administrators deny-only: reads anything Local System can).</summary>
        public string FfmpegAccount = "local_service";
        /// <summary>Log what every task would do without changing anything.</summary>
        public bool DryRun = false;
        /// <summary>Process priority of the service: normal | above_normal | high.</summary>
        public string ServicePriority = "high";
        /// <summary>Name shown in alerts; empty = computer name.</summary>
        public string ServerLabel = "";
        /// <summary>Which parts this server uses. A part that is off is hidden in the manager and nothing in it runs.</summary>
        public ModulesCfg Modules = new ModulesCfg();
        /// <summary>The template this configuration started from: studio | watchdog (information only).</summary>
        public string Profile = "studio";
    }

    public class ModulesCfg
    {
        public bool Folders = true;
        public bool Programs = true;     // the watchdog: programs and Windows services
        public bool Scripts = true;
        public bool Vertical = true;
        public bool Gpio = true;
        /// <summary>Studios: switch between studio configurations on Pathfinder's instruction, report status to Pathfinder.</summary>
        public bool Studios = false;
    }

    // ------------------------------------------------------------ folder policies
    /// <summary>What happens to one kind of file in a folder. The first matching rule decides.</summary>
    public class FileRule
    {
        public string Id = "";
        public string Name = "Rule";
        public bool Enabled = true;

        public string Action = "delete";          // delete | move | copy | keep
        public string Destination = "";           // move / copy; empty = the policy's destination
        public bool KeepSubfolders = true;        // move / copy with sub-folders: recreate the structure
        public string OnConflict = "overwrite";   // overwrite | skip | rename

        public string Pattern = "*";              // wildcards, ';' separated
        public string Exclude = "";               // wildcards, ';' separated
        public double OlderThan = 0;              // 0 = any age
        public string OlderThanUnit = "days";     // minutes | hours | days | weeks
        public double MinSizeMb = 0;              // 0 = no limit
        public double MaxSizeMb = 0;              // 0 = no limit
        public bool UseWindow = false;            // time-of-day window
        public string WindowStart = "00:00";
        public string WindowEnd = "23:59";
        public string WindowMode = "inside";      // inside | outside
        public string WindowSource = "modified";  // modified | filename
        public int KeepNewest = 0;                // newest N matching files are left alone by this rule
        /// <summary>After moving/copying, set Date Modified to recording start (from the name) + duration (ffprobe).</summary>
        public bool StampFromName = false;
    }

    public class FolderPolicy
    {
        public string Id = "";
        public string Name = "Folder";
        public bool Enabled = false;
        /// <summary>Source folder (usually on the cache drive the recorders write to).</summary>
        public string Path = "";
        /// <summary>Default destination for move / copy rules (usually on the data drive).</summary>
        public string Destination = "";
        public ScheduleCfg Schedule = new ScheduleCfg("daily", "03:00", 60);
        public bool Subfolders = false;
        /// <summary>Files modified within this many seconds are never touched (still being written).</summary>
        public int MinAgeSeconds = 120;
        public bool RemoveEmptyFolders = false;
        public List<FileRule> Rules = new List<FileRule>();
        public string Notes = "";
    }

    // ------------------------------------------------------------------ programs
    /// <summary>A program or Windows service that must always be running (VVD, vMix, ...), with an optional health check.</summary>
    public class ProgramJob
    {
        public string Id = "";
        public string Name = "Program";
        public bool Enabled = false;
        public string Kind = "program";           // program | service (a Windows service, by its service name)
        public string ServiceName = "";
        public string ExePath = "";
        public string Arguments = "";
        public string WorkingDir = "";
        /// <summary>Process name to look for (without .exe); empty = the executable's name.</summary>
        public string ProcessName = "";
        public string RunAs = "user";             // user (logged-on desktop session) | service
        /// <summary>User session: a program that asks for administrator rights starts with the signed-in user's full
        /// rights without the Windows consent prompt. Off by default.</summary>
        public bool AllowElevation = false;
        public int StartDelaySeconds = 20;        // after the service starts or the user session appears
        public int RestartDelaySeconds = 10;      // after the program exits
        public int MaxRestartsPerHour = 10;       // 0 = unlimited
        public ScheduleCfg Restart = new ScheduleCfg("manual", "04:00", 1440);   // manual = no scheduled restart
        public string Priority = "normal";        // normal | above_normal | high
        /// <summary>Health check: an address that must answer while it runs (http(s)://... or tcp://host:port).</summary>
        public bool HealthEnabled = false;
        public string HealthUrl = "";
        /// <summary>Health check login (HTTP basic), if the address needs one. The password is stored encrypted.</summary>
        public string HealthUser = "";
        public string HealthPassword = "";
        /// <summary>Send the health check login over plain http to another computer.</summary>
        public bool HealthAllowPlainLogin = false;
        public int HealthIntervalSeconds = 15;
        public int HealthFailSeconds = 60;        // no good answer for this long = not healthy
        public int HealthGraceSeconds = 90;       // after it (re)starts, before the first check counts
        public string HealthAction = "alert";     // alert | restart
        /// <summary>Studios part: the studios (ids) this entry belongs to. Empty = it runs whatever studio is active.
        /// Otherwise it runs only while one of these studios is active, and is closed when the server switches away.</summary>
        public List<string> Studios = new List<string>();
        public string Notes = "";
    }

    // ------------------------------------------------------------------- scripts
    public class ScriptJob
    {
        public string Id = "";
        public string Name = "Script";
        public bool Enabled = false;
        public string Command = "";               // .ps1 .bat .cmd .vbs .exe (or any program)
        public string Source = "";                // {source} in the arguments; watched by the file trigger
        public string Destination = "";           // {destination} in the arguments
        public string Arguments = "";             // {source} {destination}; {file} {name} {stem} {folder} for the file trigger
        public string WorkingDir = "";
        public string Trigger = "schedule";       // schedule | startup | file
        public ScheduleCfg Schedule = new ScheduleCfg("daily", "02:00", 60);
        public string WatchPath = "";
        public string WatchPattern = "*";
        public bool WatchSubfolders = false;
        public int StableSeconds = 30;            // a new file must be unchanged this long
        public bool ProcessExisting = false;      // first start: also run for files already there
        public string RunAs = "service";          // service | user
        public int TimeoutMinutes = 60;           // 0 = no limit
        public string OkExitCodes = "0";
        public string Priority = "normal";        // below_normal | normal | above_normal | high
        /// <summary>New-file trigger: at most this many runs a minute (0 = no limit); further files wait.</summary>
        public int MaxRunsPerMinute = 30;
        public string Notes = "";
    }

    // ---------------------------------------------------------- vertical conversion
    public class SourceRule
    {
        public string Id = "";
        public string Name = "Source";
        public string Match = "";
        public string Prefix = "";
        public bool Enabled = true;

        public SourceRule() { }
        public SourceRule(string id, string name, string match, string prefix)
        {
            Id = id; Name = name; Match = match; Prefix = prefix;
        }
    }

    public class OtherCfg
    {
        public bool Enabled = true;
        public string Prefix = "Vertical";
    }

    public class VideoCfg
    {
        public string Codec = "h264";               // h264 | hevc
        public string Profile = "high";             // baseline | main | high (h264 only)
        public string Rotation = "cclock";          // cclock | clock
        public string ResolutionMode = "source";    // source | custom
        public int Width = 0;
        public int Height = 0;
        public double Fps = 0;                      // 0 = keep source
        public string RateMode = "match_source";    // match_source | percent | fixed | quality
        public double FixedMbps = 8.0;
        public double Percent = 100;
        public int Quality = 23;
        public double FallbackMbps = 8.0;
        public string Preset = "balanced";          // fast | balanced | quality
        public string AudioMode = "copy";           // copy | aac | none
        public int AudioKbps = 192;
        public string ExtraInputArgs = "";
        public string ExtraOutputArgs = "";
    }

    public class GpuCfg
    {
        public string Backend = "intel_qsv";        // intel_qsv | nvidia_nvenc | amd_amf | cpu | auto
        public string AdapterName = "";
        public int AdapterIndex = -1;
        public bool HwDecode = true;
        public bool FallbackToCpu = false;
        public int CpuThreads = 0;
        public string Priority = "below_normal";    // normal | below_normal | idle
        /// <summary>Where ffmpeg runs: service | user_fallback (service, then the logged-on session) | user.</summary>
        public string Session = "user_fallback";
    }

    public class VerticalCfg
    {
        public bool Enabled = false;
        public string WatchDir = @"T:\Vertical";
        public string OutDir = @"D:\Vertical";
        public List<string> Extensions = new List<string> { ".mp4" };
        public int PollSeconds = 5;
        public string AfterSuccess = "delete";      // delete | keep | move
        /// <summary>Where the original goes after a verified conversion when AfterSuccess is "move".</summary>
        public string MoveOriginalTo = @"D:\Vertical\Originals";
        public int MaxRetries = 3;
        public List<SourceRule> Sources = new List<SourceRule>
        {
            new SourceRule("pgm", "PGM", "MultiCorder7", "Vertical PGM"),
            new SourceRule("split", "Split", "MultiCorder6", "Vertical Split")
        };
        public OtherCfg Other = new OtherCfg();
        public VideoCfg Video = new VideoCfg();
        public GpuCfg Gpu = new GpuCfg();
    }

    public class AppConfig
    {
        /// <summary>4: secrets stored with the service's key, webhook addresses and headers encrypted, security options (3.1).</summary>
        public int Version = 4;
        public GeneralCfg General = new GeneralCfg();
        public List<FolderPolicy> Folders = new List<FolderPolicy>();
        public List<ProgramJob> Programs = new List<ProgramJob>();
        public List<ScriptJob> Scripts = new List<ScriptJob>();
        public VerticalCfg Vertical = new VerticalCfg();
        public GpioCfg Gpio = new GpioCfg();
        public StudiosCfg Studios = new StudiosCfg();
        public AlertCfg Alerts = new AlertCfg();
    }

    public static class Cfg
    {
        public static readonly string[] Sections = { "general", "folders", "programs", "scripts", "vertical", "gpio", "studios", "alerts" };
        public static readonly string[] Backends = { "intel_qsv", "nvidia_nvenc", "amd_amf", "cpu" };
        public static readonly string[] Days = { "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday" };

        // ------------------------------------------------------------ helpers
        static string S(string v, string d) { return v == null ? d : v.Trim(); }

        static string C(string v, string[] choices, string d)
        {
            string x = (v ?? d).Trim().ToLowerInvariant();
            return Array.IndexOf(choices, x) >= 0 ? x : d;
        }

        static int I(int v, int lo, int hi) { return Math.Max(lo, Math.Min(hi, v)); }

        static double F(double v, double lo, double hi)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return lo;
            return Math.Max(lo, Math.Min(hi, v));
        }

        static string T(string v, string d)
        {
            try { return Util.FmtHhmm(Util.ParseTime(v)); }
            catch (FormatException) { return d; }
        }

        static List<string> Exts(List<string> v)
        {
            List<string> o = new List<string>();
            if (v == null) return o;
            foreach (string raw in v)
            {
                string x = (raw ?? "").Trim().ToLowerInvariant();
                if (x.Length == 0) continue;
                if (!x.StartsWith(".")) x = "." + x;
                if (!o.Contains(x)) o.Add(x);
            }
            return o;
        }

        static void Schedule(ScheduleCfg s, ScheduleCfg d)
        {
            s.Mode = C(s.Mode, new[] { "manual", "interval", "daily", "weekly" }, d.Mode);
            s.Time = T(s.Time, d.Time);
            s.IntervalMinutes = F(s.IntervalMinutes, 0.25, 10080);
            s.Day = C(s.Day, Days, "sunday");
        }

        public static string NewId() { return Guid.NewGuid().ToString("N").Substring(0, 8); }

        public static string UniqueId(string id, List<string> seen) { return Id(id, seen); }

        static string Id(string id, List<string> seen)
        {
            string x = S(id, "");
            if (x.Length == 0 || seen.Contains(x)) x = NewId();
            seen.Add(x);
            return x;
        }

        static string Name(string v, string d)
        {
            string x = S(v, d);
            return x.Length == 0 ? d : x;
        }

        public static readonly string[] Priorities = { "below_normal", "normal", "above_normal", "high" };

        // ------------------------------------------------------- normalisation
        /// <summary>Clamp every value into its valid range and fill gaps. Never throws.</summary>
        public static void Normalize(AppConfig c)
        {
            c.Version = 4;
            c.General = c.General ?? new GeneralCfg();
            c.Folders = c.Folders ?? new List<FolderPolicy>();
            c.Programs = c.Programs ?? new List<ProgramJob>();
            c.Scripts = c.Scripts ?? new List<ScriptJob>();
            c.Vertical = c.Vertical ?? new VerticalCfg();
            c.Gpio = c.Gpio ?? new GpioCfg();
            c.Studios = c.Studios ?? new StudiosCfg();
            c.Alerts = c.Alerts ?? new AlertCfg();

            GeneralCfg g = c.General, dg = new GeneralCfg();
            g.FfmpegPath = S(g.FfmpegPath, dg.FfmpegPath);
            g.FfprobePath = S(g.FfprobePath, dg.FfprobePath);
            g.FfmpegAccount = C(g.FfmpegAccount, new[] { "local_service", "restricted_system" }, dg.FfmpegAccount);
            g.ServicePriority = C(g.ServicePriority, new[] { "normal", "above_normal", "high" }, dg.ServicePriority);
            g.ServerLabel = S(g.ServerLabel, "");
            g.Modules = g.Modules ?? new ModulesCfg();
            g.Profile = C(g.Profile, new[] { "studio", "watchdog" }, "studio");

            List<string> seen = new List<string>();
            for (int i = 0; i < c.Folders.Count; i++)
            {
                FolderPolicy f = c.Folders[i] ?? new FolderPolicy();
                c.Folders[i] = f;
                NormalizeFolder(f, seen);
            }

            seen = new List<string>();
            for (int i = 0; i < c.Programs.Count; i++)
            {
                ProgramJob p = c.Programs[i] ?? new ProgramJob();
                c.Programs[i] = p;
                NormalizeProgram(p, seen);
            }

            seen = new List<string>();
            for (int i = 0; i < c.Scripts.Count; i++)
            {
                ScriptJob s = c.Scripts[i] ?? new ScriptJob();
                c.Scripts[i] = s;
                NormalizeScript(s, seen);
            }

            NormalizeVertical(c.Vertical);
            NormalizeGpio(c.Gpio);
            StudioSettings.Normalize(c.Studios);

            NormalizeAlerts(c.Alerts);
        }

        public static void NormalizeGpio(GpioCfg g)
        {
            g.Devices = g.Devices ?? new List<AxiaDevice>();
            g.Vmix = g.Vmix ?? new List<VmixTarget>();
            g.Triggers = g.Triggers ?? new List<GpioTrigger>();
            g.DisconnectAlertSeconds = I(g.DisconnectAlertSeconds, 10, 86400);
            List<string> seen = new List<string>();
            for (int i = 0; i < g.Devices.Count; i++)
            {
                AxiaDevice d = g.Devices[i] ?? new AxiaDevice();
                g.Devices[i] = d;
                d.Id = Id(d.Id, seen);
                d.Name = Name(d.Name, "Axia GPIO");
                d.Host = S(d.Host, "");
                if (d.Port == 0) d.Port = 93;
                d.Port = I(d.Port, 1, 65535);
                d.Password = d.Password ?? "";
                d.Notes = S(d.Notes, "");
            }
            seen = new List<string>();
            for (int i = 0; i < g.Vmix.Count; i++)
            {
                VmixTarget v = g.Vmix[i] ?? new VmixTarget();
                g.Vmix[i] = v;
                v.Id = Id(v.Id, seen);
                v.Name = Name(v.Name, "vMix");
                v.Host = S(v.Host, "");
                if (v.Port == 0) v.Port = 8088;
                v.Port = I(v.Port, 1, 65535);
                v.User = S(v.User, "");
                v.Password = v.Password ?? "";
                v.TimeoutMs = I(v.TimeoutMs, 200, 60000);
            }
            seen = new List<string>();
            for (int i = 0; i < g.Triggers.Count; i++)
            {
                GpioTrigger t = g.Triggers[i] ?? new GpioTrigger();
                g.Triggers[i] = t;
                t.Id = Id(t.Id, seen);
                t.Name = Name(t.Name, "Trigger");
                t.DeviceId = S(t.DeviceId, "");
                t.Signal = C(t.Signal, new[] { "gpi", "gpo", "cmd" }, "gpi");
                t.Port = I(t.Port, t.Signal == "cmd" ? 0 : 1, 256);
                t.Pin = I(t.Pin, 1, 5);
                t.Command = S(t.Command, "");
                t.IgnoreRepeatMs = I(t.IgnoreRepeatMs, 0, 600000);
                t.OnActive = NormalizeActions(t.OnActive);
                t.OnInactive = NormalizeActions(t.OnInactive);
                t.Notes = S(t.Notes, "");
            }
        }

        static List<GpioAction> NormalizeActions(List<GpioAction> list)
        {
            List<GpioAction> o = new List<GpioAction>();
            if (list == null) return o;
            foreach (GpioAction raw in list)
            {
                GpioAction a = raw ?? new GpioAction();
                a.Type = C(a.Type, new[] { "vmix", "http", "studio" }, "vmix");
                a.Studio = S(a.Studio, "");
                a.VmixId = S(a.VmixId, "");
                a.Function = S(a.Function, "");
                a.Input = S(a.Input, "");
                a.Value = a.Value ?? "";
                a.Duration = S(a.Duration, "");
                a.SelectedName = S(a.SelectedName, "");
                a.Mix = S(a.Mix, "");
                a.Extra = S(a.Extra, "");
                string m = S(a.Method, "GET").ToUpperInvariant();
                a.Method = Array.IndexOf(new[] { "GET", "POST", "PUT", "DELETE" }, m) >= 0 ? m : "GET";
                a.Url = S(a.Url, "");
                a.Body = a.Body ?? "";
                a.ContentType = S(a.ContentType, "application/json");
                if (a.ContentType.Length == 0) a.ContentType = "application/json";
                a.DelayMs = I(a.DelayMs, 0, 60000);
                o.Add(a);
            }
            return o;
        }

        public static List<string> ValidateGpio(GpioCfg g)
        {
            List<string> errors = new List<string>();
            for (int i = 0; i < g.Devices.Count; i++)
                if (g.Devices[i].Enabled && g.Devices[i].Host.Length == 0) errors.Add("GPIO device " + (i + 1) + " (" + g.Devices[i].Name + "): address is required");
            for (int i = 0; i < g.Vmix.Count; i++)
            {
                VmixTarget v = g.Vmix[i];
                if (v.Host.Length == 0) errors.Add("vMix " + (i + 1) + " (" + v.Name + "): address is required");
                else if (v.User.Length > 0 && !v.AllowPlainLogin && !Net.IsLoopback(v.Host))
                    errors.Add("vMix " + (i + 1) + " (" + v.Name + "): the vMix Web Controller only offers http, so its password would cross the network readable. " +
                               "Switch on 'Send the login unencrypted' to accept that, or use vMix on this server (127.0.0.1)");
            }
            for (int i = 0; i < g.Triggers.Count; i++)
            {
                GpioTrigger t = g.Triggers[i];
                string l = "GPIO trigger " + (i + 1) + " (" + t.Name + ")";
                if (!g.Devices.Exists(delegate(AxiaDevice d) { return d.Id == t.DeviceId; })) errors.Add(l + ": choose the Axia device it listens to");
                if (t.Signal == "cmd" && t.Command.Length == 0) errors.Add(l + ": the command text is required");
                List<GpioAction> all = new List<GpioAction>(t.OnActive);
                all.AddRange(t.OnInactive);
                foreach (GpioAction a in all)
                {
                    if (a.Type == "studio")
                    {
                        if (a.Studio.Length == 0) errors.Add(l + ": a 'switch studio' action has no studio");
                    }
                    else if (a.Type == "vmix")
                    {
                        if (a.Function.Length == 0) errors.Add(l + ": a vMix action has no function");
                        if (g.Vmix.Count == 0) errors.Add(l + ": add a vMix on the vMix tab for its vMix actions");
                        else if (a.VmixId.Length > 0 && !g.Vmix.Exists(delegate(VmixTarget v) { return v.Id == a.VmixId; }))
                            errors.Add(l + ": a vMix action uses a vMix that no longer exists");
                    }
                    else
                    {
                        Uri u;
                        if (!Uri.TryCreate(a.Url, UriKind.Absolute, out u) || (u.Scheme != "http" && u.Scheme != "https"))
                            errors.Add(l + ": an HTTP action needs an address starting with http:// or https://");
                        else if (u.UserInfo.Length > 0)
                            errors.Add(l + ": do not put a user name or password in the address (it would be stored and logged in plain text); use the User name and Password fields");
                        else if (Net.PlainLoginProblem(u, a.User, null, a.AllowPlainLogin) != null)
                            errors.Add(l + ": " + Net.PlainLoginProblem(u, a.User, null, false));
                    }
                }
            }
            List<string> unique = new List<string>();
            foreach (string e in errors) if (!unique.Contains(e)) unique.Add(e);
            return unique;
        }

        static bool ModuleOn(AppConfig c, string section)
        {
            ModulesCfg m = c.General.Modules;
            switch (section)
            {
                case "folders": return m.Folders;
                case "programs": return m.Programs;
                case "scripts": return m.Scripts;
                case "vertical": return m.Vertical;
                case "gpio": return m.Gpio;
                case "studios": return m.Studios;
            }
            return true;
        }

        public static void NormalizeAlerts(AlertCfg a)
        {
            AlertSettings.Normalize(a);
        }

        public static void NormalizeFolder(FolderPolicy f, List<string> seen)
        {
            f.Id = Id(f.Id, seen);
            f.Name = Name(f.Name, "Folder");
            f.Path = S(f.Path, "");
            f.Destination = S(f.Destination, "");
            f.Schedule = f.Schedule ?? new ScheduleCfg("daily", "03:00", 60);
            Schedule(f.Schedule, new ScheduleCfg("daily", "03:00", 60));
            f.MinAgeSeconds = I(f.MinAgeSeconds, 0, 86400);
            f.Notes = S(f.Notes, "");
            f.Rules = f.Rules ?? new List<FileRule>();
            List<string> rseen = new List<string>();
            for (int i = 0; i < f.Rules.Count; i++)
            {
                FileRule r = f.Rules[i] ?? new FileRule();
                f.Rules[i] = r;
                r.Id = Id(r.Id, rseen);
                r.Name = Name(r.Name, "Rule");
                r.Action = C(r.Action, new[] { "delete", "move", "copy", "keep" }, "delete");
                r.Destination = S(r.Destination, "");
                r.OnConflict = C(r.OnConflict, new[] { "overwrite", "skip", "rename" }, "overwrite");
                r.Pattern = S(r.Pattern, "*");
                if (r.Pattern.Length == 0) r.Pattern = "*";
                r.Exclude = S(r.Exclude, "");
                r.OlderThan = F(r.OlderThan, 0, 1000000);
                r.OlderThanUnit = C(r.OlderThanUnit, new[] { "minutes", "hours", "days", "weeks" }, "days");
                r.MinSizeMb = F(r.MinSizeMb, 0, 10000000);
                r.MaxSizeMb = F(r.MaxSizeMb, 0, 10000000);
                r.WindowStart = T(r.WindowStart, "00:00");
                r.WindowEnd = T(r.WindowEnd, "23:59");
                r.WindowMode = C(r.WindowMode, new[] { "inside", "outside" }, "inside");
                r.WindowSource = C(r.WindowSource, new[] { "modified", "filename" }, "modified");
                r.KeepNewest = I(r.KeepNewest, 0, 1000000);
            }
        }

        public static void NormalizeProgram(ProgramJob p, List<string> seen)
        {
            p.Id = Id(p.Id, seen);
            p.Name = Name(p.Name, "Program");
            p.ExePath = S(p.ExePath, "");
            p.Arguments = S(p.Arguments, "");
            p.WorkingDir = S(p.WorkingDir, "");
            p.ProcessName = S(p.ProcessName, "");
            p.RunAs = C(p.RunAs, new[] { "user", "service" }, "user");
            p.StartDelaySeconds = I(p.StartDelaySeconds, 0, 3600);
            p.RestartDelaySeconds = I(p.RestartDelaySeconds, 1, 3600);
            p.MaxRestartsPerHour = I(p.MaxRestartsPerHour, 0, 1000);
            p.Restart = p.Restart ?? new ScheduleCfg("manual", "04:00", 1440);
            Schedule(p.Restart, new ScheduleCfg("manual", "04:00", 1440));
            p.Priority = C(p.Priority, new[] { "normal", "above_normal", "high" }, "normal");
            p.Kind = C(p.Kind, new[] { "program", "service" }, "program");
            p.ServiceName = S(p.ServiceName, "");
            p.HealthUrl = S(p.HealthUrl, "");
            p.HealthIntervalSeconds = I(p.HealthIntervalSeconds, 5, 3600);
            p.HealthFailSeconds = I(p.HealthFailSeconds, 5, 86400);
            p.HealthGraceSeconds = I(p.HealthGraceSeconds, 0, 3600);
            p.HealthAction = C(p.HealthAction, new[] { "alert", "restart" }, "alert");
            List<string> studios = new List<string>();
            if (p.Studios != null) foreach (string sid in p.Studios) if (!string.IsNullOrEmpty(sid) && !studios.Contains(sid.Trim())) studios.Add(sid.Trim());
            p.Studios = studios;
            p.Notes = S(p.Notes, "");
        }

        public static void NormalizeScript(ScriptJob s, List<string> seen)
        {
            s.Id = Id(s.Id, seen);
            s.Name = Name(s.Name, "Script");
            s.Command = S(s.Command, "");
            s.Arguments = S(s.Arguments, "");
            s.Source = S(s.Source, "");
            s.Destination = S(s.Destination, "");
            s.WorkingDir = S(s.WorkingDir, "");
            s.Trigger = C(s.Trigger, new[] { "schedule", "startup", "file" }, "schedule");
            s.Schedule = s.Schedule ?? new ScheduleCfg("daily", "02:00", 60);
            Schedule(s.Schedule, new ScheduleCfg("daily", "02:00", 60));
            s.WatchPath = S(s.WatchPath, "");
            s.WatchPattern = S(s.WatchPattern, "*");
            if (s.WatchPattern.Length == 0) s.WatchPattern = "*";
            s.StableSeconds = I(s.StableSeconds, 0, 86400);
            s.RunAs = C(s.RunAs, new[] { "service", "user" }, "service");
            s.TimeoutMinutes = I(s.TimeoutMinutes, 0, 10080);
            s.MaxRunsPerMinute = I(s.MaxRunsPerMinute, 0, 6000);
            s.OkExitCodes = S(s.OkExitCodes, "0");
            if (s.OkExitCodes.Length == 0) s.OkExitCodes = "0";
            s.Priority = C(s.Priority, Priorities, "normal");
            s.Notes = S(s.Notes, "");
        }

        static void NormalizeVertical(VerticalCfg v)
        {
            VerticalCfg dv = new VerticalCfg();
            v.WatchDir = S(v.WatchDir, dv.WatchDir);
            v.OutDir = S(v.OutDir, dv.OutDir);
            v.Extensions = Exts(v.Extensions);
            v.PollSeconds = I(v.PollSeconds, 1, 3600);
            v.AfterSuccess = C(v.AfterSuccess, new[] { "delete", "keep", "move" }, dv.AfterSuccess);
            v.MoveOriginalTo = S(v.MoveOriginalTo, "");
            v.MaxRetries = I(v.MaxRetries, 0, 20);
            v.Sources = v.Sources ?? new List<SourceRule>();
            List<string> seen = new List<string>();
            for (int i = 0; i < v.Sources.Count; i++)
            {
                SourceRule src = v.Sources[i] ?? new SourceRule();
                v.Sources[i] = src;
                src.Name = Name(src.Name, "Source");
                src.Match = S(src.Match, "");
                src.Prefix = S(src.Prefix, "");
                src.Id = Id(src.Id, seen);
            }
            v.Other = v.Other ?? new OtherCfg();
            v.Other.Prefix = S(v.Other.Prefix, dv.Other.Prefix);

            v.Video = v.Video ?? new VideoCfg();
            VideoCfg vid = v.Video, dvid = new VideoCfg();
            vid.Codec = C(vid.Codec, new[] { "h264", "hevc" }, dvid.Codec);
            vid.Profile = C(vid.Profile, new[] { "baseline", "main", "high" }, dvid.Profile);
            vid.Rotation = C(vid.Rotation, new[] { "cclock", "clock" }, dvid.Rotation);
            vid.ResolutionMode = C(vid.ResolutionMode, new[] { "source", "custom" }, dvid.ResolutionMode);
            vid.Width = I(vid.Width, 0, 8192);
            vid.Height = I(vid.Height, 0, 8192);
            vid.Fps = F(vid.Fps, 0, 240);
            vid.RateMode = C(vid.RateMode, new[] { "match_source", "percent", "fixed", "quality" }, dvid.RateMode);
            vid.FixedMbps = F(vid.FixedMbps, 0.1, 500);
            vid.Percent = F(vid.Percent, 5, 400);
            vid.Quality = I(vid.Quality, 1, 51);
            vid.FallbackMbps = F(vid.FallbackMbps, 0.1, 500);
            vid.Preset = C(vid.Preset, new[] { "fast", "balanced", "quality" }, dvid.Preset);
            vid.AudioMode = C(vid.AudioMode, new[] { "copy", "aac", "none" }, dvid.AudioMode);
            vid.AudioKbps = I(vid.AudioKbps, 32, 512);
            vid.ExtraInputArgs = S(vid.ExtraInputArgs, "");
            vid.ExtraOutputArgs = S(vid.ExtraOutputArgs, "");

            v.Gpu = v.Gpu ?? new GpuCfg();
            GpuCfg gpu = v.Gpu, dgpu = new GpuCfg();
            gpu.Backend = C(gpu.Backend, new[] { "intel_qsv", "nvidia_nvenc", "amd_amf", "cpu", "auto" }, dgpu.Backend);
            gpu.AdapterName = S(gpu.AdapterName, "");
            gpu.AdapterIndex = I(gpu.AdapterIndex, -1, 32);
            gpu.CpuThreads = I(gpu.CpuThreads, 0, 64);
            gpu.Priority = C(gpu.Priority, new[] { "normal", "below_normal", "idle" }, dgpu.Priority);
            gpu.Session = C(gpu.Session, new[] { "service", "user_fallback", "user" }, dgpu.Session);
        }

        // ------------------------------------------------------------ validation
        static bool CheckDir(List<string> errors, string label, string value)
        {
            if (string.IsNullOrEmpty((value ?? "").Trim()))
            {
                errors.Add(label + ": folder is required");
                return false;
            }
            string reason = Util.ProtectedReason(value);
            if (reason != null)
            {
                errors.Add(label + ": refusing '" + Util.NormPath(value) + "' (" + reason + ")");
                return false;
            }
            return true;
        }

        static bool IsInside(string child, string parent)
        {
            string c = Util.PathKey(child), p = Util.PathKey(parent);
            if (c == p) return true;
            string sep = Util.IsWindowsStyle(parent) ? "\\" : "/";
            return c.StartsWith(p.TrimEnd('\\', '/') + sep);
        }

        /// <summary>Where a move/copy rule sends files: its own destination, or else the policy's.</summary>
        public static string RuleDestination(FolderPolicy f, FileRule r)
        {
            string d = (r.Destination ?? "").Trim();
            return d.Length > 0 ? d : (f.Destination ?? "").Trim();
        }

        public static List<string> ValidateFolder(FolderPolicy f, string label)
        {
            List<string> errors = new List<string>();
            bool pathOk = CheckDir(errors, label + ": source", f.Path);
            if (f.Destination.Length > 0) CheckDir(errors, label + ": destination", f.Destination);
            int i = 0;
            foreach (FileRule r in f.Rules)
            {
                i++;
                string rl = label + ", rule " + i + " (" + r.Name + ")";
                if (r.Action == "move" || r.Action == "copy")
                {
                    string dest = RuleDestination(f, r);
                    if (CheckDir(errors, rl + ": destination", dest) && pathOk)
                    {
                        if (Util.PathKey(dest) == Util.PathKey(f.Path))
                            errors.Add(rl + ": destination must be a different folder from the source");
                    }
                }
                if (r.MaxSizeMb > 0 && r.MinSizeMb > r.MaxSizeMb) errors.Add(rl + ": minimum size is larger than maximum size");
            }
            return errors;
        }

        public static List<string> ValidateProgram(ProgramJob p, string label)
        {
            List<string> errors = new List<string>();
            if (p.Kind == "service")
            {
                if (p.ServiceName.Length == 0) errors.Add(label + ": Windows service name is required");
            }
            else if (p.ExePath.Length == 0) errors.Add(label + ": program file is required");
            else if ((p.RunAs == "service" || p.AllowElevation) && !Path.IsPathRooted(Util.NormPath(p.ExePath)))
                errors.Add(label + ": a program that runs as Local System or with full rights needs its full path (e.g. C:\\Program Files\\...), " +
                           "so Windows cannot pick another file of that name from the search path");
            if (p.HealthEnabled)
            {
                string h = p.HealthUrl.ToLowerInvariant();
                if (!(h.StartsWith("http://") || h.StartsWith("https://") || h.StartsWith("tcp://")))
                    errors.Add(label + ": health check address must start with http://, https:// or tcp://");
                else if (h.StartsWith("tcp://") && h.Substring(6).TrimEnd('/').LastIndexOf(':') <= 0)
                    errors.Add(label + ": health check address must look like tcp://host:port");
                Uri hu;
                if (Uri.TryCreate(p.HealthUrl, UriKind.Absolute, out hu) && hu.UserInfo.Length > 0)
                    errors.Add(label + ": do not put a user name or password in the health check address (it would be stored and logged in plain text); use the User name and Password fields");
                else if (hu != null && Net.PlainLoginProblem(hu, p.HealthUser, null, p.HealthAllowPlainLogin) != null)
                    errors.Add(label + ": health check: " + Net.PlainLoginProblem(hu, p.HealthUser, null, false));
            }
            return errors;
        }

        public static List<string> ValidateScript(ScriptJob s, string label)
        {
            List<string> errors = new List<string>();
            if (s.Command.Length == 0) errors.Add(label + ": script or program is required");
            else if (s.RunAs != "user" && !Path.IsPathRooted(Util.NormPath(s.Command)))
                errors.Add(label + ": a script that runs as Local System needs the full path of the script or program (e.g. C:\\Program Files\\Scripts\\move.ps1), " +
                           "so Windows cannot pick another file of that name from the search path or the working folder");
            if (s.Trigger == "file") CheckDir(errors, label + ": watched folder", s.WatchPath.Length > 0 ? s.WatchPath : s.Source);
            string ext = Path.GetExtension(Util.NormPath(s.Command)).ToLowerInvariant();
            if (ext == ".bat" || ext == ".cmd")
            {
                foreach (string ph in ScriptRunner.FilePlaceholders)
                    if (s.Arguments.Contains(ph))
                        errors.Add(label + ": " + ph + " cannot be used with a .bat or .cmd script (cmd.exe would read characters in file names as commands); " +
                                   "use the environment variable WK_FILE inside the script instead");
            }
            foreach (string code in s.OkExitCodes.Split(',', ';', ' '))
            {
                int n;
                if (code.Trim().Length > 0 && !int.TryParse(code.Trim(), out n)) errors.Add(label + ": exit code '" + code.Trim() + "' is not a number");
            }
            return errors;
        }

        public static List<string> Validate(AppConfig c, string name)
        {
            List<string> errors = new List<string>();
            if (!ModuleOn(c, name)) return errors;      // a part that is switched off is not checked
            switch (name)
            {
                case "general":
                    if (c.General.Modules.Vertical)
                    {
                        if (c.General.FfmpegPath.Length == 0) errors.Add("general: ffmpeg path is required");
                        if (c.General.FfprobePath.Length == 0) errors.Add("general: ffprobe path is required");
                    }
                    break;

                case "gpio":
                    errors.AddRange(ValidateGpio(c.Gpio));
                    break;

                case "studios":
                    errors.AddRange(StudioSettings.Validate(c));
                    break;

                case "folders":
                    for (int i = 0; i < c.Folders.Count; i++)
                        errors.AddRange(ValidateFolder(c.Folders[i], "folder " + (i + 1) + " (" + c.Folders[i].Name + ")"));
                    break;

                case "programs":
                    for (int i = 0; i < c.Programs.Count; i++)
                        errors.AddRange(ValidateProgram(c.Programs[i], "program " + (i + 1) + " (" + c.Programs[i].Name + ")"));
                    break;

                case "scripts":
                    for (int i = 0; i < c.Scripts.Count; i++)
                        errors.AddRange(ValidateScript(c.Scripts[i], "script " + (i + 1) + " (" + c.Scripts[i].Name + ")"));
                    break;

                case "vertical":
                    {
                        VerticalCfg v = c.Vertical;
                        bool a = CheckDir(errors, "vertical: ingest folder", v.WatchDir);
                        bool b = CheckDir(errors, "vertical: output folder", v.OutDir);
                        if (a && b && Util.PathKey(v.WatchDir) == Util.PathKey(v.OutDir))
                            errors.Add("vertical: ingest and output must be different folders");
                        if (v.AfterSuccess == "move" && CheckDir(errors, "vertical: move originals to", v.MoveOriginalTo)
                            && a && Util.PathKey(v.WatchDir) == Util.PathKey(v.MoveOriginalTo))
                            errors.Add("vertical: originals must be moved to a folder other than the ingest folder");
                        if (v.Extensions.Count == 0) errors.Add("vertical: at least one file extension is required");
                        int i = 0;
                        foreach (SourceRule src in v.Sources)
                        {
                            i++;
                            if (src.Match.Length == 0) errors.Add("vertical source " + i + ": 'filename contains' is required");
                            if (Util.CleanFilenamePart(src.Prefix).Length == 0) errors.Add("vertical source " + i + ": output prefix is required");
                        }
                        if (Util.CleanFilenamePart(v.Other.Prefix).Length == 0) errors.Add("vertical: prefix for other files is required");
                        VideoCfg vid = v.Video;
                        if (vid.ResolutionMode == "custom" && vid.Width == 0 && vid.Height == 0)
                            errors.Add("vertical: custom resolution needs a width and/or height");
                        if (vid.Width % 2 != 0) errors.Add("vertical: width must be an even number");
                        if (vid.Height % 2 != 0) errors.Add("vertical: height must be an even number");
                        TryArgs(errors, "extra input options", vid.ExtraInputArgs);
                        TryArgs(errors, "extra output options", vid.ExtraOutputArgs);
                        break;
                    }

                case "alerts":
                    errors.AddRange(AlertSettings.Validate(c.Alerts));
                    break;
            }
            return errors;
        }

        public static List<string> ValidateAll(AppConfig c)
        {
            List<string> all = new List<string>();
            foreach (string s in Sections) all.AddRange(Validate(c, s));
            return all;
        }

        public static List<string> SplitAddresses(string text)
        {
            List<string> o = new List<string>();
            foreach (string w in (text ?? "").Split(',', ';', ' '))
                if (w.Trim().Length > 0) o.Add(w.Trim());
            return o;
        }

        static void TryArgs(List<string> errors, string label, string text)
        {
            try { Util.SplitArgs(text); }
            catch (FormatException ex) { errors.Add("vertical: " + label + ": " + ex.Message); }
        }

        public static object GetSection(AppConfig c, string name)
        {
            switch (name)
            {
                case "general": return c.General;
                case "folders": return c.Folders;
                case "programs": return c.Programs;
                case "scripts": return c.Scripts;
                case "vertical": return c.Vertical;
                case "gpio": return c.Gpio;
                case "studios": return c.Studios;
                case "alerts": return c.Alerts;
            }
            return null;
        }

        public static bool SetSection(AppConfig c, string name, object value)
        {
            switch (name)
            {
                case "general": c.General = value as GeneralCfg; return c.General != null;
                case "folders": c.Folders = value as List<FolderPolicy>; return c.Folders != null;
                case "programs": c.Programs = value as List<ProgramJob>; return c.Programs != null;
                case "scripts": c.Scripts = value as List<ScriptJob>; return c.Scripts != null;
                case "vertical": c.Vertical = value as VerticalCfg; return c.Vertical != null;
                case "gpio": c.Gpio = value as GpioCfg; return c.Gpio != null;
                case "studios": c.Studios = value as StudiosCfg; return c.Studios != null;
                case "alerts": c.Alerts = value as AlertCfg; return c.Alerts != null;
            }
            return false;
        }

        /// <summary>Parse a configuration file.</summary>
        public static AppConfig Parse(string text)
        {
            object json = Json.Parse(text);
            JObject o = json as JObject;
            if (o == null) throw new FormatException("the file does not contain a JSON object");
            AppConfig c = new AppConfig();
            int version = (int)o.Num("version", 3);
            Json.FillFrom(c, json);
            if (version < 3) c.Alerts = AlertSettings.FromVersion2(o.Get("alerts") as JObject);
            Normalize(c);
            return c;
        }

        public static string ToText(AppConfig c)
        {
            return Json.Serialize(Json.ToJson(c), true) + "\n";
        }

        public static string Hash(string text)
        {
            using (SHA1 sha = SHA1.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""));
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < 6; i++) sb.Append(h[i].ToString("x2"));
                return sb.ToString();
            }
        }

        public const string CacheDrive = @"T:\";
        public const string DataDrive = @"D:\";

        /// <summary>Move finished recordings from a cache-drive folder to the data drive, checked every minute.
        /// Every finished file goes, so the cache folder is emptied on each run.</summary>
        public static FolderPolicy Offload(string name, string source, string destination)
        {
            FolderPolicy p = new FolderPolicy();
            p.Name = name;
            p.Notes = "Cache to data drive, every minute. Files still being recorded are left until they are finished.";
            p.Path = source;
            p.Destination = destination;
            p.Schedule = new ScheduleCfg("interval", "00:00", 1);
            p.MinAgeSeconds = 60;
            PolicyMode.Apply(p, PolicyMode.Move, "*", 0, null, "rename");
            return p;
        }

        /// <summary>Retention on the data drive: delete files older than N days in the folder and all its sub-folders.</summary>
        public static FolderPolicy Cleanup(string name, string folder, double days)
        {
            FolderPolicy p = new FolderPolicy();
            p.Name = name;
            p.Path = folder;
            p.Subfolders = true;
            p.Notes = "Retention on the data drive. The cache drive needs none: the move policies empty it every minute.";
            p.Schedule = new ScheduleCfg("interval", "00:00", 60);
            PolicyMode.Apply(p, PolicyMode.Delete, "*", days, "days", null);
            return p;
        }

        /// <summary>Configuration used when no file exists yet: recorders write to the cache drive (T:); separate
        /// every-minute move policies empty it onto the data drive (D:); retention runs on D: only. All switched off.</summary>
        public static AppConfig Starter()
        {
            AppConfig c = new AppConfig();
            c.Folders.Add(Offload("Move ISO  T: > D:", CacheDrive + "ISO", DataDrive + "ISO"));
            c.Folders.Add(Offload("Move PGM1  T: > D:", CacheDrive + "PGM1", DataDrive + "PGM1"));
            FolderPolicy cam = Templates.TimeWindowSort(CacheDrive + "Producer_Cam", DataDrive + "ISO", "05:45", "09:00",
                new List<string> { ".mp4", ".avi", ".mkv", ".mov" }, "delete", 60, true);
            cam.Name = "Producer cam sort  T: > D:";
            cam.Schedule = new ScheduleCfg("interval", "00:00", 1);
            cam.Notes = "Every minute. Recordings started 05:45-09:00 move to D:\\ISO, other recordings are deleted, "
                      + "anything else moves to D:\\Producer_Cam, so the cache folder is emptied.";
            FileRule rest = new FileRule();
            rest.Name = "Anything else: move to D:\\Producer_Cam";
            rest.Action = "move";
            rest.Destination = DataDrive + "Producer_Cam";
            rest.OnConflict = "rename";
            cam.Rules.Add(rest);
            c.Folders.Add(cam);
            c.Folders.Add(Cleanup("Retention D:\\ISO (7 days)", DataDrive + "ISO", 7));
            c.Folders.Add(Cleanup("Retention D:\\PGM1 (7 days)", DataDrive + "PGM1", 7));
            c.Folders.Add(Cleanup("Retention D:\\Vertical (7 days)", DataDrive + "Vertical", 7));
            c.Folders.Add(Cleanup("Retention D:\\Producer_Cam (7 days)", DataDrive + "Producer_Cam", 7));
            c.Vertical.WatchDir = CacheDrive + "Vertical";
            c.Vertical.OutDir = DataDrive + "Vertical";
            c.Vertical.PollSeconds = 60;
            c.Vertical.AfterSuccess = "move";
            c.Vertical.MoveOriginalTo = DataDrive + @"Vertical\Originals";
            c.Programs.Add(Templates.Vvd());
            c.Programs.Add(Templates.VMix());
            c.Gpio.Vmix.Add(new VmixTarget());          // vMix on this server, for GPIO triggers
            c.Alerts = AlertSettings.Defaults();
            c.General.Profile = "studio";
            Normalize(c);
            return c;
        }

        public static readonly string[] Profiles = { "studio", "watchdog" };

        /// <summary>Starting configuration for a kind of server:
        /// studio = everything (recordings, vMix, vertical conversion, GPIO); watchdog = keep programs and services running and alert.</summary>
        public static AppConfig Starter(string profile)
        {
            if (profile != "watchdog") return Starter();
            AppConfig c = new AppConfig();
            c.General.Profile = "watchdog";
            c.General.Modules.Folders = false;
            c.General.Modules.Programs = true;
            c.General.Modules.Scripts = true;
            c.General.Modules.Vertical = false;
            c.General.Modules.Gpio = false;
            c.Alerts = AlertSettings.Defaults();
            Normalize(c);
            return c;
        }

        /// <summary>One line per program and script the configuration can run, with where it runs, and a warning when an
        /// account that is not an administrator could change it (shown on import, deploy and migration).</summary>
        public static List<string> Executables(AppConfig c)
        {
            List<string> o = new List<string>();
            foreach (ProgramJob p in c.Programs)
            {
                if (p.Kind == "service") { o.Add("Watchdog '" + p.Name + "': Windows service " + p.ServiceName); continue; }
                string where = p.RunAs == "service" ? "as Local System" : "in the signed-in session";
                o.Add("Watchdog '" + p.Name + "' (" + where + (p.Enabled ? "" : ", off") + "): " + p.ExePath + Warn(p.ExePath));
            }
            foreach (ScriptJob s in c.Scripts)
            {
                string where = s.RunAs == "user" ? "in the signed-in session" : "as Local System";
                o.Add("Script '" + s.Name + "' (" + where + ", " + s.Trigger + (s.Enabled ? "" : ", off") + "): " + s.Command + Warn(s.Command));
            }
            if (c.General.Modules.Vertical || c.Vertical.Enabled)
            {
                o.Add("ffmpeg: " + c.General.FfmpegPath + Warn(c.General.FfmpegPath));
                o.Add("ffprobe: " + c.General.FfprobePath + Warn(c.General.FfprobePath));
            }
            return o;
        }

        /// <summary>Programs that would run as Local System from a place another account could change.</summary>
        public static List<string> UntrustedExecutables(AppConfig c)
        {
            List<string> o = new List<string>();
            foreach (ProgramJob p in c.Programs)
            {
                if (p.Kind != "service" && p.ExePath.Length > 0) Add(o, "Watchdog '" + p.Name + "'", p.ExePath);
                if (p.Kind != "service" && p.WorkingDir.Length > 0) AddFolder(o, "Watchdog '" + p.Name + "' start-in folder", p.WorkingDir);
            }
            foreach (ScriptJob s in c.Scripts)
            {
                if (s.Command.Length > 0) Add(o, "Script '" + s.Name + "'", s.Command);
                if (s.RunAs != "user" && s.WorkingDir.Length > 0) AddFolder(o, "Script '" + s.Name + "' start-in folder", s.WorkingDir);
            }
            if (c.General.Modules.Vertical)
            {
                Add(o, "ffmpeg", c.General.FfmpegPath);
                Add(o, "ffprobe", c.General.FfprobePath);
            }
            return o;
        }

        static void Add(List<string> o, string what, string path)
        {
            string why = PathTrust.Check(path);
            if (why != null) o.Add(what + ": " + why);
        }

        static void AddFolder(List<string> o, string what, string path)
        {
            string why = PathTrust.CheckFolder(path);
            if (why != null) o.Add(what + ": " + why);
        }

        static string Warn(string path)
        {
            string why = PathTrust.Check(path);
            return why == null ? "" : "  << NOT PROTECTED: " + why;
        }

        /// <summary>Safe mode: every part switched off, so nothing runs; alerts still work.</summary>
        public static void SafeView(AppConfig c)
        {
            c.General.Modules.Folders = false;
            c.General.Modules.Programs = false;
            c.General.Modules.Scripts = false;
            c.General.Modules.Vertical = false;
            c.General.Modules.Gpio = false;
            c.General.Modules.Studios = false;
        }

        /// <summary>Every password, token and secret address of a configuration: map(label, value) returns the value to keep.</summary>
        public static void ForEachSecret(AppConfig c, Func<string, string, string> map)
        {
            foreach (AlertDestination d in c.Alerts.Destinations)
            {
                string l = "Alert destination " + d.Name + ": ";
                d.SmtpPassword = map(l + "SMTP password", d.SmtpPassword ?? "");
                d.JiraToken = map(l + "Jira token", d.JiraToken ?? "");
                d.Secret = map(l + "webhook secret", d.Secret ?? "");
                if (d.Type == "webhook" || Secrets.IsEncrypted(d.Url)) d.Url = map(l + "webhook address", d.Url ?? "");
                d.Headers = map(l + "extra headers", d.Headers ?? "");
                d.ProxyUrl = map(l + "proxy address", d.ProxyUrl ?? "");
            }
            foreach (ProgramJob p in c.Programs) p.HealthPassword = map("Watchdog " + p.Name + ": health check password", p.HealthPassword ?? "");
            foreach (AxiaDevice d in c.Gpio.Devices) d.Password = map("GPIO device " + d.Name + ": password", d.Password ?? "");
            foreach (VmixTarget v in c.Gpio.Vmix) v.Password = map("vMix " + v.Name + ": password", v.Password ?? "");
            if (c.Studios != null && c.Studios.Pathfinder != null)
                c.Studios.Pathfinder.Password = map("Pathfinder: password", c.Studios.Pathfinder.Password ?? "");
            foreach (GpioTrigger t in c.Gpio.Triggers)
            {
                foreach (GpioAction a in t.OnActive) a.Password = map("GPIO trigger " + t.Name + ": HTTP password", a.Password ?? "");
                foreach (GpioAction a in t.OnInactive) a.Password = map("GPIO trigger " + t.Name + ": HTTP password", a.Password ?? "");
            }
        }

        /// <summary>Encrypt every secret that is in the file as plain text (a deployed or hand-edited file) with the given
        /// function (the service's own key in the service, this computer's key in a deploy). Returns how many.</summary>
        public static int ProtectSecrets(AppConfig c, Func<string, string> protect)
        {
            int n = 0;
            ForEachSecret(c, delegate(string label, string v)
            {
                if (string.IsNullOrEmpty(v) || Secrets.IsServiceEncrypted(v) || v.StartsWith(Secrets.KeepPrefix)) return v;
                if (Secrets.IsEncrypted(v) && protect == (Func<string, string>)Secrets.Protect) return v;
                string p = protect(Secrets.Unprotect(v));
                if (p == v) return v;              // already as protected as this system allows ("plain:" without Windows)
                n++;
                return p;
            });
            return n;
        }

        public static int ProtectSecrets(AppConfig c) { return ProtectSecrets(c, Secrets.Protect); }

        /// <summary>True when a secret is still stored in a form the service should convert (plain text or this computer's key).</summary>
        public static bool HasUnconvertedSecrets(AppConfig c)
        {
            bool any = false;
            ForEachSecret(c, delegate(string label, string v)
            {
                if (!string.IsNullOrEmpty(v) && !Secrets.IsServiceEncrypted(v)) any = true;
                return v;
            });
            return any;
        }

        /// <summary>A copy for another server: no server name, and no passwords, tokens or secret addresses (they only work
        /// on this computer, and a webhook address is itself the key to the channel). Returns where something was removed.</summary>
        public static AppConfig ForExport(AppConfig c, List<string> removed)
        {
            AppConfig o = Json.Clone(c);
            o.General.ServerLabel = "";
            ForEachSecret(o, delegate(string label, string v)
            {
                if (string.IsNullOrEmpty(v)) return "";
                removed.Add(label);
                return "";
            });
            return o;
        }
    }

    /// <summary>The common one-rule policies ("move", "copy", "delete" after an age) that the manager offers
    /// without the rule editor. Anything else is "custom".</summary>
    public static class PolicyMode
    {
        public const string Move = "move", Copy = "copy", Delete = "delete", Custom = "custom";

        public static string Of(FolderPolicy f)
        {
            if (f == null || f.Rules == null || f.Rules.Count != 1) return Custom;
            FileRule r = f.Rules[0];
            if (r == null || !r.Enabled || (r.Exclude ?? "").Length > 0 || r.UseWindow || r.MinSizeMb > 0 || r.MaxSizeMb > 0
                || r.KeepNewest > 0 || r.StampFromName || (r.Destination ?? "").Trim().Length > 0 || !r.KeepSubfolders
                || (r.Pattern ?? "").Trim().Length == 0)
                return Custom;
            if ((r.Action == Move || r.Action == Copy) && r.OlderThan == 0) return r.Action;
            if (r.Action == Delete && r.OlderThan >= 1 && r.OlderThan == Math.Floor(r.OlderThan)) return Delete;
            return Custom;
        }

        /// <summary>Replaces the policy's rules (in place) with the single rule for this mode. Custom leaves them alone.</summary>
        public static void Apply(FolderPolicy f, string mode, string pattern, double age, string unit, string onConflict)
        {
            if (mode != Move && mode != Copy && mode != Delete) return;
            FileRule old = f.Rules.Count == 1 ? f.Rules[0] : null;
            FileRule r = new FileRule();
            if (old != null) r.Id = old.Id;
            r.Action = mode;
            r.Pattern = (pattern ?? "").Trim().Length == 0 ? "*" : pattern.Trim();
            string what = r.Pattern == "*" ? "every file" : r.Pattern;
            if (mode == Delete)
            {
                r.OlderThan = Math.Max(1, Math.Floor(age));
                r.OlderThanUnit = unit ?? "days";
                r.Name = "Delete " + (r.Pattern == "*" ? "" : r.Pattern + " ") + "after " + FileOps.DescribeAge(r.OlderThan, r.OlderThanUnit);
            }
            else
            {
                r.OnConflict = onConflict ?? "rename";
                r.Name = (mode == Move ? "Move " : "Copy ") + what;
            }
            f.Rules.Clear();
            f.Rules.Add(r);
        }

        public static string Label(string mode)
        {
            switch (mode)
            {
                case Move: return "move";
                case Copy: return "copy";
                case Delete: return "retention";
            }
            return "custom rules";
        }
    }

    /// <summary>Ready-made entries offered by the manager.</summary>
    public static class Templates
    {
        /// <summary>Sort by recording time: recordings that started inside the window (time read from the
        /// file name) move to the destination, the rest are deleted or kept.</summary>
        public static FolderPolicy TimeWindowSort(string src, string dst, string start, string end, List<string> exts,
                                                     string outside, int minAge, bool stamp)
        {
            List<string> pats = new List<string>();
            foreach (string e in exts)
            {
                string x = e.Trim();
                if (x.Length == 0) continue;
                pats.Add("*" + (x.StartsWith(".") ? x : "." + x));
            }
            string pattern = pats.Count > 0 ? string.Join(";", pats.ToArray()) : "*";

            FolderPolicy p = new FolderPolicy();
            p.Name = "Producer cam";
            p.Path = src;
            p.Destination = dst;
            p.Schedule = new ScheduleCfg("daily", "09:30", 60);
            p.MinAgeSeconds = minAge;

            FileRule inside = new FileRule();
            inside.Name = "Started " + start + "-" + end + ": move to ISO";
            inside.Action = "move";
            inside.Pattern = pattern;
            inside.UseWindow = true;
            inside.WindowStart = start;
            inside.WindowEnd = end;
            inside.WindowMode = "inside";
            inside.WindowSource = "filename";
            inside.StampFromName = stamp;
            inside.OnConflict = "overwrite";
            p.Rules.Add(inside);

            if (outside != "keep")
            {
                FileRule rest = new FileRule();
                rest.Name = "Started outside the window: delete";
                rest.Action = "delete";
                rest.Pattern = pattern;
                rest.UseWindow = true;
                rest.WindowStart = start;
                rest.WindowEnd = end;
                rest.WindowMode = "outside";
                rest.WindowSource = "filename";
                p.Rules.Add(rest);
            }
            return p;
        }

        public static ProgramJob Vvd()
        {
            ProgramJob p = new ProgramJob();
            p.Name = "Virtual Video Director";
            p.ExePath = @"C:\Program Files (x86)\noise productions ltd\Virtual Video Director\Virtual Video Director.exe";
            p.Arguments = "\"C:\\ProgramData\\Virtual Video Director\\Profiles\\Profile.xml\"";
            p.ProcessName = "Virtual Video Director";
            p.RunAs = "user";
            return p;
        }

        public static ProgramJob VMix()
        {
            ProgramJob p = new ProgramJob();
            p.Name = "vMix";
            p.ExePath = @"C:\Program Files (x86)\vMix\vMix64.exe";
            p.Arguments = "";
            p.ProcessName = "vMix64";
            p.RunAs = "user";
            p.Priority = "high";
            p.StartDelaySeconds = 30;
            // vMix answers on its Web API when it is responsive (Settings > Web Controller must be on)
            p.HealthUrl = "http://127.0.0.1:8088/api";
            p.HealthGraceSeconds = 120;
            p.HealthFailSeconds = 60;
            return p;
        }
    }

    // ------------------------------------------------------------------- store
    public class ConfigStore
    {
        readonly object lk = new object();
        readonly string path;
        readonly SourceLogger log;
        AppConfig cfg = new AppConfig();
        string loadedHash = "";
        double loadedAt;
        long fileTicks = -1, fileLength = -1;
        List<string> problems = new List<string>();
        string guardProblem;        // the settings folder is not protected (DataDirSecurity.Verify)
        string backupInUse;         // running from config.json.bak because config.json cannot be read
        double guardAt;

        /// <summary>Encrypts a secret typed in ("plain:" or plain text) when the file is written: the manager asks the service
        /// (its own key), the service uses its own key. Null: values are written as they are.</summary>
        public Func<string, string> Protector;

        /// <summary>The service sets this: null when the settings folder is protected, otherwise what is wrong.
        /// While it reports a problem, Get() returns the settings with every part switched off (safe mode).</summary>
        public Func<string> Guard;

        /// <summary>Why nothing runs (safe mode), or null.</summary>
        public string SafeReason
        {
            get
            {
                lock (lk)
                {
                    if (guardProblem != null) return "the settings folder is not protected: " + guardProblem;
                    return backupInUse;
                }
            }
        }

        /// <summary>Raised after a section has been saved, or "*" after the whole file was (re)loaded.</summary>
        public event Action<string> Changed;

        public ConfigStore(string path, SourceLogger log)
        {
            this.path = path;
            this.log = log;
            Cfg.Normalize(cfg);
        }

        public string Path { get { return path; } }
        public string LoadedHash { get { lock (lk) return loadedHash; } }
        public double LoadedAt { get { lock (lk) return loadedAt; } }
        public List<string> Problems { get { lock (lk) return new List<string>(problems); } }

        static string ReadWithRetry(string p)
        {
            for (int attempt = 0; ; attempt++)
            {
                try { return File.ReadAllText(p); }
                catch (IOException)
                {
                    if (attempt >= 4) throw;
                    Thread.Sleep(150);
                }
            }
        }

        void Stamp()
        {
            try
            {
                FileInfo fi = new FileInfo(path);
                fileTicks = fi.Exists ? fi.LastWriteTimeUtc.Ticks : -1;
                fileLength = fi.Exists ? fi.Length : -1;
            }
            catch (Exception) { fileTicks = -1; fileLength = -1; }
        }

        void CheckGuard()
        {
            guardAt = Util.Now();
            if (Guard == null) { guardProblem = null; return; }
            string g;
            try { g = Guard(); }
            catch (Exception ex) { g = "cannot check the settings folder: " + ex.Message; }
            if (g != guardProblem && log != null)
            {
                if (g != null) log.Error("SAFE MODE: the settings folder is not protected (" + g + "). No folder policy, program, script, conversion or GPIO trigger runs until this is fixed.");
                else if (guardProblem != null) log.Info("The settings folder is protected again; leaving safe mode.");
            }
            guardProblem = g;
        }

        List<string> WithSafeReason(List<string> list)
        {
            string r = guardProblem != null ? "SAFE MODE: the settings folder is not protected: " + guardProblem
                     : backupInUse != null ? "SAFE MODE: " + backupInUse : null;
            if (r != null && !list.Contains(r)) list.Insert(0, r);
            return list;
        }

        /// <summary>Load the file. Missing: write the starter configuration (when createIfMissing).
        /// Unreadable: try the .bak copy, then fall back to the starter, and never overwrite the bad file.</summary>
        public void Load(bool createIfMissing)
        {
            lock (lk)
            {
                CheckGuard();
                backupInUse = null;
                if (!File.Exists(path))
                {
                    cfg = Cfg.Starter();
                    if (createIfMissing)
                    {
                        Util.WriteAtomic(path, Cfg.ToText(cfg));
                        if (log != null) log.Info("Created starter configuration (everything switched off): " + path);
                    }
                    Accept(cfg, Cfg.ToText(cfg));
                    return;
                }
                string text = null;
                AppConfig loaded = null;
                try
                {
                    text = ReadWithRetry(path);
                    loaded = Cfg.Parse(text);
                }
                catch (Exception ex)
                {
                    if (log != null) log.Error("Configuration file cannot be read (" + ex.Message + "): " + path);
                    try
                    {
                        string bak = path + ".bak";
                        text = ReadWithRetry(bak);
                        loaded = Cfg.Parse(text);
                        backupInUse = "config.json cannot be read; the backup copy " + bak + " is shown, but nothing runs until config.json is fixed and saved";
                        if (log != null) log.Warn("SAFE MODE: using the backup copy " + bak + " for display only; nothing runs until the file is fixed.");
                    }
                    catch (Exception)
                    {
                        loaded = Cfg.Starter();
                        text = Cfg.ToText(loaded);
                        if (log != null) log.Error("No usable backup either; running the starter configuration (everything off).");
                    }
                    problems = WithSafeReason(new List<string> { "configuration file unreadable: " + ex.Message });
                    cfg = loaded;
                    loadedHash = Cfg.Hash(text);
                    loadedAt = Util.Now();
                    Stamp();
                    return;
                }
                Accept(loaded, text);
            }
        }

        public void Load() { Load(true); }

        void Accept(AppConfig c, string text)
        {
            cfg = c;
            loadedHash = Cfg.Hash(text);
            loadedAt = Util.Now();
            problems = WithSafeReason(Cfg.ValidateAll(c));
            Stamp();
            if (log != null) foreach (string err in problems) if (!err.StartsWith("SAFE MODE")) log.Warn("Configuration problem: " + err);
        }

        /// <summary>Reload if the file changed on disk. Returns null when nothing changed, otherwise
        /// the list of problems (empty = applied cleanly). A file that cannot be parsed is not applied.</summary>
        public List<string> CheckForChanges()
        {
            long t = -1, l = -1;
            try
            {
                FileInfo fi = new FileInfo(path);
                if (fi.Exists) { t = fi.LastWriteTimeUtc.Ticks; l = fi.Length; }
            }
            catch (Exception) { }
            lock (lk)
            {
                if (t == fileTicks && l == fileLength)
                {
                    if (Guard == null || Util.Now() - guardAt < 30) return null;
                    string before = guardProblem;
                    CheckGuard();
                    if (before == guardProblem) return null;
                    problems = WithSafeReason(Cfg.ValidateAll(cfg));
                }
                else return Reload();
            }
            Raise("*");
            return Problems;
        }

        /// <summary>Re-read the file now. Parse errors keep the current configuration.</summary>
        public List<string> Reload()
        {
            List<string> result;
            lock (lk)
            {
                CheckGuard();
                if (!File.Exists(path))
                {
                    Stamp();
                    result = new List<string> { "configuration file is missing; keeping the current settings: " + path };
                    problems = result;
                    if (log != null) log.Error(result[0]);
                    return result;
                }
                string text;
                AppConfig loaded;
                try
                {
                    text = ReadWithRetry(path);
                    loaded = Cfg.Parse(text);
                }
                catch (Exception ex)
                {
                    Stamp();
                    result = new List<string> { "configuration file rejected, keeping the previous settings: " + ex.Message };
                    problems = result;
                    if (log != null) log.Error(result[0]);
                    return result;
                }
                string hash = Cfg.Hash(text);
                bool same = hash == loadedHash && backupInUse == null;
                backupInUse = null;
                Accept(loaded, text);
                result = new List<string>(problems);
                if (same) return result;
                if (log != null) log.Info("Configuration reloaded from " + path + " (" + hash + ").");
            }
            Raise("*");
            return result;
        }

        public AppConfig Get()
        {
            lock (lk)
            {
                AppConfig c = Json.Clone(cfg);
                if (Guard != null && (guardProblem != null || backupInUse != null)) Cfg.SafeView(c);
                return c;
            }
        }

        void Raise(string name)
        {
            Action<string> handler = Changed;
            if (handler == null) return;
            try { handler(name); }
            catch (Exception ex) { if (log != null) log.Error("Config listener failed: " + ex.Message); }
        }

        void Write(AppConfig candidate)
        {
            if (Protector != null)
                Cfg.ForEachSecret(candidate, delegate(string label, string v)
                {
                    if (string.IsNullOrEmpty(v) || Secrets.IsEncrypted(v)) return v ?? "";
                    if (v.StartsWith(Secrets.KeepPrefix)) throw new InvalidOperationException(label + ": internal reference left in the settings");
                    return Protector(Secrets.Peek(v));
                });
            string text = Cfg.ToText(candidate);
            if (File.Exists(path))
            {
                try { File.Copy(path, path + ".bak", true); } catch (Exception) { }
            }
            Util.WriteAtomic(path, text);
            cfg = candidate;
            loadedHash = Cfg.Hash(text);
            loadedAt = Util.Now();
            backupInUse = null;
            problems = WithSafeReason(Cfg.ValidateAll(candidate));
            Stamp();
        }

        /// <summary>Validate and persist one section. Returns the list of errors; empty means saved.</summary>
        public List<string> SaveSection(string name, object value)
        {
            List<string> errors = new List<string>();
            if (Array.IndexOf(Cfg.Sections, name) < 0)
            {
                errors.Add("unknown section '" + name + "'");
                return errors;
            }
            lock (lk)
            {
                AppConfig candidate = Json.Clone(cfg);
                object copy = Json.CloneObject(value);
                if (copy == null || !Cfg.SetSection(candidate, name, copy))
                {
                    errors.Add("invalid value for section '" + name + "'");
                    return errors;
                }
                Cfg.Normalize(candidate);
                errors = Cfg.Validate(candidate, name);
                if (errors.Count > 0) return errors;
                Write(candidate);
            }
            Raise(name);
            return errors;
        }

        /// <summary>Service: re-encrypt every secret that is not yet stored with the service's own key (plain text, or this
        /// computer's key), in the file and in its backup copies. Returns how many were converted.</summary>
        public int ConvertSecrets(Func<string, string> protect)
        {
            int n;
            lock (lk)
            {
                if (!File.Exists(path)) return 0;
                AppConfig c = Cfg.Parse(ReadWithRetry(path));
                if (!Cfg.HasUnconvertedSecrets(c)) n = 0;
                else
                {
                    n = Cfg.ProtectSecrets(c, protect);
                    Func<string, string> keep = Protector;
                    Protector = null;
                    try { Write(c); }
                    finally { Protector = keep; }
                }
                foreach (string extra in new[] { path + ".bak", path + ".before-deploy" })
                {
                    try
                    {
                        if (!File.Exists(extra)) continue;
                        AppConfig b = Cfg.Parse(File.ReadAllText(extra));
                        if (!Cfg.HasUnconvertedSecrets(b)) continue;
                        Cfg.ProtectSecrets(b, protect);
                        Util.WriteAtomic(extra, Cfg.ToText(b));
                    }
                    catch (Exception ex) { if (log != null) log.Warn("Could not re-encrypt the secrets in " + extra + ": " + ex.Message); }
                }
            }
            return n;
        }

        /// <summary>Replace the whole configuration (import). Every section must validate.</summary>
        public List<string> SaveAll(AppConfig value)
        {
            List<string> errors;
            lock (lk)
            {
                AppConfig candidate = Json.Clone(value);
                Cfg.Normalize(candidate);
                errors = Cfg.ValidateAll(candidate);
                if (errors.Count > 0) return errors;
                Write(candidate);
            }
            Raise("*");
            return errors;
        }
    }
}
