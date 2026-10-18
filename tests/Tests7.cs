// Watchdog: Windows services, health checks, feature modules.
using System;
using System.Collections.Generic;
using System.IO;
using Watchkeeper;

namespace Watchkeeper.Tests
{
    public class WatchdogTests : Env
    {
        FakeWorld world = new FakeWorld();
        FakeServices services = new FakeServices();
        List<string> alerts = new List<string>();
        string exe;

        public WatchdogTests()
        {
            exe = D("vMix64.exe");
            File.WriteAllText(exe, "fake");
        }

        Supervisor Make()
        {
            Supervisor s = new Supervisor(Cfgs.Get, world, world, world, services, Hub);
            s.StartGraceSeconds = 30;
            s.HealthInline = true;
            s.Alert = delegate(string kind, string key, string title, string msg) { alerts.Add(kind + ": " + title); };
            return s;
        }

        void Programs(params ProgramJob[] jobs)
        {
            Set<List<ProgramJob>>("programs", delegate(List<ProgramJob> l) { l.Clear(); l.AddRange(jobs); });
        }

        static ProgramJob Service(string name)
        {
            ProgramJob p = new ProgramJob();
            p.Name = "Spooler watch";
            p.Kind = "service";
            p.ServiceName = name;
            p.Enabled = true;
            p.StartDelaySeconds = 5;
            p.RestartDelaySeconds = 10;
            p.MaxRestartsPerHour = 2;
            return p;
        }

        [Fact]
        public void Stopped_service_is_started_and_restarted_after_a_stop()
        {
            services.State["Spooler"] = "stopped";
            Programs(Service("Spooler"));
            Supervisor s = Make();
            DateTime t = DateTime.Now;
            s.Tick(t);
            Assert.Eq(0, services.Calls.Count);                  // start delay
            s.Tick(t.AddSeconds(6));
            Assert.Eq(new[] { "start Spooler" }, services.Calls.ToArray());
            s.Tick(t.AddSeconds(8));
            ProgramStatus st = s.Snapshot()[0];
            Assert.Eq("running", st.State);
            Assert.Eq("service", st.Kind);

            services.State["Spooler"] = "stopped";              // it stops on its own
            s.Tick(t.AddSeconds(10));
            Assert.In("program_restart: Spooler watch stopped", alerts);
            s.Tick(t.AddSeconds(15));
            Assert.Eq(1, services.Calls.Count);                  // restart delay 10 s
            s.Tick(t.AddSeconds(21));
            Assert.Eq(2, services.Calls.Count);

            services.State["Spooler"] = "stopped";              // and again: limit of 2 per hour reached
            s.Tick(t.AddSeconds(25));
            s.Tick(t.AddSeconds(40));
            Assert.Eq(2, services.Calls.Count);
            Assert.Eq("paused", s.Snapshot()[0].State);
            Assert.In("program_problem: Spooler watch keeps stopping", alerts);
        }

        [Fact]
        public void Missing_service_is_reported_once()
        {
            Programs(Service("NoSuchService"));
            Supervisor s = Make();
            DateTime t = DateTime.Now;
            s.Tick(t);
            s.Tick(t.AddSeconds(10));
            Assert.Eq("error", s.Snapshot()[0].State);
            Assert.Eq(1, alerts.FindAll(delegate(string a) { return a.Contains("service not found"); }).Count);
            Assert.Eq(0, services.Calls.Count);
        }

        [Fact]
        public void Scheduled_and_manual_restart_of_a_service()
        {
            services.State["Spooler"] = "running";
            Programs(Service("Spooler"));
            Supervisor s = Make();
            DateTime t = DateTime.Now;
            s.Tick(t);
            Assert.True(s.RequestRestart(s.Snapshot()[0].Id));
            s.Tick(t.AddSeconds(2));
            Assert.Eq("stop Spooler", services.Calls[0]);
            Assert.Eq("start Spooler", services.Calls[1]);
            Assert.False(alerts.Exists(delegate(string a) { return a.Contains("stopped"); }), "a restart we asked for is not an alert");
        }

