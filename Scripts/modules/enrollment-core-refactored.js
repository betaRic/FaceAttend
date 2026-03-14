/**
 * FaceAttend - Enrollment Core Module (Refactored)
 * Consolidated enrollment logic using FaceAttend.Core modules
 * 
 * @version 2.0.0
 * @requires FaceAttend.Camera
 * @requires FaceAttend.API
 * @requires FaceAttend.Notify
 * @requires FaceAttend.FaceScan
 */

var FaceAttend = window.FaceAttend || {};

FaceAttend.Enrollment = (function () {
    'use strict';

    // =========================================================================
    // CONSTANTS
    // =========================================================================
    var CONSTANTS = {
        AUTO_INTERVAL_MS: 300,
        PASS_WINDOW: 3,
        PASS_REQUIRED: 1,
        CAPTURE_TARGET: 8,
        MIN_GOOD_FRAMES: 3,
        MAX_KEEP_FRAMES: 8,
        CAPTURE_WIDTH: 640,
        CAPTURE_HEIGHT: 480,
        UPLOAD_QUALITY: 0.80,
        SHARPNESS_THRESHOLD_DESKTOP: 80,
        SHARPNESS_THRESHOLD_MOBILE: 50,
        SHARPNESS_SAMPLE_SIZE: 160,
        ANGLE_SEQUENCE: ['center', 'left', 'right', 'up', 'down'],
        AUTO_SUBMIT_ON_ALL_ANGLES: true
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

    function isMobileDevice() {
        return /iPhone|iPad|iPod|Android.*Mobile|Windows Phone/i.test(navigator.userAgent);
    }

    // =========================================================================
    // ENROLLMENT CLASS (Refactored to use Core modules)
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
            debug: false,
            enableDiversity: true,
            useCoreModules: true  // NEW: Use unified core modules
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
        this.lastFaceBox = null;

        // DOM elements
        this.elements = {};

        // Callbacks
        this.callbacks = {
            onStatus: null,
            onLivenessUpdate: null,
            onCaptureProgress: null,
            onEnrollmentComplete: null,
            onEnrollmentError: null,
            onMultiFaceWarning: null,
            onAngleUpdate: null
        };

        // Core module instances
        this.scanner = null;
        this.camera = null;

        this.captureCanvas = document.createElement('canvas');
        
        // Initialize core modules if available
        this._initCoreModules();
    }

    // -------------------------------------------------------------------------
    // NEW: Core Module Integration
    // -------------------------------------------------------------------------

    Enrollment.prototype._initCoreModules = function() {
        if (!this.config.useCoreModules) return;
        
        // Initialize API
        if (window.FaceAttend && window.FaceAttend.API) {
            window.FaceAttend.API.init({
                baseUrl: this.config.scanUrl.replace('api/scan/frame', ''),
                csrfToken: getCsrfToken()
            });
        }
        
        // Create scanner instance
        if (window.FaceAttend && window.FaceAttend.FaceScan) {
            this.scanner = Object.create(window.FaceAttend.FaceScan);
            this.scanner.init({
                minFrames: this.config.minGoodFrames,
                maxFrames: this.config.maxKeepFrames,
                livenessThreshold: this.config.perFrameThreshold,
                enableDiversity: this.config.enableDiversity,
                intervalMs: CONSTANTS.AUTO_INTERVAL_MS
            });
        }
    };

    // -------------------------------------------------------------------------
    // Camera Operations (Refactored to use FaceAttend.Camera)
    // -------------------------------------------------------------------------

    Enrollment.prototype.startCamera = function (videoElement) {
        var self = this;
        
        // Use core Camera module if available
        if (this.config.useCoreModules && window.FaceAttend && window.FaceAttend.Camera) {
            return new Promise(function(resolve, reject) {
                window.FaceAttend.Camera.start(
                    videoElement,
                    { facingMode: 'user' },
                    function(stream) {
                        self.stream = stream;
                        self.elements.cam = videoElement;
                        resolve(stream);
                    },
                    function(err) {
                        reject(err);
                    }
                );
            });
        }
        
        // Legacy fallback
        return this._legacyStartCamera(videoElement);
    };

    Enrollment.prototype._legacyStartCamera = function(videoElement) {
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
        // Use core Camera if we used it
        if (this.config.useCoreModules && window.FaceAttend && window.FaceAttend.Camera) {
            window.FaceAttend.Camera.stop();
        } else {
            // Legacy stop
            if (this.stream) {
                try {
                    this.stream.getTracks().forEach(function (t) { t.stop(); });
                } catch (e) { }
            }
            
            if (this.elements.cam) {
                this.elements.cam.srcObject = null;
            }
        }
        
        this.stopAutoEnrollment();
        this.stream = null;
        this.enrolled = false;
        this.enrolling = false;
        this.passHist = [];
        this.goodFrames = [];
        this.lastFaceBox = null;
    };

    // -------------------------------------------------------------------------
    // Auto Enrollment (Refactored to use FaceAttend.FaceScan)
    // -------------------------------------------------------------------------

    Enrollment.prototype.startAutoEnrollment = function () {
        var self = this;
        
        // Use core FaceScan module if available
        if (this.config.useCoreModules && this.scanner) {
            this.scanner.reset();
            
            var camera = window.FaceAttend.Camera;
            if (!camera.isActive()) {
                this.handleError('Camera not active');
                return;
            }
            
            this.scanner.start(camera, {
                onStart: function(config) {
                    self.handleStatus('Scanning started. Follow angle prompts.', 'info');
                },
                onProgress: function(progress) {
                    // Update our goodFrames from scanner
                    self.goodFrames = self.scanner.getFrames();
                    
                    // Notify callbacks
                    if (self.callbacks.onCaptureProgress) {
                        self.callbacks.onCaptureProgress(progress.current, progress.target);
                    }
                    
                    // Angle update
                    var nextAngle = self.scanner.getNextAngle();
                    if (self.callbacks.onAngleUpdate && nextAngle) {
                        self.callbacks.onAngleUpdate(nextAngle);
                    }
                },
                onFrameCapture: function(frame) {
                    self.lastFaceBox = frame.faceBox;
                    
                    if (self.callbacks.onLivenessUpdate) {
                        var pct = Math.round(frame.liveness * 100);
                        self.callbacks.onLivenessUpdate(pct, 'pass');
                    }
                },
                onComplete: function(result) {
                    self.handleStatus('All angles captured. Saving enrollment...', 'success');
                    self.performEnrollment();
                },
                onError: function(err) {
                    self.handleError(err.message);
                }
            });
            
            return;
        }
        
        // Legacy fallback
        this._legacyStartAutoEnrollment();
    };

    Enrollment.prototype._legacyStartAutoEnrollment = function() {
        var self = this;
        this.stopAutoEnrollment();
        this.enrolled = false;
        this.passHist = [];
        this.goodFrames = [];
        this.lastFaceBox = null;
        this.autoTimer = setInterval(this.autoTick.bind(this), CONSTANTS.AUTO_INTERVAL_MS);
    };

    Enrollment.prototype.stopAutoEnrollment = function () {
        // Stop core scanner if using it
        if (this.scanner) {
            this.scanner.stop();
        }
        
        if (this.autoTimer) {
            clearInterval(this.autoTimer);
        }
        this.autoTimer = null;
        this.enrolling = false;
    };

    // -------------------------------------------------------------------------
    // Legacy Auto Tick (kept for backward compatibility)
    // -------------------------------------------------------------------------

    Enrollment.prototype.autoTick = function () {
        // Only runs in legacy mode - FaceScan module handles this now
        if (this.config.useCoreModules && this.scanner) return;
        
        var self = this;

        if (this.enrolled || !this.stream || this.busy) return;
        var cam = this.elements.cam;
        if (!cam || !cam.videoWidth) return;

        this.busy = true;

        this.captureJpegBlob(CONSTANTS.UPLOAD_QUALITY)
            .then(function (blob) {
                return self.postScanFrame(blob);
            })
            .then(function (result) {
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

    // -------------------------------------------------------------------------
    // Server Communication (Refactored to use FaceAttend.API)
    // -------------------------------------------------------------------------

    Enrollment.prototype.postScanFrame = function (blob) {
        var self = this;
        
        // Use core API module if available
        if (this.config.useCoreModules && window.FaceAttend && window.FaceAttend.API) {
            return new Promise(function(resolve, reject) {
                window.FaceAttend.API.scanFrame(blob, function(result) {
                    // Normalize result format
                    result = self.normalizeSuccessResult(result);
                    resolve(result);
                }, function(err) {
                    reject(err);
                });
            });
        }
        
        // Legacy fetch
        var fd = new FormData();
        fd.append('__RequestVerificationToken', getCsrfToken());
        fd.append('image', blob, 'frame.jpg');

        return fetch(this.config.scanUrl, { method: 'POST', body: fd })
            .then(function (res) { return res.json(); })
            .then(function (result) { return self.normalizeSuccessResult(result); });
    };

    Enrollment.prototype.postEnrollMany = function (blobs) {
        var self = this;
        
        // Use core API module if available
        if (this.config.useCoreModules && window.FaceAttend && window.FaceAttend.API) {
            return new Promise(function(resolve, reject) {
                var startTime = Date.now();
                
                window.FaceAttend.API.enroll(
                    self.config.empId,
                    blobs,
                    { allEncodings: self.getEncodings() },
                    function(result) {
                        result.timeMs = Date.now() - startTime;
                        resolve(result);
                    },
                    function(err) {
                        reject(err);
                    }
                );
            });
        }
        
        // Legacy fetch
        var startTime = Date.now();
        var fd = new FormData();
        fd.append('__RequestVerificationToken', getCsrfToken());
        fd.append('employeeId', this.config.empId);

        for (var i = 0; i < blobs.length; i++) {
            fd.append('image', blobs[i], 'enroll_' + (i + 1) + '.jpg');
        }

        var encodings = this.getEncodings();
        if (encodings.length > 0) {
            fd.append('allEncodingsJson', JSON.stringify(encodings));
        }

        return fetch(this.config.enrollUrl, { method: 'POST', body: fd })
            .then(function (res) { return res.json(); })
            .then(function (result) {
                result = self.normalizeSuccessResult(result) || result;
                if (result) result.timeMs = Date.now() - startTime;
                return result;
            })
            .catch(function (e) {
                return { ok: false, error: 'NETWORK_ERROR', message: e.message };
            });
    };

    // -------------------------------------------------------------------------
    // Enrollment Operations
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
                        ? result.savedVectors
                        : self.goodFrames.length;

                    if (self.callbacks.onEnrollmentComplete) {
                        self.callbacks.onEnrollmentComplete(vecCount, result);
                    }
                    
                    // Use core notification
                    if (window.FaceAttend && window.FaceAttend.Notify) {
                        window.FaceAttend.Notify.success('Enrollment complete! ' + vecCount + ' face samples saved.');
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
    // Helper Methods (unchanged from original)
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

    Enrollment.prototype.getEncodings = function () {
        return this.goodFrames
            .map(function (f) { return f.encoding; })
            .filter(function (e) { return !!e; });
    };

    // -------------------------------------------------------------------------
    // Processing & Error Handling (unchanged)
    // -------------------------------------------------------------------------

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

        if (r.faceBox) {
            this.lastFaceBox = r.faceBox;
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
            var poseBucket = r.poseBucket || 'center';
            var sharpness = r.sharpness || 0;
            
            this.pushGoodFrame(r.lastBlob || null, p, r.encoding || null, poseBucket, sharpness);

            if (this.callbacks.onAngleUpdate) {
                var next = this.getNextAnglePrompt();
                if (next && next.bucket !== 'other') {
                    this.callbacks.onAngleUpdate(next);
                }
            }

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

            var needFrames = Math.max(0, this.config.minGoodFrames - this.goodFrames.length);
            var msg = 'Face OK. Liveness: ' + p.toFixed(2) +
                ', Frames: ' + this.goodFrames.length + '/' + this.config.minGoodFrames +
                ', Ready in ' + needFrames + ' more.';
            this.handleStatus(msg, 'success');
        } else {
            var failMsg = 'Face detected. Liveness: ' + p.toFixed(2) + 
                ' (need ' + this.config.perFrameThreshold + '). Hold still, improve lighting.';
            this.handleStatus(failMsg, 'warning');
            this.passHist = [];
        }
    };

    Enrollment.prototype.getNextAnglePrompt = function () {
        var captured = {};
        for (var i = 0; i < this.goodFrames.length; i++) {
            var b = this.goodFrames[i].poseBucket;
            if (b) captured[b] = true;
        }

        var prompts = {
            center: { prompt: 'Look straight at the camera', icon: 'fa-circle-dot' },
            left: { prompt: 'Turn your head slightly LEFT', icon: 'fa-arrow-left' },
            right: { prompt: 'Turn your head slightly RIGHT', icon: 'fa-arrow-right' },
            up: { prompt: 'Tilt your head slightly UP', icon: 'fa-arrow-up' },
            down: { prompt: 'Tilt your head slightly DOWN', icon: 'fa-arrow-down' }
        };

        for (var j = 0; j < CONSTANTS.ANGLE_SEQUENCE.length; j++) {
            var bucket = CONSTANTS.ANGLE_SEQUENCE[j];
            if (!captured[bucket]) {
                return { bucket: bucket, prompt: prompts[bucket].prompt, icon: prompts[bucket].icon };
            }
        }

        return { bucket: 'center', prompt: 'Hold still - capturing final frames', icon: 'fa-check' };
    };

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
        
        // Use core notification
        if (window.FaceAttend && window.FaceAttend.Notify) {
            window.FaceAttend.Notify.error(message);
        }
    };

    Enrollment.prototype.handleEnrollmentError = function (result) {
        var errorText = this.describeEnrollError(result);
        this.handleStatus(errorText, 'danger');

        if (this.callbacks.onEnrollmentError) {
            this.callbacks.onEnrollmentError(result);
        }
        
        // Use core notification
        if (window.FaceAttend && window.FaceAttend.Notify) {
            window.FaceAttend.Notify.error(errorText);
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
            return 'Employee not found in database.';
        }
        if (r.error === 'NO_GOOD_FRAME') {
            var processed = typeof r.processed === 'number'
                ? ' (processed ' + r.processed + ' images)'
                : '';
            return 'No good frame found. Please try better lighting.' + processed;
        }
        if (r.error === 'FACE_ALREADY_ENROLLED') {
            var who = r.matchEmployeeId
                ? ' matched with employee ' + escapeHtml(r.matchEmployeeId)
                : '';
            return 'Face already enrolled' + who + '. Please contact administrator.';
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
            isMobileDevice: isMobileDevice
        }
    };
})();

// Global assignment
window.FaceAttend = FaceAttend;
