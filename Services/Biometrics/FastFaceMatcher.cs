using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace FaceAttend.Services.Biometrics
{
    public static class FastFaceMatcher
    {
        private static ConcurrentDictionary<string, List<double[]>> _employeeFaces;
        private static ConcurrentDictionary<string, EmployeeInfo>   _employeeInfo;
        private static DateTime _lastLoaded = DateTime.MinValue;
        private static readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();
        private static bool _isInitialized = false;

        public static double HighDistThresholdPublic => HighDistThreshold;
        public static double MedDistThresholdPublic  => MedDistThreshold;

        private static double HighDistThreshold
            => BiometricPolicy.Current.HighDistanceThreshold;
        private static double MedDistThreshold
            => BiometricPolicy.Current.MediumDistanceThreshold;

        public class EmployeeInfo
        {
            public int    Id         { get; set; }
            public string EmployeeId { get; set; }
            public string FirstName  { get; set; }
            public string LastName   { get; set; }
            public string MiddleName { get; set; }
            public string Department { get; set; }

            public string DisplayName => string.IsNullOrWhiteSpace(LastName)
                ? EmployeeId
                : $"{LastName}, {FirstName}" +
                  (string.IsNullOrWhiteSpace(MiddleName) ? "" : $" {MiddleName}");
        }

        public enum MatchTier { High, Medium, Low }

        public class MatchResult
        {
            public bool         IsMatch            { get; set; }
            public EmployeeInfo Employee           { get; set; }
            public double       Distance           { get; set; }
            public double       Confidence         { get; set; }
            public double       SecondBestDistance { get; set; } = double.PositiveInfinity;
            public string       SecondBestEmployeeId { get; set; }
            public double       BestRawDistance    { get; set; } = double.PositiveInfinity;
            public double       AmbiguityGap       { get; set; }
            public MatchTier    Tier               { get; set; } = MatchTier.Low;
            public string       MatchedPhotoIndex  { get; set; }
            public bool         WasAmbiguous       { get; set; }
        }

        public static void Initialize()
        {
            if (_isInitialized) return;
            _lock.EnterWriteLock();
            try   { ReloadFromDatabase(); _isInitialized = true; }
            finally { _lock.ExitWriteLock(); }
        }

        /// <summary>
        /// Clears the in-memory cache and reloads all face vectors from the database.
        /// Call this after any enrollment or deletion that changes stored face data.
        /// </summary>
        public static void InvalidateAndReload() => ReloadFromDatabase();

        public static void ReloadFromDatabase()
        {
            var newFaces = new ConcurrentDictionary<string, List<double[]>>(StringComparer.OrdinalIgnoreCase);
            var newInfo  = new ConcurrentDictionary<string, EmployeeInfo>(StringComparer.OrdinalIgnoreCase);

            using (var db = new FaceAttendDBEntities())
            {
                var maxPer = ConfigurationService.GetInt("Biometrics:Enroll:MaxStoredVectors", 25);
                var emps   = FaceEncodingHelper.LoadAllEmployeeFaces(db, maxPer);

                foreach (var emp in emps)
                {
                    if (emp.FaceVectors.Count > 0)
                    {
                        newFaces[emp.EmployeeId] = emp.FaceVectors;
                        newInfo[emp.EmployeeId]  = new EmployeeInfo
                        {
                            Id         = emp.Id,
                            EmployeeId = emp.EmployeeId,
                            FirstName  = emp.FirstName,
                            LastName   = emp.LastName,
                            MiddleName = emp.MiddleName,
                            Department = emp.Department
                        };
                    }
                }

                System.Diagnostics.Trace.TraceInformation(
                    "[FastFaceMatcher] Loaded {0} vectors from {1} employees for exact matching",
                    newFaces.Values.Sum(v => v.Count), newFaces.Count);
            }

            _employeeFaces = newFaces;
            _employeeInfo  = newInfo;
            _lastLoaded    = DateTime.UtcNow;
        }

        public static MatchResult FindBestMatch(double[] faceVector, double tolerance)
        {
            if (!_isInitialized) Initialize();

            if (!FaceVectorCodec.IsValidVector(faceVector))
                return new MatchResult { IsMatch = false };

            string bestEmpId    = null;
            double bestDist     = double.MaxValue;
            double bestRawDist  = double.MaxValue;
            int    bestPhotoIdx = -1;
            string secondEmpId  = null;
            double secondDist   = double.MaxValue;

            _lock.EnterReadLock();
            try
            {
                // For attendance, correctness beats the tiny speed gain from nearest-vector
                // indexing. At this scale, exact employee-level scoring is cheap and avoids
                // accepting a single vector while another employee is nearly tied.
                foreach (var kvp in _employeeFaces)
                {
                    var empId   = kvp.Key;
                    var vectors = kvp.Value;

                    int empIdx;
                    double rawDist;
                    var empScore = ScoreEmployee(faceVector, vectors, out empIdx, out rawDist);
                    if (double.IsPositiveInfinity(empScore))
                        continue;

                    if (empScore < bestDist)
                    {
                        secondEmpId = bestEmpId;
                        secondDist  = bestDist;
                        bestEmpId   = empId;
                        bestDist    = empScore;
                        bestRawDist = rawDist;
                        bestPhotoIdx = empIdx;
                    }
                    else if (empScore < secondDist && empId != bestEmpId)
                    {
                        secondEmpId = empId;
                        secondDist  = empScore;
                    }
                }
            }
            finally
            {
                _lock.ExitReadLock();
            }

            double gap = secondDist < double.MaxValue
                ? secondDist - bestDist
                : double.PositiveInfinity;

            if (bestEmpId == null || bestDist > tolerance)
            {
                System.Diagnostics.Trace.TraceInformation(
                    "[SCAN] NO_MATCH | best={0} d={1:F3} | 2nd={2} d={3:F3} | gap={4} | tol={5:F3}",
                    bestEmpId ?? "none",
                    bestDist < double.MaxValue ? bestDist : -1,
                    secondEmpId ?? "none",
                    secondDist < double.MaxValue ? secondDist : -1,
                    gap == double.PositiveInfinity ? "inf" : gap.ToString("F3"),
                    tolerance);
                return new MatchResult { IsMatch = false };
            }

            var ambiguityRelativeGap = BiometricPolicy.Current.AmbiguityRelativeGap;
            bool ambiguous = gap != double.PositiveInfinity
                && gap < (bestDist * ambiguityRelativeGap);
            if (ambiguous)
            {
                System.Diagnostics.Trace.TraceInformation(
                    "[SCAN] AMBIGUOUS | best={0} d={1:F3} | 2nd={2} d={3:F3} | gap={4:F3} | tol={5:F3}",
                    bestEmpId, bestDist, secondEmpId ?? "none", secondDist, gap, tolerance);
                return new MatchResult
                {
                    IsMatch            = false,
                    WasAmbiguous       = true,
                    Distance           = bestDist,
                    SecondBestDistance = secondDist,
                    SecondBestEmployeeId = secondEmpId,
                    BestRawDistance    = bestRawDist,
                    AmbiguityGap       = gap
                };
            }

            var tier = ClassifyTier(bestDist, gap);

            if (tier == MatchTier.Low)
            {
                System.Diagnostics.Trace.TraceInformation(
                    "[SCAN] LOW_TIER | best={0} d={1:F3} | 2nd={2} d={3:F3} | gap={4:F3} | tol={5:F3}",
                    bestEmpId, bestDist,
                    secondEmpId ?? "none",
                    secondDist < double.MaxValue ? secondDist : -1,
                    gap == double.PositiveInfinity ? "inf" : gap.ToString("F3"),
                    tolerance);
                return new MatchResult { IsMatch = false };
            }

            var confidence = tolerance > 0
                ? Math.Max(0, Math.Min(1, 1.0 - (bestDist / tolerance)))
                : 0.0;

            System.Diagnostics.Trace.TraceInformation(
                "[SCAN] {0} | emp={1} d={2:F3} | 2nd={3} d={4:F3} | gap={5:F3} | conf={6:F2} | tol={7:F3}",
                tier.ToString().ToUpper(),
                bestEmpId, bestDist,
                secondEmpId ?? "none",
                secondDist < double.MaxValue ? secondDist : -1,
                gap == double.PositiveInfinity ? "inf" : gap.ToString("F3"),
                confidence,
                tolerance);

            EmployeeInfo info = null;
            _employeeInfo.TryGetValue(bestEmpId, out info);

            return new MatchResult
            {
                IsMatch            = true,
                Employee           = info,
                Distance           = bestDist,
                Confidence         = confidence,
                SecondBestDistance = secondDist,
                SecondBestEmployeeId = secondEmpId,
                BestRawDistance    = bestRawDist,
                AmbiguityGap       = gap,
                Tier               = tier,
                MatchedPhotoIndex  = bestPhotoIdx.ToString(),
                WasAmbiguous       = false
            };
        }

        private static double ScoreEmployee(double[] vec, List<double[]> vectors, out int bestPhotoIdx, out double rawBestDist)
        {
            bestPhotoIdx = -1;
            rawBestDist = double.PositiveInfinity;

            if (vectors == null || vectors.Count == 0)
                return double.PositiveInfinity;

            var allDists = new List<(double d, int idx)>(vectors.Count);
            for (int i = 0; i < vectors.Count; i++)
                if (FaceVectorCodec.IsValidVector(vectors[i]))
                    allDists.Add((FaceVectorCodec.Distance(vec, vectors[i]), i));

            if (allDists.Count == 0)
                return double.PositiveInfinity;

            allDists.Sort((a, b) => a.d.CompareTo(b.d));
            bestPhotoIdx = allDists[0].idx;
            rawBestDist = allDists[0].d;

            var k = Math.Min(3, allDists.Count);
            double score = 0;
            for (int i = 0; i < k; i++)
                score += allDists[i].d;

            return score / k;
        }

        private static MatchTier ClassifyTier(double dist, double gap)
        {
            // NSD (Normalized Score Difference): gap relative to best distance.
            // Scales correctly with database size — absolute gap thresholds degrade
            // as more employees with similar faces are enrolled.
            double nsd = gap / Math.Max(dist, 0.01);
            if (dist <= HighDistThreshold && nsd >= 0.30) return MatchTier.High;
            if (dist <= MedDistThreshold  && nsd >= 0.15) return MatchTier.Medium;
            return MatchTier.Low;
        }

        public static void UpdateEmployee(string employeeId, FaceAttendDBEntities db = null)
        {
            _lock.EnterWriteLock();
            try
            {
                if (db == null)
                {
                    using (var ctx = new FaceAttendDBEntities())
                        UpdateEmployeeInternal(ctx, employeeId);
                }
                else
                {
                    UpdateEmployeeInternal(db, employeeId);
                }
            }
            finally { _lock.ExitWriteLock(); }
        }

        private static void UpdateEmployeeInternal(FaceAttendDBEntities db, string employeeId)
        {
            var maxPer = ConfigurationService.GetInt("Biometrics:Enroll:MaxStoredVectors", 25);
            var emp    = FaceEncodingHelper.LoadEmployeeById(db, employeeId, maxPer);

            if (emp == null)
            {
                _employeeFaces.TryRemove(employeeId, out _);
                _employeeInfo.TryRemove(employeeId, out _);
                return;
            }

            _employeeFaces[emp.EmployeeId] = emp.FaceVectors;
            _employeeInfo[emp.EmployeeId]  = new EmployeeInfo
            {
                Id         = emp.Id,
                EmployeeId = emp.EmployeeId,
                FirstName  = emp.FirstName,
                LastName   = emp.LastName,
                MiddleName = emp.MiddleName,
                Department = emp.Department
            };
        }

        public static bool     IsInitialized => _isInitialized;
        public static DateTime LastLoaded    => _lastLoaded;

        public static MatcherStats GetStats() => new MatcherStats
        {
            IsInitialized    = _isInitialized,
            LastLoaded       = _lastLoaded,
            EmployeeCount    = _employeeFaces?.Count ?? 0,
            TotalFaceVectors = _employeeFaces?.Values.Sum(v => v.Count) ?? 0,
            MemoryEstimateMB = (_employeeFaces?.Values.Sum(v => v.Count) ?? 0) *
                BiometricPolicy.Current.EmbeddingDim * 8 / 1024.0 / 1024.0
        };
    }
}
