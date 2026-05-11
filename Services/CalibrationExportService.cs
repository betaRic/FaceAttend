using System;
using System.Linq;
using System.Text;
using FaceAttend.Services.Biometrics;
using FaceAttend.Services.Helpers;

namespace FaceAttend.Services
{
    public static class CalibrationExportService
    {
        public static byte[] BuildCsv(FaceAttendDBEntities db, int riskyPairRows, int attendanceRows)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));

            var sb = new StringBuilder();
            sb.AppendLine(CsvHelper.JoinCsv(new[]
            {
                "rowType",
                "timestampUtc",
                "sourceType",
                "employeeId",
                "employeeName",
                "otherEmployeeId",
                "otherEmployeeName",
                "eventType",
                "distance",
                "secondDistance",
                "gap",
                "threshold",
                "similarity",
                "antiSpoof",
                "riskLevel",
                "reviewCodes",
                "recommendation"
            }));

            var risky = RiskyPairAuditService.Analyze(db, riskyPairRows);
            foreach (var row in risky.Rows)
            {
                sb.AppendLine(CsvHelper.JoinCsv(new[]
                {
                    "RISKY_PAIR",
                    risky.GeneratedAtUtc.ToString("o"),
                    "",
                    row.EmployeeId,
                    row.DisplayName,
                    row.OtherEmployeeId,
                    row.OtherDisplayName,
                    "",
                    row.Distance.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture),
                    "",
                    "",
                    risky.UnsafeDistance.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture),
                    "",
                    "",
                    row.RiskLevel,
                    "",
                    row.Recommendation
                }));
            }

            var logs = db.AttendanceLogs
                .Where(l => !l.IsVoided && l.FaceDistance.HasValue)
                .OrderByDescending(l => l.Timestamp)
                .Take(attendanceRows)
                .Select(l => new
                {
                    l.Timestamp,
                    l.EmployeeId,
                    l.EmployeeFullName,
                    l.Source,
                    l.EventType,
                    l.FaceDistance,
                    l.MatchThreshold,
                    l.FaceSimilarity,
                    l.AntiSpoofScore,
                    l.ReviewReasonCodes,
                    l.Notes
                })
                .ToList();

            foreach (var log in logs)
            {
                sb.AppendLine(CsvHelper.JoinCsv(new[]
                {
                    "ATTENDANCE_MATCH",
                    log.Timestamp.ToString("o"),
                    log.Source,
                    log.EmployeeId.ToString(),
                    log.EmployeeFullName,
                    "",
                    "",
                    log.EventType,
                    Format(log.FaceDistance),
                    "",
                    ExtractGap(log.Notes),
                    Format(log.MatchThreshold),
                    Format(log.FaceSimilarity),
                    Format(log.AntiSpoofScore),
                    "",
                    log.ReviewReasonCodes,
                    log.Notes
                }));
            }

            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        private static string Format(double? value)
        {
            return value.HasValue
                ? value.Value.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture)
                : "";
        }

        private static string ExtractGap(string notes)
        {
            if (string.IsNullOrWhiteSpace(notes))
                return "";

            var marker = "gap=";
            var idx = notes.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return "";

            var start = idx + marker.Length;
            var end = notes.IndexOfAny(new[] { ' ', '.', ';', ',' }, start);
            if (end < 0) end = notes.Length;
            return notes.Substring(start, end - start).Trim();
        }
    }
}
