// Runs any script or program: on a schedule, once at service start, or when a new file
// appears in a watched folder (the file must stop changing first). Output from scripts run
// in the service session is captured into the log.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Text.RegularExpressions;

namespace Watchkeeper
{
    public static class ScriptRunner
    {
        public static readonly string[] FilePlaceholders = { "{file}", "{name}", "{stem}", "{folder}" };

        /// <summary>Characters that command interpreters read as commands or variables, even inside quotes (cmd: % and !).</summary>
        static readonly char[] Unsafe = { '%', '!', '^', '&', '|', '<', '>', '"', '`' };

        /// <summary>{source} and {destination} from the script's folders. Values are always quoted.</summary>
        public static string ExpandFolders(string args, ScriptJob s)
        {
            return args.Replace("{source}", Util.QuoteAlways(Util.NormPath(s.Source)))
                       .Replace("{destination}", Util.QuoteAlways(Util.NormPath(s.Destination)));
        }

        /// <summary>{file} {name} {stem} {folder} for the file trigger. Values are always quoted.</summary>
        public static string Expand(string args, string file)
        {
            if (string.IsNullOrEmpty(file)) return args;
            string name = Path.GetFileName(file);
            return args.Replace("{file}", Util.QuoteAlways(file))
                       .Replace("{name}", Util.QuoteAlways(name))
                       .Replace("{stem}", Util.QuoteAlways(Path.GetFileNameWithoutExtension(file)))
                       .Replace("{folder}", Util.QuoteAlways(Path.GetDirectoryName(file)));
        }

        /// <summary>Why a new file's name is not passed to a script, or null. The name comes from whoever created the file.</summary>
        public static string UnsafeFileName(string file)
        {
            if (string.IsNullOrEmpty(file)) return null;
            int bad = file.IndexOfAny(Unsafe);
            if (bad >= 0) return "the name contains '" + file[bad] + "', which a command interpreter could read as a command or variable";
            string name = file.Substring(file.LastIndexOfAny(new[] { '\\', '/' }) + 1);
            if (name.StartsWith("-") || name.StartsWith("/")) return "the name starts with '" + name[0] + "', which a script or program could read as an option";
            foreach (char c in file) if (c < 32) return "the name contains a control character";
            return null;
        }

        public static List<int> OkCodes(string text)
        {
            List<int> o = new List<int>();
            foreach (string w in (text ?? "").Split(',', ';', ' '))
            {
                int n;
                if (int.TryParse(w.Trim(), out n)) o.Add(n);
            }
            if (o.Count == 0) o.Add(0);
            return o;
        }

