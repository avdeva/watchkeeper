// Watchdog: keeps programs and Windows services running.
//
// Every 2 seconds each switched-on entry is looked at:
//   1. Is it running? What counts is chosen per entry (Running means): the application to watch, the program started
//      (a launcher, by the PID it got), either, or both. A program without an application to watch is simply itself.
//   2. Running: nothing to do except the optional health check and the daily restart.
//   3. Not running: after the restart delay it is started again (in the signed-in user's session for desktop programs),
//      then it gets "wait for it to appear" seconds before that start counts as failed. At most N starts an hour.
// Every change of state is logged in plain words and shown in the manager; problems raise alerts.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

namespace Watchkeeper
{
    public class ProgramStatus
    {
        public string Id = "";
        public string Name = "";
        public string Kind = "program";
        public bool Enabled;
        /// <summary>off | waiting | starting | running | paused | error</summary>
        public string State = "off";
        public string Text = "";
        public int Pid;
        public double RunningSince;
        public int StartsLastHour;
        public int Starts;
        public string LastProblem = "";
        public double LastProblemAt;
        /// <summary>"" (no check) | waiting | ok | failing</summary>
        public string Health = "";
        public string HealthText = "";
        public double NextRestart;
    }

    public class Watchdog
    {
        class Slot
        {
            public string Signature = "";
            public string State = "off", Text = "";
            public int Session = -2;
            public double StartAt = -1;
            public bool WasRunning, ExpectExit;
            public double RunningSince;
            public int Pid;
            public IRunning Launched;
            public double LaunchedAt;          // > 0 while waiting for a start to show up
            public double PartialSince;
            public double PendingSince;           // a service in "starting" / "stopping" since
            public List<double> StartTimes = new List<double>();
            public int Starts;
            public bool LimitAlerted;
            public string LastProblem = "", LastRaised = "";
            public double LastProblemAt;
            public DateTime? NextRestart;
            public string RestartSig = "";
            public bool RestartRequested;
            public volatile bool Busy;         // closing on a background thread
            // health
            public double HealthLastOk, HealthLastCheck;
            public volatile bool HealthBusy;
            public string HealthError = "";
            public bool HealthFailing;
            public string Health = "", HealthText = "";
        }

        readonly Func<Settings> settings;
        readonly IProcesses procs;
        readonly IServices services;
        readonly Logger log;
        readonly IAlertSink alerts;
        readonly object lk = new object();
        readonly Dictionary<string, Slot> slots = new Dictionary<string, Slot>();
        int lastSession = -2;

        /// <summary>How long a program gets to close when asked (restart, daily restart) before it is ended.</summary>
        public int CloseWaitMs = 10000;
        /// <summary>Health checks run on a thread of their own; tests run them inline.</summary>
        public bool InlineHealth;

        public Watchdog(Func<Settings> settings, IProcesses procs, IServices services, Logger log, IAlertSink alerts)
        {
            this.settings = settings;
            this.procs = procs;
            this.services = services;
            this.log = log;
            this.alerts = alerts;
        }

        // ---------------------------------------------------------------- what counts as running
        /// <summary>The effective choice: app | started | either | both. A program without an application to watch is itself (app);
        /// a service without one is the service (started).</summary>
        public static string Mode(ProgramEntry p)
        {
            if (p.WatchPath.Length == 0) return p.Kind == "service" ? "started" : "app";
            if (p.RunningMeans == "auto") return p.Kind == "service" ? "both" : "app";
            return p.RunningMeans;
        }

        static string WatchedFile(ProgramEntry p) { return p.WatchPath.Length > 0 ? p.WatchPath : p.Path; }

        public static string WatchedName(ProgramEntry p)
        {
            string f = Util.NormPath(WatchedFile(p)).Replace('/', '\\');
            string name = f.Substring(f.LastIndexOf('\\') + 1);
            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 4) : name;
        }

