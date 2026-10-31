// Settings page: general settings, the service, kept backups of the settings file, and moving settings to another server.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Watchkeeper.UI
{
    public class SettingsPage : Page
    {
        readonly Label service = new Label();
        readonly ListView backups = new ListView();
        public override string Title { get { return "Settings"; } }

        public SettingsPage(Manager m) : base(m)
        {
            Panel scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            Controls.Add(scroll);
            Heading("Settings", "General settings, the Windows service, and the copies kept of every saved version of the settings.");
            FormGrid g = new FormGrid();
            int w = Ui.BaseFont().Height * 30;
            g.Row("Server name", B.Text(w / 2, delegate { return M.Edit.General.ServerName; }, delegate(string v) { M.Edit.General.ServerName = v; }),
                  "Used in alerts. Empty = the computer name (" + Environment.MachineName + ").");
            TextBox ff = B.Text(w, delegate { return M.Edit.General.FfmpegPath; }, delegate(string v) { M.Edit.General.FfmpegPath = v; });
            g.Row("ffmpeg", Ui.Flow(ff, Ui.Button("Browse...", delegate { Browse(ff); })), "ffmpeg.exe, for vertical conversion.");
            TextBox fp = B.Text(w, delegate { return M.Edit.General.FfprobePath; }, delegate(string v) { M.Edit.General.FfprobePath = v; });
            g.Row("ffprobe", Ui.Flow(fp, Ui.Button("Browse...", delegate { Browse(fp); })), "Empty = ffprobe.exe next to ffmpeg.exe.");
            g.Row("", B.Check("Detailed log (for finding a problem)", delegate { return M.Edit.General.DetailedLog; }, delegate(bool v) { M.Edit.General.DetailedLog = v; }));

            g.Section("The service");
            service.AutoSize = true;
            service.MaximumSize = new Size(w + 200, 0);
            g.Row("State", service);
            g.Row("", Ui.Flow(Ui.Button("Install or update", delegate { Install(); }), Ui.Button("Start", delegate { Svc(true); }), Ui.Button("Stop", delegate { Svc(false); }),
                              Ui.Button("Self-test...", delegate { SelfTestNow(); })),
                  "Install or update copies this program to " + Setup.InstalledExe + " and registers the service (it starts with Windows and restarts itself if it stops).");
            g.Row("Data folder", Ui.Flow(Ui.Label(M.DataDir), Ui.Button("Open", delegate { Open(M.DataDir); })),
                  "config.json (the settings), backups, logs, status.json. Only administrators can open it.");

            g.Section("Backups of the settings");
            backups.View = View.Details;
            backups.FullRowSelect = true;
            backups.MultiSelect = false;
            backups.Width = w + 160;
            backups.Height = Ui.BaseFont().Height * 12;
            backups.Columns.Add("Kept", Ui.BaseFont().Height * 11);
            backups.Columns.Add("Why", Ui.BaseFont().Height * 22);
            backups.Columns.Add("Contents", Ui.BaseFont().Height * 18);
            g.Row("", backups, "A copy is kept of every save (and of a hand-edited file before the next save); the newest 50.");
            g.Row("", Ui.Flow(Ui.Button("Show", delegate { ShowBackup(); }), Ui.Button("Restore this version...", delegate { Restore(); }), Ui.Button("Refresh", delegate { FillBackups(); })));

            g.Section("Another server");
            g.Row("", Ui.Flow(Ui.Button("Export settings...", delegate { Export(); }), Ui.Button("Import settings...", delegate { Import(); })),
                  "Export writes the settings to a file to install on another server with deploy.cmd (passwords are left out: they only work on this computer). " +
                  "Import loads a settings file here (version 3 files are converted); check it and save.");
            g.Row("", Ui.Button("Show the import report", delegate { ImportReport(); }), "What was left out when version 3 settings were converted.");
            scroll.Controls.Add(g);
        }

        static void Browse(TextBox t)
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Filter = "Programs (*.exe)|*.exe|All files|*.*";
                if (d.ShowDialog(t.FindForm()) == DialogResult.OK) t.Text = d.FileName;
            }
        }

        static void Open(string dir) { try { Process.Start("explorer.exe", Util.QuoteArg(dir)); } catch (Exception) { } }

        public override void Reload()
        {
            B.Load();
            FillBackups();
        }

        public void FillBackups()
        {
            backups.BeginUpdate();
            backups.Items.Clear();
            foreach (Backup b in M.File.Backups())
            {
                ListViewItem it = new ListViewItem(b.Time.ToString("yyyy-MM-dd HH:mm:ss", Util.Inv));
                it.SubItems.Add(SettingsFile.ReasonText(b.Reason));
                string what;
                try { what = Engine.Summary(SettingsFile.Parse(Util.ReadAllTextRetry(b.File), null)); }
                catch (Exception ex) { what = "cannot be read: " + ex.Message; }
                it.SubItems.Add(what);
                it.Tag = b;
                backups.Items.Add(it);
            }
            backups.EndUpdate();
        }

        public ListView BackupList { get { return backups; } }

        Backup Selected { get { return backups.SelectedItems.Count > 0 ? backups.SelectedItems[0].Tag as Backup : null; } }

        void ShowBackup()
        {
            Backup b = Selected;
            if (b != null) Ui.ShowText(FindForm(), "Settings kept " + b.Time.ToString("yyyy-MM-dd HH:mm:ss", Util.Inv), Util.ReadAllTextRetry(b.File));
        }

        public bool RestoreSelected(bool ask)
        {
            Backup b = Selected;
            if (b == null) return false;
            Settings s;
            try { s = SettingsFile.Parse(Util.ReadAllTextRetry(b.File), null); }
            catch (Exception ex) { Ui.Error(FindForm(), "This version cannot be read: " + ex.Message); return false; }
            if (ask && !Ui.Confirm(FindForm(), "Replace all settings with the version kept " + b.Time.ToString("yyyy-MM-dd HH:mm:ss", Util.Inv) + "?\n\n" + Engine.Summary(s) +
                                                ".\n\nThe current settings are kept as a backup first, so this can be undone the same way.")) return false;
            M.File.Save(s, "restore");
            M.Load();
            return true;
        }

        void Restore() { if (RestoreSelected(true)) Ui.Info(FindForm(), "Restored. The service uses these settings within a few seconds."); }

        void Export()
        {
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Filter = "Settings (*.json)|*.json";
                d.FileName = "watchkeeper-settings.json";
                if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
                Settings copy = SettingsFile.Clone(M.Edit);
                SettingsFile.ForEachSecret(copy, delegate(string v) { return ""; });
                File.WriteAllText(d.FileName, SettingsFile.ToText(copy));
                Ui.Info(FindForm(), "Written to " + d.FileName + ". Passwords were left out; type them on the other server (or into the file in plain text: they are encrypted there when the service reads them).");
            }
        }

        void Import()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Filter = "Settings (*.json)|*.json|All files|*.*";
                if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
                try
                {
                    string text = File.ReadAllText(d.FileName);
                    JObject j = SettingsFile.ParseJson(text) as JObject;
                    List<string> report = new List<string>();
                    Settings s = j != null && SettingsFile.IsVersion3(j) ? Import3.Convert(j, report) : SettingsFile.Parse(text, report);
                    SettingsFile.Normalize(s);
                    if (!Ui.Confirm(FindForm(), "Load " + Engine.Summary(s) + " from " + d.FileName + " in place of the current settings? Nothing is saved until you click Save changes.")) return;
                    M.Edit = s;
                    foreach (Page p in ((MainForm)FindForm()).Pages) p.Reload();
                    M.MarkChanged();
                    if (report.Count > 0) Ui.ShowText(FindForm(), "Import", string.Join("\n", report.ToArray()));
                }
                catch (Exception ex) { Ui.Error(FindForm(), "Cannot import: " + ex.Message); }
            }
        }

        void ImportReport()
        {
            string p = Path.Combine(M.DataDir, "import-report.txt");
            if (!File.Exists(p)) { Ui.Info(FindForm(), "No version 3 settings were converted on this server."); return; }
            Ui.ShowText(FindForm(), "Import report", File.ReadAllText(p));
        }

        void Install()
        {
            try
            {
                List<string> done = Setup.Install(Application.ExecutablePath, null, false);
                Ui.ShowText(FindForm(), "Service", string.Join("\n", done.ToArray()));
            }
            catch (Exception ex) { Ui.Error(FindForm(), ex.Message); }
        }

        void Svc(bool start)
        {
            try { if (start) Setup.StartService(); else if (Ui.Confirm(FindForm(), "Stop the service? Nothing is watched, moved or converted while it is stopped.")) Setup.StopService(); }
            catch (Exception ex) { Ui.Error(FindForm(), ex.Message); }
        }

        void SelfTestNow()
        {
            List<string> lines;
            if (M.ServiceAlive)
            {
                CommandResult r = M.Command("selftest", "", 60000);
                lines = r != null ? r.Lines : new List<string> { "FAIL  the service did not answer within 60 seconds" };
            }
            else lines = SelfTest.Basic(M.DataDir);
            Ui.ShowText(FindForm(), "Self-test", string.Join("\n", lines.ToArray()));
        }

        public override void Live(EngineStatus s)
        {
            Setup.ServiceInfo i = Util.IsWindows ? Setup.Query() : null;
            List<string> lines = new List<string>();
            if (i != null) lines.Add(i.Installed ? "Installed, " + i.Status + "." : "Not installed.");
            if (M.ServiceAlive) lines.Add("Watchkeeper " + s.Version + " running since " + Util.FmtTime(s.Started) + " (process " + s.Pid + ").");
            else lines.Add("Not reporting.");
            if (s.SettingsSource == "backup") lines.Add("config.json cannot be read; the service uses the newest backup: " + s.SettingsError);
            foreach (string n in s.SettingsNotes) lines.Add("In config.json: " + n);
            service.Text = string.Join("\n", lines.ToArray());
            service.ForeColor = M.ServiceAlive && s.SettingsError.Length == 0 ? Ui.Good : Ui.Bad;
        }

        public override Color Dot(EngineStatus s) { return s.SettingsError.Length > 0 ? Ui.Bad : Ui.Good; }
    }
}
