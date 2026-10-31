// Checks of the settings that can be made without running anything. Errors mean the entry cannot work as set up
// (the service skips it and shows why); warnings mean it works but may not do what was meant.
using System;
using System.Collections.Generic;
using System.IO;

namespace Watchkeeper
{
    public static class Validate
    {
        /// <summary>A full path: C:\... or \\server\share\... (and /... off Windows, for tests).</summary>
        static bool Rooted(string p)
        {
            string x = (p ?? "").Replace('*', 'x');
            if (System.Text.RegularExpressions.Regex.IsMatch(x, @"^[A-Za-z]:[\\/]")) return true;
            if (x.StartsWith(@"\\") && x.Length > 4) return true;
            return !Util.IsWindows && x.StartsWith("/");
        }

        /// <summary>The kind of drive a letter is: "network", "missing" or "local" (replaced in tests).</summary>
        public static Func<char, string> DriveKind = RealDriveKind;

        static string RealDriveKind(char letter)
        {
            if (!Util.IsWindows) return "local";
            try
            {
                DriveInfo d = new DriveInfo(letter.ToString());
                if (d.DriveType == DriveType.Network) return "network";
                if (d.DriveType == DriveType.NoRootDirectory) return "missing";
            }
            catch (Exception) { return "missing"; }
            return "local";
        }

        /// <summary>Mapped drive letters belong to the user who mapped them: the service (running as SYSTEM) does not
        /// see them, so a folder on one must be given as \\server\share\... instead.</summary>
        static void CheckDrive(List<Problem> o, string where, string what, string path)
        {
            if (path == null || !System.Text.RegularExpressions.Regex.IsMatch(path, @"^[A-Za-z]:[\\/]")) return;
            char letter = char.ToUpperInvariant(path[0]);
            string kind = DriveKind(letter);
            if (kind == "network")
                o.Add(new Problem("warning", where, what + " is on mapped drive " + letter + ": - the service cannot see drive letters mapped by a user; use the \\\\server\\share path instead"));
            else if (kind == "missing")
                o.Add(new Problem("warning", where, what + " is on drive " + letter + ":, which this computer does not have (a mapped drive only the service cannot see, or a removable drive that is not plugged in)"));
        }

        public static string WatchName(ProgramEntry p) { return "Watchdog '" + p.Name + "'"; }
        public static string FolderName(FolderTask f) { return "Folder task '" + f.Name + "'"; }

        public static List<Problem> All(Settings s)
        {
            List<Problem> o = new List<Problem>();
            foreach (ProgramEntry p in s.Watchdog) if (p.Enabled) o.AddRange(Program(p));
            foreach (FolderTask f in s.Folders) if (f.Enabled) o.AddRange(Folder(f));
            if (s.Vertical.Enabled) o.AddRange(Vertical(s));
            if (s.Gpio.Enabled) o.AddRange(Gpio(s.Gpio));
            foreach (AlertDestination d in s.Alerts.Destinations) if (d.Enabled) o.AddRange(Alert(d));
            return o;
        }

        public static List<Problem> Program(ProgramEntry p)
        {
            List<Problem> o = new List<Problem>();
            string w = WatchName(p);
            if (p.Kind == "service")
            {
                if (p.ServiceName.Length == 0) o.Add(new Problem("error", w, "the Windows service name is missing"));
                if (p.WatchPath.Length == 0 && (p.RunningMeans == "app" || p.RunningMeans == "either" || p.RunningMeans == "both"))
                    o.Add(new Problem("error", w, "'Running means' needs the application to watch"));
            }
            else
            {
                if (p.Path.Length == 0) o.Add(new Problem("error", w, "the program to start is missing"));
                else if (!Rooted(p.Path)) o.Add(new Problem("error", w, "the program needs its full path, e.g. C:\\Program Files\\App\\App.exe"));
                if (p.StartIn.Length > 0 && !Rooted(p.StartIn)) o.Add(new Problem("error", w, "'Start in' needs a full folder path"));
            }
            if (p.WatchPath.Length > 0)
            {
                string norm = p.WatchPath.Replace('/', '\\');
                string name = norm.Substring(norm.LastIndexOf('\\') + 1);
                if (!Rooted(p.WatchPath) || p.WatchPath.IndexOfAny(new[] { '\\', '/' }) < 0)
                    o.Add(new Problem("error", w, "the application to watch needs its full path, e.g. C:\\Program Files\\App\\App.exe"));
                else if (name.IndexOf('*') >= 0 || name.IndexOf('?') >= 0)
                    o.Add(new Problem("error", w, "* may stand for part of a folder name in the application to watch, not for the program's own name"));
            }
            if (p.RestartAt.Length > 0 && Util.ParseTime(p.RestartAt) == null)
                o.Add(new Problem("error", w, "daily restart time '" + p.RestartAt + "' is not HH:MM"));
            if (p.HealthUrl.Length > 0)
            {
                string h = p.HealthUrl.ToLowerInvariant();
                Uri u;
                if (!(h.StartsWith("http://") || h.StartsWith("https://") || h.StartsWith("tcp://")) || !Uri.TryCreate(p.HealthUrl, UriKind.Absolute, out u))
                    o.Add(new Problem("error", w, "health check address must look like http://host:port/path, https://... or tcp://host:port"));
                else if (h.StartsWith("tcp://") && u.Port <= 0)
                    o.Add(new Problem("error", w, "health check address must name the port: tcp://host:port"));
            }
            if (p.Kind == "program" && p.WatchPath.Length > 0 && p.RunningMeans == "both")
                o.Add(new Problem("warning", w, "'Both' restarts the application whenever the launcher is not running; use it only for a launcher that stays open"));
            return o;
        }

