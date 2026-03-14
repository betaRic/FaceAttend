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
            if (faceVector == null || faceVector.Length != 128)
                return null;

            var employees = db.Employees
                .Where(e => e.Status == "ACTIVE"
                         && e.EmployeeId != excludeEmployeeId
                         && (e.FaceEncodingBase64 != null || e.FaceEncodingsJson != null))
                .Select(e => new {
                    e.EmployeeId,
                    e.FaceEncodingBase64,
                    e.FaceEncodingsJson
                })
                .ToList();

            foreach (var emp in employees)
            {
                var vectors = FaceEncodingHelper.LoadEmployeeVectors(
                    emp.FaceEncodingBase64,
                    emp.FaceEncodingsJson,
                    maxPerEmployee: 5);

                foreach (var vec in vectors)
                {
                    if (vec != null && vec.Length == 128)
                    {
                        if (DlibBiometrics.Distance(faceVector, vec) <= tolerance)
                            return emp.EmployeeId;
                    }
                }
            }

            return null;
        }
    }
}
