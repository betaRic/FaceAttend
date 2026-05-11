using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Hosting;
using System.Web.Mvc;
using FaceAttend.Filters;
using FaceAttend.Services;
using FaceAttend.Services.Biometrics;
using FaceAttend.Services.Security;

namespace FaceAttend.Controllers
{
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
        [AllowAnonymous]
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

            // Get face matcher stats
            var faceStats = FastFaceMatcher.GetStats();
            var modelIntegrity = ModelIntegrityService.Check();

            return Json(new
            {
                ok                   = ready,
                app                  = snap.App,
                database             = snap.Database,
                writeReady           = snap.WriteReady,
                databaseMigrations = new
                {
                    ok = snap.DatabaseMigrationsOk,
                    required = snap.DatabaseMigrationsRequired,
                    error = snap.DatabaseMigrationError,
                    biometricTemplatesTableExists = snap.BiometricTemplatesTableExists,
                    activeEmployeesMissingTemplates = snap.ActiveEmployeesMissingTemplates,
                    remainingDeviceTokenRows = snap.RemainingDeviceTokenRows
                },
                biometricWorkerReady = snap.BiometricWorkerReady,
                antiSpoofModelPresent = snap.AntiSpoofModelPresent,
                antiSpoofCircuitOpen  = snap.AntiSpoofCircuitOpen,
                antiSpoofCircuitStuck = snap.AntiSpoofCircuitStuck,
                warmUpState          = snap.WarmUpState,
                warmUpMessage        = snap.WarmUpMessage,
                modelVersion         = snap.ModelVersion,
                modelIntegrity = new
                {
                    ok = snap.ModelIntegrityOk,
                    expectedHashesConfigured = snap.ModelHashesConfigured,
                    aclOk = snap.ModelAclOk,
                    requireReadOnlyAcl = snap.ModelRequireReadOnlyAcl,
                    error = snap.ModelIntegrityError,
                    files = modelIntegrity.Files.Select(f => new
                    {
                        f.Name,
                        f.Exists,
                        f.Sha256,
                        f.ExpectedSha256,
                        f.Match,
                        f.AclLocked,
                        f.CurrentIdentityCanWriteFile,
                        f.CurrentIdentityCanWriteDirectory,
                        f.AclError
                    })
                },
                worker = new
                {
                    enabled = snap.WorkerEnabled,
                    healthy = snap.WorkerHealthy,
                    secretRequired = snap.WorkerSecretRequired,
                    secretConfigured = snap.WorkerSecretConfigured,
                    status = snap.WorkerStatus,
                    durationMs = snap.WorkerDurationMs
                },
                disk = new
                {
                    ok         = diskOk,
                    status     = diskStatus,
                    freeGb     = freeGb >= 0 ? (double?)Math.Round(freeGb, 2) : null,
                    tmpMb      = tmpFolderBytes >= 0 ? (long?)(tmpFolderBytes / (1024 * 1024)) : null,
                    warnThresholdGb  = DiskFreeWarningGb,
                    warnTmpThreshMb  = TmpFolderWarnBytes / (1024 * 1024)
                },
                faceIndex = new
                {
                    loaded     = faceStats?.IsInitialized ?? false,
                    employees = faceStats?.EmployeeCount ?? 0,
                    vectors   = faceStats?.TotalFaceVectors ?? 0,
                    memoryMb  = faceStats?.MemoryEstimateMB ?? 0,
                    cacheAgeSeconds = snap.FaceMatcherCacheAgeSeconds
                },
                security = new
                {
                    totpEnabled = AdminSessionService.IsTotpEnabled()
                },
                timestampUtc         = snap.TimestampUtc,
                error                = snap.Database ? null : snap.DatabaseError
            }, JsonRequestBehavior.AllowGet);
        }

        /// <summary>
        /// AntiSpoof endpoint — just confirms the worker process is alive.
        /// Does NOT check DB, disk, or models. Used by nginx upstream checks.
        /// </summary>
        [HttpGet]
        [Route("live")]
        [AllowAnonymous]
        [OutputCache(NoStore = true, Duration = 0, VaryByParam = "*")]
        public ActionResult Live()
        {
            return Json(new { ok = true, app = true }, JsonRequestBehavior.AllowGet);
        }

        /// <summary>
        /// Detailed diagnostics endpoint for troubleshooting warm-up failures.
        /// Shows step-by-step what has been initialized and what might be stuck.
        /// </summary>
        [HttpGet]
        [Route("diagnostics")]
        [AdminAuthorize]
        [OutputCache(NoStore = true, Duration = 0, VaryByParam = "*")]
        public ActionResult Diagnostics()
        {
            var policy = BiometricPolicy.Current;
            var modelIntegrity = ModelIntegrityService.Check();

            // Check database in detail
            object dbDetails;
            try
            {
                using (var db = new FaceAttendDBEntities())
                {
                    db.Database.Connection.Open();
                    using (var cmd = db.Database.Connection.CreateCommand())
                    {
                        cmd.CommandText = "SELECT @@VERSION";
                        var version = cmd.ExecuteScalar()?.ToString();
                        
                        cmd.CommandText = "SELECT COUNT(*) FROM Employees WHERE [Status] = 'ACTIVE'";
                        var empCount = Convert.ToInt32(cmd.ExecuteScalar());
                        
                        cmd.CommandText = "SELECT COUNT(*) FROM Visitors WHERE IsActive = 1";
                        var visitorCount = Convert.ToInt32(cmd.ExecuteScalar());
                        
                        dbDetails = new { ok = true, version, employeeCount = empCount, visitorCount };
                    }
                }
            }
            catch (Exception ex)
            {
                dbDetails = new { ok = false, error = ex.Message, errorType = ex.GetType().Name };
            }

            var biometricWorkerStatus = OpenVinoBiometrics.GetWorkerStatus();

            // Face matcher cache stats
            var matcherStats = FaceAttend.Services.Biometrics.FastFaceMatcher.GetStats();

            return Json(new
            {
                timestamp = DateTime.UtcNow,
                warmUp = new
                {
                    state = MvcApplication.WarmUpState,
                    stateName = MvcApplication.WarmUpState == 1 ? "COMPLETE" :
                                MvcApplication.WarmUpState == 0 ? "RUNNING" : "FAILED",
                    message = MvcApplication.WarmUpMessage
                },
                models = new
                {
                    detector = policy.DetectorModel,
                    recognizer = policy.RecognizerModel,
                    antiSpoof = policy.AntiSpoofModel,
                    modelVersion = policy.ModelVersion,
                    integrityOk = modelIntegrity.Ok,
                    files = modelIntegrity.Files.Select(f => new { f.Name, f.Exists, f.Match })
                },
                database = dbDetails,
                biometricWorker = biometricWorkerStatus,
                faceMatcher = new
                {
                    isInitialized    = matcherStats.IsInitialized,
                    lastLoaded       = matcherStats.LastLoaded,
                    employeeCount    = matcherStats.EmployeeCount,
                    totalFaceVectors = matcherStats.TotalFaceVectors,
                    memoryEstimateMB = matcherStats.MemoryEstimateMB
                },
                memory = new
                {
                    workingSetMb = GC.GetTotalMemory(false) / (1024 * 1024)
                }
            }, JsonRequestBehavior.AllowGet);
        }
    }
}
