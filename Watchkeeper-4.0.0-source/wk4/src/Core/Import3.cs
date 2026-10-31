// Converts a version 3 settings file to version 4. Everything that has a place in version 4 is carried over; what has
// not (scripts, studios, size and time-of-day filters, ...) is listed in the report so nothing is lost silently.
using System;
using System.Collections.Generic;
using System.IO;

namespace Watchkeeper
{
    public static class Import3
    {
        static JObject Obj(JObject o, string key) { return o == null ? null : o.Get(key) as JObject; }
        static List<object> Arr(JObject o, string key) { List<object> a = o == null ? null : o.Get(key) as List<object>; return a ?? new List<object>(); }
        static string S(JObject o, string key) { return o == null ? "" : o.Str(key, ""); }
        static string S(JObject o, string key, string def) { return o == null ? def : o.Str(key, def); }
        static bool B(JObject o, string key, bool def) { return o == null ? def : o.Bool(key, def); }
        static double N(JObject o, string key, double def) { return o == null ? def : o.Num(key, def); }
        static int I(JObject o, string key, int def) { return (int)Math.Round(N(o, key, def)); }

        static string Secret(JObject o, string key, string what, List<string> report)
        {
            string v = S(o, key);
            if (v.Length == 0) return "";
            string plain = Secrets.RevealVersion3(v);
            if (plain == null)
            {
                report.Add("NOT CARRIED OVER: " + what + " (it was encrypted in a way only the version 3 service could read): type it again.");
                return "";
            }
            return plain;
        }

        public static Settings Convert(JObject root, List<string> report)
        {
            Settings s = new Settings();
            JObject general = Obj(root, "general");
            JObject modules = Obj(general, "modules");
            s.General.ServerName = S(general, "server_label");
            if (S(general, "ffmpeg_path").Length > 0) s.General.FfmpegPath = S(general, "ffmpeg_path");
            s.General.FfprobePath = S(general, "ffprobe_path");
            if (B(general, "dry_run", false)) report.Add("Dry-run mode was on in version 3; version 4 has no dry-run mode. Everything imported is switched off until you switch it on (see below).");
            bool dry = B(general, "dry_run", false);

            ImportPrograms(root, modules, dry, s, report);
            ImportFolders(root, modules, dry, s, report);
            ImportVertical(root, modules, dry, s, report);
            ImportGpio(root, modules, dry, s, report);
            ImportAlerts(root, s, report);

            int scripts = Arr(root, "scripts").Count;
            if (scripts > 0) report.Add("NOT CARRIED OVER: " + scripts + " script(s): version 4 has no Scripts part.");
            JObject studios = Obj(root, "studios");
            if (studios != null && (Arr(studios, "studios").Count > 0 || B(Obj(studios, "pathfinder"), "enabled", false)))
                report.Add("NOT CARRIED OVER: the Studios / Pathfinder settings: version 4 has no Studios part.");
            report.Insert(0, "Converted version 3 settings on " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", Util.Inv) + ": " +
                             s.Watchdog.Count + " watchdog entries, " + s.Folders.Count + " folder tasks, " + s.Gpio.Triggers.Count + " GPIO triggers, " +
                             s.Alerts.Destinations.Count + " alert destinations.");
            return s;
        }

        static bool ModuleOn(JObject modules, string name) { return modules == null || B(modules, name, true); }

