// Scheduling, scripts, file triggers, program supervision, alerts and the control pipe.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Watchkeeper;

namespace Watchkeeper.Tests
{
    public class JobTests : Env
    {
        static ScheduleCfg Sch(string mode, string time, double minutes) { return new ScheduleCfg(mode, time, minutes); }

        List<JobSpec> specs = new List<JobSpec>();
        int calls;

        JobManager Make(Func<RunContext, TaskResult> fn, string statePath)
        {
            JobSpec s = new JobSpec();
            s.Key = "folder:a";
            s.Kind = "folder";
            s.Id = "a";
            s.Name = "A";
            s.Source = "folders";
            s.Enabled = true;
            s.Schedule = Sch("manual", "00:00", 60);
            s.Run = delegate(RunContext ctx) { Interlocked.Increment(ref calls); return fn(ctx); };
            specs.Add(s);
            return new JobManager(delegate { return specs; }, Hub, statePath);
        }

        [Fact]
        public void Compute_next_daily_weekly_interval()
        {
            DateTime now = new DateTime(2026, 10, 2, 11, 0, 0);    // a Friday
            Assert.False(JobManager.ComputeNext(Sch("manual", "09:00", 5), now, false).HasValue);
            Assert.Eq(new DateTime(2026, 10, 2, 13, 30, 0), JobManager.ComputeNext(Sch("daily", "13:30", 5), now, false).Value);
            Assert.Eq(new DateTime(2026, 10, 3, 9, 30, 0), JobManager.ComputeNext(Sch("daily", "09:30", 5), now, false).Value);
            ScheduleCfg w = Sch("weekly", "04:00", 5);
            w.Day = "sunday";
            Assert.Eq(new DateTime(2026, 10, 4, 4, 0, 0), JobManager.ComputeNext(w, now, false).Value);
            w.Day = "friday";
            Assert.Eq(new DateTime(2026, 10, 9, 4, 0, 0), JobManager.ComputeNext(w, now, false).Value);
            w.Time = "12:00";
            Assert.Eq(new DateTime(2026, 10, 2, 12, 0, 0), JobManager.ComputeNext(w, now, false).Value);
            ScheduleCfg iv = Sch("interval", "00:00", 15);
            Assert.Eq(now.AddMinutes(15), JobManager.ComputeNext(iv, now, false).Value);
            Assert.Eq(now.AddSeconds(5), JobManager.ComputeNext(iv, now, true).Value);
            Assert.Eq(new DateTime(2026, 10, 2, 9, 30, 0), JobManager.PreviousDue(Sch("daily", "09:30", 5), now).Value);
            Assert.Eq(new DateTime(2026, 10, 1, 13, 30, 0), JobManager.PreviousDue(Sch("daily", "13:30", 5), now).Value);
            w.Day = "monday"; w.Time = "04:00";
            Assert.Eq(new DateTime(2026, 9, 28, 4, 0, 0), JobManager.PreviousDue(w, now).Value);
        }

        [Fact]
        public void Describe()
        {
            Assert.Eq("Daily at 03:00", JobManager.Describe(Sch("daily", "03:00", 1), true));
            Assert.Eq("Every 15 minutes", JobManager.Describe(Sch("interval", "03:00", 15), true));
            ScheduleCfg w = Sch("weekly", "04:00", 1);
            w.Day = "sunday";
            Assert.Eq("Weekly, Sunday at 04:00", JobManager.Describe(w, true));
            Assert.Eq("Manual only", JobManager.Describe(Sch("manual", "03:00", 15), true));
            Assert.Eq("Off", JobManager.Describe(Sch("daily", "03:00", 1), false));
        }

        [Fact]
        public void Run_now_captures_output_and_result()
        {
            JobManager jm = Make(delegate(RunContext ctx)
            {
                ctx.Log.Info("hello");
                return new TaskResult(true, "done" + (ctx.DryRun ? " dry" : ""));
            }, null);
            string msg;
            Assert.True(jm.RunNow("folder:a", true, out msg));
            Assert.True(WaitFor(delegate { return jm.Snapshot()[0].LastEnd > 0; }));
            JobStatus st = jm.Snapshot()[0];
            Assert.Eq("ok", st.LastResult);
            Assert.Eq("done dry", st.LastText);
            Assert.True(st.LastDryRun);
            Assert.Eq("manual", st.LastTrigger);
            Assert.True(jm.Lines("folder:a").Exists(delegate(string l) { return l.Contains("hello"); }));
            Assert.False(jm.RunNow("nope", false, out msg));
        }

        [Fact]
        public void No_overlapping_runs_and_crashes_are_contained()
        {
            ManualResetEvent gate = new ManualResetEvent(false);
            JobManager jm = Make(delegate(RunContext ctx)
            {
                gate.WaitOne(5000);
                throw new InvalidOperationException("boom");
            }, null);
            string msg;
            Assert.True(jm.RunNow("folder:a", false, out msg));
            Assert.True(WaitFor(delegate { return calls > 0; }));
            Assert.False(jm.RunNow("folder:a", false, out msg));
            Assert.Eq("already running", msg);
            gate.Set();
            Assert.True(WaitFor(delegate { return !jm.Snapshot()[0].Running && jm.Snapshot()[0].LastEnd > 0; }));
            Assert.Eq("failed", jm.Snapshot()[0].LastResult);
            Assert.Contains(jm.Snapshot()[0].LastText, "boom");
            Assert.True(jm.RunNow("folder:a", false, out msg));
        }

        [Fact]
        public void Tick_runs_due_jobs_only_when_enabled_and_uses_global_dry_run()
        {
            JobManager jm = Make(delegate(RunContext ctx) { return new TaskResult(true, ctx.DryRun ? "dry" : "real"); }, null);
            jm.GlobalDryRun = delegate { return true; };
            specs[0].Schedule = Sch("interval", "00:00", 60);
            DateTime t = DateTime.Now;
            jm.Tick(t);
            jm.Tick(t.AddSeconds(6));
            Assert.True(WaitFor(delegate { return jm.Snapshot()[0].LastEnd > 0; }));
            Assert.Eq("dry", jm.Snapshot()[0].LastText);
            Assert.Eq("schedule", jm.Snapshot()[0].LastTrigger);
            specs[0].Enabled = false;
            jm.Tick(t.AddHours(5));
            Thread.Sleep(300);
            Assert.Eq(1, calls);
            Assert.Eq(0.0, jm.Snapshot()[0].NextRun);
        }

