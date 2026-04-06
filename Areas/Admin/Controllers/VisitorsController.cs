using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Web;
using System.Web.Mvc;
using FaceAttend.Filters;
using FaceAttend.Models.ViewModels.Admin;
using FaceAttend.Areas.Admin.Helpers;
using FaceAttend.Services;
using FaceAttend.Services.Security;
using FaceAttend.Services.Biometrics;
using FaceAttend.Services.Helpers;

namespace FaceAttend.Areas.Admin.Controllers
{
    [AdminAuthorize]
    public class VisitorsController : Controller
    {
        [HttpGet]
        public ActionResult Index(string q, bool? showInactive)
        {
            ViewBag.Title = "Visitors";
            q = (q ?? "").Trim();

            using (var db = new FaceAttendDBEntities())
            {
                var baseQ = db.Visitors.AsNoTracking().AsQueryable();

                if (!string.IsNullOrWhiteSpace(q))
                    baseQ = baseQ.Where(v => v.Name.Contains(q));

                if (!(showInactive ?? false))
                    baseQ = baseQ.Where(v => v.IsActive);

                var cap = ConfigurationService.GetInt("Visitors:MaxListRows", 500);
                if (cap < 50) cap = 50;
                if (cap > 5000) cap = 5000;

                var list = baseQ
                    .OrderByDescending(v => v.LastVisitDate)
                    .Take(cap + 1)
                    .ToList();

                var truncated = list.Count > cap;
                if (truncated) list = list.Take(cap).ToList();

                ViewBag.Query = q;
                ViewBag.ShowInactive = (showInactive ?? false);
                ViewBag.Truncated = truncated;
                ViewBag.Cap = cap;

                return View(list);
            }
        }

        [HttpGet]
        public ActionResult Create()
        {
            ViewBag.Title = "Add Visitor";
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Create(string name)
        {
            name = (name ?? "").Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                TempData["msg"] = "Name is required.";
                TempData["msgKind"] = "warning";
                return RedirectToAction("Create");
            }

            using (var db = new FaceAttendDBEntities())
            {
                var now = DateTime.UtcNow;
                var v = new Visitor
                {
                    Name = name,
                    FaceEncodingBase64 = null,
                    VisitCount = 0,
                    FirstVisitDate = now,
                    LastVisitDate = now,
                    IsActive = true
                };

                db.Visitors.Add(v);
                db.SaveChanges();
            }

            TempData["msg"] = "Visitor created.";
            TempData["msgKind"] = "success";
            return RedirectToAction("Index");
        }

        [HttpGet]
        public ActionResult Edit(int id)
        {
            ViewBag.Title = "Edit Visitor";
            using (var db = new FaceAttendDBEntities())
            {
                var v = db.Visitors.FirstOrDefault(x => x.Id == id);
                if (v == null) return HttpNotFound();

                var perFrame = ConfigurationService.GetDouble("Biometrics:LivenessThreshold", 0.75);
                ViewBag.PerFrame = perFrame.ToString("0.00####", CultureInfo.InvariantCulture);

                return View(v);
            }
        }


