using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;

namespace FaceAttend.Services
{
    public static class DatabaseMigrationStatusService
    {
        public sealed class Summary
        {
            public DateTime CheckedAtUtc { get; set; }
            public bool Ok { get; set; }
            public bool Required { get; set; }
            public bool AdminAuditLogsTableExists { get; set; }
            public bool BiometricTemplatesTableExists { get; set; }
            public int ActiveEmployeeCount { get; set; }
            public int ActiveEmployeesWithFaceData { get; set; }
            public int BiometricTemplateRows { get; set; }
            public int ActiveBiometricTemplateRows { get; set; }
            public int ActiveEmployeesMissingTemplates { get; set; }
            public bool DevicesTableExists { get; set; }
            public int LegacyDeviceRows { get; set; }
            public int RemainingDeviceTokenRows { get; set; }
            public int RemainingDeviceTokenExpiryRows { get; set; }
            public string Error { get; set; }
            public IList<string> Warnings { get; set; } = new List<string>();
        }

        public static Summary Check(FaceAttendDBEntities db)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));

            var summary = new Summary
            {
                CheckedAtUtc = DateTime.UtcNow,
                Required = ConfigurationService.GetBool("Database:RequireStabilizationMigrations", false)
            };

            try
            {
                summary.AdminAuditLogsTableExists = TableExists(db, "AdminAuditLogs");
                summary.BiometricTemplatesTableExists = TableExists(db, "BiometricTemplates");
                summary.DevicesTableExists = TableExists(db, "Devices");

                summary.ActiveEmployeeCount = ScalarInt(db,
                    "SELECT COUNT(1) FROM dbo.Employees WHERE UPPER(ISNULL([Status], '')) = 'ACTIVE'");

                summary.ActiveEmployeesWithFaceData = ScalarInt(db,
                    @"SELECT COUNT(1)
                      FROM dbo.Employees
                      WHERE UPPER(ISNULL([Status], '')) = 'ACTIVE'
                        AND (
                            NULLIF(LTRIM(RTRIM(ISNULL(FaceEncodingBase64, ''))), '') IS NOT NULL
                            OR NULLIF(LTRIM(RTRIM(ISNULL(FaceEncodingsJson, ''))), '') IS NOT NULL
                        )");

                if (summary.BiometricTemplatesTableExists)
                {
                    summary.BiometricTemplateRows = ScalarInt(db,
                        "SELECT COUNT(1) FROM dbo.BiometricTemplates");
                    summary.ActiveBiometricTemplateRows = ScalarInt(db,
                        "SELECT COUNT(1) FROM dbo.BiometricTemplates WHERE IsActive = 1");
                    summary.ActiveEmployeesMissingTemplates = ScalarInt(db,
                        @"SELECT COUNT(1)
                          FROM dbo.Employees e
                          WHERE UPPER(ISNULL(e.[Status], '')) = 'ACTIVE'
                            AND (
                                NULLIF(LTRIM(RTRIM(ISNULL(e.FaceEncodingBase64, ''))), '') IS NOT NULL
                                OR NULLIF(LTRIM(RTRIM(ISNULL(e.FaceEncodingsJson, ''))), '') IS NOT NULL
                            )
                            AND NOT EXISTS (
                                SELECT 1
                                FROM dbo.BiometricTemplates t
                                WHERE t.EmployeeId = e.Id AND t.IsActive = 1
                            )");
                }

                if (summary.DevicesTableExists)
                {
                    summary.LegacyDeviceRows = ScalarInt(db, "SELECT COUNT(1) FROM dbo.Devices");
                    summary.RemainingDeviceTokenRows = ScalarInt(db,
                        "SELECT COUNT(1) FROM dbo.Devices WHERE NULLIF(LTRIM(RTRIM(ISNULL(DeviceToken, ''))), '') IS NOT NULL");
                    summary.RemainingDeviceTokenExpiryRows = ScalarInt(db,
                        "SELECT COUNT(1) FROM dbo.Devices WHERE TokenExpiresAt IS NOT NULL");
                }

                AddWarnings(summary);
                summary.Ok = summary.Warnings.Count == 0;
            }
            catch (Exception ex)
            {
                summary.Ok = false;
                summary.Error = ex.GetBaseException().Message;
                summary.Warnings.Add("Migration status check failed.");
            }

            return summary;
        }

        private static void AddWarnings(Summary summary)
        {
            if (!summary.AdminAuditLogsTableExists)
                summary.Warnings.Add("AdminAuditLogs table is missing.");
            if (!summary.BiometricTemplatesTableExists)
                summary.Warnings.Add("BiometricTemplates migration has not been run.");
            if (summary.BiometricTemplatesTableExists && summary.ActiveEmployeesMissingTemplates > 0)
                summary.Warnings.Add("Some active employees with face data have no active biometric template metadata.");
            if (summary.RemainingDeviceTokenRows > 0)
                summary.Warnings.Add("Legacy plaintext device tokens still exist.");
            if (summary.RemainingDeviceTokenExpiryRows > 0)
                summary.Warnings.Add("Legacy device token expiry rows still exist.");
        }

        private static bool TableExists(FaceAttendDBEntities db, string table)
        {
            return ScalarInt(db,
                "SELECT CASE WHEN OBJECT_ID(N'dbo." + table.Replace("'", "''") + "', N'U') IS NULL THEN 0 ELSE 1 END") == 1;
        }

        private static int ScalarInt(FaceAttendDBEntities db, string sql)
        {
            return db.Database.SqlQuery<int>(sql).FirstOrDefault();
        }
    }
}
