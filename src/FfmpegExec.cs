// Runs one ffmpeg command either in the service session or in the signed-in user's session (Intel Quick Sync
// often only works there). Either way ffmpeg's progress (-progress pipe:1) and messages come back through
// pipes handed only to that process: nothing is written to, read from or deleted in the user's folders by the
// service. In the service session ffmpeg runs with a restricted token (no privileges, Administrators denied).
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace Watchkeeper
{
    public class FfmpegRunResult
    {
        public int ExitCode = -1;
        public string Errors = "";       // ffmpeg's messages (warnings and errors), banner removed
        public bool TimedOut;
        public bool Aborted;
        public string StartError;        // non-null: ffmpeg could not be started at all
    }

    public static class FfmpegExec
    {
        public static FfmpegRunResult Run(ILauncher launcher, ISessions sessions, string ffmpeg, List<string> args,
                                          bool userSession, string priority, int timeoutMs,
                                          Action<string> onProgress, Func<bool> abortRequested, Action<IRunning> onStarted)
        {
            return RunCaptured(launcher, ffmpeg, args, userSession, priority, timeoutMs, onProgress, abortRequested, onStarted);
        }

        static bool Wait(IRunning p, int timeoutMs, Func<bool> abortRequested, Action tick, FfmpegRunResult r)
        {
            DateTime end = timeoutMs > 0 ? DateTime.UtcNow.AddMilliseconds(timeoutMs) : DateTime.MaxValue;
            while (!p.WaitForExit(300))
            {
                if (tick != null) tick();
                if (abortRequested != null && abortRequested()) { r.Aborted = true; break; }
                if (DateTime.UtcNow > end) { r.TimedOut = true; break; }
            }
            if (r.Aborted || r.TimedOut)
            {
                p.Kill();
                p.WaitForExit(5000);
                return false;
            }
            return true;
        }

        static FfmpegRunResult RunCaptured(ILauncher launcher, string ffmpeg, List<string> args, bool userSession, string priority, int timeoutMs,
                                          Action<string> onProgress, Func<bool> abortRequested, Action<IRunning> onStarted)
        {
            FfmpegRunResult r = new FfmpegRunResult();
            if (!userSession)
            {
                string why = PathTrust.Check(ffmpeg);
                if (why != null) { r.StartError = "ffmpeg not run: " + why; return r; }
            }
            StartRequest req = new StartRequest();
            req.File = ffmpeg;
            req.Arguments = Util.JoinArgs(args);
            req.Redirect = true;
            req.Hidden = true;
            req.UserSession = userSession;
            req.Restricted = !userSession;
            req.Priority = priority;
            IRunning p;
            try { p = launcher.Start(req); }
            catch (Exception ex)
            {
                r.StartError = (userSession ? "cannot start ffmpeg in the user session: " : "cannot start ffmpeg (" + ffmpeg + "): ") + ex.Message;
                return r;
            }
            using (p)
            {
                if (onStarted != null) onStarted(p);
                if (p.StdOut == null || p.StdErr == null) throw new InvalidOperationException("ffmpeg was started without its output pipes");
                Queue<string> tail = new Queue<string>();
                Thread errReader = new Thread(delegate()
                {
                    try
                    {
                        string line;
                        while ((line = p.StdErr.ReadLine()) != null)
                        {
                            if (line.Trim().Length == 0) continue;
                            lock (tail)
                            {
                                tail.Enqueue(line.TrimEnd());
                                while (tail.Count > 60) tail.Dequeue();
                            }
                        }
                    }
                    catch (Exception) { }
                });
                errReader.IsBackground = true;
                errReader.Start();
                Thread outReader = new Thread(delegate()
                {
                    try
                    {
                        string line;
                        while ((line = p.StdOut.ReadLine()) != null)
                            if (onProgress != null) onProgress(line);
                    }
                    catch (Exception) { }
                });
                outReader.IsBackground = true;
                outReader.Start();

                bool exited = Wait(p, timeoutMs, abortRequested, null, r);
                outReader.Join(3000);
                errReader.Join(3000);
                if (exited) r.ExitCode = p.ExitCode;
                string[] lines;
                lock (tail) lines = tail.ToArray();
                r.Errors = Converter.StripBanner(string.Join("\n", lines)).Trim();
            }
            return r;
        }

    }
}
