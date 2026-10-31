// An example settings file (Watchkeeper.exe --example file.json): typical entries for a recording server, all switched
// off, to edit and hand to deploy.cmd. Paths are examples; every server has its own.
using System.Collections.Generic;

namespace Watchkeeper
{
    public static class Examples
    {
        public static Settings Studio()
        {
            Settings s = new Settings();
            s.Watchdog.Add(new ProgramEntry
            {
                Id = "vmix", Name = "vMix", Path = @"C:\Program Files (x86)\vMix\vMix64.exe", StartDelaySeconds = 20,
                HealthUrl = "http://127.0.0.1:8088/api", HealthAction = "alert",
                Notes = "Example: check the path. The health check needs Settings > Web Controller switched on in vMix."
            });
            s.Watchdog.Add(new ProgramEntry
            {
                Id = "vvd", Name = "Virtual Video Director", Path = @"C:\Program Files\Virtual Video Director\VVD Launcher.exe",
                WatchPath = @"C:\Users\Operator\AppData\Local\Virtual Video Director\app-*\Virtual Video Director.exe", LauncherWaitSeconds = 90,
                Notes = "Example of a launcher: set 'Program' to the launcher and 'Application to watch' to the program it opens (use Choose from running programs)."
            });
            s.Folders.Add(new FolderTask
            {
                Id = "move", Name = "Move recordings to storage", Source = @"T:\Recordings", Destination = @"D:\Recordings", Subfolders = true,
                Include = "*.mp4;*.mov", SettleSeconds = 60, Run = "every", IntervalMinutes = 1, RemoveEmptyFolders = true,
                Notes = "Example: from the recording drive to the storage drive every minute; recordings still being written are left alone."
            });
            s.Folders.Add(new FolderTask
            {
                Id = "retention", Name = "Delete recordings older than 30 days", Source = @"D:\Recordings", Subfolders = true, Action = "delete",
                Include = "*.mp4;*.mov", OlderThan = 30, OlderThanUnit = "days", Run = "daily", At = "03:00", RemoveEmptyFolders = true,
                Notes = "Example retention."
            });
            s.Vertical.WatchFolder = @"T:\Vertical";
            s.Vertical.OutputFolder = @"D:\Vertical";
            s.Vertical.MoveOriginalsTo = @"D:\Vertical\Originals";
            s.Vertical.Sources.Add(new VerticalSource { Name = "Program", Match = "Program", Prefix = "Vertical Program" });
            s.Alerts.Destinations.Add(new AlertDestination
            {
                Id = "mail", Name = "Operations e-mail", Enabled = false, SmtpHost = "smtp.example.com", SmtpPort = 587,
                From = "watchkeeper@example.com", To = "operations@example.com", Notes = "Example: fill in your mail server."
            });
            SettingsFile.Normalize(s);
            return s;
        }
    }
}
