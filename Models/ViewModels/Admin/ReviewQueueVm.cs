using System;
using System.Collections.Generic;
using System.Web.Mvc;

namespace FaceAttend.Models.ViewModels.Admin
{
    public class ReviewQueueVm
    {
        public string From { get; set; }
        public string To { get; set; }
        public int? OfficeId { get; set; }
        public string Employee { get; set; }
        public string Reason { get; set; }
        public string Status { get; set; }
        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 25;
        public int Total { get; set; }
        public int PendingTotal { get; set; }
        public int TotalPages => PageSize <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling((double)Total / PageSize));
        public string ActiveRangeLabel { get; set; }
        public List<SelectListItem> OfficeOptions { get; set; } = new List<SelectListItem>();
        public List<ReviewQueueRowVm> Rows { get; set; } = new List<ReviewQueueRowVm>();
    }

    public class ReviewQueueRowVm
    {
        public long Id { get; set; }
        public DateTime TimestampLocal { get; set; }
        public string EmployeeId { get; set; }
        public string EmployeeFullName { get; set; }
        public string Department { get; set; }
        public string OfficeName { get; set; }
        public string EventType { get; set; }
        public double? AntiSpoofScore { get; set; }
        public double? FaceDistance { get; set; }
        public double? GPSAccuracy { get; set; }
        public string ReviewStatus { get; set; }
        public string ReviewReasonCodes { get; set; }
        public string Notes { get; set; }
    }
}