        [Fact]
        public void Missed_daily_run_is_caught_up_after_a_restart()
        {
            string state = D("state", "jobs.json");
            JobManager jm = Make(delegate(RunContext ctx) { return new TaskResult(true, ctx.Trigger); }, state);
            specs[0].Schedule = Sch("daily", "03:00", 60);
            string msg;
            jm.RunNow("folder:a", false, out msg);
            Assert.True(WaitFor(delegate { return File.Exists(state) && jm.Snapshot()[0].LastEnd > 0; }));
            // pretend the last run was two days ago, then "restart"
            string text = File.ReadAllText(state);
            double last = jm.Snapshot()[0].LastStart;
            File.WriteAllText(state, text.Replace(Json.Serialize(last, false), Json.Serialize(last - 2 * 86400, false)));
            calls = 0;
            JobManager again = new JobManager(delegate { return specs; }, Hub, state);
            again.StartupDelaySeconds = 0;
            DateTime now = DateTime.Now;
            again.Tick(now);
            again.Tick(now.AddSeconds(1));
            Assert.True(WaitFor(delegate { return calls == 1 && again.Snapshot()[0].LastEnd > 0; }));
            Assert.Eq("catch-up", again.Snapshot()[0].LastText);

            // a run that is up to date is not repeated
            Thread.Sleep(500);    // state file is written just after the run ends
            calls = 0;
            JobManager third = new JobManager(delegate { return specs; }, Hub, state);
            third.StartupDelaySeconds = 0;
            third.Tick(now);
            third.Tick(now.AddSeconds(1));
            Thread.Sleep(300);
            Assert.Eq(0, calls);
        }

        [Fact]
        public void Startup_jobs_run_once()
        {
            JobManager jm = Make(delegate(RunContext ctx) { return new TaskResult(true, ctx.Trigger); }, null);
            specs[0].Schedule = null;
            specs[0].AtStartup = true;
            jm.StartupDelaySeconds = 1;
            DateTime now = DateTime.Now;
            jm.Tick(now);
            Assert.Eq("At service start", jm.Snapshot()[0].ScheduleText);
            jm.Tick(now.AddSeconds(2));
            Assert.True(WaitFor(delegate { return jm.Snapshot()[0].LastEnd > 0; }));
            Assert.Eq("startup", jm.Snapshot()[0].LastText);
            jm.Tick(now.AddHours(30));
            Thread.Sleep(200);
            Assert.Eq(1, calls);
        }

        [Fact]
        public void Finished_callback_gets_the_result()
        {
            JobManager jm = Make(delegate(RunContext ctx) { return new TaskResult(false, "bad"); }, null);
            string got = null;
            jm.Finished = delegate(JobSpec s, TaskResult r, RunContext c) { got = s.Key + ":" + r.Text + ":" + c.Trigger; };
            TaskResult res = jm.RunHere("folder:a", "file", "/x/y.mp4");
            Assert.Eq("bad", res.Text);
            Assert.Eq("folder:a:bad:file", got);
        }
    }

    public class ScriptTests : Env
    {
        string Script(string name, string body)
        {
            string p = D(name);
            File.WriteAllText(p, "#!/bin/sh\n" + body + "\n");
            Util.RunCapture("chmod", new[] { "+x", p }, 5000);
            return p;
        }

        RunContext Ctx(Cap cap)
        {
            RunContext c = new RunContext();
            c.Log = cap.Log;
            return c;
        }

        static ScriptJob Job(string cmd, string args)
        {
            ScriptJob s = new ScriptJob();
            s.Name = "S";
            s.Command = cmd;
            s.Arguments = args;
            Cfg.NormalizeScript(s, new List<string>());
            return s;
        }

        [Fact]
        public void Runs_and_captures_output_and_exit_code()
        {
            string p = Script("ok.sh", "echo hello $1; echo oops >&2; exit 0");
            Cap cap = new Cap();
            TaskResult r = ScriptRunner.Run(Job(p, "world"), Ctx(cap), new SystemLauncher());
            Assert.True(r.Ok);
            Assert.Eq(0, r.ExitCode);
            Assert.Contains(cap.Text(), "hello world");
            Assert.Contains(cap.Text(), "oops");
        }

        [Fact]
        public void Source_and_destination_reach_the_script()
        {
            string p = Script("dirs.sh", "echo \"from=$1 to=$2 env=$WK_SOURCE\"");
            ScriptJob j = Job(p, "{source} {destination}");
            j.Source = D("cache dir");
            j.Destination = D("data");
            Cap cap = new Cap();
            Assert.True(ScriptRunner.Run(j, Ctx(cap), new SystemLauncher()).Ok);
            Assert.Contains(cap.Text(), "from=" + D("cache dir") + " to=" + D("data") + " env=" + D("cache dir"));
        }

        [Fact]
        public void Exit_codes()
        {
            string p = Script("three.sh", "exit 3");
            TaskResult r = ScriptRunner.Run(Job(p, ""), Ctx(new Cap()), new SystemLauncher());
            Assert.False(r.Ok);
            Assert.Eq(3, r.ExitCode);
            ScriptJob j = Job(p, "");
            j.OkExitCodes = "0, 3";
            Assert.True(ScriptRunner.Run(j, Ctx(new Cap()), new SystemLauncher()).Ok);
        }

