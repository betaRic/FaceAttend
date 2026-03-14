using System;
using System.Web;
using System.Web.Mvc;

namespace FaceAttend.Areas.Admin.Filters
{
    /// <summary>
    /// Admin area-specific error handler.
    /// Catches exceptions per-page so one broken page does not break the entire admin.
    /// Breaks redirect loops by detecting a second failure and rendering a static recovery page.
    /// </summary>
    public class HandleAdminErrorAttribute : HandleErrorAttribute
    {
        public HandleAdminErrorAttribute()
        {
            ExceptionType = typeof(Exception);
        }

        public override void OnException(ExceptionContext filterContext)
        {
            if (filterContext == null)
                throw new ArgumentNullException(nameof(filterContext));

            if (filterContext.ExceptionHandled)
                return;

            // Only intercept Admin area requests
            var area = filterContext.RouteData?.DataTokens["area"] as string;
            if (string.IsNullOrEmpty(area) || !area.Equals("Admin", StringComparison.OrdinalIgnoreCase))
                return;

            var ex = filterContext.Exception;
            var httpContext = filterContext.HttpContext;
            var requestId = GenerateRequestId(httpContext);

            LogError(ex, requestId, filterContext);

            filterContext.ExceptionHandled = true;

            filterContext.Result = filterContext.HttpContext.Request.IsAjaxRequest()
                ? CreateAjaxErrorResult(ex, requestId, httpContext)
                : CreateViewResult(ex, requestId, httpContext, filterContext);

            filterContext.HttpContext.Response.StatusCode = 500;
            filterContext.HttpContext.Response.TrySkipIisCustomErrors = true;
        }

        // ── Ajax errors ──────────────────────────────────────────────────────

