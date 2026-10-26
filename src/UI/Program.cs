// Entry point.
//
//   Watchkeeper.exe                      manager window (asks for administrator rights)
//   Watchkeeper.exe --service            run as the Windows service (started by Windows)
//   Watchkeeper.exe --console            run the engine in a console window (troubleshooting)
//   Watchkeeper.exe --install            copy to Program Files, register and start the service
//   Watchkeeper.exe --uninstall          stop and remove the service (configuration is kept)
//   Watchkeeper.exe --validate <file>    check a configuration file, exit code 0 = fine
//   Watchkeeper.exe --deploy [--config <file>] [--profile studio|watchdog] [--server-name <name>]
//                                          unattended set-up of a server: writes the configuration (or a starter for
//                                          the profile when none exists), encrypts its passwords, installs and starts the service
//   Watchkeeper.exe --export-template <file>   current configuration without passwords and server name, for other servers
//   Watchkeeper.exe --write-starter <file> [--profile studio|watchdog]   a starter configuration file
//   Watchkeeper.exe --migrate-from-studiomanager   import the configuration of the earlier name (checked first)
//   --allow-untrusted-paths   deploy even when a program or script is in a folder other accounts can change
//   common: --data-dir <folder>            default C:\ProgramData\Watchkeeper
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading;
using System.Windows.Forms;

namespace Watchkeeper.UI
{
    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [DllImport("kernel32.dll")]
        static extern bool AttachConsole(int pid);

        [DllImport("kernel32.dll")]
        static extern bool AllocConsole();

        static bool crashShown;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetDefaultDllDirectories(int flags);

