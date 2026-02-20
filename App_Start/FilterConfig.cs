using System.Web.Mvc;

namespace FaceAttend
{
    public class FilterConfig
    {
        public static void RegisterGlobalFilters(GlobalFilterCollection filters)
        {
            filters.Add(new HandleErrorAttribute());

            // Adds X-Content-Type-Options, X-Frame-Options, X-XSS-Protection,
            // Referrer-Policy and no-cache headers to every response.
            filters.Add(new Filters.SecurityHeadersAttribute());

            // Uncomment to force HTTPS in production (after enabling SSL in IIS/web.config).
            // filters.Add(new RequireHttpsAttribute());
        }
    }
}
