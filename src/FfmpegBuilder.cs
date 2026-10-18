// Builds the ffmpeg command line for vertical conversion.
// Default settings with an Intel QSV adapter give a fixed reference command (verified by a test).
using System;
using System.Collections.Generic;

namespace Watchkeeper
{
    public class BuildError : Exception
    {
        public BuildError(string message) : base(message) { }
    }

    /// <summary>Concrete GPU plan derived from the user's choice and the detected adapters.</summary>
    public class Resolved
    {
        public string Backend = "cpu";
        public Adapter Adapter;
        public int QsvChildIndex = -1;
        public int NvencGpu = -1;
        /// <summary>Set for the retry after a hardware-decode failure: decode in software, encode on the GPU.</summary>
        public bool NoHwDecode;
        /// <summary>Run ffmpeg in the signed-in user's session instead of the service session.</summary>
        public bool InUserSession;

        public Resolved Copy() { return (Resolved)MemberwiseClone(); }
    }

    public class BuildResult
    {
        public List<string> Args = new List<string>();
        public List<string> Notes = new List<string>();
        public string Text { get { return Util.JoinArgs(Args); } }
    }

    public static class FfmpegBuilder
    {
        public static readonly ProbeInfo SampleInfo = new ProbeInfo
        {
            Codec = "h264", Width = 1920, Height = 1080, BitRate = 8000000, Fps = 30, Duration = 60
        };