        [Fact]
        public void Timeout_and_cancel_kill_the_process()
        {
            string p = Script("slow.sh", "sleep 30");
            ScriptJob j = Job(p, "");
            RunContext ctx = Ctx(new Cap());
            ThreadPool.QueueUserWorkItem(delegate { Thread.Sleep(700); ctx.Cancel.Set(); });
            DateTime t0 = DateTime.Now;
            TaskResult r = ScriptRunner.Run(j, ctx, new SystemLauncher());
            Assert.False(r.Ok);
            Assert.Contains(r.Text, "stopped");
            Assert.True((DateTime.Now - t0).TotalSeconds < 10);
        }

        [Fact]
        public void Missing_script_and_dry_run()
        {
            Assert.Contains(ScriptRunner.Run(Job(D("missing.sh"), ""), Ctx(new Cap()), new SystemLauncher()).Text, "not found");
            string p = Script("never.sh", "touch " + D("ran"));
            RunContext ctx = Ctx(new Cap());
            ctx.DryRun = true;
            Assert.True(ScriptRunner.Run(Job(p, ""), ctx, new SystemLauncher()).Ok);
            Assert.False(File.Exists(D("ran")));
        }

        [Fact]
        public void Placeholders_and_command_wrappers()
        {
            Assert.Eq("\"/a b/c.mp4\" \"c.mp4\" \"c\" \"/a b\"", ScriptRunner.Expand("{file} {name} {stem} {folder}", "/a b/c.mp4"));
            Assert.Eq("\"C:\\in\\\\\"", Util.QuoteAlways(@"C:\in\"));          // trailing backslash doubled before the quote
            string f, a;
            Procs.CommandFor(@"C:\Scripts\clean up.ps1", "-Days 3", out f, out a);
            Assert.Eq("powershell.exe", f);
            Assert.Eq("-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"C:\\Scripts\\clean up.ps1\" -Days 3", a);
            Procs.CommandFor(@"C:\Scripts\x.bat", "1", out f, out a);
            Assert.Eq("/d /s /c \"C:\\Scripts\\x.bat 1\"", a);
            Procs.CommandFor(@"C:\Tools\tool.exe", "/q", out f, out a);
            Assert.Eq(@"C:\Tools\tool.exe", f);
            Assert.Eq("/q", a);
        }
    }

    public class FileTriggerTests : Env
    {
        [Fact]
        public void New_files_run_the_script_once_existing_files_are_ignored()
        {
            string watch = D("in");
            Directory.CreateDirectory(watch);
            File.WriteAllText(Path.Combine(watch, "old.txt"), "x");
            string log = D("runs.log");
            string script = D("t.sh");
            File.WriteAllText(script, "#!/bin/sh\necho \"$1\" >> " + log + "\n");
            Util.RunCapture("chmod", new[] { "+x", script }, 5000);

            AppConfig c = Cfgs.Get();
            ScriptJob s = new ScriptJob();
            s.Name = "On new file";
            s.Enabled = true;
            s.Command = script;
            s.Arguments = "{name}";
            s.Trigger = "file";
            s.WatchPath = watch;
            s.WatchPattern = "*.txt";
            s.StableSeconds = 1;
            c.Scripts.Add(s);
            Assert.Eq(0, Cfgs.SaveSection("scripts", c.Scripts).Count);
            string id = Cfgs.Get().Scripts[0].Id;

            Engine e = new Engine(Tmp, new EngineOptions { StartControl = false });
            FileTriggers ft = new FileTriggers(Cfgs.Get, MakeJobs(e), Hub, D("state"));
            ft.Tick();                              // first look: existing files are remembered, not processed
            File.WriteAllText(Path.Combine(watch, "new.txt"), "x");
            File.WriteAllText(Path.Combine(watch, "skip.log"), "x");
            ft.Tick();                              // seen, not yet stable
            Assert.False(File.Exists(log));
            Thread.Sleep(1200);
            ft.Tick();
            Assert.Eq("new.txt\n", File.ReadAllText(log));
            ft.Tick();
            Assert.Eq("new.txt\n", File.ReadAllText(log));
            Assert.True(File.Exists(D("state", "seen-" + id + ".txt")));

            // a fresh watcher (service restart) remembers what it processed
            FileTriggers again = new FileTriggers(Cfgs.Get, MakeJobs(e), Hub, D("state"));
            again.Tick();
            Thread.Sleep(1200);
            again.Tick();
            Assert.Eq("new.txt\n", File.ReadAllText(log));
        }

        JobManager MakeJobs(Engine e)
        {
            ConfigStore store = Cfgs;
            return new JobManager(delegate
            {
                List<JobSpec> l = new List<JobSpec>();
                foreach (ScriptJob sc in store.Get().Scripts)
                {
                    ScriptJob script = sc;
                    JobSpec s = new JobSpec();
                    s.Key = "script:" + sc.Id;
                    s.Kind = "script";
                    s.Name = sc.Name;
                    s.Source = "scripts";
                    s.Enabled = sc.Enabled;
                    s.Run = delegate(RunContext ctx) { return ScriptRunner.Run(script, ctx, new SystemLauncher()); };
                    l.Add(s);
                }
                return l;
            }, Hub, null);
        }
    }

    // ---------------------------------------------------------------- fakes
    public class FakeRunning : IRunning
    {
        public int pid;
        public bool exited;
        public int Pid { get { return pid; } }
        public TextReader StdOut { get { return null; } }
        public TextReader StdErr { get { return null; } }
        public bool WaitForExit(int ms) { return exited; }
        public int ExitCode { get { return 0; } }
        public void Kill() { exited = true; }
        public void Dispose() { }
    }

    public class FakeWorld : ILauncher, IProcessTable, ISessions
    {
        public readonly List<ProcInfo> Procs = new List<ProcInfo>();
        public readonly List<StartRequest> Starts = new List<StartRequest>();
        public readonly List<int> Kills = new List<int>();
        public int Session = 1;
        public bool StartsSpawnName = true;     // the started program shows up under its name
        public string FailStart;
        public FakeRunning Last;
        int nextPid = 1000;

        public IRunning Start(StartRequest r)
        {
            if (FailStart != null) throw new InvalidOperationException(FailStart);
            Starts.Add(r);
            FakeRunning f = new FakeRunning();
            f.pid = nextPid++;
            Last = f;
            if (StartsSpawnName)
            {
                ProcInfo p = new ProcInfo();
                p.Pid = f.pid;
                p.Name = Watchkeeper.Procs.Stem(r.File);
                p.SessionId = r.UserSession ? Session : 0;
                lock (Procs) Procs.Add(p);
            }
            return f;
        }

        public List<ProcInfo> Find(string name)
        {
            string n = Watchkeeper.Procs.Stem(name);
            lock (Procs) return Procs.FindAll(delegate(ProcInfo p) { return p.Name == n; });
        }

        public void Kill(int pid)
        {
            Kills.Add(pid);
            lock (Procs) Procs.RemoveAll(delegate(ProcInfo p) { return p.Pid == pid; });
            if (Last != null && Last.pid == pid) Last.exited = true;
        }

        public void Crash(string name)
        {
            string n = Watchkeeper.Procs.Stem(name);
            lock (Procs) Procs.RemoveAll(delegate(ProcInfo p) { return p.Name == n; });
            if (Last != null) Last.exited = true;
        }

        public int ConsoleUserSession(out string user)
        {
            user = Session >= 0 ? "STUDIO\\operator" : "";
            return Session;
        }

        public string ConsoleUserTemp() { return Session >= 0 ? Path.GetTempPath() : null; }
    }

    public class SupervisorTests : Env
    {
        FakeWorld world = new FakeWorld();
        List<string> alerts = new List<string>();
        string exe;

        public SupervisorTests()
        {
            exe = D("vMix64.exe");
            File.WriteAllText(exe, "fake");
            Set<List<ProgramJob>>("programs", delegate(List<ProgramJob> l)
            {
                l.Clear();
                ProgramJob p = Templates.VMix();
                p.ExePath = exe;
                p.Enabled = true;
                p.StartDelaySeconds = 10;
                p.RestartDelaySeconds = 5;
                p.MaxRestartsPerHour = 3;
                l.Add(p);
            });
        }

        Supervisor Make()
        {
            Supervisor s = new Supervisor(Cfgs.Get, world, world, world, Hub);
            s.StartGraceSeconds = 30;
            s.Alert = delegate(string kind, string key, string title, string msg) { alerts.Add(kind + ": " + title); };
            return s;
        }

        ProgramStatus St(Supervisor s) { return s.Snapshot()[0]; }

        [Fact]
        public void Starts_after_the_start_delay_in_the_user_session()
        {
            Supervisor s = Make();
            DateTime t = DateTime.Now;
            s.Tick(t);
            Assert.Eq(0, world.Starts.Count);
            Assert.Eq("waiting", St(s).State);
            s.Tick(t.AddSeconds(11));
            Assert.Eq(1, world.Starts.Count);
            Assert.True(world.Starts[0].UserSession);
            Assert.Eq(exe, world.Starts[0].File);
            Assert.Eq("high", world.Starts[0].Priority);
            s.Tick(t.AddSeconds(13));
            Assert.Eq("running", St(s).State);
            Assert.Eq(1, St(s).Starts);
        }

        [Fact]
        public void Already_running_program_is_left_alone()
        {
            ProcInfo p = new ProcInfo();
            p.Pid = 42;
            p.Name = "vmix64";
            p.SessionId = 1;
            world.Procs.Add(p);
            Supervisor s = Make();
            s.Tick(DateTime.Now.AddMinutes(1));
            Assert.Eq(0, world.Starts.Count);
            Assert.Eq("running", St(s).State);
            Assert.Eq(42, St(s).Pid);
        }

        [Fact]
        public void Waits_for_a_signed_in_user()
        {
            world.Session = -1;
            Supervisor s = Make();
            DateTime t = DateTime.Now;
            s.Tick(t.AddMinutes(5));
            Assert.Eq("waiting_session", St(s).State);
            Assert.Eq(0, world.Starts.Count);
            world.Session = 2;
            s.Tick(t.AddMinutes(6));
            Assert.Eq(0, world.Starts.Count);              // start delay counts from the sign-in
            s.Tick(t.AddMinutes(6).AddSeconds(11));
            Assert.Eq(1, world.Starts.Count);
        }

        [Fact]
        public void Restarts_after_exit_with_delay_and_alert()
        {
            Supervisor s = Make();
            DateTime t = DateTime.Now;
            s.Tick(t);
            s.Tick(t.AddSeconds(11));
            s.Tick(t.AddSeconds(12));
            Assert.Eq("running", St(s).State);
            world.Crash("vMix64");
            s.Tick(t.AddSeconds(20));
            Assert.Eq(1, world.Starts.Count);
            Assert.True(alerts.Contains("program_restart: vMix stopped"));
            s.Tick(t.AddSeconds(26));
            Assert.Eq(2, world.Starts.Count);
        }

        [Fact]
        public void Restart_limit_pauses_and_alerts_once()
        {
            Supervisor s = Make();
            DateTime t = DateTime.Now;
            s.Tick(t);
            for (int i = 0; i < 6; i++)
            {
                s.Tick(t.AddSeconds(11 + i * 40));
                s.Tick(t.AddSeconds(12 + i * 40));
                world.Crash("vMix64");
                s.Tick(t.AddSeconds(13 + i * 40));
            }
            Assert.Eq(3, world.Starts.Count);
            Assert.Eq("paused", St(s).State);
            Assert.Eq(1, alerts.FindAll(delegate(string a) { return a.StartsWith("program_problem"); }).Count);
            Assert.True(s.RequestRestart(Cfgs.Get().Programs[0].Id));
            s.Tick(t.AddSeconds(300));
            Assert.Eq(4, world.Starts.Count);
        }

        [Fact]
        public void Scheduled_restart_closes_and_starts_again()
        {
            Set<List<ProgramJob>>("programs", delegate(List<ProgramJob> l) { l[0].Restart = new ScheduleCfg("daily", "04:00", 60); });
            Supervisor s = Make();
            DateTime t = DateTime.Today.AddHours(3).AddMinutes(50);
            s.Tick(t);
            s.Tick(t.AddSeconds(11));
            s.Tick(t.AddSeconds(12));
            Assert.Eq(1, world.Starts.Count);
            int pid = St(s).Pid;
            s.Tick(DateTime.Today.AddHours(4).AddSeconds(1));
            Assert.In(pid, world.Kills);
            Assert.Eq(2, world.Starts.Count);
            Assert.Eq(0, alerts.FindAll(delegate(string a) { return a.StartsWith("program_restart"); }).Count);
            Assert.Eq(Util.ToEpoch(DateTime.Today.AddDays(1).AddHours(4)), St(s).NextScheduledRestart);
        }

        [Fact]
        public void Wrong_process_name_does_not_start_copy_after_copy()
        {
            world.StartsSpawnName = false;
            Supervisor s = Make();
            DateTime t = DateTime.Now;
            s.Tick(t);
            s.Tick(t.AddSeconds(11));
            Assert.Eq(1, world.Starts.Count);
            s.Tick(t.AddSeconds(20));
            s.Tick(t.AddSeconds(50));
            s.Tick(t.AddSeconds(90));
            Assert.Eq(1, world.Starts.Count);
            Assert.Eq("error", St(s).State);
            Assert.Contains(St(s).LastError, "Check the process name");
        }

        [Fact]
        public void Missing_exe_start_failure_disabled_and_dry_run()
        {
            Supervisor s = Make();
            DateTime t = DateTime.Now;
            s.Tick(t);
            File.Delete(exe);
            s.Tick(t.AddSeconds(11));
            Assert.Eq("error", St(s).State);
            Assert.Contains(St(s).LastError, "not found");
            File.WriteAllText(exe, "x");
            world.FailStart = "access denied";
            s.Tick(t.AddSeconds(60));
            Assert.Contains(St(s).LastError, "access denied");
            world.FailStart = null;

            Set<GeneralCfg>("general", delegate(GeneralCfg g) { g.DryRun = true; });
            s.Tick(t.AddSeconds(200));
            Assert.Eq(0, world.Starts.Count);

            Set<List<ProgramJob>>("programs", delegate(List<ProgramJob> l) { l[0].Enabled = false; });
            Set<GeneralCfg>("general", delegate(GeneralCfg g) { g.DryRun = false; });
            s.Tick(t.AddSeconds(400));
            Assert.Eq(0, world.Starts.Count);
            Assert.Eq("off", St(s).State);
        }
    }

    public class AlertTests : Env
    {
        List<string> sent = new List<string>();

        AlertManager Make()
        {
            AlertManager a = new AlertManager(Cfgs.Get, Hub);
            a.Sender = delegate(AlertDestination d, AlertRecord r) { lock (sent) sent.Add(d.Id + ":" + r.Title); return "sent"; };
            return a;
        }

        static AlertDestination Hook(string id)
        {
            AlertDestination d = new AlertDestination();
            d.Id = id;
            d.Name = "Hook " + id;
            d.Type = "webhook";
            d.Url = "https://example.invalid/" + id;
            return d;
        }

        [Fact]
        public void Rules_route_by_part_severity_and_cooldown()
        {
            Set<AlertCfg>("alerts", delegate(AlertCfg a)
            {
                a.Destinations.Add(Hook("ops"));
                a.Rules[0].MinSeverity = "error";                 // All alerts > Event Log: errors only
                a.Rules[0].CooldownMinutes = 30;
                AlertRule watchdog = new AlertRule();
                watchdog.Name = "Watchdog to ops";
                watchdog.AllParts = false;
                watchdog.Parts.Add("programs");
                watchdog.MinSeverity = "warning";
                watchdog.Destinations.Add("ops");
                watchdog.CooldownMinutes = 0;
                a.Rules.Insert(0, watchdog);
            });
            AlertManager m = Make();
            m.Start();
            m.Raise("task_failure", "folder:a", "A failed", "x");          // error: event log
            m.Raise("task_failure", "folder:a", "A failed", "x");          // cooldown 30 min
            m.Raise("program_restart", "program:v", "vMix stopped", "x");  // warning: watchdog rule only
            m.Raise("program_restart", "program:v", "vMix stopped", "x");  // no cooldown on that rule
            m.Raise("service_start", "service", "Started", "x");           // warning, not a watchdog part: nothing
            Assert.True(WaitFor(delegate { lock (sent) return sent.Count >= 3; }));
            Thread.Sleep(300);
            lock (sent)
            {
                Assert.Eq(3, sent.Count);
                Assert.In("eventlog:A failed", sent);
                Assert.Eq(2, sent.FindAll(delegate(string x) { return x == "ops:vMix stopped"; }).Count);
            }
            List<AlertRecord> recent = m.Recent();
            Assert.Eq(5, recent.Count);
            Assert.Contains(recent[0].Sent, "no alert rule");
            Assert.Eq("programs", recent[1].Part);
            Assert.Eq("Watchdog to ops", recent[1].Rules);
            Assert.True(recent.Exists(delegate(AlertRecord r) { return r.Sent.Contains("less than 30 min"); }));
            m.Stop(1000);
        }

        [Fact]
        public void Entries_stop_and_recoveries()
        {
            string vmixId = "";
            Set<List<ProgramJob>>("programs", delegate(List<ProgramJob> l) { vmixId = l[1].Id; });
            Set<AlertCfg>("alerts", delegate(AlertCfg a)
            {
                a.Destinations.Add(Hook("jira"));
                a.Destinations.Add(Hook("teams"));
                AlertRule vmix = new AlertRule();
                vmix.Name = "vMix only";
                vmix.AllParts = false;
                vmix.Parts.Add("programs");
                vmix.Items.Add("program:" + vmixId);
                vmix.Destinations.Add("jira");
                vmix.Recoveries = true;
                vmix.Stop = true;
                vmix.CooldownMinutes = 0;
                a.Rules.Insert(0, vmix);
                a.Rules[1].Recoveries = false;
                a.Rules[1].Destinations.Add("teams");
                a.Rules[1].CooldownMinutes = 0;
            });
            AlertManager m = Make();
            m.Start();
            m.Raise("program_problem", "program:" + vmixId + ":health", "vMix is not answering", "x");
            m.Raise("program_problem", "program:other:start", "VVD cannot start", "x");
            m.Raise("recovered", "program:" + vmixId + ":health", "vMix answers again", "x");
            m.Raise("recovered", "program:other:start", "VVD running again", "x");
            Assert.True(WaitFor(delegate { lock (sent) return sent.Count >= 4; }));
            Thread.Sleep(300);
            lock (sent)
            {
                Assert.Eq(4, sent.Count);
                Assert.In("jira:vMix is not answering", sent);                 // stop: not the general rule
                Assert.In("jira:vMix answers again", sent);
                Assert.In("eventlog:VVD cannot start", sent);
                Assert.In("teams:VVD cannot start", sent);
            }
            Assert.Eq("vMix", m.Recent()[3].Item);
            Assert.Contains(m.Recent()[0].Sent, "no alert rule");             // VVD recovery: general rule takes no recoveries
            m.Stop(1000);
        }

        [Fact]
        public void Failures_are_recorded_and_tests_report_them()
        {
            AlertManager m = Make();
            m.Sender = delegate(AlertDestination d, AlertRecord r) { throw new InvalidOperationException("no route"); };
            AlertRecord rec = new AlertRecord();
            rec.Title = "T";
            m.Deliver(rec, new List<string> { "eventlog" });
            Assert.Contains(rec.Sent, "Windows Event Log: FAILED no route");
            ChannelTestResult t = m.Test(Hook("x"), "send");
            Assert.False(t.Ok);
            Assert.Contains(t.Message, "no route");
        }

        [Fact]
        public void Version_2_alert_settings_are_converted()
        {
            AppConfig c = Cfg.Parse(@"{""version"": 2, ""alerts"": {""event_log"": true, ""email_enabled"": true, ""smtp_host"": ""smtp.example.com"",
                ""smtp_port"": 465, ""smtp_ssl"": true, ""smtp_user"": ""u@example.com"", ""smtp_password"": ""dpapi:abc"", ""email_from"": ""u@example.com"",
                ""email_to"": ""ops@example.com"", ""webhook_enabled"": false, ""webhook_url"": ""https://hooks.example.com/x"", ""webhook_format"": ""teams"",
                ""on_program_restart"": false, ""on_service_start"": false, ""cooldown_minutes"": 15}}");
            Assert.Eq(3, c.Alerts.Destinations.Count);
            AlertDestination mail = c.Alerts.Destinations[1];
            Assert.Eq("email", mail.Type);
            Assert.True(mail.Enabled);
            Assert.Eq("implicit", mail.SmtpTls);
            Assert.Eq("dpapi:abc", mail.SmtpPassword);
            AlertDestination hook = c.Alerts.Destinations[2];
            Assert.False(hook.Enabled);
            Assert.Eq("teams", hook.Format);
            Assert.Eq(1, c.Alerts.Rules.Count);
            Assert.Eq("error", c.Alerts.Rules[0].MinSeverity);
            Assert.Eq(15, c.Alerts.Rules[0].CooldownMinutes);
            Assert.Eq(new[] { "eventlog", "email", "webhook" }, c.Alerts.Rules[0].Destinations.ToArray());
            Assert.Eq(4, c.Version);
        }

        [Fact]
        public void Password_protection_round_trip()
        {
            string s = Secrets.Protect("p@ss");
            Assert.True(s.StartsWith("plain:") || s.StartsWith("dpapi:"));
            Assert.Eq("p@ss", Secrets.Unprotect(s));
            Assert.Eq("typed", Secrets.Unprotect("typed"));
            Assert.Eq("", Secrets.Unprotect(""));
        }

        [Fact]
        public void Webhook_payload_reaches_a_real_listener()
        {
            System.Net.HttpListener l = new System.Net.HttpListener();
            int port = 18000 + new Random().Next(2000);
            l.Prefixes.Add("http://127.0.0.1:" + port + "/hook/");
            try { l.Start(); }
            catch (Exception ex) { throw new SkipException("cannot listen: " + ex.Message); }
            string body = null;
            Thread t = new Thread(delegate()
            {
                System.Net.HttpListenerContext ctx = l.GetContext();
                using (StreamReader r = new StreamReader(ctx.Request.InputStream)) body = r.ReadToEnd();
                ctx.Response.StatusCode = 200;
                ctx.Response.Close();
            });
            t.IsBackground = true;
            t.Start();
            AlertDestination a = new AlertDestination();
            a.Type = "webhook";
            a.Url = "http://127.0.0.1:" + port + "/hook/";
            a.Format = "text";
            AlertRecord rec = new AlertRecord();
            rec.Title = "Disk";
            rec.Message = "full";
            AlertManager.SendWebhook(a, rec, "Subject line", "Body text", "srv", new NetTrace());
            t.Join(5000);
            l.Stop();
            Assert.NotNull(body);
            Assert.Contains(body, "\"text\"");
            Assert.Contains(body, "Subject line");
        }
    }

