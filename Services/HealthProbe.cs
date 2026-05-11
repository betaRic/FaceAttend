using System;
using FaceAttend.Services.Biometrics;
using FaceAttend.Services.Recognition;

namespace FaceAttend.Services
{
    /// <summary>
    /// SAGUPA: Mabilis na health/readiness probe para sa IIS at load balancers.
    /// 
    /// PAGLALARAWAN (Description):
    ///   Nagche-check ng system health nang mabilis para malaman ng:
    ///   - IIS kung ready bang tanggapin ang requests
    ///   - Load balancers kung i-reroute pa ba ang traffic
    ///   - Monitoring systems kung may problema
    /// 
    /// GINAGAMIT SA:
    ///   - HealthController.Index() - /health endpoint
    ///   - Application startup validation
    /// 
    /// PRINCIPLES:
    ///   1. MABILIS - hindi dapat tumagal ng higit sa 500ms
    ///   2. LIGHTWEIGHT - hindi naglo-load ng ML models
    ///   3. DETERMINISTIC - consistent ang resulta
    /// 
    /// CHECKED COMPONENTS:
    ///   - Database connectivity
    ///   - OpenVINO worker readiness
    ///   - Model integrity metadata
    ///   - Warm-up state completion
    /// 
    /// ILOKANO: "Ti HealthProbe ket kasla 'thermometer' ti sistema - 
    ///           makitana no adda nagas-ang wenno nasayaat ti sistema"
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

            var worker = BiometricWorkerClient.CheckHealth();
            snap.WorkerEnabled = worker.Enabled;
            snap.WorkerHealthy = worker.Healthy;
            snap.WorkerStatus = worker.Status;
            snap.WorkerDurationMs = worker.DurationMs;
            snap.BiometricWorkerReady = worker.Enabled && worker.Healthy;
            snap.AntiSpoofModelPresent = snap.BiometricWorkerReady;
            snap.AntiSpoofCircuitOpen = worker.Enabled && !worker.Healthy;
            snap.AntiSpoofCircuitStuck = false;

            snap.Ready =
                snap.App &&
                snap.Database &&
                snap.WriteReady &&
                snap.BiometricWorkerReady &&
                snap.ModelIntegrityOk &&
                (!snap.DatabaseMigrationsRequired || snap.DatabaseMigrationsOk) &&
                snap.WarmUpState == 1;

            return snap;
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
            public bool BiometricWorkerReady { get; set; }
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
            public bool WorkerEnabled { get; set; }
            public bool WorkerHealthy { get; set; }
            public string WorkerStatus { get; set; }
            public long WorkerDurationMs { get; set; }
        }
    }
}
