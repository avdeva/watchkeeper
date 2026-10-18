// Email (own SMTP client), webhook formats, HTTP helper and failure explanations.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Watchkeeper;

namespace Watchkeeper.Tests
{
    /// <summary>A tiny scripted SMTP server: plain text only, records what the client sent.</summary>
    public class FakeSmtp : IDisposable
    {
        readonly TcpListener listener;
        readonly Thread thread;
        public readonly List<string> Received = new List<string>();
        public string Message = "";
        public bool Silent;                      // accept the connection but never greet
        public bool OfferAuth = true;
        public bool RejectLogin;
        public string RejectRcpt;                // reply for RCPT, e.g. "550 5.7.1 Unable to relay"
        /// <summary>Offer STARTTLS with this certificate.</summary>
        public System.Security.Cryptography.X509Certificates.X509Certificate2 Cert;

        static System.Security.Cryptography.X509Certificates.X509Certificate2 made;

        /// <summary>A self-signed certificate for "fake.example", made once with openssl.</summary>
        public static System.Security.Cryptography.X509Certificates.X509Certificate2 TestCert()
        {
            if (made != null) return made;
            string dir = Path.Combine(Path.GetTempPath(), "wk-cert-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(dir);
            Func<string, bool> run = delegate(string args)
            {
                System.Diagnostics.Process p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("openssl", args)
                    { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = dir });
                p.StandardError.ReadToEnd();
                p.WaitForExit();
                return p.ExitCode == 0;
            };
            if (!run("req -x509 -newkey rsa:2048 -nodes -keyout k.pem -out c.pem -days 2 -subj /CN=fake.example") ||
                !run("pkcs12 -export -out c.pfx -inkey k.pem -in c.pem -passout pass:x -certpbe PBE-SHA1-3DES -keypbe PBE-SHA1-3DES -macalg sha1"))
                throw new SkipException("openssl not available for a test certificate");
            made = new System.Security.Cryptography.X509Certificates.X509Certificate2(Path.Combine(dir, "c.pfx"), "x");
            return made;
        }
        public int Port { get { return ((IPEndPoint)listener.LocalEndpoint).Port; } }

        public FakeSmtp()
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            thread = new Thread(Serve);
            thread.IsBackground = true;
            thread.Start();
        }

        void Serve()
        {
            try
            {
                using (TcpClient c = listener.AcceptTcpClient())
                {
                    Stream ns = c.GetStream();
                    StreamReader rd = new StreamReader(ns, Encoding.ASCII);
                    StreamWriter wr = new StreamWriter(ns, Encoding.ASCII);
                    wr.NewLine = "\r\n";
                    wr.AutoFlush = true;
                    if (Silent) { Thread.Sleep(5000); return; }
                    wr.WriteLine("220 fake.example ESMTP ready");
                    bool data = false;
                    StringBuilder msg = new StringBuilder();
                    int authStep = 0;
                    string line;
                    while ((line = rd.ReadLine()) != null)
                    {
                        if (data)
                        {
                            if (line == ".") { data = false; Message = msg.ToString(); wr.WriteLine("250 2.0.0 queued"); continue; }
                            msg.Append(line).Append("\n");
                            continue;
                        }
                        lock (Received) Received.Add(line);
                        if (authStep == 1) { authStep = 2; wr.WriteLine("334 UGFzc3dvcmQ6"); continue; }
                        if (authStep == 2)
                        {
                            authStep = 0;
                            wr.WriteLine(RejectLogin ? "535 5.7.139 Authentication unsuccessful, SmtpClientAuthentication is disabled for the Tenant." : "235 2.7.0 Authentication successful");
                            continue;
                        }
                        string u = line.ToUpperInvariant();
                        if (u.StartsWith("EHLO"))
                        {
                            wr.WriteLine("250-fake.example Hello");
                            wr.WriteLine("250-SIZE 35882577");
                            if (Cert != null && !(ns is System.Net.Security.SslStream)) wr.WriteLine("250-STARTTLS");
                            if (OfferAuth) wr.WriteLine("250-AUTH LOGIN PLAIN");
                            wr.WriteLine("250 8BITMIME");
                        }
                        else if (u == "STARTTLS" && Cert != null)
                        {
                            wr.WriteLine("220 2.0.0 Ready to start TLS");
                            System.Net.Security.SslStream ssl = new System.Net.Security.SslStream(ns, false);
                            ssl.AuthenticateAsServer(Cert, false, System.Security.Authentication.SslProtocols.Tls12, false);
                            ns = ssl;
                            rd = new StreamReader(ns, Encoding.ASCII);
                            wr = new StreamWriter(ns, Encoding.ASCII);
                            wr.NewLine = "\r\n";
                            wr.AutoFlush = true;
                        }
                        else if (u == "AUTH LOGIN") { authStep = 1; wr.WriteLine("334 VXNlcm5hbWU6"); }
                        else if (u.StartsWith("MAIL FROM")) wr.WriteLine("250 2.1.0 Sender OK");
                        else if (u.StartsWith("RCPT TO")) wr.WriteLine(RejectRcpt ?? "250 2.1.5 Recipient OK");
                        else if (u == "DATA") { data = true; wr.WriteLine("354 Start mail input"); }
                        else if (u == "QUIT") { wr.WriteLine("221 bye"); break; }
                        else wr.WriteLine("500 unknown");
                    }
                }
            }
            catch (Exception) { }
        }

