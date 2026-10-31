using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using Watchkeeper;

namespace Watchkeeper.Tests
{
    public class WatchdogTests : Env
    {
        FakeProcs world = new FakeProcs();
        FakeServices services = new FakeServices();
        FakeAlerts alerts = new FakeAlerts();
        Settings cfg = new Settings();
        Watchdog dog;
        DateTime t0 = new DateTime(2026, 11, 2, 10, 0, 0);
        string vmix, launcher, app;

        public WatchdogTests()
        {
            vmix = D("vMix", "vMix64.exe");
            launcher = D("VVD", "VVD Launcher.exe");
            app = D("Local", "VVD", "app-2.4.1", "Virtual Video Director.exe");
            Write(vmix, "x");
            Write(launcher, "x");
            Write(app, "x");
            dog = new Watchdog(delegate { return cfg; }, world, services, Hub.For("watchdog"), alerts);
            dog.InlineRestart = true;
            dog.InlineHealth = true;
            dog.CloseWaitMs = 300;
        }

        ProgramEntry Add(string name, string path)
        {
            ProgramEntry p = new ProgramEntry();
            p.Name = name;
            p.Enabled = true;
            p.Path = path;
            p.StartDelaySeconds = 10;
            p.RestartDelaySeconds = 5;
            p.LauncherWaitSeconds = 30;
            cfg.Watchdog.Add(p);
            SettingsFile.Normalize(cfg);
            return p;
        }

        ProgramEntry AddVvd(string mode)
        {
            ProgramEntry p = Add("VVD", launcher);
            p.WatchPath = Path.Combine(D("Local", "VVD"), "app-*", "Virtual Video Director.exe");
            p.RunningMeans = mode;
            world.Spawns = delegate(StartSpec s) { return s.File == launcher ? app : s.File; };
            return p;
        }

        ProgramStatus St() { return dog.Snapshot()[0]; }
        bool begun;
        void At(double seconds)
        {
            if (!begun && seconds > 0) dog.Tick(t0);          // the service starts at t0
            begun = true;
            dog.Tick(t0.AddSeconds(seconds));
        }
        int StartsOf(string file) { return world.Starts.FindAll(delegate(StartSpec s) { return s.File == file; }).Count; }

        [Fact]
        public void Starts_after_the_delay_and_again_after_a_crash()
        {
            Add("vMix", vmix);
            At(0);
            Assert.Eq("waiting", St().State);
            Assert.Contains(St().Text, "Starting in 10s");
            At(9);
            Assert.Eq(0, world.Starts.Count);
            At(10);
            Assert.Eq(1, world.Starts.Count);
            Assert.True(world.Starts[0].Desktop, "in the signed-in session");
            At(12);
            Assert.Eq("running", St().State);
            Assert.Contains(St().Text, "Running (PID");

            world.Crash(vmix);
            At(20);
            Assert.Eq("waiting", St().State);
            Assert.Contains(alerts.Text(), "warning watchdog:" + cfg.Watchdog[0].Id + ":stopped");
            At(24);
            Assert.Eq(1, world.Starts.Count);
            At(25);
            Assert.Eq(2, world.Starts.Count);
            At(27);
            Assert.Eq("running", St().State);
            Assert.Eq(1, alerts.Count(":stopped", true));
            Assert.Contains(LogText(), "vMix: stopped running; it is started again in 5s");
        }

        [Fact]
        public void A_program_already_running_is_left_alone()
        {
            Add("vMix", vmix);
            world.Add(vmix, 1);
            At(0);
            Assert.Eq("running", St().State);
            At(60);
            Assert.Eq(0, world.Starts.Count);
            Assert.Eq(0, alerts.Sent.Count);
        }

        [Fact]
        public void A_copy_of_another_file_does_not_count()
        {
            Add("vMix", vmix);
            world.Add(D("Other", "vMix64.exe"), 1);
            world.Add(vmix, 0);                                       // in the service session, not a desktop copy
            At(0);
            At(10);
            Assert.Eq(1, world.Starts.Count);
        }

        [Fact]
        public void No_second_copy_when_it_runs_in_another_user_session()
        {
            Add("vMix", vmix);
            world.Add(vmix, 2);                                       // e.g. the auto-logon session, now disconnected
            At(0);
            At(10);
            Assert.Eq(0, world.Starts.Count);
            Assert.Eq("running", St().State);
            Assert.Contains(St().Text, "in session 2, not the signed-in user's");
        }

