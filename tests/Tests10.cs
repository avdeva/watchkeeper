// Security: links are never followed by folder policies; the settings folder guard puts the service in safe mode.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Watchkeeper;

namespace Watchkeeper.Tests
{
    public class LinkTests : Env
    {
        static void Ln(string target, string link)
        {
            Process p = Process.Start(new ProcessStartInfo("ln", "-s \"" + target + "\" \"" + link + "\"") { UseShellExecute = false });
            p.WaitForExit();
            if (p.ExitCode != 0) throw new SkipException("cannot create symbolic links here");
        }

        static FolderPolicy Policy(string path, string action, string dest)
        {
            FolderPolicy f = new FolderPolicy();
            f.Name = "P";
            f.Path = path;
            f.Destination = dest ?? "";
            f.MinAgeSeconds = 0;
            f.Subfolders = true;
            FileRule r = new FileRule();
            r.Name = action;
            r.Action = action;
            r.Pattern = "*";
            r.KeepSubfolders = true;
            r.OnConflict = "overwrite";
            f.Rules.Add(r);
            Cfg.NormalizeFolder(f, new List<string>());
            return f;
        }

        TaskResult Run(FolderPolicy f, Cap cap) { return FolderRunner.Run(f, Cfgs.Get().General, cap.Log, false, null, 0, null); }

        [Fact]
        public void Policy_folder_that_is_a_link_is_refused()
        {
            string victim = D("victim");
            Directory.CreateDirectory(victim);
            File.WriteAllText(Path.Combine(victim, "important.dll"), "x");
            Directory.CreateDirectory(D("drive"));
            Ln(victim, D("drive", "ISO"));
            Cap cap = new Cap();
            TaskResult r = Run(Policy(D("drive", "ISO"), "delete", null), cap);
            Assert.Contains(r.Text, "Folder refused");
            Assert.Contains(r.Text, "is a link");
            Assert.True(File.Exists(Path.Combine(victim, "important.dll")), "nothing deleted through the link");
        }

        [Fact]
        public void Destination_link_and_linked_subfolder_are_refused()
        {
            string victim = D("system");
            Directory.CreateDirectory(victim);
            File.WriteAllText(Path.Combine(victim, "payload.dll"), "original");
            Directory.CreateDirectory(D("cache", "x"));
            File.WriteAllText(D("cache", "x", "payload.dll"), "evil");
            Directory.CreateDirectory(D("data"));
            Ln(victim, D("data", "x"));                            // destination sub-folder replaced by a link
            Cap cap = new Cap();
            TaskResult r = Run(Policy(D("cache"), "move", D("data")), cap);
            Assert.Eq(1, r.Failed, cap.Text());
            Assert.Contains(cap.Text(), "is a link");
            Assert.Eq("original", File.ReadAllText(Path.Combine(victim, "payload.dll")));
            Assert.True(File.Exists(D("cache", "x", "payload.dll")), "source left alone");

            Ln(victim, D("linkdest"));                             // destination root itself a link
            Cap cap2 = new Cap();
            TaskResult r2 = Run(Policy(D("cache"), "copy", D("linkdest")), cap2);
            Assert.Eq(1, r2.Failed, cap2.Text());
            Assert.Eq("original", File.ReadAllText(Path.Combine(victim, "payload.dll")));
        }

        [Fact]
        public void Linked_files_and_linked_subfolders_in_the_source_are_left_alone()
        {
            string victim = D("secret");
            Directory.CreateDirectory(victim);
            File.WriteAllText(Path.Combine(victim, "keys.txt"), "secret");
            Directory.CreateDirectory(D("in"));
            File.WriteAllText(D("in", "a.mp4"), "video");
            Ln(Path.Combine(victim, "keys.txt"), D("in", "keys.mp4"));     // file link
            Ln(victim, D("in", "sub"));                                    // folder link
            Cap cap = new Cap();
            TaskResult r = Run(Policy(D("in"), "move", D("out")), cap);
            Assert.Eq(1, r.Moved, cap.Text());
            Assert.True(File.Exists(D("out", "a.mp4")));
            Assert.False(File.Exists(D("out", "keys.mp4")), "the linked file is not copied out");
            Assert.True(File.Exists(Path.Combine(victim, "keys.txt")));
            Cap cap2 = new Cap();
            Run(Policy(D("in"), "delete", null), cap2);
            Assert.True(File.Exists(Path.Combine(victim, "keys.txt")), "nothing deleted through links");
        }

