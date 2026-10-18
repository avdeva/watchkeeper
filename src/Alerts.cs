// Alerts: every problem (and its "back to normal") is checked against the alert rules from the
// top; each matching rule sends it to its destinations (email, webhook, Jira, Windows Event Log),
// at most once per the rule's cooldown, and a rule marked "stop" ends the check.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Watchkeeper
{
    public class AlertRecord
    {
        public double Time;
        public string Kind = "";
        public string Key = "";
        public string Severity = "";      // error | warning | info
        public string Part = "";          // folders | programs | scripts | vertical | gpio | config | service
        public string Ref = "";           // "program:id" etc., for rules that pick entries
        public string Item = "";          // the entry's name
        public string Title = "";
        public string Message = "";
        public string Rules = "";         // rules that matched
        public string Sent = "";          // destinations it went to, or why not
    }

    /// <summary>Result of a test send: what happened, and the step-by-step trace.</summary>
    public class ChannelTestResult
    {
        public bool Ok;
        public string Message = "";
        public string Detail = "";
    }

    /// <summary>Passwords, tokens and secret addresses in the configuration file.
    ///   dpapi-svc:  encrypted by the service with Local System's own key: only the service on this computer can read it.
    ///   dpapi:      encrypted with this computer's key (any local account could read it given a copy); written when the
    ///               service could not be reached, and converted to dpapi-svc: by the service when it loads the file.
    ///   plain:      typed in and not saved yet (or on a system without Windows encryption); converted when saved.
    ///   keep:<ref>  in a request from the manager: "the saved value of this entry" (secrets never travel back and forth).</summary>
    public static class Secrets
    {
        // Fixed for good: values saved by earlier installations must still decrypt.
        const string LegacyEntropyName = "Studio" + "Manager";
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes(LegacyEntropyName + "-alerts");
        static readonly byte[] ServiceEntropy = Encoding.UTF8.GetBytes("Watchkeeper-service-secrets");
        public const string KeepPrefix = "keep:";

        /// <summary>This computer's key (fallback when the service cannot encrypt). Throws when Windows cannot encrypt.</summary>
        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            if (!Util.IsWindows) return "plain:" + plain;
            byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.LocalMachine);
            return "dpapi:" + Convert.ToBase64String(data);
        }

        /// <summary>The service account's own key (call in the service). Throws when Windows cannot encrypt.</summary>
        public static string ProtectService(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            if (!Util.IsWindows) return "plain:" + plain;
            byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), ServiceEntropy, DataProtectionScope.CurrentUser);
            return "dpapi-svc:" + Convert.ToBase64String(data);
        }

        public static bool IsEncrypted(string v) { return v != null && (v.StartsWith("dpapi:") || v.StartsWith("dpapi-svc:")); }

        /// <summary>Stored by the service with its own key (nothing more to convert).</summary>
        public static bool IsServiceEncrypted(string v) { return v != null && v.StartsWith("dpapi-svc:"); }

        /// <summary>The text when it is not encrypted (typed in, or "plain:"), otherwise null.</summary>
        public static string Peek(string v)
        {
            if (string.IsNullOrEmpty(v)) return "";
            if (IsEncrypted(v) || v.StartsWith(KeepPrefix)) return null;
            return v.StartsWith("plain:") ? v.Substring(6) : v;
        }

        public static string Keep(string reference) { return KeepPrefix + reference; }

        /// <summary>Manager side: a value equal to the saved one is sent as a reference to it.</summary>
        public static string KeepIfSaved(string value, string saved, string reference)
        {
            if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(reference)) return value ?? "";
            return value == saved ? Keep(reference) : value;
        }

        /// <summary>Service side: "keep:ref" becomes the saved value (or "" when there is none).</summary>
        public static string Resolve(string value, Func<string, string> savedByRef)
        {
            if (value == null || !value.StartsWith(KeepPrefix)) return value ?? "";
            string saved = savedByRef(value.Substring(KeepPrefix.Length));
            return saved ?? "";
        }

        public static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return "";
            if (stored.StartsWith("plain:")) return stored.Substring(6);
            if (stored.StartsWith(KeepPrefix)) throw new InvalidOperationException("a saved password was referred to but not found; save the settings and try again");
            bool svc = stored.StartsWith("dpapi-svc:");
            if (svc || stored.StartsWith("dpapi:"))
            {
                try
                {
                    byte[] data = ProtectedData.Unprotect(Convert.FromBase64String(stored.Substring(svc ? 10 : 6)), svc ? ServiceEntropy : Entropy,
                                                          svc ? DataProtectionScope.CurrentUser : DataProtectionScope.LocalMachine);
                    return Encoding.UTF8.GetString(data);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("a saved password or token cannot be decrypted here (" + ex.Message + ")" +
                                                        (svc ? "; only the Watchkeeper service on the computer that saved it can read it" : "; enter it again"));
                }
            }
            return stored;    // typed into the file by hand; the service encrypts it when it loads the file
        }
    }

    public class AlertManager
    {
        public const string EventSource = "Watchkeeper";

        class Cool
        {
            public double LastSent;
            public int Suppressed;
        }

        class Job
        {
            public AlertRecord Record;
            public List<string> Destinations = new List<string>();
        }

        readonly Func<AppConfig> config;
        readonly SourceLogger log;
        readonly object lk = new object();
        readonly Dictionary<string, Cool> cool = new Dictionary<string, Cool>();
        readonly List<AlertRecord> recent = new List<AlertRecord>();
        readonly Queue<Job> queue = new Queue<Job>();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        volatile bool stopping;
        Thread thread;

        /// <summary>Replaceable for tests: (destination, record) -> what happened; throws on failure.</summary>
        public Func<AlertDestination, AlertRecord, string> Sender;

        public AlertManager(Func<AppConfig> config, LogHub hub)
        {
            this.config = config;
            this.log = hub.Logger("alerts");
            Sender = SendReal;
        }

        public void Start()
        {
            if (thread != null) return;
            thread = new Thread(Loop);
            thread.Name = "alerts";
            thread.IsBackground = true;
            thread.Start();
        }

        public void Stop(int waitMs)
        {
            stopping = true;
            wake.Set();
            if (thread != null && waitMs > 0) thread.Join(waitMs);
        }

        // ------------------------------------------------------------ classifying
        public static string SeverityOf(string kind)
        {
            switch (kind)
            {
                case "program_restart":
                case "service_start": return "warning";
                case "recovered":
                case "test": return "info";
            }
            return "error";
        }

        public static string PartOf(string kind, string key)
        {
            string k = key ?? "";
            if (k.StartsWith("folder:")) return "folders";
            if (k.StartsWith("script:")) return "scripts";
            if (k.StartsWith("program:")) return "programs";
            if (k.StartsWith("gpio")) return "gpio";
            if (k.StartsWith("vertical") || kind == "conversion_failure") return "vertical";
            if (kind == "config_problem" || k == "config") return "config";
            return "service";
        }

        /// <summary>"program:abc" from "program:abc:health".</summary>
        public static string RefOf(string key)
        {
            string[] p = (key ?? "").Split(':');
            return p.Length >= 2 && p[1].Length > 0 ? p[0] + ":" + p[1] : "";
        }

        public static string ItemName(AppConfig c, string reference)
        {
            int colon = reference.IndexOf(':');
            if (colon < 0) return "";
            string type = reference.Substring(0, colon), id = reference.Substring(colon + 1);
            switch (type)
            {
                case "folder": { FolderPolicy f = c.Folders.Find(delegate(FolderPolicy x) { return x.Id == id; }); return f != null ? f.Name : ""; }
                case "program": { ProgramJob p = c.Programs.Find(delegate(ProgramJob x) { return x.Id == id; }); return p != null ? p.Name : ""; }
                case "script": { ScriptJob s = c.Scripts.Find(delegate(ScriptJob x) { return x.Id == id; }); return s != null ? s.Name : ""; }
                case "gpio": { AxiaDevice d = c.Gpio.Devices.Find(delegate(AxiaDevice x) { return x.Id == id; }); return d != null ? d.Name : ""; }
                case "gpio-trigger": { GpioTrigger t = c.Gpio.Triggers.Find(delegate(GpioTrigger x) { return x.Id == id; }); return t != null ? t.Name : ""; }
            }
            return "";
        }

        /// <summary>Does this rule take this alert?</summary>
        public static bool Matches(AlertRule rule, AlertRecord r)
        {
            if (!rule.Enabled) return false;
            if (r.Kind == "recovered") { if (!rule.Recoveries) return false; }
            else if (AlertSettings.SeverityRank(r.Severity) < AlertSettings.SeverityRank(rule.MinSeverity)) return false;
            if (rule.AllParts) return true;
            if (!rule.Parts.Contains(r.Part)) return false;
            List<string> itemsOfPart = rule.Items.FindAll(delegate(string i) { return PartOf("", i) == r.Part; });
            return itemsOfPart.Count == 0 || itemsOfPart.Contains(r.Ref);
        }

        /// <summary>Report a problem, or that one is over (kind "recovered", same key). Never throws, never blocks.</summary>
        public void Raise(string kind, string key, string title, string message)
        {
            try
            {
                AppConfig cfg = config();
                AlertRecord r = new AlertRecord();
                r.Time = Util.Now();
                r.Kind = kind;
                r.Key = key ?? "";
                r.Severity = SeverityOf(kind);
                r.Part = PartOf(kind, r.Key);
                r.Ref = RefOf(r.Key);
                r.Item = r.Ref.Length > 0 ? ItemName(cfg, r.Ref) : "";
                r.Title = title;
                r.Message = message;

                List<AlertRule> matched = new List<AlertRule>();
                foreach (AlertRule rule in cfg.Alerts.Rules)
                {
                    if (!Matches(rule, r)) continue;
                    matched.Add(rule);
                    if (rule.Stop) break;
                }
                if (matched.Count == 0)
                {
                    r.Sent = "not sent: no alert rule takes it";
                    Remember(r);
                    return;
                }
                Job job = new Job();
                job.Record = r;
                List<string> names = new List<string>(), held = new List<string>();
                int suppressedBefore = 0;
                lock (lk)
                {
                    foreach (AlertRule rule in matched)
                    {
                        names.Add(rule.Name);
                        string ck = rule.Id + "|" + kind + "|" + r.Key;
                        Cool c;
                        if (!cool.TryGetValue(ck, out c)) { c = new Cool(); cool[ck] = c; }
                        if (rule.CooldownMinutes > 0 && c.LastSent > 0 && r.Time - c.LastSent < rule.CooldownMinutes * 60.0)
                        {
                            c.Suppressed++;
                            held.Add(rule.Name + " (sent less than " + rule.CooldownMinutes + " min ago)");
                            continue;
                        }
                        suppressedBefore = Math.Max(suppressedBefore, c.Suppressed);
                        c.Suppressed = 0;
                        c.LastSent = r.Time;
                        foreach (string d in rule.Destinations) if (!job.Destinations.Contains(d)) job.Destinations.Add(d);
                    }
                }
                r.Rules = string.Join(", ", names.ToArray());
                if (suppressedBefore > 0) r.Message += "\n\n(" + suppressedBefore + " similar alert(s) since the last one were not sent.)";
                if (job.Destinations.Count == 0)
                {
                    r.Sent = "not sent: " + string.Join("; ", held.ToArray());
                    Remember(r);
                    return;
                }
                r.Sent = "sending...";
                Remember(r);
                lock (lk) queue.Enqueue(job);
                wake.Set();
            }
            catch (Exception ex) { log.Error("Alert failed: " + ex.Message); }
        }

        void Remember(AlertRecord r)
        {
            lock (lk)
            {
                recent.Add(r);
                while (recent.Count > 200) recent.RemoveAt(0);
            }
        }

        public List<AlertRecord> Recent()
        {
            List<AlertRecord> o = new List<AlertRecord>();
            lock (lk)
            {
                for (int i = recent.Count - 1; i >= 0; i--) o.Add(Json.Clone(recent[i]));
            }
            return o;
        }

        void Loop()
        {
            while (!stopping)
            {
                wake.WaitOne(5000);
                while (true)
                {
                    Job j;
                    lock (lk)
                    {
                        if (queue.Count == 0) break;
                        j = queue.Dequeue();
                    }
                    Deliver(j.Record, j.Destinations);
                }
            }
        }

        /// <summary>Send now to the given destinations (used by the queue and by tests).</summary>
        public void Deliver(AlertRecord r, List<string> destinationIds)
        {
            AlertCfg a = config().Alerts;
            List<string> results = new List<string>();
            foreach (string id in destinationIds)
            {
                AlertDestination d = a.Destinations.Find(delegate(AlertDestination x) { return x.Id == id; });
                if (d == null) continue;
                if (!d.Enabled) { results.Add(d.Name + ": switched off"); continue; }
                try
                {
                    string what = Sender(d, r);
                    results.Add(d.Name + ": " + (string.IsNullOrEmpty(what) ? "sent" : what));
                }
                catch (Exception ex)
                {
                    results.Add(d.Name + ": FAILED " + ex.Message);
                    log.Warn("Could not send alert '" + r.Title + "' to " + d.Name + ": " + ex.Message);
                }
            }
            string text = results.Count > 0 ? string.Join("; ", results.ToArray()) : "no destination";
            lock (lk) r.Sent = text;
            log.Info("Alert '" + r.Title + "' (" + r.Severity + ", rules: " + r.Rules + "): " + text + ".");
        }

        public static string ServerName(AppConfig cfg)
        {
            string n = cfg.General.ServerLabel;
            return n.Length > 0 ? n : Environment.MachineName;
        }

        string Subject(AlertRecord r)
        {
            string tag = r.Kind == "recovered" ? "OK" : r.Severity == "error" ? "ERROR" : r.Severity == "warning" ? "WARNING" : "INFO";
            return "[" + ServerName(config()) + "] " + tag + ": " + r.Title;
        }

        string Body(AlertRecord r)
        {
            return r.Message + "\n\nServer: " + ServerName(config()) + "\nTime: " +
                   Util.FromEpoch(r.Time).ToString("yyyy-MM-dd HH:mm:ss", Util.Inv) +
                   "\nPart: " + AlertSettings.PartLabel(r.Part) + (r.Item.Length > 0 ? " / " + r.Item : "") +
                   "\nSeverity: " + r.Severity + (r.Kind == "recovered" ? " (back to normal)" : "") + "\nSent by Watchkeeper";
        }

        string SendReal(AlertDestination d, AlertRecord r)
        {
            NetTrace trace = new NetTrace();
            try { return SendTo(d, r, trace); }
            catch (Exception)
            {
                if (d.Type != "eventlog") log.Warn("The alert to " + d.Name + " failed. What happened:\n" + trace);
                throw;
            }
        }

        string SendTo(AlertDestination d, AlertRecord r, NetTrace trace)
        {
            switch (d.Type)
            {
                case "eventlog":
                    WriteEventLog(r);
                    trace.Add(Util.IsWindows ? "Written to the Application log (source " + EventSource + ")." : "No Event Log on this system.");
                    return "written";
                case "email":
                    SendEmail(d, Subject(r), Body(r), trace);
                    return "sent";
                case "webhook":
                    return SendWebhook(d, r, Subject(r), Body(r), ServerName(config()), trace);
                case "jira":
                    return JiraClient.Send(d, r, ServerName(config()), Subject(r), Body(r), trace);
            }
            throw new InvalidOperationException("unknown destination type '" + d.Type + "'");
        }

        static void WriteEventLog(AlertRecord r)
        {
            if (!Util.IsWindows) return;
            EventLogEntryType type = r.Severity == "error" ? EventLogEntryType.Error
                                   : r.Severity == "warning" ? EventLogEntryType.Warning
                                   : EventLogEntryType.Information;
            if (!EventLog.SourceExists(EventSource)) EventLog.CreateEventSource(EventSource, "Application");
            // the Application log can be read by every user of the computer: the title and a short summary only
            string first = (r.Message ?? "").Replace("\r", "").Split('\n')[0].Trim();
            if (first.Length > 200) first = first.Substring(0, 200) + "...";
            string text = r.Title + (first.Length > 0 ? "\n\n" + first : "") + "\n\nDetails: the Watchkeeper log and the manager's Alerts page.";
            EventLog.WriteEntry(EventSource, text, type, EventId(r.Kind));
        }

        static int EventId(string kind)
        {
            switch (kind)
            {
                case "service_start": return 100;
                case "task_failure": return 200;
                case "conversion_failure": return 300;
                case "program_restart": return 400;
                case "program_problem": return 410;
                case "config_problem": return 500;
                case "gpio_problem": return 600;
                case "recovered": return 700;
                case "test": return 800;
            }
            return 900;
        }

        public static SmtpSettings SmtpFrom(AlertDestination d)
        {
            SmtpSettings s = new SmtpSettings();
            s.Host = d.SmtpHost;
            s.Port = d.SmtpPort;
            s.Tls = d.SmtpTls;
            s.TrustFingerprint = d.SmtpTrustFingerprint;
            s.User = d.SmtpUser;
            s.Password = d.SmtpUser.Length > 0 ? Secrets.Unprotect(d.SmtpPassword) : "";
            s.From = d.EmailFrom;
            s.To = Cfg.SplitAddresses(d.EmailTo);
            s.TimeoutMs = Math.Max(5, d.SmtpTimeoutSeconds) * 1000;
            return s;
        }

        public static void SendEmail(AlertDestination d, string subject, string body, NetTrace trace)
        {
            SmtpSender.Send(SmtpFrom(d), subject, body, trace);
        }

        static string Esc(string s) { return Json.Serialize(s ?? "", false); }

        static JObject Text(string text, bool bold)
        {
            JObject t = new JObject();
            t.Set("type", "TextBlock");
            t.Set("text", text);
            t.Set("wrap", true);
            if (bold) { t.Set("weight", "Bolder"); t.Set("size", "Medium"); }
            return t;
        }

        /// <summary>The request body for the destination's webhook format.</summary>
        public static string WebhookBody(AlertDestination d, AlertRecord r, string subject, string body)
        {
            string server = subject.StartsWith("[") && subject.IndexOf(']') > 0 ? subject.Substring(1, subject.IndexOf(']') - 1) : "";
            subject = (subject ?? "").Replace("\r", " ").Replace("\n", " ");      // a title is one line wherever it ends up
            body = body ?? "";
            string time = Util.FromEpoch(r.Time).ToString("yyyy-MM-ddTHH:mm:ss", Util.Inv);
            switch (d.Format)
            {
                case "teams":
                    {
                        // Teams "Workflows" webhook: an Adaptive Card in a message envelope
                        JObject card = new JObject();
                        card.Set("$schema", "http://adaptivecards.io/schemas/adaptive-card.json");
                        card.Set("type", "AdaptiveCard");
                        card.Set("version", "1.4");
                        // alert text can contain file names and program output: shown as text, never as links or formatting
                        card.Set("body", new List<object> { Text(Markdown(subject), true), Text(Markdown(body).Replace("\n", "\n\n"), false) });
                        JObject att = new JObject();
                        att.Set("contentType", "application/vnd.microsoft.card.adaptive");
                        att.Set("contentUrl", null);
                        att.Set("content", card);
                        JObject o = new JObject();
                        o.Set("type", "message");
                        o.Set("attachments", new List<object> { att });
                        return Json.Serialize(o, false);
                    }
                case "discord":
                    {
                        JObject o = new JObject();
                        string c = DiscordText(subject) + "\n" + DiscordText(body);
                        o.Set("content", c.Length > 1900 ? c.Substring(0, 1900) + "..." : c);
                        JObject mentions = new JObject();
                        mentions.Set("parse", new List<object>());          // no @everyone, @here, user or role mentions
                        o.Set("allowed_mentions", mentions);
                        return Json.Serialize(o, false);
                    }
                case "json":
                case "jira_automation":
                    {
                        // Jira automation reads these as {{webhookData.title}}, {{webhookData.severity}} ...
                        JObject o = new JObject();
                        o.Set("title", r.Title);
                        o.Set("summary", subject);
                        o.Set("message", r.Message);
                        o.Set("body", body);
                        o.Set("severity", r.Severity);
                        o.Set("recovered", r.Kind == "recovered");
                        o.Set("kind", r.Kind);
                        o.Set("part", r.Part);
                        o.Set("item", r.Item);
                        o.Set("key", r.Key);
                        o.Set("dedupe", JiraClient.Label(server, r.Key));
                        o.Set("server", server);
                        o.Set("time", time);
                        return Json.Serialize(o, false);
                    }
                case "custom":
                    {
                        // placeholders are replaced by JSON strings (quotes included), all in one pass: text that is put in
                        // (a file name, a message) is never searched for placeholders again
                        Dictionary<string, string> values = new Dictionary<string, string>();
                        values["subject"] = subject; values["title"] = r.Title; values["message"] = r.Message; values["body"] = body;
                        values["kind"] = r.Kind; values["severity"] = r.Severity; values["part"] = r.Part; values["item"] = r.Item;
                        values["server"] = server; values["time"] = time;
                        return FillTemplate(d.Template ?? "", values);
                    }
                default:
                    {
                        // Slack, Mattermost, Google Chat, Rocket.Chat and old Teams connectors
                        JObject o = new JObject();
                        o.Set("text", "*" + SlackText(subject) + "*\n\n" + SlackText(body));
                        o.Set("unfurl_links", false);
                        o.Set("unfurl_media", false);
                        return Json.Serialize(o, false);
                    }
            }
        }

        static readonly System.Text.RegularExpressions.Regex Placeholder = new System.Text.RegularExpressions.Regex(@"\{([a-z]+)\}");

        /// <summary>A custom webhook body: each known {placeholder} becomes the JSON string of its value, in one pass.</summary>
        public static string FillTemplate(string template, Dictionary<string, string> values)
        {
            return Placeholder.Replace(template ?? "", delegate(System.Text.RegularExpressions.Match m)
            {
                string v;
                return values.TryGetValue(m.Groups[1].Value, out v) ? Esc(v) : m.Value;
            });
        }

        /// <summary>Teams cards: characters that would make links or formatting are escaped.</summary>
        public static string Markdown(string text) { return Markdown(text, "\\`*_[]()"); }

        /// <summary>Discord understands escapes for every markdown character.</summary>
        public static string DiscordText(string text) { return Markdown(text, "\\`*_{}[]()<>#+-.!|~@"); }

        static readonly System.Text.RegularExpressions.Regex Address = new System.Text.RegularExpressions.Regex(
            @"\b([a-z][a-z0-9+.-]{1,15})://|\bwww\.", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        static readonly System.Text.RegularExpressions.Regex HostLike = new System.Text.RegularExpressions.Regex(
            @"\b([a-z0-9-]+)\.(?=[a-z][a-z0-9-]{1,23}\b)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>Chat apps turn addresses and anything that looks like a domain (also file names such as
        /// "report.zip" or "clip.mov", which are real top-level domains) into links. Alert text can contain names other
        /// accounts chose, so in chat: "scheme://" becomes "scheme[:]//", "www." becomes "www[.]", and a dot before
        /// letters gets a zero-width space after it. The text still reads the same; nothing in it can be clicked.</summary>
        public static string Defang(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            string t = Address.Replace(text, delegate(System.Text.RegularExpressions.Match m)
            {
                return m.Value.EndsWith("://") ? m.Groups[1].Value + "[:]//" : m.Value.Substring(0, m.Value.Length - 1) + "[.]";
            });
            return HostLike.Replace(t, "$1.\u200B");
        }

        static string Markdown(string text, string special)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in Defang(text))
            {
                if (special.IndexOf(c) >= 0) sb.Append('\\');
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>Slack, Mattermost, Google Chat: &amp; &lt; &gt; escaped, so no &lt;!channel&gt; mention or &lt;url|text&gt; link.</summary>
        public static string SlackText(string text)
        {
            return Defang(text).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        public static HttpRequestSpec WebhookRequest(AlertDestination d, string payload)
        {
            HttpRequestSpec s = new HttpRequestSpec();
            s.Method = "POST";
            s.Url = Secrets.Unprotect(d.Url);
            s.Body = payload;
            s.ContentType = "application/json";
            s.Proxy = d.Proxy;
            s.ProxyUrl = Secrets.Unprotect(d.ProxyUrl);
            s.ProxyLogin = d.ProxyLogin;
            s.TimeoutMs = 20000;
            s.HeaderLines(Secrets.Unprotect(d.Headers));
            if (d.Format == "jira_automation" && d.Secret.Length > 0) s.Header("X-Automation-Webhook-Token", Secrets.Unprotect(d.Secret));
            return s;
        }

        public static string SendWebhook(AlertDestination d, AlertRecord r, string subject, string body, string server, NetTrace trace)
        {
            HttpResult res = Net.Send(WebhookRequest(d, WebhookBody(d, r, subject, body)), trace);
            if (!res.Answered) throw new NetFailure("webhook", "The webhook could not be reached: " + res.Error, "", null);
            if (res.Redirect.Length > 0) throw new NetFailure("webhook", "The webhook was not sent: " + res.Redirect + ".", "", null);
            if (!res.Success)
            {
                string url = Secrets.Unprotect(d.Url);
                bool teamsUrl = url.Contains("logic.azure.com") || url.Contains("powerplatform.com");
                bool jiraUrl = url.Contains("automation.atlassian.com") || url.Contains("api-private.atlassian.com/automation");
                string hint = "";
                if (teamsUrl && d.Format != "teams") hint = " This is a Teams Workflows address: choose the format 'Microsoft Teams (Workflows)'.";
                else if (jiraUrl && (res.Status == 401 || res.Status == 403)) hint = " Jira automation refused the secret: paste the webhook's secret into Secret.";
                else if (res.Status == 401 || res.Status == 403) hint = " The address is not accepted (wrong or expired webhook link, or a header is missing).";
                else if (res.Status == 404) hint = " The webhook address does not exist (deleted, or copied incompletely).";
                else if (res.Status == 407) hint = " The proxy needs a login.";
                else if (res.Status == 410) hint = " The webhook was removed or retired (old Teams connector links no longer work; create a Workflows webhook).";
                throw new NetFailure("webhook", "The webhook answered HTTP " + res.Status + (res.Body.Length > 0 ? ": " + Net.Redact(Net.OneLine(res.Body, 160)) : "") + "." + hint, "", null);
            }
            return "HTTP " + res.Status;
        }

        /// <summary>Send a test alert to one destination with the given (possibly unsaved) settings. Never throws.
        /// mode "check" (Jira): check the login and project without creating an issue.</summary>
        public ChannelTestResult Test(AlertDestination d, string mode)
        {
            AlertRecord r = new AlertRecord();
            r.Time = Util.Now();
            r.Kind = "test";
            r.Key = "test:" + d.Id;
            r.Severity = "info";
            r.Part = "service";
            r.Title = "Test alert";
            r.Message = "This is a test alert from Watchkeeper to '" + d.Name + "'. If you can read it, this destination works.";
            ChannelTestResult res = new ChannelTestResult();
            NetTrace trace = new NetTrace();
            try
            {
                string what;
                if (d.Type == "jira" && mode == "check") what = JiraClient.Check(d, trace);
                else if (Sender != (Func<AlertDestination, AlertRecord, string>)SendReal) what = Sender(d, r);
                else what = SendTo(d, r, trace);
                res.Ok = true;
                res.Message = d.Type == "jira" && mode == "check" ? what : "Done: " + (string.IsNullOrEmpty(what) ? "sent" : what) + ".";
            }
            catch (Exception ex)
            {
                string m = ex.Message;
                if (!(ex is NetFailure) && ex.InnerException != null) m += " (" + ex.InnerException.Message + ")";
                res.Ok = false;
                res.Message = m;
                trace.Add("FAILED: " + m);
            }
            res.Detail = trace.ToString();
            log.Info("Test alert to " + d.Name + ": " + (res.Ok ? res.Message : "failed: " + res.Message));
            return res;
        }

        /// <summary>.NET Framework defaults to old TLS versions; webhooks and mail servers need TLS 1.2.</summary>
        public static void EnableModernTls()
        {
            Net.EnableModernTls();
        }
    }
}