        public static TaskResult Run(ScriptJob s, RunContext ctx, ILauncher launcher)
        {
            SourceLogger log = ctx.Log;
            string refused = UnsafeFileName(ctx.File);
            if (refused != null)
            {
                log.Error(s.Name + ": not run for " + ctx.File + ": " + refused + ". Rename the file to run it.");
                return new TaskResult(false, "File name refused: " + refused);
            }
            string ext = Path.GetExtension(Util.NormPath(s.Command)).ToLowerInvariant();
            if ((ext == ".bat" || ext == ".cmd") && Array.Exists(FilePlaceholders, delegate(string ph) { return s.Arguments.Contains(ph); }))
            {
                log.Error(s.Name + ": file placeholders cannot be used with a .bat or .cmd script; read the file from WK_FILE inside the script (!WK_FILE! with delayed expansion).");
                return new TaskResult(false, "File placeholders cannot be used with a .bat or .cmd script");
            }
            string args = Expand(ExpandFolders(s.Arguments, s), ctx.File);
            string file, arguments;
            Procs.CommandFor(s.Command, args, out file, out arguments);
            string shown = Util.QuoteArg(file) + (arguments.Length > 0 ? " " + arguments : "");
            string cmd = Util.NormPath(s.Command);
            if (Path.IsPathRooted(cmd) && !File.Exists(cmd))
            {
                log.Error(s.Name + ": not found: " + cmd);
                return new TaskResult(false, "Script not found: " + cmd);
            }
            if (s.RunAs != "user")
            {
                // runs as Local System: the script (and a program it is) only from a place just administrators can change,
                // named by its full path (a bare name would be looked up in the working folder and the search path)
                if (!Path.IsPathRooted(cmd))
                {
                    string msg = "a script that runs as Local System needs the full path of the script or program, not '" + s.Command + "'";
                    log.Error(s.Name + ": not run: " + msg);
                    return new TaskResult(false, "Not run: " + msg);
                }
                string why = PathTrust.Check(cmd);
                if (why == null && !string.Equals(file, cmd, StringComparison.OrdinalIgnoreCase) && Path.IsPathRooted(file)) why = PathTrust.Check(file);
                if (why == null && s.WorkingDir.Length > 0)
                {
                    // Windows looks for DLLs in the working folder, and cmd.exe for commands
                    string wd = PathTrust.CheckFolder(s.WorkingDir);
                    if (wd != null) why = "start-in folder: " + wd;
                }
                if (why != null)
                {
                    log.Error(s.Name + ": not run: " + why);
                    return new TaskResult(false, "Not run: " + why);
                }
            }
            if (ctx.DryRun)
            {
                log.Info("[dry-run] " + s.Name + ": would run " + shown + (s.RunAs == "user" ? " in the signed-in user's session" : ""));
                return new TaskResult(true, "Dry run: not started");
            }

            StartRequest r = new StartRequest();
            r.File = file;
            r.Arguments = arguments;
            r.WorkingDir = s.WorkingDir.Length > 0 ? Util.NormPath(s.WorkingDir)
                         : (Path.IsPathRooted(cmd) ? Path.GetDirectoryName(cmd) : "");
            r.UserSession = s.RunAs == "user";
            r.Redirect = !r.UserSession;
            r.Hidden = true;
            r.Priority = s.Priority;
            // WK_*; the same values are also set under the names used before the rename, so older scripts keep working.
            string[] prefixes = { "WK_", "STUDIO" + "_" };
            foreach (string pre in prefixes)
            {
                if (ctx.File.Length > 0) r.Environment[pre + "FILE"] = ctx.File;
                if (s.Source.Length > 0) r.Environment[pre + "SOURCE"] = Util.NormPath(s.Source);
                if (s.Destination.Length > 0) r.Environment[pre + "DESTINATION"] = Util.NormPath(s.Destination);
            }

            log.Info(s.Name + ": " + shown);
            double t0 = Util.Now();
            IRunning p;
            try { p = launcher.Start(r); }
            catch (Exception ex)
            {
                log.Error(s.Name + ": " + ex.Message);
                return new TaskResult(false, ex.Message);
            }
            using (p)
            {
                List<Thread> readers = new List<Thread>();
                if (p.StdOut != null) readers.Add(Pump(p.StdOut, delegate(string line) { log.Info("  " + line); }));
                if (p.StdErr != null) readers.Add(Pump(p.StdErr, delegate(string line) { log.Warn("  " + line); }));

                double limit = s.TimeoutMinutes > 0 ? s.TimeoutMinutes * 60.0 : double.MaxValue;
                string stopped = null;
                while (!p.WaitForExit(500))
                {
                    if (ctx.Cancelled) { stopped = "stopped on request"; break; }
                    if (Util.Now() - t0 > limit) { stopped = "timed out after " + s.TimeoutMinutes + " minute(s)"; break; }
                }
                if (stopped != null)
                {
                    p.Kill();
                    p.WaitForExit(5000);
                }
                foreach (Thread t in readers) t.Join(3000);
                double secs = Math.Round(Util.Now() - t0, 1);
                if (stopped != null)
                {
                    log.Error(s.Name + ": " + stopped + "; process ended.");
                    return new TaskResult(false, stopped);
                }
                int code = p.ExitCode;
                bool ok = OkCodes(s.OkExitCodes).Contains(code);
                string text = "Exit code " + code + " after " + Util.FmtDur(secs);
                if (ok) log.Info(s.Name + ": finished. " + text + ".");
                else log.Error(s.Name + ": failed. " + text + " (expected " + s.OkExitCodes + ").");
                TaskResult res = new TaskResult(ok, text);
                res.ExitCode = code;
                return res;
            }
        }