        static string Encoder(string backend, string codec)
        {
            switch (backend)
            {
                case "intel_qsv": return codec == "hevc" ? "hevc_qsv" : "h264_qsv";
                case "nvidia_nvenc": return codec == "hevc" ? "hevc_nvenc" : "h264_nvenc";
                case "amd_amf": return codec == "hevc" ? "hevc_amf" : "h264_amf";
            }
            return codec == "hevc" ? "libx265" : "libx264";
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

        // transpose=2 is 90 degrees counter-clockwise, 1 is clockwise (software transpose and vpp_qsv).
        static int Transpose(string rotation) { return rotation == "clock" ? 1 : 2; }

        public static Resolved ResolveGpu(GpuCfg gpu, List<Adapter> adapters)
        {
            string backend = gpu.Backend;
            if (backend == "auto")
            {
                backend = "cpu";
                foreach (string pref in new[] { "intel_qsv", "nvidia_nvenc", "amd_amf" })
                {
                    if (adapters.Exists(delegate(Adapter a) { return a.Backend == pref; })) { backend = pref; break; }
                }
            }
            Resolved r = new Resolved();
            r.Backend = backend;
            if (backend == "cpu") return r;

            string wanted = backend;
            List<Adapter> same = adapters.FindAll(delegate(Adapter a) { return a.Backend == wanted; });
            Adapter chosen = null;
            if (!string.IsNullOrEmpty(gpu.AdapterName))
                chosen = same.Find(delegate(Adapter a) { return a.Name == gpu.AdapterName; });
            if (chosen == null && same.Count == 1) chosen = same[0];
            r.Adapter = chosen;

            if (gpu.AdapterIndex >= 0)
            {
                r.QsvChildIndex = gpu.AdapterIndex;
                r.NvencGpu = gpu.AdapterIndex;
            }
            else if (chosen != null && same.Count > 1)
            {
                r.QsvChildIndex = chosen.Index;
                r.NvencGpu = same.IndexOf(chosen);
            }
            return r;
        }

        static int Even(double x)
        {
            return Math.Max(2, (int)Math.Round(x / 2.0) * 2);
        }

        /// <summary>Target {w, h} for scaling after rotation, or null to keep the rotated source size.</summary>
        public static int[] OutputSize(ProbeInfo info, VideoCfg video)
        {
            if (video.ResolutionMode != "custom") return null;
            int w = video.Width, h = video.Height;
            if (w == 0 && h == 0) return null;
            int sw = info.Width, sh = info.Height;
            if (w == 0 || h == 0)
            {
                if (sw == 0 || sh == 0) throw new BuildError("source dimensions unknown; set both width and height");
                int rw = sh, rh = sw;    // rotation swaps width and height
                if (w != 0) h = Even((double)w * rh / rw);
                else w = Even((double)h * rw / rh);
            }
            if (sw != 0 && sh != 0 && w == sh && h == sw) return null;
            return new[] { w, h };
        }

        /// <summary>Dimensions the finished file should have (used to verify the output), or null.</summary>
        public static int[] ExpectedSize(ProbeInfo info, VideoCfg video)
        {
            int[] target = OutputSize(info, video);
            if (target != null) return target;
            if (info.Width != 0 && info.Height != 0) return new[] { info.Height, info.Width };
            return null;
        }

        static string MbpsText(double bps)
        {
            // decimal rounding keeps results identical on every runtime (double Math.Round differs on .5 edges)
            decimal mbps = Math.Round((decimal)(bps / 1000000.0), 2);
            if (mbps < 0.1m) mbps = 0.1m;
            return Util.Num((double)mbps, 2) + "M";
        }

        static void RateArgs(List<string> args, string backend, VideoCfg video, ProbeInfo info, List<string> notes)
        {
            string mode = video.RateMode;
            if (mode == "quality")
            {
                string q = video.Quality.ToString(Util.Inv);
                if (backend == "intel_qsv") args.AddRange(new[] { "-global_quality", q });
                else if (backend == "nvidia_nvenc") args.AddRange(new[] { "-rc", "vbr", "-cq", q, "-b:v", "0" });
                else if (backend == "amd_amf") args.AddRange(new[] { "-rc", "cqp", "-qp_i", q, "-qp_p", q });
                else args.AddRange(new[] { "-crf", q });
                return;
            }
            double bps;
            if (mode == "fixed") bps = video.FixedMbps * 1000000.0;
            else if (info.BitRate > 0) bps = info.BitRate * (mode == "percent" ? video.Percent / 100.0 : 1.0);
            else
            {
                bps = video.FallbackMbps * 1000000.0;
                notes.Add("source bitrate unknown; using fallback " + Util.Num(video.FallbackMbps, 3) + " Mbps");
            }
            long ibps = (long)bps;
            string text = MbpsText(ibps);
            args.AddRange(new[] { "-b:v", text, "-maxrate", text, "-bufsize", Math.Min(ibps * 2, 2000000000L).ToString(Util.Inv) });
        }

        static void PresetArgs(List<string> args, string backend, string preset)
        {
            if (preset == "balanced") return;    // encoder default
            bool fast = preset == "fast";
            if (backend == "nvidia_nvenc") args.AddRange(new[] { "-preset", fast ? "p2" : "p6" });
            else if (backend == "amd_amf") args.AddRange(new[] { "-quality", fast ? "speed" : "quality" });
            else args.AddRange(new[] { "-preset", fast ? "veryfast" : "slow" });
        }

        public static BuildResult Build(string ffmpeg, VerticalCfg v, ProbeInfo info, string src, string dst,
                                        Resolved resolved, bool progress)
        {
            VideoCfg video = v.Video;
            GpuCfg gpu = v.Gpu;
            string backend = resolved.Backend;
            string codec = video.Codec;
            string enc = Encoder(backend, codec);
            BuildResult res = new BuildResult();
            List<string> args = res.Args;
            List<string> notes = res.Notes;

            int rot = Transpose(video.Rotation);
            int[] size = OutputSize(info, video);
            bool hwdec = gpu.HwDecode && !resolved.NoHwDecode && backend != "cpu";
            args.Add(ffmpeg);
            args.Add("-y");
            if (progress) args.AddRange(new[] { "-hide_banner", "-loglevel", "warning", "-nostats", "-progress", "pipe:1" });

            List<string> chain = new List<string>();
            if (backend == "intel_qsv")
            {
                string device = "qsv=hw";
                if (resolved.QsvChildIndex >= 0)
                    device += ",child_device=" + resolved.QsvChildIndex.ToString(Util.Inv) + ",child_device_type=d3d11va";
                args.AddRange(new[] { "-init_hw_device", device, "-filter_hw_device", "hw" });
                string decoder = hwdec ? QsvDecoder(info.Codec) : null;
                if (hwdec && decoder == null)
                    notes.Add("no QSV decoder for '" + info.Codec + "'; decoding in software");
                if (decoder != null)
                {
                    args.AddRange(new[] { "-hwaccel", "qsv", "-c:v", decoder });
                }
                else
                {
                    chain.Add("format=nv12");
                    chain.Add("hwupload=extra_hw_frames=64");
                }
                chain.Add("vpp_qsv=transpose=" + rot.ToString(Util.Inv));
                if (size != null)
                    chain.Add("scale_qsv=w=" + size[0].ToString(Util.Inv) + ":h=" + size[1].ToString(Util.Inv));
            }
            else
            {
                if (hwdec && backend == "nvidia_nvenc")
                {
                    args.AddRange(new[] { "-hwaccel", "cuda" });
                    if (resolved.NvencGpu >= 0) args.AddRange(new[] { "-hwaccel_device", resolved.NvencGpu.ToString(Util.Inv) });
                }
                else if (hwdec && backend == "amd_amf")
                {
                    args.AddRange(new[] { "-hwaccel", "d3d11va" });
                }
                chain.Add("transpose=" + rot.ToString(Util.Inv));
                if (size != null)
                    chain.Add("scale=" + size[0].ToString(Util.Inv) + ":" + size[1].ToString(Util.Inv));
            }

            args.AddRange(Util.SplitArgs(video.ExtraInputArgs));
            List<string> input;
            try { input = Media.InputArgs(src); }
            catch (ProbeError ex) { throw new BuildError(ex.Message); }
            args.AddRange(input);
            args.AddRange(new[] { "-i", src, "-vf", string.Join(",", chain.ToArray()), "-c:v", enc });
            RateArgs(args, backend, video, info, notes);

            if (backend == "intel_qsv" && codec == "h264") args.AddRange(new[] { "-look_ahead", "0" });   // hevc_qsv has no such option
            if (backend == "nvidia_nvenc" && resolved.NvencGpu >= 0)
                args.AddRange(new[] { "-gpu", resolved.NvencGpu.ToString(Util.Inv) });
            args.AddRange(new[] { "-profile:v", codec == "h264" ? video.Profile : "main" });
            PresetArgs(args, backend, video.Preset);
            if (backend == "cpu")
            {
                args.AddRange(new[] { "-pix_fmt", "yuv420p" });
                if (gpu.CpuThreads > 0) args.AddRange(new[] { "-threads", gpu.CpuThreads.ToString(Util.Inv) });
            }
            if (video.Fps > 0) args.AddRange(new[] { "-r", Util.Num(video.Fps, 3) });

            if (video.AudioMode == "copy") args.AddRange(new[] { "-c:a", "copy" });
            else if (video.AudioMode == "aac") args.AddRange(new[] { "-c:a", "aac", "-b:a", video.AudioKbps.ToString(Util.Inv) + "k" });
            else args.Add("-an");

            args.AddRange(Util.SplitArgs(video.ExtraOutputArgs));
            args.Add(dst);
            return res;
        }
    }
}
