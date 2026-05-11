using System;
using FaceAttend.Services.Biometrics;

namespace FaceAttend.Services.Recognition
{
    public static class RecognitionDecisionFactory
    {
        public static RecognitionDecisionDto FromAttendance(
            string decisionCode,
            bool accepted,
            string sourceType,
            float antiSpoofScore,
            float antiSpoofThreshold,
            AntiSpoofPolicyResult antiSpoofResult,
            float sharpness,
            float sharpnessThreshold,
            OpenVinoBiometrics.FaceBox faceBox,
            int imageWidth,
            int imageHeight,
            FastFaceMatcher.MatchResult match,
            double threshold,
            long processingMs)
        {
            return Build(
                decisionCode,
                accepted,
                sourceType,
                antiSpoofScore,
                antiSpoofThreshold,
                antiSpoofResult,
                sharpness,
                sharpnessThreshold,
                FaceAreaRatio(faceBox, imageWidth, imageHeight),
                match,
                threshold,
                processingMs);
        }

        public static RecognitionDecisionDto FromEnrollmentFrame(
            string decisionCode,
            bool accepted,
            string sourceType,
            float antiSpoofScore,
            float antiSpoofThreshold,
            AntiSpoofPolicyResult antiSpoofResult,
            float sharpness,
            float sharpnessThreshold,
            OpenVinoBiometrics.FaceBox faceBox,
            int imageWidth,
            int imageHeight,
            long processingMs)
        {
            return Build(
                decisionCode,
                accepted,
                sourceType,
                antiSpoofScore,
                antiSpoofThreshold,
                antiSpoofResult,
                sharpness,
                sharpnessThreshold,
                FaceAreaRatio(faceBox, imageWidth, imageHeight),
                null,
                0,
                processingMs);
        }

        private static RecognitionDecisionDto Build(
            string decisionCode,
            bool accepted,
            string sourceType,
            float antiSpoofScore,
            float antiSpoofThreshold,
            AntiSpoofPolicyResult antiSpoofResult,
            float sharpness,
            float sharpnessThreshold,
            double? faceAreaRatio,
            FastFaceMatcher.MatchResult match,
            double threshold,
            long processingMs)
        {
            var policy = BiometricPolicy.Current;
            var antiSpoof = antiSpoofResult ??
                policy.EvaluateAntiSpoof(true, antiSpoofScore, IsMobileSource(sourceType));
            return new RecognitionDecisionDto
            {
                DecisionCode = decisionCode,
                Accepted = accepted,
                SourceType = sourceType,
                ModelVersion = policy.ModelVersion,
                DetectorVersion = policy.DetectorModel,
                RecognizerVersion = policy.RecognizerModel,
                AntiSpoofVersion = policy.AntiSpoofModel,
                DistanceMetric = policy.DistanceMetric,
                EmbeddingDim = policy.EmbeddingDim,
                ProcessingMs = processingMs,
                Quality = new RecognitionDecisionDto.QualityDto
                {
                    Score = RecognitionDecisionDto.ComputeQualityScore(
                        sharpness,
                        sharpnessThreshold,
                        antiSpoofScore,
                        antiSpoofThreshold,
                        faceAreaRatio),
                    AntiSpoofScore = antiSpoofScore,
                    AntiSpoofThreshold = antiSpoofThreshold,
                    AntiSpoofOk = antiSpoof.Decision == AntiSpoofDecision.Pass,
                    AntiSpoofDecision = antiSpoof.Decision.ToString().ToUpperInvariant(),
                    AntiSpoofPolicy = antiSpoof.Policy,
                    Sharpness = sharpness,
                    SharpnessThreshold = sharpnessThreshold,
                    FaceAreaRatio = faceAreaRatio
                },
                Match = match == null ? null : new RecognitionDecisionDto.MatchDto
                {
                    BestEmployeeId = match.Employee?.EmployeeId,
                    SecondBestEmployeeId = match.SecondBestEmployeeId,
                    Distance = Finite(match.Distance),
                    SecondBestDistance = Finite(match.SecondBestDistance),
                    AmbiguityGap = Finite(match.AmbiguityGap),
                    Threshold = threshold,
                    Confidence = Finite(match.Confidence),
                    Tier = match.Tier.ToString().ToUpperInvariant(),
                    Ambiguous = match.WasAmbiguous
                }
            };
        }

        private static double? FaceAreaRatio(OpenVinoBiometrics.FaceBox faceBox, int imageWidth, int imageHeight)
        {
            if (faceBox == null || imageWidth <= 0 || imageHeight <= 0)
                return null;

            var area = Math.Max(0, faceBox.Width) * Math.Max(0, faceBox.Height);
            var imageArea = (double)imageWidth * imageHeight;
            return imageArea <= 0 ? (double?)null : area / imageArea;
        }

        private static double? Finite(double value)
        {
            return double.IsInfinity(value) || double.IsNaN(value) ? (double?)null : value;
        }

        private static bool IsMobileSource(string sourceType)
        {
            return !string.IsNullOrWhiteSpace(sourceType) &&
                   sourceType.IndexOf("MOBILE", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
