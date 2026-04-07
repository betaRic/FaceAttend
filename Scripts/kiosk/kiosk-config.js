// kiosk-config.js
// Configuration constants, DOM references, endpoints, and config validation.
// Exposes window.KioskConfig — must be loaded before kiosk.js.
(function () {
    'use strict';

    var el = function (id) {
        return FaceAttend.Utils ? FaceAttend.Utils.el(id) : document.getElementById(id);
    };

    var ui = {
        officeLine:        el('officeLine'),
        timeLine:          el('timeLine'),
        dateLine:          el('dateLine'),
        livenessLine:      el('livenessLine'),
        livenessBarFill:   el('livenessBarFill'),
        scanEtaLine:       el('scanEtaLine'),

        unlockBackdrop:        el('unlockBackdrop'),
        unlockPin:             el('unlockPin'),
        unlockErr:             el('unlockErr'),
        unlockCancel:          el('unlockCancel'),
        unlockSubmit:          el('unlockSubmit'),
        unlockClose:           el('unlockClose'),
        unlockSuccessBackdrop: el('unlockSuccessBackdrop'),
        unlockGoAdmin:         el('unlockGoAdmin'),
        unlockStayKiosk:       el('unlockStayKiosk'),

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

        idleMapContainer:  document.getElementById('idleMapContainer'),
        idleMap:           document.getElementById('idleMap'),
    };

    var token = FaceAttend.Utils ? FaceAttend.Utils.getCsrfToken() :
                ((document.querySelector('input[name="__RequestVerificationToken"]') || {}).value || '');
    var appBase = (document.body.getAttribute('data-app-base') || '/').replace(/\/?$/, '/');
    var nextGenEnabled = (document.body.getAttribute('data-nextgen') || 'false').toLowerCase() === 'true';

    // All timings optimised vs original
    var CFG = {
        debug: true,

        // 60ms loop (was 120) -- 2x faster face detection response
        loopMs: 60,

        mp: {
            detectMinConf:     0.30,
            acceptMinScore:    0.60,
            stableFramesMin:   2,
            // Raised from 20ms: requires person to genuinely hold still, prevents walk-by captures
            stableNeededMs:    600,
            multiMinAreaRatio: 0.015,
        },

        idle: {
            // 200ms sense (was 250)
            senseMs:    200,
            // 1800ms lost timeout (was 2000)
            faceLostMs: 1800,
            motionMin:  2.0,
        },

        server: {
            // 900ms resolve interval (was 1200)
            resolveMs:         10000,
            // 2500ms cooldown (was 3000) -- kiosk ready 500ms sooner
            captureCooldownMs: 2500,
        },

        postScan: {
            // 3500ms hold (was 5000) -- 1.5s faster return to ready
            holdMs:   3500,
            toastMs:  6500,
        },

        gating: {
            // 3 stable frames required (was 4)
            stableFramesRequired: 3,
            stableMaxMovePx:      35,   // Lowered from 120: only genuinely still faces trigger scan
            minFaceAreaRatio:     0.05, // Natural kiosk standing distance; dlib reliable encoding needs only ~0.27% area
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
            enabled: false,             // WebSocket fast preview (set to true to enable)
            wsUrl: 'ws://localhost:8080/preview',
            previewIntervalMs: 200,
            confidenceThreshold: 0.70,
        },
    };

    var EP = {
        unlockPin:     appBase + 'Kiosk/UnlockPin',
        resolveOffice: appBase + 'Kiosk/ResolveOffice',
        attend:        appBase + 'Kiosk/Attend',
        submitVisitor: appBase + 'Kiosk/SubmitVisitor',
        deviceState:   appBase + 'Kiosk/GetCurrentMobileDeviceState'
    };

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

        ['kioskVideo', 'overlayCanvas', 'kioskRoot', 'mainPrompt', 'subPrompt'].forEach(function (id) {
            if (!document.getElementById(id))
                errors.push('Missing DOM element #' + id);
        });

        if (errors.length > 0) {
            var root = document.getElementById('kioskRoot') || document.body;
            var div  = document.createElement('div');
            div.style.cssText = 'position:fixed;top:0;left:0;right:0;padding:1rem;background:#c0392b;color:#fff;font-family:monospace;font-size:.85rem;z-index:99999;white-space:pre-wrap';
            div.textContent = 'KIOSK CONFIG ERROR -- scan loop will NOT start:\n\n' + errors.join('\n');
            root.insertAdjacentElement('afterbegin', div);
            return false;
        }
        return true;
    }

    function log() {
        if (CFG.debug) {
            var args = Array.prototype.slice.call(arguments);
            args.unshift('[FaceAttend]');
        }
    }

    window.KioskConfig = {
        appBase:        appBase,
        nextGenEnabled: nextGenEnabled,
        token:          token,
        ui:             ui,
        CFG:            CFG,
        EP:             EP,
        validateConfig: validateConfig,
        log:            log
    };
})();
