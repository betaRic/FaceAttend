using System;
using System.Web;
using System.Web.Mvc;

namespace FaceAttend.Areas.Admin.Filters
{
    /// <summary>
    /// Admin area-specific error handler.
    /// 
    /// PRINCIPLE: Errors on one page should NOT break the entire admin area.
    /// This filter catches exceptions and shows a friendly error page WITH
    /// developer details when running locally.
    /// 
    /// DEVELOPER FEATURES:
    /// - Local requests (IsLocal) see full exception details
    /// - Request ID is generated and shown for log correlation
    /// - Full stack trace is logged to Trace
    /// </summary>
    public class HandleAdminErrorAttribute : HandleErrorAttribute
    {
        public HandleAdminErrorAttribute()
        {
            // Only handle exceptions, not 404s
            ExceptionType = typeof(Exception);
        }

        public override void OnException(ExceptionContext filterContext)
        {
            if (filterContext == null)
                throw new ArgumentNullException(nameof(filterContext));

            // Don't handle if already handled
            if (filterContext.ExceptionHandled)
                return;

            var ex = filterContext.Exception;
            var httpContext = filterContext.HttpContext;

            // Generate Request ID for this error
            var requestId = GenerateRequestId(httpContext);

            // Log the full error with Request ID
            LogError(ex, requestId, filterContext);

            // Set exception as handled so it doesn't bubble up
            filterContext.ExceptionHandled = true;

            // Return appropriate result based on request type
            if (filterContext.HttpContext.Request.IsAjaxRequest())
            {
                filterContext.Result = CreateAjaxErrorResult(ex, requestId, httpContext);
            }
            else
            {
                filterContext.Result = CreateViewResult(ex, requestId, httpContext);
            }

            // Ensure proper status code
            filterContext.HttpContext.Response.StatusCode = 500;
            filterContext.HttpContext.Response.TrySkipIisCustomErrors = true;
        }

        private ActionResult CreateAjaxErrorResult(Exception ex, string requestId, HttpContextBase httpContext)
        {
            var isDeveloper = IsDeveloperRequest(httpContext);

            var result = new JsonResult
            {
                Data = new
                {
                    ok = false,
                    error = "An error occurred while processing your request.",
                    requestId = requestId,
                    // Developer-only details
                    developerInfo = isDeveloper ? new
                    {
                        exception = ex.GetType().Name,
                        message = ex.Message,
                        stackTrace = ex.StackTrace,
                        innerException = ex.InnerException?.Message
                    } : null
                },
                JsonRequestBehavior = JsonRequestBehavior.AllowGet
            };

            return result;
        }

        private ActionResult CreateViewResult(Exception ex, string requestId, HttpContextBase httpContext)
        {
            var isDeveloper = IsDeveloperRequest(httpContext);

            // Prepare view data
            var viewData = new ViewDataDictionary
            {
                ["StatusCode"] = 500,
                ["TitleText"] = "Something went wrong",
                ["MessageText"] = "We couldn't process your request. Please try again.",
                ["RequestId"] = requestId,
                ["IsDeveloper"] = isDeveloper
            };

            // Add developer-only details
            if (isDeveloper)
            {
                viewData["ExceptionType"] = ex.GetType().FullName;
                viewData["ExceptionMessage"] = ex.Message;
                viewData["StackTrace"] = ex.StackTrace;
                viewData["InnerException"] = ex.InnerException?.ToString();
            }

            return new ViewResult
            {
                ViewName = "ErrorPage",
                MasterName = "_AdminLayout",
                ViewData = viewData
            };
        }

        private bool IsDeveloperRequest(HttpContextBase httpContext)
        {
            if (httpContext == null)
                return false;

            // Local requests are always developers
            if (httpContext.Request.IsLocal)
                return true;

            // Check for debug cookie (set by developers who know the secret)
            var debugCookie = httpContext.Request.Cookies["__fa_debug"];
            if (debugCookie != null && debugCookie.Value == "1")
                return true;

            return false;
        }

        private string GenerateRequestId(HttpContextBase httpContext)
        {
            // Check if we already have a request ID
            var existing = httpContext?.Items["RequestId"] as string;
            if (!string.IsNullOrWhiteSpace(existing))
                return existing;

            // Generate new Request ID
            var id = Guid.NewGuid().ToString("N").Substring(0, 16).ToUpper();
            if (httpContext != null)
                httpContext.Items["RequestId"] = id;

            return id;
        }

        private void LogError(Exception ex, string requestId, ExceptionContext context)
        {
            try
            {
                var controller = context.RouteData?.Values["controller"]?.ToString() ?? "Unknown";
                var action = context.RouteData?.Values["action"]?.ToString() ?? "Unknown";
                var url = context.HttpContext?.Request?.Url?.ToString() ?? "Unknown";
                var user = context.HttpContext?.User?.Identity?.Name ?? "Anonymous";

                var logMessage = $@"
[ADMIN ERROR] Request ID: {requestId}
  URL: {url}
  Controller: {controller}
  Action: {action}
  User: {user}
  Exception: {ex.GetType().FullName}
  Message: {ex.Message}
  StackTrace:
{ex.StackTrace}
";

                if (ex.InnerException != null)
                {
                    logMessage += $@"
  Inner Exception: {ex.InnerException.GetType().FullName}
  Inner Message: {ex.InnerException.Message}
";
                }

                System.Diagnostics.Trace.TraceError(logMessage);
            }
            catch
            {
                // Best effort logging - don't throw if logging fails
                System.Diagnostics.Trace.TraceError($"[ADMIN ERROR] Request ID: {requestId} - Error logging failed");
            }
        }
    }
}
