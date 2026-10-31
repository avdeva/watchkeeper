// GPIO page: Axia devices, vMix, and the triggers that connect them.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Watchkeeper.UI
{
    public class GpioPage : Page
    {
        public readonly TabControl Tabs = new TabControl();
        readonly ListEditor<GpioTrigger> triggers = new ListEditor<GpioTrigger>("Add a trigger: what happens when a pin turns on or off.");
        GpioTrigger cur = new GpioTrigger();
        readonly DataGridView devices = new DataGridView(), vmix = new DataGridView();
        readonly ListBox onList = new ListBox(), offList = new ListBox();
        readonly ComboBox deviceBox = new ComboBox();
        readonly ListView ports = new ListView(), events = new ListView();
        bool filling;
        public override string Title { get { return "GPIO"; } }
        GpioSettings G { get { return M.Edit.Gpio; } }

        public GpioPage(Manager m) : base(m)
        {
            Tabs.Dock = DockStyle.Fill;
            Controls.Add(Tabs);
            Heading("GPIO (Axia)", "Watchkeeper listens to Axia GPIO devices on the network and runs vMix functions or web requests when a pin turns on or off " +
                                   "(a microphone goes live, a button is pressed). Pins are on when the contact is closed (low).");
            TabPage tTrig = new TabPage("Triggers"), tDev = new TabPage("Devices and vMix"), tLive = new TabPage("Live");
            foreach (TabPage t in new[] { tTrig, tDev, tLive }) { t.BackColor = Ui.Back; t.Padding = new Padding(8); Tabs.TabPages.Add(t); }

            // ---- triggers
            CheckBox on = B.Check("Use GPIO (connect to the devices and run the triggers)", delegate { return G.Enabled; }, delegate(bool v) { G.Enabled = v; });
            on.Dock = DockStyle.Top;
            triggers.Title = delegate(GpioTrigger t) { return t.Name; };
            triggers.Subtitle = delegate(GpioTrigger t) { AxiaDevice dv = G.Devices.Find(delegate(AxiaDevice x) { return x.Id == t.DeviceId; }); return (dv == null ? "no device" : dv.Name) + " " + t.Signal.ToUpperInvariant() + " " + t.Port + " pin " + t.Pin; };
            triggers.Mark = delegate(GpioTrigger t) { return t.Enabled && G.Enabled ? Ui.Good : Ui.Off; };
            triggers.Create = delegate { return new GpioTrigger { Id = Util.NewId(), Name = "New trigger", DeviceId = G.Devices.Count > 0 ? G.Devices[0].Id : "" }; };
            triggers.Copy = delegate(GpioTrigger t) { GpioTrigger c = new GpioTrigger(); Json.FillFrom(c, Json.ToJson(t)); c.Id = Util.NewId(); c.Name = t.Name + " (copy)"; return c; };
            triggers.Selected += delegate(GpioTrigger t) { cur = t ?? new GpioTrigger(); FillDevices(); B.Load(); FillActions(); };
            triggers.ListChanged += delegate { M.MarkChanged(); };
            tTrig.Controls.Add(triggers);
            tTrig.Controls.Add(on);
            FormGrid g = new FormGrid();
            int w = Ui.BaseFont().Height * 26;
            g.Row("", B.Check("Use this trigger", delegate { return cur.Enabled; }, delegate(bool v) { cur.Enabled = v; }));
            g.Row("Name", B.Text(w / 2, delegate { return cur.Name; }, delegate(string v) { cur.Name = v; }));
            deviceBox.DropDownStyle = ComboBoxStyle.DropDownList;
            deviceBox.Width = w / 2;
            deviceBox.SelectedIndexChanged += delegate
            {
                if (B.Loading || filling || deviceBox.SelectedIndex < 0 || deviceBox.SelectedIndex >= G.Devices.Count) return;
                cur.DeviceId = G.Devices[deviceBox.SelectedIndex].Id;
                M.MarkChanged();
                triggers.RefreshTitles();
            };
            g.Row("Device", deviceBox, "Devices are set up on the 'Devices and vMix' tab.");
            g.Row("Signal", Ui.Flow(B.Choice(new[] { new[] { "gpi", "GPI (input)" }, new[] { "gpo", "GPO (output)" } }, delegate { return cur.Signal; }, delegate(string v) { cur.Signal = v; }),
                                    Ui.Label("port"), B.Number(1, 9999, 0, delegate { return cur.Port; }, delegate(double v) { cur.Port = (int)v; }),
                                    Ui.Label("pin"), B.Number(1, 5, 0, delegate { return cur.Pin; }, delegate(double v) { cur.Pin = (int)v; })));
            g.Row("", B.Check("Inverted: on when the pin is high (open)", delegate { return cur.Invert; }, delegate(bool v) { cur.Invert = v; }));
            g.Row("Ignore repeats", Ui.Flow(B.Number(0, 600000, 0, delegate { return cur.IgnoreRepeatMs; }, delegate(double v) { cur.IgnoreRepeatMs = (int)v; }), Ui.Label("ms (the same change again within this time is ignored)")));
            g.Section("When the pin turns on");
            g.Row("", ActionBox(onList, true));
            g.Section("When the pin turns off");
            g.Row("", ActionBox(offList, false));
            g.Row("", Ui.Flow(Ui.Button("Test: run the 'on' actions", delegate { Simulate(true); }), Ui.Button("Test: run the 'off' actions", delegate { Simulate(false); })),
                  "Runs the actions from this window now, as if the pin changed (with the settings as they are here, saved or not).");
            triggers.Detail.Controls.Add(g);

            // ---- devices and vMix
            FormGrid d = new FormGrid();
            SetupGrid(devices, new[] { "On", "Name", "Address", "Port", "Password" });
            devices.CellValueChanged += delegate { if (!filling) { ReadDevices(); M.MarkChanged(); triggers.RefreshTitles(); } };
            d.Section("Axia devices");
            d.Row("", devices, "The device's IP address and LWRP port (93). Password: only if the device asks for one (LWRP LOGIN).");
            d.Row("", Ui.Flow(Ui.Button("Add device", delegate { G.Devices.Add(new AxiaDevice { Id = Util.NewId() }); FillGrids(); M.MarkChanged(); }),
                              Ui.Button("Remove device", delegate { RemoveRow(devices, G.Devices); })));
            SetupGrid(vmix, new[] { "", "Name", "Address", "Port", "Password" });
            vmix.Columns[0].Visible = false;
            vmix.Columns.Insert(4, new DataGridViewTextBoxColumn { HeaderText = "User", FillWeight = 20 });
            vmix.CellValueChanged += delegate { if (!filling) { ReadVmix(); M.MarkChanged(); } };
            d.Section("vMix");
            d.Row("", vmix, "vMix with Settings > Web Controller switched on (port 8088). User and password only if set there.");
            d.Row("", Ui.Flow(Ui.Button("Add vMix", delegate { G.Vmix.Add(new VmixTarget { Id = Util.NewId() }); FillGrids(); M.MarkChanged(); }),
                              Ui.Button("Remove vMix", delegate { RemoveRow(vmix, G.Vmix); })));
            d.Row("Alert after", Ui.Flow(B.Number(5, 86400, 0, delegate { return G.DisconnectAlertSeconds; }, delegate(double v) { G.DisconnectAlertSeconds = (int)v; }),
                                         Ui.Label("seconds a device cannot be reached")));
            Panel dscroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            dscroll.Controls.Add(d);
            tDev.Controls.Add(dscroll);

            // ---- live
            int em = Ui.BaseFont().Height;
            ports.View = events.View = View.Details;
            ports.Dock = DockStyle.Top;
            ports.Height = em * 12;
            ports.Columns.Add("Device", em * 12);
            ports.Columns.Add("State", em * 8);
            ports.Columns.Add("Ports (1 = on, 0 = off, per pin 1-5)", em * 40);
            events.Dock = DockStyle.Fill;
            events.Columns.Add("Time", em * 6);
            events.Columns.Add("Device", em * 10);
            events.Columns.Add("Pin", em * 9);
            events.Columns.Add("Change", em * 8);
            events.Columns.Add("Triggers", em * 26);
            tLive.Controls.Add(events);
            tLive.Controls.Add(ports);
            B.Add(FillGrids);
        }

        static void SetupGrid(DataGridView grid, string[] cols)
        {
            grid.AllowUserToAddRows = false;
            grid.RowHeadersVisible = false;
            grid.BackgroundColor = Ui.Back;
            grid.Width = Ui.BaseFont().Height * 44;
            grid.Height = Ui.BaseFont().Height * 8;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = cols[0], FillWeight = 8 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = cols[1], FillWeight = 25 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = cols[2], FillWeight = 25 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = cols[3], FillWeight = 10 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = cols[4], FillWeight = 18 });
            grid.CurrentCellDirtyStateChanged += delegate { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            grid.CellFormatting += delegate(object s, DataGridViewCellFormattingEventArgs e)
            {
                if (grid.Columns[e.ColumnIndex].HeaderText == "Password" && e.Value is string && ((string)e.Value).Length > 0) { e.Value = Binder.Hidden; e.FormattingApplied = true; }
            };
        }

        public DataGridView DevicesGrid { get { return devices; } }
        public DataGridView VmixGrid { get { return vmix; } }

        static string Cell(DataGridViewRow r, int i) { return (Convert.ToString(r.Cells[i].Value) ?? "").Trim(); }
        static int Port(DataGridViewRow r, int i, int def) { int p; return int.TryParse(Cell(r, i), out p) ? p : def; }

        void FillGrids()
        {
            filling = true;
            devices.Rows.Clear();
            foreach (AxiaDevice x in G.Devices) devices.Rows.Add(x.Enabled, x.Name, x.Host, x.Port.ToString(Util.Inv), x.Password);
            vmix.Rows.Clear();
            foreach (VmixTarget x in G.Vmix) vmix.Rows.Add(true, x.Name, x.Host, x.Port.ToString(Util.Inv), x.User, x.Password);
            filling = false;
            FillDevices();
        }

        void ReadDevices()
        {
            for (int i = 0; i < devices.Rows.Count && i < G.Devices.Count; i++)
            {
                DataGridViewRow r = devices.Rows[i];
                AxiaDevice x = G.Devices[i];
                x.Enabled = r.Cells[0].Value is bool && (bool)r.Cells[0].Value;
                x.Name = Cell(r, 1);
                x.Host = Cell(r, 2);
                x.Port = Port(r, 3, 93);
                string pw = Convert.ToString(r.Cells[4].Value) ?? "";
                if (pw != Binder.Hidden) x.Password = pw;
            }
            FillDevices();
        }

        void ReadVmix()
        {
            for (int i = 0; i < vmix.Rows.Count && i < G.Vmix.Count; i++)
            {
                DataGridViewRow r = vmix.Rows[i];
                VmixTarget x = G.Vmix[i];
                x.Name = Cell(r, 1);
                x.Host = Cell(r, 2);
                x.Port = Port(r, 3, 8088);
                x.User = Cell(r, 4);
                string pw = Convert.ToString(r.Cells[5].Value) ?? "";
                if (pw != Binder.Hidden) x.Password = pw;
            }
        }

        void RemoveRow<T>(DataGridView grid, List<T> items)
        {
            if (grid.CurrentRow == null || grid.CurrentRow.Index >= items.Count) return;
            items.RemoveAt(grid.CurrentRow.Index);
            FillGrids();
            M.MarkChanged();
        }

        void FillDevices()
        {
            filling = true;
            deviceBox.Items.Clear();
            foreach (AxiaDevice x in G.Devices) deviceBox.Items.Add(x.Name.Length > 0 ? x.Name : x.Host);
            deviceBox.SelectedIndex = G.Devices.FindIndex(delegate(AxiaDevice x) { return x.Id == cur.DeviceId; });
            filling = false;
        }

        Control ActionBox(ListBox box, bool on)
        {
            box.Width = Ui.BaseFont().Height * 34;
            box.Height = Ui.BaseFont().Height * 5;
            box.IntegralHeight = false;
            box.DoubleClick += delegate { EditAction(on, box.SelectedIndex); };
            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.AutoSize = true;
            buttons.FlowDirection = FlowDirection.TopDown;
            buttons.Controls.Add(Ui.Button("Add vMix function...", delegate { AddAction(on, "vmix"); }));
            buttons.Controls.Add(Ui.Button("Add web request...", delegate { AddAction(on, "http"); }));
            buttons.Controls.Add(Ui.Button("Edit...", delegate { EditAction(on, box.SelectedIndex); }));
            buttons.Controls.Add(Ui.Button("Remove", delegate
            {
                List<GpioAction> l = on ? cur.WhenOn : cur.WhenOff;
                if (box.SelectedIndex >= 0 && box.SelectedIndex < l.Count) { l.RemoveAt(box.SelectedIndex); FillActions(); M.MarkChanged(); }
            }));
            return Ui.Flow(box, buttons);
        }

        void FillActions()
        {
            onList.Items.Clear();
            foreach (GpioAction a in cur.WhenOn) onList.Items.Add((a.DelayMs > 0 ? "after " + a.DelayMs + " ms: " : "") + Gpio.Describe(G, a));
            offList.Items.Clear();
            foreach (GpioAction a in cur.WhenOff) offList.Items.Add((a.DelayMs > 0 ? "after " + a.DelayMs + " ms: " : "") + Gpio.Describe(G, a));
        }

        void AddAction(bool on, string type)
        {
            GpioAction a = new GpioAction { Type = type, Function = type == "vmix" ? "Cut" : "", Method = type == "http" ? "POST" : "GET" };
            if (!ActionDialog.Edit(FindForm(), G, a)) return;
            (on ? cur.WhenOn : cur.WhenOff).Add(a);
            FillActions();
            M.MarkChanged();
        }

        void EditAction(bool on, int index)
        {
            List<GpioAction> l = on ? cur.WhenOn : cur.WhenOff;
            if (index < 0 || index >= l.Count) return;
            GpioAction copy = new GpioAction();
            Json.FillFrom(copy, Json.ToJson(l[index]));
            if (!ActionDialog.Edit(FindForm(), G, copy)) return;
            l[index] = copy;
            FillActions();
            M.MarkChanged();
        }

        void Simulate(bool on)
        {
            Cursor = Cursors.WaitCursor;
            List<string> lines;
            try { lines = Gpio.Simulate(G, cur, on); }
            finally { Cursor = Cursors.Default; }
            Ui.ShowText(FindForm(), "Test: " + cur.Name, string.Join("\n", lines.ToArray()));
        }

        protected override void OnFieldChanged() { triggers.RefreshTitles(); }

        public override void Reload()
        {
            B.Load();
            triggers.SetItems(G.Triggers);
        }

        public override void Live(EngineStatus s)
        {
            ports.BeginUpdate();
            ports.Items.Clear();
            foreach (GpioDeviceStatus d in s.Gpio.Devices)
            {
                List<string> p = new List<string>();
                foreach (GpioPortState x in d.Ports) p.Add(x.Signal.ToUpperInvariant() + " " + x.Port + ": " + x.Pins);
                ListViewItem it = new ListViewItem(d.Name);
                it.SubItems.Add(d.State);
                it.SubItems.Add(d.Text + (p.Count > 0 ? "   " + string.Join("   ", p.ToArray()) : ""));
                it.ForeColor = Ui.StateColor(d.State);
                ports.Items.Add(it);
            }
            ports.EndUpdate();
            events.BeginUpdate();
            events.Items.Clear();
            foreach (GpioEvent e in s.Gpio.Events)
            {
                ListViewItem it = new ListViewItem(Util.FromEpoch(e.Time).ToString("HH:mm:ss", Util.Inv));
                it.SubItems.Add(e.Device);
                it.SubItems.Add(e.Signal.ToUpperInvariant() + " " + e.Port + " pin " + e.Pin);
                it.SubItems.Add(e.Edge);
                it.SubItems.Add(e.Result);
                events.Items.Add(it);
            }
            events.EndUpdate();
        }

        public override Color Dot(EngineStatus s)
        {
            if (!s.Gpio.Enabled) return Ui.Off;
            if (s.Gpio.Devices.Exists(delegate(GpioDeviceStatus d) { return d.State == "error"; })) return Ui.Bad;
            if (s.Gpio.Devices.Exists(delegate(GpioDeviceStatus d) { return d.State == "connecting"; })) return Ui.Warn;
            return Ui.Good;
        }
    }

    /// <summary>Editing one GPIO action: a vMix function or a web request.</summary>
    public static class ActionDialog
    {
        static readonly string[] Functions = { "Cut", "Fade", "Merge", "PreviewInput", "ActiveInput", "OverlayInput1In", "OverlayInput1Out", "OverlayInput2In", "OverlayInput2Out",
            "OverlayInput3In", "OverlayInput3Out", "OverlayInput4In", "OverlayInput4Out", "FadeToBlack", "AudioOn", "AudioOff", "SetVolume", "Play", "Pause",
            "StartRecording", "StopRecording", "StartStreaming", "StopStreaming", "StartMultiCorder", "StopMultiCorder", "SetText", "ScriptStart", "ScriptStop" };

        public static bool Edit(IWin32Window owner, GpioSettings g, GpioAction a)
        {
            using (Form f = new Form())
            {
                f.Text = a.Type == "vmix" ? "vMix function" : "Web request";
                f.Font = Ui.BaseFont();
                f.StartPosition = FormStartPosition.CenterParent;
                f.ShowInTaskbar = false;
                f.MinimizeBox = f.MaximizeBox = false;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.AutoSize = true;
                f.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                f.Padding = new Padding(12);
                Binder b = new Binder();
                FormGrid grid = new FormGrid();
                grid.HelpWidth = 420;
                int w = f.Font.Height * 24;
                if (a.Type == "vmix")
                {
                    if (g.Vmix.Count > 1)
                    {
                        List<string[]> targets = new List<string[]>();
                        foreach (VmixTarget t in g.Vmix) targets.Add(new[] { t.Id, t.Name });
                        grid.Row("vMix", b.Choice(targets.ToArray(), delegate { return a.VmixId.Length > 0 ? a.VmixId : g.Vmix[0].Id; }, delegate(string v) { a.VmixId = v; }));
                    }
                    ComboBox fn = new ComboBox();
                    fn.Width = w / 2;
                    fn.Items.AddRange(Functions);
                    b.Add(delegate { fn.Text = a.Function; });
                    fn.TextChanged += delegate { if (!b.Loading) a.Function = fn.Text.Trim(); };
                    grid.Row("Function", fn, "Any vMix shortcut function, e.g. Cut, OverlayInput1In, SetText.");
                    grid.Row("Input", b.Text(w / 2, delegate { return a.Input; }, delegate(string v) { a.Input = v; }), "Number, name or key of the input; empty for functions without one.");
                    grid.Row("Value", b.Text(w / 2, delegate { return a.Value; }, delegate(string v) { a.Value = v; }));
                    grid.Row("More", b.Text(w, delegate { return a.Extra; }, delegate(string v) { a.Extra = v; }), "Other parameters: Name=Value&Name2=Value2 (e.g. Duration=500).");
                }
                else
                {
                    grid.Row("Method", b.Choice(new[] { new[] { "GET", "GET" }, new[] { "POST", "POST" }, new[] { "PUT", "PUT" } }, delegate { return a.Method; }, delegate(string v) { a.Method = v; }));
                    grid.Row("Address", b.Text(w, delegate { return a.Url; }, delegate(string v) { a.Url = v.Trim(); }), "http://... or https://...");
                    TextBox body = b.Text(w, delegate { return a.Body; }, delegate(string v) { a.Body = v; });
                    body.Multiline = true;
                    body.Height = f.Font.Height * 5;
                    grid.Row("Body", body, "Sent with POST and PUT.");
                    grid.Row("Content type", b.Text(w / 2, delegate { return a.ContentType; }, delegate(string v) { a.ContentType = v; }));
                }
                grid.Row("Wait first", Ui.Flow(b.Number(0, 600000, 0, delegate { return a.DelayMs; }, delegate(double v) { a.DelayMs = (int)v; }), Ui.Label("ms")));
                bool ok = false;
                Label result = Ui.HelpText("", 420);
                grid.Row("", Ui.Flow(Ui.Button("Try it now", delegate { string err = Gpio.Run(g, a); result.Text = err == null ? "Done." : err; result.ForeColor = err == null ? Ui.Good : Ui.Bad; }), result));
                Button okb = Ui.Button("OK", delegate { ok = true; f.Close(); });
                Button cancel = Ui.Button("Cancel", delegate { f.Close(); });
                grid.Row("", Ui.Flow(okb, cancel));
                f.Controls.Add(grid);
                f.AcceptButton = okb;
                f.CancelButton = cancel;
                b.Load();
                if (Ui.DialogHook != null) { ok = Ui.DialogHook(f); return ok; }
                f.ShowDialog(owner);
                return ok;
            }
        }
    }
}
