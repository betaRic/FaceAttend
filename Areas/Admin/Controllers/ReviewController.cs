using System;
using System.Linq;
using System.Text;
using System.Web.Mvc;
using FaceAttend.Areas.Admin.Helpers;
using FaceAttend.Filters;
using FaceAttend.Models.ViewModels.Admin;
using FaceAttend.Services;
using FaceAttend.Services.Helpers;

namespace FaceAttend.Areas.Admin.Controllers
{
    [AdminAuthorize]
    [RateLimit(Name = "AdminReview", MaxRequests = 120, WindowSeconds = 60, Burst = 30)]
    public class ReviewController : Controller
    {
        [HttpGet]
        public ActionResult Index(string from, string to, int? officeId, string employee,
            string reason, string status, int? page, int? pageSize)
        {
            ViewBag.Title = "Review Queue";

            var vm = new ReviewQueueVm
            {
                From = (from ?? "").Trim(),
                To = (to ?? "").Trim(),
                OfficeId = officeId,
                Employee = (employee ?? "").Trim(),
                Reason = NormalizeReason(reason),
                Status = NormalizeStatus(status),
                Page = page.GetValueOrDefault(1),
                PageSize = pageSize.GetValueOrDefault(25)
            };

            if (vm.Page <= 0) vm.Page = 1;
            if (vm.PageSize <= 0) vm.PageSize = 25;
            if (vm.PageSize > 100) vm.PageSize = 100;

            using (var db = new FaceAttendDBEntities())
            {
                vm.OfficeOptions = AdminQueryHelper.BuildOfficeOptions(db, vm.OfficeId);

                var range = AdminQueryHelper.ParseRange(vm.From, vm.To);
                vm.From = range.FromText;
                vm.To = range.ToText;
                vm.ActiveRangeLabel = range.Label;

                var q = db.AttendanceLogs.AsNoTracking().AsQueryable();
                var includeVoided = string.Equals(vm.Status, "VOIDED", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(vm.Status, "ALL", StringComparison.OrdinalIgnoreCase);
                if (!includeVoided)
                    q = q.Where(x => !x.IsVoided);

                if (range.FromLocalInclusive.HasValue)
                    q = q.Where(x => x.Timestamp >= range.FromLocalInclusive.Value);
                if (range.ToLocalExclusive.HasValue)
                    q = q.Where(x => x.Timestamp < range.ToLocalExclusive.Value);
                if (vm.OfficeId.HasValue && vm.OfficeId.Value > 0)
                    q = q.Where(x => x.OfficeId == vm.OfficeId.Value);
                if (!string.IsNullOrWhiteSpace(vm.Employee))
                {
                    var term = vm.Employee;
                    q = q.Where(x =>
                        x.EmployeeFullName.Contains(term) ||
                        x.Employee.EmployeeId.Contains(term));
                }
                if (string.Equals(vm.Status, "VOIDED", StringComparison.OrdinalIgnoreCase))
                    q = q.Where(x => x.IsVoided || x.ReviewStatus == "VOIDED");
                else if (!string.Equals(vm.Status, "ALL", StringComparison.OrdinalIgnoreCase))
                    q = q.Where(x => x.ReviewStatus == vm.Status);
                if (!string.Equals(vm.Reason, "ALL", StringComparison.OrdinalIgnoreCase))
                    q = q.Where(x => x.ReviewReasonCodes.Contains(vm.Reason));

                vm.PendingTotal = db.AttendanceLogs.Count(x => !x.IsVoided && x.ReviewStatus == "PENDING");
                vm.Total = q.Count();

                var skip = Math.Max(0, (vm.Page - 1) * vm.PageSize);
                vm.Rows = q
                    .OrderByDescending(x => x.Timestamp)
                    .Skip(skip)
                    .Take(vm.PageSize)
                    .Select(x => new ReviewQueueRowVm
                    {
                        Id = x.Id,
                        TimestampLocal = x.Timestamp,
                        EmployeeId = x.Employee.EmployeeId,
                        EmployeeFullName = x.EmployeeFullName,
                        Department = x.Department,
                        OfficeName = x.OfficeName,
                        EventType = x.EventType,
                        AntiSpoofScore = x.AntiSpoofScore,
                        FaceDistance = x.FaceDistance,
                        GPSAccuracy = x.GPSAccuracy,
                        ReviewStatus = x.ReviewStatus,
                        ReviewReasonCodes = x.ReviewReasonCodes,
                        Notes = x.Notes
                    })
                    .ToList();
            }

            return View(vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RateLimit(Name = "AdminReviewMutate", MaxRequests = 30, WindowSeconds = 60, Burst = 5)]
        public ActionResult Approve(long id, string note)
        {
            using (var db = new FaceAttendDBEntities())
            {
                var row = db.AttendanceLogs.FirstOrDefault(x => x.Id == id && !x.IsVoided);
                if (row == null) return HttpNotFound();

                var actor = AuditHelper.GetActorIp(Request);
                row.NeedsReview = false;
                row.ReviewStatus = "APPROVED";
                row.ReviewedAt = TimeZoneHelper.NowLocal();
                row.ReviewedBy = actor;
                row.ReviewNote = StringHelper.Truncate((note ?? "").Trim(), 4000);
                db.SaveChanges();

                AuditHelper.Log(db, Request, AuditHelper.ActionAttendanceReview, "AttendanceLog", id,
                    "Attendance review approved.", null, new { row.ReviewStatus, row.ReviewNote });
            }

            TempData["msg"] = "Review approved.";
            TempData["msgKind"] = "success";
            return RedirectToAction("Index");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RateLimit(Name = "AdminReviewMutate", MaxRequests = 30, WindowSeconds = 60, Burst = 5)]
        public ActionResult Void(long id, string reason)
        {
            reason = (reason ?? "").Trim();
            if (string.IsNullOrWhiteSpace(reason))
            {
                TempData["msg"] = "Void reason is required.";
                TempData["msgKind"] = "warning";
                return RedirectToAction("Index");
            }

            using (var db = new FaceAttendDBEntities())
            {
                var row = db.AttendanceLogs.FirstOrDefault(x => x.Id == id && !x.IsVoided);
                if (row == null) return HttpNotFound();

                var actor = AuditHelper.GetActorIp(Request);
                row.IsVoided = true;
                row.NeedsReview = false;
                row.ReviewStatus = "VOIDED";
                row.VoidedAt = TimeZoneHelper.NowLocal();
                row.VoidedBy = actor;
                row.VoidReason = StringHelper.Truncate(reason, 500);
                db.SaveChanges();

                AuditHelper.Log(db, Request, AuditHelper.ActionAttendanceReview, "AttendanceLog", id,
                    "Attendance review voided.", null, new { row.ReviewStatus, row.VoidReason });
            }

            TempData["msg"] = "Record voided and removed from normal attendance reports.";
            TempData["msgKind"] = "success";
            return RedirectToAction("Index");
        }

        [HttpGet]
        [RateLimit(Name = "AdminReviewExport", MaxRequests = 10, WindowSeconds = 60, Burst = 2)]
        public ActionResult Export(string from, string to, int? officeId, string employee, string reason)
        {
            using (var db = new FaceAttendDBEntities())
            {
                var range = AdminQueryHelper.ParseRange((from ?? "").Trim(), (to ?? "").Trim());
                var reasonFilter = NormalizeReason(reason);

                var q = db.AttendanceLogs.AsNoTracking()
                    .Where(x => !x.IsVoided && x.ReviewStatus == "PENDING");

                if (range.FromLocalInclusive.HasValue)
                    q = q.Where(x => x.Timestamp >= range.FromLocalInclusive.Value);
                if (range.ToLocalExclusive.HasValue)
                    q = q.Where(x => x.Timestamp < range.ToLocalExclusive.Value);
                if (officeId.HasValue && officeId.Value > 0)
                    q = q.Where(x => x.OfficeId == officeId.Value);
                if (!string.IsNullOrWhiteSpace(employee))
                {
                    var term = employee.Trim();
                    q = q.Where(x =>
                        x.EmployeeFullName.Contains(term) ||
                        x.Employee.EmployeeId.Contains(term));
                }
                if (!string.Equals(reasonFilter, "ALL", StringComparison.OrdinalIgnoreCase))
                    q = q.Where(x => x.ReviewReasonCodes.Contains(reasonFilter));

                var rows = q
                    .OrderByDescending(x => x.Timestamp)
                    .Take(10000)
                    .Select(x => new
                    {
                        x.Timestamp,
                        EmployeeId = x.Employee.EmployeeId,
                        x.EmployeeFullName,
                        x.Department,
                        x.OfficeName,
                        x.EventType,
                        x.AntiSpoofScore,
                        x.FaceDistance,
                        x.GPSAccuracy,
                        x.ReviewReasonCodes,
                        x.Notes
                    })
                    .ToList();

                var sb = new StringBuilder();
                sb.AppendLine("TimestampLocal,EmployeeId,EmployeeName,Department,Office,EventType,AntiSpoof,Distance,GPSAccuracy,Reasons,Notes");
                foreach (var row in rows)
                {
                    sb.AppendLine(CsvHelper.JoinCsv(new[]
                    {
                        row.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                        CsvHelper.SafeCell(row.EmployeeId),
                        CsvHelper.SafeCell(row.EmployeeFullName),
                        CsvHelper.SafeCell(row.Department),
                        CsvHelper.SafeCell(row.OfficeName),
                        CsvHelper.SafeCell(row.EventType),
                        row.AntiSpoofScore.HasValue ? row.AntiSpoofScore.Value.ToString("0.000") : "",
                        row.FaceDistance.HasValue ? row.FaceDistance.Value.ToString("0.000") : "",
                        row.GPSAccuracy.HasValue ? row.GPSAccuracy.Value.ToString("0.0") : "",
                        CsvHelper.SafeCell(row.ReviewReasonCodes),
                        CsvHelper.SafeCell(row.Notes)
                    }));
                }

                var bytes = new UTF8Encoding(true).GetBytes(sb.ToString());
                AuditHelper.Log(db, Request, AuditHelper.ActionAttendanceExport, "ReviewQueue", "CsvExport",
                    "Exported pending attendance review queue.", null, new
                    {
                        from,
                        to,
                        officeId,
                        employee,
                        reason,
                        rowCount = rows.Count
                    });

                return File(bytes, "text/csv", "review_queue_" + TimeZoneHelper.NowLocal().ToString("yyyyMMdd_HHmm") + ".csv");
            }
        }

        private static string NormalizeStatus(string value)
        {
            var status = (value ?? "PENDING").Trim().ToUpperInvariant();
            return status == "ALL" || status == "APPROVED" || status == "VOIDED" || status == "NONE"
                ? status
                : "PENDING";
        }

        private static string NormalizeReason(string value)
        {
            var reason = (value ?? "ALL").Trim().ToUpperInvariant();
            switch (reason)
            {
                case "NEAR_MATCH":
                case "NEAR_ANTI_SPOOF":
                case "GPS_ACCURACY":
                case "GPS_REPEAT":
                case "ALL":
                    return reason;
                default:
                    return "ALL";
            }
        }
    }
}
