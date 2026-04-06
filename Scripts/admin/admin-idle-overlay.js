// admin-idle-overlay.js
// Shows a dim overlay after 10 minutes of inactivity. Auto-initializes on DOMContentLoaded.
(function () {
    'use strict';

    function init() {
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

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
    else init();
}());
