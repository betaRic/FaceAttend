using System;
using System.Collections.Generic;

namespace FaceAttend.Areas.Admin.Models
{
    public class DashboardViewModel
    {
        // KPI cards
        public int TotalEmployees { get; set; }
        public int TodayTimeIns { get; set; }
        public int TodayTimeOuts { get; set; }
        public int TotalVisitors { get; set; }
        public int PendingReviews { get; set; }

        // Recent activity
        public List<RecentAttendanceRow> RecentLogs { get; set; } = new List<RecentAttendanceRow>();

        // System health
        public bool DatabaseHealthy { get; set; }
        public bool LivenessModelLoaded { get; set; }
        public bool DlibModelsLoaded { get; set; }
        public bool OfflineAssetsOk { get; set; } = true;

        // Display helpers
        public string TodayDateDisplay => DateTime.Now.ToString("dddd, MMM dd, yyyy");
        public int TotalTodayScans => TodayTimeIns + TodayTimeOuts;
    }

    public class RecentAttendanceRow
    {
        public long Id { get; set; }
        public DateTime TimestampUtc { get; set; }
        public string TimestampLocalDisplay => TimestampUtc.ToLocalTime().ToString("HH:mm");

        public string EmployeeId { get; set; }
        public string EmployeeFullName { get; set; }
        public string EventType { get; set; }
        public string OfficeName { get; set; }
        public bool NeedsReview { get; set; }

        public string EventTypeBadgeClass
        {
            get
            {
                if (string.Equals(EventType, "IN", StringComparison.OrdinalIgnoreCase)) return "bg-success";
                if (string.Equals(EventType, "OUT", StringComparison.OrdinalIgnoreCase)) return "bg-warning";
                return "bg-secondary";
            }
        }
    }
}
