using System.Collections.Generic;
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

            return new GateResult { Passed = true };
        }

        private static GateResult Fail(string code, string message) =>
            new GateResult { Passed = false, ErrorCode = code, Message = message };
    }
}
