// Fakes shared by the core tests and the UI test: an Axia device speaking LWRP, a vMix Web API,
// and Windows services.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Watchkeeper;

namespace Watchkeeper.Tests
{
    public class FakeServices : IServiceControl
    {
        public readonly Dictionary<string, string> State = new Dictionary<string, string>();
        public readonly List<string> Calls = new List<string>();
        public bool StartGoesStraightToRunning = true;

        public string Status(string name)
        {
            string s;
            return State.TryGetValue(name, out s) ? s : "missing";
        }

        public void Start(string name)
        {
            Calls.Add("start " + name);
            State[name] = StartGoesStraightToRunning ? "running" : "start_pending";
        }

        public void Stop(string name, int waitMs)
        {
            Calls.Add("stop " + name);
            State[name] = "stopped";
        }

        public List<KeyValuePair<string, string>> List()
        {
            List<KeyValuePair<string, string>> o = new List<KeyValuePair<string, string>>();
            foreach (string k in State.Keys) o.Add(new KeyValuePair<string, string>(k, k + " display"));
            return o;
        }
    }

    /// <summary>Speaks enough LWRP: answers VER and ADD GPI/GPO with a BEGIN/END state block, then sends what the test pushes.</summary>
    public class FakeAxia : IDisposable
    {
        readonly TcpListener listener;
        readonly Thread thread;
        readonly Queue<string> outgoing = new Queue<string>();
        public readonly List<string> Received = new List<string>();
        public volatile bool Connected;
        public string InitialGpi = "hhhhh";
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
                                else if (line == "ADD GPI")
                                {
                                    w.WriteLine("BEGIN");
                                    for (int p = 1; p <= 8; p++) w.WriteLine("GPI " + p + " " + (p == 1 ? InitialGpi : "hhhhh"));
                                    w.WriteLine("END");
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
    /// <summary>A stand-in for Pathfinder Core PRO's SapV2 port: login, set and get of memory slots.</summary>
    public class FakePathfinder : IDisposable
    {
        readonly TcpListener listener;
        readonly Thread thread;
        public readonly Dictionary<string, string> Slots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Received = new List<string>();
        public string User = "watchkeeper", Password = "s3cret";
        public int Logins;
        TcpClient current;
        public int Port { get { return ((IPEndPoint)listener.LocalEndpoint).Port; } }

        static readonly Regex SetRx = new Regex("^set MemorySlots#0\\.MemorySlot#(\\S+) SlotValue=\"(.*)\"$", RegexOptions.IgnoreCase);
        static readonly Regex GetRx = new Regex("^get MemorySlots#0\\.MemorySlot#(\\S+) SlotValue$", RegexOptions.IgnoreCase);

        public FakePathfinder()
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            thread = new Thread(Serve);
            thread.IsBackground = true;
            thread.Start();
        }

        public string Slot(string name) { lock (Slots) { string v; return Slots.TryGetValue(name, out v) ? v : null; } }
        public void SetSlot(string name, string value) { lock (Slots) Slots[name] = value; }
        public void Drop() { try { if (current != null) current.Close(); } catch (Exception) { } }

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
                    StreamReader r = new StreamReader(c.GetStream(), Encoding.UTF8);
                    StreamWriter w = new StreamWriter(c.GetStream(), new UTF8Encoding(false));
                    w.NewLine = "\r\n";
                    w.AutoFlush = true;
                    bool authed = false;
                    string line;
                    while ((line = r.ReadLine()) != null)
                    {
                        line = line.Trim();
                        lock (Received) Received.Add(line);
                        if (line.StartsWith("Login ", StringComparison.OrdinalIgnoreCase))
                        {
                            authed = line == "Login " + User + " " + Password;
                            w.WriteLine(authed ? "login successful" : "login failed");
                            if (authed) Logins++;
                            continue;
                        }
                        if (!authed) continue;
                        Match m = SetRx.Match(line);
                        if (m.Success) { SetSlot(m.Groups[1].Value, m.Groups[2].Value); continue; }
                        m = GetRx.Match(line);
                        if (m.Success)
                        {
                            string v = Slot(m.Groups[1].Value);
                            if (v != null) w.WriteLine("indi MemorySlots#0.MemorySlot#" + m.Groups[1].Value + " SlotValue=\"" + v + "\"");
                        }
                    }
                }
                catch (Exception) { }
                try { c.Close(); } catch (Exception) { }
            }
        }

        public void Dispose()
        {
            try { listener.Stop(); } catch (Exception) { }
            Drop();
        }
    }

}
