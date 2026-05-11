using System;
using System.Collections.Generic;
using System.Linq;
using FaceAttend.Services.Helpers;

namespace FaceAttend.Services
{
    public static class VisitorService
    {
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

            var nowLocal = TimeZoneHelper.NowLocal();

            var log = new VisitorLog
            {
                VisitorId   = visitorId,
                OfficeId    = officeId,
                Timestamp   = nowLocal,
                VisitorName = StringHelper.Truncate(visitor.Name, 400),
                Purpose = StringHelper.TruncateAndTrim(purpose, 500),
                Source      = "KIOSK",
                ClientIP    = StringHelper.Truncate(clientIp  ?? "", 100),
                UserAgent   = StringHelper.Truncate(userAgent ?? "", 1000)
            };

            db.VisitorLogs.Add(log);

            visitor.VisitCount++;
            visitor.LastVisitDate = nowLocal;

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

        public static PurgeResult PurgeOldLogs(
            FaceAttendDBEntities db,
            int retentionYears)
        {
            if (db == null) throw new ArgumentNullException("db");
            if (retentionYears < 1) retentionYears = 1;

            var cutoffLocal = TimeZoneHelper.NowLocal().AddYears(-retentionYears);

            const int batchSize = 500;
            int deleted = 0;

            while (true)
            {
                var ids = db.VisitorLogs
                    .Where(l => l.Timestamp < cutoffLocal)
                    .OrderBy(l => l.Id)
                    .Take(batchSize)
                    .Select(l => l.Id)
                    .ToList();

                if (!ids.Any()) break;

                var batch = db.VisitorLogs
                    .Where(l => ids.Contains(l.Id))
                    .ToList();

                db.VisitorLogs.RemoveRange(batch);
                db.SaveChanges();

                deleted += batch.Count;

                if (batch.Count < batchSize) break;
            }

            return new PurgeResult
            {
                Deleted    = deleted,
                CutoffDate = cutoffLocal
            };
        }
    }
}
