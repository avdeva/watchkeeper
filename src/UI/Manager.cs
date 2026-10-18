// The manager window's view of the system: the configuration file (read and written directly)
// and the service (status, logs and commands over the control pipe). A background thread
// polls the service once a second so the window never waits on it.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Watchkeeper.UI
{
    public class Manager : IDisposable
    {
        public readonly string DataDir;
        public readonly ConfigStore Config;
        public readonly ControlClient Client;
        readonly object lk = new object();
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        readonly List<LogEntry> logs = new List<LogEntry>();
        readonly Thread poller;
        EngineStatus status;
        string statusError = "Connecting to the service...";
        ServiceInfo service = new ServiceInfo();
        long lastLogId;
        long logGeneration;
        List<Adapter> adapters = new List<Adapter>();

        /// <summary>Asked for the starting configuration when there is none yet (null = studio starter).</summary>
        public static Func<AppConfig> FirstRun;

        /// <summary>Test hook: replaces the Windows service query.</summary>
        public Func<ServiceInfo> ServiceQuery = ServiceSetup.Query;

        public Manager(string dataDir) : this(dataDir, null) { }

        /// <summary>What was done to protect the settings folder when the manager opened (shown once).</summary>
        public List<string> SecurityNotes = new List<string>();

        public Manager(string dataDir, string pipeName)
        {
            DataDir = Path.GetFullPath(dataDir);
            // before anything is written: the folder is owned by Administrators and closed to everyone else
            SecureResult sec = DataDirSecurity.Secure(DataDir);
            SecurityNotes = sec.Actions;
            string path = Path.Combine(DataDir, "config.json");
            Config = new ConfigStore(path, null);
            Config.Protector = ProtectSecret;
            Client = new ControlClient(pipeName ?? ControlChannel.PipeName(DataDir));
            if (!File.Exists(path)) Create(path);
            else Config.Load(false);
            Config.Changed += delegate { NotifyReload(); };
            poller = new Thread(Poll);
            poller.Name = "manager-poll";
            poller.IsBackground = true;
            poller.Start();
        }

        /// <summary>Encrypt a typed-in secret when settings are saved: by the service with its own key (only it can read
        /// the result); when the service cannot be reached, with this computer's key (the service converts it later).</summary>
        public string ProtectSecret(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            try
            {
                if (Client != null)
                {
                    string v = Client.Call("secret.protect", "value", plain).Str("value", "");
                    if (Secrets.IsEncrypted(v)) return v;
                }
            }
            catch (Exception) { }
            return Secrets.Protect(plain);
        }

        void Create(string path)
        {
            AppConfig first = null;
            if (FirstRun != null)
            {
                try { first = FirstRun(); } catch (Exception) { first = null; }
            }
            if (first != null)
            {
                Util.WriteAtomic(path, Cfg.ToText(first));
                Config.Load(false);
                return;
            }
            Config.Load(true);
        }

        // ------------------------------------------------------------------ polling
        void Poll()
        {
            int tick = 0;
            while (!stop.WaitOne(tick == 0 ? 50 : 1000))
            {
                tick++;
                try
                {
                    JObject r = Client.Call(Request("status"), 1500);
                    EngineStatus s = (EngineStatus)Json.ConvertTo(typeof(EngineStatus), r.Get("status"));
                    lock (lk)
                    {
                        if (status == null || status.StartedAt != s.StartedAt)
                        {
                            // new service process: its log numbering starts again
                            logs.Clear();
                            lastLogId = 0;
                            logGeneration++;
                        }
                        status = s;
                        statusError = null;
                    }
                    FetchLogs();
                }
                catch (Exception ex)
                {
                    lock (lk) { status = null; statusError = ex.Message; }
                }
                if (tick % 3 == 1)
                {
                    try { ServiceInfo si = ServiceQuery(); lock (lk) service = si; } catch (Exception) { }
                }
                if (tick % 2 == 0)
                {
                    try { Config.CheckForChanges(); } catch (Exception) { }
                }
            }
        }

        void FetchLogs()
        {
            long after;
            lock (lk) after = lastLogId;
            JObject r = Client.Call(Request("logs", "after", (double)after, "limit", 3000.0), 1500);
            List<object> items = r.Get("entries") as List<object>;
            if (items == null || items.Count == 0) return;
            List<LogEntry> got = new List<LogEntry>();
            foreach (object o in items)
            {
                LogEntry e = Json.ConvertTo(typeof(LogEntry), o) as LogEntry;
                if (e != null) got.Add(e);
            }
            lock (lk)
            {
                foreach (LogEntry e in got)
                {
                    if (e.Id <= lastLogId) continue;
                    logs.Add(e);
                    lastLogId = e.Id;
                }
                if (logs.Count > 8000) logs.RemoveRange(0, logs.Count - 8000);
            }
        }

        static JObject Request(string cmd, params object[] kv)
        {
            JObject req = new JObject();
            req.Set("cmd", cmd);
            for (int i = 0; i + 1 < kv.Length; i += 2) req.Set((string)kv[i], kv[i + 1] is double || kv[i + 1] is string || kv[i + 1] is bool ? kv[i + 1] : Json.ToJson(kv[i + 1]));
            return req;
        }

        public EngineStatus Status { get { lock (lk) return status; } }
        public string StatusError { get { lock (lk) return statusError; } }
        public ServiceInfo Service { get { lock (lk) return service; } }
        public bool Online { get { lock (lk) return status != null; } }

        /// <summary>Log lines received from the service with an index above 'from' (position in the buffer).</summary>
        public List<LogEntry> Logs(out long generation)
        {
            lock (lk)
            {
                generation = logGeneration;
                return new List<LogEntry>(logs);
            }
        }

        public JobStatus Job(string key)
        {
            EngineStatus s = Status;
            if (s == null) return null;
            return s.Jobs.Find(delegate(JobStatus j) { return j.Key == key; });
        }

        public ProgramStatus Program(string id)
        {
            EngineStatus s = Status;
            if (s == null) return null;
            return s.Programs.Find(delegate(ProgramStatus p) { return p.Id == id; });
        }

        // ---------------------------------------------------------------- commands
        /// <summary>Synchronous call; throws ControlError.</summary>
        public JObject Call(string cmd, params object[] kv)
        {
            return Client.Call(Request(cmd, kv), 2000);
        }

        /// <summary>Run a service call in the background and hand the answer (or error) to the UI thread.</summary>
        public void Async(Control owner, Func<JObject> work, Action<JObject, Exception> done)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                JObject result = null;
                Exception error = null;
                try { result = work(); }
                catch (Exception ex) { error = ex; }
                try
                {
                    if (owner.IsHandleCreated && !owner.IsDisposed)
                        owner.BeginInvoke((MethodInvoker)delegate { done(result, error); });
                }
                catch (Exception) { }
            });
        }

        public void NotifyReload()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { Client.Call(Request("reload"), 1000); } catch (Exception) { }
            });
        }

        public List<Adapter> Adapters { get { lock (lk) return new List<Adapter>(adapters); } }

        public List<Adapter> DetectAdapters(bool refresh)
        {
            JObject r = Call("gpu.detect", "refresh", refresh);
            List<Adapter> list = Json.ConvertTo(typeof(List<Adapter>), r.Get("adapters")) as List<Adapter> ?? new List<Adapter>();
            lock (lk) adapters = list;
            return list;
        }

        public void Dispose()
        {
            stop.Set();
            poller.Join(2000);
        }
    }
}
