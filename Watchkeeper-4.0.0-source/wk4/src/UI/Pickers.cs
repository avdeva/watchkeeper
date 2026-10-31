// "Choose from running programs" and "Choose..." for a Windows service.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Watchkeeper.UI
{
    public class ProcessPicker : Form
    {
        readonly ListView list = new ListView();
        readonly TextBox filter = new TextBox();
        readonly CheckBox everyone = new CheckBox();
        List<ProcInfo> all = new List<ProcInfo>();
        Dictionary<int, string> servicePids = new Dictionary<int, string>();
        public string Picked;
        /// <summary>Tests: the processes to list instead of this computer's.</summary>
        public static Func<int, List<ProcInfo>> Source;

        public static string Pick(IWin32Window owner, string hint)
        {
            using (ProcessPicker p = new ProcessPicker(hint))
            {
                if (Ui.DialogHook != null) return Ui.DialogHook(p) ? p.Picked : null;
                return p.ShowDialog(owner) == DialogResult.OK ? p.Picked : null;
            }
        }

        public ProcessPicker(string hint)
        {
            Text = "Choose from running programs";
            Font = Ui.BaseFont();
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            int em = Font.Height;
            ClientSize = new Size(em * 64, em * 30);
            Label head = Ui.HelpText("Start the program (for VVD: through its launcher, and wait until it is open), then pick the one that keeps running. " +
                                     "A process that is a Windows service shows its service name: a launcher that is a service is set up as a Windows service entry.", em * 60);
            head.Dock = DockStyle.Top;
            head.Padding = new Padding(10, 8, 10, 4);
            FlowLayoutPanel top = new FlowLayoutPanel();
            top.Dock = DockStyle.Top;
            top.AutoSize = true;
            top.Padding = new Padding(8, 0, 8, 4);
            top.Controls.Add(Ui.Label("Filter"));
            filter.Width = em * 16;
            filter.Text = hint ?? "";
            filter.TextChanged += delegate { Fill(); };
            top.Controls.Add(filter);
            everyone.Text = "Background programs too (all sessions)";
            everyone.AutoSize = true;
            everyone.Margin = new Padding(12, 5, 0, 0);
            everyone.CheckedChanged += delegate { LoadList(); };
            top.Controls.Add(everyone);
            top.Controls.Add(Ui.Button("Refresh", delegate { LoadList(); }));
            list.View = View.Details;
            list.FullRowSelect = true;
            list.MultiSelect = false;
            list.HideSelection = false;
            list.Dock = DockStyle.Fill;
            list.Columns.Add("Program", em * 12);
            list.Columns.Add("PID", em * 4);
            list.Columns.Add("Session", em * 4);
            list.Columns.Add("Windows service", em * 9);
            list.Columns.Add("File", em * 32);
            list.DoubleClick += delegate { Choose(); };
            FlowLayoutPanel bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Bottom;
            bar.AutoSize = true;
            bar.FlowDirection = FlowDirection.RightToLeft;
            bar.Padding = new Padding(8);
            Button cancel = Ui.Button("Cancel", delegate { DialogResult = DialogResult.Cancel; Close(); });
            bar.Controls.Add(cancel);
            bar.Controls.Add(Ui.Button("Use this program", delegate { Choose(); }));
            Controls.Add(list);
            Controls.Add(top);
            Controls.Add(head);
            Controls.Add(bar);
            CancelButton = cancel;
            LoadList();
        }

        public void LoadList()
        {
            int session;
            using (System.Diagnostics.Process me = System.Diagnostics.Process.GetCurrentProcess()) session = me.SessionId;
            try { all = Source != null ? Source(everyone.Checked ? -1 : session) : new SystemProcesses().All(everyone.Checked ? -1 : session); }
            catch (Exception) { all = new List<ProcInfo>(); }
            all.RemoveAll(delegate(ProcInfo p) { return p.Path.Length == 0; });
            servicePids = SystemServices.ServicePids();
            Fill();
        }

        void Fill()
        {
            string f = filter.Text.Trim();
            list.BeginUpdate();
            list.Items.Clear();
            foreach (ProcInfo p in all)
            {
                string svc;
                if (!servicePids.TryGetValue(p.Pid, out svc)) svc = "";
                if (f.Length > 0 && p.Name.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0 && p.Path.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0 &&
                    svc.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0) continue;
                ListViewItem it = new ListViewItem(p.Name);
                it.SubItems.Add(p.Pid.ToString(Util.Inv));
                it.SubItems.Add(p.Session.ToString(Util.Inv));
                it.SubItems.Add(svc);
                it.SubItems.Add(p.Path);
                it.Tag = p;
                list.Items.Add(it);
            }
            list.EndUpdate();
        }

        public int Count { get { return list.Items.Count; } }

        public void ChooseIndex(int i)
        {
            Picked = ((ProcInfo)list.Items[i].Tag).Path;
            DialogResult = DialogResult.OK;
        }

        public void Choose()
        {
            if (list.SelectedItems.Count == 0) return;
            Picked = ((ProcInfo)list.SelectedItems[0].Tag).Path;
            DialogResult = DialogResult.OK;
            Close();
        }

        static readonly Regex VersionFolder = new Regex(@"^(?<pre>[A-Za-z_-]*?)v?\d+(\.\d+)+.*$");

        /// <summary>The path with a folder that looks like a version number (app-2.4.1) replaced by *, or null.</summary>
        public static string WithVersionWildcard(string path)
        {
            string[] parts = path.Split('\\');
            for (int i = 1; i < parts.Length - 1; i++)
            {
                Match m = VersionFolder.Match(parts[i]);
                if (m.Success) { parts[i] = m.Groups["pre"].Value + "*"; return string.Join("\\", parts); }
            }
            return null;
        }
    }

    public static class ServicePicker
    {
        public static string Pick(IWin32Window owner)
        {
            using (Form f = new Form())
            {
                f.Text = "Choose a Windows service";
                f.Font = Ui.BaseFont();
                f.StartPosition = FormStartPosition.CenterParent;
                f.ShowInTaskbar = false;
                int em = f.Font.Height;
                f.ClientSize = new Size(em * 40, em * 28);
                ListView lv = new ListView();
                lv.View = View.Details;
                lv.FullRowSelect = true;
                lv.MultiSelect = false;
                lv.Dock = DockStyle.Fill;
                lv.Columns.Add("Display name", em * 22);
                lv.Columns.Add("Service name", em * 14);
                foreach (KeyValuePair<string, string> kv in SystemServices.List())
                {
                    ListViewItem it = new ListViewItem(kv.Value);
                    it.SubItems.Add(kv.Key);
                    it.Tag = kv.Key;
                    lv.Items.Add(it);
                }
                string picked = null;
                EventHandler use = delegate { if (lv.SelectedItems.Count > 0) { picked = (string)lv.SelectedItems[0].Tag; f.Close(); } };
                lv.DoubleClick += use;
                FlowLayoutPanel bar = new FlowLayoutPanel();
                bar.Dock = DockStyle.Bottom;
                bar.AutoSize = true;
                bar.FlowDirection = FlowDirection.RightToLeft;
                bar.Padding = new Padding(8);
                Button cancel = Ui.Button("Cancel", delegate { f.Close(); });
                bar.Controls.Add(cancel);
                bar.Controls.Add(Ui.Button("Use this service", use));
                f.Controls.Add(lv);
                f.Controls.Add(bar);
                f.CancelButton = cancel;
                if (Ui.DialogHook != null) { Ui.DialogHook(f); return picked; }
                f.ShowDialog(owner);
                return picked;
            }
        }
    }
}
