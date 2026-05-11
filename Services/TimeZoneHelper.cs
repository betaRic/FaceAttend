using System;
using System.Collections.Generic;

namespace FaceAttend.Services
{
    public static class TimeZoneHelper
    {
        private static readonly object _lock = new object();
        private static volatile TimeZoneInfo _tz;
        private static volatile string _loadedId;

        public static TimeZoneInfo LocalTimeZone => Ensure();

        private static TimeZoneInfo Ensure()
        {
            var id = NormalizeId(ConfigurationService.GetString("App:TimeZoneId", "Asia/Manila"));

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
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }

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
                catch (TimeZoneNotFoundException) { }
                catch (InvalidTimeZoneException) { }
            }

            return TimeZoneInfo.Local;
        }

        public static DateTime NowLocal()
            => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Ensure());

        public static DateTime TodayLocalDate()
            => NowLocal().Date;

        public static (DateTime fromLocalInclusive, DateTime toLocalExclusive) LocalDateRange(DateTime localDate)
        {
            var startLocal = DateTime.SpecifyKind(localDate.Date, DateTimeKind.Unspecified);
            var endLocal = DateTime.SpecifyKind(localDate.Date.AddDays(1), DateTimeKind.Unspecified);

            return (startLocal, endLocal);
        }

        public static (DateTime fromUtc, DateTime toUtcExclusive) LocalDateToUtcRange(DateTime localDate)
        {
            var localRange = LocalDateRange(localDate);
            var startUtc = TimeZoneInfo.ConvertTimeToUtc(localRange.fromLocalInclusive, Ensure());
            var endUtc = TimeZoneInfo.ConvertTimeToUtc(localRange.toLocalExclusive, Ensure());

            return (startUtc, endUtc);
        }
    }
}
