// Security: alert text cannot carry links, mentions or markup into chat tools and Jira.
using System;
using System.Collections.Generic;
using Watchkeeper;

namespace Watchkeeper.Tests
{
    public class AlertTextTests
    {
        static AlertRecord Rec(string msg)
        {
            AlertRecord r = new AlertRecord();
            r.Title = "Script failed";
            r.Message = msg;
            r.Kind = "task_failure";
            r.Severity = "error";
            r.Key = "script:x";
            return r;
        }

        [Fact]
        public void Alert_text_cannot_make_links_mentions_or_markup()
        {
            string evil = "<!channel> <https://evil.example|Click here> [login](https://evil.example) @everyone {noformat}[x|https://evil.example]";
            AlertDestination d = new AlertDestination();
            d.Type = "webhook";
            d.Format = "text";
            JObject slack = (JObject)Json.Parse(AlertManager.WebhookBody(d, Rec(evil), "[S] ERROR: x\nInjected", evil));
            string text = slack.Str("text", "");
            Assert.False(text.Contains("<!channel>") || text.Contains("<https://"), text);
            Assert.Contains(text, "&lt;!channel&gt;");
            Assert.False(text.Contains("\nInjected\n"), "the title stays one line");

            d.Format = "discord";
            JObject discord = (JObject)Json.Parse(AlertManager.WebhookBody(d, Rec(evil), "[S] x", evil));
            string dc = discord.Str("content", "");
            Assert.Contains(dc, "\\[login\\]\\(https\\[:\\]//evil\\.\u200Bexample\\)");
            Assert.False(dc.Contains("://"), dc);
            Assert.Eq(0, ((List<object>)((JObject)discord.Get("allowed_mentions")).Get("parse")).Count);

            d.Format = "teams";
            JObject teams = (JObject)Json.Parse(AlertManager.WebhookBody(d, Rec(evil), "[S] x", evil));
            string card = Json.Serialize(teams, false);
            Assert.False(card.Contains("https://"), card);
            Assert.Contains(card, "\\\\[login\\\\]\\\\(https\\\\[:\\\\]//evil");
            Assert.Contains(text, "https[:]//evil.\u200Bexample");

            string jira = JiraClient.NoFormat(evil);
            Assert.True(jira.StartsWith("{noformat}\n") && jira.EndsWith("\n{noformat}"), jira);
            Assert.Eq(2, jira.Split(new[] { "{noformat}" }, StringSplitOptions.None).Length - 1);
        }
    }
}
