using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Caching;

namespace FaceAttend.Services
{
    /// <summary>
    /// Nagbibigay ng access sa mga system configuration values na naka-store
    /// sa SystemConfiguration table ng database.
    ///
    /// Dalawang paraan ng pag-access:
    ///   1. Direct DB access (GetString, GetInt, etc.) — ginagamit kapag may
    ///      available na DbContext (e.g. sa loob ng controller action).
    ///   2. Cached access (GetStringCached, etc.) — ginagamit kapag walang
    ///      DbContext, o kapag kailangan ng mabilis na access na hindi gusto
    ///      mag-open ng bagong connection.
    ///
    /// PHASE 3 FIX (Q-03): Na-implement na ang InvalidateAll().
    ///
    /// PROBLEMA DATI:
    ///   Ang InvalidateAll() ay stub lang — walang ginagawa. Kapag nag-save ng
    ///   settings ang admin, ang cached values ng ibang keys ay hindi nao-update
    ///   hanggang hindi pa nag-expire ang cache (default 60 segundo).
    ///
    /// SOLUSYON:
    ///   Gumagamit ng cache key prefix ("sysconf:") para mahanap ang lahat ng
    ///   related cache entries. Ang MemoryCache ay hindi nagbibigay ng "clear all"
    ///   API, pero pwede tayong mag-enumerate ng lahat ng keys na may matching prefix
    ///   gamit ang MemoryCache.GetCount() at custom tracking.
    ///
    ///   Mas simpleng approach: i-track ang lahat ng "written" keys sa isang
    ///   static ConcurrentBag at i-invalidate ang lahat ng known keys.
    /// </summary>
    public static class SystemConfigService
    {
        // Prefix ng lahat ng cache keys natin.
        private const string CachePrefix = "sysconf:";

        // Default cache duration: 60 segundo.
        // Pagkatapos nito, ang susunod na read ay mag-re-query sa DB.
        private const int DefaultCacheSeconds = 60;

        // Ginagamit para sa MemoryCache operations.
        private static readonly MemoryCache Cache = MemoryCache.Default;

        // PHASE 3 FIX (Q-03): I-track ang lahat ng keys na na-write sa cache
        // para ma-invalidate ang lahat sa isang tawag.
        // ConcurrentHashSet pattern gamit ang Dictionary.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte>
            _knownKeys = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(
                StringComparer.OrdinalIgnoreCase);

        // ────────────────────────────────────────────────────────────────────
        // Direct DB access — ginagamit sa loob ng controller actions
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Nagbabasa ng raw string value mula sa DB.
        /// Returns null kung hindi mahanap ang key.
        /// </summary>
        public static string GetRaw(FaceAttendDBEntities db, string key)
        {
            if (db == null || string.IsNullOrWhiteSpace(key)) return null;
            var row = db.SystemConfigurations
                .FirstOrDefault(x => x.Key == key);
            return row?.Value;
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
            return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                ? n : fallback;
        }

        public static bool GetBool(FaceAttendDBEntities db, string key, bool fallback)
        {
            var v = GetRaw(db, key);
            if (string.IsNullOrWhiteSpace(v)) return fallback;

            if (v == "1" || v.Equals("true",  StringComparison.OrdinalIgnoreCase) ||
                            v.Equals("yes",   StringComparison.OrdinalIgnoreCase))
                return true;

            if (v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                            v.Equals("no",    StringComparison.OrdinalIgnoreCase))
                return false;

            return fallback;
        }

        public static double GetDouble(FaceAttendDBEntities db, string key, double fallback)
        {
            var v = GetRaw(db, key);
            if (string.IsNullOrWhiteSpace(v)) return fallback;
            return double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)
                ? d : fallback;
        }

        public static bool HasKey(FaceAttendDBEntities db, string key)
        {
            if (db == null || string.IsNullOrWhiteSpace(key)) return false;
            return db.SystemConfigurations.Any(x => x.Key == key);
        }

