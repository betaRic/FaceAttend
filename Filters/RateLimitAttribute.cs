using System;
using System.Linq;
using System.Net;
using System.Runtime.Caching;
using System.Web.Mvc;

namespace FaceAttend.Filters
{
    /// <summary>
    /// Per-IP token-bucket rate limiter na may burst capacity.
    ///
    /// Dagdag na hardening:
    /// - KioskAttend uses composite key (clientIp + sessionId) para mas fair sa shared NAT.
    /// - X-Forwarded-For is trusted only from configured proxies / CIDRs.
    /// - May hard cap pa rin sa direct connection IP kapag may trusted proxy sa unahan.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class,
                    AllowMultiple = true, Inherited = true)]
    public class RateLimitAttribute : ActionFilterAttribute
    {
        private static readonly MemoryCache _cache = MemoryCache.Default;
        private const string CachePrefix        = "RATELIMIT_TB::";
        private const string CachePrefixHardCap = "RATELIMIT_HC::";

        private static int GetXffHardCapPerMinute()
        {
            var v = System.Configuration.ConfigurationManager.AppSettings["RateLimit:XffHardCapPerMinute"];
            return int.TryParse(v, out var n) && n > 0 ? n : 60;
        }

        private static string GetTrustedProxyCidrsRaw()
        {
            return (System.Configuration.ConfigurationManager.AppSettings["RateLimit:TrustedProxyCidrs"] ?? "").Trim();
        }

        public string Name          { get; set; } = "default";
        public int    MaxRequests   { get; set; } = 10;
        public int    WindowSeconds { get; set; } = 60;
        public int    Burst         { get; set; } = 0;

        public override void OnActionExecuting(ActionExecutingContext filterContext)
        {
            if (MaxRequests <= 0 || WindowSeconds <= 0)
            {
                base.OnActionExecuting(filterContext);
                return;
            }

            var request  = filterContext.HttpContext.Request;
            var directIp = request.UserHostAddress ?? "unknown";
            var clientIp = GetClientIp(filterContext);
            var usingXff = !string.Equals(directIp, clientIp, StringComparison.OrdinalIgnoreCase);

            var fairnessKey = clientIp;
            if (string.Equals(Name, "KioskAttend", StringComparison.OrdinalIgnoreCase))
            {
                var sessionId = filterContext.HttpContext?.Session?.SessionID;
                if (!string.IsNullOrWhiteSpace(sessionId))
                    fairnessKey = clientIp + "|" + sessionId;
            }

            var cacheKey1 = $"{CachePrefix}{Name}:{fairnessKey}";
            var bucket1 = GetOrCreateBucket(cacheKey1, MaxRequests, Burst);

            bool denied1;
            double retryAfterSecs1;

            lock (bucket1)
            {
                ConsumeToken(bucket1, MaxRequests, WindowSeconds, Burst, out denied1, out retryAfterSecs1);
            }

            if (denied1)
            {
                WriteDeniedResult(filterContext, (int)retryAfterSecs1);
                return;
            }

            if (usingXff)
            {
                var hardCap = GetXffHardCapPerMinute();
                var cacheKey2 = $"{CachePrefixHardCap}{directIp}";
                var bucket2 = GetOrCreateBucket(cacheKey2, hardCap, 0);

                bool denied2;
                double retryAfterSecs2;

                lock (bucket2)
                {
                    ConsumeToken(bucket2, hardCap, 60, 0, out denied2, out retryAfterSecs2);
                }

                if (denied2)
                {
                    WriteDeniedResult(filterContext, (int)retryAfterSecs2);
                    return;
                }
            }

            base.OnActionExecuting(filterContext);
        }

        private static void ConsumeToken(
            TokenBucket bucket,
            int maxRequests,
            int windowSeconds,
            int burst,
            out bool denied,
            out double retryAfterSecs)
        {
            var now = DateTimeOffset.UtcNow;
            var elapsed = (now - bucket.LastRefill).TotalSeconds;
            if (elapsed < 0) elapsed = 0;

            double rate = (double)maxRequests / windowSeconds;
            if (rate <= 0)
            {
                denied = false;
                retryAfterSecs = 0;
                return;
            }

            bucket.Tokens = Math.Min(bucket.Tokens + elapsed * rate, maxRequests + burst);
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

        private static void WriteDeniedResult(ActionExecutingContext filterContext, int retryAfter)
        {
            var response = filterContext.HttpContext.Response;
            response.StatusCode = 429;
            response.AddHeader("Retry-After", retryAfter.ToString());

            filterContext.Result = new JsonResult
            {
                Data = new
                {
                    ok = false,
                    error = "RATE_LIMIT_EXCEEDED",
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
                Tokens = maxRequests + burst,
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
            var request = ctx.HttpContext.Request;
            var directIp = request.UserHostAddress ?? "unknown";

            if (IsTrustedForwarder(directIp))
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

        private static bool IsTrustedForwarder(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip)) return false;
            if (ip == "127.0.0.1" || ip == "::1" || ip == "localhost") return true;

            var raw = GetTrustedProxyCidrsRaw();
            if (string.IsNullOrWhiteSpace(raw)) return false;

            var items = raw.Split(',')
                           .Select(x => x.Trim())
                           .Where(x => !string.IsNullOrWhiteSpace(x));

            foreach (var item in items)
            {
                if (IpMatches(ip, item))
                    return true;
            }

            return false;
        }

        private static bool IpMatches(string ip, string cidrOrIp)
        {
            IPAddress address;
            if (!IPAddress.TryParse(ip, out address))
                return false;

            if (string.IsNullOrWhiteSpace(cidrOrIp))
                return false;

            if (!cidrOrIp.Contains("/"))
            {
                IPAddress exact;
                return IPAddress.TryParse(cidrOrIp, out exact) && exact.Equals(address);
            }

            var parts = cidrOrIp.Split('/');
            if (parts.Length != 2) return false;

            IPAddress network;
            int prefix;
            if (!IPAddress.TryParse(parts[0], out network)) return false;
            if (!int.TryParse(parts[1], out prefix)) return false;

            var addrBytes = address.GetAddressBytes();
            var netBytes = network.GetAddressBytes();

            // Keep it simple and safe: IPv4 CIDR only.
            if (addrBytes.Length != 4 || netBytes.Length != 4) return false;
            if (prefix < 0 || prefix > 32) return false;

            var addr = ToUInt32(addrBytes);
            var net = ToUInt32(netBytes);
            var mask = prefix == 0 ? 0u : 0xffffffffu << (32 - prefix);

            return (addr & mask) == (net & mask);
        }

        private static uint ToUInt32(byte[] bytes)
        {
            if (BitConverter.IsLittleEndian)
                bytes = bytes.Reverse().ToArray();

            return BitConverter.ToUInt32(bytes, 0);
        }

        private class TokenBucket
        {
            public double Tokens;
            public DateTimeOffset LastRefill;
        }
    }
}