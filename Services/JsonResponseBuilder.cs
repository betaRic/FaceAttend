using System;
using System.Collections.Generic;
using System.Web;
using System.Web.Mvc;

namespace FaceAttend.Services
{
    /// <summary>
    /// Centralized JSON response builder for API endpoints.
    /// 
    /// PURPOSE:
    ///   Ensures consistent JSON response format across all controllers.
    ///   Eliminates duplicate anonymous object creation.
    /// 
    /// STANDARD RESPONSE FORMAT:
    ///   {
    ///     "ok": true|false,
    ///     "data": { ... },           // Present if ok=true
    ///     "error": "ERROR_CODE",     // Present if ok=false
    ///     "message": "Human readable",
    ///     "retryAfter": 2            // Optional
    ///   }
    /// 
    /// USAGE EXAMPLES:
    ///   // Success with data
    ///   return JsonResponseBuilder.Success(new { employeeId = 123 });
    ///   
    ///   // Error
    ///   return JsonResponseBuilder.Error("NOT_FOUND", "Employee not found");
    ///   
    ///   // System busy
    ///   return JsonResponseBuilder.SystemBusy(2);
    ///   
    ///   // With performance timings (KioskController pattern)
    ///   return JsonResponseBuilder.Error("NO_IMAGE").WithTimings(timings, includeTimings);
    /// </summary>
    public static class JsonResponseBuilder
    {
        #region Core Response Methods

        /// <summary>
        /// Creates a successful JSON response
        /// </summary>
        /// <param name="data">Response data (optional)</param>
        /// <param name="message">Success message (optional)</param>
        public static JsonResult Success(object data = null, string message = null)
        {
            return new JsonResult
            {
                Data = new { ok = true, data, message },
                JsonRequestBehavior = JsonRequestBehavior.AllowGet
            };
        }

        /// <summary>
        /// Creates an error JSON response
        /// </summary>
        /// <param name="errorCode">Machine-readable error code</param>
        /// <param name="message">Human-readable error message</param>
        /// <param name="retryAfter">Seconds before retry (optional)</param>
        /// <param name="details">Additional error details (optional)</param>
        public static JsonResult Error(string errorCode, string message = null, 
            int? retryAfter = null, object details = null)
        {
            return new JsonResult
            {
                Data = new { ok = false, error = errorCode, message, retryAfter, details },
                JsonRequestBehavior = JsonRequestBehavior.AllowGet
            };
        }

        /// <summary>
        /// Creates a validation error response
        /// </summary>
        /// <param name="field">Field that failed validation</param>
        /// <param name="message">Validation error message</param>
        public static JsonResult ValidationError(string field, string message)
        {
            return new JsonResult
            {
                Data = new { ok = false, error = "VALIDATION_ERROR", field, message },
                JsonRequestBehavior = JsonRequestBehavior.AllowGet
            };
        }

        /// <summary>
        /// Creates a "not found" error response
        /// </summary>
        /// <param name="entityName">Name of the entity that was not found</param>
        public static JsonResult NotFound(string entityName)
        {
            return new JsonResult
            {
                Data = new { ok = false, error = "NOT_FOUND", message = $"{entityName} not found" },
                JsonRequestBehavior = JsonRequestBehavior.AllowGet
            };
        }

        /// <summary>
        /// Creates a "system busy" error response
        /// </summary>
        /// <param name="retryAfterSeconds">Seconds before client should retry</param>
        public static JsonResult SystemBusy(int retryAfterSeconds = 2)
        {
            return new JsonResult
            {
                Data = new 
                { 
                    ok = false, 
                    error = "SYSTEM_BUSY", 
                    message = "System is busy. Please try again in a few seconds.",
                    retryAfter = retryAfterSeconds
                },
                JsonRequestBehavior = JsonRequestBehavior.AllowGet
            };
        }

        /// <summary>
        /// Creates a rate limit exceeded error response
        /// </summary>
        /// <param name="retryAfterSeconds">Seconds before rate limit resets</param>
        public static JsonResult RateLimited(int retryAfterSeconds)
        {
            return new JsonResult
            {
                Data = new 
                { 
                    ok = false, 
                    error = "RATE_LIMIT_EXCEEDED", 
                    message = "Too many requests. Please slow down.",
                    retryAfter = retryAfterSeconds
                },
                JsonRequestBehavior = JsonRequestBehavior.AllowGet
            };
        }

        /// <summary>
        /// Creates an unauthorized error response
        /// </summary>
        public static JsonResult Unauthorized(string message = "Authentication required")
        {
            return new JsonResult
            {
                Data = new { ok = false, error = "UNAUTHORIZED", message },
                JsonRequestBehavior = JsonRequestBehavior.AllowGet
            };
        }

        /// <summary>
        /// Creates a forbidden error response
        /// </summary>
        public static JsonResult Forbidden(string message = "Access denied")
        {
            return new JsonResult
            {
                Data = new { ok = false, error = "FORBIDDEN", message },
                JsonRequestBehavior = JsonRequestBehavior.AllowGet
            };
        }