        public static List<Problem> Folder(FolderTask f)
        {
            List<Problem> o = new List<Problem>();
            string w = FolderName(f);
            if (f.Source.Length == 0) o.Add(new Problem("error", w, "the folder is missing"));
            else if (!Rooted(f.Source)) o.Add(new Problem("error", w, "the folder needs its full path, e.g. D:\\Recordings"));
            else CheckDrive(o, w, "the folder", f.Source);
            if (f.Action != "delete")
            {
                if (f.Destination.Length == 0) o.Add(new Problem("error", w, "the destination folder is missing"));
                else if (!Rooted(f.Destination)) o.Add(new Problem("error", w, "the destination needs its full path"));
                else if (f.Source.Length > 0 && Util.SamePath(f.Source, f.Destination)) o.Add(new Problem("error", w, "the destination is the folder itself"));
                else if (f.Source.Length > 0 && f.Subfolders && Util.IsInside(f.Destination, f.Source))
                    o.Add(new Problem("error", w, "the destination is inside the folder and sub-folders are included: files would be handled again and again"));
                if (Rooted(f.Destination)) CheckDrive(o, w, "the destination", f.Destination);
            }
            else if (f.OlderThan <= 0 && f.KeepNewest == 0 && f.Include.Trim() == "*")
                o.Add(new Problem("warning", w, "deletes every file in the folder (no age limit, no files kept)"));
            if (f.Action != "delete" && f.Subfolders && !f.KeepStructure && f.IfExists == "overwrite")
                o.Add(new Problem("warning", w, "files with the same name in different sub-folders all go to one folder; the later ones are given new names (2), (3), ..."));
            if (f.Run == "daily" && Util.ParseTime(f.At) == null) o.Add(new Problem("error", w, "time '" + f.At + "' is not HH:MM"));
            return o;
        }

        public static List<Problem> Vertical(Settings s)
        {
            List<Problem> o = new List<Problem>();
            VerticalSettings v = s.Vertical;
            const string w = "Vertical conversion";
            if (v.WatchFolder.Length == 0 || !Rooted(v.WatchFolder)) o.Add(new Problem("error", w, "the folder to watch needs its full path"));
            if (v.OutputFolder.Length == 0 || !Rooted(v.OutputFolder)) o.Add(new Problem("error", w, "the output folder needs its full path"));
            else if (v.WatchFolder.Length > 0 && Util.SamePath(v.WatchFolder, v.OutputFolder)) o.Add(new Problem("error", w, "the output folder must not be the folder to watch"));
            if (v.AfterSuccess == "move")
            {
                if (v.MoveOriginalsTo.Length == 0 || !Rooted(v.MoveOriginalsTo)) o.Add(new Problem("error", w, "the folder for the originals needs its full path"));
                else if (v.WatchFolder.Length > 0 && Util.SamePath(v.WatchFolder, v.MoveOriginalsTo)) o.Add(new Problem("error", w, "the folder for the originals must not be the folder to watch"));
            }
            CheckDrive(o, w, "the folder to watch", v.WatchFolder);
            CheckDrive(o, w, "the output folder", v.OutputFolder);
            if (v.AfterSuccess == "move") CheckDrive(o, w, "the folder for the originals", v.MoveOriginalsTo);
            if (s.General.FfmpegPath.Length == 0) o.Add(new Problem("error", w, "the ffmpeg path is missing (Settings page)"));
            if (v.Width > 0 && v.Width % 2 == 1 || v.Height > 0 && v.Height % 2 == 1) o.Add(new Problem("error", w, "width and height must be even numbers"));
            try { Util.SplitArgs(v.ExtraInputArgs); Util.SplitArgs(v.ExtraOutputArgs); }
            catch (FormatException ex) { o.Add(new Problem("error", w, "extra ffmpeg options: " + ex.Message)); }
            if (!v.ConvertOthers && v.Sources.FindAll(delegate(VerticalSource x) { return x.Enabled && x.Match.Length > 0; }).Count == 0)
                o.Add(new Problem("warning", w, "no source is switched on and other files are not converted: nothing will be converted"));
            return o;
        }