        private ActionResult CreateAjaxErrorResult(Exception ex, string requestId, HttpContextBase httpContext)
        {
            var isDeveloper = IsDeveloperRequest(httpContext);

            return new JsonResult
            {
                Data = new
                {
                    ok = false,
                    error = "An error occurred while processing your request.",
                    requestId,
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
        }

        // ── Full-page errors ─────────────────────────────────────────────────

        private ActionResult CreateViewResult(
            Exception ex, string requestId,
            HttpContextBase httpContext, ExceptionContext context)
        {
            // ── Redirect loop guard ───────────────────────────────────────────
            // If TempData already contains an error key OR we already redirected
            // once in this request chain, the target action is ALSO failing.
            // Redirecting again produces ERR_TOO_MANY_REDIRECTS.
            // Break the loop by rendering a static recovery page immediately.
            var ctrl = context.Controller as Controller;
            bool alreadyRedirected =
                ctrl?.TempData?.ContainsKey("Error") == true
                || context.HttpContext?.Items["__adminErrRedirected"] != null;

            if (alreadyRedirected)
            {
                context.HttpContext.Response.StatusCode = 500;
                return new ContentResult
                {
                    Content = BuildRecoveryPage(ex, requestId, IsDeveloperRequest(httpContext)),
                    ContentType = "text/html"
                };
            }

            // Mark so the next request in this chain renders the recovery page instead
            context.HttpContext.Items["__adminErrRedirected"] = true;

            // ── HTTP 401 / 403 — never redirect back, render error page directly ──
            if (ex is HttpException httpEx)
            {
                int code = httpEx.GetHttpCode();
                if (code == 401 || code == 403)
                {
                    return new ViewResult
                    {
                        ViewName = "~/Areas/Admin/Views/Shared/ErrorPage.cshtml",
                        ViewData = context.Controller.ViewData
                    };
                }
            }

            // ── Normal errors — store in TempData and redirect back to same action ──
            var isDeveloper = IsDeveloperRequest(httpContext);
            var controller = context.RouteData?.Values["controller"]?.ToString() ?? "Dashboard";
            var action     = context.RouteData?.Values["action"]?.ToString()     ?? "Index";
            var areaToken  = context.RouteData?.DataTokens["area"]?.ToString()   ?? "";

            var errorMessage = isDeveloper
                ? $"[{ex.GetType().Name}] {ex.Message}"
                : "An error occurred while processing your request. Please try again.";

            var tempData = context.Controller.TempData;
            tempData["Error"]     = errorMessage;
            tempData["RequestId"] = requestId;

            if (isDeveloper)
            {
                tempData["Developer_Exception"]   = ex.GetType().FullName;
                tempData["Developer_Message"]     = ex.Message;
                tempData["Developer_StackTrace"]  = ex.StackTrace;
            }

            var routeValues = new System.Web.Routing.RouteValueDictionary
            {
                { "controller", controller },
                { "action", action }
            };
            if (!string.IsNullOrEmpty(areaToken))
                routeValues["area"] = areaToken;

            return new RedirectToRouteResult(routeValues);
        }

        // ── Recovery page (shown when the redirect target also fails) ─────────

        private static string BuildRecoveryPage(Exception ex, string requestId, bool isDeveloper)
        {
            var devDetail = isDeveloper
                ? "<details open><summary style='cursor:pointer;color:#2563eb;font-size:.8rem'>Developer details (local only)</summary>"
                  + "<pre style='font-size:11px;overflow:auto;margin-top:.5rem;white-space:pre-wrap'>"
                  + System.Web.HttpUtility.HtmlEncode(ex?.ToString())
                  + "</pre></details>"
                : "";

            return "<!DOCTYPE html><html><head><meta charset='utf-8'><title>Admin Error</title>"
                + "<style>"
                + "body{font-family:system-ui,sans-serif;background:#f1f5f9;margin:0;padding:2rem;"
                + "display:flex;align-items:center;justify-content:center;min-height:100vh}"
                + ".card{background:#fff;border-radius:12px;padding:2rem;max-width:620px;width:100%;"
                + "box-shadow:0 4px 24px rgba(0,0,0,.08)}"
                + "h1{font-size:1.1rem;color:#dc2626;margin:0 0 .5rem;display:flex;align-items:center;gap:.4rem}"
                + "p{color:#64748b;margin:0 0 1.25rem;font-size:.875rem;line-height:1.5}"
                + ".actions{display:flex;gap:.5rem;flex-wrap:wrap;margin-bottom:1rem}"
                + "a{display:inline-flex;align-items:center;padding:.5rem 1rem;border-radius:6px;"
                + "font-size:.875rem;text-decoration:none;font-weight:500}"
                + ".btn-primary{background:#2563eb;color:#fff}"
                + ".btn-primary:hover{background:#1d4ed8}"
                + ".btn-secondary{background:#f1f5f9;color:#1e293b;border:1px solid #e2e8f0}"
                + ".btn-secondary:hover{background:#e2e8f0}"
                + "small{color:#94a3b8;font-size:.7rem;display:block;margin-top:.75rem}"
                + "</style></head><body><div class='card'>"
                + "<h1>&#9888; Admin panel encountered an error</h1>"
                + "<p>A page error was caught, but the recovery redirect also failed. "
                + "This usually means a layout file (<code>_AdminLayout.cshtml</code>) or "
                + "a shared view has a Razor compilation error.</p>"
                + "<div class='actions'>"
                + "<a class='btn-primary' href='/Admin'>&#8635;&nbsp;Dashboard</a>"
                + "<a class='btn-secondary' href='/Admin/Dashboard/Index'>Direct link</a>"
                + "</div>"
                + devDetail
                + "<small>Request ID: " + System.Web.HttpUtility.HtmlEncode(requestId) + "</small>"
                + "</div></body></html>";
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private bool IsDeveloperRequest(HttpContextBase httpContext)
        {
            if (httpContext == null) return false;
            if (httpContext.Request.IsLocal) return true;

            var debugCookie = httpContext.Request.Cookies["__fa_debug"];
            return debugCookie != null && debugCookie.Value == "1";
        }

        private string GenerateRequestId(HttpContextBase httpContext)
        {
            var existing = httpContext?.Items["RequestId"] as string;
            if (!string.IsNullOrWhiteSpace(existing)) return existing;

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
                var action     = context.RouteData?.Values["action"]?.ToString()     ?? "Unknown";
                var url        = context.HttpContext?.Request?.Url?.ToString()        ?? "Unknown";

                var msg = $"[ADMIN ERROR] RequestId={requestId} | {controller}/{action} | URL={url}"
                        + $"\n  {ex.GetType().FullName}: {ex.Message}"
                        + $"\n  {ex.StackTrace}";

                if (ex.InnerException != null)
                    msg += $"\n  Inner: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}";

                System.Diagnostics.Trace.TraceError(msg);
            }
            catch
            {
                System.Diagnostics.Trace.TraceError(
                    $"[ADMIN ERROR] RequestId={requestId} — logging failed");
            }
        }
    }
}