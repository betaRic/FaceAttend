using System;
using System.IO;
using System.Threading;
using System.Web.Hosting;
using FaceAttend.Services;

namespace FaceAttend.Services.Background
{
    public class TempFileCleanupTask : IRegisteredObject
    {
        private static TempFileCleanupTask _instance;
        private static readonly object _startLock = new object();

        private volatile bool _stopping = false;
        private readonly Thread _thread;

        private static int MaxAgeMinutes =>
            ConfigurationService.GetInt("TempFile:MaxAgeMinutes", 30);

        private static int CleanupIntervalMinutes =>
            ConfigurationService.GetInt("TempFile:CleanupIntervalMinutes", 60);

        private TempFileCleanupTask()
        {
            _thread = new Thread(RunLoop)
            {
                IsBackground = true,
                Name = "FaceAttend.TempFileCleanup"
            };
        }

        public static void Start()
        {
            lock (_startLock)
            {
                if (_instance != null) return;

                _instance = new TempFileCleanupTask();
                HostingEnvironment.RegisterObject(_instance);

                _instance._thread.Start();

                System.Diagnostics.Trace.TraceInformation(
                    "[TempFileCleanup] Background cleanup started. " +
                    $"Interval: {CleanupIntervalMinutes}m, MaxAge: {MaxAgeMinutes}m.");
            }
        }

        public void Stop(bool immediate)
        {
            _stopping = true;
            _thread.Interrupt();

            if (!immediate)
                _thread.Join(TimeSpan.FromSeconds(5));

            HostingEnvironment.UnregisterObject(this);
        }

        public static void StopSingleton(bool immediate)
        {
            TempFileCleanupTask instance;
            lock (_startLock)
            {
                instance = _instance;
            }
            instance?.Stop(immediate);
        }

        private void RunLoop()
        {
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
                    break;
                }
            }
        }

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
