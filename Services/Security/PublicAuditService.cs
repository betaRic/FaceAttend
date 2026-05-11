using System;
using System.Reflection;
using System.Web;
using System.Web.Mvc;
using FaceAttend.Services.Biometrics;
using FaceAttend.Services.Helpers;

namespace FaceAttend.Services.Security
{
    public static class PublicAuditService
    {
        public static void RecordScan(HttpRequestBase request, ActionResult result, string source, long durationMs)
        {
            if (!ConfigurationService.GetBool("Security:AuditPublicScanEvents", true))
                return;

            var json = result as JsonResult;
            var data = json?.Data;
            var ok = GetBool(data, "ok");
            var employeeId = GetString(data, "employeeId");
            var eventType = GetString(data, "eventType");
            var error = GetString(data, "error") ?? GetString(data, "action");

            Log(
                request,
                ok ? AuditHelper.ActionAttendanceScanSuccess : AuditHelper.ActionAttendanceScanFail,
                "AttendanceScan",
                ok ? employeeId : error,
                ok ? "Public attendance scan accepted." : "Public attendance scan rejected.",
                new
                {
                    source,
                    ok,
                    employeeId,
                    eventType,
                    error,
                    durationMs,
                    modelVersion = BiometricPolicy.Current.ModelVersion
                });
        }

        public static void RecordEnrollmentSubmitted(
            HttpRequestBase request,
            string employeeId,
            int employeeDbId,
            int savedVectors,
            bool isMobile)
        {
            Log(
                request,
                AuditHelper.ActionPublicEnrollmentSubmitted,
                "Employee",
                employeeId,
                "Public employee enrollment submitted for admin approval.",
                new
                {
                    employeeDbId,
                    employeeId,
                    savedVectors,
                    isMobile,
                    status = "PENDING",
                    modelVersion = BiometricPolicy.Current.ModelVersion
                });
        }

        public static void RecordMonthlyAccess(HttpRequestBase request, string employeeId, long attendanceLogId, bool exported)
        {
            Log(
                request,
                exported ? AuditHelper.ActionAttendanceSelfExport : AuditHelper.ActionAttendanceSelfView,
                "Employee",
                employeeId,
                exported
                    ? "Employee exported own current-month attendance after fresh scan."
                    : "Employee viewed own current-month attendance after fresh scan.",
                new
                {
                    employeeId,
                    attendanceLogId,
                    exported
                });
        }

        private static void Log(
            HttpRequestBase request,
            string action,
            string entityType,
            object entityId,
            string description,
            object values)
        {
            try
            {
                using (var db = new FaceAttendDBEntities())
                {
                    AuditHelper.Log(
                        db,
                        request,
                        action,
                        entityType,
                        entityId,
                        description,
                        null,
                        values);
                }
            }
            catch
            {
                // AuditHelper is already non-throwing; this protects DB construction failures.
            }
        }

        private static bool GetBool(object source, string name)
        {
            var value = GetValue(source, name);
            return value is bool && (bool)value;
        }

        private static string GetString(object source, string name)
        {
            var value = GetValue(source, name);
            var text = value == null ? null : Convert.ToString(value);
            return StringHelper.Truncate(string.IsNullOrWhiteSpace(text) ? null : text, 100);
        }

        private static object GetValue(object source, string name)
        {
            if (source == null || string.IsNullOrWhiteSpace(name))
                return null;

            var prop = source.GetType().GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            return prop == null ? null : prop.GetValue(source, null);
        }
    }
}
