using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web;
using System.Web.Mvc;
using FaceAttend.Filters;
using FaceAttend.Services;
using FaceAttend.Services.Biometrics;
using FaceAttend.Services.Security;   // SecureFileUpload (P2-F2)

namespace FaceAttend.Controllers
{
    // All biometrics endpoints require admin authentication because they read/write
    // face encodings.
    [AdminAuthorize]
    public class BiometricsController : Controller
    {
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult ScanFrame(HttpPostedFileBase image)
        {
            var result = ScanFramePipeline.Run(image, "u_");
            return Json(result);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Enroll(string employeeId, HttpPostedFileBase image)
        {
            employeeId = (employeeId ?? "").Trim().ToUpperInvariant();

            if (string.IsNullOrWhiteSpace(employeeId))
                return Json(new { ok = false, error = "NO_EMPLOYEE_ID" });

            // P1-F5: Validate employeeId against configured pattern and length limit.
            var maxLen = AppSettings.GetInt("Biometrics:EmployeeIdMaxLen", 20);
            var pattern = AppSettings.GetString(
                "Biometrics:EmployeeIdPattern", @"^[A-Z0-9_-]{1,20}$");

            if (employeeId.Length > maxLen)
                return Json(new { ok = false, error = "EMPLOYEE_ID_TOO_LONG" });

            if (!string.IsNullOrWhiteSpace(pattern) &&
                !Regex.IsMatch(employeeId, pattern, RegexOptions.CultureInvariant))
                return Json(new { ok = false, error = "EMPLOYEE_ID_INVALID_FORMAT" });

            // Day 3: multi-encoding enrollment.
            // Allow multiple uploaded images under the same field name ("image").
            // Backward compatible: if only one image is posted, it still works.
            var maxBytes = AppSettings.GetInt("Biometrics:MaxUploadBytes", 10 * 1024 * 1024);
            var maxImages = AppSettings.GetInt("Biometrics:Enroll:MaxImages", 5);

            var files = new System.Collections.Generic.List<HttpPostedFileBase>();
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

            if (files.Count == 0)
            {
                if (image == null || image.ContentLength <= 0)
                    return Json(new { ok = false, error = "NO_IMAGE" });

                files.Add(image);
            }

            foreach (var f in files)
            {
                if (f.ContentLength > maxBytes)
                    return Json(new { ok = false, error = "TOO_LARGE" });
            }

            var th = (float)AppSettings.GetDouble("Biometrics:LivenessThreshold", 0.75);
            var tol = AppSettings.GetDouble("Biometrics:DlibTolerance", 0.60);

            var dlib = new DlibBiometrics();
            var live = new OnnxLiveness();

            var candidates = new System.Collections.Generic.List<EnrollCandidate>();

            foreach (var f in files)
            {
                if (candidates.Count >= maxImages) break;

                string path = null;
                string processedPath = null;
                bool isProcessed = false;

                try
                {
                    path = SecureFileUpload.SaveTemp(f, "u_", maxBytes);
                    processedPath = ImagePreprocessor.PreprocessForDetection(path, "u_", out isProcessed);

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

                    var area = Math.Max(0, faces[0].Width) * Math.Max(0, faces[0].Height);
                    candidates.Add(new EnrollCandidate { Vec = vec, Liveness = p, Area = area });
                }
                catch
                {
                    // Skip bad frames.
                }
                finally
                {
                    ImagePreprocessor.Cleanup(processedPath, path);
                    SecureFileUpload.TryDelete(path);
                }
            }

            if (candidates.Count == 0)
                return Json(new { ok = false, error = "NO_GOOD_FRAME" });

            candidates = candidates
                .OrderByDescending(c => c.Liveness)
                .ThenByDescending(c => c.Area)
                .Take(maxImages)
                .ToList();

            using (var db = new FaceAttendDBEntities())
            {
                var emp = db.Employees.FirstOrDefault(e => e.EmployeeId == employeeId);
                if (emp == null)
                    return Json(new { ok = false, error = "EMPLOYEE_NOT_FOUND" });

                var entries = EmployeeFaceIndex.GetEntries(db);
                foreach (var cand in candidates)
                {
                    foreach (var e in entries)
                    {
                        if (string.Equals(e.EmployeeId, employeeId, StringComparison.OrdinalIgnoreCase))
                            continue;

                        var dist = DlibBiometrics.Distance(cand.Vec, e.Vec);
                        if (dist <= tol)
                        {
                            return Json(new
                            {
                                ok = false,
                                error = "FACE_ALREADY_ENROLLED",
                                matchEmployeeId = e.EmployeeId,
                                distance = dist
                            });
                        }
                    }
                }

                var bestVec = candidates[0].Vec;
                var bestBytes = DlibBiometrics.EncodeToBytes(bestVec);
                emp.FaceEncodingBase64 = Convert.ToBase64String(bestBytes);
                emp.EnrolledDate = emp.EnrolledDate ?? DateTime.UtcNow;
                emp.LastModifiedDate = DateTime.UtcNow;
                emp.ModifiedBy = "ADMIN";

                var encList = candidates
                    .Select(c => Convert.ToBase64String(DlibBiometrics.EncodeToBytes(c.Vec)))
                    .ToList();

                var encJson = Newtonsoft.Json.JsonConvert.SerializeObject(encList);

                using (var tx = db.Database.BeginTransaction())
                {
                    db.SaveChanges();

                    try
                    {
                        db.Database.ExecuteSqlCommand(
                            "UPDATE Employees SET FaceEncodingsJson = @p0 WHERE EmployeeId = @p1",
                            encJson,
                            employeeId);
                    }
                    catch
                    {
                        // Column may not exist yet.
                    }

                    tx.Commit();
                }

                EmployeeFaceIndex.Invalidate();

                return Json(new { ok = true, liveness = candidates[0].Liveness, savedVectors = encList.Count });
            }
        }

        private class EnrollCandidate
        {
            public double[] Vec { get; set; }
            public float Liveness { get; set; }
            public int Area { get; set; }
        }


        // All file handling goes through SecureFileUpload (P2-F2).
        // ImagePreprocessor handles resize cleanup (P3-F2).
        // No private SaveTemp() or TryDelete() — removed in P2-F2.
    }
}
