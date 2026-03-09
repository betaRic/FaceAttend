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
        // PHASE 2 FIX (P-03): Tinanggal ang 60-segundo full page reload.
        // Ang page ay naglo-load ng isang beses lang.
        // Ang KPI numbers ay ina-update ng AJAX (tingnan ang KpiJson action).
        // ====================================================================
        public ActionResult Index()
        {
            var vm = new DashboardViewModel();

            // Kung may error sa DB, mag-render pa rin ng page pero may warning.
            try
            {
                using (var db = new FaceAttendDBEntities())
                {
                    // Bilang ng aktibong empleyado.
                    vm.TotalEmployees = db.Employees.Count(e => e.IsActive);

                    // Gamitin ang PH local date bago i-convert sa UTC range.
                    // HINDI tama ang DateTime.UtcNow.Date para sa dashboard cards
                    // dahil ang PHT midnight ay 4:00 PM UTC ng nakaraang araw.
                    var todayLocal  = TimeZoneHelper.TodayLocalDate();
                    var todayRange  = TimeZoneHelper.LocalDateToUtcRange(todayLocal);
                    var todayUtc    = todayRange.fromUtc;
                    var tomorrowUtc = todayRange.toUtcExclusive;

                    vm.TodayTimeIns = db.AttendanceLogs.Count(l =>
                        l.Timestamp >= todayUtc &&
                        l.Timestamp <  tomorrowUtc &&
                        l.EventType == "IN");

                    vm.TodayTimeOuts = db.AttendanceLogs.Count(l =>
                        l.Timestamp >= todayUtc &&
                        l.Timestamp <  tomorrowUtc &&
                        l.EventType == "OUT");

                    vm.TotalVisitors  = db.Visitors.Count(v => v.IsActive);
                    vm.PendingReviews = db.AttendanceLogs.Count(l => l.NeedsReview);

                    // Pinakabagong 10 attendance records.
                    vm.RecentLogs =
                        (from l in db.AttendanceLogs
                         join e in db.Employees on l.EmployeeId equals e.Id into ej
                         from e in ej.DefaultIfEmpty()
                         orderby l.Timestamp descending
                         select new RecentAttendanceRow
                         {
                             Id               = l.Id,
                             TimestampUtc     = l.Timestamp,
                             EmployeeId       = (e != null ? e.EmployeeId : ""),
                             EmployeeFullName = l.EmployeeFullName,
                             EventType        = l.EventType,
                             OfficeName       = l.OfficeName,
                             NeedsReview      = l.NeedsReview
                         })
                        .Take(10)
                        .ToList();

                    vm.DatabaseHealthy = true;
                }
            }
            catch (Exception ex)
            {
                // I-log ang error — hindi na anonymous catch.
                System.Diagnostics.Trace.TraceError(
                    "[Dashboard.Index] DB error: " + ex.Message);
                vm.DatabaseHealthy = false;
            }

            // Liveness model at Dlib models — file check lang, hindi naglo-load.
            vm.LivenessModelLoaded = CheckFileExists(
                AppSettings.GetString("Biometrics:LivenessModelPath",
                    "~/App_Data/models/liveness/minifasnet.onnx"));

            vm.DlibModelsLoaded = CheckDlibModelsPresent(
                AppSettings.GetString("Biometrics:DlibModelsDir",
                    "~/App_Data/models/dlib"));

            vm.OfflineAssetsOk = true;

            // PHASE 2 FIX: Circuit breaker status para sa dashboard health card.
            var circuitState = OnnxLiveness.GetCircuitState();
            vm.LivenessCircuitOpen  = circuitState.IsOpen;
            vm.LivenessCircuitStuck = circuitState.IsStuck;

            ViewBag.Title = "Dashboard";
            return View(vm);
        }

        // ====================================================================
        // KPI JSON — Lightweight endpoint para sa AJAX refresh ng dashboard
        //
        // PHASE 2 FIX (P-03): Palitan ang 60-segundo full page reload.
        // Tinatawag ito ng JavaScript bawat 2 minuto para i-update ang mga
        // KPI numbers nang HINDI nagre-reload ng buong page.
        //
        // Mga query: 5 COUNT queries lang — napakamura sa DB.
        // Hindi kasama ang RecentLogs — para sa full refresh ng logs,
        // kailangan ng manual na click ng admin.
        // ====================================================================
        [HttpGet]
        public ActionResult KpiJson()
        {
            try
            {
                using (var db = new FaceAttendDBEntities())
                {
                    var todayLocal  = TimeZoneHelper.TodayLocalDate();
                    var todayRange  = TimeZoneHelper.LocalDateToUtcRange(todayLocal);
                    var todayUtc    = todayRange.fromUtc;
                    var tomorrowUtc = todayRange.toUtcExclusive;

                    var totalEmployees = db.Employees.Count(e => e.IsActive);
                    var todayIns       = db.AttendanceLogs.Count(l =>
                        l.Timestamp >= todayUtc && l.Timestamp < tomorrowUtc &&
                        l.EventType == "IN");
                    var todayOuts      = db.AttendanceLogs.Count(l =>
                        l.Timestamp >= todayUtc && l.Timestamp < tomorrowUtc &&
                        l.EventType == "OUT");
                    var visitors       = db.Visitors.Count(v => v.IsActive);
                    var pending        = db.AttendanceLogs.Count(l => l.NeedsReview);

                    var circuit = OnnxLiveness.GetCircuitState();

                    return Json(new
                    {
                        ok             = true,
                        totalEmployees,
                        todayIns,
                        todayOuts,
                        totalVisitors  = visitors,
                        pendingReviews = pending,
                        dbHealthy      = true,
                        livenessCircuitOpen  = circuit.IsOpen,
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
        // LIVENESS CIRCUIT RESET — Para sa admin kung nag-open ang circuit breaker
        //
        // PHASE 2 FIX (WC-07): Admin action para i-reset ang circuit breaker
        // nang hindi kailangan ng IIS app pool recycle.
        // ====================================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult ResetLivenessCircuit()
        {
            try
            {
                OnnxLiveness.ResetCircuit();
                TempData["msg"]     = "Liveness circuit breaker na-reset. Maaari na ulit mag-scan.";
                TempData["msgKind"] = "success";
            }
            catch (Exception ex)
            {
                TempData["msg"]     = "Error: " + ex.Message;
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
                // AYOS: System.IO.File.Exists — hindi Controller.File()
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
                // Directory.Exists ay OK dito — walang ambiguity sa Controller.
                if (string.IsNullOrEmpty(abs) || !System.IO.Directory.Exists(abs)) return false;

                // Kailangan ng dalawang model files para gumana ang Dlib.
                var dat = System.IO.Directory.GetFiles(
                    abs, "*.dat", System.IO.SearchOption.TopDirectoryOnly);
                return dat.Length >= 2;
            }
            catch { return false; }
        }

    }
}
