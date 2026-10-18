// Sends alert email. Written out instead of using System.Net.Mail so that every step (name
// lookup, connection, greeting, TLS, login, sender, recipients, message) is visible in a trace,
// failures name the step and the likely cause, and implicit TLS (port 465) works.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Watchkeeper
{
    public class SmtpSettings
    {
        public string Host = "";
        public int Port = 587;
        public string Tls = "starttls";          // starttls | implicit | none
        /// <summary>Accept a certificate that does not validate only when its SHA-256 fingerprint is exactly this
        /// (an internal server with its own certificate). Empty: normal validation only.</summary>
        public string TrustFingerprint = "";
        public string User = "";
        public string Password = "";
        public string From = "";
        public List<string> To = new List<string>();
        public int TimeoutMs = 20000;
    }

    public class SmtpSender
    {
        readonly SmtpSettings s;
        readonly NetTrace trace;
        TcpClient client;
        Stream stream;
        string step = "connecting";
        readonly List<string> capabilities = new List<string>();

        SmtpSender(SmtpSettings settings, NetTrace trace)
        {
            s = settings;
            this.trace = trace ?? new NetTrace();
        }

        /// <summary>Send one plain-text message. Throws NetFailure naming the step that failed.</summary>
        public static void Send(SmtpSettings settings, string subject, string body, NetTrace trace)
        {
            SmtpSender m = new SmtpSender(settings, trace);
            try { m.Run(subject, body); }
            finally { m.Close(); }
        }

        void Close()
        {
            try { if (stream != null) stream.Close(); } catch (Exception) { }
            try { if (client != null) client.Close(); } catch (Exception) { }
        }

        void Run(string subject, string body)
        {
            if (s.Host.Trim().Length == 0) throw new NetFailure("settings", "No SMTP server is set.", "", null);
            if (s.To.Count == 0) throw new NetFailure("settings", "No recipient is set.", "", null);
            trace.Add("SMTP " + s.Host + ":" + s.Port + ", " + (s.Tls == "implicit" ? "implicit TLS" : s.Tls == "starttls" ? "STARTTLS" : "no encryption") +
                      (s.User.Length > 0 ? ", login as " + s.User : ", no login"));
            step = "connecting";
            client = Net.Connect(s.Host.Trim(), s.Port, s.TimeoutMs, trace);
            client.ReceiveTimeout = s.TimeoutMs;
            client.SendTimeout = s.TimeoutMs;
            NetworkStream ns = client.GetStream();
            ns.ReadTimeout = s.TimeoutMs;
            ns.WriteTimeout = s.TimeoutMs;
            stream = ns;

            if (s.Tls == "implicit") StartTls();

            step = "waiting for the server's greeting";
            Reply greet = Read();
            if (greet.Code != 220) Fail(greet, "The server did not accept the connection.");

            Ehlo();
            if (s.Tls == "starttls")
            {
                if (!Has("STARTTLS"))
                    throw new NetFailure("STARTTLS", "The server does not offer STARTTLS on port " + s.Port + ".",
                                         "Use port 587 for a mail provider, or choose 'No encryption' for an internal relay that does not support it.", null);
                step = "STARTTLS";
                Reply r = Command("STARTTLS", null);
                if (r.Code != 220) Fail(r, "The server refused to start TLS.");
                StartTls();
                Ehlo();
            }

            if (s.User.Length > 0)
            {
                if (!(stream is SslStream))
                    throw new NetFailure("login", "The user name and password are not sent over a connection without encryption.",
                                         "Choose STARTTLS (port 587) or implicit TLS (port 465), or leave the user name empty for a relay that accepts this server by its IP address.", null);
                Login();
            }

            step = "sender (MAIL FROM)";
            Reply mf = Command("MAIL FROM:<" + Address(s.From) + ">", null);
            if (mf.Code != 250) Fail(mf, "The sender address was refused. Most providers only accept the address of the mailbox you log in with " +
                                     "(or one it may send as).");
            foreach (string to in s.To)
            {
                step = "recipient (RCPT TO " + to + ")";
                Reply rt = Command("RCPT TO:<" + Address(to) + ">", null);
                if (rt.Code != 250 && rt.Code != 251)
                    Fail(rt, rt.Text.Contains("relay") || rt.Text.Contains("5.7.1")
                         ? "Relaying denied: this server is not allowed to send to that recipient. Log in, or have this server's IP address allowed on the relay."
                         : "The recipient was refused.");
            }
            step = "message (DATA)";
            Reply d = Command("DATA", null);
            if (d.Code != 354) Fail(d, "The server refused the message.");
            byte[] msg = Encoding.ASCII.GetBytes(BuildMessage(subject, body) + "\r\n.\r\n");
            stream.Write(msg, 0, msg.Length);
            stream.Flush();
            trace.Add("C: (message, " + msg.Length + " bytes)");
            Reply done = Read();
            if (done.Code != 250) Fail(done, "The server did not accept the message.");
            step = "QUIT";
            try { Command("QUIT", null); } catch (Exception) { }
            trace.Add("Message accepted by the server.");
        }

        // ------------------------------------------------------------- protocol
        class Reply
        {
            public int Code;
            public List<string> Lines = new List<string>();
            public string Text { get { return string.Join(" ", Lines.ToArray()); } }
        }

        void Ehlo()
        {
            step = "EHLO";
            string name = Environment.MachineName;
            Reply r = Command("EHLO " + name, null);
            if (r.Code != 250)
            {
                Reply h = Command("HELO " + name, null);
                if (h.Code != 250) Fail(h, "The server refused the greeting.");
                capabilities.Clear();
                return;
            }
            capabilities.Clear();
            for (int i = 1; i < r.Lines.Count; i++) capabilities.Add(r.Lines[i].Length > 4 ? r.Lines[i].Substring(4).Trim().ToUpperInvariant() : "");
        }

        bool Has(string cap)
        {
            foreach (string c in capabilities) if (c == cap || c.StartsWith(cap + " ") || c.StartsWith(cap + "=")) return true;
            return false;
        }

        List<string> AuthMechanisms()
        {
            List<string> o = new List<string>();
            foreach (string c in capabilities)
                if (c.StartsWith("AUTH ") || c.StartsWith("AUTH="))
                    foreach (string m in c.Substring(5).Split(' ')) if (m.Trim().Length > 0 && !o.Contains(m.Trim())) o.Add(m.Trim());
            return o;
        }

        void Login()
        {
            step = "login";
            List<string> mechs = AuthMechanisms();
            if (mechs.Count == 0)
                throw new NetFailure("login", "The server does not offer a login on this connection.",
                                     s.Tls == "none" ? "Most servers only offer login after STARTTLS: choose STARTTLS." :
                                     "For an internal relay that accepts this server by its IP address, leave the user name empty.", null);
            Reply r;
            if (mechs.Contains("LOGIN"))
            {
                r = Command("AUTH LOGIN", null);
                if (r.Code == 334)
                {
                    r = Command(B64(s.User), "(user name)");
                    if (r.Code == 334) r = Command(B64(s.Password), "(password)");
                }
            }
            else if (mechs.Contains("PLAIN"))
            {
                r = Command("AUTH PLAIN " + B64("\0" + s.User + "\0" + s.Password), "AUTH PLAIN (user name and password)");
            }
            else
            {
                throw new NetFailure("login", "The server only offers " + string.Join(", ", mechs.ToArray()) + " login, which needs a modern sign-in this program does not do.",
                                     "Use a provider or relay that accepts a user name and password (AUTH LOGIN/PLAIN), or a relay that accepts this server by IP address.", null);
            }
            if (r.Code == 235)
            {
                trace.Add("Logged in.");
                return;
            }
            string t = r.Text;
            string why;
            if (t.Contains("5.7.30")) why = "Microsoft 365 no longer accepts a user name and password for this mailbox (basic authentication turned off). " +
                                            "Use a relay (port 25 to your domain's mail.protection.outlook.com host) or another provider.";
            else if (t.Contains("5.7.139") || t.ToLowerInvariant().Contains("smtpclientauthentication"))
                why = "Microsoft 365: SMTP login (Authenticated SMTP) is switched off for this mailbox or the whole organisation, or blocked by security defaults / MFA.";
            else if (t.Contains("5.7.8") || t.ToLowerInvariant().Contains("application-specific"))
                why = "Gmail: use an app password (needs 2-Step Verification), not the normal password.";
            else why = "The user name or password was rejected. Microsoft 365: the mailbox needs Authenticated SMTP switched on and MFA / security defaults must not block it. " +
                       "Gmail: use an app password.";
            Fail(r, why);
        }

        void StartTls()
        {
            step = "TLS handshake";
            trace.Add("Starting TLS...");
            SslStream ssl = new SslStream(stream, false, ValidateCertificate);
            try
            {
                // TLS 1.2 only (TLS 1.3 is not requested: Windows versions without it would fail the handshake)
                // revocation checked: a revoked certificate is refused (an unreachable revocation list alone is not fatal)
                ssl.AuthenticateAsClient(s.Host.Trim(), null, SslProtocols.Tls12, true);
            }
            catch (AuthenticationException ex)
            {
                if (certProblem.Length == 0 && Deepest(ex).Contains("CERTIFICATE_VERIFY")) certProblem = "The server's certificate was refused.";
                throw new NetFailure("TLS", "The TLS handshake failed: " + Deepest(ex) + ".",
                                     certProblem.Length > 0 ? certProblem :
                                     s.Tls == "implicit" ? "If this port expects plain text first (587 or 25), choose STARTTLS instead of implicit TLS." :
                                     "Windows may need TLS 1.2 enabled, or a firewall interferes with the connection.", ex);
            }
            catch (Exception ex)
            {
                if (ex is NetFailure) throw;
                throw new NetFailure("TLS", "The TLS handshake failed: " + Deepest(ex) + ".",
                                     s.Tls == "implicit" ? "If this port expects plain text first (587 or 25), choose STARTTLS instead of implicit TLS." :
                                     "The server or something in between closed the connection during TLS.", ex);
            }
            trace.Add("TLS established (" + ssl.SslProtocol + ", " + ssl.CipherAlgorithm + ")");
            ssl.ReadTimeout = s.TimeoutMs;
            ssl.WriteTimeout = s.TimeoutMs;
            stream = ssl;
        }

        string certProblem = "";

        bool ValidateCertificate(object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors errors)
        {
            string subject = cert != null ? cert.Subject : "(none)";
            if (Net.IsRevoked(chain))
            {
                certProblem = "The server's certificate (" + subject + ") has been revoked by its issuer. It is refused, also when its fingerprint is trusted.";
                trace.Add(certProblem);
                return false;
            }
            SslPolicyErrors counted = Net.CertificateVerdict(errors, chain);
            if (counted == SslPolicyErrors.None)
            {
                trace.Add("Certificate OK: " + subject + (counted != errors ? " (its revocation status could not be checked)" : ""));
                return true;
            }
            errors = counted;
            List<string> why = new List<string>();
            if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0) why.Add("issued for a different name (" + subject + ") than " + s.Host);
            if ((errors & SslPolicyErrors.RemoteCertificateChainErrors) != 0)
            {
                string detail = "";
                if (chain != null) foreach (X509ChainStatus st in chain.ChainStatus) detail += (detail.Length > 0 ? "; " : "") + st.StatusInformation.Trim();
                why.Add("not trusted" + (detail.Length > 0 ? " (" + detail + ")" : ""));
            }
            if ((errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0) why.Add("no certificate sent");
            string text = "Certificate " + string.Join(", ", why.ToArray());
            string fp = Fingerprint(cert);
            string want = Clean(s.TrustFingerprint);
            if (want.Length > 0 && fp.Length > 0 && want == fp)
            {
                trace.Add(text + " - accepted: its fingerprint is the one entered under 'Trust this certificate'");
                return true;
            }
            trace.Add(text + (fp.Length > 0 ? " (SHA-256 fingerprint " + fp + ")" : ""));
            certProblem = text + ". Use the name the certificate was issued for" +
                          (fp.Length > 0 ? ", or, for an internal server you trust, enter its fingerprint " + fp + " under 'Trust this certificate' (only exactly this certificate is then accepted)" : "") + ".";
            return false;
        }

        Reply Command(string line, string shown)
        {
            byte[] data = Encoding.ASCII.GetBytes(line + "\r\n");
            try
            {
                stream.Write(data, 0, data.Length);
                stream.Flush();
            }
            catch (NetFailure) { throw; }
            catch (Exception ex) { throw Broken(ex); }
            trace.Add("C: " + (shown ?? line));
            return Read();
        }

        Reply Read()
        {
            Reply r = new Reply();
            while (true)
            {
                string line = ReadLine();
                trace.Add("S: " + line);
                r.Lines.Add(line);
                int code;
                if (line.Length < 3 || !int.TryParse(line.Substring(0, 3), out code))
                    throw new NetFailure(step, "Unexpected answer from the server: " + line, "This port may not be an SMTP server.", null);
                r.Code = code;
                if (line.Length == 3 || line[3] != '-') return r;
            }
        }

        string ReadLine()
        {
            List<byte> buf = new List<byte>();
            try
            {
                while (true)
                {
                    int b = stream.ReadByte();
                    if (b < 0)
                    {
                        if (buf.Count == 0) throw new NetFailure(step, "The server closed the connection while " + step + ".",
                                                                 "It may not like this server (blocked IP) or the connection settings.", null);
                        break;
                    }
                    if (b == '\n') break;
                    if (b != '\r') buf.Add((byte)b);
                    if (buf.Count > 4096) break;
                }
            }
            catch (NetFailure) { throw; }
            catch (Exception ex) { throw Broken(ex); }
            return Encoding.UTF8.GetString(buf.ToArray());
        }

        NetFailure Broken(Exception ex)
        {
            SocketException se = null;
            for (Exception e = ex; e != null; e = e.InnerException)
            {
                AggregateException ag = e as AggregateException;
                if (ag != null && ag.InnerExceptions.Count > 0 && ag.InnerException == null) { e = ag.InnerExceptions[0]; }
                if (e is SocketException) { se = (SocketException)e; break; }
            }
            bool timedOut = se != null && (se.SocketErrorCode == SocketError.TimedOut || se.SocketErrorCode == SocketError.WouldBlock)
                            || ex.Message.ToLowerInvariant().Contains("timed out");
            if (timedOut)
            {
                string hint = step == "waiting for the server's greeting"
                    ? "Connected, but the server said nothing. If this port expects TLS straight away (usually 465), choose 'Implicit TLS'. " +
                      "Antivirus or a firewall that inspects mail can also hold the connection."
                    : step == "login" ? "Some servers stop answering after a wrong user name or password. Check both; Gmail needs an app password."
                    : "The connection stalled. A firewall or antivirus that inspects mail may be interfering.";
                return new NetFailure(step, "No answer within " + s.TimeoutMs / 1000 + " s while " + step + ".", hint, ex);
            }
            return new NetFailure(step, "The connection broke while " + step + ": " + Deepest(ex) + ".", "", ex);
        }

        void Fail(Reply r, string why)
        {
            throw new NetFailure(step, "The server answered " + r.Text + " while " + step + ".", why, null);
        }

        static string Deepest(Exception ex)
        {
            Exception e = ex;
            while (e.InnerException != null) e = e.InnerException;
            string m = (e.Message ?? "").Trim();
            int nl = m.IndexOf('\n');
            return (nl > 0 ? m.Substring(0, nl) : m).Trim();
        }

        public static string Clean(string fingerprint)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in fingerprint ?? "") if (Uri.IsHexDigit(c)) sb.Append(char.ToUpperInvariant(c));
            return sb.ToString();
        }

        public static string Fingerprint(X509Certificate cert)
        {
            if (cert == null) return "";
            try
            {
                using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
                {
                    byte[] h = sha.ComputeHash(cert.GetRawCertData());
                    return BitConverter.ToString(h).Replace("-", "");
                }
            }
            catch (Exception) { return ""; }
        }

        static string B64(string s) { return Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? "")); }

        /// <summary>The bare address from "Name &lt;addr&gt;" or "addr".</summary>
        public static string Address(string a)
        {
            string t = (a ?? "").Trim();
            int lt = t.LastIndexOf('<'), gt = t.LastIndexOf('>');
            if (lt >= 0 && gt > lt) t = t.Substring(lt + 1, gt - lt - 1);
            return t.Trim();
        }

        static string Header(string text)
        {
            foreach (char c in text) if (c > 126 || c < 32) return "=?utf-8?B?" + B64(text) + "?=";
            return text;
        }

        string BuildMessage(string subject, string body)
        {
            DateTime now = DateTime.Now;
            TimeSpan off = TimeZone.CurrentTimeZone.GetUtcOffset(now);
            string zone = (off < TimeSpan.Zero ? "-" : "+") + Math.Abs(off.Hours).ToString("00") + Math.Abs(off.Minutes).ToString("00");
            StringBuilder sb = new StringBuilder();
            sb.Append("Date: ").Append(now.ToString("ddd, dd MMM yyyy HH:mm:ss ", Util.Inv)).Append(zone).Append("\r\n");
            sb.Append("From: ").Append(s.From.Trim()).Append("\r\n");
            sb.Append("To: ").Append(string.Join(", ", s.To.ToArray())).Append("\r\n");
            sb.Append("Subject: ").Append(Header(subject ?? "")).Append("\r\n");
            sb.Append("Message-ID: <").Append(Guid.NewGuid().ToString("N")).Append("@").Append(Environment.MachineName.ToLowerInvariant()).Append(">\r\n");
            sb.Append("MIME-Version: 1.0\r\n");
            sb.Append("Content-Type: text/plain; charset=utf-8\r\n");
            sb.Append("Content-Transfer-Encoding: base64\r\n\r\n");
            string b = Convert.ToBase64String(Encoding.UTF8.GetBytes((body ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n")));
            for (int i = 0; i < b.Length; i += 76) sb.Append(b.Substring(i, Math.Min(76, b.Length - i))).Append("\r\n");
            return sb.ToString().TrimEnd('\r', '\n');
        }
    }
}
