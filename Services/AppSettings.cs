using System;
using System.Configuration;
using System.Globalization;

namespace FaceAttend.Services
{
    public static class AppSettings
    {
        /// <summary>
        /// Kumukuha ng setting value.
        /// Priority:
        ///   1) IIS / process environment variable
        ///   2) Web.config appSettings
        ///
        /// Notes:
        /// - Sinusuportahan ang exact key name, halimbawa "Biometrics:Crypto:Entropy"
        /// - Sinusuportahan din ang double-underscore form para sa IIS env vars,
        ///   halimbawa "Biometrics__Crypto__Entropy"
        /// </summary>
        private static string GetRawValue(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return null;

            var env = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(env))
                return env;

            var envAlt = Environment.GetEnvironmentVariable(key.Replace(":", "__"));
            if (!string.IsNullOrWhiteSpace(envAlt))
                return envAlt;

            var cfg = ConfigurationManager.AppSettings[key];
            if (!string.IsNullOrWhiteSpace(cfg))
                return cfg;

            return null;
        }

        public static string GetString(string key, string fallback = "")
        {
            var v = GetRawValue(key);
            return string.IsNullOrWhiteSpace(v) ? fallback : v.Trim();
        }

        public static int GetInt(string key, int fallback)
        {
            var v = GetRawValue(key);
            return int.TryParse(v, out var n) ? n : fallback;
        }

        public static double GetDouble(string key, double fallback)
        {
            var v = GetRawValue(key);
            return double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var n) ? n : fallback;
        }

        public static bool GetBool(string key, bool fallback)
        {
            var v = GetRawValue(key);
            if (string.IsNullOrWhiteSpace(v)) return fallback;

            v = v.Trim();
            if (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("yes", StringComparison.OrdinalIgnoreCase))
                return true;

            if (v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase) || v.Equals("no", StringComparison.OrdinalIgnoreCase))
                return false;

            return fallback;
        }
    }
}
