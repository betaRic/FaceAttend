using System;
using System.Linq;
using System.Web.Optimization;
using FaceAttend.Services.Helpers;

namespace FaceAttend
{
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

            var faCore = new ScriptBundle("~/bundles/fa-core")
                .Include("~/Scripts/core/fa-helpers.js")
                .Include("~/Scripts/core/camera.js")
                .Include("~/Scripts/core/api.js")
                .Include("~/Scripts/core/notify.js");
            bundles.Add(faCore);
            

            // Admin Scripts — notify.js first so window.ui is ready before admin.js runs
            bundles.Add(new ScriptBundle("~/bundles/admin")
                .Include("~/Scripts/core/notify.js")
                .Include("~/Scripts/admin.js")
                .Include("~/Scripts/admin/admin-datatable.js")
                .Include("~/Scripts/admin/admin-map.js")
                .Include("~/Scripts/admin/admin-confirm-links.js")
                .Include("~/Scripts/admin/admin-idle-overlay.js")
                .Include("~/Scripts/admin/admin-back-to-top.js"));

            // Audio Manager (loaded by kiosk and mobile layouts)
            bundles.Add(new ScriptBundle("~/bundles/audio-manager")
                .Include("~/Scripts/audio-manager.js"));

            // Kiosk Scripts — load order is strict (each module depends on prior ones)
            bundles.Add(new ScriptBundle("~/bundles/kiosk")
                .Include("~/Scripts/modules/face-guide.js")
                .Include("~/Scripts/kiosk/kiosk-config.js")
                .Include("~/Scripts/kiosk/kiosk-state.js")
                .Include("~/Scripts/kiosk/kiosk-clock.js")
                .Include("~/Scripts/kiosk/kiosk-warmup.js")
                .Include("~/Scripts/kiosk/kiosk-fullscreen.js")
                .Include("~/Scripts/kiosk/kiosk-unlock.js")
                .Include("~/Scripts/kiosk/kiosk-visitor.js")
                .Include("~/Scripts/kiosk/kiosk-device.js")
                .Include("~/Scripts/kiosk/kiosk-location.js")
                .Include("~/Scripts/kiosk/kiosk-map.js")
                .Include("~/Scripts/kiosk/kiosk-canvas.js")
                .Include("~/Scripts/kiosk/kiosk-mediapipe.js")
                .Include("~/Scripts/kiosk/kiosk-attendance.js")
                .Include("~/Scripts/kiosk.js"));

            // Enrollment Bundle (Core + Enrollment logic)
            // Order matters: fa-helpers must be first (provides Utils for other modules)
            // face-guide.js must be before enrollment-core and enrollment-tracker (both depend on it)
            bundles.Add(new ScriptBundle("~/bundles/enrollment")
                .Include("~/Scripts/core/fa-helpers.js")
                .Include("~/Scripts/core/camera.js")
                .Include("~/Scripts/core/api.js")
                .Include("~/Scripts/core/notify.js")
                .Include("~/Scripts/modules/face-guide.js")
                .Include("~/Scripts/modules/enrollment-core.js")
                .Include("~/Scripts/enrollment-tracker.js"));

            // Admin Employee Enrollment page (loaded after enrollment bundle)
            bundles.Add(new ScriptBundle("~/bundles/enroll-page")
                .Include("~/Scripts/admin/enroll-page.js"));

            // Mobile Enrollment page (loaded after enrollment bundle)
            bundles.Add(new ScriptBundle("~/bundles/mobile-enroll-page")
                .Include("~/Scripts/mobile/mobile-enroll-page.js"));

            // Admin Visitor Enrollment page (loaded after enrollment bundle)
            bundles.Add(new ScriptBundle("~/bundles/visitor-enroll-page")
                .Include("~/Scripts/admin/visitor-enroll-page.js"));


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

            bundles.Add(new NonMinifiedStyleBundle("~/Content/fa-system")
                .Include("~/Content/fa-design-system.css"));

            // Admin CSS - Use NonMinifiedStyleBundle to preserve CSS custom properties
            bundles.Add(new NonMinifiedStyleBundle("~/Content/admin")
                .Include("~/Content/admin.css"));

            // Kiosk CSS - Use NonMinifiedStyleBundle to preserve CSS custom properties
            bundles.Add(new NonMinifiedStyleBundle("~/Content/kiosk")
                .Include("~/Content/kiosk.css")
                .Include("~/Content/kiosk-bootstrap-compat.css"));
            
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
