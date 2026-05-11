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
            var width = request.Headers["X-Client-Screen-Width"]
                ?? request.Headers["X-Screen-Width"]
                ?? "0";
            var height = request.Headers["X-Client-Screen-Height"]
                ?? request.Headers["X-Screen-Height"]
                ?? "0";
            
            if (width == "0" || height == "0")
            {
                return $"{request.Browser?.ScreenPixelsWidth ?? 0}x{request.Browser?.ScreenPixelsHeight ?? 0}";
            }
            
            return $"{width}x{height}";
        }
        
        /// <summary>
        /// Check if device is a mobile device (BYOD) vs desktop/kiosk
        /// ENHANCED: Uses Sec-CH-UA-Mobile header and client-side hints which survive "Desktop site" mode
        /// </summary>
        public static bool IsMobileDevice(HttpRequestBase request)
        {
            // Sec-CH-UA-Mobile: ?1 means the hardware IS a mobile device,
            // regardless of desktop mode being enabled in browser settings.
            // This survives "Request desktop site" in Chrome Android.
            var chUaMobile = request.Headers["Sec-CH-UA-Mobile"];
            if (!string.IsNullOrWhiteSpace(chUaMobile))
            {
                if (chUaMobile.Trim() == "?1") return true;
                if (chUaMobile.Trim() == "?0") return false;
            }

            // Client-side mobile detection (JavaScript-detected, sent via headers)
            // This survives "Desktop site" mode because it's based on hardware capabilities
            var clientScreenW = request.Headers["X-Client-Screen-Width"];
            var clientScreenH = request.Headers["X-Client-Screen-Height"];
            var clientPixelRatio = request.Headers["X-Client-Pixel-Ratio"];
            var clientTouch = request.Headers["X-Client-Touch-Supported"];
            var clientMobileUA = request.Headers["X-Client-Mobile-UA"];

            if (!string.IsNullOrEmpty(clientScreenW) && !string.IsNullOrEmpty(clientScreenH))
            {
                int screenW, screenH;
                float pixelRatio;
                
                if (int.TryParse(clientScreenW, out screenW) && 
                    int.TryParse(clientScreenH, out screenH) &&
                    float.TryParse(clientPixelRatio ?? "1", out pixelRatio))
                {
                    var minDim = Math.Min(screenW, screenH);
                    var maxDim = Math.Max(screenW, screenH);
                    var isTouch = clientTouch == "true";
                    var isMobileUA = clientMobileUA == "true";
                    
                    // Mobile characteristics: touch + high pixel ratio + small screen
                    if (isTouch && pixelRatio >= 2.0f && minDim <= 600)
                    {
                        return true;
                    }
                    
                    // Additional check: if original UA was mobile but UA was spoofed
                    if (isMobileUA && isTouch && maxDim <= 1400)
                    {
                        return true;
                    }
                }
            }

            // Fallback to UA parsing for older browsers
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
        
        #region Pending Employee Enrollment (New Employee Self-Enrollment)
        
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

            employee.LastModifiedDate = DateTime.UtcNow;
            employee.ModifiedBy = adminId.ToString();
            db.SaveChanges();

            SetEmployeeStatus(db, employee.Id, "ACTIVE", adminId.ToString());

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

            BiometricTemplateMetadataService.SetActiveForEmployee(
                db,
                employeeId,
                string.Equals(normalized, "ACTIVE", StringComparison.OrdinalIgnoreCase));
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
    }
}
