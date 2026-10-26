// Studios: switching a server between studio configurations, status to Pathfinder (SapV2 memory slots and
// GPIO pins), commands from Pathfinder (memory slot) and from GPIO triggers.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Watchkeeper;

namespace Watchkeeper.Tests
{
    public class StudioTests : Env
    {
        FakeWorld world = new FakeWorld();
        List<string> alerts = new List<string>();
        string vmix, vvd, cam;

        public StudioTests()
        {
            vmix = D("vMix64.exe");
            vvd = D("VVD.exe");
            cam = D("Companion.exe");
            File.WriteAllText(vmix, "fake");
            File.WriteAllText(vvd, "fake");
            File.WriteAllText(cam, "fake");
            Set<GeneralCfg>("general", delegate(GeneralCfg g) { g.Modules.Studios = true; });
            Set<StudiosCfg>("studios", delegate(StudiosCfg s)
            {
                s.Studios.Clear();
                s.Studios.Add(Studio("s1", "Studio 1", "S1"));
                s.Studios.Add(Studio("s2", "Studio 2", "S2"));
                s.StartStudio = "s1";
                s.CloseTimeoutSeconds = 2;
                s.MinSecondsBetweenSwitches = 0;
            });
            Set<List<ProgramJob>>("programs", delegate(List<ProgramJob> l)
            {
                l.Clear();
                l.Add(Prog("vmix1", "vMix - Studio 1", vmix, "Studio1.vmix", "s1"));
                l.Add(Prog("vvd1", "VVD - Studio 1", vvd, "/config studio1", "s1"));
                l.Add(Prog("vmix2", "vMix - Studio 2", vmix, "Studio2.vmix", "s2"));
                l.Add(Prog("vvd2", "VVD - Studio 2", vvd, "/config studio2", "s2"));
                l.Add(Prog("cam", "Companion", cam, "", null));
            });
        }

        static StudioDef Studio(string id, string name, string code)
        {
            StudioDef d = new StudioDef();
            d.Id = id; d.Name = name; d.Code = code;
            return d;
        }

        static ProgramJob Prog(string id, string name, string exe, string args, string studio)
        {
            ProgramJob p = new ProgramJob();
            p.Id = id;
            p.Name = name;
            p.ExePath = exe;
            p.Arguments = args;
            p.Enabled = true;
            p.StartDelaySeconds = 0;
            p.RestartDelaySeconds = 1;
            if (studio != null) p.Studios.Add(studio);
            return p;
        }

        Supervisor sup;
        StudioManager Make()
        {
            sup = new Supervisor(Cfgs.Get, world, world, world, new FakeServices(), Hub);
            sup.Alert = delegate(string kind, string key, string title, string msg) { lock (alerts) alerts.Add(kind + ": " + title); };
            StudioManager m = new StudioManager(Cfgs.Get, sup, null, Hub, D("state"));
            m.Alert = delegate(string kind, string key, string title, string msg) { lock (alerts) alerts.Add(kind + ": " + title + " | " + msg); };
            sup.InStudio = m.InStudio;
            m.Load();
            return m;
        }

        string Logs()
        {
            List<string> o = new List<string>();
            foreach (LogEntry e in Hub.Since(0, null, null, 200)) o.Add(e.Line());
            return string.Join(" || ", o.ToArray());
        }

        List<string> StartedArgs()
        {
            List<string> o = new List<string>();
            foreach (StartRequest r in world.Starts) if (Watchkeeper.Procs.Stem(r.File) != "taskkill") o.Add(Path.GetFileName(r.File) + " " + r.Arguments);
            return o;
        }

        new static bool WaitFor(Func<bool> cond, double seconds)
        {
            DateTime end = DateTime.Now.AddSeconds(seconds);
            while (DateTime.Now < end) { if (cond()) return true; Thread.Sleep(50); }
            return cond();
        }

