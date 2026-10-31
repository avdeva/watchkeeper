// Jira destination: an alert opens an issue; while that issue is open, the same alert again and its "back to normal"
// message are added as comments. Each alert is tied to its issue by a label (wk-<hash of server and alert>), so nothing
// has to be remembered between restarts. Jira Cloud: account e-mail + API token. Server / Data Center: personal access token.
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Watchkeeper
{
    public static class Jira
    {
        public static bool IsCloud(AlertDestination d)
        {
            Uri u;
            return Uri.TryCreate(d.JiraUrl, UriKind.Absolute, out u) && u.Host.ToLowerInvariant().EndsWith(".atlassian.net");
        }

        static HttpResult Call(AlertDestination d, string token, string method, string path, string body)
        {
            Dictionary<string, string> h = new Dictionary<string, string>();
            h["Accept"] = "application/json";
            string user = null, pw = null;
            if (d.JiraUser.Length > 0) { user = d.JiraUser; pw = token; }
            else h["Authorization"] = "Bearer " + token;
            return Http.Send(method, d.JiraUrl.TrimEnd('/') + path, body, "application/json", h, user, pw, 20000);
        }

        static Exception Fail(AlertDestination d, string what, HttpResult r)
        {
            if (!r.Answered) return new InvalidOperationException("Jira could not be reached (" + what + "): " + r.Error);
            string detail = Util.OneLine(r.Body, 300);
            try
            {
                JObject o = Json.Parse(r.Body) as JObject;
                List<string> parts = new List<string>();
                List<object> msgs = o == null ? null : o.Get("errorMessages") as List<object>;
                if (msgs != null) foreach (object m in msgs) if (m is string && ((string)m).Length > 0) parts.Add((string)m);
                JObject errs = o == null ? null : o.Get("errors") as JObject;
                if (errs != null) foreach (string k in errs.Keys) parts.Add(k + ": " + errs.Str(k, ""));
                if (parts.Count > 0) detail = string.Join("; ", parts.ToArray());
            }
            catch (Exception) { }
            string hint = "";
            if (r.Status == 401) hint = IsCloud(d) ? " The login was refused: use the Atlassian account e-mail and an API token." : " The login was refused: check the personal access token.";
            else if (r.Status == 403) hint = " The account may not create issues in project " + d.JiraProject + ".";
            else if (r.Status == 404) hint = " Check the Jira address and the project key " + d.JiraProject + ".";
            else if (r.Status == 400) hint = " Check the issue type '" + d.JiraIssueType + "' and the labels.";
            else if (r.Status >= 300 && r.Status < 400) hint = " Jira redirected the request: check the address.";
            return new InvalidOperationException("Jira answered HTTP " + r.Status + " (" + what + "): " + detail + "." + hint);
        }

        /// <summary>Check the login, the project and the issue type without creating anything.</summary>
        public static string Check(AlertDestination d)
        {
            string token = Secrets.Reveal(d.JiraToken);
            HttpResult me = Call(d, token, "GET", "/rest/api/2/myself", null);
            if (!me.Success) throw Fail(d, "checking the login", me);
            JObject who = Json.Parse(me.Body) as JObject;
            string name = who == null ? "?" : who.Str("displayName", who.Str("name", "?"));
            HttpResult pr = Call(d, token, "GET", "/rest/api/2/project/" + Uri.EscapeDataString(d.JiraProject), null);
            if (!pr.Success) throw Fail(d, "reading project " + d.JiraProject, pr);
            JObject proj = Json.Parse(pr.Body) as JObject;
            List<string> types = new List<string>();
            List<object> it = proj == null ? null : proj.Get("issueTypes") as List<object>;
            if (it != null) foreach (object t in it) { JObject jt = t as JObject; if (jt != null) types.Add(jt.Str("name", "")); }
            if (types.Count > 0 && !types.Exists(delegate(string x) { return string.Equals(x, d.JiraIssueType, StringComparison.OrdinalIgnoreCase); }))
                throw new InvalidOperationException("Logged in as " + name + ", but project " + d.JiraProject + " has no issue type '" + d.JiraIssueType + "'; it has: " + string.Join(", ", types.ToArray()) + ".");
            return "Logged in as " + name + "; issues can be created in " + d.JiraProject + " (" + (proj == null ? "" : proj.Str("name", "")) + "). No test issue was created.";
        }

        public static string Label(string server, string key)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes((server ?? "").ToLowerInvariant() + "|" + key));
                return "wk-" + BitConverter.ToString(h, 0, 6).Replace("-", "").ToLowerInvariant();
            }
        }

        static string Quote(string s) { return "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""; }

        static string FindOpen(AlertDestination d, string token, string label)
        {
            string jql = "project = " + Quote(d.JiraProject) + " AND labels = " + Quote(label) + " AND statusCategory != Done ORDER BY created DESC";
            string path = (IsCloud(d) ? "/rest/api/3/search/jql" : "/rest/api/2/search") + "?jql=" + Uri.EscapeDataString(jql) + "&fields=summary&maxResults=1";
            HttpResult r = Call(d, token, "GET", path, null);
            if (!r.Success) throw Fail(d, "searching for an open issue", r);
            JObject o = Json.Parse(r.Body) as JObject;
            List<object> issues = o == null ? null : o.Get("issues") as List<object>;
            if (issues == null || issues.Count == 0) return null;
            JObject first = issues[0] as JObject;
            return first == null ? null : first.Str("key", null);
        }

        static string Plain(string text) { return "{noformat}\n" + (text ?? "").Replace("{noformat}", "{ noformat }") + "\n{noformat}"; }

        static void Comment(AlertDestination d, string token, string issue, string text)
        {
            JObject body = new JObject();
            body.Set("body", text);
            HttpResult r = Call(d, token, "POST", "/rest/api/2/issue/" + Uri.EscapeDataString(issue) + "/comment", Json.Serialize(body, false));
            if (!r.Success) throw Fail(d, "adding a comment to " + issue, r);
        }

        /// <summary>Send one alert; returns what happened ("created OPS-12", "commented on OPS-12").</summary>
        public static string Send(AlertDestination d, Alert a, string server, string subject, string body)
        {
            string token = Secrets.Reveal(d.JiraToken);
            string label = Label(server, a.Key);
            if (a.Recovery)
            {
                string open = FindOpen(d, token, label);
                if (open == null) return "no open issue to update";
                Comment(d, token, open, "Back to normal: " + a.Title + "\n\n" + Plain(body));
                return "commented on " + open;
            }
            if (a.Part != "test")
            {
                string open = FindOpen(d, token, label);
                if (open != null) { Comment(d, token, open, "Happened again: " + a.Title + "\n\n" + Plain(body)); return "commented on " + open; }
            }
            JObject fields = new JObject();
            JObject project = new JObject(); project.Set("key", d.JiraProject); fields.Set("project", project);
            JObject type = new JObject(); type.Set("name", d.JiraIssueType); fields.Set("issuetype", type);
            string summary = subject.Replace("\r", " ").Replace("\n", " ");
            fields.Set("summary", summary.Length > 250 ? summary.Substring(0, 250) : summary);
            fields.Set("description", Plain(body));
            List<object> labels = new List<object>();
            foreach (string x in d.JiraLabels.Split(',', ';', ' ')) if (x.Trim().Length > 0 && !labels.Contains(x.Trim())) labels.Add(x.Trim());
            labels.Add(label);
            fields.Set("labels", labels);
            JObject req = new JObject();
            req.Set("fields", fields);
            HttpResult r = Call(d, token, "POST", "/rest/api/2/issue", Json.Serialize(req, false));
            if (!r.Success) throw Fail(d, "creating the issue", r);
            JObject o = Json.Parse(r.Body) as JObject;
            string key = o == null ? "" : o.Str("key", "");
            if (key.Length == 0) throw new InvalidOperationException("Jira returned no issue key: " + Util.OneLine(r.Body, 200));
            return "created " + key;
        }
    }
}
