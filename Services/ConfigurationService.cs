using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.Globalization;
using System.Linq;
using System.Runtime.Caching;

namespace FaceAttend.Services
{
    public static class ConfigurationService
    {
        private const string CachePrefix = "config:";
        private const int DefaultCacheSeconds = 60;
        private const int StableCacheSeconds = 600;

        private static readonly MemoryCache Cache = MemoryCache.Default;

        private static readonly HashSet<string> StableKeys =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Biometrics:ModelDir",
                "Biometrics:OnnxModelsDir",
                "Biometrics:AntiSpoofModel",
                "Biometrics:Engine:Enabled",
                "Biometrics:Engine:Runtime",
                "Biometrics:Engine:AnalyzeTimeoutMs",
                "Biometrics:Engine:DetectorPath",
                "Biometrics:Engine:RecognizerPath",
                "Biometrics:Engine:AntiSpoofPath",
                "Biometrics:Recognizer:Normalize127",
                "Biometrics:Detect:ScoreThreshold",
                "Biometrics:Detect:NmsThreshold",
                "Biometrics:Detect:TopK",
                "Biometrics:DetectorModel",
                "Biometrics:LandmarkModel",
                "Biometrics:RecognizerModel",
                "Biometrics:ModelVersion",
                "Biometrics:EmbeddingDim",
                "Biometrics:ModelHashes",
                "Biometrics:RequireModelReadOnlyAcl",
                "App:TimeZoneId",
                "Admin:AllowedIpRanges",
                "Kiosk:AllowedIpRanges",
                "TempFile:MaxAgeMinutes",
                "TempFile:CleanupIntervalMinutes"
            };

        private static readonly ConcurrentDictionary<string, byte> KnownKeys =
            new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        public static string GetString(string key, string fallback = "")
        {
            var value = GetFromAnySource(key);
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        public static int GetInt(string key, int fallback)
        {
            return int.TryParse(GetFromAnySource(key), out var n) ? n : fallback;
        }

        public static double GetDouble(string key, double fallback)
        {
            return double.TryParse(GetFromAnySource(key), NumberStyles.Any,
                CultureInfo.InvariantCulture, out var n)
                ? n
                : fallback;
        }

        public static bool GetBool(string key, bool fallback)
        {
            return TryParseBool(GetFromAnySource(key), out var value) ? value : fallback;
        }

        public static string GetString(FaceAttendDBEntities db, string key, string fallback)
        {
            var value = FirstNonEmpty(
                GetFromEnvironment(key),
                db == null ? null : GetFromDb(db, key),
                GetFromAppSettings(key));

            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        public static int GetInt(FaceAttendDBEntities db, string key, int fallback)
        {
            return int.TryParse(GetString(db, key, ""), out var n) ? n : fallback;
        }

        public static double GetDouble(FaceAttendDBEntities db, string key, double fallback)
        {
            return double.TryParse(GetString(db, key, ""), NumberStyles.Any,
                CultureInfo.InvariantCulture, out var n)
                ? n
                : fallback;
        }

        public static bool GetBool(FaceAttendDBEntities db, string key, bool fallback)
        {
            return TryParseBool(GetString(db, key, ""), out var value) ? value : fallback;
        }

        public static bool HasKey(FaceAttendDBEntities db, string key)
        {
            return db != null
                && !string.IsNullOrWhiteSpace(key)
                && db.SystemConfigurations.Any(x => x.Key == key);
        }

        public static string GetStringCached(string key, string fallback, int cacheSeconds = DefaultCacheSeconds)
        {
            var value = FirstNonEmpty(
                GetFromEnvironment(key),
                GetFromDbCached(key, cacheSeconds),
                GetFromAppSettings(key));

            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        public static int GetIntCached(string key, int fallback, int cacheSeconds = DefaultCacheSeconds)
        {
            return int.TryParse(GetStringCached(key, "", cacheSeconds), out var n) ? n : fallback;
        }

        public static double GetDoubleCached(string key, double fallback, int cacheSeconds = DefaultCacheSeconds)
        {
            return double.TryParse(GetStringCached(key, "", cacheSeconds), NumberStyles.Any,
                CultureInfo.InvariantCulture, out var n)
                ? n
                : fallback;
        }

        public static bool GetBoolCached(string key, bool fallback, int cacheSeconds = DefaultCacheSeconds)
        {
            return TryParseBool(GetStringCached(key, "", cacheSeconds), out var value) ? value : fallback;
        }

        public static void SetInDb(FaceAttendDBEntities db, string key, string value,
            string dataType = "string", string description = "", string modifiedBy = "ADMIN")
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Key is required.", nameof(key));

            var now = DateTime.UtcNow;
            var trimmedKey = key.Trim();
            var row = db.SystemConfigurations.FirstOrDefault(x => x.Key == trimmedKey);

            if (row == null)
            {
                row = new SystemConfiguration
                {
                    Key = trimmedKey,
                    Value = Trim(value),
                    DataType = TrimOr(dataType, "string"),
                    Description = Trim(description),
                    ModifiedDate = now,
                    ModifiedBy = TrimOr(modifiedBy, "ADMIN")
                };
                db.SystemConfigurations.Add(row);
            }
            else
            {
                row.Value = Trim(value);
                if (!string.IsNullOrWhiteSpace(dataType)) row.DataType = dataType.Trim();
                if (!string.IsNullOrWhiteSpace(description)) row.Description = description.Trim();
                row.ModifiedDate = now;
                row.ModifiedBy = TrimOr(modifiedBy, "ADMIN");
            }

            db.SaveChanges();
            InvalidateCache(trimmedKey);
        }

        public static void Set(string key, string value,
            string dataType = "string", string description = "", string modifiedBy = "ADMIN")
        {
            using (var db = new FaceAttendDBEntities())
            {
                SetInDb(db, key, value, dataType, description, modifiedBy);
            }
        }

        public static void Upsert(FaceAttendDBEntities db, string key, string value,
            string dataType = "string", string description = "", string modifiedBy = "ADMIN")
        {
            SetInDb(db, key, value, dataType, description, modifiedBy);
        }

        public static void DeleteFromDb(FaceAttendDBEntities db, string key)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (string.IsNullOrWhiteSpace(key)) return;

            var trimmedKey = key.Trim();
            var row = db.SystemConfigurations.FirstOrDefault(x => x.Key == trimmedKey);
            if (row == null) return;

            db.SystemConfigurations.Remove(row);
            db.SaveChanges();
            InvalidateCache(trimmedKey);
        }

        public static void Delete(FaceAttendDBEntities db, string key)
        {
            DeleteFromDb(db, key);
        }

        public static int DeleteByPrefix(FaceAttendDBEntities db, string prefix)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (string.IsNullOrWhiteSpace(prefix)) return 0;

            var rows = db.SystemConfigurations
                .Where(x => x.Key.StartsWith(prefix))
                .ToList();

            foreach (var row in rows)
            {
                db.SystemConfigurations.Remove(row);
                InvalidateCache(row.Key);
            }

            if (rows.Count > 0)
                db.SaveChanges();

            return rows.Count;
        }