        [Fact]
        public void Only_the_active_studio_and_untagged_programs_run()
        {
            StudioManager m = Make();
            DateTime t = DateTime.Now;
            sup.Tick(t);
            sup.Tick(t.AddSeconds(1));
            List<string> started = StartedArgs();
            Assert.Eq(3, started.Count);
            Assert.In("vMix64.exe Studio1.vmix", started);
            Assert.In("VVD.exe /config studio1", started);
            Assert.In("Companion.exe ", started);
            ProgramStatus s2 = sup.Snapshot().Find(delegate(ProgramStatus x) { return x.Id == "vmix2"; });
            Assert.False(s2.InStudio);
            Assert.Eq("Not in the active studio", s2.StateText);
            sup.Tick(t.AddSeconds(3));
            string text;
            Assert.Eq("ok", m.ComputeState(Cfgs.Get(), out text));
            Assert.Eq("S1", m.Status().ActiveCode);
        }

        [Fact]
        public void A_switch_closes_the_old_studio_first_then_starts_the_new_one()
        {
            StudioManager m = Make();
            DateTime t = DateTime.Now;
            sup.Tick(t);
            sup.Tick(t.AddSeconds(2));
            int before = world.Starts.Count;
            m.Start();
            try
            {
                ActionResult r = m.RequestSwitch("S2", "test", false);
                Assert.True(r.Ok, r.Text);
                Assert.True(WaitFor(delegate { return m.Active == "s2" && m.Status().State != "switching"; }, 10), "switched");
                // both studio 1 programs were asked to close in the signed-in session (taskkill without /F) and closed
                List<StartRequest> kills = world.Starts.FindAll(delegate(StartRequest x) { return Watchkeeper.Procs.Stem(x.File) == "taskkill"; });
                Assert.Eq(2, kills.Count);
                Assert.True(kills[0].UserSession, "taskkill in the user session");
                Assert.False(kills[0].Arguments.Contains("/F"), kills[0].Arguments);
                Assert.Eq(0, world.Find("vmix64").Count);
                // the new studio's programs start on the next check, without the start delay
                sup.Tick(DateTime.Now);
                List<string> after = StartedArgs().GetRange(3, StartedArgs().Count - 3);
                Assert.In("vMix64.exe Studio2.vmix", after);
                Assert.In("VVD.exe /config studio2", after);
                Assert.Eq(2, after.Count);
                Assert.True(File.Exists(D("state", "studio.json")), "active studio saved: " + Logs());
                lock (alerts) Assert.True(alerts.Exists(delegate(string a) { return a.StartsWith("studio_switch: Now running Studio 2") && a.Contains("Closed: vMix - Studio 1, VVD - Studio 1"); }),
                                          string.Join(" || ", alerts.ToArray()));
                // a closed old-studio program is not reported as a crash
                lock (alerts) Assert.False(alerts.Exists(delegate(string a) { return a.StartsWith("program_restart"); }), string.Join(" || ", alerts.ToArray()));
            }
            finally { m.Stop(); }
            // the active studio survives a restart of the service
            StudioManager again = Make();
            Assert.Eq("s2", again.Active);
            Assert.True(before > 0, "programs were started before");
        }

        [Fact]
        public void A_program_that_does_not_close_is_ended_after_the_time_out()
        {
            world.ClosesOnRequest = false;
            StudioManager m = Make();
            sup.Tick(DateTime.Now);
            m.Start();
            try
            {
                m.RequestSwitch("Studio 2", "test", false);
                Assert.True(WaitFor(delegate { return m.Active == "s2"; }, 15), "switched");
                Assert.True(world.Kills.Count >= 2, "ended after the time-out");
                Assert.Eq(0, world.Find("vvd").Count);
            }
            finally { m.Stop(); }
        }

