// Reading, checking and saving the settings file, with a copy kept in the backups folder for every change.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Watchkeeper
{
    public class Problem
    {
        public string Level = "error";      // error (it will not work) | warning (it works, but check it)
        public string Where = "";
        public string Text = "";
        public Problem() { }
        public Problem(string level, string where, string text) { Level = level; Where = where; Text = text; }
        public override string ToString() { return (Where.Length > 0 ? Where + ": " : "") + Text; }
    }

    public class Backup
    {
        public string File = "";
        public DateTime Time;
        public string Reason = "";
    }

    public class LoadResult
    {
        public Settings Settings;
        /// <summary>file | new | restored (config.json was missing; the newest backup was put back) | imported (version 3)</summary>
        public string Source = "file";
        /// <summary>Set when config.json could not be read; Settings is then null.</summary>
        public string Error;
        /// <summary>Settings in the file that are not used (unknown names, wrong kinds of value).</summary>
        public List<string> Notes = new List<string>();
        /// <summary>Version 3 import: what was converted and what was left out.</summary>
        public List<string> ImportReport;
    }

    public class SettingsFile
    {
        public const string FormatName = "watchkeeper-4";
        public readonly string DataDir, Path, BackupDir;
        public int KeepBackups = 50;
        readonly Logger log;
        long stampLength = -1, stampTicks = -1;

        public SettingsFile(string dataDir, Logger log)
        {
            DataDir = dataDir;
            Path = System.IO.Path.Combine(dataDir, "config.json");
            BackupDir = System.IO.Path.Combine(dataDir, "backups");
            this.log = log;
        }

        // ---------------------------------------------------------------- text
        public static string ToText(Settings s) { return Json.Serialize(Json.ToJson(s), true) + Environment.NewLine; }

        /// <summary>Parse version 4 settings text. Throws FormatException with the line number when it is not valid JSON.</summary>
        public static Settings Parse(string text, List<string> notes)
        {
            object json = ParseJson(text);
            JObject o = json as JObject;
            if (o == null) throw new FormatException("the file does not contain a group of settings ({ ... })");
            if (notes != null) Json.Check(typeof(Settings), o, "", notes);
            Settings s = new Settings();
            Json.FillFrom(s, o);
            Normalize(s);
            return s;
        }

        public static object ParseJson(string text)
        {
            try { return Json.Parse(text); }
            catch (FormatException ex)
            {
                Match m = Regex.Match(ex.Message, @" at (\d+)$");
                if (!m.Success) throw new FormatException("not valid JSON: " + ex.Message);
                int at = Math.Min(text.Length, int.Parse(m.Groups[1].Value, Util.Inv));
                int line = 1, col = 1;
                for (int i = 0; i < at; i++) { if (text[i] == '\n') { line++; col = 1; } else col++; }
                throw new FormatException("not valid JSON at line " + line + ", column " + col + " (" + Regex.Replace(ex.Message, @" at \d+$", "") + ")");
            }
        }

        public static bool IsVersion3(JObject o)
        {
            return o != null && !o.Has("format") && (o.Has("programs") || o.Has("folders") || o.Has("general") || o.Has("vertical"));
        }

        public static Settings Clone(Settings s)
        {
            Settings c = new Settings();
            Json.FillFrom(c, Json.ToJson(s));
            return c;
        }

        // ---------------------------------------------------------------- load
        /// <summary>Version 3 settings are converted only when this is true (the service: only Local System can read the
        /// passwords version 3 encrypted with the service's own key).</summary>
        public bool ImportVersion3 = true;

        /// <summary>Save the file when ids had to be added (the service; the manager leaves that to the service).</summary>
        public bool SaveIds = true;

        public LoadResult Load()
        {
            LoadResult r = new LoadResult();
            if (!File.Exists(Path))
            {
                Backup b = NewestReadable();
                if (b != null)
                {
                    r.Settings = Parse(Util.ReadAllTextRetry(b.File), null);
                    Save(r.Settings, "restored");
                    r.Source = "restored";
                    if (log != null) log.Warn("config.json was missing; put back the newest backup (" + System.IO.Path.GetFileName(b.File) + ").");
                    return r;
                }
                r.Settings = new Settings();
                Save(r.Settings, "new");
                r.Source = "new";
                if (log != null) log.Info("No settings yet: created " + Path + ".");
                return r;
            }
            string text;
            try { text = Util.ReadAllTextRetry(Path); }
            catch (Exception ex) { r.Error = "config.json cannot be read: " + ex.Message; Stamp(); return r; }
            object json;
            try { json = ParseJson(text); }
            catch (FormatException ex) { r.Error = "config.json is " + ex.Message; Stamp(); return r; }
            JObject o = json as JObject;
            if (o == null) { r.Error = "config.json does not contain a group of settings ({ ... })"; Stamp(); return r; }

            if (IsVersion3(o) && !ImportVersion3)
            {
                r.Error = "config.json holds version 3 settings. The service converts them when it starts: start (or install) the service, then open this window again";
                return r;
            }
            if (IsVersion3(o))
            {
                r.ImportReport = new List<string>();
                r.Settings = Import3.Convert(o, r.ImportReport);
                Normalize(r.Settings);
                Save(r.Settings, "imported-3x");
                r.Source = "imported";
                try { Util.WriteAtomic(System.IO.Path.Combine(DataDir, "import-report.txt"), string.Join(Environment.NewLine, r.ImportReport.ToArray()) + Environment.NewLine); }
                catch (Exception) { }
                if (log != null)
                {
                    log.Warn("Converted the version 3 settings to version 4 (the old file is kept in the backups folder). See import-report.txt.");
                    foreach (string line in r.ImportReport) log.Info("import: " + line);
                }
                return r;
            }
            Json.Check(typeof(Settings), o, "", r.Notes);
            Settings s = new Settings();
            Json.FillFrom(s, o);
            if (Normalize(s) > 0 && SaveIds)
            {
                // ids identify entries to the manager, the state file and the alerts: they must stay the same from now on
                try { Save(s, "added-ids"); r.Notes.Add("entries without an id (or with the same id as another) were given one and the file was saved"); }
                catch (Exception ex) { r.Notes.Add("entries without an id were given one, but the file could not be saved: " + ex.Message); }
            }
            r.Settings = s;
            Stamp();
            return r;
        }

        /// <summary>The newest backup that can be read, or null.</summary>
        public Backup NewestReadable()
        {
            foreach (Backup b in Backups())
            {
                try
                {
                    object j = ParseJson(Util.ReadAllTextRetry(b.File));
                    if (j is JObject && !IsVersion3((JObject)j)) return b;
                }
                catch (Exception) { }
            }
            return null;
        }

        void Stamp()
        {
            try
            {
                FileInfo fi = new FileInfo(Path);
                stampLength = fi.Exists ? fi.Length : -1;
                stampTicks = fi.Exists ? fi.LastWriteTimeUtc.Ticks : -1;
            }
            catch (Exception) { stampLength = stampTicks = -2; }
        }

        /// <summary>The file changed since it was last loaded or saved here (saved by the manager or edited by hand).</summary>
        public bool ChangedOnDisk()
        {
            try
            {
                FileInfo fi = new FileInfo(Path);
                long l = fi.Exists ? fi.Length : -1, t = fi.Exists ? fi.LastWriteTimeUtc.Ticks : -1;
                return l != stampLength || t != stampTicks;
            }
            catch (Exception) { return false; }
        }

        // ---------------------------------------------------------------- save
        /// <summary>Save: passwords typed in plain text are encrypted, the file as it was is kept in the backups folder
        /// when it is not there yet, the new file replaces the old one in one step, and a copy of it is kept too.</summary>
        public void Save(Settings s, string reason)
        {
            Normalize(s);                     // every entry is saved with its id
            EncryptSecrets(s);
            string text = ToText(s);
            Directory.CreateDirectory(DataDir);
            if (File.Exists(Path))
            {
                string current = null;
                try { current = Util.ReadAllTextRetry(Path); } catch (Exception) { }
                if (current != null && current != text) KeepBackup(current, "before-" + reason);
            }
            Util.WriteAtomic(Path, text);
            Stamp();
            try { KeepBackup(text, reason); }
            catch (Exception ex) { if (log != null) log.Warn("Saved, but no backup copy could be kept: " + ex.Message); }
        }

        /// <summary>Put a settings file in place unchanged (deploy of a version 3 file: the service converts it, as only
        /// it can read the passwords version 3 encrypted for the service). The current file is kept as a backup first.</summary>
        public void SaveText(string text, string reason)
        {
            Directory.CreateDirectory(DataDir);
            if (File.Exists(Path))
            {
                string current = null;
                try { current = Util.ReadAllTextRetry(Path); } catch (Exception) { }
                if (current != null && current != text) KeepBackup(current, "before-" + reason);
            }
            Util.WriteAtomic(Path, text);
            Stamp();
        }

        void KeepBackup(string text, string reason)
        {
            Directory.CreateDirectory(BackupDir);
            List<Backup> all = Backups();
            if (all.Count > 0)
            {
                try { if (File.ReadAllText(all[0].File) == text) return; } catch (Exception) { }
            }
            DateTime t = DateTime.Now;
            t = new DateTime(t.Ticks - t.Ticks % TimeSpan.TicksPerMillisecond);
            if (all.Count > 0 && t <= all[0].Time) t = all[0].Time.AddMilliseconds(1);      // always newest, never the same name
            string name = "config-" + t.ToString("yyyyMMdd-HHmmss-fff", Util.Inv) + "-" + Regex.Replace(reason, "[^A-Za-z0-9-]", "-") + ".json";
            Util.WriteAtomic(System.IO.Path.Combine(BackupDir, name), text);
            all = Backups();
            for (int i = KeepBackups; i < all.Count; i++) { try { File.Delete(all[i].File); } catch (Exception) { } }
        }

        /// <summary>Kept copies, newest first.</summary>
        public List<Backup> Backups()
        {
            List<Backup> o = new List<Backup>();
            if (!Directory.Exists(BackupDir)) return o;
            foreach (string f in Directory.GetFiles(BackupDir, "config-*.json"))
            {
                Match m = Regex.Match(System.IO.Path.GetFileName(f), @"^config-(\d{8}-\d{6}-\d{3})-?(.*)\.json$");
                if (!m.Success) continue;
                DateTime t;
                if (!DateTime.TryParseExact(m.Groups[1].Value, "yyyyMMdd-HHmmss-fff", Util.Inv, System.Globalization.DateTimeStyles.None, out t)) continue;
                Backup b = new Backup();
                b.File = f;
                b.Time = t;
                b.Reason = m.Groups[2].Value;
                o.Add(b);
            }
            o.Sort(delegate(Backup a, Backup b) { return b.Time.CompareTo(a.Time); });
            return o;
        }

        public static string ReasonText(string reason)
        {
            if (reason.StartsWith("before-")) return "before " + ReasonText(reason.Substring(7));
            switch (reason)
            {
                case "new": return "first start";
                case "restored": return "put back after config.json was missing";
                case "imported-3x": return "converted from version 3";
                case "restore": return "restored from a backup";
                case "encrypted-passwords": return "passwords encrypted";
                case "deploy": return "deploy";
                case "edited": return "changed outside Watchkeeper";
            }
            return "saved: " + reason.Replace('-', ' ');
        }

        // ---------------------------------------------------------------- passwords
        static bool IsSecretField(FieldInfo f)
        {
            return f.FieldType == typeof(string) && (f.Name.EndsWith("Password") || f.Name.EndsWith("Token"));
        }

        /// <summary>Calls visit for every password / token field (anywhere in the settings), replacing it with the result.</summary>
        public static void ForEachSecret(object o, Func<string, string> visit)
        {
            if (o == null) return;
            System.Collections.IList list = o as System.Collections.IList;
            if (list != null) { foreach (object item in list) ForEachSecret(item, visit); return; }
            Type t = o.GetType();
            if (t.IsPrimitive || t == typeof(string)) return;
            foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (IsSecretField(f)) f.SetValue(o, visit((string)f.GetValue(o) ?? ""));
                else if (!f.FieldType.IsPrimitive && f.FieldType != typeof(string)) ForEachSecret(f.GetValue(o), visit);
            }
        }

        public static bool HasPlainSecrets(Settings s)
        {
            bool any = false;
            ForEachSecret(s, delegate(string v) { if (v.Length > 0 && !Secrets.IsEncrypted(v)) any = true; return v; });
            return any;
        }

        public static void EncryptSecrets(Settings s)
        {
            ForEachSecret(s, delegate(string v) { return Secrets.Protect(v); });
        }

        // ---------------------------------------------------------------- normalise
        static string C(string v, string d, params string[] choices)
        {
            string x = (v ?? "").Trim().ToLowerInvariant();
            foreach (string c in choices) if (c == x) return c;
            return d;
        }

        static int I(int v, int min, int max) { return Math.Max(min, Math.Min(max, v)); }
        static double D(double v, double min, double max) { return double.IsNaN(v) ? min : Math.Max(min, Math.Min(max, v)); }
        static string T(string v) { return (v ?? "").Trim(); }
        static string P(string v) { return (v ?? "").Trim().Trim('"').Trim(); }

        [ThreadStatic] static int idsMade;

        static string UniqueId(string id, HashSet<string> seen)
        {
            string x = T(id);
            if (x != (id ?? "")) idsMade++;
            if (x.Length == 0 || seen.Contains(x)) { x = Util.NewId(); idsMade++; }
            while (seen.Contains(x)) x = Util.NewId();
            seen.Add(x);
            return x;
        }

        public static readonly string[] DayNames = { "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday" };

        /// <summary>Fill in missing ids and names, and keep every value within its allowed range or choices. Returns how
        /// many ids were made or changed (entries added by hand without an id, or with the same id as another).</summary>
        public static int Normalize(Settings s)
        {
            idsMade = 0;
            s.Format = FormatName;
            if (s.General == null) s.General = new GeneralSettings();
            s.General.ServerName = T(s.General.ServerName);
            s.General.FfmpegPath = P(s.General.FfmpegPath);
            s.General.FfprobePath = P(s.General.FfprobePath);

            HashSet<string> seen = new HashSet<string>();
            if (s.Watchdog == null) s.Watchdog = new List<ProgramEntry>();
            s.Watchdog.RemoveAll(delegate(ProgramEntry x) { return x == null; });
            foreach (ProgramEntry p in s.Watchdog)
            {
                p.Id = UniqueId(p.Id, seen);
                p.Name = T(p.Name).Length > 0 ? T(p.Name) : "Program";
                p.Kind = C(p.Kind, "program", "program", "service");
                p.ServiceName = T(p.ServiceName);
                p.Path = P(p.Path);
                p.Arguments = T(p.Arguments);
                p.StartIn = P(p.StartIn);
                p.RunIn = C(p.RunIn, "desktop", "desktop", "background");
                if (p.RunIn != "desktop") p.Elevated = false;
                p.WatchPath = P(p.WatchPath);
                p.RunningMeans = C(p.RunningMeans, "auto", "auto", "app", "started", "either", "both");
                p.LauncherWaitSeconds = I(p.LauncherWaitSeconds, 5, 3600);
                p.StartDelaySeconds = I(p.StartDelaySeconds, 0, 3600);
                p.RestartDelaySeconds = I(p.RestartDelaySeconds, 1, 3600);
                p.MaxRestartsPerHour = I(p.MaxRestartsPerHour, 0, 1000);
                p.RestartAt = T(p.RestartAt);
                if (p.RestartDays == null) p.RestartDays = new List<string>();
                List<string> days = new List<string>();
                foreach (string d in p.RestartDays) { string x = T(d).ToLowerInvariant(); if (Array.IndexOf(DayNames, x) >= 0 && !days.Contains(x)) days.Add(x); }
                p.RestartDays = days;
                p.Priority = C(p.Priority, "normal", "normal", "above_normal", "high");
                p.HealthUrl = T(p.HealthUrl);
                p.HealthUser = T(p.HealthUser);
                p.HealthPassword = p.HealthPassword ?? "";
                p.HealthEverySeconds = I(p.HealthEverySeconds, 2, 3600);
                p.HealthFailSeconds = I(p.HealthFailSeconds, 2, 86400);
                p.HealthGraceSeconds = I(p.HealthGraceSeconds, 0, 3600);
                p.HealthAction = C(p.HealthAction, "alert", "alert", "restart");
                p.Notes = p.Notes ?? "";
            }

            if (s.Folders == null) s.Folders = new List<FolderTask>();
            s.Folders.RemoveAll(delegate(FolderTask x) { return x == null; });
            foreach (FolderTask f in s.Folders)
            {
                f.Id = UniqueId(f.Id, seen);
                f.Name = T(f.Name).Length > 0 ? T(f.Name) : "Folder task";
                f.Source = P(f.Source);
                f.Include = T(f.Include).Length > 0 ? T(f.Include) : "*";
                f.Exclude = T(f.Exclude);
                f.Action = C(f.Action, "move", "move", "copy", "delete");
                f.Destination = P(f.Destination);
                f.IfExists = C(f.IfExists, "rename", "rename", "skip", "overwrite");
                f.OlderThan = D(f.OlderThan, 0, 100000);
                f.OlderThanUnit = C(f.OlderThanUnit, "days", "minutes", "hours", "days");
                f.KeepNewest = I(f.KeepNewest, 0, 1000000);
                f.SettleSeconds = I(f.SettleSeconds, 0, 86400);
                f.Run = C(f.Run, "every", "every", "daily", "manual");
                f.IntervalMinutes = D(f.IntervalMinutes, 0.5, 10080);
                f.At = T(f.At).Length > 0 ? T(f.At) : "03:00";
                f.Notes = f.Notes ?? "";
            }

            VerticalSettings v = s.Vertical ?? (s.Vertical = new VerticalSettings());
            v.WatchFolder = P(v.WatchFolder);
            v.OutputFolder = P(v.OutputFolder);
            List<string> ext = new List<string>();
            foreach (string e in v.Extensions ?? new List<string>())
            {
                string x = T(e).ToLowerInvariant();
                if (x.Length == 0) continue;
                if (!x.StartsWith(".")) x = "." + x;
                if (!ext.Contains(x)) ext.Add(x);
            }
            if (ext.Count == 0) ext.Add(".mp4");
            v.Extensions = ext;
            v.SettleSeconds = I(v.SettleSeconds, 2, 3600);
            if (v.Sources == null) v.Sources = new List<VerticalSource>();
            v.Sources.RemoveAll(delegate(VerticalSource x) { return x == null; });
            foreach (VerticalSource src in v.Sources)
            {
                src.Name = T(src.Name).Length > 0 ? T(src.Name) : "Source";
                src.Match = T(src.Match);
                src.Prefix = Util.CleanFilenamePart(src.Prefix);
            }
            v.OtherPrefix = Util.CleanFilenamePart(v.OtherPrefix);
            v.AfterSuccess = C(v.AfterSuccess, "move", "delete", "keep", "move");
            v.MoveOriginalsTo = P(v.MoveOriginalsTo);
            v.Retries = I(v.Retries, 0, 20);
            v.Codec = C(v.Codec, "h264", "h264", "hevc");
            v.Rotate = C(v.Rotate, "counterclockwise", "counterclockwise", "clockwise");
            v.Width = I(v.Width, 0, 8192);
            v.Height = I(v.Height, 0, 8192);
            v.Fps = D(v.Fps, 0, 240);
            v.Bitrate = C(v.Bitrate, "source", "source", "fixed", "quality");
            v.FixedMbps = D(v.FixedMbps, 0.1, 500);
            v.Quality = I(v.Quality, 1, 51);
            v.FallbackMbps = D(v.FallbackMbps, 0.1, 500);
            v.Preset = C(v.Preset, "balanced", "fast", "balanced", "quality");
            v.Audio = C(v.Audio, "copy", "copy", "aac", "none");
            v.AudioKbps = I(v.AudioKbps, 32, 512);
            v.ExtraInputArgs = T(v.ExtraInputArgs);
            v.ExtraOutputArgs = T(v.ExtraOutputArgs);
            v.Encoder = C(v.Encoder, "auto", "auto", "intel_qsv", "nvidia_nvenc", "amd_amf", "cpu");
            v.GpuIndex = I(v.GpuIndex, -1, 16);
            v.Priority = C(v.Priority, "below_normal", "normal", "below_normal", "idle");

            GpioSettings g = s.Gpio ?? (s.Gpio = new GpioSettings());
            if (g.Devices == null) g.Devices = new List<AxiaDevice>();
            if (g.Vmix == null) g.Vmix = new List<VmixTarget>();
            if (g.Triggers == null) g.Triggers = new List<GpioTrigger>();
            g.Devices.RemoveAll(delegate(AxiaDevice x) { return x == null; });
            g.Vmix.RemoveAll(delegate(VmixTarget x) { return x == null; });
            g.Triggers.RemoveAll(delegate(GpioTrigger x) { return x == null; });
            foreach (AxiaDevice d in g.Devices)
            {
                d.Id = UniqueId(d.Id, seen);
                d.Name = T(d.Name).Length > 0 ? T(d.Name) : "Axia device";
                d.Host = T(d.Host);
                d.Port = I(d.Port, 1, 65535);
                d.Password = d.Password ?? "";
            }
            foreach (VmixTarget t in g.Vmix)
            {
                t.Id = UniqueId(t.Id, seen);
                t.Name = T(t.Name).Length > 0 ? T(t.Name) : "vMix";
                t.Host = T(t.Host).Length > 0 ? T(t.Host) : "127.0.0.1";
                t.Port = I(t.Port, 1, 65535);
                t.User = T(t.User);
                t.Password = t.Password ?? "";
            }
            foreach (GpioTrigger t in g.Triggers)
            {
                t.Id = UniqueId(t.Id, seen);
                t.Name = T(t.Name).Length > 0 ? T(t.Name) : "Trigger";
                t.DeviceId = T(t.DeviceId);
                t.Signal = C(t.Signal, "gpi", "gpi", "gpo");
                t.Port = I(t.Port, 1, 9999);
                t.Pin = I(t.Pin, 1, 5);
                t.IgnoreRepeatMs = I(t.IgnoreRepeatMs, 0, 600000);
                if (t.WhenOn == null) t.WhenOn = new List<GpioAction>();
                if (t.WhenOff == null) t.WhenOff = new List<GpioAction>();
                foreach (List<GpioAction> list in new[] { t.WhenOn, t.WhenOff })
                {
                    list.RemoveAll(delegate(GpioAction x) { return x == null; });
                    foreach (GpioAction a in list)
                    {
                        a.Type = C(a.Type, "vmix", "vmix", "http");
                        a.VmixId = T(a.VmixId);
                        a.Function = T(a.Function);
                        a.Input = T(a.Input);
                        a.Value = a.Value ?? "";
                        a.Extra = T(a.Extra);
                        a.Method = C(a.Method, "GET", "get", "post", "put").ToUpperInvariant();
                        a.Url = T(a.Url);
                        a.Body = a.Body ?? "";
                        a.ContentType = T(a.ContentType).Length > 0 ? T(a.ContentType) : "application/json";
                        a.DelayMs = I(a.DelayMs, 0, 600000);
                    }
                }
            }
            g.DisconnectAlertSeconds = I(g.DisconnectAlertSeconds, 5, 86400);

            AlertSettings al = s.Alerts ?? (s.Alerts = new AlertSettings());
            if (al.Destinations == null) al.Destinations = new List<AlertDestination>();
            al.Destinations.RemoveAll(delegate(AlertDestination x) { return x == null; });
            foreach (AlertDestination d in al.Destinations)
            {
                d.Id = UniqueId(d.Id, seen);
                d.Name = T(d.Name).Length > 0 ? T(d.Name) : "Alerts";
                d.Type = C(d.Type, "email", "email", "jira", "webhook");
                d.MinLevel = C(d.MinLevel, "warning", "warning", "error");
                d.RepeatMinutes = I(d.RepeatMinutes, 0, 10080);
                d.SmtpHost = T(d.SmtpHost);
                d.SmtpPort = I(d.SmtpPort, 1, 65535);
                d.SmtpSecurity = C(d.SmtpSecurity, "starttls", "starttls", "ssl", "none");
                d.SmtpUser = T(d.SmtpUser);
                d.From = T(d.From);
                d.To = T(d.To);
                d.JiraUrl = T(d.JiraUrl).TrimEnd('/');
                d.JiraUser = T(d.JiraUser);
                d.JiraProject = T(d.JiraProject);
                d.JiraIssueType = T(d.JiraIssueType).Length > 0 ? T(d.JiraIssueType) : "Task";
                d.JiraLabels = T(d.JiraLabels);
                d.Url = T(d.Url);
                d.Body = d.Body ?? "";
                d.SmtpPassword = d.SmtpPassword ?? "";
                d.JiraToken = d.JiraToken ?? "";
                d.Notes = d.Notes ?? "";
            }
            return idsMade;
        }
    }
}
