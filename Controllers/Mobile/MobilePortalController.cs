using System;
using System.Linq;
using System.Web.Mvc;
using FaceAttend.Models.ViewModels.Mobile;
using FaceAttend.Services;
using FaceAttend.Services.Helpers;
using FaceAttend.Services.Mobile;

namespace FaceAttend.Controllers.Mobile
{
    /// <summary>
    /// Handles the mobile portal entry point, device registration, success/status pages,
    /// and the employee attendance portal.
    /// URLs remain at /MobileRegistration/* for compatibility.
    /// </summary>
    [RoutePrefix("MobileRegistration")]
    public class MobilePortalController : Controller
    {
        // ── Entry point ───────────────────────────────────────────────────────────

        [HttpGet]
        [Route("")]
        public ActionResult Index()
        {
            if (!DeviceService.IsMobileDevice(Request))
                return RedirectToRoute(new { controller = "Kiosk", action = "Index", area = "" });

            ViewBag.DeviceFingerprint = DeviceService.GenerateFingerprint(Request);
            ViewBag.IsMobile = true;

            return View("~/Views/MobileRegistration/Index.cshtml");
        }

        // ── Device registration ───────────────────────────────────────────────────

        [HttpGet]
        [Route("Device")]
        public ActionResult Device(string employeeId, bool isNewEmployee = false, int? employeeDbId = null)
        {
            if (!DeviceService.IsMobileDevice(Request))
                return RedirectToRoute(new { controller = "Kiosk", action = "Index", area = "" });

            using (var db = new FaceAttendDBEntities())
            {
                var vm = new DeviceRegistrationVm
                {
                    EmployeeId = employeeId,
                    IsNewEmployee = isNewEmployee,
                    EmployeeDbId = employeeDbId,
                    DeviceName = GetDefaultDeviceName(),
                    Fingerprint = DeviceService.GenerateFingerprint(Request)
                };

                if (!isNewEmployee)
                {
                    var employee = db.Employees.FirstOrDefault(e => e.EmployeeId == employeeId);
                    if (employee != null)
                    {
                        vm.EmployeeFullName = $"{employee.FirstName} {employee.LastName}";
                        vm.Department = StringHelper.SanitizeDisplayText(employee.Department);

                        var existingDevice = db.Devices
                            .FirstOrDefault(d => d.EmployeeId == employee.Id && d.Status == "ACTIVE");

                        if (existingDevice != null)
                        {
                            vm.HasExistingDevice = true;
                            vm.ExistingDeviceName = existingDevice.DeviceName;
                        }
                    }
                }

                return View("~/Views/MobileRegistration/Device.cshtml", vm);
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Route("RegisterDevice")]
        public ActionResult RegisterDevice(DeviceRegistrationVm vm)
        {
            if (!ModelState.IsValid)
            {
                var errors = string.Join(" | ", ModelState.Values
                    .SelectMany(v => v.Errors)
                    .Select(e => e.ErrorMessage));
                Response.StatusCode = 400;
                Response.TrySkipIisCustomErrors = true;
                return JsonResponseBuilder.Error("VALIDATION_ERROR", errors);
            }

            var fingerprint = DeviceService.GenerateFingerprint(Request);
            var existingToken = DeviceService.GetDeviceTokenFromCookie(Request);

            using (var db = new FaceAttendDBEntities())
            {
                if (vm.IsNewEmployee && vm.EmployeeDbId.HasValue)
                {
                    var employee = db.Employees.Find(vm.EmployeeDbId.Value);
                    if (employee == null)
                        return JsonResponseBuilder.NotFound("Enrollment");

                    var status = DeviceService.GetEmployeeStatus(db, employee.Id);
                    if (!string.Equals(status, "PENDING", StringComparison.OrdinalIgnoreCase))
                        return JsonResponseBuilder.Error("INVALID_STATUS", "This enrollment is no longer pending.");

                    DeviceService.CreatePendingDevice(db, employee.Id, fingerprint, vm.DeviceName, Request.UserHostAddress);

                    var deviceToken = DeviceService.GenerateDeviceToken();
                    DeviceService.SetDeviceTokenCookie(Response, deviceToken, Request.IsSecureConnection);

                    return JsonResponseBuilder.Success(new
                    {
                        isNewEmployee = true,
                        employeeDbId = employee.Id,
                        deviceToken = deviceToken
                    }, "Registration complete! An admin will approve your enrollment.");
                }
                else
                {
                    var employee = db.Employees.FirstOrDefault(e => e.EmployeeId == vm.EmployeeId);
                    if (employee == null)
                        return JsonResponseBuilder.NotFound("Employee");

                    if (!string.Equals(employee.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase))
                        return JsonResponseBuilder.Error("INVALID_STATUS", "Only active employees can register a device.");

                    var existingDevices = db.Devices
                        .Where(d => d.EmployeeId == employee.Id && d.Status == "ACTIVE")
                        .ToList();

                    foreach (var oldDevice in existingDevices)
                        oldDevice.Status = "REPLACED";

                    var result = DeviceService.RegisterDevice(
                        db,
                        employee.Id,
                        fingerprint,
                        vm.DeviceName,
                        Request.UserHostAddress,
                        existingToken);

                    if (!result.Success)
                        return JsonResponseBuilder.Error(result.ErrorCode, result.Message);

                    DeviceService.ApproveDevice(db, result.Data, -1);

                    db.SaveChanges();

                    dynamic extraData = result.ExtraData;
                    var returnedToken = extraData?.DeviceToken ?? existingToken ?? DeviceService.GenerateDeviceToken();
                    DeviceService.SetDeviceTokenCookie(Response, returnedToken, Request.IsSecureConnection);

                    return JsonResponseBuilder.Success(new
                    {
                        isNewEmployee = false,
                        deviceToken = returnedToken
                    }, "Device registered successfully! You can now use Face Attendance.");
                }
            }
        }

        // ── Completion ────────────────────────────────────────────────────────────

        [HttpGet]
        [Route("Success")]
        public ActionResult Success(bool isNewEmployee = false, int? employeeDbId = null)
        {
            ViewBag.IsNewEmployee = isNewEmployee;
            ViewBag.EmployeeDbId = employeeDbId;
            return View("~/Views/MobileRegistration/Success.cshtml");
        }

        [HttpGet]
        [Route("CheckStatus")]
        public ActionResult CheckStatus(int employeeDbId)
        {
            using (var db = new FaceAttendDBEntities())
            {
                var employee = db.Employees.Find(employeeDbId);
                if (employee == null)
                    return JsonResponseBuilder.NotFound("Enrollment");

                var status = DeviceService.GetEmployeeStatus(db, employee.Id);
                string message = status == "PENDING" ? "Waiting for admin approval..." :
                                status == "ACTIVE" ? "Approved! You can now use the system." :
                                "This enrollment is inactive. Please contact the admin.";

                return JsonResponseBuilder.Success(new
                {
                    status = status,
                    isApproved = status == "ACTIVE",
                    message
                });
            }
        }

        // ── Employee portal ───────────────────────────────────────────────────────

        [HttpGet]
        [Route("Employee")]
        public ActionResult Employee()
        {
            if (!DeviceService.IsMobileDevice(Request))
                return RedirectToRoute(new { controller = "Kiosk", action = "Index", area = "" });

            var ua = Request.UserAgent ?? "";
            if (ua.ToLowerInvariant().Contains("ipad") ||
                (ua.ToLowerInvariant().Contains("android") && !ua.ToLowerInvariant().Contains("mobile")))
            {
                return RedirectToRoute(new { controller = "Kiosk", action = "Index", area = "" });
            }

            var fingerprint = DeviceService.GenerateFingerprint(Request);
            var deviceToken = DeviceService.GetDeviceTokenFromCookie(Request);

            using (var db = new FaceAttendDBEntities())
            {
                Device device = null;

                if (!string.IsNullOrEmpty(deviceToken))
                    device = db.Devices
                        .Include("Employee")
                        .Include("Employee.Office")
                        .FirstOrDefault(d => d.DeviceToken == deviceToken && d.Status == "ACTIVE");

                if (device == null)
                    device = db.Devices
                        .Include("Employee")
                        .Include("Employee.Office")
                        .FirstOrDefault(d => d.Fingerprint == fingerprint && d.Status == "ACTIVE");

                if (device == null)
                {
                    ViewBag.ErrorMessage = "Your device could not be recognized. Please identify yourself to continue.";
                    ViewBag.Fingerprint = fingerprint;
                    return View("~/Views/MobileRegistration/DeviceNotRecognized.cshtml");
                }

                var employee = device.Employee;
                if (employee == null || employee.Status != "ACTIVE")
                    return RedirectToAction("Identify", "MobileEnrollment");

                var todayLocal = TimeZoneHelper.TodayLocalDate();
                var todayRange = TimeZoneHelper.LocalDateToUtcRange(todayLocal);
                var todayLogs = db.AttendanceLogs
                    .Where(l => l.EmployeeId == employee.Id &&
                                l.Timestamp >= todayRange.fromUtc &&
                                l.Timestamp < todayRange.toUtcExclusive)
                    .OrderBy(l => l.Timestamp)
                    .ToList();

                var firstDayOfMonth = new DateTime(todayLocal.Year, todayLocal.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
                var firstDayOfNextMonth = firstDayOfMonth.AddMonths(1);

                var monthLogs = db.AttendanceLogs
                    .Where(l => l.EmployeeId == employee.Id &&
                                l.Timestamp >= firstDayOfMonth &&
                                l.Timestamp < firstDayOfNextMonth)
                    .OrderBy(l => l.Timestamp)
                    .ToList();

                var totalDaysPresent = monthLogs
                    .Where(l => l.EventType == "IN")
                    .Select(l => l.Timestamp.Date)
                    .Distinct()
                    .Count();

                var totalEstimatedHours = totalDaysPresent * 8.0;
                var lastAttendance = todayLogs.LastOrDefault();

                var recentLogs = monthLogs
                    .OrderByDescending(l => l.Timestamp)
                    .Take(10)
                    .OrderBy(l => l.Timestamp)
                    .Select(l => new RecentAttendanceVm
                    {
                        Date = l.Timestamp.ToString("MMM dd"),
                        Time = l.Timestamp.ToString("h:mm tt"),
                        Type = l.EventType,
                        Office = l.Office != null ? l.Office.Name : "Unknown"
                    })
                    .ToList();

                var monthlyReport = EmployeePortalService.BuildMonthlyAttendanceReport(monthLogs, todayLocal);

                var vm = new EmployeePortalVm
                {
                    EmployeeId = employee.EmployeeId,
                    FullName = $"{employee.FirstName} {employee.LastName}",
                    Position = StringHelper.SanitizeDisplayText(employee.Position),
                    Department = StringHelper.SanitizeDisplayText(employee.Department),
                    OfficeName = StringHelper.SanitizeDisplayText(employee.Office?.Name),
                    DeviceName = device.DeviceName,

                    TodayStatus = lastAttendance?.EventType == "IN" ? "Timed In" :
                                  lastAttendance?.EventType == "OUT" ? "Timed Out" : "Not Yet",
                    LastScanTime = lastAttendance != null
                        ? lastAttendance.Timestamp.ToString("h:mm tt")
                        : null,

                    TotalDaysPresent = totalDaysPresent,
                    TotalHours = Math.Round(totalEstimatedHours, 1),
                    AverageHoursPerDay = totalDaysPresent > 0
                        ? Math.Round(totalEstimatedHours / totalDaysPresent, 1)
                        : 0,

                    RecentEntries = recentLogs,
                    MonthlyReport = monthlyReport,

                    CurrentMonth = todayLocal.ToString("yyyy_MM"),
                    CurrentMonthDisplay = todayLocal.ToString("MMMM yyyy")
                };

                return View("~/Views/MobileRegistration/Employee.cshtml", vm);
            }
        }

        [HttpGet]
        [Route("ExportAttendance")]
        public ActionResult ExportAttendance()
        {
            if (!DeviceService.IsMobileDevice(Request))
                return RedirectToRoute(new { controller = "Kiosk", action = "Index", area = "" });

            var fingerprint = DeviceService.GenerateFingerprint(Request);
            var deviceToken = DeviceService.GetDeviceTokenFromCookie(Request);

            using (var db = new FaceAttendDBEntities())
            {
                Device device = null;

                if (!string.IsNullOrEmpty(deviceToken))
                    device = db.Devices.Include("Employee")
                        .FirstOrDefault(d => d.DeviceToken == deviceToken && d.Status == "ACTIVE");

                if (device == null)
                    device = db.Devices.Include("Employee")
                        .FirstOrDefault(d => d.Fingerprint == fingerprint && d.Status == "ACTIVE");

                if (device?.Employee == null)
                    return RedirectToAction("Identify", "MobileEnrollment");

                var employee = device.Employee;

                var monthParam = Request.QueryString["month"];
                DateTime targetMonth;
                if (!DateTime.TryParseExact(monthParam, "yyyy_MM", null, System.Globalization.DateTimeStyles.None, out targetMonth))
                    targetMonth = TimeZoneHelper.NowLocal();

                var firstDay = new DateTime(targetMonth.Year, targetMonth.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
                var lastDay  = firstDay.AddMonths(1);

                var logs = db.AttendanceLogs
                    .Include("Office")
                    .Where(l => l.EmployeeId == employee.Id && l.Timestamp >= firstDay && l.Timestamp < lastDay)
                    .OrderBy(l => l.Timestamp)
                    .ToList();

                var fileName = $"attendance_{employee.EmployeeId}_{targetMonth:yyyy_MM}.csv";
                var bytes    = EmployeePortalService.BuildCsvBytes(employee, logs, targetMonth);

                return File(bytes, "text/csv", fileName);
            }
        }

        // ── Private helpers ───────────────────────────────────────────────────────

        private string GetDefaultDeviceName()
        {
            var ua = Request.UserAgent ?? "";

            if (ua.Contains("iPhone"))  return "iPhone";
            if (ua.Contains("iPad"))    return "iPad";
            if (ua.Contains("Android")) return "Android Phone";
            if (ua.Contains("Windows Phone")) return "Windows Phone";

            return "Mobile Device";
        }
    }
}