        [Fact]
        public void Existing_target_link_is_replaced_not_written_through()
        {
            string victim = D("victim2");
            Directory.CreateDirectory(victim);
            File.WriteAllText(Path.Combine(victim, "service.dll"), "original");
            Directory.CreateDirectory(D("src2"));
            File.WriteAllText(D("src2", "service.dll"), "evil");
            Directory.CreateDirectory(D("dst2"));
            Ln(Path.Combine(victim, "service.dll"), D("dst2", "service.dll"));
            Ln(Path.Combine(victim, "service.dll"), D("dst2", "service.dll.copying"));
            Cap cap = new Cap();
            FileOps.ForceCrossVolume = true;
            try { Run(Policy(D("src2"), "move", D("dst2")), cap); }
            finally { FileOps.ForceCrossVolume = false; }
            Assert.Eq("original", File.ReadAllText(Path.Combine(victim, "service.dll")), cap.Text());
        }
    }

    public class SafeModeTests : Env
    {
        public override void Dispose()
        {
            DataDirSecurity.Verifier = null;
            base.Dispose();
        }

        [Fact]
        public void Unprotected_settings_folder_switches_everything_off()
        {
            string problem = "the settings folder is owned by STUDIO\\operator";
            Set<GeneralCfg>("general", delegate(GeneralCfg g) { g.Modules.Scripts = true; g.Modules.Folders = true; });
            ConfigStore s = new ConfigStore(D("config.json"), Hub.Logger("config"));
            s.Guard = delegate { return problem; };
            s.Load(false);
            Assert.False(s.Get().General.Modules.Scripts, "scripts off in safe mode");
            Assert.False(s.Get().General.Modules.Folders);
            Assert.Contains(s.SafeReason, "owned by STUDIO");
            Assert.True(s.Problems[0].StartsWith("SAFE MODE"), s.Problems[0]);
            problem = null;
            s.Reload();
            Assert.True(s.Get().General.Modules.Scripts, "back on once protected");
            Assert.Eq(null, s.SafeReason);
        }

        [Fact]
        public void Running_from_the_backup_copy_is_safe_mode()
        {
            Set<GeneralCfg>("general", delegate(GeneralCfg g) { g.Modules.Scripts = true; });
            Set<GeneralCfg>("general", delegate(GeneralCfg g) { g.ServerLabel = "x"; });     // .bak now exists
            File.WriteAllText(D("config.json"), "{ broken");
            ConfigStore s = new ConfigStore(D("config.json"), Hub.Logger("config"));
            s.Guard = delegate { return null; };
            s.Load(false);
            Assert.Contains(s.SafeReason, "backup copy");
            Assert.False(s.Get().General.Modules.Scripts);
            ConfigStore manager = new ConfigStore(D("config.json"), null);       // no guard: the manager sees the real settings
            manager.Load(false);
            Assert.True(manager.Get().General.Modules.Scripts);
        }
    }
}

namespace Watchkeeper.Tests
{
    public class InjectionTests : Env
    {
        [Fact]
        public void Unsafe_file_names_are_not_passed_to_scripts()
        {
            Assert.Null(ScriptRunner.UnsafeFileName(@"T:\Drop\Cam 1 - 23 July 2026.mp4"));
            Assert.Contains(ScriptRunner.UnsafeFileName(@"T:\Drop\a&net user x P@ss /add&.mp4"), "'&'");
            Assert.Contains(ScriptRunner.UnsafeFileName(@"T:\Drop\%COMSPEC%.mp4"), "'%'");
            Assert.Contains(ScriptRunner.UnsafeFileName(@"T:\Drop\!x!.mp4"), "'!'");
            Assert.Contains(ScriptRunner.UnsafeFileName(@"T:\Drop\-Destination.mp4"), "starts with '-'");
            Assert.Contains(ScriptRunner.UnsafeFileName(@"T:\a^b\c.mp4"), "'^'");

            string marker = D("pwned");
            string script = D("s.sh");
            File.WriteAllText(script, "#!/bin/sh\nexit 0\n");
            ScriptJob s = new ScriptJob();
            s.Name = "S";
            s.Command = script;
            s.Arguments = "{file}";
            RunContext ctx = new RunContext();
            ctx.File = D("in", "x&touch " + marker + "&.mp4");
            ctx.Log = new Cap().Log;
            TaskResult r = ScriptRunner.Run(s, ctx, new SystemLauncher());
            Assert.False(r.Ok);
            Assert.Contains(r.Text, "File name refused");
            Assert.False(File.Exists(marker));
        }

        [Fact]
        public void File_placeholders_are_refused_for_batch_files()
        {
            ScriptJob s = new ScriptJob();
            s.Name = "B";
            s.Command = @"C:\Scripts\move.cmd";
            s.Arguments = "{file} {destination}";
            List<string> e = Cfg.ValidateScript(s, "script 1");
            Assert.True(e.Exists(delegate(string x) { return x.Contains("{file} cannot be used with a .bat or .cmd script"); }), string.Join("; ", e.ToArray()));
            s.Arguments = "{destination}";
            Assert.False(Cfg.ValidateScript(s, "script 1").Exists(delegate(string x) { return x.Contains("cannot be used"); }));
        }
    }
}

