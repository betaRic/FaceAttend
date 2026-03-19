using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Web;
using System.Web.Mvc;
using FaceAttend.Services;
using FaceAttend.Services.Biometrics;
using FaceAttend.Services.Security;

namespace FaceAttend.Controllers.Api
{
    /// <summary>
    /// Unified enrollment API
    /// Single endpoint for face enrollment across admin, mobile, and visitor flows
    /// </summary>
    [RoutePrefix("api/enrollment")]
    public class EnrollmentController : Controller
    {
        /// <summary>
        /// Enroll face(s) for an employee
        /// Supports both single and multi-frame enrollment
        /// </summary>
        [HttpPost]
        [Route("enroll")]
        [ValidateAntiForgeryToken]
        public ActionResult Enroll(string employeeId, 
            List<HttpPostedFileBase> images, 
            string allEncodingsJson = null)
        {
            var sw = Stopwatch.StartNew();

            // Validate input
            if (string.IsNullOrWhiteSpace(employeeId))
            {
                return JsonResponseBuilder.Error("NO_EMPLOYEE_ID");
            }

            employeeId = employeeId.Trim().ToUpper();
            if (employeeId.Length > 20)
            {
                return JsonResponseBuilder.Error("EMPLOYEE_ID_TOO_LONG");
            }

            // Collect images from request
            var files = new List<HttpPostedFileBase>();
            if (images != null)
            {
                files.AddRange(images.Where(f => f != null && f.ContentLength > 0));
            }

            // Also check Request.Files for backward compatibility
            for (int i = 0; i < Request.Files.Count; i++)
            {
                var f = Request.Files[i];
                if (f != null && f.ContentLength > 0 && !files.Contains(f))
                {
                    files.Add(f);
                }
            }

            if (files.Count == 0)
            {
                return JsonResponseBuilder.Error("NO_IMAGE");
            }

            // Load configuration
            var maxBytes = ConfigurationService.GetInt("Biometrics:MaxUploadBytes", 10 * 1024 * 1024);
            var maxImages = ConfigurationService.GetInt("Biometrics:Enroll:CaptureTarget", 20);   // was 8
            var maxStored = ConfigurationService.GetInt("Biometrics:Enroll:MaxStoredVectors", 8); // was 5
            var strictTol = ConfigurationService.GetDouble("Biometrics:EnrollmentStrictTolerance", 0.45);
            var isMobile = DeviceService.IsMobileDevice(Request);

            files = files.Take(maxImages).ToList();

            // Validate employee exists
            Employee emp;
            using (var db = new FaceAttendDBEntities())
            {
                emp = db.Employees.FirstOrDefault(e => e.EmployeeId == employeeId);
                if (emp == null)
                {
                    return JsonResponseBuilder.Error("EMPLOYEE_NOT_FOUND");
                }
            }

            // Process frames
            var candidates = new ConcurrentBag<EnrollCandidate>();
            int processedCount = 0;
            bool duplicateFound = false;
            string duplicateId = null;
            var lockObj = new object();

            var parallelism = Math.Min(files.Count,
                ConfigurationService.GetInt("Biometrics:Enroll:Parallelism", 4));

            // Parallel frame processing
            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, 
                (file, state) =>
            {
                if (duplicateFound) return;

                string path = null, processedPath = null;
                try
                {
                    // Security validation
                    if (!FileSecurityService.IsValidImage(file.InputStream, 
                        new[] { ".jpg", ".jpeg", ".png" }))
                    {
                        return;
                    }

                    file.InputStream.Position = 0;
                    path = FileSecurityService.SaveTemp(file, "enr_", maxBytes);
                    bool isProc;
                    processedPath = ImagePreprocessor.PreprocessForDetection(path, "enr_", out isProc);

                    var dlib = new DlibBiometrics();
                    DlibBiometrics.FaceBox faceBox;
                    FaceRecognitionDotNet.Location faceLoc;
                    string detectErr;

                    if (!dlib.TryDetectSingleFaceFromFile(processedPath, out faceBox, out faceLoc, out detectErr))
                        return;

                    // Sharpness check
                    var sharpness = FaceQualityAnalyzer.CalculateSharpness(processedPath, faceBox);
                    var sharpTh = FaceQualityAnalyzer.GetSharpnessThreshold(isMobile);
                    if (sharpness < sharpTh) return;

                    // Parallel: liveness + encoding
                    double[] vec = null;
                    float liveness = 0f;
                    bool liveOk = false;

                    Parallel.Invoke(
                        () =>
                        {
                            string encErr;
                            dlib.TryEncodeFromFileWithLocation(processedPath, faceLoc, out vec, out encErr);
                        },
                        () =>
                        {
                            var live = new OnnxLiveness();
                            var scored = live.ScoreFromFile(processedPath, faceBox);
                            liveOk = scored.Ok;
                            liveness = scored.Probability ?? 0f;
                        });

                    var liveTh = (float)ConfigurationService.GetDouble(
                        "Biometrics:Enroll:LivenessThreshold", 0.82);

                    if (!liveOk || liveness < liveTh || vec == null) return;

                    // Duplicate check
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
                    try
                    {
                        using (var bmp = new System.Drawing.Bitmap(processedPath))
                        {
                            imgW = bmp.Width;
                            imgH = bmp.Height;
                        }
                    }
                    catch { }

                    var (yaw, pitch) = FaceQualityAnalyzer.EstimatePose(faceBox, imgW, imgH);
                    var bucket = FaceQualityAnalyzer.GetPoseBucket(yaw, pitch);

                    if (bucket == "other") return;

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
                        "[EnrollmentController.Enroll] Frame error: {0}", ex.Message);
                }
                finally
                {
                    ImagePreprocessor.Cleanup(processedPath, path);
                    FileSecurityService.TryDelete(path);
                }
            });