        /// <summary>Does a process file match the watched path? * stands for any part of one folder name. An empty
        /// process path (Windows did not say) matches on the name alone.</summary>
        public static bool PathMatches(string processPath, string watched)
        {
            if (string.IsNullOrEmpty(processPath)) return true;
            string w = Util.NormPath(watched);
            if (w.IndexOf('*') < 0)
            {
                try { return Util.SamePath(processPath, w); }
                catch (Exception) { return false; }
            }
            System.Text.StringBuilder sb = new System.Text.StringBuilder("^");
            foreach (char c in w)
            {
                if (c == '*') sb.Append(@"[^\\/]*");
                else if (c == '\\' || c == '/') sb.Append(@"[\\/]");
                else sb.Append(Regex.Escape(c.ToString()));
            }
            sb.Append('$');
            return Regex.IsMatch(processPath, sb.ToString(), RegexOptions.IgnoreCase);
        }

        /// <summary>session: the one to look in, or -1 for any.</summary>
        List<ProcInfo> FindFile(string file, int session)
        {
            List<ProcInfo> o = new List<ProcInfo>();
            if (string.IsNullOrEmpty(file)) return o;
            foreach (ProcInfo i in procs.Find(Procs.Stem(Util.NormPath(file).Replace('/', '\\'))))
                if ((session < 0 || i.Session == session) && PathMatches(i.Path, file)) o.Add(i);
            return o;
        }

        static bool Alive(IRunning r)
        {
            if (r == null) return false;
            try { return !r.WaitForExit(0); } catch (Exception) { return false; }
        }

        class Seen
        {
            public List<ProcInfo> App = new List<ProcInfo>();
            public List<ProcInfo> Started = new List<ProcInfo>();
            public bool ServiceUp;
            public int OtherSession = -1;
            public string ServiceStatus = "";
            public bool Running;
            /// <summary>"both": the one that is missing while the other runs.</summary>
            public string Missing;
            public List<ProcInfo> All()
            {
                List<ProcInfo> o = new List<ProcInfo>(App);
                foreach (ProcInfo x in Started) if (!o.Exists(delegate(ProcInfo y) { return y.Pid == x.Pid; })) o.Add(x);
                return o;
            }
        }

        Seen Look(ProgramEntry p, Slot st, int session)
        {
            Seen s = new Seen();
            string mode = Mode(p);
            if (p.Kind == "service")
            {
                s.ServiceStatus = services.Status(p.ServiceName);
                s.ServiceUp = s.ServiceStatus == "running";
                if (p.WatchPath.Length > 0) s.App = FindFile(p.WatchPath, -1);
            }
            else
            {
                s.App = FindFile(WatchedFile(p), session);
                if (s.App.Count == 0 && p.RunIn == "desktop")
                {
                    // already running in another user's session (the session changed): never start a second copy
                    List<ProcInfo> elsewhere = FindFile(WatchedFile(p), -1).FindAll(delegate(ProcInfo x) { return x.Session > 0 && x.Session != session; });
                    if (elsewhere.Count > 0) { s.App = elsewhere; s.OtherSession = elsewhere[0].Session; }
                }
                if (mode != "app")
                {
                    if (Alive(st.Launched))
                    {
                        ProcInfo i = new ProcInfo();
                        i.Pid = st.Launched.Pid;
                        i.Session = session;
                        i.Name = Procs.Stem(p.Path);
                        i.Path = p.Path;
                        s.Started.Add(i);
                    }
                    else s.Started = FindFile(p.Path, session);         // started before this service did
                }
            }
            bool started = p.Kind == "service" ? s.ServiceUp : s.Started.Count > 0;
            bool app = s.App.Count > 0;
            switch (mode)
            {
                case "app": s.Running = app; break;
                case "started": s.Running = started; break;
                case "either": s.Running = app || started; break;
                default:
                    s.Running = app && started;
                    if (app && !started) s.Missing = p.Kind == "service" ? "the service" : "the launcher";
                    else if (started && !app) s.Missing = "'" + WatchedName(p) + "'";
                    break;
            }
            return s;
        }

