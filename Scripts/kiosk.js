(function () {
    'use strict';

    // =========
    // dom
    // =========
    const el = (id) => document.getElementById(id);

    const video = el('kioskVideo');
    const canvas = el('overlayCanvas');
    const ctx = canvas.getContext('2d');

    const ui = {
        officeLine: el('officeLine'),
        timeLine: el('timeLine'),
        dateLine: el('dateLine'),
        livenessLine: el('livenessLine'),
        scanEtaLine: el('scanEtaLine'),

        unlockBackdrop: el('unlockBackdrop'),
        unlockPin: el('unlockPin'),
        unlockErr: el('unlockErr'),
        unlockCancel: el('unlockCancel'),
        unlockSubmit: el('unlockSubmit'),
        unlockClose: el('unlockClose'),


        visitorBackdrop: el('visitorBackdrop'),
        visitorNameRow: el('visitorNameRow'),
        visitorName: el('visitorName'),
        visitorPurpose: el('visitorPurpose'),
        visitorErr: el('visitorErr'),
        visitorCancel: el('visitorCancel'),
        visitorSubmit: el('visitorSubmit'),
        visitorClose: el('visitorClose'),

        kioskRoot: el('kioskRoot'),
        idleOverlay: el('idleOverlay'),

        mainPrompt: el('mainPrompt'),
        subPrompt: el('subPrompt'),
    };

    const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
    const appBase = (document.body.getAttribute('data-app-base') || '/').replace(/\/?$/, '/');
    const nextGenEnabled = (document.body.getAttribute('data-nextgen') || 'false').toLowerCase() === 'true';

    // =========
    // config (kept small, only what is used)
    // =========
    const CFG = {
        debug: false,

        loopMs: 120,

        mp: {
            detectMinConf: 0.30,
            acceptMinScore: 0.60,
            stableFramesMin: 2,
            stableNeededMs: 250,
            multiMinAreaRatio: 0.015,
        },

        idle: {
            senseMs: 250,
            faceLostMs: 2000,
            motionMin: 2.0,
        },

        server: {
            resolveMs: 1200,
            captureCooldownMs: 3000,
        },

        postScan: {
            holdMs: 5000,
            requireFaceGoneMs: 1200,
            toastMs: 6500,
        },

        gating: {
            stableFramesRequired: 4,
            stableMaxMovePx: 10,
            minFaceAreaRatio: 0.03,
            safeEdgeMarginRatio: 0.05,
            centerMin: 0.12,
            centerMax: 0.88,
        },

        antiSpoof: {
            motionW: 64,
            motionH: 48,
            motionWindow: 6,
            motionDiffMin: 1.2,
        },

        tasksVision: {
            wasmBase: appBase + 'Scripts/vendor/mediapipe/tasks-vision/wasm',
            modelPath: appBase + 'Scripts/vendor/mediapipe/tasks-vision/models/blaze_face_short_range.tflite',
        },
    };

    const log = (...args) => { if (CFG.debug) console.log('[FaceAttend]', ...args); };

    // =========
    // endpoints (only used ones)
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
    const ua = navigator.userAgent || '';
    const isMobile = /Android|iPhone|iPad|iPod|Mobile|Tablet/i.test(ua);
    const state = {
        // ui flow
        unlockOpen: false,
        wasIdle: true,

        visitorOpen: false,
        pendingVisitor: null,

        scanBlockUntil: 0,
        requireFaceGone: false,
        faceGoneSince: 0,

        // gps/office
        gps: { lat: null, lon: null, accuracy: null },
        allowedArea: !isMobile,
        currentOffice: { id: null, name: null },
        lastResolveAt: 0,

        // timing
        backoffUntil: 0,
        lastCaptureAt: 0,

        // mp status
        mpMode: 'none',         // 'tasks' | 'none'
        mpReadyToFire: false,
        mpStableStart: 0,
        mpFaceSeenAt: 0,
        faceStatus: 'none',     // 'none' | 'low' | 'good' | 'multi'
        mpRawCount: 0,
        mpAcceptedCount: 0,
        mpBoxCanvas: null,
        mpPrevCenter: null,

        // liveness display (ui)
        latestLiveness: null,
        livenessThreshold: 0.75,

        // motion history
        motionDiffNow: null,
        frameDiffs: [],

        // in-flight
        liveInFlight: false,

        // local sensing
        localSeenAt: 0,
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
        if (ui.subPrompt) ui.subPrompt.textContent = b || '';
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
            const isOk = type === 'success';
            Toastify({
                text: msg,
                duration: CFG.postScan.toastMs,
                close: true,
                gravity: 'bottom',
                position: 'right',
                stopOnFocus: true,
                style: { background: isOk ? '#16a34a' : (type === 'info' ? '#2563eb' : '#dc2626') },
            }).showToast();
        } else {
            console.log('[toast]', type, msg);
        }
    }

    const toastSuccess = (t) => toast('success', t);
    const toastError   = (t) => toast('error', t);
    const toastInfo    = (t) => toast('info', t);

    function armPostScanHold(ms) {
        const now = Date.now();
        const hold = (typeof ms === 'number' && isFinite(ms) && ms > 0) ? ms : CFG.postScan.holdMs;

        state.scanBlockUntil = Math.max(state.scanBlockUntil || 0, now + hold);
        state.requireFaceGone = true;
        state.faceGoneSince = 0;
    }

    function openVisitorModal(payload) {
        if (state.unlockOpen) return;
        if (state.visitorOpen) return;
        state.visitorOpen = true;
        state.pendingVisitor = payload || null;

        if (ui.visitorErr) ui.visitorErr.textContent = '';

        const isKnown = !!payload?.isKnown;
        const name = payload?.visitorName || '';

        if (ui.visitorNameRow) ui.visitorNameRow.classList.toggle('hidden', isKnown);
        if (ui.visitorName) {
            ui.visitorName.value = name;
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
        state.visitorOpen = false;
        state.pendingVisitor = null;

        if (ui.visitorBackdrop) {
            ui.visitorBackdrop.classList.add('hidden');
            ui.visitorBackdrop.setAttribute('aria-hidden', 'true');
        }

        armPostScanHold(1500);
        setPrompt('Ready.', 'Stand still. One face only.');
    }

    async function submitVisitorForm() {
        const scanId = state.pendingVisitor?.scanId || '';
        const isKnown = !!state.pendingVisitor?.isKnown;

        const name = (ui.visitorName?.value || '').trim();
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
            if (r.status === 429) {
                toastError('System busy. Please wait.');
                return;
            }
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

        ui.visitorBackdrop?.addEventListener('click', (e) => {
            if (e.target === ui.visitorBackdrop) close();
        });

        document.addEventListener('keydown', (e) => {
            if (!state.visitorOpen) return;
            if (e.key === 'Escape') { e.preventDefault(); close(); }
            if (e.key === 'Enter')  { e.preventDefault(); submitVisitorForm(); }
        });
    }


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

    function nowText() {
        const d = new Date();
        if (ui.timeLine) ui.timeLine.textContent = d.toLocaleTimeString('en-PH', { hour12: false });
        if (ui.dateLine) ui.dateLine.textContent = d.toLocaleDateString('en-PH');
    }

    function startClock() {
        nowText();
        setInterval(nowText, 1000);
    }

    function setLiveness(p, th, cls) {
        if (!ui.livenessLine) return;

        const hasP = (typeof p === 'number') && isFinite(p);
        const hasTh = (typeof th === 'number') && isFinite(th);

        ui.livenessLine.textContent = hasP
            ? ('Live: ' + p.toFixed(2) + (hasTh ? (' / ' + th.toFixed(2)) : ''))
            : 'Live: --';

        ui.livenessLine.classList.remove('live-pass', 'live-near', 'live-fail', 'live-unk');
        ui.livenessLine.classList.add(cls || 'live-unk');
    }

    // =========
    // overlay helpers (mediapipe)
    // =========
    function resizeCanvas() {
        const w = canvas.clientWidth;
        const h = canvas.clientHeight;
        if (canvas.width !== w || canvas.height !== h) {
            canvas.width = w;
            canvas.height = h;
        }
    }

    function mapVideoBoxToCanvas(vbox) {
        if (!vbox || !video.videoWidth || !video.videoHeight) return null;

        const W = canvas.width;
        const H = canvas.height;
        const imgW = video.videoWidth;
        const imgH = video.videoHeight;

        // video is rendered as cover, match the same math here
        const scale = Math.max(W / imgW, H / imgH);
        const renderW = imgW * scale;
        const renderH = imgH * scale;

        const offX = (W - renderW) / 2;
        const offY = (H - renderH) / 2;

        let x = offX + (vbox.x * scale);
        const y = offY + (vbox.y * scale);
        const w = vbox.w * scale;
        const h = vbox.h * scale;

        // mirror (video is mirrored in UI)
        x = W - (x + w);

        return { x, y, w, h };
    }

    function boxFullyVisibleCanvas(box) {
        if (!box) return false;

        const m = Math.min(canvas.width, canvas.height) * CFG.gating.safeEdgeMarginRatio;
        if (box.w <= 0 || box.h <= 0) return false;
        if (box.x < m) return false;
        if (box.y < m) return false;
        if ((box.x + box.w) > (canvas.width - m)) return false;
        if ((box.y + box.h) > (canvas.height - m)) return false;
        return true;
    }

    function isTooSmallFaceNorm(bbox) {
        if (!bbox || !isFinite(bbox.width) || !isFinite(bbox.height)) return true;
        const ratio = bbox.width * bbox.height;
        return ratio < CFG.gating.minFaceAreaRatio;
    }

    function drawLoop() {
        resizeCanvas();
        ctx.clearRect(0, 0, canvas.width, canvas.height);

        if (state.mpBoxCanvas) {
            const good = (state.faceStatus === 'good');
            const color = good ? '#00ff7a' : '#ffcc00';

            ctx.save();
            ctx.lineWidth = 4;
            ctx.strokeStyle = color;
            ctx.strokeRect(state.mpBoxCanvas.x, state.mpBoxCanvas.y, state.mpBoxCanvas.w, state.mpBoxCanvas.h);
            ctx.restore();
        }

        requestAnimationFrame(drawLoop);
    }

    function resetScanState() {
        state.mpBoxCanvas = null;
        state.mpPrevCenter = null;

        state.faceStatus = 'none';
        state.mpRawCount = 0;
        state.mpAcceptedCount = 0;
        state.mpReadyToFire = false;
        state.mpStableStart = 0;
        state.mpFaceSeenAt = 0;

        state.frameDiffs = [];

        state.latestLiveness = null;
        setLiveness(null, null, 'live-unk');
        setEta('ETA: --');
    }

    // =========
    // motion sense (single owner of updateSenseDiff, no double sampling)
    // =========
    const senseCanvas = document.createElement('canvas');
    senseCanvas.width = CFG.antiSpoof.motionW;
    senseCanvas.height = CFG.antiSpoof.motionH;
    const senseCtx = senseCanvas.getContext('2d', { willReadFrequently: true });
    let lastSenseData = null;

    function updateSenseDiff() {
        if (!video.videoWidth || !video.videoHeight) return null;

        senseCtx.drawImage(video, 0, 0, CFG.antiSpoof.motionW, CFG.antiSpoof.motionH);
        const data = senseCtx.getImageData(0, 0, CFG.antiSpoof.motionW, CFG.antiSpoof.motionH).data;

        if (!lastSenseData) {
            lastSenseData = new Uint8ClampedArray(data);
            return null;
        }

        let sum = 0;
        for (let i = 0; i < data.length; i += 4) {
            sum += Math.abs(data[i] - lastSenseData[i]);
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

            if (diff != null && diff >= CFG.idle.motionMin) {
                state.localSeenAt = Date.now();
            }

            state.localPresent = (Date.now() - state.localSeenAt) <= CFG.idle.faceLostMs;
        } finally {
            setTimeout(localSenseLoop, CFG.idle.senseMs);
        }
    }

    // =========
    // camera capture
    // =========
    const captureCanvas = document.createElement('canvas');
    const captureCtx = captureCanvas.getContext('2d');

    function captureFrameBlob(quality = 0.85) {
        const W = 640, H = 480;
        captureCanvas.width = W;
        captureCanvas.height = H;
        captureCtx.drawImage(video, 0, 0, W, H);

        return new Promise((resolve) => {
            captureCanvas.toBlob((b) => resolve(b), 'image/jpeg', quality);
        });
    }

    // =========
    // mediapipe tasks adapter (only tasks mode, simpler)
    // =========
    const mp = {
        vision: null,
        detector: null,
        failStreak: 0,

        async init() {
            if (!nextGenEnabled) {
                throw new Error('NEXTGEN_DISABLED');
            }

            const hasTasks = (typeof window.MpFilesetResolver === 'function' && typeof window.MpFaceDetectorTask === 'function');
            if (!hasTasks) {
                throw new Error('MP_ASSETS_MISSING');
            }

            try {
                this.vision = await window.MpFilesetResolver.forVisionTasks(CFG.tasksVision.wasmBase);

                try {
                    this.detector = await window.MpFaceDetectorTask.createFromOptions(this.vision, {
                        baseOptions: { modelAssetPath: CFG.tasksVision.modelPath, delegate: 'GPU' },
                        runningMode: 'VIDEO',
                        minDetectionConfidence: CFG.mp.detectMinConf,
                    });
                } catch {
                    this.detector = await window.MpFaceDetectorTask.createFromOptions(this.vision, {
                        baseOptions: { modelAssetPath: CFG.tasksVision.modelPath, delegate: 'CPU' },
                        runningMode: 'VIDEO',
                        minDetectionConfidence: CFG.mp.detectMinConf,
                    });
                }

                state.mpMode = 'tasks';
                setKioskMode('nextgen');
                log('mp mode: tasks');
            } catch (e) {
                console.warn('[FaceAttend] tasks init failed', e);
                this.vision = null;
                this.detector = null;
                state.mpMode = 'none';
                setKioskMode('nextgen');
                throw e;
            }
        },

        tick() {
            if (state.mpMode !== 'tasks') return;
            if (!this.detector) return;

            try {
                const t = performance.now();
                const res = this.detector.detectForVideo(video, t);
                const list = toNormDetectionsFromTasks(res?.detections || []);
                handleMpDetections(list);
                this.failStreak = 0;
            } catch (e) {
                this.failStreak++;
                if (this.failStreak >= 5) {
                    console.warn('[FaceAttend] tasks detect failed', e);
                    state.mpMode = 'none';
                    state.mpReadyToFire = false;
                    safeSetPrompt('Face detection error.', 'Reload the page.');
                }
            }
        },
    };

    function toNormDetectionsFromTasks(dets) {
        const vw = video.videoWidth || 1;
        const vh = video.videoHeight || 1;

        return (dets || [])
            .map((d) => {
                const score = (d?.categories && d.categories.length > 0) ? (d.categories[0].score ?? 0) : 0;
                const bb = d?.boundingBox;
                if (!bb) return null;

                const xCenter = (bb.originX + (bb.width / 2)) / vw;
                const yCenter = (bb.originY + (bb.height / 2)) / vh;
                const width = bb.width / vw;
                const height = bb.height / vh;

                return { score, bbox: { xCenter, yCenter, width, height } };
            })
            .filter(x => x && x.bbox && isFinite(x.score));
    }

    // =========
    // mp gating
    // =========
    function handleMpDetections(normList) {
        const now = Date.now();
        state.mpReadyToFire = false;

        if (!normList || normList.length === 0) {
            state.faceStatus = 'none';

            if (state.requireFaceGone) {
                if (!state.faceGoneSince) state.faceGoneSince = now;
            } else {
                state.faceGoneSince = 0;
            }

            state.mpRawCount = 0;
            state.mpAcceptedCount = 0;
            state.mpStableStart = 0;
            state.mpFaceSeenAt = 0;

            state.mpBoxCanvas = null;
            state.mpPrevCenter = null;
            return;
        }

        const rawScored = (normList || [])
            .filter(x => x && x.bbox)
            .sort((a, b) => b.score - a.score);

        state.mpRawCount = rawScored.length;
        state.mpFaceSeenAt = now;
        state.faceGoneSince = 0;

        const bestRaw = rawScored[0];
        const accepted = rawScored.filter(x => x.score >= CFG.mp.acceptMinScore);
        state.mpAcceptedCount = accepted.length;

        // guidance box (always show best box)
        const vw = video.videoWidth || 1;
        const vh = video.videoHeight || 1;
        const b0 = bestRaw.bbox;
        const vbox = {
            x: (b0.xCenter - b0.width / 2) * vw,
            y: (b0.yCenter - b0.height / 2) * vh,
            w: b0.width * vw,
            h: b0.height * vh,
        };
        state.mpBoxCanvas = mapVideoBoxToCanvas(vbox);

        // multiple faces
        if (rawScored.length > 1) {
            const second = rawScored[1];
            const area2 = (second.bbox.width || 0) * (second.bbox.height || 0);
            if (second.score >= CFG.mp.acceptMinScore && area2 >= CFG.mp.multiMinAreaRatio) {
                state.faceStatus = 'multi';
                state.mpStableStart = 0;
                state.mpPrevCenter = null;
                safeSetPrompt('Multiple faces detected.', 'One face only.');
                return;
            }
        }

        // low confidence
        if (accepted.length === 0) {
            state.faceStatus = 'low';
            state.mpStableStart = 0;
            state.mpPrevCenter = null;

            if (isTooSmallFaceNorm(b0)) {
                safeSetPrompt('Move closer.', 'Face is too far.');
            } else {
                safeSetPrompt('Look at the camera.', 'Improve lighting or move closer.');
            }
            return;
        }

        // good confidence
        state.faceStatus = 'good';

        const b = accepted[0].bbox;

        if (isTooSmallFaceNorm(b)) {
            state.mpStableStart = 0;
            state.mpPrevCenter = null;
            safeSetPrompt('Move closer.', 'Face is too far.');
            return;
        }

        if (!boxFullyVisibleCanvas(state.mpBoxCanvas)) {
            state.mpStableStart = 0;
            state.mpPrevCenter = null;
            safeSetPrompt('Move into frame.', 'Keep your full face in view.');
            return;
        }

        if (b.xCenter < CFG.gating.centerMin || b.xCenter > CFG.gating.centerMax ||
            b.yCenter < CFG.gating.centerMin || b.yCenter > CFG.gating.centerMax) {
            state.mpStableStart = 0;
            state.mpPrevCenter = null;
            safeSetPrompt('Center your face.', '');
            return;
        }

        // motion gate (anti-spoof)
        if (state.frameDiffs.length >= CFG.antiSpoof.motionWindow) {
            const motionAvg = avg(state.frameDiffs);
            if (motionAvg != null && motionAvg < CFG.antiSpoof.motionDiffMin) {
                state.mpStableStart = 0;
                state.mpPrevCenter = null;
                safeSetPrompt('Move slightly.', 'Breathe naturally.');
                return;
            }
        }

        // stability gate (center movement)
        if (!state.mpBoxCanvas) {
            state.mpStableStart = 0;
            state.mpPrevCenter = null;
            safeSetPrompt('Hold still.', '');
            return;
        }

        const c = {
            x: state.mpBoxCanvas.x + state.mpBoxCanvas.w / 2,
            y: state.mpBoxCanvas.y + state.mpBoxCanvas.h / 2,
        };

        if (!state.mpPrevCenter) {
            state.mpPrevCenter = c;
            state.mpStableStart = 0;
            safeSetPrompt('Hold still.', '');
            return;
        }

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
        if (!facePresent) { setEta('ETA: idle'); return; }

        if (!state.allowedArea) { setEta('ETA: blocked'); return; }

        if (!state.mpBoxCanvas || state.faceStatus === 'none') { setEta('ETA: waiting'); return; }
        if (state.faceStatus === 'low') { setEta('ETA: improve lighting'); return; }
        if (state.faceStatus === 'multi') { setEta('ETA: one face only'); return; }

        if (state.requireFaceGone) { setEta('ETA: step away'); return; }

        if (state.mpReadyToFire) { setEta('ETA: scanning'); return; }

        const msLeft = (state.mpStableStart > 0)
            ? Math.max(0, CFG.mp.stableNeededMs - (Date.now() - state.mpStableStart))
            : CFG.mp.stableNeededMs;

        setEta('ETA: hold still (' + (msLeft / 1000).toFixed(1) + 's)');
    }

    // =========
    // unlock ui
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

        ui.unlockBackdrop.addEventListener('click', (e) => {
            if (e.target === ui.unlockBackdrop) closeUnlock();
        });

        ui.unlockPin.addEventListener('keydown', (e) => {
            if (e.key === 'Enter') { e.preventDefault(); submitUnlock(); }
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
    // gps + office resolve
    // =========
    function startGpsIfAvailable() {
        if (!('geolocation' in navigator)) {
            state.allowedArea = false;
            if (ui.officeLine) ui.officeLine.textContent = 'GPS not available';
            if (!isMobile) resolveOfficeDesktopOnce();
            return;
        }

        // Geolocation only works on HTTPS (or localhost).
        const isSecure = (location.protocol === 'https:' || location.hostname === 'localhost' || location.hostname === '127.0.0.1');
        if (!isSecure) {
            state.allowedArea = false;
            if (ui.officeLine) ui.officeLine.textContent = 'GPS needs HTTPS';
            if (!isMobile) resolveOfficeDesktopOnce();
            return;
        }

        navigator.geolocation.watchPosition(
            (pos) => {
                state.gps.lat = pos.coords.latitude;
                state.gps.lon = pos.coords.longitude;
                state.gps.accuracy = pos.coords.accuracy;
            },
            (err) => {
                state.gps.lat = null;
                state.gps.lon = null;
                state.gps.accuracy = null;

                let msg = 'GPS error';
                if (err && err.code === 1) msg = 'GPS denied';
                else if (err && err.code === 2) msg = 'GPS unavailable';
                else if (err && err.code === 3) msg = 'GPS timeout';

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
            // Desktop kiosks: GPS may be unavailable. Fall back to server default office.
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
            setPrompt('Scan error.', (j && j.error) ? String(j.error) : 'Try again.');
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
            state.currentOffice.id = j.officeId;
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
            if (j && j.ok === true && j.allowed !== false) {
                state.allowedArea = true;
                state.currentOffice.id = j.officeId;
                state.currentOffice.name = j.officeName;
                if (ui.officeLine && state.currentOffice.name) ui.officeLine.textContent = state.currentOffice.name;
            }
        } catch { }
    }

    // =========
    // attendance submit (updates liveness line)
    // =========
    async function submitAttendance(blob) {
        state.lastCaptureAt = Date.now();
        setPrompt('Processing...', 'Please wait.');

        const fd = new FormData();
        fd.append('__RequestVerificationToken', token);
        fd.append('image', blob, 'capture.jpg');

        if (state.gps.lat != null) fd.append('lat', state.gps.lat);
        if (state.gps.lon != null) fd.append('lon', state.gps.lon);
        if (state.gps.accuracy != null) fd.append('accuracy', state.gps.accuracy);

        try {
            const r = await fetch(EP.attend, { method: 'POST', body: fd, credentials: 'same-origin' });
            if (r.status === 429) {
                setPrompt('System busy.', 'Please wait.');
                return;
            }

            const j = await r.json();

            // liveness ui update (this is what was missing)
            if (j && typeof j.liveness === 'number') {
                const p = Number(j.liveness);
                const th = (j.threshold != null) ? Number(j.threshold) : state.livenessThreshold;

                if (isFinite(th)) state.livenessThreshold = th;
                state.latestLiveness = p;

                let cls = 'live-unk';
                if (j.ok === true) cls = 'live-pass';
                else if (j.error === 'LIVENESS_FAIL') cls = (isFinite(th) && p >= (th - 0.03)) ? 'live-near' : 'live-fail';

                setLiveness(p, th, cls);
            }

            // visitor mode
            if (j && j.mode === 'VISITOR') {
                if (state.unlockOpen) return;
                if (state.visitorOpen) return;
                openVisitorModal({
                    scanId: j.scanId,
                    isKnown: !!j.isKnown,
                    visitorName: j.visitorName || '',
                });
                return;
            }

            if (j && j.ok) {
                const displayName = j.displayName || j.name || '';
                const msg = (j.message || 'Recorded.').toString();
                toastSuccess(displayName ? (displayName + ' — ' + msg) : msg);

                setPrompt('Ready.', 'Stand still. One face only.');
                armPostScanHold(CFG.postScan.holdMs);
            } else {
                const err = (j?.error || 'Failed.').toString();

                if (err === 'TOO_SOON') {
                    toastError(j?.message || 'Already scanned. Please wait before scanning again.');
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
        } catch {
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
                width: { ideal: 640, max: 640 },
                height: { ideal: 480, max: 480 },
                frameRate: { ideal: 15, max: 15 },
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

            const facePresent =
                ((state.mpFaceSeenAt > 0 && (now - state.mpFaceSeenAt) < CFG.idle.faceLostMs) || state.localPresent);

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

            if (state.visitorOpen) {
                updateEta(true);
                return;
            }

            if (now < (state.scanBlockUntil || 0)) {
                safeSetPrompt('Please wait.', 'Next scan will be ready soon.');
                updateEta(true);
                return;
            }

            if (state.requireFaceGone) {
                const goneForMs = state.faceGoneSince ? (now - state.faceGoneSince) : 0;
                if (goneForMs >= CFG.postScan.requireFaceGoneMs) {
                    state.requireFaceGone = false;
                    state.faceGoneSince = 0;
                    setPrompt('Ready.', 'Stand still. One face only.');
                } else {
                    safeSetPrompt('Step away.', 'Move away from the camera.');
                    updateEta(true);
                    return;
                }
            }

            if (state.mpMode !== 'tasks') {
                safeSetPrompt('System not ready.', 'Face detection is not available.');
                updateEta(true);
                return;
            }

            if (state.mpReadyToFire && (now - state.lastCaptureAt) > CFG.server.captureCooldownMs) {
                const blob = await captureFrameBlob(0.85);
                if (blob) {
                    state.mpReadyToFire = false;
                    state.mpStableStart = 0;

                    state.liveInFlight = true;
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
