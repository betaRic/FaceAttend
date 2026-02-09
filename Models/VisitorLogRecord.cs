using System;

namespace FaceAttend.Models
{
    public class VisitorLogRecord
    {
        public string Id { get; set; }              // GUID string
        public string Name { get; set; }            // visitor name
        public string Purpose { get; set; }         // optional
        public string Source { get; set; }          // "KIOSK"
        public string CreatedUtc { get; set; }      // ISO-8601
        public string ClientIp { get; set; }        // best-effort
        public string UserAgent { get; set; }       // best-effort
    }
}
