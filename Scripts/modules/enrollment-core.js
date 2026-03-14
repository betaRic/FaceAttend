/**
 * FaceAttend - Enrollment Core Module (Phase 2 - Diversity-Aware)
 * Consolidated enrollment logic shared between admin and mobile enrollment flows
 * 
 * PHASE 2 CHANGES:
 * - Sharpness pre-filter (client-side Laplacian)
 * - Pose bucketing (center/left/right/up/down)
 * - Angle guidance prompts
 * - Diversity-aware frame collection
 * - Auto-submit on all angles captured
 * 
 * Usage:
 *   var enrollment = FaceAttend.Enrollment.create(config);
 *   enrollment.startCamera().then(enrollment.startAutoEnrollment);
 */

var FaceAttend = window.FaceAttend || {};

FaceAttend.Enrollment = (function () {
    'use strict';

    // =========================================================================
    // CONSTANTS (Phase 2 Updated)
    // =========================================================================
    var CONSTANTS = {
        // Capture timing
        AUTO_INTERVAL_MS: 300,        // Was 250. 300ms gives pose-change time between frames.
        PASS_WINDOW:      3,
        PASS_REQUIRED:    1,

        // Frame targets
        CAPTURE_TARGET:    8,         // Collect up to 8 frames before submitting
        MIN_GOOD_FRAMES:   3,         // Minimum to allow manual submit
        MAX_KEEP_FRAMES:   8,         // Keep best 8 in goodFrames array

        // Image capture
        CAPTURE_WIDTH:  640,          // Was 480. Larger = better dlib encoding quality.
        CAPTURE_HEIGHT: 480,          // Was 360.
        UPLOAD_QUALITY: 0.80,         // Was 0.65. Higher quality for diverse-angle enrollment.

        // Quality thresholds (matched by server)
        SHARPNESS_THRESHOLD_DESKTOP: 80,
        SHARPNESS_THRESHOLD_MOBILE:  50,
        SHARPNESS_SAMPLE_SIZE:       160,  // Resize ROI to 160×160 before Laplacian

        // Angle buckets
        ANGLE_SEQUENCE: ['center', 'left', 'right', 'up', 'down'],

        // Auto-submit trigger
        AUTO_SUBMIT_ON_ALL_ANGLES: true  // Submit early if all 5 angle buckets captured
    };

    // =========================================================================
    // UTILITY FUNCTIONS
    // =========================================================================

    function getCsrfToken() {
        var token = document.querySelector('input[name="__RequestVerificationToken"]');
        return token ? token.value : '';
    }

    function escapeHtml(text) {
        if (!text) return '';
        var div = document.createElement('div');
        div.textContent = text;
        return div.innerHTML;
    }

    function debounce(fn, delay) {
        var timer = null;
        return function () {
            var context = this, args = arguments;
            clearTimeout(timer);
            timer = setTimeout(function () {
                fn.apply(context, args);
            }, delay);
        };
    }

    /**
     * Phase 2: Detect mobile device for sharpness threshold
     */
    function isMobileDevice() {
        return /iPhone|iPad|iPod|Android.*Mobile|Windows Phone/i.test(navigator.userAgent);
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
            scanUrl: '/Biometrics/ScanFrame',
            enrollUrl: '/Biometrics/Enroll',
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

        // DOM elements
        this.elements = {};

        // Callbacks (Phase 2: added onAngleUpdate)
        this.callbacks = {
            onStatus: null,
            onLivenessUpdate: null,
            onCaptureProgress: null,
            onEnrollmentComplete: null,
            onEnrollmentError: null,
            onMultiFaceWarning: null,
            onAngleUpdate: null     // Phase 2: angle guidance callback
        };

        this.captureCanvas = document.createElement('canvas');
    }

    // -------------------------------------------------------------------------
    // Phase 2: Sharpness Calculation
    // -------------------------------------------------------------------------

    /**
     * Computes Laplacian variance on face ROI, downscaled to 160×160.
     * Measures image sharpness — higher = sharper.
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

        // Use face center offset relative to full frame as yaw proxy
        var faceCenterX = (faceBox.x + faceBox.w / 2) / (canvasW || 640);
        var faceCenterY = (faceBox.y + faceBox.h / 2) / (canvasH || 480);

        var yaw = (faceCenterX - 0.5) * 60;   // approx degrees, ±30 range
        var pitch = (faceCenterY - 0.5) * 40; // approx degrees, ±20 range

        // Aspect ratio hint: narrow vertical box = looking up
        var aspect = faceBox.h / faceBox.w;
        if (aspect > 1.4) pitch -= 10;
        if (aspect < 0.8) pitch += 10;

        // If MediaPipe 6-point landmarks available, use nose offset for better yaw
        if (landmarks && landmarks.length >= 3) {
            var rEye = landmarks[0], lEye = landmarks[1], nose = landmarks[2];
            if (rEye && lEye && nose) {
                var eyeMidX = (rEye.x + lEye.x) / 2;
                var noseDeltaX = (nose.x - eyeMidX) * 100;
                yaw = noseDeltaX * 1.5; // scale to degrees
            }
        }

        var absYaw = Math.abs(yaw);
        var absPitch = Math.abs(pitch);

        if (absYaw > 30 || absPitch > 25) return 'other';
        if (absYaw < 10 && absPitch < 10) return 'center';

        if (absYaw >= absPitch) {
            if (yaw < -10) return 'left';
            if (yaw > 10) return 'right';
        } else {
            if (pitch < -10) return 'up';
            if (pitch > 10) return 'down';
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

        return { bucket: 'center', prompt: 'Hold still — capturing final frames', icon: 'fa-check' };
    };

    // -------------------------------------------------------------------------
    // Camera Operations
    // -------------------------------------------------------------------------

    Enrollment.prototype.startCamera = function (videoElement) {
        var self = this;
        this.elements.cam = videoElement || this.elements.cam;

        return new Promise(function (resolve, reject) {
            if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
                reject(new Error('Camera API not available'));
                return;
            }

            navigator.mediaDevices.getUserMedia({
                video: { facingMode: 'user' },
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
        this.stopAutoEnrollment();

        if (this.stream) {
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

        var w = CONSTANTS.CAPTURE_WIDTH;
        var h = CONSTANTS.CAPTURE_HEIGHT;

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

    Enrollment.prototype.postScanFrame = function (blob) {
        var self = this;
        var fd = new FormData();
        fd.append('__RequestVerificationToken', getCsrfToken());
        fd.append('image', blob, 'frame.jpg');

        return fetch(this.config.scanUrl, { method: 'POST', body: fd })
            .then(function (res) { return res.json(); })
            .then(function (result) { return self.normalizeSuccessResult(result); });
    };

    Enrollment.prototype.postEnrollMany = function (blobs) {
        var self = this;

        if (!blobs || !blobs.length) {
            return Promise.resolve({ ok: false, error: 'NO_IMAGE' });
        }

        var startTime = Date.now();

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

        if (this.enrolled || !this.stream || this.busy) return;

        var cam = this.elements.cam;
        if (!cam || !cam.videoWidth) return;

        this.busy = true;

        var capturedBlob = null;

        // Phase 2: Quick client-side sharpness check BEFORE upload
        var sharpnessOk = true;
        var sharpness = 0;
        if (cam.videoWidth > 0) {
            var tmpCanvas = document.createElement('canvas');
            tmpCanvas.width = CONSTANTS.CAPTURE_WIDTH;
            tmpCanvas.height = CONSTANTS.CAPTURE_HEIGHT;
            var tmpCtx = tmpCanvas.getContext('2d');
            tmpCtx.drawImage(cam, 0, 0, CONSTANTS.CAPTURE_WIDTH, CONSTANTS.CAPTURE_HEIGHT);

            var threshold = isMobileDevice()
                ? CONSTANTS.SHARPNESS_THRESHOLD_MOBILE
                : CONSTANTS.SHARPNESS_THRESHOLD_DESKTOP;
            sharpness = this.calculateSharpness(tmpCanvas, this.lastFaceBox);

            if (sharpness < threshold) {
                sharpnessOk = false;
                this.busy = false;
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
                self.handleError('Auto enroll failed: ' + (e && e.message ? e.message : e));
                self.passHist = [];
                self.goodFrames = [];
            })
            .finally(function () {
                self.busy = false;
            });
    };

    /**
     * Phase 2: Updated processScanResult with poseBucket and auto-submit
     */
    Enrollment.prototype.processScanResult = function (r) {
        if (!r || r.ok !== true) {
            var errorCode = r && r.error;
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
            } else {
                this.handleError((r && r.message) ? r.message : (errorCode || 'Scan error'));
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
            var poseBucket = this.estimatePoseBucket(null, r.faceBox, CONSTANTS.CAPTURE_WIDTH, CONSTANTS.CAPTURE_HEIGHT);
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

            if ((allAngles && hasEnoughFrames && CONSTANTS.AUTO_SUBMIT_ON_ALL_ANGLES) || hasMaxFrames) {
                this.performEnrollment();
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
