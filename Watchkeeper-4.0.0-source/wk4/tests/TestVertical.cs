using System;
using System.Collections.Generic;
using System.IO;
using Watchkeeper;

namespace Watchkeeper.Tests
{
    public class VerticalTests : Env
    {
        Settings cfg = new Settings();
        FakeAlerts alerts = new FakeAlerts();
        Vertical vert;

        public VerticalTests()
        {
            if (Util.IsWindows) throw new SkipException("the fake ffmpeg is a Python script (Linux only)");
            cfg.General.FfmpegPath = Ffmpeg;
            cfg.General.FfprobePath = Ffprobe;
            VerticalSettings v = cfg.Vertical;
            v.Enabled = true;
            v.WatchFolder = D("in");
            v.OutputFolder = D("out");
            v.MoveOriginalsTo = D("originals");
            v.AfterSuccess = "move";
            v.SettleSeconds = 2;
            v.Encoder = "intel_qsv";
            v.Sources.Add(new VerticalSource { Name = "PGM", Match = "Recorder7", Prefix = "Vertical PGM" });
            v.Sources.Add(new VerticalSource { Name = "Off", Match = "Recorder5", Prefix = "X", Enabled = false });
            Directory.CreateDirectory(D("in"));
            vert = new Vertical(delegate { return cfg; }, new SystemProcesses(), Hub.For("vertical"), alerts);
        }

        string Recording(string name)
        {
            string p = D("in", name);
            WriteMedia(p);
            Age(p, 30);
            return p;
        }

        void TwoPasses() { vert.Pass(); vert.Pass(); }

        [Fact]
        public void Names_come_from_the_source_rules()
        {
            string why;
            Assert.Eq("Vertical PGM", Vertical.PrefixFor("Recorder7 - Cam1 - 1 May.mp4", cfg.Vertical, out why));
            Assert.Null(Vertical.PrefixFor("Recorder5 - Cam1.mp4", cfg.Vertical, out why));
            Assert.Contains(why, "switched off");
            Assert.Eq("Vertical", Vertical.PrefixFor("Other.mp4", cfg.Vertical, out why));
            Assert.Eq("Vertical PGM - 23 July 2026 - 08-29-03 AM.mp4", Vertical.OutputName("Recorder7 - Cam1 - 23 July 2026 - 08-29-03 AM.mp4", "Vertical PGM"));
            Assert.Eq("Vertical - clip.mp4", Vertical.OutputName("clip.mov", "Vertical"));
        }

        [Fact]
        public void A_recording_is_converted_checked_and_the_original_moved()
        {
            string src = Recording("Recorder7 - Cam1 - 1 May 2026 - 10-00-00 AM.mp4");
            vert.Pass();
            Assert.False(Directory.Exists(D("out")) && Directory.GetFiles(D("out")).Length > 0, "not on the first look: must be seen unchanged twice");
            vert.Pass();
            string outFile = D("out", "Vertical PGM - 1 May 2026 - 10-00-00 AM.mp4");
            Assert.True(File.Exists(outFile), LogText());
            Assert.Contains(File.ReadAllText(outFile), "\"width\": 1080");
            Assert.False(File.Exists(src));
            Assert.True(File.Exists(D("originals", Path.GetFileName(src))));
            List<string[]> calls = FfmpegCalls();
            Assert.Eq("h264_qsv", calls[0][Array.LastIndexOf(calls[0], "-c:v") + 1]);
            Assert.Eq("vpp_qsv=transpose=2", ArgAfter(calls[0], "-vf"));
            Assert.Eq("8M", ArgAfter(calls[0], "-b:v"));
            Assert.Eq("done", vert.Snapshot().Recent[0].State);
            Assert.Eq(0, Directory.GetFiles(D("out"), "*.watchkeeper-partial*").Length);
        }

        [Fact]
        public void A_failing_GPU_falls_back_and_is_remembered()
        {
            Environment.SetEnvironmentVariable("FAKE_FAIL_HWDEC", "1");
            Recording("Recorder7 - Cam1 - a.mp4");
            Recording("Recorder7 - Cam1 - b.mp4");
            TwoPasses();
            Assert.True(File.Exists(D("out", "Vertical PGM - a.mp4")));
            Assert.True(File.Exists(D("out", "Vertical PGM - b.mp4")));
            List<string[]> calls = FfmpegCalls();
            Assert.Eq(3, calls.Count);                                      // a: hardware decode fails, software decode works; b: straight to what worked
            Assert.True(Array.IndexOf(calls[0], "-hwaccel") >= 0);
            Assert.True(Array.IndexOf(calls[1], "-hwaccel") < 0);
            Assert.True(Array.IndexOf(calls[2], "-hwaccel") < 0);
            Assert.Contains(vert.Snapshot().Recent[0].Note, "software decoding");
        }

        [Fact]
        public void The_signed_in_session_is_tried_when_the_GPU_fails_in_the_service()
        {
            Environment.SetEnvironmentVariable("FAKE_FAIL_UNLESS_ENV", "WATCHKEEPER_SESSION");
            Recording("Recorder7 - Cam1 - a.mp4");
            TwoPasses();
            Assert.True(File.Exists(D("out", "Vertical PGM - a.mp4")), LogText());
            Assert.Contains(vert.Snapshot().Recent[0].Note, "in the signed-in session");
        }

