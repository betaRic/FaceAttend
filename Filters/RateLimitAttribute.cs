using System;
using System.Runtime.Caching;
using System.Web.Mvc;

namespace FaceAttend.Filters
{
    /// <summary>
    /// Per-IP fixed-window rate limiter.
    /// Returns HTTP 429 with JSON body { ok: false, error: "RATE_LIMIT_EXCEEDED" }
    /// when the limit is exceeded.
    ///
    /// Usage:
    ///   [RateLimit(Name = "ScanFrame", MaxRequests = 30, WindowSeconds = 60)]
    ///   public ActionResult ScanFrame(...) { ... }
    /// </summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class,
                    AllowMultiple = true, Inherited = true)]
    public class RateLimitAttribute : ActionFilterAttribute
    {
        // Use the process-level default cache so entries survive across requests
        // but are automatically evicted when the window expires.
        private static readonly MemoryCache _cache = MemoryCache.Default;

        // Prefix all keys so they never collide with other cache entries.
        private const string CachePrefix = "RATELIMIT::";

        /// <summary>
        /// Logical name for this bucket. Use different names on different actions
        /// so limits are tracked independently (e.g. "ScanFrame" vs "ScanAttendance").
        /// </summary>
        public string Name { get; set; } = "default";

        /// <summary>Maximum number of allowed requests within <see cref="WindowSeconds"/>.</summary>
        public int MaxRequests { get; set; } = 10;

        /// <summary>Duration of the window in seconds.</summary>
        public int WindowSeconds { get; set; } = 60;

        public override void OnActionExecuting(ActionExecutingContext filterContext)
        {
            var ip = GetClientIp(filterContext);
            var cacheKey = $"{CachePrefix}{Name}:{ip}";

            var entry = GetOrAddEntry(cacheKey);

            bool limited;
            lock (entry)
            {
                var now = DateTimeOffset.UtcNow;

                // Reset counter when the window has rolled over.
                if (now >= entry.WindowEnd)
                {
                    entry.Count = 0;
                    entry.WindowEnd = now.AddSeconds(WindowSeconds);
                }

                entry.Count++;
                limited = entry.Count > MaxRequests;
            }

            if (limited)
            {
                var response = filterContext.HttpContext.Response;
                response.StatusCode = 429;
                response.AddHeader("Retry-After", WindowSeconds.ToString());
                filterContext.Result = new JsonResult
                {
                    Data = new { ok = false, error = "RATE_LIMIT_EXCEEDED" },
                    JsonRequestBehavior = JsonRequestBehavior.AllowGet
                };
                return;
            }

            base.OnActionExecuting(filterContext);
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private RateLimitEntry GetOrAddEntry(string cacheKey)
        {
            // Fast path — entry already exists.
            if (_cache.Get(cacheKey) is RateLimitEntry existing)
                return existing;

            // Slow path — create and add atomically.
            var newEntry = new RateLimitEntry
            {
                Count = 0,
                WindowEnd = DateTimeOffset.UtcNow.AddSeconds(WindowSeconds)
            };

            // Cache expires slightly after the window so the counter is
            // still available for the reset logic at the window boundary.
            var policy = new CacheItemPolicy
            {
                AbsoluteExpiration = DateTimeOffset.UtcNow.AddSeconds(WindowSeconds + 30)
            };

            // AddOrGetExisting is atomic: returns the existing item if another
            // thread beat us to the Add, otherwise returns null (our item was added).
            var raced = _cache.AddOrGetExisting(cacheKey, newEntry, policy) as RateLimitEntry;
            return raced ?? newEntry;
        }

        /// <summary>
        /// Returns the real client IP.  Does NOT trust X-Forwarded-For by default
        /// because it can be spoofed unless you sit behind a trusted reverse proxy.
        /// If you deploy behind a trusted proxy (nginx / Azure Front Door / AWS ALB),
        /// uncomment the X-Forwarded-For branch and ensure the proxy strips the
        /// header from untrusted sources.
        /// </summary>
        private static string GetClientIp(ActionExecutingContext ctx)
        {
            var request = ctx.HttpContext.Request;

            // Uncomment if behind a trusted reverse proxy:
            // var forwarded = request.Headers["X-Forwarded-For"];
            // if (!string.IsNullOrWhiteSpace(forwarded))
            //     return forwarded.Split(',')[0].Trim();

            return request.UserHostAddress ?? "unknown";
        }

        // -------------------------------------------------------------------
        // Inner types
        // -------------------------------------------------------------------

        /// <summary>Mutable counter tracked per (Name, IP) pair.</summary>
        private sealed class RateLimitEntry
        {
            public int Count { get; set; }
            public DateTimeOffset WindowEnd { get; set; }
        }
    }
}
