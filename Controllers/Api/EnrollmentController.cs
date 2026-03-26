using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Web;
using System.Web.Mvc;
using FaceAttend.Services;
using FaceAttend.Services.Biometrics;
using FaceAttend.Services.Security;
using Newtonsoft.Json;

namespace FaceAttend.Controllers.Api
{
    /// <summary>
    /// Unified face enrollment API — admin, mobile, and visitor flows all route here.
    ///
    /// Each uploaded frame goes through this pipeline:
    ///   1.  Magic-byte image validation
    ///   2.  Temp-save + optional resize (ImagePreprocessor)
    ///   3.  Load Bitmap ONCE — every subsequent op reuses this single decode
    ///   4.  Face detection from Bitmap
    ///   5.  Minimum face-area gate (too far from camera)
    ///   6.  Sharpness gate (Laplacian variance on face ROI)
    ///   7.  Pre-extract raw RGB bytes — required for thread-safe parallel ops
    ///   8.  Parallel: liveness score + face encoding
    ///   9.  Liveness gate — threshold aligned with ScanController (THRESH-01 fix)
    ///   10. Duplicate-face check
    ///   11. Pose estimation → angle bucket
    ///
    /// After all frames are scored:
    ///   • SelectDiverseFrames picks the best angle-diverse set
    ///   • EnrollmentQualityGate enforces 5-layer identity assurance
    ///   • Vectors are encrypted and persisted
    ///   • In-memory caches are invalidated
    /// </summary>
    [RoutePrefix("api/enrollment")]
    public class EnrollmentController : Controller
    {
        // =====================================================================
        // POST api/enrollment/enroll
        // =====================================================================

