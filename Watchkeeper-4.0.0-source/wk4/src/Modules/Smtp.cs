// Sends alert e-mail. Written out (instead of System.Net.Mail) so that implicit TLS on port 465 works and a failure names
// the step that failed and its likely cause.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace Watchkeeper
{
    public class Smtp
    {
        readonly AlertDestination d;
        readonly string password;
        readonly int timeoutMs;
        TcpClient client;
        Stream stream;
        string step = "connecting";
        readonly List<string> caps = new List<string>();

        Smtp(AlertDestination d, string password, int timeoutMs) { this.d = d; this.password = password; this.timeoutMs = timeoutMs; }

        /// <summary>Send one plain-text message. Throws with the failing step and what to check.</summary>
        public static void Send(AlertDestination d, string password, string subject, string body, int timeoutMs)
        {
            Smtp m = new Smtp(d, password, timeoutMs);
            try { m.Run(subject, body); }
            finally
            {
                try { if (m.stream != null) m.stream.Close(); } catch (Exception) { }
                try { if (m.client != null) m.client.Close(); } catch (Exception) { }
            }
        }

        Exception Fail(string what, string hint)
        {
            return new InvalidOperationException(what + (hint.Length > 0 ? " " + hint : ""));
        }

        void Run(string subject, string body)
        {
            List<string> to = Util.Patterns(d.To);
            if (d.SmtpHost.Length == 0) throw Fail("No mail server is set.", "");
            if (to.Count == 0) throw Fail("No recipient is set.", "");
            client = new TcpClient();
            try
            {
                IAsyncResult ar = client.BeginConnect(d.SmtpHost, d.SmtpPort, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
                    throw Fail("No answer from " + d.SmtpHost + ":" + d.SmtpPort + " within " + timeoutMs / 1000 + "s.",
                               "A firewall, the internet provider (port 25 is often blocked) or antivirus may drop the connection.");
                client.EndConnect(ar);
            }
            catch (SocketException ex)
            {
                throw Fail("Cannot connect to " + d.SmtpHost + ":" + d.SmtpPort + " (" + ex.Message + ").", "Check the server name and port.");
            }
            client.ReceiveTimeout = client.SendTimeout = timeoutMs;
            stream = client.GetStream();
            if (d.SmtpSecurity == "ssl") StartTls();
            step = "waiting for the server's greeting";
            Expect(Read(), 220, "The server did not accept the connection.");
            Ehlo();
            if (d.SmtpSecurity == "starttls")
            {
                if (!Has("STARTTLS")) throw Fail("The server does not offer STARTTLS on port " + d.SmtpPort + ".", "Use port 587, choose SSL for port 465, or 'None' for an internal relay.");
                step = "STARTTLS";
                Expect(Command("STARTTLS", null), 220, "The server refused to start TLS.");
                StartTls();
                Ehlo();
            }
            if (d.SmtpUser.Length > 0)
            {
                if (!(stream is SslStream))
                    throw Fail("The password is not sent over a connection without encryption.", "Choose STARTTLS (port 587) or SSL (port 465), or leave the user name empty for a relay that accepts this server by its address.");
                Login();
            }
            step = "sender (MAIL FROM)";
            Expect(Command("MAIL FROM:<" + Address(d.From) + ">", null), 250, "The sender address was refused (most providers only accept the mailbox you log in with).");
            foreach (string r in to)
            {
                step = "recipient " + r;
                Reply rr = Command("RCPT TO:<" + Address(r) + ">", null);
                if (rr.Code != 250 && rr.Code != 251)
                    throw Fail("The server answered '" + rr.Text + "' for the recipient " + r + ".",
                               rr.Text.Contains("relay") || rr.Text.Contains("5.7.1") ? "Relaying denied: log in, or have this server's address allowed on the relay." : "");
            }
            step = "message (DATA)";
            Expect(Command("DATA", null), 354, "The server refused the message.");
            byte[] msg = Encoding.ASCII.GetBytes(Message(subject, body, to) + "\r\n.\r\n");
            stream.Write(msg, 0, msg.Length);
            stream.Flush();
            Expect(Read(), 250, "The server did not accept the message.");
            try { Command("QUIT", null); } catch (Exception) { }
        }

        class Reply { public int Code; public List<string> Lines = new List<string>(); public string Text { get { return string.Join(" ", Lines.ToArray()); } } }

        void Expect(Reply r, int code, string why)
        {
            if (r.Code != code) throw Fail("The server answered '" + r.Text + "' while " + step + ".", why);
        }

        void Ehlo()
        {
            step = "EHLO";
            Reply r = Command("EHLO " + Environment.MachineName, null);
            caps.Clear();
            if (r.Code != 250) { Expect(Command("HELO " + Environment.MachineName, null), 250, "The server refused the greeting."); return; }
            for (int i = 1; i < r.Lines.Count; i++) caps.Add(r.Lines[i].Length > 4 ? r.Lines[i].Substring(4).Trim().ToUpperInvariant() : "");
        }

        bool Has(string cap)
        {
            foreach (string c in caps) if (c == cap || c.StartsWith(cap + " ") || c.StartsWith(cap + "=")) return true;
            return false;
        }

        void Login()
        {
            step = "login";
            List<string> mechs = new List<string>();
            foreach (string c in caps)
                if (c.StartsWith("AUTH ") || c.StartsWith("AUTH="))
                    foreach (string m in c.Substring(5).Split(' ')) if (m.Trim().Length > 0) mechs.Add(m.Trim());
            Reply r;
            if (mechs.Contains("LOGIN"))
            {
                r = Command("AUTH LOGIN", null);
                if (r.Code == 334) { r = Command(B64(d.SmtpUser), "(user name)"); if (r.Code == 334) r = Command(B64(password), "(password)"); }
            }
            else if (mechs.Contains("PLAIN")) r = Command("AUTH PLAIN " + B64("\0" + d.SmtpUser + "\0" + password), "(login)");
            else throw Fail("The server offers no user name and password login here" + (mechs.Count > 0 ? " (only " + string.Join(", ", mechs.ToArray()) + ")" : "") + ".",
                            "Use a relay that accepts this server by its address (leave the user name empty).");
            if (r.Code == 235) return;
            string t = r.Text, why;
            if (t.Contains("5.7.139") || t.Contains("5.7.30") || t.ToLowerInvariant().Contains("smtpclientauthentication"))
                why = "Microsoft 365: SMTP login is switched off for this mailbox or the organisation, or blocked by security defaults / MFA.";
            else if (t.Contains("5.7.8") || t.ToLowerInvariant().Contains("application-specific")) why = "Gmail: use an app password, not the normal password.";
            else why = "The user name or password was rejected.";
            throw Fail("Login refused ('" + t + "').", why);
        }

        void StartTls()
        {
            step = "TLS handshake";
            SslStream ssl = new SslStream(stream, false);
            try { ssl.AuthenticateAsClient(d.SmtpHost, null, (SslProtocols)3072 /* TLS 1.2 */, true); }
            catch (Exception ex)
            {
                Exception e = ex;
                while (e.InnerException != null) e = e.InnerException;
                throw Fail("The secure connection failed: " + e.Message + ".",
                           d.SmtpSecurity == "ssl" ? "If the port expects plain text first (587 or 25), choose STARTTLS." : "Check that the server's certificate is valid for " + d.SmtpHost + ".");
            }
            ssl.ReadTimeout = ssl.WriteTimeout = timeoutMs;
            stream = ssl;
        }

        Reply Command(string line, string shown)
        {
            byte[] data = Encoding.ASCII.GetBytes(line + "\r\n");
            try { stream.Write(data, 0, data.Length); stream.Flush(); }
            catch (Exception ex) { throw Fail("The connection broke while " + step + ": " + ex.Message, ""); }
            return Read();
        }

        Reply Read()
        {
            Reply r = new Reply();
            while (true)
            {
                string line = ReadLine();
                r.Lines.Add(line);
                int code;
                if (line.Length < 3 || !int.TryParse(line.Substring(0, 3), out code)) throw Fail("Unexpected answer while " + step + ": " + line, "This port may not be a mail server.");
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
                    if (b < 0) { if (buf.Count == 0) throw Fail("The server closed the connection while " + step + ".", ""); break; }
                    if (b == '\n') break;
                    if (b != '\r') buf.Add((byte)b);
                    if (buf.Count > 4096) break;
                }
            }
            catch (InvalidOperationException) { throw; }
            catch (Exception ex)
            {
                throw Fail("No answer while " + step + " (" + ex.Message + ").",
                           step == "waiting for the server's greeting" ? "If the port expects a secure connection straight away (usually 465), choose SSL." : "");
            }
            return Encoding.UTF8.GetString(buf.ToArray());
        }

        static string B64(string s) { return Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? "")); }

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

        string Message(string subject, string body, List<string> to)
        {
            DateTime now = DateTime.Now;
            TimeSpan off = TimeZone.CurrentTimeZone.GetUtcOffset(now);
            string zone = (off < TimeSpan.Zero ? "-" : "+") + Math.Abs(off.Hours).ToString("00") + Math.Abs(off.Minutes).ToString("00");
            StringBuilder sb = new StringBuilder();
            sb.Append("Date: ").Append(now.ToString("ddd, dd MMM yyyy HH:mm:ss ", Util.Inv)).Append(zone).Append("\r\n");
            sb.Append("From: ").Append(d.From).Append("\r\n");
            sb.Append("To: ").Append(string.Join(", ", to.ToArray())).Append("\r\n");
            sb.Append("Subject: ").Append(Header(subject.Replace("\r", " ").Replace("\n", " "))).Append("\r\n");
            sb.Append("Message-ID: <").Append(Guid.NewGuid().ToString("N")).Append("@").Append(Environment.MachineName.ToLowerInvariant()).Append(">\r\n");
            sb.Append("MIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n");
            string b = Convert.ToBase64String(Encoding.UTF8.GetBytes((body ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n")));
            for (int i = 0; i < b.Length; i += 76) sb.Append(b.Substring(i, Math.Min(76, b.Length - i))).Append("\r\n");
            return sb.ToString().TrimEnd('\r', '\n');
        }
    }
}
