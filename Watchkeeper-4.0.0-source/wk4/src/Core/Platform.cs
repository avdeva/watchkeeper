// What Watchkeeper needs from Windows, behind small interfaces so the logic can be tested with fakes:
// processes (list, start, end), the signed-in session, and Windows services.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Watchkeeper
{
    public class ProcInfo
    {
        public int Pid;
        public int Session;
        /// <summary>Process name without .exe, as in Task Manager's Details tab.</summary>
        public string Name = "";
        /// <summary>Full path of the program file; "" when it cannot be read.</summary>
        public string Path = "";
    }

    public class StartSpec
    {
        public string File = "";
        /// <summary>One Windows command-line string.</summary>
        public string Arguments = "";
        public string StartIn = "";
        /// <summary>True: in the signed-in user's session (visible). False: in the service's own session.</summary>
        public bool Desktop;
        /// <summary>Desktop: if the program asks for administrator rights, use the user's full rights.</summary>
        public bool Elevated;
        /// <summary>Desktop: start with the user's full rights straight away when the user has them (used to close the
        /// windows of a program that runs with full rights: Windows does not let a limited program close them).</summary>
        public bool ElevatedFirst;
        public bool Hidden;
        public bool Redirect;
        public string Priority = "normal";
        public Dictionary<string, string> Environment = new Dictionary<string, string>();
    }

    public interface IRunning : IDisposable
    {
        int Pid { get; }
        bool WaitForExit(int ms);
        int ExitCode { get; }
        TextReader StdOut { get; }
        TextReader StdErr { get; }
        void Kill();
    }

    public interface IProcesses
    {
        /// <summary>Processes with this name (without .exe, case-insensitive), with their session and file.</summary>
        List<ProcInfo> Find(string name);
        /// <summary>Every process (session -1) or those of one session, with their file where it can be read.</summary>
        List<ProcInfo> All(int session);
        /// <summary>End a process at once. Throws when it cannot be ended.</summary>
        void Kill(int pid);
        IRunning Start(StartSpec spec);
        /// <summary>The session desktop programs go to (the user signed in at the console, or the auto-logon user's
        /// session), or -1 when nobody is signed in.</summary>
        int DesktopSession(out string user);
    }

    public interface IServices
    {
        /// <summary>running | stopped | paused | starting | stopping | missing</summary>
        string Status(string name);
        void Start(string name);
        void Stop(string name, int waitMs);
        /// <summary>The process ID of a service that has one (running, starting, stopping); 0 when not known.</summary>
        int Pid(string name);
    }

    public static class Procs
    {
        public static string Stem(string name)
        {
            string low = (name ?? "").Trim().ToLowerInvariant();
            int slash = Math.Max(low.LastIndexOf('\\'), low.LastIndexOf('/'));
            if (slash >= 0) low = low.Substring(slash + 1);
            if (low.EndsWith(".exe")) low = low.Substring(0, low.Length - 4);
            return low;
        }

        public static ProcessPriorityClass ToClass(string priority)
        {
            switch (priority)
            {
                case "idle": return ProcessPriorityClass.Idle;
                case "below_normal": return ProcessPriorityClass.BelowNormal;
                case "above_normal": return ProcessPriorityClass.AboveNormal;
                case "high": return ProcessPriorityClass.High;
            }
            return ProcessPriorityClass.Normal;
        }
    }

    /// <summary>A process started with System.Diagnostics.Process.</summary>
    public class LocalRunning : IRunning
    {
        readonly Process p;
        public LocalRunning(Process p) { this.p = p; }
        public int Pid { get { return p.Id; } }
        public TextReader StdOut { get { return p.StartInfo.RedirectStandardOutput ? p.StandardOutput : null; } }
        public TextReader StdErr { get { return p.StartInfo.RedirectStandardError ? p.StandardError : null; } }
        public bool WaitForExit(int ms) { if (ms < 0) { p.WaitForExit(); return true; } return p.WaitForExit(ms); }
        public int ExitCode { get { return p.ExitCode; } }
        public void Kill() { try { if (!p.HasExited) p.Kill(); } catch (Exception) { } }
        public void Dispose() { p.Dispose(); }
    }

    public class SystemProcesses : IProcesses
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(int access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);

        public static string ImagePath(int pid)
        {
            if (!Util.IsWindows)
            {
                return ReadLink("/proc/" + pid + "/exe");
            }
            try
            {
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

        static string ReadLink(string path)
        {
            try
            {
                Type t = Type.GetType("Mono.Unix.UnixSymbolicLinkInfo, Mono.Posix");
                if (t == null) return "";
                object info = Activator.CreateInstance(t, path);
                return Convert.ToString(t.GetProperty("ContentsPath").GetValue(info, null));
            }
            catch (Exception) { return ""; }
        }

        static ProcInfo Info(Process p, bool withPath)
        {
            ProcInfo i = new ProcInfo();
            i.Pid = p.Id;
            i.Name = p.ProcessName;
            try { i.Session = p.SessionId; } catch (Exception) { i.Session = 0; }
            if (withPath) i.Path = ImagePath(p.Id);
            return i;
        }

        public List<ProcInfo> Find(string name)
        {
            string target = Procs.Stem(name);
            List<ProcInfo> o = new List<ProcInfo>();
            if (target.Length == 0) return o;
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    string n = Procs.Stem(p.ProcessName);
                    bool match = n == target || (!Util.IsWindows && target.Length > 15 && n == target.Substring(0, 15));
                    if (match && !p.HasExited) o.Add(Info(p, true));
                }
                catch (Exception) { }
                finally { p.Dispose(); }
            }
            return o;
        }

        public List<ProcInfo> All(int session)
        {
            List<ProcInfo> o = new List<ProcInfo>();
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    ProcInfo i = Info(p, false);
                    if (session >= 0 && i.Session != session) continue;
                    i.Path = ImagePath(i.Pid);
                    o.Add(i);
                }
                catch (Exception) { }
                finally { p.Dispose(); }
            }
            o.Sort(delegate(ProcInfo a, ProcInfo b) { return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
            return o;
        }

        public void Kill(int pid)
        {
            using (Process p = Process.GetProcessById(pid))
            {
                p.Kill();
                p.WaitForExit(3000);
            }
        }

        public int DesktopSession(out string user)
        {
            if (!Util.IsWindows) { user = Environment.UserName; return 0; }
            return WinSession.DesktopSession(out user);
        }

        public IRunning Start(StartSpec s)
        {
            if (s.Desktop && Util.IsWindows) return WinSession.Start(s);
            ProcessStartInfo psi = new ProcessStartInfo(s.File, s.Arguments ?? "");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = s.Hidden;
            string dir = s.StartIn;
            if (string.IsNullOrEmpty(dir)) { try { dir = Path.GetDirectoryName(s.File); } catch (Exception) { } }
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) psi.WorkingDirectory = dir;
            if (s.Redirect)
            {
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.RedirectStandardInput = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
            }
            foreach (KeyValuePair<string, string> kv in s.Environment) psi.EnvironmentVariables[kv.Key] = kv.Value;
            Process p = new Process();
            p.StartInfo = psi;
            try { p.Start(); }
            catch (Exception ex)
            {
                p.Dispose();
                throw new InvalidOperationException("cannot start " + s.File + ": " + ex.Message, ex);
            }
            if (s.Redirect) { try { p.StandardInput.Close(); } catch (Exception) { } }
            if (s.Priority != "normal") { try { p.PriorityClass = Procs.ToClass(s.Priority); } catch (Exception) { } }
            return new LocalRunning(p);
        }
    }

    public class SystemServices : IServices
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
                        case System.ServiceProcess.ServiceControllerStatus.Paused: return "paused";
                        case System.ServiceProcess.ServiceControllerStatus.StartPending:
                        case System.ServiceProcess.ServiceControllerStatus.ContinuePending: return "starting";
                        case System.ServiceProcess.ServiceControllerStatus.StopPending:
                        case System.ServiceProcess.ServiceControllerStatus.PausePending: return "stopping";
                    }
                    return "stopped";
                }
            }
            catch (Exception) { return "missing"; }
        }

        public void Start(string name)
        {
            using (System.ServiceProcess.ServiceController sc = new System.ServiceProcess.ServiceController(name))
            {
                try
                {
                    if (sc.Status == System.ServiceProcess.ServiceControllerStatus.Paused) sc.Continue();
                    else if (sc.Status == System.ServiceProcess.ServiceControllerStatus.Stopped) sc.Start();
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
                try { sc.Stop(); }
                catch (InvalidOperationException ex)
                {
                    throw new InvalidOperationException(ex.InnerException != null ? ex.InnerException.Message : ex.Message, ex);
                }
                sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromMilliseconds(waitMs));
            }
        }

        public int Pid(string name)
        {
            if (!Util.IsWindows) return 0;
            try
            {
                using (System.Management.ManagementObjectSearcher searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT ProcessId FROM Win32_Service WHERE Name = '" + name.Replace("\\", "").Replace("'", "") + "'"))
                using (System.Management.ManagementObjectCollection results = searcher.Get())
                    foreach (System.Management.ManagementBaseObject mo in results) using (mo) return Convert.ToInt32(mo["ProcessId"]);
            }
            catch (Exception) { }
            return 0;
        }

        /// <summary>(service name, display name) of every service, for the manager's picker.</summary>
        public static List<KeyValuePair<string, string>> List()
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

        /// <summary>PID -> service names hosted in that process (for the manager's process picker).</summary>
        public static Dictionary<int, string> ServicePids()
        {
            Dictionary<int, string> o = new Dictionary<int, string>();
            if (!Util.IsWindows) return o;
            try
            {
                using (System.Management.ManagementObjectSearcher searcher =
                    new System.Management.ManagementObjectSearcher("SELECT Name, ProcessId FROM Win32_Service WHERE ProcessId <> 0"))
                using (System.Management.ManagementObjectCollection results = searcher.Get())
                {
                    foreach (System.Management.ManagementBaseObject mo in results)
                    {
                        using (mo)
                        {
                            int pid = Convert.ToInt32(mo["ProcessId"]);
                            string name = Convert.ToString(mo["Name"]);
                            string had;
                            o[pid] = o.TryGetValue(pid, out had) ? had + ", " + name : name;
                        }
                    }
                }
            }
            catch (Exception) { }
            return o;
        }
    }
}
