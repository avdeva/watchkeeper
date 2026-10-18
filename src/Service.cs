// Windows service host and installation. The service runs the engine as Local System,
// starts automatically at boot, runs at the configured process priority (High by default)
// and is restarted by Windows if it ever stops unexpectedly.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Threading;

namespace Watchkeeper
{
    public class KeeperService : ServiceBase
    {
        public const string Name = "Watchkeeper";
        public const string DisplayTitle = "Watchkeeper";
        readonly string dataDir;
        Engine engine;

        public KeeperService(string dataDir)
        {
            this.dataDir = dataDir;
            ServiceName = Name;
            CanStop = true;
            CanShutdown = true;
            CanPauseAndContinue = false;
            AutoLog = false;
        }

        protected override void OnStart(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                ServiceSetup.CrashNote(dataDir, e.ExceptionObject as Exception);
            };
            try
            {
                using (Process me = Process.GetCurrentProcess()) me.PriorityClass = ProcessPriorityClass.High;
            }
            catch (Exception) { }
            try
            {
                engine = new Engine(dataDir, new EngineOptions { Mode = "service" });
                engine.Start();
            }
            catch (Exception ex)
            {
                ServiceSetup.CrashNote(dataDir, ex);
                ExitCode = 1064;    // ERROR_EXCEPTION_IN_SERVICE: Windows applies the recovery actions
                throw;
            }
        }

        protected override void OnStop()
        {
            try { RequestAdditionalTime(30000); } catch (Exception) { }
            StopEngine(false);
        }

        protected override void OnShutdown()
        {
            // the service is not in a stop-pending state here, so RequestAdditionalTime would throw
            StopEngine(true);
        }

