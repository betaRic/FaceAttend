// kiosk-mediapipe.js
// MediaPipe Tasks face detector adapter, bounding-box helpers, stable tracking.
// Exposes window.KioskMediapipe — must be loaded after kiosk-canvas.js.
(function () {
    'use strict';

    var _video          = null;
    var _canvas         = null;
    var _state          = null;
    var _cfg            = null;
    var _nextGenEnabled = false;
    var _log            = null;
    var _setKioskMode   = null;
    var _safeSetPrompt  = null;

    // ── Bounding-box helpers ───────────────────────────────────────────────────

    function toVideoBox(bb) {
        if (!bb) return null;

        var looksNormalized =
            bb.width  <= 1.5 &&
            bb.height <= 1.5 &&
            bb.originX <= 1.5 &&
            bb.originY <= 1.5;

        var x, y, w, h;
        if (looksNormalized) {
            x = bb.originX * _video.videoWidth;
            y = bb.originY * _video.videoHeight;
            w = bb.width   * _video.videoWidth;
            h = bb.height  * _video.videoHeight;
        } else {
            x = bb.originX;
            y = bb.originY;
            w = bb.width;
            h = bb.height;
        }

        // CENTER FACE: shift up by 20% of height, expand height by 8%
        var shiftUp = h * 0.20;
        var expandH = h * 0.08;
        y = y - shiftUp;
        h = h + expandH;

        return { x: x, y: y, w: w, h: h };
    }

    function boxFullyVisibleCanvas(box) {
        if (!box) return false;
        if (box.w <= 0 || box.h <= 0) return false;

        var cx = box.x + (box.w / 2);
        var cy = box.y + (box.h / 2);
        var mx = _canvas.width  * 0.04;
        var my = _canvas.height * 0.04;

        if (cx < mx || cx > (_canvas.width  - mx)) return false;
        if (cy < my || cy > (_canvas.height - my)) return false;
        if (box.w < _canvas.width  * 0.10) return false;
        if (box.h < _canvas.height * 0.14) return false;

        return true;
    }

    function isTooSmallFaceNorm(bbox) {
        if (!bbox || !isFinite(bbox.width) || !isFinite(bbox.height)) return true;
        return bbox.width * bbox.height < _cfg.gating.minFaceAreaRatio;
    }

    // ── Stable tracking ────────────────────────────────────────────────────────
    function updateStableTracking(box, now) {
        if (!box) { _state.mpStableStart = 0; return; }
        var c = { x: box.x + box.w / 2, y: box.y + box.h / 2 };

        if (!_state.mpPrevCenter) {
            _state.mpPrevCenter  = c;
            _state.mpStableStart = now;
            _state.mpReadyToFire = false;
            return;
        }

        var move = Math.hypot(c.x - _state.mpPrevCenter.x, c.y - _state.mpPrevCenter.y);
        _state.mpPrevCenter = c;

        if (move > _cfg.gating.stableMaxMovePx * 2.5) {
            _state.mpStableStart = 0;
            _state.mpReadyToFire = false;
            _safeSetPrompt('Hold still.', '');
            return;
        }

        if (_state.mpStableStart === 0) _state.mpStableStart = now;

        if ((now - _state.mpStableStart) < _cfg.mp.stableNeededMs) {
            return;
        }

        _state.mpReadyToFire = true;
    }

    // ── MediaPipe Tasks adapter ────────────────────────────────────────────────

    var mp = {
        vision:     null,
        detector:   null,
        failStreak: 0,

        init: function () {
            if (!_nextGenEnabled) return Promise.reject(new Error('NEXTGEN_DISABLED'));

            var hasTasks = (
                typeof window.MpFilesetResolver === 'function' &&
                typeof window.MpFaceDetectorTask === 'function'
            );
            if (!hasTasks) return Promise.reject(new Error('MP_ASSETS_MISSING'));

            var self = this;
            return window.MpFilesetResolver
                .forVisionTasks(_cfg.tasksVision.wasmBase)
                .then(function (vision) {
                    self.vision = vision;
                    return window.MpFaceDetectorTask.createFromOptions(vision, {
                        baseOptions: {
                            modelAssetPath: _cfg.tasksVision.modelPath,
                            delegate: 'GPU',
                        },
                        runningMode: 'VIDEO',
                        minDetectionConfidence:  _cfg.mp.detectMinConf,
                        minSuppressionThreshold: 0.3,
                    }).catch(function () {
                        return window.MpFaceDetectorTask.createFromOptions(vision, {
                            baseOptions: {
                                modelAssetPath: _cfg.tasksVision.modelPath,
                                delegate: 'CPU',
                            },
                            runningMode: 'VIDEO',
                            minDetectionConfidence:  _cfg.mp.detectMinConf,
                            minSuppressionThreshold: 0.3,
                        });
                    });
                })
                .then(function (detector) {
                    self.detector = detector;
                    _state.mpMode = 'tasks';
                    _setKioskMode('tasks');
                    _log('MediaPipe Tasks ready');
                })
                .catch(function (e) {
                    _state.mpMode = 'none';
                    _log('MediaPipe init failed', e);
                    throw e;
                });
        },

        tick: function () {
            if (_state.mpMode !== 'tasks' || !this.detector || !_video.videoWidth) return;
            try {
                var now    = Math.floor(performance.now());
                var result = this.detector.detectForVideo(_video, now);
                var dets   = (result && result.detections) ? result.detections : [];

                var valid = dets.filter(function (d) {
                    return ((d.categories && d.categories[0] && d.categories[0].score) || 0) >= _cfg.mp.acceptMinScore;
                });

                if (valid.length === 0) {
                    _state.faceStatus = 'none';
                    if (!_state.liveInFlight) {
                        _state.mpBoxCanvas = null;
                        _state.smoothedBox = null;
                        _state.mpReadyToFire = false;  // FIX: reset when face lost
                    }
                    return;
                }

                // Always pick the LARGEST face (closest to camera)
                var best = valid.reduce(function (a, b) {
                    var aA = (a.boundingBox ? a.boundingBox.width * a.boundingBox.height : 0);
                    var bA = (b.boundingBox ? b.boundingBox.width * b.boundingBox.height : 0);
                    return aA >= bA ? a : b;
                });

                if (valid.length > 1) {
                    _safeSetPrompt('Multiple faces detected.', 'Scanning the closest person.');
                }

                var bb  = best.boundingBox;
                var box = KioskCanvas.mapVideoBoxToCanvas(toVideoBox(bb));

                window.__kioskDebug = {
                    rawBox:      bb,
                    mappedBox:   box,
                    videoWidth:  _video.videoWidth,
                    videoHeight: _video.videoHeight
                };

                _state.faceStatus = (box && box.w > 20 && box.h > 20 && !isTooSmallFaceNorm(bb)) ? 'good' : 'low';

                // EMA smoothing for bounding box
                if (!_state.smoothedBox) {
                    _state.smoothedBox = { x: box.x, y: box.y, w: box.w, h: box.h };
                } else {
                    var a = 0.35;
                    _state.smoothedBox = {
                        x: _state.smoothedBox.x + a * (box.x - _state.smoothedBox.x),
                        y: _state.smoothedBox.y + a * (box.y - _state.smoothedBox.y),
                        w: _state.smoothedBox.w + a * (box.w - _state.smoothedBox.w),
                        h: _state.smoothedBox.h + a * (box.h - _state.smoothedBox.h)
                    };
                }
                _state.mpBoxCanvas  = _state.smoothedBox;
                _state.mpFaceSeenAt = Date.now();
                this.failStreak     = 0;

                if (_state.faceStatus === 'low') {
                    _state.mpReadyToFire = false;
                    _state.mpStableStart = 0;
                    _state.mpPrevCenter  = null;
                    _safeSetPrompt('Move closer.', 'Please approach the camera.');
                    return;
                }

                // FIX: For 'good' status, run stability check with near-zero threshold
                updateStableTracking(box, Date.now());

            } catch (e) {
                this.failStreak++;
                if (this.failStreak > 30) {
                    _state.mpMode = 'none';
                    _log('MediaPipe recurring error, disabling', e);
                }
            }
        }
    };

    // ── Init ───────────────────────────────────────────────────────────────────

    function init(videoEl, canvasEl, stateRef, cfgRef, nextGenEnabledVal, callbacks) {
        _video          = videoEl;
        _canvas         = canvasEl;
        _state          = stateRef;
        _cfg            = cfgRef;
        _nextGenEnabled = nextGenEnabledVal;
        _log            = callbacks.log;
        _setKioskMode   = callbacks.setKioskMode;
        _safeSetPrompt  = callbacks.safeSetPrompt;
        return mp.init();
    }

    window.KioskMediapipe = {
        init:    init,
        tick:    function () { mp.tick(); },
        isReady: function () { return _state && _state.mpMode === 'tasks'; }
    };
})();
