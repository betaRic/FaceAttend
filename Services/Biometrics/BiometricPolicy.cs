using System;

namespace FaceAttend.Services.Biometrics
{
    public enum BiometricScanMode
    {
        Kiosk,
        PublicScan,
        Enrollment
    }

    public enum AntiSpoofDecision
    {
        Pass,
        Retry,
        Review,
        Block,
        ModelError
    }

    public sealed class AntiSpoofPolicyResult
    {
        public AntiSpoofDecision Decision { get; set; }
        public string Policy { get; set; }
        public float Score { get; set; }
        public float ClearThreshold { get; set; }
        public float ReviewThreshold { get; set; }
        public float BlockThreshold { get; set; }
        public bool ModelOk { get; set; }
        public bool AcceptedForRecording =>
            Decision == AntiSpoofDecision.Pass ||
            Decision == AntiSpoofDecision.Review ||
            Decision == AntiSpoofDecision.ModelError;
        public bool NeedsReview => Decision == AntiSpoofDecision.Review || Decision == AntiSpoofDecision.ModelError;
    }

    /// <summary>
    /// Canonical biometric thresholds and model identity.
    /// Keep all identity/security thresholds here; UI hints can vary, server authority cannot.
    /// </summary>
    public sealed class BiometricPolicy
    {
        public string ModelVersion { get; private set; }
        public string DetectorModel { get; private set; }
        public string RecognizerModel { get; private set; }
        public string AntiSpoofModel { get; private set; }
        public string DistanceMetric { get; private set; }
        public int EmbeddingDim { get; private set; }
        public double HighDistanceThreshold { get; private set; }
        public double MediumDistanceThreshold { get; private set; }
        public double AttendanceTolerance { get; private set; }
        public double MobileAttendanceTolerance { get; private set; }
        public double EnrollmentStrictTolerance { get; private set; }
        public double EnrollmentRiskTolerance { get; private set; }
        public float AntiSpoofClearThreshold { get; private set; }
        public float AntiSpoofReviewThreshold { get; private set; }
        public float AntiSpoofBlockThreshold { get; private set; }
        public string AntiSpoofMode { get; private set; }
        public string AntiSpoofModelFailureAction { get; private set; }
        public float KioskSharpnessThreshold { get; private set; }
        public float MobileSharpnessThreshold { get; private set; }
        public double EnrollmentMinFaceAreaRatio { get; private set; }
        public double MobileEnrollmentMinFaceAreaRatio { get; private set; }
        public double DominantFaceAreaRatio { get; private set; }
        public double AmbiguityRelativeGap { get; private set; }

        public static BiometricPolicy Current => Load();