    public class EngineTests : Env
    {
        Engine engine;
        FakeWorld world = new FakeWorld();

        public EngineTests()
        {
            Set<List<FolderPolicy>>("folders", delegate(List<FolderPolicy> l)
            {
                l.Clear();
                FolderPolicy f = new FolderPolicy();
                f.Name = "Scratch";
                f.Path = D("scratch");
                f.MinAgeSeconds = 0;
                FileRule r = new FileRule();
                r.Action = "delete";
                r.OlderThan = 1;
                f.Rules.Add(r);
                l.Add(f);
            });
            Set<List<ProgramJob>>("programs", delegate(List<ProgramJob> l) { l.Clear(); });
            Directory.CreateDirectory(D("scratch"));
            File.WriteAllText(D("scratch", "old.tmp"), "x");
            Age(D("scratch", "old.tmp"), 3 * 86400);
            EngineOptions o = new EngineOptions();
            o.Launcher = world;
            o.Table = world;
            o.Sessions = world;
            o.AdapterDetector = delegate { return new List<Adapter>(); };
            o.PipeName = "studio-test-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            engine = new Engine(Tmp, o);
            engine.Start();
        }

        public override void Dispose()
        {
            engine.Shutdown();
            base.Dispose();
        }

        ControlClient Client() { return new ControlClient(engine.Control.Name); }

