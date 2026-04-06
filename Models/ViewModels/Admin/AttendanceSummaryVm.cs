using System;
using System.Collections.Generic;

namespace FaceAttend.Models.ViewModels.Admin
{
    /// <summary>
    /// Per-employee summary row used by the SummaryReport view.
    /// Groups DailyEmployeeRow entries for each calendar day in the report range.
    /// </summary>
    public class EmployeeSummaryRow
    {
        public string EmployeeId       { get; set; }
        public string EmployeeFullName { get; set; }
        public string Department       { get; set; }

        public List<DailyEmployeeRow> Days { get; set; } = new List<DailyEmployeeRow>();
    }

    /// <summary>
    /// Computed attendance data for a single employee on a single calendar day.
    /// Populated by AttendanceReportService.BuildDailyRow().
    /// </summary>
    public class DailyEmployeeRow
    {
        public DateTime  DateLocal  { get; set; }
        public DateTime? FirstInUtc { get; set; }
        public DateTime? LastOutUtc { get; set; }

        // AM/PM split (noon boundary)
        public DateTime? AmIn  { get; set; }
        public DateTime? AmOut { get; set; }
        public DateTime? PmIn  { get; set; }
        public DateTime? PmOut { get; set; }

        public double? HoursRaw { get; set; }
        public double? HoursNet { get; set; }

        public string StatusCode       { get; set; }
        public string StatusLabel      { get; set; }
        public string StatusBadgeClass { get; set; }

        public int? LateMinutes      { get; set; }
        public int? UndertimeMinutes { get; set; }

        public string DateLabel        => DateLocal.ToString("yyyy-MM-dd");
        public string FirstInDisplay   => FirstInUtc.HasValue ? FirstInUtc.Value.ToString("HH:mm") : "-";
        public string LastOutDisplay   => LastOutUtc.HasValue ? LastOutUtc.Value.ToString("HH:mm") : "-";
        public string AmInDisplay      => AmIn.HasValue  ? AmIn.Value.ToString("HH:mm")  : "-";
        public string AmOutDisplay     => AmOut.HasValue ? AmOut.Value.ToString("HH:mm") : "-";
        public string PmInDisplay      => PmIn.HasValue  ? PmIn.Value.ToString("HH:mm")  : "-";
        public string PmOutDisplay     => PmOut.HasValue ? PmOut.Value.ToString("HH:mm") : "-";
        public string LateDisplay      => LateMinutes.HasValue      ? (LateMinutes.Value      + "m") : "-";
        public string UndertimeDisplay => UndertimeMinutes.HasValue ? (UndertimeMinutes.Value + "m") : "-";

        public string HoursDisplay
        {
            get
            {
                var h = HoursNet ?? HoursRaw;
                return h.HasValue ? h.Value.ToString("0.0") + "h" : "-";
            }
        }
    }
}
