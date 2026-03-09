(function () {
    'use strict';

    // =========
    // dom refs
    // =========
    var el = function (id) { return document.getElementById(id); };

    var video  = el('kioskVideo');
    var canvas = el('overlayCanvas');
    var ctx    = canvas.getContext('2d');

            var ui = {
        officeLine:        el('officeLine'),
        timeLine:          el('timeLine'),
        dateLine:          el('dateLine'),
        livenessLine:      el('livenessLine'),
        scanEtaLine:       el('scanEtaLine'),

        unlockBackdrop:    el('unlockBackdrop'),
        unlockPin:         el('unlockPin'),
        unlockErr:         el('unlockErr'),
        unlockCancel:      el('unlockCancel'),
        unlockSubmit:      el('unlockSubmit'),
        unlockClose:       el('unlockClose'),

        visitorBackdrop:   el('visitorBackdrop'),
        visitorNameRow:    el('visitorNameRow'),
        visitorName:       el('visitorName'),
        visitorPurpose:    el('visitorPurpose'),
        visitorErr:        el('visitorErr'),
        visitorCancel:     el('visitorCancel'),
        visitorSubmit:     el('visitorSubmit'),
        visitorClose:      el('visitorClose'),

        kioskRoot:         el('kioskRoot'),
        idleOverlay:       el('idleOverlay'),
        idleClock:         el('idleClock'),
        idleDate:          el('idleDate'),
        idleOrgName:       el('idleOrgName'),
        idleStatusBadge:   el('idleStatusBadge'),
        idleLocationTitle: el('idleLocationTitle'),
        idleLocationSub:   el('idleLocationSub'),
        centerBlock:       el('centerBlock'),
        centerBlockTitle:  el('centerBlockTitle'),
        centerBlockSub:    el('centerBlockSub'),
        mainPrompt:        el('mainPrompt'),
        subPrompt:         el('subPrompt'),
    };

    var token        = (document.querySelector('input[name="__RequestVerificationToken"]') || {}).value || '';
    var appBase      = (document.body.getAttribute('data-app-base') || '/').replace(/\/?$/, '/');
    var nextGenEnabled = (document.body.getAttribute('data-nextgen') || 'false').toLowerCase() === 'true';

    // =========
    // config  -- all timings optimised vs original
    // =========
    var CFG = {
        debug: false,

        // OPT-01: 60ms loop (was 120) -- 2x faster face detection response
        loopMs: 60,

        mp: {
            detectMinConf:     0.30,
            acceptMinScore:    0.60,
            stableFramesMin:   2,
            // OPT-02: 150ms stable hold (was 250) -- fires 100ms sooner
            stableNeededMs:    100,
            multiMinAreaRatio: 0.015,
        },

        idle: {
            // OPT-03: 200ms sense (was 250)
            senseMs:    200,
            // OPT-04: 1800ms lost timeout (was 2000)
            faceLostMs: 1800,
            motionMin:  2.0,
        },

        server: {
            // OPT-05: 900ms resolve interval (was 1200)
            resolveMs:         10000,
            // OPT-06: 2500ms cooldown (was 3000) -- kiosk ready 500ms sooner
            captureCooldownMs: 2500,
        },

        postScan: {
            // OPT-07: 3500ms hold (was 5000) -- 1.5s faster return to ready
            holdMs:   3500,
            toastMs:  6500,
        },

        gating: {
            // OPT-08: 3 stable frames required (was 4)
            stableFramesRequired: 3,
            stableMaxMovePx:      30,
            minFaceAreaRatio:     0.03,
            safeEdgeMarginRatio:  0.02,
            centerMin:            0.12,
            centerMax:            0.88,
        },

        antiSpoof: {
            motionW:       64,
            motionH:       48,
            motionWindow:  6,
            motionDiffMin: 1.2,
        },

        tasksVision: {
            wasmBase:  appBase + 'Scripts/vendor/mediapipe/tasks-vision/wasm',
            modelPath: appBase + 'Scripts/vendor/mediapipe/tasks-vision/models/blaze_face_short_range.tflite',
        },
    };

    // =========
    // config validation
    // =========
    function validateConfig() {
        var errors = [];
        if (!CFG.loopMs || CFG.loopMs <= 0)
            errors.push('CFG.loopMs is missing or <= 0');
        if (!CFG.server || !CFG.server.captureCooldownMs)
            errors.push('CFG.server.captureCooldownMs is missing');
        if (!CFG.server || !CFG.server.resolveMs)
            errors.push('CFG.server.resolveMs is missing');
        if (!CFG.mp || !CFG.mp.stableNeededMs)
            errors.push('CFG.mp.stableNeededMs is missing');

        ['kioskVideo','overlayCanvas','kioskRoot','mainPrompt','subPrompt'].forEach(function (id) {
            if (!document.getElementById(id))
                errors.push('Missing DOM element #' + id);
        });

        if (errors.length > 0) {
            var root = document.getElementById('kioskRoot') || document.body;
            var div  = document.createElement('div');
            div.style.cssText = 'position:fixed;top:0;left:0;right:0;padding:1rem;background:#c0392b;color:#fff;font-family:monospace;font-size:.85rem;z-index:99999;white-space:pre-wrap';
            div.textContent = 'KIOSK CONFIG ERROR -- scan loop will NOT start:\n\n' + errors.join('\n');
            root.insertAdjacentElement('afterbegin', div);
            console.error('[FaceAttend] Config validation failed:', errors);
            return false;
        }
        return true;
    }

    function log() {
        if (CFG.debug) {
            var args = Array.prototype.slice.call(arguments);
            args.unshift('[FaceAttend]');
            console.log.apply(console, args);
        }
    }

    // =========
    // endpoints
    // =========
    var EP = {
        unlockPin:     appBase + 'Kiosk/UnlockPin',
        resolveOffice: appBase + 'Kiosk/ResolveOffice',
        attend:        appBase + 'Kiosk/Attend',
        submitVisitor: appBase + 'Kiosk/SubmitVisitor',
    };

    // =========
    // state
    // =========
    var ua       = navigator.userAgent || '';
    var isMobile = /Android|iPhone|iPad|iPod|Mobile|Tablet/i.test(ua);

            var state = {
        unlockOpen:      false,
        wasIdle:         true,
        visitorOpen:     false,
        pendingVisitor:  null,
        scanBlockUntil:  0,

        gps:             { lat: null, lon: null, accuracy: null },
        allowedArea:     false,
        locationState:   'pending',
        locationBanner:  'Checking location...',
        locationTitle:   'Preparing kiosk',
        locationSub:     'Please wait while the kiosk verifies the current office location.',
        currentOffice:   { id: null, name: null },
        lastResolveAt:   0,

        backoffUntil:    0,
        lastCaptureAt:   0,

        mpMode:          'none',
        mpReadyToFire:   false,
        mpStableStart:   0,
        mpFaceSeenAt:    0,
        faceStatus:      'none',
        mpRawCount:      0,
        mpAcceptedCount: 0,
        mpBoxCanvas:     null,
        mpPrevCenter:    null,

        latestLiveness:    null,
        livenessThreshold: 0.75,

        motionDiffNow:   null,
        frameDiffs:      [],

        liveInFlight:    false,
        attendAbortCtrl: null,

        localSeenAt:     0,
        localPresent:    false,

        scanLineProgress: 0,
    };

    // =========
    // helpers
    // =========
    function pushLimited(arr, v, max) {
        if (typeof v !== 'number' || !isFinite(v)) return;
        arr.push(v);
        while (arr.length > max) arr.shift();
    }

    function setPrompt(a, b) {
        if (ui.mainPrompt) ui.mainPrompt.textContent = a || '';
        if (ui.subPrompt)  ui.subPrompt.textContent  = b || '';
    }

            function safeSetPrompt(a, b) {
        if (state.liveInFlight) return;
        if ((Date.now() - state.lastCaptureAt) < 800) return;
        setPrompt(a, b);
    }

    function setCenterBlock(title, sub, show) {
        if (ui.centerBlock) ui.centerBlock.classList.toggle('hidden', !show);
        if (ui.centerBlockTitle) ui.centerBlockTitle.textContent = title || '';
        if (ui.centerBlockSub) ui.centerBlockSub.textContent = sub || '';
    }

    function humanizeResolveError(code, retryAfter, requiredAccuracy) {
        var c = (code || '').toString().toUpperCase();

        if (c === 'RATE_LIMIT_EXCEEDED') {
            return {
                title: 'Location check is busy',
                sub: 'The kiosk is verifying too often. Please wait a moment and try again.',
                banner: 'Checking location...'
            };
        }

        if (c === 'GPS_REQUIRED') {
            return {
                title: 'Location is required',
                sub: 'Enable location services so the kiosk can verify the assigned office.',
                banner: 'Location required'
            };
        }

        if (c === 'GPS_ACCURACY') {
            return {
                title: 'Location is not accurate enough',
                sub: requiredAccuracy
                    ? ('Move to an open area and wait until accuracy is within ' + requiredAccuracy + ' meters.')
                    : 'Move to an open area and try again.',
                banner: 'Accuracy too low'
            };
        }

        if (c === 'NO_OFFICES') {
            return {
                title: 'No active office is configured',
                sub: 'Please contact the system administrator.',
                banner: 'Office not configured'
            };
        }

        if (c === 'NO_OFFICE_NEARBY') {
            return {
                title: 'Outside allowed office area',
                sub: 'Move inside the DILG Region XII office radius to continue.',
                banner: 'Not in allowed area'
            };
        }

        if (c === 'GPS_DENIED') {
            return {
                title: 'Location access denied',
                sub: 'Allow location access to continue using the kiosk.',
                banner: 'Location denied'
            };
        }

        if (c === 'GPS_UNAVAILABLE') {
            return {
                title: 'Location unavailable',
                sub: 'The device could not detect the current location. Move to an open area and try again.',
                banner: 'Location unavailable'
            };
        }

        if (c === 'GPS_TIMEOUT') {
            return {
                title: 'Location request timed out',
                sub: 'Please wait a moment and try again.',
                banner: 'Location timeout'
            };
        }

        return {
            title: 'Unable to verify location',
            sub: 'Please wait a moment, then try again or contact the system administrator.',
            banner: 'Location check failed'
        };
    }

    

    function setCenterBlock(title, sub, show) {
        if (ui.centerBlock) ui.centerBlock.classList.toggle('hidden', !show);
        if (ui.centerBlockTitle) ui.centerBlockTitle.textContent = title || '';
        if (ui.centerBlockSub) ui.centerBlockSub.textContent = sub || '';
    }

    function applyLocationUi() {
        var kind = state.locationState || 'pending';
        var orgName = (document.body.getAttribute('data-org-name') || 'DILG Region XII');

        if (ui.idleOrgName) ui.idleOrgName.textContent = orgName;
        if (ui.kioskRoot) ui.kioskRoot.setAttribute('data-location-state', kind);
        if (document.body) document.body.setAttribute('data-location-state', kind);

        if (ui.officeLine) {
            ui.officeLine.textContent = state.currentOffice.name || state.locationBanner || 'Checking location...';
        }

        if (ui.idleStatusBadge) {
            ui.idleStatusBadge.classList.remove('is-pending', 'is-ready', 'is-blocked');
            ui.idleStatusBadge.classList.add(
                kind === 'allowed' ? 'is-ready' :
                kind === 'blocked' ? 'is-blocked' :
                'is-pending'
            );

            ui.idleStatusBadge.textContent =
                kind === 'allowed' ? 'Location verified' :
                kind === 'blocked' ? 'Location blocked' :
                'Checking location';
        }

        if (ui.idleLocationTitle) ui.idleLocationTitle.textContent = state.locationTitle || 'Preparing kiosk';
        if (ui.idleLocationSub) ui.idleLocationSub.textContent = state.locationSub || '';

        var showCenter = (kind === 'blocked' && ui.idleOverlay && ui.idleOverlay.classList.contains('hidden'));
        setCenterBlock(state.locationTitle, state.locationSub, showCenter);
    }

    function setLocationState(kind, title, sub, banner) {
        state.locationState = kind || 'pending';
        state.allowedArea = (state.locationState === 'allowed');
        state.locationTitle = title || '';
        state.locationSub = sub || '';
        state.locationBanner = banner || '';
        applyLocationUi();
    }

    // =========
    // toast
    // =========
    function toast(type, text) {
        var msg = (text || '').toString().trim();
        if (!msg) return;
        if (window.Toastify) {
            var bg = type === 'success' ? '#1a6b3a' : (type === 'info' ? '#1a3a6b' : '#6b1a1a');
            Toastify({
                text:        msg,
                duration:    CFG.postScan.toastMs,
                close:       true,
                gravity:     'bottom',
                position:    'right',
                stopOnFocus: true,
                style:       { background: bg },
            }).showToast();
        } else {
            console.log('[toast]', type, msg);
        }
    }

    function toastSuccess(t) { toast('success', t); }
    function toastError(t)   { toast('error',   t); }

    // =========
    // post-scan hold
    // =========
    function armPostScanHold(ms) {
        var now  = Date.now();
        var hold = (typeof ms === 'number' && isFinite(ms) && ms > 0) ? ms : CFG.postScan.holdMs;
        state.scanBlockUntil = Math.max(state.scanBlockUntil || 0, now + hold);
    }

    // =========
    // visitor modal
    // =========
    function openVisitorModal(payload) {
        if (state.unlockOpen)  return;
        if (state.visitorOpen) return;
        state.visitorOpen    = true;
        state.pendingVisitor = payload || null;

        if (ui.visitorErr) ui.visitorErr.textContent = '';

        var isKnown = !!(payload && payload.isKnown);
        var name    = (payload && payload.visitorName) ? payload.visitorName : '';

        if (ui.visitorNameRow) ui.visitorNameRow.classList.toggle('hidden', isKnown);
        if (ui.visitorName) {
            ui.visitorName.value    = name;
            ui.visitorName.disabled = isKnown;
        }
        if (ui.visitorPurpose) ui.visitorPurpose.value = '';

        if (ui.visitorBackdrop) {
            ui.visitorBackdrop.classList.remove('hidden');
            ui.visitorBackdrop.setAttribute('aria-hidden', 'false');
        }

        setPrompt('Visitor.', isKnown ? 'Enter reason for visit.' : 'Enter name and reason for visit.');
        setEta('ETA: paused');

        setTimeout(function () {
            if (!isKnown && ui.visitorName) ui.visitorName.focus();
            else if (ui.visitorPurpose) ui.visitorPurpose.focus();
        }, 50);
    }

    function closeVisitorModal() {
        state.visitorOpen    = false;
        state.pendingVisitor = null;

        if (ui.visitorBackdrop) {
            ui.visitorBackdrop.classList.add('hidden');
            ui.visitorBackdrop.setAttribute('aria-hidden', 'true');
        }
        armPostScanHold(1500);
        setPrompt('Ready.', 'Stand still. One face only.');
    }

    function submitVisitorForm() {
        var scanId  = (state.pendingVisitor && state.pendingVisitor.scanId) ? state.pendingVisitor.scanId : '';
        var isKnown = !!(state.pendingVisitor && state.pendingVisitor.isKnown);
        var name    = ((ui.visitorName    && ui.visitorName.value)    || '').trim();
        var purpose = ((ui.visitorPurpose && ui.visitorPurpose.value) || '').trim();

        if (!scanId) {
            toastError('Visitor scan expired. Please scan again.');
            closeVisitorModal();
            return;
        }
        if (!isKnown && !name) {
            if (ui.visitorErr) ui.visitorErr.textContent = 'Name is required.';
            if (ui.visitorName) ui.visitorName.focus();
            return;
        }
        if (!purpose) {
            if (ui.visitorErr) ui.visitorErr.textContent = 'Reason is required.';
            if (ui.visitorPurpose) ui.visitorPurpose.focus();
            return;
        }

        var fd = new FormData();
        fd.append('__RequestVerificationToken', token);
        fd.append('scanId', scanId);
        if (!isKnown) fd.append('name', name);
        fd.append('purpose', purpose);

        fetch(EP.submitVisitor, { method: 'POST', body: fd, credentials: 'same-origin' })
            .then(function (r) {
                if (r.status === 429) { toastError('System busy. Please wait.'); return null; }
                return r.json();
            })
            .then(function (j) {
                if (!j) return;
                if (j.ok) {
                    toastSuccess(j.message || 'Visitor saved.');
                    closeVisitorModal();
                    armPostScanHold(CFG.postScan.holdMs);
                } else {
                    if (ui.visitorErr) ui.visitorErr.textContent = j.message || j.error || 'Could not save visitor.';
                }
            })
            .catch(function () {
                toastError('System error. Please try again.');
            });
    }

    function wireVisitorUi() {
        if (ui.visitorCancel) ui.visitorCancel.addEventListener('click', closeVisitorModal);
        if (ui.visitorClose)  ui.visitorClose.addEventListener('click',  closeVisitorModal);
        if (ui.visitorSubmit) ui.visitorSubmit.addEventListener('click', submitVisitorForm);

        if (ui.visitorBackdrop) {
            ui.visitorBackdrop.addEventListener('click', function (e) {
                if (e.target === ui.visitorBackdrop) closeVisitorModal();
            });
        }

        document.addEventListener('keydown', function (e) {
            if (!state.visitorOpen) return;
            if (e.key === 'Escape') { e.preventDefault(); closeVisitorModal(); }
            if (e.key === 'Enter')  { e.preventDefault(); submitVisitorForm(); }
        });
    }

    // =========
    // ETA
    // =========
    function setEta(text) {
        if (ui.scanEtaLine) ui.scanEtaLine.textContent = text || 'ETA: --';
    }

    // =========
    // idle UI
    // =========
    function setIdleUi(idle) {
        if (ui.kioskRoot)   ui.kioskRoot.classList.toggle('kioskIdle', !!idle);
        if (ui.idleOverlay) {
            ui.idleOverlay.classList.toggle('hidden', !idle);
            ui.idleOverlay.setAttribute('aria-hidden', idle ? 'false' : 'true');
        }
    }

    function setKioskMode(mode) {
        try { if (ui.kioskRoot) ui.kioskRoot.setAttribute('data-mode', mode || 'legacy'); } catch (e) {}
    }

    // =========
    // clock -- updates HUD clock AND idle overlay clock in sync
    // =========
    function nowText() {
        var d    = new Date();
        var hh   = ('0' + d.getHours()).slice(-2);
        var mm   = ('0' + d.getMinutes()).slice(-2);
        var ss   = ('0' + d.getSeconds()).slice(-2);
        var time = hh + ':' + mm + ':' + ss;

        var days  = ['Sun','Mon','Tue','Wed','Thu','Fri','Sat'];
        var mons  = ['Jan','Feb','Mar','Apr','May','Jun','Jul','Aug','Sep','Oct','Nov','Dec'];
        var date  = days[d.getDay()] + ', ' + mons[d.getMonth()] + ' ' + d.getDate() + ' ' + d.getFullYear();

        if (ui.timeLine)  ui.timeLine.textContent  = time;
        if (ui.dateLine)  ui.dateLine.textContent  = date;
        // Sync to idle overlay
        if (ui.idleClock) ui.idleClock.textContent = time;
        if (ui.idleDate)  ui.idleDate.textContent  = date;
    }

    function startClock() {
        nowText();
        setInterval(nowText, 1000);
    }

    // =========
    // liveness display
    // =========
    function setLiveness(p, th, cls) {
        if (!ui.livenessLine) return;
        var hasP  = (typeof p  === 'number') && isFinite(p);
        var hasTh = (typeof th === 'number') && isFinite(th);
        ui.livenessLine.textContent = hasP
            ? 'Live: ' + p.toFixed(2) + (hasTh ? ' / ' + th.toFixed(2) : '')
            : 'Live: --';
        ui.livenessLine.classList.remove('live-pass','live-near','live-fail','live-unk');
        ui.livenessLine.classList.add(cls || 'live-unk');
    }

    // =========
    // canvas overlay helpers
    // =========
    function resizeCanvas() {
        var w = canvas.clientWidth;
        var h = canvas.clientHeight;
        if (canvas.width !== w || canvas.height !== h) {
            canvas.width  = w;
            canvas.height = h;
        }
    }

    function mapVideoBoxToCanvas(vbox) {
        if (!vbox || !video.videoWidth || !video.videoHeight) return null;
        var W = canvas.width, H = canvas.height;
        var imgW = video.videoWidth, imgH = video.videoHeight;
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

    function toVideoBox(bb) {
        if (!bb) return null;

        var looksNormalized =
            bb.width <= 1.5 &&
            bb.height <= 1.5 &&
            bb.originX <= 1.5 &&
            bb.originY <= 1.5;

        if (looksNormalized) {
            return {
                x: bb.originX * video.videoWidth,
                y: bb.originY * video.videoHeight,
                w: bb.width   * video.videoWidth,
                h: bb.height  * video.videoHeight
            };
        }

        return {
            x: bb.originX,
            y: bb.originY,
            w: bb.width,
            h: bb.height
        };
    }

    function boxFullyVisibleCanvas(box) {
        if (!box) return false;
        if (box.w <= 0 || box.h <= 0) return false;

        var cx = box.x + (box.w / 2);
        var cy = box.y + (box.h / 2);

        var mx = canvas.width * 0.04;
        var my = canvas.height * 0.04;

        if (cx < mx || cx > (canvas.width - mx)) return false;
        if (cy < my || cy > (canvas.height - my)) return false;

        if (box.w < canvas.width * 0.10) return false;
        if (box.h < canvas.height * 0.14) return false;

        return true;
    }

    function isTooSmallFaceNorm(bbox) {
        if (!bbox || !isFinite(bbox.width) || !isFinite(bbox.height)) return true;
        return bbox.width * bbox.height < CFG.gating.minFaceAreaRatio;
    }

    // =========
    // canvas draw -- animated corner brackets + scan line + glow
    // =========
    function drawCornerBrackets(bx, by, bw, bh, color, glowColor, lineWidth) {
        var L = Math.min(bw, bh) * 0.20;
        ctx.save();
        ctx.strokeStyle = color;
        ctx.lineWidth   = lineWidth;
        ctx.lineCap     = 'round';
        ctx.shadowColor = glowColor;
        ctx.shadowBlur  = 14;

        // top-left
        ctx.beginPath();
        ctx.moveTo(bx, by + L);
        ctx.lineTo(bx, by);
        ctx.lineTo(bx + L, by);
        ctx.stroke();

        // top-right
        ctx.beginPath();
        ctx.moveTo(bx + bw - L, by);
        ctx.lineTo(bx + bw, by);
        ctx.lineTo(bx + bw, by + L);
        ctx.stroke();

        // bottom-left
        ctx.beginPath();
        ctx.moveTo(bx, by + bh - L);
        ctx.lineTo(bx, by + bh);
        ctx.lineTo(bx + L, by + bh);
        ctx.stroke();

        // bottom-right
        ctx.beginPath();
        ctx.moveTo(bx + bw - L, by + bh);
        ctx.lineTo(bx + bw, by + bh);
        ctx.lineTo(bx + bw, by + bh - L);
        ctx.stroke();

        ctx.restore();
    }

    function drawScanLine(bx, by, bw, bh, progress, color) {
        if (progress <= 0) return;
        var scanY = by + bh * progress;
        var grad  = ctx.createLinearGradient(bx, scanY, bx + bw, scanY);
        grad.addColorStop(0,    'rgba(0,0,0,0)');
        grad.addColorStop(0.25, color);
        grad.addColorStop(0.5,  color);
        grad.addColorStop(0.75, color);
        grad.addColorStop(1,    'rgba(0,0,0,0)');

        ctx.save();
        ctx.globalAlpha = 0.55;
        ctx.strokeStyle = grad;
        ctx.lineWidth   = 1.5;
        ctx.shadowColor = color;
        ctx.shadowBlur  = 8;
        ctx.beginPath();
        ctx.moveTo(bx, scanY);
        ctx.lineTo(bx + bw, scanY);
        ctx.stroke();
        ctx.restore();
    }

    function drawFaceGlow(bx, by, bw, bh, color) {
        return;
    }

    function drawLoop() {
        resizeCanvas();
        ctx.clearRect(0, 0, canvas.width, canvas.height);

        if (state.mpBoxCanvas) {
            var scanning = state.liveInFlight;
            var good     = state.faceStatus === 'good';

            // color based on state
            var mainColor, glowColor;
            if (scanning) {
                mainColor = '#4f9cf9';
                glowColor = 'rgba(79,156,249,0.70)';
            } else if (good) {
                mainColor = '#34d399';
                glowColor = 'rgba(52,211,153,0.60)';
            } else {
                mainColor = '#fbbf24';
                glowColor = 'rgba(251,191,36,0.50)';
            }

            var b = state.mpBoxCanvas;

            // subtle glow fill behind face
            drawFaceGlow(b.x, b.y, b.w, b.h, mainColor);

            // animated scan line while in flight
            if (scanning) {
                state.scanLineProgress = (state.scanLineProgress + 0.016) % 1.0;
                drawScanLine(b.x, b.y, b.w, b.h, state.scanLineProgress, mainColor);
            } else {
                state.scanLineProgress = 0;
            }

            // corner brackets -- thicker when scanning
            var lw = scanning ? 3.5 : 2.5;
            drawCornerBrackets(b.x, b.y, b.w, b.h, mainColor, glowColor, lw);
            ctx.save();
            ctx.strokeStyle = mainColor;
            ctx.globalAlpha = 0.95;
            ctx.lineWidth   = scanning ? 2.5 : 2.0;
            ctx.strokeRect(b.x, b.y, b.w, b.h);
            ctx.restore();
        }

        requestAnimationFrame(drawLoop);
    }

    // =========
    // reset scan state
    // =========
    function resetScanState() {
        state.mpBoxCanvas      = null;
        state.mpPrevCenter     = null;
        state.faceStatus       = 'none';
        state.mpRawCount       = 0;
        state.mpAcceptedCount  = 0;
        state.mpReadyToFire    = false;
        state.mpStableStart    = 0;
        state.mpFaceSeenAt     = 0;
        state.frameDiffs       = [];
        state.latestLiveness   = null;
        state.scanLineProgress = 0;
        setLiveness(null, null, 'live-unk');
        setEta('ETA: --');
    }

    // =========
    // motion sense (local anti-idle, no server calls)
    // =========
    var senseCanvas = document.createElement('canvas');
    senseCanvas.width  = CFG.antiSpoof.motionW;
    senseCanvas.height = CFG.antiSpoof.motionH;
    var senseCtx = senseCanvas.getContext('2d', { willReadFrequently: true });
    var lastSenseData = null;

    function updateSenseDiff() {
        if (!video.videoWidth || !video.videoHeight) return null;
        senseCtx.drawImage(video, 0, 0, CFG.antiSpoof.motionW, CFG.antiSpoof.motionH);
        var data = senseCtx.getImageData(0, 0, CFG.antiSpoof.motionW, CFG.antiSpoof.motionH).data;
        if (!lastSenseData) { lastSenseData = new Uint8ClampedArray(data); return null; }
        var sum = 0;
        for (var i = 0; i < data.length; i += 4) {
            sum += Math.abs(data[i]     - lastSenseData[i]);
            sum += Math.abs(data[i + 1] - lastSenseData[i + 1]);
            sum += Math.abs(data[i + 2] - lastSenseData[i + 2]);
        }
        lastSenseData.set(data);
        return sum / (CFG.antiSpoof.motionW * CFG.antiSpoof.motionH * 3);
    }

    function localSenseLoop() {
        try {
            if (video.videoWidth && video.videoHeight) {
                var diff = updateSenseDiff();
                state.motionDiffNow = diff;
                if (diff !== null) pushLimited(state.frameDiffs, diff, CFG.antiSpoof.motionWindow);
                if (diff !== null && diff >= CFG.idle.motionMin) state.localSeenAt = Date.now();
                state.localPresent = (Date.now() - state.localSeenAt) <= CFG.idle.faceLostMs;
            }
        } finally {
            setTimeout(localSenseLoop, CFG.idle.senseMs);
        }
    }

    // =========
    // camera capture
    // OPT-10: quality 0.78 (was 0.85) -- ~18% smaller payload, faster upload
    // =========
    var captureCanvas = document.createElement('canvas');
    var captureCtx    = captureCanvas.getContext('2d');

    function captureFrameBlob(quality) {
        var q = (typeof quality === 'number') ? quality : 0.78;
        captureCanvas.width  = 640;
        captureCanvas.height = 480;
        captureCtx.drawImage(video, 0, 0, 640, 480);
        return new Promise(function (resolve) {
            captureCanvas.toBlob(function (b) { resolve(b); }, 'image/jpeg', q);
        });
    }

    // =========
    // mediapipe tasks adapter
    // =========
    var mp = {
        vision:     null,
        detector:   null,
        failStreak: 0,

        init: function () {
            if (!nextGenEnabled) return Promise.reject(new Error('NEXTGEN_DISABLED'));

            var hasTasks = (
                typeof window.MpFilesetResolver === 'function' &&
                typeof window.MpFaceDetectorTask === 'function'
            );
            if (!hasTasks) return Promise.reject(new Error('MP_ASSETS_MISSING'));

            var self = this;
            return window.MpFilesetResolver
                .forVisionTasks(CFG.tasksVision.wasmBase)
                .then(function (vision) {
                    self.vision = vision;
                    return window.MpFaceDetectorTask.createFromOptions(vision, {
                        baseOptions: {
                            modelAssetPath: CFG.tasksVision.modelPath,
                            delegate: 'GPU',
                        },
                        runningMode: 'VIDEO',
                        minDetectionConfidence:    CFG.mp.detectMinConf,
                        minSuppressionThreshold:   0.3,
                    }).catch(function () {
                        return window.MpFaceDetectorTask.createFromOptions(vision, {
                            baseOptions: {
                                modelAssetPath: CFG.tasksVision.modelPath,
                                delegate: 'CPU',
                            },
                            runningMode: 'VIDEO',
                            minDetectionConfidence:  CFG.mp.detectMinConf,
                            minSuppressionThreshold: 0.3,
                        });
                    });
                })
                .then(function (detector) {
                    self.detector = detector;
                    state.mpMode  = 'tasks';
                    setKioskMode('tasks');
                    log('MediaPipe Tasks ready');
                })
                .catch(function (e) {
                    state.mpMode = 'none';
                    log('MediaPipe init failed', e);
                    throw e;
                });
        },

        tick: function () {
            if (state.mpMode !== 'tasks' || !this.detector || !video.videoWidth) return;
            try {
                var now    = Math.floor(performance.now());
                var result = this.detector.detectForVideo(video, now);
                var dets   = (result && result.detections) ? result.detections : [];

                var valid = dets.filter(function (d) {
                    return ((d.categories && d.categories[0] && d.categories[0].score) || 0) >= CFG.mp.acceptMinScore;
                });

                var sized = valid.filter(function (d) {
                    return !isTooSmallFaceNorm(d.boundingBox);
                });

                var multi = sized.filter(function (d) {
                    var b = d.boundingBox;
                    return b && b.width * b.height >= CFG.mp.multiMinAreaRatio;
                });

                if (multi.length > 1) {
                    state.faceStatus    = 'multi';
                    state.mpBoxCanvas   = null;
                    state.mpPrevCenter  = null;
                    state.mpReadyToFire = false;
                    state.mpStableStart = 0;
                    safeSetPrompt('One face only.', '');
                    return;
                }

                if (sized.length === 0) {
                    state.faceStatus  = 'none';
                    state.mpBoxCanvas = null;
                    return;
                }

                var best = sized.reduce(function (a, b) {
                    var aA = (a.boundingBox ? a.boundingBox.width * a.boundingBox.height : 0);
                    var bA = (b.boundingBox ? b.boundingBox.width * b.boundingBox.height : 0);
                    return aA >= bA ? a : b;
                });

                var bb  = best.boundingBox;
                var box = mapVideoBoxToCanvas(toVideoBox(bb));

                window.__kioskDebug = {
                    rawBox: bb,
                    mappedBox: box,
                    videoWidth: video.videoWidth,
                    videoHeight: video.videoHeight
                };
                // bbox debug log disabled
state.faceStatus   = boxFullyVisibleCanvas(box) ? 'good' : 'low';
                state.mpBoxCanvas  = box;
                state.mpFaceSeenAt = Date.now();
                this.failStreak    = 0;

                if (state.faceStatus === 'low') {
                    state.mpReadyToFire = false;
                    state.mpStableStart = 0;
                    state.mpPrevCenter  = null;
                    safeSetPrompt('Center your face.', 'Move slightly back and keep your full face inside the frame.');
                    return;
                }

                updateStableTracking(box, Date.now());

            } catch (e) {
                this.failStreak++;
                // Always surface the real error so admins can diagnose in DevTools
                console.error('[FaceAttend] detectForVideo error #' + this.failStreak + ':', (e && e.message) ? e.message : e);
                if (this.failStreak > 30) {
                    state.mpMode = 'none';
                    console.error('[FaceAttend] MediaPipe disabled after 30 errors. Open DevTools Console to see the errors above.');
                    log('MediaPipe recurring error, disabling', e);
                }
            }
        },
    };

    // =========
    // stable tracking
    // =========
    function updateStableTracking(box, now) {
        if (!box) { state.mpStableStart = 0; return; }
        var c = { x: box.x + box.w / 2, y: box.y + box.h / 2 };
        if (!state.mpPrevCenter) { state.mpPrevCenter = c; state.mpStableStart = now; return; }
        var move = Math.hypot(c.x - state.mpPrevCenter.x, c.y - state.mpPrevCenter.y);
        state.mpPrevCenter = c;
        if (move > CFG.gating.stableMaxMovePx) {
            state.mpStableStart = 0;
            safeSetPrompt('Hold still.', '');
            return;
        }
        if (state.mpStableStart === 0) state.mpStableStart = now;
        if ((now - state.mpStableStart) < CFG.mp.stableNeededMs) {
            safeSetPrompt('Hold still.', '');
            return;
        }
        state.mpReadyToFire = true;
    }

    // =========
    // ETA update
    // =========
            function updateEta(facePresent) {
        if (!facePresent) {
            if (state.locationState === 'pending') { setEta('ETA: locating'); return; }
            if (state.locationState !== 'allowed') { setEta('ETA: blocked'); return; }
            setEta('ETA: idle');
            return;
        }

        if (state.locationState === 'pending')     { setEta('ETA: locating');         return; }
        if (state.locationState !== 'allowed')     { setEta('ETA: blocked');          return; }
        if (!state.mpBoxCanvas || state.faceStatus === 'none') { setEta('ETA: waiting');          return; }
        if (state.faceStatus === 'low')            { setEta('ETA: center face');      return; }
        if (state.faceStatus === 'multi')          { setEta('ETA: one face only');    return; }
        if (state.mpReadyToFire)                   { setEta('ETA: scanning');         return; }

        var msLeft = state.mpStableStart > 0
            ? Math.max(0, CFG.mp.stableNeededMs - (Date.now() - state.mpStableStart))
            : CFG.mp.stableNeededMs;

        setEta('ETA: hold (' + (msLeft / 1000).toFixed(1) + 's)');
    }

    // =========
    // unlock UI
    // =========
    function isUnlockAvailable() {
        return !!(ui.unlockBackdrop && ui.unlockPin && ui.unlockSubmit && ui.unlockCancel && ui.unlockErr);
    }

    function openUnlock() {
        if (!isUnlockAvailable()) return;
        if (state.visitorOpen) closeVisitorModal();
        state.unlockOpen = true;
        ui.unlockErr.textContent = '';
        ui.unlockPin.value = '';
        ui.unlockBackdrop.classList.remove('hidden');
        ui.unlockBackdrop.setAttribute('aria-hidden', 'false');
        if (ui.kioskRoot) ui.kioskRoot.classList.add('unlockOpen');
        setTimeout(function () { if (ui.unlockPin) ui.unlockPin.focus(); }, 50);
    }

    function closeUnlock() {
        if (!isUnlockAvailable()) return;
        state.unlockOpen = false;
        ui.unlockBackdrop.classList.add('hidden');
        ui.unlockBackdrop.setAttribute('aria-hidden', 'true');
        if (ui.kioskRoot) ui.kioskRoot.classList.remove('unlockOpen');
        ui.unlockErr.textContent = '';
        ui.unlockPin.value = '';
    }

    function submitUnlock() {
        if (!isUnlockAvailable()) return;
        var pin = (ui.unlockPin.value || '').trim();
        if (!pin) { ui.unlockErr.textContent = 'Enter PIN.'; ui.unlockPin.focus(); return; }

        var fd = new FormData();
        fd.append('__RequestVerificationToken', token);
        fd.append('pin', pin);
        fd.append('returnUrl', (document.body && document.body.dataset && document.body.dataset.returnUrl) || '');

        ui.unlockSubmit.disabled = true;
        ui.unlockCancel.disabled = true;
        ui.unlockErr.textContent = '';

        fetch(EP.unlockPin, { method: 'POST', body: fd })
            .then(function (r) { return r.json(); })
            .then(function (j) {
                if (j && j.ok === true) {
                    var ru = (j.returnUrl || '').trim();
                    closeUnlock();
                    if (ru) window.location.href = ru;
                } else {
                    ui.unlockErr.textContent = 'Invalid PIN.';
                    if (ui.unlockPin) ui.unlockPin.focus();
                }
            })
            .catch(function () {
                ui.unlockErr.textContent = 'Unlock failed.';
            })
            .finally(function () {
                ui.unlockSubmit.disabled = false;
                ui.unlockCancel.disabled = false;
            });
    }

    function wireUnlockUi() {
        if (!isUnlockAvailable()) return;

        ui.unlockCancel.addEventListener('click', closeUnlock);
        ui.unlockSubmit.addEventListener('click', submitUnlock);
        if (ui.unlockClose) ui.unlockClose.addEventListener('click', closeUnlock);

        ui.unlockBackdrop.addEventListener('click', function (e) {
            if (e.target === ui.unlockBackdrop) closeUnlock();
        });

        ui.unlockPin.addEventListener('keydown', function (e) {
            if (e.key === 'Enter')  { e.preventDefault(); submitUnlock(); }
            if (e.key === 'Escape') { e.preventDefault(); closeUnlock(); }
        });

        // Ctrl+Shift+Space triggers admin unlock from anywhere on page
        document.addEventListener('keydown', function (e) {
            if (!isUnlockAvailable())  return;
            if (state.unlockOpen)      return;
            var isSpace = (e.code === 'Space') || (e.key === ' ') || (e.keyCode === 32);
            if (e.ctrlKey && e.shiftKey && isSpace) {
                e.preventDefault();
                if (state.visitorOpen) closeVisitorModal();
                openUnlock();
            }
        }, true);

        // Double-click on brand label also opens unlock
        var brandEl = document.querySelector('#topLeft .brand');
        if (brandEl) brandEl.addEventListener('dblclick', openUnlock);
    }

    // =========
    // GPS + office resolve
    // =========
        function startGpsIfAvailable() {
        setLocationState(
            'pending',
            'Preparing kiosk',
            'Please wait while the kiosk verifies the current office location.',
            'Checking location...'
        );

        if (!('geolocation' in navigator)) {
            if (!isMobile) {
                resolveOfficeDesktopOnce();
                return;
            }

            setLocationState(
                'blocked',
                'Location not available',
                'Enable location services to use the DILG Region XII kiosk.',
                'Location not available'
            );
            return;
        }

        var isSecure = (location.protocol === 'https:' || location.hostname === 'localhost' || location.hostname === '127.0.0.1');
        if (!isSecure) {
            if (!isMobile) {
                resolveOfficeDesktopOnce();
                return;
            }

            setLocationState(
                'blocked',
                'Secure connection required',
                'Use HTTPS so the kiosk can access location services.',
                'HTTPS required'
            );
            return;
        }

        navigator.geolocation.watchPosition(
            function (pos) {
                state.gps.lat = pos.coords.latitude;
                state.gps.lon = pos.coords.longitude;
                state.gps.accuracy = pos.coords.accuracy;

                if (state.locationState !== 'allowed') {
                    setLocationState(
                        'pending',
                        'Location captured',
                        'Verifying the current office area.',
                        'Verifying location...'
                    );
                }

                resolveOfficeIfNeeded();
            },
            function (err) {
                state.gps.lat = state.gps.lon = state.gps.accuracy = null;

                if (!isMobile) {
                    setLocationState(
                        'pending',
                        'Checking kiosk office',
                        'Looking for the registered office profile for this kiosk.',
                        'Checking office...'
                    );
                    resolveOfficeDesktopOnce();
                    return;
                }

                var title = 'Unable to get location';
                var sub = 'Turn on location services and try again.';
                var banner = 'Location unavailable';

                if (err && err.code === 1) {
                    title = 'Location access denied';
                    sub = 'Allow location access to continue using the DILG Region XII kiosk.';
                    banner = 'Location denied';
                } else if (err && err.code === 2) {
                    title = 'Location unavailable';
                    sub = 'The device could not detect the current location. Move to an open area and try again.';
                    banner = 'Location unavailable';
                } else if (err && err.code === 3) {
                    title = 'Location timed out';
                    sub = 'The location request took too long. Please try again.';
                    banner = 'Location timeout';
                }

                setLocationState('blocked', title, sub, banner);
            },
            { enableHighAccuracy: true, maximumAge: 500, timeout: 6000 }
        );
    }

        function resolveOfficeIfNeeded() {
        var t = Date.now();
        if (t - state.lastResolveAt < CFG.server.resolveMs) return Promise.resolve();
        state.lastResolveAt = t;

        if (state.gps.lat == null || state.gps.lon == null || state.gps.accuracy == null) {
            if (!isMobile) {
                return resolveOfficeDesktopOnce();
            }

            setLocationState(
                'pending',
                'Checking location',
                'Waiting for a GPS fix. Stay within the DILG Region XII office area.',
                'Locating...'
            );
            return Promise.resolve();
        }

        setLocationState(
            'pending',
            'Checking location',
            'Please wait while we verify the office radius.',
            'Checking location...'
        );

        var fd = new FormData();
        fd.append('__RequestVerificationToken', token);
        fd.append('lat', state.gps.lat);
        fd.append('lon', state.gps.lon);
        fd.append('accuracy', state.gps.accuracy);

        return fetch(EP.resolveOffice, { method: 'POST', body: fd })
            .then(function (r) { return r.json(); })
            .then(function (j) {
                if (!j || j.ok !== true) {
                    resetScanState();
                    setLocationState(
                        'blocked',
                        'Unable to verify location',
                        (j && j.error) ? String(j.error) : 'Please try again or contact the system administrator.',
                        'Location check failed'
                    );
                    return;
                }

                if (j.allowed === false) {
                    state.currentOffice.id = null;
                    state.currentOffice.name = null;
                    setLocationState(
                        'blocked',
                        'Outside allowed office area',
                        'Move inside the DILG Region XII office radius to continue.',
                        'Not in allowed area'
                    );
                    return;
                }

                state.currentOffice.id = j.officeId;
                state.currentOffice.name = j.officeName;

                setLocationState(
                    'allowed',
                    'Location verified',
                    'You may now look at the camera for attendance.',
                    state.currentOffice.name || 'Office verified'
                );
            })
            .catch(function () {
                setLocationState(
                    'blocked',
                    'Location check failed',
                    'The kiosk could not verify the office location. Please try again.',
                    'Location check failed'
                );
            });
    }

            function resolveOfficeDesktopOnce() {
        if (isMobile) return Promise.resolve();

        if (state.currentOffice && state.currentOffice.name && state.locationState === 'allowed') {
            return Promise.resolve();
        }

        setLocationState(
            'pending',
            'Checking kiosk office',
            'Looking for the registered office profile for this kiosk.',
            'Checking office...'
        );

        var fd = new FormData();
        fd.append('__RequestVerificationToken', token);

        return fetch(EP.resolveOffice, { method: 'POST', body: fd })
            .then(function (r) {
                if (r.status === 429) {
                    var retry = Number(r.headers.get('Retry-After') || 0);
                    return { ok: false, error: 'RATE_LIMIT_EXCEEDED', retryAfter: retry };
                }
                return r.json();
            })
            .then(function (j) {
                if (j && j.ok === true && j.allowed !== false) {
                    state.currentOffice.id = j.officeId;
                    state.currentOffice.name = j.officeName;

                    setLocationState(
                        'allowed',
                        'Location verified',
                        'You may now look at the camera for attendance.',
                        state.currentOffice.name || 'Office verified'
                    );
                    return;
                }

                var mapped = humanizeResolveError(j && (j.reason || j.error), j && j.retryAfter, j && j.requiredAccuracy);
                setLocationState('blocked', mapped.title, mapped.sub, mapped.banner);
            })
            .catch(function () {
                var mapped = humanizeResolveError('UNKNOWN');
                setLocationState('blocked', mapped.title, mapped.sub, mapped.banner);
            });
    }

    // =========
    // attendance submit
    // OPT-09: AbortController cancels any stale in-flight request
    // =========
    function submitAttendance(blob) {
        // Cancel previous stale request
        if (state.attendAbortCtrl) {
            try { state.attendAbortCtrl.abort(); } catch (e) {}
        }
        state.attendAbortCtrl = new AbortController();
        var signal = state.attendAbortCtrl.signal;

        state.lastCaptureAt = Date.now();
        setPrompt('Scanning...', 'Hold still.');

        var fd = new FormData();
        fd.append('__RequestVerificationToken', token);
        fd.append('image', blob, 'capture.jpg');
        if (state.gps.lat      != null) fd.append('lat',      state.gps.lat);
        if (state.gps.lon      != null) fd.append('lon',      state.gps.lon);
        if (state.gps.accuracy != null) fd.append('accuracy', state.gps.accuracy);

        return fetch(EP.attend, { method: 'POST', body: fd, credentials: 'same-origin', signal: signal })
            .then(function (r) {
                if (r.status === 429) { setPrompt('System busy.', 'Please wait.'); return null; }
                return r.json();
            })
            .then(function (j) {
                if (!j) return;

                // update liveness display
                if (typeof j.liveness === 'number') {
                    var p  = Number(j.liveness);
                    var th = (j.threshold != null) ? Number(j.threshold) : null;
                    var threshold = th !== null ? th : 0.75;
                    var cls;
                    if (p >= threshold)              cls = 'live-pass';
                    else if (p >= threshold * 0.80)  cls = 'live-near';
                    else                             cls = 'live-fail';
                    setLiveness(p, th, cls);
                    state.latestLiveness    = p;
                    state.livenessThreshold = threshold;
                }

                if (j.ok === true) {
                    if (j.mode !== 'VISITOR') {
                        var evt  = (j.eventType || '').toUpperCase();
                        var name = j.displayName || j.name || 'Employee';
                        toastSuccess((evt === 'IN' ? 'Time In' : 'Time Out') + ' -- ' + name);
                        setPrompt(evt === 'IN' ? 'Time In recorded.' : 'Time Out recorded.', name);
                        armPostScanHold(CFG.postScan.holdMs);
                    } else {
                        openVisitorModal(j);
                    }
                } else {
                    var err = j.error || '';
                    if (err === 'ALREADY_SCANNED') {
                        toastError('Already scanned. Please wait.');
                        armPostScanHold(CFG.postScan.holdMs);
                    } else if (err === 'LIVENESS_FAIL') {
                        toastError('Liveness check failed. Move naturally and try again.');
                        armPostScanHold(1500);
                    } else {
                        toastError(j.message || err || 'Scan failed.');
                        armPostScanHold(1500);
                    }
                    setPrompt('Ready.', 'Stand still. One face only.');
                }
            })
            .catch(function (e) {
                if (e && e.name === 'AbortError') {
                    log('Attendance fetch aborted (stale request)');
                    return;
                }
                setPrompt('System error.', 'Reload the page or check the server.');
            });
    }

    // =========
    // camera start
    // =========
    function startCamera() {
        return navigator.mediaDevices.getUserMedia({
            video: {
                facingMode: 'user',
                width:      { ideal: 640, max: 640 },
                height:     { ideal: 480, max: 480 },
                frameRate:  { ideal: 15, max: 15 },
            },
            audio: false,
        }).then(function (stream) {
            video.srcObject = stream;
            return video.play();
        });
    }

    // =========
    // main loop
    // =========
            function loop() {
        var doLoop = function () {
            try {
                if (state.unlockOpen) return;
                if (!video.videoWidth || !video.videoHeight) return;

                var now = Date.now();
                mp.tick();

                var facePresent = (
                    (state.mpFaceSeenAt > 0 && (now - state.mpFaceSeenAt) < CFG.idle.faceLostMs) ||
                    state.localPresent
                );

                var shouldIdle = (!facePresent || state.locationState !== 'allowed');

                if (shouldIdle) {
                    if (!state.wasIdle) {
                        resetScanState();
                    }

                    state.wasIdle = true;
                    setIdleUi(true);

                    if (!facePresent) {
                        setPrompt(
                            'Idle.',
                            state.locationState === 'allowed'
                                ? 'Look at the camera.'
                                : (state.locationSub || 'Please wait while we verify your location.')
                        );
                        updateEta(false);
                    } else if (state.locationState === 'pending') {
                        safeSetPrompt('Checking location.', state.locationSub || 'Please wait while we verify your office area.');
                        updateEta(true);
                    } else {
                        safeSetPrompt(
                            state.locationTitle || 'Location required.',
                            state.locationSub || 'Move into the allowed office area to continue.'
                        );
                        updateEta(true);
                    }

                    resolveOfficeIfNeeded();
                    return;
                }

                if (state.wasIdle) {
                    resetScanState();
                    setPrompt('Ready.', 'Look at the camera.');
                }

                state.wasIdle = false;
                setIdleUi(false);

                resolveOfficeIfNeeded().then(function () {
                    if (state.locationState !== 'allowed') {
                        setIdleUi(true);
                        updateEta(facePresent);
                        return;
                    }

                    if (now < state.backoffUntil) { setPrompt('System busy.', 'Please wait.'); updateEta(true); return; }
                    if (state.visitorOpen) { updateEta(true); return; }
                    if (now < (state.scanBlockUntil || 0)) { safeSetPrompt('Please wait.', 'Next scan ready soon.'); updateEta(true); return; }
                    if (state.mpMode !== 'tasks') { safeSetPrompt('System not ready.', 'Face detection unavailable.'); updateEta(true); return; }

                    if (state.mpReadyToFire && (now - state.lastCaptureAt) > CFG.server.captureCooldownMs) {
                        captureFrameBlob().then(function (blob) {
                            if (!blob) return;
                            state.mpReadyToFire = false;
                            state.mpStableStart = 0;
                            state.liveInFlight = true;
                            submitAttendance(blob).finally(function () {
                                state.liveInFlight = false;
                            });
                        });
                    }

                    updateEta(true);
                });

            } catch (e) {
                if (CFG.debug) console.warn('[FaceAttend] loop error', e);
                setPrompt('System error.', 'Reload the page or check the server.');
                setEta('ETA: --');
            } finally {
                setTimeout(doLoop, CFG.loopMs);
            }
        };
        doLoop();
    }

    // =========
    // init
    // =========
            (function init() {
        if (!validateConfig()) return;

        startClock();
        wireUnlockUi();
        wireVisitorUi();

        setIdleUi(true);
        setPrompt('Preparing kiosk.', 'Please wait while the location is verified.');
        setEta('ETA: locating');
        setLiveness(null, null, 'live-unk');
        applyLocationUi();

        startGpsIfAvailable();
        resolveOfficeDesktopOnce();

        startCamera()
            .then(function () { return mp.init(); })
            .then(function () {
                setIdleUi(true);
                setPrompt('Idle.', state.locationSub || 'Please wait while the location is verified.');
                setEta(state.locationState === 'allowed' ? 'ETA: idle' : 'ETA: locating');
                setLiveness(null, null, 'live-unk');
                drawLoop();
                localSenseLoop();
                loop();
            })
            .catch(function (e) {
                setIdleUi(true);
                var msg = (e && e.message) ? String(e.message) : '';

                if (msg === 'NEXTGEN_DISABLED' || msg === 'MP_ASSETS_MISSING') {
                    setPrompt('System not ready.', 'Face detection assets are missing.');
                } else {
                    setPrompt('Camera blocked.', 'Allow camera permission and reload.');
                }

                setEta('ETA: blocked');
            });
    })();

})();

