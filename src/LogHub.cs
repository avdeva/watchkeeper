// In-memory ring buffer plus rotating log file shared by every module.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Watchkeeper
{
    public class LogEntry
    {
        public long Id;
        public double Ts;
        public string Level;
        public string Source;
        public string Msg;

        public string Line()
        {
            return Util.FromEpoch(Ts).ToString("yyyy-MM-dd HH:mm:ss", Util.Inv) + " " + Level.PadRight(5) + " [" + Source + "] " + Msg;
        }
    }

    public class LogHub
    {
        readonly object lk = new object();
        readonly Queue<LogEntry> buf = new Queue<LogEntry>();
        readonly int maxLen;
        long next = 1;

        readonly object fileLock = new object();
        string filePath;
        long maxBytes = 2000000;
        int backups = 5;

        /// <summary>Echo every line to the console (tests and --console only).</summary>
        public bool Echo;

        /// <summary>Raised for every entry, on the thread that logged it.</summary>
        public event Action<LogEntry> Added;

        public LogHub() : this(3000) { }
        public LogHub(int maxLen) { this.maxLen = maxLen; }

        public static int LevelNumber(string level)
        {
            switch ((level ?? "").ToUpperInvariant())
            {
                case "DEBUG": return 10;
                case "INFO": return 20;
                case "WARN": return 30;
                case "ERROR": return 40;
                default: return 20;
            }
        }

        public void AttachFile(string path, long maxBytes, int backups)
        {
            lock (fileLock)
            {
                this.filePath = path;
                this.maxBytes = maxBytes;
                this.backups = backups;
            }
            try { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))); } catch (Exception) { }
        }

        public void AttachFile(string path) { AttachFile(path, 2000000, 5); }

        public LogEntry Add(string level, string source, string msg)
        {
            LogEntry e = new LogEntry();
            e.Level = (level ?? "INFO").ToUpperInvariant();
            e.Source = source;
            e.Msg = msg ?? "";
            e.Ts = Util.Now();
            lock (lk)
            {
                e.Id = next++;
                buf.Enqueue(e);
                while (buf.Count > maxLen) buf.Dequeue();
            }
            string line = e.Line();
            WriteFile(line);
            if (Echo)
            {
                try { Console.Out.WriteLine(line); } catch (Exception) { }
            }
            Action<LogEntry> handler = Added;
            if (handler != null)
            {
                try { handler(e); } catch (Exception) { }
            }
            return e;
        }

        void WriteFile(string line)
        {
            lock (fileLock)
            {
                if (filePath == null) return;
                try
                {
                    FileInfo fi = new FileInfo(filePath);
                    if (fi.Exists && fi.Length >= maxBytes) Rotate();
                    File.AppendAllText(filePath, line + Environment.NewLine, new UTF8Encoding(false));
                }
                catch (Exception) { }
            }
        }

        void Rotate()
        {
            try
            {
                string last = filePath + "." + backups;
                if (File.Exists(last)) File.Delete(last);
                for (int i = backups - 1; i >= 1; i--)
                {
                    string from = filePath + "." + i;
                    if (File.Exists(from)) File.Move(from, filePath + "." + (i + 1));
                }
                if (backups >= 1) File.Move(filePath, filePath + ".1");
                else File.Delete(filePath);
            }
            catch (Exception) { }
        }

        public List<LogEntry> Since(long after, string source, string level, int limit)
        {
            int min = string.IsNullOrEmpty(level) ? 0 : LevelNumber(level);
            List<LogEntry> items = new List<LogEntry>();
            lock (lk)
            {
                foreach (LogEntry e in buf)
                {
                    if (e.Id <= after) continue;
                    if (!string.IsNullOrEmpty(source) && e.Source != source) continue;
                    if (min > 0 && LevelNumber(e.Level) < min) continue;
                    items.Add(e);
                }
            }
            if (limit > 0 && items.Count > limit) items.RemoveRange(0, items.Count - limit);
            return items;
        }

        public long Head()
        {
            lock (lk) { return next - 1; }
        }

        public SourceLogger Logger(string source)
        {
            return new SourceLogger(this, source, null);
        }
    }

    public class SourceLogger
    {
        public const int CaptureLimit = 400;
        readonly LogHub hub;
        readonly string source;
        readonly List<string> capture;

        public SourceLogger(LogHub hub, string source, List<string> capture)
        {
            this.hub = hub;
            this.source = source;
            this.capture = capture;
        }

        public SourceLogger WithCapture(List<string> lines)
        {
            return new SourceLogger(hub, source, lines);
        }

        void Emit(string level, string msg)
        {
            hub.Add(level, source, msg);
            if (capture != null && level != "DEBUG")
            {
                lock (capture)
                {
                    if (capture.Count < CaptureLimit) capture.Add(msg ?? "");
                }
            }
        }

        public void Debug(string msg) { Emit("DEBUG", msg); }
        public void Info(string msg) { Emit("INFO", msg); }
        public void Warn(string msg) { Emit("WARN", msg); }
        public void Error(string msg) { Emit("ERROR", msg); }
    }
}
