using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Watchkeeper;

namespace Watchkeeper.Tests
{
    public class NameRuleTests : Env
    {
        const string Pgm = "MultiCorder7 - Cam1 - 23 July 2026 - 08-29-03 AM.mp4";
        const string Split = "MultiCorder6 - Cam1 - 23 July 2026 - 08-29-03 AM.mp4";
        const string Other = "MultiCorder3 - Cam1 - 23 July 2026 - 08-29-03 AM.mp4";

        static string Prefix(string name, VerticalCfg v, out string why)
        {
            return Converter.DecidePrefix(name, v, out why);
        }

        [Fact]
        public void Output_stem()
        {
            Assert.Eq("Vertical PGM - 23 July 2026 - 08-29-03 AM",
                      Converter.OutputStem("MultiCorder7 - Cam1 - 23 July 2026 - 08-29-03 AM", "Vertical PGM"));
            Assert.Eq("Vertical - weird", Converter.OutputStem("weird", "Vertical"));
        }

        [Fact]
        public void Rules()
        {
            VerticalCfg v = Cfgs.Get().Vertical;
            string why;
            Assert.Eq("Vertical PGM", Prefix(Pgm, v, out why));
            Assert.Eq("Vertical Split", Prefix(Split, v, out why));
            Assert.Eq("Vertical", Prefix(Other, v, out why));
            Assert.Eq("Vertical PGM", Prefix(Pgm.ToLowerInvariant(), v, out why));    // case-insensitive
        }

        [Fact]
        public void Disabled_source_blocks_instead_of_falling_through()
        {
            VerticalCfg v = Cfgs.Get().Vertical;
            v.Sources[1].Enabled = false;    // Split off
            string why;
            Assert.Null(Prefix(Split, v, out why));
            Assert.Contains(why, "switched off");
            Assert.Eq("Vertical", Prefix(Other, v, out why));
        }

        [Fact]
        public void Other_files_switch()
        {
            VerticalCfg v = Cfgs.Get().Vertical;
            v.Other.Enabled = false;
            string why;
            Assert.Null(Prefix(Other, v, out why));
            Assert.Eq("Vertical PGM", Prefix(Pgm, v, out why));
        }

        [Fact]
        public void First_matching_source_wins()
        {
            VerticalCfg v = Cfgs.Get().Vertical;
            v.Sources.Insert(0, new SourceRule("x", "Cam1", "Cam1", "Cam One"));
            string why;
            Assert.Eq("Cam One", Prefix(Pgm, v, out why));
        }
    }

    public class ConverterTests : Env
    {
        const string Pgm = "MultiCorder7 - Cam1 - 23 July 2026 - 08-29-03 AM.mp4";
        const string Split = "MultiCorder6 - Cam1 - 23 July 2026 - 08-29-03 AM.mp4";
        const string Other = "MultiCorder3 - Cam1 - 23 July 2026 - 08-29-03 AM.mp4";
        const string PgmOut = "Vertical PGM - 23 July 2026 - 08-29-03 AM.mp4";

        string ingest, outDir;
        Converter conv;

        public ConverterTests()
        {
            ingest = D("Vertical", "Ingest");
            outDir = D("Vertical");
            Directory.CreateDirectory(ingest);
            Set<VerticalCfg>("vertical", delegate(VerticalCfg v) { v.WatchDir = ingest; v.OutDir = outDir; v.PollSeconds = 1; });
            conv = new Converter(Cfgs, Hub, new AdapterCache(300, delegate() { return new List<Adapter>(); }));
            conv.StableWaitMs = 100;
        }

        public override void Dispose()
        {
            conv.Stop(true, 10000);
            Environment.SetEnvironmentVariable("FAKE_FAIL_ENCODER", null);
            Environment.SetEnvironmentVariable("FAKE_FAIL_HWDEC", null);
            base.Dispose();
        }

        string Put(string name) { return Put(name, 1920, 1080, 600); }
        string Put(string name, double duration) { return Put(name, 1920, 1080, duration); }
        string Put(string name, int width, int height, double duration)
        {
            string p = Path.Combine(ingest, name);
            WriteMedia(p, width, height, duration);
            Age(p, 30);
            return p;
        }