        public static void InvalidateCache(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            var trimmedKey = key.Trim();
            Cache.Remove(CachePrefix + trimmedKey);
            KnownKeys.TryRemove(trimmedKey, out _);
        }

        public static void Invalidate(string key)
        {
            InvalidateCache(key);
        }

        public static void InvalidateAllCache()
        {
            var keys = KnownKeys.Keys.ToList();
            foreach (var key in keys)
                Cache.Remove(CachePrefix + key);

            KnownKeys.Clear();
            System.Diagnostics.Trace.TraceInformation(
                "[ConfigurationService] Invalidated {0} cached config keys.", keys.Count);
        }

        public static void InvalidateAll()
        {
            InvalidateAllCache();
        }

        private static string GetFromAnySource(string key)
        {
            return FirstNonEmpty(
                GetFromEnvironment(key),
                GetFromDbCached(key),
                GetFromAppSettings(key));
        }

        private static string GetFromDb(FaceAttendDBEntities db, string key)
        {
            if (db == null || string.IsNullOrWhiteSpace(key)) return null;
            return db.SystemConfigurations.FirstOrDefault(x => x.Key == key)?.Value;
        }

        private static string GetFromDbCached(string key, int cacheSeconds = DefaultCacheSeconds)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;

            var cacheKey = CachePrefix + key;
            var cached = Cache.Get(cacheKey) as string;
            if (cached != null) return cached;

            try
            {
                using (var db = new FaceAttendDBEntities())
                {
                    var value = GetFromDb(db, key);
                    if (value == null) return null;

                    Cache.Set(cacheKey, value, new CacheItemPolicy
                    {
                        AbsoluteExpiration = DateTimeOffset.UtcNow.AddSeconds(GetCacheSeconds(key, cacheSeconds))
                    });
                    KnownKeys.TryAdd(key, 0);
                    return value;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "[ConfigurationService] DB error for key '{0}': {1}", key, ex.Message);
                return null;
            }
        }

        private static string GetFromEnvironment(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;

            var env = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(env)) return env;

            return Environment.GetEnvironmentVariable(key.Replace(":", "__"));
        }

        private static string GetFromAppSettings(string key)
        {
            return string.IsNullOrWhiteSpace(key) ? null : ConfigurationManager.AppSettings[key];
        }

        private static int GetCacheSeconds(string key, int requestedSeconds)
        {
            if (StableKeys.Contains(key)) return StableCacheSeconds;
            return requestedSeconds > 0 ? requestedSeconds : DefaultCacheSeconds;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        }

        private static bool TryParseBool(string value, out bool result)
        {
            result = false;
            if (string.IsNullOrWhiteSpace(value)) return false;

            value = value.Trim();
            if (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("yes", StringComparison.OrdinalIgnoreCase))
            {
                result = true;
                return true;
            }

            if (value == "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("no", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        private static string Trim(string value)
        {
            return (value ?? "").Trim();
        }

        private static string TrimOr(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }
    }
}
