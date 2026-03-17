/**
 * FaceAttend - Enrollment Core Module (Unified v3)
 * Consolidated enrollment logic using FaceAttend.Core modules
 * 
 * @version 3.0.0
 * @requires FaceAttend.Utils
 * @requires FaceAttend.Camera
 * @requires FaceAttend.API
 * @requires FaceAttend.Notify
 * 
 * PHASE 3 CHANGES (Unification):
 * - Uses FaceAttend.Utils for utilities (getCsrfToken, debounce, isMobileDevice)
 * - Uses FaceAttend.Camera for camera operations
 * - Uses FaceAttend.API for server communication (scanFrame, enroll)
 * - Uses FaceAttend.Notify for user feedback
 * - Preserves all Phase 2 features: sharpness filter, pose bucketing, diversity collection
 * 
 * Usage:
 *   var enrollment = FaceAttend.Enrollment.create(config);
 *   enrollment.startCamera().then(enrollment.startAutoEnrollment);
 */

var FaceAttend = window.FaceAttend || {};

FaceAttend.Enrollment = (function () {
    'use strict';

    // =========================================================================
    // CONSTANTS
    // =========================================================================
    var CONSTANTS = {
        // Capture timing
        AUTO_INTERVAL_MS: 300,
        PASS_WINDOW:      3,
        PASS_REQUIRED:    1,

        // Frame targets
        CAPTURE_TARGET:    8,
        MIN_GOOD_FRAMES:   3,
        MAX_KEEP_FRAMES:   8,
        // FIX-003: MAX_IMAGES was referenced in enrollFromFiles() as
        // CONSTANTS.MAX_IMAGES but was never defined, causing it to resolve
        // to undefined. Array.prototype.slice(files, 0, undefined) returned
        // all files accidentally. Now explicitly defined and equal to
        // CAPTURE_TARGET (8) so the behavior is consistent and intentional.
        MAX_IMAGES:        8,

        // Image capture
        // CAPTURE_WIDTH/HEIGHT now used for sharpness calculation canvas only
        // Actual capture uses native camera resolution (1280x720 ideal)
        CAPTURE_WIDTH:  640,
        CAPTURE_HEIGHT: 480,
        UPLOAD_QUALITY: 0.90,  // Higher quality for enrollment (was 0.80)

        // Quality thresholds
        SHARPNESS_THRESHOLD_DESKTOP: 80,
        SHARPNESS_THRESHOLD_MOBILE:  50,
        SHARPNESS_SAMPLE_SIZE:       160,

        // Angle buckets
        ANGLE_SEQUENCE: ['center', 'left', 'right', 'up', 'down'],

        // Auto-submit trigger
        AUTO_SUBMIT_ON_ALL_ANGLES: true,

        // FIX-002: Fallback confirmation timer.
        // If the user cannot complete all 5 angle buckets (poor lighting,
        // physical limitation, or slow movement), this timer fires the
        // Swal confirmation automatically once minGoodFrames is first reached.
        // Value is milliseconds. 30000 = 30 seconds.
        AUTO_CONFIRM_TIMEOUT_MS: 30000
    };

    // =========================================================================
    // UTILITY FUNCTIONS (delegated to FaceAttend.Utils)
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
        return FaceAttend.Utils ? FaceAttend.Utils.isMobile() : 
            /iPhone|iPad|iPod|Android.*Mobile|Windows Phone/i.test(navigator.userAgent);
    }

    // =========================================================================
    // IMAGE PROCESSING
    // =========================================================================

    function compressImageFile(file, maxWidth, maxHeight, quality) {
        return new Promise(function (resolve, reject) {
            var img = new Image();
            var url = URL.createObjectURL(file);

            img.onload = function () {
                URL.revokeObjectURL(url);

                var w = img.width;
                var h = img.height;

                if (w > maxWidth || h > maxHeight) {
                    var ratio = Math.min(maxWidth / w, maxHeight / h);
                    w = Math.floor(w * ratio);
                    h = Math.floor(h * ratio);
                }

                var canvas = document.createElement('canvas');
                canvas.width = w;
                canvas.height = h;
                var ctx = canvas.getContext('2d');
                ctx.drawImage(img, 0, 0, w, h);

                canvas.toBlob(function (blob) {
                    resolve(blob);
                }, 'image/jpeg', quality || CONSTANTS.UPLOAD_QUALITY);
            };

            img.onerror = function () {
                URL.revokeObjectURL(url);
                reject(new Error('Failed to load image'));
            };

            img.src = url;
        });
    }

    function precheckImage(file, options) {
        options = options || {};
        var maxSize = options.maxSize || 5 * 1024 * 1024;
        var minWidth = options.minWidth || 200;
        var minHeight = options.minHeight || 200;
        var maxDimension = options.maxDimension || 4096;

        return new Promise(function (resolve) {
            if (file.size > maxSize) {
                resolve({ ok: false, error: 'File too large (max ' + Math.round(maxSize / 1024 / 1024) + 'MB): ' + file.name });
                return;
            }

            if (!file.type.match(/^image\/(jpeg|jpg|png)$/i)) {
                resolve({ ok: false, error: 'Invalid file type (JPEG/PNG only): ' + file.name });
                return;
            }

            var img = new Image();
            var url = URL.createObjectURL(file);

            img.onload = function () {
                URL.revokeObjectURL(url);

                if (img.width < minWidth || img.height < minHeight) {
                    resolve({ ok: false, error: 'Image too small (min ' + minWidth + 'x' + minHeight + '): ' + file.name });
                    return;
                }
                if (img.width > maxDimension || img.height > maxDimension) {
                    resolve({ ok: false, error: 'Image too large (max ' + maxDimension + 'x' + maxDimension + '): ' + file.name });
                    return;
                }

                resolve({ ok: true, width: img.width, height: img.height });
            };

            img.onerror = function () {
                URL.revokeObjectURL(url);
                resolve({ ok: false, error: 'Cannot read image: ' + file.name });
            };

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

        // State
        this.stream = null;
        this.busy = false;
        this.enrolled = false;
        this.enrolling = false;
        this.multiFaceWarned = false;
        this.autoTimer = null;
        this.passHist = [];
        this.goodFrames = [];
        this.lastFaceBox = null;  // Phase 2: Store last face box for sharpness
        this.confirmTimer = null;    // FIX-002: 30-second fallback confirm timer handle
        this._scanController = null; // Tracks in-flight scan fetch — aborted by safety timeout

        // DOM elements
        this.elements = {};

        // Callbacks (Phase 2: added onAngleUpdate; FIX-002: added onReadyToConfirm)
        this.callbacks = {
            onStatus: null,
            onLivenessUpdate: null,
            onCaptureProgress: null,
            onEnrollmentComplete: null,
            onEnrollmentError: null,
            onMultiFaceWarning: null,
            onAngleUpdate: null,        // Phase 2: angle guidance callback
            onReadyToConfirm: null      // FIX-002: fires when auto-capture is complete
                                        // Payload: { frameCount, angleCount,
                                        //           bestLiveness, allAngles, frames }
                                        // enrollment-ui.js wires this to the Swal dialog.
        };

        this.captureCanvas = document.createElement('canvas');
    }

    // -------------------------------------------------------------------------
    // Phase 2: Sharpness Calculation
    // -------------------------------------------------------------------------

    /**
     * Computes Laplacian variance on face ROI, downscaled to 160×160.
     * Measures image sharpness - higher = sharper.
     */
    Enrollment.prototype.calculateSharpness = function(canvas, faceBox) {
        var W = CONSTANTS.SHARPNESS_SAMPLE_SIZE;
        var H = CONSTANTS.SHARPNESS_SAMPLE_SIZE;
        var tmp = document.createElement('canvas');
        tmp.width = W;
        tmp.height = H;
        var tCtx = tmp.getContext('2d');

        // Use face ROI if available, else center crop
        var sx, sy, sw, sh;
        if (faceBox && faceBox.w > 0 && faceBox.h > 0) {
            sx = faceBox.x;
            sy = faceBox.y;
            sw = faceBox.w;
            sh = faceBox.h;
        } else {
            sx = canvas.width * 0.2;
            sy = canvas.height * 0.1;
            sw = canvas.width * 0.6;
            sh = canvas.height * 0.8;
        }

        // Clamp to canvas bounds
        sx = Math.max(0, Math.min(sx, canvas.width - 1));
        sy = Math.max(0, Math.min(sy, canvas.height - 1));
        sw = Math.min(sw, canvas.width - sx);
        sh = Math.min(sh, canvas.height - sy);

        if (sw <= 0 || sh <= 0) return 0;

        tCtx.drawImage(canvas, sx, sy, sw, sh, 0, 0, W, H);
        var imgData = tCtx.getImageData(0, 0, W, H).data;

        // Convert to grayscale
        var gray = new Float32Array(W * H);
        for (var i = 0; i < imgData.length; i += 4) {
            gray[i >> 2] = 0.299 * imgData[i] + 0.587 * imgData[i+1] + 0.114 * imgData[i+2];
        }

        // Laplacian 3×3 kernel: [0,1,0,1,-4,1,0,1,0]
        var sum = 0, sumSq = 0, count = 0;
        for (var y = 1; y < H - 1; y++) {
            for (var x = 1; x < W - 1; x++) {
                var idx = y * W + x;
                var lap = gray[idx - W] + gray[idx - 1]
                        - 4 * gray[idx]
                        + gray[idx + 1] + gray[idx + W];
                sum += lap;
                sumSq += lap * lap;
                count++;
            }
        }
        if (count === 0) return 0;
        var mean = sum / count;
        return (sumSq / count) - (mean * mean); // Variance
    };

    // -------------------------------------------------------------------------
    // Phase 2: Pose Estimation
    // -------------------------------------------------------------------------

    /**
     * Estimates pose bucket from face bounding box.
     * Return values: center, left, right, up, down, other
     */
    Enrollment.prototype.estimatePoseBucket = function(landmarks, faceBox, canvasW, canvasH) {
        if (!faceBox || faceBox.w <= 0) return 'center';

        // Box-geometry fallback — used only when landmarks are null/empty.
        // Aspect ratio bias REMOVED: nearly all face boxes are taller than wide
        // (ratio ~1.3-1.6), so the old "if aspect > 1.4: pitch -= 10" fired on
        // every frame and permanently biased pitch toward "up".
        var faceCenterX = (faceBox.x + faceBox.w / 2) / (canvasW || 640);
        var faceCenterY = (faceBox.y + faceBox.h / 2) / (canvasH || 480);
        var yaw   = (faceCenterX - 0.5) * 60;
        var pitch = (faceCenterY - 0.5) * 40;

        if (landmarks && landmarks.length >= 3) {
            var lEye = landmarks[0], rEye = landmarks[1], nose = landmarks[2];
            var chin = landmarks.length >= 4 ? landmarks[3] : null;

            if (lEye && rEye && nose) {
                var eyeMidX  = (lEye.x + rEye.x) / 2;
                var eyeMidY  = (lEye.y + rEye.y) / 2;
                var eyeDistX = Math.abs(lEye.x - rEye.x);

                if (eyeDistX > 1) {
                    // YAW: nose offset from eye midpoint (self-scaling)
                    yaw = (nose.x - eyeMidX) / eyeDistX * 90;

                    // PITCH: choose formula based on whether chin is available
                    if (chin && chin.y > 0 && chin.y > nose.y) {
                        // SELF-CALIBRATING: uses chin point — no fixed baseline
                        // Matches server: FaceQualityAnalyzer.EstimatePoseFromLandmarks (8-float path)
                        var faceHeight = chin.y - eyeMidY;
                        if (faceHeight < 1) faceHeight = 1;
                        var noseFraction = (nose.y - eyeMidY) / faceHeight;
                        // noseFraction ~0.45 = frontal. UP = increases, DOWN = decreases
                        pitch = -(noseFraction - 0.45) * 130;
                    } else {
                        // FALLBACK: nose-to-eye ratio with corrected 1.05 baseline
                        // (was 1.2 which biased every frontal frame to "up")
                        var normalizedPitch = (nose.y - eyeMidY) / eyeDistX;
                        pitch = -(normalizedPitch - 1.05) * 50;
                    }
                }
            }
        }

        var absYaw   = Math.abs(yaw);
        var absPitch = Math.abs(pitch);

        if (absYaw > 35 || absPitch > 30) return 'other';
        if (absYaw < 12 && absPitch < 12) return 'center';

        if (absYaw >= absPitch) {
            if (yaw < -12) return 'left';
            if (yaw > 12)  return 'right';
        } else {
            if (pitch < -12) return 'up';
            if (pitch > 12)  return 'down';
        }

        return 'center';
    };

    /**
     * Phase 2: Returns the next angle to prompt for based on captured buckets
     */
    Enrollment.prototype.getNextAnglePrompt = function() {
        var captured = {};
        for (var i = 0; i < this.goodFrames.length; i++) {
            var b = this.goodFrames[i].poseBucket;
            if (b) captured[b] = true;
        }

        var prompts = {
            center: { prompt: 'Look straight at the camera',   icon: 'fa-circle-dot' },
            left:   { prompt: 'Turn your head slightly LEFT',  icon: 'fa-arrow-left' },
            right:  { prompt: 'Turn your head slightly RIGHT', icon: 'fa-arrow-right' },
            up:     { prompt: 'Tilt your head slightly UP',    icon: 'fa-arrow-up' },
            down:   { prompt: 'Tilt your head slightly DOWN',  icon: 'fa-arrow-down' }
        };

        for (var j = 0; j < CONSTANTS.ANGLE_SEQUENCE.length; j++) {
            var bucket = CONSTANTS.ANGLE_SEQUENCE[j];
            if (!captured[bucket]) {
                return { bucket: bucket, prompt: prompts[bucket].prompt, icon: prompts[bucket].icon };
            }
        }

        return { bucket: 'center', prompt: 'Hold still - capturing final frames', icon: 'fa-check' };
    };

    // -------------------------------------------------------------------------
    // Camera Operations
    // -------------------------------------------------------------------------

    // -------------------------------------------------------------------------
    // Camera Operations (using FaceAttend.Camera)
    // -------------------------------------------------------------------------
    
    Enrollment.prototype.startCamera = function (videoElement) {
        var self = this;
        this.elements.cam = videoElement || this.elements.cam;

        // Use FaceAttend.Camera if available
        // Request 1280x720 for sharp enrollment images
        var videoConstraints = {
            facingMode: 'user',
            width: { ideal: 1280 },
            height: { ideal: 720 }
        };

        if (FaceAttend.Camera) {
            return new Promise(function (resolve, reject) {
                FaceAttend.Camera.start(
                    self.elements.cam,
                    videoConstraints,
                    function (stream) {
                        self.stream = stream;
                        resolve(stream);
                    },
                    function (err) {
                        reject(err);
                    }
                );
            });
        }

        // Fallback to native getUserMedia
        return new Promise(function (resolve, reject) {
            if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
                reject(new Error('Camera API not available'));
                return;
            }

            navigator.mediaDevices.getUserMedia({
                video: videoConstraints,
                audio: false
            }).then(function (stream) {
                self.stream = stream;
                if (self.elements.cam) {
                    self.elements.cam.srcObject = stream;
                    self.elements.cam.play().catch(function () { });
                }
                resolve(stream);
            }).catch(reject);
        });
    };

    Enrollment.prototype.stopCamera = function () {
        // FIX-002: Clear the 30-second fallback timer on full camera stop.
        // This prevents the Swal from firing after the user has navigated
        // away from the enrollment page or closed the camera pane.
        this._clearConfirmTimer();
        this.stopAutoEnrollment();

        // Use FaceAttend.Camera if we used it
        if (FaceAttend.Camera) {
            FaceAttend.Camera.stop();
        } else if (this.stream) {
            try {
                this.stream.getTracks().forEach(function (t) { t.stop(); });
            } catch (e) { }
        }

        this.stream = null;
        this.enrolled = false;
        this.enrolling = false;
        this.passHist = [];
        this.goodFrames = [];
        this.lastFaceBox = null;

        if (this.elements.cam) {
            this.elements.cam.srcObject = null;
        }
    };

    Enrollment.prototype.captureJpegBlob = function (quality) {
        var self = this;
        var cam = this.elements.cam;
        var canvas = this.captureCanvas;

        if (!cam || !cam.videoWidth) {
            return Promise.reject(new Error('Camera not ready'));
        }

        // Use native video resolution for sharp capture (1280x720 or camera native)
        var w = cam.videoWidth || CONSTANTS.CAPTURE_WIDTH;
        var h = cam.videoHeight || CONSTANTS.CAPTURE_HEIGHT;

        canvas.width = w;
        canvas.height = h;

        var ctx = canvas.getContext('2d');
        ctx.clearRect(0, 0, w, h);
        ctx.drawImage(cam, 0, 0, w, h);

        return new Promise(function (resolve) {
            canvas.toBlob(function (b) { resolve(b); }, 'image/jpeg', quality || CONSTANTS.UPLOAD_QUALITY);
        });
    };

    // -------------------------------------------------------------------------
    // Server Communication
    // -------------------------------------------------------------------------

    Enrollment.prototype.normalizeSuccessResult = function (result) {
        if (result && result.ok === true && result.data && typeof result.data === 'object') {
            var merged = { ok: true, message: result.message || null };
            for (var k in result.data) {
                if (Object.prototype.hasOwnProperty.call(result.data, k)) {
                    merged[k] = result.data[k];
                }
            }
            return merged;
        }
        return result;
    };

    // -------------------------------------------------------------------------
    // API Operations (using FaceAttend.API with Promise wrappers)
    // -------------------------------------------------------------------------
    
    Enrollment.prototype._abortCurrentScan = function () {
        if (this._scanController) {
            try { this._scanController.abort(); } catch (e) {}
            this._scanController = null;
        }
    };

    Enrollment.prototype.postScanFrame = function (blob) {
        var self = this;

        // Abort any previous in-flight scan before starting a new one.
        // This prevents request pile-up when the server is slow or the safety
        // timeout fires before the previous response returns.
        this._abortCurrentScan();

        var controller = new AbortController();
        this._scanController = controller;

        var fd = new FormData();
        fd.append('__RequestVerificationToken', getCsrfToken());
        fd.append('image', blob, 'frame.jpg');

        // Send face bbox so server can skip detection (OPT-SPEED-02)
        if (this.lastFaceBox) {
            fd.append('faceX', this.lastFaceBox.x);
            fd.append('faceY', this.lastFaceBox.y);
            fd.append('faceW', this.lastFaceBox.w);
            fd.append('faceH', this.lastFaceBox.h);
        }

        var scanUrl = this.config.scanUrl || '/api/scan/frame';

        return fetch(scanUrl, {
            method: 'POST',
            body: fd,
            credentials: 'same-origin',
            signal: controller.signal
        })
        .then(function (r) {
            if (!r.ok) throw new Error('HTTP ' + r.status);
            return r.json();
        })
        .then(function (data) {
            return self.normalizeSuccessResult ? self.normalizeSuccessResult(data) : data;
        })
        .catch(function (e) {
            if (e && e.name === 'AbortError') return null; // deliberately aborted — suppress silently
            throw e;
        })
        .finally(function () {
            if (self._scanController === controller) self._scanController = null;
        });
    };

    Enrollment.prototype.postEnrollMany = function (blobs) {
        var self = this;

        if (!blobs || !blobs.length) {
            return Promise.resolve({ ok: false, error: 'NO_IMAGE' });
        }

        var startTime = Date.now();
        
        // Use FaceAttend.API if available
        if (FaceAttend.API && FaceAttend.API.enroll) {
            return new Promise(function (resolve, reject) {
                FaceAttend.API.enroll(self.config.empId, blobs, {},
                    function (result) {
                        result = self.normalizeSuccessResult(result) || result;
                        if (result) {
                            result.timeMs = Date.now() - startTime;
                        }
                        resolve(result);
                    },
                    function (error) {
                        reject(error);
                    }
                );
            });
        }

        // Fallback to raw fetch
        var fd = new FormData();
        fd.append('__RequestVerificationToken', getCsrfToken());
        fd.append('employeeId', this.config.empId);

        for (var i = 0; i < blobs.length; i++) {
            fd.append('image', blobs[i], 'enroll_' + (i + 1) + '.jpg');
        }

        return fetch(this.config.enrollUrl, { method: 'POST', body: fd })
            .then(function (res) { return res.json(); })
            .then(function (result) {
                result = self.normalizeSuccessResult(result) || result;
                if (result) {
                    result.timeMs = Date.now() - startTime;
                }
                return result;
            })
            .catch(function (e) {
                return { ok: false, error: 'NETWORK_ERROR', message: e.message };
            });
    };

    // -------------------------------------------------------------------------
    // Auto Enrollment Logic (Phase 2 Updated)
    // -------------------------------------------------------------------------

    Enrollment.prototype.startAutoEnrollment = function () {
        // FIX-002: Clear any pending 30-second fallback timer from a previous
        // capture session before starting fresh. Without this, if the user
        // does a retake, the old timer from the previous session would still
        // fire and show a stale Swal over the fresh capture in progress.
        this._clearConfirmTimer();
        this.stopAutoEnrollment();
        this.enrolled = false;
        this.passHist = [];
        this.goodFrames = [];
        this.lastFaceBox = null;
        this.autoTimer = setInterval(this.autoTick.bind(this), CONSTANTS.AUTO_INTERVAL_MS);
    };

    Enrollment.prototype.stopAutoEnrollment = function () {
        if (this.autoTimer) {
            clearInterval(this.autoTimer);
        }
        this.autoTimer = null;
        this.enrolling = false;
    };

    // -------------------------------------------------------------------------
    // FIX-002: Confirm Timer and Ready-To-Confirm Logic
    // -------------------------------------------------------------------------

    /**
     * Prepares the confirmation payload and fires the onReadyToConfirm callback.
     *
     * Called by two code paths:
     *   1. processScanResult  immediate trigger when all angles captured or
     *      max frames hit
     *   2. _startConfirmTimer setTimeout  30-second fallback when user cannot
     *      complete all 5 angles
     *
     * Payload sent to callback:
     *   frameCount   {number}   total good frames collected this session
     *   angleCount   {number}   distinct pose buckets captured (max 5)
     *   bestLiveness {number}   highest liveness score as integer 0-100
     *   allAngles    {boolean}  true if all 5 ANGLE_SEQUENCE buckets captured
     *   frames       {Array}    shallow copy of goodFrames array (sorted by
     *                            probability descending, so [0] is best frame)
     *                            Each frame: { blob, encoding, p, poseBucket,
     *                            sharpness }
     *
     * enrollment-ui.js reads `frames[0..2].blob` to generate thumbnail previews,
     * `frameCount` and `angleCount` for the summary row, and `bestLiveness`
     * for the liveness display.
     */
    Enrollment.prototype._fireReadyToConfirm = function () {
        // Always clear the timer first  whether it was running or not.
        // _clearConfirmTimer is safe to call when confirmTimer is null.
        this._clearConfirmTimer();

        // Guard: if no callback is wired (e.g. on a page that does not use
        // enrollment-ui.js), do nothing. performEnrollment() will never be
        // called in this case, which is intentional  something must listen.
        if (!this.callbacks.onReadyToConfirm) return;

        // Build the set of distinct captured buckets from goodFrames.
        // poseBucket values match ANGLE_SEQUENCE: 'center','left','right','up','down'.
        // Frames with poseBucket === 'other' are discarded frames  never counted.
        var capturedBuckets = {};
        for (var i = 0; i < this.goodFrames.length; i++) {
            var b = this.goodFrames[i].poseBucket;
            if (b && b !== 'other') capturedBuckets[b] = true;
        }

        // Find the highest liveness probability across all good frames.
        // goodFrames is sorted descending by p so goodFrames[0].p is the best,
        // but we iterate to be safe against any future sort changes.
        var bestLiveness = 0;
        for (var j = 0; j < this.goodFrames.length; j++) {
            if (this.goodFrames[j].p > bestLiveness) {
                bestLiveness = this.goodFrames[j].p;
            }
        }

        this.callbacks.onReadyToConfirm({
            frameCount:   this.goodFrames.length,
            angleCount:   Object.keys(capturedBuckets).length,
            bestLiveness: Math.round(bestLiveness * 100),  // convert 0-1 float  0-100 integer
            allAngles:    Object.keys(capturedBuckets).length >= CONSTANTS.ANGLE_SEQUENCE.length,
            frames:       this.goodFrames.slice()  // shallow copy  caller must not mutate
        });
    };

    /**
     * Starts the 30-second fallback confirmation timer.
     *
     * Called from processScanResult the FIRST TIME goodFrames.length reaches
     * minGoodFrames. If the user has not yet captured all 5 angles after 30
     * seconds, we still fire _fireReadyToConfirm so they are not stuck forever.
     *
     * Guards inside the setTimeout callback ensure this is a no-op if:
     *   - enrollment is already in progress (enrolling === true)
     *   - enrollment was already completed (enrolled === true)
     *   - somehow goodFrames dropped below minGoodFrames (defensive)
     *
     * The timer handle is stored on this.confirmTimer so it can be cancelled
     * by _clearConfirmTimer during retake or camera stop.
     */
    Enrollment.prototype._startConfirmTimer = function () {
        var self = this;
        this.confirmTimer = setTimeout(function () {
            // These guards prevent the Swal from firing in edge cases where
            // the state has changed since the timer was started.
            if (self.enrolled || self.enrolling) return;
            if (self.goodFrames.length < self.config.minGoodFrames) return;

            self.stopAutoEnrollment();   // stop the capture interval
            self._fireReadyToConfirm(); // show the Swal
        }, CONSTANTS.AUTO_CONFIRM_TIMEOUT_MS);
    };

    /**
     * Cancels the 30-second fallback timer.
     * Safe to call when this.confirmTimer is null  does nothing in that case.
     *
     * Called from:
     *   - _fireReadyToConfirm (before firing callback, to prevent double-fire)
     *   - startAutoEnrollment (fresh retake session)
     *   - stopCamera (full teardown)
     */
    Enrollment.prototype._clearConfirmTimer = function () {
        if (this.confirmTimer) {
            clearTimeout(this.confirmTimer);
            this.confirmTimer = null;
        }
    };

    Enrollment.prototype.pushGoodFrame = function (blob, probability, encoding, poseBucket, sharpness) {
        if (!blob) return;

        this.goodFrames.push({
            blob: blob,
            encoding: encoding || null,
            p: typeof probability === 'number' ? probability : 0,
            poseBucket: poseBucket || 'center',
            sharpness: typeof sharpness === 'number' ? sharpness : 0
        });

        this.goodFrames.sort(function (a, b) { return b.p - a.p; });

        if (this.goodFrames.length > this.config.maxKeepFrames) {
            this.goodFrames.length = this.config.maxKeepFrames;
        }

        if (this.callbacks.onCaptureProgress) {
            this.callbacks.onCaptureProgress(this.goodFrames.length, this.config.minGoodFrames);
        }
    };

    Enrollment.prototype.getGoodFrameBlobs = function () {
        return this.goodFrames.map(function (x) { return x.blob; });
    };

    Enrollment.prototype.getBestEncoding = function () {
        return this.goodFrames.length ? (this.goodFrames[0].encoding || null) : null;
    };

    /**
     * Phase 2: Updated autoTick with sharpness pre-filter
     */
    Enrollment.prototype.autoTick = function () {
        var self = this;

        // DEBUG: Log why autoTick might return early (call this from browser console to enable)
        if (this._debugAutoTick) {
            console.log('[Enrollment] autoTick called:', {
                enrolled: this.enrolled,
                hasStream: !!this.stream,
                busy: this.busy,
                hasCam: !!this.elements.cam,
                videoWidth: this.elements.cam && this.elements.cam.videoWidth,
                autoTimer: this.autoTimer,
                time: new Date().toISOString()
            });
        }

        if (this.enrolled || !this.stream || this.busy) {
            if (this._debugAutoTick) {
                console.log('[Enrollment] autoTick returning early:', {
                    reason: this.enrolled ? 'enrolled' : !this.stream ? 'no stream' : 'busy',
                    enrolled: this.enrolled,
                    hasStream: !!this.stream,
                    busy: this.busy
                });
            }
            return;
        }

        var cam = this.elements.cam;
        if (!cam || !cam.videoWidth) {
            if (this._debugAutoTick) {
                console.log('[Enrollment] autoTick returning - no cam or videoWidth');
            }
            return;
        }

        this.busy = true;

        // SAFETY: If the server takes too long, abort the request and reset busy.
        // Increased to 10s — long enough for normal server response (2-4s),
        // short enough to recover from genuine hangs without infinite pending pile-up.
        var safetyTimeout = setTimeout(function() {
            if (self.busy) {
                self._abortCurrentScan(); // cancel the stale request first
                self.busy = false;
            }
        }, 10000);

        var capturedBlob = null;

        // Phase 2: Quick client-side sharpness check BEFORE upload
        var sharpnessOk = true;
        var sharpness = 0;
        if (cam.videoWidth > 0) {
            var tmpCanvas = document.createElement('canvas');
            var videoW = cam.videoWidth;
            var videoH = cam.videoHeight;
            // DO NOT pre-scale this canvas. calculateSharpness() handles its own
            // 160×160 downscaling internally. Pre-scaling here breaks coordinate
            // alignment — this.lastFaceBox is in original video coordinates, and
            // clamping it against a 160×120 canvas produces sw/sh ≈ 0 → score 0 → always blurry.
            tmpCanvas.width = videoW;
            tmpCanvas.height = videoH;
            var tmpCtx = tmpCanvas.getContext('2d');
            tmpCtx.drawImage(cam, 0, 0, videoW, videoH);

            // Adaptive threshold: scale down for lower resolution cameras
            // Base threshold calibrated for 1280x720; scale proportionally
            var baseThreshold = isMobileDevice()
                ? CONSTANTS.SHARPNESS_THRESHOLD_MOBILE
                : CONSTANTS.SHARPNESS_THRESHOLD_DESKTOP;
            var resolutionScale = Math.min(1, Math.max(videoW, videoH) / 720);
            var threshold = baseThreshold * resolutionScale;

            sharpness = this.calculateSharpness(tmpCanvas, this.lastFaceBox);

            if (sharpness < threshold) {
                sharpnessOk = false;
                this.busy = false;
                if (this._debugAutoTick) {
                    console.log('[Enrollment] Sharpness check failed:', {
                        sharpness: sharpness,
                        threshold: threshold,
                        videoW: videoW,
                        videoH: videoH
                    });
                }
                if (this.callbacks.onStatus) {
                    this.callbacks.onStatus(
                        'Image blurry (score: ' + Math.round(sharpness) +
                        '). Move closer or improve lighting.', 'warning');
                }
                return;
            }
        }

        this.captureJpegBlob(CONSTANTS.UPLOAD_QUALITY)
            .then(function (blob) {
                capturedBlob = blob;
                return self.postScanFrame(blob);
            })
            .then(function (result) {
                if (result) {
                    result.lastBlob = capturedBlob;
                    result.clientSharpness = sharpness; // Pass sharpness to processor
                }
                self.processScanResult(result);
            })
            .catch(function (e) {
                // AbortError = deliberately cancelled by safety timeout — silent, do not show error
                if (e && e.name === 'AbortError') return;
                // Transient network/server error — show brief status but do NOT reset goodFrames
                // so the user doesn't lose captured progress on a single bad frame
                self.handleStatus('Scan error, retrying...', 'warning');
                self.passHist = [];
            })
            .finally(function () {
                clearTimeout(safetyTimeout);
                self.busy = false;
            });
    };

    /**
     * Phase 2: Updated processScanResult with poseBucket and auto-submit
     */
    Enrollment.prototype.processScanResult = function (r) {
        if (!r || r.ok !== true) {
            var errorCode = r && r.error;
            if (this._debugAutoTick) {
                console.log('[Enrollment] processScanResult error:', {
                    errorCode: errorCode,
                    message: r && r.message,
                    liveness: r && r.liveness,
                    livenessOk: r && r.livenessOk
                });
            }
            if (errorCode === 'NO_FACE' || errorCode === 'NO_IMAGE') {
                this.handleStatus('No face detected.', 'warning');
                if (this.callbacks.onLivenessUpdate) {
                    this.callbacks.onLivenessUpdate(0, 'fail');
                }
            } else if (errorCode === 'LIVENESS_FAIL') {
                this.handleStatus('Liveness check failed. Please ensure good lighting.', 'warning');
                if (this.callbacks.onLivenessUpdate) {
                    this.callbacks.onLivenessUpdate(25, 'fail');
                }
            } else if (errorCode === 'ENCODING_FAIL') {
                this.handleStatus('Could not encode face. Please try again.', 'warning');
                if (this.callbacks.onLivenessUpdate) {
                    this.callbacks.onLivenessUpdate(0, 'fail');
                }
            } else if (errorCode === 'SCAN_ERROR') {
                // Silent retry for transient server errors — don't reset goodFrames,
                // don't show Swal, just log and continue scanning
                this.handleStatus('Scan error, retrying...', 'warning');
                // Keep passHist — this is a transient error, not a fundamental failure
                return;
            } else {
                // Other errors — show in status bar but don't interrupt scanning
                this.handleStatus((r && r.message) ? r.message : (errorCode || 'Scan error'), 'warning');
                if (this.callbacks.onLivenessUpdate) {
                    this.callbacks.onLivenessUpdate(0, 'fail');
                }
            }
            this.passHist = [];
            return;
        }

        if (r.count === 0) {
            this.handleStatus('No face detected.', 'warning');
            this.passHist = [];
            if (this.callbacks.onLivenessUpdate) {
                this.callbacks.onLivenessUpdate(0, 'fail');
            }
            return;
        }

        // Store face box for next sharpness calculation
        if (r.faceBox) {
            this.lastFaceBox = r.faceBox;
        }

        // Multi-face handling
        if (r.count > 1) {
            if (!this.multiFaceWarned) {
                this.multiFaceWarned = true;
                if (this.callbacks.onMultiFaceWarning) {
                    this.callbacks.onMultiFaceWarning(r.count);
                }
            }
        } else if (this.multiFaceWarned) {
            this.multiFaceWarned = false;
            if (this.callbacks.onMultiFaceWarning) {
                this.callbacks.onMultiFaceWarning(1);
            }
        }

        var p = typeof r.liveness === 'number' ? r.liveness : 0;
        var pass = (r.livenessOk === true) && (p >= this.config.perFrameThreshold);

        this.passHist.push(pass ? 1 : 0);
        if (this.passHist.length > CONSTANTS.PASS_WINDOW) {
            this.passHist.shift();
        }

        var sum = this.passHist.reduce(function (a, b) { return a + b; }, 0);

        if (this.callbacks.onLivenessUpdate) {
            var pct = Math.round(p * 100);
            var kind = pass ? 'pass' : 'fail';
            this.callbacks.onLivenessUpdate(pct, kind);
        }

        if (pass) {
            // Phase 2: Calculate pose bucket and store with frame
            // Update lastFaceBox from server response so the NEXT scan can skip
            // HOG detection by sending this bbox as faceX/Y/W/H params.
            // Without this, lastFaceBox is always null → HOG runs every frame → +200-500ms each.
            if (r.faceBox && r.faceBox.w > 0) {
                this.lastFaceBox = r.faceBox;
            }

            // Pass server landmarks and actual canvas dimensions.
            // r.landmarks = [{x,y},{x,y},{x,y}] (leftEye, rightEye, noseTip) in pixel coords.
            var cam = this.elements.cam;
            var canvasW = (cam && cam.videoWidth)  || CONSTANTS.CAPTURE_WIDTH;
            var canvasH = (cam && cam.videoHeight) || CONSTANTS.CAPTURE_HEIGHT;
            var poseBucket = this.estimatePoseBucket(r.landmarks || null, r.faceBox, canvasW, canvasH);
            var sharpness = r.sharpness || r.clientSharpness || 0;
            
            this.pushGoodFrame(r.lastBlob || null, p, r.encoding || null, poseBucket, sharpness);

            // Phase 2: Notify UI of next angle needed
            if (this.callbacks.onAngleUpdate) {
                this.callbacks.onAngleUpdate(this.getNextAnglePrompt());
            }

            // Phase 2: Check auto-submit conditions
            var capturedBuckets = {};
            for (var m = 0; m < this.goodFrames.length; m++) {
                var b = this.goodFrames[m].poseBucket;
                if (b && b !== 'other') capturedBuckets[b] = true;
            }
            var allAngles = CONSTANTS.ANGLE_SEQUENCE.every(function(bucket) {
                return capturedBuckets[bucket];
            });

            var hasEnoughFrames = this.goodFrames.length >= this.config.minGoodFrames;
            var hasMaxFrames = this.goodFrames.length >= CONSTANTS.CAPTURE_TARGET;

            // FIX-002: Start the 30-second fallback timer the FIRST TIME
            // minGoodFrames is reached. The timer is started here (not in
            // pushGoodFrame) so we have direct access to the enrollment/enrolling
            // guard flags. The !this.confirmTimer check ensures it only starts
            // once per capture session  subsequent frames hitting this block
            // do not reset the timer.
            if (hasEnoughFrames && !this.confirmTimer && !this.enrolled && !this.enrolling) {
                this._startConfirmTimer();
            }

            // FIX-002: When all 5 angles are captured (or max frames hit),
            // STOP the capture interval and fire the onReadyToConfirm callback
            // instead of calling performEnrollment() directly.
            //
            // IMPORTANT: The camera stream is NOT stopped here  only the
            // auto-tick interval is stopped. The video element keeps showing
            // the live feed while the Swal dialog is visible.
            //
            // performEnrollment() is now called by enrollment-ui.js AFTER
            // the user clicks "Confirm" in the Swal dialog.
            if ((allAngles && hasEnoughFrames && CONSTANTS.AUTO_SUBMIT_ON_ALL_ANGLES) || hasMaxFrames) {
                this.stopAutoEnrollment();   // stops setInterval; camera stream stays alive
                this._fireReadyToConfirm();  // clears timer, fires onReadyToConfirm callback
                return;
            }

            var needPass = Math.max(0, CONSTANTS.PASS_REQUIRED - sum);
            var needFrames = Math.max(0, this.config.minGoodFrames - this.goodFrames.length);

            var msg = 'Face OK. Liveness: ' + p.toFixed(2) +
                ', Frames: ' + this.goodFrames.length + '/' + this.config.minGoodFrames +
                ', Ready in ' + needFrames + ' more.';
            this.handleStatus(msg, 'success');
        } else {
            var failMsg = 'Face detected. Liveness: ' + p.toFixed(2) + ' (need ' + this.config.perFrameThreshold + '). Hold still, improve lighting.';
            this.handleStatus(failMsg, 'warning');
            this.passHist = [];
        }
    };

    Enrollment.prototype.performEnrollment = function () {
        var self = this;
        
        if (this.enrolling) {
            return;
        }
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
                        ? result.savedVectors
                        : self.goodFrames.length;

                    if (self.callbacks.onEnrollmentComplete) {
                        self.callbacks.onEnrollmentComplete(vecCount, result);
                    }
                } else {
                    self.handleEnrollmentError(result);
                }
            })
            .catch(function (e) {
                self.enrolling = false;
                self.handleError('Enrollment failed: ' + (e && e.message ? e.message : e));
                self.passHist = [];
                self.goodFrames = [];
            });
    };

    // -------------------------------------------------------------------------
    // Upload Enrollment
    // -------------------------------------------------------------------------

    Enrollment.prototype.enrollFromFiles = function (files, options) {
        var self = this;
        options = options || {};

        if (this.busy) {
            return Promise.reject(new Error('Already processing'));
        }

        if (!files || !files.length) {
            return Promise.reject(new Error('No files selected'));
        }

        this.busy = true;

        var maxImages = options.maxImages || CONSTANTS.MAX_IMAGES;
        var imgs = Array.prototype.slice.call(files, 0, maxImages);

        return Promise.all(imgs.map(function (f) {
            return precheckImage(f, options.precheck);
        })).then(function (checks) {
            for (var i = 0; i < checks.length; i++) {
                if (!checks[i].ok) {
                    throw new Error(checks[i].error);
                }
            }

            if (self.config.enablePreview && options.showPreview) {
                return options.showPreview(imgs).then(function (confirmed) {
                    if (!confirmed) {
                        throw new Error('CANCELLED');
                    }
                    return imgs;
                });
            }

            return imgs;
        }).then(function (imgs) {
            self.handleStatus('Uploading...', 'info');

            var fd = new FormData();
            fd.append('__RequestVerificationToken', getCsrfToken());
            fd.append('employeeId', self.config.empId);

            for (var i = 0; i < imgs.length; i++) {
                fd.append('image', imgs[i], imgs[i].name || ('upload_' + (i + 1) + '.jpg'));
            }

            return fetch(self.config.enrollUrl, { method: 'POST', body: fd })
                .then(function (res) { return res.json(); });
        }).then(function (result) {
            self.busy = false;

            if (result && result.ok === true) {
                var vecCount = typeof result.savedVectors === 'number'
                    ? result.savedVectors
                    : imgs.length;

                if (self.callbacks.onEnrollmentComplete) {
                    self.callbacks.onEnrollmentComplete(vecCount, result);
                }

                return result;
            } else {
                self.handleEnrollmentError(result);
                throw new Error(result.error || 'Enrollment failed');
            }
        }).catch(function (e) {
            self.busy = false;
            if (e.message !== 'CANCELLED') {
                self.handleError('Upload failed: ' + (e && e.message ? e.message : e));
            }
            throw e;
        });
    };

    // -------------------------------------------------------------------------
    // Error Handling
    // -------------------------------------------------------------------------

    Enrollment.prototype.handleStatus = function (message, kind) {
        if (this.callbacks.onStatus) {
            this.callbacks.onStatus(message, kind);
        }
    };

    Enrollment.prototype.handleError = function (message) {
        this.handleStatus(message, 'danger');
        if (this.callbacks.onEnrollmentError) {
            this.callbacks.onEnrollmentError({ error: message });
        }
    };

    Enrollment.prototype.handleEnrollmentError = function (result) {
        var errorText = this.describeEnrollError(result);
        this.handleStatus(errorText, 'danger');

        if (this.callbacks.onEnrollmentError) {
            this.callbacks.onEnrollmentError(result);
        }

        if (result && result.error !== 'FACE_ALREADY_ENROLLED') {
            this.passHist = [];
            this.goodFrames = [];
        }
    };

    Enrollment.prototype.describeEnrollError = function (r) {
        if (!r) return 'Enrollment failed (no response)';

        var step = r.step || '';
        var timeInfo = typeof r.timeMs === 'number'
            ? ' (took ' + r.timeMs + 'ms)'
            : '';

        if (r.error === 'NO_EMPLOYEE_ID') {
            return 'Please enter an employee ID.';
        }
        if (r.error === 'EMPLOYEE_ID_TOO_LONG') {
            return 'Employee ID is too long (max 20 characters).';
        }
        if (r.error === 'NO_IMAGE') {
            return 'Please select or capture at least one image.';
        }
        if (r.error === 'TOO_LARGE') {
            return 'Image file is too large (max 10MB per file).';
        }
        if (r.error === 'EMPLOYEE_NOT_FOUND') {
            return 'Employee not found in database. Please check the employee ID.';
        }
        if (r.error === 'NO_GOOD_FRAME') {
            var processed = typeof r.processed === 'number'
                ? ' (processed ' + r.processed + ' images)'
                : '';
            return 'No good frame found. Please try better lighting, hold still, and ensure your face is clearly visible.' + processed;
        }
        if (r.error === 'FACE_ALREADY_ENROLLED') {
            var who = r.matchEmployeeId
                ? ' matched with employee <b>' + escapeHtml(r.matchEmployeeId) + '</b>'
                : '';
            var dist = typeof r.distance === 'number'
                ? ', distance: <b>' + r.distance.toFixed(4) + '</b>'
                : '';
            var hits = typeof r.matchCount === 'number' && typeof r.hitsRequired === 'number'
                ? ', matched frames: <b>' + r.matchCount + '/' + r.hitsRequired + '</b>'
                : '';
            return 'Face already enrolled' + who + dist + hits + '. Please contact administrator if this is an error.';
        }
        if (r.error === 'NETWORK_ERROR') {
            return 'Network error. Please check your connection and try again.';
        }

        return (r.error || 'Enrollment failed') + ' [step: ' + step + ']' + timeInfo;
    };

    // -------------------------------------------------------------------------
    // Debug utilities
    // -------------------------------------------------------------------------
    
    Enrollment.prototype.enableDebug = function() {
        this._debugAutoTick = true;
        console.log('[Enrollment] Debug enabled. autoTick logging is now active.');
    };
    
    Enrollment.prototype.disableDebug = function() {
        this._debugAutoTick = false;
        console.log('[Enrollment] Debug disabled.');
    };
    
    Enrollment.prototype.getState = function() {
        return {
            enrolled: this.enrolled,
            enrolling: this.enrolling,
            busy: this.busy,
            hasStream: !!this.stream,
            hasAutoTimer: !!this.autoTimer,
            goodFramesCount: this.goodFrames.length,
            passHistLength: this.passHist.length,
            videoWidth: this.elements.cam && this.elements.cam.videoWidth,
            videoHeight: this.elements.cam && this.elements.cam.videoHeight
        };
    };

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    return {
        create: function (config) {
            return new Enrollment(config);
        },
        CONSTANTS: CONSTANTS,
        utils: {
            getCsrfToken: getCsrfToken,
            escapeHtml: escapeHtml,
            debounce: debounce,
            compressImageFile: compressImageFile,
            precheckImage: precheckImage,
            isMobileDevice: isMobileDevice  // Phase 2: exposed
        }
    };
})();

window.FaceAttend = FaceAttend;