        // ---------------------------------------------------------------- watchdog
        static void ImportPrograms(JObject root, JObject modules, bool dry, Settings s, List<string> report)
        {
            bool on = ModuleOn(modules, "programs");
            if (!on && Arr(root, "programs").Count > 0) report.Add("The Watchdog part was switched off: its entries are imported switched off.");
            foreach (object item in Arr(root, "programs"))
            {
                JObject o = item as JObject;
                if (o == null) continue;
                ProgramEntry p = new ProgramEntry();
                p.Id = S(o, "id");
                p.Name = S(o, "name", "Program");
                string w = "Watchdog '" + p.Name + "': ";
                p.Enabled = B(o, "enabled", false) && on && !dry;
                p.Kind = S(o, "kind", "program") == "service" ? "service" : "program";
                p.ServiceName = S(o, "service_name");
                p.Path = S(o, "exe_path");
                p.Arguments = S(o, "arguments");
                p.StartIn = S(o, "working_dir");
                p.WatchPath = S(o, "watch_path");
                string proc = S(o, "process_name");
                string stem = Path.GetFileNameWithoutExtension(p.Path.Replace('\\', '/').Substring(p.Path.Replace('\\', '/').LastIndexOf('/') + 1));
                if (proc.Length > 0 && p.WatchPath.Length == 0 && p.Kind == "program" && !string.Equals(proc, stem, StringComparison.OrdinalIgnoreCase))
                    report.Add(w + "it looked for the process '" + proc + "', which is not the program it starts. Set 'Application to watch' (start it, then use Choose from running programs).");
                else if (proc.Length > 0 && p.Kind == "service")
                    report.Add(w + "the process name '" + proc + "' is not carried over; set 'Application to watch' if the service starts an application.");
                switch (S(o, "running_check", "auto"))
                {
                    case "watched": p.RunningMeans = "app"; break;
                    case "started": p.RunningMeans = "started"; break;
                    case "either": p.RunningMeans = "either"; break;
                    case "both": p.RunningMeans = "both"; break;
                    default: p.RunningMeans = "auto"; break;
                }
                p.LauncherWaitSeconds = I(o, "launcher_wait_seconds", 60);
                p.RunIn = S(o, "run_as", "user") == "service" ? "background" : "desktop";
                p.Elevated = B(o, "allow_elevation", false);
                p.StartDelaySeconds = I(o, "start_delay_seconds", 20);
                p.RestartDelaySeconds = I(o, "restart_delay_seconds", 10);
                p.MaxRestartsPerHour = I(o, "max_restarts_per_hour", 10);
                JObject restart = Obj(o, "restart");
                string mode = S(restart, "mode", "manual");
                if (mode == "daily") p.RestartAt = S(restart, "time", "04:00");
                else if (mode == "weekly") { p.RestartAt = S(restart, "time", "04:00"); p.RestartDays.Add(S(restart, "day", "sunday")); }
                else if (mode == "interval") report.Add(w + "the restart every " + N(restart, "interval_minutes", 0) + " minutes is not carried over (version 4 restarts at a time of day).");
                p.Priority = S(o, "priority", "normal");
                if (B(o, "health_enabled", false))
                {
                    p.HealthUrl = S(o, "health_url");
                    p.HealthUser = S(o, "health_user");
                    p.HealthPassword = Secret(o, "health_password", w + "health check password", report);
                }
                else if (S(o, "health_url").Length > 0) report.Add(w + "the switched-off health check (" + S(o, "health_url") + ") is not carried over.");
                p.HealthEverySeconds = I(o, "health_interval_seconds", 15);
                p.HealthFailSeconds = I(o, "health_fail_seconds", 60);
                p.HealthGraceSeconds = I(o, "health_grace_seconds", 90);
                p.HealthAction = S(o, "health_action", "alert");
                p.Notes = S(o, "notes");
                if (Arr(o, "studios").Count > 0) report.Add(w + "it belonged to a studio; it now runs all the time when switched on.");
                s.Watchdog.Add(p);
            }
        }

