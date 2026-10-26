// UI test: runs the real engine (console mode, fake ffmpeg, fake process world) and the
// real manager window connected to it over the control pipe, under Xvfb (Mono).
//   mono uitest.exe <workDir> <shotDir>
// Prints PASS/FAIL lines and takes screenshots with ImageMagick "import".
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Watchkeeper;
using Watchkeeper.Tests;
using Watchkeeper.UI;

namespace Watchkeeper.UiTest
{
    /// <summary>Programs "start" instantly and show up under their name; nothing really runs.</summary>
    class FakeProcs : ILauncher, IProcessTable, ISessions
    {
        public readonly List<ProcInfo> Running = new List<ProcInfo>();
        public readonly List<StartRequest> Starts = new List<StartRequest>();
        readonly SystemLauncher real = new SystemLauncher();
        int pid = 5000;

        class Handle : IRunning
        {
            public int pid;
            public int Pid { get { return pid; } }
            public TextReader StdOut { get { return null; } }
            public TextReader StdErr { get { return null; } }
            public bool WaitForExit(int ms) { return false; }
            public int ExitCode { get { return 0; } }
            public void Kill() { }
            public void Dispose() { }
        }

        public IRunning Start(StartRequest r)
        {
            // scripts and ffmpeg run for real; desktop programs are simulated
            if (!r.UserSession) return real.Start(r);
            lock (Running)
            {
                Starts.Add(r);
                ProcInfo p = new ProcInfo();
                p.Pid = ++pid;
                p.Name = Procs.Stem(r.File);
                p.SessionId = 1;
                Running.Add(p);
                Handle h = new Handle();
                h.pid = p.Pid;
                return h;
            }
        }

        public List<ProcInfo> Find(string name)
        {
            string n = Procs.Stem(name);
            lock (Running) return Running.FindAll(delegate(ProcInfo p) { return p.Name == n; });
        }

        public void Kill(int id)
        {
            lock (Running) Running.RemoveAll(delegate(ProcInfo p) { return p.Pid == id; });
        }

        public int ConsoleUserSession(out string user) { user = "STUDIO\\operator"; return 1; }
        public string ConsoleUserTemp() { return Path.GetTempPath(); }
    }

    static class Harness
    {
        static int failures;
        static string shotDir;
        static MainForm form;
        static Engine engine;
        static Manager manager;
        static string armedButton;
        static string dialogText = "";
        static int dialogsHandled;

        static void ArmDialog(string button)
        {
            armedButton = button ?? "";
            dialogText = "";
            dialogsHandled = 0;
        }

        static void HandleDialogs()
        {
            if (armedButton == null) return;
            List<Form> open = new List<Form>();
            foreach (Form f in Application.OpenForms) open.Add(f);
            foreach (Form f in open)
            {
                if (f == form || !f.Visible) continue;
                StringBuilder sb = new StringBuilder();
                foreach (TextBox t in All<TextBox>(f)) sb.Append(t.Text).Append('\n');
                foreach (Label l in All<Label>(f)) sb.Append(l.Text).Append('\n');
                dialogText = sb.ToString();
                string want = armedButton;
                armedButton = null;
                dialogsHandled++;
                Button b = want.Length == 0 ? null : Find<Button>(f, delegate(Button x) { return x.Text.Replace("&", "") == want; });
                if (b != null) b.PerformClick(); else f.Close();
                return;
            }
        }