        // ---------------------------------------------------------------- the check
        public void Tick(DateTime nowDt)
        {
            Settings cfg = settings();
            double now = Util.ToEpoch(nowDt);
            string user;
            int session = -1;
            bool anyDesktop = cfg.Watchdog.Exists(delegate(ProgramEntry x) { return x.Enabled && x.Kind == "program" && x.RunIn == "desktop"; });
            if (anyDesktop)
            {
                session = procs.DesktopSession(out user);
                if (session != lastSession)
                {
                    if (session >= 0) log.Info("Signed-in session: " + session + " (" + user + ").");
                    else if (lastSession >= 0) log.Warn("Nobody is signed in; desktop programs wait for a sign-in.");
                    lastSession = session;
                }
            }
            HashSet<string> ids = new HashSet<string>();
            foreach (ProgramEntry p in cfg.Watchdog)
            {
                ids.Add(p.Id);
                Slot st;
                lock (lk) { if (!slots.TryGetValue(p.Id, out st)) { st = new Slot(); slots[p.Id] = st; } }
                if (st.Busy) continue;
                try { Check(p, st, nowDt, now, p.Kind == "program" && p.RunIn == "desktop" ? session : 0); }
                catch (Exception ex)
                {
                    Set(st, "error", ex.Message);
                    log.Error(p.Name + ": " + ex.Message);
                }
            }
            lock (lk)
            {
                List<string> gone = new List<string>();
                foreach (string k in slots.Keys) if (!ids.Contains(k)) gone.Add(k);
                foreach (string k in gone) { DisposeLaunched(slots[k]); slots.Remove(k); }
            }
        }

        void Set(Slot st, string state, string text) { lock (lk) { st.State = state; st.Text = text; } }

        void Problem(ProgramEntry p, Slot st, string key, string level, string title, string text)
        {
            bool repeat = st.LastRaised == text;
            lock (lk) { st.LastProblem = text; st.LastProblemAt = Util.Now(); st.LastRaised = text; }
            if (repeat) return;
            if (level == "error") log.Error(p.Name + ": " + text); else log.Warn(p.Name + ": " + text);
            alerts.Raise("watchdog", "watchdog:" + p.Id + ":" + key, level, title, text);
        }

        static void DisposeLaunched(Slot st)
        {
            if (st.Launched != null) { try { st.Launched.Dispose(); } catch (Exception) { } st.Launched = null; }
        }

        void Off(Slot st)
        {
            DisposeLaunched(st);
            Set(st, "off", "Switched off");
            st.WasRunning = false;
            st.LaunchedAt = 0;
            st.StartAt = -1;
            st.PartialSince = 0;
            st.Session = -2;
            st.Pid = 0;
            st.RunningSince = 0;
            lock (lk) { st.Health = ""; st.HealthText = ""; }
        }

        static string Sig(ProgramEntry p)
        {
            return p.Kind + "|" + p.Path + "|" + p.Arguments + "|" + p.WatchPath + "|" + p.ServiceName + "|" + p.RunIn + "|" + Mode(p);
        }

