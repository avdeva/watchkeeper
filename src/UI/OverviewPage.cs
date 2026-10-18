// Overview: is the service running, what ran, what is due, which programs are up, recent alerts.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace Watchkeeper.UI
{
    public class OverviewPage : Page
    {
        readonly Panel banner = new Panel();
        readonly StatusDot dot = new StatusDot();
        readonly Label bannerText = new Label();
        readonly Button bannerBtn;
        readonly ListView jobs = new ListView();
        readonly ListView programs = new ListView();
        readonly ListView alerts = new ListView();
        readonly Label vertical = new Label();
        readonly Button runBtn, dryBtn, outBtn, restartBtn;
        string jobsSig = "", progSig = "", alertSig = "";

        public Action<string> OpenPage;

        public OverviewPage(Manager m)
            : base(m, "Overview", "What the Watchkeeper service is doing on this server.")
        {
            bannerBtn = Ui.Btn("Service && settings", delegate { if (OpenPage != null) OpenPage("service"); });
            runBtn = Ui.Btn("Run now", delegate { RunSelected(false); });
            dryBtn = Ui.Btn("Dry run", delegate { RunSelected(true); });
            outBtn = Ui.Btn("Show output", delegate { ShowOutput(); });
            restartBtn = Ui.Btn("Restart", delegate { RestartSelected(); });

            banner.Dock = DockStyle.Top;
            banner.Height = Ui.BaseFont().Height * 3 + 22;
            banner.Padding = new Padding(20, 8, 20, 8);
            dot.Location = new Point(20, 14);
            bannerText.Location = new Point(42, 10);
            bannerText.AutoSize = false;
            bannerText.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            bannerBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            banner.Controls.Add(dot);
            banner.Controls.Add(bannerText);
            banner.Controls.Add(bannerBtn);
            banner.Resize += delegate { LayoutBanner(); };

            Setup(jobs, new[] { "Name", "Type", "When", "Last run", "Result", "Next run" }, new[] { 200, 80, 160, 130, 330, 130 });
            Setup(programs, new[] { "Program or service", "State", "Detail" }, new[] { 200, 110, 600 });
            Setup(alerts, new[] { "Time", "Alert", "Sent" }, new[] { 130, 360, 380 });
            jobs.DoubleClick += delegate
            {
                JobStatus j = SelectedJob();
                if (j != null && OpenPage != null) OpenPage(j.Kind == "folder" ? "folders" : "scripts");
            };
            programs.DoubleClick += delegate { if (OpenPage != null) OpenPage("programs"); };
            alerts.DoubleClick += delegate
            {
                if (alerts.SelectedItems.Count == 0) return;
                AlertRecord r = alerts.SelectedItems[0].Tag as AlertRecord;
                if (r != null) ResultDialog.Display(FindForm(), "Alert", r.Title, r.Message + "\n\n" + r.Sent);
            };
            jobs.SelectedIndexChanged += delegate { UpdateButtons(); };
            programs.SelectedIndexChanged += delegate { UpdateButtons(); };
            vertical.AutoSize = true;
            vertical.Margin = new Padding(0, 4, 0, 6);
            vertical.Cursor = Cursors.Hand;
            vertical.Click += delegate
            {
                ModulesCfg mo = M.Config.Get().General.Modules;
                if (OpenPage != null) OpenPage(mo.Vertical ? "vertical" : "gpio");
            };

            TableLayoutPanel t = new TableLayoutPanel();
            t.Dock = DockStyle.Fill;
            t.ColumnCount = 1;
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.Padding = new Padding(20, 0, 20, 12);
            t.RowCount = 7;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 28));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
            t.Controls.Add(Heading("Folder policies and scripts", runBtn, dryBtn, outBtn), 0, 0);
            t.Controls.Add(jobs, 0, 1);
            t.Controls.Add(Heading("Watchdog", restartBtn), 0, 2);
            t.Controls.Add(programs, 0, 3);
            t.Controls.Add(Heading("Conversion, GPIO and recent alerts"), 0, 4);
            t.Controls.Add(vertical, 0, 5);
            t.Controls.Add(alerts, 0, 6);

            Controls.Add(t);
            Controls.Add(banner);
            Controls.SetChildIndex(Header, Controls.Count - 1);
            t.BringToFront();
            UpdateButtons();
        }

        void LayoutBanner()
        {
            int bw = bannerBtn.PreferredSize.Width;
            bannerBtn.Location = new Point(banner.ClientSize.Width - 20 - bw, 10);
            bannerText.Width = Math.Max(100, bannerBtn.Left - 52);
            bannerText.Height = banner.ClientSize.Height - 12;
        }

        static void Setup(ListView lv, string[] cols, int[] widths)
        {
            lv.Dock = DockStyle.Fill;
            lv.View = View.Details;
            lv.FullRowSelect = true;
            lv.HideSelection = false;
            lv.MultiSelect = false;
            lv.Margin = new Padding(0, 0, 0, 6);
            for (int i = 0; i < cols.Length; i++) lv.Columns.Add(cols[i], widths[i]);
        }

        static Control Heading(string text, params Button[] buttons)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Font = Ui.Derive(Ui.BaseFont(), 1.0f, FontStyle.Bold);
            l.Margin = new Padding(0, 9, 16, 0);
            List<Control> items = new List<Control>();
            items.Add(l);
            foreach (Button b in buttons) { b.Margin = new Padding(0, 4, 6, 4); items.Add(b); }
            FlowLayoutPanel f = Ui.Flow(items.ToArray());
            f.Margin = new Padding(0, 6, 0, 2);
            return f;
        }

        JobStatus SelectedJob()
        {
            if (jobs.SelectedItems.Count == 0) return null;
            string key = jobs.SelectedItems[0].Tag as string;
            return key == null ? null : M.Job(key);
        }

        void UpdateButtons()
        {
            JobStatus j = SelectedJob();
            runBtn.Enabled = dryBtn.Enabled = j != null && !j.Running && M.Online;
            outBtn.Enabled = j != null && M.Online;
            restartBtn.Enabled = programs.SelectedItems.Count > 0 && M.Online;
        }

        void RunSelected(bool dry)
        {
            JobStatus j = SelectedJob();
            if (j == null) return;
            EngineStatus st = M.Status;
            if (!dry && (st == null || !st.DryRun) && !Ui.Confirm(FindForm(), "Run \"" + j.Name + "\" now? It really changes files or runs the script.")) return;
            M.Async(this, delegate { return M.Call("job.run", "key", j.Key, "dry", dry); }, delegate(JObject r, Exception ex)
            {
                if (ex != null) Ui.Error(FindForm(), "Not started: " + ex.Message);
            });
        }

        void ShowOutput()
        {
            JobStatus j = SelectedJob();
            if (j == null) return;
            M.Async(this, delegate { return M.Call("job.lines", "key", j.Key); }, delegate(JObject r, Exception ex)
            {
                if (ex != null) { Ui.Error(FindForm(), ex.Message); return; }
                StringBuilder sb = new StringBuilder();
                List<object> lines = r.Get("lines") as List<object>;
                if (lines != null) foreach (object l in lines) sb.Append(l as string).Append('\n');
                ResultDialog.Display(FindForm(), j.Name, j.LastEnd > 0 ? "Last run " + Ui.When(j.LastEnd) + ": " + j.LastText : "No run since the service started",
                                     sb.Length > 0 ? sb.ToString() : "(nothing recorded since the service started)");
            });
        }

        void RestartSelected()
        {
            if (programs.SelectedItems.Count == 0) return;
            string id = programs.SelectedItems[0].Tag as string;
            string name = programs.SelectedItems[0].Text;
            if (!Ui.Confirm(FindForm(), "Close " + name + " and start it again now?")) return;
            M.Async(this, delegate { return M.Call("program.restart", "id", id); }, delegate(JObject r, Exception ex)
            {
                if (ex != null) Ui.Error(FindForm(), "Restart not possible: " + ex.Message);
            });
        }

        public override void Tick()
        {
            EngineStatus s = M.Status;
            ServiceInfo si = M.Service;
            if (s == null)
            {
                dot.Dot = Ui.Bad;
                banner.BackColor = Color.FromArgb(253, 236, 234);
                bannerText.Text = "The Watchkeeper service is not running. No folder policies, scripts, program keep-alive or conversions are running.\n" +
                                  (Util.IsWindows ? (si.Installed ? "Installed but " + si.Status.ToLowerInvariant() + "." : "It is not installed yet.") + " Open Service & settings to start or install it."
                                                  : M.StatusError);
            }
            else
            {
                List<string> warn = new List<string>();
                if (s.DryRun) warn.Add("DRY-RUN MODE is on: nothing is changed.");
                if (s.SafeMode.Length > 0) warn.Insert(0, "SAFE MODE, nothing runs: " + s.SafeMode);
                List<string> other = s.ConfigProblems.FindAll(delegate(string x) { return !x.StartsWith("SAFE MODE"); });
                if (other.Count > 0) warn.Add("Configuration problems: " + string.Join("; ", other.ToArray()));
                if (s.ConsoleSession < 0 && s.Programs.Exists(delegate(ProgramStatus p) { return p.Enabled; }))
                    warn.Add("Nobody is signed in at the console, so desktop programs cannot start (check auto-logon).");
                dot.Dot = warn.Count > 0 ? Ui.Warn : Ui.Ok;
                banner.BackColor = warn.Count > 0 ? Color.FromArgb(255, 246, 225) : Color.FromArgb(232, 245, 236);
                bannerText.Text = "Service running" + (s.Mode == "console" ? " in console mode" : "") + " for " + Ui.Ago(s.StartedAt) +
                                  ", priority " + s.Priority + ". Configuration applied " + Ui.When(s.ConfigLoadedAt) + "." +
                                  (warn.Count > 0 ? "\n" + string.Join(" ", warn.ToArray()) : "");
            }
            LayoutBanner();
            FillJobs(s);
            FillPrograms(s);
            FillVertical(s);
            FillAlerts(s);
            UpdateButtons();
        }

        void FillJobs(EngineStatus s)
        {
            ModulesCfg mo = M.Config.Get().General.Modules;
            List<JobStatus> list = s != null ? s.Jobs.FindAll(delegate(JobStatus j) { return j.Kind == "folder" ? mo.Folders : mo.Scripts; }) : new List<JobStatus>();
            StringBuilder sig = new StringBuilder();
            foreach (JobStatus j in list)
                sig.Append(j.Key).Append(j.Name).Append(j.Enabled).Append(j.Running).Append(j.LastEnd).Append(j.LastResult).Append(j.NextRun).Append(j.ScheduleText).Append('|');
            if (sig.ToString() == jobsSig) return;
            jobsSig = sig.ToString();
            string selected = jobs.SelectedItems.Count > 0 ? jobs.SelectedItems[0].Tag as string : null;
            jobs.BeginUpdate();
            jobs.Items.Clear();
            foreach (JobStatus j in list)
            {
                ListViewItem it = new ListViewItem(j.Name);
                it.SubItems.Add(j.Kind == "folder" ? "Folder" : "Script");
                it.SubItems.Add(j.ScheduleText);
                it.SubItems.Add(j.Running ? "running now" : j.LastEnd > 0 ? Ui.When(j.LastEnd) + (j.LastDryRun ? " (dry)" : "") : "");
                it.SubItems.Add(j.LastText);
                it.SubItems.Add(j.Enabled && j.NextRun > 0 ? Ui.When(j.NextRun) : "");
                it.Tag = j.Key;
                if (!j.Enabled) it.ForeColor = SystemColors.GrayText;
                else if (j.Running) it.ForeColor = Ui.Accent;
                else if (j.LastResult == "failed") it.ForeColor = Ui.Bad;
                jobs.Items.Add(it);
                if (j.Key == selected) it.Selected = true;
            }
            jobs.EndUpdate();
        }

        void FillPrograms(EngineStatus s)
        {
            List<ProgramStatus> list = s != null && M.Config.Get().General.Modules.Programs ? s.Programs : new List<ProgramStatus>();
            StringBuilder sig = new StringBuilder();
            foreach (ProgramStatus p in list) sig.Append(p.Id).Append(p.Name).Append(p.State).Append(ProgramsPage.Describe(p)).Append('|');
            if (sig.ToString() == progSig) return;
            progSig = sig.ToString();
            string selected = programs.SelectedItems.Count > 0 ? programs.SelectedItems[0].Tag as string : null;
            programs.BeginUpdate();
            programs.Items.Clear();
            foreach (ProgramStatus p in list)
            {
                ListViewItem it = new ListViewItem(p.Name);
                it.SubItems.Add(p.Health == "failing" ? "Not answering" : StateText(p.State));
                it.SubItems.Add(p.Enabled ? ProgramsPage.Describe(p) : "Not supervised");
                it.Tag = p.Id;
                if (!p.Enabled) it.ForeColor = SystemColors.GrayText;
                else if (p.Health == "failing") it.ForeColor = Ui.Bad;
                else if (p.State == "running") it.ForeColor = Ui.Ok;
                else if (p.State == "error" || p.State == "paused") it.ForeColor = Ui.Bad;
                else it.ForeColor = Color.FromArgb(160, 100, 0);
                programs.Items.Add(it);
                if (p.Id == selected) it.Selected = true;
            }
            programs.EndUpdate();
        }

        public static string StateText(string state)
        {
            switch (state)
            {
                case "running": return "Running";
                case "waiting_session": return "Waiting for sign-in";
                case "waiting": return "Starting";
                case "paused": return "Paused";
                case "error": return "Problem";
            }
            return "Off";
        }

        void FillVertical(EngineStatus s)
        {
            if (s == null) { vertical.Text = ""; return; }
            ModulesCfg mo = M.Config.Get().General.Modules;
            List<string> lines = new List<string>();
            Color color = SystemColors.GrayText;
            if (mo.Vertical)
            {
                lines.Add("Vertical conversion: " + VerticalText(s.Vertical));
                color = vertical.ForeColor;
            }
            if (mo.Gpio && s.Gpio != null)
            {
                GpioStatus g = s.Gpio;
                int up = g.Devices.FindAll(delegate(GpioDeviceStatus d) { return d.State == "connected"; }).Count;
                int bad = g.Devices.FindAll(delegate(GpioDeviceStatus d) { return d.State == "error"; }).Count;
                string gt = !g.Enabled ? "switched off." : up + " Axia device(s) connected" + (bad > 0 ? ", " + bad + " unreachable" : "") + "." +
                            (g.Events.Count > 0 ? " Last change " + Ui.When(g.Events[0].Time) + ": " + GpioService.Describe(g.Events[0]) +
                                                  (g.Events[0].Triggers.Count > 0 ? " > " + string.Join(", ", g.Events[0].Triggers.ToArray()) + " (" + g.Events[0].Result + ")" : "") : "");
                lines.Add("GPIO: " + gt);
                if (bad > 0 || (g.Events.Count > 0 && g.Events[0].Result.Contains("failed"))) color = Ui.Bad;
                else if (!mo.Vertical) color = g.Enabled && up > 0 ? Ui.Ok : SystemColors.GrayText;
            }
            vertical.Text = string.Join("\n", lines.ToArray());
            vertical.ForeColor = color;
        }

        string VerticalText(ConverterStatus v)
        {
            string text;
            if (v.Current != null)
            {
                text = "Converting " + v.Current.Name + " - " + v.Current.Stage +
                       (v.Current.Stage == "encoding" ? " " + v.Current.Percent.ToString("0", Util.Inv) + "% on " + v.Current.Backend : "");
                vertical.ForeColor = Ui.Accent;
            }
            else if (v.Running)
            {
                text = "Watching for new recordings.";
                vertical.ForeColor = Ui.Ok;
            }
            else
            {
                text = "Not watching.";
                vertical.ForeColor = SystemColors.GrayText;
            }
            int done = v.History.FindAll(delegate(HistoryItem h) { return h.Status == "done"; }).Count;
            if (done > 0) text += "  " + done + " converted since the service started.";
            if (v.Failures.Count > 0) { text += "  " + v.Failures.Count + " failed file(s)."; vertical.ForeColor = Ui.Bad; }
            return text;
        }

        void FillAlerts(EngineStatus s)
        {
            List<AlertRecord> list = s != null ? s.Alerts : new List<AlertRecord>();
            string sig = list.Count + "|" + (list.Count > 0 ? list[0].Time + list[0].Sent : "");
            if (sig == alertSig) return;
            alertSig = sig;
            alerts.BeginUpdate();
            alerts.Items.Clear();
            foreach (AlertRecord r in list)
            {
                ListViewItem it = new ListViewItem(Ui.When(r.Time));
                it.SubItems.Add(r.Title);
                it.SubItems.Add(r.Sent);
                it.Tag = r;
                if (r.Kind != "service_start" && r.Kind != "test") it.ForeColor = Ui.Bad;
                alerts.Items.Add(it);
            }
            alerts.EndUpdate();
        }
    }
}