        static void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "PASS " : "FAIL ") + what);
            if (!ok) failures++;
        }

        static void Shot(string name)
        {
            Application.DoEvents();
            Thread.Sleep(400);
            Application.DoEvents();
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("import", "-window root \"" + Path.Combine(shotDir, name + ".png") + "\"");
                psi.UseShellExecute = false;
                using (Process p = Process.Start(psi)) p.WaitForExit(15000);
            }
            catch (Exception ex) { Console.WriteLine("shot failed: " + ex.Message); }
        }

        static void Pump(int ms)
        {
            DateTime end = DateTime.Now.AddMilliseconds(ms);
            while (DateTime.Now < end) { Application.DoEvents(); Thread.Sleep(20); }
        }

        static bool WaitFor(Func<bool> cond, int ms)
        {
            DateTime end = DateTime.Now.AddMilliseconds(ms);
            while (DateTime.Now < end)
            {
                Application.DoEvents();
                if (cond()) return true;
                Thread.Sleep(30);
            }
            return cond();
        }

        static List<T> All<T>(Control root) where T : Control
        {
            List<T> list = new List<T>();
            Collect<T>(root, list);
            return list;
        }

        static void Collect<T>(Control c, List<T> list) where T : Control
        {
            T t = c as T;
            if (t != null) list.Add(t);
            foreach (Control ch in c.Controls) Collect<T>(ch, list);
        }

        static T Find<T>(Control root, Func<T, bool> pred) where T : Control
        {
            foreach (T t in All<T>(root)) if (pred(t)) return t;
            return null;
        }

        static T F<T>(object o, string name)
        {
            FieldInfo fi = o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            return (T)fi.GetValue(o);
        }

        static Button Btn(Control root, string text)
        {
            return Find<Button>(root, delegate(Button b) { return b.Text == text; });
        }

        static Page Go(string key)
        {
            form.SelectKey(key);
            Pump(600);
            return form.PageFor(key);
        }

        static void Touch(string dir, string name, double daysOld)
        {
            Directory.CreateDirectory(dir);
            string p = Path.Combine(dir, name);
            File.WriteAllText(p, "data");
            File.SetLastWriteTime(p, DateTime.Now.AddDays(-daysOld));
        }

        static void WriteMedia(string path, double duration)
        {
            JObject o = new JObject();
            o.Set("fake", true);
            o.Set("width", 1920.0);
            o.Set("height", 1080.0);
            o.Set("duration", duration);
            o.Set("bit_rate", 8530000.0);
            o.Set("codec", "h264");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, Json.Serialize(o, false));
        }

        static EngineStatus Status() { return manager.Status; }

        [STAThread]
        static int Main(string[] args)
        {
            string work = Path.GetFullPath(args[0]);
            shotDir = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(shotDir);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            List<string> uiErrors = new List<string>();
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { uiErrors.Add(e.Exception.ToString()); };

            string data = Path.Combine(work, "data");
            string fakes = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fakes"));
            Environment.SetEnvironmentVariable("FAKE_DELAY", "2");

            // ---- configuration the service starts with: starter template pointed at temp folders
            AppConfig c = Cfg.Starter();
            c.General.FfmpegPath = Path.Combine(fakes, "ffmpeg");
            c.General.FfprobePath = Path.Combine(fakes, "ffprobe");
            foreach (FolderPolicy f in c.Folders)
            {
                string leaf = f.Path.Substring(f.Path.LastIndexOf('\\') + 1);
                bool cache = f.Path.StartsWith(Cfg.CacheDrive);
                f.Path = Path.Combine(work, cache ? "Cache" : "Data", leaf);
                if (f.Destination.Length > 0) f.Destination = Path.Combine(work, "Data", f.Destination.Substring(f.Destination.LastIndexOf('\\') + 1));
                foreach (FileRule r in f.Rules)
                    if (r.Destination.Length > 0) r.Destination = Path.Combine(work, "Data", r.Destination.Substring(r.Destination.LastIndexOf('\\') + 1));
            }
            c.Vertical.WatchDir = Path.Combine(work, "Vertical", "Ingest");
            c.Vertical.OutDir = Path.Combine(work, "Vertical");
            c.Vertical.MoveOriginalTo = Path.Combine(work, "Vertical", "Originals");
            c.Vertical.Gpu.Session = "service";
            foreach (ProgramJob p in c.Programs) p.ExePath = Path.Combine(work, "bin", p.ExePath.Substring(p.ExePath.LastIndexOfAny(new[] { '\\', '/' }) + 1));
            Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, "config.json"), Cfg.ToText(c));
            Directory.CreateDirectory(Path.Combine(work, "bin"));
            foreach (ProgramJob p in c.Programs) File.WriteAllText(p.ExePath, "fake");
            Touch(c.Folders[0].Path, "old-recording.mp4", 9);
            Touch(c.Folders[0].Path, "new-recording.mp4", 1);

            FakeProcs procs = new FakeProcs();
            FakeServices services = new FakeServices();
            services.State["Spooler"] = "stopped";
            FakeVmix vmx = new FakeVmix();
            FakeAxia axia = new FakeAxia();
            EngineOptions o = new EngineOptions();
            o.Services = services;
            o.Launcher = procs;
            o.Table = procs;
            o.Sessions = procs;
            o.PipeName = "wk-ui-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            o.AdapterDetector = delegate
            {
                List<KeyValuePair<string, string>> l = new List<KeyValuePair<string, string>>();
                l.Add(new KeyValuePair<string, string>("Intel(R) UHD Graphics 770", "31.0.101.4502"));
                l.Add(new KeyValuePair<string, string>("NVIDIA GeForce RTX 3060", "552.22"));
                return Gpu.FromNames(l);
            };
            engine = new Engine(data, o);
            engine.Start();

            manager = new Manager(data, o.PipeName);
            manager.ServiceQuery = delegate { return new ServiceInfo(); };
            form = new MainForm(manager, "overview");
            System.Windows.Forms.Timer dialogTimer = new System.Windows.Forms.Timer();
            dialogTimer.Interval = 150;
            dialogTimer.Tick += delegate { HandleDialogs(); };
            dialogTimer.Start();
            form.Show();
            Check(WaitFor(delegate { return manager.Online; }, 8000), "manager connects to the engine over the pipe");
            Pump(1500);

            // ---- overview
            OverviewPage ov = (OverviewPage)form.PageFor("overview");
            ListView jobs = F<ListView>(ov, "jobs");
            ListView progs = F<ListView>(ov, "programs");
            Check(WaitFor(delegate { return jobs.Items.Count == 7; }, 5000), "overview lists the seven starter folder policies (" + jobs.Items.Count + ")");
            Check(progs.Items.Count == 2 && progs.Items[0].SubItems[1].Text == "Off", "overview lists VVD and vMix, both off");
            Check(F<Label>(ov, "bannerText").Text.StartsWith("Service running in console mode"), "banner says the service runs (" + F<Label>(ov, "bannerText").Text.Replace("\n", " ") + ")");
            Check(jobs.Visible && jobs.Height > 100 && progs.Height > 60, "overview lists have a usable size");
            Shot("01-overview");

            // ---- folder policies
            FoldersPage fp = (FoldersPage)Go("folders");
            ListBox fl = F<ListBox>(F<ListEditor<FolderPolicy>>(fp, "folders"), "list");
            TextBox fName = F<TextBox>(fp, "fName");
            CheckBox fOn = F<CheckBox>(fp, "fOn");
            Check(fl.Items.Count == 7 && fl.SelectedIndex == 0 && fName.Text == c.Folders[0].Name, "folder list shows the policies, first selected");
            Check(F<PathBox>(fp, "fPath").Text == c.Folders[0].Path && F<PathBox>(fp, "fDest").Text == c.Folders[0].Destination, "source and destination shown");
            Check(fl.Width > 200 && fl.Height > 150, "folder list has a usable size (" + fl.Width + "x" + fl.Height + ")");
            Check(!fp.Dirty, "folders page is clean after loading");
            Shot("02-folders");
            RadioButton mMove = F<RadioButton>(fp, "mMove"), mDelete = F<RadioButton>(fp, "mDelete"), mCustom = F<RadioButton>(fp, "mCustom");
            NumericUpDown mAge = F<NumericUpDown>(fp, "mAge");
            Label mSummary = F<Label>(fp, "mSummary");
            Check(mMove.Checked && !mAge.Enabled && F<PathBox>(fp, "fDest").Enabled, "a move policy shows 'Moved to the destination' with no age to set");
            Check(mSummary.Text.StartsWith("Move all files to " + c.Folders[0].Destination), "folder tab summary (" + mSummary.Text + ")");
            fOn.Checked = true;
            fName.Text = "ISO recordings";
            Pump(200);
            Check(fp.Dirty, "editing marks the page dirty");
            Check(fp.Save(), "save folder policy");
            Check(manager.Config.Get().Folders[0].Enabled && manager.Config.Get().Folders[0].Name == "ISO recordings", "saved to the configuration file");
            Check(WaitFor(delegate { EngineStatus s = Status(); return s != null && s.Jobs.Exists(delegate(JobStatus j) { return j.Name == "ISO recordings" && j.Enabled; }); }, 8000),
                  "the service picked up the change");

            // rules tab
            TabControl ftabs = F<TabControl>(fp, "tabs");
            ftabs.SelectedIndex = 1;
            Pump(400);
            ListBox rl = All<ListBox>(ftabs.TabPages[1])[0];
            TextBox rName = F<TextBox>(fp, "rName");
            NumericUpDown rAge = F<NumericUpDown>(fp, "rAge");
            Check(rl.Items.Count == 1 && rName.Text == "Move every file" && rAge.Value == 0 && !F<CheckBox>(fp, "rAgeOn").Checked && !rAge.Enabled, "rules tab shows the folder's rule");
            Check(F<Label>(fp, "rSummary").Text.Contains("to " + c.Folders[0].Destination), "rule uses the policy destination (" + F<Label>(fp, "rSummary").Text + ")");
            Check(rl.Height > 80, "rule list has a usable size (" + rl.Width + "x" + rl.Height + ")");
            Btn(fp, "Add rule").PerformClick();
            Pump(200);
            Check(rl.Items.Count == 2 && rl.SelectedIndex == 1, "Add rule appends and selects a rule");
            rName.Text = "Keep masters";
            Ui.Set(F<ComboBox>(fp, "rAction"), "keep");
            F<TextBox>(fp, "rPattern").Text = "*master*";
            rAge.Value = 0;
            Pump(200);
            Btn(ftabs.TabPages[1], "Move up").PerformClick();
            Pump(200);
            Check(rl.SelectedIndex == 0, "Move up puts the keep rule first");
            Check(F<Label>(fp, "rSummary").Text.StartsWith("Keep *master*"), "rule summary describes the rule (" + F<Label>(fp, "rSummary").Text + ")");
            Shot("03-folder-rules");
            Check(fp.Save(), "save with two rules");
            List<FileRule> saved = manager.Config.Get().Folders[0].Rules;
            Check(saved.Count == 2 && saved[0].Action == "keep" && saved[1].Action == "move", "rule order and actions saved");

            // preview
            ftabs.SelectedIndex = 2;
            Pump(300);
            ListView pv = F<ListView>(fp, "previewList");
            Btn(fp, "Preview what this policy would do now").PerformClick();
            Check(WaitFor(delegate { return pv.Items.Count == 2; }, 8000), "preview lists both files");
            Check(pv.Items.Count == 2 && pv.Items[0].SubItems[1].Text == "moved" && pv.Items[1].SubItems[1].Text == "moved", "preview: both finished recordings would move");
            Check(File.Exists(Path.Combine(c.Folders[0].Path, "old-recording.mp4")), "preview changes nothing");
            Shot("04-folder-preview");

            // run now
            ftabs.SelectedIndex = 0;
            Pump(300);
            ArmDialog("Yes");
            Btn(fp, "Run now").PerformClick();
            Check(WaitFor(delegate { return File.Exists(Path.Combine(c.Folders[0].Destination, "old-recording.mp4")); }, 8000), "Run now moves the recordings to the destination");
            Check(File.Exists(Path.Combine(c.Folders[0].Destination, "new-recording.mp4")) && Directory.GetFiles(c.Folders[0].Path).Length == 0, "cache folder is empty afterwards");
            RunBar fr = F<RunBar>(fp, "run");
            Check(WaitFor(delegate { return F<Label>(fr, "result").Text.StartsWith("Last run"); }, 6000), "run result shown (" + F<Label>(fr, "result").Text + ")");

            // custom rules -> one simple rule
            Check(mCustom.Checked && mSummary.Text.Contains("2 rules"), "two rules show as custom rules (" + mSummary.Text.Replace("\n", " / ") + ")");
            ArmDialog("No");
            mMove.Checked = true;
            Pump(300);
            Check(dialogsHandled == 1 && mCustom.Checked && fl.SelectedIndex == 0 && ((FolderPolicy)fl.Items[0]).Rules.Count == 2 && !fp.Dirty,
                  "switching custom rules to 'move' asks first; No keeps the rules");
            ArmDialog("Yes");
            mMove.Checked = true;
            Pump(300);
            Check(dialogsHandled == 1 && mMove.Checked && fp.Dirty && fp.Save(), "Yes replaces them with one move rule");
            List<FileRule> one = manager.Config.Get().Folders[0].Rules;
            Check(one.Count == 1 && one[0].Action == "move" && one[0].OlderThan == 0, "saved as a single move rule with no age");

            // add a folder
            Btn(fp, "Add folder").PerformClick();
            Pump(300);
            Check(fl.Items.Count == 8 && fName.Text == "New folder", "Add folder adds and selects a new policy");
            Check(mMove.Checked && !mAge.Enabled, "a new folder policy moves files; no age to set");
            Check(!fp.Save(), "a policy without a folder is refused");
            F<PathBox>(fp, "fPath").Text = Path.Combine(work, "Cache", "Extra");
            fName.Text = "Extra";
            Pump(200);
            Check(!fp.Save(), "a move policy without a destination is refused");
            F<PathBox>(fp, "fDest").Text = Path.Combine(work, "Data", "Extra");
            Pump(200);
            Check(fp.Save() && manager.Config.Get().Folders.Count == 8, "new policy saved");
            FolderPolicy extra = manager.Config.Get().Folders[7];
            Check(extra.Rules.Count == 1 && extra.Rules[0].Action == "move" && extra.Rules[0].OlderThan == 0 && extra.Rules[0].OnConflict == "rename",
                  "saved as move, every file, keep both on name clashes");
            mDelete.Checked = true;
            Pump(200);
            Check(mAge.Enabled && mAge.Value == 7 && !F<PathBox>(fp, "fDest").Enabled && mSummary.Text.StartsWith("Delete all files older than 7 days"),
                  "retention: age enabled, destination not used (" + mSummary.Text + ")");
            mAge.Value = 14;
            Pump(200);
            Shot("02b-folder-retention");
            Check(fp.Save(), "retention policy saved");
            extra = manager.Config.Get().Folders[7];
            Check(extra.Rules.Count == 1 && extra.Rules[0].Action == "delete" && extra.Rules[0].OlderThan == 14 && extra.Rules[0].OlderThanUnit == "days",
                  "saved as delete after 14 days");
            ArmDialog("Yes");
            Btn(F<Panel>(F<ListEditor<FolderPolicy>>(fp, "folders"), "left"), "Remove").PerformClick();
            Pump(300);
            Check(dialogsHandled == 1 && fl.Items.Count == 7 && fp.Save(), "Remove asks, then removes");

            // ---- programs
            ProgramsPage pp = (ProgramsPage)Go("programs");
            ListBox pl = F<ListBox>(F<ListEditor<ProgramJob>>(pp, "list"), "list");
            Check(pl.Items.Count == 2, "programs page lists VVD and vMix");
            pl.SelectedIndex = 1;
            Pump(200);
            Check(F<TextBox>(pp, "pName").Text == "vMix" && F<TextBox>(pp, "pProc").Text == "vMix64", "vMix fields shown");
            F<CheckBox>(pp, "pOn").Checked = true;
            F<NumericUpDown>(pp, "pStartDelay").Value = 0;
            Pump(200);
            Check(pp.Save(), "save vMix switched on");
            Check(WaitFor(delegate { return procs.Starts.Count == 1; }, 10000), "the service starts vMix in the user session");
            Check(procs.Starts.Count == 1 && procs.Starts[0].Priority == "high", "started with high priority");
            Check(WaitFor(delegate { ProgramStatus s = manager.Program(manager.Config.Get().Programs[1].Id); return s != null && s.State == "running"; }, 8000), "status says running");
            Pump(1200);
            Check(F<Label>(pp, "live").Text.StartsWith("Running, PID"), "programs page shows live status (" + F<Label>(pp, "live").Text + ")");
            Shot("05-programs");
            ArmDialog("vMix");
            Btn(pp, "Add program").PerformClick();
            Pump(300);
            Check(dialogsHandled == 1 && pl.Items.Count == 3 && F<TextBox>(pp, "pName").Text == "vMix", "Add program offers templates");
            pp.Discard();
            Pump(200);

            // watchdog: a Windows service, and a health check
            ArmDialog("A Windows service");
            Btn(pp, "Add program").PerformClick();
            Pump(300);
            Check(dialogsHandled == 1 && F<RadioButton>(pp, "kService").Checked && !F<PathBox>(pp, "pExe").Enabled && F<TextBox>(pp, "pSvc").Enabled,
                  "Add > A Windows service: service fields instead of program fields");
            F<TextBox>(pp, "pSvc").Text = "Spooler";
            F<TextBox>(pp, "pName").Text = "Print spooler";
            F<CheckBox>(pp, "pOn").Checked = true;
            Pump(200);
            Check(pp.Save(), "service watch saved");
            Check(WaitFor(delegate { return services.Calls.Contains("start Spooler"); }, 10000), "the watchdog starts the stopped Windows service");
            Check(WaitFor(delegate { ProgramStatus s = manager.Program(manager.Config.Get().Programs[2].Id); return s != null && s.State == "running" && s.Kind == "service"; }, 8000),
                  "service shows as running");
            Pump(1200);
            Shot("05b-watchdog-service");
            pl.SelectedIndex = 1;
            Pump(200);
            F<CheckBox>(pp, "hOn").Checked = true;
            F<TextBox>(pp, "hUrl").Text = "http://127.0.0.1:" + vmx.Port + "/api";
            Pump(200);
            Btn(pp, "Test now").PerformClick();
            Check(WaitFor(delegate { return F<Label>(pp, "hResult").Text.StartsWith("Answers"); }, 8000), "health check test from the service (" + F<Label>(pp, "hResult").Text + ")");
            Shot("05c-watchdog-health");
            Check(pp.Save(), "health check saved");

            // ---- scripts
            ScriptsPage sp = (ScriptsPage)Go("scripts");
            string script = Path.Combine(work, "bin", "hello.sh");
            File.WriteAllText(script, "#!/bin/sh\necho \"hello from the script $1\"\n");
            Util.RunCapture("chmod", new[] { "+x", script }, 5000);
            Btn(sp, "Add script").PerformClick();
            Pump(200);
            F<PathBox>(sp, "sCmd").Text = script;
            F<TextBox>(sp, "sArgs").Text = "world";
            F<CheckBox>(sp, "sOn").Checked = true;
            Pump(200);
            Check(sp.Save(), "save a script");
            Check(WaitFor(delegate { return manager.Job("script:" + manager.Config.Get().Scripts[0].Id) != null; }, 8000), "the service lists the script");
            Pump(1200);
            ArmDialog("Yes");
            Btn(sp, "Run now").PerformClick();
            Check(WaitFor(delegate { JobStatus j = manager.Job("script:" + manager.Config.Get().Scripts[0].Id); return j != null && j.LastResult == "ok"; }, 8000), "script ran in the service");
            ArmDialog("Close");
            Btn(sp, "Show output of the last run").PerformClick();
            Check(WaitFor(delegate { return dialogsHandled == 1; }, 5000) && dialogText.Contains("hello from the script world"), "output window shows the script's output");
            Shot("06-scripts");

            // ---- vertical
            VerticalPage vp = (VerticalPage)Go("vertical");
            TabControl vt = All<TabControl>(vp)[0];
            vt.SelectedIndex = 4;
            Pump(1500);
            Check(WaitFor(delegate { return Find<Label>(vp, delegate(Label l) { return l.Text.Contains("UHD"); }) != null; }, 6000), "GPU detection comes from the service");
            Label testResult = F<Label>(vp, "testResult");
            Btn(vp, "Test these settings").PerformClick();
            Check(WaitFor(delegate { return testResult.Text.StartsWith("Works") || testResult.Text.StartsWith("Failed") || testResult.Text.StartsWith("The test"); }, 40000) &&
                  testResult.Text.StartsWith("Works"), "encoder test runs in the service (" + testResult.Text.Replace("\n", " ") + ")");
            Shot("07-vertical-gpu");
            WriteMedia(Path.Combine(c.Vertical.WatchDir, "MultiCorder7 - Cam1 - 23 July 2026 - 08-29-03 AM.mp4"), 120);
            vt.SelectedIndex = 0;
            Pump(300);
            Btn(vp, "Start watching").PerformClick();
            Check(WaitFor(delegate { EngineStatus s = Status(); return s != null && s.Vertical.History.Count >= 1; }, 30000), "the service converts a file after Start watching");
            Pump(1500);
            ListView hist = F<ListView>(vp, "historyList");
            Check(hist.Items.Count >= 1 && hist.Items[0].SubItems[2].Text == "Done", "history shows the conversion");
            Shot("08-vertical-queue");
            Check(WaitFor(delegate { return File.Exists(Path.Combine(c.Vertical.MoveOriginalTo, "MultiCorder7 - Cam1 - 23 July 2026 - 08-29-03 AM.mp4")); }, 8000)
                  && Directory.GetFiles(c.Vertical.WatchDir).Length == 0, "the original moves off the ingest folder after the conversion");
            vt.SelectedIndex = 1;
            Pump(500);
            Check(F<RadioButton>(vp, "afterMove").Checked && F<PathBox>(vp, "moveTo").Text == c.Vertical.MoveOriginalTo && F<PathBox>(vp, "moveTo").Enabled,
                  "files tab shows 'move it to' and the folder");
            Shot("08b-vertical-files");
            vt.SelectedIndex = 0;
            Pump(300);
            Btn(vp, "Stop after current file").PerformClick();
            Pump(500);

            // ---- alerts
            AlertsPage ap = (AlertsPage)Go("alerts");
            TabControl at = F<TabControl>(ap, "tabs");
            ListEditor<AlertDestination> ad = F<ListEditor<AlertDestination>>(ap, "dests");
            ListEditor<AlertRule> ar = F<ListEditor<AlertRule>>(ap, "rules");
            Check(ad.Items.Count == 1 && ad.Items[0].Type == "eventlog" && ar.Items.Count == 1 && ar.Items[0].AllParts, "new configuration: everything to the Event Log");
            Shot("09-alerts-rules");
            at.SelectedIndex = 1;
            Pump(400);
            Btn(ap, "Add destination").PerformClick();
            Pump(200);
            Ui.Set(F<ComboBox>(ap, "dType"), "webhook");
            Pump(200);
            Check(F<TextBox>(ap, "dName").Text == "Webhook" && F<TextBox>(ap, "dUrl").Visible && !F<TextBox>(ap, "dHost").Visible,
                  "choosing the type shows only its fields");
            F<TextBox>(ap, "dUrl").Text = "not a url";
            Pump(200);
            Check(!ap.Save(), "invalid webhook address is refused");
            F<TextBox>(ap, "dUrl").Text = "http://127.0.0.1:" + vmx.Port + "/hook";
            Ui.Set(F<ComboBox>(ap, "dProxy"), "none");
            Pump(200);
            Check(ap.Save() && manager.Config.Get().Alerts.Destinations.Count == 2, "webhook destination saved");
            AlertDestination hook = ad.Items[1];
            Check(F<Label>(ap, "dUsedBy").Text.StartsWith("No rule sends here"), "an unused destination says so");
            vmx.Fail = true;
            ArmDialog("");
            Btn(ap, "Send a test").PerformClick();
            Check(WaitFor(delegate { return F<Label>(ap, "testResult").Text.StartsWith("Webhook failed"); }, 10000) && F<Label>(ap, "testResult").Text.Contains("HTTP 500"),
                  "failed webhook test names the HTTP answer (" + F<Label>(ap, "testResult").Text + ")");
            Check(WaitFor(delegate { return dialogsHandled == 1; }, 4000) && dialogText.Contains("via no proxy"), "and opens the step-by-step trace");
            vmx.Fail = false;
            Btn(ap, "Send a test").PerformClick();
            Check(WaitFor(delegate { return F<Label>(ap, "testResult").Text.StartsWith("Webhook: Done"); }, 10000), "webhook test goes through the service (" + F<Label>(ap, "testResult").Text + ")");

            using (FakeJira jira = new FakeJira())
            {
                Btn(ap, "Add destination").PerformClick();
                Pump(200);
                Ui.Set(F<ComboBox>(ap, "dType"), "jira");
                Pump(200);
                F<TextBox>(ap, "dJiraUrl").Text = jira.Url;
                F<TextBox>(ap, "dJiraUser").Text = "ops@example.com";
                F<TextBox>(ap, "dJiraToken").Text = "tok";
                F<TextBox>(ap, "dProject").Text = "ops";
                Ui.Set(F<ComboBox>(ap, "dProxy"), "none");
                Pump(200);
                Check(F<Button>(ap, "tCheck").Visible && F<Button>(ap, "tSend").Text == "Create a test issue", "Jira destination offers a login check");
                Btn(ap, "Check login and project").PerformClick();
                Check(WaitFor(delegate { return F<Label>(ap, "testResult").Text.Contains("Logged in as Ops Bot"); }, 10000), "Jira login check (" + F<Label>(ap, "testResult").Text + ")");
                Btn(ap, "Create a test issue").PerformClick();
                Check(WaitFor(delegate { return F<Label>(ap, "testResult").Text.Contains("created OPS-1"); }, 10000) && jira.Issues.Count == 1,
                      "Jira test issue created (" + F<Label>(ap, "testResult").Text + ")");
                Shot("09b-alerts-jira");
                Check(ap.Save() && manager.Config.Get().Alerts.Destinations[2].JiraToken.Length > 3 && manager.Config.Get().Alerts.Destinations[2].JiraToken != "tok",
                      "Jira token saved encrypted");
            }

            at.SelectedIndex = 0;
            Pump(400);
            Btn(ap, "Add rule").PerformClick();
            Pump(200);
            F<TextBox>(ap, "rName").Text = "Watchdog to the team";
            CheckedListBox parts = F<CheckedListBox>(ap, "rParts");
            Check(F<RadioButton>(ap, "rSome").Checked && parts.Enabled, "a new rule picks parts");
            parts.SetItemChecked(Array.IndexOf(AlertSettings.Parts, "programs"), true);
            Pump(200);
            CheckedListBox items = F<CheckedListBox>(ap, "rItems");
            Check(items.Items.Count == manager.Config.Get().Programs.Count && items.Items.Count > 0 && items.Items[0].ToString().StartsWith("Watchdog: "),
                  "entries of the ticked part are listed (" + items.Items.Count + ")");
            Check(!ap.Save(), "a rule without a destination is refused");
            CheckedListBox to = F<CheckedListBox>(ap, "rDests");
            Check(to.Items.Count == 3, "all destinations offered");
            to.SetItemChecked(1, true);
            Ui.Set(F<ComboBox>(ap, "rSeverity"), "warning");
            Pump(200);
            Check(F<Label>(ap, "rSummary").Text.StartsWith("Watchdog, warnings and errors and recoveries > Webhook"), "rule summary (" + F<Label>(ap, "rSummary").Text + ")");
            Check(ap.Save(), "rule saved");
            AlertRule savedRule = manager.Config.Get().Alerts.Rules[1];
            Check(savedRule.Name == "Watchdog to the team" && !savedRule.AllParts && savedRule.Parts.Count == 1 && savedRule.Parts[0] == "programs" &&
                  savedRule.Destinations.Count == 1 && savedRule.Destinations[0] == hook.Id && savedRule.MinSeverity == "warning", "rule stored with its parts and destination");
            Shot("09c-alerts-rule");
            at.SelectedIndex = 1;
            Pump(400);
            ad.Select(ad.Items[1]);
            Pump(200);
            Check(F<Label>(ap, "dUsedBy").Text == "Used by: Watchdog to the team", "destination lists the rules using it (" + F<Label>(ap, "dUsedBy").Text + ")");
            ArmDialog("Yes");
            Btn(ad, "Remove").PerformClick();
            Check(WaitFor(delegate { return dialogsHandled == 1; }, 4000), "removal asks first");
            Pump(200);
            Check(ar.Items[1].Destinations.Count == 0, "removing a destination takes it out of the rules");
            ap.Discard();
            Pump(200);
            at.SelectedIndex = 2;
            Pump(400);
            Check(WaitFor(delegate { return F<ListView>(ap, "recent").Items.Count > 0; }, 4000), "recent alerts listed");
            Shot("09d-alerts-recent");
            at.SelectedIndex = 0;
            Pump(200);

            // ---- GPIO
            GpioPage gp = (GpioPage)Go("gpio");
            TabControl gt = F<TabControl>(gp, "tabs");
            gt.SelectedIndex = 1;
            Pump(400);
            Btn(gp, "Add device").PerformClick();
            Pump(200);
            F<TextBox>(gp, "dName").Text = "Studio GPIO node";
            F<TextBox>(gp, "dHost").Text = "127.0.0.1";
            F<NumericUpDown>(gp, "dPort").Value = axia.Port;
            Pump(200);
            ArmDialog("");
            Btn(gp, "Test connection").PerformClick();
            Check(WaitFor(delegate { return dialogsHandled == 1; }, 10000) && dialogText.Contains("S: VER LWRP") && F<Label>(gp, "dState").Text == "The device answers.",
                  "Test connection lists what the Axia device says (" + F<Label>(gp, "dState").Text + ")");
            Shot("13-gpio-devices");
            gt.SelectedIndex = 2;
            Pump(400);
            F<NumericUpDown>(gp, "vPort").Value = vmx.Port;
            Pump(200);
            Btn(gp, "Test and load inputs").PerformClick();
            Check(WaitFor(delegate { return F<Label>(gp, "vResult").Text.Contains("3 inputs"); }, 8000), "vMix test loads the inputs (" + F<Label>(gp, "vResult").Text + ")");
            Shot("14-gpio-vmix");
            gt.SelectedIndex = 0;
            Pump(400);
            Btn(gp, "Add trigger").PerformClick();
            Pump(300);
            F<TextBox>(gp, "tName").Text = "Mic 1 live";
            F<NumericUpDown>(gp, "tPort").Value = 3;
            F<NumericUpDown>(gp, "tPin").Value = 2;
            ActionEditor onEd = F<ActionEditor>(gp, "onEd"), offEd = F<ActionEditor>(gp, "offEd");
            TabControl ttabs = F<TabControl>(gp, "ttabs");
            ttabs.SelectedIndex = 1;
            Pump(300);
            Check(F<ComboBox>(onEd, "aInput").Items.Count == 3, "the action's Input list offers the vMix inputs (" + F<ComboBox>(onEd, "aInput").Items.Count + ")");
            F<ComboBox>(onEd, "aFunc").Text = "Cut";
            F<ComboBox>(onEd, "aInput").Text = "Camera 1";
            Pump(200);
            Check(F<Label>(onEd, "aHint").Text.StartsWith("Cut to Input"), "function hint shown");
            Shot("15-gpio-trigger-on");
            ttabs.SelectedIndex = 2;
            Pump(200);
            Btn(offEd, "Add action").PerformClick();
            Pump(200);
            F<ComboBox>(offEd, "aFunc").Text = "OverlayInput1Out";
            Pump(200);
            F<CheckBox>(gp, "gOn").Checked = true;
            Pump(200);
            Check(gp.Dirty && gp.Save(), "GPIO settings saved");
            GpioCfg savedG = manager.Config.Get().Gpio;
            Check(savedG.Enabled && savedG.Triggers.Count == 1 && savedG.Triggers[0].OnActive[0].Input == "Camera 1" && savedG.Triggers[0].OnInactive[0].Function == "OverlayInput1Out"
                  && savedG.Triggers[0].DeviceId == savedG.Devices[0].Id, "trigger saved with its device and both action lists");
            Check(WaitFor(delegate { EngineStatus s = Status(); return s != null && s.Gpio.Devices.Exists(delegate(GpioDeviceStatus d) { return d.State == "connected"; }); }, 10000),
                  "the service connects to the Axia device");
            Pump(2500);
            axia.Push("GPI 3 hLhhh");
            Check(WaitFor(delegate { lock (vmx.Calls) return vmx.Calls.Contains("/api/?Function=Cut&Input=Camera%201"); }, 8000), "pin 2 of GPI 3 closes: vMix cuts to Camera 1");
            axia.Push("GPI 3 hHhhh");
            Check(WaitFor(delegate { lock (vmx.Calls) return vmx.Calls.Contains("/api/?Function=OverlayInput1Out"); }, 8000), "pin opens: overlay off");
            ttabs.SelectedIndex = 0;
            Pump(300);
            Btn(gp, "Learn from the device").PerformClick();
            Pump(300);
            axia.Push("GPI 5 hhLhh");
            Check(WaitFor(delegate { return F<Label>(gp, "learnText").Text.StartsWith("Learned"); }, 8000) && F<NumericUpDown>(gp, "tPort").Value == 5 && F<NumericUpDown>(gp, "tPin").Value == 3,
                  "Learn fills port and pin from the device (" + F<Label>(gp, "learnText").Text + ")");
            Shot("16-gpio-learn");
            gp.Discard();
            Pump(300);
            ttabs.SelectedIndex = 1;
            Pump(200);
            Btn(onEd, "Run these now").PerformClick();
            Check(WaitFor(delegate { return F<Label>(onEd, "runResult").Text.StartsWith("OK:"); }, 8000), "Run these now calls vMix (" + F<Label>(onEd, "runResult").Text + ")");
            gt.SelectedIndex = 3;
            Pump(1500);
            ListView pinsLv = F<ListView>(gp, "pins"), eventsLv = F<ListView>(gp, "events");
            Check(pinsLv.Items.Count >= 16 && eventsLv.Items.Count >= 2 && eventsLv.Items[eventsLv.Items.Count - 1].SubItems[2].Text == "Mic 1 live",
                  "live view lists the ports and the changes (" + pinsLv.Items.Count + " ports, " + eventsLv.Items.Count + " changes)");
            Btn(gp, "Simulate on").PerformClick();
            Check(WaitFor(delegate { return F<Label>(gp, "sResult").Text.Length > 0; }, 5000), "simulate answers (" + F<Label>(gp, "sResult").Text + ")");
            Shot("17-gpio-live");

            // ---- service & settings
            ServicePage svc = (ServicePage)Go("service");
            Pump(800);
            Check(F<Label>(svc, "svcState").Text.StartsWith("Running"), "service page shows the running engine");
            Check(WaitFor(delegate { return F<Label>(svc, "cfgState").Text.StartsWith("In use by the service"); }, 8000), "service page confirms the file in use (" + F<Label>(svc, "cfgState").Text + ")");
            Shot("10-service");
            Check(manager.Config.Versions().Count > 0, "earlier versions were kept by the saves so far (" + manager.Config.Versions().Count + ")");
            ArmDialog("Close");
            Btn(svc, "Earlier versions...").PerformClick();
            Check(WaitFor(delegate { return dialogsHandled == 1; }, 5000) && dialogText.Contains("the settings as they were are kept here"), "Earlier versions... opens the list");
            F<CheckBox>(svc, "mGpio").Checked = false;
            Pump(200);
            Check(svc.Save(), "switch the GPIO part off");
            Pump(800);
            ListBox navList = F<ListBox>(form, "nav");
            bool hasGpio = false;
            foreach (object it in navList.Items) if (((NavItem)it).Key == "gpio") hasGpio = true;
            Check(!hasGpio && navList.Items.Count == 8, "its page leaves the navigation (" + navList.Items.Count + " pages)");
            Check(WaitFor(delegate { EngineStatus s = Status(); return s != null && !s.Gpio.Enabled; }, 8000), "and the service stops listening");
            F<CheckBox>(svc, "mGpio").Checked = true;
            Pump(200);
            Check(svc.Save(), "switch it on again");
            Pump(800);
            Check(navList.Items.Count == 9, "page back (" + navList.Items.Count + ")");

            // ---- studios and Pathfinder
            FakePathfinder pf = new FakePathfinder();
            F<CheckBox>(svc, "mStudios").Checked = true;
            Pump(200);
            Check(svc.Save(), "switch the Studios part on");
            Pump(800);
            Check(navList.Items.Count == 10, "the Studios page appears (" + navList.Items.Count + ")");
            StudiosPage stp = (StudiosPage)Go("studios");
            Btn(stp, "Add studio").PerformClick();
            Pump(200);
            F<NumericUpDown>(stp, "sPin").Value = 4;
            Btn(stp, "Add studio").PerformClick();
            Pump(200);
            F<NumericUpDown>(stp, "sPin").Value = 5;
            TabControl stabs = F<TabControl>(stp, "tabs");
            stabs.SelectedIndex = 1;
            Pump(300);
            F<CheckBox>(stp, "pOn").Checked = true;
            F<TextBox>(stp, "pHost").Text = "127.0.0.1";
            F<NumericUpDown>(stp, "pPort").Value = pf.Port;
            F<TextBox>(stp, "pUser").Text = pf.User;
            F<TextBox>(stp, "pPassword").Text = pf.Password;
            F<TextBox>(stp, "pBeat").Text = "SRV1_Heartbeat";
            F<TextBox>(stp, "pStatus").Text = "SRV1_Status";
            F<TextBox>(stp, "pStudio").Text = "SRV1_Studio";
            F<TextBox>(stp, "pCommand").Text = "SRV1_Command";
            Pump(200);
            Check(stp.Dirty && stp.Save(), "studios and the Pathfinder link saved");
            StudiosCfg savedS = manager.Config.Get().Studios;
            Check(savedS.Studios.Count == 2 && savedS.Studios[0].Code == "S1" && savedS.Studios[1].Code == "S2" && savedS.Pathfinder.Enabled &&
                  savedS.GpioStatus.StudioPins.Count == 2 && Secrets.IsEncrypted(savedS.Pathfinder.Password) == Util.IsWindows,
                  "saved: two studios, their GPIO pins and the link");
            pf.SetSlot("SRV1_Command", "");
            ArmDialog("");
            Btn(stp, "Test connection").PerformClick();
            Check(WaitFor(delegate { return dialogsHandled == 1; }, 15000) && dialogText.Contains("Logged in."),
                  "Test connection logs in to Pathfinder (" + F<Label>(stp, "pResult").Text + ")");
            Check(WaitFor(delegate { return pf.Slot("SRV1_Status") != null && pf.Slot("SRV1_Studio") == "none"; }, 15000),
                  "the service reports to Pathfinder (studio " + pf.Slot("SRV1_Studio") + ", status " + pf.Slot("SRV1_Status") + ")");
            // tag the starter programs: VVD in studio 1, vMix in studio 2
            ProgramsPage pp2 = (ProgramsPage)Go("programs");
            ListEditor<ProgramJob> plist = F<ListEditor<ProgramJob>>(pp2, "list");
            CheckedListBox pst = F<CheckedListBox>(pp2, "pStudios");
            Check(pst.Visible && pst.Items.Count == 2, "the Watchdog page offers the studios (" + pst.Items.Count + ")");
            plist.Select(plist.Items[0]);
            Pump(200);
            pst.SetItemChecked(0, true);
            Pump(300);
            plist.Select(plist.Items[1]);
            Pump(200);
            pst.SetItemChecked(1, true);
            Pump(300);
            Check(pp2.Dirty && pp2.Save(), "studios ticked on the programs saved");
            List<ProgramJob> savedP = manager.Config.Get().Programs;
            Check(savedP[0].Studios.Count == 1 && savedP[0].Studios[0] == savedS.Studios[0].Id && savedP[1].Studios[0] == savedS.Studios[1].Id, "each program belongs to its studio");
            // Pathfinder decides
            pf.SetSlot("SRV1_Command", "S2");
            Check(WaitFor(delegate { EngineStatus s = Status(); return s != null && s.Studios.ActiveCode == "S2" && s.Studios.State != "switching"; }, 15000),
                  "Pathfinder's command slot switches the server to studio 2");
            Check(WaitFor(delegate { return pf.Slot("SRV1_Studio") == "S2"; }, 10000), "and the new studio is reported back");
            stp = (StudiosPage)Go("studios");
            Pump(1500);
            Check(F<Label>(stp, "nowState").Text.StartsWith("Studio 2 (S2)") && F<Label>(stp, "nowLink").Text.Contains("Studio to run: S2"),
                  "the Studios page shows the active studio and the link (" + F<Label>(stp, "nowState").Text + " / " + F<Label>(stp, "nowLink").Text + ")");
            Shot("18-studios");
            stabs.SelectedIndex = 1;
            Pump(400);
            Shot("19-studios-pathfinder");
            stabs.SelectedIndex = 0;
            pf.SetSlot("SRV1_Command", "");
            Pump(2500);
            ComboBox swTo = F<ComboBox>(stp, "switchTo");
            Ui.Set(swTo, savedS.Studios[0].Id);
            ArmDialog("Yes");
            Btn(stp, "Switch now").PerformClick();
            Check(WaitFor(delegate { return dialogsHandled == 1; }, 4000), "a manual switch asks first");
            Check(WaitFor(delegate { EngineStatus s = Status(); return s != null && s.Studios.ActiveCode == "S1"; }, 45000), "Switch now switches to studio 1 (" + F<Label>(stp, "switchResult").Text + " / active " + (Status() != null ? Status().Studios.ActiveCode : "?") + " / chosen " + Ui.Get(swTo) + ")");
            pf.Dispose();

            // ---- external edit of the file
            AppConfig edited = manager.Config.Get();
            edited.Folders[1].Name = "PGM1 edited in Notepad";
            Thread.Sleep(50);
            File.WriteAllText(manager.Config.Path, Cfg.ToText(edited) + " ");
            File.SetLastWriteTimeUtc(manager.Config.Path, DateTime.UtcNow.AddSeconds(2));
            fp = (FoldersPage)Go("folders");
            Check(WaitFor(delegate { return fl.Items.Count == 7 && ((FolderPolicy)fl.Items[1]).Name == "PGM1 edited in Notepad"; }, 8000), "a hand edit of the file shows up in the window");
            Check(WaitFor(delegate { EngineStatus s = Status(); return s != null && s.Jobs.Exists(delegate(JobStatus j) { return j.Name == "PGM1 edited in Notepad"; }); }, 8000),
                  "and is applied by the service");

            // ---- log
            Go("log");
            Pump(1200);
            LogPage lp = (LogPage)form.PageFor("log");
            ListView log = All<ListView>(lp)[0];
            int total = log.Items.Count;
            Check(total > 10, "log page shows the service's log (" + total + " lines)");
            Shot("11-log");
            engine.Hub.Logger("gpio").Warn("Axia node 10.0.0.9 unreachable (log filter check)");
            engine.Hub.Logger("programs").Error("vMix cannot start (log filter check)");
            Pump(2500);
            ComboBox lvl = F<ComboBox>(lp, "level");
            ComboBox src = F<ComboBox>(lp, "source");
            Ui.Set(lvl, "ERROR");
            Pump(300);
            bool allErrors = log.Items.Count > 0;
            foreach (ListViewItem it in log.Items) if (it.SubItems[1].Text != "ERROR") allErrors = false;
            Check(allErrors && log.Items.Count < total, "level filter: errors only (" + log.Items.Count + " of " + total + ")");
            Ui.Set(lvl, "INFO");
            Ui.Set(src, "gpio");
            Pump(300);
            bool allGpio = log.Items.Count >= 1;
            foreach (ListViewItem it in log.Items) if (it.SubItems[2].Text != "gpio") allGpio = false;
            Check(allGpio, "source filter: only gpio lines (" + log.Items.Count + ")");
            Ui.Set(src, "");
            F<TextBox>(lp, "search").Text = "cannot start (log filter";
            Pump(900);
            Check(log.Items.Count == 1 && log.Items[0].SubItems[3].Text.Contains("vMix cannot start"), "text search finds the line (" + log.Items.Count + ")");
            F<TextBox>(lp, "search").Text = "";
            Pump(900);
            Check(log.Items.Count >= total, "clearing the filters shows everything again (" + log.Items.Count + ")");

            // ---- service goes away
            engine.Shutdown();
            Check(WaitFor(delegate { return !manager.Online; }, 6000), "manager notices the service stopped");
            Go("overview");
            Pump(1500);
            Check(F<Label>(ov, "bannerText").Text.StartsWith("The Watchkeeper service is not running"), "overview warns that nothing runs");
            Shot("12-overview-stopped");

            axia.Dispose();
            vmx.Dispose();
            Check(uiErrors.Count == 0, "no unhandled UI exceptions" + (uiErrors.Count > 0 ? ": " + uiErrors[0] : ""));
            Console.WriteLine(failures == 0 ? "ALL UI CHECKS PASSED" : failures + " UI CHECK(S) FAILED");
            form.Close();
            manager.Dispose();
            return failures == 0 ? 0 : 1;
        }
    }
}
