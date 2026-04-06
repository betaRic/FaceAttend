using System;

namespace FaceAttend.Services.Biometrics
{
    public class MatcherStats
    {
        public bool     IsInitialized    { get; set; }
        public DateTime LastLoaded       { get; set; }
        public int      EmployeeCount    { get; set; }
        public int      TotalFaceVectors { get; set; }
        public double   MemoryEstimateMB { get; set; }

        public override string ToString()
        {
            return string.Format(
                "{0} employees, {1} vectors, {2:F2} MB, initialized={3}",
                EmployeeCount,
                TotalFaceVectors,
                MemoryEstimateMB,
                IsInitialized);
        }
    }
}