        void Check(ProgramEntry p, Slot st, DateTime nowDt, double now, int session)
        {
            if (!p.Enabled) { Off(st); return; }
            string sig = Sig(p);
            if (sig != st.Signature)
            {
                if (st.Signature.Length > 0) { log.Info(p.Name + ": settings changed."); DisposeLaunched(st); st.LaunchedAt = 0; st.LastRaised = ""; }
                st.Signature = sig;
                st.StartAt = -1;
            }
            bool desktop = p.Kind == "program" && p.RunIn == "desktop";
            if (desktop && session < 0)
            {
                Set(st, "waiting", "Waiting for a user to sign in (auto-logon)");
                st.WasRunning = false;           // the session ended: not a crash
                st.Session = -1;
                st.StartAt = -1;
                st.LaunchedAt = 0;
                st.Pid = 0;
                return;
            }
            if (st.Session != session) { st.Session = session; st.StartAt = -1; }
            if (st.StartAt < 0) st.StartAt = now + p.StartDelaySeconds;

            // daily restart
            string rsig = p.RestartAt + "|" + string.Join(",", p.RestartDays.ToArray());
            if (rsig != st.RestartSig) { st.RestartSig = rsig; st.NextRestart = NextRestart(p, nowDt); }

            Seen seen = Look(p, st, session);
            if (p.Kind == "service" && seen.ServiceStatus == "missing")
            {
                Set(st, "error", "Windows service '" + p.ServiceName + "' not found on this computer");
                Problem(p, st, "start", "error", p.Name + ": service not found", "the Windows service '" + p.ServiceName + "' does not exist on this computer; check the service name");
                st.WasRunning = false;
                return;
            }

            string restartWhy = null;
            if (st.NextRestart.HasValue && nowDt >= st.NextRestart.Value)
            {
                st.NextRestart = NextRestart(p, nowDt.AddSeconds(1));
                if (seen.Running || seen.All().Count > 0) restartWhy = "daily restart";
            }
            if (st.RestartRequested) { st.RestartRequested = false; restartWhy = "restart requested in the manager"; }
            if (restartWhy != null)
            {
                log.Info(p.Name + ": " + restartWhy + ".");
                Restart(p, st, seen, restartWhy, true);
                return;
            }

            if (seen.Running)
            {
                List<ProcInfo> all = seen.All();
                int pid = all.Count > 0 ? all[0].Pid : 0;
                if (!st.WasRunning)
                {
                    st.RunningSince = now;
                    log.Info(p.Name + ": running" + Pids(p, seen) + ".");
                    string back = p.Name + " is running again" + Pids(p, seen) + ".";
                    alerts.Recover("watchdog:" + p.Id + ":stopped", p.Name + " is running again", back);
                    alerts.Recover("watchdog:" + p.Id + ":start", p.Name + " is running again", back);
                    alerts.Recover("watchdog:" + p.Id + ":limit", p.Name + " is running again", back);
                    st.HealthLastOk = now;
                    st.HealthFailing = false;
                }
                st.WasRunning = true;
                st.ExpectExit = false;
                st.LaunchedAt = 0;
                st.PartialSince = 0;
                st.Pid = pid;
                st.LastRaised = "";
                st.LimitAlerted = false;
                Set(st, "running", "Running" + Pids(p, seen));
                Health(p, st, now, seen);
                return;
            }
            lock (lk) { st.Health = ""; st.HealthText = ""; }

            // "both": one runs without the other
            if (seen.Missing != null)
            {
                if (st.PartialSince == 0) st.PartialSince = now;
                double waited = now - st.PartialSince;
                string have = p.Kind == "service" && seen.ServiceUp ? "the service" : seen.App.Count > 0 ? "'" + WatchedName(p) + "'" : "the launcher";
                if (waited < p.LauncherWaitSeconds)
                {
                    Set(st, "starting", Cap(have) + " is running; waiting for " + seen.Missing + " (" + (int)waited + " of " + p.LauncherWaitSeconds + "s)");
                    return;
                }
                st.PartialSince = 0;
                Problem(p, st, "stopped", "warning", p.Name + " stopped",
                        have + " ran without " + seen.Missing + " for " + p.LauncherWaitSeconds + "s; both must run, so everything is closed and started again");
                st.WasRunning = false;
                Restart(p, st, seen, "only one of the two was running", false);
                return;
            }
            st.PartialSince = 0;

            if (st.WasRunning)
            {
                st.WasRunning = false;
                st.RunningSince = 0;
                st.Pid = 0;
                if (!st.ExpectExit)
                {
                    st.LastRaised = "";
                    Problem(p, st, "stopped", "warning", p.Name + " stopped",
                            "stopped running; it is started again in " + p.RestartDelaySeconds + "s");
                    st.StartAt = now + p.RestartDelaySeconds;
                }
                st.ExpectExit = false;
            }

            // a start in progress: give it time to appear
            if (st.LaunchedAt > 0)
            {
                double waited = now - st.LaunchedAt;
                bool launcherAlive = p.Kind == "program" && Alive(st.Launched);
                if (p.Kind == "service" && seen.ServiceStatus == "starting")
                {
                    if (!StuckService(p, st, now, seen)) Set(st, "starting", "The service is starting (" + (int)(now - st.PendingSince) + "s)");
                    return;
                }
                if (waited < p.LauncherWaitSeconds)
                {
                    string what = p.Kind == "service" ? "Service started" : p.WatchPath.Length > 0 ? "Launcher started" + (st.Launched != null ? " (PID " + st.Launched.Pid + ")" : "") : "Started" + (st.Launched != null ? " (PID " + st.Launched.Pid + ")" : "");
                    Set(st, "starting", what + "; waiting for " + (Mode(p) == "started" ? "it" : "'" + WatchedName(p) + "'") + " to appear (" + (int)waited + " of " + p.LauncherWaitSeconds + "s)");
                    return;
                }
                st.LaunchedAt = 0;
                string msg = (Mode(p) == "started" ? "the started program" : "'" + WatchedName(p) + "'") + " did not appear within " + p.LauncherWaitSeconds + "s of starting it" +
                             (launcherAlive ? "; the program that was started (PID " + st.Launched.Pid + ") is still running and is ended" : "") +
                             (p.WatchPath.Length > 0 ? ". Check 'Application to watch' (" + p.WatchPath + ")" : "") + "; trying again in " + p.RestartDelaySeconds + "s";
                Problem(p, st, "start", "error", p.Name + " did not start", msg);
                if (launcherAlive) { try { st.Launched.Kill(); } catch (Exception) { } }
                DisposeLaunched(st);
                st.StartAt = now + p.RestartDelaySeconds;
            }

            if (p.Kind == "service" && (seen.ServiceStatus == "starting" || seen.ServiceStatus == "stopping"))
            {
                if (StuckService(p, st, now, seen)) return;
                Set(st, "waiting", "The service is " + seen.ServiceStatus + " (" + (int)(now - st.PendingSince) + "s)");
                return;
            }
            st.PendingSince = 0;

            if (now < st.StartAt)
            {
                Set(st, "waiting", "Starting in " + (int)Math.Ceiling(st.StartAt - now) + "s");
                return;
            }

            // limit
            st.StartTimes.RemoveAll(delegate(double t) { return now - t >= 3600; });
            if (p.MaxRestartsPerHour > 0 && st.StartTimes.Count >= p.MaxRestartsPerHour)
            {
                double until = st.StartTimes[0] + 3600;
                string text = "Paused: started " + st.StartTimes.Count + " times in the last hour; tries again at " + Util.FromEpoch(until).ToString("HH:mm", Util.Inv);
                Set(st, "paused", text);
                if (!st.LimitAlerted)
                {
                    st.LimitAlerted = true;
                    log.Error(p.Name + ": " + text + ".");
                    alerts.Raise("watchdog", "watchdog:" + p.Id + ":limit", "error", p.Name + " keeps stopping",
                                 p.Name + " was started " + st.StartTimes.Count + " times in the last hour and keeps stopping. Watchkeeper stops trying until " +
                                 Util.FromEpoch(until).ToString("HH:mm", Util.Inv) + ". Last problem: " + (st.LastProblem.Length > 0 ? st.LastProblem : "it exits by itself"));
                }
                return;
            }

            Start(p, st, now, session, seen);
        }