        // ────────────────────────────────────────────────────────────────────
        // Upsert at Delete
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Nag-upsert (insert o update) ng configuration value sa DB.
        /// Laging nag-i-invalidate ng cache pagkatapos ng write.
        /// </summary>
        public static void Upsert(
            FaceAttendDBEntities db,
            string key,
            string value,
            string dataType,
            string description,
            string modifiedBy)
        {
            if (db   == null) throw new ArgumentNullException(nameof(db));
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Key ay kailangan.", nameof(key));

            var now = DateTime.UtcNow;
            var row = db.SystemConfigurations.FirstOrDefault(x => x.Key == key);

            if (row == null)
            {
                row = new SystemConfiguration
                {
                    Key          = key.Trim(),
                    Value        = (value       ?? "").Trim(),
                    DataType     = (dataType    ?? "string").Trim(),
                    Description  = (description ?? "").Trim(),
                    ModifiedDate = now,
                    ModifiedBy   = (modifiedBy  ?? "ADMIN").Trim()
                };
                db.SystemConfigurations.Add(row);
            }
            else
            {
                row.Value = (value ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(dataType))    row.DataType    = dataType.Trim();
                if (!string.IsNullOrWhiteSpace(description)) row.Description = description.Trim();
                row.ModifiedDate = now;
                row.ModifiedBy   = (modifiedBy ?? "ADMIN").Trim();
            }

            db.SaveChanges();

            // I-invalidate ang cache para ang susunod na read ay mag-re-query sa DB.
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

        // ────────────────────────────────────────────────────────────────────
        // Cached access — para sa code paths na walang DbContext
        // ────────────────────────────────────────────────────────────────────

        private static string CacheKey(string key) => CachePrefix + key;

        public static void Invalidate(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            var cacheKey = CacheKey(key);
            Cache.Remove(cacheKey);
            // Alisin sa known keys tracking kapag ini-invalidate.
            // Pwedeng ibalik ito sa susunod na write.
            _knownKeys.TryRemove(key, out _);
        }

        /// <summary>
        /// PHASE 3 FIX (Q-03): Na-implement na — hindi na stub.
        ///
        /// Ino-invalidate ang lahat ng known config keys sa cache.
        /// Ginagamit pagkatapos ng bulk settings save para matiyak na
        /// ang lahat ng cached values ay fresh mula sa DB.
        ///
        /// LIMITASYON: Ini-invalidate lang ang keys na na-write through
        /// sa cache sa current app domain lifecycle. Kapag nag-restart ang
        /// app pool, ang _knownKeys ay empty na naman — pero okay lang iyon
        /// dahil ang cache mismo ay wala na ring laman.
        /// </summary>
        public static void InvalidateAll()
        {
            // Kumuha ng snapshot ng lahat ng known keys.
            var keys = _knownKeys.Keys.ToList();

            foreach (var key in keys)
            {
                var cacheKey = CacheKey(key);
                Cache.Remove(cacheKey);
            }

            // I-clear ang tracking dictionary.
            _knownKeys.Clear();

            System.Diagnostics.Trace.TraceInformation(
                $"[SystemConfigService.InvalidateAll] Ni-invalidate ang {keys.Count} cached config keys.");
        }

        private static string GetRawCached(string key, int cacheSeconds)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;

            var cacheKey = CacheKey(key);
            var cached   = Cache.Get(cacheKey) as string;
            if (cached != null) return cached;

            // Cache miss — mag-query sa DB.
            try
            {
                using (var db = new FaceAttendDBEntities())
                {
                    var raw = GetRaw(db, key);
                    if (raw == null) return null;

                    var policy = new CacheItemPolicy
                    {
                        AbsoluteExpiration = DateTimeOffset.UtcNow.AddSeconds(
                            cacheSeconds > 0 ? cacheSeconds : DefaultCacheSeconds)
                    };

                    Cache.Set(cacheKey, raw, policy);

                    // I-track ang key para ma-include sa InvalidateAll().
                    _knownKeys.TryAdd(key, 0);

                    return raw;
                }
            }
            catch (Exception ex)
            {
                // Hindi mag-crash ang app kapag nag-fail ang DB — ibalik ang null.
                System.Diagnostics.Trace.TraceWarning(
                    $"[SystemConfigService] Cache miss at DB error para sa key '{key}': " +
                    ex.Message);
                return null;
            }
        }

        public static string GetStringCached(string key, string fallback,
            int cacheSeconds = DefaultCacheSeconds)
        {
            var v = GetRawCached(key, cacheSeconds);
            return string.IsNullOrWhiteSpace(v) ? fallback : v;
        }

        public static int GetIntCached(string key, int fallback,
            int cacheSeconds = DefaultCacheSeconds)
        {
            var v = GetRawCached(key, cacheSeconds);
            if (string.IsNullOrWhiteSpace(v)) return fallback;
            return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                ? n : fallback;
        }

        public static double GetDoubleCached(string key, double fallback,
            int cacheSeconds = DefaultCacheSeconds)
        {
            var v = GetRawCached(key, cacheSeconds);
            if (string.IsNullOrWhiteSpace(v)) return fallback;
            return double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)
                ? d : fallback;
        }

        public static bool GetBoolCached(string key, bool fallback,
            int cacheSeconds = DefaultCacheSeconds)
        {
            var v = GetRawCached(key, cacheSeconds);
            if (string.IsNullOrWhiteSpace(v)) return fallback;

            if (v == "1" || v.Equals("true",  StringComparison.OrdinalIgnoreCase)) return true;
            if (v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            return fallback;
        }
    }
}
