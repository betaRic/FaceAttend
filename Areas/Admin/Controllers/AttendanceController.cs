using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Web.Mvc;
using FaceAttend.Areas.Admin.Models;
using FaceAttend.Filters;

namespace FaceAttend.Areas.Admin.Controllers
{
    [AdminAuthorize]
    public class AttendanceController : Controller
    {
        [HttpGet]
        public ActionResult Index(string from, string to, int? officeId, string employee,
            string eventType, bool? needsReview, int? page, int? pageSize)
        {
            ViewBag.Title = "Attendance";

            var vm = new AttendanceIndexVm
            {
                From            = (from ?? "").Trim(),
                To              = (to ?? "").Trim(),
                OfficeId        = officeId,
                Employee        = (employee ?? "").Trim(),
                EventType       = string.IsNullOrWhiteSpace(eventType) ? "ALL" : eventType.Trim().ToUpperInvariant(),
                NeedsReviewOnly = needsReview.HasValue && needsReview.Value,
                Page            = page.GetValueOrDefault(1),
                PageSize        = pageSize.GetValueOrDefault(25)
            };

            if (vm.Page <= 0) vm.Page = 1;
            if (vm.PageSize <= 0) vm.PageSize = 25;
            if (vm.PageSize > 200) vm.PageSize = 200;

            using (var db = new FaceAttendDBEntities())
            {
                vm.OfficeOptions = BuildOfficeOptions(db, vm.OfficeId);

                var range = ParseRange(vm.From, vm.To);
                vm.From            = range.FromText;
                vm.To              = range.ToText;
                vm.ActiveRangeLabel = range.Label;

                var baseQ = db.AttendanceLogs.AsNoTracking().AsQueryable();

                if (range.FromUtc.HasValue)
                    baseQ = baseQ.Where(x => x.Timestamp >= range.FromUtc.Value);

                if (range.ToUtcExclusive.HasValue)
                    baseQ = baseQ.Where(x => x.Timestamp < range.ToUtcExclusive.Value);

                if (vm.OfficeId.HasValue && vm.OfficeId.Value > 0)
                    baseQ = baseQ.Where(x => x.OfficeId == vm.OfficeId.Value);

                if (!string.IsNullOrWhiteSpace(vm.Employee))
                {
                    var term = vm.Employee;
                    baseQ = baseQ.Where(x =>
                        x.EmployeeFullName.Contains(term) ||
                        x.Employee.EmployeeId.Contains(term));
                }

                if (vm.EventType == "IN" || vm.EventType == "OUT")
                    baseQ = baseQ.Where(x => x.EventType == vm.EventType);

                vm.TotalNeedsReview = baseQ.Count(x => x.NeedsReview);

                var q = baseQ;
                if (vm.NeedsReviewOnly)
                    q = q.Where(x => x.NeedsReview);

                vm.Total = q.Count();

                var skip = Math.Max(0, (vm.Page - 1) * vm.PageSize);

                // EF6 generates a single LEFT JOIN here — not N+1.
                // x.Employee.EmployeeId inside .Select() before .ToList() is translated
                // to SQL by the EF query provider, so no lazy-load occurs.
                vm.Rows = q
                    .OrderByDescending(x => x.Timestamp)
                    .Skip(skip)
                    .Take(vm.PageSize)
                    .Select(x => new AttendanceRowVm
                    {
                        Id               = x.Id,
                        TimestampUtc     = x.Timestamp,
                        EmployeeId       = x.Employee.EmployeeId,
                        EmployeeFullName = x.EmployeeFullName,
                        Department       = x.Department,
                        OfficeName       = x.OfficeName,
                        EventType        = x.EventType,
                        LivenessScore    = x.LivenessScore,
                        FaceDistance     = x.FaceDistance,
                        LocationVerified = x.LocationVerified,
                        NeedsReview      = x.NeedsReview
                    })
                    .ToList();
            }

            return View(vm);
        }

