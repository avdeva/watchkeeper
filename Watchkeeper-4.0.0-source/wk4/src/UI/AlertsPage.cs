// Alerts page: where problems are reported.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Watchkeeper.UI
{
    public class AlertsPage : Page
    {
        readonly ListEditor<AlertDestination> list = new ListEditor<AlertDestination>("Add a destination: e-mail, Jira, or a webhook (Teams, Slack, ...).");
        AlertDestination cur = new AlertDestination();
        readonly List<FieldRow> email = new List<FieldRow>(), jira = new List<FieldRow>(), hook = new List<FieldRow>();
        readonly ListView recent = new ListView();
        readonly Label live = new Label();
        public override string Title { get { return "Alerts"; } }

        static readonly string[][] Types = { new[] { "email", "E-mail" }, new[] { "jira", "Jira (an issue per problem)" }, new[] { "webhook", "Webhook (Teams, Slack, ...)" } };
        static readonly string[][] Levels = { new[] { "warning", "Warnings and errors" }, new[] { "error", "Errors only" } };
        static readonly string[][] Security = { new[] { "starttls", "STARTTLS (usually port 587)" }, new[] { "ssl", "SSL/TLS (usually port 465)" }, new[] { "none", "None (internal relay, port 25)" } };

        public AlertsPage(Manager m) : base(m)
        {
            SplitContainer split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.Orientation = Orientation.Horizontal;
            Controls.Add(split);
            Heading("Alerts", "Problems (a program stopped, a folder cannot be reached, a conversion failed, a GPIO device is offline) are written to the log and the " +
                              "Windows event log, and sent to the destinations below. When a problem is over, 'back to normal' follows.");
            CheckBox ev = B.Check("Also write warnings and errors to the Windows event log (Application log, source Watchkeeper)", delegate { return M.Edit.Alerts.EventLog; }, delegate(bool v) { M.Edit.Alerts.EventLog = v; });
            ev.Dock = DockStyle.Top;
            list.Title = delegate(AlertDestination d) { return d.Name; };
            list.Subtitle = delegate(AlertDestination d) { return (d.Enabled ? "" : "off - ") + (d.Type == "email" ? "e-mail to " + d.To : d.Type == "jira" ? "Jira project " + d.JiraProject : "webhook"); };
            list.Mark = delegate(AlertDestination d)
            {
                if (!d.Enabled) return Ui.Off;
                DestinationStatus s = M.Status == null ? null : M.Status.Alerts.Destinations.Find(delegate(DestinationStatus x) { return x.Id == d.Id; });
                return s != null && s.LastError.Length > 0 ? Ui.Bad : Ui.Good;
            };
            list.Create = delegate { return new AlertDestination { Id = Util.NewId(), Name = "New destination" }; };
            list.Copy = delegate(AlertDestination d) { AlertDestination c = new AlertDestination(); Json.FillFrom(c, Json.ToJson(d)); c.Id = Util.NewId(); c.Name = d.Name + " (copy)"; return c; };
            list.Selected += delegate(AlertDestination d) { cur = d ?? new AlertDestination(); B.Load(); Toggle(); ShowLive(); };
            list.ListChanged += delegate { M.MarkChanged(); };
            split.Panel1.Controls.Add(list);
            split.Panel1.Controls.Add(ev);

            FormGrid g = new FormGrid();
            int w = Ui.BaseFont().Height * 26;
            g.Row("", B.Check("Send alerts here", delegate { return cur.Enabled; }, delegate(bool v) { cur.Enabled = v; }));
            g.Row("Name", B.Text(w / 2, delegate { return cur.Name; }, delegate(string v) { cur.Name = v; }));
            g.Row("Type", B.Choice(Types, delegate { return cur.Type; }, delegate(string v) { cur.Type = v; Toggle(); }));
            email.Add(g.Row("Mail server", Ui.Flow(B.Text(w / 2, delegate { return cur.SmtpHost; }, delegate(string v) { cur.SmtpHost = v.Trim(); }), Ui.Label("port"),
                                                   B.Number(1, 65535, 0, delegate { return cur.SmtpPort; }, delegate(double v) { cur.SmtpPort = (int)v; }))));
            email.Add(g.Row("Encryption", B.Choice(Security, delegate { return cur.SmtpSecurity; }, delegate(string v) { cur.SmtpSecurity = v; })));
            email.Add(g.Row("Login", Ui.Flow(B.Text(w / 2, delegate { return cur.SmtpUser; }, delegate(string v) { cur.SmtpUser = v.Trim(); }), Ui.Label("password"),
                                             B.Password(140, delegate { return cur.SmtpPassword; }, delegate(string v) { cur.SmtpPassword = v; })),
                            "Empty for a relay that accepts this server by its address. Gmail needs an app password; Microsoft 365 needs SMTP login allowed for the mailbox."));
            email.Add(g.Row("From", B.Text(w, delegate { return cur.From; }, delegate(string v) { cur.From = v.Trim(); })));
            email.Add(g.Row("To", B.Text(w, delegate { return cur.To; }, delegate(string v) { cur.To = v.Trim(); }), "Several addresses separated by ';'."));
            jira.Add(g.Row("Jira address", B.Text(w, delegate { return cur.JiraUrl; }, delegate(string v) { cur.JiraUrl = v.Trim(); }), "e.g. https://yourcompany.atlassian.net"));
            jira.Add(g.Row("Account", B.Text(w, delegate { return cur.JiraUser; }, delegate(string v) { cur.JiraUser = v.Trim(); }),
                           "Jira Cloud: the account's e-mail address. Jira Server / Data Center: leave empty and use a personal access token."));
            jira.Add(g.Row("API token", B.Password(w / 2, delegate { return cur.JiraToken; }, delegate(string v) { cur.JiraToken = v; })));
            jira.Add(g.Row("Project key", Ui.Flow(B.Text(80, delegate { return cur.JiraProject; }, delegate(string v) { cur.JiraProject = v.Trim(); }), Ui.Label("issue type"),
                                                  B.Text(100, delegate { return cur.JiraIssueType; }, delegate(string v) { cur.JiraIssueType = v.Trim(); }))));
            jira.Add(g.Row("Labels", B.Text(w / 2, delegate { return cur.JiraLabels; }, delegate(string v) { cur.JiraLabels = v; }),
                           "An open issue for the same problem gets a comment instead of a new issue; 'back to normal' is added as a comment."));
            hook.Add(g.Row("Address", B.Text(w, delegate { return cur.Url; }, delegate(string v) { cur.Url = v.Trim(); }), "The incoming webhook address (https://...)."));
            TextBox body = B.Text(w, delegate { return cur.Body; }, delegate(string v) { cur.Body = v; });
            body.Multiline = true;
            body.Height = Ui.BaseFont().Height * 3;
            hook.Add(g.Row("Body", body, "Empty = {\"text\": \"title and message\"} (Teams, Slack, Mattermost). Or your own JSON with {title} {message} {level} {server} {time} {part}."));
            g.Section("Which alerts");
            g.Row("Parts", Ui.Flow(B.Check("Watchdog", delegate { return cur.Watchdog; }, delegate(bool v) { cur.Watchdog = v; }),
                                   B.Check("Folder tasks", delegate { return cur.Folders; }, delegate(bool v) { cur.Folders = v; }),
                                   B.Check("Vertical", delegate { return cur.Vertical; }, delegate(bool v) { cur.Vertical = v; }),
                                   B.Check("GPIO", delegate { return cur.Gpio; }, delegate(bool v) { cur.Gpio = v; }),
                                   B.Check("Service and settings", delegate { return cur.Service; }, delegate(bool v) { cur.Service = v; })));
            g.Row("Level", B.Choice(Levels, delegate { return cur.MinLevel; }, delegate(string v) { cur.MinLevel = v; }),
                  "Warnings: a program stopped and is being started again, some files could not be moved. Errors: it cannot be fixed by itself.");
            g.Row("", B.Check("Send 'back to normal' when a problem is over", delegate { return cur.Recoveries; }, delegate(bool v) { cur.Recoveries = v; }));
            g.Row("Repeat after", Ui.Flow(B.Number(0, 10080, 0, delegate { return cur.RepeatMinutes; }, delegate(double v) { cur.RepeatMinutes = (int)v; }), Ui.Label("minutes at the earliest (the same problem)")));
            g.Row("Notes", B.Text(w, delegate { return cur.Notes; }, delegate(string v) { cur.Notes = v; }));
            live.AutoSize = true;
            live.MaximumSize = new Size(w + 200, 0);
            g.Row("Now", live);
            g.Row("", Ui.Button("Send a test message", delegate { Test(); }), "Sent from this window with the settings as they are here. Jira: checks the login and the project without creating an issue.");
            list.Detail.Controls.Add(g);

            recent.View = View.Details;
            recent.Dock = DockStyle.Fill;
            recent.FullRowSelect = true;
            int em = Ui.BaseFont().Height;
            recent.Columns.Add("Time", em * 8);
            recent.Columns.Add("Alert", em * 30);
            recent.Columns.Add("Sent to", em * 24);
            Label rl = new Label { Text = "Recent alerts", Dock = DockStyle.Top, Font = Ui.Bold(9.5f), Height = em + 8 };
            split.Panel2.Controls.Add(recent);
            split.Panel2.Controls.Add(rl);
            bool sized = false;
            split.Resize += delegate { if (sized || split.Height < 200) return; sized = true; try { split.SplitterDistance = split.Height * 66 / 100; } catch (Exception) { } };
        }

        void Toggle()
        {
            foreach (FieldRow r in email) r.Show(cur.Type == "email");
            foreach (FieldRow r in jira) r.Show(cur.Type == "jira");
            foreach (FieldRow r in hook) r.Show(cur.Type == "webhook");
        }

        protected override void OnFieldChanged() { list.RefreshTitles(); }

        public override void Reload() { B.Load(); list.SetItems(M.Edit.Alerts.Destinations); }

        void ShowLive()
        {
            DestinationStatus s = M.Status == null ? null : M.Status.Alerts.Destinations.Find(delegate(DestinationStatus x) { return x.Id == cur.Id; });
            if (s == null) { live.Text = ""; return; }
            live.Text = (s.Sent > 0 ? "Sent " + s.Sent + " since the service started; last " + Ago(s.LastSent) + "." : "Nothing sent since the service started.") +
                        (s.LastError.Length > 0 ? "\nLast failure (" + Ago(s.LastErrorAt) + "): " + s.LastError : "");
            live.ForeColor = s.LastError.Length > 0 ? Ui.Bad : Ui.Help;
        }

        public override void Live(EngineStatus s)
        {
            ShowLive();
            list.RefreshTitles();
            recent.BeginUpdate();
            recent.Items.Clear();
            foreach (AlertRecord r in s.Alerts.Recent)
            {
                ListViewItem it = new ListViewItem(Util.FromEpoch(r.Time).ToString("dd MMM HH:mm", Util.Inv));
                it.SubItems.Add((r.Recovery ? "OK: " : r.Level == "error" ? "ERROR: " : "WARNING: ") + r.Title + " - " + r.Message);
                it.SubItems.Add(r.Result);
                it.ForeColor = r.Recovery ? Ui.Good : r.Level == "error" ? Ui.Bad : Ui.Warn;
                recent.Items.Add(it);
            }
            recent.EndUpdate();
        }

        public override Color Dot(EngineStatus s)
        {
            if (s.Alerts.Destinations.Exists(delegate(DestinationStatus d) { return d.LastError.Length > 0; })) return Ui.Bad;
            return M.Edit.Alerts.Destinations.Exists(delegate(AlertDestination d) { return d.Enabled; }) ? Ui.Good : Ui.Off;
        }

        public string TestNow()
        {
            try { return Alerts.Test(cur, M.Edit); }
            catch (Exception ex) { return "Not sent: " + ex.Message; }
        }

        void Test()
        {
            Cursor = Cursors.WaitCursor;
            string r;
            try { r = TestNow(); } finally { Cursor = Cursors.Default; }
            Ui.Info(FindForm(), r);
        }
    }
}
