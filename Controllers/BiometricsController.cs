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
using FaceAttend.Services.Security;

namespace FaceAttend.Controllers
{
    /// <summary>
    /// DEPRECATED: This controller is being phased out in favor of unified API controllers.
    /// Use Controllers/Api/ScanController.cs and Controllers/Api/EnrollmentController.cs instead.
    /// 
    /// PHASE 1 IMPLEMENTATION: Diversity-aware enrollment with pose bucketing.
    /// 
    /// SECURITY NOTES:
    ///   [AdminAuthorize] attribute - kailangan ng valid admin session
    ///   Anti-forgery token validation - proteksyon laban sa CSRF attacks
    /// </summary>
    [AdminAuthorize]
    [Obsolete("Use /api/scan and /api/enrollment endpoints instead. This controller will be removed in v3.0.")]
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
                path = FileSecurityService.SaveTemp(image, "u_", max);
                processedPath = ImagePreprocessor.PreprocessForDetection(path, "u_", out isProcessed);

                var dlib = new DlibBiometrics();
                var faces = dlib.DetectFacesFromFile(processedPath);
                var count = faces == null ? 0 : faces.Length;

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

                // Find the largest face
                var mainFace = faces[0];
                if (count > 1)
                    mainFace = faces.OrderByDescending(f => f.Area).First();

                // Compute sharpness
                var sharpness = FaceQualityAnalyzer.CalculateSharpness(processedPath, mainFace);

                var live = new OnnxLiveness();
                var scored = live.ScoreFromFile(processedPath, mainFace);
                if (!scored.Ok)
                    return JsonResponseBuilder.Error(scored.Error);

                var th = (float)ConfigurationService.GetDouble("Biometrics:LivenessThreshold", 0.75);
                var p = scored.Probability ?? 0f;

