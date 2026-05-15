using System;
using System.IO;
using System.Linq;
using System.Web.Hosting;
using System.Web.Mvc;
using FaceAttend.Filters;
using FaceAttend.Models.ViewModels.Admin;
using FaceAttend.Services;
using FaceAttend.Services.Biometrics;

namespace FaceAttend.Areas.Admin.Controllers
{
    [AdminAuthorize]
    [RateLimit(Name = "AdminOperations", MaxRequests = 120, WindowSeconds = 60, Burst = 30)]
    public class OperationsController : Controller
    {
        [HttpGet]
        public ActionResult Index()
        {
            ViewBag.Title = "Operations";

            var health = HealthProbe.Check();
            var vm = new OperationsVm
            {
                DatabaseHealthy = health.Database,
                DatabaseError = health.DatabaseError,
                BiometricEngineReady = health.BiometricEngineReady,
                WarmUpState = health.WarmUpState,
                WarmUpMessage = health.WarmUpMessage,
                ModelVersion = health.ModelVersion,
                ModelIntegrityOk = health.ModelIntegrityOk,
                ModelHashesConfigured = health.ModelHashesConfigured,
                ModelAclOk = health.ModelAclOk,
                ModelRequireReadOnlyAcl = health.ModelRequireReadOnlyAcl,
                EngineEnabled = health.EngineEnabled,
                EngineHealthy = health.EngineHealthy,
                EngineReady = health.EngineReady,
                EngineStatus = health.EngineStatus,
                EngineRuntime = health.EngineRuntime,
                EngineAnalyzeSupported = health.EngineAnalyzeSupported,
                FaceMatcherCacheAgeSeconds = health.FaceMatcherCacheAgeSeconds,
                BiometricEngineStatus = BiometricEngine.GetStatus(),
                FaceMatcherStats = FastFaceMatcher.GetStats(),
                ScanMetrics = OperationalMetricsService.GetSnapshot(),
                ServerTimeLocal = TimeZoneHelper.NowLocal()
            };

            FillDisk(vm);
            FillCounts(vm);

            if (!vm.DatabaseHealthy) vm.Warnings.Add("Database readiness failed.");
            if (!health.WriteReady) vm.Warnings.Add("Database is not reporting READ_WRITE.");
            if (vm.MigrationStatus == null || !vm.MigrationStatus.Ok) vm.Warnings.Add("Database stabilization migrations are incomplete.");
            if (!vm.BiometricEngineReady) vm.Warnings.Add("Biometric engine is not scan-ready.");
            if (!vm.ModelIntegrityOk) vm.Warnings.Add("Model file hash/integrity check failed.");
            if (!vm.ModelHashesConfigured) vm.Warnings.Add("Model hashes are not pinned yet.");
            if (!vm.ModelAclOk) vm.Warnings.Add("Model files or folders are writable by the app identity; lock ACLs before production.");
            if (vm.EngineEnabled && !vm.EngineHealthy) vm.Warnings.Add("Biometric engine is enabled but not healthy.");
            if (vm.EngineHealthy && !vm.EngineAnalyzeSupported) vm.Warnings.Add("ONNX Runtime is available, but model-specific scan adapters are not implemented yet.");
            if (string.IsNullOrWhiteSpace(ConfigurationService.GetString("Kiosk:AllowedIpRanges", ""))) vm.Warnings.Add("Kiosk IP allowlist is empty; set Kiosk:AllowedIpRanges before controlled deployment.");
            if (vm.DiskFreeGb.HasValue && vm.DiskFreeGb.Value < 3.0) vm.Warnings.Add("Disk free space is below 3GB.");
            if (vm.TmpMb.HasValue && vm.TmpMb.Value > 500) vm.Warnings.Add("Temporary scan folder is above 500MB.");

            return View(vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RateLimit(Name = "AdminOperationsMutate", MaxRequests = 20, WindowSeconds = 60, Burst = 5)]
        public ActionResult ReloadFaceCache()
        {
            VisitorFaceIndex.Invalidate();
            FastFaceMatcher.ReloadFromDatabase();

            using (var db = new FaceAttendDBEntities())
            {
                AuditHelper.Log(db, Request, AuditHelper.ActionFaceCacheReload, "Operations", "FaceCache",
                    "Face cache reloaded from operations dashboard.");
            }

            TempData["msg"] = "Face cache reloaded.";
            TempData["msgKind"] = "success";
            return RedirectToAction("Index");
        }

        [HttpGet]
        [RateLimit(Name = "AdminOperationsMetrics", MaxRequests = 120, WindowSeconds = 60, Burst = 20)]
        public ActionResult MetricsJson()
        {
            var metrics = OperationalMetricsService.GetSnapshot();
            return Json(new
            {
                ok = true,
                totalScans = metrics.TotalScans,
                successCount = metrics.SuccessCount,
                failureCount = metrics.FailureCount,
                busyCount = metrics.BusyCount,
                averageMs = metrics.AverageMs,
                p50Ms = metrics.P50Ms,
                p95Ms = metrics.P95Ms,
                serverTimeLocal = TimeZoneHelper.NowLocal().ToString("HH:mm:ss")
            }, JsonRequestBehavior.AllowGet);
        }

        [HttpGet]
        [RateLimit(Name = "AdminEngineBenchmark", MaxRequests = 6, WindowSeconds = 60, Burst = 2)]
        public ActionResult EngineBenchmarkJson()
        {
            return Json(BiometricEngine.Benchmark(), JsonRequestBehavior.AllowGet);
        }

        [HttpGet]
        [RateLimit(Name = "AdminRiskAudit", MaxRequests = 10, WindowSeconds = 60, Burst = 2)]
        public ActionResult RiskyPairs(int? top)
        {
            ViewBag.Title = "Risky Pair Audit";
            using (var db = new FaceAttendDBEntities())
            {
                var maxRows = top.GetValueOrDefault(
                    ConfigurationService.GetInt("Biometrics:RiskAudit:MaxRows", 100));
                var audit = RiskyPairAuditService.Analyze(db, maxRows);
                return View(audit);
            }
        }

        [HttpGet]
        [RateLimit(Name = "AdminCalibrationExport", MaxRequests = 6, WindowSeconds = 60, Burst = 2)]
        public ActionResult CalibrationExport(int? riskyRows, int? attendanceRows)
        {
            using (var db = new FaceAttendDBEntities())
            {
                var bytes = CalibrationExportService.BuildCsv(
                    db,
                    Math.Max(10, Math.Min(1000, riskyRows.GetValueOrDefault(250))),
                    Math.Max(100, Math.Min(20000, attendanceRows.GetValueOrDefault(5000))));

                AuditHelper.Log(
                    db,
                    Request,
                    AuditHelper.ActionAttendanceExport,
                    "Calibration",
                    "ThresholdDataset",
                    "Exported biometric threshold calibration dataset.");

                return File(
                    bytes,
                    "text/csv",
                    "faceattend_threshold_calibration_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + ".csv");
            }
        }

        private static void FillCounts(OperationsVm vm)
        {
            try
            {
                using (var db = new FaceAttendDBEntities())
                {
                    vm.PendingReviews = db.AttendanceLogs.Count(x => !x.IsVoided && x.ReviewStatus == "PENDING");
                    vm.MigrationStatus = DatabaseMigrationStatusService.Check(db);
                    vm.CalibrationSummary = CalibrationSummaryService.Build(
                        db,
                        ConfigurationService.GetInt("Operations:CalibrationSummaryDays", 14));
                }
            }
            catch
            {
                vm.PendingReviews = -1;
            }
        }

        private static void FillDisk(OperationsVm vm)
        {
            try
            {
                var appData = HostingEnvironment.MapPath("~/App_Data");
                if (!string.IsNullOrWhiteSpace(appData) && Directory.Exists(appData))
                {
                    var drive = new DriveInfo(Path.GetPathRoot(appData));
                    vm.DiskFreeGb = Math.Round(drive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0), 2);
                }

                var tmp = HostingEnvironment.MapPath("~/App_Data/tmp");
                if (!string.IsNullOrWhiteSpace(tmp) && Directory.Exists(tmp))
                {
                    var bytes = Directory.GetFiles(tmp)
                        .Select(file =>
                        {
                            try { return new FileInfo(file).Length; }
                            catch { return 0L; }
                        })
                        .Sum();
                    vm.TmpMb = bytes / (1024 * 1024);
                }
            }
            catch
            {
                vm.Warnings.Add("Disk check failed.");
            }
        }
    }
}