        public static BiometricPolicy Load()
        {
            return new BiometricPolicy
            {
                ModelVersion = ConfigurationService.GetString("Biometrics:ModelVersion", "openvino-v1"),
                DetectorModel = ConfigurationService.GetString("Biometrics:DetectorModel",
                    ConfigurationService.GetString("Biometrics:Detector", "face-detection-retail-0004")),
                RecognizerModel = ConfigurationService.GetString("Biometrics:RecognizerModel",
                    ConfigurationService.GetString("Biometrics:RecognitionModel", "face-reidentification-retail-0095")),
                AntiSpoofModel = ConfigurationService.GetString("Biometrics:AntiSpoofModel", "anti-spoof-mn3"),
                DistanceMetric = ConfigurationService.GetString("Biometrics:DistanceMetric", "euclidean_l2_normalized"),
                EmbeddingDim = ConfigurationService.GetInt("Biometrics:EmbeddingDim", 256),
                HighDistanceThreshold = ConfigurationService.GetDouble("Biometrics:Match:HighDistThreshold", 0.40),
                MediumDistanceThreshold = ConfigurationService.GetDouble("Biometrics:Match:MedDistThreshold", 0.55),
                AttendanceTolerance = ConfigurationService.GetDouble("Biometrics:AttendanceTolerance", 0.50),
                MobileAttendanceTolerance = ConfigurationService.GetDouble("Biometrics:MobileAttendanceTolerance", 0.48),
                EnrollmentStrictTolerance = ConfigurationService.GetDouble("Biometrics:EnrollmentStrictTolerance", 0.45),
                EnrollmentRiskTolerance = ConfigurationService.GetDouble("Biometrics:EnrollmentRiskTolerance", 0.55),
                AntiSpoofClearThreshold = (float)ConfigurationService.GetDouble("Biometrics:AntiSpoof:ClearThreshold", 0.45),
                AntiSpoofReviewThreshold = (float)ConfigurationService.GetDouble("Biometrics:AntiSpoof:ReviewThreshold", 0.30),
                AntiSpoofBlockThreshold = (float)ConfigurationService.GetDouble("Biometrics:AntiSpoof:BlockThreshold", 0.15),
                AntiSpoofMode = Normalize(ConfigurationService.GetString("Biometrics:AntiSpoof:Mode", "REVIEW_FIRST")),
                AntiSpoofModelFailureAction = Normalize(ConfigurationService.GetString("Biometrics:AntiSpoof:ModelFailureAction", "REVIEW")),
                KioskSharpnessThreshold = (float)ConfigurationService.GetDouble("Biometrics:Enroll:SharpnessThreshold", 35),
                MobileSharpnessThreshold = (float)ConfigurationService.GetDouble("Biometrics:Enroll:SharpnessThreshold:Mobile", 28),
                EnrollmentMinFaceAreaRatio = ConfigurationService.GetDouble("Biometrics:Enroll:MinFaceAreaRatio", 0.08),
                MobileEnrollmentMinFaceAreaRatio = ConfigurationService.GetDouble("Biometrics:Enroll:MinFaceAreaRatio:Mobile", 0.06),
                DominantFaceAreaRatio = ConfigurationService.GetDouble("Biometrics:Face:DominantFaceRatio", 1.8),
                AmbiguityRelativeGap = ConfigurationService.GetDouble("Biometrics:Match:AmbiguityRelativeGap", 0.25)
            };
        }

        public double AttendanceToleranceFor(bool isMobile)
        {
            var configured = isMobile ? MobileAttendanceTolerance : AttendanceTolerance;
            return Math.Max(0.40, Math.Min(MediumDistanceThreshold, configured));
        }

        public float AntiSpoofClearThresholdFor(bool isMobile)
        {
            if (!isMobile) return AntiSpoofClearThreshold;
            return (float)ConfigurationService.GetDouble("Biometrics:AntiSpoof:MobileClearThreshold", AntiSpoofClearThreshold);
        }

        public AntiSpoofPolicyResult EvaluateAntiSpoof(bool modelOk, float? score, bool isMobile)
        {
            var clear = AntiSpoofClearThresholdFor(isMobile);
            var s = score ?? 0f;
            var result = new AntiSpoofPolicyResult
            {
                Policy = AntiSpoofMode,
                Score = s,
                ClearThreshold = clear,
                ReviewThreshold = AntiSpoofReviewThreshold,
                BlockThreshold = AntiSpoofBlockThreshold,
                ModelOk = modelOk
            };

            if (!modelOk)
            {
                result.Decision = FailureActionToDecision();
                return result;
            }

            if (s >= clear)
                result.Decision = AntiSpoofDecision.Pass;
            else if (s <= AntiSpoofBlockThreshold)
                result.Decision = AntiSpoofDecision.Block;
            else if (s >= AntiSpoofReviewThreshold)
                result.Decision = AntiSpoofDecision.Review;
            else
                result.Decision = AntiSpoofDecision.Retry;

            if (string.Equals(AntiSpoofMode, "STRICT", StringComparison.OrdinalIgnoreCase) &&
                result.Decision != AntiSpoofDecision.Pass)
            {
                result.Decision = AntiSpoofDecision.Block;
            }

            return result;
        }

        private AntiSpoofDecision FailureActionToDecision()
        {
            if (string.Equals(AntiSpoofModelFailureAction, "PASS", StringComparison.OrdinalIgnoreCase))
                return AntiSpoofDecision.Pass;
            if (string.Equals(AntiSpoofModelFailureAction, "BLOCK", StringComparison.OrdinalIgnoreCase))
                return AntiSpoofDecision.Block;
            if (string.Equals(AntiSpoofModelFailureAction, "RETRY", StringComparison.OrdinalIgnoreCase))
                return AntiSpoofDecision.Retry;

            return AntiSpoofDecision.ModelError;
        }

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "" : value.Trim().ToUpperInvariant();
        }
    }
}
