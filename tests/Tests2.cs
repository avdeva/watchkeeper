using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Watchkeeper;

namespace Watchkeeper.Tests
{
    public class BuilderTests
    {
        const string FF = @"C:\Program Files\ffmpeg\bin\ffmpeg.exe";

        // The reference command for an 8.53 Mbps source with default settings.
        static readonly string[] ReferenceArgs = {
            FF, "-y", "-init_hw_device", "qsv=hw", "-filter_hw_device", "hw", "-hwaccel", "qsv",
            "-c:v", "h264_qsv", "-protocol_whitelist", "file", "-f", "mov", "-i", "IN.mp4", "-vf", "vpp_qsv=transpose=2", "-c:v", "h264_qsv",
            "-b:v", "8.53M", "-maxrate", "8.53M", "-bufsize", "17060000", "-look_ahead", "0",
            "-profile:v", "high", "-c:a", "copy", "OUT.mp4" };

        static ProbeInfo Info()
        {
            ProbeInfo i = new ProbeInfo();
            i.Codec = "h264"; i.Width = 1920; i.Height = 1080; i.BitRate = 8530000; i.Fps = 30; i.Duration = 60;
            return i;
        }

        static VerticalCfg V(Action<VerticalCfg> change)
        {
            VerticalCfg v = new VerticalCfg();
            if (change != null) change(v);
            return v;
        }

        static BuildResult Build(VerticalCfg v, ProbeInfo info, List<Adapter> adapters, bool progress)
        {
            Resolved r = FfmpegBuilder.ResolveGpu(v.Gpu, adapters ?? new List<Adapter>());
            return FfmpegBuilder.Build(FF, v, info, "IN.mp4", "OUT.mp4", r, progress);
        }

        static BuildResult Build(VerticalCfg v) { return Build(v, Info(), null, false); }
        static string After(BuildResult b, string flag) { return Env.ArgAfter(b.Args, flag); }

        static Adapter Ad(int index, string name, string vendor, string kind, string backend)
        {
            Adapter a = new Adapter();
            a.Index = index; a.Name = name; a.Vendor = vendor; a.Kind = kind; a.Backend = backend;
            return a;
        }

        static readonly Adapter IntelIgpu = Ad(0, "Intel(R) UHD Graphics 770", "intel", "integrated", "intel_qsv");
        static readonly Adapter Nvidia = Ad(1, "NVIDIA GeForce RTX 3060", "nvidia", "discrete", "nvidia_nvenc");
        static readonly Adapter IntelArc = Ad(2, "Intel(R) Arc(TM) A380 Graphics", "intel", "discrete", "intel_qsv");

        [Fact]
        public void Default_command_matches_the_reference_command()
        {
            BuildResult b = Build(V(null));
            Assert.Eq(ReferenceArgs, b.Args.ToArray());
            Assert.Eq(0, b.Notes.Count);
        }

        [Fact]
        public void Progress_flags_are_added_for_the_watcher()
        {
            BuildResult b = Build(V(null), Info(), null, true);
            Assert.Eq(new[] { FF, "-y", "-hide_banner", "-loglevel", "warning", "-nostats", "-progress", "pipe:1" }, b.Args.GetRange(0, 8).ToArray());
        }

        [Fact]
        public void Clockwise_rotation()
        {
            Assert.Eq("vpp_qsv=transpose=1", After(Build(V(delegate(VerticalCfg v) { v.Video.Rotation = "clock"; })), "-vf"));
        }

        [Fact]
        public void Fixed_bitrate()
        {
            BuildResult b = Build(V(delegate(VerticalCfg v) { v.Video.RateMode = "fixed"; v.Video.FixedMbps = 6; }));
            Assert.Eq("6M", After(b, "-b:v"));
            Assert.Eq("6M", After(b, "-maxrate"));
            Assert.Eq("12000000", After(b, "-bufsize"));
        }

        [Fact]
        public void Percent_of_source_bitrate()
        {
            BuildResult b = Build(V(delegate(VerticalCfg v) { v.Video.RateMode = "percent"; v.Video.Percent = 50; }));
            Assert.Eq("4.26M", After(b, "-b:v"));    // 8.53 * 0.5 = 4.265, rounds half to even
            Assert.Eq("8530000", After(b, "-bufsize"));
        }

