// Fake processes, services and alert sink for tests.
using System;
using System.Collections.Generic;
using System.IO;
using Watchkeeper;

namespace Watchkeeper.Tests
{
    public class FakeRunning : IRunning
    {
        public int pid;
        public volatile bool exited;
        public int Pid { get { return pid; } }
        public bool WaitForExit(int ms) { return exited; }
        public int ExitCode { get { return 0; } }
        public TextReader StdOut { get { return null; } }
        public TextReader StdErr { get { return null; } }
        public void Kill() { exited = true; if (World != null) World.Remove(pid); }
        public void Dispose() { }
        public FakeProcs World;
    }

    public class FakeProcs : IProcesses
    {
        public readonly List<ProcInfo> Procs = new List<ProcInfo>();
        public readonly List<StartSpec> Starts = new List<StartSpec>();
        public readonly List<int> Kills = new List<int>();
        public readonly List<FakeRunning> Started = new List<FakeRunning>();
        public int Session = 1;
        public string User = "STUDIO\\operator";
        /// <summary>file started -> the process it puts up (null: none, the file itself by default).</summary>
        public Func<StartSpec, string> Spawns = delegate(StartSpec s) { return s.File; };
        public bool LauncherStays;           // the started file keeps running too (when it spawns another file)
        public string FailStart;
        public bool ClosesOnRequest = true;
        int nextPid = 1000;

        public FakeRunning Last { get { return Started.Count > 0 ? Started[Started.Count - 1] : null; } }

        public ProcInfo Add(string file, int session)
        {
            ProcInfo p = new ProcInfo();
            p.Pid = nextPid++;
            p.Session = session;
            p.Name = Path.GetFileNameWithoutExtension(file.Replace('\\', '/').Substring(file.Replace('\\', '/').LastIndexOf('/') + 1));
            p.Path = file;
            lock (Procs) Procs.Add(p);
            return p;
        }

        public void Remove(int pid)
        {
            lock (Procs) Procs.RemoveAll(delegate(ProcInfo x) { return x.Pid == pid; });
            foreach (FakeRunning r in Started) if (r.pid == pid) r.exited = true;
        }

        public void Crash(string file)
        {
            List<ProcInfo> gone;
            lock (Procs) gone = Procs.FindAll(delegate(ProcInfo x) { return x.Path == file; });
            foreach (ProcInfo x in gone) Remove(x.Pid);
        }

        public int Count(string file) { lock (Procs) return Procs.FindAll(delegate(ProcInfo x) { return x.Path == file; }).Count; }

        public List<ProcInfo> Find(string name)
        {
            string n = Watchkeeper.Procs.Stem(name);
            lock (Procs) return Procs.FindAll(delegate(ProcInfo p) { return Watchkeeper.Procs.Stem(p.Name) == n; });
        }

        public List<ProcInfo> All(int session)
        {
            lock (Procs) return Procs.FindAll(delegate(ProcInfo p) { return session < 0 || p.Session == session; });
        }

        public void Kill(int pid) { Kills.Add(pid); Remove(pid); }

        public IRunning Start(StartSpec s)
        {
            if (Watchkeeper.Procs.Stem(s.File) == "taskkill")
            {
                Starts.Add(s);
                int pid;
                if (ClosesOnRequest && int.TryParse(s.Arguments.Replace("/PID", "").Trim(), out pid)) Remove(pid);
                FakeRunning k = new FakeRunning();
                k.exited = true;
                return k;
            }
            if (FailStart != null) throw new InvalidOperationException(FailStart);
            Starts.Add(s);
            FakeRunning f = new FakeRunning();
            f.World = this;
            int session = s.Desktop ? Session : 0;
            string spawn = Spawns(s);
            if (spawn == s.File || spawn == null)
            {
                ProcInfo own = Add(s.File, session);
                f.pid = own.Pid;
                if (spawn == null) { Remove(own.Pid); f.exited = true; }
            }
            else
            {
                ProcInfo own = Add(s.File, session);
                f.pid = own.Pid;
                Add(spawn, session);
                if (!LauncherStays) { Remove(own.Pid); f.exited = true; }
            }
            Started.Add(f);
            return f;
        }

        public int DesktopSession(out string user) { user = Session >= 0 ? User : ""; return Session; }
    }

    public class FakeServices : IServices
    {
        public readonly Dictionary<string, string> State = new Dictionary<string, string>();
        public readonly List<string> Calls = new List<string>();
        public Action<string> OnStart;
        public string Status(string name) { string s; return State.TryGetValue(name, out s) ? s : "missing"; }
        public void Start(string name) { Calls.Add("start " + name); State[name] = "running"; if (OnStart != null) OnStart(name); }
        public void Stop(string name, int waitMs) { Calls.Add("stop " + name); State[name] = "stopped"; }
        public Dictionary<string, int> Pids = new Dictionary<string, int>();
        public int Pid(string name) { int p; return Pids.TryGetValue(name, out p) ? p : 0; }
    }

    public class FakeAlerts : IAlertSink
    {
        public readonly List<Alert> Sent = new List<Alert>();
        readonly HashSet<string> active = new HashSet<string>();
        public void Raise(string part, string key, string level, string title, string message)
        {
            lock (Sent) { active.Add(key); Alert a = new Alert(); a.Part = part; a.Key = key; a.Level = level; a.Title = title; a.Message = message; Sent.Add(a); }
        }
        public void Recover(string key, string title, string message)
        {
            lock (Sent)
            {
                if (!active.Remove(key)) return;
                Alert a = new Alert(); a.Key = key; a.Title = title; a.Message = message; a.Recovery = true; a.Level = "info"; Sent.Add(a);
            }
        }
        public string Text()
        {
            lock (Sent) return string.Join(" | ", Sent.ConvertAll(delegate(Alert a) { return (a.Recovery ? "RECOVERED " : a.Level + " ") + a.Key + ": " + a.Message; }).ToArray());
        }
        public int Count(string keyPart, bool recovery)
        {
            lock (Sent) return Sent.FindAll(delegate(Alert a) { return a.Key.Contains(keyPart) && a.Recovery == recovery; }).Count;
        }
    }
}
