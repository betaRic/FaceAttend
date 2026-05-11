using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Web;
using System.Web.Mvc;
using FaceAttend.Models.Dtos;
using FaceAttend.Services;
using FaceAttend.Services.Biometrics;
using FaceAttend.Services.Security;

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
            List<HttpPostedFileBase> images)
        {
            var sw = Stopwatch.StartNew();

            if (string.IsNullOrWhiteSpace(employeeId))
                return JsonResponseBuilder.Error("NO_EMPLOYEE_ID");

            employeeId = employeeId.Trim().ToUpper();
            if (employeeId.Length > 20)
                return JsonResponseBuilder.Error("EMPLOYEE_ID_TOO_LONG");

            var files = EnrollmentCaptureService.CollectFiles(Request, images);
            if (files.Count == 0)
                return JsonResponseBuilder.Error("NO_IMAGE");

            var maxBytes = ConfigurationService.GetInt("Biometrics:MaxUploadBytes", 10 * 1024 * 1024);
            var maxImages = ConfigurationService.GetInt("Biometrics:Enroll:CaptureTarget", 30);
            var maxStored = ConfigurationService.GetInt("Biometrics:Enroll:MaxStoredVectors", 25);
            var strictTol   = ConfigurationService.GetDouble("Biometrics:EnrollmentStrictTolerance", 0.45);
            var parallelism = Math.Min(
                files.Count,
                ConfigurationService.GetInt("Biometrics:Enroll:Parallelism", 4));

            files = files.Take(maxImages).ToList();

            using (var db = new FaceAttendDBEntities())
            {
                if (!db.Employees.Any(e => e.EmployeeId == employeeId))
                    return JsonResponseBuilder.Error("EMPLOYEE_NOT_FOUND");
            }

            var captureResult = EnrollmentCaptureService.ExtractCandidates(
                files,
                DeviceService.IsMobileDevice(Request),
                maxBytes,
                parallelism);

            if (captureResult.Candidates.Count == 0)
                return JsonResponseBuilder.Error("NO_GOOD_FRAME", details: new
                {
                    processed = captureResult.ProcessedCount,
                    timeMs    = sw.ElapsedMilliseconds
                });

            return FinalizeEnrollment(employeeId, captureResult.Candidates, maxStored, strictTol, sw);
        }

        private ActionResult FinalizeEnrollment(
            string employeeId,
            List<EnrollCandidate> candidates,
            int maxStored,
            double strictTol,
            Stopwatch sw)
        {
            var selected = EnrollmentCaptureService.SelectDiverseByEmbedding(candidates, maxStored);

            string duplicateId = null;
            using (var checkDb = new FaceAttendDBEntities())
            {
                duplicateId = EnrollmentCaptureService.FindDuplicateEmployeeId(
                    checkDb,
                    selected,
                    employeeId,
                    strictTol);
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

                var currentStatus = (emp.Status ?? "PENDING").Trim().ToUpperInvariant();
                if (currentStatus == "INACTIVE")
                    return JsonResponseBuilder.Error("EMPLOYEE_INACTIVE",
                        "This employee account is inactive. Contact an administrator to re-enroll.");

                EnrollmentCaptureService.ApplyStoredVectors(emp, selected);
                emp.EnrolledDate = emp.EnrolledDate ?? DateTime.UtcNow;
                if (currentStatus == "PENDING")
                    emp.Status = "ACTIVE";

                db.SaveChanges();
                BiometricTemplateMetadataService.ReplaceForEmployee(
                    db,
                    emp.Id,
                    selected,
                    AuditHelper.GetActorIp(Request),
                    string.Equals(emp.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase));

                FastFaceMatcher.UpdateEmployee(employeeId, db);
                EmployeeFaceIndex.Invalidate();
            }

            return JsonResponseBuilder.Success(new
            {
                savedVectors = selected.Count,
                timeMs       = sw.ElapsedMilliseconds
            });
        }

        [HttpGet]
        [Route("config")]
        public ActionResult Config()
        {
            var policy = BiometricPolicy.Current;
            return Json(new
            {
                antiSpoofThreshold = policy.AntiSpoofClearThreshold,
                antiSpoofReviewThreshold = policy.AntiSpoofReviewThreshold,
                antiSpoofBlockThreshold = policy.AntiSpoofBlockThreshold,
                sharpnessDesktop   = ConfigurationService.GetDouble("Biometrics:Enroll:SharpnessThreshold",       35.0),
                sharpnessMobile    = ConfigurationService.GetDouble("Biometrics:Enroll:SharpnessThreshold:Mobile", 28.0),
                minFaceAreaDesktop = policy.EnrollmentMinFaceAreaRatio,
                minFaceAreaMobile  = policy.MobileEnrollmentMinFaceAreaRatio
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

                var biometric = new OpenVinoBiometrics();
                string analyzeErr;
                var analysis = biometric.AnalyzeFile(processedPath, BiometricScanMode.Enrollment, null, out analyzeErr);

                if (analysis == null || !analysis.Ok || analysis.SelectedFaceBox == null)
                    return JsonResponseBuilder.Success(new { isDuplicate = false, faceDetected = false });

                var vec = analysis.Embedding;
                if (!FaceVectorCodec.IsValidVector(vec))
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
    }
}
