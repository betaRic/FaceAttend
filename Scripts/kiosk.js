(function () {
    'use strict';

    // =========
    // dom
    // =========
    const el = (id) => document.getElementById(id);

    const video  = el('kioskVideo');
    const canvas = el('overlayCanvas');
    const ctx    = canvas.getContext('2d');

    const ui = {
        officeLine:   el('officeLine'),
        timeLine:     el('timeLine'),
        dateLine:     el('dateLine'),
        livenessLine: el('livenessLine'),
        scanEtaLine:  el('scanEtaLine'),

        unlockBackdrop: el('unlockBackdrop'),
        unlockPin:      el('unlockPin'),
        unlockErr:      el('unlockErr'),
        unlockCancel:   el('unlockCancel'),
        unlockSubmit:   el('unlockSubmit'),
        unlockClose:    el('unlockClose'),

        visitorBackdrop: el('visitorBackdrop'),
        visitorNameRow:  el('visitorNameRow'),
        visitorName:     el('visitorName'),
        visitorPurpose:  el('visitorPurpose'),
        visitorErr:      el('visitorErr'),
        visitorCancel:   el('visitorCancel'),
        visitorSubmit:   el('visitorSubmit'),
        visitorClose:    el('visitorClose'),

        kioskRoot:   el('kioskRoot'),
        idleOverlay: el('idleOverlay'),
        mainPrompt:  el('mainPrompt'),
        subPrompt:   el('subPrompt'),

        // idle clock elements (new idle screen)
        idleClock: el('idleClock'),
        idleDate:  el('idleDate'),
    };

    const token          = document.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
    const appBase        = (document.body.getAttribute('data-app-base') || '/').replace(/\/?$/, '/');
    const nextGenEnabled = (document.body.getAttribute('data-nextgen') || 'false').toLowerCase() === 'true';

    // =========
    // config — optimised timings
    // =========
    const CFG = {
        debug: false,

        // QA-OPT-01: 60 ms loop (was 120) → 2× faster face detection response
        loopMs: 60,

        mp: {
            detectMinConf:    0.30,
            acceptMinScore:   0.60,
            stableFramesMin:  2,
            // QA-OPT-02: 150 ms stable hold (was 250) → fires 100 ms sooner
            stableNeededMs:   150,
            multiMinAreaRatio: 0.015,
        },

        idle: {
            // QA-OPT-03: 200 ms sense (was 250) → snappier idle↔active transition
            senseMs:    200,
            // QA-OPT-04: 1800 ms lost timeout (was 2000) → less "ghost" active time
            faceLostMs: 1800,
            motionMin:  2.0,
        },

        server: {
            // QA-OPT-05: 900 ms office resolve interval (was 1200)
            resolveMs:        900,
            // QA-OPT-06: 2500 ms capture cooldown (was 3000) → ready 500 ms sooner
            captureCooldownMs: 2500,
        },

        postScan: {
            // QA-OPT-07: 3500 ms post-scan hold (was 5000) → kiosk ready 1.5 s sooner
            holdMs:   3500,
            toastMs:  6500,
        },

        gating: {
            // QA-OPT-08: 3 stable frames required (was 4) → one frame fewer wait
            stableFramesRequired: 3,
            stableMaxMovePx:      10,
            minFaceAreaRatio:     0.03,
            safeEdgeMarginRatio:  0.05,
            centerMin:            0.12,
            centerMax:            0.88,
        },

        antiSpoof: {
            motionW:      64,
            motionH:      48,
            motionWindow: 6,
            motionDiffMin: 1.2,
        },

        tasksVision: {
            wasmBase:  appBase + 'Scripts/vendor/mediapipe/tasks-vision/wasm',
            modelPath: appBase + 'Scripts/vendor/mediapipe/tasks-vision/models/blaze_face_short_range.tflite',
        },
    };

    function validateConfig() {
        var errors = [];

        if (typeof CFG === 'undefined' || CFG === null) {
            errors.push('CFG is not defined.');
        } else {
            if (!CFG.loopMs || CFG.loopMs <= 0)
                errors.push('CFG.loopMs is missing or <= 0 (value: ' + CFG.loopMs + ')');
            if (!CFG.server || !CFG.server.captureCooldownMs)
                errors.push('CFG.server.captureCooldownMs is missing.');
            if (!CFG.server || !CFG.server.resolveMs)
                errors.push('CFG.server.resolveMs is missing.');
            if (!CFG.mp || !CFG.mp.stableNeededMs)
                errors.push('CFG.mp.stableNeededMs is missing.');
        }

        ['kioskVideo', 'overlayCanvas', 'kioskRoot', 'mainPrompt', 'subPrompt'].forEach(function (id) {
            if (!document.getElementById(id))
                errors.push('Required DOM element #' + id + ' is missing.');
        });

        if (errors.length > 0) {
            var root = document.getElementById('kioskRoot') || document.body;
            var div  = document.createElement('div');
            div.style.cssText = 'position:fixed;top:0;left:0;right:0;padding:1rem;background:#dc3545;color:#fff;font-family:monospace;font-size:.85rem;z-index:99999;white-space:pre-wrap';
            div.textContent = 'KIOSK CONFIG ERROR — scan loop will NOT start:\n\n' + errors.join('\n');
            root.insertAdjacentElement('afterbegin', div);
            console.error('[FaceAttend] Config validation failed:', errors);
            return false;
        }
        return true;
    }

    const log = (...args) => { if (CFG.debug) console.log('[FaceAttend]', ...args); };

    // =========
    // endpoints
    // =========
    const EP = {
        unlockPin:     appBase + 'Kiosk/UnlockPin',
        resolveOffice: appBase + 'Kiosk/ResolveOffice',
        attend:        appBase + 'Kiosk/Attend',
        submitVisitor: appBase + 'Kiosk/SubmitVisitor',
    };

    // =========
    // state
    // =========
    const ua       = navigator.userAgent || '';
    const isMobile = /Android|iPhone|iPad|iPod|Mobile|Tablet/i.test(ua);
    const state = {
        // ui flow
        unlockOpen:    false,
        wasIdle:       true,
        visitorOpen:   false,
        pendingVisitor: null,
        scanBlockUntil: 0,

        // gps/office
        gps: { lat: null, lon: null, accuracy: null },
        allowedArea:    !isMobile,
        currentOffice:  { id: null, name: null },
        lastResolveAt:  0,

        // timing
        backoffUntil:  0,
        lastCaptureAt: 0,

        // mp status
        mpMode:          'none',
        mpReadyToFire:   false,
        mpStableStart:   0,
        mpFaceSeenAt:    0,
        faceStatus:      'none',
        mpRawCount:      0,
        mpAcceptedCount: 0,
        mpBoxCanvas:     null,
        mpPrevCenter:    null,

        // liveness display
        latestLiveness:   null,
        livenessThreshold: 0.75,

        // motion history
        motionDiffNow: null,
        frameDiffs:    [],

        // in-flight
        liveInFlight:    false,
        // QA-OPT-09: AbortController for cancelling stale attendance requests
        attendAbortCtrl: null,

        // local sensing
        localSeenAt:  0,
        localPresent: false,
    };

    // =========
    // helpers
    // =========
    function pushLimited(arr, v, max) {
        if (typeof v !== 'number' || !isFinite(v)) return;
        arr.push(v);
        while (arr.length > max) arr.shift();
    }

    function avg(arr) {
        if (!arr || arr.length === 0) return null;
        let s = 0;
        for (let i = 0; i < arr.length; i++) s += arr[i];
        return s / arr.length;
    }

    function setPrompt(a, b) {
        if (ui.mainPrompt) ui.mainPrompt.textContent = a || '';
        if (ui.subPrompt)  ui.subPrompt.textContent  = b || '';
    }

    function safeSetPrompt(a, b) {
        const now = Date.now();
        if (state.liveInFlight) return;
        if ((now - state.lastCaptureAt) < 800) return;
        setPrompt(a, b);
    }

    function toast(type, text) {
        const msg = (text || '').toString().trim();
        if (!msg) return;
        if (window.Toastify) {
            Toastify({
                text:         msg,
                duration:     CFG.postScan.toastMs,
                close:        true,
                gravity:      'bottom',
                position:     'right',
                stopOnFocus:  true,
                style: { background: type === 'success' ? '#16a34a' : (type === 'info' ? '#2563eb' : '#dc2626') },
            }).showToast();
        } else {
            console.log('[toast]', type, msg);
        }
    }

    const toastSuccess = (t) => toast('success', t);
    const toastError   = (t) => toast('error', t);
    const toastInfo    = (t) => toast('info', t);

    function armPostScanHold(ms) {
        const now  = Date.now();
        const hold = (typeof ms === 'number' && isFinite(ms) && ms > 0) ? ms : CFG.postScan.holdMs;
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

        const isKnown = !!payload?.isKnown;
        const name    = payload?.visitorName || '';

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

        setTimeout(() => {
            if (!isKnown && ui.visitorName) ui.visitorName.focus();
            else ui.visitorPurpose?.focus();
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

    async function submitVisitorForm() {
        const scanId  = state.pendingVisitor?.scanId || '';
        const isKnown = !!state.pendingVisitor?.isKnown;
        const name    = (ui.visitorName?.value    || '').trim();
        const purpose = (ui.visitorPurpose?.value || '').trim();

        if (!scanId) {
            toastError('Visitor scan expired. Please scan again.');
            closeVisitorModal();
            return;
        }
        if (!isKnown && !name) {
            if (ui.visitorErr) ui.visitorErr.textContent = 'Name is required.';
            ui.visitorName?.focus();
            return;
        }
        if (!purpose) {
            if (ui.visitorErr) ui.visitorErr.textContent = 'Reason is required.';
            ui.visitorPurpose?.focus();
            return;
        }

        const fd = new FormData();
        fd.append('__RequestVerificationToken', token);
        fd.append('scanId', scanId);
        if (!isKnown) fd.append('name', name);
        fd.append('purpose', purpose);

        try {
            const r = await fetch(EP.submitVisitor, { method: 'POST', body: fd, credentials: 'same-origin' });
            if (r.status === 429) { toastError('System busy. Please wait.'); return; }
            const j = await r.json();

            if (j && j.ok) {
                toastSuccess(j.message || 'Visitor saved.');
                closeVisitorModal();
                armPostScanHold(CFG.postScan.holdMs);
            } else {
                toastError(j?.message || j?.error || 'Could not save visitor.');
            }
        } catch {
            toastError('System error. Please try again.');
        }
    }

    function wireVisitorUi() {
        const close = () => closeVisitorModal();
        ui.visitorCancel?.addEventListener('click', close);
        ui.visitorClose?.addEventListener('click', close);
        ui.visitorSubmit?.addEventListener('click', () => submitVisitorForm());
        ui.visitorBackdrop?.addEventListener('click', (e) => { if (e.target === ui.visitorBackdrop) close(); });
        document.addEventListener('keydown', (e) => {
            if (!state.visitorOpen) return;
            if (e.key === 'Escape') { e.preventDefault(); close(); }
            if (e.key === 'Enter')  { e.preventDefault(); submitVisitorForm(); }
        });
    }

    // =========
    // ETA / liveness display
    // =========
    function setEta(text) {
        if (!ui.scanEtaLine) return;
        ui.scanEtaLine.textContent = text || 'ETA: --';
    }

    function setIdleUi(idle) {
        if (!ui.kioskRoot) return;
        ui.kioskRoot.classList.toggle('kioskIdle', !!idle);
        if (ui.idleOverlay) {
            ui.idleOverlay.classList.toggle('hidden', !idle);
            ui.idleOverlay.setAttribute('aria-hidden', idle ? 'false' : 'true');
        }
    }

    function setKioskMode(mode) {
        try { ui.kioskRoot?.setAttribute('data-mode', mode || 'legacy'); } catch { }
    }

    // QA-OPT-10: nowText also updates idle clock overlay
    function nowText() {
        const d      = new Date();
        const time   = d.toLocaleTimeString('en-PH', { hour12: false });
        const date   = d.toLocaleDateString('en-PH', { weekday: 'short', year: 'numeric', month: 'short', day: 'numeric' });

        if (ui.timeLine)  ui.timeLine.textContent  = time;
        if (ui.dateLine)  ui.dateLine.textContent  = date;
        // Sync idle overlay clock
        if (ui.idleClock) ui.idleClock.textContent = time;
        if (ui.idleDate)  ui.idleDate.textContent  = date;
    }

    function startClock() {
        nowText();
        setInterval(nowText, 1000);
    }

    function setLiveness(p, th, cls) {
        if (!ui.livenessLine) return;
        const hasP  = (typeof p  === 'number') && isFinite(p);
        const hasTh = (typeof th === 'number') && isFinite(th);
        ui.livenessLine.textContent = hasP
            ? ('Live: ' + p.toFixed(2) + (hasTh ? (' / ' + th.toFixed(2)) : ''))
            : 'Live: --';
        ui.livenessLine.classList.remove('live-pass', 'live-near', 'live-fail', 'live-unk');
        ui.livenessLine.classList.add(cls || 'live-unk');
    }

    // =========
    // overlay helpers
    // =========
    function resizeCanvas() {
        const w = canvas.clientWidth;
        const h = canvas.clientHeight;
        if (canvas.width !== w || canvas.height !== h) {
            canvas.width  = w;
            canvas.height = h;
        }
    }

    function mapVideoBoxToCanvas(vbox) {
        if (!vbox || !video.videoWidth || !video.videoHeight) return null;
        const W = canvas.width, H = canvas.height;
        const imgW = video.videoWidth, imgH = video.videoHeight;
        const scale   = Math.max(W / imgW, H / imgH);
        const renderW = imgW * scale, renderH = imgH * scale;
        const offX    = (W - renderW) / 2, offY = (H - renderH) / 2;
        let x = offX + (vbox.x * scale);
        const y = offY + (vbox.y * scale);
        const w = vbox.w * scale, h = vbox.h * scale;
        x = W - (x + w);   // mirror
        return { x, y, w, h };
    }

    function boxFullyVisibleCanvas(box) {
        if (!box) return false;
        const m = Math.min(canvas.width, canvas.height) * CFG.gating.safeEdgeMarginRatio;
        if (box.w <= 0 || box.h <= 0) return false;
        if (box.x < m || box.y < m) return false;
        if ((box.x + box.w) > (canvas.width  - m)) return false;
        if ((box.y + box.h) > (canvas.height - m)) return false;
        return true;
    }

    function isTooSmallFaceNorm(bbox) {
        if (!bbox || !isFinite(bbox.width) || !isFinite(bbox.height)) return true;
        return bbox.width * bbox.height < CFG.gating.minFaceAreaRatio;
    }

    // =========
    // draw loop — animated corner-bracket scan box
    // =========
    function drawCornerBox(cx, cy, cw, ch, color, progress) {
        const L  = Math.min(cw, ch) * 0.22;  // corner arm length
        const lw = 3;
        ctx.save();
        ctx.strokeStyle = color;
        ctx.lineWidth   = lw;
        ctx.shadowColor = color;
        ctx.shadowBlur  = progress > 0.8 ? 18 : 6;
        ctx.lineCap     = 'round';

        // top-left
        ctx.beginPath(); ctx.moveTo(cx, cy + L); ctx.lineTo(cx, cy); ctx.lineTo(cx + L, cy); ctx.stroke();
        // top-right
        ctx.beginPath(); ctx.moveTo(cx + cw - L, cy); ctx.lineTo(cx + cw, cy); ctx.lineTo(cx + cw, cy + L); ctx.stroke();
        // bottom-left
        ctx.beginPath(); ctx.moveTo(cx, cy + ch - L); ctx.lineTo(cx, cy + ch); ctx.lineTo(cx + L, cy + ch); ctx.stroke();
        // bottom-right
        ctx.beginPath(); ctx.moveTo(cx + cw - L, cy + ch); ctx.lineTo(cx + cw, cy + ch); ctx.lineTo(cx + cw, cy + ch - L); ctx.stroke();

        // scanning progress line
        if (progress > 0) {
            const scanY = cy + (ch * progress);
            const grad  = ctx.createLinearGradient(cx, scanY, cx + cw, scanY);
            grad.addColorStop(0,   'transparent');
            grad.addColorStop(0.5, color);
            grad.addColorStop(1,   'transparent');
            ctx.globalAlpha  = 0.55;
            ctx.strokeStyle  = grad;
            ctx.lineWidth    = 1.5;
            ctx.shadowBlur   = 0;
            ctx.beginPath(); ctx.moveTo(cx, scanY); ctx.lineTo(cx + cw, scanY); ctx.stroke();
            ctx.globalAlpha = 1;
        }

        ctx.restore();
    }

    let _scanLineY = 0;
    function drawLoop() {
        resizeCanvas();
        ctx.clearRect(0, 0, canvas.width, canvas.height);

        if (state.mpBoxCanvas) {
            const good     = state.faceStatus === 'good';
            const scanning = state.liveInFlight;
            const color    = scanning ? '#00d4ff' : (good ? '#00ff7a' : '#ffcc00');

            // animate scan line 0→1 while in flight
            if (scanning) {
                _scanLineY = (_scanLineY + 0.018) % 1.0;
            } else {
                _scanLineY = 0;
            }

            const b = state.mpBoxCanvas;
            drawCornerBox(b.x, b.y, b.w, b.h, color, _scanLineY);
        }

        requestAnimationFrame(drawLoop);
    }

    // =========
    // state reset
    // =========
    function resetScanState() {
        state.mpBoxCanvas     = null;
        state.mpPrevCenter    = null;
        state.faceStatus      = 'none';
        state.mpRawCount      = 0;
        state.mpAcceptedCount = 0;
        state.mpReadyToFire   = false;
        state.mpStableStart   = 0;
        state.mpFaceSeenAt    = 0;
        state.frameDiffs      = [];
        state.latestLiveness  = null;
        setLiveness(null, null, 'live-unk');
        setEta('ETA: --');
    }

    // =========
    // motion sense
    // =========
    const senseCanvas = document.createElement('canvas');
    senseCanvas.width  = CFG.antiSpoof.motionW;
    senseCanvas.height = CFG.antiSpoof.motionH;
    const senseCtx = senseCanvas.getContext('2d', { willReadFrequently: true });
    let lastSenseData = null;

    function updateSenseDiff() {
        if (!video.videoWidth || !video.videoHeight) return null;
        senseCtx.drawImage(video, 0, 0, CFG.antiSpoof.motionW, CFG.antiSpoof.motionH);
        const data = senseCtx.getImageData(0, 0, CFG.antiSpoof.motionW, CFG.antiSpoof.motionH).data;
        if (!lastSenseData) { lastSenseData = new Uint8ClampedArray(data); return null; }
        let sum = 0;
        for (let i = 0; i < data.length; i += 4) {
            sum += Math.abs(data[i]     - lastSenseData[i]);
            sum += Math.abs(data[i + 1] - lastSenseData[i + 1]);
            sum += Math.abs(data[i + 2] - lastSenseData[i + 2]);
        }
        lastSenseData.set(data);
        return sum / (CFG.antiSpoof.motionW * CFG.antiSpoof.motionH * 3);
    }

    async function localSenseLoop() {
        try {
            if (!video.videoWidth || !video.videoHeight) return;
            const diff = updateSenseDiff();
            state.motionDiffNow = diff;
            if (diff != null) pushLimited(state.frameDiffs, diff, CFG.antiSpoof.motionWindow);
            if (diff != null && diff >= CFG.idle.motionMin) state.localSeenAt = Date.now();
            state.localPresent = (Date.now() - state.localSeenAt) <= CFG.idle.faceLostMs;
        } finally {
            setTimeout(localSenseLoop, CFG.idle.senseMs);
        }
    }

    // =========
    // camera capture
    // QA-OPT-11: quality 0.78 (was 0.85) → ~18% smaller payload, faster upload
    // =========
    const captureCanvas = document.createElement('canvas');
    const captureCtx    = captureCanvas.getContext('2d');

    function captureFrameBlob(quality = 0.78) {
        const W = 640, H = 480;
        captureCanvas.width  = W;
        captureCanvas.height = H;
        captureCtx.drawImage(video, 0, 0, W, H);
        return new Promise((resolve) => {
            captureCanvas.toBlob((b) => resolve(b), 'image/jpeg', quality);
        });
    }

    // =========
    // mediapipe tasks adapter
    // =========
    const mp = {
        vision:    null,
        detector:  null,
        failStreak: 0,

        async init() {
            if (!nextGenEnabled) throw new Error('NEXTGEN_DISABLED');
            const hasTasks = (typeof window.MpFilesetResolver === 'function' && typeof window.MpFaceDetectorTask === 'function');
            if (!hasTasks) throw new Error('MP_ASSETS_MISSING');

            try {
                this.vision = await window.MpFilesetResolver.forVisionTasks(CFG.tasksVision.wasmBase);
                try {
                    this.detector = await window.MpFaceDetectorTask.createFromOptions(this.vision, {
                        baseOptions: {
                            modelAssetPath: CFG.tasksVision.modelPath,
                            delegate:       'GPU',
                        },
                        minDetectionConfidence: CFG.mp.detectMinConf,
                        minSuppressionThreshold: 0.3,
                    });
                } catch {
                    this.detector = await window.MpFaceDetectorTask.createFromOptions(this.vision, {
                        baseOptions: {
                            modelAssetPath: CFG.tasksVision.modelPath,
                            delegate:       'CPU',
                        },
                        minDetectionConfidence: CFG.mp.detectMinConf,
                        minSuppressionThreshold: 0.3,
                    });
                }
                state.mpMode = 'tasks';
                setKioskMode('tasks');
                log('MediaPipe Tasks ready');
            } catch (e) {
                state.mpMode = 'none';
                log('MediaPipe init failed', e);
                throw e;
            }
        },

        tick() {
            if (state.mpMode !== 'tasks' || !this.detector || !video.videoWidth) return;

            try {
                const now    = performance.now();
                const result = this.detector.detectForVideo(video, now);
                const dets   = result?.detections ?? [];

                // Filter by confidence
                const valid = dets.filter(d => {
                    const s = d.categories?.[0]?.score ?? 0;
                    return s >= CFG.mp.acceptMinScore;
                });

                // Filter out tiny faces (far away)
                const sized = valid.filter(d => !isTooSmallFaceNorm(d.boundingBox));

                // Multi-face check
                const multi = sized.filter(d => {
                    const b = d.boundingBox;
                    return b && b.width * b.height >= CFG.mp.multiMinAreaRatio;
                });

                if (multi.length > 1) {
                    state.faceStatus      = 'multi';
                    state.mpBoxCanvas     = null;
                    state.mpPrevCenter    = null;
                    state.mpReadyToFire   = false;
                    state.mpStableStart   = 0;
                    safeSetPrompt('One face only.', '');
                    return;
                }

                if (sized.length === 0) {
                    state.faceStatus = 'none';
                    state.mpBoxCanvas = null;
                    return;
                }

                // Best face
                const best = sized.reduce((a, b) =>
                    ((a.boundingBox?.width ?? 0) * (a.boundingBox?.height ?? 0)) >
                    ((b.boundingBox?.width ?? 0) * (b.boundingBox?.height ?? 0)) ? a : b);

                const bb  = best.boundingBox;
                const box = mapVideoBoxToCanvas({
                    x: bb.originX * video.videoWidth,
                    y: bb.originY * video.videoHeight,
                    w: bb.width   * video.videoWidth,
                    h: bb.height  * video.videoHeight,
                });

                state.faceStatus  = boxFullyVisibleCanvas(box) ? 'good' : 'low';
                state.mpBoxCanvas = box;
                state.mpFaceSeenAt = Date.now();
                this.failStreak   = 0;

                updateStableTracking(box, Date.now());
            } catch (e) {
                this.failStreak++;
                if (this.failStreak > 30) {
                    state.mpMode = 'none';
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

        const c = { x: box.x + box.w / 2, y: box.y + box.h / 2 };
        if (!state.mpPrevCenter) { state.mpPrevCenter = c; state.mpStableStart = now; return; }

        const move = Math.hypot(c.x - state.mpPrevCenter.x, c.y - state.mpPrevCenter.y);
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
    // eta
    // =========
    function updateEta(facePresent) {
        if (!facePresent)                               { setEta('ETA: idle');              return; }
        if (!state.allowedArea)                         { setEta('ETA: blocked');           return; }
        if (!state.mpBoxCanvas || state.faceStatus === 'none') { setEta('ETA: waiting');   return; }
        if (state.faceStatus === 'low')                 { setEta('ETA: improve lighting'); return; }
        if (state.faceStatus === 'multi')               { setEta('ETA: one face only');    return; }
        if (state.mpReadyToFire)                        { setEta('ETA: scanning');         return; }

        const msLeft = (state.mpStableStart > 0)
            ? Math.max(0, CFG.mp.stableNeededMs - (Date.now() - state.mpStableStart))
            : CFG.mp.stableNeededMs;
        setEta('ETA: hold still (' + (msLeft / 1000).toFixed(1) + 's)');
    }

    // =========
    // unlock UI
    // =========
    function isUnlockAvailable() {
        return !!ui.unlockBackdrop && !!ui.unlockPin && !!ui.unlockSubmit && !!ui.unlockCancel && !!ui.unlockErr;
    }

    function openUnlock() {
        if (!isUnlockAvailable()) return;
        if (state.visitorOpen) closeVisitorModal();
        state.unlockOpen = true;
        ui.unlockErr.textContent = '';
        ui.unlockPin.value = '';
        ui.unlockBackdrop.classList.remove('hidden');
        ui.unlockBackdrop.setAttribute('aria-hidden', 'false');
        ui.kioskRoot?.classList.add('unlockOpen');
        setTimeout(() => ui.unlockPin.focus(), 50);
    }

    function closeUnlock() {
        if (!isUnlockAvailable()) return;
        state.unlockOpen = false;
        ui.unlockBackdrop.classList.add('hidden');
        ui.unlockBackdrop.setAttribute('aria-hidden', 'true');
        ui.kioskRoot?.classList.remove('unlockOpen');
        ui.unlockErr.textContent = '';
        ui.unlockPin.value = '';
    }

    async function submitUnlock() {
        if (!isUnlockAvailable()) return;
        const pin = (ui.unlockPin.value || '').trim();
        if (!pin) { ui.unlockErr.textContent = 'Enter PIN.'; ui.unlockPin.focus(); return; }

        const fd = new FormData();
        fd.append('__RequestVerificationToken', token);
        fd.append('pin', pin);
        fd.append('returnUrl', document.body?.dataset?.returnUrl || '');

        ui.unlockSubmit.disabled = true;
        ui.unlockCancel.disabled = true;
        ui.unlockErr.textContent = '';

        try {
            const r = await fetch(EP.unlockPin, { method: 'POST', body: fd });
            const j = await r.json();
            if (j && j.ok === true) {
                const ru = (j.returnUrl || '').trim();
                closeUnlock();
                if (ru) window.location.href = ru;
                return;
            }
            ui.unlockErr.textContent = 'Invalid PIN.';
            ui.unlockPin.focus();
        } catch {
            ui.unlockErr.textContent = 'Unlock failed.';
        } finally {
            ui.unlockSubmit.disabled = false;
            ui.unlockCancel.disabled = false;
        }
    }

    function wireUnlockUi() {
        if (!isUnlockAvailable()) return;

        ui.unlockCancel.addEventListener('click', closeUnlock);
        ui.unlockSubmit.addEventListener('click', submitUnlock);
        if (ui.unlockClose) ui.unlockClose.addEventListener('click', closeUnlock);

        ui.unlockBackdrop.addEventListener('click', (e) => { if (e.target === ui.unlockBackdrop) closeUnlock(); });

        ui.unlockPin.addEventListener('keydown', (e) => {
            if (e.key === 'Enter')  { e.preventDefault(); submitUnlock(); }
            if (e.key === 'Escape') { e.preventDefault(); closeUnlock(); }
        });

        document.addEventListener('keydown', (e) => {
            if (!isUnlockAvailable()) return;
            if (state.unlockOpen) return;
            const isSpace = (e.code === 'Space') || (e.key === ' ') || (e.keyCode === 32);
            if (e.ctrlKey && e.shiftKey && isSpace) {
                e.preventDefault();
                if (state.visitorOpen) closeVisitorModal();
                openUnlock();
            }
        }, true);

        document.querySelector('#topLeft .brand')?.addEventListener('dblclick', openUnlock);
    }

    // =========
    // GPS + office resolve
    // =========
    function startGpsIfAvailable() {
        if (!('geolocation' in navigator)) {
            state.allowedArea = false;
            if (ui.officeLine) ui.officeLine.textContent = 'GPS not available';
            if (!isMobile) resolveOfficeDesktopOnce();
            return;
        }
        const isSecure = (location.protocol === 'https:' || location.hostname === 'localhost' || location.hostname === '127.0.0.1');
        if (!isSecure) {
            state.allowedArea = false;
            if (ui.officeLine) ui.officeLine.textContent = 'GPS needs HTTPS';
            if (!isMobile) resolveOfficeDesktopOnce();
            return;
        }

        navigator.geolocation.watchPosition(
            (pos) => {
                state.gps.lat      = pos.coords.latitude;
                state.gps.lon      = pos.coords.longitude;
                state.gps.accuracy = pos.coords.accuracy;
            },
            (err) => {
                state.gps.lat = state.gps.lon = state.gps.accuracy = null;
                let msg = 'GPS error';
                if (err?.code === 1) msg = 'GPS denied';
                else if (err?.code === 2) msg = 'GPS unavailable';
                else if (err?.code === 3) msg = 'GPS timeout';
                state.allowedArea = false;
                if (ui.officeLine) ui.officeLine.textContent = msg;
                if (!isMobile) resolveOfficeDesktopOnce();
            },
            { enableHighAccuracy: true, maximumAge: 500, timeout: 6000 }
        );
    }

    async function resolveOfficeIfNeeded() {
        if (!state.localPresent) return;
        const t = Date.now();
        if (t - state.lastResolveAt < CFG.server.resolveMs) return;
        state.lastResolveAt = t;

        if (state.gps.lat == null || state.gps.lon == null || state.gps.accuracy == null) {
            if (!isMobile) {
                await resolveOfficeDesktopOnce();
                if (state.allowedArea !== false) state.allowedArea = true;
                return;
            }
            state.allowedArea = false;
            if (ui.officeLine) ui.officeLine.textContent = 'Locating.';
            return;
        }

        const fd = new FormData();
        fd.append('__RequestVerificationToken', token);
        fd.append('lat', state.gps.lat);
        fd.append('lon', state.gps.lon);
        fd.append('accuracy', state.gps.accuracy);

        const r = await fetch(EP.resolveOffice, { method: 'POST', body: fd });
        const j = await r.json();

        if (!j || j.ok !== true) {
            resetScanState();
            setPrompt('Scan error.', (j?.error) ? String(j.error) : 'Try again.');
            return;
        }
        if (j.allowed === false) {
            state.allowedArea = false;
            if (ui.officeLine) ui.officeLine.textContent = 'Not in allowed area';
            setPrompt('Not in allowed area.', 'Move closer to a designated office.');
            return;
        }

        state.allowedArea = !!j.allowed;
        if (state.allowedArea) {
            state.currentOffice.id   = j.officeId;
            state.currentOffice.name = j.officeName;
            if (ui.officeLine) ui.officeLine.textContent = state.currentOffice.name || 'Office OK';
        }
    }

    async function resolveOfficeDesktopOnce() {
        if (isMobile) return;
        if (state.currentOffice && state.currentOffice.name) return;
        try {
            const fd = new FormData();
            fd.append('__RequestVerificationToken', token);
            const r = await fetch(EP.resolveOffice, { method: 'POST', body: fd });
            const j = await r.json();
            if (j?.ok === true && j.allowed !== false) {
                state.allowedArea       = true;
                state.currentOffice.id   = j.officeId;
                state.currentOffice.name = j.officeName;
                if (ui.officeLine && state.currentOffice.name) ui.officeLine.textContent = state.currentOffice.name;
            }
        } catch { }
    }

    // =========
    // attendance submit
    // QA-OPT-09: AbortController — cancels previous stale request before sending new one
    // =========
    async function submitAttendance(blob) {
        // Cancel any previous in-flight request
        if (state.attendAbortCtrl) {
            try { state.attendAbortCtrl.abort(); } catch { }
        }
        state.attendAbortCtrl = new AbortController();
        const signal = state.attendAbortCtrl.signal;

        state.lastCaptureAt = Date.now();
        setPrompt('Scanning...', 'Hold still.');

        const fd = new FormData();
        fd.append('__RequestVerificationToken', token);
        fd.append('image', blob, 'capture.jpg');
        if (state.gps.lat      != null) fd.append('lat',      state.gps.lat);
        if (state.gps.lon      != null) fd.append('lon',      state.gps.lon);
        if (state.gps.accuracy != null) fd.append('accuracy', state.gps.accuracy);

        try {
            const r = await fetch(EP.attend, { method: 'POST', body: fd, credentials: 'same-origin', signal });
            if (r.status === 429) { setPrompt('System busy.', 'Please wait.'); return; }

            const j = await r.json();

            // Liveness UI update
            if (j && typeof j.liveness === 'number') {
                const p  = Number(j.liveness);
                const th = (j.threshold != null) ? Number(j.threshold) : null;
                if (p >= (th ?? 0.75))     setLiveness(p, th, 'live-pass');
                else if (p >= (th ?? 0.75) * 0.80) setLiveness(p, th, 'live-near');
                else                                setLiveness(p, th, 'live-fail');
                state.latestLiveness   = p;
                state.livenessThreshold = th ?? 0.75;
            }

            if (j && j.ok === true) {
                // ── Employee attendance success ──
                if (j.mode !== 'VISITOR') {
                    const evt  = (j.eventType || '').toUpperCase();
                    const name = j.displayName || j.name || 'Employee';
                    toastSuccess((evt === 'IN' ? '✓ Time In' : '✓ Time Out') + ' — ' + name);
                    setPrompt(evt === 'IN' ? 'Time In recorded.' : 'Time Out recorded.', name);
                    armPostScanHold(CFG.postScan.holdMs);
                } else {
                    // ── Visitor mode ──
                    openVisitorModal(j);
                }
            } else {
                const err = j?.error || '';
                if (err === 'ALREADY_SCANNED') {
                    toastError('Already scanned. Please wait.');
                    armPostScanHold(CFG.postScan.holdMs);
                } else if (err === 'LIVENESS_FAIL') {
                    toastError('Liveness failed. Move naturally and try again.');
                    armPostScanHold(1500);
                } else {
                    toastError(j?.message || err);
                    armPostScanHold(1500);
                }
                setPrompt('Ready.', 'Stand still. One face only.');
            }
        } catch (e) {
            if (e?.name === 'AbortError') {
                log('Attendance fetch aborted (stale)');
                return;
            }
            setPrompt('System error.', 'Reload the page or check the server.');
        }
    }

    // =========
    // camera
    // =========
    async function startCamera() {
        const stream = await navigator.mediaDevices.getUserMedia({
            video: {
                facingMode: 'user',
                width:     { ideal: 640, max: 640 },
                height:    { ideal: 480, max: 480 },
                frameRate: { ideal: 15,  max: 15  },
            },
            audio: false,
        });
        video.srcObject = stream;
        await video.play();
    }

    // =========
    // main loop
    // =========
    async function loop() {
        try {
            if (state.unlockOpen) return;
            if (!video.videoWidth || !video.videoHeight) return;

            const now = Date.now();
            mp.tick();

            const facePresent = (state.mpFaceSeenAt > 0 && (now - state.mpFaceSeenAt) < CFG.idle.faceLostMs) || state.localPresent;

            if (!facePresent) {
                if (!state.wasIdle) {
                    resetScanState();
                    setPrompt('Idle.', 'Look at the camera.');
                    setEta('ETA: idle');
                }
                state.wasIdle = true;
                setIdleUi(true);
                return;
            }

            if (state.wasIdle) {
                resetScanState();
                setPrompt('Ready.', 'Look at the camera.');
            }
            state.wasIdle = false;
            setIdleUi(false);

            await resolveOfficeIfNeeded();
            if (!state.allowedArea) { updateEta(true); return; }

            if (now < state.backoffUntil) {
                setPrompt('System busy.', 'Please wait.');
                updateEta(true);
                return;
            }
            if (state.visitorOpen)           { updateEta(true); return; }
            if (now < (state.scanBlockUntil || 0)) {
                safeSetPrompt('Please wait.', 'Next scan will be ready soon.');
                updateEta(true);
                return;
            }
            if (state.mpMode !== 'tasks') {
                safeSetPrompt('System not ready.', 'Face detection is not available.');
                updateEta(true);
                return;
            }

            if (state.mpReadyToFire && (now - state.lastCaptureAt) > CFG.server.captureCooldownMs) {
                const blob = await captureFrameBlob();
                if (blob) {
                    state.mpReadyToFire = false;
                    state.mpStableStart = 0;
                    state.liveInFlight  = true;
                    try {
                        await submitAttendance(blob);
                    } finally {
                        state.liveInFlight = false;
                    }
                }
            }

            updateEta(true);
        } catch (e) {
            if (CFG.debug) console.warn('[FaceAttend] loop error', e);
            setPrompt('System error.', 'Reload the page or check the server.');
            setEta('ETA: --');
        } finally {
            setTimeout(loop, CFG.loopMs);
        }
    }

    // =========
    // init
    // =========
    (async function init() {
        if (!validateConfig()) return;

        startClock();
        startGpsIfAvailable();
        resolveOfficeDesktopOnce();
        wireUnlockUi();
        wireVisitorUi();

        try {
            await startCamera();
            await mp.init();

            setIdleUi(true);
            setPrompt('Idle.', 'Look at the camera.');
            setEta('ETA: idle');
            setLiveness(null, null, 'live-unk');

            drawLoop();
            localSenseLoop();
            loop();
        } catch (e) {
            setIdleUi(false);
            const msg = (e && e.message) ? String(e.message) : '';
            if (msg === 'NEXTGEN_DISABLED' || msg === 'MP_ASSETS_MISSING') {
                setPrompt('System not ready.', 'Face detection assets are missing.');
            } else {
                setPrompt('Camera blocked.', 'Allow camera permission.');
            }
            setEta('ETA: --');
        }
    })();

})();
