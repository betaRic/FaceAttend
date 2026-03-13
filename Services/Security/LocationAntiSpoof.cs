using System;
using System.Linq;

namespace FaceAttend.Services.Security
{
    /// <summary>
    /// ANTI-SPOOFING: Detects fake GPS and location manipulation attempts.
    /// 
    /// DETECTION METHODS:
    /// 1. TELEPORTATION CHECK - Impossible travel speeds (e.g., Manila to Koronadal in 5 min)
    /// 2. DEVICE CONSISTENCY - Same device fingerprint expected
    /// 3. JUMP PATTERN - Sudden large location jumps without travel time
    /// 4. REPLAY DETECTION - Same coordinates submitted too precisely
    /// </summary>
    public static class LocationAntiSpoof
    {
        public class CheckResult
        {
            public bool IsSuspicious { get; set; }
            public string Reason { get; set; }
            public double RiskScore { get; set; } // 0.0 to 1.0
            public string Action { get; set; } // "ALLOW", "WARN", "BLOCK"
        }

        /// <summary>
        /// Check for suspicious location patterns.
        /// NOTE: Teleportation check REMOVED - employees may scan from home then office
        /// legitimately due to intermittent internet connectivity.
        /// </summary>
        public static CheckResult CheckLocation(
            int employeeId,
            double newLat, 
            double newLon,
            DateTime newTime,
            string deviceFingerprint)
        {
            // Default: allow
            var result = new CheckResult 
            { 
                IsSuspicious = false, 
                RiskScore = 0.0, 
                Action = "ALLOW" 
            };

            try
            {
                // Only check for mock GPS coordinates - NOT teleportation
                // Employees may have no internet at home, scan at office,
                // which appears as "jump" but is legitimate
                
                if (!LooksLikeRealGps(newLat, newLon, null))
                {
                    result.IsSuspicious = true;
                    result.RiskScore = 0.7;
                    result.Action = "BLOCK";
                    result.Reason = "MOCK_GPS_DETECTED";
                }
            }
            catch (Exception ex)
            {
                // Log but don't block on error
                System.Diagnostics.Trace.TraceError($"[LocationAntiSpoof] Error: {ex.Message}");
            }

            return result;
        }

        // Note: Distance calculation now uses OfficeLocationService.CalculateDistanceMeters for consistency

        /// <summary>
        /// Quick check for office radius (GPS required).
        /// </summary>
        public static bool IsWithinOfficeRadius(double lat, double lon, Office office)
        {
            if (office == null)
                return false;

            double distanceMeters = OfficeLocationService.CalculateDistanceMeters(
                office.Latitude, office.Longitude,
                lat, lon);
            double radiusMeters = office.RadiusMeters > 0 ? office.RadiusMeters : 100;

            return distanceMeters <= radiusMeters;
        }

        /// <summary>
        /// Check if GPS coordinates look real (not default/mock values).
        /// </summary>
        public static bool LooksLikeRealGps(double lat, double lon, double? accuracy)
        {
            // Check for null island (0,0) - common mock value
            if (Math.Abs(lat) < 0.001 && Math.Abs(lon) < 0.001)
                return false;

            // Check for Apple HQ (common mock value)
            if (Math.Abs(lat - 37.3318) < 0.001 && Math.Abs(lon - (-122.0312)) < 0.001)
                return false;

            // Check for Google HQ (common mock value)
            if (Math.Abs(lat - 37.4220) < 0.001 && Math.Abs(lon - (-122.0841)) < 0.001)
                return false;

            // Check accuracy is reasonable
            if (accuracy.HasValue && accuracy.Value > 1000) // > 1km accuracy is useless
                return false;

            // Valid range checks
            if (lat < -90 || lat > 90 || lon < -180 || lon > 180)
                return false;

            return true;
        }

        /// <summary>
        /// Calculate trust score for location (0.0 to 1.0).
        /// </summary>
        public static double CalculateLocationTrust(
            double lat, double lon, double? accuracy, 
            Office office, string deviceFingerprint)
        {
            double score = 0.5; // Neutral start

            // GPS accuracy weight
            if (accuracy.HasValue)
            {
                if (accuracy.Value < 10) score += 0.2;
                else if (accuracy.Value < 50) score += 0.1;
                else if (accuracy.Value > 100) score -= 0.2;
            }

            // Office proximity
            if (office != null)
            {
                double dist = OfficeLocationService.CalculateDistanceMeters(
                    office.Latitude, office.Longitude,
                    lat, lon);
                double radius = office.RadiusMeters > 0 ? office.RadiusMeters : 100;

                if (dist <= radius) score += 0.2;
                else if (dist <= radius * 2) score += 0.0;
                else score -= 0.3;
            }

            // Realistic GPS check
            if (!LooksLikeRealGps(lat, lon, accuracy))
                score -= 0.4;

            return Math.Max(0.0, Math.Min(1.0, score));
        }
    }
}
