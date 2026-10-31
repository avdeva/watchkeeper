// The Windows service and its installation.
//
// Installation (Watchkeeper.exe --install, run as administrator; deploy.cmd does this):
//   1. stop the service if it runs, copy Watchkeeper.exe to C:\Program Files\Watchkeeper
//   2. create C:\ProgramData\Watchkeeper and give only SYSTEM and Administrators access to it
//   3. use a settings file that was handed over (only when the server has none, unless replacing is asked for)
//   4. register the service (automatic start, Local System, restarts itself after a failure) and start it
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
    public class WatchkeeperService : ServiceBase
    {
        public const string Name = "Watchkeeper";
        Engine engine;
        readonly object lk = new object();
        bool stopping;

        public WatchkeeperService()
        {
            ServiceName = Name;
            CanStop = true;
            CanShutdown = true;
            AutoLog = false;
        }

        protected override void OnStart(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { Setup.CrashNote(e.ExceptionObject as Exception); };
            // Windows allows 30 seconds to report Running; the engine starts in the background so a slow start never fails the service
            Thread t = new Thread(delegate()
            {
                try
                {
                    Engine e = new Engine(Engine.DefaultDataDir(), new SystemProcesses(), new SystemServices());
                    lock (lk) { if (stopping) return; engine = e; }
                    e.Start();
                }
                catch (Exception ex)
                {
                    Setup.CrashNote(ex);
                    ExitCode = 1064;          // with the failure flag set, Windows restarts the service
                    try { Stop(); } catch (Exception) { Environment.Exit(1064); }
                }
            });
            t.IsBackground = true;
            t.Name = "engine-start";
            t.Start();
        }

        protected override void OnStop()
        {
            try { RequestAdditionalTime(60000); } catch (Exception) { }
            Engine e;
            lock (lk) { stopping = true; e = engine; }
            if (e != null) e.Stop();
        }

        protected override void OnShutdown() { OnStop(); }
    }

    public static class Setup
    {
        public static string InstallDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Watchkeeper"); } }
        public static string InstalledExe { get { return Path.Combine(InstallDir, "Watchkeeper.exe"); } }

        public static bool IsAdmin()
        {
            if (!Util.IsWindows) return true;
            try { using (WindowsIdentity id = WindowsIdentity.GetCurrent()) return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); }
            catch (Exception) { return false; }
        }

        public static void CrashNote(Exception ex)
        {
            try
            {
                string dir = Engine.DefaultDataDir();
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "service-crash.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Util.Inv) + " " + ex + "\r\n\r\n");
            }
            catch (Exception) { }
        }

        public class ServiceInfo
        {
            public bool Installed;
            public string Status = "not installed";
            public string BinaryPath = "";
        }

        public static ServiceInfo Query()
        {
            ServiceInfo i = new ServiceInfo();
            if (!Util.IsWindows) return i;
            try
            {
                foreach (ServiceController sc in ServiceController.GetServices())
                    using (sc)
                        if (string.Equals(sc.ServiceName, WatchkeeperService.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            i.Installed = true;
                            i.Status = sc.Status == ServiceControllerStatus.Running ? "running" : sc.Status == ServiceControllerStatus.Stopped ? "stopped" : sc.Status.ToString().ToLowerInvariant();
                        }
                if (i.Installed)
                    using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + WatchkeeperService.Name))
                        if (k != null) i.BinaryPath = Convert.ToString(k.GetValue("ImagePath", ""));
            }
            catch (Exception) { }
            return i;
        }

        static string Sc(out int code, params string[] args)
        {
            Ffmpeg.Capture r = Ffmpeg.RunCapture(Util.SystemExe("sc.exe"), args, 30000);
            code = r.ExitCode;
            return (r.Out + r.Err).Trim();
        }

        static void ScOrThrow(params string[] args)
        {
            int code;
            string text = Sc(out code, args);
            if (code != 0) throw new InvalidOperationException("sc " + string.Join(" ", args) + " failed (" + code + "): " + text);
        }

        public static void StartService()
        {
            using (ServiceController sc = new ServiceController(WatchkeeperService.Name))
            {
                if (sc.Status == ServiceControllerStatus.Running) return;
                sc.Start();
                DateTime end = DateTime.UtcNow.AddSeconds(30);
                while (DateTime.UtcNow < end)
                {
                    Thread.Sleep(500);
                    sc.Refresh();
                    if (sc.Status == ServiceControllerStatus.Running) return;
                    if (sc.Status == ServiceControllerStatus.Stopped) throw new InvalidOperationException("the service stopped right after starting; see " + Path.Combine(Engine.DefaultDataDir(), "service-crash.log"));
                }
                throw new InvalidOperationException("the service did not report Running within 30 seconds");
            }
        }

        public static void StopService()
        {
            using (ServiceController sc = new ServiceController(WatchkeeperService.Name))
            {
                if (sc.Status == ServiceControllerStatus.Stopped) return;
                if (sc.Status != ServiceControllerStatus.StopPending) sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
            }
        }

        /// <summary>Only SYSTEM and Administrators may open the data folder (settings with passwords, logs, commands).
        /// Applied at every installation; existing files inherit it.</summary>
        public static void SecureDataDir(string dir)
        {
            Directory.CreateDirectory(dir);
            if (!Util.IsWindows) return;
            DirectorySecurity ds = new DirectorySecurity();
            ds.SetAccessRuleProtection(true, false);
            InheritanceFlags inh = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            ds.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inh, PropagationFlags.None, AccessControlType.Allow));
            ds.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inh, PropagationFlags.None, AccessControlType.Allow));
            ds.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            Directory.SetAccessControl(dir, ds);
            // files that had their own permissions get the folder's
            foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    FileSecurity fs = new FileSecurity();
                    fs.SetAccessRuleProtection(false, false);
                    File.SetAccessControl(f, fs);
                }
                catch (Exception) { }
            }
        }

        /// <summary>Who besides SYSTEM and Administrators can open the folder; empty when nobody.</summary>
        public static List<string> OthersWithAccess(string dir)
        {
            List<string> o = new List<string>();
            if (!Util.IsWindows || !Directory.Exists(dir)) return o;
            SecurityIdentifier system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            SecurityIdentifier admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            foreach (FileSystemAccessRule r in Directory.GetAccessControl(dir).GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (r.AccessControlType != AccessControlType.Allow) continue;
                SecurityIdentifier sid = (SecurityIdentifier)r.IdentityReference;
                if (sid.Equals(system) || sid.Equals(admins)) continue;
                string name;
                try { name = sid.Translate(typeof(NTAccount)).Value; } catch (Exception) { name = sid.Value; }
                o.Add(name);
            }
            return o;
        }

        /// <summary>Install or update. config: a settings file to use (null = keep / create). Returns what was done.</summary>
        public static List<string> Install(string sourceExe, string config, bool replaceConfig)
        {
            List<string> done = new List<string>();
            if (!Util.IsWindows) throw new InvalidOperationException("installing the service needs Windows");
            if (!IsAdmin()) throw new InvalidOperationException("installing needs administrator rights (right-click deploy.cmd > Run as administrator)");
            string dataDir = Engine.DefaultDataDir();

            // check a handed-over settings file before changing anything
            Settings handed = null;
            string handedText = null;
            bool handedV3 = false;
            if (config != null)
            {
                handedText = File.ReadAllText(config);
                JObject j = SettingsFile.ParseJson(handedText) as JObject;
                if (j == null) throw new InvalidOperationException(config + " does not contain settings");
                List<string> report = new List<string>();
                handedV3 = SettingsFile.IsVersion3(j);
                handed = handedV3 ? Import3.Convert(j, report) : SettingsFile.Parse(handedText, null);
                SettingsFile.Normalize(handed);
                if (handedV3) done.Add("Version 3 settings: the service converts them at its first start (see import-report.txt then).");
                List<Problem> problems = Validate.All(handed);
                foreach (Problem p in problems) done.Add((p.Level == "error" ? "PROBLEM in the settings: " : "Note: ") + p);
            }

            ServiceInfo before = Query();
            if (before.Installed && before.Status != "stopped")
            {
                StopService();
                done.Add("Stopped the running service.");
            }
            Directory.CreateDirectory(InstallDir);
            if (!Util.SamePath(sourceExe, InstalledExe))
            {
                for (int attempt = 0; ; attempt++)
                {
                    try { File.Copy(sourceExe, InstalledExe, true); break; }
                    catch (IOException)
                    {
                        if (attempt >= 20) throw new InvalidOperationException(InstalledExe + " is in use (is the manager open?); close it and try again");
                        Thread.Sleep(500);
                    }
                }
                done.Add("Copied the program to " + InstalledExe + ".");
            }

            SecureDataDir(dataDir);
            done.Add("Settings folder " + dataDir + ": only SYSTEM and Administrators have access.");

            SettingsFile sf = new SettingsFile(dataDir, null);
            if (handed != null)
            {
                if (!File.Exists(sf.Path) || replaceConfig)
                {
                    if (handedV3) sf.SaveText(handedText, "deploy");
                    else sf.Save(handed, "deploy");
                    done.Add((replaceConfig && before.Installed ? "Replaced the settings with " : "Settings taken from ") + config + " (the previous settings are kept in the backups folder): " + Engine.Summary(handed) + ".");
                }
                else done.Add("This server already has settings; they are kept. " + config + " was not used (use --replace-config to replace them).");
            }
            else if (!File.Exists(sf.Path) && sf.NewestReadable() == null) done.Add("No settings yet: an empty settings file is created at the first start. Open the manager to set up Watchkeeper.");

            string bin = "\"" + InstalledExe + "\" --service";
            if (before.Installed)
            {
                ScOrThrow("config", WatchkeeperService.Name, "binPath=", bin, "start=", "auto", "obj=", "LocalSystem", "DisplayName=", "Watchkeeper");
                done.Add("Updated the service registration.");
            }
            else
            {
                ScOrThrow("create", WatchkeeperService.Name, "binPath=", bin, "start=", "auto", "obj=", "LocalSystem", "DisplayName=", "Watchkeeper");
                done.Add("Registered the service Watchkeeper (starts with Windows, runs as Local System).");
            }
            int code;
            Sc(out code, "description", WatchkeeperService.Name, "Keeps programs running, moves and cleans up recordings, converts vertical video, GPIO to vMix, alerts. Settings: " + sf.Path);
            ScOrThrow("failure", WatchkeeperService.Name, "reset=", "86400", "actions=", "restart/5000/restart/10000/restart/30000");
            Sc(out code, "failureflag", WatchkeeperService.Name, "1");
            done.Add("If the service ever stops by itself, Windows starts it again (after 5, 10, then 30 seconds).");
            try { if (!EventLog.SourceExists("Watchkeeper")) EventLog.CreateEventSource("Watchkeeper", "Application"); } catch (Exception) { }
            StartService();
            done.Add("Started the service.");
            return done;
        }

        public static List<string> Uninstall()
        {
            List<string> done = new List<string>();
            if (!IsAdmin()) throw new InvalidOperationException("removing the service needs administrator rights");
            ServiceInfo i = Query();
            if (!i.Installed) { done.Add("The service is not installed."); return done; }
            if (i.Status != "stopped") { StopService(); done.Add("Stopped the service."); }
            ScOrThrow("delete", WatchkeeperService.Name);
            done.Add("Removed the service. The settings and logs in " + Engine.DefaultDataDir() + " are kept.");
            return done;
        }
    }
}