namespace Watchkeeper.Tests
{
    public class TrustTests : Env
    {
        FakeWorld world = new FakeWorld();
        List<string> alerts = new List<string>();
        string exe;

        public TrustTests()
        {
            PathTrust.Checker = delegate(string p) { return p.Contains("unsafe") ? "'" + p + "' can be changed by BUILTIN\\Users (Modify)" : null; };
            exe = D("unsafe", "vMix64.exe");
            Directory.CreateDirectory(D("unsafe"));
            File.WriteAllText(exe, "fake");
        }

        public override void Dispose()
        {
            PathTrust.Checker = null;
            base.Dispose();
        }

        void Program(string runAs, bool elevate)
        {
            Set<List<ProgramJob>>("programs", delegate(List<ProgramJob> l)
            {
                l.Clear();
                ProgramJob p = Templates.VMix();
                p.ExePath = exe;
                p.Enabled = true;
                p.StartDelaySeconds = 0;
                p.RunAs = runAs;
                p.AllowElevation = elevate;
                l.Add(p);
            });
        }

        Supervisor Make()
        {
            Supervisor s = new Supervisor(Cfgs.Get, world, world, world, Hub);
            s.Alert = delegate(string kind, string key, string title, string msg) { alerts.Add(kind + ": " + title + " " + msg); };
            return s;
        }

        [Fact]
        public void Unprotected_program_is_not_started_as_local_system_or_elevated()
        {
            Program("service", false);
            Supervisor s = Make();
            s.Tick(DateTime.Now.AddMinutes(1));
            Assert.Eq(0, world.Starts.Count);
            Assert.Contains(s.Snapshot()[0].StateText, "not started");
            Assert.True(alerts.Exists(delegate(string a) { return a.Contains("not protected"); }), string.Join("; ", alerts.ToArray()));

            Program("user", true);
            s = Make();
            s.Tick(DateTime.Now.AddMinutes(1));
            Assert.Eq(0, world.Starts.Count);

            Program("user", false);           // runs as the signed-in user only: no extra rights to gain
            s = Make();
            s.Tick(DateTime.Now.AddMinutes(1));
            Assert.Eq(1, world.Starts.Count);
            Assert.False(world.Starts[0].AllowElevation);
        }

        [Fact]
        public void Same_named_process_from_elsewhere_is_not_taken_for_the_program()
        {
            exe = D("safe", "vMix64.exe");
            Directory.CreateDirectory(D("safe"));
            File.WriteAllText(exe, "fake");
            Program("user", false);
            ProcInfo spoof = new ProcInfo();
            spoof.Pid = 66;
            spoof.Name = "vMix64";
            spoof.SessionId = 1;
            spoof.Path = D("users", "evil", "vMix64.exe");
            world.Procs.Add(spoof);
            Supervisor s = Make();
            s.Tick(DateTime.Now.AddMinutes(1));
            Assert.Eq(1, world.Starts.Count);                          // the look-alike does not count as running
            ProcInfo other = new ProcInfo();
            other.Pid = 77;
            other.Name = "vMix64";
            other.SessionId = 0;                                       // same name in another session
            world.Procs.Add(other);
            Assert.False(world.Kills.Contains(66) || world.Kills.Contains(77));
        }

        [Fact]
        public void Scripts_and_ffmpeg_from_unprotected_folders_do_not_run_as_local_system()
        {
            ScriptJob s = new ScriptJob();
            s.Name = "S";
            s.Command = D("unsafe", "s.sh");
            File.WriteAllText(s.Command, "#!/bin/sh\nexit 0\n");
            s.RunAs = "service";
            RunContext ctx = new RunContext();
            ctx.Log = new Cap().Log;
            TaskResult r = ScriptRunner.Run(s, ctx, new SystemLauncher());
            Assert.False(r.Ok);
            Assert.Contains(r.Text, "can be changed by BUILTIN\\Users");

            FfmpegRunResult f = FfmpegExec.Run(new SystemLauncher(), new SystemSessions(), D("unsafe", "ffmpeg"), new List<string>(), false, "normal", 1000, null, null, null);
            Assert.Contains(f.StartError, "ffmpeg not run");

            AppConfig c = Cfgs.Get();
            ScriptJob sj = new ScriptJob();
            sj.Name = "Move";
            sj.Command = D("unsafe", "move.ps1");
            c.Scripts.Add(sj);
            List<string> u = Cfg.UntrustedExecutables(c);
            Assert.Eq(1, u.Count);
            Assert.Contains(u[0], "Script 'Move'");
        }
    }
}