        [Fact]
        public void Bufsize_is_capped()
        {
            BuildResult b = Build(V(delegate(VerticalCfg v) { v.Video.RateMode = "fixed"; v.Video.FixedMbps = 500; }));
            Assert.Eq("1000000000", After(b, "-bufsize"));
            ProbeInfo info = Info();
            info.BitRate = 2000000000;
            Assert.Eq("2000000000", After(Build(V(null), info, null, false), "-bufsize"));
        }

        [Fact]
        public void Missing_source_bitrate_uses_fallback_and_says_so()
        {
            ProbeInfo info = Info();
            info.BitRate = 0;
            BuildResult b = Build(V(delegate(VerticalCfg v) { v.Video.FallbackMbps = 5; }), info, null, false);
            Assert.Eq("5M", After(b, "-b:v"));
            Assert.True(b.Notes.Exists(delegate(string n) { return n.Contains("fallback"); }));
        }

        [Fact]
        public void Quality_mode_per_backend()
        {
            Action<VerticalCfg, string> q = delegate(VerticalCfg v, string backend)
            {
                v.Video.RateMode = "quality"; v.Video.Quality = 21; v.Gpu.Backend = backend;
            };
            BuildResult a = Build(V(delegate(VerticalCfg v) { q(v, "intel_qsv"); }));
            Assert.Eq("21", After(a, "-global_quality"));
            Assert.NotIn("-b:v", a.Args);
            a = Build(V(delegate(VerticalCfg v) { q(v, "nvidia_nvenc"); }));
            Assert.Eq(new[] { "vbr", "21", "0" }, new[] { After(a, "-rc"), After(a, "-cq"), After(a, "-b:v") });
            a = Build(V(delegate(VerticalCfg v) { q(v, "amd_amf"); }));
            Assert.Eq(new[] { "cqp", "21", "21" }, new[] { After(a, "-rc"), After(a, "-qp_i"), After(a, "-qp_p") });
            a = Build(V(delegate(VerticalCfg v) { q(v, "cpu"); }));
            Assert.Eq("21", After(a, "-crf"));
        }

        [Fact]
        public void Nvidia_pipeline()
        {
            BuildResult a = Build(V(delegate(VerticalCfg v) { v.Gpu.Backend = "nvidia_nvenc"; }));
            Assert.Eq("cuda", After(a, "-hwaccel"));
            Assert.Eq("transpose=2", After(a, "-vf"));
            Assert.Eq("h264_nvenc", After(a, "-c:v"));
            Assert.NotIn("-gpu", a.Args);
            Assert.NotIn("-look_ahead", a.Args);
        }

        [Fact]
        public void Amd_pipeline()
        {
            BuildResult a = Build(V(delegate(VerticalCfg v) { v.Gpu.Backend = "amd_amf"; }));
            Assert.Eq("d3d11va", After(a, "-hwaccel"));
            Assert.In("h264_amf", a.Args);
        }

        [Fact]
        public void Cpu_pipeline_has_no_hardware_flags()
        {
            BuildResult a = Build(V(delegate(VerticalCfg v) { v.Gpu.Backend = "cpu"; v.Gpu.CpuThreads = 4; }));
            foreach (string flag in new[] { "-hwaccel", "-init_hw_device", "-look_ahead", "-gpu" }) Assert.NotIn(flag, a.Args);
            Assert.Eq("libx264", After(a, "-c:v"));
            Assert.Eq("4", After(a, "-threads"));
            Assert.Eq("yuv420p", After(a, "-pix_fmt"));
        }

        [Fact]
        public void Hevc()
        {
            BuildResult a = Build(V(delegate(VerticalCfg v) { v.Video.Codec = "hevc"; }));
            List<string> cv = new List<string>();
            for (int i = 0; i < a.Args.Count - 1; i++) if (a.Args[i] == "-c:v") cv.Add(a.Args[i + 1]);
            Assert.Eq(new[] { "h264_qsv", "hevc_qsv" }, cv.ToArray());    // h264 source decoder, hevc encoder
            Assert.Eq("main", After(a, "-profile:v"));
            Assert.NotIn("-look_ahead", a.Args);    // hevc_qsv rejects it
            a = Build(V(delegate(VerticalCfg v) { v.Gpu.Backend = "intel_qsv"; }));
            Assert.Eq("0", After(a, "-look_ahead"));    // h264_qsv keeps it
            a = Build(V(delegate(VerticalCfg v) { v.Video.Codec = "hevc"; v.Gpu.Backend = "cpu"; }));
            Assert.Eq("libx265", After(a, "-c:v"));
        }

