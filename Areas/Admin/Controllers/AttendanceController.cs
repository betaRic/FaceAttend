using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Text;
using System.Web.Mvc;
using FaceAttend.Areas.Admin.Models;
using FaceAttend.Filters;
using FaceAttend.Services;

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
                ExportMaxRows   = AppSettings.GetInt("Attendance:ExportMaxRows", 10000)
            };

            if (vm.Page     <= 0)   vm.Page     = 1;
            if (vm.PageSize <= 0)   vm.PageSize = 25;
            if (vm.PageSize > 200)  vm.PageSize = 200;

            using (var db = new FaceAttendDBEntities())
            {
                vm.OfficeOptions = BuildOfficeOptions(db, vm.OfficeId);

                var range = ParseRange(vm.From, vm.To);
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

                vm.TotalNeedsReview = baseQ.Count(x => x.NeedsReview);

                // ── Apply NeedsReviewOnly filter for pagination ───────────────────

                var q = baseQ;
                if (vm.NeedsReviewOnly)
                    q = q.Where(x => x.NeedsReview);

                vm.Total = q.Count();

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
                        WiFiSSID         = x.WiFiSSID
                    })
                    .ToList();

                // ── Summary statistics (cards section) ───────────────────────────
                // Computed from baseQ (un-paged, before NeedsReviewOnly filter)
                // so the stats represent the full filtered dataset.

                if (vm.Total > 0 || baseQ.Any())
                {
                    vm.ShowSummary = true;

                    // Daily breakdown — group by UTC date, take up to 14 days,
                    // ordered ascending so the view can render a left-to-right trend.
                    var dailyRaw = baseQ
                        .GroupBy(x => DbFunctions.TruncateTime(x.Timestamp))
                        .Select(g => new
                        {
                            Date   = g.Key,
                            InCnt  = g.Count(x => x.EventType == "IN"),
                            OutCnt = g.Count(x => x.EventType == "OUT")
                        })
                        .OrderBy(g => g.Date)
                        .ToList();

                    vm.DailyBreakdown = dailyRaw
                        .Select(g => new DailySummaryRow
                        {
                            Date     = g.Date.HasValue ? g.Date.Value : DateTime.MinValue,
                            InCount  = g.InCnt,
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

                    // Attendance rate — only when today falls inside the filter window.
                    vm.TotalActiveEmployees = db.Employees.Count(e => e.IsActive);

                    var todayUtc    = DateTime.UtcNow.Date;
                    var tomorrowUtc = todayUtc.AddDays(1);

                    bool todayInRange =
                        (!range.FromUtc.HasValue       || todayUtc >= range.FromUtc.Value.Date) &&
                        (!range.ToUtcExclusive.HasValue || todayUtc <  range.ToUtcExclusive.Value.Date.AddDays(1));

                    if (todayInRange && vm.TotalActiveEmployees > 0)
                    {
                        // Count today's INs from the already-filtered baseQ,
                        // so office/employee filters are respected.
                        int todayIns = baseQ.Count(x =>
                            x.Timestamp >= todayUtc &&
                            x.Timestamp <  tomorrowUtc &&
                            x.EventType  == "IN");

                        vm.AttendanceRatePercent =
                            (int)Math.Min(100, Math.Round(100.0 * todayIns / vm.TotalActiveEmployees));
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
            return RedirectToAction("Details", new { id });
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
                Employee        = (department ?? "").Trim(),
                IsSummaryReport = true,
                Page            = 1,
                PageSize        = 200
            };

            using (var db = new FaceAttendDBEntities())
            {
                vm.OfficeOptions = BuildOfficeOptions(db, vm.OfficeId);

                var range = ParseRange(vm.From, vm.To);
                vm.From             = range.FromText;
                vm.To               = range.ToText;
                vm.ActiveRangeLabel = range.Label;

                // 31-day cap (based on LOCAL date span)
                var daySpan = (range.ToLocalDate - range.FromLocalDate).TotalDays;
                if (daySpan > 31)
                {
                    TempData["msg"] = "Summary report is limited to 31 days. Please narrow your date range.";
                    return RedirectToAction("Index", new { from = vm.From, to = vm.To, officeId });
                }

                var policy = LoadAttendancePolicy(db);

                var q = db.AttendanceLogs.AsNoTracking().AsQueryable();

                if (range.FromUtc.HasValue)
                    q = q.Where(x => x.Timestamp >= range.FromUtc.Value);
                if (range.ToUtcExclusive.HasValue)
                    q = q.Where(x => x.Timestamp < range.ToUtcExclusive.Value);
                if (officeId.HasValue && officeId.Value > 0)
                    q = q.Where(x => x.OfficeId == officeId.Value);
                if (!string.IsNullOrWhiteSpace(department))
                    q = q.Where(x => x.Department.Contains(department));

                var raw = q
                    .Select(x => new RawLog
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

                vm.EmployeeSummary = raw
                    .GroupBy(x => new { x.EmpId, x.FullName, x.Dept })
                    .Select(eg =>
                    {
                        var byDay = eg
                            .GroupBy(x => x.Timestamp.ToLocalTime().Date)
                            .ToDictionary(g => g.Key, g => g.ToList());

                        var days = new List<DailyEmployeeRow>(dates.Count);
                        foreach (var dayLocal in dates)
                        {
                            byDay.TryGetValue(dayLocal, out var events);
                            days.Add(BuildDailyRow(dayLocal, events, policy));
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
        /// Web.config (default 10,000).  Includes WiFiSSID column.
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

                int max = AppSettings.GetInt("Attendance:ExportMaxRows", 10000);

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
                        Notes            = x.Notes,
                        WiFiSSID         = x.WiFiSSID        // NEW column
                    })
                    .ToList();

                var csv      = BuildCsv(rows);
                var bytes    = Encoding.UTF8.GetBytes(csv);
                var fileName = "attendance_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".csv";
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
                var range = ParseRange((from ?? "").Trim(), (to ?? "").Trim());

                // 31-day cap
                if ((range.ToLocalDate - range.FromLocalDate).TotalDays > 31)
                {
                    var bytesErr = Encoding.UTF8.GetBytes("Error: limited to 31 days per export.");
                    return File(bytesErr, "text/plain", "attendance_summary_error.txt");
                }

                var policy = LoadAttendancePolicy(db);

                var q = db.AttendanceLogs.AsNoTracking().AsQueryable();

                if (range.FromUtc.HasValue)
                    q = q.Where(x => x.Timestamp >= range.FromUtc.Value);
                if (range.ToUtcExclusive.HasValue)
                    q = q.Where(x => x.Timestamp < range.ToUtcExclusive.Value);
                if (officeId.HasValue && officeId.Value > 0)
                    q = q.Where(x => x.OfficeId == officeId.Value);
                if (!string.IsNullOrWhiteSpace(department))
                    q = q.Where(x => x.Department.Contains(department));

                var raw = q
                    .Select(x => new RawLog
                    {
                        EmpId      = x.Employee.EmployeeId,
                        FullName   = x.EmployeeFullName,
                        Dept       = x.Department,
                        EventType  = x.EventType,
                        Timestamp  = x.Timestamp
                    })
                    .OrderBy(x => x.EmpId)
                    .ThenBy(x => x.Timestamp)
                    .Take(50000)
                    .ToList();

                var dates = Enumerable.Range(0, (range.ToLocalDate - range.FromLocalDate).Days + 1)
                    .Select(i => range.FromLocalDate.AddDays(i))
                    .ToList();

                var sb = new StringBuilder();
                sb.AppendLine("EmployeeId,EmployeeName,Department,Date,FirstIn,LastOut,HoursNet,Status,LateMin,UndertimeMin");

                var grouped = raw.GroupBy(x => new { x.EmpId, x.FullName, x.Dept })
                    .OrderBy(g => g.Key.EmpId);

                foreach (var eg in grouped)
                {
                    var byDay = eg
                        .GroupBy(x => x.Timestamp.ToLocalTime().Date)
                        .ToDictionary(g => g.Key, g => g.ToList());

                    foreach (var dayLocal in dates)
                    {
                        byDay.TryGetValue(dayLocal, out var events);
                        var row = BuildDailyRow(dayLocal, events, policy);

                        sb.Append(JoinCsv(new[]
                        {
                            SafeCell(eg.Key.EmpId),
                            SafeCell(eg.Key.FullName),
                            SafeCell(eg.Key.Dept),
                            row.DateLabel,
                            row.FirstInUtc.HasValue ? row.FirstInUtc.Value.ToLocalTime().ToString("HH:mm") : "",
                            row.LastOutUtc.HasValue ? row.LastOutUtc.Value.ToLocalTime().ToString("HH:mm") : "",
                            (row.HoursNet ?? row.HoursRaw).HasValue
                                ? (row.HoursNet ?? row.HoursRaw).Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                                : "",
                            SafeCell(row.StatusLabel),
                            row.LateMinutes.HasValue ? row.LateMinutes.Value.ToString() : "",
                            row.UndertimeMinutes.HasValue ? row.UndertimeMinutes.Value.ToString() : ""
                        }));
                        sb.AppendLine();
                    }
                }

                var bytes = Encoding.UTF8.GetBytes(sb.ToString());
                var fileName = "attendance_summary_" + DateTime.Now.ToString("yyyyMMdd") + ".csv";
                return File(bytes, "text/csv", fileName);
            }
        }

        // ── Private helpers ──────────────────────────────────────────────────────

        private static List<SelectListItem> BuildOfficeOptions(
            FaceAttendDBEntities db, int? selected)
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
            public DateTime? FromUtc         { get; set; }
            public DateTime? ToUtcExclusive  { get; set; }
            public DateTime  FromLocalDate   { get; set; }
            public DateTime  ToLocalDate     { get; set; }
            public string    FromText        { get; set; }
            public string    ToText          { get; set; }
            public string    Label           { get; set; }
        }

        /// <summary>
        /// Parses the from/to strings into UTC boundaries.
        /// Accepts named presets: "today", "thisweek", "lastweek", "thismonth".
        /// Falls back to DateTime.TryParse for arbitrary date strings.
        /// </summary>
        private static RangeResult ParseRange(string from, string to)
        {
            var today       = DateTime.Now.Date;
            var defaultFrom = today.AddDays(-6);
            var defaultTo   = today;

            DateTime fromLocal;
            DateTime toLocal;

            // ── Named preset handling ────────────────────────────────────────────
            switch ((from ?? "").Trim().ToLowerInvariant())
            {
                case "today":
                    fromLocal = today;
                    toLocal   = today;
                    break;

                case "thisweek":
                    // Week starts Monday; DayOfWeek.Sunday == 0.
                    int dow1    = (int)today.DayOfWeek;
                    int offset1 = dow1 == 0 ? -6 : -(dow1 - 1);
                    fromLocal   = today.AddDays(offset1);
                    toLocal     = today;
                    break;

                case "lastweek":
                    int dow2    = (int)today.DayOfWeek;
                    int offset2 = dow2 == 0 ? -6 : -(dow2 - 1);
                    toLocal     = today.AddDays(offset2 - 1);   // last day of last week
                    fromLocal   = toLocal.AddDays(-6);           // first day of last week
                    break;

                case "thismonth":
                    fromLocal = new DateTime(today.Year, today.Month, 1);
                    toLocal   = today;
                    break;

                default:
                    // Arbitrary date string — try parse, fall back to defaults.
                    if (!DateTime.TryParse(from, out fromLocal)) fromLocal = defaultFrom;
                    fromLocal = fromLocal.Date;

                    if (!DateTime.TryParse(to, out toLocal)) toLocal = defaultTo;
                    toLocal = toLocal.Date;
                    break;
            }

            // Ensure from <= to.
            if (toLocal < fromLocal)
            {
                var tmp = fromLocal;
                fromLocal = toLocal;
                toLocal   = tmp;
            }

            // Build a human-readable label.
            var label = fromLocal == toLocal
                ? fromLocal.ToString("MMM d, yyyy")
                : fromLocal.ToString("MMM d") + " – " + toLocal.ToString("MMM d, yyyy");

            return new RangeResult
            {
                FromUtc        = fromLocal.ToUniversalTime(),
                ToUtcExclusive = toLocal.AddDays(1).ToUniversalTime(),
                FromLocalDate  = fromLocal,
                ToLocalDate    = toLocal,
                FromText       = fromLocal.ToString("yyyy-MM-dd"),
                ToText         = toLocal.ToString("yyyy-MM-dd"),
                Label          = label
            };
        }

        // ── Reporting policy + daily computation ───────────────────────────────

        private class AttendancePolicy
        {
            public TimeSpan WorkStart { get; set; }
            public TimeSpan WorkEnd { get; set; }
            public int GraceMinutes { get; set; }
            public double FullDayHours { get; set; }
            public double HalfDayHours { get; set; }
            public int LunchMinutes { get; set; }
            public double LunchDeductAfterHours { get; set; }
        }

        private class RawLog
        {
            public string EmpId { get; set; }
            public string FullName { get; set; }
            public string Dept { get; set; }
            public string EventType { get; set; }
            public DateTime Timestamp { get; set; } // UTC
        }

        private static AttendancePolicy LoadAttendancePolicy(FaceAttendDBEntities db)
        {
            // SystemConfigService keys are optional; fall back to Web.config.
            var sStart = SystemConfigService.GetString(db, "Attendance:WorkStart", AppSettings.GetString("Attendance:WorkStart", "08:00"));
            var sEnd   = SystemConfigService.GetString(db, "Attendance:WorkEnd",   AppSettings.GetString("Attendance:WorkEnd",   "17:00"));

            TimeSpan start;
            TimeSpan end;
            if (!TimeSpan.TryParse(sStart, out start)) start = new TimeSpan(8, 0, 0);
            if (!TimeSpan.TryParse(sEnd, out end))     end   = new TimeSpan(17, 0, 0);

            return new AttendancePolicy
            {
                WorkStart = start,
                WorkEnd = end,
                GraceMinutes = SystemConfigService.GetInt(db, "Attendance:GraceMinutes", AppSettings.GetInt("Attendance:GraceMinutes", 10)),
                FullDayHours = SystemConfigService.GetDouble(db, "Attendance:FullDayHours", AppSettings.GetDouble("Attendance:FullDayHours", 8)),
                HalfDayHours = SystemConfigService.GetDouble(db, "Attendance:HalfDayHours", AppSettings.GetDouble("Attendance:HalfDayHours", 4)),
                LunchMinutes = SystemConfigService.GetInt(db, "Attendance:LunchMinutes", AppSettings.GetInt("Attendance:LunchMinutes", 60)),
                LunchDeductAfterHours = SystemConfigService.GetDouble(db, "Attendance:LunchDeductAfterHours", AppSettings.GetDouble("Attendance:LunchDeductAfterHours", 5.5))
            };
        }

        private static DailyEmployeeRow BuildDailyRow(DateTime dayLocal, List<RawLog> events, AttendancePolicy p)
        {
            DateTime? firstInUtc = null;
            DateTime? lastOutUtc = null;

            if (events != null && events.Count > 0)
            {
                firstInUtc = events
                    .Where(x => x.EventType == "IN")
                    .OrderBy(x => x.Timestamp)
                    .Select(x => (DateTime?)x.Timestamp)
                    .FirstOrDefault();

                lastOutUtc = events
                    .Where(x => x.EventType == "OUT")
                    .OrderByDescending(x => x.Timestamp)
                    .Select(x => (DateTime?)x.Timestamp)
                    .FirstOrDefault();
            }

            var row = new DailyEmployeeRow
            {
                DateLocal = dayLocal,
                FirstInUtc = firstInUtc,
                LastOutUtc = lastOutUtc
            };

            bool hasIn = firstInUtc.HasValue;
            bool hasOut = lastOutUtc.HasValue;

            if (!hasIn && !hasOut)
            {
                row.StatusCode = "ABSENT";
                row.StatusLabel = "Absent";
                row.StatusBadgeClass = "bg-danger";
                return row;
            }

            if (hasIn && !hasOut)
            {
                row.StatusCode = "OPEN_SHIFT";
                row.StatusLabel = "Open shift";
                row.StatusBadgeClass = "bg-warning text-dark";
                return row;
            }

            if (!hasIn && hasOut)
            {
                row.StatusCode = "OUT_ONLY";
                row.StatusLabel = "OUT only";
                row.StatusBadgeClass = "bg-light text-dark border";
                return row;
            }

            // Both in and out
            var firstLocal = firstInUtc.Value.ToLocalTime();
            var lastLocal = lastOutUtc.Value.ToLocalTime();

            var rawHours = (lastLocal - firstLocal).TotalHours;
            if (rawHours < 0) rawHours = 0;
            row.HoursRaw = rawHours;

            double netHours = rawHours;
            if (rawHours >= p.LunchDeductAfterHours)
                netHours = Math.Max(0, rawHours - (p.LunchMinutes / 60.0));

            row.HoursNet = netHours;

            // Late minutes vs WorkStart + grace
            var graceStart = dayLocal.Date.Add(p.WorkStart).Add(TimeSpan.FromMinutes(p.GraceMinutes));
            int lateMin = (firstLocal > graceStart)
                ? (int)Math.Round((firstLocal - graceStart).TotalMinutes)
                : 0;

            row.LateMinutes = lateMin > 0 ? (int?)lateMin : null;

            int requiredMin = (int)Math.Round(p.FullDayHours * 60);
            int netMin = (int)Math.Round(netHours * 60);

            int undertimeMin = Math.Max(0, requiredMin - netMin);
            row.UndertimeMinutes = undertimeMin > 0 ? (int?)undertimeMin : null;

            bool isHalf = netHours >= p.HalfDayHours && netHours < p.FullDayHours;
            bool isFull = netHours >= p.FullDayHours;
            bool isUnder = netHours < p.HalfDayHours;

            if (isFull && lateMin == 0)
            {
                row.StatusCode = "ON_TIME";
                row.StatusLabel = "On time";
                row.StatusBadgeClass = "bg-success";
            }
            else if (isFull && lateMin > 0)
            {
                row.StatusCode = "LATE";
                row.StatusLabel = "Late";
                row.StatusBadgeClass = "bg-warning text-dark";
            }
            else if (isHalf)
            {
                row.StatusCode = lateMin > 0 ? "LATE_HALF_DAY" : "HALF_DAY";
                row.StatusLabel = lateMin > 0 ? "Late (half day)" : "Half day";
                row.StatusBadgeClass = "bg-info text-dark";
            }
            else if (isUnder)
            {
                row.StatusCode = "UNDERTIME";
                row.StatusLabel = "Undertime";
                row.StatusBadgeClass = "bg-secondary";
            }
            else
            {
                row.StatusCode = "PRESENT";
                row.StatusLabel = lateMin > 0 ? "Late" : "Present";
                row.StatusBadgeClass = lateMin > 0 ? "bg-warning text-dark" : "bg-success";
            }

            return row;
        }

        // ── CSV building ─────────────────────────────────────────────────────────

        private class ExportRow
        {
            public DateTime Timestamp        { get; set; }
            public string   EmpId            { get; set; }
            public string   EmployeeFullName { get; set; }
            public string   Department       { get; set; }
            public string   OfficeName       { get; set; }
            public string   EventType        { get; set; }
            public double?  LivenessScore    { get; set; }
            public double?  FaceDistance     { get; set; }
            public bool     LocationVerified { get; set; }
            public double?  GPSAccuracy      { get; set; }
            public bool     NeedsReview      { get; set; }
            public string   Notes            { get; set; }
            public string   WiFiSSID         { get; set; }   // NEW
        }

        private static string BuildCsv(IEnumerable<ExportRow> rows)
        {
            var sb = new StringBuilder();
            sb.AppendLine(
                "TimestampLocal,EmployeeId,EmployeeName,Department,Office," +
                "EventType,LivenessScore,FaceDistance,LocationVerified," +
                "GPSAccuracy,NeedsReview,WiFiSSID,Notes");

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
                            System.Globalization.CultureInfo.InvariantCulture) : "",
                    r.FaceDistance.HasValue
                        ? r.FaceDistance.Value.ToString("0.000",
                            System.Globalization.CultureInfo.InvariantCulture) : "",
                    r.LocationVerified ? "YES" : "NO",
                    r.GPSAccuracy.HasValue
                        ? r.GPSAccuracy.Value.ToString("0.0",
                            System.Globalization.CultureInfo.InvariantCulture) : "",
                    r.NeedsReview ? "YES" : "NO",
                    SafeCell(r.WiFiSSID),
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