        void Start(ProgramEntry p, Slot st, double now, int session, Seen seen)
        {
            if (p.Kind == "service")
            {
                // stopping and starting a service can take a while: on a thread of its own, so the other entries are not held up
                bool stopFirst = seen.ServiceUp;
                st.StartTimes.Add(now);
                st.Starts++;
                st.LaunchedAt = now;
                st.StartAt = now + p.RestartDelaySeconds;
                Set(st, "starting", "Starting the service");
                string name = p.ServiceName, entry = p.Name, watched = WatchedName(p);
                int retry = Math.Max(30, p.RestartDelaySeconds);
                ProgramEntry copy = new ProgramEntry();
                Json.FillFrom(copy, Json.ToJson(p));
                Busy(st, "service-" + name, delegate
                {
                    try
                    {
                        if (stopFirst)
                        {
                            // the service runs, the application does not (Running means: the application)
                            log.Info(entry + ": the service runs without '" + watched + "'; stopping it to start it again.");
                            services.Stop(name, 30000);
                        }
                        services.Start(name);
                        log.Info(entry + ": started the Windows service " + name + ".");
                        Set(st, "starting", "Service start requested");
                    }
                    catch (Exception ex)
                    {
                        st.LaunchedAt = 0;
                        st.StartAt = Util.Now() + retry;
                        Set(st, "error", "Cannot start the service: " + ex.Message);
                        Problem(copy, st, "start", "error", entry + " cannot start", "cannot start the Windows service " + name + ": " + ex.Message);
                    }
                });
                return;
            }

            string exe = Util.NormPath(p.Path);
            if (!File.Exists(exe))
            {
                Set(st, "error", "Program file not found: " + exe);
                Problem(p, st, "start", "error", p.Name + " cannot start", "the program file " + exe + " does not exist");
                st.StartAt = now + 30;
                return;
            }
            // a launcher of an earlier start that is still running would start a second copy
            if (Alive(st.Launched)) { try { st.Launched.Kill(); } catch (Exception) { } }
            foreach (ProcInfo x in seen.Started) { try { procs.Kill(x.Pid); } catch (Exception) { } }
            DisposeLaunched(st);
            StartSpec spec = new StartSpec();
            spec.File = exe;
            spec.Arguments = p.Arguments;
            spec.StartIn = p.StartIn.Length > 0 ? Util.NormPath(p.StartIn) : Path.GetDirectoryName(exe);
            spec.Desktop = p.RunIn == "desktop";
            spec.Elevated = p.Elevated;
            spec.Hidden = p.RunIn != "desktop";
            spec.Priority = p.Priority;
            st.StartTimes.Add(now);
            st.Starts++;
            try
            {
                st.Launched = procs.Start(spec);
                st.LaunchedAt = now;
                st.StartAt = now + p.RestartDelaySeconds;
                log.Info(p.Name + ": started " + exe + (p.Arguments.Length > 0 ? " " + p.Arguments : "") + " (PID " + st.Launched.Pid +
                         (spec.Desktop ? ", session " + session : "") + ").");
                Set(st, "starting", "Started (PID " + st.Launched.Pid + ")");
            }
            catch (Exception ex)
            {
                st.StartAt = now + Math.Max(30, p.RestartDelaySeconds);
                Set(st, "error", "Cannot start: " + ex.Message);
                Problem(p, st, "start", "error", p.Name + " cannot start", "cannot start " + exe + ": " + ex.Message);
            }
        }

