// The manager window: pages on the left, the selected page on the right, a banner for what needs attention, and
// Save / Discard at the bottom (one save for all pages).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Watchkeeper.UI
{
    public class MainForm : Form
    {
        public readonly Manager M;
        public readonly List<Page> Pages = new List<Page>();
        readonly ListBox nav = new ListBox();
        readonly Panel content = new Panel();
        readonly Panel banner = new Panel();
        readonly Label bannerText = new Label();
        readonly Button bannerButton;
        readonly Button save, discard;
        readonly Label problems = new Label();
        readonly Timer timer = new Timer();
        Page shown;

        public MainForm(Manager m)
        {
            M = m;
            Text = "Watchkeeper " + Engine.Version + " - " + Environment.MachineName;
            Font = Ui.BaseFont();
            AutoScaleMode = AutoScaleMode.Font;
            BackColor = Ui.Back;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            int em = Font.Height;
            ClientSize = new Size(Math.Min(work.Width - 40, em * 92), Math.Min(work.Height - 40, em * 56));
            MinimumSize = new Size(Math.Min(work.Width, em * 60), Math.Min(work.Height, em * 32));
            StartPosition = FormStartPosition.CenterScreen;

            Pages.Add(new OverviewPage(m, this));
            Pages.Add(new WatchdogPage(m));
            Pages.Add(new FoldersPage(m));
            Pages.Add(new VerticalPage(m));
            Pages.Add(new GpioPage(m));
            Pages.Add(new AlertsPage(m));
            Pages.Add(new SettingsPage(m));
            Pages.Add(new LogPage(m));

            nav.Dock = DockStyle.Left;
            nav.Width = em * 13;
            nav.BorderStyle = BorderStyle.None;
            nav.BackColor = Ui.Side;
            nav.DrawMode = DrawMode.OwnerDrawFixed;
            nav.ItemHeight = em * 2 + 6;
            nav.IntegralHeight = false;
            foreach (Page p in Pages) nav.Items.Add(p);
            nav.DrawItem += DrawNav;
            nav.SelectedIndexChanged += delegate { if (nav.SelectedIndex >= 0) ShowPage(Pages[nav.SelectedIndex]); };

            banner.Dock = DockStyle.Top;
            banner.Height = em * 2 + 14;
            banner.Padding = new Padding(12, 6, 12, 6);
            banner.Visible = false;
            bannerText.AutoSize = false;
            bannerText.Dock = DockStyle.Fill;
            bannerText.TextAlign = ContentAlignment.MiddleLeft;
            bannerButton = Ui.Button("", delegate { BannerAction(); });
            bannerButton.Dock = DockStyle.Right;
            banner.Controls.Add(bannerText);
            banner.Controls.Add(bannerButton);

            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = em * 2 + 18;
            bottom.Padding = new Padding(12, 8, 12, 8);
            bottom.BackColor = Ui.Side;
            save = Ui.Button("Save changes", delegate { SaveAll(); });
            discard = Ui.Button("Discard changes", delegate { if (Ui.Confirm(this, "Discard all changes since the last save?")) M.Load(); });
            save.Dock = DockStyle.Right;
            discard.Dock = DockStyle.Right;
            problems.Dock = DockStyle.Fill;
            problems.TextAlign = ContentAlignment.MiddleLeft;
            problems.Cursor = Cursors.Hand;
            problems.Click += delegate { ShowProblems(); };
            bottom.Controls.Add(problems);
            bottom.Controls.Add(discard);
            bottom.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 });
            bottom.Controls.Add(save);

            content.Dock = DockStyle.Fill;
            Controls.Add(content);
            Controls.Add(banner);
            Controls.Add(nav);
            Controls.Add(bottom);

            M.Changed += UpdateBar;
            M.Reloaded += delegate { foreach (Page p in Pages) p.Reload(); };
            timer.Interval = 2000;
            timer.Tick += delegate { Poll(); };
            Load += delegate
            {
                M.Load();
                Poll();
                nav.SelectedIndex = 0;
                timer.Start();
                if (M.LoadSource == "imported")
                    Ui.Info(this, "The settings of the previous version were converted. What could not be carried over is listed on the Settings page (Import report).");
            };
            FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (M.Busy && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; return; }     // closes once the service has answered
                if (!M.Dirty) return;
                if (Ui.MessageHook != null) return;
                DialogResult r = MessageBox.Show(this, "Save the changes before closing?", "Watchkeeper", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (r == DialogResult.Cancel || (r == DialogResult.Yes && !M.Save(this))) e.Cancel = true;
            };
        }

        public void ShowPage(Page p)
        {
            if (shown == p) return;
            content.SuspendLayout();
            content.Controls.Clear();
            content.Controls.Add(p);
            content.ResumeLayout();
            shown = p;
            int i = Pages.IndexOf(p);
            if (nav.SelectedIndex != i) nav.SelectedIndex = i;
            if (M.Status != null) p.Live(M.Status);
        }

        public Page PageByKey(string key) { return Pages.Find(delegate(Page p) { return p.Key == key; }); }

        public void SaveAll()
        {
            try { M.Save(this); }
            catch (Exception ex) { Ui.Error(this, "The settings could not be saved: " + ex.Message); }
        }

        public void Poll()
        {
            M.PollStatus();
            if (M.Status != null) foreach (Page p in Pages) { try { if (p == shown || p is OverviewPage) p.Live(M.Status); } catch (Exception) { } }
            nav.Invalidate();
            UpdateBanner();
        }

        string bannerMode = "";

        void UpdateBanner()
        {
            string text = null, button = null, mode = "";
            Color color = Ui.Warn;
            Setup.ServiceInfo svc = Util.IsWindows ? Setup.Query() : null;
            if (M.LoadError != null && M.LoadError.Contains("version 3"))
            {
                text = M.LoadError + ".";
                color = Ui.Warn;
                if (svc != null && !svc.Installed) { button = "Install service"; mode = "install"; }
                else if (svc != null && svc.Status != "running") { button = "Start service"; mode = "start"; }
                else { button = "Reload"; mode = "reload"; }
            }
            else if (M.LoadError != null)
            {
                text = "config.json cannot be read (" + M.LoadError + "). Shown here: " + (M.Edit != null ? "the newest backup" : "empty settings") + ". Saving replaces the damaged file.";
                color = Ui.Bad;
            }
            else if (svc != null && !svc.Installed)
            {
                text = "The Watchkeeper service is not installed: nothing runs until it is.";
                button = "Install service"; mode = "install"; color = Ui.Bad;
            }
            else if (svc != null && svc.Status != "running")
            {
                text = "The Watchkeeper service is " + svc.Status + ": nothing runs until it is started.";
                button = "Start service"; mode = "start"; color = Ui.Bad;
            }
            else if (!M.ServiceAlive)
            {
                text = M.Status == null ? "Waiting for the service's first status..." : "The service has not reported for " + Util.FmtDuration(Util.Now() - M.Status.Updated) + ".";
            }
            else if (M.Status.SettingsError.Length > 0)
            {
                text = "The service cannot read config.json: " + M.Status.SettingsError;
                color = Ui.Bad;
            }
            bannerMode = mode;
            banner.Visible = text != null;
            if (text == null) return;
            banner.BackColor = Color.FromArgb(40, color);
            bannerText.Text = text;
            bannerText.ForeColor = color;
            bannerButton.Visible = button != null;
            if (button != null) bannerButton.Text = button;
        }

        void BannerAction()
        {
            try
            {
                if (bannerMode == "install")
                {
                    List<string> done = Setup.Install(Application.ExecutablePath, null, false);
                    Ui.ShowText(this, "Service installed", string.Join("\n", done.ToArray()));
                    if (M.LoadError != null && M.LoadError.Contains("version 3")) { System.Threading.Thread.Sleep(3000); M.Load(); }
                }
                else if (bannerMode == "start")
                {
                    Setup.StartService();
                    if (M.LoadError != null && M.LoadError.Contains("version 3")) { System.Threading.Thread.Sleep(3000); M.Load(); }
                }
                else if (bannerMode == "reload") M.Load();
            }
            catch (Exception ex) { Ui.Error(this, ex.Message); }
            Poll();
        }

        void UpdateBar()
        {
            bool dirty = M.Dirty;
            save.Enabled = dirty;
            discard.Enabled = dirty;
            save.Font = dirty ? new Font(Ui.BaseFont(), FontStyle.Bold) : Ui.BaseFont();
            List<Problem> list = Watchkeeper.Validate.All(M.Edit);
            int errors = list.FindAll(delegate(Problem p) { return p.Level == "error"; }).Count;
            int warnings = list.Count - errors;
            if (list.Count == 0) { problems.Text = dirty ? "Unsaved changes." : "All changes are saved."; problems.ForeColor = Ui.Help; }
            else
            {
                problems.Text = (errors > 0 ? errors + " problem" + (errors == 1 ? "" : "s") : "") + (errors > 0 && warnings > 0 ? ", " : "") +
                                (warnings > 0 ? warnings + " warning" + (warnings == 1 ? "" : "s") : "") + " in the settings (click to see)" + (dirty ? ". Unsaved changes." : ".");
                problems.ForeColor = errors > 0 ? Ui.Bad : Ui.Warn;
            }
            foreach (Page p in Pages) { if (p is WatchdogPage) ((WatchdogPage)p).RefreshTitles(); }
        }

        void ShowProblems()
        {
            List<Problem> list = Watchkeeper.Validate.All(M.Edit);
            if (list.Count == 0) { Ui.Info(this, "No problems found in the settings."); return; }
            Ui.ShowText(this, "Problems in the settings", string.Join("\n", list.ConvertAll(delegate(Problem p) { return (p.Level == "error" ? "PROBLEM  " : "warning  ") + p; }).ToArray()));
        }

        void DrawNav(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            Page p = Pages[e.Index];
            bool sel = e.Index == nav.SelectedIndex;
            using (SolidBrush b = new SolidBrush(sel ? Color.White : Ui.Side)) e.Graphics.FillRectangle(b, e.Bounds);
            if (sel) using (SolidBrush a = new SolidBrush(Ui.Accent)) e.Graphics.FillRectangle(a, e.Bounds.Left, e.Bounds.Top, 4, e.Bounds.Height);
            Color dot = Ui.Off;
            try { if (M.Status != null && M.ServiceAlive) dot = p.Dot(M.Status); } catch (Exception) { }
            int h = e.Bounds.Height;
            using (SolidBrush d = new SolidBrush(dot)) e.Graphics.FillEllipse(d, e.Bounds.Left + 14, e.Bounds.Top + h / 2 - 4, 8, 8);
            TextRenderer.DrawText(e.Graphics, p.Title, sel ? new Font(Ui.BaseFont(), FontStyle.Bold) : Ui.BaseFont(),
                                  new Rectangle(e.Bounds.Left + 30, e.Bounds.Top, e.Bounds.Width - 30, h), Color.Black, TextFormatFlags.VerticalCenter);
        }
    }
}
