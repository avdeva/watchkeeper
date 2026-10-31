// The settings model: everything Watchkeeper does comes from these classes, stored as one JSON file
// (C:\ProgramData\Watchkeeper\config.json). Keys are the field names in snake_case ("RestartDelaySeconds" -> "restart_delay_seconds").
using System;
using System.Collections.Generic;

namespace Watchkeeper
{
    public class Settings
    {
        /// <summary>Marks the file as a version 4 settings file (version 3 files are converted when they are loaded).</summary>
        public string Format = SettingsFile.FormatName;
        public GeneralSettings General = new GeneralSettings();
        public List<ProgramEntry> Watchdog = new List<ProgramEntry>();
        public List<FolderTask> Folders = new List<FolderTask>();
        public VerticalSettings Vertical = new VerticalSettings();
        public GpioSettings Gpio = new GpioSettings();
        public AlertSettings Alerts = new AlertSettings();
    }

    public class GeneralSettings
    {
        /// <summary>Name used in alerts. Empty = the computer name.</summary>
        public string ServerName = "";
        public string FfmpegPath = @"C:\Program Files\ffmpeg\bin\ffmpeg.exe";
        /// <summary>Empty = ffprobe.exe next to ffmpeg.</summary>
        public string FfprobePath = "";
        /// <summary>Write detailed (debug) lines to the log.</summary>
        public bool DetailedLog = false;
    }

    // ------------------------------------------------------------------ watchdog
    public class ProgramEntry
    {
        public string Id = "";
        public string Name = "Program";
        public bool Enabled = false;
        /// <summary>program | service</summary>
        public string Kind = "program";
        /// <summary>Windows service name (Kind = service), as in services.msc > Properties.</summary>
        public string ServiceName = "";
        /// <summary>The file to start (Kind = program). For a launcher, the launcher.</summary>
        public string Path = "";
        public string Arguments = "";
        /// <summary>Folder to start in. Empty = the program's own folder.</summary>
        public string StartIn = "";
        /// <summary>desktop = in the signed-in user's session (programs with a window) | background = hidden, in the service.</summary>
        public string RunIn = "desktop";
        /// <summary>desktop: start with the signed-in user's full administrator rights (no consent prompt).</summary>
        public bool Elevated = false;
        /// <summary>The application to watch when Path is a launcher, or the application a Windows service starts.
        /// Full path; * may stand for part of a folder name (a version folder: ...\app-*\App.exe).</summary>
        public string WatchPath = "";
        /// <summary>auto | app | started | either | both: what has to run for the entry to count as running.</summary>
        public string RunningMeans = "auto";
        /// <summary>How long the application gets to appear after a start (and how long one may run without the other with "both").</summary>
        public int LauncherWaitSeconds = 60;
        /// <summary>After the computer starts or a user signs in.</summary>
        public int StartDelaySeconds = 20;
        /// <summary>After it stops.</summary>
        public int RestartDelaySeconds = 10;
        /// <summary>Then pause for the rest of the hour and alert. 0 = no limit.</summary>
        public int MaxRestartsPerHour = 10;
        /// <summary>"HH:MM" = close and start again every day at that time (on RestartDays). Empty = never.</summary>
        public string RestartAt = "";
        /// <summary>Days for RestartAt (monday ... sunday). Empty = every day.</summary>
        public List<string> RestartDays = new List<string>();
        /// <summary>normal | above_normal | high</summary>
        public string Priority = "normal";
        /// <summary>http(s)://... (any answer below 500 counts) or tcp://host:port. Empty = no health check.</summary>
        public string HealthUrl = "";
        public string HealthUser = "";
        public string HealthPassword = "";
        public int HealthEverySeconds = 15;
        /// <summary>Not answering for this long = not healthy.</summary>
        public int HealthFailSeconds = 60;
        /// <summary>Loading time after a start before the first check counts.</summary>
        public int HealthGraceSeconds = 90;
        /// <summary>alert | restart</summary>
        public string HealthAction = "alert";
        public string Notes = "";
    }

