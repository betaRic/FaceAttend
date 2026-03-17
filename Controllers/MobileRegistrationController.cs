using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Mvc;
using FaceAttend.Services;
using FaceAttend.Services.Biometrics;

namespace FaceAttend.Controllers
{
    /// <summary>
    /// Mobile Device Registration Wizard
    /// Unified flow for new employee enrollment and existing employee device registration
    /// NOTE: This controller is NOT in the Admin area - it's accessible at root level
    /// </summary>
    public class MobileRegistrationController : Controller
    {
        #region Step 1: Entry Point - Employee Type Selection

        /// <summary>
        /// STEP 1: Show choice between New Employee and Existing Employee
        /// </summary>
        [HttpGet]
        public ActionResult Index()
        {
            // Only allow mobile devices
            if (!DeviceService.IsMobileDevice(Request))
            {
                return RedirectToRoute(new { controller = "Kiosk", action = "Index", area = "" });
            }

            ViewBag.DeviceFingerprint = DeviceService.GenerateFingerprint(Request);
            ViewBag.IsMobile = true;
            
            return View();
        }

        #endregion

        #region Step 2A: New Employee - Face Enrollment Wizard

        /// <summary>
        /// STEP 2A: New employee face enrollment wizard
        /// Similar to admin enrollment but simplified
        /// </summary>
        [HttpGet]
        public ActionResult Enroll()
        {
            if (!DeviceService.IsMobileDevice(Request))
                return RedirectToRoute(new { controller = "Kiosk", action = "Index", area = "" });

            using (var db = new FaceAttendDBEntities())
            {
                ViewBag.Offices = db.Offices.Where(o => o.IsActive).ToList();
                ViewBag.Fingerprint = DeviceService.GenerateFingerprint(Request);
                ViewBag.PerFrameThreshold = ConfigurationService.GetDouble("Biometrics:LivenessThreshold", 0.75);
            }
            
            return View();
        }

        /// <summary>
        /// AJAX: Real-time face detection during enrollment
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult ScanFrame()
        {
            try
            {
                var image = Request.Files["image"];
                if (image == null || image.ContentLength == 0)
                {
                    return JsonResponseBuilder.Error("NO_IMAGE");
                }

                // Save to temp file
                var tempPath = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), 
                    $"mobile_enroll_{Guid.NewGuid():N}.jpg");
                
                image.SaveAs(tempPath);

