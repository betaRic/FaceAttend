using System;
using System.Collections.Generic;

namespace FaceAttend.Services
{
    /// <summary>
    /// Central timezone helper for the whole app.
    ///
    /// Lahat ng business date boundaries dapat dumaan dito para iisa lang ang
    /// batayan ng "today", reports, exports, at attendance day cutoffs.
    /// </summary>
    public static class TimeZoneHelper
    {
        private static readonly object _lock = new object();

        // Volatile references para safe ang fast-path reads kahit may concurrent rebuild.
        private static volatile TimeZoneInfo _tz;
        private static volatile string _loadedId;

        public static TimeZoneInfo LocalTimeZone => Ensure();

        private static TimeZoneInfo Ensure()
        {
            var id = NormalizeId(AppSettings.GetString("App:TimeZoneId", "Asia/Manila"));

            var tz = _tz;
            var loadedId = _loadedId;

            if (tz != null && string.Equals(loadedId, id, StringComparison.OrdinalIgnoreCase))
                return tz;

            lock (_lock)
            {
                tz = _tz;
                loadedId = _loadedId;

                if (tz != null && string.Equals(loadedId, id, StringComparison.OrdinalIgnoreCase))
                    return tz;

                tz = ResolveTimeZone(id);
                _tz = tz;
                _loadedId = id;
                return tz;
            }
        }

        private static string NormalizeId(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return "Asia/Manila";

            return id.Trim();
        }

        private static TimeZoneInfo ResolveTimeZone(string id)
        {
            id = NormalizeId(id);

            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch { }

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Asia/Manila", "Singapore Standard Time" },
                { "Asia/Singapore", "Singapore Standard Time" },
                { "Etc/UTC", "UTC" },
                { "UTC", "UTC" }
            };

            string winId;
            if (map.TryGetValue(id, out winId))
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(winId); }
                catch { }
            }

            return TimeZoneInfo.Local;
        }

        public static DateTime NowLocal()
            => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Ensure());

        public static DateTime TodayLocalDate()
            => NowLocal().Date;

        public static DateTime UtcToLocal(DateTime utc)
        {
            if (utc.Kind != DateTimeKind.Utc)
                utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);

            return TimeZoneInfo.ConvertTimeFromUtc(utc, Ensure());
        }

        public static DateTime LocalToUtc(DateTime local)
        {
            local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            return TimeZoneInfo.ConvertTimeToUtc(local, Ensure());
        }

        public static (DateTime fromUtc, DateTime toUtcExclusive) LocalDateToUtcRange(DateTime localDate)
        {
            // Taglish note:
            // localDate dito ay "calendar day" lang. Gawing Unspecified para
            // ang ConvertTimeToUtc ay gumamit ng configured app timezone.
            var startLocal = DateTime.SpecifyKind(localDate.Date, DateTimeKind.Unspecified);
            var endLocal = DateTime.SpecifyKind(localDate.Date.AddDays(1), DateTimeKind.Unspecified);

            var startUtc = TimeZoneInfo.ConvertTimeToUtc(startLocal, Ensure());
            var endUtc = TimeZoneInfo.ConvertTimeToUtc(endLocal, Ensure());

            return (startUtc, endUtc);
        }
    }
}
