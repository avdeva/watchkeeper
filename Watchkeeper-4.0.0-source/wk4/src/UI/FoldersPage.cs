// Folder tasks page: move, copy or delete files on a schedule.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Watchkeeper.UI
{
    public class FoldersPage : Page
    {
        readonly ListEditor<FolderTask> list = new ListEditor<FolderTask>("Add a task that moves, copies or deletes the files of a folder.");
        FolderTask cur = new FolderTask();
        readonly Label live = new Label();
        FieldRow destRow, structRow, existsRow, intervalRow, atRow;
        public override string Title { get { return "Folder tasks"; } }

        static readonly string[][] Actions = { new[] { "move", "Move them to another folder" }, new[] { "copy", "Copy them to another folder" }, new[] { "delete", "Delete them" } };
        static readonly string[][] Exists = { new[] { "rename", "Keep both (add (2), (3), ... to the new one)" }, new[] { "skip", "Leave the file where it is" }, new[] { "overwrite", "Replace the file there" } };
        static readonly string[][] Units = { new[] { "minutes", "minutes" }, new[] { "hours", "hours" }, new[] { "days", "days" } };
        static readonly string[][] Runs = { new[] { "every", "Every few minutes" }, new[] { "daily", "Once a day" }, new[] { "manual", "Only with Run now" } };

        public FoldersPage(Manager m) : base(m)
        {
            list.Title = delegate(FolderTask t) { return t.Name; };
            list.Subtitle = delegate(FolderTask t) { return Line(t); };
            list.Mark = delegate(FolderTask t) { FolderTaskStatus s = StatusOf(t); return !t.Enabled ? Ui.Off : s == null ? Ui.Warn : Ui.StateColor(s.State); };
            list.Create = delegate { return new FolderTask { Id = Util.NewId(), Name = "New folder task" }; };
            list.Copy = delegate(FolderTask t) { FolderTask c = new FolderTask(); Json.FillFrom(c, Json.ToJson(t)); c.Id = Util.NewId(); c.Name = t.Name + " (copy)"; c.Enabled = false; return c; };
            list.Selected += delegate(FolderTask t) { cur = t ?? new FolderTask(); B.Load(); Toggle(); ShowLive(); };
            list.ListChanged += delegate { M.MarkChanged(); };
            Controls.Add(list);
            Heading("Folder tasks", "Each task handles the files of one folder: move them (for example from the recording drive to the storage drive), " +
                                    "copy them, or delete old ones. Files still being written are always left alone. Tasks run one after another, in this order.");

            FormGrid g = new FormGrid();
            int w = Ui.BaseFont().Height * 30;
            g.Row("", B.Check("Run this task", delegate { return cur.Enabled; }, delegate(bool v) { cur.Enabled = v; }));
            g.Row("Name", B.Text(w / 2, delegate { return cur.Name; }, delegate(string v) { cur.Name = v; }));
            TextBox src = B.Text(w, delegate { return cur.Source; }, delegate(string v) { cur.Source = v; });
            g.Row("Folder", Ui.Flow(src, BrowseFolder(src)));
            g.Row("", B.Check("Include sub-folders", delegate { return cur.Subfolders; }, delegate(bool v) { cur.Subfolders = v; Toggle(); }));
            g.Section("Which files");
            g.Row("Files", B.Text(w / 2, delegate { return cur.Include; }, delegate(string v) { cur.Include = v; }), "Name patterns separated by ';', e.g. *.mp4;*.mov. * = every file.");
            g.Row("Except", B.Text(w / 2, delegate { return cur.Exclude; }, delegate(string v) { cur.Exclude = v; }), "Files matching these patterns are never touched.");
            g.Row("Older than", Ui.Flow(B.Number(0, 100000, 1, delegate { return cur.OlderThan; }, delegate(double v) { cur.OlderThan = v; }),
                                        B.Choice(Units, delegate { return cur.OlderThanUnit; }, delegate(string v) { cur.OlderThanUnit = v; })),
                  "By the date the file was last changed. 0 = any age.");
            g.Row("Keep the newest", Ui.Flow(B.Number(0, 1000000, 0, delegate { return cur.KeepNewest; }, delegate(double v) { cur.KeepNewest = (int)v; }), Ui.Label("files (0 = none)")));
            g.Row("Leave alone", Ui.Flow(B.Number(0, 86400, 0, delegate { return cur.SettleSeconds; }, delegate(double v) { cur.SettleSeconds = (int)v; }), Ui.Label("seconds after a file last changed")),
                  "A recording in progress keeps changing; it is handled once it has been unchanged this long. Files another program has open for writing are always left.");
            g.Section("What to do");
            g.Row("Action", B.Choice(Actions, delegate { return cur.Action; }, delegate(string v) { cur.Action = v; Toggle(); }));
            TextBox dst = B.Text(w, delegate { return cur.Destination; }, delegate(string v) { cur.Destination = v; });
            destRow = g.Row("To", Ui.Flow(dst, BrowseFolder(dst)), "Moving to another drive copies the file, checks the copy, and only then deletes the original.");
            structRow = g.Row("", B.Check("Recreate the sub-folders there", delegate { return cur.KeepStructure; }, delegate(bool v) { cur.KeepStructure = v; }));
            existsRow = g.Row("If the name is taken", B.Choice(Exists, delegate { return cur.IfExists; }, delegate(string v) { cur.IfExists = v; }));
            g.Row("", B.Check("Remove sub-folders that are empty afterwards", delegate { return cur.RemoveEmptyFolders; }, delegate(bool v) { cur.RemoveEmptyFolders = v; }));
            g.Section("When");
            g.Row("Run", B.Choice(Runs, delegate { return cur.Run; }, delegate(string v) { cur.Run = v; Toggle(); }));
            intervalRow = g.Row("Every", Ui.Flow(B.Number(0.5m, 10080, 1, delegate { return cur.IntervalMinutes; }, delegate(double v) { cur.IntervalMinutes = v; }), Ui.Label("minutes")));
            atRow = g.Row("At", B.Text(60, delegate { return cur.At; }, delegate(string v) { cur.At = v.Trim(); }), "HH:MM. A time missed while the computer was off runs when it is back.");
            g.Row("Notes", B.Text(w, delegate { return cur.Notes; }, delegate(string v) { cur.Notes = v; }));
            g.Section("Now");
            live.AutoSize = true;
            live.MaximumSize = new Size(w + 200, 0);
            g.Row("State", live);
            g.Row("", Ui.Flow(Ui.Button("Preview...", delegate { Preview(); }), Ui.Button("Run now", delegate { RunNow(); })),
                  "Preview lists what the task would do now, without changing anything. Run now asks the service to run it once.");
            list.Detail.Controls.Add(g);
        }

        static Button BrowseFolder(TextBox target)
        {
            return Ui.Button("Browse...", delegate
            {
                using (FolderBrowserDialog d = new FolderBrowserDialog())
                {
                    d.SelectedPath = Util.NormPath(target.Text);
                    if (d.ShowDialog(target.FindForm()) == DialogResult.OK) target.Text = d.SelectedPath;
                }
            });
        }

        void Toggle()
        {
            bool del = cur.Action == "delete";
            destRow.Show(!del);
            existsRow.Show(!del);
            structRow.Show(!del && cur.Subfolders);
            intervalRow.Show(cur.Run == "every");
            atRow.Show(cur.Run == "daily");
        }

        protected override void OnFieldChanged() { list.RefreshTitles(); }

        public override void Reload() { list.SetItems(M.Edit.Folders); }

        FolderTaskStatus StatusOf(FolderTask t)
        {
            if (M.Status == null || !M.ServiceAlive) return null;
            return M.Status.Folders.Find(delegate(FolderTaskStatus s) { return s.Id == t.Id; });
        }

        string Line(FolderTask t)
        {
            if (!t.Enabled) return "switched off";
            FolderTaskStatus s = StatusOf(t);
            return s == null ? (M.ServiceAlive ? "not saved yet" : "service not running") : s.Text;
        }

        void ShowLive()
        {
            FolderTaskStatus s = StatusOf(cur);
            if (s == null) { live.Text = M.ServiceAlive ? "Not known yet (save the settings)." : "The service is not running."; live.ForeColor = Ui.Help; return; }
            List<string> lines = new List<string> { s.Text };
            if (s.LastRun > 0) lines.Add("Last run: " + Util.FmtTime(s.LastRun) + " - " + (s.LastResult.Length > 0 ? s.LastResult : "?"));
            if (s.NextRun > 0 && s.Enabled) lines.Add("Next run: " + At(s.NextRun));
            live.Text = string.Join("\n", lines.ToArray());
            live.ForeColor = Ui.StateColor(s.State);
        }

        public override void Live(EngineStatus s) { list.RefreshTitles(); ShowLive(); }

        public override Color Dot(EngineStatus s)
        {
            bool any = false, bad = false;
            foreach (FolderTaskStatus f in s.Folders) { if (!f.Enabled) continue; any = true; if (f.State == "error") bad = true; }
            return !any ? Ui.Off : bad ? Ui.Bad : Ui.Good;
        }

        public string PreviewText()
        {
            TaskResult r = FolderTasks.Run(cur, true, null, null);
            if (r.Fatal != null) return "The task cannot run: " + r.Fatal;
            List<string> lines = new List<string>();
            lines.Add("If the task ran now: " + r.Summary() + " (" + r.Matched + " file(s) match).");
            lines.Add("");
            foreach (FileStep s in r.Steps)
            {
                string what = s.Action == "skip" ? "LEAVE   " : s.Action.ToUpperInvariant().PadRight(8);
                lines.Add(what + s.Source + (s.Target.Length > 0 && s.Action != "skip" ? "  ->  " + s.Target : "") + (s.Note.Length > 0 ? "   (" + s.Note + ")" : ""));
                if (lines.Count > 2000) { lines.Add("... (only the first 2000 files are listed)"); break; }
            }
            foreach (string e in r.Errors) lines.Add("PROBLEM " + e);
            return string.Join("\n", lines.ToArray());
        }

        void Preview()
        {
            Cursor = Cursors.WaitCursor;
            string text;
            try { text = PreviewText(); } finally { Cursor = Cursors.Default; }
            Ui.ShowText(FindForm(), "Preview: " + cur.Name, text);
        }

        void RunNow()
        {
            if (M.Dirty) { Ui.Info(FindForm(), "Save the changes first."); return; }
            if (!M.ServiceAlive) { Ui.Info(FindForm(), "The service is not running."); return; }
            CommandResult r = M.Command("run_folder_task", cur.Id, 10000);
            Ui.Info(FindForm(), r == null ? "The service did not answer." : cur.Name + ": " + r.Text + ". The result shows under State.");
        }
    }
}
