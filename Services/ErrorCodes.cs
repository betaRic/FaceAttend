using System.Collections.Generic;

namespace FaceAttend.Services
{
    /// <summary>
    /// Centralized error codes for the entire application.
    /// Use these instead of magic strings throughout the codebase.
    /// </summary>
    public static class ErrorCodes
    {
        // ─────────────────────────────────────────────────────────────────────────────
        // IMAGE / UPLOAD ERRORS
        // ─────────────────────────────────────────────────────────────────────────────
        public const string NO_IMAGE = "NO_IMAGE";
        public const string TOO_LARGE = "TOO_LARGE";
        public const string INVALID_FORMAT = "INVALID_IMAGE_FORMAT";
        public const string IMAGE_LOAD_FAIL = "IMAGE_LOAD_FAIL";
        public const string BITMAP_CONVERT_FAIL = "BITMAP_CONVERT_FAIL";

        // ─────────────────────────────────────────────────────────────────────────────
        // BIOMETRIC ERRORS
        // ─────────────────────────────────────────────────────────────────────────────
        public const string FACE_FAIL = "FACE_FAIL";
        public const string NO_FACE = "NO_FACE";
        public const string MULTI_FACE = "MULTI_FACE";
        public const string POOR_QUALITY = "POOR_QUALITY";
        public const string ENCODING_FAIL = "ENCODING_FAIL";
        public const string LIVENESS_FAIL = "LIVENESS_FAIL";
        public const string LIVENESS_REJECT = "LIVENESS_REJECT";
        public const string CIRCUIT_OPEN = "CIRCUIT_OPEN";
        public const string SESSION_STUCK = "SESSION_STUCK";
        public const string NO_SESSION = "NO_SESSION";
        public const string POOL_TIMEOUT = "POOL_TIMEOUT";

        // ─────────────────────────────────────────────────────────────────────────────
        // MATCHING ERRORS
        // ─────────────────────────────────────────────────────────────────────────────
        public const string NO_MATCH = "NO_MATCH";
        public const string AMBIGUOUS_MATCH = "AMBIGUOUS_MATCH";
        public const string LOW_CONFIDENCE = "LOW_CONFIDENCE";
        public const string MATCH_TIMEOUT = "MATCH_TIMEOUT";

        // ─────────────────────────────────────────────────────────────────────────────
        // EMPLOYEE ERRORS
        // ─────────────────────────────────────────────────────────────────────────────
        public const string EMPLOYEE_NOT_FOUND = "EMPLOYEE_NOT_FOUND";
        public const string EMPLOYEE_INACTIVE = "EMPLOYEE_INACTIVE";
        public const string EMPLOYEE_PENDING = "EMPLOYEE_PENDING";
        public const string EMPLOYEE_ID_EXISTS = "EMPLOYEE_ID_EXISTS";
        public const string EMPLOYEE_DUPLICATE = "EMPLOYEE_DUPLICATE";

        // ─────────────────────────────────────────────────────────────────────────────
        // DEVICE ERRORS
        // ─────────────────────────────────────────────────────────────────────────────
        public const string DEVICE_NOT_REGISTERED = "DEVICE_NOT_REGISTERED";
        public const string DEVICE_PENDING = "DEVICE_PENDING";
        public const string DEVICE_BLOCKED = "DEVICE_BLOCKED";
        public const string DEVICE_MISMATCH = "DEVICE_MISMATCH";
        public const string DEVICE_DISABLED = "DEVICE_DISABLED";

        // ─────────────────────────────────────────────────────────────────────────────
        // LOCATION ERRORS
        // ─────────────────────────────────────────────────────────────────────────────
        public const string GPS_NOT_AVAILABLE = "GPS_NOT_AVAILABLE";
        public const string GPS_INACCURATE = "GPS_INACCURATE";
        public const string GPS_OUTSIDE_OFFICE = "GPS_OUTSIDE_OFFICE";
        public const string GPS_SPOOF_DETECTED = "GPS_SPOOF_DETECTED";
        public const string GPS_REPEAT_COORDS = "GPS_REPEAT_COORDS";
        public const string OFFICE_NOT_FOUND = "OFFICE_NOT_FOUND";

