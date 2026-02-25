using System;
using System.Collections.Generic;

namespace FaceAttend.Services.Biometrics
{
    /// <summary>
    /// Adaptive threshold tuning for mixed or older devices.
    /// This never makes matching more lenient than the configured base tolerance.
    /// </summary>
    public static class FaceMatchTuner
    {
        private const double AbsoluteFloor = 0.50;

        public static AdaptiveThreshold CalculateAdaptiveThreshold(
            double baseTolerance,
            double imageBrightness,
            double faceDetectionScore,
            bool isMobile,
            int imageWidth)
        {
            var adjustment = 0.0;
            var reasons = new List<string>();

            if (isMobile)
            {
                adjustment -= 0.02;
                reasons.Add("mobile_device");
            }

            if (imageBrightness < 60)
            {
                adjustment -= 0.03;
                reasons.Add("low_light");
            }
            else if (imageBrightness > 200)
            {
                adjustment -= 0.01;
                reasons.Add("overexposed");
            }

            if (faceDetectionScore < 0.70)
            {
                adjustment -= 0.02;
                reasons.Add("low_confidence");
            }

            if (imageWidth > 0 && imageWidth < 320)
            {
                adjustment -= 0.02;
                reasons.Add("low_resolution");
            }

            // Never more lenient than base, never below the security floor.
            var adjusted = baseTolerance + adjustment;
            if (adjusted > baseTolerance) adjusted = baseTolerance;
            if (adjusted < AbsoluteFloor) adjusted = AbsoluteFloor;

            return new AdaptiveThreshold
            {
                BaseTolerance = baseTolerance,
                AdjustedTolerance = adjusted,
                Adjustment = adjustment,
                Reasons = reasons,
                QualityScore = CalculateQualityScore(imageBrightness, faceDetectionScore, imageWidth)
            };
        }

        private static int CalculateQualityScore(double brightness, double confidence, int width)
        {
            var brightnessScore = (int)Math.Max(0, Math.Min(100, brightness / 2.55));
            var confidenceScore = (int)Math.Max(0, Math.Min(100, confidence * 100));
            var resolutionScore = width <= 0 ? 60 : (int)Math.Max(0, Math.Min(100, width / 10.0));

            return (int)(confidenceScore * 0.5 + brightnessScore * 0.3 + resolutionScore * 0.2);
        }
    }

    public class AdaptiveThreshold
    {
        public double BaseTolerance { get; set; }
        public double AdjustedTolerance { get; set; }
        public double Adjustment { get; set; }
        public List<string> Reasons { get; set; }
        public int QualityScore { get; set; }
    }
}
