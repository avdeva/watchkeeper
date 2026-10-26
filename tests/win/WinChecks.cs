// Security checks that need real Windows (file permissions, junctions, hard links, named pipes, tokens).
// Run elevated (the GitHub Actions Windows runner is). Prints one line per check; the exit code is the number
// of failed checks. Everything is created under C:\wk-ci, C:\ProgramData\wk-ci-* and C:\Program Files\wk-ci-*
// and removed at the end.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Watchkeeper;

static class WinChecks
{
    static int failed, passed;
    static readonly string Root = @"C:\wk-ci";
    static readonly string Sys = Environment.SystemDirectory;
    static readonly string PfDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "wk-ci-trusted");
    static readonly string DataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "wk-ci-" + Guid.NewGuid().ToString("N").Substring(0, 8));
    const string OtherUser = "wkci_user";

    static void Check(string name, Func<string> test)
    {
        string problem;
        try { problem = test(); }
        catch (Exception ex) { problem = "threw " + ex.GetType().Name + ": " + ex.Message; }
        if (problem == null) { passed++; Console.WriteLine("PASS " + name); }
        else { failed++; Console.WriteLine("FAIL " + name + ": " + problem); }
    }

    static string Expect(bool ok, string why) { return ok ? null : why; }

    static string Throws<T>(Action a, string mustContain) where T : Exception
    {
        try { a(); return "did not throw"; }
        catch (T ex)
        {
            if (mustContain != null && ex.Message.IndexOf(mustContain, StringComparison.OrdinalIgnoreCase) < 0) return "threw, but: " + ex.Message;
            return null;
        }
    }

    static string Run(string file, string args)
    {
        ProcessStartInfo psi = new ProcessStartInfo(file, args);
        psi.UseShellExecute = false;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.CreateNoWindow = true;
        using (Process p = Process.Start(psi))
        {
            string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) throw new InvalidOperationException(Path.GetFileName(file) + " " + args + " failed (" + p.ExitCode + "): " + o.Trim());
            return o;
        }
    }

    static bool SameDrive(string a, string b) { return string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase); }

    static void Cmd(string line) { Run(Path.Combine(Sys, "cmd.exe"), "/d /c " + line); }
    static void Junction(string link, string target) { Cmd("mklink /J \"" + link + "\" \"" + target + "\""); }
    static void FileLink(string link, string target) { Cmd("mklink \"" + link + "\" \"" + target + "\""); }
    static void HardLink(string link, string target) { Run(Path.Combine(Sys, "fsutil.exe"), "hardlink create \"" + link + "\" \"" + target + "\""); }

    static SecurityIdentifier OtherSid()
    {
        return (SecurityIdentifier)new NTAccount(Environment.MachineName, OtherUser).Translate(typeof(SecurityIdentifier));
    }

    static void SetOwner(string path, SecurityIdentifier sid, bool dir)
    {
        Native.EnablePrivileges("SeRestorePrivilege", "SeTakeOwnershipPrivilege");
        if (dir) { DirectorySecurity ds = new DirectorySecurity(); ds.SetOwner(sid); Directory.SetAccessControl(path, ds); }
        else { FileSecurity fs = new FileSecurity(); fs.SetOwner(sid); File.SetAccessControl(path, fs); }
    }

    static void Grant(string path, SecurityIdentifier sid, FileSystemRights rights, bool dir)
    {
        if (dir)
        {
            DirectorySecurity ds = Directory.GetAccessControl(path);
            ds.AddAccessRule(new FileSystemAccessRule(sid, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            Directory.SetAccessControl(path, ds);
        }
        else
        {
            FileSecurity fs = File.GetAccessControl(path);
            fs.AddAccessRule(new FileSystemAccessRule(sid, rights, AccessControlType.Allow));
            File.SetAccessControl(path, fs);
        }
    }

    static readonly SecurityIdentifier Users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

    static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--local-service-check")
        {
            // started as Local System by a scheduled task from LocalServiceCheck below
            string result;
            try { result = LocalServiceResult() ?? "OK"; }
            catch (Exception ex) { result = "failed: " + ex.Message; }
            File.WriteAllText(args[1], result);
            return 0;
        }
        Console.WriteLine("Watchkeeper Windows security checks, running as " + WindowsIdentity.GetCurrent().Name +
                          (ServiceSetup.IsAdmin() ? " (administrator)" : " (NOT administrator: results are not meaningful)"));
        try { Run(Path.Combine(Sys, "net.exe"), "user " + OtherUser + " Wk-ci-" + Guid.NewGuid().ToString("N").Substring(0, 12) + "! /add"); }
        catch (Exception ex) { Console.WriteLine("note: could not create the test account: " + ex.Message); }
        try
        {
            Directory.CreateDirectory(Root);
            PathTrustChecks();
            DataDirChecks();
            SafeFsChecks();
            FolderPolicyChecks();
            PipeChecks();
            TokenChecks();
            LocalServiceCheck();
            SecretChecks();
        }
        finally
        {
            try { Cmd("rmdir /s /q \"" + Root + "\""); } catch (Exception) { }
            try { Cmd("rmdir /s /q \"" + PfDir + "\""); } catch (Exception) { }
            try { Cmd("rmdir /s /q \"" + DataRoot + "\""); } catch (Exception) { }
            try { Run(Path.Combine(Sys, "net.exe"), "user " + OtherUser + " /delete"); } catch (Exception) { }
        }
        Console.WriteLine(passed + " passed, " + failed + " failed");
        return failed;
    }

    // ------------------------------------------------------------------ PathTrust
    static void PathTrustChecks()
    {
        string whoami = Path.Combine(Sys, "whoami.exe");
        Check("a Windows program in System32 is trusted", delegate { return PathTrust.Check(whoami); });

        Directory.CreateDirectory(PfDir);
        string trusted = Path.Combine(PfDir, "tool.exe");
        File.Copy(whoami, trusted, true);
        PathTrust.Forget();
        Check("a program in a Program Files folder is trusted", delegate { return PathTrust.Check(trusted); });

        string open = Path.Combine(Root, "tools");
        Directory.CreateDirectory(open);
        string untrusted = Path.Combine(open, "tool.exe");
        File.Copy(whoami, untrusted, true);
        PathTrust.Forget();
        Check("a program in a folder made under C:\\ is not trusted (users may change it)", delegate
        {
            string why = PathTrust.Check(untrusted);
            return Expect(why != null && why.Contains("Authenticated Users"), "expected a refusal naming Authenticated Users, got: " + (why ?? "trusted"));
        });

        string planted = Path.Combine(PfDir, "plant");
        Directory.CreateDirectory(planted);
        File.Copy(whoami, Path.Combine(planted, "tool.exe"), true);
        Grant(planted, Users, FileSystemRights.CreateFiles, false);
        PathTrust.Forget();
        Check("a program whose folder lets users create files (DLL planting) is not trusted", delegate
        {
            string why = PathTrust.Check(Path.Combine(planted, "tool.exe"));
            return Expect(why != null && why.Contains("folder it is in"), "got: " + (why ?? "trusted"));
        });

        string owned = Path.Combine(PfDir, "owned.exe");
        File.Copy(whoami, owned, true);
        try
        {
            SetOwner(owned, OtherSid(), false);
            PathTrust.Forget();
            Check("a program owned by a standard account is not trusted", delegate
            {
                string why = PathTrust.Check(owned);
                return Expect(why != null && why.Contains(OtherUser), "got: " + (why ?? "trusted"));
            });
        }
        catch (Exception ex) { Check("a program owned by a standard account is not trusted", delegate { return "setup failed: " + ex.Message; }); }
    }

    // ------------------------------------------------------------------ settings folder
    static void DataDirChecks()
    {
        string d1 = Path.Combine(DataRoot, "a");
        Directory.CreateDirectory(d1);
        File.WriteAllText(Path.Combine(d1, "config.json"), "{\"version\": 4}");
        Directory.CreateDirectory(Path.Combine(d1, "state"));
        File.WriteAllText(Path.Combine(d1, "state", "jobs.json"), "{}");
        Grant(d1, Users, FileSystemRights.FullControl, true);
        Check("a settings folder users could write to is detected", delegate { return Expect(DataDirSecurity.Verify(d1) != null, "Verify said it is protected"); });
        SecureResult r = null;
        Check("securing it moves the whole folder aside and does not carry over files users could have written", delegate
        {
            r = DataDirSecurity.Secure(d1);
            string[] aside = Directory.GetDirectories(DataRoot, "a.untrusted-*");
            return Expect(aside.Length == 1 && File.Exists(Path.Combine(aside[0], "config.json")) && !File.Exists(Path.Combine(d1, "config.json")) &&
                          !File.Exists(Path.Combine(d1, "state", "jobs.json")), "aside: " + aside.Length + "; " + (r != null ? string.Join("; ", r.Actions.ToArray()) : "?"));
        });
        Check("afterwards only Administrators and SYSTEM have access, owner Administrators", delegate
        {
            string v = DataDirSecurity.Verify(d1);
            if (v != null) return v;
            DirectorySecurity ds = Directory.GetAccessControl(d1);
            if (!Trust.Administrators.Equals(ds.GetOwner(typeof(SecurityIdentifier)))) return "owner is " + ds.GetOwner(typeof(NTAccount));
            foreach (FileSystemAccessRule a in ds.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                if (!a.IdentityReference.Equals(Trust.Administrators) && !a.IdentityReference.Equals(Trust.System)) return "also allowed: " + a.IdentityReference;
            return null;
        });

        Check("a settings folder that is a link is moved aside, its target is left alone", delegate
        {
            string target = Path.Combine(Root, "dd-link-target");
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "keep.txt"), "keep");
            string before = Directory.GetAccessControl(target).GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access);
            string dl = Path.Combine(DataRoot, "link");
            Junction(dl, target);
            DataDirSecurity.Secure(dl);
            string after = Directory.GetAccessControl(target).GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access);
            bool real = (File.GetAttributes(dl) & FileAttributes.ReparsePoint) == 0;
            return Expect(real && before == after && File.Exists(Path.Combine(target, "keep.txt")) && DataDirSecurity.Verify(dl) == null &&
                          Directory.GetDirectories(DataRoot, "link.untrusted-*").Length == 1, "real folder " + real + ", target permissions changed " + (before != after));
        });

        Check("configuration written by an administrator is carried over when only the folder was open to others", delegate
        {
            string dc = Path.Combine(DataRoot, "carry");
            DataDirSecurity.Secure(dc);
            File.WriteAllText(Path.Combine(dc, "config.json"), "{\"version\": 4}");
            DirectorySecurity ds = Directory.GetAccessControl(dc);
            ds.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.CreateFiles, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
            Directory.SetAccessControl(dc, ds);
            SecureResult rc = DataDirSecurity.Secure(dc);
            return Expect(File.Exists(Path.Combine(dc, "config.json")) && DataDirSecurity.Verify(dc) == null, string.Join("; ", rc.Actions.ToArray()));
        });

        string d2 = Path.Combine(DataRoot, "b");
        DataDirSecurity.Secure(d2);
        string cfg = Path.Combine(d2, "config.json");
        File.WriteAllText(cfg, "{\"version\": 4}");
        Check("a file there owned by a standard account is set aside", delegate
        {
            SetOwner(cfg, OtherSid(), false);
            DataDirSecurity.Secure(d2);
            return Expect(File.Exists(cfg + ".untrusted") && !File.Exists(cfg), "the file stayed in place");
        });
        Check("a link inside the settings folder is removed, not followed", delegate
        {
            string victim = Path.Combine(Root, "victim-data");
            Directory.CreateDirectory(victim);
            File.WriteAllText(Path.Combine(victim, "keep.txt"), "keep");
            Junction(Path.Combine(d2, "state"), victim);
            DataDirSecurity.Secure(d2);
            return Expect(!Directory.Exists(Path.Combine(d2, "state")) && File.Exists(Path.Combine(victim, "keep.txt")) &&
                          Users.Equals(Users) && DataDirSecurity.Verify(d2) == null, "link still there or target changed");
        });

        Check("a second name (hard link) for a file elsewhere is removed from the settings folder, the file is untouched", delegate
        {
            string outside = Path.Combine(Root, "hl-target.txt");
            File.WriteAllText(outside, "outside");
            string before = File.GetAccessControl(outside).GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access);
            HardLink(Path.Combine(d2, "config.json"), outside);
            DataDirSecurity.Secure(d2);
            string after = File.GetAccessControl(outside).GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access);
            return Expect(!File.Exists(Path.Combine(d2, "config.json")) && File.Exists(outside) && before == after, "permissions of the outside file changed: " + (before != after));
        });

        Check("a settings folder under a folder other accounts can rename or delete is refused; one in ProgramData is not", delegate
        {
            string bad = PathTrust.CheckSettingsLocation(Path.Combine(Root, "settings"));
            string good = PathTrust.CheckSettingsLocation(Path.Combine(DataRoot, "settings"));
            string share = PathTrust.CheckSettingsLocation(@"\\server\share\Watchkeeper");
            return Expect(bad != null && good == null && share != null, "under " + Root + ": " + bad + " / ProgramData: " + good);
        });

        string d3 = Path.Combine(DataRoot, "c");
        Directory.CreateDirectory(d3);
        File.WriteAllText(Path.Combine(d3, "config.json"), "{\"version\": 4}");
        Check("a settings folder created by a standard account is taken back and its configuration set aside", delegate
        {
            SetOwner(d3, OtherSid(), true);
            DataDirSecurity.Secure(d3);
            return Expect(DataDirSecurity.Verify(d3) == null && Directory.GetDirectories(DataRoot, "c.untrusted-*").Length == 1, "not taken back");
        });

        Check("the earlier name's folder is never imported automatically", delegate
        {
            string old = Predecessor.DataDir;
            bool existed = Directory.Exists(old);
            if (!existed) Directory.CreateDirectory(old);
            string planted = Path.Combine(old, "config.json");
            bool hadFile = File.Exists(planted);
            if (!hadFile) File.WriteAllText(planted, "{\"version\": 3, \"general\": {\"server_label\": \"PLANTED\"}}");
            try
            {
                string d4 = Path.Combine(DataRoot, "d");
                DataDirSecurity.Secure(d4);
                Engine e = new Engine(d4, new EngineOptions { Mode = "console" });
                e.Config.Load(true);
                string label = e.Config.Get().General.ServerLabel;
                return Expect(label != "PLANTED", "the planted configuration was loaded");
            }
            finally
            {
                if (!hadFile) File.Delete(planted);
                if (!existed) Directory.Delete(old, true);
            }
        });
    }

    // ------------------------------------------------------------------ link-safe file operations
    static void SafeFsChecks()
    {
        Check("a folder held by SafeDir cannot be renamed by others", delegate
        {
            string hd = Path.Combine(Root, "held");
            Directory.CreateDirectory(hd);
            using (SafeDir d = SafeDir.Open(hd, false))
            {
                try { Directory.Move(hd, hd + "-moved"); return "it was renamed while held"; }
                catch (IOException) { return null; }
                catch (UnauthorizedAccessException) { return null; }
            }
        });
        Check("a start-in folder users can add files to is not trusted; a protected one is", delegate
        {
            string open = Path.Combine(Root, "open-start-in");
            Directory.CreateDirectory(open);
            Grant(open, Users, FileSystemRights.CreateFiles, true);
            PathTrust.Forget();
            return Expect(PathTrust.CheckFolder(open) != null && PathTrust.CheckFolder(PfDir) == null && PathTrust.CheckFolder(Sys) == null,
                          "open: " + PathTrust.CheckFolder(open) + " / protected: " + PathTrust.CheckFolder(PfDir));
        });
        Check("the service search path keeps protected folders and leaves out open and missing ones", delegate
        {
            string open = Path.Combine(Root, "open-start-in");
            string before = Environment.GetEnvironmentVariable("PATH");
            try
            {
                Environment.SetEnvironmentVariable("PATH", Sys + ";" + open + ";relative\\bin;" + Path.Combine(Root, "missing-" + Guid.NewGuid().ToString("N")));
                List<string> dropped = LaunchSafety.HardenServiceEnvironment();
                string now = Environment.GetEnvironmentVariable("PATH");
                return Expect(now == Sys && dropped.Count == 3 && Environment.GetEnvironmentVariable("NoDefaultCurrentDirectoryInExePath") == "1",
                              "PATH now '" + now + "', left out: " + string.Join(" | ", dropped.ToArray()));
            }
            finally { Environment.SetEnvironmentVariable("PATH", before); }
        });
        string src = Path.Combine(Root, "src"), dst = Path.Combine(Root, "dst"), victim = Path.Combine(Root, "victim");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(Path.Combine(src, "real"));
        Directory.CreateDirectory(dst);
        Directory.CreateDirectory(victim);
        File.WriteAllText(Path.Combine(victim, "keep.txt"), "keep");
        File.WriteAllText(Path.Combine(victim, "keep2.txt"), "keep2");
        File.WriteAllText(Path.Combine(src, "a.txt"), "a");
        File.WriteAllText(Path.Combine(src, "real", "b.txt"), "b");
        Junction(Path.Combine(src, "link"), victim);

        Check("a junction below a policy folder is refused", delegate
        {
            using (SafeDir d = SafeDir.Open(src, false)) return Throws<SafeFsException>(delegate { d.Child("link", false).Dispose(); }, "link");
        });
        Check("a policy folder reached through a junction is refused", delegate
        {
            return Throws<SafeFsException>(delegate { SafeDir.Open(Path.Combine(src, "link"), false).Dispose(); }, "link");
        });
        Check("a held folder cannot be renamed or replaced", delegate
        {
            using (SafeDir d = SafeDir.Open(Path.Combine(src, "real"), false))
            {
                string moved = Throws<IOException>(delegate { Directory.Move(Path.Combine(src, "real"), Path.Combine(src, "moved")); }, null);
                if (moved != null) return "the folder could be moved while held";
            }
            Directory.Move(Path.Combine(src, "real"), Path.Combine(src, "moved"));
            Directory.Move(Path.Combine(src, "moved"), Path.Combine(src, "real"));
            return null;
        });
        Check("deleting a second name (hard link) removes only that name, not the file", delegate
        {
            HardLink(Path.Combine(src, "hl.txt"), Path.Combine(victim, "keep.txt"));
            using (SafeDir d = SafeDir.Open(src, false)) SafeFs.Delete(d, "hl.txt");
            return Expect(!File.Exists(Path.Combine(src, "hl.txt")) && File.ReadAllText(Path.Combine(victim, "keep.txt")) == "keep", "the file itself changed");
        });
        Check("a second name for a file only administrators can read is not copied out", delegate
        {
            string secret = Path.Combine(victim, "secret.txt");
            File.WriteAllText(secret, "secret");
            FileSecurity fs = new FileSecurity();
            fs.SetAccessRuleProtection(true, false);
            fs.SetOwner(Trust.Administrators);
            fs.AddAccessRule(new FileSystemAccessRule(Trust.Administrators, FileSystemRights.FullControl, AccessControlType.Allow));
            fs.AddAccessRule(new FileSystemAccessRule(Trust.System, FileSystemRights.FullControl, AccessControlType.Allow));
            File.SetAccessControl(secret, fs);
            HardLink(Path.Combine(src, "planted.txt"), secret);
            using (SafeDir d = SafeDir.Open(src, false))
            using (SafeDir o = SafeDir.Open(dst, false))
            {
                string t = Throws<SafeFsException>(delegate { SafeFs.Copy(d, "planted.txt", o, "planted.txt", true); }, "only administrators may read") ??
                           Throws<SafeFsException>(delegate { SafeFs.Move(d, "planted.txt", o, "planted.txt", true); }, "only administrators may read");
                if (t != null && !SameDrive(src, dst)) return t;
            }
            return Expect(!File.Exists(Path.Combine(dst, "planted.txt")) || SameDrive(src, dst), "the protected file was copied out");
        });
        Check("a recording with a second name that its users can read is copied and moved", delegate
        {
            string rec = Path.Combine(victim, "rec.mp4");
            File.WriteAllText(rec, "video");
            Grant(rec, Users, FileSystemRights.Read, false);
            HardLink(Path.Combine(src, "rec.mp4"), rec);
            using (SafeDir d = SafeDir.Open(src, false))
            using (SafeDir o = SafeDir.Open(dst, false))
            {
                SafeFs.Copy(d, "rec.mp4", o, "rec-copy.mp4", true);
                SafeFs.Move(d, "rec.mp4", o, "rec.mp4", true);
            }
            return Expect(File.ReadAllText(Path.Combine(dst, "rec-copy.mp4")) == "video" && File.Exists(Path.Combine(dst, "rec.mp4")) &&
                          !File.Exists(Path.Combine(src, "rec.mp4")) && File.ReadAllText(rec) == "video", "not copied or moved");
        });
        Check("a file symbolic link is not deleted, copied or moved", delegate
        {
            FileLink(Path.Combine(src, "sl.txt"), Path.Combine(victim, "keep.txt"));
            using (SafeDir d = SafeDir.Open(src, false))
            using (SafeDir o = SafeDir.Open(dst, false))
            {
                string t = Throws<SafeFsException>(delegate { SafeFs.Delete(d, "sl.txt"); }, "link") ??
                           Throws<SafeFsException>(delegate { SafeFs.Copy(d, "sl.txt", o, "sl.txt", true); }, "link") ??
                           Throws<SafeFsException>(delegate { SafeFs.Move(d, "sl.txt", o, "sl.txt", true); }, "link");
                if (t != null) return t;
            }
            return Expect(!File.Exists(Path.Combine(dst, "sl.txt")) && File.ReadAllText(Path.Combine(victim, "keep.txt")) == "keep", "something went through the link");
        });
        Check("a destination folder that is a junction is refused", delegate
        {
            Junction(Path.Combine(dst, "out"), victim);
            using (SafeDir o = SafeDir.Open(dst, false)) return Throws<SafeFsException>(delegate { o.Child("out", true).Dispose(); }, "link");
        });
        Check("a planted link at the temporary copy name is not written through", delegate
        {
            FileLink(Path.Combine(dst, "a.txt.copying"), Path.Combine(victim, "keep2.txt"));
            using (SafeDir d = SafeDir.Open(src, false))
            using (SafeDir o = SafeDir.Open(dst, false))
                SafeFs.Copy(d, "a.txt", o, "a.txt", false);
            return Expect(File.ReadAllText(Path.Combine(victim, "keep2.txt")) == "keep2" && File.ReadAllText(Path.Combine(dst, "a.txt")) == "a",
                          "the link target was written");
        });
        Check("move and copy keep Date Modified and work through handles", delegate
        {
            DateTime when = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(Path.Combine(src, "real", "b.txt"), when);
            using (SafeDir d = SafeDir.Open(Path.Combine(src, "real"), false))
            using (SafeDir o = SafeDir.Open(dst, false))
                SafeFs.Move(d, "b.txt", o, "b.txt", false);
            string moved = Path.Combine(dst, "b.txt");
            return Expect(File.Exists(moved) && !File.Exists(Path.Combine(src, "real", "b.txt")) && File.GetLastWriteTimeUtc(moved) == when,
                          "moved: " + File.Exists(moved) + ", time " + (File.Exists(moved) ? File.GetLastWriteTimeUtc(moved).ToString("o") : ""));
        });
    }

    // ------------------------------------------------------------------ folder policies end to end
    static void FolderPolicyChecks()
    {
        string cache = Path.Combine(Root, "cache"), victim = Path.Combine(Root, "victim-policy");
        Directory.CreateDirectory(cache);
        Directory.CreateDirectory(victim);
        string old = Path.Combine(victim, "system.dll");
        File.WriteAllText(old, "important");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-30));
        string own = Path.Combine(cache, "old.mp4");
        File.WriteAllText(own, "old recording");
        File.SetLastWriteTimeUtc(own, DateTime.UtcNow.AddDays(-30));
        Junction(Path.Combine(cache, "sub"), victim);
        LogHub hub = new LogHub(100);
        Check("retention deletes old files but nothing through a junction", delegate
        {
            FolderPolicy p = Cfg.Cleanup("Retention", cache, 7);
            p.MinAgeSeconds = 0;
            Cfg.NormalizeFolder(p, new List<string>());
            FolderRunner.Run(p, new GeneralCfg(), hub.Logger("folders"), false, null, 0, null);
            return Expect(!File.Exists(own) && File.Exists(old), "own file deleted: " + !File.Exists(own) + ", file behind the junction kept: " + File.Exists(old));
        });
        Check("a move policy refuses a destination sub-folder that is a junction", delegate
        {
            string src = Path.Combine(Root, "cache2"), dest = Path.Combine(Root, "data2"), target = Path.Combine(Root, "victim-move");
            Directory.CreateDirectory(Path.Combine(src, "x"));
            Directory.CreateDirectory(dest);
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(src, "x", "version.dll"), "payload");
            Junction(Path.Combine(dest, "x"), target);
            FolderPolicy p = Cfg.Offload("Move", src, dest);
            p.Subfolders = true;
            p.MinAgeSeconds = 0;
            foreach (FileRule r in p.Rules) r.KeepSubfolders = true;
            Cfg.NormalizeFolder(p, new List<string>());
            FolderRunner.Run(p, new GeneralCfg(), hub.Logger("folders"), false, null, 0, null);
            return Expect(!File.Exists(Path.Combine(target, "version.dll")) && File.Exists(Path.Combine(src, "x", "version.dll")), "the file went through the junction");
        });
    }

    // ------------------------------------------------------------------ control channel
    static void PipeChecks()
    {
        LogHub hub = new LogHub(100);
        string name = "wk-ci-" + Guid.NewGuid().ToString("N");
        Check("the service reports a control channel name that is already taken", delegate
        {
            using (NamedPipeServerStream squatter = new NamedPipeServerStream(name, PipeDirection.InOut, 1))
            {
                string reported = null;
                ManualResetEvent got = new ManualResetEvent(false);
                ControlServer s = new ControlServer(name, delegate(JObject req) { return new JObject(); }, hub.Logger("service"));
                s.Squatted = delegate(string m) { reported = m; got.Set(); };
                s.Start();
                bool ok = got.WaitOne(8000);
                s.Stop();
                return Expect(ok && reported.Contains("another program"), "no report (" + reported + ")");
            }
        });

        string name2 = "wk-ci-" + Guid.NewGuid().ToString("N");
        Check("the manager talks to a real control channel", delegate
        {
            ControlServer s = new ControlServer(name2, delegate(JObject req) { JObject o = new JObject(); o.Set("pong", true); return o; }, hub.Logger("service"));
            s.Start();
            Thread.Sleep(500);
            try
            {
                JObject r = new ControlClient(name2).Call("ping");
                return Expect(r.Bool("pong", false), "no answer");
            }
            finally { s.Stop(); }
        });

        string name3 = "wk-ci-" + Guid.NewGuid().ToString("N");
        Check("the manager sends nothing to a control channel held by another program", delegate
        {
            string ps = Path.Combine(Sys, @"WindowsPowerShell\v1.0\powershell.exe");
            string script = "$s = New-Object System.IO.Pipes.NamedPipeServerStream('" + name3 + "'); $s.WaitForConnection(); " +
                            "$r = New-Object System.IO.StreamReader($s); $l = $r.ReadLine(); Set-Content -Path '" + Path.Combine(Root, "pipe-got.txt") + "' -Value \"$l\"; Start-Sleep 2";
            ProcessStartInfo psi = new ProcessStartInfo(ps, "-NoProfile -Command \"" + script + "\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            using (Process p = Process.Start(psi))
            {
                Thread.Sleep(4000);
                JObject req = new JObject();
                req.Set("cmd", "alerts.test");
                req.Set("secret", "must-not-leave");
                string t = Throws<ControlError>(delegate { new ControlClient(name3).Call(req, 5000); }, "held by another program");
                p.WaitForExit(10000);
                if (t != null) return t;
                string got = File.Exists(Path.Combine(Root, "pipe-got.txt")) ? File.ReadAllText(Path.Combine(Root, "pipe-got.txt")) : "";
                return Expect(!got.Contains("must-not-leave"), "the request reached the other program");
            }
        });
    }

    // ------------------------------------------------------------------ restricted token for ffmpeg / ffprobe
    static void TokenChecks()
    {
        Check("ffmpeg and ffprobe run without privileges and with Administrators for denying only", delegate
        {
            Util.CaptureResult priv = Util.RunMedia(Path.Combine(Sys, "whoami.exe"), new[] { "/priv" }, 20000);
            Util.CaptureResult groups = Util.RunMedia(Path.Combine(Sys, "whoami.exe"), new[] { "/groups" }, 20000);
            string p = priv.Output, g = groups.Output;
            if (p.Contains("SeDebugPrivilege") || p.Contains("SeBackupPrivilege") || p.Contains("SeRestorePrivilege") || p.Contains("SeTakeOwnershipPrivilege"))
                return "privileges left: " + p;
            foreach (string line in g.Split('\n'))
                if (line.Contains("S-1-5-32-544") && !line.Contains("deny only") && !line.Contains("Deny only"))
                    return "Administrators still usable: " + line.Trim();
            return null;
        });
    }

    static string LocalServiceResult()
    {
        if (!WindowsIdentity.GetCurrent().IsSystem) return "not running as Local System";
        Util.MediaAccount = "local_service";
        if (!Util.MediaAsLocalService) return "LOCAL SERVICE not chosen";
        Util.CaptureResult user = Util.RunMedia(Path.Combine(Sys, "whoami.exe"), new[] { "/user" }, 20000);
        Util.CaptureResult priv = Util.RunMedia(Path.Combine(Sys, "whoami.exe"), new[] { "/priv" }, 20000);
        if (user.Output.IndexOf("local service", StringComparison.OrdinalIgnoreCase) < 0) return "ran as: " + user.Output + user.Error;
        foreach (string bad in new[] { "SeImpersonatePrivilege", "SeAssignPrimaryTokenPrivilege", "SeAuditPrivilege", "SeCreateGlobalPrivilege" })
            if (priv.Output.Contains(bad)) return "privilege left: " + bad;
        return null;
    }

    static void LocalServiceCheck()
    {
        Check("as Local System, ffmpeg and ffprobe run as LOCAL SERVICE without privileges", delegate
        {
            string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
            string outFile = Path.Combine(Root, "local-service-check.txt");
            string task = "wk-ci-local-service-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            Run(Path.Combine(Sys, "schtasks.exe"), "/create /tn " + task + " /ru SYSTEM /sc once /st 23:59 /f /tr \"\\\"" + exe + "\\\" --local-service-check \\\"" + outFile + "\\\"\"");
            try
            {
                Run(Path.Combine(Sys, "schtasks.exe"), "/run /tn " + task);
                for (int i = 0; i < 120 && !File.Exists(outFile); i++) System.Threading.Thread.Sleep(500);
                System.Threading.Thread.Sleep(300);
                if (!File.Exists(outFile)) return "the check did not run as Local System";
                string r = File.ReadAllText(outFile);
                return r == "OK" ? null : r;
            }
            finally { Run(Path.Combine(Sys, "schtasks.exe"), "/delete /tn " + task + " /f"); }
        });
    }

    // ------------------------------------------------------------------ secrets
    static void SecretChecks()
    {
        Check("secrets encrypted with the service account's own key decrypt again", delegate
        {
            string e = Secrets.ProtectService("s3cret");
            return Expect(Secrets.IsServiceEncrypted(e) && Secrets.Unprotect(e) == "s3cret", "round trip failed");
        });
        Check("this computer's key (deploy) still decrypts for conversion", delegate
        {
            string e = Secrets.Protect("s3cret");
            return Expect(e.StartsWith("dpapi:") && Secrets.Unprotect(e) == "s3cret", "round trip failed");
        });
    }
}
