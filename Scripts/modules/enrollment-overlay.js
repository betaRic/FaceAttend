// enrollment-overlay.js
// Canvas bounding-box overlay for enrollment views.
// Replaces the oval/ellipse guide with corner-bracket + rectangle feedback.
// Uses corner bracket logic from kiosk-canvas.js, adapted for enrollment canvas coords.
// Exposes FaceAttend.EnrollOverlay — must be loaded after face-guide.js.

var FaceAttend = window.FaceAttend || {};

FaceAttend.EnrollOverlay = (function () {
    'use strict';

    var _canvas = null;
    var _ctx    = null;

    // ── Colors ────────────────────────────────────────────────────────────────────

    function resolveColor(guideState, done, target) {
        if (!guideState || guideState === 'none') return { main: '#6b7280', glow: 'rgba(107,114,128,0.40)' };
        if (guideState === 'too_close')           return { main: '#f59e0b', glow: 'rgba(245,158,11,0.55)' };
        if (guideState === 'too_far')             return { main: '#f59e0b', glow: 'rgba(245,158,11,0.55)' };
        if (guideState === 'off_center')          return { main: '#fbbf24', glow: 'rgba(251,191,36,0.45)' };
        if (done >= target)                       return { main: '#22c55e', glow: 'rgba(34,197,94,0.55)'  };
        // good position, capturing
        return { main: '#34d399', glow: 'rgba(52,211,153,0.50)' };
    }

    // ── Corner brackets ───────────────────────────────────────────────────────────

    function drawCornerBrackets(bx, by, bw, bh, color, glowColor, lineWidth) {
        var L = Math.min(bw, bh) * 0.20;
        _ctx.save();
        _ctx.strokeStyle = color;
        _ctx.lineWidth   = lineWidth;
        _ctx.lineCap     = 'round';
        _ctx.shadowColor = glowColor;
        _ctx.shadowBlur  = 14;

        // top-left
        _ctx.beginPath();
        _ctx.moveTo(bx, by + L);
        _ctx.lineTo(bx, by);
        _ctx.lineTo(bx + L, by);
        _ctx.stroke();

        // top-right
        _ctx.beginPath();
        _ctx.moveTo(bx + bw - L, by);
        _ctx.lineTo(bx + bw, by);
        _ctx.lineTo(bx + bw, by + L);
        _ctx.stroke();

        // bottom-left
        _ctx.beginPath();
        _ctx.moveTo(bx, by + bh - L);
        _ctx.lineTo(bx, by + bh);
        _ctx.lineTo(bx + L, by + bh);
        _ctx.stroke();

        // bottom-right
        _ctx.beginPath();
        _ctx.moveTo(bx + bw - L, by + bh);
        _ctx.lineTo(bx + bw, by + bh);
        _ctx.lineTo(bx + bw, by + bh - L);
        _ctx.stroke();

        _ctx.restore();
    }

    // ── Public API ────────────────────────────────────────────────────────────────

    /**
     * init(canvas) — call once when the enrollment canvas is available.
     */
    function init(canvas) {
        _canvas = canvas;
        _ctx    = canvas ? canvas.getContext('2d') : null;
    }

    /**
     * draw(smoothedBox, guideState, done, target)
     *   smoothedBox — { x, y, w, h } in canvas CSS pixel coords (from enrollment-tracker)
     *   guideState  — 'none' | 'too_close' | 'too_far' | 'off_center' | 'good'
     *   done        — number of good frames captured so far
     *   target      — minGoodFrames target
     */
    function draw(smoothedBox, guideState, done, target) {
        if (!_ctx || !_canvas) return;

        // Sync canvas internal resolution to CSS size
        var w = _canvas.clientWidth  || _canvas.width;
        var h = _canvas.clientHeight || _canvas.height;
        if (_canvas.width !== w || _canvas.height !== h) {
            _canvas.width  = w;
            _canvas.height = h;
        }

        _ctx.clearRect(0, 0, _canvas.width, _canvas.height);

        if (!smoothedBox || smoothedBox.w < 4) return;

        var colors = resolveColor(guideState, done || 0, target || 1);

        var bx = smoothedBox.x;
        var by = smoothedBox.y;
        var bw = smoothedBox.w;
        var bh = smoothedBox.h;

        // Subtle rectangle border
        _ctx.save();
        _ctx.strokeStyle = colors.main;
        _ctx.globalAlpha = 0.75;
        _ctx.lineWidth   = 2.0;
        _ctx.strokeRect(bx, by, bw, bh);
        _ctx.restore();

        // Corner brackets with glow
        drawCornerBrackets(bx, by, bw, bh, colors.main, colors.glow, 2.5);
    }

    /**
     * clear() — erase overlay (e.g. when camera stops).
     */
    function clear() {
        if (_ctx && _canvas) _ctx.clearRect(0, 0, _canvas.width, _canvas.height);
    }

    return { init: init, draw: draw, clear: clear };
})();