        /// <summary>Run work on a thread of its own while the entry is not checked (tests: InlineRestart waits for it).</summary>
        void Busy(Slot st, string name, ThreadStart work)
        {
            st.Busy = true;
            Thread t = new Thread(delegate()
            {
                try { work(); }
                catch (Exception ex) { log.Error(name + ": " + ex.Message); }
                finally { st.Busy = false; }
            });
            t.IsBackground = true;
            t.Name = name;
            t.Start();
            if (InlineRestart) t.Join();
        }

        /// <summary>A Windows service stuck in "starting" or "stopping" longer than the wait: its process is ended, so
        /// Windows sees it stopped, and it is started again. True when it was ended now.</summary>
        bool StuckService(ProgramEntry p, Slot st, double now, Seen seen)
        {
            if (st.PendingSince == 0) st.PendingSince = now;
            double limit = Math.Max(60, p.LauncherWaitSeconds);
            if (now - st.PendingSince < limit) return false;
            st.PendingSince = 0;
            string name = p.ServiceName;
            int pid = services.Pid(name);
            Problem(p, st, "start", "error", p.Name + ": service stuck",
                    "the Windows service " + name + " has been '" + seen.ServiceStatus + "' for " + (int)limit + "s" +
                    (pid > 0 ? "; its process (PID " + pid + ") is ended and the service started again" : "; its process cannot be found, so it cannot be ended"));
            if (pid <= 0) return false;
            st.LaunchedAt = 0;
            st.WasRunning = false;
            st.StartAt = now;
            Busy(st, "end-" + name, delegate
            {
                try { procs.Kill(pid); }
                catch (Exception ex) { log.Warn(p.Name + ": could not end PID " + pid + ": " + ex.Message); }
            });
            return true;
        }

        static string Cap(string t) { return t.Length == 0 ? t : t.Substring(0, 1).ToUpperInvariant() + t.Substring(1); }

        string Pids(ProgramEntry p, Seen s)
        {
            List<string> parts = new List<string>();
            if (s.App.Count > 0)
            {
                string label = p.WatchPath.Length > 0 ? "application PID " : "PID ";
                parts.Add(label + s.App[0].Pid + (s.App.Count > 1 ? " (+" + (s.App.Count - 1) + " more)" : ""));
            }
            if (s.OtherSession >= 0) parts.Add("in session " + s.OtherSession + ", not the signed-in user's: no second copy is started");
            if (p.Kind == "service") parts.Add("service " + s.ServiceStatus);
            else if (s.Started.Count > 0 && !s.App.Exists(delegate(ProcInfo x) { return x.Pid == s.Started[0].Pid; }))
                parts.Add("launcher PID " + s.Started[0].Pid);
            return parts.Count == 0 ? "" : " (" + string.Join(", ", parts.ToArray()) + ")";
        }