        // ─────────────────────────────────────────────────────────────────────────────
        // AUTHENTICATION ERRORS
        // ─────────────────────────────────────────────────────────────────────────────
        public const string INVALID_PIN = "INVALID_PIN";
        public const string PIN_LOCKED = "PIN_LOCKED";
        public const string SESSION_EXPIRED = "SESSION_EXPIRED";
        public const string IP_NOT_ALLOWED = "IP_NOT_ALLOWED";
        public const string INVALID_TOTP = "INVALID_TOTP";
        public const string TOTP_REQUIRED = "TOTP_REQUIRED";
        public const string RECOVERY_CODE_USED = "RECOVERY_CODE_USED";

        // ─────────────────────────────────────────────────────────────────────────────
        // ATTENDANCE ERRORS
        // ─────────────────────────────────────────────────────────────────────────────
        public const string ATTENDANCE_DUPLICATE = "ATTENDANCE_DUPLICATE";
        public const string ATTENDANCE_GAP_TOO_SHORT = "ATTENDANCE_GAP_TOO_SHORT";
        public const string ATTENDANCE_ALREADY_OUT = "ATTENDANCE_ALREADY_OUT";
        public const string ATTENDANCE_ALREADY_IN = "ATTENDANCE_ALREADY_IN";

        // ─────────────────────────────────────────────────────────────────────────────
        // ENROLLMENT ERRORS
        // ─────────────────────────────────────────────────────────────────────────────
        public const string ENROLL_NO_GOOD_FRAME = "ENROLL_NO_GOOD_FRAME";
        public const string ENROLL_INSUFFICIENT_SAMPLES = "ENROLL_INSUFFICIENT_SAMPLES";
        public const string ENROLL_INSUFFICIENT_DIVERSITY = "ENROLL_INSUFFICIENT_DIVERSITY";
        public const string ENROLL_LOW_QUALITY_FRAMES = "ENROLL_LOW_QUALITY_FRAMES";
        public const string ENROLL_PENDING_APPROVAL = "ENROLL_PENDING_APPROVAL";
        public const string ENROLL_ALREADY_ACTIVE = "ENROLL_ALREADY_ACTIVE";

        // ─────────────────────────────────────────────────────────────────────────────
        // GENERAL ERRORS
        // ─────────────────────────────────────────────────────────────────────────────
        public const string UNKNOWN_ERROR = "UNKNOWN_ERROR";
        public const string DATABASE_ERROR = "DATABASE_ERROR";
        public const string TIMEOUT = "TIMEOUT";
        public const string NOT_IMPLEMENTED = "NOT_IMPLEMENTED";
        public const string ACCESS_DENIED = "ACCESS_DENIED";
        public const string INVALID_REQUEST = "INVALID_REQUEST";

        // ─────────────────────────────────────────────────────────────────────────────
        // VISITOR ERRORS
        // ─────────────────────────────────────────────────────────────────────────────
        public const string VISITOR_NOT_FOUND = "VISITOR_NOT_FOUND";
        public const string VISITOR_NOT_ACTIVE = "VISITOR_NOT_ACTIVE";
        public const string VISITOR_ALREADY_CHECKED_IN = "VISITOR_ALREADY_CHECKED_IN";

        /// <summary>
        /// Returns a user-friendly message for the given error code.
        /// </summary>
        public static string GetMessage(string errorCode)
        {
            if (string.IsNullOrWhiteSpace(errorCode))
                return "An unknown error occurred.";

            if (_messages.TryGetValue(errorCode, out var message))
                return message;

            return "An error occurred: " + errorCode;
        }

