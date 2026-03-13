using System;
using System.Collections.Generic;

namespace FaceAttend.Services
{
    /// <summary>
    /// Standardized API response format for all JSON endpoints.
    /// 
    /// PHASE 1 ENHANCEMENT (H1.2):
    ///   Provides consistent JSON response structure across all controllers
    ///   for easier client-side handling and debugging.
    /// 
    /// USAGE:
    ///   // Success response
    ///   return Json(ApiResponse.Success(new { employeeId = "123", name = "John" }));
    ///   
    ///   // Error response
    ///   return Json(ApiResponse.Error("EMPLOYEE_NOT_FOUND", "Employee not found in database"));
    ///   
    ///   // With timings
    ///   return Json(ApiResponse.Success(data, timings));
    /// </summary>
    public class ApiResponse
    {
        /// <summary>
        /// Indicates if the operation was successful.
        /// </summary>
        public bool Ok { get; set; }

        /// <summary>
        /// Error code (null if successful).
        /// Use PascalCase constants like "EMPLOYEE_NOT_FOUND".
        /// </summary>
        public string ErrorCode { get; set; }

        /// <summary>
        /// Human-readable error message (null if successful).
        /// </summary>
        public string Message { get; set; }

        /// <summary>
        /// Response data (null if error).
        /// </summary>
        public object Data { get; set; }

        /// <summary>
        /// Performance timings (optional).
        /// </summary>
        public Dictionary<string, long> Timings { get; set; }

        /// <summary>
        /// Server timestamp in UTC.
        /// </summary>
        public DateTime TimestampUtc { get; set; }

        /// <summary>
        /// Response version for API versioning.
        /// </summary>
        public string Version { get; set; }

        public ApiResponse()
        {
            TimestampUtc = DateTime.UtcNow;
            Version = "1.0";
        }

        // =====================================================================
        // STATIC FACTORY METHODS
        // =====================================================================

        /// <summary>
        /// Creates a success response.
        /// </summary>
        public static ApiResponse Success(object data = null, Dictionary<string, long> timings = null)
        {
            return new ApiResponse
            {
                Ok = true,
                Data = data,
                Timings = timings
            };
        }

        /// <summary>
        /// Creates an error response.
        /// </summary>
        public static ApiResponse Error(string errorCode, string message = null, Dictionary<string, long> timings = null)
        {
            return new ApiResponse
            {
                Ok = false,
                ErrorCode = errorCode,
                Message = message ?? GetDefaultErrorMessage(errorCode),
                Timings = timings
            };
        }

        /// <summary>
        /// Creates a validation error response.
        /// </summary>
        public static ApiResponse ValidationError(string field, string message)
        {
            return new ApiResponse
            {
                Ok = false,
                ErrorCode = "VALIDATION_ERROR",
                Message = $"{field}: {message}",
                Data = new { field, message }
            };
        }

        /// <summary>
        /// Creates a rate limit error response.
        /// </summary>
        public static ApiResponse RateLimit(int retryAfterSeconds)
        {
            return new ApiResponse
            {
                Ok = false,
                ErrorCode = "RATE_LIMIT_EXCEEDED",
                Message = $"Too many requests. Please retry after {retryAfterSeconds} seconds.",
                Data = new { retryAfter = retryAfterSeconds }
            };
        }

        /// <summary>
        /// Creates a system busy error response.
        /// </summary>
        public static ApiResponse SystemBusy(int retryAfterSeconds = 2)
        {
            return new ApiResponse
            {
                Ok = false,
                ErrorCode = "SYSTEM_BUSY",
                Message = "System is busy. Please try again in a few seconds.",
                Data = new { retryAfter = retryAfterSeconds }
            };
        }

        // =====================================================================
        // ERROR CODE CONSTANTS
        // =====================================================================

        public static class ErrorCodes
        {
            // Authentication/Authorization
            public const string INVALID_PIN = "INVALID_PIN";
            public const string SESSION_EXPIRED = "SESSION_EXPIRED";
            public const string UNAUTHORIZED = "UNAUTHORIZED";
            public const string FORBIDDEN = "FORBIDDEN";
            public const string IP_NOT_ALLOWED = "IP_NOT_ALLOWED";

