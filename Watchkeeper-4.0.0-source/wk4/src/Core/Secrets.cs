// Passwords and tokens in the settings file are stored encrypted for this computer (Windows DPAPI, machine key):
// the service and an administrator's manager can both read them, a copy of the file on another computer cannot.
// A value typed into the file in plain text is encrypted the next time the settings are saved or loaded by the service.
using System;
using System.Security.Cryptography;
using System.Text;

namespace Watchkeeper
{
    public static class Secrets
    {
        public const string Prefix = "enc:";
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Watchkeeper-4-settings");

        public static bool IsEncrypted(string v) { return v != null && v.StartsWith(Prefix, StringComparison.Ordinal); }

        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain) || IsEncrypted(plain)) return plain ?? "";
            byte[] data = Encoding.UTF8.GetBytes(plain);
            if (Util.IsWindows) data = ProtectedData.Protect(data, Entropy, DataProtectionScope.LocalMachine);
            return Prefix + Convert.ToBase64String(data);      // off Windows (tests only): encoded, not encrypted
        }

        /// <summary>The plain value. Throws when it was encrypted on another computer.</summary>
        public static string Reveal(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return "";
            if (!IsEncrypted(stored)) return stored;
            byte[] data;
            try { data = Convert.FromBase64String(stored.Substring(Prefix.Length)); }
            catch (FormatException) { throw new InvalidOperationException("the stored password is damaged; type it again"); }
            if (Util.IsWindows)
            {
                try { data = ProtectedData.Unprotect(data, Entropy, DataProtectionScope.LocalMachine); }
                catch (CryptographicException)
                {
                    throw new InvalidOperationException("the stored password was encrypted on another computer; type it again");
                }
            }
            return Encoding.UTF8.GetString(data);
        }

        /// <summary>Reveal, or "" when it cannot be read (the reason goes to problem).</summary>
        public static string TryReveal(string stored, out string problem)
        {
            problem = null;
            try { return Reveal(stored); }
            catch (Exception ex) { problem = ex.Message; return ""; }
        }

        // ---------------------------------------------------------------- 3.x settings
        static readonly byte[] Entropy3Machine = Encoding.UTF8.GetBytes("Studio" + "Manager-alerts");
        static readonly byte[] Entropy3Service = Encoding.UTF8.GetBytes("Watchkeeper-service-secrets");

        /// <summary>A password stored by version 3: plain, "dpapi:" (machine key) or "dpapi-svc:" (the service account's key,
        /// readable only inside the service). Null when it cannot be read here.</summary>
        public static string RevealVersion3(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return "";
            bool svc = stored.StartsWith("dpapi-svc:", StringComparison.Ordinal);
            bool machine = stored.StartsWith("dpapi:", StringComparison.Ordinal);
            if (stored.StartsWith("keep:", StringComparison.Ordinal)) return null;
            if (stored.StartsWith("plain:", StringComparison.Ordinal)) return stored.Substring(6);
            if (!svc && !machine) return stored;
            if (!Util.IsWindows) return null;
            try
            {
                byte[] data = Convert.FromBase64String(stored.Substring(svc ? 10 : 6));
                data = ProtectedData.Unprotect(data, svc ? Entropy3Service : Entropy3Machine,
                                               svc ? DataProtectionScope.CurrentUser : DataProtectionScope.LocalMachine);
                return Encoding.UTF8.GetString(data);
            }
            catch (Exception) { return null; }
        }
    }
}
