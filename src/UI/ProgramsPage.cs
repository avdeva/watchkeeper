// The watchdog: programs (VVD, vMix, ...) and Windows services that must always run. The
// service starts them, starts them again when they stop, can restart them on a schedule and
// can check that they keep answering (health check) and alert or restart when they do not.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Watchkeeper.UI
{
    public class ProgramsPage : SectionPage
    {
        readonly ListEditor<ProgramJob> list = new ListEditor<ProgramJob>("program");
        readonly CheckBox pOn = Ui.Chk("Keep this running");
        readonly TextBox pName = Ui.Text(260);
        readonly RadioButton kProgram = Ui.Radio("A program");
        readonly RadioButton kService = Ui.Radio("A Windows service");
        readonly TextBox pSvc = Ui.Text(260);
        readonly Button pSvcPick;
        readonly PathBox pExe = new PathBox(false, "Programs (*.exe)|*.exe|All files (*.*)|*.*", true);
        readonly TrustNote pTrust = new TrustNote();
        readonly TextBox pArgs = Ui.Text(420);
        readonly PathBox pDir = new PathBox(true, null, true);
        readonly TextBox pProc = Ui.Text(200);
        readonly RadioButton pUser = Ui.Radio("The signed-in user's desktop (for programs with a window)");
        readonly RadioButton pService = Ui.Radio("The service, hidden (background programs only)");
        readonly NumericUpDown pStartDelay = Ui.Num(0, 3600, 5, 0);
        readonly NumericUpDown pRestartDelay = Ui.Num(1, 3600, 1, 0);
        readonly NumericUpDown pMax = Ui.Num(0, 1000, 1, 0);
        readonly ScheduleEditor pRestart = new ScheduleEditor(true);
        readonly ComboBox pPriority;
        readonly CheckBox hOn = Ui.Chk("Check that it keeps answering");
        readonly TextBox hUrl = Ui.Text(360);
        readonly TextBox hUser = Ui.Text(200);
        readonly TextBox hPassword = Ui.Text(200);
        readonly CheckBox hPlain = Ui.Chk("Send the login unencrypted (http to another computer: readable on the network)");
        readonly CheckBox pElevate = Ui.Chk("Allow elevated start: if the program asks for administrator rights, start it with the signed-in user's full rights without the Windows consent prompt");
        const string Unchanged = "••••••••";
        readonly NumericUpDown hEvery = Ui.Num(5, 3600, 5, 0);
        readonly NumericUpDown hFail = Ui.Num(5, 86400, 10, 0);
        readonly NumericUpDown hGrace = Ui.Num(0, 3600, 10, 0);
        readonly RadioButton hAlert = Ui.Radio("Alert");
        readonly RadioButton hRestart = Ui.Radio("Alert and restart it");
        readonly Button hTest;
        readonly Label hResult = new Label();
        readonly TextBox pNotes = Ui.Text(420);
        readonly Label live = new Label();
        readonly Button restartBtn;
        readonly List<Control> programOnly = new List<Control>();
        int fill;

        public ProgramsPage(Manager m)
            : base(m, "programs", "Watchdog",
                   "Programs and Windows services that must always be running, such as vMix or a playout service. The service starts them, starts them again if they stop or crash, " +
                   "can restart them on a schedule, and can check that they keep answering.")
        {
            pPriority = Ui.Combo(new Choice("normal", "Normal"), new Choice("above_normal", "Above normal"), new Choice("high", "High"));
            restartBtn = Ui.Btn("Restart now", delegate { RestartNow(); });
            restartBtn.Tag = "nodirty";
            pSvcPick = Ui.Btn("Choose...", delegate { PickService(); });
            pSvcPick.Tag = "nodirty";
            hTest = Ui.Btn("Test now", delegate { TestHealth(); });
            hTest.Tag = "nodirty";

            list.EmptyText = "Nothing watched yet.\r\n\r\nClick \"Add program\" and pick vMix, VVD, another program or a Windows service.";
            list.RemoveNote = "It keeps running; it is just no longer watched.";
            list.Create = delegate { return ChooseTemplate(); };
            list.Copy = delegate(ProgramJob p)
            {
                ProgramJob c = Json.Clone(p);
                c.Id = "";
                c.Name = p.Name + " (copy)";
                c.Enabled = false;
                return c;
            };
            list.Title = delegate(ProgramJob p) { return p.Name; };
            list.Subtitle = delegate(ProgramJob p)
            {
                if (p.Kind == "service") return p.ServiceName.Length == 0 ? "No service chosen yet" : "Windows service " + p.ServiceName;
                return p.ExePath.Length == 0 ? "No program chosen yet" : p.ExePath;
            };
            list.Badge = delegate(ProgramJob p)
            {
                ProgramStatus s = p.Id.Length > 0 ? M.Program(p.Id) : null;
                if (!p.Enabled) return "";
                if (s == null) return "";
                if (s.Health == "failing") return "not answering";
                switch (s.State)
                {
                    case "running": return "running";
                    case "waiting_session": return "no user";
                    case "paused": return "paused";
                    case "error": return "problem";
                    case "off": return "";
                }
                return "starting";
            };
            list.IsOn = delegate(ProgramJob p) { return p.Enabled; };

            FormGrid g = new FormGrid();
            g.Row(null, pOn);
            g.Row("Name", pName);
            g.Row("What", Ui.Flow(kProgram, kService));
            g.Row("Service name", Ui.Flow(pSvc, pSvcPick), "The service name (not the display name), as in the service's Properties in services.msc.");
            g.Row("Program", pExe);
            g.Row(null, pTrust);
            g.Row("Arguments", pArgs, "Quote paths with spaces, e.g. \"C:\\ProgramData\\Virtual Video Director\\Profiles\\Profile.xml\". Keep files the program loads in a folder only administrators can change.");
            g.Row("Start in", pDir, "Empty = the program's own folder.");
            g.Row("Process name", pProc, "Name in Task Manager's Details tab, without .exe. Empty = the program file's name. Used to tell whether it is running.");
            FlowLayoutPanel where = new FlowLayoutPanel();
            where.FlowDirection = FlowDirection.TopDown;
            where.AutoSize = true;
            where.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            where.WrapContents = false;
            where.Margin = new Padding(0);
            where.Controls.Add(pUser);
            where.Controls.Add(pService);
            g.Row("Run in", where);
            g.Row(null, pElevate);
            g.Note("Only for programs that need it, kept in a folder only administrators can change (Watchkeeper checks this before every start).");
            g.Row("Priority", pPriority);
            programOnly.AddRange(new Control[] { pExe, pArgs, pDir, pProc, pUser, pService, pPriority, pElevate });

            g.Header("Starting");
            Label s1 = Ui.Lbl("seconds after sign-in or service start");
            g.Row("First start", Ui.Flow(pStartDelay, s1));
            Label s2 = Ui.Lbl("seconds after it stops");
            g.Row("Start again", Ui.Flow(pRestartDelay, s2));
            Label s3 = Ui.Lbl("starts per hour, then pause and alert (0 = no limit)");
            g.Row("At most", Ui.Flow(pMax, s3));
            g.Row("Scheduled restart", pRestart, "Programs are ended (not asked to close) and started again; services are stopped and started.");

            g.Header("Health check");
            g.Row(null, hOn);
            g.Row("Address", hUrl, "http://... must answer (any status below 500), e.g. vMix: http://127.0.0.1:8088/api. tcp://host:port must accept a connection.");
            g.Row("User name", hUser, "Only if the address needs a login (HTTP basic). Never put a user name or password in the address.");
            g.Row("Password", hPassword, "Stored encrypted. Leave the dots to keep the saved password.");
            g.Row(null, hPlain, "Without this, a login is only sent over https or to this server itself.");
            Label e1 = Ui.Lbl("seconds");
            g.Row("Check every", Ui.Flow(hEvery, e1));
            Label e2 = Ui.Lbl("seconds without a good answer");
            g.Row("Not healthy after", Ui.Flow(hFail, e2));
            Label e3 = Ui.Lbl("seconds after it starts (loading time)");
            g.Row("First check", Ui.Flow(hGrace, e3));
            g.Row("Then", Ui.Flow(hAlert, hRestart));
            hResult.AutoSize = true;
            hResult.MaximumSize = new Size(520, 0);
            hResult.Margin = new Padding(8, 7, 0, 0);
            hResult.Tag = "nodirty";
            g.Row(null, Ui.Flow(hTest, hResult));

            g.Row("Notes", pNotes);
            live.AutoSize = true;
            live.MaximumSize = new Size(560, 0);
            live.Margin = new Padding(0, 7, 10, 0);
            live.Tag = "nodirty";
            g.Row("Now", Ui.Flow(live, restartBtn));
            list.Detail.Controls.Add(g);

            EventHandler apply = delegate { Apply(); };
            pOn.CheckedChanged += apply;
            pName.TextChanged += apply;
            kProgram.CheckedChanged += apply;
            kService.CheckedChanged += apply;
            pSvc.TextChanged += apply;
            pExe.ValueChanged += apply;
            pExe.Picked += delegate { FromExe(); };
            pArgs.TextChanged += apply;
            pDir.ValueChanged += apply;
            pProc.TextChanged += apply;
            pUser.CheckedChanged += apply;
            pStartDelay.ValueChanged += apply;
            pRestartDelay.ValueChanged += apply;
            pMax.ValueChanged += apply;
            pRestart.ValueChanged += apply;
            pPriority.SelectedIndexChanged += apply;
            hOn.CheckedChanged += apply;
            hUrl.TextChanged += apply;
            hUser.TextChanged += apply;
            hPassword.TextChanged += apply;
            hPlain.CheckedChanged += apply;
            hPassword.UseSystemPasswordChar = true;
            pElevate.CheckedChanged += apply;
            hEvery.ValueChanged += apply;
            hFail.ValueChanged += apply;
            hGrace.ValueChanged += apply;
            hAlert.CheckedChanged += apply;
            hRestart.CheckedChanged += apply;
            pNotes.TextChanged += apply;
            list.SelectedChanged += delegate { Fill(); };
            list.Changed += delegate { MarkDirty(); };

            Body.AutoScroll = false;
            Body.Controls.Add(list);
            Init();
        }

        ProgramJob ChooseTemplate()
        {
            using (Form f = new Form())
            {
                f.Text = "Add to the watchdog";
                f.Font = Ui.BaseFont();
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.MinimizeBox = f.MaximizeBox = false;
                f.ShowInTaskbar = false;
                f.ClientSize = new Size(420, 242);
                string pick = null;
                Label l = new Label();
                l.Text = "What should be kept running?";
                l.SetBounds(16, 16, 380, 24);
                f.Controls.Add(l);
                string[][] choices = { new[] { "vmix", "vMix" }, new[] { "vvd", "Virtual Video Director" }, new[] { "other", "Another program" },
                                       new[] { "service", "A Windows service" } };
                for (int i = 0; i < choices.Length; i++)
                {
                    string key = choices[i][0];
                    Button b = new Button();
                    b.Text = choices[i][1];
                    b.SetBounds(16, 48 + i * 42, 388, 34);
                    b.UseVisualStyleBackColor = true;
                    b.Click += delegate { pick = key; f.DialogResult = DialogResult.OK; };
                    f.Controls.Add(b);
                }
                if (f.ShowDialog(FindForm()) != DialogResult.OK || pick == null) return null;
                ProgramJob p;
                if (pick == "vvd") p = Templates.Vvd();
                else if (pick == "vmix") p = Templates.VMix();
                else if (pick == "service")
                {
                    p = new ProgramJob();
                    p.Name = "New service";
                    p.Kind = "service";
                    p.StartDelaySeconds = 0;
                }
                else
                {
                    p = new ProgramJob();
                    p.Name = "New program";
                }
                p.Enabled = false;
                return p;
            }
        }

        void PickService()
        {
            List<KeyValuePair<string, string>> all = new SystemServiceControl().List();
            if (all.Count == 0) { Ui.Info(FindForm(), "No Windows services could be listed on this computer. Type the service name."); return; }
            using (Form f = new Form())
            {
                f.Text = "Choose a Windows service";
                f.Font = Ui.BaseFont();
                f.StartPosition = FormStartPosition.CenterParent;
                f.MinimizeBox = f.MaximizeBox = false;
                f.ShowInTaskbar = false;
                f.ClientSize = new Size(620, 480);
                TextBox filter = new TextBox();
                filter.Dock = DockStyle.Top;
                ListView lv = new ListView();
                lv.View = View.Details;
                lv.FullRowSelect = true;
                lv.HideSelection = false;
                lv.MultiSelect = false;
                lv.Dock = DockStyle.Fill;
                lv.Columns.Add("Display name", 360);
                lv.Columns.Add("Service name", 220);
                Button ok = new Button();
                ok.Text = "Use this service";
                ok.Dock = DockStyle.Bottom;
                ok.Height = 34;
                string chosen = null;
                MethodInvoker refill = delegate
                {
                    string q = filter.Text.Trim();
                    lv.BeginUpdate();
                    lv.Items.Clear();
                    foreach (KeyValuePair<string, string> kv in all)
                    {
                        if (q.Length > 0 && kv.Key.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 && kv.Value.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        ListViewItem it = new ListViewItem(kv.Value);
                        it.SubItems.Add(kv.Key);
                        it.Tag = kv.Key;
                        lv.Items.Add(it);
                    }
                    lv.EndUpdate();
                };
                filter.TextChanged += delegate { refill(); };
                EventHandler accept = delegate
                {
                    if (lv.SelectedItems.Count == 0) return;
                    chosen = lv.SelectedItems[0].Tag as string;
                    f.DialogResult = DialogResult.OK;
                };
                ok.Click += accept;
                lv.DoubleClick += accept;
                f.Controls.Add(lv);
                f.Controls.Add(filter);
                f.Controls.Add(ok);
                refill();
                if (f.ShowDialog(FindForm()) != DialogResult.OK || chosen == null) return;
                pSvc.Text = chosen;
                if (pName.Text.Trim().Length == 0 || pName.Text == "New service")
                {
                    KeyValuePair<string, string> hit = all.Find(delegate(KeyValuePair<string, string> kv) { return kv.Key == chosen; });
                    pName.Text = hit.Value ?? chosen;
                }
            }
        }

        void FromExe()
        {
            string exe = pExe.Text.Trim().Trim('"');
            if (exe.Length == 0) return;
            if (pName.Text.Trim().Length == 0 || pName.Text == "New program")
                pName.Text = Path.GetFileNameWithoutExtension(exe);
            if (pProc.Text.Trim().Length == 0) pProc.Text = Path.GetFileNameWithoutExtension(exe);
        }

        void UpdateEnabled()
        {
            bool svc = kService.Checked;
            foreach (Control c in programOnly) c.Enabled = !svc;
            pSvc.Enabled = pSvcPick.Enabled = svc;
            bool h = hOn.Checked;
            hUrl.Enabled = hUser.Enabled = hEvery.Enabled = hFail.Enabled = hGrace.Enabled = hAlert.Enabled = hRestart.Enabled = h;
            hPassword.Enabled = hPlain.Enabled = h && hUser.Text.Trim().Length > 0;
            pElevate.Enabled = !svc && pUser.Checked;
            pTrust.Check(pExe.Text, !svc && (pService.Checked || (pUser.Checked && pElevate.Checked)));
            hTest.Enabled = h && hUrl.Text.Trim().Length > 0;
        }

        void Fill()
        {
            ProgramJob p = list.Selected;
            fill++;
            try
            {
                Quiet(delegate
                {
                    ProgramJob d = p ?? new ProgramJob();
                    pOn.Checked = d.Enabled;
                    pName.Text = p != null ? d.Name : "";
                    kProgram.Checked = d.Kind != "service";
                    kService.Checked = d.Kind == "service";
                    pSvc.Text = d.ServiceName;
                    pExe.Text = d.ExePath;
                    pArgs.Text = d.Arguments;
                    pDir.Text = d.WorkingDir;
                    pProc.Text = d.ProcessName;
                    pUser.Checked = d.RunAs == "user";
                    pService.Checked = d.RunAs == "service";
                    Ui.SetNum(pStartDelay, d.StartDelaySeconds);
                    Ui.SetNum(pRestartDelay, d.RestartDelaySeconds);
                    Ui.SetNum(pMax, d.MaxRestartsPerHour);
                    pRestart.Load(d.Restart);
                    Ui.Set(pPriority, d.Priority);
                    hOn.Checked = d.HealthEnabled;
                    hUrl.Text = d.HealthUrl;
                    hUser.Text = d.HealthUser;
                    hPlain.Checked = d.HealthAllowPlainLogin;
                    hPassword.Text = d.HealthPassword.Length > 0 ? Unchanged : "";
                    pElevate.Checked = d.AllowElevation;
                    Ui.SetNum(hEvery, d.HealthIntervalSeconds);
                    Ui.SetNum(hFail, d.HealthFailSeconds);
                    Ui.SetNum(hGrace, d.HealthGraceSeconds);
                    hAlert.Checked = d.HealthAction != "restart";
                    hRestart.Checked = d.HealthAction == "restart";
                    pNotes.Text = d.Notes;
                });
            }
            finally { fill--; }
            hResult.Text = "";
            UpdateEnabled();
            Tick();
        }

        void Apply()
        {
            UpdateEnabled();
            if (fill > 0 || Loading) return;
            ProgramJob p = list.Selected;
            if (p == null) return;
            p.Enabled = pOn.Checked;
            p.Name = pName.Text;
            p.Kind = kService.Checked ? "service" : "program";
            p.ServiceName = pSvc.Text.Trim();
            p.ExePath = pExe.Text;
            p.Arguments = pArgs.Text;
            p.WorkingDir = pDir.Text;
            p.ProcessName = pProc.Text;
            p.RunAs = pService.Checked ? "service" : "user";
            p.StartDelaySeconds = (int)pStartDelay.Value;
            p.RestartDelaySeconds = (int)pRestartDelay.Value;
            p.MaxRestartsPerHour = (int)pMax.Value;
            p.Restart = pRestart.Value();
            p.Priority = Ui.Get(pPriority);
            p.HealthEnabled = hOn.Checked;
            p.HealthUrl = hUrl.Text.Trim();
            p.HealthUser = hUser.Text.Trim();
            p.HealthAllowPlainLogin = hPlain.Checked;
            if (p.HealthUser.Length == 0) p.HealthPassword = "";
            else if (hPassword.Text != Unchanged) p.HealthPassword = hPassword.Text.Length > 0 ? "plain:" + hPassword.Text : "";
            p.AllowElevation = pElevate.Checked && p.RunAs == "user";
            p.HealthIntervalSeconds = (int)hEvery.Value;
            p.HealthFailSeconds = (int)hFail.Value;
            p.HealthGraceSeconds = (int)hGrace.Value;
            p.HealthAction = hRestart.Checked ? "restart" : "alert";
            p.Notes = pNotes.Text;
            list.RefreshSelected();
        }

        void TestHealth()
        {
            string url = hUrl.Text.Trim();
            if (url.Length == 0) return;
            if (!M.Online)
            {
                hResult.ForeColor = Ui.Bad;
                hResult.Text = "The service is not running; the check is made by the service.";
                return;
            }
            hResult.ForeColor = SystemColors.GrayText;
            hResult.Text = "Checking...";
            ProgramJob cur = list.Selected;
            JObject req = Req("health.test", "url", url);
            if (cur != null && cur.HealthUser.Length > 0)
            {
                ProgramJob saved = M.Config.Get().Programs.Find(delegate(ProgramJob x) { return x.Id == cur.Id; });
                req.Set("id", cur.Id);
                req.Set("user", cur.HealthUser);
                req.Set("allow_plain_login", cur.HealthAllowPlainLogin);
                req.Set("password", Secrets.KeepIfSaved(cur.HealthPassword, saved != null ? saved.HealthPassword : null, cur.Id));
            }
            M.Async(this, delegate { return M.Client.Call(req, 3000); }, delegate(JObject r, Exception ex)
            {
                if (ex != null) { hResult.ForeColor = Ui.Bad; hResult.Text = ex.Message; return; }
                string problem = r.Str("problem", "");
                hResult.ForeColor = problem.Length == 0 ? Ui.Ok : Ui.Bad;
                hResult.Text = problem.Length == 0 ? "Answers (" + r.Num("ms", 0).ToString("0", Util.Inv) + " ms)." : "Not healthy: " + problem;
            });
        }

        static JObject Req(string cmd, string k, string v)
        {
            JObject o = new JObject();
            o.Set("cmd", cmd);
            o.Set(k, v);
            return o;
        }

        void RestartNow()
        {
            ProgramJob p = list.Selected;
            if (p == null || p.Id.Length == 0) return;
            if (!Ui.Confirm(FindForm(), (p.Kind == "service" ? "Stop and start the service " : "Close ") + p.Name + (p.Kind == "service" ? " now?" : " and start it again now?"))) return;
            M.Async(this, delegate { return M.Call("program.restart", "id", p.Id); }, delegate(JObject r, Exception ex)
            {
                if (ex != null) Ui.Error(FindForm(), "Restart not possible: " + ex.Message);
            });
        }

        public static string Describe(ProgramStatus s)
        {
            if (s == null) return "";
            string t = s.StateText;
            if (s.State == "running")
            {
                t = s.Kind == "service" ? "Running" : "Running, PID " + s.Pid + (s.Session > 0 ? ", session " + s.Session : "");
                if (s.RunningSince > 0) t += ", for " + Ui.Ago(s.RunningSince);
            }
            if (s.HealthText.Length > 0) t += ". " + s.HealthText;
            if (s.StartsLastHour > 0) t += ". Started " + s.StartsLastHour + "x in the last hour";
            if (s.LastError.Length > 0 && s.State != "error") t += ". Last problem: " + s.LastError;
            if (s.NextScheduledRestart > 0) t += ". Next scheduled restart " + Ui.When(s.NextScheduledRestart);
            return t;
        }

        public override void Tick()
        {
            ProgramJob p = list.Selected;
            list.RefreshAll();
            if (p == null) { live.Text = ""; restartBtn.Enabled = false; return; }
            if (!M.Online)
            {
                live.ForeColor = SystemColors.GrayText;
                live.Text = "The service is not running, so nothing is being watched.";
                restartBtn.Enabled = false;
                return;
            }
            bool saved = p.Id.Length > 0 && M.Config.Get().Programs.Exists(delegate(ProgramJob x) { return x.Id == p.Id; });
            ProgramStatus s = saved ? M.Program(p.Id) : null;
            restartBtn.Enabled = s != null && p.Enabled && !Dirty;
            if (!saved || Dirty)
            {
                live.ForeColor = SystemColors.GrayText;
                live.Text = "Save to apply.";
                return;
            }
            if (s == null) { live.Text = ""; return; }
            live.ForeColor = s.Health == "failing" ? Ui.Bad : s.State == "running" ? Ui.Ok : s.State == "error" || s.State == "paused" ? Ui.Bad : s.State == "off" ? SystemColors.GrayText : Ui.Warn;
            live.Text = Describe(s);
        }

        protected override void LoadFrom(AppConfig c)
        {
            list.SetItems(Json.CloneList(c.Programs));
        }

        protected override void Collect(AppConfig c)
        {
            c.Programs = Json.CloneList(list.Items);
        }
    }
}
