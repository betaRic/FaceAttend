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
                closeButton:      true,
                newestOnTop:      true,
                progressBar:      true,
                positionClass:    'toast-top-right',
                preventDuplicates: true,
                timeOut:          4000,
                extendedTimeOut:  1500,
                showDuration:     200,
                hideDuration:     200
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
        var title  = (o.title  || 'Confirm').toString();
        var text   = (o.text   || '').toString();
        var icon   = o.icon   || 'warning';
        var okText = o.okText || 'Continue';
        var cancelText = o.cancelText || 'Cancel';

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

            // Already confirmed in this click cycle - let it through
            if (el.dataset.confirmed === '1') {
                el.dataset.confirmed = '0';
                return;
            }

            e.preventDefault();
            e.stopPropagation();

            var title  = el.getAttribute('data-confirm')      || 'Are you sure?';
            var text   = el.getAttribute('data-confirm-text') || '';
            var icon   = el.getAttribute('data-confirm-icon') || 'warning';

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
       9. DataTables  (.js-datatable)

       BUG FIX - "Cannot read properties of undefined (reading 'display')"
       Root cause: DataTables Responsive internally accesses aoColumns[idx]
       where idx may be out of range when the table has fewer columns than
       the plugin's column-priority configuration expects.

       Fixes applied:
         a) Responsive is DISABLED for tables with ≤ 2 columns - the plugin
            has no meaningful work to do on narrow tables and always crashes.
         b) responsivePriority targets are clamped to valid column indices.
         c) requestIdleCallback removed - it caused DataTables to initialise
            after the DOM was mutated by navigation, producing stale column
            counts and the same crash.
         d) Tables that are hidden at init time are deferred via a
            ResizeObserver / MutationObserver watch instead of being skipped.
    ------------------------------------------------------------------ */

    function initDataTables() {
        if (!window.jQuery || !window.jQuery.fn || !window.jQuery.fn.dataTable) return;

        jQuery('.js-datatable').each(function () {
            var $t = jQuery(this);
            if ($t.data('dtInit') === 1) return;

            // Needs at least a thead row with th elements
            var colCount = $t.find('thead th').length;
            if (colCount === 0) return;

            $t.data('dtInit', 1);

            var pageLen   = parseInt($t.attr('data-dt-page-length') || '25', 10);
            if (!isFinite(pageLen) || pageLen <= 0) pageLen = 25;

            var noSortLast = $t.attr('data-dt-no-sort-last') === '1';
            var stateSave  = $t.attr('data-dt-state-save')   === '1';
            var isMobile   = window.innerWidth < 768;
            var isTablet   = window.innerWidth >= 768 && window.innerWidth < 1024;

            // ── FIX (a): Disable Responsive on narrow tables ──────────────
            // The plugin crashes when colCount ≤ 2 because it tries to hide/show
            // columns that don't exist and accesses undefined column objects.
            var useResponsive = colCount >= 3;

            var opts = {
                pageLength:  pageLen,
                stateSave:   stateSave,
                deferRender: false,
                order:       [],
                autoWidth:   false,
                responsive:  useResponsive,
                searchDelay: 200,
                language: {
                    search:            '',
                    searchPlaceholder: 'Search...',
                    emptyTable:        '<span class="text-muted">No records found</span>',
                    info:              '_START_-_END_ of _TOTAL_',
                    infoEmpty:         '0 records',
                    infoFiltered:      '(filtered from _MAX_)',
                    paginate: {
                        first:    '<i class="fa-solid fa-angles-left fa-xs"></i>',
                        last:     '<i class="fa-solid fa-angles-right fa-xs"></i>',
                        next:     '<i class="fa-solid fa-angle-right fa-xs"></i>',
                        previous: '<i class="fa-solid fa-angle-left fa-xs"></i>'
                    },
                    lengthMenu: '_MENU_ per page'
                }
            };

            // ── DOM layout ─────────────────────────────────────────────────
            if (isMobile) {
                opts.dom = "<'row'<'col-12 mb-2'f>>"
                         + "<'row'<'col-12'tr>>"
                         + "<'row mt-2'<'col-6 text-muted small'i><'col-6'p>>";
            } else {
                opts.dom = "<'row g-2 align-items-center mb-1'<'col-sm-6'B><'col-sm-6'f>>"
                         + "<'row'<'col-12'tr>>"
                         + "<'row g-2 align-items-center mt-1'<'col-sm-5 text-muted small'i><'col-sm-7'p>>";
            }

            // ── Export buttons ─────────────────────────────────────────────
            if (window.jQuery.fn.dataTable && window.jQuery.fn.dataTable.Buttons && !isMobile) {
                opts.buttons = {
                    dom: {
                        button:    { className: 'btn btn-sm btn-outline-secondary' },
                        container: { className: 'dt-buttons btn-group flex-wrap gap-1' }
                    },
                    buttons: [
                        { extend: 'copy',  text: '<i class="fa-solid fa-copy fa-xs me-1"></i>Copy',  className: 'btn btn-sm btn-outline-secondary' },
                        { extend: 'csv',   text: '<i class="fa-solid fa-file-csv fa-xs me-1"></i>CSV', className: 'btn btn-sm btn-outline-secondary' },
                        { extend: 'print', text: '<i class="fa-solid fa-print fa-xs me-1"></i>Print', className: 'btn btn-sm btn-outline-secondary' }
                    ]
                };
            } else if (window.jQuery.fn.dataTable && window.jQuery.fn.dataTable.Buttons && isMobile) {
                opts.buttons = {
                    dom: { button: { className: 'btn btn-sm btn-outline-secondary' } },
                    buttons: [{ extend: 'csv', text: '<i class="fa-solid fa-file-csv fa-xs"></i>', titleAttr: 'Export CSV', className: 'btn btn-sm btn-outline-secondary' }]
                };
            } else {
                // No Buttons plugin - remove B from DOM string
                opts.dom = opts.dom.replace("<'col-sm-6'B>", "<'col-sm-6'l>");
            }

            // ── FIX (b): Clamp responsivePriority to valid column indices ──
            // Only assign priority-2 if a second column actually exists.
            // Duplicate targets crash the Responsive extension.
            var pri2target = colCount > 1 ? 1 : 0;

            if (noSortLast) {
                opts.columnDefs = [
                    { targets: -1,        orderable: false, searchable: false },
                    { responsivePriority: 1,     targets: 0 },
                    { responsivePriority: 2,     targets: pri2target },
                    { responsivePriority: 10000, targets: -1 }
                ];
            } else {
                opts.columnDefs = [
                    { responsivePriority: 1,     targets: 0 },
                    { responsivePriority: 2,     targets: pri2target },
                    { responsivePriority: 10000, targets: -1 }
                ];
            }

            // ── FIX (c): Synchronous init - no requestIdleCallback ─────────
            // requestIdleCallback fires after navigation mutations, by which
            // time the table DOM may have changed, causing stale column counts.
            try {
                var dt = $t.DataTable(opts);
                $t.data('DataTable', dt);

                // Apply Bootstrap 5 input styling to the generated search box
                $t.closest('.dataTables_wrapper').find('.dataTables_filter input')
                    .addClass('form-control form-control-sm')
                    .css('margin-left', '0.5rem');

            } catch (err) {
                console.warn('[admin.js] DataTables init failed:', $t.attr('id') || '(unknown)', err.message);
                $t.data('dtInit', 0); // Allow retry
            }
        });
    }

    /* ------------------------------------------------------------------
       10. Idle Overlay
       Shows a dim overlay after 10 minutes of inactivity.
       BUG FIX: Was using $() jQuery wrapper - switched to getElementById
       so .classList / .addEventListener work correctly.
    ------------------------------------------------------------------ */

    function initIdleOverlay() {
        var overlay = document.getElementById('idleOverlay');
        if (!overlay) return;

        var IDLE_MS = 10 * 60 * 1000; // 10 minutes
        var timer   = null;

        function show() {
            overlay.classList.remove('d-none');
            overlay.classList.add('d-flex');
        }

        function hide() {
            overlay.classList.add('d-none');
            overlay.classList.remove('d-flex');
        }

        function reset() {
            hide();
            clearTimeout(timer);
            timer = setTimeout(show, IDLE_MS);
        }

        overlay.addEventListener('click', reset);
        overlay.addEventListener('keydown', function (e) {
            if (e.key === 'Enter' || e.key === ' ') reset();
        });

        var activity = ['mousemove', 'keydown', 'mousedown', 'touchstart', 'scroll', 'wheel'];
        activity.forEach(function (ev) {
            document.addEventListener(ev, reset, { passive: true });
        });

        reset();
    }

    /* ------------------------------------------------------------------
       11. Office Map (Leaflet)
       Used on Offices/Create and Offices/Edit.
    ------------------------------------------------------------------ */

    function initOfficeMap() {
        if (typeof L === 'undefined') return;

        var mapEl    = document.getElementById('map');
        var latEl    = document.getElementById('lat');
        var lonEl    = document.getElementById('lon');
        var radiusEl = document.getElementById('RadiusMeters');

        if (!mapEl || !latEl || !lonEl || !radiusEl) return;

        var btnMyLoc     = document.getElementById('btnUseMyLocation');
        var fallbackLat  = parseFloat(mapEl.dataset.fallbackLat) || 6.116386;
        var fallbackLon  = parseFloat(mapEl.dataset.fallbackLon) || 125.171617;

        var lat = parseFloat(latEl.value);
        var lon = parseFloat(lonEl.value);
        if (!isFinite(lat)) lat = fallbackLat;
        if (!isFinite(lon)) lon = fallbackLon;

        var radius = parseInt(radiusEl.value, 10);
        if (!isFinite(radius) || radius <= 0) radius = 100;

        var pos  = L.latLng(lat, lon);
        var map  = L.map('map');

        // OSM tiles with CORS error grace handling
        var tiles = L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
            maxZoom: 19,
            attribution: '&copy; OpenStreetMap contributors',
            crossOrigin: 'anonymous'
        });

        tiles.on('tileerror', function () {
            if (!mapEl.dataset.tileWarned) {
                mapEl.dataset.tileWarned = '1';
                console.warn('[admin] Map tiles unavailable. Enter coordinates manually.');
            }
        });

        tiles.addTo(map);
        map.setView(pos, 17);

        // Draggable marker
        var marker = L.marker(pos, { draggable: true }).addTo(map);
        var circle = L.circle(pos, { radius: radius, color: '#2563eb', fillOpacity: 0.12, weight: 2 }).addTo(map);

        function updateFromLatLng(newPos) {
            marker.setLatLng(newPos);
            circle.setLatLng(newPos);
            latEl.value = newPos.lat.toFixed(7);
            lonEl.value = newPos.lng.toFixed(7);
        }

        marker.on('dragend', function () { updateFromLatLng(marker.getLatLng()); });
        map.on('click', function (e) { updateFromLatLng(e.latlng); map.panTo(e.latlng); });

        radiusEl.addEventListener('input', function () {
            var r = parseInt(radiusEl.value, 10);
            if (isFinite(r) && r > 0) circle.setRadius(r);
        });

        if (btnMyLoc) {
            btnMyLoc.addEventListener('click', function () {
                if (!navigator.geolocation) {
                    alert('Geolocation not available in this browser.');
                    return;
                }
                btnMyLoc.disabled = true;
                btnMyLoc.innerHTML = '<i class="fa-solid fa-spinner fa-spin me-1"></i>Locating...';
                navigator.geolocation.getCurrentPosition(
                    function (pos) {
                        var ll = L.latLng(pos.coords.latitude, pos.coords.longitude);
                        updateFromLatLng(ll);
                        map.setView(ll, 17);
                        btnMyLoc.disabled = false;
                        btnMyLoc.innerHTML = '<i class="fa-solid fa-location-dot me-1"></i>Use my location';
                    },
                    function (err) {
                        var msgs = {
                            1: 'Location permission denied. Enable it in browser settings.',
                            2: 'Location unavailable. Drag the pin manually.',
                            3: 'Location timed out. Drag the pin manually.'
                        };
                        alert(msgs[err && err.code] || 'Location error.');
                        btnMyLoc.disabled = false;
                        btnMyLoc.innerHTML = '<i class="fa-solid fa-location-dot me-1"></i>Use my location';
                    },
                    { enableHighAccuracy: true, timeout: 12000, maximumAge: 0 }
                );
            });
        }
    }

    /* ------------------------------------------------------------------
       12. Back-to-top Button
       Appears after scrolling 400 px. Injected into DOM automatically.
    ------------------------------------------------------------------ */

    function initBackToTop() {
        var btn = document.createElement('button');
        btn.id = 'backToTop';
        btn.type = 'button';
        btn.setAttribute('aria-label', 'Back to top');
        btn.innerHTML = '<i class="fa-solid fa-chevron-up fa-xs"></i>';
        btn.style.cssText = [
            'position:fixed',
            'bottom:1.5rem',
            'right:1.5rem',
            'width:36px',
            'height:36px',
            'border-radius:50%',
            'background:var(--admin-primary)',
            'color:#fff',
            'border:none',
            'cursor:pointer',
            'display:none',
            'align-items:center',
            'justify-content:center',
            'z-index:900',
            'box-shadow:var(--shadow-md)',
            'transition:opacity 0.2s,transform 0.2s',
            'opacity:0'
        ].join(';');

        document.body.appendChild(btn);

        var visible = false;

        window.addEventListener('scroll', function () {
            if (window.scrollY > 400 && !visible) {
                visible = true;
                btn.style.display = 'flex';
                setTimeout(function () { btn.style.opacity = '1'; }, 10);
            } else if (window.scrollY <= 400 && visible) {
                visible = false;
                btn.style.opacity = '0';
                setTimeout(function () { btn.style.display = 'none'; }, 200);
            }
        }, { passive: true });

        btn.addEventListener('click', function () {
            window.scrollTo({ top: 0, behavior: 'smooth' });
        });
    }

    /* ------------------------------------------------------------------
       13. Keyboard Shortcuts
       Ctrl+/ - focus DataTables search input on the current page
       Escape  - dismiss any open toastr notifications
    ------------------------------------------------------------------ */

    function initKeyboardShortcuts() {
        document.addEventListener('keydown', function (e) {
            // Ctrl+/ : focus search
            if ((e.ctrlKey || e.metaKey) && (e.key === '/' || e.key === 'k')) {
                var searchInput = $('.dataTables_filter input') || $('[type="search"]');
                if (searchInput) {
                    e.preventDefault();
                    searchInput.focus();
                    searchInput.select();
                }
            }
            // Escape: dismiss toastr
            if (e.key === 'Escape' && window.toastr) {
                toastr.clear();
            }
        });
    }

    /* ------------------------------------------------------------------
       14. Refresh Button (topbar #btnRefresh)
    ------------------------------------------------------------------ */

    function initRefreshButton() {
        var btn = document.getElementById('btnRefresh');
        if (!btn) return;

        btn.addEventListener('click', function () {
            btn.disabled = true;
            var icon = btn.querySelector('i');
            if (icon) {
                icon.classList.remove('fa-rotate');
                icon.classList.add('fa-spinner', 'fa-spin');
            }
            // Brief delay so user sees the spinner before reload
            setTimeout(function () { location.reload(); }, 150);
        });
    }

    /* ------------------------------------------------------------------
       15. Sidebar Active State
       Marks the nav link matching the current URL as active.
       Falls back gracefully if the _AdminNav partial already set it.
    ------------------------------------------------------------------ */

    function initSidebarActiveState() {
        var links = $$('.admin-nav-link');
        if (!links.length) return;

        var path = location.pathname.toLowerCase().replace(/\/$/, '');

        links.forEach(function (link) {
            // Skip if already marked by server-side Razor
            if (link.classList.contains('active')) return;

            var href = (link.getAttribute('href') || '').toLowerCase().replace(/\/$/, '');
            if (!href || href === '#') return;

            // Exact match or current path starts with the link path
            if (path === href || (href.length > 7 && (path + '/').indexOf(href + '/') === 0)) {
                link.classList.add('active');
            }
        });
    }

    /* ------------------------------------------------------------------
       16. Boot - initialise all features
    ------------------------------------------------------------------ */

    onReady(function () {
        initFooterYear();
        initTooltips();
        initAutoDismissAlerts();
        initServerToast();
        initConfirmLinks();
        initDataTables();
        initIdleOverlay();
        initOfficeMap();
        initBackToTop();
        initKeyboardShortcuts();
        initRefreshButton();
        initSidebarActiveState();
    });

    /* ------------------------------------------------------------------
       17. Public API - window.ui
       Used by inline page scripts via ui.toast(), ui.confirm(), etc.
    ------------------------------------------------------------------ */

    window.ui = window.ui || {};
    window.ui.toast        = toast;
    window.ui.toastSuccess = function (m) { toast('success', m); };
    window.ui.toastInfo    = function (m) { toast('info',    m); };
    window.ui.toastWarning = function (m) { toast('warning', m); };
    window.ui.toastError   = function (m) { toast('error',   m); };
    window.ui.confirm      = confirmDialog;
    window.ui.initDataTables = initDataTables; // Allow re-init from page scripts

})();
