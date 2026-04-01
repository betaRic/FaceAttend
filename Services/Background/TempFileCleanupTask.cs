using System;
using System.IO;
using System.Threading;
using System.Web.Hosting;
using FaceAttend.Services;

namespace FaceAttend.Services.Background
{
    /// <summary>
    /// Periodically deletes orphaned temp files from ~/App_Data/tmp/.
    /// Temp files accumulate when IIS crashes and finally blocks don't run.
    /// Uses IRegisteredObject so the cleanup thread shuts down cleanly on app pool stop.
    /// Config: TempFile:MaxAgeMinutes (default 30), TempFile:CleanupIntervalMinutes (default 60).
    /// Start from Global.asax Application_Start; Stop() is called automatically by IIS.
    /// </summary>
    public class TempFileCleanupTask : IRegisteredObject
    {
        private static TempFileCleanupTask _instance;
        private static readonly object _startLock = new object();

        private volatile bool _stopping = false;
        private readonly Thread _thread;

        // ─── Configuration ────────────────────────────────────────────────────────

        /// <summary>
        /// Minimum age before a temp file is considered orphaned and deleted. Default: 30 minutes.
        /// Normal scans complete in 2-5 seconds, so 30 minutes is very conservative.
        /// </summary>
        private static int MaxAgeMinutes =>
            ConfigurationService.GetInt("TempFile:MaxAgeMinutes", 30);

        /// <summary>How often to run cleanup. Default: 60 minutes.</summary>
        private static int CleanupIntervalMinutes =>
            ConfigurationService.GetInt("TempFile:CleanupIntervalMinutes", 60);

        // ─── Lifecycle ────────────────────────────────────────────────────────────

        private TempFileCleanupTask()
        {
            _thread = new Thread(RunLoop)
            {
                IsBackground = true,
                Name = "FaceAttend.TempFileCleanup"
            };
        }

        /// <summary>Starts the cleanup task. Safe to call multiple times.</summary>
        public static void Start()
        {
            lock (_startLock)
            {
                if (_instance != null) return;

                _instance = new TempFileCleanupTask();
                HostingEnvironment.RegisterObject(_instance);

                _instance._thread.Start();

                System.Diagnostics.Trace.TraceInformation(
                    "[TempFileCleanup] Background cleanup task na-start. " +
                    $"Interval: {CleanupIntervalMinutes}m, MaxAge: {MaxAgeMinutes}m.");
            }
        }

        /// <summary>Called by IIS on app pool shutdown. immediate=false: stop soon; immediate=true: stop now.</summary>
        public void Stop(bool immediate)
        {
            _stopping = true;
            _thread.Interrupt();

            if (!immediate)
                _thread.Join(TimeSpan.FromSeconds(5));

            HostingEnvironment.UnregisterObject(this);
        }

        /// <summary>
        /// Static wrapper so callers don't need to hold a reference to the singleton.
        /// No-op if the task was never started.
        /// </summary>
        public static void StopSingleton(bool immediate)
        {
            TempFileCleanupTask instance;
            lock (_startLock)
            {
                instance = _instance;
            }
            instance?.Stop(immediate);
        }

        // ─── Cleanup logic ────────────────────────────────────────────────────────

        private void RunLoop()
        {
            // Initial delay to avoid interfering with app startup.
            try { Thread.Sleep(TimeSpan.FromMinutes(5)); }
            catch (ThreadInterruptedException) { return; }

            while (!_stopping)
            {
                try
                {
                    CleanupOnce();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.TraceWarning(
                        "[TempFileCleanup] Cleanup error: " + ex.Message);
                }

                try
                {
                    Thread.Sleep(TimeSpan.FromMinutes(CleanupIntervalMinutes));
                }
                catch (ThreadInterruptedException)
                {
                    break; // App pool shutting down.
                }
            }
        }

        /// <summary>Deletes stale temp files from ~/App_Data/tmp/.</summary>
        private static void CleanupOnce()
        {
            var tmpDir = HostingEnvironment.MapPath("~/App_Data/tmp");
            if (string.IsNullOrWhiteSpace(tmpDir) || !Directory.Exists(tmpDir))
                return;

            var cutoff = DateTime.UtcNow.AddMinutes(-MaxAgeMinutes);
            int deleted = 0;
            int errors = 0;

            foreach (var file in Directory.GetFiles(tmpDir))
            {
                try
                {
                    var info = new FileInfo(file);

                    // LastWriteTimeUtc is most recent after SaveAs.
                    if (info.LastWriteTimeUtc > cutoff)
                        continue;

                    File.Delete(file);
                    deleted++;
                }
                catch (Exception ex)
                {
                    errors++;
                    System.Diagnostics.Trace.TraceWarning(
                        $"[TempFileCleanup] Cannot delete '{Path.GetFileName(file)}': " +
                        ex.Message);
                }
            }

            if (deleted > 0 || errors > 0)
            {
                System.Diagnostics.Trace.TraceInformation(
                    $"[TempFileCleanup] Done: {deleted} deleted, {errors} errors, cutoff={cutoff:HH:mm} UTC.");
            }
        }
    }
}