        [Fact]
        public void Pipe_refuses_a_stranger_and_oversized_or_deep_requests()
        {
            ControlClient c = Client();
            c.ServerCheck = delegate { return "process 4242 C:\\Users\\Public\\evil.exe running as STUDIO\\guest"; };
            ControlError e = Assert.Throws<ControlError>(delegate { c.Call("ping"); });
            Assert.Contains(e.Message, "held by another program");
            Assert.Contains(e.Message, "nothing was sent");

            FormatException deep = Assert.Throws<FormatException>(delegate { Json.Parse(new string('[', 5000) + new string(']', 5000)); });
            Assert.Contains(deep.Message, "nested deeper than");

            ControlClient ok = Client();
            JObject big = new JObject();
            big.Set("cmd", "ping");
            big.Set("pad", new string('x', ControlServer.MaxLine + 10));
            Assert.Throws<ControlError>(delegate { ok.Call(big, 2000); });
            Assert.Eq(Engine.Version, ok.Call("ping").Str("version", ""));      // the service is still fine
        }

        [Fact]
        public void Saved_secrets_are_referred_to_not_sent()
        {
            Set<AlertCfg>("alerts", delegate(AlertCfg a)
            {
                AlertDestination d = new AlertDestination();
                d.Id = "hook";
                d.Name = "Hook";
                d.Type = "webhook";
                d.Url = Secrets.Protect("http://127.0.0.1:1/hook");
                d.Proxy = "none";
                a.Destinations.Add(d);
            });
            engine.Config.Reload();
            AlertDestination copy = Json.Clone(Cfgs.Get().Alerts.Destinations.Find(delegate(AlertDestination x) { return x.Id == "hook"; }));
            copy.Url = Secrets.KeepIfSaved(copy.Url, Cfgs.Get().Alerts.Destinations.Find(delegate(AlertDestination x) { return x.Id == "hook"; }).Url, "hook");
            Assert.Eq("keep:hook", copy.Url);
            JObject req = new JObject();
            req.Set("cmd", "alerts.test");
            req.Set("destination", Json.ToJson(copy));
            ChannelTestResult r = (ChannelTestResult)Json.ConvertTo(typeof(ChannelTestResult), Client().Call(req, 3000).Get("result"));
            Assert.Contains(r.Detail, "127.0.0.1");                 // the service used the saved address
            Assert.False(r.Detail.Contains("/hook"), "the address path is not written to traces");
        }

