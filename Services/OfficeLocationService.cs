using System;
using System.Linq;

namespace FaceAttend.Services
{
    /// <summary>
    /// Handles office location selection based on GPS coordinates.
    /// Extracted from KioskController to reduce controller size.
    /// 
    /// MERGED: Contains GeoUtil functionality (GPS distance calculation).
    /// </summary>
    public static class OfficeLocationService
    {
        public class OfficePickResult
        {
            public bool Allowed { get; set; }
            public string Reason { get; set; }
            public Office Office { get; set; }
            public double DistanceMeters { get; set; }
            public int RadiusMeters { get; set; }
            public int RequiredAccuracy { get; set; }
        }

        /// <summary>
        /// Picks the nearest office based on GPS coordinates.
        /// </summary>
        public static OfficePickResult PickOffice(FaceAttendDBEntities db, double lat, double lon, double? accuracy)
        {
            int requiredAcc = ConfigurationService.GetInt(
                db, "Location:GPSAccuracyRequired",
                ConfigurationService.GetInt("Location:GPSAccuracyRequired", 50));

            if (!accuracy.HasValue)
                return new OfficePickResult { Allowed = false, Reason = "GPS_ACCURACY", RequiredAccuracy = requiredAcc };

            if (accuracy.Value > requiredAcc)
                return new OfficePickResult { Allowed = false, Reason = "GPS_ACCURACY", RequiredAccuracy = requiredAcc };

            int defaultRadius = ConfigurationService.GetInt(
                db, "Location:GPSRadiusDefault",
                ConfigurationService.GetInt("Location:GPSRadiusDefault", 100));

            var offices = db.Offices.Where(o => o.IsActive).ToList();
            if (offices == null || offices.Count == 0)
                return new OfficePickResult { Allowed = false, Reason = "NO_OFFICES", RequiredAccuracy = requiredAcc };

            Office best = null;
            double bestDist = double.PositiveInfinity;
            int bestRadius = 0;

            foreach (var o in offices)
            {
                int radius = o.RadiusMeters > 0 ? o.RadiusMeters : defaultRadius;
                double d = CalculateDistanceMeters(lat, lon, o.Latitude, o.Longitude);
                if (d <= radius && d < bestDist)
                {
                    best = o;
                    bestDist = d;
                    bestRadius = radius;
                }
            }

            if (best == null)
                return new OfficePickResult { Allowed = false, Reason = "NO_OFFICE_NEARBY", RequiredAccuracy = requiredAcc };

            return new OfficePickResult
            {
                Allowed = true,
                Reason = "OK",
                Office = best,
                DistanceMeters = bestDist,
                RadiusMeters = bestRadius,
                RequiredAccuracy = requiredAcc
            };
        }

        /// <summary>
        /// Gets the fallback office when GPS is not available.
        /// </summary>
        public static Office GetFallbackOffice(FaceAttendDBEntities db)
        {
            int preferred = ConfigurationService.GetInt(
                db, "Kiosk:FallbackOfficeId",
                ConfigurationService.GetInt("Kiosk:FallbackOfficeId", 0));

            if (preferred > 0)
            {
                var chosen = db.Offices.FirstOrDefault(o => o.Id == preferred && o.IsActive);
                if (chosen != null) return chosen;
            }

            return db.Offices.Where(o => o.IsActive).OrderBy(o => o.Name).FirstOrDefault();
        }

        /// <summary>
        /// Truncates GPS coordinates for privacy/storage.
        /// </summary>
        public static double? TruncateGpsCoordinate(double? value)
        {
            if (!value.HasValue) return null;
            // 4 decimals ~= around 11m precision
            return Math.Round(value.Value, 4, MidpointRounding.AwayFromZero);
        }

        #region GeoUtil Functions (Merged)

        /// <summary>
        /// Calculates the distance between two GPS coordinates using the Haversine formula.
        /// Result is in meters.
        /// 
        /// FORMULA: Haversine formula - most accurate for short distances (&lt; 1km)
        /// Earth radius: 6,371 km (average)
        /// </summary>
        /// <param name="lat1">Latitude of first point (employee)</param>
        /// <param name="lon1">Longitude of first point (employee)</param>
        /// <param name="lat2">Latitude of second point (office)</param>
        /// <param name="lon2">Longitude of second point (office)</param>
        /// <returns>Distance in meters</returns>
        public static double CalculateDistanceMeters(double lat1, double lon1, double lat2, double lon2)
        {
            const double R = 6371000.0; // Earth radius meters
            double dLat = ToRadians(lat2 - lat1);
            double dLon = ToRadians(lon2 - lon1);

            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                       Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
                       Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

            double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            return R * c;
        }

        /// <summary>
        /// Converts degrees to radians.
        /// </summary>
        private static double ToRadians(double degrees)
        {
            return degrees * (Math.PI / 180.0);
        }

        #endregion
    }
}
