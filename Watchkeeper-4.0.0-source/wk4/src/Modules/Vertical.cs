// Vertical conversion: recordings that appear in the watched folder are turned on their side (portrait) with ffmpeg.
//
// A file is converted once it has stopped changing. The output is written under a temporary name, checked (size and
// length compared with the source) and only then given its real name; only after that is the original deleted or moved.
// When an encoder fails, the next one in the plan is tried; the one that worked is tried first for the next file.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

namespace Watchkeeper
{
    public class VerticalFile
    {
        public string Name = "";
        /// <summary>waiting | converting | done | exists | skipped | retry | failed</summary>
        public string State = "";
        public string Note = "";
        public string Output = "";
    }

    public class VerticalStatus
    {
        public bool Enabled;
        /// <summary>off | idle | converting | error</summary>
        public string State = "off";
        public string Text = "";
        public string Current = "";
        public double Percent;
        public string Encoder = "";
        public List<VerticalFile> Files = new List<VerticalFile>();
        public List<VerticalFile> Recent = new List<VerticalFile>();
    }

    public class Vertical
    {
        class Failure { public int Count; public double NextTry; public string Error = ""; public long Size; public DateTime Written; }

        readonly Func<Settings> settings;
        readonly IProcesses procs;
        readonly Logger log;
        readonly IAlertSink alerts;
        readonly object lk = new object();
        readonly Dictionary<string, Failure> failures = new Dictionary<string, Failure>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, long> lastSize = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        VerticalStatus status = new VerticalStatus();
        string preferred;                 // the attempt that worked last
        string preferredSig;
        Thread worker;
        volatile bool stopping;
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        public int PollMs = 5000;
        public int StallMs = 600000;

        static readonly Regex RecorderAndCamera = new Regex(@"^[^-]+ - [^-]+ - ");

        public Vertical(Func<Settings> settings, IProcesses procs, Logger log, IAlertSink alerts)
        {
            this.settings = settings;
            this.procs = procs;
            this.log = log;
            this.alerts = alerts;
        }

        public void Start()
        {
            worker = new Thread(delegate()
            {
                while (!stopping)
                {
                    try { Pass(); }
                    catch (Exception ex) { log.Error("Vertical conversion: " + ex.Message); }
                    wake.WaitOne(PollMs);
                }
            });
            worker.IsBackground = true;
            worker.Name = "vertical";
            worker.Start();
        }

        public void Stop()
        {
            stopping = true;
            wake.Set();
            if (worker != null) worker.Join(15000);
        }

        /// <summary>Forget failures so failed files are tried again (manager: Retry).</summary>
        public void RetryFailed()
        {
            lock (lk) failures.Clear();
            wake.Set();
        }

        public VerticalStatus Snapshot()
        {
            lock (lk)
            {
                VerticalStatus c = new VerticalStatus();
                Json.FillFrom(c, Json.ToJson(status));
                return c;
            }
        }

