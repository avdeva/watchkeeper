// Windows only: start a program inside the logged-on console session from the service.
// A Windows service runs in session 0, which has no visible desktop; VVD, vMix and any other
// desktop program must run in the user's session. With auto-logon that user is always signed in.
//
// Steps (the documented pattern for services): WTSGetActiveConsoleSessionId -> WTSQueryUserToken
// (needs Local System) -> DuplicateTokenEx (primary token) -> CreateEnvironmentBlock ->
// CreateProcessAsUser on winsta0\default.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Watchkeeper
{
    public static class WinSession
    {
        const uint InvalidSession = 0xFFFFFFFF;
        const uint MaximumAllowed = 0x02000000;
        const int SecurityImpersonation = 2;
        const int TokenPrimary = 1;
        const uint CreateUnicodeEnvironment = 0x00000400;
        const uint CreateNoWindow = 0x08000000;
        const uint CreateNewConsole = 0x00000010;
        const int StartfUseShowWindow = 0x00000001;
        const short SwHide = 0;
        const short SwShowNormal = 1;
        const uint Infinite = 0xFFFFFFFF;
        const int WtsUserName = 5;
        const int WtsDomainName = 7;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [DllImport("kernel32.dll")]
        static extern uint WTSGetActiveConsoleSessionId();

        [DllImport("wtsapi32.dll", SetLastError = true)]
        static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

        [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "WTSQuerySessionInformationW")]
        static extern bool WTSQuerySessionInformation(IntPtr server, uint sessionId, int infoClass, out IntPtr buffer, out int bytes);

        [DllImport("wtsapi32.dll")]
        static extern void WTSFreeMemory(IntPtr memory);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WTS_SESSION_INFO
        {
            public int SessionId;
            public IntPtr WinStationName;
            public int State;                  // 0 active, 4 disconnected
        }

        [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "WTSEnumerateSessionsW")]
        static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr sessions, out int count);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool GetTokenInformation(IntPtr token, int infoClass, out IntPtr info, int length, out int returned);

        const int TokenLinkedToken = 19;
        const int ErrorElevationRequired = 740;

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool DuplicateTokenEx(IntPtr existing, uint access, IntPtr attributes, int level, int type, out IntPtr newToken);

        [DllImport("userenv.dll", SetLastError = true)]
        static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);

        [DllImport("userenv.dll", SetLastError = true)]
        static extern bool DestroyEnvironmentBlock(IntPtr env);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateProcessAsUserW")]
        static extern bool CreateProcessAsUser(IntPtr token, string application, StringBuilder commandLine,
            IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags,
            IntPtr environment, string currentDirectory, ref STARTUPINFO startup, out PROCESS_INFORMATION info);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            public bool bInheritHandle;
        }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateProcessAsUserW")]
        static extern bool CreateProcessAsUserEx(IntPtr token, string application, StringBuilder commandLine,
            IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags,
            IntPtr environment, string currentDirectory, ref STARTUPINFOEX startup, out PROCESS_INFORMATION info);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CreatePipe(out IntPtr read, out IntPtr write, ref SECURITY_ATTRIBUTES sa, int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetHandleInformation(IntPtr handle, int mask, int flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool UpdateProcThreadAttribute(IntPtr list, int flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);

        [DllImport("kernel32.dll")]
        static extern void DeleteProcThreadAttributeList(IntPtr list);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentProcess();

        [StructLayout(LayoutKind.Sequential)]
        struct SID_AND_ATTRIBUTES { public IntPtr Sid; public int Attributes; }

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool CreateRestrictedToken(IntPtr existing, int flags, int disableCount, SID_AND_ATTRIBUTES[] disable,
            int deleteCount, IntPtr deletePrivileges, int restrictCount, IntPtr restrict, out IntPtr newToken);

        const uint ExtendedStartupInfoPresent = 0x00080000;
        const int StartfUseStdHandles = 0x00000100;
        const int HandleFlagInherit = 1;
        static readonly IntPtr ProcThreadAttributeHandleList = new IntPtr(0x00020002);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern uint WaitForSingleObject(IntPtr handle, uint ms);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetExitCodeProcess(IntPtr process, out uint code);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool TerminateProcess(IntPtr process, uint code);

        static string LastError()
        {
            int code = Marshal.GetLastWin32Error();
            return new Win32Exception(code).Message + " (error " + code + ")";
        }

        static string QueryString(uint session, int infoClass)
        {
            IntPtr buf;
            int bytes;
            if (!WTSQuerySessionInformation(IntPtr.Zero, session, infoClass, out buf, out bytes)) return "";
            try { return Marshal.PtrToStringUni(buf) ?? ""; }
            finally { WTSFreeMemory(buf); }
        }

        static int CurrentSession()
        {
            using (Process p = Process.GetCurrentProcess()) return p.SessionId;
        }

        static string User(uint session)
        {
            string u = QueryString(session, WtsUserName);
            if (u.Length == 0) return "";
            string d = QueryString(session, WtsDomainName);
            return d.Length > 0 ? d + "\\" + u : u;
        }

        static string AutoLogonUser()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"))
                    return k == null ? "" : Convert.ToString(k.GetValue("DefaultUserName", "")) ?? "";
            }
            catch (Exception) { return ""; }
        }

        /// <summary>The session desktop programs go to, or -1: the user signed in at the console; otherwise the
        /// auto-logon user's session even when it moved to Remote Desktop or is disconnected (programs keep running there).</summary>
        public static int ConsoleUserSession(out string user)
        {
            user = "";
            try
            {
                uint console = WTSGetActiveConsoleSessionId();
                if (console != InvalidSession)
                {
                    string u = User(console);
                    if (u.Length > 0) { user = u; return (int)console; }
                }
                IntPtr buf;
                int count;
                if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out buf, out count)) return -1;
                List<int> active = new List<int>(), disconnected = new List<int>();
                try
                {
                    int size = Marshal.SizeOf(typeof(WTS_SESSION_INFO));
                    for (int i = 0; i < count; i++)
                    {
                        WTS_SESSION_INFO si = (WTS_SESSION_INFO)Marshal.PtrToStructure(new IntPtr(buf.ToInt64() + i * size), typeof(WTS_SESSION_INFO));
                        if (si.SessionId == 0) continue;
                        if (si.State == 0) active.Add(si.SessionId);
                        else if (si.State == 4) disconnected.Add(si.SessionId);
                    }
                }
                finally { WTSFreeMemory(buf); }
                string auto = AutoLogonUser();
                if (auto.Length > 0)
                {
                    foreach (List<int> group in new[] { active, disconnected })
                        foreach (int id in group)
                        {
                            string u = User((uint)id);
                            string bare = u.Contains("\\") ? u.Substring(u.LastIndexOf('\\') + 1) : u;
                            if (string.Equals(bare, auto, StringComparison.OrdinalIgnoreCase)) { user = u; return id; }
                        }
                }
                // Never any other session: a user who connects by Remote Desktop while nobody is at the console must not
                // receive the programs (or their elevated rights).
                return -1;
            }
            catch (Exception) { return -1; }
        }

        static IntPtr UserToken(uint session)
        {
            IntPtr token;
            if (!WTSQueryUserToken(session, out token))
                throw new InvalidOperationException("cannot get the signed-in user's token for session " + session + ": " + LastError() +
                    ". The service must run as Local System and a user must be signed in at the console (auto-logon).");
            try
            {
                IntPtr primary;
                if (!DuplicateTokenEx(token, MaximumAllowed, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out primary))
                    throw new InvalidOperationException("DuplicateTokenEx failed: " + LastError());
                return primary;
            }
            finally { CloseHandle(token); }
        }

        static Dictionary<string, string> ReadEnvironment(IntPtr block)
        {
            Dictionary<string, string> env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            IntPtr p = block;
            while (true)
            {
                string entry = Marshal.PtrToStringUni(p);
                if (string.IsNullOrEmpty(entry)) break;
                int eq = entry.IndexOf('=', 1);         // entries such as "=C:=C:\" start with '='
                if (eq > 0) env[entry.Substring(0, eq)] = entry.Substring(eq + 1);
                p = new IntPtr(p.ToInt64() + (entry.Length + 1) * 2);
            }
            return env;
        }

        static uint PriorityFlag(string priority)
        {
            switch (priority)
            {
                case "idle": return 0x00000040;
                case "below_normal": return 0x00004000;
                case "above_normal": return 0x00008000;
                case "high": return 0x00000080;
            }
            return 0x00000020;
        }

        public static IRunning Start(StartRequest r)
        {
            string who;
            int found = ConsoleUserSession(out who);
            if (found < 0) throw new InvalidOperationException("nobody is signed in (auto-logon not done yet?)");
            uint session = (uint)found;

            if (CurrentSession() == (int)session)
            {
                // Not running as a service (for example --console from a signed-in desktop): start directly.
                ProcessStartInfo psi = new ProcessStartInfo(r.File, r.Arguments ?? "");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = r.Hidden;
                if (r.Redirect)
                {
                    psi.RedirectStandardOutput = true;
                    psi.RedirectStandardError = true;
                    psi.StandardOutputEncoding = Encoding.UTF8;
                    psi.StandardErrorEncoding = Encoding.UTF8;
                }
                if (!string.IsNullOrEmpty(r.WorkingDir)) psi.WorkingDirectory = r.WorkingDir;
                foreach (KeyValuePair<string, string> kv in r.Environment) psi.EnvironmentVariables[kv.Key] = kv.Value;
                Process p = Process.Start(psi);
                if (r.Priority != "normal") { try { p.PriorityClass = Procs.ToClass(r.Priority); } catch (Exception) { } }
                return new LocalRunning(p);
            }

            IntPtr token = IntPtr.Zero, block = IntPtr.Zero;
            try
            {
                token = UserToken(session);
                if (!CreateEnvironmentBlock(out block, token, false))
                    throw new InvalidOperationException("CreateEnvironmentBlock failed: " + LastError());
                int err;
                IRunning run = Launch(token, r, "winsta0\\default", ReadEnvironment(block), out err);
                if (run != null) return run;
                if (err != ErrorElevationRequired)
                    throw new InvalidOperationException("cannot start " + r.File + " in the user session: " + new Win32Exception(err).Message + " (error " + err + ")");
                // The program asks for administrator rights; with UAC the session token is the limited one.
                if (!r.AllowElevation)
                    throw new InvalidOperationException(r.File + " asks for administrator rights. It is not started with the signed-in user's full rights " +
                                                        "unless 'Allow elevated start' is ticked for this entry (that skips the Windows consent prompt).");
                IntPtr linked = IntPtr.Zero;
                int got;
                if (!GetTokenInformation(token, TokenLinkedToken, out linked, IntPtr.Size, out got))
                    throw new InvalidOperationException("cannot start " + r.File + " elevated: the signed-in user has no administrator rights (" + LastError() + ")");
                try
                {
                    run = Launch(linked, r, "winsta0\\default", ReadEnvironment(block), out err);
                    if (run == null) throw new InvalidOperationException("cannot start " + r.File + " elevated: " + new Win32Exception(err).Message + " (error " + err + ")");
                    return run;
                }
                finally { CloseHandle(linked); }
            }
            finally
            {
                if (block != IntPtr.Zero) DestroyEnvironmentBlock(block);
                if (token != IntPtr.Zero) CloseHandle(token);
            }
        }

        /// <summary>A process in the service's own session with a restricted copy of its token: every privilege removed and
        /// the Administrators group usable only for denying access. Used for ffmpeg and ffprobe, which read untrusted files.</summary>
        public static IRunning StartRestricted(StartRequest r)
        {
            IntPtr own = IntPtr.Zero, restricted = IntPtr.Zero;
            IntPtr adminSid = IntPtr.Zero;
            try
            {
                // TOKEN_DUPLICATE | TOKEN_ASSIGN_PRIMARY | TOKEN_QUERY
                if (!OpenProcessToken(GetCurrentProcess(), 0x0002 | 0x0001 | 0x0008, out own))
                    throw new InvalidOperationException("OpenProcessToken failed: " + LastError());
                System.Security.Principal.SecurityIdentifier admins = Trust.Administrators;
                byte[] sid = new byte[admins.BinaryLength];
                admins.GetBinaryForm(sid, 0);
                adminSid = Marshal.AllocHGlobal(sid.Length);
                Marshal.Copy(sid, 0, adminSid, sid.Length);
                SID_AND_ATTRIBUTES[] disable = { new SID_AND_ATTRIBUTES { Sid = adminSid, Attributes = 0 } };
                if (!CreateRestrictedToken(own, 0x1 /* DISABLE_MAX_PRIVILEGE */, 1, disable, 0, IntPtr.Zero, 0, IntPtr.Zero, out restricted))
                    throw new InvalidOperationException("CreateRestrictedToken failed: " + LastError());
                Dictionary<string, string> env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables()) env[(string)e.Key] = (string)e.Value;
                int err;
                IRunning run = Launch(restricted, r, null, env, out err);
                if (run == null) throw new InvalidOperationException("cannot start " + r.File + ": " + new Win32Exception(err).Message + " (error " + err + ")");
                return run;
            }
            finally
            {
                if (adminSid != IntPtr.Zero) Marshal.FreeHGlobal(adminSid);
                if (restricted != IntPtr.Zero) CloseHandle(restricted);
                if (own != IntPtr.Zero) CloseHandle(own);
            }
        }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "LogonUserW")]
        static extern bool LogonUser(string user, string domain, string password, int logonType, int provider, out IntPtr token);

        /// <summary>A process as the built-in LOCAL SERVICE account (not Local System), with every privilege removed (also
        /// SeImpersonatePrivilege, which that account would otherwise hold), in a window station of its own. Used for ffmpeg
        /// and ffprobe when the service runs them: a flaw in a decoder then gives the attacker an account with almost no
        /// rights instead of Local System. Needs the service's own Local System rights to log the account on.</summary>
        public static IRunning StartAsLocalService(StartRequest r)
        {
            IntPtr ls = IntPtr.Zero, restricted = IntPtr.Zero;
            try
            {
                // LOGON32_LOGON_SERVICE, LOGON32_PROVIDER_DEFAULT; no password for the built-in service accounts
                if (!LogonUser("LocalService", "NT AUTHORITY", null, 5, 0, out ls) && !LogonUser("LOCAL SERVICE", "NT AUTHORITY", null, 5, 0, out ls))
                    throw new InvalidOperationException("cannot log on LOCAL SERVICE: " + LastError());
                if (!CreateRestrictedToken(ls, 0x1 /* DISABLE_MAX_PRIVILEGE */, 0, null, 0, IntPtr.Zero, 0, IntPtr.Zero, out restricted))
                    throw new InvalidOperationException("CreateRestrictedToken failed: " + LastError());
                Dictionary<string, string> env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables()) env[(string)e.Key] = (string)e.Value;
                string profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"ServiceProfiles\LocalService");
                string temp = Path.Combine(profile, @"AppData\Local\Temp");
                env["USERPROFILE"] = profile;
                env["APPDATA"] = Path.Combine(profile, @"AppData\Roaming");
                env["LOCALAPPDATA"] = Path.Combine(profile, @"AppData\Local");
                env["TEMP"] = temp;
                env["TMP"] = temp;
                env["USERNAME"] = "LOCAL SERVICE";
                int err;
                // "" = the window station of this logon session, not the service's own
                IRunning run = Launch(restricted, r, "", env, out err);
                if (run == null) throw new InvalidOperationException("cannot start " + r.File + " as LOCAL SERVICE: " + new Win32Exception(err).Message + " (error " + err + ")");
                return run;
            }
            finally
            {
                if (restricted != IntPtr.Zero) CloseHandle(restricted);
                if (ls != IntPtr.Zero) CloseHandle(ls);
            }
        }

        static IntPtr InheritablePipe(out IntPtr parentEnd, bool childReads)
        {
            SECURITY_ATTRIBUTES sa = new SECURITY_ATTRIBUTES();
            sa.nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES));
            sa.bInheritHandle = true;
            IntPtr read, write;
            if (!CreatePipe(out read, out write, ref sa, 0)) throw new InvalidOperationException("CreatePipe failed: " + LastError());
            parentEnd = childReads ? write : read;
            SetHandleInformation(parentEnd, HandleFlagInherit, 0);      // the service's end is never inherited
            return childReads ? read : write;
        }

        /// <summary>CreateProcessAsUser. With Redirect, the child gets pipes for its standard handles and inherits those
        /// three handles only (an explicit handle list), nothing else of the service. Returns null and the error code when
        /// Windows refuses to start it.</summary>
        static IRunning Launch(IntPtr token, StartRequest r, string desktop, Dictionary<string, string> baseEnv, out int error)
        {
            error = 0;
            Dictionary<string, string> env = new Dictionary<string, string>(baseEnv, StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> kv in r.Environment) env[kv.Key] = kv.Value;
            StringBuilder eb = new StringBuilder();
            foreach (KeyValuePair<string, string> kv in env) eb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
            eb.Append('\0');
            IntPtr envPtr = Marshal.StringToHGlobalUni(eb.ToString());
            IntPtr outRead = IntPtr.Zero, outWrite = IntPtr.Zero, errRead = IntPtr.Zero, errWrite = IntPtr.Zero, inRead = IntPtr.Zero, inWrite = IntPtr.Zero;
            IntPtr attrList = IntPtr.Zero, handles = IntPtr.Zero;
            bool attrInit = false;
            try
            {
                STARTUPINFOEX si = new STARTUPINFOEX();
                si.StartupInfo.cb = Marshal.SizeOf(typeof(STARTUPINFOEX));
                si.StartupInfo.lpDesktop = desktop;
                si.StartupInfo.dwFlags = StartfUseShowWindow;
                si.StartupInfo.wShowWindow = r.Hidden ? SwHide : SwShowNormal;
                bool inherit = false;
                uint flags = CreateUnicodeEnvironment | PriorityFlag(r.Priority) | (r.Hidden ? CreateNoWindow : CreateNewConsole);
                if (r.Redirect)
                {
                    outWrite = InheritablePipe(out outRead, false);
                    errWrite = InheritablePipe(out errRead, false);
                    inRead = InheritablePipe(out inWrite, true);
                    si.StartupInfo.dwFlags |= StartfUseStdHandles;
                    si.StartupInfo.hStdOutput = outWrite;
                    si.StartupInfo.hStdError = errWrite;
                    si.StartupInfo.hStdInput = inRead;
                    IntPtr size = IntPtr.Zero;
                    InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                    attrList = Marshal.AllocHGlobal(size);
                    if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref size)) throw new InvalidOperationException("InitializeProcThreadAttributeList failed: " + LastError());
                    attrInit = true;
                    handles = Marshal.AllocHGlobal(IntPtr.Size * 3);
                    Marshal.WriteIntPtr(handles, 0, outWrite);
                    Marshal.WriteIntPtr(handles, IntPtr.Size, errWrite);
                    Marshal.WriteIntPtr(handles, IntPtr.Size * 2, inRead);
                    if (!UpdateProcThreadAttribute(attrList, 0, ProcThreadAttributeHandleList, handles, new IntPtr(IntPtr.Size * 3), IntPtr.Zero, IntPtr.Zero))
                        throw new InvalidOperationException("UpdateProcThreadAttribute failed: " + LastError());
                    si.lpAttributeList = attrList;
                    flags |= ExtendedStartupInfoPresent;
                    inherit = true;
                }
                StringBuilder cmd = new StringBuilder(Util.QuoteArg(r.File) + (string.IsNullOrEmpty(r.Arguments) ? "" : " " + r.Arguments), 32768);
                string dir = r.WorkingDir;
                if (string.IsNullOrEmpty(dir))
                {
                    try { if (Path.IsPathRooted(r.File)) dir = Path.GetDirectoryName(r.File); } catch (Exception) { }
                }
                if (string.IsNullOrEmpty(dir)) dir = null;
                PROCESS_INFORMATION pi;
                if (!CreateProcessAsUserEx(token, null, cmd, IntPtr.Zero, IntPtr.Zero, inherit, flags, envPtr, dir, ref si, out pi))
                {
                    error = Marshal.GetLastWin32Error();
                    return null;
                }
                CloseHandle(pi.hThread);
                SessionRunning run = new SessionRunning(pi.hProcess, pi.dwProcessId);
                if (r.Redirect)
                {
                    run.Attach(outRead, errRead);
                    outRead = errRead = IntPtr.Zero;      // owned by the readers now
                }
                return run;
            }
            finally
            {
                foreach (IntPtr h in new[] { outWrite, errWrite, inRead, inWrite, outRead, errRead })
                    if (h != IntPtr.Zero) CloseHandle(h);
                if (attrInit) DeleteProcThreadAttributeList(attrList);
                if (attrList != IntPtr.Zero) Marshal.FreeHGlobal(attrList);
                if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
                Marshal.FreeHGlobal(envPtr);
            }
        }

        /// <summary>A process started with CreateProcessAsUser; output through pipes when redirected.</summary>
        class SessionRunning : IRunning
        {
            IntPtr handle;
            readonly int pid;
            TextReader stdout, stderr;

            public SessionRunning(IntPtr handle, int pid) { this.handle = handle; this.pid = pid; }

            public void Attach(IntPtr outRead, IntPtr errRead)
            {
                stdout = new StreamReader(new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(outRead, true), FileAccess.Read, 4096), Encoding.UTF8);
                stderr = new StreamReader(new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(errRead, true), FileAccess.Read, 4096), Encoding.UTF8);
            }

            public int Pid { get { return pid; } }
            public TextReader StdOut { get { return stdout; } }
            public TextReader StdErr { get { return stderr; } }

            public bool WaitForExit(int ms)
            {
                if (handle == IntPtr.Zero) return true;
                return WaitForSingleObject(handle, ms < 0 ? Infinite : (uint)ms) == 0;
            }

            public int ExitCode
            {
                get
                {
                    uint code;
                    if (handle == IntPtr.Zero || !GetExitCodeProcess(handle, out code)) return -1;
                    return unchecked((int)code);
                }
            }

            public void Kill()
            {
                if (handle != IntPtr.Zero) TerminateProcess(handle, 1);
            }

            public void Dispose()
            {
                if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; }
                if (stdout != null) { stdout.Dispose(); stdout = null; }
                if (stderr != null) { stderr.Dispose(); stderr = null; }
            }
        }
    }
}
