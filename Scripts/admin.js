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
       2. Toast Notifications
       Wraps toastr if available, falls back gracefully.
    ------------------------------------------------------------------ */

    function toast(type, message) {
        var msg = (message || '').toString().trim();
        if (!msg) return;

        if (window.toastr) {
            toastr.options = {
                closeButton:       true,
                newestOnTop:       true,
                progressBar:       true,
                positionClass:     'toast-top-right',
                preventDuplicates: true,
                timeOut:           4000,
                extendedTimeOut:   1500,
                showDuration:      200,
                hideDuration:      200
            };
            var fn = toastr[type] ? toastr[type] : toastr.info;
            fn(msg);
            return;
        }

        // Fallback: inline banner at top of .admin-content
        var container = $('.admin-content') || document.body;
        var div = document.createElement('div');
        var color = type === 'error' || type === 'danger' ? 'danger'
                  : type === 'success' ? 'success'
                  : type === 'warning' ? 'warning'
                  : 'info';
        div.className = 'alert alert-' + color + ' alert-dismissible fade show mt-3';
        div.setAttribute('role', 'alert');
        div.innerHTML = msg + '<button type="button" class="btn-close" data-bs-dismiss="alert"></button>';
        container.insertBefore(div, container.firstChild);
        setTimeout(function () {
            div.classList.remove('show');
            setTimeout(function () { if (div.parentNode) div.parentNode.removeChild(div); }, 300);
        }, 5000);
    }

    /* ------------------------------------------------------------------
       3. Confirm Dialog
       Uses SweetAlert2 when loaded, native confirm as fallback.
    ------------------------------------------------------------------ */

    function confirmDialog(opts) {
        var o = opts || {};
        var title      = (o.title      || 'Confirm').toString();
        var text       = (o.text       || '').toString();
        var icon       = o.icon        || 'warning';
        var okText     = o.okText      || 'Continue';
        var cancelText = o.cancelText  || 'Cancel';

        if (window.Swal) {
            return Swal.fire({
                title:             title,
                text:              text || undefined,
                icon:              icon,
                showCancelButton:  true,
                confirmButtonText: okText,
                cancelButtonText:  cancelText,
                focusCancel:       true,
                buttonsStyling:    true,
                customClass: {
                    confirmButton: 'btn btn-' + (icon === 'danger' || icon === 'error' ? 'danger' : 'primary') + ' me-2',
                    cancelButton:  'btn btn-outline-secondary'
                }
            }).then(function (r) { return !!(r && r.isConfirmed); });
        }

        return Promise.resolve(confirm(text ? title + '\n\n' + text : title));
    }

    /* ------------------------------------------------------------------
       4. Footer Year
    ------------------------------------------------------------------ */

    function initFooterYear() {
        var el = document.getElementById('footerYear');
        if (el) el.textContent = new Date().getFullYear();
    }

    /* ------------------------------------------------------------------
       5. Bootstrap Tooltips
    ------------------------------------------------------------------ */

    function initTooltips() {
        if (!window.bootstrap || !window.bootstrap.Tooltip) return;
        $$('[data-bs-toggle="tooltip"]').forEach(function (el) {
            new bootstrap.Tooltip(el, { trigger: 'hover focus' });
        });
    }

    /* ------------------------------------------------------------------
       6. Auto-dismiss Flash Alerts
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
       7. Server Toast (TempData → JS bridge)
    ------------------------------------------------------------------ */

    function initServerToast() {
        if (window.__toastMsg) {
            var valid = ['success', 'info', 'warning', 'error'];
            var type  = valid.indexOf(window.__toastType) >= 0 ? window.__toastType : 'success';
            toast(type, window.__toastMsg);
            window.__toastMsg  = null;
            window.__toastType = null;
        }
    }

    /* ------------------------------------------------------------------
       8. Confirm Links  [data-confirm="..."]
       Intercepts anchor + form submit buttons to show a dialog first.
    ------------------------------------------------------------------ */

    function initConfirmLinks() {
        document.addEventListener('click', function (e) {
            var el = e.target && e.target.closest('[data-confirm]');
            if (!el) return;

            if (el.dataset.confirmed === '1') {
                el.dataset.confirmed = '0';
                return;
            }

            e.preventDefault();
            e.stopPropagation();

            var title = el.getAttribute('data-confirm')      || 'Are you sure?';
            var text  = el.getAttribute('data-confirm-text') || '';
            var icon  = el.getAttribute('data-confirm-icon') || 'warning';

            confirmDialog({ title: title, text: text, icon: icon })
                .then(function (ok) {
                    if (!ok) return;
                    if (el.tagName === 'A' && el.href && el.href !== '#') {
                        location.href = el.href;
                        return;
                    }
                    var form = el.closest('form');
                    if (form) { form.submit(); return; }
                    el.dataset.confirmed = '1';
                    el.click();
                });
        });
    }

    /* ------------------------------------------------------------------
       9. Idle Overlay
       Shows a dim overlay after 10 minutes of inactivity.
    ------------------------------------------------------------------ */

    function initIdleOverlay() {
        var overlay = document.getElementById('idleOverlay');
        if (!overlay) return;

        var IDLE_MS = 10 * 60 * 1000;
        var timer   = null;

        function show() { overlay.classList.remove('d-none'); overlay.classList.add('d-flex'); }
        function hide() { overlay.classList.add('d-none');    overlay.classList.remove('d-flex'); }

        function reset() {
            hide();
            clearTimeout(timer);
            timer = setTimeout(show, IDLE_MS);
        }

        overlay.addEventListener('click', reset);
        overlay.addEventListener('keydown', function (e) {
            if (e.key === 'Enter' || e.key === ' ') reset();
        });

        ['mousemove', 'keydown', 'mousedown', 'touchstart', 'scroll', 'wheel'].forEach(function (ev) {
            document.addEventListener(ev, reset, { passive: true });
        });

        reset();
    }

    /* ------------------------------------------------------------------
       10. Back-to-top Button
       Appears after scrolling 400 px. Injected into DOM automatically.
    ------------------------------------------------------------------ */

    function initBackToTop() {
        var btn = document.createElement('button');
        btn.id = 'backToTop';
        btn.type = 'button';
        btn.setAttribute('aria-label', 'Back to top');
        btn.innerHTML = '<i class="fa-solid fa-chevron-up fa-xs"></i>';
        btn.style.cssText = [
            'position:fixed', 'bottom:1.5rem', 'right:1.5rem',
            'width:36px', 'height:36px', 'border-radius:50%',
            'background:var(--admin-primary)', 'color:#fff', 'border:none',
            'cursor:pointer', 'display:none', 'align-items:center',
            'justify-content:center', 'z-index:900', 'box-shadow:var(--shadow-md)',
            'transition:opacity 0.2s,transform 0.2s', 'opacity:0'
        ].join(';');
        document.body.appendChild(btn);

        var visible = false;
        window.addEventListener('scroll', function () {
            if (window.scrollY > 400 && !visible) {
                visible = true; btn.style.display = 'flex';
                setTimeout(function () { btn.style.opacity = '1'; }, 10);
            } else if (window.scrollY <= 400 && visible) {
                visible = false; btn.style.opacity = '0';
                setTimeout(function () { btn.style.display = 'none'; }, 200);
            }
        }, { passive: true });

        btn.addEventListener('click', function () { window.scrollTo({ top: 0, behavior: 'smooth' }); });
    }

    /* ------------------------------------------------------------------
       11. Keyboard Shortcuts
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
       12. Refresh Button (topbar #btnRefresh)
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
       13. Sidebar Active State
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
       14. Boot - initialise all features
       DataTables → admin-datatable.js  (self-initializing)
       Office map → admin-map.js        (self-initializing)
    ------------------------------------------------------------------ */

    onReady(function () {
        initFooterYear();
        initTooltips();
        initAutoDismissAlerts();
        initServerToast();
        initConfirmLinks();
        initIdleOverlay();
        initBackToTop();
        initKeyboardShortcuts();
        initRefreshButton();
        initSidebarActiveState();
    });

    /* ------------------------------------------------------------------
       15. Public API - window.ui
       Used by inline page scripts via ui.toast(), ui.confirm(), etc.
       window.ui.initDataTables is assigned by admin-datatable.js.
    ------------------------------------------------------------------ */

    window.ui = window.ui || {};
    window.ui.toast        = toast;
    window.ui.toastSuccess = function (m) { toast('success', m); };
    window.ui.toastInfo    = function (m) { toast('info',    m); };
    window.ui.toastWarning = function (m) { toast('warning', m); };
    window.ui.toastError   = function (m) { toast('error',   m); };
    window.ui.confirm      = confirmDialog;

}());