        [Fact]
        public void Pipe_ping_status_preview_run_and_errors()
        {
            ControlClient c = Client();
            JObject pong = c.Call("ping");
            Assert.Eq(Engine.Version, pong.Str("version", ""));

            EngineStatus st = (EngineStatus)Json.ConvertTo(typeof(EngineStatus), c.Call("status").Get("status"));
            Assert.Eq(1, st.Jobs.Count);
            Assert.Eq("Scratch", st.Jobs[0].Name);
            Assert.Eq(Engine.Version, st.Version);
            Assert.Eq(1, st.ConsoleSession);
            Assert.Eq("STUDIO\\operator", st.ConsoleUser);
            Assert.True(st.ConfigHash.Length > 0);

            FolderPolicy f = Cfgs.Get().Folders[0];
            JObject pv = c.Call("folder.preview", "folder", f, "limit", 50);
            PreviewResult p = (PreviewResult)Json.ConvertTo(typeof(PreviewResult), pv.Get("preview"));
            Assert.Eq(1, p.Plan.Count);
            Assert.Eq("delete", p.Plan[0].Action);
            Assert.True(File.Exists(D("scratch", "old.tmp")));

            c.Call("job.run", "key", "folder:" + f.Id, "dry", false);
            Assert.True(WaitFor(delegate { return !File.Exists(D("scratch", "old.tmp")); }));
            Assert.True(WaitFor(delegate
            {
                EngineStatus s2 = (EngineStatus)Json.ConvertTo(typeof(EngineStatus), c.Call("status").Get("status"));
                return s2.Jobs[0].LastResult == "ok";
            }));
            List<object> lines = c.Call("job.lines", "key", "folder:" + f.Id).Get("lines") as List<object>;
            Assert.True(lines.Count > 0);

            try { c.Call("nope"); throw new AssertionFailed("expected an error"); }
            catch (ControlError ex) { Assert.Contains(ex.Message, "unknown command"); }
            try { c.Call("job.run", "key", "folder:missing"); throw new AssertionFailed("expected an error"); }
            catch (ControlError ex) { Assert.Contains(ex.Message, "unknown job"); }

            List<object> logs = c.Call("logs", "after", 0).Get("entries") as List<object>;
            Assert.True(logs.Count > 3);
        }

