/* global $, toastr, Swal */
(function () {
  const base = "";

  const cfg = {
    frames: 6,
    perFrameLiveness: 0.75,
    passOkFrames: 2,
    passBest: 0.88,
    burstQuality: 0.75,
    enrollQuality: 0.92,
    cooldownMs: 700
  };

  const video = $("#camA").get(0);
  const canvas = document.getElementById("capA");
  const ctx = canvas.getContext("2d", { willReadFrequently: true });

  const $empId = $("#empId");
  const $btnStart = $("#btnStart");
  const $btnRedo = $("#btnRedo");

  const $status = $("#aStatus");
  const $sub = $("#aSub");
  const $hintMsg = $("#hintMsgA");
  const $hintSub = $("#hintSubA");

  const $box = $("#faceBoxA");
  const $guide = $("#guideA");

  const $camDot = $("#dotCamA");
  const $dlibDot = $("#dotDlibA");
  const $livDot = $("#dotLivA");
  const $netDot = $("#dotNetA");

  toastr.options = {
    closeButton: false,
    newestOnTop: true,
    progressBar: true,
    positionClass: "toast-top-center",
    timeOut: 1600,
    extendedTimeOut: 700,
    preventDuplicates: true
  };

  function setDot($dot, state) {
    $dot.removeClass("good bad warn");
    if (state === "good") $dot.addClass("good");
    else if (state === "bad") $dot.addClass("bad");
    else $dot.addClass("warn");
  }

  function setStatus(t, s) {
    $status.text(t);
    if (typeof s === "string") $sub.text(s);
  }

  function setHint(m, s) {
    if (typeof m === "string") $hintMsg.text(m);
    if (typeof s === "string") $hintSub.text(s);
  }

  let stream = null;
  let running = false;

  async function startCamera() {
    try {
      stream = await navigator.mediaDevices.getUserMedia({
        video: { facingMode: "user", width: { ideal: 1280 }, height: { ideal: 720 } },
        audio: false
      });
      video.srcObject = stream;

      await new Promise(res => { video.onloadedmetadata = () => res(); });

      canvas.width = video.videoWidth || 640;
      canvas.height = video.videoHeight || 480;

      setDot($camDot, "good");
    } catch (e) {
      setDot($camDot, "bad");
      await Swal.fire({ icon: "error", title: "Camera blocked", text: "Allow camera access, then reload." });
      throw e;
    }
  }

  function captureJpegBlob(q) {
    ctx.drawImage(video, 0, 0, canvas.width, canvas.height);
    return new Promise(resolve => canvas.toBlob(resolve, "image/jpeg", q));
  }

  async function postImage(url, blob, extra) {
    const fd = new FormData();
    if (extra) Object.keys(extra).forEach(k => fd.append(k, extra[k]));
    fd.append("image", blob, "frame.jpg");
    return $.ajax({ url, method: "POST", data: fd, processData: false, contentType: false });
  }

  function mapCoverBoxToScreen(box) {
    const rect = video.getBoundingClientRect();
    const vw = rect.width, vh = rect.height;
    const sw = canvas.width, sh = canvas.height;

    const scale = Math.max(vw / sw, vh / sh);
    const dispW = sw * scale;
    const dispH = sh * scale;
    const offX = (vw - dispW) / 2;
    const offY = (vh - dispH) / 2;

    return {
      left: rect.left + offX + (box.Left * scale),
      top: rect.top + offY + (box.Top * scale),
      width: box.Width * scale,
      height: box.Height * scale
    };
  }

  function showFaceBox(face, level) {
    if (!face) {
      $box.hide();
      $guide.css("border-color", "rgba(255,255,255,.18)");
      return;
    }

    const m = mapCoverBoxToScreen(face);
    const wrapRect = document.querySelector(".cam-shell").getBoundingClientRect();

    $box
      .removeClass("warn bad")
      .addClass(level === "warn" ? "warn" : (level === "bad" ? "bad" : ""))
      .css({
        display: "block",
        left: (m.left - wrapRect.left) + "px",
        top: (m.top - wrapRect.top) + "px",
        width: m.width + "px",
        height: m.height + "px"
      });
  }

  async function runEnroll() {
    if (running) return;
    running = true;

    try {
      const emp = ($empId.val() || "").toString().trim().toUpperCase();
      $empId.val(emp);

      if (!emp) {
        toastr.warning("Employee ID is required.");
        running = false;
        return;
      }

      setDot($netDot, "warn");
      setStatus("STARTING", "Preparing camera");
      setHint("Center your face", "Hold still during scan");

      if (!stream) await startCamera();
      setDot($netDot, "good");

      $btnRedo.addClass("d-none");

      let okCount = 0;
      let bestP = 0;
      let bestBlob = null;

      setDot($livDot, "warn");

      for (let i = 0; i < cfg.frames; i++) {
        const blob = await captureJpegBlob(cfg.burstQuality);
        const scan = await postImage(base + "/Biometrics/ScanFrame", blob);

        const count = scan && typeof scan.count === "number"
          ? scan.count
          : (scan.faces && scan.faces.length ? scan.faces.length : 0);

        if (count === 0) {
          setDot($dlibDot, "warn");
          setHint("No face detected", "Look at the camera");
          showFaceBox(null, "bad");
          continue;
        }
        if (count > 1) {
          setDot($dlibDot, "bad");
          setHint("One person only", "Others step away from the camera");
          showFaceBox(null, "bad");
          continue;
        }

        const face = scan.faces[0];
        setDot($dlibDot, "good");
        showFaceBox(face, "good");
        setHint("Hold still", "Scanning…");

        const p = (scan && typeof scan.liveness === "number") ? scan.liveness : null;
        if (p !== null) {
          setDot($livDot, "good");
          bestP = Math.max(bestP, p);
          if (p >= cfg.perFrameLiveness) okCount++;
          if (!bestBlob || p >= bestP) bestBlob = blob;
        } else {
          setDot($livDot, "warn");
        }

        setStatus("SCANNING", `ok ${okCount}/${cfg.passOkFrames} • best ${bestP.toFixed(2)}`);

        if (okCount >= cfg.passOkFrames || bestP >= cfg.passBest) break;
      }

      const pass = (okCount >= cfg.passOkFrames) || (bestP >= cfg.passBest);
      if (!pass || !bestBlob) {
        setDot($livDot, "bad");
        setStatus("FAIL", "Redo scan");
        setHint("Scan failed", "Adjust and redo");
        toastr.warning("Scan failed. Redo.");
        $btnRedo.removeClass("d-none");
        return;
      }

      setStatus("ENROLLING", "Saving face template");
      setHint("Please wait", "Saving record");

      const enrollBlob = bestBlob; // best liveness frame
      const res = await postImage(base + "/Biometrics/Enroll", enrollBlob, { employeeId: emp });

      if (res && res.ok === true) {
        toastr.success("Enrollment saved.");
        await Swal.fire({ icon: "success", title: "Enrolled", text: emp });
        setStatus("DONE", "Ready");
        setHint("Enrollment complete", "You can enroll another");
        return;
      }

      const err = (res && (res.error || res.message)) ? (res.error || res.message) : "ENROLL_FAILED";
      toastr.error("Enroll failed: " + err);
      setStatus("FAIL", "Redo scan");
      setHint("Enroll failed", err);
      $btnRedo.removeClass("d-none");
    } catch (e) {
      toastr.error((e && e.message) ? e.message : "Error");
      setStatus("ERROR", "Redo scan");
      setHint("System error", "Redo");
      $btnRedo.removeClass("d-none");
    } finally {
      running = false;
    }
  }

  $btnStart.on("click", runEnroll);
  $btnRedo.on("click", runEnroll);

  // Uppercase enforcement
  $empId.on("input", function () {
    const v = ($empId.val() || "").toString().toUpperCase();
    $empId.val(v);
  });

  setStatus("Idle", "Ready");
})();