        [STAThread]
        static int Main(string[] args)
        {
            // Windows libraries only from the system folder, never from the folder Watchkeeper.exe was started from
            if (Util.IsWindows) { try { SetDefaultDllDirectories(0x00000800); } catch (Exception) { } }
            string dataDir = null, page = null, validate = null, configFile = null, profile = null, serverName = null, outFile = null;
            string mode = "manager";
            bool noElevate = false;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "--service") mode = "service";
                else if (a == "--console") mode = "console";
                else if (a == "--install") mode = "install";
                else if (a == "--uninstall") mode = "uninstall";
                else if (a == "--no-elevate") noElevate = true;
                else if (a == "--validate" && i + 1 < args.Length) { mode = "validate"; validate = args[++i]; }
                else if (a == "--deploy") mode = "deploy";
                else if (a == "--migrate-from-studiomanager") mode = "migrate";
                else if (a == "--allow-untrusted-paths") allowUntrusted = true;
                else if (a == "--export-template" && i + 1 < args.Length) { mode = "export"; outFile = args[++i]; }
                else if (a == "--write-starter" && i + 1 < args.Length) { mode = "starter"; outFile = args[++i]; }
                else if (a == "--config" && i + 1 < args.Length) configFile = args[++i];
                else if (a == "--profile" && i + 1 < args.Length) profile = args[++i].ToLowerInvariant();
                else if (a == "--server-name" && i + 1 < args.Length) serverName = args[++i];
                else if (a == "--data-dir" && i + 1 < args.Length) dataDir = args[++i];
                else if (a == "--page" && i + 1 < args.Length) page = args[++i];
            }
            if (string.IsNullOrEmpty(dataDir)) dataDir = Engine.DefaultDataDir();
            dataDir = Path.GetFullPath(dataDir);

            // a settings folder elsewhere than the default: only where administrators alone control the folders above it
            if (Util.IsWindows && mode != "validate" && mode != "starter" &&
                !string.Equals(dataDir.TrimEnd('\\'), Engine.DefaultDataDir().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                string why = PathTrust.CheckSettingsLocation(dataDir);
                if (why != null)
                {
                    string text = "Watchkeeper will not use " + dataDir + " as its settings folder: " + why + ".\n\n" +
                                  "Use the default folder (no --data-dir), or a folder whose parent folders only administrators can change.";
                    if (mode == "manager") MessageBox.Show(text, "Watchkeeper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    else if (mode != "service") { Console_(); Console.WriteLine(text); }
                    return 7;
                }
            }

            // With administrator rights, run only from a folder just administrators can change: files planted next to
            // Watchkeeper.exe (or the program itself, swapped) would otherwise run with those rights.
            if (Util.IsWindows && mode != "service" && mode != "validate" && mode != "export" && mode != "starter" && ServiceSetup.IsAdmin())
            {
                string why = PathTrust.Check(Application.ExecutablePath);
                if (why != null)
                {
                    string text = "Watchkeeper will not run with administrator rights from this folder: " + why + ".\n\n" +
                                  "Copy Watchkeeper.exe (and deploy.cmd) to a folder only administrators can change, for example your own " +
                                  "Downloads or Desktop folder, or C:\\Program Files\\Watchkeeper, and start it from there.";
                    if (mode == "manager") MessageBox.Show(text, "Watchkeeper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    else { Console_(); Console.WriteLine(text); }
                    return 6;
                }
            }

            switch (mode)
            {
                case "service":
                    ServiceBase.Run(new KeeperService(dataDir));
                    return 0;
                case "console":
                    return RunConsole(dataDir);
                case "validate":
                    return Validate(validate);
                case "install":
                case "uninstall":
                    return InstallCommand(mode, dataDir);
                case "deploy":
                    return Deploy(dataDir, configFile, profile, serverName);
                case "migrate":
                    return Migrate(dataDir);
                case "export":
                    return ExportTemplate(dataDir, outFile);
                case "starter":
                    return WriteStarter(outFile, profile);
            }
            return RunManager(dataDir, page, noElevate, args);
        }

        static void Console_()
        {
            if (!Util.IsWindows) return;
            try { if (!AttachConsole(-1)) AllocConsole(); } catch (Exception) { }
        }

        static int RunConsole(string dataDir)
        {
            Console_();
            Engine e = new Engine(dataDir, new EngineOptions { Mode = "console" });
            e.Hub.Echo = true;
            ManualResetEvent quit = new ManualResetEvent(false);
            Console.CancelKeyPress += delegate(object s, ConsoleCancelEventArgs a) { a.Cancel = true; quit.Set(); };
            e.Start();
            Console.WriteLine("Watchkeeper engine running. Press Ctrl+C to stop.");
            quit.WaitOne();
            e.Shutdown();
            return 0;
        }

        static int Validate(string file)
        {
            Console_();
            try
            {
                AppConfig c = Cfg.Parse(File.ReadAllText(file));
                List<string> problems = Cfg.ValidateAll(c);
                foreach (string p in problems) Console.WriteLine("problem: " + p);
                Console.WriteLine(problems.Count == 0 ? "OK: " + c.Folders.Count + " folder(s), " + c.Programs.Count + " program(s), " + c.Scripts.Count + " script(s)"
                                                      : problems.Count + " problem(s)");
                return problems.Count == 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine("cannot read " + file + ": " + ex.Message);
                return 2;
            }
        }

        static bool allowUntrusted;

        static int Migrate(string dataDir)
        {
            Console_();
            try
            {
                if (Util.IsWindows && !ServiceSetup.IsAdmin()) { Console.WriteLine("Needs administrator rights."); return 5; }
                Console.WriteLine(Predecessor.Migrate(dataDir));
                Console.WriteLine("Done. Open Watchkeeper.exe to check the settings, then use Install / update service.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed: " + ex.Message);
                return 1;
            }
        }

        static string deployLog;

        static void Out(string text)
        {
            Console.WriteLine(text);
            if (deployLog == null) return;
            try { File.AppendAllText(deployLog, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ", Util.Inv) + text + Environment.NewLine); } catch (Exception) { }
        }

        /// <summary>Set up this server without the manager window (for deploying to many servers).</summary>
        static int Deploy(string dataDir, string configFile, string profile, string serverName)
        {
            Console_();
            try
            {
                if (Util.IsWindows && !ServiceSetup.IsAdmin())
                {
                    Console.WriteLine("Needs administrator rights: right-click deploy.cmd (or the command prompt) and choose Run as administrator.");
                    return 5;
                }
                // protect the settings folder before the configuration (with its passwords) is written into it
                SecureResult sec = DataDirSecurity.Secure(dataDir);
                deployLog = Path.Combine(dataDir, "deploy.log");
                foreach (string a in sec.Actions) Out(a);
                Out("Watchkeeper " + Engine.Version + " deploy on " + Environment.MachineName + ", data folder " + dataDir);
                if (profile != null && Array.IndexOf(Cfg.Profiles, profile) < 0)
                {
                    Out("Unknown profile '" + profile + "'. Use: " + string.Join(", ", Cfg.Profiles));
                    return 2;
                }
                if (Util.IsWindows && !ServiceSetup.IsAdmin())
                {
                    Out("Needs administrator rights: right-click deploy.cmd (or the command prompt) and choose Run as administrator.");
                    return 5;
                }
                string target = Path.Combine(dataDir, "config.json");
                AppConfig c = null;
                int plainInFile = 0;
                if (configFile != null)
                {
                    c = Cfg.Parse(File.ReadAllText(configFile));
                    Out("Using the configuration in " + Path.GetFullPath(configFile));
                    Cfg.ForEachSecret(c, delegate(string label, string v)
                    {
                        if (!string.IsNullOrEmpty(v) && !Secrets.IsEncrypted(v)) plainInFile++;
                        return v;
                    });
                }
                else if (!File.Exists(target))
                {
                    c = Cfg.Starter(profile ?? "studio");
                    Out("No configuration yet: writing the " + (profile ?? "studio") + " starter configuration (everything switched off).");
                }
                else Out("Keeping the existing configuration " + target);
                if (c != null)
                {
                    if (serverName != null) c.General.ServerLabel = serverName.Trim();
                    int n = Cfg.ProtectSecrets(c);
                    List<string> problems = Cfg.ValidateAll(c);
                    if (problems.Count > 0)
                    {
                        foreach (string p in problems) Out("problem: " + p);
                        Out("Nothing changed: fix the configuration file and run again.");
                        return 1;
                    }
                    Out("This configuration runs (when each entry is switched on):");
                    foreach (string line in Cfg.Executables(c)) Out("  " + line);
                    List<string> untrusted = Cfg.UntrustedExecutables(c);
                    if (untrusted.Count > 0 && !allowUntrusted)
                    {
                        foreach (string u in untrusted) Out("refused: " + u);
                        Out("Nothing changed. Move these files to a folder only administrators can change (for example Program Files), " +
                            "or run again with --allow-untrusted-paths (they will still not run until they are protected).");
                        return 1;
                    }
                    if (File.Exists(target))
                    {
                        File.Copy(target, target + ".before-deploy", true);
                        new ConfigStore(target, null).KeepCurrent("before-deploy");
                    }
                    Util.WriteAtomic(target, Cfg.ToText(c));
                    Out("Configuration written to " + target + (n > 0 ? " (" + n + " password(s) encrypted for this computer)" : "") + ".");
                }
                if (!Util.IsWindows)
                {
                    Out("The service can only be installed on Windows; stopping here.");
                    return 0;
                }
                Out(ServiceSetup.Install(Application.ExecutablePath, dataDir));
                Out("Done. Open Watchkeeper.exe to check the settings and switch on what this server should do.");
                if (plainInFile > 0)
                    Out("WARNING: " + Path.GetFullPath(configFile) + " holds " + plainInFile + " password(s), token(s) or secret address(es) in plain text. " +
                        "They are now encrypted in this server's settings; delete that file (and any copies) now.");
                return 0;
            }
            catch (Exception ex)
            {
                Out("Failed: " + ex.Message);
                return 1;
            }
        }

        static int ExportTemplate(string dataDir, string file)
        {
            Console_();
            try
            {
                string path = Path.Combine(dataDir, "config.json");
                AppConfig c = Cfg.Parse(File.ReadAllText(path));
                List<string> removed = new List<string>();
                File.WriteAllText(file, Cfg.ToText(Cfg.ForExport(c, removed)));
                Console.WriteLine("Written " + file + (removed.Count > 0 ? ". Passwords removed (type them in the file before deploying, or in the manager afterwards): " + string.Join("; ", removed.ToArray()) : "."));
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed: " + ex.Message);
                return 1;
            }
        }

        static int WriteStarter(string file, string profile)
        {
            Console_();
            try
            {
                if (profile != null && Array.IndexOf(Cfg.Profiles, profile) < 0) { Console.WriteLine("Unknown profile '" + profile + "'."); return 2; }
                File.WriteAllText(file, Cfg.ToText(Cfg.Starter(profile ?? "studio")));
                Console.WriteLine("Written " + file);
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed: " + ex.Message);
                return 1;
            }
        }

        static int InstallCommand(string mode, string dataDir)
        {
            Console_();
            try
            {
                string text = mode == "install" ? ServiceSetup.Install(Application.ExecutablePath, dataDir) : ServiceSetup.Uninstall();
                Console.WriteLine(text);
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed: " + ex.Message);
                return 1;
            }
        }

        static int RunManager(string dataDir, string page, bool noElevate, string[] args)
        {
            if (Util.IsWindows)
            {
                try { SetProcessDPIAware(); } catch (Exception) { }
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (Util.IsWindows && !noElevate && !ServiceSetup.IsAdmin())
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath, Util.JoinArgs(args));
                    psi.UseShellExecute = true;
                    psi.Verb = "runas";
                    Process.Start(psi);
                    return 0;
                }
                catch (Exception)
                {
                    MessageBox.Show("Watchkeeper needs administrator rights to change the service and its configuration.",
                                    "Watchkeeper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return 1;
                }
            }

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { OnCrash(e.Exception); };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { OnCrash(e.ExceptionObject as Exception); };

            Manager.FirstRun = delegate { return SetupDialog.Ask(); };
            Manager manager;
            try
            {
                manager = new Manager(dataDir);
            }
            catch (Exception ex)
            {
                StartupFailed(ex, dataDir);
                return 2;
            }
            try
            {
                using (MainForm form = new MainForm(manager, page))
                {
                    Application.Run(form);
                }
            }
            catch (Exception ex)
            {
                StartupFailed(ex, dataDir);
                return 2;
            }
            finally
            {
                manager.Dispose();
            }
            return 0;
        }

        static void StartupFailed(Exception ex, string dataDir)
        {
            string text = ex.ToString();
            string file = Path.Combine(Path.GetTempPath(), "Watchkeeper-startup-error.txt");
            try { File.WriteAllText(file, text); } catch (Exception) { }
            string shortText = text.Length > 900 ? text.Substring(0, 900) + "..." : text;
            MessageBox.Show("Watchkeeper could not start.\n\nData folder: " + dataDir + "\n\n" + shortText + "\n\n(Full text: " + file + ")",
                            "Watchkeeper", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        static void OnCrash(Exception ex)
        {
            if (crashShown) return;
            crashShown = true;
            try
            {
                MessageBox.Show("Something unexpected went wrong in the manager window. The service is not affected.\n\n" +
                                (ex != null ? ex.Message : "unknown error"),
                                "Watchkeeper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception) { }
        }
    }
}
