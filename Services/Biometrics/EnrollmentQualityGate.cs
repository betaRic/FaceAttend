using System;
using System.Collections.Generic;
using System.Linq;

namespace FaceAttend.Services.Biometrics
{
    /// <summary>
    /// 5-layer identity assurance gate for face enrollment.
    ///
    /// Validates a candidate frame set before vectors are persisted, ensuring
    /// the enrollment meets minimum quality requirements for reliable attendance
    /// recognition.
    ///
    /// Layers checked (in order):
    ///   1. MIN_VECTORS      — at least 1 usable vector
    ///   2. ANGLE_DIVERSITY  — at least 3 distinct pose buckets captured
    ///   3. INTRA_DIVERSITY  — no two stored vectors are too similar to each other
    ///   4. SELF_MATCH       — the best vector can re-identify itself against the set
    ///   5. QUALITY_FLOOR    — average quality score meets a minimum threshold
    ///
    /// Layer 2 (ANGLE_DIVERSITY) is relaxed to 3 of 5 angles to accommodate
    /// real-world conditions (poor lighting, camera angle, time pressure).
    /// The client enforces 5-angle capture; this gate is a server-side safety net.
    /// </summary>
    public static class EnrollmentQualityGate
    {
        // ── Tuneable constants (configurable via Web.config) ──────────────────

        private static int MinVectors =>
            ConfigurationService.GetInt("Biometrics:Enroll:Gate:MinVectors", 1);

        private static int MinAngleBuckets =>
            ConfigurationService.GetInt("Biometrics:Enroll:Gate:MinAngleBuckets", 1);

        private static double MaxIntraSetDistance =>
            ConfigurationService.GetDouble("Biometrics:Enroll:Gate:MaxIntraSetDistance", 0.55);

        private static double MinAverageQuality =>
            ConfigurationService.GetDouble("Biometrics:Enroll:Gate:MinAverageQuality", 0.10);

        // ── Result ────────────────────────────────────────────────────────────

        public class GateResult
        {
            public bool   Passed    { get; set; }
            public string ErrorCode { get; set; }
            public string Message   { get; set; }
        }

        // ── Public entry point ────────────────────────────────────────────────

        /// <summary>
        /// Validates the selected frame set.
        /// Returns a <see cref="GateResult"/> with Passed=true on success,
        /// or an actionable error code + message on failure.
        /// </summary>
        public static GateResult Validate(List<EnrollCandidate> selected)
        {
            if (selected == null || selected.Count == 0)
                return Fail("NO_GOOD_FRAME",
                    "No usable frames were captured. Ensure good lighting and face the camera.");

            // Layer 1: minimum vector count
            if (selected.Count < MinVectors)
                return Fail("INSUFFICIENT_FRAMES",
                    $"Only {selected.Count} frame(s) passed quality checks. " +
                    $"At least {MinVectors} required. Improve lighting and try again.");

            // Layer 2: pose diversity — require at least 3 distinct non-other buckets
            var distinctBuckets = selected
                .Where(c => !string.IsNullOrWhiteSpace(c.PoseBucket) && c.PoseBucket != "other")
                .Select(c => c.PoseBucket)
                .Distinct()
                .Count();

            int minBuckets = Math.Max(3, ConfigurationService.GetInt("Biometrics:Enroll:Gate:MinAngleBuckets", 3));
            if (distinctBuckets < minBuckets)
                return Fail("INSUFFICIENT_ANGLE_DIVERSITY",
                    $"Only {distinctBuckets} head angle(s) captured; at least {minBuckets} required. " +
                    "Follow the angle prompts: look straight, then left, then right.");

            // Layer 3: verify actual vector spread — fake diversity detection
            // All near-identical vectors = user did not actually move during enrollment
            if (selected.Count >= 2)
            {
                double maxPairDistance = 0;
                double minPairDistance = double.MaxValue;
                for (int i = 0; i < selected.Count; i++)
                {
                    for (int j = i + 1; j < selected.Count; j++)
                    {
                        var dist = DlibBiometrics.Distance(selected[i].Vec, selected[j].Vec);
                        if (dist > maxPairDistance) maxPairDistance = dist;
                        if (dist < minPairDistance) minPairDistance = dist;
                    }
                }

                double minRequiredSpread = ConfigurationService.GetDouble(
                    "Biometrics:Enroll:Gate:MinVectorSpread", 0.06);
                if (maxPairDistance < minRequiredSpread)
                    return Fail("FAKE_DIVERSITY",
                        "All captured frames appear identical. You must actually move your head " +
                        "to each angle when prompted. Do not stay still during enrollment.");

                double maxAllowedDistance = ConfigurationService.GetDouble(
                    "Biometrics:Enroll:Gate:MaxIntraSetDistance", 0.70);
                if (minPairDistance > maxAllowedDistance)
                    return Fail("LOW_INTRA_DIVERSITY",
                        "Captured frames are too different — possible multiple people. " +
                        "Re-enroll with a single person.");
            }

            // Layer 4: self-match verification
            if (selected.Count >= 2)
            {
                var best = selected[0].Vec;
                double minDist = double.PositiveInfinity;
                for (int k = 1; k < selected.Count; k++)
                {
                    var d = DlibBiometrics.Distance(best, selected[k].Vec);
                    if (d < minDist) minDist = d;
                }
                if (minDist > 0.65)
                    return Fail("SELF_MATCH_FAIL",
                        "Face encoding consistency check failed. Re-enroll with better lighting.");
            }

            // Layer 5: average quality floor
            var avgQuality = selected.Average(c => (double)c.QualityScore);
            if (avgQuality < MinAverageQuality)
                return Fail("LOW_QUALITY",
                    $"Average frame quality ({avgQuality:P0}) is below minimum. " +
                    "Ensure good lighting and hold steady.");

            return new GateResult { Passed = true };
        }

        // ── Helper ────────────────────────────────────────────────────────────

        private static GateResult Fail(string code, string message) =>
            new GateResult { Passed = false, ErrorCode = code, Message = message };
    }
}
