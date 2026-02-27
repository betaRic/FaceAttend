using System;

namespace FaceAttend.Areas.Admin.Models
{
    public class VisitorLogRowVm
    {
        public DateTime TimestampUtc { get; set; }
        public int? VisitorId { get; set; }
        public string VisitorName { get; set; }
        public string Purpose { get; set; }
        public string Source { get; set; }
        public string OfficeName { get; set; }

        public bool IsKnown => VisitorId.HasValue;
    }
}
