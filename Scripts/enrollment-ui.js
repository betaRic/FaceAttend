/**
 * FaceAttend — Unified Enrollment UI Controller (Phase 2)
 * enrollment-ui.js
 *
 * Replaces: admin-enroll.js, inline enrollment script in MobileRegistration/Enroll.cshtml
 *
 * Depends on: Scripts/modules/enrollment-core.js (must load first)
 *
 * Usage: Include in any page that has an #enrollRoot element with data attributes.
 * All configuration is read from data-* attributes — no hardcoded URLs or IDs.
 *
 * Required data attributes on #enrollRoot:
 *   data-employee-id    — employee ID string
 *   data-scan-url       — URL for ScanFrame endpoint
 *   data-enroll-url     — URL for Enroll endpoint
 *   data-redirect-url   — URL to redirect after success
 *
 * Optional data attributes:
 *   data-mode           — "admin" | "mobile" | "visitor" (default: "admin")
 *   data-min-frames     — minimum frames before manual submit (default: 3)
 *   data-liveness-th    — per-frame liveness threshold (default: 0.75)
 */
(function() {
    'use strict';

    // ── Bootstrap ──────────────────────────────────────────────────────────────

    var root = document.getElementById('enrollRoot');
    if (!root) return;
    if (typeof FaceAttend === 'undefined' || typeof FaceAttend.Enrollment === 'undefined') {
        console.error('[enrollment-ui] enrollment-core.js must be loaded first.');
        return;
    }

    var cfg = {
        empId:       (root.getAttribute('data-employee-id') || '').trim(),
        mode:        root.getAttribute('data-mode')         || 'admin',
        scanUrl:     root.getAttribute('data-scan-url')     || '/Biometrics/ScanFrame',
        enrollUrl:   root.getAttribute('data-enroll-url')   || '/Biometrics/Enroll',
        redirectUrl: root.getAttribute('data-redirect-url') || '/',
        minFrames:   parseInt(root.getAttribute('data-min-frames') || '3', 10),
        livenessTh:  parseFloat(root.getAttribute('data-liveness-th') || '0.75')
    };

    // ── DOM References ─────────────────────────────────────────────────────────

    function q(id) { return document.getElementById(id); }
    function qs(sel) { return root.querySelector(sel); }

    var ui = {
        video:        q('enrollVideo'),
        anglePrompt:  q('anglePrompt'),
        angleIcon:    q('angleIcon'),
        diversityDots: root.querySelectorAll('.enroll-diversity-dot'),
        progressText: q('enrollProgressText'),
        progressBar:  q('enrollProgressBar'),
        statusMsg:    q('enrollStatus'),
        livenessBar:  q('enrollLivenessBar'),
        livenessVal:  q('enrollLivenessVal'),
        startBtn:     q('enrollStartBtn'),
        confirmBtn:   q('enrollConfirmBtn'),
        retakeBtn:    q('enrollRetakeBtn'),
        processingOverlay: q('enrollProcessing'),
        processingStatus:  q('enrollProcessingStatus')
    };

    // ── Enrollment Instance ────────────────────────────────────────────────────

    var enrollment = FaceAttend.Enrollment.create({
        empId:            cfg.empId,
        perFrameThreshold: cfg.livenessTh,
        scanUrl:          cfg.scanUrl,
        enrollUrl:        cfg.enrollUrl,
        redirectUrl:      cfg.redirectUrl,
        minGoodFrames:    cfg.minFrames,
        maxKeepFrames:    8,
        enablePreview:    false
    });

    enrollment.elements.cam = ui.video;

    // ── Callbacks ──────────────────────────────────────────────────────────────

    enrollment.callbacks.onStatus = function(text, kind) {
        setStatus(text, kind);
    };

    enrollment.callbacks.onLivenessUpdate = function(pct, kind) {
        setLiveness(pct, kind);
    };

    enrollment.callbacks.onCaptureProgress = function(current, target) {
        updateProgress(current, target);
        updateDiversityDots();
    };

    // Phase 2: Angle guidance callback
    enrollment.callbacks.onAngleUpdate = function(next) {
        if (next && next.bucket !== 'other') {
            showAngleGuidance(next);
        }
    };

    enrollment.callbacks.onEnrollmentComplete = function(count) {
        showProcessing(false);
        Swal.fire({
            icon: 'success',
            title: 'Enrollment Complete!',
            text: count + ' face samples saved successfully.',
            confirmButtonText: 'Done',
            background: isDark() ? '#0f172a' : '#fff',
            color:      isDark() ? '#f8fafc' : '#0f172a'
        }).then(function() {
            window.location.href = cfg.redirectUrl;
        });
    };

    enrollment.callbacks.onEnrollmentError = function(result) {
        showProcessing(false);
        var msg = enrollment.describeEnrollError(result);
        Swal.fire({
            icon:  'error',
            title: 'Enrollment Failed',
            html:  '<div style="font-size:.95rem">' + msg + '</div>',
            background: isDark() ? '#0f172a' : '#fff',
            color:      isDark() ? '#f8fafc' : '#0f172a'
        });
    };

    // ── UI Helpers ─────────────────────────────────────────────────────────────

    function isDark() {
        return document.documentElement.getAttribute('data-theme') === 'kiosk'
            || cfg.mode === 'mobile';
    }

    function setStatus(text, kind) {
        if (!ui.statusMsg) return;
        ui.statusMsg.textContent = text;
        ui.statusMsg.className = 'enroll-status enroll-status--' + (kind || 'info');
    }

    function setLiveness(pct, kind) {
        if (!ui.livenessBar) return;
        var safePct = Math.max(0, Math.min(100, pct || 0));
        ui.livenessBar.style.width = safePct + '%';
        ui.livenessBar.className = 'enroll-liveness-fill enroll-liveness-fill--' + (kind || 'info');
        if (ui.livenessVal) ui.livenessVal.textContent = safePct + '%';
    }

    function updateProgress(current, target) {
        if (ui.progressText) {
            ui.progressText.textContent = current + ' / ' + target + ' frames';
        }
        if (ui.progressBar) {
            ui.progressBar.style.width = Math.round((current / target) * 100) + '%';
        }
        // Show/hide confirm button
        if (ui.confirmBtn) {
            ui.confirmBtn.classList.toggle(
                'enroll-hidden', current < (cfg.minFrames || 3));
        }
    }

    function updateDiversityDots() {
        if (!ui.diversityDots || !ui.diversityDots.length) return;
        var captured = {};
        for (var i = 0; i < enrollment.goodFrames.length; i++) {
            var b = enrollment.goodFrames[i].poseBucket;
            if (b) captured[b] = true;
        }
        ui.diversityDots.forEach(function(dot) {
            var bucket = dot.getAttribute('data-bucket');
            dot.classList.toggle('enroll-diversity-dot--captured', !!captured[bucket]);
        });
    }

    function showAngleGuidance(next) {
        if (ui.anglePrompt) ui.anglePrompt.textContent = next.prompt;
        if (ui.angleIcon) {
            ui.angleIcon.className = 'enroll-angle-icon fa-solid ' + (next.icon || 'fa-circle-dot');
        }
    }

    function showProcessing(show, statusText) {
        if (!ui.processingOverlay) return;
        ui.processingOverlay.classList.toggle('enroll-hidden', !show);
        if (show && ui.processingStatus && statusText) {
            ui.processingStatus.textContent = statusText;
        }
    }

    // ── Event Handlers ─────────────────────────────────────────────────────────

    if (ui.startBtn) {
        ui.startBtn.addEventListener('click', function() {
            ui.startBtn.disabled = true;
            setStatus('Starting camera...', 'info');

            enrollment.startCamera(ui.video)
                .then(function() {
                    ui.startBtn.classList.add('enroll-hidden');
                    enrollment.startAutoEnrollment();
                    showAngleGuidance(enrollment.getNextAnglePrompt());
                    setStatus('Camera ready. Follow the angle prompts.', 'info');
                })
                .catch(function(e) {
                    ui.startBtn.disabled = false;
                    Swal.fire('Camera Error', e.message || 'Could not access camera.', 'error');
                });
        });
    }

    if (ui.confirmBtn) {
        ui.confirmBtn.addEventListener('click', function() {
            if (enrollment.goodFrames.length < cfg.minFrames) return;
            showProcessing(true, 'Processing enrollment...');
            enrollment.performEnrollment();
        });
    }

    if (ui.retakeBtn) {
        ui.retakeBtn.addEventListener('click', function() {
            enrollment.goodFrames = [];
            enrollment.passHist   = [];
            enrollment.enrolled   = false;
            enrollment.enrolling  = false;
            updateProgress(0, 8);
            updateDiversityDots();
            enrollment.startAutoEnrollment();
            showAngleGuidance(enrollment.getNextAnglePrompt());
            if (ui.confirmBtn) ui.confirmBtn.classList.add('enroll-hidden');
            setStatus('Retaking. Follow the angle prompts.', 'info');
        });
    }

    // ── Init ───────────────────────────────────────────────────────────────────

    (function init() {
        updateProgress(0, 8);
        setStatus('Click Start to begin enrollment.', 'info');
        if (ui.confirmBtn) ui.confirmBtn.classList.add('enroll-hidden');
        showAngleGuidance({ bucket: 'center', prompt: 'Start and look straight ahead', icon: 'fa-circle-dot' });
    })();

    // Cleanup on page unload
    window.addEventListener('beforeunload', function() {
        enrollment.stopCamera();
    });

    // Phase 2: Expose instance for mobile wizard interop
    window.FaceAttendEnrollment = enrollment;

})();
