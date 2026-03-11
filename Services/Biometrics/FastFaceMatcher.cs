using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace FaceAttend.Services.Biometrics
{
    /// <summary>
    /// ULTRA-FAST face matching with pre-loaded employee faces in RAM.
    /// 
    /// GOAL: Sub-50ms matching time for instant recognition.
    /// 
    /// Traditional approach: DB query every scan (~100-200ms)
    /// FastFaceMatcher: RAM lookup (~5-20ms) = 10x faster!
    /// </summary>
    public static class FastFaceMatcher
    {
        // EmployeeId -> Face Vectors (multiple photos per employee)
        private static ConcurrentDictionary<string, List<double[]>> _employeeFaces;
        
        // EmployeeId -> Employee Info (name, dept, etc)
        private static ConcurrentDictionary<string, EmployeeInfo> _employeeInfo;
        
        // Last update timestamp for cache invalidation
        private static DateTime _lastLoaded = DateTime.MinValue;
        private static readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();
        private static bool _isInitialized = false;

        public class EmployeeInfo
        {
            public int Id { get; set; }
            public string EmployeeId { get; set; }
            public string FirstName { get; set; }
            public string LastName { get; set; }
            public string MiddleName { get; set; }
            public string Department { get; set; }
            public bool IsActive { get; set; }
            
            public string DisplayName => string.IsNullOrWhiteSpace(LastName) ? EmployeeId : 
                $"{LastName}, {FirstName}" + (string.IsNullOrWhiteSpace(MiddleName) ? "" : $" {MiddleName}");
        }

        public class MatchResult
        {
            public bool IsMatch { get; set; }
            public EmployeeInfo Employee { get; set; }
            public double Distance { get; set; }
            public double Confidence { get; set; } // 0.0 to 1.0
            public string MatchedPhotoIndex { get; set; }
        }

        /// <summary>
        /// Initialize the fast matcher by loading all active employee faces into RAM.
        /// Call this at Application_Start.
        /// </summary>
        public static void Initialize()
        {
            if (_isInitialized) return;
            
            _lock.EnterWriteLock();
            try
            {
                ReloadFromDatabase();
                _isInitialized = true;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Decode face vectors from Base64 string.
        /// </summary>
        private static double[] DecodeVector(string base64)
        {
            if (string.IsNullOrEmpty(base64)) return null;
            try
            {
                var bytes = Convert.FromBase64String(base64);
                return DlibBiometrics.DecodeFromBytes(bytes);
            }
            catch { return null; }
        }

        /// <summary>
        /// Decode multiple face vectors from JSON array.
        /// </summary>
        private static List<double[]> DecodeVectorsFromJson(string json)
        {
            var result = new List<double[]>();
            if (string.IsNullOrEmpty(json)) return result;
            
            try
            {
                var base64List = Newtonsoft.Json.JsonConvert.DeserializeObject<List<string>>(json);
                if (base64List != null)
                {
                    foreach (var b64 in base64List)
                    {
                        var vec = DecodeVector(b64);
                        if (vec != null) result.Add(vec);
                    }
                }
            }
            catch { }
            
            return result;
        }

        /// <summary>
        /// Reload all faces from database. Call when new employee added or faces updated.
        /// </summary>
        public static void ReloadFromDatabase()
        {
            var newEmployeeFaces = new ConcurrentDictionary<string, List<double[]>>(StringComparer.OrdinalIgnoreCase);
            var newEmployeeInfo = new ConcurrentDictionary<string, EmployeeInfo>(StringComparer.OrdinalIgnoreCase);

            using (var db = new FaceAttendDBEntities())
            {
                // Load all active employees with their face encodings
                var employees = db.Employees
                    .Where(e => e.IsActive)
                    .Select(e => new
                    {
                        e.Id,
                        e.EmployeeId,
                        e.FirstName,
                        e.LastName,
                        e.MiddleName,
                        e.Department,
                        e.IsActive,
                        e.FaceEncodingBase64,
                        e.FaceEncodingsJson
                    })
                    .ToList();

                foreach (var emp in employees)
                {
                    var vectors = new List<double[]>();
                    
                    // Decode primary face encoding
                    if (!string.IsNullOrEmpty(emp.FaceEncodingBase64))
                    {
                        var vec = DecodeVector(emp.FaceEncodingBase64);
                        if (vec != null) vectors.Add(vec);
                    }
                    
                    // Decode additional face encodings from JSON
                    var additionalVectors = DecodeVectorsFromJson(emp.FaceEncodingsJson);
                    vectors.AddRange(additionalVectors);

                    if (vectors.Count > 0)
                    {
                        newEmployeeFaces[emp.EmployeeId] = vectors;
                        newEmployeeInfo[emp.EmployeeId] = new EmployeeInfo
                        {
                            Id = emp.Id,
                            EmployeeId = emp.EmployeeId,
                            FirstName = emp.FirstName,
                            LastName = emp.LastName,
                            MiddleName = emp.MiddleName,
                            Department = emp.Department,
                            IsActive = emp.IsActive
                        };
                    }
                }
            }

            _employeeFaces = newEmployeeFaces;
            _employeeInfo = newEmployeeInfo;
            _lastLoaded = DateTime.UtcNow;
        }

        /// <summary>
        /// Find best matching employee in under 50ms using RAM cache.
        /// </summary>
        public static MatchResult FindBestMatch(double[] faceVector, double tolerance = 0.60)
        {
            if (!_isInitialized) Initialize();
            if (faceVector == null || faceVector.Length != 128) 
                return new MatchResult { IsMatch = false };

            string bestEmployeeId = null;
            double bestDistance = double.MaxValue;
            int bestPhotoIndex = -1;

            _lock.EnterReadLock();
            try
            {
                // Sequential search (faster for small datasets, less overhead than Parallel)
                foreach (var kvp in _employeeFaces)
                {
                    string empId = kvp.Key;
                    var vectors = kvp.Value;
                    
                    for (int i = 0; i < vectors.Count; i++)
                    {
                        double dist = DlibBiometrics.Distance(faceVector, vectors[i]);
                        if (dist < tolerance && dist < bestDistance)
                        {
                            bestDistance = dist;
                            bestEmployeeId = empId;
                            bestPhotoIndex = i;
                        }
                    }
                }
            }
            finally
            {
                _lock.ExitReadLock();
            }

            if (bestEmployeeId != null && bestDistance <= tolerance)
            {
                var confidence = tolerance > 0 ? Math.Max(0, Math.Min(1, 1.0 - (bestDistance / tolerance))) : 0;
                
                EmployeeInfo empInfo = null;
                _employeeInfo.TryGetValue(bestEmployeeId, out empInfo);
                
                return new MatchResult
                {
                    IsMatch = true,
                    Employee = empInfo,
                    Distance = bestDistance,
                    Confidence = confidence,
                    MatchedPhotoIndex = bestPhotoIndex.ToString()
                };
            }

            return new MatchResult { IsMatch = false };
        }

        /// <summary>
        /// Get stats about the cache.
        /// </summary>
        public static object GetStats()
        {
            return new
            {
                IsInitialized = _isInitialized,
                LastLoaded = _lastLoaded,
                EmployeeCount = _employeeFaces?.Count ?? 0,
                TotalFaceVectors = _employeeFaces?.Values.Sum(v => v.Count) ?? 0,
                MemoryEstimateMB = (_employeeFaces?.Count ?? 0) * 128 * 8 / 1024.0 / 1024.0
            };
        }

        /// <summary>
        /// Add or update an employee in the cache (call after enrollment).
        /// </summary>
        public static void UpdateEmployee(string employeeId)
        {
            _lock.EnterWriteLock();
            try
            {
                using (var db = new FaceAttendDBEntities())
                {
                    var emp = db.Employees
                        .Where(e => e.EmployeeId == employeeId)
                        .Select(e => new
                        {
                            e.Id,
                            e.EmployeeId,
                            e.FirstName,
                            e.LastName,
                            e.MiddleName,
                            e.Department,
                            e.IsActive,
                            e.FaceEncodingBase64,
                            e.FaceEncodingsJson
                        })
                        .FirstOrDefault();

                    if (emp == null || !emp.IsActive)
                    {
                        // Remove if exists
                        _employeeFaces.TryRemove(employeeId, out _);
                        _employeeInfo.TryRemove(employeeId, out _);
                        return;
                    }

                    var vectors = new List<double[]>();
                    
                    if (!string.IsNullOrEmpty(emp.FaceEncodingBase64))
                    {
                        var vec = DecodeVector(emp.FaceEncodingBase64);
                        if (vec != null) vectors.Add(vec);
                    }
                    
                    var additionalVectors = DecodeVectorsFromJson(emp.FaceEncodingsJson);
                    vectors.AddRange(additionalVectors);

                    if (vectors.Count > 0)
                    {
                        _employeeFaces[emp.EmployeeId] = vectors;
                        _employeeInfo[emp.EmployeeId] = new EmployeeInfo
                        {
                            Id = emp.Id,
                            EmployeeId = emp.EmployeeId,
                            FirstName = emp.FirstName,
                            LastName = emp.LastName,
                            MiddleName = emp.MiddleName,
                            Department = emp.Department,
                            IsActive = emp.IsActive
                        };
                    }
                }
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }
    }
}
