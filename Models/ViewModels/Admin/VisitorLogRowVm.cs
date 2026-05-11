using System;

namespace FaceAttend.Models.ViewModels.Admin
{
    public class VisitorLogRowVm
    {
        public DateTime TimestampLocal { get; set; }
        public int? VisitorId { get; set; }
        public string VisitorName { get; set; }
        public string Purpose { get; set; }
        public string Source { get; set; }
        public string OfficeName { get; set; }

        public bool IsKnown => VisitorId.HasValue;
    }
}
