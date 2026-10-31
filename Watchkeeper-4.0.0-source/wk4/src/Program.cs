// Watchkeeper.exe: without arguments the manager window; otherwise one of the modes below.
//
//   --service                         run as the Windows service (used by Windows)
//   --install [--config file.json] [--replace-config]
//                                     install or update the service (as administrator); deploy.cmd uses this
//   --uninstall                       remove the service (settings and logs are kept)
//   --check file.json                 check a settings file and list its problems
//   --selftest                        check that everything works on this computer
//   --example file.json               write an example settings file (everything switched off) to edit for deploy.cmd
//   --console                         run the engine in this window (for finding a problem), Enter stops it
//   --version
//   --log file.txt                    also write everything printed to this file
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Windows.Forms;

namespace Watchkeeper
{
    static class Program
    {
        [DllImport("kernel32.dll")]
        static extern bool AttachConsole(int pid);

        static string outputLog;

        static void Say(string text)
        {
            Console.WriteLine(text);
            if (outputLog == null) return;
            try { File.AppendAllText(outputLog, text + Environment.NewLine); } catch (Exception) { }
        }

        [STAThread]
        static int Main(string[] args)
        {
            string mode = "manager", config = null, file = null;
            bool replace = false;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "--log" && i + 1 < args.Length) outputLog = args[++i];
                else if (a == "--config" && i + 1 < args.Length) config = args[++i];
                else if (a == "--replace-config") replace = true;
                else if (a == "--check" && i + 1 < args.Length) { mode = "check"; file = args[++i]; }
                else if (a == "--example" && i + 1 < args.Length) { mode = "example"; file = args[++i]; }
                else if (a.StartsWith("--")) mode = a.Substring(2);
            }
            if (mode == "service")
            {
                ServiceBase.Run(new WatchkeeperService());
                return 0;
            }
            if (mode == "manager")
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                if (Util.IsWindows && !Setup.IsAdmin())
                {
                    MessageBox.Show("Watchkeeper needs administrator rights: right-click Watchkeeper.exe and choose Run as administrator.", "Watchkeeper",
                                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return 5;
                }
                Application.Run(new UI.MainForm(new UI.Manager(Engine.DefaultDataDir())));
                return 0;
            }

            if (Util.IsWindows) AttachConsole(-1);
            if (outputLog != null) { try { File.WriteAllText(outputLog, ""); } catch (Exception) { outputLog = null; } }
            Say("");
            Say("Watchkeeper " + Engine.Version + " " + mode);
            try
            {
                switch (mode)
                {
                    case "version":
                        return 0;
                    case "install":
                        if (config != null && !File.Exists(config)) { Say("FAILED: " + config + " not found."); return 2; }
                        foreach (string line in Setup.Install(Application.ExecutablePath, config, replace)) Say("  " + line);
                        Say("Done. Open Watchkeeper.exe (as administrator) to check the settings.");
                        return 0;
                    case "uninstall":
                        foreach (string line in Setup.Uninstall()) Say("  " + line);
                        return 0;
                    case "check":
                        return Check(file);
                    case "example":
                        File.WriteAllText(file, SettingsFile.ToText(Examples.Studio()));
                        Say("Written " + file + " (everything switched off; edit it, then deploy.cmd " + Path.GetFileName(file) + ").");
                        return 0;
                    case "selftest":
                        return SelfTestCli();
                    case "console":
                        return Console_();
                }
                Say("Unknown option --" + mode + ". Options: --install [--config file] [--replace-config], --uninstall, --check file, --example file, --selftest, --console, --version, --log file");
                return 2;
            }
            catch (Exception ex)
            {
                Say("FAILED: " + ex.Message);
                return 1;
            }
        }

        static int Check(string file)
        {
            string text = File.ReadAllText(file);
            JObject j = SettingsFile.ParseJson(text) as JObject;
            if (j == null) { Say("FAILED: no settings in " + file); return 1; }
            List<string> notes = new List<string>();
            Settings s;
            if (SettingsFile.IsVersion3(j))
            {
                Say("This is a version 3 settings file; it is converted when it is used:");
                s = Import3.Convert(j, notes);
            }
            else s = SettingsFile.Parse(text, notes);
            SettingsFile.Normalize(s);
            foreach (string n in notes) Say("  note: " + n);
            List<Problem> problems = Validate.All(s);
            foreach (Problem p in problems) Say("  " + (p.Level == "error" ? "PROBLEM: " : "warning: ") + p);
            Say(Engine.Summary(s) + ".");
            bool errors = problems.Exists(delegate(Problem p) { return p.Level == "error"; });
            Say(errors ? "Problems found." : "OK.");
            return errors ? 1 : 0;
        }

        static int SelfTestCli()
        {
            string dir = Engine.DefaultDataDir();
            List<string> lines = null;
            EngineStatus st = Engine.ReadStatus(dir);
            if (st != null && st.Updated > 0 && Util.Now() - st.Updated < 15)
            {
                Say("Asking the service to run the checks (as Local System)...");
                string id = Engine.SendCommand(dir, "selftest", "");
                DateTime end = DateTime.Now.AddSeconds(60);
                while (DateTime.Now < end && lines == null)
                {
                    System.Threading.Thread.Sleep(300);
                    EngineStatus s = Engine.ReadStatus(dir);
                    CommandResult r = s == null ? null : s.Commands.Find(delegate(CommandResult c) { return c.Id == id; });
                    if (r != null) lines = r.Lines;
                }
                if (lines == null) Say("The service did not answer; running the checks here instead.");
            }
            if (lines == null) lines = SelfTest.Basic(dir);
            foreach (string l in lines) Say(l);
            return lines.Exists(delegate(string l) { return l.StartsWith("FAIL"); }) ? 1 : 0;
        }

        static int Console_()
        {
            Engine e = new Engine(Engine.DefaultDataDir(), new SystemProcesses(), new SystemServices());
            e.Hub.Echo = delegate(LogEntry l) { Console.WriteLine(l.Line()); };
            e.Start();
            Say("Running. Press Enter to stop.");
            Console.ReadLine();
            e.Stop();
            return 0;
        }
    }
}
