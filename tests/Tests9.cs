// Jira: issue per alert, comments for repeats and recoveries, connection check and explained failures.
using System;
using System.Collections.Generic;
using System.Threading;
using Watchkeeper;

namespace Watchkeeper.Tests
{
    public class JiraTests : Env
    {
        static AlertDestination Jira(FakeJira j)
        {
            AlertDestination d = new AlertDestination();
            d.Id = "jira";
            d.Name = "Jira OPS";
            d.Type = "jira";
            d.JiraUrl = j.Url;
            d.JiraUser = "ops@example.com";
            d.JiraToken = Secrets.Protect("tok");
            d.JiraProject = "OPS";
            d.JiraIssueType = "Task";
            d.JiraLabels = "watchkeeper, broadcast";
            d.Proxy = "none";
            AlertSettings.NormalizeDestination(d);
            return d;
        }

        static AlertRecord Rec(string kind, string key, string title)
        {
            AlertRecord r = new AlertRecord();
            r.Kind = kind;
            r.Key = key;
            r.Title = title;
            r.Severity = AlertManager.SeverityOf(kind);
            return r;
        }

        FakeJira Start()
        {
            try { return new FakeJira(); }
            catch (Exception ex) { throw new SkipException("cannot listen: " + ex.Message); }
        }

        [Fact]
        public void One_issue_per_alert_with_comments_for_repeats_and_recovery()
        {
            using (FakeJira j = Start())
            {
                AlertDestination d = Jira(j);
                string r1 = JiraClient.Send(d, Rec("program_problem", "program:v:health", "vMix is not answering"), "STUDIO1", "[STUDIO1] ERROR: vMix is not answering", "body 1", new NetTrace());
                Assert.Eq("created OPS-1", r1);
                Assert.Eq(new[] { "watchkeeper", "broadcast", JiraClient.Label("STUDIO1", "program:v:health") }, j.Issues[0].Labels.ToArray());
                Assert.Eq("[STUDIO1] ERROR: vMix is not answering", j.Issues[0].Summary);
                string r2 = JiraClient.Send(d, Rec("program_problem", "program:v:health", "vMix is not answering"), "STUDIO1", "s", "body 2", new NetTrace());
                Assert.Eq("commented on OPS-1", r2);
                Assert.Eq(1, j.Issues.Count);
                Assert.Contains(j.Issues[0].Comments[0], "Happened again");
                string other = JiraClient.Send(d, Rec("program_problem", "program:v:health", "x"), "STUDIO2", "s", "b", new NetTrace());
                Assert.Eq("created OPS-2", other);                                   // same alert, other server: its own issue
                string rec = JiraClient.Send(d, Rec("recovered", "program:v:health", "vMix answers again"), "STUDIO1", "s", "fine", new NetTrace());
                Assert.Eq("commented on OPS-1", rec);
                Assert.Contains(j.Issues[0].Comments[1], "Back to normal");
                j.Issues[0].Done = true;
                Assert.Eq("created OPS-3", JiraClient.Send(d, Rec("program_problem", "program:v:health", "again"), "STUDIO1", "s", "b", new NetTrace()));
                Assert.Eq("no open issue to update", JiraClient.Send(d, Rec("recovered", "folder:x", "ok"), "STUDIO1", "s", "b", new NetTrace()));
                d.JiraReuseOpen = false;
                Assert.Eq("created OPS-4", JiraClient.Send(d, Rec("program_problem", "program:v:health", "again"), "STUDIO1", "s", "b", new NetTrace()));
                Assert.True(j.Requests.Exists(delegate(string r) { return r.StartsWith("GET /rest/api/2/search?jql="); }), "Data Center search");
            }
        }

        [Fact]
        public void Check_and_explained_failures()
        {
            using (FakeJira j = Start())
            {
                AlertDestination d = Jira(j);
                Assert.Contains(JiraClient.Check(d, new NetTrace()), "Logged in as Ops Bot; project OPS (Operations)");
                d.JiraIssueType = "Incident";
                NetFailure t = Assert.Throws<NetFailure>(delegate { JiraClient.Check(d, new NetTrace()); });
                Assert.Contains(t.Message, "it has: Task, Bug");
                NetFailure c = Assert.Throws<NetFailure>(delegate { JiraClient.Create(d, "s", "b", new List<string>(), new NetTrace()); });
                Assert.Contains(c.Message, "issuetype: Specify a valid issue type");
                d.JiraIssueType = "Task";
                d.JiraProject = "NOPE";
                NetFailure p = Assert.Throws<NetFailure>(delegate { JiraClient.Check(d, new NetTrace()); });
                Assert.Contains(p.Message, "HTTP 404");
                Assert.Contains(p.Message, "No project could be found");
                d.JiraToken = Secrets.Protect("wrong");
                NetFailure a = Assert.Throws<NetFailure>(delegate { JiraClient.Check(d, new NetTrace()); });
                Assert.Contains(a.Message, "HTTP 401");
                Assert.Contains(a.Message, "login was refused");
            }
            AlertDestination cloud = new AlertDestination();
            cloud.JiraUrl = "https://example.atlassian.net";
            Assert.True(JiraClient.IsCloud(cloud));
        }

        [Fact]
        public void Alert_rule_to_jira_through_the_service_path()
        {
            using (FakeJira j = Start())
            {
                AlertDestination d = Jira(j);
                Set<AlertCfg>("alerts", delegate(AlertCfg a)
                {
                    a.Destinations.Add(d);
                    a.Rules[0].Destinations = new List<string> { "jira" };
                    a.Rules[0].CooldownMinutes = 0;
                });
                AlertManager m = new AlertManager(Cfgs.Get, Hub);
                m.Start();
                m.Raise("task_failure", "folder:abc", "Move ISO failed", "Disk full");
                Assert.True(WaitFor(delegate { return m.Recent().Count > 0 && m.Recent()[0].Sent.Contains("created OPS-1"); }, 10), m.Recent().Count > 0 ? m.Recent()[0].Sent : "");
                m.Raise("recovered", "folder:abc", "Move ISO works again", "ok");
                Assert.True(WaitFor(delegate { return m.Recent()[0].Sent.Contains("commented on OPS-1"); }, 10), m.Recent()[0].Sent);
                Assert.Contains(j.Issues[0].Description, "Disk full");
                ChannelTestResult check = m.Test(d, "check");
                Assert.True(check.Ok, check.Message);
                Assert.Contains(check.Detail, "GET " + j.Url + "/rest/api/2/myself");
                m.Stop(1000);
            }
        }
    }
}
