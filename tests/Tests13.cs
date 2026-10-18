// Security fixes of 3.1.1: custom webhook placeholders filled in one pass, addresses in chat cannot be clicked, logins
// are not sent over plain http to other computers, the account ffmpeg runs as, the watched folder of a file-triggered
// script and the start-in folder of scripts run as Local System.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Watchkeeper;

namespace Watchkeeper.Tests
{
    public class Fixes311Tests : Env
    {
        public override void Dispose()
        {
            PathTrust.Checker = null;
            base.Dispose();
        }

        [Fact]
        public void Custom_webhook_placeholders_are_filled_in_one_pass()
        {
            Dictionary<string, string> v = new Dictionary<string, string>();
            v["title"] = "file {server}.mp4";
            v["server"] = "STUDIO-1";
            v["message"] = "a \"quoted\" {title}";
            string body = AlertManager.FillTemplate("{\"t\": {title}, \"s\": {server}, \"m\": {message}, \"x\": \"{unknown}\"}", v);
            JObject o = (JObject)Json.Parse(body);
            Assert.Eq("file {server}.mp4", o.Str("t", ""));
            Assert.Eq("STUDIO-1", o.Str("s", ""));
            Assert.Eq("a \"quoted\" {title}", o.Str("m", ""));
            Assert.Eq("{unknown}", o.Str("x", ""));
        }

        [Fact]
        public void Addresses_and_domain_like_names_in_chat_cannot_be_clicked()
        {
            string t = AlertManager.Defang("open https://evil.example/x or www.evil.example, file report.zip, 3.5 GB");
            Assert.False(t.Contains("://"), t);
            Assert.Contains(t, "https[:]//evil.​example");
            Assert.Contains(t, "www[.]evil.​example");
            Assert.Contains(t, "report.​zip");
            Assert.Contains(t, "3.5 GB");
            Assert.Eq("plain text", AlertManager.Defang("plain text"));
            Assert.Contains(AlertManager.SlackText("<https://a.example|x>"), "&lt;https[:]//a.​example|x&gt;");
        }

        [Fact]
        public void A_login_is_not_sent_over_plain_http_to_another_computer()
        {
            Assert.NotNull(Net.PlainLoginProblem(new Uri("http://10.0.0.5:8088/api"), "admin", null, false));
            Assert.Null(Net.PlainLoginProblem(new Uri("http://127.0.0.1:8088/api"), "admin", null, false));
            Assert.Null(Net.PlainLoginProblem(new Uri("http://localhost/api"), "admin", null, false));
            Assert.Null(Net.PlainLoginProblem(new Uri("http://[::1]/api"), "admin", null, false));
            Assert.Null(Net.PlainLoginProblem(new Uri("https://10.0.0.5/api"), "admin", null, false));
            Assert.Null(Net.PlainLoginProblem(new Uri("http://10.0.0.5/api"), "", null, false));
            Assert.Null(Net.PlainLoginProblem(new Uri("http://10.0.0.5/api"), "admin", null, true));
            List<KeyValuePair<string, string>> h = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("Authorization", "Bearer x") };
            Assert.NotNull(Net.PlainLoginProblem(new Uri("http://10.0.0.5/api"), "", h, false));

            // refused before anything is sent (nothing listens on this address)
            HttpRequestSpec s = new HttpRequestSpec();
            s.Url = "http://192.0.2.1:9/x";
            s.User = "admin";
            s.Password = "secret";
            HttpResult r = Net.Send(s, null);
            Assert.False(r.Answered);
            Assert.Contains(r.Error, "not sent to 192.0.2.1 over plain http");

            GpioCfg g = new GpioCfg();
            VmixTarget v = new VmixTarget();
            v.Name = "vMix B";
            v.Host = "10.0.0.6";
            v.User = "user";
            g.Vmix.Add(v);
            Assert.True(Cfg.ValidateGpio(g).Exists(delegate(string e) { return e.Contains("only offers http"); }), "vMix on another computer with a login");
            v.AllowPlainLogin = true;
            Assert.False(Cfg.ValidateGpio(g).Exists(delegate(string e) { return e.Contains("only offers http"); }), "allowed explicitly");
            v.AllowPlainLogin = false;
            v.Host = "127.0.0.1";
            Assert.False(Cfg.ValidateGpio(g).Exists(delegate(string e) { return e.Contains("only offers http"); }), "vMix on this server");