    // ------------------------------------------------------------------ folders
    public class FolderTask
    {
        public string Id = "";
        public string Name = "Folder task";
        public bool Enabled = false;
        public string Source = "";
        public bool Subfolders = false;
        /// <summary>File name patterns, ';' separated. "*" = every file.</summary>
        public string Include = "*";
        public string Exclude = "";
        /// <summary>move | copy | delete</summary>
        public string Action = "move";
        public string Destination = "";
        /// <summary>With sub-folders: recreate the sub-folder structure at the destination.</summary>
        public bool KeepStructure = true;
        /// <summary>When the destination already has a file of that name: rename | skip | overwrite</summary>
        public string IfExists = "rename";
        /// <summary>Only files older than this (by date modified). 0 = any age.</summary>
        public double OlderThan = 0;
        /// <summary>minutes | hours | days</summary>
        public string OlderThanUnit = "days";
        /// <summary>The newest N matching files are left alone.</summary>
        public int KeepNewest = 0;
        /// <summary>Files changed within this many seconds (still being written) are left alone.</summary>
        public int SettleSeconds = 60;
        public bool RemoveEmptyFolders = false;
        /// <summary>every (IntervalMinutes) | daily (At) | manual (only Run now)</summary>
        public string Run = "every";
        public double IntervalMinutes = 5;
        public string At = "03:00";
        public string Notes = "";
    }

    // ------------------------------------------------------------------ vertical conversion
    public class VerticalSource
    {
        public string Name = "Source";
        /// <summary>Text the file name contains (case-insensitive).</summary>
        public string Match = "";
        /// <summary>Output names start with this.</summary>
        public string Prefix = "Vertical";
        public bool Enabled = true;
    }

    public class VerticalSettings
    {
        public bool Enabled = false;
        public string WatchFolder = "";
        public string OutputFolder = "";
        public List<string> Extensions = new List<string> { ".mp4" };
        /// <summary>A file is converted once it has not changed for this long.</summary>
        public int SettleSeconds = 10;
        /// <summary>First match decides the output name; a switched-off source leaves its files alone.</summary>
        public List<VerticalSource> Sources = new List<VerticalSource>();
        /// <summary>Files that match no source: converted with OtherPrefix, or left alone.</summary>
        public bool ConvertOthers = true;
        public string OtherPrefix = "Vertical";
        /// <summary>The original after a checked conversion: delete | keep | move</summary>
        public string AfterSuccess = "move";
        public string MoveOriginalsTo = "";
        public int Retries = 3;

        /// <summary>h264 | hevc</summary>
        public string Codec = "h264";
        /// <summary>counterclockwise | clockwise</summary>
        public string Rotate = "counterclockwise";
        /// <summary>Output size; 0 and 0 = the source size turned on its side.</summary>
        public int Width = 0;
        public int Height = 0;
        /// <summary>0 = keep the source frame rate.</summary>
        public double Fps = 0;
        /// <summary>source (same bitrate as the source) | fixed (FixedMbps) | quality (constant quality)</summary>
        public string Bitrate = "source";
        public double FixedMbps = 8;
        public int Quality = 23;
        /// <summary>Used when the source bitrate cannot be read.</summary>
        public double FallbackMbps = 8;
        /// <summary>fast | balanced | quality</summary>
        public string Preset = "balanced";
        /// <summary>copy | aac | none</summary>
        public string Audio = "copy";
        public int AudioKbps = 192;
        public string ExtraInputArgs = "";
        public string ExtraOutputArgs = "";

        /// <summary>auto | intel_qsv | nvidia_nvenc | amd_amf | cpu</summary>
        public string Encoder = "auto";
        /// <summary>Which GPU when there are several of the kind. -1 = the default.</summary>
        public int GpuIndex = -1;
        public bool HardwareDecode = true;
        /// <summary>Try again in the signed-in user's session when the GPU cannot be used from the service.</summary>
        public bool UserSessionFallback = true;
        /// <summary>Finally try the CPU encoder.</summary>
        public bool CpuFallback = true;
        /// <summary>normal | below_normal | idle</summary>
        public string Priority = "below_normal";
    }

