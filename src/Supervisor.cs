// The watchdog: keeps programs such as VVD and vMix, and Windows services, running. Every couple
// of seconds each enabled entry is checked; one that is not running is started (programs in the
// signed-in desktop session when they need it), after a delay, with a limit on starts per hour.
// Optional scheduled restarts, and an optional health check (an address that must keep
// answering) that alerts or restarts when it stops answering.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Watchkeeper
{
    public class ProgramStatus
    {
        public string Id = "";
        public string Name = "";
        public bool Enabled;
        public string State = "off";      // off | waiting_session | waiting | running | paused | error
        public string StateText = "";
        public int Pid;
        public int Session = -1;
        public double RunningSince;        // epoch, 0 = unknown / not running
        public int Starts;                 // starts by this service since it started
        public int StartsLastHour;
        public double LastExit;
        public string LastError = "";
        public double NextAction;          // epoch of the next start / scheduled restart, 0 = none
        public double NextScheduledRestart;
        public string Kind = "program";
        /// <summary>False when the entry is not run because none of its studios is active (Studios part).</summary>
        public bool InStudio = true;
        public string Health = "";         // "" (no check) | waiting | ok | failing
        public string HealthText = "";
    }

    public class Supervisor
    {
        class Slot
        {
            public bool WasRunning;
            public int Pid;
            public int Session = -1;
            public double RunningSince;
            public double StartAt;            // do not start before this
            public bool StartAtSet;
            public List<double> StartTimes = new List<double>();
            public int Starts;
            public double LastExit;
            public string LastError = "";
            public string State = "off";
            public string StateText = "";
            public string RestartSig;
            public DateTime? NextScheduled;
            public bool PausedAlerted;
            public IRunning Last;             // handle of the process we started last
            public double LastStartedAt;
            public bool ManualRestart;
            public bool ExpectExit;           // we closed it on purpose (scheduled / manual restart)
            // health check
            public double HealthEpoch = -1;   // start of the run being checked
            public double HealthLastOk;
            public double HealthLastCheck;
            public bool HealthBusy;
            public bool HealthDone;
            public string HealthResult;
            public string HealthLastError = "";
            public bool HealthFailing;
            public string Health = "";
            public string HealthText = "";
        }

        readonly Func<AppConfig> config;
        readonly ILauncher launcher;
        readonly IProcessTable table;
        readonly ISessions sessions;
        readonly IServiceControl services;
        readonly SourceLogger log;
        readonly Dictionary<string, Slot> states = new Dictionary<string, Slot>();
        readonly object lk = new object();
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        Thread thread;
        int lastSession = -2;

        public int TickMs = 2000;
        /// <summary>A started process is given this long to appear under its name before another start is tried.</summary>
        public int StartGraceSeconds = 45;
        /// <summary>(kind, key, title, message) - see AlertManager.</summary>
        public Action<string, string, string, string> Alert;
        public Func<bool> GlobalDryRun = delegate { return false; };
        /// <summary>(address, timeout ms) -> null when healthy, otherwise the reason. Replaceable for tests.</summary>
        public Func<string, int, string> HealthProbe = Net.Probe;
        /// <summary>Tests: run the health probe inside Tick instead of on a worker thread.</summary>
        public bool HealthInline;
        /// <summary>Studios part: false for an entry that belongs to studios none of which is active (it is then not run).</summary>
        public Func<ProgramJob, bool> InStudio = delegate { return true; };

        public Supervisor(Func<AppConfig> config, ILauncher launcher, IProcessTable table, ISessions sessions, LogHub hub)
            : this(config, launcher, table, sessions, new SystemServiceControl(), hub) { }

        public Supervisor(Func<AppConfig> config, ILauncher launcher, IProcessTable table, ISessions sessions, IServiceControl services, LogHub hub)
        {
            this.config = config;
            this.launcher = launcher;
            this.table = table;
            this.sessions = sessions;
            this.services = services ?? new SystemServiceControl();
            this.log = hub.Logger("programs");
        }

        public void Start()
        {
            lock (lk)
            {
                if (thread != null && thread.IsAlive) return;
                stop.Reset();
                thread = new Thread(Loop);
                thread.Name = "supervisor";
                thread.IsBackground = true;
                thread.Priority = ThreadPriority.AboveNormal;
                thread.Start();
            }
        }

        public void Stop() { stop.Set(); }

        void Loop()
        {
            while (!stop.WaitOne(TickMs))
            {
                try { Tick(DateTime.Now); }
                catch (Exception ex) { log.Error("Supervisor error: " + ex.Message); }
            }
        }

        // alert keys raised per entry and not yet "back to normal"
        readonly Dictionary<string, List<string>> open = new Dictionary<string, List<string>>();

        void RaiseAlert(string kind, string key, string title, string message)
        {
            Action<string, string, string, string> a = Alert;
            string[] k = key.Split(':');
            if (kind != "recovered" && k.Length >= 2)
            {
                lock (lk)
                {
                    List<string> l;
                    if (!open.TryGetValue(k[1], out l)) { l = new List<string>(); open[k[1]] = l; }
                    if (!l.Contains(key)) l.Add(key);
                }
            }
            if (a == null) return;
            try { a(kind, key, title, message); } catch (Exception) { }
        }

        /// <summary>The entry runs (or answers) again: send "back to normal" for its open alerts.</summary>
        void Recover(ProgramJob p, bool health, string title, string message)
        {
            List<string> keys = new List<string>();
            lock (lk)
            {
                List<string> l;
                if (!open.TryGetValue(p.Id, out l)) return;
                foreach (string key in l.ToArray())
                {
                    bool isHealth = key.EndsWith(":health");
                    if (isHealth != health) continue;
                    keys.Add(key);
                    l.Remove(key);
                }
            }
            Action<string, string, string, string> a = Alert;
            if (a == null) return;
            foreach (string key in keys)
            {
                try { a("recovered", key, title, message); } catch (Exception) { }
            }
        }

        public static string ProcessNameOf(ProgramJob p)
        {
            if (p.ProcessName.Length > 0) return p.ProcessName;
            return Procs.Stem(p.ExePath);
        }

        public void Tick(DateTime nowDt)
        {
            AppConfig cfg = config();
            double now = Util.ToEpoch(nowDt);
            string user;
            int session = sessions.ConsoleUserSession(out user);
            if (session != lastSession)
            {
                if (session >= 0) log.Info("Signed-in console session: " + session + " (" + user + ").");
                else if (lastSession >= 0) log.Warn("Nobody is signed in at the console; desktop programs wait for a sign-in.");
                lastSession = session;
            }
            HashSet<string> ids = new HashSet<string>();
            foreach (ProgramJob p in cfg.Programs)
            {
                ids.Add(p.Id);
                Slot st;
                lock (lk)
                {
                    if (!states.TryGetValue(p.Id, out st)) { st = new Slot(); states[p.Id] = st; }
                }
                try
                {
                    if (!cfg.General.Modules.Programs) { Off(st); continue; }
                    if (p.Enabled && !InStudio(p))
                    {
                        Off(st);
                        Set(st, "off", "Not in the active studio");
                        continue;
                    }
                    if (p.Kind == "service") CheckService(p, st, nowDt, now, cfg.General.DryRun);
                    else Check(p, st, nowDt, now, session, cfg.General.DryRun);
                }
                catch (Exception ex)
                {
                    lock (lk) { st.State = "error"; st.StateText = ex.Message; st.LastError = ex.Message; }
                    log.Error(p.Name + ": " + ex.Message);
                }
            }
            lock (lk)
            {
                List<string> gone = new List<string>();
                foreach (string k in states.Keys) if (!ids.Contains(k)) gone.Add(k);
                foreach (string k in gone) states.Remove(k);
            }
        }

        void Set(Slot st, string state, string text)
        {
            lock (lk) { st.State = state; st.StateText = text; }
        }

        void Off(Slot st)
        {
            Set(st, "off", "Not supervised");
            st.WasRunning = false;
            st.StartAtSet = false;
            st.HealthEpoch = -1;
            lock (lk) { st.Health = ""; st.HealthText = ""; }
        }

        void Check(ProgramJob p, Slot st, DateTime nowDt, double now, int consoleSession, bool dryRun)
        {
            if (!p.Enabled)
            {
                Off(st);
                return;
            }
            bool userSession = p.RunAs == "user";
            int session = userSession ? consoleSession : 0;
            if (userSession && session < 0)
            {
                Set(st, "waiting_session", "Waiting for a user to sign in (auto-logon)");
                st.Session = -1;
                st.StartAtSet = false;
                st.WasRunning = false;      // the session ended: not a crash of the program
                st.Pid = 0;
                st.RunningSince = 0;
                st.ExpectExit = false;
                return;
            }
            if (st.Session != session)
            {
                // new session (first check, or the user signed in again): give the desktop time to settle
                st.Session = session;
                st.StartAtSet = false;
            }
            if (!st.StartAtSet)
            {
                st.StartAt = now + p.StartDelaySeconds;
                st.StartAtSet = true;
            }

            // ---- scheduled restart
            string sig = p.Restart.Mode + "|" + p.Restart.Time + "|" + p.Restart.Day + "|" + Util.Num(p.Restart.IntervalMinutes, 3);
            if (sig != st.RestartSig)
            {
                st.RestartSig = sig;
                st.NextScheduled = p.Restart.Mode == "manual" ? null : JobManager.ComputeNext(p.Restart, nowDt, false);
            }

            string name = ProcessNameOf(p);
            List<ProcInfo> found = FindIn(p, name, userSession ? session : 0);

            bool restartNow = false;
            if (st.NextScheduled.HasValue && nowDt >= st.NextScheduled.Value)
            {
                st.NextScheduled = JobManager.ComputeNext(p.Restart, nowDt, false);
                if (found.Count > 0)
                {
                    log.Info(p.Name + ": scheduled restart.");
                    restartNow = true;
                }
            }
            if (st.ManualRestart)
            {
                st.ManualRestart = false;
                restartNow = true;
                log.Info(p.Name + ": restart requested from the manager.");
            }
            if (restartNow)
            {
                if (dryRun) log.Info("[dry-run] " + p.Name + ": would close and start again.");
                else
                {
                    st.ExpectExit = true;
                    foreach (ProcInfo pi in found)
                    {
                        try { table.Kill(pi.Pid); }
                        catch (Exception ex) { log.Warn(p.Name + ": could not close PID " + pi.Pid + ": " + ex.Message); }
                    }
                    found = FindIn(p, name, userSession ? session : 0);
                    st.StartAt = now;    // start again straight away
                }
            }

            if (found.Count > 0)
            {
                if (!st.WasRunning)
                {
                    st.RunningSince = now;
                    if (st.Last == null || st.Last.Pid != found[0].Pid)
                        log.Info(p.Name + ": running (PID " + found[0].Pid + (userSession ? ", session " + session : "") + ").");
                    Recover(p, false, p.Name + " is running again", p.Name + " is running again (PID " + found[0].Pid + ").");
                }
                st.WasRunning = true;
                st.Pid = found[0].Pid;
                st.ExpectExit = false;
                lock (lk) { st.LastError = ""; }
                Set(st, "running", found.Count > 1 ? found.Count + " instances running" : "Running");
                List<ProcInfo> running = found;
                Health(p, st, now, dryRun, delegate
                {
                    st.ExpectExit = true;
                    foreach (ProcInfo pi in running)
                    {
                        try { table.Kill(pi.Pid); }
                        catch (Exception ex) { log.Warn(p.Name + ": could not close PID " + pi.Pid + ": " + ex.Message); }
                    }
                    st.StartAt = now;
                });
                return;
            }
            lock (lk) { st.Health = ""; st.HealthText = ""; }

            // ---- not running
            if (st.WasRunning)
            {
                st.WasRunning = false;
                st.LastExit = now;
                st.Pid = 0;
                st.RunningSince = 0;
                if (!st.ExpectExit)
                {
                    log.Warn(p.Name + ": has stopped running; starting it again in " + p.RestartDelaySeconds + "s.");
                    RaiseAlert("program_restart", "program:" + p.Id, p.Name + " stopped",
                               p.Name + " stopped running and will be started again in " + p.RestartDelaySeconds + " seconds.");
                    st.StartAt = now + p.RestartDelaySeconds;
                }
                st.ExpectExit = false;
            }

            // a process we just started may still be loading and not yet visible under its name
            if (st.Last != null && now - st.LastStartedAt < StartGraceSeconds)
            {
                bool alive;
                try { alive = !st.Last.WaitForExit(0); } catch (Exception) { alive = false; }
                if (alive)
                {
                    Set(st, "waiting", "Started PID " + st.Last.Pid + ", waiting for '" + name + "' to appear");
                    return;
                }
            }
            else if (st.Last != null && now - st.LastStartedAt >= StartGraceSeconds)
            {
                bool alive;
                try { alive = !st.Last.WaitForExit(0); } catch (Exception) { alive = false; }
                if (alive)
                {
                    // Our process is alive but nothing called <name> exists: the name is wrong. Do not start more copies.
                    string msg = "started PID " + st.Last.Pid + " is running, but no process named '" + name +
                                 "' exists. Check the process name; no further copies are started.";
                    Set(st, "error", msg);
                    if (st.LastError != msg)
                    {
                        lock (lk) { st.LastError = msg; }
                        log.Error(p.Name + ": " + msg);
                        RaiseAlert("program_problem", "program:" + p.Id + ":name", p.Name + ": process name not found", msg);
                    }
                    return;
                }
            }

            if (now < st.StartAt)
            {
                Set(st, "waiting", "Starting in " + ((int)Math.Ceiling(st.StartAt - now)) + "s");
                return;
            }

            if (!WithinLimit(p, st, now)) return;

            string exe = Util.NormPath(p.ExePath);
            if (!File.Exists(exe))
            {
                string msg = "program file not found: " + exe;
                Set(st, "error", msg);
                if (st.LastError != msg)
                {
                    lock (lk) { st.LastError = msg; }
                    log.Error(p.Name + ": " + msg);
                    RaiseAlert("program_problem", "program:" + p.Id + ":missing", p.Name + " cannot start", msg);
                }
                st.StartAt = now + 30;
                return;
            }

            // run as Local System, or with the user's full rights: only from a place just administrators can change
            if (!userSession || p.AllowElevation)
            {
                string why = PathTrust.Check(exe);
                if (why == null && p.WorkingDir.Length > 0)
                {
                    string wd = PathTrust.CheckFolder(p.WorkingDir);
                    if (wd != null) why = "start-in folder: " + wd;
                }
                if (why != null)
                {
                    string msg = "not started: " + why;
                    Set(st, "error", msg);
                    if (st.LastError != msg)
                    {
                        lock (lk) { st.LastError = msg; }
                        log.Error(p.Name + ": " + msg);
                        RaiseAlert("program_problem", "program:" + p.Id + ":untrusted", p.Name + " not started: the program is not protected", msg);
                    }
                    st.StartAt = now + 60;
                    return;
                }
            }

            if (dryRun)
            {
                Set(st, "waiting", "Dry-run: would start now");
                if (st.LastError != "dry")
                {
                    log.Info("[dry-run] " + p.Name + ": not running; would start " + exe + " " + p.Arguments);
                    lock (lk) { st.LastError = "dry"; }
                }
                return;
            }

            StartRequest r = new StartRequest();
            r.File = exe;
            r.Arguments = p.Arguments;
            r.WorkingDir = p.WorkingDir.Length > 0 ? Util.NormPath(p.WorkingDir) : Path.GetDirectoryName(exe);
            r.UserSession = userSession;
            r.AllowElevation = p.AllowElevation;
            r.Hidden = false;
            r.Priority = p.Priority;
            try
            {
                if (st.Last != null) { try { st.Last.Dispose(); } catch (Exception) { } }
                st.Last = launcher.Start(r);
                st.LastStartedAt = now;
                st.StartTimes.Add(now);
                st.Starts++;
                st.StartAt = now + p.RestartDelaySeconds;
                lock (lk) { st.LastError = ""; }
                if (p.Priority != "normal") Procs.SetPriority(st.Last.Pid, p.Priority);
                log.Info(p.Name + ": started " + exe + (p.Arguments.Length > 0 ? " " + p.Arguments : "") +
                         " (PID " + st.Last.Pid + (userSession ? ", session " + session : "") + ").");
                Set(st, "waiting", "Started PID " + st.Last.Pid);
            }
            catch (Exception ex)
            {
                string msg = "cannot start: " + ex.Message;
                Set(st, "error", msg);
                st.StartAt = now + Math.Max(30, p.RestartDelaySeconds);
                st.StartTimes.Add(now);
                if (st.LastError != msg)
                {
                    lock (lk) { st.LastError = msg; }
                    log.Error(p.Name + ": " + msg);
                    RaiseAlert("program_problem", "program:" + p.Id + ":start", p.Name + " cannot start", msg);
                }
            }
        }

        // ------------------------------------------------------------ Windows services
        void CheckService(ProgramJob p, Slot st, DateTime nowDt, double now, bool dryRun)
        {
            if (!p.Enabled)
            {
                Off(st);
                return;
            }
            st.Session = 0;
            if (!st.StartAtSet)
            {
                st.StartAt = now + p.StartDelaySeconds;
                st.StartAtSet = true;
            }
            string sig = p.Restart.Mode + "|" + p.Restart.Time + "|" + p.Restart.Day + "|" + Util.Num(p.Restart.IntervalMinutes, 3);
            if (sig != st.RestartSig)
            {
                st.RestartSig = sig;
                st.NextScheduled = p.Restart.Mode == "manual" ? null : JobManager.ComputeNext(p.Restart, nowDt, false);
            }
            string name = p.ServiceName;
            string status = services.Status(name);
            if (status == "missing")
            {
                string msg = "Windows service '" + name + "' not found on this computer";
                Set(st, "error", msg);
                st.WasRunning = false;
                if (st.LastError != msg)
                {
                    lock (lk) { st.LastError = msg; }
                    log.Error(p.Name + ": " + msg + ".");
                    RaiseAlert("program_problem", "program:" + p.Id + ":missing", p.Name + ": service not found", msg + ". Check the service name.");
                }
                return;
            }

            bool restartNow = false;
            if (st.NextScheduled.HasValue && nowDt >= st.NextScheduled.Value)
            {
                st.NextScheduled = JobManager.ComputeNext(p.Restart, nowDt, false);
                if (status == "running")
                {
                    log.Info(p.Name + ": scheduled restart of service " + name + ".");
                    restartNow = true;
                }
            }
            if (st.ManualRestart)
            {
                st.ManualRestart = false;
                restartNow = true;
                log.Info(p.Name + ": restart requested from the manager.");
            }
            if (restartNow)
            {
                if (dryRun) log.Info("[dry-run] " + p.Name + ": would stop and start service " + name + ".");
                else
                {
                    st.ExpectExit = true;
                    try { services.Stop(name, 30000); }
                    catch (Exception ex) { log.Warn(p.Name + ": could not stop service " + name + ": " + ex.Message); }
                    status = services.Status(name);
                    st.StartAt = now;
                }
            }

            if (status == "running")
            {
                if (!st.WasRunning)
                {
                    st.RunningSince = now;
                    log.Info(p.Name + ": service " + name + " running.");
                    Recover(p, false, p.Name + " is running again", "The Windows service " + name + " (" + p.Name + ") is running again.");
                }
                st.WasRunning = true;
                st.ExpectExit = false;
                lock (lk) { st.LastError = ""; }
                Set(st, "running", "Running");
                Health(p, st, now, dryRun, delegate
                {
                    st.ExpectExit = true;
                    try { services.Stop(name, 30000); }
                    catch (Exception ex) { log.Warn(p.Name + ": could not stop service " + name + ": " + ex.Message); }
                    st.StartAt = now;
                });
                return;
            }
            lock (lk) { st.Health = ""; st.HealthText = ""; }
            if (status == "start_pending" || status == "continue_pending")
            {
                Set(st, "waiting", "Service is starting");
                return;
            }
            if (status == "stop_pending" || status == "pause_pending")
            {
                Set(st, "waiting", "Service is stopping");
                return;
            }

            // stopped or paused
            if (st.WasRunning)
            {
                st.WasRunning = false;
                st.LastExit = now;
                st.RunningSince = 0;
                if (!st.ExpectExit)
                {
                    log.Warn(p.Name + ": service " + name + " is " + status + "; starting it again in " + p.RestartDelaySeconds + "s.");
                    RaiseAlert("program_restart", "program:" + p.Id, p.Name + " stopped",
                               "The Windows service " + name + " (" + p.Name + ") stopped and will be started again in " + p.RestartDelaySeconds + " seconds.");
                    st.StartAt = now + p.RestartDelaySeconds;
                }
                st.ExpectExit = false;
            }
            if (now < st.StartAt)
            {
                Set(st, "waiting", "Starting in " + ((int)Math.Ceiling(st.StartAt - now)) + "s");
                return;
            }
            if (!WithinLimit(p, st, now)) return;
            if (dryRun)
            {
                Set(st, "waiting", "Dry-run: would start the service now");
                if (st.LastError != "dry")
                {
                    log.Info("[dry-run] " + p.Name + ": service " + name + " is " + status + "; would start it.");
                    lock (lk) { st.LastError = "dry"; }
                }
                return;
            }
            try
            {
                services.Start(name);
                st.StartTimes.Add(now);
                st.Starts++;
                st.LastStartedAt = now;
                st.StartAt = now + Math.Max(p.RestartDelaySeconds, 15);
                lock (lk) { st.LastError = ""; }
                log.Info(p.Name + ": started service " + name + ".");
                Set(st, "waiting", "Start requested");
            }
            catch (Exception ex)
            {
                string msg = "cannot start service " + name + ": " + ex.Message;
                Set(st, "error", msg);
                st.StartAt = now + Math.Max(30, p.RestartDelaySeconds);
                st.StartTimes.Add(now);
                if (st.LastError != msg)
                {
                    lock (lk) { st.LastError = msg; }
                    log.Error(p.Name + ": " + msg);
                    RaiseAlert("program_problem", "program:" + p.Id + ":start", p.Name + " cannot start", msg);
                }
            }
        }

        bool WithinLimit(ProgramJob p, Slot st, double now)
        {
            st.StartTimes.RemoveAll(delegate(double t) { return now - t > 3600; });
            if (p.MaxRestartsPerHour > 0 && st.StartTimes.Count >= p.MaxRestartsPerHour)
            {
                double resume = st.StartTimes[0] + 3600;
                string text = "Started " + st.StartTimes.Count + " times in the last hour; paused until " +
                              Util.FromEpoch(resume).ToString("HH:mm", Util.Inv);
                Set(st, "paused", text);
                if (!st.PausedAlerted)
                {
                    st.PausedAlerted = true;
                    log.Error(p.Name + ": " + text + ".");
                    RaiseAlert("program_problem", "program:" + p.Id + ":paused", p.Name + " keeps stopping",
                               p.Name + " " + text.Substring(0, 1).ToLowerInvariant() + text.Substring(1) + ". Check it on the server.");
                }
                return false;
            }
            st.PausedAlerted = false;
            return true;
        }

        // --------------------------------------------------------------- health check
        delegate void Restarter();

        void Health(ProgramJob p, Slot st, double now, bool dryRun, Restarter restart)
        {
            if (!p.HealthEnabled || p.HealthUrl.Length == 0)
            {
                lock (lk) { st.Health = ""; st.HealthText = ""; }
                return;
            }
            double since = Math.Max(st.RunningSince, st.LastStartedAt);
            if (st.HealthEpoch != since)
            {
                st.HealthEpoch = since;
                st.HealthLastOk = since + p.HealthGraceSeconds;
                st.HealthFailing = false;
                st.HealthLastError = "";
                st.HealthLastCheck = 0;
            }
            if (now < since + p.HealthGraceSeconds)
            {
                lock (lk)
                {
                    st.Health = "waiting";
                    st.HealthText = "Health check starts in " + (int)Math.Ceiling(since + p.HealthGraceSeconds - now) + "s";
                }
                return;
            }

            // collect a finished probe
            string result = null;
            bool done = false;
            lock (lk)
            {
                if (st.HealthDone) { done = true; result = st.HealthResult; st.HealthDone = false; }
            }
            if (done)
            {
                if (result == null)
                {
                    st.HealthLastOk = now;
                    if (st.HealthFailing)
                    {
                        log.Info(p.Name + ": health check answers again (" + p.HealthUrl + ").");
                        Recover(p, true, p.Name + " answers again", p.Name + " answers its health check (" + p.HealthUrl + ") again.");
                    }
                    st.HealthFailing = false;
                    st.HealthLastError = "";
                }
                else
                {
                    if (st.HealthLastError != result) log.Warn(p.Name + ": health check failed (" + p.HealthUrl + "): " + result);
                    st.HealthLastError = result;
                }
            }

            // start the next probe
            bool busy;
            lock (lk) busy = st.HealthBusy;
            if (!busy && now - st.HealthLastCheck >= p.HealthIntervalSeconds)
            {
                st.HealthLastCheck = now;
                string url = p.HealthUrl;
                int timeout = Math.Min(10000, p.HealthIntervalSeconds * 1000);
                Func<string, int, string> probe = HealthProbe;
                string hUser = p.HealthUser ?? "", hPass = p.HealthPassword ?? "";
                if (hUser.Length > 0 && probe == (Func<string, int, string>)Net.Probe)
                {
                    bool plain = p.HealthAllowPlainLogin;
                    probe = delegate(string u, int t) { return Net.Probe(u, t, hUser, Secrets.Unprotect(hPass), plain); };
                }
                lock (lk) st.HealthBusy = true;
                WaitCallback work = delegate
                {
                    string r;
                    try { r = probe(url, timeout); }
                    catch (Exception ex) { r = ex.Message; }
                    lock (lk) { st.HealthResult = r; st.HealthDone = true; st.HealthBusy = false; }
                };
                if (HealthInline) work(null);
                else ThreadPool.QueueUserWorkItem(work);
            }

            double bad = now - st.HealthLastOk;
            if (bad >= p.HealthFailSeconds && !st.HealthFailing)
            {
                st.HealthFailing = true;
                string msg = p.Name + " has not answered its health check (" + p.HealthUrl + ") for " + (int)bad + " seconds" +
                             (st.HealthLastError.Length > 0 ? ": " + st.HealthLastError : "") + ".";
                log.Error(msg + (p.HealthAction == "restart" ? " Restarting it." : ""));
                RaiseAlert("program_problem", "program:" + p.Id + ":health", p.Name + " is not answering",
                           msg + (p.HealthAction == "restart" ? "\n\nIt is being restarted." : "\n\nIt is still running; check it on the server."));
                if (p.HealthAction == "restart")
                {
                    if (dryRun) log.Info("[dry-run] " + p.Name + ": would restart because the health check fails.");
                    else restart();
                }
            }
            lock (lk)
            {
                if (st.HealthFailing) { st.Health = "failing"; st.HealthText = "Not answering " + p.HealthUrl + (st.HealthLastError.Length > 0 ? ": " + st.HealthLastError : ""); }
                else if (st.HealthLastError.Length > 0) { st.Health = "ok"; st.HealthText = "Last check failed (" + st.HealthLastError + "), retrying"; }
                else { st.Health = "ok"; st.HealthText = "Answering " + p.HealthUrl; }
            }
        }

        /// <summary>Processes of this entry: the name, the session it runs in (the user's, or the service's own session 0),
        /// and the program file. A process another account started under the same name from somewhere else is not
        /// taken for this program (it neither hides a stopped program nor gets closed on a restart).</summary>
        List<ProcInfo> FindIn(ProgramJob p, string name, int session)
        {
            List<ProcInfo> all = table.Find(name);
            string exe = Util.NormPath(p.ExePath);
            string full = "", folder = "";
            try
            {
                full = Path.GetFullPath(exe);
                folder = Path.GetDirectoryName(full) ?? "";
            }
            catch (Exception) { }
            bool sameStem = string.Equals(Procs.Stem(name), Procs.Stem(exe), StringComparison.OrdinalIgnoreCase);
            return all.FindAll(delegate(ProcInfo i)
            {
                if (i.SessionId != session) return false;
                if (string.IsNullOrEmpty(i.Path) || full.Length == 0) return true;       // the path cannot be read: the name decides
                if (sameStem) return string.Equals(i.Path, full, StringComparison.OrdinalIgnoreCase);
                // a configured process name of another program (a launcher starts it): it must come from the same folder tree
                return folder.Length > 0 && i.Path.StartsWith(folder.TrimEnd('\\', '/') + (Util.IsWindows ? "\\" : "/"), StringComparison.OrdinalIgnoreCase);
            });
        }

        /// <summary>Close the program (if running) and start it again on the next check.</summary>
        public bool RequestRestart(string id)
        {
            lock (lk)
            {
                Slot st;
                if (!states.TryGetValue(id, out st)) return false;
                st.ManualRestart = true;
                st.StartTimes.Clear();
                st.PausedAlerted = false;
                st.StartAt = 0;
                return true;
            }
        }

        /// <summary>Studio switch: make the next check start this entry at once (no first-start delay).</summary>
        public void StartSoon(string id)
        {
            lock (lk)
            {
                Slot st;
                if (!states.TryGetValue(id, out st)) { st = new Slot(); states[id] = st; }
                st.StartAt = 0;
                st.StartAtSet = true;
                st.StartTimes.Clear();
                st.PausedAlerted = false;
            }
        }

        /// <summary>Studio switch: close every process of this entry (or stop its Windows service). A program in the
        /// signed-in session is first asked to close as if its window were closed (taskkill without /F, run in that
        /// session), and ended after timeoutSeconds if it is still running. Null when nothing of it runs any more,
        /// otherwise what is still running. Call while the entry is not supervised (outside the active studio).</summary>
        public string CloseForSwitch(ProgramJob p, int timeoutSeconds, Func<bool> cancelled)
        {
            Slot st;
            lock (lk)
            {
                if (!states.TryGetValue(p.Id, out st)) { st = new Slot(); states[p.Id] = st; }
                st.ExpectExit = true;
            }
            if (p.Kind == "service")
            {
                string status = services.Status(p.ServiceName);
                if (status == "missing" || status == "stopped") return null;
                log.Info(p.Name + ": stopping the Windows service " + p.ServiceName + " (studio switch).");
                try { services.Stop(p.ServiceName, Math.Max(5, timeoutSeconds) * 1000); }
                catch (Exception ex) { return p.Name + ": the service did not stop: " + ex.Message; }
                status = services.Status(p.ServiceName);
                return status == "stopped" || status == "missing" ? null : p.Name + ": the service is still " + status;
            }
            string name = ProcessNameOf(p);
            List<int> sessionsToCheck = new List<int>();
            string user;
            int console = sessions.ConsoleUserSession(out user);
            if (p.RunAs == "user") { if (console >= 0) sessionsToCheck.Add(console); }
            else sessionsToCheck.Add(0);
            if (st.Session >= 0 && !sessionsToCheck.Contains(st.Session)) sessionsToCheck.Add(st.Session);
            List<ProcInfo> found = new List<ProcInfo>();
            foreach (int sid in sessionsToCheck) found.AddRange(FindIn(p, name, sid));
            if (found.Count == 0) return null;
            log.Info(p.Name + ": closing " + found.Count + " process(es) for the studio switch.");
            bool asked = false;
            foreach (ProcInfo pi in found)
            {
                if (pi.SessionId <= 0) continue;           // no windows in the service session: ended below
                try
                {
                    StartRequest r = new StartRequest();
                    r.File = Util.SystemExe("taskkill.exe");
                    r.Arguments = "/PID " + pi.Pid;
                    r.UserSession = true;
                    r.Hidden = true;
                    using (IRunning t = launcher.Start(r)) t.WaitForExit(5000);
                    asked = true;
                }
                catch (Exception ex) { log.Warn(p.Name + ": could not ask PID " + pi.Pid + " to close: " + ex.Message); }
            }
            double until = Util.Now() + (asked ? Math.Max(1, timeoutSeconds) : 0);
            while (Util.Now() < until)
            {
                if (cancelled != null && cancelled()) break;
                if (StillRunning(p, name, sessionsToCheck).Count == 0) { log.Info(p.Name + ": closed."); return null; }
                Thread.Sleep(250);
            }
            List<ProcInfo> left = StillRunning(p, name, sessionsToCheck);
            foreach (ProcInfo pi in left)
            {
                try { table.Kill(pi.Pid); }
                catch (Exception ex) { log.Warn(p.Name + ": could not end PID " + pi.Pid + ": " + ex.Message); }
            }
            if (left.Count > 0) log.Warn(p.Name + ": ended " + left.Count + " process(es) that did not close" + (asked ? " within " + timeoutSeconds + "s" : "") + ".");
            for (int i = 0; i < 20; i++)
            {
                left = StillRunning(p, name, sessionsToCheck);
                if (left.Count == 0) return null;
                Thread.Sleep(250);
            }
            return p.Name + ": still running (PID " + left[0].Pid + ") after it was ended";
        }

        List<ProcInfo> StillRunning(ProgramJob p, string name, List<int> sessionIds)
        {
            List<ProcInfo> o = new List<ProcInfo>();
            foreach (int sid in sessionIds) o.AddRange(FindIn(p, name, sid));
            return o;
        }

        public List<ProgramStatus> Snapshot()
        {
            AppConfig cfg = config();
            List<ProgramStatus> o = new List<ProgramStatus>();
            double now = Util.Now();
            lock (lk)
            {
                foreach (ProgramJob p in cfg.Programs)
                {
                    ProgramStatus s = new ProgramStatus();
                    s.Id = p.Id;
                    s.Name = p.Name;
                    s.Enabled = p.Enabled && cfg.General.Modules.Programs;
                    s.Kind = p.Kind;
                    try { s.InStudio = InStudio(p); } catch (Exception) { s.InStudio = true; }
                    Slot st;
                    if (states.TryGetValue(p.Id, out st))
                    {
                        s.State = p.Enabled ? st.State : "off";
                        s.StateText = p.Enabled ? st.StateText : "Not supervised";

                        s.Pid = st.Pid;
                        s.Session = st.Session;
                        s.RunningSince = st.RunningSince;
                        s.Starts = st.Starts;
                        int n = 0;
                        foreach (double t in st.StartTimes) if (now - t <= 3600) n++;
                        s.StartsLastHour = n;
                        s.LastExit = st.LastExit;
                        s.LastError = st.LastError == "dry" ? "" : st.LastError;
                        s.NextAction = st.State == "waiting" && st.StartAt > now ? st.StartAt : 0;
                        s.NextScheduledRestart = st.NextScheduled.HasValue ? Util.ToEpoch(st.NextScheduled.Value) : 0;
                        s.Health = st.Health;
                        s.HealthText = st.HealthText;
                    }
                    else
                    {
                        s.State = p.Enabled ? "waiting" : "off";
                        s.StateText = p.Enabled ? "Not checked yet" : "Not supervised";
                    }
                    o.Add(s);
                }
            }
            return o;
        }
    }
}