        // ---------------------------------------------------------------- folders
        static void ImportFolders(JObject root, JObject modules, bool dry, Settings s, List<string> report)
        {
            bool on = ModuleOn(modules, "folders");
            if (!on && Arr(root, "folders").Count > 0) report.Add("The Folder policies part was switched off: its tasks are imported switched off.");
            foreach (object item in Arr(root, "folders"))
            {
                JObject o = item as JObject;
                if (o == null) continue;
                string pname = S(o, "name", "Folder");
                string w = "Folder policy '" + pname + "': ";
                List<object> rules = Arr(o, "rules");
                int made = 0;
                if (rules.Count > 1)
                    report.Add(w + "its " + rules.Count + " rules became separate tasks that run one after the other; check that no task takes files another one should handle.");
                JObject schedule = Obj(o, "schedule");
                foreach (object ri in rules)
                {
                    JObject r = ri as JObject;
                    if (r == null) continue;
                    string rname = S(r, "name", "Rule");
                    string action = S(r, "action", "delete");
                    if (action == "keep")
                    {
                        report.Add(w + "rule '" + rname + "' (keep) is not carried over: it protected files from the rules after it. Add its pattern to 'Exclude' of the tasks that must not touch those files.");
                        continue;
                    }
                    FolderTask f = new FolderTask();
                    f.Name = rules.Count > 1 ? pname + " - " + rname : pname;
                    f.Enabled = B(o, "enabled", false) && B(r, "enabled", true) && on && !dry;
                    f.Source = S(o, "path");
                    f.Subfolders = B(o, "subfolders", false);
                    f.SettleSeconds = I(o, "min_age_seconds", 60);
                    f.RemoveEmptyFolders = B(o, "remove_empty_folders", false);
                    f.Action = action == "move" || action == "copy" ? action : "delete";
                    f.Destination = S(r, "destination").Length > 0 ? S(r, "destination") : S(o, "destination");
                    f.KeepStructure = B(r, "keep_subfolders", true);
                    f.IfExists = S(r, "on_conflict", "rename");
                    f.Include = S(r, "pattern", "*");
                    f.Exclude = S(r, "exclude");
                    double older = N(r, "older_than", 0);
                    string unit = S(r, "older_than_unit", "days");
                    if (unit == "weeks") { older *= 7; unit = "days"; }
                    f.OlderThan = older;
                    f.OlderThanUnit = unit;
                    f.KeepNewest = I(r, "keep_newest", 0);
                    string mode = S(schedule, "mode", "daily");
                    if (mode == "interval") { f.Run = "every"; f.IntervalMinutes = N(schedule, "interval_minutes", 60); }
                    else if (mode == "daily") { f.Run = "daily"; f.At = S(schedule, "time", "03:00"); }
                    else if (mode == "weekly") { f.Run = "daily"; f.At = S(schedule, "time", "03:00"); report.Add(w + "it ran weekly; the task now runs daily at " + f.At + "."); }
                    else f.Run = "manual";
                    if (N(r, "min_size_mb", 0) > 0 || N(r, "max_size_mb", 0) > 0) report.Add(w + "rule '" + rname + "': the file size limits are not carried over.");
                    if (B(r, "use_window", false)) report.Add(w + "rule '" + rname + "': the time-of-day window is not carried over.");
                    if (B(r, "stamp_from_name", false)) report.Add(w + "rule '" + rname + "': setting the date from the file name is not carried over.");
                    f.Notes = S(o, "notes");
                    s.Folders.Add(f);
                    made++;
                }
                if (made == 0 && rules.Count == 0) report.Add(w + "it had no rules; nothing imported.");
            }
        }

