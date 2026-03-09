using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web;
using System.Web.Mvc;
using FaceRecognitionDotNet;
using Newtonsoft.Json;
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

            // FIX M-08: guard against accidental face overwrite.
            using (var dbCheck = new FaceAttendDBEntities())
            {
                var existing = dbCheck.Employees
                    .AsNoTracking()
                    .FirstOrDefault(e => e.EmployeeId == employeeId && e.IsActive);

                if (existing != null && !string.IsNullOrWhiteSpace(existing.FaceEncodingBase64))
                {
                    var force = (Request?.Form?["force"] ?? "")
                        .Trim()
                        .ToLowerInvariant();

                    if (force != "true" && force != "1")
                    {
                        return Json(new
                        {
                            ok = false,
                            error = "ALREADY_ENROLLED",
                            employeeId,
                            employeeName = existing.FullName,
                            message = $"Si {existing.FullName} ay may existing face enrollment na. I-confirm ang re-enrollment."
                        });
                    }

                    System.Diagnostics.Trace.TraceWarning(
                        $"[Enroll] FORCE RE-ENROLL: {employeeId} ({existing.FullName}) from {Request?.UserHostAddress ?? "unknown"}");
                }
            }
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

            // Unahin ang SystemConfig para ang admin runtime settings ang masunod.
            var th = (float)SystemConfigService.GetDoubleCached(
                "Biometrics:LivenessThreshold",
                AppSettings.GetDouble("Biometrics:LivenessThreshold", 0.75));

            var tol = SystemConfigService.GetDoubleCached(
                "Biometrics:DlibTolerance",
                AppSettings.GetDouble("Biometrics:DlibTolerance", 0.60));

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

                    var area = Math.Max(0, faceBox.Width) * Math.Max(0, faceBox.Height);
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
                emp.FaceEncodingBase64 = BiometricCrypto.ProtectBase64Bytes(bestBytes);
                emp.EnrolledDate = emp.EnrolledDate ?? DateTime.UtcNow;
                emp.LastModifiedDate = DateTime.UtcNow;
                emp.ModifiedBy = AuditHelper.GetActorIp(Request);

                var encList = candidates
                    .Select(c => BiometricCrypto.ProtectBase64Bytes(DlibBiometrics.EncodeToBytes(c.Vec)))
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();

                var encJson = BiometricCrypto.ProtectString(
                    Newtonsoft.Json.JsonConvert.SerializeObject(encList));

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
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("[BiometricsController] FaceEncodingsJson update skipped: " + ex.Message);
                    }

                    tx.Commit();
                }

                AuditHelper.Log(
                    db,
                    Request,
                    AuditHelper.ActionFaceEnroll,
                    "Employee",
                    employeeId,
                    "Nag-enroll ng face vectors para sa employee.",
                    null,
                    new
                    {
                        savedVectors = encList.Count,
                        primaryVector = !string.IsNullOrWhiteSpace(emp.FaceEncodingBase64),
                        hasJson = !string.IsNullOrWhiteSpace(encJson)
                    });

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
    }
}