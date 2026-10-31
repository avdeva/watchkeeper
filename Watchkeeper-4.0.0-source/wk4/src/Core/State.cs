// Small values the service remembers between restarts (when each folder task last ran), in state.json.
using System;
using System.Collections.Generic;
using System.IO;

namespace Watchkeeper
{
    public class StateStore
    {
        readonly string path;
        readonly object lk = new object();
        readonly Dictionary<string, double> values = new Dictionary<string, double>();

        public StateStore(string path)
        {
            this.path = path;
            if (path == null || !File.Exists(path)) return;
            try
            {
                JObject o = Json.Parse(File.ReadAllText(path)) as JObject;
                if (o != null) foreach (string k in o.Keys) { object v = o.Get(k); if (v is double) values[k] = (double)v; }
            }
            catch (Exception) { }          // a damaged state file only means tasks run as if for the first time
        }

        public double Get(string key)
        {
            lock (lk) { double v; return values.TryGetValue(key, out v) ? v : 0; }
        }

        public void Set(string key, double value)
        {
            string text;
            lock (lk)
            {
                values[key] = value;
                JObject o = new JObject();
                foreach (KeyValuePair<string, double> kv in values) o.Set(kv.Key, kv.Value);
                text = Json.Serialize(o, true);
            }
            if (path == null) return;
            try { Util.WriteAtomic(path, text); } catch (Exception) { }
        }
    }
}