        ConverterStatus St() { return conv.Status(); }

        List<HistoryItem> History(string status)
        {
            return St().History.FindAll(delegate(HistoryItem h) { return h.Status == status; });
        }

        bool WaitDone(int n, string status, double timeout)
        {
            return WaitFor(delegate { return History(status).Count >= n; }, timeout);
        }
        bool WaitDone() { return WaitDone(1, "done", 20); }
        bool WaitDone(int n) { return WaitDone(n, "done", 20); }
        bool WaitFailed() { return WaitDone(1, "failed", 20); }

        bool WaitRow(string name, string action)
        {
            return WaitFor(delegate
            {
                return St().Ingest.Exists(delegate(IngestRow r) { return r.Name == name && r.Action == action; });
            }, 10);
        }

        JObject ReadOut(string name)
        {
            return (JObject)Json.Parse(File.ReadAllText(Path.Combine(outDir, name)));
        }

        string[] OutMp4()
        {
            List<string> names = new List<string>();
            foreach (string f in Directory.GetFiles(outDir)) if (f.EndsWith(".mp4")) names.Add(Path.GetFileName(f));
            return names.ToArray();
        }

        string[] Partials()
        {
            List<string> names = new List<string>();
            foreach (string f in Directory.GetFiles(outDir)) if (f.EndsWith(".partial.mp4")) names.Add(Path.GetFileName(f));
            return names.ToArray();
        }

        static void Sleep(double seconds) { Thread.Sleep((int)(seconds * 1000)); }

        // ------------------------------------------------------------------
        [Fact]
        public void Converts_renames_verifies_and_deletes_source()
        {
            string src = Put(Pgm, 1920, 1080, 30);
            conv.Start();
            Assert.True(WaitDone());
            JObject meta = ReadOut(PgmOut);
            Assert.Eq(1080.0, (double)meta.Get("width"));    // rotated
            Assert.Eq(1920.0, (double)meta.Get("height"));
            Assert.False(File.Exists(src));
            Assert.Eq(0, Partials().Length);
            List<string[]> calls = EncodeCalls();
            Assert.Eq(1, calls.Count);
            Assert.In("vpp_qsv=transpose=2", calls[0]);
            Assert.True(calls[0][calls[0].Length - 1].EndsWith(".partial.mp4"));
            Assert.Eq(PgmOut, History("done")[0].OutName);
            Assert.True(History("done")[0].SourceDeleted);
        }

        [Fact]
        public void Prefixes_per_source_and_ignored_files()
        {
            Set<VerticalCfg>("vertical", delegate(VerticalCfg v) { v.Other.Enabled = false; });
            Put(Pgm);
            Put(Split);
            string c = Put(Other);    // no rule, 'other' off -> must stay untouched
            conv.Start();
            Assert.True(WaitDone(2));
            string[] names = OutMp4();
            Assert.In("Vertical PGM - 23 July 2026 - 08-29-03 AM.mp4", names);
            Assert.In("Vertical Split - 23 July 2026 - 08-29-03 AM.mp4", names);
            Assert.True(File.Exists(c));
            Assert.True(WaitRow(Other, "ignored"));
            Assert.Eq(2, EncodeCalls().Count);
        }

        [Fact]
        public void Disabled_source_is_never_converted()
        {
            Set<VerticalCfg>("vertical", delegate(VerticalCfg v) { v.Sources[0].Enabled = false; });
            string p = Put(Pgm);
            conv.Start();
            Assert.True(WaitRow(Pgm, "ignored"));
            Sleep(1.3);
            Assert.True(File.Exists(p));
            Assert.Eq(0, EncodeCalls().Count);
        }

        [Fact]
        public void Extension_filter()
        {
            string p = Put("MultiCorder7 - Cam1 - 23 July 2026 - 08-29-03 AM.mov");
            conv.Start();
            Sleep(1.5);
            Assert.True(File.Exists(p));
            Assert.Eq(0, EncodeCalls().Count);
        }

