// kiosk-device.js
// Device token management, device registration, mobile device state check.
// Exposes window.KioskDevice — must be loaded after kiosk-state.js.
(function () {
    'use strict';

    var DEVICE_TOKEN_KEY    = 'FaceAttend_DeviceToken';
    var DEVICE_TOKEN_COOKIE = 'FaceAttend_DeviceToken';

    var _state          = null;
    var _EP             = null;
    var _token          = null;
    var _appBase        = null;
    var _isPersonalMobile = false;
    var _setPrompt      = null;
    var _setIdleUi      = null;
    var _armPostScanHold = null;
    var _toastSuccess   = null;
    var _toastError     = null;

    // ── Cookie / token helpers ─────────────────────────────────────────────────

    function getCookieValue(name) {
        var match = document.cookie.match(new RegExp('(?:^|; )' + name.replace(/[.$?*|{}()\[\]\/+^]/g, '\\$&') + '=([^;]*)'));
        return match ? decodeURIComponent(match[1]) : '';
    }

    function isForcedKioskMode() {
        return getCookieValue('ForceKioskMode') === 'true';
    }

    function getDeviceToken() {
        var tok = null;
        try { tok = localStorage.getItem(DEVICE_TOKEN_KEY); } catch (e) {}
        if (!tok) {
            var match = document.cookie.match(new RegExp('(^| )' + DEVICE_TOKEN_COOKIE + '=([^;]+)'));
            if (match) tok = match[2];
        }
        return tok;
    }

    function setDeviceToken(tok) {
        if (!tok) return;
        try { localStorage.setItem(DEVICE_TOKEN_KEY, tok); } catch (e) {}
        var expiry = new Date();
        expiry.setFullYear(expiry.getFullYear() + 1);
        var secureFlag = window.location.protocol === 'https:' ? '; Secure' : '';
        document.cookie = DEVICE_TOKEN_COOKIE + '=' + tok + '; expires=' + expiry.toUTCString() + '; path=/; SameSite=Lax' + secureFlag;
    }

    function clearDeviceToken() {
        try { localStorage.removeItem(DEVICE_TOKEN_KEY); } catch (e) {}
        document.cookie = DEVICE_TOKEN_COOKIE + '=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/;';
    }

    // ── Register button visibility ─────────────────────────────────────────────

    function getMobileRegisterBtn() {
        return document.getElementById('mobileRegisterBtn');
    }

    function setMobileRegisterVisible(show) {
        var btn = getMobileRegisterBtn();
        if (!btn) return;
        if (show && _state.deviceStatus === 'active') return;
        btn.style.display = show ? 'block' : 'none';
    }

    // ── Registration flow ──────────────────────────────────────────────────────

    function doRegisterDevice(employeeId, deviceName) {
        var fd = new FormData();
        fd.append('__RequestVerificationToken', _token);
        fd.append('employeeId', employeeId);
        fd.append('deviceName', deviceName);
        var existingToken = getDeviceToken();
        if (existingToken) fd.append('deviceToken', existingToken);

        if (_setPrompt) _setPrompt('Registering device...', 'Please wait.');

        fetch(_appBase + 'MobileRegistration/RegisterDevice', { method: 'POST', body: fd, credentials: 'same-origin' })
            .then(function (r) { return r.json(); })
            .then(function (j) {
                if (j.ok) {
                    if (j.deviceToken || (j.data && j.data.deviceToken)) {
                        setDeviceToken(j.deviceToken || j.data.deviceToken);
                    }
                    _state.deviceChecked = true;
                    _state.deviceStatus  = 'pending';
                    setMobileRegisterVisible(false);
                    if (_setIdleUi)       _setIdleUi(true);
                    if (_toastSuccess)    _toastSuccess('Device registered! Waiting for admin approval.');
                    if (_setPrompt)       _setPrompt('Device registered.', 'Admin approval required.');
                } else {
                    if (_toastError)  _toastError(j.message || 'Registration failed.');
                    if (_setPrompt)   _setPrompt('Registration failed.', j.message || 'Please try again.');
                }
                if (_armPostScanHold) _armPostScanHold(3000);
            })
            .catch(function () {
                if (_toastError)      _toastError('Network error. Please try again.');
                if (_setPrompt)       _setPrompt('Registration failed.', 'Network error.');
                if (_armPostScanHold) _armPostScanHold(3000);
            });
    }

    function registerDevice(employeeId, employeeName) {
        if (window.Swal && window.Swal.fire) {
            Swal.fire({
                title: 'Register Device',
                text: employeeName ? 'Register device for ' + employeeName : 'Enter a name for this device',
                input: 'text',
                inputPlaceholder: 'e.g., My iPhone, Galaxy S24',
                inputAttributes: { autocapitalize: 'off', autocomplete: 'off' },
                showCancelButton:    true,
                confirmButtonText:   'Register',
                cancelButtonText:    'Cancel',
                confirmButtonColor:  '#3b82f6',
                cancelButtonColor:   '#64748b',
                background:          '#0f172a',
                color:               '#f8fafc',
                customClass: {
                    popup:         'kiosk-swal-popup',
                    title:         'kiosk-swal-title',
                    htmlContainer: 'kiosk-swal-text',
                    input:         'kiosk-swal-input',
                    confirmButton: 'kiosk-swal-confirm',
                    cancelButton:  'kiosk-swal-cancel'
                },
                preConfirm: function (deviceName) {
                    if (!deviceName || !deviceName.trim()) {
                        Swal.showValidationMessage('Please enter a device name');
                    }
                    return deviceName ? deviceName.trim() : null;
                }
            }).then(function (result) {
                if (result.isConfirmed && result.value) {
                    doRegisterDevice(employeeId, result.value);
                } else {
                    if (_setPrompt) _setPrompt('Device registration cancelled.', '');
                }
            });
        } else {
            var deviceName = prompt('Enter a name for this device (e.g., "My iPhone"):', '');
            if (!deviceName) {
                if (_setPrompt) _setPrompt('Device registration cancelled.', '');
                return;
            }
            doRegisterDevice(employeeId, deviceName);
        }
    }

    // ── Device state check ─────────────────────────────────────────────────────

    function checkCurrentMobileDeviceState() {
        if (!_isPersonalMobile || isForcedKioskMode()) {
            _state.deviceChecked = true;
            _state.deviceStatus  = 'active';
            setMobileRegisterVisible(false);
            return Promise.resolve();
        }

        _state.deviceChecked = false;
        _state.deviceStatus  = 'unknown';
        setMobileRegisterVisible(false);

        return fetch(_EP.deviceState, {
            method: 'GET',
            credentials: 'same-origin',
            headers: { 'X-Requested-With': 'XMLHttpRequest' }
        })
        .then(function (r) { return r.json(); })
        .then(function (j) {
            _state.deviceChecked = true;
            if (!j || !j.ok) { _state.deviceStatus = 'unknown'; return; }
            _state.deviceStatus = String(j.deviceStatus || 'unknown').toLowerCase();
            setMobileRegisterVisible(_state.deviceStatus === 'not_registered');
        })
        .catch(function () {
            _state.deviceChecked = true;
            _state.deviceStatus  = 'unknown';
            setMobileRegisterVisible(false);
        });
    }

    // ── Init ───────────────────────────────────────────────────────────────────

    function init(state, EP, token, appBase, isPersonalMobile, setPrompt, setIdleUi, armPostScanHold, toastSuccess, toastError) {
        _state           = state;
        _EP              = EP;
        _token           = token;
        _appBase         = appBase;
        _isPersonalMobile = isPersonalMobile;
        _setPrompt       = setPrompt;
        _setIdleUi       = setIdleUi;
        _armPostScanHold = armPostScanHold;
        _toastSuccess    = toastSuccess;
        _toastError      = toastError;
    }

    window.KioskDevice = {
        init:                     init,
        getCookieValue:           getCookieValue,
        isForcedKioskMode:        isForcedKioskMode,
        getDeviceToken:           getDeviceToken,
        setDeviceToken:           setDeviceToken,
        clearDeviceToken:         clearDeviceToken,
        setMobileRegisterVisible: setMobileRegisterVisible,
        registerDevice:           registerDevice,
        checkCurrentMobileDeviceState: checkCurrentMobileDeviceState
    };
})();
