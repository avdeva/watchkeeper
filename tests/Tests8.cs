// Axia GPIO (LWRP) to vMix: parsing, triggers, a fake Axia device over TCP and a fake vMix over HTTP.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Watchkeeper;

namespace Watchkeeper.Tests
{
    public class GpioTests : Env
    {
        List<string> alerts = new List<string>();

        static GpioAction VmixAction(string function, string input)
        {
            GpioAction a = new GpioAction();
            a.Type = "vmix";
            a.Function = function;
            a.Input = input;
            return a;
        }

        GpioCfg Setup(int axiaPort, int vmixPort)
        {
            GpioCfg g = new GpioCfg();
            g.Enabled = true;
            AxiaDevice d = new AxiaDevice();
            d.Id = "node1";
            d.Name = "Studio GPIO node";
            d.Host = "127.0.0.1";
            d.Port = axiaPort;
            g.Devices.Add(d);
            VmixTarget v = new VmixTarget();
            v.Id = "vm";
            v.Port = vmixPort;
            g.Vmix.Add(v);
            GpioTrigger mic = new GpioTrigger();
            mic.Id = "mic";
            mic.Name = "Mic 1 live";
            mic.DeviceId = "node1";
            mic.Signal = "gpi";
            mic.Port = 3;
            mic.Pin = 2;
            mic.OnActive.Add(VmixAction("Cut", "Camera & Guest"));
            GpioAction lower = VmixAction("OverlayInput1In", "3");
            lower.DelayMs = 50;
            mic.OnActive.Add(lower);
            mic.OnInactive.Add(VmixAction("OverlayInput1Out", ""));
            g.Triggers.Add(mic);
            GpioTrigger cmd = new GpioTrigger();
            cmd.Id = "rec";
            cmd.Name = "Record command";
            cmd.DeviceId = "node1";
            cmd.Signal = "cmd";
            cmd.Port = 0;
            cmd.Command = "Start Rec";
            cmd.OnActive.Add(VmixAction("StartRecording", ""));
            g.Triggers.Add(cmd);
            return g;
        }

        GpioService Make()
        {
            GpioService s = new GpioService(Cfgs.Get, Hub);
            s.ReconcileMs = 200;
            s.Alert = delegate(string kind, string key, string title, string msg) { lock (alerts) alerts.Add(kind + ": " + title); };
            return s;
        }

        [Fact]
        public void Pin_changes_fire_on_and_off_and_the_state_dump_does_not()
        {
            GpioCfg g = Setup(1, 1);
            g.Triggers[0].OnActive.Clear();
            g.Triggers[0].OnInactive.Clear();
            Set<GpioCfg>("gpio", delegate(GpioCfg x) { x.Enabled = true; x.Devices = g.Devices; x.Vmix = g.Vmix; x.Triggers = g.Triggers; });
            GpioService s = Make();
            AxiaDevice d = Cfgs.Get().Gpio.Devices[0];
            s.OnLine(d, "GPI 3 hlhhh", true, false);                 // state dump: pin 2 already on
            Assert.Eq(0, s.Status().Events.Count);
            s.OnLine(d, "GPI 3 hHhhh", false, false);                // pin 2 off
            s.OnLine(d, "GPI 3 xLxxx", false, false);                // pin 2 on (x = unchanged)
            s.OnLine(d, "GPI 3 xLxxx", false, false);                // no change, nothing
            s.OnLine(d, "GPI 4 Lhhhh", false, false);                // first sight of port 4, pin 1 on: counts
            s.OnLine(d, "GPI 5 hhhhh", false, false);                // first sight, all off: nothing
            List<GpioEvent> ev = s.Status().Events;                  // newest first
            Assert.Eq(3, ev.Count);
            Assert.Eq("off", ev[2].Edge);
            Assert.Eq(2, ev[2].Pin);
            Assert.In("Mic 1 live", ev[2].Triggers);
            Assert.Eq("on", ev[1].Edge);
            Assert.Eq(4, ev[0].Port);
            Assert.Eq(0, ev[0].Triggers.Count);
            GpioPortState p3 = s.Status().Devices[0].Ports.Find(delegate(GpioPortState p) { return p.Signal == "gpi" && p.Port == 3; });
            Assert.Eq("01000", p3.Pins);
        }