        void StopEngine(bool fast)
        {
            Engine e = engine;
            engine = null;
            if (e == null) return;
            try { e.Shutdown(fast); }
            catch (Exception ex) { ServiceSetup.CrashNote(dataDir, ex); }
        }
    }

    public class ServiceInfo
    {
        public bool Installed;
        public string Status = "";          // Running | Stopped | StartPending | ...
        public string BinaryPath = "";
        public string StartType = "";
    }

    /// <summary>The same program under its earlier name (Studio Manager). Nothing is taken over automatically:
    /// a configuration is only imported with --migrate-from-studiomanager, and only from a folder that just
    /// administrators could change. The earlier service is removed on install only when it really is that program.</summary>
    public static class Predecessor
    {
        public const string ServiceName = "Studio" + "Manager";

        static string Folder(Environment.SpecialFolder root)
        {
            string r = Environment.GetFolderPath(root);
            return string.IsNullOrEmpty(r) ? "" : Path.Combine(r, ServiceName);
        }

        public static string DataDir { get { return Folder(Environment.SpecialFolder.CommonApplicationData); } }

        /// <summary>Null when only administrators could have written this file or folder; otherwise why not.</summary>
        static string Untrusted(string path, bool folder)
        {
            if (!Util.IsWindows) return null;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return path + " is a link to another place";
            FileSystemSecurity sec = folder ? (FileSystemSecurity)Directory.GetAccessControl(path, AccessControlSections.Owner | AccessControlSections.Access)
                                            : File.GetAccessControl(path, AccessControlSections.Owner | AccessControlSections.Access);
            SecurityIdentifier owner = Trust.Owner(sec);
            if (owner == null || Trust.AdminState(owner) != 1) return path + " is owned by " + (owner != null ? Trust.Name(owner) : "an unknown account");
            string who = Trust.Untrusted(sec, Trust.ChangeFile, false);
            if (who != null) return path + " can be changed by " + who;
            return null;
        }

        /// <summary>--migrate-from-studiomanager: copy the earlier configuration into an empty settings folder after
        /// checking that only administrators could have written it. Returns what was done; throws when refused.</summary>
        public static string Migrate(string dataDir)
        {
            string old = DataDir;
            string from = Path.Combine(old, "config.json"), to = Path.Combine(dataDir, "config.json");
            if (!File.Exists(from)) throw new InvalidOperationException("There is no earlier configuration at " + from + ".");
            if (File.Exists(to)) throw new InvalidOperationException(to + " already exists; nothing was changed.");
            string why = Untrusted(old, true) ?? Untrusted(from, false);
            if (why != null)
                throw new InvalidOperationException("Refused: " + why + ". A configuration that another account could have written may run anything as Local System. " +
                                                    "Check the file yourself and load it with Service & settings > Import instead.");
            AppConfig c = Cfg.Parse(File.ReadAllText(from));
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Imported " + from + ". It runs these as Local System or in the signed-in session when switched on:");
            foreach (string line in Cfg.Executables(c)) sb.AppendLine("  " + line);
            DataDirSecurity.Secure(dataDir);
            File.Copy(from, to);
            string oldState = Path.Combine(old, "state"), newState = Path.Combine(dataDir, "state");
            if (Directory.Exists(oldState))
            {
                Directory.CreateDirectory(newState);
                foreach (string f in Directory.GetFiles(oldState))
                {
                    string t = Path.Combine(newState, Path.GetFileName(f));
                    if (File.Exists(t) || Untrusted(f, false) != null) continue;
                    File.Copy(f, t);
                }
            }
            DataDirSecurity.Secure(dataDir);
            return sb.ToString().TrimEnd();
        }

        /// <summary>The earlier service's program, when the service exists and really is Studio Manager; otherwise null.</summary>
        static string EarlierExe()
        {
            using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceName))
            {
                if (k == null) return null;
                string image = Convert.ToString(k.GetValue("ImagePath", "")).Trim();
                string exe = image.StartsWith("\"") ? image.Substring(1, Math.Max(0, image.IndexOf('"', 1) - 1)) : image.Split(' ')[0];
                if (!string.Equals(Path.GetFileName(exe), ServiceName + ".exe", StringComparison.OrdinalIgnoreCase)) return null;
                if (!string.Equals(Path.GetFileName(Path.GetDirectoryName(exe) ?? ""), ServiceName, StringComparison.OrdinalIgnoreCase)) return null;
                if (File.Exists(exe))
                {
                    FileVersionInfo v = FileVersionInfo.GetVersionInfo(exe);
                    if (!string.Equals(v.OriginalFilename ?? "", ServiceName + ".exe", StringComparison.OrdinalIgnoreCase)) return null;
                }
                return exe;
            }
        }

        /// <summary>Stop and delete the earlier service and its program folder. Returns what was done, or null.</summary>
        public static string RemoveService()
        {
            if (!Util.IsWindows) return null;
            string exe;
            try { exe = EarlierExe(); }
            catch (Exception) { exe = null; }
            if (exe == null) return null;
            StringBuilder sb = new StringBuilder();
            try
            {
                using (ServiceController sc = new ServiceController(ServiceName))
                {
                    if (sc.Status != ServiceControllerStatus.Stopped)
                    {
                        if (sc.Status != ServiceControllerStatus.StopPending) sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(45));
                    }
                }
            }
            catch (Exception ex) { sb.AppendLine("Could not stop the earlier service " + ServiceName + ": " + ex.Message); }
            Util.CaptureResult r = Util.RunCapture(Util.SystemExe("sc.exe"), new[] { "delete", ServiceName }, 30000);
            sb.AppendLine(r.ExitCode == 0 ? "Removed the earlier service " + ServiceName + "." : "Could not remove the earlier service " + ServiceName + ": " + (r.Output + r.Error).Trim());
            try
            {
                string dir = Path.GetDirectoryName(exe);
                string pf = Folder(Environment.SpecialFolder.ProgramFiles);
                if (r.ExitCode == 0 && string.Equals(Path.GetFullPath(dir).TrimEnd('\\'), Path.GetFullPath(pf).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) &&
                    Directory.Exists(dir) && (File.GetAttributes(dir) & FileAttributes.ReparsePoint) == 0)
                {
                    Thread.Sleep(1000);
                    Directory.Delete(dir, true);
                    sb.AppendLine("Removed " + dir + ".");
                }
            }
            catch (Exception) { }
            return sb.ToString().TrimEnd();
        }
    }

    public static class ServiceSetup
    {
        public static string InstallDir
        {
            get
            {
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                return Path.Combine(pf, "Watchkeeper");
            }
        }

        public static string InstalledExe { get { return Path.Combine(InstallDir, "Watchkeeper.exe"); } }

        public static bool IsAdmin()
        {
            if (!Util.IsWindows) return true;
            try
            {
                using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception) { return false; }
        }

        static string CrashTail(string dataDir)
        {
            try
            {
                string p = Path.Combine(dataDir, "service-crash.log");
                if (!File.Exists(p)) return " See the Windows Event Viewer (System log).";
                string t = File.ReadAllText(p);
                return "\n\nLast error recorded in " + p + ":\n" + (t.Length > 1500 ? t.Substring(t.Length - 1500) : t);
            }
            catch (Exception) { return ""; }
        }

        public static void CrashNote(string dataDir, Exception ex)
        {
            try
            {
                Directory.CreateDirectory(dataDir);
                File.AppendAllText(Path.Combine(dataDir, "service-crash.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Util.Inv) + " " + (ex != null ? ex.ToString() : "unknown error") + "\r\n\r\n");
            }
            catch (Exception) { }
        }

        /// <summary>Arguments are passed one by one and quoted by Windows rules (binPath contains quotes).</summary>
        static string Sc(out int code, params string[] args)
        {
            Util.CaptureResult r = Util.RunCapture(Util.SystemExe("sc.exe"), args, 30000);
            code = r.ExitCode;
            return (r.Output + r.Error).Trim();
        }

        static void ScOrThrow(params string[] args)
        {
            int code;
            string text = Sc(out code, args);
            if (code != 0) throw new InvalidOperationException("sc " + Util.JoinArgs(args) + " failed (" + code + "): " + text);
        }

        public static string[] CreateArgs(string bin)
        {
            return new[] { "create", KeeperService.Name, "binPath=", bin, "start=", "auto", "obj=", "LocalSystem", "DisplayName=", KeeperService.DisplayTitle };
        }

        public static ServiceInfo Query()
        {
            ServiceInfo i = new ServiceInfo();
            if (!Util.IsWindows) return i;
            try
            {
                foreach (ServiceController sc in ServiceController.GetServices())
                {
                    using (sc)
                    {
                        if (!string.Equals(sc.ServiceName, KeeperService.Name, StringComparison.OrdinalIgnoreCase)) continue;
                        i.Installed = true;
                        i.Status = sc.Status.ToString();
                    }
                }
                if (i.Installed)
                {
                    using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                               @"SYSTEM\CurrentControlSet\Services\" + KeeperService.Name))
                    {
                        if (k != null)
                        {
                            i.BinaryPath = Convert.ToString(k.GetValue("ImagePath", ""));
                            object start = k.GetValue("Start");
                            int s = start is int ? (int)start : -1;
                            object delayed = k.GetValue("DelayedAutostart");
                            i.StartType = s == 2 ? (delayed is int && (int)delayed == 1 ? "Automatic (delayed)" : "Automatic")
                                        : s == 3 ? "Manual" : s == 4 ? "Disabled" : "";
                        }
                    }
                }
            }
            catch (Exception) { }
            return i;
        }

        static void WaitFor(ServiceControllerStatus status, int seconds)
        {
            using (ServiceController sc = new ServiceController(KeeperService.Name))
            {
                try { sc.WaitForStatus(status, TimeSpan.FromSeconds(seconds)); }
                catch (System.ServiceProcess.TimeoutException) { }
            }
        }

        public static void StartService()
        {
            using (ServiceController sc = new ServiceController(KeeperService.Name))
            {
                if (sc.Status == ServiceControllerStatus.Running) return;
                sc.Start();
                DateTime end = DateTime.UtcNow.AddSeconds(30);
                while (DateTime.UtcNow < end)
                {
                    Thread.Sleep(500);
                    sc.Refresh();
                    if (sc.Status == ServiceControllerStatus.Running) return;
                    if (sc.Status == ServiceControllerStatus.Stopped)
                        throw new InvalidOperationException("The service stopped right after starting." + CrashTail(Engine.DefaultDataDir()));
                }
                throw new InvalidOperationException("The service did not report Running within 30 seconds." + CrashTail(Engine.DefaultDataDir()));
            }
        }

        public static void StopService()
        {
            using (ServiceController sc = new ServiceController(KeeperService.Name))
            {
                if (sc.Status == ServiceControllerStatus.Stopped) return;
                if (sc.Status != ServiceControllerStatus.StopPending) sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(45));
            }
        }

        /// <summary>Copy this program to Program Files, register (or update) the service and start it.
        /// Returns a description of what was done.</summary>
        public static string Install(string sourceExe, string dataDir)
        {
            if (!Util.IsWindows) throw new InvalidOperationException("installing a service needs Windows");
            if (!IsAdmin()) throw new InvalidOperationException("installing the service needs administrator rights");
            StringBuilder done = new StringBuilder();
            if (string.IsNullOrEmpty(dataDir)) dataDir = Engine.DefaultDataDir();
            SecureResult sec = DataDirSecurity.Secure(dataDir);       // throws when the folder cannot be protected
            foreach (string a in sec.Actions) done.AppendLine(a);
            string problem = DataDirSecurity.Verify(dataDir);
            if (problem != null) throw new InvalidOperationException("The settings folder could not be protected: " + problem + ". Nothing was installed.");
            string removed = Predecessor.RemoveService();
            if (removed != null) done.AppendLine(removed);
            ServiceInfo before = Query();
            if (before.Installed && before.Status != "Stopped")
            {
                StopService();
                done.AppendLine("Stopped the running service.");
            }

            Directory.CreateDirectory(InstallDir);
            string target = InstalledExe;
            if (!string.Equals(Path.GetFullPath(sourceExe), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            {
                // the old exe can stay locked for a moment after the service stops
                for (int attempt = 0; ; attempt++)
                {
                    try { File.Copy(sourceExe, target, true); break; }
                    catch (IOException)
                    {
                        if (attempt >= 20) throw;
                        Thread.Sleep(500);
                    }
                }
                done.AppendLine("Copied the program to " + target + ".");
            }

            string bin = Util.QuoteArg(target) + " --service";
            if (!string.IsNullOrEmpty(dataDir) && !string.Equals(Path.GetFullPath(dataDir).TrimEnd('\\'), Engine.DefaultDataDir().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                bin += " --data-dir " + Util.QuoteArg(dataDir);
            if (before.Installed)
            {
                ScOrThrow("config", KeeperService.Name, "binPath=", bin, "start=", "auto", "obj=", "LocalSystem");
                done.AppendLine("Updated the service registration.");
            }
            else
            {
                ScOrThrow(CreateArgs(bin));
                done.AppendLine("Registered the service (starts automatically at boot, runs as Local System).");
            }
            int code;
            Sc(out code, "description", KeeperService.Name, "Folder policies, scripts, program keep-alive (VVD, vMix) and vertical video conversion. Configuration: " +
                Path.Combine(dataDir, "config.json"));
            // restart after 5 s, 10 s, then every 30 s; failure counter resets after a day
            ScOrThrow("failure", KeeperService.Name, "reset=", "86400", "actions=", "restart/5000/restart/10000/restart/30000");
            Sc(out code, "failureflag", KeeperService.Name, "1");
            done.AppendLine("Recovery: restart automatically if the service stops unexpectedly.");

            try
            {
                if (!EventLog.SourceExists(AlertManager.EventSource)) EventLog.CreateEventSource(AlertManager.EventSource, "Application");
            }
            catch (Exception) { }

            StartService();
            done.AppendLine("Service started.");
            return done.ToString().Trim();
        }

        public static string Uninstall()
        {
            if (!Util.IsWindows) throw new InvalidOperationException("needs Windows");
            ServiceInfo i = Query();
            if (!i.Installed) return "The service is not installed.";
            if (i.Status != "Stopped") StopService();
            ScOrThrow("delete", KeeperService.Name);
            return "The service was stopped and removed. The configuration in " + Engine.DefaultDataDir() + " was kept.";
        }

        public static void Restart()
        {
            StopService();
            StartService();
        }
    }
}
