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
        scanUrl:     root.getAttribute('data-scan-url')      || '/api/scan/frame',
        enrollUrl:   root.getAttribute('data-enroll-url')    || '/api/enrollment/enroll',
        redirectUrl: root.getAttribute('data-redirect-url')  || '',
        minFrames:   parseInt(root.getAttribute('data-min-frames')   || '3',    10),
        livenessTh:  parseFloat(root.getAttribute('data-liveness-th') || '0.75')
    };

    // ── DOM ────────────────────────────────────────────────────────────────────
    // Use FaceAttend.Utils.el if available, fallback to native
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
        // FIX-002: confirmBtn and retakeBtn REMOVED.
        // Confirmation and retake are now handled by the automatic Swal
        // dialog fired via enrollment.callbacks.onReadyToConfirm.
        // The button elements (#enrollConfirmBtn, #enrollRetakeBtn) have also
        // been removed from _EnrollmentComponent.cshtml markup.
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

    // ── Camera Component Initialization ───────────────────────────────────────
    // The _Camera.cshtml partial now outputs data-* attributes instead of inline
    // <script> blocks (which cause Razor "nested sections" error when rendered
    // from inside @section scripts). We initialize the camera component here.
    (function initCameraComponent() {
        var cameraContainer = root.querySelector('.fa-camera');
        if (!cameraContainer) return;

        var containerId = cameraContainer.dataset.containerId;
        var videoId     = cameraContainer.dataset.videoId;
        var guideId     = cameraContainer.dataset.guideId;
        var statusId    = cameraContainer.dataset.statusId;

        var container = document.getElementById(containerId);
        var video     = document.getElementById(videoId);
        var guide     = document.getElementById(guideId);
        var statusEl  = document.getElementById(statusId);
        var flash     = document.getElementById(containerId + '-flash');

        if (!container) return;

        // Camera state
        var cameraState = { isActive: false };

        function startCameraComponent(options) {
            if (!window.FaceAttend || !window.FaceAttend.Camera) {
                console.error('[Camera Component] FaceAttend.Camera not available');
                return Promise.reject('Camera module not loaded');
            }

            var opts = {};
            try {
                opts = JSON.parse(container.dataset.cameraOptions || '{}');
            } catch(e) {}

            if (options) Object.assign(opts, options);

            return new Promise(function(resolve, reject) {
                window.FaceAttend.Camera.start(
                    video,
                    opts,
                    function(stream) {
                        cameraState.isActive = true;
                        container.classList.add('fa-camera--active');
                        resolve(stream);
                    },
                    function(err) { reject(err); }
                );
            });
        }

        function stopCameraComponent() {
            if (window.FaceAttend && window.FaceAttend.Camera) {
                window.FaceAttend.Camera.stop();
            }
            cameraState.isActive = false;
            container.classList.remove('fa-camera--active');
        }

        function setGuideState(state) {
            if (!guide) return;
            guide.classList.remove(
                'fa-camera__guide--active',
                'fa-camera__guide--success',
                'fa-camera__guide--warning'
            );
            if (state) guide.classList.add('fa-camera__guide--' + state);
        }

        function setStatusText(text, type) {
            if (!statusEl) return;
            statusEl.textContent = text;
            statusEl.className = 'fa-camera__status';
            if (type) statusEl.classList.add('fa-camera__status--' + type);
        }

        function triggerFlash() {
            if (!flash) return;
            flash.classList.add('fa-camera__flash--active');
            setTimeout(function() {
                flash.classList.remove('fa-camera__flash--active');
            }, 150);
        }

        // Auto-start if requested
        var autoStart = container.dataset.autostart === 'true';
        if (autoStart && window.FaceAttend && window.FaceAttend.Camera) {
            startCameraComponent();
        }

        // Expose API on container
        container.faceCamera = {
            start: startCameraComponent,
            stop: stopCameraComponent,
            setGuideState: setGuideState,
            setStatus: setStatusText,
            flash: triggerFlash
        };
    })();

    // ── FaceProgress Component Initialization ────────────────────────────────
    (function initFaceProgressComponent() {
        var fpContainer = root.querySelector('.face-progress');
        if (!fpContainer) return;

        var containerId = fpContainer.dataset.containerId;
        var textId      = fpContainer.dataset.textId;
        var barId       = fpContainer.dataset.barId;
        var dotsId      = fpContainer.dataset.dotsId;
        var anglesId    = fpContainer.dataset.anglesId;
        var target      = parseInt(fpContainer.dataset.target || '5', 10);
        var maxDots     = parseInt(fpContainer.dataset.max || '10', 10);

        var textEl   = document.getElementById(textId);
        var barEl    = document.getElementById(barId);
        var dotsEl   = document.getElementById(dotsId);
        var anglesEl = document.getElementById(anglesId);
        var promptEl = document.getElementById(containerId + '-prompt');

        function updateProgress(current, buckets) {
            var percentage = Math.min(100, Math.round((current / target) * 100));

            if (textEl) textEl.textContent = current + ' / ' + target;

            if (barEl) {
                barEl.style.width = percentage + '%';
                barEl.classList.toggle('progress-bar__fill--success', current >= target);
            }

            if (dotsEl) {
                var dots = dotsEl.querySelectorAll('.progress-dots__dot');
                dots.forEach(function(dot, index) {
                    dot.classList.remove('progress-dots__dot--active', 'progress-dots__dot--complete');
                    if (index < current) dot.classList.add('progress-dots__dot--complete');
                });
            }

            if (anglesEl && buckets) {
                buckets.forEach(function(bucket) {
                    var item = anglesEl.querySelector('[data-bucket="' + bucket + '"]');
                    if (item) item.classList.add('progress-angles__item--captured');
                });
            }
        }

        function setNextAngle(label, icon) {
            if (!promptEl) return;
            if (label) {
                promptEl.style.display = 'flex';
                promptEl.querySelector('span').textContent = label;
                if (icon) {
                    promptEl.querySelector('i').className = 'fa-solid ' + icon;
                }
            } else {
                promptEl.style.display = 'none';
            }
        }

        // Expose API on container
        fpContainer.faceProgress = {
            update: updateProgress,
            setNextAngle: setNextAngle
        };
    })();

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
    // Use FaceAttend.Utils.isDark if available
    function dark() {
        if (FaceAttend.Utils && FaceAttend.Utils.isDark) {
            return FaceAttend.Utils.isDark();
        }
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
        // FIX-002: confirmBtn toggle removed  button no longer exists in markup.
        // The Swal confirmation fires automatically via onReadyToConfirm callback.
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

    // Wrapper for notifications (uses FaceAttend.Notify if available)
    function swal(opts) {
        // Use FaceAttend.Notify if available
        if (FaceAttend.Notify) {
            if (opts.icon === 'error') {
                FaceAttend.Notify.errorModal(opts.title, opts.text);
            } else if (opts.icon === 'success') {
                FaceAttend.Notify.successModal(opts.title, opts.text, opts.onConfirm);
            } else {
                // Default to error modal for other types
                FaceAttend.Notify.errorModal(opts.title || 'Notice', opts.text);
            }
            return;
        }
        
        // Fallback to native Swal
        if (typeof Swal !== 'undefined') {
            Swal.fire(Object.assign({
                background: dark() ? '#0f172a' : '#fff',
                color:      dark() ? '#f8fafc' : '#0f172a'
            }, opts));
        }
    }

    //  FIX-002: Internal retake 
    // Called when the user clicks "Retake" in the Swal confirmation dialog.
    // Resets all capture state and restarts the auto-capture loop.
    //
    // NOTE: enrollment.startAutoEnrollment() already handles resetting
    // goodFrames, passHist, enrolled, lastFaceBox internally.
    // The camera stream is still active at this point  only the capture
    // interval was stopped when _fireReadyToConfirm fired. So we do NOT
    // need to call startCamera() again  just startAutoEnrollment().
    function _doRetake() {
        enrollment.startAutoEnrollment();               // resets state, restarts interval
        updateProgress(0, cfg.minFrames || 8);          // reset progress bar + text
        updateDots();                                   // reset angle dots to uncaptured
        if (typeof enrollment.getNextAnglePrompt === 'function') {
            showAngle(enrollment.getNextAnglePrompt()); // reset angle guidance to 'center'
        }
        setStatus('Retaking  follow the angle prompts.', 'info');
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
                setStatus('Camera error: ' + msg + ' — Please allow camera access and reload.', 'danger');
            });
    }

    function stopCamera() {
        if (!_running) return;
        _running = false;
        enrollment.stopCamera();
        setStatus('Camera stopped.', 'info');
        setLiveness(0, 'info');
    }

    // ── Face bounding box overlay (replaces static dashed circle guide) ────────
    var overlayCanvas = document.getElementById('enrollFaceCanvas');
    var overlayCtx    = overlayCanvas ? overlayCanvas.getContext('2d') : null;

    function drawFaceOverlay() {
        requestAnimationFrame(drawFaceOverlay);
        if (!overlayCanvas || !overlayCtx) return;

        // Size canvas to its CSS display size each frame (handles resizes)
        var cw = overlayCanvas.offsetWidth;
        var ch = overlayCanvas.offsetHeight;
        if (cw < 1 || ch < 1) return;
        overlayCanvas.width  = cw;
        overlayCanvas.height = ch;
        overlayCtx.clearRect(0, 0, cw, ch);

        var faceBox = enrollment.lastFaceBox;
        if (!faceBox || !faceBox.w || !faceBox.h) return;
        if (!ui.video || !ui.video.videoWidth)      return;

        // Map face box from video pixel space → canvas display space
        var scaleX = cw / ui.video.videoWidth;
        var scaleY = ch / ui.video.videoHeight;

        // Video is CSS-mirrored (transform:scaleX(-1)), so mirror X coordinate
        var bx = (ui.video.videoWidth - faceBox.x - faceBox.w) * scaleX;
        var by = faceBox.y * scaleY;
        var bw = faceBox.w * scaleX;
        var bh = faceBox.h * scaleY;

        var goodCount = enrollment.goodFrames ? enrollment.goodFrames.length : 0;
        var target    = cfg.minFrames || 8;
        var color     = goodCount >= target ? '#22c55e' : '#3b82f6';
        var cLen      = Math.min(bw, bh) * 0.18;

        overlayCtx.strokeStyle = color;
        overlayCtx.lineWidth   = 2.5;
        overlayCtx.lineCap     = 'round';
        overlayCtx.lineJoin    = 'round';
        overlayCtx.shadowColor = color;
        overlayCtx.shadowBlur  = 10;

        function bracket(ax, ay, bx2, by2, cx, cy) {
            overlayCtx.beginPath();
            overlayCtx.moveTo(ax, ay);
            overlayCtx.lineTo(bx2, by2);
            overlayCtx.lineTo(cx, cy);
            overlayCtx.stroke();
        }
        // Top-left
        bracket(bx + cLen, by,      bx,      by,      bx,      by + cLen);
        // Top-right
        bracket(bx+bw-cLen, by,     bx+bw,   by,      bx+bw,   by + cLen);
        // Bottom-left
        bracket(bx + cLen, by+bh,   bx,      by+bh,   bx,      by+bh-cLen);
        // Bottom-right
        bracket(bx+bw-cLen, by+bh,  bx+bw,   by+bh,   bx+bw,   by+bh-cLen);

        // Thin rect fill behind corners
        overlayCtx.shadowBlur  = 0;
        overlayCtx.globalAlpha = 0.25;
        overlayCtx.lineWidth   = 0.5;
        overlayCtx.strokeRect(bx, by, bw, bh);
        overlayCtx.globalAlpha = 1;
    }
    drawFaceOverlay();

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

    // FIX-002: onReadyToConfirm  fires when auto-capture completes.
    // The core has stopped the capture interval. Camera stream is still active.
    // This callback shows the Swal confirmation dialog with:
    //   - Thumbnails of the 3 best captured frames (converted from Blob  DataURL)
    //   - Frame count and angle count summary
    //   - Best liveness score
    //   - Confirm button  calls enrollment.performEnrollment()
    //   - Retake button  calls _doRetake()
    //
    // MOBILE SURFACE INTERCEPTION:
    // If window.enrollCallbacks.onReadyToConfirm is defined (set by the mobile
    // wizard in Views/MobileRegistration/Enroll.cshtml), this callback delegates
    // entirely to it and returns early WITHOUT showing the Swal.
    // The mobile wizard advances to Step 3 (review) instead.
    // This pattern is consistent with how onCaptureProgress and
    // onEnrollmentComplete are forwarded to the mobile wizard.
    enrollment.callbacks.onReadyToConfirm = function (data) {
        setStatus('Capture complete! Review your frames below.', 'success');

        //  External interceptor check (mobile wizard) 
        if (typeof window.enrollCallbacks === 'object' &&
            window.enrollCallbacks !== null &&
            typeof window.enrollCallbacks.onReadyToConfirm === 'function') {
            window.enrollCallbacks.onReadyToConfirm(data);
            return;  // mobile wizard handles everything from here
        }

        //  Admin / Visitor surface: show Swal confirmation 

        // Step 1: Convert Blob objects to DataURLs for <img> thumbnail previews.
        // data.frames is sorted by liveness probability descending, so
        // frames[0] is always the best quality frame. We take the top 3.
        var topFrames = data.frames.slice(0, 3);

        var thumbPromises = topFrames.map(function (frame) {
            return new Promise(function (resolve) {
                // Guard: blob must exist (some frames may have null blob in edge cases)
                if (!frame || !frame.blob) { resolve(null); return; }
                var reader = new FileReader();
                reader.onload  = function (e) { resolve(e.target.result); };
                reader.onerror = function ()  { resolve(null); };
                reader.readAsDataURL(frame.blob);
            });
        });

        Promise.all(thumbPromises).then(function (dataUrls) {

            // Step 2: Build thumbnail strip HTML.
            // Each thumbnail is 80x80px, object-fit:cover, with a subtle border.
            // Null DataURLs (failed reads) are silently skipped.
            var thumbHtml = '';
            dataUrls.forEach(function (url) {
                if (url) {
                    thumbHtml +=
                        '<img src="' + url + '" ' +
                        'style="width:80px;height:80px;object-fit:cover;' +
                        'border-radius:8px;border:2px solid rgba(255,255,255,0.15);' +
                        'flex-shrink:0;margin:4px;" />';
                }
            });

            // Step 3: Build angle status line.
            // Green check if all 5 captured, amber warning if partial.
            var angleStatusHtml = data.allAngles
                ? '<span style="color:#22c55e;">' +
                  '<i class="fa-solid fa-circle-check" style="margin-right:6px;"></i>' +
                  'All 5 angles captured</span>'
                : '<span style="color:#f59e0b;">' +
                  '<i class="fa-solid fa-triangle-exclamation" style="margin-right:6px;"></i>' +
                  data.angleCount + ' / 5 angles captured</span>';

            // Step 4: Assemble the full Swal HTML body.
            var summaryHtml =
                // Thumbnail row
                '<div style="display:flex;justify-content:center;gap:8px;' +
                'margin-bottom:14px;flex-wrap:wrap;">' +
                    thumbHtml +
                '</div>' +
                // Stats card
                '<div style="background:rgba(255,255,255,0.05);border-radius:10px;' +
                'padding:12px 16px;text-align:left;font-size:0.875rem;line-height:2;">' +
                    '<div>' +
                        '<i class="fa-solid fa-layer-group" ' +
                        'style="margin-right:8px;color:#3b82f6;"></i>' +
                        '<strong>' + data.frameCount + '</strong> frames captured' +
                    '</div>' +
                    '<div>' + angleStatusHtml + '</div>' +
                    '<div>' +
                        '<i class="fa-solid fa-shield-heart" ' +
                        'style="margin-right:8px;color:#22c55e;"></i>' +
                        'Best liveness: <strong>' + data.bestLiveness + '%</strong>' +
                    '</div>' +
                '</div>';

            // Step 5: Show Swal.
            // allowOutsideClick and allowEscapeKey are both false  the user
            // MUST choose Confirm or Retake. We cannot let them dismiss by
            // accident and leave the enrollment in a limbo state (capture
            // stopped, no submission, no retake).
            if (typeof Swal !== 'undefined') {
                Swal.fire({
                    title:              'Ready to Enroll',
                    html:               summaryHtml,
                    icon:               'success',
                    showCancelButton:   true,
                    confirmButtonText:  '<i class="fa-solid fa-check" ' +
                                        'style="margin-right:6px;"></i>Confirm Enrollment',
                    cancelButtonText:   '<i class="fa-solid fa-rotate-left" ' +
                                        'style="margin-right:6px;"></i>Retake',
                    confirmButtonColor: '#22c55e',
                    cancelButtonColor:  '#475569',
                    background:         dark() ? '#0f172a' : '#ffffff',
                    color:              dark() ? '#f8fafc' : '#0f172a',
                    allowOutsideClick:  false,
                    allowEscapeKey:     false,
                    reverseButtons:     false   // Confirm on left, Retake on right
                }).then(function (result) {
                    if (result.isConfirmed) {
                        // User confirmed  submit frames to server
                        showProcessing(true, 'Processing enrollment...');
                        enrollment.performEnrollment();
                    } else {
                        // result.isDismissed with dismiss reason 'cancel'
                        // User wants to retake  restart capture from scratch
                        _doRetake();
                    }
                });

            } else {
                // Fallback: native browser confirm (Swal bundle not loaded).
                // This should never happen in production  sweetalert bundle
                // is always loaded on enrollment pages. Included as a safety net.
                var confirmed = window.confirm(
                    'Ready to Enroll!\n\n' +
                    data.frameCount + ' frames captured\n' +
                    data.angleCount + '/5 angles\n' +
                    'Best liveness: ' + data.bestLiveness + '%\n\n' +
                    'Click OK to confirm, Cancel to retake.'
                );
                if (confirmed) {
                    showProcessing(true, 'Processing enrollment...');
                    enrollment.performEnrollment();
                } else {
                    _doRetake();
                }
            }
        }); // end Promise.all.then
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
        // Silent fail — status bar only, no Swal popup. Employee sees the message
        // and can retake without being interrupted by a modal dialog.
        setStatus(msg, 'danger');
        if (typeof window.enrollCallbacks === 'object' && window.enrollCallbacks.onEnrollmentError)
            window.enrollCallbacks.onEnrollmentError({ message: msg });
    };

    // ── Button handlers ────────────────────────────────────────────────────────
    // FIX-002: confirmBtn and retakeBtn event listeners REMOVED.
    // Confirmation and retake are now handled by the Swal dialog in the
    // onReadyToConfirm callback above. The button elements themselves have
    // also been removed from _EnrollmentComponent.cshtml.

    // ── Init - UI state only, camera NOT started ───────────────────────────────
    updateProgress(0, cfg.minFrames || 8);
    setStatus('Waiting for camera...', 'info');
    // FIX-002: confirmBtn init removed  button no longer exists in markup.
    showAngle({ bucket: 'center', prompt: 'Look straight at the camera', icon: 'fa-circle-dot' });

    window.addEventListener('beforeunload', stopCamera);

    // ── Public API ─────────────────────────────────────────────────────────────
    Object.defineProperty(enrollment, 'isRunning', { get: function () { return _running; } });
    enrollment.start = startCamera;
    enrollment.stop  = stopCamera;

    window.FaceAttendEnrollment = enrollment;

})();