        static Thread Pump(TextReader reader, Action<string> sink)
        {
            Thread t = new Thread(delegate()
            {
                try
                {
                    int n = 0;
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.Trim().Length == 0) continue;
                        if (++n <= 2000) sink(line.TrimEnd());
                        else if (n == 2001) sink("... further output not logged");
                    }
                }
                catch (Exception) { }
            });
            t.IsBackground = true;
            t.Start();
            return t;
        }
    }

    /// <summary>Watches the folders of file-triggered scripts and runs the script once per new file.</summary>
    public class FileTriggers
    {
        class Pending
        {
            public long Size;
            public long Ticks;
            public double StableSince;
        }

        readonly Func<AppConfig> config;
        readonly JobManager jobs;
        readonly SourceLogger log;
        readonly string stateDir;
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        readonly Dictionary<string, Dictionary<string, Pending>> pending = new Dictionary<string, Dictionary<string, Pending>>();
        readonly Dictionary<string, HashSet<string>> seen = new Dictionary<string, HashSet<string>>();
        readonly HashSet<string> warned = new HashSet<string>();
        readonly Dictionary<string, Queue<double>> recentRuns = new Dictionary<string, Queue<double>>();
        readonly HashSet<string> backlogAlerted = new HashSet<string>();
        Thread thread;

        public int PollMs = 2000;
        /// <summary>Waiting files for one script above which an alert is raised.</summary>
        public int BacklogAlert = 100;
        /// <summary>kind, key, title, message (the alert manager's Raise).</summary>
        public Action<string, string, string, string> Alert;

        Queue<double> Runs(ScriptJob s, double now)
        {
            Queue<double> q;
            if (!recentRuns.TryGetValue(s.Id, out q)) { q = new Queue<double>(); recentRuns[s.Id] = q; }
            while (q.Count > 0 && now - q.Peek() >= 60) q.Dequeue();
            return q;
        }

        /// <summary>False when this script already ran its limit in the last minute (the files wait).</summary>
        bool MayRun(ScriptJob s, double now)
        {
            return s.MaxRunsPerMinute <= 0 || Runs(s, now).Count < s.MaxRunsPerMinute;
        }

        public FileTriggers(Func<AppConfig> config, JobManager jobs, LogHub hub, string stateDir)
        {
            this.config = config;
            this.jobs = jobs;
            this.log = hub.Logger("scripts");
            this.stateDir = stateDir;
        }

        public void Start()
        {
            if (thread != null && thread.IsAlive) return;
            stop.Reset();
            thread = new Thread(Loop);
            thread.Name = "file-triggers";
            thread.IsBackground = true;
            thread.Start();
        }

        public void Stop() { stop.Set(); }

        void Loop()
        {
            while (!stop.WaitOne(PollMs))
            {
                try { Tick(); }
                catch (Exception ex) { log.Error("File trigger error: " + ex.Message); }
            }
        }

        string SeenPath(string id) { return Path.Combine(stateDir, "seen-" + id + ".txt"); }

        static string Sig(string path, long size, long ticks)
        {
            return path.ToLowerInvariant() + "|" + size.ToString(Util.Inv) + "|" + ticks.ToString(Util.Inv);
        }

        HashSet<string> LoadSeen(string id, out bool existed)
        {
            existed = false;
            HashSet<string> set;
            if (seen.TryGetValue(id, out set)) { existed = true; return set; }
            set = new HashSet<string>();
            string p = SeenPath(id);
            if (File.Exists(p))
            {
                existed = true;
                try { foreach (string line in File.ReadAllLines(p)) if (line.Length > 0) set.Add(line); }
                catch (Exception) { }
            }
            seen[id] = set;
            return set;
        }

        void SaveSeen(string id)
        {
            HashSet<string> set;
            if (!seen.TryGetValue(id, out set)) return;
            try
            {
                Directory.CreateDirectory(stateDir);
                Util.WriteAtomic(SeenPath(id), string.Join("\n", new List<string>(set).ToArray()) + "\n");
            }
            catch (Exception ex) { log.Warn("Cannot save the list of processed files: " + ex.Message); }
        }

        static void List(DirectoryInfo dir, List<Regex> pats, bool recurse, List<FileInfo> o)
        {
            foreach (FileInfo fi in dir.GetFiles())
            {
                if ((fi.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0) continue;
                if (pats.Count > 0 && !FileOps.AnyMatch(pats, fi.Name)) continue;
                o.Add(fi);
            }
            if (!recurse) return;
            foreach (DirectoryInfo d in dir.GetDirectories())
            {
                if ((d.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0) continue;
                List(d, pats, true, o);
            }
        }

        public void Tick()
        {
            AppConfig cfg = config();
            double now = Util.Now();
            if (!cfg.General.Modules.Scripts) return;
            foreach (ScriptJob s in cfg.Scripts)
            {
                if (!s.Enabled || s.Trigger != "file") continue;
                if (stop.WaitOne(0)) return;
                string folder = Util.NormPath(s.WatchPath.Length > 0 ? s.WatchPath : s.Source);
                string key = "script:" + s.Id;
                if (!Directory.Exists(folder))
                {
                    if (warned.Add(s.Id)) log.Warn(s.Name + ": watched folder does not exist: " + folder);
                    continue;
                }
                // the watched folder and every folder above it: no links, and held so they cannot be swapped for a link
                // (to another place whose files the script would then be given) while the folder is read and the script runs
                SafeDir root;
                try { root = SafeDir.Open(folder, false); }
                catch (Exception ex)
                {
                    if (warned.Add(s.Id)) log.Error(s.Name + ": watched folder refused: " + ex.Message);
                    continue;
                }
                using (root)
                {
                    warned.Remove(s.Id);
                    if (!TickOne(s, root, folder, key, now)) return;
                }
            }
        }

        /// <summary>One file-triggered script, with its watched folder held. False when the service is stopping.</summary>
        bool TickOne(ScriptJob s, SafeDir root, string folder, string key, double now)
        {
            {
                List<FileInfo> files = new List<FileInfo>();
                try { List(new DirectoryInfo(folder), FileOps.CompilePatterns(s.WatchPattern), s.WatchSubfolders, files); }
                catch (Exception ex)
                {
                    log.Warn(s.Name + ": cannot read " + folder + ": " + ex.Message);
                    return true;
                }
                bool existed;
                HashSet<string> done = LoadSeen(s.Id, out existed);
                Dictionary<string, string> current = new Dictionary<string, string>();
                foreach (FileInfo fi in files)
                {
                    try { current[fi.FullName] = Sig(fi.FullName, fi.Length, fi.LastWriteTimeUtc.Ticks); }
                    catch (Exception) { }
                }
                if (!existed && !s.ProcessExisting)
                {
                    foreach (string sig in current.Values) done.Add(sig);
                    SaveSeen(s.Id);
                    log.Info(s.Name + ": watching " + folder + " (" + current.Count + " existing file(s) ignored).");
                    return true;
                }
                // forget files that are gone, keeps the list short
                HashSet<string> live = new HashSet<string>(current.Values);
                int before = done.Count;
                done.RemoveWhere(delegate(string x) { return !live.Contains(x); });
                if (done.Count != before) SaveSeen(s.Id);

                Dictionary<string, Pending> pend;
                if (!pending.TryGetValue(s.Id, out pend)) { pend = new Dictionary<string, Pending>(); pending[s.Id] = pend; }
                List<string> gonePending = new List<string>();
                foreach (string k in pend.Keys) if (!current.ContainsKey(k)) gonePending.Add(k);
                foreach (string k in gonePending) pend.Remove(k);

                List<FileInfo> order = new List<FileInfo>(files);
                order.Sort(delegate(FileInfo a, FileInfo b) { return a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc); });
                int waiting = 0;
                foreach (FileInfo fi in order)
                {
                    string w;
                    if (current.TryGetValue(fi.FullName, out w) && !done.Contains(w)) waiting++;
                }
                if (waiting > BacklogAlert)
                {
                    if (backlogAlerted.Add(s.Id) && Alert != null)
                        Alert("task_failure", key + ":backlog", s.Name + ": " + waiting + " files waiting",
                              waiting + " new files are waiting in " + folder + " for '" + s.Name + "' (at most " + s.MaxRunsPerMinute +
                              " runs a minute). Check what creates them.");
                }
                else backlogAlerted.Remove(s.Id);
                foreach (FileInfo fi in order)
                {
                    string sig;
                    if (!current.TryGetValue(fi.FullName, out sig) || done.Contains(sig)) continue;
                    long size = fi.Length, ticks = fi.LastWriteTimeUtc.Ticks;
                    Pending pd;
                    if (!pend.TryGetValue(fi.FullName, out pd) || pd.Size != size || pd.Ticks != ticks)
                    {
                        pd = new Pending();
                        pd.Size = size;
                        pd.Ticks = ticks;
                        pd.StableSince = now;
                        pend[fi.FullName] = pd;
                    }
                    if (now - pd.StableSince < s.StableSeconds) continue;
                    if (!MayRun(s, now)) break;      // limit reached: the remaining files wait for the next minute
                    List<SafeDir> keep = new List<SafeDir>();
                    TaskResult r;
                    try
                    {
                        // the folder the file is in is held too, and the file itself must not be a link or have a second name
                        string refused = null;
                        try
                        {
                            string rel = RelativeDir(folder, fi.DirectoryName);
                            SafeDir d = rel == null ? null : root.Descend(rel, false, keep);
                            if (d == null) refused = "it is not inside " + folder;
                            else if (Util.IsWindows) SafeFs.OpenFile(d, fi.Name, 0, Native.ShareRead | Native.ShareWrite | Native.ShareDelete).Dispose();
                        }
                        catch (Exception ex) { refused = ex.Message; }
                        if (refused != null)
                        {
                            log.Error(s.Name + ": not run for " + fi.FullName + ": " + refused);
                            pend.Remove(fi.FullName);
                            done.Add(sig);
                            SaveSeen(s.Id);
                            continue;
                        }
                        r = jobs.RunHere(key, "file", fi.FullName);
                    }
                    finally { foreach (SafeDir d in keep) d.Dispose(); }
                    if (r == null) break;            // still running from a previous file: next tick
                    Runs(s, now).Enqueue(now);
                    pend.Remove(fi.FullName);
                    done.Add(sig);                  // also after a failure: no endless retries of one file
                    SaveSeen(s.Id);
                    if (stop.WaitOne(0)) return false;
                }
            }
            return true;
        }

        /// <summary>"a\b" for a folder below the watched one, "" for the watched folder itself, null when outside it.</summary>
        static string RelativeDir(string root, string dir)
        {
            string r = Path.GetFullPath(root).TrimEnd('\\', '/'), d = Path.GetFullPath(dir).TrimEnd('\\', '/');
            if (string.Equals(r, d, StringComparison.OrdinalIgnoreCase)) return "";
            if (d.Length > r.Length + 1 && d.StartsWith(r, StringComparison.OrdinalIgnoreCase) && (d[r.Length] == '\\' || d[r.Length] == '/'))
                return d.Substring(r.Length + 1);
            return null;
        }
    }
}
