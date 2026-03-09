(function () {
    'use strict';

    // face enroll wizard -- live scan + upload
    // OPT: faster timings, AbortController, corner-bracket overlay

    var root = document.getElementById('enrollRoot');
    if (!root) return;

    // ---- config from data- attributes ----
    var empId      = (root.getAttribute('data-employee-id') || '').trim();
    var perFrame   = parseFloat(root.getAttribute('data-per-frame') || '0.75');
    if (!isFinite(perFrame) || perFrame <= 0) perFrame = 0.75;

    var scanUrl     = root.getAttribute('data-scan-url')     || '/Biometrics/ScanFrame';
    var enrollUrl   = root.getAttribute('data-enroll-url')   || '/Biometrics/Enroll';
    var redirectUrl = root.getAttribute('data-redirect-url') || '';

    // ---- dom ----
    function el(id) { return document.getElementById(id); }

    var wizard     = el('enrollWizard');
    var livePane   = el('livePane');
    var uploadPane = el('uploadPane');
    var cam        = el('cam');
    var cap        = el('cap');
    var camStatus  = el('camStatus');
    var upStatus   = el('upStatus');
    var file       = el('file');
    var overlay    = el('enrollOverlay');

    // ---- state ----
    var stream         = null;
    var busy           = false;
    var enrolled       = false;

    // OPT-E1: 220ms auto-tick (was 320) -- 31% more scan attempts per second
    var AUTO_INTERVAL_MS = 220;
    var PASS_WINDOW      = 5;
    var PASS_REQUIRED    = 2;

    var ENROLL_MAX_IMAGES       = 5;
    var ENROLL_COLLECT_TRIES    = 12;
    // OPT-E2: 100ms delay between collect tries (was 140)
    var ENROLL_COLLECT_DELAY_MS = 100;

    var autoTimer  = null;
    var passHist   = [];

    // OPT-E3: AbortController for cancelling stale scan requests
    var scanAbort  = null;

    var overlayBox     = null;
    var overlayColor   = '#94a3b8';  // slate-400 default
    var overlayDrawing = false;

    function token() {
        var t = document.querySelector('input[name="__RequestVerificationToken"]');
        return t ? t.value : '';
    }

    function sleep(ms) {
        return new Promise(function (r) { setTimeout(r, ms); });
    }

    // ---- status helpers ----
    function setStatus(statusEl, text, kind) {
        if (!statusEl) return;
        var cls = kind || 'info';
        statusEl.innerHTML = '<div class="alert alert-' + cls + ' py-2 mb-0">' + text + '</div>';
    }

    function setStatusHtml(statusEl, html, kind) {
        if (!statusEl) return;
        var cls = kind || 'info';
        statusEl.innerHTML = '<div class="alert alert-' + cls + ' py-2 mb-0">' + html + '</div>';
    }

    // ---- wizard navigation ----
    function showWizard() {
        if (wizard)     wizard.style.display     = '';
        if (livePane)   livePane.style.display   = 'none';
        if (uploadPane) uploadPane.style.display = 'none';
    }

    function showLive() {
        if (wizard)     wizard.style.display     = 'none';
        if (livePane)   livePane.style.display   = '';
        if (uploadPane) uploadPane.style.display = 'none';
    }

    function showUpload() {
        if (wizard)     wizard.style.display     = 'none';
        if (livePane)   livePane.style.display   = 'none';
        if (uploadPane) uploadPane.style.display = '';
    }

    // ---- camera ----
    function startCam() {
        if (stream) return Promise.resolve();
        return navigator.mediaDevices.getUserMedia({
            video: {
                facingMode: 'user',
                width:      { ideal: 1280 },
                height:     { ideal: 720  },
                frameRate:  { ideal: 15, max: 30 },
            },
            audio: false,
        }).then(function (s) {
            stream = s;
            if (cam) {
                cam.srcObject = stream;
                return cam.play();
            }
        }).then(function () {
            startOverlayDraw();
        });
    }

    function stopCam() {
        stopAuto();
        if (scanAbort) { try { scanAbort.abort(); } catch (e) {} scanAbort = null; }
        if (stream) {
            try { stream.getTracks().forEach(function (t) { t.stop(); }); } catch (e) {}
        }
        stream   = null;
        enrolled = false;
        passHist = [];
        if (cam) cam.srcObject = null;
    }

    // ---- capture ----
    function captureJpegBlob(quality) {
        var q = (typeof quality === 'number') ? quality : 0.78;
        var w = (cam && cam.videoWidth)  || 1280;
        var h = (cam && cam.videoHeight) || 720;
        if (cap) { cap.width = w; cap.height = h; }
        var c = cap ? cap.getContext('2d') : null;
        if (c && cam) c.drawImage(cam, 0, 0, w, h);
        return new Promise(function (resolve) {
            if (!cap) { resolve(null); return; }
            cap.toBlob(function (b) { resolve(b); }, 'image/jpeg', q);
        });
    }

    // ---- server calls ----
    function postScanFrame(blob) {
        // Cancel stale request before sending new one
        if (scanAbort) { try { scanAbort.abort(); } catch (e) {} }
        scanAbort = new AbortController();

        var fd = new FormData();
        fd.append('__RequestVerificationToken', token());
        fd.append('image', blob, 'frame.jpg');

        return fetch(scanUrl, { method: 'POST', body: fd, signal: scanAbort.signal })
            .then(function (r) { return r.json(); });
    }

    function postEnrollMany(blobs) {
        var fd = new FormData();
        fd.append('__RequestVerificationToken', token());
        fd.append('employeeId', empId);
        if (!blobs || !blobs.length) return Promise.resolve({ ok: false, error: 'NO_IMAGE' });
        for (var i = 0; i < blobs.length; i++) {
            fd.append('image', blobs[i], 'enroll_' + (i + 1) + '.jpg');
        }
        return fetch(enrollUrl, { method: 'POST', body: fd })
            .then(function (r) { return r.json(); });
    }

    function collectGoodEnrollFrames() {
        var frames = [];
        var chain  = Promise.resolve();

        for (var i = 0; i < ENROLL_COLLECT_TRIES; i++) {
            chain = chain.then(function () {
                if (frames.length >= ENROLL_MAX_IMAGES && frames[0] && frames[0].p >= 0.90) return;
                // OPT-E4: quality 0.78 for collection (was 0.80)
                return captureJpegBlob(0.78).then(function (blob) {
                    return postScanFrame(blob).then(function (r) {
                        if (!r || r.ok !== true) return sleep(ENROLL_COLLECT_DELAY_MS);
                        if (r.count !== 1)       return sleep(ENROLL_COLLECT_DELAY_MS);
                        var p    = (typeof r.liveness === 'number') ? r.liveness : 0;
                        var pass = (r.livenessOk === true) && (p >= perFrame);
                        if (pass) {
                            frames.push({ blob: blob, p: p });
                            frames.sort(function (a, b) { return b.p - a.p; });
                            if (frames.length > ENROLL_MAX_IMAGES) frames.length = ENROLL_MAX_IMAGES;
                        }
                        return sleep(ENROLL_COLLECT_DELAY_MS);
                    }).catch(function (e) {
                        if (e && e.name === 'AbortError') return;
                        return sleep(ENROLL_COLLECT_DELAY_MS);
                    });
                });
            });
        }

        return chain.then(function () {
            return frames.map(function (x) { return x.blob; });
        });
    }

    // ---- bounding box overlay: corner brackets matching kiosk style ----
    function startOverlayDraw() {
        if (overlayDrawing || !overlay) return;
        overlayDrawing = true;

        function drawFrame() {
            if (!overlay) return;
            var c = overlay.getContext('2d');
            var rect = overlay.getBoundingClientRect();
            if (overlay.width  !== rect.width)  overlay.width  = rect.width;
            if (overlay.height !== rect.height) overlay.height = rect.height;

            c.clearRect(0, 0, overlay.width, overlay.height);

            var b = overlayBox;
            if (b && b.imgW && b.imgH) {
                var sx = overlay.width  / b.imgW;
                var sy = overlay.height / b.imgH;
                // mirror x to match scaleX(-1) on camera
                var bx = overlay.width - (b.x + b.w) * sx;
                var by = b.y * sy;
                var bw = b.w * sx;
                var bh = b.h * sy;
                var pad = 8;
                bx -= pad; by -= pad; bw += pad * 2; bh += pad * 2;

                var L = Math.min(bw, bh) * 0.18;
                c.save();
                c.strokeStyle = overlayColor;
                c.lineWidth   = 2.5;
                c.lineCap     = 'round';
                c.shadowColor = overlayColor;
                c.shadowBlur  = 10;

                // top-left
                c.beginPath(); c.moveTo(bx, by + L);        c.lineTo(bx, by);        c.lineTo(bx + L, by);        c.stroke();
                // top-right
                c.beginPath(); c.moveTo(bx + bw - L, by);   c.lineTo(bx + bw, by);   c.lineTo(bx + bw, by + L);   c.stroke();
                // bottom-left
                c.beginPath(); c.moveTo(bx, by + bh - L);   c.lineTo(bx, by + bh);   c.lineTo(bx + L, by + bh);   c.stroke();
                // bottom-right
                c.beginPath(); c.moveTo(bx + bw - L, by + bh); c.lineTo(bx + bw, by + bh); c.lineTo(bx + bw, by + bh - L); c.stroke();

                c.restore();
            }

            requestAnimationFrame(drawFrame);
        }

        requestAnimationFrame(drawFrame);
    }

    // ---- auto enroll ----
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

    function autoTick() {
        if (enrolled || !stream || busy) return;
        busy = true;

        // OPT-E5: quality 0.75 for tick (matches original, optimal for speed)
        captureJpegBlob(0.75).then(function (blob) {
            return postScanFrame(blob).then(function (r) {
                overlayBox = (r && r.faceBox) ? r.faceBox : null;

                if (!r || r.ok !== true) {
                    overlayColor = '#f87171';  // red
                    setStatus(camStatus, (r && r.error) ? r.error : 'Scan error.', 'danger');
                    passHist = [];
                    return;
                }
                if (r.count === 0) {
                    overlayBox   = null;
                    overlayColor = '#94a3b8';  // slate
                    setStatus(camStatus, 'No face detected.', 'warning');
                    passHist = [];
                    return;
                }
                if (r.count > 1) {
                    overlayColor = '#fb923c';  // orange
                    setStatus(camStatus, 'One person only.', 'warning');
                    passHist = [];
                    return;
                }

                var p    = (typeof r.liveness === 'number') ? r.liveness : 0;
                var pass = (r.livenessOk === true) && (p >= perFrame);

                overlayColor = pass ? '#34d399' : '#fbbf24';

                passHist.push(pass ? 1 : 0);
                if (passHist.length > PASS_WINDOW) passHist.shift();

                var sum  = 0;
                for (var i = 0; i < passHist.length; i++) sum += passHist[i];
                var need = Math.max(0, PASS_REQUIRED - sum);

                if (pass) {
                    setStatusHtml(camStatus,
                        'Face OK -- liveness: <b>' + p.toFixed(2) + '</b>' +
                        (need > 0 ? ', hold still (' + need + ' more).' : ', collecting...'), 'success');
                } else {
                    setStatusHtml(camStatus,
                        'Face OK -- liveness: <b>' + p.toFixed(2) + '</b>, improve lighting.', 'warning');
                }

                if (sum < PASS_REQUIRED) return;

                setStatus(camStatus, 'Collecting frames...', 'info');

                return collectGoodEnrollFrames().then(function (frames) {
                    if (!frames || !frames.length) {
                        overlayColor = '#f87171';
                        setStatus(camStatus, 'Could not collect enough frames -- try again.', 'danger');
                        passHist = [];
                        return;
                    }

                    setStatus(camStatus, 'Saving enrollment (' + frames.length + ' frame(s))...', 'info');

                    return postEnrollMany(frames).then(function (saved) {
                        if (saved && saved.ok === true) {
                            enrolled     = true;
                            overlayColor = '#60a5fa';  // blue
                            setStatus(camStatus, 'Enrollment saved. Redirecting...', 'success');
                            stopAuto();
                            if (redirectUrl) {
                                setTimeout(function () { window.location.href = redirectUrl; }, 2000);
                            }
                            return;
                        }
                        overlayColor = '#f87171';
                        setStatus(camStatus, (saved && saved.error) ? saved.error : 'Enrollment failed. Try again.', 'danger');
                        passHist = [];
                    });
                });

            }).catch(function (e) {
                if (e && e.name === 'AbortError') return;
                overlayColor = '#94a3b8';
                overlayBox   = null;
            });
        }).finally(function () {
            busy = false;
        });
    }

    // ---- upload path ----
    function handleUpload(f) {
        if (!f) return;
        setStatus(upStatus, 'Processing...', 'info');

        var fd = new FormData();
        fd.append('__RequestVerificationToken', token());
        fd.append('employeeId', empId);
        fd.append('image', f, f.name);

        fetch(enrollUrl, { method: 'POST', body: fd })
            .then(function (r) { return r.json(); })
            .then(function (data) {
                if (data && data.ok === true) {
                    setStatus(upStatus, 'Enrollment saved!', 'success');
                    if (redirectUrl) setTimeout(function () { window.location.href = redirectUrl; }, 2000);
                } else {
                    setStatus(upStatus, (data && data.error) ? data.error : 'Upload failed. Try again.', 'danger');
                }
            })
            .catch(function () {
                setStatus(upStatus, 'Upload error. Check connection.', 'danger');
            });
    }

    // ---- wire events ----
    var choices = document.querySelectorAll('[data-enroll-mode]');
    for (var ci = 0; ci < choices.length; ci++) {
        (function (c) {
            c.addEventListener('click', function () {
                var mode = c.getAttribute('data-enroll-mode');
                if (mode === 'live') {
                    showLive();
                    startCam()
                        .then(function () {
                            setStatus(camStatus, 'Camera ready. Look directly at the lens.', 'info');
                            startAuto();
                        })
                        .catch(function () {
                            setStatus(camStatus, 'Camera blocked. Please allow camera permission.', 'danger');
                        });
                } else {
                    showUpload();
                }
            });
            c.addEventListener('keydown', function (e) {
                if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); c.click(); }
            });
        })(choices[ci]);
    }

    var backBtns = document.querySelectorAll('[data-enroll-back]');
    for (var bi = 0; bi < backBtns.length; bi++) {
        backBtns[bi].addEventListener('click', function () {
            stopCam();
            showWizard();
        });
    }

    if (file) {
        file.addEventListener('change', function () {
            if (file.files && file.files[0]) handleUpload(file.files[0]);
        });
    }

})();