        [Fact]
        public void A_service_stuck_stopping_is_ended_and_started_again()
        {
            ProgramEntry p = Add("Playout", "");
            p.Kind = "service";
            p.ServiceName = "Playout";
            services.State["Playout"] = "stopping";
            ProcInfo proc = world.Add(D("svc", "playout.exe"), 0);
            services.Pids["Playout"] = proc.Pid;
            At(0);
            At(30);
            Assert.Contains(St().Text, "The service is stopping");
            Assert.Eq(0, world.Kills.Count);
            At(61);
            Assert.Eq(proc.Pid, world.Kills[0]);
            Assert.Contains(alerts.Text(), "has been 'stopping' for 60s");
            services.State["Playout"] = "stopped";
            At(62);
            Assert.True(services.Calls.Contains("start Playout"));
        }

        [Fact]
        public void Waits_for_a_sign_in()
        {
            Add("vMix", vmix);
            world.Session = -1;
            At(0);
            At(100);
            Assert.Eq(0, world.Starts.Count);
            Assert.Contains(St().Text, "Waiting for a user to sign in");
            world.Session = 3;
            At(101);
            At(111);
            Assert.Eq(1, world.Starts.Count);
            Assert.Eq(3, world.Procs[0].Session);
        }

        [Fact]
        public void A_start_that_fails_raises_an_error_and_is_tried_again()
        {
            Add("vMix", vmix);
            world.FailStart = "access denied";
            At(10);
            Assert.Eq("error", St().State);
            Assert.Contains(alerts.Text(), "error watchdog:" + cfg.Watchdog[0].Id + ":start: cannot start");
            world.FailStart = null;
            At(20);
            Assert.Eq(0, world.Starts.Count);
            At(41);
            Assert.Eq(1, world.Starts.Count);
            At(43);
            Assert.Eq("running", St().State);
            Assert.Eq(1, alerts.Count(":start", true));
        }

        [Fact]
        public void A_missing_program_file_is_reported()
        {
            Add("Gone", D("nope.exe"));
            At(10);
            Assert.Eq("error", St().State);
            Assert.Contains(St().Text, "Program file not found");
            At(12);
            Assert.Eq(1, alerts.Count(":start", false));         // not repeated every check
        }

        [Fact]
        public void Too_many_restarts_pause_it_with_one_alert()
        {
            ProgramEntry p = Add("vMix", vmix);
            p.MaxRestartsPerHour = 3;
            At(10);
            double t = 10;
            for (int i = 0; i < 6; i++)
            {
                At(t += 1);              // running
                world.Crash(vmix);
                At(t += 1);              // stopped: started again in 5 s
                At(t += 5);
            }
            Assert.Eq(3, world.Starts.Count);
            Assert.Eq("paused", St().State);
            Assert.Contains(St().Text, "started 3 times in the last hour");
            Assert.Eq(1, alerts.Count(":limit", false));
            At(t + 3700);
            Assert.Eq(4, world.Starts.Count);
        }

        [Fact]
        public void The_application_a_launcher_starts_is_watched()
        {
            AddVvd("auto");
            At(10);
            Assert.Eq(1, StartsOf(launcher));
            At(12);
            Assert.Eq("running", St().State);                        // the launcher exited, the application runs
            Assert.Contains(St().Text, "application PID");
            world.Crash(app);
            At(20);
            At(25);
            Assert.Eq(2, StartsOf(launcher));
            At(27);
            Assert.Eq("running", St().State);
        }

        [Fact]
        public void A_launcher_whose_application_never_appears_is_ended_and_tried_again()
        {
            AddVvd("auto");
            world.Spawns = delegate(StartSpec s) { return s.File; };       // the launcher runs, nothing else appears
            At(10);
            At(20);
            Assert.Eq("starting", St().State);
            Assert.Contains(St().Text, "waiting for 'Virtual Video Director' to appear (10 of 30s)");
            Assert.Eq(1, StartsOf(launcher));
            At(41);
            Assert.True(world.Last.exited, "the launcher was ended");
            Assert.Contains(alerts.Text(), "did not appear within 30s");
            At(46);
            Assert.Eq(2, StartsOf(launcher));
        }

