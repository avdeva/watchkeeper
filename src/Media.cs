// ffprobe wrapper and process priority helper.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace Watchkeeper
{
    public class ProbeError : Exception
    {
        public ProbeError(string message) : base(message) { }
    }

    /// <summary>Facts about the first video stream. Zero means unknown.</summary>
    public class ProbeInfo
    {
        public string Codec = "";
        public int Width;
        public int Height;
        public double BitRate;
        public double Fps;
        public double Duration;
    }

    public static class Media
    {
        public static void SetPriority(Process p, string priority)
        {
            try
            {
                if (priority == "below_normal") p.PriorityClass = ProcessPriorityClass.BelowNormal;
                else if (priority == "idle") p.PriorityClass = ProcessPriorityClass.Idle;
            }
            catch (Exception) { }
        }

        static double Num(object v)
        {
            double d = 0;
            if (v is double) d = (double)v;
            else if (v is string)
            {
                if (!double.TryParse((string)v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return 0;
            }
            else return 0;
            return d > 0 ? d : 0;
        }

        static double Fraction(object v)
        {
            string s = v as string;
            if (s != null && s.IndexOf('/') > 0)
            {
                string[] parts = s.Split('/');
                double a, b;
                if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out a) &&
                    double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out b) && b != 0)
                    return a / b;
                return 0;
            }
            return Num(v);
        }

        /// <summary>Input options that keep ffmpeg and ffprobe to reading this one local file as the container its extension
        /// names: no network or other protocols, and no format guessed from the content (a file named .mp4 that really is a
        /// playlist or a concat list could otherwise make them read other files or addresses). Throws ProbeError for a file
        /// type that is not a known recording container.</summary>
        public static List<string> InputArgs(string path)
        {
            string fmt = ContainerFor(path);
            if (fmt == null)
                throw new ProbeError("'" + System.IO.Path.GetExtension(path) + "' is not a recording container Watchkeeper reads " +
                                     "(.mp4, .mov, .m4v, .mkv, .webm, .mxf, .ts, .m2ts, .mts, .avi, .flv, .wmv)");
            return new List<string> { "-protocol_whitelist", "file", "-f", fmt };
        }

        /// <summary>ffmpeg's name for the container of this file type, or null when it is not a known recording container.</summary>
        public static string ContainerFor(string path)
        {
            switch ((System.IO.Path.GetExtension(path ?? "") ?? "").ToLowerInvariant())
            {
                case ".mp4": case ".m4v": case ".mov": case ".3gp": case ".3g2": return "mov";
                case ".mkv": case ".webm": return "matroska";
                case ".mxf": return "mxf";
                case ".ts": case ".m2ts": case ".mts": return "mpegts";
                case ".avi": return "avi";
                case ".flv": return "flv";
                case ".wmv": case ".asf": return "asf";
            }
            return null;
        }

        /// <summary>Probe the first video stream. Bit rate comes from the stream, then the container.</summary>
        public static ProbeInfo Probe(string ffprobe, string path, int timeoutMs)
        {
            List<string> args = new List<string> {
                "-v", "error", "-select_streams", "v:0", "-show_entries",
                "stream=codec_name,width,height,bit_rate,r_frame_rate,duration:format=duration,bit_rate",
                "-of", "json" };
            args.AddRange(InputArgs(path));
            args.Add("-i");
            args.Add(path);
            Util.CaptureResult r;
            try { r = Util.RunMedia(ffprobe, args, timeoutMs); }
            catch (Exception ex) { throw new ProbeError("cannot run ffprobe (" + ffprobe + "): " + ex.Message); }
            if (r.TimedOut) throw new ProbeError("ffprobe timed out");
            if (r.ExitCode != 0)
            {
                string e = (r.Error ?? "").Trim();
                if (e.Length > 300) e = e.Substring(e.Length - 300);
                throw new ProbeError("ffprobe failed: " + (e.Length > 0 ? e : r.ExitCode.ToString(Util.Inv)));
            }
            JObject root;
            try { root = Json.Parse(string.IsNullOrWhiteSpace(r.Output) ? "{}" : r.Output) as JObject; }
            catch (FormatException) { throw new ProbeError("ffprobe returned unreadable output"); }
            if (root == null) throw new ProbeError("ffprobe returned unreadable output");
            List<object> streams = root.Get("streams") as List<object>;
            JObject fmt = root.Get("format") as JObject ?? new JObject();
            if (streams == null || streams.Count == 0 || !(streams[0] is JObject))
                throw new ProbeError("no video stream found");
            JObject st = (JObject)streams[0];
            ProbeInfo info = new ProbeInfo();
            info.Codec = st.Get("codec_name") as string ?? "";
            info.Width = (int)Num(st.Get("width"));
            info.Height = (int)Num(st.Get("height"));
            info.BitRate = Num(st.Get("bit_rate")) > 0 ? Num(st.Get("bit_rate")) : Num(fmt.Get("bit_rate"));
            info.Fps = Fraction(st.Get("r_frame_rate"));
            info.Duration = Num(st.Get("duration")) > 0 ? Num(st.Get("duration")) : Num(fmt.Get("duration"));
            return info;
        }

        public static ProbeInfo Probe(string ffprobe, string path) { return Probe(ffprobe, path, 60000); }

        /// <summary>Container duration in seconds, or 0 if it cannot be determined.</summary>
        public static double ProbeDuration(string ffprobe, string path, int timeoutMs)
        {
            List<string> args = new List<string> { "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0" };
            try
            {
                args.AddRange(InputArgs(path));
                args.Add("-i");
                args.Add(path);
                Util.CaptureResult r = Util.RunMedia(ffprobe, args, timeoutMs);
                if (r.TimedOut || r.ExitCode != 0) return 0;
                string text = (r.Output ?? "").Trim();
                if (text.Length == 0) return 0;
                string first = text.Split('\n')[0].Trim();
                return Num(first);
            }
            catch (Exception) { return 0; }
        }

        public static double ProbeDuration(string ffprobe, string path) { return ProbeDuration(ffprobe, path, 30000); }
    }
}
