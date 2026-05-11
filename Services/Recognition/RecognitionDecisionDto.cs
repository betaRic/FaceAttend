using System;

namespace FaceAttend.Services.Recognition
{
    public sealed class RecognitionDecisionDto
    {
        public string DecisionCode { get; set; }
        public bool Accepted { get; set; }
        public string SourceType { get; set; }
        public string ModelVersion { get; set; }
        public string DetectorVersion { get; set; }
        public string RecognizerVersion { get; set; }
        public string AntiSpoofVersion { get; set; }
        public string DistanceMetric { get; set; }
        public int EmbeddingDim { get; set; }
        public QualityDto Quality { get; set; }
        public MatchDto Match { get; set; }
        public long? ProcessingMs { get; set; }

        public sealed class QualityDto
        {
            public double Score { get; set; }
            public float AntiSpoofScore { get; set; }
            public float AntiSpoofThreshold { get; set; }
            public bool AntiSpoofOk { get; set; }
            public string AntiSpoofDecision { get; set; }
            public string AntiSpoofPolicy { get; set; }
            public float Sharpness { get; set; }
            public float SharpnessThreshold { get; set; }
            public double? FaceAreaRatio { get; set; }
        }

        public sealed class MatchDto
        {
            public string BestEmployeeId { get; set; }
            public string SecondBestEmployeeId { get; set; }
            public double? Distance { get; set; }
            public double? SecondBestDistance { get; set; }
            public double? AmbiguityGap { get; set; }
            public double? Threshold { get; set; }
            public double? Confidence { get; set; }
            public string Tier { get; set; }
            public bool Ambiguous { get; set; }
        }

        public static double ComputeQualityScore(
            float sharpness,
            float sharpnessThreshold,
            float antiSpoofScore,
            float antiSpoofThreshold,
            double? faceAreaRatio)
        {
            var sharpnessPart = sharpnessThreshold <= 0
                ? 1.0
                : Clamp(sharpness / Math.Max(sharpnessThreshold * 1.35f, 1f), 0, 1);

            var antiSpoofPart = antiSpoofThreshold <= 0
                ? 1.0
                : Clamp(antiSpoofScore / Math.Max(antiSpoofThreshold * 1.35f, 0.01f), 0, 1);

            var areaPart = faceAreaRatio.HasValue
                ? Clamp(faceAreaRatio.Value / 0.18, 0, 1)
                : 0.75;

            return Math.Round((sharpnessPart * 0.35) + (antiSpoofPart * 0.45) + (areaPart * 0.20), 4);
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}
