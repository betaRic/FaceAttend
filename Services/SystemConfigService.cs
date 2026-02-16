using System;
using System.Globalization;
using System.Linq;

namespace FaceAttend.Services
{
    public static class SystemConfigService
    {
        public static double GetDouble(FaceAttendDBEntities db, string key, double fallback)
        {
            try
            {
                var row = db.SystemConfigurations.FirstOrDefault(x => x.Key == key);
                if (row == null || string.IsNullOrWhiteSpace(row.Value)) return fallback;

                if (double.TryParse(row.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
                    return d;

                return fallback;
            }
            catch { return fallback; }
        }
    }
}