            // Handle duplicate found
            if (duplicateFound)
            {
                return JsonResponseBuilder.Error("FACE_ALREADY_ENROLLED",
                    details: new
                    {
                        step = "duplicate_check",
                        matchEmployeeId = duplicateId,
                        processed = processedCount,
                        timeMs = sw.ElapsedMilliseconds
                    });
            }

            if (candidates.IsEmpty)
            {
                return JsonResponseBuilder.Error("NO_GOOD_FRAME",
                    details: new
                    {
                        step = "processing",
                        processed = processedCount,
                        timeMs = sw.ElapsedMilliseconds
                    });
            }

            // Diversity-aware selection
            // Diversity-aware selection
            var selected = SelectDiverseFrames(candidates.ToList(), maxStored, isMobile);
            // Save to database
            using (var db = new FaceAttendDBEntities())
            {
                emp = db.Employees.First(e => e.EmployeeId == employeeId);

                // Best single vector (primary)
                var bestBytes = DlibBiometrics.EncodeToBytes(selected[0].Vec);
                emp.FaceEncodingBase64 = BiometricCrypto.ProtectBase64Bytes(bestBytes);

                // All selected vectors as JSON
                var jsonList = selected.Select(c =>
                    BiometricCrypto.ProtectBase64Bytes(DlibBiometrics.EncodeToBytes(c.Vec))
                ).ToList();
                emp.FaceEncodingsJson = BiometricCrypto.ProtectString(
                    Newtonsoft.Json.JsonConvert.SerializeObject(jsonList));

                emp.EnrolledDate = emp.EnrolledDate ?? DateTime.UtcNow;
                emp.Status = "ACTIVE";

                db.SaveChanges();
                
                // Cache invalidation - PASS DB CONTEXT to ensure transactional consistency
                // This ensures the cache sees the just-committed data
                FastFaceMatcher.UpdateEmployee(employeeId, db);
                EmployeeFaceIndex.Invalidate();
            }

