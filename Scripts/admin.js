/* ==========================================================================
   FaceAttend Admin Scripts
   ES5 Compatible - WebGrease Safe
   ========================================================================== */

(function () {
    'use strict';

    /* ------------------------------------------------------------------
       1. Core Utilities
    ------------------------------------------------------------------ */

    function onReady(fn) {
        if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', fn);
        else fn();
    }

    // querySelector shorthand - returns a NATIVE DOM element (not jQuery)
    function $(sel) { return document.querySelector(sel); }
    function $$(sel) {
        var nl = document.querySelectorAll(sel);
        var a = [];
        for (var i = 0; i < nl.length; i++) a.push(nl[i]);
        return a;
    }

    /* ------------------------------------------------------------------
       2. Footer Year
    ------------------------------------------------------------------ */

    function initFooterYear() {
        var el = document.getElementById('footerYear');
        if (el) el.textContent = new Date().getFullYear();
    }

    /* ------------------------------------------------------------------
       3. Bootstrap Tooltips
    ------------------------------------------------------------------ */

    function initTooltips() {
        if (!window.bootstrap || !window.bootstrap.Tooltip) return;
        $$('[data-bs-toggle="tooltip"]').forEach(function (el) {
            new bootstrap.Tooltip(el, { trigger: 'hover focus' });
        });
    }

    /* ------------------------------------------------------------------
       4. Auto-dismiss Flash Alerts
       Success alerts from TempData fade out after 4 s.
       Error alerts stay until dismissed manually.
    ------------------------------------------------------------------ */

    function initAutoDismissAlerts() {
        $$('.alert-success, .ea-alert.success').forEach(function (el) {
            setTimeout(function () {
                el.style.transition = 'opacity 0.4s ease';
                el.style.opacity = '0';
                setTimeout(function () {
                    if (el.parentNode) el.parentNode.removeChild(el);
                }, 400);
            }, 4000);
        });
    }

    /* ------------------------------------------------------------------
       5. Server Toast (TempData → JS bridge)
       Delegates to window.ui.toast provided by notify.js.
    ------------------------------------------------------------------ */

    function initServerToast() {
        if (window.__toastMsg) {
            var valid = ['success', 'info', 'warning', 'error'];
            var type  = valid.indexOf(window.__toastType) >= 0 ? window.__toastType : 'success';
            window.ui.toast(window.__toastMsg, { type: type });
            window.__toastMsg  = null;
            window.__toastType = null;
        }
    }

    /* ------------------------------------------------------------------
       6. Keyboard Shortcuts
       Ctrl+/ - focus DataTables search input on the current page
       Escape  - dismiss any open toastr notifications
    ------------------------------------------------------------------ */

    function initKeyboardShortcuts() {
        document.addEventListener('keydown', function (e) {
            if ((e.ctrlKey || e.metaKey) && (e.key === '/' || e.key === 'k')) {
                var searchInput = $('.dataTables_filter input') || $('[type="search"]');
                if (searchInput) { e.preventDefault(); searchInput.focus(); searchInput.select(); }
            }
            if (e.key === 'Escape' && window.toastr) toastr.clear();
        });
    }

    /* ------------------------------------------------------------------
       7. Refresh Button (topbar #btnRefresh)
    ------------------------------------------------------------------ */

    function initRefreshButton() {
        var btn = document.getElementById('btnRefresh');
        if (!btn) return;
        btn.addEventListener('click', function () {
            btn.disabled = true;
            var icon = btn.querySelector('i');
            if (icon) { icon.classList.remove('fa-rotate'); icon.classList.add('fa-spinner', 'fa-spin'); }
            setTimeout(function () { location.reload(); }, 150);
        });
    }

    /* ------------------------------------------------------------------
       8. Sidebar Active State
       Marks the nav link matching the current URL as active.
    ------------------------------------------------------------------ */

    function initSidebarActiveState() {
        var links = $$('.admin-nav-link');
        if (!links.length) return;
        var hasServerActive = links.some(function (l) { return l.classList.contains('active'); });
        if (hasServerActive) return;
        var path = location.pathname.toLowerCase().replace(/\/$/, '');
        links.forEach(function (link) {
            var href = (link.getAttribute('href') || '').toLowerCase().replace(/\/$/, '');
            if (!href || href === '#') return;
            if (path === href || (href.length > 7 && (path + '/').indexOf(href + '/') === 0))
                link.classList.add('active');
        });
    }

    /* ------------------------------------------------------------------
       9. Boot - initialise all features
       DataTables   → admin-datatable.js   (self-initializing)
       Office map   → admin-map.js         (self-initializing)
       Confirm links → admin-confirm-links.js (self-initializing)
       Idle overlay  → admin-idle-overlay.js  (self-initializing)
       Back-to-top   → admin-back-to-top.js   (self-initializing)
    ------------------------------------------------------------------ */

    onReady(function () {
        initFooterYear();
        initTooltips();
        initAutoDismissAlerts();
        initServerToast();
        initKeyboardShortcuts();
        initRefreshButton();
        initSidebarActiveState();
    });

    /* ------------------------------------------------------------------
       10. Public API stub
       Toast, confirm, and confirm aliases are provided by notify.js.
       window.ui.initDataTables is assigned by admin-datatable.js.
    ------------------------------------------------------------------ */

    window.ui = window.ui || {};

}());