        [Fact]
        public void Inverted_trigger_and_repeat_guard()
        {
            GpioCfg g = Setup(1, 1);
            g.Triggers[0].Invert = true;
            g.Triggers[0].IgnoreRepeatMs = 60000;
            g.Triggers[0].OnActive.Clear();
            g.Triggers[0].OnInactive.Clear();
            Set<GpioCfg>("gpio", delegate(GpioCfg x) { x.Enabled = true; x.Devices = g.Devices; x.Vmix = g.Vmix; x.Triggers = g.Triggers; });
            GpioService s = Make();
            AxiaDevice d = Cfgs.Get().Gpio.Devices[0];
            s.OnLine(d, "GPI 3 hhhhh", true, false);
            s.OnLine(d, "GPI 3 hlhhh", false, false);
            s.OnLine(d, "GPI 3 hhhhh", false, false);
            s.OnLine(d, "GPI 3 hlhhh", false, false);                // same edge again within 60 s: ignored
            List<GpioEvent> ev = s.Status().Events;
            Assert.Eq(3, ev.Count);
            Assert.Eq(1, ev[2].Triggers.Count);
            Assert.Eq(1, ev[1].Triggers.Count);
            Assert.Eq(0, ev[0].Triggers.Count);
        }

        [Fact]
        public void Axia_device_to_vMix_end_to_end()
        {
            FakeVmix vm;
            try { vm = new FakeVmix(); }
            catch (Exception ex) { throw new SkipException("cannot listen: " + ex.Message); }
            using (vm)
            using (FakeAxia ax = new FakeAxia())
            {
                GpioCfg g = Setup(ax.Port, vm.Port);
                Assert.Eq(0, Cfg.ValidateGpio(g).Count);
                Set<GpioCfg>("gpio", delegate(GpioCfg x) { x.Enabled = true; x.Devices = g.Devices; x.Vmix = g.Vmix; x.Triggers = g.Triggers; });
                GpioService s = Make();
                s.Start();
                try
                {
                    Assert.True(WaitFor(delegate { return s.Status().Devices[0].State == "connected" && s.Status().Devices[0].Version.Length > 0; }, 10),
                                "connects and reads the version");
                    Assert.True(WaitFor(delegate { lock (ax.Received) return ax.Received.Contains("ADD GPO"); }, 5));
                    Thread.Sleep(2200);                                        // past the start-up state dump
                    ax.Push("GPI 3 hLhhh");
                    Assert.True(WaitFor(delegate { lock (vm.Calls) return vm.Calls.Count >= 2; }, 10), "vMix called twice");
                    lock (vm.Calls)
                    {
                        Assert.Eq("/api/?Function=Cut&Input=Camera%20%26%20Guest", vm.Calls[0]);
                        Assert.Eq("/api/?Function=OverlayInput1In&Input=3", vm.Calls[1]);
                    }
                    Assert.True(WaitFor(delegate { GpioEvent e = s.Status().Events[0]; return e.Result.Contains("2 action(s) done"); }, 5),
                                s.Status().Events[0].Result);
                    ax.Push("GPI 3 hHhhh");
                    Assert.True(WaitFor(delegate { lock (vm.Calls) return vm.Calls.Count >= 3; }, 10));
                    lock (vm.Calls) Assert.Eq("/api/?Function=OverlayInput1Out", vm.Calls[2]);
                    ax.Push("GPO 4 CMD:\"Start Rec\"");
                    Assert.True(WaitFor(delegate { lock (vm.Calls) return vm.Calls.Count >= 4; }, 10));
                    lock (vm.Calls) Assert.Eq("/api/?Function=StartRecording", vm.Calls[3]);

                    // vMix refuses: the event shows it and an alert goes out
                    vm.Fail = true;
                    ax.Push("GPI 3 hLhhh");
                    Assert.True(WaitFor(delegate { lock (alerts) return alerts.Exists(delegate(string a) { return a.Contains("Mic 1 live failed"); }); }, 10));
                    Assert.True(WaitFor(delegate { return s.Status().Events[0].Result.Contains("failed"); }, 5));
                    vm.Fail = false;

                    // the device goes away and comes back: reconnects, and the state dump does not fire
                    int before;
                    lock (vm.Calls) before = vm.Calls.Count;
                    ax.InitialGpi = "hhhhh";
                    ax.DropClient();
                    Assert.True(WaitFor(delegate { return s.Status().Devices[0].State != "connected"; }, 10), "notices the drop");
                    Assert.True(WaitFor(delegate { return s.Status().Devices[0].State == "connected"; }, 15), "reconnects");
                    Thread.Sleep(2500);
                    lock (vm.Calls) Assert.Eq(before, vm.Calls.Count);
                }
                finally { s.Stop(); }
            }
        }

