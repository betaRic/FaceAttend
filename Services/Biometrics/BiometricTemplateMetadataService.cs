using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using FaceAttend.Models.Dtos;

namespace FaceAttend.Services.Biometrics
{
    public static class BiometricTemplateMetadataService
    {
        public sealed class TemplateMetadata
        {
            public int VectorIndex { get; set; }
            public string ModelVersion { get; set; }
            public int EmbeddingDim { get; set; }
            public string DistanceMetric { get; set; }
            public double? QualityScore { get; set; }
            public double? AntiSpoofScore { get; set; }
            public double? Sharpness { get; set; }
            public double? PoseYaw { get; set; }
            public double? PosePitch { get; set; }
            public bool IsActive { get; set; }
            public string CreatedBy { get; set; }
        }

        public static bool TableExists(FaceAttendDBEntities db)
        {
            if (db == null) return false;
            return db.Database.SqlQuery<int>(
                "SELECT CASE WHEN OBJECT_ID(N'dbo.BiometricTemplates', N'U') IS NULL THEN 0 ELSE 1 END")
                .FirstOrDefault() == 1;
        }

        public static int CountActive(FaceAttendDBEntities db)
        {
            if (!TableExists(db)) return 0;
            return db.Database.SqlQuery<int>(
                "SELECT COUNT(1) FROM dbo.BiometricTemplates WHERE IsActive = 1")
                .FirstOrDefault();
        }

        public static void ReplaceForEmployee(
            FaceAttendDBEntities db,
            int employeeDbId,
            IEnumerable<EnrollCandidate> selected,
            string createdBy,
            bool isActive)
        {
            if (db == null || employeeDbId <= 0 || selected == null)
                return;
            if (!TableExists(db))
                return;

            var now = DateTime.UtcNow;
            var policy = BiometricPolicy.Current;
            var modelVersion = policy.ModelVersion;
            var metric = policy.DistanceMetric;
            var rows = selected.Where(x => x != null && x.Vec != null).ToList();

            db.Database.ExecuteSqlCommand(
                @"UPDATE dbo.BiometricTemplates
                  SET IsActive = 0, RetiredAt = @retiredAt
                  WHERE EmployeeId = @employeeId AND RetiredAt IS NULL",
                new SqlParameter("@retiredAt", now),
                new SqlParameter("@employeeId", employeeDbId));

            for (var i = 0; i < rows.Count; i++)
            {
                var c = rows[i];
                db.Database.ExecuteSqlCommand(
                    @"INSERT INTO dbo.BiometricTemplates
                      (EmployeeId, VectorIndex, ModelVersion, EmbeddingDim, DistanceMetric,
                       QualityScore, AntiSpoofScore, Sharpness, PoseYaw, PosePitch,
                       CreatedAt, CreatedBy, IsActive)
                      VALUES
                      (@employeeId, @vectorIndex, @modelVersion, @embeddingDim, @metric,
                       @qualityScore, @antiSpoofScore, @sharpness, @poseYaw, @posePitch,
                       @createdAt, @createdBy, @isActive)",
                    new SqlParameter("@employeeId", employeeDbId),
                    new SqlParameter("@vectorIndex", i),
                    new SqlParameter("@modelVersion", modelVersion),
                    new SqlParameter("@embeddingDim", c.Vec.Length),
                    new SqlParameter("@metric", metric),
                    new SqlParameter("@qualityScore", (object)c.QualityScore ?? DBNull.Value),
                    new SqlParameter("@antiSpoofScore", (object)c.AntiSpoof ?? DBNull.Value),
                    new SqlParameter("@sharpness", (object)c.Sharpness ?? DBNull.Value),
                    new SqlParameter("@poseYaw", (object)c.PoseYaw ?? DBNull.Value),
                    new SqlParameter("@posePitch", (object)c.PosePitch ?? DBNull.Value),
                    new SqlParameter("@createdAt", now),
                    new SqlParameter("@createdBy", (object)createdBy ?? DBNull.Value),
                    new SqlParameter("@isActive", isActive));
            }
        }

        public static void SetActiveForEmployee(FaceAttendDBEntities db, int employeeDbId, bool isActive)
        {
            if (db == null || employeeDbId <= 0 || !TableExists(db))
                return;

            db.Database.ExecuteSqlCommand(
                @"UPDATE dbo.BiometricTemplates
                  SET IsActive = @isActive,
                      RetiredAt = CASE WHEN @isActive = 1 THEN NULL ELSE COALESCE(RetiredAt, SYSUTCDATETIME()) END
                  WHERE EmployeeId = @employeeId
                    AND (@isActive = 0 OR RetiredAt IS NULL)",
                new SqlParameter("@isActive", isActive),
                new SqlParameter("@employeeId", employeeDbId));
        }
    }
}
