// UI check (Mono under Xvfb): runs a real engine in this process (programs are simulated, ffmpeg is the fake one),
// opens the manager window on the same data folder, and works through every page the way a person would.
//   DISPLAY=:99 mono build/uitest.exe <work folder> <screenshot folder>
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Watchkeeper;
using Watchkeeper.Tests;
using Watchkeeper.UI;

namespace Watchkeeper.UiTest
{
    /// <summary>Programs are simulated; ffmpeg and ffprobe really run (the fake ones).</summary>
    class MixedProcs : IProcesses
    {
        public readonly FakeProcs Fake = new FakeProcs();
        readonly SystemProcesses real = new SystemProcesses();
        public List<ProcInfo> Find(string name) { return Fake.Find(name); }
        public List<ProcInfo> All(int session) { return Fake.All(session); }
        public void Kill(int pid) { Fake.Kill(pid); }
        public int DesktopSession(out string user) { return Fake.DesktopSession(out user); }
        public IRunning Start(StartSpec s)
        {
            string stem = Procs.Stem(s.File);
            return stem == "ffmpeg" || stem == "ffprobe" ? real.Start(s) : Fake.Start(s);
        }
    }

    static class Harness
    {
        static int failures, passes;
        static string shots, work;
        static MainForm form;
        static readonly List<string> messages = new List<string>();
        static Func<string, string, bool> answer = delegate { return true; };

        static void Check(bool ok, string what)
        {
            if (ok) passes++; else failures++;
            Console.WriteLine((ok ? "PASS " : "FAIL ") + what);
        }

        static void Pump(int ms)
        {
            DateTime end = DateTime.Now.AddMilliseconds(ms);
            do { Application.DoEvents(); Thread.Sleep(15); } while (DateTime.Now < end);
        }

        static bool WaitFor(Func<bool> cond, int ms)
        {
            DateTime end = DateTime.Now.AddMilliseconds(ms);
            while (DateTime.Now < end)
            {
                Application.DoEvents();
                form.Poll();
                if (cond()) return true;
                Thread.Sleep(100);
            }
            return cond();
        }

        static void Shot(string name)
        {
            Pump(300);
            try
            {
                Process p = Process.Start(new ProcessStartInfo("import", "-window root " + Path.Combine(shots, name + ".png")) { UseShellExecute = false });
                p.WaitForExit(10000);
            }
            catch (Exception) { }
        }

        static T F<T>(object o, string name) where T : class
        {
            System.Reflection.FieldInfo f = o.GetType().GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (f == null) throw new InvalidOperationException("no field " + name + " on " + o.GetType().Name);
            return (T)f.GetValue(o);
        }

        static Button Btn(Control root, string text)
        {
            foreach (Control c in All(root)) { Button b = c as Button; if (b != null && b.Text == text && b.Visible) return b; }
            throw new InvalidOperationException("no visible button '" + text + "'");
        }

        static IEnumerable<Control> All(Control root)
        {
            foreach (Control c in root.Controls)
            {
                yield return c;
                foreach (Control d in All(c)) yield return d;
            }
        }

        /// <summary>The field in the row labelled so (first match on the page's visible grid).</summary>
        static T Field<T>(Control root, string label) where T : Control
        {
            foreach (Control c in All(root))
            {
                Label l = c as Label;
                if (l == null || l.Text != label || !l.Visible) continue;
                TableLayoutPanel t = l.Parent as TableLayoutPanel;
                if (t == null) continue;
                TableLayoutPanelCellPosition pos = t.GetPositionFromControl(l);
                Control f = t.GetControlFromPosition(1, pos.Row);
                if (f is T) return (T)f;
                foreach (Control d in All(f)) if (d is T) return (T)d;
            }
            throw new InvalidOperationException("no field '" + label + "'");
        }

        static CheckBox Tick(Control root, string text)
        {
            foreach (Control c in All(root)) { CheckBox b = c as CheckBox; if (b != null && b.Text == text) return b; }
            throw new InvalidOperationException("no check box '" + text + "'");
        }

        static void Choose(ComboBox c, string text)
        {
            for (int i = 0; i < c.Items.Count; i++) if ((string)c.Items[i] == text) { c.SelectedIndex = i; return; }
            throw new InvalidOperationException("no choice '" + text + "'");
        }

