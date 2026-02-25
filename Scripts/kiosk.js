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
            detectMs: 450,
            resolveMs: 1200,
            captureCooldownMs: 3000,
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
        unlockPin: appBase + 'Kiosk/UnlockPin',
        resolveOffice: appBase + 'Kiosk/ResolveOffice',
        detectFace: appBase + 'Kiosk/DetectFace',
        scanAttendance: appBase + 'Kiosk/ScanAttendance',
        attend: appBase + 'Kiosk/Attend',
    };

    // =========
    // state
    // =========
    const ua = navigator.userAgent || '';
    const isMobile = /Android|iPhone|iPad|iPod|Mobile|Tablet/i.test(ua);

    let nextGenActive = nextGenEnabled;

    const state = {
        // ui flow
        unlockOpen: false,
        wasIdle: true,

        // gps/office
        gps: { lat: null, lon: null, accuracy: null },
        allowedArea: !isMobile,
        currentOffice: { id: null, name: null },
        lastResolveAt: 0,

        // timing
        backoffUntil: 0,
        lastCaptureAt: 0,
        lastDetectAt: 0,

        // box + stability
        boxRaw: null,
        boxSmooth: null,
        lastCenters: [],
        stableFrames: 0,

        // mp status
        mpMode: 'none',         // 'tasks' | 'none'
        mpReadyToFire: false,
        mpStableStart: 0,
        mpFaceSeenAt: 0,
        faceStatus: 'none',     // 'none' | 'low' | 'good' | 'multi'
        mpRawCount: 0,
        mpAcceptedCount: 0,

        // legacy/server status
        serverFaceSeenAt: 0,
        faceCount: 0,

        // liveness display (ui)
        latestLiveness: null,
        livenessThreshold: 0.75,

        // motion history
        motionDiffNow: null,
        frameDiffs: [],

        // in-flight
        detectInFlight: false,
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
    // box math
    // =========
    function resizeCanvas() {
        const w = canvas.clientWidth;
        const h = canvas.clientHeight;
        if (canvas.width !== w || canvas.height !== h) {
            canvas.width = w;
            canvas.height = h;
        }
    }

    function lerp(a, b, t) { return a + (b - a) * t; }
    function lerpBox(cur, tgt, t) {
        if (!cur) return { ...tgt };
        return {
            x: lerp(cur.x, tgt.x, t),
            y: lerp(cur.y, tgt.y, t),
            w: lerp(cur.w, tgt.w, t),
            h: lerp(cur.h, tgt.h, t),
        };
    }

    function computeCanvasBox(raw) {
        if (!raw || !raw.imgW || !raw.imgH) return null;

        const W = canvas.width;
        const H = canvas.height;

        const imgW = raw.imgW;
        const imgH = raw.imgH;

        const scale = Math.max(W / imgW, H / imgH);
        const renderW = imgW * scale;
        const renderH = imgH * scale;

        const offX = (W - renderW) / 2;
        const offY = (H - renderH) / 2;

        let x = offX + (raw.x * scale);
        const y = offY + (raw.y * scale);
        const w = raw.w * scale;
        const h = raw.h * scale;

        // mirror (video is mirrored, canvas is not)
        x = W - (x + w);

        return { x, y, w, h };
    }

    function isTooSmallFace(raw) {
        if (!raw || !raw.imgW || !raw.imgH) return true;
        const ratio = (raw.w * raw.h) / (raw.imgW * raw.imgH);
        return ratio < CFG.gating.minFaceAreaRatio;
    }

    function faceFullyVisible() {
        if (!state.boxSmooth) return false;
        const m = Math.min(canvas.width, canvas.height) * CFG.gating.safeEdgeMarginRatio;

        const b = state.boxSmooth;
        if (b.w <= 0 || b.h <= 0) return false;
        if (b.x < m) return false;
        if (b.y < m) return false;
        if ((b.x + b.w) > (canvas.width - m)) return false;
        if ((b.y + b.h) > (canvas.height - m)) return false;
        return true;
    }

    function updateStability() {
        if (!state.boxSmooth) {
            state.lastCenters = [];
            state.stableFrames = 0;
            return;
        }

        const b = state.boxSmooth;
        const c = { x: b.x + b.w / 2, y: b.y + b.h / 2 };

        state.lastCenters.push(c);
        if (state.lastCenters.length > 6) state.lastCenters.shift();

        if (state.lastCenters.length < 2) {
            state.stableFrames = 0;
            return;
        }

        const a = state.lastCenters[state.lastCenters.length - 2];
        const move = Math.hypot(c.x - a.x, c.y - a.y);

        state.stableFrames = (move <= CFG.gating.stableMaxMovePx)
            ? Math.min(state.stableFrames + 1, CFG.gating.stableFramesRequired)
            : 0;
    }

    function drawLoop() {
        resizeCanvas();
        ctx.clearRect(0, 0, canvas.width, canvas.height);

        if (state.boxSmooth) {
            const good = (state.faceStatus === 'good');
            const color = good ? '#00ff7a' : '#ffcc00';

            ctx.save();
            ctx.lineWidth = 4;
            ctx.strokeStyle = color;
            ctx.strokeRect(state.boxSmooth.x, state.boxSmooth.y, state.boxSmooth.w, state.boxSmooth.h);
            ctx.restore();
        }

        requestAnimationFrame(drawLoop);
    }

    function resetScanState() {
        state.boxRaw = null;
        state.boxSmooth = null;
        state.lastCenters = [];
        state.stableFrames = 0;

        state.faceStatus = 'none';
        state.mpRawCount = 0;
        state.mpAcceptedCount = 0;
        state.mpReadyToFire = false;
        state.mpStableStart = 0;

        state.faceCount = 0;

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
            if (!nextGenActive) {
                state.mpMode = 'none';
                setKioskMode('legacy');
                return;
            }

            const hasTasks = (typeof window.MpFilesetResolver === 'function' && typeof window.MpFaceDetectorTask === 'function');
            if (!hasTasks) {
                nextGenActive = false;
                state.mpMode = 'none';
                setKioskMode('legacy');
                return;
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
                console.warn('[FaceAttend] tasks init failed, using legacy', e);
                this.vision = null;
                this.detector = null;
                nextGenActive = false;
                state.mpMode = 'none';
                setKioskMode('legacy');
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
                    console.warn('[FaceAttend] tasks detect failed, switching off nextgen', e);
                    nextGenActive = false;
                    state.mpMode = 'none';
                    setKioskMode('legacy');
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
            state.mpRawCount = 0;
            state.mpAcceptedCount = 0;

            state.mpStableStart = 0;
            state.mpFaceSeenAt = 0;

            state.boxRaw = null;
            state.boxSmooth = null;
            state.lastCenters = [];
            state.stableFrames = 0;
            return;
        }

        const rawScored = normList
            .filter(x => x.bbox)
            .sort((a, b) => b.score - a.score);

        state.mpRawCount = rawScored.length;
        state.mpFaceSeenAt = now;

        const bestRaw = rawScored[0];
        const accepted = rawScored.filter(x => x.score >= CFG.mp.acceptMinScore);
        state.mpAcceptedCount = accepted.length;

        // update box always (yellow guidance)
        {
            const b = bestRaw.bbox;
            const imgW = video.videoWidth;
            const imgH = video.videoHeight;

            const w = b.width * imgW;
            const h = b.height * imgH;
            const x = (b.xCenter - b.width / 2) * imgW;
            const y = (b.yCenter - b.height / 2) * imgH;

            state.boxRaw = { x: Math.round(x), y: Math.round(y), w: Math.round(w), h: Math.round(h), imgW, imgH };
            const tgt = computeCanvasBox(state.boxRaw);
            if (tgt) state.boxSmooth = lerpBox(state.boxSmooth, tgt, 0.35);
        }

        updateStability();

        // multiple faces
        if (rawScored.length > 1) {
            const second = rawScored[1];
            const area2 = (second.bbox.width || 0) * (second.bbox.height || 0);
            if (second.score >= CFG.mp.acceptMinScore && area2 >= CFG.mp.multiMinAreaRatio) {
                state.faceStatus = 'multi';
                state.mpStableStart = 0;
                safeSetPrompt('Multiple faces detected.', 'One face only.');
                return;
            }
        }

        // low confidence
        if (accepted.length === 0) {
            state.faceStatus = 'low';
            state.mpStableStart = 0;

            if (state.boxRaw && isTooSmallFace(state.boxRaw)) {
                safeSetPrompt('Move closer.', 'Face is too far.');
            } else {
                safeSetPrompt('Look at the camera.', 'Improve lighting or move closer.');
            }
            return;
        }

        // good confidence
        state.faceStatus = 'good';

        const b = accepted[0].bbox;

        if (isTooSmallFace(state.boxRaw)) {
            state.mpStableStart = 0;
            safeSetPrompt('Move closer.', 'Face is too far.');
            return;
        }

        if (!faceFullyVisible()) {
            state.mpStableStart = 0;
            safeSetPrompt('Move into frame.', 'Keep your full face in view.');
            return;
        }

        if (b.xCenter < CFG.gating.centerMin || b.xCenter > CFG.gating.centerMax ||
            b.yCenter < CFG.gating.centerMin || b.yCenter > CFG.gating.centerMax) {
            state.mpStableStart = 0;
            safeSetPrompt('Center your face.', '');
            return;
        }

        // motion gate (anti-spoof)
        if (state.frameDiffs.length >= CFG.antiSpoof.motionWindow) {
            const motionAvg = avg(state.frameDiffs);
            if (motionAvg != null && motionAvg < CFG.antiSpoof.motionDiffMin) {
                state.mpStableStart = 0;
                safeSetPrompt('Move slightly.', 'Breathe naturally.');
                return;
            }
        }

        // stability gate
        if (state.stableFrames < CFG.mp.stableFramesMin) {
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
    function updateEta(facePresent, useNextGen) {
        if (!facePresent) { setEta('ETA: idle'); return; }

        if (useNextGen) {
            if (!state.boxRaw || state.faceStatus === 'none') { setEta('ETA: waiting'); return; }
            if (state.faceStatus === 'low') { setEta('ETA: improve lighting'); return; }
            if (state.faceStatus === 'multi') { setEta('ETA: one face only'); return; }

            const need = CFG.mp.stableFramesMin;
            const st = Math.min(state.stableFrames, need);
            const msLeft = (state.mpStableStart > 0)
                ? Math.max(0, CFG.mp.stableNeededMs - (Date.now() - state.mpStableStart))
                : CFG.mp.stableNeededMs;

            setEta('ETA: hold still ' + st + '/' + need + ' (' + (msLeft / 1000).toFixed(1) + 's)');
            return;
        }

        if (!state.boxRaw || state.faceCount === 0) { setEta('ETA: waiting'); return; }
        setEta('ETA: scanning');
    }

    // =========
    // unlock ui
    // =========
    function isUnlockAvailable() {
        return !!ui.unlockBackdrop && !!ui.unlockPin && !!ui.unlockSubmit && !!ui.unlockCancel && !!ui.unlockErr;
    }

    function openUnlock() {
        if (!isUnlockAvailable()) return;
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
                openUnlock();
            }
        }, true);

        document.querySelector('#topLeft .brand')?.addEventListener('dblclick', openUnlock);
    }

    // =========
    // gps + office resolve
    // =========
    function startGpsIfMobile() {
        if (!isMobile) return;
        if (!('geolocation' in navigator)) {
            state.allowedArea = false;
            if (ui.officeLine) ui.officeLine.textContent = 'GPS not available';
            return;
        }

        navigator.geolocation.watchPosition(
            (pos) => {
                state.gps.lat = pos.coords.latitude;
                state.gps.lon = pos.coords.longitude;
                state.gps.accuracy = pos.coords.accuracy;
            },
            () => {
                state.gps.lat = null;
                state.gps.lon = null;
                state.gps.accuracy = null;
            },
            { enableHighAccuracy: true, maximumAge: 500, timeout: 6000 }
        );
    }

    async function resolveOfficeIfNeeded() {
        if (!isMobile) return;
        if (!state.localPresent) return;

        const t = Date.now();
        if (t - state.lastResolveAt < CFG.server.resolveMs) return;
        state.lastResolveAt = t;

        if (state.gps.lat == null || state.gps.lon == null || state.gps.accuracy == null) {
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
                state.currentOffice.id = j.officeId;
                state.currentOffice.name = j.officeName;
                if (ui.officeLine && state.currentOffice.name) ui.officeLine.textContent = state.currentOffice.name;
            }
        } catch { }
    }

    // =========
    // legacy detect (kept)
    // =========
    async function postFrame(endpoint, blob) {
        const fd = new FormData();
        fd.append('__RequestVerificationToken', token);
        fd.append('image', blob, 'frame.jpg');

        if (isMobile) {
            if (state.gps.lat != null) fd.append('lat', state.gps.lat);
            if (state.gps.lon != null) fd.append('lon', state.gps.lon);
            if (state.gps.accuracy != null) fd.append('accuracy', state.gps.accuracy);
        }

        const r = await fetch(endpoint, { method: 'POST', body: fd });

        if (r.status === 429) {
            const ra = parseInt(r.headers.get('Retry-After') || '0', 10);
            state.backoffUntil = Date.now() + ((isFinite(ra) && ra > 0) ? ra * 1000 : 2000);
            return { ok: false, error: 'TOO_MANY_REQUESTS', retryAfter: ra || 2 };
        }

        const ct = (r.headers.get('content-type') || '').toLowerCase();
        if (!ct.includes('application/json')) {
            return { ok: false, error: 'BAD_RESPONSE', status: r.status };
        }

        return await r.json();
    }

    async function pollDetectFace() {
        if (state.detectInFlight) return;

        const t = Date.now();
        if (t - state.lastDetectAt < CFG.server.detectMs) return;
        state.lastDetectAt = t;

        state.detectInFlight = true;
        try {
            const blob = await captureFrameBlob();
            if (!blob) return;

            const j = await postFrame(EP.detectFace, blob);
            if (!j || j.ok !== true) return;

            state.faceCount = (typeof j.faceCount === 'number') ? j.faceCount : (j.count || 0);
            state.boxRaw = j.faceBox || null;

            if (!state.boxRaw || state.faceCount === 0) {
                state.boxSmooth = null;
                state.stableFrames = 0;
                setPrompt('Ready.', 'Look at the camera.');
                return;
            }

            state.serverFaceSeenAt = Date.now();

            const tgt = computeCanvasBox(state.boxRaw);
            if (tgt) state.boxSmooth = lerpBox(state.boxSmooth, tgt, 0.35);

            updateStability();

            if (state.faceCount > 1) { setPrompt('Multiple faces detected.', 'One face only.'); return; }
            if (!faceFullyVisible()) { setPrompt('Move into frame.', 'Keep your full face in view.'); return; }
            if (state.stableFrames < CFG.gating.stableFramesRequired) { setPrompt('Hold still.', 'Do not move.'); return; }

            setPrompt('Checking...', 'Hold still.');
        } finally {
            state.detectInFlight = false;
        }
    }

    // =========
    // attendance submit (updates liveness line)
    // =========
    async function submitAttendance(blob, endpoint) {
        state.lastCaptureAt = Date.now();
        setPrompt('Processing...', 'Please wait.');

        const fd = new FormData();
        fd.append('__RequestVerificationToken', token);
        fd.append('image', blob, 'capture.jpg');

        if (isMobile) {
            if (state.gps.lat != null) fd.append('lat', state.gps.lat);
            if (state.gps.lon != null) fd.append('lon', state.gps.lon);
            if (state.gps.accuracy != null) fd.append('accuracy', state.gps.accuracy);
        }

        try {
            const url = endpoint || EP.scanAttendance;
            const r = await fetch(url, { method: 'POST', body: fd, credentials: 'same-origin' });
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

            if (j && j.ok) {
                const displayName = j.displayName || j.name;
                setPrompt(displayName ? ('Welcome, ' + displayName + '.') : 'Success.', j.message || 'Recorded.');
                setTimeout(() => setPrompt('Ready.', 'Look at the camera.'), 1600);
            } else {
                const err = j?.error || 'Failed.';
                if (err === 'LIVENESS_FAIL') {
                    setPrompt('Liveness failed.', 'Move naturally and try again.');
                } else {
                    setPrompt(err, 'Try again.');
                }
                setTimeout(() => setPrompt('Ready.', 'Look at the camera.'), 1600);
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
            const useNextGen = nextGenActive && (state.mpMode === 'tasks');

            if (useNextGen) mp.tick();

            const facePresent = useNextGen
                ? ((state.mpFaceSeenAt > 0 && (now - state.mpFaceSeenAt) < CFG.idle.faceLostMs) || state.localPresent)
                : state.localPresent;

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
            if (!state.allowedArea) { updateEta(true, useNextGen); return; }

            if (now < state.backoffUntil) {
                setPrompt('System busy.', 'Please wait.');
                updateEta(true, useNextGen);
                return;
            }

            if (useNextGen) {
                if (state.mpReadyToFire && (now - state.lastCaptureAt) > CFG.server.captureCooldownMs) {
                    const blob = await captureFrameBlob(0.85);
                    if (blob) {
                        state.mpReadyToFire = false;
                        state.mpStableStart = 0;

                        state.liveInFlight = true;
                        try {
                            await submitAttendance(blob, EP.attend);
                        } finally {
                            state.liveInFlight = false;
                        }
                    }
                }
            } else {
                await pollDetectFace();
            }

            updateEta(true, useNextGen);
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
        startGpsIfMobile();
        resolveOfficeDesktopOnce();
        wireUnlockUi();

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
        } catch {
            setIdleUi(false);
            setPrompt('Camera blocked.', 'Allow camera permission.');
            setEta('ETA: --');
        }
    })();

})();