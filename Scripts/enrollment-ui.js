/**
 * FaceAttend - Unified Enrollment UI Controller
 * Scripts/enrollment-ui.js
 *
 * @version 3.1.1  (production-hardened)
 *
 * FIXES IN THIS VERSION vs uploaded enrollment-ui.js:
 *
 *   FIX-ANGLE-01  onReadyToConfirm — enforce all 5 angles before allowing
 *                 submission.  Previous version went straight to the confirm
 *                 Swal regardless of angle coverage.  Now shows a "More angles
 *                 needed" warning with the missing angle list and calls
 *                 _doRetake() so scanning actually resumes (the patch from
 *                 enrollment-ui-onReadyToConfirm-patch.js is now inlined here).
 *
 *   FIX-RETAKE-01 _doRetake() definition moved ABOVE onReadyToConfirm so the
 *                 function is defined before first use.  Previous layout had it
 *                 after the callbacks block, which is fine at runtime (hoisting
 *                 in function scope) but confusing and fragile.
 *
 * REQUIRES: Scripts/modules/enrollment-core.js loaded first
 *
 * data-* attributes on #enrollRoot:
 *   data-employee-id   string   employee / visitor ID
 *   data-scan-url      string   ScanFrame endpoint URL
 *   data-enroll-url    string   Enroll endpoint URL
 *   data-redirect-url  string   redirect after success (empty = no redirect)
 *   data-mode          string   "admin" | "mobile" | "visitor"  (default: admin)
 *   data-min-frames    int      min good frames required (default: 3)
 *   data-liveness-th   float    per-frame liveness threshold (default: 0.75)
 */
