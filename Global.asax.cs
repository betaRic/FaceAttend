using System;
using System.Configuration;
using System.Threading.Tasks;
using System.Web;
using System.Web.Mvc;
using System.Web.Optimization;
using System.Web.Routing;
using Newtonsoft.Json;
using FaceAttend.Services;
using FaceAttend.Services.Background;
using FaceAttend.Services.Biometrics;

namespace FaceAttend
{
    public class MvcApplication : System.Web.HttpApplication
    {
        private const int WarmUpStateRunning  = 0;
        private const int WarmUpStateComplete = 1;
        private const int WarmUpStateFailed   = -1;

        private const string WarmUpMessageRunning  = "RUNNING";
        private const string WarmUpMessageComplete = "COMPLETE";
        private const string WarmUpMessageTimeout  = "TIMEOUT";
        private const string WarmUpMessageFailed   = "FAILED";

        private static readonly TimeSpan WarmUpTimeout = TimeSpan.FromMinutes(5);

        private static volatile int _warmUpState = WarmUpStateRunning;
        private static string _warmUpMessage = WarmUpMessageRunning;

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
            RegisterMvcComponents();
            RunStartupValidation();
            StartBackgroundServices();
            StartWarmUpInBackground();
        }

        private static void RegisterMvcComponents()
        {
            AreaRegistration.RegisterAllAreas();
            FilterConfig.RegisterGlobalFilters(GlobalFilters.Filters);
            RouteConfig.RegisterRoutes(RouteTable.Routes);
            BundleConfig.RegisterBundles(BundleTable.Bundles);
        }

        private static void RunStartupValidation()
        {
            try
            {
                ValidateCriticalConfiguration();
            }
            catch (Exception ex)
            {
                LogStartupValidationFailure(ex);
                throw;
            }
        }

        private static void LogStartupValidationFailure(Exception ex)
        {
            try
            {
                using (var el = new System.Diagnostics.EventLog("Application"))
                {
                    el.Source = "FaceAttend";
                    el.WriteEntry(
                        "[Application_Start] STARTUP VALIDATION FAILED:\n" + ex,
                        System.Diagnostics.EventLogEntryType.Error);
                }
            }
            catch { }

            System.Diagnostics.Trace.TraceError(
                "[Application_Start] STARTUP VALIDATION FAILED: " + ex);
        }

        private static void StartBackgroundServices()
        {
            TempFileCleanupTask.Start();
        }

        private static void StartWarmUpInBackground()
        {
            _warmUpState = WarmUpStateRunning;
            _warmUpMessage = WarmUpMessageRunning;

            var warmUpTask = Task.Run(() => RunWarmUpPipeline());

            Task.Run(() => ObserveWarmUpTask(warmUpTask));
        }

        private static void RunWarmUpPipeline()
        {
            WarmUpBiometricWorker();
            WarmUpEmployeeFaceIndex();
            WarmUpVisitorFaceIndex();

            _warmUpState = WarmUpStateComplete;
            _warmUpMessage = WarmUpMessageComplete;
        }

        private static void ObserveWarmUpTask(Task warmUpTask)
        {
            try
            {
                if (!warmUpTask.Wait(WarmUpTimeout))
                {
                    _warmUpState = WarmUpStateFailed;
                    _warmUpMessage = WarmUpMessageTimeout;

                    System.Diagnostics.Trace.TraceError(
                        "[Application_Start] CRITICAL: Warm-up timed out after " +
                        WarmUpTimeout.TotalMinutes + " minutes.");
                }
            }
            catch (Exception ex)
            {
                _warmUpState = WarmUpStateFailed;
                _warmUpMessage = WarmUpMessageFailed;

                System.Diagnostics.Trace.TraceError(
                    "[Application_Start] Warm-up observer failed: " + ex.Message);
            }
        }

        private static void WarmUpBiometricWorker()
        {
            try
            {
                OpenVinoBiometrics.InitializeWorker();

                System.Diagnostics.Trace.TraceInformation(
                    "[Application_Start] OpenVINO biometric worker is healthy.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError(
                    "[Application_Start] WARNING: OpenVINO biometric worker is not ready: " +
                    ex.Message);
            }
        }

