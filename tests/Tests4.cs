// Configuration and folder policies.
using System;
using System.Collections.Generic;
using System.IO;
using Watchkeeper;

namespace Watchkeeper.Tests
{
    public class ConfigTests : Env
    {
        [Fact]
        public void Starter_config_moves_from_the_cache_drive_and_is_switched_off()
        {
            AppConfig c = Cfg.Starter();
            Assert.Eq(new[] { "Move ISO  T: > D:", "Move PGM1  T: > D:", "Producer cam sort  T: > D:",
                              "Retention D:\\ISO (7 days)", "Retention D:\\PGM1 (7 days)", "Retention D:\\Vertical (7 days)",
                              "Retention D:\\Producer_Cam (7 days)" },
                      c.Folders.ConvertAll(delegate(FolderPolicy f) { return f.Name; }).ToArray());
            // the move policies: every minute, cache drive (T:) to data drive (D:), every finished file
            for (int i = 0; i < 3; i++)
            {
                FolderPolicy m = c.Folders[i];
                Assert.True(m.Path.StartsWith(@"T:\"), m.Name);
                Assert.Eq("interval", m.Schedule.Mode);
                Assert.Eq(1.0, m.Schedule.IntervalMinutes);
                Assert.Eq("move", m.Rules[m.Rules.Count - 1].Action);
                Assert.Eq("*", m.Rules[m.Rules.Count - 1].Pattern);
                Assert.False(m.Rules[m.Rules.Count - 1].UseWindow);
            }
            FolderPolicy iso = c.Folders[0];
            Assert.Eq(@"T:\ISO", iso.Path);
            Assert.Eq(@"D:\ISO", iso.Destination);
            Assert.Eq("", iso.Rules[0].Destination);
            Assert.Eq(@"D:\PGM1", c.Folders[1].Destination);
            FolderPolicy cam = c.Folders[2];
            Assert.Eq(@"T:\Producer_Cam", cam.Path);
            Assert.Eq(@"D:\ISO", cam.Destination);
            Assert.Eq(3, cam.Rules.Count);
            Assert.Eq("move", cam.Rules[0].Action);
            Assert.Eq("05:45", cam.Rules[0].WindowStart);
            Assert.Eq("09:00", cam.Rules[0].WindowEnd);
            Assert.Eq("filename", cam.Rules[0].WindowSource);
            Assert.Eq("delete", cam.Rules[1].Action);
            Assert.Eq("outside", cam.Rules[1].WindowMode);
            Assert.Eq(@"D:\Producer_Cam", cam.Rules[2].Destination);
            // retention: data drive only, sub-folders included
            for (int i = 3; i < 7; i++)
            {
                FolderPolicy r = c.Folders[i];
                Assert.True(r.Path.StartsWith(@"D:\"), r.Name);
                Assert.True(r.Subfolders, r.Name);
                Assert.Eq("delete", r.Rules[0].Action);
                Assert.Eq(7.0, r.Rules[0].OlderThan);
                Assert.Eq("days", r.Rules[0].OlderThanUnit);
            }
            foreach (FolderPolicy f in c.Folders)
                foreach (FileRule r in f.Rules)
                    if (r.Action == "delete" && r.OlderThan > 0) Assert.False(f.Path.StartsWith(@"T:\"), "age-based delete on the cache: " + f.Name);
            Assert.Eq(@"T:\Vertical", c.Vertical.WatchDir);
            Assert.Eq(@"D:\Vertical", c.Vertical.OutDir);
            Assert.Eq(60, c.Vertical.PollSeconds);
            Assert.Eq("move", c.Vertical.AfterSuccess);
            Assert.Eq(@"D:\Vertical\Originals", c.Vertical.MoveOriginalTo);
            Assert.Eq(new[] { "Virtual Video Director", "vMix" }, c.Programs.ConvertAll(delegate(ProgramJob p) { return p.Name; }).ToArray());
            Assert.Eq("high", c.General.ServicePriority);
            Assert.Eq("user_fallback", c.Vertical.Gpu.Session);
            Assert.False(c.Vertical.Enabled);
            foreach (FolderPolicy f in c.Folders) Assert.False(f.Enabled);
            foreach (ProgramJob p in c.Programs) Assert.False(p.Enabled);
            Assert.Eq(0, Cfg.ValidateAll(c).Count);
        }

        [Fact]
        public void Policy_mode_is_derived_from_the_rules()
        {
            AppConfig c = Cfg.Starter();
            Assert.Eq(new[] { "move", "move", "custom", "delete", "delete", "delete", "delete" },
                      c.Folders.ConvertAll(delegate(FolderPolicy f) { return PolicyMode.Of(f); }).ToArray());

            FolderPolicy p = new FolderPolicy();
            p.Path = @"T:\X";
            p.Destination = @"D:\X";
            Assert.Eq("custom", PolicyMode.Of(p));                     // no rules
            PolicyMode.Apply(p, "move", "", 0, null, "rename");
            Assert.Eq("move", PolicyMode.Of(p));
            FileRule r = p.Rules[0];
            Assert.Eq("*", r.Pattern);
            Assert.Eq(0.0, r.OlderThan);
            Assert.Eq("rename", r.OnConflict);
            Assert.Eq("Move every file", r.Name);
            Assert.Eq(0, Cfg.ValidateFolder(p, "p").Count);
            r.Id = "abc";

            PolicyMode.Apply(p, "delete", "*.mp4", 14.6, "days", null);
            Assert.Eq(1, p.Rules.Count);
            Assert.Eq("abc", p.Rules[0].Id);                           // same rule, new settings
            Assert.Eq("delete", PolicyMode.Of(p));
            Assert.Eq(14.0, p.Rules[0].OlderThan);
            Assert.Eq("Delete *.mp4 after 14 days", p.Rules[0].Name);

            PolicyMode.Apply(p, "copy", "*", 0, null, "skip");
            Assert.Eq("copy", PolicyMode.Of(p));
            Assert.Eq(0.0, p.Rules[0].OlderThan);

            p.Rules[0].OlderThan = 2;                                  // copy with an age: needs the rule editor
            Assert.Eq("custom", PolicyMode.Of(p));
            p.Rules[0].OlderThan = 0;
            p.Rules[0].UseWindow = true;
            Assert.Eq("custom", PolicyMode.Of(p));
            p.Rules[0].UseWindow = false;
            p.Rules[0].Destination = @"D:\Elsewhere";
            Assert.Eq("custom", PolicyMode.Of(p));
            p.Rules[0].Destination = "";
            p.Rules.Add(new FileRule());
            Assert.Eq("custom", PolicyMode.Of(p));
            PolicyMode.Apply(p, "custom", "*", 0, null, null);         // custom leaves the rules alone
            Assert.Eq(2, p.Rules.Count);
        }

        [Fact]
        public void Move_policy_without_an_age_moves_every_finished_file()
        {
            string src = D("cache", "A"), dst = D("data", "A");
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src, "one.mp4"), "1");
            File.WriteAllText(Path.Combine(src, "two.txt"), "2");
            File.SetLastWriteTimeUtc(Path.Combine(src, "one.mp4"), DateTime.UtcNow.AddMinutes(-5));
            File.SetLastWriteTimeUtc(Path.Combine(src, "two.txt"), DateTime.UtcNow.AddMinutes(-5));
            FolderPolicy p = new FolderPolicy();
            p.Name = "A";
            p.Path = src;
            p.Destination = dst;
            p.MinAgeSeconds = 60;
            PolicyMode.Apply(p, "move", "*", 0, null, "rename");
            Cfg.NormalizeFolder(p, new List<string>());
            TaskResult res = FolderRunner.Run(p, Cfgs.Get().General, new Cap().Log, false, null, 0, null);
            Assert.True(res.Ok, res.Text);
            Assert.Eq(2, res.Moved);
            Assert.Eq(0, Directory.GetFiles(src).Length);
            Assert.True(File.Exists(Path.Combine(dst, "one.mp4")));
        }

        [Fact]
        public void Vertical_move_needs_a_folder_other_than_the_ingest_folder()
        {
            AppConfig c = Cfg.Starter();
            c.Vertical.AfterSuccess = "move";
            c.Vertical.MoveOriginalTo = "";
            Assert.True(Cfg.ValidateAll(c).Exists(delegate(string e) { return e.Contains("move originals to: folder is required"); }));
            c.Vertical.MoveOriginalTo = c.Vertical.WatchDir;
            Assert.True(Cfg.ValidateAll(c).Exists(delegate(string e) { return e.Contains("other than the ingest folder"); }));
            c.Vertical.AfterSuccess = "delete";
            Assert.Eq(0, Cfg.ValidateAll(c).Count);
        }

        [Fact]
        public void Move_rule_needs_a_rule_or_policy_destination()
        {
            FolderPolicy f = new FolderPolicy();
            f.Name = "F";
            f.Path = @"E:\ISO";
            FileRule r = new FileRule();
            r.Action = "move";
            f.Rules.Add(r);
            Assert.True(Cfg.ValidateFolder(f, "f").Exists(delegate(string e) { return e.Contains("destination: folder is required"); }));
            f.Destination = @"D:\ISO";
            Assert.Eq(0, Cfg.ValidateFolder(f, "f").Count);
            Assert.Eq(@"D:\ISO", Cfg.RuleDestination(f, r));
            r.Destination = @"D:\Other";
            Assert.Eq(@"D:\Other", Cfg.RuleDestination(f, r));
            f.Destination = @"E:\ISO";
            r.Destination = "";
            Assert.True(Cfg.ValidateFolder(f, "f").Exists(delegate(string e) { return e.Contains("different folder from the source"); }));
        }

        [Fact]
        public void Normalize_coerces_and_clamps()
        {
            AppConfig c = Cfg.Parse(@"{
              ""general"": {""service_priority"": ""realtime""},
              ""folders"": [{""path"": ""X:\\a"", ""min_age_seconds"": -4, ""schedule"": {""mode"": ""weekly"", ""day"": ""funday"", ""time"": ""25:00""},
                             ""rules"": [{""action"": ""explode"", ""older_than"": -5, ""older_than_unit"": ""years"", ""pattern"": """",
                                          ""window_start"": ""99:99"", ""window_mode"": ""sideways"", ""keep_newest"": -1}]}],
              ""programs"": [{""run_as"": ""admin"", ""restart_delay_seconds"": 0, ""priority"": ""realtime""}],
              ""scripts"": [{""trigger"": ""sometimes"", ""ok_exit_codes"": """", ""priority"": ""turbo""}],
              ""alerts"": {""destinations"": [{""type"": ""pager"", ""format"": ""fax"", ""smtp_port"": 99999}], ""rules"": [{""min_severity"": ""panic"", ""destinations"": [""nowhere""]}]},
              ""vertical"": {""video"": {""quality"": 999, ""fps"": -3, ""codec"": ""vp9""},
                             ""gpu"": {""backend"": ""voodoo"", ""priority"": ""turbo"", ""session"": ""moon""}}}");
            Assert.Eq("high", c.General.ServicePriority);
            FolderPolicy f = c.Folders[0];
            Assert.Eq(0, f.MinAgeSeconds);
            Assert.Eq("weekly", f.Schedule.Mode);
            Assert.Eq("sunday", f.Schedule.Day);
            Assert.Eq("03:00", f.Schedule.Time);
            FileRule r = f.Rules[0];
            Assert.Eq("delete", r.Action);
            Assert.Eq(0.0, r.OlderThan);
            Assert.Eq("days", r.OlderThanUnit);
            Assert.Eq("*", r.Pattern);
            Assert.Eq("00:00", r.WindowStart);
            Assert.Eq("inside", r.WindowMode);
            Assert.Eq(0, r.KeepNewest);
            Assert.True(f.Id.Length > 0 && r.Id.Length > 0);
            Assert.Eq("user", c.Programs[0].RunAs);
            Assert.Eq(1, c.Programs[0].RestartDelaySeconds);
            Assert.Eq("normal", c.Programs[0].Priority);
            Assert.Eq("schedule", c.Scripts[0].Trigger);
            Assert.Eq("0", c.Scripts[0].OkExitCodes);
            Assert.Eq("normal", c.Scripts[0].Priority);
            Assert.Eq("eventlog", c.Alerts.Destinations[0].Type);
            Assert.Eq("text", c.Alerts.Destinations[0].Format);
            Assert.Eq(65535, c.Alerts.Destinations[0].SmtpPort);
            Assert.Eq("warning", c.Alerts.Rules[0].MinSeverity);
            Assert.Eq(0, c.Alerts.Rules[0].Destinations.Count);      // unknown destination dropped
            Assert.Eq(51, c.Vertical.Video.Quality);
            Assert.Eq(0.0, c.Vertical.Video.Fps);
            Assert.Eq("h264", c.Vertical.Video.Codec);
            Assert.Eq("intel_qsv", c.Vertical.Gpu.Backend);
            Assert.Eq("below_normal", c.Vertical.Gpu.Priority);
            Assert.Eq("user_fallback", c.Vertical.Gpu.Session);
        }

        [Fact]
        public void Duplicate_ids_are_made_unique()
        {
            AppConfig c = Cfg.Parse(@"{""folders"": [{""id"": ""a"", ""path"": ""X:\\a""}, {""id"": ""a"", ""path"": ""X:\\b""}],
                                     ""programs"": [{""id"": ""p""}, {""id"": ""p""}]}");
            Assert.True(c.Folders[0].Id != c.Folders[1].Id);
            Assert.True(c.Programs[0].Id != c.Programs[1].Id);
        }

        static FolderPolicy Folder(string path)
        {
            FolderPolicy f = new FolderPolicy();
            f.Name = "F";
            f.Path = path;
            return f;
        }

        [Fact]
        public void Validation_errors()
        {
            AppConfig c = Cfgs.Get();
            c.Folders.Add(Folder(""));
            Assert.True(Cfgs.SaveSection("folders", c.Folders).Exists(delegate(string e) { return e.Contains("folder is required"); }));

            c = Cfgs.Get();
            c.Folders.Add(Folder(@"C:\Windows\Temp"));
            Assert.True(Cfgs.SaveSection("folders", c.Folders).Exists(delegate(string e) { return e.Contains("refusing"); }));

            c = Cfgs.Get();
            FolderPolicy f = Folder(D("a"));
            FileRule move = new FileRule();
            move.Action = "move";
            f.Rules.Add(move);
            c.Folders.Add(f);
            Assert.True(Cfgs.SaveSection("folders", c.Folders).Exists(delegate(string e) { return e.Contains("destination: folder is required"); }));
            move.Destination = D("a");
            Assert.True(Cfgs.SaveSection("folders", c.Folders).Exists(delegate(string e) { return e.Contains("different folder"); }));
            move.Destination = D("b");
            move.MinSizeMb = 10;
            move.MaxSizeMb = 5;
            Assert.True(Cfgs.SaveSection("folders", c.Folders).Exists(delegate(string e) { return e.Contains("minimum size"); }));

            c = Cfgs.Get();
            c.Programs.Add(new ProgramJob());
            Assert.True(Cfgs.SaveSection("programs", c.Programs).Exists(delegate(string e) { return e.Contains("program file is required"); }));

            c = Cfgs.Get();
            ScriptJob s = new ScriptJob();
            s.Trigger = "file";
            s.OkExitCodes = "0,x";
            c.Scripts.Add(s);
            List<string> errs = Cfgs.SaveSection("scripts", c.Scripts);
            Assert.True(errs.Exists(delegate(string e) { return e.Contains("script or program is required"); }));
            Assert.True(errs.Exists(delegate(string e) { return e.Contains("watched folder: folder is required"); }));
            Assert.True(errs.Exists(delegate(string e) { return e.Contains("'x' is not a number"); }));

            c = Cfgs.Get();
            AlertDestination mail = new AlertDestination();
            mail.Type = "email";
            mail.Name = "Ops mail";
            AlertDestination hook = new AlertDestination();
            hook.Type = "webhook";
            hook.Url = "ftp://x";
            AlertDestination jira = new AlertDestination();
            jira.Type = "jira";
            c.Alerts.Destinations.AddRange(new[] { mail, hook, jira });
            AlertRule empty = new AlertRule();
            empty.Name = "Nowhere";
            c.Alerts.Rules.Add(empty);
            errs = Cfgs.SaveSection("alerts", c.Alerts);
            Assert.True(errs.Exists(delegate(string e) { return e.Contains("SMTP server"); }));
            Assert.True(errs.Exists(delegate(string e) { return e.Contains("recipient"); }));
            Assert.True(errs.Exists(delegate(string e) { return e.Contains("webhook address"); }));
            Assert.True(errs.Exists(delegate(string e) { return e.Contains("project key"); }));
            Assert.True(errs.Exists(delegate(string e) { return e.Contains("Nowhere") && e.Contains("at least one destination"); }));

            c = Cfgs.Get();
            c.Vertical.Video.Width = 721;
            Assert.True(Cfgs.SaveSection("vertical", c.Vertical).Exists(delegate(string e) { return e.Contains("even"); }));
        }

        [Fact]
        public void Invalid_save_does_not_change_stored_config()
        {
            string before = File.ReadAllText(D("config.json"));
            AppConfig c = Cfgs.Get();
            c.Folders[0].Path = @"C:\";
            Assert.True(Cfgs.SaveSection("folders", c.Folders).Count > 0);
            Assert.Eq(before, File.ReadAllText(D("config.json")));
            Assert.Eq(@"T:\ISO", Cfgs.Get().Folders[0].Path);
        }

        [Fact]
        public void Save_persists_reloads_and_keeps_a_backup()
        {
            Set<List<FolderPolicy>>("folders", delegate(List<FolderPolicy> l) { l[0].Name = "Changed"; l.RemoveAt(1); });
            ConfigStore again = new ConfigStore(D("config.json"), null);
            again.Load();
            Assert.Eq("Changed", again.Get().Folders[0].Name);
            Assert.Eq(6, again.Get().Folders.Count);
            Assert.True(File.Exists(D("config.json.bak")));
            string text = File.ReadAllText(D("config.json"));
            Assert.Contains(text, "\"older_than_unit\"");
            Assert.Contains(text, "\"service_priority\"");
            Assert.Contains(text, "\"version\": 4");
        }

        [Fact]
        public void Unreadable_file_falls_back_to_backup_and_is_not_overwritten()
        {
            Set<GeneralCfg>("general", delegate(GeneralCfg g) { g.ServerLabel = "Studio A"; });    // creates .bak of the previous file
            Set<GeneralCfg>("general", delegate(GeneralCfg g) { g.ServerLabel = "Studio B"; });    // .bak now has Studio A
            File.WriteAllText(D("config.json"), "{ not json");
            ConfigStore again = new ConfigStore(D("config.json"), null);
            again.Load();
            Assert.Eq("Studio A", again.Get().General.ServerLabel);
            Assert.Eq("{ not json", File.ReadAllText(D("config.json")));
            Assert.True(again.Problems.Count > 0);
        }

        [Fact]
        public void Missing_file_writes_the_starter_config()
        {
            ConfigStore s = new ConfigStore(D("new", "config.json"), null);
            s.Load(true);
            Assert.True(File.Exists(D("new", "config.json")));
            Assert.Eq(7, s.Get().Folders.Count);
        }

        [Fact]
        public void External_edit_is_picked_up_and_broken_edit_is_rejected()
        {
            List<string> seen = new List<string>();
            Cfgs.Changed += delegate(string n) { seen.Add(n); };
            Assert.Null(Cfgs.CheckForChanges());
            AppConfig c = Cfgs.Get();
            c.General.ServerLabel = "Edited by hand";
            System.Threading.Thread.Sleep(20);
            File.WriteAllText(D("config.json"), Cfg.ToText(c) + "\n\n");
            File.SetLastWriteTimeUtc(D("config.json"), DateTime.UtcNow.AddSeconds(5));
            List<string> problems = Cfgs.CheckForChanges();
            Assert.NotNull(problems);
            Assert.Eq(0, problems.Count);
            Assert.Eq("Edited by hand", Cfgs.Get().General.ServerLabel);
            Assert.Eq(new[] { "*" }, seen.ToArray());

            File.WriteAllText(D("config.json"), "{ broken");
            File.SetLastWriteTimeUtc(D("config.json"), DateTime.UtcNow.AddSeconds(10));
            problems = Cfgs.CheckForChanges();
            Assert.Eq(1, problems.Count);
            Assert.Contains(problems[0], "keeping the previous settings");
            Assert.Eq("Edited by hand", Cfgs.Get().General.ServerLabel);
            Assert.Eq(1, seen.Count);
        }

        [Fact]
        public void Save_all_imports_a_whole_config()
        {
            AppConfig c = Cfg.Starter();
            c.Folders.RemoveAt(0);
            c.General.ServerLabel = "Imported";
            Assert.Eq(0, Cfgs.SaveAll(c).Count);
            Assert.Eq("Imported", Cfgs.Get().General.ServerLabel);
            Assert.Eq(6, Cfgs.Get().Folders.Count);
            c.Folders[0].Path = "";
            Assert.True(Cfgs.SaveAll(c).Count > 0);
            Assert.Eq(6, Cfgs.Get().Folders.Count);
        }

        [Fact]
        public void Listeners_fire_only_after_successful_saves()
        {
            List<string> seen = new List<string>();
            Cfgs.Changed += delegate(string name) { seen.Add(name); };
            Set<AlertCfg>("alerts", delegate(AlertCfg a) { a.Rules[0].CooldownMinutes = 5; });
            AppConfig c = Cfgs.Get();
            c.Folders[0].Path = "";
            Cfgs.SaveSection("folders", c.Folders);
            Assert.Eq(new[] { "alerts" }, seen.ToArray());
        }

        [Fact]
        public void Unknown_section_and_wrong_type_are_rejected()
        {
            Assert.True(Cfgs.SaveSection("nope", new GeneralCfg()).Count > 0);
            Assert.True(Cfgs.SaveSection("folders", new GeneralCfg()).Count > 0);
        }

        [Fact]
        public void Get_returns_an_independent_copy()
        {
            AppConfig c = Cfgs.Get();
            c.Folders[0].Name = "x";
            Assert.Eq(Cfg.Starter().Folders[0].Name, Cfgs.Get().Folders[0].Name);
        }
    }

    public class FolderTests : Env
    {
        string src, dst;
        GeneralCfg general;

        public FolderTests()
        {
            src = D("Producer_Cam");
            dst = D("ISO");
            Directory.CreateDirectory(src);
            general = Cfgs.Get().General;
        }

        static string Rec(string time) { return "MultiCorder2 - Cam2 - 23 July 2026 - " + time + ".mp4"; }

        string Put(string name) { return Put(src, name, 600, 3600); }
        string Put(string dir, string name, double duration, double ageSeconds)
        {
            string p = Path.Combine(dir, name);
            WriteMedia(p, duration);
            Age(p, ageSeconds);
            return p;
        }

        static string Touch(string dir, string name, double ageDays, int bytes)
        {
            Directory.CreateDirectory(dir);
            string p = Path.Combine(dir, name);
            File.WriteAllText(p, new string('x', bytes));
            Age(p, ageDays * 86400);
            return p;
        }

        FolderPolicy Cam()
        {
            FolderPolicy f = Templates.TimeWindowSort(src, dst, "05:45", "09:00", new List<string> { ".mp4", ".avi", ".mkv", ".mov" }, "delete", 0, true);
            Cfg.NormalizeFolder(f, new List<string>());
            return f;
        }

        static FolderPolicy Policy(string path, params FileRule[] rules)
        {
            FolderPolicy f = new FolderPolicy();
            f.Name = "P";
            f.Path = path;
            f.MinAgeSeconds = 0;
            f.Rules.AddRange(rules);
            Cfg.NormalizeFolder(f, new List<string>());
            return f;
        }

        static FileRule Rule(string action, double olderDays, string pattern)
        {
            FileRule r = new FileRule();
            r.Name = action + " " + pattern;
            r.Action = action;
            r.OlderThan = olderDays;
            r.Pattern = pattern;
            return r;
        }

        TaskResult Run(FolderPolicy f, bool dry, Cap cap) { return FolderRunner.Run(f, general, cap.Log, dry, null, 0, null); }
        TaskResult Run(FolderPolicy f) { return Run(f, false, new Cap()); }

        static string[] Names(string dir)
        {
            if (!Directory.Exists(dir)) return new string[0];
            string[] files = Directory.GetFiles(dir);
            for (int i = 0; i < files.Length; i++) files[i] = Path.GetFileName(files[i]);
            Array.Sort(files, StringComparer.Ordinal);
            return files;
        }

        // ------------------------------------------------ producer-cam sort (window from the file name)
        [Fact]
        public void Window_boundaries_and_actions()
        {
            Put(Rec("08-29-03 AM"));
            Put(Rec("05-45-00 AM"));      // start boundary inclusive
            Put(Rec("09-00-00 AM"));      // end boundary inclusive
            Put(Rec("05-44-59 AM"));      // just before -> delete
            Put(Rec("09-00-01 AM"));      // just after  -> delete
            Put(Rec("12-10-00 PM"));      // delete
            TaskResult res = Run(Cam());
            string[] expected = { Rec("08-29-03 AM"), Rec("05-45-00 AM"), Rec("09-00-00 AM") };
            Array.Sort(expected, StringComparer.Ordinal);
            Assert.Eq(expected, Names(dst));
            Assert.Eq(0, Directory.GetFiles(src).Length);
            Assert.Eq(3, res.Moved);
            Assert.Eq(3, res.Deleted);
            Assert.True(res.Ok);
        }

        [Fact]
        public void Starter_producer_cam_policy_empties_the_cache_folder()
        {
            FolderPolicy f = null;
            foreach (FolderPolicy x in Cfg.Starter().Folders) if (x.Path == Cfg.CacheDrive + "Producer_Cam") f = x;
            string other = Path.Combine(dst, "Producer_Cam");
            f.Path = src;
            f.Destination = dst;
            f.Rules[2].Destination = other;
            f.MinAgeSeconds = 0;
            Put(Rec("08-29-03 AM"));          // inside the window -> ISO
            Put(Rec("12-10-00 PM"));          // outside -> deleted
            Put("Producer cam no time.mp4");   // no recording time -> other folder
            Put("notes.txt");                  // other type -> other folder
            Cap cap = new Cap();
            TaskResult res = Run(f, false, cap);
            Assert.Eq(0, Directory.GetFiles(src).Length, string.Join(" | ", Names(src)) + " // " + string.Join(" | ", Names(other)));
            Assert.True(File.Exists(Path.Combine(dst, Rec("08-29-03 AM"))));
            Assert.Eq(new[] { "Producer cam no time.mp4", "notes.txt" }, Names(other));
            Assert.Eq(3, res.Moved);
            Assert.Eq(1, res.Deleted);
        }

        [Fact]
        public void Modified_time_is_start_plus_duration()
        {
            Put(Rec("08-29-03 AM"));
            Run(Cam());
            Assert.Eq(new DateTime(2026, 7, 23, 8, 39, 3), File.GetLastWriteTime(Path.Combine(dst, Rec("08-29-03 AM"))));
        }

        [Fact]
        public void Modified_time_falls_back_to_start_without_ffprobe()
        {
            general.FfprobePath = D("missing-ffprobe");
            Put(Rec("08-29-03 AM"));
            Cap cap = new Cap();
            Run(Cam(), false, cap);
            Assert.Eq(new DateTime(2026, 7, 23, 8, 29, 3), File.GetLastWriteTime(Path.Combine(dst, Rec("08-29-03 AM"))));
            Assert.Contains(cap.Text(), "duration unavailable");
        }

        [Fact]
        public void Unparseable_names_are_left_alone()
        {
            Put("random.mp4");
            Put("MultiCorder2 - Cam2 - 31 Foo 2026 - 08-00-00 AM.mp4");
            Put(Rec("13-00-00 PM"));
            Put("MultiCorder2 - Cam2 - 31 February 2026 - 08-00-00 AM.mp4");
            File.WriteAllText(Path.Combine(src, "notes.txt"), "x");
            Age(Path.Combine(src, "notes.txt"), 3600);
            TaskResult res = Run(Cam());
            Assert.Eq(5, Directory.GetFiles(src).Length);
            Assert.Eq(0, res.Deleted + res.Moved);
            Assert.Eq(5, res.Untouched);
        }

        [Fact]
        public void Twelve_hour_clock_edges()
        {
            Assert.Eq(new DateTime(2026, 7, 23, 0, 30, 0), FileOps.ParseRecordingTime(Rec("12-30-00 AM")).Value);
            Assert.Eq(new DateTime(2026, 7, 23, 12, 30, 0), FileOps.ParseRecordingTime(Rec("12-30-00 PM")).Value);
            Assert.Eq(new DateTime(2026, 7, 23, 13, 0, 0), FileOps.ParseRecordingTime(Rec("01-00-00 PM")).Value);
            Assert.Eq(new DateTime(2026, 9, 5, 13, 2, 3), FileOps.ParseRecordingTime("x - y - 5 Sep 2026 - 01-02-03 pm.mp4").Value);
            Assert.False(FileOps.ParseRecordingTime("random.mp4").HasValue);
        }

        [Fact]
        public void Dry_run_changes_nothing()
        {
            Put(Rec("08-29-03 AM"));
            Put(Rec("12-10-00 PM"));
            Cap cap = new Cap();
            TaskResult res = Run(Cam(), true, cap);
            Assert.Eq(2, Directory.GetFiles(src).Length);
            Assert.False(Directory.Exists(dst));
            Assert.Contains(cap.Text(), "would move");
            Assert.Contains(cap.Text(), "would delete");
            Assert.Eq(1, res.Moved);
            Assert.Eq(1, res.Deleted);
        }

        [Fact]
        public void Recently_modified_files_are_left_alone()
        {
            FolderPolicy f = Cam();
            f.MinAgeSeconds = 120;
            string p = Path.Combine(src, Rec("08-29-03 AM"));
            WriteMedia(p);    // modified now: still being recorded
            Cap cap = new Cap();
            TaskResult res = Run(f, false, cap);
            Assert.True(File.Exists(p));
            Assert.Eq(1, res.Skipped);
            Assert.Contains(cap.Text(), "may still be written");
        }

        [Fact]
        public void Window_wrapping_midnight()
        {
            FolderPolicy f = Cam();
            f.Rules[0].WindowStart = f.Rules[1].WindowStart = "22:00";
            f.Rules[0].WindowEnd = f.Rules[1].WindowEnd = "02:00";
            Put(Rec("11-30-00 PM"));
            Put(Rec("01-00-00 AM"));
            Put(Rec("08-00-00 AM"));
            TaskResult res = Run(f);
            Assert.Eq(2, res.Moved);
            Assert.Eq(1, res.Deleted);
        }

        [Fact]
        public void Existing_destination_is_overwritten_by_default()
        {
            Directory.CreateDirectory(dst);
            File.WriteAllText(Path.Combine(dst, Rec("08-29-03 AM")), "old");
            Put(Rec("08-29-03 AM"));
            TaskResult res = Run(Cam());
            Assert.Eq(1, res.Moved);
            Assert.True(File.ReadAllText(Path.Combine(dst, Rec("08-29-03 AM"))) != "old");
        }

        [Fact]
        public void Missing_and_protected_folders_are_reported()
        {
            TaskResult res = Run(Policy(D("nope"), Rule("delete", 1, "*")));
            Assert.False(res.Ok);
            Assert.Contains(res.Text, "does not exist");
            res = Run(Policy("/etc", Rule("delete", 1, "*")));
            Assert.False(res.Ok);
            Assert.Contains(res.Text, "refused");
        }

        // ------------------------------------------------ retention (age by Date Modified)
        [Fact]
        public void Deletes_only_files_older_than_cutoff()
        {
            string iso = D("iso");
            Touch(iso, "old.mp4", 8, 10);
            Touch(iso, "edge-old.mp4", 7.01, 10);
            Touch(iso, "new.mp4", 6.9, 10);
            TaskResult res = Run(Policy(iso, Rule("delete", 7, "*")));
            Assert.Eq(new[] { "new.mp4" }, Names(iso));
            Assert.Eq(2, res.Deleted);
            Assert.Eq(20L, res.Bytes);
            Assert.True(res.Ok);
        }

        [Fact]
        public void Age_units()
        {
            string dir = D("u");
            Touch(dir, "two-hours.log", 2.0 / 24, 1);
            Touch(dir, "ten-minutes.log", 10.0 / 1440, 1);
            FileRule r = Rule("delete", 90, "*");
            r.OlderThanUnit = "minutes";
            Run(Policy(dir, r));
            Assert.Eq(new[] { "ten-minutes.log" }, Names(dir));
        }

        [Fact]
        public void Patterns_excludes_and_regex_characters()
        {
            string dir = D("p");
            Touch(dir, "a.mp4", 10, 1);
            Touch(dir, "B.MP4", 10, 1);
            Touch(dir, "a.mkv", 10, 1);
            Touch(dir, "a.log", 10, 1);
            Touch(dir, "keep-me.mp4", 10, 1);
            Touch(dir, "a(1).mp4", 10, 1);
            FileRule r = Rule("delete", 7, "*.mp4;*.mkv");
            r.Exclude = "keep-*;a(?).mp4";
            Run(Policy(dir, r));
            Assert.Eq(new[] { "a(1).mp4", "a.log", "keep-me.mp4" }, Names(dir));
        }

        [Fact]
        public void First_matching_rule_wins_and_keep_protects()
        {
            string dir = D("f");
            Touch(dir, "important.mp4", 30, 1);
            Touch(dir, "clip.mp4", 30, 1);
            Touch(dir, "clip.mov", 30, 1);
            FileRule keep = Rule("keep", 0, "important*");
            FileRule move = Rule("move", 0, "*.mov");
            move.Destination = D("archive");
            FileRule del = Rule("delete", 7, "*");
            TaskResult res = Run(Policy(dir, keep, move, del));
            Assert.Eq(new[] { "important.mp4" }, Names(dir));
            Assert.Eq(new[] { "clip.mov" }, Names(D("archive")));
            Assert.Eq(1, res.Kept);
            Assert.Eq(1, res.Moved);
            Assert.Eq(1, res.Deleted);
        }

        [Fact]
        public void Disabled_rule_is_skipped()
        {
            string dir = D("d");
            Touch(dir, "a.mp4", 30, 1);
            FileRule r = Rule("delete", 1, "*");
            r.Enabled = false;
            TaskResult res = Run(Policy(dir, r));
            Assert.Eq(1, Names(dir).Length);
            Assert.Eq(1, res.Untouched);
        }

        [Fact]
        public void Keep_newest_n()
        {
            string dir = D("k");
            for (int i = 1; i <= 5; i++) Touch(dir, "f" + i + ".mp4", i, 1);    // f1 is the newest
            FileRule r = Rule("delete", 0, "*.mp4");
            r.KeepNewest = 2;
            Run(Policy(dir, r));
            Assert.Eq(new[] { "f1.mp4", "f2.mp4" }, Names(dir));
        }

        [Fact]
        public void Size_limits()
        {
            string dir = D("s");
            Touch(dir, "small.bin", 1, 100);
            Touch(dir, "big.bin", 1, 3 * 1048576);
            FileRule r = Rule("delete", 0, "*");
            r.MinSizeMb = 1;
            Run(Policy(dir, r));
            Assert.Eq(new[] { "small.bin" }, Names(dir));
            r.MinSizeMb = 0;
            r.MaxSizeMb = 0.001;
            Touch(dir, "big2.bin", 1, 3 * 1048576);
            Run(Policy(dir, r));
            Assert.Eq(new[] { "big2.bin" }, Names(dir));
        }

        [Fact]
        public void Modified_time_window_inside_and_outside()
        {
            string dir = D("w");
            string morning = Touch(dir, "morning.mp4", 1, 1);
            string evening = Touch(dir, "evening.mp4", 1, 1);
            DateTime y = DateTime.Today.AddDays(-1);
            File.SetLastWriteTime(morning, y.AddHours(8));
            File.SetLastWriteTime(evening, y.AddHours(20));
            FileRule move = Rule("move", 0, "*");
            move.Destination = D("day");
            move.UseWindow = true;
            move.WindowStart = "06:00";
            move.WindowEnd = "18:00";
            FileRule del = Rule("delete", 0, "*");
            del.UseWindow = true;
            del.WindowStart = "06:00";
            del.WindowEnd = "18:00";
            del.WindowMode = "outside";
            TaskResult res = Run(Policy(dir, move, del));
            Assert.Eq(new[] { "morning.mp4" }, Names(D("day")));
            Assert.Eq(0, Names(dir).Length);
            Assert.Eq(1, res.Moved);
            Assert.Eq(1, res.Deleted);
        }

        [Fact]
        public void Subfolders_copy_with_structure_rename_and_cleanup()
        {
            string dir = D("tree");
            Touch(Path.Combine(dir, "a"), "x.mp4", 3, 1);
            Touch(Path.Combine(dir, "a", "b"), "y.mp4", 3, 1);
            Touch(dir, "z.mp4", 3, 1);
            Directory.CreateDirectory(Path.Combine(dir, "empty", "deeper"));
            FileRule copy = Rule("copy", 1, "*.mp4");
            copy.Destination = D("backup");
            FolderPolicy f = Policy(dir, copy);
            f.Subfolders = true;
            f.RemoveEmptyFolders = true;
            TaskResult res = Run(f);
            Assert.Eq(3, res.Copied);
            Assert.True(File.Exists(D("backup", "a", "x.mp4")));
            Assert.True(File.Exists(D("backup", "a", "b", "y.mp4")));
            Assert.True(File.Exists(D("backup", "z.mp4")));
            Assert.True(File.Exists(Path.Combine(dir, "a", "b", "y.mp4")));    // copy keeps the original
            Assert.False(Directory.Exists(Path.Combine(dir, "empty")));
            Assert.True(Directory.Exists(Path.Combine(dir, "a")));

            // second run: names collide
            copy.OnConflict = "rename";
            Run(f);
            Assert.True(File.Exists(D("backup", "z (1).mp4")));
            copy.OnConflict = "skip";
            res = Run(f);
            Assert.Eq(3, res.Skipped);
            Assert.False(File.Exists(D("backup", "z (2).mp4")));
        }

        [Fact]
        public void Flat_copy_and_destination_inside_the_source_is_not_reprocessed()
        {
            string dir = D("flat");
            Touch(Path.Combine(dir, "sub"), "x.mp4", 3, 1);
            FileRule move = Rule("move", 1, "*.mp4");
            move.Destination = Path.Combine(dir, "done");
            move.KeepSubfolders = false;
            FolderPolicy f = Policy(dir, move);
            f.Subfolders = true;
            TaskResult res = Run(f);
            Assert.Eq(1, res.Moved);
            Assert.True(File.Exists(Path.Combine(dir, "done", "x.mp4")));
            res = Run(f);
            Assert.Eq(0, res.Moved);
        }

        [Fact]
        public void Protected_destination_ignores_the_rule()
        {
            string dir = D("pd");
            Touch(dir, "x.mp4", 3, 1);
            FileRule move = Rule("move", 0, "*");
            move.Destination = "/etc";
            TaskResult res = Run(Policy(dir, move));
            Assert.False(res.Ok);
            Assert.Eq(1, res.RuleErrors);
            Assert.True(File.Exists(Path.Combine(dir, "x.mp4")));
        }

        [Fact]
        public void Policy_destination_is_used_and_moves_between_drives_are_verified()
        {
            string cache = D("cache", "ISO"), data = D("data", "ISO");
            Touch(cache, "a.mp4", 1, 1000);
            Touch(cache, "b.mp4", 1, 2000);
            DateTime when = new DateTime(2026, 7, 23, 8, 39, 3, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(Path.Combine(cache, "b.mp4"), when);
            FolderPolicy f = Policy(cache, Rule("move", 0, "*"));
            f.Destination = data;
            FileOps.ForceCrossVolume = true;
            try
            {
                TaskResult res = Run(f);
                Assert.Eq(2, res.Moved);
                Assert.Eq(new[] { "a.mp4", "b.mp4" }, Names(data));
                Assert.Eq(0, Names(cache).Length);
                Assert.Eq(2000L, new FileInfo(Path.Combine(data, "b.mp4")).Length);
                Assert.Eq(0, Directory.GetFiles(data, "*.copying").Length);
                Assert.Eq(when, File.GetLastWriteTimeUtc(Path.Combine(data, "b.mp4")));    // Date Modified kept
            }
            finally { FileOps.ForceCrossVolume = false; }
        }

        [Fact]
        public void File_open_for_writing_is_skipped()
        {
            string cache = D("rec");
            Touch(cache, "recording.mp4", 1, 10);
            using (FileStream w = new FileStream(Path.Combine(cache, "recording.mp4"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                if (!FileOps.InUse(Path.Combine(cache, "recording.mp4"))) throw new SkipException("this runtime does not report sharing conflicts");
                FolderPolicy f = Policy(cache, Rule("move", 0, "*"));
                f.Destination = D("out");
                TaskResult res = Run(f);
                Assert.Eq(1, res.Skipped);
                Assert.Eq(0, res.Moved);
            }
            Assert.True(File.Exists(Path.Combine(cache, "recording.mp4")));
        }

        [Fact]
        public void Preview_plan_lists_every_file()
        {
            string dir = D("pv");
            Touch(dir, "old.mp4", 10, 5);
            Touch(dir, "new.mp4", 1, 5);
            List<PlanItem> plan = new List<PlanItem>();
            TaskResult res = FolderRunner.Run(Policy(dir, Rule("delete", 7, "*")), general, new Cap().Log, true, plan, 100, null);
            Assert.Eq(2, plan.Count);
            Assert.Eq("none", plan[0].Action);     // new.mp4 sorts first
            Assert.Eq("delete", plan[1].Action);
            Assert.Eq("old.mp4", plan[1].File);
            Assert.Eq(1, res.Deleted);
            Assert.Eq(2, Names(dir).Length);
        }

        [Fact]
        public void Describe_rule()
        {
            FileRule r = Rule("move", 7, "*.mp4");
            r.Destination = @"E:\Archive";
            r.UseWindow = true;
            r.WindowStart = "05:45";
            r.WindowEnd = "09:00";
            Assert.Eq(@"Move *.mp4 older than 7 days, modified between 05:45-09:00 to E:\Archive", FolderRunner.Describe(r));
            Assert.Eq("Delete all files older than 1 day", FolderRunner.Describe(Rule("delete", 1, "*")));
        }
    }
}
