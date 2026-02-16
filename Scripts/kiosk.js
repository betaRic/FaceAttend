(function () {
    const video = document.getElementById('kioskVideo');
    const canvas = document.getElementById('overlayCanvas');
    const ctx = canvas.getContext('2d');

    const officeLine = document.getElementById('officeLine');
    const timeLine = document.getElementById('timeLine');
    const dateLine = document.getElementById('dateLine');
    const livenessLine = document.getElementById('livenessLine');

    const centerBlock = document.getElementById('centerBlock');
    const mainPrompt = document.getElementById('mainPrompt');
    const subPrompt = document.getElementById('subPrompt');
    const bottomCenter = document.getElementById('bottomCenter');

    const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value || '';

    // Behavior tuning
    const SCANFRAME_MS = 350;                 // server ScanFrame polling
    const RESOLVE_MS = 1200;                  // office resolve polling (mobile only)
    const CAPTURE_COOLDOWN_MS = 3000;         // cooldown after attendance result
    const LIVENESS_STREAK_REQUIRED = 2;       // anti spoof: consecutive liveness pass
    const STABLE_FRAMES_REQUIRED = 4;         // anti spoof: stable frames
    const STABLE_MAX_MOVE_PX = 10;            // anti spoof: max center move
    const MIN_FACE_AREA_RATIO = 0.03;         // ignore tiny faces
    const GUIDE_RADIUS_RATIO = 0.18;          // guide circle size
    const CENTER_TOLERANCE_RATIO = 0.55;      // how close face center must be to guide center

    const ua = navigator.userAgent || '';
    const isMobile = /Android|iPhone|iPad|iPod|Mobile|Tablet/i.test(ua);

    let stream = null;
    let lastScanAt = 0;
    let lastResolveAt = 0;
    let lastCaptureAt = 0;

    let gps = { lat: null, lon: null, accuracy: null };
    let allowedArea = !isMobile; // desktop allowed by default
    let currentOffice = { id: null, name: null };

    // Face box from server (image coords)
    let boxRaw = null;     // { x, y, w, h, imgW, imgH }
    let boxSmooth = null;  // eased box in canvas coords

    // Anti spoof trackers
    let lastCenters = [];
    let stableFrames = 0;
    let livenessStreak = 0;

    // UI state
    let latestLiveness = null;
    let latestFaceCount = 0;
    let pulseUntil = 0;

    function setPrompt(a, b) {
        mainPrompt.textContent = a || '';
        subPrompt.textContent = b || '';
    }

    function showCenterBlock(show, title, sub) {
        if (!isMobile) {
            centerBlock.classList.add('hidden');
            return;
        }
        if (show) {
            centerBlock.classList.remove('hidden');
            centerBlock.querySelector('.blockTitle').textContent = title || 'Not in allowed area.';
            centerBlock.querySelector('.blockSub').textContent = sub || 'Move closer to a designated office.';
        } else {
            centerBlock.classList.add('hidden');
        }
    }

    function ceil2(num) {
        if (typeof num !== 'number' || !isFinite(num)) return '--';
        return (Math.ceil(num * 100) / 100).toFixed(2);
    }

    function nowText() {
        const d = new Date();
        timeLine.textContent = d.toLocaleTimeString('en-PH', { hour12: false });
        dateLine.textContent = d.toLocaleDateString('en-PH');
    }

    function resizeCanvas() {
        const w = canvas.clientWidth;
        const h = canvas.clientHeight;
        if (canvas.width !== w || canvas.height !== h) {
            canvas.width = w;
            canvas.height = h;
        }
    }

    function lerp(a, b, t) {
        return a + (b - a) * t;
    }

    function lerpBox(cur, tgt, t) {
        if (!cur) return { ...tgt };
        return {
            x: lerp(cur.x, tgt.x, t),
            y: lerp(cur.y, tgt.y, t),
            w: lerp(cur.w, tgt.w, t),
            h: lerp(cur.h, tgt.h, t)
        };
    }

    function drawLoop() {
        resizeCanvas();
        ctx.clearRect(0, 0, canvas.width, canvas.height);

        // Guide circle
        const cx = canvas.width / 2;
        const cy = canvas.height / 2;
        const guideR = Math.min(canvas.width, canvas.height) * GUIDE_RADIUS_RATIO;

        ctx.save();
        ctx.globalAlpha = 0.35;
        ctx.lineWidth = 3;
        ctx.strokeStyle = '#ffffff';
        ctx.beginPath();
        ctx.arc(cx, cy, guideR, 0, Math.PI * 2);
        ctx.stroke();
        ctx.restore();

        // Bounding box
        if (boxSmooth) {
            const good = (latestFaceCount === 1);
            const color = good ? '#00ff7a' : '#ffcc00';

            ctx.save();
            ctx.lineWidth = 4;
            ctx.strokeStyle = color;

            ctx.strokeRect(boxSmooth.x, boxSmooth.y, boxSmooth.w, boxSmooth.h);

            // Pulse overlay on success
            const t = Date.now();
            if (t < pulseUntil) {
                const p = 1 - ((pulseUntil - t) / 600);
                const alpha = 0.55 * (1 - p);
                ctx.globalAlpha = alpha;
                ctx.lineWidth = 10;
                ctx.strokeStyle = '#00ff7a';
                ctx.strokeRect(boxSmooth.x - 6, boxSmooth.y - 6, boxSmooth.w + 12, boxSmooth.h + 12);
            }

            ctx.restore();
        }

        requestAnimationFrame(drawLoop);
    }

    async function startCamera() {
        stream = await navigator.mediaDevices.getUserMedia({
            video: { facingMode: 'user' },
            audio: false
        });
        video.srcObject = stream;
        await video.play();
    }

    function startClock() {
        nowText();
        setInterval(nowText, 1000);
    }

    function startGpsIfMobile() {
        if (!isMobile) return;

        if (!('geolocation' in navigator)) {
            allowedArea = false;
            officeLine.textContent = 'GPS not available';
            showCenterBlock(true, 'GPS not available.', 'Use a phone or tablet with GPS.');
            return;
        }

        navigator.geolocation.watchPosition(
            (pos) => {
                gps.lat = pos.coords.latitude;
                gps.lon = pos.coords.longitude;
                gps.accuracy = pos.coords.accuracy;
            },
            () => {
                gps.lat = null;
                gps.lon = null;
                gps.accuracy = null;
            },
            { enableHighAccuracy: true, maximumAge: 500, timeout: 6000 }
        );
    }

    async function resolveOfficeIfNeeded() {
        if (!isMobile) return;

        const t = Date.now();
        if (t - lastResolveAt < RESOLVE_MS) return;
        lastResolveAt = t;

        if (gps.lat == null || gps.lon == null || gps.accuracy == null) {
            allowedArea = false;
            officeLine.textContent = 'Locating...';
            showCenterBlock(true, 'Locating...', 'Turn on GPS and allow location.');
            return;
        }

        const fd = new FormData();
        fd.append('__RequestVerificationToken', token);
        fd.append('lat', gps.lat);
        fd.append('lon', gps.lon);
        fd.append('accuracy', gps.accuracy);

        const r = await fetch('/Kiosk/ResolveOffice', { method: 'POST', body: fd });
        const j = await r.json();

        if (!j || j.ok !== true) {
            boxSmooth = null;
            latestFaceCount = 0;
            latestLiveness = null;
            setPrompt('Scan error.', (j && j.error) ? String(j.error) : 'Try again.');
            return;
        }

        // Server can still deny scans (ex: GPS gate).
        if (j.allowed === false) {
            allowedArea = false;
            officeLine.textContent = 'Not in allowed area';
            showCenterBlock(true, 'Not in allowed area.', 'Move closer to a designated office.');
            setPrompt('Not in allowed area.', 'Move closer to a designated office.');
            return;
        }

        allowedArea = !!j.allowed;
        if (allowedArea) {
            // Supports either { office: { id, name } } or flat officeId/officeName.
            const off = j.office || null;
            currentOffice.id = (off && typeof off.id !== 'undefined') ? off.id : j.officeId;
            currentOffice.name = (off && off.name) ? off.name : j.officeName;
            officeLine.textContent = currentOffice.name || 'Office OK';
            showCenterBlock(false);
        } else {
            currentOffice.id = null;
            currentOffice.name = null;
            officeLine.textContent = 'Not in allowed area';
            showCenterBlock(true, 'Not in allowed area.', 'Move closer to a designated office.');
        }
    }

    function captureFrameBlob() {
        // Draw current video to an offscreen canvas (video is mirrored via CSS, but raw pixels are not mirrored)
        const off = document.createElement('canvas');
        off.width = video.videoWidth || 640;
        off.height = video.videoHeight || 480;
        const octx = off.getContext('2d');

        // Mirror into pixel output to match what user sees
        octx.translate(off.width, 0);
        octx.scale(-1, 1);
        octx.drawImage(video, 0, 0, off.width, off.height);

        return new Promise((resolve) => {
            off.toBlob((b) => resolve(b), 'image/jpeg', 0.92);
        });
    }

    function computeCanvasBox(raw) {
        if (!raw || !raw.imgW || !raw.imgH) return null;
        const sx = canvas.width / raw.imgW;
        const sy = canvas.height / raw.imgH;
        return {
            x: raw.x * sx,
            y: raw.y * sy,
            w: raw.w * sx,
            h: raw.h * sy
        };
    }

    function isTooSmallFace(raw) {
        if (!raw || !raw.imgW || !raw.imgH) return true;
        const area = raw.w * raw.h;
        const full = raw.imgW * raw.imgH;
        const ratio = area / full;
        return ratio < MIN_FACE_AREA_RATIO;
    }

    function centerOk(raw) {
        if (!raw) return false;
        const bx = boxSmooth ? (boxSmooth.x + boxSmooth.w / 2) : null;
        const by = boxSmooth ? (boxSmooth.y + boxSmooth.h / 2) : null;
        if (bx == null || by == null) return false;

        const cx = canvas.width / 2;
        const cy = canvas.height / 2;
        const guideR = Math.min(canvas.width, canvas.height) * GUIDE_RADIUS_RATIO;

        const dx = bx - cx;
        const dy = by - cy;
        const dist = Math.sqrt(dx * dx + dy * dy);
        return dist <= guideR * CENTER_TOLERANCE_RATIO;
    }

    function updateStability() {
        if (!boxSmooth) {
            lastCenters = [];
            stableFrames = 0;
            return;
        }
        const c = { x: boxSmooth.x + boxSmooth.w / 2, y: boxSmooth.y + boxSmooth.h / 2 };
        lastCenters.push(c);
        if (lastCenters.length > 6) lastCenters.shift();

        if (lastCenters.length < 2) {
            stableFrames = 0;
            return;
        }

        const a = lastCenters[lastCenters.length - 2];
        const dx = c.x - a.x;
        const dy = c.y - a.y;
        const move = Math.sqrt(dx * dx + dy * dy);

        if (move <= STABLE_MAX_MOVE_PX) stableFrames++;
        else stableFrames = 0;
    }

    async function pollScanFrame() {
        const t = Date.now();
        if (t - lastScanAt < SCANFRAME_MS) return;
        lastScanAt = t;

        if (!allowedArea) {
            setPrompt('Not in allowed area.', 'Move closer to a designated office.');
            return;
        }

        if (!video.videoWidth || !video.videoHeight) return;

        const blob = await captureFrameBlob();
        if (!blob) return;

        const fd = new FormData();
        fd.append('__RequestVerificationToken', token);
        fd.append('image', blob, 'frame.jpg');

        // If the server requires GPS (mobile/tablet), send it.
        if (isMobile) {
            if (gps.lat != null) fd.append('lat', gps.lat);
            if (gps.lon != null) fd.append('lon', gps.lon);
            if (gps.accuracy != null) fd.append('accuracy', gps.accuracy);
        }

        // Kiosk scan endpoint (NOT the admin Biometrics controller).
        const r = await fetch('/Kiosk/ScanFrame', { method: 'POST', body: fd });

        // If we got redirected (ex: to /Kiosk?unlock=1), JSON parsing will fail.
        const ct = (r.headers.get('content-type') || '').toLowerCase();
        if (!ct.includes('application/json')) {
            throw new Error('Bad response (' + r.status + ')');
        }

        const j = await r.json();

        // Server may return either the new or legacy field names.
        latestFaceCount = (typeof j.faceCount === 'number') ? j.faceCount : (j.count || 0);
        latestLiveness = (typeof j.livenessScore === 'number') ? j.livenessScore : ((typeof j.liveness === 'number') ? j.liveness : null);

        // Always visible liveness label
        livenessLine.textContent = 'Live: ' + (latestLiveness == null ? '--' : ceil2(latestLiveness));

        // Face box
        boxRaw = j.faceBox || null;

        if (!boxRaw || latestFaceCount === 0) {
            boxSmooth = null;
            livenessStreak = 0;
            stableFrames = 0;
            setPrompt('Ready.', 'Stand still. One face only.');
            return;
        }

        // Convert to canvas coords and ease
        const tgt = computeCanvasBox(boxRaw);
        if (tgt) {
            boxSmooth = lerpBox(boxSmooth, tgt, 0.35);
        }

        // Ignore small faces
        if (isTooSmallFace(boxRaw)) {
            livenessStreak = 0;
            stableFrames = 0;
            setPrompt('Move closer.', 'Face is too far.');
            return;
        }

        if (latestFaceCount > 1) {
            livenessStreak = 0;
            stableFrames = 0;
            setPrompt('Multiple faces detected.', 'One face only.');
            return;
        }

        // Update stability
        updateStability();

        // Prompts
        const centered = centerOk(boxRaw);
        if (!centered) {
            livenessStreak = 0;
            setPrompt('Center your face.', 'Align your face inside the circle.');
            return;
        }

        if (stableFrames < STABLE_FRAMES_REQUIRED) {
            livenessStreak = 0;
            setPrompt('Hold still.', 'Do not move.');
            return;
        }

        // Anti spoof delay: require consecutive liveness passes
        const livePass = (j.livenessPass === true) || (j.livenessOk === true);
        if (livePass) livenessStreak++;
        else livenessStreak = 0;

        if (livenessStreak < LIVENESS_STREAK_REQUIRED) {
            setPrompt('Checking...', 'Hold still.');
            return;
        }

        // Auto capture
        if (Date.now() - lastCaptureAt < CAPTURE_COOLDOWN_MS) return;
        await submitAttendance(blob);
    }

    async function submitAttendance(blob) {
        lastCaptureAt = Date.now();
        setPrompt('Processing...', 'Please wait.');

        const fd = new FormData();
        fd.append('__RequestVerificationToken', token);
        fd.append('image', blob, 'capture.jpg');

        if (isMobile) {
            if (gps.lat != null) fd.append('lat', gps.lat);
            if (gps.lon != null) fd.append('lon', gps.lon);
            if (gps.accuracy != null) fd.append('accuracy', gps.accuracy);
        }

        const r = await fetch('/Kiosk/ScanAttendance', { method: 'POST', body: fd });
        const j = await r.json();

        if (j.ok) {
            pulseUntil = Date.now() + 600;
            bottomCenter.classList.add('pulseSuccess');
            setTimeout(() => bottomCenter.classList.remove('pulseSuccess'), 900);

            const officeName = j.officeName || (j.office && j.office.name) || officeLine.textContent;
            officeLine.textContent = officeName;

            // Main success prompt
            const displayName = j.displayName || j.name;
            const who = displayName ? ('Welcome, ' + displayName + '.') : 'Success.';
            setPrompt(who, j.message || 'Recorded.');

            // cooldown
            setTimeout(() => {
                livenessStreak = 0;
                stableFrames = 0;
                setPrompt('Ready.', 'Stand still. One face only.');
            }, 1600);
        } else {
            livenessStreak = 0;
            stableFrames = 0;
            setPrompt(j.error || 'Failed.', 'Try again.');
            setTimeout(() => setPrompt('Ready.', 'Stand still. One face only.'), 1600);
        }
    }

    async function loop() {
        try {
            await resolveOfficeIfNeeded();
            await pollScanFrame();
        } catch (e) {
            // keep kiosk alive, but surface the issue
            setPrompt('System error.', 'Reload the page or check the server.');
        } finally {
            setTimeout(loop, 100);
        }
    }

    (async function init() {
        startClock();
        startGpsIfMobile();

        try {
            await startCamera();
            setPrompt('Ready.', 'Stand still. One face only.');
            drawLoop();
            loop();
        } catch (e) {
            setPrompt('Camera blocked.', 'Allow camera permission.');
        }
    })();
})();
