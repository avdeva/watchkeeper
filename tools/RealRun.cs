// Runs the vertical converter against a real ffmpeg. Usage: mono realrun.exe <workdir> <backend> [extra-ffmpeg-path]
using System;
using System.IO;
using System.Threading;
using System.Diagnostics;
using System.Collections.Generic;
using Watchkeeper;

static class RealRun
{

    static void Gen(string path, string size, string vcodec, bool audio, int secs, string extra)
    {
        string args = "-hide_banner -loglevel error -y -f lavfi -i \"testsrc2=size=" + size + ":rate=30,format=yuv420p\" ";
        if (audio) args += "-f lavfi -i \"sine=frequency=440:sample_rate=48000\" ";
        args += "-t " + secs + " -c:v " + vcodec + " -b:v 4M " + (audio ? "-c:a aac -b:a 128k " : "-an ") + extra + " \"" + path + "\"";
        Process p = Process.Start(new ProcessStartInfo("/usr/bin/ffmpeg", args) { UseShellExecute = false });
        p.WaitForExit();
    }

    class Variant
    {
        public string Name; public Action<VerticalCfg> Apply; public string Size = "1280x720"; public string VCodec = "libx264";
        public bool Audio = true; public string Ext = "mp4"; public string Extra = ""; public int[] Expect; // expected w,h
        public Variant(string n, Action<VerticalCfg> a) { Name = n; Apply = a; }
    }

    static int Matrix(string work)
    {
        List<Variant> vs = new List<Variant>();
        vs.Add(new Variant("default", delegate(VerticalCfg v) { }) { Expect = new[] { 720, 1280 } });
        vs.Add(new Variant("hevc", delegate(VerticalCfg v) { v.Video.Codec = "hevc"; }) { Expect = new[] { 720, 1280 } });
        vs.Add(new Variant("percent50", delegate(VerticalCfg v) { v.Video.RateMode = "percent"; v.Video.Percent = 50; }) { Expect = new[] { 720, 1280 } });
        vs.Add(new Variant("fixed3", delegate(VerticalCfg v) { v.Video.RateMode = "fixed"; v.Video.FixedMbps = 3; }) { Expect = new[] { 720, 1280 } });
        vs.Add(new Variant("quality28", delegate(VerticalCfg v) { v.Video.RateMode = "quality"; v.Video.Quality = 28; }) { Expect = new[] { 720, 1280 } });
        vs.Add(new Variant("aac", delegate(VerticalCfg v) { v.Video.AudioMode = "aac"; }) { Expect = new[] { 720, 1280 } });
        vs.Add(new Variant("noaudio-opt", delegate(VerticalCfg v) { v.Video.AudioMode = "none"; }) { Expect = new[] { 720, 1280 } });
        vs.Add(new Variant("custom540x960", delegate(VerticalCfg v) { v.Video.ResolutionMode = "custom"; v.Video.Width = 540; v.Video.Height = 960; }) { Expect = new[] { 540, 960 } });
        vs.Add(new Variant("custom-width-only", delegate(VerticalCfg v) { v.Video.ResolutionMode = "custom"; v.Video.Width = 360; v.Video.Height = 0; }) { Expect = new[] { 360, 640 } });
        vs.Add(new Variant("fps15", delegate(VerticalCfg v) { v.Video.Fps = 15; }) { Expect = new[] { 720, 1280 } });
        vs.Add(new Variant("preset-fast", delegate(VerticalCfg v) { v.Video.Preset = "fast"; }) { Expect = new[] { 720, 1280 } });
        vs.Add(new Variant("preset-quality", delegate(VerticalCfg v) { v.Video.Preset = "quality"; }) { Expect = new[] { 720, 1280 } });
        vs.Add(new Variant("clockwise", delegate(VerticalCfg v) { v.Video.Rotation = "clock"; }) { Expect = new[] { 720, 1280 } });
        vs.Add(new Variant("extra-out", delegate(VerticalCfg v) { v.Video.ExtraOutputArgs = "-movflags +faststart"; }) { Expect = new[] { 720, 1280 } });
        vs.Add(new Variant("source-noaudio", delegate(VerticalCfg v) { }) { Audio = false, Expect = new[] { 720, 1280 } });
        vs.Add(new Variant("source-hevc", delegate(VerticalCfg v) { }) { VCodec = "libx265", Expect = new[] { 720, 1280 } });
        vs.Add(new Variant("source-4k", delegate(VerticalCfg v) { }) { Size = "3840x2160", Expect = new[] { 2160, 3840 } });
        vs.Add(new Variant("source-odd", delegate(VerticalCfg v) { }) { Size = "1278x718", Expect = new[] { 718, 1278 } });
        vs.Add(new Variant("source-mkv", delegate(VerticalCfg v) { }) { Ext = "mkv", Expect = new[] { 720, 1280 } });
        vs.Add(new Variant("source-portrait", delegate(VerticalCfg v) { }) { Size = "720x1280", Expect = new[] { 1280, 720 } });
        vs.Add(new Variant("threads2", delegate(VerticalCfg v) { }) { Expect = new[] { 720, 1280 } });
        int bad = 0;
        foreach (Variant v in vs)
        {
            string w = Path.Combine(work, "m-" + v.Name);
            if (Directory.Exists(w)) Directory.Delete(w, true);
            string inDir = Path.Combine(w, "in");
            Directory.CreateDirectory(inDir);
            string src = Path.Combine(inDir, "MultiCorder7 - Cam1 - 23 July 2026 - 08-29-03 AM." + v.Ext);
            Gen(src, v.Size, v.VCodec, v.Audio, 3, "");
            Engine core = new Engine(Path.Combine(w, "data"), new EngineOptions { StartControl = false });
            core.Config.Load();
            AppConfig c = core.Config.Get();
            c.General.FfmpegPath = "/usr/bin/ffmpeg"; c.General.FfprobePath = "/usr/bin/ffprobe";
            core.Config.SaveSection("general", c.General);
            c = core.Config.Get();
            c.Vertical.WatchDir = inDir; c.Vertical.OutDir = Path.Combine(w, "out");
            c.Vertical.Gpu.Backend = "cpu";
            if (v.Name == "threads2") c.Vertical.Gpu.CpuThreads = 2;
            if (v.Ext != "mp4") c.Vertical.Extensions = new List<string> { "." + v.Ext };
            v.Apply(c.Vertical);
            c.Vertical.Enabled = true;
            List<string> errs = core.Config.SaveSection("vertical", c.Vertical);
            if (errs.Count > 0) { Console.WriteLine("CONFIG REJECTED " + v.Name + ": " + string.Join("; ", errs.ToArray())); bad++; continue; }
            core.Vertical.StableWaitMs = 300;
            core.Start();
            DateTime end = DateTime.Now.AddSeconds(90);
            while (DateTime.Now < end)
            {
                ConverterStatus st = core.Vertical.Status();
                if (st.History.Count > 0 && st.Current == null) break;
                Thread.Sleep(300);
            }
            ConverterStatus f = core.Vertical.Status();
            core.Shutdown();
            string result;
            if (f.History.Count == 0) result = "NO RESULT";
            else
            {
                HistoryItem h = f.History[0];
                result = h.Status + (h.Error.Length > 0 ? " (" + h.Error + ")" : "");
                if (h.Status == "done")
                {
                    string outp = Path.Combine(Path.Combine(w, "out"), h.OutName);
                    Process pr = Process.Start(new ProcessStartInfo("/usr/bin/ffprobe", "-v error -select_streams v:0 -show_entries stream=width,height -of csv=p=0 \"" + outp + "\"") { UseShellExecute = false, RedirectStandardOutput = true });
                    string o = pr.StandardOutput.ReadToEnd().Trim(); pr.WaitForExit();
                    string want = v.Expect[0] + "," + v.Expect[1];
                    if (o != want) { result += " SIZE " + o + " expected " + want; }
                }
            }
            bool ok = result == "done";
            if (!ok) bad++;
            Console.WriteLine((ok ? "ok   " : "FAIL ") + v.Name + ": " + result);
        }
        Console.WriteLine(bad == 0 ? "MATRIX ALL OK" : bad + " VARIANT(S) FAILED");
        return bad;
    }

