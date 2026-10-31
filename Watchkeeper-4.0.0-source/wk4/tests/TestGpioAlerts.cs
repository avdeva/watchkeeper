using System;
using System.Collections.Generic;
using System.IO;
using Watchkeeper;

namespace Watchkeeper.Tests
{
    public class GpioTests : Env
    {
        Settings cfg = new Settings();
        FakeAlerts alerts = new FakeAlerts();

        GpioTrigger Setup(FakeAxia axia, FakeVmix vmix)
        {
            cfg.Gpio.Enabled = true;
            AxiaDevice d = new AxiaDevice { Id = "dev", Name = "Node", Host = "127.0.0.1", Port = axia.Port };
            cfg.Gpio.Devices.Add(d);
            cfg.Gpio.Vmix.Add(new VmixTarget { Id = "vm", Name = "vMix", Host = "127.0.0.1", Port = vmix.Port });
            GpioTrigger t = new GpioTrigger { Id = "t1", Name = "Mic live", DeviceId = "dev", Signal = "gpi", Port = 1, Pin = 3 };
            t.WhenOn.Add(new GpioAction { Type = "vmix", Function = "OverlayInput1In", Input = "Mic Live", Extra = "Duration=500" });
            t.WhenOff.Add(new GpioAction { Type = "vmix", Function = "OverlayInput1Out" });
            cfg.Gpio.Triggers.Add(t);
            return t;
        }

        [Fact]
        public void A_pin_change_runs_its_actions_and_the_first_report_does_not()
        {
            using (FakeAxia axia = new FakeAxia())
            using (FakeVmix vmix = new FakeVmix())
            {
                axia.InitialGpi = "hhlhh";                          // pin 3 already on when connecting: nothing runs
                Setup(axia, vmix);
                Gpio g = new Gpio(delegate { return cfg; }, Hub.For("gpio"), alerts);
                g.Inline = true;
                g.Tick();
                Assert.True(WaitFor(delegate { return g.Snapshot().Devices[0].State == "connected"; }, 10));
                Assert.True(WaitFor(delegate { return g.Snapshot().Devices[0].Ports.Count >= 8; }, 5));
                System.Threading.Thread.Sleep(500);
                Assert.Eq(0, vmix.Calls.Count);
                Assert.Eq("11111".Substring(0, 0) + "001" + "00", g.Snapshot().Devices[0].Ports.Find(delegate(GpioPortState p) { return p.Signal == "gpi" && p.Port == 1; }).Pins);
                axia.Push("GPI 1 hhhhh");
                Assert.True(WaitFor(delegate { lock (vmix.Calls) return vmix.Calls.Count == 1; }, 5), "off ran");
                axia.Push("GPI 1 hhlhh");
                Assert.True(WaitFor(delegate { lock (vmix.Calls) return vmix.Calls.Count == 2; }, 5), "on ran");
                Assert.Eq("/api/?Function=OverlayInput1Out", vmix.Calls[0]);
                Assert.Eq("/api/?Function=OverlayInput1In&Input=Mic%20Live&Duration=500", vmix.Calls[1]);
                Assert.Contains(g.Snapshot().Events[0].Result, "Mic live on");
                g.Stop();
            }
        }

        [Fact]
        public void Changes_while_disconnected_and_unasked_states_run_nothing()
        {
            using (FakeAxia axia = new FakeAxia())
            using (FakeVmix vmix = new FakeVmix())
            {
                axia.AddReportsState = false;                       // states only come from the GPI query
                Setup(axia, vmix);
                Gpio g = new Gpio(delegate { return cfg; }, Hub.For("gpio"), alerts);
                g.Inline = true;
                g.Tick();
                Assert.True(WaitFor(delegate { return g.Snapshot().Devices[0].Ports.Count >= 8; }, 10), "states learned from the query");
                axia.InitialGpi = "hhlhh";                          // pin 3 goes on while the link is down
                axia.DropClient();
                Assert.True(WaitFor(delegate { lock (axia.Received) return axia.Received.FindAll(delegate(string l) { return l == "GPI"; }).Count >= 2; }, 15), "reconnected and asked again");
                System.Threading.Thread.Sleep(500);
                Assert.Eq(0, vmix.Calls.Count);
                axia.Push("GPI 1 hhhhh");
                Assert.True(WaitFor(delegate { lock (vmix.Calls) return vmix.Calls.Count == 1; }, 5), "a real change still runs");
                Assert.Eq("/api/?Function=OverlayInput1Out", vmix.Calls[0]);
                g.Stop();
            }
        }

