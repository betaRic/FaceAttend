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
using FaceAttend.Models.Dtos;
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

            files = files.Take(maxImages).ToList();

            using (var db = new FaceAttendDBEntities())
            {
                if (!db.Employees.Any(e => e.EmployeeId == employeeId))
                    return JsonResponseBuilder.Error("EMPLOYEE_NOT_FOUND");
            }

            // Fast path: if client sent pre-computed encodings from per-frame ScanFrame calls,
            // skip disk I/O, ImagePreprocessor, dlib detection, ONNX liveness, and dlib encoding.
            // Cuts typical 30-60 s "Saving frames" wait down to < 1 s.
            if (!string.IsNullOrEmpty(allEncodingsJson))
            {
                try
                {
                    var rawList = JsonConvert.DeserializeObject<List<string>>(allEncodingsJson);
                    if (rawList != null && rawList.Count > 0)
                    {
                        var fastCandidates = new List<EnrollCandidate>();
                        foreach (var b64 in rawList)
                        {
                            var bytes = Convert.FromBase64String(b64);
                            var vec   = DlibBiometrics.DecodeFromBytes(bytes);
                            if (vec != null && vec.Length == 128)
                                fastCandidates.Add(new EnrollCandidate { Vec = vec, Liveness = 1f, QualityScore = 1f });
                        }
                        if (fastCandidates.Count > 0)
                            return FinalizeEnrollment(employeeId, fastCandidates, maxStored, strictTol, sw);
                    }
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning("[Enroll] Fast-path parse failed, falling back to blob processing: {0}", ex.Message);
                }
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

                    if (!dlib.TryDetectSingleFaceFromBitmap(bitmap, out faceBox, out faceLoc, out detectErr, allowLargestFace: false))
                        return;

                    byte[] rgbData;
                    try   { rgbData = DlibBiometrics.ExtractRgbData(bitmap); }
                    catch { return; }

                    double[] vec             = null;
                    float[]  enrollLandmarks = null;
                    string   encErr2;
                    dlib.TryEncodeWithLandmarksFromRgbData(
                        rgbData, imgW, imgH, faceLoc,
                        out vec, out enrollLandmarks, out encErr2);

                    if (vec == null) return;

                    Interlocked.Increment(ref processedCount);

                    candidates.Add(new EnrollCandidate
                    {
                        Vec          = vec,
                        Liveness     = 1f,
                        Area         = Math.Max(0, faceBox.Width) * Math.Max(0, faceBox.Height),
                        Sharpness    = 0f,
                        PoseYaw      = 0f,
                        PosePitch    = 0f,
                        QualityScore = 1f
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

            return FinalizeEnrollment(employeeId, candidates.ToList(), maxStored, strictTol, sw);
        }

        private ActionResult FinalizeEnrollment(
            string employeeId,
            List<EnrollCandidate> candidates,
            int maxStored,
            double strictTol,
            Stopwatch sw)
        {
            var selected = SelectDiverseByEmbedding(candidates, maxStored);

            // Check ALL selected vectors to catch cases where one closely matches another employee.
            string duplicateId = null;
            using (var checkDb = new FaceAttendDBEntities())
            {
                foreach (var candidate in selected)
                {
                    duplicateId = DuplicateCheckHelper.FindDuplicate(
                        checkDb, candidate.Vec, employeeId, strictTol);
                    if (!string.IsNullOrEmpty(duplicateId)) break;
                }
            }
            if (!string.IsNullOrEmpty(duplicateId))
                return JsonResponseBuilder.Error("FACE_ALREADY_ENROLLED", details: new
                {
                    matchEmployeeId = duplicateId,
                    timeMs          = sw.ElapsedMilliseconds
                });

            var gate = EnrollmentQualityGate.Validate(selected);
            if (!gate.Passed)
                return JsonResponseBuilder.Error(gate.ErrorCode, gate.Message);

            using (var db = new FaceAttendDBEntities())
            {
                var emp = db.Employees.First(e => e.EmployeeId == employeeId);

                // Guard: never re-activate an admin-deactivated employee via enrollment
                var currentStatus = (emp.Status ?? "PENDING").Trim().ToUpperInvariant();
                if (currentStatus == "INACTIVE")
                    return JsonResponseBuilder.Error("EMPLOYEE_INACTIVE",
                        "This employee account is inactive. Contact an administrator to re-enroll.");

                emp.FaceEncodingBase64 = BiometricCrypto.ProtectBase64Bytes(
                    DlibBiometrics.EncodeToBytes(selected[0].Vec));

                emp.FaceEncodingsJson = BiometricCrypto.ProtectString(
                    JsonConvert.SerializeObject(
                        selected.Select(c => BiometricCrypto.ProtectBase64Bytes(
                                                 DlibBiometrics.EncodeToBytes(c.Vec)))
                                .ToList()));

                emp.EnrolledDate = emp.EnrolledDate ?? DateTime.UtcNow;
                // Only promote PENDING → ACTIVE; ACTIVE employees keep their status unchanged
                if (currentStatus == "PENDING")
                    emp.Status = "ACTIVE";

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

        /// <summary>
        /// Returns enrollment quality thresholds so the JS client stays in sync with server config.
        /// Called by all enrollment pages (admin + mobile) after page load. Fallback defaults here
        /// must match Web.config and enrollment-core.js CONSTANTS to prevent threshold drift.
        /// </summary>
        [HttpGet]
        [Route("config")]
        public ActionResult Config()
        {
            return Json(new
            {
                livenessThreshold  = ConfigurationService.GetDouble("Biometrics:LivenessThreshold",               0.65),
                sharpnessDesktop   = ConfigurationService.GetDouble("Biometrics:Enroll:SharpnessThreshold",       35.0),
                sharpnessMobile    = ConfigurationService.GetDouble("Biometrics:Enroll:SharpnessThreshold:Mobile", 28.0),
                minFaceAreaDesktop = ConfigurationService.GetDouble("Biometrics:Enroll:MinFaceAreaRatio",          0.08),
                minFaceAreaMobile  = ConfigurationService.GetDouble("Biometrics:Enroll:MinFaceAreaRatio:Mobile",   0.06)
            }, JsonRequestBehavior.AllowGet);
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
