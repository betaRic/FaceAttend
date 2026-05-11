using System.Collections.Generic;
using System.Linq;
using FaceAttend.Models.Dtos;

namespace FaceAttend.Services.Biometrics
{
    public static class EnrollmentQualityGate
    {
        public class GateResult
        {
            public bool   Passed    { get; set; }
            public string ErrorCode { get; set; }
            public string Message   { get; set; }
        }

        public static GateResult Validate(List<EnrollCandidate> selected)
        {
            if (selected == null || selected.Count == 0)
                return Fail("NO_GOOD_FRAME",
                    "No usable frames were captured. Ensure good lighting and face the camera.");

            // Require minimum 3 diverse samples for reliable matching
            if (selected.Count < 3)
                return Fail("INSUFFICIENT_SAMPLES",
                    $"Only {selected.Count} good frame(s) captured. Need at least 3 for reliable enrollment.");

            // Check for sufficient diversity (samples shouldn't be too similar)
            var diversity = CalculateDiversity(selected);
            if (diversity < 0.15)
                return Fail("INSUFFICIENT_DIVERSITY",
                    "Captured faces are too similar. Please capture from different angles.");

            // Verify face quality scores are acceptable
            var avgQuality = selected.Average(c => c.QualityScore);
            if (avgQuality < 0.5f)
                return Fail("LOW_QUALITY_FRAMES",
                    "Captured frames have low quality. Ensure good lighting and face the camera directly.");

            return new GateResult { Passed = true };
        }

        private static double CalculateDiversity(List<EnrollCandidate> candidates)
        {
            if (candidates.Count < 2) return 1.0;

            double minDist = double.MaxValue;
            for (int i = 0; i < candidates.Count; i++)
            {
                for (int j = i + 1; j < candidates.Count; j++)
                {
                    var d = FaceVectorCodec.Distance(candidates[i].Vec, candidates[j].Vec);
                    if (d < minDist) minDist = d;
                }
            }
            return minDist;
        }

        private static GateResult Fail(string code, string message) =>
            new GateResult { Passed = false, ErrorCode = code, Message = message };
    }
}