        [Fact]
        public void Presets()
        {
            Assert.NotIn("-preset", Build(V(null)).Args);
            Assert.Eq("veryfast", After(Build(V(delegate(VerticalCfg v) { v.Video.Preset = "fast"; })), "-preset"));
            Assert.Eq("slow", After(Build(V(delegate(VerticalCfg v) { v.Video.Preset = "quality"; })), "-preset"));
            Assert.Eq("p2", After(Build(V(delegate(VerticalCfg v) { v.Video.Preset = "fast"; v.Gpu.Backend = "nvidia_nvenc"; })), "-preset"));
            Assert.Eq("p6", After(Build(V(delegate(VerticalCfg v) { v.Video.Preset = "quality"; v.Gpu.Backend = "nvidia_nvenc"; })), "-preset"));
            Assert.Eq("quality", After(Build(V(delegate(VerticalCfg v) { v.Video.Preset = "quality"; v.Gpu.Backend = "amd_amf"; })), "-quality"));
        }

        [Fact]
        public void Profile()
        {
            Assert.Eq("main", After(Build(V(delegate(VerticalCfg v) { v.Video.Profile = "main"; })), "-profile:v"));
        }

        [Fact]
        public void Fps_audio_and_extra_args()
        {
            BuildResult a = Build(V(delegate(VerticalCfg v)
            {
                v.Video.Fps = 29.97;
                v.Video.AudioMode = "aac";
                v.Video.AudioKbps = 128;
                v.Video.ExtraInputArgs = "-thread_queue_size 512";
                v.Video.ExtraOutputArgs = "-movflags +faststart -metadata title=\"a b\"";
            }));
            Assert.Eq("29.97", After(a, "-r"));
            Assert.Eq("aac", After(a, "-c:a"));
            Assert.Eq("128k", After(a, "-b:a"));
            Assert.True(a.Args.IndexOf("-thread_queue_size") < a.Args.IndexOf("-i"));
            Assert.True(a.Args.IndexOf("-movflags") > a.Args.IndexOf("-i"));
            Assert.Eq("title=a b", After(a, "-metadata"));
            Assert.Eq("OUT.mp4", a.Args[a.Args.Count - 1]);
            a = Build(V(delegate(VerticalCfg v) { v.Video.AudioMode = "none"; }));
            Assert.In("-an", a.Args);
            Assert.NotIn("-c:a", a.Args);
        }

        [Fact]
        public void Resolution_scaling()
        {
            BuildResult a = Build(V(delegate(VerticalCfg v) { v.Video.ResolutionMode = "custom"; v.Video.Width = 720; v.Video.Height = 1280; }));
            Assert.Eq("vpp_qsv=transpose=2,scale_qsv=w=720:h=1280", After(a, "-vf"));
            a = Build(V(delegate(VerticalCfg v) { v.Video.ResolutionMode = "custom"; v.Video.Width = 720; v.Video.Height = 1280; v.Gpu.Backend = "cpu"; }));
            Assert.Eq("transpose=2,scale=720:1280", After(a, "-vf"));
        }

        [Fact]
        public void One_dimension_keeps_aspect_ratio()
        {
            VideoCfg v = V(delegate(VerticalCfg x) { x.Video.ResolutionMode = "custom"; x.Video.Width = 540; x.Video.Height = 0; }).Video;
            Assert.Eq(new[] { 540, 960 }, FfmpegBuilder.OutputSize(Info(), v));
            v = V(delegate(VerticalCfg x) { x.Video.ResolutionMode = "custom"; x.Video.Width = 0; x.Video.Height = 1280; }).Video;
            Assert.Eq(new[] { 720, 1280 }, FfmpegBuilder.OutputSize(Info(), v));
        }