        [HttpPost]
        [Route("enroll")]
        [ValidateAntiForgeryToken]
        public ActionResult Enroll(
            string employeeId,
            List<HttpPostedFileBase> images,
            string allEncodingsJson = null)
        {
            var sw = Stopwatch.StartNew();

            // ── Input validation ──────────────────────────────────────────────
            if (string.IsNullOrWhiteSpace(employeeId))
                return JsonResponseBuilder.Error("NO_EMPLOYEE_ID");

            employeeId = employeeId.Trim().ToUpper();
            if (employeeId.Length > 20)
                return JsonResponseBuilder.Error("EMPLOYEE_ID_TOO_LONG");

            var files = CollectFiles(images);
            if (files.Count == 0)
                return JsonResponseBuilder.Error("NO_IMAGE");

            // ── Read config ONCE — never inside the hot parallel loop ─────────
            var maxBytes    = ConfigurationService.GetInt("Biometrics:MaxUploadBytes",          10 * 1024 * 1024);
            var maxImages   = ConfigurationService.GetInt("Biometrics:Enroll:CaptureTarget",    20);
            var maxStored   = ConfigurationService.GetInt("Biometrics:Enroll:MaxStoredVectors", 8);
            var strictTol   = ConfigurationService.GetDouble("Biometrics:EnrollmentStrictTolerance", 0.45);
            var parallelism = Math.Min(files.Count,
                              ConfigurationService.GetInt("Biometrics:Enroll:Parallelism", 4));
            var isMobile    = DeviceService.IsMobileDevice(Request);

            // THRESH-01 FIX: Use the same config key as ScanController.
            // The old key "Biometrics:Enroll:LivenessThreshold" defaulted to 0.82,
            // silently discarding every frame that already passed the scan endpoint
            // at 0.75. That mismatch was the root cause of NO_GOOD_FRAME errors.
            var liveTh  = (float)ConfigurationService.GetDouble("Biometrics:LivenessThreshold", 0.75);
            var sharpTh = FaceQualityAnalyzer.GetSharpnessThreshold(isMobile);

            // QUALITY-01: Minimum face-area ratio — rejects distant/tiny faces
            var minAreaRatio = isMobile
                ? ConfigurationService.GetDouble("Biometrics:Enroll:MinFaceAreaRatio:Mobile", 0.05)
                : ConfigurationService.GetDouble("Biometrics:Enroll:MinFaceAreaRatio",         0.08);

            files = files.Take(maxImages).ToList();

            // ── Verify employee exists before spending CPU on frames ──────────
            using (var db = new FaceAttendDBEntities())
            {
                if (!db.Employees.Any(e => e.EmployeeId == employeeId))
                    return JsonResponseBuilder.Error("EMPLOYEE_NOT_FOUND");
            }

            // ── Parallel frame processing ─────────────────────────────────────
            var  candidates     = new ConcurrentBag<EnrollCandidate>();
            int  processedCount = 0;
            bool duplicateFound = false;
            string duplicateId  = null;
            var  dupLock        = new object();

            Parallel.ForEach(files,
                new ParallelOptions { MaxDegreeOfParallelism = parallelism },
                (file, loopState) =>
            {
                if (duplicateFound) return;

                string path = null, processedPath = null;
                Bitmap bitmap = null;
                try
                {
                    // ── 1. Security: validate image magic bytes ───────────────
                    if (!FileSecurityService.IsValidImage(file.InputStream,
                            new[] { ".jpg", ".jpeg", ".png" }))
                        return;

                    // ── 2. Save to temp + resize if oversized ─────────────────
                    file.InputStream.Position = 0;
                    path = FileSecurityService.SaveTemp(file, "enr_", maxBytes);

                    bool wasResized;
                    processedPath = ImagePreprocessor.PreprocessForDetection(
                        path, "enr_", out wasResized);

                    // ── 3. Single Bitmap decode — every op below reuses it ────
                    // The old code decoded the same JPEG 3–4 times per frame.
                    // One decode → sharpness, RGB extraction, detection,
                    //              parallel liveness + encoding.
                    bitmap = new Bitmap(processedPath);
                    int imgW = bitmap.Width;
                    int imgH = bitmap.Height;

                    // ── 4. Face detection from Bitmap (no second file read) ───
                    var dlib = new DlibBiometrics();
                    DlibBiometrics.FaceBox faceBox;
                    FaceRecognitionDotNet.Location faceLoc;
                    string detectErr;

                    if (!dlib.TryDetectSingleFaceFromBitmap(
                            bitmap, out faceBox, out faceLoc, out detectErr))
                        return;

                    // ── 5. Face-area gate: reject distant/tiny faces ──────────
                    if (imgW > 0 && imgH > 0)
                    {
                        double areaRatio = (double)(faceBox.Width * faceBox.Height)
                                         / (imgW * imgH);
                        if (areaRatio < minAreaRatio) return;
                    }

                    // ── 6. Sharpness gate ─────────────────────────────────────
                    var sharpness = FaceQualityAnalyzer.CalculateSharpnessFromBitmap(
                        bitmap, faceBox);
                    if (sharpness < sharpTh) return;

                    // ── 7. Pre-extract RGB bytes for thread-safe parallelism ───
                    // OnnxLiveness.ScoreFromBitmap internally clones the Bitmap
                    // and calls LockBits on the clone — safe.
                    // DlibBiometrics.TryEncodeWithLandmarksFromRgbData works only
                    // on the byte array and never touches the Bitmap object.
                    // Pre-extracting here guarantees no concurrent LockBits on the
                    // same Bitmap. See FastScanPipeline.EnrollmentScanInMemory for
                    // the identical pattern.
                    byte[] rgbData;
                    try   { rgbData = DlibBiometrics.ExtractRgbData(bitmap); }
                    catch { return; }

                    // ── 8. Parallel: liveness + encoding ─────────────────────
                    double[] vec            = null;
                    float[]  enrollLandmarks = null;
                    float    livenessScr     = 0f;
                    bool     liveOk          = false;

                    Parallel.Invoke(
                        () =>
                        {
                            // Encoding from pre-extracted byte array only — Bitmap not touched
                            string encErr;
                            dlib.TryEncodeWithLandmarksFromRgbData(
                                rgbData, imgW, imgH, faceLoc,
                                out vec, out enrollLandmarks, out encErr);
                        },
                        () =>
                        {
                            // Liveness from Bitmap clone — OnnxLiveness makes its own
                            // clone so LockBits calls never race with the encoding thread
                            var live   = new OnnxLiveness();
                            var scored = live.ScoreFromBitmap(bitmap, faceBox);
                            liveOk      = scored.Ok;
                            livenessScr = scored.Probability ?? 0f;
                        });

                    // ── 9. Liveness gate (THRESH-01 aligned) ─────────────────
                    if (!liveOk || livenessScr < liveTh || vec == null) return;

                    // ── 10. Duplicate-face check (one DB call per good frame) ──
                    lock (dupLock)
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
                                    duplicateId    = dup;
                                    loopState.Stop();
                                    return;
                                }
                            }
                        }
                    }
                    if (duplicateFound) return;

