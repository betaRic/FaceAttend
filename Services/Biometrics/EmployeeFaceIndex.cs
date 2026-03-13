using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace FaceAttend.Services.Biometrics
{
    /// <summary>
    /// Employee face index - REFACTORED to use FaceIndexBase and FaceEncodingHelper
    /// for consistent face encoding loading with FastFaceMatcher.
    /// </summary>
    public static class EmployeeFaceIndex
    {
        public class Entry
        {
            public string EmployeeId { get; set; }
            public double[] Vec { get; set; }
        }

        // Inner class implementing the base
        private class EmployeeFaceIndexImpl : FaceIndexBase<Entry>
        {
            public EmployeeFaceIndexImpl() : base(
                ConfigurationService.GetInt("Biometrics:BallTreeThreshold", 50))
            {
            }

            protected override List<Entry> LoadEntriesFromDatabase(FaceAttendDBEntities db)
            {
                var list = new List<Entry>();
                var maxPerEmployee = ConfigurationService.GetInt("Biometrics:Enroll:MaxImages", 5);

                // Try SQL query first (supports multi-encoding)
                bool loadedViaSql = TryLoadViaSql(db, list, maxPerEmployee);

                if (!loadedViaSql)
                {
                    // Fallback to EF using shared helper
                    LoadViaEF(db, list, maxPerEmployee);
                }

                return list;
            }

            protected override double[] GetVectorFromEntry(Entry entry) => entry.Vec;
            protected override string GetIdFromEntry(Entry entry) => entry.EmployeeId;

            private bool TryLoadViaSql(FaceAttendDBEntities db, List<Entry> list, int maxPerEmployee)
            {
                try
                {
                    var rows = db.Database.SqlQuery<EmployeeRow>(
                        "SELECT EmployeeId, FaceEncodingBase64, FaceEncodingsJson " +
                        "FROM Employees " +
                        "WHERE Status = 'ACTIVE' AND (FaceEncodingBase64 IS NOT NULL OR FaceEncodingsJson IS NOT NULL)"
                    ).ToList();

                    foreach (var r in rows)
                    {
                        LoadEmployeeEncodings(r, list, maxPerEmployee);
                    }
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            private void LoadViaEF(FaceAttendDBEntities db, List<Entry> list, int maxPerEmployee)
            {
                // Use shared helper for consistent loading
                var employees = FaceEncodingHelper.LoadAllEmployeeFaces(db, maxPerEmployee);
                
                foreach (var emp in employees)
                {
                    foreach (var vec in emp.FaceVectors)
                    {
                        list.Add(new Entry { EmployeeId = emp.EmployeeId, Vec = vec });
                    }
                }
            }

            private void LoadEmployeeEncodings(EmployeeRow r, List<Entry> list, int maxPerEmployee)
            {
                if (string.IsNullOrWhiteSpace(r.EmployeeId)) return;

                // Use shared helper for consistent decoding
                var vectors = FaceEncodingHelper.LoadEmployeeVectors(
                    r.FaceEncodingBase64,
                    r.FaceEncodingsJson,
                    maxPerEmployee);

                foreach (var vec in vectors)
                {
                    list.Add(new Entry { EmployeeId = r.EmployeeId, Vec = vec });
                }
            }
        }

        private class EmployeeRow
        {
            public string EmployeeId { get; set; }
            public string FaceEncodingBase64 { get; set; }
            public string FaceEncodingsJson { get; set; }
        }

        // Singleton instance
        private static readonly EmployeeFaceIndexImpl _instance = new EmployeeFaceIndexImpl();

        // Public API - delegates to instance
        public static void Invalidate() => _instance.Invalidate();
        public static IReadOnlyList<Entry> GetEntries(FaceAttendDBEntities db) => _instance.GetEntries(db);
        public static void Rebuild(FaceAttendDBEntities db) => _instance.Rebuild(db);
        public static string FindNearest(FaceAttendDBEntities db, double[] vec, double tolerance, out double bestDist)
            => _instance.FindNearest(db, vec, tolerance, out bestDist);
    }
}
