using System;
using System.Runtime.Caching;
using System.Web.Mvc;

namespace FaceAttend.Filters
{
    /// <summary>
    /// Per-IP token-bucket rate limiter na may burst capacity.
    ///
    /// Token-bucket behavior:
    ///   - Nagre-refill ang tokens nang tuluy-tuloy sa rate na MaxRequests/WindowSeconds bawat segundo.
    ///   - Ang Burst parameter ay nagdadagdag ng extra initial capacity para ma-absorb ang biglang spikes.
    ///   - Bawat request ay kumokonsumo ng isang token; kapag wala nang token, nagbabalik ng HTTP 429.
    ///   - Ang Retry-After header ay naglalaman ng mga segundo bago available ang susunod na token.
    ///
    /// PHASE 3 FIX (S-10): X-Forwarded-For extraction — naka-enable na.
    ///
    /// PROBLEMA DATI:
    ///   Ang GetClientIp() ay gumagamit lang ng Request.UserHostAddress — na para sa
    ///   lahat ng offices sa likod ng NAT/router ay iisa lang ang IP address.
    ///   Ibig sabihin, lahat ng 30 empleyado sa Office A ay nagbabahagi ng isang
    ///   rate-limit bucket — ang isang empleyado na nag-scan ng maraming beses ay
    ///   maaaring mag-lock out sa lahat ng ibang empleyado sa parehong office.
    ///
    /// SOLUSYON:
    ///   Basahin ang X-Forwarded-For header kung present — ito ang tunay na IP ng
    ///   device na nag-scan (hindi ang IP ng router/NAT ng office).
    ///
    /// BABALA SA SECURITY:
    ///   Ang X-Forwarded-For ay pwedeng i-spoof ng client (e.g. mag-set ng header
    ///   na "X-Forwarded-For: 1.2.3.4" para mawala sa rate limiting).
    ///   SOLUSYON DITO: tinatanggap lang natin ang header kapag ang request ay
    ///   nagmula sa lokal na network (private IP ranges). Ang mga external na
    ///   request ay gumagamit pa rin ng UserHostAddress.
    ///
    ///   Para sa Phase 4 (kapag may nginx reverse proxy na), mas angkop na gamitin
    ///   ang X-Real-IP header na isineset ng nginx — hindi ito pwedeng i-spoof ng client
    ///   dahil isineset ito ng proxy sa server side.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class,
                    AllowMultiple = true, Inherited = true)]
    public class RateLimitAttribute : ActionFilterAttribute
    {
        private static readonly MemoryCache _cache = MemoryCache.Default;
        private const string CachePrefix = "RATELIMIT_TB::";

        public string Name          { get; set; } = "default";
        public int    MaxRequests   { get; set; } = 10;
        public int    WindowSeconds { get; set; } = 60;
        public int    Burst         { get; set; } = 0;

        public override void OnActionExecuting(ActionExecutingContext filterContext)
        {
            // Fail-open kapag invalid ang config — mas mainam na magpatuloy
            // kaysa mag-block ng lahat ng requests.
            if (MaxRequests <= 0 || WindowSeconds <= 0)
            {
                base.OnActionExecuting(filterContext);
                return;
            }

            var ip       = GetClientIp(filterContext);
            var cacheKey = $"{CachePrefix}{Name}:{ip}";
            var bucket   = GetOrCreateBucket(cacheKey);

            bool   denied;
            double retryAfterSecs;

            lock (bucket)
            {
                var now     = DateTimeOffset.UtcNow;
                var elapsed = (now - bucket.LastRefill).TotalSeconds;
                if (elapsed < 0) elapsed = 0;  // Guard laban sa clock adjustment

                double rate = (double)MaxRequests / WindowSeconds; // tokens per second

                if (rate <= 0)
                {
                    denied        = false;
                    retryAfterSecs = 0;
                }
                else
                {
                    // Magdagdag ng tokens batay sa nakalipas na oras.
                    bucket.Tokens    = Math.Min(bucket.Tokens + elapsed * rate, MaxRequests + Burst);
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
                        ok         = false,
                        error      = "RATE_LIMIT_EXCEEDED",
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
                Tokens    = MaxRequests + Burst,
                LastRefill = DateTimeOffset.UtcNow
            };

            var policy = new CacheItemPolicy
            {
                // 2 minuto ng inactivity bago ma-expire ang bucket
                SlidingExpiration = TimeSpan.FromMinutes(2)
            };

            var raced = _cache.AddOrGetExisting(cacheKey, newBucket, policy) as TokenBucket;
            return raced ?? newBucket;
        }

        /// <summary>
        /// PHASE 3 FIX (S-10): Kinukuha ang tunay na client IP.
        ///
        /// Hierarchy ng trust:
        ///   1. X-Forwarded-For — kung nagmula ang request sa lokal/trusted na IP
        ///      (isineset ng router o reverse proxy ng internal network).
        ///   2. UserHostAddress — ang direktang IP ng koneksyon.
        ///
        /// BAKIT LOKAL LANG:
        ///   Tinatanggap lang natin ang X-Forwarded-For mula sa private IP ranges:
        ///     10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 127.0.0.1
        ///   Ang mga external IP (public internet) ay hindi pinagkakatiwalaan —
        ///   maaari nilang i-set ang X-Forwarded-For para lagusan ang rate limiting.
        ///
        /// HALIMBAWA NG FLOW SA OFFICE A:
        ///   - Office A router IP (public): 123.45.67.89
        ///   - Employee phone (private): 192.168.1.50
        ///   - X-Forwarded-For: 192.168.1.50 (isineset ng router/proxy)
        ///   - UserHostAddress: 123.45.67.89 (ang router)
        ///   - RESULT: Rate limit ay per-phone (192.168.1.50), hindi per-router.
        ///
        /// PARA SA PHASE 4 (nginx):
        ///   Kapag naka-configure na ang nginx bilang reverse proxy, gamitin na lang
        ///   ang X-Real-IP header (hindi X-Forwarded-For) dahil mas reliable:
        ///     proxy_set_header X-Real-IP $remote_addr;
        ///   Pagkatapos ay tanggapin natin ang header kahit galing sa anumang IP.
        /// </summary>
        private static string GetClientIp(ActionExecutingContext ctx)
        {
            var request        = ctx.HttpContext.Request;
            var directIp       = request.UserHostAddress ?? "unknown";

            // Tingnan kung lokal/trusted ang direktang koneksyon.
            // Kapag lokal, tinatanggap natin ang X-Forwarded-For.
            if (IsPrivateOrLoopback(directIp))
            {
                var forwarded = request.Headers["X-Forwarded-For"];
                if (!string.IsNullOrWhiteSpace(forwarded))
                {
                    // Ang X-Forwarded-For ay pwedeng may maraming IPs kung may multiple proxies.
                    // Format: "client, proxy1, proxy2"
                    // Gusto natin ang pinaka-unang IP (ang tunay na client).
                    var firstIp = forwarded.Split(',')[0].Trim();
                    if (!string.IsNullOrWhiteSpace(firstIp))
                        return firstIp;
                }
            }

            return directIp;
        }

        /// <summary>
        /// Tinitingnan kung ang IP ay lokal (private range o loopback).
        /// Tinatanggap ang IPv4 private ranges at loopback.
        /// </summary>
        private static bool IsPrivateOrLoopback(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip)) return false;

            // Loopback
            if (ip == "127.0.0.1" || ip == "::1" || ip == "localhost")
                return true;

            // Para sa simplicity: sinusuri natin ang prefix ng IP.
            // Sa production, mas tumpak ang System.Net.IPAddress parsing.
            if (ip.StartsWith("10."))         return true;  // 10.0.0.0/8
            if (ip.StartsWith("192.168."))    return true;  // 192.168.0.0/16
            if (ip.StartsWith("172."))
            {
                // 172.16.0.0/12 — sinisigurado natin ang exact range
                var parts = ip.Split('.');
                if (parts.Length >= 2 && int.TryParse(parts[1], out var second))
                    if (second >= 16 && second <= 31)
                        return true;
            }

            return false;
        }

        private sealed class TokenBucket
        {
            public double          Tokens    { get; set; }
            public DateTimeOffset  LastRefill { get; set; }
        }
    }
}
