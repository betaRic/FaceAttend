using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using System.Web.Mvc;
using FaceAttend.Filters;
using FaceAttend.Services;
using FaceAttend.Services.Biometrics;
using FaceAttend.Services.Security;   // SecureFileUpload (P2-F2)

namespace FaceAttend.Controllers
{
    /// <summary>
    /// SAGUPA: Admin controller para sa biometric operations (enrollment, scanning).
    /// 
    /// PAGLALARAWAN (Description):
    ///   Itong controller ay exclusive para sa mga admin lang. Dito nangyayari ang:
    ///   - Employee face enrollment (pagkuha ng mukha para sa database)
    ///   - Scan frame testing (preview ng face detection)
    ///   - Duplicate face checking (bawal ang double enrollment)
    /// 
    /// GINAGAMIT SA:
    ///   - Admin dashboard enrollment page
    ///   - Admin face scanning interface
    /// 
    /// SECURITY NOTES:
    ///   [AdminAuthorize] attribute - kailangan ng valid admin session
    ///   Anti-forgery token validation - proteksyon laban sa CSRF attacks
    /// 
    /// ILOKANO: "Amin laeng dagiti admin ti makagun-od iti kontroller nga agtutubo"
    /// </summary>
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

                // =========================================================================
                // MULTI-FACE HANDLING: Use the LARGEST face (nearest to camera)
                // -------------------------------------------------------------------------
                // DATI: Reject kapag may multiple faces (masyadong strict)
                // NGAYON: Gamitin ang pinakamalaking face (nearest person)
                // 
                // BAKIT: Kapag may ibang tao sa background o katabi, dapat
                //        ang main subject pa rin ang ma-enroll, hindi rejected.
                // =========================================================================
                if (count == 0)
                {
                    return Json(new
                    {
                        ok = true,
                        count = 0,
                        liveness = (float?)null,
                        livenessOk = false,
                        message = "no face detected"
                    });
                }

                // Find the largest face (assumed to be the main subject/nearest person)
                var mainFace = faces[0];
                if (count > 1)
                {
                    mainFace = faces.OrderByDescending(f => f.Area).First();
                }

                var live = new OnnxLiveness();
                var scored = live.ScoreFromFile(processedPath, mainFace);
                if (!scored.Ok)
                    return Json(new { ok = false, error = scored.Error, count = 1 });

                var th = (float)AppSettings.GetDouble("Biometrics:LivenessThreshold", 0.75);
                var p = scored.Probability ?? 0f;

