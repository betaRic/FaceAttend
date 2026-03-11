using System.Web.Mvc;

namespace FaceAttend.Areas.Admin
{
    public class AdminAreaRegistration : AreaRegistration
    {
        public override string AreaName => "Admin";

        public override void RegisterArea(AreaRegistrationContext context)
        {
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
