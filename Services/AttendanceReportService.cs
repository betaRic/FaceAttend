using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FaceAttend.Models.ViewModels.Admin;
using FaceAttend.Services.Helpers;

namespace FaceAttend.Services
{
    /// <summary>
    /// Builds attendance report rows and CSV exports.
    /// Extracted from AttendanceController to keep the controller thin.
    /// </summary>
    public static class AttendanceReportService
    {
        public class AttendancePolicy
        {
            public TimeSpan WorkStart { get; set; }
            public TimeSpan WorkEnd { get; set; }
            public int GraceMinutes { get; set; }
            public double FullDayHours { get; set; }
            public double HalfDayHours { get; set; }
            public int LunchMinutes { get; set; }
            public double LunchDeductAfterHours { get; set; }
        }

        public class RawLog
        {
            public string EmpId { get; set; }
            public string FullName { get; set; }
            public string Dept { get; set; }
            public string EventType { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class ExportRow
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
            public string   WiFiBSSID        { get; set; }
        }

        public static AttendancePolicy LoadPolicy()
        {
            var sStart = ConfigurationService.GetString("Attendance:WorkStart", "08:00");
            var sEnd   = ConfigurationService.GetString("Attendance:WorkEnd",   "17:00");

            TimeSpan start;
            TimeSpan end;
            if (!TimeSpan.TryParse(sStart, out start)) start = new TimeSpan(8, 0, 0);
            if (!TimeSpan.TryParse(sEnd, out end))     end   = new TimeSpan(17, 0, 0);

            return new AttendancePolicy
            {
                WorkStart             = start,
                WorkEnd               = end,
                GraceMinutes          = ConfigurationService.GetInt("Attendance:GraceMinutes",          10),
                FullDayHours          = ConfigurationService.GetDouble("Attendance:FullDayHours",       8),
                HalfDayHours          = ConfigurationService.GetDouble("Attendance:HalfDayHours",       4),
                LunchMinutes          = ConfigurationService.GetInt("Attendance:LunchMinutes",          60),
                LunchDeductAfterHours = ConfigurationService.GetDouble("Attendance:LunchDeductAfterHours", 5.5)
            };
        }

        public static DailyEmployeeRow BuildDailyRow(DateTime dayLocal, List<RawLog> events, AttendancePolicy p)
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

            var noon = dayLocal.Date.AddHours(12);

            var amIns  = events != null ? events.Where(x => x.EventType == "IN"  && x.Timestamp < noon).OrderBy(x => x.Timestamp).ToList() : null;
            var amOuts = events != null ? events.Where(x => x.EventType == "OUT" && x.Timestamp < noon).OrderBy(x => x.Timestamp).ToList() : null;
            var pmIns  = events != null ? events.Where(x => x.EventType == "IN"  && x.Timestamp >= noon).OrderBy(x => x.Timestamp).ToList() : null;
            var pmOuts = events != null ? events.Where(x => x.EventType == "OUT" && x.Timestamp >= noon).OrderByDescending(x => x.Timestamp).ToList() : null;

            var row = new DailyEmployeeRow
            {
                DateLocal  = dayLocal,
                FirstInUtc = firstInUtc,
                LastOutUtc = lastOutUtc,
                AmIn  = amIns  != null && amIns.Any()  ? (DateTime?)amIns.First().Timestamp  : null,
                AmOut = amOuts != null && amOuts.Any() ? (DateTime?)amOuts.First().Timestamp : null,
                PmIn  = pmIns  != null && pmIns.Any()  ? (DateTime?)pmIns.First().Timestamp  : null,
                PmOut = pmOuts != null && pmOuts.Any() ? (DateTime?)pmOuts.First().Timestamp : null,
            };

            bool hasIn  = firstInUtc.HasValue;
            bool hasOut = lastOutUtc.HasValue;

            if (!hasIn && !hasOut)
            {
                row.StatusCode       = "ABSENT";
                row.StatusLabel      = "Absent";
                row.StatusBadgeClass = "bg-danger";
                return row;
            }

            if (hasIn && !hasOut)
            {
                row.StatusCode       = "OPEN_SHIFT";
                row.StatusLabel      = "Open shift";
                row.StatusBadgeClass = "bg-warning text-dark";
                return row;
            }

