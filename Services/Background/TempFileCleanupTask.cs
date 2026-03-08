using System;
using System.IO;
using System.Threading;
using System.Web.Hosting;
using FaceAttend.Services;

namespace FaceAttend.Services.Background
{
    /// <summary>
    /// Background na nag-cle-clean ng orphaned temp face image files.
    ///
    /// PHASE 3 FIX (P-06): Nag-iimplement ng periodic cleanup ng ~/App_Data/tmp/.
    ///
    /// PROBLEMA DATI:
    ///   Ang mga temp files (upload files ng kiosk scan) ay nililinis ng mga
    ///   finally blocks sa KioskController at ScanFramePipeline — kaya normal
    ///   na workflow ay maliit lang ang tmp folder.
    ///
    ///   PERO: Kapag nag-crash ang IIS process (power outage, OOM, app pool crash),
    ///   ang finally blocks ay HINDI natatakbo — naiiwan ang mga temp files.
    ///   Sa matagal na panahon, ang tmp folder ay lumalaki hanggang maubusan ng disk.
    ///   (Worst case WC-06: Disk full → lahat ng scans ay nag-fa-fail)
    ///
    /// SOLUSYON:
    ///   Isang IRegisteredObject background thread na tumatakbo bawat N minuto
    ///   at nagde-delete ng mga temp files na mas matanda sa MaxAgeMinutes.
    ///
    ///   Ginagamit ang IRegisteredObject para matiyak na ang cleanup thread ay
    ///   maayos na nasasara kapag mag-shutdown ang IIS app pool.
    ///
    /// PAANO GAMITIN:
    ///   Sa Global.asax Application_Start:
    ///     TempFileCleanupTask.Start();
    ///
    ///   Ang Stop() ay awtomatikong tinatawag ng IIS sa app pool shutdown.
    ///   Hindi na kailangan itawag sa Application_End.
    /// </summary>
    public class TempFileCleanupTask : IRegisteredObject
    {
        // Singleton — isang instance lang ang dapat mag-run sa isang pagkakataon.
        private static TempFileCleanupTask _instance;
        private static readonly object _startLock = new object();

        private volatile bool _stopping = false;
        private readonly Thread _thread;

        // ─── Configuration ────────────────────────────────────────────────────────

        /// <summary>
        /// Gaano katagal na bago ang isang temp file ay ituring na "orphaned"
        /// at pwedeng i-delete. Default: 30 minuto.
        /// Ang normal na scans ay umaagal ng 2-5 segundo — 30 minuto ay
        /// napaka-conservative.
        /// </summary>
        private static int MaxAgeMinutes =>
            AppSettings.GetInt("TempFile:MaxAgeMinutes", 30);

        /// <summary>
        /// Gaano kadalas mag-run ang cleanup. Default: 60 minuto.
        /// </summary>
        private static int CleanupIntervalMinutes =>
            AppSettings.GetInt("TempFile:CleanupIntervalMinutes", 60);

        // ─── Lifecycle ────────────────────────────────────────────────────────────

        private TempFileCleanupTask()
        {
            _thread = new Thread(RunLoop)
            {
                IsBackground = true,
                Name         = "FaceAttend.TempFileCleanup"
            };
        }

        /// <summary>
        /// Ini-start ang cleanup background task.
        /// Ligtas na tawagin ng maraming beses — ang double-check locking ay
        /// nagtitigarantiya na isa lang ang instance ang mag-start.
        /// </summary>
        public static void Start()
        {
            lock (_startLock)
            {
                if (_instance != null) return; // Already running

                _instance = new TempFileCleanupTask();

                // I-register sa ASP.NET para matiyak na ang Stop() ay tinatawag
                // ng IIS bago mag-shutdown ang app pool.
                HostingEnvironment.RegisterObject(_instance);

                _instance._thread.Start();

                System.Diagnostics.Trace.TraceInformation(
                    "[TempFileCleanup] Background cleanup task na-start. " +
                    $"Interval: {CleanupIntervalMinutes}m, MaxAge: {MaxAgeMinutes}m.");
            }
        }

        /// <summary>
        /// Tinatawag ng IIS sa app pool shutdown.
        /// Ang immediate parameter:
        ///   false = "please stop soon" — nagbibigay ng pagkakataon para matapos ang cleanup.
        ///   true  = "stop NOW" — forced shutdown na.
        /// </summary>
        public void Stop(bool immediate)
        {
            _stopping = true;
            _thread.Interrupt(); // Gisingin ang sleeping thread para makapag-exit agad.

            // Hintayin ang thread na matapos (max 5 segundo para sa graceful shutdown).
            if (!immediate)
                _thread.Join(TimeSpan.FromSeconds(5));

            HostingEnvironment.UnregisterObject(this);
        }

        // ─── Cleanup logic ────────────────────────────────────────────────────────

        private void RunLoop()
        {
            // Maghintay ng 5 minuto sa simula para hindi mag-interfere sa app startup.
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
                    // Hindi mag-crash ang background thread kahit nag-fail ang cleanup.
                    System.Diagnostics.Trace.TraceWarning(
                        "[TempFileCleanup] Error sa cleanup: " + ex.Message);
                }

                // Matulog hanggang sa susunod na interval.
                try
                {
                    Thread.Sleep(TimeSpan.FromMinutes(CleanupIntervalMinutes));
                }
                catch (ThreadInterruptedException)
                {
                    // Naka-interrupt — baka nag-shutdown na ang app pool. Lumabas na.
                    break;
                }
            }
        }

        /// <summary>
        /// Isang round ng cleanup — nagde-delete ng mga stale temp files.
        /// </summary>
        private static void CleanupOnce()
        {
            var tmpDir = HostingEnvironment.MapPath("~/App_Data/tmp");
            if (string.IsNullOrWhiteSpace(tmpDir) || !Directory.Exists(tmpDir))
                return;

            var cutoff  = DateTime.UtcNow.AddMinutes(-MaxAgeMinutes);
            int deleted = 0;
            int errors  = 0;

            foreach (var file in Directory.GetFiles(tmpDir))
            {
                try
                {
                    var info = new FileInfo(file);

                    // Skip kung hindi pa matanda ang file — baka ginagamit pa ito.
                    // Ginagamit natin ang LastWriteTimeUtc dahil ito ang pinaka-updated
                    // pagkatapos ng SaveAs.
                    if (info.LastWriteTimeUtc > cutoff)
                        continue;

                    // Ang tmp folder ay para sa temp files lang.
                    // Kapag stale na ang file, ligtas na itong alisin kahit ano pa ang extension.
                    File.Delete(file);
                    deleted++;
                }
                catch (Exception ex)
                {
                    errors++;
                    System.Diagnostics.Trace.TraceWarning(
                        $"[TempFileCleanup] Hindi ma-delete ang '{Path.GetFileName(file)}': " +
                        ex.Message);
                }
            }

            if (deleted > 0 || errors > 0)
            {
                System.Diagnostics.Trace.TraceInformation(
                    $"[TempFileCleanup] Natapos ang cleanup: {deleted} na-delete, " +
                    $"{errors} error, cutoff={cutoff:HH:mm} UTC.");
            }
        }
    }
}
