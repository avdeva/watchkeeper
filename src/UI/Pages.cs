// Page base classes: every page has a title; settings pages add a Save / Discard bar.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Watchkeeper.UI
{
    public abstract class Page : UserControl
    {
        protected readonly Manager M;
        readonly Label title = new Label();
        readonly Label subtitle = new Label();
        protected readonly TableLayoutPanel Header = new TableLayoutPanel();

        protected Page(Manager m, string heading, string description)
        {
            M = m;
            Dock = DockStyle.Fill;
            Header.Dock = DockStyle.Top;
            Header.AutoSize = true;
            Header.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Header.ColumnCount = 1;
            Header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Header.RowCount = 2;
            Header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Header.Padding = new Padding(20, 16, 20, 10);

            title.Text = heading;
            title.UseMnemonic = false;
            subtitle.UseMnemonic = false;
            title.AutoSize = true;
            title.Margin = new Padding(0);
            title.Font = Ui.Derive(Ui.BaseFont(), 1.55f, FontStyle.Regular);
            subtitle.Text = description;
            subtitle.AutoSize = true;
            subtitle.MaximumSize = new Size(820, 0);
            subtitle.ForeColor = SystemColors.GrayText;
            subtitle.Margin = new Padding(0, 4, 0, 0);
            Header.Controls.Add(title, 0, 0);
            Header.Controls.Add(subtitle, 0, 1);
            Controls.Add(Header);
        }

        public string Heading { get { return title.Text; } }
        public virtual bool Dirty { get { return false; } }
        public virtual void Reload() { }
        /// <summary>Called when any configuration section was saved: refresh only if this page's data changed.</summary>
        public virtual void ReloadIfChanged() { Reload(); }
        public virtual bool Save() { return true; }
        public virtual void Discard() { Reload(); }
        /// <summary>Called about once a second while the page is visible.</summary>
        public virtual void Tick() { }
        public virtual void OnShown() { }
    }

    /// <summary>Page that edits one configuration section with an explicit Save / Discard bar.</summary>
    public abstract class SectionPage : Page
    {
        readonly string section;
        readonly Panel bar = new Panel();
        readonly Label status = new Label();
        readonly Label errors = new Label();
        readonly Button save, discard;
        protected readonly Panel Body = new Panel();
        bool loading;
        bool dirty;
        string loadedJson = "";
        readonly System.Windows.Forms.Timer savedFade = new System.Windows.Forms.Timer();

        protected SectionPage(Manager m, string section, string heading, string description)
            : base(m, heading, description)
        {
            this.section = section;

            bar.Dock = DockStyle.Bottom;
            bar.Height = 52;
            bar.Padding = new Padding(20, 10, 20, 10);
            bar.BackColor = SystemColors.ControlLight;

            save = Ui.Btn("Save", delegate { Save(); });
            save.Dock = DockStyle.Right;
            save.Font = Ui.Derive(Ui.BaseFont(), 1.0f, FontStyle.Bold);
            save.Margin = new Padding(0);
            discard = Ui.Btn("Discard changes", delegate { Discard(); });
            discard.Dock = DockStyle.Right;
            discard.Margin = new Padding(0);
            Panel gap = new Panel();
            gap.Width = 8;
            gap.Dock = DockStyle.Right;
            status.Dock = DockStyle.Fill;
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.AutoEllipsis = true;
            bar.Controls.Add(status);
            bar.Controls.Add(discard);
            bar.Controls.Add(gap);
            bar.Controls.Add(save);

            errors.Dock = DockStyle.Bottom;
            errors.AutoSize = true;
            errors.MaximumSize = new Size(900, 0);
            errors.ForeColor = Ui.Bad;
            errors.Padding = new Padding(20, 6, 20, 6);
            errors.Visible = false;

            Body.Dock = DockStyle.Fill;
            Body.AutoScroll = true;
            Body.Padding = new Padding(20, 4, 20, 8);

            Controls.Add(Body);
            Controls.Add(errors);
            Controls.Add(bar);
            Controls.SetChildIndex(Header, Controls.Count - 1);
            Body.BringToFront();

            savedFade.Interval = 3000;
            savedFade.Tick += delegate { savedFade.Stop(); if (!dirty) { status.Text = ""; } };
            UpdateBar();
        }

        /// <summary>Call at the end of the derived constructor, after the controls exist.</summary>
        protected void Init()
        {
            Hook(Body);
            Reload();
        }

        protected abstract void LoadFrom(AppConfig c);
        protected abstract void Collect(AppConfig c);
        protected virtual void AfterSave() { }

        protected bool Loading { get { return loading; } }

        /// <summary>Run UI updates that must not count as the user editing the page.</summary>
        protected void Quiet(MethodInvoker action)
        {
            bool prev = loading;
            loading = true;
            try { action(); }
            finally { loading = prev; }
        }

        public override bool Dirty { get { return dirty; } }

        public override void Reload()
        {
            loading = true;
            try { LoadFrom(M.Config.Get()); }
            finally { loading = false; }
            loadedJson = SectionJson();
            SetDirty(false);
            errors.Visible = false;
            OnEdited();
        }

        string SectionJson()
        {
            try { return Json.Serialize(Json.ToJson(Cfg.GetSection(M.Config.Get(), section)), false); }
            catch (Exception) { return ""; }
        }

        public override void ReloadIfChanged()
        {
            if (dirty) return;
            string now = SectionJson();
            if (now.Length > 0 && now == loadedJson) return;    // nothing new for this page (for example after its own save)
            Reload();
        }

        public override void Discard()
        {
            Reload();
            status.ForeColor = SystemColors.GrayText;
            status.Text = "Changes discarded";
            savedFade.Stop();
            savedFade.Start();
        }

        public override bool Save()
        {
            AppConfig c = M.Config.Get();
            try { Collect(c); }
            catch (UiInputError ex)
            {
                ShowErrors(new List<string> { ex.Message });
                return false;
            }
            List<string> problems = M.Config.SaveSection(section, Cfg.GetSection(c, section));
            if (problems.Count > 0)
            {
                ShowErrors(problems);
                return false;
            }
            Reload();
            AfterSave();
            status.ForeColor = Ui.Ok;
            status.Text = "Saved";
            savedFade.Stop();
            savedFade.Start();
            return true;
        }

        void ShowErrors(List<string> problems)
        {
            errors.Text = "Not saved:\n• " + string.Join("\n• ", problems.ToArray());
            errors.Visible = true;
        }

        protected void MarkDirty()
        {
            if (loading) return;
            SetDirty(true);
            OnEdited();
        }

        /// <summary>Called after every edit and after every reload (used for live previews).</summary>
        protected virtual void OnEdited() { }

        void SetDirty(bool value)
        {
            dirty = value;
            UpdateBar();
            if (value)
            {
                status.ForeColor = Ui.Warn;
                status.Text = "Unsaved changes";
                errors.Visible = false;
            }
            else if (status.Text == "Unsaved changes") status.Text = "";
            OnDirtyChanged();
        }

        protected virtual void OnDirtyChanged() { }

        void UpdateBar()
        {
            save.Enabled = dirty;
            discard.Enabled = dirty;
        }

        void Hook(Control c)
        {
            if (c.Tag is string && (string)c.Tag == "nodirty") return;
            TextBox t = c as TextBox;
            if (t != null && !t.ReadOnly) t.TextChanged += delegate { MarkDirty(); };
            CheckBox cb = c as CheckBox;
            if (cb != null) cb.CheckedChanged += delegate { MarkDirty(); };
            RadioButton rb = c as RadioButton;
            if (rb != null) rb.CheckedChanged += delegate { MarkDirty(); };
            ComboBox cmb = c as ComboBox;
            if (cmb != null) cmb.SelectedIndexChanged += delegate { MarkDirty(); };
            NumericUpDown n = c as NumericUpDown;
            if (n != null) n.ValueChanged += delegate { MarkDirty(); };
            DateTimePicker dt = c as DateTimePicker;
            if (dt != null) dt.ValueChanged += delegate { MarkDirty(); };
            DataGridView grid = c as DataGridView;
            if (grid != null)
            {
                grid.CellValueChanged += delegate { MarkDirty(); };
                grid.RowsAdded += delegate { MarkDirty(); };
                grid.RowsRemoved += delegate { MarkDirty(); };
                grid.CurrentCellDirtyStateChanged += delegate
                {
                    if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
                };
            }
            foreach (Control child in c.Controls) Hook(child);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) savedFade.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>"Run now" / "Dry run" buttons for one job of the service, with the outcome of the last run.</summary>
    public class RunBar : FlowLayoutPanel
    {
        readonly Manager m;
        readonly Func<string> key;
        readonly Func<bool> canRun;
        readonly Button run, dry;
        readonly Label result = new Label();
        string pending = "";

        public RunBar(Manager m, Func<string> key, Func<bool> canRun)
        {
            this.m = m;
            this.key = key;
            this.canRun = canRun;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            WrapContents = false;
            Margin = new Padding(0, 4, 0, 4);
            Anchor = AnchorStyles.Left;
            run = Ui.Btn("Run now", delegate { Go(false); });
            dry = Ui.Btn("Dry run", delegate { Go(true); });
            result.AutoSize = true;
            result.MaximumSize = new Size(520, 0);
            result.Margin = new Padding(8, 7, 0, 0);
            Controls.Add(run);
            Controls.Add(dry);
            Controls.Add(result);
            Tag = "nodirty";
        }

        void Go(bool dryRun)
        {
            string k = key();
            if (string.IsNullOrEmpty(k)) return;
            EngineStatus st = m.Status;
            bool effectiveDry = dryRun || (st != null && st.DryRun);
            if (!effectiveDry && !Ui.Confirm(FindForm(), "Run this now? It really moves, copies or deletes files (or runs the script) using the saved settings.")) return;
            pending = "Starting...";
            Refresh2();
            m.Async(this, delegate { return m.Call("job.run", "key", k, "dry", dryRun); }, delegate(JObject r, Exception ex)
            {
                pending = ex != null ? "Not started: " + ex.Message : "";
                Refresh2();
            });
        }

        public void Refresh2()
        {
            string k = key();
            JobStatus v = string.IsNullOrEmpty(k) ? null : m.Job(k);
            bool online = m.Online;
            bool ok = canRun == null || canRun();
            run.Enabled = dry.Enabled = online && ok && v != null && !v.Running;
            EngineStatus st = m.Status;
            run.Text = st != null && st.DryRun ? "Run now (dry-run is on)" : "Run now";
            if (pending.Length > 0)
            {
                result.ForeColor = pending.StartsWith("Not") ? Ui.Bad : SystemColors.GrayText;
                result.Text = pending;
                if (v != null && v.Running) pending = "";
                return;
            }
            if (!online)
            {
                result.ForeColor = SystemColors.GrayText;
                result.Text = "The service is not running.";
            }
            else if (!ok)
            {
                result.ForeColor = SystemColors.GrayText;
                result.Text = "Save first; runs use the saved settings.";
            }
            else if (v == null)
            {
                result.ForeColor = SystemColors.GrayText;
                result.Text = "The service has not picked this up yet.";
            }
            else if (v.Running)
            {
                result.ForeColor = Ui.Warn;
                result.Text = "Running...";
            }
            else if (v.LastResult.Length > 0)
            {
                result.ForeColor = v.LastResult == "ok" ? Ui.Ok : Ui.Bad;
                result.Text = (v.LastDryRun ? "Dry run " : "Last run ") + Ui.When(v.LastEnd) + ": " + v.LastText;
            }
            else
            {
                result.ForeColor = SystemColors.GrayText;
                result.Text = "Has not run yet." + (v.NextRun > 0 ? " Next: " + Ui.When(v.NextRun) : "");
            }
        }
    }
}
