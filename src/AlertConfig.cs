// Alert settings: destinations (where an alert can go: email, webhook, Jira, Windows Event Log)
// and rules (which alerts go where). Rules are checked from the top; every rule that matches
// sends to its destinations, and a rule marked "stop" ends the check for the alerts it matches.
using System;
using System.Collections.Generic;

namespace Watchkeeper
{
    public class AlertDestination
    {
        public string Id = "";
        public string Name = "Destination";
        public bool Enabled = true;
        public string Type = "eventlog";            // eventlog | email | webhook | jira

        // email
        public string SmtpHost = "";
        public int SmtpPort = 587;
        public string SmtpTls = "starttls";         // starttls | implicit | none
        /// <summary>SHA-256 fingerprint of an internal server's own certificate to accept although it does not validate.</summary>
        public string SmtpTrustFingerprint = "";
        public string SmtpUser = "";
        /// <summary>Encrypted for this computer ("dpapi:...") once saved by the manager or deployed.</summary>
        public string SmtpPassword = "";
        public string EmailFrom = "";
        public string EmailTo = "";                 // comma or ';' separated
        public int SmtpTimeoutSeconds = 20;

        // webhook (the address, headers, secret and proxy address are stored encrypted: a webhook address is itself a key)
        public string Url = "";
        /// <summary>The webhook's host, shown in the manager instead of the encrypted address.</summary>
        public string UrlHint = "";
        public string Format = "text";              // text | teams | discord | json | jira_automation | custom
        public string Template = "";                // custom: JSON with {subject} {title} {message} {body} {kind} {severity} {part} {item} {server} {time}
        public string Headers = "";                 // extra request headers, one "Name: value" per line
        public string Secret = "";                  // Jira automation: the webhook's secret (X-Automation-Webhook-Token), encrypted
        public string Proxy = "none";               // none | system | manual
        public string ProxyUrl = "";
        /// <summary>Send this computer's Windows login to the proxy. Off by default: an impostor proxy could relay it.</summary>
        public bool ProxyLogin = false;

        // Jira (REST API)
        public string JiraUrl = "";                 // https://yoursite.atlassian.net or the Data Center address
        public string JiraAuth = "basic";           // basic (Cloud: email + API token; Data Center: user + password) | bearer (Data Center personal access token)
        public string JiraUser = "";
        public string JiraToken = "";               // API token, password or personal access token, encrypted
        public string JiraProject = "";
        public string JiraIssueType = "Task";
        public string JiraPriority = "";            // empty = the project's default
        public string JiraLabels = "watchkeeper";
        /// <summary>Repeats of the same alert become a comment on its issue while that issue is still open.</summary>
        public bool JiraReuseOpen = true;
        /// <summary>"Back to normal" messages become a comment on the open issue.</summary>
        public bool JiraCommentRecovery = true;

        public string Notes = "";
    }

    public class AlertRule
    {
        public string Id = "";
        public string Name = "Rule";
        public bool Enabled = true;
        /// <summary>True: every alert. False: only the parts (and entries) below.</summary>
        public bool AllParts = true;
        public List<string> Parts = new List<string>();        // folders | programs | scripts | vertical | gpio | config | service
        /// <summary>Only these entries ("folder:id", "program:id", "script:id", "gpio:deviceId", "gpio-trigger:id"); empty = all entries of the parts.</summary>
        public List<string> Items = new List<string>();
        public string MinSeverity = "warning";                 // info | warning | error
        /// <summary>Also send the "back to normal" message when the problem is over.</summary>
        public bool Recoveries = true;
        public List<string> Destinations = new List<string>();
        public int CooldownMinutes = 30;                       // the same alert at most once per period (0 = every time)
        /// <summary>Alerts this rule matches are not checked against the rules below it.</summary>
        public bool Stop = false;
        public string Notes = "";
    }

    public class AlertCfg
    {
        public List<AlertDestination> Destinations = new List<AlertDestination>();
        public List<AlertRule> Rules = new List<AlertRule>();
        /// <summary>Put lines a failed script printed into the alert (they can contain whatever the script printed).</summary>
        public bool IncludeScriptOutput = false;
    }

    public static class AlertSettings
    {
        public static readonly string[] Parts = { "folders", "programs", "scripts", "vertical", "gpio", "config", "service" };
        public static readonly string[] Severities = { "info", "warning", "error" };

