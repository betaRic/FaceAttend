using System;
using System.Collections.Generic;
using System.Linq;

namespace FaceAttend.Services
{
    /// <summary>
    /// Business logic for recording and maintaining visitor activity logs.
    ///
    /// Two responsibilities:
    ///   1. RecordVisit  — known enrolled visitor recognised by face.
    ///   2. PurgeOldLogs — retention cleanup, deletes logs older than N years.
    ///
    /// Visitor *profiles* (the Visitor table) are never deleted by this service —
    /// only VisitorLog rows are removed during purge.
    /// </summary>
    public static class VisitorService
    {
        // ── Result types ─────────────────────────────────────────────────────────

        public class RecordResult
        {
            public bool   Ok          { get; set; }
            public string Code        { get; set; }
            public string Message     { get; set; }
            public string VisitorName { get; set; }
            public bool   IsKnown     { get; set; }
        }

        public class PurgeResult
        {
            public int      Deleted    { get; set; }
            public DateTime CutoffDate { get; set; }
        }

        // ── RecordVisit ──────────────────────────────────────────────────────────

        /// <summary>
        /// Records a visit for a known, enrolled Visitor profile.
        /// Updates VisitCount and LastVisitDate on the Visitor row.
        /// Inserts a VisitorLog row with Source = "KIOSK".
        /// </summary>
        /// <param name="db">Open EF context (caller manages lifetime).</param>
        /// <param name="visitorId">Matched visitor's primary key.</param>
        /// <param name="officeId">Office the kiosk is located at.</param>
        /// <param name="purpose">Optional visit purpose string (max 500 chars).</param>
        /// <param name="clientIp">Kiosk IP address for audit trail.</param>
        /// <param name="userAgent">Browser user agent string for audit trail.</param>
        public static RecordResult RecordVisit(
            FaceAttendDBEntities db,
            int    visitorId,
            int    officeId,
            string purpose,
            string clientIp,
            string userAgent)
        {
            if (db == null) throw new ArgumentNullException("db");

            var visitor = db.Visitors
                .FirstOrDefault(v => v.Id == visitorId && v.IsActive);

            if (visitor == null)
                return new RecordResult
                {
                    Ok      = false,
                    Code    = "VISITOR_NOT_FOUND",
                    Message = "Visitor profile not found or is no longer active."
                };

            var now = DateTime.UtcNow;

            var log = new VisitorLog
            {
                VisitorId   = visitorId,
                OfficeId    = officeId,
                Timestamp   = now,
                VisitorName = Trunc(visitor.Name, 400),
                Purpose = string.IsNullOrWhiteSpace(purpose) ? null : purpose.Trim().Substring(0, Math.Min(purpose.Trim().Length, 500)),
                Source      = "KIOSK",
                ClientIP    = Trunc(clientIp  ?? "", 100),
                UserAgent   = Trunc(userAgent ?? "", 1000)
            };

            db.VisitorLogs.Add(log);

            visitor.VisitCount++;
            visitor.LastVisitDate = now;

            db.SaveChanges();

            return new RecordResult
            {
                Ok          = true,
                Code        = "RECORDED",
                VisitorName = visitor.Name,
                IsKnown     = true,
                Message     = "Visit recorded. Welcome, " + visitor.Name + "."
            };
        }

        // ── PurgeOldLogs ─────────────────────────────────────────────────────────

        /// <summary>
        /// Deletes VisitorLog rows whose Timestamp is older than retentionYears.
        /// Uses batched LINQ deletes to avoid loading all rows into memory.
        /// Visitor profile rows are never touched.
        /// </summary>
        /// <param name="db">Open EF context (caller manages lifetime).</param>
        /// <param name="retentionYears">
        ///   How many years of logs to keep.  Minimum enforced at 1.
        /// </param>
        public static PurgeResult PurgeOldLogs(
            FaceAttendDBEntities db,
            int retentionYears)
        {
            if (db == null) throw new ArgumentNullException("db");
            if (retentionYears < 1) retentionYears = 1;

            var cutoff = DateTime.UtcNow.AddYears(-retentionYears);

            // Batch delete: pull IDs in groups of 500 to avoid loading entire rows
            // into memory and to keep the delete transaction small.
            const int batchSize = 500;
            int deleted = 0;

            while (true)
            {
                // Materialise just the primary keys — not the full entity rows.
                var ids = db.VisitorLogs
                    .Where(l => l.Timestamp < cutoff)
                    .OrderBy(l => l.Id)
                    .Take(batchSize)
                    .Select(l => l.Id)
                    .ToList();

                if (!ids.Any()) break;

                // Fetch only the rows whose IDs we just identified.
                var batch = db.VisitorLogs
                    .Where(l => ids.Contains(l.Id))
                    .ToList();

                db.VisitorLogs.RemoveRange(batch);
                db.SaveChanges();

                deleted += batch.Count;

                // If we got fewer than batchSize, there are no more rows to delete.
                if (batch.Count < batchSize) break;
            }

            return new PurgeResult
            {
                Deleted    = deleted,
                CutoffDate = cutoff
            };
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        private static string Trunc(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Length <= maxLen ? s : s.Substring(0, maxLen);
        }

        /// <summary>
        /// Prevents CSV formula injection in visitor names by prepending a single
        /// quote to values that start with a formula trigger character.
        /// </summary>
        private static string SanitiseName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return s;
            s = s.Trim();
            if (s.Length > 0 &&
                (s[0] == '=' || s[0] == '+' || s[0] == '-' || s[0] == '@'))
                s = "'" + s;
            return s;
        }

        /// <summary>
        /// Strips and sanitises the purpose field.
        /// </summary>
        private static string SanitisePurpose(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return (s ?? "").Trim();
            s = s.Trim();
            if (s.Length > 0 &&
                (s[0] == '=' || s[0] == '+' || s[0] == '-' || s[0] == '@'))
                s = "'" + s;
            return s;
        }
    }
}
