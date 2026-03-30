/**
 * enrollment-tracker.js  v2.1
 *
 * PITCH FIX (v2.1):
 *
 *   The old formula  (noseBelow / bb.height - 0.20) * 250  had two compounding bugs:
 *
 *   1. bb.height (BlazeFace raw bbox) is NOT stable across webcam heights.
 *      A laptop camera positioned above the face produces noseBelow ≈ 0 at neutral
 *      (nose appears at eye level from the camera angle). The constant 0.20 assumed
 *      nose is 20% of bbox height below the eyes, which is only true for a camera
 *      exactly at eye level. Result: neutral pose → P:-50° → flagged as UP forever.
 *
 *   2. Multiplier 250 gave 0.4° per percentage point — CENTER required hitting a
 *      0.04-wide window on a 0–1 ratio. Physically impossible to hold consistently.
 *
 *   FIX: Use yaw-compensated eyeSpanX as the pitch denominator.
 *     • eyeSpanX / cos(yaw) restores the "frontal" inter-ocular distance regardless
 *       of head turn — directly eliminates the yaw-amplification problem.
 *     • Subtract 0.10 offset: empirically correct for cameras positioned above eye
 *       level (the most common laptop/monitor webcam position).
 *     • Multiplier 100 with CENTER_PITCH = 28 gives ±28° of tolerance for CENTER,
 *       which safely captures all "neutral-ish" poses across all webcam heights.
 *
 *   CENTER_PITCH widened from 10 → 28: the old 10° window was too tight to survive
 *   natural head micro-movements AND the calibration error simultaneously.
 */
