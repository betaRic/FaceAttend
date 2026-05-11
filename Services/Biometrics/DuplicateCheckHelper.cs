using System.Linq;

namespace FaceAttend.Services.Biometrics
{
    /// <summary>
    /// Shared helper for duplicate face detection during enrollment.
    ///
    /// WHY NOT USE FastFaceMatcher CACHE:
    ///   The cache can be stale immediately after enrollment of a different employee.
    ///   During enrollment we must query the database directly to get an accurate result.
    ///   The cache is appropriate for kiosk attendance (read-only, high frequency).
    ///   It is NOT appropriate for enrollment duplicate checks (write path, accuracy critical).
    /// </summary>
    public static class DuplicateCheckHelper
    {
        public sealed class ClosestFaceResult
        {
            public string EmployeeId { get; set; }
            public string Status { get; set; }
            public double Distance { get; set; }
        }

        /// <summary>
        /// Checks whether the given face vector already exists in the database
        /// for any active employee other than excludeEmployeeId.
        ///
        /// Returns the EmployeeId of the matching employee, or null if no duplicate.
        /// Uses a strict tolerance (typically 0.45) to avoid false positives.
        /// </summary>
        public static string FindDuplicate(
            FaceAttendDBEntities db,
            double[] faceVector,
            string excludeEmployeeId,
            double tolerance)
        {
            var closest = FindClosest(db, faceVector, excludeEmployeeId);
            return closest != null && closest.Distance <= tolerance
                ? closest.EmployeeId
                : null;
        }

        public static ClosestFaceResult FindClosest(
            FaceAttendDBEntities db,
            double[] faceVector,
            string excludeEmployeeId)
        {
            if (!FaceVectorCodec.IsValidVector(faceVector))
                return null;

            var employees = db.Employees
                .Where(e => (e.Status == "ACTIVE" || e.Status == "PENDING")
                         && e.EmployeeId != excludeEmployeeId
                         && (e.FaceEncodingBase64 != null || e.FaceEncodingsJson != null))
                .Select(e => new {
                    e.EmployeeId,
                    e.FaceEncodingBase64,
                    e.FaceEncodingsJson,
                    e.Status
                })
                .ToList();

            var maxPerEmployee = ConfigurationService.GetInt("Biometrics:Enroll:MaxStoredVectors", 25);
            ClosestFaceResult closest = null;

            foreach (var emp in employees)
            {
                var vectors = FaceEncodingHelper.LoadEmployeeVectors(
                    emp.FaceEncodingBase64,
                    emp.FaceEncodingsJson,
                    maxPerEmployee: maxPerEmployee);

                foreach (var vec in vectors)
                {
                    if (FaceVectorCodec.IsValidVector(vec))
                    {
                        var distance = FaceVectorCodec.Distance(faceVector, vec);
                        if (closest == null || distance < closest.Distance)
                        {
                            closest = new ClosestFaceResult
                            {
                                EmployeeId = emp.EmployeeId,
                                Status = emp.Status,
                                Distance = distance
                            };
                        }
                    }
                }
            }

            return closest;
        }
    }
}