        [Fact]
        public void A_slow_trigger_does_not_hold_up_another()
        {
            using (FakeAxia axia = new FakeAxia())
            using (FakeVmix vmix = new FakeVmix())
            {
                GpioTrigger slow = Setup(axia, vmix);
                slow.WhenOn.Insert(0, new GpioAction { Type = "vmix", Function = "Wait", DelayMs = 3000 });
                GpioTrigger quick = new GpioTrigger { Id = "t2", Name = "Quick", DeviceId = "dev", Signal = "gpi", Port = 2, Pin = 1 };
                quick.WhenOn.Add(new GpioAction { Type = "vmix", Function = "Cut" });
                cfg.Gpio.Triggers.Add(quick);
                Gpio g = new Gpio(delegate { return cfg; }, Hub.For("gpio"), alerts);
                AxiaDevice d = cfg.Gpio.Devices[0];
                g.OnLine(d, "GPI 1 hhhhh", true);
                g.OnLine(d, "GPI 2 hhhhh", true);
                g.OnLine(d, "GPI 1 hhlhh", false);
                g.OnLine(d, "GPI 2 lhhhh", false);
                Assert.True(WaitFor(delegate { lock (vmix.Calls) return vmix.Calls.Contains("/api/?Function=Cut"); }, 2), "the quick trigger ran while the slow one waited");
                Assert.True(WaitFor(delegate { lock (vmix.Calls) return vmix.Calls.Count == 3; }, 8), "the slow one finished in order");
                g.Stop();
            }
        }

        [Fact]
        public void Repeats_inverted_pins_and_other_pins()
        {
            using (FakeAxia axia = new FakeAxia())
            using (FakeVmix vmix = new FakeVmix())
            {
                GpioTrigger t = Setup(axia, vmix);
                t.Invert = true;
                t.IgnoreRepeatMs = 60000;
                Gpio g = new Gpio(delegate { return cfg; }, Hub.For("gpio"), alerts);
                g.Inline = true;
                AxiaDevice d = cfg.Gpio.Devices[0];
                g.OnLine(d, "GPI 1 hhhhh", true);
                g.OnLine(d, "GPI 1 hhlhh", false);                  // low = off when inverted
                g.OnLine(d, "GPI 1 hhhhh", false);                  // high = on
                g.OnLine(d, "GPI 1 hhlhh", false);
                g.OnLine(d, "GPI 1 hhhhh", false);                  // on again within a minute: ignored
                g.OnLine(d, "GPI 1 lhhhh", false);                  // another pin: no trigger
                Assert.Eq(new List<string> { "/api/?Function=OverlayInput1Out", "/api/?Function=OverlayInput1In&Input=Mic%20Live&Duration=500" }, vmix.Calls);
                Assert.Contains(g.Snapshot().Events[1].Result, "repeat ignored");
            }
        }

        [Fact]
        public void A_failing_action_alerts_and_recovers()
        {
            using (FakeAxia axia = new FakeAxia())
            using (FakeVmix vmix = new FakeVmix())
            {
                Setup(axia, vmix);
                vmix.Fail = true;
                Gpio g = new Gpio(delegate { return cfg; }, Hub.For("gpio"), alerts);
                g.Inline = true;
                AxiaDevice d = cfg.Gpio.Devices[0];
                g.OnLine(d, "GPI 1 hhhhh", true);
                g.OnLine(d, "GPI 1 hhlhh", false);
                Assert.Contains(alerts.Text(), "vMix answered HTTP 500");
                vmix.Fail = false;
                g.OnLine(d, "GPI 1 hhhhh", false);
                Assert.Eq(1, alerts.Count("gpio:trigger:t1", true));
            }
        }

