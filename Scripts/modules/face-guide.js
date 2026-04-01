/* FaceAttend.FaceGuide — Fixed oval face guide module
 * Used by: enrollment-tracker.js (enrollment flow) and kiosk.js (recognition flow)
 *
 * Fixed target mode (enrollment):
 *   A static portrait oval at the center of the canvas represents the ideal
 *   enrollment distance. The oval does NOT follow the face. Instead, the user
 *   moves toward/away from the camera until their face fits the oval.
 *   An entrance animation (EMA from small to full size) runs on first draw.
 *
 * States (enrollment): none → too_close → too_far → off_center → good
 *   too_close: face area > TOO_CLOSE_AREA (liveness CNN fails at close range)
 *   too_far:   face area < TOO_FAR_AREA
 *   off_center: face center outside CENTER_MARGIN band
 *   good:       face in the target zone
 */
var FaceAttend = window.FaceAttend || {};

FaceAttend.FaceGuide = (function () {
    'use strict';

    // ── Fixed oval geometry (fractions of canvas dimensions) ──────────────────
    // RX=0.25 keeps ry inside the 47% height clamp on any 4:3 canvas (640×480 video).
    // ASPECT=0.85 → ry/rx ≈ 1.18:1, matching the face's actual H:W in canvas pixels
    // (face from BlazeFace bbox is ~1.15–1.20:1 on 640×480) and keeps oval height at
    // ~78% of canvas vs 89% with ASPECT=0.75.
    var CX_RATIO = 0.50;   // horizontal center
    var CY_RATIO = 0.49;   // vertical center — ~47px top margin on 4:3 canvas with ASPECT=0.85
    var RX_RATIO = 0.25;   // horizontal radius — 50% of canvas width total
    var ASPECT   = 0.85;   // ry = rx / ASPECT  → portrait; 0.85 matches face bbox H:W on 640×480

    // ── Entrance animation EMA ─────────────────────────────────────────────────
    var OVAL_ALPHA    = 0.12;  // slow convergence → ~1s grow-in
    var INIT_RX_RATIO = 0.10;  // starting radius = 40% of RX_RATIO target
    var _ema = { cx: null, cy: null, rx: null, ry: null };

    // ── State thresholds ───────────────────────────────────────────────────────
    // Empirically: BlazeFace bbox includes ~25% padding. On a standard desktop at 50cm,
    // bb.width × bb.height reaches 0.35–0.52 with liveness=100%. Threshold must be above
    // that zone. 0.60 corresponds to face filling ~75% of 640px frame width — confirmed
    // liveness failure territory. Image #2 evidence: liveness=100% at faceArea > 0.30
    // proves the original 0.30 was wrong.
    var TOO_CLOSE_AREA = 0.60;  // face area ratio above this = liveness fails
    var TOO_FAR_AREA   = 0.09;  // face area ratio below this = amber "too far"
    var CENTER_MARGIN  = 0.15;  // face center must be within middle 70% of canvas

    // ── Colors ─────────────────────────────────────────────────────────────────
    var COLOR_NONE    = 'rgba(255,255,255,0.65)';
    var COLOR_WARNING = '#f59e0b';   // amber — too far, too close, or off-center
    var COLOR_GOOD    = '#22c55e';   // green  — good position
    var COLOR_BUSY    = '#4f9cf9';   // blue   — actively capturing

    // ─────────────────────────────────────────────────────────────────────────
    // getState
    // @param {number}      faceArea  0-1 normalized area ratio from BlazeFace bbox
    // @param {object|null} faceBox   {x, y, w, h} in canvas CSS pixels, or null
    // @param {number}      canvasW   canvas width in CSS pixels
    // @param {number}      canvasH   canvas height in CSS pixels
    // @returns {'none'|'too_close'|'too_far'|'off_center'|'good'}
    // ─────────────────────────────────────────────────────────────────────────
    function getState(faceArea, faceBox, canvasW, canvasH) {
        if (!faceBox || !(faceArea > 0)) return 'none';
        if (faceArea > TOO_CLOSE_AREA)   return 'too_close';
        if (faceArea < TOO_FAR_AREA)     return 'too_far';

        var cx = faceBox.x + faceBox.w / 2;
        var cy = faceBox.y + faceBox.h / 2;
        var mx = canvasW  * CENTER_MARGIN;
        var my = canvasH  * CENTER_MARGIN;

        if (cx < mx || cx > canvasW - mx || cy < my || cy > canvasH - my)
            return 'off_center';

        return 'good';
    }

    // ─────────────────────────────────────────────────────────────────────────
    // draw
    // Renders the fixed oval guide onto an existing 2D canvas context.
    //
    // Technique:
    //   1. Semi-transparent black fill over the entire canvas (vignette)
    //   2. destination-out ellipse fill punches a clear hole (reveals video)
    //   3. source-over ellipse stroke draws the colored border
    //   4. Optional progress arc (thin, outside the oval) for enrollment
    //
    // @param {CanvasRenderingContext2D} ctx
    // @param {number}  canvasW   canvas width  in CSS pixels
    // @param {number}  canvasH   canvas height in CSS pixels
    // @param {string}  state     return value of getState()
    // @param {number}  progress  0.0-1.0 enrollment completion (0 = skip arc)
    // @param {boolean} isBusy    true while actively capturing frames
    // ─────────────────────────────────────────────────────────────────────────
    function draw(ctx, canvasW, canvasH, state, progress, isBusy) {
        if (!ctx || !canvasW || !canvasH) return;

        // ── Fixed oval target geometry ────────────────────────────────────────
        var targetCx = canvasW * CX_RATIO;
        var targetCy = canvasH * CY_RATIO;
        var targetRx = canvasW * RX_RATIO;
        var targetRy = targetRx / ASPECT;

        // Clamp: don't let oval overflow canvas bounds
        targetRx = Math.min(targetRx, canvasW * 0.47);
        targetRy = Math.min(targetRy, canvasH * 0.47);

        // ── EMA entrance animation ────────────────────────────────────────────
        if (_ema.cx === null) {
            // First draw: start small, centered
            _ema.cx = targetCx;
            _ema.cy = targetCy;
            _ema.rx = canvasW * INIT_RX_RATIO;
            _ema.ry = _ema.rx / ASPECT;
        } else {
            _ema.cx += OVAL_ALPHA * (targetCx - _ema.cx);
            _ema.cy += OVAL_ALPHA * (targetCy - _ema.cy);
            _ema.rx += OVAL_ALPHA * (targetRx - _ema.rx);
            _ema.ry += OVAL_ALPHA * (targetRy - _ema.ry);
        }

        var cx = _ema.cx, cy = _ema.cy, rx = _ema.rx, ry = _ema.ry;

        // ── 1. Dark vignette overlay ──────────────────────────────────────────
        ctx.save();
        ctx.globalCompositeOperation = 'source-over';
        ctx.globalAlpha = 0.60;
        ctx.fillStyle   = '#000';
        ctx.fillRect(0, 0, canvasW, canvasH);
        ctx.restore();

        // ── 2. Punch oval hole through vignette ───────────────────────────────
        ctx.save();
        ctx.globalCompositeOperation = 'destination-out';
        ctx.globalAlpha = 1;
        ctx.beginPath();
        ctx.ellipse(cx, cy, rx, ry, 0, 0, Math.PI * 2);
        ctx.fill();
        ctx.restore();

        // ── 3. Oval border stroke ─────────────────────────────────────────────
        var borderColor = state === 'none'
            ? COLOR_NONE
            : (state === 'too_far' || state === 'too_close' || state === 'off_center')
                ? COLOR_WARNING
                : isBusy ? COLOR_BUSY : COLOR_GOOD;

        ctx.save();
        ctx.globalCompositeOperation = 'source-over';
        ctx.globalAlpha = 1;
        ctx.strokeStyle = borderColor;
        ctx.lineWidth   = 3;
        ctx.shadowColor = borderColor;
        ctx.shadowBlur  = (state === 'none') ? 0 : 12;
        ctx.beginPath();
        ctx.ellipse(cx, cy, rx, ry, 0, 0, Math.PI * 2);
        ctx.stroke();
        ctx.restore();

        // ── 4. Progress arc (enrollment only — pass progress=0 to skip) ───────
        if (progress > 0 && (state === 'good' || isBusy)) {
            var endAngle = -Math.PI / 2 + Math.PI * 2 * Math.min(progress, 1);
            ctx.save();
            ctx.globalCompositeOperation = 'source-over';
            ctx.globalAlpha = 0.85;
            ctx.strokeStyle = COLOR_GOOD;
            ctx.lineWidth   = 4;
            ctx.lineCap     = 'round';
            ctx.shadowColor = COLOR_GOOD;
            ctx.shadowBlur  = 6;
            ctx.beginPath();
            ctx.ellipse(cx, cy, rx + 7, ry + 7, 0, -Math.PI / 2, endAngle);
            ctx.stroke();
            ctx.restore();
        }
    }

    return {
        getState : getState,
        draw     : draw,

        // Expose constants for callers that want to compute oval bounds
        CX_RATIO : CX_RATIO,
        CY_RATIO : CY_RATIO,
        RX_RATIO : RX_RATIO,
        ASPECT   : ASPECT
    };
})();

window.FaceAttend = FaceAttend;
