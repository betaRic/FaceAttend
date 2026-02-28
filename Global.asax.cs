using System.Web.Mvc;
using System.Web.Optimization;
using System.Web.Routing;
using System.Threading.Tasks;
using System;
using System.Web;
using FaceAttend.Services.Biometrics;  // P1-F2: required for DlibBiometrics + OnnxLiveness

namespace FaceAttend
{
    public class MvcApplication : System.Web.HttpApplication
    {
        protected void Application_Start()
        {
            AreaRegistration.RegisterAllAreas();
            FilterConfig.RegisterGlobalFilters(GlobalFilters.Filters);
            RouteConfig.RegisterRoutes(RouteTable.Routes);
            BundleConfig.RegisterBundles(BundleTable.Bundles);

            // Warm up Dlib + ONNX so the first scan is not slow.
            Task.Run(() =>
            {
                try { new DlibBiometrics(); } catch { }
                try { OnnxLiveness.WarmUp(); } catch { }
            });
        }

        // -------------------------------------------------------------------
        // Custom error routing
        // -------------------------------------------------------------------

        protected void Application_Error()
        {
            // During local debugging, keep the default error page.
            // This prevents custom errors from hiding stack traces while you fix issues.
            if (Context != null && Context.IsDebuggingEnabled && Request != null && Request.IsLocal)
                return;

            var ex = Server.GetLastError();
            if (ex == null) return;

            // Avoid loops if the error is thrown while rendering an error page.
            var rawUrl = (Request?.RawUrl ?? "");
            if (rawUrl.StartsWith(VirtualPathUtility.ToAbsolute("~/Error"), StringComparison.OrdinalIgnoreCase) ||
                rawUrl.StartsWith(VirtualPathUtility.ToAbsolute("~/Admin/Error"), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            int code = 500;
            if (ex is HttpException httpEx)
                code = httpEx.GetHttpCode();

            string action = MapStatusToAction(code);
            bool isAdmin = IsAdminRequest(Request);
            string target = isAdmin ? $"~/Admin/Error/{action}" : $"~/Error/{action}";

            // Clear the error and rewrite the request to our error controller.
            Server.ClearError();
            Response.Clear();
            Response.TrySkipIisCustomErrors = true;
            Response.StatusCode = code;

            // Mark to prevent EndRequest from rewriting again.
            Context.Items["__fa_error_handled"] = true;

            Server.TransferRequest(VirtualPathUtility.ToAbsolute(target), true);
        }

        protected void Application_EndRequest()
        {
            // Handle "no route" 404s and some IIS-generated 404s.
            // During local debugging, do not interfere.
            if (Context == null || Request == null || Response == null) return;
            if (Context.IsDebuggingEnabled && Request.IsLocal) return;

            // Skip if already handled in Application_Error.
            if (Context.Items["__fa_error_handled"] != null) return;

            if (Response.StatusCode == 404)
            {
                var rawUrl = (Request.RawUrl ?? "");
                if (rawUrl.StartsWith(VirtualPathUtility.ToAbsolute("~/Error"), StringComparison.OrdinalIgnoreCase) ||
                    rawUrl.StartsWith(VirtualPathUtility.ToAbsolute("~/Admin/Error"), StringComparison.OrdinalIgnoreCase))
                    return;

                bool isAdmin = IsAdminRequest(Request);
                string target = isAdmin ? "~/Admin/Error/NotFound" : "~/Error/NotFound";

                Response.TrySkipIisCustomErrors = true;
                Context.Items["__fa_error_handled"] = true;

                Server.TransferRequest(VirtualPathUtility.ToAbsolute(target), true);
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
                case 401: return "Forbidden"; // treat as forbidden for UI
                case 403: return "Forbidden";
                case 404: return "NotFound";
                case 429: return "TooManyRequests";
                case 503: return "Unavailable";
                default:  return "Index";
            }
        }

        // P1-F2: Dispose unmanaged Dlib and ONNX resources on IIS app pool recycle.
        // Both DisposeInstance() and DisposeSession() are fully implemented in their
        // respective classes but were never called — this was a resource leak on every
        // recycle. Application_End is the correct place: it is called once per app
        // domain shutdown, before the process terminates.
        protected void Application_End()
        {
            DlibBiometrics.DisposeInstance();
            OnnxLiveness.DisposeSession();
        }
    }
}
