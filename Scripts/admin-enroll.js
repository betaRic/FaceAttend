(function () {
  // face enroll wizard, (upload or live scan), auto enroll, minimal buttons
  // note: clicking a mode card counts as a user gesture, so camera/file permission works better.

  var root = document.getElementById("enrollRoot");
  if (!root) return;

  // ------------------------
  // config from data- attrs
  // ------------------------
  var empId = (root.getAttribute("data-employee-id") || "").trim();
  var perFrame = parseFloat(root.getAttribute("data-per-frame") || "0.75");
  if (!isFinite(perFrame) || perFrame <= 0) perFrame = 0.75;

  var scanUrl = root.getAttribute("data-scan-url") || "/Biometrics/ScanFrame";
  var enrollUrl = root.getAttribute("data-enroll-url") || "/Biometrics/Enroll";

  // ------------------------
  // dom
  // ------------------------
  function el(id) { return document.getElementById(id); }

  var wizard = el("enrollWizard");
  var livePane = el("livePane");
  var uploadPane = el("uploadPane");

  var cam = el("cam");
  var cap = el("cap");
  var camStatus = el("camStatus");
  var upStatus = el("upStatus");

  var file = el("file");

  // ------------------------
  // state
  // ------------------------
  var stream = null;
  var busy = false;
  var enrolled = false;

  // auto enroll tuning
  var AUTO_INTERVAL_MS = 350;
  var PASS_WINDOW = 5;
  var PASS_REQUIRED = 2;

  // capture tuning
  // fixed 640x480 para hindi sobrang bigat ang bawat scan request
  var CAPTURE_WIDTH = 640;
  var CAPTURE_HEIGHT = 480;

  // enrollment tuning
  var ENROLL_MAX_IMAGES = 5;
  var MIN_ENROLL_FRAMES = 3;

  var autoTimer = null;
  var passHist = [];
  var goodFrames = [];

  function token() {
    var t = document.querySelector('input[name="__RequestVerificationToken"]');
    return t ? t.value : "";
  }

  function escapeHtml(value) {
    return String(value || "").replace(/[&<>"']/g, function (ch) {
      return ({
        "&": "&amp;",
        "<": "&lt;",
        ">": "&gt;",
        '"': "&quot;",
        "'": "&#39;"
      })[ch] || ch;
    });
  }

  function setStatus(target, html, kind) {
    if (!target) return;
    target.innerHTML = '<div class="alert alert-' + kind + ' py-2 mb-0">' + html + '</div>';
  }

  function clearStatus(target) {
    if (!target) return;
    target.innerHTML = "";
  }

  function showWizard(show) {
    if (!wizard) return;
    wizard.classList.toggle("d-none", !show);
  }

  function showPane(pane, show) {
    if (!pane) return;
    pane.classList.toggle("d-none", !show);
  }

  function resetGoodFrames() {
    goodFrames = [];
  }

  function pushGoodFrame(blob, p) {
    if (!blob) return;

    goodFrames.push({ blob: blob, p: (typeof p === "number" ? p : 0) });
    goodFrames.sort(function (a, b) { return b.p - a.p; });

    if (goodFrames.length > ENROLL_MAX_IMAGES) {
      goodFrames.length = ENROLL_MAX_IMAGES;
    }
  }

  function getGoodFrameBlobs() {
    return goodFrames.map(function (x) { return x.blob; });
  }

  function describeEnrollError(r) {
    if (!r) return "enroll failed";

    if (r.error === "FACE_ALREADY_ENROLLED") {
      var who = r.matchEmployeeId
        ? " matched with employee <b>" + escapeHtml(r.matchEmployeeId) + "</b>"
        : "";

      var dist = (typeof r.distance === "number")
        ? ", distance: <b>" + r.distance.toFixed(4) + "</b>"
        : "";

      var hits = (typeof r.matchCount === "number" && typeof r.hitsRequired === "number")
        ? ", matched frames: <b>" + r.matchCount + "/" + r.hitsRequired + "</b>"
        : "";

      return "face already enrolled." + who + dist + hits;
    }

    if (r.error === "NO_GOOD_FRAME") {
      return "no good frame found, try better lighting and hold still.";
    }

    if (r.error === "EMPLOYEE_NOT_FOUND") {
      return "employee not found.";
    }

    return r.error || "enroll failed";
  }

  // ------------------------
  // camera
  // ------------------------
  async function startCam() {
    clearStatus(camStatus);

    if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
      throw new Error("camera api not available");
    }

    stream = await navigator.mediaDevices.getUserMedia({
      video: { facingMode: "user" },
      audio: false
    });

    cam.srcObject = stream;

    // in some browsers need play() for video to start
    try { await cam.play(); } catch { }

    setStatus(camStatus, "camera ready, auto enroll running, hold still.", "info");
  }

  function stopCam() {
    stopAuto();

    if (stream) {
      try { stream.getTracks().forEach(function (t) { t.stop(); }); } catch { }
    }

    stream = null;
    enrolled = false;
    passHist = [];
    resetGoodFrames();

    if (cam) cam.srcObject = null;
  }

  // ------------------------
  // capture + server calls
  // ------------------------
  async function captureJpegBlob(quality) {
    var w = CAPTURE_WIDTH;
    var h = CAPTURE_HEIGHT;

    cap.width = w;
    cap.height = h;

    var ctx = cap.getContext("2d");
    ctx.clearRect(0, 0, w, h);
    ctx.drawImage(cam, 0, 0, w, h);

    return await new Promise(function (resolve) {
      cap.toBlob(function (b) { resolve(b); }, "image/jpeg", quality);
    });
  }

  async function postScanFrame(blob) {
    var fd = new FormData();
    fd.append("__RequestVerificationToken", token());
    fd.append("image", blob, "frame.jpg");

    var res = await fetch(scanUrl, { method: "POST", body: fd });
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

  // ------------------------
  // auto enroll logic (live)
  // ------------------------
  function startAuto() {
    stopAuto();
    enrolled = false;
    passHist = [];
    resetGoodFrames();
    autoTimer = setInterval(autoTick, AUTO_INTERVAL_MS);
  }

  function stopAuto() {
    if (autoTimer) clearInterval(autoTimer);
    autoTimer = null;
  }

  async function autoTick() {
    if (enrolled) return;
    if (!stream) return;
    if (busy) return;

    busy = true;

    try {
      var blob = await captureJpegBlob(0.72);
      var r = await postScanFrame(blob);

      if (!r || r.ok !== true) {
        setStatus(camStatus, (r && r.error) ? r.error : "scan error", "danger");
        passHist = [];
        resetGoodFrames();
        return;
      }

      if (r.count === 0) {
        setStatus(camStatus, "no face detected.", "warning");
        passHist = [];
        resetGoodFrames();
        return;
      }

      if (r.count > 1) {
        setStatus(camStatus, "one person only.", "warning");
        passHist = [];
        resetGoodFrames();
        return;
      }

      var p = (typeof r.liveness === "number") ? r.liveness : 0;
      var pass = (r.livenessOk === true) && (p >= perFrame);

      passHist.push(pass ? 1 : 0);
      if (passHist.length > PASS_WINDOW) passHist.shift();

      var sum = 0;
      for (var i = 0; i < passHist.length; i++) sum += passHist[i];

      if (pass) {
        pushGoodFrame(blob, p);

        var needPass = Math.max(0, PASS_REQUIRED - sum);
        var needFrames = Math.max(0, MIN_ENROLL_FRAMES - goodFrames.length);

        if (sum < PASS_REQUIRED || goodFrames.length < MIN_ENROLL_FRAMES) {
          setStatus(
            camStatus,
            "face ok, liveness: <b>" + p.toFixed(2) + "</b>, good frames: <b>" + goodFrames.length + "/" + MIN_ENROLL_FRAMES + "</b>, ready in <b>" + Math.max(needPass, needFrames) + "</b> more good frame(s).",
            "success"
          );
          return;
        }

        var frames = getGoodFrameBlobs();
        setStatus(camStatus, "saving enrollment (" + frames.length + " frame(s))...", "info");

        var saved = await postEnrollMany(frames);

        if (saved && saved.ok === true) {
          enrolled = true;
          setStatus(camStatus, "enrollment saved.", "success");
          stopAuto();
          return;
        }

        setStatus(camStatus, describeEnrollError(saved), "danger");
        passHist = [];
        resetGoodFrames();
        return;
      }

      setStatus(camStatus, "face ok, liveness: <b>" + p.toFixed(2) + "</b>, improve lighting and hold still.", "warning");
    } catch (e) {
      setStatus(camStatus, "auto enroll failed: " + (e && e.message ? e.message : e), "danger");
      passHist = [];
      resetGoodFrames();
    } finally {
      busy = false;
    }
  }

  // ------------------------
  // upload enroll (auto)
  // ------------------------
  async function enrollUploadAuto() {
    if (busy) return;

    if (!file || !file.files || !file.files[0]) {
      setStatus(upStatus, "choose an image first.", "warning");
      return;
    }

    busy = true;

    try {
      setStatus(upStatus, "verifying...", "info");

      var imgs = Array.prototype.slice.call(file.files || [], 0, ENROLL_MAX_IMAGES);
      var fd = new FormData();
      fd.append("__RequestVerificationToken", token());
      fd.append("employeeId", empId);

      imgs.forEach(function (f, i) {
        fd.append("image", f, f.name || ("upload_" + (i + 1) + ".jpg"));
      });

      var res = await fetch(enrollUrl, { method: "POST", body: fd });
      var r = await res.json();

      if (r && r.ok === true) {
        setStatus(upStatus, "enrollment saved.", "success");
      } else {
        setStatus(upStatus, describeEnrollError(r), "danger");
      }
    } catch (e) {
      setStatus(upStatus, "upload failed: " + (e && e.message ? e.message : e), "danger");
    } finally {
      busy = false;
    }
  }

  // ------------------------
  // wizard wiring
  // ------------------------
  function chooseMode(mode) {
    // hide wizard, show correct pane
    showWizard(false);

    if (mode === "live") {
      showPane(uploadPane, false);
      showPane(livePane, true);

      // this click is the "gesture", so start camera here
      (async function () {
        try {
          await startCam();
          startAuto();
        } catch (e) {
          stopCam();
          showWizard(true);
          showPane(livePane, false);
          setStatus(camStatus, "camera blocked: " + (e && e.message ? e.message : e) + ", try again.", "danger");
        }
      })();

      return;
    }

    if (mode === "upload") {
      stopCam();
      showPane(livePane, false);
      showPane(uploadPane, true);
      clearStatus(upStatus);

      if (file) {
        try { file.focus(); } catch { }
      }

      return;
    }
  }

  function wireWizard() {
    if (!wizard) return;

    var cards = wizard.querySelectorAll("[data-enroll-mode]");
    for (var i = 0; i < cards.length; i++) {
      (function (card) {
        function go() {
          var mode = (card.getAttribute("data-enroll-mode") || "").trim();
          chooseMode(mode);
        }

        card.addEventListener("click", go);

        // keyboard
        card.addEventListener("keydown", function (e) {
          if (e.key === "Enter" || e.key === " ") {
            e.preventDefault();
            go();
          }
        });
      })(cards[i]);
    }
  }

  function wireUpload() {
    if (!file) return;
    file.addEventListener("change", function () {
      // auto enroll as soon as files are chosen
      enrollUploadAuto();
    });
  }

  document.addEventListener("fa:stopCam", function () {
    stopCam();
  });

  // cleanup when leaving
  window.addEventListener("beforeunload", function () {
    stopCam();
  });

  // init: wizard first, hide panes
  showWizard(true);
  showPane(livePane, false);
  showPane(uploadPane, false);

  setStatus(camStatus, "choose a method above.", "info");
  wireWizard();
  wireUpload();
})();