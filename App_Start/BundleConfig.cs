using System;
using System.Linq;
using System.Web.Optimization;
using FaceAttend.Services.Helpers;

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
    ///   ~/bundles/bootstrap   - Bootstrap JS (CDN)
    ///   ~/bundles/vendor      - Third-party plugins (DataTables, SweetAlert2 CDN)
    ///   ~/bundles/admin       - Admin scripts
    ///   ~/bundles/adminEnroll - Enrollment scripts
    ///   ~/bundles/kiosk       - Kiosk scripts
    ///   
    ///   ~/Content/css         - Base CSS (Bootstrap, Toastr)
    ///   ~/bundles/vendor-css  - Third-party CSS (FontAwesome, DataTables)
    ///   ~/Content/admin       - Admin styles
    ///   ~/Content/admin-enroll - Enrollment styles
    ///   ~/Content/kiosk       - Kiosk styles
    /// 
    /// IMPORTANT - ES6+ SYNTAX COMPATIBILITY:
    ///   The default WebGrease minifier does NOT support ES6+ syntax (const, let, =>, etc.)
    ///   Vendor libraries like Bootstrap 5 and SweetAlert2 use ES6+ and are already minified.
    ///   We use NonMinifiedScriptBundle for these to avoid JSParser NullReferenceException.
    /// </summary>
    public class BundleConfig
    {
        public static void RegisterBundles(BundleCollection bundles)
        {
            // =================================================================
            // JAVASCRIPT BUNDLES
            // =================================================================

            // jQuery Core (ES5 - can be minified)
            bundles.Add(new ScriptBundle("~/bundles/jquery")
                .Include("~/Scripts/jquery-3.7.1.min.js"));

            // jQuery Validation (ES5 - can be minified)
            bundles.Add(new ScriptBundle("~/bundles/jqueryval")
                .Include(
                    "~/Scripts/jquery.validate.min.js",
                    "~/Scripts/jquery.validate.unobtrusive.min.js"
                ));

            // Bootstrap 5 - ES6+ syntax, already minified, use CDN
            // Local fallback for offline development
            var bootstrapBundle = new NonMinifiedScriptBundle("~/bundles/bootstrap");
            if (FileSystemHelper.FileExists("~/Scripts/bootstrap.bundle.min.js"))
                bootstrapBundle.Include("~/Scripts/bootstrap.bundle.min.js");
            bundles.Add(bootstrapBundle);

            // Toastr Notifications (ES5 - can be minified)
            bundles.Add(new ScriptBundle("~/bundles/toastr")
                .Include("~/Scripts/toastr.min.js"));

            // Vendor Plugins (SweetAlert2 ES6+ CDN, DataTables ES5)
            // SweetAlert2 uses ES6+ - use CDN version that's already minified
            var vendor = new NonMinifiedScriptBundle("~/bundles/vendor");
            
            // DataTables - ES5 compatible, can be minified
            foreach (var p in new[]
            {
                "~/Scripts/vendor/datatables/dataTables.min.js",
                "~/Scripts/vendor/datatables/dataTables.bootstrap5.min.js",
                "~/Scripts/vendor/datatables/js/dataTables.buttons.min.js",
                "~/Scripts/vendor/datatables/js/buttons.html5.min.js",
                "~/Scripts/vendor/datatables/js/buttons.print.min.js",
                "~/Scripts/vendor/datatables/buttons.bootstrap5.min.js"
            })
            {
                if (FileSystemHelper.FileExists(p)) vendor.Include(p);
            }

            // DataTables Responsive
            var responsiveJsFiles = new[]
            {
                "~/Scripts/vendor/datatables/js/dataTables.responsive.min.js",
                "~/Scripts/vendor/datatables/responsive.bootstrap5.min.js"
            };
            foreach (var p in responsiveJsFiles)
            {
                if (FileSystemHelper.FileExists(p)) vendor.Include(p);
            }
            bundles.Add(vendor);

            // SweetAlert2 - ES6+ syntax, use CDN
            bundles.Add(new NonMinifiedScriptBundle("~/bundles/sweetalert")
                .Include("~/Scripts/vendor/sweetalert2/sweetalert2.all.min.js"));

            // Kiosk Vendor (Toastify + Leaflet + SweetAlert2)
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

            // Admin Scripts (consolidated - ES5 compatible)
            bundles.Add(new ScriptBundle("~/bundles/admin")
                .Include("~/Scripts/admin.js"));

            // Admin Enrollment Scripts
            // Note: enroll-wizard.js removed - the inline script in Enroll.cshtml handles the UI
            bundles.Add(new ScriptBundle("~/bundles/adminEnroll")
                .Include("~/Scripts/modules/enrollment-core.js")
                .Include("~/Scripts/admin-enroll.js"));

            // Kiosk Scripts (ES5 compatible)
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
            // Use NonMinifiedStyleBundle because FontAwesome and DataTables CSS contain
            // modern CSS features (custom properties, vendor prefixes) that WebGrease minifier can't handle
            var vendorCss = new NonMinifiedStyleBundle("~/bundles/vendor-css")
                .Include("~/Content/vendor/fontawesome/css/all.min.css", new CssRewriteUrlTransform());

            foreach (var p in new[]
            {
                "~/Scripts/vendor/sweetalert2/sweetalert2.min.css",
                "~/Scripts/vendor/datatables/dataTables.bootstrap5.min.css",
                "~/Scripts/vendor/datatables/buttons.bootstrap5.min.css"
            })
            {
                if (FileSystemHelper.FileExists(p)) vendorCss.Include(p);
            }

            // DataTables Responsive CSS
            var responsiveCssFiles = new[]
            {
                "~/Scripts/vendor/datatables/responsive.dataTables.min.css",
                "~/Scripts/vendor/datatables/responsive.bootstrap5.min.css"
            };
            foreach (var p in responsiveCssFiles)
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

            // Kiosk Base (Bootstrap only)
            bundles.Add(new StyleBundle("~/Content/kiosk-base")
                .Include("~/Content/bootstrap.min.css"));

            // Shared Variables and Animations
            bundles.Add(new StyleBundle("~/Content/shared")
                .Include(
                    "~/Content/_variables.css",
                    "~/Content/_animations.css"
                ));

            // Admin CSS (consolidated)
            bundles.Add(new StyleBundle("~/Content/admin")
                .Include(
                    "~/Content/admin.css"
                ));

            // Admin Enrollment CSS
            // Uses NonMinifiedStyleBundle because it contains CSS custom properties (--ea-*)
            // that the WebGrease CSS minifier cannot handle
            bundles.Add(new NonMinifiedStyleBundle("~/Content/admin-enroll")
                .Include("~/Content/admin-enroll.css"));

            // Kiosk CSS
            bundles.Add(new StyleBundle("~/Content/kiosk")
                .Include("~/Content/kiosk.css"));

            // =================================================================
            // BUNDLE OPTIMIZATION
            // =================================================================
            // Enable optimizations in production based on Web.config setting
            // NonMinifiedScriptBundle classes skip minification entirely
#if DEBUG
            BundleTable.EnableOptimizations = false;
#else
            var enableOptimizations = System.Configuration.ConfigurationManager.AppSettings["BundleTable:EnableOptimizations"];
            BundleTable.EnableOptimizations = string.Equals(enableOptimizations, "true", StringComparison.OrdinalIgnoreCase);
#endif
        }
    }
}
