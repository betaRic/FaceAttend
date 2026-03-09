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
        public ActionResult Index()
        {
            var vm = new DashboardViewModel();

            try
            {
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
                            l.Timestamp >= todayUtc && l.Timestamp < tomorrowUtc && l.EventType == "IN");

                        vm.TodayTimeOuts = db.AttendanceLogs.Count(l =>
                            l.Timestamp >= todayUtc && l.Timestamp < tomorrowUtc && l.EventType == "OUT");

                        vm.TotalVisitors = db.Visitors.Count(v => v.IsActive);
                        vm.PendingReviews = db.AttendanceLogs.Count(l => l.NeedsReview);

                        // FIX (CS0019): l.EmployeeId is int — materialize first,
                        // then convert int->string in C# memory (not inside EF LINQ).
                        // EF6 cannot translate .ToString() to SQL.
                        var rawLogs = db.AttendanceLogs
                            .OrderByDescending(l => l.Timestamp)
                            .Take(10)
                            .Select(l => new
                            {
                                l.Id,
                                l.Timestamp,
                                l.EmployeeId,
                                l.EmployeeFullName,
                                l.EventType,
                                l.OfficeName,
                                l.NeedsReview,
                            })
                            .ToList();

                        vm.RecentLogs = rawLogs.Select(l => new RecentAttendanceRow
                        {
                            Id = l.Id,
                            TimestampUtc = l.Timestamp,
                            EmployeeId = l.EmployeeId.ToString(),
                            EmployeeFullName = l.EmployeeFullName ?? "",
                            EventType = l.EventType,
                            OfficeName = l.OfficeName ?? "",
                            NeedsReview = l.NeedsReview,
                        }).ToList();

                        vm.DatabaseHealthy = true;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.TraceError("[Dashboard.Index] DB error: " + ex.Message);
                    vm.DatabaseHealthy = false;
                }

                vm.LivenessModelLoaded = CheckFileExists(
                    AppSettings.GetString("Biometrics:LivenessModelPath",
                        "~/App_Data/models/liveness/minifasnet.onnx"));

                vm.DlibModelsLoaded = CheckDlibModelsPresent(
                    AppSettings.GetString("Biometrics:DlibModelsDir",
                        "~/App_Data/models/dlib"));

                vm.OfflineAssetsOk = true;

                try
                {
                    var circuitState = OnnxLiveness.GetCircuitState();
                    vm.LivenessCircuitOpen = circuitState.IsOpen;
                    vm.LivenessCircuitStuck = circuitState.IsStuck;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.TraceError("[Dashboard.Index] Liveness health error: " + ex);
                    vm.LivenessCircuitOpen = false;
                    vm.LivenessCircuitStuck = true;
                }

                ViewBag.Title = "Dashboard";
                return View(vm);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("[Dashboard.Index] Unhandled error: " + ex.ToString());
                TempData["msg"] = "Dashboard error. Details written to application log.";
                TempData["msgKind"] = "danger";
                Response.StatusCode = 500;
                return RedirectToAction("Index", "Error", new { area = "Admin" });
            }
        }

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
                        l.Timestamp >= todayUtc && l.Timestamp < tomorrowUtc && l.EventType == "IN");
                    var todayOuts = db.AttendanceLogs.Count(l =>
                        l.Timestamp >= todayUtc && l.Timestamp < tomorrowUtc && l.EventType == "OUT");
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
                var dat = System.IO.Directory.GetFiles(abs, "*.dat", System.IO.SearchOption.TopDirectoryOnly);
                return dat.Length >= 2;
            }
            catch { return false; }
        }
    }
}