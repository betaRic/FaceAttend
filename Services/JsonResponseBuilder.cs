using System;
using System.Collections.Generic;
using System.Web.Mvc;

namespace FaceAttend.Services
{
    public static class JsonResponseBuilder
    {
        private static JsonResult Json(object data)
        {
            return new JsonResult
            {
                Data = data,
                JsonRequestBehavior = JsonRequestBehavior.AllowGet
            };
        }

        public static JsonResult Success(object data = null, string message = null)
        {
            return Json(new { ok = true, data, message });
        }

        public static JsonResult Error(string errorCode, string message = null,
            int? retryAfter = null, object details = null)
        {
            return Json(new { ok = false, error = errorCode, message, retryAfter, details });
        }

        public static JsonResult NotFound(string entityName)
        {
            return Json(new { ok = false, error = "NOT_FOUND", message = entityName + " not found" });
        }

        public static JsonResult SystemBusy(int retryAfterSeconds = 2)
        {
            return Json(new
            {
                ok = false,
                error = "SYSTEM_BUSY",
                message = "System is busy. Please try again in a few seconds.",
                retryAfter = retryAfterSeconds
            });
        }

        public static JsonResult ErrorWithTimings(string errorCode,
            IDictionary<string, long> timings, bool includeTimings, string message = null)
        {
            return Json(new
            {
                ok = false,
                error = errorCode,
                message,
                timings = includeTimings ? timings : null
            });
        }

        public static JsonResult AntiSpoofFail(float score, float threshold, string decision,
            IDictionary<string, long> timings = null, bool includeTimings = false)
        {
            return Json(new
            {
                ok = false,
                error = "ANTI_SPOOF_FAIL",
                antiSpoofScore = score,
                threshold,
                decision,
                timings = includeTimings ? timings : null
            });
        }

        public static JsonResult AntiSpoofRetry(float score, float threshold,
            IDictionary<string, long> timings = null, bool includeTimings = false)
        {
            return Json(new
            {
                ok = false,
                error = "ANTI_SPOOF_RETRY_NEEDED",
                message = "Please scan again with your face clear and the screen steady.",
                antiSpoofScore = score,
                threshold,
                retryAfter = 1,
                timings = includeTimings ? timings : null
            });
        }

        public static JsonResult EncodingFail(string detail,
            IDictionary<string, long> timings = null, bool includeTimings = false, bool debug = false)
        {
            return Json(new
            {
                ok = false,
                error = "ENCODING_FAIL",
                detail = debug ? detail : null,
                timings = includeTimings ? timings : null
            });
        }

        public static JsonResult TooSoon(string message, int? minGapSeconds = null,
            IDictionary<string, long> timings = null, bool includeTimings = false)
        {
            return Json(new
            {
                ok = false,
                error = "TOO_SOON",
                message,
                minGapSeconds,
                timings = includeTimings ? timings : null
            });
        }

        public static JsonResult AttendanceSuccess(
            string employeeId, string name, string displayName, string eventType,
            string message, int officeId, string officeName, float antiSpoofScore,
            double distance, DateTime attemptedAtLocal,
            IDictionary<string, long> timings = null, bool includeTimings = false,
            object attendanceAccess = null,
            object recognition = null)
        {
            return Json(new
            {
                ok = true,
                employeeId,
                name,
                displayName,
                eventType,
                message,
                officeId,
                officeName,
                antiSpoofScore,
                distance,
                attemptedAtLocal,
                attendanceAccess,
                recognition,
                timings = includeTimings ? timings : null
            });
        }

        public static JsonResult VisitorScan(string scanId, bool isKnown,
            string visitorName, double? distance, double threshold, float antiSpoofScore,
            IDictionary<string, long> timings = null, bool includeTimings = false)
        {
            return Json(new
            {
                ok = true,
                mode = "VISITOR",
                scanId,
                isKnown,
                visitorName = isKnown ? visitorName : null,
                distance = double.IsInfinity(distance ?? double.PositiveInfinity) ? (double?)null : distance,
                threshold,
                antiSpoofScore,
                timings = includeTimings ? timings : null
            });
        }

        public static JsonResult SelfEnrollOffer(string fingerprint, string message)
        {
            return Json(new
            {
                ok = false,
                action = "SELF_ENROLL",
                fingerprint,
                message
            });
        }

        public static JsonResult SuspiciousLocation(string message,
            IDictionary<string, long> timings = null, bool includeTimings = false)
        {
            return Json(new
            {
                ok = false,
                error = "SUSPICIOUS_LOCATION",
                message,
                timings = includeTimings ? timings : null
            });
        }

        public static JsonResult ScanError(string detail = null, string inner = null,
            IDictionary<string, long> timings = null, bool includeTimings = false, bool debug = false)
        {
            return Json(new
            {
                ok = false,
                error = "SCAN_ERROR",
                detail = debug ? detail : null,
                inner = debug ? inner : null,
                timings = includeTimings ? timings : null
            });
        }

        public static JsonResult OfficeResolved(bool allowed, bool gpsRequired,
            int? officeId = null, string officeName = null, string reason = null,
            int? requiredAccuracy = null, double? accuracy = null)
        {
            return Json(new
            {
                ok = true,
                gpsRequired,
                allowed,
                officeId,
                officeName,
                reason,
                requiredAccuracy,
                accuracy
            });
        }

        public static JsonResult RequestTimeout(
            IDictionary<string, long> timings = null, bool includeTimings = false)
        {
            return Json(new
            {
                ok = false,
                error = "REQUEST_TIMEOUT",
                message = "Request timed out. Please try again.",
                retryAfter = 2,
                timings = includeTimings ? timings : null
            });
        }

        public static JsonResult NoOffices(
            IDictionary<string, long> timings = null, bool includeTimings = false)
        {
            return Json(new
            {
                ok = false,
                error = "NO_OFFICES",
                message = "No office configured. Please contact your administrator.",
                timings = includeTimings ? timings : null
            });
        }

        public static JsonResult NotRecognized(
            IDictionary<string, long> timings = null, bool includeTimings = false)
        {
            return Json(new
            {
                ok = false,
                error = "NOT_RECOGNIZED",
                message = "Face not recognized. Please check if you're enrolled.",
                timings = includeTimings ? timings : null
            });
        }
    }
}
