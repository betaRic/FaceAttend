using System;
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

            int minVectors = ConfigurationService.GetInt("Biometrics:Enroll:Gate:MinVectors", 1);
            if (selected.Count < minVectors)
                return Fail("INSUFFICIENT_FRAMES",
                    $"Only {selected.Count} frame(s) passed quality checks; at least {minVectors} required.");

            if (selected.Count >= 2)
            {
                double maxPairDist = 0;
                double minPairDist = double.MaxValue;
                for (int i = 0; i < selected.Count; i++)
                for (int j = i + 1; j < selected.Count; j++)
                {
                    var d = DlibBiometrics.Distance(selected[i].Vec, selected[j].Vec);
                    if (d > maxPairDist) maxPairDist = d;
                    if (d < minPairDist) minPairDist = d;
                }

                var minSpread = ConfigurationService.GetDouble("Biometrics:Enroll:Gate:MinVectorSpread", 0.06);
                if (maxPairDist < minSpread)
                    return Fail("FAKE_DIVERSITY",
                        "All captured frames appear identical. Move your head naturally during enrollment.");

                var maxDist = ConfigurationService.GetDouble("Biometrics:Enroll:Gate:MaxIntraSetDistance", 0.70);
                if (minPairDist > maxDist)
                    return Fail("LOW_INTRA_DIVERSITY",
                        "Captured frames are too different — possible multiple people. Re-enroll with a single person.");
            }

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

            var minQuality = ConfigurationService.GetDouble("Biometrics:Enroll:Gate:MinAverageQuality", 0.10);
            var avgQuality = selected.Average(c => (double)c.QualityScore);
            if (avgQuality < minQuality)
                return Fail("LOW_QUALITY",
                    $"Average frame quality ({avgQuality:P0}) is below minimum. Ensure good lighting and hold steady.");

            return new GateResult { Passed = true };
        }

        private static GateResult Fail(string code, string message) =>
            new GateResult { Passed = false, ErrorCode = code, Message = message };
    }
}
