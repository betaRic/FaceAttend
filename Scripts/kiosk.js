/* global $, toastr, Swal */
(function () {
  const base = ""; // same-origin

  // Fast burst config (tune later)
  const cfg = {
    frames: 6,
    perFrameLiveness: 0.75,
    passOkFrames: 2,
    passBest: 0.88,

    // Guards based on face-box size in the captured frame
    minFaceRatio: 0.12,  // too far if below
    maxFaceRatio: 0.45,  // too close if above

    // Guards based on face center position (0..1)
    centerMin: 0.18,
    centerMax: 0.82,

    // How long to show final result before restarting scan
    cooldownMs: 900,

    // JPEG quality for burst frames (speed)
    burstQuality: 0.75,

    // JPEG quality for identify frame (accuracy)
    identifyQuality: 0.90
  };

  const $video = $("#cam");
  const video = $video.get(0);
  const canvas = document.getElementById("cap");
  const ctx = canvas.getContext("2d", { willReadFrequently: true });

  const $status = $("#status");
  const $sub = $("#sub");
  const $clock = $("#clock");
  const $guide = $("#guide");
  const $box = $("#faceBox");

  const $hintMsg = $("#hintMsg");
  const $hintSub = $("#hintSub");

  const $camDot = $("#dotCam");
  const $dlibDot = $("#dotDlib");
  const $livDot = $("#dotLiv");
  const $netDot = $("#dotNet");

  const debug = new URLSearchParams(location.search).get("debug") === "1";
  if (!debug) $("#debugRow").hide();

  toastr.options = {
    closeButton: false,
    newestOnTop: true,
    progressBar: true,
    positionClass: "toast-top-center",
    timeOut: 1600,
    extendedTimeOut: 700,
    preventDuplicates: true
  };

  function setStatus(text, sub) {
    $status.text(text).addClass("pulse");
    setTimeout(() => $status.removeClass("pulse"), 200);
    if (typeof sub === "string") $sub.text(sub);
  }

  function setHint(msg, sub) {
    if (typeof msg === "string") $hintMsg.text(msg);
    if (typeof sub === "string") $hintSub.text(sub);
  }

  function setDot($dot, state) {
    $dot.removeClass("good bad warn");
    if (state === "good") $dot.addClass("good");
    else if (state === "bad") $dot.addClass("bad");
    else if (state === "warn") $dot.addClass("warn");
  }

  function tickClock() { $clock.text(new Date().toLocaleString()); }
  setInterval(tickClock, 1000);
  tickClock();

  let stream = null;
  let running = false;
  let coolingDown = false;

  // Hidden admin hotkey (silent)
  $(document).on("keydown", function (e) {
    if (e.ctrlKey && e.shiftKey && (e.key === "A" || e.key === "a")) {
      window.location.href = "/Admin/Enrolled";
    }
  });

  async function startCamera() {
    const constraints = {
      video: { facingMode: "user", width: { ideal: 1280 }, height: { ideal: 720 } },
      audio: false
    };

    try {
      stream = await navigator.mediaDevices.getUserMedia(constraints);
      video.srcObject = stream;

      await new Promise(res => { video.onloadedmetadata = () => res(); });

      canvas.width = video.videoWidth || 640;
      canvas.height = video.videoHeight || 480;

      setDot($camDot, "good");
    } catch (err) {
      setDot($camDot, "bad");
      await Swal.fire({
        icon: "error",
        title: "Camera blocked",
        text: "Allow camera access, then reload the page."
      });
      throw err;
    }
  }

  function captureJpegBlob(quality) {
    ctx.drawImage(video, 0, 0, canvas.width, canvas.height);
    return new Promise(resolve => canvas.toBlob(resolve, "image/jpeg", quality));
  }

  async function postImage(url, blob) {
    const fd = new FormData();
    fd.append("image", blob, "frame.jpg");
    return $.ajax({ url, method: "POST", data: fd, processData: false, contentType: false });
  }

  // Map source coords -> displayed coords for object-fit: cover
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

  function computeGuards(face) {
    const sw = canvas.width, sh = canvas.height;

    const ratio = face.Width / sw; // face size relative to frame width
    const cx = (face.Left + (face.Width / 2)) / sw;
    const cy = (face.Top + (face.Height / 2)) / sh;

    if (ratio < cfg.minFaceRatio) return { code: "TOO_FAR", level: "warn", msg: "Move closer", sub: "Center your face and step closer", ratio, cx, cy };
    if (ratio > cfg.maxFaceRatio) return { code: "TOO_CLOSE", level: "warn", msg: "Move back", sub: "Step back a little", ratio, cx, cy };
    if (cx < cfg.centerMin || cx > cfg.centerMax || cy < cfg.centerMin || cy > cfg.centerMax)
      return { code: "OFF_CENTER", level: "warn", msg: "Center your face", sub: "Align your face with the guide", ratio, cx, cy };

    return { code: "OK", level: "good", msg: "Hold still", sub: "Scanning…", ratio, cx, cy };
  }

  function showFaceUI(faceCount, faceBox, levelClass) {
    if (faceCount === 1 && faceBox) {
      $guide.removeClass("bad").addClass("good");

      const m = mapCoverBoxToScreen(faceBox);
      const wrapRect = document.getElementById("wrap").getBoundingClientRect();

      $box
        .removeClass("warn bad")
        .addClass(levelClass === "warn" ? "warn" : (levelClass === "bad" ? "bad" : ""))
        .css({
          display: "block",
          left: (m.left - wrapRect.left) + "px",
          top: (m.top - wrapRect.top) + "px",
          width: m.width + "px",
          height: m.height + "px"
        });

      setDot($dlibDot, "good");
      return;
    }

    // else
    $guide.removeClass("good").addClass("bad");
    $box.hide();
  }

  async function visitorPrompt() {
    const r = await Swal.fire({
      icon: "question",
      title: "Not enrolled",
      text: "If you are a visitor, tap Visitor. Otherwise tap Try again.",
      showCancelButton: true,
      confirmButtonText: "Visitor",
      cancelButtonText: "Try again",
      reverseButtons: true
    });

    if (!r.isConfirmed) return;

    const r2 = await Swal.fire({
      title: "Visitor",
      html:
        '<input id="vName" class="form-control mb-2" placeholder="Full name" />' +
        '<input id="vPurpose" class="form-control" placeholder="Purpose (optional)" />',
      focusConfirm: false,
      showCancelButton: true,
      confirmButtonText: "Continue",
      cancelButtonText: "Cancel",
      preConfirm: () => {
        const name = (document.getElementById("vName").value || "").trim();
        const purpose = (document.getElementById("vPurpose").value || "").trim();

        if (!name) {
          Swal.showValidationMessage("Full name is required");
          return;
        }
        return { name, purpose };
      }});

    if (!r2.isConfirmed) return;

        // Log visitor now (JSON in App_Data/visitors.json)
    const payload = { name: (r2.value && r2.value.name) || "", purpose: (r2.value && r2.value.purpose) || "" };

    try {
      const resp = await $.ajax({
        url: base + "/Attendance/Visitor",
        method: "POST",
        contentType: "application/json; charset=utf-8",
        data: JSON.stringify(payload)
      });

      if (resp && resp.ok === true) {
        toastr.success("Visitor recorded. Please proceed to the office desk.");
      } else {
        toastr.warning("Visitor not saved. Please proceed to the office desk.");
      }
    } catch (err) {
      toastr.warning("Visitor not saved. Please proceed to the office desk.");
    }}

  function cooldownThenRestart() {
    coolingDown = true;
    setTimeout(() => {
      coolingDown = false;
      startFlow();
    }, cfg.cooldownMs);
  }

  async function startFlow() {
    if (running || coolingDown) return;
    running = true;

    try {
      setDot($netDot, "warn");
      setStatus("STARTING…", "Preparing camera");
      setHint("Center your face in the frame", "Hold still during scanning");

      if (!stream) await startCamera();

      setDot($netDot, "good");
      setStatus("SCANNING…", "Hold still");
      setDot($livDot, "warn");

      let okCount = 0;
      let bestP = 0;

      for (let i = 0; i < cfg.frames; i++) {
        const blob = await captureJpegBlob(cfg.burstQuality);

        const scan = await postImage(base + "/Biometrics/ScanFrame", blob);

        const count = scan && typeof scan.count === "number"
          ? scan.count
          : (scan.faces && scan.faces.length ? scan.faces.length : 0);

        const face = (scan.faces && scan.faces.length) ? scan.faces[0] : null;

        if (count === 0) {
          showFaceUI(0, null, "bad");
          setDot($dlibDot, "warn");
          setHint("No face detected", "Look at the camera");
          if (debug) $("#debug").text("No face");
          continue;
        }

        if (count > 1) {
          showFaceUI(count, null, "bad");
          setDot($dlibDot, "bad");
          setHint("One person only", "Others step away from the camera");
          if (debug) $("#debug").text("Multiple faces: " + count);
          continue;
        }

        // exactly 1 face
        const g = computeGuards(face);
        showFaceUI(1, face, g.level);
        setHint(g.msg, g.sub);

        if (debug) $("#debug").text(`Guard=${g.code} ratio=${g.ratio.toFixed(3)} cx=${g.cx.toFixed(2)} cy=${g.cy.toFixed(2)}`);

        // Only count liveness if guard is OK
        if (g.code !== "OK") continue;

        const p = (scan && typeof scan.liveness === "number") ? scan.liveness : null;

        if (p !== null) {
          setDot($livDot, "good");
          bestP = Math.max(bestP, p);
          if (p >= cfg.perFrameLiveness) okCount++;
        } else {
          setDot($livDot, "warn");
        }

        setStatus("SCANNING…", `ok ${okCount}/${cfg.passOkFrames} • best ${bestP.toFixed(2)}`);

        // Early exit (fast)
        if (okCount >= cfg.passOkFrames || bestP >= cfg.passBest) break;
      }

      const pass = (okCount >= cfg.passOkFrames) || (bestP >= cfg.passBest);

      if (!pass) {
        setStatus("TRY AGAIN", "Liveness did not pass");
        setHint("Scan failed", "Adjust position and try again");
        toastr.warning("Scan failed. Try again.");
        setDot($livDot, "bad");
        cooldownThenRestart();
        return;
      }

      setStatus("PASS", "Identifying…");
      setHint("Please wait", "Checking your record");
      toastr.success("Liveness PASS");

      const blob2 = await captureJpegBlob(cfg.identifyQuality);
      const id = await postImage(base + "/Biometrics/Identify", blob2);

      if (id && id.ok === true) {
        setStatus("SUCCESS", "Attendance recorded");
        setHint("Done", "You may proceed");
        toastr.success("Attendance recorded");

        // Later: call Attendance/Record here (we can add it when ready)
        cooldownThenRestart();
        return;
      }

      // No match
      setStatus("NOT ENROLLED", "No matching record");
      setHint("Not enrolled", "Go to admin for enrollment");
      toastr.error("Not enrolled / No match");

      await visitorPrompt();
      cooldownThenRestart();
    } catch (e) {
      setStatus("ERROR", "Please try again");
      setHint("System error", "Try again");
      toastr.error((e && e.message) ? e.message : "Error");
      cooldownThenRestart();
    } finally {
      running = false;
    }
  }

  // Auto-start
  startFlow();
})();




