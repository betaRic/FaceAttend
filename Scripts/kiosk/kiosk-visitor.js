// kiosk-visitor.js
// Visitor log modal: open, submit, close.
// Exposes window.KioskVisitor — must be loaded after kiosk-config.js and kiosk-state.js.
(function () {
    'use strict';

    var _ui            = null;
    var _state         = null;
    var _EP            = null;
    var _token         = null;
    var _CFG           = null;
    var _setPrompt     = null;
    var _setEta        = null;
    var _armPostScanHold = null;
    var _toastSuccess  = null;
    var _toastError    = null;

    function open(payload) {
        if (_state.unlockOpen)  return;
        if (_state.visitorOpen) return;
        _state.visitorOpen    = true;
        _state.pendingVisitor = payload || null;

        if (_ui.visitorErr) _ui.visitorErr.textContent = '';

        var isKnown = !!(payload && payload.isKnown);
        var name    = (payload && payload.visitorName) ? payload.visitorName : '';

        if (_ui.visitorNameRow) _ui.visitorNameRow.classList.toggle('hidden', isKnown);
        if (_ui.visitorName) {
            _ui.visitorName.value    = name;
            _ui.visitorName.disabled = isKnown;
        }
        if (_ui.visitorPurpose) _ui.visitorPurpose.value = '';

        if (_ui.visitorBackdrop) {
            _ui.visitorBackdrop.classList.remove('hidden');
            _ui.visitorBackdrop.setAttribute('aria-hidden', 'false');
        }

        if (_setPrompt) _setPrompt('Visitor.', isKnown ? 'Enter reason for visit.' : 'Enter name and reason for visit.');
        if (_setEta)    _setEta('ETA: paused');

        setTimeout(function () {
            if (!isKnown && _ui.visitorName) _ui.visitorName.focus();
            else if (_ui.visitorPurpose)     _ui.visitorPurpose.focus();
        }, 50);
    }

    function close() {
        _state.visitorOpen    = false;
        _state.pendingVisitor = null;

        if (_ui.visitorBackdrop) {
            _ui.visitorBackdrop.classList.add('hidden');
            _ui.visitorBackdrop.setAttribute('aria-hidden', 'true');
        }
        if (_armPostScanHold) _armPostScanHold(1500);
        if (_setPrompt)       _setPrompt('Ready.', 'Stand still. One face only.');
    }

    function submitVisitorForm() {
        var scanId  = (_state.pendingVisitor && _state.pendingVisitor.scanId) ? _state.pendingVisitor.scanId : '';
        var isKnown = !!(_state.pendingVisitor && _state.pendingVisitor.isKnown);
        var name    = ((_ui.visitorName    && _ui.visitorName.value)    || '').trim();
        var purpose = ((_ui.visitorPurpose && _ui.visitorPurpose.value) || '').trim();

        if (!scanId) {
            if (_toastError) _toastError('Visitor scan expired. Please scan again.');
            close();
            return;
        }
        if (!isKnown && !name) {
            if (_ui.visitorErr) _ui.visitorErr.textContent = 'Name is required.';
            if (_ui.visitorName) _ui.visitorName.focus();
            return;
        }
        if (!purpose) {
            if (_ui.visitorErr) _ui.visitorErr.textContent = 'Reason is required.';
            if (_ui.visitorPurpose) _ui.visitorPurpose.focus();
            return;
        }

        var fd = new FormData();
        fd.append('__RequestVerificationToken', _token);
        fd.append('scanId', scanId);
        if (!isKnown) fd.append('name', name);
        fd.append('purpose', purpose);

        fetch(_EP.submitVisitor, { method: 'POST', body: fd, credentials: 'same-origin' })
            .then(function (r) {
                if (r.status === 429) { if (_toastError) _toastError('System busy. Please wait.'); return null; }
                return r.json();
            })
            .then(function (j) {
                if (!j) return;
                if (j.ok) {
                    if (_toastSuccess) _toastSuccess(j.message || 'Visitor saved.');
                    close();
                    if (_armPostScanHold) _armPostScanHold(_CFG.postScan.holdMs);
                } else {
                    if (_ui.visitorErr) _ui.visitorErr.textContent = j.message || j.error || 'Could not save visitor.';
                }
            })
            .catch(function () {
                if (_toastError) _toastError('System error. Please try again.');
            });
    }

    function wire() {
        if (_ui.visitorCancel) _ui.visitorCancel.addEventListener('click', close);
        if (_ui.visitorClose)  _ui.visitorClose.addEventListener('click',  close);
        if (_ui.visitorSubmit) _ui.visitorSubmit.addEventListener('click', submitVisitorForm);

        if (_ui.visitorBackdrop) {
            _ui.visitorBackdrop.addEventListener('click', function (e) {
                if (e.target === _ui.visitorBackdrop) close();
            });
        }

        document.addEventListener('keydown', function (e) {
            if (!_state.visitorOpen) return;
            if (e.key === 'Escape') { e.preventDefault(); close(); }
            if (e.key === 'Enter')  { e.preventDefault(); submitVisitorForm(); }
        });
    }

    function init(ui, state, EP, token, CFG, setPrompt, setEta, armPostScanHold, toastSuccess, toastError) {
        _ui              = ui;
        _state           = state;
        _EP              = EP;
        _token           = token;
        _CFG             = CFG;
        _setPrompt       = setPrompt;
        _setEta          = setEta;
        _armPostScanHold = armPostScanHold;
        _toastSuccess    = toastSuccess;
        _toastError      = toastError;
    }

    window.KioskVisitor = {
        init:  init,
        wire:  wire,
        open:  open,
        close: close,
        isOpen: function () { return _state && _state.visitorOpen; }
    };
})();
