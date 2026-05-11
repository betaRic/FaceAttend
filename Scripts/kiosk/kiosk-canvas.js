// kiosk-canvas.js
// Canvas overlay: face bounding box, corner brackets, scan line animation.
// Exposes window.KioskCanvas — must be loaded after kiosk-state.js.
(function () {
    'use strict';

    var _video  = null;
    var _canvas = null;
    var _ctx    = null;
    var _state  = null;
    var _ui     = null;

    // ── Resize ─────────────────────────────────────────────────────────────────

    function resizeCanvas() {
        var w = _canvas.clientWidth;
        var h = _canvas.clientHeight;
        if (_canvas.width !== w || _canvas.height !== h) {
            _canvas.width  = w;
            _canvas.height = h;
        }
    }

    // ── Coordinate mapping ─────────────────────────────────────────────────────

    function mapVideoBoxToCanvas(vbox) {
        if (!vbox || !_video.videoWidth || !_video.videoHeight) return null;
        var W = _canvas.width, H = _canvas.height;
        var imgW = _video.videoWidth, imgH = _video.videoHeight;
        var scale   = Math.max(W / imgW, H / imgH);
        var renderW = imgW * scale, renderH = imgH * scale;
        var offX    = (W - renderW) / 2, offY = (H - renderH) / 2;
        var x = offX + vbox.x * scale;
        var y = offY + vbox.y * scale;
        var w = vbox.w * scale;
        var h = vbox.h * scale;
        x = W - (x + w); // mirror to match scaleX(-1) on video
        return { x: x, y: y, w: w, h: h };
    }

    // ── Draw helpers ───────────────────────────────────────────────────────────

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

    function drawScanLine(bx, by, bw, bh, progress, color) {
        if (progress <= 0) return;
        var scanY = by + bh * progress;
        var grad  = _ctx.createLinearGradient(bx, scanY, bx + bw, scanY);
        grad.addColorStop(0,    'rgba(0,0,0,0)');
        grad.addColorStop(0.25, color);
        grad.addColorStop(0.5,  color);
        grad.addColorStop(0.75, color);
        grad.addColorStop(1,    'rgba(0,0,0,0)');

        _ctx.save();
        _ctx.globalAlpha = 0.55;
        _ctx.strokeStyle = grad;
        _ctx.lineWidth   = 1.5;
        _ctx.shadowColor = color;
        _ctx.shadowBlur  = 8;
        _ctx.beginPath();
        _ctx.moveTo(bx, scanY);
        _ctx.lineTo(bx + bw, scanY);
        _ctx.stroke();
        _ctx.restore();
    }

    // ── Draw loop ──────────────────────────────────────────────────────────────

    function drawLoop() {
        resizeCanvas();
        _ctx.clearRect(0, 0, _canvas.width, _canvas.height);

        if (_state.mpBoxCanvas) {
            var scanning = _state.liveInFlight;
            var good     = _state.faceStatus === 'good';

            // color based on antiSpoof status (priority over scanning/good states)
            var mainColor, glowColor;
            var antiSpoofCls = _ui.antiSpoofLine ? (_ui.antiSpoofLine.className || '') : '';

            if (antiSpoofCls.indexOf('live-fail') >= 0) {
                mainColor = '#ef4444';
                glowColor = 'rgba(239,68,68,0.60)';
            } else if (antiSpoofCls.indexOf('live-near') >= 0) {
                mainColor = '#f59e0b';
                glowColor = 'rgba(245,158,11,0.60)';
            } else if (antiSpoofCls.indexOf('live-pass') >= 0) {
                mainColor = '#22c55e';
                glowColor = 'rgba(34,197,94,0.60)';
            } else if (scanning) {
                mainColor = '#4f9cf9';
                glowColor = 'rgba(79,156,249,0.70)';
            } else if (good) {
                mainColor = '#34d399';
                glowColor = 'rgba(52,211,153,0.60)';
            } else {
                mainColor = '#fbbf24';
                glowColor = 'rgba(251,191,36,0.50)';
            }

            var b = _state.mpBoxCanvas;

            // animated scan line while in flight
            if (scanning) {
                _state.scanLineProgress = (_state.scanLineProgress + 0.016) % 1.0;
                drawScanLine(b.x, b.y, b.w, b.h, _state.scanLineProgress, mainColor);
            } else {
                _state.scanLineProgress = 0;
            }

            // corner brackets -- thicker when scanning
            var lw = scanning ? 3.5 : 2.5;
            drawCornerBrackets(b.x, b.y, b.w, b.h, mainColor, glowColor, lw);
            _ctx.save();
            _ctx.strokeStyle = mainColor;
            _ctx.globalAlpha = 0.95;
            _ctx.lineWidth   = scanning ? 2.5 : 2.0;
            _ctx.strokeRect(b.x, b.y, b.w, b.h);
            _ctx.restore();
        }

        requestAnimationFrame(drawLoop);
    }

    // ── Init ───────────────────────────────────────────────────────────────────

    function init(videoEl, canvasEl, stateRef, uiRef) {
        _video  = videoEl;
        _canvas = canvasEl;
        _ctx    = canvasEl.getContext('2d');
        _state  = stateRef;
        _ui     = uiRef;
    }

    window.KioskCanvas = {
        init:                init,
        start:               function () { drawLoop(); },
        resizeCanvas:        resizeCanvas,
        mapVideoBoxToCanvas: mapVideoBoxToCanvas
    };
})();