        [Fact]
        public void Unknown_studios_and_the_minimum_time_between_switches()
        {
            StudioManager m = Make();
            Assert.False(m.RequestSwitch("S9", "test", false).Ok);
            Assert.True(m.RequestSwitch("S1", "test", false).Text.Contains("already active"));
            Set<StudiosCfg>("studios", delegate(StudiosCfg s) { s.MinSecondsBetweenSwitches = 600; });
            m.Start();
            try
            {
                m.RequestSwitch("S2", "test", false);
                Assert.True(WaitFor(delegate { return m.Active == "s2"; }, 10));
                m.RequestSwitch("S1", "GPIO", false);
                Thread.Sleep(1500);
                Assert.Eq("s2", m.Active);                    // held back: switches at most every 10 minutes
                m.RequestSwitch("S1", "the manager", true);     // an operator in the manager is not held back
                Assert.True(WaitFor(delegate { return m.Active == "s1"; }, 10));
                m.RequestSwitch("none", "the manager", true);
                Assert.True(WaitFor(delegate { return m.Active == ""; }, 10));
            }
            finally { m.Stop(); }
        }

        [Fact]
        public void Studio_settings_are_checked()
        {
            AppConfig c = Cfgs.Get();
            Assert.Eq(0, StudioSettings.Validate(c).Count);
            c.Studios.Studios.Add(Studio("s3", "Studio 3", "s1"));
            c.Studios.Studios.Add(Studio("s4", "Studio 4", "none"));
            c.Studios.Studios.Add(Studio("s5", "Studio 5", "bad code"));
            c.Programs[0].Studios.Add("gone");
            c.Studios.Pathfinder.Enabled = true;
            c.Studios.Pathfinder.Host = "10.0.0.10";
            c.Studios.Pathfinder.User = "wk";
            c.Studios.Pathfinder.Password = "plain:two words";
            c.Studios.Pathfinder.HeartbeatSlot = "bad slot";
            c.Studios.GpioStatus.Enabled = true;
            c.Studios.GpioStatus.HeartbeatPin = 1;
            c.Studios.GpioStatus.OkPin = 1;
            string all = string.Join(" | ", StudioSettings.Validate(c).ToArray());
            Assert.Contains(all, "another studio has the code s1");
            Assert.Contains(all, "'none' is reserved");
            Assert.Contains(all, "letters, digits, _ or -");
            Assert.Contains(all, "belongs to a studio that no longer exists");
            Assert.Contains(all, "cannot contain spaces");
            Assert.Contains(all, "without TLS the Pathfinder login crosses the network readable");
            Assert.Contains(all, "memory slot name 'bad slot'");
            Assert.Contains(all, "pin 1 is used twice");
            Assert.Contains(all, "choose the Axia device");
            // a program that belongs to a missing studio never runs
            StudioManager m = Make();
            ProgramJob orphan = new ProgramJob();
            orphan.Studios.Add("gone");
            Assert.False(m.InStudio(orphan));
        }

        [Fact]
        public void GPIO_status_pins_follow_heartbeat_health_and_studio()
        {
            Set<GpioCfg>("gpio", delegate(GpioCfg g)
            {
                AxiaDevice d = new AxiaDevice();
                d.Id = "drv";
                d.Name = "IP driver";
                d.Host = "127.0.0.1";
                g.Devices.Clear();
                g.Devices.Add(d);
            });
            Set<StudiosCfg>("studios", delegate(StudiosCfg s)
            {
                s.GpioStatus.DeviceId = "drv";
                s.GpioStatus.Port = 3;
                s.GpioStatus.HeartbeatPin = 1;
                s.GpioStatus.OkPin = 2;
                s.GpioStatus.StudioPins.Clear();
                StudioPin a = new StudioPin(); a.StudioId = "s1"; a.Pin = 4;
                StudioPin b = new StudioPin(); b.StudioId = "s2"; b.Pin = 5;
                s.GpioStatus.StudioPins.Add(a);
                s.GpioStatus.StudioPins.Add(b);
            });
            StudioManager m = Make();
            DateTime t = DateTime.Now;
            sup.Tick(t);
            sup.Tick(t.AddSeconds(1));
            Assert.Eq("GPI 3 llxlh", m.GpioLine(Cfgs.Get(), true));
            Assert.Eq("GPI 3 hlxlh", m.GpioLine(Cfgs.Get(), false));
            world.Crash("vvd");
            sup.Tick(t.AddSeconds(2));
            Assert.Eq("GPI 3 lhxlh", m.GpioLine(Cfgs.Get(), true));         // OK pin off: a program of the studio is down
        }

