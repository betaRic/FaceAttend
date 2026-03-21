/**
 * enrollment-tracking.js
 * Real-time 60fps face tracking for the enrollment UI.
 *
 * WHY THIS EXISTS:
 *   enrollment-ui.js draws the bounding box from enrollment.lastFaceBox,
 *   which only updates when the server scan responds (~300ms / 3fps).
 *   The RAF draw loop runs at 60fps but the input is 3fps — EMA can smooth
 *   the jumps but can't invent frames that were never detected.
 *
 *   This module adds a parallel MediaPipe detection loop (identical to the
 *   kiosk's mp.tick pipeline) that writes enrollment.liveTrackingBox at 60fps.
 *   drawFaceOverlay in enrollment-ui.js prefers that value and falls back to
 *   the server box when MediaPipe is unavailable.
 *
 * PIPELINE:
 *   MpFaceDetectorTask.detectForVideo (every RAF, ~60fps)
 *     → toVideoBox       — denormalize + face-centering shift (same as kiosk)
 *     → EMA smooth       — α = 0.35, same as kiosk state.smoothedBox
 *     → mapToCanvas      — object-fit:cover scale + mirror flip X
 *     → liveTrackingBox  — CSS-pixel coords consumed by drawFaceOverlay
 *
 * GRACEFUL DEGRADE:
 *   If MediaPipe WASM/model is unavailable (enrollment page doesn't load the
 *   vision_loader module), this module exits silently and enrollment-ui.js
 *   falls back to its existing server-box behavior. Nothing breaks.
 *
 * LOADING MP ON ENROLLMENT PAGES:
 *   The kiosk view already loads:
 *     <script type="module" src="~/Scripts/vendor/mediapipe/tasks-vision/vision_loader.mjs">
 *   Add the same tag to your Admin/Enroll.cshtml and Mobile Enroll.cshtml layouts,
 *   OR let this module dynamically inject it (handled below via tryLoadMp).
 */
