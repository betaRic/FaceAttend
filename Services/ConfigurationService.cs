using System;
using System.Configuration;
using System.Globalization;
using System.Linq;
using System.Runtime.Caching;

namespace FaceAttend.Services
{
    /// <summary>
    /// UNIFIED configuration service - single source of truth for all configuration.
    /// Replaces: AppSettings, SystemConfigService, and AppConfig.
    /// 
    /// Priority (highest to lowest):
    ///   1. Environment variable (FACEATTEND_XXX or XXX__XXX format)
    ///   2. Database SystemConfiguration table (with caching)
    ///   3. Web.config appSettings
    ///   4. Default value
    /// </summary>
    public static class ConfigurationService
    {
        private const string CachePrefix = "config:";
        private const int DefaultCacheSeconds = 60;
        private const int StableCacheSeconds = 600; // 10 minutes
        private static readonly MemoryCache Cache = MemoryCache.Default;

        // Keys that change only at deploy time — use long TTL to reduce DB round-trips
        private static readonly System.Collections.Generic.HashSet<string> _stableKeys =
            new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Biometrics:DlibModelsDir",
                "Biometrics:LivenessModelPath",
                "Biometrics:DlibDetector",
                "Biometrics:DlibPoolSize",
                "App:TimeZoneId",
                "Admin:AllowedIpRanges",
                "TempFile:MaxAgeMinutes",
                "TempFile:CleanupIntervalMinutes"
            };

        // Track all known keys for cache invalidation
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte>
            _knownKeys = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(
                StringComparer.OrdinalIgnoreCase);

        #region Get Methods (Unified Priority)

        public static string GetString(string key, string fallback = "")
        {
            var value = GetFromAnySource(key);
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        public static int GetInt(string key, int fallback)
        {
            var value = GetFromAnySource(key);
            return int.TryParse(value, out var n) ? n : fallback;
        }

        public static double GetDouble(string key, double fallback)
        {
            var value = GetFromAnySource(key);
            return double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var n) ? n : fallback;
        }

        public static bool GetBool(string key, bool fallback)
        {
            var value = GetFromAnySource(key);
            if (string.IsNullOrWhiteSpace(value)) return fallback;

            value = value.Trim();
            if (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase) || 
                value.Equals("yes", StringComparison.OrdinalIgnoreCase))
                return true;

            if (value == "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase) || 
                value.Equals("no", StringComparison.OrdinalIgnoreCase))
                return false;

