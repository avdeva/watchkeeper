using System;
using System.Collections.Generic;
using System.IO;
using Watchkeeper;

namespace Watchkeeper.Tests
{
    public class UtilTests
    {
        [Fact]
        public void Ffmpeg_banner_is_removed_from_error_text()
        {
            string text = "ffmpeg version 6.1.1 Copyright (c) 2000-2023\r\n  built with gcc 13\r\n  configuration: --prefix=/usr\r\n" +
                          "  libavutil      58. 29.100 / 58. 29.100\r\n  libswscale      7.  5.100 /  7.  5.100\r\n" +
                          "Device creation failed: -542398533.\r\nError parsing global options: Generic error\r\n";
            Assert.Eq("Device creation failed: -542398533. | Error parsing global options: Generic error", Converter.MeaningfulTail(text, 6));
            Assert.Eq("Error parsing global options: Generic error", Converter.MeaningfulTail(text, 1));
            Assert.Eq("", Converter.MeaningfulTail("", 6));
        }

        [Fact]
        public void NormPath_handles_windows_and_posix_styles()
        {
            Assert.Eq(@"D:\ISO\a", Util.NormPath("d:/ISO//a/"));
            Assert.Eq(@"D:\", Util.NormPath("\"d:\""));
            Assert.Eq(@"D:\a", Util.NormPath(@"D:\x\..\a"));
            Assert.Eq(@"\\srv\share\x", Util.NormPath(@"\\srv\share\x\"));
            Assert.Eq("/a/c", Util.NormPath("/a/b/../c/"));
            Assert.Eq("", Util.NormPath("  "));
        }

        [Fact]
        public void ProtectedReason_blocks_roots_and_system_folders()
        {
            Assert.NotNull(Util.ProtectedReason(@"C:\"));
            Assert.NotNull(Util.ProtectedReason(@"D:"));
            Assert.NotNull(Util.ProtectedReason(@"C:\Windows"));
            Assert.NotNull(Util.ProtectedReason(@"c:\windows\system32"));
            Assert.NotNull(Util.ProtectedReason(@"C:\Program Files (x86)\x"));
            Assert.NotNull(Util.ProtectedReason(@"C:\Users"));
            Assert.NotNull(Util.ProtectedReason(@"C:\Users\Bob"));
            Assert.NotNull(Util.ProtectedReason(@"\\server\share"));
            Assert.NotNull(Util.ProtectedReason("/"));
            Assert.NotNull(Util.ProtectedReason("/etc/x"));
            Assert.NotNull(Util.ProtectedReason("/home/bob"));
            Assert.NotNull(Util.ProtectedReason(""));
            Assert.Null(Util.ProtectedReason(@"D:\ISO"));
            Assert.Null(Util.ProtectedReason(@"C:\Users\Bob\Videos"));
            Assert.Null(Util.ProtectedReason(@"\\server\share\ISO"));
            Assert.Null(Util.ProtectedReason("/tmp/x/ISO"));
        }

        [Fact]
        public void SplitArgs_groups_quotes_and_keeps_backslashes()
        {
            Assert.Eq(new[] { "-a", "b c", "-m", "title=a b" }, Util.SplitArgs("-a 'b c' -m \"title=a b\"").ToArray());
            Assert.Eq(new[] { @"C:\x\y" }, Util.SplitArgs(@"C:\x\y").ToArray());
            Assert.Eq(0, Util.SplitArgs("   ").Count);
            Assert.Eq(new[] { "" }, Util.SplitArgs("\"\"").ToArray());
            Assert.Throws<FormatException>(delegate { Util.SplitArgs("-x \"open"); });
        }

        [Fact]
        public void QuoteArg_follows_windows_rules()
        {
            Assert.Eq("abc", Util.QuoteArg("abc"));
            Assert.Eq("\"a b\"", Util.QuoteArg("a b"));
            Assert.Eq("\"\"", Util.QuoteArg(""));
            Assert.Eq("\"a\\\"b\"", Util.QuoteArg("a\"b"));
            Assert.Eq("\"C:\\dir with space\\\\\"", Util.QuoteArg(@"C:\dir with space\"));
            Assert.Eq(@"C:\x\y.mp4", Util.QuoteArg(@"C:\x\y.mp4"));
        }

        [Fact]
        public void Time_parsing_and_formatting()
        {
            Assert.Eq(new TimeSpan(5, 45, 0), Util.ParseTime("5:45"));
            Assert.Eq(new TimeSpan(9, 0, 1), Util.ParseTime("09:00:01"));
            Assert.Throws<FormatException>(delegate { Util.ParseTime("24:00"); });
            Assert.Throws<FormatException>(delegate { Util.ParseTime("abc"); });
            Assert.Eq("05:45", Util.FmtHhmm(new TimeSpan(5, 45, 0)));
            Assert.Eq("09:00:01", Util.FmtHhmm(new TimeSpan(9, 0, 1)));
        }

        [Fact]
        public void Formatting_helpers()
        {
            Assert.Eq("0 B", Util.FmtSize(0));
            Assert.Eq("1.5 KB", Util.FmtSize(1536));
            Assert.Eq("2.0 GB", Util.FmtSize(2.0 * 1024 * 1024 * 1024));
            Assert.Eq("1:05", Util.FmtDur(65));
            Assert.Eq("1h 01m", Util.FmtDur(3661));
            Assert.Eq("7", Util.Num(7.0, 2));
            Assert.Eq("0.5", Util.Num(0.5, 2));
            Assert.Eq("29.97", Util.Num(29.97, 3));
        }

        [Fact]
        public void CleanFilenamePart_strips_illegal_characters()
        {
            Assert.Eq("Vertical PGM", Util.CleanFilenamePart("  Vertical PGM. "));
            Assert.Eq("ab", Util.CleanFilenamePart("a<b>:\"/\\|?*"));
        }
    }

    public class JsonTests
    {
        [Fact]
        public void Round_trip_of_nested_values()
        {
            string text = "{\"a\": [1, 2.5, \"x\\n\\u00e9\", true, null], \"b\": {\"c\": -3e2}}";
            object v = Json.Parse(text);
            string again = Json.Serialize(v, false);
            Assert.Eq("{\"a\":[1,2.5,\"x\\n\u00e9\",true,null],\"b\":{\"c\":-300}}", again);
        }

        [Fact]
        public void Invalid_json_throws()
        {
            Assert.Throws<FormatException>(delegate { Json.Parse("{ not json"); });
            Assert.Throws<FormatException>(delegate { Json.Parse("[1,2"); });
            Assert.Throws<FormatException>(delegate { Json.Parse("{} x"); });
            Assert.Throws<FormatException>(delegate { Json.Parse(""); });
        }

        [Fact]
        public void Snake_case_field_names()
        {
            Assert.Eq("ffmpeg_path", Json.SnakeCase("FfmpegPath"));
            Assert.Eq("min_age_seconds", Json.SnakeCase("MinAgeSeconds"));
            Assert.Eq("enabled", Json.SnakeCase("Enabled"));
        }

        [Fact]
        public void FillFrom_ignores_wrong_types_and_missing_keys()
        {
            FolderPolicy f = new FolderPolicy();
            Json.FillFrom(f, Json.Parse("{\"min_age_seconds\": \"abc\", \"name\": 5, \"path\": \"E:\\\\X\", \"enabled\": \"yes\"}"));
            Assert.Eq(120, f.MinAgeSeconds);
            Assert.Eq("Folder", f.Name);
            Assert.Eq(@"E:\X", f.Path);
            Assert.False(f.Enabled);
        }

        [Fact]
        public void Lists_of_objects_are_replaced_not_merged()
        {
            FolderPolicy f = new FolderPolicy();
            Json.FillFrom(f, Json.Parse("{\"rules\": [{\"name\": \"One\", \"older_than\": 2}]}"));
            Assert.Eq(1, f.Rules.Count);
            Assert.Eq("One", f.Rules[0].Name);
            Assert.Eq(2.0, f.Rules[0].OlderThan);
            Assert.True(f.Rules[0].Enabled);   // default for a missing field
            Json.FillFrom(f, Json.Parse("{\"rules\": []}"));
            Assert.Eq(0, f.Rules.Count);
        }

        [Fact]
        public void Clone_preserves_integer_long_and_double_fields()
        {
            AppConfig a = new AppConfig();
            a.Vertical.PollSeconds = 17;
            a.Vertical.Video.Width = 720;
            a.Vertical.Video.Fps = 29.97;
            FolderPolicy f = new FolderPolicy();
            f.MinAgeSeconds = 0;
            a.Folders.Add(f);
            AppConfig b = Json.Clone(a);
            Assert.Eq(17, b.Vertical.PollSeconds);
            Assert.Eq(720, b.Vertical.Video.Width);
            Assert.Eq(29.97, b.Vertical.Video.Fps);
            Assert.Eq(0, b.Folders[0].MinAgeSeconds);
            AppConfig c = Cfg.Parse(Cfg.ToText(a));
            Assert.Eq(17, c.Vertical.PollSeconds);
            Assert.Eq(0, c.Folders[0].MinAgeSeconds);
            HistoryItem h = new HistoryItem();
            h.OutSize = 5000000000L;
            Assert.Eq(5000000000L, Json.Clone(h).OutSize);
        }

        [Fact]
        public void Clone_is_deep()
        {
            AppConfig a = Cfg.Starter();
            AppConfig b = Json.Clone(a);
            b.Folders[3].Rules[0].OlderThan = 99;
            b.Vertical.Extensions.Add(".xyz");
            Assert.Eq(7.0, a.Folders[3].Rules[0].OlderThan);
            Assert.Eq(1, a.Vertical.Extensions.Count);
            List<FolderPolicy> l = (List<FolderPolicy>)Json.CloneObject(a.Folders);
            l[0].Name = "changed";
            Assert.Eq("Move ISO  T: > D:", a.Folders[0].Name);
            Assert.Eq(a.Folders.Count, l.Count);
        }
    }
}
