using System;
using System.Linq;
using System.Web.Optimization;
using FaceAttend.Services.Helpers;

namespace FaceAttend
{
    /// <summary>
    /// Unified Bundle Configuration for FaceAttend v2
    /// 
    /// BUNDLE STRUCTURE:
    ///   ~/bundles/jquery           - jQuery core
    ///   ~/bundles/jqueryval        - jQuery validation
    ///   ~/bundles/bootstrap        - Bootstrap JS
    ///   ~/bundles/vendor           - Third-party plugins
    ///   ~/bundles/facescan-core    - Core face scanning modules
    ///   ~/bundles/facescan-ui      - UI components (v2 simplified)
    ///   ~/bundles/admin            - Admin scripts
    ///   ~/bundles/kiosk            - Kiosk scripts
    ///   
    ///   ~/Content/css              - Base CSS (Bootstrap, Toastr)
    ///   ~/bundles/vendor-css       - Third-party CSS
    ///   ~/Content/facescan-v2      - Unified design system (v2 - USE THIS)
    ///   ~/Content/admin            - Admin styles
    ///   ~/Content/kiosk            - Kiosk styles
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

            // Bootstrap 5
            var bootstrapBundle = new NonMinifiedScriptBundle("~/bundles/bootstrap");
            if (FileSystemHelper.FileExists("~/Scripts/bootstrap.bundle.min.js"))
                bootstrapBundle.Include("~/Scripts/bootstrap.bundle.min.js");
            bundles.Add(bootstrapBundle);

            // Toastr Notifications
            bundles.Add(new ScriptBundle("~/bundles/toastr")
                .Include("~/Scripts/toastr.min.js"));

            // Vendor Plugins (DataTables, SweetAlert2)
            var vendor = new NonMinifiedScriptBundle("~/bundles/vendor");
            foreach (var p in new[]
            {
                "~/Scripts/vendor/datatables/dataTables.min.js",
                "~/Scripts/vendor/datatables/dataTables.bootstrap5.min.js",
                "~/Scripts/vendor/datatables/js/dataTables.buttons.min.js",
                "~/Scripts/vendor/datatables/js/buttons.html5.min.js",
                "~/Scripts/vendor/datatables/js/buttons.print.min.js",
                "~/Scripts/vendor/datatables/buttons.bootstrap5.min.js",
                "~/Scripts/vendor/datatables/js/dataTables.responsive.min.js",
                "~/Scripts/vendor/datatables/responsive.bootstrap5.min.js"
            })
            {
                if (FileSystemHelper.FileExists(p)) vendor.Include(p);
            }
            bundles.Add(vendor);

            // SweetAlert2
            bundles.Add(new NonMinifiedScriptBundle("~/bundles/sweetalert")
                .Include("~/Scripts/vendor/sweetalert2/sweetalert2.all.min.js"));

            // Kiosk Vendor
            var kioskVendor = new ScriptBundle("~/bundles/kiosk-vendor");
            foreach (var p in new[]
            {
                "~/Scripts/vendor/toastify/toastify.min.js",
                "~/Scripts/vendor/leaflet/leaflet.js",
                "~/Scripts/vendor/sweetalert2/sweetalert2.all.min.js"
            })
            {
                if (FileSystemHelper.FileExists(p)) kioskVendor.Include(p);
            }
            bundles.Add(kioskVendor);

            // FaceAttend Core Modules (Camera, API, Notify, FaceScan)
            bundles.Add(new ScriptBundle("~/bundles/facescan-core")
                .Include("~/Scripts/core/camera.js")
                .Include("~/Scripts/core/api.js")
                .Include("~/Scripts/core/notify.js")
                .Include("~/Scripts/core/facescan.js"));

            // FaceAttend UI Components v2 (Simplified - Single File)
            bundles.Add(new ScriptBundle("~/bundles/facescan-ui")
                .Include("~/Scripts/facescan-ui.js"));

            // Admin Scripts
            bundles.Add(new ScriptBundle("~/bundles/admin")
                .Include("~/Scripts/admin.js"));

            // Kiosk Scripts
            bundles.Add(new ScriptBundle("~/bundles/kiosk")
                .Include("~/Scripts/kiosk.js"));

            // Enrollment Bundle (Core + UI + Enrollment logic)
            bundles.Add(new ScriptBundle("~/bundles/enrollment")
                .Include("~/Scripts/core/camera.js")
                .Include("~/Scripts/core/api.js")
                .Include("~/Scripts/core/notify.js")
                .Include("~/Scripts/core/facescan.js")
                .Include("~/Scripts/facescan-ui.js")
                .Include("~/Scripts/modules/enrollment-core-refactored.js")
                .Include("~/Scripts/enrollment-ui.js"));

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
            var vendorCss = new NonMinifiedStyleBundle("~/bundles/vendor-css")
                .Include("~/Content/vendor/fontawesome/css/all.min.css", new CssRewriteUrlTransform());

            foreach (var p in new[]
            {
                "~/Scripts/vendor/sweetalert2/sweetalert2.min.css",
                "~/Scripts/vendor/datatables/dataTables.bootstrap5.min.css",
                "~/Scripts/vendor/datatables/buttons.bootstrap5.min.css",
                "~/Scripts/vendor/datatables/responsive.dataTables.min.css",
                "~/Scripts/vendor/datatables/responsive.bootstrap5.min.css"
            })
            {
                if (FileSystemHelper.FileExists(p)) vendorCss.Include(p);
            }
            bundles.Add(vendorCss);

            // Kiosk Vendor CSS
            var kioskVendorCss = new StyleBundle("~/Content/kiosk-vendor");
            foreach (var p in new[]
            {
                "~/Scripts/vendor/toastify/toastify.min.css",
                "~/Content/vendor/leaflet/leaflet.css",
                "~/Scripts/vendor/sweetalert2/sweetalert2.min.css"
            })
            {
                if (FileSystemHelper.FileExists(p)) kioskVendorCss.Include(p);
            }
            bundles.Add(kioskVendorCss);

            // Kiosk Base
            bundles.Add(new StyleBundle("~/Content/kiosk-base")
                .Include("~/Content/bootstrap.min.css"));

            // FaceAttend v2 - Unified Design System (SIMPLIFIED - USE THIS)
            bundles.Add(new NonMinifiedStyleBundle("~/Content/facescan-v2")
                .Include("~/Content/facescan-v2.css"));

            // Legacy bundles (for backward compatibility)
            bundles.Add(new NonMinifiedStyleBundle("~/Content/facescan")
                .Include("~/Content/_tokens.css")
                .Include("~/Content/_components.css"));

            // Admin CSS
            bundles.Add(new StyleBundle("~/Content/admin")
                .Include("~/Content/admin.css"));

            // Kiosk CSS
            bundles.Add(new StyleBundle("~/Content/kiosk")
                .Include("~/Content/kiosk.css"));

            // Enrollment CSS
            bundles.Add(new NonMinifiedStyleBundle("~/Content/enrollment")
                .Include("~/Content/enrollment.css"));

            // =================================================================
            // BUNDLE OPTIMIZATION
            // =================================================================
#if DEBUG
            BundleTable.EnableOptimizations = false;
#else
            var enableOptimizations = System.Configuration.ConfigurationManager.AppSettings["BundleTable:EnableOptimizations"];
            BundleTable.EnableOptimizations = string.Equals(enableOptimizations, "true", StringComparison.OrdinalIgnoreCase);
#endif
        }
    }
}
