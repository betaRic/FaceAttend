using System;
using System.IO;
using System.Linq;
using System.Web.Hosting;
using System.Web.Mvc;
using FaceAttend.Areas.Admin.Models;
using FaceAttend.Filters;
using FaceAttend.Services;

namespace FaceAttend.Areas.Admin.Controllers
{
    [AdminAuthorize]
    public class DashboardController : Controller
    {
        // P4-F2: Load real data into DashboardViewModel.
        // The original controller returned View() with no model — every KPI card
        // showed "-" and the recent logs table showed "No records yet." permanently.
        public ActionResult Index()
        {
            var vm = new DashboardViewModel();

            // ----------------------------------------------------------------
            // Database queries — wrapped in a single try/catch so a DB failure
            // degrades gracefully: the dashboard loads with zeros and
            // DatabaseHealthy = false rather than throwing a 500.
            // ----------------------------------------------------------------
            try
            {
                using (var db = new FaceAttendDBEntities())
                {
                    // KPI: active employee count.
                    vm.TotalEmployees = db.Employees.Count(e => e.IsActive);

                    // KPI: today's time-ins and time-outs.
                    // AttendanceLog.Timestamp is stored as UTC (set by KioskController).
                    // Compare against today's UTC midnight so the count matches the
                    // server's local date when the timezone offset is small, and is
                    // unambiguous on servers in any timezone.
                    var todayUtc = DateTime.UtcNow.Date;
                    var tomorrowUtc = todayUtc.AddDays(1);

                    vm.TodayTimeIns = db.AttendanceLogs.Count(l =>
                        l.Timestamp >= todayUtc &&
                        l.Timestamp <  tomorrowUtc &&
                        l.EventType == "IN");

                    vm.TodayTimeOuts = db.AttendanceLogs.Count(l =>
                        l.Timestamp >= todayUtc &&
                        l.Timestamp <  tomorrowUtc &&
                        l.EventType == "OUT");

                    // KPI: known (active) visitor profiles.
                    vm.TotalVisitors = db.Visitors.Count(v => v.IsActive);

                    // KPI: attendance logs pending manual review.
                    vm.PendingReviews = db.AttendanceLogs.Count(l => l.NeedsReview);

                    // Recent attendance — last 10 records across all employees,
                    // newest first. Projected to the slim RecentAttendanceRow DTO
                    // so EF doesn't load the entire entity graph.
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
            catch
            {
                // Non-fatal: dashboard renders with zeros and red DB health indicator.
                vm.DatabaseHealthy = false;
            }

            // ----------------------------------------------------------------
            // System health checks — file presence only, no model loading.
            // These are fast synchronous checks safe to run on every page load.
            // ----------------------------------------------------------------
            vm.LivenessModelLoaded = CheckFileExists(
                AppSettings.GetString("Biometrics:LivenessModelPath",
                    "~/App_Data/models/liveness/minifasnet.onnx"));

            vm.DlibModelsLoaded = CheckDlibModelsPresent(
                AppSettings.GetString("Biometrics:DlibModelsDir",
                    "~/App_Data/models/dlib"));

            // OfflineAssetsOk defaults to true in the ViewModel constructor.
            // Extend here if you add offline asset checks (e.g. face-api.js wasm files).
            vm.OfflineAssetsOk = true;

            ViewBag.Title = "Dashboard";
            return View(vm);
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        /// <summary>
        /// Maps a virtual path (~/...) to a physical path and checks existence.
        /// Returns false if the path is empty or the file does not exist.
        /// </summary>
        private static bool CheckFileExists(string virtualPath)
        {
            if (string.IsNullOrWhiteSpace(virtualPath))
                return false;

            try
            {
                var physical = HostingEnvironment.MapPath(virtualPath);
                return !string.IsNullOrWhiteSpace(physical) && System.IO.File.Exists(physical);
            }
            catch
            {
                return false;
            }
        }


        /// <summary>
        /// Checks whether the Dlib models directory exists and contains at least
        /// one .dat file (the expected model format). A missing or empty directory
        /// means Dlib face detection will fail at runtime.
        /// </summary>
        private static bool CheckDlibModelsPresent(string virtualDir)
        {
            if (string.IsNullOrWhiteSpace(virtualDir))
                return false;
            try
            {
                var physical = HostingEnvironment.MapPath(virtualDir);
                if (string.IsNullOrWhiteSpace(physical) || !Directory.Exists(physical))
                    return false;

                return Directory.EnumerateFiles(physical, "*.dat",
                    SearchOption.TopDirectoryOnly).Any();
            }
            catch
            {
                return false;
            }
        }
    }
}