        // ---------------------------------------------------------------- naming
        /// <summary>The output prefix for a file name; null (and why) when it is left alone.</summary>
        public static string PrefixFor(string name, VerticalSettings v, out string why)
        {
            why = null;
            foreach (VerticalSource s in v.Sources)
            {
                if (s.Match.Length == 0 || name.IndexOf(s.Match, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (s.Enabled) return s.Prefix;
                why = "source '" + s.Name + "' is switched off";
                return null;
            }
            if (v.ConvertOthers) return v.OtherPrefix;
            why = "matches no source";
            return null;
        }

        /// <summary>"Recorder 7 - Cam1 - 23 July 2026 - 08-29-03 AM" becomes "Prefix - 23 July 2026 - 08-29-03 AM".</summary>
        public static string OutputName(string fileName, string prefix)
        {
            string stem = Path.GetFileNameWithoutExtension(fileName);
            string rest = RecorderAndCamera.IsMatch(stem) ? RecorderAndCamera.Replace(stem, "", 1) : stem;
            return (prefix.Length > 0 ? prefix + " - " + rest : rest) + ".mp4";
        }

        // ---------------------------------------------------------------- one pass over the folder
        public void Pass()
        {
            Settings cfg = settings();
            VerticalSettings v = cfg.Vertical;
            if (!v.Enabled)
            {
                SetState("off", "Switched off", null);
                return;
            }
            List<Problem> problems = Validate.Vertical(cfg);
            problems.RemoveAll(delegate(Problem p) { return p.Level != "error"; });
            if (problems.Count > 0)
            {
                SetState("error", "Not set up: " + problems[0].Text, null);
                return;
            }
            string watch = Util.NormPath(v.WatchFolder), outDir = Util.NormPath(v.OutputFolder);
            if (!Directory.Exists(watch))
            {
                string msg = "the folder " + watch + " does not exist or cannot be reached";
                if (status.State != "error" || status.Text != Cap(msg)) { log.Error("Vertical conversion: " + msg); alerts.Raise("vertical", "vertical:folder", "error", "Vertical conversion cannot run", msg); }
                SetState("error", Cap(msg), null);
                return;
            }
            alerts.Recover("vertical:folder", "Vertical conversion runs again", "The folder " + watch + " can be read again.");

            List<FileInfo> files = new List<FileInfo>();
            try
            {
                foreach (FileInfo f in new DirectoryInfo(watch).GetFiles())
                    if (v.Extensions.Contains(f.Extension.ToLowerInvariant())) files.Add(f);
            }
            catch (Exception ex) { SetState("error", "Cannot read " + watch + ": " + ex.Message, null); return; }
            files.Sort(delegate(FileInfo a, FileInfo b) { return a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc); });

            List<VerticalFile> rows = new List<VerticalFile>();
            List<FileInfo> ready = new List<FileInfo>();
            double now = Util.Now();
            HashSet<string> present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (FileInfo f in files)
            {
                present.Add(f.FullName);
                VerticalFile row = new VerticalFile();
                row.Name = f.Name;
                rows.Add(row);
                string why;
                string prefix = PrefixFor(f.Name, v, out why);
                if (prefix == null) { row.State = "skipped"; row.Note = "left alone: " + why; continue; }
                row.Output = OutputName(f.Name, prefix);
                if (File.Exists(Path.Combine(outDir, row.Output))) { row.State = "exists"; row.Note = "the output already exists; the original is left alone"; continue; }
                long size;
                DateTime written;
                try { size = f.Length; written = f.LastWriteTimeUtc; } catch (Exception) { continue; }
                Failure fail;
                lock (lk)
                {
                    if (failures.TryGetValue(f.FullName, out fail) && (fail.Size != size || fail.Written != written)) { failures.Remove(f.FullName); fail = null; }
                }
                if (fail != null && fail.Count > v.Retries) { row.State = "failed"; row.Note = "gave up after " + fail.Count + " attempts: " + fail.Error; continue; }
                if (fail != null && now < fail.NextTry) { row.State = "retry"; row.Note = "tries again in " + Util.FmtDuration(fail.NextTry - now) + ": " + fail.Error; continue; }
                double age = (DateTime.UtcNow - written).TotalSeconds;
                long before;
                bool sameSize = lastSize.TryGetValue(f.FullName, out before) && before == size;
                lastSize[f.FullName] = size;
                if (age < v.SettleSeconds || !sameSize) { row.State = "waiting"; row.Note = "still being written"; continue; }
                row.State = "waiting";
                row.Note = "next";
                ready.Add(f);
            }
            List<string> goneKeys = new List<string>();
            foreach (string k in lastSize.Keys) if (!present.Contains(k)) goneKeys.Add(k);
            foreach (string k in goneKeys) lastSize.Remove(k);
            lock (lk) { status.Files = rows; status.Enabled = true; }
            if (ready.Count == 0) { SetState("idle", files.Count == 0 ? "Waiting for recordings in " + watch : "Waiting", null); return; }

            foreach (FileInfo f in ready)
            {
                if (stopping || !settings().Vertical.Enabled) return;
                VerticalFile row = rows.Find(delegate(VerticalFile r) { return r.Name == f.Name; });
                Convert(cfg, f, row);
                if (row.State == "done") lock (lk) status.Files.Remove(row);      // listed under the recent conversions from now on
            }
            SetState("idle", "Waiting", null);
        }

        static string Cap(string t) { return t.Length == 0 ? t : t.Substring(0, 1).ToUpperInvariant() + t.Substring(1); }

        void SetState(string state, string text, string current)
        {
            lock (lk)
            {
                status.State = state;
                status.Text = text;
                status.Current = current ?? "";
                if (state == "off") { status.Enabled = false; status.Files = new List<VerticalFile>(); }
                if (current == null) { status.Percent = 0; status.Encoder = ""; }
            }
        }

        void Remember(VerticalFile row)
        {
            lock (lk)
            {
                VerticalFile c = new VerticalFile();
                Json.FillFrom(c, Json.ToJson(row));
                status.Recent.Insert(0, c);
                if (status.Recent.Count > 30) status.Recent.RemoveAt(status.Recent.Count - 1);
            }
        }

        // ---------------------------------------------------------------- one file
        void Convert(Settings cfg, FileInfo f, VerticalFile row)
        {
            VerticalSettings v = cfg.Vertical;
            string ffmpeg = Util.NormPath(cfg.General.FfmpegPath), ffprobe = Ffmpeg.ProbePath(cfg.General);
            string outDir = Util.NormPath(v.OutputFolder);
            string target = Path.Combine(outDir, row.Output);
            string partial = Path.Combine(outDir, Path.GetFileNameWithoutExtension(row.Output) + ".watchkeeper-partial.mp4");
            row.State = "converting";
            row.Note = "";
            SetState("converting", "Converting " + f.Name, f.Name);
            log.Info("Converting " + f.Name + " -> " + row.Output);
            double started = Util.Now();
            string error = null;
            Attempt used = null;
            try
            {
                if (!FreeToRead(f.FullName)) { row.State = "waiting"; row.Note = "in use by another program"; return; }
                Directory.CreateDirectory(outDir);
                ProbeInfo info = Ffmpeg.Probe(ffprobe, f.FullName);
                List<Attempt> plan = Ffmpeg.Plan(v, ffmpeg);
                string sig = v.Encoder + "|" + v.HardwareDecode + "|" + v.UserSessionFallback + "|" + v.CpuFallback + "|" + v.Codec;
                if (preferredSig == sig && preferred != null)
                {
                    int i = plan.FindIndex(delegate(Attempt a) { return a.Key == preferred; });
                    if (i > 0) { Attempt a = plan[i]; plan.RemoveAt(i); plan.Insert(0, a); }
                }
                List<string> tried = new List<string>();
                foreach (Attempt a in plan)
                {
                    if (stopping) { error = "the service is stopping"; break; }
                    lock (lk) { status.Encoder = a.Label(); status.Percent = 0; }
                    List<string> args = Ffmpeg.Build(v, info, f.FullName, partial, a);
                    double duration = info.Duration;
                    RunResult r = Ffmpeg.Run(procs, ffmpeg, args, a.Desktop, v.Priority, StallMs,
                        delegate(double seconds) { if (duration > 0) lock (lk) status.Percent = Math.Min(99.9, seconds / duration * 100); },
                        delegate { return stopping; });
                    string problem = r.Ok ? Check(ffprobe, partial, v, info) : r.Describe();
                    if (problem == null) { used = a; break; }
                    tried.Add(a.Label() + ": " + problem);
                    log.Warn("Vertical conversion of " + f.Name + " with " + a.Label() + " failed: " + problem);
                    TryDelete(partial);
                    if (stopping) break;
                }
                if (used == null && error == null) error = string.Join("; ", tried.ToArray());
                if (used != null)
                {
                    if (File.Exists(target)) throw new IOException("the output " + target + " appeared while converting; the new file is kept as " + partial);
                    File.Move(partial, target);
                    preferred = used.Key;
                    preferredSig = sig;
                    AfterSuccess(v, f);
                }
            }
            catch (Exception ex) { error = ex.Message; TryDelete(partial); }

            double secs = Util.Now() - started;
            if (error == null)
            {
                row.State = "done";
                row.Note = "converted in " + Util.FmtDuration(secs) + " with " + used.Label();
                log.Info("Converted " + f.Name + " -> " + row.Output + " in " + Util.FmtDuration(secs) + " (" + used.Label() + ").");
                lock (lk) failures.Remove(f.FullName);
                alerts.Recover("vertical:file:" + f.Name, "Converted " + f.Name, f.Name + " was converted after earlier failures.");
            }
            else
            {
                Failure fail;
                lock (lk)
                {
                    if (!failures.TryGetValue(f.FullName, out fail)) { fail = new Failure(); failures[f.FullName] = fail; }
                    fail.Count++;
                    fail.Error = error;
                    try { fail.Size = f.Length; fail.Written = f.LastWriteTimeUtc; } catch (Exception) { }
                    double[] backoff = { 60, 300, 900, 3600 };
                    fail.NextTry = Util.Now() + backoff[Math.Min(fail.Count - 1, backoff.Length - 1)];
                }
                bool gaveUp = fail.Count > v.Retries;
                row.State = gaveUp ? "failed" : "retry";
                row.Note = error;
                log.Error("Vertical conversion of " + f.Name + " failed (attempt " + fail.Count + " of " + (v.Retries + 1) + "): " + error);
                if (gaveUp)
                    alerts.Raise("vertical", "vertical:file:" + f.Name, "error", "Vertical conversion failed: " + f.Name,
                                 f.Name + " could not be converted after " + fail.Count + " attempts; the original is left in place. " + error);
            }
            Remember(row);
        }

        static bool FreeToRead(string path)
        {
            try { using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) return true; }
            catch (Exception) { return false; }
        }

