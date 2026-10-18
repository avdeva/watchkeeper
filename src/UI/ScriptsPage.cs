// Scripts and programs run by the service: on a schedule, once at service start, or for
// every new file that appears in a folder.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace Watchkeeper.UI
{
    public class ScriptsPage : SectionPage
    {
        readonly ListEditor<ScriptJob> list = new ListEditor<ScriptJob>("script");
        readonly CheckBox sOn = Ui.Chk("Switched on");
        readonly TextBox sName = Ui.Text(260);
        readonly PathBox sCmd = new PathBox(false, "Scripts and programs (*.ps1;*.bat;*.cmd;*.vbs;*.exe)|*.ps1;*.bat;*.cmd;*.vbs;*.exe|All files (*.*)|*.*", true);
        readonly TextBox sArgs = Ui.Text(420);
        readonly PathBox sDir = new PathBox(true, null, true);
        readonly PathBox sSource = new PathBox(true, null, true);
        readonly PathBox sDestination = new PathBox(true, null, true);
        readonly RadioButton tSchedule = Ui.Radio("On a schedule");
        readonly RadioButton tStartup = Ui.Radio("When the service starts");
        readonly RadioButton tFile = Ui.Radio("For each new file in a folder");
        readonly ScheduleEditor sSchedule = new ScheduleEditor();
        readonly PathBox sWatch = new PathBox(true, null, true);
        readonly TextBox sPattern = Ui.Text(200);
        readonly CheckBox sWatchSub = Ui.Chk("Include sub-folders");
        readonly NumericUpDown sStable = Ui.Num(0, 86400, 5, 0);
        readonly CheckBox sExisting = Ui.Chk("Also run for files already there when this is first switched on");
        readonly NumericUpDown sRate = Ui.Num(0, 6000, 5, 0);
        readonly TrustNote sTrust = new TrustNote();
        readonly RadioButton rService = Ui.Radio("The service (hidden, output goes to the log)");
        readonly RadioButton rUser = Ui.Radio("The signed-in user's session (output is not captured)");
        readonly NumericUpDown sTimeout = Ui.Num(0, 10080, 5, 0);
        readonly TextBox sCodes = Ui.Text(120);
        readonly ComboBox sPriority;
        readonly TextBox sNotes = Ui.Text(420);
        readonly RunBar run;
        readonly Button outputBtn;
        int fill;

        public ScriptsPage(Manager m)
            : base(m, "scripts", "Scripts",
                   "Any PowerShell script, batch file or program, run by the service on a schedule, at start-up, or once for every new file in a folder.")
        {
            sPriority = Ui.Combo(new Choice("below_normal", "Below normal"), new Choice("normal", "Normal"),
                                 new Choice("above_normal", "Above normal"), new Choice("high", "High"));
            run = new RunBar(m, delegate { ScriptJob s = list.Selected; return s == null || s.Id.Length == 0 ? "" : "script:" + s.Id; },
                             delegate { return !Dirty && Saved(list.Selected); });
            outputBtn = Ui.Btn("Show output of the last run", delegate { ShowOutput(); });
            outputBtn.Tag = "nodirty";

            list.EmptyText = "No scripts yet.\r\n\r\nClick \"Add script\".";
            list.Create = delegate
            {
                ScriptJob s = new ScriptJob();
                s.Name = "New script";
                return s;
            };
            list.Copy = delegate(ScriptJob s)
            {
                ScriptJob c = Json.Clone(s);
                c.Id = "";
                c.Name = s.Name + " (copy)";
                c.Enabled = false;
                return c;
            };
            list.Title = delegate(ScriptJob s) { return s.Name; };
            list.Subtitle = delegate(ScriptJob s) { return s.Command.Length == 0 ? "No script chosen yet" : s.Command; };
            list.Badge = delegate(ScriptJob s)
            {
                if (!s.Enabled) return "";
                return s.Trigger == "file" ? "new files" : s.Trigger == "startup" ? "at start" : JobManager.Describe(s.Schedule, true);
            };
            list.IsOn = delegate(ScriptJob s) { return s.Enabled; };

            FormGrid g = new FormGrid();
            g.Row(null, sOn);
            g.Row("Name", sName);
            g.Row("Script or program", sCmd, ".ps1 runs through PowerShell (execution policy bypassed), .bat/.cmd through cmd, .vbs through cscript.");
            g.Row(null, sTrust);
            g.Row("Source", sSource, "Usually on the cache drive. Passed as {source} and the environment variable WK_SOURCE; also the watched folder for new files.");
            g.Row("Destination", sDestination, "Usually on the data drive. Passed as {destination} and WK_DESTINATION.");
            g.Row("Arguments", sArgs, "e.g. -Source {source} -Destination {destination}. For new files also {file} (full path), {name}, {stem} (name without extension), {folder}; " +
                                       "not for .bat/.cmd scripts, which read the file from the environment variable WK_FILE (file names could otherwise run commands).");
            g.Row("Start in", sDir, "Empty = the script's folder.");
            g.Header("When");
            FlowLayoutPanel trig = new FlowLayoutPanel();
            trig.FlowDirection = FlowDirection.TopDown;
            trig.AutoSize = true;
            trig.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            trig.WrapContents = false;
            trig.Margin = new Padding(0);
            trig.Controls.Add(tSchedule);
            trig.Controls.Add(tStartup);
            trig.Controls.Add(tFile);
            g.Row("Run", trig);
            g.Row("Schedule", sSchedule);
            g.Row("Watched folder", sWatch, "Empty = the source folder.");
            g.Row("Files matching", Ui.Flow(sPattern, sWatchSub));
            Label secs = Ui.Lbl("seconds without changes before it runs");
            g.Row("Wait until a file is", Ui.Flow(sStable, secs));
            g.Row(null, sExisting);
            Label perMin = Ui.Lbl("runs a minute (0 = no limit); further files wait their turn");
            g.Row("At most", Ui.Flow(sRate, perMin), "Stops a flood of new files from starting hundreds of runs. An alert is raised when more than 100 files are waiting.");
            g.Header("How");
            FlowLayoutPanel where = new FlowLayoutPanel();
            where.FlowDirection = FlowDirection.TopDown;
            where.AutoSize = true;
            where.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            where.WrapContents = false;
            where.Margin = new Padding(0);
            where.Controls.Add(rService);
            where.Controls.Add(rUser);
            g.Row("Run in", where);
            Label mins = Ui.Lbl("minutes, then stop it (0 = no limit)");
            g.Row("Allow up to", Ui.Flow(sTimeout, mins));
            g.Row("Success exit codes", sCodes, "Comma separated. Any other exit code counts as a failure and sends an alert.");
            g.Row("Priority", sPriority);
            g.Row("Notes", sNotes);
            g.Row("Run it now", run);
            g.Row(null, outputBtn);
            list.Detail.Controls.Add(g);

            EventHandler apply = delegate { Apply(); };
            sOn.CheckedChanged += apply;
            sName.TextChanged += apply;
            sCmd.ValueChanged += apply;
            sCmd.Picked += delegate
            {
                if (sName.Text.Trim().Length == 0 || sName.Text == "New script")
                    sName.Text = System.IO.Path.GetFileNameWithoutExtension(sCmd.Text.Trim().Trim('"'));
            };
            sArgs.TextChanged += apply;
            sSource.ValueChanged += apply;
            sDestination.ValueChanged += apply;
            sDir.ValueChanged += apply;
            tSchedule.CheckedChanged += apply;
            tStartup.CheckedChanged += apply;
            tFile.CheckedChanged += apply;
            sSchedule.ValueChanged += apply;
            sWatch.ValueChanged += apply;
            sPattern.TextChanged += apply;
            sWatchSub.CheckedChanged += apply;
            sStable.ValueChanged += apply;
            sExisting.CheckedChanged += apply;
            sRate.ValueChanged += apply;
            rService.CheckedChanged += apply;
            rUser.CheckedChanged += apply;
            sTimeout.ValueChanged += apply;
            sCodes.TextChanged += apply;
            sPriority.SelectedIndexChanged += apply;
            sNotes.TextChanged += apply;
            list.SelectedChanged += delegate { Fill(); };
            list.Changed += delegate { MarkDirty(); };

            Body.AutoScroll = false;
            Body.Controls.Add(list);
            Init();
        }

        bool Saved(ScriptJob s)
        {
            if (s == null || s.Id.Length == 0) return false;
            return M.Config.Get().Scripts.Exists(delegate(ScriptJob x) { return x.Id == s.Id; });
        }

        void UpdateEnabled()
        {
            sTrust.Check(sCmd.Text, !rUser.Checked);
            bool file = tFile.Checked;
            sSchedule.Enabled = tSchedule.Checked;
            sWatch.Enabled = sPattern.Enabled = sWatchSub.Enabled = sStable.Enabled = sExisting.Enabled = sRate.Enabled = file;
        }

        void Fill()
        {
            ScriptJob s = list.Selected;
            fill++;
            try
            {
                Quiet(delegate
                {
                    ScriptJob d = s ?? new ScriptJob();
                    sOn.Checked = d.Enabled;
                    sName.Text = s != null ? d.Name : "";
                    sCmd.Text = d.Command;
                    sArgs.Text = d.Arguments;
                    sSource.Text = d.Source;
                    sDestination.Text = d.Destination;
                    sDir.Text = d.WorkingDir;
                    tSchedule.Checked = d.Trigger == "schedule";
                    tStartup.Checked = d.Trigger == "startup";
                    tFile.Checked = d.Trigger == "file";
                    sSchedule.Load(d.Schedule);
                    sWatch.Text = d.WatchPath;
                    sPattern.Text = d.WatchPattern;
                    sWatchSub.Checked = d.WatchSubfolders;
                    Ui.SetNum(sStable, d.StableSeconds);
                    sExisting.Checked = d.ProcessExisting;
                    Ui.SetNum(sRate, d.MaxRunsPerMinute);
                    rService.Checked = d.RunAs == "service";
                    rUser.Checked = d.RunAs == "user";
                    Ui.SetNum(sTimeout, d.TimeoutMinutes);
                    sCodes.Text = d.OkExitCodes;
                    Ui.Set(sPriority, d.Priority);
                    sNotes.Text = d.Notes;
                });
            }
            finally { fill--; }
            UpdateEnabled();
            run.Refresh2();
        }

        void Apply()
        {
            UpdateEnabled();
            if (fill > 0 || Loading) return;
            ScriptJob s = list.Selected;
            if (s == null) return;
            s.Enabled = sOn.Checked;
            s.Name = sName.Text;
            s.Command = sCmd.Text;
            s.Arguments = sArgs.Text;
            s.Source = sSource.Text;
            s.Destination = sDestination.Text;
            s.WorkingDir = sDir.Text;
            s.Trigger = tFile.Checked ? "file" : tStartup.Checked ? "startup" : "schedule";
            s.Schedule = sSchedule.Value();
            s.WatchPath = sWatch.Text;
            s.WatchPattern = sPattern.Text;
            s.WatchSubfolders = sWatchSub.Checked;
            s.StableSeconds = (int)sStable.Value;
            s.ProcessExisting = sExisting.Checked;
            s.MaxRunsPerMinute = (int)sRate.Value;
            s.RunAs = rUser.Checked ? "user" : "service";
            s.TimeoutMinutes = (int)sTimeout.Value;
            s.OkExitCodes = sCodes.Text;
            s.Priority = Ui.Get(sPriority);
            s.Notes = sNotes.Text;
            list.RefreshSelected();
        }

        void ShowOutput()
        {
            ScriptJob s = list.Selected;
            if (s == null || s.Id.Length == 0) return;
            string key = "script:" + s.Id;
            M.Async(this, delegate { return M.Call("job.lines", "key", key); }, delegate(JObject r, Exception ex)
            {
                if (ex != null) { Ui.Error(FindForm(), "Cannot get the output: " + ex.Message); return; }
                List<object> lines = r.Get("lines") as List<object>;
                StringBuilder sb = new StringBuilder();
                if (lines != null) foreach (object l in lines) sb.Append(l as string).Append('\n');
                JobStatus j = M.Job(key);
                string head = j != null && j.LastEnd > 0 ? "Last run " + Ui.When(j.LastEnd) + ": " + j.LastText : "No run since the service started";
                ResultDialog.Display(FindForm(), "Script output", head, sb.Length > 0 ? sb.ToString() : "(no output recorded)");
            });
        }

        public override void Tick()
        {
            run.Refresh2();
            list.RefreshAll();
        }

        protected override void OnDirtyChanged() { if (run != null) run.Refresh2(); }

        protected override void LoadFrom(AppConfig c)
        {
            list.SetItems(Json.CloneList(c.Scripts));
        }

        protected override void Collect(AppConfig c)
        {
            c.Scripts = Json.CloneList(list.Items);
        }
    }
}