        // ---------------------------------------------------------------- vertical
        static void ImportVertical(JObject root, JObject modules, bool dry, Settings s, List<string> report)
        {
            JObject v = Obj(root, "vertical");
            if (v == null) return;
            VerticalSettings t = s.Vertical;
            t.Enabled = B(v, "enabled", false) && ModuleOn(modules, "vertical") && !dry;
            t.WatchFolder = S(v, "watch_dir");
            t.OutputFolder = S(v, "out_dir");
            List<string> ext = new List<string>();
            foreach (object e in Arr(v, "extensions")) if (e is string) ext.Add((string)e);
            if (ext.Count > 0) t.Extensions = ext;
            t.AfterSuccess = S(v, "after_success", "delete");
            t.MoveOriginalsTo = S(v, "move_original_to");
            t.Retries = I(v, "max_retries", 3);
            foreach (object si in Arr(v, "sources"))
            {
                JObject so = si as JObject;
                if (so == null) continue;
                VerticalSource src = new VerticalSource();
                src.Name = S(so, "name", "Source");
                src.Match = S(so, "match");
                src.Prefix = S(so, "prefix", "Vertical");
                src.Enabled = B(so, "enabled", true);
                t.Sources.Add(src);
            }
            JObject other = Obj(v, "other");
            t.ConvertOthers = B(other, "enabled", true);
            t.OtherPrefix = S(other, "prefix", "Vertical");
            JObject video = Obj(v, "video");
            t.Codec = S(video, "codec", "h264");
            t.Rotate = S(video, "rotation", "cclock") == "clock" ? "clockwise" : "counterclockwise";
            if (S(video, "resolution_mode", "source") == "custom") { t.Width = I(video, "width", 0); t.Height = I(video, "height", 0); }
            t.Fps = N(video, "fps", 0);
            switch (S(video, "rate_mode", "match_source"))
            {
                case "fixed": t.Bitrate = "fixed"; break;
                case "quality": t.Bitrate = "quality"; break;
                case "percent":
                    t.Bitrate = "source";
                    if (Math.Abs(N(video, "percent", 100) - 100) > 0.01) report.Add("Vertical conversion: the bitrate of " + N(video, "percent", 100) + "% of the source is now 100% of the source (no percentage in version 4).");
                    break;
                default: t.Bitrate = "source"; break;
            }
            t.FixedMbps = N(video, "fixed_mbps", 8);
            t.Quality = I(video, "quality", 23);
            t.FallbackMbps = N(video, "fallback_mbps", 8);
            t.Preset = S(video, "preset", "balanced");
            t.Audio = S(video, "audio_mode", "copy");
            t.AudioKbps = I(video, "audio_kbps", 192);
            t.ExtraInputArgs = S(video, "extra_input_args");
            t.ExtraOutputArgs = S(video, "extra_output_args");
            JObject gpu = Obj(v, "gpu");
            t.Encoder = S(gpu, "backend", "auto");
            t.GpuIndex = I(gpu, "adapter_index", -1);
            t.HardwareDecode = B(gpu, "hw_decode", true);
            t.CpuFallback = B(gpu, "fallback_to_cpu", false);
            string session = S(gpu, "session", "user_fallback");
            t.UserSessionFallback = session != "service";
            if (session == "user") report.Add("Vertical conversion: ffmpeg ran only in the signed-in session; version 4 tries the service first, then the signed-in session.");
            t.Priority = S(gpu, "priority", "below_normal");
        }

        // ---------------------------------------------------------------- GPIO
        static void ImportGpio(JObject root, JObject modules, bool dry, Settings s, List<string> report)
        {
            JObject g = Obj(root, "gpio");
            if (g == null) return;
            GpioSettings t = s.Gpio;
            t.Enabled = B(g, "enabled", false) && ModuleOn(modules, "gpio") && !dry;
            t.DisconnectAlertSeconds = I(g, "disconnect_alert_seconds", 60);
            foreach (object di in Arr(g, "devices"))
            {
                JObject d = di as JObject;
                if (d == null) continue;
                AxiaDevice x = new AxiaDevice();
                x.Id = S(d, "id");
                x.Name = S(d, "name", "Axia device");
                x.Enabled = B(d, "enabled", true);
                x.Host = S(d, "host");
                x.Port = I(d, "port", 93);
                x.Password = Secret(d, "password", "GPIO device '" + x.Name + "' password", report);
                if (B(d, "login", false) && x.Password.Length == 0)
                    report.Add("GPIO device '" + x.Name + "': it logged in without a password; version 4 logs in only when a password is set.");
                t.Devices.Add(x);
            }
            foreach (object vi in Arr(g, "vmix"))
            {
                JObject d = vi as JObject;
                if (d == null) continue;
                VmixTarget x = new VmixTarget();
                x.Id = S(d, "id");
                x.Name = S(d, "name", "vMix");
                x.Host = S(d, "host", "127.0.0.1");
                x.Port = I(d, "port", 8088);
                x.User = S(d, "user");
                x.Password = Secret(d, "password", "vMix '" + x.Name + "' password", report);
                t.Vmix.Add(x);
            }
            foreach (object ti in Arr(g, "triggers"))
            {
                JObject o = ti as JObject;
                if (o == null) continue;
                string name = S(o, "name", "Trigger");
                string signal = S(o, "signal", "gpi");
                if (signal == "cmd")
                {
                    report.Add("NOT CARRIED OVER: GPIO trigger '" + name + "' reacted to a command message; version 4 reacts to GPI and GPO pins only.");
                    continue;
                }
                GpioTrigger x = new GpioTrigger();
                x.Id = S(o, "id");
                x.Name = name;
                x.Enabled = B(o, "enabled", true);
                x.DeviceId = S(o, "device_id");
                x.Signal = signal;
                x.Port = I(o, "port", 1);
                x.Pin = I(o, "pin", 1);
                x.Invert = B(o, "invert", false);
                x.IgnoreRepeatMs = I(o, "ignore_repeat_ms", 0);
                x.WhenOn = Actions(Arr(o, "on_active"), "GPIO trigger '" + name + "'", report);
                x.WhenOff = Actions(Arr(o, "on_inactive"), "GPIO trigger '" + name + "'", report);
                t.Triggers.Add(x);
            }
        }

