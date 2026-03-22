using System;
using System.Linq;
using System.Web.Optimization;
using FaceAttend.Services.Helpers;

namespace FaceAttend
{
    /// <summary>
    /// Unified Bundle Configuration for FaceAttend v3 (Unification)
    /// 
    /// DESIGN SYSTEM PRINCIPLE:
    ///   fa-design-system.css is THE SINGLE SOURCE OF TRUTH for all CSS tokens.
    ///   All surface files (admin.css, kiosk.css) load this first, then add only
    ///   their component styles. No :root {} blocks in surface files.
    /// 
    /// BUNDLE STRUCTURE:
    ///   VENDOR JS:
    ///   ~/bundles/jquery           - jQuery core
    ///   ~/bundles/jqueryval        - jQuery validation
    ///   ~/bundles/bootstrap        - Bootstrap JS
    ///   ~/bundles/toastr           - Toastr notifications
    ///   ~/bundles/vendor           - DataTables + SweetAlert2
    ///   ~/bundles/kiosk-vendor     - Toastify + Leaflet + SweetAlert2
    ///   ~/bundles/sweetalert       - SweetAlert2 standalone
    ///   
    ///   FACEATTEND CORE (all surfaces use this):
    ///   ~/bundles/fa-core          - Utils + Camera + API + Notify + FaceScan
    ///   ~/bundles/facescan-core    - ALIAS for fa-core (backward compat)
    ///   
    ///   SURFACE JS:
    ///   ~/bundles/admin            - Admin scripts
    ///   ~/bundles/kiosk            - Kiosk scripts
    ///   ~/bundles/enrollment       - Enrollment core + UI
    ///   ~/bundles/facescan-ui      - Camera/progress UI components
    ///   
    ///   VENDOR CSS:
    ///   ~/Content/css              - Bootstrap + Toastr (admin base)
    ///   ~/bundles/vendor-css       - FontAwesome + DataTables + SweetAlert2
    ///   ~/Content/kiosk-vendor     - Toastify + Leaflet + SweetAlert2
    ///   
    ///   DESIGN SYSTEM CSS (all surfaces use this):
    ///   ~/Content/fa-system        - fa-design-system.css (THE TOKENS)
    ///   ~/Content/facescan-v2      - DEPRECATED: will be removed (use fa-system)
    ///   
    ///   SURFACE CSS:
    ///   ~/Content/admin            - Admin component styles
    ///   ~/Content/kiosk            - Kiosk component styles
    ///   ~/Content/fa-mobile        - Mobile component styles (was mobile-kiosk)
    ///   ~/Content/enrollment       - Enrollment component styles
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

            // FaceAttend Core — Single source of truth for all surfaces
            // Provides: FaceAttend.Utils, FaceAttend.Camera, FaceAttend.API, FaceAttend.Notify, FaceAttend.FaceScan
            var faCore = new ScriptBundle("~/bundles/fa-core")
                .Include("~/Scripts/core/fa-helpers.js")   // FaceAttend.Utils ($, $$, el, debounce, getCsrfToken, ready)
                .Include("~/Scripts/core/camera.js")       // FaceAttend.Camera
                .Include("~/Scripts/core/api.js")          // FaceAttend.API
                .Include("~/Scripts/core/notify.js")       // FaceAttend.Notify (toast, confirm, alert)
                .Include("~/Scripts/core/facescan.js");    // FaceAttend.FaceScan
            bundles.Add(faCore);
            
            // Alias for backward compatibility (layouts that reference ~/bundles/facescan-core)
            bundles.Add(new ScriptBundle("~/bundles/facescan-core")
                .Include("~/Scripts/core/fa-helpers.js")
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

            // Enrollment Bundle (Core + Enrollment logic)
            // Order matters: fa-helpers must be first (provides Utils for other modules)
            bundles.Add(new ScriptBundle("~/bundles/enrollment")
                .Include("~/Scripts/core/fa-helpers.js")
                .Include("~/Scripts/core/camera.js")
                .Include("~/Scripts/core/api.js")
                .Include("~/Scripts/core/notify.js")
                .Include("~/Scripts/core/facescan.js")
                .Include("~/Scripts/modules/enrollment-core.js")
                .Include("~/Scripts/enrollment-tracker.js")
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

            // FaceAttend Design System — THE SINGLE SOURCE OF TRUTH for CSS tokens
            // All surfaces must load this BEFORE their surface-specific CSS
            bundles.Add(new NonMinifiedStyleBundle("~/Content/fa-system")
                .Include("~/Content/fa-design-system.css"));
            
            // NOTE: ~/Content/kiosk-base removed - use ~/Content/bootstrap directly
            // Kiosk pages should load: ~/Content/bootstrap → ~/Content/fa-system → ~/Content/kiosk
            
            // DEPRECATED: Will be removed in Phase 3. Use ~/Content/fa-system instead.
            bundles.Add(new NonMinifiedStyleBundle("~/Content/facescan-v2")
                .Include("~/Content/facescan-v2.css"));

            // NOTE: Legacy bundles removed in Phase 3
            // ~/Content/facescan, ~/Content/facescan-v2, ~/Content/kiosk-base
            // All tokens now in ~/Content/fa-system (fa-design-system.css)

            // Admin CSS - Use NonMinifiedStyleBundle to preserve CSS custom properties
            bundles.Add(new NonMinifiedStyleBundle("~/Content/admin")
                .Include("~/Content/admin.css"));

            // Kiosk CSS - Use NonMinifiedStyleBundle to preserve CSS custom properties
            bundles.Add(new NonMinifiedStyleBundle("~/Content/kiosk")
                .Include("~/Content/kiosk.css"));
            
            // Mobile CSS - Use NonMinifiedStyleBundle to preserve CSS custom properties
            bundles.Add(new NonMinifiedStyleBundle("~/Content/fa-mobile")
                .Include("~/Content/fa-mobile.css"));

            // Enrollment CSS
            bundles.Add(new NonMinifiedStyleBundle("~/Content/enrollment")
                .Include("~/Content/enrollment.css"));

            // Component CSS (Method Selector, Uploader, Wizard, Camera, Modal)
            bundles.Add(new NonMinifiedStyleBundle("~/Content/fa-components")
                .Include("~/Content/_unified/components/method-selector.css")
                .Include("~/Content/_unified/components/uploader.css")
                .Include("~/Content/_unified/components/wizard.css")
                .Include("~/Content/_unified/components/camera.css")
                .Include("~/Content/_unified/components/modal.css"));

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
