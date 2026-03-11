/* ==========================================================================
   FaceAttend Admin Scripts - Consolidated
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
    function $$(selector) { return Array.from(document.querySelectorAll(selector)); }

    /* ----------------------------------------------------------------------
       2. UI Components
       ---------------------------------------------------------------------- */
    
    // Toast notifications
    function toast(type, message) {
        const msg = (message || '').toString();
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
        const o = opts || {};
        const title = (o.title || 'Confirm').toString();
        const text = (o.text || '').toString();

        if (window.Swal?.fire) {
            return Swal.fire({
                title: title,
                text: text,
                icon: o.icon || 'warning',
                showCancelButton: true,
                confirmButtonText: o.okText || 'Continue',
                cancelButtonText: o.cancelText || 'Cancel',
                focusCancel: true
            }).then(r => r?.isConfirmed);
        }

        // Fallback
        return Promise.resolve(confirm(text ? (title + '\n\n' + text) : title));
    }

    /* ----------------------------------------------------------------------
       3. Feature: Footer Year
       ---------------------------------------------------------------------- */
    function initFooterYear() {
        const el = $('#footerYear');
        if (el) el.textContent = new Date().getFullYear();
    }

    /* ----------------------------------------------------------------------
       4. Feature: Tooltips
       ---------------------------------------------------------------------- */
    function initTooltips() {
        if (!window.bootstrap?.Tooltip) return;
        $$('[data-bs-toggle="tooltip"]').forEach(el => new bootstrap.Tooltip(el));
    }

    /* ----------------------------------------------------------------------
       6. Feature: Toast from Server
       ---------------------------------------------------------------------- */
    function initServerToast() {
        if (window.__toastMsg) {
            const type = ['success', 'info', 'warning', 'error'].includes(window.__toastType)
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
            const el = this;
            if (el.dataset?.confirmed === '1') {
                el.dataset.confirmed = '0';
                return true;
            }

            const title = el.getAttribute('data-confirm') || 'Confirm';
            const text = el.getAttribute('data-confirm-text') || '';
            const icon = el.getAttribute('data-confirm-icon') || 'warning';

            e.preventDefault();

            confirmDialog({ title, text, icon }).then(ok => {
                if (!ok) return;

                if (el.tagName === 'A' && el.href) {
                    location.href = el.href;
                    return;
                }

                const form = el.closest?.('form');
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
        if (!window.jQuery?.fn?.dataTable) return;

        jQuery('.js-datatable').each(function () {
            const $t = jQuery(this);
            if ($t.data('dtInit') === 1) return;
            $t.data('dtInit', 1);

            let pageLen = parseInt($t.attr('data-dt-page-length') || '25', 10);
            if (!isFinite(pageLen) || pageLen <= 0) pageLen = 25;

            const noSortLast = $t.attr('data-dt-no-sort-last') === '1';
            const stateSave = $t.attr('data-dt-state-save') === '1';

            // Responsive layout based on screen size
            const isMobile = window.innerWidth < 768;
            const isTablet = window.innerWidth >= 768 && window.innerWidth < 1024;

            const opts = {
                pageLength: pageLen,
                stateSave: stateSave,
                autoWidth: false,
                order: [],
                responsive: true, // Enable DataTables responsive plugin
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
                // Mobile: Stack everything vertically, compact buttons
                opts.dom = "<'row'<'col-12 mb-2'f>>" +
                          "<'row'<'col-12'tr>>" +
                          "<'row'<'col-12 mt-2 d-flex justify-content-between align-items-center'ip>>";
            } else if (isTablet) {
                // Tablet: 2-column layout
                opts.dom = "<'row g-2 align-items-center'<'col-sm-6'B><'col-sm-6'f>>" +
                          "<'row'<'col-12'tr>>" +
                          "<'row g-2 align-items-center'<'col-sm-5'i><'col-sm-7'p>>";
            } else {
                // Desktop: Full layout
                opts.dom = "<'row g-2 align-items-center'<'col-sm-6'B><'col-sm-6'f>>" +
                          "<'row'<'col-12'tr>>" +
                          "<'row g-2 align-items-center'<'col-sm-5'i><'col-sm-7'p>>";
            }

            // Configure buttons with mobile-friendly options
            if (jQuery.fn.dataTable.Buttons && !isMobile) {
                opts.buttons = {
                    dom: {
                        button: {
                            className: 'btn btn-sm btn-outline-secondary'
                        },
                        container: {
                            className: 'dt-buttons btn-group flex-wrap'
                        }
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
                // Mobile: Compact button group
                opts.buttons = {
                    dom: {
                        button: {
                            className: 'btn btn-sm btn-outline-secondary'
                        },
                        container: {
                            className: 'dt-buttons btn-group'
                        }
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
                    { responsivePriority: 1, targets: 0 }, // Always show first column
                    { responsivePriority: 2, targets: 1 }, // Priority for second column
                    { responsivePriority: 10000, targets: -1 } // Action column lowest priority
                ];
            } else {
                opts.columnDefs = [
                    { responsivePriority: 1, targets: 0 },
                    { responsivePriority: 2, targets: 1 },
                    { responsivePriority: 10000, targets: -1 }
                ];
            }

            try {
                const dt = $t.DataTable(opts);
                
                // Store DataTable instance for resize handling
                $t.data('DataTable', dt);
            } catch (err) {
                console.warn('DataTables init failed', err);
            }
        });
    }

    /* ----------------------------------------------------------------------
       9. Feature: Idle Overlay
       ---------------------------------------------------------------------- */
    function initIdleOverlay() {
        const overlay = $('#idleOverlay');
        if (!overlay) return;

        const IDLE_MS = 10 * 60 * 1000;
        let t = null;

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
        ['mousemove', 'keydown', 'mousedown', 'touchstart', 'scroll'].forEach(evt => {
            document.addEventListener(evt, reset, { passive: true });
        });

        reset();
    }

    /* ----------------------------------------------------------------------
       10. Feature: Office Map (Consolidated from admin-office-map.js)
       ---------------------------------------------------------------------- */
    function initOfficeMap() {
        if (typeof L === 'undefined') return;

        const mapEl = $('#map');
        if (!mapEl) return;

        const latEl = $('#lat');
        const lonEl = $('#lon');
        const radiusEl = $('#RadiusMeters');
        if (!latEl || !lonEl || !radiusEl) return;

        const btnMyLoc = $('#btnUseMyLocation');
        const fallbackLat = parseFloat(mapEl.dataset.fallbackLat) || 6.116386;
        const fallbackLon = parseFloat(mapEl.dataset.fallbackLon) || 125.171617;

        let lat = parseFloat(latEl.value);
        let lon = parseFloat(lonEl.value);
        if (!isFinite(lat)) lat = fallbackLat;
        if (!isFinite(lon)) lon = fallbackLon;

        let radius = parseInt(radiusEl.value, 10);
        if (!isFinite(radius) || radius <= 0) radius = 100;

        const pos = L.latLng(lat, lon);
        const map = L.map('map');

        const tiles = L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
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

        const marker = L.marker(pos, { draggable: true }).addTo(map);
        const circle = L.circle(pos, {
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
            const a = parseFloat(latEl.value);
            const b = parseFloat(lonEl.value);
            if (!isFinite(a) || !isFinite(b)) return;
            const p = L.latLng(a, b);
            marker.setLatLng(p);
            circle.setLatLng(p);
            map.panTo(p);
        }

        function syncRadius() {
            const r = parseInt(radiusEl.value, 10);
            if (!isFinite(r) || r <= 0) return;
            circle.setRadius(r);
        }

        map.on('click', e => {
            marker.setLatLng(e.latlng);
            circle.setLatLng(e.latlng);
            syncInputs(e.latlng.lat, e.latlng.lng);
        });

        marker.on('drag', e => {
            const p = e.target.getLatLng();
            circle.setLatLng(p);
            syncInputs(p.lat, p.lng);
        });

        latEl.addEventListener('change', syncMap);
        lonEl.addEventListener('change', syncMap);
        radiusEl.addEventListener('change', syncRadius);

        if (btnMyLoc && navigator.geolocation) {
            btnMyLoc.addEventListener('click', () => {
                btnMyLoc.disabled = true;
                btnMyLoc.textContent = 'Locating...';

                navigator.geolocation.getCurrentPosition(
                    p => {
                        btnMyLoc.disabled = false;
                        btnMyLoc.textContent = 'Use my location';

                        if (p.coords.accuracy > 500) {
                            const proceed = confirm(
                                `GPS accuracy is low (${Math.round(p.coords.accuracy)} m).\n\n` +
                                'Your network may be geolocating to the wrong city.\n\n' +
                                'Use this location anyway?'
                            );
                            if (!proceed) return;
                        }

                        syncInputs(p.coords.latitude, p.coords.longitude);
                        syncMap();
                    },
                    err => {
                        btnMyLoc.disabled = false;
                        btnMyLoc.textContent = 'Use my location';

                        const msgs = {
                            1: 'Location access denied. Enable it in browser settings.',
                            2: 'Location unavailable. Drag the pin manually.',
                            3: 'Location timed out. Drag the pin manually.'
                        };
                        alert(msgs[err?.code] || 'Location error.');
                    },
                    { enableHighAccuracy: true, timeout: 12000, maximumAge: 0 }
                );
            });
        }
    }

    /* ----------------------------------------------------------------------
       11. Initialize Everything
       ---------------------------------------------------------------------- */
    onReady(() => {
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
    window.ui.toastSuccess = m => toast('success', m);
    window.ui.toastInfo = m => toast('info', m);
    window.ui.toastWarning = m => toast('warning', m);
    window.ui.toastError = m => toast('error', m);
    window.ui.confirm = confirmDialog;

})();
