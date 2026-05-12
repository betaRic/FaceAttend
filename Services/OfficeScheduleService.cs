using System;
using System.Collections.Generic;
using System.Linq;

namespace FaceAttend.Services
{
    /// <summary>
    /// Weekly recurring schedule logic for offices.
    /// WorkDays / WfhDays are stored as comma-separated ISO day numbers (1=Mon … 7=Sun).
    /// NULL WorkDays defaults to Mon–Fri for backward compatibility.
    /// </summary>
    public static class OfficeScheduleService
    {
        private static readonly HashSet<DayOfWeek> DefaultWorkDays =
            new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                                     DayOfWeek.Thursday, DayOfWeek.Friday };

        /// <summary>
        /// Parses a comma-separated ISO day string ("1,2,3,4,5") into a HashSet of DayOfWeek.
        /// ISO 1=Mon … 6=Sat, ISO 7=Sun → DayOfWeek.Sunday.
        /// </summary>
        public static HashSet<DayOfWeek> ParseDayMask(string mask)
        {
            var set = new HashSet<DayOfWeek>();
            if (string.IsNullOrWhiteSpace(mask)) return set;

            foreach (var part in mask.Split(','))
            {
                int iso;
                if (!int.TryParse(part.Trim(), out iso) || iso < 1 || iso > 7) continue;
                // ISO 7 (Sunday) = DayOfWeek.Sunday (0); ISO 1-6 map directly
                set.Add(iso == 7 ? DayOfWeek.Sunday : (DayOfWeek)iso);
            }

            return set;
        }

        /// <summary>
        /// Returns true if <paramref name="localDate"/> is a scheduled working day for this office.
        /// NULL WorkDays → defaults to Mon–Fri.
        /// </summary>
        public static bool IsWorkDay(Office office, DateTime localDate)
        {
            if (office == null) return true;

            var days = string.IsNullOrWhiteSpace(office.WorkDays)
                ? DefaultWorkDays
                : ParseDayMask(office.WorkDays);

            return days.Contains(localDate.DayOfWeek);
        }

        /// <summary>
        /// Returns true if <paramref name="localDate"/> is a WFH day for this office.
        /// Requires WfhEnabled=true and the day to be in the WfhDays mask.
        /// </summary>
        public static bool IsWfhDay(Office office, DateTime localDate)
        {
            if (office == null || !office.WfhEnabled) return false;
            if (string.IsNullOrWhiteSpace(office.WfhDays)) return false;

            return ParseDayMask(office.WfhDays).Contains(localDate.DayOfWeek);
        }

        /// <summary>
        /// Returns true only if the date is both a work day AND a WFH day.
        /// </summary>
        public static bool IsWfhEnabledToday(Office office, DateTime localDate)
        {
            return IsWorkDay(office, localDate) && IsWfhDay(office, localDate);
        }

        /// <summary>
        /// Validates and normalises an ISO day list into a comma-separated string.
        /// Deduplicates, clamps to 1–7, sorts ascending.
        /// Returns empty string for empty input.
        /// </summary>
        public static string NormalizeDayMask(IEnumerable<int> isoDays)
        {
            if (isoDays == null) return "";

            var valid = isoDays
                .Where(d => d >= 1 && d <= 7)
                .Distinct()
                .OrderBy(d => d)
                .ToList();

            return string.Join(",", valid);
        }
    }
}
