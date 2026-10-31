// Log page: the newest lines of the service's log, newest first.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Watchkeeper.UI
{
    public class LogPage : Page
    {
        readonly ListView lines = new ListView();
        readonly ComboBox level = new ComboBox();
        readonly TextBox filter = new TextBox();
        long lastSeq = -1;
        public override string Title { get { return "Log"; } }

        public LogPage(Manager m) : base(m)
        {
            int em = Ui.BaseFont().Height;
            lines.View = View.Details;
            lines.Dock = DockStyle.Fill;
            lines.FullRowSelect = true;
            lines.Columns.Add("Time", em * 10);
            lines.Columns.Add("Level", em * 5);
            lines.Columns.Add("Part", em * 6);
            lines.Columns.Add("What happened", em * 60);
            lines.DoubleClick += delegate { if (lines.SelectedItems.Count > 0) Ui.ShowText(FindForm(), "Log line", ((LogEntry)lines.SelectedItems[0].Tag).Line()); };
            Controls.Add(lines);
            FlowLayoutPanel top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 6) };
            level.DropDownStyle = ComboBoxStyle.DropDownList;
            level.Items.AddRange(new object[] { "Everything", "Warnings and errors", "Errors" });
            level.SelectedIndex = 0;
            level.SelectedIndexChanged += delegate { lastSeq = -1; if (M.Status != null) Live(M.Status); };
            filter.Width = em * 14;
            filter.TextChanged += delegate { lastSeq = -1; if (M.Status != null) Live(M.Status); };
            top.Controls.Add(Ui.Label("Show"));
            top.Controls.Add(level);
            top.Controls.Add(Ui.Label("containing"));
            top.Controls.Add(filter);
            top.Controls.Add(Ui.Button("Open the log folder", delegate { try { Process.Start("explorer.exe", Util.QuoteArg(Path.Combine(M.DataDir, "logs"))); } catch (Exception) { } }));
            Controls.Add(top);
            Heading("Log", "What the service did, newest first (the last 300 lines). The full log is kept for 30 days in the logs folder, one file per day.");
        }

        public override void Live(EngineStatus s)
        {
            long newest = s.Log.Count > 0 ? s.Log[s.Log.Count - 1].Seq : 0;
            if (newest == lastSeq) return;
            lastSeq = newest;
            int min = level.SelectedIndex;
            string f = filter.Text.Trim();
            lines.BeginUpdate();
            lines.Items.Clear();
            for (int i = s.Log.Count - 1; i >= 0; i--)
            {
                LogEntry e = s.Log[i];
                if (min == 1 && e.Level != "warning" && e.Level != "error") continue;
                if (min == 2 && e.Level != "error") continue;
                if (f.Length > 0 && e.Text.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0 && e.Source.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0) continue;
                ListViewItem it = new ListViewItem(Util.FromEpoch(e.Time).ToString("dd MMM HH:mm:ss", Util.Inv));
                it.SubItems.Add(e.Level);
                it.SubItems.Add(e.Source);
                it.SubItems.Add(e.Text);
                it.Tag = e;
                if (e.Level == "error") it.ForeColor = Ui.Bad;
                else if (e.Level == "warning") it.ForeColor = Ui.Warn;
                lines.Items.Add(it);
            }
            lines.EndUpdate();
        }

        public int Count { get { return lines.Items.Count; } }
    }
}