        static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch (Exception) { } }

        /// <summary>Null when the converted file looks right, otherwise what is wrong with it.</summary>
        public static string Check(string ffprobe, string output, VerticalSettings v, ProbeInfo src)
        {
            if (!File.Exists(output) || new FileInfo(output).Length == 0) return "ffmpeg wrote no output";
            ProbeInfo o;
            try { o = Ffmpeg.Probe(ffprobe, output); }
            catch (Exception ex) { return "the output cannot be read: " + ex.Message; }
            int[] want = Ffmpeg.ExpectedSize(v, src);
            if (want != null && (o.Width != want[0] || o.Height != want[1]))
                return "the output is " + o.Width + "x" + o.Height + " instead of " + want[0] + "x" + want[1];
            if (src.Duration > 0 && o.Duration > 0 && Math.Abs(o.Duration - src.Duration) > Math.Max(2.0, 0.02 * src.Duration))
                return "the output is " + o.Duration.ToString("0.0", Util.Inv) + "s long instead of " + src.Duration.ToString("0.0", Util.Inv) + "s";
            return null;
        }

        void AfterSuccess(VerticalSettings v, FileInfo f)
        {
            try
            {
                if (v.AfterSuccess == "delete") { f.Delete(); log.Info("Deleted the original " + f.Name + "."); }
                else if (v.AfterSuccess == "move")
                {
                    string dir = Util.NormPath(v.MoveOriginalsTo);
                    Directory.CreateDirectory(dir);
                    string to = Path.Combine(dir, f.Name);
                    if (File.Exists(to)) to = FolderTasks.FreeName(to);
                    if (string.Equals(Path.GetPathRoot(Path.GetFullPath(f.FullName)), Path.GetPathRoot(Path.GetFullPath(to)), StringComparison.OrdinalIgnoreCase))
                        File.Move(f.FullName, to);
                    else
                    {
                        File.Copy(f.FullName, to + ".watchkeeper-partial", true);
                        File.Move(to + ".watchkeeper-partial", to);
                        f.Delete();
                    }
                    log.Info("Moved the original " + f.Name + " to " + to + ".");
                }
            }
            catch (Exception ex)
            {
                string msg = "converted " + f.Name + ", but the original could not be " + (v.AfterSuccess == "delete" ? "deleted" : "moved") + ": " + ex.Message;
                log.Warn("Vertical conversion: " + msg);
                alerts.Raise("vertical", "vertical:original:" + f.Name, "warning", "Vertical conversion: original left in place", msg);
            }
        }

