/**
 * FaceAttend - Unified Enrollment UI Controller
 * Scripts/enrollment-ui.js
 *
 * REQUIRES: Scripts/modules/enrollment-core.js loaded first
 *
 * KEY DESIGN RULE:
 *   Camera does NOT start on page load. #enrollRoot lives inside a hidden
 *   .fa-pane. Camera only starts when window.FaceAttendEnrollment.start()
 *   is called - which Enroll.cshtml triggers from showLive().
 *
 * data-* attributes on #enrollRoot:
 *   data-employee-id   string   employee / visitor ID
 *   data-scan-url      string   ScanFrame endpoint URL
 *   data-enroll-url    string   Enroll endpoint URL
 *   data-redirect-url  string   redirect after success (empty = no redirect)
 *   data-mode          string   "admin" | "mobile" | "visitor"  (default: admin)
 *   data-min-frames    int      min good frames before Save button shows (default: 3)
 *   data-liveness-th   float    per-frame liveness threshold (default: 0.75)
 */
(function () {
    'use strict';

    // ── Guard ──────────────────────────────────────────────────────────────────
    var root = document.getElementById('enrollRoot');
    if (!root) return;

    if (typeof FaceAttend === 'undefined' || typeof FaceAttend.Enrollment === 'undefined') {
        console.error('[enrollment-ui] enrollment-core.js must load before enrollment-ui.js');
        return;
    }

    // ── Config ─────────────────────────────────────────────────────────────────
    var cfg = {
        empId:       (root.getAttribute('data-employee-id') || '').trim(),
        mode:        root.getAttribute('data-mode')          || 'admin',
        scanUrl:     root.getAttribute('data-scan-url')      || '/Biometrics/ScanFrame',
        enrollUrl:   root.getAttribute('data-enroll-url')    || '/Biometrics/Enroll',
        redirectUrl: root.getAttribute('data-redirect-url')  || '',
        minFrames:   parseInt(root.getAttribute('data-min-frames')   || '3',    10),
        livenessTh:  parseFloat(root.getAttribute('data-liveness-th') || '0.75')
    };

    // ── DOM ────────────────────────────────────────────────────────────────────
    function q(id) { return document.getElementById(id); }

    var ui = {
        video:             q('enrollVideo'),
        anglePrompt:       q('anglePrompt'),
        angleIcon:         q('angleIcon'),
        diversityDots:     root.querySelectorAll('.enroll-diversity-dot'),
        progressText:      q('enrollProgressText'),
        progressBar:       q('enrollProgressBar'),
        statusMsg:         q('enrollStatus'),
        livenessBar:       q('enrollLivenessBar'),
        livenessVal:       q('enrollLivenessVal'),
        confirmBtn:        q('enrollConfirmBtn'),
        retakeBtn:         q('enrollRetakeBtn'),
        processingOverlay: q('enrollProcessing'),
        processingStatus:  q('enrollProcessingStatus')
    };

    // Hide legacy start button if still in markup
    var legacyBtn = q('enrollStartBtn');
    if (legacyBtn) legacyBtn.style.display = 'none';

    // ── Internal state ─────────────────────────────────────────────────────────
    var _running       = false;
    var _errShownOnce  = false;

    // ── Enrollment instance ────────────────────────────────────────────────────
    var enrollment = FaceAttend.Enrollment.create({
        empId:             cfg.empId,
        perFrameThreshold: cfg.livenessTh,
        scanUrl:           cfg.scanUrl,
        enrollUrl:         cfg.enrollUrl,
        redirectUrl:       cfg.redirectUrl,
        minGoodFrames:     cfg.minFrames,
        maxKeepFrames:     8,
        enablePreview:     false
    });

    enrollment.elements.cam = ui.video;

    // getEncodings() - returns base64 face encodings from all captured good frames.
    // Called by mobile wizard submitEnrollment() to build the server POST payload.
    // enrollment-core.js stores the server-returned encoding on each goodFrame.
    enrollment.getEncodings = function () {
        var result = [];
        for (var i = 0; i < this.goodFrames.length; i++) {
            var frame = this.goodFrames[i];
            var enc = frame.encoding || frame.enc || null;
            if (enc) result.push(enc);
        }
        return result;
    };

    // ── UI helpers ─────────────────────────────────────────────────────────────
    function dark() {
        return cfg.mode === 'mobile'
            || document.documentElement.getAttribute('data-theme') === 'kiosk';
    }

    function setStatus(text, kind) {
        if (!ui.statusMsg) return;
        ui.statusMsg.textContent = text || '';
        ui.statusMsg.className   = 'enroll-status enroll-status--' + (kind || 'info');
    }

    function setLiveness(pct, kind) {
        if (!ui.livenessBar) return;
        var p = Math.max(0, Math.min(100, pct || 0));
        ui.livenessBar.style.width = p + '%';
        ui.livenessBar.className   = 'enroll-liveness-fill enroll-liveness-fill--' + (kind || 'info');
        if (ui.livenessVal) ui.livenessVal.textContent = p + '%';
    }

    function updateProgress(current, target) {
        var t = target || 8;
        if (ui.progressText) ui.progressText.textContent = current + ' / ' + t + ' frames';
        if (ui.progressBar)  ui.progressBar.style.width  = Math.round((current / t) * 100) + '%';
        if (ui.confirmBtn)
            ui.confirmBtn.classList.toggle('enroll-hidden', current < cfg.minFrames);
    }

    function updateDots() {
        if (!ui.diversityDots || !ui.diversityDots.length) return;
        var captured = {};
        for (var i = 0; i < enrollment.goodFrames.length; i++) {
            var b = enrollment.goodFrames[i].poseBucket;
            if (b) captured[b] = true;
        }
        ui.diversityDots.forEach(function (dot) {
            dot.classList.toggle(
                'enroll-diversity-dot--captured',
                !!captured[dot.getAttribute('data-bucket')]
            );
        });
    }

    function showAngle(next) {
        if (!next) return;
        if (ui.anglePrompt) ui.anglePrompt.textContent = next.prompt || '';
        if (ui.angleIcon)
            ui.angleIcon.className = 'enroll-angle-icon fa-solid ' + (next.icon || 'fa-circle-dot');
    }

    function showProcessing(show, status) {
        if (!ui.processingOverlay) return;
        ui.processingOverlay.classList.toggle('enroll-hidden', !show);
        if (show && ui.processingStatus && status)
            ui.processingStatus.textContent = status;
    }

    function swal(opts) {
        if (typeof Swal !== 'undefined') {
            Swal.fire(Object.assign({
                background: dark() ? '#0f172a' : '#fff',
                color:      dark() ? '#f8fafc' : '#0f172a'
            }, opts));
        }
    }

    // ── Camera start / stop - called by view pane controller ──────────────────
    function startCamera() {
        if (_running) return;
        _running      = true;
        _errShownOnce = false;
        setStatus('Starting camera...', 'info');

        enrollment.startCamera(ui.video)
            .then(function () {
                enrollment.startAutoEnrollment();
                if (typeof enrollment.getNextAnglePrompt === 'function')
                    showAngle(enrollment.getNextAnglePrompt());
                setStatus('Camera ready. Look straight at the camera.', 'info');
            })
            .catch(function (e) {
                _running = false;
                var msg = (e && e.message) || 'Could not access camera.';
                setStatus('Camera error: ' + msg, 'danger');
                if (!_errShownOnce) {
                    _errShownOnce = true;
                    swal({ icon: 'error', title: 'Camera Error', text: msg });
                }
            });
    }

    function stopCamera() {
        if (!_running) return;
        _running = false;
        enrollment.stopCamera();
        setStatus('Camera stopped.', 'info');
        setLiveness(0, 'info');
    }

    // ── Callbacks ──────────────────────────────────────────────────────────────
    enrollment.callbacks.onStatus = setStatus;

    enrollment.callbacks.onLivenessUpdate = setLiveness;

    enrollment.callbacks.onCaptureProgress = function (current, target) {
        updateProgress(current, target);
        updateDots();
        // Forward to mobile wizard callback if present
        if (typeof window.enrollCallbacks === 'object' &&
            window.enrollCallbacks !== null &&
            typeof window.enrollCallbacks.onCaptureProgress === 'function') {
            window.enrollCallbacks.onCaptureProgress(current);
        }
    };

    enrollment.callbacks.onAngleUpdate = function (next) {
        if (next && next.bucket !== 'other') showAngle(next);
    };

    enrollment.callbacks.onEnrollmentComplete = function (count) {
        showProcessing(false);
        swal({
            icon:             'success',
            title:            'Enrollment Complete!',
            text:             count + ' face samples saved.',
            confirmButtonText:'Done'
        });
        // Notify mobile wizard if present
        if (typeof window.enrollCallbacks === 'object' && window.enrollCallbacks.onEnrollmentComplete)
            window.enrollCallbacks.onEnrollmentComplete({ vectorsSaved: count });

        if (cfg.redirectUrl) {
            setTimeout(function () { window.location.href = cfg.redirectUrl; }, 1800);
        }
    };

    enrollment.callbacks.onEnrollmentError = function (result) {
        showProcessing(false);
        var msg = typeof enrollment.describeEnrollError === 'function'
            ? enrollment.describeEnrollError(result)
            : ((result && result.error) || 'Enrollment failed.');
        setStatus(msg, 'danger');
        swal({ icon: 'error', title: 'Enrollment Failed', html: '<div style="font-size:.9rem">' + msg + '</div>' });
        if (typeof window.enrollCallbacks === 'object' && window.enrollCallbacks.onEnrollmentError)
            window.enrollCallbacks.onEnrollmentError({ message: msg });
    };

    // ── Button handlers ────────────────────────────────────────────────────────
    if (ui.confirmBtn) {
        ui.confirmBtn.addEventListener('click', function () {
            if (enrollment.goodFrames.length < cfg.minFrames) return;
            showProcessing(true, 'Processing enrollment...');
            enrollment.performEnrollment();
        });
    }

    if (ui.retakeBtn) {
        ui.retakeBtn.addEventListener('click', function () {
            enrollment.goodFrames = [];
            enrollment.passHist   = [];
            enrollment.enrolled   = false;
            enrollment.enrolling  = false;
            updateProgress(0, cfg.minFrames || 8);
            updateDots();
            if (ui.confirmBtn) ui.confirmBtn.classList.add('enroll-hidden');
            enrollment.startAutoEnrollment();
            if (typeof enrollment.getNextAnglePrompt === 'function')
                showAngle(enrollment.getNextAnglePrompt());
            setStatus('Retaking - follow the angle prompts.', 'info');
        });
    }

    // ── Init - UI state only, camera NOT started ───────────────────────────────
    updateProgress(0, cfg.minFrames || 8);
    setStatus('Waiting for camera...', 'info');
    if (ui.confirmBtn) ui.confirmBtn.classList.add('enroll-hidden');
    showAngle({ bucket: 'center', prompt: 'Look straight at the camera', icon: 'fa-circle-dot' });

    window.addEventListener('beforeunload', stopCamera);

    // ── Public API ─────────────────────────────────────────────────────────────
    Object.defineProperty(enrollment, 'isRunning', { get: function () { return _running; } });
    enrollment.start = startCamera;
    enrollment.stop  = stopCamera;

    window.FaceAttendEnrollment = enrollment;

})();
