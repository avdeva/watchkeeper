// Overview: one line for everything Watchkeeper does, in plain words, and what needs attention.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Watchkeeper.UI
{
    public class OverviewPage : Page
    {
        readonly ListView list = new ListView();
        readonly MainForm form;
        public override string Title { get { return "Overview"; } }

        public OverviewPage(Manager m, MainForm form) : base(m)
        {
            this.form = form;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            list.Dock = DockStyle.Fill;
            list.BorderStyle = BorderStyle.None;
            int em = Ui.BaseFont().Height;
            list.Columns.Add("Part", em * 9);
            list.Columns.Add("What", em * 16);
            list.Columns.Add("Now", em * 46);
            list.DoubleClick += delegate
            {
                if (list.SelectedItems.Count == 0) return;
                string key = list.SelectedItems[0].Tag as string;
                Page p = key == null ? null : this.form.PageByKey(key);
                if (p != null) this.form.ShowPage(p);
            };
            Controls.Add(list);
            Panel bar = new Panel();
            bar.Dock = DockStyle.Bottom;
            bar.Height = em * 2 + 12;
            bar.Padding = new Padding(0, 6, 0, 0);
            Button self = Ui.Button("Run the self-test...", delegate { SelfTestNow(); });
            self.Dock = DockStyle.Left;
            bar.Controls.Add(self);
            Controls.Add(bar);
            Heading("Overview", "What Watchkeeper is doing on this server right now. Double-click a line to open its page. Colours: green = fine, " +
                                "orange = waiting or busy, red = needs attention, grey = switched off.");
        }

        void Add(string part, string what, string now, string state, string page)
        {
            ListViewItem it = new ListViewItem(part);
            it.SubItems.Add(what);
            it.SubItems.Add(now);
            it.ForeColor = Ui.StateColor(state);
            it.Tag = page;
            list.Items.Add(it);
        }

        public override void Live(EngineStatus s)
        {
            int top = list.TopItem != null ? list.TopItem.Index : 0;
            list.BeginUpdate();
            list.Items.Clear();
            if (!M.ServiceAlive) Add("Service", "Watchkeeper", "Not running or not reporting: nothing below is up to date", "error", "settings");
            else Add("Service", "Watchkeeper " + s.Version, "Running since " + Util.FmtTime(s.Started) + (s.SettingsSource == "backup" ? "; config.json cannot be read, the newest backup is in use" : ""),
                     s.SettingsError.Length > 0 ? "error" : "running", "settings");
            foreach (string p in s.Problems) Add("Settings", "", p, p.StartsWith("warning") ? "waiting" : "error", null);
            foreach (ProgramStatus p in s.Watchdog)
                Add("Watchdog", p.Name, p.Text + (p.Health.Length > 0 && p.State == "running" ? "; health check: " + p.HealthText : "") +
                    (p.LastProblem.Length > 0 && p.State != "running" ? " (last problem: " + p.LastProblem + ")" : ""),
                    p.Health == "failing" ? "error" : p.State, "watchdog");
            foreach (FolderTaskStatus f in s.Folders)
                Add("Folder tasks", f.Name, f.Text + (f.Enabled && f.NextRun > 0 && f.State == "idle" ? "; next run " + At(f.NextRun) : ""), f.State, "folder tasks");
            if (s.Vertical.Enabled || s.Vertical.State == "error")
                Add("Vertical", "Conversion", s.Vertical.State == "converting" ? s.Vertical.Text + " - " + s.Vertical.Percent.ToString("0", Util.Inv) + "% (" + s.Vertical.Encoder + ")" : s.Vertical.Text,
                    s.Vertical.State, "vertical conversion");
            else Add("Vertical", "Conversion", "Switched off", "off", "vertical conversion");
            if (s.Gpio.Enabled) foreach (GpioDeviceStatus d in s.Gpio.Devices) Add("GPIO", d.Name, d.Text, d.State, "gpio");
            else Add("GPIO", "", "Switched off", "off", "gpio");
            foreach (string a in s.Alerts.Active) Add("Alerts", "Open problem", a, "error", "alerts");
            foreach (DestinationStatus d in s.Alerts.Destinations)
                if (d.LastError.Length > 0) Add("Alerts", d.Name, "Last send failed: " + d.LastError, "error", "alerts");
            list.EndUpdate();
            if (top > 0 && top < list.Items.Count) { try { list.TopItem = list.Items[top]; } catch (Exception) { } }
        }

        public override Color Dot(EngineStatus s)
        {
            bool bad = s.SettingsError.Length > 0 || s.Alerts.Active.Count > 0;
            return bad ? Ui.Bad : Ui.Good;
        }

        void SelfTestNow()
        {
            List<string> lines;
            if (M.ServiceAlive)
            {
                Cursor = Cursors.WaitCursor;
                CommandResult r = M.Command("selftest", "", 60000);
                Cursor = Cursors.Default;
                lines = r != null ? r.Lines : new List<string> { "FAIL  the service did not answer within 60 seconds" };
                lines.Insert(0, "Run by the service (as Local System), the way the watchdog runs programs:");
            }
            else
            {
                lines = SelfTest.Basic(M.DataDir);
                lines.Insert(0, "The service is not running, so only the checks this window can do were run:");
            }
            Ui.ShowText(FindForm(), "Self-test", string.Join("\n", lines.ToArray()));
        }
    }
}