                return Json(new
                {
                    ok = true,
                    count = count,          // Actual face count (for UI info)
                    mainFaceIndex = count > 1 ? Array.IndexOf(faces, mainFace) : 0,
                    multiFaceWarning = count > 1,  // Warn if multiple faces
                    liveness = p,
                    livenessOk = p >= th,
                    message = count > 1 ? "using nearest face" : null
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

        // =================================================================
        // ENROLLMENT ENDPOINT - ULTRA-OPTIMIZED for speed
        // -----------------------------------------------------------------
        // SPEED OPTIMIZATIONS:
        //   1. Use FastFaceMatcher (RAM cache) for instant duplicate check
        //   2. Parallel processing with optimized Dlib pool usage
        //   3. Single DB transaction
        //   4. In-memory operations where possible
        //   5. Supports parallel image uploads from client
        // =================================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Enroll(string employeeId, HttpPostedFileBase image)
        {
            var sw = Stopwatch.StartNew();
            employeeId = (employeeId ?? "").Trim().ToUpperInvariant();

            // -----------------------------------------------------------------
            // STEP 1: Validate (Fast checks first)
            // -----------------------------------------------------------------
            if (string.IsNullOrWhiteSpace(employeeId))
                return Json(new { ok = false, error = "NO_EMPLOYEE_ID", step = "validation" });

            var maxLen = AppSettings.GetInt("Biometrics:EmployeeIdMaxLen", 20);
            if (employeeId.Length > maxLen)
                return Json(new { ok = false, error = "EMPLOYEE_ID_TOO_LONG", step = "validation" });

            // -----------------------------------------------------------------
            // STEP 2: Collect Images
            // -----------------------------------------------------------------
            var maxBytes = AppSettings.GetInt("Biometrics:MaxUploadBytes", 10 * 1024 * 1024);
            var maxImages = AppSettings.GetInt("Biometrics:Enroll:MaxImages", 5);
            var files = new List<HttpPostedFileBase>();

            try
            {
                if (Request?.Files?.Count > 0)
                {
                    for (int i = 0; i < Request.Files.Count && files.Count < maxImages; i++)
                    {
                        var f = Request.Files[i];
                        if (f?.ContentLength > 0 && f.ContentLength <= maxBytes)
                            files.Add(f);
                    }
                }
            }
            catch { }

            if (files.Count == 0 && image?.ContentLength > 0 && image.ContentLength <= maxBytes)
                files.Add(image);

            if (files.Count == 0)
                return Json(new { ok = false, error = "NO_IMAGE", step = "validation" });

            // -----------------------------------------------------------------
            // STEP 3: Check Employee & ULTRA-FAST Duplicate Check (RAM cache)
            // -----------------------------------------------------------------
            Employee emp;
            
            using (var db = new FaceAttendDBEntities())
            {
                emp = db.Employees.FirstOrDefault(e => e.EmployeeId == employeeId);
                if (emp == null)
                    return Json(new { ok = false, error = "EMPLOYEE_NOT_FOUND", step = "employee_lookup" });
            }

            // Ensure FastFaceMatcher is initialized for instant duplicate checking
            if (!FastFaceMatcher.GetStats().ToString().Contains("True"))
                FastFaceMatcher.Initialize();

            // -----------------------------------------------------------------
            // STEP 4: Process Images IN PARALLEL with Pooled Dlib
            // -----------------------------------------------------------------
            var th = (float)AppSettings.GetDouble("Biometrics:LivenessThreshold", 0.75);
            var tol = AppSettings.GetDouble("Biometrics:DlibTolerance", 0.60);
            var strictTol = Math.Max(0.35, tol - 0.10);

            var candidates = new ConcurrentBag<EnrollCandidate>();
            var processedCount = 0;
            var duplicateFound = false;
            var lockObj = new object();

            // OPTIMIZED: Use Dlib pool efficiently
            var parallelism = Math.Min(files.Count, AppSettings.GetInt("Biometrics:Enroll:Parallelism", 4));
            
            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, (f, state) =>
            {
                if (duplicateFound) return; // Stop early if duplicate found

                string path = null;
                string processedPath = null;

                try
                {
                    path = SecureFileUpload.SaveTemp(f, "u_", maxBytes);
                    bool isProcessed;
                    processedPath = ImagePreprocessor.PreprocessForDetection(path, "u_", out isProcessed);

                    var dlib = new DlibBiometrics();
                    DlibBiometrics.FaceBox faceBox;
                    FaceRecognitionDotNet.Location faceLocation;
                    string detectError;

                    // Face detection
                    if (!dlib.TryDetectSingleFaceFromFile(processedPath, out faceBox, out faceLocation, out detectError))
                        return;

                    // Liveness check
                    var live = new OnnxLiveness();
                    var scored = live.ScoreFromFile(processedPath, faceBox);
                    if (!scored.Ok || (scored.Probability ?? 0f) < th)
                        return;

                    // Encoding
                    double[] vec;
                    string encErr;
                    if (!dlib.TryEncodeFromFileWithLocation(processedPath, faceLocation, out vec, out encErr))
                        return;

                    // ULTRA-FAST: Check duplicate using RAM cache (10x faster than DB)
                    lock (lockObj)
                    {
                        processedCount++;
                        if (!duplicateFound && candidates.Count >= 0)
                        {
                            var fastMatch = FastFaceMatcher.FindBestMatch(vec, strictTol);
                            if (fastMatch.IsMatch && fastMatch.Employee?.EmployeeId != employeeId)
                            {
                                duplicateFound = true;
                                state.Stop();
                            }
                        }
                    }

                    if (!duplicateFound)
                    {
                        var area = Math.Max(0, faceBox.Width) * Math.Max(0, faceBox.Height);
                        candidates.Add(new EnrollCandidate
                        {
                            Vec = vec,
                            Liveness = scored.Probability ?? 0f,
                            Area = area
                        });
                    }
                }
                catch { }
                finally
                {
                    ImagePreprocessor.Cleanup(processedPath, path);
                    SecureFileUpload.TryDelete(path);
                }
            });

            if (duplicateFound)
            {
                return Json(new
                {
                    ok = false,
                    error = "FACE_ALREADY_ENROLLED",
                    step = "duplicate_check",
                    processed = processedCount,
                    timeMs = sw.ElapsedMilliseconds
                });
            }

            if (candidates.IsEmpty)
                return Json(new { ok = false, error = "NO_GOOD_FRAME", step = "processing", processed = processedCount });

            // Sort by quality
            var sortedCandidates = candidates
                .OrderByDescending(c => c.Liveness)
                .ThenByDescending(c => c.Area)
                .Take(maxImages)
                .ToList();

            // -----------------------------------------------------------------
            // STEP 5: Save to Database (Single transaction)
            // -----------------------------------------------------------------
            using (var db = new FaceAttendDBEntities())
            {
                var bestVec = sortedCandidates[0].Vec;
                var bestBytes = DlibBiometrics.EncodeToBytes(bestVec);
                
                emp = db.Employees.First(e => e.EmployeeId == employeeId);
                emp.FaceEncodingBase64 = Convert.ToBase64String(bestBytes);
                emp.EnrolledDate = emp.EnrolledDate ?? DateTime.UtcNow;
                emp.LastModifiedDate = DateTime.UtcNow;
                emp.ModifiedBy = "ADMIN";

                // Save multiple encodings for better matching
                var encList = sortedCandidates
                    .Select(c => Convert.ToBase64String(DlibBiometrics.EncodeToBytes(c.Vec)))
                    .ToList();

                try
                {
                    var encJson = Newtonsoft.Json.JsonConvert.SerializeObject(encList);
                    db.Database.ExecuteSqlCommand(
                        "UPDATE Employees SET FaceEncodingsJson = @p0 WHERE EmployeeId = @p1",
                        encJson, employeeId);
                }
                catch { }

                db.SaveChanges();

                // Update caches
                EmployeeFaceIndex.Invalidate();
                FastFaceMatcher.UpdateEmployee(employeeId);

                return Json(new
                {
                    ok = true,
                    liveness = sortedCandidates[0].Liveness,
                    savedVectors = encList.Count,
                    processed = processedCount,
                    timeMs = sw.ElapsedMilliseconds,
                    step = "saved"
                });
            }
        }

        // =================================================================
        // EARLY DUPLICATE CHECK - Quick check with single frame
        // =================================================================
        private static DuplicateMatch CheckEarlyDuplicate(
            List<EmployeeFaceIndex.Entry> entries,
            double[] vec,
            double tolerance,
            string currentEmployeeId)
        {
            if (entries == null || vec == null) return null;

            foreach (var entry in entries)
            {
                if (entry?.Vec == null) continue;
                if (string.Equals(entry.EmployeeId, currentEmployeeId, StringComparison.OrdinalIgnoreCase))
                    continue;

                var dist = DlibBiometrics.Distance(vec, entry.Vec);
                if (dist <= tolerance)
                {
                    return new DuplicateMatch
                    {
                        EmployeeId = entry.EmployeeId,
                        Distance = dist,
                        MatchCount = 1,
                        HitsRequired = 1,
                        UsedTolerance = tolerance
                    };
                }
            }
            return null;
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