// Alerts: rules decide which alerts go where; destinations are the places an alert can go
// (Windows Event Log, email, webhook, Jira). Rules are checked from the top. Tests are sent by
// the service and come back with a step-by-step trace, so a failure shows exactly where it stopped.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace Watchkeeper.UI
{
    public class AlertsPage : SectionPage
    {
        class Opt
        {
            public readonly string Value, Text;
            public Opt(string v, string t) { Value = v; Text = t; }
            public override string ToString() { return Text; }
        }

        const string Unchanged = "••••••••";

        readonly TabControl tabs = new TabControl();
        readonly CheckBox includeOutput = Ui.Chk("Include what a failed script printed in its alert (it can contain anything the script printed, including secrets)");

        // ---- rules
        readonly ListEditor<AlertRule> rules = new ListEditor<AlertRule>("rule");
        readonly CheckBox rOn = Ui.Chk("Switched on");
        readonly TextBox rName = Ui.Text(320);
        readonly RadioButton rAll = Ui.Radio("All alerts, from every part of Watchkeeper");
        readonly RadioButton rSome = Ui.Radio("Only alerts from the parts ticked below");
        readonly CheckedListBox rParts = Checks(320, 7);
        readonly CheckedListBox rItems = Checks(420, 8);
        readonly Label rItemsNote = Ui.Hint("");
        readonly ComboBox rSeverity;
        readonly CheckBox rRecover = Ui.Chk("Also send \"back to normal\" when the problem is over");
        readonly CheckedListBox rDests = Checks(420, 6);
        readonly NumericUpDown rCooldown = Ui.Num(0, 10080, 5, 0);
        readonly CheckBox rStop = Ui.Chk("Stop here: alerts this rule takes are not checked against the rules below it");
        readonly TextBox rNotes = Ui.Text(420);
        readonly Label rSummary = new Label();

        // ---- destinations
        readonly ListEditor<AlertDestination> dests = new ListEditor<AlertDestination>("destination");
        readonly CheckBox dOn = Ui.Chk("Switched on");
        readonly TextBox dName = Ui.Text(320);
        readonly ComboBox dType;
        readonly TextBox dHost = Ui.Text(260);
        readonly NumericUpDown dPort = Ui.Num(1, 65535, 1, 0);
        readonly ComboBox dTls;
        readonly TextBox dFingerprint = Ui.Text(520);
        readonly TextBox dUser = Ui.Text(260);
        readonly TextBox dPassword = Ui.Text(220);
        readonly TextBox dFrom = Ui.Text(260);
        readonly TextBox dTo = Ui.Text(420);
        readonly NumericUpDown dTimeout = Ui.Num(5, 120, 5, 0);
        readonly TextBox dUrl = Ui.Text(520);
        readonly ComboBox dFormat;
        readonly TextBox dTemplate = Multi(520, 90);
        readonly TextBox dHeaders = Multi(520, 56);
        readonly TextBox dSecret = Ui.Text(260);
        readonly TextBox dJiraUrl = Ui.Text(360);
        readonly ComboBox dJiraAuth;
        readonly TextBox dJiraUser = Ui.Text(260);
        readonly TextBox dJiraToken = Ui.Text(260);
        readonly TextBox dProject = Ui.Text(100);
        readonly TextBox dIssueType = Ui.Text(160);
        readonly TextBox dPriority = Ui.Text(160);
        readonly TextBox dLabels = Ui.Text(320);
        readonly CheckBox dReuse = Ui.Chk("While its issue is open, add repeats of the same alert as a comment instead of opening another issue");
        readonly CheckBox dCommentRecovery = Ui.Chk("Add \"back to normal\" as a comment on the open issue");
        readonly ComboBox dProxy;
        readonly TextBox dProxyUrl = Ui.Text(260);
        readonly CheckBox dProxyLogin = Ui.Chk("Send this computer's Windows login to the proxy (only if the proxy requires it)");
        readonly TextBox dNotes = Ui.Text(420);
        readonly Label dUsedBy = Ui.Hint("");
        readonly Button tSend, tCheck, details;
        readonly Label testResult = new Label();
        readonly List<Control> emailRows = new List<Control>();
        readonly List<Control> webhookRows = new List<Control>();
        readonly List<Control> templateRows = new List<Control>();
        readonly List<Control> secretRows = new List<Control>();
        readonly List<Control> jiraRows = new List<Control>();
        readonly List<Control> jiraUserRows = new List<Control>();
        readonly List<Control> netRows = new List<Control>();
        readonly List<Control> eventRows = new List<Control>();

        // ---- recent
        readonly ListView recent = new ListView();

        int fill;
        int busy;
        string recentSig = "";
        string lastDetail = "";
        string lastTitle = "";

        public AlertsPage(Manager m)
            : base(m, "alerts", "Alerts",
                   "Rules decide which alerts go where: everything, or only some parts of Watchkeeper (and some of their entries). " +
                   "Destinations are where alerts go: the Windows Event Log, email, a webhook (Teams, Slack, Discord, Jira Automation...) or a Jira issue.")
        {
            rSeverity = Ui.Combo(new Choice("error", "Errors: something failed, stopped or cannot be reached"),
                                 new Choice("warning", "Warnings and errors: also restarts and service starts"),
                                 new Choice("info", "Everything"));
            dType = Ui.Combo(new Choice("email", "Email (SMTP)"), new Choice("webhook", "Webhook"),
                             new Choice("jira", "Jira issue (REST API)"), new Choice("eventlog", "Windows Event Log"));
            dTls = Ui.Combo(new Choice("starttls", "STARTTLS (port 587)"), new Choice("implicit", "Implicit TLS (port 465)"), new Choice("none", "No encryption (internal relay, port 25)"));
            dFormat = Ui.Combo(new Choice("text", "Slack / Google Chat / Mattermost (text)"), new Choice("teams", "Microsoft Teams (Workflows)"),
                               new Choice("discord", "Discord"), new Choice("jira_automation", "Jira Automation (incoming webhook)"),
                               new Choice("json", "Plain JSON"), new Choice("custom", "Custom JSON"));
            dJiraAuth = Ui.Combo(new Choice("basic", "User + API token or password (Jira Cloud, Data Center)"),
                                 new Choice("bearer", "Personal access token (Data Center)"));
            dProxy = Ui.Combo(new Choice("none", "No proxy (direct)"), new Choice("manual", "This proxy:"), new Choice("system", "Windows proxy settings"));
            rSeverity.Width = 440;
            dJiraAuth.Width = 420;
            dFormat.Width = Math.Max(dFormat.Width, 300);
            dPassword.UseSystemPasswordChar = true;
            dJiraToken.UseSystemPasswordChar = true;
            dSecret.UseSystemPasswordChar = true;

            tSend = Ui.Btn("Send a test", delegate { Test("send"); });
            tCheck = Ui.Btn("Check login and project", delegate { Test("check"); });
            details = Ui.Btn("What happened...", delegate { ShowDetails(); });
            details.Enabled = false;
            foreach (Button b in new[] { tSend, tCheck, details }) b.Tag = "nodirty";
            testResult.AutoSize = true;
            testResult.MaximumSize = new Size(640, 0);
            testResult.Margin = new Padding(0, 4, 0, 0);
            testResult.Tag = "nodirty";

            tabs.Dock = DockStyle.Fill;
            tabs.TabPages.Add(Tab("Rules", BuildRules()));
            tabs.TabPages.Add(Tab("Destinations", BuildDestinations()));
            tabs.TabPages.Add(Tab("Recent alerts", BuildRecent()));
            tabs.SelectedIndexChanged += delegate { OnTab(); };

            FormGrid top = new FormGrid();
            top.Row(null, includeOutput);
            top.Dock = DockStyle.Top;
            Body.AutoScroll = false;
            Body.Controls.Add(tabs);
            Body.Controls.Add(top);
            Init();
        }

        // ================================================================== building

        static CheckedListBox Checks(int width, int lines)
        {
            CheckedListBox c = new CheckedListBox();
            c.CheckOnClick = true;
            c.IntegralHeight = false;
            c.Width = width;
            c.Height = Ui.BaseFont().Height * lines + 8;
            c.Anchor = AnchorStyles.Left;
            c.Margin = new Padding(0, 3, 0, 3);
            return c;
        }

        static TextBox Multi(int width, int height)
        {
            TextBox t = new TextBox();
            t.Multiline = true;
            t.ScrollBars = ScrollBars.Vertical;
            t.Width = width;
            t.Height = height;
            t.Font = Ui.MonoFont(Ui.BaseFont());
            t.Anchor = AnchorStyles.Left;
            return t;
        }

        static TabPage Tab(string title, Control content)
        {
            TabPage p = new TabPage(title);
            p.Padding = new Padding(10, 8, 6, 6);
            p.UseVisualStyleBackColor = true;
            p.Controls.Add(content);
            return p;
        }

        static FlowLayoutPanel Stack(params Control[] items)
        {
            FlowLayoutPanel f = new FlowLayoutPanel();
            f.FlowDirection = FlowDirection.TopDown;
            f.AutoSize = true;
            f.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            f.WrapContents = false;
            f.Margin = new Padding(0);
            foreach (Control c in items) f.Controls.Add(c);
            return f;
        }

        /// <summary>Rows added to the grid by 'add' are collected so they can be shown or hidden together.</summary>
        static void Group(FormGrid g, List<Control> into, MethodInvoker add)
        {
            int before = g.Controls.Count;
            add();
            for (int i = before; i < g.Controls.Count; i++) into.Add(g.Controls[i]);
        }

        static void Show(List<Control> rows, bool visible)
        {
            foreach (Control c in rows) c.Visible = visible;
        }

        Control BuildRules()
        {
            rules.EmptyText = "No rules: alerts are only written to the Watchkeeper log.\r\n\r\nClick \"Add rule\".";
            rules.RemoveNote = "Alerts this rule took are no longer sent by it.";
            rules.Create = delegate
            {
                AlertRule r = new AlertRule();
                r.Id = Cfg.UniqueId("", RuleIds());
                r.Name = "New rule";
                r.AllParts = false;
                r.MinSeverity = "error";
                return r;
            };
            rules.Copy = delegate(AlertRule r)
            {
                AlertRule c = Json.Clone(r);
                c.Id = Cfg.UniqueId("", RuleIds());
                c.Name = r.Name + " (copy)";
                c.Enabled = false;
                return c;
            };
            rules.Title = delegate(AlertRule r) { return r.Name; };
            rules.Subtitle = delegate(AlertRule r) { return RuleLine(r); };
            rules.Badge = delegate(AlertRule r) { return r.MinSeverity == "error" ? "errors" : r.MinSeverity == "warning" ? "warnings" : "everything"; };
            rules.IsOn = delegate(AlertRule r) { return r.Enabled; };

            Opt[] parts = new Opt[AlertSettings.Parts.Length];
            for (int i = 0; i < parts.Length; i++) parts[i] = new Opt(AlertSettings.Parts[i], PartText(AlertSettings.Parts[i]));
            rParts.Items.AddRange(parts);

            rSummary.AutoSize = true;
            rSummary.MaximumSize = new Size(640, 0);
            rSummary.Margin = new Padding(0, 4, 0, 6);
            rSummary.Font = Ui.Derive(Ui.BaseFont(), 1.0f, FontStyle.Bold);

            FormGrid g = new FormGrid();
            g.Row(null, rOn);
            g.Row("Name", rName);
            g.Row(null, rSummary);
            g.Header("Which alerts");
            g.Row("Take", Stack(rAll, rSome));
            g.Row("Parts", rParts);
            g.Row("Entries", rItems);
            g.Row(null, rItemsNote);
            g.Row("Severity", rSeverity, "Errors: a folder policy, script or conversion fails; a program or service cannot be started, keeps stopping or stops answering; " +
                                         "a GPIO device or action fails; the configuration has problems. Warnings: a program or service was restarted, the service started.");
            g.Row(null, rRecover);
            g.Header("Where to");
            g.Row("Send to", rDests, "Destinations are set up on the Destinations tab.");
            Label min = Ui.Lbl("minutes (0 = every time; repeats are counted and mentioned in the next alert)");
            g.Row("Same alert at most once per", Ui.Flow(rCooldown, min));
            g.Row(null, rStop);
            g.Row("Notes", rNotes);
            g.Note("Rules are checked from the top. Every rule that takes an alert sends it to its destinations; a destination gets each alert once even when several rules send to it.");
            rules.Detail.Controls.Add(g);

            EventHandler apply = delegate { ApplyRule(); };
            rOn.CheckedChanged += apply;
            rName.TextChanged += apply;
            rAll.CheckedChanged += apply;
            rSome.CheckedChanged += apply;
            rSeverity.SelectedIndexChanged += apply;
            rRecover.CheckedChanged += apply;
            rCooldown.ValueChanged += apply;
            rStop.CheckedChanged += apply;
            rNotes.TextChanged += apply;
            rParts.ItemCheck += delegate(object s, ItemCheckEventArgs e) { PartsChecked(e); };
            rItems.ItemCheck += delegate(object s, ItemCheckEventArgs e) { ListChecked(rItems, e, true); };
            rDests.ItemCheck += delegate(object s, ItemCheckEventArgs e) { ListChecked(rDests, e, false); };
            rules.SelectedChanged += delegate { FillRule(); };
            rules.Changed += delegate { MarkDirty(); RefreshUsedBy(); };
            return rules;
        }

        Control BuildDestinations()
        {
            dests.EmptyText = "No destinations yet.\r\n\r\nClick \"Add destination\".";
            dests.RemoveNote = "Rules that send to it stop sending there.";
            dests.Create = delegate
            {
                AlertDestination d = new AlertDestination();
                d.Id = Cfg.UniqueId("", DestIds());
                d.Type = "email";
                d.Name = DefaultName("email");
                return d;
            };
            dests.Copy = delegate(AlertDestination d)
            {
                AlertDestination c = Json.Clone(d);
                c.Id = Cfg.UniqueId("", DestIds());
                c.Name = d.Name + " (copy)";
                c.Enabled = false;
                return c;
            };
            dests.Title = delegate(AlertDestination d) { return d.Name; };
            dests.Subtitle = delegate(AlertDestination d) { return DestLine(d); };
            dests.Badge = delegate(AlertDestination d) { return TypeText(d.Type); };
            dests.IsOn = delegate(AlertDestination d) { return d.Enabled; };

            FormGrid g = new FormGrid();
            g.Row(null, dOn);
            g.Row("Name", dName);
            g.Row("Type", dType);
            g.Row(null, dUsedBy);

            Group(g, eventRows, delegate
            {
                g.Note("Writes to the Application log with source \"" + AlertManager.EventSource + "\": errors as Error, warnings as Warning, the rest as Information. " +
                       "Monitoring tools that read the Event Log pick these up.");
            });

            Group(g, emailRows, delegate
            {
                g.Header("Email");
                Label colon = Ui.Lbl("port");
                colon.Margin = new Padding(8, 6, 4, 6);
                g.Row("SMTP server", Ui.Flow(dHost, colon, dPort));
                g.Row("Encryption", dTls, "Microsoft 365 and Gmail: STARTTLS on 587. Port 465 needs implicit TLS. An internal relay on port 25 often has none.");
                g.Row("Trust this certificate", dFingerprint, "Only for an internal mail server with its own certificate: its SHA-256 fingerprint, shown by a failed test. " +
                                                              "Exactly that certificate is then accepted; any other one is refused.");
                g.Row("User name", dUser, "Empty for a relay that accepts this server by its IP address.");
                g.Row("Password", dPassword, "Stored encrypted for this computer. Leave the dots to keep the saved password.");
                g.Row("From", dFrom, "Usually the same address as the user name.");
                g.Row("To", dTo, "Comma separated.");
                Label secs = Ui.Lbl("seconds");
                g.Row("Give up after", Ui.Flow(dTimeout, secs));
            });

            Group(g, webhookRows, delegate
            {
                g.Header("Webhook");
                g.Row("Address", dUrl);
                g.Row("Format", dFormat, "Teams: in the channel choose Workflows > \"Post to a channel when a webhook request is received\" and paste its address here. " +
                                         "Jira Automation: a rule with the \"Incoming webhook\" trigger; its fields arrive as {{webhookData.summary}}, {{webhookData.body}}, " +
                                         "{{webhookData.severity}}, {{webhookData.recovered}} and {{webhookData.dedupe}} (the same for every repeat of one problem).");
            });
            Group(g, templateRows, delegate
            {
                g.Row("Custom body", dTemplate, "JSON with {subject} {title} {message} {body} {kind} {severity} {part} {item} {server} {time}; each becomes a quoted JSON string, e.g. {\"msg\": {body}}.");
            });
            Group(g, secretRows, delegate
            {
                g.Row("Webhook secret", dSecret, "The secret shown with the Jira Automation webhook address; sent as X-Automation-Webhook-Token. Stored encrypted.");
            });
            Group(g, webhookRows, delegate
            {
                g.Row("Extra headers", dHeaders, "One per line, e.g. Authorization: Bearer abc123. Optional.");
            });

            Group(g, jiraRows, delegate
            {
                g.Header("Jira");
                g.Row("Jira address", dJiraUrl, "e.g. https://yoursite.atlassian.net (Cloud) or https://jira.example.com (Data Center).");
                g.Row("Log in with", dJiraAuth);
            });
            Group(g, jiraUserRows, delegate
            {
                g.Row("User", dJiraUser, "Jira Cloud: the email address of the Atlassian account. Data Center: the user name.");
            });
            Group(g, jiraRows, delegate
            {
                g.Row("Token", dJiraToken, "Jira Cloud: an API token (id.atlassian.com > Security > API tokens). Data Center: the password or a personal access token. Stored encrypted.");
                g.Row("Project key", dProject, "e.g. OPS. The account needs permission to create issues and add comments there.");
                g.Row("Issue type", dIssueType, "Must exist in the project, e.g. Task, Bug or Incident. \"Check login and project\" lists the types.");
                g.Row("Priority", dPriority, "Empty = the project's default. Otherwise a priority name, e.g. High.");
                g.Row("Labels", dLabels, "Comma separated. A label wk-... is added to tie the issue to its alert.");
                g.Row(null, dReuse);
                g.Row(null, dCommentRecovery);
            });

            Group(g, netRows, delegate
            {
                g.Row("Proxy", Ui.Flow(dProxy, dProxyUrl), "Only if this network reaches the internet through a proxy, e.g. http://proxy:8080. " +
                      "The service runs as Local System and does not see a proxy set for a user.");
                g.Row(null, dProxyLogin);
                g.Note("Sending the Windows login lets an impostor proxy on the network capture and relay it; leave it off unless the proxy refuses with HTTP 407.");
            });

            g.Header("Test");
            g.Row(null, Ui.Flow(tSend, tCheck, details));
            g.Row(null, testResult);
            g.Note("Tests are sent by the service with the settings shown here, saved or not.");
            g.Row("Notes", dNotes);
            dests.Detail.Controls.Add(g);

            EventHandler apply = delegate { ApplyDest(); };
            foreach (Control c in new Control[] { dName, dHost, dUser, dPassword, dFrom, dTo, dUrl, dTemplate, dHeaders, dSecret, dJiraUrl, dJiraUser,
                                                  dJiraToken, dProject, dIssueType, dPriority, dLabels, dProxyUrl, dNotes })
                c.TextChanged += apply;
            foreach (CheckBox c in new[] { dOn, dReuse, dCommentRecovery, dProxyLogin }) c.CheckedChanged += apply;
            dFingerprint.TextChanged += apply;
            foreach (ComboBox c in new[] { dTls, dFormat, dJiraAuth, dProxy }) c.SelectedIndexChanged += apply;
            dPort.ValueChanged += apply;
            dTimeout.ValueChanged += apply;
            dType.SelectedIndexChanged += delegate { TypeChanged(); };
            dTls.SelectedIndexChanged += delegate
            {
                if (Loading || fill > 0) return;
                string t = Ui.Get(dTls);
                if (t == "implicit" && dPort.Value == 587) dPort.Value = 465;
                else if (t == "starttls" && dPort.Value == 465) dPort.Value = 587;
                else if (t == "none" && (dPort.Value == 465 || dPort.Value == 587)) dPort.Value = 25;
            };
            dests.SelectedChanged += delegate { FillDest(); };
            dests.Changed += delegate { DestinationsChanged(); MarkDirty(); };
            return dests;
        }

        Control BuildRecent()
        {
            Panel p = new Panel();
            p.Dock = DockStyle.Fill;
            recent.Dock = DockStyle.Fill;
            recent.View = View.Details;
            recent.FullRowSelect = true;
            recent.HideSelection = false;
            recent.Tag = "nodirty";
            recent.Columns.Add("Time", 120);
            recent.Columns.Add("Severity", 70);
            recent.Columns.Add("Alert", 260);
            recent.Columns.Add("Part / entry", 170);
            recent.Columns.Add("Rules", 130);
            recent.Columns.Add("Sent", 340);
            recent.DoubleClick += delegate { ShowAlert(); };
            Label note = Ui.Hint("Alerts raised since the service started, newest first. Double-click one for its full text and what each destination answered.");
            note.Dock = DockStyle.Top;
            note.MaximumSize = new Size(0, 0);
            note.Padding = new Padding(0, 0, 0, 6);
            p.Controls.Add(recent);
            p.Controls.Add(note);
            return p;
        }

        // ================================================================== text helpers

        static string PartText(string part)
        {
            switch (part)
            {
                case "folders": return "Folder policies (move, copy, retention)";
                case "programs": return "Watchdog (programs and services)";
                case "scripts": return "Scripts";
                case "vertical": return "Vertical conversion";
                case "gpio": return "Axia GPIO (devices and triggers)";
                case "config": return "Configuration file problems";
                case "service": return "Service start";
            }
            return part;
        }

        static string TypeText(string type)
        {
            switch (type)
            {
                case "email": return "email";
                case "webhook": return "webhook";
                case "jira": return "Jira";
                case "eventlog": return "Event Log";
            }
            return type;
        }

        static string DefaultName(string type)
        {
            switch (type)
            {
                case "email": return "Email";
                case "webhook": return "Webhook";
                case "jira": return "Jira";
                case "eventlog": return "Windows Event Log";
            }
            return "Destination";
        }

        static string Host(string url)
        {
            Uri u;
            return Uri.TryCreate(url, UriKind.Absolute, out u) ? u.Host : url;
        }

        static string DestLine(AlertDestination d)
        {
            switch (d.Type)
            {
                case "email": return d.EmailTo.Length > 0 ? "To " + d.EmailTo : "No recipients yet";
                case "webhook":
                    {
                        string f = d.Format == "teams" ? "Teams" : d.Format == "discord" ? "Discord" : d.Format == "jira_automation" ? "Jira Automation" :
                                   d.Format == "json" ? "JSON" : d.Format == "custom" ? "custom JSON" : "text";
                        string shown = Secrets.Peek(d.Url) != null ? Host(Secrets.Peek(d.Url)) : (d.UrlHint.Length > 0 ? Host(d.UrlHint) : "address saved");
                        return d.Url.Length > 0 ? shown + " (" + f + ")" : "No address yet";
                    }
                case "jira": return d.JiraProject.Length > 0 && d.JiraUrl.Length > 0 ? "Project " + d.JiraProject + " on " + Host(d.JiraUrl) : "Not set up yet";
                case "eventlog": return "Application log, source " + AlertManager.EventSource;
            }
            return "";
        }

        static string RefText(string reference, AppConfig c)
        {
            int colon = reference.IndexOf(':');
            string type = colon > 0 ? reference.Substring(0, colon) : reference;
            string what;
            switch (type)
            {
                case "folder": what = "Folder policy"; break;
                case "program": what = "Watchdog"; break;
                case "script": what = "Script"; break;
                case "gpio": what = "GPIO device"; break;
                case "gpio-trigger": what = "GPIO trigger"; break;
                default: what = type; break;
            }
            string name = AlertManager.ItemName(c, reference);
            return what + ": " + (name.Length > 0 ? name : "(removed) " + reference);
        }

        /// <summary>The entries of the saved configuration that alerts can name, for the given parts.</summary>
        static List<string> Entries(AppConfig c, List<string> parts)
        {
            List<string> o = new List<string>();
            if (parts.Contains("folders")) foreach (FolderPolicy f in c.Folders) o.Add("folder:" + f.Id);
            if (parts.Contains("programs")) foreach (ProgramJob p in c.Programs) o.Add("program:" + p.Id);
            if (parts.Contains("scripts")) foreach (ScriptJob s in c.Scripts) o.Add("script:" + s.Id);
            if (parts.Contains("gpio"))
            {
                foreach (AxiaDevice d in c.Gpio.Devices) o.Add("gpio:" + d.Id);
                foreach (GpioTrigger t in c.Gpio.Triggers) o.Add("gpio-trigger:" + t.Id);
            }
            return o;
        }

        List<string> RuleIds()
        {
            List<string> o = new List<string>();
            foreach (AlertRule r in rules.Items) o.Add(r.Id);
            return o;
        }

        List<string> DestIds()
        {
            List<string> o = new List<string>();
            foreach (AlertDestination d in dests.Items) o.Add(d.Id);
            return o;
        }

        AlertCfg Working()
        {
            AlertCfg a = new AlertCfg();
            a.Destinations = dests.Items;
            a.Rules = rules.Items;
            return a;
        }

        string RuleLine(AlertRule r)
        {
            List<string> parts = new List<string>();
            foreach (string p in r.Parts) parts.Add(AlertSettings.PartLabel(p));
            string what = r.AllParts ? "All alerts" : parts.Count > 0 ? string.Join(", ", parts.ToArray()) : "No parts chosen";
            List<string> to = new List<string>();
            foreach (string id in r.Destinations)
            {
                AlertDestination d = dests.Items.Find(delegate(AlertDestination x) { return x.Id == id; });
                if (d != null) to.Add(d.Name);
            }
            return what + " > " + (to.Count > 0 ? string.Join(", ", to.ToArray()) : "nowhere yet");
        }

        // ================================================================== rules

        void FillRule()
        {
            AlertRule r = rules.Selected;
            fill++;
            try
            {
                Quiet(delegate
                {
                    AlertRule d = r ?? new AlertRule();
                    rOn.Checked = d.Enabled;
                    rName.Text = r != null ? d.Name : "";
                    rAll.Checked = d.AllParts;
                    rSome.Checked = !d.AllParts;
                    for (int i = 0; i < rParts.Items.Count; i++) rParts.SetItemChecked(i, d.Parts.Contains(((Opt)rParts.Items[i]).Value));
                    Ui.Set(rSeverity, d.MinSeverity);
                    rRecover.Checked = d.Recoveries;
                    Ui.SetNum(rCooldown, d.CooldownMinutes);
                    rStop.Checked = d.Stop;
                    rNotes.Text = d.Notes;
                    FillItems(d);
                    FillDestChoices(d);
                });
            }
            finally { fill--; }
            UpdateRuleEnabled();
        }

        void FillItems(AlertRule r)
        {
            fill++;
            try
            {
                AppConfig c = M.Config.Get();
                List<string> refs = r.AllParts ? new List<string>() : Entries(c, r.Parts);
                foreach (string i in r.Items) if (!refs.Contains(i)) refs.Add(i);
                rItems.BeginUpdate();
                rItems.Items.Clear();
                foreach (string x in refs) rItems.Items.Add(new Opt(x, RefText(x, c)), r.Items.Contains(x));
                rItems.EndUpdate();
                if (r.AllParts) rItemsNote.Text = "";
                else if (refs.Count == 0) rItemsNote.Text = "The ticked parts have no saved entries to choose from; the rule takes every alert of those parts.";
                else rItemsNote.Text = "Nothing ticked = every entry of the ticked parts. Tick entries to send only their alerts (other parts are not affected). Lists the saved entries.";
            }
            finally { fill--; }
        }

        void FillDestChoices(AlertRule r)
        {
            fill++;
            try
            {
                rDests.BeginUpdate();
                rDests.Items.Clear();
                foreach (AlertDestination d in dests.Items)
                    rDests.Items.Add(new Opt(d.Id, d.Name + "  (" + TypeText(d.Type) + (d.Enabled ? "" : ", off") + ")"), r.Destinations.Contains(d.Id));
                rDests.EndUpdate();
            }
            finally { fill--; }
        }

        void UpdateRuleEnabled()
        {
            bool some = rSome.Checked;
            rParts.Enabled = some;
            rItems.Enabled = some && rItems.Items.Count > 0;
            AlertRule r = rules.Selected;
            rSummary.Text = r != null ? AlertSettings.Describe(r, Working()) + "." : "";
        }

        void ApplyRule()
        {
            if (fill > 0 || Loading) { UpdateRuleEnabled(); return; }
            AlertRule r = rules.Selected;
            if (r == null) return;
            bool wasAll = r.AllParts;
            r.Enabled = rOn.Checked;
            r.Name = rName.Text;
            r.AllParts = rAll.Checked;
            r.MinSeverity = Ui.Get(rSeverity);
            r.Recoveries = rRecover.Checked;
            r.CooldownMinutes = (int)rCooldown.Value;
            r.Stop = rStop.Checked;
            r.Notes = rNotes.Text;
            if (wasAll != r.AllParts) FillItems(r);
            UpdateRuleEnabled();
            rules.RefreshSelected();
            RefreshUsedBy();
        }

        static List<string> Checked(CheckedListBox c, ItemCheckEventArgs e)
        {
            List<string> o = new List<string>();
            for (int i = 0; i < c.Items.Count; i++)
            {
                bool on = e != null && e.Index == i ? e.NewValue == CheckState.Checked : c.GetItemChecked(i);
                if (on) o.Add(((Opt)c.Items[i]).Value);
            }
            return o;
        }

        void PartsChecked(ItemCheckEventArgs e)
        {
            if (fill > 0 || Loading) return;
            AlertRule r = rules.Selected;
            if (r == null) return;
            r.Parts = Checked(rParts, e);
            r.Items.RemoveAll(delegate(string i) { return !r.Parts.Contains(AlertManager.PartOf("", i)); });
            FillItems(r);
            UpdateRuleEnabled();
            rules.RefreshSelected();
            MarkDirty();
        }

        void ListChecked(CheckedListBox c, ItemCheckEventArgs e, bool items)
        {
            if (fill > 0 || Loading) return;
            AlertRule r = rules.Selected;
            if (r == null) return;
            if (items) r.Items = Checked(c, e);
            else r.Destinations = Checked(c, e);
            UpdateRuleEnabled();
            rules.RefreshSelected();
            RefreshUsedBy();
            MarkDirty();
        }

        // ================================================================== destinations

        void FillDest()
        {
            AlertDestination d = dests.Selected;
            fill++;
            try
            {
                Quiet(delegate
                {
                    AlertDestination x = d ?? new AlertDestination();
                    dOn.Checked = x.Enabled;
                    dName.Text = d != null ? x.Name : "";
                    Ui.Set(dType, x.Type);
                    dHost.Text = x.SmtpHost;
                    Ui.SetNum(dPort, x.SmtpPort);
                    Ui.Set(dTls, x.SmtpTls);
                    dFingerprint.Text = x.SmtpTrustFingerprint;
                    dUser.Text = x.SmtpUser;
                    dPassword.Text = x.SmtpPassword.Length > 0 ? Unchanged : "";
                    dFrom.Text = x.EmailFrom;
                    dTo.Text = x.EmailTo;
                    Ui.SetNum(dTimeout, x.SmtpTimeoutSeconds);
                    dUrl.Text = Shown(x.Url);
                    Ui.Set(dFormat, x.Format);
                    dTemplate.Text = x.Template.Length > 0 ? x.Template : "{\"title\": {subject}, \"text\": {body}}";
                    dHeaders.Text = Shown(x.Headers).Replace("\r\n", "\n").Replace("\n", "\r\n");
                    dSecret.Text = x.Secret.Length > 0 ? Unchanged : "";
                    dJiraUrl.Text = x.JiraUrl;
                    Ui.Set(dJiraAuth, x.JiraAuth);
                    dJiraUser.Text = x.JiraUser;
                    dJiraToken.Text = x.JiraToken.Length > 0 ? Unchanged : "";
                    dProject.Text = x.JiraProject;
                    dIssueType.Text = x.JiraIssueType;
                    dPriority.Text = x.JiraPriority;
                    dLabels.Text = x.JiraLabels;
                    dReuse.Checked = x.JiraReuseOpen;
                    dCommentRecovery.Checked = x.JiraCommentRecovery;
                    Ui.Set(dProxy, x.Proxy);
                    dProxyUrl.Text = Shown(x.ProxyUrl);
                    dProxyLogin.Checked = x.ProxyLogin;
                    dNotes.Text = x.Notes;
                });
            }
            finally { fill--; }
            testResult.Text = "";
            lastDetail = "";
            details.Enabled = false;
            UpdateDestEnabled();
            RefreshUsedBy();
        }

        void UpdateDestEnabled()
        {
            string type = Ui.Get(dType);
            string format = Ui.Get(dFormat);
            Show(eventRows, type == "eventlog");
            Show(emailRows, type == "email");
            Show(webhookRows, type == "webhook");
            Show(templateRows, type == "webhook" && format == "custom");
            Show(secretRows, type == "webhook" && format == "jira_automation");
            Show(jiraRows, type == "jira");
            Show(jiraUserRows, type == "jira" && Ui.Get(dJiraAuth) == "basic");
            Show(netRows, type == "webhook" || type == "jira");
            dPassword.Enabled = dUser.Text.Trim().Length > 0;
            dProxyUrl.Enabled = Ui.Get(dProxy) == "manual";
            dProxyLogin.Enabled = Ui.Get(dProxy) != "none";
            tCheck.Visible = type == "jira";
            tSend.Text = type == "jira" ? "Create a test issue" : "Send a test";
            tSend.Enabled = tCheck.Enabled = busy == 0 && dests.Selected != null;
        }

        /// <summary>A typed-in secret stays "plain:" in memory; it is encrypted (by the service) when the page is saved.</summary>
        static string Secret(TextBox box, string stored)
        {
            if (box.Text == Unchanged) return stored;
            return box.Text.Length == 0 ? "" : "plain:" + box.Text;
        }

        /// <summary>An encrypted value is shown as dots; a typed (not yet saved) one as text.</summary>
        static string Shown(string v)
        {
            if (string.IsNullOrEmpty(v)) return "";
            return Secrets.Peek(v) ?? Unchanged;
        }

        void ApplyDest()
        {
            if (fill > 0 || Loading) { UpdateDestEnabled(); return; }
            AlertDestination d = dests.Selected;
            if (d == null) return;
            d.Enabled = dOn.Checked;
            d.Name = dName.Text;
            d.SmtpHost = dHost.Text;
            d.SmtpPort = (int)dPort.Value;
            d.SmtpTls = Ui.Get(dTls);
            d.SmtpTrustFingerprint = dFingerprint.Text.Trim();
            d.SmtpUser = dUser.Text.Trim();
            d.SmtpPassword = d.SmtpUser.Length == 0 ? "" : Secret(dPassword, d.SmtpPassword);
            d.EmailFrom = dFrom.Text;
            d.EmailTo = dTo.Text;
            d.SmtpTimeoutSeconds = (int)dTimeout.Value;
            if (dUrl.Text != Unchanged)
            {
                d.Url = dUrl.Text.Trim().Length > 0 ? "plain:" + dUrl.Text.Trim() : "";
                Uri hu;
                d.UrlHint = Uri.TryCreate(dUrl.Text.Trim(), UriKind.Absolute, out hu) ? hu.Scheme + "://" + hu.Host : "";
            }
            d.Format = Ui.Get(dFormat);
            d.Template = d.Format == "custom" ? dTemplate.Text : "";
            if (dHeaders.Text != Unchanged) d.Headers = dHeaders.Text.Trim().Length > 0 ? "plain:" + dHeaders.Text.Replace("\r\n", "\n") : "";
            d.Secret = Secret(dSecret, d.Secret);
            d.JiraUrl = dJiraUrl.Text;
            d.JiraAuth = Ui.Get(dJiraAuth);
            d.JiraUser = dJiraUser.Text;
            d.JiraToken = Secret(dJiraToken, d.JiraToken);
            d.JiraProject = dProject.Text;
            d.JiraIssueType = dIssueType.Text;
            d.JiraPriority = dPriority.Text;
            d.JiraLabels = dLabels.Text;
            d.JiraReuseOpen = dReuse.Checked;
            d.JiraCommentRecovery = dCommentRecovery.Checked;
            d.Proxy = Ui.Get(dProxy);
            d.ProxyLogin = dProxyLogin.Checked && d.Proxy != "none";
            if (dProxyUrl.Text != Unchanged) d.ProxyUrl = dProxyUrl.Text.Trim().Length > 0 ? "plain:" + dProxyUrl.Text.Trim() : "";
            d.Notes = dNotes.Text;
            UpdateDestEnabled();
            dests.RefreshSelected();
        }

        void TypeChanged()
        {
            if (fill > 0 || Loading) { UpdateDestEnabled(); return; }
            AlertDestination d = dests.Selected;
            if (d == null) return;
            string old = d.Type;
            d.Type = Ui.Get(dType);
            if (dName.Text.Trim().Length == 0 || dName.Text == DefaultName(old)) dName.Text = DefaultName(d.Type);
            testResult.Text = "";
            details.Enabled = false;
            UpdateDestEnabled();
            dests.RefreshSelected();
        }

        void DestinationsChanged()
        {
            List<string> ids = DestIds();
            foreach (AlertRule r in rules.Items) r.Destinations.RemoveAll(delegate(string id) { return !ids.Contains(id); });
            rules.RefreshAll();
            RefreshUsedBy();
        }

        void RefreshUsedBy()
        {
            AlertDestination d = dests.Selected;
            if (d == null) { dUsedBy.Text = ""; return; }
            List<string> by = new List<string>();
            foreach (AlertRule r in rules.Items) if (r.Destinations.Contains(d.Id)) by.Add(r.Name + (r.Enabled ? "" : " (off)"));
            dUsedBy.ForeColor = by.Count == 0 ? Ui.Warn : SystemColors.GrayText;
            dUsedBy.Text = by.Count == 0 ? "No rule sends here yet. Tick it under \"Send to\" in a rule on the Rules tab."
                                         : "Used by: " + string.Join(", ", by.ToArray());
        }

        void OnTab()
        {
            if (tabs.SelectedIndex == 0)
            {
                AlertRule r = rules.Selected;
                if (r != null) FillDestChoices(r);
                UpdateRuleEnabled();
                rules.RefreshAll();
            }
            else if (tabs.SelectedIndex == 1)
            {
                RefreshUsedBy();
                dests.RefreshAll();
            }
            else Tick();
        }

        // ================================================================== tests

        void Test(string mode)
        {
            AlertDestination d = dests.Selected;
            if (d == null) return;
            if (!M.Online)
            {
                testResult.ForeColor = Ui.Bad;
                testResult.Text = "The service is not running; tests are sent by the service.";
                return;
            }
            AlertDestination copy = Json.Clone(d);
            AlertSettings.NormalizeDestination(copy);
            List<string> problems = AlertSettings.ValidateDestination(copy, copy.Name);
            if (problems.Count > 0 && copy.Type != "eventlog")
            {
                testResult.ForeColor = Ui.Bad;
                testResult.Text = string.Join("\n", problems.ToArray());
                return;
            }
            // saved secrets are not sent to the service: only a reference it resolves from the saved settings
            AlertDestination saved = M.Config.Get().Alerts.Destinations.Find(delegate(AlertDestination x) { return x.Id == copy.Id; });
            if (saved != null)
            {
                copy.SmtpPassword = Secrets.KeepIfSaved(copy.SmtpPassword, saved.SmtpPassword, copy.Id);
                copy.JiraToken = Secrets.KeepIfSaved(copy.JiraToken, saved.JiraToken, copy.Id);
                copy.Secret = Secrets.KeepIfSaved(copy.Secret, saved.Secret, copy.Id);
                copy.Url = Secrets.KeepIfSaved(copy.Url, saved.Url, copy.Id);
                copy.Headers = Secrets.KeepIfSaved(copy.Headers, saved.Headers, copy.Id);
                copy.ProxyUrl = Secrets.KeepIfSaved(copy.ProxyUrl, saved.ProxyUrl, copy.Id);
            }
            string what = mode == "check" ? "Checking the Jira login and project" : copy.Type == "jira" ? "Creating a test issue" : "Sending a test";
            testResult.ForeColor = SystemColors.GrayText;
            testResult.Text = what + "... (up to " + (copy.Type == "email" ? "a minute" : "20 seconds") + ")";
            details.Enabled = false;
            busy++;
            UpdateDestEnabled();
            M.Async(this, delegate
            {
                JObject req = new JObject();
                req.Set("cmd", "alerts.test");
                req.Set("destination", Json.ToJson(copy));
                req.Set("mode", mode);
                return M.Client.Call(req, 3000);
            }, delegate(JObject r, Exception ex)
            {
                busy--;
                UpdateDestEnabled();
                if (ex != null)
                {
                    testResult.ForeColor = Ui.Bad;
                    testResult.Text = copy.Name + ": " + ex.Message;
                    return;
                }
                ChannelTestResult res = (ChannelTestResult)Json.ConvertTo(typeof(ChannelTestResult), r.Get("result"));
                testResult.ForeColor = res.Ok ? Ui.Ok : Ui.Bad;
                testResult.Text = (res.Ok ? copy.Name + ": " : copy.Name + " failed: ") + res.Message;
                lastDetail = res.Detail;
                lastTitle = (res.Ok ? "Test of " : "Failed test of ") + copy.Name;
                details.Enabled = lastDetail.Length > 0;
                if (!res.Ok && lastDetail.Length > 0) ShowDetails();
            });
        }

        void ShowDetails()
        {
            if (lastDetail.Length == 0) return;
            ResultDialog.Display(FindForm(), "What happened", lastTitle + ": " + testResult.Text, lastDetail);
        }

        void ShowAlert()
        {
            if (recent.SelectedItems.Count == 0) return;
            AlertRecord r = recent.SelectedItems[0].Tag as AlertRecord;
            if (r == null) return;
            StringBuilder sb = new StringBuilder();
            sb.Append("Time: ").Append(Ui.When(r.Time)).Append('\n');
            sb.Append("Severity: ").Append(r.Severity).Append(r.Kind == "recovered" ? " (back to normal)" : "").Append('\n');
            sb.Append("Part: ").Append(AlertSettings.PartLabel(r.Part)).Append(r.Item.Length > 0 ? " / " + r.Item : "").Append('\n');
            sb.Append("Rules: ").Append(r.Rules.Length > 0 ? r.Rules : "(none)").Append("\n\n");
            sb.Append(r.Message).Append("\n\n");
            sb.Append("Sent:\n").Append(r.Sent.Replace("; ", "\n"));
            ResultDialog.Display(FindForm(), "Alert", r.Title, sb.ToString());
        }

        // ================================================================== page

        public override void Tick()
        {
            EngineStatus s = M.Status;
            List<AlertRecord> list = s != null ? s.Alerts : new List<AlertRecord>();
            string sig = list.Count + "|" + (list.Count > 0 ? list[0].Time + list[0].Sent : "");
            if (sig == recentSig) return;
            recentSig = sig;
            recent.BeginUpdate();
            recent.Items.Clear();
            foreach (AlertRecord r in list)
            {
                ListViewItem it = new ListViewItem(Ui.When(r.Time));
                it.SubItems.Add(r.Kind == "recovered" ? "ok" : r.Severity);
                it.SubItems.Add(r.Title);
                it.SubItems.Add(AlertSettings.PartLabel(r.Part) + (r.Item.Length > 0 ? " / " + r.Item : ""));
                it.SubItems.Add(r.Rules);
                it.SubItems.Add(r.Sent);
                it.Tag = r;
                if (r.Kind == "recovered") it.ForeColor = Ui.Ok;
                else if (r.Severity == "error") it.ForeColor = Ui.Bad;
                else if (r.Severity == "warning") it.ForeColor = Ui.Warn;
                recent.Items.Add(it);
            }
            recent.EndUpdate();
        }

        protected override void LoadFrom(AppConfig c)
        {
            includeOutput.Checked = c.Alerts.IncludeScriptOutput;
            dests.SetItems(Json.CloneList(c.Alerts.Destinations));
            rules.SetItems(Json.CloneList(c.Alerts.Rules));
        }

        protected override void Collect(AppConfig c)
        {
            AlertCfg a = new AlertCfg();
            a.Destinations = Json.CloneList(dests.Items);
            a.Rules = Json.CloneList(rules.Items);
            a.IncludeScriptOutput = includeOutput.Checked;
            c.Alerts = a;
        }
    }
}
