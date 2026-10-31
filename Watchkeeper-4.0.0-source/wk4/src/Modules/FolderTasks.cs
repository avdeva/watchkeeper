// Folder tasks: move, copy or delete the files of a folder that match, on a schedule.
//
// A file is only touched when it matches the patterns, is old enough, has not changed for "settle" seconds (still being
// recorded) and no other program has it open for writing. Moving to another drive copies to a temporary name next to the
// destination, checks the size, renames it, and only then deletes the original. Tasks run one after another on one
// worker thread, in the order of the list.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Watchkeeper
{
    public class FolderTaskStatus
    {
        public string Id = "";
        public string Name = "";
        public bool Enabled;
        /// <summary>off | idle | queued | running | error</summary>
        public string State = "off";
        public string Text = "";
        public double LastRun;
        public string LastResult = "";
        public double NextRun;
    }

    public class FileStep
    {
        public string Source = "";
        public string Target = "";
        /// <summary>move | copy | delete | skip</summary>
        public string Action = "";
        public string Note = "";
    }

    public class TaskResult
    {
        public int Moved, Copied, Deleted, Skipped, Failed, Matched;
        public long Bytes;
        public List<FileStep> Steps = new List<FileStep>();
        public List<string> Errors = new List<string>();
        /// <summary>The task could not run at all (folder missing, ...).</summary>
        public string Fatal;

        public string Summary()
        {
            if (Fatal != null) return "Failed: " + Fatal;
            List<string> parts = new List<string>();
            if (Moved > 0) parts.Add(Moved + " moved");
            if (Copied > 0) parts.Add(Copied + " copied");
            if (Deleted > 0) parts.Add(Deleted + " deleted");
            if (Skipped > 0) parts.Add(Skipped + " left for now");
            if (Failed > 0) parts.Add(Failed + " failed");
            if (parts.Count == 0) return "Nothing to do";
            return string.Join(", ", parts.ToArray()) + (Bytes > 0 ? " (" + Util.FmtSize(Bytes) + ")" : "");
        }
    }

    public class FolderTasks
    {
        readonly Func<Settings> settings;
        readonly Logger log;
        readonly IAlertSink alerts;
        readonly StateStore state;
        readonly object lk = new object();
        readonly Queue<string> queue = new Queue<string>();
        readonly Dictionary<string, FolderTaskStatus> status = new Dictionary<string, FolderTaskStatus>();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly Dictionary<string, double> firstSeen = new Dictionary<string, double>();
        volatile bool stopping;
        Thread worker;
        string running;

        /// <summary>Tests: treat every move as a move to another drive.</summary>
        public static bool ForceCopyMove;

        public FolderTasks(Func<Settings> settings, Logger log, IAlertSink alerts, StateStore state)
        {
            this.settings = settings;
            this.log = log;
            this.alerts = alerts;
            this.state = state;
        }

        public void Start()
        {
            worker = new Thread(Work);
            worker.IsBackground = true;
            worker.Name = "folder-tasks";
            worker.Start();
        }

        public void Stop()
        {
            stopping = true;
            wake.Set();
            if (worker != null) worker.Join(10000);
        }

        // ---------------------------------------------------------------- schedule
        /// <summary>When the task is due next (epoch), or 0 for never (manual).</summary>
        public static double NextRun(FolderTask t, double lastRun, DateTime now)
        {
            if (t.Run == "manual") return 0;
            if (t.Run == "every") return lastRun <= 0 ? Util.ToEpoch(now) : lastRun + t.IntervalMinutes * 60;
            TimeSpan at = Util.ParseTime(t.At) ?? new TimeSpan(3, 0, 0);
            DateTime today = now.Date + at;
            DateTime last = lastRun > 0 ? Util.FromEpoch(lastRun) : now;
            return Util.ToEpoch(last < today ? today : today.AddDays(1));      // a time missed while the service was stopped runs at once
        }

        public void Tick(DateTime now)
        {
            Settings cfg = settings();
            double n = Util.ToEpoch(now);
            lock (lk)
            {
                foreach (FolderTask t in cfg.Folders)
                {
                    FolderTaskStatus s = Status(t);
                    if (!t.Enabled) { if (s.State != "running" && s.State != "queued") { s.State = "off"; s.Text = "Switched off"; } s.NextRun = 0; continue; }
                    double last = state.Get("folder:" + t.Id);
                    s.LastRun = last;
                    double anchor;
                    if (last <= 0 && t.Run == "daily")
                    {
                        // never ran: wait for the time instead of running at the first start
                        if (!firstSeen.TryGetValue(t.Id, out anchor)) { anchor = n; firstSeen[t.Id] = n; }
                        last = anchor;
                    }
                    s.NextRun = NextRun(t, last, now);
                    if (s.State == "off") { s.State = "idle"; s.Text = "Waiting"; }
                    if (s.NextRun > 0 && n >= s.NextRun && s.State != "running" && s.State != "queued") Enqueue(t.Id, s);
                }
            }
        }

        FolderTaskStatus Status(FolderTask t)
        {
            FolderTaskStatus s;
            if (!status.TryGetValue(t.Id, out s)) { s = new FolderTaskStatus(); s.Id = t.Id; status[t.Id] = s; }
            s.Name = t.Name;
            s.Enabled = t.Enabled;
            return s;
        }

        void Enqueue(string id, FolderTaskStatus s)
        {
            if (queue.Contains(id) || running == id) return;
            queue.Enqueue(id);
            s.State = "queued";
            s.Text = "Waiting for its turn";
            wake.Set();
        }

        /// <summary>Run a task now (manager), switched on or not.</summary>
        public bool RunNow(string id)
        {
            Settings cfg = settings();
            FolderTask t = cfg.Folders.Find(delegate(FolderTask x) { return x.Id == id; });
            if (t == null) return false;
            lock (lk) Enqueue(id, Status(t));
            return true;
        }

        void Work()
        {
            while (!stopping)
            {
                string id = null;
                lock (lk) { if (queue.Count > 0) { id = queue.Dequeue(); running = id; } }
                if (id == null) { wake.WaitOne(1000); continue; }
                try { RunById(id); }
                catch (Exception ex) { log.Error("Folder task: " + ex.Message); }
                finally { lock (lk) running = null; }
            }
        }

        /// <summary>Tests: run the queued tasks on this thread.</summary>
        public void RunQueued()
        {
            while (true)
            {
                string id;
                lock (lk) { if (queue.Count == 0) return; id = queue.Dequeue(); running = id; }
                try { RunById(id); } finally { lock (lk) running = null; }
            }
        }

        void RunById(string id)
        {
            FolderTask t = settings().Folders.Find(delegate(FolderTask x) { return x.Id == id; });
            if (t == null) return;
            FolderTaskStatus s;
            lock (lk) { s = Status(t); s.State = "running"; s.Text = "Running"; }
            double started = Util.Now();
            TaskResult r;
            try { r = Run(t, false, log, delegate { return stopping; }); }
            catch (Exception ex) { r = new TaskResult(); r.Fatal = "unexpected problem: " + ex.Message; }
            state.Set("folder:" + t.Id, started);
            lock (lk)
            {
                s.LastRun = started;
                s.LastResult = r.Summary();
                s.State = r.Fatal != null ? "error" : t.Enabled ? "idle" : "off";
                s.Text = r.Fatal != null ? r.Fatal : "Last run " + Util.FromEpoch(started).ToString("HH:mm:ss", Util.Inv) + ": " + r.Summary();
            }
            string w = "Folder task '" + t.Name + "'";
            if (r.Fatal != null)
            {
                log.Error(w + ": " + r.Fatal);
                alerts.Raise("folders", "folders:" + t.Id + ":run", "error", w + " cannot run", r.Fatal);
                return;
            }
            alerts.Recover("folders:" + t.Id + ":run", w + " runs again", w + " runs again: " + r.Summary());
            if (r.Moved + r.Copied + r.Deleted + r.Failed > 0) log.Info(w + ": " + r.Summary() + ".");
            if (r.Failed > 0)
                alerts.Raise("folders", "folders:" + t.Id + ":files", "warning", w + ": " + r.Failed + " file(s) failed",
                             r.Summary() + ".\n" + string.Join("\n", r.Errors.GetRange(0, Math.Min(10, r.Errors.Count)).ToArray()) +
                             (r.Errors.Count > 10 ? "\n(and " + (r.Errors.Count - 10) + " more; see the log)" : ""));
            else alerts.Recover("folders:" + t.Id + ":files", w + ": files handled again", w + ": " + r.Summary() + ".");
        }

        public List<FolderTaskStatus> Snapshot()
        {
            Settings cfg = settings();
            List<FolderTaskStatus> o = new List<FolderTaskStatus>();
            lock (lk)
            {
                foreach (FolderTask t in cfg.Folders)
                {
                    FolderTaskStatus s = Status(t);
                    FolderTaskStatus c = new FolderTaskStatus();
                    Json.FillFrom(c, Json.ToJson(s));
                    o.Add(c);
                }
            }
            return o;
        }

        // ---------------------------------------------------------------- one run
        static double AgeLimitSeconds(FolderTask t)
        {
            double mult = t.OlderThanUnit == "minutes" ? 60 : t.OlderThanUnit == "hours" ? 3600 : 86400;
            return t.OlderThan * mult;
        }

        static void Collect(string dir, bool recurse, List<FileInfo> into, List<string> errors)
        {
            DirectoryInfo d = new DirectoryInfo(dir);
            try { into.AddRange(d.GetFiles()); }
            catch (Exception ex) { errors.Add(dir + ": " + ex.Message); return; }
            if (!recurse) return;
            DirectoryInfo[] subs;
            try { subs = d.GetDirectories(); }
            catch (Exception ex) { errors.Add(dir + ": " + ex.Message); return; }
            foreach (DirectoryInfo s in subs)
            {
                if ((s.Attributes & FileAttributes.ReparsePoint) != 0) continue;     // junctions and links are not followed
                Collect(s.FullName, true, into, errors);
            }
        }

        /// <summary>No other program has the file open for writing.</summary>
        static bool Free(string path)
        {
            try
            {
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) return true;
            }
            catch (Exception) { return false; }
        }

        static string Relative(string root, string dir)
        {
            string r = Path.GetFullPath(root).TrimEnd('\\', '/');
            string d = Path.GetFullPath(dir).TrimEnd('\\', '/');
            if (d.Length <= r.Length) return "";
            return d.Substring(r.Length + 1);
        }

        /// <summary>Run the task once (preview: only say what would happen). cancelled is checked between files.</summary>
        public static TaskResult Run(FolderTask t, bool preview, Logger log, Func<bool> cancelled)
        {
            TaskResult r = new TaskResult();
            string src = Util.NormPath(t.Source);
            if (src.Length == 0) { r.Fatal = "no folder is set"; return r; }
            if (!Directory.Exists(src)) { r.Fatal = "the folder " + src + " does not exist or cannot be reached"; return r; }
            string dst = Util.NormPath(t.Destination);
            if (t.Action != "delete")
            {
                if (dst.Length == 0) { r.Fatal = "no destination folder is set"; return r; }
                if (Util.SamePath(src, dst)) { r.Fatal = "the destination is the folder itself"; return r; }
                if (!preview)
                {
                    try { Directory.CreateDirectory(dst); }
                    catch (Exception ex) { r.Fatal = "the destination " + dst + " cannot be created: " + ex.Message; return r; }
                }
            }

            List<FileInfo> files = new List<FileInfo>();
            List<string> listErrors = new List<string>();
            Collect(src, t.Subfolders, files, listErrors);
            foreach (string e in listErrors) { r.Errors.Add("cannot read " + e); r.Failed++; }
            List<string> include = Util.Patterns(t.Include), exclude = Util.Patterns(t.Exclude);
            DateTime now = DateTime.Now;
            double minAge = AgeLimitSeconds(t);
            List<FileInfo> matched = new List<FileInfo>();
            foreach (FileInfo f in files)
            {
                if (f.Name.IndexOf(".watchkeeper-partial", StringComparison.OrdinalIgnoreCase) >= 0) continue;     // being written by Watchkeeper
                if (!Util.MatchesAny(f.Name, include, true) || Util.MatchesAny(f.Name, exclude, false)) continue;
                if (dst.Length > 0 && t.Action != "delete" && Util.IsInside(f.DirectoryName, dst)) continue;    // never what this task already put there
                double age;
                try { age = (now - f.LastWriteTime).TotalSeconds; } catch (Exception) { continue; }
                if (minAge > 0 && age < minAge) continue;
                matched.Add(f);
            }
            matched.Sort(delegate(FileInfo a, FileInfo b) { return b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc); });   // newest first
            HashSet<string> written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);       // targets of this run
            r.Matched = matched.Count;
            for (int i = 0; i < matched.Count; i++)
            {
                if (cancelled != null && cancelled()) break;
                FileInfo f = matched[i];
                FileStep step = new FileStep();
                step.Source = f.FullName;
                r.Steps.Add(step);
                if (i < t.KeepNewest) { step.Action = "skip"; step.Note = "one of the newest " + t.KeepNewest; continue; }
                double age = (now - f.LastWriteTime).TotalSeconds;
                if (age < t.SettleSeconds) { step.Action = "skip"; step.Note = "changed " + (int)age + "s ago (still being written?)"; r.Skipped++; continue; }
                if (t.Action == "delete")
                {
                    step.Action = "delete";
                    if (preview) { r.Deleted++; continue; }
                    if (!Free(f.FullName)) { step.Action = "skip"; step.Note = "in use by another program"; r.Skipped++; continue; }
                    try
                    {
                        long size = f.Length;
                        if ((f.Attributes & FileAttributes.ReadOnly) != 0) f.Attributes &= ~FileAttributes.ReadOnly;
                        f.Delete();
                        r.Deleted++;
                        r.Bytes += size;
                        if (log != null) log.Info("Deleted " + f.FullName);
                    }
                    catch (Exception ex) { r.Failed++; r.Errors.Add(f.FullName + ": " + ex.Message); step.Note = ex.Message; }
                    continue;
                }

                string rel = t.Subfolders && t.KeepStructure ? Relative(src, f.DirectoryName) : "";
                string targetDir = rel.Length > 0 ? Path.Combine(dst, rel) : dst;
                string target = Path.Combine(targetDir, f.Name);
                if (File.Exists(target) && !written.Contains(target) && SameFile(f, target, t.Action == "move"))
                {
                    // already there (copied by an earlier run): a copy is done; a move only has to remove the original
                    step.Target = target;
                    if (t.Action == "copy") { step.Action = "skip"; step.Note = "already copied"; r.Skipped++; continue; }
                    step.Action = "move";
                    step.Note = "already at the destination; the original is removed";
                    if (preview) { r.Moved++; continue; }
                    if (!Free(f.FullName)) { step.Action = "skip"; step.Note = "in use by another program"; r.Skipped++; continue; }
                    try
                    {
                        if ((f.Attributes & FileAttributes.ReadOnly) != 0) f.Attributes &= ~FileAttributes.ReadOnly;
                        f.Delete();
                        r.Moved++;
                        if (log != null) log.Info("Removed " + f.FullName + " (already at " + target + ")");
                    }
                    catch (Exception ex) { r.Failed++; r.Errors.Add(f.FullName + ": already at the destination, but the original could not be deleted: " + ex.Message); step.Note = ex.Message; }
                    continue;
                }
                if (File.Exists(target) || written.Contains(target))
                {
                    if (t.IfExists == "skip") { step.Action = "skip"; step.Target = target; step.Note = "a different file of that name is at the destination"; r.Skipped++; continue; }
                    // never overwrite a file this run put there (two sub-folders with the same file name)
                    if (t.IfExists == "rename" || written.Contains(target)) target = FreeName(target, written);
                }
                written.Add(target);
                step.Action = t.Action;
                step.Target = target;
                if (preview) { if (t.Action == "move") r.Moved++; else r.Copied++; continue; }
                if (!Free(f.FullName)) { step.Action = "skip"; step.Note = "in use by another program"; r.Skipped++; continue; }
                try
                {
                    Directory.CreateDirectory(targetDir);
                    long size = f.Length;
                    Transfer(f, target, t.Action == "move");
                    if (t.Action == "move") r.Moved++; else r.Copied++;
                    r.Bytes += size;
                    if (log != null) log.Info((t.Action == "move" ? "Moved " : "Copied ") + f.FullName + " -> " + target);
                }
                catch (Exception ex) { r.Failed++; r.Errors.Add(f.FullName + ": " + ex.Message); step.Note = ex.Message; }
            }
            if (!preview && t.RemoveEmptyFolders && t.Subfolders) RemoveEmpty(src, src, log);
            return r;
        }

        /// <summary>name.ext -> name (2).ext, (3), ... until the name is free.</summary>
        public static string FreeName(string target) { return FreeName(target, null); }

        static string FreeName(string target, HashSet<string> taken)
        {
            string dir = Path.GetDirectoryName(target), stem = Path.GetFileNameWithoutExtension(target), ext = Path.GetExtension(target);
            for (int i = 2; ; i++)
            {
                string c = Path.Combine(dir, stem + " (" + i + ")" + ext);
                if (!File.Exists(c) && (taken == null || !taken.Contains(c))) return c;
            }
        }

        /// <summary>The file at target is a copy of f: same size, same date modified (within 2 seconds) and the same
        /// bytes at the start and the end - or, before an original is deleted (full), the same bytes throughout.</summary>
        static bool SameFile(FileInfo f, string target, bool full)
        {
            try
            {
                FileInfo t = new FileInfo(target);
                if (t.Length != f.Length || Math.Abs((t.LastWriteTimeUtc - f.LastWriteTimeUtc).TotalSeconds) > 2) return false;
                using (FileStream a = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (FileStream b = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    const int chunk = 1 << 16;
                    byte[] x = new byte[chunk], y = new byte[chunk];
                    long len = a.Length;
                    List<long> starts = new List<long>();
                    if (full) for (long p = 0; p < len; p += chunk) starts.Add(p);
                    else { starts.Add(0); if (len > chunk) starts.Add(len - chunk); }
                    foreach (long start in starts)
                    {
                        a.Position = b.Position = start;
                        int n = (int)Math.Min(chunk, len - start);
                        if (ReadFully(a, x, n) != n || ReadFully(b, y, n) != n) return false;
                        for (int i = 0; i < n; i++) if (x[i] != y[i]) return false;
                    }
                }
                return true;
            }
            catch (Exception) { return false; }
        }

        static int ReadFully(Stream s, byte[] buf, int n)
        {
            int got = 0;
            while (got < n) { int r = s.Read(buf, got, n - got); if (r <= 0) break; got += r; }
            return got;
        }

        static bool SameVolume(string a, string b)
        {
            if (ForceCopyMove) return false;
            try { return string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { return false; }
        }

        static void Transfer(FileInfo f, string target, bool move)
        {
            DateTime written = f.LastWriteTimeUtc;
            if (move && SameVolume(f.FullName, target))
            {
                if (File.Exists(target)) File.Replace(f.FullName, target, null);       // overwrite was chosen: one step
                else File.Move(f.FullName, target);
                return;
            }
            string partial = target + ".watchkeeper-partial";
            try
            {
                File.Copy(f.FullName, partial, true);
                long want = f.Length, got = new FileInfo(partial).Length;
                if (want != got) throw new IOException("the copy has " + got + " bytes instead of " + want);
                File.SetLastWriteTimeUtc(partial, written);
                if (File.Exists(target)) File.Replace(partial, target, null);           // overwrite: the old file is only gone once the new one is in place
                else File.Move(partial, target);
            }
            catch
            {
                try { if (File.Exists(partial)) File.Delete(partial); } catch (Exception) { }
                throw;
            }
            if (move)
            {
                try
                {
                    if ((f.Attributes & FileAttributes.ReadOnly) != 0) f.Attributes &= ~FileAttributes.ReadOnly;
                    f.Delete();
                }
                catch (Exception ex)
                {
                    throw new IOException("copied to " + target + ", but the original could not be deleted (" + ex.Message + "); both copies exist");
                }
            }
        }

        static void RemoveEmpty(string dir, string root, Logger log)
        {
            string[] subs;
            try { subs = Directory.GetDirectories(dir); } catch (Exception) { return; }
            foreach (string s in subs)
            {
                try { if ((new DirectoryInfo(s).Attributes & FileAttributes.ReparsePoint) != 0) continue; } catch (Exception) { continue; }
                RemoveEmpty(s, root, log);
            }
            if (Util.SamePath(dir, root)) return;
            try
            {
                if (Directory.GetFileSystemEntries(dir).Length == 0)
                {
                    Directory.Delete(dir);
                    if (log != null) log.Info("Removed empty folder " + dir);
                }
            }
            catch (Exception) { }
        }
    }
}
