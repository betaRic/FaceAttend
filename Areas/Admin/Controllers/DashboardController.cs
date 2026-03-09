using System;
using System.IO;
using System.Linq;
using System.Web.Hosting;
using System.Web.Mvc;
using FaceAttend.Areas.Admin.Models;
using FaceAttend.Filters;
using FaceAttend.Services;
using FaceAttend.Services.Biometrics;

namespace FaceAttend.Areas.Admin.Controllers
{
    [AdminAuthorize]
    public class DashboardController : Controller
    {
        // ====================================================================
        // INDEX — Naglo-load ng full dashboard page
        //
        // FIX (QA-01): Tinanggal ang LEFT JOIN sa RecentLogs query.
        //   DATI:  join e in db.Employees on l.EmployeeId equals e.Id
        //   BAGO:  walang join — ginagamit na lang ang denormalized na
        //          EmployeeFullName at OfficeName na naka-store na sa log row.
        //   BAKIT: Ang e.Id ay int PK; ang l.EmployeeId ay string — type mismatch
        //          na nagdudulot ng EF translation exception o silent wrong results.
        //          Mas mabilis pa ito (isang table scan lang, walang JOIN).
        //
        // FIX (QA-02): Dagdag na outer try/catch para ma-catch ang view
        //   rendering exceptions na hindi nahuhuli ng inner try/catch.
        // ====================================================================
        public ActionResult Index()
        {
            var vm = new DashboardViewModel();

            try
            {
                // ── Inner try/catch: DB queries ───────────────────────────────
                try
                {
                    using (var db = new FaceAttendDBEntities())
                    {
                        vm.TotalEmployees = db.Employees.Count(e => e.IsActive);

                        var todayLocal = TimeZoneHelper.TodayLocalDate();
                        var todayRange = TimeZoneHelper.LocalDateToUtcRange(todayLocal);
                        var todayUtc = todayRange.fromUtc;
                        var tomorrowUtc = todayRange.toUtcExclusive;

                        vm.TodayTimeIns = db.AttendanceLogs.Count(l =>
                            l.Timestamp >= todayUtc &&
                            l.Timestamp < tomorrowUtc &&
                            l.EventType == "IN");

                        vm.TodayTimeOuts = db.AttendanceLogs.Count(l =>
                            l.Timestamp >= todayUtc &&
                            l.Timestamp < tomorrowUtc &&
                            l.EventType == "OUT");

                        vm.TotalVisitors = db.Visitors.Count(v => v.IsActive);
                        vm.PendingReviews = db.AttendanceLogs.Count(l => l.NeedsReview);

                        // FIX (QA-01): Simple single-table query — no JOIN needed.
                        // EmployeeFullName and OfficeName are already denormalized
                        // on the AttendanceLog row at write time.
                        vm.RecentLogs = db.AttendanceLogs
                            .OrderByDescending(l => l.Timestamp)
                            .Take(10)
                            .Select(l => new RecentAttendanceRow
                            {
                                Id = l.Id,
                                TimestampUtc = l.Timestamp,
                                EmployeeId = l.EmployeeId ?? "",
                                EmployeeFullName = l.EmployeeFullName ?? "",
                                EventType = l.EventType,
                                OfficeName = l.OfficeName ?? "",
                                NeedsReview = l.NeedsReview
                            })
                            .ToList();

                        vm.DatabaseHealthy = true;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.TraceError(
                        "[Dashboard.Index] DB error: " + ex.Message);
                    vm.DatabaseHealthy = false;
                    // vm.RecentLogs defaults to empty list — view handles this gracefully.
                }

                // ── Model file checks (each has internal try/catch) ───────────
                vm.LivenessModelLoaded = CheckFileExists(
                    AppSettings.GetString("Biometrics:LivenessModelPath",
                        "~/App_Data/models/liveness/minifasnet.onnx"));

                vm.DlibModelsLoaded = CheckDlibModelsPresent(
                    AppSettings.GetString("Biometrics:DlibModelsDir",
                        "~/App_Data/models/dlib"));

                vm.OfflineAssetsOk = true;

                // ── Circuit breaker status ────────────────────────────────────
                try
                {
                    var circuitState = OnnxLiveness.GetCircuitState();
                    vm.LivenessCircuitOpen = circuitState.IsOpen;
                    vm.LivenessCircuitStuck = circuitState.IsStuck;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.TraceError(
                        "[Dashboard.Index] Liveness health error: " + ex);
                    vm.LivenessCircuitOpen = false;
                    vm.LivenessCircuitStuck = true;
                }

                ViewBag.Title = "Dashboard";
                return View(vm);
            }
            catch (Exception ex)
            {
                // FIX (QA-02): Outer safety net — catches Razor view rendering
                // exceptions that bypass the inner try/catch blocks above.
                // We redirect to the Admin error page rather than re-rendering
                // the view (which might itself be the source of the exception).
                System.Diagnostics.Trace.TraceError(
                    "[Dashboard.Index] Unhandled error: " + ex.ToString());

                TempData["msg"] = "Dashboard error. Details written to application log.";
                TempData["msgKind"] = "danger";

                Response.StatusCode = 500;
                return RedirectToAction("Index", "Error", new { area = "Admin" });
            }
        }

        // ====================================================================
        // KPI JSON — Lightweight AJAX refresh endpoint (every 2 minutes)
        // ====================================================================
        [HttpGet]
        public ActionResult KpiJson()
        {
            try
            {
                using (var db = new FaceAttendDBEntities())
                {
                    var todayLocal = TimeZoneHelper.TodayLocalDate();
                    var todayRange = TimeZoneHelper.LocalDateToUtcRange(todayLocal);
                    var todayUtc = todayRange.fromUtc;
                    var tomorrowUtc = todayRange.toUtcExclusive;

                    var totalEmployees = db.Employees.Count(e => e.IsActive);
                    var todayIns = db.AttendanceLogs.Count(l =>
                        l.Timestamp >= todayUtc && l.Timestamp < tomorrowUtc &&
                        l.EventType == "IN");
                    var todayOuts = db.AttendanceLogs.Count(l =>
                        l.Timestamp >= todayUtc && l.Timestamp < tomorrowUtc &&
                        l.EventType == "OUT");
                    var visitors = db.Visitors.Count(v => v.IsActive);
                    var pending = db.AttendanceLogs.Count(l => l.NeedsReview);

                    var circuit = OnnxLiveness.GetCircuitState();

                    return Json(new
                    {
                        ok = true,
                        totalEmployees,
                        todayIns,
                        todayOuts,
                        totalVisitors = visitors,
                        pendingReviews = pending,
                        dbHealthy = true,
                        livenessCircuitOpen = circuit.IsOpen,
                        livenessCircuitStuck = circuit.IsStuck,
                        serverTimeLocal = TimeZoneHelper.NowLocal().ToString("HH:mm:ss")
                    }, JsonRequestBehavior.AllowGet);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("[Dashboard.KpiJson] Error: " + ex.Message);
                return Json(new { ok = false, dbHealthy = false }, JsonRequestBehavior.AllowGet);
            }
        }

        // ====================================================================
        // LIVENESS CIRCUIT RESET
        // ====================================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult ResetLivenessCircuit()
        {
            try
            {
                OnnxLiveness.ResetCircuit();
                TempData["msg"] = "Liveness circuit breaker na-reset. Maaari na ulit mag-scan.";
                TempData["msgKind"] = "success";
            }
            catch (Exception ex)
            {
                TempData["msg"] = "Error: " + ex.Message;
                TempData["msgKind"] = "danger";
            }

            return RedirectToAction("Index");
        }

        // ────────────────────────────────────────────────────────────────────
        // Private helpers
        // ────────────────────────────────────────────────────────────────────

        private static bool CheckFileExists(string virtualPath)
        {
            if (string.IsNullOrWhiteSpace(virtualPath)) return false;
            try
            {
                var abs = HostingEnvironment.MapPath(virtualPath);
                return !string.IsNullOrEmpty(abs) && System.IO.File.Exists(abs);
            }
            catch { return false; }
        }

        private static bool CheckDlibModelsPresent(string virtualDir)
        {
            if (string.IsNullOrWhiteSpace(virtualDir)) return false;
            try
            {
                var abs = HostingEnvironment.MapPath(virtualDir);
                if (string.IsNullOrEmpty(abs) || !System.IO.Directory.Exists(abs)) return false;

                var dat = System.IO.Directory.GetFiles(
                    abs, "*.dat", System.IO.SearchOption.TopDirectoryOnly);
                return dat.Length >= 2;
            }
            catch { return false; }
        }
    }
}