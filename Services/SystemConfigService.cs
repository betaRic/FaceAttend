using System;
using System.Globalization;
using System.Linq;
using System.Runtime.Caching;

namespace FaceAttend.Services
{
    public static class SystemConfigService
    {
        private static readonly MemoryCache Cache = MemoryCache.Default;
        private const int DefaultCacheSeconds = 15;

        public static string GetRaw(FaceAttendDBEntities db, string key)
        {
            if (db == null) return null;
            if (string.IsNullOrWhiteSpace(key)) return null;

            try
            {
                var row = db.SystemConfigurations.FirstOrDefault(x => x.Key == key);
                return row == null ? null : (row.Value ?? "").Trim();
            }
            catch
            {
                return null;
            }
        }

        public static bool HasKey(FaceAttendDBEntities db, string key)
        {
            if (db == null) return false;
            if (string.IsNullOrWhiteSpace(key)) return false;

            try
            {
                return db.SystemConfigurations.Any(x => x.Key == key);
            }
            catch
            {
                return false;
            }
        }

        public static string GetString(FaceAttendDBEntities db, string key, string fallback)
        {
            var v = GetRaw(db, key);
            return string.IsNullOrWhiteSpace(v) ? fallback : v;
        }

        public static int GetInt(FaceAttendDBEntities db, string key, int fallback)
        {
            var v = GetRaw(db, key);
            if (string.IsNullOrWhiteSpace(v)) return fallback;
            return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;
        }

        public static bool GetBool(FaceAttendDBEntities db, string key, bool fallback)
        {
            var v = GetRaw(db, key);
            if (string.IsNullOrWhiteSpace(v)) return fallback;

            if (v.Equals("1") || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("yes", StringComparison.OrdinalIgnoreCase))
                return true;

            if (v.Equals("0") || v.Equals("false", StringComparison.OrdinalIgnoreCase) || v.Equals("no", StringComparison.OrdinalIgnoreCase))
                return false;

            return fallback;
        }

        public static double GetDouble(FaceAttendDBEntities db, string key, double fallback)
        {
            var v = GetRaw(db, key);
            if (string.IsNullOrWhiteSpace(v)) return fallback;

            if (double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
                return d;

            return fallback;
        }

        public static void Upsert(FaceAttendDBEntities db, string key, string value, string dataType, string description, string modifiedBy)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Key is required.", nameof(key));

            var now = DateTime.UtcNow;

            var row = db.SystemConfigurations.FirstOrDefault(x => x.Key == key);
            if (row == null)
            {
                row = new SystemConfiguration
                {
                    Key = key.Trim(),
                    Value = (value ?? "").Trim(),
                    DataType = (dataType ?? "string").Trim(),
                    Description = (description ?? "").Trim(),
                    ModifiedDate = now,
                    ModifiedBy = (modifiedBy ?? "ADMIN").Trim()
                };
                db.SystemConfigurations.Add(row);
            }
            else
            {
                row.Value = (value ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(dataType)) row.DataType = dataType.Trim();
                if (!string.IsNullOrWhiteSpace(description)) row.Description = description.Trim();
                row.ModifiedDate = now;
                row.ModifiedBy = (modifiedBy ?? "ADMIN").Trim();
            }

            db.SaveChanges();
            Invalidate(key);
        }

        public static void Delete(FaceAttendDBEntities db, string key)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (string.IsNullOrWhiteSpace(key)) return;

            var row = db.SystemConfigurations.FirstOrDefault(x => x.Key == key);
            if (row == null) return;

            db.SystemConfigurations.Remove(row);
            db.SaveChanges();
            Invalidate(key);
        }

        // ---- Cached access (for code paths that don't have a DB context) ----

        public static void Invalidate(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            Cache.Remove(CacheKey(key));
        }

        public static void InvalidateAll()
        {
            // MemoryCache has no clear-all, but you can invalidate selectively.
            // Keep this method for future use.
        }

        public static string GetStringCached(string key, string fallback, int cacheSeconds = DefaultCacheSeconds)
        {
            var v = GetRawCached(key, cacheSeconds);
            return string.IsNullOrWhiteSpace(v) ? fallback : v;
        }

        public static int GetIntCached(string key, int fallback, int cacheSeconds = DefaultCacheSeconds)
        {
            var v = GetRawCached(key, cacheSeconds);
            if (string.IsNullOrWhiteSpace(v)) return fallback;
            return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;
        }

        public static double GetDoubleCached(string key, double fallback, int cacheSeconds = DefaultCacheSeconds)
        {
            var v = GetRawCached(key, cacheSeconds);
            if (string.IsNullOrWhiteSpace(v)) return fallback;
            return double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : fallback;
        }

        public static bool GetBoolCached(string key, bool fallback, int cacheSeconds = DefaultCacheSeconds)
        {
            var v = GetRawCached(key, cacheSeconds);
            if (string.IsNullOrWhiteSpace(v)) return fallback;

            if (v.Equals("1") || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("yes", StringComparison.OrdinalIgnoreCase))
                return true;

            if (v.Equals("0") || v.Equals("false", StringComparison.OrdinalIgnoreCase) || v.Equals("no", StringComparison.OrdinalIgnoreCase))
                return false;

            return fallback;
        }

        private static string GetRawCached(string key, int cacheSeconds)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;

            var k = CacheKey(key);
            var hit = Cache.Get(k) as string;
            if (hit != null) return hit;

            string val = null;
            try
            {
                using (var db = new FaceAttendDBEntities())
                    val = GetRaw(db, key);
            }
            catch
            {
                val = null;
            }

            var policy = new CacheItemPolicy { AbsoluteExpiration = DateTimeOffset.UtcNow.AddSeconds(cacheSeconds <= 0 ? DefaultCacheSeconds : cacheSeconds) };
            Cache.Set(k, val ?? "", policy);

            return val;
        }

        private static string CacheKey(string key)
        {
            return "SYS_CFG::" + key.Trim();
        }
    }
}
