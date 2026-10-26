// Studios page: the studios this server can run, the link to Pathfinder (status out, studio in) and the
// GPIO status pins, with the live state and a manual switch.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace Watchkeeper.UI
{
    public class StudiosPage : SectionPage
    {
        StudiosCfg cfg = new StudiosCfg();
        readonly Dictionary<string, int> pins = new Dictionary<string, int>();     // studio id -> GPIO status pin
        int fill;

        // live state and manual switch
        readonly Label nowState = new Label();
        readonly Label nowLink = new Label();
        readonly Label nowGpio = new Label();
        readonly Label nowLast = new Label();
        readonly ComboBox switchTo = new ComboBox();
        readonly Button switchBtn;
        readonly Label switchResult = new Label();
        readonly TabControl tabs = new TabControl();

        // studios
        readonly ListEditor<StudioDef> list = new ListEditor<StudioDef>("studio");
        readonly TextBox sName = Ui.Text(260);
        readonly TextBox sCode = Ui.Text(120);
        readonly NumericUpDown sPin = Ui.Num(0, 5, 1, 0);
        readonly TextBox sNotes = Ui.Text(420);
        readonly Label sPrograms = new Label();
        readonly ComboBox startStudio = new ComboBox();
        readonly NumericUpDown closeTimeout = Ui.Num(1, 600, 5, 0);
        readonly NumericUpDown minBetween = Ui.Num(0, 3600, 5, 0);

        // Pathfinder
        readonly CheckBox pOn = Ui.Chk("Report to Pathfinder and take the studio from Pathfinder");
        readonly TextBox pHost = Ui.Text(200);
        readonly NumericUpDown pPort = Ui.Num(1, 65535, 1, 0);
        readonly CheckBox pTls = Ui.Chk("TLS (Pathfinder Core PRO port 9602, with its certificate set up)");
        readonly TextBox pFinger = Ui.Text(420);
        readonly TextBox pUser = Ui.Text(200);
        readonly TextBox pPassword = Ui.Text(200);
        readonly CheckBox pPlain = Ui.Chk("Send the login unencrypted (port 9600 without TLS: readable on the network)");
        readonly NumericUpDown pInterval = Ui.Num(1, 60, 1, 0);
        readonly TextBox pBeat = Ui.Text(260);
        readonly TextBox pStatus = Ui.Text(260);
        readonly TextBox pStudio = Ui.Text(260);
        readonly TextBox pCommand = Ui.Text(260);
        readonly Button pTest;
        readonly Label pResult = new Label();

        // GPIO status
        readonly CheckBox gOn = Ui.Chk("Set status pins on an Axia device");
        readonly ComboBox gDevice = new ComboBox();
        readonly ComboBox gSignal;
        readonly NumericUpDown gPort = Ui.Num(1, 256, 1, 0);
        readonly NumericUpDown gInterval = Ui.Num(1, 60, 1, 0);
        readonly NumericUpDown gBeat = Ui.Num(0, 5, 1, 0);
        readonly NumericUpDown gOk = Ui.Num(0, 5, 1, 0);
        readonly Label gPins = new Label();

        public StudiosPage(Manager m)
            : base(m, "studios", "Studios",
                   "The studios this server can run, each with its own programs (tick them on the Watchdog page). Pathfinder decides which studio runs: " +
                   "Watchkeeper reports a heartbeat, its status and the active studio to Pathfinder, and switches when Pathfinder (or a GPIO trigger) asks. " +
                   "It never switches on its own.")
        {
            gSignal = Ui.Combo(new Choice("gpi", "GPI (the IP-Audio driver's virtual inputs; what Pathfinder reads)"), new Choice("gpo", "GPO"));
            switchBtn = Ui.Btn("Switch now", delegate { SwitchNow(); });
            pTest = Ui.Btn("Test connection", delegate { TestLink(); });
            foreach (Control c in new Control[] { nowState, nowLink, nowGpio, nowLast, switchTo, switchBtn, switchResult, pTest, pResult, sPrograms, gPins }) c.Tag = "nodirty";
            foreach (ComboBox c in new[] { switchTo, startStudio, gDevice })
            {
                c.DropDownStyle = ComboBoxStyle.DropDownList;
                c.Width = 300;
                c.Anchor = AnchorStyles.Left;
            }
            pPassword.UseSystemPasswordChar = true;
            foreach (Label l in new[] { nowState, nowLink, nowGpio, nowLast, switchResult, pResult, sPrograms, gPins })
            {
                l.AutoSize = true;
                l.MaximumSize = new Size(760, 0);
                l.Margin = new Padding(0, 4, 0, 0);
            }
            switchResult.Margin = new Padding(8, 7, 0, 0);

            FormGrid top = new FormGrid();
            top.Row("Now", nowState);
            top.Row("Pathfinder", nowLink);
            top.Row("GPIO status", nowGpio);
            top.Row("Last switch", nowLast);
            top.Row("Switch to", Ui.Flow(switchTo, switchBtn, switchResult));
            top.Dock = DockStyle.Top;

            tabs.Dock = DockStyle.Fill;
            tabs.TabPages.Add(Tab("Studios", BuildStudios()));
            tabs.TabPages.Add(Tab("Pathfinder", Scroller(BuildPathfinder())));
            tabs.TabPages.Add(Tab("GPIO status", Scroller(BuildGpio())));

            Body.AutoScroll = false;
            Body.Controls.Add(tabs);
            Body.Controls.Add(top);
            Init();
        }

        static TabPage Tab(string title, Control content)
        {
            TabPage p = new TabPage(title);
            p.Padding = new Padding(10, 8, 6, 6);
            p.UseVisualStyleBackColor = true;
            p.Controls.Add(content);
            return p;
        }

        static Panel Scroller(Control content)
        {
            Panel s = new Panel();
            s.Dock = DockStyle.Fill;
            s.AutoScroll = true;
            s.MinimumSize = new Size(160, 120);
            s.Controls.Add(content);
            return s;
        }

        // =============================================================== studios
        Control BuildStudios()
        {
            FormGrid g0 = new FormGrid();
            g0.Row("First start", startStudio, "The studio this server runs the first time. After that it keeps the last active studio across restarts until Pathfinder or someone here chooses another.");
            Label s1 = Ui.Lbl("seconds to close, then they are ended");
            g0.Row("Old studio's programs get", Ui.Flow(closeTimeout, s1));
            Label s2 = Ui.Lbl("seconds apart (Pathfinder and GPIO; a switch from this page is not held back)");
            g0.Row("Switches at least", Ui.Flow(minBetween, s2));
            g0.Dock = DockStyle.Top;

            list.EmptyText = "No studios yet.\r\n\r\nClick \"Add studio\" for each studio this server can run, then tick its programs on the Watchdog page.";
            list.RemoveNote = "Watchdog entries and GPIO actions that use it stop running until they are changed.";
            list.Create = delegate
            {
                StudioDef d = new StudioDef();
                d.Id = Cfg.NewId();
                d.Name = "Studio " + (cfg.Studios.Count + 1);
                d.Code = "S" + (cfg.Studios.Count + 1);
                return d;
            };
            list.Copy = delegate(StudioDef d)
            {
                StudioDef c = Json.Clone(d);
                c.Id = Cfg.NewId();
                c.Name = d.Name + " (copy)";
                c.Code = d.Code + "B";
                return c;
            };
            list.Title = delegate(StudioDef d) { return d.Name; };
            list.Subtitle = delegate(StudioDef d) { return "Code " + (d.Code.Length > 0 ? d.Code : "(none)") + "  -  " + ProgramsOf(d.Id); };
            list.Badge = delegate(StudioDef d)
            {
                EngineStatus st = M.Status;
                return st != null && st.Studios != null && st.Studios.Active == d.Id ? "active" : "";
            };
            list.SelectedChanged += delegate { FillStudio(); };
            list.Changed += delegate { MarkDirty(); RefreshCombos(); };

            FormGrid g = new FormGrid();
            g.Row("Name", sName);
            g.Row("Code", sCode, "What Pathfinder writes into the command memory slot (or a GPIO command message sends) to choose this studio, e.g. S1. " +
                                 "Letters, digits, _ and -. \"none\" chooses no studio.");
            Label pinNote = Ui.Lbl("(GPIO status tab; 0 = none) low while this studio is active");
            g.Row("GPIO status pin", Ui.Flow(sPin, pinNote));
            g.Row("Programs", sPrograms);
            g.Row("Notes", sNotes);
            list.Detail.Controls.Add(g);

            EventHandler apply = delegate { ApplyStudio(); };
            sName.TextChanged += apply;
            sCode.TextChanged += apply;
            sPin.ValueChanged += apply;
            sNotes.TextChanged += apply;

            Panel p = new Panel();
            p.Dock = DockStyle.Fill;
            p.Controls.Add(list);
            p.Controls.Add(g0);
            return p;
        }

        string ProgramsOf(string id)
        {
            List<string> n = new List<string>();
            foreach (ProgramJob p in M.Config.Get().Programs) if (p.Studios.Contains(id)) n.Add(p.Name + (p.Enabled ? "" : " (off)"));
            return n.Count > 0 ? string.Join(", ", n.ToArray()) : "no programs ticked yet";
        }

        void FillStudio()
        {
            StudioDef d = list.Selected;
            fill++;
            try
            {
                Quiet(delegate
                {
                    StudioDef x = d ?? new StudioDef();
                    sName.Text = d != null ? x.Name : "";
                    sCode.Text = x.Code;
                    int pin;
                    Ui.SetNum(sPin, d != null && pins.TryGetValue(d.Id, out pin) ? pin : 0);
                    sNotes.Text = x.Notes;
                });
            }
            finally { fill--; }
            sPrograms.ForeColor = SystemColors.GrayText;
            sPrograms.Text = d == null ? "" : ProgramsOf(d.Id) + ". Untagged watchdog entries run in every studio.";
        }

        void ApplyStudio()
        {
            if (fill > 0 || Loading) return;
            StudioDef d = list.Selected;
            if (d == null) return;
            d.Name = sName.Text;
            d.Code = sCode.Text.Trim();
            d.Notes = sNotes.Text;
            pins[d.Id] = (int)sPin.Value;
            list.RefreshSelected();
            RefreshCombos();
            MarkDirty();
        }

        void RefreshCombos()
        {
            Quiet(RefreshCombosNow);
        }

        void RefreshCombosNow()
        {
            fill++;
            try
            {
                string keepStart = Ui.Get(startStudio), keepSwitch = Ui.Get(switchTo);
                startStudio.Items.Clear();
                startStudio.Items.Add(new Choice("", "No studio"));
                foreach (StudioDef d in cfg.Studios) startStudio.Items.Add(new Choice(d.Id, d.Name + "  (" + d.Code + ")"));
                Ui.Set(startStudio, keepStart);
                if (startStudio.SelectedIndex < 0) startStudio.SelectedIndex = 0;
                // the switch list offers the saved studios: the service knows only those
                switchTo.Items.Clear();
                foreach (StudioDef d in M.Config.Get().Studios.Studios) switchTo.Items.Add(new Choice(d.Id, d.Name + "  (" + d.Code + ")"));
                switchTo.Items.Add(new Choice(StudioSettings.None, "No studio (close the studio programs)"));
                Ui.Set(switchTo, keepSwitch);
                if (switchTo.SelectedIndex < 0 && switchTo.Items.Count > 0) switchTo.SelectedIndex = 0;
            }
            finally { fill--; }
        }

        // ============================================================ Pathfinder
        Control BuildPathfinder()
        {
            FormGrid g = new FormGrid();
            g.Row(null, pOn);
            Label portL = Ui.Lbl("port");
            portL.Margin = new Padding(12, 6, 4, 0);
            g.Row("Pathfinder Core PRO", Ui.Flow(pHost, portL, pPort), "Its address on the Axia network. SapV2 protocol: port 9600, or 9602 with TLS.");
            g.Row(null, pTls);
            g.Row("Trust this certificate", pFinger, "Only for TLS with a certificate Windows does not trust (self-signed): its SHA-256 fingerprint. \"Test connection\" shows it.");
            g.Row("User name", pUser, "A Pathfinder Core PRO user (no spaces). Give it only what it needs: the memory slots below.");
            g.Row("Password", pPassword, "Stored encrypted. Leave the dots to keep the saved password. No spaces.");
            g.Row(null, pPlain, "Without TLS, the login crosses the Axia network readable; only when this is ticked (or Pathfinder runs on this server).");
            Label secs = Ui.Lbl("seconds");
            g.Row("Every", Ui.Flow(pInterval, secs));
            g.Header("Memory slots (create them in Pathfinder first; empty = not used)");
            g.Row("Heartbeat", pBeat, "Watchkeeper writes a counter that goes up every interval. In Pathfinder: a Logic Flow restarts a timer on every change; " +
                                      "when the timer runs out, this server is gone. Example name: SRV1_Heartbeat.");
            g.Row("Status", pStatus, "ok, degraded (a program of the studio is not running or not answering), switching or error. Example: SRV1_Status.");
            g.Row("Active studio", pStudio, "The code of the studio this server runs (none = no studio; switching while it changes). Example: SRV1_Studio.");
            g.Row("Studio to run", pCommand, "Pathfinder writes the code of the studio this server should run (or none). Watchkeeper reads it every interval and switches " +
                                             "when it differs from the active studio. Empty slot = no instruction. Example: SRV1_Command.");
            pResult.Margin = new Padding(8, 7, 0, 0);
            g.Row(null, Ui.Flow(pTest, pResult));
            g.Note("The connection is made by Watchkeeper (nothing on this server listens for Pathfinder). Use one Pathfinder user per server or one for all; " +
                   "a slot name per server keeps them apart.");

            EventHandler apply = delegate { ApplyLink(); };
            foreach (TextBox t in new[] { pHost, pFinger, pUser, pPassword, pBeat, pStatus, pStudio, pCommand }) t.TextChanged += apply;
            foreach (CheckBox c in new[] { pOn, pTls, pPlain }) c.CheckedChanged += apply;
            pPort.ValueChanged += apply;
            pInterval.ValueChanged += apply;
            pTls.CheckedChanged += delegate
            {
                if (fill > 0 || Loading) return;
                if (pTls.Checked && pPort.Value == 9600) pPort.Value = 9602;
                else if (!pTls.Checked && pPort.Value == 9602) pPort.Value = 9600;
            };
            return g;
        }

        void ApplyLink()
        {
            UpdateEnabled();
            if (fill > 0 || Loading) return;
            PathfinderCfg p = cfg.Pathfinder;
            p.Enabled = pOn.Checked;
            p.Host = pHost.Text.Trim();
            p.Port = (int)pPort.Value;
            p.Tls = pTls.Checked;
            p.TrustFingerprint = pFinger.Text.Trim();
            p.User = pUser.Text.Trim();
            if (pPassword.Text != GpioPage.Unchanged) p.Password = pPassword.Text.Length > 0 ? "plain:" + pPassword.Text : "";
            p.AllowPlainLogin = pPlain.Checked;
            p.IntervalSeconds = (int)pInterval.Value;
            p.HeartbeatSlot = pBeat.Text.Trim();
            p.StatusSlot = pStatus.Text.Trim();
            p.StudioSlot = pStudio.Text.Trim();
            p.CommandSlot = pCommand.Text.Trim();
        }

        void TestLink()
        {
            if (!M.Online) { pResult.ForeColor = Ui.Bad; pResult.Text = "The service is not running; the test is made by the service."; return; }
            ApplyLink();
            PathfinderCfg snap = Json.Clone(cfg.Pathfinder);
            snap.Password = Secrets.KeepIfSaved(snap.Password, M.Config.Get().Studios.Pathfinder.Password, "pathfinder");
            pResult.ForeColor = SystemColors.GrayText;
            pResult.Text = "Connecting...";
            M.Async(this, delegate
            {
                JObject req = new JObject();
                req.Set("cmd", "studios.test");
                req.Set("pathfinder", Json.ToJson(snap));
                return M.Client.Call(req, 30000);
            }, delegate(JObject r, Exception ex)
            {
                if (ex != null) { pResult.ForeColor = Ui.Bad; pResult.Text = ex.Message; return; }
                string trace = r.Str("trace", "");
                bool ok = trace.Contains("Logged in.") && !trace.Contains("check that this memory slot exists");
                pResult.ForeColor = ok ? Ui.Ok : Ui.Bad;
                pResult.Text = ok ? "Logged in; every memory slot answered." : "Not everything worked; see the details.";
                ResultDialog.Display(FindForm(), "Pathfinder connection", pResult.Text, trace);
            });
        }

        // ========================================================== GPIO status
        Control BuildGpio()
        {
            FormGrid g = new FormGrid();
            g.Row(null, gOn);
            g.Row("Axia device", gDevice, "From the GPIO page. Usually the IP-Audio driver on this server (127.0.0.1, port 93), whose GPIO port Pathfinder reads. " +
                                          "The GPIO part must be on and listening.");
            g.Row("Set", gSignal);
            g.Row("Port", gPort);
            Label secs = Ui.Lbl("seconds");
            g.Row("Every", Ui.Flow(gInterval, secs));
            g.Header("Pins (1-5, 0 = not used; low = on)");
            g.Row("Heartbeat", gBeat, "Changes between low and high every interval while Watchkeeper runs. Pathfinder: a timer restarted on every change of this pin.");
            g.Row("OK", gOk, "Low while every program of the active studio runs and answers.");
            g.Row("Studio pins", gPins);
            g.Note("Each studio's pin is set on the Studios tab: low while that studio is active.");

            EventHandler apply = delegate { ApplyGpio(); };
            gOn.CheckedChanged += apply;
            gDevice.SelectedIndexChanged += apply;
            gSignal.SelectedIndexChanged += apply;
            foreach (NumericUpDown n in new[] { gPort, gInterval, gBeat, gOk }) n.ValueChanged += apply;
            return g;
        }

        void ApplyGpio()
        {
            UpdateEnabled();
            if (fill > 0 || Loading) return;
            GpioStatusCfg g = cfg.GpioStatus;
            g.Enabled = gOn.Checked;
            g.DeviceId = Ui.Get(gDevice);
            g.Signal = Ui.Get(gSignal);
            g.Port = (int)gPort.Value;
            g.IntervalSeconds = (int)gInterval.Value;
            g.HeartbeatPin = (int)gBeat.Value;
            g.OkPin = (int)gOk.Value;
        }

        void UpdateEnabled()
        {
            bool p = pOn.Checked;
            foreach (Control c in new Control[] { pHost, pPort, pTls, pUser, pPassword, pInterval, pBeat, pStatus, pStudio, pCommand }) c.Enabled = p;
            pFinger.Enabled = p && pTls.Checked;
            pPlain.Enabled = p && !pTls.Checked;
            bool g = gOn.Checked;
            foreach (Control c in new Control[] { gDevice, gSignal, gPort, gInterval, gBeat, gOk }) c.Enabled = g;
            List<string> parts = new List<string>();
            foreach (StudioDef d in cfg.Studios)
            {
                int pin;
                if (pins.TryGetValue(d.Id, out pin) && pin > 0) parts.Add(d.Name + ": pin " + pin);
            }
            gPins.ForeColor = SystemColors.GrayText;
            gPins.Text = parts.Count > 0 ? string.Join(", ", parts.ToArray()) : "none set";
        }

        // ================================================================ switch
        void SwitchNow()
        {
            string target = Ui.Get(switchTo);
            if (target.Length == 0) return;
            if (!M.Online) { switchResult.ForeColor = Ui.Bad; switchResult.Text = "The service is not running."; return; }
            EngineStatus st = M.Status;
            string warn = "";
            if (st != null && st.Studios != null && st.Studios.PathfinderState == "connected" && st.Studios.Desired.Length > 0)
                warn = "\n\nPathfinder asks for '" + st.Studios.Desired + "'. Unless that is changed in Pathfinder, Watchkeeper switches back to it within seconds.";
            Choice c = switchTo.SelectedItem as Choice;
            if (!Ui.Confirm(FindForm(), "Switch this server to " + (c != null ? c.Text : target) + " now? The programs of the current studio are closed " +
                                        "(asked first, ended if they do not close) and those of the new studio started." + warn)) return;
            switchResult.ForeColor = SystemColors.GrayText;
            switchResult.Text = "Asking the service...";
            M.Async(this, delegate { return M.Call("studios.switch", "studio", target); }, delegate(JObject r, Exception ex)
            {
                if (ex != null) { switchResult.ForeColor = Ui.Bad; switchResult.Text = ex.Message; return; }
                switchResult.ForeColor = r.Bool("ok", false) ? SystemColors.GrayText : Ui.Bad;
                switchResult.Text = r.Str("text", "");
            });
        }

        public override void Tick()
        {
            EngineStatus st = M.Status;
            StudioStatus s = st != null ? st.Studios : null;
            bool online = M.Online && s != null;
            switchBtn.Enabled = online && s.On;
            if (!online)
            {
                foreach (Label l in new[] { nowState, nowLink, nowGpio, nowLast }) { l.ForeColor = SystemColors.GrayText; l.Text = "The service is not running."; }
                return;
            }
            nowState.ForeColor = s.State == "ok" ? Ui.Ok : s.State == "error" ? Ui.Bad : s.State == "off" ? SystemColors.GrayText : Ui.Warn;
            nowState.Text = s.ActiveName + (s.ActiveCode.Length > 0 && s.Active.Length > 0 ? " (" + s.ActiveCode + ")" : "") +
                            (s.ActiveSince > 0 && s.Active.Length > 0 ? " since " + Ui.When(s.ActiveSince) : "") + " - " + s.State + ": " + s.StateText;
            nowLink.ForeColor = s.PathfinderState == "connected" ? Ui.Ok : s.PathfinderState == "error" ? Ui.Bad : SystemColors.GrayText;
            nowLink.Text = s.PathfinderText + (s.PathfinderState == "connected" ? ". Studio to run: " + (s.Desired.Length > 0 ? s.Desired : "(empty: no instruction)") : "");
            nowGpio.ForeColor = s.GpioState == "ok" ? Ui.Ok : s.GpioState == "error" ? Ui.Bad : SystemColors.GrayText;
            nowGpio.Text = s.GpioText;
            nowLast.ForeColor = SystemColors.GrayText;
            nowLast.Text = s.LastSwitch.Length > 0 ? s.LastSwitch : "None since the service started.";
            list.RefreshAll();
        }

        public override void OnShown() { RefreshCombos(); }

        protected override void LoadFrom(AppConfig c)
        {
            cfg = Json.Clone(c.Studios);
            pins.Clear();
            foreach (StudioPin sp in cfg.GpioStatus.StudioPins) pins[sp.StudioId] = sp.Pin;
            fill++;
            try
            {
                RefreshCombos();
                Ui.Set(startStudio, cfg.StartStudio);
                Ui.SetNum(closeTimeout, cfg.CloseTimeoutSeconds);
                Ui.SetNum(minBetween, cfg.MinSecondsBetweenSwitches);
                PathfinderCfg p = cfg.Pathfinder;
                pOn.Checked = p.Enabled;
                pHost.Text = p.Host;
                Ui.SetNum(pPort, p.Port);
                pTls.Checked = p.Tls;
                pFinger.Text = p.TrustFingerprint;
                pUser.Text = p.User;
                pPassword.Text = p.Password.Length > 0 ? GpioPage.Unchanged : "";
                pPlain.Checked = p.AllowPlainLogin;
                Ui.SetNum(pInterval, p.IntervalSeconds);
                pBeat.Text = p.HeartbeatSlot;
                pStatus.Text = p.StatusSlot;
                pStudio.Text = p.StudioSlot;
                pCommand.Text = p.CommandSlot;
                GpioStatusCfg g = cfg.GpioStatus;
                gOn.Checked = g.Enabled;
                gDevice.Items.Clear();
                foreach (AxiaDevice d in c.Gpio.Devices) gDevice.Items.Add(new Choice(d.Id, d.Name + " (" + d.Host + ":" + d.Port + ")"));
                Ui.Set(gDevice, g.DeviceId);
                Ui.Set(gSignal, g.Signal);
                Ui.SetNum(gPort, g.Port);
                Ui.SetNum(gInterval, g.IntervalSeconds);
                Ui.SetNum(gBeat, g.HeartbeatPin);
                Ui.SetNum(gOk, g.OkPin);
            }
            finally { fill--; }
            list.SetItems(cfg.Studios);
            UpdateEnabled();
        }

        protected override void Collect(AppConfig c)
        {
            ApplyLink();
            ApplyGpio();
            cfg.StartStudio = Ui.Get(startStudio);
            cfg.CloseTimeoutSeconds = (int)closeTimeout.Value;
            cfg.MinSecondsBetweenSwitches = (int)minBetween.Value;
            cfg.Studios = new List<StudioDef>(list.Items);
            cfg.GpioStatus.StudioPins.Clear();
            foreach (StudioDef d in cfg.Studios)
            {
                int pin;
                if (pins.TryGetValue(d.Id, out pin) && pin > 0)
                {
                    StudioPin sp = new StudioPin();
                    sp.StudioId = d.Id;
                    sp.Pin = pin;
                    cfg.GpioStatus.StudioPins.Add(sp);
                }
            }
            c.Studios = Json.Clone(cfg);
        }

        protected override void AfterSave() { RefreshCombos(); }
    }
}