        [Fact]
        public void File_change_is_applied_and_reported()
        {
            ControlClient c = Client();
            AppConfig cfg = Cfgs.Get();
            cfg.General.ServerLabel = "Changed on disk";
            Thread.Sleep(30);
            File.WriteAllText(D("config.json"), Cfg.ToText(cfg) + " ");
            File.SetLastWriteTimeUtc(D("config.json"), DateTime.UtcNow.AddSeconds(3));
            Assert.True(WaitFor(delegate { return engine.Config.Get().General.ServerLabel == "Changed on disk"; }, 10));
            File.WriteAllText(D("config.json"), "{ broken");
            List<object> problems = c.Call("reload").Get("problems") as List<object>;
            Assert.Eq(1, problems.Count);
            Assert.Eq("Changed on disk", engine.Config.Get().General.ServerLabel);
            Assert.True(engine.Alerts.Recent().Exists(delegate(AlertRecord r) { return r.Kind == "service_start"; }));
        }

        [Fact]
        public void Not_running_service_gives_a_clear_error()
        {
            ControlClient c = new ControlClient("studio-test-nobody-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            try { c.Call("ping"); throw new AssertionFailed("expected an error"); }
            catch (ControlError ex) { Assert.Contains(ex.Message, "not running"); }
        }
    }