        public static List<Problem> Gpio(GpioSettings g)
        {
            List<Problem> o = new List<Problem>();
            foreach (AxiaDevice d in g.Devices)
                if (d.Enabled && d.Host.Length == 0) o.Add(new Problem("error", "GPIO device '" + d.Name + "'", "the address is missing"));
            foreach (VmixTarget t in g.Vmix)
                if (t.Host.Length == 0) o.Add(new Problem("error", "vMix '" + t.Name + "'", "the address is missing"));
            foreach (GpioTrigger t in g.Triggers)
            {
                if (!t.Enabled) continue;
                string w = "GPIO trigger '" + t.Name + "'";
                if (!g.Devices.Exists(delegate(AxiaDevice d) { return d.Id == t.DeviceId; })) o.Add(new Problem("error", w, "choose the device"));
                if (t.WhenOn.Count == 0 && t.WhenOff.Count == 0) o.Add(new Problem("warning", w, "has no actions"));
                foreach (GpioAction a in Actions(t))
                {
                    if (a.Type == "vmix")
                    {
                        if (g.Vmix.Count == 0) o.Add(new Problem("error", w, "uses vMix, but no vMix is set up"));
                        else if (a.VmixId.Length > 0 && !g.Vmix.Exists(delegate(VmixTarget x) { return x.Id == a.VmixId; })) o.Add(new Problem("error", w, "uses a vMix that no longer exists"));
                        if (a.Function.Length == 0) o.Add(new Problem("error", w, "a vMix action has no function"));
                    }
                    else
                    {
                        Uri u;
                        if (!Uri.TryCreate(a.Url, UriKind.Absolute, out u) || (u.Scheme != "http" && u.Scheme != "https"))
                            o.Add(new Problem("error", w, "a web request needs an address starting with http:// or https://"));
                    }
                }
            }
            return o;
        }

        static IEnumerable<GpioAction> Actions(GpioTrigger t)
        {
            foreach (GpioAction a in t.WhenOn) yield return a;
            foreach (GpioAction a in t.WhenOff) yield return a;
        }

        public static List<Problem> Alert(AlertDestination d)
        {
            List<Problem> o = new List<Problem>();
            string w = "Alert destination '" + d.Name + "'";
            if (d.Type == "email")
            {
                if (d.SmtpHost.Length == 0) o.Add(new Problem("error", w, "the mail server is missing"));
                if (d.From.Length == 0 || d.From.IndexOf('@') < 0) o.Add(new Problem("error", w, "the sender address is missing"));
                if (Util.Patterns(d.To).Count == 0) o.Add(new Problem("error", w, "no recipient"));
                foreach (string a in Util.Patterns(d.To)) if (a.IndexOf('@') < 0) o.Add(new Problem("error", w, "'" + a + "' is not an e-mail address"));
            }
            else if (d.Type == "jira")
            {
                Uri u;
                if (!Uri.TryCreate(d.JiraUrl, UriKind.Absolute, out u) || (u.Scheme != "https" && u.Scheme != "http")) o.Add(new Problem("error", w, "the Jira address must start with https://"));
                if (d.JiraProject.Length == 0) o.Add(new Problem("error", w, "the project key is missing"));
                if (d.JiraToken.Length == 0) o.Add(new Problem("error", w, "the API token is missing"));
            }
            else
            {
                Uri u;
                if (!Uri.TryCreate(d.Url, UriKind.Absolute, out u) || (u.Scheme != "https" && u.Scheme != "http")) o.Add(new Problem("error", w, "the address must start with https://"));
            }
            if (!d.Watchdog && !d.Folders && !d.Vertical && !d.Gpio && !d.Service) o.Add(new Problem("warning", w, "no kind of alert is ticked"));
            return o;
        }

        public static bool HasErrors(List<Problem> list)
        {
            return list.Exists(delegate(Problem p) { return p.Level == "error"; });
        }
    }
}