        /// <summary>
        /// AJAX: Save face enrollment for a visitor (live camera path).
        /// Called by performEnrollment() in enrollment-core.js via enrollUrl.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult EnrollFace(string employeeId)
        {
            int visitorId;
            if (!int.TryParse(employeeId, out visitorId))
                return JsonResponseBuilder.Error("INVALID_ID", "Invalid visitor ID.");

            var files = new System.Collections.Generic.List<HttpPostedFileBase>();
            for (int i = 0; i < Request.Files.Count; i++)
            {
                var f = Request.Files[i];
                if (f != null && f.ContentLength > 0) files.Add(f);
            }

            if (files.Count == 0)
                return JsonResponseBuilder.Error("NO_IMAGE");

            var isMobile  = DeviceService.IsMobileDevice(Request);
            var maxStored = ConfigurationService.GetInt("Biometrics:Enroll:MaxStoredVectors", 5);
            var vecs      = new System.Collections.Generic.List<double[]>();

            foreach (var img in files.Take(maxStored))
            {
                var scan = FaceAttend.Services.Biometrics.FastScanPipeline.EnrollmentScanInMemory(img, null, isMobile);
                if (scan.Ok && scan.FaceEncoding != null) vecs.Add(scan.FaceEncoding);
            }

            if (vecs.Count == 0)
                return JsonResponseBuilder.Error("NO_FACE", "No valid face found in uploaded frames.");

            using (var db = new FaceAttendDBEntities())
            {
                var visitor = db.Visitors.FirstOrDefault(v => v.Id == visitorId);
                if (visitor == null) return JsonResponseBuilder.NotFound("Visitor");

                var bestBytes = FaceAttend.Services.Biometrics.DlibBiometrics.EncodeToBytes(vecs[0]);
                visitor.FaceEncodingBase64 = FaceAttend.Services.Biometrics.BiometricCrypto.ProtectBase64Bytes(bestBytes);
                visitor.IsActive = true;
                db.SaveChanges();
                VisitorFaceIndex.Invalidate();
            }

            return JsonResponseBuilder.Success(new { savedVectors = vecs.Count },
                string.Format("{0} face sample(s) saved for visitor.", vecs.Count));
        }

