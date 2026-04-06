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
            // MOBILE REGISTRATION ROUTES — kept at /MobileRegistration/* URLs
            // Dispatched to MobileEnrollmentController or MobilePortalController
            // depending on responsibility.
            // =====================================================================

            // Enrollment actions → MobileEnrollmentController
            routes.MapRoute(
                name: "MobileRegistration_Enroll",
                url: "MobileRegistration/Enroll",
                defaults: new { controller = "MobileEnrollment", action = "Enroll", area = "" },
                namespaces: new[] { "FaceAttend.Controllers.Mobile" }
            );
            routes.MapRoute(
                name: "MobileRegistration_ScanFrame",
                url: "MobileRegistration/ScanFrame",
                defaults: new { controller = "MobileEnrollment", action = "ScanFrame", area = "" },
                namespaces: new[] { "FaceAttend.Controllers.Mobile" }
            );
            routes.MapRoute(
                name: "MobileRegistration_SubmitEnrollment",
                url: "MobileRegistration/SubmitEnrollment",
                defaults: new { controller = "MobileEnrollment", action = "SubmitEnrollment", area = "" },
                namespaces: new[] { "FaceAttend.Controllers.Mobile" }
            );
            routes.MapRoute(
                name: "MobileRegistration_Identify",
                url: "MobileRegistration/Identify",
                defaults: new { controller = "MobileEnrollment", action = "Identify", area = "" },
                namespaces: new[] { "FaceAttend.Controllers.Mobile" }
            );
            routes.MapRoute(
                name: "MobileRegistration_IdentifyEmployee",
                url: "MobileRegistration/IdentifyEmployee",
                defaults: new { controller = "MobileEnrollment", action = "IdentifyEmployee", area = "" },
                namespaces: new[] { "FaceAttend.Controllers.Mobile" }
            );

            // Portal actions → MobilePortalController
            routes.MapRoute(
                name: "MobileRegistration_Index",
                url: "MobileRegistration",
                defaults: new { controller = "MobilePortal", action = "Index", area = "" },
                namespaces: new[] { "FaceAttend.Controllers.Mobile" }
            );
            routes.MapRoute(
                name: "MobileRegistration_Device",
                url: "MobileRegistration/Device",
                defaults: new { controller = "MobilePortal", action = "Device", area = "" },
                namespaces: new[] { "FaceAttend.Controllers.Mobile" }
            );
            routes.MapRoute(
                name: "MobileRegistration_RegisterDevice",
                url: "MobileRegistration/RegisterDevice",
                defaults: new { controller = "MobilePortal", action = "RegisterDevice", area = "" },
                namespaces: new[] { "FaceAttend.Controllers.Mobile" }
            );
            routes.MapRoute(
                name: "MobileRegistration_Success",
                url: "MobileRegistration/Success",
                defaults: new { controller = "MobilePortal", action = "Success", area = "" },
                namespaces: new[] { "FaceAttend.Controllers.Mobile" }
            );
            routes.MapRoute(
                name: "MobileRegistration_CheckStatus",
                url: "MobileRegistration/CheckStatus",
                defaults: new { controller = "MobilePortal", action = "CheckStatus", area = "" },
                namespaces: new[] { "FaceAttend.Controllers.Mobile" }
            );
            routes.MapRoute(
                name: "MobileRegistration_Employee",
                url: "MobileRegistration/Employee",
                defaults: new { controller = "MobilePortal", action = "Employee", area = "" },
                namespaces: new[] { "FaceAttend.Controllers.Mobile" }
            );
            routes.MapRoute(
                name: "MobileRegistration_ExportAttendance",
                url: "MobileRegistration/ExportAttendance",
                defaults: new { controller = "MobilePortal", action = "ExportAttendance", area = "" },
                namespaces: new[] { "FaceAttend.Controllers.Mobile" }
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
