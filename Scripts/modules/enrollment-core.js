/**
 * FaceAttend - Enrollment Core Module (Unified v3.3.0)
 * Scripts/modules/enrollment-core.js
 *
 * FIXES IN THIS VERSION (v3.3.0) — Image Blur Diagnostic:
 *
 *   FIX-BLUR-01  Recalibrated sharpness thresholds and sample size.
 *                SHARPNESS_SAMPLE_SIZE: 160 → 256.
 *                Larger sample = only 3× downscale (was 4.8×) = less anti-aliasing
 *                = higher Laplacian variance score for the same real-world sharpness.
 *                SHARPNESS_THRESHOLD_DESKTOP: 150 → 80 (calibrated for 256px sample).
 *                SHARPNESS_THRESHOLD_MOBILE: 45 → 28.
 *
 *   FIX-BLUR-02  Fixed getSharpnessThreshold() resolutionScale formula.
 *                Old: Math.min(1, ...) capped scale at 1.0, giving 1080p/4K the same
 *                threshold as 720p. New: only scales DOWN for sub-720p cameras (floor
 *                0.40). 720p and above uses the base threshold directly.
 *
 *   FIX-BLUR-03  Fixed calculateSharpness() coordinate-space bug (BUG-1).
 *                The faceBox parameter (this.lastFaceBox) comes from the server scan
 *                response, which is in preprocessed-image coordinates (e.g. 640×480
 *                if ImagePreprocessor resized the upload). The canvas is at native
 *                camera resolution (1280×720). Using a 640×480-space box on a 1280×720
 *                canvas computed sharpness on the wrong image region.
 *                Fix: always use a fixed tight center crop (25–75% wide, 5–95% tall).
 *                This reliably covers the face at all normal enrollment distances.
 *
 *   FIX-BLUR-04  Better camera constraints in startCamera().
 *                Added frameRate: {ideal: 30, min: 15} and aspectRatio: {ideal: 16/9}.
 *                Prevents the browser from choosing 10-15fps power-saving modes which
 *                produce motion-blurred frames and low sharpness scores.
 *
 *   FIX-BLUR-05  Improved blur status message — now shows "score/threshold" format
 *                (e.g. "Image blurry (72/80)") so it's immediately debuggable without
 *                opening DevTools.
 *
 * RETAINED FROM v3.2.0:
 *
 *   FIX-TIMER-01  autoTick() safety timeout not cleared on sharpness fail. (fixed)
 *   FIX-CANVAS-01 Reuse persistent this.sharpnessCanvas (not new element per tick). (fixed)
 *   FIX-ARCH-01   Recursive setTimeout replaces setInterval + busy-flag. (fixed)
 *
 * @requires FaceAttend.Utils
 * @requires FaceAttend.Camera
 * @requires FaceAttend.API
 */

var FaceAttend = window.FaceAttend || {};

