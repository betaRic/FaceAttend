using System;
using FaceAttend.Services.Biometrics;

namespace FaceAttend.Services
{
    /// <summary>
    /// Fast readiness snapshot for IIS, monitoring, and the admin operations page.
    /// </summary>
    public static class HealthProbe
    {
        public static HealthSnapshot Check()
        {
            var snap = new HealthSnapshot
            {
                TimestampUtc = DateTime.UtcNow,
                App = true,
                WarmUpState = MvcApplication.WarmUpState,
                WarmUpMessage = MvcApplication.WarmUpMessage,
                ModelVersion = BiometricPolicy.Current.ModelVersion
            };

            try
            {
                using (var db = new FaceAttendDBEntities())
                {
                    db.Database.Connection.Open();
                    using (var cmd = db.Database.Connection.CreateCommand())
                    {
                        cmd.CommandText = "SELECT 1";
                        var result = cmd.ExecuteScalar();
                        snap.Database = result != null;

                        cmd.CommandText = "SELECT CONVERT(nvarchar(20), DATABASEPROPERTYEX(DB_NAME(), 'Updateability'))";
                        var updateability = Convert.ToString(cmd.ExecuteScalar());
                        snap.WriteReady = string.Equals(updateability, "READ_WRITE", StringComparison.OrdinalIgnoreCase);
                    }

                    var migrations = DatabaseMigrationStatusService.Check(db);
                    snap.DatabaseMigrationsOk = migrations.Ok;
                    snap.DatabaseMigrationsRequired = migrations.Required;
                    snap.DatabaseMigrationError = migrations.Error;
                    snap.BiometricTemplatesTableExists = migrations.BiometricTemplatesTableExists;
                    snap.ActiveEmployeesMissingTemplates = migrations.ActiveEmployeesMissingTemplates;
                    snap.RemainingDeviceTokenRows = migrations.RemainingDeviceTokenRows;
                }
            }
            catch (Exception ex)
            {
                snap.Database = false;
                snap.DatabaseError = ex.Message;
            }

            var modelIntegrity = ModelIntegrityService.Check();
            snap.ModelIntegrityOk = modelIntegrity.Ok;
            snap.ModelHashesConfigured = modelIntegrity.ExpectedHashesConfigured;
            snap.ModelAclOk = modelIntegrity.AclOk;
            snap.ModelRequireReadOnlyAcl = modelIntegrity.RequireReadOnlyAcl;
            snap.ModelIntegrityError = modelIntegrity.Error;

            var matcherStats = FastFaceMatcher.GetStats();
            snap.FaceMatcherInitialized = matcherStats.IsInitialized;
            snap.FaceMatcherEmployees = matcherStats.EmployeeCount;
            snap.FaceMatcherVectors = matcherStats.TotalFaceVectors;
            snap.FaceMatcherCacheAgeSeconds = matcherStats.LastLoaded == DateTime.MinValue
                ? (int?)null
                : (int)Math.Max(0, (DateTime.UtcNow - matcherStats.LastLoaded).TotalSeconds);

            var engine = BiometricEngine.GetStatus();
            snap.EngineEnabled = engine.Enabled;
            snap.EngineHealthy = engine.Healthy;
            snap.EngineReady = engine.Ready;
            snap.EngineRuntime = engine.Runtime;
            snap.EngineStatus = engine.Status;
            snap.EngineDurationMs = engine.DurationMs;
            snap.EngineAnalyzeSupported = engine.AnalyzeSupported;
            snap.BiometricEngineReady = engine.Ready;
            snap.AntiSpoofModelPresent = EngineSlotPresent(engine, "antiSpoof");
            snap.AntiSpoofCircuitOpen = engine.Enabled && !engine.Ready;
            snap.AntiSpoofCircuitStuck = false;

            snap.Ready =
                snap.App &&
                snap.Database &&
                snap.WriteReady &&
                snap.BiometricEngineReady &&
                snap.ModelIntegrityOk &&
                (!snap.DatabaseMigrationsRequired || snap.DatabaseMigrationsOk) &&
                snap.WarmUpState == 1;

            return snap;
        }

        private static bool EngineSlotPresent(BiometricEngineStatus engine, string slotName)
        {
            if (engine?.Models == null || string.IsNullOrWhiteSpace(slotName))
                return false;

            foreach (var model in engine.Models)
            {
                if (model != null &&
                    string.Equals(model.Slot, slotName, StringComparison.OrdinalIgnoreCase) &&
                    model.Exists &&
                    model.ExtensionOk)
                    return true;
            }

            return false;
        }

        public class HealthSnapshot
        {
            public DateTime TimestampUtc { get; set; }
            public bool App { get; set; }
            public bool Ready { get; set; }
            public bool Database { get; set; }
            public string DatabaseError { get; set; }
            public bool WriteReady { get; set; }
            public bool DatabaseMigrationsOk { get; set; }
            public bool DatabaseMigrationsRequired { get; set; }
            public string DatabaseMigrationError { get; set; }
            public bool BiometricTemplatesTableExists { get; set; }
            public int ActiveEmployeesMissingTemplates { get; set; }
            public int RemainingDeviceTokenRows { get; set; }
            public bool BiometricEngineReady { get; set; }
            public bool AntiSpoofModelPresent { get; set; }
            public bool AntiSpoofCircuitOpen { get; set; }
            public bool AntiSpoofCircuitStuck { get; set; }
            public int WarmUpState { get; set; }
            public string WarmUpMessage { get; set; }
            public string ModelVersion { get; set; }
            public bool ModelIntegrityOk { get; set; }
            public bool ModelHashesConfigured { get; set; }
            public bool ModelAclOk { get; set; }
            public bool ModelRequireReadOnlyAcl { get; set; }
            public string ModelIntegrityError { get; set; }
            public bool FaceMatcherInitialized { get; set; }
            public int FaceMatcherEmployees { get; set; }
            public int FaceMatcherVectors { get; set; }
            public int? FaceMatcherCacheAgeSeconds { get; set; }
            public bool EngineEnabled { get; set; }
            public bool EngineHealthy { get; set; }
            public bool EngineReady { get; set; }
            public bool EngineAnalyzeSupported { get; set; }
            public string EngineRuntime { get; set; }
            public string EngineStatus { get; set; }
            public long EngineDurationMs { get; set; }
        }
    }
}
