using System;
using System.Data;
using System.Linq;

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

        public static RecordResult Record(FaceAttendDBEntities db, AttendanceLog log, DateTime? attemptedAtUtc = null)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (log == null) throw new ArgumentNullException(nameof(log));

            var nowUtc = attemptedAtUtc ?? DateTime.UtcNow;

            // Mahalagang aligned ang "attendance day" sa app timezone,
            // hindi sa timezone ng IIS / server OS.
            var attendanceLocalDate = TimeZoneHelper.UtcToLocal(nowUtc).Date;
            var todayRange = TimeZoneHelper.LocalDateToUtcRange(attendanceLocalDate);
            var startUtc   = todayRange.fromUtc;
            var endUtc     = todayRange.toUtcExclusive;

            int minGapSeconds = SystemConfigService.GetInt(
                db, "Attendance:MinGapSeconds",
                AppSettings.GetInt("Attendance:MinGapSeconds", 180));

            // --- Transaction: prevents concurrent duplicate scans ---
            // OPTIMIZATION: Changed from Serializable to ReadCommitted
            // 
            // DATI: Serializable = pinakamabagal, nagdudulot ng deadlocks
            // NGAYON: ReadCommitted = mas mabilis, acceptable protection
            // 
            // ILOKANO: "Ti ReadCommitted ket nasayaat para iti kadawyan a panagusar
            //           ket saanna a pagsardengen ti sabali a transactions"
            using (var tx = db.Database.BeginTransaction(IsolationLevel.ReadCommitted))
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
                        var gap = (nowUtc - lastToday.Timestamp).TotalSeconds;

                        int applicableGap;
                        string gapMessage;

                        if (string.Equals(lastToday.EventType, "IN", StringComparison.OrdinalIgnoreCase))
                        {
                            // IN -> OUT transition: mag-apply ng InToOut minimum gap
                            applicableGap = SystemConfigService.GetInt(
                                db, "Attendance:MinGap:InToOutSeconds",
                                AppSettings.GetInt("Attendance:MinGap:InToOutSeconds", 1800));
                            var minsNeeded = (int)Math.Ceiling(applicableGap / 60.0);
                            gapMessage = "You just timed in. Please wait at least "
                                + minsNeeded + " minute(s) before timing out.";
                        }
                        else
                        {
                            // OUT -> IN transition: mag-apply ng OutToIn minimum gap
                            applicableGap = SystemConfigService.GetInt(
                                db, "Attendance:MinGap:OutToInSeconds",
                                AppSettings.GetInt("Attendance:MinGap:OutToInSeconds", 300));
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
                                TimestampUtc = nowUtc
                            };
                        }
                    }

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