            // Employee
            public const string EMPLOYEE_NOT_FOUND = "EMPLOYEE_NOT_FOUND";
            public const string EMPLOYEE_ID_EXISTS = "EMPLOYEE_ID_EXISTS";
            public const string EMPLOYEE_ID_TOO_LONG = "EMPLOYEE_ID_TOO_LONG";
            public const string EMPLOYEE_ID_INVALID_FORMAT = "EMPLOYEE_ID_INVALID_FORMAT";

            // Biometrics
            public const string NO_IMAGE = "NO_IMAGE";
            public const string IMAGE_TOO_LARGE = "IMAGE_TOO_LARGE";
            public const string INVALID_IMAGE_FORMAT = "INVALID_IMAGE_FORMAT";
            public const string NO_FACE = "NO_FACE";
            public const string MULTI_FACE = "MULTI_FACE";
            public const string FACE_TOO_SMALL = "FACE_TOO_SMALL";
            public const string ENCODING_FAIL = "ENCODING_FAIL";
            public const string LIVENESS_FAIL = "LIVENESS_FAIL";
            public const string FACE_ALREADY_ENROLLED = "FACE_ALREADY_ENROLLED";
            public const string POOL_TIMEOUT = "POOL_TIMEOUT";

            // Location
            public const string GPS_REQUIRED = "GPS_REQUIRED";
            public const string GPS_ACCURACY = "GPS_ACCURACY";
            public const string GPS_DENIED = "GPS_DENIED";
            public const string GPS_UNAVAILABLE = "GPS_UNAVAILABLE";
            public const string GPS_TIMEOUT = "GPS_TIMEOUT";
            public const string NO_OFFICES = "NO_OFFICES";
            public const string NO_OFFICE_NEARBY = "NO_OFFICE_NEARBY";
            public const string SUSPICIOUS_LOCATION = "SUSPICIOUS_LOCATION";

            // Attendance
            public const string TOO_SOON = "TOO_SOON";
            public const string ALREADY_SCANNED = "ALREADY_SCANNED";
            public const string SCAN_ERROR = "SCAN_ERROR";
            public const string REQUEST_TIMEOUT = "REQUEST_TIMEOUT";

            // Device
            public const string DEVICE_NOT_REGISTERED = "DEVICE_NOT_REGISTERED";
            public const string DEVICE_PENDING = "DEVICE_PENDING";
            public const string DEVICE_BLOCKED = "DEVICE_BLOCKED";
            public const string DEVICE_REGISTERED_TO_OTHER = "DEVICE_REGISTERED_TO_OTHER";
            public const string DEVICE_ALREADY_REGISTERED = "DEVICE_ALREADY_REGISTERED";

            // Visitor
            public const string SCAN_EXPIRED = "SCAN_EXPIRED";
            public const string SCAN_SESSION_MISMATCH = "SCAN_SESSION_MISMATCH";
            public const string NAME_REQUIRED = "NAME_REQUIRED";
            public const string PURPOSE_REQUIRED = "PURPOSE_REQUIRED";
            public const string VISITOR_SAVE_ERROR = "VISITOR_SAVE_ERROR";

            // System
            public const string SYSTEM_ERROR = "SYSTEM_ERROR";
            public const string CIRCUIT_OPEN = "CIRCUIT_OPEN";
            public const string SESSION_STUCK = "SESSION_STUCK";
            public const string NO_SESSION = "NO_SESSION";
            public const string PREPROCESS_FAIL = "PREPROCESS_FAIL";
            public const string TIMEOUT = "TIMEOUT";
            public const string ONNX_ERROR = "ONNX_ERROR";
        }

        // =====================================================================
        // HELPER METHODS
        // =====================================================================