        [Fact]
        public void Failed_encode_keeps_source_and_removes_partial()
        {
            string name = "MultiCorder7 - Cam1 - 23 July 2026 - FAILME 08-29-03 AM.mp4";
            string src = Put(name);
            Set<VerticalCfg>("vertical", delegate(VerticalCfg v) { v.MaxRetries = 0; });
            conv.Start();
            Assert.True(WaitFailed());
            Assert.True(File.Exists(src));    // the source must survive a failed conversion
            Assert.Eq(0, OutMp4().Length);
            Assert.True(WaitRow(name, "failed"));
            Sleep(1.5);
            Assert.Eq(2, EncodeCalls().Count);    // hardware decode, then software decode; no retry loop after that
            Assert.Contains(St().Failures[0].Error, "exit code 1");
        }

        [Fact]
        public void Backoff_then_retry_succeeds()
        {
            string src = Put(Pgm);
            Environment.SetEnvironmentVariable("FAKE_FAIL_ENCODER", "h264_qsv");
            conv.Start();
            Assert.True(WaitFailed());
            Assert.True(WaitRow(Pgm, "backoff"));
            int n = EncodeCalls().Count;
            Sleep(1.5);
            Assert.Eq(n, EncodeCalls().Count);    // waiting out the back-off
            Environment.SetEnvironmentVariable("FAKE_FAIL_ENCODER", null);
            conv.RetryFailed();
            Assert.True(WaitDone());
            Assert.False(File.Exists(src));
        }

        [Fact]
        public void Changed_file_resets_failure_history()
        {
            string src = Put(Pgm);
            Environment.SetEnvironmentVariable("FAKE_FAIL_ENCODER", "h264_qsv");
            conv.Start();
            Assert.True(WaitFailed());
            Environment.SetEnvironmentVariable("FAKE_FAIL_ENCODER", null);
            WriteMedia(src, 1920, 1080, 777.5);    // file replaced -> new size/mtime signature
            Age(src, 30);
            Assert.True(WaitDone());
            Assert.Eq(777.5, (double)ReadOut(PgmOut).Get("duration"));
        }

        [Fact]
        public void Gpu_failure_falls_back_to_cpu_when_enabled()
        {
            Set<VerticalCfg>("vertical", delegate(VerticalCfg v) { v.Gpu.FallbackToCpu = true; });
            Environment.SetEnvironmentVariable("FAKE_FAIL_ENCODER", "h264_qsv");
            Put(Pgm);
            conv.Start();
            Assert.True(WaitDone());
            List<string[]> calls = EncodeCalls();
            Assert.Eq(3, calls.Count);    // GPU with hardware decode, GPU with software decode, CPU
            Assert.In("-hwaccel", calls[0]);
            Assert.False(Array.IndexOf(calls[1], "-hwaccel") >= 0);
            Assert.In("libx264", calls[2]);
            Assert.Contains(History("done")[0].Backend, "CPU");
        }

        [Fact]
        public void Software_decode_retry_rescues_a_hardware_decode_failure()
        {
            Set<VerticalCfg>("vertical", delegate(VerticalCfg v) { v.Gpu.FallbackToCpu = false; });
            Environment.SetEnvironmentVariable("FAKE_FAIL_HWDEC", "1");
            string src = Put(Pgm);
            conv.Start();
            Assert.True(WaitDone());
            Environment.SetEnvironmentVariable("FAKE_FAIL_HWDEC", null);
            List<string[]> calls = EncodeCalls();
            Assert.Eq(2, calls.Count);
            Assert.In("h264_qsv", calls[1]);
            Assert.False(Array.IndexOf(calls[1], "-hwaccel") >= 0);
            Assert.False(File.Exists(src));
            Assert.Contains(History("done")[0].Backend, "software decoding");
        }

