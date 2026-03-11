using System.Web.Mvc;
using FaceAttend.Areas.Admin.Filters;

namespace FaceAttend.Areas.Admin
{
    public class AdminAreaRegistration : AreaRegistration
    {
        public override string AreaName => "Admin";

        public override void RegisterArea(AreaRegistrationContext context)
        {
            // Register admin-specific error handler
            // This catches exceptions per-page so one broken page doesn't break the whole admin
            GlobalFilters.Filters.Add(new HandleAdminErrorAttribute());

            var route = context.MapRoute(
                "Admin_default",
                "Admin/{controller}/{action}/{id}",
                new { controller = "Dashboard", action = "Index", id = UrlParameter.Optional },
                namespaces: new[] { "FaceAttend.Areas.Admin.Controllers" }
            );

            route.DataTokens["UseNamespaceFallback"] = false;
        }
    }
}
