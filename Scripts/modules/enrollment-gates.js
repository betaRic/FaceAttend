/**
 * enrollment-gates-fix.js
 * Must be loaded AFTER enrollment-core.js (after the fa-core / enrollment bundles).
 *
 * Problem: enrollment-core.js has multiple client-side quality gates that block
 * frame submission even when the face looks good to the user:
 *   1. Brightness gate (MIN_BRIGHTNESS = 30) — rejects slightly dim environments
 *   2. Sharpness gate (35 desktop / 28 mobile) — rejects normal mobile frames
 *   3. Face area gate (0.08 desktop / 0.06 mobile) — rejects frames at arm-length distance
 *   4. Centering gate (margin 0.20) — rejects faces slightly off-center
 *   5. Too-close gate (MAX_FACE_AREA_RATIO = 0.90) — fine as-is, keep it
 *
 * Fix: Lower all thresholds and let the SERVER decide frame quality via ScanFrame.
 * The server/worker path owns authoritative quality and anti-spoof decisions.
 */
(function () {
    'use strict';

    function patch() {
        if (!window.FaceAttend || !window.FaceAttend.Enrollment) {
            console.warn('[enrollment-gates-fix] FaceAttend.Enrollment not found — retrying');
            setTimeout(patch, 100);
            return;
        }

        var C = window.FaceAttend.Enrollment.CONSTANTS;
        if (!C) {
            console.warn('[enrollment-gates-fix] CONSTANTS not exposed');
            return;
        }

        // ── 1. Lower thresholds so server decides quality ──────────────────────

        // Face area: was 0.08 desktop / 0.06 mobile → now very permissive
        // Server rejects if face is too small via authoritative detection failure
        C.MIN_FACE_AREA_RATIO_DESKTOP = 0.02;
        C.MIN_FACE_AREA_RATIO_MOBILE  = 0.02;

        // Sharpness: was 35 desktop / 28 mobile → now nearly off
        // Server has its own sharpness check; client gate causes silent drop
        C.SHARPNESS_THRESHOLD_DESKTOP = 8;
        C.SHARPNESS_THRESHOLD_MOBILE  = 5;

        // Brightness: was 30 → 0 (disabled)
        // Server-side worker quality checks handle this more reliably than a pixel mean gate.
        C.MIN_BRIGHTNESS = 0;

        // Distance warning: keep it as informational, not blocking
        C.FACE_AREA_WARNING_RATIO = 0.01;

        console.log('[enrollment-gates-fix] Thresholds relaxed — server will decide frame quality');

        // ── 2. Patch _runOneTick to skip the centering gate ───────────────────
        // The centering gate (margin 0.20) is hardcoded and causes silent frame
        // drops when the user's face is slightly off-center on mobile.
        // We replace _runOneTick with an identical copy minus the centering check.

        var OrigEnrollment = null;

        // We can access the prototype via a temporary instance
        try {
            var tempInstance = window.FaceAttend.Enrollment.create({ empId: '__test__' });
            OrigEnrollment = Object.getPrototypeOf(tempInstance);
        } catch (e) {
            console.warn('[enrollment-gates-fix] Could not access prototype:', e);
            return;
        }

        if (!OrigEnrollment || !OrigEnrollment._runOneTick) {
            console.warn('[enrollment-gates-fix] _runOneTick not found on prototype');
            return;
        }

        var _origRunOneTick = OrigEnrollment._runOneTick;

        OrigEnrollment._runOneTick = function () {
            var self = this;

            if (this.enrolled || !this.stream) return Promise.resolve();
            var cam = this.elements.cam;
            if (!cam || !cam.videoWidth) return Promise.resolve();

            var liveArea = this.liveFaceArea || 0;
            var minRatio = C.MIN_FACE_AREA_RATIO_MOBILE; // use mobile threshold (most permissive)

            // Face area gate — still block if truly no face detected by tracker
            if (liveArea > 0 && liveArea < minRatio) {
                if (this.callbacks.onDistanceFeedback) {
                    this.callbacks.onDistanceFeedback({
                        status:    liveArea < minRatio * 0.5 ? 'too_far' : 'borderline',
                        ratio:     liveArea,
                        threshold: minRatio
                    });
                }
                if (this.callbacks.onAntiSpoofUpdate) this.callbacks.onAntiSpoofUpdate(0, 'fail');
                return Promise.resolve();
            }

            // Too-close gate — keep this one (server antiSpoof also fails when face fills frame)
            if (liveArea > C.MAX_FACE_AREA_RATIO) {
                this.handleStatus('Too close — back up.', 'warning');
                if (this.callbacks.onAntiSpoofUpdate) this.callbacks.onAntiSpoofUpdate(0, 'fail');
                return Promise.resolve();
            }

            // ── CENTERING GATE REMOVED ──
            // Original code checked liveTrackingBox with margin=0.20.
            // This blocked frames when face was slightly off-center on mobile.
            // Server-side detection handles off-center faces fine.

            var isMobileDevice = /iPhone|iPad|iPod|Android.*Mobile|Windows Phone/i.test(navigator.userAgent);
            var capturedBlob = null;
            var sharpness = 0;

            if (cam.videoWidth > 0) {
                var sc = this.captureCanvas;
                sc.width = cam.videoWidth; sc.height = cam.videoHeight;
                sc.getContext('2d').drawImage(cam, 0, 0, sc.width, sc.height);

                // Use the patched (lowered) threshold
                var adaptiveThreshold = isMobileDevice
                    ? C.SHARPNESS_THRESHOLD_MOBILE
                    : C.SHARPNESS_THRESHOLD_DESKTOP;

                sharpness = this.calculateSharpness(sc);

                if (sharpness < adaptiveThreshold) {
                    // Only show the message — don't block the frame from going to server
                    // Server will reject truly unusable frames via encoding failure
                    if (this.callbacks.onStatus) {
                        this.callbacks.onStatus(
                            'Image may be blurry (' + Math.round(sharpness) + '). Hold steady.',
                            'warning');
                    }
                    // FALL THROUGH — let server decide
                }

                // ── BRIGHTNESS GATE REMOVED ──
                // Original code blocked if mean brightness < 30.
                // Server antiSpoof handles dim lighting better than a pixel average check.
            }

            return this.captureJpegBlob(C.UPLOAD_QUALITY)
                .then(function (blob) {
                    capturedBlob = blob;
                    return self.postScanFrame(blob);
                })
                .then(function (result) {
                    if (!result) return;
                    result.lastBlob = capturedBlob;
                    result.clientSharpness = sharpness;
                    self.processScanResult(result);
                })
                .catch(function (e) {
                    if (e && e.name === 'AbortError') return;
                    self.handleStatus(isMobileDevice ? 'Retrying...' : 'Scan error, retrying...', 'warning');
                    self.passHist = [];
                });
        };

        console.log('[enrollment-gates-fix] _runOneTick patched — centering and brightness gates removed');
    }

    // Run after DOM + scripts are ready
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', patch);
    } else {
        // Scripts may still be loading — defer slightly
        setTimeout(patch, 50);
    }

})();
