using System;
using System.IO;
using System.Web.Hosting;
using FaceAttend.Services.Biometrics;

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
    ///   ✓ Database connectivity
    ///   ✓ Dlib models presence (shape_predictor_68_face_landmarks.dat, etc.)
    ///   ✓ Liveness model presence (minifasnet.onnx)
    ///   ✓ Warm-up state completion
    ///   ✓ Circuit breaker state
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
                DlibModelsPresent = CheckDlibModelsPresent(
                    AppSettings.GetString("Biometrics:DlibModelsDir", "~/App_Data/models/dlib")),
                LivenessModelPresent = CheckFileExists(
                    AppSettings.GetString("Biometrics:LivenessModelPath", "~/App_Data/models/liveness/minifasnet.onnx"))
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
                    }
                }
            }
            catch (Exception ex)
            {
                snap.Database = false;
                snap.DatabaseError = ex.Message;
            }

            var circuit = OnnxLiveness.GetCircuitState();
            snap.LivenessCircuitOpen = circuit.IsOpen;
            snap.LivenessCircuitStuck = circuit.IsStuck;

            snap.Ready =
                snap.App &&
                snap.Database &&
                snap.DlibModelsPresent &&
                snap.LivenessModelPresent &&
                snap.WarmUpState == 1;

            return snap;
        }

        private static bool CheckFileExists(string virtualPath)
        {
            if (string.IsNullOrWhiteSpace(virtualPath)) return false;
            try
            {
                var abs = HostingEnvironment.MapPath(virtualPath);
                return !string.IsNullOrWhiteSpace(abs) && File.Exists(abs);
            }
            catch { return false; }
        }

        private static bool CheckDlibModelsPresent(string virtualDir)
        {
            if (string.IsNullOrWhiteSpace(virtualDir)) return false;
            try
            {
                var abs = HostingEnvironment.MapPath(virtualDir);
                if (string.IsNullOrWhiteSpace(abs) || !Directory.Exists(abs))
                    return false;

                var dat = Directory.GetFiles(abs, "*.dat", SearchOption.TopDirectoryOnly);
                return dat.Length >= 2;
            }
            catch { return false; }
        }

        public class HealthSnapshot
        {
            public DateTime TimestampUtc { get; set; }
            public bool App { get; set; }
            public bool Ready { get; set; }
            public bool Database { get; set; }
            public string DatabaseError { get; set; }
            public bool DlibModelsPresent { get; set; }
            public bool LivenessModelPresent { get; set; }
            public bool LivenessCircuitOpen { get; set; }
            public bool LivenessCircuitStuck { get; set; }
            public int WarmUpState { get; set; }
            public string WarmUpMessage { get; set; }
        }
    }
}
