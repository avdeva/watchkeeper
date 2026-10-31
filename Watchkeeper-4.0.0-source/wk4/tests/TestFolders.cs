using System;
using System.Collections.Generic;
using System.IO;
using Watchkeeper;

namespace Watchkeeper.Tests
{
    public class FolderTests : Env
    {
        FolderTask Task(string action)
        {
            FolderTask t = new FolderTask();
            t.Id = "t1";
            t.Name = "Test";
            t.Enabled = true;
            t.Source = D("cache");
            t.Destination = D("data");
            t.Action = action;
            t.SettleSeconds = 60;
            t.OlderThan = 0;
            Directory.CreateDirectory(t.Source);
            return t;
        }

        string Old(string path) { Write(path, "content of " + Path.GetFileName(path)); Age(path, 600); return path; }

        public override void Dispose()
        {
            FolderTasks.ForceCopyMove = false;
            base.Dispose();
        }

        [Fact]
        public void Moves_settled_files_and_leaves_files_being_written()
        {
            FolderTask t = Task("move");
            Old(D("cache", "a.mp4"));
            Write(D("cache", "recording.mp4"), "growing");          // just written
            TaskResult r = FolderTasks.Run(t, false, Hub.For("folders"), null);
            Assert.Eq(1, r.Moved);
            Assert.Eq(1, r.Skipped);
            Assert.True(File.Exists(D("data", "a.mp4")));
            Assert.False(File.Exists(D("cache", "a.mp4")));
            Assert.True(File.Exists(D("cache", "recording.mp4")));
            Assert.Eq("1 moved, 1 left for now (" + Util.FmtSize(new FileInfo(D("data", "a.mp4")).Length) + ")", r.Summary());
        }

        [Fact]
        public void Moving_to_another_drive_copies_checks_and_keeps_the_date()
        {
            FolderTasks.ForceCopyMove = true;
            FolderTask t = Task("move");
            string a = Old(D("cache", "a.mp4"));
            DateTime written = File.GetLastWriteTimeUtc(a);
            TaskResult r = FolderTasks.Run(t, false, null, null);
            Assert.Eq(1, r.Moved);
            Assert.False(File.Exists(a));
            Assert.Eq("content of a.mp4", File.ReadAllText(D("data", "a.mp4")));
            Assert.Near(0, (File.GetLastWriteTimeUtc(D("data", "a.mp4")) - written).TotalSeconds, 1);
            Assert.Eq(0, Directory.GetFiles(D("data"), "*.watchkeeper-partial").Length);
        }

        [Fact]
        public void Copy_with_skip_copies_each_file_once()
        {
            FolderTask t = Task("copy");
            t.IfExists = "skip";
            Old(D("cache", "a.mp4"));
            Assert.Eq(1, FolderTasks.Run(t, false, null, null).Copied);
            TaskResult again = FolderTasks.Run(t, false, null, null);
            Assert.Eq(0, again.Copied);
            Assert.Eq(1, again.Skipped);
            Assert.True(File.Exists(D("cache", "a.mp4")));
        }

        [Fact]
        public void A_copy_task_copies_each_file_once_whatever_the_name_setting()
        {
            FolderTask t = Task("copy");
            t.IfExists = "rename";
            Old(D("cache", "a.mp4"));
            for (int i = 0; i < 3; i++) FolderTasks.Run(t, false, null, null);
            Assert.Eq(1, Directory.GetFiles(D("data")).Length);
            Write(D("cache", "a.mp4"), "a new recording, same name");
            Age(D("cache", "a.mp4"), 600);
            FolderTasks.Run(t, false, null, null);
            Assert.Eq(2, Directory.GetFiles(D("data")).Length);       // different content: kept both
        }

