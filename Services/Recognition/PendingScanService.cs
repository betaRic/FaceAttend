using System;
using System.Runtime.Caching;
using FaceAttend.Services;

namespace FaceAttend.Services.Recognition
{
    /// <summary>
    /// Manages the MEDIUM-tier pending scan cache.
    /// A MEDIUM match requires two consecutive frames from the same device to agree
    /// before attendance is recorded. This service stores the first-frame result and
    /// retrieves it when the second frame arrives.
    /// </summary>
    public static class PendingScanService
    {
        private const string Prefix = "PENDINGSCAN::";
        private static readonly MemoryCache _cache = MemoryCache.Default;

        public class Entry
        {
            public string   EmployeeId   { get; set; }
            public int      EmployeeDbId { get; set; }
            public double   Distance     { get; set; }
            public float    AntiSpoof     { get; set; }
            public double   AmbiguityGap { get; set; }
            public int      OfficeId     { get; set; }
            public string   OfficeName   { get; set; }
            public string   DeviceKey    { get; set; }
            public DateTime CreatedUtc   { get; set; }
        }

        private static int GetTtlSeconds()
        {
            var s = ConfigurationService.GetInt("Kiosk:PendingScanTtlSeconds", 8);
            return s < 3 ? 3 : (s > 30 ? 30 : s);
        }

        /// <summary>Stores a pending first-frame result keyed by device identifier.</summary>
        public static void Store(string deviceKey, Entry entry)
        {
            _cache.Set(
                Prefix + deviceKey,
                entry,
                DateTimeOffset.UtcNow.AddSeconds(GetTtlSeconds()));
        }

        /// <summary>Returns the pending entry for a device, or null if not found/expired.</summary>
        public static Entry Get(string deviceKey)
            => string.IsNullOrEmpty(deviceKey) ? null : _cache.Get(Prefix + deviceKey) as Entry;

        /// <summary>Consumes (removes) the pending entry after it has been resolved.</summary>
        public static void Remove(string deviceKey)
        {
            if (!string.IsNullOrEmpty(deviceKey))
                _cache.Remove(Prefix + deviceKey);
        }
    }
}
