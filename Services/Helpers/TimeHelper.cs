using System;
using System.Globalization;

namespace FaceAttend.Services.Helpers
{
    public static class TimeHelper
    {
        /// <summary>
        /// Parses a time string in hh:mm, hh:mm:ss, or any format accepted by TimeSpan.TryParse.
        /// </summary>
        public static bool TryParseTime(string value, out TimeSpan result)
        {
            result = TimeSpan.Zero;

            if (string.IsNullOrWhiteSpace(value))
                return false;

            value = value.Trim();

            return
                TimeSpan.TryParseExact(value, @"hh\:mm",    CultureInfo.InvariantCulture, out result) ||
                TimeSpan.TryParseExact(value, @"hh\:mm\:ss", CultureInfo.InvariantCulture, out result) ||
                TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out result);
        }
    }
}
