// kiosk-warmup.js
// Polls the server /Health endpoint until the biometric worker is ready.
// Exposes window.KioskWarmup — must be loaded after kiosk-state.js.
(function () {
    'use strict';

    var _appBase = null;
    var _state   = null;

    function pollServerReady() {
        fetch(_appBase + 'Health', {
            method: 'GET',
            credentials: 'same-origin',
            headers: { 'X-Requested-With': 'XMLHttpRequest' }
        })
        .then(function (r) { return r.ok ? r.json() : null; })
        .then(function (j) {
            if (j && j.warmUpState === 1) {
                _state.serverReady = true;
            } else {
                setTimeout(pollServerReady, 2000);
            }
        })
        .catch(function () {
            setTimeout(pollServerReady, 3000);
        });
    }

    function start(appBase, state) {
        _appBase = appBase;
        _state   = state;
        // Give IIS time to respond to the first request before polling
        setTimeout(pollServerReady, 500);
    }

    window.KioskWarmup = { start: start };
})();
