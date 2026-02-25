using System;
using System.Runtime.Caching;
using System.Web.Mvc;

namespace FaceAttend.Filters
{
    /// <summary>
    /// Per-IP token-bucket rate limiter with burst capacity.
    ///
    /// Token-bucket behavior:
    ///   - Tokens refill continuously at a rate of MaxRequests/WindowSeconds per second.
    ///   - Burst parameter adds extra initial capacity so sudden spikes are absorbed.
    ///   - Each request consumes one token; requests when empty return HTTP 429.
    ///   - Retry-After header contains the seconds until the next token is available.
    ///
    /// Notes:
    ///   - Bucket key is (Name, IP). If you deploy behind a trusted reverse proxy,
    ///     you may enable X-Forwarded-For parsing inside GetClientIp().
    ///   - We clamp negative elapsed to 0 to guard against clock adjustments.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class,
                    AllowMultiple = true, Inherited = true)]
    public class RateLimitAttribute : ActionFilterAttribute
    {
        private static readonly MemoryCache _cache = MemoryCache.Default;
        private const string CachePrefix = "RATELIMIT_TB::";

        public string Name { get; set; } = "default";
        public int MaxRequests { get; set; } = 10;
        public int WindowSeconds { get; set; } = 60;
        public int Burst { get; set; } = 0;

        public override void OnActionExecuting(ActionExecutingContext filterContext)
        {
            // Fail-open for invalid configs.
            if (MaxRequests <= 0 || WindowSeconds <= 0)
            {
                base.OnActionExecuting(filterContext);
                return;
            }

            var ip = GetClientIp(filterContext);
            var cacheKey = $"{CachePrefix}{Name}:{ip}";

            var bucket = GetOrCreateBucket(cacheKey);

            bool denied;
            double retryAfterSecs;

            lock (bucket)
            {
                var now = DateTimeOffset.UtcNow;
                var elapsed = (now - bucket.LastRefill).TotalSeconds;
                if (elapsed < 0) elapsed = 0;

                double rate = (double)MaxRequests / WindowSeconds; // tokens per second
                if (rate <= 0)
                {
                    denied = false;
                    retryAfterSecs = 0;
                }
                else
                {
                    bucket.Tokens = Math.Min(bucket.Tokens + elapsed * rate, MaxRequests + Burst);
                    bucket.LastRefill = now;

                    if (bucket.Tokens >= 1.0)
                    {
                        bucket.Tokens -= 1.0;
                        denied = false;
                        retryAfterSecs = 0;
                    }
                    else
                    {
                        denied = true;
                        retryAfterSecs = Math.Ceiling((1.0 - bucket.Tokens) / rate);
                    }
                }
            }

            if (denied)
            {
                var response = filterContext.HttpContext.Response;
                response.StatusCode = 429;
                response.AddHeader("Retry-After", ((int)retryAfterSecs).ToString());

                filterContext.Result = new JsonResult
                {
                    Data = new
                    {
                        ok = false,
                        error = "RATE_LIMIT_EXCEEDED",
                        retryAfter = (int)retryAfterSecs
                    },
                    JsonRequestBehavior = JsonRequestBehavior.AllowGet
                };
                return;
            }

            base.OnActionExecuting(filterContext);
        }

        private TokenBucket GetOrCreateBucket(string cacheKey)
        {
            if (_cache.Get(cacheKey) is TokenBucket existing)
                return existing;

            var newBucket = new TokenBucket
            {
                Tokens = MaxRequests + Burst,
                LastRefill = DateTimeOffset.UtcNow
            };

            var policy = new CacheItemPolicy
            {
                SlidingExpiration = TimeSpan.FromMinutes(2)
            };

            var raced = _cache.AddOrGetExisting(cacheKey, newBucket, policy) as TokenBucket;
            return raced ?? newBucket;
        }

        private static string GetClientIp(ActionExecutingContext ctx)
        {
            // Only enable this if behind a trusted reverse proxy that strips spoofed headers.
            // var forwarded = ctx.HttpContext.Request.Headers["X-Forwarded-For"];
            // if (!string.IsNullOrWhiteSpace(forwarded))
            //     return forwarded.Split(',')[0].Trim();

            return ctx.HttpContext.Request.UserHostAddress ?? "unknown";
        }

        private sealed class TokenBucket
        {
            public double Tokens { get; set; }
            public DateTimeOffset LastRefill { get; set; }
        }
    }
}
