// Local stand-ins for the network services Watchkeeper talks to: an Axia device (LWRP), vMix, Jira, a mail server, a webhook.
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
    public class FakeAxia : IDisposable
    {
        readonly TcpListener listener;
        readonly Thread thread;
        readonly Queue<string> outgoing = new Queue<string>();
        public readonly List<string> Received = new List<string>();
        public volatile bool Connected;
        public volatile string InitialGpi = "hhhhh";
        public bool AddReportsState = true;     // some devices only report states when asked
        TcpClient current;
        public int Port { get { return ((IPEndPoint)listener.LocalEndpoint).Port; } }

        public FakeAxia()
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            thread = new Thread(Serve);
            thread.IsBackground = true;
            thread.Start();
        }

        public void Push(string line) { lock (outgoing) outgoing.Enqueue(line); }

        public void DropClient()
        {
            try { if (current != null) current.Close(); } catch (Exception) { }
        }

        void Serve()
        {
            while (true)
            {
                TcpClient c;
                try { c = listener.AcceptTcpClient(); }
                catch (Exception) { return; }
                current = c;
                try
                {
                    NetworkStream ns = c.GetStream();
                    StreamWriter w = new StreamWriter(ns, Encoding.ASCII);
                    w.NewLine = "\r\n";
                    w.AutoFlush = true;
                    Connected = true;
                    StringBuilder buf = new StringBuilder();
                    byte[] chunk = new byte[1024];
                    while (c.Connected)
                    {
                        if (c.Client.Poll(50000, SelectMode.SelectRead))
                        {
                            int n = ns.Read(chunk, 0, chunk.Length);
                            if (n == 0) break;
                            buf.Append(Encoding.ASCII.GetString(chunk, 0, n));
                            string all = buf.ToString();
                            int nl;
                            while ((nl = all.IndexOf('\n')) >= 0)
                            {
                                string line = all.Substring(0, nl).Trim();
                                all = all.Substring(nl + 1);
                                lock (Received) Received.Add(line);
                                if (line == "VER") w.WriteLine("VER LWRP:1.4.5 DEVN:\"FakeNode\" SYSV:2.0");
                                else if (line == "ADD GPI" && AddReportsState)
                                {
                                    w.WriteLine("BEGIN");
                                    for (int p = 1; p <= 8; p++) w.WriteLine("GPI " + p + " " + (p == 1 ? InitialGpi : "hhhhh"));
                                    w.WriteLine("END");
                                }
                                else if (line == "GPI")
                                {
                                    for (int p = 1; p <= 8; p++) w.WriteLine("GPI " + p + " " + (p == 1 ? InitialGpi : "hhhhh"));
                                }
                                else if (line == "GPO")
                                {
                                    for (int p = 1; p <= 8; p++) w.WriteLine("GPO " + p + " hhhhh");
                                }
                                else if (line == "ADD GPO")
                                {
                                    w.WriteLine("BEGIN");
                                    for (int p = 1; p <= 8; p++) w.WriteLine("GPO " + p + " hhhhh");
                                    w.WriteLine("END");
                                }
                            }
                            buf = new StringBuilder(all);
                        }
                        lock (outgoing) while (outgoing.Count > 0) w.WriteLine(outgoing.Dequeue());
                    }
                }
                catch (Exception) { }
                Connected = false;
                try { c.Close(); } catch (Exception) { }
            }
        }

        public void Dispose()
        {
            try { listener.Stop(); } catch (Exception) { }
            DropClient();
        }
    }

    /// <summary>Answers the vMix Web API: /api returns the state XML, /api/?Function=... records the call.</summary>
    public class FakeVmix : IDisposable
    {
        readonly HttpListener l = new HttpListener();
        public readonly List<string> Calls = new List<string>();
        public int Port;
        public bool Fail;

        public const string Xml = "<vmix><version>27.0.0.92</version><edition>4K</edition><inputs>" +
                                  "<input key=\"aa-11\" number=\"1\" type=\"Capture\" title=\"Camera 1\" state=\"Running\">Camera 1</input>" +
                                  "<input key=\"bb-22\" number=\"2\" type=\"Capture\" title=\"Camera &amp; Guest\" state=\"Running\">Camera &amp; Guest</input>" +
                                  "<input key=\"cc-33\" number=\"3\" type=\"GT\" title=\"Lower third.gtzip\"><text index=\"0\" name=\"Headline.Text\">x</text></input>" +
                                  "</inputs><overlays/><preview>2</preview><active>1</active></vmix>";

        public FakeVmix()
        {
            Port = 23000 + new Random().Next(4000);
            l.Prefixes.Add("http://127.0.0.1:" + Port + "/");
            l.Start();
            Thread t = new Thread(delegate()
            {
                while (true)
                {
                    HttpListenerContext ctx;
                    try { ctx = l.GetContext(); }
                    catch (Exception) { return; }
                    string url = ctx.Request.RawUrl;
                    string body;
                    if (url == "/api" || url == "/api/") { body = Xml; ctx.Response.StatusCode = 200; }
                    else
                    {
                        lock (Calls) Calls.Add(url);
                        ctx.Response.StatusCode = Fail ? 500 : 200;
                        body = Fail ? "Function not found" : "Function completed successfully.";
                    }
                    byte[] b = Encoding.UTF8.GetBytes(body);
                    ctx.Response.OutputStream.Write(b, 0, b.Length);
                    ctx.Response.Close();
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        public void Dispose() { try { l.Stop(); } catch (Exception) { } }
    }


    /// <summary>Enough of the Jira REST API: login check, project, create issue, search by label, comment.</summary>
    public class FakeJira : IDisposable
    {
        public class Issue
        {
            public string Key = "";
            public string Summary = "";
            public string Description = "";
            public List<string> Labels = new List<string>();
            public List<string> Comments = new List<string>();
            public bool Done;
        }

        readonly HttpListener l = new HttpListener();
        public readonly List<Issue> Issues = new List<Issue>();
        public readonly List<string> Requests = new List<string>();
        public string Auth = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("ops@example.com:tok"));
        public int Port;
        public string Url { get { return "http://127.0.0.1:" + Port; } }

        public FakeJira()
        {
            Port = 27000 + new Random().Next(3000);
            l.Prefixes.Add("http://127.0.0.1:" + Port + "/");
            l.Start();
            Thread t = new Thread(Serve);
            t.IsBackground = true;
            t.Start();
        }

        void Reply(HttpListenerContext ctx, int status, string body)
        {
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            byte[] b = Encoding.UTF8.GetBytes(body);
            ctx.Response.OutputStream.Write(b, 0, b.Length);
            ctx.Response.Close();
        }

        void Serve()
        {
            while (true)
            {
                HttpListenerContext ctx;
                try { ctx = l.GetContext(); }
                catch (Exception) { return; }
                try { Handle(ctx); }
                catch (Exception ex) { try { Reply(ctx, 500, "{\"errorMessages\":[\"" + ex.Message + "\"]}"); } catch (Exception) { } }
            }
        }

        void Handle(HttpListenerContext ctx)
        {
            string path = ctx.Request.Url.AbsolutePath;
            string body;
            using (StreamReader r = new StreamReader(ctx.Request.InputStream)) body = r.ReadToEnd();
            lock (Requests) Requests.Add(ctx.Request.HttpMethod + " " + ctx.Request.Url.PathAndQuery);
            if (ctx.Request.Headers["Authorization"] != Auth) { Reply(ctx, 401, "{\"errorMessages\":[\"You are not authenticated.\"]}"); return; }
            if (path == "/rest/api/2/myself") { Reply(ctx, 200, "{\"displayName\":\"Ops Bot\",\"name\":\"ops\"}"); return; }
            if (path.StartsWith("/rest/api/2/project/"))
            {
                if (path.EndsWith("/OPS")) Reply(ctx, 200, "{\"key\":\"OPS\",\"name\":\"Operations\",\"issueTypes\":[{\"name\":\"Task\"},{\"name\":\"Bug\"}]}");
                else Reply(ctx, 404, "{\"errorMessages\":[\"No project could be found with key '" + path.Substring(20) + "'.\"],\"errors\":{}}");
                return;
            }
            if (path == "/rest/api/2/issue" && ctx.Request.HttpMethod == "POST")
            {
                JObject f = (JObject)((JObject)Json.Parse(body)).Get("fields");
                string type = ((JObject)f.Get("issuetype")).Str("name", "");
                if (type != "Task" && type != "Bug") { Reply(ctx, 400, "{\"errorMessages\":[],\"errors\":{\"issuetype\":\"Specify a valid issue type\"}}"); return; }
                Issue i = new Issue();
                lock (Issues)
                {
                    i.Key = "OPS-" + (Issues.Count + 1);
                    i.Summary = f.Str("summary", "");
                    i.Description = f.Str("description", "");
                    foreach (object o in (List<object>)f.Get("labels")) i.Labels.Add((string)o);
                    Issues.Add(i);
                }
                Reply(ctx, 201, "{\"id\":\"100\",\"key\":\"" + i.Key + "\"}");
                return;
            }
            if (path == "/rest/api/2/search" || path == "/rest/api/3/search/jql")
            {
                string jql = ctx.Request.QueryString["jql"] ?? "";
                System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(jql, "labels = \"([^\"]+)\"");
                string label = m.Success ? m.Groups[1].Value : "";
                List<string> keys = new List<string>();
                lock (Issues)
                    for (int k = Issues.Count - 1; k >= 0; k--)
                        if (!Issues[k].Done && Issues[k].Labels.Contains(label)) keys.Add("{\"key\":\"" + Issues[k].Key + "\"}");
                Reply(ctx, 200, "{\"issues\":[" + string.Join(",", keys.ToArray()) + "]}");
                return;
            }
            if (path.StartsWith("/rest/api/2/issue/") && path.EndsWith("/comment"))
            {
                string key = path.Substring(18, path.Length - 18 - 8);
                lock (Issues)
                {
                    Issue i = Issues.Find(delegate(Issue x) { return x.Key == key; });
                    if (i == null) { Reply(ctx, 404, "{\"errorMessages\":[\"Issue does not exist\"]}"); return; }
                    i.Comments.Add(((JObject)Json.Parse(body)).Str("body", ""));
                }
                Reply(ctx, 201, "{\"id\":\"1\"}");
                return;
            }
            Reply(ctx, 404, "{\"errorMessages\":[\"not here\"]}");
        }

        public void Dispose() { try { l.Stop(); } catch (Exception) { } }
    }
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

        public readonly List<string> Messages = new List<string>();

        void Serve()
        {
            while (true)
            {
                TcpClient c0;
                try { c0 = listener.AcceptTcpClient(); } catch (Exception) { return; }
                ServeOne(c0);
            }
        }

        void ServeOne(TcpClient c0)
        {
            try
            {
                using (TcpClient c = c0)
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
                            if (line == ".") { data = false; Message = msg.ToString(); lock (Messages) Messages.Add(Message); msg.Length = 0; wr.WriteLine("250 2.0.0 queued"); continue; }
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
    /// <summary>Records POSTed bodies; answers 200 (or Status).</summary>
    public class FakeWebhook : IDisposable
    {
        readonly HttpListener l = new HttpListener();
        public readonly List<string> Bodies = new List<string>();
        public readonly List<string> Paths = new List<string>();
        public int Port;
        public int Status = 200;
        public string Url { get { return "http://127.0.0.1:" + Port + "/hook"; } }

        public FakeWebhook()
        {
            Port = 31000 + new Random().Next(4000);
            l.Prefixes.Add("http://127.0.0.1:" + Port + "/");
            l.Start();
            Thread t = new Thread(delegate()
            {
                while (true)
                {
                    HttpListenerContext ctx;
                    try { ctx = l.GetContext(); } catch (Exception) { return; }
                    string body;
                    using (StreamReader r = new StreamReader(ctx.Request.InputStream)) body = r.ReadToEnd();
                    lock (Bodies) { Bodies.Add(body); Paths.Add(ctx.Request.HttpMethod + " " + ctx.Request.RawUrl); }
                    ctx.Response.StatusCode = Status;
                    byte[] b = Encoding.UTF8.GetBytes("ok");
                    ctx.Response.OutputStream.Write(b, 0, b.Length);
                    ctx.Response.Close();
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        public void Dispose() { try { l.Stop(); } catch (Exception) { } }
    }
}
