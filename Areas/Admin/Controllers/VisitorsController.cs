using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Web;
using System.Web.Mvc;
using FaceRecognitionDotNet;
using FaceAttend.Filters;
using FaceAttend.Areas.Admin.Models;
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

                var perFrame = ConfigurationService.GetDouble(db, "Biometrics:LivenessThreshold", 0.75);
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

            var max = ConfigurationService.GetInt("Biometrics:MaxUploadBytes", 10 * 1024 * 1024);
            if (image.ContentLength > max)
                return Json(new { ok = false, error = "TOO_LARGE" });

            string path = null;
            string processedPath = null;

            try
            {
                path = FileSecurityService.SaveTemp(image, "v_", max);

                bool isProcessed;
                processedPath = ImagePreprocessor.PreprocessForDetection(path, "v_", out isProcessed);

                var dlib = new DlibBiometrics();
                // FaceBox ay nested class ng DlibBiometrics — kailangan ng fully-qualified name.
                DlibBiometrics.FaceBox faceBox;
                Location faceLocation;
                string detectErr;
                if (!dlib.TryDetectSingleFaceFromFile(processedPath, out faceBox, out faceLocation, out detectErr))
                    return Json(new { ok = false, error = detectErr ?? "DETECT_FAIL" });

                var live = new OnnxLiveness();
                var scored = live.ScoreFromFile(processedPath, faceBox);
                if (!scored.Ok)
                    return Json(new { ok = false, error = scored.Error });

                var th = (float)ConfigurationService.GetDoubleCached(
                    "Biometrics:LivenessThreshold",
                    ConfigurationService.GetDouble("Biometrics:LivenessThreshold", 0.75));
                var p = scored.Probability ?? 0f;
                if (p < th)
                    return Json(new { ok = false, error = "LIVENESS_FAIL", liveness = p });

                string encErr;
                double[] vec;
                if (!dlib.TryEncodeFromFileWithLocation(processedPath, faceLocation, out vec, out encErr) || vec == null)
                {
                    var debug = ConfigurationService.GetBool("Biometrics:Debug", false);
                    if (debug)
                        return Json(new { ok = false, error = encErr ?? "ENCODING_FAIL", detail = encErr });

                    return Json(new { ok = false, error = encErr ?? "ENCODING_FAIL" });
                }

                var tol = ConfigurationService.GetDouble("Visitors:DlibTolerance",
                    ConfigurationService.GetDouble("Biometrics:DlibTolerance", 0.60));

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
                    v.FaceEncodingBase64 = BiometricCrypto.ProtectBase64Bytes(bytes);
                    db.SaveChanges();

                    VisitorFaceIndex.Invalidate();
                }

                return Json(new { ok = true, liveness = p });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("[Visitors.EnrollFace] Enrollment failed: " + ex);
                var debug = ConfigurationService.GetBool("Biometrics:Debug", false);
                if (debug)
                    return Json(new { ok = false, error = "ENROLL_ERROR", detail = ex.Message });

                return Json(new { ok = false, error = "ENROLL_ERROR" });
            }
            finally
            {
                ImagePreprocessor.Cleanup(processedPath, path);
                FileSecurityService.TryDelete(path);
            }
        }

        private class EnrollCandidate
        {
            public double[] Vec { get; set; }
            public float Liveness { get; set; }
            public int Area { get; set; }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult EnrollWizard(string employeeId, HttpPostedFileBase image)
        {
            // Wizard uses shared enrollment component, so it posts "employeeId".
            // For visitors, this is the Visitor.Id value.
            var idText = (employeeId ?? "").Trim();

            int id;
            if (!int.TryParse(idText, out id) || id <= 0)
                return Json(new { ok = false, error = "NO_VISITOR_ID" });

            var maxBytes = ConfigurationService.GetInt("Biometrics:MaxUploadBytes", 10 * 1024 * 1024);
            var maxImages = ConfigurationService.GetInt("Biometrics:Enroll:MaxImages", 5);

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

            var th = (float)ConfigurationService.GetDoubleCached("Biometrics:LivenessThreshold", 0.75);

            var tol = ConfigurationService.GetDouble("Visitors:DlibTolerance",
                ConfigurationService.GetDouble("Biometrics:DlibTolerance", 0.60));

            var candidates = new List<EnrollCandidate>();

            var dlib = new DlibBiometrics();
            var live = new OnnxLiveness();

            foreach (var f in files)
            {
                string path = null;
                string processedPath = null;

                try
                {
                    path = FileSecurityService.SaveTemp(f, "v_", maxBytes);

                    bool isProcessed;
                    processedPath = ImagePreprocessor.PreprocessForDetection(path, "v_", out isProcessed);

                    // FaceBox ay nested class ng DlibBiometrics — kailangan ng fully-qualified name.
                    DlibBiometrics.FaceBox faceBox;
                    Location faceLocation;
                    string detectErr;
                    if (!dlib.TryDetectSingleFaceFromFile(processedPath, out faceBox, out faceLocation, out detectErr))
                        continue;

                    var scored = live.ScoreFromFile(processedPath, faceBox);
                    if (!scored.Ok) continue;

                    var p = scored.Probability ?? 0f;
                    if (p < th) continue;

                    string encErr;
                    double[] vec;
                    if (!dlib.TryEncodeFromFileWithLocation(processedPath, faceLocation, out vec, out encErr) || vec == null)
                        continue;

                    var area = Math.Max(1, faceBox.Width * faceBox.Height);

                    candidates.Add(new EnrollCandidate
                    {
                        Vec = vec,
                        Liveness = p,
                        Area = area
                    });
                }
                catch
                {
                    // Ignore a single bad frame.
                }
                finally
                {
                    ImagePreprocessor.Cleanup(processedPath, path);
                    FileSecurityService.TryDelete(path);
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
                v.FaceEncodingBase64 = BiometricCrypto.ProtectBase64Bytes(bytes);
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

        // CSV helpers moved to FaceAttend.Services.Helpers.CsvHelper
    }
}