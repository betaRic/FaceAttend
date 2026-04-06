using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Text;
using System.Web.Mvc;
using FaceAttend.Models.ViewModels.Admin;
using FaceAttend.Areas.Admin.Helpers;
using FaceAttend.Filters;
using FaceAttend.Services;
using FaceAttend.Services.Helpers;

namespace FaceAttend.Areas.Admin.Controllers
{
    [AdminAuthorize]
    public class AttendanceController : Controller
    {
        // ── Index ────────────────────────────────────────────────────────────────

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
                PageSize        = pageSize.GetValueOrDefault(25),
                ExportMaxRows   = ConfigurationService.GetInt("Attendance:ExportMaxRows", 10000)
            };

            if (vm.Page     <= 0)   vm.Page     = 1;
            if (vm.PageSize <= 0)   vm.PageSize = 25;
            if (vm.PageSize > 200)  vm.PageSize = 200;

            using (var db = new FaceAttendDBEntities())
            {
                vm.OfficeOptions = AdminQueryHelper.BuildOfficeOptions(db, vm.OfficeId);

                var range = AdminQueryHelper.ParseRange(vm.From, vm.To);
                vm.From             = range.FromText;
                vm.To               = range.ToText;
                vm.ActiveRangeLabel = range.Label;

                // ── Base query (all filters except NeedsReviewOnly) ───────────────

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

                // Counts: compute in one DB round-trip.
                var counts = baseQ
                    .GroupBy(_ => 1)
                    .Select(g => new
                    {
                        Total = g.Count(),
                        Needs = g.Count(x => x.NeedsReview)
                    })
                    .FirstOrDefault();

                var baseTotal = counts == null ? 0 : counts.Total;
                vm.TotalNeedsReview = counts == null ? 0 : counts.Needs;
                vm.Total = vm.NeedsReviewOnly ? vm.TotalNeedsReview : baseTotal;

                // ── Apply NeedsReviewOnly filter for pagination ───────────────────

                var q = vm.NeedsReviewOnly ? baseQ.Where(x => x.NeedsReview) : baseQ;

                var skip = Math.Max(0, (vm.Page - 1) * vm.PageSize);

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
                        NeedsReview      = x.NeedsReview,
                        WiFiBSSID         = x.WiFiBSSID
                    })
                    .ToList();

                // ── Summary statistics (cards section) ───────────────────────────
                // Computed from baseQ (un-paged, before NeedsReviewOnly filter)
                // so the stats represent the full filtered dataset.

                if (baseTotal > 0)
                {
                    vm.ShowSummary = true;

                    // Timestamps are now stored in local time (Asia/Manila) - no conversion needed
                    var dailyRaw = baseQ
                        .Select(x => new { x.Timestamp, x.EventType })
                        .ToList()
                        .GroupBy(x => x.Timestamp.Date)
                        .Select(g => new
                        {
                            Date = g.Key,
                            InCnt = g.Count(x => x.EventType == "IN"),
                            OutCnt = g.Count(x => x.EventType == "OUT")
                        })
                        .OrderByDescending(g => g.Date)
                        .Take(14)
                        .OrderBy(g => g.Date)
                        .ToList();

                    vm.DailyBreakdown = dailyRaw
                        .Select(g => new DailySummaryRow
                        {
                            Date = g.Date,
                            InCount = g.InCnt,
                            OutCount = g.OutCnt
                        })
                        .ToList();

                    // Office breakdown — all offices in the result, by total volume.
                    var officeRaw = baseQ
                        .GroupBy(x => x.OfficeName)
                        .Select(g => new
                        {
                            Name   = g.Key,
                            InCnt  = g.Count(x => x.EventType == "IN"),
                            OutCnt = g.Count(x => x.EventType == "OUT")
                        })
                        .OrderByDescending(g => g.InCnt + g.OutCnt)
                        .ToList();

                    vm.OfficeBreakdown = officeRaw
                        .Select(g => new OfficeSummaryRow
                        {
                            OfficeName = g.Name ?? "(unknown)",
                            InCount    = g.InCnt,
                            OutCount   = g.OutCnt
                        })
                        .ToList();

                    var todayLocal = TimeZoneHelper.TodayLocalDate();
                    bool todayInRange = todayLocal >= range.FromLocalDate && todayLocal <= range.ToLocalDate;

                    if (todayInRange)
                    {
                        vm.TotalActiveEmployees = db.Employees.Count(e => e.Status == "ACTIVE");

                        if (vm.TotalActiveEmployees > 0)
                        {
                            var todayRow = dailyRaw.FirstOrDefault(x => x.Date == todayLocal);
                            int todayIns = todayRow == null ? 0 : todayRow.InCnt;

                            vm.AttendanceRatePercent =
                                (int)Math.Min(100, Math.Round(100.0 * todayIns / vm.TotalActiveEmployees));
                        }
                    }
                }
            }

            return View(vm);
        }

        // ── Details ──────────────────────────────────────────────────────────────

        [HttpGet]
        public ActionResult Details(long id)
        {
            ViewBag.Title = "Attendance Details";

            using (var db = new FaceAttendDBEntities())
            {
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

        // ── MarkReviewed ─────────────────────────────────────────────────────────

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
            TempData["msgKind"] = "success";
            return RedirectToAction("Details", new { id });
        }

        // ── Delete (Reject) ──────────────────────────────────────────────────────

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Delete(long id)
        {
            using (var db = new FaceAttendDBEntities())
            {
                var row = db.AttendanceLogs.FirstOrDefault(x => x.Id == id);
                if (row == null) return HttpNotFound();

                db.AttendanceLogs.Remove(row);
                db.SaveChanges();
            }

            TempData["msg"] = "Record deleted.";
            TempData["msgKind"] = "success";
            return RedirectToAction("Index");
        }

        // ── SummaryReport ────────────────────────────────────────────────────────

        /// <summary>
        /// Per-employee daily breakdown showing first IN, last OUT, and hours worked
        /// for the selected date range.  Limited to 31 days per query to prevent OOM.
        /// </summary>
        [HttpGet]
        public ActionResult SummaryReport(string from, string to,
            int? officeId, string department)
        {
            ViewBag.Title = "Attendance Summary";

            var vm = new AttendanceIndexVm
            {
                From            = (from ?? "").Trim(),
                To              = (to ?? "").Trim(),
                OfficeId        = officeId,
                Employee        = "",
                DepartmentFilter = (department ?? "").Trim(),
                IsSummaryReport = true,
                Page            = 1,
                PageSize        = 200
            };

            using (var db = new FaceAttendDBEntities())
            {
                vm.OfficeOptions = AdminQueryHelper.BuildOfficeOptions(db, vm.OfficeId);

                var range = AdminQueryHelper.ParseRange(vm.From, vm.To);
                vm.From             = range.FromText;
                vm.To               = range.ToText;
                vm.ActiveRangeLabel = range.Label;

                // 31-day cap (based on LOCAL date span)
                var daySpan = (range.ToLocalDate - range.FromLocalDate).TotalDays;
                if (daySpan > 31)
                {
                    TempData["msg"] = "Summary report is limited to 31 days. Please narrow your date range.";
                    TempData["msgKind"] = "warning";
                    return RedirectToAction("Index", new { from = vm.From, to = vm.To, officeId });
                }

                var policy = AttendanceReportService.LoadPolicy();

                var q = db.AttendanceLogs.AsNoTracking().AsQueryable();

                if (range.FromUtc.HasValue)
                    q = q.Where(x => x.Timestamp >= range.FromUtc.Value);
                if (range.ToUtcExclusive.HasValue)
                    q = q.Where(x => x.Timestamp < range.ToUtcExclusive.Value);
                if (officeId.HasValue && officeId.Value > 0)
                    q = q.Where(x => x.OfficeId == officeId.Value);
                var deptTerm = (department ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(deptTerm))
                    q = q.Where(x => x.Department.Contains(deptTerm));

                int cap = ConfigurationService.GetInt("Attendance:SummaryExportMaxRawRows", 50000);
                if (cap < 1000) cap = 1000;
                if (cap > 200000) cap = 200000;

                var raw = q
                    .Include(x => x.Employee)
                    .Select(x => new AttendanceReportService.RawLog
                    {
                        EmpId      = x.Employee.EmployeeId,
                        FullName   = x.EmployeeFullName,
                        Dept       = x.Department,
                        EventType  = x.EventType,
                        Timestamp  = x.Timestamp
                    })
                    .OrderBy(x => x.EmpId)
                    .ThenBy(x => x.Timestamp)
                    .ToList();

                // Build date list in LOCAL time (inclusive)
                var dates = Enumerable.Range(0, (range.ToLocalDate - range.FromLocalDate).Days + 1)
                    .Select(i => range.FromLocalDate.AddDays(i))
                    .ToList();

                // Timestamps are now stored in local time - no conversion needed
                vm.EmployeeSummary = raw
                    .GroupBy(x => new { x.EmpId, x.FullName, x.Dept })
                    .Select(eg =>
                    {
                        var byDay = eg
                            .GroupBy(x => x.Timestamp.Date)
                            .ToDictionary(g => g.Key, g => g.ToList());

                        var days = new List<DailyEmployeeRow>(dates.Count);
                        foreach (var dayLocal in dates)
                        {
                            byDay.TryGetValue(dayLocal, out var events);
                            days.Add(AttendanceReportService.BuildDailyRow(dayLocal, events, policy));
                        }

                        return new EmployeeSummaryRow
                        {
                            EmployeeId       = eg.Key.EmpId,
                            EmployeeFullName = eg.Key.FullName,
                            Department       = eg.Key.Dept,
                            Days             = days
                        };
                    })
                    .OrderBy(e => e.EmployeeId)
                    .ToList();

                vm.Total = vm.EmployeeSummary.Count;
            }

            return View("SummaryReport", vm);
        }

        // ── Export CSV ───────────────────────────────────────────────────────────

        /// <summary>
        /// CSV export.  Row cap is configurable via Attendance:ExportMaxRows in
        /// Web.config (default 10,000).  Includes WiFiBSSID column.
        ///
        /// Security: CSV injection is mitigated by SafeCell() which prefixes formula
        /// triggers (=, +, -, @) with a single quote.  Export is a GET protected by
        /// [AdminAuthorize] so only authenticated admins can reach it.
        /// </summary>
        [HttpGet]
        public ActionResult Export(string from, string to, int? officeId, string employee,
            string eventType, bool? needsReview)
        {
            using (var db = new FaceAttendDBEntities())
            {
                var range = AdminQueryHelper.ParseRange((from ?? "").Trim(), (to ?? "").Trim());

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

                int max = ConfigurationService.GetInt("Attendance:ExportMaxRows", 10000);

                var rows = q
                    .OrderByDescending(x => x.Timestamp)
                    .Take(max + 1)
                    .Select(x => new AttendanceReportService.ExportRow
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
                        Notes            = x.Notes,
                        WiFiBSSID         = x.WiFiBSSID        // NEW column
                    })
                    .ToList();

                bool truncated = rows.Count > max;
                if (truncated) rows = rows.Take(max).ToList();

                var csv      = AttendanceReportService.BuildCsv(rows);
                var bytes    = new UTF8Encoding(true).GetBytes(csv);
                var nowLocal = TimeZoneHelper.NowLocal();
                var fileName = truncated
                    ? ("attendance_truncated_" + max + "_" + nowLocal.ToString("yyyyMMdd_HHmm") + ".csv")
                    : ("attendance_" + nowLocal.ToString("yyyyMMdd_HHmm") + ".csv");
                return File(bytes, "text/csv", fileName);
            }
        }

        // ── ExportSummaryCsv ─────────────────────────────────────────────────────

        /// <summary>
        /// Downloads the per-employee daily summary as CSV.
        /// Columns: EmployeeId, EmployeeName, Department, Date, FirstIn, LastOut, HoursWorked.
        /// Hard upper bound: 50,000 raw rows pulled from the DB before in-memory grouping.
        /// </summary>
        [HttpGet]
        public ActionResult ExportSummaryCsv(string from, string to,
            int? officeId, string department)
        {
            using (var db = new FaceAttendDBEntities())
            {
                var range = AdminQueryHelper.ParseRange((from ?? "").Trim(), (to ?? "").Trim());

                // 31-day cap
                if ((range.ToLocalDate - range.FromLocalDate).TotalDays > 31)
                {
                    var bytesErr = Encoding.UTF8.GetBytes("Error: limited to 31 days per export.");
                    return File(bytesErr, "text/plain", "attendance_summary_error.txt");
                }

                var policy = AttendanceReportService.LoadPolicy();

                var q = db.AttendanceLogs.AsNoTracking().AsQueryable();

                if (range.FromUtc.HasValue)
                    q = q.Where(x => x.Timestamp >= range.FromUtc.Value);
                if (range.ToUtcExclusive.HasValue)
                    q = q.Where(x => x.Timestamp < range.ToUtcExclusive.Value);
                if (officeId.HasValue && officeId.Value > 0)
                    q = q.Where(x => x.OfficeId == officeId.Value);
                var deptTerm = (department ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(deptTerm))
                    q = q.Where(x => x.Department.Contains(deptTerm));

                int cap = ConfigurationService.GetInt("Attendance:SummaryExportMaxRawRows", 50000);
                if (cap < 1000) cap = 1000;
                if (cap > 200000) cap = 200000;

                var raw = q
                    .Select(x => new AttendanceReportService.RawLog
                    {
                        EmpId      = x.Employee.EmployeeId,
                        FullName   = x.EmployeeFullName,
                        Dept       = x.Department,
                        EventType  = x.EventType,
                        Timestamp  = x.Timestamp
                    })
                    .OrderBy(x => x.EmpId)
                    .ThenBy(x => x.Timestamp)
                    .Take(cap + 1)
                    .ToList();

                if (raw.Count > cap)
                {
                    var msg = "Error: too many rows for summary export. Narrow the date range or add filters. Max raw rows: " + cap + ".";
                    var bytesErr = Encoding.UTF8.GetBytes(msg);
                    return File(bytesErr, "text/plain", "attendance_summary_error.txt");
                }

                var dates = Enumerable.Range(0, (range.ToLocalDate - range.FromLocalDate).Days + 1)
                    .Select(i => range.FromLocalDate.AddDays(i))
                    .ToList();

                var sb = new StringBuilder();
                sb.AppendLine("EmployeeId,EmployeeName,Department,Date,AmIn,AmOut,PmIn,PmOut,FirstIn,LastOut,HoursNet,Status,LateMin,UndertimeMin");

                var grouped = raw.GroupBy(x => new { x.EmpId, x.FullName, x.Dept })
                    .OrderBy(g => g.Key.EmpId);

                foreach (var eg in grouped)
                {
                    // Timestamps are now stored in local time - no conversion needed
                    var byDay = eg
                        .GroupBy(x => x.Timestamp.Date)
                        .ToDictionary(g => g.Key, g => g.ToList());

                    foreach (var dayLocal in dates)
                    {
                        byDay.TryGetValue(dayLocal, out var events);
                        var row = AttendanceReportService.BuildDailyRow(dayLocal, events, policy);

                        sb.Append(CsvHelper.JoinCsv(new[]
                        {
                            CsvHelper.SafeCell(eg.Key.EmpId),
                            CsvHelper.SafeCell(eg.Key.FullName),
                            CsvHelper.SafeCell(eg.Key.Dept),
                            row.DateLabel,
                            row.AmIn.HasValue ? row.AmIn.Value.ToString("HH:mm") : "",
                            row.AmOut.HasValue ? row.AmOut.Value.ToString("HH:mm") : "",
                            row.PmIn.HasValue ? row.PmIn.Value.ToString("HH:mm") : "",
                            row.PmOut.HasValue ? row.PmOut.Value.ToString("HH:mm") : "",
                            row.FirstInUtc.HasValue ? row.FirstInUtc.Value.ToString("HH:mm") : "",
                            row.LastOutUtc.HasValue ? row.LastOutUtc.Value.ToString("HH:mm") : "",
                            (row.HoursNet ?? row.HoursRaw).HasValue
                                ? (row.HoursNet ?? row.HoursRaw).Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                                : "",
                            CsvHelper.SafeCell(row.StatusLabel),
                            row.LateMinutes.HasValue ? row.LateMinutes.Value.ToString() : "",
                            row.UndertimeMinutes.HasValue ? row.UndertimeMinutes.Value.ToString() : ""
                        }));
                        sb.AppendLine();
                    }
                }

                var bytes = new UTF8Encoding(true).GetBytes(sb.ToString());
                var fileName = "attendance_summary_" + TimeZoneHelper.NowLocal().ToString("yyyyMMdd") + ".csv";
                return File(bytes, "text/csv", fileName);
            }
        }

        // ── Private helpers ──────────────────────────────────────────────────────

        

        

        // Reporting policy, raw log, export row, and daily row computation
        // are handled by AttendanceReportService.
    }
}
