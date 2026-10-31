using System;
using System.Collections.Generic;
using System.IO;
using Watchkeeper;

namespace Watchkeeper.Tests
{
    public class EngineTests : Env
    {
        FakeProcs world = new FakeProcs();
        FakeServices services = new FakeServices();

        Engine Make()
        {
            Engine e = new Engine(D("data"), world, services);
            e.Manual = true;
            e.TickMs = 100000;
            return e;
        }

        void SaveSettings(Action<Settings> change)
        {
            SettingsFile f = new SettingsFile(D("data"), null);
            LoadResult r = f.Load();
            Settings s = r.Settings ?? new Settings();
            change(s);
            f.Save(s, "test");
        }

        [Fact]
        public void Starts_writes_status_and_follows_settings_changes()
        {
            string exe = D("vMix64.exe");
            Write(exe, "x");
            Engine e = Make();
            e.Start();
            try
            {
                e.Step(DateTime.Now);
                EngineStatus s = Engine.ReadStatus(D("data"));
                Assert.NotNull(s);
                Assert.Eq(Engine.Version, s.Version);
                Assert.Eq("new", s.SettingsSource);
                Assert.Eq(0, s.Watchdog.Count);

                System.Threading.Thread.Sleep(20);
                SaveSettings(delegate(Settings x)
                {
                    x.Watchdog.Add(new ProgramEntry { Name = "vMix", Enabled = true, Path = exe, StartDelaySeconds = 0 });
                });
                e.Step(DateTime.Now);
                e.Step(DateTime.Now.AddSeconds(1));
                Assert.Eq(1, world.Starts.Count);
                s = Engine.ReadStatus(D("data"));
                Assert.Eq("vMix", s.Watchdog[0].Name);
                Assert.Contains(string.Join("\n", s.Log.ConvertAll(delegate(LogEntry l) { return l.Line(); }).ToArray()), "Settings reloaded");
            }
            finally { e.Stop(); }
            Assert.Eq(0.0, Engine.ReadStatus(D("data")).Updated);      // marked as stopped
        }

        [Fact]
        public void Commands_from_the_manager_are_carried_out()
        {
            string exe = D("vMix64.exe");
            Write(exe, "x");
            SaveSettings(delegate(Settings x) { x.Watchdog.Add(new ProgramEntry { Id = "vm", Name = "vMix", Enabled = true, Path = exe, StartDelaySeconds = 0 }); });
            Engine e = Make();
            e.Watchdog.InlineRestart = true;
            e.Watchdog.CloseWaitMs = 100;
            e.Start();
            try
            {
                e.Step(DateTime.Now);
                e.Step(DateTime.Now.AddSeconds(1));
                string id = Engine.SendCommand(D("data"), "restart_program", "vm");
                string bad = Engine.SendCommand(D("data"), "run_folder_task", "nope");
                e.Step(DateTime.Now.AddSeconds(2));         // the command is read: restart requested
                e.Step(DateTime.Now.AddSeconds(3));         // closed
                e.Step(DateTime.Now.AddSeconds(4));         // started again
                EngineStatus s = Engine.ReadStatus(D("data"));
                CommandResult r = s.Commands.Find(delegate(CommandResult c) { return c.Id == id; });
                Assert.True(r.Ok);
                Assert.Eq("no such folder task", s.Commands.Find(delegate(CommandResult c) { return c.Id == bad; }).Text);
                Assert.Eq(2, world.Starts.FindAll(delegate(StartSpec x) { return x.File == exe; }).Count);
                Assert.Eq(0, Directory.GetFiles(Path.Combine(D("data"), "commands")).Length);
            }
            finally { e.Stop(); }
        }

        [Fact]
        public void A_broken_settings_file_keeps_the_last_good_settings_and_alerts()
        {
            using (FakeWebhook hook = new FakeWebhook())
            {
                SaveSettings(delegate(Settings x)
                {
                    x.General.ServerName = "good";
                    x.Alerts.EventLog = false;
                    x.Alerts.Destinations.Add(new AlertDestination { Name = "Hook", Type = "webhook", Url = hook.Url });
                });
                File.WriteAllText(D("data", "config.json"), "{ broken");
                Engine e = Make();
                e.Alerts.Inline = true;
                e.Start();
                try
                {
                    Assert.Eq("good", e.Get().General.ServerName);
                    EngineStatus s = e.Status();
                    Assert.Eq("backup", s.SettingsSource);
                    Assert.Contains(s.SettingsError, "not valid JSON");
                    Assert.True(WaitFor(delegate { return hook.Bodies.Count == 1; }, 5));
                    Assert.Contains(hook.Bodies[0], "Settings file cannot be read");
                    Assert.Eq("{ broken", File.ReadAllText(D("data", "config.json")));

                    File.WriteAllText(D("data", "config.json"), "{ broken");          // saved again, still the same problem
                    File.SetLastWriteTimeUtc(D("data", "config.json"), DateTime.UtcNow.AddSeconds(5));
                    for (int i = 0; i < 5; i++) e.Step(DateTime.Now);
                    Assert.Eq(1, e.Alerts.Snapshot().Recent.FindAll(delegate(AlertRecord x) { return x.Key == "service:settings"; }).Count);

                    SaveSettings(delegate(Settings x) { x.General.ServerName = "fixed"; });
                    e.Step(DateTime.Now);
                    Assert.Eq("fixed", e.Get().General.ServerName);
                    Assert.True(WaitFor(delegate { return hook.Bodies.Count == 2; }, 5), "back to normal sent");
                }
                finally { e.Stop(); }
            }
        }

        [Fact]
        public void Plain_passwords_typed_into_the_file_are_encrypted_by_the_service()
        {
            Directory.CreateDirectory(D("data"));
            File.WriteAllText(D("data", "config.json"), "{\"format\":\"watchkeeper-4\",\"alerts\":{\"destinations\":[{\"name\":\"m\",\"type\":\"email\",\"smtp_password\":\"typed\"}]}}");
            Engine e = Make();
            e.Start();
            e.Stop();
            string text = File.ReadAllText(D("data", "config.json"));
            Assert.NotContains(text, "typed");
            Assert.Contains(text, "enc:");
        }

        [Fact]
        public void Validation_problems_are_shown_in_the_status()
        {
            SaveSettings(delegate(Settings x) { x.Folders.Add(new FolderTask { Name = "Move", Enabled = true, Source = "", Action = "move" }); });
            Engine e = Make();
            e.Start();
            try { Assert.Contains(string.Join("|", e.Status().Problems.ToArray()), "Folder task 'Move': the folder is missing"); }
            finally { e.Stop(); }
        }
    }
}