            return fallback;
        }

        #endregion

        #region Get Methods with DbContext (Legacy Compatibility)

        /// <summary>
        /// Gets string from database directly (with fallback to Web.config/env).
        /// Use when you already have a DbContext open.
        /// </summary>
        public static string GetString(FaceAttendDBEntities db, string key, string fallback)
        {
            if (db != null)
            {
                var dbValue = GetFromDb(db, key);
                if (!string.IsNullOrWhiteSpace(dbValue))
                    return dbValue.Trim();
            }
            
            // Fall back to unified priority chain (env > Web.config)
            return GetString(key, fallback);
        }

        public static int GetInt(FaceAttendDBEntities db, string key, int fallback)
        {
            var value = GetString(db, key, "");
            return int.TryParse(value, out var n) ? n : fallback;
        }

        public static double GetDouble(FaceAttendDBEntities db, string key, double fallback)
        {
            var value = GetString(db, key, "");
            return double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var n) ? n : fallback;
        }

        public static bool GetBool(FaceAttendDBEntities db, string key, bool fallback)
        {
            var value = GetString(db, key, "");
            if (string.IsNullOrWhiteSpace(value)) return fallback;

            value = value.Trim();
            if (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase) || 
                value.Equals("yes", StringComparison.OrdinalIgnoreCase))
                return true;

            if (value == "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase) || 
                value.Equals("no", StringComparison.OrdinalIgnoreCase))
                return false;

            return fallback;
        }

        /// <summary>
        /// Checks if a key exists in the database (for migration warnings, etc.)
        /// </summary>
        public static bool HasKey(FaceAttendDBEntities db, string key)
        {
            if (db == null || string.IsNullOrWhiteSpace(key)) return false;
            return db.SystemConfigurations.Any(x => x.Key == key);
        }

        #endregion

        #region Cached Get Methods (Legacy Compatibility)

        public static string GetStringCached(string key, string fallback, int cacheSeconds = DefaultCacheSeconds)
        {
            var value = GetFromDbCached(key, cacheSeconds);
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            
            // Fall back to env/Web.config
            return GetString(key, fallback);
        }

        public static int GetIntCached(string key, int fallback, int cacheSeconds = DefaultCacheSeconds)
        {
            var value = GetStringCached(key, "", cacheSeconds);
            return int.TryParse(value, out var n) ? n : fallback;
        }

        public static double GetDoubleCached(string key, double fallback, int cacheSeconds = DefaultCacheSeconds)
        {
            var value = GetStringCached(key, "", cacheSeconds);
            return double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var n) ? n : fallback;
        }

        public static bool GetBoolCached(string key, bool fallback, int cacheSeconds = DefaultCacheSeconds)
        {
            var value = GetStringCached(key, "", cacheSeconds);
            if (string.IsNullOrWhiteSpace(value)) return fallback;

            value = value.Trim();
            if (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase) || 
                value.Equals("yes", StringComparison.OrdinalIgnoreCase))
                return true;

            if (value == "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase) || 
                value.Equals("no", StringComparison.OrdinalIgnoreCase))
                return false;

            return fallback;
        }

        #endregion

        #region Set/Update Methods (Legacy Compatibility)

        /// <summary>
        /// Upserts a configuration value to the database.
        /// </summary>
        public static void SetInDb(FaceAttendDBEntities db, string key, string value, 
            string dataType = "string", string description = "", string modifiedBy = "ADMIN")
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Key is required.", nameof(key));

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
            InvalidateCache(key);
        }

        /// <summary>
        /// Alias for SetInDb - for compatibility with SystemConfigService naming.
        /// </summary>
        public static void Upsert(FaceAttendDBEntities db, string key, string value, 
            string dataType = "string", string description = "", string modifiedBy = "ADMIN")
        {
            SetInDb(db, key, value, dataType, description, modifiedBy);
        }

        /// <summary>
        /// Deletes a configuration value from the database.
        /// </summary>
        public static void DeleteFromDb(FaceAttendDBEntities db, string key)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (string.IsNullOrWhiteSpace(key)) return;

            var row = db.SystemConfigurations.FirstOrDefault(x => x.Key == key);
            if (row == null) return;

            db.SystemConfigurations.Remove(row);
            db.SaveChanges();
            InvalidateCache(key);
        }

        /// <summary>
        /// Alias for DeleteFromDb - for compatibility with SystemConfigService naming.
        /// </summary>
        public static void Delete(FaceAttendDBEntities db, string key)
        {
            DeleteFromDb(db, key);
        }

        #endregion

        #region Cache Management

        public static void InvalidateCache(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            Cache.Remove(CachePrefix + key);
            _knownKeys.TryRemove(key, out _);
        }

        /// <summary>
        /// Alias for InvalidateCache - for compatibility.
        /// </summary>
        public static void Invalidate(string key)
        {
            InvalidateCache(key);
        }

        public static void InvalidateAllCache()
        {
            var keys = _knownKeys.Keys.ToList();
            foreach (var key in keys)
            {
                Cache.Remove(CachePrefix + key);
            }
            _knownKeys.Clear();
            
            System.Diagnostics.Trace.TraceInformation(
                $"[ConfigurationService] Invalidated {keys.Count} cached config keys.");
        }

        /// <summary>
        /// Alias for InvalidateAllCache - for compatibility with SystemConfigService.
        /// </summary>
        public static void InvalidateAll()
        {
            InvalidateAllCache();
        }

        #endregion

        #region Private Helpers

        private static string GetFromAnySource(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;

            // Priority 1: Environment variable
            var env = GetFromConfig(key);
            if (!string.IsNullOrWhiteSpace(env))
                return env;

            // Priority 2: Database (cached)
            var db = GetFromDbCached(key);
            if (!string.IsNullOrWhiteSpace(db))
                return db;

            return null;
        }

        /// <summary>
        /// Gets value directly from DB (bypass cache) - use when you have DbContext
        /// </summary>
        private static string GetFromDb(FaceAttendDBEntities db, string key)
        {
            if (db == null || string.IsNullOrWhiteSpace(key)) return null;
            
            var row = db.SystemConfigurations.FirstOrDefault(x => x.Key == key);
            return row?.Value;
        }

        /// <summary>
        /// Gets value from DB with caching - use when you don't have DbContext
        /// </summary>
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

                    var ttl = _stableKeys.Contains(key) ? StableCacheSeconds
                            : cacheSeconds > 0 ? cacheSeconds : DefaultCacheSeconds;
                    var policy = new CacheItemPolicy
                    {
                        AbsoluteExpiration = DateTimeOffset.UtcNow.AddSeconds(ttl)
                    };

                    Cache.Set(cacheKey, value, policy);
                    _knownKeys.TryAdd(key, 0);
                    return value;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning(
                    $"[ConfigurationService] DB error for key '{key}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Gets value from environment variable or Web.config
        /// </summary>
        private static string GetFromConfig(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;

            // Try exact key as env var
            var env = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(env))
                return env;

            // Try double-underscore format (e.g., Biometrics__Crypto__Entropy)
            var envAlt = Environment.GetEnvironmentVariable(key.Replace(":", "__"));
            if (!string.IsNullOrWhiteSpace(envAlt))
                return envAlt;

            // Try Web.config appSettings
            var cfg = ConfigurationManager.AppSettings[key];
            if (!string.IsNullOrWhiteSpace(cfg))
                return cfg;

            return null;
        }

        #endregion
    }
}
