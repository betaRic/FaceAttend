using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace FaceAttend.Services.Biometrics
{
    /// <summary>
    /// In-memory face matcher with per-employee top-2 ambiguity guard and confidence tiering.
    ///
    /// MatchResult now includes:
    ///   Distance           — Euclidean distance to best match
    ///   SecondBestDistance — distance to nearest *different* employee
    ///   AmbiguityGap       — SecondBestDist − BestDist (larger = more distinct)
    ///   Tier               — HIGH / MEDIUM / LOW based on gap + absolute distance
    ///
    /// CONFIDENCE TIERS (all thresholds configurable via Web.config):
    ///   HIGH   — dist ≤ 0.42 AND gap ≥ 0.12  → record immediately
    ///   MEDIUM — dist ≤ 0.55 AND gap ≥ 0.08  → require a second confirming frame
    ///   LOW    — anything else in-tolerance    → reject (treat as unknown)
    ///
    /// These are conservative defaults. Once real scan distances are logged from
    /// production you can tune Biometrics:Match:* keys in Web.config.
    ///
    /// AMBIGUITY GUARD (15% rule):
    ///   if gap &lt; bestDist * 0.15 → reject regardless of absolute distance.
    /// </summary>
    public static class FastFaceMatcher
    {
        // ── In-memory state ───────────────────────────────────────────────────
        private static ConcurrentDictionary<string, List<double[]>> _employeeFaces;
        private static ConcurrentDictionary<string, EmployeeInfo>   _employeeInfo;
        private static DateTime _lastLoaded = DateTime.MinValue;
        private static readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();
        private static bool _isInitialized = false;

        // ── Tier thresholds (read at match time from config) ──────────────────
        // Public accessors for KioskController MEDIUM confirmation integrity check
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

        // ── DTOs ──────────────────────────────────────────────────────────────

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

            /// <summary>Euclidean distance to the best-matching employee vector.</summary>
            public double       Distance           { get; set; }

            /// <summary>0–1 confidence relative to the acceptance tolerance.</summary>
            public double       Confidence         { get; set; }

            /// <summary>
            /// Euclidean distance to the nearest *different* employee.
            /// double.PositiveInfinity when only one employee exists in the DB.
            /// </summary>
            public double       SecondBestDistance { get; set; } = double.PositiveInfinity;

            /// <summary>
            /// SecondBestDistance − Distance.
            /// Larger gap = more distinct, less ambiguous match.
            /// </summary>
            public double       AmbiguityGap       { get; set; }

            /// <summary>HIGH / MEDIUM / LOW tier.</summary>
            public MatchTier    Tier               { get; set; } = MatchTier.Low;

            /// <summary>Index of the matching photo within the employee's stored vector list.</summary>
            public string       MatchedPhotoIndex  { get; set; }

            /// <summary>True if rejected because two employees were within 15% of each other.</summary>
            public bool         WasAmbiguous       { get; set; }
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────

        public static void Initialize()
        {
            if (_isInitialized) return;
            _lock.EnterWriteLock();
            try   { ReloadFromDatabase(); _isInitialized = true; }
            finally { _lock.ExitWriteLock(); }
        }

        public static void ReloadFromDatabase()
        {
            var newFaces = new ConcurrentDictionary<string, List<double[]>>(StringComparer.OrdinalIgnoreCase);
            var newInfo  = new ConcurrentDictionary<string, EmployeeInfo>(StringComparer.OrdinalIgnoreCase);

            using (var db = new FaceAttendDBEntities())
            {
                var maxPer = ConfigurationService.GetInt("Biometrics:Enroll:MaxImages", 5);
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
            }

            _employeeFaces = newFaces;
            _employeeInfo  = newInfo;
            _lastLoaded    = DateTime.UtcNow;
        }

        // ── Core matching ─────────────────────────────────────────────────────

        /// <summary>
        /// Finds the best-matching employee with ambiguity guard and confidence tiering.
        /// Logs all match attempts to Trace for post-deployment threshold tuning.
        /// </summary>
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
                foreach (var kvp in _employeeFaces)
                {
                    var empId   = kvp.Key;
                    var vectors = kvp.Value;

                    // Per-employee minimum distance across all stored vectors
                    double empMin = double.MaxValue;
                    int    empIdx = -1;
                    for (int i = 0; i < vectors.Count; i++)
                    {
                        var d = DlibBiometrics.Distance(faceVector, vectors[i]);
                        if (d < empMin) { empMin = d; empIdx = i; }
                    }

                    if (empMin < bestDist)
                    {
                        secondEmpId  = bestEmpId;
                        secondDist   = bestDist;
                        bestEmpId    = empId;
                        bestDist     = empMin;
                        bestPhotoIdx = empIdx;
                    }
                    else if (empMin < secondDist && empId != bestEmpId)
                    {
                        secondEmpId = empId;
                        secondDist  = empMin;
                    }
                }
            }
            finally
            {
                _lock.ExitReadLock();
            }

            double gap = (secondDist < double.MaxValue)
                ? secondDist - bestDist
                : double.PositiveInfinity;

            // ── Distance logging — written to Windows trace / IIS log ─────────
            // These lines are the raw data you need to tune thresholds.
            // Format: [SCAN] result | emp | dist | 2nd | gap | tier
            // Query from IIS logs with:  grep "\[SCAN\]" your_trace.log
            string logLine;

            // ── Outside tolerance ─────────────────────────────────────────────
            if (bestEmpId == null || bestDist > tolerance)
            {
                logLine = string.Format(
                    "[SCAN] NO_MATCH | best={0} d={1:F3} | 2nd={2} d={3:F3} | gap={4} | tol={5:F3}",
                    bestEmpId ?? "none",
                    bestDist < double.MaxValue ? bestDist : -1,
                    secondEmpId ?? "none",
                    secondDist < double.MaxValue ? secondDist : -1,
                    gap == double.PositiveInfinity ? "inf" : gap.ToString("F3"),
                    tolerance);
                System.Diagnostics.Trace.TraceInformation(logLine);

                return new MatchResult { IsMatch = false };
            }

            // ── Ambiguity guard (15% rule) ────────────────────────────────────
            bool ambiguous = gap != double.PositiveInfinity && gap < (bestDist * 0.20);
            if (ambiguous)
            {
                logLine = string.Format(
                    "[SCAN] AMBIGUOUS | best={0} d={1:F3} | 2nd={2} d={3:F3} | gap={4:F3} | tol={5:F3}",
                    bestEmpId, bestDist, secondEmpId, secondDist, gap, tolerance);
                System.Diagnostics.Trace.TraceInformation(logLine);

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

            // LOW tier = in-tolerance but not distinct enough — reject
            if (tier == MatchTier.Low)
            {
                logLine = string.Format(
                    "[SCAN] LOW_TIER | best={0} d={1:F3} | 2nd={2} d={3:F3} | gap={4:F3} | tol={5:F3}",
                    bestEmpId, bestDist,
                    secondEmpId ?? "none",
                    secondDist < double.MaxValue ? secondDist : -1,
                    gap == double.PositiveInfinity ? "inf" : gap.ToString("F3"),
                    tolerance);
                System.Diagnostics.Trace.TraceInformation(logLine);

                return new MatchResult { IsMatch = false };
            }

            var confidence = tolerance > 0
                ? Math.Max(0, Math.Min(1, 1.0 - (bestDist / tolerance)))
                : 0.0;

            logLine = string.Format(
                "[SCAN] {0} | emp={1} d={2:F3} | 2nd={3} d={4:F3} | gap={5:F3} | conf={6:F2} | tol={7:F3}",
                tier.ToString().ToUpper(),
                bestEmpId, bestDist,
                secondEmpId ?? "none",
                secondDist < double.MaxValue ? secondDist : -1,
                gap == double.PositiveInfinity ? "inf" : gap.ToString("F3"),
                confidence,
                tolerance);
            System.Diagnostics.Trace.TraceInformation(logLine);

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

        private static MatchTier ClassifyTier(double dist, double gap)
        {
            if (dist <= HighDistThreshold && gap >= HighGapThreshold) return MatchTier.High;
            if (dist <= MedDistThreshold  && gap >= MedGapThreshold)  return MatchTier.Medium;
            return MatchTier.Low;
        }

        // ── Cache management ──────────────────────────────────────────────────

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
            var maxPer = ConfigurationService.GetInt("Biometrics:Enroll:MaxImages", 5);
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

        // ── Properties ────────────────────────────────────────────────────────

        public static bool     IsInitialized => _isInitialized;
        public static DateTime LastLoaded    => _lastLoaded;

        public static object GetStats() => new
        {
            IsInitialized    = _isInitialized,
            LastLoaded       = _lastLoaded,
            EmployeeCount    = _employeeFaces?.Count ?? 0,
            TotalFaceVectors = _employeeFaces?.Values.Sum(v => v.Count) ?? 0,
            MemoryEstimateMB = (_employeeFaces?.Count ?? 0) * 128 * 8 / 1024.0 / 1024.0
        };
    }
}