        [Fact]
        public void Health_check_alerts_then_restarts_a_program_that_stops_answering()
        {
            ProgramJob p = Templates.VMix();
            p.ExePath = exe;
            p.Enabled = true;
            p.StartDelaySeconds = 0;
            p.HealthEnabled = true;
            p.HealthUrl = "http://127.0.0.1:8088/api";
            p.HealthIntervalSeconds = 10;
            p.HealthFailSeconds = 30;
            p.HealthGraceSeconds = 60;
            p.HealthAction = "restart";
            Programs(p);
            Supervisor s = Make();
            string answer = null;
            List<string> probed = new List<string>();
            s.HealthProbe = delegate(string url, int ms) { probed.Add(url); return answer; };
            DateTime t = DateTime.Now;
            s.Tick(t);                                            // starts vMix
            Assert.Eq(1, world.Starts.Count);
            s.Tick(t.AddSeconds(2));
            Assert.Eq("running", s.Snapshot()[0].State);
            s.Tick(t.AddSeconds(30));
            Assert.Eq(0, probed.Count);                           // grace period
            Assert.Eq("waiting", s.Snapshot()[0].Health);
            s.Tick(t.AddSeconds(63));
            s.Tick(t.AddSeconds(64));
            Assert.Eq(1, probed.Count);
            Assert.Eq("ok", s.Snapshot()[0].Health);

            answer = "no answer within 10 s";                     // vMix hangs
            for (int i = 74; i <= 92; i += 2) s.Tick(t.AddSeconds(i));       // last good answer at 64 s
            Assert.False(alerts.Exists(delegate(string a) { return a.Contains("not answering"); }), "not before 30 s without an answer");
            for (int i = 94; i <= 110; i += 2) s.Tick(t.AddSeconds(i));
            Assert.In("program_problem: vMix is not answering", alerts);
            Assert.Eq(1, world.Kills.Count);                      // restarted
            s.Tick(t.AddSeconds(112));
            Assert.Eq(2, world.Starts.Count);
            Assert.False(alerts.Exists(delegate(string a) { return a.Contains("vMix stopped"); }), "the restart is not reported as a crash");
        }

        [Fact]
        public void Health_check_alert_only_leaves_it_running()
        {
            ProgramJob p = Templates.VMix();
            p.ExePath = exe;
            p.Enabled = true;
            p.StartDelaySeconds = 0;
            p.HealthEnabled = true;
            p.HealthUrl = "tcp://127.0.0.1:8099";
            p.HealthIntervalSeconds = 5;
            p.HealthFailSeconds = 10;
            p.HealthGraceSeconds = 0;
            Programs(p);
            Supervisor s = Make();
            s.HealthProbe = delegate(string url, int ms) { return "refused"; };
            DateTime t = DateTime.Now;
            for (int i = 0; i <= 30; i += 2) s.Tick(t.AddSeconds(i));
            Assert.In("program_problem: vMix is not answering", alerts);
            Assert.Eq(0, world.Kills.Count);
            Assert.Eq("failing", s.Snapshot()[0].Health);
            Assert.Contains(s.Snapshot()[0].HealthText, "refused");
        }

        [Fact]
        public void Watchdog_module_off_runs_nothing()
        {
            services.State["Spooler"] = "stopped";
            Programs(Service("Spooler"));
            Set<GeneralCfg>("general", delegate(GeneralCfg g) { g.Modules.Programs = false; });
            Supervisor s = Make();
            DateTime t = DateTime.Now;
            s.Tick(t);
            s.Tick(t.AddSeconds(30));
            Assert.Eq(0, services.Calls.Count);
            Assert.Eq("off", s.Snapshot()[0].State);
            Assert.False(s.Snapshot()[0].Enabled);
        }

        [Fact]
        public void Validation_of_services_and_health_checks()
        {
            ProgramJob p = Service("");
            List<string> e = Cfg.ValidateProgram(p, "p");
            Assert.True(e.Exists(delegate(string x) { return x.Contains("service name is required"); }));
            p.ServiceName = "Spooler";
            Assert.Eq(0, Cfg.ValidateProgram(p, "p").Count);
            p.HealthEnabled = true;
            p.HealthUrl = "ftp://x";
            Assert.True(Cfg.ValidateProgram(p, "p").Exists(delegate(string x) { return x.Contains("health check address"); }));
            p.HealthUrl = "tcp://host";
            Assert.True(Cfg.ValidateProgram(p, "p").Exists(delegate(string x) { return x.Contains("tcp://host:port"); }));
            p.HealthUrl = "tcp://host:8099";
            Assert.Eq(0, Cfg.ValidateProgram(p, "p").Count);
        }
    }
}