            if (!hasIn && hasOut)
            {
                row.StatusCode       = "OUT_ONLY";
                row.StatusLabel      = "OUT only";
                row.StatusBadgeClass = "bg-light text-dark border";
                return row;
            }

            // Both in and out — timestamps are stored in local time
            var firstLocal = firstInUtc.Value;
            var lastLocal  = lastOutUtc.Value;

            var rawHours = (lastLocal - firstLocal).TotalHours;
            if (rawHours < 0) rawHours = 0;
            row.HoursRaw = rawHours;

            double netHours = rawHours;
            if (rawHours >= p.LunchDeductAfterHours)
                netHours = Math.Max(0, rawHours - (p.LunchMinutes / 60.0));

            row.HoursNet = netHours;

            var graceStart = dayLocal.Date.Add(p.WorkStart).Add(TimeSpan.FromMinutes(p.GraceMinutes));
            int lateMin = (firstLocal > graceStart)
                ? (int)Math.Round((firstLocal - graceStart).TotalMinutes)
                : 0;

            row.LateMinutes = lateMin > 0 ? (int?)lateMin : null;

            int requiredMin = (int)Math.Round(p.FullDayHours * 60);
            int netMin      = (int)Math.Round(netHours * 60);
            int undertimeMin = Math.Max(0, requiredMin - netMin);
            row.UndertimeMinutes = undertimeMin > 0 ? (int?)undertimeMin : null;

            bool isHalf  = netHours >= p.HalfDayHours && netHours < p.FullDayHours;
            bool isFull  = netHours >= p.FullDayHours;
            bool isUnder = netHours < p.HalfDayHours;

            if (isFull && lateMin == 0)
            {
                row.StatusCode       = "ON_TIME";
                row.StatusLabel      = "On time";
                row.StatusBadgeClass = "bg-success";
            }
            else if (isFull && lateMin > 0)
            {
                row.StatusCode       = "LATE";
                row.StatusLabel      = "Late";
                row.StatusBadgeClass = "bg-warning text-dark";
            }
            else if (isHalf)
            {
                row.StatusCode       = lateMin > 0 ? "LATE_HALF_DAY" : "HALF_DAY";
                row.StatusLabel      = lateMin > 0 ? "Late (half day)" : "Half day";
                row.StatusBadgeClass = "bg-info text-dark";
            }
            else if (isUnder)
            {
                row.StatusCode       = "UNDERTIME";
                row.StatusLabel      = "Undertime";
                row.StatusBadgeClass = "bg-secondary";
            }
            else
            {
                row.StatusCode       = "PRESENT";
                row.StatusLabel      = lateMin > 0 ? "Late" : "Present";
                row.StatusBadgeClass = lateMin > 0 ? "bg-warning text-dark" : "bg-success";
            }

            return row;
        }

        public static string BuildCsv(IEnumerable<ExportRow> rows)
        {
            var sb = new StringBuilder();
            sb.AppendLine(
                "TimestampLocal,EmployeeId,EmployeeName,Department,Office," +
                "EventType,LivenessScore,FaceDistance,LocationVerified," +
                "GPSAccuracy,NeedsReview,WiFiBSSID,Notes");

            foreach (var r in rows)
            {
                var local = r.Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
                sb.Append(CsvHelper.JoinCsv(new[]
                {
                    local,
                    CsvHelper.SafeCell(r.EmpId),
                    CsvHelper.SafeCell(r.EmployeeFullName),
                    CsvHelper.SafeCell(r.Department),
                    CsvHelper.SafeCell(r.OfficeName),
                    CsvHelper.SafeCell(r.EventType),
                    r.LivenessScore.HasValue
                        ? r.LivenessScore.Value.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) : "",
                    r.FaceDistance.HasValue
                        ? r.FaceDistance.Value.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) : "",
                    r.LocationVerified ? "YES" : "NO",
                    r.GPSAccuracy.HasValue
                        ? r.GPSAccuracy.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "",
                    r.NeedsReview ? "YES" : "NO",
                    CsvHelper.SafeCell(r.WiFiBSSID),
                    CsvHelper.SafeCell(r.Notes)
                }));
                sb.AppendLine();
            }

            return sb.ToString();
        }
    }
}
