using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Globalization;
using System.Linq;
using FaceAttend.Services.Helpers;

namespace FaceAttend.Services
{
    public static class CalibrationSummaryService
    {
        public sealed class Summary
        {
            public DateTime GeneratedAtUtc { get; set; }
            public int Days { get; set; }
            public int MatchCount { get; set; }
            public int NeedsReviewCount { get; set; }
            public int NearThresholdCount { get; set; }
            public int UnsafeDistanceCount { get; set; }
            public int LowGapCount { get; set; }
            public int MobileCount { get; set; }
            public int KioskCount { get; set; }
            public int UnknownSourceCount { get; set; }
            public double MedianDistance { get; set; }
            public double P95Distance { get; set; }
            public double MaxDistance { get; set; }
            public double AverageAntiSpoof { get; set; }
            public double? MinAmbiguityGap { get; set; }
            public double AttendanceTolerance { get; set; }
            public double MobileTolerance { get; set; }
            public double NearMatchRatio { get; set; }
            public double UnsafeDistance { get; set; }
            public double LowGapThreshold { get; set; }
        }

        public static Summary Build(FaceAttendDBEntities db, int days)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));

            days = Math.Max(1, Math.Min(days, 90));
            var since = TimeZoneHelper.NowLocal().Date.AddDays(-days);
            var nearRatio = ConfigurationService.GetDoubleCached("NeedsReview:NearMatchRatio", 0.90);
            var unsafeDistance = ConfigurationService.GetDouble(
                "Biometrics:RiskAudit:UnsafeDistance",
                Biometrics.FastFaceMatcher.MedDistThresholdPublic);
            var lowGapThreshold = ConfigurationService.GetDouble("Biometrics:AmbiguityGapThreshold", 0.035);

            var rows = db.AttendanceLogs
                .AsNoTracking()
                .Where(x => !x.IsVoided && x.FaceDistance.HasValue && x.Timestamp >= since)
                .Select(x => new
                {
                    x.Timestamp,
                    x.Source,
                    x.FaceDistance,
                    x.MatchThreshold,
                    x.AntiSpoofScore,
                    x.NeedsReview,
                    x.ReviewStatus,
                    x.Notes
                })
                .ToList();

            var distances = rows
                .Select(x => x.FaceDistance.Value)
                .OrderBy(x => x)
                .ToList();

            var gaps = rows
                .Select(x => RecognitionNotesParser.ExtractAmbiguityGap(x.Notes))
                .Where(x => x.HasValue)
                .Select(x => x.Value)
                .OrderBy(x => x)
                .ToList();

            return new Summary
            {
                GeneratedAtUtc = DateTime.UtcNow,
                Days = days,
                MatchCount = rows.Count,
                NeedsReviewCount = rows.Count(x =>
                    x.NeedsReview ||
                    string.Equals(x.ReviewStatus, "PENDING", StringComparison.OrdinalIgnoreCase)),
                NearThresholdCount = rows.Count(x =>
                    x.MatchThreshold.HasValue &&
                    x.MatchThreshold.Value > 0 &&
                    x.FaceDistance.Value >= (x.MatchThreshold.Value * nearRatio)),
                UnsafeDistanceCount = rows.Count(x => x.FaceDistance.Value >= unsafeDistance),
                LowGapCount = gaps.Count(x => x <= lowGapThreshold),
                MobileCount = rows.Count(x => string.Equals(x.Source, "MOBILE", StringComparison.OrdinalIgnoreCase)),
                KioskCount = rows.Count(x => string.Equals(x.Source, "KIOSK", StringComparison.OrdinalIgnoreCase)),
                UnknownSourceCount = rows.Count(x => string.IsNullOrWhiteSpace(x.Source)),
                MedianDistance = StatsHelper.Percentile(distances, 0.50),
                P95Distance = StatsHelper.Percentile(distances, 0.95),
                MaxDistance = distances.Count == 0 ? 0 : distances[distances.Count - 1],
                AverageAntiSpoof = rows.Where(x => x.AntiSpoofScore.HasValue)
                    .Select(x => x.AntiSpoofScore.Value)
                    .DefaultIfEmpty(0)
                    .Average(),
                MinAmbiguityGap = gaps.Count == 0 ? (double?)null : gaps[0],
                AttendanceTolerance = ConfigurationService.GetDouble("Biometrics:AttendanceTolerance", 0.60),
                MobileTolerance = ConfigurationService.GetDouble("Biometrics:MobileAttendanceTolerance", 0.48),
                NearMatchRatio = nearRatio,
                UnsafeDistance = unsafeDistance,
                LowGapThreshold = lowGapThreshold
            };
        }

    }
}
