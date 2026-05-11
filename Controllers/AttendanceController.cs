using System;
using System.Data.Entity;
using System.Linq;
using System.Threading;
using System.Web;
using System.Web.Mvc;
using FaceAttend.Filters;
using FaceAttend.Services;
using FaceAttend.Services.Mobile;
using FaceAttend.Services.Recognition;
using FaceAttend.Services.Security;

namespace FaceAttend.Controllers
{
    [RoutePrefix("Attendance")]
    public class AttendanceController : Controller
    {
        private static int _activeScanCount;

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Route("Scan")]
        [RateLimit(Name = "AttendanceScan", MaxRequests = 60, WindowSeconds = 60, Burst = 20)]
        public ActionResult Scan(
            double? lat,
            double? lon,
            double? accuracy,
            HttpPostedFileBase image,
            bool wfhMode = false)
        {
            var scanSw = System.Diagnostics.Stopwatch.StartNew();
            var maxConcurrent = Math.Max(1, ConfigurationService.GetInt("Kiosk:MaxConcurrentScans", 4));
            if (Interlocked.Increment(ref _activeScanCount) > maxConcurrent)
            {
                Interlocked.Decrement(ref _activeScanCount);
                OperationalMetricsService.RecordBusy();
                Response.StatusCode = 429;
                Response.TrySkipIisCustomErrors = true;
                var busy = JsonResponseBuilder.Error("SCAN_BUSY", "Scanner is busy. Please retry.");
                PublicAuditService.RecordScan(Request, busy, "PUBLIC_SCAN", scanSw.ElapsedMilliseconds);
                return busy;
            }

            try
            {
                var result = new AttendanceScanService().Scan(
                    lat,
                    lon,
                    accuracy,
                    image,
                    TimeZoneHelper.NowLocal(),
                    includePerfTimings: ConfigurationService.GetBool("Kiosk:EnablePerfTimings", false),
                    httpContext: HttpContext,
                    wfhMode: wfhMode);
                OperationalMetricsService.RecordScan(scanSw.ElapsedMilliseconds, result);
                PublicAuditService.RecordScan(Request, result, "PUBLIC_SCAN", scanSw.ElapsedMilliseconds);
                return result;
            }
            finally
            {
                Interlocked.Decrement(ref _activeScanCount);
            }
        }

        [HttpGet]
        [Route("MyMonth")]
        [RateLimit(Name = "AttendanceMyMonth", MaxRequests = 60, WindowSeconds = 60, Burst = 20)]
        public ActionResult MyMonth()
        {
            AttendanceAccessReceiptService.ReceiptPayload receipt;
            string error;
            if (!AttendanceAccessReceiptService.TryValidate(Request, out receipt, out error))
                return DenyReceipt(error);

            using (var db = new FaceAttendDBEntities())
            {
                var employee = db.Employees
                    .Include(e => e.Office)
                    .FirstOrDefault(e =>
                        e.Id == receipt.EmployeeDbId &&
                        e.EmployeeId == receipt.EmployeePublicId);

                if (employee == null ||
                    !string.Equals(employee.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase))
                    return DenyReceipt("EMPLOYEE_NOT_ACTIVE");

                var hasReceiptLog = db.AttendanceLogs.Any(l =>
                    l.Id == receipt.AttendanceLogId &&
                    l.EmployeeId == employee.Id &&
                    !l.IsVoided);
                if (!hasReceiptLog)
                    return DenyReceipt("RECEIPT_LOG_NOT_FOUND");

                var vm = EmployeePortalService.BuildPortalVm(
                    db,
                    employee,
                    TimeZoneHelper.TodayLocalDate(),
                    receipt.ExpiresUtc);

                PublicAuditService.RecordMonthlyAccess(Request, employee.EmployeeId, receipt.AttendanceLogId, exported: false);
                return View("~/Views/MobileRegistration/Employee.cshtml", vm);
            }
        }

        [HttpGet]
        [Route("ExportMyMonth")]
        [RateLimit(Name = "AttendanceExportMyMonth", MaxRequests = 20, WindowSeconds = 60, Burst = 5)]
        public ActionResult ExportMyMonth()
        {
            AttendanceAccessReceiptService.ReceiptPayload receipt;
            string error;
            if (!AttendanceAccessReceiptService.TryValidate(Request, out receipt, out error))
                return DenyReceipt(error);

            using (var db = new FaceAttendDBEntities())
            {
                var employee = db.Employees.FirstOrDefault(e =>
                    e.Id == receipt.EmployeeDbId &&
                    e.EmployeeId == receipt.EmployeePublicId);
                if (employee == null ||
                    !string.Equals(employee.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase))
                    return DenyReceipt("EMPLOYEE_NOT_ACTIVE");

                var today = TimeZoneHelper.TodayLocalDate();
                var firstDay = new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
                var lastDay = firstDay.AddMonths(1);
                var logs = db.AttendanceLogs
                    .Include("Office")
                    .Where(l =>
                        l.EmployeeId == employee.Id &&
                        !l.IsVoided &&
                        l.Timestamp >= firstDay &&
                        l.Timestamp < lastDay)
                    .OrderBy(l => l.Timestamp)
                    .ToList();

                var bytes = EmployeePortalService.BuildCsvBytes(employee, logs, firstDay);
                var fileName = string.Format("attendance_{0}_{1:yyyy_MM}.csv", employee.EmployeeId, firstDay);
                PublicAuditService.RecordMonthlyAccess(Request, employee.EmployeeId, receipt.AttendanceLogId, exported: true);
                return File(bytes, "text/csv", fileName);
            }
        }

        private ActionResult DenyReceipt(string error)
        {
            AttendanceAccessReceiptService.Clear(Response, Request != null && Request.IsSecureConnection);
            Response.StatusCode = 403;
            Response.TrySkipIisCustomErrors = true;
            return Content(
                "Fresh successful face scan required to view attendance. Please scan again. (" +
                (error ?? "ACCESS_DENIED") + ")",
                "text/plain");
        }
    }
}