        [Fact]
        public void A_move_whose_original_stayed_behind_is_completed_without_another_copy()
        {
            FolderTasks.ForceCopyMove = true;
            FolderTask t = Task("move");
            string a = Old(D("cache", "a.mp4"));
            Directory.CreateDirectory(D("data"));
            File.Copy(a, D("data", "a.mp4"));
            File.SetLastWriteTimeUtc(D("data", "a.mp4"), File.GetLastWriteTimeUtc(a));
            TaskResult r = FolderTasks.Run(t, false, null, null);
            Assert.Eq(1, r.Moved);
            Assert.False(File.Exists(a));
            Assert.Eq(1, Directory.GetFiles(D("data")).Length);
        }

        [Fact]
        public void Same_names_from_different_sub_folders_never_overwrite_each_other()
        {
            FolderTask t = Task("move");
            t.Subfolders = true;
            t.KeepStructure = false;
            t.IfExists = "overwrite";
            Write(D("cache", "Cam1", "clip001.mp4"), "same bytes");
            Write(D("cache", "Cam2", "clip001.mp4"), "same bytes");
            Age(D("cache", "Cam1", "clip001.mp4"), 600);
            File.SetLastWriteTimeUtc(D("cache", "Cam2", "clip001.mp4"), File.GetLastWriteTimeUtc(D("cache", "Cam1", "clip001.mp4")));
            FolderTasks.Run(t, false, null, null);
            Assert.Eq(2, Directory.GetFiles(D("data")).Length);        // even with the same size, date and bytes
        }

        [Fact]
        public void A_name_already_taken_is_renamed_or_overwritten()
        {
            FolderTask t = Task("move");
            Old(D("cache", "a.mp4"));
            Write(D("data", "a.mp4"), "older");
            FolderTasks.Run(t, false, null, null);
            Assert.Eq("older", File.ReadAllText(D("data", "a.mp4")));
            Assert.Eq("content of a.mp4", File.ReadAllText(D("data", "a (2).mp4")));
            t.IfExists = "overwrite";
            Old(D("cache", "a.mp4"));
            FolderTasks.Run(t, false, null, null);
            Assert.Eq("content of a.mp4", File.ReadAllText(D("data", "a.mp4")));
        }

        [Fact]
        public void Patterns_age_and_newest_files_are_respected()
        {
            FolderTask t = Task("delete");
            t.Include = "*.mp4;*.mov";
            t.Exclude = "keep*";
            t.OlderThan = 2;
            t.OlderThanUnit = "days";
            t.KeepNewest = 1;
            foreach (string n in new[] { "old1.mp4", "old2.mov", "keep-me.mp4", "notes.txt" }) { Write(D("cache", n), "x"); Age(D("cache", n), 5 * 86400); }
            Age(D("cache", "old1.mp4"), 4 * 86400);                 // the newest of the old ones is kept
            Write(D("cache", "new.mp4"), "x");
            Age(D("cache", "new.mp4"), 3600);
            TaskResult preview = FolderTasks.Run(t, true, null, null);
            Assert.Eq(1, preview.Deleted);
            Assert.True(File.Exists(D("cache", "old2.mov")), "preview changes nothing");
            TaskResult r = FolderTasks.Run(t, false, null, null);
            Assert.Eq(1, r.Deleted);
            Assert.False(File.Exists(D("cache", "old2.mov")));
            foreach (string n in new[] { "old1.mp4", "keep-me.mp4", "notes.txt", "new.mp4" }) Assert.True(File.Exists(D("cache", n)), n + " stays");
        }

        [Fact]
        public void Subfolders_keep_their_structure_and_empty_ones_are_removed()
        {
            FolderTask t = Task("move");
            t.Subfolders = true;
            t.RemoveEmptyFolders = true;
            Old(D("cache", "2026-11-01", "cam1", "a.mp4"));
            Old(D("cache", "top.mp4"));
            TaskResult r = FolderTasks.Run(t, false, null, null);
            Assert.Eq(2, r.Moved);
            Assert.True(File.Exists(D("data", "2026-11-01", "cam1", "a.mp4")));
            Assert.True(File.Exists(D("data", "top.mp4")));
            Assert.False(Directory.Exists(D("cache", "2026-11-01")));
            Assert.True(Directory.Exists(D("cache")), "the folder itself stays");
        }