        [HttpGet]
        public ActionResult Details(long id)
        {
            ViewBag.Title = "Attendance Details";

            using (var db = new FaceAttendDBEntities())
            {
                // FIX: Original made two separate DB round-trips (one for the log, one
                // for the employee).  A single query with Include() is cleaner and avoids
                // the extra round-trip.  AsNoTracking() because this is a read-only view.
                var row = db.AttendanceLogs
                    .AsNoTracking()
                    .Include("Employee")
                    .FirstOrDefault(x => x.Id == id);

                if (row == null) return HttpNotFound();

                var vm = new AttendanceDetailsVm
                {
                    Id               = row.Id,
                    TimestampUtc     = row.Timestamp,
                    EventType        = row.EventType,
                    Source           = row.Source,

                    EmployeeId       = row.Employee == null ? "" : (row.Employee.EmployeeId ?? ""),
                    EmployeeFullName = row.EmployeeFullName,
                    Department       = row.Department,

                    OfficeId         = row.OfficeId,
                    OfficeName       = row.OfficeName,
                    OfficeType       = row.OfficeType,

                    GPSLatitude      = row.GPSLatitude,
                    GPSLongitude     = row.GPSLongitude,
                    GPSAccuracy      = row.GPSAccuracy,
                    LocationVerified = row.LocationVerified,
                    LocationError    = row.LocationError,

                    FaceDistance     = row.FaceDistance,
                    FaceSimilarity   = row.FaceSimilarity,
                    MatchThreshold   = row.MatchThreshold,

                    LivenessScore    = row.LivenessScore,
                    LivenessResult   = row.LivenessResult,
                    LivenessError    = row.LivenessError,

                    ClientIP         = row.ClientIP,
                    UserAgent        = row.UserAgent,

                    NeedsReview      = row.NeedsReview,
                    Notes            = row.Notes
                };

                return View(vm);
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult MarkReviewed(long id, string note)
        {
            using (var db = new FaceAttendDBEntities())
            {
                var row = db.AttendanceLogs.FirstOrDefault(x => x.Id == id);
                if (row == null) return HttpNotFound();

                row.NeedsReview = false;

                var stamp = "Reviewed " + DateTime.UtcNow.ToString("u");
                var extra = (note ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(extra)) stamp += " - " + extra;

                row.Notes = string.IsNullOrWhiteSpace(row.Notes)
                    ? stamp
                    : row.Notes + "\n" + stamp;

                db.SaveChanges();
            }

            TempData["msg"] = "Marked as reviewed.";
            return RedirectToAction("Details", new { id });
        }

        /// <summary>
        /// CSV export — capped at 5,000 rows.
        ///
        /// Security notes:
        ///   • CSV injection is mitigated by SafeCell() which prefixes formula triggers
        ///     (=, +, -, @) with a single quote so spreadsheets treat them as text.
        ///   • Export is a GET so it can be triggered via a link.  The endpoint is
        ///     protected by [AdminAuthorize] so only authenticated admins can reach it.
        ///   • The 5,000-row cap prevents accidental OOM on large datasets.  For full
        ///     data exports use a background job + streaming approach.
        /// </summary>
        [HttpGet]
        public ActionResult Export(string from, string to, int? officeId, string employee,
            string eventType, bool? needsReview)
        {
            using (var db = new FaceAttendDBEntities())
            {
                var range = ParseRange((from ?? "").Trim(), (to ?? "").Trim());

                var q = db.AttendanceLogs.AsNoTracking().AsQueryable();

                if (range.FromUtc.HasValue)
                    q = q.Where(x => x.Timestamp >= range.FromUtc.Value);

                if (range.ToUtcExclusive.HasValue)
                    q = q.Where(x => x.Timestamp < range.ToUtcExclusive.Value);

                if (officeId.HasValue && officeId.Value > 0)
                    q = q.Where(x => x.OfficeId == officeId.Value);

                var et = string.IsNullOrWhiteSpace(eventType)
                    ? "ALL"
                    : eventType.Trim().ToUpperInvariant();
                if (et == "IN" || et == "OUT")
                    q = q.Where(x => x.EventType == et);

                if (!string.IsNullOrWhiteSpace(employee))
                {
                    var term = employee.Trim();
                    q = q.Where(x =>
                        x.EmployeeFullName.Contains(term) ||
                        x.Employee.EmployeeId.Contains(term));
                }

                if (needsReview.HasValue && needsReview.Value)
                    q = q.Where(x => x.NeedsReview);

                const int max = 5000;
                var rows = q
                    .OrderByDescending(x => x.Timestamp)
                    .Take(max)
                    .Select(x => new ExportRow
                    {
                        Timestamp        = x.Timestamp,
                        EmpId            = x.Employee.EmployeeId,
                        EmployeeFullName = x.EmployeeFullName,
                        Department       = x.Department,
                        OfficeName       = x.OfficeName,
                        EventType        = x.EventType,
                        LivenessScore    = x.LivenessScore,
                        FaceDistance     = x.FaceDistance,
                        LocationVerified = x.LocationVerified,
                        GPSAccuracy      = x.GPSAccuracy,
                        NeedsReview      = x.NeedsReview,
                        Notes            = x.Notes
                    })
                    .ToList();

                var csv      = BuildCsv(rows);
                var bytes    = Encoding.UTF8.GetBytes(csv);
                var fileName = "attendance_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".csv";
                return File(bytes, "text/csv", fileName);
            }
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private static List<SelectListItem> BuildOfficeOptions(FaceAttendDBEntities db, int? selected)
        {
            var list = new List<SelectListItem>
            {
                new SelectListItem { Text = "All offices", Value = "" }
            };

            db.Offices.AsNoTracking()
              .Where(o => o.IsActive)
              .OrderBy(o => o.Name)
              .ToList()
              .ForEach(o => list.Add(new SelectListItem
              {
                  Text     = o.Name,
                  Value    = o.Id.ToString(),
                  Selected = selected.HasValue && selected.Value == o.Id
              }));

            return list;
        }

        private class RangeResult
        {
            public DateTime? FromUtc { get; set; }
            public DateTime? ToUtcExclusive { get; set; }
            public string FromText { get; set; }
            public string ToText { get; set; }
            public string Label { get; set; }
        }

        private static RangeResult ParseRange(string from, string to)
        {
            var today          = DateTime.Now.Date;
            var defaultFrom    = today.AddDays(-6);
            var defaultTo      = today;

            DateTime fromLocal;
            if (!DateTime.TryParse(from, out fromLocal)) fromLocal = defaultFrom;
            fromLocal = fromLocal.Date;

            DateTime toLocal;
            if (!DateTime.TryParse(to, out toLocal)) toLocal = defaultTo;
            toLocal = toLocal.Date;

            if (toLocal < fromLocal)
            {
                var tmp = fromLocal;
                fromLocal = toLocal;
                toLocal   = tmp;
            }

            return new RangeResult
            {
                FromUtc        = fromLocal.ToUniversalTime(),
                ToUtcExclusive = toLocal.AddDays(1).ToUniversalTime(),
                FromText       = fromLocal.ToString("yyyy-MM-dd"),
                ToText         = toLocal.ToString("yyyy-MM-dd"),
                Label          = fromLocal.ToString("MMM d") + " - " + toLocal.ToString("MMM d")
            };
        }

        private class ExportRow
        {
            public DateTime Timestamp { get; set; }
            public string EmpId { get; set; }
            public string EmployeeFullName { get; set; }
            public string Department { get; set; }
            public string OfficeName { get; set; }
            public string EventType { get; set; }
            public double? LivenessScore { get; set; }
            public double? FaceDistance { get; set; }
            public bool LocationVerified { get; set; }
            public double? GPSAccuracy { get; set; }
            public bool NeedsReview { get; set; }
            public string Notes { get; set; }
        }

        private static string BuildCsv(IEnumerable<ExportRow> rows)
        {
            var sb = new StringBuilder();
            sb.AppendLine("TimestampLocal,EmployeeId,EmployeeName,Department,Office,EventType," +
                          "LivenessScore,FaceDistance,LocationVerified,GPSAccuracy,NeedsReview,Notes");

            foreach (var r in rows)
            {
                var local = r.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                sb.Append(JoinCsv(new[]
                {
                    local,
                    SafeCell(r.EmpId),
                    SafeCell(r.EmployeeFullName),
                    SafeCell(r.Department),
                    SafeCell(r.OfficeName),
                    SafeCell(r.EventType),
                    r.LivenessScore.HasValue
                        ? r.LivenessScore.Value.ToString("0.000",
                            System.Globalization.CultureInfo.InvariantCulture)
                        : "",
                    r.FaceDistance.HasValue
                        ? r.FaceDistance.Value.ToString("0.000",
                            System.Globalization.CultureInfo.InvariantCulture)
                        : "",
                    r.LocationVerified ? "YES" : "NO",
                    r.GPSAccuracy.HasValue
                        ? r.GPSAccuracy.Value.ToString("0.0",
                            System.Globalization.CultureInfo.InvariantCulture)
                        : "",
                    r.NeedsReview ? "YES" : "NO",
                    SafeCell(r.Notes)
                }));
                sb.AppendLine();
            }

            return sb.ToString();
        }

        private static string JoinCsv(IEnumerable<string> cells)
            => string.Join(",", cells.Select(EscapeCsv));

        private static string EscapeCsv(string s)
        {
            if (s == null) s = "";
            var needsQuote = s.Contains(",") || s.Contains("\"") ||
                             s.Contains("\n") || s.Contains("\r");
            s = s.Replace("\"", "\"\"");
            return needsQuote ? "\"" + s + "\"" : s;
        }

        /// <summary>
        /// Prevents CSV formula injection by prepending a single quote to any
        /// cell value that starts with a formula trigger character (=, +, -, @).
        /// Excel and Google Sheets will then display the value as plain text.
        /// </summary>
        private static string SafeCell(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var t = s.Trim();
            if (t.StartsWith("=") || t.StartsWith("+") ||
                t.StartsWith("-") || t.StartsWith("@"))
                return "'" + t;
            return t;
        }
    }
}