        [Fact]
        public void The_CPU_is_the_last_resort()
        {
            Environment.SetEnvironmentVariable("FAKE_FAIL_ENCODER", "h264_qsv");
            Recording("Recorder7 - Cam1 - a.mp4");
            TwoPasses();
            Assert.True(File.Exists(D("out", "Vertical PGM - a.mp4")));
            List<string[]> calls = FfmpegCalls();
            Assert.Eq("libx264", ArgAfter(calls[calls.Count - 1], "-c:v"));
            Assert.Eq(5, calls.Count);                                      // QSV x4 (hw/sw decode, service/session), then the CPU
        }

        [Fact]
        public void A_bad_output_is_not_kept_and_the_original_stays()
        {
            cfg.Vertical.CpuFallback = false;
            cfg.Vertical.UserSessionFallback = false;
            cfg.Vertical.Retries = 0;
            string src = Recording("Recorder7 - SHORTOUT - x.mp4");
            TwoPasses();
            Assert.True(File.Exists(src));
            Assert.Eq(0, Directory.GetFiles(D("out")).Length);
            Assert.Eq("failed", vert.Snapshot().Recent[0].State);
            Assert.Contains(vert.Snapshot().Recent[0].Note, "long instead of");
            Assert.Contains(alerts.Text(), "could not be converted after 1 attempts");
        }

        [Fact]
        public void Failures_back_off_and_retry_can_be_forced()
        {
            cfg.Vertical.CpuFallback = false;
            cfg.Vertical.UserSessionFallback = false;
            string src = Recording("Recorder7 - FAILME - x.mp4");
            TwoPasses();
            int calls = FfmpegCalls().Count;
            vert.Pass();
            Assert.Eq(calls, FfmpegCalls().Count);                          // waits before trying again
            Assert.Eq("retry", vert.Snapshot().Files[0].State);
            vert.RetryFailed();
            TwoPasses();
            Assert.True(FfmpegCalls().Count > calls);
            Assert.True(File.Exists(src));
        }

        [Fact]
        public void Existing_outputs_and_switched_off_sources_are_left_alone()
        {
            string a = Recording("Recorder7 - Cam1 - a.mp4");
            string b = Recording("Recorder5 - Cam1 - b.mp4");
            Write(D("out", "Vertical PGM - a.mp4"), "already");
            TwoPasses();
            Assert.True(File.Exists(a));
            Assert.True(File.Exists(b));
            Assert.Eq(0, FfmpegCalls().Count);
            List<VerticalFile> files = vert.Snapshot().Files;
            Assert.Eq("exists", files.Find(delegate(VerticalFile f) { return f.Name.StartsWith("Recorder7"); }).State);
            Assert.Eq("skipped", files.Find(delegate(VerticalFile f) { return f.Name.StartsWith("Recorder5"); }).State);
        }

        [Fact]
        public void A_missing_folder_is_an_alert()
        {
            cfg.Vertical.WatchFolder = D("nope");
            vert.Pass();
            Assert.Eq("error", vert.Snapshot().State);
            Assert.Contains(alerts.Text(), "does not exist");
        }

        [Fact]
        public void The_command_for_each_encoder()
        {
            ProbeInfo info = new ProbeInfo { Codec = "h264", Width = 1920, Height = 1080, BitRate = 6000000, Duration = 10 };
            VerticalSettings v = cfg.Vertical;
            List<string> qsv = Watchkeeper.Ffmpeg.Build(v, info, "in.mp4", "out.mp4", new Attempt { Encoder = "intel_qsv", HardwareDecode = true });
            Assert.Eq("-y -hide_banner -loglevel error -nostats -progress pipe:1 -init_hw_device qsv=hw -filter_hw_device hw -hwaccel qsv -c:v h264_qsv " +
                      "-protocol_whitelist file -f mov -i in.mp4 -vf vpp_qsv=transpose=2 -c:v h264_qsv -b:v 6M -maxrate 6M -bufsize 12000000 -look_ahead 0 " +
                      "-profile:v high -c:a copy -f mp4 out.mp4", string.Join(" ", qsv.ToArray()));
            v.Width = 720;
            v.Rotate = "clockwise";
            List<string> nv = Watchkeeper.Ffmpeg.Build(v, info, "in.mp4", "out.mp4", new Attempt { Encoder = "nvidia_nvenc", HardwareDecode = true });
            Assert.Eq("transpose=1,scale=720:1280", ArgAfter(nv, "-vf"));
            Assert.Eq("cuda", ArgAfter(nv, "-hwaccel"));
            v.Bitrate = "quality";
            List<string> cpu = Watchkeeper.Ffmpeg.Build(v, info, "in.mp4", "out.mp4", new Attempt { Encoder = "cpu" });
            Assert.Eq("23", ArgAfter(cpu, "-crf"));
            Assert.Eq("yuv420p", ArgAfter(cpu, "-pix_fmt"));
            List<Attempt> plan = Watchkeeper.Ffmpeg.Plan(v, Ffmpeg);
            Assert.Eq(5, plan.Count);
            Assert.Eq("Intel Quick Sync (software decoding, in the signed-in session)", plan[3].Label());
        }

        [Fact]
        public void The_encoder_test_reports_each_attempt()
        {
            Environment.SetEnvironmentVariable("FAKE_FAIL_HWDEC", "1");
            List<string> lines = Vertical.TestEncoders(cfg, new SystemProcesses());
            Assert.Eq(5, lines.Count);
            Assert.True(lines[0].StartsWith("FAILED  Intel Quick Sync"), lines[0]);
            Assert.True(lines[1].StartsWith("WORKS   Intel Quick Sync (software decoding)"), lines[1]);
        }
    }
}