        static int Main(string[] args)
        {
            work = args[0];
            shots = args[1];
            if (Directory.Exists(work)) Directory.Delete(work, true);
            Directory.CreateDirectory(work);
            Directory.CreateDirectory(shots);
            string bin = AppDomain.CurrentDomain.BaseDirectory;
            Environment.SetEnvironmentVariable("FAKE_DELAY", "0.05");
            Environment.SetEnvironmentVariable("FAKE_LOG", null);
            string data = Path.Combine(work, "data");

            MixedProcs procs = new MixedProcs();
            FakeServices services = new FakeServices();
            Engine engine = new Engine(data, procs, services);
            engine.TickMs = 500;
            engine.Vertical.PollMs = 500;
            engine.Watchdog.CloseWaitMs = 200;
            engine.Start();
            Ui.MessageHook = delegate(string kind, string text) { lock (messages) messages.Add(kind + ": " + text); return answer(kind, text); };

            Application.EnableVisualStyles();
            Manager m = new Manager(data);
            form = new MainForm(m);
            form.Show();
            Pump(1500);
            int rc = 1;
            try
            {
                Run(m, procs, services, engine, bin);
                rc = failures == 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAIL unexpected: " + ex);
                failures++;
            }
            form.Close();
            Ui.MessageHook = null;
            engine.Stop();
            Console.WriteLine(passes + " passed, " + failures + " failed");
            Console.WriteLine(failures == 0 ? "ALL UI CHECKS PASSED" : "UI CHECKS FAILED");
            return rc;
        }

