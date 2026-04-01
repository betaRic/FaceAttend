using System;
using System.Runtime.Caching;
using FaceAttend.Services;

namespace FaceAttend.Services.Recognition
{
    /// <summary>
    /// Manages the short-lived visitor scan cache used to bridge the kiosk scan
    /// result and the visitor form submission.
    /// </summary>
    public static class VisitorScanService
    {
        private const string Prefix = "VISITORSCAN::";
        private static readonly MemoryCache _cache = MemoryCache.Default;

        public class Entry
        {
            public double[] Vec            { get; set; }
            public int      OfficeId       { get; set; }
            public int?     VisitorId      { get; set; }
            public string   VisitorName    { get; set; }
            public string   SessionBinding { get; set; }
        }

        private static int GetTtlSeconds()
        {
            var s = ConfigurationService.GetInt("Kiosk:VisitorScanTtlSeconds", 180);
            return s < 30 ? 30 : s;
        }

        /// <summary>
        /// Stores a visitor scan result and returns the opaque scan ID for the client.
        /// </summary>
        public static string Store(double[] vec, int officeId, int? visitorId, string visitorName, string sessionBinding)
        {
            var scanId = Guid.NewGuid().ToString("N");
            _cache.Set(
                Prefix + scanId,
                new Entry
                {
                    Vec            = vec,
                    OfficeId       = officeId,
                    VisitorId      = visitorId,
                    VisitorName    = visitorName,
                    SessionBinding = sessionBinding
                },
                DateTimeOffset.UtcNow.AddSeconds(GetTtlSeconds()));
            return scanId;
        }

        /// <summary>Returns the cached entry, or null if expired/not found.</summary>
        public static Entry Get(string scanId)
            => string.IsNullOrEmpty(scanId) ? null : _cache.Get(Prefix + scanId) as Entry;

        /// <summary>Removes the cached entry after it has been consumed.</summary>
        public static void Remove(string scanId)
        {
            if (!string.IsNullOrEmpty(scanId))
                _cache.Remove(Prefix + scanId);
        }
    }
}
