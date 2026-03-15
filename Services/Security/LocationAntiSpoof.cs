using System;
using System.Linq;

namespace FaceAttend.Services.Security
{
    /// <summary>
    /// ANTI-SPOOFING: Detects fake GPS and location manipulation attempts.
    /// 
    /// DETECTION METHODS:
    /// 1. MOCK GPS DETECTION - Null island, Apple HQ, Google HQ coordinates
    /// 2. REPEAT COORDINATES - GPS spoof apps don't add natural drift
    /// 3. DEVICE CONSISTENCY - Same device fingerprint expected
    /// </summary>
    public static class LocationAntiSpoof
    {
        // In-process cache: last seen GPS per device fingerprint.
        // Key: deviceFingerprint, Value: (lat, lon, seenCount, firstSeenAt)
        private static readonly System.Runtime.Caching.MemoryCache _gpsCache =
            System.Runtime.Caching.MemoryCache.Default;
        private const string GpsCachePrefix = "GPS_LAST::";

        private class GpsEntry
        {
            public double Lat { get; set; }
            public double Lon { get; set; }
            public int    RepeatCount { get; set; }
            public DateTime FirstSeenAt { get; set; }
        }

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
                // ── Check 1: Mock GPS coordinates ───────────────────────────
                if (!LooksLikeRealGps(newLat, newLon, null))
                {
                    result.IsSuspicious = true;
                    result.RiskScore    = 0.7;
                    result.Action       = "BLOCK";
                    result.Reason       = "MOCK_GPS_DETECTED";
                    return result;
                }

                if (string.IsNullOrWhiteSpace(deviceFingerprint))
                    return result;

                var cacheKey = GpsCachePrefix + deviceFingerprint;

                // ── Check 2: Exact coordinate repeat (GPS spoof apps don't add natural drift) ──
                // Real GPS has 3-15m natural drift between readings -- coordinates are never
                // bit-for-bit identical across separate scans minutes apart.
                // Threshold: 6 decimal places = ~0.1m precision. If coordinates match to
                // 5 decimal places (1m), count as a repeat.
                var existing = _gpsCache.Get(cacheKey) as GpsEntry;

                if (existing != null)
                {
                    // Compare to 5 decimal places (~1m resolution)
                    bool sameCoord =
                        Math.Abs(newLat - existing.Lat) < 0.00001 &&
                        Math.Abs(newLon - existing.Lon) < 0.00001;

                    if (sameCoord)
                    {
                        existing.RepeatCount++;
                        _gpsCache.Set(cacheKey, existing,
                            new System.Runtime.Caching.CacheItemPolicy
                            {
                                AbsoluteExpiration = DateTimeOffset.UtcNow.AddMinutes(30)
                            });

                        // 3+ identical coordinate reads -> flag as suspicious
                        // First 2 are allowed (employee may be genuinely stationary at desk)
                        if (existing.RepeatCount >= 3)
                        {
                            result.IsSuspicious = true;
                            result.RiskScore    = 0.6;
                            // WARN not BLOCK -- flag for admin review, don't hard-block
                            // (employee may genuinely be sitting still at their desk)
                            result.Action  = "WARN";
                            result.Reason  = $"GPS_REPEAT_COORDS (x{existing.RepeatCount})";
                        }
                    }
                    else
                    {
                        // Coordinates changed -- natural drift, reset counter
                        existing.Lat         = newLat;
                        existing.Lon         = newLon;
                        existing.RepeatCount = 1;
                        existing.FirstSeenAt = newTime;
                        _gpsCache.Set(cacheKey, existing,
                            new System.Runtime.Caching.CacheItemPolicy
                            {
                                AbsoluteExpiration = DateTimeOffset.UtcNow.AddMinutes(30)
                            });
                    }
                }
                else
                {
                    // First time seeing this device -- store
                    _gpsCache.Set(cacheKey, new GpsEntry
                        {
                            Lat         = newLat,
                            Lon         = newLon,
                            RepeatCount = 1,
                            FirstSeenAt = newTime
                        },
                        new System.Runtime.Caching.CacheItemPolicy
                        {
                            AbsoluteExpiration = DateTimeOffset.UtcNow.AddMinutes(30)
                        });
                }
            }
            catch (Exception ex)
            {
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
