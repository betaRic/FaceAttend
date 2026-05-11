using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Web.Mvc;
using FaceAttend.Filters;
using FaceAttend.Models.ViewModels.Mobile;
using FaceAttend.Services;
using FaceAttend.Services.Biometrics;
using FaceAttend.Services.Helpers;
using FaceAttend.Services.Recognition;
using FaceAttend.Services.Security;

namespace FaceAttend.Controllers.Mobile
{
    [RoutePrefix("MobileRegistration")]
    public class MobileEnrollmentController : Controller
    {
        [HttpGet]
        [Route("Enroll")]
        [RateLimit(Name = "MobileEnrollPage", MaxRequests = 60, WindowSeconds = 60, Burst = 20)]
        public ActionResult Enroll()
        {
            if (!DeviceService.IsMobileDevice(Request))
                return RedirectToRoute(new { controller = "Kiosk", action = "Index", area = "" });

            using (var db = new FaceAttendDBEntities())
            {
                ViewBag.Offices = db.Offices.Where(o => o.IsActive).ToList();
                ViewBag.Fingerprint = DeviceService.GenerateFingerprint(Request);
                ViewBag.PerFrameThreshold = BiometricPolicy.Current.AntiSpoofClearThresholdFor(true);
            }

            return View("~/Views/MobileRegistration/Enroll-mobile.cshtml");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Route("ScanFrame")]
        [RateLimit(Name = "MobileEnrollFrame", MaxRequests = 90, WindowSeconds = 60, Burst = 30)]
        public ActionResult ScanFrame()
        {
            try
            {
                var image = Request.Files["image"];
                if (image == null || image.ContentLength == 0)
                    return JsonResponseBuilder.Error("NO_IMAGE");

                var isMobile = DeviceService.IsMobileDevice(Request);
                var policy = BiometricPolicy.Current;
                var antiSpoofThreshold = policy.AntiSpoofClearThresholdFor(isMobile);
                var scan = FastScanPipeline.EnrollmentScanInMemory(
                    image,
                    null,
                    isMobile,
                    antiSpoofThreshold);

                if (!scan.Ok)
                    return JsonResponseBuilder.Error(scan.Error ?? "SCAN_FAIL", scan.Error);

                var antiSpoof = policy.EvaluateAntiSpoof(scan.AntiSpoofModelOk, scan.AntiSpoofScore, isMobile);
                bool antiSpoofOk = antiSpoof.Decision == AntiSpoofDecision.Pass;
                float poseYaw, posePitch;
                if (scan.Landmarks5 != null && scan.Landmarks5.Length >= 6)
                {
                    (poseYaw, posePitch) = FaceQualityAnalyzer.EstimatePoseFromLandmarks(scan.Landmarks5);

                    if (isMobile)
                    {
                        poseYaw *= 0.8f;
                        posePitch *= 0.8f;
                    }
                }
                else
                {
                    (poseYaw, posePitch) = FaceQualityAnalyzer.EstimatePose(
                        scan.FaceBox, scan.ImageWidth, scan.ImageHeight);
                }

                var poseBucket = FaceQualityAnalyzer.GetPoseBucket(poseYaw, posePitch);
                var faceAreaRatio = scan.FaceBox != null && scan.ImageWidth > 0 && scan.ImageHeight > 0
                    ? (double?)(Math.Max(0, scan.FaceBox.Width) * Math.Max(0, scan.FaceBox.Height) /
                                (double)(scan.ImageWidth * scan.ImageHeight))
                    : null;
                var sharpnessOk = scan.Sharpness >= scan.SharpnessThreshold;
                var recognition = RecognitionDecisionFactory.FromEnrollmentFrame(
                    antiSpoofOk && sharpnessOk ? "ENROLLMENT_FRAME_ACCEPTABLE" : "ENROLLMENT_FRAME_REJECT",
                    antiSpoofOk && sharpnessOk,
                    isMobile ? "MOBILE_ENROLLMENT" : "KIOSK_ENROLLMENT",
                    scan.AntiSpoofScore,
                    antiSpoofThreshold,
                    antiSpoof,
                    scan.Sharpness,
                    scan.SharpnessThreshold,
                    scan.FaceBox,
                    scan.ImageWidth,
                    scan.ImageHeight,
                    scan.TimingMs);

                object landmarksResponse = null;
                if (scan.Landmarks5 != null && scan.Landmarks5.Length >= 6)
                {
                    var lm = scan.Landmarks5;
                    bool hasChin = lm.Length >= 8 && lm[7] > 0f;
                    if (hasChin)
                        landmarksResponse = new object[]
                        {
                            new { x = (int)lm[0], y = (int)lm[1] },
                            new { x = (int)lm[2], y = (int)lm[3] },
                            new { x = (int)lm[4], y = (int)lm[5] },
                            new { x = (int)lm[6], y = (int)lm[7] }
                        };
                    else
                        landmarksResponse = new object[]
                        {
                            new { x = (int)lm[0], y = (int)lm[1] },
                            new { x = (int)lm[2], y = (int)lm[3] },
                            new { x = (int)lm[4], y = (int)lm[5] }
                        };
                }

                return JsonResponseBuilder.Success(new
                {
                    antiSpoofScore = scan.AntiSpoofScore,
                    antiSpoofOk = antiSpoofOk,
                    antiSpoofDecision = antiSpoof.Decision.ToString().ToUpperInvariant(),
                    sharpness = scan.Sharpness,
                    sharpnessThreshold = scan.SharpnessThreshold,
                    count = 1,
                    isMatch = false,
                    matchEmployee = (string)null,
                    sharpnessOk = sharpnessOk,
                    poseYaw = poseYaw,
                    posePitch = posePitch,
                    poseBucket = poseBucket,
                    recognition = recognition,
                    landmarks = landmarksResponse,
                    faceBox = scan.FaceBox != null ? new
                    {
                        x = scan.FaceBox.Left,
                        y = scan.FaceBox.Top,
                        w = scan.FaceBox.Width,
                        h = scan.FaceBox.Height,
                        areaRatio = (float)(scan.FaceBox.Width * scan.FaceBox.Height) / (scan.ImageWidth * scan.ImageHeight)
                    } : null,
                    timingMs = scan.TimingMs,
                    isMobile = isMobile
                });
            }
            catch (Exception ex)
            {
                return JsonResponseBuilder.Error("SERVER_ERROR", ex.Message);
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Route("SubmitEnrollment")]
        [RateLimit(Name = "MobileSubmitEnrollment", MaxRequests = 8, WindowSeconds = 60, Burst = 2)]
        public ActionResult SubmitEnrollment(NewEmployeeEnrollmentVm vm, List<HttpPostedFileBase> images)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    var errors = string.Join(" | ", ModelState.Values
                        .SelectMany(v => v.Errors)
                        .Select(e => e.ErrorMessage));
                    Response.StatusCode = 400;
                    Response.TrySkipIisCustomErrors = true;
                    return JsonResponseBuilder.Error("VALIDATION_ERROR", errors);
                }

