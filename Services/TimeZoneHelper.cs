using System;
using System.Collections.Generic;

namespace FaceAttend.Services
{
    public static class TimeZoneHelper
    {
        private static readonly object _lock = new object();
        private static TimeZoneInfo _tz;
        private static string _loadedId;

        public static TimeZoneInfo LocalTimeZone => Ensure();

        private static TimeZoneInfo Ensure()
        {
            var id = AppSettings.GetString("App:TimeZoneId", "Asia/Manila");

            if (_tz != null && string.Equals(_loadedId, id, StringComparison.OrdinalIgnoreCase))
                return _tz;

            lock (_lock)
            {
                if (_tz != null && string.Equals(_loadedId, id, StringComparison.OrdinalIgnoreCase))
                    return _tz;

                _tz = ResolveTimeZone(id);
                _loadedId = id;
                return _tz;
            }
        }

        private static TimeZoneInfo ResolveTimeZone(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) id = "Asia/Manila";
            id = id.Trim();

            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch { }

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Asia/Manila", "Singapore Standard Time" },
                { "Asia/Singapore", "Singapore Standard Time" },
                { "Etc/UTC", "UTC" },
                { "UTC", "UTC" }
            };

            if (map.TryGetValue(id, out var winId))
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
            var startLocal = DateTime.SpecifyKind(localDate.Date, DateTimeKind.Unspecified);
            var endLocal = DateTime.SpecifyKind(localDate.Date.AddDays(1), DateTimeKind.Unspecified);

            var startUtc = TimeZoneInfo.ConvertTimeToUtc(startLocal, Ensure());
            var endUtc = TimeZoneInfo.ConvertTimeToUtc(endLocal, Ensure());

            return (startUtc, endUtc);
        }
    }
}