using System;
using System.Runtime.Caching;
using System.Web.Mvc;

namespace FaceAttend.Filters
{
    /// <summary>
    /// Per-IP token-bucket rate limiter na may burst capacity.
    ///
    /// AUDIT FIX (M-07): Hard cap para sa X-Forwarded-For spoofing.
    ///
    /// PROBLEMA DATI:
    ///   Kapag naka-loob ang request sa private network, tinatanggap natin ang
    ///   X-Forwarded-For bilang tunay na client IP. Ang isang attacker sa
    ///   parehong subnet (o nakapasok sa network) ay pwedeng mag-spoof ng XFF
    ///   gamit ang iba-ibang IP para ma-bypass ang rate limiting.
    ///
    /// SOLUSYON:
    ///   Dalawang-layer na rate limiting:
    ///   1. Per-XFF IP: ginagamit ang XFF IP bilang pangunahing key (tulad ng dati).
    ///   2. Per-directIp (UserHostAddress): palaging nag-a-apply ng minimum hard cap
    ///      (XffHardCapPerMinute, default 60/min) sa tunay na source IP.
    ///
    ///   Kahit i-spoof ng attacker ang XFF, ang tunay na IP (UserHostAddress)
    ///   ay mananatiling naka-throttle. Hindi maaaring ma-bypass ang rate limit
    ///   sa pamamagitan ng XFF spoofing.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class,
                    AllowMultiple = true, Inherited = true)]
    public class RateLimitAttribute : ActionFilterAttribute
    {
        private static readonly MemoryCache _cache = MemoryCache.Default;
        private const string CachePrefix        = "RATELIMIT_TB::";
        private const string CachePrefixHardCap = "RATELIMIT_HC::";

        // Hard cap applied to the real connection IP when using X-Forwarded-For.
        // Configurable via Web.config: RateLimit:XffHardCapPerMinute (default 60).
        private static int GetXffHardCapPerMinute()
        {
            var v = System.Configuration.ConfigurationManager.AppSettings["RateLimit:XffHardCapPerMinute"];
            return int.TryParse(v, out var n) && n > 0 ? n : 60;
        }

        public string Name          { get; set; } = "default";
        public int    MaxRequests   { get; set; } = 10;
        public int    WindowSeconds { get; set; } = 60;
        public int    Burst         { get; set; } = 0;

        public override void OnActionExecuting(ActionExecutingContext filterContext)
        {
            // Fail-open kapag invalid ang config.
            if (MaxRequests <= 0 || WindowSeconds <= 0)
            {
                base.OnActionExecuting(filterContext);
                return;
            }

            var request   = filterContext.HttpContext.Request;
            var directIp  = request.UserHostAddress ?? "unknown";
            var clientIp  = GetClientIp(filterContext);
            var usingXff  = !string.Equals(directIp, clientIp, StringComparison.OrdinalIgnoreCase);

            // ── Layer 1: Per-clientIp (XFF or directIp) token bucket ──────────────
            var cacheKey1 = $"{CachePrefix}{Name}:{clientIp}";
            var bucket1   = GetOrCreateBucket(cacheKey1, MaxRequests, Burst);

            bool   denied1;
            double retryAfterSecs1;

            lock (bucket1)
            {
                ConsumeToken(bucket1, MaxRequests, WindowSeconds, Burst,
                    out denied1, out retryAfterSecs1);
            }

            if (denied1)
            {
                WriteDeniedResult(filterContext, (int)retryAfterSecs1);
                return;
            }

            // ── Layer 2: Hard cap on directIp when XFF is in use ─────────────────
            // AUDIT FIX (M-07): Kahit i-spoof ang XFF, ang tunay na source IP
            // ay mananatiling naka-throttle ng hard cap.
            if (usingXff)
            {
                var hardCap    = GetXffHardCapPerMinute();
                var cacheKey2  = $"{CachePrefixHardCap}{directIp}";
                var bucket2    = GetOrCreateBucket(cacheKey2, hardCap, 0);

                bool   denied2;
                double retryAfterSecs2;

                lock (bucket2)
                {
                    ConsumeToken(bucket2, hardCap, 60, 0,
                        out denied2, out retryAfterSecs2);
                }

                if (denied2)
                {
                    WriteDeniedResult(filterContext, (int)retryAfterSecs2);
                    return;
                }
            }

            base.OnActionExecuting(filterContext);
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private static void ConsumeToken(
            TokenBucket bucket,
            int maxRequests,
            int windowSeconds,
            int burst,
            out bool denied,
            out double retryAfterSecs)
        {
            var now     = DateTimeOffset.UtcNow;
            var elapsed = (now - bucket.LastRefill).TotalSeconds;
            if (elapsed < 0) elapsed = 0;

            double rate = (double)maxRequests / windowSeconds;

            if (rate <= 0)
            {
                denied         = false;
                retryAfterSecs = 0;
                return;
            }

            bucket.Tokens    = Math.Min(bucket.Tokens + elapsed * rate, maxRequests + burst);
            bucket.LastRefill = now;

            if (bucket.Tokens >= 1.0)
            {
                bucket.Tokens -= 1.0;
                denied         = false;
                retryAfterSecs = 0;
            }
            else
            {
                denied         = true;
                retryAfterSecs = Math.Ceiling((1.0 - bucket.Tokens) / rate);
            }
        }

        private static void WriteDeniedResult(ActionExecutingContext filterContext, int retryAfter)
        {
            var response = filterContext.HttpContext.Response;
            response.StatusCode = 429;
            response.AddHeader("Retry-After", retryAfter.ToString());

            filterContext.Result = new JsonResult
            {
                Data = new
                {
                    ok         = false,
                    error      = "RATE_LIMIT_EXCEEDED",
                    retryAfter
                },
                JsonRequestBehavior = JsonRequestBehavior.AllowGet
            };
        }

        private TokenBucket GetOrCreateBucket(string cacheKey, int maxRequests, int burst)
        {
            if (_cache.Get(cacheKey) is TokenBucket existing)
                return existing;

            var newBucket = new TokenBucket
            {
                Tokens     = maxRequests + burst,
                LastRefill = DateTimeOffset.UtcNow
            };

            var policy = new CacheItemPolicy
            {
                SlidingExpiration = TimeSpan.FromMinutes(2)
            };

            var raced = _cache.AddOrGetExisting(cacheKey, newBucket, policy) as TokenBucket;
            return raced ?? newBucket;
        }

        /// <summary>
        /// Kinukuha ang tunay na client IP.
        /// Hierarchy ng trust:
        ///   1. X-Forwarded-For — kung nagmula ang request sa lokal/trusted na IP.
        ///   2. UserHostAddress — ang direktang IP ng koneksyon.
        /// </summary>
        private static string GetClientIp(ActionExecutingContext ctx)
        {
            var request   = ctx.HttpContext.Request;
            var directIp  = request.UserHostAddress ?? "unknown";

            if (IsPrivateOrLoopback(directIp))
            {
                var forwarded = request.Headers["X-Forwarded-For"];
                if (!string.IsNullOrWhiteSpace(forwarded))
                {
                    var firstIp = forwarded.Split(',')[0].Trim();
                    if (!string.IsNullOrWhiteSpace(firstIp))
                        return firstIp;
                }
            }

            return directIp;
        }

        private static bool IsPrivateOrLoopback(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip)) return false;
            if (ip == "127.0.0.1" || ip == "::1" || ip == "localhost") return true;

            var parts = ip.Split('.');
            if (parts.Length != 4) return false;

            if (!byte.TryParse(parts[0], out var a)) return false;
            if (!byte.TryParse(parts[1], out var b)) return false;

            // 10.x.x.x
            if (a == 10) return true;
            // 172.16.x.x – 172.31.x.x
            if (a == 172 && b >= 16 && b <= 31) return true;
            // 192.168.x.x
            if (a == 192 && b == 168) return true;

            return false;
        }

        private class TokenBucket
        {
            public double        Tokens    { get; set; }
            public DateTimeOffset LastRefill { get; set; }
        }
    }
}
