// Checks of the parts that only work on a real Windows server, run on the server itself: the manager's "Self-test"
// button (run by the service, so as Local System, exactly as the watchdog works) or "Watchkeeper.exe --selftest".
// Every line starts with PASS, FAIL or INFO.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Watchkeeper
{
    public static class SelfTest
    {
        static void Check(List<string> o, string what, Func<string> test)
        {
            try
            {
                string r = test();
                o.Add(r == null ? "PASS  " + what : r.StartsWith("INFO") ? r : "FAIL  " + what + ": " + r);
            }
            catch (Exception ex) { o.Add("FAIL  " + what + ": " + ex.Message); }
        }

        /// <summary>Checks that need nothing but this process.</summary>
        public static List<string> Basic(string dataDir)
        {
            List<string> o = new List<string>();
            o.Add("INFO  Watchkeeper " + Engine.Version + " on " + Environment.MachineName + ", " + Environment.OSVersion + ", .NET " + Environment.Version +
                  ", running as " + Environment.UserDomainName + "\\" + Environment.UserName);
            Check(o, "administrator rights", delegate { return Setup.IsAdmin() ? null : "this process has no administrator rights"; });
            Check(o, "passwords can be encrypted and read back", delegate
            {
                string enc = Secrets.Protect("check-" + Util.NewId());
                return Secrets.IsEncrypted(enc) && Secrets.Reveal(enc).StartsWith("check-") ? null : "round trip failed";
            });
            Check(o, "settings folder " + dataDir + " is protected", delegate
            {
                if (!Directory.Exists(dataDir)) return "it does not exist (install the service)";
                List<string> others = Setup.OthersWithAccess(dataDir);
                return others.Count == 0 ? null : "also open to " + string.Join(", ", others.ToArray()) + " (run deploy.cmd again to fix it)";
            });
            Check(o, "settings file can be read", delegate
            {
                SettingsFile f = new SettingsFile(dataDir, null);
                if (!File.Exists(f.Path)) return "INFO  no settings file yet";
                List<string> notes = new List<string>();
                Settings s = SettingsFile.Parse(Util.ReadAllTextRetry(f.Path), notes);
                List<Problem> p = Validate.All(s).FindAll(delegate(Problem x) { return x.Level == "error"; });
                return p.Count == 0 ? null : p.Count + " problem(s), e.g. " + p[0];
            });
            Check(o, "the service is installed and running from " + Setup.InstalledExe, delegate
            {
                Setup.ServiceInfo i = Setup.Query();
                if (!i.Installed) return "the service is not installed (run deploy.cmd)";
                if (i.Status != "running") return "the service is " + i.Status;
                return i.BinaryPath.IndexOf(Setup.InstalledExe, StringComparison.OrdinalIgnoreCase) >= 0 ? null : "it runs " + i.BinaryPath;
            });
            Check(o, "Windows services can be read", delegate
            {
                string s = new SystemServices().Status("EventLog");
                return s == "running" ? null : "the EventLog service reads as '" + s + "'";
            });
            Check(o, "running programs and their files can be read", delegate
            {
                using (Process me = Process.GetCurrentProcess())
                {
                    List<ProcInfo> found = new SystemProcesses().Find(me.ProcessName);
                    ProcInfo mine = found.Find(delegate(ProcInfo x) { return x.Pid == me.Id; });
                    if (mine == null) return "this process was not found in the list";
                    return mine.Path.Length > 0 ? null : "the file of a process cannot be read";
                }
            });
            return o;
        }

        /// <summary>Run by the service (Local System): also starts a program in the signed-in session and ends it,
        /// as the watchdog does, and checks ffmpeg.</summary>
        public static List<string> InService(Engine engine, IProcesses procs)
        {
            List<string> o = Basic(engine.DataDir);
            string user;
            int session = procs.DesktopSession(out user);
            if (session < 0) o.Add("FAIL  signed-in session: nobody is signed in, so desktop programs cannot be started (set up auto-logon)");
            else
            {
                o.Add("PASS  signed-in session " + session + " (" + user + ")");
                Check(o, "a program can be started in the signed-in session, found and ended", delegate
                {
                    StartSpec s = new StartSpec();
                    s.File = Util.SystemExe("ping.exe");
                    s.Arguments = "-n 30 127.0.0.1";
                    s.Desktop = true;
                    s.Hidden = true;
                    using (IRunning r = procs.Start(s))
                    {
                        Thread.Sleep(500);
                        ProcInfo p = procs.Find("ping").Find(delegate(ProcInfo x) { return x.Pid == r.Pid; });
                        if (p == null) { r.Kill(); return "started (PID " + r.Pid + ") but not found in the process list"; }
                        if (p.Session != session) { r.Kill(); return "it runs in session " + p.Session + " instead of " + session; }
                        if (p.Path.Length == 0) { r.Kill(); return "its file cannot be read"; }
                        procs.Kill(p.Pid);
                        Thread.Sleep(300);
                        return procs.Find("ping").Exists(delegate(ProcInfo x) { return x.Pid == r.Pid; }) ? "it could not be ended" : null;
                    }
                });
            }
            Settings cfg = engine.Get();
            string ff = Util.NormPath(cfg.General.FfmpegPath);
            if (!cfg.Vertical.Enabled && !File.Exists(ff)) o.Add("INFO  ffmpeg not checked (vertical conversion is off and " + ff + " does not exist)");
            else Check(o, "ffmpeg runs (" + ff + ")", delegate
            {
                if (!File.Exists(ff)) return "not found";
                Ffmpeg.Capture c = Ffmpeg.RunCapture(ff, new[] { "-hide_banner", "-version" }, 20000);
                return c.ExitCode == 0 ? null : "exit code " + c.ExitCode + ": " + Util.OneLine(c.Err, 200);
            });
            Check(o, "the status file is written", delegate
            {
                return File.Exists(engine.StatusPath) && (DateTime.Now - File.GetLastWriteTime(engine.StatusPath)).TotalSeconds < 30 ? null : "status.json is missing or old";
            });
            return o;
        }
    }
}
