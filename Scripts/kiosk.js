(function () {
    'use strict';

    // =========
    // dom refs (using FaceAttend.Utils if available)
    // =========
    var el = function (id) { 
        return FaceAttend.Utils ? FaceAttend.Utils.el(id) : document.getElementById(id); 
    };

    var video  = el('kioskVideo');
    var canvas = el('overlayCanvas');
    var ctx    = canvas.getContext('2d');

            var ui = {
        officeLine:        el('officeLine'),
        timeLine:          el('timeLine'),
        dateLine:          el('dateLine'),
        livenessLine:      el('livenessLine'),
        livenessBarFill:   el('livenessBarFill'),
        scanEtaLine:       el('scanEtaLine'),

        unlockBackdrop:    el('unlockBackdrop'),
        unlockPin:         el('unlockPin'),
        unlockErr:         el('unlockErr'),
        unlockCancel:      el('unlockCancel'),
        unlockSubmit:      el('unlockSubmit'),
        unlockClose:       el('unlockClose'),
        unlockSuccessBackdrop: el('unlockSuccessBackdrop'),
        unlockGoAdmin:     el('unlockGoAdmin'),
        unlockStayKiosk:   el('unlockStayKiosk'),

        visitorBackdrop:   el('visitorBackdrop'),
        visitorNameRow:    el('visitorNameRow'),
        visitorName:       el('visitorName'),
        visitorPurpose:    el('visitorPurpose'),
        visitorErr:        el('visitorErr'),
        visitorCancel:     el('visitorCancel'),
        visitorSubmit:     el('visitorSubmit'),
        visitorClose:      el('visitorClose'),

        kioskRoot:         el('kioskRoot'),
        idleOverlay:       el('idleOverlay'),
        idleClock:         el('idleClock'),
        idleDate:          el('idleDate'),
        idleOrgName:       el('idleOrgName'),
        idleStatusBadge:   el('idleStatusBadge'),
        idleLocationTitle: el('idleLocationTitle'),
        idleLocationSub:   el('idleLocationSub'),
        centerBlock:       el('centerBlock'),
        centerBlockTitle:  el('centerBlockTitle'),
        centerBlockSub:    el('centerBlockSub'),
        mainPrompt:        el('mainPrompt'),
        subPrompt:         el('subPrompt'),
    };

    var token        = FaceAttend.Utils ? FaceAttend.Utils.getCsrfToken() : 
                       ((document.querySelector('input[name="__RequestVerificationToken"]') || {}).value || '');
    var appBase      = (document.body.getAttribute('data-app-base') || '/').replace(/\/?$/, '/');
    var nextGenEnabled = (document.body.getAttribute('data-nextgen') || 'false').toLowerCase() === 'true';

    // Handle ?reset=1 parameter - clears device mode selection
    if (location.search.includes('reset=1')) {
        localStorage.removeItem('FaceAttend_DeviceMode');
        document.cookie = 'ForceKioskMode=; path=/; expires=Thu, 01 Jan 1970 00:00:00 GMT';
        // Remove the parameter from URL
        history.replaceState(null, '', location.pathname + location.hash);
    }

    // =========
    // config  -- all timings optimised vs original
    // =========
    var CFG = {
        debug: false,

        // OPT-01: 60ms loop (was 120) -- 2x faster face detection response
        loopMs: 60,

        mp: {
            detectMinConf:     0.30,
            acceptMinScore:    0.60,
            stableFramesMin:   2,
            // FIX-02: 20ms stable hold (was 50) -- walk-by mode, just 20ms of relative stability
            stableNeededMs:    20,
            multiMinAreaRatio: 0.015,
        },

        idle: {
            // OPT-03: 200ms sense (was 250)
            senseMs:    200,
            // OPT-04: 1800ms lost timeout (was 2000)
            faceLostMs: 1800,
            motionMin:  2.0,
        },

        server: {
            // OPT-05: 900ms resolve interval (was 1200)
            resolveMs:         10000,
            // OPT-06: 2500ms cooldown (was 3000) -- kiosk ready 500ms sooner
            captureCooldownMs: 2500,
        },

        postScan: {
            // OPT-07: 3500ms hold (was 5000) -- 1.5s faster return to ready
            holdMs:   3500,
            toastMs:  6500,
        },

        gating: {
            // OPT-08: 3 stable frames required (was 4)
            stableFramesRequired: 3,
            stableMaxMovePx:      120,  // FIX-02: Increased from 60 - allows walking movement, burst handles accuracy
            minFaceAreaRatio:     0.03,
            safeEdgeMarginRatio:  0.02,
            centerMin:            0.12,
            centerMax:            0.88,
        },

        antiSpoof: {
            motionW:       64,
            motionH:       48,
            motionWindow:  6,
            motionDiffMin: 1.2,
        },

        tasksVision: {
            wasmBase:  appBase + 'Scripts/vendor/mediapipe/tasks-vision/wasm',
            modelPath: appBase + 'Scripts/vendor/mediapipe/tasks-vision/models/blaze_face_short_range.tflite',
        },

        fastPreview: {
            enabled: false,              // WebSocket fast preview (set to true to enable)
            wsUrl: 'ws://localhost:8080/preview',  // WebSocket endpoint
            previewIntervalMs: 200,      // Min interval between preview requests
            confidenceThreshold: 0.70,   // Min confidence to show preview name
        },
    };

    // =========
    // config validation
    // =========
    function validateConfig() {
        var errors = [];
        if (!CFG.loopMs || CFG.loopMs <= 0)
            errors.push('CFG.loopMs is missing or <= 0');
        if (!CFG.server || !CFG.server.captureCooldownMs)
            errors.push('CFG.server.captureCooldownMs is missing');
        if (!CFG.server || !CFG.server.resolveMs)
            errors.push('CFG.server.resolveMs is missing');
        if (!CFG.mp || !CFG.mp.stableNeededMs)
            errors.push('CFG.mp.stableNeededMs is missing');

        ['kioskVideo','overlayCanvas','kioskRoot','mainPrompt','subPrompt'].forEach(function (id) {
            if (!document.getElementById(id))
                errors.push('Missing DOM element #' + id);
        });

        if (errors.length > 0) {
            var root = document.getElementById('kioskRoot') || document.body;
            var div  = document.createElement('div');
            div.style.cssText = 'position:fixed;top:0;left:0;right:0;padding:1rem;background:#c0392b;color:#fff;font-family:monospace;font-size:.85rem;z-index:99999;white-space:pre-wrap';
            div.textContent = 'KIOSK CONFIG ERROR -- scan loop will NOT start:\n\n' + errors.join('\n');
            root.insertAdjacentElement('afterbegin', div);
            // Config validation failed - silent
            return false;
        }
        return true;
    }

    function log() {
        if (CFG.debug) {
            var args = Array.prototype.slice.call(arguments);
            args.unshift('[FaceAttend]');
            // Debug log suppressed
        }
    }

    // =========
    // endpoints
    // =========
    var EP = {
        unlockPin:     appBase + 'Kiosk/UnlockPin',
        resolveOffice: appBase + 'Kiosk/ResolveOffice',
        attend:        appBase + 'Kiosk/Attend',
        submitVisitor: appBase + 'Kiosk/SubmitVisitor',
        deviceState:   appBase + 'Kiosk/GetCurrentMobileDeviceState'
    };

    // =========
    // server warm-up gate
    // =========
    function pollServerReady() {
        fetch(appBase + 'Health', {
            method: 'GET',
            credentials: 'same-origin',
            headers: { 'X-Requested-With': 'XMLHttpRequest' }
        })
        .then(function(r) { return r.ok ? r.json() : null; })
        .then(function(j) {
            if (j && j.warmUpState === 1) {
                state.serverReady = true;
                log('[warmup] Server ready.');
            } else {
                // Still loading models  retry in 2s
                setTimeout(pollServerReady, 2000);
            }
        })
        .catch(function() {
            // Network error  retry in 3s
            setTimeout(pollServerReady, 3000);
        });
    }
    // Start polling after 500ms (give IIS time to respond to first request)
    setTimeout(pollServerReady, 500);

    // =========
    // state
    // =========
    var ua       = navigator.userAgent || '';
    var isMobile = /Android|iPhone|iPad|iPod|Mobile|Tablet/i.test(ua);
    var isPersonalMobile = /iPhone|iPod|Windows Phone|IEMobile|BlackBerry|Android.+Mobile/i.test(ua);
    var allowUnlock = (document.body.getAttribute('data-allow-unlock') || 'false') === 'true';
    var pageLoadTime = Date.now();  // Track when page loaded to prevent immediate scan

            var state = {
        unlockOpen:      false,
        adminModalOpen:  false,         // Block scanning when admin success modal is shown
        serverReady:     false,        // NEW: blocks scans until warm-up is complete
        wasIdle:         true,
        visitorOpen:     false,
        pendingVisitor:  null,
        scanBlockUntil:  0,
        submitInProgress: false,
        deviceStatus:    'unknown',
        deviceChecked:   false,

        gps:             { lat: null, lon: null, accuracy: null },
        allowedArea:     false,
        locationState:   'pending',
        locationBanner:  'Checking location...',
        locationTitle:   'Preparing kiosk',
        locationSub:     'Please wait while the kiosk verifies the current office location.',
        currentOffice: { id: null, name: null }, lastResolveAt: 0,
        officeVerifiedUntil: 0, officeResolveRetryUntil: 0,
        lastVerifiedLat: null, lastVerifiedLon: null,  // GPS position at time of last successful resolve
        backoffUntil: 0, lastCaptureAt: 0,

        mpMode:          'none',
        mpReadyToFire:   false,
        mpStableStart:   0,
        mpFaceSeenAt:    0,
        faceStatus:      'none',
        mpRawCount:      0,
        mpAcceptedCount: 0,
        mpBoxCanvas:     null,
        mpPrevCenter:    null,
        smoothedBox:     null,  // FIX-01: EMA smoothed bounding box

        latestLiveness:    null,
        livenessThreshold: 0.60,  // UPDATED: More forgiving threshold

        motionDiffNow:   null,
        frameDiffs:      [],

        liveInFlight:    false,
        attendAbortCtrl: null,

        localSeenAt:     0,
        localPresent:    false,

        scanLineProgress: 0,

        // ULTRA-FAST PREVIEW state
        fastWs:           null,           // WebSocket connection
        fastPreviewLastAt: 0,             // Last preview attempt
        fastPreviewResult: null,          // {name, confidence, employeeId}
        fastPreviewScanning: false,       // Currently sending to WS
        fastPreviewFailCount: 0,          // Connection failure count
    };

    // =========
    // helpers
    // =========
    function pushLimited(arr, v, max) {
        if (typeof v !== 'number' || !isFinite(v)) return;
        arr.push(v);
        while (arr.length > max) arr.shift();
    }

    function setPrompt(a, b) {
        if (ui.mainPrompt) ui.mainPrompt.textContent = a || '';
        if (ui.subPrompt)  ui.subPrompt.textContent  = b || '';
    }

            function safeSetPrompt(a, b) {
        if (state.liveInFlight) return;
        if ((Date.now() - state.lastCaptureAt) < 800) return;
        setPrompt(a, b);
    }

    function setCenterBlock(title, sub, show) {
        if (ui.centerBlock) ui.centerBlock.classList.toggle('hidden', !show);
        if (ui.centerBlockTitle) ui.centerBlockTitle.textContent = title || '';
        if (ui.centerBlockSub) ui.centerBlockSub.textContent = sub || '';
    }

    function humanizeResolveError(code, retryAfter, requiredAccuracy) {
        var c = (code || '').toString().toUpperCase();

        if (c === 'RATE_LIMIT_EXCEEDED') {
            return {
                title: 'Location check is busy',
                sub: 'The kiosk is verifying too often. Please wait a moment and try again.',
                banner: 'Checking location...'
            };
        }

        if (c === 'GPS_REQUIRED') {
            return {
                title: 'Location is required',
                sub: 'Enable location services so the kiosk can verify the assigned office.',
                banner: 'Location required'
            };
        }

        if (c === 'GPS_ACCURACY') {
            return {
                title: 'Location is not accurate enough',
                sub: requiredAccuracy
                    ? ('Move to an open area and wait until accuracy is within ' + requiredAccuracy + ' meters.')
                    : 'Move to an open area and try again.',
                banner: 'Accuracy too low'
            };
        }

        if (c === 'NO_OFFICES') {
            return {
                title: 'No active office is configured',
                sub: 'Please contact the system administrator.',
                banner: 'Office not configured'
            };
        }

        if (c === 'NO_OFFICE_NEARBY') {
            return {
                title: 'Outside allowed office area',
                sub: 'Move inside the DILG Region XII office radius to continue.',
                banner: 'Not in allowed area'
            };
        }

        if (c === 'GPS_DENIED') {
            return {
                title: 'Location access denied',
                sub: 'Allow location access to continue using the kiosk.',
                banner: 'Location denied'
            };
        }

        if (c === 'GPS_UNAVAILABLE') {
            return {
                title: 'Location unavailable',
                sub: 'The device could not detect the current location. Move to an open area and try again.',
                banner: 'Location unavailable'
            };
        }

        if (c === 'GPS_TIMEOUT') {
            return {
                title: 'Location request timed out',
                sub: 'Please wait a moment and try again.',
                banner: 'Location timeout'
            };
        }

        return {
            title: 'Unable to verify location',
            sub: 'Please wait a moment, then try again or contact the system administrator.',
            banner: 'Location check failed'
        };
    }

    

    // REMOVED DUPLICATE: setCenterBlock function already defined at line 259
    // See original definition above

    function applyLocationUi() {
        var kind = state.locationState || 'pending';
        var orgName = (document.body.getAttribute('data-org-name') || 'DILG Region XII');

        if (ui.idleOrgName) ui.idleOrgName.textContent = orgName;
        if (ui.kioskRoot) ui.kioskRoot.setAttribute('data-location-state', kind);
        if (document.body) document.body.setAttribute('data-location-state', kind);

        if (ui.officeLine) {
            ui.officeLine.textContent = state.currentOffice.name || state.locationBanner || 'Checking location...';
        }

        if (ui.idleStatusBadge) {
            ui.idleStatusBadge.classList.remove('is-pending', 'is-ready', 'is-blocked');
            ui.idleStatusBadge.classList.add(
                kind === 'allowed' ? 'is-ready' :
                kind === 'blocked' ? 'is-blocked' :
                'is-pending'
            );

            ui.idleStatusBadge.textContent =
                kind === 'allowed' ? 'Location verified' :
                kind === 'blocked' ? 'Location blocked' :
                'Checking location';
        }

        if (ui.idleLocationTitle) ui.idleLocationTitle.textContent = state.locationTitle || 'Preparing kiosk';
        if (ui.idleLocationSub) ui.idleLocationSub.textContent = state.locationSub || '';

        var showCenter = (kind === 'blocked' && ui.idleOverlay && ui.idleOverlay.classList.contains('hidden'));
        setCenterBlock(state.locationTitle, state.locationSub, showCenter);
    }

    function setLocationState(kind, title, sub, banner) {
        state.locationState = kind || 'pending';
        state.allowedArea = (state.locationState === 'allowed');
        state.locationTitle = title || '';
        state.locationSub = sub || '';
        state.locationBanner = banner || '';
        applyLocationUi();
    }

    // =========
    // toast (delegates to FaceAttend.Notify if available)
    // =========
    function toast(type, text) {
        var msg = (text || '').toString().trim();
        if (!msg) return;
        
        // Use FaceAttend.Notify if available
        if (FaceAttend.Notify) {
            FaceAttend.Notify.toast(msg, { type: type, duration: type === 'success' ? 3000 : 4000 });
            return;
        }
        
        // Fallback to SweetAlert2
        if (window.Swal) {
            var isSuccess = type === 'success';
            var isError = type === 'error';
            var icon = isSuccess ? 'success' : (isError ? 'error' : 'info');
            var title = isSuccess ? 'Success' : (isError ? 'Error' : 'Info');
            
            Swal.fire({
                title: title,
                text: msg,
                icon: icon,
                toast: true,
                position: 'top-end',
                showConfirmButton: false,
                timer: isSuccess ? 3000 : 4000,
                timerProgressBar: true,
                background: isSuccess ? '#f0fdf4' : (isError ? '#fef2f2' : '#eff6ff'),
                color: isSuccess ? '#166534' : (isError ? '#991b1b' : '#1e40af'),
                customClass: { popup: 'kiosk-toast-popup' },
                didOpen: function(popup) {
                    popup.style.borderRadius = '12px';
                    popup.style.boxShadow = '0 10px 40px rgba(0,0,0,0.2)';
                }
            });
        } else if (window.Toastify) {
            var bg = type === 'success' ? '#1a6b3a' : (type === 'info' ? '#1a3a6b' : '#6b1a1a');
            Toastify({
                text:        msg,
                duration:    CFG.postScan.toastMs,
                close:       true,
                gravity:     'bottom',
                position:    'right',
                stopOnFocus: true,
                style:       { background: bg },
            }).showToast();
        }
    }

    function toastSuccess(t) { toast('success', t); }
    function toastError(t)   { toast('error',   t); }

    // =========
    // post-scan hold
    // =========
    function armPostScanHold(ms) {
        var now  = Date.now();
        var hold = (typeof ms === 'number' && isFinite(ms) && ms > 0) ? ms : CFG.postScan.holdMs;
        state.scanBlockUntil = Math.max(state.scanBlockUntil || 0, now + hold);
    }

    // =========
    // visitor modal
    // =========
    function openVisitorModal(payload) {
        if (state.unlockOpen)  return;
        if (state.visitorOpen) return;
        state.visitorOpen    = true;
        state.pendingVisitor = payload || null;

        if (ui.visitorErr) ui.visitorErr.textContent = '';

        var isKnown = !!(payload && payload.isKnown);
        var name    = (payload && payload.visitorName) ? payload.visitorName : '';

        if (ui.visitorNameRow) ui.visitorNameRow.classList.toggle('hidden', isKnown);
        if (ui.visitorName) {
            ui.visitorName.value    = name;
            ui.visitorName.disabled = isKnown;
        }
        if (ui.visitorPurpose) ui.visitorPurpose.value = '';

        if (ui.visitorBackdrop) {
            ui.visitorBackdrop.classList.remove('hidden');
            ui.visitorBackdrop.setAttribute('aria-hidden', 'false');
        }

        setPrompt('Visitor.', isKnown ? 'Enter reason for visit.' : 'Enter name and reason for visit.');
        setEta('ETA: paused');

        setTimeout(function () {
            if (!isKnown && ui.visitorName) ui.visitorName.focus();
            else if (ui.visitorPurpose) ui.visitorPurpose.focus();
        }, 50);
    }

    function closeVisitorModal() {
        state.visitorOpen    = false;
        state.pendingVisitor = null;

        if (ui.visitorBackdrop) {
            ui.visitorBackdrop.classList.add('hidden');
            ui.visitorBackdrop.setAttribute('aria-hidden', 'true');
        }
        armPostScanHold(1500);
        setPrompt('Ready.', 'Stand still. One face only.');
    }

    // =========
    // Device registration
    // =========
    function registerDevice(employeeId, employeeName) {
        // Use SweetAlert2 if available, fallback to native prompt
        if (window.Swal && window.Swal.fire) {
            Swal.fire({
                title: 'Register Device',
                text: employeeName ? 'Register device for ' + employeeName : 'Enter a name for this device',
                input: 'text',
                inputPlaceholder: 'e.g., My iPhone, Galaxy S24',
                inputAttributes: {
                    autocapitalize: 'off',
                    autocomplete: 'off'
                },
                showCancelButton: true,
                confirmButtonText: 'Register',
                cancelButtonText: 'Cancel',
                confirmButtonColor: '#3b82f6',
                cancelButtonColor: '#64748b',
                background: '#0f172a',
                color: '#f8fafc',
                customClass: {
                    popup: 'kiosk-swal-popup',
                    title: 'kiosk-swal-title',
                    htmlContainer: 'kiosk-swal-text',
                    input: 'kiosk-swal-input',
                    confirmButton: 'kiosk-swal-confirm',
                    cancelButton: 'kiosk-swal-cancel'
                },
                preConfirm: function(deviceName) {
                    if (!deviceName || !deviceName.trim()) {
                        Swal.showValidationMessage('Please enter a device name');
                    }
                    return deviceName ? deviceName.trim() : null;
                }
            }).then(function(result) {
                if (result.isConfirmed && result.value) {
                    doRegisterDevice(employeeId, result.value);
                } else {
                    setPrompt('Device registration cancelled.', '');
                }
            });
        } else {
            // Fallback to native prompt
            var deviceName = prompt('Enter a name for this device (e.g., "My iPhone"):', '');
            if (!deviceName) {
                setPrompt('Device registration cancelled.', '');
                return;
            }
            doRegisterDevice(employeeId, deviceName);
        }
    }


    function getCookieValue(name) {
        var match = document.cookie.match(new RegExp('(?:^|; )' + name.replace(/[.$?*|{}()\[\]\/+^]/g, '\\$&') + '=([^;]*)'));
        return match ? decodeURIComponent(match[1]) : '';
    }

    function isForcedKioskMode() {
        return getCookieValue('ForceKioskMode') === 'true';
    }

    function getMobileRegisterBtn() {
        return document.getElementById('mobileRegisterBtn');
    }

    function setMobileRegisterVisible(show) {
        var mobileRegisterBtn = getMobileRegisterBtn();
        if (!mobileRegisterBtn) return;
        mobileRegisterBtn.style.display = show ? 'block' : 'none';
    }

    function checkCurrentMobileDeviceState() {
        if (!isPersonalMobile || isForcedKioskMode()) {
            state.deviceChecked = true;
            state.deviceStatus = 'active';
            setMobileRegisterVisible(false);
            return Promise.resolve();
        }

        state.deviceChecked = false;
        state.deviceStatus = 'unknown';
        setMobileRegisterVisible(false);

        return fetch(EP.deviceState, {
            method: 'GET',
            credentials: 'same-origin',
            headers: { 'X-Requested-With': 'XMLHttpRequest' }
        })
            .then(function (r) { return r.json(); })
            .then(function (j) {
                state.deviceChecked = true;
                if (!j || !j.ok) {
                    state.deviceStatus = 'unknown';
                    return;
                }

                state.deviceStatus = String(j.deviceStatus || 'unknown').toLowerCase();

                if (state.deviceStatus === 'not_registered') {
                    setMobileRegisterVisible(true);
                } else {
                    setMobileRegisterVisible(false);
                }
            })
            .catch(function () {
                state.deviceChecked = true;
                state.deviceStatus = 'unknown';
                setMobileRegisterVisible(false);
            });
    }

    // =========
    // Device Token Management (Persistent Device Identification)
    // =========
    var DEVICE_TOKEN_KEY = 'FaceAttend_DeviceToken';
    var DEVICE_TOKEN_COOKIE = 'FaceAttend_DeviceToken';
    
    function getDeviceToken() {
        // Try localStorage first
        var token = null;
        try {
            token = localStorage.getItem(DEVICE_TOKEN_KEY);

        } catch (e) {

        }
        
        // Fallback to cookie
        if (!token) {
            var match = document.cookie.match(new RegExp('(^| )' + DEVICE_TOKEN_COOKIE + '=([^;]+)'));
            if (match) {
                token = match[2];

            }
        }
        

        return token;
    }
    
    function setDeviceToken(token) {
        if (!token) return;
        
        // DEBUG

        
        // Save to localStorage (primary - survives longer)
        try {
            localStorage.setItem(DEVICE_TOKEN_KEY, token);

        } catch (e) {

        }
        
        // Also set cookie (1 year expiry) - don't use Secure on HTTP
        var expiry = new Date();
        expiry.setFullYear(expiry.getFullYear() + 1);
        var isHttps = window.location.protocol === 'https:';
        var secureFlag = isHttps ? '; Secure' : '';
        document.cookie = DEVICE_TOKEN_COOKIE + '=' + token + '; expires=' + expiry.toUTCString() + '; path=/; SameSite=Lax' + secureFlag;

    }
    
    function clearDeviceToken() {
        try {
            localStorage.removeItem(DEVICE_TOKEN_KEY);
        } catch (e) {}
        
        // Clear cookie
        document.cookie = DEVICE_TOKEN_COOKIE + '=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/;';
    }

    function doRegisterDevice(employeeId, deviceName) {
        var fd = new FormData();
        fd.append('__RequestVerificationToken', token);
        fd.append('employeeId', employeeId);
        fd.append('deviceName', deviceName);
        
        // Include existing device token if available
        var existingToken = getDeviceToken();
        if (existingToken) {
            fd.append('deviceToken', existingToken);
        }

        setPrompt('Registering device...', 'Please wait.');

        fetch(appBase + 'MobileRegistration/RegisterDevice', { method: 'POST', body: fd, credentials: 'same-origin' })
            .then(function (r) { return r.json(); })
            .then(function (j) {
                if (j.ok) {
                    // Save device token for persistent identification
                    if (j.deviceToken || (j.data && j.data.deviceToken)) {
                        setDeviceToken(j.deviceToken || j.data.deviceToken);
                    }
                    state.deviceChecked = true;
                    state.deviceStatus = 'pending';
                    setMobileRegisterVisible(false);
                    setIdleUi(true);
                    toastSuccess('Device registered! Waiting for admin approval.');
                    setPrompt('Device registered.', 'Admin approval required.');
                } else {
                    toastError(j.message || 'Registration failed.');
                    setPrompt('Registration failed.', j.message || 'Please try again.');
                }
                armPostScanHold(3000);
            })
            .catch(function () {
                toastError('Network error. Please try again.');
                setPrompt('Registration failed.', 'Network error.');
                armPostScanHold(3000);
            });
    }

    function submitVisitorForm() {
        var scanId  = (state.pendingVisitor && state.pendingVisitor.scanId) ? state.pendingVisitor.scanId : '';
        var isKnown = !!(state.pendingVisitor && state.pendingVisitor.isKnown);
        var name    = ((ui.visitorName    && ui.visitorName.value)    || '').trim();
        var purpose = ((ui.visitorPurpose && ui.visitorPurpose.value) || '').trim();

        if (!scanId) {
            toastError('Visitor scan expired. Please scan again.');
            closeVisitorModal();
            return;
        }
        if (!isKnown && !name) {
            if (ui.visitorErr) ui.visitorErr.textContent = 'Name is required.';
            if (ui.visitorName) ui.visitorName.focus();
            return;
        }
        if (!purpose) {
            if (ui.visitorErr) ui.visitorErr.textContent = 'Reason is required.';
            if (ui.visitorPurpose) ui.visitorPurpose.focus();
            return;
        }

        var fd = new FormData();
        fd.append('__RequestVerificationToken', token);
        fd.append('scanId', scanId);
        if (!isKnown) fd.append('name', name);
        fd.append('purpose', purpose);

        fetch(EP.submitVisitor, { method: 'POST', body: fd, credentials: 'same-origin' })
            .then(function (r) {
                if (r.status === 429) { toastError('System busy. Please wait.'); return null; }
                return r.json();
            })
            .then(function (j) {
                if (!j) return;
                if (j.ok) {
                    toastSuccess(j.message || 'Visitor saved.');
                    closeVisitorModal();
                    armPostScanHold(CFG.postScan.holdMs);
                } else {
                    if (ui.visitorErr) ui.visitorErr.textContent = j.message || j.error || 'Could not save visitor.';
                }
            })
            .catch(function () {
                toastError('System error. Please try again.');
            });
    }

    function wireVisitorUi() {
        if (ui.visitorCancel) ui.visitorCancel.addEventListener('click', closeVisitorModal);
        if (ui.visitorClose)  ui.visitorClose.addEventListener('click',  closeVisitorModal);
        if (ui.visitorSubmit) ui.visitorSubmit.addEventListener('click', submitVisitorForm);

        if (ui.visitorBackdrop) {
            ui.visitorBackdrop.addEventListener('click', function (e) {
                if (e.target === ui.visitorBackdrop) closeVisitorModal();
            });
        }

        document.addEventListener('keydown', function (e) {
            if (!state.visitorOpen) return;
            if (e.key === 'Escape') { e.preventDefault(); closeVisitorModal(); }
            if (e.key === 'Enter')  { e.preventDefault(); submitVisitorForm(); }
        });
    }

    // =========
    // ETA
    // =========
    function setEta(text) {
        if (ui.scanEtaLine) ui.scanEtaLine.textContent = text || 'ETA: --';
    }

    // =========
    // idle UI
    // =========
    function setIdleUi(idle) {
        if (ui.kioskRoot)   ui.kioskRoot.classList.toggle('kioskIdle', !!idle);
        if (ui.idleOverlay) {
            ui.idleOverlay.classList.toggle('hidden', !idle);
            ui.idleOverlay.setAttribute('aria-hidden', idle ? 'false' : 'true');
        }
    }

    function setKioskMode(mode) {
        try { if (ui.kioskRoot) ui.kioskRoot.setAttribute('data-mode', mode || 'legacy'); } catch (e) {}
    }

    // =========
    // clock -- updates HUD clock AND idle overlay clock in sync
    // =========
    function nowText() {
        var d    = new Date();
        var hh   = ('0' + d.getHours()).slice(-2);
        var mm   = ('0' + d.getMinutes()).slice(-2);
        var ss   = ('0' + d.getSeconds()).slice(-2);
        var time = hh + ':' + mm + ':' + ss;

        var days  = ['Sun','Mon','Tue','Wed','Thu','Fri','Sat'];
        var mons  = ['Jan','Feb','Mar','Apr','May','Jun','Jul','Aug','Sep','Oct','Nov','Dec'];
        var date  = days[d.getDay()] + ', ' + mons[d.getMonth()] + ' ' + d.getDate() + ' ' + d.getFullYear();

        if (ui.timeLine)  ui.timeLine.textContent  = time;
        if (ui.dateLine)  ui.dateLine.textContent  = date;
        // Sync to idle overlay
        if (ui.idleClock) ui.idleClock.textContent = time;
        if (ui.idleDate)  ui.idleDate.textContent  = date;
    }

    function startClock() {
        nowText();
        setInterval(nowText, 1000);
    }

    // =========
    // liveness display
    // =========
    function setLiveness(p, th, cls) {
        if (!ui.livenessLine) return;
        var hasP  = (typeof p  === 'number') && isFinite(p);
        var hasTh = (typeof th === 'number') && isFinite(th);
        
        // User-friendly text based on liveness state
        var statusText = 'Live: --';
        if (hasP) {
            if (cls === 'live-pass') {
                statusText = 'Live: PASS (' + p.toFixed(2) + ')';
            } else if (cls === 'live-near') {
                statusText = 'Live: CHECKING... (' + p.toFixed(2) + ')';
            } else if (cls === 'live-fail') {
                statusText = 'Live: FAILED - Move naturally (' + p.toFixed(2) + ')';
            } else {
                statusText = 'Live: ' + p.toFixed(2) + (hasTh ? ' / ' + th.toFixed(2) : '');
            }
        }
        
        ui.livenessLine.textContent = statusText;
        ui.livenessLine.classList.remove('live-pass','live-near','live-fail','live-unk');
        ui.livenessLine.classList.add(cls || 'live-unk');
        
        // Update liveness bar visual (interactive bar that fills left to right)
        if (ui.livenessBarFill) {
            var barWidth = hasP ? Math.round(p * 100) + '%' : '0%';
            ui.livenessBarFill.style.width = barWidth;
            ui.livenessBarFill.classList.remove('live-pass','live-near','live-fail','live-unk');
            ui.livenessBarFill.classList.add(cls || 'live-unk');
        }
    }

    // =========
    // canvas overlay helpers
    // =========
    function resizeCanvas() {
        var w = canvas.clientWidth;
        var h = canvas.clientHeight;
        if (canvas.width !== w || canvas.height !== h) {
            canvas.width  = w;
            canvas.height = h;
        }
    }

    function mapVideoBoxToCanvas(vbox) {
        if (!vbox || !video.videoWidth || !video.videoHeight) return null;
        var W = canvas.width, H = canvas.height;
        var imgW = video.videoWidth, imgH = video.videoHeight;
        var scale   = Math.max(W / imgW, H / imgH);
        var renderW = imgW * scale, renderH = imgH * scale;
        var offX    = (W - renderW) / 2, offY = (H - renderH) / 2;
        var x = offX + vbox.x * scale;
        var y = offY + vbox.y * scale;
        var w = vbox.w * scale;
        var h = vbox.h * scale;
        x = W - (x + w); // mirror to match scaleX(-1) on video
        return { x: x, y: y, w: w, h: h };
    }

    function toVideoBox(bb) {
        if (!bb) return null;

        var looksNormalized =
            bb.width <= 1.5 &&
            bb.height <= 1.5 &&
            bb.originX <= 1.5 &&
            bb.originY <= 1.5;

        var x, y, w, h;
        if (looksNormalized) {
            x = bb.originX * video.videoWidth;
            y = bb.originY * video.videoHeight;
            w = bb.width   * video.videoWidth;
            h = bb.height  * video.videoHeight;
        } else {
            x = bb.originX;
            y = bb.originY;
            w = bb.width;
            h = bb.height;
        }
        
        // CENTER FACE: MediaPipe box includes too much neck, not enough forehead
        // Shift up by 20% of height and expand height by 8% to capture full face
        var shiftUp = h * 0.20;
        var expandH = h * 0.08;
        
        y = y - shiftUp;
        h = h + expandH;
        
        return { x: x, y: y, w: w, h: h };
    }

    function boxFullyVisibleCanvas(box) {
        if (!box) return false;
        if (box.w <= 0 || box.h <= 0) return false;

        var cx = box.x + (box.w / 2);
        var cy = box.y + (box.h / 2);

        var mx = canvas.width * 0.04;
        var my = canvas.height * 0.04;

        if (cx < mx || cx > (canvas.width - mx)) return false;
        if (cy < my || cy > (canvas.height - my)) return false;

        if (box.w < canvas.width * 0.10) return false;
        if (box.h < canvas.height * 0.14) return false;

        return true;
    }

    function isTooSmallFaceNorm(bbox) {
        if (!bbox || !isFinite(bbox.width) || !isFinite(bbox.height)) return true;
        return bbox.width * bbox.height < CFG.gating.minFaceAreaRatio;
    }

    // =========
    // canvas draw -- animated corner brackets + scan line + glow
    // =========
    function drawCornerBrackets(bx, by, bw, bh, color, glowColor, lineWidth) {
        var L = Math.min(bw, bh) * 0.20;
        ctx.save();
        ctx.strokeStyle = color;
        ctx.lineWidth   = lineWidth;
        ctx.lineCap     = 'round';
        ctx.shadowColor = glowColor;
        ctx.shadowBlur  = 14;

        // top-left
        ctx.beginPath();
        ctx.moveTo(bx, by + L);
        ctx.lineTo(bx, by);
        ctx.lineTo(bx + L, by);
        ctx.stroke();

        // top-right
        ctx.beginPath();
        ctx.moveTo(bx + bw - L, by);
        ctx.lineTo(bx + bw, by);
        ctx.lineTo(bx + bw, by + L);
        ctx.stroke();

        // bottom-left
        ctx.beginPath();
        ctx.moveTo(bx, by + bh - L);
        ctx.lineTo(bx, by + bh);
        ctx.lineTo(bx + L, by + bh);
        ctx.stroke();

        // bottom-right
        ctx.beginPath();
        ctx.moveTo(bx + bw - L, by + bh);
        ctx.lineTo(bx + bw, by + bh);
        ctx.lineTo(bx + bw, by + bh - L);
        ctx.stroke();

        ctx.restore();
    }

    function drawScanLine(bx, by, bw, bh, progress, color) {
        if (progress <= 0) return;
        var scanY = by + bh * progress;
        var grad  = ctx.createLinearGradient(bx, scanY, bx + bw, scanY);
        grad.addColorStop(0,    'rgba(0,0,0,0)');
        grad.addColorStop(0.25, color);
        grad.addColorStop(0.5,  color);
        grad.addColorStop(0.75, color);
        grad.addColorStop(1,    'rgba(0,0,0,0)');

        ctx.save();
        ctx.globalAlpha = 0.55;
        ctx.strokeStyle = grad;
        ctx.lineWidth   = 1.5;
        ctx.shadowColor = color;
        ctx.shadowBlur  = 8;
        ctx.beginPath();
        ctx.moveTo(bx, scanY);
        ctx.lineTo(bx + bw, scanY);
        ctx.stroke();
        ctx.restore();
    }

    function drawLoop() {
        resizeCanvas();
        ctx.clearRect(0, 0, canvas.width, canvas.height);

        if (state.mpBoxCanvas) {
            var scanning = state.liveInFlight;
            var good     = state.faceStatus === 'good';

            // color based on liveness status (priority over scanning/good states)
            var mainColor, glowColor;
            var livenessCls = ui.livenessLine.className || '';
            
            if (livenessCls.includes('live-fail')) {
                // Liveness failed - red box
                mainColor = '#ef4444';
                glowColor = 'rgba(239,68,68,0.60)';
            } else if (livenessCls.includes('live-near')) {
                // Liveness near threshold - yellow/orange box
                mainColor = '#f59e0b';
                glowColor = 'rgba(245,158,11,0.60)';
            } else if (livenessCls.includes('live-pass')) {
                // Liveness passed - green box
                mainColor = '#22c55e';
                glowColor = 'rgba(34,197,94,0.60)';
            } else if (scanning) {
                mainColor = '#4f9cf9';
                glowColor = 'rgba(79,156,249,0.70)';
            } else if (good) {
                mainColor = '#34d399';
                glowColor = 'rgba(52,211,153,0.60)';
            } else {
                mainColor = '#fbbf24';
                glowColor = 'rgba(251,191,36,0.50)';
            }

            var b = state.mpBoxCanvas;

            // animated scan line while in flight
            if (scanning) {
                state.scanLineProgress = (state.scanLineProgress + 0.016) % 1.0;
                drawScanLine(b.x, b.y, b.w, b.h, state.scanLineProgress, mainColor);
            } else {
                state.scanLineProgress = 0;
            }

            // corner brackets -- thicker when scanning
            var lw = scanning ? 3.5 : 2.5;
            drawCornerBrackets(b.x, b.y, b.w, b.h, mainColor, glowColor, lw);
            ctx.save();
            ctx.strokeStyle = mainColor;
            ctx.globalAlpha = 0.95;
            ctx.lineWidth   = scanning ? 2.5 : 2.0;
            ctx.strokeRect(b.x, b.y, b.w, b.h);
            ctx.restore();
        }

        requestAnimationFrame(drawLoop);
    }

    // =========
    // reset scan state
    // =========
    function resetScanState() {
        state.mpBoxCanvas      = null;
        state.mpPrevCenter     = null;
        state.faceStatus       = 'none';
        state.mpRawCount       = 0;
        state.mpAcceptedCount  = 0;
        state.mpReadyToFire    = false;
        state.mpStableStart    = 0;
        state.mpFaceSeenAt     = 0;
        state.frameDiffs       = [];
        state.latestLiveness   = null;
        state.scanLineProgress = 0;
        // CRITICAL: Reset submission flags when resetting scan state
        // This prevents getting stuck in "Capturing..." state
        state.submitInProgress = false;
        state.liveInFlight     = false;
        setLiveness(null, null, 'live-unk');
        setEta('ETA: --');
    }
    
    // Safety timeout: If capture takes too long, reset flags
    var captureSafetyTimeout = null;
    function startCaptureSafetyTimeout() {
        clearTimeout(captureSafetyTimeout);
        captureSafetyTimeout = setTimeout(function() {
            if (state.submitInProgress || state.liveInFlight) {

                state.submitInProgress = false;
                state.liveInFlight = false;
                setPrompt('Capture timeout.', 'Please try again.');
            }
        }, 15000); // 15 second max capture time
    }
    function clearCaptureSafetyTimeout() {
        clearTimeout(captureSafetyTimeout);
    }

    // =========
    // motion sense (local anti-idle, no server calls)
    // =========
    var senseCanvas = document.createElement('canvas');
    senseCanvas.width  = CFG.antiSpoof.motionW;
    senseCanvas.height = CFG.antiSpoof.motionH;
    var senseCtx = senseCanvas.getContext('2d', { willReadFrequently: true });
    var lastSenseData = null;

    function updateSenseDiff() {
        if (!video.videoWidth || !video.videoHeight) return null;
        senseCtx.drawImage(video, 0, 0, CFG.antiSpoof.motionW, CFG.antiSpoof.motionH);
        var data = senseCtx.getImageData(0, 0, CFG.antiSpoof.motionW, CFG.antiSpoof.motionH).data;
        if (!lastSenseData) { lastSenseData = new Uint8ClampedArray(data); return null; }
        var sum = 0;
        for (var i = 0; i < data.length; i += 4) {
            sum += Math.abs(data[i]     - lastSenseData[i]);
            sum += Math.abs(data[i + 1] - lastSenseData[i + 1]);
            sum += Math.abs(data[i + 2] - lastSenseData[i + 2]);
        }
        lastSenseData.set(data);
        return sum / (CFG.antiSpoof.motionW * CFG.antiSpoof.motionH * 3);
    }

    function localSenseLoop() {
        try {
            if (video.videoWidth && video.videoHeight) {
                var diff = updateSenseDiff();
                state.motionDiffNow = diff;
                if (diff !== null) pushLimited(state.frameDiffs, diff, CFG.antiSpoof.motionWindow);
                if (diff !== null && diff >= CFG.idle.motionMin) state.localSeenAt = Date.now();
                state.localPresent = (Date.now() - state.localSeenAt) <= CFG.idle.faceLostMs;
            }
        } finally {
            setTimeout(localSenseLoop, CFG.idle.senseMs);
        }
    }

    // =========
    // camera capture
    // OPT-SPEED-01: quality 0.65 (was 0.78), resolution 480x360 (was 640x480)
    // ~40% smaller payload = faster upload + faster server processing
    // =========
    var captureCanvas = document.createElement('canvas');
    var captureCtx    = captureCanvas.getContext('2d');

    // Target resolution for speed (480x360 is plenty for face recognition)
    // Capture at higher resolution to match enrollment quality
    var CAPTURE_W = 1280;
    var CAPTURE_H = 720;

    function captureFrameBlob(quality) {
        // Higher JPEG quality for better face recognition accuracy
        var q = (typeof quality === 'number') ? quality : 0.90;
        captureCanvas.width  = CAPTURE_W;
        captureCanvas.height = CAPTURE_H;
        captureCtx.drawImage(video, 0, 0, CAPTURE_W, CAPTURE_H);
        return new Promise(function (resolve) {
            captureCanvas.toBlob(function (b) { resolve(b); }, 'image/jpeg', q);
        });
    }

    // Get current face bbox relative to capture canvas (for server-side optimization)
    function getFaceBoxForServer() {
        if (!state.mpBoxCanvas || !canvas.width || !canvas.height) return null;
        
        // Map from overlay canvas coordinates to capture canvas coordinates
        var scaleX = CAPTURE_W / canvas.width;
        var scaleY = CAPTURE_H / canvas.height;
        
        var box = state.mpBoxCanvas;
        return {
            x: Math.round(box.x * scaleX),
            y: Math.round(box.y * scaleY),
            w: Math.round(box.w * scaleX),
            h: Math.round(box.h * scaleY)
        };
    }

    // FIX-03: Fast sharpness check using Laplacian variance on face ROI
    function isFrameSharp(faceBox) {
        var SIZE = 64;
        var tmp = document.createElement('canvas');
        tmp.width = tmp.height = SIZE;
        var tCtx = tmp.getContext('2d');

        var sx = faceBox ? faceBox.x : canvas.width * 0.2;
        var sy = faceBox ? faceBox.y : canvas.height * 0.1;
        var sw = faceBox ? faceBox.w : canvas.width * 0.6;
        var sh = faceBox ? faceBox.h : canvas.height * 0.8;

        // Draw face ROI downscaled to 64×64
        tCtx.drawImage(video, sx, sy, sw, sh, 0, 0, SIZE, SIZE);
        var data = tCtx.getImageData(0, 0, SIZE, SIZE).data;

        // Grayscale + Laplacian variance
        var gray = new Float32Array(SIZE * SIZE);
        for (var i = 0; i < data.length; i += 4) {
            gray[i >> 2] = 0.299 * data[i] + 0.587 * data[i+1] + 0.114 * data[i+2];
        }
        var sum = 0, sumSq = 0, n = 0;
        for (var y = 1; y < SIZE - 1; y++) {
            for (var x = 1; x < SIZE - 1; x++) {
                var idx = y * SIZE + x;
                var lap = gray[idx-SIZE] + gray[idx-1] - 4*gray[idx] + gray[idx+1] + gray[idx+SIZE];
                sum += lap; sumSq += lap * lap; n++;
            }
        }
        var mean = sum / n;
        var variance = (sumSq / n) - (mean * mean);
        return variance > 30; // Below 30 = too blurry to bother sending
    }

    // =========
    // mediapipe tasks adapter
    // =========
    var mp = {
        vision:     null,
        detector:   null,
        failStreak: 0,

        init: function () {
            if (!nextGenEnabled) return Promise.reject(new Error('NEXTGEN_DISABLED'));

            var hasTasks = (
                typeof window.MpFilesetResolver === 'function' &&
                typeof window.MpFaceDetectorTask === 'function'
            );
            if (!hasTasks) return Promise.reject(new Error('MP_ASSETS_MISSING'));

            var self = this;
            return window.MpFilesetResolver
                .forVisionTasks(CFG.tasksVision.wasmBase)
                .then(function (vision) {
                    self.vision = vision;
                    return window.MpFaceDetectorTask.createFromOptions(vision, {
                        baseOptions: {
                            modelAssetPath: CFG.tasksVision.modelPath,
                            delegate: 'GPU',
                        },
                        runningMode: 'VIDEO',
                        minDetectionConfidence:    CFG.mp.detectMinConf,
                        minSuppressionThreshold:   0.3,
                    }).catch(function () {
                        return window.MpFaceDetectorTask.createFromOptions(vision, {
                            baseOptions: {
                                modelAssetPath: CFG.tasksVision.modelPath,
                                delegate: 'CPU',
                            },
                            runningMode: 'VIDEO',
                            minDetectionConfidence:  CFG.mp.detectMinConf,
                            minSuppressionThreshold: 0.3,
                        });
                    });
                })
                .then(function (detector) {
                    self.detector = detector;
                    state.mpMode  = 'tasks';
                    setKioskMode('tasks');
                    log('MediaPipe Tasks ready');
                })
                .catch(function (e) {
                    state.mpMode = 'none';
                    log('MediaPipe init failed', e);
                    throw e;
                });
        },

        tick: function () {
            if (state.mpMode !== 'tasks' || !this.detector || !video.videoWidth) return;
            try {
                var now    = Math.floor(performance.now());
                var result = this.detector.detectForVideo(video, now);
                var dets   = (result && result.detections) ? result.detections : [];

                // Filter by confidence only (no size filtering)
                var valid = dets.filter(function (d) {
                    return ((d.categories && d.categories[0] && d.categories[0].score) || 0) >= CFG.mp.acceptMinScore;
                });

                // No faces detected
                if (valid.length === 0) {
                    state.faceStatus  = 'none';
                    state.mpBoxCanvas = null;
                    state.smoothedBox = null;  // FIX-01: Reset EMA when face lost
                    return;
                }

                // Always pick the LARGEST face (closest to camera)
                // This handles both single face and multiple faces cases
                var best = valid.reduce(function (a, b) {
                    var aA = (a.boundingBox ? a.boundingBox.width * a.boundingBox.height : 0);
                    var bA = (b.boundingBox ? b.boundingBox.width * b.boundingBox.height : 0);
                    return aA >= bA ? a : b;
                });

                // Warn if multiple faces detected (but still process the largest)
                if (valid.length > 1) {
                    safeSetPrompt('Multiple faces detected.', 'Scanning the closest person.');
                }

                var bb  = best.boundingBox;
                var box = mapVideoBoxToCanvas(toVideoBox(bb));

                window.__kioskDebug = {
                    rawBox: bb,
                    mappedBox: box,
                    videoWidth: video.videoWidth,
                    videoHeight: video.videoHeight
                };
                // bbox debug log disabled
state.faceStatus   = (box && box.w > 20 && box.h > 20) ? 'good' : 'low'; // RELAXED: just check minimum dimensions
                
                // FIX-01: EMA smoothing for bounding box (glides instead of jumps)
                if (!state.smoothedBox) {
                    state.smoothedBox = { x: box.x, y: box.y, w: box.w, h: box.h };
                } else {
                    var a = 0.35; // Alpha: 0.35 gives smooth glide without noticeable lag
                    state.smoothedBox = {
                        x: state.smoothedBox.x + a * (box.x - state.smoothedBox.x),
                        y: state.smoothedBox.y + a * (box.y - state.smoothedBox.y),
                        w: state.smoothedBox.w + a * (box.w - state.smoothedBox.w),
                        h: state.smoothedBox.h + a * (box.h - state.smoothedBox.h)
                    };
                }
                state.mpBoxCanvas  = state.smoothedBox;
                state.mpFaceSeenAt = Date.now();
                this.failStreak    = 0;

                if (state.faceStatus === 'low') {
                    state.mpReadyToFire = false;
                    state.mpStableStart = 0;
                    state.mpPrevCenter  = null;
                    safeSetPrompt('Move closer.', 'Please approach the camera.');
                    return;
                }

                updateStableTracking(box, Date.now());

            } catch (e) {
                this.failStreak++;
                // Always surface the real error so admins can diagnose in DevTools

                if (this.failStreak > 30) {
                    state.mpMode = 'none';

                    log('MediaPipe recurring error, disabling', e);
                }
            }
        },
    };

    // =========
    // stable tracking
    // =========
    function updateStableTracking(box, now) {
        if (!box) { state.mpStableStart = 0; return; }
        var c = { x: box.x + box.w / 2, y: box.y + box.h / 2 };
        if (!state.mpPrevCenter) { state.mpPrevCenter = c; state.mpStableStart = now; return; }
        var move = Math.hypot(c.x - state.mpPrevCenter.x, c.y - state.mpPrevCenter.y);
        state.mpPrevCenter = c;
        
        // FIX-02: Walk-by mode - velocity-based decay instead of hard reset
        if (move > CFG.gating.stableMaxMovePx * 2) {
            // Actually running/thrashing - hard reset
            state.mpStableStart = 0;
            safeSetPrompt('Hold still.', '');
            return;
        }
        if (move > CFG.gating.stableMaxMovePx) {
            // Walking pace - don't reset, let timer keep ticking
            if (state.mpStableStart === 0) state.mpStableStart = now;
        }
        
        if (state.mpStableStart === 0) state.mpStableStart = now;
        if ((now - state.mpStableStart) < CFG.mp.stableNeededMs) {
            safeSetPrompt('Hold still.', '');
            return;
        }
        state.mpReadyToFire = true;
    }

    // =========
    // ETA update
    // =========
            function updateEta(facePresent) {
        if (!facePresent) {
            if (state.locationState === 'pending') { setEta('ETA: locating'); return; }
            if (state.locationState !== 'allowed') { setEta('ETA: blocked'); return; }
            setEta('ETA: idle');
            return;
        }

        if (state.locationState === 'pending')     { setEta('ETA: locating');         return; }
        if (state.locationState !== 'allowed')     { setEta('ETA: blocked');          return; }
        if (!state.mpBoxCanvas || state.faceStatus === 'none') { setEta('ETA: waiting');          return; }
        if (state.faceStatus === 'low')            { setEta('ETA: center face');      return; }
        if (state.faceStatus === 'multi')          { setEta('ETA: one face only');    return; }
        if (state.mpReadyToFire)                   { setEta('ETA: scanning');         return; }

        var msLeft = state.mpStableStart > 0
            ? Math.max(0, CFG.mp.stableNeededMs - (Date.now() - state.mpStableStart))
            : CFG.mp.stableNeededMs;

        setEta('ETA: hold (' + (msLeft / 1000).toFixed(1) + 's)');
    }

    // =========
    // unlock UI
    // =========
    function isUnlockAvailable() {
        // SECURITY: Admin unlock disabled on mobile devices
        if (!allowUnlock) return false;
        return !!(ui.unlockBackdrop && ui.unlockPin && ui.unlockSubmit && ui.unlockCancel && ui.unlockErr);
    }

    function openUnlock() {
        if (!isUnlockAvailable()) return;
        if (state.visitorOpen) closeVisitorModal();
        state.unlockOpen = true;
        ui.unlockErr.textContent = '';
        ui.unlockPin.value = '';
        ui.unlockBackdrop.classList.remove('hidden');
        ui.unlockBackdrop.setAttribute('aria-hidden', 'false');
        if (ui.kioskRoot) ui.kioskRoot.classList.add('unlockOpen');
        setTimeout(function () { if (ui.unlockPin) ui.unlockPin.focus(); }, 50);
    }

    function closeUnlock() {
        if (!isUnlockAvailable()) return;
        state.unlockOpen = false;
        ui.unlockBackdrop.classList.add('hidden');
        ui.unlockBackdrop.setAttribute('aria-hidden', 'true');
        if (ui.kioskRoot) ui.kioskRoot.classList.remove('unlockOpen');
        ui.unlockErr.textContent = '';
        ui.unlockPin.value = '';
    }

    var pendingReturnUrl = ''; // Store return URL for after confirmation

    function submitUnlock() {
        if (!isUnlockAvailable()) return;
        var pin = (ui.unlockPin.value || '').trim();
        if (!pin) { ui.unlockErr.textContent = 'Enter PIN.'; ui.unlockPin.focus(); return; }

        var fd = new FormData();
        fd.append('__RequestVerificationToken', token);
        fd.append('pin', pin);
        fd.append('returnUrl', (document.body && document.body.dataset && document.body.dataset.returnUrl) || '');

        ui.unlockSubmit.disabled = true;
        ui.unlockCancel.disabled = true;
        ui.unlockErr.textContent = '';

        fetch(EP.unlockPin, { method: 'POST', body: fd })
            .then(function (r) {
                // Check for mobile device block (403)
                if (r.status === 403) {
                    closeUnlock();
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
                    // Return a dummy object to stop further processing
                    return { handled: true };
                }
                return r.json();
            })
            .then(function (j) {
                // If already handled (403), skip
                if (j && j.handled) return;
                if (j && j.ok === true) {
                    pendingReturnUrl = (j.returnUrl || '').trim();
                    closeUnlock();
                    showUnlockSuccess(); // Show confirmation modal instead of immediate redirect
                } else {
                    // Check if mobile device blocked (403)
                    if (j && j.error === 'UNLOCK_DISABLED_ON_MOBILE') {
                        closeUnlock();
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
                        ui.unlockErr.textContent = 'Invalid PIN.';
                        if (ui.unlockPin) ui.unlockPin.focus();
                    }
                }
            })
            .catch(function () {
                ui.unlockErr.textContent = 'Unlock failed.';
            })
            .finally(function () {
                ui.unlockSubmit.disabled = false;
                ui.unlockCancel.disabled = false;
            });
    }

    function showUnlockSuccess() {
        if (!ui.unlockSuccessBackdrop) return;
        // Pause scanning while admin modal is open
        state.adminModalOpen = true;
        ui.unlockSuccessBackdrop.classList.remove('hidden');
        ui.unlockSuccessBackdrop.setAttribute('aria-hidden', 'false');
        setPrompt('Admin access granted.', 'Choose where to go.');
    }

    function closeUnlockSuccess() {
        if (!ui.unlockSuccessBackdrop) return;
        ui.unlockSuccessBackdrop.classList.add('hidden');
        ui.unlockSuccessBackdrop.setAttribute('aria-hidden', 'true');
        state.adminModalOpen = false;
        pendingReturnUrl = '';
        setPrompt('Ready.', 'Look at the camera.');
    }

    function goToAdmin() {
        var targetUrl = pendingReturnUrl || (appBase + 'Admin/Index');

        window.location.href = targetUrl;
    }

    function stayInKiosk() {
        closeUnlockSuccess();
        // User stays in kiosk, can continue scanning
    }

    function wireUnlockUi() {
        if (!isUnlockAvailable()) return;

        ui.unlockCancel.addEventListener('click', closeUnlock);
        ui.unlockSubmit.addEventListener('click', submitUnlock);
        if (ui.unlockClose) ui.unlockClose.addEventListener('click', closeUnlock);

        ui.unlockBackdrop.addEventListener('click', function (e) {
            if (e.target === ui.unlockBackdrop) closeUnlock();
        });

        ui.unlockPin.addEventListener('keydown', function (e) {
            if (e.key === 'Enter')  { e.preventDefault(); submitUnlock(); }
            if (e.key === 'Escape') { e.preventDefault(); closeUnlock(); }
        });

        // Wire up success modal buttons
        if (ui.unlockGoAdmin) ui.unlockGoAdmin.addEventListener('click', goToAdmin);
        if (ui.unlockStayKiosk) ui.unlockStayKiosk.addEventListener('click', stayInKiosk);
        if (ui.unlockSuccessBackdrop) {
            ui.unlockSuccessBackdrop.addEventListener('click', function (e) {
                if (e.target === ui.unlockSuccessBackdrop) stayInKiosk();
            });
        }

        // Ctrl+Shift+Space triggers admin unlock from anywhere on page
        document.addEventListener('keydown', function (e) {
            if (!isUnlockAvailable())  return;
            if (state.unlockOpen)      return;
            var isSpace = (e.code === 'Space') || (e.key === ' ') || (e.keyCode === 32);
            if (e.ctrlKey && e.shiftKey && isSpace) {
                e.preventDefault();
                if (state.visitorOpen) closeVisitorModal();
                openUnlock();
            }
        }, true);

        // Double-click on brand label also opens unlock
        var brandEl = document.querySelector('#topLeft .brand');
        if (brandEl) brandEl.addEventListener('dblclick', openUnlock);
    }

    // =========
    // GPS + office resolve
    // =========
        function startGpsIfAvailable() {
        setLocationState(
            'pending',
            'Preparing kiosk',
            'Please wait while the kiosk verifies the current office location.',
            'Checking location...'
        );

        if (!('geolocation' in navigator)) {
            if (!isMobile) {
                resolveOfficeDesktopOnce();
                return;
            }

            setLocationState(
                'blocked',
                'Location not available',
                'Enable location services to use the DILG Region XII kiosk.',
                'Location not available'
            );
            return;
        }

        var isSecure = (location.protocol === 'https:' || location.hostname === 'localhost' || location.hostname === '127.0.0.1');
        if (!isSecure) {
            if (!isMobile) {
                resolveOfficeDesktopOnce();
                return;
            }

            setLocationState(
                'blocked',
                'Secure connection required',
                'Use HTTPS so the kiosk can access location services.',
                'HTTPS required'
            );
            return;
        }

        navigator.geolocation.watchPosition(
            function (pos) {
                var prevLat = state.gps.lat;
                var prevLon = state.gps.lon;

                state.gps.lat = pos.coords.latitude;
                state.gps.lon = pos.coords.longitude;
                state.gps.accuracy = pos.coords.accuracy;

                if (prevLat != null && prevLon != null) {
                    var movedLat = Math.abs(prevLat - state.gps.lat);
                    var movedLon = Math.abs(prevLon - state.gps.lon);
                    if (movedLat > 0.0002 || movedLon > 0.0002) {
                        state.officeVerifiedUntil = 0;
                    }
                }

                if (state.locationState !== 'allowed') {
                    setLocationState(
                        'pending',
                        'Location captured',
                        'Verifying the current office area.',
                        'Verifying location...'
                    );
                }

                resolveOfficeIfNeeded();
            },
            function (err) {
                state.gps.lat = state.gps.lon = state.gps.accuracy = null;

                if (!isMobile) {
                    setLocationState(
                        'pending',
                        'Checking kiosk office',
                        'Looking for the registered office profile for this kiosk.',
                        'Checking office...'
                    );
                    resolveOfficeDesktopOnce();
                    return;
                }

                var title = 'Unable to get location';
                var sub = 'Turn on location services and try again.';
                var banner = 'Location unavailable';

                if (err && err.code === 1) {
                    title = 'Location access denied';
                    sub = 'Allow location access to continue using the DILG Region XII kiosk.';
                    banner = 'Location denied';
                } else if (err && err.code === 2) {
                    title = 'Location unavailable';
                    sub = 'The device could not detect the current location. Move to an open area and try again.';
                    banner = 'Location unavailable';
                } else if (err && err.code === 3) {
                    title = 'Location timed out';
                    sub = 'The location request took too long. Please try again.';
                    banner = 'Location timeout';
                }

                setLocationState('blocked', title, sub, banner);
            },
            { enableHighAccuracy: true, maximumAge: 500, timeout: 6000 }
        );
    }

    // Haversine distance in meters between two GPS coordinates (fast approx for short distances)
    function gpsDistanceMeters(lat1, lon1, lat2, lon2) {
        if (lat1 == null || lon1 == null || lat2 == null || lon2 == null) return 0;
        var R = 6371000;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.sin(dLat / 2) * Math.sin(dLat / 2) +
                Math.cos(lat1 * Math.PI / 180) * Math.cos(lat2 * Math.PI / 180) *
                Math.sin(dLon / 2) * Math.sin(dLon / 2);
        return R * 2 * Math.atan2(Math.sqrt(a), Math.sqrt(1 - a));
    }

    function resolveOfficeIfNeeded() {
        var t = Date.now();

        // If location was previously verified, check whether GPS has drifted
        // more than 60m from the verified position. If so, force a re-resolve.
        // This catches employees who walk outside the office radius mid-session.
        // 60m threshold is generous enough to ignore natural GPS noise (~3-15m)
        // but tight enough to catch someone leaving the building.
        if (state.locationState === 'allowed' &&
            state.lastVerifiedLat != null &&
            state.gps.lat != null)
        {
            var drift = gpsDistanceMeters(
                state.lastVerifiedLat, state.lastVerifiedLon,
                state.gps.lat, state.gps.lon);
            if (drift > 60) {
                // GPS moved significantly -- invalidate cached verification
                state.officeVerifiedUntil = 0;
                state.lastVerifiedLat = null;
                state.lastVerifiedLon = null;
            }
        }

        if (state.locationState === 'allowed' &&
            state.currentOffice &&
            state.currentOffice.id &&
            t < (state.officeVerifiedUntil || 0)) {
            return Promise.resolve();
        }

        if (t < (state.officeResolveRetryUntil || 0)) {
            return Promise.resolve();
        }

        if (t - state.lastResolveAt < CFG.server.resolveMs) {
            return Promise.resolve();
        }

        state.lastResolveAt = t;

        if (state.gps.lat == null || state.gps.lon == null || state.gps.accuracy == null) {
            if (!isMobile) {
                return resolveOfficeDesktopOnce();
            }

            setLocationState(
                'pending',
                'Checking location',
                'Waiting for a GPS fix. Stay within the DILG Region XII office area.',
                'Locating...'
            );
            return Promise.resolve();
        }

        setLocationState(
            'pending',
            'Checking location',
            'Please wait while we verify the office radius.',
            'Checking location...'
        );

        var fd = new FormData();
        fd.append('__RequestVerificationToken', token);
        fd.append('lat', state.gps.lat);
        fd.append('lon', state.gps.lon);
        fd.append('accuracy', state.gps.accuracy);

        return fetch(EP.resolveOffice, { method: 'POST', body: fd })
            .then(function (r) {
                if (r.status === 429) {
                    return {
                        ok: false,
                        error: 'RATE_LIMIT_EXCEEDED',
                        retryAfter: Number(r.headers.get('Retry-After') || 0)
                    };
                }
                return r.json();
            })
            .then(function (j) {
                if (!j || j.ok !== true) {
                    if (j && j.error === 'RATE_LIMIT_EXCEEDED') {
                        var retryMs = Math.max(1000, Number(j.retryAfter || 0) * 1000);
                        state.officeResolveRetryUntil = Date.now() + retryMs;
                        var mappedBusy = humanizeResolveError(j.error, j.retryAfter, j.requiredAccuracy);
                        setLocationState('pending', mappedBusy.title, mappedBusy.sub, mappedBusy.banner);
                        return;
                    }

                    resetScanState();
                    setLocationState(
                        'blocked',
                        'Unable to verify location',
                        (j && j.error) ? String(j.error) : 'Please try again or contact the system administrator.',
                        'Location check failed'
                    );
                    return;
                }

                if (j.allowed === false) {
                    state.currentOffice.id = null;
                    state.currentOffice.name = null;
                    state.officeVerifiedUntil = 0;

                    var mappedBlocked = humanizeResolveError(j.reason || j.error, j.retryAfter, j.requiredAccuracy);
                    setLocationState('blocked', mappedBlocked.title, mappedBlocked.sub, mappedBlocked.banner);
                    return;
                }

                state.currentOffice.id = j.officeId;
                state.currentOffice.name = j.officeName;
                state.officeVerifiedUntil = Date.now() + (isMobile ? 60 * 1000 : 5 * 60 * 1000);
                state.officeResolveRetryUntil = 0;
                // Store GPS position at verification time for drift detection
                state.lastVerifiedLat = state.gps.lat;
                state.lastVerifiedLon = state.gps.lon;

                setLocationState(
                    'allowed',
                    'Location verified',
                    'You may now look at the camera for attendance.',
                    state.currentOffice.name || 'Office verified'
                );
            })
            .catch(function () {
                setLocationState(
                    'blocked',
                    'Location check failed',
                    'The kiosk could not verify the office location. Please try again.',
                    'Location check failed'
                );
            });
    }

    function resolveOfficeDesktopOnce() {
        var t = Date.now();

        if (isMobile) return Promise.resolve();

        if (state.currentOffice &&
            state.currentOffice.name &&
            state.locationState === 'allowed' &&
            t < (state.officeVerifiedUntil || 0)) {
            return Promise.resolve();
        }

        if (t < (state.officeResolveRetryUntil || 0)) {
            return Promise.resolve();
        }

        setLocationState(
            'pending',
            'Checking kiosk office',
            'Looking for the registered office profile for this kiosk.',
            'Checking office...'
        );

        var fd = new FormData();
        fd.append('__RequestVerificationToken', token);

        return fetch(EP.resolveOffice, { method: 'POST', body: fd })
            .then(function (r) {
                if (r.status === 429) {
                    var retry = Number(r.headers.get('Retry-After') || 0);
                    return { ok: false, error: 'RATE_LIMIT_EXCEEDED', retryAfter: retry };
                }
                return r.json();
            })
            .then(function (j) {
                if (j && j.ok === true && j.allowed !== false) {
                    state.currentOffice.id = j.officeId;
                    state.currentOffice.name = j.officeName;
                    state.officeVerifiedUntil = Date.now() + (5 * 60 * 1000);
                    state.officeResolveRetryUntil = 0;
                    state.lastVerifiedLat = state.gps.lat;
                    state.lastVerifiedLon = state.gps.lon;

                    setLocationState(
                        'allowed',
                        'Location verified',
                        'You may now look at the camera for attendance.',
                        state.currentOffice.name || 'Office verified'
                    );
                    return;
                }

                var retryMs = Math.max(0, Number(j && j.retryAfter || 0) * 1000);
                state.officeResolveRetryUntil = retryMs > 0 ? (Date.now() + retryMs) : 0;

                var mapped = humanizeResolveError(j && (j.reason || j.error), j && j.retryAfter, j && j.requiredAccuracy);
                setLocationState('blocked', mapped.title, mapped.sub, mapped.banner);
            })
            .catch(function () {
                var mapped = humanizeResolveError('UNKNOWN');
                setLocationState('blocked', mapped.title, mapped.sub, mapped.banner);
            });
    }

    // =========
    // burst capture for robust identification
    // Captures multiple frames for consensus voting (mobile + enhanced kiosk)
    // =========
    function captureBurstFrames(count, intervalMs) {
        return new Promise(function(resolve, reject) {
            // Wait for video to be ready
            if (!video.videoWidth || !video.videoHeight) {

                reject(new Error('Video not ready'));
                return;
            }
            
            var frames = [];
            var canvas = document.createElement('canvas');
            canvas.width = video.videoWidth;
            canvas.height = video.videoHeight;
            var ctx = canvas.getContext('2d');
            var attempts = 0;
            var maxAttempts = count * 2; // Allow some retries
            
            function captureFrame(index) {
                if (frames.length >= count) {

                    resolve(frames);
                    return;
                }
                
                if (attempts >= maxAttempts) {

                    resolve(frames);
                    return;
                }
                
                attempts++;
                
                try {
                    ctx.drawImage(video, 0, 0);
                    canvas.toBlob(function(blob) {
                        if (blob && blob.size > 1000) { // Ensure blob is valid and not empty
                            frames.push(blob);

                        } else {

                        }
                        
                        if (frames.length < count && attempts < maxAttempts) {
                            setTimeout(function() { captureFrame(index + 1); }, intervalMs);
                        } else {
                            resolve(frames);
                        }
                    }, 'image/jpeg', 0.90);  // Higher quality for better recognition
                } catch (e) {

                    resolve(frames);
                }
            }
            
            captureFrame(0);
        });
    }

    // =========
    // attendance submit
    // OPT-SPEED-02: Send face bbox to server so it can skip detection (saves ~150ms)
    // BURST MODE: For mobile and enhanced kiosk, captures 10 frames for consensus
    // =========
    function submitAttendance(blob) {
        // Prevent double-fire: if already submitting, skip
        if (state.submitInProgress) {
            return Promise.resolve({ ok: false, error: 'ALREADY_SUBMITTING' });
        }
        
        if (state.attendAbortCtrl) {
            try { state.attendAbortCtrl.abort(); } catch (e) {}
        }

        state.submitInProgress = true;
        state.attendAbortCtrl = new AbortController();
        var signal = state.attendAbortCtrl.signal;

        state.lastCaptureAt = Date.now();
        setPrompt('Scanning.', 'Hold still.');

        var fd = new FormData();
        fd.append('__RequestVerificationToken', token);
        fd.append('image', blob, 'capture.jpg');
        if (state.gps.lat      != null) fd.append('lat',      state.gps.lat);
        if (state.gps.lon      != null) fd.append('lon',      state.gps.lon);
        if (state.gps.accuracy != null) fd.append('accuracy', state.gps.accuracy);
        
        // Include device token for persistent device identification
        var deviceToken = getDeviceToken();
        if (deviceToken) fd.append('deviceToken', deviceToken);
        
        // Send face bbox so server can skip detection (major speedup)
        var faceBox = getFaceBoxForServer();
        if (faceBox) {
            fd.append('faceX', faceBox.x);
            fd.append('faceY', faceBox.y);
            fd.append('faceW', faceBox.w);
            fd.append('faceH', faceBox.h);
        }

        return fetch(EP.attend, { method: 'POST', body: fd, credentials: 'same-origin', signal: signal })
            .then(function (r) {
                if (r.status === 429 || r.status === 503) {
                    return {
                        ok: false,
                        error: r.status === 429 ? 'RATE_LIMIT_EXCEEDED' : 'SYSTEM_BUSY',
                        retryAfter: Number(r.headers.get('Retry-After') || 0)
                    };
                }
                return r.json();
            })
            .then(function (j) {

                handleAttendanceResponse(j);
            })
            .catch(function (e) {
                if (e && e.name === 'AbortError') return;
                state.backoffUntil = Date.now() + 2000;
                setPrompt('System error.', 'Reload the page or check the server.');
            })
            .finally(function () {
                state.submitInProgress = false;
            });
    }

    // =========
    // BURST attendance submit for mobile
    // Sends 3 frames for consensus voting - optimized for speed
    // =========
    function submitBurstAttendance(blobs, onSwalShownCallback) {
        if (state.submitInProgress) {

            return Promise.resolve({ ok: false, error: 'ALREADY_SUBMITTING' });
        }

        state.submitInProgress = true;

        if (state.attendAbortCtrl) {
            try { state.attendAbortCtrl.abort(); } catch (e) { }
        }

        state.attendAbortCtrl = new AbortController();
        var signal = state.attendAbortCtrl.signal;

        var fd = new FormData();
        fd.append('__RequestVerificationToken', token);
        fd.append('frameCount', blobs.length);
        
        blobs.forEach(function(blob, index) {
            fd.append('frame_' + index, blob, 'frame_' + index + '.jpg');
        });
        
        if (state.gps.lat      != null) fd.append('lat',      state.gps.lat);
        if (state.gps.lon      != null) fd.append('lon',      state.gps.lon);
        if (state.gps.accuracy != null) fd.append('accuracy', state.gps.accuracy);
        
        // CRITICAL FIX: Send face bbox so server can skip detection (saves ~150ms per frame!)
        var faceBox = getFaceBoxForServer();
        if (faceBox) {
            fd.append('faceX', faceBox.x);
            fd.append('faceY', faceBox.y);
            fd.append('faceW', faceBox.w);
            fd.append('faceH', faceBox.h);
        }
        
        // Include device token for persistent device identification
        var deviceToken = getDeviceToken();
        if (deviceToken) fd.append('deviceToken', deviceToken);

        return fetch(appBase + 'Kiosk/AttendBurst', { 
            method: 'POST', 
            body: fd, 
            credentials: 'same-origin', 
            signal: signal 
        })
            .then(function (r) {
                if (r.status === 429 || r.status === 503) {
                    return {
                        ok: false,
                        error: r.status === 429 ? 'RATE_LIMIT_EXCEEDED' : 'SYSTEM_BUSY',
                        retryAfter: Number(r.headers.get('Retry-After') || 0)
                    };
                }
                return r.json();
            })
            .then(function (j) {
                // Handle the same as regular submitAttendance
                handleAttendanceResponse(j, onSwalShownCallback);
            })
            .catch(function (e) {
                if (e && e.name === 'AbortError') return;
                state.backoffUntil = Date.now() + 2000;
                setPrompt('System error.', 'Reload the page or check the server.');
            })
            .finally(function () {
                state.submitInProgress = false;
            });
    }
    
    // Shared attendance response handler
    function handleAttendanceResponse(j, onSwalShownCallback) {

        if (!j) {
            // No response data - silent fail
            return;
        }

        if (typeof j.liveness === 'number') {
            var p  = Number(j.liveness);
            var th = (j.threshold != null) ? Number(j.threshold) : null;
            var threshold = th !== null ? th : 0.75;
            var cls;
            if (p >= threshold) cls = 'live-pass';
            else if (p >= threshold * 0.80) cls = 'live-near';
            else cls = 'live-fail';

            setLiveness(p, th, cls);
            state.latestLiveness = p;
            state.livenessThreshold = threshold;
        }

        var err = j.error || '';
        var retryMs = Math.max(1500, Number(j.retryAfter || 0) * 1000);

        if (err === 'RATE_LIMIT_EXCEEDED' || err === 'SYSTEM_BUSY' || err === 'REQUEST_TIMEOUT') {
            state.backoffUntil = Date.now() + retryMs;
        } else if (j.ok === true) {
            state.backoffUntil = 0;
        }

        if (j.ok === true) {
            // DEBUG: Log device token - check both direct and nested locations
            var deviceToken = j.deviceToken || (j.data && j.data.deviceToken);
            if (deviceToken) {

                setDeviceToken(deviceToken);
            } else {

            }
            
            // Reset consecutive failure counter on success
            state.consecutiveFailures = 0;
            
            if (j.mode !== 'VISITOR') {
                var evt  = (j.eventType || '').toUpperCase();
                var name = j.displayName || j.name || 'Employee';
                var isTimeIn = evt === 'IN';
                
                // Play success sound
                if (window.FaceAttendAudio) {
                    FaceAttendAudio.playSuccess();
                }
                
                // MOBILE: Redirect to employee page after success
                var shouldRedirectMobile = isMobile && !document.body.getAttribute('data-force-kiosk');
                
                // DEBUG: Log redirect decision


                
                if (shouldRedirectMobile) {

                    if (window.Swal) {
                        Swal.fire({
                            title: '<i class="fa-solid fa-circle-check" style="color: #22c55e; font-size: 3rem; margin-bottom: 0.5rem;"></i>',
                            html: '<div style="font-size: 1.25rem; font-weight: 700; color: #1f2937; margin-bottom: 0.25rem;">' + (isTimeIn ? 'Time In' : 'Time Out') + '</div>' +
                                  '<div style="font-size: 1.5rem; font-weight: 600; color: #059669;">' + name + '</div>',
                            icon: null,
                            toast: false,
                            position: 'center',
                            showConfirmButton: false,
                            timer: 1500,
                            background: '#f0fdf4',
                            backdrop: 'rgba(0,0,0,0.3)',
                            didOpen: function() {
                                // TIMING: Swal is now visible - call the callback
                                if (typeof onSwalShownCallback === 'function') {
                                    onSwalShownCallback();
                                }
                            }
                        }).then(function() {
                            window.location.href = appBase + 'MobileRegistration/Employee';
                        });
                    } else {
                        toastSuccess((isTimeIn ? 'Time In' : 'Time Out') + ' -- ' + name);
                        setTimeout(function() {
                            window.location.href = appBase + 'MobileRegistration/Employee';
                        }, 2500);
                    }
                    
                    setPrompt(isTimeIn ? 'Time In recorded.' : 'Time Out recorded.', name);
                    armPostScanHold(CFG.postScan.holdMs);
                } else {
                    if (window.Swal) {
                        var iconClass = isTimeIn ? 'fa-circle-check' : 'fa-circle-arrow-right';
                        var iconColor = isTimeIn ? '#22c55e' : '#3b82f6';
                        Swal.fire({
                            title: '<i class="fa-solid ' + iconClass + '" style="color: ' + iconColor + '; font-size: 3rem; margin-bottom: 0.5rem;"></i>',
                            html: '<div style="font-size: 1.25rem; font-weight: 700; color: #1f2937; margin-bottom: 0.25rem;">' + (isTimeIn ? 'Time In' : 'Time Out') + '</div>' +
                                  '<div style="font-size: 1.5rem; font-weight: 600; color: #059669;">' + name + '</div>' +
                                  '<div style="font-size: 0.875rem; color: #6b7280; margin-top: 8px;">' + (j.message || 'Attendance recorded') + '</div>',
                            icon: null,
                            toast: false,
                            position: 'center',
                            showConfirmButton: false,
                            timer: 2500,
                            timerProgressBar: true,
                            background: isTimeIn ? '#f0fdf4' : '#eff6ff',
                            backdrop: 'rgba(0,0,0,0.3)',
                            width: '400px',
                            customClass: {
                                popup: 'attendance-success-popup',
                                title: 'attendance-success-title'
                            },
                            didOpen: function(popup) {
                                popup.style.borderRadius = '16px';
                                popup.style.boxShadow = '0 20px 60px rgba(0,0,0,0.3)';
                            }
                        });
                    } else {
                        toastSuccess((isTimeIn ? 'Time In' : 'Time Out') + ' -- ' + name);
                    }
                    
                    setPrompt(isTimeIn ? 'Time In recorded.' : 'Time Out recorded.', name);
                    armPostScanHold(CFG.postScan.holdMs);
                }
            } else {
                // Track consecutive visitor/failure attempts
                state.consecutiveFailures = (state.consecutiveFailures || 0) + 1;
                
                // Provide helpful feedback after multiple failed attempts
                if (state.consecutiveFailures >= 3) {
                    if (window.Swal) {
                        Swal.fire({
                            title: 'Having trouble?',
                            text: 'Make sure you are enrolled in the system. Try moving closer to the camera, look straight ahead, and ensure good lighting.',
                            icon: 'info',
                            confirmButtonText: 'Got it',
                            confirmButtonColor: '#3b82f6',
                            background: '#0f172a',
                            color: '#f8fafc',
                            timer: 5000,
                            timerProgressBar: true
                        });
                    }
                    // Reset counter after showing tip
                    state.consecutiveFailures = 0;
                }
                
                openVisitorModal(j);
            }
            return;
        }

        // DEVICE FLOW: Handle new action responses
        var action = j.action || '';
        
        if (action === 'REGISTER_DEVICE') {
            if (isPersonalMobile && !isForcedKioskMode()) {
                state.deviceChecked = true;
                state.deviceStatus = 'not_registered';
                setIdleUi(true);
                setPrompt('Device not registered.', 'Tap "Register This Device" below.');
                setMobileRegisterVisible(true);
                armPostScanHold(5000);
            } else {
                if (confirm('This device is not registered. Register "' + (j.employeeName || 'your device') + '" now?')) {
                    registerDevice(j.employeeId, j.employeeName);
                } else {
                    setPrompt('Device not registered.', 'Please contact administrator.');
                    armPostScanHold(3000);
                }
            }
            return;
        }
        
        if (action === 'DEVICE_PENDING') {
            state.deviceChecked = true;
            state.deviceStatus = 'pending';
            setIdleUi(true);
            setMobileRegisterVisible(false);
            toastError('Your device registration is pending admin approval.');
            setPrompt('Device pending approval.', 'Please wait for admin to approve.');
            armPostScanHold(3000);
            return;
        }
        
        if (action === 'DEVICE_BLOCKED') {
            state.deviceChecked = true;
            state.deviceStatus = 'blocked';
            setIdleUi(true);
            setMobileRegisterVisible(false);
            toastError('This device has been blocked.');
            setPrompt('Device blocked.', 'Please contact administrator.');
            armPostScanHold(3000);
            return;
        }
        
        if (action === 'SELF_ENROLL') {
            // MOBILE: Check if face matched a different employee
            var matchedEmployee = j.matchedEmployee || j.matchedEmployeeId || j.employeeName;
            
            if (isPersonalMobile && !isForcedKioskMode()) {
                if (matchedEmployee && matchedEmployee !== 'Unknown') {
                    // Face matched a DIFFERENT employee - show warning
                    toastError('This device belongs to ' + matchedEmployee + '. Please use your own device.');
                    Swal.fire({
                        title: 'Wrong Device',
                        html: '<div style="font-size: 1.1rem;">This device belongs to <strong>' + matchedEmployee + '</strong>.<br>Please use your own registered device.</div>',
                        icon: 'warning',
                        confirmButtonText: 'Understood',
                        confirmButtonColor: '#f59e0b',
                        background: '#0f172a',
                        color: '#f8fafc'
                    });
                    setPrompt('Wrong employee detected.', 'Use your own device.');
                } else {
                    // Face not recognized - silently retry for mobile
                    // Don't show enrollment UI, just retry
                    setPrompt('Not recognized.', 'Retrying...');
                    setTimeout(function() {
                        // Reset and allow immediate retry
                        state.submitInProgress = false;
                        state.liveInFlight = false;
                        state.mpReadyToFire = true;
                    }, 1000);
                }
                armPostScanHold(3000);
            } else {
                // Kiosk mode - show normal enrollment prompt
                setPrompt('Enrollment required.', 'Please register to use the system.');
                armPostScanHold(5000);
            }
            return;
        }

        if (err === 'ALREADY_SCANNED' || err === 'TOO_SOON') {
            toastError(j.message || 'Already scanned. Please wait.');
            // MOBILE: Redirect to employee page after showing message
            var isPersonalMobile = /iPhone|iPad|iPod|Android.*Mobile|Windows Phone/i.test(navigator.userAgent);
            var isForcedKiosk = document.body.getAttribute('data-force-kiosk') === 'true';
            if (isPersonalMobile && !isForcedKiosk) {
                setTimeout(function() {
                    window.location.href = (window.appBase || '/') + 'MobileRegistration/Employee';
                }, 2000);
            }
            armPostScanHold(CFG.postScan.holdMs);
        } else if (err === 'LIVENESS_FAIL') {
            // Visual feedback only - bounding box color + status bar shows liveness state
            // No toast - user sees red bounding box and "Liveness failed" on status bar
            armPostScanHold(1500);
        } else if (err === 'NOT_RECOGNIZED') {
            // Face not recognized - give helpful feedback
            toastError('Face not recognized. Try moving closer or adjusting angle.');
            setPrompt('Face not recognized.', 'Try moving closer or adjusting angle.');
            armPostScanHold(1500);
        } else if (err === 'WRONG_DEVICE') {
            // Mobile: Device belongs to different employee
            var matchedEmployee = (j.details && j.details.matchedEmployee) || j.matchedEmployee || 'another employee';
            toastError('This device is registered to ' + matchedEmployee + '. Please use your own device.');
            Swal.fire({
                title: 'Wrong Device',
                html: '<div style="font-size: 1.1rem;">This device is registered to <strong>' + matchedEmployee + '</strong>.<br>Please use your own registered device.</div>',
                icon: 'warning',
                confirmButtonText: 'Understood',
                confirmButtonColor: '#f59e0b',
                background: '#0f172a',
                color: '#f8fafc'
            });
            setPrompt('Wrong employee detected.', 'Use your own device.');
            armPostScanHold(5000);
        } else if (err === 'RATE_LIMIT_EXCEEDED' || err === 'SYSTEM_BUSY') {
            toastError('System busy. Please wait a moment and try again.');
            setPrompt('System busy.', 'Please wait.');
            armPostScanHold(retryMs);
            return;
        } else if (err === 'REQUEST_TIMEOUT') {
            toastError('Scan timed out. Please try again.');
            armPostScanHold(retryMs);
        } else {
            toastError(j.message || err || 'Scan failed.');
            armPostScanHold(1500);
        }

        setPrompt('Ready.', 'Stand still. One face only.');
    }

    // =========
    // ULTRA-FAST: WebSocket real-time preview
    // Shows who's detected BEFORE they scan!
    // =========
    function initFastPreview() {
        if (!CFG.fastPreview.enabled) return;
        if (!window.WebSocket) return;
        if (state.fastWs && state.fastWs.readyState === WebSocket.OPEN) return;

        try {
            var ws = new WebSocket(CFG.fastPreview.wsUrl);
            
            ws.onopen = function() {
                log('Fast preview WebSocket connected');
                state.fastPreviewResult = null;
                state.fastPreviewFailCount = 0;
            };
            
            ws.onmessage = function(e) {
                try {
                    var r = JSON.parse(e.data);
                    state.fastPreviewScanning = false;
                    
                    if (r.recognized && r.confidence >= CFG.fastPreview.confidenceThreshold) {
                        // HIGH CONFIDENCE: Show name immediately!
                        state.fastPreviewResult = {
                            name: r.employeeName,
                            confidence: r.confidence,
                            employeeId: r.employeeId
                        };
                        // Update UI with preview name
                        if (!state.liveInFlight && state.faceStatus === 'good') {
                            setPrompt('Hello ' + r.employeeName.split(',')[0] + '!', 'Tap to scan or just hold still.');
                        }
                    } else {
                        state.fastPreviewResult = null;
                    }
                } catch (ex) {
                    state.fastPreviewScanning = false;
                }
            };
            
            ws.onerror = function(e) {
                log('Fast preview WebSocket error', e);
                state.fastPreviewScanning = false;
            };
            
            ws.onclose = function() {
                log('Fast preview WebSocket closed');
                state.fastWs = null;
                state.fastPreviewResult = null;
                // Reconnect after delay (with exponential backoff)
                if (!state.fastPreviewFailCount) state.fastPreviewFailCount = 0;
                state.fastPreviewFailCount++;
                if (state.fastPreviewFailCount <= 3) {
                    var delay = Math.min(30000, 5000 * state.fastPreviewFailCount);
                    setTimeout(initFastPreview, delay);
                } else {
                    log('Fast preview disabled after multiple failures');
                    CFG.fastPreview.enabled = false;
                }
            };
            
            state.fastWs = ws;
        } catch (e) {
            log('Failed to init fast preview', e);
        }
    }

    function doFastPreview() {
        if (!CFG.fastPreview.enabled) return;
        if (!state.fastWs || state.fastWs.readyState !== WebSocket.OPEN) {
            // Only try to reconnect if we haven't failed too many times
            if ((state.fastPreviewFailCount || 0) < 3) {
                initFastPreview();
            }
            return;
        }
        if (state.fastPreviewScanning) return;
        if (!state.mpBoxCanvas || state.faceStatus !== 'good') return;
        
        var now = Date.now();
        if (now - state.fastPreviewLastAt < CFG.fastPreview.previewIntervalMs) return;
        state.fastPreviewLastAt = now;
        
        // Capture small image for preview
        var faceBox = getFaceBoxForServer();
        if (!faceBox) return;
        
        state.fastPreviewScanning = true;
        
        captureCanvas.toBlob(function(blob) {
            if (!blob) {
                state.fastPreviewScanning = false;
                return;
            }
            
            var reader = new FileReader();
            reader.onloadend = function() {
                var base64 = reader.result;
                var msg = JSON.stringify({
                    action: 'recognize',
                    imageBase64: base64,
                    faceX: faceBox.x,
                    faceY: faceBox.y,
                    faceW: faceBox.w,
                    faceH: faceBox.h
                });
                
                try {
                    state.fastWs.send(msg);
                } catch (e) {
                    state.fastPreviewScanning = false;
                }
            };
            reader.readAsDataURL(blob);
        }, 'image/jpeg', 0.50); // Lower quality for preview (faster)
    }

    // =========
    // camera start
    // Camera: Use higher resolution for better face recognition
    // Enrollment uses high quality, so kiosk must match for accurate matching
    function startCamera() {
        return navigator.mediaDevices.getUserMedia({
            video: {
                facingMode: 'user',
                width:      { ideal: 1280, max: 1920 },
                height:     { ideal: 720, max: 1080 },
                frameRate:  { ideal: 30, max: 30 },
            },
            audio: false,
        }).then(function (stream) {
            video.srcObject = stream;
            return video.play();
        });
    }

    // =========
    // main loop
    // =========
            function loop() {
        var doLoop = function () {
            try {
                if (state.unlockOpen) return;
                if (!video.videoWidth || !video.videoHeight) return;

                var now = Date.now();
                mp.tick();

                var facePresent = (
                    (state.mpFaceSeenAt > 0 && (now - state.mpFaceSeenAt) < CFG.idle.faceLostMs) ||
                    state.localPresent
                );

                // Personal mobile phones stay in idle until the current device is active.
                var mobileDeviceGateActive = (
                    isPersonalMobile &&
                    !isForcedKioskMode() &&
                    state.deviceChecked &&
                    state.deviceStatus !== 'active'
                );

                var shouldIdle = (!facePresent || state.locationState !== 'allowed' || mobileDeviceGateActive);

                if (shouldIdle) {
                    if (!state.wasIdle) {
                        resetScanState();
                    }

                    state.wasIdle = true;
                    setIdleUi(true);

                    if (state.locationState === 'pending') {
                        safeSetPrompt('Checking location.', state.locationSub || 'Please wait while we verify your office area.');
                        setMobileRegisterVisible(false);
                        updateEta(true);
                    } else if (state.locationState !== 'allowed') {
                        safeSetPrompt(
                            state.locationTitle || 'Location required.',
                            state.locationSub || 'Move into the allowed office area to continue.'
                        );
                        setMobileRegisterVisible(false);
                        updateEta(true);
                    } else if (mobileDeviceGateActive) {
                        if (state.deviceStatus === 'not_registered') {
                            setPrompt('Device not registered.', 'Tap "Register This Device" below.');
                            setMobileRegisterVisible(true);
                        } else if (state.deviceStatus === 'pending') {
                            setPrompt('Device pending approval.', 'Please wait for admin approval.');
                            setMobileRegisterVisible(false);
                        } else if (state.deviceStatus === 'blocked') {
                            setPrompt('Device blocked.', 'Please contact administrator.');
                            setMobileRegisterVisible(false);
                        } else {
                            setPrompt('Checking device.', 'Please wait.');
                            setMobileRegisterVisible(false);
                        }
                        updateEta(true);
                    } else if (!facePresent) {
                        setPrompt(
                            'Idle.',
                            'Look at the camera.'
                        );
                        if (isPersonalMobile && state.deviceStatus === 'not_registered') {
                            setMobileRegisterVisible(true);
                        } else {
                            setMobileRegisterVisible(false);
                        }
                        updateEta(false);
                    }

                    resolveOfficeIfNeeded();
                    return;
                }

                if (state.wasIdle) {
                    resetScanState();
                    setPrompt('Ready.', 'Look at the camera.');
                }

                state.wasIdle = false;
                setIdleUi(false);

                resolveOfficeIfNeeded().then(function () {
                    if (state.locationState !== 'allowed') {
                        setIdleUi(true);
                        updateEta(facePresent);
                        return;
                    }

                    if (now < state.backoffUntil) { setPrompt('System busy.', 'Please wait.'); updateEta(true); return; }
                    if (state.visitorOpen) { updateEta(true); return; }
                    if (state.unlockOpen) { updateEta(true); return; }  // Block scan when PIN modal open
                    if (state.adminModalOpen) { updateEta(true); return; }  // Block scan when admin success modal open
                    if (now < (state.scanBlockUntil || 0)) { safeSetPrompt('Please wait.', 'Next scan ready soon.'); updateEta(true); return; }
                    if (state.mpMode !== 'tasks') { safeSetPrompt('System not ready.', 'Face detection unavailable.'); updateEta(true); return; }
                    
                    // PREVENT IMMEDIATE SCAN: Must wait at least 2 seconds after page load
                    // This prevents liveness from firing immediately when webpage loads
                    var timeSincePageLoad = now - pageLoadTime;
                    if (timeSincePageLoad < 2000) { 
                        safeSetPrompt('Initializing...', 'Please wait a moment.'); 
                        updateEta(true); 
                        return; 
                    }

                    // SERVER WARM-UP GATE: Block scans until Dlib + ONNX models fully loaded
                    // The 68-landmark model (~97MB)  pool size takes 15-20s on cold start.
                    // The /Health endpoint reports ready:true when WarmUpState == 1.
                    if (!state.serverReady) {
                        safeSetPrompt('System starting...', 'Models loading, please wait.');
                        updateEta(true);
                        return;
                    }

                    // ULTRA-FAST PREVIEW: Try to recognize face in background
                    doFastPreview();

                    if (state.mpReadyToFire && (now - state.lastCaptureAt) > CFG.server.captureCooldownMs) {
                        // CRITICAL: Check if already submitting to prevent duplicate scans
                        if (state.submitInProgress || state.liveInFlight) {
                            updateEta(true);
                            return;
                        }
                        
                        // OPTIMIZED: Mobile uses 5-frame burst for faster response
                        // Reduced from 10 to 5 frames, 30ms interval for speed
                        var useBurst = isMobile && !document.body.getAttribute('data-force-kiosk');
                        
                        if (useBurst) {
                            // FIX-03: Check frame sharpness before capture to skip blurry frames
                            var faceBox = getFaceBoxForServer();
                            if (faceBox && !isFrameSharp(faceBox)) {
                                // Frame too blurry - skip this cycle, try again next loop
                                state.liveInFlight = false;
                                state.mpReadyToFire = true; // allow immediate retry
                                safeSetPrompt('Too blurry.', 'Please hold still.');
                                return;
                            }
                            
                            // Mobile burst capture - OPTIMIZED for speed
                            state.mpReadyToFire = false;
                            state.mpStableStart = 0;
                            state.liveInFlight = true;
                            setPrompt('Capturing...', 'Hold still');
                            
                            // TIMING: Start tracking performance
                            var timingStart = performance.now();
                            var timingMarkers = {};

                            
                            // Start safety timeout
                            startCaptureSafetyTimeout();
                            
                            captureBurstFrames(3, 30).then(function(blobs) {
                                timingMarkers.captureComplete = performance.now();

                                
                                clearCaptureSafetyTimeout();
                                // Allow submission with at least 1 frame (server will handle fallback)
                                if (blobs.length < 1) {
                                    state.liveInFlight = false;
                                    state.submitInProgress = false;
                                    setPrompt('Capture failed.', 'Please try again.');
                                    return;
                                }

                                timingMarkers.submissionStart = performance.now();
                                
                                submitBurstAttendance(blobs, function onSwalShown() {
                                    // TIMING: Swal is now visible
                                    var timingEnd = performance.now();
                                    timingMarkers.swalShown = timingEnd;
                                    

                                }).finally(function () {
                                    clearCaptureSafetyTimeout();
                                    state.liveInFlight = false;
                                });
                            }).catch(function(err) {
                                clearCaptureSafetyTimeout();
                                // Handle any capture errors
                                state.liveInFlight = false;
                                state.submitInProgress = false;

                                setPrompt('Capture error.', 'Please try again.');
                            });
                        } else {
                            // Desktop/Kiosk single frame
                            captureFrameBlob().then(function (blob) {
                                if (!blob) return;
                                state.mpReadyToFire = false;
                                state.mpStableStart = 0;
                                state.liveInFlight = true;
                                submitAttendance(blob).finally(function () {
                                    state.liveInFlight = false;
                                });
                            });
                        }
                    }

                    updateEta(true);
                });

            } catch (e) {
                // Loop error - silent
                setPrompt('System error.', 'Reload the page or check the server.');
                setEta('ETA: --');
            } finally {
                setTimeout(doLoop, CFG.loopMs);
            }
        };
        doLoop();
    }

    // =========
    // init
    // =========
            (function init() {
        if (!validateConfig()) return;

        startClock();
        wireUnlockUi();
        wireVisitorUi();

        setIdleUi(true);
        setPrompt('Initializing...', 'Loading face detection models (this may take 15 seconds).');
        setEta('ETA: loading');
        setLiveness(null, null, 'live-unk');
        applyLocationUi();

        startGpsIfAvailable();
        resolveOfficeDesktopOnce();

        startCamera()
            .then(function () { return mp.init(); })
            .then(function () { return checkCurrentMobileDeviceState(); })
            .then(function () {
                setIdleUi(true);

                if (isPersonalMobile && !isForcedKioskMode() && state.deviceStatus === 'not_registered') {
                    setPrompt('Device not registered.', 'Tap "Register This Device" below.');
                    setMobileRegisterVisible(true);
                } else if (isPersonalMobile && !isForcedKioskMode() && state.deviceStatus === 'pending') {
                    setPrompt('Device pending approval.', 'Please wait for admin approval.');
                    setMobileRegisterVisible(false);
                } else if (isPersonalMobile && !isForcedKioskMode() && state.deviceStatus === 'blocked') {
                    setPrompt('Device blocked.', 'Please contact administrator.');
                    setMobileRegisterVisible(false);
                } else {
                    setPrompt('Idle.', state.locationSub || 'Please wait while the location is verified.');
                    setMobileRegisterVisible(false);
                }

                setEta(state.locationState === 'allowed' ? 'ETA: idle' : 'ETA: locating');
                setLiveness(null, null, 'live-unk');
                drawLoop();
                localSenseLoop();
                loop();
                initFastPreview(); // Start ultra-fast preview
            })
            .catch(function (e) {
                setIdleUi(true);
                var msg = (e && e.message) ? String(e.message) : '';

                if (msg === 'NEXTGEN_DISABLED' || msg === 'MP_ASSETS_MISSING') {
                    setPrompt('System not ready.', 'Face detection assets are missing.');
                } else {
                    setPrompt('Camera blocked.', 'Allow camera permission and reload.');
                }

                setEta('ETA: blocked');
            });
    })();

    // =========================================================================
    // HANDLE PAGE VISIBILITY - Resume camera when returning from admin
    // =========================================================================
    (function initVisibilityHandling() {
        // When navigating back from admin, the page may be restored from cache
        // but the video stream is paused/frozen. We need to resume it.
        
        function resumeCameraIfNeeded() {
            if (video.paused || video.ended) {
                log('Resuming camera after page restore');
                video.play().catch(function(e) {
                    log('Failed to resume video', e);
                    // If play fails, try restarting camera entirely
                    startCamera().catch(function(e2) {
                        log('Camera restart failed', e2);
                    });
                });
            }
            
            // Reset the local sense data to avoid false motion readings
            lastSenseData = null;
            state.localSeenAt = Date.now();
        }
        
        // Handle visibility change (when user switches back to this tab)
        document.addEventListener('visibilitychange', function() {
            if (!document.hidden) {
                log('Page became visible, resuming camera');
                resumeCameraIfNeeded();
            }
        });
        
        // Handle pageshow - fires when page is restored from bfcache (back button)
        window.addEventListener('pageshow', function(e) {
            if (e.persisted) {
                log('Page restored from cache, resuming camera');
                resumeCameraIfNeeded();
            }
        });
        
        // Also handle focus events as a fallback
        window.addEventListener('focus', function() {
            if (video.paused) {
                log('Window focused, resuming camera');
                resumeCameraIfNeeded();
            }
        });
    })();
    
    // =========================================================================
    // FULLSCREEN MODE for Mobile/Kiosk
    // =========================================================================
    function enterFullscreen() {
        var elem = document.documentElement;
        if (elem.requestFullscreen) {
            return elem.requestFullscreen();
        } else if (elem.webkitRequestFullscreen) {
            return elem.webkitRequestFullscreen();
        } else if (elem.msRequestFullscreen) {
            return elem.msRequestFullscreen();
        }
        return Promise.reject('Fullscreen not supported');
    }
    
    function exitFullscreen() {
        if (document.exitFullscreen) {
            return document.exitFullscreen();
        } else if (document.webkitExitFullscreen) {
            return document.webkitExitFullscreen();
        } else if (document.msExitFullscreen) {
            return document.msExitFullscreen();
        }
        return Promise.reject('Fullscreen not supported');
    }
    
    function isFullscreen() {
        return !!(document.fullscreenElement || 
                  document.webkitFullscreenElement || 
                  document.msFullscreenElement);
    }
    
    // AUTO FULLSCREEN: Enter fullscreen automatically on page load for both mobile and kiosk
    function initAutoFullscreen() {
        // Try to enter fullscreen immediately
        var tryFullscreen = function() {
            if (!isFullscreen()) {
                enterFullscreen().catch(function() {
                    // If immediate fails (common on mobile), wait for user interaction
                });
            }
        };
        
        // Attempt immediately
        tryFullscreen();
        
        // Also attempt on first user interaction (required by some mobile browsers)
        var onFirstInteraction = function() {
            if (!isFullscreen()) {
                enterFullscreen().catch(function() {});
            }
            // Remove listeners after first interaction
            document.removeEventListener('click', onFirstInteraction);
            document.removeEventListener('touchstart', onFirstInteraction);
        };
        
        document.addEventListener('click', onFirstInteraction);
        document.addEventListener('touchstart', onFirstInteraction);
        
        // Re-enter fullscreen if user exits (for kiosk mode)
        document.addEventListener('fullscreenchange', function() {
            // Optional: auto-reenter after delay if in kiosk mode
            // Disabled to allow admin escape - uncomment if strict kiosk needed
            // if (!isFullscreen() && isForcedKiosk) {
            //     setTimeout(tryFullscreen, 500);
            // }
        });
    }
    
    // Initialize auto fullscreen on load
    initAutoFullscreen();

})();