        [Fact]
        public void Simulate_and_run_now_without_hardware()
        {
            FakeVmix vm;
            try { vm = new FakeVmix(); }
            catch (Exception ex) { throw new SkipException("cannot listen: " + ex.Message); }
            using (vm)
            {
                GpioCfg g = Setup(1, vm.Port);
                g.Enabled = false;          // simulation works even while switched off
                Set<GpioCfg>("gpio", delegate(GpioCfg x) { x.Enabled = false; x.Devices = g.Devices; x.Vmix = g.Vmix; x.Triggers = g.Triggers; });
                GpioService s = Make();
                s.Start();
                try
                {
                    GpioEvent e = s.Simulate("node1", "gpi", 3, 2, true, null);
                    Assert.NotNull(e);
                    Assert.True(e.Simulated);
                    Assert.In("Mic 1 live", e.Triggers);
                    Assert.True(WaitFor(delegate { lock (vm.Calls) return vm.Calls.Count >= 2; }, 10));
                    List<ActionResult> r = GpioService.RunActions(g.Triggers[0].OnInactive, g.Vmix, null);
                    Assert.True(r[0].Ok, r[0].Text);
                    Assert.Contains(r[0].Text, "OverlayInput1Out");
                }
                finally { s.Stop(); }

                VmixInfo info = Vmix.State(g.Vmix[0]);
                Assert.True(info.Ok, info.Error);
                Assert.Eq("27.0.0.92", info.Version);
                Assert.Eq(3, info.Inputs.Count);
                Assert.Eq("Camera & Guest", info.Inputs[1].Title);
                Assert.Eq("bb-22", info.Inputs[1].Key);
                Assert.Eq("GT", info.Inputs[2].Type);
            }
        }

        [Fact]
        public void Probe_reports_what_the_device_says()
        {
            using (FakeAxia ax = new FakeAxia())
            {
                AxiaDevice d = new AxiaDevice();
                d.Host = "127.0.0.1";
                d.Port = ax.Port;
                d.Login = true;
                List<string> lines = GpioService.Probe(d, 1500);
                string all = string.Join("\n", lines.ToArray());
                Assert.Contains(all, "C: LOGIN");
                Assert.Contains(all, "S: VER LWRP");
                Assert.Contains(all, "S: GPI 8 hhhhh");
                Assert.Contains(all, "line(s) received");
            }
            AxiaDevice none = new AxiaDevice();
            none.Host = "127.0.0.1";
            none.Port = 1;
            Assert.Contains(string.Join("\n", GpioService.Probe(none, 500).ToArray()), "FAILED");
        }

        [Fact]
        public void Url_building_and_validation()
        {
            VmixTarget t = new VmixTarget();
            GpioAction a = VmixAction("SetText", "Lower third");
            a.SelectedName = "Headline.Text";
            a.Value = "Breaking: 5 & 6";
            a.Extra = "Mix=1&Foo";
            Assert.Eq("http://127.0.0.1:8088/api/?Function=SetText&Input=Lower%20third&Value=Breaking%3A%205%20%26%206&SelectedName=Headline.Text&Mix=1&Foo",
                      Vmix.Url(t, a));

            GpioCfg g = Setup(93, 8088);
            g.Triggers[0].DeviceId = "gone";
            g.Triggers[1].Command = "";
            g.Triggers[1].OnActive[0].Function = "";
            GpioAction http = new GpioAction();
            http.Type = "http";
            http.Url = "ftp://x";
            g.Triggers[0].OnInactive.Add(http);
            g.Devices[0].Host = "";
            List<string> e = Cfg.ValidateGpio(g);
            string all = string.Join("\n", e.ToArray());
            Assert.Contains(all, "choose the Axia device");
            Assert.Contains(all, "command text is required");
            Assert.Contains(all, "has no function");
            Assert.Contains(all, "HTTP action needs an address");
            Assert.Contains(all, "address is required");

            AppConfig c = Cfg.Starter();
            c.Gpio = g;
            c.General.Modules.Gpio = false;
            Assert.Eq(0, Cfg.Validate(c, "gpio").Count);            // switched-off part is not checked
        }

        [Fact]
        public void Unreachable_device_is_shown_and_alerted()
        {
            TcpListener l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            GpioCfg g = Setup(port, 1);
            Set<GpioCfg>("gpio", delegate(GpioCfg x) { x.Enabled = true; x.DisconnectAlertSeconds = 10; x.Devices = g.Devices; x.Vmix = g.Vmix; x.Triggers = g.Triggers; });
            GpioService s = Make();
            s.Start();
            try
            {
                Assert.True(WaitFor(delegate { return s.Status().Devices[0].State == "error"; }, 10));
                Assert.Contains(s.Status().Devices[0].Text, "Cannot connect");
                Assert.True(WaitFor(delegate { lock (alerts) return alerts.Exists(delegate(string a) { return a.Contains("unreachable"); }); }, 15));
            }
            finally { s.Stop(); }
        }
    }
}
