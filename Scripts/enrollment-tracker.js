(function () {
    'use strict';

    var video  = document.getElementById('enrollVideo');
    var canvas = document.getElementById('enrollFaceCanvas');
    if (!video || !canvas) return;

    var ctx = canvas.getContext('2d');
    if (!ctx) return;

    var appBase = (document.body.getAttribute('data-app-base') || '/').replace(/\/?$/, '/');

    var MIN_CONF   = 0.30;
    var ALPHA      = 0.35;
    var SHIFT_UP   = 0.20;
    var EXPAND_H   = 0.08;

    var detector      = null;
    var smoothed      = null;
    var active        = false;
    var lastTs        = -1;

    var currentFaceArea = 0;

    var videoTrack  = null;
    var poiThrottle = 0;

    function enhanceCameraFocus() {
        var stream = video.srcObject;
        if (!stream || !(stream instanceof MediaStream)) return;

        var tracks = stream.getVideoTracks();
        if (!tracks.length) return;

        videoTrack = tracks[0];
        var caps   = videoTrack.getCapabilities ? videoTrack.getCapabilities() : {};
        var advanced = [];

        if (caps.focusMode && caps.focusMode.indexOf('continuous') !== -1)
            advanced.push({ focusMode: 'continuous' });
        if (caps.zoom && caps.zoom.min <= 1 && caps.zoom.max >= 1)
            advanced.push({ zoom: 1 });
        if (caps.resizeMode && caps.resizeMode.indexOf('none') !== -1)
            advanced.push({ resizeMode: 'none' });
        if (caps.whiteBalanceMode && caps.whiteBalanceMode.indexOf('continuous') !== -1)
            advanced.push({ whiteBalanceMode: 'continuous' });
        if (caps.exposureMode && caps.exposureMode.indexOf('continuous') !== -1)
            advanced.push({ exposureMode: 'continuous' });

        if (!advanced.length) return;
        videoTrack.applyConstraints({ advanced: advanced }).catch(function () {});
    }

    function applyFocusPoint(normX, normY) {
        if (!videoTrack) return;
        var now = Date.now();
        if (now - poiThrottle < 1000) return;
        poiThrottle = now;

        var caps = videoTrack.getCapabilities ? videoTrack.getCapabilities() : {};
        if (!caps.pointsOfInterest) return;

        videoTrack.applyConstraints({
            advanced: [{ pointsOfInterest: [{ x: normX, y: normY }], focusMode: 'single-shot' }]
        }).catch(function () {});
    }

    function toVideoBox(bb) {
        if (!bb || !video.videoWidth) return null;
        var isNorm = bb.width <= 1.5 && bb.height <= 1.5;
        var x = isNorm ? bb.originX * video.videoWidth  : bb.originX;
        var y = isNorm ? bb.originY * video.videoHeight : bb.originY;
        var w = isNorm ? bb.width   * video.videoWidth  : bb.width;
        var h = isNorm ? bb.height  * video.videoHeight : bb.height;
        y -= h * SHIFT_UP;
        h += h * EXPAND_H;
        return { x: x, y: y, w: w, h: h };
    }

    function mapToCanvas(vbox, cssW, cssH) {
        if (!vbox || !video.videoWidth) return null;
        var vW    = video.videoWidth, vH = video.videoHeight;
        var scale = Math.max(cssW / vW, cssH / vH);
        var offX  = (cssW - vW * scale) / 2;
        var offY  = (cssH - vH * scale) / 2;
        var x     = offX + vbox.x * scale;
        var y     = offY + vbox.y * scale;
        var w     = vbox.w * scale;
        var h     = vbox.h * scale;
        x = cssW - (x + w);
        return { x: x, y: y, w: w, h: h };
    }

    function syncCanvas() {
        var dpr  = window.devicePixelRatio || 1;
        var cssW = canvas.offsetWidth  || 640;
        var cssH = canvas.offsetHeight || 480;
        var pw   = Math.round(cssW * dpr);
        var ph   = Math.round(cssH * dpr);
        if (canvas.width !== pw || canvas.height !== ph) {
            canvas.width  = pw;
            canvas.height = ph;
            ctx.scale(dpr, dpr);
        }
        return { w: cssW, h: cssH };
    }

    function tick() {
        if (!active) return;
        requestAnimationFrame(tick);

        var dims = syncCanvas();
        var cssW = dims.w, cssH = dims.h;
        ctx.clearRect(0, 0, cssW, cssH);

        var detectedBox = null;

        if (detector && video.videoWidth && video.videoHeight) {
            var ts = Math.floor(performance.now());
            if (ts > lastTs) {
                lastTs = ts;
                try {
                    var result = detector.detectForVideo(video, ts);
                    var dets   = result && result.detections ? result.detections : [];
                    var valid  = dets.filter(function (d) {
                        return ((d.categories && d.categories[0] &&
                                 d.categories[0].score) || 0) >= MIN_CONF;
                    });

                    if (valid.length) {
                        var best = valid.reduce(function (a, b) {
                            var aA = a.boundingBox ? a.boundingBox.width * a.boundingBox.height : 0;
                            var bA = b.boundingBox ? b.boundingBox.width * b.boundingBox.height : 0;
                            return aA >= bA ? a : b;
                        });

                        detectedBox = mapToCanvas(toVideoBox(best.boundingBox), cssW, cssH);

                        if (best.boundingBox && video.videoWidth && video.videoHeight) {
                            var bbW = best.boundingBox.width, bbH = best.boundingBox.height;
                            var rawArea = bbW * bbH;
                            // MediaPipe returns pixel-space coords; normalize to 0-1 ratio
                            var isNormBb = bbW <= 1.5 && bbH <= 1.5;
                            currentFaceArea = isNormBb
                                ? rawArea
                                : rawArea / (video.videoWidth * video.videoHeight);
                        } else {
                            currentFaceArea = 0;
                        }

                        if (best.boundingBox) {
                            var ncx = best.boundingBox.originX + best.boundingBox.width  / 2;
                            var ncy = best.boundingBox.originY + best.boundingBox.height / 2;
                            applyFocusPoint(ncx, ncy);
                        }
                    } else {
                        currentFaceArea = 0;
                    }
                } catch (e) {}
            }
        }

        if (detectedBox) {
            if (!smoothed) {
                smoothed = { x: detectedBox.x, y: detectedBox.y, w: detectedBox.w, h: detectedBox.h };
            } else {
                smoothed.x += ALPHA * (detectedBox.x - smoothed.x);
                smoothed.y += ALPHA * (detectedBox.y - smoothed.y);
                smoothed.w += ALPHA * (detectedBox.w - smoothed.w);
                smoothed.h += ALPHA * (detectedBox.h - smoothed.h);
            }
        } else {
            if (smoothed) {
                smoothed.w *= 0.92;
                smoothed.h *= 0.92;
                if (smoothed.w < 10) smoothed = null;
            }
        }

        // ── Oval guide (always drawn, even without a face) ────────────────────
        var enroll     = window.FaceAttendEnrollment;
        var isBusy     = !!(enroll && enroll.busy);
        var done       = enroll && enroll.goodFrames ? enroll.goodFrames.length : 0;
        var target     = (enroll && enroll.config && enroll.config.minGoodFrames) || 25;
        var guideState = (FaceAttend.FaceGuide && smoothed)
            ? FaceAttend.FaceGuide.getState(currentFaceArea, smoothed, cssW, cssH)
            : 'none';

        if (FaceAttend.FaceGuide) {
            FaceAttend.FaceGuide.draw(ctx, cssW, cssH, guideState, done / target, isBusy);
        }

        // ── Guide text prompt ─────────────────────────────────────────────────
        var promptEl = document.getElementById('enrollGuidePrompt');
        if (promptEl) {
            if (!smoothed || guideState === 'none') {
                promptEl.innerHTML = '<i class="fa-solid fa-circle-dot"></i> Position your face in the oval';
            } else if (guideState === 'too_close') {
                promptEl.innerHTML = '<i class="fa-solid fa-arrow-down"></i> Too close — back up';
            } else if (guideState === 'too_far') {
                promptEl.innerHTML = '<i class="fa-solid fa-arrow-up"></i> Move closer to the camera';
            } else if (guideState === 'off_center') {
                promptEl.innerHTML = '<i class="fa-solid fa-arrows-up-down-left-right"></i> Center your face in the oval';
            } else if (done >= target) {
                promptEl.innerHTML = '<i class="fa-solid fa-check"></i> Face captured';
            } else if (isBusy) {
                promptEl.innerHTML = '<i class="fa-solid fa-circle-notch fa-spin"></i> Hold still...';
            } else {
                promptEl.innerHTML = '<i class="fa-solid fa-circle-check"></i> Good position — hold still';
            }
        }

        if (!smoothed) return;

        if (window.FaceAttendEnrollment) {
            var bx = smoothed.x, by = smoothed.y, bw = smoothed.w, bh = smoothed.h;
            window.FaceAttendEnrollment.liveTrackingBox = { x: bx, y: by, w: bw, h: bh };
            window.FaceAttendEnrollment.liveFaceArea     = currentFaceArea;
        }
    }

    function buildDetector(vision) {
        var modelPath = appBase +
            'Scripts/vendor/mediapipe/tasks-vision/models/blaze_face_short_range.tflite';
        var opts = {
            baseOptions: { modelAssetPath: modelPath, delegate: 'GPU' },
            runningMode:             'VIDEO',
            minDetectionConfidence:  MIN_CONF,
            minSuppressionThreshold: 0.3
        };
        return window.MpFaceDetectorTask.createFromOptions(vision, opts)
            .catch(function () {
                opts.baseOptions.delegate = 'CPU';
                return window.MpFaceDetectorTask.createFromOptions(vision, opts);
            });
    }

    function initMp() {
        if (typeof window.MpFilesetResolver  !== 'function' ||
            typeof window.MpFaceDetectorTask !== 'function') return;

        var wasmBase = appBase + 'Scripts/vendor/mediapipe/tasks-vision/wasm';
        window.MpFilesetResolver.forVisionTasks(wasmBase)
            .then(function (vision) { return buildDetector(vision); })
            .then(function (det) {
                detector = det;
                active   = true;
                requestAnimationFrame(tick);
            })
            .catch(function (e) {
                console.warn('[enrollment-tracker] MediaPipe init failed:', e);
            });
    }

    function tryLoadMp() {
        if (typeof window.MpFilesetResolver === 'function') { initMp(); return; }

        var s  = document.createElement('script');
        s.type = 'module';
        s.src  = appBase + 'Scripts/vendor/mediapipe/tasks-vision/vision_loader.mjs';
        document.head.appendChild(s);

        var attempts = 0;
        var iv = setInterval(function () {
            if (typeof window.MpFilesetResolver === 'function') {
                clearInterval(iv); initMp();
            } else if (++attempts > 50) {
                clearInterval(iv);
            }
        }, 200);
    }

    function waitForCamera() {
        if (video.videoWidth > 0) { enhanceCameraFocus(); tryLoadMp(); return; }
        var checks = 0;
        var iv = setInterval(function () {
            if (video.videoWidth > 0) { clearInterval(iv); enhanceCameraFocus(); tryLoadMp(); }
            else if (++checks > 120) { clearInterval(iv); }
        }, 500);
    }

    video.addEventListener('playing', function () { setTimeout(enhanceCameraFocus, 300); });

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', function () { setTimeout(waitForCamera, 500); });
    } else {
        setTimeout(waitForCamera, 500);
    }

    window.addEventListener('beforeunload', function () {
        active = false;
        if (detector) { try { detector.close(); } catch (e) {} }
    });
})();
