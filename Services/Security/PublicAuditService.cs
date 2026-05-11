using System;
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
            var ok = ObjectValueReader.GetBool(data, "ok");
            var employeeId = ObjectValueReader.GetString(data, "employeeId", 100);
            var eventType = ObjectValueReader.GetString(data, "eventType", 100);
            var error = ObjectValueReader.GetString(data, "error", 100)
                ?? ObjectValueReader.GetString(data, "action", 100);

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

    }
}
