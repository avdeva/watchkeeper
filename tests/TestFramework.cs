// Tiny test framework (no NuGet needed): [Fact] methods on classes, one fresh instance per test.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using Watchkeeper;

namespace Watchkeeper.Tests
{
    [AttributeUsage(AttributeTargets.Method)]
    public class FactAttribute : Attribute { }

    public class SkipException : Exception
    {
        public SkipException(string why) : base(why) { }
    }

    public class AssertionFailed : Exception
    {
        public AssertionFailed(string message) : base(message) { }
    }

    public static class Assert
    {
        static string Fmt(object o)
        {
            if (o == null) return "null";
            if (o is string) return "\"" + o + "\"";
            IEnumerable e = o as IEnumerable;
            if (e != null)
            {
                List<string> parts = new List<string>();
                foreach (object x in e) parts.Add(Fmt(x));
                return "[" + string.Join(", ", parts.ToArray()) + "]";
            }
            return Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture);
        }

        public static void True(bool cond) { True(cond, "expected true"); }
        public static void True(bool cond, string msg) { if (!cond) throw new AssertionFailed(msg); }
        public static void False(bool cond) { True(!cond, "expected false"); }
        public static void False(bool cond, string msg) { True(!cond, msg); }

        public static void Eq<T>(T expected, T actual) { Eq(expected, actual, null); }
        public static void Eq<T>(T expected, T actual, string msg)
        {
            if (!SameValue(expected, actual))
                throw new AssertionFailed((msg != null ? msg + ": " : "") + "expected " + Fmt(expected) + " but was " + Fmt(actual));
        }

        static bool SameValue(object a, object b)
        {
            if (a == null || b == null) return a == null && b == null;
            IEnumerable ea = a as IEnumerable, eb = b as IEnumerable;
            if (ea != null && eb != null && !(a is string))
            {
                IEnumerator ia = ea.GetEnumerator(), ib = eb.GetEnumerator();
                while (true)
                {
                    bool ma = ia.MoveNext(), mb = ib.MoveNext();
                    if (ma != mb) return false;
                    if (!ma) return true;
                    if (!SameValue(ia.Current, ib.Current)) return false;
                }
            }
            return a.Equals(b);
        }

        public static void Contains(string hay, string needle)
        {
            if (hay == null || hay.IndexOf(needle, StringComparison.Ordinal) < 0)
                throw new AssertionFailed("expected text to contain \"" + needle + "\" but was: " + Fmt(hay));
        }

        public static void NotContains(string hay, string needle)
        {
            if (hay != null && hay.IndexOf(needle, StringComparison.Ordinal) >= 0)
                throw new AssertionFailed("expected text NOT to contain \"" + needle + "\" but was: " + Fmt(hay));
        }

        public static void In<T>(T item, IEnumerable<T> list)
        {
            foreach (T x in list) if (SameValue(x, item)) return;
            throw new AssertionFailed("expected " + Fmt(item) + " in " + Fmt(list));
        }

        public static void NotIn<T>(T item, IEnumerable<T> list)
        {
            foreach (T x in list) if (SameValue(x, item)) throw new AssertionFailed("expected " + Fmt(item) + " NOT in " + Fmt(list));
        }

        public static void Near(double expected, double actual, double delta)
        {
            if (Math.Abs(expected - actual) > delta)
                throw new AssertionFailed("expected " + expected + " +/- " + delta + " but was " + actual);
        }

        public static void Null(object o) { if (o != null) throw new AssertionFailed("expected null but was " + Fmt(o)); }
        public static void NotNull(object o) { if (o == null) throw new AssertionFailed("expected a value but was null"); }

