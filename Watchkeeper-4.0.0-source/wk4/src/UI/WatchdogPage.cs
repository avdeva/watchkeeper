// Watchdog page: the programs and Windows services that must keep running.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Watchkeeper.UI
{
    public class WatchdogPage : Page
    {
        readonly ListEditor<ProgramEntry> list = new ListEditor<ProgramEntry>("Add a program or a Windows service that must keep running.");
        ProgramEntry cur = new ProgramEntry();
        readonly Label live = new Label();
        readonly List<FieldRow> programRows = new List<FieldRow>(), serviceRows = new List<FieldRow>();
        readonly CheckBox[] days = new CheckBox[7];
        public override string Title { get { return "Watchdog"; } }

        static readonly string[][] Kinds = { new[] { "program", "A program" }, new[] { "service", "A Windows service" } };
        static readonly string[][] RunIn = { new[] { "desktop", "On the signed-in user's desktop (programs with a window)" }, new[] { "background", "Hidden, in the background (no window)" } };
        static readonly string[][] Means = {
            new[] { "auto", "Automatic" },
            new[] { "app", "The application to watch is running" },
            new[] { "started", "What Watchkeeper started (its PID) is running" },
            new[] { "either", "Either of them is running" },
            new[] { "both", "Both are running" } };
        static readonly string[][] Priorities = { new[] { "normal", "Normal" }, new[] { "above_normal", "Above normal" }, new[] { "high", "High" } };
        static readonly string[][] HealthActions = { new[] { "alert", "Alert only" }, new[] { "restart", "Alert and restart it" } };

        public WatchdogPage(Manager m) : base(m)
        {
            list.Title = delegate(ProgramEntry p) { return p.Name; };
            list.Subtitle = delegate(ProgramEntry p) { return Line(p); };
            list.Mark = delegate(ProgramEntry p) { ProgramStatus s = StatusOf(p); return !p.Enabled ? Ui.Off : s == null ? Ui.Warn : Ui.StateColor(s.Health == "failing" ? "error" : s.State); };
            list.Create = delegate { return new ProgramEntry { Id = Util.NewId(), Name = "New program" }; };
            list.Copy = delegate(ProgramEntry p) { ProgramEntry c = new ProgramEntry(); Json.FillFrom(c, Json.ToJson(p)); c.Id = Util.NewId(); c.Name = p.Name + " (copy)"; c.Enabled = false; return c; };
            list.Selected += delegate(ProgramEntry p) { cur = p ?? new ProgramEntry(); B.Load(); Toggle(); ShowLive(); };
            list.ListChanged += delegate { M.MarkChanged(); };
            Controls.Add(list);
            Heading("Watchdog", "Programs and Windows services that must always run. Watchkeeper starts them (desktop programs in the signed-in user's session), " +
                                "starts them again when they stop, can restart them every day, and can check that they keep answering.");

            FormGrid g = new FormGrid();
            int w = Ui.BaseFont().Height * 30;
            g.Row("", B.Check("Keep this running", delegate { return cur.Enabled; }, delegate(bool v) { cur.Enabled = v; }));
            g.Row("Name", B.Text(w / 2, delegate { return cur.Name; }, delegate(string v) { cur.Name = v; }));
            g.Row("What", B.Choice(Kinds, delegate { return cur.Kind; }, delegate(string v) { cur.Kind = v; Toggle(); }));

            TextBox svc = B.Text(w / 2, delegate { return cur.ServiceName; }, delegate(string v) { cur.ServiceName = v.Trim(); });
            serviceRows.Add(g.Row("Service name", Ui.Flow(svc, Ui.Button("Choose...", delegate { string n = ServicePicker.Pick(FindForm()); if (n != null) svc.Text = n; })),
                                  "The service name (not the display name), as in services.msc > the service > Properties."));

            TextBox path = B.Text(w, delegate { return cur.Path; }, delegate(string v) { cur.Path = v; });
            programRows.Add(g.Row("Program", Ui.Flow(path, Browse(path, "Programs (*.exe)|*.exe|All files|*.*")),
                                  "The program file. For a launcher (VVD), the launcher; then also set the application to watch below."));
            programRows.Add(g.Row("Arguments", B.Text(w, delegate { return cur.Arguments; }, delegate(string v) { cur.Arguments = v; }),
                                  "Quote paths with spaces, e.g. \"C:\\ProgramData\\App\\Profile.xml\"."));
            TextBox startIn = B.Text(w, delegate { return cur.StartIn; }, delegate(string v) { cur.StartIn = v; });
            programRows.Add(g.Row("Start in", startIn, "Empty = the program's own folder."));
            programRows.Add(g.Row("Run", B.Choice(RunIn, delegate { return cur.RunIn; }, delegate(string v) { cur.RunIn = v; Toggle(); })));
            programRows.Add(g.Row("", B.Check("Start with the user's full administrator rights (no consent prompt)", delegate { return cur.Elevated; }, delegate(bool v) { cur.Elevated = v; }),
                                  "Only for a program that asks for administrator rights; desktop programs only."));

            g.Section("Launcher and the application to watch");
            TextBox watch = B.Text(w, delegate { return cur.WatchPath; }, delegate(string v) { cur.WatchPath = v; });
            g.Row("Application to watch", Ui.Flow(watch, Browse(watch, "Programs (*.exe)|*.exe")),
                  "Only when the program above is a launcher that starts the real application, or when the Windows service starts one. Its full path; " +
                  "* may stand for part of a folder name that changes with each version: ...\\app-*\\VVD.exe. Empty = watch the program itself.");
            g.Row("", Ui.Button("Choose from running programs...", delegate { PickWatch(watch); }));
            g.Row("Running means", B.Choice(Means, delegate { return cur.RunningMeans; }, delegate(string v) { cur.RunningMeans = v; }),
                  "Automatic: the application to watch (for a Windows service: the service and the application). " +
                  "'What Watchkeeper started' follows the launcher by its process ID: use it when the launcher stays open and the application cannot be found. " +
                  "'Both': when one runs without the other for longer than the wait below, everything is closed and started again - only for a launcher that stays open.");
            g.Row("Wait to appear", Ui.Flow(B.Number(5, 3600, 0, delegate { return cur.LauncherWaitSeconds; }, delegate(double v) { cur.LauncherWaitSeconds = (int)v; }), Ui.Label("seconds")),
                  "How long a start gets before it counts as failed (the application may take a while to open).");

            g.Section("When to start");
            g.Row("First start", Ui.Flow(B.Number(0, 3600, 0, delegate { return cur.StartDelaySeconds; }, delegate(double v) { cur.StartDelaySeconds = (int)v; }), Ui.Label("seconds after Windows starts or the user signs in")));
            g.Row("Start again", Ui.Flow(B.Number(1, 3600, 0, delegate { return cur.RestartDelaySeconds; }, delegate(double v) { cur.RestartDelaySeconds = (int)v; }), Ui.Label("seconds after it stops")));
            g.Row("At most", Ui.Flow(B.Number(0, 1000, 0, delegate { return cur.MaxRestartsPerHour; }, delegate(double v) { cur.MaxRestartsPerHour = (int)v; }), Ui.Label("starts per hour, then pause and alert (0 = no limit)")));
            FlowLayoutPanel dayFlow = new FlowLayoutPanel();
            dayFlow.AutoSize = true;
            dayFlow.WrapContents = false;
            dayFlow.Margin = new Padding(0);
            dayFlow.Controls.Add(B.Text(60, delegate { return cur.RestartAt; }, delegate(string v) { cur.RestartAt = v.Trim(); }));
            dayFlow.Controls.Add(Ui.Label("on"));
            string[] shortNames = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
            for (int i = 0; i < 7; i++)
            {
                int d = i;
                days[i] = B.Check(shortNames[i], delegate { return cur.RestartDays.Contains(SettingsFile.DayNames[d]); }, delegate(bool v)
                {
                    cur.RestartDays.Remove(SettingsFile.DayNames[d]);
                    if (v) cur.RestartDays.Add(SettingsFile.DayNames[d]);
                    cur.RestartDays.Sort(delegate(string a, string b) { return Array.IndexOf(SettingsFile.DayNames, a).CompareTo(Array.IndexOf(SettingsFile.DayNames, b)); });
                });
                dayFlow.Controls.Add(days[i]);
            }
            g.Row("Daily restart", dayFlow, "HH:MM, e.g. 04:00 - it is closed and started again at that time. No day ticked = every day. Empty = never.");
            programRows.Add(g.Row("Priority", B.Choice(Priorities, delegate { return cur.Priority; }, delegate(string v) { cur.Priority = v; })));

            g.Section("Health check (optional)");
            g.Row("Address", B.Text(w, delegate { return cur.HealthUrl; }, delegate(string v) { cur.HealthUrl = v.Trim(); }),
                  "Must keep answering while it runs: http://... (any answer below HTTP 500 counts, e.g. vMix: http://127.0.0.1:8088/api) or tcp://host:port. Empty = no check.");
            g.Row("Login", Ui.Flow(B.Text(140, delegate { return cur.HealthUser; }, delegate(string v) { cur.HealthUser = v; }), Ui.Label("password"),
                                   B.Password(140, delegate { return cur.HealthPassword; }, delegate(string v) { cur.HealthPassword = v; })), "Only if the address needs one.");
            g.Row("Check every", Ui.Flow(B.Number(2, 3600, 0, delegate { return cur.HealthEverySeconds; }, delegate(double v) { cur.HealthEverySeconds = (int)v; }), Ui.Label("seconds; not healthy after"),
                                         B.Number(2, 86400, 0, delegate { return cur.HealthFailSeconds; }, delegate(double v) { cur.HealthFailSeconds = (int)v; }), Ui.Label("seconds without an answer")));
            g.Row("Loading time", Ui.Flow(B.Number(0, 3600, 0, delegate { return cur.HealthGraceSeconds; }, delegate(double v) { cur.HealthGraceSeconds = (int)v; }), Ui.Label("seconds after a start before checking")));
            g.Row("Then", B.Choice(HealthActions, delegate { return cur.HealthAction; }, delegate(string v) { cur.HealthAction = v; }));
            g.Row("Notes", B.Text(w, delegate { return cur.Notes; }, delegate(string v) { cur.Notes = v; }));

            g.Section("Now");
            live.AutoSize = true;
            live.MaximumSize = new Size(w + 200, 0);
            g.Row("State", live);
            g.Row("", Ui.Button("Restart now", delegate { RestartNow(); }), "Closes it (a program with a window is asked to close first) and starts it again. Needs the saved settings.");
            list.Detail.Controls.Add(g);
        }

        static Button Browse(TextBox target, string filter)
        {
            return Ui.Button("Browse...", delegate
            {
                using (OpenFileDialog d = new OpenFileDialog())
                {
                    d.Filter = filter;
                    try { if (target.Text.Length > 0) d.InitialDirectory = Path.GetDirectoryName(Util.NormPath(target.Text)); } catch (Exception) { }
                    if (d.ShowDialog(target.FindForm()) == DialogResult.OK) target.Text = d.FileName;
                }
            });
        }

        void Toggle()
        {
            bool svc = cur.Kind == "service";
            foreach (FieldRow r in programRows) r.Show(!svc);
            foreach (FieldRow r in serviceRows) r.Show(svc);
        }

        protected override void OnFieldChanged() { list.RefreshTitles(); }
        public void RefreshTitles() { list.RefreshTitles(); }

        public override void Reload()
        {
            list.SetItems(M.Edit.Watchdog);
        }

        ProgramStatus StatusOf(ProgramEntry p)
        {
            if (M.Status == null || !M.ServiceAlive) return null;
            return M.Status.Watchdog.Find(delegate(ProgramStatus s) { return s.Id == p.Id; });
        }

        string Line(ProgramEntry p)
        {
            if (!p.Enabled) return "switched off";
            ProgramStatus s = StatusOf(p);
            if (s == null) return M.ServiceAlive ? "not saved yet" : "service not running";
            return s.Text;
        }

        void ShowLive()
        {
            ProgramStatus s = StatusOf(cur);
            if (s == null) { live.Text = M.ServiceAlive ? "Not known yet (save the settings)." : "The service is not running."; live.ForeColor = Ui.Help; return; }
            List<string> lines = new List<string>();
            lines.Add(s.Text);
            if (s.Health.Length > 0) lines.Add("Health check: " + s.HealthText);
            if (s.StartsLastHour > 0) lines.Add("Started " + s.StartsLastHour + " time(s) in the last hour.");
            if (s.LastProblem.Length > 0) lines.Add("Last problem (" + Ago(s.LastProblemAt) + "): " + s.LastProblem);
            if (s.NextRestart > 0) lines.Add("Next daily restart: " + At(s.NextRestart));
            live.Text = string.Join("\n", lines.ToArray());
            live.ForeColor = Ui.StateColor(s.Health == "failing" ? "error" : s.State);
        }

        public override void Live(EngineStatus s)
        {
            list.RefreshTitles();
            ShowLive();
        }

        public override Color Dot(EngineStatus s)
        {
            bool any = false, bad = false, busy = false;
            foreach (ProgramStatus p in s.Watchdog)
            {
                if (!p.Enabled) continue;
                any = true;
                if (p.State == "error" || p.State == "paused" || p.Health == "failing") bad = true;
                else if (p.State != "running") busy = true;
            }
            return !any ? Ui.Off : bad ? Ui.Bad : busy ? Ui.Warn : Ui.Good;
        }

        void RestartNow()
        {
            if (M.Dirty) { Ui.Info(FindForm(), "Save the changes first."); return; }
            if (!M.ServiceAlive) { Ui.Info(FindForm(), "The service is not running."); return; }
            CommandResult r = M.Command("restart_program", cur.Id, 10000);
            Ui.Info(FindForm(), r == null ? "The service did not answer." : cur.Name + ": " + r.Text + ".");
        }

        void PickWatch(TextBox target)
        {
            string picked = ProcessPicker.Pick(FindForm(), Procs.Stem(cur.Path));
            if (picked == null) return;
            if (Util.SamePath(picked, cur.Path) &&
                !Ui.Confirm(FindForm(), "That is the program this entry starts. If it is a launcher, pick the application it starts instead. Use it anyway?")) return;
            string wild = ProcessPicker.WithVersionWildcard(picked);
            if (wild != null && Ui.Confirm(FindForm(), "The folder of\n" + picked + "\nlooks like a version number, so it will change with the next update.\n\nWatch\n" + wild + "\ninstead (* = any version)?"))
                picked = wild;
            target.Text = picked;
        }
    }
}
