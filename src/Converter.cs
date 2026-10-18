// Vertical (portrait) conversion watcher.
//
// Differences from the script, all deliberate:
//   * the source file is only deleted after ffmpeg exits 0 AND the output passes
//     verification (the script deleted the source whenever any output file existed,
//     which loses footage when an encode dies half way);
//   * output is written to a .partial.mp4 file and renamed when verified;
//   * failures are retried with back-off instead of every 5 seconds forever;
//   * everything (GPU, bitrate, resolution, which files) comes from the config.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Watchkeeper
{
    public class IngestRow
    {
        public string Name = "";
        public string Path = "";
        public long Size;
        public double Mtime;
        public string Action = "convert";    // convert | converting | exists | ignored | backoff | failed | dryrun
        public string Prefix = "";
        public string OutName = "";
        public string OutStem = "";
        public string Note = "";
        public IngestRow Copy() { return (IngestRow)MemberwiseClone(); }
    }

    public class CurrentJob
    {
        public string Name = "";
        public string Path = "";
        public string Stage = "";
        public double Percent;
        public string Speed = "";
        public string Fps = "";
        public double EtaSeconds = -1;
        public double OutSeconds;
        public double Started;
        public string Backend = "";
        public string OutName = "";
        public CurrentJob Copy() { return (CurrentJob)MemberwiseClone(); }
    }

    public class HistoryItem
    {
        public string Name = "";
        public string Status = "";           // done | failed | aborted
        public string Error = "";
        public double Ended;
        public double Seconds;
        public string Backend = "";
        public string OutName = "";
        public long OutSize;
        public bool SourceDeleted;
        public string Detail = "";          // command line and ffmpeg output of every attempt (for failures)
    }

    public class FailureItem
    {
        public string Name = "";
        public string Path = "";
        public int Count;
        public string Error = "";
        public double NextTry;
    }

    public class ConverterStatus
    {
        public bool Running;
        public bool Stopping;
        public CurrentJob Current;
        public List<IngestRow> Ingest = new List<IngestRow>();
        public List<HistoryItem> History = new List<HistoryItem>();    // newest first
        public List<FailureItem> Failures = new List<FailureItem>();
    }

    public class CommandPreview
    {
        public bool Ok;
        public string Error = "";
        public string Command = "";
        public List<string> Notes = new List<string>();
        public string Backend = "";
        public string Adapter = "";
    }

    public class EncoderTestResult
    {
        public bool Ok;
        public string Backend = "";
        public string Label = "";
        public string Adapter = "";
        public string Command = "";
        public string Error = "";
        public string Output = "";
        public double Seconds;
        /// <summary>Everything that went wrong, attempt by attempt, with commands and ffmpeg's own words.</summary>
        public string Log = "";
        /// <summary>Extra advice, for example when only the software-decoding attempt worked.</summary>
        public string Note = "";
    }

    public class Converter
    {
        public const string PartialSuffix = ".partial.mp4";
        static readonly Regex DatePart = new Regex(@"^[^-]+ - [^-]+ - ");

        class FailState
        {
            public int Count;
            public string Error = "";
            public long Size;
            public long MtimeTicks;
            public double NextTry;
        }

        /// <summary>Milliseconds between size checks while a file is still being copied.</summary>
        public int StableWaitMs = 2000;
        /// <summary>Seconds of back-off added per failed attempt.</summary>
        public double BackoffSeconds = 60;

        readonly ConfigStore cfgs;
        readonly SourceLogger log;
        readonly AdapterCache adapters;
        readonly ILauncher launcher;
        readonly ISessions sessions;
        readonly object lk = new object();
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        Thread thread;
        volatile bool abort;
        IRunning proc;

        /// <summary>(kind, key, title, message): raised when a file is given up on.</summary>
        public Action<string, string, string, string> Alert;
        CurrentJob current;
        List<IngestRow> ingest = new List<IngestRow>();
        readonly List<HistoryItem> history = new List<HistoryItem>();
        readonly Dictionary<string, FailState> fail = new Dictionary<string, FailState>();
        readonly HashSet<string> logged = new HashSet<string>();
        readonly HashSet<string> dryDone = new HashSet<string>();
        string lastCommand = "";      // worker thread only
        string lastOutput = "";

        public Converter(ConfigStore cfgs, LogHub hub, AdapterCache adapters)
            : this(cfgs, hub, adapters, new SystemLauncher(), new SystemSessions()) { }

        public Converter(ConfigStore cfgs, LogHub hub, AdapterCache adapters, ILauncher launcher, ISessions sessions)
        {
            this.cfgs = cfgs;
            this.log = hub.Logger("vertical");
            this.adapters = adapters;
            this.launcher = launcher;
            this.sessions = sessions;
        }

        bool Stopped { get { return stop.WaitOne(0); } }

        // ------------------------------------------------------------------ control
        /// <summary>Start the watcher, or cancel a pending stop if it is still winding down.</summary>
        public void Start()
        {
            lock (lk)
            {
                if (thread != null)
                {
                    stop.Reset();
                    return;
                }
                stop.Reset();
                abort = false;
                thread = new Thread(Loop);
                thread.Name = "vertical-watcher";
                thread.IsBackground = true;
                thread.Start();
            }
        }

        public void Stop(bool abortCurrent, int waitMs)
        {
            stop.Set();
            if (abortCurrent) AbortCurrent();
            Thread t = thread;
            if (waitMs > 0 && t != null && t.IsAlive && t != Thread.CurrentThread) t.Join(waitMs);
        }

        public void Stop() { Stop(false, 0); }

        public void AbortCurrent()
        {
            abort = true;
            IRunning p;
            lock (lk) { p = proc; }
            if (p != null)
            {
                try { p.Kill(); } catch (Exception) { }
            }
        }

        public bool Alive { get { return thread != null; } }

        public int RetryFailed()
        {
            int n;
            lock (lk)
            {
                n = fail.Count;
                fail.Clear();
            }
            if (n > 0) log.Info("Cleared " + n + " failed file(s); they will be retried.");
            return n;
        }

        public ConverterStatus Status()
        {
            ConverterStatus s = new ConverterStatus();
            bool stopped = Stopped;
            lock (lk)
            {
                bool alive = thread != null;
                s.Running = alive && !stopped;
                s.Stopping = alive && stopped;
                string curPath = current != null ? current.Path : null;
                s.Current = current != null ? current.Copy() : null;
                foreach (IngestRow r in ingest)
                {
                    IngestRow c = r.Copy();
                    if (curPath != null && c.Path == curPath) c.Action = "converting";
                    s.Ingest.Add(c);
                }
                for (int i = history.Count - 1; i >= 0; i--) s.History.Add(history[i]);
                foreach (KeyValuePair<string, FailState> kv in fail)
                {
                    FailureItem f = new FailureItem();
                    f.Name = System.IO.Path.GetFileName(kv.Key);
                    f.Path = kv.Key;
                    f.Count = kv.Value.Count;
                    f.Error = kv.Value.Error;
                    f.NextTry = kv.Value.NextTry;
                    s.Failures.Add(f);
                }
            }
            return s;
        }

        // --------------------------------------------------------------------- loop
        void Loop()
        {
            log.Info("Watcher started.");
            try
            {
                CleanPartials(cfgs.Get());
                while (true)
                {
                    lock (lk)
                    {
                        if (Stopped)    // decide to exit atomically with Start()
                        {
                            thread = null;
                            break;
                        }
                    }
                    AppConfig cfg = cfgs.Get();
                    try
                    {
                        List<IngestRow> todo = Scan(cfg);
                        IngestRow job = todo.Find(delegate(IngestRow r) { return r.Action == "convert"; });
                        if (job != null)
                        {
                            ProcessJob(cfg, job);
                            continue;
                        }
                    }
                    catch (Exception ex)    // keep the watcher alive no matter what
                    {
                        log.Error("Watcher error: " + ex.Message);
                        stop.WaitOne(5000);
                        continue;
                    }
                    stop.WaitOne(cfg.Vertical.PollSeconds * 1000);
                }
            }
            finally
            {
                lock (lk)
                {
                    current = null;
                    if (thread == Thread.CurrentThread) thread = null;
                }
                log.Info("Watcher stopped.");
            }
        }

        void CleanPartials(AppConfig cfg)
        {
            string outDir = Util.NormPath(cfg.Vertical.OutDir);
            if (Util.ProtectedReason(outDir) != null || !Directory.Exists(outDir)) return;
            try
            {
                using (SafeDir d = SafeDir.Open(outDir, false))
                    foreach (FileInfo fi in new DirectoryInfo(outDir).GetFiles())
                    {
                        if (!fi.Name.ToLowerInvariant().EndsWith(PartialSuffix)) continue;
                        try
                        {
                            SafeFs.DeleteEntry(d, fi.Name);      // removes the entry itself, never what a link points at
                            log.Info("Removed leftover partial file: " + fi.Name);
                        }
                        catch (Exception) { }
                    }
            }
            catch (Exception ex) { log.Warn("Output folder refused: " + ex.Message); }
        }

        // --------------------------------------------------------------------- scan
        /// <summary>Which output prefix applies to this file? Null when the file must be left alone.
        /// The first source (in list order) whose text is contained in the filename decides; a disabled
        /// source blocks the file instead of falling through.</summary>
        public static string DecidePrefix(string name, VerticalCfg v, out string why)
        {
            why = null;
            string low = name.ToLowerInvariant();
            foreach (SourceRule src in v.Sources)
            {
                if (src.Match.Length > 0 && low.Contains(src.Match.ToLowerInvariant()))
                {
                    if (src.Enabled) return Util.CleanFilenamePart(src.Prefix);
                    why = "source '" + src.Name + "' is switched off";
                    return null;
                }
            }
            if (v.Other.Enabled) return Util.CleanFilenamePart(v.Other.Prefix);
            why = "matches no source and 'all other files' is switched off";
            return null;
        }

        /// <summary>'MultiCorder7 - Cam1 - 23 July 2026 - 08-29-03 AM' becomes 'Vertical PGM - 23 July 2026 - 08-29-03 AM'.</summary>
        public static string OutputStem(string basename, string prefix)
        {
            return prefix + " - " + DatePart.Replace(basename, "", 1);
        }

        internal List<IngestRow> Scan(AppConfig cfg)
        {
            VerticalCfg v = cfg.Vertical;
            string watch = Util.NormPath(v.WatchDir);
            string outDir = Util.NormPath(v.OutDir);
            string reason = Util.ProtectedReason(watch) ?? Util.ProtectedReason(outDir);
            if (reason != null)
            {
                log.Error("Folder refused (" + reason + "); watcher idle.");
                SetIngest(new List<IngestRow>());
                return new List<IngestRow>();
            }
            if (!Directory.Exists(watch))
            {
                if (logged.Add("nowatch")) log.Warn("Ingest folder does not exist: " + watch);
                SetIngest(new List<IngestRow>());
                return new List<IngestRow>();
            }
            logged.Remove("nowatch");
            if (!cfg.General.DryRun) dryDone.Clear();

            double now = Util.Now();
            List<IngestRow> rows = new List<IngestRow>();
            List<FileInfo> entries = new List<FileInfo>();
            try
            {
                foreach (FileInfo fi in new DirectoryInfo(watch).GetFiles())
                {
                    if (!v.Extensions.Contains(fi.Extension.ToLowerInvariant())) continue;
                    if (fi.Name.ToLowerInvariant().EndsWith(PartialSuffix)) continue;
                    if ((fi.Attributes & FileAttributes.ReparsePoint) != 0) continue;      // links are never converted
                    entries.Add(fi);
                }
            }
            catch (Exception ex)
            {
                log.Error("Cannot read ingest folder: " + ex.Message);
                return rows;
            }
            entries.Sort(delegate(FileInfo a, FileInfo b)
            {
                int c = a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc);
                return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            foreach (FileInfo fi in entries)
            {
                long size, mtimeTicks;
                try { size = fi.Length; mtimeTicks = fi.LastWriteTimeUtc.Ticks; }
                catch (Exception) { continue; }    // vanished between listing and stat
                string basename = System.IO.Path.GetFileNameWithoutExtension(fi.Name);
                string why;
                string prefix = DecidePrefix(fi.Name, v, out why);
                IngestRow row = new IngestRow();
                row.Name = fi.Name;
                row.Path = fi.FullName;
                row.Size = size;
                row.Mtime = Util.ToEpoch(fi.LastWriteTimeUtc);
                row.Prefix = prefix ?? "";
                if (prefix == null)
                {
                    row.Action = "ignored";
                    row.Note = why;
                    rows.Add(row);
                    continue;
                }
                string stem = OutputStem(basename, prefix);
                row.OutStem = stem;
                row.OutName = stem + ".mp4";
                if (File.Exists(System.IO.Path.Combine(outDir, row.OutName)))
                {
                    row.Action = "exists";
                    row.Note = "output already exists; source left untouched";
                    if (logged.Add(fi.FullName)) log.Info("Skipped (output exists): " + fi.Name);
                }
                else if (dryDone.Contains(fi.FullName))
                {
                    row.Action = "dryrun";
                    row.Note = "dry-run: command logged, nothing converted";
                }
                else
                {
                    FailState f;
                    lock (lk)
                    {
                        if (fail.TryGetValue(fi.FullName, out f) && (f.Size != size || f.MtimeTicks != mtimeTicks))
                        {
                            fail.Remove(fi.FullName);    // file changed: forget old failures
                            f = null;
                        }
                    }
                    if (f != null)
                    {
                        if (f.Count > v.MaxRetries)
                        {
                            row.Action = "failed";
                            row.Note = "gave up after " + f.Count + " attempts: " + f.Error;
                        }
                        else if (now < f.NextTry)
                        {
                            row.Action = "backoff";
                            row.Note = "retry in " + ((long)(f.NextTry - now)).ToString(Util.Inv) + "s: " + f.Error;
                        }
                    }
                }
                lock (lk)
                {
                    if (current != null && current.Path == fi.FullName)
                    {
                        row.Action = "converting";
                        row.Note = "";
                    }
                }
                rows.Add(row);
            }
            SetIngest(rows);
            return rows;
        }

        void SetIngest(List<IngestRow> rows)
        {
            List<IngestRow> copy = new List<IngestRow>();
            foreach (IngestRow r in rows) copy.Add(r.Copy());
            lock (lk) { ingest = copy; }
        }

        // ------------------------------------------------------------------ process
        void AddHistory(HistoryItem item)
        {
            lock (lk)
            {
                history.Add(item);
                while (history.Count > 60) history.RemoveAt(0);
            }
        }

        void RecordFailure(string path, string error, double started, string backend)
        {
            RecordFailure(path, error, started, backend, "");
        }

        void RecordFailure(string path, string error, double started, string backend, string detail)
        {
            bool givenUp = false;
            long size = 0, ticks = 0;
            try
            {
                FileInfo fi = new FileInfo(path);
                size = fi.Length;
                ticks = fi.LastWriteTimeUtc.Ticks;
            }
            catch (Exception) { }
            lock (lk)
            {
                FailState f;
                if (!fail.TryGetValue(path, out f)) f = new FailState();
                f.Count++;
                f.Error = error;
                f.Size = size;
                f.MtimeTicks = ticks;
                f.NextTry = Util.Now() + BackoffSeconds * f.Count;
                fail[path] = f;
                givenUp = f.Count > cfgs.Get().Vertical.MaxRetries;
            }
            HistoryItem h = new HistoryItem();
            h.Name = System.IO.Path.GetFileName(path);
            h.Status = "failed";
            h.Error = error;
            h.Ended = Util.Now();
            h.Seconds = Math.Round(Util.Now() - started, 1);
            h.Backend = backend;
            h.Detail = detail ?? "";
            AddHistory(h);
            log.Error("Failed: " + h.Name + ": " + error);
            if (h.Detail.Length > 0) log.Error("Details for " + h.Name + ":\n" + h.Detail);
            Action<string, string, string, string> a = Alert;
            if (givenUp && a != null)
            {
                try
                {
                    a("conversion_failure", "vertical:" + h.Name, "Vertical conversion failed: " + h.Name,
                      "Gave up converting " + h.Name + " after repeated failures. The original file was kept.\n\nError: " + error +
                      (h.Detail.Length > 0 ? "\n\n" + (h.Detail.Length > 4000 ? h.Detail.Substring(0, 4000) + "..." : h.Detail) : ""));
                }
                catch (Exception) { }
            }
        }

        /// <summary>Wait until the file size stops changing (same idea as the script).</summary>
        internal string WaitStable(string path)
        {
            long prev = -1;
            while (!Stopped)
            {
                long size;
                try { size = new FileInfo(path).Length; }
                catch (Exception) { return "gone"; }
                if (size == prev && !FileOps.InUse(path)) return size > 0 ? "ok" : "empty";
                prev = size;
                stop.WaitOne(StableWaitMs);
            }
            return "stopped";
        }

        void ProcessJob(AppConfig cfg, IngestRow job)
        {
            VerticalCfg v = cfg.Vertical;
            GeneralCfg g = cfg.General;
            string src = job.Path, name = job.Name;
            string outDir = Util.NormPath(v.OutDir);
            string final = System.IO.Path.Combine(outDir, job.OutName);
            string partial = System.IO.Path.Combine(outDir, job.OutStem + PartialSuffix);
            SafeDir outSafe = null, srcSafe = null;
            SafeFs.HeldFile held = null;
            double started = Util.Now();
            abort = false;
            lock (lk)
            {
                current = new CurrentJob();
                current.Name = name;
                current.Path = src;
                current.Stage = "waiting for file to finish copying";
                current.Started = started;
                current.OutName = job.OutName;
            }
            try
            {
                string state = WaitStable(src);
                if (state == "stopped") return;
                if (state == "gone")
                {
                    log.Warn("File disappeared before conversion: " + name);
                    return;
                }
                if (state == "empty")
                {
                    RecordFailure(src, "file is empty", started, "");
                    return;
                }

                // the ingest and output folders (and every folder above them) checked to be real folders and held open
                try
                {
                    outSafe = SafeDir.Open(outDir, !g.DryRun);
                    srcSafe = SafeDir.Open(System.IO.Path.GetDirectoryName(src), false);
                }
                catch (Exception ex)
                {
                    RecordFailure(src, "folder refused: " + ex.Message, started, "");
                    return;
                }

                SetStage("probing");
                ProbeInfo info;
                try { info = Media.Probe(g.FfprobePath, src); }
                catch (ProbeError ex)
                {
                    RecordFailure(src, ex.Message, started, "");
                    return;
                }

                List<Adapter> found = adapters.Get();
                Resolved resolved = FfmpegBuilder.ResolveGpu(v.Gpu, found);
                List<Resolved> plan = Plan(v.Gpu, resolved, found, true);

                if (g.DryRun)
                {
                    BuildResult dry;
                    try { dry = FfmpegBuilder.Build(g.FfmpegPath, v, info, src, partial, resolved, true); }
                    catch (BuildError ex)
                    {
                        RecordFailure(src, ex.Message, started, "");
                        return;
                    }
                    log.Info("[dry-run] Would convert " + name + " -> " + job.OutName + " on " + Gpu.BackendLabel(resolved.Backend));
                    log.Info("[dry-run] " + dry.Text);
                    dryDone.Add(src);
                    return;
                }

                // a new output file that must not exist yet, held so it cannot be swapped for a link while ffmpeg writes it
                held = SafeFs.CreateHeld(outSafe, job.OutStem + PartialSuffix);
                held.AllowMedia();                // ffmpeg as LOCAL SERVICE may write this file and no other
                partial = held.Path;
                string error = "unknown error";
                string firstError = null;
                string label = "";
                bool ok = false;
                StringBuilder detail = new StringBuilder();
                for (int attempt = 0; attempt < plan.Count; attempt++)
                {
                    Resolved res = plan[attempt];
                    label = Label(res);
                    string adapterName = res.Adapter != null ? res.Adapter.Name : "";
                    lock (lk)
                    {
                        current.Stage = "encoding";
                        current.Backend = label + (adapterName.Length > 0 ? " - " + adapterName : "");
                        current.Percent = 0;
                        current.Speed = "";
                        current.Fps = "";
                        current.EtaSeconds = -1;
                    }
                    if (attempt > 0) log.Warn("Retrying " + name + " on " + label + " after the previous attempt failed.");
                    lastCommand = ""; lastOutput = "";
                    ok = Encode(g, v, info, src, partial, res, name, label, out error);
                    if (ok || error == "aborted") break;
                    if (attempt == 0) firstError = error;
                    detail.AppendLine("Attempt " + (attempt + 1) + " on " + label + " failed: " + error);
                    if (lastCommand.Length > 0) detail.AppendLine("Command: " + lastCommand);
                    if (lastOutput.Length > 0) detail.AppendLine(lastOutput);
                    detail.AppendLine();
                }
                if (!ok)
                {
                    held.Discard();
                    if (error == "aborted")
                    {
                        log.Warn("Aborted: " + name + " (source kept)");
                        HistoryItem h = new HistoryItem();
                        h.Name = name;
                        h.Status = "aborted";
                        h.Ended = Util.Now();
                        h.Seconds = Math.Round(Util.Now() - started, 1);
                        AddHistory(h);
                    }
                    else
                    {
                        // the first attempt's error is the informative one; later attempts are fallbacks
                        string shown = firstError ?? error;
                        if (plan.Count > 1) shown += " (all " + plan.Count + " attempts failed; see details)";
                        RecordFailure(src, shown, started, label, detail.ToString().TrimEnd());
                    }
                    return;
                }

                lock (lk) { current.Stage = "verifying"; current.Percent = 100; }
                string problem = VerifyOutput(g, v, partial, info);
                if (problem != null)
                {
                    held.Discard();
                    RecordFailure(src, "verification failed: " + problem, started, label);
                    return;
                }

                held.RevokeMedia();
                // the portrait file carries the recording's Date Modified (also what retention on the data drive goes by)
                DateTime srcModified = File.GetLastWriteTimeUtc(src);
                held.MoveTo(job.OutName);         // replaces an existing file of that name, never writing through a link
                held = null;
                KeepModified(outSafe, job.OutName, srcModified);
                long outSize = new FileInfo(final).Length;
                bool deleted = false;
                string movedTo = null;
                if (v.AfterSuccess == "delete") deleted = DeleteSource(srcSafe, name);
                else if (v.AfterSuccess == "move") movedTo = MoveSource(srcSafe, name, v.MoveOriginalTo);
                lock (lk) { fail.Remove(src); }
                HistoryItem done = new HistoryItem();
                done.Name = name;
                done.Status = "done";
                done.Ended = Util.Now();
                done.Seconds = Math.Round(Util.Now() - started, 1);
                done.Backend = label;
                done.OutName = job.OutName;
                done.OutSize = outSize;
                done.SourceDeleted = deleted || movedTo != null;
                if (movedTo != null) done.Detail = "Original moved to " + movedTo;
                AddHistory(done);
                log.Info("Completed: " + final);
            }
            finally
            {
                if (held != null) held.Discard();
                if (outSafe != null) outSafe.Dispose();
                if (srcSafe != null) srcSafe.Dispose();
                lock (lk)
                {
                    current = null;
                    proc = null;
                }
            }
        }

        void KeepModified(SafeDir dir, string name, DateTime utc)
        {
            if (utc.Year < 1980) return;      // source gone or unreadable: leave the output's own time
            try { SafeFs.SetModified(dir, name, utc); }
            catch (Exception ex) { log.Warn("Could not set Date Modified on " + name + ": " + ex.Message); }
        }

        void SetStage(string stage)
        {
            lock (lk) { if (current != null) current.Stage = stage; }
        }

        /// <summary>Moves the converted original off the ingest folder (cross-volume safe: copy, size check,
        /// delete). Returns the new path, or null when it stayed where it was.</summary>
        string MoveSource(SafeDir srcDir, string name, string folder)
        {
            string dir = Util.NormPath(folder ?? "");
            string reason = Util.ProtectedReason(dir);
            if (reason != null)
            {
                log.Warn("Converted, but the original " + name + " was not moved: '" + dir + "' refused (" + reason + ")");
                return null;
            }
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using (SafeDir dst = SafeDir.Open(dir, true))
                    {
                        string free = SafeFs.FreeName(dst, name);
                        SafeFs.Move(srcDir, name, dst, free, false);
                        string moved = dst.Combine(free);
                        log.Info("Original moved to " + moved);
                        return moved;
                    }
                }
                catch (Exception ex)
                {
                    if (attempt == 2)
                    {
                        log.Warn("Converted, but could not move the original " + name + " to " + dir + ": " + ex.Message);
                        return null;
                    }
                    Thread.Sleep(1000);
                }
            }
            return null;
        }

        bool DeleteSource(SafeDir srcDir, string name)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (!SafeFs.EntryExists(srcDir.Combine(name))) return true;
                    SafeFs.Delete(srcDir, name);     // through the file's own handle, never a link
                    return true;
                }
                catch (Exception ex)
                {
                    if (attempt == 2)
                    {
                        log.Warn("Converted, but could not delete source " + name + ": " + ex.Message);
                        return false;
                    }
                    Thread.Sleep(1000);
                }
            }
            return false;
        }

        /// <summary>Attempt order: GPU (hardware decode), GPU (software decode), the same in the signed-in
        /// user's session (when the GPU session setting allows), then the CPU (when fallback is on).</summary>
        public static List<Resolved> Plan(GpuCfg gpu, Resolved resolved, List<Adapter> found, bool includeCpu)
        {
            List<Resolved> gpuSteps = new List<Resolved>();
            gpuSteps.Add(resolved);
            if (resolved.Backend != "cpu" && gpu.HwDecode)
            {
                Resolved soft = resolved.Copy();
                soft.NoHwDecode = true;
                gpuSteps.Add(soft);
            }
            List<Resolved> plan = new List<Resolved>();
            bool hw = resolved.Backend != "cpu";
            if (!hw || gpu.Session != "user") plan.AddRange(gpuSteps);
            if (hw && (gpu.Session == "user_fallback" || gpu.Session == "user"))
            {
                foreach (Resolved r in gpuSteps)
                {
                    Resolved u = r.Copy();
                    u.InUserSession = true;
                    plan.Add(u);
                }
            }
            if (includeCpu && gpu.FallbackToCpu && hw)
            {
                GpuCfg cpuGpu = Json.Clone(gpu);
                cpuGpu.Backend = "cpu";
                plan.Add(FfmpegBuilder.ResolveGpu(cpuGpu, found));
            }
            return plan;
        }

        public static string Label(Resolved r)
        {
            string l = Gpu.BackendLabel(r.Backend);
            List<string> notes = new List<string>();
            if (r.NoHwDecode) notes.Add("software decoding");
            if (r.InUserSession) notes.Add("user session");
            return notes.Count == 0 ? l : l + " (" + string.Join(", ", notes.ToArray()) + ")";
        }

        void OnProgress(string line, double duration)
        {
            int eq = line.IndexOf('=');
            if (eq <= 0) return;
            string key = line.Substring(0, eq).Trim();
            string val = line.Substring(eq + 1).Trim();
            if (key == "out_time_us" || key == "out_time_ms")
            {
                long us;
                if (!long.TryParse(val, System.Globalization.NumberStyles.Integer, Util.Inv, out us)) return;
                double seconds = us / 1000000.0;
                double pct = duration > 0 ? Math.Min(99.9, seconds / duration * 100.0) : 0;
                lock (lk)
                {
                    if (current != null)
                    {
                        current.Percent = Math.Round(pct, 1);
                        current.OutSeconds = seconds;
                    }
                }
            }
            else if (key == "speed")
            {
                Match m = Regex.Match(val, @"^([0-9.]+)x");
                lock (lk)
                {
                    if (current == null) return;
                    current.Speed = val;
                    double sp;
                    if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, Util.Inv, out sp)
                        && sp > 0 && duration > 0 && current.OutSeconds > 0)
                        current.EtaSeconds = Math.Max(0, (duration - current.OutSeconds) / sp);
                }
            }
            else if (key == "fps")
            {
                lock (lk) { if (current != null) current.Fps = val; }
            }
        }

        bool Encode(GeneralCfg g, VerticalCfg v, ProbeInfo info, string src, string partial, Resolved resolved,
                    string name, string label, out string error)
        {
            BuildResult b;
            try { b = FfmpegBuilder.Build(g.FfmpegPath, v, info, src, partial, resolved, true); }
            catch (BuildError ex) { error = ex.Message; return false; }
            foreach (string n in b.Notes) log.Warn(name + ": " + n);
            log.Info("Processing " + name + " on " + label);
            log.Debug(b.Text);
            lastCommand = b.Text;

            double duration = info.Duration;
            double lastScan = Util.Now();
            FfmpegRunResult r = FfmpegExec.Run(launcher, sessions, g.FfmpegPath, b.Args.GetRange(1, b.Args.Count - 1),
                resolved.InUserSession, v.Gpu.Priority, 0,
                delegate(string line) { OnProgress(line, duration); },
                delegate
                {
                    if (Util.Now() - lastScan > 5.0)    // keep the ingest list fresh during long encodes
                    {
                        lastScan = Util.Now();
                        try { Scan(cfgs.Get()); } catch (Exception) { }
                    }
                    return abort;
                },
                delegate(IRunning p)
                {
                    lock (lk) { proc = p; }
                    if (abort) p.Kill();
                });
            lock (lk) { proc = null; }
            if (r.StartError != null)
            {
                error = r.StartError;
                return false;
            }
            if (abort || r.Aborted)
            {
                error = "aborted";
                return false;
            }
            if (r.ExitCode != 0)
            {
                lastOutput = r.Errors;
                string msg = MeaningfulTail(r.Errors, 6);
                if (msg.Length == 0) msg = "no output from ffmpeg";
                error = "ffmpeg exit code " + r.ExitCode.ToString(Util.Inv) + ": " + msg;
                return false;
            }
            error = "";
            return true;
        }

        string VerifyOutput(GeneralCfg g, VerticalCfg v, string outPath, ProbeInfo srcInfo)
        {
            ProbeInfo outInfo;
            try
            {
                if (new FileInfo(outPath).Length == 0) return "output file is empty";
                outInfo = Media.Probe(g.FfprobePath, outPath);
            }
            catch (ProbeError ex) { return ex.Message; }
            catch (IOException ex) { return ex.Message; }
            int[] want = null;
            try { want = FfmpegBuilder.ExpectedSize(srcInfo, v.Video); }
            catch (BuildError) { }
            if (want != null && (outInfo.Width != want[0] || outInfo.Height != want[1]))
                return "output is " + outInfo.Width + "x" + outInfo.Height + ", expected " + want[0] + "x" + want[1];
            double sd = srcInfo.Duration, od = outInfo.Duration;
            if (sd > 0 && od > 0 && Math.Abs(od - sd) > Math.Max(2.0, 0.02 * sd))
                return "output duration " + od.ToString("0.0", Util.Inv) + "s differs from source " + sd.ToString("0.0", Util.Inv) + "s";
            return null;
        }

        // ---------------------------------------------------------------- UI helpers
        public static CommandPreview Preview(GeneralCfg general, VerticalCfg vertical, List<Adapter> found)
        {
            CommandPreview p = new CommandPreview();
            Resolved resolved = FfmpegBuilder.ResolveGpu(vertical.Gpu, found);
            try
            {
                BuildResult b = FfmpegBuilder.Build(general.FfmpegPath, vertical, FfmpegBuilder.SampleInfo,
                                                    "INPUT.mp4", "OUTPUT.mp4", resolved, false);
                p.Ok = true;
                p.Command = b.Text;
                p.Notes.AddRange(b.Notes);
                p.Notes.Add("Preview uses a sample 1920x1080 / 8 Mbps / h264 source.");
            }
            catch (BuildError ex) { p.Error = ex.Message; }
            p.Backend = resolved.Backend;
            p.Adapter = resolved.Adapter != null ? resolved.Adapter.Name : "";
            return p;
        }

        public static EncoderTestResult RunEncoderTest(GeneralCfg general, VerticalCfg vertical, List<Adapter> found, int timeoutMs)
        {
            return RunEncoderTest(general, vertical, found, timeoutMs, new SystemLauncher(), new SystemSessions());
        }

        static DirectorySecurity MediaTempSecurity()
        {
            DirectorySecurity ds = DataDirSecurity.Desired();
            System.Security.Principal.SecurityIdentifier who = Util.LocalServiceSid;
            if (!Util.MediaAsLocalService)
            {
                // a restricted token can use the account itself but not Administrators (console mode, not as a service)
                using (System.Security.Principal.WindowsIdentity me = System.Security.Principal.WindowsIdentity.GetCurrent()) who = me.User;
            }
            ds.AddAccessRule(new FileSystemAccessRule(who, FileSystemRights.Modify,
                                                      InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            return ds;
        }

        /// <summary>Encode a 2 second generated clip with the chosen GPU settings, in the same order of
        /// attempts a real conversion uses (without the CPU fallback).</summary>
        public static EncoderTestResult RunEncoderTest(GeneralCfg general, VerticalCfg vertical, List<Adapter> found, int timeoutMs,
                                                       ILauncher launcher, ISessions sessions)
        {
            string ffmpeg = general.FfmpegPath, ffprobe = general.FfprobePath;
            Resolved resolved = FfmpegBuilder.ResolveGpu(vertical.Gpu, found);
            EncoderTestResult r = new EncoderTestResult();
            r.Backend = resolved.Backend;
            r.Label = Gpu.BackendLabel(resolved.Backend);
            r.Adapter = resolved.Adapter != null ? resolved.Adapter.Name : "";
            StringBuilder log = new StringBuilder();
            string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "watchkeeper-test-" + Guid.NewGuid().ToString("N"));
            string sample = System.IO.Path.Combine(tmp, "sample.mp4");
            string outFile = System.IO.Path.Combine(tmp, "test-out.mp4");
            try
            {
                // a new folder (never one that already exists) for administrators, Local System and the ffmpeg account only
                if (Util.IsWindows) { if (!Native.CreateSecuredFolder(tmp, MediaTempSecurity())) throw new IOException(tmp + " already exists"); }
                else Directory.CreateDirectory(tmp);
                string[] gen = {
                    "-y", "-hide_banner", "-loglevel", "warning", "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=30",
                    "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", "2",
                    "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", sample };
                Util.CaptureResult c;
                try { c = Util.RunMedia(ffmpeg, gen, timeoutMs); }
                catch (Exception ex)
                {
                    r.Error = "cannot run ffmpeg (" + ffmpeg + "): " + ex.Message;
                    r.Log = r.Error;
                    return r;
                }
                if (c.TimedOut || c.ExitCode != 0 || !File.Exists(sample))
                {
                    r.Error = "could not create the test clip: " + (c.TimedOut ? "timed out" : MeaningfulTail(c.Error, 6));
                    r.Log = r.Error + "\n\nCommand: " + Util.JoinArgs(new List<string>(gen));
                    return r;
                }
                ProbeInfo info = Media.Probe(ffprobe, sample);
                List<Resolved> plan = Plan(vertical.Gpu, resolved, found, false);

                for (int a = 0; a < plan.Count; a++)
                {
                    Resolved attempt = plan[a];
                    string what = "Attempt " + (a + 1) + " (" + Label(attempt) + ")";
                    string error = null;
                    try { if (File.Exists(outFile)) File.Delete(outFile); } catch (Exception) { }
                    BuildResult b = FfmpegBuilder.Build(ffmpeg, vertical, info, sample, outFile, attempt, false);
                    if (a == 0 || r.Command.Length == 0) r.Command = b.Text;
                    double t0 = Util.Now();
                    // Same arguments as the preview, plus quieter output so the error is easy to read.
                    List<string> runArgs = b.Args.GetRange(1, b.Args.Count - 1);
                    int dash = runArgs.IndexOf("-y");
                    runArgs.InsertRange(dash >= 0 ? dash + 1 : 0, new[] { "-hide_banner", "-loglevel", "warning" });
                    FfmpegRunResult run = FfmpegExec.Run(launcher, sessions, ffmpeg, runArgs, attempt.InUserSession, "normal", timeoutMs, null, null, null);
                    r.Seconds = Math.Round(Util.Now() - t0, 2);
                    if (run.StartError != null) error = run.StartError;
                    else if (run.TimedOut) error = "encode timed out";
                    else if (run.ExitCode != 0) error = "ffmpeg exit code " + run.ExitCode + ": " + MeaningfulTail(run.Errors, 6);

                    if (error == null)
                    {
                        try
                        {
                            ProbeInfo res = Media.Probe(ffprobe, outFile);
                            r.Output = res.Width + "x" + res.Height + " " + res.Codec;
                            int[] want = FfmpegBuilder.ExpectedSize(info, vertical.Video);
                            if (want != null && (res.Width != want[0] || res.Height != want[1]))
                                error = "output is " + res.Width + "x" + res.Height + ", expected " + want[0] + "x" + want[1];
                        }
                        catch (ProbeError ex) { error = "output unreadable: " + ex.Message; }
                    }

                    if (error == null)
                    {
                        r.Ok = true;
                        r.Label = Label(attempt);
                        if (a > 0)
                        {
                            List<string> hints = new List<string>();
                            if (attempt.InUserSession && !plan[0].InUserSession)
                                hints.Add("the GPU only worked from the signed-in user's session. Conversions do this automatically while " +
                                          "\"Where ffmpeg runs\" allows it and a user is signed in");
                            if (attempt.NoHwDecode)
                                hints.Add("only software decoding worked; untick \"Decode on the GPU as well\" to skip the failed first attempt");
                            r.Note = "Earlier attempts failed: " + string.Join("; ", hints.ToArray()) + ".";
                            r.Log = log.ToString() + what + " worked.";
                        }
                        return r;
                    }

                    if (r.Error.Length == 0) r.Error = error;
                    log.Append(what).Append(" failed: ").Append(error).Append("\n");
                    log.Append("Command: ").Append(b.Text).Append("\n");
                    if (run.Errors.Length > 0) log.Append("ffmpeg said:\n").Append(Tail(run.Errors, 3000)).Append("\n");
                    log.Append("\n");
                }
                r.Log = log.ToString().TrimEnd();
                return r;
            }
            catch (ProbeError ex) { r.Error = ex.Message; r.Log = ex.Message; return r; }
            catch (BuildError ex) { r.Error = ex.Message; r.Log = ex.Message; return r; }
            finally
            {
                try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch (Exception) { }
            }
        }

        static readonly Regex BannerLine = new Regex(
            @"^\s*(ffmpeg version |built with |configuration:|lib(avutil|avcodec|avformat|avdevice|avfilter|swscale|swresample|postproc)\s+\d)",
            RegexOptions.IgnoreCase);

        /// <summary>ffmpeg's version banner (printed on stderr unless -hide_banner) says nothing about a failure.</summary>
        internal static string StripBanner(string text)
        {
            List<string> keep = new List<string>();
            foreach (string l in (text ?? "").Replace("\r", "").Split('\n'))
                if (!BannerLine.IsMatch(l)) keep.Add(l);
            return string.Join("\n", keep.ToArray());
        }

        /// <summary>The last few non-empty lines of ffmpeg's output, joined with " | ".</summary>
        internal static string MeaningfulTail(string text, int lines)
        {
            List<string> keep = new List<string>();
            foreach (string l in StripBanner(text).Split('\n'))
                if (l.Trim().Length > 0) keep.Add(l.Trim());
            int from = Math.Max(0, keep.Count - lines);
            string joined = string.Join(" | ", keep.GetRange(from, keep.Count - from).ToArray());
            return joined.Length > 700 ? joined.Substring(joined.Length - 700) : joined;
        }

        static string Tail(string text, int n)
        {
            string t = (text ?? "").Trim();
            return t.Length > n ? t.Substring(t.Length - n) : t;
        }
    }
}
