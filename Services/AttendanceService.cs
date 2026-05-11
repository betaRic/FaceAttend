using System;
using System.Data;
using System.Data.Entity;
using System.Diagnostics;
using System.Linq;

namespace FaceAttend.Services
{
    public class AttendanceService
    {
        private readonly FaceAttendDBEntities _db;

        public AttendanceService(FaceAttendDBEntities db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public class RecordResult
        {
            public bool Ok { get; set; }
            public string Code { get; set; }
            public string Message { get; set; }
            public string EventType { get; set; }
            public DateTime TimestampLocal { get; set; }
            public int ApplicableGapSeconds { get; set; }
            public long AttendanceLogId { get; set; }
        }

        public RecordResult Record(AttendanceLog log, DateTime? attemptedAtLocal = null)
        {
            if (log == null) throw new ArgumentNullException(nameof(log));

            var nowLocal = attemptedAtLocal ?? TimeZoneHelper.NowLocal();
            var todayRange = TimeZoneHelper.LocalDateRange(nowLocal);
            var startLocal = todayRange.fromLocalInclusive;
            var endLocal = todayRange.toLocalExclusive;

            int minGapSeconds = ConfigurationService.GetInt(
                _db, "Attendance:MinGapSeconds",
                ConfigurationService.GetInt("Attendance:MinGapSeconds", 180));

            using (var tx = _db.Database.BeginTransaction(IsolationLevel.Serializable))
            {
                try
                {
                    var lastToday = _db.AttendanceLogs
                        .Where(x =>
                            x.EmployeeId == log.EmployeeId &&
                            !x.IsVoided &&
                            x.Timestamp >= startLocal &&
                            x.Timestamp < endLocal)
                        .OrderByDescending(x => x.Timestamp)
                        .FirstOrDefault();

                    string next;
                    if (lastToday == null)
                        next = "IN";
                    else if (string.Equals(lastToday.EventType, "IN", StringComparison.OrdinalIgnoreCase))
                        next = "OUT";
                    else
                        next = "IN";

                    if (lastToday != null)
                    {
                        var gap = (nowLocal - lastToday.Timestamp).TotalSeconds;

                        int applicableGap;
                        string gapMessage;

                        if (string.Equals(lastToday.EventType, "IN", StringComparison.OrdinalIgnoreCase))
                        {
                            applicableGap = ConfigurationService.GetInt(
                                _db, "Attendance:MinGap:InToOutSeconds",
                                ConfigurationService.GetInt("Attendance:MinGap:InToOutSeconds", 1800));
                            var minsNeeded = (int)Math.Ceiling(applicableGap / 60.0);
                            gapMessage = "You just timed in. Please wait at least "
                                + minsNeeded + " minute(s) before timing out.";
                        }
                        else
                        {
                            applicableGap = ConfigurationService.GetInt(
                                _db, "Attendance:MinGap:OutToInSeconds",
                                ConfigurationService.GetInt("Attendance:MinGap:OutToInSeconds", 300));
                            var minsNeeded = (int)Math.Ceiling(applicableGap / 60.0);
                            gapMessage = "Please wait at least "
                                + minsNeeded + " minute(s) before timing in again.";
                        }

                        applicableGap = Math.Max(applicableGap, minGapSeconds);

                        if (gap >= 0 && gap < applicableGap)
                        {
                            tx.Rollback();
                            return new RecordResult
                            {
                                Ok      = false,
                                Code    = "TOO_SOON",
                                Message = gapMessage,
                                TimestampLocal = nowLocal,
                                ApplicableGapSeconds = applicableGap
                            };
                        }
                    }

                    log.Timestamp = nowLocal;
                    log.AttemptedAtLocal = nowLocal;
                    log.EventType = next;
                    log.Source    = string.IsNullOrWhiteSpace(log.Source) ? "KIOSK" : log.Source;
                    log.ReviewStatus = string.IsNullOrWhiteSpace(log.ReviewStatus)
                        ? (log.NeedsReview ? "PENDING" : "NONE")
                        : log.ReviewStatus;
                    log.IsVoided = false;

                    _db.AttendanceLogs.Add(log);

                    try
                    {
                        _db.SaveChanges();
                    }
                    catch (System.Data.Entity.Infrastructure.DbUpdateException dbEx)
                    {
                        RollbackQuietly(tx);

                        var inner = dbEx.InnerException?.InnerException?.Message
                                 ?? dbEx.InnerException?.Message ?? dbEx.Message;

                        if (inner.IndexOf("UX_AttendanceLogs", StringComparison.OrdinalIgnoreCase) >= 0
                         || inner.IndexOf("duplicate key", StringComparison.OrdinalIgnoreCase) >= 0
                         || inner.IndexOf("unique index", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return new RecordResult
                            {
                                Ok      = false,
                                Code    = "TOO_SOON",
                                Message = "Already scanned. Duplicate record prevented.",
                                TimestampLocal = nowLocal
                            };
                        }
                        throw;
                    }

                    tx.Commit();

                    return new RecordResult
                    {
                        Ok          = true,
                        Code        = "RECORDED",
                        EventType   = next,
                        TimestampLocal = nowLocal,
                        AttendanceLogId = log.Id,
                        Message     = next == "IN" ? "Time in recorded." : "Time out recorded."
                    };
                }
                catch
                {
                    RollbackQuietly(tx);
                    throw;
                }
            }
        }

        private static void RollbackQuietly(DbContextTransaction tx)
        {
            try
            {
                tx.Rollback();
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("[AttendanceService] Rollback failed: " + ex.Message);
            }
        }
    }
}
