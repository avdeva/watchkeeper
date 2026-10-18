// Network helpers shared by alerts, the vMix/HTTP actions of GPIO triggers and program health
// checks: HTTP requests with explicit proxy choice and readable failures, TCP reachability
// probes, and a step-by-step trace that shows exactly where a connection fails.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Watchkeeper
{
    /// <summary>Numbered, timed lines describing what a network operation did.</summary>
    public class NetTrace
    {
        readonly Stopwatch clock = Stopwatch.StartNew();
        public readonly List<string> Lines = new List<string>();

        public void Add(string line)
        {
            lock (Lines) Lines.Add(string.Format(Util.Inv, "{0,6:0.000}s  {1}", clock.Elapsed.TotalSeconds, line));
        }

        public override string ToString()
        {
            lock (Lines) return string.Join("\n", Lines.ToArray());
        }
    }

    /// <summary>A failure with the step it happened in and a plain explanation of the likely cause.</summary>
    public class NetFailure : Exception
    {
        public readonly string Step;
        public readonly string Explanation;

        public NetFailure(string step, string message, string explanation, Exception inner)
            : base(message + (string.IsNullOrEmpty(explanation) ? "" : " " + explanation), inner)
        {
            Step = step;
            Explanation = explanation ?? "";
        }
    }

    public class HttpResult
    {
        public int Status;              // 0 = no HTTP answer
        public string Body = "";
        public string Error = "";       // empty when an HTTP answer arrived
        public double Seconds;
        /// <summary>Set for a 3xx answer: where it pointed (host only) and why it was not followed.</summary>
        public string Redirect = "";
        public string Location = "";
        public bool Answered { get { return Status > 0; } }
        public bool Success { get { return Status >= 200 && Status < 300; } }
    }

    public class HttpRequestSpec
    {
        public string Method = "GET";
        public string Url = "";
        public string Body = "";
        public string ContentType = "application/json";
        public string User = "";
        public string Password = "";
        public string Proxy = "none";        // system | none | manual
        public string ProxyUrl = "";
        /// <summary>Send this computer's Windows login to the proxy (off: a proxy that wants a login refuses with 407).</summary>
        public bool ProxyLogin;
        /// <summary>Send the login (User/Password or an Authorization header) over plain http to another computer.
        /// Off: refused, since anyone on the network path can read it. Loopback addresses are always allowed.</summary>
        public bool AllowPlainLogin;
        public int TimeoutMs = 10000;
        public int MaxBody = 4000;
        /// <summary>Write the address path to traces (false: only scheme, host and port; webhook paths are keys).</summary>
        public bool TracePath;
        /// <summary>Extra headers (Authorization, tokens...). Values are not written to traces.</summary>
        public List<KeyValuePair<string, string>> Headers = new List<KeyValuePair<string, string>>();

        public void Header(string name, string value) { Headers.Add(new KeyValuePair<string, string>(name, value)); }

        /// <summary>Add "Name: value" lines.</summary>
        public void HeaderLines(string text)
        {
            foreach (string line in (text ?? "").Split('\n'))
            {
                int c = line.IndexOf(':');
                if (c > 0) Header(line.Substring(0, c).Trim(), line.Substring(c + 1).Trim());
            }
        }
    }

    public static class Net
    {
        /// <summary>Resolve a host name; the addresses are written to the trace. Throws NetFailure.</summary>
        public static IPAddress[] Resolve(string host, NetTrace trace)
        {
            IPAddress ip;
            if (IPAddress.TryParse(host, out ip)) return new[] { ip };
            try
            {
                IPAddress[] all = Dns.GetHostAddresses(host);
                List<IPAddress> ordered = new List<IPAddress>();
                foreach (IPAddress a in all) if (a.AddressFamily == AddressFamily.InterNetwork) ordered.Add(a);
                foreach (IPAddress a in all) if (a.AddressFamily != AddressFamily.InterNetwork) ordered.Add(a);
                if (trace != null)
                {
                    List<string> s = new List<string>();
                    foreach (IPAddress a in ordered) s.Add(a.ToString());
                    trace.Add("DNS: " + host + " = " + (s.Count > 0 ? string.Join(", ", s.ToArray()) : "(no addresses)"));
                }
                if (ordered.Count == 0) throw new NetFailure("dns", host + " has no addresses.", "Check the server name.", null);
                return ordered.ToArray();
            }
            catch (SocketException ex)
            {
                if (trace != null) trace.Add("DNS: " + host + " not found (" + ex.Message + ")");
                throw new NetFailure("dns", "The server name " + host + " could not be found.",
                                     "Check the spelling and that this server can resolve internet names (DNS).", ex);
            }
        }

        /// <summary>Open a TCP connection with a time limit, trying each address in turn. Throws NetFailure.</summary>
        public static TcpClient Connect(string host, int port, int timeoutMs, NetTrace trace)
        {
            IPAddress[] addrs = Resolve(host, trace);
            Exception last = null;
            bool timedOut = false;
            foreach (IPAddress a in addrs)
            {
                TcpClient c = new TcpClient(a.AddressFamily);
                if (trace != null) trace.Add("Connecting to " + a + ":" + port + "...");
                try
                {
                    IAsyncResult ar = c.BeginConnect(a, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
                    {
                        timedOut = true;
                        if (trace != null) trace.Add("  no answer within " + timeoutMs / 1000.0 + " s");
                        try { c.Close(); } catch (Exception) { }
                        continue;
                    }
                    c.EndConnect(ar);
                    if (trace != null) trace.Add("  connected");
                    return c;
                }
                catch (Exception ex)
                {
                    last = ex;
                    if (trace != null) trace.Add("  failed: " + ex.Message);
                    try { c.Close(); } catch (Exception) { }
                }
            }
            if (timedOut && last == null)
                throw new NetFailure("connect", "No answer from " + host + ":" + port + " within " + timeoutMs / 1000 + " s.",
                                     "Something between this server and " + host + " drops the connection: a site firewall or router, the internet provider " +
                                     "(port 25 is often blocked), a proxy that must be used instead, or antivirus. Check from this server with: " +
                                     "Test-NetConnection " + host + " -Port " + port, null);
            throw new NetFailure("connect", "Cannot connect to " + host + ":" + port + " (" + (last != null ? last.Message : "unknown error") + ").",
                                 "Nothing is listening on that port, or a firewall actively refuses the connection. Check the server name and port.", last);
        }

        /// <summary>True when a TCP connection can be opened; used by health checks ("tcp://host:port").</summary>
        public static string TcpProbe(string host, int port, int timeoutMs)
        {
            try
            {
                using (TcpClient c = Connect(host, port, timeoutMs, null)) return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        static IWebProxy ProxyFor(HttpRequestSpec s)
        {
            switch (s.Proxy)
            {
                case "none": return null;
                case "manual":
                    {
                        WebProxy p = new WebProxy(s.ProxyUrl.Contains("://") ? s.ProxyUrl : "http://" + s.ProxyUrl);
                        p.UseDefaultCredentials = s.ProxyLogin;
                        return p;
                    }
                default:
                    {
                        // Windows' own proxy settings (which may be found automatically on the network). This computer's
                        // Windows login is only sent when explicitly allowed: an impostor proxy could relay it.
                        IWebProxy p = WebRequest.GetSystemWebProxy();
                        if (p != null) p.Credentials = s.ProxyLogin ? CredentialCache.DefaultCredentials : null;
                        return p;
                    }
            }
        }

        public static string ProxyDescription(HttpRequestSpec s, Uri target)
        {
            try
            {
                IWebProxy p = ProxyFor(s);
                if (p == null) return "no proxy (direct)";
                if (p.IsBypassed(target)) return (s.Proxy == "manual" ? "proxy bypassed for this address" : "system setting: direct");
                Uri via = p.GetProxy(target);
                if (via == null || via == target) return s.Proxy == "manual" ? "proxy " + s.ProxyUrl : "system setting: direct";
                return "proxy " + via.GetLeftPart(UriPartial.Authority);
            }
            catch (Exception ex) { return "proxy setting unreadable (" + ex.Message + ")"; }
        }

        /// <summary>Send one HTTP request. Never throws: failures are in HttpResult.Error.</summary>
        public static HttpResult Send(HttpRequestSpec s, NetTrace trace)
        {
            HttpResult r = new HttpResult();
            Stopwatch sw = Stopwatch.StartNew();
            Uri uri;
            if (!Uri.TryCreate((s.Url ?? "").Trim(), UriKind.Absolute, out uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                r.Error = "the address must start with http:// or https:// (" + s.Url + ")";
                if (trace != null) trace.Add("Invalid address: " + s.Url);
                return r;
            }
            string plain = PlainLoginProblem(uri, s.User, s.Headers, s.AllowPlainLogin);
            if (plain != null)
            {
                r.Error = plain;
                if (trace != null) trace.Add("Not sent: " + plain);
                return r;
            }
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(uri);
                req.Method = string.IsNullOrEmpty(s.Method) ? "GET" : s.Method.ToUpperInvariant();
                req.Timeout = s.TimeoutMs;
                req.ReadWriteTimeout = s.TimeoutMs;
                req.KeepAlive = false;
                // never follow redirects: the login, secret headers and the alert would go to wherever the answer points
                req.AllowAutoRedirect = false;
                req.UserAgent = "Watchkeeper/" + Engine.Version;
                req.Proxy = ProxyFor(s);
                if (!string.IsNullOrEmpty(s.User))
                    req.Headers["Authorization"] = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(s.User + ":" + (s.Password ?? "")));
                req.Accept = "application/json, */*";
                foreach (KeyValuePair<string, string> h in s.Headers)
                {
                    if (string.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)) s.ContentType = h.Value;
                    else if (string.Equals(h.Key, "Accept", StringComparison.OrdinalIgnoreCase)) req.Accept = h.Value;
                    else if (string.Equals(h.Key, "User-Agent", StringComparison.OrdinalIgnoreCase)) req.UserAgent = h.Value;
                    else req.Headers[h.Key] = h.Value;
                }
                if (trace != null) trace.Add(req.Method + " " + Shown(uri, s.TracePath) + " via " + ProxyDescription(s, uri));
                bool hasBody = req.Method != "GET" && req.Method != "HEAD";
                if (hasBody)
                {
                    byte[] data = Encoding.UTF8.GetBytes(s.Body ?? "");
                    req.ContentType = string.IsNullOrEmpty(s.ContentType) ? "application/json" : s.ContentType;
                    req.ContentLength = data.Length;
                    using (Stream st = req.GetRequestStream()) st.Write(data, 0, data.Length);
                    if (trace != null) trace.Add("Sent " + data.Length + " bytes (" + req.ContentType + ")");
                }
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    r.Status = (int)resp.StatusCode;
                    r.Body = ReadBody(resp, s.MaxBody);
                    Uri loc;
                    string where = resp.Headers["Location"];
                    if (!string.IsNullOrEmpty(where) && Uri.TryCreate(uri, where, out loc)) r.Location = Shown(loc, false);
                }
            }
            catch (WebException ex)
            {
                HttpWebResponse resp = ex.Response as HttpWebResponse;
                if (resp != null)
                {
                    r.Status = (int)resp.StatusCode;
                    try { r.Body = ReadBody(resp, s.MaxBody); } catch (Exception) { }
                    try { resp.Close(); } catch (Exception) { }
                }
                else r.Error = Explain(ex, uri, s);
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
            }
            r.Seconds = sw.Elapsed.TotalSeconds;
            if (r.Status >= 300 && r.Status < 400 && r.Error.Length == 0)
                r.Redirect = "the server answered with a redirect (HTTP " + r.Status + ") to " + (r.Location.Length > 0 ? r.Location : "another address") +
                             "; redirects are not followed, so enter the final address";
            if (trace != null)
            {
                if (r.Answered) trace.Add("HTTP " + r.Status + " after " + r.Seconds.ToString("0.00", Util.Inv) + " s" +
                                          (r.Body.Length > 0 ? ": " + Redact(OneLine(r.Body, 160)) : ""));
                else trace.Add("Failed after " + r.Seconds.ToString("0.00", Util.Inv) + " s: " + r.Error);
            }
            return r;
        }

        static string ReadBody(HttpWebResponse resp, int max)
        {
            using (Stream st = resp.GetResponseStream())
            {
                if (st == null) return "";
                using (StreamReader rd = new StreamReader(st, Encoding.UTF8))
                {
                    char[] buf = new char[Math.Max(256, max)];
                    int n = rd.ReadBlock(buf, 0, buf.Length);
                    return new string(buf, 0, n);
                }
            }
        }

        /// <summary>An address as it may appear in traces and logs: never a user name or password; the path only when asked.</summary>
        public static string Shown(Uri uri, bool path)
        {
            string s = uri.Scheme + "://" + uri.Host + (uri.IsDefaultPort ? "" : ":" + uri.Port.ToString(Util.Inv));
            return path ? s + uri.AbsolutePath : s + (uri.AbsolutePath.Length > 1 ? "/..." : "");
        }

        static readonly System.Text.RegularExpressions.Regex TokenLike = new System.Text.RegularExpressions.Regex(
            @"(?i)(bearer\s+|token[""'=:\s]+|key[""'=:\s]+|secret[""'=:\s]+|password[""'=:\s]+|sig=)?[A-Za-z0-9_\-\.%+/]{24,}={0,2}");

        /// <summary>Server answers and errors may echo keys: long token-like strings are masked.</summary>
        public static string Redact(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            return TokenLike.Replace(text, delegate(System.Text.RegularExpressions.Match m) { return m.Value.Length > 0 && m.Groups[1].Success ? m.Groups[1].Value + "[hidden]" : "[hidden]"; });
        }

        public static string OneLine(string s, int max)
        {
            string t = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            while (t.Contains("  ")) t = t.Replace("  ", " ");
            return t.Length > max ? t.Substring(0, max) + "..." : t;
        }

        static string Explain(WebException ex, Uri uri, HttpRequestSpec s)
        {
            string host = uri.Host;
            switch (ex.Status)
            {
                case WebExceptionStatus.NameResolutionFailure:
                    return "the server name " + host + " could not be found (DNS). Check the address and this server's DNS settings.";
                case WebExceptionStatus.ProxyNameResolutionFailure:
                    return "the proxy server name could not be found. Check the proxy setting.";
                case WebExceptionStatus.ConnectFailure:
                    return "cannot connect to " + host + " (" + Inner(ex) + "). A firewall refuses it, nothing listens there, or a proxy must be used.";
                case WebExceptionStatus.Timeout:
                    return "no answer within " + s.TimeoutMs / 1000 + " s. The connection is dropped on the way (firewall, or a proxy must be used: set one under Proxy) " +
                           "or the server is too slow.";
                case WebExceptionStatus.TrustFailure:
                    return "the server's certificate is not trusted (" + Inner(ex) + "). An inspecting firewall may be replacing it, or the server's certificate is wrong.";
                case WebExceptionStatus.SecureChannelFailure:
                    return "the secure (TLS) connection failed (" + Inner(ex) + "). Windows may need TLS 1.2 enabled, or a firewall interferes.";
                case WebExceptionStatus.ConnectionClosed:
                case WebExceptionStatus.KeepAliveFailure:
                case WebExceptionStatus.ReceiveFailure:
                case WebExceptionStatus.SendFailure:
                    return "the connection was closed during the request (" + Inner(ex) + ").";
            }
            return ex.Message + (ex.InnerException != null ? " (" + ex.InnerException.Message + ")" : "");
        }

        static string Inner(Exception ex)
        {
            Exception e = ex;
            while (e.InnerException != null) e = e.InnerException;
            return e.Message;
        }

        /// <summary>Health check: "tcp://host:port" opens a connection; http(s) URLs must answer with a status below 500.
        /// Returns null when healthy, otherwise the reason.</summary>
        public static string Probe(string target, int timeoutMs) { return Probe(target, timeoutMs, "", "", false); }

        /// <summary>Null when a login may be sent to this address: https, a loopback address, or allowed explicitly.</summary>
        public static string PlainLoginProblem(Uri uri, string user, List<KeyValuePair<string, string>> headers, bool allowed)
        {
            if (allowed || uri == null || !string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase)) return null;
            bool login = !string.IsNullOrEmpty(user);
            if (headers != null)
                foreach (KeyValuePair<string, string> h in headers)
                    if (string.Equals(h.Key, "Authorization", StringComparison.OrdinalIgnoreCase) || string.Equals(h.Key, "Cookie", StringComparison.OrdinalIgnoreCase)) login = true;
            if (!login || IsLoopback(uri.Host)) return null;
            return "the login is not sent to " + uri.Host + " over plain http, where anyone on the network path could read it. " +
                   "Use an https address, or switch on 'Send the login unencrypted' for this target if the device only offers http";
        }

        public static bool IsLoopback(string host)
        {
            string h = (host ?? "").Trim().Trim('[', ']');
            if (string.Equals(h, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
            IPAddress ip;
            return IPAddress.TryParse(h, out ip) && IPAddress.IsLoopback(ip);
        }

        public static string Probe(string target, int timeoutMs, string user, string password) { return Probe(target, timeoutMs, user, password, false); }

        public static string Probe(string target, int timeoutMs, string user, string password, bool allowPlainLogin)
        {
            string t = (target ?? "").Trim();
            if (t.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase))
            {
                string hp = t.Substring(6).TrimEnd('/');
                int colon = hp.LastIndexOf(':');
                int port;
                if (colon <= 0 || !int.TryParse(hp.Substring(colon + 1), out port)) return "health check address must look like tcp://host:port";
                return TcpProbe(hp.Substring(0, colon), port, timeoutMs);
            }
            HttpRequestSpec s = new HttpRequestSpec();
            s.Url = t;
            s.User = user ?? "";
            s.Password = password ?? "";
            s.AllowPlainLogin = allowPlainLogin;
            s.Proxy = "none";
            s.TimeoutMs = timeoutMs;
            s.MaxBody = 200;
            HttpResult r = Send(s, null);
            if (!r.Answered) return r.Error;
            if (r.Status >= 500) return "answered HTTP " + r.Status;
            return null;
        }

        /// <summary>The certificate errors that count. A revoked certificate always does. "Revocation status unknown" or
        /// "revocation server offline" alone does not (soft fail, as browsers do), so servers that cannot reach the
        /// revocation lists keep working; everything else (wrong name, untrusted, expired) still counts.</summary>
        public static System.Net.Security.SslPolicyErrors CertificateVerdict(System.Net.Security.SslPolicyErrors errors,
                                                                             System.Security.Cryptography.X509Certificates.X509Chain chain)
        {
            if ((errors & System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors) == 0 || chain == null) return errors;
            const System.Security.Cryptography.X509Certificates.X509ChainStatusFlags soft =
                System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.RevocationStatusUnknown |
                System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.OfflineRevocation;
            foreach (System.Security.Cryptography.X509Certificates.X509ChainStatus st in chain.ChainStatus)
                if ((st.Status & ~soft) != 0) return errors;
            return errors & ~System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors;
        }

        public static bool IsRevoked(System.Security.Cryptography.X509Certificates.X509Chain chain)
        {
            if (chain == null) return false;
            foreach (System.Security.Cryptography.X509Certificates.X509ChainStatus st in chain.ChainStatus)
                if ((st.Status & System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.Revoked) != 0) return true;
            return false;
        }

        /// <summary>TLS 1.2 only, whatever older versions the machine default still allows. TLS 1.3 is not requested:
        /// on Windows versions without it, asking for it makes connections fail instead of falling back.</summary>
        public static void EnableModernTls()
        {
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } catch (Exception) { }
            // revoked certificates are refused; when the revocation lists cannot be reached the usual checks still decide
            try
            {
                ServicePointManager.CheckCertificateRevocationList = true;
                ServicePointManager.ServerCertificateValidationCallback = delegate(object sender, System.Security.Cryptography.X509Certificates.X509Certificate cert,
                                                                                   System.Security.Cryptography.X509Certificates.X509Chain chain, System.Net.Security.SslPolicyErrors errors)
                {
                    return CertificateVerdict(errors, chain) == System.Net.Security.SslPolicyErrors.None;
                };
            }
            catch (Exception) { }
            try { ServicePointManager.Expect100Continue = false; } catch (Exception) { }
        }
    }
}
