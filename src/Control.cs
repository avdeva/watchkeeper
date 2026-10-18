// Local control channel between the manager window and the service: a named pipe that only
// Administrators and Local System can open, never reachable over the network. One JSON
// request line, one JSON response line per connection.
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace Watchkeeper
{
    public class ControlError : Exception
    {
        public ControlError(string message) : base(message) { }
        public ControlError(string message, Exception inner) : base(message, inner) { }
    }

    public static class ControlChannel
    {
        public static string PipeName(string dataDir)
        {
            string key = Path.GetFullPath(dataDir).TrimEnd('\\', '/').ToLowerInvariant();
            return "Watchkeeper-" + Cfg.Hash(key);
        }
    }

    public class ControlServer
    {
        readonly string name;
        readonly Func<JObject, JObject> handler;
        readonly SourceLogger log;
        volatile bool stopping;
        Thread thread;

        public ControlServer(string name, Func<JObject, JObject> handler, SourceLogger log)
        {
            this.name = name;
            this.handler = handler;
            this.log = log;
        }

        public string Name { get { return name; } }

        public void Start()
        {
            stopping = false;
            thread = new Thread(Loop);
            thread.Name = "control";
            thread.IsBackground = true;
            thread.Start();
        }

        PipeSecurity Security()
        {
            PipeSecurity ps = new PipeSecurity();
            ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                                                PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
            ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                                                PipeAccessRights.FullControl, AccessControlType.Allow));
            // the account running the server (Local System for the service, an admin in --console mode)
            // needs to create the following pipe instances
            using (WindowsIdentity me = WindowsIdentity.GetCurrent())
                if (me.User != null)
                    ps.AddAccessRule(new PipeAccessRule(me.User, PipeAccessRights.FullControl, AccessControlType.Allow));
            ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
                                                PipeAccessRights.FullControl, AccessControlType.Deny));
            return ps;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public bool bInheritHandle; }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances, uint outSize, uint inSize,
                                                     uint timeout, ref SECURITY_ATTRIBUTES sa);

        const uint PipeAccessDuplex = 3, FileFlagFirstPipeInstance = 0x00080000, PipeRejectRemoteClients = 0x00000008;

        /// <summary>The first instance is created with FILE_FLAG_FIRST_PIPE_INSTANCE: if another program already holds the
        /// name, creation fails instead of joining it. Later instances are created while one of ours still exists, and
        /// only accounts allowed by the pipe's permissions (this one) may add instances.</summary>
        NamedPipeServerStream Create(bool first)
        {
            if (!Util.IsWindows)
                return new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                                                 PipeTransmissionMode.Byte, PipeOptions.None);
            byte[] sd = Security().GetSecurityDescriptorBinaryForm();
            GCHandle pin = GCHandle.Alloc(sd, GCHandleType.Pinned);
            try
            {
                SECURITY_ATTRIBUTES sa = new SECURITY_ATTRIBUTES();
                sa.nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES));
                sa.lpSecurityDescriptor = pin.AddrOfPinnedObject();
                SafePipeHandle h = CreateNamedPipe(@"\\.\pipe\" + name, PipeAccessDuplex | (first ? FileFlagFirstPipeInstance : 0),
                                                   PipeRejectRemoteClients, 255, 65536, 65536, 0, ref sa);
                if (h.IsInvalid)
                {
                    int err = Marshal.GetLastWin32Error();
                    h.Dispose();
                    if (first && (err == 5 || err == 231))
                        throw new ControlError("another program already uses the control channel name " + name + " (Windows error " + err +
                                               "); the manager cannot reach the service until that program is closed or the computer restarts");
                    throw new ControlError("cannot create the control channel: Windows error " + err);
                }
                return new NamedPipeServerStream(PipeDirection.InOut, false, false, h);
            }
            finally { pin.Free(); }
        }

        /// <summary>Raised when the control channel cannot be created because its name is taken.</summary>
        public Action<string> Squatted;

        void Loop()
        {
            int failures = 0;
            NamedPipeServerStream next = null;
            bool reported = false;
            while (!stopping)
            {
                NamedPipeServerStream server = next;
                next = null;
                try
                {
                    if (server == null) server = Create(true);
                    reported = false;
                    server.WaitForConnection();
                    failures = 0;
                }
                catch (Exception ex)
                {
                    if (server != null) { try { server.Dispose(); } catch (Exception) { } }
                    if (stopping) break;
                    if (++failures <= 3) log.Error("Control channel error: " + ex.Message);
                    if (ex is ControlError && ex.Message.StartsWith("another program") && !reported)
                    {
                        reported = true;
                        Action<string> h = Squatted;
                        if (h != null) { try { h(ex.Message); } catch (Exception) { } }
                    }
                    Thread.Sleep(Math.Min(10000, 500 * failures));
                    continue;
                }
                if (stopping) { server.Dispose(); break; }
                // the next instance exists before this one is handed over, so the name is never free
                try { next = Create(false); }
                catch (Exception ex) { log.Debug("Control channel: next instance not created yet: " + ex.Message); }
                NamedPipeServerStream conn = server;
                ThreadPool.QueueUserWorkItem(delegate { Serve(conn); });
            }
            if (next != null) { try { next.Dispose(); } catch (Exception) { } }
        }

        /// <summary>One line, at most this many characters (a request is a small JSON object).</summary>
        public const int MaxLine = 1 << 20;

        public static string ReadLine(Stream stream, int max)
        {
            StringBuilder sb = new StringBuilder();
            Decoder dec = new UTF8Encoding(false).GetDecoder();
            byte[] one = new byte[1];
            char[] ch = new char[2];
            while (true)
            {
                int n = stream.Read(one, 0, 1);
                if (n == 0) return sb.Length > 0 ? sb.ToString() : null;
                if (one[0] == (byte)'\n') return sb.ToString();
                int c = dec.GetChars(one, 0, 1, ch, 0);
                sb.Append(ch, 0, c);
                if (sb.Length > max) throw new IOException("message longer than " + max + " characters");
            }
        }

        void Serve(NamedPipeServerStream stream)
        {
            try
            {
                using (stream)
                {
                    string line = ReadLine(stream, MaxLine);
                    if (line == null) return;
                    JObject resp;
                    try
                    {
                        JObject req = Json.Parse(line) as JObject;
                        if (req == null) throw new FormatException("request is not a JSON object");
                        resp = handler(req);
                        if (resp == null) resp = new JObject();
                        if (!resp.Has("ok")) resp.Set("ok", true);
                    }
                    catch (Exception ex)
                    {
                        resp = new JObject();
                        resp.Set("ok", false);
                        resp.Set("error", ex.Message);
                    }
                    byte[] data = new UTF8Encoding(false).GetBytes(Json.Serialize(resp, false) + "\n");
                    stream.Write(data, 0, data.Length);
                    stream.Flush();
                    if (Util.IsWindows) { try { stream.WaitForPipeDrain(); } catch (Exception) { } }
                }
            }
            catch (Exception ex)
            {
                if (!stopping) log.Debug("Control connection ended: " + ex.Message);
            }
        }

        public void Stop()
        {
            stopping = true;
            try
            {
                using (NamedPipeClientStream c = new NamedPipeClientStream(".", name, PipeDirection.InOut))
                    c.Connect(500);
            }
            catch (Exception) { }
            if (thread != null) thread.Join(2000);
        }
    }

    public class ControlClient
    {
        readonly string name;

        public ControlClient(string name) { this.name = name; }

        /// <summary>Tests replace the check of who holds the other end.</summary>
        public Func<NamedPipeClientStream, string> ServerCheck;

        public string Name { get { return name; } }

        /// <summary>Send one request. Throws ControlError when the service cannot be reached or reports an error.</summary>
        public JObject Call(JObject request, int connectTimeoutMs)
        {
            string line;
            try
            {
                // Identification only: the server may check who is calling but cannot act as the caller
                using (NamedPipeClientStream c = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.None,
                                                                           TokenImpersonationLevel.Identification))
                {
                    c.Connect(connectTimeoutMs);
                    string who = ServerCheck != null ? ServerCheck(c) : PipeServerCheck.Verify(c);
                    if (who != null) throw new ControlError("the control channel is held by another program, not the Watchkeeper service (" + who + "); nothing was sent");
                    byte[] data = new UTF8Encoding(false).GetBytes(Json.Serialize(request, false) + "\n");
                    c.Write(data, 0, data.Length);
                    c.Flush();
                    line = ControlServer.ReadLine(c, 64 << 20);
                }
            }
            catch (TimeoutException) { throw new ControlError("the Watchkeeper service is not running (no answer)"); }
            catch (UnauthorizedAccessException) { throw new ControlError("access to the service was refused; run the manager as an administrator"); }
            catch (IOException ex) { throw new ControlError("lost the connection to the service: " + ex.Message, ex); }
            if (line == null) throw new ControlError("the service closed the connection without answering");
            JObject resp = Json.Parse(line) as JObject;
            if (resp == null) throw new ControlError("unreadable answer from the service");
            if (!resp.Bool("ok", false)) throw new ControlError(resp.Str("error", "the service reported an error"));
            return resp;
        }

        public JObject Call(string cmd, params object[] keyValues)
        {
            JObject req = new JObject();
            req.Set("cmd", cmd);
            for (int i = 0; i + 1 < keyValues.Length; i += 2) req.Set((string)keyValues[i], Json.ToJson(keyValues[i + 1]));
            return Call(req, 2000);
        }
    }

    /// <summary>Who holds the server end of the pipe: the Watchkeeper service (its process id as reported by Windows'
    /// service manager), this same process, or a Watchkeeper console engine of the same administrator.</summary>
    public static class PipeServerCheck
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr OpenSCManager(string machine, string db, uint access);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr OpenService(IntPtr scm, string name, uint access);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool QueryServiceStatusEx(IntPtr service, int level, byte[] buffer, int size, out int needed);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool CloseServiceHandle(IntPtr h);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returned);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);

        static uint ServicePid()
        {
            IntPtr scm = OpenSCManager(null, null, 0x0001 /* SC_MANAGER_CONNECT */);
            if (scm == IntPtr.Zero) return 0;
            try
            {
                IntPtr svc = OpenService(scm, KeeperService.Name, 0x0004 /* SERVICE_QUERY_STATUS */);
                if (svc == IntPtr.Zero) return 0;
                try
                {
                    byte[] buf = new byte[64];
                    int needed;
                    if (!QueryServiceStatusEx(svc, 0 /* SC_STATUS_PROCESS_INFO */, buf, buf.Length, out needed)) return 0;
                    return BitConverter.ToUInt32(buf, 28);          // SERVICE_STATUS_PROCESS.dwProcessId
                }
                finally { CloseServiceHandle(svc); }
            }
            finally { CloseServiceHandle(scm); }
        }

        static SecurityIdentifier ProcessUser(uint pid)
        {
            IntPtr p = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            if (p == IntPtr.Zero) return null;
            IntPtr token = IntPtr.Zero, buf = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(p, 0x0008 /* TOKEN_QUERY */, out token)) return null;
                int needed;
                GetTokenInformation(token, 1 /* TokenUser */, IntPtr.Zero, 0, out needed);
                if (needed <= 0) return null;
                buf = Marshal.AllocHGlobal(needed);
                if (!GetTokenInformation(token, 1, buf, needed, out needed)) return null;
                return new SecurityIdentifier(Marshal.ReadIntPtr(buf));
            }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                if (token != IntPtr.Zero) CloseHandle(token);
                CloseHandle(p);
            }
        }

        /// <summary>Null when the other end is trusted, otherwise a description of what holds it.</summary>
        public static string Verify(NamedPipeClientStream c)
        {
            if (!Util.IsWindows) return null;
            uint pid;
            if (!GetNamedPipeServerProcessId(c.SafePipeHandle, out pid)) return "its process cannot be identified: " + Native.LastError();
            using (Process me = Process.GetCurrentProcess()) if (pid == (uint)me.Id) return null;
            if (pid == ServicePid()) return null;
            // --console: an engine started by this same administrator from a Watchkeeper.exe
            string path = SystemProcessTable.ImagePath((int)pid);
            SecurityIdentifier user = ProcessUser(pid);
            using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                if (user != null && id.User != null && user.Equals(id.User) &&
                    string.Equals(Path.GetFileName(path), "Watchkeeper.exe", StringComparison.OrdinalIgnoreCase) && PathTrust.Check(path) == null)
                    return null;
            return "process " + pid + (path.Length > 0 ? " " + path : "") + (user != null ? " running as " + Trust.Name(user) : "");
        }
    }
}
