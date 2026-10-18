// Folder policies: the list of folders on the left; for the selected folder its settings,
// its rules (checked from the top, the first match decides) and a preview that asks the
// service what would happen right now.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Watchkeeper.UI
{
    public class FoldersPage : SectionPage
    {
        readonly ListEditor<FolderPolicy> folders = new ListEditor<FolderPolicy>("folder");
        readonly TabControl tabs = new TabControl();

        // folder fields
        readonly CheckBox fOn = Ui.Chk("Run this policy on its schedule");
        readonly TextBox fName = Ui.Text(260);
        readonly PathBox fPath = new PathBox(true, null, true);
        readonly PathBox fDest = new PathBox(true, null, true);
        readonly ScheduleEditor fSchedule = new ScheduleEditor();
        readonly CheckBox fSub = Ui.Chk("Include sub-folders");
        readonly CheckBox fEmpty = Ui.Chk("Remove sub-folders that end up empty");
        readonly NumericUpDown fMinAge = Ui.Num(0, 86400, 10, 0);
        readonly TextBox fNotes = Ui.Text(420);
        readonly RunBar run;

        // what the policy does (simple one-rule policies, or custom rules)
        readonly RadioButton mMove = Ui.Radio("Moved to the destination");
        readonly RadioButton mCopy = Ui.Radio("Copied to the destination");
        readonly RadioButton mDelete = Ui.Radio("Deleted once they are older than a set age (retention)");
        readonly RadioButton mCustom = Ui.Radio("Handled by custom rules (Rules tab)");
        readonly NumericUpDown mAge = Ui.Num(1, 1000000, 1, 0);
        readonly ComboBox mUnit;
        readonly ComboBox mConflict;
        readonly TextBox mPattern = Ui.Text(220);
        readonly Label mSummary = new Label();

        // rules
        readonly ListEditor<FileRule> rules = new ListEditor<FileRule>("rule", true, 0);
        readonly CheckBox rOn = Ui.Chk("Rule switched on");
        readonly TextBox rName = Ui.Text(260);
        readonly ComboBox rAction;
        readonly PathBox rDest = new PathBox(true, null, true);
        readonly CheckBox rKeepSub = Ui.Chk("Recreate sub-folders at the destination");
        readonly ComboBox rConflict;
        readonly CheckBox rStamp = Ui.Chk("Set Date Modified to the recording's end (time in the name + length)");
        readonly TextBox rPattern = Ui.Text(220);
        readonly TextBox rExclude = Ui.Text(220);
        readonly CheckBox rAgeOn = Ui.Chk("Only files older than");
        readonly NumericUpDown rAge = Ui.Num(0, 1000000, 1, 2);
        readonly ComboBox rUnit;
        readonly CheckBox rWindow = Ui.Chk("Only files whose time of day is in a window");
        readonly ComboBox rSource, rMode;
        readonly DateTimePicker rStart = Ui.TimeBox();
        readonly DateTimePicker rEnd = Ui.TimeBox();
        readonly NumericUpDown rMin = Ui.Num(0, 10000000, 1, 1);
        readonly NumericUpDown rMax = Ui.Num(0, 10000000, 1, 1);
        readonly NumericUpDown rNewest = Ui.Num(0, 1000000, 1, 0);
        readonly Label rSummary = new Label();

        // preview
        readonly Button previewBtn;
        readonly Label previewText = new Label();
        readonly ListView previewList = new ListView();

        int fill;

        public FoldersPage(Manager m)
            : base(m, "folders", "Folder policies",
                   "Each folder has its own schedule and rules. Every file is checked against the rules from the top; the first rule that matches decides what happens to it.")
        {
            rAction = Ui.Combo(new Choice("delete", "Delete"), new Choice("move", "Move to another folder"),
                               new Choice("copy", "Copy to another folder"), new Choice("keep", "Keep (protect from the rules below)"));
            rConflict = Ui.Combo(new Choice("overwrite", "Replace it"), new Choice("skip", "Leave the file where it is"),
                                 new Choice("rename", "Keep both (add a number)"));
            rUnit = Ui.Combo(new Choice("minutes", "minutes"), new Choice("hours", "hours"), new Choice("days", "days"), new Choice("weeks", "weeks"));
            rUnit.Width = 100;
            mUnit = Ui.Combo(new Choice("minutes", "minutes"), new Choice("hours", "hours"), new Choice("days", "days"), new Choice("weeks", "weeks"));
            mUnit.Width = 100;
            mConflict = Ui.Combo(new Choice("rename", "Keep both (add a number)"), new Choice("overwrite", "Replace it"),
                                 new Choice("skip", "Leave the file where it is"));
            rSource = Ui.Combo(new Choice("modified", "modified time"), new Choice("filename", "time in the name"));
            rMode = Ui.Combo(new Choice("inside", "is between"), new Choice("outside", "is outside"));
            previewBtn = Ui.Btn("Preview what this policy would do now", delegate { Preview(); });
            run = new RunBar(m, delegate { FolderPolicy f = folders.Selected; return f == null || f.Id.Length == 0 ? "" : "folder:" + f.Id; },
                             delegate { return !Dirty && Saved(folders.Selected); });

            // ---- folder list
            folders.EmptyText = "No folder policies yet.\r\n\r\nClick \"Add folder\" to manage a folder.";
            folders.RemoveNote = "Files in the folder are not touched.";
            folders.Create = delegate
            {
                FolderPolicy f = new FolderPolicy();
                f.Name = "New folder";
                f.Enabled = false;
                f.Schedule = new ScheduleCfg("interval", "00:00", 1);
                f.MinAgeSeconds = 60;
                PolicyMode.Apply(f, PolicyMode.Move, "*", 0, null, "rename");
                return f;
            };
            folders.Copy = delegate(FolderPolicy f)
            {
                FolderPolicy c = Json.Clone(f);
                c.Id = "";
                c.Name = f.Name + " (copy)";
                c.Enabled = false;
                foreach (FileRule r in c.Rules) r.Id = "";
                return c;
            };
            folders.Title = delegate(FolderPolicy f) { return f.Name; };
            folders.Subtitle = delegate(FolderPolicy f)
            {
                if (f.Path.Length == 0) return "No source chosen yet";
                string mode = PolicyMode.Of(f);
                string where = (mode == PolicyMode.Move || mode == PolicyMode.Copy || (mode == PolicyMode.Custom && f.Destination.Length > 0))
                    ? f.Path + "  >  " + f.Destination : f.Path;
                return PolicyMode.Label(mode) + ":  " + where;
            };
            folders.Badge = delegate(FolderPolicy f)
            {
                if (!f.Enabled) return "";
                return JobManager.Describe(f.Schedule, true).Replace("Daily at ", "daily ");
            };
            folders.IsOn = delegate(FolderPolicy f) { return f.Enabled; };

            tabs.Dock = DockStyle.Fill;
            tabs.TabPages.Add(BuildFolderTab());
            tabs.TabPages.Add(BuildRulesTab());
            tabs.TabPages.Add(BuildPreviewTab());
            tabs.SelectedIndexChanged += delegate { if (tabs.SelectedIndex == 0) ShowMode(); };
            folders.Detail.AutoScroll = false;
            folders.Detail.Controls.Add(tabs);

            folders.SelectedChanged += delegate { FillFolder(); };
            folders.Changed += delegate { MarkDirty(); };
            rules.SelectedChanged += delegate { FillRule(); };
            rules.Changed += delegate { MarkDirty(); folders.RefreshSelected(); };

            Body.AutoScroll = false;
            Body.Controls.Add(folders);
            Init();
        }

        bool Saved(FolderPolicy f)
        {
            if (f == null || f.Id.Length == 0) return false;
            return M.Config.Get().Folders.Exists(delegate(FolderPolicy x) { return x.Id == f.Id; });
        }

        static TabPage Tab(string title, Control content, bool scroll)
        {
            TabPage p = new TabPage(title);
            p.Padding = new Padding(10, 8, 6, 6);
            p.UseVisualStyleBackColor = true;
            if (scroll)
            {
                Panel s = new Panel();
                s.Dock = DockStyle.Fill;
                s.AutoScroll = true;
                s.MinimumSize = new Size(160, 120);
                s.Controls.Add(content);
                p.Controls.Add(s);
            }
            else p.Controls.Add(content);
            return p;
        }

        TabPage BuildFolderTab()
        {
            FormGrid g = new FormGrid();
            g.Row(null, fOn);
            g.Row("Name", fName);
            g.Row("Source", fPath, "The folder this policy works on, e.g. a cache-drive folder the recorders write to, or a data-drive folder for retention.");

            g.Header("What happens to the files");
            FlowLayoutPanel modes = new FlowLayoutPanel();
            modes.FlowDirection = FlowDirection.TopDown;
            modes.AutoSize = true;
            modes.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            modes.WrapContents = false;
            modes.Margin = new Padding(0);
            modes.Controls.Add(mMove);
            modes.Controls.Add(mCopy);
            modes.Controls.Add(mDelete);
            modes.Controls.Add(mCustom);
            g.Row("Files are", modes);
            g.Row("Destination", fDest, "Usually on the data drive. Not used when files are only deleted.");
            g.Row("Same name already there", mConflict);
            g.Row("Delete files older than", Ui.Flow(mAge, mUnit), "By Date Modified.");
            g.Row("Only names matching", mPattern, "Wildcards, e.g. *.mp4;*.mkv. * = every file.");
            mSummary.AutoSize = true;
            mSummary.MaximumSize = new Size(560, 0);
            mSummary.ForeColor = Ui.Accent;
            mSummary.Margin = new Padding(0, 6, 0, 4);
            mSummary.Tag = "nodirty";
            g.Row(null, mSummary);

            g.Header("When");
            g.Row("Runs", fSchedule);
            g.Row("Run it now", run);

            g.Header("Safety");
            g.Row("Sub-folders", Ui.Flow(fSub, fEmpty), "Hidden and system files, and folder links, are never touched.");
            Label secs = Ui.Lbl("seconds");
            g.Row("Leave files changed in the last", Ui.Flow(fMinAge, secs), "Protects files that are still being recorded or copied. Files open in another program are always left alone.");
            g.Row("Notes", fNotes);

            // these change the rules (ApplyMode marks the page dirty); switching to "custom" changes nothing
            mMove.Tag = mCopy.Tag = mDelete.Tag = mCustom.Tag = mAge.Tag = mUnit.Tag = mConflict.Tag = mPattern.Tag = "nodirty";
            EventHandler modeChanged = delegate(object sender, EventArgs e)
            {
                RadioButton rb = sender as RadioButton;
                if (rb != null && !rb.Checked) return;      // the radio being unchecked; its partner does the work
                ModeChanged();
            };
            mMove.CheckedChanged += modeChanged;
            mCopy.CheckedChanged += modeChanged;
            mDelete.CheckedChanged += modeChanged;
            mCustom.CheckedChanged += modeChanged;
            EventHandler modeField = delegate { ModeFieldChanged(); };
            mAge.ValueChanged += modeField;
            mUnit.SelectedIndexChanged += modeField;
            mConflict.SelectedIndexChanged += modeField;
            mPattern.TextChanged += modeField;

            fOn.CheckedChanged += delegate { ApplyFolder(); };
            fName.TextChanged += delegate { ApplyFolder(); };
            fPath.ValueChanged += delegate { ApplyFolder(); };
            fPath.Picked += delegate { NameFromFolder(); };
            fDest.ValueChanged += delegate { ApplyFolder(); UpdateModeUi(); };
            fSchedule.ValueChanged += delegate { ApplyFolder(); };
            fSub.CheckedChanged += delegate { fEmpty.Enabled = fSub.Checked; ApplyFolder(); };
            fEmpty.CheckedChanged += delegate { ApplyFolder(); };
            fMinAge.ValueChanged += delegate { ApplyFolder(); };
            fNotes.TextChanged += delegate { ApplyFolder(); };
            return Tab("Folder", g, true);
        }

        TabPage BuildRulesTab()
        {
            rules.EmptyText = "No rules: nothing happens in this folder.\r\n\r\nClick \"Add rule\".";
            rules.Create = delegate
            {
                FileRule r = new FileRule();
                r.Name = "New rule";
                r.Action = "move";
                r.OnConflict = "rename";
                return r;
            };
            rules.Copy = delegate(FileRule r)
            {
                FileRule c = Json.Clone(r);
                c.Id = "";
                c.Name = r.Name + " (copy)";
                return c;
            };
            rules.Title = delegate(FileRule r) { return r.Name; };
            rules.Subtitle = delegate(FileRule r) { return FolderRunner.Describe(r, EffectiveDest(r)); };
            rules.IsOn = delegate(FileRule r) { return r.Enabled; };
            rules.RemoveNote = "";

            FormGrid g = new FormGrid();
            g.Row(null, rOn);
            g.Row("Name", rName);
            g.Header("What happens");
            g.Row("Action", rAction);
            g.Row("Destination", rDest, "Empty = the policy's destination (Folder tab).");
            g.Row("Same name already there", rConflict);
            g.Row(null, rKeepSub);
            g.Row(null, rStamp);
            g.Header("Which files (every file unless narrowed here)");
            g.Row("Names matching", rPattern, "Wildcards, e.g. *.mp4;*.mkv. A single * means every file.");
            g.Row("Except", rExclude);
            Label byModified = Ui.Lbl("by Date Modified");
            g.Row("Age", Ui.Flow(rAgeOn, rAge, rUnit, byModified));
            Label and = Ui.Lbl("and");
            and.Margin = new Padding(4, 6, 4, 6);
            g.Row("Time of day", rWindow);
            g.Row("", Ui.Flow(rSource, rMode, rStart, and, rEnd), "Date Modified, or the recording time in names like \"... 23 July 2026 - 08-29-03 AM.mp4\". 22:00 - 02:00 wraps past midnight.");
            Label mb1 = Ui.Lbl("MB, at most");
            Label mb2 = Ui.Lbl("MB (0 = no limit)");
            g.Row("Size at least", Ui.Flow(rMin, mb1, rMax, mb2));
            Label newest = Ui.Lbl("newest matching files (0 = none)");
            g.Row("Always leave the", Ui.Flow(rNewest, newest));
            rSummary.AutoSize = true;
            rSummary.MaximumSize = new Size(560, 0);
            rSummary.ForeColor = Ui.Accent;
            rSummary.Margin = new Padding(0, 10, 0, 0);
            rSummary.Tag = "nodirty";
            g.Row(null, rSummary);
            rules.Detail.Controls.Add(g);

            rOn.CheckedChanged += delegate { ApplyRule(); };
            rName.TextChanged += delegate { ApplyRule(); };
            rAction.SelectedIndexChanged += delegate { ApplyRule(); };
            rDest.ValueChanged += delegate { ApplyRule(); };
            rKeepSub.CheckedChanged += delegate { ApplyRule(); };
            rConflict.SelectedIndexChanged += delegate { ApplyRule(); };
            rStamp.CheckedChanged += delegate { ApplyRule(); };
            rPattern.TextChanged += delegate { ApplyRule(); };
            rExclude.TextChanged += delegate { ApplyRule(); };
            rAgeOn.CheckedChanged += delegate
            {
                if (rAgeOn.Checked && rAge.Value == 0 && fill == 0 && !Loading)
                {
                    rAge.Value = 7;
                    Ui.Set(rUnit, "days");
                }
                ApplyRule();
            };
            rAge.ValueChanged += delegate { ApplyRule(); };
            rUnit.SelectedIndexChanged += delegate { ApplyRule(); };
            rWindow.CheckedChanged += delegate { ApplyRule(); };
            rSource.SelectedIndexChanged += delegate { ApplyRule(); };
            rMode.SelectedIndexChanged += delegate { ApplyRule(); };
            rStart.ValueChanged += delegate { ApplyRule(); };
            rEnd.ValueChanged += delegate { ApplyRule(); };
            rMin.ValueChanged += delegate { ApplyRule(); };
            rMax.ValueChanged += delegate { ApplyRule(); };
            rNewest.ValueChanged += delegate { ApplyRule(); };
            return Tab("Rules", rules, false);
        }

        TabPage BuildPreviewTab()
        {
            Panel top = new Panel();
            top.Dock = DockStyle.Top;
            top.Height = Ui.BaseFont().Height * 5 + 24;
            previewBtn.Location = new Point(0, 0);
            previewBtn.Tag = "nodirty";
            previewText.Location = new Point(0, previewBtn.PreferredSize.Height + 8);
            previewText.AutoSize = true;
            previewText.MaximumSize = new Size(620, 0);
            previewText.ForeColor = SystemColors.GrayText;
            previewText.Text = "Runs the rules shown here (including unsaved changes) inside the service, with its permissions. Nothing is changed.";
            top.Controls.Add(previewBtn);
            top.Controls.Add(previewText);

            previewList.Dock = DockStyle.Fill;
            previewList.View = View.Details;
            previewList.FullRowSelect = true;
            previewList.HideSelection = false;
            previewList.Tag = "nodirty";
            previewList.Columns.Add("File", 240);
            previewList.Columns.Add("Would be", 90);
            previewList.Columns.Add("Rule", 150);
            previewList.Columns.Add("Destination / note", 320);

            Panel p = new Panel();
            p.Dock = DockStyle.Fill;
            p.Controls.Add(previewList);
            p.Controls.Add(top);
            return Tab("Preview", p, false);
        }

        // ------------------------------------------------------------- folder fields
        void NameFromFolder()
        {
            string n = fName.Text.Trim();
            if (n.Length > 0 && n != "New folder") return;
            string p = fPath.Text.Trim().Trim('"').TrimEnd('\\', '/');
            int i = Math.Max(p.LastIndexOf('\\'), p.LastIndexOf('/'));
            if (p.Length > 0) fName.Text = i >= 0 && i < p.Length - 1 ? p.Substring(i + 1) : p;
        }

        void FillFolder()
        {
            FolderPolicy f = folders.Selected;
            fill++;
            try
            {
                Quiet(delegate
                {
                    fOn.Checked = f != null && f.Enabled;
                    fName.Text = f != null ? f.Name : "";
                    fPath.Text = f != null ? f.Path : "";
                    fDest.Text = f != null ? f.Destination : "";
                    fSchedule.Load(f != null ? f.Schedule : new ScheduleCfg());
                    fSub.Checked = f != null && f.Subfolders;
                    fEmpty.Checked = f != null && f.RemoveEmptyFolders;
                    fEmpty.Enabled = fSub.Checked;
                    Ui.SetNum(fMinAge, f != null ? f.MinAgeSeconds : 120);
                    fNotes.Text = f != null ? f.Notes : "";
                    rules.SetItems(f != null ? f.Rules : new List<FileRule>());
                });
            }
            finally { fill--; }
            ShowMode();
            previewList.Items.Clear();
            run.Refresh2();
        }

        void ApplyFolder()
        {
            if (fill > 0 || Loading) return;
            FolderPolicy f = folders.Selected;
            if (f == null) return;
            f.Enabled = fOn.Checked;
            f.Name = fName.Text;
            f.Path = fPath.Text;
            f.Destination = fDest.Text;
            rules.RefreshAll();
            f.Schedule = fSchedule.Value();
            f.Subfolders = fSub.Checked;
            f.RemoveEmptyFolders = fEmpty.Checked;
            f.MinAgeSeconds = (int)fMinAge.Value;
            f.Notes = fNotes.Text;
            folders.RefreshSelected();
        }

        // ------------------------------------------------------- what the policy does
        string CurrentMode()
        {
            return mMove.Checked ? PolicyMode.Move : mCopy.Checked ? PolicyMode.Copy
                 : mDelete.Checked ? PolicyMode.Delete : PolicyMode.Custom;
        }

        /// <summary>Shows the selected policy's mode (derived from its rules) without changing anything.</summary>
        void ShowMode()
        {
            FolderPolicy f = folders.Selected;
            string mode = f == null ? PolicyMode.Move : PolicyMode.Of(f);
            fill++;
            try
            {
                Quiet(delegate
                {
                    mMove.Checked = mode == PolicyMode.Move;
                    mCopy.Checked = mode == PolicyMode.Copy;
                    mDelete.Checked = mode == PolicyMode.Delete;
                    mCustom.Checked = mode == PolicyMode.Custom;
                    FileRule r = mode != PolicyMode.Custom && f != null ? f.Rules[0] : null;
                    mPattern.Text = r != null ? r.Pattern : "*";
                    Ui.SetNum(mAge, r != null && mode == PolicyMode.Delete ? r.OlderThan : 7);
                    Ui.Set(mUnit, r != null && mode == PolicyMode.Delete ? r.OlderThanUnit : "days");
                    Ui.Set(mConflict, r != null && mode != PolicyMode.Delete ? r.OnConflict : "rename");
                });
            }
            finally { fill--; }
            UpdateModeUi();
        }

        void UpdateModeUi()
        {
            string m = CurrentMode();
            bool simple = m != PolicyMode.Custom;
            mAge.Enabled = mUnit.Enabled = m == PolicyMode.Delete;
            mConflict.Enabled = m == PolicyMode.Move || m == PolicyMode.Copy;
            mPattern.Enabled = simple;
            fDest.Enabled = m != PolicyMode.Delete;
            FolderPolicy f = folders.Selected;
            if (f == null) { mSummary.Text = ""; return; }
            if (simple && f.Rules.Count == 1)
            {
                mSummary.Text = FolderRunner.Describe(f.Rules[0], Cfg.RuleDestination(f, f.Rules[0])) + ". Nothing else in the folder is touched.";
                return;
            }
            StringBuilder sb = new StringBuilder();
            if (f.Rules.Count == 0) sb.Append("No rules yet: nothing happens. Add them on the Rules tab.");
            else
            {
                sb.Append(f.Rules.Count == 1 ? "1 rule" : f.Rules.Count + " rules, the first match decides").Append(" (Rules tab):");
                for (int i = 0; i < f.Rules.Count && i < 4; i++)
                    sb.Append("\n").Append(i + 1).Append(". ").Append(f.Rules[i].Enabled ? "" : "(off) ")
                      .Append(FolderRunner.Describe(f.Rules[i], Cfg.RuleDestination(f, f.Rules[i])));
                if (f.Rules.Count > 4) sb.Append("\n...");
            }
            mSummary.Text = sb.ToString();
        }

        void ModeChanged()
        {
            UpdateModeUi();
            if (fill > 0 || Loading) return;
            FolderPolicy f = folders.Selected;
            if (f == null) return;
            string m = CurrentMode();
            if (m != PolicyMode.Custom && PolicyMode.Of(f) == PolicyMode.Custom && f.Rules.Count > 0)
            {
                string what = m == PolicyMode.Delete ? "delete files older than " + FileOps.DescribeAge((double)mAge.Value, Ui.Get(mUnit))
                            : m == PolicyMode.Move ? "move files to the destination" : "copy files to the destination";
                if (!Ui.Confirm(FindForm(), "This policy has " + (f.Rules.Count == 1 ? "a custom rule" : f.Rules.Count + " custom rules") +
                                ". Replace " + (f.Rules.Count == 1 ? "it" : "them") + " with one rule that will " + what + "?"))
                {
                    fill++;
                    try { Quiet(delegate { mCustom.Checked = true; }); }
                    finally { fill--; }
                    UpdateModeUi();
                    return;
                }
            }
            if (m == PolicyMode.Custom)
            {
                UpdateModeUi();
                return;     // the rules stay as they are; the Rules tab edits them
            }
            ApplyMode();
        }

        void ModeFieldChanged()
        {
            if (fill > 0 || Loading) return;
            if (CurrentMode() == PolicyMode.Custom) return;
            ApplyMode();
        }

        void ApplyMode()
        {
            FolderPolicy f = folders.Selected;
            if (f == null) return;
            PolicyMode.Apply(f, CurrentMode(), mPattern.Text, (double)mAge.Value, Ui.Get(mUnit), Ui.Get(mConflict));
            rules.SetItems(f.Rules);
            MarkDirty();
            folders.RefreshSelected();
            UpdateModeUi();
        }

        // --------------------------------------------------------------- rule fields
        void FillRule()
        {
            FileRule r = rules.Selected;
            fill++;
            try
            {
                Quiet(delegate
                {
                    FileRule d = r ?? new FileRule();
                    rOn.Checked = d.Enabled;
                    rName.Text = r != null ? d.Name : "";
                    Ui.Set(rAction, d.Action);
                    rDest.Text = d.Destination;
                    rKeepSub.Checked = d.KeepSubfolders;
                    Ui.Set(rConflict, d.OnConflict);
                    rStamp.Checked = d.StampFromName;
                    rPattern.Text = d.Pattern;
                    rExclude.Text = d.Exclude;
                    rAgeOn.Checked = d.OlderThan > 0;
                    Ui.SetNum(rAge, d.OlderThan);
                    Ui.Set(rUnit, d.OlderThanUnit);
                    rWindow.Checked = d.UseWindow;
                    Ui.Set(rSource, d.WindowSource);
                    Ui.Set(rMode, d.WindowMode);
                    Ui.SetTime(rStart, d.WindowStart);
                    Ui.SetTime(rEnd, d.WindowEnd);
                    Ui.SetNum(rMin, d.MinSizeMb);
                    Ui.SetNum(rMax, d.MaxSizeMb);
                    Ui.SetNum(rNewest, d.KeepNewest);
                });
            }
            finally { fill--; }
            UpdateRuleUi();
        }

        string EffectiveDest(FileRule r)
        {
            FolderPolicy f = folders.Selected;
            return f == null ? r.Destination : Cfg.RuleDestination(f, r);
        }

        void UpdateRuleUi()
        {
            string a = Ui.Get(rAction);
            bool transfer = a == "move" || a == "copy";
            rDest.Enabled = rConflict.Enabled = rStamp.Enabled = transfer;
            rKeepSub.Enabled = transfer && fSub.Checked;
            rSource.Enabled = rMode.Enabled = rStart.Enabled = rEnd.Enabled = rWindow.Checked;
            rAge.Enabled = rUnit.Enabled = rAgeOn.Checked;
            FileRule r = rules.Selected;
            rSummary.Text = r == null ? "" : FolderRunner.Describe(r, EffectiveDest(r)) + ".";
        }

        void ApplyRule()
        {
            if (fill > 0 || Loading) { UpdateRuleUi(); return; }
            FileRule r = rules.Selected;
            if (r == null) return;
            r.Enabled = rOn.Checked;
            r.Name = rName.Text;
            r.Action = Ui.Get(rAction);
            r.Destination = rDest.Text;
            r.KeepSubfolders = rKeepSub.Checked;
            r.OnConflict = Ui.Get(rConflict);
            r.StampFromName = rStamp.Checked;
            r.Pattern = rPattern.Text;
            r.Exclude = rExclude.Text;
            r.OlderThan = rAgeOn.Checked ? (double)rAge.Value : 0;
            r.OlderThanUnit = Ui.Get(rUnit);
            r.UseWindow = rWindow.Checked;
            r.WindowSource = Ui.Get(rSource);
            r.WindowMode = Ui.Get(rMode);
            r.WindowStart = Ui.GetTime(rStart);
            r.WindowEnd = Ui.GetTime(rEnd);
            r.MinSizeMb = (double)rMin.Value;
            r.MaxSizeMb = (double)rMax.Value;
            r.KeepNewest = (int)rNewest.Value;
            rules.RefreshSelected();
            folders.RefreshSelected();
            UpdateRuleUi();
        }

        // ------------------------------------------------------------------ preview
        void Preview()
        {
            FolderPolicy f = folders.Selected;
            if (f == null) return;
            if (!M.Online)
            {
                previewText.ForeColor = Ui.Bad;
                previewText.Text = "The service is not running, so the preview cannot be made. " + (M.StatusError ?? "");
                return;
            }
            FolderPolicy snap = Json.Clone(f);
            previewBtn.Enabled = false;
            previewText.ForeColor = SystemColors.GrayText;
            previewText.Text = "Checking " + snap.Path + "...";
            previewList.Items.Clear();
            M.Async(this, delegate { return M.Client.Call(Req(snap), 3000); }, delegate(JObject r, Exception ex)
            {
                previewBtn.Enabled = true;
                if (ex != null)
                {
                    previewText.ForeColor = Ui.Bad;
                    previewText.Text = "Preview failed: " + ex.Message;
                    return;
                }
                PreviewResult p = (PreviewResult)Json.ConvertTo(typeof(PreviewResult), r.Get("preview"));
                ShowPreview(p);
            });
        }

        static JObject Req(FolderPolicy f)
        {
            JObject o = new JObject();
            o.Set("cmd", "folder.preview");
            o.Set("folder", Json.ToJson(f));
            o.Set("limit", 3000.0);
            return o;
        }

        static string ActionText(string a)
        {
            switch (a)
            {
                case "delete": return "deleted";
                case "move": return "moved";
                case "copy": return "copied";
                case "keep": return "kept";
                case "skip": return "skipped";
                case "none": return "untouched";
                case "failed": return "error";
            }
            return a;
        }

        void ShowPreview(PreviewResult p)
        {
            TaskResult t = p.Result;
            previewText.ForeColor = t.Ok ? SystemColors.ControlText : Ui.Bad;
            previewText.Text = (t.Ok ? "" : "Problem: ") + t.Text + "." + (p.Plan.Count >= 3000 ? " Only the first 3000 files are listed." : "");
            previewList.BeginUpdate();
            previewList.Items.Clear();
            List<PlanItem> ordered = new List<PlanItem>(p.Plan);
            // files that something happens to first
            ordered.Sort(delegate(PlanItem a, PlanItem b)
            {
                int ra = a.Action == "none" ? 1 : 0, rb = b.Action == "none" ? 1 : 0;
                return ra != rb ? ra.CompareTo(rb) : string.Compare(a.File, b.File, StringComparison.OrdinalIgnoreCase);
            });
            foreach (PlanItem i in ordered)
            {
                ListViewItem it = new ListViewItem(i.File);
                it.SubItems.Add(ActionText(i.Action));
                it.SubItems.Add(i.Rule);
                it.SubItems.Add(i.Destination.Length > 0 ? i.Destination + (i.Note.Length > 0 ? " (" + i.Note + ")" : "") : i.Note);
                if (i.Action == "delete") it.ForeColor = Ui.Bad;
                else if (i.Action == "move" || i.Action == "copy") it.ForeColor = Ui.Accent;
                else if (i.Action == "none" || i.Action == "skip" || i.Action == "keep") it.ForeColor = SystemColors.GrayText;
                previewList.Items.Add(it);
            }
            previewList.EndUpdate();
        }

        // -------------------------------------------------------------- load / save
        protected override void LoadFrom(AppConfig c)
        {
            folders.SetItems(Json.CloneList(c.Folders));
        }

        protected override void Collect(AppConfig c)
        {
            c.Folders = Json.CloneList(folders.Items);
        }

        public override void Tick()
        {
            run.Refresh2();
        }

        protected override void OnDirtyChanged() { if (run != null) run.Refresh2(); }
    }
}
