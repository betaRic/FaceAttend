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
        antiSpoofLine:      el('antiSpoofLine'),
        antiSpoofBarFill:   el('antiSpoofBarFill'),
        scanEtaLine:       el('scanEtaLine'),

        unlockBackdrop:        el('unlockBackdrop'),
        unlockPin:             el('unlockPin'),
        unlockErr:             el('unlockErr'),
        unlockCancel:          el('unlockCancel'),
        unlockSubmit:          el('unlockSubmit'),
        unlockClose:           el('unlockClose'),
        adminLoadingBackdrop:  el('adminLoadingBackdrop'),

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

    var CFG = {
        debug: false,

        loopMs: 60,

        mp: {
            detectMinConf:     0.30,
            acceptMinScore:    0.60,
            stableFramesMin:   1,
            // FIX: Near-zero wait — fire as soon as face detected, let SERVER decide quality.
            // The old 600ms + strict 35px threshold was the main cause of frames never firing.
            stableNeededMs:    50,
            multiMinAreaRatio: 0.015,
        },

        idle: {
            senseMs:    200,
            faceLostMs: 1800,
            motionMin:  2.0,
        },

        server: {
            resolveMs:         10000,
            // FIX: Reduced from 2500ms so the system is ready sooner after a scan result
            captureCooldownMs: 1500,
        },

        postScan: {
            holdMs:   3500,
            toastMs:  6500,
        },

        gating: {
            stableFramesRequired: 1,
            // FIX: Restored to 120 (original). The 35px value caused constant resets.
            // Server-side antiSpoof/encoding handles quality — client just needs to detect a face.
            stableMaxMovePx:      120,
            // FIX: Very permissive area ratio. Server will reject if face is too small.
            minFaceAreaRatio:     0.03,
            safeEdgeMarginRatio:  0.02,
            centerMin:            0.08,
            centerMax:            0.92,
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

    };

    var EP = {
        unlockPin:         appBase + 'Kiosk/UnlockPin',
        autoAdmin:         appBase + 'Kiosk/AutoAdmin',
        checkAdminAuthed:  appBase + 'Kiosk/CheckAdminAuthed',
        resolveOffice:     appBase + 'Kiosk/ResolveOffice',
        attend:            appBase + 'Kiosk/Attend',
        submitVisitor:     appBase + 'Kiosk/SubmitVisitor'
    };

    function validateConfig() {
        var errors = [];
        if (!CFG.loopMs || CFG.loopMs <= 0)
            errors.push('CFG.loopMs is missing or <= 0');
        if (!CFG.server || !CFG.server.captureCooldownMs)
            errors.push('CFG.server.captureCooldownMs is missing');
        if (!CFG.server || !CFG.server.resolveMs)
            errors.push('CFG.server.resolveMs is missing');
        if (!CFG.mp || CFG.mp.stableNeededMs === undefined)
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