        [ValidateAntiForgeryToken]
        public ActionResult Edit(int id, string name, bool isActive = true)
        {
            name = (name ?? "").Trim();

            using (var db = new FaceAttendDBEntities())
            {
                var v = db.Visitors.FirstOrDefault(x => x.Id == id);
                if (v == null) return HttpNotFound();

                if (string.IsNullOrWhiteSpace(name))
                {
                    TempData["msg"] = "Name is required.";
                    TempData["msgKind"] = "warning";
                    return RedirectToAction("Edit", new { id });
                }

                v.Name = name;
                v.IsActive = isActive;
                db.SaveChanges();

                VisitorFaceIndex.Invalidate();
            }

            TempData["msg"] = "Visitor updated.";
            TempData["msgKind"] = "success";
            return RedirectToAction("Index");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Deactivate(int id)
        {
            using (var db = new FaceAttendDBEntities())
            {
                var v = db.Visitors.FirstOrDefault(x => x.Id == id);
                if (v == null) return HttpNotFound();

                var visitorName = v.Name;

                // PERMANENT DELETION: Delete all visitor logs first
                var visitorLogs = db.VisitorLogs.Where(vl => vl.VisitorId == id).ToList();
                foreach (var log in visitorLogs)
                {
                    db.VisitorLogs.Remove(log);
                }

                // Delete visitor record permanently
                db.Visitors.Remove(v);
                db.SaveChanges();

                VisitorFaceIndex.Invalidate();
            }

            TempData["msg"] = "Visitor permanently deleted.";
            TempData["msgKind"] = "success";
            return RedirectToAction("Index");
        }

        [HttpGet]
        public ActionResult Enroll(int id)
        {
            ViewBag.Title = "Enroll Visitor Face";
            using (var db = new FaceAttendDBEntities())
            {
                var v = db.Visitors.FirstOrDefault(x => x.Id == id);
                if (v == null) return HttpNotFound();
                return View(v);
            }
        }

        [HttpGet]
        public ActionResult Logs(string from, string to, int? officeId, string q, bool? knownOnly)
        {
            ViewBag.Title = "Visitor Logs";

            using (var db = new FaceAttendDBEntities())
            {
                var range = AdminQueryHelper.ParseRange((from ?? "").Trim(), (to ?? "").Trim());

                var logs = db.VisitorLogs.AsNoTracking().AsQueryable();

                if (range.FromUtc.HasValue) logs = logs.Where(x => x.Timestamp >= range.FromUtc.Value);
                if (range.ToUtcExclusive.HasValue) logs = logs.Where(x => x.Timestamp < range.ToUtcExclusive.Value);

                if (officeId.HasValue && officeId.Value > 0)
                    logs = logs.Where(x => x.OfficeId == officeId.Value);

                q = (q ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(q))
                    logs = logs.Where(x => x.VisitorName.Contains(q) || x.Purpose.Contains(q));

                if (knownOnly ?? false)
                    logs = logs.Where(x => x.VisitorId != null);

                var rows = logs
                    .OrderByDescending(x => x.Timestamp)
                    .Take(5000)
                    .Select(x => new VisitorLogRowVm
                    {
                        TimestampUtc = x.Timestamp,
                        VisitorId = x.VisitorId,
                        VisitorName = x.VisitorName,
                        Purpose = x.Purpose,
                        Source = x.Source,
                        OfficeName = x.Office.Name
                    })
                    .ToList();

                ViewBag.From = range.FromText;
                ViewBag.To = range.ToText;
                ViewBag.OfficeId = officeId;
                ViewBag.Query = q;
                ViewBag.KnownOnly = (knownOnly ?? false);
                ViewBag.OfficeOptions = AdminQueryHelper.BuildOfficeOptions(db, officeId);

                return View(rows);
            }
        }

        [HttpGet]
        public ActionResult ExportLogsCsv(string from, string to, int? officeId, string q, bool? knownOnly)
        {
            using (var db = new FaceAttendDBEntities())
            {
                var range = AdminQueryHelper.ParseRange((from ?? "").Trim(), (to ?? "").Trim());

                var logs = db.VisitorLogs.AsNoTracking().AsQueryable();

                if (range.FromUtc.HasValue) logs = logs.Where(x => x.Timestamp >= range.FromUtc.Value);
                if (range.ToUtcExclusive.HasValue) logs = logs.Where(x => x.Timestamp < range.ToUtcExclusive.Value);

                if (officeId.HasValue && officeId.Value > 0)
                    logs = logs.Where(x => x.OfficeId == officeId.Value);

                q = (q ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(q))
                    logs = logs.Where(x => x.VisitorName.Contains(q) || x.Purpose.Contains(q));

                if (knownOnly ?? false)
                    logs = logs.Where(x => x.VisitorId != null);

                var rows = logs
                    .OrderByDescending(x => x.Timestamp)
                    .Take(10000)
                    .Select(x => new
                    {
                        x.Timestamp,
                        x.VisitorId,
                        x.VisitorName,
                        x.Purpose,
                        x.Source,
                        OfficeName = x.Office.Name
                    })
                    .ToList();

                var sb = new StringBuilder();
                sb.AppendLine("TimestampLocal,VisitorName,Known,Office,Purpose,Source");

                foreach (var r in rows)
                {
                    sb.AppendLine(CsvHelper.JoinCsv(new[]
                    {
                        TimeZoneHelper.UtcToLocal(r.Timestamp).ToString("yyyy-MM-dd HH:mm:ss"),
                        CsvHelper.SanitizeAndEscape(r.VisitorName ?? ""),
                        r.VisitorId.HasValue ? "YES" : "NO",
                        CsvHelper.SanitizeAndEscape(r.OfficeName ?? ""),
                        CsvHelper.SanitizeAndEscape(r.Purpose ?? ""),
                        CsvHelper.SanitizeAndEscape(r.Source ?? "")
                    }));
                }

                var bytes = new UTF8Encoding(true).GetBytes(sb.ToString());
                var file = "visitor_logs_" + TimeZoneHelper.NowLocal().ToString("yyyyMMdd_HHmm") + ".csv";
                return File(bytes, "text/csv", file);
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Purge()
        {
            int years = ConfigurationService.GetInt("Visitors:RetentionYears", 2);

            using (var db = new FaceAttendDBEntities())
            {
                var res = VisitorService.PurgeOldLogs(db, years);
                TempData["msg"] = "Purged " + res.Deleted + " visitor logs older than " + years + " year(s).";
                TempData["msgKind"] = "success";
            }

            return RedirectToAction("Logs");
        }

    }
}
