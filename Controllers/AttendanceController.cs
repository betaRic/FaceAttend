using System;
using System.Web.Mvc;
using FaceAttend.Models;
using FaceAttend.Services.Storage;

namespace FaceAttend.Controllers
{
    public class AttendanceController : Controller
    {
        private readonly IVisitorRepository _visitors = new JsonVisitorRepository();
        private readonly IAttendanceRepository _attendance = new JsonAttendanceRepository();

        public class VisitorInput
        {
            public string Name { get; set; }
            public string Purpose { get; set; }
        }

        public class RecordInput
        {
            public string EmployeeId { get; set; }
            public string Source { get; set; }
            public double? Distance { get; set; }
            public double? Similarity { get; set; }
        }

        [HttpPost]
        public ActionResult Visitor(VisitorInput input)
        {
            if (input == null) return Json(new { ok = false, error = "NO_BODY" });

            var name = (input.Name ?? "").Trim();
            var purpose = (input.Purpose ?? "").Trim();

            if (string.IsNullOrWhiteSpace(name))
                return Json(new { ok = false, error = "NAME_REQUIRED" });

            var record = new VisitorLogRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name,
                Purpose = purpose,
                Source = "KIOSK",
                CreatedUtc = DateTime.UtcNow.ToString("o"),
                ClientIp = GetClientIp(),
                UserAgent = Request.UserAgent
            };

            _visitors.Add(record);

            return Json(new { ok = true, id = record.Id });
        }

        // Called by kiosk after Identify success
        [HttpPost]
        public ActionResult Record(RecordInput input)
        {
            if (input == null) return Json(new { ok = false, error = "NO_BODY" });

            var emp = (input.EmployeeId ?? "").Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(emp))
                return Json(new { ok = false, error = "EMPLOYEE_ID_REQUIRED" });

            var src = (input.Source ?? "KIOSK").Trim().ToUpperInvariant();
            if (src.Length > 32) src = src.Substring(0, 32);

            var rec = new AttendanceLogRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                EmployeeId = emp,
                Source = src,
                Distance = input.Distance,
                Similarity = input.Similarity,
                CreatedUtc = DateTime.UtcNow.ToString("o"),
                ClientIp = GetClientIp(),
                UserAgent = Request.UserAgent
            };

            _attendance.Add(rec);

            return Json(new { ok = true, id = rec.Id, createdUtc = rec.CreatedUtc });
        }

        private string GetClientIp()
        {
            try
            {
                var xf = Request.Headers["X-Forwarded-For"];
                if (!string.IsNullOrWhiteSpace(xf))
                {
                    var parts = xf.Split(',');
                    if (parts.Length > 0) return parts[0].Trim();
                }
                return Request.UserHostAddress;
            }
            catch
            {
                return null;
            }
        }
    }
}
