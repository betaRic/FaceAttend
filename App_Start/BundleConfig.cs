using System;
using System.Linq;
using System.Web.Hosting;
using System.Web.Optimization;

namespace FaceAttend
{
    public class BundleConfig
    {
        public static void RegisterBundles(BundleCollection bundles)
        {
            bundles.Add(new ScriptBundle("~/bundles/jquery")
                .Include("~/Scripts/jquery-3.7.1.min.js"));

            bundles.Add(new ScriptBundle("~/bundles/jqueryval")
                .Include(
                    "~/Scripts/jquery.validate.min.js",
                    "~/Scripts/jquery.validate.unobtrusive.min.js"
                ));

            var bootstrap = new ScriptBundle("~/bundles/bootstrap")
                .Include("~/Scripts/bootstrap.bundle.min.js");

            var minify = bootstrap.Transforms.OfType<JsMinify>().FirstOrDefault();
            if (minify != null) bootstrap.Transforms.Remove(minify);

            bundles.Add(bootstrap);

            bundles.Add(new ScriptBundle("~/bundles/modernizr")
                .Include("~/Scripts/modernizr-2.8.3.js"));

            bundles.Add(new ScriptBundle("~/bundles/toastr")
                .Include("~/Scripts/toastr.min.js"));

            var vendor = new ScriptBundle("~/bundles/vendor");
            foreach (var p in new[]
            {
                "~/Scripts/vendor/sweetalert2/sweetalert2.all.min.js",
                "~/Scripts/vendor/datatables/dataTables.min.js",
                "~/Scripts/vendor/datatables/dataTables.bootstrap5.min.js",
                "~/Scripts/vendor/datatables/js/dataTables.buttons.min.js",
                "~/Scripts/vendor/datatables/js/buttons.html5.min.js",
                "~/Scripts/vendor/datatables/js/buttons.print.min.js",
                "~/Scripts/vendor/datatables/buttons.bootstrap5.min.js"
            })
            {
                if (FileExists(p)) vendor.Include(p);
            }

            var respCore = "~/Scripts/vendor/datatables/js/dataTables.responsive.min.js";
            var respBs5  = "~/Scripts/vendor/datatables/responsive.bootstrap5.min.js";
            if (FileExists(respCore)) vendor.Include(respCore);
            if (FileExists(respCore) && FileExists(respBs5)) vendor.Include(respBs5);
            bundles.Add(vendor);

            var kioskVendor = new ScriptBundle("~/bundles/kiosk-vendor");
            if (FileExists("~/Scripts/vendor/toastify/toastify.min.js"))
                kioskVendor.Include("~/Scripts/vendor/toastify/toastify.min.js");
            bundles.Add(kioskVendor);

            bundles.Add(new ScriptBundle("~/bundles/admin")
                .Include("~/Scripts/admin.js"));

            bundles.Add(new ScriptBundle("~/bundles/adminEnroll")
                .Include("~/Scripts/admin-enroll.js"));

            bundles.Add(new ScriptBundle("~/bundles/kiosk")
                .Include("~/Scripts/kiosk.js"));

            bundles.Add(new StyleBundle("~/Content/css")
                .Include(
                    "~/Content/bootstrap.min.css",
                    "~/Content/toastr.min.css",
                    "~/Content/site.css"
                ));

            var vendorCss = new StyleBundle("~/bundles/vendor-css")
                .Include("~/Content/vendor/fontawesome/css/all.min.css", new CssRewriteUrlTransform());

            foreach (var p in new[]
            {
                "~/Scripts/vendor/sweetalert2/sweetalert2.min.css",
                "~/Scripts/vendor/datatables/dataTables.bootstrap5.min.css",
                "~/Scripts/vendor/datatables/buttons.bootstrap5.min.css"
            })
            {
                if (FileExists(p)) vendorCss.Include(p);
            }

            var respCoreCss = "~/Scripts/vendor/datatables/responsive.dataTables.min.css";
            var respBs5Css  = "~/Scripts/vendor/datatables/responsive.bootstrap5.min.css";
            if (FileExists(respCoreCss)) vendorCss.Include(respCoreCss);
            if (FileExists(respCoreCss) && FileExists(respBs5Css)) vendorCss.Include(respBs5Css);
            bundles.Add(vendorCss);

            var kioskVendorCss = new StyleBundle("~/Content/kiosk-vendor");
            if (FileExists("~/Scripts/vendor/toastify/toastify.min.css"))
                kioskVendorCss.Include("~/Scripts/vendor/toastify/toastify.min.css");
            bundles.Add(kioskVendorCss);

            bundles.Add(new StyleBundle("~/Content/kiosk-base")
                .Include("~/Content/bootstrap.min.css"));

            bundles.Add(new StyleBundle("~/Content/admin")
                .Include(
                    "~/Content/admin.css",
                    "~/Content/admin-enhancements.css"
                ));

            bundles.Add(new StyleBundle("~/Content/kiosk")
                .Include("~/Content/kiosk.css"));

            BundleTable.EnableOptimizations = true;
        }

        private static bool FileExists(string virtualPath)
        {
            var p = HostingEnvironment.MapPath(virtualPath);
            return !string.IsNullOrWhiteSpace(p) && System.IO.File.Exists(p);
        }
    }
}