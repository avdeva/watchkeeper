// Logging: one text file per day in the logs folder (kept 30 days) plus the newest lines in memory for the manager.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Watchkeeper
{
    public class LogEntry
    {
        public long Seq;
        public double Time;
        public string Level = "info";      // debug | info | warning | error
        public string Source = "";
        public string Text = "";

        public string Line()
        {
            return Util.FromEpoch(Time).ToString("yyyy-MM-dd HH:mm:ss", Util.Inv) + " " + Level.ToUpperInvariant().PadRight(7) +
                   " [" + Source + "] " + Text;
        }
    }

    public class LogHub
    {
        readonly object lk = new object();
        readonly List<LogEntry> recent = new List<LogEntry>();
        long seq;
        public string Folder;               // null = memory only (tests, the manager)
        public int KeepDays = 30;
        public int KeepInMemory = 1000;
        public bool Debug;
        string lastCleanDay = "";
        /// <summary>Also called for every line (tests, the console mode).</summary>
        public Action<LogEntry> Echo;

        public LogHub() { }
        public LogHub(string folder) { Folder = folder; }

        public Logger For(string source) { return new Logger(this, source); }

        public void Write(string level, string source, string text)
        {
            if (level == "debug" && !Debug) return;
            LogEntry e = new LogEntry();
            e.Time = Util.Now();
            e.Level = level;
            e.Source = source;
            e.Text = text ?? "";
            lock (lk)
            {
                e.Seq = ++seq;
                recent.Add(e);
                if (recent.Count > KeepInMemory) recent.RemoveRange(0, recent.Count - KeepInMemory);
                if (Folder != null) ToFile(e);
            }
            Action<LogEntry> echo = Echo;
            if (echo != null) { try { echo(e); } catch (Exception) { } }
        }

        void ToFile(LogEntry e)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                DateTime local = Util.FromEpoch(e.Time);
                string day = local.ToString("yyyyMMdd", Util.Inv);
                File.AppendAllText(Path.Combine(Folder, "watchkeeper-" + day + ".log"), e.Line() + Environment.NewLine, Encoding.UTF8);
                if (day != lastCleanDay)
                {
                    lastCleanDay = day;
                    foreach (string f in Directory.GetFiles(Folder, "watchkeeper-*.log"))
                    {
                        try { if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-KeepDays)) File.Delete(f); }
                        catch (Exception) { }
                    }
                }
            }
            catch (Exception) { }     // logging must never stop the service
        }

        public List<LogEntry> Since(long afterSeq, int max)
        {
            lock (lk)
            {
                List<LogEntry> o = new List<LogEntry>();
                foreach (LogEntry e in recent) if (e.Seq > afterSeq) o.Add(e);
                if (o.Count > max) o.RemoveRange(0, o.Count - max);
                return o;
            }
        }

        public string TodayFile()
        {
            return Folder == null ? null : Path.Combine(Folder, "watchkeeper-" + DateTime.Now.ToString("yyyyMMdd", Util.Inv) + ".log");
        }
    }

    public class Logger
    {
        readonly LogHub hub;
        public readonly string Source;
        public Logger(LogHub hub, string source) { this.hub = hub; Source = source; }
        public void Debug(string t) { hub.Write("debug", Source, t); }
        public void Info(string t) { hub.Write("info", Source, t); }
        public void Warn(string t) { hub.Write("warning", Source, t); }
        public void Error(string t) { hub.Write("error", Source, t); }
    }
}