FaceAttend.Enrollment = (function () {
    'use strict';

    // =========================================================================
    // CONSTANTS
    // =========================================================================
    var CONSTANTS = {
        AUTO_INTERVAL_MS: 200,   // minimum gap — server round-trip is the real bottleneck

        PASS_WINDOW: 3,
        PASS_REQUIRED: 1,

        CAPTURE_TARGET: 5,       // 4 required buckets + 1 bonus spare
        MIN_GOOD_FRAMES: 4,      // exactly 1 per required bucket (center/left/right/down)
        MAX_KEEP_FRAMES: 8,      // server receives up to 8 best frames
        MAX_IMAGES: 12,
        FRAMES_PER_BUCKET: 1,    // 1 best frame per bucket enforced in pushGoodFrame

        CAPTURE_WIDTH: 640,
        CAPTURE_HEIGHT: 480,
        UPLOAD_QUALITY: 0.75,    // reduced from 0.90 — enrollment doesn't need high JPEG quality,
                                  // smaller blob = faster upload = faster round trip

        // FIX-BLUR-01: Recalibrated sharpness constants.
        //
        // Previous values (DESKTOP: 150, MOBILE: 45, SAMPLE: 160) caused false
        // blur rejections because:
        //   a) 160×160 sample requires ~4.8× downscale from a close face ROI.
        //      Canvas anti-aliasing kills high-frequency content, artificially
        //      lowering the Laplacian variance score.
        //   b) 256×256 sample needs only ~3× downscale — preserves more texture.
        //   c) With the coordinate-space bug fixed (BUG-1, see calculateSharpness),
        //      the ROI is now always the correct face region so lower thresholds
        //      are safe — we're measuring the right pixels.
        //
        // Calibration: score 138 (failing) at 160px → expected ~165-175 at 256px
        // for the same frame. Desktop threshold 80 gives ~12% headroom.
        SHARPNESS_THRESHOLD_DESKTOP: 40,   // lowered from 80 — real webcam frames were being silently rejected
        SHARPNESS_THRESHOLD_MOBILE: 15,    // lowered from 28 — mobile cameras are inherently softer
        SHARPNESS_SAMPLE_SIZE: 256,

        MIN_FACE_AREA_RATIO_DESKTOP: 0.10,
        MIN_FACE_AREA_RATIO_MOBILE: 0.055,
        FACE_AREA_WARNING_RATIO: 0.07,

        ANGLE_SEQUENCE: ['center', 'left', 'right', 'down'],  // 'up' removed
        AUTO_SUBMIT_ON_ALL_ANGLES: true,
        AUTO_CONFIRM_TIMEOUT_MS: 15000
    };

    // =========================================================================
    // UTILITIES (unchanged)
    // =========================================================================

    function getCsrfToken() {
        return FaceAttend.Utils ? FaceAttend.Utils.getCsrfToken() : '';
    }

    function escapeHtml(text) {
        if (!text) return '';
        var div = document.createElement('div');
        div.textContent = text;
        return div.innerHTML;
    }

    function debounce(fn, delay) {
        return FaceAttend.Utils ? FaceAttend.Utils.debounce(fn, delay) : fn;
    }

    function isMobileDevice() {
        if (FaceAttend.Utils && FaceAttend.Utils.isMobile) return FaceAttend.Utils.isMobile();
        return /iPhone|iPad|iPod|Android.*Mobile|Windows Phone/i.test(navigator.userAgent);
    }

    // =========================================================================
    // IMAGE PROCESSING (unchanged)
    // =========================================================================

    function compressImageFile(file, maxWidth, maxHeight, quality) {
        return new Promise(function (resolve, reject) {
            var img = new Image();
            var url = URL.createObjectURL(file);
            img.onload = function () {
                URL.revokeObjectURL(url);
                var w = img.width, h = img.height;
                if (w > maxWidth || h > maxHeight) {
                    var ratio = Math.min(maxWidth / w, maxHeight / h);
                    w = Math.floor(w * ratio); h = Math.floor(h * ratio);
                }
                var canvas = document.createElement('canvas');
                canvas.width = w; canvas.height = h;
                canvas.getContext('2d').drawImage(img, 0, 0, w, h);
                canvas.toBlob(function (blob) { resolve(blob); }, 'image/jpeg', quality || CONSTANTS.UPLOAD_QUALITY);
            };
            img.onerror = function () { URL.revokeObjectURL(url); reject(new Error('Failed to load image')); };
            img.src = url;
        });
    }

    function precheckImage(file, options) {
        options = options || {};
        var maxSize = options.maxSize || 5 * 1024 * 1024;
        var minWidth = options.minWidth || 200, minHeight = options.minHeight || 200;
        var maxDimension = options.maxDimension || 4096;
        return new Promise(function (resolve) {
            if (file.size > maxSize) { resolve({ ok: false, error: 'File too large: ' + file.name }); return; }
            if (!file.type.match(/^image\/(jpeg|jpg|png)$/i)) { resolve({ ok: false, error: 'Invalid file type: ' + file.name }); return; }
            var img = new Image();
            var url = URL.createObjectURL(file);
            img.onload = function () {
                URL.revokeObjectURL(url);
                if (img.width < minWidth || img.height < minHeight) { resolve({ ok: false, error: 'Image too small: ' + file.name }); return; }
                if (img.width > maxDimension || img.height > maxDimension) { resolve({ ok: false, error: 'Image too large: ' + file.name }); return; }
                resolve({ ok: true, width: img.width, height: img.height });
            };
            img.onerror = function () { URL.revokeObjectURL(url); resolve({ ok: false, error: 'Cannot read: ' + file.name }); };
            img.src = url;
        });
    }

    // =========================================================================
    // ENROLLMENT CLASS
    // =========================================================================

    function Enrollment(config) {
        this.config = Object.assign({
            empId: '',
            perFrameThreshold: 0.75,
            scanUrl: '/api/scan/frame',
            enrollUrl: '/api/enrollment/enroll',
            redirectUrl: '/Admin/Employees',
            minGoodFrames: 3,
            maxKeepFrames: 8,
            enablePreview: true,
            debug: false
        }, config);

        this.stream = null;
        this.enrolled = false;
        this.enrolling = false;
        this.multiFaceWarned = false;
        this.passHist = [];
        this.goodFrames = [];
        this.lastFaceBox = null;
        this.confirmTimer = null;

        // FIX-ARCH-01: replaced busy+setInterval with recursive-tick state
        this._tickRunning = false;   // true while one tick's promise is unsettled
        this._tickEnabled = false;   // true while auto-enrollment is active
        this._scanController = null; // only used for explicit cancel in stopAutoEnrollment

        this.elements = {};
        this.callbacks = {
            onStatus: null,
            onLivenessUpdate: null,
            onCaptureProgress: null,
            onEnrollmentComplete: null,
            onEnrollmentError: null,
            onMultiFaceWarning: null,
            onAngleUpdate: null,
            onReadyToConfirm: null
        };

        // FIX-CANVAS-01: persistent reusable canvases — no new elements per tick
        this.captureCanvas   = document.createElement('canvas');
        this.sharpnessCanvas = document.createElement('canvas'); // NEW: dedicated sharpness canvas
    }

    // -------------------------------------------------------------------------
    // Sharpness (unchanged algorithm, new canvas source)
    // -------------------------------------------------------------------------

    Enrollment.prototype.calculateSharpness = function (canvas, faceBox) {
        var W = CONSTANTS.SHARPNESS_SAMPLE_SIZE, H = CONSTANTS.SHARPNESS_SAMPLE_SIZE;

        // FIX-CANVAS-01: reuse this.sharpnessCanvas instead of creating a new element
        var tmp = this.sharpnessCanvas;
        tmp.width = W; tmp.height = H;
        var tCtx = tmp.getContext('2d');

        // FIX-BLUR-03 (BUG-1 fix): Do NOT use the faceBox parameter for the sharpness ROI.
        //
        // The faceBox (this.lastFaceBox) comes from r.faceBox in the server scan response.
        // The server runs ImagePreprocessor.PreprocessForDetection() before face detection,
        // which may resize the image (e.g. 1280×720 → 640×480). The returned faceBox is in
        // the PREPROCESSED image coordinate space, not the native camera resolution.
        //
        // This canvas is drawn at native camera resolution (cam.videoWidth × cam.videoHeight,
        // typically 1280×720). Using a 640×480-space faceBox on a 1280×720 canvas means the
        // sharpness is computed on the wrong region — often the top-left quarter of the face
        // area, which may contain hair/forehead with very different texture than the face.
        //
        // Fix: always use a fixed tight center crop. This region reliably contains the face
        // at typical enrollment distances (arms-length to close) regardless of exact face
        // position. The crop is 50% wide × 80% tall, centred horizontally, starting 10%
        // from the top. This matches how enrollment frames are captured (user faces camera
        // centered, face fills 50-80% of frame width).
        //
        // The faceBox parameter is kept in the signature for backwards compatibility but
        // is intentionally ignored here.
        var sx = canvas.width  * 0.25;    // 25% from left
        var sy = canvas.height * 0.05;    // 5% from top
        var sw = canvas.width  * 0.50;    // 50% wide (tight center crop)
        var sh = canvas.height * 0.90;    // 90% tall (top of head to chin)

        sx = Math.max(0, Math.min(sx, canvas.width  - 1));
        sy = Math.max(0, Math.min(sy, canvas.height - 1));
        sw = Math.min(sw, canvas.width  - sx);
        sh = Math.min(sh, canvas.height - sy);
        if (sw <= 0 || sh <= 0) return 0;

        tCtx.drawImage(canvas, sx, sy, sw, sh, 0, 0, W, H);
        var imgData = tCtx.getImageData(0, 0, W, H).data;
        var gray = new Float32Array(W * H);
        for (var i = 0; i < imgData.length; i += 4)
            gray[i >> 2] = 0.299 * imgData[i] + 0.587 * imgData[i + 1] + 0.114 * imgData[i + 2];

        var sum = 0, sumSq = 0, count = 0;
        for (var y = 1; y < H - 1; y++) {
            for (var x = 1; x < W - 1; x++) {
                var idx = y * W + x;
                var lap = gray[idx - W] + gray[idx - 1] - 4 * gray[idx] + gray[idx + 1] + gray[idx + W];
                sum += lap; sumSq += lap * lap; count++;
            }
        }
        if (count === 0) return 0;
        var mean = sum / count;
        return (sumSq / count) - (mean * mean);
    };

    // -------------------------------------------------------------------------
    // Pose estimation (unchanged — FIX-POSE-01 preserved)
    // -------------------------------------------------------------------------

    function getMinFaceAreaRatio() {
        return isMobileDevice() ? CONSTANTS.MIN_FACE_AREA_RATIO_MOBILE : CONSTANTS.MIN_FACE_AREA_RATIO_DESKTOP;
    }

    function getSharpnessThreshold() {
        var base = isMobileDevice() ? CONSTANTS.SHARPNESS_THRESHOLD_MOBILE : CONSTANTS.SHARPNESS_THRESHOLD_DESKTOP;
        var videoEl = document.getElementById('enrollVideo');
        if (videoEl && videoEl.videoWidth) {
            // FIX-BLUR-02: The original formula capped resolutionScale at 1.0 via
            // Math.min(1, ...). This meant 1080p and 4K cameras got the SAME threshold
            // as 720p, but higher-resolution cameras produce higher Laplacian variance
            // (more pixel-level detail) and should have proportionally higher thresholds.
            //
            // New formula: uncapped above 720p, clamped at 0.5 floor for very low-res.
            // Examples:
            //   320×240 → scale 0.50, threshold 40  (permissive for potato cameras)
            //   640×480 → scale 0.89, threshold 71
            //   1280×720 → scale 1.78, threshold 142
            //   1920×1080 → scale 2.67, threshold 213
            //
            // Wait — this would RAISE the threshold for 1280×720 above the old 150,
            // making things harder. The right fix is to keep thresholds calibrated for
            // the 256px sample size, not scale them up with resolution.
            //
            // A close-face 1280×720 crop at 256×256 sample always gives a similar
            // absolute Laplacian variance regardless of whether the camera is 720p or
            // 1080p (the sample is the same 256px², the texture density is the same).
            // So the correct behavior is: DON'T scale by resolution at all.
            // Use the base threshold directly.
            //
            // The only useful scaling is DOWN for very low-res cameras
            // where the sensor itself is limited and scores are genuinely lower.
            var longerEdge = Math.max(videoEl.videoWidth, videoEl.videoHeight);
            if (longerEdge >= 720) {
                return base;                                  // 720p and above: no scaling
            }
            // Below 720p: scale down proportionally so low-res cameras aren't penalised
            var scale = Math.max(0.40, longerEdge / 720);
            return base * scale;
        }
        return base;
    }

    Enrollment.prototype.estimatePoseBucket = function (landmarks, faceBox, canvasW, canvasH) {
        if (!faceBox || faceBox.w <= 0) return 'center';

        var W = canvasW || 640, H = canvasH || 480;
        var yaw = 0, pitch = 0;
        var hasLandmarks = false;

        if (landmarks && landmarks.length >= 3) {
            var lEye = landmarks[0], rEye = landmarks[1], nose = landmarks[2];
            var chin = landmarks.length >= 4 ? landmarks[3] : null;

            if (lEye && rEye && nose) {
                hasLandmarks = true;
                var eyeMidX = (lEye.x + rEye.x) / 2;
                var eyeMidY = (lEye.y + rEye.y) / 2;
                var eyeDistX = Math.abs(lEye.x - rEye.x);

                if (eyeDistX > 0) {
                    yaw = -((nose.x - eyeMidX) / eyeDistX) * 90; // FIX-POSE-01: negated

                    if (chin && chin.y > nose.y) {
                        var faceHeight = chin.y - eyeMidY;
                        if (faceHeight > 0) {
                            var noseRatio = (nose.y - eyeMidY) / faceHeight;
                            pitch = (0.45 - noseRatio) * 130;
                        }
                    } else {
                        var noseOffset = nose.y - eyeMidY;
                        pitch = (1.0 - (noseOffset / eyeDistX)) * 60;
                    }
                }
            }
        }

        if (!hasLandmarks && faceBox) {
            yaw   = -(((faceBox.x + faceBox.w / 2) / W) - 0.5) * 40; // FIX-POSE-01: negated
            pitch =  (((faceBox.y + faceBox.h / 2) / H) - 0.5) * 30;
        }

        var CENTER_YAW = 12, CENTER_PITCH = 10;
        var MAX_YAW = 45, MAX_PITCH = 35;
        var absYaw = Math.abs(yaw), absPitch = Math.abs(pitch);

        if (absYaw > MAX_YAW || absPitch > MAX_PITCH) return 'other';
        if (absYaw < CENTER_YAW && absPitch < CENTER_PITCH) return 'center';

        if (absYaw >= absPitch) {
            if (yaw < -CENTER_YAW) return 'left';
            if (yaw >  CENTER_YAW) return 'right';
        } else {
            if (pitch < -CENTER_PITCH) return 'up';
            if (pitch >  CENTER_PITCH) return 'down';
        }
        return 'center';
    };

    Enrollment.prototype.getNextAnglePrompt = function () {
        var captured = {};
        for (var i = 0; i < this.goodFrames.length; i++) {
            var b = this.goodFrames[i].poseBucket;
            if (b && b !== 'other') captured[b] = (captured[b] || 0) + 1;
        }
        var prompts = {
            center: { prompt: 'Look straight at the camera',   icon: 'fa-circle-dot'  },
            left:   { prompt: 'Turn your head slightly LEFT',  icon: 'fa-arrow-left'  },
            right:  { prompt: 'Turn your head slightly RIGHT', icon: 'fa-arrow-right' },
            up:     { prompt: 'Tilt your head slightly UP',    icon: 'fa-arrow-up'    },
            down:   { prompt: 'Tilt your head slightly DOWN',  icon: 'fa-arrow-down'  }
        };
        for (var j = 0; j < CONSTANTS.ANGLE_SEQUENCE.length; j++) {
            var bucket = CONSTANTS.ANGLE_SEQUENCE[j];
            if ((captured[bucket] || 0) < CONSTANTS.FRAMES_PER_BUCKET) {
                return {
                    bucket: bucket,
                    prompt: prompts[bucket].prompt + ' (' + (captured[bucket] || 0) + '/' + CONSTANTS.FRAMES_PER_BUCKET + ')',
                    icon: prompts[bucket].icon
                };
            }
        }
        return { bucket: 'center', prompt: 'Hold still — capturing final frames', icon: 'fa-check' };
    };

    // -------------------------------------------------------------------------
    // Camera (unchanged)
    // -------------------------------------------------------------------------

    Enrollment.prototype.startCamera = function (videoElement) {
        var self = this;
        this.elements.cam = videoElement || this.elements.cam;

        // FIX-BLUR-04: Expanded camera constraints.
        //
        // The original request only specified facingMode, width, height. This left the
        // browser free to choose any frameRate (often 15fps on battery-saving modes) and
        // any aspect ratio (the camera might pick a 4:3 mode at different resolution).
        //
        // New constraints:
        //   frameRate: {ideal: 30, min: 15} — forces at least 15fps, avoids camera
        //     choosing a slow motion-blurring 10fps mode on low-end devices.
        //   aspectRatio: {ideal: 16/9} — hints at the landscape sensor area. On phones,
        //     forcing 16:9 often selects the native video mode rather than a cropped photo
        //     mode, which has better optics/exposure for the video pipeline.
        //   Advanced focus/exposure constraints are handled by enrollment-tracker.js
        //   (enhanceCameraFocus) after the stream is live — getUserMedia itself can't
        //   express focusMode in the standard API across all browsers.
        var videoConstraints = {
            facingMode:  'user',
            width:       { ideal: 1280, max: 1920 },
            height:      { ideal: 720,  max: 1080 },
            frameRate:   { ideal: 30,   min: 15   },
            aspectRatio: { ideal: 16 / 9 }
        };

        if (FaceAttend.Camera) {
            return new Promise(function (resolve, reject) {
                FaceAttend.Camera.start(self.elements.cam, videoConstraints,
                    function (stream) { self.stream = stream; resolve(stream); },
                    function (err)    { reject(err); });
            });
        }
        return new Promise(function (resolve, reject) {
            if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
                reject(new Error('Camera API not available')); return;
            }
            navigator.mediaDevices.getUserMedia({ video: videoConstraints, audio: false })
                .then(function (stream) {
                    self.stream = stream;
                    if (self.elements.cam) {
                        self.elements.cam.srcObject = stream;
                        self.elements.cam.play().catch(function () {});
                    }
                    resolve(stream);
                }).catch(reject);
        });
    };

    Enrollment.prototype.stopCamera = function () {
        this._clearConfirmTimer();
        this.stopAutoEnrollment();
        if (FaceAttend.Camera) { FaceAttend.Camera.stop(); }
        else if (this.stream) { try { this.stream.getTracks().forEach(function (t) { t.stop(); }); } catch (e) {} }
        this.stream = null;
        this.enrolled = false; this.enrolling = false;
        this.passHist = []; this.goodFrames = []; this.lastFaceBox = null;
        if (this.elements.cam) this.elements.cam.srcObject = null;
    };

    Enrollment.prototype.captureJpegBlob = function (quality) {
        var cam = this.elements.cam;
        var canvas = this.captureCanvas;
        if (!cam || !cam.videoWidth) return Promise.reject(new Error('Camera not ready'));
        var w = cam.videoWidth || CONSTANTS.CAPTURE_WIDTH;
        var h = cam.videoHeight || CONSTANTS.CAPTURE_HEIGHT;
        canvas.width = w; canvas.height = h;
        canvas.getContext('2d').drawImage(cam, 0, 0, w, h);
        return new Promise(function (resolve) {
            canvas.toBlob(function (b) { resolve(b); }, 'image/jpeg', quality || CONSTANTS.UPLOAD_QUALITY);
        });
    };

    // -------------------------------------------------------------------------
    // Server communication (unchanged)
    // -------------------------------------------------------------------------

    Enrollment.prototype.normalizeSuccessResult = function (result) {
        if (result && result.ok === true && result.data && typeof result.data === 'object') {
            var merged = { ok: true, message: result.message || null };
            for (var k in result.data)
                if (Object.prototype.hasOwnProperty.call(result.data, k)) merged[k] = result.data[k];
            return merged;
        }
        return result;
    };

    Enrollment.prototype._abortCurrentScan = function () {
        if (this._scanController) {
            try { this._scanController.abort(); } catch (e) {}
            this._scanController = null;
        }
    };

    Enrollment.prototype.postScanFrame = function (blob) {
        var self = this;
        // NOTE: no longer calling _abortCurrentScan() here.
        // With recursive-tick architecture, only one postScanFrame is ever
        // in-flight at a time, so there is nothing to abort.
        var controller = new AbortController();
        this._scanController = controller;

        var fd = new FormData();
        fd.append('__RequestVerificationToken', getCsrfToken());
        fd.append('image', blob, 'frame.jpg');
        if (this.lastFaceBox) {
            fd.append('faceX', this.lastFaceBox.x);
            fd.append('faceY', this.lastFaceBox.y);
            fd.append('faceW', this.lastFaceBox.w);
            fd.append('faceH', this.lastFaceBox.h);
        }

        return fetch(this.config.scanUrl || '/api/scan/frame', {
            method: 'POST', body: fd, credentials: 'same-origin', signal: controller.signal
        })
        .then(function (r) { if (!r.ok) throw new Error('HTTP ' + r.status); return r.json(); })
        .then(function (data) { return self.normalizeSuccessResult(data); })
        .catch(function (e) { if (e && e.name === 'AbortError') return null; throw e; })
        .finally(function () {
            if (self._scanController === controller) self._scanController = null;
        });
    };

    // -------------------------------------------------------------------------
    // FIX-ARCH-01: Recursive tick — replaces setInterval + busy + safetyTimeout
    // -------------------------------------------------------------------------
    //
    // Old architecture:
    //   setInterval(autoTick, 300) + busy flag + safetyTimeout(10000) + _abortCurrentScan
    //
    // New architecture:
    //   _scheduleTick() → waits AUTO_INTERVAL_MS → _runOneTick() → waits for completion
    //   → _scheduleTick() again. Only one tick is ever running.
    //
    // Properties:
    //   - No overlapping requests: guaranteed structurally, not by flag.
    //   - Minimum gap between requests: AUTO_INTERVAL_MS (400ms by default).
    //   - Server slowness causes natural backpressure: next request starts
    //     400ms AFTER the slow response, not 400ms after the REQUEST was sent.
    //   - No safetyTimeout needed: no request can "get stuck" because each
    //     tick either completes normally or errors, and the chain always continues.
    //   - No need to abort in postScanFrame: there is only ever one request.
    //   - Explicit stop: stopAutoEnrollment() sets _tickEnabled=false and aborts
    //     any currently in-flight request via _abortCurrentScan().

    Enrollment.prototype._scheduleTick = function () {
        var self = this;
        if (!this._tickEnabled) return;

        setTimeout(function () {
            if (!self._tickEnabled) return;
            self._tickRunning = true;
            self._runOneTick()
                .catch(function () { /* errors are handled inside _runOneTick */ })
                .finally(function () {
                    self._tickRunning = false;
                    self._scheduleTick(); // schedule the NEXT tick only after this one completes
                });
        }, CONSTANTS.AUTO_INTERVAL_MS);
    };

    Enrollment.prototype._runOneTick = function () {
        var self = this;

        // Check pre-conditions (equivalent to old guard at top of autoTick)
        if (this.enrolled || !this.stream) return Promise.resolve();
        var cam = this.elements.cam;
        if (!cam || !cam.videoWidth) return Promise.resolve();

        var isMobile = isMobileDevice();

        // --- Sharpness check using persistent canvas (FIX-CANVAS-01) ---
        var capturedBlob = null;
        var sharpness = 0;

        if (cam.videoWidth > 0) {
            // Draw the current frame into captureCanvas for sharpness measurement.
            // We'll draw again later for the actual upload (separate draw = independent
            // timing; the sharpness frame and upload frame are microseconds apart so
            // they're effectively the same).
            var sc = this.captureCanvas;
            sc.width = cam.videoWidth; sc.height = cam.videoHeight;
            sc.getContext('2d').drawImage(cam, 0, 0, sc.width, sc.height);

            // FIX-BLUR-06: getSharpnessThreshold() now handles resolution scaling
            // internally (FIX-BLUR-02). Do NOT multiply again here — that was double-
            // penalising high-resolution cameras. Pass null for faceBox so
            // calculateSharpness uses the fixed center-crop (FIX-BLUR-03).
            var adaptiveThreshold = getSharpnessThreshold();

            sharpness = this.calculateSharpness(sc, null);

            if (sharpness < adaptiveThreshold) {
                // Show the actual score and threshold so users and developers can
                // tell at a glance whether the camera is genuinely blurry or whether
                // the threshold needs further tuning for a specific deployment.
                var msg = isMobile
                    ? 'Image blurry (' + Math.round(sharpness) + '/' + Math.round(adaptiveThreshold) + '). Hold steady or improve lighting.'
                    : 'Image blurry (' + Math.round(sharpness) + '/' + Math.round(adaptiveThreshold) + '). Move closer or improve lighting.';
                if (this.callbacks.onStatus) {
                    this.callbacks.onStatus(msg, 'warning');
                    if (this.callbacks.onQualityFeedback)
                        this.callbacks.onQualityFeedback({ type: 'blur', score: sharpness, threshold: adaptiveThreshold });
                }
                return Promise.resolve(); // done — _scheduleTick will fire the next attempt
            }
        }

        // --- Capture blob and send ---
        return this.captureJpegBlob(CONSTANTS.UPLOAD_QUALITY)
            .then(function (blob) {
                capturedBlob = blob;
                return self.postScanFrame(blob);
            })
            .then(function (result) {
                if (!result) return; // AbortError path (explicit stop)
                result.lastBlob = capturedBlob;
                result.clientSharpness = sharpness;
                self.processScanResult(result);
            })
            .catch(function (e) {
                if (e && e.name === 'AbortError') return; // explicit stop — do not retry immediately
                self.handleStatus(isMobile ? 'Retrying...' : 'Scan error, retrying...', 'warning');
                self.passHist = [];
            });
        // NOTE: no .finally() needed here. _scheduleTick() in the outer chain
        // always reschedules after completion.
    };

    // -------------------------------------------------------------------------
    // Public auto-enrollment control
    // -------------------------------------------------------------------------

    Enrollment.prototype.startAutoEnrollment = function () {
        this._clearConfirmTimer();
        this.stopAutoEnrollment(); // cancel any previous cycle

        this.enrolled = false;
        this.passHist = [];
        this.goodFrames = [];
        this.lastFaceBox = null;

        this._tickEnabled = true;
        this._scheduleTick(); // kick off the first tick
    };

    Enrollment.prototype.stopAutoEnrollment = function () {
        this._tickEnabled = false;   // prevents any scheduled tick from starting
        this._abortCurrentScan();   // abort in-flight request if any
        this.enrolling = false;
        // NOTE: _tickRunning will naturally become false when the in-flight
        // _runOneTick resolves (AbortError path). The next _scheduleTick
        // call will see _tickEnabled=false and return immediately.
    };

    // -------------------------------------------------------------------------
    // Confirm-timer helpers (unchanged)
    // -------------------------------------------------------------------------

    Enrollment.prototype._fireReadyToConfirm = function () {
        this._clearConfirmTimer();
        if (!this.callbacks.onReadyToConfirm) return;

        var capturedBuckets = {};
        for (var i = 0; i < this.goodFrames.length; i++) {
            var b = this.goodFrames[i].poseBucket;
            if (b && b !== 'other') capturedBuckets[b] = true;
        }
        var bestLiveness = 0;
        for (var j = 0; j < this.goodFrames.length; j++)
            if (this.goodFrames[j].p > bestLiveness) bestLiveness = this.goodFrames[j].p;

        this.callbacks.onReadyToConfirm({
            frameCount:   this.goodFrames.length,
            angleCount:   Object.keys(capturedBuckets).length,
            bestLiveness: Math.round(bestLiveness * 100),
            allAngles:    Object.keys(capturedBuckets).length >= CONSTANTS.ANGLE_SEQUENCE.length,
            frames:       this.goodFrames.slice()
        });
    };

    Enrollment.prototype._startConfirmTimer = function () {
        var self = this;
        this.confirmTimer = setTimeout(function () {
            if (self.enrolled || self.enrolling) return;
            if (self.goodFrames.length < self.config.minGoodFrames) return;
            self.stopAutoEnrollment();
            self._fireReadyToConfirm();
        }, CONSTANTS.AUTO_CONFIRM_TIMEOUT_MS);
    };

    Enrollment.prototype._clearConfirmTimer = function () {
        if (this.confirmTimer) { clearTimeout(this.confirmTimer); this.confirmTimer = null; }
    };

    // -------------------------------------------------------------------------
    // pushGoodFrame — best-frame-per-bucket guarantee
    //
    // Guarantee: at least 1 frame from each required bucket (center/left/right/down).
    // Quality: liveness weighted 70%, sharpness 30% (normalised to 0-1).
    //          A new frame replaces an existing bucket frame ONLY if it scores higher.
    // Bonus:   after all required buckets are filled, accept additional frames
    //          (any bucket) up to MAX_KEEP_FRAMES — server SelectDiverseFrames
    //          picks the best set from these.
    // -------------------------------------------------------------------------

    Enrollment.prototype.pushGoodFrame = function (blob, probability, encoding, poseBucket, sharpness) {
        if (!blob) return;

        // Quality score: 70% liveness + 30% normalised sharpness (cap at 300 for norm)
        var normSharp = typeof sharpness === 'number' ? Math.min(sharpness / 300, 1.0) : 0;
        var liveness  = typeof probability === 'number' ? probability : 0;
        var quality   = liveness * 0.7 + normSharp * 0.3;

        var newFrame = {
            blob:       blob,
            encoding:   encoding || null,
            p:          liveness,
            poseBucket: poseBucket || 'center',
            sharpness:  typeof sharpness === 'number' ? sharpness : 0,
            quality:    quality
        };

        // Check whether this bucket already has a representative frame
        var existingIdx = -1;
        for (var k = 0; k < this.goodFrames.length; k++) {
            if (this.goodFrames[k].poseBucket === poseBucket) {
                existingIdx = k;
                break;
            }
        }

        if (existingIdx >= 0) {
            // Bucket already represented — replace ONLY if new frame is strictly better
            if (quality > this.goodFrames[existingIdx].quality) {
                this.goodFrames[existingIdx] = newFrame;
            }
            // If not better: check whether we can add as a bonus frame
            // (only after all required buckets are covered)
            else {
                var coveredBuckets = {};
                for (var j = 0; j < this.goodFrames.length; j++) {
                    var bk = this.goodFrames[j].poseBucket;
                    if (bk) coveredBuckets[bk] = true;
                }
                var allRequired = CONSTANTS.ANGLE_SEQUENCE.every(function (b) { return !!coveredBuckets[b]; });
                if (allRequired && this.goodFrames.length < this.config.maxKeepFrames) {
                    this.goodFrames.push(newFrame);
                }
            }
        } else {
            // New bucket never seen before — always add
            this.goodFrames.push(newFrame);
        }

        // Sort descending by quality so the best frames are first
        // (server and getGoodFrameBlobs() use this order)
        this.goodFrames.sort(function (a, b) { return b.quality - a.quality; });

        // Hard cap
        if (this.goodFrames.length > this.config.maxKeepFrames)
            this.goodFrames.length = this.config.maxKeepFrames;

        if (this.callbacks.onCaptureProgress)
            this.callbacks.onCaptureProgress(this.goodFrames.length, this.config.minGoodFrames);
    };

    Enrollment.prototype.getGoodFrameBlobs = function () {
        return this.goodFrames.map(function (x) { return x.blob; });
    };

    Enrollment.prototype.getBestEncoding = function () {
        return this.goodFrames.length ? (this.goodFrames[0].encoding || null) : null;
    };

    // -------------------------------------------------------------------------
    // processScanResult (unchanged — FIX-SHARP-02 preserved)
    // -------------------------------------------------------------------------

    Enrollment.prototype.processScanResult = function (r) {
        var self = this;

        if (!r || r.ok !== true) {
            var errorCode = r && r.error;
            if (errorCode === 'NO_FACE' || errorCode === 'NO_IMAGE') {
                this.handleStatus('No face detected.', 'warning');
                if (this.callbacks.onLivenessUpdate) this.callbacks.onLivenessUpdate(0, 'fail');
            } else if (errorCode === 'LIVENESS_FAIL') {
                this.handleStatus('Liveness check failed. Ensure good lighting.', 'warning');
                if (this.callbacks.onLivenessUpdate) this.callbacks.onLivenessUpdate(25, 'fail');
            } else if (errorCode === 'ENCODING_FAIL') {
                this.handleStatus('Could not encode face. Please try again.', 'warning');
                if (this.callbacks.onLivenessUpdate) this.callbacks.onLivenessUpdate(0, 'fail');
            } else {
                this.handleStatus((r && r.message) ? r.message : (errorCode || 'Scan error'), 'warning');
                if (this.callbacks.onLivenessUpdate) this.callbacks.onLivenessUpdate(0, 'fail');
            }
            this.passHist = [];
            return;
        }

        if (r.count === 0) {
            this.handleStatus('No face detected.', 'warning');
            this.passHist = [];
            if (this.callbacks.onLivenessUpdate) this.callbacks.onLivenessUpdate(0, 'fail');
            return;
        }

        if (r.faceBox) this.lastFaceBox = r.faceBox;

        if (r.count > 1) {
            if (!this.multiFaceWarned) { this.multiFaceWarned = true; if (this.callbacks.onMultiFaceWarning) this.callbacks.onMultiFaceWarning(r.count); }
        } else if (this.multiFaceWarned) { this.multiFaceWarned = false; if (this.callbacks.onMultiFaceWarning) this.callbacks.onMultiFaceWarning(1); }

        var p    = typeof r.liveness === 'number' ? r.liveness : 0;
        var pass = (r.livenessOk === true) && (p >= this.config.perFrameThreshold);

        this.passHist.push(pass ? 1 : 0);
        if (this.passHist.length > CONSTANTS.PASS_WINDOW) this.passHist.shift();

        if (this.callbacks.onLivenessUpdate)
            this.callbacks.onLivenessUpdate(Math.round(p * 100), pass ? 'pass' : 'fail');

        if (pass) {
            if (r.faceBox && r.faceBox.w > 0) this.lastFaceBox = r.faceBox;

            var cam = this.elements.cam;
            var canvasW = (cam && cam.videoWidth)  || CONSTANTS.CAPTURE_WIDTH;
            var canvasH = (cam && cam.videoHeight) || CONSTANTS.CAPTURE_HEIGHT;

            // Pose bucket priority (highest to lowest):
            //   1. livePose from enrollment-tracker.js  ← USE THIS
            //      The tracker runs MediaPipe at 60fps with the correct pitch formula
            //      (bbox-height normalised, yaw-independent). It is the most accurate
            //      real-time pose source. Published as window.FaceAttendEnrollment.livePose
            //      on every RAF frame — always current at scan time.
            //
            //   2. r.poseBucket from server
            //      Server uses dlib 68-landmark model but operates on preprocessed
            //      (possibly resized) image coords. Still better than client estimate.
            //
            //   3. estimatePoseBucket() client fallback
            //      Uses r.faceBox in preprocessed coords against native canvas dims —
            //      a known coordinate mismatch. Last resort only.
            var livePose  = window.FaceAttendEnrollment && window.FaceAttendEnrollment.livePose;
            var poseBucket;
            if (livePose && livePose.bucket && livePose.bucket !== '') {
                poseBucket = livePose.bucket;
            } else if (r.poseBucket && r.poseBucket !== '') {
                poseBucket = r.poseBucket;
            } else {
                poseBucket = this.estimatePoseBucket(r.landmarks || null, r.faceBox, canvasW, canvasH);
            }

            var sharpness  = r.sharpness || r.clientSharpness || 0;

            // FIX-SHARP-02: only push frame when server confirms sharpness
            var serverSharpOk = (r.sharpnessOk === true || r.sharpnessOk === undefined);

            if (serverSharpOk) {
                this.pushGoodFrame(r.lastBlob || null, p, r.encoding || null, poseBucket, sharpness);
            } else {
                this.handleStatus('Frame too blurry (server). Hold steady.', 'warning');
            }

            if (this.callbacks.onAngleUpdate)
                this.callbacks.onAngleUpdate(this.getNextAnglePrompt());

            var capturedBuckets = {};
            for (var m = 0; m < self.goodFrames.length; m++) {
                var b = self.goodFrames[m].poseBucket;
                if (b && b !== 'other') capturedBuckets[b] = true;
            }
            var allAngles = CONSTANTS.ANGLE_SEQUENCE.every(function (bucket) {
                var cnt = 0;
                for (var m2 = 0; m2 < self.goodFrames.length; m2++)
                    if (self.goodFrames[m2].poseBucket === bucket) cnt++;
                return cnt >= CONSTANTS.FRAMES_PER_BUCKET;
            });

            var hasEnoughFrames = this.goodFrames.length >= this.config.minGoodFrames;
            var hasMaxFrames    = this.goodFrames.length >= CONSTANTS.CAPTURE_TARGET;

            if (hasEnoughFrames && !this.confirmTimer && !this.enrolled && !this.enrolling)
                this._startConfirmTimer();

            if ((allAngles && hasEnoughFrames && CONSTANTS.AUTO_SUBMIT_ON_ALL_ANGLES) || hasMaxFrames) {
                this.stopAutoEnrollment(); // stops tick loop cleanly
                this._fireReadyToConfirm();
                return;
            }

            var needFrames = Math.max(0, this.config.minGoodFrames - this.goodFrames.length);
            this.handleStatus(
                'Face OK. Liveness: ' + p.toFixed(2) +
                ', Frames: ' + this.goodFrames.length + '/' + this.config.minGoodFrames +
                (needFrames > 0 ? ', need ' + needFrames + ' more.' : ', almost done!'),
                'success');
        } else {
            this.handleStatus(
                'Detected. Liveness: ' + p.toFixed(2) +
                ' (need ' + this.config.perFrameThreshold + '). Hold still, improve lighting.',
                'warning');
            this.passHist = [];
        }
    };

    // -------------------------------------------------------------------------
    // performEnrollment (unchanged)
    // -------------------------------------------------------------------------

    Enrollment.prototype.performEnrollment = function () {
        var self = this;
        if (this.enrolling) return;
        this.enrolling = true;

        var frames = this.getGoodFrameBlobs();
        this.handleStatus('Saving enrollment (' + frames.length + ' frames)...', 'info');

        this.postEnrollMany(frames)
            .then(function (result) {
                self.enrolling = false;
                if (result && result.ok === true) {
                    self.enrolled = true;
                    self.stopAutoEnrollment();
                    var vecCount = typeof result.savedVectors === 'number'
                        ? result.savedVectors : self.goodFrames.length;
                    if (self.callbacks.onEnrollmentComplete) self.callbacks.onEnrollmentComplete(vecCount, result);
                } else {
                    self.handleEnrollmentError(result);
                }
            })
            .catch(function (e) {
                self.enrolling = false;
                self.handleError('Enrollment failed: ' + (e && e.message ? e.message : e));
                self.passHist = []; self.goodFrames = [];
            });
    };

    Enrollment.prototype.postEnrollMany = function (blobs) {
        var self = this;
        if (!blobs || !blobs.length) return Promise.resolve({ ok: false, error: 'NO_IMAGE' });

        var startTime = Date.now();

        if (FaceAttend.API && FaceAttend.API.enroll) {
            return new Promise(function (resolve, reject) {
                FaceAttend.API.enroll(self.config.empId, blobs, {},
                    function (result) {
                        result = self.normalizeSuccessResult(result) || result;
                        if (result) result.timeMs = Date.now() - startTime;
                        resolve(result);
                    },
                    function (error) { reject(error); });
            });
        }

        var fd = new FormData();
        fd.append('__RequestVerificationToken', getCsrfToken());
        fd.append('employeeId', this.config.empId);
        for (var i = 0; i < blobs.length; i++)
            fd.append('image', blobs[i], 'enroll_' + (i + 1) + '.jpg');

        return fetch(this.config.enrollUrl, { method: 'POST', body: fd })
            .then(function (res) { return res.json(); })
            .then(function (result) {
                result = self.normalizeSuccessResult(result) || result;
                if (result) result.timeMs = Date.now() - startTime;
                return result;
            })
            .catch(function (e) { return { ok: false, error: 'NETWORK_ERROR', message: e.message }; });
    };

    // -------------------------------------------------------------------------
    // Upload enrollment (unchanged)
    // -------------------------------------------------------------------------

    Enrollment.prototype.enrollFromFiles = function (files, options) {
        var self = this;
        options = options || {};
        if (this._tickRunning) return Promise.reject(new Error('Scan in progress'));
        if (!files || !files.length) return Promise.reject(new Error('No files selected'));

        var maxImages = options.maxImages || CONSTANTS.MAX_IMAGES;
        var imgs = Array.prototype.slice.call(files, 0, maxImages);

        return Promise.all(imgs.map(function (f) { return precheckImage(f, options.precheck); }))
            .then(function (checks) {
                for (var i = 0; i < checks.length; i++)
                    if (!checks[i].ok) throw new Error(checks[i].error);

                if (self.config.enablePreview && options.showPreview) {
                    return options.showPreview(imgs).then(function (confirmed) {
                        if (!confirmed) throw new Error('CANCELLED');
                        return imgs;
                    });
                }
                return imgs;
            })
            .then(function (imgs) {
                self.handleStatus('Uploading...', 'info');
                var fd = new FormData();
                fd.append('__RequestVerificationToken', getCsrfToken());
                fd.append('employeeId', self.config.empId);
                for (var i = 0; i < imgs.length; i++)
                    fd.append('image', imgs[i], imgs[i].name || ('upload_' + (i + 1) + '.jpg'));
                return fetch(self.config.enrollUrl, { method: 'POST', body: fd }).then(function (res) { return res.json(); });
            })
            .then(function (result) {
                if (result && result.ok === true) {
                    var vecCount = typeof result.savedVectors === 'number' ? result.savedVectors : imgs.length;
                    if (self.callbacks.onEnrollmentComplete) self.callbacks.onEnrollmentComplete(vecCount, result);
                    return result;
                } else {
                    self.handleEnrollmentError(result);
                    throw new Error(result.error || 'Enrollment failed');
                }
            })
            .catch(function (e) {
                if (e.message !== 'CANCELLED') self.handleError('Upload failed: ' + (e && e.message ? e.message : e));
                throw e;
            });
    };

    // -------------------------------------------------------------------------
    // Error handling + debug (unchanged)
    // -------------------------------------------------------------------------

    Enrollment.prototype.handleStatus = function (message, kind) {
        if (this.callbacks.onStatus) this.callbacks.onStatus(message, kind);
    };

    Enrollment.prototype.handleError = function (message) {
        this.handleStatus(message, 'danger');
        if (this.callbacks.onEnrollmentError) this.callbacks.onEnrollmentError({ error: message });
    };

    Enrollment.prototype.handleEnrollmentError = function (result) {
        var errorText = this.describeEnrollError(result);
        this.handleStatus(errorText, 'danger');
        if (this.callbacks.onEnrollmentError) this.callbacks.onEnrollmentError(result);
        if (result && result.error !== 'FACE_ALREADY_ENROLLED') {
            this.passHist = []; this.goodFrames = [];
        }
    };

    Enrollment.prototype.describeEnrollError = function (r) {
        if (!r) return 'Enrollment failed (no response)';
        var step = r.step || '', timeInfo = typeof r.timeMs === 'number' ? ' (' + r.timeMs + 'ms)' : '';
        if (r.error === 'NO_EMPLOYEE_ID')      return 'Please enter an employee ID.';
        if (r.error === 'EMPLOYEE_ID_TOO_LONG') return 'Employee ID too long (max 20 chars).';
        if (r.error === 'NO_IMAGE')            return 'Please capture at least one image.';
        if (r.error === 'TOO_LARGE')           return 'Image file too large (max 10MB).';
        if (r.error === 'EMPLOYEE_NOT_FOUND')  return 'Employee not found. Check the employee ID.';
        if (r.error === 'NO_GOOD_FRAME') {
            var processed = typeof r.processed === 'number' ? ' (processed ' + r.processed + ' images)' : '';
            return 'No good frame found. Better lighting, hold still, face clearly visible.' + processed;
        }
        if (r.error === 'FACE_ALREADY_ENROLLED') {
            var who  = r.matchEmployeeId ? ' matched with employee <b>' + escapeHtml(r.matchEmployeeId) + '</b>' : '';
            return 'Face already enrolled' + who + '. Contact administrator if this is an error.';
        }
        if (r.error === 'NETWORK_ERROR') return 'Network error. Check connection and try again.';
        return (r.error || 'Enrollment failed') + ' [step: ' + step + ']' + timeInfo;
    };

    Enrollment.prototype.enableDebug  = function () { this.config.debug = true;  };
    Enrollment.prototype.disableDebug = function () { this.config.debug = false; };
    Enrollment.prototype.getState     = function () {
        return {
            enrolled:        this.enrolled,
            enrolling:       this.enrolling,
            tickEnabled:     this._tickEnabled,
            tickRunning:     this._tickRunning,
            hasStream:       !!this.stream,
            goodFramesCount: this.goodFrames.length,
            passHistLength:  this.passHist.length,
            videoWidth:      this.elements.cam && this.elements.cam.videoWidth,
            videoHeight:     this.elements.cam && this.elements.cam.videoHeight
        };
    };

    Enrollment.prototype.getEncodings = function () {
        var result = [];
        for (var i = 0; i < this.goodFrames.length; i++) {
            var enc = this.goodFrames[i].encoding || this.goodFrames[i].enc || null;
            if (enc) result.push(enc);
        }
        return result;
    };

    // =========================================================================
    // Public API
    // =========================================================================

    return {
        create: function (config) { return new Enrollment(config); },
        CONSTANTS: CONSTANTS,
        utils: {
            getCsrfToken:      getCsrfToken,
            escapeHtml:        escapeHtml,
            debounce:          debounce,
            compressImageFile: compressImageFile,
            precheckImage:     precheckImage,
            isMobileDevice:    isMobileDevice
        }
    };
})();

window.FaceAttend = FaceAttend;