        [Fact]
        public void GPIO_status_pins_are_set_on_the_Axia_device()
        {
            using (FakeAxia ax = new FakeAxia())
            {
                Set<GpioCfg>("gpio", delegate(GpioCfg g)
                {
                    AxiaDevice d = new AxiaDevice();
                    d.Id = "drv";
                    d.Name = "IP driver";
                    d.Host = "127.0.0.1";
                    d.Port = ax.Port;
                    d.Password = "plain:lwrp";
                    g.Enabled = true;
                    g.Devices.Clear();
                    g.Devices.Add(d);
                });
                Set<StudiosCfg>("studios", delegate(StudiosCfg s)
                {
                    s.GpioStatus.Enabled = true;
                    s.GpioStatus.DeviceId = "drv";
                    s.GpioStatus.IntervalSeconds = 1;
                });
                GpioService gpio = new GpioService(Cfgs.Get, Hub);
                gpio.Start();
                sup = new Supervisor(Cfgs.Get, world, world, world, new FakeServices(), Hub);
                StudioManager m = new StudioManager(Cfgs.Get, sup, gpio, Hub, D("state"));
                sup.InStudio = m.InStudio;
                m.Load();
                m.Start();
                try
                {
                    Assert.True(WaitFor(delegate { lock (ax.Received) return ax.Received.FindAll(delegate(string l) { return l.StartsWith("GPI 1 "); }).Count >= 2; }, 15),
                                "status lines sent: " + string.Join(" | ", ax.Received.ToArray()));
                    List<string> got;
                    lock (ax.Received) got = new List<string>(ax.Received);
                    int login = got.IndexOf("LOGIN lwrp"), first = got.FindIndex(delegate(string l) { return l.StartsWith("GPI 1 "); });
                    Assert.True(login >= 0 && login < first, "logged in before setting pins: " + string.Join(" | ", got.ToArray()));
                    List<string> gpi = got.FindAll(delegate(string l) { return l.StartsWith("GPI 1 "); });
                    Assert.True(gpi[0][6] != gpi[1][6], "heartbeat pin changes: " + gpi[0] + " / " + gpi[1]);
                    Assert.Eq("ok", m.Status().GpioState);
                }
                finally { m.Stop(); gpio.Stop(); }
            }
        }

        [Fact]
        public void Pathfinder_slots_report_status_and_its_command_slot_switches_the_studio()
        {
            using (FakePathfinder pf = new FakePathfinder())
            {
                Set<StudiosCfg>("studios", delegate(StudiosCfg s)
                {
                    PathfinderCfg p = s.Pathfinder;
                    p.Enabled = true;
                    p.Host = "127.0.0.1";
                    p.Port = pf.Port;
                    p.User = "watchkeeper";
                    p.Password = "plain:s3cret";
                    p.IntervalSeconds = 1;
                    p.HeartbeatSlot = "SRV1_Heartbeat";
                    p.StatusSlot = "SRV1_Status";
                    p.StudioSlot = "SRV1_Studio";
                    p.CommandSlot = "SRV1_Command";
                });
                StudioManager m = Make();
                DateTime t = DateTime.Now;
                sup.Tick(t);
                sup.Tick(t.AddSeconds(1));
                m.Start();
                try
                {
                    Assert.True(WaitFor(delegate { return pf.Slot("SRV1_Studio") == "S1" && pf.Slot("SRV1_Status") == "ok"; }, 15),
                                "status written: " + string.Join(" | ", pf.Received.ToArray()) + " / link: " + m.Status().PathfinderState + " " + m.Status().PathfinderText +
                                " / status " + pf.Slot("SRV1_Status") + " / log: " + Logs());
                    string beat1 = pf.Slot("SRV1_Heartbeat");
                    Assert.True(WaitFor(delegate { return pf.Slot("SRV1_Heartbeat") != beat1; }, 5), "heartbeat counts");
                    Assert.Eq("connected", m.Status().PathfinderState);

                    // Pathfinder decides: studio 2
                    pf.SetSlot("SRV1_Command", "S2");
                    Assert.True(WaitFor(delegate { return m.Active == "s2"; }, 15), "switched by Pathfinder");
                    Assert.True(WaitFor(delegate { return pf.Slot("SRV1_Studio") == "S2"; }, 10), "the new studio is reported");
                    Assert.Eq("S2", m.Status().Desired);

                    // an unknown studio changes nothing and raises one alert
                    pf.SetSlot("SRV1_Command", "S7");
                    Assert.True(WaitFor(delegate { lock (alerts) return alerts.Exists(delegate(string a) { return a.Contains("unknown studio"); }); }, 10));
                    Assert.Eq("s2", m.Active);

                    // the connection drops: it reconnects and logs in again
                    int logins = pf.Logins;
                    pf.Drop();
                    Assert.True(WaitFor(delegate { return pf.Logins > logins; }, 20), "reconnected");
                }
                finally { m.Stop(); }
            }
        }

