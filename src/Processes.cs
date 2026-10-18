// Starting, finding and stopping processes, behind small interfaces so the supervisor,
// the script runner and the converter can be tested with fakes. Programs that need the
// desktop (VVD, vMix) are started in the logged-on console session (see WinSession.cs).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Watchkeeper
{
    public class ProcInfo
    {
        public int Pid;
        public int SessionId;
        public string Name = "";
        /// <summary>Full path of the program file, or empty when it cannot be read.</summary>
        public string Path = "";
    }

    public class StartRequest
    {
        public string File = "";
        public string Arguments = "";            // one Windows command-line string
        public string WorkingDir = "";
        public bool UserSession;                 // start in the logged-on console session
        public bool Redirect;                    // capture stdout/stderr (through pipes handed only to this process)
        /// <summary>Service session: run with a restricted token (all privileges removed, Administrators only for denying).</summary>
        public bool Restricted;
        /// <summary>User session: a program that asks for administrator rights may start with the user's full token.</summary>
        public bool AllowElevation;
        public bool Hidden = true;               // no console window (service session); user session shows GUI normally
        public string Priority = "normal";       // idle | below_normal | normal | above_normal | high
        public Dictionary<string, string> Environment = new Dictionary<string, string>();
    }

    public interface IRunning : IDisposable
    {
        int Pid { get; }
        /// <summary>Null when output is not redirected.</summary>
        TextReader StdOut { get; }
        TextReader StdErr { get; }
        bool WaitForExit(int ms);
        int ExitCode { get; }
        void Kill();
    }

    public interface ILauncher
    {
        /// <summary>Start a process. Throws with a readable message when it cannot.</summary>
        IRunning Start(StartRequest r);
    }

    public interface IProcessTable
    {
        List<ProcInfo> Find(string processName);
        void Kill(int pid);
    }

    public interface ISessions
    {
        /// <summary>Session id of the user signed in at the console, or -1 when nobody is.</summary>
        int ConsoleUserSession(out string user);
    }

    /// <summary>Windows services, behind an interface so the watchdog can be tested with a fake.</summary>
    public interface IServiceControl
    {
        /// <summary>running | stopped | paused | start_pending | stop_pending | continue_pending | pause_pending | missing</summary>
        string Status(string name);
        /// <summary>Start a stopped service or continue a paused one. Returns at once (the service may still be starting).</summary>
        void Start(string name);
        void Stop(string name, int waitMs);
        /// <summary>(service name, display name) of the services on this computer.</summary>
        List<KeyValuePair<string, string>> List();
    }

    public class SystemServiceControl : IServiceControl
    {
        public string Status(string name)
        {
            if (string.IsNullOrEmpty(name)) return "missing";
            try
            {
                using (System.ServiceProcess.ServiceController sc = new System.ServiceProcess.ServiceController(name))
                {
                    switch (sc.Status)
                    {
                        case System.ServiceProcess.ServiceControllerStatus.Running: return "running";
                        case System.ServiceProcess.ServiceControllerStatus.Stopped: return "stopped";
                        case System.ServiceProcess.ServiceControllerStatus.Paused: return "paused";
                        case System.ServiceProcess.ServiceControllerStatus.StartPending: return "start_pending";
                        case System.ServiceProcess.ServiceControllerStatus.StopPending: return "stop_pending";
                        case System.ServiceProcess.ServiceControllerStatus.ContinuePending: return "continue_pending";
                        case System.ServiceProcess.ServiceControllerStatus.PausePending: return "pause_pending";
                    }
                    return "stopped";
                }
            }
            catch (InvalidOperationException) { return "missing"; }
            catch (Exception) { return "missing"; }
        }

        public void Start(string name)
        {
            using (System.ServiceProcess.ServiceController sc = new System.ServiceProcess.ServiceController(name))
            {
                try
                {
                    if (sc.Status == System.ServiceProcess.ServiceControllerStatus.Paused) sc.Continue();
                    else sc.Start();
                }
                catch (InvalidOperationException ex)
                {
                    throw new InvalidOperationException(ex.InnerException != null ? ex.InnerException.Message : ex.Message, ex);
                }
            }
        }

        public void Stop(string name, int waitMs)
        {
            using (System.ServiceProcess.ServiceController sc = new System.ServiceProcess.ServiceController(name))
            {
                if (sc.Status == System.ServiceProcess.ServiceControllerStatus.Stopped) return;
                sc.Stop();
                sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromMilliseconds(waitMs));
            }
        }

        public List<KeyValuePair<string, string>> List()
        {
            List<KeyValuePair<string, string>> o = new List<KeyValuePair<string, string>>();
            try
            {
                foreach (System.ServiceProcess.ServiceController sc in System.ServiceProcess.ServiceController.GetServices())
                {
                    try { o.Add(new KeyValuePair<string, string>(sc.ServiceName, sc.DisplayName)); }
                    catch (Exception) { }
                    finally { sc.Dispose(); }
                }
            }
            catch (Exception) { }
            o.Sort(delegate(KeyValuePair<string, string> a, KeyValuePair<string, string> b) { return string.Compare(a.Value, b.Value, StringComparison.OrdinalIgnoreCase); });
            return o;
        }
    }

    public static class Procs
    {
        public static ProcessPriorityClass ToClass(string priority)
        {
            switch (priority)
            {
                case "idle": return ProcessPriorityClass.Idle;
                case "below_normal": return ProcessPriorityClass.BelowNormal;
                case "above_normal": return ProcessPriorityClass.AboveNormal;
                case "high": return ProcessPriorityClass.High;
                case "realtime": return ProcessPriorityClass.RealTime;
            }
            return ProcessPriorityClass.Normal;
        }

        public static void SetPriority(int pid, string priority)
        {
            if (string.IsNullOrEmpty(priority) || priority == "normal") return;
            try
            {
                using (Process p = Process.GetProcessById(pid)) p.PriorityClass = ToClass(priority);
            }
            catch (Exception) { }
        }

        public static string Stem(string name)
        {
            string low = (name ?? "").Trim().ToLowerInvariant();
            if (low.EndsWith(".exe")) low = low.Substring(0, low.Length - 4);
            int slash = Math.Max(low.LastIndexOf('\\'), low.LastIndexOf('/'));
            return slash >= 0 ? low.Substring(slash + 1) : low;
        }

        /// <summary>How to run a script file: .ps1 through PowerShell, .bat/.cmd through cmd, .vbs/.js through cscript.</summary>
        public static void CommandFor(string command, string args, out string file, out string arguments)
        {
            string cmd = Util.NormPath(command);
            string ext = Path.GetExtension(cmd).ToLowerInvariant();
            switch (ext)
            {
                case ".ps1":
                    file = Util.PowerShellExe;
                    arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File " + Util.QuoteArg(cmd) + (args.Length > 0 ? " " + args : "");
                    return;
                case ".bat":
                case ".cmd":
                    file = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.System), "cmd.exe");
                    if (!Util.IsWindows) file = "cmd.exe";
                    // /S: strip only the outer quotes, so a quoted script path with spaces works
                    arguments = "/d /s /c \"" + Util.QuoteArg(cmd) + (args.Length > 0 ? " " + args : "") + "\"";
                    return;
                case ".vbs":
                case ".js":
                    file = Util.SystemExe("cscript.exe");
                    arguments = "//nologo " + Util.QuoteArg(cmd) + (args.Length > 0 ? " " + args : "");
                    return;
            }
            file = cmd;
            arguments = args;
        }
    }

    /// <summary>A process started with System.Diagnostics.Process (service session).</summary>
    public class LocalRunning : IRunning
    {
        readonly Process p;
        public LocalRunning(Process p) { this.p = p; }
        public int Pid { get { return p.Id; } }
        public TextReader StdOut { get { return p.StartInfo.RedirectStandardOutput ? p.StandardOutput : null; } }
        public TextReader StdErr { get { return p.StartInfo.RedirectStandardError ? p.StandardError : null; } }
        public bool WaitForExit(int ms)
        {
            if (ms < 0) { p.WaitForExit(); return true; }
            return p.WaitForExit(ms);
        }
        public int ExitCode { get { return p.ExitCode; } }
        public void Kill()
        {
            try { if (!p.HasExited) p.Kill(); } catch (Exception) { }
        }
        public void Dispose() { p.Dispose(); }
    }

    /// <summary>Real launcher: service-session processes through Process.Start, user-session ones through WinSession.</summary>
    public class SystemLauncher : ILauncher
    {
        public IRunning Start(StartRequest r)
        {
            if (r.UserSession)
            {
                if (!Util.IsWindows) throw new InvalidOperationException("starting in the user session needs Windows");
                return WinSession.Start(r);
            }
            if (r.Restricted && Util.IsWindows) return Util.MediaAsLocalService ? WinSession.StartAsLocalService(r) : WinSession.StartRestricted(r);
            ProcessStartInfo psi = new ProcessStartInfo(r.File, r.Arguments ?? "");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = r.Hidden;
            if (!string.IsNullOrEmpty(r.WorkingDir)) psi.WorkingDirectory = r.WorkingDir;
            if (r.Redirect)
            {
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.RedirectStandardInput = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
            }
            foreach (KeyValuePair<string, string> kv in r.Environment) psi.EnvironmentVariables[kv.Key] = kv.Value;
            Process p = new Process();
            p.StartInfo = psi;
            try { p.Start(); }
            catch (Exception ex)
            {
                p.Dispose();
                throw new InvalidOperationException("cannot start " + r.File + ": " + ex.Message, ex);
            }
            if (r.Redirect) { try { p.StandardInput.Close(); } catch (Exception) { } }
            if (r.Priority != "normal")
            {
                try { p.PriorityClass = Procs.ToClass(r.Priority); } catch (Exception) { }
            }
            return new LocalRunning(p);
        }
    }

    public class SystemProcessTable : IProcessTable
    {
        static bool IsZombie(int pid)
        {
            try
            {
                string stat = File.ReadAllText("/proc/" + pid.ToString(Util.Inv) + "/stat");
                int close = stat.LastIndexOf(')');
                if (close < 0 || close + 2 >= stat.Length) return false;
                char state = stat[close + 2];
                return state == 'Z' || state == 'X';
            }
            catch (Exception) { return false; }
        }

        public List<ProcInfo> Find(string processName)
        {
            string target = Procs.Stem(processName);
            List<ProcInfo> o = new List<ProcInfo>();
            if (target.Length == 0) return o;
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    string n = Procs.Stem(p.ProcessName);
                    // Linux /proc names are cut to 15 characters
                    bool match = n == target || (!Util.IsWindows && target.Length > 15 && n == target.Substring(0, 15));
                    if (!match) continue;
                    if (!Util.IsWindows && IsZombie(p.Id)) continue;
                    ProcInfo i = new ProcInfo();
                    i.Pid = p.Id;
                    i.Name = p.ProcessName;
                    try { i.SessionId = p.SessionId; } catch (Exception) { i.SessionId = 0; }
                    i.Path = ImagePath(p.Id);
                    o.Add(i);
                }
                catch (Exception) { }
                finally { p.Dispose(); }
            }
            return o;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(int access, bool inherit, int pid);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);

        /// <summary>The program file of a process (any session), or "" when it cannot be read.</summary>
        public static string ImagePath(int pid)
        {
            try
            {
                if (!Util.IsWindows) return "";
                IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
                if (h == IntPtr.Zero) return "";
                try
                {
                    StringBuilder sb = new StringBuilder(1024);
                    int size = sb.Capacity;
                    return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : "";
                }
                finally { CloseHandle(h); }
            }
            catch (Exception) { return ""; }
        }

        public void Kill(int pid)
        {
            using (Process p = Process.GetProcessById(pid))
            {
                p.Kill();
                p.WaitForExit(10000);
            }
        }
    }

    public class SystemSessions : ISessions
    {
        public int ConsoleUserSession(out string user)
        {
            user = "";
            if (!Util.IsWindows)
            {
                user = System.Environment.UserName;
                return 0;
            }
            return WinSession.ConsoleUserSession(out user);
        }
    }
}
