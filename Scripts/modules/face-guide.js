/* FaceAttend.FaceGuide — Shared oval face guide module
 * Used by: enrollment-tracker.js (enrollment flow) and kiosk.js (recognition flow)
 * Draws a fixed portrait oval on a canvas with dark vignette + state-colored border.
 * The oval is FIXED — the user moves their face to fill it (industry standard).
 */
var FaceAttend = window.FaceAttend || {};

FaceAttend.FaceGuide = (function () {
    'use strict';

    // ── Oval geometry (fractions of canvas dimensions) ──────────────────────
    var CX_RATIO = 0.50;   // horizontal center
    var CY_RATIO = 0.46;   // vertical center (slightly above midpoint)
    var RX_RATIO = 0.38;   // horizontal radius
    var ASPECT   = 0.75;   // ry = rx / ASPECT  → portrait (taller than wide)

    // ── State thresholds ─────────────────────────────────────────────────────
    var TOO_FAR_AREA  = 0.09;  // face area ratio below this = amber "too far"
    var CENTER_MARGIN = 0.20;  // face center must be within middle 60% of canvas

    // ── Colors ───────────────────────────────────────────────────────────────
    var COLOR_NONE    = 'rgba(255,255,255,0.65)';
    var COLOR_WARNING = '#f59e0b';   // amber — too far or off-center
    var COLOR_GOOD    = '#22c55e';   // green  — good position
    var COLOR_BUSY    = '#4f9cf9';   // blue   — actively capturing

    // ─────────────────────────────────────────────────────────────────────────
    // getState
    // Determines the current guide state from face detection data.
    //
    // @param {number}      faceArea  0-1 normalized area ratio from BlazeFace bbox
    // @param {object|null} faceBox   {x, y, w, h} in canvas CSS pixels, or null
    // @param {number}      canvasW   canvas width in CSS pixels
    // @param {number}      canvasH   canvas height in CSS pixels
    // @returns {'none'|'too_far'|'off_center'|'good'}
    // ─────────────────────────────────────────────────────────────────────────
    function getState(faceArea, faceBox, canvasW, canvasH) {
        if (!faceBox || !(faceArea > 0)) return 'none';
        if (faceArea < TOO_FAR_AREA) return 'too_far';

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
    // Renders the oval guide onto an existing 2D canvas context.
    // Call this at the START of each frame draw, before any other overlays,
    // so brackets/badges appear on top of the oval.
    //
    // Technique:
    //   1. Semi-transparent black fill over the entire canvas (vignette)
    //   2. destination-out ellipse fill punches a clear hole (reveals video)
    //   3. source-over ellipse stroke draws the colored border
    //   4. Optional progress arc (thin, outside the oval) for enrollment
    //
    // @param {CanvasRenderingContext2D} ctx
    // @param {number} canvasW       canvas width  in CSS pixels
    // @param {number} canvasH       canvas height in CSS pixels
    // @param {string} state         return value of getState()
    // @param {number} progress      0.0-1.0 enrollment completion (0 = skip arc)
    // @param {boolean} isBusy       true while actively capturing frames
    // ─────────────────────────────────────────────────────────────────────────
    function draw(ctx, canvasW, canvasH, state, progress, isBusy) {
        if (!ctx || !canvasW || !canvasH) return;

        var cx = canvasW * CX_RATIO;
        var cy = canvasH * CY_RATIO;
        var rx = canvasW * RX_RATIO;
        var ry = rx / ASPECT;

        // 1. Dark vignette overlay
        ctx.save();
        ctx.globalCompositeOperation = 'source-over';
        ctx.globalAlpha = 0.60;
        ctx.fillStyle   = '#000';
        ctx.fillRect(0, 0, canvasW, canvasH);
        ctx.restore();

        // 2. Punch oval hole through vignette
        ctx.save();
        ctx.globalCompositeOperation = 'destination-out';
        ctx.globalAlpha = 1;
        ctx.beginPath();
        ctx.ellipse(cx, cy, rx, ry, 0, 0, Math.PI * 2);
        ctx.fill();
        ctx.restore();

        // 3. Oval border stroke
        var borderColor = state === 'none'
            ? COLOR_NONE
            : (state === 'too_far' || state === 'off_center')
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

        // 4. Progress arc (enrollment only — pass progress=0 to skip)
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
