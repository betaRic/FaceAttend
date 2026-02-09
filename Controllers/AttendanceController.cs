using System;
using System.Web.Mvc;
using FaceAttend.Models;
using FaceAttend.Services.Storage;

namespace FaceAttend.Controllers
{
    public class AttendanceController : Controller
    {
        private readonly IVisitorRepository _visitors = new JsonVisitorRepository();

        public class VisitorInput
        {
            public string Name { get; set; }
            public string Purpose { get; set; }
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

        private string GetClientIp()
        {
            try
            {
                var xf = Request.Headers["X-Forwarded-For"];
                if (!string.IsNullOrWhiteSpace(xf))
                {
                    // first IP in list
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
