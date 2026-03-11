using System;
using System.Configuration;
using System.Threading.Tasks;
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
        // =====================================================================
        // Warm-up state tracking
        // ---------------------------------------------------------------------
        // Layunin:
        //   Para may simple status tayo kung ang mabibigat na startup resources
        //   ay tapos na ba mag-load o hindi pa.
        //
        // State meaning:
        //   0  = running / not yet complete
        //   1  = complete
        //  -1  = failed or timeout
        // =====================================================================
        private const int WarmUpStateRunning = 0;
        private const int WarmUpStateComplete = 1;
        private const int WarmUpStateFailed = -1;

        private const string WarmUpMessageRunning = "RUNNING";
        private const string WarmUpMessageComplete = "COMPLETE";
        private const string WarmUpMessageTimeout = "TIMEOUT";
        private const string WarmUpMessageFailed = "FAILED";

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

        // =====================================================================
        // Application startup
        // ---------------------------------------------------------------------
        // Dito nagsisimula ang buong MVC app lifecycle.
        //
        // High-level flow:
        //   1. Register MVC components
        //   2. Validate critical config
        //   3. Start background cleanup task
        //   4. Start background warm-up
        // =====================================================================
        protected void Application_Start()
        {
            RegisterMvcComponents();
            RunStartupValidation();
            StartBackgroundServices();
            StartWarmUpInBackground();
        }

        // =====================================================================
        // MVC registration
        // ---------------------------------------------------------------------
        // Lahat ng standard MVC bootstrapping nandito para malinis basahin ang
        // Application_Start.
        // =====================================================================
        private static void RegisterMvcComponents()
        {
            AreaRegistration.RegisterAllAreas();
            FilterConfig.RegisterGlobalFilters(GlobalFilters.Filters);
            RouteConfig.RegisterRoutes(RouteTable.Routes);
            BundleConfig.RegisterBundles(BundleTable.Bundles);
        }

        // =====================================================================
        // Startup validation
        // ---------------------------------------------------------------------
        // Critical config checks bago magsimula ang app.
        //
        // Rule:
        //   - Kapag may tunay na startup validation failure na hindi dapat
        //     i-ignore, puwedeng mag-throw para hindi tumuloy ang app sa bad state.
        // =====================================================================
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
            catch
            {
                // Best effort lang.
                // Huwag nang mag-crash dahil lang hindi nakapag-log sa Event Viewer.
            }

            System.Diagnostics.Trace.TraceError(
                "[Application_Start] STARTUP VALIDATION FAILED: " + ex);
        }

        // =====================================================================
        // Background services
        // ---------------------------------------------------------------------
        // Mga recurring/singleton services na hiwalay sa warm-up pipeline.
        // =====================================================================
        private static void StartBackgroundServices()
        {
            TempFileCleanupTask.Start();
        }

        // =====================================================================
        // Warm-up orchestration
        // ---------------------------------------------------------------------
        // Bakit background?
        //   Para hindi ma-block ang IIS startup thread. Kapag nag-block dito,
        //   puwedeng bumagal ang app startup at magka-health check issue.
        //
        // Ano ang wina-warm-up?
        //   1. Dlib pool
        //   2. ONNX liveness model
        //   3. Employee face index
        //   4. Visitor face index
        //
        // Note:
        //   Ang warm-up ay best-effort. Kapag may isang step na pumalya,
        //   hindi ibig sabihin patay na agad ang buong app. Depende sa failure,
        //   puwedeng gumana pa rin ang app pero may first-request penalty o
        //   limited functionality.
        // =====================================================================
        private static void StartWarmUpInBackground()
        {
            _warmUpState = WarmUpStateRunning;
            _warmUpMessage = WarmUpMessageRunning;

            var warmUpTask = Task.Run(() => RunWarmUpPipeline());

            Task.Run(() => ObserveWarmUpTask(warmUpTask));
        }

        private static void RunWarmUpPipeline()
        {
            WarmUpDlibPool();
            WarmUpOnnxLiveness();
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

        // =====================================================================
        // Warm-up step 1: Dlib pool
        // ---------------------------------------------------------------------
        // Ito ang karaniwang pinaka-mabigat na init step.
        //
        // Bakit mahalaga?
        //   Para ang mga unang biometric requests ay hindi na gagawa ng cold
        //   initialization habang may actual user request.
        //
        // Failure impact:
        //   Non-fatal sa startup, pero posibleng mag-cause ng pool timeout,
        //   delayed scans, or biometric failures hanggang maayos ang root cause.
        // =====================================================================
        private static void WarmUpDlibPool()
        {
            try
            {
                DlibBiometrics.InitializePool();

                System.Diagnostics.Trace.TraceInformation(
                    "[Application_Start] Dlib instance pool loaded successfully.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError(
                    "[Application_Start] WARNING: Failed to initialize Dlib pool: " +
                    ex.Message);
            }
        }

        // =====================================================================
        // Warm-up step 2: ONNX liveness
        // ---------------------------------------------------------------------
        // Niloload nito ang ONNX model sa memory para hindi cold-load sa unang
        // actual liveness check.
        //
        // Failure impact:
        //   Non-fatal sa startup, pero liveness-related requests puwedeng bumagal
        //   o pumalya depende sa fallback behavior ng app.
        // =====================================================================
        private static void WarmUpOnnxLiveness()
        {
            try
            {
                OnnxLiveness.WarmUp();

                System.Diagnostics.Trace.TraceInformation(
                    "[Application_Start] ONNX liveness model loaded successfully.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError(
                    "[Application_Start] WARNING: Failed to load ONNX liveness model: " +
                    ex.Message);
            }
        }

        // =====================================================================
        // Warm-up step 3: Employee face index
        // ---------------------------------------------------------------------
        // Pre-load ng employee face encodings mula DB papunta sa memory cache.
        //
        // Bakit mahalaga?
        //   Kapag hindi ito naka-warm-up, ang unang employee scan pagkatapos ng
        //   app recycle ay puwedeng mag-trigger ng rebuild sa request path.
        //
        // Failure impact:
        //   Non-fatal. Magre-rebuild na lang sa unang scan, pero may delay.
        // =====================================================================
        private static void WarmUpEmployeeFaceIndex()
        {
            try
            {
                using (var db = new FaceAttendDBEntities())
                {
                    EmployeeFaceIndex.Rebuild(db);
                }

                System.Diagnostics.Trace.TraceInformation(
                    "[Application_Start] Employee face index preloaded successfully.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "[Application_Start] Employee face index preload failed: " +
                    ex.Message + " | Fallback: rebuild on first employee scan.");
            }

            // ULTRA-FAST: Pre-load all faces into RAM for instant recognition
            try
            {
                FaceAttend.Services.Biometrics.FastFaceMatcher.Initialize();
                System.Diagnostics.Trace.TraceInformation(
                    "[Application_Start] ULTRA-FAST face matcher loaded. " +
                    FaceAttend.Services.Biometrics.FastFaceMatcher.GetStats()?.ToString());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "[Application_Start] Fast face matcher failed: " + ex.Message);
            }
        }

        // =====================================================================
        // Warm-up step 4: Visitor face index
        // ---------------------------------------------------------------------
        // Pre-load ng visitor face encodings mula DB papunta sa memory cache.
        //
        // Bakit mahalaga?
        //   Ito ang dating madaling ma-miss. Kapag hindi ito na-preload,
        //   ang unang visitor scan pagkatapos ng recycle puwedeng mag-cold rebuild
        //   at magpakita ng delay bago lumabas ang result/modal.
        //
        // Failure impact:
        //   Non-fatal. Magre-rebuild na lang sa unang visitor scan, pero may delay.
        // =====================================================================
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

        // =====================================================================
        // Global error routing
        // ---------------------------------------------------------------------
        // Layunin:
        //   I-route ang unhandled errors sa tamang error controller/view
        //   imbes na hayaan si IIS/ASP.NET na magpakita ng generic page.
        //
        // Important behavior:
        //   - Local debug request: huwag pigilan ang default yellow screen
        //   - Iwasan ang infinite loop kapag error page mismo ang nag-error
        //   - Separate admin and non-admin error pages
        // =====================================================================
        // =====================================================================
        // Global error handler
        // ---------------------------------------------------------------------
        // Layunin: I-handle ang mga unhandled exceptions at i-log ang mga ito
        // para madaling i-debug kapag may problema.
        // 
        // GINAGAWA:
        //   1. Kunin ang last error mula sa Server
        //   2. I-log ang error sa Trace (makikita sa logs)
        //   3. Kung local debugging, hayaan ang Yellow Screen of Death
        //   4. Kung production at hindi Admin area, redirect sa error page
        // 
        // TANDAAN: Admin area may sariling error handler (HandleAdminErrorAttribute)
        // para hindi maapektuhan ang buong admin kapag may error sa isang page.
        // =====================================================================
        protected void Application_Error()
        {
            var ex = Server.GetLastError();
            var httpEx = ex as HttpException;
            var statusCode = httpEx?.GetHttpCode() ?? 500;
            
            // I-log ang error para makita sa server logs
            System.Diagnostics.Trace.TraceError($"[Application_Error] {statusCode}: {ex?.Message}");
            
            // Kung local debugging at enabled ang debug mode, 
            // hayaan ang default ASP.NET error page (Yellow Screen of Death)
            // para makita ng developer ang full error details
            if (HttpContext.Current?.IsDebuggingEnabled == true && 
                Request.IsLocal)
            {
                return; // Hayaan ang default error handling
            }

            // CHECK: Kung Admin area, hayaan ang HandleAdminErrorAttribute ang bahala
            // para per-page lang ang error handling, hindi buong admin
            var routeData = HttpContext.Current?.Request?.RequestContext?.RouteData;
            var area = routeData?.DataTokens["area"] as string;
            if (!string.IsNullOrEmpty(area) && area.Equals("Admin", StringComparison.OrdinalIgnoreCase))
            {
                // Admin area has its own error handling via HandleAdminErrorAttribute
                // Let it handle the exception instead of redirecting
                return;
            }
            
            // Clear the error para hindi na mag-propagate pa
            Server.ClearError();
            Response.TrySkipIisCustomErrors = true;
            
            // I-redirect sa tamang error page base sa status code
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
            
            try
            {
                Response.Redirect($"~/Error/{action}");
            }
            catch
            {
                // Kung nag-fail ang redirect, hayaan na lang
                // hindi na natin pwedeng i-redirect ulit dito
            }
        }
        // =====================================================================
        // EndRequest 404 handling
        // ---------------------------------------------------------------------
        // Bakit kailangan pa ito?
        //   May ilang 404 cases na hindi dumadaan sa normal exception flow pero
        //   naka-set lang ang Response.StatusCode = 404.
        //
        // Layunin:
        //   Siguraduhin na pati ganitong 404 ay napupunta sa custom error page.
        // =====================================================================
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

        // =====================================================================
        // Shutdown cleanup
        // ---------------------------------------------------------------------
        // Best-effort cleanup lang ito bago i-unload ang app domain.
        //
        // Goal:
        //   1. Stop cleanup worker
        //   2. Dispose Dlib pool
        //   3. Dispose ONNX session
        //
        // Note:
        //   Huwag mag-throw dito. Shutdown path ito.
        // =====================================================================
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
                DlibBiometrics.DisposePool();
            }
            catch
            {
                // Best effort cleanup only.
            }

            try
            {
                OnnxLiveness.DisposeSession();
            }
            catch
            {
                // Best effort cleanup only.
            }
        }

        // =====================================================================
        // Critical configuration validation
        // ---------------------------------------------------------------------
        // Dito chine-check ang configs na mataas ang impact sa admin access
        // at deployment correctness.
        //
        // Important:
        //   Ang current implementation ay nagla-log ng critical/warning signals.
        //   Hindi pa ito hard-fail sa lahat ng cases.
        // =====================================================================
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
                ?? AppSettings.GetString("Admin:AllowedIpRanges", string.Empty)
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

        // =====================================================================
        // Error handling helpers
        // =====================================================================
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