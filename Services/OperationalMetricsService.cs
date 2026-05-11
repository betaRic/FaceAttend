using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Web.Mvc;

namespace FaceAttend.Services
{
    public static class OperationalMetricsService
    {
        private const int MaxSamples = 500;
        private static readonly object Sync = new object();
        private static readonly Queue<ScanSample> Samples = new Queue<ScanSample>();

        public sealed class Snapshot
        {
            public int TotalScans { get; set; }
            public int SuccessCount { get; set; }
            public int FailureCount { get; set; }
            public int BusyCount { get; set; }
            public double AverageMs { get; set; }
            public long P50Ms { get; set; }
            public long P95Ms { get; set; }
            public List<ScanSample> RecentFailures { get; set; } = new List<ScanSample>();
        }

        public sealed class ScanSample
        {
            public DateTime TimestampLocal { get; set; }
            public long DurationMs { get; set; }
            public bool Ok { get; set; }
            public string Outcome { get; set; }
        }

        public static void RecordScan(long durationMs, ActionResult result)
        {
            var ok = false;
            var outcome = "UNKNOWN";

            var json = result as JsonResult;
            if (json?.Data != null)
            {
                ok = GetBool(json.Data, "ok");
                outcome = GetString(json.Data, "error")
                    ?? GetString(json.Data, "action")
                    ?? (ok ? "OK" : "UNKNOWN");
            }

            Add(durationMs, ok, outcome);
        }

        public static void RecordBusy()
        {
            Add(0, false, "SYSTEM_BUSY");
        }

        public static Snapshot GetSnapshot()
        {
            lock (Sync)
            {
                var rows = Samples.ToList();
                var durations = rows
                    .Where(x => x.DurationMs > 0)
                    .Select(x => x.DurationMs)
                    .OrderBy(x => x)
                    .ToList();

                return new Snapshot
                {
                    TotalScans = rows.Count,
                    SuccessCount = rows.Count(x => x.Ok),
                    FailureCount = rows.Count(x => !x.Ok),
                    BusyCount = rows.Count(x => x.Outcome == "SYSTEM_BUSY"),
                    AverageMs = durations.Count == 0 ? 0 : durations.Average(),
                    P50Ms = Percentile(durations, 0.50),
                    P95Ms = Percentile(durations, 0.95),
                    RecentFailures = rows
                        .Where(x => !x.Ok)
                        .OrderByDescending(x => x.TimestampLocal)
                        .Take(12)
                        .ToList()
                };
            }
        }

        private static void Add(long durationMs, bool ok, string outcome)
        {
            lock (Sync)
            {
                Samples.Enqueue(new ScanSample
                {
                    TimestampLocal = TimeZoneHelper.NowLocal(),
                    DurationMs = durationMs,
                    Ok = ok,
                    Outcome = string.IsNullOrWhiteSpace(outcome) ? (ok ? "OK" : "UNKNOWN") : outcome
                });

                while (Samples.Count > MaxSamples)
                    Samples.Dequeue();
            }
        }

        private static long Percentile(IList<long> values, double percentile)
        {
            if (values == null || values.Count == 0) return 0;
            var index = (int)Math.Ceiling(values.Count * percentile) - 1;
            if (index < 0) index = 0;
            if (index >= values.Count) index = values.Count - 1;
            return values[index];
        }

        private static bool GetBool(object source, string name)
        {
            var prop = GetProperty(source, name);
            if (prop == null) return false;
            var value = prop.GetValue(source, null);
            return value is bool b && b;
        }

        private static string GetString(object source, string name)
        {
            var prop = GetProperty(source, name);
            var value = prop?.GetValue(source, null);
            return value == null ? null : Convert.ToString(value);
        }

        private static PropertyInfo GetProperty(object source, string name)
        {
            return source.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
        }
    }
}