        // ---------------------------------------------------------------- encoder test (manager)
        /// <summary>Convert a generated 2-second clip with each attempt of the plan; one line per attempt.</summary>
        public static List<string> TestEncoders(Settings cfg, IProcesses procs)
        {
            List<string> lines = new List<string>();
            VerticalSettings v = cfg.Vertical;
            string ffmpeg = Util.NormPath(cfg.General.FfmpegPath), ffprobe = Ffmpeg.ProbePath(cfg.General);
            if (!File.Exists(ffmpeg)) { lines.Add("ffmpeg not found: " + ffmpeg); return lines; }
            string tmp = Path.Combine(Path.GetTempPath(), "watchkeeper-encoder-test-" + Util.NewId());
            Directory.CreateDirectory(tmp);
            try
            {
                string sample = Path.Combine(tmp, "sample.mp4");
                Ffmpeg.Capture c = Ffmpeg.RunCapture(ffmpeg, new[] { "-y", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=30:duration=2",
                                                                      "-f", "lavfi", "-i", "sine=duration=2", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", sample }, 60000);
                if (c.ExitCode != 0) { lines.Add("Could not make a test clip: " + Util.OneLine(c.Err, 300)); return lines; }
                ProbeInfo info = Ffmpeg.Probe(ffprobe, sample);
                foreach (Attempt a in Ffmpeg.Plan(v, ffmpeg))
                {
                    string outFile = Path.Combine(tmp, "out-" + lines.Count + ".mp4");
                    RunResult r = Ffmpeg.Run(procs, ffmpeg, Ffmpeg.Build(v, info, sample, outFile, a), a.Desktop, "normal", 60000, null, null);
                    string problem = r.Ok ? Check(ffprobe, outFile, v, info) : r.Describe();
                    lines.Add((problem == null ? "WORKS   " : "FAILED  ") + a.Label() + (problem == null ? "" : ": " + problem));
                }
            }
            catch (Exception ex) { lines.Add("Test failed: " + ex.Message); }
            finally { try { Directory.Delete(tmp, true); } catch (Exception) { } }
            return lines;
        }
    }
}
