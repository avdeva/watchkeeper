// Small HTTP and TCP helpers for health checks, vMix, web requests, webhooks and Jira.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Watchkeeper
{
    public class HttpResult
    {
        /// <summary>The server answered (any status).</summary>
        public bool Answered;
        public int Status;
        public string Body = "";
        /// <summary>Why there was no answer.</summary>
        public string Error = "";
        public double Ms;
        public bool Success { get { return Answered && Status >= 200 && Status < 300; } }
    }

    public static class Http
    {
        static Http()
        {
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072 | (SecurityProtocolType)768; }   // TLS 1.2 and 1.1
            catch (Exception) { }
            ServicePointManager.Expect100Continue = false;
        }

        public static void Init() { }

        public static HttpResult Send(string method, string url, string body, string contentType, Dictionary<string, string> headers,
                                      string user, string password, int timeoutMs)
        {
            HttpResult r = new HttpResult();
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = method;
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.Proxy = null;
                req.AllowAutoRedirect = false;
                req.KeepAlive = false;
                req.UserAgent = "Watchkeeper";
                if (!string.IsNullOrEmpty(user))
                    req.Headers["Authorization"] = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + (password ?? "")));
                if (headers != null)
                    foreach (KeyValuePair<string, string> h in headers)
                    {
                        if (string.Equals(h.Key, "Accept", StringComparison.OrdinalIgnoreCase)) req.Accept = h.Value;
                        else if (string.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)) contentType = h.Value;
                        else req.Headers[h.Key] = h.Value;
                    }
                if (body != null && method != "GET")
                {
                    byte[] b = Encoding.UTF8.GetBytes(body);
                    req.ContentType = string.IsNullOrEmpty(contentType) ? "application/json" : contentType;
                    req.ContentLength = b.Length;
                    using (Stream s = req.GetRequestStream()) s.Write(b, 0, b.Length);
                }
                HttpWebResponse resp;
                try { resp = (HttpWebResponse)req.GetResponse(); }
                catch (WebException wex)
                {
                    resp = wex.Response as HttpWebResponse;
                    if (resp == null) throw;
                }
                using (resp)
                {
                    r.Answered = true;
                    r.Status = (int)resp.StatusCode;
                    using (Stream s = resp.GetResponseStream())
                    using (StreamReader rd = new StreamReader(s, Encoding.UTF8))
                    {
                        char[] buf = new char[200000];
                        int n = rd.ReadBlock(buf, 0, buf.Length);
                        r.Body = new string(buf, 0, n);
                    }
                }
            }
            catch (WebException ex) { r.Error = Describe(ex); }
            catch (Exception ex) { r.Error = ex.Message; }
            r.Ms = sw.Elapsed.TotalMilliseconds;
            return r;
        }

        static string Describe(WebException ex)
        {
            switch (ex.Status)
            {
                case WebExceptionStatus.Timeout: return "no answer in time";
                case WebExceptionStatus.ConnectFailure: return "connection refused or host unreachable";
                case WebExceptionStatus.NameResolutionFailure: return "the name could not be found (DNS)";
                case WebExceptionStatus.TrustFailure:
                case WebExceptionStatus.SecureChannelFailure: return "secure connection failed (certificate or TLS): " + ex.Message;
            }
            return ex.Message;
        }

        /// <summary>Null when the port accepts a connection, otherwise why not.</summary>
        public static string TcpProbe(string host, int port, int timeoutMs)
        {
            try
            {
                using (TcpClient c = new TcpClient())
                {
                    IAsyncResult ar = c.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return "no connection within " + (timeoutMs / 1000) + "s";
                    c.EndConnect(ar);
                    return null;
                }
            }
            catch (Exception ex) { return ex.Message; }
        }

        /// <summary>Health check: null when the address answers (HTTP status below 500, or a TCP connection), otherwise why not.</summary>
        public static string Probe(string target, string user, string password, int timeoutMs)
        {
            Uri u;
            if (!Uri.TryCreate(target, UriKind.Absolute, out u)) return "not a valid address: " + target;
            if (u.Scheme == "tcp") return TcpProbe(u.Host, u.Port, timeoutMs);
            HttpResult r = Send("GET", target, null, null, null, user, password, timeoutMs);
            if (!r.Answered) return r.Error;
            if (r.Status >= 500) return "answered HTTP " + r.Status;
            return null;
        }

        public static string JsonEscape(string s)
        {
            string q = Json.Serialize(s ?? "", false);
            return q.Substring(1, q.Length - 2);
        }
    }
}
