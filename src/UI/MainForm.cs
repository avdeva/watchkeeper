// Manager window: navigation list and page host. Closing it does not stop anything; the
// service keeps working.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace Watchkeeper.UI
{
    public class NavItem
    {
        public string Key;
        public string Title;
        public Page Page;
        public bool HasDot;
        public Color Dot = Ui.Off;
    }

    public class MainForm : Form
    {
        readonly Manager m;
        readonly ListBox nav = new ListBox();
        readonly Panel side = new Panel();
        readonly Panel brand = new Panel();
        readonly Panel host = new Panel();
        readonly List<NavItem> all = new List<NavItem>();
        List<NavItem> items = new List<NavItem>();       // the pages of the parts this server uses
        string modulesSig = "";
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        int current = -1;
        bool navigating;
        long errorsSeenUpTo;
        string dirtySig = "";

        public MainForm(Manager m, string firstPage)
        {
            this.m = m;
            Text = "Watchkeeper";
            Font = Ui.BaseFont();
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1280, 860);
            MinimumSize = new Size(960, 640);
            Icon = AppIcon();

            BuildPages();
            BuildNav();
            ApplyModules(true);
            host.Dock = DockStyle.Fill;
            foreach (NavItem it in all)
            {
                it.Page.Visible = false;
                host.Controls.Add(it.Page);
            }
            BuildBrand();
            nav.Dock = DockStyle.Fill;
            side.Dock = DockStyle.Left;
            side.Width = nav.Width;
            side.BackColor = nav.BackColor;
            side.Controls.Add(nav);
            side.Controls.Add(brand);
            Controls.Add(host);
            Controls.Add(side);
            side.BringToFront();
            host.BringToFront();

            m.Config.Changed += OnConfigChanged;
            timer.Interval = 1000;
            timer.Tick += delegate { OnTimer(); };

            int start = 0;
            if (!string.IsNullOrEmpty(firstPage))
            {
                for (int i = 0; i < items.Count; i++)
                    if (items[i].Key == firstPage || items[i].Title.ToLowerInvariant().StartsWith(firstPage.ToLowerInvariant())) start = i;
            }
            else if (!m.Service.Installed && Util.IsWindows) start = items.FindIndex(delegate(NavItem n) { return n.Key == "service"; });
            Select(Math.Max(0, start), true);
            timer.Start();
        }

        // ------------------------------------------------------------------ pages
        void BuildPages()
        {
            OverviewPage ov = new OverviewPage(m);
            ov.OpenPage = delegate(string key) { SelectKey(key); };
            Add("overview", "Overview", ov, true);
            Add("folders", "Folder policies", new FoldersPage(m), true);
            Add("programs", "Watchdog", new ProgramsPage(m), true);
            Add("scripts", "Scripts", new ScriptsPage(m), true);
            Add("vertical", "Vertical conversion", new VerticalPage(m), true);
            Add("gpio", "GPIO (Axia)", new GpioPage(m), true);
            Add("alerts", "Alerts", new AlertsPage(m), false);
            Add("service", "Service & settings", new ServicePage(m), true);
            Add("log", "Log", new LogPage(m), true);
        }

        void Add(string key, string title, Page page, bool dot)
        {
            NavItem it = new NavItem();
            it.Key = key;
            it.Title = title;
            it.Page = page;
            it.HasDot = dot;
            all.Add(it);
        }

        public Page PageFor(string key)
        {
            NavItem it = all.Find(delegate(NavItem n) { return n.Key == key; });
            return it != null ? it.Page : null;
        }

        static bool PartOn(string key, ModulesCfg mo)
        {
            switch (key)
            {
                case "folders": return mo.Folders;
                case "programs": return mo.Programs;
                case "scripts": return mo.Scripts;
                case "vertical": return mo.Vertical;
                case "gpio": return mo.Gpio;
            }
            return true;
        }

        /// <summary>Show only the pages of the parts this server uses (Service &amp; settings > Parts of Watchkeeper).</summary>
        void ApplyModules(bool initial)
        {
            ModulesCfg mo = m.Config.Get().General.Modules;
            string sig = "" + mo.Folders + mo.Programs + mo.Scripts + mo.Vertical + mo.Gpio;
            if (!initial && sig == modulesSig) return;
            modulesSig = sig;
            string curKey = current >= 0 && current < items.Count ? items[current].Key : null;
            items = all.FindAll(delegate(NavItem n) { return PartOn(n.Key, mo); });
            navigating = true;
            try
            {
                nav.Items.Clear();
                foreach (NavItem it in items) nav.Items.Add(it);
            }
            finally { navigating = false; }
            foreach (NavItem it in all) if (!items.Contains(it)) it.Page.Visible = false;
            if (initial) return;
            current = -1;
            int idx = curKey == null ? 0 : items.FindIndex(delegate(NavItem n) { return n.Key == curKey; });
            Select(Math.Max(0, idx), true);
        }

        void BuildNav()
        {
            Font navFont = Ui.Derive(Ui.BaseFont(), 1.05f, FontStyle.Regular);
            nav.Dock = DockStyle.Left;
            nav.Width = 210;
            nav.BorderStyle = BorderStyle.None;
            nav.BackColor = Color.FromArgb(243, 244, 246);
            nav.Font = navFont;
            nav.DrawMode = DrawMode.OwnerDrawFixed;
            nav.ItemHeight = TextRenderer.MeasureText("Ag", navFont).Height + 22;
            nav.IntegralHeight = false;
            nav.TabStop = true;
            nav.DrawItem += DrawNav;
            nav.SelectedIndexChanged += delegate
            {
                if (navigating || nav.SelectedIndex < 0) return;
                Select(nav.SelectedIndex, false);
            };
        }

        void DrawNav(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            NavItem it = items[e.Index];
            bool sel = (e.State & DrawItemState.Selected) != 0;
            Rectangle r = e.Bounds;
            using (Brush bg = new SolidBrush(sel ? Color.White : nav.BackColor))
                e.Graphics.FillRectangle(bg, r);
            if (sel)
                using (Brush accent = new SolidBrush(Ui.Accent))
                    e.Graphics.FillRectangle(accent, r.X, r.Y + 6, 4, r.Height - 12);

            int pad = 18;
            int cy = r.Y + r.Height / 2;
            int d = Math.Max(9, r.Height / 4);
            if (it.HasDot)
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (Brush b = new SolidBrush(it.Dot))
                    e.Graphics.FillEllipse(b, r.X + pad, cy - d / 2, d, d);
            }
            string text = it.Title + (it.Page.Dirty ? "  •" : "");
            Rectangle tr = new Rectangle(r.X + pad + d + 12, r.Y, r.Width - pad - d - 16, r.Height);
            TextRenderer.DrawText(e.Graphics, text, e.Font, tr,
                sel ? SystemColors.ControlText : Color.FromArgb(60, 64, 72),
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        // ------------------------------------------------------------- navigation
        public void SelectKey(string key)
        {
            for (int i = 0; i < items.Count; i++)
                if (items[i].Key == key) { Select(i, false); return; }
        }

        void Select(int index, bool initial)
        {
            if (!initial && current >= 0 && index != current && !ResolveDirty(items[current]))
            {
                navigating = true;
                nav.SelectedIndex = current;
                navigating = false;
                return;
            }
            if (index == current) return;
            if (current >= 0) items[current].Page.Visible = false;
            current = index;
            navigating = true;
            nav.SelectedIndex = index;
            navigating = false;
            Page p = items[index].Page;
            p.Visible = true;
            p.BringToFront();
            if (items[index].Key == "log") errorsSeenUpTo = LastErrorId();
            if (IsHandleCreated) p.OnShown();
            p.Tick();
            nav.Invalidate();
        }

        long LastErrorId()
        {
            long gen;
            List<LogEntry> logs = m.Logs(out gen);
            for (int i = logs.Count - 1; i >= 0; i--) if (logs[i].Level == "ERROR") return logs[i].Id;
            return 0;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            FitToScreen();
        }

        void FitToScreen()
        {
            try
            {
                Rectangle wa = Screen.FromControl(this).WorkingArea;
                int w = Math.Min(Width, wa.Width), h = Math.Min(Height, wa.Height);
                if (w != Width || h != Height)
                {
                    MinimumSize = new Size(Math.Min(MinimumSize.Width, w), Math.Min(MinimumSize.Height, h));
                    Size = new Size(w, h);
                }
                if (Left < wa.Left || Top < wa.Top || Right > wa.Right || Bottom > wa.Bottom)
                    Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + (wa.Height - Height) / 2);
            }
            catch (Exception) { }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (current >= 0) items[current].Page.OnShown();
        }

        bool ResolveDirty(NavItem it)
        {
            if (!it.Page.Dirty) return true;
            DialogResult r = MessageBox.Show(this,
                "\"" + it.Title + "\" has unsaved changes.\n\nSave them?", "Watchkeeper",
                MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) return false;
            if (r == DialogResult.Yes) return it.Page.Save();
            it.Page.Discard();
            return true;
        }

        bool ResolveAllDirty()
        {
            foreach (NavItem it in items)
            {
                if (!it.Page.Dirty) continue;
                int i = items.IndexOf(it);
                if (i != current) Select(i, false);
                if (!ResolveDirty(it)) return false;
            }
            return true;
        }

        // ------------------------------------------------------------------ events
        void OnConfigChanged(string section)
        {
            if (!IsHandleCreated) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    foreach (NavItem it in all)
                        if (!it.Page.Dirty) it.Page.ReloadIfChanged();
                    ApplyModules(false);
                    nav.Invalidate();
                });
            }
            catch (Exception) { }
        }

        void OnTimer()
        {
            try
            {
                UpdateDots();
                if (Visible && current >= 0) items[current].Page.Tick();
            }
            catch (Exception) { }
        }

        static Color JobsDot(EngineStatus s, string kind)
        {
            List<JobStatus> list = s.Jobs.FindAll(delegate(JobStatus j) { return j.Kind == kind && j.Enabled; });
            if (list.Count == 0) return Ui.Off;
            if (list.Exists(delegate(JobStatus j) { return j.LastResult == "failed"; })) return Ui.Bad;
            if (list.Exists(delegate(JobStatus j) { return j.Running; })) return Ui.Warn;
            return Ui.Ok;
        }

        void UpdateDots()
        {
            bool changed = false;
            EngineStatus s = m.Status;
            foreach (NavItem it in items)
            {
                Color c = Ui.Off;
                if (s == null)
                {
                    if (it.Key == "overview" || it.Key == "service") c = Ui.Bad;
                }
                else
                {
                    switch (it.Key)
                    {
                        case "overview":
                        case "service":
                            c = s.ConfigProblems.Count > 0 || s.DryRun ? Ui.Warn : Ui.Ok;
                            break;
                        case "folders": c = JobsDot(s, "folder"); break;
                        case "scripts": c = JobsDot(s, "script"); break;
                        case "programs":
                            {
                                List<ProgramStatus> on = s.Programs.FindAll(delegate(ProgramStatus p) { return p.Enabled; });
                                if (on.Count == 0) c = Ui.Off;
                                else if (on.Exists(delegate(ProgramStatus p) { return p.State == "error" || p.State == "paused" || p.Health == "failing"; })) c = Ui.Bad;
                                else if (on.Exists(delegate(ProgramStatus p) { return p.State != "running"; })) c = Ui.Warn;
                                else c = Ui.Ok;
                                break;
                            }
                        case "gpio":
                            {
                                GpioStatus g = s.Gpio;
                                if (g == null || !g.Enabled) c = Ui.Off;
                                else if (g.Devices.Exists(delegate(GpioDeviceStatus d) { return d.State == "error"; })) c = Ui.Bad;
                                else if (g.Events.Count > 0 && g.Events[0].Result.Contains("failed")) c = Ui.Warn;
                                else if (g.Devices.Exists(delegate(GpioDeviceStatus d) { return d.State == "connected"; })) c = Ui.Ok;
                                else c = Ui.Warn;
                                break;
                            }
                        case "vertical":
                            {
                                ConverterStatus v = s.Vertical;
                                if (v.Running) c = v.Current != null ? Ui.Warn : Ui.Ok;
                                else if (v.Stopping) c = Ui.Warn;
                                if (v.Failures.Count > 0) c = Ui.Bad;
                                break;
                            }
                        case "log":
                            c = items[current].Key != "log" && LastErrorId() > errorsSeenUpTo ? Ui.Bad : Ui.Off;
                            break;
                    }
                }
                if (c != it.Dot) { it.Dot = c; changed = true; }
            }
            System.Text.StringBuilder sig = new System.Text.StringBuilder();
            foreach (NavItem it in items) sig.Append(it.Page.Dirty ? '1' : '0');
            if (sig.ToString() != dirtySig) { dirtySig = sig.ToString(); changed = true; }
            if (changed) nav.Invalidate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && !ResolveAllDirty())
            {
                e.Cancel = true;
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop();
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }

        // -------------------------------------------------------------------- brand
        void BuildBrand()
        {
            Font brandFont = Ui.Derive(Ui.BaseFont(), 1.3f, FontStyle.Bold);
            int logoSize = Math.Max(24, (int)(brandFont.Height * 1.7));
            Bitmap logo = LogoBitmap(logoSize);
            brand.Dock = DockStyle.Top;
            brand.Height = logoSize + 28;
            brand.BackColor = nav.BackColor;
            brand.Paint += delegate(object sender, PaintEventArgs e)
            {
                int y = (brand.Height - logoSize) / 2;
                if (logo != null)
                {
                    e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    e.Graphics.DrawImage(logo, 16, y, logoSize, logoSize);
                }
                Rectangle r = new Rectangle(16 + logoSize + 10, 0, brand.Width - logoSize - 30, brand.Height);
                TextRenderer.DrawText(e.Graphics, "Watchkeeper", brandFont, r, Color.FromArgb(11, 31, 51),
                                      TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                using (Pen p = new Pen(Color.FromArgb(225, 228, 232)))
                    e.Graphics.DrawLine(p, 12, brand.Height - 1, brand.Width - 12, brand.Height - 1);
            };
        }

        static Bitmap LogoBitmap(int size)
        {
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("logo.png"))
                    if (s != null)
                        using (Image i = Image.FromStream(s))
                            return new Bitmap(i);
            }
            catch (Exception) { }
            return null;
        }

        // -------------------------------------------------------------------- icon
        public static Icon AppIcon()
        {
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"))
                    if (s != null) return new Icon(s);
            }
            catch (Exception) { }
            return SystemIcons.Application;
        }
    }
}
