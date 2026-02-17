using System;
using System.ComponentModel.DataAnnotations;

namespace FaceAttend.Areas.Admin.Models
{
    public class AttendanceDetailsVm
    {
        public long Id { get; set; }
        public DateTime TimestampUtc { get; set; }
        public string EventType { get; set; }
        public string Source { get; set; }

        public string EmployeeId { get; set; }
        public string EmployeeFullName { get; set; }
        public string Department { get; set; }

        public int OfficeId { get; set; }
        public string OfficeName { get; set; }
        public string OfficeType { get; set; }

        public double? GPSLatitude { get; set; }
        public double? GPSLongitude { get; set; }
        public double? GPSAccuracy { get; set; }
        public bool LocationVerified { get; set; }
        public string LocationError { get; set; }

        public double? FaceDistance { get; set; }
        public double? FaceSimilarity { get; set; }
        public double? MatchThreshold { get; set; }

        public double? LivenessScore { get; set; }
        public string LivenessResult { get; set; }
        public string LivenessError { get; set; }

        public string ClientIP { get; set; }
        public string UserAgent { get; set; }

        public bool NeedsReview { get; set; }

        [Display(Name = "Notes")]
        public string Notes { get; set; }
    }
}
