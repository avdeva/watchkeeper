// Security checks on Windows file permissions:
//   - which accounts count as trusted (Local System, Administrators, TrustedInstaller, and accounts that are
//     members of the local Administrators group);
//   - PathTrust: whether a program or script could be changed by an account that is not trusted, before the
//     service runs it as Local System;
//   - DataDirSecurity: the settings folder is owned by Administrators and only Administrators and Local System
//     have access; files planted there by other accounts are set aside and never loaded.
// On other systems (development and tests) the checks are skipped unless a test installs its own checker.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Watchkeeper
{
    public static class Trust
    {
        public static readonly SecurityIdentifier System = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        public static readonly SecurityIdentifier Administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        public static readonly SecurityIdentifier TrustedInstaller = new SecurityIdentifier("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");
        static readonly SecurityIdentifier CreatorOwner = new SecurityIdentifier(WellKnownSidType.CreatorOwnerSid, null);
        static readonly SecurityIdentifier OwnerRights = new SecurityIdentifier("S-1-3-4");

        static readonly SecurityIdentifier[] Broad =
        {
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinGuestsSid, null),
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            new SecurityIdentifier(WellKnownSidType.AnonymousSid, null),
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            new SecurityIdentifier(WellKnownSidType.BatchSid, null),
            new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null),
            new SecurityIdentifier(WellKnownSidType.NetworkServiceSid, null),
            new SecurityIdentifier("S-1-5-32-547"),   // Power Users
            new SecurityIdentifier("S-1-5-32-555"),   // Remote Desktop Users
        };

        static readonly object lk = new object();
        static HashSet<string> adminMembers;
        static DateTime adminMembersAt;
        static readonly Dictionary<string, int> userCache = new Dictionary<string, int>();

        /// <summary>The account is SYSTEM, Administrators or TrustedInstaller (fixed, known accounts).</summary>
        public static bool IsBuiltInTrusted(SecurityIdentifier sid)
        {
            return sid != null && (sid.Equals(System) || sid.Equals(Administrators) || sid.Equals(TrustedInstaller));
        }

        /// <summary>1 = an administrator (or trusted system account), 0 = definitely not, -1 = cannot tell.</summary>
        public static int AdminState(SecurityIdentifier sid)
        {
            if (sid == null) return 0;
            if (IsBuiltInTrusted(sid)) return 1;
            foreach (SecurityIdentifier b in Broad) if (sid.Equals(b)) return 0;
            if (!Util.IsWindows) return -1;
            if (IsCurrentAdmin(sid)) return 1;
            HashSet<string> members = AdminMembers();
            if (members != null && members.Contains(sid.Value)) return 1;
            lock (lk)
            {
                int cached;
                if (userCache.TryGetValue(sid.Value, out cached)) return cached;
            }
            int state = Native.SidIsMemberOfAdministrators(sid);
            lock (lk) userCache[sid.Value] = state;
            return state;
        }

        static string currentAdminSid;
        static bool currentChecked;

        /// <summary>The account running this program, when it runs with administrator rights (for example a domain account
        /// that is an administrator through a group, while the domain controller cannot be asked).</summary>
        static bool IsCurrentAdmin(SecurityIdentifier sid)
        {
            lock (lk)
            {
                if (!currentChecked)
                {
                    currentChecked = true;
                    try
                    {
                        using (WindowsIdentity me = WindowsIdentity.GetCurrent())
                            if (me.User != null && new WindowsPrincipal(me).IsInRole(WindowsBuiltInRole.Administrator)) currentAdminSid = me.User.Value;
                    }
                    catch (Exception) { }
                }
                return currentAdminSid != null && currentAdminSid == sid.Value;
            }
        }

        /// <summary>Ignore these in permission lists: they only apply to the owner (checked on its own).</summary>
        public static bool IsOwnerPlaceholder(SecurityIdentifier sid)
        {
            return sid != null && (sid.Equals(CreatorOwner) || sid.Equals(OwnerRights));
        }

        static HashSet<string> AdminMembers()
        {
            lock (lk)
            {
                if (adminMembers != null && (DateTime.UtcNow - adminMembersAt).TotalMinutes < 10) return adminMembers;
            }
            HashSet<string> set = Native.LocalGroupMemberSids(Administrators);
            lock (lk)
            {
                adminMembers = set;
                adminMembersAt = DateTime.UtcNow;
                userCache.Clear();
            }
            return set;
        }

        public static string Name(SecurityIdentifier sid)
        {
            try { return ((NTAccount)sid.Translate(typeof(NTAccount))).Value; }
            catch (Exception) { return sid.Value; }
        }

        // ---- rights that let an account change or replace what is protected
        const int GenericAll = 0x10000000, GenericWrite = 0x40000000;
        public const FileSystemRights ChangeFile = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete |
                                                   FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        /// <summary>The folder a program is in: creating files there plants DLLs next to the program.</summary>
        public const FileSystemRights ChangeParent = FileSystemRights.CreateFiles | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete |
                                                     FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        /// <summary>Folders further up: renaming or replacing a folder on the way changes what the path points at.</summary>
        public const FileSystemRights ChangeAncestor = FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete |
                                                       FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

        public static bool Grants(FileSystemAccessRule r, FileSystemRights mask)
        {
            int v = (int)r.FileSystemRights;
            return (v & (int)mask) != 0 || (v & GenericAll) != 0 || (v & GenericWrite) != 0;
        }

        /// <summary>First account that is not trusted and holds one of these rights on the object itself, or null.</summary>
        public static string Untrusted(FileSystemSecurity sec, FileSystemRights mask, bool anyRight)
        {
            AuthorizationRuleCollection rules = sec.GetAccessRules(true, true, typeof(SecurityIdentifier));
            foreach (FileSystemAccessRule r in rules)
            {
                if (r.AccessControlType != AccessControlType.Allow) continue;
                if ((r.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;     // applies to children only
                SecurityIdentifier sid = r.IdentityReference as SecurityIdentifier;
                if (sid == null || IsOwnerPlaceholder(sid)) continue;
                if (!anyRight && !Grants(r, mask)) continue;
                if (AdminState(sid) == 1) continue;
                return Name(sid) + " (" + r.FileSystemRights + ")";
            }
            return null;
        }

        public static SecurityIdentifier Owner(FileSystemSecurity sec)
        {
            try { return sec.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier; }
            catch (Exception) { return null; }
        }
    }

    /// <summary>Can this program or script be changed by an account that is not trusted? Checked before the service
    /// runs anything as Local System.</summary>
    public static class PathTrust
    {
        /// <summary>Tests replace the check (other systems have no Windows permissions).</summary>
        public static Func<string, string> Checker;

        static readonly object lk = new object();
        static readonly Dictionary<string, KeyValuePair<DateTime, string>> cache = new Dictionary<string, KeyValuePair<DateTime, string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Null when only trusted accounts can change the file and the folders it is in; otherwise why not.</summary>
        public static string Check(string path)
        {
            if (Checker != null) return Checker(path);
            if (!Util.IsWindows || string.IsNullOrEmpty(path)) return null;
            string full;
            try { full = Path.GetFullPath(Util.NormPath(path)); }
            catch (Exception ex) { return "the path is not valid (" + ex.Message + ")"; }
            lock (lk)
            {
                KeyValuePair<DateTime, string> hit;
                if (cache.TryGetValue(full, out hit) && (DateTime.UtcNow - hit.Key).TotalSeconds < 30) return hit.Value;
            }
            string result = Evaluate(full);
            lock (lk) cache[full] = new KeyValuePair<DateTime, string>(DateTime.UtcNow, result);
            return result;
        }

        public static void Forget() { lock (lk) cache.Clear(); }

        /// <summary>Null when only trusted accounts can add or change files in this folder and change the folders it is in;
        /// otherwise why not. For working folders and search-path folders of programs run as Local System: Windows looks
        /// for DLLs (and cmd.exe for commands) in them.</summary>
        public static string CheckFolder(string path)
        {
            if (Checker != null) return Checker(path);
            if (!Util.IsWindows || string.IsNullOrEmpty(path)) return null;
            string full;
            try { full = Path.GetFullPath(Util.NormPath(path)).TrimEnd('\\'); if (full.EndsWith(":")) full += "\\"; }
            catch (Exception ex) { return "the path is not valid (" + ex.Message + ")"; }
            string key = "folder|" + full;
            lock (lk)
            {
                KeyValuePair<DateTime, string> hit;
                if (cache.TryGetValue(key, out hit) && (DateTime.UtcNow - hit.Key).TotalSeconds < 30) return hit.Value;
            }
            string result;
            string remote = Remote(full);
            if (remote != null) result = remote;
            else if (!Directory.Exists(full)) result = "the folder '" + full + "' does not exist";
            else
            {
                try { result = Folders(Native.FinalPath(full) ?? full, true); }
                catch (Exception ex) { result = "cannot check the permissions of '" + full + "': " + ex.Message; }
            }
            lock (lk) cache[key] = new KeyValuePair<DateTime, string>(DateTime.UtcNow, result);
            return result;
        }

        /// <summary>For the settings folder: null when it is local and every folder above it can only be renamed, deleted or
        /// re-permissioned by administrators (others could otherwise move the protected folder away and put their own in its place).</summary>
        public static string CheckSettingsLocation(string dataDir)
        {
            if (Checker != null || !Util.IsWindows) return null;
            string full;
            try { full = Path.GetFullPath(Util.NormPath(dataDir)).TrimEnd('\\'); }
            catch (Exception ex) { return "the path is not valid (" + ex.Message + ")"; }
            string remote = Remote(full);
            if (remote != null) return remote;
            string parent = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(parent)) return "'" + full + "' is a drive, not a folder";
            while (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent)) parent = Path.GetDirectoryName(parent);
            if (string.IsNullOrEmpty(parent)) return "no folder above '" + full + "' exists";
            try { return Folders(Native.FinalPath(parent) ?? parent, false); }
            catch (Exception ex) { return "cannot check the permissions above '" + full + "': " + ex.Message; }
        }

        static string Remote(string full)
        {
            if (full.StartsWith(@"\\")) return "'" + full + "' is on a network share; Watchkeeper cannot confirm who can change it. Copy it to a local folder that only administrators can change";
            try
            {
                DriveInfo d = new DriveInfo(Path.GetPathRoot(full));
                if (d.DriveType == DriveType.Network) return "'" + full + "' is on a network drive; copy it to a local folder that only administrators can change";
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>The folder and every folder above it: owner an administrator; nobody else may add to the first one,
        /// or delete, rename or change the permissions of any of them.</summary>
        static string Folders(string dir, bool first)
        {
            while (!string.IsNullOrEmpty(dir))
            {
                DirectorySecurity ds = Directory.GetAccessControl(dir, AccessControlSections.Owner | AccessControlSections.Access);
                SecurityIdentifier dOwner = Trust.Owner(ds);
                if (dOwner == null || Trust.AdminState(dOwner) != 1)
                    return Fix(dir, "is owned by " + (dOwner != null ? Trust.Name(dOwner) : "an unknown account") + ", which is not confirmed to be an administrator");
                FileSystemRights mask = first ? Trust.ChangeParent : Trust.ChangeAncestor;
                if (Path.GetDirectoryName(dir) == null) mask &= ~FileSystemRights.Delete;      // a drive's top folder cannot be deleted or renamed
                string who = Trust.Untrusted(ds, mask, false);
                if (who != null) return Fix(dir, (first ? "(the folder itself) " : "(a folder on its path) ") + "can be changed by " + who);
                first = false;
                string up = Path.GetDirectoryName(dir);
                if (up == dir) break;
                dir = up;
            }
            return null;
        }

        static string Evaluate(string full)
        {
            try
            {
                string remote = Remote(full);
                if (remote != null) return remote;
                if (!File.Exists(full)) return null;          // "not found" is reported by the caller
                string real = Native.FinalPath(full) ?? full;
                FileSecurity fs = File.GetAccessControl(real, AccessControlSections.Owner | AccessControlSections.Access);
                SecurityIdentifier owner = Trust.Owner(fs);
                if (owner == null || Trust.AdminState(owner) != 1)
                    return Fix(real, "is owned by " + (owner != null ? Trust.Name(owner) : "an unknown account") + ", which is not confirmed to be an administrator");
                string who = Trust.Untrusted(fs, Trust.ChangeFile, false);
                if (who != null) return Fix(real, "can be changed by " + who);
                string folders = Folders(Path.GetDirectoryName(real), true);
                if (folders != null && folders.Contains("(the folder itself) ")) folders = folders.Replace("(the folder itself) ", "(the folder it is in) ");
                return folders;
            }
            catch (Exception ex)
            {
                return "cannot check the permissions of '" + full + "': " + ex.Message;
            }
        }

        static string Fix(string path, string what)
        {
            return "'" + path + "' " + what + ", so that account could make the service run its own code as Local System. " +
                   "Move the file to Program Files (or another folder only administrators can change), or remove that permission";
        }
    }

    /// <summary>The service's own environment, which every program it starts as Local System inherits.</summary>
    public static class LaunchSafety
    {
        /// <summary>Windows service only. cmd.exe stops looking for commands in the working folder
        /// (NoDefaultCurrentDirectoryInExePath), and folders on the search path (PATH) that another account could add
        /// files to, that do not exist, or that are not full local paths are left out, since programs started as Local System
        /// look there for other programs and for DLLs. Returns what was left out and why.</summary>
        public static List<string> HardenServiceEnvironment()
        {
            List<string> dropped = new List<string>();
            if (!Util.IsWindows) return dropped;
            Environment.SetEnvironmentVariable("NoDefaultCurrentDirectoryInExePath", "1");
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            List<string> keep = new List<string>();
            foreach (string part in path.Split(';'))
            {
                string e = part.Trim().Trim('"');
                if (e.Length == 0) continue;
                string x = Environment.ExpandEnvironmentVariables(e);
                string why = FullLocal(x) ? PathTrust.CheckFolder(x) : "not a full local path";
                if (why == null) { if (!keep.Contains(e)) keep.Add(e); }
                else dropped.Add(e + ": " + why);
            }
            Environment.SetEnvironmentVariable("PATH", string.Join(";", keep.ToArray()));
            return dropped;
        }

        static bool FullLocal(string p)
        {
            return p.Length >= 3 && char.IsLetter(p[0]) && p[1] == ':' && (p[2] == '\\' || p[2] == '/');
        }
    }

    public class SecureResult
    {
        public readonly List<string> Actions = new List<string>();
        public readonly List<string> SetAside = new List<string>();
    }

    /// <summary>The settings folder (C:\ProgramData\Watchkeeper): owned by Administrators; only Administrators and
    /// Local System have access. It holds the configuration, which decides what runs as Local System.</summary>
    public static class DataDirSecurity
    {
        /// <summary>Tests replace the check of the folder (other systems have no Windows permissions).</summary>
        public static Func<string, string> Verifier;

        public static DirectorySecurity Desired()
        {
            DirectorySecurity ds = new DirectorySecurity();
            ds.SetOwner(Trust.Administrators);
            ds.SetAccessRuleProtection(true, false);
            InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            ds.AddAccessRule(new FileSystemAccessRule(Trust.Administrators, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            ds.AddAccessRule(new FileSystemAccessRule(Trust.System, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            return ds;
        }

        static bool Sensitive(string dataDir, string file)
        {
            string name = Path.GetFileName(file).ToLowerInvariant();
            if (name.StartsWith("config.json")) return true;
            string rel = file.Substring(dataDir.Length).TrimStart('\\', '/').ToLowerInvariant();
            return rel.StartsWith("state\\") || rel.StartsWith("state/") || rel.StartsWith("backups\\") || rel.StartsWith("backups/");
        }

        /// <summary>A file another account could have written: owned by an account that is not an administrator, or an
        /// account that is not an administrator may change it.</summary>
        static bool PlantedFile(FileSystemSecurity sec)
        {
            SecurityIdentifier owner = Trust.Owner(sec);
            if (owner != null && Trust.AdminState(owner) == 0) return true;
            foreach (FileSystemAccessRule r in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                SecurityIdentifier sid = r.IdentityReference as SecurityIdentifier;
                if (r.AccessControlType != AccessControlType.Allow || sid == null || Trust.IsOwnerPlaceholder(sid)) continue;
                if ((r.PropagationFlags & PropagationFlags.InheritOnly) != 0 || !Trust.Grants(r, Trust.ChangeFile)) continue;
                int state = Trust.AdminState(sid);
                if (state == 0 || (state == -1 && !r.IsInherited)) return true;
            }
            return false;
        }

        /// <summary>A folder another account controls: its owner, or an explicit permission (not the normal ones inherited
        /// from ProgramData) for an account that is not an administrator.</summary>
        static bool PlantedFolder(FileSystemSecurity sec)
        {
            SecurityIdentifier owner = Trust.Owner(sec);
            if (owner != null && Trust.AdminState(owner) == 0) return true;
            foreach (FileSystemAccessRule r in sec.GetAccessRules(true, false, typeof(SecurityIdentifier)))
            {
                SecurityIdentifier sid = r.IdentityReference as SecurityIdentifier;
                if (r.AccessControlType != AccessControlType.Allow || sid == null || Trust.IsOwnerPlaceholder(sid)) continue;
                if (Trust.Grants(r, Trust.ChangeFile) && Trust.AdminState(sid) != 1) return true;
            }
            return false;
        }

        /// <summary>Rights that let an account change a folder or what is created in it (also through inheritance).</summary>
        const FileSystemRights FolderWrite = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes |
                                             FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
                                             FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

        /// <summary>Why this folder (its own settings or what it passes on to new files) is not safe, or null. Unlike the checks
        /// for files further down, inherit-only entries count: they apply to the configuration files created inside.</summary>
        static string UnsafeRoot(FileSystemSecurity sec)
        {
            SecurityIdentifier owner = Trust.Owner(sec);
            if (owner == null || Trust.AdminState(owner) != 1) return "it is owned by " + (owner != null ? Trust.Name(owner) : "an unknown account");
            foreach (FileSystemAccessRule r in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                SecurityIdentifier sid = r.IdentityReference as SecurityIdentifier;
                if (r.AccessControlType != AccessControlType.Allow || sid == null || Trust.IsOwnerPlaceholder(sid)) continue;
                if (!Trust.Grants(r, FolderWrite) || Trust.AdminState(sid) == 1) continue;
                return Trust.Name(sid) + " could change it (" + r.FileSystemRights + ")";
            }
            return null;
        }

        /// <summary>Create the folder or make it safe: owner Administrators, access for Administrators and Local System only,
        /// inherited by everything inside. The folder is opened once without following links and held while it is worked
        /// on, and its owner and permissions are set through that open handle, so it cannot be swapped for a link. A folder
        /// another account could change (or a link in its place) is not repaired in place: it is renamed aside as a whole,
        /// a new folder is created, and only configuration and state files that an administrator wrote are copied over.
        /// Throws when the folder cannot be protected.</summary>
        public static SecureResult Secure(string dataDir)
        {
            SecureResult res = new SecureResult();
            string dir = Path.GetFullPath(dataDir).TrimEnd('\\', '/');
            if (!Util.IsWindows)
            {
                Directory.CreateDirectory(dir);
                return res;
            }
            string parent = Path.GetDirectoryName(dir);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            using (Native.Privileges("SeTakeOwnershipPrivilege", "SeRestorePrivilege", "SeBackupPrivilege"))
            {
                string aside = null;
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    SafeFileHandle h = Native.OpenFolderForSecurity(dir);
                    if (h == null)
                    {
                        if (Native.CreateSecuredFolder(dir, Desired())) res.Actions.Add("Created " + dir + " for Administrators and Local System only.");
                        continue;           // opened and checked on the next round, whoever created it
                    }
                    using (h)
                    {
                        uint attrs, links;
                        Native.HandleInfo(h, out attrs, out links);
                        string why;
                        bool replaced = false;
                        if ((attrs & 0x400) != 0) { why = "it is a link to another place"; replaced = true; }
                        else if ((attrs & 0x10) == 0) { why = "it is not a folder"; replaced = true; }
                        else why = UnsafeRoot(Native.GetSecurity(h, true));
                        if (why != null)
                        {
                            string to = dir + ".untrusted-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", Util.Inv);
                            for (int n = 2; SafeFs.EntryExists(to); n++) to = dir + ".untrusted-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", Util.Inv) + "-" + n;
                            SafeFs.RenameByHandle(h, to, false);
                            res.Actions.Add("Moved " + dir + " aside to " + to + " because " + why + ". A new settings folder is created; " +
                                            "only files an administrator wrote are copied into it.");
                            res.SetAside.Add(dir + " (now " + to + ")");
                            if (!replaced && aside == null) aside = to;
                            continue;
                        }
                        Native.SetSecurity(h, Desired());
                        Walk(dir, dir, false, res);
                        if (aside != null) CopyBack(aside, dir, res);
                        return res;
                    }
                }
            }
            throw new InvalidOperationException("the settings folder " + dir + " could not be protected: it was replaced again while it was being made safe");
        }

        static readonly string[] CarriedFiles = { "config.json", "config.json.bak", "config.json.before-deploy" };

        /// <summary>From a folder that was moved aside: configuration and state files that only an administrator could have
        /// written (owner an administrator, nobody else may change it, not a link, a single name) are copied into the new folder.</summary>
        static void CopyBack(string aside, string dir, SecureResult res)
        {
            try
            {
                using (SafeDir from = SafeDir.Open(aside, false))
                {
                    foreach (string name in CarriedFiles) CopyOne(from, name, dir, res);
                    if (!SafeFs.EntryExists(Path.Combine(aside, "state"))) return;
                    using (SafeDir state = from.Child("state", false))
                    {
                        string target = Path.Combine(dir, "state");
                        Directory.CreateDirectory(target);
                        foreach (string f in Directory.GetFiles(state.Path)) CopyOne(state, Path.GetFileName(f), target, res);
                    }
                }
            }
            catch (Exception ex) { res.Actions.Add("Nothing more was copied from " + aside + ": " + ex.Message); }
        }

        static void CopyOne(SafeDir from, string name, string toDir, SecureResult res)
        {
            string src = from.Combine(name);
            if (!SafeFs.EntryExists(src)) return;
            string why;
            byte[] data = SafeFs.ReadIfTrusted(from, name, 64 << 20, out why);
            if (data == null)
            {
                res.SetAside.Add(src);
                res.Actions.Add("Not copied: " + src + " (" + why + "). It is not loaded.");
                return;
            }
            File.WriteAllBytes(Path.Combine(toDir, name), data);
            res.Actions.Add("Copied " + src + " into the new settings folder (written by an administrator).");
        }

        /// <summary>The checks for a single file another account could have written (used when copying files back).</summary>
        public static string UntrustedFile(FileSystemSecurity sec)
        {
            SecurityIdentifier owner = Trust.Owner(sec);
            if (owner == null || Trust.AdminState(owner) != 1) return "owned by " + (owner != null ? Trust.Name(owner) : "an unknown account");
            foreach (FileSystemAccessRule r in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                SecurityIdentifier sid = r.IdentityReference as SecurityIdentifier;
                if (r.AccessControlType != AccessControlType.Allow || sid == null || Trust.IsOwnerPlaceholder(sid)) continue;
                if (!Trust.Grants(r, Trust.ChangeFile | FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes)) continue;
                if (Trust.AdminState(sid) != 1) return Trust.Name(sid) + " could change it";
            }
            return null;
        }

        /// <summary>Everything inside the settings folder: each entry is opened without following links and held while it
        /// is checked and changed, so it cannot be swapped for a link in between. Links and second names (hard links) are
        /// removed; owners are reset to Administrators and permissions to those inherited from the settings folder; a
        /// configuration or state file another account could have written is set aside.</summary>
        static void Walk(string root, string dir, bool parentPlanted, SecureResult res)
        {
            foreach (string entry in Directory.GetFileSystemEntries(dir))
            {
                SafeFileHandle h = Native.OpenForSecurity(entry);
                if (h == null)
                {
                    if (Sensitive(root, entry)) throw new IOException(entry + " cannot be opened to check who wrote it (" + Native.LastError() + ")");
                    res.Actions.Add("Not checked (it could not be opened): " + entry + ".");
                    continue;
                }
                using (h)
                {
                    uint attrs, links;
                    Native.HandleInfo(h, out attrs, out links);
                    bool isDir = (attrs & 0x10) != 0;
                    if ((attrs & 0x400) != 0)
                    {
                        SafeFs.DeleteByHandle(h, entry);
                        res.Actions.Add("Removed a link in the settings folder: " + entry + ".");
                        continue;
                    }
                    if (!isDir && links > 1)
                    {
                        SafeFs.DeleteByHandle(h, entry);         // removes this name only, not the file it also names elsewhere
                        res.Actions.Add("Removed " + entry + ": it was a second name (hard link) for a file somewhere else.");
                        continue;
                    }
                    FileSystemSecurity sec = Native.GetSecurity(h, isDir);
                    bool planted = isDir ? PlantedFolder(sec) : PlantedFile(sec);
                    Native.SetInheritedAdminOnly(h, isDir);
                    if (isDir)
                    {
                        Walk(root, entry, planted || parentPlanted, res);      // h stays open: the folder cannot be renamed meanwhile
                        continue;
                    }
                    if ((planted || parentPlanted) && Sensitive(root, entry) && !entry.EndsWith(".untrusted", StringComparison.OrdinalIgnoreCase))
                    {
                        string to = entry + ".untrusted";
                        if (SafeFs.EntryExists(to)) to = entry + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss", Util.Inv) + ".untrusted";
                        SafeFs.RenameByHandle(h, to, false);
                        res.SetAside.Add(entry);
                        res.Actions.Add("Set aside " + entry + " as " + Path.GetFileName(to) + ": it was created or changed by an account that is not an administrator. It is not loaded.");
                    }
                }
            }
        }

        /// <summary>Null when the folder and its configuration files are protected; otherwise what is wrong.</summary>
        public static string Verify(string dataDir)
        {
            if (Verifier != null) return Verifier(dataDir);
            if (!Util.IsWindows) return null;
            try
            {
                string dir = Path.GetFullPath(dataDir).TrimEnd('\\', '/');
                if (!Directory.Exists(dir)) return "the settings folder " + dir + " does not exist";
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) return "the settings folder " + dir + " is a link to another place";
                DirectorySecurity ds = Directory.GetAccessControl(dir, AccessControlSections.Owner | AccessControlSections.Access);
                SecurityIdentifier owner = Trust.Owner(ds);
                if (owner == null || Trust.AdminState(owner) != 1) return "the settings folder " + dir + " is owned by " + (owner != null ? Trust.Name(owner) : "an unknown account");
                string who = Trust.Untrusted(ds, 0, true);
                if (who != null) return "the settings folder " + dir + " can be opened by " + who;
                foreach (string name in new[] { "config.json", "config.json.bak" })
                {
                    string f = Path.Combine(dir, name);
                    if (!File.Exists(f)) continue;
                    if ((File.GetAttributes(f) & FileAttributes.ReparsePoint) != 0) return f + " is a link to another place";
                    FileSecurity fs = File.GetAccessControl(f, AccessControlSections.Owner | AccessControlSections.Access);
                    SecurityIdentifier fo = Trust.Owner(fs);
                    if (fo != null && Trust.AdminState(fo) == 0) return f + " is owned by " + Trust.Name(fo);
                    who = Trust.Untrusted(fs, 0, true);
                    if (who != null) return f + " can be opened by " + who;
                }
                return null;
            }
            catch (Exception ex) { return "cannot check the settings folder: " + ex.Message; }
        }
    }

    /// <summary>Windows calls used by the checks above and by SafeFs.</summary>
    public static class Native
    {
        // ---------------------------------------------------------------- privileges
        [StructLayout(LayoutKind.Sequential)]
        struct LUID { public uint Low; public int High; }

        [StructLayout(LayoutKind.Sequential)]
        struct TOKEN_PRIVILEGES_ONE { public int Count; public LUID Luid; public int Attributes; }

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool LookupPrivilegeValue(string system, string name, out LUID luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES_ONE state, int length, IntPtr previous, IntPtr returned);

        [DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "AdjustTokenPrivileges")]
        static extern bool AdjustTokenPrivilegesPrev(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES_ONE state, int length,
                                                     ref TOKEN_PRIVILEGES_ONE previous, out int returned);

        /// <summary>Turn on privileges the account holds for the duration of a using block, and back off afterwards
        /// (only those that were off before), so they are not left on for the process and the programs it starts.</summary>
        public static IDisposable Privileges(params string[] names)
        {
            PrivilegeScope scope = new PrivilegeScope();
            if (!Util.IsWindows) return scope;
            IntPtr token;
            if (!OpenProcessToken(GetCurrentProcess(), 0x0020 | 0x0008, out token)) return scope;
            try
            {
                foreach (string n in names)
                {
                    LUID luid;
                    if (!LookupPrivilegeValue(null, n, out luid)) continue;
                    TOKEN_PRIVILEGES_ONE tp = new TOKEN_PRIVILEGES_ONE();
                    tp.Count = 1;
                    tp.Luid = luid;
                    tp.Attributes = 0x2;
                    TOKEN_PRIVILEGES_ONE prev = new TOKEN_PRIVILEGES_ONE();
                    int got;
                    if (AdjustTokenPrivilegesPrev(token, false, ref tp, Marshal.SizeOf(typeof(TOKEN_PRIVILEGES_ONE)), ref prev, out got) &&
                        Marshal.GetLastWin32Error() == 0 && prev.Count == 1 && (prev.Attributes & 0x2) == 0)
                        scope.Enabled.Add(luid);
                }
            }
            finally { CloseHandle(token); }
            return scope;
        }

        sealed class PrivilegeScope : IDisposable
        {
            public readonly List<LUID> Enabled = new List<LUID>();
            public void Dispose()
            {
                if (Enabled.Count == 0) return;
                IntPtr token;
                if (!OpenProcessToken(GetCurrentProcess(), 0x0020 | 0x0008, out token)) return;
                try
                {
                    foreach (LUID l in Enabled)
                    {
                        TOKEN_PRIVILEGES_ONE tp = new TOKEN_PRIVILEGES_ONE();
                        tp.Count = 1;
                        tp.Luid = l;
                        tp.Attributes = 0;      // off again
                        AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
                    }
                }
                finally { CloseHandle(token); Enabled.Clear(); }
            }
        }

        // ---------------------------------------------------------------- security of an open file or folder
        [StructLayout(LayoutKind.Sequential)]
        struct BY_HANDLE_FILE_INFORMATION
        {
            public uint Attributes;
            public long Created, Accessed, Written;
            public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetFileInformationByHandle(SafeFileHandle h, out BY_HANDLE_FILE_INFORMATION info);

        [DllImport("advapi32.dll")]
        static extern uint GetSecurityInfo(SafeFileHandle h, int objectType, int info, out IntPtr owner, out IntPtr group, out IntPtr dacl,
                                           out IntPtr sacl, out IntPtr descriptor);

        [DllImport("advapi32.dll")]
        static extern int GetSecurityDescriptorLength(IntPtr descriptor);

        [DllImport("kernel32.dll")]
        static extern IntPtr LocalFree(IntPtr p);

        [StructLayout(LayoutKind.Sequential)]
        struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public bool bInheritHandle; }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateDirectoryW")]
        static extern bool CreateDirectoryNative(string path, ref SECURITY_ATTRIBUTES sa);

        const int SeFileObject = 1;
        const uint OwnerInfo = 1, DaclInfo = 4;
        public const uint ReadControl = 0x20000, WriteDac = 0x40000, WriteOwner = 0x80000;

        public static void HandleInfo(SafeFileHandle h, out uint attributes, out uint links)
        {
            BY_HANDLE_FILE_INFORMATION i;
            if (!GetFileInformationByHandle(h, out i)) throw new IOException("cannot read the file information: " + LastError());
            attributes = i.Attributes;
            links = i.Links;
        }

        /// <summary>Owner and permissions of an open file or folder (the handle needs READ_CONTROL).</summary>
        public static FileSystemSecurity GetSecurity(SafeFileHandle h, bool folder)
        {
            IntPtr owner, group, dacl, sacl, sd;
            uint rc = GetSecurityInfo(h, SeFileObject, (int)(OwnerInfo | DaclInfo), out owner, out group, out dacl, out sacl, out sd);
            if (rc != 0) throw new IOException("cannot read the permissions: " + new System.ComponentModel.Win32Exception((int)rc).Message);
            try
            {
                byte[] bytes = new byte[GetSecurityDescriptorLength(sd)];
                Marshal.Copy(sd, bytes, 0, bytes.Length);
                FileSystemSecurity sec = folder ? (FileSystemSecurity)new DirectorySecurity() : new FileSecurity();
                sec.SetSecurityDescriptorBinaryForm(bytes, AccessControlSections.Owner | AccessControlSections.Access);
                return sec;
            }
            finally { LocalFree(sd); }
        }

        [DllImport("ntdll.dll")]
        static extern int NtSetSecurityObject(SafeFileHandle h, uint info, byte[] descriptor);

        /// <summary>Write owner and permissions to the open object itself. NtSetSecurityObject is used rather than
        /// SetSecurityInfo: it changes only this object, never looks up the parent by name and never walks the folder
        /// tree by path on its own (each entry inside is opened and set by Walk instead).</summary>
        static void WriteSecurity(SafeFileHandle h, RawSecurityDescriptor sd)
        {
            byte[] bytes = new byte[sd.BinaryLength];
            sd.GetBinaryForm(bytes, 0);
            int status = NtSetSecurityObject(h, OwnerInfo | DaclInfo, bytes);
            if (status != 0) throw new IOException("cannot set the permissions (NTSTATUS 0x" + status.ToString("X8") + ")");
        }

        /// <summary>Set owner and protected permissions on an open folder (the handle needs WRITE_DAC and WRITE_OWNER).</summary>
        public static void SetSecurity(SafeFileHandle h, DirectorySecurity desired)
        {
            RawSecurityDescriptor raw = new RawSecurityDescriptor(desired.GetSecurityDescriptorBinaryForm(), 0);
            WriteSecurity(h, new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected | ControlFlags.SelfRelative,
                                                       raw.Owner, null, null, raw.DiscretionaryAcl));
        }

        /// <summary>Open a folder (or whatever has that name) without following links, to read and set its permissions and to
        /// rename it; others may not rename or delete it while the handle is open. Null when nothing has that name.</summary>
        public static SafeFileHandle OpenFolderForSecurity(string path)
        {
            SafeFileHandle h = CreateFile(path, ReadControl | WriteDac | WriteOwner | DeleteAccess | FileReadAttributes | Synchronize,
                                          ShareRead | ShareWrite, IntPtr.Zero, OpenExisting, FlagBackupSemantics | FlagOpenReparsePoint, IntPtr.Zero);
            if (!h.IsInvalid) return h;
            int err = Marshal.GetLastWin32Error();
            h.Dispose();
            if (err == 2 || err == 3) return null;
            if (err == 32) throw new IOException(path + " is held open by another program, so it cannot be protected (sharing violation)");
            throw new IOException("cannot open " + path + ": " + new System.ComponentModel.Win32Exception(err).Message + " (error " + err + ")");
        }

        /// <summary>Replace the explicit permissions of one account on an open file: allow these rights, or none (0).
        /// Inherited permissions and those of other accounts are kept. The handle needs READ_CONTROL and WRITE_DAC.</summary>
        public static void SetExplicitAccess(SafeFileHandle h, SecurityIdentifier sid, int rights)
        {
            FileSecurity sec = (FileSecurity)GetSecurity(h, false);
            sec.PurgeAccessRules(sid);
            if (rights != 0) sec.AddAccessRule(new FileSystemAccessRule(sid, (FileSystemRights)rights, AccessControlType.Allow));
            RawSecurityDescriptor raw = new RawSecurityDescriptor(sec.GetSecurityDescriptorBinaryForm(), 0);
            byte[] bytes = new byte[raw.BinaryLength];
            raw.GetBinaryForm(bytes, 0);
            int status = NtSetSecurityObject(h, DaclInfo, bytes);
            if (status != 0) throw new IOException("cannot set the permissions (NTSTATUS 0x" + status.ToString("X8") + ")");
        }

        /// <summary>Open a file or folder inside the settings folder without following links (null when it cannot be opened).</summary>
        public static SafeFileHandle OpenForSecurity(string path)
        {
            SafeFileHandle h = CreateFile(path, ReadControl | WriteDac | WriteOwner | DeleteAccess | FileReadAttributes | Synchronize,
                                          ShareRead | ShareWrite, IntPtr.Zero, OpenExisting, FlagBackupSemantics | FlagOpenReparsePoint, IntPtr.Zero);
            if (!h.IsInvalid) return h;
            h.Dispose();
            return null;
        }

        /// <summary>Owner Administrators and only the permissions passed on by the settings folder (Administrators and Local
        /// System, full control), written explicitly as inherited entries so the result does not depend on the parent lookup.</summary>
        public static void SetInheritedAdminOnly(SafeFileHandle h, bool folder)
        {
            AceFlags flags = AceFlags.Inherited | (folder ? AceFlags.ContainerInherit | AceFlags.ObjectInherit : AceFlags.None);
            RawAcl acl = new RawAcl(GenericAcl.AclRevision, 2);
            acl.InsertAce(0, new CommonAce(flags, AceQualifier.AccessAllowed, 0x1F01FF, Trust.Administrators, false, null));
            acl.InsertAce(1, new CommonAce(flags, AceQualifier.AccessAllowed, 0x1F01FF, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), false, null));
            WriteSecurity(h, new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclAutoInherited | ControlFlags.SelfRelative,
                                                       Trust.Administrators, null, null, acl));
        }

        /// <summary>Create a folder with these permissions from the start. False when something with that name already exists.</summary>
        public static bool CreateSecuredFolder(string path, DirectorySecurity security)
        {
            byte[] sd = security.GetSecurityDescriptorBinaryForm();
            GCHandle pin = GCHandle.Alloc(sd, GCHandleType.Pinned);
            try
            {
                SECURITY_ATTRIBUTES sa = new SECURITY_ATTRIBUTES();
                sa.nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES));
                sa.lpSecurityDescriptor = pin.AddrOfPinnedObject();
                if (CreateDirectoryNative(path, ref sa)) return true;
                int err = Marshal.GetLastWin32Error();
                if (err == 183) return false;           // already exists
                throw new IOException("cannot create " + path + ": " + new System.ComponentModel.Win32Exception(err).Message);
            }
            finally { pin.Free(); }
        }

        /// <summary>Turn on privileges the account already holds (administrators hold these; they start switched off).
        /// Stays on for the process: only for tests and short-lived tools; the service uses Privileges() instead.</summary>
        public static void EnablePrivileges(params string[] names)
        {
            if (!Util.IsWindows) return;
            IntPtr token;
            if (!OpenProcessToken(GetCurrentProcess(), 0x0020 | 0x0008, out token)) return;     // ADJUST_PRIVILEGES | QUERY
            try
            {
                foreach (string n in names)
                {
                    LUID luid;
                    if (!LookupPrivilegeValue(null, n, out luid)) continue;
                    TOKEN_PRIVILEGES_ONE tp = new TOKEN_PRIVILEGES_ONE();
                    tp.Count = 1;
                    tp.Luid = luid;
                    tp.Attributes = 0x2;     // SE_PRIVILEGE_ENABLED
                    AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
                }
            }
            finally { CloseHandle(token); }
        }

        // ---------------------------------------------------------------- final path of a file or folder
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint size, uint flags);

        public const uint FileReadAttributes = 0x80, Synchronize = 0x00100000, DeleteAccess = 0x00010000;
        public const uint ShareRead = 1, ShareWrite = 2, ShareDelete = 4;
        public const uint OpenExisting = 3;
        public const uint FlagBackupSemantics = 0x02000000, FlagOpenReparsePoint = 0x00200000;

        public static string FinalPathOf(SafeFileHandle h)
        {
            StringBuilder sb = new StringBuilder(1024);
            uint n = GetFinalPathNameByHandle(h, sb, (uint)sb.Capacity, 0);
            if (n == 0) return null;
            if (n >= sb.Capacity)
            {
                sb = new StringBuilder((int)n + 1);
                if (GetFinalPathNameByHandle(h, sb, (uint)sb.Capacity, 0) == 0) return null;
            }
            string p = sb.ToString();
            if (p.StartsWith(@"\\?\UNC\")) return @"\\" + p.Substring(8);
            if (p.StartsWith(@"\\?\")) return p.Substring(4);
            return p;
        }

        /// <summary>The path after following links, or null when it cannot be opened.</summary>
        public static string FinalPath(string path)
        {
            if (!Util.IsWindows) return null;
            using (SafeFileHandle h = CreateFile(path, FileReadAttributes, ShareRead | ShareWrite | ShareDelete, IntPtr.Zero, OpenExisting, FlagBackupSemantics, IntPtr.Zero))
            {
                if (h.IsInvalid) return null;
                return FinalPathOf(h);
            }
        }

        // ---------------------------------------------------------------- local Administrators members
        [StructLayout(LayoutKind.Sequential)]
        struct LOCALGROUP_MEMBERS_INFO_0 { public IntPtr Sid; }

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
        static extern int NetLocalGroupGetMembers(string server, string group, int level, out IntPtr buf, int max, out int read, out int total, IntPtr resume);

        [DllImport("netapi32.dll")]
        static extern int NetApiBufferFree(IntPtr buf);

        public static HashSet<string> LocalGroupMemberSids(SecurityIdentifier group)
        {
            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!Util.IsWindows) return set;
            try
            {
                string name = ((NTAccount)group.Translate(typeof(NTAccount))).Value;
                int slash = name.IndexOf('\\');
                if (slash >= 0) name = name.Substring(slash + 1);
                IntPtr buf;
                int read, total;
                int rc = NetLocalGroupGetMembers(null, name, 0, out buf, -1, out read, out total, IntPtr.Zero);
                if (rc != 0) return set;
                try
                {
                    int size = Marshal.SizeOf(typeof(LOCALGROUP_MEMBERS_INFO_0));
                    for (int i = 0; i < read; i++)
                    {
                        LOCALGROUP_MEMBERS_INFO_0 m = (LOCALGROUP_MEMBERS_INFO_0)Marshal.PtrToStructure(new IntPtr(buf.ToInt64() + i * size), typeof(LOCALGROUP_MEMBERS_INFO_0));
                        set.Add(new SecurityIdentifier(m.Sid).Value);
                    }
                }
                finally { NetApiBufferFree(buf); }
            }
            catch (Exception) { }
            return set;
        }

        // ---------------------------------------------------------------- group membership of any account (Authz)
        [DllImport("authz.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool AuthzInitializeResourceManager(int flags, IntPtr check, IntPtr compute, IntPtr free, string name, out IntPtr rm);

        [DllImport("authz.dll", SetLastError = true)]
        static extern bool AuthzInitializeContextFromSid(int flags, byte[] sid, IntPtr rm, IntPtr expiration, long id, IntPtr args, out IntPtr ctx);

        [DllImport("authz.dll", SetLastError = true)]
        static extern bool AuthzGetInformationFromContext(IntPtr ctx, int infoClass, int size, out int needed, IntPtr buffer);

        [DllImport("authz.dll")]
        static extern bool AuthzFreeContext(IntPtr ctx);

        [DllImport("authz.dll")]
        static extern bool AuthzFreeResourceManager(IntPtr rm);

        /// <summary>1 = member of the local Administrators group (directly or through groups), 0 = not, -1 = cannot tell.</summary>
        public static int SidIsMemberOfAdministrators(SecurityIdentifier sid)
        {
            if (!Util.IsWindows) return -1;
            IntPtr rm = IntPtr.Zero, ctx = IntPtr.Zero, buf = IntPtr.Zero;
            try
            {
                if (!AuthzInitializeResourceManager(1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, null, out rm)) return -1;
                byte[] b = new byte[sid.BinaryLength];
                sid.GetBinaryForm(b, 0);
                if (!AuthzInitializeContextFromSid(0, b, rm, IntPtr.Zero, 0, IntPtr.Zero, out ctx)) return -1;
                int needed;
                AuthzGetInformationFromContext(ctx, 2, 0, out needed, IntPtr.Zero);       // AuthzContextInfoGroupsSids
                if (needed <= 0) return -1;
                buf = Marshal.AllocHGlobal(needed);
                if (!AuthzGetInformationFromContext(ctx, 2, needed, out needed, buf)) return -1;
                int count = Marshal.ReadInt32(buf);
                int offset = IntPtr.Size;                                   // TOKEN_GROUPS: count, then aligned SID_AND_ATTRIBUTES
                int entry = IntPtr.Size * 2;
                for (int i = 0; i < count; i++)
                {
                    IntPtr p = Marshal.ReadIntPtr(buf, offset + i * entry);
                    if (new SecurityIdentifier(p).Equals(Trust.Administrators)) return 1;
                }
                return 0;
            }
            catch (Exception) { return -1; }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                if (ctx != IntPtr.Zero) AuthzFreeContext(ctx);
                if (rm != IntPtr.Zero) AuthzFreeResourceManager(rm);
            }
        }

        public static string LastError()
        {
            int code = Marshal.GetLastWin32Error();
            return new Win32Exception(code).Message + " (error " + code + ")";
        }
    }
}
