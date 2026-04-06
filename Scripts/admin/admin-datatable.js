/**
 * admin-datatable.js
 * DataTables initialisation for .js-datatable elements.
 * Extracted from admin.js — auto-initializes on DOM ready.
 * Exposes window.ui.initDataTables for page-level re-init.
 */
(function () {
    'use strict';

    function initDataTables() {
        if (!window.jQuery || !window.jQuery.fn || !window.jQuery.fn.dataTable) return;

        jQuery('.js-datatable').each(function () {
            var $t = jQuery(this);
            if ($t.data('dtInit') === 1) return;

            var colCount = $t.find('thead th').length;
            if (colCount === 0) return;

            $t.data('dtInit', 1);

            var pageLen  = parseInt($t.attr('data-dt-page-length') || '25', 10);
            if (!isFinite(pageLen) || pageLen <= 0) pageLen = 25;

            var noSortLast = $t.attr('data-dt-no-sort-last') === '1';
            var stateSave  = $t.attr('data-dt-state-save')   === '1';
            var isMobile   = window.innerWidth < 768;

            // Disable Responsive on narrow tables (≤ 2 columns) — the plugin
            // crashes when it tries to hide columns that don't exist.
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

            if (isMobile) {
                opts.dom = "<'row'<'col-12 mb-2'f>>"
                         + "<'row'<'col-12'tr>>"
                         + "<'row mt-2'<'col-6 text-muted small'i><'col-6'p>>";
            } else {
                opts.dom = "<'row g-2 align-items-center mb-1'<'col-sm-6'B><'col-sm-6'f>>"
                         + "<'row'<'col-12'tr>>"
                         + "<'row g-2 align-items-center mt-1'<'col-sm-5 text-muted small'i><'col-sm-7'p>>";
            }

            if (window.jQuery.fn.dataTable && window.jQuery.fn.dataTable.Buttons && !isMobile) {
                opts.buttons = {
                    dom: {
                        button:    { className: 'btn btn-sm btn-outline-secondary' },
                        container: { className: 'dt-buttons btn-group flex-wrap gap-1' }
                    },
                    buttons: [
                        { extend: 'copy',  text: '<i class="fa-solid fa-copy fa-xs me-1"></i>Copy',   className: 'btn btn-sm btn-outline-secondary' },
                        { extend: 'csv',   text: '<i class="fa-solid fa-file-csv fa-xs me-1"></i>CSV', className: 'btn btn-sm btn-outline-secondary' },
                        { extend: 'print', text: '<i class="fa-solid fa-print fa-xs me-1"></i>Print',  className: 'btn btn-sm btn-outline-secondary' }
                    ]
                };
            } else if (window.jQuery.fn.dataTable && window.jQuery.fn.dataTable.Buttons && isMobile) {
                opts.buttons = {
                    dom: { button: { className: 'btn btn-sm btn-outline-secondary' } },
                    buttons: [{ extend: 'csv', text: '<i class="fa-solid fa-file-csv fa-xs"></i>', titleAttr: 'Export CSV', className: 'btn btn-sm btn-outline-secondary' }]
                };
            } else {
                opts.dom = opts.dom.replace("<'col-sm-6'B>", "<'col-sm-6'l>");
            }

            // Clamp responsivePriority to valid column indices.
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

            try {
                var dt = $t.DataTable(opts);
                $t.data('DataTable', dt);

                $t.closest('.dataTables_wrapper').find('.dataTables_filter input')
                    .addClass('form-control form-control-sm')
                    .css('margin-left', '0.5rem');

            } catch (err) {
                console.warn('[admin-datatable] init failed:', $t.attr('id') || '(unknown)', err.message);
                $t.data('dtInit', 0);
            }
        });
    }

    // Auto-init on DOM ready
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', initDataTables);
    } else {
        initDataTables();
    }

    // Expose for page-level re-init
    window.ui = window.ui || {};
    window.ui.initDataTables = initDataTables;

}());