        private static string GetDefaultErrorMessage(string errorCode)
        {
            if (string.IsNullOrWhiteSpace(errorCode))
                return "An unknown error occurred.";

            switch (errorCode.ToUpperInvariant())
            {
                case ErrorCodes.INVALID_PIN:
                    return "Invalid PIN. Please try again.";
                case ErrorCodes.SESSION_EXPIRED:
                    return "Your session has expired. Please log in again.";
                case ErrorCodes.UNAUTHORIZED:
                    return "You are not authorized to perform this action.";
                case ErrorCodes.FORBIDDEN:
                    return "Access denied.";
                case ErrorCodes.IP_NOT_ALLOWED:
                    return "Your IP address is not authorized.";

                case ErrorCodes.EMPLOYEE_NOT_FOUND:
                    return "Employee not found in the database.";
                case ErrorCodes.EMPLOYEE_ID_EXISTS:
                    return "An employee with this ID already exists.";
                case ErrorCodes.EMPLOYEE_ID_TOO_LONG:
                    return "Employee ID is too long. Maximum 20 characters.";
                case ErrorCodes.EMPLOYEE_ID_INVALID_FORMAT:
                    return "Employee ID format is invalid. Use letters, numbers, hyphens, and underscores only.";

                case ErrorCodes.NO_IMAGE:
                    return "No image was provided.";
                case ErrorCodes.IMAGE_TOO_LARGE:
                    return "Image is too large. Maximum size is 10MB.";
                case ErrorCodes.INVALID_IMAGE_FORMAT:
                    return "Invalid image format. Please upload JPG or PNG.";
                case ErrorCodes.NO_FACE:
                    return "No face detected in the image.";
                case ErrorCodes.MULTI_FACE:
                    return "Multiple faces detected. Please scan one face at a time.";
                case ErrorCodes.FACE_TOO_SMALL:
                    return "Face is too small in the image. Please move closer.";
                case ErrorCodes.ENCODING_FAIL:
                    return "Failed to process face encoding.";
                case ErrorCodes.LIVENESS_FAIL:
                    return "Liveness check failed. Please ensure you are a real person.";
                case ErrorCodes.FACE_ALREADY_ENROLLED:
                    return "This face is already enrolled for another employee.";
                case ErrorCodes.POOL_TIMEOUT:
                    return "System is too busy. Please try again in a moment.";

                case ErrorCodes.GPS_REQUIRED:
                    return "GPS location is required.";
                case ErrorCodes.GPS_ACCURACY:
                    return "GPS accuracy is insufficient. Please move to an open area.";
                case ErrorCodes.GPS_DENIED:
                    return "Location access was denied. Please enable location services.";
                case ErrorCodes.GPS_UNAVAILABLE:
                    return "Location services are unavailable.";
                case ErrorCodes.GPS_TIMEOUT:
                    return "Location request timed out.";
                case ErrorCodes.NO_OFFICES:
                    return "No offices are configured in the system.";
                case ErrorCodes.NO_OFFICE_NEARBY:
                    return "You are not within the allowed office area.";
                case ErrorCodes.SUSPICIOUS_LOCATION:
                    return "Location verification failed. Please contact administrator.";

                case ErrorCodes.TOO_SOON:
                    return "You scanned too recently. Please wait before scanning again.";
                case ErrorCodes.ALREADY_SCANNED:
                    return "You have already scanned today.";
                case ErrorCodes.SCAN_ERROR:
                    return "An error occurred during scanning. Please try again.";
                case ErrorCodes.REQUEST_TIMEOUT:
                    return "The request timed out. Please try again.";

                case ErrorCodes.DEVICE_NOT_REGISTERED:
                    return "This device is not registered.";
                case ErrorCodes.DEVICE_PENDING:
                    return "Device registration is pending approval.";
                case ErrorCodes.DEVICE_BLOCKED:
                    return "This device has been blocked.";
                case ErrorCodes.DEVICE_REGISTERED_TO_OTHER:
                    return "This device is registered to another employee.";
                case ErrorCodes.DEVICE_ALREADY_REGISTERED:
                    return "This device is already registered.";

                case ErrorCodes.SCAN_EXPIRED:
                    return "Scan session has expired. Please scan again.";
                case ErrorCodes.SCAN_SESSION_MISMATCH:
                    return "Scan session is invalid. Please scan again.";
                case ErrorCodes.NAME_REQUIRED:
                    return "Name is required.";
                case ErrorCodes.PURPOSE_REQUIRED:
                    return "Purpose is required.";
                case ErrorCodes.VISITOR_SAVE_ERROR:
                    return "Failed to save visitor information.";

                case ErrorCodes.SYSTEM_ERROR:
                    return "A system error occurred. Please contact support.";
                case ErrorCodes.CIRCUIT_OPEN:
                    return "System is temporarily unavailable. Please try again later.";
                case ErrorCodes.SESSION_STUCK:
                    return "Biometric session needs to be reset.";
                case ErrorCodes.NO_SESSION:
                    return "Biometric session not initialized.";
                case ErrorCodes.PREPROCESS_FAIL:
                    return "Failed to preprocess image.";
                case ErrorCodes.TIMEOUT:
                    return "Operation timed out.";
                case ErrorCodes.ONNX_ERROR:
                    return "Biometric processing error.";

                default:
                    return $"Error: {errorCode}";
            }
        }
    }
}
