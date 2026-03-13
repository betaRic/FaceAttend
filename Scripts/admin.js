/* ==========================================================================
   FaceAttend Admin Scripts - Consolidated
   ES5 Compatible Version for WebGrease Minifier
   ========================================================================== */

(function () {
    'use strict';

    /* ----------------------------------------------------------------------
       1. Utility Functions
       ---------------------------------------------------------------------- */
    function onReady(fn) {
        if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', fn);
        else fn();
    }

    function $(selector) { return document.querySelector(selector); }
    function $$(selector) { 
        var elements = document.querySelectorAll(selector);
        var arr = [];
        for (var i = 0; i < elements.length; i++) {
            arr.push(elements[i]);
        }
        return arr;
    }

    /* ----------------------------------------------------------------------
       2. UI Components
       ---------------------------------------------------------------------- */
    
    // Toast notifications
    function toast(type, message) {
        var msg = (message || '').toString();
        if (!msg) return;

        if (window.toastr) {
            toastr.options = {
                closeButton: true,
                newestOnTop: true,
                progressBar: false,
                positionClass: 'toast-top-right',
                preventDuplicates: true,
                timeOut: 3500,
                extendedTimeOut: 1200,
                showDuration: 120,
                hideDuration: 120
            };
            toastr[type](msg);
            return;
        }

        // Fallback
        if (type === 'error') alert(msg);
        else console.log(msg);
    }

    // Confirmation dialog
    function confirmDialog(opts) {
        var o = opts || {};
        var title = (o.title || 'Confirm').toString();
        var text = (o.text || '').toString();

        if (window.Swal && window.Swal.fire) {
            return Swal.fire({
                title: title,
                text: text,
                icon: o.icon || 'warning',
                showCancelButton: true,
                confirmButtonText: o.okText || 'Continue',
                cancelButtonText: o.cancelText || 'Cancel',
                focusCancel: true
            }).then(function(r) { 
                return r && r.isConfirmed; 
            });
        }

        // Fallback
        return Promise.resolve(confirm(text ? (title + '\n\n' + text) : title));
    }

    /* ----------------------------------------------------------------------
       3. Feature: Footer Year
       ---------------------------------------------------------------------- */
    function initFooterYear() {
        var el = $('#footerYear');
        if (el) el.textContent = new Date().getFullYear();
    }

    /* ----------------------------------------------------------------------
       4. Feature: Tooltips
       ---------------------------------------------------------------------- */
    function initTooltips() {
        if (!window.bootstrap || !window.bootstrap.Tooltip) return;
        $$('[data-bs-toggle="tooltip"]').forEach(function(el) { 
            new bootstrap.Tooltip(el); 
        });
    }

    /* ----------------------------------------------------------------------
       6. Feature: Toast from Server
       ---------------------------------------------------------------------- */
    function initServerToast() {
        if (window.__toastMsg) {
            var validTypes = ['success', 'info', 'warning', 'error'];
            var type = validTypes.indexOf(window.__toastType) >= 0
                ? window.__toastType
                : 'success';
            toast(type, window.__toastMsg);
            window.__toastMsg = null;
            window.__toastType = null;
        }
    }

    /* ----------------------------------------------------------------------
       7. Feature: Confirm Links
       ---------------------------------------------------------------------- */
    function initConfirmLinks() {
        if (!window.jQuery) return;

        jQuery(document).on('click', '[data-confirm]', function (e) {
            var el = this;
            if (el.dataset && el.dataset.confirmed === '1') {
                el.dataset.confirmed = '0';
                return true;
            }

            var title = el.getAttribute('data-confirm') || 'Confirm';
            var text = el.getAttribute('data-confirm-text') || '';
            var icon = el.getAttribute('data-confirm-icon') || 'warning';

            e.preventDefault();

            confirmDialog({ title: title, text: text, icon: icon }).then(function(ok) {
                if (!ok) return;

                if (el.tagName === 'A' && el.href) {
                    location.href = el.href;
                    return;
                }

                var form = el.closest && el.closest('form');
                if (form) {
                    form.submit();
                    return;
                }

                el.dataset.confirmed = '1';
                el.click();
            });

            return false;
        });
    }

    /* ----------------------------------------------------------------------
       8. Feature: DataTables (Mobile-Responsive)
       ---------------------------------------------------------------------- */
    function initDataTables() {
        if (!window.jQuery || !window.jQuery.fn || !window.jQuery.fn.dataTable) return;

        jQuery('.js-datatable').each(function () {
            var $t = jQuery(this);
            if ($t.data('dtInit') === 1) return;
            
            // Ensure element exists and is visible before initializing
            if (!$t.length || !$t[0] || $t.is(':hidden')) return;
            
            // Check if the table has any rows/columns to prevent DataTables errors
            if ($t.find('tbody tr').length === 0 && $t.find('thead th').length === 0) return;
            
            $t.data('dtInit', 1);

            var pageLen = parseInt($t.attr('data-dt-page-length') || '25', 10);
            if (!isFinite(pageLen) || pageLen <= 0) pageLen = 25;

            var noSortLast = $t.attr('data-dt-no-sort-last') === '1';
            // PERFORMANCE FIX: Disable stateSave by default to speed up navigation
            // Only enable if explicitly requested via data-dt-state-save="1"
            var stateSave = $t.attr('data-dt-state-save') === '1';

            // Responsive layout based on screen size
            var isMobile = window.innerWidth < 768;
            var isTablet = window.innerWidth >= 768 && window.innerWidth < 1024;

            var opts = {
                pageLength: pageLen,
                stateSave: stateSave,
                // PERFORMANCE FIX: Disable deferred render to prevent slow initial loads
                deferRender: false,
                // PERFORMANCE FIX: Use simpler ordering
                order: [],
                autoWidth: false,
                responsive: true,
                // PERFORMANCE FIX: Limit search delay
                searchDelay: 200,
                language: {
                    search: '',
                    searchPlaceholder: 'Search...',
                    emptyTable: 'No data available',
                    info: '_START_ - _END_ of _TOTAL_',
                    infoEmpty: '0 - 0 of 0',
                    paginate: {
                        first: '<i class="fa-solid fa-angle-double-left"></i>',
                        last: '<i class="fa-solid fa-angle-double-right"></i>',
                        next: '<i class="fa-solid fa-angle-right"></i>',
                        previous: '<i class="fa-solid fa-angle-left"></i>'
                    }
                }
            };

            // Mobile-optimized DOM structure
            if (isMobile) {
                opts.dom = "<'row'<'col-12 mb-2'f>>" +
                          "<'row'<'col-12'tr>>" +
                          "<'row'<'col-12 mt-2 d-flex justify-content-between align-items-center'ip>>";
            } else if (isTablet) {
                opts.dom = "<'row g-2 align-items-center'<'col-sm-6'B><'col-sm-6'f>>" +
                          "<'row'<'col-12'tr>>" +
                          "<'row g-2 align-items-center'<'col-sm-5'i><'col-sm-7'p>>";
            } else {
                opts.dom = "<'row g-2 align-items-center'<'col-sm-6'B><'col-sm-6'f>>" +
                          "<'row'<'col-12'tr>>" +
                          "<'row g-2 align-items-center'<'col-sm-5'i><'col-sm-7'p>>";
            }

            // Configure buttons with mobile-friendly options
            if (jQuery.fn.dataTable.Buttons && !isMobile) {
                opts.buttons = {
                    dom: {
                        button: { className: 'btn btn-sm btn-outline-secondary' },
                        container: { className: 'dt-buttons btn-group flex-wrap' }
                    },
                    buttons: [
                        { 
                            extend: 'copy', 
                            text: '<i class="fa-solid fa-copy me-1"></i>Copy',
                            className: 'btn btn-sm btn-outline-secondary'
                        },
                        { 
                            extend: 'csv', 
                            text: '<i class="fa-solid fa-file-csv me-1"></i>CSV',
                            className: 'btn btn-sm btn-outline-secondary'
                        },
                        { 
                            extend: 'print', 
                            text: '<i class="fa-solid fa-print me-1"></i>Print',
                            className: 'btn btn-sm btn-outline-secondary'
                        }
                    ]
                };
            } else if (jQuery.fn.dataTable.Buttons && isMobile) {
                opts.buttons = {
                    dom: {
                        button: { className: 'btn btn-sm btn-outline-secondary' },
                        container: { className: 'dt-buttons btn-group' }
                    },
                    buttons: [
                        { 
                            extend: 'csv', 
                            text: '<i class="fa-solid fa-file-csv"></i>',
                            titleAttr: 'Export CSV',
                            className: 'btn btn-sm btn-outline-secondary'
                        }
                    ]
                };
            } else {
                opts.dom = opts.dom.replace("<'col-sm-6'B>", "<'col-sm-6'l>");
            }

            if (noSortLast) {
                opts.columnDefs = [
                    { targets: -1, orderable: false, searchable: false },
                    { responsivePriority: 1, targets: 0 },
                    { responsivePriority: 2, targets: 1 },
                    { responsivePriority: 10000, targets: -1 }
                ];
            } else {
                opts.columnDefs = [
                    { responsivePriority: 1, targets: 0 },
                    { responsivePriority: 2, targets: 1 },
                    { responsivePriority: 10000, targets: -1 }
                ];
            }

            try {
                // PERFORMANCE FIX: Use requestIdleCallback if available for non-blocking init
                var scheduleInit = window.requestIdleCallback || function(cb) { setTimeout(cb, 1); };
                scheduleInit(function() {
                    try {
                        var dt = $t.DataTable(opts);
                        $t.data('DataTable', dt);
                    } catch (innerErr) {
                        console.warn('DataTables init failed for table:', $t.attr('id') || 'unknown', innerErr);
                    }
                });
            } catch (err) {
                console.warn('DataTables init failed', err);
            }
        });
    }

    /* ----------------------------------------------------------------------
       9. Feature: Idle Overlay
       ---------------------------------------------------------------------- */
    function initIdleOverlay() {
        var overlay = $('#idleOverlay');
        if (!overlay) return;

        var IDLE_MS = 10 * 60 * 1000;
        var t = null;

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
            if (t) clearTimeout(t);
            t = setTimeout(show, IDLE_MS);
        }

        overlay.addEventListener('click', reset);
        var events = ['mousemove', 'keydown', 'mousedown', 'touchstart', 'scroll'];
        for (var i = 0; i < events.length; i++) {
            document.addEventListener(events[i], reset, { passive: true });
        }

        reset();
    }

    /* ----------------------------------------------------------------------
       10. Feature: Office Map (Consolidated from admin-office-map.js)
       ---------------------------------------------------------------------- */
    function initOfficeMap() {
        if (typeof L === 'undefined') return;

        var mapEl = $('#map');
        if (!mapEl) return;

        var latEl = $('#lat');
        var lonEl = $('#lon');
        var radiusEl = $('#RadiusMeters');
        if (!latEl || !lonEl || !radiusEl) return;

        var btnMyLoc = $('#btnUseMyLocation');
        var fallbackLat = parseFloat(mapEl.dataset.fallbackLat) || 6.116386;
        var fallbackLon = parseFloat(mapEl.dataset.fallbackLon) || 125.171617;

        var lat = parseFloat(latEl.value);
        var lon = parseFloat(lonEl.value);
        if (!isFinite(lat)) lat = fallbackLat;
        if (!isFinite(lon)) lon = fallbackLon;

        var radius = parseInt(radiusEl.value, 10);
        if (!isFinite(radius) || radius <= 0) radius = 100;

        var pos = L.latLng(lat, lon);
        var map = L.map('map');

        var tiles = L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
            maxZoom: 19,
            attribution: '&copy; OpenStreetMap contributors',
            crossOrigin: 'anonymous'
        });

        tiles.on('tileerror', function () {
            if (!mapEl.dataset.tileWarned) {
                mapEl.dataset.tileWarned = '1';
                console.warn('Map tiles blocked by CORS. Form still works; enter coordinates manually.');
            }
        });

        tiles.addTo(map);
        map.setView(pos, 16);

        var marker = L.marker(pos, { draggable: true }).addTo(map);
        var circle = L.circle(pos, {
            radius: radius,
            color: '#0d6efd',
            fillColor: '#0d6efd',
            fillOpacity: 0.10,
            weight: 2
        }).addTo(map);

        function syncInputs(a, b) {
            latEl.value = a.toFixed(7);
            lonEl.value = b.toFixed(7);
        }

        function syncMap() {
            var a = parseFloat(latEl.value);
            var b = parseFloat(lonEl.value);
            if (!isFinite(a) || !isFinite(b)) return;
            var p = L.latLng(a, b);
            marker.setLatLng(p);
            circle.setLatLng(p);
            map.panTo(p);
        }

        function syncRadius() {
            var r = parseInt(radiusEl.value, 10);
            if (!isFinite(r) || r <= 0) return;
            circle.setRadius(r);
        }

        map.on('click', function(e) {
            marker.setLatLng(e.latlng);
            circle.setLatLng(e.latlng);
            syncInputs(e.latlng.lat, e.latlng.lng);
        });

        marker.on('drag', function(e) {
            var p = e.target.getLatLng();
            circle.setLatLng(p);
            syncInputs(p.lat, p.lng);
        });

        latEl.addEventListener('change', syncMap);
        lonEl.addEventListener('change', syncMap);
        radiusEl.addEventListener('change', syncRadius);

        if (btnMyLoc && navigator.geolocation) {
            btnMyLoc.addEventListener('click', function() {
                btnMyLoc.disabled = true;
                btnMyLoc.textContent = 'Locating...';

                navigator.geolocation.getCurrentPosition(
                    function(p) {
                        btnMyLoc.disabled = false;
                        btnMyLoc.textContent = 'Use my location';

                        if (p.coords.accuracy > 500) {
                            var proceed = confirm(
                                'GPS accuracy is low (' + Math.round(p.coords.accuracy) + ' m).\n\n' +
                                'Your network may be geolocating to the wrong city.\n\n' +
                                'Use this location anyway?'
                            );
                            if (!proceed) return;
                        }

                        syncInputs(p.coords.latitude, p.coords.longitude);
                        syncMap();
                    },
                    function(err) {
                        btnMyLoc.disabled = false;
                        btnMyLoc.textContent = 'Use my location';

                        var msgs = {
                            1: 'Location access denied. Enable it in browser settings.',
                            2: 'Location unavailable. Drag the pin manually.',
                            3: 'Location timed out. Drag the pin manually.'
                        };
                        alert(msgs[err && err.code] || 'Location error.');
                    },
                    { enableHighAccuracy: true, timeout: 12000, maximumAge: 0 }
                );
            });
        }
    }

    /* ----------------------------------------------------------------------
       11. Initialize Everything
       ---------------------------------------------------------------------- */
    onReady(function() {
        initFooterYear();
        initTooltips();
        initServerToast();
        initConfirmLinks();
        initDataTables();
        initIdleOverlay();
        initOfficeMap();
    });

    /* ----------------------------------------------------------------------
       12. Public API
       ---------------------------------------------------------------------- */
    window.ui = window.ui || {};
    window.ui.toast = toast;
    window.ui.toastSuccess = function(m) { toast('success', m); };
    window.ui.toastInfo = function(m) { toast('info', m); };
    window.ui.toastWarning = function(m) { toast('warning', m); };
    window.ui.toastError = function(m) { toast('error', m); };
    window.ui.confirm = confirmDialog;

})();