(function () {
    'use strict';

    // Only activate on pages that have an enrollment component
    var root = document.getElementById('enrollRoot');
    if (!root) return;

    var video  = document.getElementById('enrollVideo');
    var canvas = document.getElementById('enrollFaceCanvas');
    if (!video || !canvas) return;

    var appBase = (document.body.getAttribute('data-app-base') || '/').replace(/\/?$/, '/');

    // =========================================================================
    // State
    // =========================================================================

    var detector    = null;   // MpFaceDetectorTask instance
    var smoothedBox = null;   // EMA-smoothed box in CSS-pixel space
    var loopActive  = false;  // set true once detector is ready
    var lastTs      = -1;     // last performance.now() passed to detectForVideo

    // Exactly the same constants as kiosk CFG
    var ALPHA      = 0.35;    // EMA alpha — matches state.smoothedBox in kiosk.js
    var MIN_CONF   = 0.30;    // minimum detection confidence
    var SHIFT_UP   = 0.20;    // shift box up by this fraction of height (kiosk: 0.20)
    var EXPAND_H   = 0.08;    // expand box height by this fraction (kiosk: 0.08)

    // =========================================================================
    // Coordinate helpers — direct ports from kiosk.js
    // =========================================================================

    /**
     * Convert a MediaPipe BoundingBox to video-pixel coords.
     * Handles both normalised (0-1) and already-pixel inputs.
     * Applies the same face-centering adjustment the kiosk uses.
     */
    function toVideoBox(bb) {
        if (!bb) return null;

        var isNorm = bb.width <= 1.5 && bb.height <= 1.5 &&
                     bb.originX <= 1.5 && bb.originY <= 1.5;

        var x = isNorm ? bb.originX * video.videoWidth  : bb.originX;
        var y = isNorm ? bb.originY * video.videoHeight : bb.originY;
        var w = isNorm ? bb.width   * video.videoWidth  : bb.width;
        var h = isNorm ? bb.height  * video.videoHeight : bb.height;

        // Shift box up so forehead is included, not clipped
        y -= h * SHIFT_UP;
        h += h * EXPAND_H;

        return { x: x, y: y, w: w, h: h };
    }

    /**
     * Map a video-pixel box to CSS-pixel canvas coords.
     *
     * Handles object-fit: cover scaling (the same Math.max formula kiosk uses)
     * and the X mirror flip needed because .enroll-video has transform:scaleX(-1).
     *
     * Returns coords in CSS pixels so drawFaceOverlay (which scales its context
     * by DPR) can use them directly without further scaling.
     *
     * @param  {object} vbox  — {x, y, w, h} in video pixel space
     * @param  {number} cssW  — canvas.offsetWidth  (CSS pixels)
     * @param  {number} cssH  — canvas.offsetHeight (CSS pixels)
     * @returns {object|null}
     */
    function mapToCanvas(vbox, cssW, cssH) {
        if (!vbox || !video.videoWidth || !video.videoHeight) return null;

        var imgW = video.videoWidth;
        var imgH = video.videoHeight;

        // object-fit: cover — scale to fill, may crop
        var scale   = Math.max(cssW / imgW, cssH / imgH);
        var renderW = imgW * scale;
        var renderH = imgH * scale;
        var offX    = (cssW - renderW) / 2;
        var offY    = (cssH - renderH) / 2;

        var x = offX + vbox.x * scale;
        var y = offY + vbox.y * scale;
        var w = vbox.w * scale;
        var h = vbox.h * scale;

        // Mirror flip: canvas must match scaleX(-1) applied to the video element
        x = cssW - (x + w);

        return { x: x, y: y, w: w, h: h };
    }

    /**
     * Ensure the canvas pixel dimensions match the display at the current DPR.
     * Returns the CSS-pixel dimensions for use in coordinate math.
     */
    function syncCanvas() {
        var dpr  = window.devicePixelRatio || 1;
        var cssW = canvas.offsetWidth  || 640;
        var cssH = canvas.offsetHeight || 480;
        var pw   = Math.round(cssW * dpr);
        var ph   = Math.round(cssH * dpr);
        if (canvas.width !== pw || canvas.height !== ph) {
            canvas.width  = pw;
            canvas.height = ph;
        }
        return { w: cssW, h: cssH };
    }

    // =========================================================================
    // Detection loop — runs via requestAnimationFrame at ~60fps
    // =========================================================================

    function tick() {
        if (!loopActive) return;
        requestAnimationFrame(tick);

        if (!detector || !video.videoWidth || !video.videoHeight) return;

        // detectForVideo requires a strictly increasing timestamp
        var ts = Math.floor(performance.now());
        if (ts <= lastTs) return;
        lastTs = ts;

        var result, dets, valid;
        try {
            result = detector.detectForVideo(video, ts);
            dets   = result && result.detections ? result.detections : [];
            valid  = dets.filter(function (d) {
                return ((d.categories && d.categories[0] && d.categories[0].score) || 0) >= MIN_CONF;
            });
        } catch (e) {
            return; // MediaPipe sometimes throws on bad frames; keep loop alive
        }

        if (!valid.length) {
            // No face — clear smoothed box so drawing code knows to hide the overlay
            smoothedBox = null;
            if (window.FaceAttendEnrollment) {
                window.FaceAttendEnrollment.liveTrackingBox = null;
            }
            return;
        }

        // Always pick the LARGEST face (closest person) — same as kiosk
        var best = valid.reduce(function (a, b) {
            var aArea = a.boundingBox ? a.boundingBox.width * a.boundingBox.height : 0;
            var bArea = b.boundingBox ? b.boundingBox.width * b.boundingBox.height : 0;
            return aArea >= bArea ? a : b;
        });

        var dims = syncCanvas();
        var cbox = mapToCanvas(toVideoBox(best.boundingBox), dims.w, dims.h);
        if (!cbox) return;

        // EMA smoothing — α=0.35 gives 5-6 frames of lag, imperceptible but jitter-free
        if (!smoothedBox) {
            smoothedBox = { x: cbox.x, y: cbox.y, w: cbox.w, h: cbox.h };
        } else {
            smoothedBox.x += ALPHA * (cbox.x - smoothedBox.x);
            smoothedBox.y += ALPHA * (cbox.y - smoothedBox.y);
            smoothedBox.w += ALPHA * (cbox.w - smoothedBox.w);
            smoothedBox.h += ALPHA * (cbox.h - smoothedBox.h);
        }

        // Publish for drawFaceOverlay to consume
        if (window.FaceAttendEnrollment) {
            window.FaceAttendEnrollment.liveTrackingBox = {
                x: smoothedBox.x,
                y: smoothedBox.y,
                w: smoothedBox.w,
                h: smoothedBox.h
            };
        }
    }

    // =========================================================================
    // MediaPipe initialisation
    // =========================================================================

    function buildDetector(vision) {
        var opts = {
            baseOptions: {
                modelAssetPath: appBase + 'Scripts/vendor/mediapipe/tasks-vision/models/blaze_face_short_range.tflite',
                delegate: 'GPU'
            },
            runningMode:              'VIDEO',
            minDetectionConfidence:   MIN_CONF,
            minSuppressionThreshold:  0.3
        };

        return window.MpFaceDetectorTask.createFromOptions(vision, opts)
            .catch(function () {
                // GPU delegate failed (no WebGL / iOS Safari) — try CPU
                opts.baseOptions.delegate = 'CPU';
                return window.MpFaceDetectorTask.createFromOptions(vision, opts);
            });
    }

    function initMp() {
        if (typeof window.MpFilesetResolver  !== 'function' ||
            typeof window.MpFaceDetectorTask !== 'function') {
            return; // MP globals not present — degrade silently
        }

        var wasmBase = appBase + 'Scripts/vendor/mediapipe/tasks-vision/wasm';

        window.MpFilesetResolver.forVisionTasks(wasmBase)
            .then(function (vision) { return buildDetector(vision); })
            .then(function (det) {
                detector   = det;
                loopActive = true;
                requestAnimationFrame(tick);
            })
            .catch(function () {
                // Model / WASM load failed — enrollment-ui.js fallback handles it
            });
    }

    /**
     * If the MediaPipe vision_loader module isn't already on the page
     * (only kiosk view includes it by default), inject it and wait.
     * The enrollment view can also add:
     *   <script type="module" src="~/Scripts/vendor/mediapipe/tasks-vision/vision_loader.mjs">
     * to skip this dynamic-load path.
     */
    function tryLoadMp() {
        if (typeof window.MpFilesetResolver === 'function') {
            initMp();
            return;
        }

        // Inject the module script
        var s = document.createElement('script');
        s.type = 'module';
        s.src  = appBase + 'Scripts/vendor/mediapipe/tasks-vision/vision_loader.mjs';
        document.head.appendChild(s);

        // Poll for MpFilesetResolver to appear (the module sets it on window)
        var attempts = 0;
        var iv = setInterval(function () {
            if (typeof window.MpFilesetResolver === 'function') {
                clearInterval(iv);
                initMp();
            } else if (++attempts > 40) {   // 8 seconds max
                clearInterval(iv);           // give up silently
            }
        }, 200);
    }

    // =========================================================================
    // Wait for the enrollment camera to have a live stream before initialising
    // =========================================================================

    function waitForCamera() {
        if (video.videoWidth > 0) {
            tryLoadMp();
            return;
        }
        var checks = 0;
        var iv = setInterval(function () {
            if (video.videoWidth > 0) {
                clearInterval(iv);
                tryLoadMp();
            } else if (++checks > 60) {     // 30 seconds max
                clearInterval(iv);
            }
        }, 500);
    }

    // Give the enrollment camera a head-start before we try to attach
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', function () {
            setTimeout(waitForCamera, 800);
        });
    } else {
        setTimeout(waitForCamera, 800);
    }

    // =========================================================================
    // Cleanup on page unload
    // =========================================================================

    window.addEventListener('beforeunload', function () {
        loopActive = false;
        if (detector) {
            try { detector.close(); } catch (e) {}
            detector = null;
        }
    });

})();
