using System;
using System.Diagnostics;
using System.Web;
using Newtonsoft.Json;
using FaceAttend.Services.Helpers;

namespace FaceAttend.Services
{
    /// <summary>
    /// Centralized admin audit writer.
    /// Non-throwing ito: kapag pumalya ang audit write, hindi nito ibabagsak
    /// ang main admin action. Importanteng may best-effort trail tayo pero
    /// hindi dapat masira ang actual na operation dahil lang sa audit insert.
    /// </summary>
    public static class AuditHelper
    {
        // Employee actions
        public const string ActionEmployeeCreate     = "EMPLOYEE_CREATE";
        public const string ActionEmployeeEdit       = "EMPLOYEE_EDIT";
        public const string ActionEmployeeDeactivate = "EMPLOYEE_DEACTIVATE";
        public const string ActionEmployeeApprove    = "EMPLOYEE_APPROVE";
        public const string ActionEmployeeReject    = "EMPLOYEE_REJECT";
        
        // Office actions
        public const string ActionOfficeCreate        = "OFFICE_CREATE";
        public const string ActionOfficeEdit          = "OFFICE_EDIT";
        public const string ActionOfficeDelete        = "OFFICE_DELETE";
        public const string ActionOfficeScheduleEdit  = "OFFICE_SCHEDULE_EDIT";
        public const string ActionOfficeBulkSchedule  = "OFFICE_BULK_SCHEDULE";
        
        // Security actions
        public const string ActionSettingChange      = "SETTING_CHANGE";
        public const string ActionFaceEnroll         = "FACE_ENROLL";
        public const string ActionLogin              = "LOGIN";
        public const string ActionLoginFailed        = "LOGIN_FAILED";
        public const string ActionLogout             = "LOGOUT";
        public const string ActionTotpEnable         = "TOTP_ENABLE";
        public const string ActionTotpDisable        = "TOTP_DISABLE";
        public const string ActionFaceCacheReload    = "FACE_CACHE_RELOAD";
        
        // Device actions
        public const string ActionDeviceApprove      = "DEVICE_APPROVE";
        public const string ActionDeviceReject       = "DEVICE_REJECT";
        public const string ActionDeviceDisable      = "DEVICE_DISABLE";
        
        // Attendance actions
        public const string ActionAttendanceExport   = "ATTENDANCE_EXPORT";
        public const string ActionAttendanceReview   = "ATTENDANCE_REVIEW";
        
        // Visitor actions
        public const string ActionVisitorCreate      = "VISITOR_CREATE";
        public const string ActionVisitorDelete      = "VISITOR_DELETE";
        public const string ActionVisitorLogCreate   = "VISITOR_LOG";

        public static string GetActorIp(HttpRequestBase request)
        {
            var ip = request != null ? (request.UserHostAddress ?? "").Trim() : "";
            return string.IsNullOrWhiteSpace(ip) ? "ADMIN" : ip;
        }

        public static void Log(
            FaceAttendDBEntities db,
            HttpRequestBase request,
            string action,
            string entityType,
            object entityId,
            string description,
            object oldValues = null,
            object newValues = null)
        {
            Log(db, GetActorIp(request), action, entityType, entityId, description, oldValues, newValues);
        }

        public static void Log(
            FaceAttendDBEntities db,
            string adminIp,
            string action,
            string entityType,
            object entityId,
            string description,
            object oldValues = null,
            object newValues = null)
        {
            if (db == null) return;

            try
            {
                var row = new AdminAuditLog
                {
                    Timestamp   = DateTime.UtcNow,
                    AdminIp     = StringHelper.Truncate(adminIp, 100),
                    Action      = StringHelper.Truncate(action, 100),
                    EntityType  = StringHelper.Truncate(entityType, 100),
                    EntityId    = StringHelper.Truncate(entityId == null ? null : Convert.ToString(entityId), 100),
                    Description = StringHelper.Truncate(description, 1000),
                    OldValues   = ToJson(oldValues),
                    NewValues   = ToJson(newValues)
                };

                db.AdminAuditLogs.Add(row);
                db.SaveChanges();
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("[AuditHelper] Audit write failed: " + ex.Message);
            }
        }

        private static string ToJson(object value)
        {
            if (value == null) return null;

            try
            {
                return JsonConvert.SerializeObject(value);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("[AuditHelper] JSON serialize failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Logs admin login success.
        /// </summary>
        public static void LogLogin(FaceAttendDBEntities db, string adminIp, bool success, string reason = null)
        {
            if (db == null) return;
            try
            {
                var row = new AdminAuditLog
                {
                    Timestamp   = DateTime.UtcNow,
                    AdminIp     = StringHelper.Truncate(adminIp, 100),
                    Action      = success ? ActionLogin : ActionLoginFailed,
                    EntityType  = "Admin",
                    EntityId    = "SESSION",
                    Description = success 
                        ? "Admin logged in successfully" 
                        : ("Login failed: " + (reason ?? "unknown reason"))
                };
                db.AdminAuditLogs.Add(row);
                db.SaveChanges();
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("[AuditHelper] Login audit write failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Logs admin logout.
        /// </summary>
        public static void LogLogout(FaceAttendDBEntities db, string adminIp)
        {
            if (db == null) return;
            try
            {
                var row = new AdminAuditLog
                {
                    Timestamp   = DateTime.UtcNow,
                    AdminIp     = StringHelper.Truncate(adminIp, 100),
                    Action      = ActionLogout,
                    EntityType  = "Admin",
                    EntityId    = "SESSION",
                    Description = "Admin logged out"
                };
                db.AdminAuditLogs.Add(row);
                db.SaveChanges();
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("[AuditHelper] Logout audit write failed: " + ex.Message);
            }
        }

    }
}
