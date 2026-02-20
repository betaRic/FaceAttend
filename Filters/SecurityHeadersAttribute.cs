using System.Web.Mvc;
using FaceAttend.Services;

namespace FaceAttend.Filters
{
    /// <summary>
    /// Adds common HTTP security headers to every response.
    /// Registered globally in FilterConfig.RegisterGlobalFilters().
    /// </summary>
    public class SecurityHeadersAttribute : ActionFilterAttribute
    {
        public override void OnResultExecuting(ResultExecutingContext filterContext)
        {
            var h = filterContext.HttpContext.Response.Headers;

            // Prevent MIME-type sniffing (e.g. serving a text file as executable).
            h["X-Content-Type-Options"] = "nosniff";

            // Prevent the page from being embedded in an iframe on another site (clickjacking).
            h["X-Frame-Options"] = "SAMEORIGIN";

            // Legacy XSS filter for older browsers. Modern browsers use CSP instead.
            h["X-XSS-Protection"] = "1; mode=block";

            // Control how much referrer information is sent.
            h["Referrer-Policy"] = "strict-origin-when-cross-origin";

            // CSP: start in Report-Only mode by default to avoid breaking pages.
            // When ready, set Security:CspReportOnly=false.
            var csp = AppSettings.GetString(
                "Security:Csp",
                "default-src 'self'; base-uri 'self'; object-src 'none'; frame-ancestors 'self'; img-src 'self' data:; " +
                "style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline' 'unsafe-eval'; connect-src 'self'");

            var cspReportOnly = AppSettings.GetBool("Security:CspReportOnly", true);
            h[cspReportOnly ? "Content-Security-Policy-Report-Only" : "Content-Security-Policy"] = csp;

            // HSTS: only when HTTPS is enforced.
            if (AppSettings.GetBool("Security:HstsEnabled", false) &&
                filterContext.HttpContext.Request.IsSecureConnection)
            {
                h["Strict-Transport-Security"] = AppSettings.GetString(
                    "Security:HstsValue",
                    "max-age=31536000; includeSubDomains");
            }

            // Prevent caching of sensitive admin pages.
            // Only apply to Admin area responses.
            var routeData = filterContext.RouteData;
            var area = routeData?.DataTokens?["area"]?.ToString();
            if (!string.IsNullOrEmpty(area) &&
                area.Equals("Admin", System.StringComparison.OrdinalIgnoreCase))
            {
                var response = filterContext.HttpContext.Response;
                response.Cache.SetCacheability(System.Web.HttpCacheability.NoCache);
                response.Cache.SetNoStore();
                response.Cache.SetExpires(System.DateTime.UtcNow.AddDays(-1));
            }

            base.OnResultExecuting(filterContext);
        }
    }
}
