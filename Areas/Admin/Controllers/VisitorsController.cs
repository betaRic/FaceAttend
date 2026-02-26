using System;
using System.Collections.Generic;
using System.Globalization;
using System.Data.Entity;
using System.Linq;
using System.Text;
using System.Web;
using System.Web.Mvc;
using FaceAttend.Filters;
using FaceAttend.Areas.Admin.Helpers;
using FaceAttend.Services;
using FaceAttend.Services.Biometrics;
using FaceAttend.Services.Security;

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

                var cap = AppSettings.GetInt("Visitors:MaxListRows", 500);
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

                var perFrame = SystemConfigService.GetDouble(db, "Biometrics:LivenessThreshold", 0.75);
                ViewBag.PerFrame = perFrame.ToString("0.00####", CultureInfo.InvariantCulture);

                return View(v);
            }
        }

        [HttpPost]
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
                    return RedirectToAction("Edit", new { id });
                }

                v.Name = name;
                v.IsActive = isActive;
                db.SaveChanges();

                VisitorFaceIndex.Invalidate();
            }

            TempData["msg"] = "Visitor updated.";
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

                v.IsActive = false;
                db.SaveChanges();

                VisitorFaceIndex.Invalidate();
            }

            TempData["msg"] = "Visitor deactivated.";
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

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult ScanFrame(HttpPostedFileBase image)
        {
            var result = ScanFramePipeline.Run(image, "v_");
            return Json(result);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult EnrollFace(int id, HttpPostedFileBase image)
        {
            if (image == null || image.ContentLength <= 0)
                return Json(new { ok = false, error = "NO_IMAGE" });

            var max = AppSettings.GetInt("Biometrics:MaxUploadBytes", 10 * 1024 * 1024);
            if (image.ContentLength > max)
                return Json(new { ok = false, error = "TOO_LARGE" });

            string path = null;
            string processedPath = null;

            try
            {
                path = SecureFileUpload.SaveTemp(image, "v_", max);

                bool isProcessed;
                processedPath = ImagePreprocessor.PreprocessForDetection(path, "v_", out isProcessed);

                var dlib = new DlibBiometrics();
                var faces = dlib.DetectFacesFromFile(processedPath);

                if (faces == null || faces.Length == 0)
                    return Json(new { ok = false, error = "NO_FACE" });
                if (faces.Length > 1)
                    return Json(new { ok = false, error = "MULTI_FACE" });

                var live = new OnnxLiveness();
                var scored = live.ScoreFromFile(processedPath, faces[0]);
                if (!scored.Ok)
                    return Json(new { ok = false, error = scored.Error });

                var th = (float)AppSettings.GetDouble("Biometrics:LivenessThreshold", 0.75);
                var p = scored.Probability ?? 0f;
                if (p < th)
                    return Json(new { ok = false, error = "LIVENESS_FAIL", liveness = p });

                string encErr;
                var vec = dlib.GetSingleFaceEncodingFromFile(processedPath, out encErr);
                if (vec == null)
                    return Json(new { ok = false, error = "ENCODING_FAIL", detail = encErr });

                var tol = AppSettings.GetDouble("Visitors:DlibTolerance",
                    AppSettings.GetDouble("Biometrics:DlibTolerance", 0.60));

                using (var db = new FaceAttendDBEntities())
                {
                    var v = db.Visitors.FirstOrDefault(x => x.Id == id);
                    if (v == null) return Json(new { ok = false, error = "VISITOR_NOT_FOUND" });

                    var entries = VisitorFaceIndex.GetEntries(db);
                    foreach (var e in entries)
                    {
                        if (e.VisitorId == id) continue;
                        var dist = DlibBiometrics.Distance(vec, e.Vec);
                        if (dist <= tol)
                        {
                            return Json(new
                            {
                                ok = false,
                                error = "FACE_ALREADY_ENROLLED",
                                matchVisitorId = e.VisitorId,
                                matchName = e.Name,
                                distance = dist
                            });
                        }
                    }

                    var bytes = DlibBiometrics.EncodeToBytes(vec);
                    v.FaceEncodingBase64 = Convert.ToBase64String(bytes);
                    db.SaveChanges();

                    VisitorFaceIndex.Invalidate();
                }

                return Json(new { ok = true, liveness = p });
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, error = "ENROLL_ERROR", detail = ex.Message });
            }
            finally
            {
                ImagePreprocessor.Cleanup(processedPath, path);
                SecureFileUpload.TryDelete(path);
            }
        }


        private class EnrollCandidate
        {
            public double[] Vec   { get; set; }
            public float    Liveness { get; set; }
            public int      Area  { get; set; }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult EnrollWizard(string employeeId, HttpPostedFileBase image)
        {
            // Wizard reuses admin-enroll.js, so it posts "employeeId".
            // For visitors, this is the Visitor.Id value.
            var idText = (employeeId ?? "").Trim();

            int id;
            if (!int.TryParse(idText, out id) || id <= 0)
                return Json(new { ok = false, error = "NO_VISITOR_ID" });

            var maxBytes  = AppSettings.GetInt("Biometrics:MaxUploadBytes", 10 * 1024 * 1024);
            var maxImages = AppSettings.GetInt("Biometrics:Enroll:MaxImages", 5);

            var files = new List<HttpPostedFileBase>();
            try
            {
                if (Request != null && Request.Files != null && Request.Files.Count > 0)
                {
                    for (int i = 0; i < Request.Files.Count; i++)
                    {
                        var f = Request.Files[i];
                        if (f == null || f.ContentLength <= 0) continue;

                        files.Add(f);
                        if (files.Count >= maxImages) break;
                    }
                }
            }
            catch
            {
                // Ignore and fall back to the single parameter.
            }

            if (files.Count == 0 && image != null && image.ContentLength > 0)
                files.Add(image);

            if (files.Count == 0)
                return Json(new { ok = false, error = "NO_IMAGE" });

            foreach (var f in files)
            {
                if (f.ContentLength > maxBytes)
                    return Json(new { ok = false, error = "TOO_LARGE" });
            }

            var th = (float)SystemConfigService.GetDoubleCached("Biometrics:LivenessThreshold", 0.75);

            var tol = AppSettings.GetDouble("Visitors:DlibTolerance",
                AppSettings.GetDouble("Biometrics:DlibTolerance", 0.60));

            var candidates = new List<EnrollCandidate>();

            var dlib = new DlibBiometrics();
            var live = new OnnxLiveness();

            foreach (var f in files)
            {
                string path = null;
                string processedPath = null;

                try
                {
                    path = SecureFileUpload.SaveTemp(f, "v_", maxBytes);

                    bool isProcessed;
                    processedPath = ImagePreprocessor.PreprocessForDetection(path, "v_", out isProcessed);

                    var faces = dlib.DetectFacesFromFile(processedPath);

                    if (faces == null || faces.Length == 0) continue;
                    if (faces.Length > 1) continue;

                    var scored = live.ScoreFromFile(processedPath, faces[0]);
                    if (!scored.Ok) continue;

                    var p = scored.Probability ?? 0f;
                    if (p < th) continue;

                    string encErr;
                    var vec = dlib.GetSingleFaceEncodingFromFile(processedPath, out encErr);
                    if (vec == null) continue;

                    var area = Math.Max(1, faces[0].Width * faces[0].Height);

                    candidates.Add(new EnrollCandidate
                    {
                        Vec      = vec,
                        Liveness = p,
                        Area     = area
                    });
                }
                catch
                {
                    // Ignore a single bad frame.
                }
                finally
                {
                    ImagePreprocessor.Cleanup(processedPath, path);
                    SecureFileUpload.TryDelete(path);
                }
            }

            if (candidates.Count == 0)
                return Json(new { ok = false, error = "NO_VALID_FACE" });

            // Pick best (highest liveness, then largest face box).
            var best = candidates
                .OrderByDescending(x => x.Liveness)
                .ThenByDescending(x => x.Area)
                .First();

            using (var db = new FaceAttendDBEntities())
            {
                var v = db.Visitors.FirstOrDefault(x => x.Id == id);
                if (v == null) return Json(new { ok = false, error = "VISITOR_NOT_FOUND" });

                var entries = VisitorFaceIndex.GetEntries(db);

                // Duplicate check against other visitors using all candidates.
                foreach (var c in candidates)
                {
                    foreach (var e in entries)
                    {
                        if (e.VisitorId == id) continue;

                        var dist = DlibBiometrics.Distance(c.Vec, e.Vec);
                        if (dist <= tol)
                        {
                            return Json(new
                            {
                                ok = false,
                                error = "FACE_ALREADY_ENROLLED",
                                matchVisitorId = e.VisitorId,
                                matchName = e.Name,
                                distance = dist
                            });
                        }
                    }
                }

                var bytes = DlibBiometrics.EncodeToBytes(best.Vec);
                v.FaceEncodingBase64 = Convert.ToBase64String(bytes);
                db.SaveChanges();

                VisitorFaceIndex.Invalidate();
            }

            return Json(new { ok = true, liveness = best.Liveness });
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
                    sb.AppendLine(string.Join(",", new[]
                    {
                        EscapeCsv(r.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")),
                        EscapeCsv(SafeCell(r.VisitorName ?? "")),
                        EscapeCsv(r.VisitorId.HasValue ? "YES" : "NO"),
                        EscapeCsv(SafeCell(r.OfficeName ?? "")),
                        EscapeCsv(SafeCell(r.Purpose ?? "")),
                        EscapeCsv(SafeCell(r.Source ?? ""))
                    }));
                }

                var bytes = Encoding.UTF8.GetBytes(sb.ToString());
                var file = "visitor_logs_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".csv";
                return File(bytes, "text/csv", file);
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Purge()
        {
            int years = AppSettings.GetInt("Visitors:RetentionYears", 2);

            using (var db = new FaceAttendDBEntities())
            {
                var res = VisitorService.PurgeOldLogs(db, years);
                TempData["msg"] = "Purged " + res.Deleted + " visitor logs older than " + years + " year(s).";
            }

            return RedirectToAction("Logs");
        }

        // Helpers

        private static string SafeCell(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var t = s.Trim();
            if (t.StartsWith("=") || t.StartsWith("+") || t.StartsWith("-") || t.StartsWith("@"))
                return "'" + t;
            return t;
        }

        private static string EscapeCsv(string s)
        {
            if (s == null) s = "";
            var needs = s.Contains(",") || s.Contains("\"") || s.Contains("\n") || s.Contains("\r");
            s = s.Replace("\"", "\"\"");
            return needs ? "\"" + s + "\"" : s;
        }
    }
}
