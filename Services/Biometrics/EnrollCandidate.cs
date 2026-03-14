using System;

namespace FaceAttend.Services.Biometrics
{
    /// <summary>
    /// Represents a candidate face frame collected during enrollment.
    /// Holds both the face vector and all quality signals used for
    /// diversity-aware selection by SelectDiverseFrames.
    /// </summary>
    public class EnrollCandidate
    {
        // Core biometric data
        public double[] Vec { get; set; }
        public float Liveness { get; set; }
        public int Area { get; set; }

        // Quality signals (from FaceQualityAnalyzer)
        public float Sharpness { get; set; }
        public float PoseYaw { get; set; }
        public float PosePitch { get; set; }
        public string PoseBucket { get; set; } = "center";

        // Composite score computed by FaceQualityAnalyzer.CalculateQualityScore
        public float QualityScore { get; set; }
    }
}