        static List<GpioAction> Actions(List<object> list, string w, List<string> report)
        {
            List<GpioAction> o = new List<GpioAction>();
            foreach (object ai in list)
            {
                JObject a = ai as JObject;
                if (a == null) continue;
                string type = S(a, "type", "vmix");
                if (type == "studio") { report.Add("NOT CARRIED OVER: " + w + ": its studio switch action (no Studios part in version 4)."); continue; }
                GpioAction x = new GpioAction();
                x.Type = type == "http" ? "http" : "vmix";
                x.VmixId = S(a, "vmix_id");
                x.Function = S(a, "function", "Cut");
                x.Input = S(a, "input");
                x.Value = S(a, "value");
                List<string> extra = new List<string>();
                if (S(a, "duration").Length > 0) extra.Add("Duration=" + S(a, "duration"));
                if (S(a, "selected_name").Length > 0) extra.Add("SelectedName=" + S(a, "selected_name"));
                if (S(a, "mix").Length > 0) extra.Add("Mix=" + S(a, "mix"));
                if (S(a, "extra").Length > 0) extra.Add(S(a, "extra"));
                x.Extra = string.Join("&", extra.ToArray());
                x.Method = S(a, "method", "GET");
                x.Url = S(a, "url");
                x.Body = S(a, "body");
                x.ContentType = S(a, "content_type", "application/json");
                x.DelayMs = I(a, "delay_ms", 0);
                if (x.Type == "http" && S(a, "user").Length > 0) report.Add(w + ": the login of its web request is not carried over; put it in the address's own scheme if the device accepts one.");
                o.Add(x);
            }
            return o;
        }

