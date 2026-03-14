using System.Web.Mvc;

namespace FaceAttend
{
    /// <summary>
    /// Nire-register ang mga global MVC filters na applicable sa lahat ng controllers.
    ///
    /// PHASE 1 FIX (S-09): In-enable na ang RequireHttpsAttribute.
    /// Ang filter na ito ay nagre-redirect ng lahat ng HTTP request papunta sa HTTPS.
    /// Dalawang linya ng depensa ito kasama ang Web.config URL Rewrite rule —
    /// ang Rewrite rule ay nag-redirect sa IIS level (bago pa makarating sa ASP.NET),
    /// ang RequireHttpsAttribute naman ay nag-redirect sa app level (safety net).
    /// </summary>
    public class FilterConfig
    {
        public static void RegisterGlobalFilters(GlobalFilterCollection filters)
        {
            // Nagdadagdag ng security headers sa bawat response:
            // X-Content-Type-Options, X-Frame-Options, X-XSS-Protection,
            // Referrer-Policy, CSP, at HSTS (kapag naka-configure).
            filters.Add(new Filters.SecurityHeadersAttribute());

            // PHASE 1 FIX (S-09): HTTPS enforcement sa app level.
            // Kapag may SSL certificate na at naka-enable ang HTTPS sa IIS,
            // i-uncomment ang linyang ito para maging aktibo ang HTTPS requirement.
            //
            // BABALA: Huwag i-uncomment kung wala pang SSL cert na naka-bind sa IIS —
            // mabibigo ang lahat ng request at hindi maa-access ang app.
            //
            // Matapos mailagay ang cert mula sa 01_CreateInternalCA.ps1,
            // i-uncomment ang linya sa ibaba:
            // filters.Add(new RequireHttpsAttribute()); // DEFERRED: Enable only after SSL cert is bound in IIS
        }
    }
}