        private static void WarmUpEmployeeFaceIndex()
        {
            try
            {
                // CONSOLIDATED: FastFaceMatcher now has BallTree built-in
                // No need to separately rebuild EmployeeFaceIndex
                // This was redundant - both loaded the same data at startup
                using (var db = new FaceAttendDBEntities())
                {
                    FastFaceMatcher.ReloadFromDatabase();
                }

                System.Diagnostics.Trace.TraceInformation(
                    "[Application_Start] Employee face index preloaded successfully. " +
                    FastFaceMatcher.GetStats()?.ToString());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "[Application_Start] Employee face index preload failed: " +
                    ex.Message + " | Fallback: rebuild on first employee scan.");
            }
        }

        private static void WarmUpVisitorFaceIndex()
        {
            try
            {
                using (var db = new FaceAttendDBEntities())
                {
                    VisitorFaceIndex.Rebuild(db);
                }

                System.Diagnostics.Trace.TraceInformation(
                    "[Application_Start] Visitor face index preloaded successfully.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "[Application_Start] Visitor face index preload failed: " +
                    ex.Message + " | Fallback: rebuild on first visitor scan.");
            }
        }

        protected void Application_Error()
        {
            var ex = Server.GetLastError();
            var httpEx = ex as HttpException;
            var statusCode = httpEx?.GetHttpCode() ?? 500;

            System.Diagnostics.Trace.TraceError($"[Application_Error] {statusCode}: {ex?.Message}");

            if (HttpContext.Current?.IsDebuggingEnabled == true && Request.IsLocal)
                return;

            var routeData = HttpContext.Current?.Request?.RequestContext?.RouteData;
            var area = routeData?.DataTokens["area"] as string;
            if (!string.IsNullOrEmpty(area) && area.Equals("Admin", StringComparison.OrdinalIgnoreCase))
                return;

            var isJsonRequest = false;
            try
            {
                var accept = Request?.Headers?["Accept"] ?? string.Empty;
                var xhr = Request?.Headers?["X-Requested-With"] ?? string.Empty;
                var contentType = Request?.ContentType ?? string.Empty;
                isJsonRequest = accept.IndexOf("application/json", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                string.Equals(xhr, "XMLHttpRequest", StringComparison.OrdinalIgnoreCase) ||
                                contentType.IndexOf("application/json", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                isJsonRequest = false;
            }

            if (isJsonRequest)
            {
                Server.ClearError();
                Response.TrySkipIisCustomErrors = true;
                Response.StatusCode = statusCode;
                Response.ContentType = "application/json";

                var payload = new
                {
                    ok = false,
                    error = statusCode == 404 ? "NOT_FOUND" : "SERVER_ERROR",
                    message = (Request != null && Request.IsLocal && HttpContext.Current?.IsDebuggingEnabled == true)
                        ? (ex?.GetBaseException()?.Message ?? "Unexpected error")
                        : "We couldn't process your request."
                };

                Response.Write(JsonConvert.SerializeObject(payload));
                try { Response.End(); } catch { }
                return;
            }
            
            Server.ClearError();
            Response.TrySkipIisCustomErrors = true;

            string action;
            switch (statusCode)
            {
                case 404:
                    action = "NotFound";
                    break;
                case 403:
                    action = "Forbidden";
                    break;
                case 429:
                    action = "TooManyRequests";
                    break;
                case 400:
                    action = "BadRequest";
                    break;
                default:
                    action = "Index";
                    break;
            }
            
            try { Response.Redirect($"~/Error/{action}"); }
            catch { }
        }

        protected void Application_EndRequest()
        {
            if (Context == null || Request == null || Response == null) return;
            if (ShouldBypassCustomErrors()) return;
            if (Context.Items["__fa_error_handled"] != null) return;

            if (Response.StatusCode != 404) return;

            var rawUrl = Request.RawUrl ?? "";
            if (IsErrorRoute(rawUrl))
            {
                return;
            }

            string target = "~/Error/NotFound";

            Response.TrySkipIisCustomErrors = true;
            Context.Items["__fa_error_handled"] = true;

            Server.TransferRequest(VirtualPathUtility.ToAbsolute(target), true);
        }

        protected void Application_End()
        {
            try
            {
                TempFileCleanupTask.StopSingleton(false);
            }
            catch
            {
                // Best effort cleanup only.
            }

            try
            {
                OpenVinoBiometrics.DisposeWorker();
            }
            catch
            {
                // Best effort cleanup only.
            }

        }

        private static void ValidateCriticalConfiguration()
        {
            ValidateAdminPinHashConfiguration();
            ValidateAdminAllowedIpRangesConfiguration();
        }

        private static void ValidateAdminPinHashConfiguration()
        {
            var pinHash =
                (Environment.GetEnvironmentVariable("FACEATTEND_ADMIN_PIN_HASH")
                ?? ConfigurationManager.AppSettings["Admin:PinHash"]
                ?? string.Empty)
                .Trim();

            if (string.IsNullOrWhiteSpace(pinHash))
            {
                System.Diagnostics.Trace.TraceError(
                    "[Application_Start] CRITICAL: Admin PIN hash is not configured. " +
                    "Admin PIN login will fail until FACEATTEND_ADMIN_PIN_HASH or Admin:PinHash is set.");
            }
        }

        private static void ValidateAdminAllowedIpRangesConfiguration()
        {
            var adminRanges =
                (Environment.GetEnvironmentVariable("FACEATTEND_ADMIN_ALLOWED_IP_RANGES")
                ?? ConfigurationService.GetString("Admin:AllowedIpRanges", string.Empty)
                ?? string.Empty)
                .Trim();

            if (string.Equals(adminRanges, "127.0.0.1, ::1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(adminRanges, "127.0.0.1,::1", StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Trace.TraceWarning(
                    "[Application_Start] WARNING: Admin:AllowedIpRanges is still localhost-only. " +
                    "Update FACEATTEND_ADMIN_ALLOWED_IP_RANGES or Web.config before production use.");
            }
        }

        private bool ShouldBypassCustomErrors()
        {
            return Context != null &&
                   Context.IsDebuggingEnabled &&
                   Request != null &&
                   Request.IsLocal;
        }

        private static bool IsErrorRoute(string rawUrl)
        {
            if (string.IsNullOrWhiteSpace(rawUrl)) return false;

            return rawUrl.StartsWith(
                       VirtualPathUtility.ToAbsolute("~/Error"),
                       StringComparison.OrdinalIgnoreCase)
                   ||
                   rawUrl.StartsWith(
                       VirtualPathUtility.ToAbsolute("~/Admin/Error"),
                       StringComparison.OrdinalIgnoreCase);
        }
    }
}
