/**
 * enrollment-tracker.js  v2.0
 *
 * Self-contained 60fps face tracker for ALL enrollment pages.
 * Requires only:   <video id="enrollVideo">   <canvas id="enrollFaceCanvas">
 *
 * WHAT'S NEW IN v2.0:
 *
 *   CAM-01  Camera focus enhancement.
 *           After the stream is live, this module grabs the video track and
 *           applies  focusMode:'continuous', zoom:1, resizeMode:'none'  plus
 *           continuous white-balance and exposure. All constraints are checked
 *           against getCapabilities() first — unsupported ones are silently
 *           skipped, so nothing breaks on fixed-focus webcams.
 *
 *   CAM-02  Point-of-interest focus targeting.
 *           When a face is detected, the face-centre is fed back to the camera
 *           as a pointsOfInterest hint (throttled to once per second). On
 *           phones/laptops with hardware phase-detect AF this snaps focus to
 *           the face and eliminates the soft-focus that blurry scores report.
 *
 *   POSE-01 Pose estimation from MediaPipe keypoints.
 *           FaceDetector returns 6 normalised keypoints per detection.
 *           Yaw (left/right) and pitch (up/down) are derived from the eye-
 *           midpoint vs nose-tip offset — same math as enrollment-core.js
 *           estimatePoseBucket().
 *
 *   POSE-02 Pose debug badge on bounding box.
 *           Top-right corner:    bucket label + arrow  e.g. "← LEFT"
 *           Bottom-right corner: yaw / pitch angles + detection confidence
 *           Coloured to match the box state (blue / amber / green).
 *
 * INTEGRATION:
 *   1. Add to enrollment bundle after vision_loader:
 *        .Include("~/Scripts/enrollment-tracker.js")
 *
 *   2. Remove the old startFaceOverlay() call and its RAF loop from
 *      Admin/Enroll.cshtml — this file replaces them completely.
 *
 *   3. Optionally add vision_loader tag to enrollment views to skip the
 *      dynamic-injection fallback:
 *        <script type="module"
 *          src="@Url.Content("~/Scripts/vendor/mediapipe/tasks-vision/vision_loader.mjs")">
 *        </script>
 */
