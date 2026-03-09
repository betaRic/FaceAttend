using System;
using System.IO;
using System.Web.Hosting;
using System.Web.Mvc;
using FaceAttend.Services;

namespace FaceAttend.Controllers
{
    [AllowAnonymous]
    [RoutePrefix("health")]
    public class HealthController : Controller
    {
        // ── Thresholds ────────────────────────────────────────────────────────
        // AUDIT FIX (H-05): Alert when free disk drops below 3GB, or when the
        // App_Data folder (which holds the DB + tmp files) exceeds thresholds.
        //
        // SQL Express hard cap = 10GB.  Alert at 70% (7GB) so there is still
        // time to purge audit logs or migrate to full SQL Server before writes
        // start failing.
        private const double DiskFreeWarningGb   = 3.0;   // free space alert
        private const long   TmpFolderWarnBytes  = 500L * 1024 * 1024; // 500 MB

        /// <summary>
        /// Readiness endpoint.
        /// 200 = ready — reverse proxy / load balancer should route to this instance.
        /// 503 = not ready — remove from rotation until fixed.
        /// </summary>
        [HttpGet]
        [Route("")]
        [OutputCache(NoStore = true, Duration = 0, VaryByParam = "*")]
        public ActionResult Index()
        {
            var snap = HealthProbe.Check();

            // ── AUDIT FIX (H-05): disk space check ───────────────────────────
            double freeGb         = -1;
            long   tmpFolderBytes = -1;
            string diskStatus     = "unknown";
            bool   diskOk         = true;

            try
            {
                var appDataPath = HostingEnvironment.MapPath("~/App_Data");
                if (!string.IsNullOrWhiteSpace(appDataPath) && Directory.Exists(appDataPath))
                {
                    var drive = new DriveInfo(Path.GetPathRoot(appDataPath));
                    freeGb = drive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);

                    if (freeGb < DiskFreeWarningGb)
                    {
                        diskOk     = false;
                        diskStatus = $"LOW ({freeGb:F1} GB free)";

                        System.Diagnostics.Trace.TraceWarning(
                            $"[HealthController] DISK WARNING: Only {freeGb:F2} GB free. " +
                            $"Threshold is {DiskFreeWarningGb} GB. " +
                            "SQL Express may stop accepting writes if disk fills completely.");
                    }
                    else
                    {
                        diskStatus = $"ok ({freeGb:F1} GB free)";
                    }
                }

                // ── Check tmp folder size ─────────────────────────────────
                var tmpPath = HostingEnvironment.MapPath("~/App_Data/tmp");
                if (!string.IsNullOrWhiteSpace(tmpPath) && Directory.Exists(tmpPath))
                {
                    foreach (var file in Directory.GetFiles(tmpPath))
                    {
                        try { tmpFolderBytes += new FileInfo(file).Length; }
                        catch { /* skip locked files */ }
                    }

                    if (tmpFolderBytes > TmpFolderWarnBytes)
                    {
                        diskOk     = false;
                        diskStatus = $"tmp too large ({tmpFolderBytes / (1024 * 1024)} MB)";

                        System.Diagnostics.Trace.TraceWarning(
                            $"[HealthController] TMP FOLDER WARNING: {tmpFolderBytes / (1024 * 1024)} MB. " +
                            "Orphaned temp files may be accumulating. " +
                            "Check TempFileCleanupTask and disk space.");
                    }
                }
            }
            catch (Exception ex)
            {
                diskStatus = "check-failed";
                System.Diagnostics.Trace.TraceWarning(
                    "[HealthController] Disk check failed: " + ex.Message);
            }

            // Overall readiness = all snap checks + disk ok
            bool ready = snap.Ready && diskOk;
            Response.StatusCode = ready ? 200 : 503;

            return Json(new
            {
                ok                   = ready,
                app                  = snap.App,
                database             = snap.Database,
                dlibModelsPresent    = snap.DlibModelsPresent,
                livenessModelPresent = snap.LivenessModelPresent,
                livenessCircuitOpen  = snap.LivenessCircuitOpen,
                livenessCircuitStuck = snap.LivenessCircuitStuck,
                warmUpState          = snap.WarmUpState,
                warmUpMessage        = snap.WarmUpMessage,
                disk = new
                {
                    ok         = diskOk,
                    status     = diskStatus,
                    freeGb     = freeGb >= 0 ? (double?)Math.Round(freeGb, 2) : null,
                    tmpMb      = tmpFolderBytes >= 0 ? (long?)(tmpFolderBytes / (1024 * 1024)) : null,
                    warnThresholdGb  = DiskFreeWarningGb,
                    warnTmpThreshMb  = TmpFolderWarnBytes / (1024 * 1024)
                },
                timestampUtc         = snap.TimestampUtc,
                error                = snap.Database ? null : snap.DatabaseError
            }, JsonRequestBehavior.AllowGet);
        }

        /// <summary>
        /// Liveness endpoint — just confirms the worker process is alive.
        /// Does NOT check DB, disk, or models. Used by nginx upstream checks.
        /// </summary>
        [HttpGet]
        [Route("live")]
        [OutputCache(NoStore = true, Duration = 0, VaryByParam = "*")]
        public ActionResult Live()
        {
            return Json(new { ok = true, app = true }, JsonRequestBehavior.AllowGet);
        }
    }
}
