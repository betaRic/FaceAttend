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
    }
}
