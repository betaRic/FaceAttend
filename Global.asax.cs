using System;
using System.Threading.Tasks;
using System.Configuration;
using System.Web;
using System.Web.Mvc;
using System.Web.Optimization;
using System.Web.Routing;
using FaceAttend.Services;
using FaceAttend.Services.Background;
using FaceAttend.Services.Biometrics;

namespace FaceAttend
{
    public class MvcApplication : System.Web.HttpApplication
    {
        private static volatile int _warmUpState = 0;
        private static string _warmUpMessage = "NOT_STARTED";

        public static int WarmUpState
        {
            get { return _warmUpState; }
        }

        public static string WarmUpMessage
        {
            get { return _warmUpMessage; }
        }

        protected void Application_Start()
        {
            AreaRegistration.RegisterAllAreas();
            FilterConfig.RegisterGlobalFilters(GlobalFilters.Filters);
            RouteConfig.RegisterRoutes(RouteTable.Routes);
            BundleConfig.RegisterBundles(BundleTable.Bundles);
            try
            {
                ValidateCriticalConfiguration();
            }
            catch (Exception ex)
            {
                try
                {
                    using (var el = new System.Diagnostics.EventLog("Application"))
                    {
                        el.Source = "FaceAttend";
                        el.WriteEntry(
                            "[Application_Start] STARTUP VALIDATION FAILED:\n" + ex.ToString(),
                            System.Diagnostics.EventLogEntryType.Error);
                    }
                }
                catch { }

                System.Diagnostics.Trace.TraceError(
                    "[Application_Start] STARTUP VALIDATION FAILED: " + ex.ToString());
                throw;
            }
            TempFileCleanupTask.Start();

            // ================================================================
            // PHASE 2 FIX (P-01, P-02, P-05): Warm-up ng lahat ng mabibigat
            // na resources sa background bago pa dumating ang unang request.
            //
            // BAKIT BACKGROUND TASK?
            //   Ang Application_Start ay tumatakbo sa main thread ng IIS startup.
            //   Kung mag-block tayo dito, mabagal ang startup at baka mag-timeout
            //   ang IIS health checks. Kaya ginagawa natin ito sa background.
            //
            // ANO ANG GINAGAWA NG WARM-UP?
            //   1. DlibBiometrics.InitializePool() — gumagawa ng N instances ng
            //      FaceRecognition. Ito ang pinakamatagal (30-60 segundo depende
            //      sa pool size at CPU speed). Habang hindi pa tapos ito,
            //      ang mga unang scans ay mag-queue sa pool at hihintay.
            //
            //   2. OnnxLiveness.WarmUp() — naglo-load ng ONNX model sa memory.
            //      Mas mabilis kaysa sa Dlib (3-5 segundo).
            //
            //   3. EmployeeFaceIndex warm-up — naglo-load ng lahat ng face
            //      encodings ng mga empleyado mula sa database papunta sa
            //      in-memory cache. Kapag hindi ito ginawa, ang unang scan
            //      pagkatapos ng app pool recycle ay magti-trigger ng rebuild
            //      na nag-o-block ng lahat ng ibang scans habang nangyayari ito.
            // ================================================================
            _warmUpState = 0;
            _warmUpMessage = "RUNNING";

            var warmUpTask = Task.Run(() =>
            {
                // Hakbang 1: Initialize ang Dlib instance pool.
                // Kailangan itong gawin bago ang ONNX warm-up para magamit
                // ng mga unang requests ang pool agad pagkatapos ng startup.
                try
                {
                    DlibBiometrics.InitializePool();
                    System.Diagnostics.Trace.TraceInformation(
                        "[Application_Start] Dlib instance pool — na-initialize na.");
                }
                catch (Exception ex)
                {
                    // Hindi dapat mag-crash ang app kahit hindi ma-load ang Dlib.
                    // Magbibigay ng POOL_TIMEOUT error sa mga scans hanggang hindi
                    // pa nare-resolve ang problema.
                    System.Diagnostics.Trace.TraceError(
                        "[Application_Start] BABALA: Hindi na-initialize ang Dlib pool: " +
                        ex.Message);
                }

                // Hakbang 2: I-load ang ONNX liveness model.
                try
                {
                    OnnxLiveness.WarmUp();
                    System.Diagnostics.Trace.TraceInformation(
                        "[Application_Start] ONNX liveness model — na-load na.");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.TraceError(
                        "[Application_Start] BABALA: Hindi na-load ang ONNX model: " +
                        ex.Message);
                }

                // Hakbang 3: I-pre-load ang face encodings ng mga empleyado.
                // Ito ang PHASE 2 FIX (P-05) — winarm-up na natin ang index
                // para ang unang scan ay hindi na kailangang mag-rebuild.
                try
                {
                    using (var db = new FaceAttendDBEntities())
                    {
                        EmployeeFaceIndex.Rebuild(db);
                        System.Diagnostics.Trace.TraceInformation(
                            "[Application_Start] Employee face index — na-load na.");
                    }
                }
                catch (Exception ex)
                {
                    // Non-fatal: mag-rebuild ito sa unang scan request.
                    System.Diagnostics.Trace.TraceWarning(
                        "[Application_Start] Hindi na-pre-load ang employee face index: " +
                        ex.Message + " — mag-rebuild sa unang scan.");
                }

                _warmUpState = 1;
                _warmUpMessage = "COMPLETE";
            });

            Task.Run(() =>
            {
                try
                {
                    if (!warmUpTask.Wait(TimeSpan.FromMinutes(5)))
                    {
                        _warmUpState = -1;
                        _warmUpMessage = "TIMEOUT";
                        System.Diagnostics.Trace.TraceError(
                            "[Application_Start] CRITICAL: Warm-up timed out after 5 minutes.");
                    }
                }
                catch (Exception ex)
                {
                    _warmUpState = -1;
                    _warmUpMessage = "FAILED";
                    System.Diagnostics.Trace.TraceError(
                        "[Application_Start] Warm-up observer failed: " + ex.Message);
                }
            });
        }

