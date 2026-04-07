using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Web.Mvc;
using FaceAttend.Models.ViewModels.Mobile;
using FaceAttend.Services;
using FaceAttend.Services.Biometrics;
using FaceAttend.Services.Helpers;

namespace FaceAttend.Controllers.Mobile
{
    /// <summary>
    /// Handles new-employee face enrollment and existing-employee identification
    /// for the mobile registration wizard.
    /// URLs remain at /MobileRegistration/* for compatibility.
    /// </summary>
    [RoutePrefix("MobileRegistration")]
    public class MobileEnrollmentController : Controller
    {
        // ── New employee: face enrollment wizard ─────────────────────────────────

        [HttpGet]
        [Route("Enroll")]
        public ActionResult Enroll()
        {
            if (!DeviceService.IsMobileDevice(Request))
                return RedirectToRoute(new { controller = "Kiosk", action = "Index", area = "" });

            using (var db = new FaceAttendDBEntities())
            {
                ViewBag.Offices = db.Offices.Where(o => o.IsActive).ToList();
                ViewBag.Fingerprint = DeviceService.GenerateFingerprint(Request);
                ViewBag.PerFrameThreshold = ConfigurationService.GetDouble("Biometrics:LivenessThreshold", 0.65);
            }

            return View("~/Views/MobileRegistration/Enroll-mobile.cshtml");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Route("ScanFrame")]
        public ActionResult ScanFrame()
        {
            try
            {
                var image = Request.Files["image"];
                if (image == null || image.ContentLength == 0)
                    return JsonResponseBuilder.Error("NO_IMAGE");

                var isMobile = DeviceService.IsMobileDevice(Request);

                var scan = FastScanPipeline.EnrollmentScanInMemory(image, null, isMobile);

                if (!scan.Ok)
                    return JsonResponseBuilder.Error(scan.Error ?? "SCAN_FAIL", scan.Error);

                double livenessThreshold = ConfigurationService.GetDouble("Biometrics:LivenessThreshold", 0.65);

                bool livenessOk = scan.LivenessOk && scan.LivenessScore >= (float)livenessThreshold;

                var enrollStrictTolerance = isMobile ? 0.50 :
                    ConfigurationService.GetDouble("Biometrics:EnrollmentStrictTolerance", 0.45);

                string duplicateEmployeeId = null;
                using (var checkDb = new FaceAttendDBEntities())
                {
                    duplicateEmployeeId = DuplicateCheckHelper.FindDuplicate(
                        checkDb, scan.FaceEncoding, null, enrollStrictTolerance);
                }

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
                    liveness = scan.LivenessScore,
                    livenessOk = livenessOk,
                    encoding = scan.Base64Encoding,
                    sharpness = scan.Sharpness,
                    sharpnessThreshold = scan.SharpnessThreshold,
                    count = 1,
                    isMatch = !string.IsNullOrEmpty(duplicateEmployeeId),
                    matchEmployee = duplicateEmployeeId,
                    sharpnessOk = scan.Sharpness >= scan.SharpnessThreshold,
                    poseYaw = poseYaw,
                    posePitch = posePitch,
                    poseBucket = poseBucket,
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
        public ActionResult SubmitEnrollment(NewEmployeeEnrollmentVm vm)
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

                var fingerprint = DeviceService.GenerateFingerprint(Request);
                var isMobile = DeviceService.IsMobileDevice(Request);

                var normalizedEmployeeId = vm.EmployeeId.Trim().ToUpperInvariant();

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

                    byte[] faceTemplate;
                    double[] faceVector;
                    try
                    {
                        faceTemplate = Convert.FromBase64String(vm.FaceEncoding ?? string.Empty);
                        faceVector = DlibBiometrics.DecodeFromBytes(faceTemplate);
                    }
                    catch
                    {
                        Response.StatusCode = 400;
                        Response.TrySkipIisCustomErrors = true;
                        return JsonResponseBuilder.Error("INVALID_FACE_DATA", "Invalid face data.");
                    }

                    if (faceTemplate == null || faceTemplate.Length == 0 || faceVector == null)
                    {
                        Response.StatusCode = 400;
                        Response.TrySkipIisCustomErrors = true;
                        return JsonResponseBuilder.Error("FACE_REQUIRED", "Face enrollment is required.");
                    }

                    var strictTolerance = isMobile ? 0.50 :
                        ConfigurationService.GetDouble("Biometrics:EnrollmentStrictTolerance", 0.45);

                    var duplicateEmployeeId = DuplicateCheckHelper.FindDuplicate(
                        db, faceVector, normalizedEmployeeId, strictTolerance);

                    if (!string.IsNullOrEmpty(duplicateEmployeeId))
                    {
                        Response.StatusCode = 400;
                        Response.TrySkipIisCustomErrors = true;
                        return JsonResponseBuilder.Error(
                            "FACE_ALREADY_ENROLLED",
                            $"This face is already enrolled to employee: {duplicateEmployeeId}. " +
                            "If this is an error, please contact admin.");
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

                        FaceEncodingBase64 = BiometricCrypto.ProtectBase64Bytes(faceTemplate),
                        FaceEncodingsJson = !string.IsNullOrWhiteSpace(vm.AllFaceEncodingsJson)
                            ? EncryptEncodingsJson(vm.AllFaceEncodingsJson)
                            : null,

                        Status = "PENDING",

                        CreatedDate = DateTime.UtcNow,
                        EnrolledDate = DateTime.UtcNow,
                        LastModifiedDate = DateTime.UtcNow,
                        ModifiedBy = "SELF_ENROLLMENT_MOBILE"
                    };

                    db.Employees.Add(employee);
                    db.SaveChanges();

                    Services.Biometrics.EmployeeFaceIndex.Invalidate();

                    try
                    {
                        FastFaceMatcher.UpdateEmployee(employee.EmployeeId, db);
                    }
                    catch
                    {
                        // Non-fatal: cache will rebuild on next scan
                    }

                    Response.StatusCode = 200;
                    Response.TrySkipIisCustomErrors = true;

                    return JsonResponseBuilder.Success(new
                    {
                        employeeDbId = employee.Id,
                        employeeId = employee.EmployeeId,
                        status = employee.Status,
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

        /// <summary>
        /// Converts a JSON array of raw base64 face-vector strings (as returned by ScanFrame)
        /// into the encrypted format written by EnrollmentController.Enroll:
        ///   ProtectString( JSON( [ ProtectBase64Bytes(bytes1), ProtectBase64Bytes(bytes2), … ] ) )
        /// Returns null on any parse/encrypt failure so the caller can fall back gracefully.
        /// </summary>
        private static string EncryptEncodingsJson(string rawJson)
        {
            try
            {
                var rawList = Newtonsoft.Json.JsonConvert.DeserializeObject<System.Collections.Generic.List<string>>(rawJson);
                if (rawList == null || rawList.Count == 0) return null;

                var protectedList = rawList
                    .Select(b64 => BiometricCrypto.ProtectBase64Bytes(Convert.FromBase64String(b64)))
                    .ToList();

                return BiometricCrypto.ProtectString(
                    Newtonsoft.Json.JsonConvert.SerializeObject(protectedList));
            }
            catch
            {
                return null;
            }
        }

        // ── Existing employee: face identification ────────────────────────────────

        [HttpGet]
        [Route("Identify")]
        public ActionResult Identify()
        {
            if (!DeviceService.IsMobileDevice(Request))
                return RedirectToRoute(new { controller = "Kiosk", action = "Index", area = "" });

            ViewBag.Fingerprint = DeviceService.GenerateFingerprint(Request);
            ViewBag.PerFrameThreshold = ConfigurationService.GetDouble("Biometrics:LivenessThreshold", 0.65);

            return View("~/Views/MobileRegistration/Identify.cshtml");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Route("IdentifyEmployee")]
        public ActionResult IdentifyEmployee()
        {
            try
            {
                var image = Request.Files["image"];
                if (image == null || image.ContentLength == 0)
                    return JsonResponseBuilder.Error("NO_IMAGE");

                Bitmap bitmap = null;
                try
                {
                    using (var ms = new MemoryStream())
                    {
                        image.InputStream.CopyTo(ms);
                        ms.Position = 0;
                        bitmap = new Bitmap(ms);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.TraceError("[IdentifyEmployee] Failed to load image: " + ex.Message);
                    return JsonResponseBuilder.Error("IMAGE_LOAD_FAIL", "Could not process image.");
                }

                double[] faceEncoding;
                using (bitmap)
                {
                    var dlib = new DlibBiometrics();
                    DlibBiometrics.FaceBox faceBox;
                    FaceRecognitionDotNet.Location faceLoc;
                    string detectErr;

                    if (!dlib.TryDetectSingleFaceFromBitmap(bitmap, out faceBox, out faceLoc, out detectErr))
                    {
                        System.Diagnostics.Trace.TraceWarning("[IdentifyEmployee] Face detection failed: " + detectErr);
                        return JsonResponseBuilder.Error("NO_FACE", "Could not detect a face. Please try again.");
                    }

                    string encErr;
                    if (!dlib.TryEncodeFromBitmapWithLocation(bitmap, faceLoc, out faceEncoding, out encErr) || faceEncoding == null)
                    {
                        System.Diagnostics.Trace.TraceError("[IdentifyEmployee] Encoding failed: " + encErr);
                        return JsonResponseBuilder.Error("ENCODING_FAIL", "Could not process face.");
                    }
                }

                if (!FastFaceMatcher.IsInitialized)
                    FastFaceMatcher.Initialize();

                double identifyTol = ConfigurationService.GetDouble(
                    "Biometrics:AttendanceTolerance",
                    ConfigurationService.GetDouble("Biometrics:DlibTolerance", 0.60));

                var matchResult = FastFaceMatcher.FindBestMatch(faceEncoding, identifyTol);

                if (!matchResult.IsMatch)
                    return JsonResponseBuilder.Error("NOT_RECOGNIZED", "Face not recognized. Please check if you're enrolled.");

                using (var db = new FaceAttendDBEntities())
                {
                    var employee = db.Employees
                        .FirstOrDefault(e => e.EmployeeId == matchResult.Employee.EmployeeId);

                    if (employee == null)
                        return JsonResponseBuilder.NotFound("Employee");

                    var employeeStatus = employee.Status ?? "INACTIVE";
                    if (!string.Equals(employeeStatus, "ACTIVE", StringComparison.OrdinalIgnoreCase))
                        return JsonResponseBuilder.Error("INVALID_STATUS", "This employee is not active yet.");

                    var currentFingerprint = DeviceService.GenerateFingerprint(Request);
                    var currentToken = DeviceService.GetDeviceTokenFromCookie(Request);

                    var existingDevice = db.Devices
                        .FirstOrDefault(d => d.EmployeeId == employee.Id && d.Status == "ACTIVE");

                    bool isCurrentDeviceRegistered = existingDevice != null && (
                        existingDevice.Fingerprint == currentFingerprint ||
                        (!string.IsNullOrEmpty(currentToken) && existingDevice.DeviceToken == currentToken)
                    );

                    return JsonResponseBuilder.Success(new
                    {
                        employeeId = employee.EmployeeId,
                        employeeDbId = employee.Id,
                        fullName = $"{employee.FirstName} {employee.LastName}",
                        department = StringHelper.SanitizeDisplayText(employee.Department),
                        position = StringHelper.SanitizeDisplayText(employee.Position),
                        office = StringHelper.SanitizeDisplayText(employee.Office?.Name),
                        hasExistingDevice = existingDevice != null,
                        existingDeviceName = existingDevice?.DeviceName,
                        isCurrentDeviceRegistered = isCurrentDeviceRegistered,
                        confidence = matchResult.Confidence
                    });
                }
            }
            catch (Exception ex)
            {
                return JsonResponseBuilder.Error("SERVER_ERROR", ex.Message);
            }
        }
    }
}