        public static T Throws<T>(Action a) where T : Exception
        {
            try { a(); }
            catch (T ex) { return ex; }
            catch (Exception ex) { throw new AssertionFailed("expected " + typeof(T).Name + " but got " + ex.GetType().Name + ": " + ex.Message); }
            throw new AssertionFailed("expected " + typeof(T).Name + " but nothing was thrown");
        }
    }

    /// <summary>Records every log line a task writes.</summary>
    public class Cap
    {
        public readonly List<string> Lines = new List<string>();
        public readonly SourceLogger Log;
        public Cap()
        {
            LogHub hub = new LogHub();
            Log = hub.Logger("test").WithCapture(Lines);
        }
        public string Text() { return string.Join("\n", Lines.ToArray()); }
    }

    /// <summary>Temp folder tree plus a config store wired to the fake ffmpeg/ffprobe.</summary>
    public class Env : IDisposable
    {
        public string Tmp;
        public LogHub Hub;
        public ConfigStore Cfgs;

        public static string Fakes { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fakes"); } }
        public static string Ffmpeg { get { return Path.Combine(Fakes, "ffmpeg"); } }
        public static string Ffprobe { get { return Path.Combine(Fakes, "ffprobe"); } }

        public Env()
        {
            Tmp = Path.Combine(Path.GetTempPath(), "wktest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Tmp);
            Hub = new LogHub();
            Cfgs = new ConfigStore(D("config.json"), Hub.Logger("config"));
            Cfgs.Load();
            Environment.SetEnvironmentVariable("FAKE_FAIL_ENCODER", null);
            Environment.SetEnvironmentVariable("FAKE_LOG", D("ffmpeg-calls.jsonl"));
            Environment.SetEnvironmentVariable("FAKE_DELAY", "0.1");
            Set<GeneralCfg>("general", delegate(GeneralCfg g) { g.FfmpegPath = Ffmpeg; g.FfprobePath = Ffprobe; });
        }

        public virtual void Dispose()
        {
            Environment.SetEnvironmentVariable("FAKE_FAIL_ENCODER", null);
            try { Directory.Delete(Tmp, true); } catch (Exception) { }
        }

        public string D(params string[] parts)
        {
            return Path.Combine(Tmp, Path.Combine(parts));
        }

        /// <summary>Change one config section through the real save path and require that it validates.</summary>
        public void Set<T>(string section, Action<T> change) where T : class
        {
            AppConfig c = Cfgs.Get();
            T s = (T)Cfg.GetSection(c, section);
            change(s);
            List<string> errors = Cfgs.SaveSection(section, s);
            Assert.Eq(0, errors.Count, "config errors: " + string.Join("; ", errors.ToArray()));
        }

        public static void WriteMedia(string path, int width, int height, double duration, double bitRate, string codec, string extraKey, object extraVal)
        {
            JObject o = new JObject();
            o.Set("fake", true);
            o.Set("width", (double)width);
            o.Set("height", (double)height);
            o.Set("duration", duration);
            o.Set("bit_rate", bitRate);
            o.Set("codec", codec);
            if (extraKey != null) o.Set(extraKey, extraVal);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, Json.Serialize(o, false));
        }

        public static void WriteMedia(string path)
        {
            WriteMedia(path, 1920, 1080, 600, 8530000, "h264", null, null);
        }

        public static void WriteMedia(string path, double duration)
        {
            WriteMedia(path, 1920, 1080, duration, 8530000, "h264", null, null);
        }

        public static void WriteMedia(string path, int width, int height, double duration)
        {
            WriteMedia(path, width, height, duration, 8530000, "h264", null, null);
        }

        public static void Age(string path, double seconds)
        {
            File.SetLastWriteTime(path, DateTime.Now.AddSeconds(-seconds));
        }

        public static bool WaitFor(Func<bool> cond) { return WaitFor(cond, 20); }
        public static bool WaitFor(Func<bool> cond, double timeoutSeconds)
        {
            DateTime end = DateTime.Now.AddSeconds(timeoutSeconds);
            while (DateTime.Now < end)
            {
                if (cond()) return true;
                Thread.Sleep(50);
            }
            return cond();
        }

        public List<string[]> FfmpegCalls()
        {
            List<string[]> calls = new List<string[]>();
            string p = D("ffmpeg-calls.jsonl");
            if (!File.Exists(p)) return calls;
            foreach (string line in File.ReadAllLines(p))
            {
                if (line.Trim().Length == 0) continue;
                List<object> arr = (List<object>)Json.Parse(line);
                string[] a = new string[arr.Count];
                for (int i = 0; i < a.Length; i++) a[i] = (string)arr[i];
                calls.Add(a);
            }
            return calls;
        }

        public List<string[]> EncodeCalls()
        {
            return FfmpegCalls().FindAll(delegate(string[] c)
            {
                return Array.IndexOf(c, "-i") >= 0 && Array.IndexOf(c, "lavfi") < 0;
            });
        }

        public static string ArgAfter(IList<string> args, string flag)
        {
            int i = -1;
            for (int k = 0; k < args.Count; k++) if (args[k] == flag) { i = k; break; }
            if (i < 0 || i + 1 >= args.Count) throw new AssertionFailed("flag " + flag + " not found in: " + string.Join(" ", new List<string>(args).ToArray()));
            return args[i + 1];
        }
    }

    public static class Runner
    {
        class Case
        {
            public Type Type;
            public MethodInfo Method;
            public string Name { get { return Type.Name + "." + Method.Name; } }
        }

        public static int Main(string[] args)
        {
            string filter = args.Length > 0 ? args[0] : null;
            List<Case> cases = new List<Case>();
            foreach (Type t in Assembly.GetExecutingAssembly().GetTypes())
            {
                if (!t.IsClass || t.IsAbstract) continue;
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (m.GetCustomAttributes(typeof(FactAttribute), false).Length == 0) continue;
                    Case c = new Case();
                    c.Type = t; c.Method = m;
                    if (filter == null || c.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) cases.Add(c);
                }
            }
            cases.Sort(delegate(Case a, Case b) { return string.CompareOrdinal(a.Name, b.Name); });

            int passed = 0, failed = 0, skipped = 0;
            List<string> failures = new List<string>();
            foreach (Case c in cases)
            {
                string outcome = null;
                Stopwatch sw = Stopwatch.StartNew();
                Thread th = new Thread(delegate() { outcome = Execute(c); });
                th.IsBackground = true;
                th.Start();
                if (!th.Join(120000)) outcome = "FAIL timed out after 120 s";
                sw.Stop();
                string line;
                if (outcome == null) { passed++; line = "  ok    "; }
                else if (outcome.StartsWith("SKIP")) { skipped++; line = "  skip  "; }
                else { failed++; line = "  FAIL  "; failures.Add(c.Name + ": " + outcome); }
                Console.WriteLine(line + c.Name + " (" + sw.ElapsedMilliseconds + " ms)" + (outcome != null ? "  " + outcome : ""));
            }
            Console.WriteLine();
            Console.WriteLine(passed + " passed, " + failed + " failed, " + skipped + " skipped");
            foreach (string f in failures) Console.WriteLine("FAILED " + f);
            return failed == 0 ? 0 : 1;
        }

        static string Execute(Case c)
        {
            object instance = null;
            try
            {
                try { instance = Activator.CreateInstance(c.Type); }
                catch (TargetInvocationException ex) { throw ex.InnerException; }
                try { c.Method.Invoke(instance, null); }
                catch (TargetInvocationException ex) { throw ex.InnerException; }
                return null;
            }
            catch (SkipException ex) { return "SKIP " + ex.Message; }
            catch (AssertionFailed ex) { return "FAIL " + ex.Message; }
            catch (Exception ex) { return "FAIL " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace; }
            finally
            {
                IDisposable d = instance as IDisposable;
                if (d != null) { try { d.Dispose(); } catch (Exception) { } }
            }
        }
    }
}
