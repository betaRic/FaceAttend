using System.Collections.Generic;

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

                foreach (var emp in FaceEncodingHelper.LoadAllEmployeeFaces(db, maxPerEmployee))
                    foreach (var vec in emp.FaceVectors)
                        list.Add(new Entry { EmployeeId = emp.EmployeeId, Vec = vec });

                return list;
            }

            protected override double[] GetVectorFromEntry(Entry entry) => entry.Vec;
            protected override string GetIdFromEntry(Entry entry) => entry.EmployeeId;
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
