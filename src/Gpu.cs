// GPU adapter detection and ffmpeg encoder discovery.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Watchkeeper
{
    public class Adapter
    {
        public int Index;
        public string Name = "";
        public string Vendor = "other";    // intel | nvidia | amd | other
        public string Kind = "unknown";    // integrated | discrete | unknown
        public string Backend = "";        // intel_qsv | nvidia_nvenc | amd_amf | "" (none)
        public string Driver = "";

        public string Describe()
        {
            string k = Kind == "unknown" ? "" : " (" + Kind + ")";
            return Name + k;
        }
    }

    public class EncoderList
    {
        public bool Ok;
        public string Error = "";
        public List<string> Encoders = new List<string>();
    }

    public static class Gpu
    {
        public static string BackendLabel(string backend)
        {
            switch (backend)
            {
                case "intel_qsv": return "Intel Quick Sync (QSV)";
                case "nvidia_nvenc": return "NVIDIA NVENC";
                case "amd_amf": return "AMD AMF";
                case "cpu": return "CPU (software x264/x265)";
                case "auto": return "Automatic";
            }
            return backend;
        }

        public static string BackendForVendor(string vendor)
        {
            switch (vendor)
            {
                case "intel": return "intel_qsv";
                case "nvidia": return "nvidia_nvenc";
                case "amd": return "amd_amf";
            }
            return "";
        }

        static readonly Regex Ignored = new Regex(
            "basic display|remote display|virtual|parsec|citrix|vmware|hyper-v|displaylink|indirect",
            RegexOptions.IgnoreCase);

        /// <summary>Guess vendor and integrated/discrete from the adapter name.</summary>
        public static void Classify(string name, out string vendor, out string kind)
        {
            string n = name ?? "";
            if (Regex.IsMatch(n, @"nvidia|geforce|quadro|\brtx\b|\bgtx\b|tesla", RegexOptions.IgnoreCase))
            {
                vendor = "nvidia"; kind = "discrete"; return;
            }
            if (Regex.IsMatch(n, "intel", RegexOptions.IgnoreCase))
            {
                vendor = "intel";
                kind = (Regex.IsMatch(n, @"\barc\b", RegexOptions.IgnoreCase) && Regex.IsMatch(n, @"\b[AB]\d{3}"))
                    ? "discrete" : "integrated";
                return;
            }
            if (Regex.IsMatch(n, "amd|radeon|advanced micro", RegexOptions.IgnoreCase))
            {
                vendor = "amd";
                if (Regex.IsMatch(n, @"\bRX\b|\bPRO\b|\bW\d{4}\b|\bR9\b|\bFury\b|\bVII\b")) kind = "discrete";
                else if (Regex.IsMatch(n, @"radeon(\(tm\))? graphics|vega \d+ graphics|radeon 7\d\dM|radeon 8\d\dM", RegexOptions.IgnoreCase)) kind = "integrated";
                else kind = "unknown";
                return;
            }
            vendor = "other"; kind = "unknown";
        }

        /// <summary>Build the adapter list from (name, driver) pairs, dropping virtual/remote display devices.</summary>
        public static List<Adapter> FromNames(IEnumerable<KeyValuePair<string, string>> items)
        {
            List<Adapter> list = new List<Adapter>();
            foreach (KeyValuePair<string, string> kv in items)
            {
                string name = (kv.Key ?? "").Trim();
                if (name.Length == 0 || Ignored.IsMatch(name)) continue;
                Adapter a = new Adapter();
                Classify(name, out a.Vendor, out a.Kind);
                a.Index = list.Count;
                a.Name = name;
                a.Backend = BackendForVendor(a.Vendor);
                a.Driver = kv.Value ?? "";
                list.Add(a);
            }
            return list;
        }

        /// <summary>Parse PowerShell "Get-CimInstance Win32_VideoController | ConvertTo-Json".</summary>
        public static List<Adapter> ParseCimJson(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return new List<Adapter>();
            object parsed = Json.Parse(text);
            List<object> items = parsed as List<object>;
            if (items == null)
            {
                items = new List<object>();
                items.Add(parsed);
            }
            List<KeyValuePair<string, string>> pairs = new List<KeyValuePair<string, string>>();
            foreach (object o in items)
            {
                JObject j = o as JObject;
                if (j == null) continue;
                pairs.Add(new KeyValuePair<string, string>(j.Get("Name") as string, j.Get("DriverVersion") as string));
            }
            return FromNames(pairs);
        }

        static List<Adapter> DetectWmi()
        {
            List<KeyValuePair<string, string>> pairs = new List<KeyValuePair<string, string>>();
            using (System.Management.ManagementObjectSearcher searcher =
                new System.Management.ManagementObjectSearcher("SELECT Name, DriverVersion FROM Win32_VideoController"))
            using (System.Management.ManagementObjectCollection results = searcher.Get())
            {
                foreach (System.Management.ManagementBaseObject mo in results)
                {
                    pairs.Add(new KeyValuePair<string, string>(mo["Name"] as string, mo["DriverVersion"] as string));
                    mo.Dispose();
                }
            }
            return FromNames(pairs);
        }

        static List<Adapter> DetectPowerShell()
        {
            string[] args = {
                "-NoProfile", "-NonInteractive", "-Command",
                "Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion | ConvertTo-Json -Compress" };
            Util.CaptureResult r = Util.RunCapture(Util.PowerShellExe, args, 30000);
            if (r.TimedOut || r.ExitCode != 0) return new List<Adapter>();
            return ParseCimJson(r.Output);
        }

        /// <summary>List video adapters in the order Windows reports them. Empty when detection fails.</summary>
        public static List<Adapter> Detect()
        {
            if (!Util.IsWindows) return new List<Adapter>();
            try
            {
                List<Adapter> list = DetectWmi();
                if (list.Count > 0) return list;
            }
            catch (Exception) { }
            try { return DetectPowerShell(); }
            catch (Exception) { return new List<Adapter>(); }
        }

        static readonly string[] EncoderNames = {
            "h264_qsv", "hevc_qsv", "h264_nvenc", "hevc_nvenc", "h264_amf", "hevc_amf", "libx264", "libx265" };

        public static List<string> ParseEncoders(string text)
        {
            List<string> found = new List<string>();
            foreach (string line in (text ?? "").Split('\n'))
            {
                string[] parts = line.Split(new[] { ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[0].StartsWith("V") && Array.IndexOf(EncoderNames, parts[1]) >= 0
                    && !found.Contains(parts[1]))
                    found.Add(parts[1]);
            }
            found.Sort(StringComparer.Ordinal);
            return found;
        }

        public static EncoderList ListEncoders(string ffmpeg)
        {
            EncoderList res = new EncoderList();
            Util.CaptureResult r;
            try { r = Util.RunMedia(ffmpeg, new[] { "-hide_banner", "-encoders" }, 30000); }
            catch (Exception ex)
            {
                res.Error = "cannot run ffmpeg (" + ffmpeg + "): " + ex.Message;
                return res;
            }
            if (r.TimedOut) { res.Error = "ffmpeg timed out"; return res; }
            if (r.ExitCode != 0)
            {
                string e = (r.Error ?? "").Trim();
                if (e.Length > 300) e = e.Substring(e.Length - 300);
                res.Error = e.Length > 0 ? e : "exit code " + r.ExitCode;
                return res;
            }
            res.Ok = true;
            res.Encoders = ParseEncoders(r.Output);
            return res;
        }

        public static string FfmpegVersion(string ffmpeg)
        {
            try
            {
                Util.CaptureResult r = Util.RunMedia(ffmpeg, new[] { "-version" }, 15000);
                if (r.TimedOut || r.ExitCode != 0) return "";
                string o = (r.Output ?? "").Trim();
                int nl = o.IndexOf('\n');
                return (nl >= 0 ? o.Substring(0, nl) : o).Trim();
            }
            catch (Exception) { return ""; }
        }
    }

    /// <summary>Detection shells out to WMI, so cache it briefly.</summary>
    public class AdapterCache
    {
        readonly object lk = new object();
        readonly double ttl;
        readonly Func<List<Adapter>> detector;
        double at;
        List<Adapter> value = new List<Adapter>();

        public AdapterCache() : this(300.0, Gpu.Detect) { }

        public AdapterCache(double ttlSeconds, Func<List<Adapter>> detector)
        {
            ttl = ttlSeconds;
            this.detector = detector;
        }

        public List<Adapter> Get(bool refresh)
        {
            lock (lk)
            {
                if (refresh || value.Count == 0 || Util.Now() - at > ttl)
                {
                    List<Adapter> fresh;
                    try { fresh = detector() ?? new List<Adapter>(); }
                    catch (Exception) { fresh = new List<Adapter>(); }
                    value = fresh;
                    at = Util.Now();
                }
                return new List<Adapter>(value);
            }
        }

        public List<Adapter> Get() { return Get(false); }

        /// <summary>What is cached right now; never runs detection.</summary>
        public List<Adapter> Peek()
        {
            lock (lk) { return new List<Adapter>(value); }
        }
    }
}
