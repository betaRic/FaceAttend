(function () {
    function onReady(fn) {
        if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", fn);
        else fn();
    }

    function setFooterYear() {
        var y = document.getElementById("footerYear");
        if (y) y.textContent = new Date().getFullYear();
    }

    function wireRefreshButton() {
        var btn = document.getElementById("btnRefresh");
        if (!btn) return;

        btn.addEventListener("click", function () {
            window.location.reload();
        });
    }

    function wireTooltips() {
        if (!window.bootstrap || !bootstrap.Tooltip) return;

        var els = [].slice.call(document.querySelectorAll('[data-bs-toggle="tooltip"]'));
        els.forEach(function (el) { new bootstrap.Tooltip(el); });
    }

    function configureToastr() {
        if (!window.toastr) return;

        toastr.options = {
            closeButton: true,
            newestOnTop: true,
            progressBar: false,
            positionClass: "toast-top-right",
            preventDuplicates: true,
            timeOut: 3500,
            extendedTimeOut: 1200,
            showDuration: 120,
            hideDuration: 120,
            showMethod: "fadeIn",
            hideMethod: "fadeOut"
        };

        // Add basic accessibility flags once the container exists
        setTimeout(function () {
            var c = document.getElementById("toast-container");
            if (!c) return;
            c.setAttribute("role", "status");
            c.setAttribute("aria-live", "polite");
        }, 0);
    }

    function toast(type, message) {
        var msg = (message || "").toString();
        if (!msg) return;

        if (window.toastr) {
            if (type === "success") toastr.success(msg);
            else if (type === "warning") toastr.warning(msg);
            else if (type === "error") toastr.error(msg);
            else toastr.info(msg);
            return;
        }

        // Fallback
        if (type === "error") alert(msg);
        else console.log(msg);
    }

    function confirmDialog(opts) {
        var o = opts || {};
        var title = (o.title || "Confirm").toString();
        var text = (o.text || "").toString();
        var icon = (o.icon || "warning").toString();

        if (window.Swal && Swal.fire) {
            return Swal.fire({
                title: title,
                text: text,
                icon: icon,
                showCancelButton: true,
                confirmButtonText: o.okText || "Continue",
                cancelButtonText: o.cancelText || "Cancel",
                focusCancel: true
            }).then(function (r) {
                return r && r.isConfirmed;
            });
        }

        // Fallback
        var msg = text ? (title + "\n\n" + text) : title;
        return Promise.resolve(window.confirm(msg));
    }

    function wireConfirmLinks() {
        if (!window.jQuery) return;

        jQuery(document).on("click", "[data-confirm]", function (e) {
            var el = this;
            if (el && el.dataset && el.dataset.confirmed === "1") {
                el.dataset.confirmed = "0";
                return true;
            }

            var title = el.getAttribute("data-confirm") || "Confirm";
            var text = el.getAttribute("data-confirm-text") || "";
            var icon = el.getAttribute("data-confirm-icon") || "warning";

            e.preventDefault();

            confirmDialog({ title: title, text: text, icon: icon }).then(function (ok) {
                if (!ok) return;

                // Anchor
                if (el.tagName === "A" && el.href) {
                    window.location.href = el.href;
                    return;
                }

                // Button inside a form
                var form = el.closest ? el.closest("form") : null;
                if (form) {
                    form.submit();
                    return;
                }

                // Default
                el.dataset.confirmed = "1";
                el.click();
            });

            return false;
        });
    }

    function wireDataTables() {
        if (!window.jQuery) return;
        if (!jQuery.fn || !jQuery.fn.dataTable) return;

        jQuery(".js-datatable").each(function () {
            var $t = jQuery(this);
            if ($t.data("dtInit") === 1) return;
            $t.data("dtInit", 1);

            var pageLen = parseInt($t.attr("data-dt-page-length") || "25", 10);
            if (!isFinite(pageLen) || pageLen <= 0) pageLen = 25;

            var noSortLast = $t.attr("data-dt-no-sort-last") === "1";

            var stateSave = $t.attr("data-dt-state-save") === "1";

            var opts = {
                pageLength: pageLen,
                stateSave: stateSave,
                autoWidth: false,
                order: [],
                language: {
                    search: "",
                    searchPlaceholder: "Search..."
                },
                dom:
                    "<'row g-2 align-items-center'<'col-sm-6'B><'col-sm-6'f>>" +
                    "<'row'<'col-12'tr>>" +
                    "<'row g-2 align-items-center'<'col-sm-5'i><'col-sm-7'p>>"
            };

            if (window.jQuery.fn.dataTable.Buttons) {
                opts.buttons = [
                    { extend: "copy", className: "btn btn-sm btn-outline-secondary" },
                    { extend: "csv", className: "btn btn-sm btn-outline-secondary" },
                    { extend: "print", className: "btn btn-sm btn-outline-secondary" }
                ];
            } else {
                opts.dom =
                    "<'row g-2 align-items-center'<'col-sm-6'l><'col-sm-6'f>>" +
                    "<'row'<'col-12'tr>>" +
                    "<'row g-2 align-items-center'<'col-sm-5'i><'col-sm-7'p>>";
            }

            if (noSortLast) {
                opts.columnDefs = [
                    { targets: -1, orderable: false, searchable: false }
                ];
            }

            try {
                $t.DataTable(opts);
            } catch (err) {
                console.warn("DataTables init failed", err);
            }
        });
    }

    function wireIdleOverlay() {
        var overlay = document.getElementById("idleOverlay");
        if (!overlay) return;

        var IDLE_MS = 10 * 60 * 1000;
        var t = null;

        function show() {
            overlay.classList.remove("d-none");
            overlay.classList.add("d-flex");
        }

        function hide() {
            overlay.classList.add("d-none");
            overlay.classList.remove("d-flex");
        }

        function reset() {
            hide();
            if (t) clearTimeout(t);
            t = setTimeout(show, IDLE_MS);
        }

        overlay.addEventListener("click", function () {
            reset();
        });

        ["mousemove", "keydown", "mousedown", "touchstart", "scroll"].forEach(function (evt) {
            document.addEventListener(evt, reset, { passive: true });
        });

        reset();
    }

    // Public helpers
    window.ui = window.ui || {};
    window.ui.toastSuccess = function (m) { toast("success", m); };
    window.ui.toastInfo = function (m) { toast("info", m); };
    window.ui.toastWarning = function (m) { toast("warning", m); };
    window.ui.toastError = function (m) { toast("error", m); };
    window.ui.confirm = confirmDialog;

    onReady(function () {
        setFooterYear();
        wireRefreshButton();
        wireTooltips();

        configureToastr();

        if (window.__toastMsg) {
            var t = (window.__toastType || "success").toString().toLowerCase();
            if (t !== "success" && t !== "info" && t !== "warning" && t !== "error") t = "success";
            toast(t, window.__toastMsg);
            window.__toastMsg = null;
            window.__toastType = null;
        }

        wireConfirmLinks();
        wireDataTables();
        wireIdleOverlay();
    });
})();
