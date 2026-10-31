// ffmpeg and ffprobe: reading a video's facts, building the conversion command, running it, and the order in which
// encoders are tried (GPU with hardware decoding, GPU with software decoding, the same in the signed-in session, CPU).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Watchkeeper
{
    public class ProbeInfo
    {
        public string Codec = "";
        public int Width, Height;
        public double BitRate, Fps, Duration;
    }

    /// <summary>One way of running the conversion.</summary>
    public class Attempt
    {
        /// <summary>intel_qsv | nvidia_nvenc | amd_amf | cpu</summary>
        public string Encoder = "cpu";
        public bool HardwareDecode;
        /// <summary>Run ffmpeg in the signed-in user's session (a GPU is often not usable from the service's session).</summary>
        public bool Desktop;
        public int GpuIndex = -1;

        public string Key { get { return Encoder + "|" + HardwareDecode + "|" + Desktop; } }

        public string Label()
        {
            string l = Ffmpeg.EncoderLabel(Encoder);
            List<string> notes = new List<string>();
            if (Encoder != "cpu" && !HardwareDecode) notes.Add("software decoding");
            if (Desktop) notes.Add("in the signed-in session");
            return notes.Count == 0 ? l : l + " (" + string.Join(", ", notes.ToArray()) + ")";
        }
    }

    public class RunResult
    {
        public int ExitCode = -1;
        public bool TimedOut, Cancelled;
        public string StartError;
        public string Errors = "";
        public bool Ok { get { return StartError == null && !TimedOut && !Cancelled && ExitCode == 0; } }

        public string Describe()
        {
            if (StartError != null) return StartError;
            if (Cancelled) return "stopped";
            if (TimedOut) return "ffmpeg stopped making progress and was ended";
            string e = Errors.Length > 400 ? "..." + Errors.Substring(Errors.Length - 400) : Errors;
            return "ffmpeg failed (exit code " + ExitCode + ")" + (e.Length > 0 ? ": " + e : "");
        }
    }

    public static class Ffmpeg
    {
        public static string EncoderLabel(string e)
        {
            switch (e)
            {
                case "intel_qsv": return "Intel Quick Sync";
                case "nvidia_nvenc": return "NVIDIA NVENC";
                case "amd_amf": return "AMD AMF";
                case "cpu": return "CPU";
            }
            return "Automatic";
        }

        public static string ProbePath(GeneralSettings g)
        {
            if (g.FfprobePath.Length > 0) return Util.NormPath(g.FfprobePath);
            string ff = Util.NormPath(g.FfmpegPath);
            string dir = Path.GetDirectoryName(ff) ?? "";
            return Path.Combine(dir, Util.IsWindows ? "ffprobe.exe" : "ffprobe");
        }

        /// <summary>ffmpeg's name for the container of this file type; null when it is not a known recording container.</summary>
        public static string ContainerFor(string path)
        {
            switch ((Path.GetExtension(path ?? "") ?? "").ToLowerInvariant())
            {
                case ".mp4": case ".m4v": case ".mov": return "mov";
                case ".mkv": case ".webm": return "matroska";
                case ".mxf": return "mxf";
                case ".ts": case ".m2ts": case ".mts": return "mpegts";
                case ".avi": return "avi";
            }
            return null;
        }

        /// <summary>Read only this local file, as the container its extension names.</summary>
        static List<string> InputArgs(string path)
        {
            string fmt = ContainerFor(path);
            if (fmt == null) throw new InvalidOperationException("'" + Path.GetExtension(path) + "' files are not converted (.mp4, .mov, .m4v, .mkv, .webm, .mxf, .ts, .m2ts, .mts, .avi)");
            return new List<string> { "-protocol_whitelist", "file", "-f", fmt };
        }

        // ---------------------------------------------------------------- running
        public class Capture { public int ExitCode = -1; public string Out = "", Err = ""; public bool TimedOut; }

        public static Capture RunCapture(string file, IEnumerable<string> args, int timeoutMs)
        {
            Capture c = new Capture();
            StartSpec s = new StartSpec();
            s.File = file;
            s.Arguments = Util.JoinArgs(args);
            s.Hidden = true;
            s.Redirect = true;
            using (IRunning p = new SystemProcesses().Start(s))
            {
                StringBuilder o = new StringBuilder(), e = new StringBuilder();
                Thread to = new Thread(delegate() { try { o.Append(p.StdOut.ReadToEnd()); } catch (Exception) { } });
                Thread te = new Thread(delegate() { try { e.Append(p.StdErr.ReadToEnd()); } catch (Exception) { } });
                to.IsBackground = te.IsBackground = true;
                to.Start(); te.Start();
                if (!p.WaitForExit(timeoutMs)) { c.TimedOut = true; p.Kill(); p.WaitForExit(3000); }
                to.Join(3000); te.Join(3000);
                if (!c.TimedOut) c.ExitCode = p.ExitCode;
                c.Out = o.ToString();
                c.Err = e.ToString();
            }
            return c;
        }

        static double Num(object v)
        {
            double d = 0;
            if (v is double) d = (double)v;
            else if (v is string && !double.TryParse((string)v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return 0;
            return d > 0 ? d : 0;
        }

        public static ProbeInfo Probe(string ffprobe, string path)
        {
            List<string> args = new List<string> { "-v", "error", "-select_streams", "v:0", "-show_entries",
                "stream=codec_name,width,height,bit_rate,r_frame_rate,duration:format=duration,bit_rate", "-of", "json" };
            args.AddRange(InputArgs(path));
            args.Add("-i");
            args.Add(path);
            Capture r;
            try { r = RunCapture(ffprobe, args, 60000); }
            catch (Exception ex) { throw new InvalidOperationException("cannot run ffprobe (" + ffprobe + "): " + ex.Message); }
            if (r.TimedOut) throw new InvalidOperationException("ffprobe did not finish within a minute");
            if (r.ExitCode != 0) throw new InvalidOperationException("ffprobe cannot read the file: " + Util.OneLine(r.Err, 300));
            JObject root = null;
            try { root = Json.Parse(r.Out.Trim().Length == 0 ? "{}" : r.Out) as JObject; } catch (FormatException) { }
            List<object> streams = root == null ? null : root.Get("streams") as List<object>;
            if (streams == null || streams.Count == 0 || !(streams[0] is JObject)) throw new InvalidOperationException("no video in the file");
            JObject st = (JObject)streams[0];
            JObject fmt = root.Get("format") as JObject ?? new JObject();
            ProbeInfo i = new ProbeInfo();
            i.Codec = st.Str("codec_name", "");
            i.Width = (int)Num(st.Get("width"));
            i.Height = (int)Num(st.Get("height"));
            i.BitRate = Num(st.Get("bit_rate")) > 0 ? Num(st.Get("bit_rate")) : Num(fmt.Get("bit_rate"));
            string fr = st.Str("r_frame_rate", "");
            if (fr.IndexOf('/') > 0) { double a = Num(fr.Split('/')[0]), b = Num(fr.Split('/')[1]); i.Fps = b > 0 ? a / b : 0; }
            i.Duration = Num(st.Get("duration")) > 0 ? Num(st.Get("duration")) : Num(fmt.Get("duration"));
            return i;
        }

        /// <summary>Run ffmpeg with -progress on stdout. progress gets the seconds converted so far. Ended when it makes
        /// no progress for stallMs, or when cancelled() turns true.</summary>
        public static RunResult Run(IProcesses procs, string ffmpeg, List<string> args, bool desktop, string priority, int stallMs,
                                    Action<double> progress, Func<bool> cancelled)
        {
            RunResult r = new RunResult();
            StartSpec s = new StartSpec();
            s.File = ffmpeg;
            s.Arguments = Util.JoinArgs(args);
            s.Hidden = true;
            s.Redirect = true;
            s.Desktop = desktop;
            s.Priority = priority;
            if (desktop) s.Environment["WATCHKEEPER_SESSION"] = "desktop";
            IRunning p;
            try { p = procs.Start(s); }
            catch (Exception ex) { r.StartError = (desktop ? "cannot start ffmpeg in the signed-in session: " : "cannot start ffmpeg (" + ffmpeg + "): ") + ex.Message; return r; }
            using (p)
            {
                Queue<string> tail = new Queue<string>();
                long lastProgress = DateTime.UtcNow.Ticks;
                Thread te = new Thread(delegate()
                {
                    try
                    {
                        string line;
                        while ((line = p.StdErr.ReadLine()) != null)
                        {
                            if (line.Trim().Length == 0) continue;
                            lock (tail) { tail.Enqueue(line.Trim()); while (tail.Count > 30) tail.Dequeue(); }
                        }
                    }
                    catch (Exception) { }
                });
                Thread to = new Thread(delegate()
                {
                    try
                    {
                        string line;
                        while ((line = p.StdOut.ReadLine()) != null)
                        {
                            Interlocked.Exchange(ref lastProgress, DateTime.UtcNow.Ticks);
                            if (line.StartsWith("out_time_us=") || line.StartsWith("out_time_ms="))
                            {
                                long us;
                                if (long.TryParse(line.Substring(line.IndexOf('=') + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out us) && progress != null)
                                    progress(us / 1000000.0);
                            }
                        }
                    }
                    catch (Exception) { }
                });
                te.IsBackground = to.IsBackground = true;
                te.Start(); to.Start();
                while (!p.WaitForExit(300))
                {
                    if (cancelled != null && cancelled()) { r.Cancelled = true; break; }
                    if ((DateTime.UtcNow.Ticks - Interlocked.Read(ref lastProgress)) / 10000 > stallMs) { r.TimedOut = true; break; }
                }
                if (r.Cancelled || r.TimedOut) { p.Kill(); p.WaitForExit(5000); }
                to.Join(3000); te.Join(3000);
                if (!r.Cancelled && !r.TimedOut) r.ExitCode = p.ExitCode;
                lock (tail) r.Errors = string.Join(" | ", tail.ToArray());
            }
            return r;
        }

        // ---------------------------------------------------------------- encoders
        static readonly object cacheLock = new object();
        static string cachedFor;
        static List<string> cachedEncoders, cachedVendors;
        static DateTime cachedAt;

        /// <summary>The GPU makers found on this computer: intel, nvidia, amd.</summary>
        public static List<string> GpuVendors()
        {
            List<string> o = new List<string>();
            if (!Util.IsWindows) return o;
            try
            {
                using (System.Management.ManagementObjectSearcher searcher = new System.Management.ManagementObjectSearcher("SELECT Name FROM Win32_VideoController"))
                using (System.Management.ManagementObjectCollection results = searcher.Get())
                {
                    foreach (System.Management.ManagementBaseObject mo in results)
                    {
                        string n = Convert.ToString(mo["Name"]) ?? "";
                        mo.Dispose();
                        if (Regex.IsMatch(n, "nvidia|geforce|quadro|rtx", RegexOptions.IgnoreCase)) { if (!o.Contains("nvidia")) o.Add("nvidia"); }
                        else if (Regex.IsMatch(n, "intel", RegexOptions.IgnoreCase)) { if (!o.Contains("intel")) o.Add("intel"); }
                        else if (Regex.IsMatch(n, "amd|radeon", RegexOptions.IgnoreCase)) { if (!o.Contains("amd")) o.Add("amd"); }
                    }
                }
            }
            catch (Exception) { }
            return o;
        }

        public static List<string> Encoders(string ffmpeg)
        {
            List<string> found = new List<string>();
            Capture r = RunCapture(ffmpeg, new[] { "-hide_banner", "-encoders" }, 30000);
            foreach (string line in r.Out.Split('\n'))
            {
                string[] parts = line.Split(new[] { ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[0].StartsWith("V") && !found.Contains(parts[1])) found.Add(parts[1]);
            }
            return found;
        }

        static void Detect(string ffmpeg, out List<string> encoders, out List<string> vendors)
        {
            lock (cacheLock)
            {
                if (cachedFor != ffmpeg || (DateTime.UtcNow - cachedAt).TotalHours > 1)
                {
                    try { cachedEncoders = Encoders(ffmpeg); } catch (Exception) { cachedEncoders = new List<string>(); }
                    cachedVendors = GpuVendors();
                    cachedFor = ffmpeg;
                    cachedAt = DateTime.UtcNow;
                }
                encoders = cachedEncoders;
                vendors = cachedVendors;
            }
        }

        /// <summary>The order of attempts for the settings: the chosen GPU (or for Automatic each GPU present), with hardware
        /// decoding then without, the same again in the signed-in session, then the CPU.</summary>
        public static List<Attempt> Plan(VerticalSettings v, string ffmpeg)
        {
            List<string> gpus = new List<string>();
            if (v.Encoder == "auto")
            {
                List<string> encoders, vendors;
                Detect(ffmpeg, out encoders, out vendors);
                string codec = v.Codec == "hevc" ? "hevc" : "h264";
                foreach (string[] pair in new[] { new[] { "intel", "intel_qsv", "_qsv" }, new[] { "nvidia", "nvidia_nvenc", "_nvenc" }, new[] { "amd", "amd_amf", "_amf" } })
                    if (vendors.Contains(pair[0]) && encoders.Contains(codec + pair[2])) gpus.Add(pair[1]);
            }
            else if (v.Encoder != "cpu") gpus.Add(v.Encoder);
            List<Attempt> plan = new List<Attempt>();
            foreach (bool desktop in v.UserSessionFallback ? new[] { false, true } : new[] { false })
                foreach (string g in gpus)
                {
                    if (v.HardwareDecode) plan.Add(new Attempt { Encoder = g, HardwareDecode = true, Desktop = desktop, GpuIndex = v.GpuIndex });
                    plan.Add(new Attempt { Encoder = g, HardwareDecode = false, Desktop = desktop, GpuIndex = v.GpuIndex });
                }
            if (gpus.Count == 0 || v.CpuFallback || v.Encoder == "cpu") plan.Add(new Attempt { Encoder = "cpu" });
            return plan;
        }

        // ---------------------------------------------------------------- the command
        static string Enc(string encoder, string codec)
        {
            bool hevc = codec == "hevc";
            switch (encoder)
            {
                case "intel_qsv": return hevc ? "hevc_qsv" : "h264_qsv";
                case "nvidia_nvenc": return hevc ? "hevc_nvenc" : "h264_nvenc";
                case "amd_amf": return hevc ? "hevc_amf" : "h264_amf";
            }
            return hevc ? "libx265" : "libx264";
        }

        static string QsvDecoder(string codec)
        {
            switch (codec)
            {
                case "h264": return "h264_qsv";
                case "hevc": return "hevc_qsv";
                case "mpeg2video": return "mpeg2_qsv";
                case "vp9": return "vp9_qsv";
                case "av1": return "av1_qsv";
            }
            return null;
        }

        static int Even(double x) { return Math.Max(2, (int)Math.Round(x / 2.0) * 2); }

        /// <summary>The output size after turning the picture on its side (and scaling, if a size is set); null = keep.</summary>
        public static int[] Scale(VerticalSettings v, ProbeInfo info)
        {
            int w = v.Width, h = v.Height;
            if (w == 0 && h == 0) return null;
            int rw = info.Height, rh = info.Width;
            if (w == 0 || h == 0)
            {
                if (rw == 0 || rh == 0) throw new InvalidOperationException("the source size is unknown; set both width and height");
                if (w != 0) h = Even((double)w * rh / rw); else w = Even((double)h * rw / rh);
            }
            if (w == rw && h == rh) return null;
            return new[] { w, h };
        }

        public static int[] ExpectedSize(VerticalSettings v, ProbeInfo info)
        {
            int[] s = Scale(v, info);
            if (s != null) return s;
            return info.Width > 0 && info.Height > 0 ? new[] { info.Height, info.Width } : null;
        }

        public static List<string> Build(VerticalSettings v, ProbeInfo info, string src, string dst, Attempt a)
        {
            List<string> args = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-nostats", "-progress", "pipe:1" };
            string rot = v.Rotate == "clockwise" ? "1" : "2";            // transpose 1 = 90 degrees clockwise, 2 = counter-clockwise
            int[] size = Scale(v, info);
            List<string> chain = new List<string>();
            bool hw = a.HardwareDecode && a.Encoder != "cpu";
            if (a.Encoder == "intel_qsv")
            {
                string device = "qsv=hw";
                if (a.GpuIndex >= 0) device += ",child_device=" + a.GpuIndex.ToString(Util.Inv) + ",child_device_type=d3d11va";
                args.AddRange(new[] { "-init_hw_device", device, "-filter_hw_device", "hw" });
                string dec = hw ? QsvDecoder(info.Codec) : null;
                if (dec != null) args.AddRange(new[] { "-hwaccel", "qsv", "-c:v", dec });
                else { chain.Add("format=nv12"); chain.Add("hwupload=extra_hw_frames=64"); }
                chain.Add("vpp_qsv=transpose=" + rot);
                if (size != null) chain.Add("scale_qsv=w=" + size[0].ToString(Util.Inv) + ":h=" + size[1].ToString(Util.Inv));
            }
            else
            {
                if (hw && a.Encoder == "nvidia_nvenc")
                {
                    args.AddRange(new[] { "-hwaccel", "cuda" });
                    if (a.GpuIndex >= 0) args.AddRange(new[] { "-hwaccel_device", a.GpuIndex.ToString(Util.Inv) });
                }
                else if (hw && a.Encoder == "amd_amf") args.AddRange(new[] { "-hwaccel", "d3d11va" });
                chain.Add("transpose=" + rot);
                if (size != null) chain.Add("scale=" + size[0].ToString(Util.Inv) + ":" + size[1].ToString(Util.Inv));
            }
            args.AddRange(Util.SplitArgs(v.ExtraInputArgs));
            args.AddRange(InputArgs(src));
            args.AddRange(new[] { "-i", src, "-vf", string.Join(",", chain.ToArray()), "-c:v", Enc(a.Encoder, v.Codec) });

            if (v.Bitrate == "quality")
            {
                string q = v.Quality.ToString(Util.Inv);
                if (a.Encoder == "intel_qsv") args.AddRange(new[] { "-global_quality", q });
                else if (a.Encoder == "nvidia_nvenc") args.AddRange(new[] { "-rc", "vbr", "-cq", q, "-b:v", "0" });
                else if (a.Encoder == "amd_amf") args.AddRange(new[] { "-rc", "cqp", "-qp_i", q, "-qp_p", q });
                else args.AddRange(new[] { "-crf", q });
            }
            else
            {
                double bps = v.Bitrate == "fixed" ? v.FixedMbps * 1e6 : info.BitRate > 0 ? info.BitRate : v.FallbackMbps * 1e6;
                decimal mbps = Math.Max(0.1m, Math.Round((decimal)(bps / 1e6), 2));
                string text = Util.Num((double)mbps, 2) + "M";
                long ibps = (long)((double)mbps * 1e6);
                args.AddRange(new[] { "-b:v", text, "-maxrate", text, "-bufsize", Math.Min(ibps * 2, 2000000000L).ToString(Util.Inv) });
            }
            if (a.Encoder == "intel_qsv" && v.Codec == "h264") args.AddRange(new[] { "-look_ahead", "0" });
            if (a.Encoder == "nvidia_nvenc" && a.GpuIndex >= 0) args.AddRange(new[] { "-gpu", a.GpuIndex.ToString(Util.Inv) });
            args.AddRange(new[] { "-profile:v", v.Codec == "h264" ? "high" : "main" });
            if (v.Preset != "balanced")
            {
                bool fast = v.Preset == "fast";
                if (a.Encoder == "nvidia_nvenc") args.AddRange(new[] { "-preset", fast ? "p2" : "p6" });
                else if (a.Encoder == "amd_amf") args.AddRange(new[] { "-quality", fast ? "speed" : "quality" });
                else args.AddRange(new[] { "-preset", fast ? "veryfast" : "slow" });
            }
            if (a.Encoder == "cpu") args.AddRange(new[] { "-pix_fmt", "yuv420p" });
            if (v.Fps > 0) args.AddRange(new[] { "-r", Util.Num(v.Fps, 3) });
            if (v.Audio == "copy") args.AddRange(new[] { "-c:a", "copy" });
            else if (v.Audio == "aac") args.AddRange(new[] { "-c:a", "aac", "-b:a", v.AudioKbps.ToString(Util.Inv) + "k" });
            else args.Add("-an");
            args.AddRange(Util.SplitArgs(v.ExtraOutputArgs));
            args.AddRange(new[] { "-f", "mp4", dst });
            return args;
        }
    }
}
