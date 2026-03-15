using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace FaceAttend.Services.Biometrics
{
    /// <summary>
    /// Shared helper for decoding face encodings from various storage formats.
    /// Used by both EmployeeFaceIndex and FastFaceMatcher to ensure consistent decoding.
    /// </summary>
    public static class FaceEncodingHelper
    {
        /// <summary>
        /// Decodes a single face vector from stored Base64 (handles encryption).
        /// </summary>
        public static double[] DecodeVector(string base64)
        {
            if (string.IsNullOrWhiteSpace(base64)) return null;

            try
            {
                byte[] bytes;
                if (!BiometricCrypto.TryGetBytesFromStoredBase64(base64, out bytes))
                    return null;

                return DlibBiometrics.DecodeFromBytes(bytes);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning($"[FaceEncodingHelper] Failed to decode vector: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Decodes multiple face vectors from JSON array of Base64 strings.
        /// </summary>
        public static List<double[]> DecodeVectorsFromJson(string json, int maxVectors = int.MaxValue)
        {
            var result = new List<double[]>();
            if (string.IsNullOrWhiteSpace(json)) return result;

            try
            {
                // Decrypt if needed
                string plainJson;
                if (!BiometricCrypto.TryUnprotectString(json, out plainJson))
                    plainJson = json;

                var base64List = JsonConvert.DeserializeObject<List<string>>(plainJson);
                if (base64List == null) return result;

                int count = 0;
                foreach (var b64 in base64List)
                {
                    if (count >= maxVectors) break;
                    if (string.IsNullOrWhiteSpace(b64)) continue;

                    var vec = DecodeVector(b64);
                    if (vec != null)
                    {
                        result.Add(vec);
                        count++;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning($"[FaceEncodingHelper] Failed to decode JSON vectors: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Loads all face vectors for an employee from both single and JSON multi-encoding fields.
        /// </summary>
        public static List<double[]> LoadEmployeeVectors(
            string faceEncodingBase64,
            string faceEncodingsJson,
            int maxPerEmployee = 5)
        {
            var vectors = new List<double[]>();

            // Decode JSON multi-encodings first (newer format)
            if (!string.IsNullOrWhiteSpace(faceEncodingsJson))
            {
                var jsonVectors = DecodeVectorsFromJson(faceEncodingsJson, maxPerEmployee);
                vectors.AddRange(jsonVectors);
            }

            // Fallback to legacy single encoding
            if (vectors.Count == 0 && !string.IsNullOrWhiteSpace(faceEncodingBase64))
            {
                var vec = DecodeVector(faceEncodingBase64);
                if (vec != null)
                    vectors.Add(vec);
            }

            return vectors;
        }

        /// <summary>
        /// Loads all active employees with their face vectors from the database.
        /// This is the shared loading logic used by both FastFaceMatcher and EmployeeFaceIndex.
        /// </summary>
        public static List<EmployeeFaceData> LoadAllEmployeeFaces(FaceAttendDBEntities db, int maxPerEmployee = 5)
        {
            var result = new List<EmployeeFaceData>();

            try
            {
                // Try raw SQL first for performance (supports multi-encoding)
                var rows = db.Database.SqlQuery<EmployeeEncodingRow>(
                    @"SELECT Id, EmployeeId, FirstName, LastName, MiddleName, Department,
                             FaceEncodingBase64, FaceEncodingsJson
                      FROM Employees
                      WHERE Status = 'ACTIVE'
                        AND (FaceEncodingBase64 IS NOT NULL OR FaceEncodingsJson IS NOT NULL)"
                ).ToList();

                foreach (var row in rows)
                {
                    var vectors = LoadEmployeeVectors(
                        row.FaceEncodingBase64,
                        row.FaceEncodingsJson,
                        maxPerEmployee);

                    if (vectors.Count > 0)
                    {
                        result.Add(new EmployeeFaceData
                        {
                            Id = row.Id,
                            EmployeeId = row.EmployeeId,
                            FirstName = row.FirstName,
                            LastName = row.LastName,
                            MiddleName = row.MiddleName,
                            Department = row.Department,

                            FaceVectors = vectors
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError($"[FaceEncodingHelper] SQL load failed, falling back to EF: {ex.Message}");

                // Fallback to EF
                result = LoadViaEF(db, maxPerEmployee);
            }

            return result;
        }

        /// <summary>
        /// Fallback loading via Entity Framework.
        /// </summary>
        private static List<EmployeeFaceData> LoadViaEF(FaceAttendDBEntities db, int maxPerEmployee)
        {
            var result = new List<EmployeeFaceData>();

            var employees = db.Employees
                .Where(e => e.Status == "ACTIVE" && 
                           (e.FaceEncodingBase64 != null || e.FaceEncodingsJson != null))
                .Select(e => new
                {
                    e.Id,
                    e.EmployeeId,
                    e.FirstName,
                    e.LastName,
                    e.MiddleName,
                    e.Department,

                    e.FaceEncodingBase64,
                    e.FaceEncodingsJson
                })
                .ToList();

            foreach (var emp in employees)
            {
                var vectors = LoadEmployeeVectors(
                    emp.FaceEncodingBase64,
                    emp.FaceEncodingsJson,
                    maxPerEmployee);

                if (vectors.Count > 0)
                {
                    result.Add(new EmployeeFaceData
                    {
                        Id = emp.Id,
                        EmployeeId = emp.EmployeeId,
                        FirstName = emp.FirstName,
                        LastName = emp.LastName,
                        MiddleName = emp.MiddleName,
                        Department = emp.Department,
                        FaceVectors = vectors
                    });
                }
            }

            return result;
        }

        /// <summary>
        /// Loads a single employee's face data by EmployeeId.
        /// </summary>
        public static EmployeeFaceData LoadEmployeeById(FaceAttendDBEntities db, string employeeId, int maxPerEmployee = 5)
        {
            var emp = db.Employees
                .Where(e => e.EmployeeId == employeeId && e.Status == "ACTIVE")
                .Select(e => new
                {
                    e.Id,
                    e.EmployeeId,
                    e.FirstName,
                    e.LastName,
                    e.MiddleName,
                    e.Department,

                    e.FaceEncodingBase64,
                    e.FaceEncodingsJson
                })
                .FirstOrDefault();

            if (emp == null)
                return null;

            var vectors = LoadEmployeeVectors(
                emp.FaceEncodingBase64,
                emp.FaceEncodingsJson,
                maxPerEmployee);

            if (vectors.Count == 0)
                return null;

            return new EmployeeFaceData
            {
                Id = emp.Id,
                EmployeeId = emp.EmployeeId,
                FirstName = emp.FirstName,
                LastName = emp.LastName,
                MiddleName = emp.MiddleName,
                Department = emp.Department,
                FaceVectors = vectors
            };
        }

        #region DTOs

        /// <summary>
        /// Employee face data transfer object.
        /// </summary>
        public class EmployeeFaceData
        {
            public int Id { get; set; }
            public string EmployeeId { get; set; }
            public string FirstName { get; set; }
            public string LastName { get; set; }
            public string MiddleName { get; set; }
            public string Department { get; set; }
            public List<double[]> FaceVectors { get; set; }

            public string DisplayName => string.IsNullOrWhiteSpace(LastName) ? EmployeeId :
                $"{LastName}, {FirstName}" + (string.IsNullOrWhiteSpace(MiddleName) ? "" : $" {MiddleName}");
        }

        /// <summary>
        /// Raw row from database query.
        /// </summary>
        private class EmployeeEncodingRow
        {
            public int Id { get; set; }
            public string EmployeeId { get; set; }
            public string FirstName { get; set; }
            public string LastName { get; set; }
            public string MiddleName { get; set; }
            public string Department { get; set; }
            public string FaceEncodingBase64 { get; set; }
            public string FaceEncodingsJson { get; set; }
        }

        #endregion
    }
}