namespace Watchkeeper.Tests
{
    public class DeployTests : Env
    {
        [Fact]
        public void Watchdog_profile_shows_only_the_watchdog_parts_and_is_valid()
        {
            AppConfig c = Cfg.Starter("watchdog");
            Assert.Eq("watchdog", c.General.Profile);
            Assert.False(c.General.Modules.Folders);
            Assert.True(c.General.Modules.Programs);
            Assert.True(c.General.Modules.Scripts);
            Assert.False(c.General.Modules.Vertical);
            Assert.False(c.General.Modules.Gpio);
            Assert.Eq(0, c.Folders.Count);
            Assert.Eq(0, c.Programs.Count);
            Assert.Eq(0, Cfg.ValidateAll(c).Count);
            c.General.FfmpegPath = "";                      // not needed without vertical conversion
            Assert.Eq(0, Cfg.ValidateAll(c).Count);
            AppConfig back = Cfg.Parse(Cfg.ToText(c));
            Assert.False(back.General.Modules.Folders);
            AppConfig studio = Cfg.Starter("studio");
            Assert.True(studio.General.Modules.Gpio);
            Assert.Eq(1, studio.Gpio.Vmix.Count);
            Assert.Eq("127.0.0.1", studio.Gpio.Vmix[0].Host);
        }

        [Fact]
        public void Plain_passwords_in_a_deployed_file_are_protected_and_exports_drop_them()
        {
            AppConfig c = Cfg.Starter();
            AlertDestination mail = new AlertDestination();
            mail.Type = "email";
            mail.SmtpUser = "alerts@example.com";
            mail.SmtpPassword = "typed-in-file";
            c.Alerts.Destinations.Add(mail);
            AxiaDevice d = new AxiaDevice();
            d.Host = "10.0.0.9";
            d.Login = true;
            d.Password = "lwrp";
            c.Gpio.Devices.Add(d);
            c.Gpio.Vmix[0].Password = Secrets.Protect("already");
            AlertDestination hook = new AlertDestination();
            hook.Type = "webhook";
            hook.Url = "https://hooks.slack.com/services/T0/B0/secret-token";
            hook.Headers = "Authorization: Bearer abc";
            c.Alerts.Destinations.Add(hook);
            int n = Cfg.ProtectSecrets(c) - 2;          // the webhook address and its headers are secrets too
            Assert.Eq(2, n);
            Assert.True(mail.SmtpPassword.StartsWith("dpapi:") || mail.SmtpPassword.StartsWith("plain:"));
            Assert.Eq("typed-in-file", Secrets.Unprotect(mail.SmtpPassword));
            Assert.Eq("lwrp", Secrets.Unprotect(c.Gpio.Devices[0].Password));
            Assert.Eq(0, Cfg.ProtectSecrets(c));            // nothing left to do

            c.General.ServerLabel = "Studio A";
            List<string> removed = new List<string>();
            AppConfig x = Cfg.ForExport(c, removed);
            Assert.Eq("", x.General.ServerLabel);
            Assert.Eq("", x.Alerts.Destinations[1].SmtpPassword);
            Assert.Eq("", x.Gpio.Devices[0].Password);
            Assert.Eq("", x.Gpio.Vmix[0].Password);
            Assert.Eq("", x.Alerts.Destinations[2].Url);
            Assert.Eq("", x.Alerts.Destinations[2].Headers);
            Assert.Eq(5, removed.Count);
            Assert.Eq("Studio A", c.General.ServerLabel);  // the original is untouched
        }

        [Fact]
        public void Parts_that_are_off_do_not_run()
        {
            Set<List<FolderPolicy>>("folders", delegate(List<FolderPolicy> l) { foreach (FolderPolicy f in l) f.Enabled = true; });
            Set<GeneralCfg>("general", delegate(GeneralCfg g) { g.Modules.Folders = false; g.Modules.Scripts = false; });
            Engine e = new Engine(Tmp, new EngineOptions { StartControl = false });
            e.Config.Load(false);
            List<JobStatus> jobs = e.Jobs.Snapshot();
            Assert.True(jobs.Count > 0);
            Assert.False(jobs.Exists(delegate(JobStatus j) { return j.Enabled; }), "folder policies are off with their part");
            Assert.Eq(0, Cfg.Validate(Cfgs.Get(), "folders").Count);
        }
    }
}