                    // ── 11. Pose estimation → angle bucket (landmarks-first, matches client) ──
                    float yaw, pitch;
                    if (enrollLandmarks != null && enrollLandmarks.Length >= 6)
                        (yaw, pitch) = FaceQualityAnalyzer.EstimatePoseFromLandmarks(enrollLandmarks);
                    else
                        (yaw, pitch) = FaceQualityAnalyzer.EstimatePose(faceBox, imgW, imgH);
                    var bucket = FaceQualityAnalyzer.GetPoseBucket(yaw, pitch);
                    if (bucket == "other") return;  // extreme angle — discard

                    int area = Math.Max(0, faceBox.Width) * Math.Max(0, faceBox.Height);

                    candidates.Add(new EnrollCandidate
                    {
                        Vec          = vec,
                        Liveness     = livenessScr,
                        Area         = area,
                        Sharpness    = sharpness,
                        PoseYaw      = yaw,
                        PosePitch    = pitch,
                        PoseBucket   = bucket,
                        QualityScore = FaceQualityAnalyzer.CalculateQualityScore(
                            livenessScr, sharpness, area, yaw, pitch)
                    });
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning("[Enroll] Frame error: {0}", ex.Message);
                }
                finally
                {
                    // Always dispose and clean up — even on exception
                    bitmap?.Dispose();
                    ImagePreprocessor.Cleanup(processedPath, path);
                    FileSecurityService.TryDelete(path);
                }
            });

            // ── Early exits ───────────────────────────────────────────────────
            if (duplicateFound)
                return JsonResponseBuilder.Error("FACE_ALREADY_ENROLLED", details: new
                {
                    matchEmployeeId = duplicateId,
                    processed       = processedCount,
                    timeMs          = sw.ElapsedMilliseconds
                });

            if (candidates.IsEmpty)
                return JsonResponseBuilder.Error("NO_GOOD_FRAME", details: new
                {
                    processed = processedCount,
                    timeMs    = sw.ElapsedMilliseconds
                });

            // ── Select the best angle-diverse frame set ───────────────────────
            var selected = SelectDiverseFrames(candidates.ToList(), maxStored, isMobile);

            // Vector spread check — ensures enrollment vectors actually span different poses
            // If all vectors are near-identical the user did not move during enrollment
            if (selected.Count >= 2)
            {
                double maxSpread = 0;
                for (int i = 0; i < selected.Count; i++)
                    for (int j = i + 1; j < selected.Count; j++)
                    {
                        double d = DlibBiometrics.Distance(selected[i].Vec, selected[j].Vec);
                        if (d > maxSpread) maxSpread = d;
                    }

                double minSpreadRequired = ConfigurationService.GetDouble(
                    "Biometrics:Enroll:Gate:MinVectorSpread", 0.06);
                if (maxSpread < minSpreadRequired)
                    return JsonResponseBuilder.Error("FAKE_DIVERSITY", details: new
                    {
                        message  = "All captured frames are too similar. Move your head to each " +
                                   "angle when prompted. Do not remain still during enrollment.",
                        maxSpread = maxSpread,
                        required  = minSpreadRequired
                    });
            }

            // ── 5-layer identity assurance gate ──────────────────────────────
            // Checks: min vector count, angle diversity, intra-set diversity,
            // self-match verification, average quality floor.
            // Returns an actionable error message so the client can guide the user.
            // Self-match quality filter: remove vectors too far from rest of set
            // Catches bad enrollment frames that slipped past liveness (e.g. motion blur, partial occlusion)
            // A vector that cannot match its siblings at <0.40 will cause MEDIUM-tier hits at attendance time
            var selfMatchThreshold = ConfigurationService.GetDouble(
                "Biometrics:Enroll:Gate:SelfMatchMaxDist", 0.40);
            if (selected.Count > 1)
            {
                selected = selected.Where(candidate =>
                {
                    double minDist = double.PositiveInfinity;
                    foreach (var other in selected)
                    {
                        if (ReferenceEquals(other, candidate)) continue;
                        var d = DlibBiometrics.Distance(candidate.Vec, other.Vec);
                        if (d < minDist) minDist = d;
                    }
                    return minDist <= selfMatchThreshold;
                }).ToList();

                if (selected.Count == 0)
                    return JsonResponseBuilder.Error("NO_GOOD_FRAME", details: new
                    {
                        message  = "No vectors passed self-match quality check. Re-enroll with better lighting.",
                        processed = processedCount,
                        timeMs    = sw.ElapsedMilliseconds
                    });
            }

            var gate = EnrollmentQualityGate.Validate(selected);
            if (!gate.Passed)
                return JsonResponseBuilder.Error(gate.ErrorCode, gate.Message);

            // ── Persist to database ───────────────────────────────────────────
            using (var db = new FaceAttendDBEntities())
            {
                var emp = db.Employees.First(e => e.EmployeeId == employeeId);

                // Primary single vector — kept for legacy code that reads
                // FaceEncodingBase64 directly
                emp.FaceEncodingBase64 = BiometricCrypto.ProtectBase64Bytes(
                    DlibBiometrics.EncodeToBytes(selected[0].Vec));

                // All selected vectors as an encrypted JSON array
                emp.FaceEncodingsJson = BiometricCrypto.ProtectString(
                    JsonConvert.SerializeObject(
                        selected.Select(c => BiometricCrypto.ProtectBase64Bytes(
                                                 DlibBiometrics.EncodeToBytes(c.Vec)))
                                .ToList()));

                emp.EnrolledDate = emp.EnrolledDate ?? DateTime.UtcNow;
                emp.Status       = "ACTIVE";

                db.SaveChanges();

                // Pass the open DB context so the cache update sees the
                // just-committed rows — no read-your-own-writes gap.
                FastFaceMatcher.UpdateEmployee(employeeId, db);
                EmployeeFaceIndex.Invalidate();
            }

            return JsonResponseBuilder.Success(new
            {
                savedVectors = selected.Count,
                timeMs       = sw.ElapsedMilliseconds,
                poseBuckets  = selected.Select(c => c.PoseBucket).ToList()
            });
        }

        // =====================================================================
        // POST api/enrollment/check-duplicate
        // =====================================================================

        [HttpPost]
        [Route("check-duplicate")]
        [ValidateAntiForgeryToken]
        public ActionResult CheckDuplicate(
            HttpPostedFileBase image,
            string excludeEmployeeId = null)
        {
            if (image == null || image.ContentLength <= 0)
                return JsonResponseBuilder.Error("NO_IMAGE");

            string tempPath = null, processedPath = null;
            try
            {
                var maxBytes  = ConfigurationService.GetInt(
                    "Biometrics:MaxUploadBytes", 10 * 1024 * 1024);
                tempPath      = FileSecurityService.SaveTemp(image, "dup_", maxBytes);

                bool wasResized;
                processedPath = ImagePreprocessor.PreprocessForDetection(
                    tempPath, "dup_", out wasResized);

                var dlib = new DlibBiometrics();
                DlibBiometrics.FaceBox faceBox;
                FaceRecognitionDotNet.Location faceLoc;
                string detectErr;

                if (!dlib.TryDetectSingleFaceFromFile(
                        processedPath, out faceBox, out faceLoc, out detectErr))
                    return JsonResponseBuilder.Success(new
                    {
                        isDuplicate  = false,
                        faceDetected = false
                    });

                double[] vec; string encErr;
                if (!dlib.TryEncodeFromFileWithLocation(
                        processedPath, faceLoc, out vec, out encErr) || vec == null)
                    return JsonResponseBuilder.Success(new
                    {
                        isDuplicate  = false,
                        faceDetected = true,
                        encodable    = false
                    });

                var strictTol = ConfigurationService.GetDouble(
                    "Biometrics:EnrollmentStrictTolerance", 0.45);

                using (var db = new FaceAttendDBEntities())
                {
                    var dup = DuplicateCheckHelper.FindDuplicate(
                        db, vec, excludeEmployeeId, strictTol);

                    return JsonResponseBuilder.Success(new
                    {
                        isDuplicate     = !string.IsNullOrEmpty(dup),
                        matchEmployeeId = dup,
                        faceDetected    = true,
                        encodable       = true
                    });
                }
            }
            catch (Exception ex)
            {
                Trace.TraceError("[CheckDuplicate] Error: {0}", ex);
                return JsonResponseBuilder.Error("CHECK_ERROR");
            }
            finally
            {
                ImagePreprocessor.Cleanup(processedPath, tempPath);
                FileSecurityService.TryDelete(tempPath);
            }
        }

        // =====================================================================
        // Private helpers
        // =====================================================================

        /// <summary>
        /// Collects uploaded files from both the typed MVC model parameter
        /// and the raw Request.Files bag (for backward compatibility with
        /// older clients that POST files without named parameters).
        /// </summary>
        private List<HttpPostedFileBase> CollectFiles(List<HttpPostedFileBase> typed)
        {
            var files = new List<HttpPostedFileBase>();

            if (typed != null)
                files.AddRange(typed.Where(f => f != null && f.ContentLength > 0));

            for (int i = 0; i < Request.Files.Count; i++)
            {
                var f = Request.Files[i];
                if (f != null && f.ContentLength > 0 && !files.Contains(f))
                    files.Add(f);
            }

            return files;
        }

        /// <summary>
        /// Selects up to <paramref name="targetCount"/> frames that cover as
        /// many distinct pose angles as possible.
        ///
        /// Phase 1 — one best-quality frame per angle bucket (all 5 angles).
        /// Phase 2 — fill remaining slots with the highest-quality frames
        ///            regardless of angle.
        ///
        /// The old mobile-only shortcut (center/left/right only in Phase 1)
        /// was removed. The client-side angle enforcement now guarantees 5-angle
        /// input for both platforms, so Phase 1 always attempts all 5 buckets.
        /// </summary>
        private static List<EnrollCandidate> SelectDiverseFrames(
            List<EnrollCandidate> candidates, int targetCount, bool isMobile)
        {
            // Recompute with platform-tuned weights before sorting
            foreach (var c in candidates)
                c.QualityScore = ComputeQualityScore(c, isMobile);

            var allBuckets = new[] { "center", "left", "right", "up", "down" };
            var selected   = new List<EnrollCandidate>(targetCount);

            // Phase 1: one best frame per angle bucket
            foreach (var bucket in allBuckets)
            {
                if (selected.Count >= targetCount) break;

                var best = candidates
                    .Where(c => c.PoseBucket == bucket && !selected.Contains(c))
                    .OrderByDescending(c => c.QualityScore)
                    .FirstOrDefault();

                if (best != null) selected.Add(best);
            }

            // Phase 2: fill remaining slots with any high-quality frame
            var remaining = candidates
                .Where(c => !selected.Contains(c))
                .OrderByDescending(c => c.QualityScore)
                .Take(targetCount - selected.Count);

            selected.AddRange(remaining);

            // Return highest-quality first so selected[0] is always the best vector
            return selected.OrderByDescending(c => c.QualityScore).ToList();
        }

        /// <summary>
        /// Platform-tuned composite quality score (0–1 range).
        ///
        /// Desktop — delegates directly to FaceQualityAnalyzer (standard weights).
        /// Mobile  — raises liveness weight (primary signal on noisy cameras),
        ///           lowers sharpness weight, normalizes sharpness against a
        ///           mobile-realistic max (200 vs 400 on desktop), and clamps
        ///           pose centrality to [0, 1] to prevent negative scores on
        ///           angles beyond ±45°.
        /// </summary>
        private static float ComputeQualityScore(EnrollCandidate c, bool isMobile)
        {
            if (!isMobile)
                return (float)FaceQualityAnalyzer.CalculateQualityScore(
                    c.Liveness, c.Sharpness, c.Area, c.PoseYaw, c.PosePitch);

            const float wLiveness  = 0.50f;
            const float wSharpness = 0.20f;
            const float wArea      = 0.20f;
            const float wPose      = 0.10f;

            float normSharpness = Math.Min(c.Sharpness / 200f, 1f);
            float normArea      = Math.Min(c.Area      / 50000f, 1f);

            // Clamp to [0, 1]: extreme angles would yield negative without this
            float poseCentrality = Math.Max(0f,
                1f - (Math.Abs(c.PoseYaw) + Math.Abs(c.PosePitch)) / 90f);

            return (c.Liveness     * wLiveness)
                 + (normSharpness  * wSharpness)
                 + (normArea       * wArea)
                 + (poseCentrality * wPose);
        }
    }
}
