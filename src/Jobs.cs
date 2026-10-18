// Scheduling for folder policies and scripts. The job list is rebuilt from the configuration
// on every tick, so added, removed and changed entries take effect without a restart.
// Last-run times are kept in state.json so a daily run missed while the server was off
// can be caught up at the next start.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Watchkeeper
{
    /// <summary>Outcome of one run.</summary>
    public class TaskResult
    {
        public bool Ok;
        public string Text = "";
        public int Moved, Deleted, Copied, Kept, Skipped, Failed, Untouched, Considered;
        public int Rules, RuleErrors;
        public long Bytes;
        public int ExitCode;
        public string Detail = "";

        public TaskResult() { }
        public TaskResult(bool ok, string text) { Ok = ok; Text = text; }
    }

    public class RunContext
    {
        public SourceLogger Log;
        public bool DryRun;
        public string Trigger = "manual";       // schedule | manual | startup | catch-up | file
        public string File = "";                // file trigger: the file that arrived
        public ManualResetEvent Cancel = new ManualResetEvent(false);
        public bool Cancelled { get { return Cancel.WaitOne(0); } }
    }

    public class JobSpec
    {
        public string Key = "";                 // folder:<id> | script:<id>
        public string Kind = "";                // folder | script
        public string Id = "";
        public string Name = "";
        public string Source = "";              // log source
        public bool Enabled;
        public ScheduleCfg Schedule;            // null = never on a timer (file trigger)
        public bool AtStartup;                  // run once shortly after the service starts
        public Func<RunContext, TaskResult> Run;
    }

    /// <summary>Status of one job as shown in the manager.</summary>
    public class JobStatus
    {
        public string Key = "";
        public string Kind = "";
        public string Id = "";
        public string Name = "";
        public bool Enabled;
        public string ScheduleText = "";
        public bool Running;
        public double LastStart;
        public double LastEnd;
        public string LastResult = "";          // "" | ok | failed
        public string LastText = "";
        public bool LastDryRun;
        public string LastTrigger = "";
        public double NextRun;                  // epoch, 0 = not scheduled
        public int Runs;
    }

    public class JobManager
    {
        class State
        {
            public bool Running;
            public bool Seen;                   // present in the job list since this process started
            public string Sig;
            public DateTime? NextRun;
            public string NextTrigger = "schedule";
            public double LastStart, LastEnd;
            public string LastResult = "";
            public string LastText = "";
            public bool LastDryRun;
            public string LastTrigger = "";
            public int Runs;
            public List<string> LastLines = new List<string>();
            public RunContext Ctx;
        }

        class Persisted
        {
            public string Key = "";
            public double LastStart, LastEnd;
            public string LastResult = "";
            public string LastText = "";
            public bool LastDryRun;
        }

        class PersistedFile
        {
            public List<Persisted> Jobs = new List<Persisted>();
        }

        readonly Func<List<JobSpec>> provider;
        readonly LogHub hub;
        readonly string statePath;
        readonly Dictionary<string, State> states = new Dictionary<string, State>();
        readonly Dictionary<string, JobSpec> specs = new Dictionary<string, JobSpec>();
        readonly object lk = new object();
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        readonly double started = Util.Now();
        Thread thread;

        /// <summary>Delay before a catch-up or start-up run.</summary>
        public int StartupDelaySeconds = 60;
        /// <summary>Global dry-run (from the general settings).</summary>
        public Func<bool> GlobalDryRun = delegate { return false; };
        /// <summary>Called after every run (alerts).</summary>
        public Action<JobSpec, TaskResult, RunContext> Finished;

        public JobManager(Func<List<JobSpec>> provider, LogHub hub, string statePath)
        {
            this.provider = provider;
            this.hub = hub;
            this.statePath = statePath;
            LoadState();
        }

        // ------------------------------------------------------------- schedules
        static DayOfWeek ParseDay(string d)
        {
            int i = Array.IndexOf(Cfg.Days, (d ?? "").ToLowerInvariant());
            // Cfg.Days starts on Monday; DayOfWeek starts on Sunday
            return i < 0 ? DayOfWeek.Sunday : (DayOfWeek)((i + 1) % 7);
        }

        public static DateTime? ComputeNext(ScheduleCfg s, DateTime now, bool first)
        {
            switch (s.Mode)
            {
                case "interval":
                    return first ? now.AddSeconds(5) : now.AddMinutes(s.IntervalMinutes);
                case "daily":
                    {
                        DateTime c = now.Date + Util.ParseTime(s.Time);
                        if (c <= now) c = c.AddDays(1);
                        return c;
                    }
                case "weekly":
                    {
                        DateTime c = now.Date + Util.ParseTime(s.Time);
                        DayOfWeek want = ParseDay(s.Day);
                        while (c.DayOfWeek != want || c <= now) c = c.AddDays(1);
                        return c;
                    }
            }
            return null;    // manual
        }

        /// <summary>Most recent scheduled time at or before now (daily / weekly), or null.</summary>
        public static DateTime? PreviousDue(ScheduleCfg s, DateTime now)
        {
            if (s.Mode == "daily")
            {
                DateTime c = now.Date + Util.ParseTime(s.Time);
                if (c > now) c = c.AddDays(-1);
                return c;
            }
            if (s.Mode == "weekly")
            {
                DateTime c = now.Date + Util.ParseTime(s.Time);
                DayOfWeek want = ParseDay(s.Day);
                while (c.DayOfWeek != want || c > now) c = c.AddDays(-1);
                return c;
            }
            return null;
        }

        public static string Describe(ScheduleCfg s, bool enabled)
        {
            if (!enabled) return "Off";
            if (s == null) return "";
            switch (s.Mode)
            {
                case "manual": return "Manual only";
                case "daily": return "Daily at " + s.Time;
                case "weekly": return "Weekly, " + char.ToUpperInvariant(s.Day[0]) + s.Day.Substring(1) + " at " + s.Time;
            }
            double m = s.IntervalMinutes;
            return "Every " + Util.Num(m, 2) + " minute" + (m == 1 ? "" : "s");
        }

        // ---------------------------------------------------------------- state file
        void LoadState()
        {
            if (statePath == null || !File.Exists(statePath)) return;
            try
            {
                PersistedFile pf = new PersistedFile();
                Json.FillFrom(pf, Json.Parse(File.ReadAllText(statePath)));
                foreach (Persisted p in pf.Jobs)
                {
                    State st = new State();
                    st.LastStart = p.LastStart;
                    st.LastEnd = p.LastEnd;
                    st.LastResult = p.LastResult;
                    st.LastText = p.LastText;
                    st.LastDryRun = p.LastDryRun;
                    states[p.Key] = st;
                }
            }
            catch (Exception ex) { hub.Add("WARN", "service", "Job state file unreadable, starting fresh: " + ex.Message); }
        }

        void SaveState()
        {
            if (statePath == null) return;
            PersistedFile pf = new PersistedFile();
            lock (lk)
            {
                foreach (KeyValuePair<string, State> kv in states)
                {
                    if (kv.Value.LastStart == 0) continue;
                    Persisted p = new Persisted();
                    p.Key = kv.Key;
                    p.LastStart = kv.Value.LastStart;
                    p.LastEnd = kv.Value.LastEnd;
                    p.LastResult = kv.Value.LastResult;
                    p.LastText = kv.Value.LastText;
                    p.LastDryRun = kv.Value.LastDryRun;
                    pf.Jobs.Add(p);
                }
            }
            try { Util.WriteAtomic(statePath, Json.Serialize(Json.ToJson(pf), true)); }
            catch (Exception ex) { hub.Add("WARN", "service", "Cannot save job state: " + ex.Message); }
        }

        // ---------------------------------------------------------------- control
        public void Start()
        {
            lock (lk)
            {
                if (thread != null && thread.IsAlive) return;
                stop.Reset();
                thread = new Thread(Loop);
                thread.Name = "scheduler";
                thread.IsBackground = true;
                thread.Priority = ThreadPriority.AboveNormal;
                thread.Start();
            }
        }

        public void Stop()
        {
            stop.Set();
            lock (lk)
            {
                foreach (State st in states.Values) if (st.Ctx != null) st.Ctx.Cancel.Set();
            }
        }

        void Loop()
        {
            while (!stop.WaitOne(1000))
            {
                try { Tick(DateTime.Now); }
                catch (Exception ex) { hub.Add("ERROR", "service", "Scheduler error: " + ex.Message); }
            }
        }

        public void Tick() { Tick(DateTime.Now); }

        public void Tick(DateTime now)
        {
            List<JobSpec> list = provider();
            List<JobSpec> due = new List<JobSpec>();
            List<string> triggers = new List<string>();
            lock (lk)
            {
                HashSet<string> keys = new HashSet<string>();
                foreach (JobSpec spec in list)
                {
                    keys.Add(spec.Key);
                    specs[spec.Key] = spec;
                    State st;
                    bool known = states.TryGetValue(spec.Key, out st);
                    if (!known) { st = new State(); states[spec.Key] = st; }
                    ScheduleCfg s = spec.Schedule;
                    string sig = spec.Enabled + "|" + spec.AtStartup + "|" +
                                 (s == null ? "-" : s.Mode + "|" + s.Time + "|" + s.Day + "|" + Util.Num(s.IntervalMinutes, 4));
                    if (sig != st.Sig)
                    {
                        bool firstSight = !st.Seen;
                        st.Sig = sig;
                        st.Seen = true;
                        st.NextTrigger = "schedule";
                        st.NextRun = spec.Enabled && s != null ? ComputeNext(s, now, true) : null;
                        if (spec.Enabled && firstSight && Util.Now() - started < 120)
                        {
                            if (spec.AtStartup)
                            {
                                st.NextRun = now.AddSeconds(StartupDelaySeconds);
                                st.NextTrigger = "startup";
                            }
                            else if (known && s != null && s.CatchUp && st.LastStart > 0)
                            {
                                DateTime? prev = PreviousDue(s, now);
                                if (prev.HasValue && Util.ToEpoch(prev.Value) > st.LastStart)
                                {
                                    st.NextRun = now.AddSeconds(StartupDelaySeconds);
                                    st.NextTrigger = "catch-up";
                                    hub.Add("INFO", spec.Source, spec.Name + ": the run due at " + prev.Value.ToString("yyyy-MM-dd HH:mm", Util.Inv) +
                                            " was missed; running it in " + StartupDelaySeconds + "s.");
                                }
                            }
                        }
                    }
                    if (st.NextRun.HasValue && now >= st.NextRun.Value && !st.Running)
                    {
                        triggers.Add(st.NextTrigger);
                        st.NextTrigger = "schedule";
                        st.NextRun = s != null ? ComputeNext(s, now, false) : null;
                        due.Add(spec);
                    }
                }
                List<string> gone = new List<string>();
                foreach (string k in specs.Keys) if (!keys.Contains(k)) gone.Add(k);
                foreach (string k in gone)
                {
                    specs.Remove(k);
                    State st;
                    if (states.TryGetValue(k, out st) && !st.Running) { st.Sig = null; st.NextRun = null; }
                }
            }
            for (int i = 0; i < due.Count; i++) Launch(due[i], GlobalDryRun(), triggers[i], "");
        }

        /// <summary>Test hook: make the job due on the next tick.</summary>
        public void ForceDue(string key)
        {
            lock (lk) { State st; if (states.TryGetValue(key, out st)) st.NextRun = DateTime.Now.AddSeconds(-1); }
        }

        JobSpec Find(string key)
        {
            lock (lk)
            {
                JobSpec s;
                if (specs.TryGetValue(key, out s)) return s;
            }
            foreach (JobSpec s in provider()) if (s.Key == key) return s;
            return null;
        }

        public bool RunNow(string key, bool dryRun, out string message)
        {
            JobSpec spec = Find(key);
            if (spec == null) { message = "unknown job " + key; return false; }
            if (!Launch(spec, dryRun || GlobalDryRun(), "manual", "")) { message = "already running"; return false; }
            message = "started";
            return true;
        }

        public bool Abort(string key)
        {
            lock (lk)
            {
                State st;
                if (!states.TryGetValue(key, out st) || !st.Running || st.Ctx == null) return false;
                st.Ctx.Cancel.Set();
                return true;
            }
        }

        bool Claim(JobSpec spec, bool dryRun, string trigger, string file, out RunContext ctx)
        {
            ctx = null;
            lock (lk)
            {
                State st;
                if (!states.TryGetValue(spec.Key, out st)) { st = new State(); states[spec.Key] = st; }
                if (st.Running) return false;
                st.Running = true;
                ctx = new RunContext();
                ctx.DryRun = dryRun;
                ctx.Trigger = trigger;
                ctx.File = file ?? "";
                st.Ctx = ctx;
            }
            return true;
        }

        bool Launch(JobSpec spec, bool dryRun, string trigger, string file)
        {
            RunContext ctx;
            if (!Claim(spec, dryRun, trigger, file, out ctx)) return false;
            Thread t = new Thread(delegate() { Execute(spec, ctx); });
            t.Name = "job-" + spec.Key;
            t.IsBackground = true;
            t.Start();
            return true;
        }

        /// <summary>Run in the calling thread (file triggers). Null when the job is already running.</summary>
        public TaskResult RunHere(string key, string trigger, string file)
        {
            JobSpec spec = Find(key);
            if (spec == null) return new TaskResult(false, "unknown job");
            RunContext ctx;
            if (!Claim(spec, GlobalDryRun(), trigger, file, out ctx)) return null;
            return Execute(spec, ctx);
        }

        TaskResult Execute(JobSpec spec, RunContext ctx)
        {
            State st;
            lock (lk) { st = states[spec.Key]; }
            List<string> lines = new List<string>();
            ctx.Log = hub.Logger(spec.Source).WithCapture(lines);
            lock (lk)
            {
                st.LastStart = Util.Now();
                st.LastTrigger = ctx.Trigger;
                st.LastDryRun = ctx.DryRun;
            }
            string why = ctx.Trigger == "file" ? "new file " + Path.GetFileName(ctx.File) : ctx.Trigger;
            ctx.Log.Info(spec.Name + ": run started (" + why + ")" + (ctx.DryRun ? ", dry-run: nothing will be changed" : ""));
            TaskResult result;
            try
            {
                result = spec.Run(ctx) ?? new TaskResult(false, "no result");
            }
            catch (Exception ex)
            {
                hub.Add("DEBUG", spec.Source, ex.ToString());
                ctx.Log.Error(spec.Name + ": run failed unexpectedly: " + ex.Message);
                result = new TaskResult(false, "Crashed: " + ex.Message);
            }
            lock (lk)
            {
                st.LastEnd = Util.Now();
                st.LastResult = result.Ok ? "ok" : "failed";
                st.LastText = result.Text;
                st.LastLines = lines;
                st.Runs++;
                st.Running = false;
                st.Ctx = null;
            }
            SaveState();
            Action<JobSpec, TaskResult, RunContext> fin = Finished;
            if (fin != null)
            {
                try { fin(spec, result, ctx); }
                catch (Exception ex) { hub.Add("ERROR", "service", "After-run handler failed: " + ex.Message); }
            }
            return result;
        }

        public List<JobStatus> Snapshot()
        {
            List<JobSpec> list = provider();
            List<JobStatus> o = new List<JobStatus>();
            lock (lk)
            {
                foreach (JobSpec spec in list)
                {
                    JobStatus v = new JobStatus();
                    v.Key = spec.Key;
                    v.Kind = spec.Kind;
                    v.Id = spec.Id;
                    v.Name = spec.Name;
                    v.Enabled = spec.Enabled;
                    v.ScheduleText = spec.AtStartup ? (spec.Enabled ? "At service start" : "Off")
                                     : spec.Schedule == null ? (spec.Enabled ? "When files arrive" : "Off")
                                     : Describe(spec.Schedule, spec.Enabled);
                    State st;
                    if (states.TryGetValue(spec.Key, out st))
                    {
                        v.Running = st.Running;
                        v.LastStart = st.LastStart;
                        v.LastEnd = st.LastEnd;
                        v.LastResult = st.LastResult;
                        v.LastText = st.LastText;
                        v.LastDryRun = st.LastDryRun;
                        v.LastTrigger = st.LastTrigger;
                        v.NextRun = st.NextRun.HasValue ? Util.ToEpoch(st.NextRun.Value) : 0;
                        v.Runs = st.Runs;
                    }
                    o.Add(v);
                }
            }
            return o;
        }

        public List<string> Lines(string key)
        {
            lock (lk)
            {
                State st;
                if (!states.TryGetValue(key, out st)) return new List<string>();
                lock (st.LastLines) return new List<string>(st.LastLines);
            }
        }
    }
}