        [Fact]
        public void Failure_details_include_every_attempt_and_the_command()
        {
            string name = "MultiCorder7 - Cam1 - 23 July 2026 - FAILME 08-29-03 AM.mp4";
            Put(name);
            Set<VerticalCfg>("vertical", delegate(VerticalCfg v) { v.MaxRetries = 0; });
            conv.Start();
            Assert.True(WaitFailed());
            HistoryItem h = History("failed")[0];
            Assert.Contains(h.Detail, "Attempt 1");
            Assert.Contains(h.Detail, "Attempt 2");
            Assert.Contains(h.Detail, "Command:");
            Assert.Contains(h.Detail, "Conversion failed!");
        }

        [Fact]
        public void Gpu_failure_without_fallback_fails()
        {
            Set<VerticalCfg>("vertical", delegate(VerticalCfg v) { v.Gpu.FallbackToCpu = false; v.MaxRetries = 0; });
            Environment.SetEnvironmentVariable("FAKE_FAIL_ENCODER", "h264_qsv");
            string src = Put(Pgm);
            conv.Start();
            Assert.True(WaitFailed());
            Assert.True(File.Exists(src));
            Assert.Eq(2, EncodeCalls().Count);
        }

        [Fact]
        public void Verification_failure_keeps_source()
        {
            string name = "MultiCorder7 - Cam1 - 23 July 2026 - SHORTOUT 08-29-03 AM.mp4";
            string src = Put(name, 100);
            Set<VerticalCfg>("vertical", delegate(VerticalCfg v) { v.MaxRetries = 0; });
            conv.Start();
            Assert.True(WaitFailed());
            Assert.True(File.Exists(src));
            Assert.Contains(St().Failures[0].Error, "verification failed");
            Assert.Eq(0, OutMp4().Length);
        }

        [Fact]
        public void Existing_output_is_skipped()
        {
            Directory.CreateDirectory(outDir);
            string existing = Path.Combine(outDir, PgmOut);
            File.WriteAllText(existing, "already here");
            string src = Put(Pgm);
            conv.Start();
            Assert.True(WaitRow(Pgm, "exists"));
            Sleep(1.2);
            Assert.True(File.Exists(src));
            Assert.Eq(0, EncodeCalls().Count);
            Assert.Eq("already here", File.ReadAllText(existing));
        }

        [Fact]
        public void Keep_source_option()
        {
            Set<VerticalCfg>("vertical", delegate(VerticalCfg v) { v.AfterSuccess = "keep"; });
            string src = Put(Pgm);
            conv.Start();
            Assert.True(WaitDone());
            Assert.True(File.Exists(src));
            Assert.True(WaitRow(Pgm, "exists"));
            Sleep(1.2);
            Assert.Eq(1, EncodeCalls().Count);    // not converted again
        }

