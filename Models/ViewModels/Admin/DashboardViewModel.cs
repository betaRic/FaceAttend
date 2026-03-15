using System;
using System.Collections.Generic;
using FaceAttend.Services;

namespace FaceAttend.Models.ViewModels.Admin
{
    /// <summary>
    /// ViewModel para sa Admin Dashboard page.
    ///
    /// PHASE 2 FIX (P-03, WC-07): Dinagdagan ang circuit breaker status fields
    /// para makita ng admin sa dashboard kung naka-open ang liveness circuit.
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
        public bool LivenessModelLoaded { get; set; }
        public bool DlibModelsLoaded   { get; set; }
        public bool OfflineAssetsOk    { get; set; } = true;

        // PHASE 2 FIX (WC-07): Circuit breaker status
        // Kung true ang LivenessCircuitOpen, ang liveness check ay hindi tumatakbo
        // at lahat ng scan attempts ay magfa-fail. Kailangan ng admin action para
        // i-reset (tingnan ang Dashboard > Reset Circuit button).
        public bool LivenessCircuitOpen  { get; set; }
        public bool LivenessCircuitStuck { get; set; }

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
        public DateTime TimestampUtc     { get; set; }
        public string   TimestampLocalDisplay =>
            TimeZoneHelper.UtcToLocal(TimestampUtc).ToString("HH:mm");

        public string EmployeeId       { get; set; }
        public string EmployeeFullName { get; set; }
        public string EventType        { get; set; }
        public string OfficeName       { get; set; }
        public bool   NeedsReview      { get; set; }
    }
}
