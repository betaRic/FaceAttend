using System;

namespace FaceAttend.Models
{
    public class AttendanceLogRecord
    {
        public string Id { get; set; }
        public string EmployeeId { get; set; }
        public string Source { get; set; }
        public string CreatedUtc { get; set; }

        public double? Distance { get; set; }
        public double? Similarity { get; set; }

        public string ClientIp { get; set; }
        public string UserAgent { get; set; }
    }
}