        #endregion

        #region KioskController Specific Patterns

        /// <summary>
        /// Creates an error response with optional performance timings.
        /// Used by KioskController and BiometricsController.
        /// </summary>
        /// <param name="errorCode">Machine-readable error code</param>
        /// <param name="timings">Performance timings dictionary</param>
        /// <param name="includeTimings">Whether to include timings in response</param>
        /// <param name="message">Optional custom message</param>
        public static JsonResult ErrorWithTimings(string errorCode, 
            IDictionary<string, long> timings, bool includeTimings, string message = null)
        {
            return new JsonResult
            {
                Data = new 
                { 
                    ok = false, 
                    error = errorCode, 
                    message,
                    timings = includeTimings ? timings : null 
                },
                JsonRequestBehavior = JsonRequestBehavior.AllowGet
            };
        }

        /// <summary>
        /// Creates a liveness failure response with details.
        /// </summary>
        public static JsonResult LivenessFail(float liveness, float threshold,
            IDictionary<string, long> timings = null, bool includeTimings = false)
        {
            return new JsonResult
            {
                Data = new 
                { 
                    ok = false, 
                    error = "LIVENESS_FAIL", 
                    liveness, 
                    threshold, 
                    timings = includeTimings ? timings : null 
                },
                JsonRequestBehavior = JsonRequestBehavior.AllowGet
            };
        }

        /// <summary>
        /// Creates an encoding failure response with optional debug details.
        /// </summary>
        public static JsonResult EncodingFail(string detail, 
            IDictionary<string, long> timings = null, bool includeTimings = false, bool debug = false)
        {
            var data = new 
            { 
                ok = false, 
                error = "ENCODING_FAIL", 
                detail = debug ? detail : null,
                timings = includeTimings ? timings : null 
            };

            return new JsonResult
            {
                Data = data,
                JsonRequestBehavior = JsonRequestBehavior.AllowGet
            };
        }

        /// <summary>
        /// Creates a "too soon" error response for attendance timing.
        /// </summary>
        public static JsonResult TooSoon(string message, int? minGapSeconds = null,
            IDictionary<string, long> timings = null, bool includeTimings = false)
        {
            var data = new 
            { 
                ok = false, 
                error = "TOO_SOON", 
                message,
                minGapSeconds,
                timings = includeTimings ? timings : null
            };

            return new JsonResult
            {
                Data = data,
                JsonRequestBehavior = JsonRequestBehavior.AllowGet
            };
        }

        /// <summary>
        /// Creates an attendance success response.
        /// </summary>
        public static JsonResult AttendanceSuccess(
            string employeeId, string name, string displayName, string eventType, 
            string message, int officeId, string officeName, float liveness, 
            double distance, DateTime attemptedAtUtc,
            IDictionary<string, long> timings = null, bool includeTimings = false,
            string deviceToken = null)
        {
            return new JsonResult
            {
                Data = new
                {
                    ok = true,
                    employeeId,
                    name,
                    displayName,
                    eventType,
                    message,
                    officeId,
                    officeName,
                    liveness,
                    distance,
                    attemptedAtUtc,
                    timings = includeTimings ? timings : null,
                    deviceToken // Return token so client can save to localStorage
                },
                JsonRequestBehavior = JsonRequestBehavior.AllowGet
            };
        }

        /// <summary>
        /// Creates a visitor scan response.
        /// </summary>
        public static JsonResult VisitorScan(string scanId, bool isKnown, 
            string visitorName, double? distance, double threshold, float liveness,
            IDictionary<string, long> timings = null, bool includeTimings = false)
        {
            return new JsonResult
            {
                Data = new
                {
                    ok = true,
                    mode = "VISITOR",
                    scanId,
                    isKnown,
                    visitorName = isKnown ? visitorName : null,
                    distance = double.IsInfinity(distance ?? double.PositiveInfinity) ? (double?)null : distance,
                    threshold,
                    liveness,
                    timings = includeTimings ? timings : null
                },
                JsonRequestBehavior = JsonRequestBehavior.AllowGet
            };
        }

        /// <summary>
        /// Creates a device registration required response.
        /// </summary>
        public static JsonResult RegisterDeviceRequired(int employeeId, 
            string employeeName, string fingerprint, string message)
        {
            return new JsonResult
            {
                Data = new
                {
                    ok = false,
                    action = "REGISTER_DEVICE",
                    employeeId,
                    employeeName,
                    fingerprint,
                    message
                },
                JsonRequestBehavior = JsonRequestBehavior.AllowGet
            };
        }

        /// <summary>
        /// Creates a device pending approval response.
        /// </summary>
        public static JsonResult DevicePending(string message)
        {
            return new JsonResult
            {
                Data = new
                {
                    ok = false,
                    action = "DEVICE_PENDING",
                    message
                },
                JsonRequestBehavior = JsonRequestBehavior.AllowGet
            };
        }

