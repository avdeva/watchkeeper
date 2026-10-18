// Security: a flood of new files cannot start unlimited script runs; ffmpeg and ffprobe only read local
// files as the container their extension names.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Watchkeeper;

namespace Watchkeeper.Tests
{
    public class FloodAndMediaTests : Env
    {
        JobManager Jobs()
        {
            ConfigStore store = Cfgs;
            return new JobManager(delegate
            {
                List<JobSpec> l = new List<JobSpec>();
                foreach (ScriptJob sc in store.Get().Scripts)
                {
                    ScriptJob script = sc;
                    JobSpec s = new JobSpec();
                    s.Key = "script:" + sc.Id;
                    s.Kind = "script";
                    s.Name = sc.Name;
                    s.Source = "scripts";
                    s.Enabled = sc.Enabled;
                    s.Run = delegate(RunContext ctx) { return ScriptRunner.Run(script, ctx, new SystemLauncher()); };
                    l.Add(s);
                }
                return l;
            }, Hub, D("jobs.json"));
        }

        [Fact]
        public void New_file_runs_are_limited_per_minute_and_a_backlog_raises_an_alert()
        {
            if (Util.IsWindows) throw new SkipException("uses a shell script");
            string watch = D("flood");
            Directory.CreateDirectory(watch);
            string log = D("runs.log");
            string script = D("t.sh");
            File.WriteAllText(script, "#!/bin/sh\necho \"$1\" >> " + log + "\n");
            Util.RunCapture("chmod", new[] { "+x", script }, 5000);
            ScriptJob s = new ScriptJob();
            s.Name = "Per file";
            s.Enabled = true;
            s.Command = script;
            s.Arguments = "{name}";
            s.Trigger = "file";
            s.WatchPath = watch;
            s.WatchPattern = "*.txt";
            s.StableSeconds = 0;
            s.MaxRunsPerMinute = 3;
            Set<List<ScriptJob>>("scripts", delegate(List<ScriptJob> l) { l.Clear(); l.Add(s); });

            FileTriggers ft = new FileTriggers(Cfgs.Get, Jobs(), Hub, D("state"));
            List<string> alerts = new List<string>();
            ft.Alert = delegate(string kind, string key, string title, string msg) { lock (alerts) alerts.Add(key + " " + title); };
            ft.BacklogAlert = 5;
            ft.Tick();                                         // first look: nothing there yet
            for (int i = 0; i < 10; i++) File.WriteAllText(Path.Combine(watch, "f" + i + ".txt"), "x");
            for (int i = 0; i < 6; i++) { ft.Tick(); Thread.Sleep(150); }
            int ran = File.Exists(log) ? File.ReadAllLines(log).Length : 0;
            Assert.Eq(3, ran);                                 // three runs in this minute, the other files wait
            Assert.Eq(1, alerts.Count);
            Assert.Contains(alerts[0], ":backlog");
            Assert.Contains(alerts[0], "files waiting");
        }

        [Fact]
        public void Rate_limit_is_kept_in_range()
        {
            ScriptJob s = new ScriptJob();
            s.MaxRunsPerMinute = -5;
            Cfg.NormalizeScript(s, new List<string>());
            Assert.Eq(0, s.MaxRunsPerMinute);
            s.MaxRunsPerMinute = 999999;
            Cfg.NormalizeScript(s, new List<string>());
            Assert.Eq(6000, s.MaxRunsPerMinute);
            Assert.Eq(30, new ScriptJob().MaxRunsPerMinute);
        }

        [Fact]
        public void Media_input_is_locked_to_local_files_and_the_named_container()
        {
            Assert.Eq(new[] { "-protocol_whitelist", "file", "-f", "mov" }, Media.InputArgs(@"T:\ISO\cam1.MP4").ToArray());
            Assert.Eq("matroska", Media.ContainerFor("a.mkv"));
            Assert.Eq("mxf", Media.ContainerFor("a.mxf"));
            Assert.Eq("mpegts", Media.ContainerFor("a.m2ts"));
            ProbeError e = Assert.Throws<ProbeError>(delegate { Media.InputArgs("playlist.m3u8"); });
            Assert.Contains(e.Message, "not a recording container");
            Assert.Throws<ProbeError>(delegate { Media.InputArgs("noextension"); });
            Assert.Eq(0.0, Media.ProbeDuration("ffprobe", "list.txt"));        // refused before anything runs
        }
        [Fact]
        public void Local_System_scripts_and_programs_need_a_full_path()
        {
            ScriptJob s = new ScriptJob();
            s.Name = "S";
            s.Command = "robocopy.exe";
            s.RunAs = "service";
            Assert.True(Cfg.ValidateScript(s, "s").Exists(delegate(string e) { return e.Contains("needs the full path"); }), "service script with a bare name refused");
            s.RunAs = "user";
            Assert.False(Cfg.ValidateScript(s, "s").Exists(delegate(string e) { return e.Contains("needs the full path"); }), "user-session script may use the search path");
            ProgramJob p = new ProgramJob();
            p.Name = "P";
            p.ExePath = "tool.exe";
            p.RunAs = "service";
            Assert.True(Cfg.ValidateProgram(p, "p").Exists(delegate(string e) { return e.Contains("needs its full path"); }), "service program with a bare name refused");
            p.RunAs = "user";
            Assert.False(Cfg.ValidateProgram(p, "p").Exists(delegate(string e) { return e.Contains("needs its full path"); }), "plain user-session program allowed");
            p.AllowElevation = true;
            Assert.True(Cfg.ValidateProgram(p, "p").Exists(delegate(string e) { return e.Contains("needs its full path"); }), "elevated program with a bare name refused");
        }
    }
}
