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

        // BallTree index for O(log n) search - enabled when >= 50 employees
        private static BallTreeIndex _ballTree;
        private static int _ballTreeThreshold = 50;

        public static double HighDistThresholdPublic => HighDistThreshold;
        public static double MedDistThresholdPublic  => MedDistThreshold;

        private static double HighDistThreshold
            => ConfigurationService.GetDouble("Biometrics:Match:HighDistThreshold", 0.40);
        private static double HighGapThreshold
            => ConfigurationService.GetDouble("Biometrics:Match:HighGapThreshold", 0.15);
        private static double MedDistThreshold
            => ConfigurationService.GetDouble("Biometrics:Match:MedDistThreshold", 0.55);
        private static double MedGapThreshold
            => ConfigurationService.GetDouble("Biometrics:Match:MedGapThreshold", 0.15);

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

                // Build BallTree for fast O(log n) search when >= threshold employees
                _ballTree = null;
                var totalVectors = newFaces.Values.Sum(v => v.Count);
                if (totalVectors >= _ballTreeThreshold)
                {
                    try
                    {
                        var leafSize = ConfigurationService.GetInt("Biometrics:BallTreeLeafSize", 16);
                        var entries = new List<BallTreeIndex.Entry>();
                        foreach (var kvp in newFaces)
                        {
                            foreach (var vec in kvp.Value)
                            {
                                entries.Add(new BallTreeIndex.Entry
                                {
                                    Id = kvp.Key,
                                    Vec = vec
                                });
                            }
                        }
                        _ballTree = new BallTreeIndex(entries, leafSize);
                        System.Diagnostics.Trace.TraceInformation(
                            "[FastFaceMatcher] BallTree built with {0} vectors from {1} employees",
                            entries.Count, newFaces.Count);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Trace.TraceWarning(
                            "[FastFaceMatcher] BallTree build failed: {0}", ex.Message);
                    }
                }
            }

            _employeeFaces = newFaces;
            _employeeInfo  = newInfo;
            _lastLoaded    = DateTime.UtcNow;
        }

        public static MatchResult FindBestMatch(double[] faceVector, double tolerance)
        {
            if (!_isInitialized) Initialize();

            if (faceVector == null || faceVector.Length != 128)
                return new MatchResult { IsMatch = false };

            string bestEmpId    = null;
            double bestDist     = double.MaxValue;
            int    bestPhotoIdx = -1;
            string secondEmpId  = null;
            double secondDist   = double.MaxValue;

            _lock.EnterReadLock();
            try
            {
                // Use BallTree for O(log n) search when available
                var tree = _ballTree;
                if (tree != null)
                {
                    // BallTree gives us the nearest neighbor
                    var ballResult = tree.FindNearest(faceVector, tolerance, out double ballDist);
                    if (ballResult != null)
                    {
                        bestEmpId = ballResult;
                        bestDist = ballDist;
                    }

                    // If no match in BallTree tolerance, try full scan as fallback
                    // This handles cases where the closest is within tolerance but BallTree returned null
                    if (bestEmpId == null)
                    {
                        // Fallback to linear scan for candidates within extended tolerance
                        bestEmpId = FindNearestLinearFallback(faceVector, tolerance * 1.2, out bestDist, out bestPhotoIdx);
                    }

                    // Need to compute second-best for ambiguity check
                    if (bestEmpId != null)
                    {
                        // Get best employee's vectors to find second-best
                        if (_employeeFaces.TryGetValue(bestEmpId, out var bestVectors))
                        {
                            var allDists = new List<(double d, int idx)>();
                            for (int i = 0; i < bestVectors.Count; i++)
                            {
                                var d = DlibBiometrics.Distance(faceVector, bestVectors[i]);
                                allDists.Add((d, i));
                            }
                            allDists.Sort((a, b) => a.d.CompareTo(b.d));

                            // Second-best is second closest vector OR second closest employee
                            if (allDists.Count > 1)
                            {
                                secondDist = allDists[1].d;
                            }
                            else
                            {
                                // Need to check other employees for true second-best
                                secondDist = FindSecondBestDistance(faceVector, bestEmpId, bestDist);
                            }
                        }
                    }
                }
                else
                {
                    // Fallback to linear scan (original O(n) implementation)
                    foreach (var kvp in _employeeFaces)
                    {
                        var empId   = kvp.Key;
                        var vectors = kvp.Value;

                        var allDists = new List<(double d, int idx)>(vectors.Count);
                        for (int i = 0; i < vectors.Count; i++)
                            allDists.Add((DlibBiometrics.Distance(faceVector, vectors[i]), i));
                        allDists.Sort((a, b) => a.d.CompareTo(b.d));

                        int    empIdx   = allDists[0].idx;
                        int    k        = Math.Min(3, allDists.Count);
                        double empScore = 0;
                        for (int i = 0; i < k; i++) empScore += allDists[i].d;
                        empScore /= k;

                        if (empScore < bestDist)
                        {
                            secondEmpId  = bestEmpId;
                            secondDist   = bestDist;
                            bestEmpId    = empId;
                            bestDist     = empScore;
                            bestPhotoIdx = empIdx;
                        }
                        else if (empScore < secondDist && empId != bestEmpId)
                        {
                            secondEmpId = empId;
                            secondDist  = empScore;
                        }
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

            bool ambiguous = gap != double.PositiveInfinity
                && gap < (bestDist * 0.25);
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
                AmbiguityGap       = gap,
                Tier               = tier,
                MatchedPhotoIndex  = bestPhotoIdx.ToString(),
                WasAmbiguous       = false
            };
        }

        private static string FindNearestLinearFallback(double[] vec, double tolerance, out double bestDist, out int bestPhotoIdx)
        {
            bestDist = double.MaxValue;
            bestPhotoIdx = -1;
            string bestEmpId = null;

            foreach (var kvp in _employeeFaces)
            {
                var vectors = kvp.Value;
                var allDists = new List<(double d, int idx)>(vectors.Count);
                for (int i = 0; i < vectors.Count; i++)
                    allDists.Add((DlibBiometrics.Distance(vec, vectors[i]), i));
                allDists.Sort((a, b) => a.d.CompareTo(b.d));

                if (allDists[0].d < bestDist)
                {
                    bestDist = allDists[0].d;
                    bestPhotoIdx = allDists[0].idx;
                    bestEmpId = kvp.Key;
                }
            }

            return bestDist <= tolerance ? bestEmpId : null;
        }

        private static double FindSecondBestDistance(double[] vec, string excludeEmpId, double bestDist)
        {
            double secondBest = double.MaxValue;

            foreach (var kvp in _employeeFaces)
            {
                if (kvp.Key == excludeEmpId) continue;

                var vectors = kvp.Value;
                double minDist = double.MaxValue;
                foreach (var v in vectors)
                {
                    var d = DlibBiometrics.Distance(vec, v);
                    if (d < minDist) minDist = d;
                }

                if (minDist < secondBest) secondBest = minDist;
            }

            return secondBest;
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
            MemoryEstimateMB = (_employeeFaces?.Count ?? 0) * 128 * 8 / 1024.0 / 1024.0
        };
    }
}
