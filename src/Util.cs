// Shared helpers. Written in C# 5 so the .NET Framework's built-in csc.exe can compile it.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Watchkeeper
{
    public static class Util
    {
        public static readonly bool IsWindows = Environment.OSVersion.Platform == PlatformID.Win32NT;
        public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        static readonly Regex WinPath = new Regex(@"^(?:[A-Za-z]:(?:[\\/]|$)|\\\\)");

        public static double Now()
        {
            return (DateTime.UtcNow - Epoch).TotalSeconds;
        }

        public static double ToEpoch(DateTime local)
        {
            return (local.ToUniversalTime() - Epoch).TotalSeconds;
        }

        public static DateTime FromEpoch(double seconds)
        {
            return Epoch.AddSeconds(seconds).ToLocalTime();
        }

        // ------------------------------------------------------------ paths
        public static bool IsWindowsStyle(string p)
        {
            return !string.IsNullOrEmpty(p) && WinPath.IsMatch(p);
        }

        static List<string> Segments(string s, char sep)
        {
            List<string> parts = new List<string>();
            foreach (string seg in s.Split(sep))
            {
                if (seg.Length == 0 || seg == ".") continue;
                if (seg == "..")
                {
                    if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                    continue;
                }
                parts.Add(seg);
            }
            return parts;
        }

        /// <summary>Trim quotes and normalise separators. Windows-style paths are
        /// normalised as such even on other platforms (keeps tests honest).</summary>
        public static string NormPath(string path)
        {
            string p = (path ?? "").Trim().Trim('"').Trim();
            if (p.Length == 0) return "";
            if (IsWindowsStyle(p))
            {
                p = p.Replace('/', '\\');
                string prefix, rest;
                if (p.StartsWith("\\\\"))
                {
                    List<string> unc = Segments(p.Substring(2), '\\');
                    if (unc.Count < 2) return "\\\\" + string.Join("\\", unc.ToArray());
                    prefix = "\\\\" + unc[0] + "\\" + unc[1];
                    unc.RemoveRange(0, 2);
                    return unc.Count == 0 ? prefix + "\\" : prefix + "\\" + string.Join("\\", unc.ToArray());
                }
                prefix = p.Substring(0, 2);
                rest = p.Substring(2);
                List<string> parts = Segments(rest, '\\');
                prefix = char.ToUpperInvariant(prefix[0]) + prefix.Substring(1);
                return parts.Count == 0 ? prefix + "\\" : prefix + "\\" + string.Join("\\", parts.ToArray());
            }
            bool abs = p.StartsWith("/");
            List<string> segs = Segments(p, '/');
            string joined = string.Join("/", segs.ToArray());
            return abs ? "/" + joined : (joined.Length == 0 ? "." : joined);
        }

        public static string PathKey(string p)
        {
            string n = NormPath(p);
            return IsWindowsStyle(n) ? n.ToLowerInvariant() : n;
        }

        static readonly string[] WinTree = { "windows", "program files", "program files (x86)", "programdata" };
        static readonly string[] WinSpecial = { "$recycle.bin", "system volume information" };
        static readonly string[] PosixTree = { "etc", "usr", "bin", "sbin", "lib", "lib64", "boot", "proc", "sys", "dev" };
        static readonly string[] PosixExact = { "/home", "/root", "/tmp", "/var", "/opt", "/mnt", "/media" };

        /// <summary>Reason the folder is too dangerous for delete/move operations, or null.</summary>
        public static string ProtectedReason(string path)
        {
            string p = NormPath(path);
            if (p.Length == 0) return "folder is empty";
            if (IsWindowsStyle(p))
            {
                string rest;
                bool unc = p.StartsWith("\\\\");
                if (unc)
                {
                    List<string> u = Segments(p.Substring(2), '\\');
                    if (u.Count <= 2) return "drive or share root";
                    u.RemoveRange(0, 2);
                    rest = string.Join("\\", u.ToArray());
                }
                else
                {
                    rest = p.Substring(2);
                }
                List<string> parts = Segments(rest, '\\');
                if (parts.Count == 0) return "drive or share root";
                string first = parts[0].ToLowerInvariant();
                if (Array.IndexOf(WinTree, first) >= 0 || Array.IndexOf(WinSpecial, first) >= 0) return "system folder";
                if (!unc && first == "users" && parts.Count <= 2) return "user profile folder";
                return null;
            }
            if (p == "/") return "filesystem root";
            List<string> ps = Segments(p, '/');
            if (ps.Count > 0 && Array.IndexOf(PosixTree, ps[0]) >= 0) return "system folder";
            if (Array.IndexOf(PosixExact, p) >= 0) return "system folder";
            if (ps.Count == 2 && ps[0] == "home") return "user home folder";
            try
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
                if (!string.IsNullOrEmpty(home) && p == NormPath(home)) return "user home folder";
            }
            catch (Exception) { }
            return null;
        }

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

        public static void WriteAtomic(string path, string text)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            Directory.CreateDirectory(dir);
            string tmp = Path.Combine(dir, ".tmp-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(path)) File.Replace(tmp, path, null);
                    else File.Move(tmp, path);
                    return;
                }
                catch (IOException)
                {
                    // a reader (or a virus scanner) can hold the file for a moment
                    if (attempt >= 9) { try { File.Delete(tmp); } catch (Exception) { } throw; }
                    System.Threading.Thread.Sleep(100);
                }
                catch (UnauthorizedAccessException)
                {
                    if (attempt >= 9) { try { File.Delete(tmp); } catch (Exception) { } throw; }
                    System.Threading.Thread.Sleep(100);
                }
            }
        }

        // --------------------------------------------------------- formatting
        public static string FmtSize(double n)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            while (n >= 1024 && i < units.Length - 1) { n /= 1024; i++; }
            return i == 0 ? ((long)n).ToString(Inv) + " B" : n.ToString("0.0", Inv) + " " + units[i];
        }

        public static string FmtDur(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds)) return "";
            long s = (long)Math.Round(seconds);
            if (s >= 3600) return string.Format(Inv, "{0}h {1:00}m", s / 3600, (s % 3600) / 60);
            return string.Format(Inv, "{0}:{1:00}", s / 60, s % 60);
        }

        /// <summary>Invariant, shortest sensible text for a number (used in ffmpeg arguments).</summary>
        public static string Num(double v, int decimals)
        {
            string fmt = decimals <= 0 ? "0" : "0." + new string('#', decimals);
            return v.ToString(fmt, Inv);
        }

        public static TimeSpan ParseTime(string s)
        {
            Match m = Regex.Match(s ?? "", @"^\s*(\d{1,2}):(\d{2})(?::(\d{2}))?\s*$");
            if (!m.Success) throw new FormatException("invalid time '" + s + "' (expected HH:MM)");
            int h = int.Parse(m.Groups[1].Value, Inv), mi = int.Parse(m.Groups[2].Value, Inv);
            int sec = m.Groups[3].Success ? int.Parse(m.Groups[3].Value, Inv) : 0;
            if (h > 23 || mi > 59 || sec > 59) throw new FormatException("invalid time '" + s + "'");
            return new TimeSpan(h, mi, sec);
        }

        public static string FmtHhmm(TimeSpan t)
        {
            return t.Seconds == 0 ? string.Format(Inv, "{0:00}:{1:00}", t.Hours, t.Minutes)
                                  : string.Format(Inv, "{0:00}:{1:00}:{2:00}", t.Hours, t.Minutes, t.Seconds);
        }

        // ------------------------------------------------------- arguments
        /// <summary>Quote one argument following the CommandLineToArgvW rules.</summary>
        /// <summary>A program in the Windows system folder by its full path (never found through the search path, so a file
        /// with the same name next to Watchkeeper.exe or in the current folder is never run instead).</summary>
        public static string SystemExe(string relative)
        {
            if (!IsWindows) return relative.Substring(relative.LastIndexOfAny(new[] { '\\', '/' }) + 1);
            return System.IO.Path.Combine(Environment.SystemDirectory, relative);
        }

        public static string PowerShellExe { get { return SystemExe(@"WindowsPowerShell\v1.0\powershell.exe"); } }

        /// <summary>Quoted even without spaces (values from file names and folders).</summary>
        public static string QuoteAlways(string a) { return Quote(a ?? "", true); }

        public static string QuoteArg(string a) { return Quote(a, false); }

        static string Quote(string a, bool always)
        {
            if (!always && a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) return a;
            StringBuilder sb = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in a)
            {
                if (c == '\\') { backslashes++; }
                else if (c == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                    backslashes = 0;
                }
                else
                {
                    sb.Append('\\', backslashes);
                    backslashes = 0;
                    sb.Append(c);
                }
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

        /// <summary>Split user-typed extra ffmpeg options. Quotes group words; backslashes are literal.</summary>
        public static List<string> SplitArgs(string text)
        {
            List<string> result = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return result;
            StringBuilder cur = new StringBuilder();
            bool inToken = false;
            char quote = '\0';
            foreach (char c in text)
            {
                if (quote != '\0')
                {
                    if (c == quote) quote = '\0'; else cur.Append(c);
                }
                else if (c == '"' || c == '\'')
                {
                    quote = c; inToken = true;
                }
                else if (char.IsWhiteSpace(c))
                {
                    if (inToken) { result.Add(cur.ToString()); cur.Length = 0; inToken = false; }
                }
                else
                {
                    cur.Append(c); inToken = true;
                }
            }
            if (quote != '\0') throw new FormatException("unterminated quote");
            if (inToken) result.Add(cur.ToString());
            return result;
        }

        // ------------------------------------------------------- processes
        public class CaptureResult
        {
            public int ExitCode;
            public string Output = "";
            public string Error = "";
            public bool TimedOut;
        }

        public static ProcessStartInfo Hidden(string file, string arguments)
        {
            ProcessStartInfo psi = new ProcessStartInfo(file, arguments);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardInput = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            return psi;
        }

        /// <summary>Run a program to completion and capture its output. Throws if it cannot start.</summary>
        /// <summary>ffmpeg and ffprobe: refused when an account that is not an administrator could change the program,
        /// and run with a restricted token (no privileges, Administrators only for denying), since they read untrusted files.</summary>
        /// <summary>The account ffmpeg and ffprobe run as when the service starts them: local_service | restricted_system
        /// (general.ffmpeg_account; set when the configuration is applied).</summary>
        public static volatile string MediaAccount = "local_service";

        /// <summary>True when ffmpeg and ffprobe are started as LOCAL SERVICE: Windows, the setting, and this process is
        /// Local System (only it can log that account on; the manager uses a restricted token instead).</summary>
        public static bool MediaAsLocalService
        {
            get
            {
                if (!IsWindows || MediaAccount != "local_service") return false;
                try { using (System.Security.Principal.WindowsIdentity me = System.Security.Principal.WindowsIdentity.GetCurrent()) return me.IsSystem; }
                catch (Exception) { return false; }
            }
        }

        public static readonly System.Security.Principal.SecurityIdentifier LocalServiceSid =
            new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalServiceSid, null);

        public static CaptureResult RunMedia(string file, IEnumerable<string> args, int timeoutMs)
        {
            string why = PathTrust.Check(file);
            if (why != null) throw new InvalidOperationException("not run: " + why);
            if (!IsWindows) return RunCapture(file, args, timeoutMs);
            StartRequest req = new StartRequest();
            req.File = file;
            req.Arguments = JoinArgs(args);
            req.Redirect = true;
            req.Hidden = true;
            req.Restricted = true;
            CaptureResult r = new CaptureResult();
            StringBuilder o = new StringBuilder(), e = new StringBuilder();
            using (IRunning p = new SystemLauncher().Start(req))
            {
                System.Threading.Thread to = Pump(p.StdOut, o), te = Pump(p.StdErr, e);
                if (!p.WaitForExit(timeoutMs))
                {
                    r.TimedOut = true;
                    p.Kill();
                    p.WaitForExit(2000);
                }
                to.Join(3000);
                te.Join(3000);
                r.ExitCode = r.TimedOut ? -1 : p.ExitCode;
            }
            lock (o) r.Output = o.ToString();
            lock (e) r.Error = e.ToString();
            return r;
        }

        static System.Threading.Thread Pump(System.IO.TextReader reader, StringBuilder sink)
        {
            System.Threading.Thread t = new System.Threading.Thread(delegate()
            {
                try
                {
                    string line;
                    while (reader != null && (line = reader.ReadLine()) != null) lock (sink) sink.AppendLine(line);
                }
                catch (Exception) { }
            });
            t.IsBackground = true;
            t.Start();
            return t;
        }

        public static CaptureResult RunCapture(string file, IEnumerable<string> args, int timeoutMs)
        {
            CaptureResult r = new CaptureResult();
            StringBuilder o = new StringBuilder(), e = new StringBuilder();
            using (Process p = new Process())
            {
                p.StartInfo = Hidden(file, JoinArgs(args));
                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs a) { if (a.Data != null) lock (o) o.AppendLine(a.Data); };
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs a) { if (a.Data != null) lock (e) e.AppendLine(a.Data); };
                p.Start();
                try { p.StandardInput.Close(); } catch (Exception) { }
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(timeoutMs))
                {
                    r.TimedOut = true;
                    try { p.Kill(); } catch (Exception) { }
                    p.WaitForExit(2000);
                }
                else
                {
                    p.WaitForExit(); // flush async readers
                }
                r.ExitCode = r.TimedOut ? -1 : p.ExitCode;
            }
            lock (o) r.Output = o.ToString();
            lock (e) r.Error = e.ToString();
            return r;
        }
    }
}
