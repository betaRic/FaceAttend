using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Mvc;
using FaceAttend.Services;
using FaceAttend.Services.Biometrics;
using FaceAttend.Services.Security;
using Newtonsoft.Json;

namespace FaceAttend.Controllers.Api
{
    [RoutePrefix("api/enrollment")]
    public class EnrollmentController : Controller
    {
        [HttpPost]
        [Route("enroll")]
        [ValidateAntiForgeryToken]
        public ActionResult Enroll(
            string employeeId,
            List<HttpPostedFileBase> images,
            string allEncodingsJson = null)
        {
            var sw = Stopwatch.StartNew();

            if (string.IsNullOrWhiteSpace(employeeId))
                return JsonResponseBuilder.Error("NO_EMPLOYEE_ID");

            employeeId = employeeId.Trim().ToUpper();
            if (employeeId.Length > 20)
                return JsonResponseBuilder.Error("EMPLOYEE_ID_TOO_LONG");

            var files = CollectFiles(images);
            if (files.Count == 0)
                return JsonResponseBuilder.Error("NO_IMAGE");

            var maxBytes    = ConfigurationService.GetInt("Biometrics:MaxUploadBytes",          10 * 1024 * 1024);
            var maxImages   = ConfigurationService.GetInt("Biometrics:Enroll:CaptureTarget",    30);
            var maxStored   = ConfigurationService.GetInt("Biometrics:Enroll:MaxStoredVectors", 25);
            var strictTol   = ConfigurationService.GetDouble("Biometrics:EnrollmentStrictTolerance", 0.45);
            var parallelism = Math.Min(files.Count,
                              ConfigurationService.GetInt("Biometrics:Enroll:Parallelism", 4));
            var isMobile    = DeviceService.IsMobileDevice(Request);
            var liveTh      = (float)ConfigurationService.GetDouble("Biometrics:LivenessThreshold", 0.75);
            var sharpTh     = FaceQualityAnalyzer.GetSharpnessThreshold(isMobile);
            var minAreaRatio = isMobile
                ? ConfigurationService.GetDouble("Biometrics:Enroll:MinFaceAreaRatio:Mobile", 0.05)
                : ConfigurationService.GetDouble("Biometrics:Enroll:MinFaceAreaRatio",         0.08);

            files = files.Take(maxImages).ToList();

            using (var db = new FaceAttendDBEntities())
            {
                if (!db.Employees.Any(e => e.EmployeeId == employeeId))
                    return JsonResponseBuilder.Error("EMPLOYEE_NOT_FOUND");
            }

            var candidates     = new ConcurrentBag<EnrollCandidate>();
            int processedCount = 0;

            Parallel.ForEach(files,
                new ParallelOptions { MaxDegreeOfParallelism = parallelism },
                file =>
            {
                string path = null, processedPath = null;
                Bitmap bitmap = null;
                try
                {
                    if (!FileSecurityService.IsValidImage(file.InputStream, new[] { ".jpg", ".jpeg", ".png" }))
                        return;

                    file.InputStream.Position = 0;
                    path = FileSecurityService.SaveTemp(file, "enr_", maxBytes);

                    bool wasResized;
                    processedPath = ImagePreprocessor.PreprocessForDetection(path, "enr_", out wasResized);

                    bitmap = new Bitmap(processedPath);
                    int imgW = bitmap.Width, imgH = bitmap.Height;

                    var dlib = new DlibBiometrics();
                    DlibBiometrics.FaceBox faceBox;
                    FaceRecognitionDotNet.Location faceLoc;
                    string detectErr;

                    if (!dlib.TryDetectSingleFaceFromBitmap(bitmap, out faceBox, out faceLoc, out detectErr))
                        return;

                    if (imgW > 0 && imgH > 0)
                    {
                        double areaRatio = (double)(faceBox.Width * faceBox.Height) / (imgW * imgH);
                        if (areaRatio < minAreaRatio) return;
                    }

                    var sharpness = FaceQualityAnalyzer.CalculateSharpnessFromBitmap(bitmap, faceBox);
                    if (sharpness < sharpTh) return;

                    byte[] rgbData;
                    try   { rgbData = DlibBiometrics.ExtractRgbData(bitmap); }
                    catch { return; }

                    double[] vec             = null;
                    float[]  enrollLandmarks = null;
                    float    livenessScr     = 0f;
                    bool     liveOk          = false;

                    Parallel.Invoke(
                        () =>
                        {
                            string encErr;
                            dlib.TryEncodeWithLandmarksFromRgbData(
                                rgbData, imgW, imgH, faceLoc,
                                out vec, out enrollLandmarks, out encErr);
                        },
                        () =>
                        {
                            var live   = new OnnxLiveness();
                            var scored = live.ScoreFromBitmap(bitmap, faceBox);
                            liveOk      = scored.Ok;
                            livenessScr = scored.Probability ?? 0f;
                        });

                    if (!liveOk || livenessScr < liveTh || vec == null) return;

                    Interlocked.Increment(ref processedCount);

                    float yaw, pitch;
                    if (enrollLandmarks != null && enrollLandmarks.Length >= 6)
                        (yaw, pitch) = FaceQualityAnalyzer.EstimatePoseFromLandmarks(enrollLandmarks);
                    else
                        (yaw, pitch) = FaceQualityAnalyzer.EstimatePose(faceBox, imgW, imgH);

                    if (Math.Abs(yaw) > 45f || Math.Abs(pitch) > 55f) return;

                    candidates.Add(new EnrollCandidate
                    {
                        Vec          = vec,
                        Liveness     = livenessScr,
                        Area         = Math.Max(0, faceBox.Width) * Math.Max(0, faceBox.Height),
                        Sharpness    = sharpness,
                        PoseYaw      = yaw,
                        PosePitch    = pitch,
                        QualityScore = FaceQualityAnalyzer.CalculateQualityScore(
                            livenessScr, sharpness,
                            Math.Max(0, faceBox.Width) * Math.Max(0, faceBox.Height),
                            yaw, pitch)
                    });
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning("[Enroll] Frame error: {0}", ex.Message);
                }
                finally
                {
                    bitmap?.Dispose();
                    ImagePreprocessor.Cleanup(processedPath, path);
                    FileSecurityService.TryDelete(path);
                }
            });

            if (candidates.IsEmpty)
                return JsonResponseBuilder.Error("NO_GOOD_FRAME", details: new
                {
                    processed = processedCount,
                    timeMs    = sw.ElapsedMilliseconds
                });

            var selected = SelectDiverseByEmbedding(candidates.ToList(), maxStored);

            string duplicateId = null;
            using (var checkDb = new FaceAttendDBEntities())
            {
                duplicateId = DuplicateCheckHelper.FindDuplicate(
                    checkDb, selected[0].Vec, employeeId, strictTol);
            }
            if (!string.IsNullOrEmpty(duplicateId))
                return JsonResponseBuilder.Error("FACE_ALREADY_ENROLLED", details: new
                {
                    matchEmployeeId = duplicateId,
                    processed       = processedCount,
                    timeMs          = sw.ElapsedMilliseconds
                });

            var selfMatchThreshold = ConfigurationService.GetDouble(
                "Biometrics:Enroll:Gate:SelfMatchMaxDist", 0.60);
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
                        message   = "No vectors passed self-match quality check. Re-enroll with better lighting.",
                        processed = processedCount,
                        timeMs    = sw.ElapsedMilliseconds
                    });
            }

            var gate = EnrollmentQualityGate.Validate(selected);
            if (!gate.Passed)
                return JsonResponseBuilder.Error(gate.ErrorCode, gate.Message);

            using (var db = new FaceAttendDBEntities())
            {
                var emp = db.Employees.First(e => e.EmployeeId == employeeId);

                emp.FaceEncodingBase64 = BiometricCrypto.ProtectBase64Bytes(
                    DlibBiometrics.EncodeToBytes(selected[0].Vec));

                emp.FaceEncodingsJson = BiometricCrypto.ProtectString(
                    JsonConvert.SerializeObject(
                        selected.Select(c => BiometricCrypto.ProtectBase64Bytes(
                                                 DlibBiometrics.EncodeToBytes(c.Vec)))
                                .ToList()));

                emp.EnrolledDate = emp.EnrolledDate ?? DateTime.UtcNow;
                emp.Status       = "ACTIVE";

                db.SaveChanges();

                FastFaceMatcher.UpdateEmployee(employeeId, db);
                EmployeeFaceIndex.Invalidate();
            }

            return JsonResponseBuilder.Success(new
            {
                savedVectors = selected.Count,
                timeMs       = sw.ElapsedMilliseconds
            });
        }

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
                var maxBytes  = ConfigurationService.GetInt("Biometrics:MaxUploadBytes", 10 * 1024 * 1024);
                tempPath      = FileSecurityService.SaveTemp(image, "dup_", maxBytes);

                bool wasResized;
                processedPath = ImagePreprocessor.PreprocessForDetection(tempPath, "dup_", out wasResized);

                var dlib = new DlibBiometrics();
                DlibBiometrics.FaceBox faceBox;
                FaceRecognitionDotNet.Location faceLoc;
                string detectErr;

                if (!dlib.TryDetectSingleFaceFromFile(processedPath, out faceBox, out faceLoc, out detectErr))
                    return JsonResponseBuilder.Success(new { isDuplicate = false, faceDetected = false });

                double[] vec; string encErr;
                if (!dlib.TryEncodeFromFileWithLocation(processedPath, faceLoc, out vec, out encErr) || vec == null)
                    return JsonResponseBuilder.Success(new { isDuplicate = false, faceDetected = true, encodable = false });

                var strictTol = ConfigurationService.GetDouble("Biometrics:EnrollmentStrictTolerance", 0.45);

                using (var db = new FaceAttendDBEntities())
                {
                    var dup = DuplicateCheckHelper.FindDuplicate(db, vec, excludeEmployeeId, strictTol);
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

        private static List<EnrollCandidate> SelectDiverseByEmbedding(
            List<EnrollCandidate> candidates, int targetCount)
        {
            if (candidates.Count <= targetCount)
                return candidates.OrderByDescending(c => c.QualityScore).ToList();

            var sorted    = candidates.OrderByDescending(c => c.QualityScore).ToList();
            var selected  = new List<EnrollCandidate>(targetCount) { sorted[0] };
            var remaining = sorted.Skip(1).ToList();

            while (selected.Count < targetCount && remaining.Count > 0)
            {
                double bestMinDist = -1;
                int    bestIdx     = 0;

                for (int i = 0; i < remaining.Count; i++)
                {
                    double minDist = double.MaxValue;
                    foreach (var s in selected)
                    {
                        var d = DlibBiometrics.Distance(remaining[i].Vec, s.Vec);
                        if (d < minDist) minDist = d;
                    }
                    if (minDist > bestMinDist) { bestMinDist = minDist; bestIdx = i; }
                }

                selected.Add(remaining[bestIdx]);
                remaining.RemoveAt(bestIdx);
            }

            return selected.OrderByDescending(c => c.QualityScore).ToList();
        }
    }
}
