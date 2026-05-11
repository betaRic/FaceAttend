using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace FaceAttend.Services.Helpers
{
    public static class ObjectValueReader
    {
        public static bool GetBool(object source, string name)
        {
            var value = GetValue(source, name);
            return value is bool && (bool)value;
        }

        public static string GetString(object source, string name, int maxLength = 0)
        {
            var value = GetValue(source, name);
            var text = value == null ? null : Convert.ToString(value);
            if (string.IsNullOrWhiteSpace(text))
                return null;

            return maxLength > 0 ? StringHelper.Truncate(text, maxLength) : text;
        }

        public static object GetValue(object source, string name)
        {
            if (source == null || string.IsNullOrWhiteSpace(name))
                return null;

            var prop = source.GetType().GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);

            return prop == null ? null : prop.GetValue(source, null);
        }
    }

    public static class StatsHelper
    {
        public static double Percentile(IList<double> values, double percentile)
        {
            if (values == null || values.Count == 0) return 0;
            return values[PercentileIndex(values.Count, percentile)];
        }

        public static long Percentile(IList<long> values, double percentile)
        {
            if (values == null || values.Count == 0) return 0;
            return values[PercentileIndex(values.Count, percentile)];
        }

        private static int PercentileIndex(int count, double percentile)
        {
            var index = (int)Math.Ceiling(count * percentile) - 1;
            if (index < 0) return 0;
            return index >= count ? count - 1 : index;
        }
    }

    public static class RecognitionNotesParser
    {
        public static string ExtractAmbiguityGapText(string notes)
        {
            if (string.IsNullOrWhiteSpace(notes))
                return "";

            const string marker = "gap=";
            var idx = notes.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return "";

            var start = idx + marker.Length;
            var end = notes.IndexOfAny(new[] { ' ', '.', ';', ',' }, start);
            if (end < 0) end = notes.Length;

            return notes.Substring(start, end - start).Trim();
        }

        public static double? ExtractAmbiguityGap(string notes)
        {
            var raw = ExtractAmbiguityGapText(notes);
            if (string.IsNullOrWhiteSpace(raw) ||
                string.Equals(raw, "inf", StringComparison.OrdinalIgnoreCase))
                return null;

            double value;
            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                ? (double?)value
                : null;
        }
    }
}