        static void Run(Manager m, MixedProcs procs, FakeServices services, Engine engine, string bin)
        {
            // ---- overview
            Check(WaitFor(delegate { return m.ServiceAlive; }, 8000), "the manager sees the running service");
            OverviewPage ov = (OverviewPage)form.Pages[0];
            ListView ovList = F<ListView>(ov, "list");
            Check(ovList.Items.Count >= 3 && ovList.Items[0].SubItems[2].Text.StartsWith("Running since"), "overview lists the service and the parts");
            Shot("01-overview");

            // ---- watchdog
            WatchdogPage wd = (WatchdogPage)form.PageByKey("watchdog");
            form.ShowPage(wd);
            Pump(300);
            Btn(wd, "Add").PerformClick();
            Pump(200);
            string exe = Path.Combine(work, "Apps", "vMix64.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exe));
            File.WriteAllText(exe, "x");
            Check(m.Dirty && F<Button>(form, "save").Enabled, "adding an entry marks the settings changed");
            Field<TextBox>(wd, "Name").Text = "vMix";
            Field<TextBox>(wd, "Program").Text = exe;
            Field<NumericUpDown>(wd, "First start").Value = 0;
            Tick(wd, "Keep this running").Checked = true;
            Pump(200);
            ProgramEntry p0 = m.Edit.Watchdog[0];
            Check(p0.Name == "vMix" && p0.Path == exe && p0.Enabled && p0.StartDelaySeconds == 0, "the fields write into the settings");
            form.SaveAll();
            Check(!m.Dirty && !F<Button>(form, "save").Enabled, "saved: nothing left to save");
            Check(WaitFor(delegate { return procs.Fake.Starts.Exists(delegate(StartSpec s) { return s.File == exe; }); }, 8000), "the service starts the new program");
            Check(WaitFor(delegate { ProgramStatus s = m.Status.Watchdog.Find(delegate(ProgramStatus x) { return x.Name == "vMix"; }); return s != null && s.State == "running"; }, 8000),
                  "the page shows it running");
            Pump(600);
            Check(F<Label>(wd, "live").Text.StartsWith("Running (PID"), "the entry's state line (" + F<Label>(wd, "live").Text + ")");
            Shot("02-watchdog");

            // the Windows service fields replace the program fields
            ComboBox what = Field<ComboBox>(wd, "What");
            Choose(what, "A Windows service");
            Pump(200);
            bool progHidden = true;
            try { Field<TextBox>(wd, "Program"); progHidden = false; } catch (InvalidOperationException) { }
            Check(progHidden && Field<TextBox>(wd, "Service name").Visible, "a Windows service shows the service name instead of the program");
            Choose(what, "A program");
            Pump(200);

            // choose from running programs: the version folder becomes a wildcard
            string vvd = Path.Combine(work, "Local", "VVD", "app-2.4.1", "Virtual Video Director.exe").Replace('/', '\\');
            ProcessPicker.Source = delegate(int s) { return new List<ProcInfo> { new ProcInfo { Pid = 4321, Session = 1, Name = "Virtual Video Director", Path = vvd } }; };
            Ui.DialogHook = delegate(Form f) { ProcessPicker pp = f as ProcessPicker; if (pp != null) { F<TextBox>(pp, "filter").Text = ""; if (pp.Count > 0) { pp.ChooseIndex(0); return true; } }  return false; };
            Btn(wd, "Choose from running programs...").PerformClick();
            Pump(200);
            Check(m.Edit.Watchdog[0].WatchPath.Contains("app-*"), "Choose from running programs offers the version wildcard (" + m.Edit.Watchdog[0].WatchPath + ")");
            Ui.DialogHook = null;
            Field<TextBox>(wd, "Application to watch").Text = "";
            Field<TextBox>(wd, "Daily restart").Text = "25:00";
            Pump(200);
            Check(F<Label>(form, "problems").Text.Contains("1 problem"), "a wrong time shows as a problem (" + F<Label>(form, "problems").Text + ")");
            answer = delegate(string kind, string text) { return false; };
            form.SaveAll();
            Check(m.Dirty, "saving with a problem asks first (answered no: not saved)");
            answer = delegate { return true; };
            Field<TextBox>(wd, "Daily restart").Text = "";
            Pump(200);
            form.SaveAll();
            Check(!m.Dirty, "saved after fixing it");

            // ---- folder tasks
            FoldersPage fp = (FoldersPage)form.PageByKey("folder tasks");
            form.ShowPage(fp);
            Pump(300);
            Btn(fp, "Add").PerformClick();
            Pump(200);
            string cache = Path.Combine(work, "cache"), store = Path.Combine(work, "store");
            Directory.CreateDirectory(cache);
            string rec = Path.Combine(cache, "Cam1.mp4");
            File.WriteAllText(rec, "recording");
            File.SetLastWriteTime(rec, DateTime.Now.AddMinutes(-10));
            Field<TextBox>(fp, "Name").Text = "Move recordings";
            Field<TextBox>(fp, "Folder").Text = cache;
            Field<TextBox>(fp, "To").Text = store;
            Choose(Field<ComboBox>(fp, "Run"), "Only with Run now");
            Tick(fp, "Run this task").Checked = true;
            Pump(200);
            bool everyHidden = true;
            try { Field<NumericUpDown>(fp, "Every"); everyHidden = false; } catch (InvalidOperationException) { }
            Check(everyHidden, "'Only with Run now' hides the interval");
            string preview = fp.PreviewText();
            Check(preview.Contains("MOVE") && preview.Contains("Cam1.mp4") && File.Exists(rec), "Preview lists the move and changes nothing");
            form.SaveAll();
            Pump(1200);
            lock (messages) messages.Clear();
            Btn(fp, "Run now").PerformClick();
            Check(WaitFor(delegate { return File.Exists(Path.Combine(store, "Cam1.mp4")); }, 8000), "Run now: the service moves the file");
            Check(WaitFor(delegate { FolderTaskStatus s = m.Status.Folders.Find(delegate(FolderTaskStatus x) { return x.Name == "Move recordings"; }); return s != null && s.Text.Contains("1 moved"); }, 5000),
                  "the task's state shows the result");
            Shot("03-folders");

            // ---- vertical
            VerticalPage vp = (VerticalPage)form.PageByKey("vertical conversion");
            form.ShowPage(vp);
            Pump(300);
            string vin = Path.Combine(work, "vin"), vout = Path.Combine(work, "vout"), vorig = Path.Combine(work, "vorig");
            Directory.CreateDirectory(vin);
            m.Edit.General.FfmpegPath = Path.Combine(bin, "fakes", "ffmpeg");
            m.Edit.General.FfprobePath = Path.Combine(bin, "fakes", "ffprobe");
            Field<TextBox>(vp, "Folder to watch").Text = vin;
            Field<TextBox>(vp, "Output folder").Text = vout;
            Field<TextBox>(vp, "Originals to").Text = vorig;
            Field<NumericUpDown>(vp, "Wait").Value = 2;
            Choose(Field<ComboBox>(vp, "Use"), "CPU only");
            Btn(vp, "Add source").PerformClick();
            Pump(100);
            DataGridView grid = vp.SourcesGrid;
            grid.Rows[0].Cells[0].Value = true;
            grid.Rows[0].Cells[1].Value = "PGM";
            grid.Rows[0].Cells[2].Value = "Recorder7";
            grid.Rows[0].Cells[3].Value = "Vertical PGM";
            Tick(vp, "Convert recordings").Checked = true;
            Pump(200);
            Check(m.Edit.Vertical.Sources.Count == 1 && m.Edit.Vertical.Sources[0].Prefix == "Vertical PGM", "the sources table writes into the settings");
            form.SaveAll();
            string media = Path.Combine(vin, "Recorder7 - Cam1 - 1 May 2026 - 10-00-00 AM.mp4");
            Env.WriteMedia(media);
            File.SetLastWriteTime(media, DateTime.Now.AddSeconds(-30));
            Check(WaitFor(delegate { return File.Exists(Path.Combine(vout, "Vertical PGM - 1 May 2026 - 10-00-00 AM.mp4")); }, 15000), "the service converts a recording");
            Check(WaitFor(delegate { return m.Status.Vertical.Recent.Count > 0 && m.Status.Vertical.Recent[0].State == "done"; }, 5000), "the page lists the conversion");
            Shot("04-vertical");

            // ---- GPIO
            using (FakeAxia axia = new FakeAxia())
            using (FakeVmix vmix = new FakeVmix())
            {
                GpioPage gp = (GpioPage)form.PageByKey("gpio");
                form.ShowPage(gp);
                gp.Tabs.SelectedIndex = 1;
                Pump(300);
                Btn(gp, "Add device").PerformClick();
                Btn(gp, "Add vMix").PerformClick();
                Pump(200);
                gp.DevicesGrid.Rows[0].Cells[0].Value = true;
                gp.DevicesGrid.Rows[0].Cells[1].Value = "Node 1";
                gp.DevicesGrid.Rows[0].Cells[2].Value = "127.0.0.1";
                gp.DevicesGrid.Rows[0].Cells[3].Value = axia.Port.ToString();
                gp.VmixGrid.Rows[0].Cells[1].Value = "vMix";
                gp.VmixGrid.Rows[0].Cells[2].Value = "127.0.0.1";
                gp.VmixGrid.Rows[0].Cells[3].Value = vmix.Port.ToString();
                Pump(100);
                Check(m.Edit.Gpio.Devices[0].Port == axia.Port && m.Edit.Gpio.Vmix[0].Port == vmix.Port, "device and vMix tables write into the settings");
                gp.Tabs.SelectedIndex = 0;
                Pump(200);
                Btn(gp, "Add").PerformClick();
                Pump(200);
                Field<TextBox>(gp, "Name").Text = "Mic live";
                Pump(100);
                Ui.DialogHook = delegate(Form f)
                {
                    foreach (Control c in All(f)) { ComboBox cb = c as ComboBox; if (cb != null && cb.DropDownStyle == ComboBoxStyle.DropDown) cb.Text = "OverlayInput1In"; }
                    return true;
                };
                Btn(gp, "Add vMix function...").PerformClick();
                Ui.DialogHook = null;
                GpioTrigger t = m.Edit.Gpio.Triggers[0];
                t.Port = 1;
                t.Pin = 3;
                Check(t.WhenOn.Count == 1 && t.WhenOn[0].Function == "OverlayInput1In" && t.DeviceId == m.Edit.Gpio.Devices[0].Id, "a trigger with a vMix action");
                lock (messages) messages.Clear();
                Btn(gp, "Test: run the 'on' actions").PerformClick();
                Check(vmix.Calls.Contains("/api/?Function=OverlayInput1In"), "Test runs the action against vMix");
                Tick(gp, "Use GPIO (connect to the devices and run the triggers)").Checked = true;
                Pump(100);
                form.SaveAll();
                Check(WaitFor(delegate { return m.Status.Gpio.Devices.Count == 1 && m.Status.Gpio.Devices[0].State == "connected"; }, 10000), "the service connects to the device");
                Pump(2500);
                int before = vmix.Calls.Count;
                axia.Push("GPI 1 hhlhh");
                Check(WaitFor(delegate { return vmix.Calls.Count > before; }, 8000), "a pin change runs the trigger in the service");
                gp.Tabs.SelectedIndex = 2;
                Check(WaitFor(delegate { return F<ListView>(gp, "events").Items.Count > 0; }, 5000), "the Live tab lists the change");
                Shot("05-gpio");
                Tick(gp, "Use GPIO (connect to the devices and run the triggers)").Checked = false;
                form.SaveAll();
            }

            // ---- alerts
            using (FakeSmtp smtp = new FakeSmtp())
            {
                smtp.OfferAuth = false;
                AlertsPage ap = (AlertsPage)form.PageByKey("alerts");
                form.ShowPage(ap);
                Pump(300);
                Btn(ap, "Add").PerformClick();
                Pump(200);
                Field<TextBox>(ap, "Name").Text = "Ops mail";
                Field<TextBox>(ap, "Mail server").Text = "127.0.0.1";
                Field<NumericUpDown>(ap, "Mail server").Value = smtp.Port;
                Choose(Field<ComboBox>(ap, "Encryption"), "None (internal relay, port 25)");
                Field<TextBox>(ap, "From").Text = "wk@example.com";
                Field<TextBox>(ap, "To").Text = "ops@example.com";
                Pump(100);
                bool jiraHidden = true;
                try { Field<TextBox>(ap, "Jira address"); jiraHidden = false; } catch (InvalidOperationException) { }
                Check(jiraHidden, "e-mail shows only the e-mail fields");
                Check(ap.TestNow().StartsWith("Sent") && WaitFor(delegate { return smtp.Messages.Count == 1; }, 5000), "Send a test message");
                form.SaveAll();
                Pump(1200);
                procs.Fake.Crash(exe);
                Check(WaitFor(delegate { return smtp.Messages.Exists(delegate(string x) { return x.Contains("vMix stopped"); }); }, 10000), "a program that stops sends an e-mail");
                Check(WaitFor(delegate { return m.Status.Alerts.Recent.Count > 0; }, 5000), "the alert is listed on the page");
                Pump(600);
                Shot("06-alerts");
            }

            // ---- settings: backups and restore
            SettingsPage sp = (SettingsPage)form.PageByKey("settings");
            form.ShowPage(sp);
            Pump(300);
            sp.FillBackups();
            ListView bl = sp.BackupList;
            Check(bl.Items.Count >= 6, "backups of every save are listed (" + bl.Items.Count + ")");
            int idx = -1;
            for (int i = 0; i < bl.Items.Count; i++) if (((Backup)bl.Items[i].Tag).Reason == "manager" && File.ReadAllText(((Backup)bl.Items[i].Tag).File).Contains("\"name\": \"vMix\"") &&
                                                         !File.ReadAllText(((Backup)bl.Items[i].Tag).File).Contains("Move recordings")) { idx = i; break; }
            Check(idx >= 0, "a backup from before the folder task exists");
            if (idx >= 0)
            {
                bl.Items[idx].Selected = true;
                Check(sp.RestoreSelected(false) && m.Edit.Folders.Count == 0 && m.Edit.Watchdog.Count == 1, "restoring a backup brings back those settings");
            }
            Shot("07-settings");

            // ---- log
            LogPage lp = (LogPage)form.PageByKey("log");
            form.ShowPage(lp);
            Check(WaitFor(delegate { return lp.Count > 10; }, 5000), "the log page shows the service's log (" + lp.Count + " lines)");
            Shot("08-log");

            // ---- the window can be made small
            form.ClientSize = new Size(form.MinimumSize.Width, form.MinimumSize.Height);
            form.ShowPage(wd);
            Pump(400);
            Shot("09-small");
            Check(form.ClientSize.Height <= form.MinimumSize.Height + 10, "the window can be made small");
        }
    }
}