(function () {
    'use strict';

    var video  = document.getElementById('enrollVideo');
    var canvas = document.getElementById('enrollFaceCanvas');
    if (!video || !canvas) return;

    var ctx = canvas.getContext('2d');
    if (!ctx) return;

    var appBase = (document.body.getAttribute('data-app-base') || '/').replace(/\/?$/, '/');

    var MIN_CONF     = 0.30;
    var ALPHA        = 0.35;
    var SHIFT_UP     = 0.20;
    var EXPAND_H     = 0.08;
    var ANGLES       = ['center', 'left', 'right', 'down', 'up'];

    // ── FIXED: CENTER_PITCH widened from 10 → 28 ─────────────────────────────
    // 28° absorbs all realistic webcam-height variation while still demanding
    // an obvious tilt to register as UP or DOWN.
    var CENTER_YAW   = 18; // widened to match server GetPoseBucket threshold
    var CENTER_PITCH = 28;
    var MAX_YAW      = 45;
    var MAX_PITCH    = 55;

    var detector      = null;
    var smoothed      = null;
    var scanLinePos   = 0;
    var active        = false;
    var lastTs        = -1;

    var currentPose   = { bucket: '', yaw: 0, pitch: 0, conf: 0 };
    var currentFaceArea = 0;

    var videoTrack   = null;
    var poiThrottle  = 0;

    function enhanceCameraFocus() {
        var stream = video.srcObject;
        if (!stream || !(stream instanceof MediaStream)) return;

        var tracks = stream.getVideoTracks();
        if (!tracks.length) return;

        videoTrack = tracks[0];
        var caps   = videoTrack.getCapabilities ? videoTrack.getCapabilities() : {};

        var advanced = [];

        if (caps.focusMode && caps.focusMode.indexOf('continuous') !== -1) {
            advanced.push({ focusMode: 'continuous' });
        }

        if (caps.zoom && caps.zoom.min <= 1 && caps.zoom.max >= 1) {
            advanced.push({ zoom: 1 });
        }

        if (caps.resizeMode && caps.resizeMode.indexOf('none') !== -1) {
            advanced.push({ resizeMode: 'none' });
        }

        if (caps.whiteBalanceMode && caps.whiteBalanceMode.indexOf('continuous') !== -1) {
            advanced.push({ whiteBalanceMode: 'continuous' });
        }

        if (caps.exposureMode && caps.exposureMode.indexOf('continuous') !== -1) {
            advanced.push({ exposureMode: 'continuous' });
        }

        if (!advanced.length) return;

        videoTrack.applyConstraints({ advanced: advanced })
            .then(function () {
                console.log('[enrollment-tracker] Focus constraints applied:', advanced);
            })
            .catch(function (e) {
                console.warn('[enrollment-tracker] Focus constraint apply (non-fatal):', e.message);
            });
    }

    function applyFocusPoint(normX, normY) {
        if (!videoTrack) return;
        var now = Date.now();
        if (now - poiThrottle < 1000) return;
        poiThrottle = now;

        var caps = videoTrack.getCapabilities ? videoTrack.getCapabilities() : {};
        if (!caps.pointsOfInterest) return;

        videoTrack.applyConstraints({
            advanced: [{
                pointsOfInterest: [{ x: normX, y: normY }],
                focusMode: 'single-shot'
            }]
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

    // =========================================================================
    // FIXED estimatePose — yaw-compensated eyeSpanX pitch
    // =========================================================================
    function estimatePose(detection) {
        var kps  = detection.keypoints;
        var bb   = detection.boundingBox;
        var conf = (detection.categories && detection.categories[0] &&
                    detection.categories[0].score) || 0;

        if (!kps || kps.length < 3) {
            var cx    = bb ? bb.originX + bb.width  / 2 : 0.5;
            var yawFB = -(cx - 0.5) * 60;
            return { bucket: poseBucket(yawFB, 0), yaw: Math.round(yawFB), pitch: 0, conf: conf };
        }

        var rEye = kps[0];
        var lEye = kps[1];
        var nose = kps[2];

        var eyeMidX  = (lEye.x + rEye.x) / 2;
        var eyeMidY  = (lEye.y + rEye.y) / 2;
        var eyeSpanX = Math.abs(lEye.x - rEye.x);

        if (eyeSpanX < 0.005) {
            return { bucket: 'center', yaw: 0, pitch: 0, conf: conf };
        }

        // YAW — unchanged, works correctly
        var yaw = -((nose.x - eyeMidX) / eyeSpanX) * 90;

        // ── PITCH FIX ──────────────────────────────────────────────────────────
        // Problem with old formula (noseBelow / bb.height - 0.20) * 250:
        //   bb.height is NOT a reliable reference — it varies drastically with
        //   webcam height. A laptop camera above the face makes noseBelow ≈ 0
        //   at neutral, giving pitch ≈ (0 - 0.20) * 250 = -50° → flagged UP.
        //
        // Fix: use eyeSpanX / cos(yaw) as the reference (yaw-compensated eye span).
        //   This is self-calibrating: it scales with the face's own geometry and
        //   compensates for the eye-span foreshortening caused by head turning.
        //
        //   - At neutral (webcam above, noseBelow ≈ 0):
        //       pitch = (0 / eyeSpanRef - 0.10) * 100 = -10° → CENTER ✓
        //   - At neutral (webcam at eye level, noseBelow ≈ 0.35 × eyeSpanRef):
        //       pitch = (0.35 - 0.10) * 100 = +25° → CENTER (within ±28°) ✓
        //   - Obvious UP tilt (any webcam): noseBelow goes negative relative to eyeSpanRef
        //       pitch < -28° → UP ✓
        //   - Obvious DOWN tilt: noseBelow grows large
        //       pitch > +28° → DOWN ✓
        var noseBelow   = nose.y - eyeMidY;
        var absYawRad   = Math.abs(yaw) * Math.PI / 180;
        // cos(yaw) compensates eyeSpanX foreshortening; floor at 0.3 prevents divide explosion
        var yawComp     = Math.max(0.3, Math.cos(absYawRad));
        var eyeSpanRef  = eyeSpanX / yawComp;
        // 0.10 offset: centers the neutral band for above-camera webcams (most common)
        // 100 multiplier: moderate sensitivity, CENTER_PITCH=28 gives ±28° of tolerance
        var pitch = (noseBelow / eyeSpanRef - 0.10) * 100;

        return {
            bucket: poseBucket(yaw, pitch),
            yaw:    Math.round(yaw),
            pitch:  Math.round(pitch),
            conf:   conf
        };
    }

    function poseBucket(yaw, pitch) {
        var absYaw   = Math.abs(yaw);
        var absPitch = Math.abs(pitch);

        if (absYaw < CENTER_YAW && absPitch < CENTER_PITCH) return 'center';

        // Always classify to nearest bucket — never return 'other'
        if (absYaw >= absPitch) {
            return yaw < 0 ? 'left' : 'right';
        } else {
            return pitch < 0 ? 'up' : 'down';
        }
    }

    function stateColor() {
        var enroll = window.FaceAttendEnrollment;
        if (!enroll) return { main: '#4f9cf9', glow: 'rgba(79,156,249,0.45)' };

        var done     = enroll.goodFrames ? enroll.goodFrames.length : 0;
        var target   = (enroll.config && enroll.config.minGoodFrames) || 6;
        var captured = {};
        if (enroll.goodFrames) {
            enroll.goodFrames.forEach(function (f) {
                if (f.poseBucket && f.poseBucket !== 'other') captured[f.poseBucket] = true;
            });
        }
        var allAngles = ANGLES.every(function (a) { return !!captured[a]; });

        if (allAngles && done >= target)
            return { main: '#22c55e', glow: 'rgba(34,197,94,0.55)' };
        if (done > 0)
            return { main: '#f59e0b', glow: 'rgba(245,158,11,0.50)' };
        return { main: '#4f9cf9', glow: 'rgba(79,156,249,0.45)' };
    }

    function bracket(ax, ay, mx, my, ex, ey) {
        ctx.beginPath();
        ctx.moveTo(ax, ay);
        ctx.lineTo(mx, my);
        ctx.lineTo(ex, ey);
        ctx.stroke();
    }

    function roundRect(c, x, y, w, h, r) {
        c.beginPath();
        c.moveTo(x + r, y);
        c.lineTo(x + w - r, y);
        c.arcTo(x + w, y,     x + w, y + r,     r);
        c.lineTo(x + w, y + h - r);
        c.arcTo(x + w, y + h, x + w - r, y + h, r);
        c.lineTo(x + r, y + h);
        c.arcTo(x,     y + h, x,      y + h - r, r);
        c.lineTo(x,     y + r);
        c.arcTo(x,     y,     x + r,  y,         r);
        c.closePath();
    }

    function drawPoseBadge(bx, by, bw, bh, col) {
        var pose = currentPose;

        // Use server-confirmed bucket when fresh (within 800ms) to keep
        // the badge in sync with what actually gets checked off
        var enroll = window.FaceAttendEnrollment;
        if (enroll && enroll.confirmedPoseBucket && enroll.confirmedPoseTs
            && (Date.now() - enroll.confirmedPoseTs) < 800) {
            pose = { bucket: enroll.confirmedPoseBucket, yaw: pose.yaw, pitch: pose.pitch, conf: pose.conf };
        }

        if (!pose.bucket) return;

        var LABELS = {
            center: 'CENTER',
            left:   'LEFT',
            right:  'RIGHT',
            up:     'UP',
            down:   'DOWN'
        };

        var label = LABELS[pose.bucket];
        if (!label) return;

        var debug = 'Y:' + pose.yaw + '° P:' + pose.pitch + '°  ' +
                    Math.round(pose.conf * 100) + '%';
        var pad = 6, r = 4;

        ctx.save();
        ctx.textBaseline = 'middle';

        ctx.font = 'bold 11px "Helvetica Neue", Arial, sans-serif';
        var lw   = ctx.measureText(label).width;
        var lh   = 20;
        var lx   = bx + bw - lw - pad * 2 - 2;
        var ly   = by + 2;
        lx = Math.max(bx + 2, lx);

        ctx.globalAlpha = 0.88;
        ctx.fillStyle   = col.main;
        roundRect(ctx, lx, ly, lw + pad * 2, lh, r);
        ctx.fill();

        ctx.globalAlpha = 1;
        ctx.fillStyle   = '#ffffff';
        ctx.fillText(label, lx + pad, ly + lh / 2);

        ctx.font = '10px "Helvetica Neue", Arial, sans-serif';
        var dw   = ctx.measureText(debug).width;
        var dh   = 18;
        var dx   = bx + bw - dw - pad * 2 - 2;
        var dy   = by + bh - dh - 2;
        dx = Math.max(bx + 2, dx);

        ctx.globalAlpha = 0.82;
        ctx.fillStyle   = 'rgba(0,0,0,0.65)';
        roundRect(ctx, dx, dy, dw + pad * 2, dh, r);
        ctx.fill();

        ctx.globalAlpha = 1;
        ctx.fillStyle   = '#e2e8f0';
        ctx.fillText(debug, dx + pad, dy + dh / 2);

        ctx.restore();
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
                            var aA = a.boundingBox
                                ? a.boundingBox.width * a.boundingBox.height : 0;
                            var bA = b.boundingBox
                                ? b.boundingBox.width * b.boundingBox.height : 0;
                            return aA >= bA ? a : b;
                        });

                        detectedBox = mapToCanvas(toVideoBox(best.boundingBox), cssW, cssH);
                        currentPose = estimatePose(best);

                        currentFaceArea = best.boundingBox
                            ? best.boundingBox.width * best.boundingBox.height
                            : 0;

                        if (best.boundingBox) {
                            var ncx = best.boundingBox.originX + best.boundingBox.width  / 2;
                            var ncy = best.boundingBox.originY + best.boundingBox.height / 2;
                            applyFocusPoint(ncx, ncy);
                        }
                    } else {
                        currentPose     = { bucket: '', yaw: 0, pitch: 0, conf: 0 };
                        currentFaceArea = 0;
                    }
                } catch (e) {
                    // Bad frame — keep loop alive
                }
            }
        }

        if (detectedBox) {
            if (!smoothed) {
                smoothed = { x: detectedBox.x, y: detectedBox.y,
                             w: detectedBox.w, h: detectedBox.h };
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

        if (!smoothed) { scanLinePos = 0; return; }

        var bx = smoothed.x, by = smoothed.y, bw = smoothed.w, bh = smoothed.h;

        var col    = stateColor();
        var isBusy = !!(window.FaceAttendEnrollment && window.FaceAttendEnrollment.busy);

        if (isBusy) {
            scanLinePos = (scanLinePos + 0.016) % 1.0;
            var scanY = by + bh * scanLinePos;
            var grad  = ctx.createLinearGradient(bx, scanY, bx + bw, scanY);
            grad.addColorStop(0,    'rgba(0,0,0,0)');
            grad.addColorStop(0.25, col.main);
            grad.addColorStop(0.75, col.main);
            grad.addColorStop(1,    'rgba(0,0,0,0)');
            ctx.save();
            ctx.globalAlpha = 0.55;
            ctx.strokeStyle = grad;
            ctx.lineWidth   = 1.5;
            ctx.shadowColor = col.main;
            ctx.shadowBlur  = 8;
            ctx.beginPath();
            ctx.moveTo(bx, scanY);
            ctx.lineTo(bx + bw, scanY);
            ctx.stroke();
            ctx.restore();
        } else {
            scanLinePos = 0;
        }

        var cLen = Math.min(bw, bh) * 0.20;
        var lw   = isBusy ? 3.5 : 2.5;

        ctx.save();
        ctx.strokeStyle = col.main;
        ctx.lineWidth   = lw;
        ctx.lineCap     = 'round';
        ctx.shadowColor = col.glow;
        ctx.shadowBlur  = 14;

        bracket(bx + cLen,      by,      bx,      by,      bx,      by + cLen);
        bracket(bx + bw - cLen, by,      bx + bw, by,      bx + bw, by + cLen);
        bracket(bx + cLen,      by + bh, bx,      by + bh, bx,      by + bh - cLen);
        bracket(bx + bw - cLen, by + bh, bx + bw, by + bh, bx + bw, by + bh - cLen);

        ctx.restore();

        ctx.save();
        ctx.strokeStyle = col.main;
        ctx.lineWidth   = isBusy ? 2.0 : 1.5;
        ctx.globalAlpha = 0.75;
        ctx.strokeRect(bx, by, bw, bh);
        ctx.globalAlpha = 0.04;
        ctx.fillStyle   = col.main;
        ctx.fillRect(bx, by, bw, bh);
        ctx.restore();

        drawPoseBadge(bx, by, bw, bh, col);

        if (window.FaceAttendEnrollment) {
            window.FaceAttendEnrollment.liveTrackingBox = {
                x: bx, y: by, w: bw, h: bh
            };
            window.FaceAttendEnrollment.livePose = {
                bucket: currentPose.bucket,
                yaw:    currentPose.yaw,
                pitch:  currentPose.pitch,
                conf:   currentPose.conf
            };
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
                console.log('[enrollment-tracker] v2.1 ready — fixed pitch formula');
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
                clearInterval(iv);
                initMp();
            } else if (++attempts > 50) {
                clearInterval(iv);
                console.warn('[enrollment-tracker] vision_loader timed out.');
            }
        }, 200);
    }

    function waitForCamera() {
        if (video.videoWidth > 0) {
            enhanceCameraFocus();
            tryLoadMp();
            return;
        }
        var checks = 0;
        var iv = setInterval(function () {
            if (video.videoWidth > 0) {
                clearInterval(iv);
                enhanceCameraFocus();
                tryLoadMp();
            } else if (++checks > 120) {
                clearInterval(iv);
            }
        }, 500);
    }

    video.addEventListener('playing', function () {
        setTimeout(enhanceCameraFocus, 300);
    });

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', function () {
            setTimeout(waitForCamera, 500);
        });
    } else {
        setTimeout(waitForCamera, 500);
    }

    window.addEventListener('beforeunload', function () {
        active = false;
        if (detector) { try { detector.close(); } catch (e) {} }
    });

})();