        [Fact]
        public void An_unreachable_device_alerts_after_the_set_time_and_recovers()
        {
            FakeAxia axia = new FakeAxia();
            int port = axia.Port;
            axia.Dispose();
            using (FakeVmix vmix = new FakeVmix())
            {
                cfg.Gpio.Enabled = true;
                cfg.Gpio.DisconnectAlertSeconds = 5;
                cfg.Gpio.Devices.Add(new AxiaDevice { Id = "dev", Name = "Node", Host = "127.0.0.1", Port = port });
                Gpio g = new Gpio(delegate { return cfg; }, Hub.For("gpio"), alerts);
                g.Tick();
                Assert.True(WaitFor(delegate { return g.Snapshot().Devices[0].State == "error"; }, 10));
                Assert.Eq(0, alerts.Count("gpio:device", false));
                Assert.True(WaitFor(delegate { g.Tick(); return alerts.Count("gpio:device", false) == 1; }, 10), "alert after 5 s");
                cfg.Gpio.Enabled = false;
                g.Tick();
                Assert.Eq("off", g.Snapshot().Devices[0].State);
                g.Stop();
            }
        }

        [Fact]
        public void Web_request_actions()
        {
            using (FakeWebhook hook = new FakeWebhook())
            {
                GpioSettings gs = new GpioSettings();
                GpioAction a = new GpioAction { Type = "http", Method = "POST", Url = hook.Url, Body = "{\"on\":true}" };
                Assert.Null(Gpio.Run(gs, a));
                Assert.Eq("{\"on\":true}", hook.Bodies[0]);
                hook.Status = 404;
                Assert.Contains(Gpio.Run(gs, a), "HTTP 404");
                Assert.Contains(Gpio.Run(gs, new GpioAction { Type = "vmix", Function = "Cut" }), "no vMix is set up");
            }
        }
    }

    public class AlertTests : Env
    {
        Settings cfg = new Settings();
        Alerts alerts;

        public AlertTests()
        {
            cfg.General.ServerName = "Studio A";
            cfg.Alerts.EventLog = false;
            alerts = new Alerts(delegate { return cfg; }, Hub.For("alerts"));
            alerts.Inline = true;
        }

        [Fact]
        public void Email_with_routing_repeat_limits_and_recovery()
        {
            using (FakeSmtp smtp = new FakeSmtp())
            {
                smtp.OfferAuth = false;
                AlertDestination d = new AlertDestination { Id = "m", Name = "Ops", Type = "email", SmtpHost = "127.0.0.1", SmtpPort = smtp.Port, SmtpSecurity = "none", From = "wk@example.com", To = "ops@example.com; tech@example.com" };
                d.Gpio = false;
                cfg.Alerts.Destinations.Add(d);
                alerts.Raise("watchdog", "watchdog:x:stopped", "warning", "vMix stopped", "stopped running");
                Assert.True(WaitFor(delegate { return smtp.Messages.Count == 1; }, 5));
                Assert.Contains(smtp.Messages[0], "Subject: [Studio A] WARNING: vMix stopped");
                Assert.Contains(smtp.Received.Find(delegate(string l) { return l.StartsWith("RCPT TO:<tech@"); }) ?? "", "tech@example.com");
                alerts.Raise("watchdog", "watchdog:x:stopped", "warning", "vMix stopped", "again");     // within 30 minutes: held back
                alerts.Raise("gpio", "gpio:t", "error", "GPIO", "not for this destination");
                d.MinLevel = "error";
                alerts.Raise("folders", "folders:t:files", "warning", "Files", "below the minimum level");
                alerts.Recover("watchdog:x:stopped", "vMix is running again", "running");
                Assert.True(WaitFor(delegate { return smtp.Messages.Count == 2; }, 5));
                System.Threading.Thread.Sleep(300);
                Assert.Eq(2, smtp.Messages.Count);
                Assert.Contains(smtp.Messages[1], "Subject: [Studio A] OK: vMix is running again");
                AlertStatus s = alerts.Snapshot();
                Assert.Contains(s.Recent[3].Result, "held back");
                Assert.Eq("no destination takes it", s.Recent[2].Result);
                Assert.Eq(2, s.Destinations[0].Sent);
            }
        }

        [Fact]
        public void A_recovery_goes_only_where_the_alert_went()
        {
            using (FakeWebhook hook = new FakeWebhook())
            {
                AlertDestination d = new AlertDestination { Id = "w", Name = "Hook", Type = "webhook", Url = hook.Url, MinLevel = "error" };
                cfg.Alerts.Destinations.Add(d);
                alerts.Raise("watchdog", "k", "warning", "Stopped", "x");
                alerts.Recover("k", "Running", "y");
                alerts.Recover("never-raised", "Running", "y");
                Assert.Eq(0, hook.Bodies.Count);
            }
        }

