// What the parts report to the alerts part. A problem is raised under a key; the same key is "back to normal" when
// the problem is over. The alerts part decides where each one goes and how often.
namespace Watchkeeper
{
    public class Alert
    {
        /// <summary>watchdog | folders | vertical | gpio | service</summary>
        public string Part = "";
        /// <summary>Identifies the problem, e.g. "watchdog:ab12cd34:stopped". A recovery uses the same key.</summary>
        public string Key = "";
        /// <summary>warning | error | info (info: recoveries and test messages)</summary>
        public string Level = "warning";
        public string Title = "";
        public string Message = "";
        public bool Recovery;
        public double Time = Util.Now();
    }

    public interface IAlertSink
    {
        void Raise(string part, string key, string level, string title, string message);
        /// <summary>The problem under this key is over. Sent only when the key was raised before.</summary>
        void Recover(string key, string title, string message);
    }

    /// <summary>For tests and parts used without alerts.</summary>
    public class NoAlerts : IAlertSink
    {
        public void Raise(string part, string key, string level, string title, string message) { }
        public void Recover(string key, string title, string message) { }
    }
}
