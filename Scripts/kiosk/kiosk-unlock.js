// kiosk-unlock.js
// Admin unlock modal: PIN entry, success confirmation, admin redirect.
// Exposes window.KioskUnlock — must be loaded after kiosk-config.js and kiosk-state.js.
(function () {
    'use strict';

    var _ui         = null;
    var _state      = null;
    var _EP         = null;
    var _token      = null;
    var _appBase    = null;
    var _allowUnlock = false;
    var _setPrompt  = null;

    var _pendingReturnUrl = '';

    function isUnlockAvailable() {
        if (!_allowUnlock) return false;
        return !!(_ui.unlockBackdrop && _ui.unlockPin && _ui.unlockSubmit && _ui.unlockCancel && _ui.unlockErr);
    }

    function open() {
        if (!isUnlockAvailable()) return;
        if (_state.visitorOpen && window.KioskVisitor) window.KioskVisitor.close();
        _state.unlockOpen = true;
        _ui.unlockErr.textContent = '';
        _ui.unlockPin.value = '';
        _ui.unlockBackdrop.classList.remove('hidden');
        _ui.unlockBackdrop.setAttribute('aria-hidden', 'false');
        if (_ui.kioskRoot) _ui.kioskRoot.classList.add('unlockOpen');
        setTimeout(function () { if (_ui.unlockPin) _ui.unlockPin.focus(); }, 50);
    }

    function close() {
        if (!isUnlockAvailable()) return;
        _state.unlockOpen = false;
        _ui.unlockBackdrop.classList.add('hidden');
        _ui.unlockBackdrop.setAttribute('aria-hidden', 'true');
        if (_ui.kioskRoot) _ui.kioskRoot.classList.remove('unlockOpen');
        _ui.unlockErr.textContent = '';
        _ui.unlockPin.value = '';
    }

    function submitUnlock() {
        if (!isUnlockAvailable()) return;
        var pin = (_ui.unlockPin.value || '').trim();
        if (!pin) { _ui.unlockErr.textContent = 'Enter PIN.'; _ui.unlockPin.focus(); return; }

        var fd = new FormData();
        fd.append('__RequestVerificationToken', _token);
        fd.append('pin', pin);
        fd.append('returnUrl', (document.body && document.body.dataset && document.body.dataset.returnUrl) || '');

        _ui.unlockSubmit.disabled = true;
        _ui.unlockCancel.disabled = true;
        _ui.unlockErr.textContent = '';

        fetch(_EP.unlockPin, { method: 'POST', body: fd })
            .then(function (r) {
                if (r.status === 403) {
                    close();
                    if (window.Swal) {
                        Swal.fire({
                            title: 'Admin Access Unavailable',
                            text: 'Admin unlock is disabled on mobile devices for security. Please use a desktop computer or laptop to access the admin panel.',
                            icon: 'info',
                            confirmButtonText: 'Got it',
                            confirmButtonColor: '#3b82f6',
                            background: '#0f172a',
                            color: '#f8fafc'
                        });
                    } else {
                        alert('Admin unlock is disabled on mobile devices. Please use a desktop computer.');
                    }
                    return { handled: true };
                }
                return r.json();
            })
            .then(function (j) {
                if (j && j.handled) return;
                if (j && j.ok === true) {
                    _pendingReturnUrl = (j.returnUrl || '').trim();
                    close();
                    showUnlockSuccess();
                } else {
                    if (j && j.error === 'UNLOCK_DISABLED_ON_MOBILE') {
                        close();
                        if (window.Swal) {
                            Swal.fire({
                                title: 'Admin Access Unavailable',
                                text: 'Admin unlock is disabled on mobile devices for security. Please use a desktop computer or laptop to access the admin panel.',
                                icon: 'info',
                                confirmButtonText: 'Got it',
                                confirmButtonColor: '#3b82f6',
                                background: '#0f172a',
                                color: '#f8fafc'
                            });
                        } else {
                            alert('Admin unlock is disabled on mobile devices. Please use a desktop computer.');
                        }
                    } else {
                        _ui.unlockErr.textContent = 'Invalid PIN.';
                        if (_ui.unlockPin) _ui.unlockPin.focus();
                    }
                }
            })
            .catch(function () {
                _ui.unlockErr.textContent = 'Unlock failed.';
            })
            .finally(function () {
                _ui.unlockSubmit.disabled = false;
                _ui.unlockCancel.disabled = false;
            });
    }

    function showUnlockSuccess() {
        if (!_ui.unlockSuccessBackdrop) return;
        _state.adminModalOpen = true;
        _ui.unlockSuccessBackdrop.classList.remove('hidden');
        _ui.unlockSuccessBackdrop.setAttribute('aria-hidden', 'false');
        if (_setPrompt) _setPrompt('Admin access granted.', 'Choose where to go.');
    }

    function closeUnlockSuccess() {
        if (!_ui.unlockSuccessBackdrop) return;
        _ui.unlockSuccessBackdrop.classList.add('hidden');
        _ui.unlockSuccessBackdrop.setAttribute('aria-hidden', 'true');
        _state.adminModalOpen = false;
        _pendingReturnUrl = '';
        if (_setPrompt) _setPrompt('Ready.', 'Look at the camera.');
    }

    function goToAdmin() {
        var targetUrl = _pendingReturnUrl || (_appBase + 'Admin/Index');
        window.location.href = targetUrl;
    }

    function stayInKiosk() {
        closeUnlockSuccess();
    }

    function wire() {
        if (!isUnlockAvailable()) return;

        _ui.unlockCancel.addEventListener('click', close);
        _ui.unlockSubmit.addEventListener('click', submitUnlock);
        if (_ui.unlockClose) _ui.unlockClose.addEventListener('click', close);

        _ui.unlockBackdrop.addEventListener('click', function (e) {
            if (e.target === _ui.unlockBackdrop) close();
        });

        _ui.unlockPin.addEventListener('keydown', function (e) {
            if (e.key === 'Enter')  { e.preventDefault(); submitUnlock(); }
            if (e.key === 'Escape') { e.preventDefault(); close(); }
        });

        if (_ui.unlockGoAdmin)         _ui.unlockGoAdmin.addEventListener('click', goToAdmin);
        if (_ui.unlockStayKiosk)       _ui.unlockStayKiosk.addEventListener('click', stayInKiosk);
        if (_ui.unlockSuccessBackdrop) {
            _ui.unlockSuccessBackdrop.addEventListener('click', function (e) {
                if (e.target === _ui.unlockSuccessBackdrop) stayInKiosk();
            });
        }

        document.addEventListener('keydown', function (e) {
            if (!isUnlockAvailable()) return;
            if (_state.unlockOpen)    return;
            var isSpace = (e.code === 'Space') || (e.key === ' ') || (e.keyCode === 32);
            if (e.ctrlKey && e.shiftKey && isSpace) {
                e.preventDefault();
                if (_state.visitorOpen && window.KioskVisitor) window.KioskVisitor.close();
                open();
            }
        }, true);

        var brandEl = document.querySelector('#topLeft .brand');
        if (brandEl) brandEl.addEventListener('dblclick', open);
    }

    function init(ui, state, EP, token, appBase, allowUnlock, setPrompt) {
        _ui          = ui;
        _state       = state;
        _EP          = EP;
        _token       = token;
        _appBase     = appBase;
        _allowUnlock = allowUnlock;
        _setPrompt   = setPrompt;
    }

    window.KioskUnlock = {
        init:  init,
        wire:  wire,
        open:  open,
        close: close,
        isOpen: function () { return _state && _state.unlockOpen; }
    };
})();
