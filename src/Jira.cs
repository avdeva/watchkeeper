// Jira alert destination over the REST API: an alert opens an issue; while that issue is still
// open, repeats of the same alert and its "back to normal" message are added as comments instead
// of opening more issues. Each alert is tied to its issue by a label (wk-<hash of server and alert>).
// Jira Cloud: email + API token (basic). Jira Data Center: user + password (basic) or a personal
// access token (bearer). Issues are created through /rest/api/2 (plain-text description, both
// editions); searching uses /rest/api/3/search/jql on Cloud and /rest/api/2/search on Data Center.
using System;
using System.Collections.Generic;
using System.Text;

namespace Watchkeeper
{
    public static class JiraClient
    {
        public static bool IsCloud(AlertDestination d)
        {
            Uri u;
            return Uri.TryCreate(d.JiraUrl, UriKind.Absolute, out u) && u.Host.ToLowerInvariant().EndsWith(".atlassian.net");
        }

        static HttpRequestSpec Request(AlertDestination d, string method, string path, string body)
        {
            HttpRequestSpec s = new HttpRequestSpec();
            s.Method = method;
            s.Url = d.JiraUrl.TrimEnd('/') + path;
            s.Body = body ?? "";
            s.ContentType = "application/json";
            s.Proxy = d.Proxy;
            s.ProxyUrl = Secrets.Unprotect(d.ProxyUrl);
            s.ProxyLogin = d.ProxyLogin;
            s.TimeoutMs = 20000;
            s.TracePath = true;          // Jira paths are not secrets (the login is in a header)
            s.MaxBody = 20000;
            string token = Secrets.Unprotect(d.JiraToken);
            if (d.JiraAuth == "bearer") s.Header("Authorization", "Bearer " + token);
            else { s.User = d.JiraUser; s.Password = token; }
            return s;
        }

        static string Errors(HttpResult r)
        {
            try
            {
                JObject o = Json.Parse(r.Body) as JObject;
                if (o != null)
                {
                    List<string> parts = new List<string>();
                    List<object> msgs = o.Get("errorMessages") as List<object>;
                    if (msgs != null) foreach (object m in msgs) if (m is string && ((string)m).Length > 0) parts.Add((string)m);
                    JObject errs = o.Get("errors") as JObject;
                    if (errs != null) foreach (string k in errs.Keys) parts.Add(k + ": " + errs.Str(k, ""));
                    if (parts.Count > 0) return string.Join("; ", parts.ToArray());
                }
            }
            catch (Exception) { }
            return Net.OneLine(r.Body, 300);
        }

        static NetFailure Fail(AlertDestination d, string what, HttpResult r)
        {
            if (!r.Answered) return new NetFailure("jira", "Jira could not be reached (" + what + "): " + r.Error, "", null);
            if (r.Redirect.Length > 0) return new NetFailure("jira", "Jira was not used (" + what + "): " + r.Redirect + ". Check the Jira address.", "", null);
            string hint = "";
            if (r.Status == 401) hint = IsCloud(d) ? " The login was refused: use your Atlassian account email as the user and an API token (id.atlassian.com > Security > API tokens) as the token."
                                                   : " The login was refused: check the user and password, or use a personal access token with 'Bearer'.";
            else if (r.Status == 403) hint = " The account may not browse or create issues in project " + d.JiraProject + ".";
            else if (r.Status == 404) hint = " Check the Jira address and the project key " + d.JiraProject + " (and that the account can see the project).";
            else if (r.Status == 400) hint = " Jira refused the fields: check the issue type '" + d.JiraIssueType + "'" + (d.JiraPriority.Length > 0 ? ", priority '" + d.JiraPriority + "'" : "") + " and the labels.";
            return new NetFailure("jira", "Jira answered HTTP " + r.Status + " (" + what + "): " + Errors(r) + "." + hint, "", null);
        }

        /// <summary>The label tying an alert (by server and alert key) to its issue.</summary>
        public static string Label(string server, string key)
        {
            return "wk-" + Cfg.Hash((server ?? "").ToLowerInvariant() + "|" + key);
        }

