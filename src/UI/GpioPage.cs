// GPIO page: Axia devices (LWRP), vMix instances, triggers (which pin does what in vMix or over
// HTTP), and a live view of every pin with the most recent changes. Triggers can be learned
// from the device, tried with "Run now", and simulated end to end without hardware.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace Watchkeeper.UI
{
    /// <summary>Editor for one list of actions (what happens when a pin turns on, or off).</summary>
    public class ActionEditor : Panel
    {
        readonly ListEditor<GpioAction> list = new ListEditor<GpioAction>("action", true, 150);
        readonly ComboBox aType;
        readonly ComboBox aVmix = new ComboBox();
        readonly ComboBox aFunc = new ComboBox();
        readonly Label aHint = new Label();
        readonly ComboBox aInput = new ComboBox();
        readonly TextBox aValue = Ui.Text(300);
        readonly TextBox aDuration = Ui.Text(80);
        readonly TextBox aSelName = Ui.Text(200);
        readonly TextBox aMix = Ui.Text(60);
        readonly TextBox aExtra = Ui.Text(300);
        readonly ComboBox aMethod;
        readonly TextBox aUrl = Ui.Text(460);
        readonly TextBox aCType = Ui.Text(220);
        readonly TextBox aBody = new TextBox();
        readonly TextBox aUser = Ui.Text(200);
        readonly TextBox aPassword = Ui.Text(200);
        readonly CheckBox aPlain = Ui.Chk("Send the login unencrypted (http to another computer: readable on the network)");
        readonly NumericUpDown aDelay = Ui.Num(0, 60000, 100, 0);
        readonly Button runBtn;
        readonly Label runResult = new Label();
        readonly List<Control> vmixRows = new List<Control>(), httpRows = new List<Control>();
        int fill;

        public event EventHandler Changed;
        public event EventHandler RunRequested;
        public Func<List<VmixTarget>> Targets = delegate { return new List<VmixTarget>(); };
        public Func<string, List<VmixInput>> Inputs = delegate { return null; };

        public ActionEditor(string runText)
        {
            Dock = DockStyle.Fill;
            Tag = "nodirty";        // edits are reported through Changed (the page's own change tracking skips this panel)
            aType = Ui.Combo(new Choice("vmix", "vMix function"), new Choice("http", "HTTP request (any REST API)"));
            aMethod = Ui.Combo(new Choice("GET", "GET"), new Choice("POST", "POST"), new Choice("PUT", "PUT"), new Choice("DELETE", "DELETE"));
            aVmix.DropDownStyle = ComboBoxStyle.DropDownList;
            aVmix.Width = 260;
            aVmix.Anchor = AnchorStyles.Left;
            aFunc.DropDownStyle = ComboBoxStyle.DropDown;
            aFunc.Width = 260;
            aFunc.Anchor = AnchorStyles.Left;
            aFunc.MaxDropDownItems = 20;
            foreach (string[] f in Vmix.Functions) aFunc.Items.Add(f[0]);
            aInput.DropDownStyle = ComboBoxStyle.DropDown;
            aInput.Width = 300;
            aInput.Anchor = AnchorStyles.Left;
            aInput.MaxDropDownItems = 20;
            aHint.AutoSize = true;
            aHint.MaximumSize = new Size(520, 0);
            aHint.ForeColor = SystemColors.GrayText;
            aHint.Margin = new Padding(0, 0, 0, 4);
            aHint.Tag = "nodirty";
            aBody.Multiline = true;
            aBody.ScrollBars = ScrollBars.Vertical;
            aBody.Width = 460;
            aBody.Height = 70;
            aBody.Font = Ui.MonoFont(Ui.BaseFont());
            aBody.Anchor = AnchorStyles.Left;
            runBtn = Ui.Btn(runText, delegate { EventHandler h = RunRequested; if (h != null) h(this, EventArgs.Empty); });
            runBtn.Tag = "nodirty";
            runResult.AutoSize = true;
            runResult.MaximumSize = new Size(560, 0);
            runResult.Margin = new Padding(8, 7, 0, 0);
            runResult.Tag = "nodirty";

            list.EmptyText = "No actions: nothing happens.\r\n\r\nClick \"Add action\".";
            list.Create = delegate
            {
                GpioAction a = new GpioAction();
                List<VmixTarget> t = Targets();
                if (t.Count > 0) a.VmixId = t[0].Id;
                return a;
            };
            list.Copy = delegate(GpioAction a) { return Json.Clone(a); };
            list.Title = delegate(GpioAction a) { return Title(a); };
            list.Subtitle = delegate(GpioAction a) { return Subtitle(a); };
            list.SelectedChanged += delegate { Fill(); };
            list.Changed += delegate { Raise(); };

            FormGrid g = new FormGrid();
            g.Row("Action", aType);
            Row(g, vmixRows, "vMix", aVmix, null);
            Row(g, vmixRows, "Function", aFunc, null);
            g.Wide(aHint);
            vmixRows.Add(aHint);
            Row(g, vmixRows, "Input", aInput, "Number, title or key. The list shows the inputs of the chosen vMix (load them on the vMix tab).");
            Row(g, vmixRows, "Value", aValue, null);
            Label ms = Ui.Lbl("ms");
            Label mixL = Ui.Lbl("Mix");
            mixL.Margin = new Padding(12, 6, 4, 0);
            Row(g, vmixRows, "Duration", Ui.Flow(aDuration, ms, mixL, aMix), null);
            Row(g, vmixRows, "SelectedName", aSelName, "Title field for SetText / SetImage, e.g. Headline.Text.");
            Row(g, vmixRows, "More parameters", aExtra, "Name=Value&Name2=Value2, added as they are.");
            Row(g, httpRows, "Method", aMethod, null);
            Row(g, httpRows, "Address", aUrl, "e.g. http://192.168.1.20:8080/api/scene/1");
            Row(g, httpRows, "Content type", aCType, null);
            Row(g, httpRows, "Body", aBody, "Sent with POST and PUT.");
            Row(g, httpRows, "User name", aUser, "Only if the address needs a login (HTTP basic). Never put a user name or password in the address.");
            Row(g, httpRows, "Password", aPassword, "Stored encrypted. Leave the dots to keep the saved password.");
            Row(g, httpRows, null, aPlain, "Without this, a login is only sent over https or to this server itself.");
            Label ms2 = Ui.Lbl("ms before this action");
            g.Row("Wait", Ui.Flow(aDelay, ms2));
            g.Row(null, Ui.Flow(runBtn, runResult));
            list.Detail.Controls.Add(g);
            Controls.Add(list);

            EventHandler apply = delegate { Apply(); };
            aType.SelectedIndexChanged += apply;
            aVmix.SelectedIndexChanged += delegate { FillInputs(); Apply(); };
            aFunc.TextChanged += apply;
            aInput.TextChanged += apply;
            aInput.SelectionChangeCommitted += delegate
            {
                Choice c = aInput.SelectedItem as Choice;
                if (c != null) BeginInvoke((MethodInvoker)delegate { aInput.Text = c.Value; Apply(); });
            };
            aValue.TextChanged += apply;
            aDuration.TextChanged += apply;
            aSelName.TextChanged += apply;
            aMix.TextChanged += apply;
            aExtra.TextChanged += apply;
            aMethod.SelectedIndexChanged += apply;
            aUrl.TextChanged += apply;
            aCType.TextChanged += apply;
            aBody.TextChanged += apply;
            aUser.TextChanged += apply;
            aPassword.TextChanged += apply;
            aPlain.CheckedChanged += apply;
            aPassword.UseSystemPasswordChar = true;
            aDelay.ValueChanged += apply;
        }

        static void Row(FormGrid g, List<Control> group, string label, Control c, string hint)
        {
            int before = g.Controls.Count;
            g.Row(label, c, hint);
            for (int i = before; i < g.Controls.Count; i++) group.Add(g.Controls[i]);
        }

        public static string Title(GpioAction a)
        {
            if (a.Type == "http") return a.Method + " " + (a.Url.Length > 0 ? a.Url : "(no address)");
            return (a.Function.Length > 0 ? a.Function : "(no function)") + (a.Input.Length > 0 ? "  " + a.Input : "");
        }

        static string Subtitle(GpioAction a)
        {
            List<string> p = new List<string>();
            if (a.DelayMs > 0) p.Add("after " + a.DelayMs + " ms");
            if (a.Type == "vmix")
            {
                if (a.Value.Length > 0) p.Add("Value " + a.Value);
                if (a.SelectedName.Length > 0) p.Add(a.SelectedName);
                if (a.Duration.Length > 0) p.Add(a.Duration + " ms");
            }
            return p.Count > 0 ? string.Join(", ", p.ToArray()) : (a.Type == "vmix" ? "vMix" : "HTTP");
        }

        public void SetItems(List<GpioAction> items)
        {
            runResult.Text = "";
            list.SetItems(items);
        }

        public void ShowResult(string text, bool ok)
        {
            runResult.ForeColor = ok ? Ui.Ok : Ui.Bad;
            runResult.Text = text;
        }

        public void RefreshTargets()
        {
            fill++;
            try
            {
                string keep = aVmix.SelectedItem is Choice ? ((Choice)aVmix.SelectedItem).Value : "";
                aVmix.Items.Clear();
                foreach (VmixTarget t in Targets()) aVmix.Items.Add(new Choice(t.Id, t.Name + " (" + t.Host + ":" + t.Port + ")"));
                Ui.Set(aVmix, keep);
            }
            finally { fill--; }
            FillInputs();
        }

        void FillInputs()
        {
            string id = Ui.Get(aVmix);
            List<VmixInput> inputs = id.Length > 0 ? Inputs(id) : null;
            string text = aInput.Text;
            fill++;
            try
            {
                aInput.Items.Clear();
                if (inputs != null)
                    foreach (VmixInput i in inputs) aInput.Items.Add(new Choice(i.Title, i.Number + "   " + i.Title + "   (" + i.Type + ")"));
                aInput.Text = text;
            }
            finally { fill--; }
        }

        void UpdateVisible()
        {
            bool vmix = Ui.Get(aType) != "http";
            foreach (Control c in vmixRows) c.Visible = vmix;
            foreach (Control c in httpRows) c.Visible = !vmix;
            aHint.Text = Vmix.Hint(aFunc.Text);
        }

        void Fill()
        {
            GpioAction a = list.Selected;
            fill++;
            try
            {
                GpioAction d = a ?? new GpioAction();
                Ui.Set(aType, d.Type);
                RefreshTargetsQuiet(d.VmixId);
                aFunc.Text = d.Function;
                aInput.Text = d.Input;
                aValue.Text = d.Value;
                aDuration.Text = d.Duration;
                aSelName.Text = d.SelectedName;
                aMix.Text = d.Mix;
                aExtra.Text = d.Extra;
                Ui.Set(aMethod, d.Method);
                aUrl.Text = d.Url;
                aCType.Text = d.ContentType;
                aBody.Text = d.Body;
                aUser.Text = d.User;
                aPlain.Checked = d.AllowPlainLogin;
                aPassword.Text = d.Password.Length > 0 ? GpioPage.Unchanged : "";
                Ui.SetNum(aDelay, d.DelayMs);
            }
            finally { fill--; }
            UpdateVisible();
        }

        void RefreshTargetsQuiet(string select)
        {
            aVmix.Items.Clear();
            foreach (VmixTarget t in Targets()) aVmix.Items.Add(new Choice(t.Id, t.Name + " (" + t.Host + ":" + t.Port + ")"));
            if (select.Length > 0) Ui.Set(aVmix, select);
            else if (aVmix.Items.Count > 0) aVmix.SelectedIndex = 0;
            FillInputs();
        }

        void Apply()
        {
            UpdateVisible();
            if (fill > 0) return;
            GpioAction a = list.Selected;
            if (a == null) return;
            a.Type = Ui.Get(aType);
            a.VmixId = Ui.Get(aVmix);
            a.Function = aFunc.Text.Trim();
            a.Input = aInput.Text.Trim();
            a.Value = aValue.Text;
            a.Duration = aDuration.Text.Trim();
            a.SelectedName = aSelName.Text.Trim();
            a.Mix = aMix.Text.Trim();
            a.Extra = aExtra.Text.Trim();
            a.Method = Ui.Get(aMethod);
            a.Url = aUrl.Text.Trim();
            a.ContentType = aCType.Text.Trim();
            a.Body = aBody.Text;
            a.User = aUser.Text.Trim();
            a.AllowPlainLogin = aPlain.Checked;
            if (a.User.Length == 0) a.Password = "";
            else if (aPassword.Text != GpioPage.Unchanged) a.Password = aPassword.Text.Length > 0 ? "plain:" + aPassword.Text : "";
            a.DelayMs = (int)aDelay.Value;
            list.RefreshSelected();
            Raise();
        }

        void Raise()
        {
            EventHandler h = Changed;
            if (h != null) h(this, EventArgs.Empty);
        }
    }

    public class GpioPage : SectionPage
    {
        GpioCfg cfg = new GpioCfg();
        readonly Dictionary<string, List<VmixInput>> inputs = new Dictionary<string, List<VmixInput>>();
        readonly HashSet<string> inputsAsked = new HashSet<string>();

        readonly CheckBox gOn = Ui.Chk("Listen to the Axia devices and run the triggers");
        readonly NumericUpDown gAlert = Ui.Num(10, 86400, 10, 0);
        readonly Label gState = new Label();
        readonly TabControl tabs = new TabControl();

        // triggers
        readonly ListEditor<GpioTrigger> triggers = new ListEditor<GpioTrigger>("trigger");
        readonly TabControl ttabs = new TabControl();
        readonly CheckBox tOn = Ui.Chk("Trigger switched on");
        readonly TextBox tName = Ui.Text(260);
        readonly ComboBox tDevice = new ComboBox();
        readonly ComboBox tSignal;
        readonly NumericUpDown tPort = Ui.Num(0, 256, 1, 0);
        readonly NumericUpDown tPin = Ui.Num(1, 5, 1, 0);
        readonly CheckBox tInvert = Ui.Chk("Inverted: \"on\" is when the pin is high (open)");
        readonly TextBox tCommand = Ui.Text(300);
        readonly NumericUpDown tRepeat = Ui.Num(0, 600000, 50, 0);
        readonly TextBox tNotes = Ui.Text(420);
        readonly Button learnBtn;
        readonly Label learnText = new Label();
        readonly Label tLive = new Label();
        readonly ActionEditor onEd = new ActionEditor("Run these now");
        readonly ActionEditor offEd = new ActionEditor("Run these now");
        long learnAfter = -1;
        double learnUntil, learnFrom;

        // devices
        readonly ListEditor<AxiaDevice> devices = new ListEditor<AxiaDevice>("device");
        readonly CheckBox dOn = Ui.Chk("Connect to this device");
        readonly TextBox dName = Ui.Text(260);
        readonly TextBox dHost = Ui.Text(200);
        readonly NumericUpDown dPort = Ui.Num(1, 65535, 1, 0);
        readonly CheckBox dLogin = Ui.Chk("Send LOGIN after connecting");
        readonly TextBox dPassword = Ui.Text(200);
        readonly TextBox dNotes = Ui.Text(420);
        readonly Button dProbe;
        readonly Label dState = new Label();

        // vMix
        readonly ListEditor<VmixTarget> vmix = new ListEditor<VmixTarget>("vMix");
        readonly TextBox vName = Ui.Text(260);
        readonly TextBox vHost = Ui.Text(200);
        readonly NumericUpDown vPort = Ui.Num(1, 65535, 1, 0);
        readonly TextBox vUser = Ui.Text(200);
        readonly TextBox vPassword = Ui.Text(200);
        readonly CheckBox vPlain = Ui.Chk("Send the login unencrypted (vMix on another computer: readable on the network)");
        readonly NumericUpDown vTimeout = Ui.Num(200, 60000, 100, 0);
        readonly Button vTest;
        readonly Label vResult = new Label();

        // live
        readonly ListView pins = new ListView();
        readonly ListView events = new ListView();
        readonly ComboBox sDevice = new ComboBox();
        readonly ComboBox sSignal;
        readonly NumericUpDown sPort = Ui.Num(1, 256, 1, 0);
        readonly NumericUpDown sPin = Ui.Num(1, 5, 1, 0);
        readonly Label sResult = new Label();
        string pinsSig = "", eventsSig = "";

        public const string Unchanged = "••••••••";
        int fill;

        public GpioPage(Manager m)
            : base(m, "gpio", "GPIO (Axia)",
                   "Turns Axia GPIO into actions: a closed contact, a console button or a fader start on an Axia GPIO node, xNode, engine or IP-Audio driver " +
                   "becomes a vMix function (cut, overlay, recording...) or any HTTP request. The service listens over Livewire (LWRP, TCP port 93).")
        {
            tSignal = Ui.Combo(new Choice("gpi", "GPI (input pins: switches, buttons wired to the node)"), new Choice("gpo", "GPO (output pins: what the console sends, e.g. fader on)"),
                               new Choice("cmd", "Command message (GPO CMD:\"text\")"));
            sSignal = Ui.Combo(new Choice("gpi", "GPI"), new Choice("gpo", "GPO"));
            tDevice.DropDownStyle = ComboBoxStyle.DropDownList;
            tDevice.Width = 300;
            tDevice.Anchor = AnchorStyles.Left;
            sDevice.DropDownStyle = ComboBoxStyle.DropDownList;
            sDevice.Width = 220;
            sDevice.Anchor = AnchorStyles.Left;
            dPassword.UseSystemPasswordChar = true;
            vPassword.UseSystemPasswordChar = true;
            learnBtn = Ui.Btn("Learn from the device", delegate { StartLearn(); });
            dProbe = Ui.Btn("Test connection", delegate { Probe(); });
            vTest = Ui.Btn("Test and load inputs", delegate { TestVmix(); });
            foreach (Control c in new Control[] { learnBtn, dProbe, vTest, gState, learnText, tLive, dState, vResult, sResult }) c.Tag = "nodirty";

            // ---- top
            gState.AutoSize = true;
            gState.MaximumSize = new Size(900, 0);
            gState.Margin = new Padding(0, 4, 0, 0);
            Label secs = Ui.Lbl("seconds");
            FormGrid top = new FormGrid();
            top.Row(null, gOn);
            top.Row("Alert when a device is unreachable for", Ui.Flow(gAlert, secs));
            top.Row(null, gState);
            top.Dock = DockStyle.Top;

            tabs.Dock = DockStyle.Fill;
            tabs.TabPages.Add(Tab("Triggers", BuildTriggers()));
            tabs.TabPages.Add(Tab("Axia devices", BuildDevices()));
            tabs.TabPages.Add(Tab("vMix", BuildVmix()));
            tabs.TabPages.Add(Tab("Live", BuildLive()));
            tabs.SelectedIndexChanged += delegate { OnTab(); };

            gOn.CheckedChanged += delegate { if (!Loading && fill == 0) { cfg.Enabled = gOn.Checked; MarkDirty(); } };
            gAlert.ValueChanged += delegate { if (!Loading && fill == 0) { cfg.DisconnectAlertSeconds = (int)gAlert.Value; MarkDirty(); } };

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

        // ============================================================== triggers
        Control BuildTriggers()
        {
            triggers.EmptyText = "No triggers yet.\r\n\r\nAdd the Axia device and vMix first, then click \"Add trigger\".";
            triggers.Create = delegate
            {
                GpioTrigger t = new GpioTrigger();
                t.Name = "New trigger";
                if (cfg.Devices.Count > 0) t.DeviceId = cfg.Devices[0].Id;
                GpioAction a = new GpioAction();
                if (cfg.Vmix.Count > 0) a.VmixId = cfg.Vmix[0].Id;
                t.OnActive.Add(a);
                return t;
            };
            triggers.Copy = delegate(GpioTrigger t)
            {
                GpioTrigger c = Json.Clone(t);
                c.Id = "";
                c.Name = t.Name + " (copy)";
                return c;
            };
            triggers.Title = delegate(GpioTrigger t) { return t.Name; };
            triggers.Subtitle = delegate(GpioTrigger t) { return Describe(t); };
            triggers.IsOn = delegate(GpioTrigger t) { return t.Enabled; };
            triggers.SelectedChanged += delegate { FillTrigger(); };
            triggers.Changed += delegate { MarkDirty(); };

            FormGrid g = new FormGrid();
            g.Row(null, tOn);
            g.Row("Name", tName);
            g.Row("Axia device", tDevice);
            g.Row("Listen to", tSignal);
            Label pinL = Ui.Lbl("pin");
            pinL.Margin = new Padding(12, 6, 4, 0);
            g.Row("Port", Ui.Flow(tPort, pinL, tPin), "GPIO port number (1-8 on a GPIO node, as set up in the console/PathfinderCore) and pin 1-5. Command messages: port 0 = any.");
            g.Row(null, tInvert);
            g.Row("Command text", tCommand, "For command messages: the text after CMD:, e.g. Start Rec.");
            Label ms = Ui.Lbl("ms (0 = never; guards against contact bounce)");
            g.Row("Ignore repeats within", Ui.Flow(tRepeat, ms));
            learnText.AutoSize = true;
            learnText.MaximumSize = new Size(480, 0);
            learnText.Margin = new Padding(8, 7, 0, 0);
            g.Row("Easiest", Ui.Flow(learnBtn, learnText));
            tLive.AutoSize = true;
            tLive.Margin = new Padding(0, 6, 0, 0);
            g.Row("Now", tLive);
            g.Row("Notes", tNotes);

            ttabs.Dock = DockStyle.Fill;
            ttabs.TabPages.Add(Tab("Signal", Scroller(g)));
            ttabs.TabPages.Add(Tab("When it turns on", onEd));
            ttabs.TabPages.Add(Tab("When it turns off", offEd));
            triggers.Detail.AutoScroll = false;
            triggers.Detail.Controls.Add(ttabs);

            foreach (ActionEditor ed in new[] { onEd, offEd })
            {
                ActionEditor e = ed;
                e.Targets = delegate { return cfg.Vmix; };
                e.Inputs = delegate(string id) { List<VmixInput> l; return inputs.TryGetValue(id, out l) ? l : null; };
                e.Changed += delegate { if (fill == 0 && !Loading) { MarkDirty(); triggers.RefreshSelected(); } };
                e.RunRequested += delegate { RunNow(e == onEd ? "on" : "off", e); };
            }

            EventHandler apply = delegate { ApplyTrigger(); };
            tOn.CheckedChanged += apply;
            tName.TextChanged += apply;
            tDevice.SelectedIndexChanged += apply;
            tSignal.SelectedIndexChanged += apply;
            tPort.ValueChanged += apply;
            tPin.ValueChanged += apply;
            tInvert.CheckedChanged += apply;
            tCommand.TextChanged += apply;
            tRepeat.ValueChanged += apply;
            tNotes.TextChanged += apply;
            return triggers;
        }

        string DeviceName(string id)
        {
            AxiaDevice d = cfg.Devices.Find(delegate(AxiaDevice x) { return x.Id == id; });
            return d != null ? d.Name : "(no device)";
        }

        string Describe(GpioTrigger t)
        {
            string src = t.Signal == "cmd" ? DeviceName(t.DeviceId) + " command \"" + t.Command + "\""
                       : DeviceName(t.DeviceId) + " " + t.Signal.ToUpperInvariant() + " " + t.Port + " pin " + t.Pin + (t.Invert ? " (inverted)" : "");
            List<string> on = new List<string>();
            foreach (GpioAction a in t.OnActive) on.Add(ActionEditor.Title(a));
            return src + (on.Count > 0 ? "  >  " + string.Join(", ", on.ToArray()) : "");
        }

        void RefreshDeviceCombos()
        {
            fill++;
            try
            {
                Quiet(delegate
                {
                    foreach (ComboBox c in new[] { tDevice, sDevice })
                    {
                        string keep = Ui.Get(c);
                        c.Items.Clear();
                        foreach (AxiaDevice d in cfg.Devices) c.Items.Add(new Choice(d.Id, d.Name + " (" + d.Host + ")"));
                        if (keep.Length > 0) Ui.Set(c, keep);
                        else if (c.Items.Count > 0) c.SelectedIndex = 0;
                    }
                    GpioTrigger t = triggers.Selected;
                    if (t != null) Ui.Set(tDevice, t.DeviceId);
                });
            }
            finally { fill--; }
        }

        void FillTrigger()
        {
            GpioTrigger t = triggers.Selected;
            RefreshDeviceCombos();
            fill++;
            try
            {
                Quiet(delegate
                {
                    GpioTrigger d = t ?? new GpioTrigger();
                    tOn.Checked = d.Enabled;
                    tName.Text = t != null ? d.Name : "";
                    Ui.Set(tDevice, d.DeviceId);
                    Ui.Set(tSignal, d.Signal);
                    Ui.SetNum(tPort, d.Port);
                    Ui.SetNum(tPin, d.Pin);
                    tInvert.Checked = d.Invert;
                    tCommand.Text = d.Command;
                    Ui.SetNum(tRepeat, d.IgnoreRepeatMs);
                    tNotes.Text = d.Notes;
                    onEd.SetItems(t != null ? t.OnActive : new List<GpioAction>());
                    offEd.SetItems(t != null ? t.OnInactive : new List<GpioAction>());
                });
            }
            finally { fill--; }
            learnAfter = -1;
            learnText.Text = "";
            UpdateTriggerUi();
        }

        void UpdateTriggerUi()
        {
            bool cmd = Ui.Get(tSignal) == "cmd";
            tPin.Enabled = tInvert.Enabled = !cmd;
            tCommand.Enabled = cmd;
            ttabs.TabPages[1].Text = cmd ? "When the command arrives" : "When it turns on";
            ttabs.TabPages[2].Text = cmd ? "(not used for commands)" : "When it turns off";
        }

        void ApplyTrigger()
        {
            UpdateTriggerUi();
            if (fill > 0 || Loading) return;
            GpioTrigger t = triggers.Selected;
            if (t == null) return;
            t.Enabled = tOn.Checked;
            t.Name = tName.Text;
            t.DeviceId = Ui.Get(tDevice);
            t.Signal = Ui.Get(tSignal);
            t.Port = (int)tPort.Value;
            t.Pin = (int)tPin.Value;
            t.Invert = tInvert.Checked;
            t.Command = tCommand.Text.Trim();
            t.IgnoreRepeatMs = (int)tRepeat.Value;
            t.Notes = tNotes.Text;
            triggers.RefreshSelected();
            MarkDirty();
        }

        void StartLearn()
        {
            if (!M.Online) { learnText.ForeColor = Ui.Bad; learnText.Text = "The service is not running."; return; }
            EngineStatus s = M.Status;
            if (s == null || !s.Gpio.Enabled || !s.Gpio.Devices.Exists(delegate(GpioDeviceStatus d) { return d.State == "connected"; }))
            {
                learnText.ForeColor = Ui.Bad;
                learnText.Text = "No Axia device is connected. Save the device and switch GPIO on first (Live tab shows the connection).";
                return;
            }
            learnAfter = s.Gpio.Events.Count > 0 ? s.Gpio.Events[0].Seq : 0;
            learnFrom = Util.Now() - 0.3;
            learnUntil = Util.Now() + 30;
            learnText.ForeColor = Ui.Accent;
            learnText.Text = "Now press the button, close the contact or switch the fader on...";
        }

        void LearnTick()
        {
            if (learnAfter < 0) return;
            if (Util.Now() > learnUntil)
            {
                learnAfter = -1;
                learnText.ForeColor = Ui.Bad;
                learnText.Text = "Nothing changed within 30 seconds.";
                return;
            }
            EngineStatus s = M.Status;
            if (s == null) return;
            GpioEvent hit = null;
            foreach (GpioEvent e in s.Gpio.Events)
                if (e.Seq > learnAfter && e.Time >= learnFrom && (hit == null || e.Seq < hit.Seq)) hit = e;
            if (hit == null) return;
            learnAfter = -1;
            fill++;
            try
            {
                Ui.Set(tDevice, hit.DeviceId);
                Ui.Set(tSignal, hit.Signal);
                if (hit.Signal == "cmd") { Ui.SetNum(tPort, hit.Port); tCommand.Text = hit.Text; }
                else { Ui.SetNum(tPort, hit.Port); Ui.SetNum(tPin, hit.Pin); tInvert.Checked = hit.Edge == "off"; }
            }
            finally { fill--; }
            ApplyTrigger();
            learnText.ForeColor = Ui.Ok;
            learnText.Text = "Learned: " + GpioService.Describe(hit) + (hit.Signal != "cmd" && hit.Edge == "off" ? " (it went high, so the trigger is inverted)" : "") + ".";
        }

        void RunNow(string edge, ActionEditor ed)
        {
            GpioTrigger t = triggers.Selected;
            if (t == null) return;
            if (!M.Online) { ed.ShowResult("The service is not running; actions are run by the service.", false); return; }
            ed.ShowResult("Running...", true);
            GpioTrigger snap = Json.Clone(t);
            GpioCfg g = Json.Clone(cfg);
            // saved passwords are not sent: only a reference the service resolves itself
            GpioCfg saved = M.Config.Get().Gpio;
            foreach (VmixTarget v in g.Vmix)
            {
                VmixTarget sv = saved.Vmix.Find(delegate(VmixTarget x) { return x.Id == v.Id; });
                v.Password = Secrets.KeepIfSaved(v.Password, sv != null ? sv.Password : null, v.Id);
            }
            foreach (AxiaDevice d in g.Devices) d.Password = "";
            GpioTrigger st = saved.Triggers.Find(delegate(GpioTrigger x) { return x.Id == snap.Id; });
            for (int i = 0; i < snap.OnActive.Count; i++)
                snap.OnActive[i].Password = Secrets.KeepIfSaved(snap.OnActive[i].Password, st != null && i < st.OnActive.Count ? st.OnActive[i].Password : null, snap.Id + "/on/" + i);
            for (int i = 0; i < snap.OnInactive.Count; i++)
                snap.OnInactive[i].Password = Secrets.KeepIfSaved(snap.OnInactive[i].Password, st != null && i < st.OnInactive.Count ? st.OnInactive[i].Password : null, snap.Id + "/off/" + i);
            M.Async(this, delegate
            {
                JObject req = new JObject();
                req.Set("cmd", "gpio.run");
                req.Set("trigger", Json.ToJson(snap));
                req.Set("gpio", Json.ToJson(g));
                req.Set("edge", edge);
                return M.Client.Call(req, 3000);
            }, delegate(JObject r, Exception ex)
            {
                if (ex != null) { ed.ShowResult(ex.Message, false); return; }
                List<object> raw = r.Get("results") as List<object>;
                List<ActionResult> res = new List<ActionResult>();
                if (raw != null) foreach (object o in raw) res.Add((ActionResult)Json.ConvertTo(typeof(ActionResult), o));
                if (res.Count == 0) { ed.ShowResult("No actions to run.", true); return; }
                List<string> lines = new List<string>();
                bool ok = true;
                foreach (ActionResult a in res) { lines.Add((a.Ok ? "OK: " : "FAILED: ") + a.Text); ok &= a.Ok; }
                ed.ShowResult(string.Join("\n", lines.ToArray()), ok);
            });
        }

        // =============================================================== devices
        Control BuildDevices()
        {
            devices.EmptyText = "No Axia devices yet.\r\n\r\nClick \"Add device\" and enter its IP address.";
            devices.RemoveNote = "Triggers that listen to it stop working.";
            devices.Create = delegate
            {
                AxiaDevice d = new AxiaDevice();
                d.Name = "Axia GPIO node";
                return d;
            };
            devices.Copy = delegate(AxiaDevice d)
            {
                AxiaDevice c = Json.Clone(d);
                c.Id = "";
                c.Name = d.Name + " (copy)";
                return c;
            };
            devices.Title = delegate(AxiaDevice d) { return d.Name; };
            devices.Subtitle = delegate(AxiaDevice d) { return d.Host.Length == 0 ? "No address yet" : d.Host + ":" + d.Port; };
            devices.Badge = delegate(AxiaDevice d)
            {
                GpioDeviceStatus s = DeviceStatus(d.Id);
                return s == null ? "" : s.State == "connected" ? "connected" : s.State == "error" ? "unreachable" : "";
            };
            devices.IsOn = delegate(AxiaDevice d) { return d.Enabled; };
            devices.SelectedChanged += delegate { FillDevice(); };
            devices.Changed += delegate { EnsureIds(); MarkDirty(); RefreshDeviceCombos(); };

            FormGrid g = new FormGrid();
            g.Row(null, dOn);
            g.Row("Name", dName);
            Label portL = Ui.Lbl("port");
            portL.Margin = new Padding(12, 6, 4, 0);
            g.Row("IP address", Ui.Flow(dHost, portL, dPort), "The GPIO node, xNode, engine (QOR, Fusion, iQ) or PC with the IP-Audio driver. LWRP port: 93.");
            g.Row(null, dLogin);
            g.Row("Password", dPassword, "LWRP password, if the device has one. Stored encrypted for this computer. Usually not needed just to listen.");
            g.Row("Notes", dNotes);
            dState.AutoSize = true;
            dState.MaximumSize = new Size(520, 0);
            dState.Margin = new Padding(8, 7, 0, 0);
            g.Row(null, Ui.Flow(dProbe, dState));
            g.Note("Test connection connects from the service with the settings shown (saved or not) and lists what the device answers for 3 seconds.");
            devices.Detail.Controls.Add(g);

            EventHandler apply = delegate { ApplyDevice(); };
            dOn.CheckedChanged += apply;
            dName.TextChanged += apply;
            dHost.TextChanged += apply;
            dPort.ValueChanged += apply;
            dLogin.CheckedChanged += apply;
            dPassword.TextChanged += apply;
            dNotes.TextChanged += apply;
            return devices;
        }

        void EnsureIds()
        {
            foreach (AxiaDevice d in cfg.Devices) if (d.Id.Length == 0) d.Id = Cfg.NewId();
            foreach (VmixTarget v in cfg.Vmix) if (v.Id.Length == 0) v.Id = Cfg.NewId();
        }

        void FillDevice()
        {
            AxiaDevice d = devices.Selected;
            fill++;
            try
            {
                Quiet(delegate
                {
                    AxiaDevice x = d ?? new AxiaDevice();
                    dOn.Checked = x.Enabled;
                    dName.Text = d != null ? x.Name : "";
                    dHost.Text = x.Host;
                    Ui.SetNum(dPort, x.Port);
                    dLogin.Checked = x.Login;
                    dPassword.Text = x.Password.Length > 0 ? Unchanged : "";
                    dNotes.Text = x.Notes;
                });
            }
            finally { fill--; }
            dPassword.Enabled = dLogin.Checked;
            dState.Text = "";
        }

        void ApplyDevice()
        {
            dPassword.Enabled = dLogin.Checked;
            if (fill > 0 || Loading) return;
            AxiaDevice d = devices.Selected;
            if (d == null) return;
            d.Enabled = dOn.Checked;
            d.Name = dName.Text;
            d.Host = dHost.Text.Trim();
            d.Port = (int)dPort.Value;
            d.Login = dLogin.Checked;
            if (dPassword.Text != Unchanged) d.Password = dPassword.Text.Length > 0 ? "plain:" + dPassword.Text : "";
            d.Notes = dNotes.Text;
            devices.RefreshSelected();
            RefreshDeviceCombos();
            MarkDirty();
        }

        void Probe()
        {
            AxiaDevice d = devices.Selected;
            if (d == null) return;
            if (!M.Online) { dState.ForeColor = Ui.Bad; dState.Text = "The service is not running; the test is made by the service."; return; }
            dState.ForeColor = SystemColors.GrayText;
            dState.Text = "Connecting and listening for 3 seconds...";
            AxiaDevice snap = Json.Clone(d);
            AxiaDevice saved = M.Config.Get().Gpio.Devices.Find(delegate(AxiaDevice x) { return x.Id == snap.Id; });
            snap.Password = Secrets.KeepIfSaved(snap.Password, saved != null ? saved.Password : null, snap.Id);
            M.Async(this, delegate
            {
                JObject req = new JObject();
                req.Set("cmd", "gpio.probe");
                req.Set("device", Json.ToJson(snap));
                req.Set("listen", 3000.0);
                return M.Client.Call(req, 3000);
            }, delegate(JObject r, Exception ex)
            {
                if (ex != null) { dState.ForeColor = Ui.Bad; dState.Text = ex.Message; return; }
                List<object> raw = r.Get("lines") as List<object>;
                StringBuilder sb = new StringBuilder();
                bool failed = false, heard = false;
                if (raw != null) foreach (object o in raw)
                {
                    string l = o as string ?? "";
                    sb.Append(l).Append('\n');
                    if (l.Contains("FAILED")) failed = true;
                    if (l.Contains("S: ")) heard = true;
                }
                dState.ForeColor = failed || !heard ? Ui.Bad : Ui.Ok;
                dState.Text = failed ? "Cannot connect (details opened)." : heard ? "The device answers." : "Connected, but the device said nothing.";
                ResultDialog.Display(FindForm(), "Axia device test", snap.Name + " (" + snap.Host + ":" + snap.Port + ")", sb.ToString());
            });
        }

        GpioDeviceStatus DeviceStatus(string id)
        {
            EngineStatus s = M.Status;
            if (s == null || s.Gpio == null) return null;
            return s.Gpio.Devices.Find(delegate(GpioDeviceStatus x) { return x.Id == id; });
        }

        // ================================================================== vMix
        Control BuildVmix()
        {
            vmix.EmptyText = "No vMix yet.\r\n\r\nClick \"Add vMix\". For vMix on this server keep 127.0.0.1 and port 8088.";
            vmix.RemoveNote = "Actions that use it stop working.";
            vmix.Create = delegate { return new VmixTarget(); };
            vmix.Copy = delegate(VmixTarget v)
            {
                VmixTarget c = Json.Clone(v);
                c.Id = "";
                c.Name = v.Name + " (copy)";
                return c;
            };
            vmix.Title = delegate(VmixTarget v) { return v.Name; };
            vmix.Subtitle = delegate(VmixTarget v)
            {
                List<VmixInput> l;
                return v.Host + ":" + v.Port + (inputs.TryGetValue(v.Id, out l) ? "  (" + l.Count + " inputs)" : "");
            };
            vmix.SelectedChanged += delegate { FillVmix(); };
            vmix.Changed += delegate { EnsureIds(); MarkDirty(); onEd.RefreshTargets(); offEd.RefreshTargets(); };

            FormGrid g = new FormGrid();
            g.Row("Name", vName);
            Label portL = Ui.Lbl("port");
            portL.Margin = new Padding(12, 6, 4, 0);
            g.Row("Address", Ui.Flow(vHost, portL, vPort), "vMix > Settings > Web Controller must be switched on. 127.0.0.1 = vMix on this server.");
            g.Row("User name", vUser, "Only if the Web Controller has a password.");
            g.Row("Password", vPassword);
            g.Row(null, vPlain, "The vMix Web Controller only offers http. Without this, the login is only sent to vMix on this server.");
            Label ms = Ui.Lbl("ms");
            g.Row("Give up after", Ui.Flow(vTimeout, ms));
            vResult.AutoSize = true;
            vResult.MaximumSize = new Size(520, 0);
            vResult.Margin = new Padding(8, 7, 0, 0);
            g.Row(null, Ui.Flow(vTest, vResult));
            g.Note("The service also watches vMix itself on the Watchdog page (keep it running, health check on http://127.0.0.1:8088/api).");
            vmix.Detail.Controls.Add(g);

            EventHandler apply = delegate { ApplyVmix(); };
            vName.TextChanged += apply;
            vHost.TextChanged += apply;
            vPort.ValueChanged += apply;
            vUser.TextChanged += apply;
            vPassword.TextChanged += apply;
            vPlain.CheckedChanged += apply;
            vTimeout.ValueChanged += apply;
            return vmix;
        }

        void FillVmix()
        {
            VmixTarget v = vmix.Selected;
            fill++;
            try
            {
                Quiet(delegate
                {
                    VmixTarget x = v ?? new VmixTarget();
                    vName.Text = v != null ? x.Name : "";
                    vHost.Text = x.Host;
                    Ui.SetNum(vPort, x.Port);
                    vUser.Text = x.User;
                    vPlain.Checked = x.AllowPlainLogin;
                    vPassword.Text = x.Password.Length > 0 ? Unchanged : "";
                    Ui.SetNum(vTimeout, x.TimeoutMs);
                });
            }
            finally { fill--; }
            vResult.Text = "";
        }

        void ApplyVmix()
        {
            if (fill > 0 || Loading) return;
            VmixTarget v = vmix.Selected;
            if (v == null) return;
            v.Name = vName.Text;
            v.Host = vHost.Text.Trim();
            v.Port = (int)vPort.Value;
            v.User = vUser.Text.Trim();
            v.AllowPlainLogin = vPlain.Checked;
            if (vPassword.Text != Unchanged) v.Password = vPassword.Text.Length > 0 ? "plain:" + vPassword.Text : "";
            v.TimeoutMs = (int)vTimeout.Value;
            vmix.RefreshSelected();
            MarkDirty();
        }

        void TestVmix()
        {
            VmixTarget v = vmix.Selected;
            if (v == null) return;
            if (!M.Online) { vResult.ForeColor = Ui.Bad; vResult.Text = "The service is not running; the test is made by the service."; return; }
            vResult.ForeColor = SystemColors.GrayText;
            vResult.Text = "Asking vMix...";
            LoadInputs(v, true);
        }

        void LoadInputs(VmixTarget v, bool show)
        {
            VmixTarget snap = Json.Clone(v);
            VmixTarget saved = M.Config.Get().Gpio.Vmix.Find(delegate(VmixTarget x) { return x.Id == snap.Id; });
            snap.Password = Secrets.KeepIfSaved(snap.Password, saved != null ? saved.Password : null, snap.Id);
            inputsAsked.Add(v.Id);
            M.Async(this, delegate
            {
                JObject req = new JObject();
                req.Set("cmd", "gpio.vmix");
                req.Set("vmix", Json.ToJson(snap));
                return M.Client.Call(req, 3000);
            }, delegate(JObject r, Exception ex)
            {
                VmixInfo info = ex == null ? (VmixInfo)Json.ConvertTo(typeof(VmixInfo), r.Get("info")) : null;
                if (info != null && info.Ok)
                {
                    inputs[snap.Id] = info.Inputs;
                    onEd.RefreshTargets();
                    offEd.RefreshTargets();
                    vmix.RefreshAll();
                }
                if (!show) return;
                if (info == null) { vResult.ForeColor = Ui.Bad; vResult.Text = ex.Message; return; }
                vResult.ForeColor = info.Ok ? Ui.Ok : Ui.Bad;
                vResult.Text = info.Ok ? "vMix " + info.Version + " " + info.Edition + ": " + info.Inputs.Count + " inputs (now offered in the triggers' Input list). On air: input " +
                                         info.Active + ", preview: " + info.Preview + "."
                                       : info.Error;
            });
        }

        // ================================================================== live
        Control BuildLive()
        {
            pins.View = View.Details;
            pins.FullRowSelect = true;
            pins.HideSelection = false;
            pins.Tag = "nodirty";
            pins.Dock = DockStyle.Fill;
            pins.Font = Ui.MonoFont(Ui.BaseFont());
            pins.Columns.Add("Device", 200);
            pins.Columns.Add("Port", 90);
            pins.Columns.Add("Pins 1 2 3 4 5", 150);
            pins.Columns.Add("Last change", 160);
            events.View = View.Details;
            events.FullRowSelect = true;
            events.HideSelection = false;
            events.Tag = "nodirty";
            events.Dock = DockStyle.Fill;
            events.Columns.Add("Time", 90);
            events.Columns.Add("Change", 280);
            events.Columns.Add("Trigger", 180);
            events.Columns.Add("Result", 380);
            events.DoubleClick += delegate
            {
                if (events.SelectedItems.Count == 0) return;
                GpioEvent e = events.SelectedItems[0].Tag as GpioEvent;
                if (e != null) ResultDialog.Display(FindForm(), "GPIO change", GpioService.Describe(e), "Triggers: " + string.Join(", ", e.Triggers.ToArray()) + "\n\n" + e.Result);
            };

            Button simOn = Ui.Btn("Simulate on", delegate { Simulate(true); });
            Button simOff = Ui.Btn("Simulate off", delegate { Simulate(false); });
            simOn.Tag = simOff.Tag = "nodirty";
            Label portL = Ui.Lbl("port");
            Label pinL = Ui.Lbl("pin");
            sResult.AutoSize = true;
            sResult.Margin = new Padding(8, 7, 0, 0);
            FlowLayoutPanel sim = Ui.Flow(Ui.Lbl("Without hardware:"), sDevice, sSignal, portL, sPort, pinL, sPin, simOn, simOff, sResult);
            sim.Dock = DockStyle.Bottom;
            sim.Tag = "nodirty";

            Label h1 = Ui.Lbl("Pins (● on = low / closed)");
            h1.Dock = DockStyle.Top;
            Label h2 = Ui.Lbl("Recent changes (double-click for details)");
            h2.Dock = DockStyle.Top;
            Panel top = new Panel();
            top.Dock = DockStyle.Top;
            top.Height = 220;
            top.Controls.Add(pins);
            top.Controls.Add(h1);
            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Fill;
            bottom.Controls.Add(events);
            bottom.Controls.Add(h2);
            Panel all = new Panel();
            all.Dock = DockStyle.Fill;
            all.Controls.Add(bottom);
            all.Controls.Add(top);
            all.Controls.Add(sim);
            return all;
        }

        void Simulate(bool on)
        {
            if (!M.Online) { sResult.ForeColor = Ui.Bad; sResult.Text = "The service is not running."; return; }
            if (Dirty) { sResult.ForeColor = Ui.Bad; sResult.Text = "Save first; the service uses the saved triggers."; return; }
            string dev = Ui.Get(sDevice);
            if (dev.Length == 0) { sResult.ForeColor = Ui.Bad; sResult.Text = "Add an Axia device first."; return; }
            string signal = Ui.Get(sSignal);
            int port = (int)sPort.Value, pin = (int)sPin.Value;
            M.Async(this, delegate
            {
                JObject req = new JObject();
                req.Set("cmd", "gpio.simulate");
                req.Set("device", dev);
                req.Set("signal", signal);
                req.Set("port", (double)port);
                req.Set("pin", (double)pin);
                req.Set("on", on);
                return M.Client.Call(req, 3000);
            }, delegate(JObject r, Exception ex)
            {
                if (ex != null) { sResult.ForeColor = Ui.Bad; sResult.Text = ex.Message; return; }
                GpioEvent e = r.Get("event") is JObject ? (GpioEvent)Json.ConvertTo(typeof(GpioEvent), r.Get("event")) : null;
                sResult.ForeColor = Ui.Ok;
                sResult.Text = e == null ? "No change (the pin already had that state)." : e.Triggers.Count == 0 ? "No trigger for that pin." : "Ran: " + string.Join(", ", e.Triggers.ToArray());
            });
        }

        void LiveTick()
        {
            EngineStatus s = M.Status;
            GpioStatus g = s != null && s.Gpio != null ? s.Gpio : new GpioStatus();
            StringBuilder sig = new StringBuilder();
            foreach (GpioDeviceStatus d in g.Devices)
            {
                sig.Append(d.Id).Append(d.State);
                foreach (GpioPortState p in d.Ports) sig.Append(p.Signal).Append(p.Port).Append(p.Pins).Append(p.Changed);
            }
            if (sig.ToString() != pinsSig)
            {
                pinsSig = sig.ToString();
                pins.BeginUpdate();
                pins.Items.Clear();
                foreach (GpioDeviceStatus d in g.Devices)
                {
                    if (d.Ports.Count == 0)
                    {
                        ListViewItem it = new ListViewItem(d.Name);
                        it.SubItems.Add("");
                        it.SubItems.Add("");
                        it.SubItems.Add(d.State == "connected" ? "connected, no ports reported yet" : d.Text);
                        it.ForeColor = d.State == "connected" ? SystemColors.GrayText : Ui.Bad;
                        pins.Items.Add(it);
                        continue;
                    }
                    foreach (GpioPortState p in d.Ports)
                    {
                        ListViewItem it = new ListViewItem(d.Name);
                        it.SubItems.Add(p.Signal.ToUpperInvariant() + " " + p.Port);
                        StringBuilder dots = new StringBuilder();
                        foreach (char c in p.Pins) dots.Append(c == '1' ? "● " : c == '0' ? "○ " : "? ");
                        it.SubItems.Add(dots.ToString().TrimEnd());
                        it.SubItems.Add(p.Changed > 0 ? Ui.When(p.Changed) : "");
                        if (p.Pins.Contains("1")) it.ForeColor = Ui.Accent;
                        pins.Items.Add(it);
                    }
                }
                pins.EndUpdate();
            }
            string esig = g.Events.Count > 0 ? g.Events[0].Seq + "|" + g.Events[0].Result + "|" + g.Events.Count : "";
            if (esig != eventsSig)
            {
                eventsSig = esig;
                events.BeginUpdate();
                events.Items.Clear();
                foreach (GpioEvent e in g.Events)
                {
                    ListViewItem it = new ListViewItem(Util.FromEpoch(e.Time).ToString("HH:mm:ss", Util.Inv));
                    it.SubItems.Add(GpioService.Describe(e) + (e.Simulated ? " (simulated)" : ""));
                    it.SubItems.Add(string.Join(", ", e.Triggers.ToArray()));
                    it.SubItems.Add(e.Result);
                    it.Tag = e;
                    if (e.Result.Contains("failed")) it.ForeColor = Ui.Bad;
                    else if (e.Triggers.Count == 0) it.ForeColor = SystemColors.GrayText;
                    events.Items.Add(it);
                }
                events.EndUpdate();
            }
        }

        // ============================================================ page plumbing
        void OnTab()
        {
            if (tabs.SelectedIndex == 0)
            {
                RefreshDeviceCombos();
                onEd.RefreshTargets();
                offEd.RefreshTargets();
                if (M.Online) foreach (VmixTarget v in cfg.Vmix) if (!inputsAsked.Contains(v.Id) && v.Id.Length > 0) LoadInputs(v, false);
            }
            if (tabs.SelectedIndex == 3) { RefreshDeviceCombos(); LiveTick(); }
        }

        void UpdateState()
        {
            EngineStatus s = M.Status;
            if (s == null) { gState.ForeColor = SystemColors.GrayText; gState.Text = "The service is not running."; return; }
            if (!s.Gpio.Enabled)
            {
                gState.ForeColor = SystemColors.GrayText;
                gState.Text = cfg.Enabled ? "Saved settings: GPIO is switched off. Save to apply." : "Switched off: nothing is listened to.";
                return;
            }
            int up = 0, total = s.Gpio.Devices.FindAll(delegate(GpioDeviceStatus d) { return d.State != "off"; }).Count;
            List<string> down = new List<string>();
            foreach (GpioDeviceStatus d in s.Gpio.Devices)
            {
                if (d.State == "connected") up++;
                else if (d.State != "off") down.Add(d.Name + ": " + d.Text);
            }
            gState.ForeColor = down.Count > 0 ? Ui.Bad : up > 0 ? Ui.Ok : SystemColors.GrayText;
            gState.Text = total == 0 ? "On, but no device is set up." : up + " of " + total + " device(s) connected" + (down.Count > 0 ? ". " + string.Join("; ", down.ToArray()) : ".");
        }

        public override void Tick()
        {
            UpdateState();
            LearnTick();
            devices.RefreshAll();
            GpioTrigger t = triggers.Selected;
            if (t != null)
            {
                GpioDeviceStatus d = DeviceStatus(t.DeviceId);
                GpioPortState p = d == null || t.Signal == "cmd" ? null : d.Ports.Find(delegate(GpioPortState x) { return x.Signal == t.Signal && x.Port == t.Port; });
                if (d == null) { tLive.ForeColor = SystemColors.GrayText; tLive.Text = "Device not connected (save, and switch GPIO on)."; }
                else if (t.Signal == "cmd") { tLive.ForeColor = SystemColors.GrayText; tLive.Text = d.State == "connected" ? "Device connected." : d.Text; }
                else if (p == null || t.Pin < 1 || t.Pin > p.Pins.Length) { tLive.ForeColor = SystemColors.GrayText; tLive.Text = d.State == "connected" ? "This port has not been reported yet." : d.Text; }
                else
                {
                    bool low = p.Pins[t.Pin - 1] == '1';
                    bool on = low != t.Invert;
                    tLive.ForeColor = on ? Ui.Accent : SystemColors.ControlText;
                    tLive.Text = "Pin is " + (low ? "low (closed)" : "high (open)") + ": the trigger is " + (on ? "ON" : "off") + ".";
                }
            }
            if (tabs.SelectedIndex == 3) LiveTick();
        }

        public override void OnShown() { OnTab(); }

        protected override void LoadFrom(AppConfig c)
        {
            cfg = Json.Clone(c.Gpio);
            fill++;
            try
            {
                gOn.Checked = cfg.Enabled;
                Ui.SetNum(gAlert, cfg.DisconnectAlertSeconds);
            }
            finally { fill--; }
            devices.SetItems(cfg.Devices);
            vmix.SetItems(cfg.Vmix);
            RefreshDeviceCombos();
            onEd.RefreshTargets();
            offEd.RefreshTargets();
            triggers.SetItems(cfg.Triggers);
        }

        protected override void Collect(AppConfig c)
        {
            EnsureIds();
            cfg.Enabled = gOn.Checked;
            cfg.DisconnectAlertSeconds = (int)gAlert.Value;
            c.Gpio = Json.Clone(cfg);
        }
    }
}
