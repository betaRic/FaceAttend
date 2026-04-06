// admin-confirm-links.js
// Intercepts [data-confirm] clicks and shows a confirmation dialog before proceeding.
// Auto-initializes on DOMContentLoaded. Requires window.ui.confirm (from notify.js).
(function () {
    'use strict';

    function init() {
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

            window.ui.confirm({ title: title, text: text, icon: icon })
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

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
    else init();
}());
