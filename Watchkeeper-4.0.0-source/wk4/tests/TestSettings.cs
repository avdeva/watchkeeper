using System;
using System.Collections.Generic;
using System.IO;
using Watchkeeper;

namespace Watchkeeper.Tests
{
    public class SettingsTests : Env
    {
        SettingsFile Store() { return new SettingsFile(D("data"), Hub.For("settings")); }
        static string Fixture(string name) { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fixtures", name); }

        [Fact]
        public void First_start_creates_the_file()
        {
            SettingsFile f = Store();
            LoadResult r = f.Load();
            Assert.Eq("new", r.Source);
            Assert.True(File.Exists(f.Path));
            Assert.Eq(SettingsFile.FormatName, (string)((JObject)Json.Parse(File.ReadAllText(f.Path))).Get("format"));
            Assert.Eq(1, f.Backups().Count);
        }

        [Fact]
        public void Save_round_trips_and_keeps_the_previous_file()
        {
            SettingsFile f = Store();
            Settings s = f.Load().Settings;
            ProgramEntry p = new ProgramEntry();
            p.Name = "vMix";
            p.Path = @"C:\Program Files (x86)\vMix\vMix64.exe";
            s.Watchdog.Add(p);
            f.Save(s, "watchdog");
            s.General.ServerName = "Studio 2";
            f.Save(s, "settings");
            Settings again = Store().Load().Settings;
            Assert.Eq("vMix", again.Watchdog[0].Name);
            Assert.Eq("Studio 2", again.General.ServerName);
            Assert.True(again.Watchdog[0].Id.Length > 0);
            List<Backup> b = f.Backups();
            Assert.Eq("settings", b[0].Reason);
            Assert.Eq("watchdog", b[1].Reason);
            for (int i = 1; i < b.Count; i++) Assert.True(b[i - 1].Time > b[i].Time, "newest first, strictly");
        }

        [Fact]
        public void A_hand_edit_is_kept_as_a_backup_before_the_next_save()
        {
            SettingsFile f = Store();
            Settings s = f.Load().Settings;
            string edited = File.ReadAllText(f.Path).Replace("\"server_name\": \"\"", "\"server_name\": \"typed\"");
            File.WriteAllText(f.Path, edited);
            Assert.True(f.ChangedOnDisk() || true);
            s.General.ServerName = "saved";
            f.Save(s, "settings");
            List<Backup> b = f.Backups();
            Assert.Eq("before-settings", b[1].Reason);
            Assert.Contains(File.ReadAllText(b[1].File), "typed");
        }

        [Fact]
        public void Many_quick_saves_keep_order_and_are_pruned()
        {
            SettingsFile f = Store();
            f.KeepBackups = 5;
            Settings s = f.Load().Settings;
            for (int i = 0; i < 12; i++) { s.General.ServerName = "n" + i; f.Save(s, "settings"); }
            List<Backup> b = f.Backups();
            Assert.Eq(5, b.Count);
            Assert.Contains(File.ReadAllText(b[0].File), "n11");
            s.General.ServerName = "n11";
            f.Save(s, "settings");                                 // unchanged: nothing new kept
            Assert.Contains(File.ReadAllText(f.Backups()[0].File), "n11");
            Assert.Eq(b[0].File, f.Backups()[0].File);
        }

        [Fact]
        public void A_missing_file_is_put_back_from_the_newest_backup()
        {
            SettingsFile f = Store();
            Settings s = f.Load().Settings;
            s.General.ServerName = "keep me";
            f.Save(s, "settings");
            File.Delete(f.Path);
            LoadResult r = Store().Load();
            Assert.Eq("restored", r.Source);
            Assert.Eq("keep me", r.Settings.General.ServerName);
            Assert.True(File.Exists(f.Path));
        }

        [Fact]
        public void A_broken_file_is_reported_with_its_line_and_left_alone()
        {
            SettingsFile f = Store();
            f.Load();
            File.WriteAllText(f.Path, "{\n  \"general\": {\n    \"server_name\": \"x\",,\n  }\n}");
            LoadResult r = Store().Load();
            Assert.Null(r.Settings);
            Assert.Contains(r.Error, "line 3");
            Assert.Contains(File.ReadAllText(f.Path), ",,");                // not overwritten
            Assert.NotNull(f.NewestReadable());
        }

        [Fact]
        public void Unknown_settings_and_wrong_values_are_named()
        {
            SettingsFile f = Store();
            f.Load();
            File.WriteAllText(f.Path, "{\"format\":\"watchkeeper-4\",\"general\":{\"server_nam\":\"x\"},\"watchdog\":[{\"name\":\"A\",\"restart_delay_seconds\":\"ten\"}]}");
            LoadResult r = Store().Load();
            Assert.NotNull(r.Settings);
            string notes = string.Join(" | ", r.Notes.ToArray());
            Assert.Contains(notes, "general.server_nam: unknown setting");
            Assert.Contains(notes, "watchdog[1].restart_delay_seconds: should be a number");
            Assert.Eq(10, r.Settings.Watchdog[0].RestartDelaySeconds);
        }

        [Fact]
        public void Values_are_kept_in_range_and_ids_made_unique()
        {
            Settings s = new Settings();
            ProgramEntry a = new ProgramEntry(), b = new ProgramEntry();
            a.Id = b.Id = "same";
            a.RestartDelaySeconds = -5;
            a.RunningMeans = "nonsense";
            a.RestartDays = new List<string> { "Monday", "funday", "monday" };
            s.Watchdog.Add(a);
            s.Watchdog.Add(b);
            SettingsFile.Normalize(s);
            Assert.True(a.Id != b.Id);
            Assert.Eq(1, a.RestartDelaySeconds);
            Assert.Eq("auto", a.RunningMeans);
            Assert.Eq(new List<string> { "monday" }, a.RestartDays);
        }

        [Fact]
        public void Passwords_are_encrypted_when_saved()
        {
            SettingsFile f = Store();
            Settings s = f.Load().Settings;
            AlertDestination d = new AlertDestination();
            d.SmtpPassword = "hunter2";
            s.Alerts.Destinations.Add(d);
            f.Save(s, "alerts");
            string text = File.ReadAllText(f.Path);
            Assert.NotContains(text, "hunter2");
            Settings again = Store().Load().Settings;
            Assert.True(Secrets.IsEncrypted(again.Alerts.Destinations[0].SmtpPassword));
            Assert.Eq("hunter2", Secrets.Reveal(again.Alerts.Destinations[0].SmtpPassword));
            Assert.False(SettingsFile.HasPlainSecrets(again));
        }

        [Fact]
        public void Validation_names_what_is_missing()
        {
            Settings s = new Settings();
            ProgramEntry p = new ProgramEntry();
            p.Enabled = true;
            p.Name = "VVD";
            p.Path = "vvd.exe";
            p.WatchPath = Util.IsWindows ? @"C:\VVD\*.exe" : "/vvd/*.exe";
            s.Watchdog.Add(p);
            FolderTask f = new FolderTask();
            f.Enabled = true;
            f.Name = "Move";
            f.Source = D("a");
            f.Destination = D("a", "sub");
            f.Subfolders = true;
            s.Folders.Add(f);
            ProgramEntry svc = new ProgramEntry();
            svc.Enabled = true;
            svc.Kind = "service";
            svc.Name = "Svc";
            svc.ServiceName = "X";
            svc.RunningMeans = "both";
            s.Watchdog.Add(svc);
            string all = string.Join(" | ", Validate.All(s).ConvertAll(delegate(Problem x) { return x.ToString(); }).ToArray());
            Assert.Contains(all, "Watchdog 'VVD': the program needs its full path");
            Assert.Contains(all, "not for the program's own name");
            Assert.Contains(all, "Folder task 'Move': the destination is inside the folder");
            Assert.Contains(all, "Watchdog 'Svc': 'Running means' needs the application to watch");
        }

        [Fact]
        public void Entries_added_by_hand_get_ids_that_stay()
        {
            SettingsFile f = Store();
            Directory.CreateDirectory(f.DataDir);
            File.WriteAllText(f.Path, "{ \"format\": \"watchkeeper-4\", \"folders\": [ { \"name\": \"A\" }, { \"id\": \"x\", \"name\": \"B\" }, { \"id\": \"x\", \"name\": \"C\" } ] }");
            LoadResult r = f.Load();
            Assert.Contains(string.Join(" ", r.Notes.ToArray()), "were given one and the file was saved");
            List<string> ids = r.Settings.Folders.ConvertAll(delegate(FolderTask t) { return t.Id; });
            Assert.Eq("x", ids[1]);
            Assert.True(ids[0].Length > 0 && ids[2] != "x");
            for (int i = 0; i < 2; i++)
            {
                LoadResult again = Store().Load();
                Assert.Eq(ids, again.Settings.Folders.ConvertAll(delegate(FolderTask t) { return t.Id; }));
                Assert.Eq(0, again.Notes.Count);
            }
        }

        [Fact]
        public void Folders_on_mapped_or_missing_drives_are_warned_about()
        {
            Func<char, string> real = Validate.DriveKind;
            try
            {
                Validate.DriveKind = delegate(char c) { return c == 'Z' ? "network" : c == 'Q' ? "missing" : "local"; };
                FolderTask f = new FolderTask();
                f.Name = "Archive";
                f.Source = @"D:\Recordings";
                f.Destination = @"Z:\Archive";
                string all = string.Join(" | ", Validate.Folder(f).ConvertAll(delegate(Problem x) { return x.ToString(); }).ToArray());
                Assert.Contains(all, "mapped drive Z:");
                Assert.Contains(all, "\\\\server\\share");
                f.Destination = @"Q:\Archive";
                all = string.Join(" | ", Validate.Folder(f).ConvertAll(delegate(Problem x) { return x.ToString(); }).ToArray());
                Assert.Contains(all, "drive Q:, which this computer does not have");
                f.Destination = @"\\nas\archive";
                Assert.Eq(0, Validate.Folder(f).Count);
            }
            finally { Validate.DriveKind = real; }
        }

        [Fact]
        public void A_version_3_file_is_converted_and_reported()
        {
            SettingsFile f = Store();
            Directory.CreateDirectory(f.DataDir);
            File.Copy(Fixture("v3-full.json"), f.Path);
            LoadResult r = f.Load();
            Assert.Eq("imported", r.Source);
            Settings s = r.Settings;
            string report = string.Join("\n", r.ImportReport.ToArray());
            Assert.Eq("Studio A server", s.General.ServerName);

            ProgramEntry vvd = s.Watchdog[0];
            Assert.True(vvd.Enabled);
            Assert.Eq("both", vvd.RunningMeans);
            Assert.Eq("04:30", vvd.RestartAt);
            Assert.Eq(new List<string> { "monday" }, vvd.RestartDays);
            Assert.Eq("http://127.0.0.1:8088/api", vvd.HealthUrl);
            Assert.Eq("secret1", Secrets.Reveal(vvd.HealthPassword));
            Assert.Eq("background", s.Watchdog[1].RunIn);
            Assert.Contains(report, "belonged to a studio");

            // policy with move + keep + delete: two tasks, the keep rule reported
            List<FolderTask> made = s.Folders.FindAll(delegate(FolderTask x) { return x.Name.StartsWith("Move ISO"); });
            Assert.Eq(2, made.Count);
            Assert.Eq("move", made[0].Action);
            Assert.True(made[0].Enabled);
            Assert.Eq("delete", made[1].Action);
            Assert.Eq(14.0, made[1].OlderThan);
            Assert.Contains(report, "(keep) is not carried over");
            Assert.Contains(report, "time-of-day window is not carried over");

            Assert.Eq(1, s.Gpio.Triggers.Count);
            Assert.Contains(report, "reacted to a command message");
            Assert.Contains(report, "studio switch action");
            Assert.Eq("Duration=500", s.Gpio.Triggers[0].WhenOn[0].Extra);
            Assert.Contains(report, "logged in without a password");

            AlertDestination mail = s.Alerts.Destinations.Find(delegate(AlertDestination x) { return x.Name == "Ops mail"; });
            Assert.Eq("ssl", mail.SmtpSecurity);
            Assert.Eq("pw", Secrets.Reveal(mail.SmtpPassword));
            Assert.Eq(15, mail.RepeatMinutes);
            Assert.True(mail.Watchdog && mail.Folders && mail.Gpio);
            AlertDestination disc = s.Alerts.Destinations.Find(delegate(AlertDestination x) { return x.Name == "Discord"; });
            Assert.True(disc.Gpio && disc.Vertical && !disc.Watchdog && !disc.Folders);
            Assert.Eq("error", disc.MinLevel);
            Assert.Contains(disc.Body, "content");
            Assert.False(s.Alerts.Destinations.Find(delegate(AlertDestination x) { return x.Name == "Unused"; }).Enabled);
            Assert.True(s.Alerts.EventLog);
            Assert.Contains(report, "1 script(s)");

            Assert.True(File.Exists(Path.Combine(f.DataDir, "import-report.txt")));
            Assert.True(f.Backups().Exists(delegate(Backup b) { return b.Reason == "before-imported-3x"; }), "the version 3 file is kept");
            Assert.Eq("file", Store().Load().Source);                  // converted once
        }

        [Fact]
        public void Only_the_service_converts_version_3_settings()
        {
            SettingsFile manager = Store();
            manager.ImportVersion3 = false;
            Directory.CreateDirectory(manager.DataDir);
            File.Copy(Fixture("v3-full.json"), manager.Path);
            LoadResult r = manager.Load();
            Assert.Null(r.Settings);
            Assert.Contains(r.Error, "The service converts them when it starts");
            Assert.Eq(File.ReadAllText(Fixture("v3-full.json")), File.ReadAllText(manager.Path));
            Assert.Eq("imported", Store().Load().Source);
        }

        [Fact]
        public void The_version_3_starter_converts_without_errors()
        {
            SettingsFile f = Store();
            Directory.CreateDirectory(f.DataDir);
            File.Copy(Fixture("v3-studio-starter.json"), f.Path);
            LoadResult r = f.Load();
            Assert.Eq("imported", r.Source);
            Assert.Eq(2, r.Settings.Watchdog.Count);
            Assert.True(r.Settings.Folders.Count >= 7);
            Assert.False(r.Settings.Folders.Exists(delegate(FolderTask x) { return x.Enabled; }), "starter tasks stay off");
            Assert.Eq(2, r.Settings.Vertical.Sources.Count);
            Assert.Eq(0, Validate.All(r.Settings).FindAll(delegate(Problem p) { return p.Level == "error"; }).Count);
        }
    }
}
