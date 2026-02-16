(function () {
    var root = document.getElementById("enrollRoot");
    if (!root) return;

    var empId = (root.getAttribute("data-employee-id") || "").trim();
    var perFrame = parseFloat(root.getAttribute("data-per-frame") || "0.75") || 0.75;
    var scanUrl = root.getAttribute("data-scan-url") || "/Biometrics/ScanFrame";
    var enrollUrl = root.getAttribute("data-enroll-url") || "/Biometrics/Enroll";

    var cam = document.getElementById("cam");
    var cap = document.getElementById("cap");
    var btnStart = document.getElementById("btnStart");
    var btnScan = document.getElementById("btnScan");
    var btnStop = document.getElementById("btnStop");
    var camStatus = document.getElementById("camStatus");
    var upStatus = document.getElementById("upStatus");

    var file = document.getElementById("file");
    var btnUpload = document.getElementById("btnUpload");

    var stream = null;
    var busy = false;

    function token() {
        var el = document.querySelector('input[name="__RequestVerificationToken"]');
        return el ? el.value : "";
    }

    function setStatus(el, html, kind) {
        if (!el) return;
        el.innerHTML = '<div class="alert alert-' + kind + ' py-2 mb-0">' + html + '</div>';
    }

    function clearStatus(el) {
        if (el) el.innerHTML = "";
    }

    async function startCam() {
        clearStatus(camStatus);

        if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia)
            throw new Error("Camera API not available");

        stream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: "user" }, audio: false });
        cam.srcObject = stream;

        btnScan.disabled = false;
        btnStop.disabled = false;
    }

    function stopCam() {
        if (!stream) return;
        stream.getTracks().forEach(function (t) { t.stop(); });
        stream = null;
        cam.srcObject = null;

        btnScan.disabled = true;
        btnStop.disabled = true;
    }

    function sleep(ms) {
        return new Promise(function (r) { setTimeout(r, ms); });
    }

    async function captureJpegBlob(quality) {
        var w = cam.videoWidth || 1280;
        var h = cam.videoHeight || 720;

        cap.width = w;
        cap.height = h;

        var ctx = cap.getContext("2d");
        ctx.drawImage(cam, 0, 0, w, h);

        return await new Promise(function (resolve) {
            cap.toBlob(function (b) { resolve(b); }, "image/jpeg", quality);
        });
    }

    async function postScanFrame(blob) {
        var fd = new FormData();
        fd.append("image", blob, "frame.jpg");

        var res = await fetch(scanUrl, { method: "POST", body: fd });
        return await res.json();
    }

    async function postEnroll(blob, filename) {
        var fd = new FormData();
        fd.append("__RequestVerificationToken", token());
        fd.append("employeeId", empId);
        fd.append("image", blob, filename || "enroll.jpg");

        var res = await fetch(enrollUrl, { method: "POST", body: fd });
        return await res.json();
    }

    async function scanAndEnroll() {
        if (!stream) return;
        if (busy) return;
        busy = true;

        try {
            setStatus(camStatus, "Scanning... hold still.", "info");

            var okCount = 0;
            var best = 0;
            var lastBlob = null;

            for (var i = 0; i < 15; i++) {
                lastBlob = await captureJpegBlob(0.85);
                var r = await postScanFrame(lastBlob);

                if (!r || r.ok !== true) {
                    setStatus(camStatus, (r && r.error) ? r.error : "Scan error", "danger");
                    busy = false;
                    return;
                }

                if (r.count === 0) {
                    setStatus(camStatus, "No face detected.", "warning");
                    continue;
                }

                if (r.count > 1) {
                    setStatus(camStatus, "One person only.", "warning");
                    continue;
                }

                var p = (typeof r.liveness === "number") ? r.liveness : 0;
                if (p > best) best = p;

                if (r.livenessOk === true && p >= perFrame) okCount++;

                var kind = r.livenessOk ? "success" : "warning";
                setStatus(camStatus, "Face ok. Liveness: <b>" + p.toFixed(2) + "</b> (best " + best.toFixed(2) + ")", kind);

                if (okCount >= 2 || best >= 0.88) break;
                await sleep(220);
            }

            if (okCount < 2 && best < 0.88) {
                setStatus(camStatus, "Liveness failed. Try again with better lighting.", "danger");
                busy = false;
                return;
            }

            setStatus(camStatus, "Saving enrollment...", "info");
            var saved = await postEnroll(lastBlob, "enroll.jpg");

            if (saved && saved.ok === true) {
                setStatus(camStatus, "Enrollment saved.", "success");
            } else {
                setStatus(camStatus, (saved && saved.error) ? saved.error : "Enroll failed", "danger");
            }
        } catch (e) {
            setStatus(camStatus, "Camera scan failed: " + e.message, "danger");
        } finally {
            busy = false;
        }
    }

    async function enrollUpload() {
        if (busy) return;
        if (!file.files || !file.files[0]) {
            setStatus(upStatus, "Choose an image first.", "warning");
            return;
        }

        busy = true;
        try {
            setStatus(upStatus, "Verifying...", "info");

            var img = file.files[0];
            var res = await postEnroll(img, img.name || "upload.jpg");

            if (res && res.ok === true) {
                setStatus(upStatus, "Enrollment saved.", "success");
            } else {
                setStatus(upStatus, (res && res.error) ? res.error : "Enroll failed", "danger");
            }
        } catch (e) {
            setStatus(upStatus, "Upload failed: " + e.message, "danger");
        } finally {
            busy = false;
        }
    }

    if (btnStart) {
        btnStart.addEventListener("click", async function () {
            try {
                await startCam();
                setStatus(camStatus, "Camera ready.", "success");
            } catch (e) {
                setStatus(camStatus, "Camera blocked: " + e.message, "danger");
            }
        });
    }

    if (btnStop) btnStop.addEventListener("click", stopCam);
    if (btnScan) btnScan.addEventListener("click", scanAndEnroll);
    if (btnUpload) btnUpload.addEventListener("click", enrollUpload);
})();
