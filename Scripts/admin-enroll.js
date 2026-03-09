(function () {
    // Face enroll wizard — live scan + upload
    // QA-OPT: faster timings, AbortController, smaller JPEG payload

    var root = document.getElementById("enrollRoot");
    if (!root) return;

    // ------------------------
    // config from data- attrs
    // ------------------------
    var empId   = (root.getAttribute("data-employee-id") || "").trim();
    var perFrame = parseFloat(root.getAttribute("data-per-frame") || "0.75");
    if (!isFinite(perFrame) || perFrame <= 0) perFrame = 0.75;

    var scanUrl     = root.getAttribute("data-scan-url")     || "/Biometrics/ScanFrame";
    var enrollUrl   = root.getAttribute("data-enroll-url")   || "/Biometrics/Enroll";
    var redirectUrl = root.getAttribute("data-redirect-url") || "";

    // ------------------------
    // dom
    // ------------------------
    function el(id) { return document.getElementById(id); }

    var wizard   = el("enrollWizard");
    var livePane = el("livePane");
    var uploadPane = el("uploadPane");

    var cam       = el("cam");
    var cap       = el("cap");
    var camStatus = el("camStatus");
    var upStatus  = el("upStatus");

    var file = el("file");

    var overlay = el("enrollOverlay");
    var overlayBox   = null;
    var overlayColor = "#adb5bd";
    var overlayDrawing = false;

    // ------------------------
    // state
    // ------------------------
    var stream   = null;
    var busy     = false;
    var enrolled = false;

    // QA-OPT-E1: 220 ms auto-tick (was 320) — 31% more scan attempts per second
    var AUTO_INTERVAL_MS = 220;
    var PASS_WINDOW   = 5;
    var PASS_REQUIRED = 2;

    // QA-OPT-E2: collect frames faster
    var ENROLL_MAX_IMAGES       = 5;
    var ENROLL_COLLECT_TRIES    = 12;
    // QA-OPT-E3: 100 ms delay between collect tries (was 140)
    var ENROLL_COLLECT_DELAY_MS = 100;

    var autoTimer = null;
    var passHist  = [];

    // QA-OPT-E4: AbortController for cancelling stale scan requests
    var _scanAbort = null;

    function token() {
        var t = document.querySelector('input[name="__RequestVerificationToken"]');
        return t ? t.value : '';
    }

    function sleep(ms) {
        return new Promise(function (r) { setTimeout(r, ms); });
    }

    // ------------------------
    // status helpers
    // ------------------------
    function setStatus(el, text, kind) {
        if (!el) return;
        el.innerHTML = '<div class="alert alert-' + (kind || 'info') + ' py-2 mb-0">' + text + '</div>';
    }

    function setStatusHtml(el, html, kind) {
        if (!el) return;
        el.innerHTML = '<div class="alert alert-' + (kind || 'info') + ' py-2 mb-0">' + html + '</div>';
    }

    // ------------------------
    // wizard navigation
    // ------------------------
    function showWizard() {
        if (wizard)     wizard.style.display = '';
        if (livePane)   livePane.style.display = 'none';
        if (uploadPane) uploadPane.style.display = 'none';
    }

    function showLive() {
        if (wizard)     wizard.style.display = 'none';
        if (livePane)   livePane.style.display = '';
        if (uploadPane) uploadPane.style.display = 'none';
    }

    function showUpload() {
        if (wizard)     wizard.style.display = 'none';
        if (livePane)   livePane.style.display = 'none';
        if (uploadPane) uploadPane.style.display = '';
    }

    // ------------------------
    // camera
    // ------------------------
    async function startCam() {
        if (stream) return;
        stream = await navigator.mediaDevices.getUserMedia({
            video: {
                facingMode: 'user',
                width:     { ideal: 1280 },
                height:    { ideal:  720 },
                frameRate: { ideal:  15, max: 30 },
            },
            audio: false,
        });
        if (cam) {
            cam.srcObject = stream;
            await cam.play();
        }
        startOverlayDraw();
    }

    function stopCam() {
        stopAuto();
        if (_scanAbort) { try { _scanAbort.abort(); } catch { } _scanAbort = null; }
        if (stream) {
            try { stream.getTracks().forEach(function (t) { t.stop(); }); } catch { }
        }
        stream   = null;
        enrolled = false;
        passHist = [];
        if (cam) cam.srcObject = null;
    }

    // ------------------------
    // capture + server calls
    // ------------------------
    async function captureJpegBlob(quality) {
        var w = (cam && cam.videoWidth)  || 1280;
        var h = (cam && cam.videoHeight) || 720;
        if (cap) { cap.width = w; cap.height = h; }

        var ctx = cap ? cap.getContext("2d") : null;
        if (ctx && cam) ctx.drawImage(cam, 0, 0, w, h);

        return await new Promise(function (resolve) {
            if (!cap) { resolve(null); return; }
            cap.toBlob(function (b) { resolve(b); }, "image/jpeg", quality);
        });
    }

    async function postScanFrame(blob) {
        // Cancel previous stale request
        if (_scanAbort) { try { _scanAbort.abort(); } catch { } }
        _scanAbort = new AbortController();

        var fd = new FormData();
        fd.append("__RequestVerificationToken", token());
        fd.append("image", blob, "frame.jpg");

        var res = await fetch(scanUrl, { method: "POST", body: fd, signal: _scanAbort.signal });
        return await res.json();
    }

    async function postEnrollMany(blobs) {
        var fd = new FormData();
        fd.append("__RequestVerificationToken", token());
        fd.append("employeeId", empId);

        if (!blobs || !blobs.length) return { ok: false, error: "NO_IMAGE" };

        for (var i = 0; i < blobs.length; i++) {
            fd.append("image", blobs[i], "enroll_" + (i + 1) + ".jpg");
        }

        var res = await fetch(enrollUrl, { method: "POST", body: fd });
        return await res.json();
    }

    async function collectGoodEnrollFrames() {
        var frames = [];

        for (var i = 0; i < ENROLL_COLLECT_TRIES; i++) {
            // QA-OPT-E5: quality 0.78 (was 0.80) — smaller upload during collection
            var blob = await captureJpegBlob(0.78);
            var r;
            try { r = await postScanFrame(blob); }
            catch (e) {
                if (e && e.name === 'AbortError') break;
                await sleep(ENROLL_COLLECT_DELAY_MS); continue;
            }

            if (!r || r.ok !== true) { await sleep(ENROLL_COLLECT_DELAY_MS); continue; }
            if (r.count !== 1)       { await sleep(ENROLL_COLLECT_DELAY_MS); continue; }

            var p    = (typeof r.liveness === "number") ? r.liveness : 0;
            var pass = (r.livenessOk === true) && (p >= perFrame);

            if (pass) {
                frames.push({ blob: blob, p: p });
                frames.sort(function (a, b) { return b.p - a.p; });
                if (frames.length > ENROLL_MAX_IMAGES) frames.length = ENROLL_MAX_IMAGES;
                if (frames.length >= ENROLL_MAX_IMAGES && frames[0].p >= 0.90) break;
            }

            await sleep(ENROLL_COLLECT_DELAY_MS);
        }

        return frames.map(function (x) { return x.blob; });
    }

    // -------------------------
    // bounding box overlay draw
    // -------------------------
    function startOverlayDraw() {
        if (overlayDrawing) return;
        overlayDrawing = true;

        function drawFrame() {
            if (!overlay) return;
            var ctx = overlay.getContext("2d");

            var rect = overlay.getBoundingClientRect();
            if (overlay.width  !== rect.width)  overlay.width  = rect.width;
            if (overlay.height !== rect.height) overlay.height = rect.height;

            ctx.clearRect(0, 0, overlay.width, overlay.height);

            var b = overlayBox;
            if (b && b.imgW && b.imgH) {
                var scaleX = overlay.width  / b.imgW;
                var scaleY = overlay.height / b.imgH;
                var bx = overlay.width - (b.x + b.w) * scaleX;  // mirrored
                var by = b.y * scaleY;
                var bw = b.w * scaleX;
                var bh = b.h * scaleY;
                var pad = 8;
                bx -= pad; by -= pad; bw += pad * 2; bh += pad * 2;

                // Draw corner brackets instead of full rectangle — matches kiosk style
                var L = Math.min(bw, bh) * 0.18;
                ctx.save();
                ctx.strokeStyle = overlayColor;
                ctx.lineWidth   = 2.5;
                ctx.shadowColor = overlayColor;
                ctx.shadowBlur  = 8;
                ctx.lineCap     = 'round';

                ctx.beginPath(); ctx.moveTo(bx, by + L);        ctx.lineTo(bx, by);        ctx.lineTo(bx + L, by);        ctx.stroke();
                ctx.beginPath(); ctx.moveTo(bx + bw - L, by);   ctx.lineTo(bx + bw, by);   ctx.lineTo(bx + bw, by + L);   ctx.stroke();
                ctx.beginPath(); ctx.moveTo(bx, by + bh - L);   ctx.lineTo(bx, by + bh);   ctx.lineTo(bx + L, by + bh);   ctx.stroke();
                ctx.beginPath(); ctx.moveTo(bx + bw - L, by + bh); ctx.lineTo(bx + bw, by + bh); ctx.lineTo(bx + bw, by + bh - L); ctx.stroke();

                ctx.restore();
            }

            requestAnimationFrame(drawFrame);
        }

        requestAnimationFrame(drawFrame);
    }

    // ------------------------
    // auto enroll logic (live)
    // ------------------------
    function startAuto() {
        stopAuto();
        enrolled = false;
        passHist = [];
        autoTimer = setInterval(autoTick, AUTO_INTERVAL_MS);
    }

    function stopAuto() {
        if (autoTimer) clearInterval(autoTimer);
        autoTimer = null;
    }

    async function autoTick() {
        if (enrolled || !stream || busy) return;
        busy = true;

        try {
            // QA-OPT-E6: quality 0.75 for auto-tick (unchanged from original, already optimal)
            var blob = await captureJpegBlob(0.75);
            var r;
            try { r = await postScanFrame(blob); }
            catch (e) {
                if (e && e.name === 'AbortError') return;
                overlayColor = "#adb5bd";
                overlayBox   = null;
                return;
            }

            overlayBox = (r && r.faceBox) ? r.faceBox : null;

            if (!r || r.ok !== true) {
                overlayColor = "#dc3545";
                setStatus(camStatus, (r && r.error) ? r.error : "scan error", "danger");
                passHist = [];
                return;
            }

            if (r.count === 0) {
                overlayBox   = null;
                overlayColor = "#adb5bd";
                setStatus(camStatus, "No face detected.", "warning");
                passHist = [];
                return;
            }

            if (r.count > 1) {
                overlayColor = "#fd7e14";
                setStatus(camStatus, "One person only.", "warning");
                passHist = [];
                return;
            }

            var p    = (typeof r.liveness === "number") ? r.liveness : 0;
            var pass = (r.livenessOk === true) && (p >= perFrame);

            overlayColor = pass ? "#20c997" : "#ffc107";

            passHist.push(pass ? 1 : 0);
            if (passHist.length > PASS_WINDOW) passHist.shift();

            var sum  = 0;
            for (var i = 0; i < passHist.length; i++) sum += passHist[i];
            var need = Math.max(0, PASS_REQUIRED - sum);

            if (pass) {
                setStatusHtml(camStatus,
                    "Face OK &mdash; liveness: <b>" + p.toFixed(2) + "</b>" +
                    (need > 0 ? ", hold still (" + need + " more)." : ", collecting…"), "success");
            } else {
                setStatusHtml(camStatus,
                    "Face OK &mdash; liveness: <b>" + p.toFixed(2) + "</b>, improve lighting.", "warning");
            }

            if (sum < PASS_REQUIRED) return;

            setStatus(camStatus, "Collecting frames…", "info");
            var frames = await collectGoodEnrollFrames();
            if (!frames || !frames.length) {
                overlayColor = "#dc3545";
                setStatus(camStatus, "Could not collect enough frames — try again.", "danger");
                passHist = [];
                return;
            }

            setStatus(camStatus, "Saving enrollment (" + frames.length + " frame(s))…", "info");
            var saved = await postEnrollMany(frames);

            if (saved && saved.ok === true) {
                enrolled     = true;
                overlayColor = "#0d6efd";
                setStatus(camStatus, "Enrollment saved. Redirecting…", "success");
                stopAuto();
                if (redirectUrl) {
                    setTimeout(function () { window.location.href = redirectUrl; }, 2000);
                }
                return;
            }

            overlayColor = "#dc3545";
            setStatus(camStatus, (saved && saved.error) ? saved.error : "Enrollment failed. Try again.", "danger");
            passHist = [];

        } finally {
            busy = false;
        }
    }

    // ------------------------
    // upload path
    // ------------------------
    async function handleUpload(f) {
        if (!f) return;

        setStatus(upStatus, "Processing…", "info");

        var fd = new FormData();
        fd.append("__RequestVerificationToken", token());
        fd.append("employeeId", empId);
        fd.append("image", f, f.name);

        try {
            var res  = await fetch(enrollUrl, { method: "POST", body: fd });
            var data = await res.json();

            if (data && data.ok === true) {
                setStatus(upStatus, "Enrollment saved!", "success");
                if (redirectUrl) setTimeout(function () { window.location.href = redirectUrl; }, 2000);
            } else {
                setStatus(upStatus, (data && data.error) ? data.error : "Upload failed. Try again.", "danger");
            }
        } catch {
            setStatus(upStatus, "Upload error. Check connection.", "danger");
        }
    }

    // ------------------------
    // wire events
    // ------------------------
    function wireWizard() {
        var choices = document.querySelectorAll('[data-enroll-mode]');
        choices.forEach(function (c) {
            c.addEventListener('click', async function () {
                var mode = c.getAttribute('data-enroll-mode');
                if (mode === 'live') {
                    showLive();
                    try {
                        await startCam();
                        setStatus(camStatus, "Camera ready. Look directly at the lens.", "info");
                        startAuto();
                    } catch (e) {
                        setStatus(camStatus, "Camera blocked. Please allow camera permission.", "danger");
                    }
                } else {
                    showUpload();
                }
            });

            c.addEventListener('keydown', function (e) {
                if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); c.click(); }
            });
        });

        var backBtns = document.querySelectorAll('[data-enroll-back]');
        backBtns.forEach(function (b) {
            b.addEventListener('click', function () {
                stopCam();
                showWizard();
            });
        });

        if (file) {
            file.addEventListener('change', function () {
                if (file.files && file.files[0]) handleUpload(file.files[0]);
            });
        }
    }

    wireWizard();

})();