        public static string PartLabel(string part)
        {
            switch (part)
            {
                case "folders": return "Folder policies";
                case "programs": return "Watchdog";
                case "scripts": return "Scripts";
                case "vertical": return "Vertical conversion";
                case "gpio": return "GPIO";
                case "config": return "Configuration file";
                case "service": return "Service start";
            }
            return part;
        }

        public static int SeverityRank(string s)
        {
            switch (s)
            {
                case "error": return 3;
                case "warning": return 2;
                case "info": return 1;
            }
            return 0;
        }

        /// <summary>The settings a new configuration starts with: everything to the Windows Event Log.</summary>
        public static AlertCfg Defaults()
        {
            AlertCfg a = new AlertCfg();
            AlertDestination ev = new AlertDestination();
            ev.Id = "eventlog";
            ev.Name = "Windows Event Log";
            ev.Type = "eventlog";
            a.Destinations.Add(ev);
            AlertRule r = new AlertRule();
            r.Id = "all";
            r.Name = "All alerts";
            r.Destinations.Add("eventlog");
            a.Rules.Add(r);
            return a;
        }

        /// <summary>Convert the single-email / single-webhook alert settings of configuration version 2.</summary>
        public static AlertCfg FromVersion2(JObject old)
        {
            AlertCfg a = new AlertCfg();
            List<string> ids = new List<string>();
            if (old == null) return Defaults();
            if (old.Bool("event_log", true))
            {
                AlertDestination ev = new AlertDestination();
                ev.Id = "eventlog";
                ev.Name = "Windows Event Log";
                ev.Type = "eventlog";
                a.Destinations.Add(ev);
                ids.Add(ev.Id);
            }
            if (old.Str("smtp_host", "").Trim().Length > 0)
            {
                AlertDestination m = new AlertDestination();
                m.Id = "email";
                m.Name = "Email";
                m.Type = "email";
                m.Enabled = old.Bool("email_enabled", false);
                m.SmtpHost = old.Str("smtp_host", "");
                m.SmtpPort = (int)old.Num("smtp_port", 587);
                string tls = old.Str("smtp_tls", "");
                if (tls.Length == 0) tls = old.Bool("smtp_ssl", true) ? (m.SmtpPort == 465 ? "implicit" : "starttls") : "none";
                m.SmtpTls = tls;
                // "accept any certificate" of version 2 is not carried over: a test shows the fingerprint to trust instead
                m.SmtpUser = old.Str("smtp_user", "");
                m.SmtpPassword = old.Str("smtp_password", "");
                m.EmailFrom = old.Str("email_from", "");
                m.EmailTo = old.Str("email_to", "");
                m.SmtpTimeoutSeconds = (int)old.Num("smtp_timeout_seconds", 20);
                a.Destinations.Add(m);
                ids.Add(m.Id);
            }
            if (old.Str("webhook_url", "").Trim().Length > 0)
            {
                AlertDestination w = new AlertDestination();
                w.Id = "webhook";
                w.Name = "Webhook";
                w.Type = "webhook";
                w.Enabled = old.Bool("webhook_enabled", false);
                w.Url = old.Str("webhook_url", "");
                w.Format = old.Str("webhook_format", "text");
                w.Template = old.Str("webhook_template", "");
                w.Proxy = old.Str("webhook_proxy", "system");
                w.ProxyUrl = old.Str("webhook_proxy_url", "");
                a.Destinations.Add(w);
                ids.Add(w.Id);
            }
            AlertRule r = new AlertRule();
            r.Id = "all";
            r.Name = "All alerts";
            r.MinSeverity = old.Bool("on_program_restart", false) || old.Bool("on_service_start", true) ? "warning" : "error";
            r.CooldownMinutes = (int)old.Num("cooldown_minutes", 30);
            r.Destinations.AddRange(ids);
            a.Rules.Add(r);
            return a;
        }

        static string C(string v, string[] choices, string d)
        {
            string x = (v ?? d).Trim().ToLowerInvariant();
            return Array.IndexOf(choices, x) >= 0 ? x : d;
        }

        static string S(string v) { return (v ?? "").Trim(); }

        static int I(int v, int lo, int hi) { return Math.Max(lo, Math.Min(hi, v)); }

