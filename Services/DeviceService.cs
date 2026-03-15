using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using FaceAttend.Services.Biometrics;

namespace FaceAttend.Services
{
    /// <summary>
    /// Device registration and pending employee enrollment service.
    /// 
    /// REFACTORED: Now uses unified OperationResult<T> class instead of multiple 
    /// similar result classes (DeviceRegistrationResult, DeviceValidationResult, etc.)
    /// 
    /// MERGED: Contains KioskSessionService functionality (session binding, device fingerprinting).
    /// </summary>
    public static class DeviceService
    {
        #region Session Management (Merged from KioskSessionService)

        /// <summary>
        /// Gets a session binding key for visitor scans to prevent session hijacking.
        /// </summary>
        public static string GetVisitorSessionBinding(HttpContextBase httpContext)
        {
            if (httpContext == null || httpContext.Session == null)
                return "";

            return httpContext.Session.SessionID ?? "";
        }

        /// <summary>
        /// Generates a short device fingerprint for anti-spoofing (16 chars).
        /// For full fingerprint, use GenerateFingerprint(HttpRequestBase).
        /// </summary>
        public static string GetShortDeviceFingerprint(HttpContextBase httpContext)
        {
            if (httpContext?.Request == null) return "";

            var ua = httpContext.Request.UserAgent ?? "";
            var accept = httpContext.Request.Headers["Accept"] ?? "";
            var acceptLang = httpContext.Request.Headers["Accept-Language"] ?? "";

            // Simple hash of browser characteristics
            var raw = ua + "|" + accept + "|" + acceptLang + "|" + httpContext.Request.Browser.Platform;
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                return Convert.ToBase64String(hash).Substring(0, 16); // Short fingerprint
            }
        }

        #endregion

        #region Device Fingerprint
        
