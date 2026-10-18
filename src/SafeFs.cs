// File operations that cannot be redirected. Folder policies and the vertical conversion run as Local System
// in folders other accounts can write to. A folder replaced by a link (junction, symbolic link, mount point)
// or a file that is a link would otherwise let such an account make the service delete, overwrite or create
// files anywhere. Here:
//   - every folder from the drive root down to the one worked in is opened without following links, checked to
//     be a real folder, and held open so it cannot be renamed, deleted or replaced while it is used;
//   - files are opened without following links; links and files with more than one name (hard links) are refused,
//     and the file must really be inside the folder that was checked;
//   - deletes and renames act on the open file, never on a name looked up again; copies go to a new temporary file
//     that must not exist yet and are renamed into place through their handle.
// On other systems (development, tests) the same checks are made with the managed file API.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Watchkeeper
{
    public class SafeFsException : IOException
    {
        public SafeFsException(string message) : base(message) { }
    }

    /// <summary>A checked folder, with every folder above it held open.</summary>
    public sealed class SafeDir : IDisposable
    {
        readonly List<SafeFileHandle> held;
        readonly SafeDir parent;
        public readonly string Path;      // as given (normalized), used to build names
        public readonly string Final;     // where it really is (after the drive's own mapping, e.g. SUBST)

        SafeDir(string path, string final, List<SafeFileHandle> held, SafeDir parent)
        {
            Path = path;
            Final = final;
            this.held = held;
            this.parent = parent;
        }

        static string Clean(string path)
        {
            string p = System.IO.Path.GetFullPath(Util.NormPath(path));
            string root = System.IO.Path.GetPathRoot(p) ?? "";
            if (p.Length > root.Length) p = p.TrimEnd('\\', '/');
            return p;
        }

        /// <summary>Open a folder and every folder above it, refusing links. create: missing folders are made.</summary>
        public static SafeDir Open(string path, bool create)
        {
            string full = Clean(path);
            if (!Util.IsWindows) return OpenManaged(full, create);
            string root = System.IO.Path.GetPathRoot(full);
            List<SafeFileHandle> handles = new List<SafeFileHandle>();
            try
            {
                SafeFileHandle h = SafeFs.OpenDirHandle(root.EndsWith("\\") ? root : root + "\\", false);
                handles.Add(h);
                string cur = root;
                string rest = full.Substring(root.Length);
                foreach (string part in rest.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    cur = System.IO.Path.Combine(cur, part);
                    if (create && !SafeFs.EntryExists(cur))
                    {
                        if (!Directory.CreateDirectory(cur).Exists) throw new SafeFsException("cannot create " + cur);
                    }
                    handles.Add(SafeFs.OpenDirHandle(cur, true));
                }
                string final = Native.FinalPathOf(handles[handles.Count - 1]) ?? full;
                return new SafeDir(full, final, handles, null);
            }
            catch (Exception)
            {
                foreach (SafeFileHandle x in handles) x.Dispose();
                throw;
            }
        }

        static SafeDir OpenManaged(string full, bool create)
        {
            string root = System.IO.Path.GetPathRoot(full);
            string cur = root;
            foreach (string part in full.Substring(root.Length).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                cur = System.IO.Path.Combine(cur, part);
                if (create && !SafeFs.EntryExists(cur)) Directory.CreateDirectory(cur);
                SafeFs.CheckManagedDir(cur);
            }
            if (!Directory.Exists(full)) throw new SafeFsException("folder does not exist: " + full);
            return new SafeDir(full, full, new List<SafeFileHandle>(), null);
        }

        /// <summary>A sub-folder (one level), checked and held like the others.</summary>
        public SafeDir Child(string name, bool create)
        {
            SafeFs.CheckName(name);
            string p = System.IO.Path.Combine(Path, name);
            if (create && !SafeFs.EntryExists(p)) Directory.CreateDirectory(p);
            if (!Util.IsWindows)
            {
                SafeFs.CheckManagedDir(p);
                return new SafeDir(p, p, new List<SafeFileHandle>(), this);
            }
            SafeFileHandle h = SafeFs.OpenDirHandle(p, true);
            string final = Native.FinalPathOf(h);
            if (final == null || !SafeFs.Within(final, Final))
            {
                h.Dispose();
                throw new SafeFsException(p + " is not inside " + Path);
            }
            return new SafeDir(p, final, new List<SafeFileHandle> { h }, this);
        }

        /// <summary>A folder below this one ("a\b\c"), every level checked.</summary>
        public SafeDir Descend(string relative, bool create, List<SafeDir> keep)
        {
            SafeDir d = this;
            foreach (string part in (relative ?? "").Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                d = d.Child(part, create);
                if (keep != null) keep.Add(d);
            }
            return d;
        }

        public string Combine(string name)
        {
            SafeFs.CheckName(name);
            return System.IO.Path.Combine(Path, name);
        }

        public void Dispose()
        {
            foreach (SafeFileHandle h in held) h.Dispose();
            held.Clear();
        }
    }

    public static class SafeFs
    {
        // ---------------------------------------------------------------- Windows calls
        [StructLayout(LayoutKind.Sequential)]
        struct BY_HANDLE_FILE_INFORMATION
        {
            public uint Attributes;
            public long Created, Accessed, Written;
            public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetFileInformationByHandle(SafeFileHandle h, out BY_HANDLE_FILE_INFORMATION info);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetFileInformationByHandle(SafeFileHandle h, int infoClass, ref byte dispose, int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetFileInformationByHandle(SafeFileHandle h, int infoClass, IntPtr info, int size);

        const int FileDispositionInfo = 4, FileRenameInfo = 3;
        const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
        const uint CreateNew = 1;
        const uint ListDirectory = 0x1;
        const uint FileAttributeReparse = 0x400, FileAttributeDirectory = 0x10;

        public static void CheckName(string name)
        {
            if (string.IsNullOrEmpty(name) || name == "." || name == ".." || name.IndexOfAny(new[] { '\\', '/', ':' }) >= 0 ||
                name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
                throw new SafeFsException("not a plain file or folder name: '" + name + "'");
        }

        public static bool Within(string final, string root)
        {
            string r = root.TrimEnd('\\', '/');
            return final.Length > r.Length + 1 && final.StartsWith(r, StringComparison.OrdinalIgnoreCase) && (final[r.Length] == '\\' || final[r.Length] == '/');
        }

        static string Err() { return Native.LastError(); }

        /// <summary>Open a folder without following links and without allowing it to be renamed or deleted while held.</summary>
        /// Windows only records the sharing mode of an open that asks for read, write or delete access, so the handle asks to
        /// list the folder: with attribute access alone it would not stop others from renaming the folder.
        internal static SafeFileHandle OpenDirHandle(string path, bool refuseLink)
        {
            SafeFileHandle h = Native.CreateFile(path, ListDirectory | Native.FileReadAttributes | Native.Synchronize, Native.ShareRead | Native.ShareWrite, IntPtr.Zero,
                                                 Native.OpenExisting, Native.FlagBackupSemantics | Native.FlagOpenReparsePoint, IntPtr.Zero);
            if (h.IsInvalid && Marshal.GetLastWin32Error() == 5)
            {
                // the account may pass through but not list this folder: still checked for links, but it is not held
                h.Dispose();
                h = Native.CreateFile(path, Native.FileReadAttributes | Native.Synchronize, Native.ShareRead | Native.ShareWrite, IntPtr.Zero,
                                      Native.OpenExisting, Native.FlagBackupSemantics | Native.FlagOpenReparsePoint, IntPtr.Zero);
            }
            if (h.IsInvalid) throw new SafeFsException("cannot open folder " + path + ": " + Err());
            BY_HANDLE_FILE_INFORMATION info;
            if (!GetFileInformationByHandle(h, out info)) { h.Dispose(); throw new SafeFsException("cannot read " + path + ": " + Err()); }
            if ((info.Attributes & FileAttributeDirectory) == 0) { h.Dispose(); throw new SafeFsException(path + " is not a folder"); }
            if (refuseLink && (info.Attributes & FileAttributeReparse) != 0)
            {
                h.Dispose();
                throw new SafeFsException(path + " is a link (junction, symbolic link or mount point); links are never followed");
            }
            return h;
        }

        internal static void CheckManagedDir(string path)
        {
            FileAttributes a;
            try { a = File.GetAttributes(path); }
            catch (Exception ex) { throw new SafeFsException("cannot open folder " + path + ": " + ex.Message); }
            if ((a & FileAttributes.Directory) == 0) throw new SafeFsException(path + " is not a folder");
            if ((a & FileAttributes.ReparsePoint) != 0 || IsManagedLink(path))
                throw new SafeFsException(path + " is a link (junction, symbolic link or mount point); links are never followed");
        }

        /// <summary>Mono reports symbolic links as ReparsePoint only sometimes; ask the system directly.</summary>
        static bool IsManagedLink(string path)
        {
            if (Util.IsWindows) return false;
            return LinkTarget(System.IO.Path.GetFullPath(path).TrimEnd('/')) != null;
        }

        [DllImport("libc", SetLastError = true)]
        static extern int readlink(string path, byte[] buf, int size);

        static string LinkTarget(string path)
        {
            try
            {
                byte[] buf = new byte[4096];
                int n = readlink(path, buf, buf.Length);
                return n > 0 ? Encoding.UTF8.GetString(buf, 0, n) : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>True when anything (file, folder or link) has this name; links are not followed.</summary>
        public static bool EntryExists(string path)
        {
            if (!Util.IsWindows) return File.Exists(path) || Directory.Exists(path) || LinkTarget(path) != null;
            SafeFileHandle h = Native.CreateFile(path, Native.FileReadAttributes, Native.ShareRead | Native.ShareWrite | Native.ShareDelete, IntPtr.Zero,
                                                 Native.OpenExisting, Native.FlagBackupSemantics | Native.FlagOpenReparsePoint, IntPtr.Zero);
            bool ok = !h.IsInvalid;
            h.Dispose();
            if (ok) return true;
            int e = Marshal.GetLastWin32Error();
            return e != 2 && e != 3;          // not found / path not found
        }

        /// <summary>Open a file inside a checked folder: not a link, a single name, really inside the folder.</summary>
        internal static SafeFileHandle OpenFile(SafeDir dir, string name, uint access, uint share)
        {
            string path = dir.Combine(name);
            SafeFileHandle h = Native.CreateFile(path, access | Native.FileReadAttributes | Native.Synchronize, share, IntPtr.Zero,
                                                 Native.OpenExisting, Native.FlagOpenReparsePoint, IntPtr.Zero);
            if (h.IsInvalid) throw new SafeFsException("cannot open " + path + ": " + Err());
            BY_HANDLE_FILE_INFORMATION info;
            if (!GetFileInformationByHandle(h, out info)) { h.Dispose(); throw new SafeFsException("cannot read " + path + ": " + Err()); }
            string refuse = null;
            if ((info.Attributes & FileAttributeReparse) != 0) refuse = "is a link; links are never followed";
            else if ((info.Attributes & FileAttributeDirectory) != 0) refuse = "is a folder";
            else if (info.Links > 1) refuse = "has more than one name (hard link); it is left alone";
            else
            {
                string final = Native.FinalPathOf(h);
                if (final == null || !Within(final, dir.Final)) refuse = "is not inside " + dir.Path;
            }
            if (refuse != null) { h.Dispose(); throw new SafeFsException(path + " " + refuse); }
            return h;
        }

        static void CheckManagedFile(string path)
        {
            FileAttributes a = File.GetAttributes(path);
            if ((a & FileAttributes.ReparsePoint) != 0 || LinkTarget(path) != null) throw new SafeFsException(path + " is a link; links are never followed");
            if ((a & FileAttributes.Directory) != 0) throw new SafeFsException(path + " is a folder");
        }

        internal static void DeleteByHandle(SafeFileHandle h, string what)
        {
            byte yes = 1;
            if (!SetFileInformationByHandle(h, FileDispositionInfo, ref yes, 1)) throw new SafeFsException("cannot delete " + what + ": " + Err());
        }

        /// <summary>Rename the open file to a full path (inside held folders). replace: an existing entry of that name is replaced
        /// (the entry itself, never what a link points at).</summary>
        internal static void RenameByHandle(SafeFileHandle h, string target, bool replace)
        {
            byte[] name = Encoding.Unicode.GetBytes(target);
            int size = 4 + IntPtr.Size + 4 + name.Length + 2;     // ReplaceIfExists (padded), RootDirectory, FileNameLength, FileName
            size = Math.Max(size, 24);
            IntPtr buf = Marshal.AllocHGlobal(size + 8);
            try
            {
                for (int i = 0; i < size + 8; i++) Marshal.WriteByte(buf, i, 0);
                Marshal.WriteByte(buf, 0, (byte)(replace ? 1 : 0));
                int offRoot = IntPtr.Size == 8 ? 8 : 4;
                Marshal.WriteIntPtr(buf, offRoot, IntPtr.Zero);
                int offLen = offRoot + IntPtr.Size;
                Marshal.WriteInt32(buf, offLen, name.Length);
                Marshal.Copy(name, 0, new IntPtr(buf.ToInt64() + offLen + 4), name.Length);
                if (!SetFileInformationByHandle(h, FileRenameInfo, buf, offLen + 4 + name.Length + 2))
                    throw new SafeFsException("cannot move to " + target + ": " + Err());
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        // ---------------------------------------------------------------- operations
        /// <summary>Delete a file of a checked folder (never through a link).</summary>
        public static void Delete(SafeDir dir, string name)
        {
            if (!Util.IsWindows)
            {
                string p = dir.Combine(name);
                CheckManagedFile(p);
                File.Delete(p);
                return;
            }
            using (SafeFileHandle h = OpenFile(dir, name, Native.DeleteAccess, Native.ShareRead | Native.ShareWrite | Native.ShareDelete))
                DeleteByHandle(h, dir.Combine(name));
        }

        /// <summary>Delete an entry of whatever kind without following it (a link is removed, not what it points at).
        /// Used for leftovers of our own temporary names.</summary>
        public static void DeleteEntry(SafeDir dir, string name)
        {
            string p = dir.Combine(name);
            if (!EntryExists(p)) return;
            if (!Util.IsWindows)
            {
                if (Directory.Exists(p) && LinkTarget(p) == null) Directory.Delete(p, false); else File.Delete(p);
                return;
            }
            SafeFileHandle h = Native.CreateFile(p, Native.DeleteAccess | Native.FileReadAttributes, Native.ShareRead | Native.ShareWrite | Native.ShareDelete,
                                                 IntPtr.Zero, Native.OpenExisting, Native.FlagOpenReparsePoint | Native.FlagBackupSemantics, IntPtr.Zero);
            if (h.IsInvalid) throw new SafeFsException("cannot remove " + p + ": " + Err());
            using (h) DeleteByHandle(h, p);
        }

        /// <summary>Remove an empty sub-folder (not a link); false when it is not empty.</summary>
        public static bool DeleteEmptyDir(SafeDir parent, string name)
        {
            string p = parent.Combine(name);
            if (!Util.IsWindows)
            {
                CheckManagedDir(p);
                if (Directory.GetFileSystemEntries(p).Length > 0) return false;
                Directory.Delete(p, false);
                return true;
            }
            SafeFileHandle h = Native.CreateFile(p, Native.DeleteAccess | Native.FileReadAttributes, Native.ShareRead | Native.ShareWrite | Native.ShareDelete,
                                                 IntPtr.Zero, Native.OpenExisting, Native.FlagOpenReparsePoint | Native.FlagBackupSemantics, IntPtr.Zero);
            if (h.IsInvalid) return false;
            using (h)
            {
                BY_HANDLE_FILE_INFORMATION info;
                if (!GetFileInformationByHandle(h, out info) || (info.Attributes & FileAttributeReparse) != 0 || (info.Attributes & FileAttributeDirectory) == 0) return false;
                string final = Native.FinalPathOf(h);
                if (final == null || !Within(final, parent.Final)) return false;
                byte yes = 1;
                return SetFileInformationByHandle(h, FileDispositionInfo, ref yes, 1);     // fails when not empty
            }
        }

        /// <summary>Set Date Modified of a file in a checked folder (never through a link).</summary>
        public static void SetModified(SafeDir dir, string name, DateTime utc)
        {
            if (!Util.IsWindows)
            {
                string p = dir.Combine(name);
                CheckManagedFile(p);
                File.SetLastWriteTimeUtc(p, utc);
                return;
            }
            using (SafeFileHandle h = OpenFile(dir, name, 0x100 /* FILE_WRITE_ATTRIBUTES */, Native.ShareRead | Native.ShareWrite | Native.ShareDelete))
                SetWritten(h, utc.ToFileTimeUtc());
        }

        /// <summary>Copy a file into a checked folder under a new name: a temporary file that must not exist yet, size
        /// checked, Date Modified kept, then renamed into place. replace: an existing file of that name is replaced.</summary>
        public static void Copy(SafeDir src, string name, SafeDir dst, string dstName, bool replace)
        {
            string target = dst.Combine(dstName);
            string tmpName = dstName + ".copying";
            if (!Util.IsWindows)
            {
                string from = src.Combine(name);
                CheckManagedFile(from);
                string tmp = dst.Combine(tmpName);
                DeleteEntry(dst, tmpName);
                File.Copy(from, tmp, false);
                long want = new FileInfo(from).Length, got = new FileInfo(tmp).Length;
                if (want != got) { File.Delete(tmp); throw new IOException("copy check failed: " + got + " of " + want + " bytes arrived"); }
                File.SetLastWriteTimeUtc(tmp, File.GetLastWriteTimeUtc(from));
                if (EntryExists(target))
                {
                    if (!replace) { File.Delete(tmp); throw new IOException(target + " already exists"); }
                    if (LinkTarget(target) != null || Directory.Exists(target)) { File.Delete(tmp); throw new SafeFsException(target + " is a link or a folder"); }
                    File.Delete(target);
                }
                File.Move(tmp, target);
                return;
            }
            DeleteEntry(dst, tmpName);
            using (SafeFileHandle sh = OpenFile(src, name, GenericRead, Native.ShareRead | Native.ShareWrite))
            {
                SafeFileHandle th = Native.CreateFile(dst.Combine(tmpName), GenericWrite | GenericRead | Native.DeleteAccess, Native.ShareRead, IntPtr.Zero,
                                                      CreateNew, Native.FlagOpenReparsePoint, IntPtr.Zero);
                if (th.IsInvalid) throw new SafeFsException("cannot create " + dst.Combine(tmpName) + ": " + Err());
                bool done = false;
                try
                {
                    long copied = 0, want;
                    using (FileStream input = new FileStream(new SafeFileHandle(sh.DangerousGetHandle(), false), FileAccess.Read, 1 << 20))
                    using (FileStream output = new FileStream(new SafeFileHandle(th.DangerousGetHandle(), false), FileAccess.ReadWrite, 1 << 20))
                    {
                        want = input.Length;
                        byte[] buf = new byte[1 << 20];
                        int n;
                        while ((n = input.Read(buf, 0, buf.Length)) > 0) { output.Write(buf, 0, n); copied += n; }
                        output.Flush(true);
                        if (copied != want || output.Length != want) throw new IOException("copy check failed: " + output.Length + " of " + want + " bytes arrived");
                    }
                    BY_HANDLE_FILE_INFORMATION si;
                    if (GetFileInformationByHandle(sh, out si)) SetWritten(th, si.Written);
                    if (!replace && EntryExists(target)) throw new IOException(target + " already exists");
                    RenameByHandle(th, target, replace);
                    done = true;
                }
                finally
                {
                    if (!done) { try { DeleteByHandle(th, tmpName); } catch (Exception) { } }
                    th.Dispose();
                }
            }
        }

        /// <summary>The bytes of a file in a checked folder when only administrators could have written it (owner an
        /// administrator, nobody else may change it, not a link, a single name); otherwise null and why.</summary>
        public static byte[] ReadIfTrusted(SafeDir dir, string name, long maxBytes, out string why)
        {
            why = null;
            if (!Util.IsWindows)
            {
                string p = dir.Combine(name);
                CheckManagedFile(p);
                return File.ReadAllBytes(p);
            }
            using (SafeFileHandle h = OpenFile(dir, name, GenericRead, Native.ShareRead))
            {
                why = DataDirSecurity.UntrustedFile(Native.GetSecurity(h, false));
                if (why != null) return null;
                using (FileStream fs = new FileStream(new SafeFileHandle(h.DangerousGetHandle(), false), FileAccess.Read))
                {
                    if (fs.Length > maxBytes) { why = "larger than " + maxBytes + " bytes"; return null; }
                    byte[] data = new byte[fs.Length];
                    int off = 0, n;
                    while (off < data.Length && (n = fs.Read(data, off, data.Length - off)) > 0) off += n;
                    return data;
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct FILE_BASIC_INFO { public long Created, Accessed, Written, Changed; public uint Attributes; }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetFileInformationByHandle(SafeFileHandle h, int infoClass, ref FILE_BASIC_INFO info, int size);

        static void SetWritten(SafeFileHandle h, long written)
        {
            FILE_BASIC_INFO b = new FILE_BASIC_INFO();
            b.Written = written;          // 0 elsewhere = leave unchanged
            SetFileInformationByHandle(h, 0, ref b, Marshal.SizeOf(typeof(FILE_BASIC_INFO)));
        }

        /// <summary>Move a file into a checked folder. Same drive: renamed through its handle. Another drive: copied (see Copy),
        /// then the original is deleted through its handle.</summary>
        public static void Move(SafeDir src, string name, SafeDir dst, string dstName, bool replace)
        {
            string target = dst.Combine(dstName);
            if (!Util.IsWindows)
            {
                string from = src.Combine(name);
                CheckManagedFile(from);
                if (SameRoot(src.Path, dst.Path))
                {
                    if (EntryExists(target))
                    {
                        if (!replace) throw new IOException(target + " already exists");
                        if (LinkTarget(target) != null || Directory.Exists(target)) throw new SafeFsException(target + " is a link or a folder");
                        File.Delete(target);
                    }
                    File.Move(from, target);
                    return;
                }
                Copy(src, name, dst, dstName, replace);
                File.Delete(from);
                return;
            }
            using (SafeFileHandle h = OpenFile(src, name, Native.DeleteAccess, Native.ShareRead | Native.ShareWrite))
            {
                if (SameVolume(src.Final, dst.Final))
                {
                    if (!replace && EntryExists(target)) throw new IOException(target + " already exists");
                    RenameByHandle(h, target, replace);
                    return;
                }
            }
            Copy(src, name, dst, dstName, replace);
            Delete(src, name);
        }

        static bool SameRoot(string a, string b)
        {
            return string.Equals(System.IO.Path.GetPathRoot(a), System.IO.Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase);
        }

        static bool SameVolume(string a, string b)
        {
            if (FileOps.ForceCrossVolume) return false;
            return SameRoot(a, b);
        }

        /// <summary>A name that is free in the folder: "name (1).ext", "name (2).ext"... (links count as taken).</summary>
        public static string FreeName(SafeDir dir, string name)
        {
            if (!EntryExists(dir.Combine(name))) return name;
            string stem = System.IO.Path.GetFileNameWithoutExtension(name), ext = System.IO.Path.GetExtension(name);
            for (int i = 1; i < 100000; i++)
            {
                string n = stem + " (" + i.ToString(Util.Inv) + ")" + ext;
                if (!EntryExists(dir.Combine(n))) return n;
            }
            throw new IOException("no free name for " + name + " in " + dir.Path);
        }

        /// <summary>Create a new empty file that must not exist yet and keep it open (without allowing it to be deleted or
        /// renamed by others), so it cannot be swapped for a link while another program (ffmpeg) writes it by name.</summary>
        public static HeldFile CreateHeld(SafeDir dir, string name)
        {
            string p = dir.Combine(name);
            DeleteEntry(dir, name);
            if (!Util.IsWindows)
            {
                using (new FileStream(p, FileMode.CreateNew, FileAccess.Write)) { }
                return new HeldFile(dir, name, null);
            }
            // read access so that Windows records the sharing mode (no delete, no rename by others while held);
            // READ_CONTROL and WRITE_DAC to let the ffmpeg account write this one file
            SafeFileHandle h = Native.CreateFile(p, ListDirectory | Native.FileReadAttributes | Native.Synchronize | Native.ReadControl | Native.WriteDac,
                                                 Native.ShareRead | Native.ShareWrite, IntPtr.Zero, CreateNew, Native.FlagOpenReparsePoint, IntPtr.Zero);
            if (h.IsInvalid) throw new SafeFsException("cannot create " + p + ": " + Err());
            return new HeldFile(dir, name, h);
        }

        public sealed class HeldFile : IDisposable
        {
            readonly SafeDir dir;
            readonly string name;
            SafeFileHandle h;
            public HeldFile(SafeDir dir, string name, SafeFileHandle h) { this.dir = dir; this.name = name; this.h = h; }
            public string Path { get { return dir.Combine(name); } }
            public string Name { get { return name; } }

            /// <summary>Move it to another name in the same folder (replacing an existing entry of that name, never
            /// writing through a link). The file is opened again without following links before it is moved.</summary>
            public void MoveTo(string newName)
            {
                Dispose();
                Move(dir, name, dir, newName, true);
            }

            /// <summary>When ffmpeg runs as LOCAL SERVICE: let that account read and write this file (and nothing else here).</summary>
            public void AllowMedia()
            {
                if (h != null && Util.MediaAsLocalService) Native.SetExplicitAccess(h, Util.LocalServiceSid, 0x120089 | 0x120116 /* generic read and write */);
            }

            /// <summary>Take that permission away again once ffmpeg and ffprobe are done with the file.</summary>
            public void RevokeMedia()
            {
                if (h != null && Util.IsWindows) Native.SetExplicitAccess(h, Util.LocalServiceSid, 0);
            }

            /// <summary>Remove it (when the work failed).</summary>
            public void Discard()
            {
                Dispose();
                try { if (EntryExists(Path)) DeleteEntry(dir, name); } catch (Exception) { }
            }

            public void Dispose() { if (h != null) { h.Dispose(); h = null; } }
        }
    }
}
