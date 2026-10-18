// Minimal JSON reader/writer plus reflection mapping of plain config classes.
// Keys are the class field names converted to snake_case ("FfmpegPath" -> "ffmpeg_path").
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace Watchkeeper
{
    public class JObject
    {
        readonly List<string> keys = new List<string>();
        readonly Dictionary<string, object> map = new Dictionary<string, object>();

        public void Set(string key, object value)
        {
            if (!map.ContainsKey(key)) keys.Add(key);
            map[key] = value;
        }

        public object Get(string key)
        {
            object v;
            return map.TryGetValue(key, out v) ? v : null;
        }

        public bool Has(string key) { return map.ContainsKey(key); }
        public IList<string> Keys { get { return keys; } }

        public string Str(string key, string def)
        {
            string s = Get(key) as string;
            return s ?? def;
        }

        public double Num(string key, double def)
        {
            object v = Get(key);
            return v is double ? (double)v : def;
        }

        public bool Bool(string key, bool def)
        {
            object v = Get(key);
            return v is bool ? (bool)v : def;
        }

        /// <summary>The value converted to a config/DTO class (new instance), or null.</summary>
        public T As<T>(string key) where T : class, new()
        {
            object v = Get(key);
            if (v == null) return null;
            return Json.ConvertTo(typeof(T), v) as T;
        }
    }

    public static class Json
    {
        // ---------------------------------------------------------------- parse
        public static object Parse(string text)
        {
            int i = 0;
            if (text == null) throw new FormatException("no JSON text");
            object v = ParseValue(text, ref i, 0);
            SkipWs(text, ref i);
            if (i != text.Length) throw new FormatException("unexpected data after JSON value at " + i);
            return v;
        }

        static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        /// <summary>Deeper nesting is refused: unlimited nesting would overflow the stack and end the process.</summary>
        public const int MaxDepth = 64;

        static object ParseValue(string s, ref int i, int depth)
        {
            if (depth > MaxDepth) throw new FormatException("JSON nested deeper than " + MaxDepth + " levels at " + i);
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("unexpected end of JSON");
            char c = s[i];
            if (c == '{') return ParseObject(s, ref i, depth + 1);
            if (c == '[') return ParseArray(s, ref i, depth + 1);
            if (c == '"') return ParseString(s, ref i);
            if (Literal(s, ref i, "true")) return true;
            if (Literal(s, ref i, "false")) return false;
            if (Literal(s, ref i, "null")) return null;
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            double d;
            if (i == start || !double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                throw new FormatException("invalid JSON at " + start);
            return d;
        }

        static bool Literal(string s, ref int i, string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) return false;
            i += word.Length;
            return true;
        }

        static JObject ParseObject(string s, ref int i, int depth)
        {
            JObject o = new JObject();
            i++;
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return o; }
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new FormatException("expected property name at " + i);
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("expected ':' at " + i);
                i++;
                o.Set(key, ParseValue(s, ref i, depth));
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; return o; }
                throw new FormatException("expected ',' or '}' at " + i);
            }
        }

        static List<object> ParseArray(string s, ref int i, int depth)
        {
            List<object> a = new List<object>();
            i++;
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return a; }
            while (true)
            {
                a.Add(ParseValue(s, ref i, depth));
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; return a; }
                throw new FormatException("expected ',' or ']' at " + i);
            }
        }

        static string ParseString(string s, ref int i)
        {
            StringBuilder sb = new StringBuilder();
            i++;
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("bad \\u escape");
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: sb.Append(e); break;
                }
            }
            throw new FormatException("unterminated string");
        }

        // ------------------------------------------------------------ serialise
        public static string Serialize(object value, bool indent)
        {
            StringBuilder sb = new StringBuilder();
            Write(sb, value, indent, 0);
            return sb.ToString();
        }

        static void NewLine(StringBuilder sb, bool indent, int level)
        {
            if (!indent) return;
            sb.Append('\n');
            sb.Append(' ', level * 2);
        }

        static void Write(StringBuilder sb, object v, bool indent, int level)
        {
            if (v == null) { sb.Append("null"); return; }
            if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
            if (v is string) { WriteString(sb, (string)v); return; }
            if (v is double || v is float || v is int || v is long)
            {
                double d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                if (d == Math.Floor(d) && Math.Abs(d) < 1e15) sb.Append(((long)d).ToString(CultureInfo.InvariantCulture));
                else sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            JObject o = v as JObject;
            if (o != null)
            {
                if (o.Keys.Count == 0) { sb.Append("{}"); return; }
                sb.Append('{');
                bool first = true;
                foreach (string k in o.Keys)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    NewLine(sb, indent, level + 1);
                    WriteString(sb, k);
                    sb.Append(indent ? ": " : ":");
                    Write(sb, o.Get(k), indent, level + 1);
                }
                NewLine(sb, indent, level);
                sb.Append('}');
                return;
            }
            IEnumerable list = v as IEnumerable;
            if (list != null)
            {
                bool any = false;
                sb.Append('[');
                foreach (object item in list)
                {
                    if (any) sb.Append(',');
                    any = true;
                    NewLine(sb, indent, level + 1);
                    Write(sb, item, indent, level + 1);
                }
                if (any) NewLine(sb, indent, level);
                sb.Append(']');
                return;
            }
            throw new InvalidOperationException("cannot serialise " + v.GetType());
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ------------------------------------------------------------ mapping
        public static string SnakeCase(string name)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (char.IsUpper(c) && i > 0) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        public static object ToJson(object obj)
        {
            if (obj == null) return null;
            Type t = obj.GetType();
            if (obj is int) return (double)(int)obj;     // the JSON model has one numeric type
            if (obj is long) return (double)(long)obj;
            if (obj is string || obj is bool || obj is double) return obj;
            IList list = obj as IList;
            if (list != null)
            {
                List<object> a = new List<object>();
                foreach (object item in list) a.Add(ToJson(item));
                return a;
            }
            JObject o = new JObject();
            foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
                o.Set(SnakeCase(f.Name), ToJson(f.GetValue(obj)));
            return o;
        }

        /// <summary>Copy values from parsed JSON onto an existing object. Missing keys and
        /// values of the wrong type leave the existing (default) value untouched.</summary>
        public static void FillFrom(object target, object json)
        {
            JObject o = json as JObject;
            if (o == null || target == null) return;
            foreach (FieldInfo f in target.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                string key = SnakeCase(f.Name);
                if (!o.Has(key)) continue;
                object v = o.Get(key);
                object converted;
                if (ConvertValue(f.FieldType, v, f.GetValue(target), out converted)) f.SetValue(target, converted);
            }
        }

        static bool ConvertValue(Type t, object v, object current, out object result)
        {
            result = null;
            if (v == null) return false;
            if (t == typeof(string)) { if (!(v is string)) return false; result = v; return true; }
            if (t == typeof(bool)) { if (!(v is bool)) return false; result = v; return true; }
            if (t == typeof(int))
            {
                if (!(v is double)) return false;
                double d = (double)v;
                result = (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, Math.Round(d)));
                return true;
            }
            if (t == typeof(double)) { if (!(v is double)) return false; result = v; return true; }
            if (t == typeof(long))
            {
                if (!(v is double)) return false;
                double d = (double)v;
                result = (long)Math.Max(long.MinValue, Math.Min(long.MaxValue, Math.Round(d)));
                return true;
            }
            if (t == typeof(List<string>) && v is string)
            {
                // hand-edited config: "mp4, .mkv ; mov" is accepted for a list of strings
                List<string> words = new List<string>();
                foreach (string w in ((string)v).Split(',', ';'))
                    if (w.Trim().Length > 0) words.Add(w.Trim());
                result = words;
                return true;
            }
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
            {
                List<object> src = v as List<object>;
                if (src == null) return false;
                Type item = t.GetGenericArguments()[0];
                IList list = (IList)Activator.CreateInstance(t);
                foreach (object e in src)
                {
                    object ce;
                    if (item.IsClass && item != typeof(string))
                    {
                        object inst = Activator.CreateInstance(item);
                        FillFrom(inst, e);
                        list.Add(inst);
                    }
                    else if (ConvertValue(item, e, null, out ce)) list.Add(ce);
                }
                result = list;
                return true;
            }
            if (t.IsClass)
            {
                if (!(v is JObject)) return false;
                object inst = current ?? Activator.CreateInstance(t);
                FillFrom(inst, v);
                result = inst;
                return true;
            }
            return false;
        }

        /// <summary>Convert parsed JSON to a value of the given type (lists and classes included); null when impossible.</summary>
        public static object ConvertTo(Type t, object json)
        {
            object r;
            return ConvertValue(t, json, null, out r) ? r : null;
        }

        /// <summary>Deep copy of a list of config objects.</summary>
        public static List<T> CloneList<T>(List<T> list) where T : new()
        {
            List<T> copy = new List<T>();
            if (list == null) return copy;
            foreach (T item in list) copy.Add(Clone(item));
            return copy;
        }

        public static T Clone<T>(T obj) where T : new()
        {
            T copy = new T();
            FillFrom(copy, ToJson(obj));
            return copy;
        }

        /// <summary>Deep copy by runtime type (needs a public parameterless constructor).</summary>
        public static object CloneObject(object obj)
        {
            if (obj == null) return null;
            if (obj is IList) return ConvertTo(obj.GetType(), ToJson(obj));
            object copy = Activator.CreateInstance(obj.GetType());
            FillFrom(copy, ToJson(obj));
            return copy;
        }
    }
}