        /// <summary>
        /// Generate unique device fingerprint from request
        /// </summary>
        public static string GenerateFingerprint(HttpRequestBase request)
        {
            // Combine stable device characteristics
            var components = new[]
            {
                GetStableUserAgent(request.UserAgent),
                request.Headers["Accept-Language"] ?? "unknown",
                request.Browser?.Platform ?? "unknown",
                GetScreenInfo(request)
            };
            
            var combined = string.Join("|", components);
            
            using (var sha256 = SHA256.Create())
            {
                var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(combined));
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant().Substring(0, 32);
            }
        }
        
        private static string GetStableUserAgent(string userAgent)
        {
            if (string.IsNullOrWhiteSpace(userAgent))
                return "unknown";
            
            // Remove version numbers for stability
            var cleaned = System.Text.RegularExpressions.Regex.Replace(
                userAgent.ToLowerInvariant(),
                @"(chrome|firefox|safari|edge|version|android|ios)/[\d.]+", 
                "$1");
            
            return cleaned;
        }
        
        private static string GetScreenInfo(HttpRequestBase request)
        {
            var width = request.Headers["X-Screen-Width"] ?? "0";
            var height = request.Headers["X-Screen-Height"] ?? "0";
            
            if (width == "0" || height == "0")
            {
                return $"{request.Browser?.ScreenPixelsWidth ?? 0}x{request.Browser?.ScreenPixelsHeight ?? 0}";
            }
            
            return $"{width}x{height}";
        }
        
        /// <summary>
        /// Check if device is a mobile device (BYOD) vs desktop/kiosk
        /// </summary>
        public static bool IsMobileDevice(HttpRequestBase request)
        {
            var userAgent = request.UserAgent?.ToLowerInvariant() ?? "";
            
            // STRICT mobile detection - only true phones
            // Tablets and laptops should be treated as kiosks
            
            // iPhone (not iPad)
            if (userAgent.Contains("iphone"))
                return true;
            
            // Android phones (exclude tablets: Android without Tablet/Smart-TV)
            if (userAgent.Contains("android"))
            {
                // Exclude Android tablets and TVs
                if (userAgent.Contains("tablet") || 
                    userAgent.Contains("smart-tv") ||
                    userAgent.Contains("tv") ||
                    userAgent.Contains("android tv"))
                    return false;
                
                // Check for mobile flag or screen size hint
                if (userAgent.Contains("mobile"))
                    return true;
                
                // Large screen Android without mobile flag = tablet
                return false;
            }
            
            // Windows Phone, BlackBerry, etc.
            if (userAgent.Contains("windows phone") ||
                userAgent.Contains("iemobile") ||
                userAgent.Contains("blackberry"))
                return true;
            
            return false;
        }
        
        #endregion
        
        #region Device Token (Persistent Identification)
        
        private const string DeviceTokenCookieName = "FaceAttend_DeviceToken";
        private const int DeviceTokenExpiryDays = 365; // 1 year
        
        /// <summary>
        /// Generate a unique device token for persistent identification
        /// </summary>
        public static string GenerateDeviceToken()
        {
            using (var rng = new RNGCryptoServiceProvider())
            {
                var tokenBytes = new byte[32];
                rng.GetBytes(tokenBytes);
                return Convert.ToBase64String(tokenBytes)
                    .Replace("+", "-")
                    .Replace("/", "_")
                    .Replace("=", "");
            }
        }
        
        /// <summary>
        /// Get device token from cookie (returns null if not found)
        /// </summary>
        public static string GetDeviceTokenFromCookie(HttpRequestBase request)
        {
            var cookie = request.Cookies[DeviceTokenCookieName];
            return cookie?.Value;
        }
        
        /// <summary>
        /// Set device token cookie (1 year expiration)
        /// </summary>
        public static void SetDeviceTokenCookie(HttpResponseBase response, string token, bool isSecure = false)
        {
            var cookie = new HttpCookie(DeviceTokenCookieName, token)
            {
                Expires = DateTime.UtcNow.AddDays(DeviceTokenExpiryDays),
                HttpOnly = true,
                Secure = isSecure, // Only secure if HTTPS is available
                SameSite = SameSiteMode.Lax // Lax allows navigation from external links
            };
            response.SetCookie(cookie);
        }
        
        /// <summary>
        /// Clear device token cookie
        /// </summary>
        public static void ClearDeviceTokenCookie(HttpResponseBase response, bool isSecure = false)
        {
            var cookie = new HttpCookie(DeviceTokenCookieName, "")
            {
                Expires = DateTime.UtcNow.AddDays(-1),
                HttpOnly = true,
                Secure = isSecure,
                SameSite = SameSiteMode.Lax
            };
            response.SetCookie(cookie);
        }
        
        #endregion
        
        #region Device Registration (Existing Employee)
        
        /// <summary>
        /// Register a new device for an existing employee
        /// Called when employee scans on their phone but device not recognized
        /// </summary>
        public static OperationResult<int> RegisterDevice(
            FaceAttendDBEntities db,
            int employeeId,
            string fingerprint,
            string deviceName,
            string ipAddress,
            string deviceToken = null) // Optional: existing token to reuse
        {
            // PHASE 1: Check by device token first (persistent identification)
            // SECURITY: Strict device ownership - one device token = one employee
            if (!string.IsNullOrEmpty(deviceToken))
            {
                var existingByToken = db.Devices
                    .FirstOrDefault(d => d.DeviceToken == deviceToken);
                
                if (existingByToken != null)
                {
                    if (existingByToken.EmployeeId == employeeId)
                    {
                        // Token belongs to this employee - update fingerprint and return success
                        existingByToken.Fingerprint = fingerprint;
                        existingByToken.LastUsedAt = DateTime.UtcNow;
                        
                        // FIX: If device was REPLACED, reactivate it
                        if (existingByToken.Status == "REPLACED")
                        {
                            existingByToken.Status = "ACTIVE";
                            existingByToken.ApprovedAt = DateTime.UtcNow;
                        }
                        
                        db.SaveChanges();
                        
                        return OperationResult<int>.Ok(existingByToken.Id,
                            "Device recognized and updated.");
                    }
                    // SECURITY: Token belongs to different employee - HARD REJECT
                    // This prevents device sharing/token hijacking between employees
                    return OperationResult<int>.Fail("DEVICE_TOKEN_OWNED_BY_OTHER",
                        "This device is already registered to another employee. Please use a different device.");
                }
            }
            
            // PHASE 2: Check by fingerprint
            var existingDevice = db.Devices
                .FirstOrDefault(d => d.Fingerprint == fingerprint);
            
            if (existingDevice != null)
            {
                if (existingDevice.EmployeeId == employeeId)
                {
                    // Update token if provided
                    if (!string.IsNullOrEmpty(deviceToken))
                    {
                        existingDevice.DeviceToken = deviceToken;
                        db.SaveChanges();
                    }
                    return OperationResult<int>.Fail("DEVICE_ALREADY_REGISTERED",
                        "This device is already registered to you.");
                }
                else
                {
                    return OperationResult<int>.Fail("DEVICE_REGISTERED_TO_OTHER",
                        "This device is registered to another employee.");
                }
            }
            
            // PHASE 3: Check if employee already has a device (1 device limit)
            var employeeExistingDevice = db.Devices
                .FirstOrDefault(d => d.EmployeeId == employeeId && d.Status == "ACTIVE");
            
            if (employeeExistingDevice != null)
            {
                // Instead of rejecting, we replace the old device
                // This implements the 1-device-per-employee policy
                employeeExistingDevice.Status = "REPLACED";
                employeeExistingDevice.LastUsedAt = DateTime.UtcNow;
            }
            
            // PHASE 4: Create device record with token
            // Generate token if not provided
            var newToken = deviceToken ?? GenerateDeviceToken();
            
            var device = new Device
            {
                EmployeeId = employeeId,
                Fingerprint = fingerprint,
                DeviceToken = newToken,
                TokenExpiresAt = DateTime.UtcNow.AddDays(DeviceTokenExpiryDays),
                DeviceName = SanitizeDeviceName(deviceName),
                DeviceType = "MOBILE",
                Status = "PENDING",
                RegisteredAt = DateTime.UtcNow,
                RegisteredFromIp = ipAddress
            };
            
            db.Devices.Add(device);
            db.SaveChanges();
            
            // Log for audit
            AuditHelper.Log(db, (string)null, "DEVICE_REGISTRATION_PENDING", "Device", 
                device.Id.ToString(), 
                $"Device registration pending for employee {employeeId}",
                null, new { device.DeviceName, device.Fingerprint, device.DeviceToken });
            
            return OperationResult<int>.Ok(device.Id, 
                "Device registration submitted. Waiting for admin approval.",
                new { DeviceToken = newToken }); // Return token for cookie
        }
        
        /// <summary>
        /// Validate if device is registered and active
        /// Checks device token first (persistent), then fingerprint (fallback)
        /// </summary>
        public static OperationResult<bool> ValidateDevice(
            FaceAttendDBEntities db,
            string fingerprint,
            int employeeId,
            string deviceToken = null)
        {
            Device device = null;
            
            // PHASE 1: Try to find device by token (most reliable)
            if (!string.IsNullOrEmpty(deviceToken))
            {
                device = db.Devices
                    .FirstOrDefault(d => d.DeviceToken == deviceToken && d.EmployeeId == employeeId);
                
                // Check token expiry
                if (device?.TokenExpiresAt != null && device.TokenExpiresAt < DateTime.UtcNow)
                {
                    // Token expired - will fall back to fingerprint
                    device = null;
                }
            }
            
            // PHASE 2: Fallback to fingerprint
            if (device == null)
            {
                device = db.Devices
                    .FirstOrDefault(d => d.Fingerprint == fingerprint && d.EmployeeId == employeeId);
                
                // If found by fingerprint but has no token, generate one now
                if (device != null && string.IsNullOrEmpty(device.DeviceToken))
                {
                    device.DeviceToken = GenerateDeviceToken();
                    device.TokenExpiresAt = DateTime.UtcNow.AddDays(DeviceTokenExpiryDays);
                }
            }
            
            if (device == null)
            {
                // CRITICAL FIX: Check if device is registered to a DIFFERENT employee
                Device deviceByToken = null;
                Device deviceByFingerprint = null;
                
                if (!string.IsNullOrEmpty(deviceToken))
                {
                    deviceByToken = db.Devices
                        .Include("Employee")
                        .FirstOrDefault(d => d.DeviceToken == deviceToken && d.Status == "ACTIVE");
                }
                
                deviceByFingerprint = db.Devices
                    .Include("Employee")
                    .FirstOrDefault(d => d.Fingerprint == fingerprint && d.Status == "ACTIVE");
                
                var existingDevice = deviceByToken ?? deviceByFingerprint;
                if (existingDevice != null && existingDevice.EmployeeId != employeeId)
                {
                    // Device belongs to a different employee!
                    var ownerName = existingDevice.Employee != null 
                        ? $"{existingDevice.Employee.FirstName} {existingDevice.Employee.LastName}"
                        : "another employee";
                    
                    // Return failure with owner info
                    return new OperationResult<bool>
                    {
                        Success = false,
                        ErrorCode = "WRONG_EMPLOYEE",
                        Message = $"This device is registered to {ownerName}. Please use your own registered device.",
                        Data = true
                    };
                }
                
                return OperationResult<bool>.Fail("NOT_REGISTERED", "Device not registered");
            }
            
            if (device.Status == "PENDING")
            {
                return OperationResult<bool>.Fail("PENDING", "Device pending admin approval");
            }
            
            if (device.Status == "BLOCKED")
            {
                return OperationResult<bool>.Fail("BLOCKED", "Device blocked");
            }
            
            // Update fingerprint (might have changed) and usage
            device.Fingerprint = fingerprint;
            device.LastUsedAt = DateTime.UtcNow;
            device.UseCount++;
            db.SaveChanges();
            
            return OperationResult<bool>.Ok(true, device.DeviceToken); // Return token for cookie refresh
        }
        
        /// <summary>
        /// Approve a pending device
        /// </summary>
        public static OperationResult ApproveDevice(FaceAttendDBEntities db, int deviceId, int adminId)
        {
            var device = db.Devices.Find(deviceId);
            if (device == null || device.Status != "PENDING")
                return OperationResult.Fail("NOT_FOUND", "Device not found or not pending");
            
            device.Status = "ACTIVE";
            device.ApprovedAt = DateTime.UtcNow;
            device.ApprovedBy = adminId;
            
            db.SaveChanges();
            
            AuditHelper.Log(db, (string)null, "DEVICE_APPROVED", "Device",
                device.Id.ToString(),
                $"Device approved for employee {device.EmployeeId}",
                null, null);
            
            return OperationResult.Ok($"Device '{device.DeviceName}' approved successfully");
        }
        
        #endregion
        
        #region Pending Employee Enrollment (New Employee Self-Enrollment)
        
        // NOTE: SubmitPendingEmployee method removed - MobileRegistrationController now handles
        // self-enrollment directly via SubmitEnrollment action
        
        /// <summary>
        /// Admin approves pending employee
        /// Creates actual Employee record and Device record
        /// </summary>
        public static OperationResult<int> ApprovePendingEmployee(
            FaceAttendDBEntities db,
            int pendingEmployeeId,
            int adminId)
        {
            var employee = db.Employees.Find(pendingEmployeeId);
            if (employee == null)
            {
                return OperationResult<int>.Fail("NOT_FOUND", "Pending employee not found.");
            }

            var currentStatus = GetEmployeeStatus(db, employee.Id);
            if (!string.Equals(currentStatus, "PENDING", StringComparison.OrdinalIgnoreCase))
            {
                return OperationResult<int>.Fail("INVALID_STATUS", "Employee is not pending approval.");
            }

            // Get pending device info from Devices table (not Employees table - columns removed in schema update)
            var pendingDevice = db.Devices
                .FirstOrDefault(d => d.EmployeeId == employee.Id && d.Status == "PENDING");

            // FIX: Always activate employee regardless of device info
            employee.LastModifiedDate = DateTime.UtcNow;
            employee.ModifiedBy = adminId.ToString();
            db.SaveChanges();

            SetEmployeeStatus(db, employee.Id, "ACTIVE", adminId.ToString());

            // FIX: Approve pending device if exists, but don't fail if missing
            // (device can be registered later via self-enrollment flow)
            if (pendingDevice != null && !string.IsNullOrWhiteSpace(pendingDevice.Fingerprint))
            {
                var fingerprintDevice = db.Devices.FirstOrDefault(d => d.Fingerprint == pendingDevice.Fingerprint);
                if (fingerprintDevice != null && fingerprintDevice.EmployeeId != employee.Id)
                {
                    return OperationResult<int>.Fail("DEVICE_REGISTERED_TO_OTHER",
                        "The pending device is already registered to another employee.");
                }

                var activeDevices = db.Devices
                    .Where(d => d.EmployeeId == employee.Id && d.Status == "ACTIVE" && d.Fingerprint != pendingDevice.Fingerprint)
                    .ToList();

                foreach (var oldDevice in activeDevices)
                {
                    oldDevice.Status = "REPLACED";
                    oldDevice.LastUsedAt = DateTime.UtcNow;
                }

                if (fingerprintDevice == null)
                {
                    db.Devices.Add(new Device
                    {
                        EmployeeId = employee.Id,
                        Fingerprint = pendingDevice.Fingerprint,
                        DeviceName = SanitizeDeviceName(pendingDevice.DeviceName),
                        DeviceType = "MOBILE",
                        Status = "ACTIVE",
                        RegisteredAt = DateTime.UtcNow,
                        ApprovedAt = DateTime.UtcNow,
                        ApprovedBy = adminId,
                        RegisteredFromIp = pendingDevice.RegisteredFromIp,
                        // FIX: Also set device token for persistent identification
                        DeviceToken = GenerateDeviceToken(),
                        TokenExpiresAt = DateTime.UtcNow.AddDays(DeviceTokenExpiryDays)
                    });
                }
                else
                {
                    fingerprintDevice.EmployeeId = employee.Id;
                    fingerprintDevice.DeviceName = SanitizeDeviceName(pendingDevice.DeviceName);
                    fingerprintDevice.DeviceType = "MOBILE";
                    fingerprintDevice.Status = "ACTIVE";
                    fingerprintDevice.ApprovedAt = DateTime.UtcNow;
                    fingerprintDevice.ApprovedBy = adminId;
                    fingerprintDevice.RegisteredFromIp = pendingDevice.RegisteredFromIp;
                    // FIX: Ensure device has token
                    if (string.IsNullOrEmpty(fingerprintDevice.DeviceToken))
                    {
                        fingerprintDevice.DeviceToken = GenerateDeviceToken();
                        fingerprintDevice.TokenExpiresAt = DateTime.UtcNow.AddDays(DeviceTokenExpiryDays);
                    }
                }

                db.SaveChanges();
            }

            // Device info is now stored in Devices table only (columns removed from Employees)
            Services.Biometrics.EmployeeFaceIndex.Invalidate();
            global::FaceAttend.Services.Biometrics.FastFaceMatcher.ReloadFromDatabase();

            AuditHelper.Log(db, (string)null, "EMPLOYEE_ENROLLMENT_APPROVED", "Employee",
                employee.EmployeeId,
                $"Employee enrollment approved: {employee.FirstName} {employee.LastName}",
                null, new { EmployeeId = employee.Id, Status = "ACTIVE" });

            return OperationResult<int>.Ok(employee.Id,
                $"Employee {employee.FirstName} {employee.LastName} approved and enrolled.");
        }
        
        /// <summary>
        /// Reject pending employee enrollment
        /// </summary>
        public static OperationResult RejectPendingEmployee(
            FaceAttendDBEntities db,
            int pendingEmployeeId,
            int adminId,
            string reason)
        {
            var employee = db.Employees.Find(pendingEmployeeId);
            if (employee == null)
                return OperationResult.Fail("NOT_FOUND", "Pending employee not found");

            var currentStatus = GetEmployeeStatus(db, employee.Id);
            if (!string.Equals(currentStatus, "PENDING", StringComparison.OrdinalIgnoreCase))
                return OperationResult.Fail("INVALID_STATUS", "Employee is not pending");

            employee.LastModifiedDate = DateTime.UtcNow;
            employee.ModifiedBy = adminId.ToString();
            db.SaveChanges();

            SetEmployeeStatus(db, employee.Id, "INACTIVE", adminId.ToString());

            var fullName = $"{employee.FirstName} {employee.LastName}".Trim();
            AuditHelper.Log(db, (string)null, "EMPLOYEE_ENROLLMENT_REJECTED", "Employee",
                employee.EmployeeId,
                $"Employee enrollment rejected: {fullName}. Reason: {reason}",
                null, new { Status = "INACTIVE", Reason = reason });

            return OperationResult.Ok($"Enrollment for {fullName} has been rejected");
        }
        
        #endregion

        #region Employee Status Helpers

        public static string GetEmployeeStatus(FaceAttendDBEntities db, int employeeId)
        {
            // FIXED: Removed IsActive reference - Employee table only has Status column
            var status = db.Database.SqlQuery<string>(
                @"SELECT TOP 1 ISNULL([Status], 'INACTIVE')
                  FROM dbo.Employees
                  WHERE Id = @id",
                new SqlParameter("@id", employeeId))
                .FirstOrDefault();

            return string.IsNullOrWhiteSpace(status) ? "INACTIVE" : status.Trim().ToUpperInvariant();
        }

        public static void SetEmployeeStatus(FaceAttendDBEntities db, int employeeId, string status, string modifiedBy)
        {
            var normalized = NormalizeEmployeeStatus(status);

            db.Database.ExecuteSqlCommand(
                @"UPDATE dbo.Employees
                  SET [Status] = @status,
                      LastModifiedDate = @modifiedAt,
                      ModifiedBy = @modifiedBy
                  WHERE Id = @id",
                new SqlParameter("@status", normalized),
                new SqlParameter("@modifiedAt", DateTime.UtcNow),
                new SqlParameter("@modifiedBy", (object)modifiedBy ?? DBNull.Value),
                new SqlParameter("@id", employeeId));
        }

        /// <summary>
        /// Creates a pending device record for a new employee enrollment.
        /// Device info is stored in the Devices table (columns removed from Employees in schema update).
        /// </summary>
        public static void CreatePendingDevice(FaceAttendDBEntities db, int employeeId, string fingerprint, string deviceName, string ipAddress)
        {
            // Remove any existing pending devices for this employee
            var existingPending = db.Devices.Where(d => d.EmployeeId == employeeId && d.Status == "PENDING").ToList();
            foreach (var d in existingPending)
            {
                d.Status = "REPLACED";
            }
            
            db.Devices.Add(new Device
            {
                EmployeeId = employeeId,
                Fingerprint = fingerprint,
                DeviceName = SanitizeDeviceName(deviceName),
                DeviceType = "MOBILE",
                Status = "PENDING",
                RegisteredAt = DateTime.UtcNow,
                RegisteredFromIp = ipAddress,
                DeviceToken = GenerateDeviceToken(),
                TokenExpiresAt = DateTime.UtcNow.AddDays(DeviceTokenExpiryDays)
            });
            
            db.SaveChanges();
        }

        private static string NormalizeEmployeeStatus(string status)
        {
            var normalized = (status ?? "ACTIVE").Trim().ToUpperInvariant();
            if (normalized != "ACTIVE" && normalized != "PENDING" && normalized != "INACTIVE")
            {
                normalized = "ACTIVE";
            }
            return normalized;
        }

        #endregion
        
        #region Helper Methods
        
        private static string SanitizeDeviceName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "Unknown Device";
            
            var sanitized = new string(name
                .Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_' || c == '(' || c == ')')
                .ToArray());
            
            return sanitized.Length > 100 ? sanitized.Substring(0, 100) : sanitized;
        }
        
        #endregion
    }
}
