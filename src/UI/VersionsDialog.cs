// Earlier versions of the configuration file: every save keeps the file as it was before (backups folder next to
// config.json). Shows them with what each contains, and restores one (the current file is kept as a version first).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Watchkeeper.UI
{
    public class VersionsDialog : Form
    {
        readonly Manager m;
        readonly ListView list = new ListView();
        readonly Button show, restore;
        public bool Restored;

        public VersionsDialog(Manager m)
        {
            this.m = m;
            Text = "Earlier versions of the configuration";
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            FormBorderStyle = FormBorderStyle.Sizable;
            Font = Ui.BaseFont();
            ClientSize = new Size(Math.Max(760, Font.Height * 50), Math.Max(420, Font.Height * 26));
            MinimumSize = new Size(520, 300);

            Label head = new Label();
            head.Text = "Before every save, import, restore or deploy, and when the file is changed outside Watchkeeper, the settings as they were are kept here " +
                        "(the newest " + m.Config.KeepVersions + "). Folder: " + m.Config.BackupDir;
            head.Dock = DockStyle.Top;
            head.AutoSize = false;
            head.Height = Font.Height * 3 + 16;
            head.Padding = new Padding(14, 12, 14, 0);

            list.View = View.Details;
            list.FullRowSelect = true;
            list.HideSelection = false;
            list.MultiSelect = false;
            list.Dock = DockStyle.Fill;
            list.Columns.Add("Kept", 170);
            list.Columns.Add("Kept because", 190);
            list.Columns.Add("Contents", 380);
            list.SelectedIndexChanged += delegate { UpdateButtons(); };
            list.DoubleClick += delegate { Show2(); };

            Panel bar = new Panel();
            bar.Dock = DockStyle.Bottom;
            bar.Height = 52;
            bar.Padding = new Padding(14, 10, 14, 10);
            Button close = Ui.Btn("Close", delegate { Close(); });
            restore = Ui.Btn("Restore this version...", delegate { Restore(); });
            show = Ui.Btn("Show", delegate { Show2(); });
            foreach (Button b in new[] { close, restore, show })
            {
                b.Dock = DockStyle.Right;
                b.Margin = new Padding(0);
                Panel gap = new Panel();
                gap.Dock = DockStyle.Right;
                gap.Width = 8;
                bar.Controls.Add(b);
                bar.Controls.Add(gap);
            }

            Panel pad = new Panel();
            pad.Dock = DockStyle.Fill;
            pad.Padding = new Padding(14, 6, 14, 6);
            pad.Controls.Add(list);
            Controls.Add(pad);
            Controls.Add(bar);
            Controls.Add(head);
            CancelButton = close;
            Fill();
        }

        static string Why(string why)
        {
            switch (why)
            {
                case "before-edit": return "changed outside Watchkeeper";
                case "before-import": return "before an import";
                case "before-restore": return "before a restore";
                case "before-deploy": return "before a deploy";
                case "save": return "before a save";
            }
            if (why.StartsWith("before-")) return "before saving " + why.Substring(7);
            return why;
        }

        static string Summary(string file)
        {
            try
            {
                AppConfig c = Cfg.Parse(File.ReadAllText(file));
                return c.Folders.Count + " folder policies, " + c.Programs.Count + " programs, " + c.Scripts.Count + " scripts, " +
                       c.Gpio.Triggers.Count + " GPIO triggers, " + c.Alerts.Destinations.Count + " alert destinations";
            }
            catch (Exception ex) { return "cannot be read: " + ex.Message; }
        }

        void Fill()
        {
            list.Items.Clear();
            foreach (ConfigStore.Version v in m.Config.Versions())
            {
                ListViewItem it = new ListViewItem(v.Time.ToString("yyyy-MM-dd HH:mm:ss", Util.Inv));
                it.SubItems.Add(Why(v.Why));
                it.SubItems.Add(Summary(v.File));
                it.Tag = v;
                list.Items.Add(it);
            }
            if (list.Items.Count == 0)
            {
                ListViewItem none = new ListViewItem("(none yet)");
                none.SubItems.Add("");
                none.SubItems.Add("A version is kept at the next save.");
                list.Items.Add(none);
            }
            UpdateButtons();
        }

        ConfigStore.Version Selected
        {
            get { return list.SelectedItems.Count > 0 ? list.SelectedItems[0].Tag as ConfigStore.Version : null; }
        }

        void UpdateButtons()
        {
            show.Enabled = restore.Enabled = Selected != null;
        }

        void Show2()
        {
            ConfigStore.Version v = Selected;
            if (v == null) return;
            string text;
            try { text = File.ReadAllText(v.File); }
            catch (Exception ex) { Ui.Error(this, "Cannot read " + v.File + ": " + ex.Message); return; }
            ResultDialog.Display(this, "Configuration kept " + v.Time.ToString("yyyy-MM-dd HH:mm:ss", Util.Inv), v.File, text);
        }

        void Restore()
        {
            ConfigStore.Version v = Selected;
            if (v == null) return;
            AppConfig c;
            try { c = Cfg.Parse(File.ReadAllText(v.File)); }
            catch (Exception ex) { Ui.Error(this, "This version cannot be used: " + ex.Message); return; }
            List<string> runs = Cfg.Executables(c);
            List<string> untrusted = Cfg.UntrustedExecutables(c);
            string what = runs.Count > 0 ? "\n\nWhen switched on it runs:\n" + string.Join("\n", runs.ToArray()) : "";
            if (untrusted.Count > 0) what += "\n\nWARNING: " + untrusted.Count + " of these are in a folder other accounts can change; the service will not run them until they are protected.";
            if (!Ui.Confirm(this, "Replace ALL current settings with the version kept " + v.Time.ToString("yyyy-MM-dd HH:mm:ss", Util.Inv) + "?\n\n" +
                                  Summary(v.File) + "." + what + "\n\nThe current settings are kept as a version first, so this can be undone the same way.")) return;
            List<string> errors = m.Config.SaveAll(c, "restore");
            if (errors.Count > 0)
            {
                ResultDialog.Display(this, "Restore refused", "This version has problems; nothing was changed", "- " + string.Join("\n- ", errors.ToArray()));
                return;
            }
            Restored = true;
            Ui.Info(this, "Restored. The service applies these settings within a few seconds.");
            Fill();
        }
    }
}