        // ---------------------------------------------------------------- restart
        /// <summary>Close everything of the entry on a thread of its own (programs with a window are asked to close
        /// first, then ended), then start again at once.</summary>
        void Restart(ProgramEntry p, Slot st, Seen seen, string why, bool expected)
        {
            st.ExpectExit = expected;
            st.Busy = true;
            Set(st, "starting", "Closing (" + why + ")");
            ProgramEntry copy = new ProgramEntry();
            Json.FillFrom(copy, Json.ToJson(p));
            Thread t = new Thread(delegate()
            {
                try { CloseAll(copy, st, seen); }
                catch (Exception ex) { log.Warn(copy.Name + ": closing: " + ex.Message); }
                finally
                {
                    st.WasRunning = false;
                    st.RunningSince = 0;
                    st.LaunchedAt = 0;
                    st.StartAt = Util.Now();
                    st.Busy = false;
                }
            });
            t.IsBackground = true;
            t.Name = "close-" + p.Name;
            t.Start();
            if (InlineRestart) t.Join();
        }

        /// <summary>Tests: wait for closing to finish inside Tick.</summary>
        public bool InlineRestart;

        void CloseAll(ProgramEntry p, Slot st, Seen seen)
        {
            if (p.Kind == "service")
            {
                try { if (services.Status(p.ServiceName) != "stopped") services.Stop(p.ServiceName, 30000); }
                catch (Exception ex) { log.Warn(p.Name + ": could not stop the service: " + ex.Message); }
            }
            List<ProcInfo> all = seen.All();
            if (Alive(st.Launched) && !all.Exists(delegate(ProcInfo x) { return x.Pid == st.Launched.Pid; }))
            {
                try { st.Launched.Kill(); } catch (Exception) { }
            }
            bool asked = false;
            foreach (ProcInfo x in all)
            {
                if (x.Session <= 0) continue;            // no windows outside the desktop session
                try
                {
                    StartSpec ask = new StartSpec();
                    ask.File = Util.SystemExe("taskkill.exe");
                    ask.Arguments = "/PID " + x.Pid;
                    ask.Desktop = true;
                    ask.Hidden = true;
                    ask.ElevatedFirst = p.Elevated;     // a program with full rights only takes a close request from one with full rights
                    using (IRunning r = procs.Start(ask)) r.WaitForExit(5000);
                    asked = true;
                }
                catch (Exception) { }
            }
            DateTime until = DateTime.Now.AddMilliseconds(asked ? CloseWaitMs : 0);
            List<ProcInfo> left = StillRunning(all);
            while (left.Count > 0 && DateTime.Now < until)
            {
                Thread.Sleep(250);
                left = StillRunning(all);
            }
            foreach (ProcInfo x in left)
            {
                try { procs.Kill(x.Pid); }
                catch (Exception ex) { log.Warn(p.Name + ": could not end PID " + x.Pid + ": " + ex.Message); }
            }
            if (all.Count > 0) log.Info(p.Name + ": closed" + (left.Count > 0 ? " (" + left.Count + " process(es) had to be ended)" : "") + ".");
            DisposeLaunched(st);
        }

        List<ProcInfo> StillRunning(List<ProcInfo> list)
        {
            List<ProcInfo> o = new List<ProcInfo>();
            foreach (ProcInfo x in list)
            {
                foreach (ProcInfo y in procs.Find(x.Name))
                    if (y.Pid == x.Pid) { o.Add(x); break; }
            }
            return o;
        }

        public bool RequestRestart(string id)
        {
            lock (lk)
            {
                Slot st;
                if (!slots.TryGetValue(id, out st)) return false;
                st.RestartRequested = true;
                st.StartTimes.Clear();
                st.LimitAlerted = false;
                return true;
            }
        }

        public static DateTime? NextRestart(ProgramEntry p, DateTime after)
        {
            TimeSpan? at = Util.ParseTime(p.RestartAt);
            if (!at.HasValue) return null;
            for (int i = 0; i < 8; i++)
            {
                DateTime t = after.Date.AddDays(i) + at.Value;
                if (t <= after) continue;
                string day = SettingsFile.DayNames[((int)t.DayOfWeek + 6) % 7];
                if (p.RestartDays.Count == 0 || p.RestartDays.Contains(day)) return t;
            }
            return null;
        }

