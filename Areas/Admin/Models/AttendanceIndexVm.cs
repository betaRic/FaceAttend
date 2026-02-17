using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Web.Mvc;

namespace FaceAttend.Areas.Admin.Models
{
    public class AttendanceRowVm
    {
        public long Id { get; set; }
        public DateTime TimestampUtc { get; set; }
        public string EmployeeId { get; set; }
        public string EmployeeFullName { get; set; }
        public string Department { get; set; }
        public string OfficeName { get; set; }
        public string EventType { get; set; }
        public double? LivenessScore { get; set; }
        public double? FaceDistance { get; set; }
        public bool LocationVerified { get; set; }
        public bool NeedsReview { get; set; }
    }

    public class AttendanceIndexVm
    {
        // Filters (query string)
        [Display(Name = "From")]
        public string From { get; set; }

        [Display(Name = "To")]
        public string To { get; set; }

        [Display(Name = "Office")]
        public int? OfficeId { get; set; }

        [Display(Name = "Employee")]
        public string Employee { get; set; }

        [Display(Name = "Type")]
        public string EventType { get; set; }

        [Display(Name = "Needs review")]
        public bool NeedsReviewOnly { get; set; }

        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 25;

        // Output
        public int Total { get; set; }
        public int TotalNeedsReview { get; set; }

        public List<SelectListItem> OfficeOptions { get; set; } = new List<SelectListItem>();
        public List<AttendanceRowVm> Rows { get; set; } = new List<AttendanceRowVm>();

        public int TotalPages
        {
            get
            {
                if (PageSize <= 0) return 1;
                var pages = (int)Math.Ceiling((double)Total / PageSize);
                return pages <= 0 ? 1 : pages;
            }
        }

        public string ActiveRangeLabel { get; set; }
    }
}