        [Fact]
        public void Webhook_bodies_are_valid_json()
        {
            using (FakeWebhook hook = new FakeWebhook())
            {
                AlertDestination d = new AlertDestination { Id = "w", Name = "Hook", Type = "webhook", Url = hook.Url };
                cfg.Alerts.Destinations.Add(d);
                alerts.Raise("folders", "k", "error", "Task \"Move\" failed", "line 1\nC:\\path");
                Assert.Eq(1, hook.Bodies.Count);
                JObject o = (JObject)Json.Parse(hook.Bodies[0]);
                Assert.Eq("[Studio A] ERROR: Task \"Move\" failed\nline 1\nC:\\path", o.Str("text", ""));
                d.Body = "{\"content\": \"{title}\", \"level\": \"{level}\", \"part\": \"{part}\"}";
                Assert.Eq("{\"content\": \"[Studio A] ERROR: x\", \"level\": \"error\", \"part\": \"folders\"}",
                          Alerts.WebhookBody(d, new Alert { Part = "folders", Level = "error", Title = "x", Message = "m" }, "Studio A", "[Studio A] ERROR: x", "b"));
            }
        }

        [Fact]
        public void Jira_opens_one_issue_and_comments_after_that()
        {
            using (FakeJira jira = new FakeJira())
            {
                jira.Auth = "Bearer pat";
                AlertDestination d = new AlertDestination { Id = "j", Name = "Jira", Type = "jira", JiraUrl = jira.Url, JiraToken = Secrets.Protect("pat"), JiraProject = "OPS", RepeatMinutes = 0 };
                cfg.Alerts.Destinations.Add(d);
                alerts.Raise("watchdog", "watchdog:x:start", "error", "vMix cannot start", "file missing");
                alerts.Raise("watchdog", "watchdog:x:start", "error", "vMix cannot start", "still missing");
                alerts.Recover("watchdog:x:start", "vMix is running again", "fine");
                Assert.Eq(1, jira.Issues.Count, LogText());
                Assert.Eq("[Studio A] ERROR: vMix cannot start", jira.Issues[0].Summary);
                Assert.True(jira.Issues[0].Labels.Contains("watchkeeper"));
                Assert.Eq(2, jira.Issues[0].Comments.Count);
                Assert.Contains(jira.Issues[0].Comments[1], "Back to normal");
                d.JiraProject = "NOPE";
                Assert.Contains(Assert.Throws<InvalidOperationException>(delegate { Alerts.Test(d, cfg); }).Message, "HTTP");
            }
        }

        [Fact]
        public void A_failed_send_is_reported_with_the_reason()
        {
            AlertDestination d = new AlertDestination { Id = "m", Name = "Ops", Type = "email", SmtpHost = "127.0.0.1", SmtpPort = 1, SmtpSecurity = "none", From = "a@b.c", To = "d@e.f" };
            cfg.Alerts.Destinations.Add(d);
            alerts.Raise("watchdog", "k", "error", "x", "y");
            DestinationStatus s = alerts.Snapshot().Destinations[0];
            Assert.Contains(s.LastError, "Cannot connect to 127.0.0.1:1");
            Assert.Contains(LogText(), "could not be sent to Ops");
        }

        [Fact]
        public void Mail_login_needs_encryption_and_rejections_are_explained()
        {
            using (FakeSmtp smtp = new FakeSmtp())
            {
                AlertDestination d = new AlertDestination { Type = "email", SmtpHost = "127.0.0.1", SmtpPort = smtp.Port, SmtpSecurity = "none", SmtpUser = "u", SmtpPassword = "p", From = "a@b.c", To = "d@e.f" };
                Assert.Contains(Assert.Throws<InvalidOperationException>(delegate { Alerts.Test(d, cfg); }).Message, "not sent over a connection without encryption");
                d.SmtpUser = "";
                smtp.RejectRcpt = "550 5.7.1 Unable to relay";
                Assert.Contains(Assert.Throws<InvalidOperationException>(delegate { Alerts.Test(d, cfg); }).Message, "Relaying denied");
            }
        }
    }
}