        // ---------------------------------------------------------------- health check
        void Health(ProgramEntry p, Slot st, double now, Seen seen)
        {
            if (p.HealthUrl.Length == 0) { lock (lk) { st.Health = ""; st.HealthText = ""; } return; }
            double startedFor = now - st.RunningSince;
            if (startedFor < p.HealthGraceSeconds)
            {
                st.HealthLastOk = now;
                lock (lk) { st.Health = "waiting"; st.HealthText = "First check in " + (int)(p.HealthGraceSeconds - startedFor) + "s"; }
                return;
            }
            if (!st.HealthBusy && now - st.HealthLastCheck >= p.HealthEverySeconds)
            {
                st.HealthLastCheck = now;
                st.HealthBusy = true;
                string url = p.HealthUrl, user = p.HealthUser, problem;
                string pw = p.HealthUser.Length > 0 ? Secrets.TryReveal(p.HealthPassword, out problem) : "";
                int timeout = Math.Min(10000, Math.Max(2000, p.HealthEverySeconds * 1000));
                double checkAt = now;
                ThreadStart probe = delegate
                {
                    try
                    {
                        string err = Http.Probe(url, user, pw, timeout);
                        if (err == null) st.HealthLastOk = Math.Max(st.HealthLastOk, checkAt);
                        st.HealthError = err ?? "";
                    }
                    finally { st.HealthBusy = false; }
                };
                if (InlineHealth) probe();
                else { Thread t = new Thread(probe); t.IsBackground = true; t.Name = "health-" + p.Name; t.Start(); }
            }
            double bad = now - st.HealthLastOk;
            if (bad < p.HealthFailSeconds)
            {
                if (st.HealthFailing)
                {
                    st.HealthFailing = false;
                    log.Info(p.Name + ": the health check answers again.");
                    alerts.Recover("watchdog:" + p.Id + ":health", p.Name + " answers again", p.HealthUrl + " answers again.");
                }
                lock (lk) { st.Health = "ok"; st.HealthText = st.HealthError.Length > 0 ? "No answer for " + (int)bad + "s: " + st.HealthError : "Answering"; }
                return;
            }
            lock (lk) { st.Health = "failing"; st.HealthText = "No good answer for " + (int)bad + "s: " + st.HealthError; }
            if (!st.HealthFailing)
            {
                st.HealthFailing = true;
                string msg = p.Name + " is running but " + p.HealthUrl + " has not answered for " + (int)bad + "s (" + st.HealthError + ")" +
                             (p.HealthAction == "restart" ? "; it is restarted" : "");
                log.Error(msg);
                alerts.Raise("watchdog", "watchdog:" + p.Id + ":health", "error", p.Name + " is not answering", msg);
                if (p.HealthAction == "restart")
                {
                    st.HealthLastOk = now;
                    st.HealthFailing = false;
                    Restart(p, st, seen, "health check failed", true);
                }
            }
        }

        // ---------------------------------------------------------------- status
        public List<ProgramStatus> Snapshot()
        {
            Settings cfg = settings();
            List<ProgramStatus> o = new List<ProgramStatus>();
            double now = Util.Now();
            lock (lk)
            {
                foreach (ProgramEntry p in cfg.Watchdog)
                {
                    ProgramStatus s = new ProgramStatus();
                    s.Id = p.Id;
                    s.Name = p.Name;
                    s.Kind = p.Kind;
                    s.Enabled = p.Enabled;
                    Slot st;
                    if (slots.TryGetValue(p.Id, out st))
                    {
                        s.State = p.Enabled ? st.State : "off";
                        s.Text = p.Enabled ? st.Text : "Switched off";
                        s.Pid = st.Pid;
                        s.RunningSince = st.RunningSince;
                        s.Starts = st.Starts;
                        int n = 0;
                        foreach (double t in st.StartTimes) if (now - t < 3600) n++;
                        s.StartsLastHour = n;
                        s.LastProblem = st.LastProblem;
                        s.LastProblemAt = st.LastProblemAt;
                        s.Health = st.Health;
                        s.HealthText = st.HealthText;
                        s.NextRestart = st.NextRestart.HasValue ? Util.ToEpoch(st.NextRestart.Value) : 0;
                    }
                    else
                    {
                        s.State = p.Enabled ? "waiting" : "off";
                        s.Text = p.Enabled ? "Not checked yet" : "Switched off";
                    }
                    o.Add(s);
                }
            }
            return o;
        }
    }
}
