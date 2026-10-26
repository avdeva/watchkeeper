// The Windows service (install, start, stop), the configuration file (export, import) and
// general settings (ffmpeg, dry-run, priority).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Watchkeeper.UI
{
    public class ServicePage : SectionPage
    {
        readonly Label svcState = new Label();
        readonly StatusDot svcDot = new StatusDot();
        readonly Label svcDetail = new Label();
        readonly Button installBtn, startBtn, stopBtn, restartBtn, removeBtn;
        readonly Label cfgState = new Label();
        readonly PathBox ffmpeg = new PathBox(false, "ffmpeg (ffmpeg.exe)|ffmpeg.exe|Programs (*.exe)|*.exe|All files (*.*)|*.*");
        readonly PathBox ffprobe = new PathBox(false, "ffprobe (ffprobe.exe)|ffprobe.exe|Programs (*.exe)|*.exe|All files (*.*)|*.*");
        readonly Label checkResult = new Label();
        readonly Button check;
        readonly CheckBox dryRun = Ui.Chk("Dry-run mode: every task only logs what it would do; nothing is moved, deleted, started or converted");
        readonly ComboBox priority;
        readonly ComboBox ffAccount;
        readonly TextBox label = Ui.Text(260);
        readonly CheckBox mFolders = Ui.Chk("Folder policies (move from a cache drive, retention)");
        readonly CheckBox mPrograms = Ui.Chk("Watchdog (programs and Windows services, health checks)");
        readonly CheckBox mScripts = Ui.Chk("Scripts");
        readonly CheckBox mVertical = Ui.Chk("Vertical conversion (needs ffmpeg)");
        readonly CheckBox mGpio = Ui.Chk("GPIO: Axia to vMix / HTTP");
        readonly CheckBox mStudios = Ui.Chk("Studios: switch studio configurations on Pathfinder's instruction, report status to Pathfinder");
        bool busy;

        public ServicePage(Manager m)
            : base(m, "general", "Service & settings",
                   "The Watchkeeper service does all the work, even when nobody is signed in. This window only edits its configuration file and shows what it is doing.")
        {
            priority = Ui.Combo(new Choice("high", "High (recommended)"), new Choice("above_normal", "Above normal"), new Choice("normal", "Normal"));
            ffAccount = Ui.Combo(new Choice("local_service", "LOCAL SERVICE, no privileges (recommended)"),
                                 new Choice("restricted_system", "Local System, privileges removed"));
            installBtn = Ui.Btn("Install / update service", delegate { Install(); });
            startBtn = Ui.Btn("Start", delegate { ServiceAction("start"); });
            stopBtn = Ui.Btn("Stop", delegate { ServiceAction("stop"); });
            restartBtn = Ui.Btn("Restart", delegate { ServiceAction("restart"); });
            removeBtn = Ui.Btn("Uninstall", delegate { ServiceAction("uninstall"); });
            foreach (Button b in new[] { installBtn, startBtn, stopBtn, restartBtn, removeBtn }) b.Tag = "nodirty";

            FormGrid g = new FormGrid();
            g.Header("Service");
            svcState.AutoSize = true;
            svcState.Font = Ui.Derive(Ui.BaseFont(), 1.0f, FontStyle.Bold);
            svcState.Margin = new Padding(0, 6, 0, 0);
            svcDot.Margin = new Padding(0, 9, 8, 0);
            g.Row("Status", Ui.Flow(svcDot, svcState));
            svcDetail.AutoSize = true;
            svcDetail.MaximumSize = new Size(640, 0);
            svcDetail.ForeColor = SystemColors.GrayText;
            svcDetail.Margin = new Padding(0, 0, 0, 6);
            svcDetail.Tag = "nodirty";
            g.Row(null, svcDetail);
            g.Row(null, Ui.Flow(installBtn, startBtn, stopBtn, restartBtn, removeBtn));
            g.Note("Install copies this program to " + ServiceSetup.InstalledExe + ", registers the service to start automatically at boot as Local System, " +
                   "sets Windows to restart it if it ever stops unexpectedly, and starts it. Use it again after downloading a new version.");

            g.Header("Configuration file");
            Label path = new Label();
            path.Text = M.Config.Path;
            path.AutoSize = true;
            path.Margin = new Padding(0, 6, 0, 0);
            g.Row("File", path);
            cfgState.AutoSize = true;
            cfgState.MaximumSize = new Size(640, 0);
            cfgState.Margin = new Padding(0, 2, 0, 6);
            cfgState.Tag = "nodirty";
            g.Row(null, cfgState);
            g.Row(null, Ui.Flow(Ui.Btn("Export a copy...", delegate { Export(); }), Ui.Btn("Export for another server...", delegate { ExportTemplate(); }),
                                Ui.Btn("Import...", delegate { Import(); })));
            g.Row(null, Ui.Flow(Ui.Btn("Open folder", delegate { OpenFolder(); }), Ui.Btn("Open in Notepad", delegate { OpenNotepad(); }),
                                Ui.Btn("Earlier versions...", delegate { Versions(); })));
            g.Note("Before every save the settings as they were are kept as a version (the newest " + M.Config.KeepVersions + ", and config.json.bak); " +
                   "Earlier versions... shows them and restores one.");
            g.Note("Every page saves into this one file. The service reads it at start and applies changes within a few seconds. A file with mistakes is rejected and the previous settings stay in force.");
            g.Note("Another server: \"Export for another server\" leaves out passwords and the server name. Put the file next to Watchkeeper.exe and deploy.cmd on that server " +
                   "and run deploy.cmd as administrator (Watchkeeper.exe --deploy --config <file>): it installs the service with that configuration.");

            g.Header("Parts used on this server");
            foreach (CheckBox c in new[] { mFolders, mPrograms, mScripts, mVertical, mGpio, mStudios }) g.Row(null, c);
            g.Note("A part that is off is hidden here and nothing in it runs. Alerts, the log and this page are always there. Saved with the settings below.");

            g.Header("Settings");
            g.Row(null, dryRun);
            g.Row("Service priority", priority, "High keeps the service responsive when the server is busy. Conversions and scripts have their own priorities.");
            g.Row("Server name in alerts", label, "Empty = the computer name (" + Environment.MachineName + ").");
            g.Row("ffmpeg.exe", ffmpeg, "Used by the vertical conversion and to read recording lengths. Not needed when vertical conversion is off.");
            g.Row("ffprobe.exe", ffprobe);
            g.Row("ffmpeg runs as", ffAccount, "ffmpeg reads recordings other accounts can create. As LOCAL SERVICE a flaw in it gives an attacker almost no rights; " +
                                               "the recordings must then be readable by that account (Users can read them on most drives). " +
                                               "Use Local System only when the encoder test fails as LOCAL SERVICE.");
            check = Ui.Btn("Check these programs", delegate { RunCheck(); });
            check.Tag = "nodirty";
            checkResult.AutoSize = true;
            checkResult.MaximumSize = new Size(640, 0);
            checkResult.Margin = new Padding(0, 8, 0, 0);
            checkResult.Tag = "nodirty";
            g.Row(null, Ui.Flow(check));
            g.Wide(checkResult);

            Body.Controls.Add(g);
            Init();
        }

        // ------------------------------------------------------------------ service
        void Install()
        {
            if (!Util.IsWindows) { Ui.Info(FindForm(), "Installing the service needs Windows."); return; }
            if (!Ui.Confirm(FindForm(), "Install or update the Watchkeeper service now?\n\nA running service is stopped for a few seconds while the program is replaced.")) return;
            Run(delegate { return ServiceSetup.Install(Application.ExecutablePath, M.DataDir); }, "Service installed");
        }

        void ServiceAction(string what)
        {
            if (!Util.IsWindows) return;
            if (what == "stop" && !Ui.Confirm(FindForm(), "Stop the service? Folder policies, scripts, program keep-alive and conversions stop until it is started again. Programs already running stay open.")) return;
            if (what == "uninstall" && !Ui.Confirm(FindForm(), "Remove the Watchkeeper service? Nothing will run automatically any more. The configuration file is kept.")) return;
            Run(delegate
            {
                switch (what)
                {
                    case "start": ServiceSetup.StartService(); return "Service started.";
                    case "stop": ServiceSetup.StopService(); return "Service stopped.";
                    case "restart": ServiceSetup.Restart(); return "Service restarted.";
                    default: return ServiceSetup.Uninstall();
                }
            }, null);
        }

        void Run(Func<string> work, string title)
        {
            busy = true;
            Tick();
            svcDetail.Text = "Working...";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string text;
                bool ok = true;
                try { text = work(); }
                catch (Exception ex) { text = ex.Message; ok = false; }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        busy = false;
                        Tick();
                        if (!ok) Ui.Error(FindForm(), text);
                        else if (title != null) ResultDialog.Display(FindForm(), "Watchkeeper service", title, text);
                    });
                }
                catch (Exception) { }
            });
        }

        // ---------------------------------------------------------------- the file
        void Export()
        {
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Filter = "Watchkeeper configuration (*.json)|*.json";
                d.FileName = "Watchkeeper-" + Environment.MachineName + "-" + DateTime.Now.ToString("yyyyMMdd", Util.Inv) + ".json";
                if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
                try
                {
                    File.WriteAllText(d.FileName, Cfg.ToText(M.Config.Get()));
                    Ui.Info(FindForm(), "Saved to " + d.FileName + ".\n\nAn SMTP password in it only works on this computer.");
                }
                catch (Exception ex) { Ui.Error(FindForm(), "Cannot save: " + ex.Message); }
            }
        }

        void ExportTemplate()
        {
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Filter = "Watchkeeper configuration (*.json)|*.json";
                d.FileName = "Watchkeeper-template-" + DateTime.Now.ToString("yyyyMMdd", Util.Inv) + ".json";
                if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
                try
                {
                    List<string> removed = new List<string>();
                    File.WriteAllText(d.FileName, Cfg.ToText(Cfg.ForExport(M.Config.Get(), removed)));
                    Ui.Info(FindForm(), "Saved to " + d.FileName + "." +
                            (removed.Count > 0 ? "\n\nPasswords left out (they only work on this computer): " + string.Join("; ", removed.ToArray()) +
                                                 ". Type them into the file before deploying (they are encrypted on the new server), or enter them in the manager there." : "") +
                            "\n\nOn the other server: Watchkeeper.exe --deploy --config \"" + Path.GetFileName(d.FileName) + "\" (as administrator).");
                }
                catch (Exception ex) { Ui.Error(FindForm(), "Cannot save: " + ex.Message); }
            }
        }

        void Import()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Filter = "Watchkeeper configuration (*.json)|*.json|All files (*.*)|*.*";
                if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
                AppConfig c;
                try { c = Cfg.Parse(File.ReadAllText(d.FileName)); }
                catch (Exception ex) { Ui.Error(FindForm(), "That file cannot be used: " + ex.Message); return; }
                string summary = c.Folders.Count + " folder policies, " + c.Programs.Count + " programs, " + c.Scripts.Count + " scripts" +
                                 (c.Vertical.Enabled ? ", vertical conversion on" : "");
                List<string> runs = Cfg.Executables(c);
                List<string> untrusted = Cfg.UntrustedExecutables(c);
                string what = runs.Count > 0 ? "\n\nWhen switched on it runs:\n" + string.Join("\n", runs.ToArray()) : "";
                if (untrusted.Count > 0) what += "\n\nWARNING: " + untrusted.Count + " of these are in a folder other accounts can change; the service will not run them until they are protected.";
                if (!Ui.Confirm(FindForm(), "Replace ALL current settings with the ones in\n" + d.FileName + "?\n\n" + summary + "." + what +
                                            "\n\nA configuration decides what runs as Local System: import only files you trust. The current settings are kept as a version first (Earlier versions...).")) return;
                List<string> errors = M.Config.SaveAll(c);
                if (errors.Count > 0)
                {
                    ResultDialog.Display(FindForm(), "Import refused", "The file has problems; nothing was changed", "- " + string.Join("\n- ", errors.ToArray()));
                    return;
                }
                Ui.Info(FindForm(), "Imported. The service applies the new settings within a few seconds.");
            }
        }

        void Versions()
        {
            using (VersionsDialog d = new VersionsDialog(M)) d.ShowDialog(FindForm());
        }

        void OpenFolder()
        {
            try { Process.Start(new ProcessStartInfo(M.DataDir) { UseShellExecute = true }); }
            catch (Exception ex) { Ui.Error(FindForm(), "Cannot open the folder: " + ex.Message); }
        }

        void OpenNotepad()
        {
            try { Process.Start(new ProcessStartInfo(Util.SystemExe("notepad.exe"), Util.QuoteArg(M.Config.Path)) { UseShellExecute = false }); }
            catch (Exception ex) { Ui.Error(FindForm(), "Cannot open Notepad: " + ex.Message); }
        }

        // -------------------------------------------------------------- ffmpeg check
        void RunCheck()
        {
            string ff = ffmpeg.Text.Trim().Trim('"'), fp = ffprobe.Text.Trim().Trim('"');
            check.Enabled = false;
            checkResult.ForeColor = SystemColors.GrayText;
            checkResult.Text = "Checking...";
            ThreadPool.QueueUserWorkItem(delegate
            {
                StringBuilder sb = new StringBuilder();
                bool ok = true;
                string ffWhy = PathTrust.Check(ff), fpWhy = PathTrust.Check(fp);
                if (ffWhy != null) sb.AppendLine("ffmpeg: NOT PROTECTED, the service will not run it: " + ffWhy);
                if (fpWhy != null) sb.AppendLine("ffprobe: NOT PROTECTED, the service will not run it: " + fpWhy);
                if (ffWhy != null || fpWhy != null) ok = false;
                string ver = Gpu.FfmpegVersion(ff);
                if (ver.Length == 0) { sb.AppendLine("ffmpeg: cannot run " + ff); ok = false; }
                else
                {
                    sb.AppendLine("ffmpeg: " + ver);
                    EncoderList l = Gpu.ListEncoders(ff);
                    if (l.Ok)
                    {
                        sb.AppendLine("Encoders found: " + (l.Encoders.Count > 0 ? string.Join(", ", l.Encoders.ToArray()) : "none of the expected ones"));
                        string[] want = { "h264_qsv", "h264_nvenc", "h264_amf", "libx264" };
                        List<string> missing = new List<string>();
                        foreach (string w in want) if (!l.Encoders.Contains(w)) missing.Add(w);
                        if (missing.Count > 0) sb.AppendLine("Not in this build: " + string.Join(", ", missing.ToArray()) + " (only matters if you choose that GPU)");
                    }
                }
                try
                {
                    Util.CaptureResult r = Util.RunMedia(fp, new[] { "-version" }, 15000);
                    if (r.ExitCode == 0 && !r.TimedOut)
                    {
                        string first = (r.Output ?? "").Trim();
                        int nl = first.IndexOf('\n');
                        sb.AppendLine("ffprobe: " + (nl >= 0 ? first.Substring(0, nl) : first).Trim());
                    }
                    else { sb.AppendLine("ffprobe: cannot run " + fp); ok = false; }
                }
                catch (Exception) { sb.AppendLine("ffprobe: cannot run " + fp); ok = false; }
                string text = sb.ToString().TrimEnd();
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        check.Enabled = true;
                        checkResult.ForeColor = ok ? Ui.Ok : Ui.Bad;
                        checkResult.Text = text;
                    });
                }
                catch (Exception) { }
            });
        }

        // ------------------------------------------------------------- load / save
        protected override void LoadFrom(AppConfig c)
        {
            GeneralCfg g = c.General;
            dryRun.Checked = g.DryRun;
            Ui.Set(priority, g.ServicePriority);
            label.Text = g.ServerLabel;
            ffmpeg.Text = g.FfmpegPath;
            ffprobe.Text = g.FfprobePath;
            Ui.Set(ffAccount, g.FfmpegAccount);
            mFolders.Checked = g.Modules.Folders;
            mPrograms.Checked = g.Modules.Programs;
            mScripts.Checked = g.Modules.Scripts;
            mVertical.Checked = g.Modules.Vertical;
            mGpio.Checked = g.Modules.Gpio;
            mStudios.Checked = g.Modules.Studios;
        }

        protected override void Collect(AppConfig c)
        {
            GeneralCfg g = c.General;
            g.DryRun = dryRun.Checked;
            g.ServicePriority = Ui.Get(priority);
            g.ServerLabel = label.Text;
            g.FfmpegPath = ffmpeg.Text;
            g.FfprobePath = ffprobe.Text;
            g.FfmpegAccount = Ui.Get(ffAccount);
            g.Modules.Folders = mFolders.Checked;
            g.Modules.Programs = mPrograms.Checked;
            g.Modules.Scripts = mScripts.Checked;
            g.Modules.Vertical = mVertical.Checked;
            g.Modules.Gpio = mGpio.Checked;
            g.Modules.Studios = mStudios.Checked;
        }

        public override void Tick()
        {
            ServiceInfo si = M.Service;
            EngineStatus st = M.Status;
            bool win = Util.IsWindows;
            if (st != null)
            {
                svcDot.Dot = Ui.Ok;
                svcState.Text = "Running" + (st.Mode == "console" ? " (console mode, not as a service)" : "");
                svcDetail.Text = "Version " + st.Version + ", PID " + st.Pid + ", priority " + st.Priority + ", running for " + Ui.Ago(st.StartedAt) +
                                 ". Signed-in desktop user: " + (st.ConsoleSession >= 0 ? st.ConsoleUser + " (session " + st.ConsoleSession + ")" : "none") +
                                 (si.Installed && si.StartType.Length > 0 ? ". Start-up: " + si.StartType : "") + ".";
            }
            else if (si.Installed)
            {
                svcDot.Dot = si.Status == "Stopped" ? Ui.Bad : Ui.Warn;
                svcState.Text = si.Status == "Stopped" ? "Installed but stopped: nothing runs" : si.Status;
                svcDetail.Text = (M.StatusError ?? "") + (si.BinaryPath.Length > 0 ? "\nProgram: " + si.BinaryPath : "");
            }
            else
            {
                svcDot.Dot = Ui.Bad;
                svcState.Text = win ? "Not installed: nothing runs automatically" : "Not reachable";
                svcDetail.Text = win ? "Click \"Install / update service\"." : (M.StatusError ?? "");
            }
            if (busy) svcDetail.Text = "Working...";
            installBtn.Enabled = win && !busy;
            startBtn.Enabled = win && !busy && si.Installed && si.Status == "Stopped";
            stopBtn.Enabled = win && !busy && si.Installed && si.Status == "Running";
            restartBtn.Enabled = win && !busy && si.Installed && si.Status == "Running";
            removeBtn.Enabled = win && !busy && si.Installed;

            if (st == null)
            {
                cfgState.ForeColor = SystemColors.GrayText;
                cfgState.Text = "Saved here; the service applies it when it runs.";
            }
            else if (st.ConfigProblems.Count > 0)
            {
                cfgState.ForeColor = Ui.Bad;
                cfgState.Text = "The service reports problems:\n- " + string.Join("\n- ", st.ConfigProblems.ToArray());
            }
            else if (st.ConfigHash == M.Config.LoadedHash)
            {
                cfgState.ForeColor = Ui.Ok;
                cfgState.Text = "In use by the service since " + Ui.When(st.ConfigLoadedAt) + " (version " + st.ConfigHash + ").";
            }
            else
            {
                cfgState.ForeColor = Ui.Warn;
                cfgState.Text = "The service is still using the previous version of the file (" + st.ConfigHash + "); it applies this one within a few seconds.";
            }
        }
    }
}