        /// <summary>
        /// Creates a device blocked response.
        /// </summary>
        public static JsonResult DeviceBlocked(string message)
        {
            return new JsonResult
            {
                Data = new
                {
                    ok = false,
                    action = "DEVICE_BLOCKED",
                    message
                },
                JsonRequestBehavior = JsonRequestBehavior.AllowGet
            };
        }

        /// <summary>
        /// Creates a self-enrollment offer response.
        /// </summary>
        public static JsonResult SelfEnrollOffer(string fingerprint, string message)
        {
            return new JsonResult
            {
                Data = new
                {
                    ok = false,
                    action = "SELF_ENROLL",
                    fingerprint,
                    message
                },
                JsonRequestBehavior = JsonRequestBehavior.AllowGet
            };
        }

        /// <summary>
        /// Creates a suspicious location response.
        /// </summary>
        public static JsonResult SuspiciousLocation(string message,
            IDictionary<string, long> timings = null, bool includeTimings = false)
        {
            return new JsonResult
            {
                Data = new 
                { 
                    ok = false, 
                    error = "SUSPICIOUS_LOCATION", 
                    message,
                    timings = includeTimings ? timings : null 
                },
                JsonRequestBehavior = JsonRequestBehavior.AllowGet
            };
        }

        /// <summary>
        /// Creates a scan error response with optional debug info.
        /// </summary>
        public static JsonResult ScanError(string detail = null, string inner = null,
            IDictionary<string, long> timings = null, bool includeTimings = false, bool debug = false)
        {
            return new JsonResult
            {
                Data = new
                {
                    ok = false,
                    error = "SCAN_ERROR",
                    detail = debug ? detail : null,
                    inner = debug ? inner : null,
                    timings = includeTimings ? timings : null
                },
                JsonRequestBehavior = JsonRequestBehavior.AllowGet
            };
        }

        /// <summary>
        /// Creates an office resolution response.
        /// </summary>
        public static JsonResult OfficeResolved(bool allowed, bool gpsRequired, 
            int? officeId = null, string officeName = null, string reason = null,
            int? requiredAccuracy = null, double? accuracy = null)
        {
            return new JsonResult
            {
                Data = new
                {
                    ok = true,
                    gpsRequired,
                    allowed,
                    officeId,
                    officeName,
                    reason,
                    requiredAccuracy,
                    accuracy
                },
                JsonRequestBehavior = JsonRequestBehavior.AllowGet
            };
        }

        /// <summary>
        /// Creates a request timeout response.
        /// </summary>
        public static JsonResult RequestTimeout(
            IDictionary<string, long> timings = null, bool includeTimings = false)
        {
            return new JsonResult
            {
                Data = new
                {
                    ok = false,
                    error = "REQUEST_TIMEOUT",
                    message = "Request timed out. Please try again.",
                    retryAfter = 2,
                    timings = includeTimings ? timings : null
                },
                JsonRequestBehavior = JsonRequestBehavior.AllowGet
            };
        }

        /// <summary>
        /// Creates a no offices configured response.
        /// </summary>
        public static JsonResult NoOffices(
            IDictionary<string, long> timings = null, bool includeTimings = false)
        {
            return new JsonResult
            {
                Data = new
                {
                    ok = false,
                    error = "NO_OFFICES",
                    message = "No office configured. Please contact your administrator.",
                    timings = includeTimings ? timings : null
                },
                JsonRequestBehavior = JsonRequestBehavior.AllowGet
            };
        }

        /// <summary>
        /// Creates a face not recognized response.
        /// </summary>
        public static JsonResult NotRecognized(
            IDictionary<string, long> timings = null, bool includeTimings = false)
        {
            return new JsonResult
            {
                Data = new
                {
                    ok = false,
                    error = "NOT_RECOGNIZED",
                    message = "Face not recognized. Please check if you're enrolled.",
                    timings = includeTimings ? timings : null
                },
                JsonRequestBehavior = JsonRequestBehavior.AllowGet
            };
        }

        #endregion
    }

    /// <summary>
    /// Extension methods for Controller to simplify JSON response creation.
    /// </summary>
    public static class JsonResponseControllerExtensions
    {
        /// <summary>
        /// Returns a JSON error response.
        /// </summary>
        public static JsonResult JsonError(this Controller controller, string errorCode, string message = null)
        {
            return JsonResponseBuilder.Error(errorCode, message);
        }

        /// <summary>
        /// Returns a JSON success response.
        /// </summary>
        public static JsonResult JsonSuccess(this Controller controller, object data = null, string message = null)
        {
            return JsonResponseBuilder.Success(data, message);
        }

        /// <summary>
        /// Returns a JSON error with performance timings.
        /// </summary>
        public static JsonResult JsonErrorWithTimings(this Controller controller, string errorCode,
            IDictionary<string, long> timings, bool includeTimings, string message = null)
        {
            return JsonResponseBuilder.ErrorWithTimings(errorCode, timings, includeTimings, message);
        }
    }
}