        [Fact]
        public void No_scaling_when_target_equals_rotated_source()
        {
            VideoCfg v = V(delegate(VerticalCfg x) { x.Video.ResolutionMode = "custom"; x.Video.Width = 1080; x.Video.Height = 1920; }).Video;
            Assert.Null(FfmpegBuilder.OutputSize(Info(), v));
            Assert.Eq(new[] { 1080, 1920 }, FfmpegBuilder.ExpectedSize(Info(), v));
        }

        [Fact]
        public void Expected_size_is_rotated_source_by_default()
        {
            Assert.Eq(new[] { 1080, 1920 }, FfmpegBuilder.ExpectedSize(Info(), V(null).Video));
            ProbeInfo i = Info();
            i.Width = 1280; i.Height = 720;
            Assert.Eq(new[] { 720, 1280 }, FfmpegBuilder.ExpectedSize(i, V(null).Video));
        }

        [Fact]
        public void Unknown_source_size_with_one_dimension_is_an_error()
        {
            VideoCfg v = V(delegate(VerticalCfg x) { x.Video.ResolutionMode = "custom"; x.Video.Width = 540; x.Video.Height = 0; }).Video;
            Assert.Throws<BuildError>(delegate { FfmpegBuilder.OutputSize(new ProbeInfo(), v); });
        }

        [Fact]
        public void Qsv_without_hw_decoder_uploads_frames()
        {
            ProbeInfo i = Info();
            i.Codec = "mjpeg";
            BuildResult a = Build(V(null), i, null, false);
            Assert.NotIn("-hwaccel", a.Args);
            Assert.Eq("format=nv12,hwupload=extra_hw_frames=64,vpp_qsv=transpose=2", After(a, "-vf"));
            Assert.True(a.Notes.Count > 0);
        }

        [Fact]
        public void Hw_decode_off()
        {
            BuildResult a = Build(V(delegate(VerticalCfg v) { v.Gpu.HwDecode = false; }));
            Assert.NotIn("-hwaccel", a.Args);
            Assert.Contains(After(a, "-vf"), "hwupload=extra_hw_frames=64");
            a = Build(V(delegate(VerticalCfg v) { v.Gpu.HwDecode = false; v.Gpu.Backend = "nvidia_nvenc"; }));
            Assert.NotIn("-hwaccel", a.Args);
        }

        [Fact]
        public void Qsv_decoder_follows_source_codec()
        {
            ProbeInfo i = Info();
            i.Codec = "hevc";
            Assert.Eq("hevc_qsv", After(Build(V(null), i, null, false), "-c:v"));
        }

        // ----------------------------------------------------------- GPU selection
        [Fact]
        public void Single_intel_adapter_leaves_device_selection_to_the_driver()
        {
            BuildResult a = Build(V(delegate(VerticalCfg v) { v.Gpu.AdapterName = IntelIgpu.Name; }), Info(), new List<Adapter> { IntelIgpu, Nvidia }, false);
            Assert.Eq("qsv=hw", After(a, "-init_hw_device"));
        }

        [Fact]
        public void Two_qsv_adapters_pins_the_chosen_one()
        {
            List<Adapter> all = new List<Adapter> { IntelIgpu, Nvidia, IntelArc };
            BuildResult a = Build(V(delegate(VerticalCfg v) { v.Gpu.AdapterName = IntelArc.Name; }), Info(), all, false);
            Assert.Eq("qsv=hw,child_device=2,child_device_type=d3d11va", After(a, "-init_hw_device"));
            a = Build(V(delegate(VerticalCfg v) { v.Gpu.AdapterName = IntelIgpu.Name; }), Info(), all, false);
            Assert.Eq("qsv=hw,child_device=0,child_device_type=d3d11va", After(a, "-init_hw_device"));
        }

        [Fact]
        public void Manual_index_override()
        {
            List<Adapter> two = new List<Adapter> { IntelIgpu, Nvidia };
            BuildResult a = Build(V(delegate(VerticalCfg v) { v.Gpu.AdapterIndex = 1; }), Info(), two, false);
            Assert.Contains(After(a, "-init_hw_device"), "child_device=1");
            a = Build(V(delegate(VerticalCfg v) { v.Gpu.Backend = "nvidia_nvenc"; v.Gpu.AdapterIndex = 1; }), Info(), two, false);
            Assert.Eq("1", After(a, "-gpu"));
            Assert.Eq("1", After(a, "-hwaccel_device"));
        }

