using System.Linq;
using System.Web.Optimization;

namespace FaceAttend
{
    public class BundleConfig
    {
        public static void RegisterBundles(BundleCollection bundles)
        {
            // JS
            bundles.Add(new ScriptBundle("~/bundles/jquery")
                .Include("~/Scripts/jquery-3.7.1.min.js"));

            bundles.Add(new ScriptBundle("~/bundles/jqueryval")
                .Include(
                    "~/Scripts/jquery.validate.min.js",
                    "~/Scripts/jquery.validate.unobtrusive.min.js"
                ));

            var bootstrap = new ScriptBundle("~/bundles/bootstrap")
                .Include("~/Scripts/bootstrap.bundle.min.js");

            // Keep bootstrap bundle as-is (already minified). Remove extra minify transform.
            var minify = bootstrap.Transforms.OfType<JsMinify>().FirstOrDefault();
            if (minify != null) bootstrap.Transforms.Remove(minify);

            bundles.Add(bootstrap);

            bundles.Add(new ScriptBundle("~/bundles/modernizr")
                .Include("~/Scripts/modernizr-2.8.3.js"));

            bundles.Add(new ScriptBundle("~/bundles/toastr")
                .Include("~/Scripts/toastr.min.js"));

            bundles.Add(new ScriptBundle("~/bundles/admin")
                .Include("~/Scripts/admin.js"));

            bundles.Add(new ScriptBundle("~/bundles/adminEnroll")
                .Include("~/Scripts/admin-enroll.js"));

            bundles.Add(new ScriptBundle("~/bundles/kiosk")
                .Include("~/Scripts/kiosk.js"));

            // CSS
            bundles.Add(new StyleBundle("~/Content/css")
                .Include(
                    "~/Content/bootstrap.min.css",
                    "~/Content/toastr.min.css",
                    "~/Content/site.css"
                ));

            bundles.Add(new StyleBundle("~/Content/admin")
                .Include("~/Content/admin.css"));

            bundles.Add(new StyleBundle("~/Content/kiosk")
                .Include("~/Content/kiosk.css"));

            // Leave disabled while debugging
            // BundleTable.EnableOptimizations = true;
        }
    }
}
