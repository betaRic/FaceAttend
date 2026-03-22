using System;
using System.Collections.Generic;
using System.Linq;
using FaceAttend.Services.Biometrics;
using FaceAttend.Services.Security;

namespace FaceAttend.Services
{
    public static class EnrollmentAdaptiveService
    {
        public static void TryAddVector(FaceAttendDBEntities db, int employeeId,
            double[] newVec, int maxStored = 8)
        {
            var emp = db.Employees.FirstOrDefault(e => e.Id == employeeId
                                                   && e.Status == "ACTIVE");
            if (emp == null || newVec == null) return;

            // Decrypt existing vectors
            List<string> existing;
            try
            {
                string plainJson;
                existing = (emp.FaceEncodingsJson != null
                            && BiometricCrypto.TryUnprotectString(emp.FaceEncodingsJson, out plainJson)
                            && plainJson != null)
                    ? Newtonsoft.Json.JsonConvert.DeserializeObject<List<string>>(plainJson)
                    : new List<string>();
            }
            catch { existing = new List<string>(); }

            // Encode and encrypt the new vector
            var newEncrypted = BiometricCrypto.ProtectBase64Bytes(
                DlibBiometrics.EncodeToBytes(newVec));
            if (string.IsNullOrEmpty(newEncrypted)) return;

            existing.Add(newEncrypted);

            // Keep only the most recent maxStored vectors
            if (existing.Count > maxStored)
                existing = existing.Skip(existing.Count - maxStored).ToList();

            emp.FaceEncodingsJson = BiometricCrypto.ProtectString(
                Newtonsoft.Json.JsonConvert.SerializeObject(existing));

            db.SaveChanges();

            // Invalidate caches so next scan uses updated vectors
            FastFaceMatcher.UpdateEmployee(emp.EmployeeId, db);
            EmployeeFaceIndex.Invalidate();
        }
    }
}
