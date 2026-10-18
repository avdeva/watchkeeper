// Folder policies: every file in the folder (optionally its sub-folders) is checked against
// the policy's rules from the top; the first rule that matches decides what happens to it
// (delete, move, copy or keep). Files no rule matches are left alone.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Watchkeeper
{
    /// <summary>One line of a preview / run: what happened (or would happen) to a file.</summary>
    public class PlanItem
    {
        public string File = "";         // relative to the policy folder
        public string Action = "";       // delete | move | copy | keep | skip | none | failed
        public string Rule = "";
        public string Destination = "";
        public string Note = "";
        public long Size;
        public double Modified;          // epoch seconds
    }

    public static class FileOps
    {
        static readonly string[] MonthNames = {
            "january", "february", "march", "april", "may", "june", "july",
            "august", "september", "october", "november", "december" };

        // "MultiCorder2 - Cam2 - 23 July 2026 - 08-29-03 AM.mp4"
        static readonly Regex FileRe = new Regex(
            @"([0-9]{1,2}) ([A-Za-z]+) ([0-9]{4}) - ([0-9]{2})-([0-9]{2})-([0-9]{2}) ([AP]M)", RegexOptions.IgnoreCase);

        static int MonthNumber(string name)
        {
            string low = name.ToLowerInvariant();
            if (low == "sept") return 9;
            for (int i = 0; i < MonthNames.Length; i++)
                if (MonthNames[i] == low || (low.Length == 3 && MonthNames[i].StartsWith(low))) return i + 1;
            return 0;
        }

        /// <summary>Recording start time from a name like "... 23 July 2026 - 08-29-03 AM.mp4", or null.
        /// Throws FormatException for a timestamp that cannot be a real date.</summary>
        public static DateTime? ParseRecordingTime(string name)
        {
            Match m = FileRe.Match(name);
            if (!m.Success) return null;
            int month = MonthNumber(m.Groups[2].Value);
            if (month == 0) throw new FormatException("unknown month '" + m.Groups[2].Value + "'");
            int hour = int.Parse(m.Groups[4].Value, Util.Inv);
            if (hour < 1 || hour > 12) throw new FormatException("invalid hour '" + m.Groups[4].Value + "' for 12-hour clock");
            bool am = m.Groups[7].Value.ToUpperInvariant() == "AM";
            if (am) hour = hour == 12 ? 0 : hour;
            else hour = hour == 12 ? 12 : hour + 12;
            try
            {
                return new DateTime(int.Parse(m.Groups[3].Value, Util.Inv), month, int.Parse(m.Groups[1].Value, Util.Inv),
                                    hour, int.Parse(m.Groups[5].Value, Util.Inv), int.Parse(m.Groups[6].Value, Util.Inv));
            }
            catch (ArgumentOutOfRangeException ex)
            {
                throw new FormatException(ex.Message);
            }
        }

        public static bool InWindow(TimeSpan t, TimeSpan start, TimeSpan end)
        {
            if (start <= end) return start <= t && t <= end;
            return t >= start || t <= end;    // window wraps past midnight
        }

        /// <summary>Wildcard list ("*.mp4;*.mov") to case-insensitive regexes. Empty text = no patterns.</summary>
        public static List<Regex> CompilePatterns(string pattern)
        {
            List<Regex> list = new List<Regex>();
            foreach (string raw in (pattern ?? "").Replace(',', ';').Split(';'))
            {
                string p = raw.Trim();
                if (p.Length == 0) continue;
                StringBuilder sb = new StringBuilder("^");
                foreach (char c in p)
                {
                    if (c == '*') sb.Append(".*");
                    else if (c == '?') sb.Append('.');
                    else sb.Append(Regex.Escape(c.ToString()));
                }
                sb.Append('$');
                list.Add(new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.Singleline));
            }
            return list;
        }

        public static bool AnyMatch(List<Regex> pats, string name)
        {
            foreach (Regex r in pats) if (r.IsMatch(name)) return true;
            return false;
        }

        /// <summary>Test hook: treat every move as a move between drives.</summary>
        public static bool ForceCrossVolume;

        /// <summary>True while another program has the file open for writing (a recording in progress).</summary>
        public static bool InUse(string path)
        {
            try
            {
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) return false;
            }
            catch (FileNotFoundException) { return false; }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return false; }
        }

        /// <summary>"name.ext" -> "name (1).ext", "name (2).ext", ... (first that does not exist).</summary>
        public static string FreeName(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path);
            string stem = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            for (int i = 1; i < 100000; i++)
            {
                string p = Path.Combine(dir, stem + " (" + i.ToString(Util.Inv) + ")" + ext);
                if (!File.Exists(p)) return p;
            }
            throw new IOException("no free name for " + path);
        }

        public static double UnitSeconds(string unit)
        {
            switch (unit)
            {
                case "minutes": return 60;
                case "hours": return 3600;
                case "weeks": return 7 * 86400;
            }
            return 86400;
        }

        public static string DescribeAge(double value, string unit)
        {
            string u = unit;
            if (value == 1) u = unit.TrimEnd('s');
            return Util.Num(value, 2) + " " + u;
        }
    }

    public static class FolderRunner
    {
        class Compiled
        {
            public FileRule Rule;
            public List<Regex> Include, Exclude;
            public double AgeSeconds;
            public TimeSpan WinStart, WinEnd;
            public long MinBytes, MaxBytes;
            public HashSet<string> Newest = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public string Dest = "";
        }

        class Entry
        {
            public FileInfo Info;
            public string Rel;
            public string RelDir;
        }

        public static string Describe(FileRule r) { return Describe(r, r.Destination); }

        /// <summary>One sentence describing a rule, for lists and logs. dest: where move/copy sends files.</summary>
        public static string Describe(FileRule r, string dest)
        {
            StringBuilder sb = new StringBuilder();
            switch (r.Action)
            {
                case "delete": sb.Append("Delete"); break;
                case "move": sb.Append("Move"); break;
                case "copy": sb.Append("Copy"); break;
                default: sb.Append("Keep"); break;
            }
            sb.Append(r.Pattern == "*" ? " all files" : " " + r.Pattern);
            if (r.Exclude.Length > 0) sb.Append(" except ").Append(r.Exclude);
            if (r.OlderThan > 0) sb.Append(" older than ").Append(FileOps.DescribeAge(r.OlderThan, r.OlderThanUnit));
            if (r.UseWindow)
                sb.Append(r.WindowSource == "filename" ? ", recorded " : ", modified ")
                  .Append(r.WindowMode == "inside" ? "between " : "outside ")
                  .Append(r.WindowStart).Append("-").Append(r.WindowEnd);
            if (r.MinSizeMb > 0) sb.Append(", at least ").Append(Util.Num(r.MinSizeMb, 2)).Append(" MB");
            if (r.MaxSizeMb > 0) sb.Append(", at most ").Append(Util.Num(r.MaxSizeMb, 2)).Append(" MB");
            if (r.KeepNewest > 0) sb.Append(", newest ").Append(r.KeepNewest).Append(" kept");
            if ((r.Action == "move" || r.Action == "copy") && (dest ?? "").Length > 0) sb.Append(" to ").Append(dest);
            return sb.ToString();
        }

        static bool IsSkippable(FileSystemInfo fi)
        {
            // Hidden and system entries are skipped;
            // reparse points (junctions, links) are never followed.
            FileAttributes a = fi.Attributes;
            return (a & (FileAttributes.Hidden | FileAttributes.System)) != 0;
        }

        static void Enumerate(DirectoryInfo dir, string rel, bool recurse, HashSet<string> skipDirs, List<Entry> outList)
        {
            foreach (FileInfo fi in dir.GetFiles())
            {
                if (IsSkippable(fi) || (fi.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                string n = fi.Name.ToLowerInvariant();
                if (n.EndsWith(".moving") || n.EndsWith(".copying")) continue;
                Entry e = new Entry();
                e.Info = fi;
                e.RelDir = rel;
                e.Rel = rel.Length == 0 ? fi.Name : Path.Combine(rel, fi.Name);
                outList.Add(e);
            }
            if (!recurse) return;
            foreach (DirectoryInfo sub in dir.GetDirectories())
            {
                if (IsSkippable(sub) || (sub.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (skipDirs.Contains(Util.PathKey(sub.FullName))) continue;
                Enumerate(sub, rel.Length == 0 ? sub.Name : Path.Combine(rel, sub.Name), true, skipDirs, outList);
            }
        }

        static bool Matches(Compiled c, Entry e, double nowEpoch, out string why)
        {
            why = null;
            FileRule r = c.Rule;
            string name = e.Info.Name;
            if (c.Include.Count > 0 && !FileOps.AnyMatch(c.Include, name)) return false;
            if (FileOps.AnyMatch(c.Exclude, name)) return false;
            long size = e.Info.Length;
            if (c.MinBytes > 0 && size < c.MinBytes) return false;
            if (c.MaxBytes > 0 && size > c.MaxBytes) return false;
            if (c.AgeSeconds > 0 && nowEpoch - Util.ToEpoch(e.Info.LastWriteTimeUtc) < c.AgeSeconds) return false;
            if (r.UseWindow)
            {
                TimeSpan t;
                if (r.WindowSource == "filename")
                {
                    DateTime? rec;
                    try { rec = FileOps.ParseRecordingTime(name); }
                    catch (FormatException ex) { why = "time in the name cannot be read: " + ex.Message; return false; }
                    if (rec == null) { why = "no recording time in the name"; return false; }
                    t = rec.Value.TimeOfDay;
                }
                else t = e.Info.LastWriteTime.TimeOfDay;
                bool inside = FileOps.InWindow(t, c.WinStart, c.WinEnd);
                if (inside != (r.WindowMode == "inside")) return false;
            }
            if (c.Newest.Contains(e.Rel)) { why = "one of the newest " + r.KeepNewest; return false; }
            return true;
        }

        /// <summary>Apply a policy. plan (optional) receives up to planLimit items describing each file.</summary>
        public static TaskResult Run(FolderPolicy f, GeneralCfg g, SourceLogger log, bool dryRun,
                                     List<PlanItem> plan, int planLimit, Func<bool> cancelled)
        {
            TaskResult res = new TaskResult();
            string folder = Util.NormPath(f.Path);
            string tag = dryRun ? "[dry-run] " : "";
            string reason = Util.ProtectedReason(folder);
            if (reason != null)
            {
                log.Error(f.Name + ": folder refused (" + reason + "): " + folder);
                res.Text = "Folder refused: " + reason;
                return res;
            }
            if (!Directory.Exists(folder))
            {
                log.Error(f.Name + ": folder does not exist: " + folder);
                res.Text = "Folder does not exist: " + folder;
                return res;
            }
            // the folder and every folder above it: checked to be real folders (not links) and held open for the run
            SafeDir root;
            try { root = SafeDir.Open(folder, false); }
            catch (Exception ex)
            {
                log.Error(f.Name + ": folder refused: " + ex.Message);
                res.Text = "Folder refused: " + ex.Message;
                return res;
            }
            DirCache dirs = new DirCache(root);
            try
            {

            // ---- compile rules
            List<Compiled> rules = new List<Compiled>();
            HashSet<string> skipDirs = new HashSet<string>();
            foreach (FileRule r in f.Rules)
            {
                if (!r.Enabled) continue;
                Compiled c = new Compiled();
                c.Rule = r;
                c.Include = FileOps.CompilePatterns(r.Pattern);
                c.Exclude = FileOps.CompilePatterns(r.Exclude);
                c.AgeSeconds = r.OlderThan * FileOps.UnitSeconds(r.OlderThanUnit);
                c.MinBytes = (long)(r.MinSizeMb * 1048576.0);
                c.MaxBytes = (long)(r.MaxSizeMb * 1048576.0);
                try
                {
                    c.WinStart = Util.ParseTime(r.WindowStart);
                    c.WinEnd = Util.ParseTime(r.WindowEnd);
                }
                catch (FormatException) { c.WinStart = TimeSpan.Zero; c.WinEnd = new TimeSpan(23, 59, 59); }
                if (r.Action == "move" || r.Action == "copy")
                {
                    c.Dest = Util.NormPath(Cfg.RuleDestination(f, r));
                    string dr = Util.ProtectedReason(c.Dest);
                    if (dr != null)
                    {
                        log.Error(f.Name + ": rule '" + r.Name + "' destination refused (" + dr + "): " + c.Dest + "; rule ignored");
                        res.RuleErrors++;
                        continue;
                    }
                    if (Util.PathKey(c.Dest) == Util.PathKey(folder))
                    {
                        log.Error(f.Name + ": rule '" + r.Name + "' moves files into the folder itself; rule ignored");
                        res.RuleErrors++;
                        continue;
                    }
                    skipDirs.Add(Util.PathKey(c.Dest));    // never process files a rule has just moved there
                }
                rules.Add(c);
            }
            res.Rules = rules.Count;

            // ---- list files
            List<Entry> files = new List<Entry>();
            try { Enumerate(new DirectoryInfo(folder), "", f.Subfolders, skipDirs, files); }
            catch (Exception ex)
            {
                log.Error(f.Name + ": cannot read " + folder + ": " + ex.Message);
                res.Text = "Cannot read the folder: " + ex.Message;
                return res;
            }
            files.Sort(delegate(Entry a, Entry b) { return string.Compare(a.Rel, b.Rel, StringComparison.OrdinalIgnoreCase); });

            double now = Util.Now();
            // "keep newest N": among files matching the rule's name and size filters, newest by Date Modified
            foreach (Compiled c in rules)
            {
                if (c.Rule.KeepNewest <= 0) continue;
                List<Entry> cand = new List<Entry>();
                foreach (Entry e in files)
                {
                    string n = e.Info.Name;
                    if (c.Include.Count > 0 && !FileOps.AnyMatch(c.Include, n)) continue;
                    if (FileOps.AnyMatch(c.Exclude, n)) continue;
                    long size = e.Info.Length;
                    if (c.MinBytes > 0 && size < c.MinBytes) continue;
                    if (c.MaxBytes > 0 && size > c.MaxBytes) continue;
                    cand.Add(e);
                }
                cand.Sort(delegate(Entry a, Entry b) { return b.Info.LastWriteTimeUtc.CompareTo(a.Info.LastWriteTimeUtc); });
                for (int i = 0; i < cand.Count && i < c.Rule.KeepNewest; i++) c.Newest.Add(cand[i].Rel);
            }

            // ---- apply
            foreach (Entry e in files)
            {
                if (cancelled != null && cancelled())
                {
                    log.Warn(f.Name + ": stopped before finishing.");
                    res.Text = "Stopped";
                    return res;
                }
                res.Considered++;
                PlanItem item = new PlanItem();
                item.File = e.Rel;
                try
                {
                    item.Size = e.Info.Length;
                    item.Modified = Util.ToEpoch(e.Info.LastWriteTimeUtc);
                }
                catch (Exception) { continue; }    // vanished

                double age = now - item.Modified;
                if (age < f.MinAgeSeconds)
                {
                    item.Action = "skip";
                    item.Note = "modified " + ((long)age).ToString(Util.Inv) + "s ago, may still be written";
                    log.Info(f.Name + ": skipped (" + item.Note + "): " + e.Rel);
                    res.Skipped++;
                    AddPlan(plan, planLimit, item);
                    continue;
                }

                Compiled hit = null;
                string lastWhy = null;
                foreach (Compiled c in rules)
                {
                    string why;
                    if (Matches(c, e, now, out why)) { hit = c; break; }
                    if (why != null && lastWhy == null) lastWhy = why;
                }
                if (hit == null)
                {
                    item.Action = "none";
                    item.Note = lastWhy ?? "no rule matches";
                    res.Untouched++;
                    AddPlan(plan, planLimit, item);
                    continue;
                }
                FileRule rule = hit.Rule;
                item.Rule = rule.Name;
                if (rule.Action != "keep" && FileOps.InUse(e.Info.FullName))
                {
                    item.Action = "skip";
                    item.Note = "open in another program (still recording?)";
                    log.Info(f.Name + ": skipped (" + item.Note + "): " + e.Rel);
                    res.Skipped++;
                    AddPlan(plan, planLimit, item);
                    continue;
                }
                try
                {
                    switch (rule.Action)
                    {
                        case "keep":
                            item.Action = "keep";
                            res.Kept++;
                            break;

                        case "delete":
                            item.Action = "delete";
                            if (dryRun) log.Info(tag + f.Name + ": would delete (" + rule.Name + "): " + e.Rel);
                            else
                            {
                                SafeFs.Delete(dirs.Source(e.RelDir), e.Info.Name);
                                log.Info(f.Name + ": deleted (" + rule.Name + "): " + e.Rel);
                            }
                            res.Deleted++;
                            res.Bytes += item.Size;
                            break;

                        default:
                            Transfer(f, g, hit, e, dryRun, log, res, item, dirs);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    item.Action = "failed";
                    item.Note = ex.Message;
                    log.Error(f.Name + ": failed to " + rule.Action + " " + e.Rel + ": " + ex.Message);
                    res.Failed++;
                }
                AddPlan(plan, planLimit, item);
            }

            if (f.Subfolders && f.RemoveEmptyFolders) RemoveEmpty(new DirectoryInfo(folder), "", true, skipDirs, dryRun, f.Name, log, res, dirs);

            string verb = dryRun ? "would be " : "";
            List<string> parts = new List<string>();
            parts.Add(res.Considered + " file(s) checked");
            if (res.Deleted > 0) parts.Add(res.Deleted + " " + verb + "deleted (" + Util.FmtSize(res.Bytes) + ")");
            if (res.Moved > 0) parts.Add(res.Moved + " " + verb + "moved");
            if (res.Copied > 0) parts.Add(res.Copied + " " + verb + "copied");
            if (res.Kept > 0) parts.Add(res.Kept + " kept");
            if (res.Skipped > 0) parts.Add(res.Skipped + " skipped");
            if (res.Failed > 0) parts.Add(res.Failed + " failed");
            if (res.RuleErrors > 0) parts.Add(res.RuleErrors + " rule(s) ignored");
            res.Text = string.Join(", ", parts.ToArray());
            log.Info(f.Name + ": " + (dryRun ? "dry run" : "run") + " finished. " + res.Text + ".");
            res.Ok = res.Failed == 0 && res.RuleErrors == 0;
            return res;
            }
            finally { dirs.Dispose(); }
        }

        static void AddPlan(List<PlanItem> plan, int limit, PlanItem item)
        {
            if (plan != null && plan.Count < limit) plan.Add(item);
        }

        static void Transfer(FolderPolicy f, GeneralCfg g, Compiled c, Entry e, bool dryRun, SourceLogger log, TaskResult res, PlanItem item, DirCache dirs)
        {
            FileRule rule = c.Rule;
            bool move = rule.Action == "move";
            item.Action = rule.Action;
            string relDest = rule.KeepSubfolders && e.RelDir.Length > 0 ? e.RelDir : "";
            string destDir = relDest.Length > 0 ? Path.Combine(c.Dest, relDest) : c.Dest;
            string name = e.Info.Name;
            string target = Path.Combine(destDir, name);
            string verb = move ? "move" : "copy";
            string tag = dryRun ? "[dry-run] " : "";

            bool exists = SafeFs.EntryExists(target);
            if (exists && rule.OnConflict == "skip")
            {
                item.Action = "skip";
                item.Destination = target;
                item.Note = "already exists at the destination";
                log.Info(f.Name + ": skipped (" + rule.Name + ", already at destination): " + e.Rel);
                res.Skipped++;
                return;
            }
            if (exists && rule.OnConflict == "overwrite") item.Note = "replaces the existing file";

            if (dryRun)
            {
                if (exists && rule.OnConflict == "rename") target = FileOps.FreeName(target);
                item.Destination = target;
                log.Info(tag + f.Name + ": would " + verb + " (" + rule.Name + "): " + e.Rel + " -> " + target + (exists ? " [" + rule.OnConflict + "]" : ""));
            }
            else
            {
                // source and destination folders checked and held; the file itself is opened without following links
                SafeDir src = dirs.Source(e.RelDir);
                SafeDir dst = dirs.Dest(c.Dest, relDest);
                string dstName = exists && rule.OnConflict == "rename" ? SafeFs.FreeName(dst, name) : name;
                target = dst.Combine(dstName);
                item.Destination = target;
                bool replace = rule.OnConflict == "overwrite";
                if (move) SafeFs.Move(src, name, dst, dstName, replace);
                else SafeFs.Copy(src, name, dst, dstName, replace);
                if (rule.StampFromName) Stamp(g, dst, dstName, f.Name, log);
                log.Info(f.Name + ": " + (move ? "moved" : "copied") + " (" + rule.Name + "): " + e.Rel + " -> " + target +
                         (exists && rule.OnConflict == "overwrite" ? " [overwrote existing]" : ""));
            }
            if (move) res.Moved++; else res.Copied++;
            res.Bytes += item.Size;
        }

        static void Stamp(GeneralCfg g, SafeDir dir, string name, string policy, SourceLogger log)
        {
            DateTime? start = null;
            try { start = FileOps.ParseRecordingTime(name); }
            catch (FormatException) { }
            if (start == null)
            {
                log.Warn(policy + ": no recording time in '" + name + "'; Date Modified left unchanged.");
                return;
            }
            double duration = 0;
            try { duration = Media.ProbeDuration(g.FfprobePath, dir.Combine(name)); }
            catch (Exception) { }
            DateTime stamp = start.Value;
            if (duration > 0) stamp = stamp.AddSeconds(duration);
            else log.Warn(policy + ": duration unavailable for " + name + "; Date Modified set to the start time only.");
            SafeFs.SetModified(dir, name, stamp.ToUniversalTime());
        }

        static bool RemoveEmpty(DirectoryInfo dir, string rel, bool isRoot, HashSet<string> skipDirs, bool dryRun, string policy, SourceLogger log, TaskResult res, DirCache dirs)
        {
            bool empty = true;
            try
            {
                foreach (DirectoryInfo sub in dir.GetDirectories())
                {
                    if (IsSkippable(sub) || (sub.Attributes & FileAttributes.ReparsePoint) != 0 || skipDirs.Contains(Util.PathKey(sub.FullName)))
                    {
                        empty = false;
                        continue;
                    }
                    if (!RemoveEmpty(sub, rel.Length == 0 ? sub.Name : Path.Combine(rel, sub.Name), false, skipDirs, dryRun, policy, log, res, dirs)) empty = false;
                }
                if (dir.GetFileSystemInfos().Length > 0) empty = false;
            }
            catch (Exception) { return false; }
            if (isRoot || !empty) return false;
            if (dryRun)
            {
                log.Info("[dry-run] " + policy + ": would remove empty folder " + dir.FullName);
                return true;
            }
            try
            {
                string parentRel = Path.GetDirectoryName(rel) ?? "";
                dirs.Forget(rel);
                if (!SafeFs.DeleteEmptyDir(dirs.Source(parentRel), dir.Name)) return false;
                log.Info(policy + ": removed empty folder " + dir.FullName);
                return true;
            }
            catch (Exception ex)
            {
                log.Warn(policy + ": could not remove empty folder " + dir.FullName + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>The checked folders of one run, opened once and held until the run ends.</summary>
        sealed class DirCache : IDisposable
        {
            readonly SafeDir root;
            readonly Dictionary<string, SafeDir> source = new Dictionary<string, SafeDir>(StringComparer.OrdinalIgnoreCase);
            readonly Dictionary<string, SafeDir> dest = new Dictionary<string, SafeDir>(StringComparer.OrdinalIgnoreCase);
            readonly List<SafeDir> all = new List<SafeDir>();

            public DirCache(SafeDir root) { this.root = root; all.Add(root); }

            public SafeDir Source(string rel)
            {
                if (string.IsNullOrEmpty(rel)) return root;
                SafeDir d;
                if (source.TryGetValue(rel, out d)) return d;
                string parent = Path.GetDirectoryName(rel) ?? "";
                d = Source(parent).Child(Path.GetFileName(rel), false);
                all.Add(d);
                source[rel] = d;
                return d;
            }

            /// <summary>Release a source folder about to be removed (a held folder cannot be deleted).</summary>
            public void Forget(string rel)
            {
                SafeDir d;
                if (rel.Length > 0 && source.TryGetValue(rel, out d)) { d.Dispose(); source.Remove(rel); }
            }

            public SafeDir Dest(string destRoot, string rel)
            {
                string key = destRoot + "|" + rel;
                SafeDir d;
                if (dest.TryGetValue(key, out d)) return d;
                if (rel.Length == 0)
                {
                    d = SafeDir.Open(destRoot, true);
                }
                else
                {
                    string parent = Path.GetDirectoryName(rel) ?? "";
                    d = Dest(destRoot, parent).Child(Path.GetFileName(rel), true);
                }
                all.Add(d);
                dest[key] = d;
                return d;
            }

            public void Dispose()
            {
                foreach (SafeDir d in all) d.Dispose();
                all.Clear();
            }
        }
    }
}
