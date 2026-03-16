using System;
using System.Data;
using System.Linq;

using FaceAttend.Services.Interfaces;

namespace FaceAttend.Services
{
    /// <summary>
    /// SAGUPA: Nagre-record ng attendance events (Time In / Time Out).
    /// 
    /// PAGLALARAWAN (Description):
    ///   Ang service na ito ang nagha-handle ng lahat ng attendance recording
    ///   para sa mga empleyado. Sinusubaybayan nito ang:
    ///   - Time In (pagpasok)
    ///   - Time Out (paglabas)
    ///   - MinGap checking (anti-double tap)
    ///   - Directional gaps (IN→OUT vs OUT→IN)
    /// 
    /// GINAGAMIT SA:
    ///   - KioskController.Attend() - kapag nag-scan ang empleyado
    ///   - Admin dashboard - manual entry (kung mayroon)
    /// 
    /// IMPORTANTENG PAALALA:
    ///   [OPTIMIZATION NEEDED] Ang Serializable isolation ay pinakamabagal at 
    ///   nagdudulot ng deadlocks sa maraming concurrent users. Isasaalang-alang
    ///   ang pagpalit sa Snapshot isolation kung supported ng SQL Express.
    /// 
    /// FIX APPLIED (Race Condition):
    ///   Dati, dalawang sabay na scan (same employee) ay puwedeng parehong 
    ///   makapasa sa MinGap check bago mag-insert — nagdudulot ng duplicate.
    /// 
    ///   Ang solusyon: SERIALIZABLE transaction. Hindi makakapag-insert ang
    ///   pangalawang scan habang may transaction ang unang scan.
    /// 
    ///   Note: Para sa high-volume, magdagdag ng unique filtered index sa DB:
    ///   (EmployeeId, CAST(Timestamp AS DATE), EventType)
    /// </summary>
    public class AttendanceService : IAttendanceService
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
            public DateTime TimestampUtc { get; set; }
            public int ApplicableGapSeconds { get; set; }
        }

        public RecordResult Record(AttendanceLog log, DateTime? attemptedAtUtc = null)
        {
            if (log == null) throw new ArgumentNullException(nameof(log));

            // SWITCHED TO LOCAL TIME: Timestamps stored in DB are now local (Asia/Manila)
            var nowLocal = attemptedAtUtc ?? TimeZoneHelper.NowLocal();

            // Direct date comparison since timestamps are now local
            var startLocal = nowLocal.Date;                    // start of today in local time
            var endLocal   = nowLocal.Date.AddDays(1);         // start of tomorrow in local time

            int minGapSeconds = ConfigurationService.GetInt(
                _db, "Attendance:MinGapSeconds",
                ConfigurationService.GetInt("Attendance:MinGapSeconds", 180));

            // --- Transaction: prevents concurrent duplicate scans ---
            // RepeatableRead prevents phantom reads within the transaction window.
            // Two concurrent scans for the same employee can both read lastToday before
            // either writes — ReadCommitted allowed that. RepeatableRead holds a shared
            // read lock on the rows read until commit, so the second scan will wait until
            // the first commits, then re-read and see the new record correctly.
            // This is lighter than Serializable (no range locks) and sufficient here
            // because we only query by EmployeeId within a date range.
            using (var tx = _db.Database.BeginTransaction(IsolationLevel.RepeatableRead))
            {
                try
                {
                    var lastToday = _db.AttendanceLogs
                        .Where(x =>
                            x.EmployeeId == log.EmployeeId &&
                            x.Timestamp >= startLocal &&
                            x.Timestamp < endLocal)
                        .OrderByDescending(x => x.Timestamp)
                        .FirstOrDefault();

                    // ── Tukuyin muna ang susunod na event BAGO mag-gap check ──────────────────
                    // Kailangan munang malaman kung IN->OUT o OUT->IN ang transition para
                    // magamit ang tamang directional gap limit.
                    string next;
                    if (lastToday == null)
                        next = "IN";
                    else if (string.Equals(lastToday.EventType, "IN", StringComparison.OrdinalIgnoreCase))
                        next = "OUT";
                    else
                        next = "IN";

                    // ── Directional MinGap check ─────────────────────────────────────────────
                    // IN->OUT : minimum 30 minuto (1800s) — hindi pwedeng mag-time-out agad
                    //           pagkatapos ng time-in (madalas na aksidente o abuse ito).
                    // OUT->IN : minimum 5 minuto  (300s)  — short break / pagbalik mula errand.
                    // Ang base minGapSeconds (180s) ay palaging enforced bilang anti-doubletap floor.
                    if (lastToday != null)
                    {
                        var gap = (nowLocal - lastToday.Timestamp).TotalSeconds;

                        int applicableGap;
                        string gapMessage;

                        if (string.Equals(lastToday.EventType, "IN", StringComparison.OrdinalIgnoreCase))
                        {
                            // IN -> OUT transition: mag-apply ng InToOut minimum gap
                            applicableGap = ConfigurationService.GetInt(
                                _db, "Attendance:MinGap:InToOutSeconds",
                                ConfigurationService.GetInt("Attendance:MinGap:InToOutSeconds", 1800));
                            var minsNeeded = (int)Math.Ceiling(applicableGap / 60.0);
                            gapMessage = "You just timed in. Please wait at least "
                                + minsNeeded + " minute(s) before timing out.";
                        }
                        else
                        {
                            // OUT -> IN transition: mag-apply ng OutToIn minimum gap
                            applicableGap = ConfigurationService.GetInt(
                                _db, "Attendance:MinGap:OutToInSeconds",
                                ConfigurationService.GetInt("Attendance:MinGap:OutToInSeconds", 300));
                            var minsNeeded = (int)Math.Ceiling(applicableGap / 60.0);
                            gapMessage = "Please wait at least "
                                + minsNeeded + " minute(s) before timing in again.";
                        }

                        // I-enforce ang base minGapSeconds bilang absolute floor (anti-doubletap).
                        // Sa normal config: 180s < 1800s at 300s — Math.Max ay walang epekto.
                        // Pero kung binago ng admin ang values, protektado pa rin tayo.
                        applicableGap = Math.Max(applicableGap, minGapSeconds);

                        if (gap >= 0 && gap < applicableGap)
                        {
                            tx.Rollback();
                            return new RecordResult
                            {
                                Ok      = false,
                                Code    = "TOO_SOON",
                                Message = gapMessage,
                                TimestampUtc = nowLocal,   // Now local time
                                ApplicableGapSeconds = applicableGap
                            };
                        }
                    }

                    log.Timestamp = nowLocal;  // Store as local time
                    log.EventType = next;
                    log.Source    = string.IsNullOrWhiteSpace(log.Source) ? "KIOSK" : log.Source;

                    _db.AttendanceLogs.Add(log);

                    try
                    {
                        _db.SaveChanges();
                    }
                    catch (System.Data.Entity.Infrastructure.DbUpdateException dbEx)
                    {
                        // Unique index violation -- a concurrent scan for the same employee
                        // + event type + day already committed. Treat as TOO_SOON.
                        try { tx.Rollback(); } catch { }

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
                                TimestampUtc = nowLocal   // Now local time
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
                        TimestampUtc = nowLocal,  // Now local time
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
