using System.Web.Mvc;
using System.Web.Routing;

namespace FaceAttend
{
    public class RouteConfig
    {
        public static void RegisterRoutes(RouteCollection routes)
        {
            routes.IgnoreRoute("{resource}.axd/{*pathInfo}");

            // Attribute routes first — covers MobileEnrollmentController and MobilePortalController
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
