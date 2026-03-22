/**
 * FaceAttend — Enrollment UI Controller
 * Scripts/enrollment-ui.js
 *
 * @version 4.0.0
 *
 * CHANGES vs 3.1.1:
 *
 *   TRACK-01  drawFaceOverlay now prefers enrollment.liveTrackingBox
 *             (written at 60fps by enrollment-tracking.js) over the
 *             server-response lastFaceBox (~3fps). Tracking is now as
 *             fluid as the kiosk. Falls back gracefully when the tracking
 *             module is absent.
 *
 *   TRACK-02  object-fit:cover scale math fixed. Original used an aspect-
 *             ratio branch that reproduced object-fit:contain, not cover.
 *             Now uses Math.max(dw/vW, dh/vH) — identical to kiosk.
 *
 *   TRACK-03  Box color now reflects state: blue (idle/scanning), amber
 *             (frames accumulating), green (all angles captured). Same
 *             palette as the kiosk liveness indicator.
 *
 *   TRACK-04  Animated scan line added while enrollment.busy === true,
 *             matching the kiosk capture feel.
 *
 *   OPT-01    Removed unused _errShownOnce flag.
 *   OPT-02    DPR canvas sync uses Math.round to avoid fractional pixels.
 *   OPT-03    bracket() moved outside drawFaceOverlay so it is not
 *             recreated on every animation frame.
 *   OPT-04    stopFaceOverlay now also resets scanLinePos.
 *   OPT-05    Camera component and FaceProgress component inits unchanged
 *             but dead variables removed.
 *
 * REQUIRES: Scripts/modules/enrollment-core.js loaded first
 * ENHANCES: Scripts/enrollment-tracking.js (optional, enables 60fps tracking)
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
        scanUrl:     root.getAttribute('data-scan-url')      || '/api/scan/frame',
        enrollUrl:   root.getAttribute('data-enroll-url')    || '/api/enrollment/enroll',
        redirectUrl: root.getAttribute('data-redirect-url')  || '',
        minFrames:   parseInt(root.getAttribute('data-min-frames')    || '3',  10),
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

    // Hide legacy start button if present
    var legacyBtn = q('enrollStartBtn');
    if (legacyBtn) legacyBtn.style.display = 'none';

    // ── Internal state ─────────────────────────────────────────────────────────
    var _running = false;

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

    // ── Camera component init ─────────────────────────────────────────────────
    (function initCameraComponent() {
        var cameraContainer = root.querySelector('.fa-camera');
        if (!cameraContainer) return;

        var containerId = cameraContainer.dataset.containerId;
        var container   = document.getElementById(containerId);
        var video       = document.getElementById(cameraContainer.dataset.videoId);
        var flash       = document.getElementById(containerId + '-flash');
        if (!container) return;

        function startCameraComponent(options) {
            if (!window.FaceAttend || !window.FaceAttend.Camera) {
                console.error('[Camera Component] FaceAttend.Camera not available');
                return Promise.reject('Camera module not loaded');
            }
            var opts = {};
            try { opts = JSON.parse(container.dataset.cameraOptions || '{}'); } catch (e) {}
            if (options) Object.assign(opts, options);
            return new Promise(function (resolve, reject) {
                window.FaceAttend.Camera.start(video, opts,
                    function (stream) { container.classList.add('fa-camera--active'); resolve(stream); },
                    function (err)    { reject(err); });
            });
        }

        function triggerFlash() {
            if (!flash) return;
            flash.classList.add('fa-camera__flash--active');
            setTimeout(function () { flash.classList.remove('fa-camera__flash--active'); }, 150);
        }

        if (container.dataset.autostart === 'true' && window.FaceAttend && window.FaceAttend.Camera) {
            startCameraComponent();
        }

        container.faceCamera = { start: startCameraComponent, flash: triggerFlash };
    })();

    // ── FaceProgress component init ───────────────────────────────────────────
    (function initFaceProgressComponent() {
        var fp = root.querySelector('.face-progress');
        if (!fp) return;

        var textEl   = document.getElementById(fp.dataset.textId);
        var barEl    = document.getElementById(fp.dataset.barId);
        var dotsEl   = document.getElementById(fp.dataset.dotsId);
        var anglesEl = document.getElementById(fp.dataset.anglesId);
        var target   = parseInt(fp.dataset.target || '5', 10);

        fp.faceProgress = {
            update: function (current, buckets) {
                var pct = Math.min(100, Math.round((current / target) * 100));
                if (textEl) textEl.textContent = current + ' / ' + target;
                if (barEl) {
                    barEl.style.width = pct + '%';
                    barEl.classList.toggle('progress-bar__fill--success', current >= target);
                }
                if (dotsEl) {
                    dotsEl.querySelectorAll('.progress-dots__dot').forEach(function (dot, i) {
                        dot.classList.toggle('progress-dots__dot--complete', i < current);
                    });
                }
                if (anglesEl && buckets) {
                    buckets.forEach(function (bucket) {
                        var item = anglesEl.querySelector('[data-bucket="' + bucket + '"]');
                        if (item) item.classList.add('progress-angles__item--captured');
                    });
                }
            }
        };
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
        ui.diversityDots.forEach(function (dot) {
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
            Swal.fire(Object.assign({
                background: dark() ? '#0f172a' : '#fff',
                color:      dark() ? '#f8fafc' : '#0f172a'
            }, opts));
        }
    }

    // ── Camera start / stop ───────────────────────────────────────────────────
    function startCamera() {
        if (_running) return;
        _running = true;
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
    //
    // TRACK-01: Prefers enrollment.liveTrackingBox (60fps, from
    //   enrollment-tracking.js) over enrollment.lastFaceBox (3fps from
    //   server). Falls back transparently when the module is absent.
    //
    // TRACK-02: Uses Math.max for cover-scale, identical to kiosk.
    //   Original code used an aspect-ratio branch that reproduced
    //   object-fit:contain (letter-boxed), not cover (cropped to fill).

    var overlayCanvas = document.getElementById('enrollFaceCanvas');
    var overlayCtx    = overlayCanvas ? overlayCanvas.getContext('2d') : null;
    var lastBoxSmooth = null;
    var boxAnimId     = null;
    var scanLinePos   = 0;  // 0..1, animates while enrollment.busy

    // OPT-03: bracket() defined once, not recreated per frame
    function bracket(ax, ay, mx, my, ex, ey) {
        overlayCtx.beginPath();
        overlayCtx.moveTo(ax, ay);
        overlayCtx.lineTo(mx, my);
        overlayCtx.lineTo(ex, ey);
        overlayCtx.stroke();
    }

    function drawFaceOverlay() {
        if (!overlayCanvas || !overlayCtx || !ui.video) return;
        boxAnimId = requestAnimationFrame(drawFaceOverlay);

        // ── 1. Sync canvas to CSS size + DPR ──────────────────────────────────
        var dpr = window.devicePixelRatio || 1;
        var dw  = overlayCanvas.offsetWidth;
        var dh  = overlayCanvas.offsetHeight;
        if (dw < 1 || dh < 1) return;

        // OPT-02: Math.round prevents fractional physical pixels
        var pw = Math.round(dw * dpr);
        var ph = Math.round(dh * dpr);
        if (overlayCanvas.width !== pw || overlayCanvas.height !== ph) {
            overlayCanvas.width  = pw;
            overlayCanvas.height = ph;
            overlayCtx.scale(dpr, dpr);
        }
        overlayCtx.clearRect(0, 0, dw, dh);

        // ── 2. Choose box source ───────────────────────────────────────────────
        //
        // Path A: liveTrackingBox — set by enrollment-tracking.js at 60fps.
        //   Already in CSS-pixel space with EMA(α=0.35) applied.
        //   Use a very light extra blend (α=0.50) to remove sub-pixel noise.
        //
        // Path B: lastFaceBox — set by server response (~3fps).
        //   In server image coords. Needs cover-scale + mirror mapping,
        //   then EMA(α=0.30) to smooth the slow update rate.

        var liveBox = enrollment.liveTrackingBox || null;

        if (liveBox) {
            // Path A — live 60fps tracking
            var EMA_A = 0.50;
            if (!lastBoxSmooth) {
                lastBoxSmooth = { x: liveBox.x, y: liveBox.y, w: liveBox.w, h: liveBox.h };
            } else {
                lastBoxSmooth.x += EMA_A * (liveBox.x - lastBoxSmooth.x);
                lastBoxSmooth.y += EMA_A * (liveBox.y - lastBoxSmooth.y);
                lastBoxSmooth.w += EMA_A * (liveBox.w - lastBoxSmooth.w);
                lastBoxSmooth.h += EMA_A * (liveBox.h - lastBoxSmooth.h);
            }
        } else {
            // Path B — server box fallback
            var fb = enrollment.lastFaceBox;
            if (!fb || !fb.w || !ui.video.videoWidth) { lastBoxSmooth = null; return; }

            var vW = ui.video.videoWidth, vH = ui.video.videoHeight;
            // TRACK-02: object-fit:cover — Math.max fills canvas, may crop video
            var scale = Math.max(dw / vW, dh / vH);
            var ox    = (dw - vW * scale) / 2;
            var oy    = (dh - vH * scale) / 2;

            var rawX  = fb.x * scale + ox;
            var rawW  = fb.w * scale;
            var dispX = dw - (rawX + rawW); // mirror X to match scaleX(-1) on video
            var dispY = fb.y * scale + oy;

            var EMA_B = 0.30;
            if (!lastBoxSmooth) {
                lastBoxSmooth = { x: dispX, y: dispY, w: rawW, h: fb.h * scale };
            } else {
                lastBoxSmooth.x += EMA_B * (dispX         - lastBoxSmooth.x);
                lastBoxSmooth.y += EMA_B * (dispY         - lastBoxSmooth.y);
                lastBoxSmooth.w += EMA_B * (rawW          - lastBoxSmooth.w);
                lastBoxSmooth.h += EMA_B * (fb.h * scale  - lastBoxSmooth.h);
            }
        }

        var bx = lastBoxSmooth.x, by = lastBoxSmooth.y;
        var bw = lastBoxSmooth.w, bh = lastBoxSmooth.h;

        // ── 3. State-based color (TRACK-03) ───────────────────────────────────
        //
        //   green  — all 4 angles captured (ready to confirm)
        //   amber  — frames accumulating
        //   blue   — idle / scanning
        var done      = enrollment.goodFrames ? enrollment.goodFrames.length : 0;
        var isBusy    = !!enrollment.busy;
        var ANGLES    = ['center', 'left', 'right', 'down'];
        var captured  = {};
        if (enrollment.goodFrames) {
            enrollment.goodFrames.forEach(function (f) {
                if (f.poseBucket && f.poseBucket !== 'other') captured[f.poseBucket] = true;
            });
        }
        var allAngles = ANGLES.every(function (a) { return !!captured[a]; });

        var mainColor, glowColor;
        if (allAngles && done >= (cfg.minFrames || 6)) {
            mainColor = '#22c55e'; glowColor = 'rgba(34,197,94,0.55)';
        } else if (done > 0) {
            mainColor = '#f59e0b'; glowColor = 'rgba(245,158,11,0.50)';
        } else {
            mainColor = '#4f9cf9'; glowColor = 'rgba(79,156,249,0.45)';
        }

        // ── 4. Scan line while capturing (TRACK-04) ───────────────────────────
        if (isBusy) {
            scanLinePos = (scanLinePos + 0.016) % 1.0;
            var scanY = by + bh * scanLinePos;
            var grad  = overlayCtx.createLinearGradient(bx, scanY, bx + bw, scanY);
            grad.addColorStop(0,    'rgba(0,0,0,0)');
            grad.addColorStop(0.25, mainColor);
            grad.addColorStop(0.75, mainColor);
            grad.addColorStop(1,    'rgba(0,0,0,0)');
            overlayCtx.save();
            overlayCtx.globalAlpha = 0.55;
            overlayCtx.strokeStyle = grad;
            overlayCtx.lineWidth   = 1.5;
            overlayCtx.shadowColor = mainColor;
            overlayCtx.shadowBlur  = 8;
            overlayCtx.beginPath();
            overlayCtx.moveTo(bx, scanY);
            overlayCtx.lineTo(bx + bw, scanY);
            overlayCtx.stroke();
            overlayCtx.restore();
        } else {
            scanLinePos = 0;
        }

        // ── 5. Corner brackets ────────────────────────────────────────────────
        var cLen = Math.min(bw, bh) * 0.20;
        var lw   = isBusy ? 3.5 : 2.5;

        overlayCtx.save();
        overlayCtx.strokeStyle = mainColor;
        overlayCtx.lineWidth   = lw;
        overlayCtx.lineCap     = 'round';
        overlayCtx.lineJoin    = 'round';
        overlayCtx.shadowColor = glowColor;
        overlayCtx.shadowBlur  = 14;

        bracket(bx + cLen,      by,      bx,      by,      bx,      by + cLen);
        bracket(bx + bw - cLen, by,      bx + bw, by,      bx + bw, by + cLen);
        bracket(bx + cLen,      by + bh, bx,      by + bh, bx,      by + bh - cLen);
        bracket(bx + bw - cLen, by + bh, bx + bw, by + bh, bx + bw, by + bh - cLen);

        overlayCtx.restore();

        // ── 6. Box outline + subtle fill ──────────────────────────────────────
        overlayCtx.save();
        overlayCtx.strokeStyle = mainColor;
        overlayCtx.lineWidth   = isBusy ? 2.0 : 1.5;
        overlayCtx.globalAlpha = 0.75;
        overlayCtx.strokeRect(bx, by, bw, bh);
        overlayCtx.globalAlpha = 0.04;
        overlayCtx.fillStyle   = mainColor;
        overlayCtx.fillRect(bx, by, bw, bh);
        overlayCtx.restore();
    }

    function stopFaceOverlay() {
        if (boxAnimId) { cancelAnimationFrame(boxAnimId); boxAnimId = null; }
        if (overlayCtx && overlayCanvas) {
            overlayCtx.clearRect(0, 0, overlayCanvas.width, overlayCanvas.height);
        }
        lastBoxSmooth = null;
        scanLinePos   = 0;  // OPT-04
    }

    if (overlayCanvas) drawFaceOverlay();

    // ── Internal retake ────────────────────────────────────────────────────────
    // RETAKE BUG FIX:
    // The stale lastBoxSmooth caused the overlay to keep drawing the old box
    // position for several frames after retake. Clearing it here forces the
    // EMA to restart from the tracker's next live detection instead of
    // interpolating from a ghost position.
    // DO NOT call startCamera() or stopCamera() here — the stream stays alive.
    // DO NOT start a second overlay RAF loop — the tracker already owns the canvas.
    function _doRetake() {
        lastBoxSmooth = null;           // clear stale EMA box — tracker resets on next tick
        enrollment.startAutoEnrollment();
        updateProgress(0, cfg.minFrames || 5);
        updateDots();
        setStatus('Ready. Look straight at the camera.', 'info');
        setLiveness(0, 'info');
    }

    // ── Callbacks ──────────────────────────────────────────────────────────────
    enrollment.callbacks.onStatus         = setStatus;
    enrollment.callbacks.onLivenessUpdate = setLiveness;

    enrollment.callbacks.onCaptureProgress = function (current, target) {
        updateProgress(current, target);
        updateDots();
        if (typeof window.enrollCallbacks === 'object' &&
            window.enrollCallbacks !== null &&
            typeof window.enrollCallbacks.onCaptureProgress === 'function') {
            window.enrollCallbacks.onCaptureProgress(current);
        }
    };

    enrollment.callbacks.onAngleUpdate = function (next) {
        if (next && next.bucket !== 'other') showAngle(next);
    };

    enrollment.callbacks.onDistanceFeedback = function (feedback) {
        var statusEl = document.getElementById('cameraStatusText');
        if (!statusEl) return;
        var mob = FaceAttend.Utils && FaceAttend.Utils.isMobile ? FaceAttend.Utils.isMobile() : false;
        if (feedback.status === 'too_far') statusEl.textContent = mob ? 'Move a bit closer 📱' : 'Move closer — face too small';
        if (feedback.status === 'warning') statusEl.textContent = 'Good, but can be closer 👍';
        if (feedback.status === 'good')    statusEl.textContent = 'Perfect distance! Hold still ✓';
    };

    enrollment.callbacks.onQualityFeedback = function (feedback) {
        if (feedback.type === 'blur') {
            var s = document.getElementById('cameraStatusText');
            if (s) s.textContent = 'Image blurry — hold steadier or add light';
        }
    };

    // ── onReadyToConfirm — enforce all 4 angles (FIX-ANGLE-01) ───────────────
    enrollment.callbacks.onReadyToConfirm = function (data) {
        setStatus('Capture complete! Reviewing...', 'success');

        // Path 1: mobile wizard intercept
        if (typeof window.enrollCallbacks === 'object' &&
            window.enrollCallbacks !== null &&
            typeof window.enrollCallbacks.onReadyToConfirm === 'function') {
            window.enrollCallbacks.onReadyToConfirm(data);
            return;
        }

        // Path 2: missing angles — warn and resume
        if (!data.allAngles) {
            var REQUIRED = ['center', 'left', 'right', 'down'];
            var capturedSet = {};
            (data.frames || []).forEach(function (f) {
                if (f.poseBucket) capturedSet[f.poseBucket] = true;
            });
            var missing = REQUIRED.filter(function (a) { return !capturedSet[a]; });

            if (typeof Swal !== 'undefined') {
                Swal.fire({
                    icon:              'warning',
                    title:             'More angles needed',
                    html:              'Please capture <b>all 4 angles</b> for robust identification.' +
                                       '<br><br>Missing: <b>' + missing.join(', ') + '</b>' +
                                       '<br>Captured so far: ' + data.angleCount + ' / 5',
                    confirmButtonText: '<i class="fa-solid fa-camera me-1"></i>Continue Capturing',
                    confirmButtonColor: '#3b82f6',
                    background:        dark() ? '#0f172a' : '#fff',
                    color:             dark() ? '#f8fafc' : '#0f172a',
                    allowOutsideClick: false,
                    allowEscapeKey:    false
                }).then(function () { _doRetake(); });
            } else {
                alert('Missing angles: ' + missing.join(', ') + '. Please continue capturing.');
                _doRetake();
            }
            return;
        }

        // Path 3: all 4 angles — show confirm dialog with thumbnails
        var thumbPromises = data.frames.slice(0, 3).map(function (frame) {
            return new Promise(function (resolve) {
                if (!frame || !frame.blob) { resolve(null); return; }
                var reader = new FileReader();
                reader.onload  = function (e) { resolve(e.target.result); };
                reader.onerror = function ()  { resolve(null); };
                reader.readAsDataURL(frame.blob);
            });
        });

        Promise.all(thumbPromises).then(function (dataUrls) {
            var thumbHtml = '';
            dataUrls.forEach(function (url) {
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
                        'all 4 angles captured</span></div>' +
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
                    confirmButtonColor: '#22c55e',
                    cancelButtonColor:  '#475569',
                    background:        dark() ? '#0f172a' : '#fff',
                    color:             dark() ? '#f8fafc' : '#0f172a',
                    allowOutsideClick: false,
                    allowEscapeKey:    false
                }).then(function (result) {
                    if (result.isConfirmed) {
                        showProcessing(true, 'Processing enrollment...');
                        enrollment.performEnrollment();
                    } else {
                        _doRetake();
                    }
                });
            } else {
                var ok = window.confirm(
                    'Ready to Enroll!\n\n' + data.frameCount + ' frames, all 4 angles.\n' +
                    'Best liveness: ' + data.bestLiveness + '%\n\nConfirm?');
                if (ok) { showProcessing(true, 'Processing enrollment...'); enrollment.performEnrollment(); }
                else      _doRetake();
            }
        });
    };

    enrollment.callbacks.onEnrollmentComplete = function (count) {
        showProcessing(false);
        swalFire({ icon: 'success', title: 'Enrollment Complete!', text: count + ' face samples saved.' });
        if (typeof window.enrollCallbacks === 'object' && window.enrollCallbacks.onEnrollmentComplete)
            window.enrollCallbacks.onEnrollmentComplete({ vectorsSaved: count });
        if (cfg.redirectUrl) setTimeout(function () { window.location.href = cfg.redirectUrl; }, 1800);
    };

    enrollment.callbacks.onEnrollmentError = function (result) {
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
    Object.defineProperty(enrollment, 'isRunning', { get: function () { return _running; } });
    enrollment.start = startCamera;
    enrollment.stop  = stopCamera;

    enrollment.getEncodings = function () {
        var result = [];
        for (var i = 0; i < this.goodFrames.length; i++) {
            var enc = this.goodFrames[i].encoding || this.goodFrames[i].enc || null;
            if (enc) result.push(enc);
        }
        return result;
    };

    window.FaceAttendEnrollment = enrollment;

})();
