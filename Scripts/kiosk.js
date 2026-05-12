(function () {
    'use strict';

    // =========
    // module aliases (config, dom refs, endpoints from kiosk-config.js)
    // =========
    var CFG            = KioskConfig.CFG;
    var EP             = KioskConfig.EP;
    var appBase        = KioskConfig.appBase;
    var nextGenEnabled = KioskConfig.nextGenEnabled;
    var token          = KioskConfig.token;
    var ui             = KioskConfig.ui;
    var log            = KioskConfig.log;
    var validateConfig = KioskConfig.validateConfig;

    var startClock = KioskClock.startClock;
    var setEta     = KioskClock.setEta;
    var updateEta  = KioskClock.updateEta;

    var applyLocationUi          = KioskLocation.applyLocationUi;
    var startGpsIfAvailable      = KioskLocation.startGpsIfAvailable;
    var resolveOfficeIfNeeded    = KioskLocation.resolveOfficeIfNeeded;
    var resolveOfficeDesktopOnce = KioskLocation.resolveOfficeDesktopOnce;

    var video  = document.getElementById('kioskVideo');
    var canvas = document.getElementById('overlayCanvas');
    // ctx lives in kiosk-canvas.js

    // Handle ?reset=1 parameter - clears forced kiosk mode.
    if (location.search.includes('reset=1')) {
        document.cookie = 'ForceKioskMode=; path=/; expires=Thu, 01 Jan 1970 00:00:00 GMT';
        history.replaceState(null, '', location.pathname + location.hash);
    }

    // pollServerReady -> KioskWarmup.start() called in init()

    // =========
    // state aliases (from kiosk-state.js)
    // =========
    var state            = KioskState.state;
    var isMobile         = KioskState.isMobile;
    var isPersonalMobile = KioskState.isPersonalMobile;
    var allowUnlock      = KioskState.allowUnlock;
    var pageLoadTime     = KioskState.pageLoadTime;

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

    // setCenterBlock / humanizeResolveError / setLocationState / applyLocationUi -> KioskLocation module

    function getCookieValue(name) {
        var match = document.cookie.match(new RegExp('(?:^|; )' + name.replace(/[.$?*|{}()\[\]\/+^]/g, '\\$&') + '=([^;]*)'));
        return match ? decodeURIComponent(match[1]) : '';
    }

    function isForcedKioskMode() {
        return getCookieValue('ForceKioskMode') === 'true';
    }

    function setMobileRegisterVisible(show) {
        var btn = document.getElementById('mobileRegisterBtn');
        if (!btn) return;
        btn.style.display = show ? '' : 'none';
    }

    // idle map / GPS / office resolve / location state -> KioskMap + KioskLocation modules
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

    // armPostScanHold -> KioskAttendance.armPostScanHold

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
    // antiSpoof display
    // =========
    function setAntiSpoof(p, th, cls) {
        if (!ui.antiSpoofLine) return;
        var hasP  = (typeof p  === 'number') && isFinite(p);
        var hasTh = (typeof th === 'number') && isFinite(th);
        
        // User-friendly text based on antiSpoof state
        var statusText = 'Anti-spoof: --';
        if (hasP) {
            if (cls === 'live-pass') {
                statusText = 'Anti-spoof: PASS (' + p.toFixed(2) + ')';
            } else if (cls === 'live-near') {
                statusText = 'Anti-spoof: CHECKING... (' + p.toFixed(2) + ')';
            } else if (cls === 'live-fail') {
                statusText = 'Anti-spoof: FAILED - use your real face (' + p.toFixed(2) + ')';
            } else {
                statusText = 'Anti-spoof: ' + p.toFixed(2) + (hasTh ? ' / ' + th.toFixed(2) : '');
            }
        }
        
        ui.antiSpoofLine.textContent = statusText;
        ui.antiSpoofLine.classList.remove('live-pass','live-near','live-fail','live-unk');
        ui.antiSpoofLine.classList.add(cls || 'live-unk');
        
        // Update antiSpoof bar visual (interactive bar that fills left to right)
        if (ui.antiSpoofBarFill) {
            var barWidth = hasP ? Math.round(p * 100) + '%' : '0%';
            ui.antiSpoofBarFill.style.width = barWidth;
            ui.antiSpoofBarFill.classList.remove('live-pass','live-near','live-fail','live-unk');
            ui.antiSpoofBarFill.classList.add(cls || 'live-unk');
        }
    }

    // canvas helpers + drawLoop -> KioskCanvas module

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
        state.latestAntiSpoof   = null;
        state.scanLineProgress = 0;
        // CRITICAL: Reset submission flags when resetting scan state
        // This prevents getting stuck in "Capturing..." state
        state.submitInProgress = false;
        state.liveInFlight     = false;
        setAntiSpoof(null, null, 'live-unk');
        setEta('ETA: --');
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

    // captureFrameBlob -> KioskAttendance module
    // mp object, updateStableTracking, isTooSmallFaceNorm -> KioskMediapipe module
    // submitAttendance, handleAttendanceResponse -> KioskAttendance module




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
                KioskMediapipe.tick();

                var facePresent = (
                    (state.mpFaceSeenAt > 0 && (now - state.mpFaceSeenAt) < CFG.idle.faceLostMs) ||
                    state.localPresent
                );

                var shouldIdle = (!facePresent || state.locationState !== 'allowed');

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
                    } else if (!facePresent) {
                        setPrompt(
                            'Idle.',
                            'Look at the camera.'
                        );
                        if (isPersonalMobile && !isForcedKioskMode()) {
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
                    if (now < (state.scanBlockUntil || 0)) { safeSetPrompt('Please wait.', state.blockMessage || 'Next scan ready soon.'); updateEta(true); return; }
                    if (!KioskMediapipe.isReady()) { safeSetPrompt('System not ready.', 'Face detection unavailable.'); updateEta(true); return; }
                    
                    // PREVENT IMMEDIATE SCAN: Must wait at least 2 seconds after page load
                    // This prevents antiSpoof from firing immediately when webpage loads
                    var timeSincePageLoad = now - pageLoadTime;
                    if (timeSincePageLoad < 2000) { 
                        safeSetPrompt('Initializing...', 'Please wait a moment.'); 
                        updateEta(true); 
                        return; 
                    }

                    // SERVER WARM-UP GATE: Block scans until biometric worker health is ready.
                    // The /Health endpoint reports ready:true when WarmUpState == 1.
                    if (!state.serverReady) {
                        safeSetPrompt('System starting...', 'Models loading, please wait.');
                        updateEta(true);
                        return;
                    }

                    if (state.mpReadyToFire && (now - state.lastCaptureAt) > CFG.server.captureCooldownMs) {
                        // CRITICAL: Check if already submitting to prevent duplicate scans
                        if (state.submitInProgress || state.liveInFlight) {
                            updateEta(true);
                            return;
                        }

                        KioskAttendance.captureFrameBlob().then(function (blob) {
                            if (!blob) return;
                            state.mpReadyToFire = false;
                            state.mpStableStart = 0;
                            state.liveInFlight  = true;
                            KioskAttendance.submit(blob).finally(function () {
                                state.liveInFlight = false;
                            });
                        });
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

        KioskCanvas.init(video, canvas, state, ui);
        KioskAttendance.init(video, canvas, state, token, EP, CFG, {
            appBase:                  appBase,
            isMobile:                 isMobile,
            isPersonalMobile:         isPersonalMobile,
            setPrompt:                setPrompt,
            safeSetPrompt:            safeSetPrompt,
            setAntiSpoof:              setAntiSpoof,
            updateEta:                updateEta,
            setIdleUi:                setIdleUi,
            setMobileRegisterVisible: setMobileRegisterVisible,
            toastSuccess:             toastSuccess,
            toastError:               toastError,
            isForcedKioskMode:        isForcedKioskMode,
            openVisitor:              function (j) { KioskVisitor.open(j); }
        });

        KioskClock.init(ui, state, CFG);
        KioskWarmup.start(appBase, state);
        KioskUnlock.init(ui, state, EP, token, appBase, allowUnlock, setPrompt);
        KioskVisitor.init(ui, state, EP, token, CFG, setPrompt, setEta, KioskAttendance.armPostScanHold, toastSuccess, toastError);
        KioskLocation.init(state, EP, token, appBase, isMobile, ui);
        KioskMap.init(state, ui);
        startClock();
        KioskUnlock.wire();
        KioskVisitor.wire();

        setIdleUi(true);
        setPrompt('Initializing...', 'Loading face detection models (this may take 15 seconds).');
        setEta('ETA: loading');
        setAntiSpoof(null, null, 'live-unk');
        applyLocationUi();

        startGpsIfAvailable();
        resolveOfficeDesktopOnce();

        startCamera()
            .then(function () {
                return KioskMediapipe.init(video, canvas, state, CFG, nextGenEnabled, {
                    log:           log,
                    setKioskMode:  setKioskMode,
                    safeSetPrompt: safeSetPrompt
                });
            })
            .then(function () {
                setIdleUi(true);

                if (isPersonalMobile && !isForcedKioskMode()) {
                    setPrompt('Employee registration available.', 'Tap "Register Employee" below.');
                    setMobileRegisterVisible(true);
                } else {
                    setPrompt('Idle.', state.locationSub || 'Please wait while the location is verified.');
                    setMobileRegisterVisible(false);
                }

                setEta(state.locationState === 'allowed' ? 'ETA: idle' : 'ETA: locating');
                setAntiSpoof(null, null, 'live-unk');
                KioskCanvas.start();
                localSenseLoop();
                loop();
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


    // fullscreen / visibility handling -> KioskFullscreen module
    KioskFullscreen.initAutoFullscreen();
    KioskFullscreen.initVisibilityHandling(video, startCamera);

})();