        public static void Normalize(AlertCfg a)
        {
            a.Destinations = a.Destinations ?? new List<AlertDestination>();
            a.Rules = a.Rules ?? new List<AlertRule>();
            List<string> seen = new List<string>();
            for (int i = 0; i < a.Destinations.Count; i++)
            {
                AlertDestination d = a.Destinations[i] ?? new AlertDestination();
                a.Destinations[i] = d;
                d.Id = Cfg.UniqueId(d.Id, seen);
                d.Name = S(d.Name).Length == 0 ? "Destination" : S(d.Name);
                d.Type = C(d.Type, new[] { "eventlog", "email", "webhook", "jira" }, "eventlog");
                NormalizeDestination(d);
            }
            seen = new List<string>();
            for (int i = 0; i < a.Rules.Count; i++)
            {
                AlertRule r = a.Rules[i] ?? new AlertRule();
                a.Rules[i] = r;
                r.Id = Cfg.UniqueId(r.Id, seen);
                r.Name = S(r.Name).Length == 0 ? "Rule" : S(r.Name);
                r.Parts = Clean(r.Parts, Parts);
                r.Items = Clean(r.Items, null);
                r.MinSeverity = C(r.MinSeverity, Severities, "warning");
                List<string> dests = new List<string>();
                foreach (string id in r.Destinations ?? new List<string>())
                    if (!string.IsNullOrEmpty(id) && !dests.Contains(id) && a.Destinations.Exists(delegate(AlertDestination d) { return d.Id == id; })) dests.Add(id);
                r.Destinations = dests;
                r.CooldownMinutes = I(r.CooldownMinutes, 0, 10080);
                r.Notes = S(r.Notes);
            }
        }

        public static void NormalizeDestination(AlertDestination d)
        {
            d.SmtpHost = S(d.SmtpHost);
            d.SmtpPort = I(d.SmtpPort, 1, 65535);
            d.SmtpTls = C(d.SmtpTls, new[] { "starttls", "implicit", "none" }, "starttls");
            d.SmtpTrustFingerprint = S(d.SmtpTrustFingerprint);
            d.SmtpUser = S(d.SmtpUser);
            d.SmtpPassword = d.SmtpPassword ?? "";
            d.EmailFrom = S(d.EmailFrom);
            d.EmailTo = S(d.EmailTo);
            d.SmtpTimeoutSeconds = I(d.SmtpTimeoutSeconds, 5, 120);
            d.Url = S(d.Url);
            d.Format = C(d.Format, new[] { "text", "teams", "discord", "json", "jira_automation", "custom" }, "text");
            d.Template = d.Template ?? "";
            d.Headers = d.Headers ?? "";
            d.Secret = d.Secret ?? "";
            d.Proxy = C(d.Proxy, new[] { "system", "none", "manual" }, "none");
            d.ProxyUrl = S(d.ProxyUrl);
            d.JiraUrl = S(d.JiraUrl).TrimEnd('/');
            d.JiraAuth = C(d.JiraAuth, new[] { "basic", "bearer" }, "basic");
            d.JiraUser = S(d.JiraUser);
            d.JiraToken = d.JiraToken ?? "";
            d.JiraProject = S(d.JiraProject).ToUpperInvariant();
            d.JiraIssueType = S(d.JiraIssueType).Length == 0 ? "Task" : S(d.JiraIssueType);
            d.JiraPriority = S(d.JiraPriority);
            d.JiraLabels = S(d.JiraLabels);
            d.Notes = S(d.Notes);
        }

        static List<string> Clean(List<string> list, string[] allowed)
        {
            List<string> o = new List<string>();
            if (list == null) return o;
            foreach (string raw in list)
            {
                string x = S(raw);
                if (x.Length == 0 || o.Contains(x)) continue;
                if (allowed != null && Array.IndexOf(allowed, x) < 0) continue;
                o.Add(x);
            }
            return o;
        }