        // ────────────────────────────────────────────────────────────────────
        // Custom error routing
        // ────────────────────────────────────────────────────────────────────

        protected void Application_Error()
        {
            // Sa local debug mode: hayaan ang ASP.NET na mag-show ng YSOD.
            if (Context != null && Context.IsDebuggingEnabled &&
                Request != null && Request.IsLocal)
            {
                Response.TrySkipIisCustomErrors = true;
                return;
            }

            var ex = Server.GetLastError();
            if (ex == null) return;

            // Iwasan ang infinite loop kapag nag-error sa error page mismo.
            var rawUrl = (Request?.RawUrl ?? "");
            if (rawUrl.StartsWith(
                    VirtualPathUtility.ToAbsolute("~/Error"),
                    StringComparison.OrdinalIgnoreCase) ||
                rawUrl.StartsWith(
                    VirtualPathUtility.ToAbsolute("~/Admin/Error"),
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            int code = 500;
            if (ex is HttpException httpEx)
                code = httpEx.GetHttpCode();

            string action = MapStatusToAction(code);
            bool isAdmin = IsAdminRequest(Request);
            string target = isAdmin
                ? $"~/Admin/Error/{action}"
                : $"~/Error/{action}";

            Server.ClearError();
            Response.Clear();
            Response.TrySkipIisCustomErrors = true;
            Response.StatusCode = code;
            Context.Items["__fa_error_handled"] = true;

            Server.TransferRequest(VirtualPathUtility.ToAbsolute(target), true);
        }

        protected void Application_EndRequest()
        {
            if (Context == null || Request == null || Response == null) return;
            if (Context.IsDebuggingEnabled && Request.IsLocal) return;
            if (Context.Items["__fa_error_handled"] != null) return;

            if (Response.StatusCode == 404)
            {
                var rawUrl = (Request.RawUrl ?? "");
                if (rawUrl.StartsWith(
                        VirtualPathUtility.ToAbsolute("~/Error"),
                        StringComparison.OrdinalIgnoreCase) ||
                    rawUrl.StartsWith(
                        VirtualPathUtility.ToAbsolute("~/Admin/Error"),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                bool isAdmin = IsAdminRequest(Request);
                string target = isAdmin ? "~/Admin/Error/NotFound" : "~/Error/NotFound";

                Response.TrySkipIisCustomErrors = true;
                Context.Items["__fa_error_handled"] = true;
                Server.TransferRequest(VirtualPathUtility.ToAbsolute(target), true);
            }
        }

        // ────────────────────────────────────────────────────────────────────
        // Shutdown cleanup
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Nililinis ang lahat ng unmanaged resources bago mag-shutdown ang app pool.
        /// Tinatawagin ito ng IIS isang beses lang sa bawat app domain shutdown.
        ///
        /// PHASE 2 FIX (P-01): DlibBiometrics.DisposePool() — pinalitan ang
        /// lumang DisposeInstance() para ma-dispose ang lahat ng pool instances.
        /// </summary>
        protected void Application_End()
        {
            // I-stop ang background cleanup thread bago mag-shutdown ang app pool.
            // Ginagamit natin ang StopSingleton() — static wrapper para hindi kailangan
            // ng reference sa instance (ang Stop() mismo ay instance method ng IRegisteredObject).
            try { TempFileCleanupTask.StopSingleton(false); }
            catch { /* best effort */ }

            // I-dispose ang lahat ng Dlib FaceRecognition instances sa pool.
            try { DlibBiometrics.DisposePool(); }
            catch { /* best effort */ }

            // I-dispose ang ONNX InferenceSession.
            try { OnnxLiveness.DisposeSession(); }
            catch { /* best effort */ }
        }

        // ────────────────────────────────────────────────────────────────────
        // Private helpers
        // ────────────────────────────────────────────────────────────────────

        private static void ValidateCriticalConfiguration()
        {
            var pinHash = (
                Environment.GetEnvironmentVariable("FACEATTEND_ADMIN_PIN_HASH")
                ?? ConfigurationManager.AppSettings["Admin:PinHash"]
                ?? ""
            ).Trim();

            if (string.IsNullOrWhiteSpace(pinHash))
            {
                System.Diagnostics.Trace.TraceError(
                    "[Application_Start] CRITICAL: FACEATTEND_ADMIN_PIN_HASH is not configured. " +
                    "Admin PIN login will fail until the environment variable is set.");
            }

            var adminRanges = (
                Environment.GetEnvironmentVariable("FACEATTEND_ADMIN_ALLOWED_IP_RANGES")
                ?? AppSettings.GetString("Admin:AllowedIpRanges", "")
                ?? ""
            ).Trim();

            if (string.Equals(adminRanges, "127.0.0.1, ::1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(adminRanges, "127.0.0.1,::1", StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Trace.TraceWarning(
                    "[Application_Start] Admin:AllowedIpRanges is still limited to localhost. " +
                    "Set FACEATTEND_ADMIN_ALLOWED_IP_RANGES or update Web.config before production deployment.");
            }
        }

        private static bool IsAdminRequest(HttpRequest request)
        {
            if (request == null) return false;
            var adminRoot = VirtualPathUtility.ToAbsolute("~/Admin");
            var path = request.Path ?? "";
            return path.StartsWith(adminRoot, StringComparison.OrdinalIgnoreCase);
        }

        private static string MapStatusToAction(int statusCode)
        {
            switch (statusCode)
            {
                case 400: return "BadRequest";
                case 401: return "Forbidden";
                case 403: return "Forbidden";
                case 404: return "NotFound";
                case 429: return "TooManyRequests";
                case 503: return "Unavailable";
                default: return "Index";
            }
        }
    }
}