        // ---------------------------------------------------------------- alerts
        static void ImportAlerts(JObject root, Settings s, List<string> report)
        {
            JObject a = Obj(root, "alerts");
            if (a == null) return;
            List<JObject> rules = new List<JObject>();
            foreach (object ri in Arr(a, "rules")) if (ri is JObject && B((JObject)ri, "enabled", true)) rules.Add((JObject)ri);
            s.Alerts.EventLog = false;
            foreach (object di in Arr(a, "destinations"))
            {
                JObject d = di as JObject;
                if (d == null) continue;
                string id = S(d, "id");
                string name = S(d, "name", "Alerts");
                string type = S(d, "type", "eventlog");
                List<JObject> mine = rules.FindAll(delegate(JObject r)
                {
                    foreach (object x in Arr(r, "destinations")) if (x as string == id) return true;
                    return false;
                });
                if (type == "eventlog")
                {
                    s.Alerts.EventLog = B(d, "enabled", true) && mine.Count > 0;
                    continue;
                }
                AlertDestination x2 = new AlertDestination();
                x2.Id = id;
                x2.Name = name;
                x2.Enabled = B(d, "enabled", true) && mine.Count > 0;
                string w = "Alert destination '" + name + "': ";
                if (B(d, "enabled", true) && mine.Count == 0) report.Add(w + "no rule sent alerts to it; it is imported switched off.");
                // what it receives: the union of the rules that send to it
                x2.Watchdog = x2.Folders = x2.Vertical = x2.Gpio = x2.Service = mine.Count == 0;
                string minLevel = "error";
                bool recoveries = false;
                int repeat = -1;
                foreach (JObject r in mine)
                {
                    if (B(r, "all_parts", true)) x2.Watchdog = x2.Folders = x2.Vertical = x2.Gpio = x2.Service = true;
                    else foreach (object pi in Arr(r, "parts"))
                    {
                        switch (pi as string)
                        {
                            case "programs": x2.Watchdog = true; break;
                            case "folders": x2.Folders = true; break;
                            case "vertical": x2.Vertical = true; break;
                            case "gpio": x2.Gpio = true; break;
                            case "service": case "config": x2.Service = true; break;
                        }
                    }
                    if (Arr(r, "items").Count > 0) report.Add(w + "rule '" + S(r, "name") + "' was limited to some entries; it now covers its whole part.");
                    string sev = S(r, "min_severity", "warning");
                    if (sev != "error") minLevel = "warning";
                    if (B(r, "recoveries", true)) recoveries = true;
                    int cd = I(r, "cooldown_minutes", 30);
                    repeat = repeat < 0 ? cd : Math.Min(repeat, cd);
                }
                x2.MinLevel = mine.Count == 0 ? "warning" : minLevel;
                x2.Recoveries = mine.Count == 0 || recoveries;
                x2.RepeatMinutes = repeat < 0 ? 30 : repeat;
                if (type == "email")
                {
                    x2.Type = "email";
                    x2.SmtpHost = S(d, "smtp_host");
                    x2.SmtpPort = I(d, "smtp_port", 587);
                    string tls = S(d, "smtp_tls", "starttls");
                    x2.SmtpSecurity = tls == "implicit" ? "ssl" : tls == "none" ? "none" : "starttls";
                    x2.SmtpUser = S(d, "smtp_user");
                    x2.SmtpPassword = Secret(d, "smtp_password", w + "mail server password", report);
                    x2.From = S(d, "email_from");
                    x2.To = S(d, "email_to");
                    if (S(d, "smtp_trust_fingerprint").Length > 0) report.Add(w + "the trusted certificate fingerprint is not carried over: the mail server's certificate must now be valid.");
                }
                else if (type == "jira")
                {
                    x2.Type = "jira";
                    x2.JiraUrl = S(d, "jira_url");
                    x2.JiraUser = S(d, "jira_auth", "basic") == "bearer" ? "" : S(d, "jira_user");
                    x2.JiraToken = Secret(d, "jira_token", w + "Jira token", report);
                    x2.JiraProject = S(d, "jira_project");
                    x2.JiraIssueType = S(d, "jira_issue_type", "Task");
                    x2.JiraLabels = S(d, "jira_labels", "watchkeeper");
                    if (S(d, "jira_priority").Length > 0) report.Add(w + "the issue priority is not carried over (the project's default is used).");
                }
                else
                {
                    x2.Type = "webhook";
                    x2.Url = Secret(d, "url", w + "webhook address", report);
                    string format = S(d, "format", "text");
                    if (format == "discord") x2.Body = "{\"content\": \"{title}\\n{message}\"}";
                    else if (format == "json") x2.Body = "{\"title\": \"{title}\", \"message\": \"{message}\", \"level\": \"{level}\", \"part\": \"{part}\", \"server\": \"{server}\", \"time\": \"{time}\"}";
                    else if (format == "custom" || format == "jira_automation")
                    {
                        string tpl = S(d, "template");
                        x2.Body = tpl.Replace("{subject}", "{title}").Replace("{body}", "{message}").Replace("{severity}", "{level}")
                                     .Replace("{kind}", "{level}").Replace("{item}", "{part}");
                        if (format == "jira_automation") report.Add(w + "it called a Jira automation webhook; its secret is not carried over (version 4 sends no extra headers). Use a Jira destination instead.");
                    }
                    if (S(d, "headers").Length > 0) report.Add(w + "the extra request headers are not carried over.");
                    if (S(d, "proxy", "none") != "none") report.Add(w + "the proxy setting is not carried over (version 4 connects directly).");
                }
                x2.Notes = S(d, "notes");
                s.Alerts.Destinations.Add(x2);
            }
        }
    }
}