        static string Jql(string s) { return "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""; }

        /// <summary>Key of the newest issue with this label that is not done, or null.</summary>
        public static string FindOpen(AlertDestination d, string label, NetTrace trace)
        {
            string jql = "project = " + Jql(d.JiraProject) + " AND labels = " + Jql(label) + " AND statusCategory != Done ORDER BY created DESC";
            string path = (IsCloud(d) ? "/rest/api/3/search/jql" : "/rest/api/2/search") +
                          "?jql=" + Uri.EscapeDataString(jql) + "&fields=summary&maxResults=1";
            HttpResult r = Net.Send(Request(d, "GET", path, null), trace);
            if (!r.Success) throw Fail(d, "searching for an open issue", r);
            JObject o = Json.Parse(r.Body) as JObject;
            List<object> issues = o != null ? o.Get("issues") as List<object> : null;
            if (issues == null || issues.Count == 0) return null;
            JObject first = issues[0] as JObject;
            return first != null ? first.Str("key", null) : null;
        }

        public static string Create(AlertDestination d, string summary, string description, List<string> labels, NetTrace trace)
        {
            JObject fields = new JObject();
            JObject project = new JObject();
            project.Set("key", d.JiraProject);
            fields.Set("project", project);
            JObject type = new JObject();
            type.Set("name", d.JiraIssueType);
            fields.Set("issuetype", type);
            summary = (summary ?? "").Replace("\r", " ").Replace("\n", " ");
            fields.Set("summary", summary.Length > 250 ? summary.Substring(0, 250) : summary);
            fields.Set("description", NoFormat(description));
            List<object> l = new List<object>();
            foreach (string x in labels) l.Add(x);
            fields.Set("labels", l);
            if (d.JiraPriority.Length > 0)
            {
                JObject pr = new JObject();
                pr.Set("name", d.JiraPriority);
                fields.Set("priority", pr);
            }
            JObject body = new JObject();
            body.Set("fields", fields);
            HttpResult r = Net.Send(Request(d, "POST", "/rest/api/2/issue", Json.Serialize(body, false)), trace);
            if (!r.Success) throw Fail(d, "creating the issue", r);
            JObject o = Json.Parse(r.Body) as JObject;
            string key = o != null ? o.Str("key", "") : "";
            if (key.Length == 0) throw new NetFailure("jira", "Jira created something but returned no issue key: " + Net.OneLine(r.Body, 200), "", null);
            return key;
        }

        /// <summary>Alert text in a Jira description or comment is shown as plain text, never as wiki markup (links, mentions).</summary>
        public static string NoFormat(string text)
        {
            string t = (text ?? "").Replace("{noformat}", "{ noformat }");
            return "{noformat}\n" + t + "\n{noformat}";
        }

        public static void Comment(AlertDestination d, string issue, string text, NetTrace trace)
        {
            JObject body = new JObject();
            body.Set("body", text);
            HttpResult r = Net.Send(Request(d, "POST", "/rest/api/2/issue/" + Uri.EscapeDataString(issue) + "/comment", Json.Serialize(body, false)), trace);
            if (!r.Success) throw Fail(d, "adding a comment to " + issue, r);
        }

        static string OneLine(string s)
        {
            string t = (s ?? "").Replace("\r", " ").Replace("\n", " ");
            StringBuilder sb = new StringBuilder();
            foreach (char c in t) { if ("[]{}|!*_^~+-#".IndexOf(c) >= 0) sb.Append('\\'); sb.Append(c); }
            return sb.ToString();
        }

        public static List<string> Labels(AlertDestination d, string label)
        {
            List<string> o = new List<string>();
            foreach (string x in d.JiraLabels.Split(',', ';', ' '))
                if (x.Trim().Length > 0 && !o.Contains(x.Trim())) o.Add(x.Trim());
            o.Add(label);
            return o;
        }

        /// <summary>Send one alert. Returns what happened ("created OPS-12", "commented on OPS-12").</summary>
        public static string Send(AlertDestination d, AlertRecord r, string server, string subject, string body, NetTrace trace)
        {
            string label = Label(server, r.Key);
            if (r.Kind == "recovered")
            {
                if (!d.JiraCommentRecovery) return "recoveries are not sent to this Jira destination";
                string open = FindOpen(d, label, trace);
                if (open == null) return "no open issue to update";
                Comment(d, open, "Back to normal: " + OneLine(r.Title) + "\n\n" + NoFormat(body), trace);
                return "commented on " + open;
            }
            if (d.JiraReuseOpen && r.Kind != "test")
            {
                string open = FindOpen(d, label, trace);
                if (open != null)
                {
                    Comment(d, open, "Happened again: " + OneLine(r.Title) + "\n\n" + NoFormat(body), trace);
                    return "commented on " + open;
                }
            }
            string key = Create(d, subject, body, Labels(d, label), trace);
            return "created " + key;
        }

        /// <summary>Check the login and the project without creating anything.</summary>
        public static string Check(AlertDestination d, NetTrace trace)
        {
            HttpResult me = Net.Send(Request(d, "GET", "/rest/api/2/myself", null), trace);
            if (!me.Success) throw Fail(d, "checking the login", me);
            JObject who = Json.Parse(me.Body) as JObject;
            string name = who != null ? who.Str("displayName", who.Str("name", "?")) : "?";
            HttpResult pr = Net.Send(Request(d, "GET", "/rest/api/2/project/" + Uri.EscapeDataString(d.JiraProject), null), trace);
            if (!pr.Success) throw Fail(d, "reading project " + d.JiraProject, pr);
            JObject proj = Json.Parse(pr.Body) as JObject;
            string pname = proj != null ? proj.Str("name", d.JiraProject) : d.JiraProject;
            List<string> types = new List<string>();
            List<object> it = proj != null ? proj.Get("issueTypes") as List<object> : null;
            if (it != null) foreach (object t in it) { JObject jt = t as JObject; if (jt != null) types.Add(jt.Str("name", "")); }
            string typeNote = "";
            if (types.Count > 0 && !types.Exists(delegate(string x) { return string.Equals(x, d.JiraIssueType, StringComparison.OrdinalIgnoreCase); }))
                typeNote = " Issue type '" + d.JiraIssueType + "' is not in this project; it has: " + string.Join(", ", types.ToArray()) + ".";
            if (typeNote.Length > 0) throw new NetFailure("jira", "Logged in as " + name + ", project " + pname + " found." + typeNote, "", null);
            return "Logged in as " + name + "; project " + d.JiraProject + " (" + pname + ") can be used" + (types.Count > 0 ? " with issue type " + d.JiraIssueType : "") + ".";
        }
    }
}