(function () {
    'use strict';

    // ── Guard ──────────────────────────────────────────────────────────────────
    // This script only activates on pages that include _EnrollmentComponent.cshtml
    // (which adds <div id="enrollRoot" ...>).
    // Admin Enroll.cshtml and Mobile Enroll.cshtml do NOT have #enrollRoot —
    // they manage their own inline enrollment instances directly.
    var root = document.getElementById('enrollRoot');
    if (!root) return;

    if (typeof FaceAttend === 'undefined' || typeof FaceAttend.Enrollment === 'undefined') {
        console.error('[enrollment-ui] enrollment-core.js must load before enrollment-ui.js');
        return;
    }

    // ── Config from data-* attributes ─────────────────────────────────────────
    var cfg = {
        empId:       (root.getAttribute('data-employee-id') || '').trim(),
        mode:        root.getAttribute('data-mode')          || 'admin',
        scanUrl:     root.getAttribute('data-scan-url')      || '/api/scan/frame',
        enrollUrl:   root.getAttribute('data-enroll-url')    || '/api/enrollment/enroll',
        redirectUrl: root.getAttribute('data-redirect-url')  || '',
        minFrames:   parseInt(root.getAttribute('data-min-frames')    || '3',    10),
        livenessTh:  parseFloat(root.getAttribute('data-liveness-th') || '0.75')
    };

    // ── DOM refs ───────────────────────────────────────────────────────────────
    function q(id) {
        return FaceAttend.Utils ? FaceAttend.Utils.el(id) : document.getElementById(id);
    }

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
        processingOverlay: q('enrollProcessing'),
        processingStatus:  q('enrollProcessingStatus')
    };

    // Hide legacy start button if still in markup
    var legacyBtn = q('enrollStartBtn');
    if (legacyBtn) legacyBtn.style.display = 'none';

    // ── Internal state ─────────────────────────────────────────────────────────
    var _running      = false;
    var _errShownOnce = false;

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

    // ── Camera Component Initialization ───────────────────────────────────────
    (function initCameraComponent() {
        var cameraContainer = root.querySelector('.fa-camera');
        if (!cameraContainer) return;

        var containerId = cameraContainer.dataset.containerId;
        var videoId     = cameraContainer.dataset.videoId;
        var container   = document.getElementById(containerId);
        var video       = document.getElementById(videoId);
        var flash       = document.getElementById(containerId + '-flash');

        if (!container) return;

        function startCameraComponent(options) {
            if (!window.FaceAttend || !window.FaceAttend.Camera) {
                console.error('[Camera Component] FaceAttend.Camera not available');
                return Promise.reject('Camera module not loaded');
            }
            var opts = {};
            try { opts = JSON.parse(container.dataset.cameraOptions || '{}'); } catch(e) {}
            if (options) Object.assign(opts, options);

            return new Promise(function(resolve, reject) {
                window.FaceAttend.Camera.start(video, opts,
                    function(stream) { container.classList.add('fa-camera--active'); resolve(stream); },
                    function(err)    { reject(err); });
            });
        }

        function triggerFlash() {
            if (!flash) return;
            flash.classList.add('fa-camera__flash--active');
            setTimeout(function() { flash.classList.remove('fa-camera__flash--active'); }, 150);
        }

        if (container.dataset.autostart === 'true' && window.FaceAttend && window.FaceAttend.Camera) {
            startCameraComponent();
        }

        container.faceCamera = { start: startCameraComponent, flash: triggerFlash };
    })();

    // ── FaceProgress Component Initialization ─────────────────────────────────
    (function initFaceProgressComponent() {
        var fpContainer = root.querySelector('.face-progress');
        if (!fpContainer) return;

        var textId   = fpContainer.dataset.textId;
        var barId    = fpContainer.dataset.barId;
        var dotsId   = fpContainer.dataset.dotsId;
        var anglesId = fpContainer.dataset.anglesId;
        var target   = parseInt(fpContainer.dataset.target || '5', 10);

        var textEl   = document.getElementById(textId);
        var barEl    = document.getElementById(barId);
        var dotsEl   = document.getElementById(dotsId);
        var anglesEl = document.getElementById(anglesId);

        function updateProgress(current, buckets) {
            var pct = Math.min(100, Math.round((current / target) * 100));
            if (textEl)   textEl.textContent = current + ' / ' + target;
            if (barEl) {
                barEl.style.width = pct + '%';
                barEl.classList.toggle('progress-bar__fill--success', current >= target);
            }
            if (dotsEl) {
                dotsEl.querySelectorAll('.progress-dots__dot').forEach(function(dot, i) {
                    dot.classList.toggle('progress-dots__dot--complete', i < current);
                });
            }
            if (anglesEl && buckets) {
                buckets.forEach(function(bucket) {
                    var item = anglesEl.querySelector('[data-bucket="' + bucket + '"]');
                    if (item) item.classList.add('progress-angles__item--captured');
                });
            }
        }

        fpContainer.faceProgress = { update: updateProgress };
    })();

    // ── UI helpers ─────────────────────────────────────────────────────────────
    function dark() {
        if (FaceAttend.Utils && FaceAttend.Utils.isDark) return FaceAttend.Utils.isDark();
        return cfg.mode === 'mobile' ||
               document.documentElement.getAttribute('data-theme') === 'kiosk';
    }

    function setStatus(text, kind) {
        if (!ui.statusMsg) return;
        ui.statusMsg.textContent = text || '';
        ui.statusMsg.className   = 'enroll-status enroll-status--' + (kind || 'info');
    }

    function setLiveness(pct, kind) {
        var p = Math.max(0, Math.min(100, pct || 0));
        if (ui.livenessBar) {
            ui.livenessBar.style.width = p + '%';
            ui.livenessBar.className   = 'enroll-liveness-fill enroll-liveness-fill--' + (kind || 'info');
        }
        if (ui.livenessVal) ui.livenessVal.textContent = p + '%';
    }

    function updateProgress(current, target) {
        var t = target || 8;
        if (ui.progressText) ui.progressText.textContent = current + ' / ' + t + ' frames';
        if (ui.progressBar)  ui.progressBar.style.width  = Math.round((current / t) * 100) + '%';
    }

    function updateDots() {
        if (!ui.diversityDots || !ui.diversityDots.length) return;
        var captured = {};
        for (var i = 0; i < enrollment.goodFrames.length; i++) {
            var b = enrollment.goodFrames[i].poseBucket;
            if (b) captured[b] = true;
        }
        ui.diversityDots.forEach(function(dot) {
            dot.classList.toggle(
                'enroll-diversity-dot--captured',
                !!captured[dot.getAttribute('data-bucket')]);
        });
    }

    function showAngle(next) {
        if (!next) return;
        if (ui.anglePrompt) ui.anglePrompt.textContent = next.prompt || '';
        if (ui.angleIcon)   ui.angleIcon.className = 'enroll-angle-icon fa-solid ' + (next.icon || 'fa-circle-dot');
    }

    function showProcessing(show, status) {
        if (!ui.processingOverlay) return;
        ui.processingOverlay.classList.toggle('enroll-hidden', !show);
        if (show && ui.processingStatus && status) ui.processingStatus.textContent = status;
    }

    function swalFire(opts) {
        if (FaceAttend.Notify) {
            if (opts.icon === 'error')   { FaceAttend.Notify.errorModal(opts.title, opts.text); return; }
            if (opts.icon === 'success') { FaceAttend.Notify.successModal(opts.title, opts.text); return; }
        }
        if (typeof Swal !== 'undefined') {
            Swal.fire(Object.assign({ background: dark() ? '#0f172a' : '#fff', color: dark() ? '#f8fafc' : '#0f172a' }, opts));
        }
    }

    // ── Camera start / stop ───────────────────────────────────────────────────
    function startCamera() {
        if (_running) return;
        _running = true; _errShownOnce = false;
        setStatus('Starting camera...', 'info');
        enrollment.startCamera(ui.video)
            .then(function() {
                enrollment.startAutoEnrollment();
                if (typeof enrollment.getNextAnglePrompt === 'function')
                    showAngle(enrollment.getNextAnglePrompt());
                setStatus('Camera ready. Look straight at the camera.', 'info');
            })
            .catch(function(e) {
                _running = false;
                setStatus('Camera error: ' + ((e && e.message) || e) + ' — Please allow camera access and reload.', 'danger');
            });
    }

    function stopCamera() {
        if (!_running) return;
        _running = false;
        enrollment.stopCamera();
        setStatus('Camera stopped.', 'info');
        setLiveness(0, 'info');
    }

    // ── Face bounding-box overlay ─────────────────────────────────────────────
    var overlayCanvas = document.getElementById('enrollFaceCanvas');
    var overlayCtx    = overlayCanvas ? overlayCanvas.getContext('2d') : null;
    var lastBoxSmooth = null;
    var boxAnimId     = null;

    function drawFaceOverlay() {
        if (!overlayCanvas || !overlayCtx || !ui.video) return;
        boxAnimId = requestAnimationFrame(drawFaceOverlay);

        var dpr = window.devicePixelRatio || 1;
        var dw  = overlayCanvas.offsetWidth;
        var dh  = overlayCanvas.offsetHeight;
        if (dw < 1 || dh < 1) return;

        if (overlayCanvas.width !== dw * dpr) {
            overlayCanvas.width  = dw * dpr;
            overlayCanvas.height = dh * dpr;
            overlayCtx.scale(dpr, dpr);
        }
        overlayCtx.clearRect(0, 0, dw, dh);

        var faceBox = enrollment.lastFaceBox;
        if (!faceBox || !faceBox.w || !ui.video.videoWidth) { lastBoxSmooth = null; return; }

        var vW = ui.video.videoWidth, vH = ui.video.videoHeight;
        var vAsp = vW / vH, dAsp = dw / dh;
        var scale, ox, oy;
        if (vAsp > dAsp) { scale = dh / vH; ox = (dw - vW * scale) / 2; oy = 0; }
        else              { scale = dw / vW; ox = 0; oy = (dh - vH * scale) / 2; }

        var rawX = faceBox.x * scale + ox;
        var rawW = faceBox.w * scale;
        var dispX = dw - (rawX + rawW); // mirror
        var dispY = faceBox.y * scale + oy;
        var dispW = rawW;
        var dispH = faceBox.h * scale;

        var EMA = 0.30;
        if (!lastBoxSmooth) { lastBoxSmooth = { x: dispX, y: dispY, w: dispW, h: dispH }; }
        else {
            lastBoxSmooth.x += EMA * (dispX - lastBoxSmooth.x);
            lastBoxSmooth.y += EMA * (dispY - lastBoxSmooth.y);
            lastBoxSmooth.w += EMA * (dispW - lastBoxSmooth.w);
            lastBoxSmooth.h += EMA * (dispH - lastBoxSmooth.h);
        }

        var bx = lastBoxSmooth.x, by = lastBoxSmooth.y, bw = lastBoxSmooth.w, bh = lastBoxSmooth.h;
        var done  = enrollment.goodFrames ? enrollment.goodFrames.length : 0;
        var target = cfg.minFrames || 6;
        var color = done >= target ? '#22c55e' : '#3b82f6';
        var glow  = done >= target ? 'rgba(34,197,94,0.5)' : 'rgba(59,130,246,0.5)';
        var cLen  = Math.min(bw, bh) * 0.20;

        overlayCtx.strokeStyle = color;
        overlayCtx.lineWidth   = 2.5;
        overlayCtx.lineCap     = 'round';
        overlayCtx.lineJoin    = 'round';
        overlayCtx.shadowColor = glow;
        overlayCtx.shadowBlur  = 12;

        function bracket(ax, ay, bx2, by2, cx2, cy2) {
            overlayCtx.beginPath(); overlayCtx.moveTo(ax, ay);
            overlayCtx.lineTo(bx2, by2); overlayCtx.lineTo(cx2, cy2); overlayCtx.stroke();
        }
        bracket(bx + cLen, by,      bx,      by,      bx,      by + cLen);
        bracket(bx+bw-cLen, by,     bx+bw,   by,      bx+bw,   by + cLen);
        bracket(bx + cLen, by+bh,   bx,      by+bh,   bx,      by+bh-cLen);
        bracket(bx+bw-cLen, by+bh,  bx+bw,   by+bh,   bx+bw,   by+bh-cLen);

        overlayCtx.shadowBlur  = 0;
        overlayCtx.globalAlpha = 0.05;
        overlayCtx.fillStyle   = color;
        overlayCtx.fillRect(bx, by, bw, bh);
        overlayCtx.globalAlpha = 1;
    }

    function stopFaceOverlay() {
        if (boxAnimId)  { cancelAnimationFrame(boxAnimId); boxAnimId = null; }
        if (overlayCtx && overlayCanvas) overlayCtx.clearRect(0, 0, overlayCanvas.width, overlayCanvas.height);
        lastBoxSmooth = null;
    }

    if (overlayCanvas) drawFaceOverlay();

    // ── Internal retake ────────────────────────────────────────────────────────
    // Must be defined BEFORE onReadyToConfirm uses it.
    function _doRetake() {
        enrollment.startAutoEnrollment();
        updateProgress(0, cfg.minFrames || 8);
        updateDots();
        if (typeof enrollment.getNextAnglePrompt === 'function')
            showAngle(enrollment.getNextAnglePrompt());
        setStatus('Retaking — follow the angle prompts.', 'info');
    }

    // ── Callbacks ──────────────────────────────────────────────────────────────
    enrollment.callbacks.onStatus        = setStatus;
    enrollment.callbacks.onLivenessUpdate = setLiveness;

    enrollment.callbacks.onCaptureProgress = function(current, target) {
        updateProgress(current, target);
        updateDots();
        if (typeof window.enrollCallbacks === 'object' &&
            window.enrollCallbacks !== null &&
            typeof window.enrollCallbacks.onCaptureProgress === 'function') {
            window.enrollCallbacks.onCaptureProgress(current);
        }
    };

    enrollment.callbacks.onAngleUpdate = function(next) {
        if (next && next.bucket !== 'other') showAngle(next);
    };

    enrollment.callbacks.onDistanceFeedback = function(feedback) {
        var statusEl = document.getElementById('cameraStatusText');
        if (!statusEl) return;
        var mob = FaceAttend.Utils && FaceAttend.Utils.isMobile ? FaceAttend.Utils.isMobile() : false;
        if (feedback.status === 'too_far')  statusEl.textContent = mob ? 'Move a bit closer 📱' : 'Move closer — face too small';
        if (feedback.status === 'warning')  statusEl.textContent = 'Good, but can be closer 👍';
        if (feedback.status === 'good')     statusEl.textContent = 'Perfect distance! Hold still ✓';
    };

    enrollment.callbacks.onQualityFeedback = function(feedback) {
        if (feedback.type === 'blur') {
            var s = document.getElementById('cameraStatusText');
            if (s) s.textContent = 'Image blurry — hold steadier or add light';
        }
    };

    // ── FIX-ANGLE-01: onReadyToConfirm with mandatory allAngles enforcement ────
    //
    // BEFORE: went straight to confirm Swal, allowing all-center enrollment.
    // AFTER:  blocks submission until all 5 angles are captured, shows which
    //         are missing, calls _doRetake() so scanning resumes immediately.
    //
    // Two code paths:
    //   1. External interceptor  mobile wizard overrides this entirely.
    //   2. allAngles gate  if not all 5 captured, show warning + restart.
    //   3. Normal confirm  all 5 captured, show thumbnails + confirm Swal.
    enrollment.callbacks.onReadyToConfirm = function(data) {
        setStatus('Capture complete! Reviewing...', 'success');

        // ── Path 1: mobile wizard intercept ──────────────────────────────────
        if (typeof window.enrollCallbacks === 'object' &&
            window.enrollCallbacks !== null &&
            typeof window.enrollCallbacks.onReadyToConfirm === 'function') {
            window.enrollCallbacks.onReadyToConfirm(data);
            return;
        }

        // ── Path 2: FIX-ANGLE-01 — enforce all 5 angles ─────────────────────
        if (!data.allAngles) {
            var REQUIRED = ['center', 'left', 'right', 'up', 'down'];
            var capturedSet = {};
            (data.frames || []).forEach(function(f) { if (f.poseBucket) capturedSet[f.poseBucket] = true; });
            var missing = REQUIRED.filter(function(a) { return !capturedSet[a]; });

            if (typeof Swal !== 'undefined') {
                Swal.fire({
                    icon:              'warning',
                    title:             'More angles needed',
                    html:              'Please capture <b>all 5 angles</b> for robust identification.' +
                                       '<br><br>Missing: <b>' + missing.join(', ') + '</b>' +
                                       '<br>Captured so far: ' + data.angleCount + ' / 5',
                    confirmButtonText: '<i class="fa-solid fa-camera me-1"></i>Continue Capturing',
                    confirmButtonColor:'#3b82f6',
                    background:        dark() ? '#0f172a' : '#fff',
                    color:             dark() ? '#f8fafc' : '#0f172a',
                    allowOutsideClick: false,
                    allowEscapeKey:    false
                }).then(function() {
                    // FIX-RETAKE-01: _doRetake() defined above — resumes scanning
                    _doRetake();
                });
            } else {
                alert('Missing angles: ' + missing.join(', ') + '. Please continue capturing.');
                _doRetake();
            }
            return; // block submission
        }

        // ── Path 3: all 5 angles present — show confirm Swal ─────────────────
        var topFrames = data.frames.slice(0, 3);
        var thumbPromises = topFrames.map(function(frame) {
            return new Promise(function(resolve) {
                if (!frame || !frame.blob) { resolve(null); return; }
                var reader = new FileReader();
                reader.onload  = function(e) { resolve(e.target.result); };
                reader.onerror = function()  { resolve(null); };
                reader.readAsDataURL(frame.blob);
            });
        });

        Promise.all(thumbPromises).then(function(dataUrls) {
            var thumbHtml = '';
            dataUrls.forEach(function(url) {
                if (url) thumbHtml +=
                    '<img src="' + url + '" style="width:80px;height:80px;object-fit:cover;' +
                    'border-radius:8px;border:2px solid rgba(255,255,255,0.15);margin:4px;" />';
            });

            var summaryHtml =
                '<div style="display:flex;justify-content:center;gap:8px;margin-bottom:14px;flex-wrap:wrap;">' + thumbHtml + '</div>' +
                '<div style="background:rgba(255,255,255,0.05);border-radius:10px;padding:12px 16px;text-align:left;font-size:0.875rem;line-height:2;">' +
                    '<div><i class="fa-solid fa-layer-group" style="margin-right:8px;color:#3b82f6;"></i>' +
                        '<strong>' + data.frameCount + '</strong> frames captured</div>' +
                    '<div><span style="color:#22c55e;"><i class="fa-solid fa-circle-check" style="margin-right:6px;"></i>' +
                        'All 5 angles captured</span></div>' +
                    '<div><i class="fa-solid fa-shield-heart" style="margin-right:8px;color:#22c55e;"></i>' +
                        'Best liveness: <strong>' + data.bestLiveness + '%</strong></div>' +
                '</div>';

            if (typeof Swal !== 'undefined') {
                Swal.fire({
                    title:             'Ready to Enroll',
                    html:              summaryHtml,
                    icon:              'success',
                    showCancelButton:  true,
                    confirmButtonText: '<i class="fa-solid fa-check" style="margin-right:6px;"></i>Confirm Enrollment',
                    cancelButtonText:  '<i class="fa-solid fa-rotate-left" style="margin-right:6px;"></i>Retake',
                    confirmButtonColor:'#22c55e',
                    cancelButtonColor: '#475569',
                    background:        dark() ? '#0f172a' : '#fff',
                    color:             dark() ? '#f8fafc' : '#0f172a',
                    allowOutsideClick: false,
                    allowEscapeKey:    false
                }).then(function(result) {
                    if (result.isConfirmed) {
                        showProcessing(true, 'Processing enrollment...');
                        enrollment.performEnrollment();
                    } else {
                        _doRetake();
                    }
                });
            } else {
                var ok = window.confirm(
                    'Ready to Enroll!\n\n' + data.frameCount + ' frames, all 5 angles.\n' +
                    'Best liveness: ' + data.bestLiveness + '%\n\nConfirm?');
                if (ok) { showProcessing(true, 'Processing enrollment...'); enrollment.performEnrollment(); }
                else      _doRetake();
            }
        });
    };

    enrollment.callbacks.onEnrollmentComplete = function(count) {
        showProcessing(false);
        swalFire({ icon: 'success', title: 'Enrollment Complete!', text: count + ' face samples saved.' });
        if (typeof window.enrollCallbacks === 'object' && window.enrollCallbacks.onEnrollmentComplete)
            window.enrollCallbacks.onEnrollmentComplete({ vectorsSaved: count });
        if (cfg.redirectUrl) setTimeout(function() { window.location.href = cfg.redirectUrl; }, 1800);
    };

    enrollment.callbacks.onEnrollmentError = function(result) {
        showProcessing(false);
        var msg = typeof enrollment.describeEnrollError === 'function'
            ? enrollment.describeEnrollError(result)
            : ((result && result.error) || 'Enrollment failed.');
        setStatus(msg, 'danger');
        if (typeof window.enrollCallbacks === 'object' && window.enrollCallbacks.onEnrollmentError)
            window.enrollCallbacks.onEnrollmentError({ message: msg });
    };

    // ── Init ──────────────────────────────────────────────────────────────────
    updateProgress(0, cfg.minFrames || 8);
    setStatus('Waiting for camera...', 'info');
    showAngle({ bucket: 'center', prompt: 'Look straight at the camera', icon: 'fa-circle-dot' });
    window.addEventListener('beforeunload', stopCamera);

    // ── Public API ────────────────────────────────────────────────────────────
    Object.defineProperty(enrollment, 'isRunning', { get: function() { return _running; } });
    enrollment.start = startCamera;
    enrollment.stop  = stopCamera;

    enrollment.getEncodings = function() {
        var result = [];
        for (var i = 0; i < this.goodFrames.length; i++) {
            var enc = this.goodFrames[i].encoding || this.goodFrames[i].enc || null;
            if (enc) result.push(enc);
        }
        return result;
    };

    window.FaceAttendEnrollment = enrollment;

})();
