using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Web;
using System.Web.Mvc;
using FaceAttend.Filters;
using FaceAttend.Services;
using FaceAttend.Services.Biometrics;
using FaceAttend.Services.Security;   // FileSecurityService (merged)

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
                return JsonResponseBuilder.Error("NO_IMAGE");

            var max = ConfigurationService.GetInt("Biometrics:MaxUploadBytes", 10 * 1024 * 1024);
            if (image.ContentLength > max)
                return JsonResponseBuilder.Error("TOO_LARGE");

            // SECURITY: Validate file content is actually an image
            if (!FileSecurityService.IsValidImage(image.InputStream, new[] { ".jpg", ".jpeg", ".png" }))
                return JsonResponseBuilder.Error("INVALID_IMAGE_FORMAT");

            string path = null;
            string processedPath = null;
            bool isProcessed = false;

            try
            {
                // Dito muna dadaan lahat ng upload para iwas kalat / unsafe temp files.
                path = FileSecurityService.SaveTemp(image, "u_", max);

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
                    return JsonResponseBuilder.Success(new
                    {
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
                    return JsonResponseBuilder.Error(scored.Error);

                var th = (float)ConfigurationService.GetDouble("Biometrics:LivenessThreshold", 0.75);
                var p = scored.Probability ?? 0f;

                return JsonResponseBuilder.Success(new
                {
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
                return JsonResponseBuilder.Error("SCAN_ERROR", ex.Message);
            }
            finally
            {
                ImagePreprocessor.Cleanup(processedPath, path);
                FileSecurityService.TryDelete(path);
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
                return JsonResponseBuilder.Error("NO_EMPLOYEE_ID", details: new { step = "validation" });

            var maxLen = ConfigurationService.GetInt("Biometrics:EmployeeIdMaxLen", 20);
            if (employeeId.Length > maxLen)
                return JsonResponseBuilder.Error("EMPLOYEE_ID_TOO_LONG", details: new { step = "validation" });

            // -----------------------------------------------------------------
            // STEP 2: Collect Images
            // -----------------------------------------------------------------
            var maxBytes = ConfigurationService.GetInt("Biometrics:MaxUploadBytes", 10 * 1024 * 1024);
            var maxImages = ConfigurationService.GetInt("Biometrics:Enroll:MaxImages", 5);
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
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning("[Biometrics] File enumeration failed: " + ex.Message);
            }

            if (files.Count == 0 && image?.ContentLength > 0 && image.ContentLength <= maxBytes)
                files.Add(image);

            if (files.Count == 0)
                return JsonResponseBuilder.Error("NO_IMAGE", details: new { step = "validation" });

            // -----------------------------------------------------------------
            // STEP 3: Check Employee & ULTRA-FAST Duplicate Check (RAM cache)
            // -----------------------------------------------------------------
            Employee emp;
            
            using (var db = new FaceAttendDBEntities())
            {
                emp = db.Employees.FirstOrDefault(e => e.EmployeeId == employeeId);
                if (emp == null)
                    return JsonResponseBuilder.Error("EMPLOYEE_NOT_FOUND", details: new { step = "employee_lookup" });
            }

            // Ensure FastFaceMatcher is initialized for instant duplicate checking
            if (!FastFaceMatcher.IsInitialized)
                FastFaceMatcher.Initialize();

            // -----------------------------------------------------------------
            // STEP 4: Process Images IN PARALLEL with Pooled Dlib
            // -----------------------------------------------------------------
            var th = (float)ConfigurationService.GetDouble("Biometrics:LivenessThreshold", 0.75);
            var tol = ConfigurationService.GetDouble("Biometrics:DlibTolerance", 0.60);
            var strictTol = Math.Max(0.35, tol - 0.10);

            var candidates = new ConcurrentBag<EnrollCandidate>();
            var processedCount = 0;
            var duplicateFound = false;
            var lockObj = new object();

            // OPTIMIZED: Use Dlib pool efficiently
            var parallelism = Math.Min(files.Count, ConfigurationService.GetInt("Biometrics:Enroll:Parallelism", 4));
            
            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, (f, state) =>
            {
                if (duplicateFound) return; // Stop early if duplicate found

                string path = null;
                string processedPath = null;

                try
                {
                    path = FileSecurityService.SaveTemp(f, "u_", maxBytes);
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

                    // CRITICAL FIX: Check duplicate directly from database to avoid cache staleness
                    // This prevents Employee B being incorrectly flagged as Employee A
                    lock (lockObj)
                    {
                        processedCount++;
                        if (!duplicateFound)
                        {
                            // Use database query instead of cache for enrollment duplicate check
                            using (var checkDb = new FaceAttendDBEntities())
                            {
                                var dupEmployeeId = FindDuplicateEmployeeInDatabase(checkDb, vec, employeeId, strictTol);
                                if (!string.IsNullOrEmpty(dupEmployeeId))
                                {
                                    duplicateFound = true;
                                    state.Stop();
                                }
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
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.TraceWarning("[Biometrics] Parallel enrollment processing error: " + ex.Message);
                }
                finally
                {
                    ImagePreprocessor.Cleanup(processedPath, path);
                    FileSecurityService.TryDelete(path);
                }
            });

            if (duplicateFound)
            {
                return JsonResponseBuilder.Error("FACE_ALREADY_ENROLLED", 
                    details: new { step = "duplicate_check", processed = processedCount, timeMs = sw.ElapsedMilliseconds });
            }

            if (candidates.IsEmpty)
                return JsonResponseBuilder.Error("NO_GOOD_FRAME", 
                    details: new { step = "processing", processed = processedCount });

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
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.TraceWarning("[Biometrics] Failed to save additional encodings: " + ex.Message);
                }

                db.SaveChanges();

                // Update caches - FIX: Full reload to ensure consistency
                EmployeeFaceIndex.Invalidate();
                FastFaceMatcher.ReloadFromDatabase();
                if (!FastFaceMatcher.IsInitialized)
                    FastFaceMatcher.Initialize();

                return JsonResponseBuilder.Success(new
                {
                    liveness = sortedCandidates[0].Liveness,
                    savedVectors = encList.Count,
                    processed = processedCount,
                    timeMs = sw.ElapsedMilliseconds,
                    step = "saved"
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

        /// <summary>
        /// CRITICAL FIX: Checks for duplicate faces directly in database.
        /// Bypasses FastFaceMatcher cache to avoid stale data issues.
        /// </summary>
        private string FindDuplicateEmployeeInDatabase(FaceAttendDBEntities db, double[] faceVector, string excludeEmployeeId, double tolerance)
        {
            if (faceVector == null || faceVector.Length != 128)
                return null;

            // Query active employees excluding the current one
            var employees = db.Employees
                .Where(e => e.Status == "ACTIVE" && 
                           e.EmployeeId != excludeEmployeeId &&
                           (e.FaceEncodingBase64 != null || e.FaceEncodingsJson != null))
                .Select(e => new { e.EmployeeId, e.FaceEncodingBase64, e.FaceEncodingsJson })
                .ToList();

            foreach (var emp in employees)
            {
                var existingVectors = FaceEncodingHelper.LoadEmployeeVectors(
                    emp.FaceEncodingBase64,
                    emp.FaceEncodingsJson,
                    5); // max 5 vectors per employee

                foreach (var existingVec in existingVectors)
                {
                    if (existingVec != null && existingVec.Length == 128)
                    {
                        var distance = DlibBiometrics.Distance(faceVector, existingVec);
                        if (distance <= tolerance)
                        {
                            return emp.EmployeeId; // Found duplicate
                        }
                    }
                }
            }

            return null; // No duplicate found
        }

        // All file handling goes through FileSecurityService.
        // ImagePreprocessor handles resize cleanup (P3-F2).
    }
}