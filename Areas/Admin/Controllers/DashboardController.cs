using System;
using System.Linq;
using System.Web.Mvc;
using FaceAttend.Models.ViewModels.Admin;
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
                        vm.TotalEmployees = db.Employees.Count(e => e.Status == "ACTIVE");

                        var todayLocal = TimeZoneHelper.TodayLocalDate();
                        var todayRange = TimeZoneHelper.LocalDateRange(todayLocal);
                        var todayStart = todayRange.fromLocalInclusive;
                        var tomorrowStart = todayRange.toLocalExclusive;

                        vm.TodayTimeIns = db.AttendanceLogs.Count(l =>
                            !l.IsVoided &&
                            l.Timestamp >= todayStart && l.Timestamp < tomorrowStart && l.EventType == "IN");

                        vm.TodayTimeOuts = db.AttendanceLogs.Count(l =>
                            !l.IsVoided &&
                            l.Timestamp >= todayStart && l.Timestamp < tomorrowStart && l.EventType == "OUT");

                        vm.TotalVisitors = db.Visitors.Count(v => v.IsActive);
                        vm.PendingReviews = db.AttendanceLogs.Count(l => !l.IsVoided && l.ReviewStatus == "PENDING");

                        var rawLogs = db.AttendanceLogs
                            .Where(l => !l.IsVoided)
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
                            TimestampLocal = l.Timestamp,
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

                var engine = BiometricEngine.GetStatus();
                vm.BiometricEngineReady = engine.Ready;

                vm.OfflineAssetsOk = true;

                ViewBag.Title = "Dashboard";
                return View(vm);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("[Dashboard.Index] Unhandled error: " + ex.ToString());
                vm.DatabaseHealthy = false;
                ViewBag.Title = "Dashboard";
                return View(vm);
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
                    var todayRange = TimeZoneHelper.LocalDateRange(todayLocal);
                    var todayStart = todayRange.fromLocalInclusive;
                    var tomorrowStart = todayRange.toLocalExclusive;

                    var totalEmployees = db.Employees.Count(e => e.Status == "ACTIVE");
                    var todayIns = db.AttendanceLogs.Count(l =>
                        !l.IsVoided &&
                        l.Timestamp >= todayStart && l.Timestamp < tomorrowStart && l.EventType == "IN");
                    var todayOuts = db.AttendanceLogs.Count(l =>
                        !l.IsVoided &&
                        l.Timestamp >= todayStart && l.Timestamp < tomorrowStart && l.EventType == "OUT");
                    var visitors = db.Visitors.Count(v => v.IsActive);
                    var pending = db.AttendanceLogs.Count(l => !l.IsVoided && l.ReviewStatus == "PENDING");
                    var engine = BiometricEngine.GetStatus();

                    return Json(new
                    {
                        ok = true,
                        totalEmployees,
                        todayIns,
                        todayOuts,
                        totalVisitors = visitors,
                        pendingReviews = pending,
                        dbHealthy = true,
                        biometricEngineReady = engine.Ready,
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
        public ActionResult ClearFaceCache()
        {
            try
            {
                // Clear the in-memory face caches.
                VisitorFaceIndex.Invalidate();
                FastFaceMatcher.ReloadFromDatabase();
                
                TempData["msg"] = "Face cache cleared successfully. New enrollments can now proceed.";
                TempData["msgKind"] = "success";
            }
            catch (Exception ex)
            {
                TempData["msg"] = "Error clearing cache: " + ex.Message;
                TempData["msgKind"] = "danger";
            }
            return RedirectToAction("Index");
        }
        // FileSystemHelper now provides CheckFileExists; biometric readiness is owned by BiometricEngine.
    }
}
