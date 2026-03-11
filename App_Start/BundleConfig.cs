using System;
using System.Linq;
using System.Web.Hosting;
using System.Web.Optimization;

namespace FaceAttend
{
    /// <summary>
    /// SAGUPA: Nagre-register ng CSS at JavaScript bundles para sa application.
    /// 
    /// PAGLALARAWAN:
    ///   Ang bundling ay nagko-combine ng maraming files into single request,
    ///   para mas mabilis ang page load. Ginagamit ito sa production.
    /// 
    /// GINAGAMIT SA:
    ///   - Global.asax.cs sa Application_Start
    ///   - Mga Views via @Styles.Render() at @Scripts.Render()
    /// 
    /// BUNDLE STRUCTURE:
    ///   ~/bundles/jquery      - jQuery core
    ///   ~/bundles/jqueryval   - jQuery validation
    ///   ~/bundles/bootstrap   - Bootstrap JS
    ///   ~/bundles/vendor      - Third-party plugins (DataTables, SweetAlert2)
    ///   ~/bundles/admin       - Admin scripts
    ///   ~/bundles/adminEnroll - Enrollment scripts
    ///   ~/bundles/kiosk       - Kiosk scripts
    ///   
    ///   ~/Content/css         - Base CSS (Bootstrap, Toastr)
    ///   ~/bundles/vendor-css  - Third-party CSS (FontAwesome, DataTables)
    ///   ~/Content/admin       - Admin styles
    ///   ~/Content/admin-enroll - Enrollment styles
    ///   ~/Content/kiosk       - Kiosk styles
    /// </summary>
    public class BundleConfig
    {
        public static void RegisterBundles(BundleCollection bundles)
        {
            // =================================================================
            // JAVASCRIPT BUNDLES
            // =================================================================

            // jQuery Core
            bundles.Add(new ScriptBundle("~/bundles/jquery")
                .Include("~/Scripts/jquery-3.7.1.min.js"));

            // jQuery Validation
            bundles.Add(new ScriptBundle("~/bundles/jqueryval")
                .Include(
                    "~/Scripts/jquery.validate.min.js",
                    "~/Scripts/jquery.validate.unobtrusive.min.js"
                ));

            // Bootstrap (with minify removed para mas mabilis)
            var bootstrap = new ScriptBundle("~/bundles/bootstrap")
                .Include("~/Scripts/bootstrap.bundle.min.js");
            var minify = bootstrap.Transforms.OfType<JsMinify>().FirstOrDefault();
            if (minify != null) bootstrap.Transforms.Remove(minify);
            bundles.Add(bootstrap);

            // Toastr Notifications
            bundles.Add(new ScriptBundle("~/bundles/toastr")
                .Include("~/Scripts/toastr.min.js"));

            // Vendor Plugins (DataTables, SweetAlert2)
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

            // DataTables Responsive
            var respCore = "~/Scripts/vendor/datatables/js/dataTables.responsive.min.js";
            var respBs5  = "~/Scripts/vendor/datatables/responsive.bootstrap5.min.js";
            if (FileExists(respCore)) vendor.Include(respCore);
            if (FileExists(respCore) && FileExists(respBs5)) vendor.Include(respBs5);
            bundles.Add(vendor);

            // Kiosk Vendor (Toastify + Leaflet)
            var kioskVendor = new ScriptBundle("~/bundles/kiosk-vendor");
            if (FileExists("~/Scripts/vendor/toastify/toastify.min.js"))
                kioskVendor.Include("~/Scripts/vendor/toastify/toastify.min.js");
            if (FileExists("~/Scripts/vendor/leaflet/leaflet.js"))
                kioskVendor.Include("~/Scripts/vendor/leaflet/leaflet.js");
            bundles.Add(kioskVendor);

            // Admin Scripts (consolidated - includes office map)
            var admin = new ScriptBundle("~/bundles/admin")
                .Include("~/Scripts/admin.js");

            var adminMinify = admin.Transforms.OfType<JsMinify>().FirstOrDefault();
            if (adminMinify != null) admin.Transforms.Remove(adminMinify);

            bundles.Add(admin);

            // Admin Enrollment Scripts
            var adminEnroll = new ScriptBundle("~/bundles/adminEnroll")
                .Include("~/Scripts/vendor/sweetalert2/sweetalert2.all.min.js")
                .Include("~/Scripts/admin-enroll.js");
            var adminEnrollMinify = adminEnroll.Transforms.OfType<JsMinify>().FirstOrDefault();
            if (adminEnrollMinify != null) adminEnroll.Transforms.Remove(adminEnrollMinify);
            bundles.Add(adminEnroll);

            // Kiosk Scripts
            // Kiosk Scripts (includes embedded toast system)
            bundles.Add(new ScriptBundle("~/bundles/kiosk")
                .Include("~/Scripts/kiosk.js"));

            // =================================================================
            // CSS BUNDLES
            // =================================================================

            // Base CSS (Bootstrap + Toastr)
            bundles.Add(new StyleBundle("~/Content/css")
                .Include(
                    "~/Content/bootstrap.min.css",
                    "~/Content/toastr.min.css"
                ));

            // Vendor CSS (FontAwesome + DataTables + SweetAlert2)
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

            // DataTables Responsive CSS
            var respCoreCss = "~/Scripts/vendor/datatables/responsive.dataTables.min.css";
            var respBs5Css  = "~/Scripts/vendor/datatables/responsive.bootstrap5.min.css";
            if (FileExists(respCoreCss)) vendorCss.Include(respCoreCss);
            if (FileExists(respCoreCss) && FileExists(respBs5Css)) vendorCss.Include(respBs5Css);
            bundles.Add(vendorCss);

            // Kiosk Vendor CSS
            var kioskVendorCss = new StyleBundle("~/Content/kiosk-vendor");
            if (FileExists("~/Scripts/vendor/toastify/toastify.min.css"))
                kioskVendorCss.Include("~/Scripts/vendor/toastify/toastify.min.css");
            if (FileExists("~/Content/vendor/leaflet/leaflet.css"))
                kioskVendorCss.Include("~/Content/vendor/leaflet/leaflet.css");
            bundles.Add(kioskVendorCss);

            // Kiosk Base (Bootstrap only)
            bundles.Add(new StyleBundle("~/Content/kiosk-base")
                .Include("~/Content/bootstrap.min.css"));

            // Admin CSS (consolidated - includes all admin styles)
            bundles.Add(new StyleBundle("~/Content/admin")
                .Include(
                    "~/Content/admin.css"      // Consolidated: base + enhancements
                ));

            // Admin Enrollment CSS
            bundles.Add(new StyleBundle("~/Content/admin-enroll")
                .Include("~/Content/admin-enroll.css"));

            // Kiosk CSS (includes responsive styles)
            bundles.Add(new StyleBundle("~/Content/kiosk")
                .Include("~/Content/kiosk.css"));

            // Kiosk Responsive (placeholder - styles merged into kiosk.css)
            bundles.Add(new StyleBundle("~/Content/kiosk-responsive")
                .Include("~/Content/kiosk-responsive.css"));

            // =================================================================
            // BUNDLE OPTIMIZATION
            // =================================================================
            // NOTE: Set to true sa production para mag-minify ang bundles
            BundleTable.EnableOptimizations = false;
        }

        /// <summary>
        /// Helper para i-check kung exist ang file bago i-include sa bundle.
        /// Iiwasan ang 404 errors kung missing ang vendor files.
        /// </summary>
        private static bool FileExists(string virtualPath)
        {
            var p = HostingEnvironment.MapPath(virtualPath);
            return !string.IsNullOrWhiteSpace(p) && System.IO.File.Exists(p);
        }
    }
}
