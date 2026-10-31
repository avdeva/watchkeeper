// Small helpers used everywhere: time, paths, wildcards, atomic writes, command-line quoting.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Watchkeeper
{
    public static class Util
    {
        public static readonly bool IsWindows = Environment.OSVersion.Platform == PlatformID.Win32NT;
        public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>Seconds since 1970 (UTC).</summary>
        public static double Now() { return (DateTime.UtcNow - Epoch).TotalSeconds; }
        public static double ToEpoch(DateTime t) { return (t.ToUniversalTime() - Epoch).TotalSeconds; }
        public static DateTime FromEpoch(double s) { return Epoch.AddSeconds(s).ToLocalTime(); }

        public static string NewId() { return Guid.NewGuid().ToString("N").Substring(0, 8); }

        /// <summary>A path as typed by a person: quotes and spaces trimmed, %VARIABLES% expanded.</summary>
        public static string NormPath(string path)
        {
            if (path == null) return "";
            string p = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"').Trim());
            if (!IsWindows) p = p.Replace('\\', '/');
            return p;
        }

        public static bool SamePath(string a, string b)
        {
            try
            {
                string x = Path.GetFullPath(NormPath(a)).TrimEnd('\\', '/');
                string y = Path.GetFullPath(NormPath(b)).TrimEnd('\\', '/');
                return string.Equals(x, y, IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            }
            catch (Exception) { return false; }
        }

        /// <summary>True when child is the folder itself or inside it.</summary>
        public static bool IsInside(string child, string folder)
        {
            try
            {
                string c = Path.GetFullPath(NormPath(child)).TrimEnd('\\', '/');
                string f = Path.GetFullPath(NormPath(folder)).TrimEnd('\\', '/');
                StringComparison cmp = IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                return string.Equals(c, f, cmp) || c.StartsWith(f + Path.DirectorySeparatorChar, cmp);
            }
            catch (Exception) { return false; }
        }

        /// <summary>File name without the characters Windows does not allow.</summary>
        public static string CleanFilenamePart(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder();
            foreach (char c in s)
            {
                if (c < 32 || "<>:\"/\\|?*".IndexOf(c) >= 0) continue;
                sb.Append(c);
            }
            return sb.ToString().Trim().TrimEnd('.', ' ');
        }

        // ---------------------------------------------------------------- wildcards
        /// <summary>Patterns separated by ';' or ','; * and ? wildcards; case-insensitive. Empty list = everything.</summary>
        public static List<string> Patterns(string text)
        {
            List<string> o = new List<string>();
            foreach (string p in (text ?? "").Split(';', ','))
                if (p.Trim().Length > 0) o.Add(p.Trim());
            return o;
        }

        public static bool Wildcard(string name, string pattern)
        {
            string rx = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return Regex.IsMatch(name, rx, RegexOptions.IgnoreCase);
        }

        public static bool MatchesAny(string name, List<string> patterns, bool emptyMeansAll)
        {
            if (patterns.Count == 0) return emptyMeansAll;
            foreach (string p in patterns) if (Wildcard(name, p)) return true;
            return false;
        }

        // ---------------------------------------------------------------- files
        /// <summary>Write a text file so that readers see either the old or the new content, never half of it.</summary>
        public static void WriteAtomic(string path, string text)
        {
            string full = Path.GetFullPath(path);
            string dir = Path.GetDirectoryName(full);
            Directory.CreateDirectory(dir);
            string tmp = Path.Combine(dir, "." + Path.GetFileName(full) + "." + NewId() + ".tmp");
            using (FileStream fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write))
            {
                byte[] b = new UTF8Encoding(false).GetBytes(text);
                fs.Write(b, 0, b.Length);
                fs.Flush(true);
            }
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(full)) File.Replace(tmp, full, null);
                    else File.Move(tmp, full);
                    return;
                }
                catch (Exception ex)
                {
                    if (!(ex is IOException || ex is UnauthorizedAccessException) || attempt >= 20)
                    {
                        try { File.Delete(tmp); } catch (Exception) { }
                        throw;
                    }
                    Thread.Sleep(100);      // a reader or a virus scanner can hold the file for a moment
                }
            }
        }

        public static string ReadAllTextRetry(string path)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (StreamReader r = new StreamReader(fs, Encoding.UTF8, true))
                        return r.ReadToEnd();
                }
                catch (IOException)
                {
                    if (attempt >= 10 || !File.Exists(path)) throw;
                    Thread.Sleep(100);
                }
            }
        }

        // ---------------------------------------------------------------- formatting
        public static string FmtSize(double n)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            while (n >= 1024 && i < units.Length - 1) { n /= 1024; i++; }
            return i == 0 ? ((long)n).ToString(Inv) + " B" : n.ToString("0.0", Inv) + " " + units[i];
        }

        public static string FmtDuration(double seconds)
        {
            if (double.IsNaN(seconds) || seconds < 0) return "";
            long s = (long)Math.Round(seconds);
            if (s < 60) return s + "s";
            if (s < 3600) return (s / 60) + "m " + (s % 60) + "s";
            if (s < 86400) return (s / 3600) + "h " + ((s % 3600) / 60) + "m";
            return (s / 86400) + "d " + ((s % 86400) / 3600) + "h";
        }

        public static string FmtTime(double epoch)
        {
            if (epoch <= 0) return "";
            return FromEpoch(epoch).ToString("yyyy-MM-dd HH:mm:ss", Inv);
        }

        /// <summary>Invariant, shortest sensible text for a number.</summary>
        public static string Num(double v, int decimals)
        {
            string fmt = decimals <= 0 ? "0" : "0." + new string('#', decimals);
            return v.ToString(fmt, Inv);
        }

        /// <summary>"HH:MM" to a time of day; null when it is not one.</summary>
        public static TimeSpan? ParseTime(string s)
        {
            Match m = Regex.Match(s ?? "", @"^\s*(\d{1,2}):(\d{2})\s*$");
            if (!m.Success) return null;
            int h = int.Parse(m.Groups[1].Value, Inv), mi = int.Parse(m.Groups[2].Value, Inv);
            if (h > 23 || mi > 59) return null;
            return new TimeSpan(h, mi, 0);
        }

        public static string OneLine(string s, int max)
        {
            if (s == null) return "";
            string t = Regex.Replace(s, @"\s+", " ").Trim();
            return t.Length > max ? t.Substring(0, max) + "..." : t;
        }

        // ---------------------------------------------------------------- command lines
        /// <summary>Quote one argument following the Windows (CommandLineToArgvW) rules.</summary>
        public static string QuoteArg(string a)
        {
            a = a ?? "";
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) return a;
            StringBuilder sb = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in a)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                }
                else
                {
                    sb.Append('\\', backslashes);
                    sb.Append(c);
                }
                backslashes = 0;
            }
            sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }

        public static string JoinArgs(IEnumerable<string> args)
        {
            List<string> q = new List<string>();
            foreach (string a in args) q.Add(QuoteArg(a));
            return string.Join(" ", q.ToArray());
        }

        /// <summary>Split typed extra options: quotes group words, backslashes are literal.</summary>
        public static List<string> SplitArgs(string text)
        {
            List<string> result = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return result;
            StringBuilder cur = new StringBuilder();
            bool inToken = false;
            char quote = '\0';
            foreach (char c in text)
            {
                if (quote != '\0') { if (c == quote) quote = '\0'; else cur.Append(c); }
                else if (c == '"' || c == '\'') { quote = c; inToken = true; }
                else if (char.IsWhiteSpace(c)) { if (inToken) { result.Add(cur.ToString()); cur.Length = 0; inToken = false; } }
                else { cur.Append(c); inToken = true; }
            }
            if (quote != '\0') throw new FormatException("a quote is not closed");
            if (inToken) result.Add(cur.ToString());
            return result;
        }

        /// <summary>A program in the Windows system folder, by its full path.</summary>
        public static string SystemExe(string name)
        {
            if (!IsWindows) return name.Substring(name.LastIndexOfAny(new[] { '\\', '/' }) + 1);
            return Path.Combine(Environment.SystemDirectory, name);
        }
    }
}