        [Fact]
        public void Pathfinder_login_failure_and_slot_value_parsing()
        {
            Assert.Eq("S2", PathfinderLink.ParseSlotValue("indi MemorySlots#0.MemorySlot#SRV1_Command SlotValue=\"S2\"", "srv1_command"));
            Assert.Eq("Studio 2", PathfinderLink.ParseSlotValue("INDI MemorySlots#0.MemorySlot#Cmd SlotValue=\"%BeginEncap%Studio 2%EndEncap%\"", "Cmd"));
            Assert.Eq("", PathfinderLink.ParseSlotValue("indi MemorySlots#0.MemorySlot#Cmd SlotValue=\"\"", "Cmd"));
            Assert.Null(PathfinderLink.ParseSlotValue("indi MemorySlots#0.MemorySlot#Other SlotValue=\"S2\"", "Cmd"));
            Assert.Eq("set MemorySlots#0.MemorySlot#A SlotValue=\"ok\"", PathfinderLink.SetLine("A", "ok"));
            using (FakePathfinder pf = new FakePathfinder())
            {
                PathfinderCfg p = new PathfinderCfg();
                p.Host = "127.0.0.1";
                p.Port = pf.Port;
                p.User = "watchkeeper";
                p.Password = "plain:wrong";
                string trace = StudioManager.TestPathfinder(p);
                Assert.Contains(trace, "Pathfinder refused the login");
                Assert.NotContains(trace, "wrong");                       // the password is not in the trace
                p.Password = "plain:s3cret";
                p.CommandSlot = "Missing";
                trace = StudioManager.TestPathfinder(p);
                Assert.Contains(trace, "Logged in.");
                Assert.Contains(trace, "check that this memory slot exists");
                // without TLS, a login to another computer is refused before connecting
                p.Host = "192.0.2.1";
                trace = StudioManager.TestPathfinder(p);
                Assert.Contains(trace, "not sent to 192.0.2.1 without TLS");
            }
        }

        [Fact]
        public void A_GPIO_trigger_can_switch_the_studio()
        {
            StudioManager m = Make();
            GpioService.StudioSwitch = m.SwitchFromGpio;
            try
            {
                GpioAction a = new GpioAction();
                a.Type = "studio";
                a.Studio = "s2";
                m.Start();
                ActionResult r = GpioService.RunAction(a, new List<VmixTarget>());
                Assert.True(r.Ok, r.Text);
                Assert.True(WaitFor(delegate { return m.Active == "s2"; }, 10));
                m.Stop();
            }
            finally { GpioService.StudioSwitch = null; }
        }

        [Fact]
        public void With_the_Studios_part_off_every_enabled_program_runs()
        {
            Set<GeneralCfg>("general", delegate(GeneralCfg g) { g.Modules.Studios = false; });
            StudioManager m = Make();
            sup.Tick(DateTime.Now);
            Assert.True(sup.Snapshot().TrueForAll(delegate(ProgramStatus x) { return x.InStudio; }), "studio tags are ignored");
            Assert.True(StartedArgs().Count >= 3);
            Assert.False(m.RequestSwitch("S2", "test", false).Ok);
        }
    }
}