        [Fact]
        public void A_version_folder_wildcard_finds_the_updated_application()
        {
            Assert.True(Watchdog.PathMatches(@"C:\Users\op\AppData\Local\VVD\app-2.5.0\VVD.exe", @"C:\Users\op\AppData\Local\VVD\app-*\VVD.exe"));
            Assert.True(Watchdog.PathMatches(@"c:\users\OP\appdata\local\vvd\app-3\vvd.exe", @"C:\Users\op\AppData\Local\VVD\app-*\VVD.exe"));
            Assert.False(Watchdog.PathMatches(@"C:\Users\op\AppData\Local\VVD\app-2\sub\VVD.exe", @"C:\Users\op\AppData\Local\VVD\app-*\VVD.exe"));
            Assert.False(Watchdog.PathMatches(@"C:\Other\app-2\VVD.exe", @"C:\Users\op\AppData\Local\VVD\app-*\VVD.exe"));
            AddVvd("auto");
            world.Add(D("Local", "VVD", "app-2.6.0", "Virtual Video Director.exe"), 1);
            At(0);
            Assert.Eq("running", St().State);
            Assert.Eq(0, world.Starts.Count);
        }

        [Fact]
        public void Running_can_mean_the_launcher_by_its_PID()
        {
            AddVvd("started");
            world.Spawns = delegate(StartSpec s) { return s.File; };       // the launcher stays, the application is not visible
            At(10);
            At(12);
            Assert.Eq("running", St().State);
            Assert.Contains(St().Text, "PID " + world.Last.pid);
            At(200);
            Assert.Eq(1, world.Starts.Count);
            world.Crash(launcher);
            At(210);
            At(215);
            Assert.Eq(2, world.Starts.Count);
        }

        [Fact]
        public void Running_can_mean_either()
        {
            AddVvd("either");
            world.LauncherStays = true;
            At(10);
            At(12);
            Assert.Eq("running", St().State);
            world.Crash(app);
            At(20);
            Assert.Eq("running", St().State);                        // the launcher still counts
            Assert.Eq(1, world.Starts.Count);
        }

        [Fact]
        public void Both_closes_what_runs_alone_after_the_wait()
        {
            AddVvd("both");
            world.LauncherStays = true;
            At(10);
            At(12);
            Assert.Eq("running", St().State);
            Assert.Contains(St().Text, "launcher PID");
            world.Crash(app);                                         // the application crashes, the launcher stays
            At(20);
            Assert.Eq("starting", St().State);
            Assert.Contains(St().Text, "waiting for 'Virtual Video Director'");
            At(51);                                                   // 30 s wait over: closed and started again
            Assert.Eq(0, world.Count(launcher) - (world.Last.exited ? 0 : 1));
            At(52);
            Assert.Eq(2, StartsOf(launcher));
            Assert.Contains(alerts.Text(), "ran without 'Virtual Video Director' for 30s");
        }

        [Fact]
        public void Background_programs_run_in_the_service_session()
        {
            ProgramEntry p = Add("Tool", vmix);
            p.RunIn = "background";
            world.Session = -1;                                       // nobody signed in: does not matter
            At(10);
            Assert.Eq(1, world.Starts.Count);
            Assert.False(world.Starts[0].Desktop);
            Assert.True(world.Starts[0].Hidden);
            At(12);
            Assert.Eq("running", St().State);
        }

        [Fact]
        public void A_stopped_service_is_started()
        {
            ProgramEntry p = Add("Spooler", "");
            p.Kind = "service";
            p.ServiceName = "Spooler";
            services.State["Spooler"] = "stopped";
            At(10);
            Assert.Eq("start Spooler", services.Calls[0]);
            At(12);
            Assert.Eq("running", St().State);
            services.State["Spooler"] = "stopped";
            At(20);
            Assert.Eq(1, alerts.Count(":stopped", false));
            At(25);
            Assert.Eq(2, services.Calls.FindAll(delegate(string c) { return c == "start Spooler"; }).Count);
        }

        [Fact]
        public void A_missing_service_is_an_error()
        {
            ProgramEntry p = Add("Nope", "");
            p.Kind = "service";
            p.ServiceName = "NoSuchService";
            At(10);
            Assert.Eq("error", St().State);
            Assert.Contains(alerts.Text(), "does not exist on this computer");
        }