                return JsonResponseBuilder.Success(new
                {
                    count = count,
                    mainFaceIndex = count > 1 ? Array.IndexOf(faces, mainFace) : 0,
                    multiFaceWarning = count > 1,
                    liveness = p,
                    livenessOk = p >= th,
                    sharpness = sharpness,   // NEW: Phase 1
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
        // ENROLLMENT ENDPOINT - PHASE 1: Diversity-aware with pose bucketing
        // =================================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Enroll(string employeeId)
        {
            var sw = Stopwatch.StartNew();

            // ── Input validation ──────────────────────────────────────────────────
            if (string.IsNullOrWhiteSpace(employeeId))
                return JsonResponseBuilder.Error("NO_EMPLOYEE_ID");

            employeeId = employeeId.Trim().ToUpper();
            if (employeeId.Length > 20)
                return JsonResponseBuilder.Error("EMPLOYEE_ID_TOO_LONG");

            var files = new List<HttpPostedFileBase>();
            for (int i = 0; i < Request.Files.Count; i++)
            {
                var f = Request.Files[i];
                if (f != null && f.ContentLength > 0) files.Add(f);
            }

            if (files.Count == 0)
                return JsonResponseBuilder.Error("NO_IMAGE");

            var maxBytes = ConfigurationService.GetInt("Biometrics:MaxUploadBytes", 10 * 1024 * 1024);
            var maxImages = ConfigurationService.GetInt("Biometrics:Enroll:CaptureTarget", 8);
            var maxStored = ConfigurationService.GetInt("Biometrics:Enroll:MaxStoredVectors", 5);

            files = files.Take(maxImages).ToList();

            // Validate employee exists
            Employee emp;
            using (var db = new FaceAttendDBEntities())
            {
                emp = db.Employees.FirstOrDefault(e => e.EmployeeId == employeeId);
                if (emp == null)
                    return JsonResponseBuilder.Error("EMPLOYEE_NOT_FOUND");
            }

            var strictTol = ConfigurationService.GetDouble(
                "Biometrics:EnrollmentStrictTolerance", 0.45);
            var isMobile = DeviceService.IsMobileDevice(Request);

            var candidates = new ConcurrentBag<EnrollCandidate>();
            int processedCount = 0;
            bool duplicateFound = false;
            string duplicateId = null;
            var lockObj = new object();

            var parallelism = Math.Min(files.Count,
                ConfigurationService.GetInt("Biometrics:Enroll:Parallelism", 4));

            // ── Parallel frame processing ──────────────────────────────────────────
            Parallel.ForEach(files,
                new ParallelOptions { MaxDegreeOfParallelism = parallelism },
                (f, state) =>
            {
                if (duplicateFound) return;

                string path = null, processedPath = null;
                try
                {
                    // File security validation
                    if (!FileSecurityService.IsValidImage(
                            f.InputStream, new[] { ".jpg", ".jpeg", ".png" }))
                        return;

                    f.InputStream.Position = 0;
                    path = FileSecurityService.SaveTemp(f, "enr_", maxBytes);
                    bool isProc;
                    processedPath = ImagePreprocessor.PreprocessForDetection(
                        path, "enr_", out isProc);

                    var dlib = new DlibBiometrics();
                    DlibBiometrics.FaceBox faceBox;
                    FaceRecognitionDotNet.Location faceLocation;
                    string detectErr;

                    if (!dlib.TryDetectSingleFaceFromFile(
                            processedPath, out faceBox, out faceLocation, out detectErr))
                        return;

                    // Sharpness check (fast — ROI crop, 160×160)
                    var sharpness = FaceQualityAnalyzer.CalculateSharpness(processedPath, faceBox);
                    var sharpTh = FaceQualityAnalyzer.GetSharpnessThreshold(isMobile);
                    if (sharpness < sharpTh) return; // reject blurry frame

                    // Parallel: liveness + encoding
                    double[] vec = null;
                    float liveness = 0f;
                    bool liveOk = false;

                    Parallel.Invoke(
                        () => {
                            string encErr;
                            dlib.TryEncodeFromFileWithLocation(
                                processedPath, faceLocation, out vec, out encErr);
                        },
                        () => {
                            var live = new OnnxLiveness();
                            var scored = live.ScoreFromFile(processedPath, faceBox);
                            liveOk = scored.Ok;
                            liveness = scored.Probability ?? 0f;
                        });

                    var liveTh = (float)ConfigurationService.GetDouble(
                        "Biometrics:Enroll:LivenessThreshold", 0.75);

                    if (!liveOk || liveness < liveTh || vec == null) return;

                    // Duplicate check (database, not cache)
                    lock (lockObj)
                    {
                        processedCount++;
                        if (!duplicateFound)
                        {
                            using (var checkDb = new FaceAttendDBEntities())
                            {
                                var dup = DuplicateCheckHelper.FindDuplicate(
                                    checkDb, vec, employeeId, strictTol);

                                if (!string.IsNullOrEmpty(dup))
                                {
                                    duplicateFound = true;
                                    duplicateId = dup;
                                    state.Stop();
                                    return;
                                }
                            }
                        }
                    }

                    if (duplicateFound) return;

                    // Pose estimation
                    int imgW = 640, imgH = 480;
                    try {
                        using (var bmp = new System.Drawing.Bitmap(processedPath))
                        { imgW = bmp.Width; imgH = bmp.Height; }
                    } catch { }

                    var (yaw, pitch) = FaceQualityAnalyzer.EstimatePose(faceBox, imgW, imgH);
                    var bucket = FaceQualityAnalyzer.GetPoseBucket(yaw, pitch);

                    if (bucket == "other") return; // extreme angle — discard

                    int area = Math.Max(0, faceBox.Width) * Math.Max(0, faceBox.Height);

                    candidates.Add(new EnrollCandidate
                    {
                        Vec = vec,
                        Liveness = liveness,
                        Area = area,
                        Sharpness = sharpness,
                        PoseYaw = yaw,
                        PosePitch = pitch,
                        PoseBucket = bucket,
                        QualityScore = FaceQualityAnalyzer.CalculateQualityScore(
                            liveness, sharpness, area, yaw, pitch)
                    });
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.TraceWarning(
                        "[BiometricsController.Enroll] Frame error: " + ex.Message);
                }
                finally
                {
                    ImagePreprocessor.Cleanup(processedPath, path);
                    FileSecurityService.TryDelete(path);
                }
            });

            // ── Duplicate found ────────────────────────────────────────────────────
            if (duplicateFound)
            {
                return JsonResponseBuilder.Error("FACE_ALREADY_ENROLLED",
                    details: new {
                        step = "duplicate_check",
                        matchEmployeeId = duplicateId,
                        processed = processedCount,
                        timeMs = sw.ElapsedMilliseconds
                    });
            }

            if (candidates.IsEmpty)
            {
                return JsonResponseBuilder.Error("NO_GOOD_FRAME",
                    details: new {
                        step = "processing",
                        processed = processedCount,
                        timeMs = sw.ElapsedMilliseconds
                    });
            }

            // ── Diversity-aware selection ─────────────────────────────────────────
            var selected = SelectDiverseFrames(candidates.ToList(), maxStored);

            // ── Save to database ──────────────────────────────────────────────────
            using (var db = new FaceAttendDBEntities())
            {
                emp = db.Employees.First(e => e.EmployeeId == employeeId);

                // Best single vector (primary, for legacy systems)
                var bestBytes = DlibBiometrics.EncodeToBytes(selected[0].Vec);
                emp.FaceEncodingBase64 = BiometricCrypto.ProtectBase64Bytes(bestBytes);

                // All selected vectors as JSON (multi-vector matching)
                var jsonList = selected.Select(c =>
                    BiometricCrypto.ProtectBase64Bytes(DlibBiometrics.EncodeToBytes(c.Vec))
                ).ToList();
                emp.FaceEncodingsJson = BiometricCrypto.ProtectString(
                    Newtonsoft.Json.JsonConvert.SerializeObject(jsonList));

                emp.EnrolledDate = emp.EnrolledDate ?? DateTime.UtcNow;
                emp.Status = "ACTIVE";

                db.SaveChanges();
            }

            // ── Cache invalidation ────────────────────────────────────────────────
            FastFaceMatcher.UpdateEmployee(employeeId);
            EmployeeFaceIndex.Invalidate();

            return JsonResponseBuilder.Success(new
            {
                savedVectors = selected.Count,
                timeMs = sw.ElapsedMilliseconds,
                poseBuckets = selected.Select(c => c.PoseBucket).ToList()
            });
        }

        /// <summary>
        /// Selects up to targetCount candidates with pose diversity priority.
        ///
        /// Phase 1: Pick the highest-quality candidate from each desired pose bucket.
        /// Phase 2: Fill remaining slots with highest composite quality score.
        /// </summary>
        private static List<EnrollCandidate> SelectDiverseFrames(
            List<EnrollCandidate> candidates, int targetCount)
        {
            var desiredBuckets = new[] { "center", "left", "right", "up", "down" };
            var selected = new List<EnrollCandidate>();

            // Phase 1: best from each bucket
            foreach (var bucket in desiredBuckets)
            {
                if (selected.Count >= targetCount) break;

                var best = candidates
                    .Where(c => c.PoseBucket == bucket && !selected.Contains(c))
                    .OrderByDescending(c => c.QualityScore)
                    .FirstOrDefault();

                if (best != null) selected.Add(best);
            }

            // Phase 2: fill with highest quality regardless of bucket
            var remaining = candidates
                .Where(c => !selected.Contains(c))
                .OrderByDescending(c => c.QualityScore)
                .Take(targetCount - selected.Count);

            selected.AddRange(remaining);

            return selected
                .OrderByDescending(c => c.QualityScore)
                .ToList();
        }

        private class DuplicateMatch
        {
            public string EmployeeId { get; set; }
            public double Distance { get; set; }
            public int MatchCount { get; set; }
            public int HitsRequired { get; set; }
            public double UsedTolerance { get; set; }
        }
    }
}
