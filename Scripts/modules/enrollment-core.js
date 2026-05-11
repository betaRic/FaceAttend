var FaceAttend = window.FaceAttend || {};

FaceAttend.Enrollment = (function () {
    'use strict';

    var CONSTANTS = {
        AUTO_INTERVAL_MS:  1000,
        PASS_WINDOW:       3,
        PASS_REQUIRED:     1,
        CAPTURE_TARGET:    10,
        MIN_GOOD_FRAMES:   10,
        MAX_KEEP_FRAMES:   12,
        MAX_IMAGES:        12,
        CAPTURE_WIDTH:     640,
        CAPTURE_HEIGHT:    480,
        UPLOAD_QUALITY:    0.92,
        SHARPNESS_THRESHOLD_DESKTOP: 35,
        SHARPNESS_THRESHOLD_MOBILE:  28,
        SHARPNESS_SAMPLE_SIZE:       256,
        MIN_FACE_AREA_RATIO_DESKTOP: 0.08,
        MIN_FACE_AREA_RATIO_MOBILE:  0.06,
        FACE_AREA_WARNING_RATIO:     0.05,
        MAX_FACE_AREA_RATIO:         0.90,
        MIN_BRIGHTNESS:              30,
        AUTO_CONFIRM_TIMEOUT_MS:     5000
    };

    function getCsrfToken() {
        return FaceAttend.Utils ? FaceAttend.Utils.getCsrfToken() : '';
    }

    function buildRequestHeaders(extraHeaders) {
        if (FaceAttend.Utils && typeof FaceAttend.Utils.mergeRequestHeaders === 'function')
            return FaceAttend.Utils.mergeRequestHeaders(extraHeaders);

        var headers = {};
        if (extraHeaders) {
            Object.keys(extraHeaders).forEach(function (key) {
                headers[key] = extraHeaders[key];
            });
        }
        return headers;
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

    function Enrollment(config) {
        this.config = Object.assign({
            empId: '',
            perFrameThreshold: 0.45,
            scanUrl: '/api/scan/frame',
            enrollUrl: '/api/enrollment/enroll',
            redirectUrl: '/Admin/Employees',
            minGoodFrames: 10,
            maxKeepFrames: 12,
            enablePreview: true,
            debug: false
        }, config);

        this.stream          = null;
        this.enrolled        = false;
        this.enrolling       = false;
        this.multiFaceWarned = false;
        this.passHist        = [];
        this.goodFrames      = [];
        this.lastFaceBox     = null;
        this.confirmTimer    = null;

        this._tickRunning          = false;
        this._tickEnabled          = false;
        this._scanController       = null;
        this._zeroAntiSpoofStreak   = 0;

        this.elements  = {};
        this.callbacks = {
            onStatus:             null,
            onAntiSpoofUpdate:     null,
            onCaptureProgress:    null,
            onEnrollmentComplete: null,
            onEnrollmentError:    null,
            onMultiFaceWarning:   null,
            onReadyToConfirm:     null,
            onDistanceFeedback:   null
        };

        this.captureCanvas   = document.createElement('canvas');
        this.sharpnessCanvas = document.createElement('canvas');
    }

    Enrollment.prototype.calculateSharpness = function (canvas) {
        var W = CONSTANTS.SHARPNESS_SAMPLE_SIZE, H = CONSTANTS.SHARPNESS_SAMPLE_SIZE;

        var tmp  = this.sharpnessCanvas;
        tmp.width = W; tmp.height = H;
        var tCtx = tmp.getContext('2d');
        var sx = canvas.width  * 0.25;
        var sy = canvas.height * 0.05;
        var sw = canvas.width  * 0.50;
        var sh = canvas.height * 0.90;

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

    Enrollment.prototype.calculateBrightness = function () {
        var W = CONSTANTS.SHARPNESS_SAMPLE_SIZE, H = CONSTANTS.SHARPNESS_SAMPLE_SIZE;
        var tmp = this.sharpnessCanvas;
        if (!tmp || !tmp.width) return 128;
        var imgData = tmp.getContext('2d').getImageData(0, 0, W, H).data;
        var sum = 0;
        for (var i = 0; i < imgData.length; i += 4)
            sum += 0.299 * imgData[i] + 0.587 * imgData[i + 1] + 0.114 * imgData[i + 2];
        return sum / (imgData.length >> 2);
    };

    function getMinFaceAreaRatio() {
        return isMobileDevice() ? CONSTANTS.MIN_FACE_AREA_RATIO_MOBILE : CONSTANTS.MIN_FACE_AREA_RATIO_DESKTOP;
    }

    function getSharpnessThreshold() {
        return isMobileDevice()
            ? CONSTANTS.SHARPNESS_THRESHOLD_MOBILE
            : CONSTANTS.SHARPNESS_THRESHOLD_DESKTOP;
    }

    Enrollment.prototype.startCamera = function (videoElement) {
        var self = this;
        this.elements.cam = videoElement || this.elements.cam;
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
            method: 'POST',
            body: fd,
            credentials: 'same-origin',
            signal: controller.signal,
            headers: buildRequestHeaders({
                'X-Requested-With': 'XMLHttpRequest'
            })
        })
        .then(function (r) { if (!r.ok) throw new Error('HTTP ' + r.status); return r.json(); })
        .then(function (data) { return self.normalizeSuccessResult(data); })
        .catch(function (e) { if (e && e.name === 'AbortError') return null; throw e; })
        .finally(function () {
            if (self._scanController === controller) self._scanController = null;
        });
    };

    Enrollment.prototype._scheduleTick = function () {
        var self = this;
        if (!this._tickEnabled) return;

        setTimeout(function () {
            if (!self._tickEnabled) return;
            self._tickRunning = true;
            self._runOneTick()
                .catch(function () {})
                .finally(function () {
                    self._tickRunning = false;
                    self._scheduleTick();
                });
        }, CONSTANTS.AUTO_INTERVAL_MS);
    };

    Enrollment.prototype._runOneTick = function () {
        var self = this;

        if (this.enrolled || !this.stream) return Promise.resolve();
        var cam = this.elements.cam;
        if (!cam || !cam.videoWidth) return Promise.resolve();

        var isMobile = isMobileDevice();
        var capturedBlob = null;
        var sharpness = 0;

        return this.captureJpegBlob(CONSTANTS.UPLOAD_QUALITY)
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
                self.handleStatus(isMobile ? 'Retrying...' : 'Scan error, retrying...', 'warning');
                self.passHist = [];
            });
    };

    Enrollment.prototype.startAutoEnrollment = function () {
        this._clearConfirmTimer();
        this.stopAutoEnrollment();

        this.enrolled = false;
        this.passHist = [];
        this.goodFrames = [];
        this._zeroAntiSpoofStreak = 0;
        this.lastFaceBox = null;

        this._tickEnabled = true;
        this._scheduleTick();
    };

    Enrollment.prototype.stopAutoEnrollment = function () {
        this._tickEnabled = false;
        this._abortCurrentScan();
        this.enrolling = false;
    };

    Enrollment.prototype._fireReadyToConfirm = function () {
        this._clearConfirmTimer();
        if (!this.callbacks.onReadyToConfirm) return;

        var bestAntiSpoof = 0;
        for (var j = 0; j < this.goodFrames.length; j++)
            if (this.goodFrames[j].p > bestAntiSpoof) bestAntiSpoof = this.goodFrames[j].p;

        this.callbacks.onReadyToConfirm({
            frameCount:   this.goodFrames.length,
            bestAntiSpoof: Math.round(bestAntiSpoof * 100),
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

    Enrollment.prototype.pushGoodFrame = function (blob, probability, sharpness) {
        if (!blob) return;

        var normSharp = typeof sharpness === 'number' ? Math.min(sharpness / 500, 1.0) : 0;
        var antiSpoof  = typeof probability === 'number' ? probability : 0;
        var quality   = antiSpoof * 0.7 + normSharp * 0.3;

        var newFrame = {
            blob:      blob,
            p:         antiSpoof,
            sharpness: typeof sharpness === 'number' ? sharpness : 0,
            quality:   quality
        };

        if (this.goodFrames.length < this.config.maxKeepFrames) {
            this.goodFrames.push(newFrame);
        } else {
            var worstIdx = 0;
            for (var i = 1; i < this.goodFrames.length; i++)
                if (this.goodFrames[i].quality < this.goodFrames[worstIdx].quality) worstIdx = i;
            if (quality > this.goodFrames[worstIdx].quality)
                this.goodFrames[worstIdx] = newFrame;
        }

        this.goodFrames.sort(function (a, b) { return b.quality - a.quality; });

        if (this.callbacks.onCaptureProgress)
            this.callbacks.onCaptureProgress(this.goodFrames.length, this.config.minGoodFrames);
    };

    Enrollment.prototype.getGoodFrameBlobs = function () {
        return this.goodFrames.map(function (x) { return x.blob; });
    };

    Enrollment.prototype.processScanResult = function (r) {
        var self = this;

        if (!r || r.ok !== true) {
            var errorCode = r && r.error;
            if (errorCode === 'NO_FACE' || errorCode === 'NO_IMAGE') {
                this.handleStatus('No face detected.', 'warning');
                if (this.callbacks.onAntiSpoofUpdate) this.callbacks.onAntiSpoofUpdate(0, 'fail');
            } else if (errorCode === 'ANTI_SPOOF_FAIL') {
                this.handleStatus('Anti-spoof check failed. Ensure good lighting.', 'warning');
                if (this.callbacks.onAntiSpoofUpdate) this.callbacks.onAntiSpoofUpdate(25, 'fail');
            } else if (errorCode === 'ENCODING_FAIL') {
                this.handleStatus('Could not encode face. Please try again.', 'warning');
                if (this.callbacks.onAntiSpoofUpdate) this.callbacks.onAntiSpoofUpdate(0, 'fail');
            } else {
                this.handleStatus((r && r.message) ? r.message : (errorCode || 'Scan error'), 'warning');
                if (this.callbacks.onAntiSpoofUpdate) this.callbacks.onAntiSpoofUpdate(0, 'fail');
            }
            this.passHist = [];
            return;
        }

        if (r.count === 0) {
            this.handleStatus('No face detected.', 'warning');
            this.passHist = [];
            if (this.callbacks.onAntiSpoofUpdate) this.callbacks.onAntiSpoofUpdate(0, 'fail');
            return;
        }

        if (r.faceBox) this.lastFaceBox = r.faceBox;

        if (r.count > 1) {
            if (!this.multiFaceWarned) { this.multiFaceWarned = true; if (this.callbacks.onMultiFaceWarning) this.callbacks.onMultiFaceWarning(r.count); }
        } else if (this.multiFaceWarned) { this.multiFaceWarned = false; if (this.callbacks.onMultiFaceWarning) this.callbacks.onMultiFaceWarning(1); }

        var p    = typeof r.antiSpoofScore === 'number' ? r.antiSpoofScore : 0;
        var pass = (r.antiSpoofOk === true) && (p >= this.config.perFrameThreshold);

        this.passHist.push(pass ? 1 : 0);
        if (this.passHist.length > CONSTANTS.PASS_WINDOW) this.passHist.shift();

        if (this.callbacks.onAntiSpoofUpdate)
            this.callbacks.onAntiSpoofUpdate(Math.round(p * 100), pass ? 'pass' : 'fail');

        if (pass) {
            this._zeroAntiSpoofStreak = 0;

            if (r.faceBox && r.faceBox.w > 0) this.lastFaceBox = r.faceBox;

            var sharpness = r.sharpness || r.clientSharpness || 0;
            this.pushGoodFrame(r.lastBlob || null, p, sharpness);

            var hasEnoughFrames = this.goodFrames.length >= this.config.minGoodFrames;
            var hasMaxFrames    = this.goodFrames.length >= this.config.maxKeepFrames;

            if (hasMaxFrames || (hasEnoughFrames && p >= 0.70)) {
                this.stopAutoEnrollment();
                this._clearConfirmTimer();
                this._fireReadyToConfirm();
                return;
            }

            if (hasEnoughFrames && !this.confirmTimer && !this.enrolled && !this.enrolling)
                this._startConfirmTimer();

            var needFrames = Math.max(0, this.config.minGoodFrames - this.goodFrames.length);
            this.handleStatus(
                'Good — keep still. ' + this.goodFrames.length + '/' + this.config.minGoodFrames + ' frames',
                'success');
        } else {
            if (p < 0.02 && r.count > 0) {
                this._zeroAntiSpoofStreak = (this._zeroAntiSpoofStreak || 0) + 1;
            } else {
                this._zeroAntiSpoofStreak = 0;
            }

            if (this._zeroAntiSpoofStreak >= 8) {
                this.handleStatus('Look directly at the camera in good lighting.', 'warning');
            } else if (r.count === 0) {
                this.handleStatus('No face detected — face the camera.', 'warning');
            } else {
                this.handleStatus(
                    'Anti-spoof: ' + Math.round(p * 100) + '% (need ' +
                    Math.round(this.config.perFrameThreshold * 100) + '%) — look at camera.',
                    'warning');
            }
            this.passHist = [];
        }
    };

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
            fd.append('images', blobs[i], 'enroll_' + (i + 1) + '.jpg');

        return fetch(this.config.enrollUrl, {
            method: 'POST',
            body: fd,
            credentials: 'same-origin',
            headers: buildRequestHeaders({
                'X-Requested-With': 'XMLHttpRequest'
            })
        })
            .then(function (res) { return res.json(); })
            .then(function (result) {
                result = self.normalizeSuccessResult(result) || result;
                if (result) result.timeMs = Date.now() - startTime;
                return result;
            })
            .catch(function (e) { return { ok: false, error: 'NETWORK_ERROR', message: e.message }; });
    };

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
                    fd.append('images', imgs[i], imgs[i].name || ('upload_' + (i + 1) + '.jpg'));
                return fetch(self.config.enrollUrl, {
                    method: 'POST',
                    body: fd,
                    credentials: 'same-origin',
                    headers: buildRequestHeaders({
                        'X-Requested-With': 'XMLHttpRequest'
                    })
                }).then(function (res) { return res.json(); });
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
        var timeInfo = typeof r.timeMs === 'number' ? ' (' + r.timeMs + 'ms)' : '';
        if (r.error === 'NO_EMPLOYEE_ID')       return 'Please enter an employee ID.';
        if (r.error === 'EMPLOYEE_ID_TOO_LONG') return 'Employee ID too long (max 20 chars).';
        if (r.error === 'NO_IMAGE')             return 'Please capture at least one image.';
        if (r.error === 'TOO_LARGE')            return 'Image file too large (max 10MB).';
        if (r.error === 'EMPLOYEE_NOT_FOUND')   return 'Employee not found. Check the employee ID.';
        if (r.error === 'NO_GOOD_FRAME') {
            var processed = typeof r.processed === 'number' ? ' (processed ' + r.processed + ' images)' : '';
            return 'No good frame found. Better lighting, hold still, face clearly visible.' + processed;
        }
        if (r.error === 'FACE_ALREADY_ENROLLED') {
            var who = r.matchEmployeeId ? ' matched with employee <b>' + escapeHtml(r.matchEmployeeId) + '</b>' : '';
            return 'Face already enrolled' + who + '. Contact administrator if this is an error.';
        }
        if (r.error === 'NETWORK_ERROR') return 'Network error. Check connection and try again.';
        return (r.error || 'Enrollment failed') + timeInfo;
    };

    Enrollment.prototype.loadServerConfig = function (url) {
        var self = this;
        return fetch(url || '/api/enrollment/config', { credentials: 'same-origin' })
            .then(function (r) { if (!r.ok) throw new Error('HTTP ' + r.status); return r.json(); })
            .then(function (cfg) {
                if (!cfg || typeof cfg !== 'object') return;
                if (typeof cfg.antiSpoofThreshold  === 'number') self.config.perFrameThreshold       = cfg.antiSpoofThreshold;
                if (typeof cfg.sharpnessDesktop   === 'number') CONSTANTS.SHARPNESS_THRESHOLD_DESKTOP = cfg.sharpnessDesktop;
                if (typeof cfg.sharpnessMobile    === 'number') CONSTANTS.SHARPNESS_THRESHOLD_MOBILE  = cfg.sharpnessMobile;
                if (typeof cfg.minFaceAreaDesktop === 'number') CONSTANTS.MIN_FACE_AREA_RATIO_DESKTOP  = cfg.minFaceAreaDesktop;
                if (typeof cfg.minFaceAreaMobile  === 'number') CONSTANTS.MIN_FACE_AREA_RATIO_MOBILE   = cfg.minFaceAreaMobile;
            })
            .catch(function () {});
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

    return {
        create:    function (config) { return new Enrollment(config); },
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
