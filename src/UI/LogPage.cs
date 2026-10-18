// Log viewer: filters by level, source and text, follows new lines, shows the full text of the
// selected line. The list is rebuilt from the manager's copy of the service log whenever a
// filter changes, so what is shown always matches the filters.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Watchkeeper.UI
{
    public class LogPage : Page
    {
        const int MaxShown = 3000;
        static readonly string[] KnownSources = { "service", "config", "folders", "scripts", "programs", "vertical", "gpio", "alerts" };

        readonly ListView list = new ListView();
        readonly TextBox detail = new TextBox();
        readonly ComboBox level, source;
        readonly TextBox search = Ui.Text(200);
        readonly CheckBox follow = Ui.Chk("Follow newest");
        readonly Label count = new Label();
        readonly Timer searchDelay = new Timer();
        readonly List<LogEntry> shown = new List<LogEntry>();
        readonly HashSet<string> sources = new HashSet<string>();
        long after;
        long hiddenUpTo;
        long generation = -1;

        public LogPage(Manager m)
            : base(m, "Log", "Everything the service did, newest at the bottom. The same lines are in watchkeeper.log in the configuration folder.")
        {
            level = Ui.Combo(new Choice("INFO", "Info, warnings and errors"), new Choice("WARN", "Warnings and errors"),
                             new Choice("ERROR", "Errors only"), new Choice("DEBUG", "Everything (debug)"));
            source = Ui.Combo(new Choice("", "All sources"));
            foreach (string s in KnownSources) AddSource(s);
            follow.Checked = true;
            follow.Margin = new Padding(8, 5, 12, 0);
            count.AutoSize = true;
            count.ForeColor = SystemColors.GrayText;
            count.Margin = new Padding(8, 7, 0, 0);
            Label find = Ui.Lbl("Find");
            find.Margin = new Padding(8, 6, 4, 0);

            Button copy = Ui.Btn("Copy lines", delegate { CopyAll(); });
            Button clear = Ui.Btn("Clear view", delegate { ClearView(); });
            Button open = Ui.Btn("Open file", delegate { OpenFile(); });
            level.SelectedIndexChanged += delegate { Rebuild(); };
            source.SelectedIndexChanged += delegate { Rebuild(); };
            searchDelay.Interval = 300;
            searchDelay.Tick += delegate { searchDelay.Stop(); Rebuild(); };
            search.TextChanged += delegate { searchDelay.Stop(); searchDelay.Start(); };
            follow.CheckedChanged += delegate { if (follow.Checked) ScrollToEnd(); };

            FlowLayoutPanel bar = Ui.Flow(level, source, find, search, follow, copy, clear, open, count);
            bar.Anchor = AnchorStyles.Left;
            bar.Margin = new Padding(0, 0, 0, 6);

            list.View = View.Details;
            list.FullRowSelect = true;
            list.GridLines = false;
            list.HideSelection = false;
            list.MultiSelect = false;
            list.Dock = DockStyle.Fill;
            list.Font = Ui.MonoFont(Ui.BaseFont());
            list.Columns.Add("Time", 175);
            list.Columns.Add("Level", 60);
            list.Columns.Add("Source", 90);
            list.Columns.Add("Message", 900);
            list.SelectedIndexChanged += delegate { ShowDetail(); };
            list.Resize += delegate { FitColumn(); };

            detail.Multiline = true;
            detail.ReadOnly = true;
            detail.ScrollBars = ScrollBars.Vertical;
            detail.Dock = DockStyle.Fill;
            detail.Font = Ui.MonoFont(Ui.BaseFont());
            detail.BackColor = SystemColors.Window;

            TableLayoutPanel t = new TableLayoutPanel();
            t.Dock = DockStyle.Fill;
            t.ColumnCount = 1;
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.RowCount = 3;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 110));
            t.Padding = new Padding(20, 4, 20, 14);
            t.Controls.Add(bar, 0, 0);
            t.Controls.Add(list, 0, 1);
            t.Controls.Add(detail, 0, 2);
            Controls.Add(t);
            Controls.SetChildIndex(Header, Controls.Count - 1);
            t.BringToFront();
            Rebuild();
        }

        void AddSource(string s)
        {
            if (string.IsNullOrEmpty(s) || sources.Contains(s)) return;
            sources.Add(s);
            source.Items.Add(new Choice(s, s));
        }

        void FitColumn()
        {
            int w = list.ClientSize.Width - list.Columns[0].Width - list.Columns[1].Width - list.Columns[2].Width - 4;
            if (w > 200) list.Columns[3].Width = w;
        }

        /// <summary>The filter as it stands: (minimum level, source or "", lower-case text or "").</summary>
        public bool Passes(LogEntry e)
        {
            if (e.Id <= hiddenUpTo) return false;
            string src = Ui.Get(source);
            if (src.Length > 0 && !string.Equals(e.Source, src, StringComparison.OrdinalIgnoreCase)) return false;
            if (LogHub.LevelNumber(e.Level) < LogHub.LevelNumber(Ui.Get(level))) return false;
            string q = search.Text.Trim();
            if (q.Length > 0 && (e.Msg ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0
                && (e.Source ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) return false;
            return true;
        }

        static ListViewItem Row(LogEntry le)
        {
            ListViewItem it = new ListViewItem(Util.FromEpoch(le.Ts).ToString("yyyy-MM-dd HH:mm:ss", Util.Inv));
            it.SubItems.Add(le.Level);
            it.SubItems.Add(le.Source);
            string msg = (le.Msg ?? "").Replace("\r", "");
            int nl = msg.IndexOf('\n');
            it.SubItems.Add(nl >= 0 ? msg.Substring(0, nl) + "  (...)" : msg);
            if (le.Level == "ERROR") it.ForeColor = Ui.Bad;
            else if (le.Level == "WARN") it.ForeColor = Color.FromArgb(160, 100, 0);
            else if (le.Level == "DEBUG") it.ForeColor = SystemColors.GrayText;
            it.Tag = le;
            return it;
        }

        /// <summary>Rebuild the whole list from the current filters.</summary>
        public void Rebuild()
        {
            long gen;
            List<LogEntry> all = M.Logs(out gen);
            if (gen != generation) { generation = gen; hiddenUpTo = 0; }
            shown.Clear();
            after = 0;
            foreach (LogEntry e in all)
            {
                after = Math.Max(after, e.Id);
                AddSource(e.Source);
                if (Passes(e)) shown.Add(e);
            }
            if (shown.Count > MaxShown) shown.RemoveRange(0, shown.Count - MaxShown);
            List<ListViewItem> rows = new List<ListViewItem>(shown.Count);
            foreach (LogEntry e in shown) rows.Add(Row(e));
            list.BeginUpdate();
            try
            {
                list.SelectedIndices.Clear();
                list.Items.Clear();
                list.Items.AddRange(rows.ToArray());
            }
            finally { list.EndUpdate(); }
            detail.Text = "";
            UpdateCount();
            ScrollToEnd();
        }

        void Append()
        {
            long gen;
            List<LogEntry> all = M.Logs(out gen);
            if (gen != generation) { Rebuild(); return; }
            List<ListViewItem> rows = new List<ListViewItem>();
            foreach (LogEntry e in all)
            {
                if (e.Id <= after) continue;
                after = e.Id;
                AddSource(e.Source);
                if (!Passes(e)) continue;
                shown.Add(e);
                rows.Add(Row(e));
            }
            if (rows.Count == 0) return;
            list.BeginUpdate();
            try
            {
                list.Items.AddRange(rows.ToArray());
                int extra = shown.Count - MaxShown;
                if (extra > 0)
                {
                    shown.RemoveRange(0, extra);
                    for (int i = 0; i < extra && list.Items.Count > 0; i++) list.Items.RemoveAt(0);
                }
            }
            finally { list.EndUpdate(); }
            UpdateCount();
            if (follow.Checked) ScrollToEnd();
        }

        void UpdateCount()
        {
            int errors = 0, warns = 0;
            foreach (LogEntry e in shown)
            {
                if (e.Level == "ERROR") errors++;
                else if (e.Level == "WARN") warns++;
            }
            count.Text = shown.Count + " lines" + (errors + warns > 0 ? " (" + errors + " errors, " + warns + " warnings)" : "");
        }

        void ScrollToEnd()
        {
            if (!follow.Checked || list.Items.Count == 0) return;
            try { list.EnsureVisible(list.Items.Count - 1); } catch (Exception) { }
        }

        void ShowDetail()
        {
            if (list.SelectedItems.Count == 0) { detail.Text = ""; return; }
            LogEntry e = list.SelectedItems[0].Tag as LogEntry;
            if (e != null) detail.Text = e.Line().Replace("\r", "").Replace("\n", "\r\n");
        }

        /// <summary>Number of rows and how many of them pass the current filters (tests).</summary>
        public int Rows { get { return list.Items.Count; } }

        void CopyAll()
        {
            StringBuilder sb = new StringBuilder();
            foreach (LogEntry e in shown) sb.Append(e.Line()).Append("\r\n");
            try { if (sb.Length > 0) Clipboard.SetText(sb.ToString()); }
            catch (Exception ex) { Ui.Error(FindForm(), "Cannot use the clipboard: " + ex.Message); }
        }

        void ClearView()
        {
            // hide everything logged so far; new lines still appear
            long gen;
            List<LogEntry> all = M.Logs(out gen);
            hiddenUpTo = all.Count > 0 ? all[all.Count - 1].Id : 0;
            Rebuild();
        }

        void OpenFile()
        {
            string p = Path.Combine(M.DataDir, "watchkeeper.log");
            try
            {
                if (!File.Exists(p)) { Ui.Info(FindForm(), "No log file yet."); return; }
                Process.Start(new ProcessStartInfo(p) { UseShellExecute = true });
            }
            catch (Exception ex) { Ui.Error(FindForm(), "Cannot open the log file: " + ex.Message); }
        }

        public override void OnShown() { FitColumn(); Append(); }

        public override void Tick() { Append(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) searchDelay.Dispose();
            base.Dispose(disposing);
        }
    }
}
