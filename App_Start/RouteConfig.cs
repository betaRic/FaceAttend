using System.Web.Mvc;
using System.Web.Routing;

namespace FaceAttend
{
    public class RouteConfig
    {
        public static void RegisterRoutes(RouteCollection routes)
        {
            routes.IgnoreRoute("{resource}.axd/{*pathInfo}");

            // =====================================================================
            // EXPLICIT ROUTES FOR MobileRegistration (ROOT AREA - NOT ADMIN)
            // =====================================================================
            // These routes MUST be registered BEFORE MapMvcAttributeRoutes
            // to ensure they take precedence and don't get matched to Admin area
            
            // MobileRegistration Index
            routes.MapRoute(
                name: "MobileRegistration_Index",
                url: "MobileRegistration",
                defaults: new { controller = "MobileRegistration", action = "Index", area = "" },
                namespaces: new[] { "FaceAttend.Controllers" }
            );

            // MobileRegistration Identify (GET) - For existing employee flow
            routes.MapRoute(
                name: "MobileRegistration_Identify",
                url: "MobileRegistration/Identify",
                defaults: new { controller = "MobileRegistration", action = "Identify", area = "" },
                namespaces: new[] { "FaceAttend.Controllers" }
            );

            // MobileRegistration IdentifyEmployee (POST) - AJAX endpoint
            routes.MapRoute(
                name: "MobileRegistration_IdentifyEmployee",
                url: "MobileRegistration/IdentifyEmployee",
                defaults: new { controller = "MobileRegistration", action = "IdentifyEmployee", area = "" },
                namespaces: new[] { "FaceAttend.Controllers" }
            );

            // MobileRegistration Enroll (GET) - For new employee flow
            routes.MapRoute(
                name: "MobileRegistration_Enroll",
                url: "MobileRegistration/Enroll",
                defaults: new { controller = "MobileRegistration", action = "Enroll", area = "" },
                namespaces: new[] { "FaceAttend.Controllers" }
            );

            // MobileRegistration ScanFrame (POST) - AJAX endpoint
            routes.MapRoute(
                name: "MobileRegistration_ScanFrame",
                url: "MobileRegistration/ScanFrame",
                defaults: new { controller = "MobileRegistration", action = "ScanFrame", area = "" },
                namespaces: new[] { "FaceAttend.Controllers" }
            );

            // MobileRegistration SubmitEnrollment (POST)
            routes.MapRoute(
                name: "MobileRegistration_SubmitEnrollment",
                url: "MobileRegistration/SubmitEnrollment",
                defaults: new { controller = "MobileRegistration", action = "SubmitEnrollment", area = "" },
                namespaces: new[] { "FaceAttend.Controllers" }
            );

            // MobileRegistration Device (GET)
            routes.MapRoute(
                name: "MobileRegistration_Device",
                url: "MobileRegistration/Device",
                defaults: new { controller = "MobileRegistration", action = "Device", area = "" },
                namespaces: new[] { "FaceAttend.Controllers" }
            );

            // MobileRegistration RegisterDevice (POST)
            routes.MapRoute(
                name: "MobileRegistration_RegisterDevice",
                url: "MobileRegistration/RegisterDevice",
                defaults: new { controller = "MobileRegistration", action = "RegisterDevice", area = "" },
                namespaces: new[] { "FaceAttend.Controllers" }
            );

            // MobileRegistration Success (GET)
            routes.MapRoute(
                name: "MobileRegistration_Success",
                url: "MobileRegistration/Success",
                defaults: new { controller = "MobileRegistration", action = "Success", area = "" },
                namespaces: new[] { "FaceAttend.Controllers" }
            );

            // MobileRegistration CheckStatus (GET) - AJAX endpoint
            routes.MapRoute(
                name: "MobileRegistration_CheckStatus",
                url: "MobileRegistration/CheckStatus",
                defaults: new { controller = "MobileRegistration", action = "CheckStatus", area = "" },
                namespaces: new[] { "FaceAttend.Controllers" }
            );

            // Generic MobileRegistration route (fallback for any other actions)
            routes.MapRoute(
                name: "MobileRegistration_Generic",
                url: "MobileRegistration/{action}/{id}",
                defaults: new { controller = "MobileRegistration", action = "Index", id = UrlParameter.Optional, area = "" },
                namespaces: new[] { "FaceAttend.Controllers" }
            );

            // NOW register attribute routes (for other controllers that use them)
            routes.MapMvcAttributeRoutes();

            // Default route for root controllers (Kiosk, etc.)
            routes.MapRoute(
                name: "Default",
                url: "{controller}/{action}/{id}",
                defaults: new { controller = "Kiosk", action = "Index", id = UrlParameter.Optional, area = "" },
                namespaces: new[] { "FaceAttend.Controllers" }
            );
        }
    }
}
