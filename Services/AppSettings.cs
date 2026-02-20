using System.Configuration;
using System.Globalization;

namespace FaceAttend.Services
{
    public static class AppSettings
    {
        public static string GetString(string key, string fallback = "")
        {
            var v = ConfigurationManager.AppSettings[key];
            return string.IsNullOrWhiteSpace(v) ? fallback : v.Trim();
        }

        public static int GetInt(string key, int fallback)
        {
            var v = ConfigurationManager.AppSettings[key];
            return int.TryParse(v, out var n) ? n : fallback;
        }

        public static double GetDouble(string key, double fallback)
        {
            var v = ConfigurationManager.AppSettings[key];
            return double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var n) ? n : fallback;
        }

        public static bool GetBool(string key, bool fallback)
        {
            var v = ConfigurationManager.AppSettings[key];
            if (string.IsNullOrWhiteSpace(v)) return fallback;

            v = v.Trim();
            if (v == "1" || v.Equals("true", System.StringComparison.OrdinalIgnoreCase) || v.Equals("yes", System.StringComparison.OrdinalIgnoreCase))
                return true;

            if (v == "0" || v.Equals("false", System.StringComparison.OrdinalIgnoreCase) || v.Equals("no", System.StringComparison.OrdinalIgnoreCase))
                return false;

            return fallback;
        }
    }
}