            ProgramJob p = new ProgramJob();
            p.Name = "P";
            p.ExePath = "/opt/x/tool";
            p.HealthEnabled = true;
            p.HealthUrl = "http://10.0.0.7/health";
            p.HealthUser = "u";
            Assert.True(Cfg.ValidateProgram(p, "p").Exists(delegate(string e) { return e.Contains("over plain http"); }), "health check login over http");
            p.HealthAllowPlainLogin = true;
            Assert.False(Cfg.ValidateProgram(p, "p").Exists(delegate(string e) { return e.Contains("over plain http"); }), "allowed explicitly");
        }

        [Fact]
        public void The_ffmpeg_account_defaults_to_local_service_and_is_kept_in_range()
        {
            AppConfig c = Cfg.Parse("{\"version\": 4, \"general\": {\"ffmpeg_account\": \"administrator\"}}");
            Assert.Eq("local_service", c.General.FfmpegAccount);
            c = Cfg.Parse("{\"version\": 4, \"general\": {\"ffmpeg_account\": \"restricted_system\"}}");
            Assert.Eq("restricted_system", c.General.FfmpegAccount);
            Assert.Eq("local_service", new GeneralCfg().FfmpegAccount);
            Assert.False(Util.MediaAsLocalService, "only on Windows, as Local System");
        }

        ScriptJob FileScript(string watch, string log)
        {
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
            s.WatchSubfolders = true;
            s.ProcessExisting = true;
            s.StableSeconds = 0;
            return s;
        }

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
        public void A_watched_folder_that_is_a_link_is_refused()
        {
            if (Util.IsWindows) throw new SkipException("uses a symbolic link and a shell script");
            string real = D("elsewhere");
            Directory.CreateDirectory(real);
            File.WriteAllText(Path.Combine(real, "secret.txt"), "x");
            string watch = D("watch-link");
            Util.RunCapture("ln", new[] { "-s", real, watch }, 5000);
            string log = D("runs.log");
            ScriptJob s = FileScript(watch, log);
            Set<List<ScriptJob>>("scripts", delegate(List<ScriptJob> l) { l.Clear(); l.Add(s); });
            FileTriggers ft = new FileTriggers(Cfgs.Get, Jobs(), Hub, D("state"));
            for (int i = 0; i < 3; i++) { ft.Tick(); Thread.Sleep(100); }
            Assert.False(File.Exists(log), "the script ran for a file behind the link");
            Assert.True(Hub.Since(0, null, null, 500).Exists(delegate(LogEntry e) { return e.Line().Contains("watched folder refused"); }), "refusal logged");
        }

        [Fact]
        public void A_file_in_a_linked_subfolder_is_not_passed_to_the_script()
        {
            if (Util.IsWindows) throw new SkipException("uses a symbolic link and a shell script");
            string real = D("elsewhere2");
            Directory.CreateDirectory(real);
            File.WriteAllText(Path.Combine(real, "secret.txt"), "x");
            string watch = D("watch");
            Directory.CreateDirectory(watch);
            File.WriteAllText(Path.Combine(watch, "ok.txt"), "x");
            Util.RunCapture("ln", new[] { "-s", real, Path.Combine(watch, "sub") }, 5000);
            string log = D("runs2.log");
            ScriptJob s = FileScript(watch, log);
            Set<List<ScriptJob>>("scripts", delegate(List<ScriptJob> l) { l.Clear(); l.Add(s); });
            FileTriggers ft = new FileTriggers(Cfgs.Get, Jobs(), Hub, D("state"));
            for (int i = 0; i < 3; i++) { ft.Tick(); Thread.Sleep(100); }
            string ran = File.Exists(log) ? File.ReadAllText(log) : "";
            Assert.Contains(ran, "ok.txt");
            Assert.NotContains(ran, "secret.txt");
        }

        [Fact]
        public void A_Local_System_script_with_an_open_start_in_folder_is_not_run()
        {
            PathTrust.Checker = delegate(string p) { return p.Contains("open-dir") ? "'" + p + "' (the folder itself) can be changed by BUILTIN\\Users" : null; };
            ScriptJob s = new ScriptJob();
            s.Name = "Start-in";
            s.Command = "/bin/true";
            s.RunAs = "service";
            s.WorkingDir = D("open-dir");
            RunContext ctx = new RunContext();
            ctx.Log = Hub.Logger("scripts");
            ctx.File = "";
            TaskResult r = ScriptRunner.Run(s, ctx, new SystemLauncher());
            Assert.False(r.Ok);
            Assert.Contains(r.Text, "start-in folder");
            AppConfig c = new AppConfig();
            c.Scripts.Add(s);
            Assert.True(Cfg.UntrustedExecutables(c).Exists(delegate(string e) { return e.Contains("start-in folder"); }), "listed at deploy");
        }
    }
}
