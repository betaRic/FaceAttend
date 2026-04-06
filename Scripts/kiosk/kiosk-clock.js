// kiosk-clock.js
// Clock display and ETA line helpers.
// Exposes window.KioskClock — must be loaded after kiosk-config.js and kiosk-state.js.
(function () {
    'use strict';

    var _ui    = null;
    var _state = null;
    var _CFG   = null;

    function nowText() {
        var d    = new Date();
        var hh   = ('0' + d.getHours()).slice(-2);
        var mm   = ('0' + d.getMinutes()).slice(-2);
        var ss   = ('0' + d.getSeconds()).slice(-2);
        var time = hh + ':' + mm + ':' + ss;

        var days = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
        var mons = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
        var date = days[d.getDay()] + ', ' + mons[d.getMonth()] + ' ' + d.getDate() + ' ' + d.getFullYear();

        if (_ui.timeLine)  _ui.timeLine.textContent  = time;
        if (_ui.dateLine)  _ui.dateLine.textContent  = date;
        if (_ui.idleClock) _ui.idleClock.textContent = time;
        if (_ui.idleDate)  _ui.idleDate.textContent  = date;
    }

    function startClock() {
        nowText();
        setInterval(nowText, 1000);
    }

    function setEta(text) {
        if (_ui.scanEtaLine) _ui.scanEtaLine.textContent = text || 'ETA: --';
    }

    function updateEta(facePresent) {
        var state = _state;
        if (!facePresent) {
            if (state.locationState === 'pending') { setEta('ETA: locating'); return; }
            if (state.locationState !== 'allowed') { setEta('ETA: blocked'); return; }
            setEta('ETA: idle');
            return;
        }

        if (state.locationState === 'pending')     { setEta('ETA: locating');    return; }
        if (state.locationState !== 'allowed')     { setEta('ETA: blocked');     return; }
        if (!state.mpBoxCanvas || state.faceStatus === 'none') { setEta('ETA: waiting');    return; }
        if (state.faceStatus === 'low')            { setEta('ETA: center face'); return; }
        if (state.faceStatus === 'multi')          { setEta('ETA: one face only'); return; }
        if (state.mpReadyToFire)                   { setEta('ETA: scanning');    return; }

        var msLeft = state.mpStableStart > 0
            ? Math.max(0, _CFG.mp.stableNeededMs - (Date.now() - state.mpStableStart))
            : _CFG.mp.stableNeededMs;

        setEta('ETA: hold (' + (msLeft / 1000).toFixed(1) + 's)');
    }

    function init(ui, state, CFG) {
        _ui    = ui;
        _state = state;
        _CFG   = CFG;
    }

    window.KioskClock = {
        init:      init,
        startClock: startClock,
        nowText:   nowText,
        setEta:    setEta,
        updateEta: updateEta
    };
})();