        [Fact]
        public void A_missing_folder_fails_the_task_with_a_clear_reason()
        {
            FolderTask t = Task("move");
            t.Source = D("nope");
            TaskResult r = FolderTasks.Run(t, false, null, null);
            Assert.Contains(r.Fatal, "does not exist or cannot be reached");
        }

        [Fact]
        public void Every_and_daily_schedules()
        {
            FolderTask t = Task("move");
            t.Run = "every";
            t.IntervalMinutes = 5;
            DateTime now = new DateTime(2026, 11, 2, 10, 0, 0);
            Assert.Eq(Util.ToEpoch(now), FolderTasks.NextRun(t, 0, now));
            Assert.Eq(Util.ToEpoch(now) + 300, FolderTasks.NextRun(t, Util.ToEpoch(now), now));
            t.Run = "daily";
            t.At = "03:00";
            Assert.Eq(Util.ToEpoch(new DateTime(2026, 11, 3, 3, 0, 0)), FolderTasks.NextRun(t, Util.ToEpoch(now), now));
            Assert.Eq(Util.ToEpoch(new DateTime(2026, 11, 2, 3, 0, 0)), FolderTasks.NextRun(t, Util.ToEpoch(now.AddDays(-1)), now));   // missed: now
            t.Run = "manual";
            Assert.Eq(0.0, FolderTasks.NextRun(t, 0, now));
        }

        [Fact]
        public void The_service_runs_due_tasks_and_alerts_on_failures()
        {
            Settings cfg = new Settings();
            FolderTask t = Task("move");
            cfg.Folders.Add(t);
            FakeAlerts alerts = new FakeAlerts();
            StateStore state = new StateStore(D("state.json"));
            FolderTasks ft = new FolderTasks(delegate { return cfg; }, Hub.For("folders"), alerts, state);
            Old(D("cache", "a.mp4"));
            ft.Tick(DateTime.Now);
            ft.RunQueued();
            Assert.True(File.Exists(D("data", "a.mp4")));
            Assert.Contains(ft.Snapshot()[0].Text, "1 moved");
            Assert.True(state.Get("folder:t1") > 0);

            Directory.Delete(D("cache"), true);
            ft.Tick(DateTime.Now.AddMinutes(6));
            ft.RunQueued();
            Assert.Eq("error", ft.Snapshot()[0].State);
            Assert.Eq(1, alerts.Count("folders:t1:run", false));
            Directory.CreateDirectory(D("cache"));
            ft.Tick(DateTime.Now.AddMinutes(12));
            ft.RunQueued();
            Assert.Eq(1, alerts.Count("folders:t1:run", true));
            Assert.Eq(new StateStore(D("state.json")).Get("folder:t1"), state.Get("folder:t1"));    // remembered across restarts
        }

        [Fact]
        public void A_daily_task_waits_for_its_time_on_the_first_start()
        {
            Settings cfg = new Settings();
            FolderTask t = Task("delete");
            t.Run = "daily";
            t.At = "03:00";
            cfg.Folders.Add(t);
            Old(D("cache", "a.mp4"));
            FolderTasks ft = new FolderTasks(delegate { return cfg; }, Hub.For("folders"), new NoAlerts(), new StateStore(null));
            DateTime ten = DateTime.Today.AddHours(10);
            ft.Tick(ten);
            ft.RunQueued();
            Assert.True(File.Exists(D("cache", "a.mp4")));
            ft.Tick(DateTime.Today.AddDays(1).AddHours(3).AddSeconds(5));
            ft.RunQueued();
            Assert.False(File.Exists(D("cache", "a.mp4")));
        }
    }
}