                var isMobile = DeviceService.IsMobileDevice(Request);
                var normalizedEmployeeId = vm.EmployeeId.Trim().ToUpperInvariant();
                var files = EnrollmentCaptureService.CollectFiles(Request, images);
                var maxBytes = ConfigurationService.GetInt("Biometrics:MaxUploadBytes", 10 * 1024 * 1024);
                var maxImages = ConfigurationService.GetInt("Biometrics:Enroll:CaptureTarget", 30);
                var maxStored = ConfigurationService.GetInt("Biometrics:Enroll:MaxStoredVectors", 25);
                var parallelism = Math.Min(
                    files.Count == 0 ? 1 : files.Count,
                    ConfigurationService.GetInt("Biometrics:Enroll:Parallelism", 4));

                files = files.Take(maxImages).ToList();
                if (files.Count == 0)
                {
                    Response.StatusCode = 400;
                    Response.TrySkipIisCustomErrors = true;
                    return JsonResponseBuilder.Error("FACE_REQUIRED", "Face enrollment is required.");
                }

                using (var db = new FaceAttendDBEntities())
                {
                    if (db.Employees.Any(e =>
                        e.EmployeeId == normalizedEmployeeId &&
                        (e.Status == null || e.Status != "INACTIVE")))
                    {
                        Response.StatusCode = 400;
                        Response.TrySkipIisCustomErrors = true;
                        return JsonResponseBuilder.Error(
                            "EMPLOYEE_ID_EXISTS",
                            "Employee ID already exists or is pending approval.");
                    }

                    var captureResult = EnrollmentCaptureService.ExtractCandidates(
                        files,
                        isMobile,
                        maxBytes,
                        parallelism);
                    if (captureResult.Candidates.Count == 0)
                    {
                        Response.StatusCode = 400;
                        Response.TrySkipIisCustomErrors = true;
                        return JsonResponseBuilder.Error(
                            "NO_GOOD_FRAME",
                            "No usable face samples were captured. Retake in better lighting and hold still.");
                    }

                    var strictTolerance =
                        ConfigurationService.GetDouble("Biometrics:EnrollmentStrictTolerance", 0.45);

                    var selected = EnrollmentCaptureService.SelectDiverseByEmbedding(
                        captureResult.Candidates,
                        maxStored);
                    var closestEmployee = EnrollmentCaptureService.FindClosestEmployee(
                        db,
                        selected,
                        normalizedEmployeeId);
                    var duplicateEmployeeId = closestEmployee != null && closestEmployee.Distance <= strictTolerance
                        ? closestEmployee.EmployeeId
                        : null;

                    if (!string.IsNullOrEmpty(duplicateEmployeeId))
                    {
                        Response.StatusCode = 400;
                        Response.TrySkipIisCustomErrors = true;
                        return JsonResponseBuilder.Error(
                            "FACE_ALREADY_ENROLLED",
                            $"This face is already enrolled to employee: {duplicateEmployeeId}. " +
                            "If this is an error, please contact admin.");
                    }

                    var riskyTolerance = ConfigurationService.GetDouble(
                        "Biometrics:EnrollmentRiskTolerance",
                        FastFaceMatcher.MedDistThresholdPublic);
                    if (closestEmployee != null && closestEmployee.Distance <= riskyTolerance)
                    {
                        Response.StatusCode = 400;
                        Response.TrySkipIisCustomErrors = true;
                        return JsonResponseBuilder.Error(
                            "FACE_RISKY_PAIR",
                            $"Enrollment is too visually close to employee: {closestEmployee.EmployeeId}. " +
                            "Please contact admin for supervised enrollment.",
                            details: new
                            {
                                closestEmployeeId = closestEmployee.EmployeeId,
                                distance = closestEmployee.Distance,
                                threshold = riskyTolerance
                            });
                    }

                    var gate = EnrollmentQualityGate.Validate(selected);
                    if (!gate.Passed)
                    {
                        Response.StatusCode = 400;
                        Response.TrySkipIisCustomErrors = true;
                        return JsonResponseBuilder.Error(gate.ErrorCode, gate.Message);
                    }

                    var employee = new Employee
                    {
                        EmployeeId = normalizedEmployeeId,
                        FirstName = (vm.FirstName ?? string.Empty).Trim(),
                        MiddleName = string.IsNullOrWhiteSpace(vm.MiddleName) ? null : vm.MiddleName.Trim(),
                        LastName = (vm.LastName ?? string.Empty).Trim(),
                        Position = string.IsNullOrWhiteSpace(vm.Position) ? null : vm.Position.Trim(),
                        Department = string.IsNullOrWhiteSpace(vm.Department) ? null : vm.Department.Trim(),
                        OfficeId = vm.OfficeId,
                        Status = "PENDING",
                        CreatedDate = DateTime.UtcNow,
                        EnrolledDate = DateTime.UtcNow,
                        LastModifiedDate = DateTime.UtcNow,
                        ModifiedBy = "SELF_ENROLLMENT_MOBILE"
                    };
                    EnrollmentCaptureService.ApplyStoredVectors(employee, selected);

                    db.Employees.Add(employee);
                    db.SaveChanges();
                    BiometricTemplateMetadataService.ReplaceForEmployee(
                        db,
                        employee.Id,
                        selected,
                        "SELF_ENROLLMENT_MOBILE",
                        isActive: false);
                    PublicAuditService.RecordEnrollmentSubmitted(
                        Request,
                        employee.EmployeeId,
                        employee.Id,
                        selected.Count,
                        isMobile);

                    Services.Biometrics.EmployeeFaceIndex.Invalidate();

                    try
                    {
                        FastFaceMatcher.UpdateEmployee(employee.EmployeeId, db);
                    }
                    catch
                    {
                    }

                    Response.StatusCode = 200;
                    Response.TrySkipIisCustomErrors = true;

                    return JsonResponseBuilder.Success(new
                    {
                        employeeDbId = employee.Id,
                        employeeId = employee.EmployeeId,
                        status = employee.Status,
                        savedVectors = selected.Count,
                        modelVersion = BiometricPolicy.Current.ModelVersion,
                        quality = new
                        {
                            average = selected.Count == 0 ? 0 : selected.Average(x => x.QualityScore),
                            min = selected.Count == 0 ? 0 : selected.Min(x => x.QualityScore),
                            max = selected.Count == 0 ? 0 : selected.Max(x => x.QualityScore)
                        },
                        isMobile = isMobile
                    }, "Enrollment submitted. Waiting for admin approval.");
                }
            }
            catch (System.Data.Entity.Validation.DbEntityValidationException ex)
            {
                var details = ex.EntityValidationErrors
                    .SelectMany(x => x.ValidationErrors)
                    .Select(x => (x.PropertyName ?? "(unknown)") + ": " + (x.ErrorMessage ?? "validation error"))
                    .ToList();

                Response.StatusCode = 400;
                Response.TrySkipIisCustomErrors = true;

                return Content(
                    Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        ok = false,
                        error = "VALIDATION_ERROR",
                        message = string.Join(" | ", details)
                    }),
                    "application/json");
            }
            catch (Exception ex)
            {
                Response.StatusCode = 500;
                Response.TrySkipIisCustomErrors = true;

                return Content(
                    Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        ok = false,
                        error = "SERVER_ERROR",
                        message = ex.GetBaseException().Message
                    }),
                    "application/json");
            }
        }

        [HttpGet]
        [Route("Identify")]
        [RateLimit(Name = "MobileIdentifyPage", MaxRequests = 60, WindowSeconds = 60, Burst = 20)]
        public ActionResult Identify()
        {
            if (!DeviceService.IsMobileDevice(Request))
                return RedirectToRoute(new { controller = "Kiosk", action = "Index", area = "" });

            ViewBag.Fingerprint = DeviceService.GenerateFingerprint(Request);
            ViewBag.PerFrameThreshold = BiometricPolicy.Current.AntiSpoofClearThresholdFor(true);

            return View("~/Views/MobileRegistration/Identify.cshtml");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Route("IdentifyEmployee")]
        [RateLimit(Name = "MobileIdentifyEmployee", MaxRequests = 30, WindowSeconds = 60, Burst = 10)]
        public ActionResult IdentifyEmployee()
        {
            Response.StatusCode = 410;
            Response.TrySkipIisCustomErrors = true;
            return JsonResponseBuilder.Error(
                "ENDPOINT_RETIRED",
                "Use /Attendance/Scan for public attendance scans.");
        }
    }
}
