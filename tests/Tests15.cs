// The configuration file keeps earlier versions: before every save (and when it is changed outside Watchkeeper)
// the settings as they were are written to backups\ and config.json.bak; a save that cannot keep them is refused.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Watchkeeper;

namespace Watchkeeper.Tests
{
    public class ConfigVersionTests : Env
    {
        string Label() { return Cfgs.Get().General.ServerLabel; }

        void SetLabel(string label)
        {
            Set<GeneralCfg>("general", delegate(GeneralCfg g) { g.ServerLabel = label; });
        }

        static string LabelIn(string file) { return Cfg.Parse(File.ReadAllText(file)).General.ServerLabel; }

        [Fact]
        public void Every_save_keeps_the_settings_as_they_were()
        {
            SetLabel("one");
            Thread.Sleep(5);
            SetLabel("two");
            Thread.Sleep(5);
            SetLabel("three");
            List<ConfigStore.Version> v = Cfgs.Versions();
            Assert.Eq(4, v.Count);                                       // the starter file, the test set-up, "one", "two"
            Assert.Eq("two", LabelIn(v[0].File));                         // newest first
            Assert.Eq("one", LabelIn(v[1].File));
            Assert.Eq("before-general", v[0].Why);
            Assert.Eq("two", LabelIn(Cfgs.Path + ".bak"));
            Assert.Eq("three", LabelIn(Cfgs.Path));
        }

        [Fact]
        public void The_same_settings_are_not_kept_twice_and_old_versions_are_pruned()
        {
            Cfgs.KeepVersions = 3;
            for (int i = 0; i < 6; i++) { SetLabel("v" + i); Thread.Sleep(5); }
            Assert.Eq(3, Cfgs.Versions().Count);
            Assert.Eq("v4", LabelIn(Cfgs.Versions()[0].File));
            int before = Cfgs.Versions().Count;
            SetLabel("v5");                                               // nothing changed: the kept version would be the same
            Assert.Eq(before, Cfgs.Versions().Count);
            Assert.Eq("v4", LabelIn(Cfgs.Versions()[0].File));
        }

        [Fact]
        public void A_save_that_cannot_keep_the_previous_settings_is_refused()
        {
            SetLabel("keep me");
            Directory.Delete(Cfgs.BackupDir, true);
            File.WriteAllText(Cfgs.BackupDir, "not a folder");               // the backups folder cannot be created
            GeneralCfg g = Cfgs.Get().General;
            g.ServerLabel = "lost";
            List<string> errors = Cfgs.SaveSection("general", g);
            Assert.Eq(1, errors.Count);
            Assert.Contains(errors[0], "could not be kept as a backup");
            Assert.Contains(errors[0], "nothing was saved");
            Assert.Eq("keep me", LabelIn(Cfgs.Path));
            Assert.Eq("keep me", Label());
        }

        [Fact]
        public void A_change_made_outside_Watchkeeper_keeps_the_settings_that_were_in_force()
        {
            SetLabel("before notepad");
            AppConfig edited = Cfgs.Get();
            edited.General.ServerLabel = "typed in notepad";
            File.WriteAllText(Cfgs.Path, Cfg.ToText(edited) + " ");
            File.SetLastWriteTimeUtc(Cfgs.Path, DateTime.UtcNow.AddSeconds(5));
            Cfgs.CheckForChanges();
            Assert.Eq("typed in notepad", Label());
            ConfigStore.Version v = Cfgs.Versions()[0];
            Assert.Eq("before-edit", v.Why);
            Assert.Eq("before notepad", LabelIn(v.File));
        }

        [Fact]
        public void A_restore_keeps_the_current_settings_first()
        {
            SetLabel("old");
            Thread.Sleep(5);
            SetLabel("new");
            ConfigStore.Version old = Cfgs.Versions()[0];
            Assert.Eq("old", LabelIn(old.File));
            List<string> errors = Cfgs.SaveAll(Cfg.Parse(File.ReadAllText(old.File)), "restore");
            Assert.Eq(0, errors.Count);
            Assert.Eq("old", Label());
            Assert.Eq("before-restore", Cfgs.Versions()[0].Why);
            Assert.Eq("new", LabelIn(Cfgs.Versions()[0].File));
        }
    }
}