        public void Dispose()
        {
            try { listener.Stop(); } catch (Exception) { }
        }
    }

    public class NetTests : Env
    {
        SmtpSettings Settings(FakeSmtp f)
        {
            SmtpSettings s = new SmtpSettings();
            s.Host = "127.0.0.1";
            s.Port = f.Port;
            f.Cert = FakeSmtp.TestCert();
            s.Tls = "starttls";
            s.TrustFingerprint = SmtpSender.Fingerprint(f.Cert);
            s.User = "alerts@example.com";
            s.Password = "secret";
            s.From = "Studio <alerts@example.com>";
            s.To = new List<string> { "ops@example.com", "boss@example.com" };
            s.TimeoutMs = 3000;
            return s;
        }

        [Fact]
        public void Email_goes_through_every_step_and_the_message_is_readable()
        {
            using (FakeSmtp f = new FakeSmtp())
            {
                NetTrace t = new NetTrace();
                SmtpSender.Send(Settings(f), "Studio – test ✓", "Line one\nLine two", t);
                string trace = t.ToString();
                Assert.Contains(trace, "connected");
                Assert.Contains(trace, "S: 220 fake.example");
                Assert.Contains(trace, "C: (password)");
                Assert.False(trace.Contains("c2VjcmV0"), "password must not be in the trace");
                Assert.Contains(trace, "Message accepted");
                Assert.In("MAIL FROM:<alerts@example.com>", f.Received);
                Assert.In("RCPT TO:<boss@example.com>", f.Received);
                Assert.Contains(f.Message, "Subject: =?utf-8?B?");
                Assert.Contains(f.Message, "To: ops@example.com, boss@example.com");
                string b64 = f.Message.Substring(f.Message.IndexOf("\n\n") + 2).Replace("\n", "");
                Assert.Eq("Line one\r\nLine two", Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
            }
        }

        [Fact]
        public void Silent_server_names_the_greeting_step_and_implicit_tls()
        {
            using (FakeSmtp f = new FakeSmtp())
            {
                f.Silent = true;
                SmtpSettings s = Settings(f);
                s.TimeoutMs = 1500;
                NetTrace t = new NetTrace();
                NetFailure ex = Assert.Throws<NetFailure>(delegate { SmtpSender.Send(s, "x", "y", t); });
                Assert.Eq("waiting for the server's greeting", ex.Step);
                Assert.Contains(ex.Message, "Implicit TLS");
            }
        }

        [Fact]
        public void Login_rejection_is_explained()
        {
            using (FakeSmtp f = new FakeSmtp())
            {
                f.RejectLogin = true;
                NetFailure ex = Assert.Throws<NetFailure>(delegate { SmtpSender.Send(Settings(f), "x", "y", new NetTrace()); });
                Assert.Eq("login", ex.Step);
                Assert.Contains(ex.Message, "Authenticated SMTP");
            }
        }

        [Fact]
        public void Relay_denied_and_missing_starttls_are_explained()
        {
            using (FakeSmtp f = new FakeSmtp())
            {
                f.RejectRcpt = "550 5.7.1 Unable to relay";
                SmtpSettings s = Settings(f);
                s.User = "";
                NetFailure ex = Assert.Throws<NetFailure>(delegate { SmtpSender.Send(s, "x", "y", new NetTrace()); });
                Assert.Contains(ex.Step, "recipient");
                Assert.Contains(ex.Message, "Relaying denied");
            }
            using (FakeSmtp f = new FakeSmtp())
            {
                SmtpSettings s = Settings(f);
                f.Cert = null;                       // the server offers no STARTTLS
                NetFailure ex = Assert.Throws<NetFailure>(delegate { SmtpSender.Send(s, "x", "y", new NetTrace()); });
                Assert.Eq("STARTTLS", ex.Step);
            }
        }

        [Fact]
        public void Login_needs_encryption_and_an_untrusted_certificate_names_its_fingerprint()
        {
            using (FakeSmtp f = new FakeSmtp())
            {
                SmtpSettings s = Settings(f);
                s.Tls = "none";
                NetFailure ex = Assert.Throws<NetFailure>(delegate { SmtpSender.Send(s, "x", "y", new NetTrace()); });
                Assert.Eq("login", ex.Step);
                Assert.Contains(ex.Message, "not sent over a connection without encryption");
                Assert.False(f.Received.Exists(delegate(string l) { return l.StartsWith("AUTH"); }), "no AUTH without TLS");
            }
            using (FakeSmtp f = new FakeSmtp())
            {
                SmtpSettings s = Settings(f);
                s.TrustFingerprint = "";
                NetTrace t = new NetTrace();
                NetFailure ex = Assert.Throws<NetFailure>(delegate { SmtpSender.Send(s, "x", "y", t); });
                Assert.Eq("TLS", ex.Step);
                Assert.Contains(ex.Message, SmtpSender.Fingerprint(f.Cert));
                Assert.Contains(ex.Message, "Trust this certificate");
            }
            using (FakeSmtp f = new FakeSmtp())
            {
                SmtpSettings s = Settings(f);
                s.TrustFingerprint = new string('A', 64);           // another certificate's fingerprint
                Assert.Throws<NetFailure>(delegate { SmtpSender.Send(s, "x", "y", new NetTrace()); });
            }
        }

        [Fact]
        public void Closed_port_is_a_connect_failure()
        {
            TcpListener l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            SmtpSettings s = new SmtpSettings();
            s.Host = "127.0.0.1";
            s.Port = port;
            s.To.Add("a@b.c");
            s.TimeoutMs = 2000;
            NetFailure ex = Assert.Throws<NetFailure>(delegate { SmtpSender.Send(s, "x", "y", new NetTrace()); });
            Assert.Eq("connect", ex.Step);
        }

        [Fact]
        public void Webhook_formats()
        {
            AlertDestination a = new AlertDestination();
            a.Type = "webhook";
            AlertRecord r = new AlertRecord();
            r.Title = "vMix stopped";
            r.Message = "It \"stopped\".";
            r.Kind = "program_restart";
            r.Severity = "warning";
            r.Key = "program:abc";
            a.Format = "teams";
            JObject teams = (JObject)Json.Parse(AlertManager.WebhookBody(a, r, "[Studio] vMix stopped", "Body"));
            Assert.Eq("message", teams.Str("type", ""));
            JObject att = (JObject)((List<object>)teams.Get("attachments"))[0];
            Assert.Eq("application/vnd.microsoft.card.adaptive", att.Str("contentType", ""));
            Assert.Eq("AdaptiveCard", ((JObject)att.Get("content")).Str("type", ""));
            a.Format = "text";
            Assert.Contains(AlertManager.WebhookBody(a, r, "S", "B"), "\"text\"");
            a.Format = "jira_automation";
            JObject ja = (JObject)Json.Parse(AlertManager.WebhookBody(a, r, "[Studio] WARNING: vMix stopped", "B"));
            Assert.Eq("warning", ja.Str("severity", ""));
            Assert.Eq("Studio", ja.Str("server", ""));
            Assert.Eq(JiraClient.Label("Studio", "program:abc"), ja.Str("dedupe", ""));
            a.Secret = "s3cret";
            a.Headers = "X-Team: broadcast\nAuthorization: Bearer abc";
            HttpRequestSpec req = AlertManager.WebhookRequest(a, "{}");
            Assert.True(req.Headers.Exists(delegate(KeyValuePair<string, string> h) { return h.Key == "X-Automation-Webhook-Token" && h.Value == "s3cret"; }));
            Assert.True(req.Headers.Exists(delegate(KeyValuePair<string, string> h) { return h.Key == "X-Team" && h.Value == "broadcast"; }));
            a.Format = "custom";
            a.Template = "{\"t\": {title}, \"m\": {message}, \"s\": {severity}}";
            JObject custom = (JObject)Json.Parse(AlertManager.WebhookBody(a, r, "S", "B"));
            Assert.Eq("It \"stopped\".", custom.Str("m", ""));
            Assert.Eq("warning", custom.Str("s", ""));
            AppConfig c = Cfg.Starter();
            AlertDestination bad = new AlertDestination();
            bad.Type = "webhook";
            bad.Url = "https://example.com/x";
            bad.Format = "custom";
            bad.Template = "{\"t\": \"{title}\"}";      // quotes doubled: invalid once filled in
            c.Alerts.Destinations.Add(bad);
            Assert.True(Cfg.Validate(c, "alerts").Exists(delegate(string e) { return e.Contains("not valid JSON"); }));
        }

        [Fact]
        public void Http_failures_are_explained_and_error_bodies_kept()
        {
            HttpListener l = new HttpListener();
            int port = 21000 + new Random().Next(3000);
            l.Prefixes.Add("http://127.0.0.1:" + port + "/");
            try { l.Start(); }
            catch (Exception ex) { throw new SkipException("cannot listen: " + ex.Message); }
            Thread t = new Thread(delegate()
            {
                try
                {
                    HttpListenerContext ctx = l.GetContext();
                    ctx.Response.StatusCode = 400;
                    byte[] b = Encoding.UTF8.GetBytes("{\"error\":\"Invalid payload\"}");
                    ctx.Response.OutputStream.Write(b, 0, b.Length);
                    ctx.Response.Close();
                }
                catch (Exception) { }
            });
            t.IsBackground = true;
            t.Start();
            AlertDestination a = new AlertDestination();
            a.Type = "webhook";
            a.Url = "http://127.0.0.1:" + port + "/hook";
            a.Proxy = "none";
            AlertRecord r = new AlertRecord();
            r.Title = "x";
            NetTrace trace = new NetTrace();
            NetFailure ex2 = Assert.Throws<NetFailure>(delegate { AlertManager.SendWebhook(a, r, "S", "B", "srv", trace); });
            l.Stop();
            Assert.Contains(ex2.Message, "HTTP 400");
            Assert.Contains(ex2.Message, "Invalid payload");
            Assert.Contains(trace.ToString(), "via no proxy");

            HttpRequestSpec s = new HttpRequestSpec();
            s.Url = "http://no-such-host.invalid/x";
            s.Proxy = "none";
            s.TimeoutMs = 5000;
            HttpResult res = Net.Send(s, null);
            Assert.False(res.Answered);
            Assert.True(res.Error.Contains("could not be found") || res.Error.Contains("connect"), res.Error);
        }

        [Fact]
        public void Probe_checks_tcp_and_http()
        {
            TcpListener l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            Assert.Null(Net.Probe("tcp://127.0.0.1:" + port, 2000));
            l.Stop();
            Assert.NotNull(Net.Probe("tcp://127.0.0.1:" + port, 2000));
            Assert.Contains(Net.Probe("tcp://nonsense", 1000), "tcp://host:port");
        }
    }
}