(function () {
    'use strict';

    // ── DOM guard ──────────────────────────────────────────────────────────────
    var video  = document.getElementById('enrollVideo');
    var canvas = document.getElementById('enrollFaceCanvas');
    if (!video || !canvas) return;

    var ctx = canvas.getContext('2d');
    if (!ctx) return;

    var appBase = (document.body.getAttribute('data-app-base') || '/').replace(/\/?$/, '/');

    // ── Constants ─────────────────────────────────────────────────────────────
    var MIN_CONF     = 0.30;   // MediaPipe detection confidence floor
    var ALPHA        = 0.35;   // EMA alpha — identical to kiosk smoothedBox
    var SHIFT_UP     = 0.20;   // shift box up 20% of height to include forehead
    var EXPAND_H     = 0.08;   // expand box height 8%
    var ANGLES       = ['center', 'left', 'right', 'down'];  // 'up' removed

    // Pose bucket thresholds — same values as enrollment-core.js
    var CENTER_YAW   = 12;
    var CENTER_PITCH = 10;
    var MAX_YAW      = 45;
    var MAX_PITCH    = 35;

    // ── Module state ──────────────────────────────────────────────────────────
    var detector      = null;
    var smoothed      = null;
    var scanLinePos   = 0;
    var active        = false;
    var lastTs        = -1;

    // Pose state — updated every detection tick, consumed every draw tick
    var currentPose   = { bucket: '', yaw: 0, pitch: 0, conf: 0 };

    // Face area ratio in normalised image space (0-1). Used for real distance check.
    // Separate from pose bucket — a face can be 'other' (extreme angle) but still close.
    var currentFaceArea = 0;

    // Camera focus state
    var videoTrack   = null;   // MediaStreamTrack, set once stream is live
    var poiThrottle  = 0;      // epoch ms — rate-limits pointsOfInterest calls

    // ==========================================================================
    // CAM-01 / CAM-02 — Camera focus enhancement
    // ==========================================================================

    /**
     * Grab the video track from the live stream and apply all supported
     * focus/quality constraints. Safe to call multiple times — checks
     * capabilities before every applyConstraints().
     */
    function enhanceCameraFocus() {
        var stream = video.srcObject;
        if (!stream || !(stream instanceof MediaStream)) return;

        var tracks = stream.getVideoTracks();
        if (!tracks.length) return;

        videoTrack = tracks[0];
        var caps   = videoTrack.getCapabilities ? videoTrack.getCapabilities() : {};

        // Build advanced constraint set from what the camera actually supports
        var advanced = [];

        // Continuous autofocus
        if (caps.focusMode && caps.focusMode.indexOf('continuous') !== -1) {
            advanced.push({ focusMode: 'continuous' });
        }

        // Lock zoom to 1x — forces full sensor resolution, eliminates digital-zoom blur
        if (caps.zoom && caps.zoom.min <= 1 && caps.zoom.max >= 1) {
            advanced.push({ zoom: 1 });
        }

        // Prevent browser from interpolating / blurring via software resize
        if (caps.resizeMode && caps.resizeMode.indexOf('none') !== -1) {
            advanced.push({ resizeMode: 'none' });
        }

        // Continuous white balance
        if (caps.whiteBalanceMode && caps.whiteBalanceMode.indexOf('continuous') !== -1) {
            advanced.push({ whiteBalanceMode: 'continuous' });
        }

        // Continuous exposure
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

    /**
     * CAM-02: Point-of-interest focus targeting.
     * Sends the detected face centre to the camera as a focus hint.
     * Throttled to once per second to avoid hammering the camera driver.
     *
     * @param {number} normX  face-centre X in normalised image coords (0-1)
     * @param {number} normY  face-centre Y in normalised image coords (0-1)
     */
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

    // ==========================================================================
    // Coordinate helpers — direct ports from kiosk.js
    // ==========================================================================

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
        var scale = Math.max(cssW / vW, cssH / vH);   // object-fit:cover
        var offX  = (cssW - vW * scale) / 2;
        var offY  = (cssH - vH * scale) / 2;
        var x     = offX + vbox.x * scale;
        var y     = offY + vbox.y * scale;
        var w     = vbox.w * scale;
        var h     = vbox.h * scale;
        x = cssW - (x + w);                            // mirror X for CSS scaleX(-1)
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

    // ==========================================================================
    // POSE-01 — Pose estimation from MediaPipe keypoints
    // ==========================================================================

    /**
     * MediaPipe FaceDetector keypoints (normalised 0-1, UNMIRRORED image coords):
     *   0: right_eye  (subject's right)
     *   1: left_eye   (subject's left)
     *   2: nose_tip
     *   3: mouth_center
     *   4: right_ear_tragion
     *   5: left_ear_tragion
     *
     * Yaw convention (matches enrollment-core.js FIX-POSE-01):
     *   negative = face turning to subject's left
     *   positive = face turning to subject's right
     *
     * Pitch convention:
     *   negative = tilting head up (nose rises toward eyes)
     *   positive = tilting head down (nose drops below eyes)
     */
    function estimatePose(detection) {
        var kps  = detection.keypoints;
        var bb   = detection.boundingBox;
        var conf = (detection.categories && detection.categories[0] &&
                    detection.categories[0].score) || 0;

        // Fallback when keypoints are absent
        if (!kps || kps.length < 3) {
            var cx    = bb ? bb.originX + bb.width  / 2 : 0.5;
            var yawFB = -(cx - 0.5) * 60;
            return { bucket: poseBucket(yawFB, 0), yaw: Math.round(yawFB), pitch: 0, conf: conf };
        }

        var rEye = kps[0]; // subject's right eye
        var lEye = kps[1]; // subject's left eye
        var nose = kps[2]; // nose tip

        var eyeMidX  = (lEye.x + rEye.x) / 2;
        var eyeMidY  = (lEye.y + rEye.y) / 2;
        var eyeSpanX = Math.abs(lEye.x - rEye.x);

        if (eyeSpanX < 0.005) {
            return { bucket: 'center', yaw: 0, pitch: 0, conf: conf };
        }

        // Yaw: nose horizontal offset from eye midpoint, normalised by eye span.
        // Negated (FIX-POSE-01 convention) so positive yaw = turning right.
        // Yaw uses eye span as denominator — correct because eye span is a
        // horizontal measurement unaffected by pitch rotation.
        var yaw = -((nose.x - eyeMidX) / eyeSpanX) * 90;

        // FIX-DIST-01: Pitch MUST NOT use eyeSpanX as denominator.
        //
        // The OLD formula  pitch = (noseBelow / eyeSpanX - 0.5) * 100  had a
        // critical flaw: eyeSpanX is a PROJECTED width that shrinks as the head
        // turns (yaw rotation compresses the eye span by cos(yaw)). At 60° yaw,
        // eyeSpanX ≈ 0.5× its frontal value, making the denominator 2× smaller
        // → pitch score doubles for the same physical tilt. This is why P:71° was
        // reported for a face that was only mildly tilted down.
        //
        // FIX: normalise by bounding box HEIGHT instead of eye span.
        // The bbox height from MediaPipe is measured along the image Y axis and is
        // stable regardless of head yaw rotation. Face height and bbox height are
        // proportional, so this measure is yaw-independent.
        //
        // Calibration:
        //   At neutral (straight-ahead), the nose tip is ~20% of face bbox height
        //   below the eye midpoint. This is the zero-pitch anchor.
        //   pitch = (noseBelow/bboxH - 0.20) * 250
        //   Examples:
        //     noseBelow/bboxH = 0.20 → pitch = 0°  (neutral)
        //     noseBelow/bboxH = 0.34 → pitch = 35° (MAX_PITCH threshold)
        //     noseBelow/bboxH = 0.12 → pitch = -20° (tilted up, CENTER range)

        var pitch = 0;
        if (bb && bb.height > 0.01) {
            var noseBelow = nose.y - eyeMidY;
            pitch = (noseBelow / bb.height - 0.20) * 250;
        } else {
            // Fallback to eye-span normalisation when bbox unavailable,
            // but apply a yaw-compensation factor to reduce the amplification.
            var noseBelow2 = nose.y - eyeMidY;
            var absYawRad  = Math.abs(yaw) * Math.PI / 180;
            var yawComp    = Math.max(0.5, Math.cos(absYawRad)); // prevent over-correction
            pitch = (noseBelow2 / (eyeSpanX / yawComp) - 0.5) * 100;
        }

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

        // Center: both axes within deadzone
        if (absYaw < CENTER_YAW && absPitch < CENTER_PITCH) return 'center';

        // Always classify to the nearest bucket — never return 'other'.
        // The camera may be permanently below or to the side of the user,
        // inflating one axis. Blocking on MAX thresholds means the system
        // never registers a valid pose. Dominant axis wins unconditionally.
        if (absYaw >= absPitch) {
            return yaw < 0 ? 'left' : 'right';
        } else {
            return pitch < 0 ? 'up' : 'down';
        }
    }

    // ==========================================================================
    // State colour — matches kiosk palette
    // ==========================================================================

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

    // ==========================================================================
    // Draw helpers
    // ==========================================================================

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

    /**
     * Pose debug badge inside the bounding box.
     * Top-right:    pose label  e.g. CENTER / LEFT / RIGHT / UP / DOWN
     * Bottom-right: Y/P angles + confidence
     */
    function drawPoseBadge(bx, by, bw, bh, col) {
        var pose = currentPose;
        if (!pose.bucket) return;

        var LABELS = {
            center: 'CENTER',
            left:   'LEFT',
            right:  'RIGHT',
            up:     'UP',
            down:   'DOWN'
        };

        var label = LABELS[pose.bucket];
        if (!label) return; // unknown bucket — draw nothing

        var debug = 'Y:' + pose.yaw + '° P:' + pose.pitch + '°  ' +
                    Math.round(pose.conf * 100) + '%';
        var pad = 6, r = 4;

        ctx.save();
        ctx.textBaseline = 'middle';

        // ── TOP-RIGHT: pose label ─────────────────────────────────────────────
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

        // ── BOTTOM-RIGHT: Y/P/confidence ──────────────────────────────────────
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

    // ==========================================================================
    // Main RAF loop — detect + smooth + draw at ~60fps
    // ==========================================================================

    function tick() {
        if (!active) return;
        requestAnimationFrame(tick);

        var dims = syncCanvas();
        var cssW = dims.w, cssH = dims.h;
        ctx.clearRect(0, 0, cssW, cssH);

        // ── 1. Detect ─────────────────────────────────────────────────────────
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
                        // Always pick the largest (closest) face
                        var best = valid.reduce(function (a, b) {
                            var aA = a.boundingBox
                                ? a.boundingBox.width * a.boundingBox.height : 0;
                            var bA = b.boundingBox
                                ? b.boundingBox.width * b.boundingBox.height : 0;
                            return aA >= bA ? a : b;
                        });

                        detectedBox = mapToCanvas(toVideoBox(best.boundingBox), cssW, cssH);
                        currentPose = estimatePose(best);

                        // Store face area in NORMALISED coords for distance check.
                        // This is independent of canvas/CSS sizing.
                        currentFaceArea = best.boundingBox
                            ? best.boundingBox.width * best.boundingBox.height
                            : 0;

                        // CAM-02: feed face centre back to camera as focus hint
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
                    // Bad frame — keep loop alive, skip this tick
                }
            }
        }

        // ── 2. EMA smooth ──────────────────────────────────────────────────────
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
            // Face lost — decay box away over ~10 frames then hide
            if (smoothed) {
                smoothed.w *= 0.92;
                smoothed.h *= 0.92;
                if (smoothed.w < 10) smoothed = null;
            }
        }

        if (!smoothed) { scanLinePos = 0; return; }

        var bx = smoothed.x, by = smoothed.y, bw = smoothed.w, bh = smoothed.h;

        // ── 3. State colour ───────────────────────────────────────────────────
        var col    = stateColor();
        var isBusy = !!(window.FaceAttendEnrollment && window.FaceAttendEnrollment.busy);

        // ── 4. Scan line while a frame is being captured ──────────────────────
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

        // ── 5. Corner brackets ────────────────────────────────────────────────
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

        // ── 6. Box outline + subtle fill ──────────────────────────────────────
        ctx.save();
        ctx.strokeStyle = col.main;
        ctx.lineWidth   = isBusy ? 2.0 : 1.5;
        ctx.globalAlpha = 0.75;
        ctx.strokeRect(bx, by, bw, bh);
        ctx.globalAlpha = 0.04;
        ctx.fillStyle   = col.main;
        ctx.fillRect(bx, by, bw, bh);
        ctx.restore();

        // ── 7. POSE-02: pose debug badge ──────────────────────────────────────
        drawPoseBadge(bx, by, bw, bh, col);

        // ── 8. Publish live box + pose for enrollment-ui.js / enrollment-core.js ─────
        if (window.FaceAttendEnrollment) {
            window.FaceAttendEnrollment.liveTrackingBox = {
                x: bx, y: by, w: bw, h: bh
            };
            // livePose is read by processScanResult() in enrollment-core.js so the
            // tracker's pose (not the server re-estimate) is stored per captured frame.
            window.FaceAttendEnrollment.livePose = {
                bucket: currentPose.bucket,
                yaw:    currentPose.yaw,
                pitch:  currentPose.pitch,
                conf:   currentPose.conf
            };
        }
    }

    // ==========================================================================
    // MediaPipe initialisation
    // ==========================================================================

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
                console.log('[enrollment-tracker] v2.0 ready — 60fps + pose debug active');
            })
            .catch(function (e) {
                console.warn('[enrollment-tracker] MediaPipe init failed:', e);
            });
    }

    function tryLoadMp() {
        if (typeof window.MpFilesetResolver === 'function') { initMp(); return; }

        // Dynamic injection fallback
        var s  = document.createElement('script');
        s.type = 'module';
        s.src  = appBase + 'Scripts/vendor/mediapipe/tasks-vision/vision_loader.mjs';
        document.head.appendChild(s);

        var attempts = 0;
        var iv = setInterval(function () {
            if (typeof window.MpFilesetResolver === 'function') {
                clearInterval(iv);
                initMp();
            } else if (++attempts > 50) {   // 10s timeout
                clearInterval(iv);
                console.warn('[enrollment-tracker] vision_loader timed out.');
            }
        }, 200);
    }

    // ==========================================================================
    // Boot — wait for camera stream, enhance focus, then start MediaPipe
    // ==========================================================================

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
            } else if (++checks > 120) {   // 60s timeout
                clearInterval(iv);
            }
        }, 500);
    }

    // Also hook the playing event in case the camera was live before this ran
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
