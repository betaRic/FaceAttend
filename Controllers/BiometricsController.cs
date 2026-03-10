using System;
using System.Collections.Generic;
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
            if (image == null || image.ContentLength <= 0)
                return Json(new { ok = false, error = "NO_IMAGE" });

            var max = AppSettings.GetInt("Biometrics:MaxUploadBytes", 10 * 1024 * 1024);
            if (image.ContentLength > max)
                return Json(new { ok = false, error = "TOO_LARGE" });

            string path = null;
            string processedPath = null;
            bool isProcessed = false;

            try
            {
                // Dito muna dadaan lahat ng upload para iwas kalat / unsafe temp files.
                path = SecureFileUpload.SaveTemp(image, "u_", max);

                // Resize lang if needed para hindi mabigat ang detection.
                processedPath = ImagePreprocessor.PreprocessForDetection(path, "u_", out isProcessed);

                var dlib = new DlibBiometrics();
                var faces = dlib.DetectFacesFromFile(processedPath);
                var count = faces == null ? 0 : faces.Length;

                if (count != 1)
                    return Json(new
                    {
                        ok = true,
                        count,
                        liveness = (float?)null,
                        livenessOk = false
                    });

                var live = new OnnxLiveness();
                var scored = live.ScoreFromFile(processedPath, faces[0]);
                if (!scored.Ok)
                    return Json(new { ok = false, error = scored.Error, count = 1 });

                var th = (float)AppSettings.GetDouble("Biometrics:LivenessThreshold", 0.75);
                var p = scored.Probability ?? 0f;

                return Json(new
                {
                    ok = true,
                    count = 1,
                    liveness = p,
                    livenessOk = p >= th
                });
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, error = "SCAN_ERROR: " + ex.Message });
            }
            finally
            {
                ImagePreprocessor.Cleanup(processedPath, path);
                SecureFileUpload.TryDelete(path);
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Enroll(string employeeId, HttpPostedFileBase image)
        {
            employeeId = (employeeId ?? "").Trim().ToUpperInvariant();

            if (string.IsNullOrWhiteSpace(employeeId))
                return Json(new { ok = false, error = "NO_EMPLOYEE_ID" });

            // Basic validation sa employee id.
            var maxLen = AppSettings.GetInt("Biometrics:EmployeeIdMaxLen", 20);
            var pattern = AppSettings.GetString(
                "Biometrics:EmployeeIdPattern", @"^[A-Z0-9_-]{1,20}$");

            if (employeeId.Length > maxLen)
                return Json(new { ok = false, error = "EMPLOYEE_ID_TOO_LONG" });

            if (!string.IsNullOrWhiteSpace(pattern) &&
                !Regex.IsMatch(employeeId, pattern, RegexOptions.CultureInvariant))
                return Json(new { ok = false, error = "EMPLOYEE_ID_INVALID_FORMAT" });

            var maxBytes = AppSettings.GetInt("Biometrics:MaxUploadBytes", 10 * 1024 * 1024);
            var maxImages = AppSettings.GetInt("Biometrics:Enroll:MaxImages", 5);

            var files = new List<HttpPostedFileBase>();

            try
            {
                if (Request != null && Request.Files != null && Request.Files.Count > 0)
                {
                    for (int i = 0; i < Request.Files.Count; i++)
                    {
                        var f = Request.Files[i];
                        if (f == null || f.ContentLength <= 0)
                            continue;

                        files.Add(f);

                        if (files.Count >= maxImages)
                            break;
                    }
                }
            }
            catch
            {
                // Fallback sa single image parameter.
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

            // Para sa single-frame fallback, mas higpitan natin ang tolerance
            // para hindi isang noisy frame lang ang magba-block agad.
            var strictTol = AppSettings.GetDouble(
                "Biometrics:Enroll:StrictDuplicateTolerance",
                Math.Max(0.35, tol - 0.10));

            if (strictTol > tol)
                strictTol = tol;

            if (strictTol <= 0)
                strictTol = tol;

            var duplicateHitsRequired = AppSettings.GetInt("Biometrics:Enroll:DuplicateHitsRequired", 2);
            if (duplicateHitsRequired < 1)
                duplicateHitsRequired = 1;

            if (duplicateHitsRequired > maxImages)
                duplicateHitsRequired = maxImages;

            var dlib = new DlibBiometrics();
            var live = new OnnxLiveness();

            var candidates = new List<EnrollCandidate>();

            foreach (var f in files)
            {
                if (candidates.Count >= maxImages)
                    break;

                string path = null;
                string processedPath = null;
                bool isProcessed = false;

                try
                {
                    path = SecureFileUpload.SaveTemp(f, "u_", maxBytes);
                    processedPath = ImagePreprocessor.PreprocessForDetection(path, "u_", out isProcessed);

                    DlibBiometrics.FaceBox faceBox;
                    FaceRecognitionDotNet.Location faceLocation;
                    string detectError;

                    // Important: detect once lang, then reuse location sa encoding.
                    if (!dlib.TryDetectSingleFaceFromFile(
                        processedPath,
                        out faceBox,
                        out faceLocation,
                        out detectError))
                    {
                        continue;
                    }

                    var scored = live.ScoreFromFile(processedPath, faceBox);
                    if (!scored.Ok)
                        continue;

                    var p = scored.Probability ?? 0f;
                    if (p < th)
                        continue;

                    double[] vec;
                    string encErr;
                    if (!dlib.TryEncodeFromFileWithLocation(
                        processedPath,
                        faceLocation,
                        out vec,
                        out encErr))
                    {
                        continue;
                    }

                    var area = Math.Max(0, faceBox.Width) * Math.Max(0, faceBox.Height);
                    candidates.Add(new EnrollCandidate
                    {
                        Vec = vec,
                        Liveness = p,
                        Area = area
                    });
                }
                catch
                {
                    // Skip bad frame.
                }
                finally
                {
                    ImagePreprocessor.Cleanup(processedPath, path);
                    SecureFileUpload.TryDelete(path);
                }
            }

            if (candidates.Count == 0)
                return Json(new { ok = false, error = "NO_GOOD_FRAME" });

            // Highest liveness first, then bigger face area.
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

                // Bagong duplicate rule:
                // - kung single candidate lang, strict tolerance lang ang gamit.
                // - kung multi-frame, kailangan consistent hit sa same employee.
                var duplicate = FindDuplicateMatch(
                    entries,
                    candidates,
                    employeeId,
                    tol,
                    strictTol,
                    duplicateHitsRequired);

                if (duplicate != null)
                {
                    return Json(new
                    {
                        ok = false,
                        error = "FACE_ALREADY_ENROLLED",
                        matchEmployeeId = duplicate.EmployeeId,
                        distance = duplicate.Distance,
                        matchCount = duplicate.MatchCount,
                        hitsRequired = duplicate.HitsRequired,
                        usedTolerance = duplicate.UsedTolerance
                    });
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
                        // Column may not exist yet. Legacy-safe lang.
                    }

                    tx.Commit();
                }

                EmployeeFaceIndex.Invalidate();

                return Json(new
                {
                    ok = true,
                    liveness = candidates[0].Liveness,
                    savedVectors = encList.Count
                });
            }
        }

        private static DuplicateMatch FindDuplicateMatch(
            IEnumerable<EmployeeFaceIndex.Entry> entries,
            IList<EnrollCandidate> candidates,
            string employeeId,
            double tolerance,
            double strictTolerance,
            int hitsRequired)
        {
            if (entries == null || candidates == null || candidates.Count == 0)
                return null;

            if (hitsRequired < 1)
                hitsRequired = 1;

            // Single-frame fallback: stricter check para less false positive.
            if (candidates.Count == 1)
            {
                DuplicateMatch best = null;

                foreach (var entry in entries)
                {
                    if (entry == null || entry.Vec == null)
                        continue;

                    if (string.Equals(entry.EmployeeId, employeeId, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var dist = DlibBiometrics.Distance(candidates[0].Vec, entry.Vec);
                    if (dist > strictTolerance)
                        continue;

                    if (best == null || dist < best.Distance)
                    {
                        best = new DuplicateMatch
                        {
                            EmployeeId = entry.EmployeeId,
                            Distance = dist,
                            MatchCount = 1,
                            HitsRequired = 1,
                            UsedTolerance = strictTolerance
                        };
                    }
                }

                return best;
            }

            var hitCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var bestDistances = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            foreach (var cand in candidates)
            {
                foreach (var entry in entries)
                {
                    if (entry == null || entry.Vec == null)
                        continue;

                    if (string.Equals(entry.EmployeeId, employeeId, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var dist = DlibBiometrics.Distance(cand.Vec, entry.Vec);
                    if (dist > tolerance)
                        continue;

                    int currentHits;
                    if (!hitCounts.TryGetValue(entry.EmployeeId, out currentHits))
                        currentHits = 0;

                    hitCounts[entry.EmployeeId] = currentHits + 1;

                    double currentBest;
                    if (!bestDistances.TryGetValue(entry.EmployeeId, out currentBest) || dist < currentBest)
                        bestDistances[entry.EmployeeId] = dist;
                }
            }

            DuplicateMatch bestMatch = null;

            foreach (var kv in hitCounts)
            {
                if (kv.Value < hitsRequired)
                    continue;

                double bestDist;
                if (!bestDistances.TryGetValue(kv.Key, out bestDist))
                    bestDist = double.PositiveInfinity;

                if (bestMatch == null ||
                    kv.Value > bestMatch.MatchCount ||
                    (kv.Value == bestMatch.MatchCount && bestDist < bestMatch.Distance))
                {
                    bestMatch = new DuplicateMatch
                    {
                        EmployeeId = kv.Key,
                        Distance = bestDist,
                        MatchCount = kv.Value,
                        HitsRequired = hitsRequired,
                        UsedTolerance = tolerance
                    };
                }
            }

            return bestMatch;
        }

        private class EnrollCandidate
        {
            public double[] Vec { get; set; }
            public float Liveness { get; set; }
            public int Area { get; set; }
        }

        private class DuplicateMatch
        {
            public string EmployeeId { get; set; }
            public double Distance { get; set; }
            public int MatchCount { get; set; }
            public int HitsRequired { get; set; }
            public double UsedTolerance { get; set; }
        }

        // All file handling goes through SecureFileUpload (P2-F2).
        // ImagePreprocessor handles resize cleanup (P3-F2).
    }
}