        public static List<string> ValidateDestination(AlertDestination d, string label)
        {
            List<string> e = new List<string>();
            switch (d.Type)
            {
                case "email":
                    if (d.SmtpHost.Length == 0) e.Add(label + ": SMTP server is required");
                    if (d.EmailFrom.Length == 0) e.Add(label + ": sender address is required");
                    if (Cfg.SplitAddresses(d.EmailTo).Count == 0) e.Add(label + ": at least one recipient is required");
                    if (d.SmtpUser.Length == 0 && d.SmtpPassword.Length > 0) e.Add(label + ": a password is set but no user name");
                    if (d.SmtpUser.Length > 0 && d.SmtpTls == "none") e.Add(label + ": a login is only sent over an encrypted connection; choose STARTTLS or implicit TLS, or leave the user name empty for a relay");
                    if (d.SmtpTrustFingerprint.Length > 0 && SmtpSender.Clean(d.SmtpTrustFingerprint).Length != 64) e.Add(label + ": the certificate fingerprint must be the 64 hexadecimal characters of a SHA-256 fingerprint");
                    break;
                case "webhook":
                    {
                        Uri u = null;
                        string url = Secrets.Peek(d.Url);          // null: encrypted, checked when it was typed in
                        if (url != null && (!Uri.TryCreate(url, UriKind.Absolute, out u) || (u.Scheme != "http" && u.Scheme != "https")))
                            e.Add(label + ": webhook address must start with http:// or https://");
                        if (url != null && u != null && u.Scheme == "http" && !u.IsLoopback)
                            e.Add(label + ": the webhook address must start with https:// (http:// only for this computer, 127.0.0.1 or localhost)");
                        if (d.Proxy == "manual" && d.ProxyUrl.Length == 0) e.Add(label + ": a proxy address is required when 'this proxy' is chosen");
                        foreach (string line in (Secrets.Peek(d.Headers) ?? "").Split('\n'))
                            if (line.Trim().Length > 0 && line.IndexOf(':') <= 0) e.Add(label + ": header line '" + line.Trim() + "' must look like Name: value");
                        if (d.Format == "custom")
                        {
                            try { Json.Parse(AlertManager.WebhookBody(d, Sample(), "Subject", "Body")); }
                            catch (Exception ex) { e.Add(label + ": the custom body is not valid JSON once filled in (" + ex.Message + ")"); }
                        }
                        break;
                    }
                case "jira":
                    {
                        Uri u;
                        if (!Uri.TryCreate(d.JiraUrl, UriKind.Absolute, out u) || (u.Scheme != "https" && !(u.Scheme == "http" && u.IsLoopback)))
                            e.Add(label + ": Jira address must start with https:// (e.g. https://yoursite.atlassian.net); the login is never sent unencrypted");
                        if (d.JiraProject.Length == 0) e.Add(label + ": Jira project key is required (e.g. OPS)");
                        if (d.JiraToken.Length == 0) e.Add(label + ": Jira API token (or password) is required");
                        if (d.JiraAuth == "basic" && d.JiraUser.Length == 0) e.Add(label + ": Jira user (Cloud: your Atlassian email) is required");
                        break;
                    }
            }
            return e;
        }

        public static AlertRecord Sample()
        {
            AlertRecord r = new AlertRecord();
            r.Title = "Title";
            r.Message = "Message \"quoted\"\nline 2";
            r.Kind = "test";
            r.Severity = "info";
            r.Part = "service";
            return r;
        }

        public static List<string> Validate(AlertCfg a)
        {
            List<string> e = new List<string>();
            for (int i = 0; i < a.Destinations.Count; i++)
                if (a.Destinations[i].Enabled) e.AddRange(ValidateDestination(a.Destinations[i], "alert destination " + (i + 1) + " (" + a.Destinations[i].Name + ")"));
            for (int i = 0; i < a.Rules.Count; i++)
            {
                AlertRule r = a.Rules[i];
                string l = "alert rule " + (i + 1) + " (" + r.Name + ")";
                if (r.Enabled && r.Destinations.Count == 0) e.Add(l + ": choose at least one destination");
                if (r.Enabled && !r.AllParts && r.Parts.Count == 0) e.Add(l + ": choose the parts it applies to, or 'all alerts'");
            }
            return e;
        }

        /// <summary>Plain-language summary of a rule (shown in the manager).</summary>
        public static string Describe(AlertRule r, AlertCfg a)
        {
            List<string> parts = new List<string>();
            foreach (string p in r.Parts) parts.Add(PartLabel(p));
            string what = r.AllParts ? "All alerts" : (parts.Count > 0 ? string.Join(", ", parts.ToArray()) : "(no parts chosen)") +
                                                     (r.Items.Count > 0 ? " (" + r.Items.Count + " chosen entr" + (r.Items.Count == 1 ? "y" : "ies") + ")" : "");
            string sev = r.MinSeverity == "error" ? "errors" : r.MinSeverity == "warning" ? "warnings and errors" : "everything";
            List<string> dests = new List<string>();
            foreach (string id in r.Destinations)
            {
                AlertDestination d = a.Destinations.Find(delegate(AlertDestination x) { return x.Id == id; });
                if (d != null) dests.Add(d.Name);
            }
            return what + ", " + sev + (r.Recoveries ? " and recoveries" : "") + " > " + (dests.Count > 0 ? string.Join(", ", dests.ToArray()) : "(no destination)") +
                   (r.CooldownMinutes > 0 ? ", at most every " + r.CooldownMinutes + " min" : "") + (r.Stop ? ". Stop here" : "");
        }
    }
}