    static int Main(string[] a)
    {
        if (a[0] == "matrix") return Matrix(a[1]);
        string work = Path.GetFullPath(a[0]);
        string backend = a.Length > 1 ? a[1] : "cpu";
        string ff = a.Length > 2 ? a[2] : "/usr/bin/ffmpeg";
        string fp = a.Length > 3 ? a[3] : "/usr/bin/ffprobe";
        if (backend.StartsWith("test:"))
        {
            AppConfig tc = new AppConfig();
            tc.General.FfmpegPath = ff; tc.General.FfprobePath = fp;
            tc.Vertical.Gpu.Backend = backend.Substring(5);
            Cfg.Normalize(tc);
            var adapters = new System.Collections.Generic.List<Adapter>();
            EncoderTestResult r = Converter.RunEncoderTest(tc.General, tc.Vertical, adapters, 60000);
            Console.WriteLine("TEST ok=" + r.Ok + " backend=" + r.Backend + " secs=" + r.Seconds);
            Console.WriteLine("CMD: " + r.Command);
            Console.WriteLine("ERR: " + r.Error);
            Console.WriteLine("OUT: " + r.Output);
            Console.WriteLine("NOTE: " + r.Note);
            Console.WriteLine("LOG: " + r.Log);
            return 0;
        }
        string data = Path.Combine(work, "data");
        if (Directory.Exists(data)) Directory.Delete(data, true);
        Engine core = new Engine(data, new EngineOptions { StartControl = false });
        core.Hub.Echo = true;
        core.Config.Load();
        AppConfig c = core.Config.Get();
        c.General.FfmpegPath = ff; c.General.FfprobePath = fp;
        var e = core.Config.SaveSection("general", c.General);
        c = core.Config.Get();
        c.Vertical.WatchDir = Path.Combine(work, "in");
        c.Vertical.OutDir = Path.Combine(work, "out");
        c.Vertical.Gpu.Backend = backend;
        c.Vertical.Enabled = true;
        e = core.Config.SaveSection("vertical", c.Vertical);
        foreach (string s in e) Console.WriteLine("CONFIG ERROR: " + s);
        core.Vertical.StableWaitMs = 500;
        core.Start();
        DateTime end = DateTime.Now.AddSeconds(120);
        while (DateTime.Now < end)
        {
            ConverterStatus st = core.Vertical.Status();
            if (st.History.Count > 0 && st.Current == null) break;
            Thread.Sleep(500);
        }
        ConverterStatus f = core.Vertical.Status();
        foreach (HistoryItem h in f.History)
            Console.WriteLine("HISTORY: " + h.Status + " " + h.Name + " -> " + h.OutName + " size=" + h.OutSize + " srcDeleted=" + h.SourceDeleted + " err=" + h.Error + " backend=" + h.Backend);
        foreach (HistoryItem h in f.History)
            if (h.Detail.Length > 0) Console.WriteLine("DETAIL:\n" + h.Detail);
        core.Shutdown();
        return 0;
    }
}