        [Fact]
        public void A_service_and_the_application_it_starts_must_both_run()
        {
            ProgramEntry p = Add("VVD service", "");
            p.Kind = "service";
            p.ServiceName = "VVDLauncher";
            p.WatchPath = app;
            services.State["VVDLauncher"] = "running";
            ProcInfo a = world.Add(app, 0);                           // the service starts it in its own session
            At(0);
            Assert.Eq("running", St().State);
            Assert.Contains(St().Text, "application PID " + a.Pid);
            world.Crash(app);
            At(5);
            Assert.Eq("starting", St().State);
            Assert.Contains(St().Text, "The service is running; waiting for 'Virtual Video Director'");
            services.OnStart = delegate { world.Add(app, 0); };
            At(36);                                                   // wait over: the service is stopped ...
            Assert.True(services.Calls.Contains("stop VVDLauncher"));
            At(37);                                                   // ... and started again
            Assert.True(services.Calls.Contains("start VVDLauncher"));
            At(39);
            Assert.Eq("running", St().State);
        }

        [Fact]
        public void A_program_with_full_rights_is_asked_to_close_with_full_rights()
        {
            ProgramEntry p = Add("vMix", vmix);
            p.Elevated = true;
            p.RestartAt = "10:05";
            At(10);
            At(12);
            At(300);
            StartSpec ask = world.Starts.Find(delegate(StartSpec s) { return Watchkeeper.Procs.Stem(s.File) == "taskkill"; });
            Assert.True(ask != null, "asked to close");
            Assert.True(ask.ElevatedFirst, "with full rights");
            Assert.True(world.Starts[0].Elevated && !world.Starts[0].ElevatedFirst, "the program itself starts as set up");
        }

        [Fact]
        public void The_daily_restart_closes_and_starts_again()
        {
            ProgramEntry p = Add("vMix", vmix);
            p.RestartAt = "10:05";
            At(10);
            At(12);
            Assert.Eq("running", St().State);
            Assert.Eq(Util.ToEpoch(t0.AddMinutes(5)), St().NextRestart);
            At(299);
            Assert.Eq(1, world.Starts.Count);
            At(300);
            Assert.True(world.Starts.Exists(delegate(StartSpec s) { return s.File.EndsWith("taskkill.exe") || s.File == "taskkill.exe"; }), "asked to close first");
            At(301);
            Assert.Eq(2, StartsOf(vmix));
            At(303);
            Assert.Eq("running", St().State);
            Assert.Eq(0, alerts.Count(":stopped", false));            // an intended restart is not a problem
            Assert.Eq(Util.ToEpoch(t0.AddDays(1).AddMinutes(5)), St().NextRestart);
        }

        [Fact]
        public void Daily_restart_days_are_respected()
        {
            ProgramEntry p = new ProgramEntry();
            p.RestartAt = "04:00";
            p.RestartDays = new List<string> { "sunday" };
            DateTime? next = Watchdog.NextRestart(p, new DateTime(2026, 11, 2, 10, 0, 0));    // a Monday
            Assert.Eq(new DateTime(2026, 11, 8, 4, 0, 0), next.Value);
        }

        [Fact]
        public void A_restart_from_the_manager()
        {
            Add("vMix", vmix);
            At(10);
            At(12);
            Assert.True(dog.RequestRestart(cfg.Watchdog[0].Id));
            At(14);
            At(15);
            Assert.Eq(2, StartsOf(vmix));
            Assert.Eq(0, alerts.Count(":stopped", false));
        }

        [Fact]
        public void A_health_check_that_stops_answering_alerts_and_can_restart()
        {
            TcpListener l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            ProgramEntry p = Add("vMix", vmix);
            p.HealthUrl = "tcp://127.0.0.1:" + port;
            p.HealthGraceSeconds = 10;
            p.HealthEverySeconds = 5;
            p.HealthFailSeconds = 20;
            p.HealthAction = "restart";
            At(10);
            At(12);
            Assert.Eq("waiting", St().Health);
            At(25);
            Assert.Eq("ok", St().Health);
            l.Stop();
            At(30);
            At(40);
            Assert.Eq("ok", St().Health);
            Assert.Contains(St().HealthText, "No answer for");
            At(46);
            Assert.Contains(alerts.Text(), "has not answered for");
            At(48);
            Assert.Eq(2, StartsOf(vmix));                             // restarted
        }

        [Fact]
        public void Changed_settings_take_effect()
        {
            ProgramEntry p = Add("vMix", vmix);
            At(10);
            At(12);
            p.Enabled = false;
            At(14);
            Assert.Eq("off", St().State);
            p.Enabled = true;
            At(16);
            Assert.Eq("running", St().State);                         // still running: adopted again, not started twice
            Assert.Eq(1, world.Starts.Count);
        }
    }
}