    // ------------------------------------------------------------------ GPIO
    public class AxiaDevice
    {
        public string Id = "";
        public string Name = "Axia device";
        public bool Enabled = true;
        public string Host = "";
        public int Port = 93;
        /// <summary>LWRP password; empty = no login.</summary>
        public string Password = "";
    }

    public class VmixTarget
    {
        public string Id = "";
        public string Name = "vMix";
        public string Host = "127.0.0.1";
        public int Port = 8088;
        public string User = "";
        public string Password = "";
    }

    public class GpioAction
    {
        /// <summary>vmix | http</summary>
        public string Type = "vmix";
        /// <summary>vMix to call; empty = the first one.</summary>
        public string VmixId = "";
        public string Function = "Cut";
        public string Input = "";
        public string Value = "";
        /// <summary>More parameters: Name=Value&amp;Name2=Value2</summary>
        public string Extra = "";
        public string Method = "GET";
        public string Url = "";
        public string Body = "";
        public string ContentType = "application/json";
        /// <summary>Wait this long before this action.</summary>
        public int DelayMs = 0;
    }

    public class GpioTrigger
    {
        public string Id = "";
        public string Name = "Trigger";
        public bool Enabled = true;
        public string DeviceId = "";
        /// <summary>gpi (inputs) | gpo (outputs)</summary>
        public string Signal = "gpi";
        public int Port = 1;
        /// <summary>1 - 5</summary>
        public int Pin = 1;
        /// <summary>Normally a pin is on when it is low (contact closed). Inverted: on when high.</summary>
        public bool Invert = false;
        /// <summary>The same change again within this time is ignored.</summary>
        public int IgnoreRepeatMs = 0;
        public List<GpioAction> WhenOn = new List<GpioAction>();
        public List<GpioAction> WhenOff = new List<GpioAction>();
    }

    public class GpioSettings
    {
        public bool Enabled = false;
        public List<AxiaDevice> Devices = new List<AxiaDevice>();
        public List<VmixTarget> Vmix = new List<VmixTarget>();
        public List<GpioTrigger> Triggers = new List<GpioTrigger>();
        /// <summary>Alert when a device has been unreachable this long.</summary>
        public int DisconnectAlertSeconds = 60;
    }

    // ------------------------------------------------------------------ alerts
    public class AlertDestination
    {
        public string Id = "";
        public string Name = "Alerts";
        public bool Enabled = true;
        /// <summary>email | jira | webhook</summary>
        public string Type = "email";

        // which alerts
        public bool Watchdog = true;
        public bool Folders = true;
        public bool Vertical = true;
        public bool Gpio = true;
        public bool Service = true;
        /// <summary>warning | error</summary>
        public string MinLevel = "warning";
        /// <summary>Also send "back to normal".</summary>
        public bool Recoveries = true;
        /// <summary>The same alert again within this time is not sent again.</summary>
        public int RepeatMinutes = 30;

        // email
        public string SmtpHost = "";
        public int SmtpPort = 587;
        /// <summary>starttls | ssl | none</summary>
        public string SmtpSecurity = "starttls";
        public string SmtpUser = "";
        public string SmtpPassword = "";
        public string From = "";
        /// <summary>Addresses separated by ';' or ','.</summary>
        public string To = "";

        // jira
        public string JiraUrl = "";
        /// <summary>Jira Cloud: the account e-mail address. Jira Server / Data Center: empty (the token is a personal access token).</summary>
        public string JiraUser = "";
        public string JiraToken = "";
        public string JiraProject = "";
        public string JiraIssueType = "Task";
        public string JiraLabels = "watchkeeper";

        // webhook
        public string Url = "";
        /// <summary>Body with {title} {message} {level} {server} {time} {part}; empty = {"text": "title\nmessage"}.</summary>
        public string Body = "";

        public string Notes = "";
    }

    public class AlertSettings
    {
        /// <summary>Also write warnings and errors to the Windows Application event log.</summary>
        public bool EventLog = true;
        public List<AlertDestination> Destinations = new List<AlertDestination>();
    }
}
