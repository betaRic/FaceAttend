using System;
using System.Collections.Concurrent;

namespace FaceAttend.Services.Security
{
    // Deprecated: this helper is not used by the app.
    // The live endpoints use Filters/RateLimitAttribute (token bucket) instead.
    // Safe to delete from the project when convenient.
    public static class RateLimit
    {
        private class Counter
        {
            public readonly object LockObj = new object();
            public DateTime WindowStartUtc;
            public int Count;
        }

        private static readonly ConcurrentDictionary<string, Counter> Counters = new ConcurrentDictionary<string, Counter>();

        // Returns true if the caller is allowed.
        public static bool Allow(string key, int limit, TimeSpan window, out int remaining)
        {
            remaining = 0;
            if (string.IsNullOrWhiteSpace(key)) return true;
            if (limit <= 0) return true;
            if (window <= TimeSpan.Zero) window = TimeSpan.FromMinutes(1);

            var now = DateTime.UtcNow;
            var c = Counters.GetOrAdd(key, _ => new Counter { WindowStartUtc = now, Count = 0 });

            lock (c.LockObj)
            {
                if (now - c.WindowStartUtc >= window)
                {
                    c.WindowStartUtc = now;
                    c.Count = 0;
                }

                if (c.Count >= limit)
                {
                    remaining = 0;
                    return false;
                }

                c.Count++;
                remaining = Math.Max(0, limit - c.Count);
                return true;
            }
        }
    }
}