                try
                {
                    var dlib = new DlibBiometrics();
                    DlibBiometrics.FaceBox faceBox;
                    FaceRecognitionDotNet.Location faceLoc;
                    string detectErr;

                    // Detect face
                    if (!dlib.TryDetectSingleFaceFromFile(tempPath, out faceBox, out faceLoc, out detectErr))
                    {
                        return JsonResponseBuilder.Error("NO_FACE", detectErr);
                    }

                    // Liveness check
                    var liveness = new OnnxLiveness();
                    var scored = liveness.ScoreFromFile(tempPath, faceBox);
                    
                    // FIX: Use mobile-specific lower threshold since this is always a mobile flow
double livenessThreshold = ConfigurationService.GetDouble(
    "Biometrics:Liveness:MobileThreshold",
    ConfigurationService.GetDouble("Biometrics:LivenessThreshold", 0.30));
                    bool livenessOk = scored.Ok && (scored.Probability ?? 0) >= livenessThreshold;

                    // Encode face
                    double[] vec;
                    string encErr;
                    if (!dlib.TryEncodeFromFileWithLocation(tempPath, faceLoc, out vec, out encErr) || vec == null)
                    {
                        return JsonResponseBuilder.Error("ENCODING_FAIL", encErr);
                    }

                    // Check for duplicate face
                    // CRITICAL FIX: Use database query instead of cache to avoid stale data
                    var enrollStrictTolerance = ConfigurationService.GetDouble("Biometrics:EnrollmentStrictTolerance", 0.45);
                    string duplicateEmployeeId = null;
                    using (var checkDb = new FaceAttendDBEntities())
                    {
                        duplicateEmployeeId = DuplicateCheckHelper.FindDuplicate(checkDb, vec, null, enrollStrictTolerance);
                    }
                    var isMatch = !string.IsNullOrEmpty(duplicateEmployeeId);
                    
                    return JsonResponseBuilder.Success(new
                    {
                        liveness = scored.Probability ?? 0,
                        livenessOk = livenessOk,
                        count = 1,
                        isMatch = isMatch,
                        matchEmployee = duplicateEmployeeId,
                        encoding = Convert.ToBase64String(DlibBiometrics.EncodeToBytes(vec))
                    });
                }
                finally
                {
                    System.IO.File.Delete(tempPath);
                }
            }
            catch (Exception ex)
            {
                return JsonResponseBuilder.Error("SERVER_ERROR", ex.Message);
            }
        }

        /// <summary>
        /// Submit new employee enrollment with face
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult SubmitEnrollment(NewEmployeeEnrollmentVm vm)
        {
            try
            {
                // Sanitize all inputs first
                vm.Sanitize();

                // Server-side validation
                var validationErrors = vm.Validate();
                if (validationErrors.Count > 0)
                {
                    Response.StatusCode = 400;
                    Response.TrySkipIisCustomErrors = true;
                    return JsonResponseBuilder.Error("VALIDATION_ERROR", string.Join(" | ", validationErrors));
                }

                var fingerprint = DeviceService.GenerateFingerprint(Request);

                using (var db = new FaceAttendDBEntities())
                {
                    var normalizedEmployeeId = vm.EmployeeId.Trim().ToUpperInvariant();

                    // Check for duplicate employee ID (handle null status)
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

                    // CRITICAL FIX: Server-side duplicate face check
                    // Use database query directly to avoid cache staleness issues
                    // that could cause Employee B to be incorrectly matched to Employee A
                    var strictTolerance = ConfigurationService.GetDouble("Biometrics:EnrollmentStrictTolerance", 0.45);
                    
                    // Check for duplicate directly from database (bypass cache)
                    var duplicateEmployeeId = DuplicateCheckHelper.FindDuplicate(db, faceVector, normalizedEmployeeId, strictTolerance);
                    
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

                        FaceEncodingBase64 = Convert.ToBase64String(faceTemplate),
                        // Store all captured encodings for robust matching
                        FaceEncodingsJson = !string.IsNullOrWhiteSpace(vm.AllFaceEncodingsJson) 
                            ? vm.AllFaceEncodingsJson 
                            : null,

                        // self-enrollment flow
                        Status = "PENDING",

                        CreatedDate = DateTime.UtcNow,
                        EnrolledDate = DateTime.UtcNow,
                        LastModifiedDate = DateTime.UtcNow,
                        ModifiedBy = "SELF_ENROLLMENT"
                    };

                    db.Employees.Add(employee);
                    db.SaveChanges();

                    // FIX: Invalidate face index so new enrollment is recognized after approval
                    Services.Biometrics.EmployeeFaceIndex.Invalidate();

                    // FIX: Refresh in-memory face cache so the new employee is immediately recognizable
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
                        status = employee.Status
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

        #endregion

        #region Step 2B: Existing Employee - Face Identification

        /// <summary>
        /// STEP 2B: Existing employee face identification
        /// </summary>
        [HttpGet]
        public ActionResult Identify()
        {
            if (!DeviceService.IsMobileDevice(Request))
                return RedirectToRoute(new { controller = "Kiosk", action = "Index", area = "" });

            ViewBag.Fingerprint = DeviceService.GenerateFingerprint(Request);
            ViewBag.PerFrameThreshold = ConfigurationService.GetDouble("Biometrics:LivenessThreshold", 0.75);
            
            return View();
        }

        /// <summary>
        /// AJAX: Identify employee by face
        /// OPTIMIZED: Uses in-memory processing (no temp files) for speed
        /// NO LIVENESS: Only for identification - liveness runs at attendance time
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult IdentifyEmployee()
        {
            try
            {
                var image = Request.Files["image"];
                if (image == null || image.ContentLength == 0)
                {
                    return JsonResponseBuilder.Error("NO_IMAGE");
                }

                // OPTIMIZED: In-memory processing (no temp files)
                // Detection + Encoding ONLY (no liveness - that's for attendance time)
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
                    // DEBUG: Log image dimensions
                    System.Diagnostics.Trace.TraceInformation($"[IdentifyEmployee] Image dimensions: {bitmap.Width}x{bitmap.Height}");
                    
                    var dlib = new DlibBiometrics();
                    DlibBiometrics.FaceBox faceBox;
                    FaceRecognitionDotNet.Location faceLoc;
                    string detectErr;
                    
                    // Detect face
                    if (!dlib.TryDetectSingleFaceFromBitmap(bitmap, out faceBox, out faceLoc, out detectErr))
                    {
                        System.Diagnostics.Trace.TraceWarning($"[IdentifyEmployee] Face detection failed: {detectErr}");
                        return JsonResponseBuilder.Error("NO_FACE", "Could not detect a face. Please try again.");
                    }
                    
                    System.Diagnostics.Trace.TraceInformation($"[IdentifyEmployee] Face detected: {faceBox.Left},{faceBox.Top},{faceBox.Width},{faceBox.Height}");
                    
                    // Encode face (NO LIVENESS - that's for attendance time)
                    string encErr;
                    if (!dlib.TryEncodeFromBitmapWithLocation(bitmap, faceLoc, out faceEncoding, out encErr) || faceEncoding == null)
                    {
                        System.Diagnostics.Trace.TraceError($"[IdentifyEmployee] Encoding failed: {encErr}");
                        return JsonResponseBuilder.Error("ENCODING_FAIL", "Could not process face.");
                    }
                }

                // FIX: Ensure matcher is initialized before use
                if (!FastFaceMatcher.IsInitialized)
                    FastFaceMatcher.Initialize();
                
                // FIX: Read tolerance from config instead of hardcoded value
                double identifyTol = ConfigurationService.GetDouble(
                    "Biometrics:AttendanceTolerance",
                    ConfigurationService.GetDouble("Biometrics:DlibTolerance", 0.60));
                
                var matchResult = FastFaceMatcher.FindBestMatch(faceEncoding, identifyTol);

                if (!matchResult.IsMatch)
                {
                    return JsonResponseBuilder.Error("NOT_RECOGNIZED", "Face not recognized. Please check if you're enrolled.");
                }

                // Get full employee details
                using (var db = new FaceAttendDBEntities())
                {
                    var employeeId = matchResult.Employee.EmployeeId;
                    var employee = db.Employees
                        .FirstOrDefault(e => e.EmployeeId == employeeId);

                    if (employee == null)
                    {
                        return JsonResponseBuilder.NotFound("Employee");
                    }

                    // IMPORTANT: Only allow ACTIVE employees to identify themselves
                    var employeeStatus = employee.Status ?? "INACTIVE";
                    if (!string.Equals(employeeStatus, "ACTIVE", StringComparison.OrdinalIgnoreCase))
                    {
                        return JsonResponseBuilder.Error("INVALID_STATUS", "This employee is not active yet.");
                    }

                    var currentFingerprint = DeviceService.GenerateFingerprint(Request);
                    var currentToken = DeviceService.GetDeviceTokenFromCookie(Request);

                    // Check if already has device registered
                    var existingDevice = db.Devices
                        .FirstOrDefault(d => d.EmployeeId == employee.Id && d.Status == "ACTIVE");

                    // Is THIS specific device already registered to this employee?
                    bool isCurrentDeviceRegistered = existingDevice != null && (
                        existingDevice.Fingerprint == currentFingerprint ||
                        (!string.IsNullOrEmpty(currentToken) && existingDevice.DeviceToken == currentToken)
                    );

                    return JsonResponseBuilder.Success(new
                    {
                        employeeId = employee.EmployeeId,
                        employeeDbId = employee.Id,  // IMPORTANT: needed for device registration
                        fullName = $"{employee.FirstName} {employee.LastName}",
                        department = SanitizeDisplayText(employee.Department),
                        position = SanitizeDisplayText(employee.Position),
                        office = SanitizeDisplayText(employee.Office?.Name),
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

        #endregion

        #region Step 3: Device Registration

        /// <summary>
        /// STEP 3: Device registration form
        /// </summary>
        [HttpGet]
        public ActionResult Device(string employeeId, bool isNewEmployee = false, int? employeeDbId = null)
        {
            if (!DeviceService.IsMobileDevice(Request))
                return RedirectToRoute(new { controller = "Kiosk", action = "Index", area = "" });

            using (var db = new FaceAttendDBEntities())
            {
                var vm = new DeviceRegistrationVm
                {
                    EmployeeId = employeeId,
                    IsNewEmployee = isNewEmployee,
                    EmployeeDbId = employeeDbId,
                    DeviceName = GetDefaultDeviceName(),
                    Fingerprint = DeviceService.GenerateFingerprint(Request)
                };

                if (!isNewEmployee)
                {
                    var employee = db.Employees.FirstOrDefault(e => e.EmployeeId == employeeId);
                    if (employee != null)
                    {
                        vm.EmployeeFullName = $"{employee.FirstName} {employee.LastName}";
                        // FIX: Sanitize department to remove invalid characters that can't render
                        vm.Department = SanitizeDisplayText(employee.Department);

                        var existingDevice = db.Devices
                            .FirstOrDefault(d => d.EmployeeId == employee.Id && d.Status == "ACTIVE");

                        if (existingDevice != null)
                        {
                            vm.HasExistingDevice = true;
                            vm.ExistingDeviceName = existingDevice.DeviceName;
                        }
                    }
                }

                return View(vm);
            }
        }

        /// <summary>
        /// Submit device registration
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult RegisterDevice(DeviceRegistrationVm vm)
        {
            // Sanitize all inputs first
            vm.Sanitize();

            // Server-side validation
            var validationErrors = vm.Validate();
            if (validationErrors.Count > 0)
            {
                Response.StatusCode = 400;
                Response.TrySkipIisCustomErrors = true;
                return JsonResponseBuilder.Error("VALIDATION_ERROR", string.Join(" | ", validationErrors));
            }

            var fingerprint = DeviceService.GenerateFingerprint(Request);
            var existingToken = DeviceService.GetDeviceTokenFromCookie(Request);

            using (var db = new FaceAttendDBEntities())
            {
                if (vm.IsNewEmployee && vm.EmployeeDbId.HasValue)
                {
                    var employee = db.Employees.Find(vm.EmployeeDbId.Value);
                    if (employee == null)
                    {
                        return JsonResponseBuilder.NotFound("Enrollment");
                    }

                    var status = DeviceService.GetEmployeeStatus(db, employee.Id);
                    if (!string.Equals(status, "PENDING", StringComparison.OrdinalIgnoreCase))
                    {
                        return JsonResponseBuilder.Error("INVALID_STATUS", "This enrollment is no longer pending.");
                    }

                    DeviceService.CreatePendingDevice(db, employee.Id, fingerprint, vm.DeviceName, Request.UserHostAddress);
                    
                    // Generate and set device token for persistent identification
                    var deviceToken = DeviceService.GenerateDeviceToken();
                    DeviceService.SetDeviceTokenCookie(Response, deviceToken, Request.IsSecureConnection);

                    return JsonResponseBuilder.Success(new
                    {
                        isNewEmployee = true,
                        employeeDbId = employee.Id,
                        deviceToken = deviceToken
                    }, "Registration complete! An admin will approve your enrollment.");
                }
                else
                {
                    var employee = db.Employees.FirstOrDefault(e => e.EmployeeId == vm.EmployeeId);
                    if (employee == null)
                    {
                        return JsonResponseBuilder.NotFound("Employee");
                    }

                    if (!string.Equals(employee.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase))
                    {
                        return JsonResponseBuilder.Error("INVALID_STATUS", "Only active employees can register a device.");
                    }

                    var existingDevices = db.Devices
                        .Where(d => d.EmployeeId == employee.Id && d.Status == "ACTIVE")
                        .ToList();

                    foreach (var oldDevice in existingDevices)
                    {
                        oldDevice.Status = "REPLACED";
                    }

                    var result = DeviceService.RegisterDevice(
                        db,
                        employee.Id,
                        fingerprint,
                        vm.DeviceName,
                        Request.UserHostAddress,
                        existingToken); // Pass existing token if available

                    if (!result.Success)
                    {
                        return JsonResponseBuilder.Error(result.ErrorCode, result.Message);
                    }

                    // Approve the new device
                    var approveResult = DeviceService.ApproveDevice(db, result.Data, -1);
                    if (!approveResult.Success)
                    {
                        #if DEBUG

                        #endif
                    }
                    else
                    {
                        #if DEBUG

                        #endif
                    }
                    
                    // Force save to ensure status is persisted
                    db.SaveChanges();
                    
                    // Set or refresh device token cookie
                    dynamic extraData = result.ExtraData;
                    var returnedToken = extraData?.DeviceToken ?? existingToken ?? DeviceService.GenerateDeviceToken();
                    DeviceService.SetDeviceTokenCookie(Response, returnedToken, Request.IsSecureConnection);
                    
                    // Debug logging removed - use proper logging framework if needed

                    return JsonResponseBuilder.Success(new
                    {
                        isNewEmployee = false,
                        deviceToken = returnedToken
                    }, "Device registered successfully! You can now use Face Attendance.");
                }
            }
        }

        #endregion

        #region Step 4: Completion

        /// <summary>
        /// Success page after registration
        /// </summary>
        [HttpGet]
        public ActionResult Success(bool isNewEmployee = false, int? employeeDbId = null)
        {
            ViewBag.IsNewEmployee = isNewEmployee;
            ViewBag.EmployeeDbId = employeeDbId;
            return View();
        }

        /// <summary>
        /// Check enrollment status (for new employees waiting for approval)
        /// </summary>
        [HttpGet]
        public ActionResult CheckStatus(int employeeDbId)
        {
            using (var db = new FaceAttendDBEntities())
            {
                var employee = db.Employees.Find(employeeDbId);
                if (employee == null)
                {
                    return JsonResponseBuilder.NotFound("Enrollment");
                }

                var status = DeviceService.GetEmployeeStatus(db, employee.Id);
                string message = status == "PENDING" ? "Waiting for admin approval..." :
                                status == "ACTIVE" ? "Approved! You can now use the system." :
                                "This enrollment is inactive. Please contact the admin.";

                return JsonResponseBuilder.Success(new
                {
                    status = status,
                    isApproved = status == "ACTIVE",
                    message
                });
            }
        }

        #endregion

        #region Helper Methods

        private string GetDefaultDeviceName()
        {
            var ua = Request.UserAgent ?? "";
            
            if (ua.Contains("iPhone"))
                return "iPhone";
            if (ua.Contains("iPad"))
                return "iPad";
            if (ua.Contains("Android"))
                return "Android Phone";
            if (ua.Contains("Windows Phone"))
                return "Windows Phone";
            
            return "Mobile Device";
        }

        /// <summary>
        /// Sanitizes text for display by removing characters that can't render properly
        /// </summary>
        private string SanitizeDisplayText(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return input;
            
            // First decode any HTML entities if present
            var decoded = System.Net.WebUtility.HtmlDecode(input);
            
            // Replace common problematic Unicode characters with safe alternatives
            var result = decoded
                // Replace various Unicode dashes/hyphens with standard hyphen
                .Replace("\u2013", "-")  // En dash
                .Replace("\u2014", "-")  // Em dash
                .Replace("\u2212", "-")  // Minus sign
                // Replace fancy quotes with standard quotes
                .Replace("\u2018", "'")   // Left single quote
                .Replace("\u2019", "'")   // Right single quote
                .Replace("\u201C", "\"")  // Left double quote
                .Replace("\u201D", "\"")  // Right double quote
                // Replace other problematic chars
                .Replace("\u2026", "...") // Ellipsis
                .Replace("\u00A0", " ")  // Non-breaking space
                // Remove control characters
                .Replace("\u0000", "")   // Null character
                .Replace("\uFFFD", "");  // Replacement character
            
            // Use Regex to remove any non-printable or corrupted characters
            // Allow: letters, numbers, spaces, common punctuation (no quotes)
            result = System.Text.RegularExpressions.Regex.Replace(result, @"[^\p{L}\p{N}\s\-\.\/\\\(\)\,\&\#\@\']", "-");
            
            // Clean up multiple dashes
            result = System.Text.RegularExpressions.Regex.Replace(result, @"-+", "-");
            
            return result.Trim('-', ' ');
        }

        #endregion

        #region Employee Portal (Post-Attendance)

        /// <summary>
        /// EMPLOYEE PORTAL: Mobile-only page for employees to view their attendance summary
        /// Accessed after successful attendance scan or via direct link
        /// Requires active registered device - NO query string employee ID
        /// </summary>
        [HttpGet]
        public ActionResult Employee()
        {
            // STRICT: Mobile only - phones only, not tablets, not desktop
            if (!DeviceService.IsMobileDevice(Request))
            {
                return RedirectToRoute(new { controller = "Kiosk", action = "Index", area = "" });
            }
            
            // Additional check: detect tablets and redirect them to kiosk
            var ua = Request.UserAgent ?? "";
            if (ua.ToLowerInvariant().Contains("ipad") || 
                (ua.ToLowerInvariant().Contains("android") && !ua.ToLowerInvariant().Contains("mobile")))
            {
                return RedirectToRoute(new { controller = "Kiosk", action = "Index", area = "" });
            }

            // Resolve employee from active registered device
            // SECURITY: Never trust query string - always resolve from device
            var fingerprint = DeviceService.GenerateFingerprint(Request);
            var deviceToken = DeviceService.GetDeviceTokenFromCookie(Request);
            
            // DEBUG logging
            #if DEBUG


            #endif
            
            using (var db = new FaceAttendDBEntities())
            {
                Device device = null;
                
                // Try device token first
                if (!string.IsNullOrEmpty(deviceToken))
                {
                    device = db.Devices
                        .Include("Employee")
                        .Include("Employee.Office")
                        .FirstOrDefault(d => d.DeviceToken == deviceToken && d.Status == "ACTIVE");
                    #if DEBUG

                    #endif
                }
                
                // Fallback to fingerprint
                if (device == null)
                {
                    device = db.Devices
                        .Include("Employee")
                        .Include("Employee.Office")
                        .FirstOrDefault(d => d.Fingerprint == fingerprint && d.Status == "ACTIVE");
                    #if DEBUG

                    #endif
                }
                
                if (device == null)
                {
                    // SECURITY FIX: Removed auto-approval of PENDING devices
                    // Removed auto-reactivation of REPLACED devices
                    // Device state must be managed explicitly by admin
                    
                    // List all devices for debugging
                    var allDevices = db.Devices.Select(d => new { d.Id, d.Status, d.Fingerprint }).ToList();
                    #if DEBUG

                    #endif
                    foreach (var d in allDevices)
                    {
                        #if DEBUG

                        #endif
                    }
                    
                    // No registered device - show error message with option to identify
                    #if DEBUG

                    #endif
                    ViewBag.ErrorMessage = "Your device could not be recognized. Please identify yourself to continue.";
                    ViewBag.Fingerprint = fingerprint;
                    return View("DeviceNotRecognized");
                }
                
                var employee = device.Employee;
                if (employee == null || employee.Status != "ACTIVE")
                {
                    return RedirectToAction("Identify");
                }
                
                // Get today's attendance (FIX: Use local timezone for correct date boundary)
                var todayLocal = TimeZoneHelper.TodayLocalDate();
                var todayRange = TimeZoneHelper.LocalDateToUtcRange(todayLocal);
                var todayLogs = db.AttendanceLogs
                    .Where(l => l.EmployeeId == employee.Id && 
                                l.Timestamp >= todayRange.fromUtc &&
                                l.Timestamp < todayRange.toUtcExclusive)
                    .OrderBy(l => l.Timestamp)
                    .ToList();

                // Get this month's attendance summary (timestamps are now local time)
                var firstDayOfMonth = new DateTime(todayLocal.Year, todayLocal.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
                var firstDayOfNextMonth = firstDayOfMonth.AddMonths(1);

                var monthLogs = db.AttendanceLogs
                    .Where(l => l.EmployeeId == employee.Id &&
                                l.Timestamp >= firstDayOfMonth &&
                                l.Timestamp < firstDayOfNextMonth)
                    .OrderBy(l => l.Timestamp)
                    .ToList();

                // Calculate statistics (timestamps are now local time)
                var totalDaysPresent = monthLogs
                    .Where(l => l.EventType == "IN")
                    .Select(l => l.Timestamp.Date)
                    .Distinct()
                    .Count();

                // Note: Duration calculation requires pairing IN/OUT events
                // For now, estimate based on typical 8-hour workday
                var totalEstimatedHours = totalDaysPresent * 8.0;

                var lastAttendance = todayLogs.LastOrDefault();

                // Get recent entries (last 10)
                var recentLogs = monthLogs
                    .OrderByDescending(l => l.Timestamp)
                    .Take(10)
                    .OrderBy(l => l.Timestamp)
                    .Select(l => new RecentAttendanceVm
                    {
                        Date = l.Timestamp.ToString("MMM dd"),
                        Time = l.Timestamp.ToString("h:mm tt"),
                        Type = l.EventType,
                        Office = l.Office != null ? l.Office.Name : "Unknown"
                    })
                    .ToList();

                // Build monthly attendance report (day-by-day Time In/Out)
                var monthlyReport = BuildMonthlyAttendanceReport(monthLogs, todayLocal);

                var vm = new EmployeePortalVm
                {
                    EmployeeId = employee.EmployeeId,
                    FullName = $"{employee.FirstName} {employee.LastName}",
                    Position = SanitizeDisplayText(employee.Position),
                    Department = SanitizeDisplayText(employee.Department),
                    OfficeName = SanitizeDisplayText(employee.Office?.Name),
                    DeviceName = device.DeviceName,

                    // Today's status
                    TodayStatus = lastAttendance?.EventType == "IN" ? "Timed In" :
                                  lastAttendance?.EventType == "OUT" ? "Timed Out" : "Not Yet",
                    LastScanTime = lastAttendance != null
                        ? lastAttendance.Timestamp.ToString("h:mm tt")
                        : null,

                    // Monthly stats
                    TotalDaysPresent = totalDaysPresent,
                    TotalHours = Math.Round(totalEstimatedHours, 1),
                    AverageHoursPerDay = totalDaysPresent > 0
                        ? Math.Round(totalEstimatedHours / totalDaysPresent, 1)
                        : 0,

                    // Recent entries
                    RecentEntries = recentLogs,

                    // Monthly day-by-day report
                    MonthlyReport = monthlyReport,

                    // For CSV export
                    CurrentMonth = todayLocal.ToString("yyyy_MM"),
                    CurrentMonthDisplay = todayLocal.ToString("MMMM yyyy")
                };

                return View(vm);
            }
        }

        /// <summary>
        /// Builds monthly attendance report with day-by-day Time In/Out
        /// </summary>
        private List<DailyAttendanceVm> BuildMonthlyAttendanceReport(List<AttendanceLog> monthLogs, DateTime todayLocal)
        {
            var report = new List<DailyAttendanceVm>();
            var currentMonth = todayLocal.Month;
            var currentYear = todayLocal.Year;
            var daysInMonth = DateTime.DaysInMonth(currentYear, currentMonth);

            // Group logs by date (timestamps are now local time)
            var logsByDate = monthLogs
                .GroupBy(l => l.Timestamp.Date)
                .ToDictionary(g => g.Key, g => g.OrderBy(l => l.Timestamp).ToList());

            for (int day = 1; day <= daysInMonth; day++)
            {
                var date = new DateTime(currentYear, currentMonth, day);
                var isWeekend = date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday;
                var isFuture = date > todayLocal;

                var dayRecord = new DailyAttendanceVm
                {
                    Date = date,
                    DayOfWeek = date.ToString("ddd"),
                    DateDisplay = date.ToString("MMM dd"),
                    IsWeekend = isWeekend,
                    Status = isFuture ? "-" : (isWeekend ? "Weekend" : "Absent")
                };

                // Check if we have logs for this date
                if (logsByDate.TryGetValue(date, out var dayLogs))
                {
                    var timeIn = dayLogs.FirstOrDefault(l => l.EventType == "IN");
                    var timeOut = dayLogs.LastOrDefault(l => l.EventType == "OUT");

                    if (timeIn != null)
                    {
                        dayRecord.TimeIn = timeIn.Timestamp.ToString("h:mm tt");
                        dayRecord.Office = timeIn.Office?.Name;
                    }

                    if (timeOut != null && timeOut != timeIn)
                    {
                        dayRecord.TimeOut = timeOut.Timestamp.ToString("h:mm tt");
                    }

                    // Calculate hours worked
                    if (timeIn != null && timeOut != null && timeOut != timeIn)
                    {
                        var duration = timeOut.Timestamp - timeIn.Timestamp;
                        dayRecord.HoursWorked = Math.Round(duration.TotalHours, 2);
                        dayRecord.Status = "Present";
                    }
                    else if (timeIn != null)
                    {
                        dayRecord.Status = date.Date == todayLocal.Date ? "In Progress" : "Incomplete";
                    }
                }

                report.Add(dayRecord);
            }

            return report.OrderByDescending(r => r.Date).ToList();
        }
        
        /// <summary>
        /// Export monthly attendance as CSV for Excel
        /// </summary>
        [HttpGet]
        public ActionResult ExportAttendance()
        {
            // Mobile only
            if (!DeviceService.IsMobileDevice(Request))
            {
                return RedirectToRoute(new { controller = "Kiosk", action = "Index", area = "" });
            }
            
            // Resolve employee from device
            var fingerprint = DeviceService.GenerateFingerprint(Request);
            var deviceToken = DeviceService.GetDeviceTokenFromCookie(Request);
            
            using (var db = new FaceAttendDBEntities())
            {
                Device device = null;
                
                if (!string.IsNullOrEmpty(deviceToken))
                {
                    device = db.Devices
                        .Include("Employee")
                        .FirstOrDefault(d => d.DeviceToken == deviceToken && d.Status == "ACTIVE");
                }
                
                if (device == null)
                {
                    device = db.Devices
                        .Include("Employee")
                        .FirstOrDefault(d => d.Fingerprint == fingerprint && d.Status == "ACTIVE");
                }
                
                if (device?.Employee == null)
                {
                    return RedirectToAction("Identify");
                }
                
                var employee = device.Employee;
                
                // Get selected month or current month
                var monthParam = Request.QueryString["month"];
                DateTime targetMonth;
                if (!DateTime.TryParseExact(monthParam, "yyyy_MM", null, System.Globalization.DateTimeStyles.None, out targetMonth))
                {
                    targetMonth = TimeZoneHelper.NowLocal();
                }
                
                var firstDay = new DateTime(targetMonth.Year, targetMonth.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
                var lastDay = firstDay.AddMonths(1);
                
                var logs = db.AttendanceLogs
                    .Where(l => l.EmployeeId == employee.Id && 
                                l.Timestamp >= firstDay && 
                                l.Timestamp < lastDay)
                    .OrderBy(l => l.Timestamp)
                    .Select(l => new
                    {
                        l.Timestamp,
                        l.EventType,
                        l.ClientIP,
                        DurationMinutes = (int?)null, // Calculate if needed
                        OfficeName = l.Office.Name
                    })
                    .ToList();
                
                // Build CSV
                var csv = new System.Text.StringBuilder();
                csv.AppendLine("Employee ID,Full Name,Position,Department");
                csv.AppendLine($"\"{employee.EmployeeId}\",\"{employee.FirstName} {employee.LastName}\",\"{employee.Position}\",\"{employee.Department}\"");
                csv.AppendLine();
                csv.AppendLine($"Attendance Report for {targetMonth.ToString("MMMM yyyy")}");
                csv.AppendLine();
                csv.AppendLine("Date,Time,Event Type,Office,Duration (minutes)");
                
                foreach (var log in logs)
                {
                    // Timestamps are now stored in local time
                    csv.AppendLine($"\"{log.Timestamp:yyyy-MM-dd}\",\"{log.Timestamp:HH:mm:ss}\",\"{log.EventType}\",\"{log.OfficeName}\",\"-\"");
                }
                
                csv.AppendLine();
                csv.AppendLine($"Total Entries: {logs.Count}");
                csv.AppendLine($"Total Days Present: {logs.Where(l => l.EventType == "IN").Select(l => l.Timestamp.Date).Distinct().Count()}");
                csv.AppendLine($"Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
                
                var fileName = $"attendance_{employee.EmployeeId}_{targetMonth:yyyy_MM}.csv";
                var bytes = System.Text.Encoding.UTF8.GetBytes(csv.ToString());
                
                return File(bytes, "text/csv", fileName);
            }
        }

        #endregion

        // NOTE: FindDuplicateEmployeeInDatabase removed - use DuplicateCheckHelper.FindDuplicate instead
        // This helper is now shared in Services/Biometrics/DuplicateCheckHelper.cs
    }

    #region View Models

    /// <summary>
    /// Employee Portal View Model
    /// </summary>
    public class EmployeePortalVm
    {
        public string EmployeeId { get; set; }
        public string FullName { get; set; }
        public string Position { get; set; }
        public string Department { get; set; }
        public string OfficeName { get; set; }
        public string DeviceName { get; set; }
        
        // Today's status
        public string TodayStatus { get; set; }
        public string LastScanTime { get; set; }
        
        // Monthly stats
        public int TotalDaysPresent { get; set; }
        public double TotalHours { get; set; }
        public double AverageHoursPerDay { get; set; }
        
        // Recent entries
        public List<RecentAttendanceVm> RecentEntries { get; set; }

        // Monthly day-by-day report
        public List<DailyAttendanceVm> MonthlyReport { get; set; }
        
        // For CSV export
        public string CurrentMonth { get; set; }
        public string CurrentMonthDisplay { get; set; }
    }
    
    public class RecentAttendanceVm
    {
        public string Date { get; set; }
        public string Time { get; set; }
        public string Type { get; set; }
        public string Office { get; set; }
    }

    /// <summary>
    /// Daily attendance record for monthly report
    /// </summary>
    public class DailyAttendanceVm
    {
        public DateTime Date { get; set; }
        public string DayOfWeek { get; set; }
        public string DateDisplay { get; set; }
        public string TimeIn { get; set; }
        public string TimeOut { get; set; }
        public double? HoursWorked { get; set; }
        public string Status { get; set; } // Present, Absent, Weekend, Holiday
        public bool IsWeekend { get; set; }
        public string Office { get; set; }
    }

    public class NewEmployeeEnrollmentVm
    {
        public string EmployeeId { get; set; }
        public string FirstName { get; set; }
        public string MiddleName { get; set; }
        public string LastName { get; set; }
        public string Position { get; set; }
        public string Department { get; set; }
        public int OfficeId { get; set; }
        public string FaceEncoding { get; set; }
        public string AllFaceEncodingsJson { get; set; }  // JSON array of all captured encodings
        public string DeviceName { get; set; }

        /// <summary>
        /// Validates the enrollment data on the server side
        /// </summary>
        public System.Collections.Generic.List<string> Validate()
        {
            var errors = new System.Collections.Generic.List<string>();

            // Employee ID validation
            if (string.IsNullOrWhiteSpace(EmployeeId))
                errors.Add("Employee ID is required");
            else if (EmployeeId.Length < 5 || EmployeeId.Length > 20)
                errors.Add("Employee ID must be 5-20 characters");
            else if (!System.Text.RegularExpressions.Regex.IsMatch(EmployeeId, @"^[A-Z0-9 -]+$"))
                errors.Add("Employee ID contains invalid characters");

            // First Name validation
            if (string.IsNullOrWhiteSpace(FirstName))
                errors.Add("First Name is required");
            else if (FirstName.Length < 2 || FirstName.Length > 50)
                errors.Add("First Name must be 2-50 characters");
            else if (!System.Text.RegularExpressions.Regex.IsMatch(FirstName, @"^[A-Z .\-']+$"))
                errors.Add("First Name contains invalid characters");

            // Last Name validation
            if (string.IsNullOrWhiteSpace(LastName))
                errors.Add("Last Name is required");
            else if (LastName.Length < 2 || LastName.Length > 50)
                errors.Add("Last Name must be 2-50 characters");
            else if (!System.Text.RegularExpressions.Regex.IsMatch(LastName, @"^[A-Z .\-']+$"))
                errors.Add("Last Name contains invalid characters");

            // Middle Name validation (optional)
            if (!string.IsNullOrWhiteSpace(MiddleName))
            {
                if (MiddleName.Length > 50)
                    errors.Add("Middle Name must be 50 characters or less");
                else if (!System.Text.RegularExpressions.Regex.IsMatch(MiddleName, @"^[A-Z .\-']*$"))
                    errors.Add("Middle Name contains invalid characters");
            }

            // Position validation
            if (string.IsNullOrWhiteSpace(Position))
                errors.Add("Position is required");
            else if (Position.Length < 2 || Position.Length > 100)
                errors.Add("Position must be 2-100 characters");
            else if (!System.Text.RegularExpressions.Regex.IsMatch(Position, @"^[A-Z0-9 .\-/(),]+$"))
                errors.Add("Position contains invalid characters");

            // Department validation
            if (string.IsNullOrWhiteSpace(Department))
                errors.Add("Department is required");
            else if (Department.Length < 2 || Department.Length > 100)
                errors.Add("Department must be 2-100 characters");
            else if (!System.Text.RegularExpressions.Regex.IsMatch(Department, @"^[A-Z0-9 .\-/(),]+$"))
                errors.Add("Department contains invalid characters");

            // Office ID validation
            if (OfficeId <= 0)
                errors.Add("Office is required");

            // Device Name validation
            if (string.IsNullOrWhiteSpace(DeviceName))
                errors.Add("Device Name is required");
            else if (DeviceName.Length < 2 || DeviceName.Length > 50)
                errors.Add("Device Name must be 2-50 characters");
            else if (!System.Text.RegularExpressions.Regex.IsMatch(DeviceName, @"^[A-Z0-9 ._@]+$"))
                errors.Add("Device Name contains invalid characters");

            // Face Encoding validation
            if (string.IsNullOrWhiteSpace(FaceEncoding))
                errors.Add("Face enrollment is required");

            return errors;
        }

        /// <summary>
        /// Sanitizes all string inputs to prevent XSS
        /// </summary>
        public void Sanitize()
        {
            EmployeeId = SanitizeInput(EmployeeId)?.ToUpperInvariant();
            FirstName = SanitizeInput(FirstName)?.ToUpperInvariant();
            MiddleName = string.IsNullOrWhiteSpace(MiddleName) ? null : SanitizeInput(MiddleName)?.ToUpperInvariant();
            LastName = SanitizeInput(LastName)?.ToUpperInvariant();
            Position = SanitizeInput(Position)?.ToUpperInvariant();
            Department = SanitizeInput(Department)?.ToUpperInvariant();
            DeviceName = SanitizeInput(DeviceName)?.ToUpperInvariant();
        }

        private string SanitizeInput(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return input;

            // Remove HTML tags
            input = System.Text.RegularExpressions.Regex.Replace(input, @"<[^>]+>", string.Empty);
            // Remove script tags and content
            input = System.Text.RegularExpressions.Regex.Replace(input, @"<script[^>]*>[\s\S]*?</script>", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            // Remove javascript protocol
            input = System.Text.RegularExpressions.Regex.Replace(input, @"javascript:", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            // Remove event handlers
            input = System.Text.RegularExpressions.Regex.Replace(input, @"on\w+\s*=", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            // Trim whitespace
            return input.Trim();
        }
    }

    public class DeviceRegistrationVm
    {
        public string EmployeeId { get; set; }
        public string EmployeeFullName { get; set; }
        public string Department { get; set; }
        public string Position { get; set; }
        public bool IsNewEmployee { get; set; }
        public int? EmployeeDbId { get; set; }
        public string DeviceName { get; set; }
        public string Fingerprint { get; set; }
        public bool HasExistingDevice { get; set; }
        public string ExistingDeviceName { get; set; }

        /// <summary>
        /// Validates the device registration data
        /// </summary>
        public System.Collections.Generic.List<string> Validate()
        {
            var errors = new System.Collections.Generic.List<string>();

            // Device Name validation
            if (string.IsNullOrWhiteSpace(DeviceName))
                errors.Add("Device Name is required");
            else if (DeviceName.Length < 2 || DeviceName.Length > 50)
                errors.Add("Device Name must be 2-50 characters");
            else if (!System.Text.RegularExpressions.Regex.IsMatch(DeviceName, @"^[A-Z0-9\s\.\-_@]+$"))
                errors.Add("Device Name contains invalid characters");

            return errors;
        }

        /// <summary>
        /// Sanitizes all string inputs to prevent XSS
        /// </summary>
        public void Sanitize()
        {
            EmployeeId = SanitizeInput(EmployeeId)?.ToUpperInvariant();
            DeviceName = SanitizeInput(DeviceName)?.ToUpperInvariant();
            EmployeeFullName = SanitizeInput(EmployeeFullName);
            Department = SanitizeInput(Department);
            Position = SanitizeInput(Position);
            ExistingDeviceName = SanitizeInput(ExistingDeviceName);
        }

        private string SanitizeInput(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return input;

            // Remove HTML tags
            input = System.Text.RegularExpressions.Regex.Replace(input, @"<[^>]+>", string.Empty);
            // Remove script tags and content
            input = System.Text.RegularExpressions.Regex.Replace(input, @"<script[^>]*>[\s\S]*?</script>", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            // Remove javascript protocol
            input = System.Text.RegularExpressions.Regex.Replace(input, @"javascript:", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            // Remove event handlers
            input = System.Text.RegularExpressions.Regex.Replace(input, @"on\w+\s*=", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            // Trim whitespace
            return input.Trim();
        }
    }

    #endregion
}
