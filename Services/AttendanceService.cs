using System;
using System.Data;
using System.Linq;

namespace FaceAttend.Services
{
    /// <summary>
    /// Records attendance events.
    ///
    /// Fix applied vs. original:
    ///   Race condition — two simultaneous scans (same employee, same kiosk) could
    ///   both pass the MinGap check before either inserted a row, resulting in
    ///   duplicate attendance records within the forbidden window.
    ///
    ///   The fix wraps the entire read-then-write in a SERIALIZABLE transaction.
    ///   Serializable prevents phantoms for the query range, so a second concurrent
    ///   scan cannot insert a competing row that would bypass the MinGap check.
    ///
    ///   Note: for high-volume deployments, adding a unique filtered index on
    ///   (EmployeeId, CAST(Timestamp AS DATE), EventType) at the database level
    ///   provides an additional safety net beyond the transaction.
    /// </summary>
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

        public static RecordResult Record(FaceAttendDBEntities db, AttendanceLog log)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (log == null) throw new ArgumentNullException(nameof(log));

            var nowUtc = DateTime.UtcNow;

            // "Today" boundaries in server-local time, converted to UTC for DB queries.
            var nowLocal    = DateTime.Now;
            var startLocal  = nowLocal.Date;
            var startUtc    = startLocal.ToUniversalTime();
            var endUtc      = startLocal.AddDays(1).ToUniversalTime();

            int minGapSeconds = SystemConfigService.GetInt(
                db, "Attendance:MinGapSeconds",
                AppSettings.GetInt("Attendance:MinGapSeconds", 10));

            // --- Transaction: prevents concurrent duplicate scans ---
            // Serializable prevents phantom inserts into the read range between
            // the MinGap check and the insert.
            using (var tx = db.Database.BeginTransaction(IsolationLevel.Serializable))
            {
                try
                {
                    var lastToday = db.AttendanceLogs
                        .Where(x =>
                            x.EmployeeId == log.EmployeeId &&
                            x.Timestamp >= startUtc &&
                            x.Timestamp < endUtc)
                        .OrderByDescending(x => x.Timestamp)
                        .FirstOrDefault();

                    if (lastToday != null)
                    {
                        var gap = (nowUtc - lastToday.Timestamp).TotalSeconds;
                        if (gap >= 0 && gap < minGapSeconds)
                        {
                            tx.Rollback();
                            return new RecordResult
                            {
                                Ok = false,
                                Code = "TOO_SOON",
                                Message = "Please wait and scan again.",
                                TimestampUtc = nowUtc
                            };
                        }
                    }

                    // Determine IN / OUT by simple alternation.
                    string next;
                    if (lastToday == null)
                        next = "IN";
                    else if (string.Equals(lastToday.EventType, "IN", StringComparison.OrdinalIgnoreCase))
                        next = "OUT";
                    else
                        next = "IN";

                    log.Timestamp = nowUtc;
                    log.EventType = next;
                    log.Source    = string.IsNullOrWhiteSpace(log.Source) ? "KIOSK" : log.Source;

                    db.AttendanceLogs.Add(log);
                    db.SaveChanges();

                    tx.Commit();

                    return new RecordResult
                    {
                        Ok          = true,
                        Code        = "RECORDED",
                        EventType   = next,
                        TimestampUtc = nowUtc,
                        Message     = next == "IN" ? "Time in recorded." : "Time out recorded."
                    };
                }
                catch
                {
                    try { tx.Rollback(); } catch { /* best effort */ }
                    throw;
                }
            }
        }
    }
}
