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

            // FIX: Only handle exceptions from Admin area controllers
            // This filter is registered globally but should only affect Admin controllers
            var area = filterContext.RouteData?.DataTokens["area"] as string;
            if (string.IsNullOrEmpty(area) || !area.Equals("Admin", StringComparison.OrdinalIgnoreCase))
            {
                // Not an Admin area request - let the default error handling deal with it
                return;
            }

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
                filterContext.Result = CreateViewResult(ex, requestId, httpContext, filterContext);
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

        private ActionResult CreateViewResult(Exception ex, string requestId, HttpContextBase httpContext, ExceptionContext context)
        {
            var isDeveloper = IsDeveloperRequest(httpContext);
            var controller = context.RouteData?.Values["controller"]?.ToString() ?? "Dashboard";
            var action = context.RouteData?.Values["action"]?.ToString() ?? "Index";
            
            // FIX: Preserve the original area instead of hardcoding "Admin"
            // This filter is registered globally, so it can catch exceptions from any area
            var area = context.RouteData?.DataTokens["area"]?.ToString() ?? "";

            // Build error message for end users
            var errorMessage = "An error occurred while processing your request. Please try again.";
            
            // For developer requests, include more details in the error
            if (isDeveloper)
            {
                errorMessage = $"[{ex.GetType().Name}] {ex.Message}";
            }

            // Store error in TempData for inline display
            var tempData = context.Controller.TempData;
            tempData["Error"] = errorMessage;
            tempData["RequestId"] = requestId;
            
            // For developer, also store full details
            if (isDeveloper)
            {
                tempData["Developer_Exception"] = ex.GetType().FullName;
                tempData["Developer_Message"] = ex.Message;
                tempData["Developer_StackTrace"] = ex.StackTrace;
            }

            // Redirect back to the same action to show the error inline
            // FIX: Use the original area from RouteData instead of hardcoding "Admin"
            var routeValues = new System.Web.Routing.RouteValueDictionary
            {
                { "controller", controller },
                { "action", action }
            };
            
            // Only add area if it's not empty
            if (!string.IsNullOrEmpty(area))
            {
                routeValues["area"] = area;
            }
            
            return new RedirectToRouteResult(routeValues);
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
