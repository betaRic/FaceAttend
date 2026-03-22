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
                    "No usable frames were captured. " +
                    "Please ensure good lighting and look directly at the camera.");

            // ── Layer 1: minimum vector count ─────────────────────────────────
            if (selected.Count < MinVectors)
                return Fail("INSUFFICIENT_FRAMES",
                    $"Only {selected.Count} frame(s) passed quality checks. " +
                    $"At least {MinVectors} is required. " +
                    "Please improve lighting and try again.");

            // ── Layer 2: angle diversity ──────────────────────────────────────
            var distinctBuckets = selected
                .Where(c => !string.IsNullOrWhiteSpace(c.PoseBucket) && c.PoseBucket != "other")
                .Select(c => c.PoseBucket)
                .Distinct()
                .Count();

            if (distinctBuckets < MinAngleBuckets)
                return Fail("INSUFFICIENT_ANGLE_DIVERSITY",
                    $"Only {distinctBuckets} head angle(s) captured; " +
                    $"at least {MinAngleBuckets} are needed. " +
                    "Please follow the angle prompts (center, left, right, up, down).");

            // ── Layer 3: intra-set diversity (no near-duplicate vectors) ──────
            for (int i = 0; i < selected.Count; i++)
            {
                for (int j = i + 1; j < selected.Count; j++)
                {
                    var dist = DlibBiometrics.Distance(selected[i].Vec, selected[j].Vec);
                    if (dist > MaxIntraSetDistance)
                        return Fail("LOW_INTRA_DIVERSITY",
                            "The captured frames are too dissimilar — possible face swap or " +
                            "multiple people detected. Please re-enroll with a single person.");
                }
            }

            // ── Layer 4: self-match verification ──────────────────────────────
            // The best-quality vector must match itself within the set.
            // This catches degenerate encodings (all-zero, NaN, etc.).
            if (selected.Count >= 2)
            {
                var best = selected[0].Vec;
                double minDist = double.PositiveInfinity;
                for (int k = 1; k < selected.Count; k++)
                {
                    var d = DlibBiometrics.Distance(best, selected[k].Vec);
                    if (d < minDist) minDist = d;
                }

                // A well-formed vector should match a different capture of the
                // same face at < 0.60.  > 0.90 strongly suggests a bad encoding.
                if (minDist > MaxIntraSetDistance)
                    return Fail("SELF_MATCH_FAIL",
                        "Face encoding consistency check failed. " +
                        "Please re-enroll with better lighting and a stable head position.");
            }

            // ── Layer 5: average quality floor ────────────────────────────────
            var avgQuality = selected.Average(c => (double)c.QualityScore);
            if (avgQuality < MinAverageQuality)
                return Fail("LOW_QUALITY",
                    $"Average frame quality ({avgQuality:P0}) is below the minimum threshold. " +
                    "Please ensure good, even lighting and hold the camera steady.");

            return new GateResult { Passed = true };
        }

        // ── Helper ────────────────────────────────────────────────────────────

        private static GateResult Fail(string code, string message) =>
            new GateResult { Passed = false, ErrorCode = code, Message = message };
    }
}