        [Fact]
        public void Converted_file_keeps_the_original_date_modified()
        {
            string src = Put(Pgm);
            DateTime when = new DateTime(2026, 7, 23, 8, 39, 3, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(src, when);
            conv.Start();
            Assert.True(WaitDone());
            Assert.False(File.Exists(src));
            Assert.Eq(when, File.GetLastWriteTimeUtc(Path.Combine(outDir, PgmOut)));
        }

        [Fact]
        public void Move_source_option_moves_the_original_off_the_ingest_folder()
        {
            string originals = Path.Combine(outDir, "Originals");
            Set<VerticalCfg>("vertical", delegate(VerticalCfg v) { v.AfterSuccess = "move"; v.MoveOriginalTo = originals; });
            Directory.CreateDirectory(originals);
            File.WriteAllText(Path.Combine(originals, Pgm), "older original with the same name");
            string src = Put(Pgm);
            DateTime when = new DateTime(2026, 7, 23, 8, 39, 3, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(src, when);
            FileOps.ForceCrossVolume = true;
            try
            {
                conv.Start();
                Assert.True(WaitDone());
            }
            finally { FileOps.ForceCrossVolume = false; }
            Assert.False(File.Exists(src));
            string moved = Path.Combine(originals, Path.GetFileNameWithoutExtension(Pgm) + " (1)" + Path.GetExtension(Pgm));
            Assert.True(File.Exists(moved), "moved next to the existing one");
            Assert.Eq(when, File.GetLastWriteTimeUtc(moved));
            Assert.Eq(when, File.GetLastWriteTimeUtc(Path.Combine(outDir, PgmOut)));
            Assert.Eq("older original with the same name", File.ReadAllText(Path.Combine(originals, Pgm)));
            Assert.True(File.Exists(Path.Combine(outDir, PgmOut)));
            HistoryItem h = History("done")[0];
            Assert.True(h.SourceDeleted);
            Assert.Contains(h.Detail, "Original moved to");
            Sleep(1.2);
            Assert.Eq(1, EncodeCalls().Count);
        }

        [Fact]
        public void Dry_run_logs_command_and_converts_nothing()
        {
            Set<GeneralCfg>("general", delegate(GeneralCfg g) { g.DryRun = true; });
            string src = Put(Pgm);
            conv.Start();
            Assert.True(WaitRow(Pgm, "dryrun"));
            Sleep(1.2);
            Assert.True(File.Exists(src));
            Assert.Eq(0, EncodeCalls().Count);
            StringBuilder sb = new StringBuilder();
            foreach (LogEntry e in Hub.Since(0, null, null, 0)) sb.AppendLine(e.Msg);
            string text = sb.ToString();
            Assert.Contains(text, "[dry-run] Would convert");
            Assert.Contains(text, "vpp_qsv=transpose=2");
            Assert.Eq(1, text.Split(new[] { "Would convert" }, StringSplitOptions.None).Length - 1);    // logged once, not every poll
        }

        [Fact]
        public void Abort_keeps_source_and_cleans_partial()
        {
            Environment.SetEnvironmentVariable("FAKE_DELAY", "6");
            string src = Put(Pgm, 1920, 1080, 60);
            conv.Start();
            Assert.True(WaitFor(delegate { CurrentJob c = St().Current; return c != null && c.Percent > 0; }));
            Assert.Eq("encoding", St().Current.Stage);
            conv.AbortCurrent();
            Assert.True(WaitDone(1, "aborted", 10));
            Assert.True(File.Exists(src));
            Assert.Eq(0, OutMp4().Length);
            Assert.Eq(0, St().Failures.Count);    // an abort is not a failure
        }

        [Fact]
        public void Progress_is_reported()
        {
            Environment.SetEnvironmentVariable("FAKE_DELAY", "1.5");
            Put(Pgm, 1920, 1080, 60);
            conv.Start();
            CurrentJob seen = null;
            WaitFor(delegate
            {
                CurrentJob c = St().Current;
                if (c != null) seen = c;
                return c != null && c.Percent >= 40;
            }, 10);
            Assert.NotNull(seen);
            Assert.True(seen.Percent >= 40, "percent was " + seen.Percent);
            Assert.Eq("2.0x", seen.Speed);
            Assert.Contains(seen.Backend, "Intel");
            Assert.True(seen.EtaSeconds >= 0);
        }

        [Fact]
        public void Wait_stable_waits_for_copy_to_finish()
        {
            string p = Path.Combine(ingest, "growing.mp4");
            File.WriteAllText(p, "a");
            conv.StableWaitMs = 300;
            Thread t = new Thread(delegate()
            {
                for (int i = 0; i < 5; i++)
                {
                    Thread.Sleep(150);
                    File.AppendAllText(p, "more");
                }
            });
            t.Start();
            DateTime t0 = DateTime.Now;
            Assert.Eq("ok", conv.WaitStable(p));
            t.Join();
            Assert.True((DateTime.Now - t0).TotalSeconds >= 0.75);
            string empty = Path.Combine(ingest, "empty.mp4");
            File.WriteAllText(empty, "");
            Assert.Eq("empty", conv.WaitStable(empty));
            Assert.Eq("gone", conv.WaitStable(Path.Combine(ingest, "gone.mp4")));
        }

        [Fact]
        public void Leftover_partial_files_are_cleaned_on_start()
        {
            Directory.CreateDirectory(outDir);
            string junk = Path.Combine(outDir, "Vertical PGM - x.partial.mp4");
            string keep = Path.Combine(outDir, "keep.mp4");
            File.WriteAllText(junk, "");
            File.WriteAllText(keep, "");
            conv.Start();
            Assert.True(WaitFor(delegate { return !File.Exists(junk); }, 5));
            Assert.True(File.Exists(keep));
        }

        [Fact]
        public void Stop_and_restart()
        {
            conv.Start();
            Assert.True(WaitFor(delegate { return St().Running; }, 5));
            conv.Stop();
            Assert.True(WaitFor(delegate { return !conv.Alive; }, 5));
            Assert.False(St().Running);
            conv.Start();
            Assert.True(WaitFor(delegate { return St().Running; }, 5));
        }

        [Fact]
        public void Start_cancels_a_pending_stop_during_an_encode()
        {
            Environment.SetEnvironmentVariable("FAKE_DELAY", "1.5");
            Put(Pgm, 1920, 1080, 30);
            conv.Start();
            Assert.True(WaitFor(delegate { CurrentJob c = St().Current; return c != null && c.Stage == "encoding"; }, 10));
            conv.Stop();
            Assert.True(St().Stopping);
            conv.Start();    // user changed their mind
            Assert.True(WaitDone());
            Environment.SetEnvironmentVariable("FAKE_DELAY", "0.1");
            Put(Split);
            Assert.True(WaitDone(2));    // watcher kept running
        }

        [Fact]
        public void Stop_lets_current_file_finish()
        {
            Environment.SetEnvironmentVariable("FAKE_DELAY", "1.0");
            string src = Put(Pgm);
            conv.Start();
            Assert.True(WaitFor(delegate { CurrentJob c = St().Current; return c != null && c.Stage == "encoding"; }, 10));
            conv.Stop();
            Assert.True(WaitDone());
            Assert.True(WaitFor(delegate { return !conv.Alive; }, 5));
            Assert.False(File.Exists(src));
        }

        [Fact]
        public void Protected_ingest_folder_is_refused()
        {
            AppConfig cfg = Cfgs.Get();
            cfg.Vertical.WatchDir = @"C:\Windows";
            Assert.Eq(0, conv.Scan(cfg).Count);
        }

        [Fact]
        public void Missing_ingest_folder_does_not_crash_watcher()
        {
            Set<VerticalCfg>("vertical", delegate(VerticalCfg v) { v.WatchDir = D("nope"); });
            conv.Start();
            Sleep(1.2);
            Assert.True(conv.Alive);
        }

        [Fact]
        public void Missing_ffprobe_is_a_clean_failure()
        {
            Set<GeneralCfg>("general", delegate(GeneralCfg g) { g.FfprobePath = D("no-such-ffprobe"); });
            Set<VerticalCfg>("vertical", delegate(VerticalCfg v) { v.MaxRetries = 0; });
            string src = Put(Pgm);
            conv.Start();
            Assert.True(WaitFailed());
            Assert.True(File.Exists(src));
            Assert.Contains(St().Failures[0].Error, "cannot run ffprobe");
        }

        [Fact]
        public void Unreadable_media_is_a_clean_failure()
        {
            string p = Path.Combine(ingest, Pgm);
            File.WriteAllText(p, "this is not a video");
            Age(p, 30);
            Set<VerticalCfg>("vertical", delegate(VerticalCfg v) { v.MaxRetries = 0; });
            conv.Start();
            Assert.True(WaitFailed());
            Assert.True(File.Exists(p));
            Assert.Contains(St().Failures[0].Error, "ffprobe failed");
            Assert.Eq(0, EncodeCalls().Count);
        }

        [Fact]
        public void Missing_ffmpeg_is_a_clean_failure()
        {
            Set<GeneralCfg>("general", delegate(GeneralCfg g) { g.FfmpegPath = D("no-such-ffmpeg"); });
            Set<VerticalCfg>("vertical", delegate(VerticalCfg v) { v.MaxRetries = 0; });
            string src = Put(Pgm);
            conv.Start();
            Assert.True(WaitFailed());
            Assert.True(File.Exists(src));
            Assert.Contains(St().Failures[0].Error, "cannot start ffmpeg");
        }

        [Fact]
        public void Resolution_and_bitrate_settings_reach_ffmpeg()
        {
            Set<VerticalCfg>("vertical", delegate(VerticalCfg v)
            {
                v.Video.ResolutionMode = "custom"; v.Video.Width = 720; v.Video.Height = 1280;
                v.Video.RateMode = "fixed"; v.Video.FixedMbps = 4; v.Video.Fps = 30;
            });
            Put(Pgm, 1920, 1080, 600);
            conv.Start();
            Assert.True(WaitDone());
            string[] call = EncodeCalls()[0];
            Assert.Contains(ArgAfter(call, "-vf"), "scale_qsv=w=720:h=1280");
            Assert.Eq("4M", ArgAfter(call, "-b:v"));
            Assert.Eq("30", ArgAfter(call, "-r"));
            JObject meta = ReadOut(PgmOut);
            Assert.Eq(720.0, (double)meta.Get("width"));
            Assert.Eq(1280.0, (double)meta.Get("height"));
        }

        [Fact]
        public void Oldest_file_is_converted_first()
        {
            string a = Put(Pgm);
            string b = Put("MultiCorder7 - Cam1 - 23 July 2026 - 09-00-00 AM.mp4");
            Age(a, 100);
            Age(b, 50);
            conv.Start();
            Assert.True(WaitDone(2));
            List<string[]> calls = EncodeCalls();
            Assert.Contains(calls[0][Array.IndexOf(calls[0], "-i") + 1], "08-29-03");
        }

        [Fact]
        public void Cpu_backend_is_used_when_chosen()
        {
            Set<VerticalCfg>("vertical", delegate(VerticalCfg v) { v.Gpu.Backend = "cpu"; });
            Put(Pgm);
            conv.Start();
            Assert.True(WaitDone());
            Assert.In("libx264", EncodeCalls()[0]);
            Assert.Contains(History("done")[0].Backend, "CPU");
        }
    }

    public class EncoderTestTests : Env
    {
        [Fact]
        public void Encoder_test_passes_with_working_ffmpeg()
        {
            AppConfig cfg = Cfgs.Get();
            EncoderTestResult res = Converter.RunEncoderTest(cfg.General, cfg.Vertical, new List<Adapter>(), 60000);
            Assert.True(res.Ok, res.Error);
            Assert.Eq("1080x1920 h264", res.Output);
            Assert.Contains(res.Command, "vpp_qsv=transpose=2");
        }

        [Fact]
        public void Encoder_test_reports_gpu_failure()
        {
            Environment.SetEnvironmentVariable("FAKE_FAIL_ENCODER", "h264_qsv");
            AppConfig cfg = Cfgs.Get();
            EncoderTestResult res = Converter.RunEncoderTest(cfg.General, cfg.Vertical, new List<Adapter>(), 60000);
            Assert.False(res.Ok);
            Assert.Contains(res.Error, "Error initializing encoder");
        }

        [Fact]
        public void Encoder_test_with_missing_ffmpeg()
        {
            AppConfig cfg = Cfgs.Get();
            cfg.General.FfmpegPath = D("nope");
            EncoderTestResult res = Converter.RunEncoderTest(cfg.General, cfg.Vertical, new List<Adapter>(), 60000);
            Assert.False(res.Ok);
            Assert.Contains(res.Error, "cannot run ffmpeg");
        }

        [Fact]
        public void Preview_command()
        {
            AppConfig cfg = Cfgs.Get();
            CommandPreview res = Converter.Preview(cfg.General, cfg.Vertical, new List<Adapter>());
            Assert.True(res.Ok);
            Assert.Contains(res.Command, "h264_qsv");
            Assert.Eq("intel_qsv", res.Backend);
        }

        [Fact]
        public void Encoder_listing_and_version()
        {
            Environment.SetEnvironmentVariable("FAKE_ENCODERS", "h264_qsv,libx264");
            try
            {
                EncoderList l = Gpu.ListEncoders(Ffmpeg);
                Assert.True(l.Ok);
                Assert.Eq(new[] { "h264_qsv", "libx264" }, l.Encoders.ToArray());
            }
            finally { Environment.SetEnvironmentVariable("FAKE_ENCODERS", null); }
            Assert.Contains(Gpu.FfmpegVersion(Ffmpeg), "fake-1.0");
            Assert.False(Gpu.ListEncoders(D("nope")).Ok);
            Assert.Eq("", Gpu.FfmpegVersion(D("nope")));
        }

        [Fact]
        public void Probe_reads_stream_and_container_bitrate()
        {
            string p = D("clip.mp4");
            WriteMedia(p, 1920, 1080, 12.5, 4000000, "hevc", null, null);
            ProbeInfo i = Media.Probe(Ffprobe, p);
            Assert.Eq("hevc", i.Codec);
            Assert.Eq(1920, i.Width);
            Assert.Eq(1080, i.Height);
            Assert.Eq(4000000.0, i.BitRate);
            Assert.Eq(30.0, i.Fps);
            Assert.Eq(12.5, i.Duration);
            WriteMedia(p, 1920, 1080, 12.5, 4000000, "h264", "stream_bitrate_missing", true);
            Assert.Eq(4000000.0, Media.Probe(Ffprobe, p).BitRate);    // falls back to the container
            WriteMedia(p, 1920, 1080, 12.5, 4000000, "h264", "no_bitrate", true);
            Assert.Eq(0.0, Media.Probe(Ffprobe, p).BitRate);
            Assert.Eq(12.5, Media.ProbeDuration(Ffprobe, p));
            Assert.Throws<ProbeError>(delegate { Media.Probe(Ffprobe, D("nope.mp4")); });
            Assert.Eq(0.0, Media.ProbeDuration(D("no-ffprobe"), p));
        }
    }

    public class LogTests
    {
        [Fact]
        public void Ring_buffer_filter_and_file_rotation()
        {
            string dir = Path.Combine(Path.GetTempPath(), "logtest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                LogHub hub = new LogHub(5);
                hub.AttachFile(Path.Combine(dir, "x.log"), 200, 2);
                SourceLogger a = hub.Logger("a"), b = hub.Logger("b");
                for (int i = 0; i < 8; i++) a.Info("line " + i);
                b.Warn("warned");
                b.Debug("dbg");
                b.Error("bad");
                List<LogEntry> all = hub.Since(0, null, null, 0);
                Assert.Eq(5, all.Count);
                Assert.Eq("bad", all[4].Msg);
                Assert.Eq(2, hub.Since(0, "b", "WARN", 0).Count);
                Assert.Eq(1, hub.Since(0, null, "ERROR", 0).Count);
                Assert.Eq(11L, hub.Head());
                Assert.Eq(1, hub.Since(10, null, null, 0).Count);
                string listing = string.Join(",", Directory.GetFiles(dir));
                Assert.True(File.Exists(Path.Combine(dir, "x.log")), "files: " + listing);
                Assert.True(File.Exists(Path.Combine(dir, "x.log.1")), "files: " + listing);
                Assert.False(File.Exists(Path.Combine(dir, "x.log.3")));
            }
            finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
        }

        [Fact]
        public void Capture_skips_debug_and_is_limited()
        {
            LogHub hub = new LogHub();
            List<string> lines = new List<string>();
            SourceLogger log = hub.Logger("t").WithCapture(lines);
            log.Debug("hidden");
            log.Info("shown");
            for (int i = 0; i < 600; i++) log.Info("x");
            Assert.Eq("shown", lines[0]);
            Assert.Eq(SourceLogger.CaptureLimit, lines.Count);
        }

        [Fact]
        public void Added_event_fires()
        {
            LogHub hub = new LogHub();
            List<string> seen = new List<string>();
            hub.Added += delegate(LogEntry e) { seen.Add(e.Msg); };
            hub.Logger("t").Info("one");
            Assert.Eq(new[] { "one" }, seen.ToArray());
        }
    }
}
