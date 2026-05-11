using System;

namespace FaceAttend.Services.Security
{
    public static class LocationAntiSpoof
    {
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

        public static CheckResult CheckLocation(
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

                var existing = _gpsCache.Get(cacheKey) as GpsEntry;

                if (existing != null)
                {
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

                        if (existing.RepeatCount >= 3)
                        {
                            result.IsSuspicious = true;
                            result.RiskScore    = 0.6;
                            result.Action  = "WARN";
                            result.Reason  = $"GPS_REPEAT_COORDS (x{existing.RepeatCount})";
                        }
                    }
                    else
                    {
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

        private static bool LooksLikeRealGps(double lat, double lon, double? accuracy)
        {
            if (Math.Abs(lat) < 0.001 && Math.Abs(lon) < 0.001)
                return false;

            if (Math.Abs(lat - 37.3318) < 0.001 && Math.Abs(lon - (-122.0312)) < 0.001)
                return false;

            if (Math.Abs(lat - 37.4220) < 0.001 && Math.Abs(lon - (-122.0841)) < 0.001)
                return false;

            if (accuracy.HasValue && accuracy.Value > 1000)
                return false;

            if (lat < -90 || lat > 90 || lon < -180 || lon > 180)
                return false;

            return true;
        }
    }
}