        [Fact]
        public void Second_nvidia_card()
        {
            Adapter n2 = Ad(2, "NVIDIA GeForce GTX 1050", "nvidia", "discrete", "nvidia_nvenc");
            BuildResult a = Build(V(delegate(VerticalCfg v) { v.Gpu.Backend = "nvidia_nvenc"; v.Gpu.AdapterName = n2.Name; }),
                                  Info(), new List<Adapter> { IntelIgpu, Nvidia, n2 }, false);
            Assert.Eq("1", After(a, "-gpu"));    // second NVIDIA card
        }

        [Fact]
        public void Auto_prefers_intel_then_nvidia_then_cpu()
        {
            GpuCfg g = new GpuCfg();
            g.Backend = "auto";
            Assert.Eq("intel_qsv", FfmpegBuilder.ResolveGpu(g, new List<Adapter> { Nvidia, IntelIgpu }).Backend);
            Assert.Eq("nvidia_nvenc", FfmpegBuilder.ResolveGpu(g, new List<Adapter> { Nvidia }).Backend);
            Assert.Eq("cpu", FfmpegBuilder.ResolveGpu(g, new List<Adapter>()).Backend);
        }

        [Fact]
        public void Classification()
        {
            string[][] cases = {
                new[] { "Intel(R) UHD Graphics 770", "intel", "integrated" },
                new[] { "Intel(R) Iris(R) Xe Graphics", "intel", "integrated" },
                new[] { "Intel(R) Arc(TM) Graphics", "intel", "integrated" },
                new[] { "Intel(R) Arc(TM) A380 Graphics", "intel", "discrete" },
                new[] { "NVIDIA GeForce RTX 3060", "nvidia", "discrete" },
                new[] { "NVIDIA Quadro P2000", "nvidia", "discrete" },
                new[] { "AMD Radeon RX 6600", "amd", "discrete" },
                new[] { "AMD Radeon(TM) Graphics", "amd", "integrated" },
                new[] { "Something Else", "other", "unknown" } };
            foreach (string[] c in cases)
            {
                string vendor, kind;
                Gpu.Classify(c[0], out vendor, out kind);
                Assert.Eq(c[1] + "/" + c[2], vendor + "/" + kind, c[0]);
            }
        }

        [Fact]
        public void Parse_cim_json_filters_virtual_adapters_and_handles_single_object()
        {
            string two = "[{\"Name\":\"Intel(R) UHD Graphics 770\",\"DriverVersion\":\"31.0.101.4146\"}," +
                         "{\"Name\":\"NVIDIA GeForce RTX 3060\",\"DriverVersion\":\"31.0.15.3623\"}," +
                         "{\"Name\":\"Microsoft Basic Display Adapter\",\"DriverVersion\":\"10.0\"}]";
            List<Adapter> got = Gpu.ParseCimJson(two);
            Assert.Eq(new[] { "Intel(R) UHD Graphics 770", "NVIDIA GeForce RTX 3060" }, got.ConvertAll(delegate(Adapter a) { return a.Name; }).ToArray());
            Assert.Eq(new[] { "intel_qsv", "nvidia_nvenc" }, got.ConvertAll(delegate(Adapter a) { return a.Backend; }).ToArray());
            Assert.Eq(new[] { 0, 1 }, got.ConvertAll(delegate(Adapter a) { return a.Index; }).ToArray());
            Assert.Eq(1, Gpu.ParseCimJson("{\"Name\":\"NVIDIA GeForce GTX 1650\"}").Count);
            Assert.Eq(0, Gpu.ParseCimJson("").Count);
        }

        [Fact]
        public void Parse_encoders()
        {
            string text = " V....D h264_qsv            H.264 (Intel)\n V....D libx264 x\n A....D aac AAC\n" +
                          " V....D mpeg4 MPEG-4\n V....D h264_nvenc NVENC";
            Assert.Eq(new[] { "h264_nvenc", "h264_qsv", "libx264" }, Gpu.ParseEncoders(text).ToArray());
        }
    }
}
