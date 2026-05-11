using System;
using System.Collections.Generic;
using FaceAttend.Services;

namespace FaceAttend.Models.ViewModels.Admin
{
    /// <summary>
    /// ViewModel para sa Admin Dashboard page.
    ///
    /// Admin dashboard data.
    /// </summary>
    public class DashboardViewModel
    {
        // ── KPI cards ────────────────────────────────────────────────────────
        public int TotalEmployees { get; set; }
        public int TodayTimeIns   { get; set; }
        public int TodayTimeOuts  { get; set; }
        public int TotalVisitors  { get; set; }
        public int PendingReviews { get; set; }

        // ── Recent activity ──────────────────────────────────────────────────
        public List<RecentAttendanceRow> RecentLogs { get; set; } = new List<RecentAttendanceRow>();

        // ── System health ────────────────────────────────────────────────────
        public bool DatabaseHealthy    { get; set; }
        public bool BiometricWorkerReady   { get; set; }
        public bool OfflineAssetsOk    { get; set; } = true;

        // ── Display helpers ──────────────────────────────────────────────────
        public string TodayDateDisplay => TimeZoneHelper.NowLocal().ToString("dddd, MMM dd, yyyy");
    }

    /// <summary>
    /// Slim na row object para sa "Recent Attendance" table sa dashboard.
    /// Hindi ini-load ang buong AttendanceLog entity para makatipid sa memory.
    /// </summary>
    public class RecentAttendanceRow
    {
        public long     Id               { get; set; }
        public DateTime TimestampLocal   { get; set; }
        public string   TimestampLocalDisplay =>
            TimestampLocal.ToString("HH:mm");

        public string EmployeeId       { get; set; }
        public string EmployeeFullName { get; set; }
        public string EventType        { get; set; }
        public string OfficeName       { get; set; }
        public bool   NeedsReview      { get; set; }
    }
}