        private static readonly Dictionary<string, string> _messages = new Dictionary<string, string>
        {
            // Image errors
            { NO_IMAGE, "No image was provided." },
            { TOO_LARGE, "The image exceeds the maximum allowed size." },
            { INVALID_FORMAT, "Invalid image format. Please use JPG or PNG." },
            { IMAGE_LOAD_FAIL, "Failed to load the image." },
            { BITMAP_CONVERT_FAIL, "Failed to process the image." },

            // Biometric errors
            { FACE_FAIL, "Face detection failed." },
            { NO_FACE, "No face detected in the image." },
            { MULTI_FACE, "Multiple faces detected. Please ensure only one person is in frame." },
            { POOR_QUALITY, "Image quality is too poor for face recognition." },
            { ENCODING_FAIL, "Failed to encode the face." },
            { LIVENESS_FAIL, "Liveness detection failed." },
            { LIVENESS_REJECT, "Liveness check failed. Please try again with a live face." },
            { CIRCUIT_OPEN, "Liveness detection is temporarily unavailable." },
            { SESSION_STUCK, "Liveness detection session is stuck. Please try again later." },
            { NO_SESSION, "Liveness model not loaded." },
            { POOL_TIMEOUT, "Face recognition service timed out. Please try again." },

            // Matching errors
            { NO_MATCH, "No matching employee found." },
            { AMBIGUOUS_MATCH, "Multiple possible matches found. Please try again." },
            { LOW_CONFIDENCE, "Match confidence too low." },
            { MATCH_TIMEOUT, "Face matching timed out." },

            // Employee errors
            { EMPLOYEE_NOT_FOUND, "Employee not found." },
            { EMPLOYEE_INACTIVE, "Employee account is inactive." },
            { EMPLOYEE_PENDING, "Employee is pending approval." },
            { EMPLOYEE_ID_EXISTS, "Employee ID already exists." },
            { EMPLOYEE_DUPLICATE, "A similar face is already enrolled." },

            // Device errors
            { DEVICE_NOT_REGISTERED, "Device not registered." },
            { DEVICE_PENDING, "Device is pending approval." },
            { DEVICE_BLOCKED, "Device has been blocked." },
            { DEVICE_MISMATCH, "Device does not match the employee." },
            { DEVICE_DISABLED, "Device has been disabled." },

            // Location errors
            { GPS_NOT_AVAILABLE, "GPS location not available." },
            { GPS_INACCURATE, "GPS accuracy is too low." },
            { GPS_OUTSIDE_OFFICE, "You are outside the allowed office area." },
            { GPS_SPOOF_DETECTED, "Suspicious location detected." },
            { GPS_REPEAT_COORDS, "Location appears static. Please try again." },
            { OFFICE_NOT_FOUND, "Office not found." },

            // Auth errors
            { INVALID_PIN, "Invalid PIN." },
            { PIN_LOCKED, "Too many failed attempts. Please try again later." },
            { SESSION_EXPIRED, "Your session has expired. Please log in again." },
            { IP_NOT_ALLOWED, "Your IP address is not allowed." },
            { INVALID_TOTP, "Invalid verification code." },
            { TOTP_REQUIRED, "Two-factor authentication code required." },
            { RECOVERY_CODE_USED, "This recovery code has already been used." },

            // Attendance errors
            { ATTENDANCE_DUPLICATE, "Attendance already recorded." },
            { ATTENDANCE_GAP_TOO_SHORT, "Please wait before clocking in again." },
            { ATTENDANCE_ALREADY_OUT, "Already clocked out." },
            { ATTENDANCE_ALREADY_IN, "Already clocked in." },

            // Enrollment errors
            { ENROLL_NO_GOOD_FRAME, "No usable face frames captured." },
            { ENROLL_INSUFFICIENT_SAMPLES, "Need at least 3 good face samples." },
            { ENROLL_INSUFFICIENT_DIVERSITY, "Captured faces are too similar. Capture from different angles." },
            { ENROLL_LOW_QUALITY_FRAMES, "Captured frames have low quality." },
            { ENROLL_PENDING_APPROVAL, "Enrollment pending admin approval." },
            { ENROLL_ALREADY_ACTIVE, "Employee is already active." },

            // General errors
            { UNKNOWN_ERROR, "An unknown error occurred." },
            { DATABASE_ERROR, "Database operation failed." },
            { TIMEOUT, "Operation timed out." },
            { NOT_IMPLEMENTED, "This feature is not implemented." },
            { ACCESS_DENIED, "Access denied." },
            { INVALID_REQUEST, "Invalid request." },

            // Visitor errors
            { VISITOR_NOT_FOUND, "Visitor not found." },
            { VISITOR_NOT_ACTIVE, "Visitor is not active." },
            { VISITOR_ALREADY_CHECKED_IN, "Visitor already checked in." }
        };
    }
}