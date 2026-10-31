// Alerts: every problem the parts raise goes to the destinations (e-mail, Jira, webhook) whose ticked parts and minimum
// level it matches, and to the Windows event log. The same alert is sent at most once per "repeat after" period. When the
// problem is over, "back to normal" goes to the destinations that received the alert and want recoveries.
// Sending happens on a thread of its own; a failed send is tried again after 1, 5 and 15 minutes.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace Watchkeeper
{
    public class AlertRecord
    {
        public double Time;
        public string Part = "", Key = "", Level = "", Title = "", Message = "";
        public bool Recovery;
        /// <summary>Where it went, or why it was not sent.</summary>
        public string Result = "";
    }

    public class DestinationStatus
    {
        public string Id = "";
        public string Name = "";
        public double LastSent;
        public string LastError = "";
        public double LastErrorAt;
        public int Sent;
    }

    public class AlertStatus
    {
        public List<AlertRecord> Recent = new List<AlertRecord>();
        public List<DestinationStatus> Destinations = new List<DestinationStatus>();
        public List<string> Active = new List<string>();
    }

    public class Alerts : IAlertSink
    {
        class Job
        {
            public AlertDestination Dest;
            public Alert Alert;
            public AlertRecord Record;
            public int Tries;
            public double NotBefore;
        }

        readonly Func<Settings> settings;
        readonly Logger log;
        readonly object lk = new object();
        readonly Dictionary<string, Alert> active = new Dictionary<string, Alert>();
        readonly Dictionary<string, double> lastSent = new Dictionary<string, double>();         // destination|key -> time
        readonly HashSet<string> delivered = new HashSet<string>();                               // destination|key that got the alert
        readonly List<Job> queue = new List<Job>();
        readonly List<AlertRecord> recent = new List<AlertRecord>();
        readonly Dictionary<string, DestinationStatus> dstatus = new Dictionary<string, DestinationStatus>();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        Thread worker;
        volatile bool stopping;
        static readonly double[] RetryAfter = { 60, 300, 900 };

        /// <summary>Tests: send on the calling thread.</summary>
        public bool Inline;

        public Alerts(Func<Settings> settings, Logger log)
        {
            this.settings = settings;
            this.log = log;
        }

        public void Start()
        {
            worker = new Thread(Work);
            worker.IsBackground = true;
            worker.Name = "alerts";
            worker.Start();
        }

        public void Stop()
        {
            stopping = true;
            wake.Set();
            if (worker != null) worker.Join(10000);
        }

        public static string Server(Settings s)
        {
            return s.General.ServerName.Length > 0 ? s.General.ServerName : Environment.MachineName;
        }

        static int Rank(string level) { return level == "error" ? 3 : level == "warning" ? 2 : 1; }

        static bool Wants(AlertDestination d, string part)
        {
            switch (part)
            {
                case "watchdog": return d.Watchdog;
                case "folders": return d.Folders;
                case "vertical": return d.Vertical;
                case "gpio": return d.Gpio;
                case "service": return d.Service;
                case "test": return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- raising
        public void Raise(string part, string key, string level, string title, string message)
        {
            Alert a = new Alert();
            a.Part = part;
            a.Key = key;
            a.Level = level;
            a.Title = title;
            a.Message = message;
            lock (lk) active[key] = a;
            Dispatch(a);
        }

        public void Recover(string key, string title, string message)
        {
            Alert was;
            lock (lk)
            {
                if (!active.TryGetValue(key, out was)) return;
                active.Remove(key);
            }
            Alert a = new Alert();
            a.Part = was.Part;
            a.Key = key;
            a.Level = "info";
            a.Title = title;
            a.Message = message;
            a.Recovery = true;
            Dispatch(a);
        }

        void Dispatch(Alert a)
        {
            Settings cfg = settings();
            AlertRecord rec = new AlertRecord();
            rec.Time = a.Time;
            rec.Part = a.Part;
            rec.Key = a.Key;
            rec.Level = a.Level;
            rec.Title = a.Title;
            rec.Message = a.Message;
            rec.Recovery = a.Recovery;
            List<string> went = new List<string>();
            if (cfg.Alerts.EventLog) WriteEventLog(a, cfg);
            foreach (AlertDestination d in cfg.Alerts.Destinations)
            {
                if (!d.Enabled || !Wants(d, a.Part)) continue;
                string dk = d.Id + "|" + a.Key;
                lock (lk)
                {
                    if (a.Recovery)
                    {
                        if (!d.Recoveries || !delivered.Remove(dk)) continue;
                        lastSent.Remove(dk);
                    }
                    else
                    {
                        if (Rank(a.Level) < Rank(d.MinLevel)) continue;
                        double last;
                        if (lastSent.TryGetValue(dk, out last) && a.Time - last < d.RepeatMinutes * 60) { went.Add(d.Name + " (held back: sent " + Util.FmtDuration(a.Time - last) + " ago)"); continue; }
                        lastSent[dk] = a.Time;
                        delivered.Add(dk);
                    }
                    Job j = new Job();
                    j.Dest = d;
                    j.Alert = a;
                    j.Record = rec;
                    queue.Add(j);
                }
                went.Add(d.Name);
            }
            rec.Result = went.Count == 0 ? "no destination takes it" : string.Join(", ", went.ToArray());
            lock (lk)
            {
                recent.Insert(0, rec);
                if (recent.Count > 100) recent.RemoveAt(recent.Count - 1);
            }
            if (Inline) Drain(); else wake.Set();
        }

        void Work()
        {
            while (!stopping)
            {
                try { Drain(); } catch (Exception ex) { log.Error("Alerts: " + ex.Message); }
                wake.WaitOne(5000);
            }
            try { Drain(); } catch (Exception) { }       // what is queued when the service stops is still sent once
        }

        void Drain()
        {
            while (true)
            {
                Job j = null;
                double now = Util.Now();
                lock (lk)
                {
                    j = queue.Find(delegate(Job x) { return x.NotBefore <= now; });
                    if (j != null) queue.Remove(j);
                }
                if (j == null) return;
                Settings cfg = settings();
                AlertDestination d = cfg.Alerts.Destinations.Find(delegate(AlertDestination x) { return x.Id == j.Dest.Id; }) ?? j.Dest;
                DestinationStatus ds = DStatus(d);
                try
                {
                    string what = Deliver(d, j.Alert, cfg);
                    lock (lk) { ds.LastSent = Util.Now(); ds.Sent++; ds.LastError = ""; }
                    log.Info("Alert '" + j.Alert.Title + "' sent to " + d.Name + (what.Length > 0 ? " (" + what + ")" : "") + ".");
                }
                catch (Exception ex)
                {
                    j.Tries++;
                    lock (lk) { ds.LastError = ex.Message; ds.LastErrorAt = Util.Now(); }
                    if (j.Tries <= RetryAfter.Length && !Inline)
                    {
                        j.NotBefore = Util.Now() + RetryAfter[j.Tries - 1];
                        lock (lk) queue.Add(j);
                        log.Warn("Alert '" + j.Alert.Title + "' could not be sent to " + d.Name + " (tries again in " + Util.FmtDuration(RetryAfter[j.Tries - 1]) + "): " + ex.Message);
                    }
                    else log.Error("Alert '" + j.Alert.Title + "' could not be sent to " + d.Name + ": " + ex.Message);
                }
            }
        }

        DestinationStatus DStatus(AlertDestination d)
        {
            lock (lk)
            {
                DestinationStatus s;
                if (!dstatus.TryGetValue(d.Id, out s)) { s = new DestinationStatus(); s.Id = d.Id; dstatus[d.Id] = s; }
                s.Name = d.Name;
                return s;
            }
        }

        // ---------------------------------------------------------------- delivery
        public static string Subject(Alert a, string server)
        {
            string tag = a.Recovery ? "OK" : a.Level == "error" ? "ERROR" : a.Level == "warning" ? "WARNING" : "INFO";
            return "[" + server + "] " + tag + ": " + a.Title;
        }

        public static string Body(Alert a, string server)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(a.Message).Append("\n\n");
            sb.Append("Server: ").Append(server).Append('\n');
            sb.Append("Time: ").Append(Util.FmtTime(a.Time)).Append('\n');
            sb.Append("Part: ").Append(a.Part).Append('\n');
            if (!a.Recovery && a.Part != "test") sb.Append("\nA message follows when this is back to normal.");
            return sb.ToString();
        }

        /// <summary>Send one alert to one destination now. Returns a short note; throws when it could not be sent.</summary>
        public static string Deliver(AlertDestination d, Alert a, Settings cfg)
        {
            string server = Server(cfg);
            string subject = Subject(a, server), body = Body(a, server);
            if (d.Type == "email")
            {
                Smtp.Send(d, Secrets.Reveal(d.SmtpPassword), subject, body, 30000);
                return "";
            }
            if (d.Type == "jira") return Jira.Send(d, a, server, subject, body);
            string json = WebhookBody(d, a, server, subject, body);
            HttpResult r = Http.Send("POST", d.Url, json, "application/json", null, null, null, 20000);
            if (!r.Answered) throw new InvalidOperationException("no answer from the webhook: " + r.Error);
            if (!r.Success) throw new InvalidOperationException("the webhook answered HTTP " + r.Status + ": " + Util.OneLine(r.Body, 200));
            return "";
        }

        public static string WebhookBody(AlertDestination d, Alert a, string server, string subject, string body)
        {
            string tpl = d.Body.Trim().Length > 0 ? d.Body : "{\"text\": \"{title}\\n{message}\"}";
            return tpl.Replace("{title}", Http.JsonEscape(subject))
                      .Replace("{message}", Http.JsonEscape(a.Message))
                      .Replace("{level}", Http.JsonEscape(a.Recovery ? "ok" : a.Level))
                      .Replace("{server}", Http.JsonEscape(server))
                      .Replace("{time}", Http.JsonEscape(Util.FmtTime(a.Time)))
                      .Replace("{part}", Http.JsonEscape(a.Part));
        }

        /// <summary>The manager's Test button: a test message to one destination.</summary>
        public static string Test(AlertDestination d, Settings cfg)
        {
            if (d.Type == "jira") return Jira.Check(d);
            Alert a = new Alert();
            a.Part = "test";
            a.Key = "test:" + Util.NewId();
            a.Level = "info";
            a.Title = "Test message";
            a.Message = "This is a test message from Watchkeeper on " + Server(cfg) + ". Alerts will arrive like this.";
            string note = Deliver(d, a, cfg);
            return "Sent" + (note.Length > 0 ? " (" + note + ")" : "") + ".";
        }

        static bool eventSourceChecked;

        void WriteEventLog(Alert a, Settings cfg)
        {
            if (!Util.IsWindows || (a.Level == "info" && !a.Recovery)) return;
            try
            {
                if (!eventSourceChecked)
                {
                    eventSourceChecked = true;
                    if (!EventLog.SourceExists("Watchkeeper")) EventLog.CreateEventSource("Watchkeeper", "Application");
                }
                EventLogEntryType t = a.Recovery ? EventLogEntryType.Information : a.Level == "error" ? EventLogEntryType.Error : EventLogEntryType.Warning;
                EventLog.WriteEntry("Watchkeeper", Subject(a, Server(cfg)) + "\n\n" + a.Message, t, a.Recovery ? 1000 : a.Level == "error" ? 3000 : 2000);
            }
            catch (Exception) { }
        }

        public AlertStatus Snapshot()
        {
            Settings cfg = settings();
            AlertStatus s = new AlertStatus();
            lock (lk)
            {
                foreach (AlertRecord r in recent) { AlertRecord c = new AlertRecord(); Json.FillFrom(c, Json.ToJson(r)); s.Recent.Add(c); }
                foreach (AlertDestination d in cfg.Alerts.Destinations)
                {
                    DestinationStatus ds;
                    DestinationStatus c = new DestinationStatus();
                    c.Id = d.Id;
                    c.Name = d.Name;
                    if (dstatus.TryGetValue(d.Id, out ds)) Json.FillFrom(c, Json.ToJson(ds));
                    s.Destinations.Add(c);
                }
                foreach (Alert a in active.Values) s.Active.Add(a.Title);
            }
            return s;
        }
    }
}