    /// <summary>Runs "user session" requests as ordinary local processes, so the file-based
    /// progress and FFREPORT handling can be exercised with a real ffmpeg.</summary>
    public class LocalAsUser : ILauncher, ISessions
    {
        readonly SystemLauncher real = new SystemLauncher();
        public readonly List<StartRequest> Seen = new List<StartRequest>();
        public string Temp;
        public IRunning Start(StartRequest r)
        {
            Seen.Add(r);
            StartRequest c = new StartRequest();
            c.File = r.File; c.Arguments = r.Arguments; c.WorkingDir = r.WorkingDir; c.Hidden = true;
            c.Redirect = r.Redirect;
            c.Priority = "normal";
            foreach (KeyValuePair<string, string> kv in r.Environment) c.Environment[kv.Key] = kv.Value;
            if (r.UserSession) c.Environment["FAKE_USER_SESSION"] = "1";      // lets the fake ffmpeg tell the sessions apart
            return real.Start(c);
        }
        public int ConsoleUserSession(out string user) { user = "u"; return 1; }
        public string ConsoleUserTemp() { return Temp; }
    }

    public class FfmpegExecTests : Env
    {
        static void NeedFfmpeg()
        {
            if (!File.Exists("/usr/bin/ffmpeg")) throw new SkipException("no /usr/bin/ffmpeg");
        }

        [Fact]
        public void User_session_run_reads_progress_and_messages_through_pipes()
        {
            NeedFfmpeg();
            LocalAsUser l = new LocalAsUser();
            l.Temp = D("usertemp");
            Directory.CreateDirectory(l.Temp);
            List<string> args = new List<string> { "-y", "-hide_banner", "-loglevel", "warning", "-nostats", "-progress", "pipe:1",
                "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=30", "-t", "2", "-c:v", "libx264", D("out.mp4") };
            List<string> progress = new List<string>();
            FfmpegRunResult r = FfmpegExec.Run(l, l, "/usr/bin/ffmpeg", args, true, "normal", 60000,
                delegate(string line) { lock (progress) progress.Add(line); }, null, null);
            Assert.Null(r.StartError);
            Assert.Eq(0, r.ExitCode);
            Assert.True(File.Exists(D("out.mp4")));
            Assert.True(progress.Exists(delegate(string x) { return x.StartsWith("out_time_us="); }));
            Assert.True(progress.Contains("progress=end"));
            Assert.True(l.Seen[0].UserSession);
            Assert.True(l.Seen[0].Redirect, "output through pipes");
            Assert.False(l.Seen[0].Environment.ContainsKey("FFREPORT"));
            Assert.False(Directory.Exists(Path.Combine(l.Temp, "Watchkeeper")), "nothing written in the user's TEMP");

            args = new List<string> { "-y", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=30", "-t", "1", "-c:v", "no_such_encoder", D("bad.mp4") };
            r = FfmpegExec.Run(l, l, "/usr/bin/ffmpeg", args, true, "normal", 60000, null, null, null);
            Assert.True(r.ExitCode != 0);
            Assert.Contains(r.Errors, "no_such_encoder");
            Assert.False(r.Errors.Contains("Command line:"));
        }

        [Fact]
        public void Converter_falls_back_to_the_user_session()
        {
            // the service-session attempts fail (fake ffmpeg refuses), the user-session attempt succeeds
            LocalAsUser l = new LocalAsUser();
            l.Temp = D("usertemp");
            Directory.CreateDirectory(l.Temp);
            string ingest = D("ingest"), outDir = D("out");
            Directory.CreateDirectory(ingest);
            Set<VerticalCfg>("vertical", delegate(VerticalCfg v) { v.WatchDir = ingest; v.OutDir = outDir; v.PollSeconds = 1; v.Gpu.Session = "user_fallback"; });
            Environment.SetEnvironmentVariable("FAKE_FAIL_UNLESS_ENV", "FAKE_USER_SESSION");
            try
            {
                Converter conv = new Converter(Cfgs, Hub, new AdapterCache(300, delegate { return new List<Adapter>(); }), l, l);
                conv.StableWaitMs = 100;
                string src = Path.Combine(ingest, "MultiCorder7 - Cam1 - 23 July 2026 - 08-29-03 AM.mp4");
                WriteMedia(src);
                Age(src, 60);
                conv.Start();
                Assert.True(WaitFor(delegate { return conv.Status().History.Count > 0; }, 30));
                conv.Stop(true, 5000);
                HistoryItem h = conv.Status().History[0];
                Assert.Eq("done", h.Status);
                Assert.Contains(h.Backend, "user session");
                Assert.False(File.Exists(src));
            }
            finally { Environment.SetEnvironmentVariable("FAKE_FAIL_UNLESS_ENV", null); }
        }
    }

    public class ServiceSetupTests
    {
        [Fact]
        public void Sc_create_command_line_keeps_the_quoted_program_path()
        {
            string bin = Util.QuoteArg(@"C:\Program Files\Watchkeeper\Watchkeeper.exe") + " --service";
            string line = Util.JoinArgs(ServiceSetup.CreateArgs(bin));
            Assert.Eq("create Watchkeeper binPath= \"\\\"C:\\Program Files\\Watchkeeper\\Watchkeeper.exe\\\" --service\" start= auto obj= LocalSystem DisplayName= Watchkeeper", line);
        }

        [Fact]
        public void Pipe_name_depends_on_the_data_folder_only()
        {
            Assert.Eq(ControlChannel.PipeName("/tmp/a"), ControlChannel.PipeName("/tmp/a/"));
            Assert.True(ControlChannel.PipeName("/tmp/a") != ControlChannel.PipeName("/tmp/b"));
            Assert.True(ControlChannel.PipeName("/tmp/a").StartsWith("Watchkeeper-"));
        }
    }
}