            return JsonResponseBuilder.Success(new
            {
                savedVectors = selected.Count,
                timeMs = sw.ElapsedMilliseconds,
                poseBuckets = selected.Select(c => c.PoseBucket).ToList()
            });
        }

        /// <summary>
        /// Check for duplicate face without enrolling
        /// </summary>
        [HttpPost]
        [Route("check-duplicate")]
        [ValidateAntiForgeryToken]
        public ActionResult CheckDuplicate(HttpPostedFileBase image, string excludeEmployeeId = null)
        {
            if (image == null || image.ContentLength <= 0)
            {
                return JsonResponseBuilder.Error("NO_IMAGE");
            }

            string tempPath = null;
            string processedPath = null;

            try
            {
                var maxBytes = ConfigurationService.GetInt("Biometrics:MaxUploadBytes", 10 * 1024 * 1024);
                tempPath = FileSecurityService.SaveTemp(image, "dup_", maxBytes);

                bool isProcessed;
                processedPath = ImagePreprocessor.PreprocessForDetection(tempPath, "dup_", out isProcessed);

                var dlib = new DlibBiometrics();
                DlibBiometrics.FaceBox faceBox;
                FaceRecognitionDotNet.Location faceLoc;
                string detectErr;

                if (!dlib.TryDetectSingleFaceFromFile(processedPath, out faceBox, out faceLoc, out detectErr))
                {
                    return JsonResponseBuilder.Success(new
                    {
                        ok = true,
                        isDuplicate = false,
                        faceDetected = false
                    });
                }

                double[] vec;
                string encodeErr;
                if (!dlib.TryEncodeFromFileWithLocation(processedPath, faceLoc, out vec, out encodeErr) 
                    || vec == null)
                {
                    return JsonResponseBuilder.Success(new
                    {
                        ok = true,
                        isDuplicate = false,
                        faceDetected = true,
                        encodable = false
                    });
                }

                var strictTol = ConfigurationService.GetDouble(
                    "Biometrics:EnrollmentStrictTolerance", 0.45);

                using (var db = new FaceAttendDBEntities())
                {
                    var dup = DuplicateCheckHelper.FindDuplicate(db, vec, excludeEmployeeId, strictTol);

                    if (!string.IsNullOrEmpty(dup))
                    {
                        return JsonResponseBuilder.Success(new
                        {
                            ok = true,
                            isDuplicate = true,
                            matchEmployeeId = dup,
                            faceDetected = true,
                            encodable = true
                        });
                    }
                }

                return JsonResponseBuilder.Success(new
                {
                    ok = true,
                    isDuplicate = false,
                    faceDetected = true,
                    encodable = true
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("[EnrollmentController.CheckDuplicate] Error: {0}", ex);
                return JsonResponseBuilder.Error("CHECK_ERROR");
            }
            finally
            {
                ImagePreprocessor.Cleanup(processedPath, tempPath);
                FileSecurityService.TryDelete(tempPath);
            }
        }

        /// <summary>
        /// Select diverse frames based on pose buckets
        /// </summary>
        // In SelectDiverseFrames method - weight quality higher to prevent bad enrollments
        private static List<EnrollCandidate> SelectDiverseFrames(
            List<EnrollCandidate> candidates, int targetCount, bool isMobile)
        {
            var desiredBuckets = new[] { "center", "left", "right", "up", "down" };
            var selected = new List<EnrollCandidate>();

            // Quality calculation with mobile adjustments
            foreach (var c in candidates)
            {
                // FIX: Explicit cast to match EnrollCandidate.QualityScore type (float)
                c.QualityScore = (float)CalculateMobileOptimizedQuality(c, isMobile);
            }

            // Phase 1: Take best from each bucket, prioritizing center and left/right
            var priorityBuckets = isMobile
                ? new[] { "center", "left", "right" }  // Mobile: prioritize horizontal angles
                : desiredBuckets;

            foreach (var bucket in priorityBuckets)
            {
                if (selected.Count >= targetCount) break;

                var best = candidates
                    .Where(c => c.PoseBucket == bucket && !selected.Contains(c))
                    .OrderByDescending(c => c.QualityScore)
                    .FirstOrDefault();

                if (best != null) selected.Add(best);
            }

            // Phase 2: Fill remaining with highest quality regardless of angle
            var remaining = candidates
                .Where(c => !selected.Contains(c))
                .OrderByDescending(c => c.QualityScore)
                .Take(targetCount - selected.Count);

            selected.AddRange(remaining);

            // MOBILE: If we still don't have enough diversity, lower threshold and retry
            if (isMobile && selected.Select(c => c.PoseBucket).Distinct().Count() < 3)
            {
                // Accept lower quality frames to get diversity
                var extra = candidates
                    .Where(c => !selected.Contains(c))
                    .OrderByDescending(c => c.Liveness) // At least good liveness
                    .Take(targetCount - selected.Count);
                selected.AddRange(extra);
            }

            return selected.OrderByDescending(c => c.QualityScore).ToList();
        }

        // Change this method signature from double to float
        private static float CalculateMobileOptimizedQuality(EnrollCandidate c, bool isMobile)
        {
            if (!isMobile)
            {
                // FIX: Cast to float
                return (float)FaceQualityAnalyzer.CalculateQualityScore(
                    c.Liveness, c.Sharpness, c.Area, c.PoseYaw, c.PosePitch);
            }

            // Mobile: Weight liveness higher, sharpness lower (mobile cameras are noisier)
            var livenessWeight = 0.50f;  // Use 'f' suffix for float literals
            var sharpnessWeight = 0.20f;
            var areaWeight = 0.20f;
            var poseWeight = 0.10f;

            // Normalize sharpness (mobile max ~200 vs desktop ~400)
            var sharpnessNorm = Math.Min(c.Sharpness / 200.0, 1.0);

            // FIX: Return float, not double
            return (float)((c.Liveness * livenessWeight) +
                   (sharpnessNorm * sharpnessWeight) +
                   (Math.Min(c.Area / 50000.0, 1.0) * areaWeight) +
                   ((1.0 - Math.Abs(c.PoseYaw) / 45.0) * poseWeight * 0.5) +
                   ((1.0 - Math.Abs(c.PosePitch) / 45.0) * poseWeight * 0.5));
        }
    }
}
