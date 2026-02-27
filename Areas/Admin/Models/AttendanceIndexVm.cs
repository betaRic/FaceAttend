using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Web.Mvc;

namespace FaceAttend.Areas.Admin.Models
{
    // ── Summary rows (populated on Index and SummaryReport) ─────────────────────

    public class DailySummaryRow
    {
        public DateTime Date     { get; set; }
        public int      InCount  { get; set; }
        public int      OutCount { get; set; }

        // Helpers used by the view
        public int    Total     => InCount + OutCount;
        public string DateLabel => Date.ToLocalTime().ToString("MMM d");
    }

    public class OfficeSummaryRow
    {
        public string OfficeName { get; set; }
        public int    InCount    { get; set; }
        public int    OutCount   { get; set; }
        public int    Total      => InCount + OutCount;
    }

    // ── Per-employee summary rows (populated on SummaryReport only) ──────────────

    public class EmployeeSummaryRow
    {
        public string EmployeeId       { get; set; }
        public string EmployeeFullName { get; set; }
        public string Department       { get; set; }

        // One entry per calendar day (UTC date) that has at least one event.
        public List<DailyEmployeeRow> Days { get; set; } = new List<DailyEmployeeRow>();
    }

    public class DailyEmployeeRow
    {
        // Local date (NOT UTC date)
        public DateTime DateLocal { get; set; }

        // Raw UTC timestamps (stored in DB)
        public DateTime? FirstInUtc { get; set; }
        public DateTime? LastOutUtc { get; set; }

        // Computed values
        public double? HoursRaw { get; set; }
        public double? HoursNet { get; set; }

        // Status
        public string StatusCode { get; set; }
        public string StatusLabel { get; set; }
        public string StatusBadgeClass { get; set; }

        public int? LateMinutes { get; set; }
        public int? UndertimeMinutes { get; set; }

        // Display helpers
        public string DateLabel => DateLocal.ToString("yyyy-MM-dd");

        public string FirstInDisplay => FirstInUtc.HasValue
            ? FirstInUtc.Value.ToLocalTime().ToString("HH:mm")
            : "-";

        public string LastOutDisplay => LastOutUtc.HasValue
            ? LastOutUtc.Value.ToLocalTime().ToString("HH:mm")
            : "-";

        public string HoursDisplay
        {
            get
            {
                var h = HoursNet ?? HoursRaw;
                return h.HasValue ? h.Value.ToString("0.0") + "h" : "-";
            }
        }

        public string LateDisplay => LateMinutes.HasValue ? (LateMinutes.Value + "m") : "-";
        public string UndertimeDisplay => UndertimeMinutes.HasValue ? (UndertimeMinutes.Value + "m") : "-";
    }

    // ── Row in the main attendance table ─────────────────────────────────────────

    public class AttendanceRowVm
    {
        public long     Id               { get; set; }
        public DateTime TimestampUtc     { get; set; }
        public string   EmployeeId       { get; set; }
        public string   EmployeeFullName { get; set; }
        public string   Department       { get; set; }
        public string   OfficeName       { get; set; }
        public string   EventType        { get; set; }
        public double?  LivenessScore    { get; set; }
        public double?  FaceDistance     { get; set; }
        public bool     LocationVerified { get; set; }
        public bool     NeedsReview      { get; set; }
        public string   WiFiSSID         { get; set; }   // NEW: added for export & detail display
    }

    // ── Index ViewModel ───────────────────────────────────────────────────────────

    public class AttendanceIndexVm
    {
        // ── Filter parameters (query string) ─────────────────────────────────────

        [Display(Name = "From")]
        public string From { get; set; }

        [Display(Name = "To")]
        public string To { get; set; }

        [Display(Name = "Office")]
        public int? OfficeId { get; set; }

        [Display(Name = "Employee")]
        public string Employee { get; set; }

        [Display(Name = "Department")]
        public string DepartmentFilter { get; set; }

        [Display(Name = "Type")]
        public string EventType { get; set; }

        [Display(Name = "Needs review")]
        public bool NeedsReviewOnly { get; set; }

        public int Page     { get; set; } = 1;
        public int PageSize { get; set; } = 25;

        // ── Core output ──────────────────────────────────────────────────────────

        public int Total            { get; set; }
        public int TotalNeedsReview { get; set; }
        public int TotalPages
        {
            get
            {
                if (PageSize <= 0) return 1;
                var pages = (int)Math.Ceiling((double)Total / PageSize);
                return pages <= 0 ? 1 : pages;
            }
        }

        public string ActiveRangeLabel { get; set; }

        public List<SelectListItem>  OfficeOptions { get; set; } = new List<SelectListItem>();
        public List<AttendanceRowVm> Rows          { get; set; } = new List<AttendanceRowVm>();

        // ── Summary statistics (Index page cards) ────────────────────────────────

        // True when the result set has data and summary cards should render.
        public bool ShowSummary { get; set; } = false;

        // Daily breakdown — up to 14 days within the filter window, ascending.
        public List<DailySummaryRow>  DailyBreakdown  { get; set; } = new List<DailySummaryRow>();

        // All offices represented in the filtered result, sorted by total volume.
        public List<OfficeSummaryRow> OfficeBreakdown { get; set; } = new List<OfficeSummaryRow>();

        // Total active employees — used for attendance rate calculation.
        public int TotalActiveEmployees { get; set; }

        // Today's IN count ÷ TotalActiveEmployees × 100.
        // Null when today is outside the selected date range.
        public int? AttendanceRatePercent { get; set; }

        // ── Export ───────────────────────────────────────────────────────────────

        // Configurable cap displayed in the Export CSV button label.
        public int ExportMaxRows { get; set; } = 10000;

        // ── Summary Report (SummaryReport action only) ───────────────────────────

        // True when this VM is used for the SummaryReport view, not the Index view.
        public bool IsSummaryReport { get; set; } = false;

        // Per-employee daily breakdown — populated only on SummaryReport.
        public List<EmployeeSummaryRow> EmployeeSummary { get; set; } = new List<EmployeeSummaryRow>();
    }
}
