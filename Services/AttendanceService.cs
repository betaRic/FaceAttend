using System;
using System.Linq;

namespace FaceAttend.Services
{
    public static class AttendanceService
    {
        public class RecordResult
        {
            public bool Ok { get; set; }
            public string Code { get; set; }
            public string Message { get; set; }
            public string EventType { get; set; }
            public DateTime TimestampUtc { get; set; }
        }

        // Minimal rules for now:
        // - day reset (server local day)
        // - alternation: IN then OUT
        // - minimum gap to prevent double scans
        public static RecordResult Record(FaceAttendDBEntities db, AttendanceLog log)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (log == null) throw new ArgumentNullException(nameof(log));

            var nowUtc = DateTime.UtcNow;

            // Define "today" using server local time.
            var nowLocal = DateTime.Now;
            var startLocal = nowLocal.Date;
            var startUtc = startLocal.ToUniversalTime();
            var endUtc = startLocal.AddDays(1).ToUniversalTime();

            int minGapSeconds = AppSettings.GetInt("Attendance:MinGapSeconds", 10);

            var lastToday = db.AttendanceLogs
                .Where(x => x.EmployeeId == log.EmployeeId && x.Timestamp >= startUtc && x.Timestamp < endUtc)
                .OrderByDescending(x => x.Timestamp)
                .FirstOrDefault();

            if (lastToday != null)
            {
                var gap = (nowUtc - lastToday.Timestamp).TotalSeconds;
                if (gap >= 0 && gap < minGapSeconds)
                {
                    return new RecordResult
                    {
                        Ok = false,
                        Code = "TOO_SOON",
                        Message = "Please wait and scan again.",
                        TimestampUtc = nowUtc
                    };
                }
            }

            string next;
            if (lastToday == null) next = "IN";
            else if (string.Equals(lastToday.EventType, "IN", StringComparison.OrdinalIgnoreCase)) next = "OUT";
            else next = "IN";

            log.Timestamp = nowUtc;
            log.EventType = next;
            log.Source = string.IsNullOrWhiteSpace(log.Source) ? "KIOSK" : log.Source;

            db.AttendanceLogs.Add(log);
            db.SaveChanges();

            return new RecordResult
            {
                Ok = true,
                Code = "RECORDED",
                EventType = next,
                TimestampUtc = nowUtc,
                Message = next == "IN" ? "Time in recorded." : "Time out recorded."
            };
        }
    }
}
