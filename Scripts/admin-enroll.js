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
  
  // multi-face handling
  var multiFaceWarned = false;

  // capture tuning
  // fixed 640x480 para hindi sobrang bigat ang bawat scan request
  var CAPTURE_WIDTH = 640;
  var CAPTURE_HEIGHT = 480;

  // enrollment tuning
  var ENROLL_MAX_IMAGES = 5;
  var MIN_ENROLL_FRAMES = 5;  // Changed from 3 to 5 - collect all 5 for best quality

  var autoTimer = null;
  var passHist = [];
  var goodFrames = [];

  function token() {
    var t = document.querySelector('input[name="__RequestVerificationToken"]');
    return t ? t.value : "";
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

  // =========================================================================
  // ENHANCED ERROR MESSAGES with step information
  // =========================================================================
  function describeEnrollError(r) {
    if (!r) return "enrollment failed (no response)";

    var step = r.step || "";
    var timeInfo = (typeof r.timeMs === "number") 
      ? " (took " + r.timeMs + "ms)" 
      : "";

    // VALIDATION ERRORS (Fail fast - quick)
    if (r.error === "NO_EMPLOYEE_ID") {
      return "please enter an employee ID.";
    }
    if (r.error === "EMPLOYEE_ID_TOO_LONG") {
      return "employee ID is too long (max 20 characters).";
    }
    if (r.error === "EMPLOYEE_ID_INVALID_FORMAT") {
      return "employee ID format is invalid (use letters, numbers, dash, underscore only).";
    }
    if (r.error === "NO_IMAGE") {
      return "please select or capture at least one image.";
    }
    if (r.error === "TOO_LARGE") {
      return "image file is too large (max 10MB per file).";
    }

    // EMPLOYEE LOOKUP ERRORS (Fail fast)
    if (r.error === "EMPLOYEE_NOT_FOUND") {
      return "employee not found in database. Please check the employee ID.";
    }

    // PROCESSING ERRORS
    if (r.error === "NO_GOOD_FRAME") {
      var processed = (typeof r.processed === "number") 
        ? " (processed " + r.processed + " images)" 
        : "";
      return "no good frame found. Please try better lighting, hold still, and ensure your face is clearly visible." + processed;
    }

    // DUPLICATE ERRORS
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

      return "face already enrolled" + who + dist + hits + ". Please contact administrator if this is an error.";
    }

    // NETWORK/TIMEOUT ERRORS
    if (r.error === "NETWORK_ERROR") {
      return "network error. Please check your connection and try again.";
    }

    // Default error with step info for debugging
    return (r.error || "enrollment failed") + " [step: " + step + "]" + timeInfo;
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
  // OPTIMIZED: Lower quality for faster upload, smaller resolution
  var CAPTURE_WIDTH = 480;  // Was 640
  var CAPTURE_HEIGHT = 360; // Was 480
  var UPLOAD_QUALITY = 0.65; // Was 0.72 - 20% smaller files
  
  async function captureJpegBlob(quality) {
    var w = CAPTURE_WIDTH;
    var h = CAPTURE_HEIGHT;

    cap.width = w;
    cap.height = h;

    var ctx = cap.getContext("2d");
    ctx.clearRect(0, 0, w, h);
    ctx.drawImage(cam, 0, 0, w, h);

    return await new Promise(function (resolve) {
      cap.toBlob(function (b) { resolve(b); }, "image/jpeg", quality || UPLOAD_QUALITY);
    });
  }
  
  // OPTIMIZED: Compress image file before upload
  async function compressImageFile(file, maxWidth, maxHeight, quality) {
    return new Promise(function(resolve, reject) {
      var img = new Image();
      var url = URL.createObjectURL(file);
      
      img.onload = function() {
        URL.revokeObjectURL(url);
        
        // Calculate new dimensions
        var w = img.width;
        var h = img.height;
        
        if (w > maxWidth || h > maxHeight) {
          var ratio = Math.min(maxWidth / w, maxHeight / h);
          w = Math.floor(w * ratio);
          h = Math.floor(h * ratio);
        }
        
        // Draw to canvas
        var canvas = document.createElement('canvas');
        canvas.width = w;
        canvas.height = h;
        var ctx = canvas.getContext('2d');
        ctx.drawImage(img, 0, 0, w, h);
        
        // Compress
        canvas.toBlob(function(blob) {
          resolve(blob);
        }, 'image/jpeg', quality || UPLOAD_QUALITY);
      };
      
      img.onerror = function() {
        URL.revokeObjectURL(url);
        reject(new Error('Failed to load image'));
      };
      
      img.src = url;
    });
  }

  async function postScanFrame(blob) {
    var fd = new FormData();
    fd.append("__RequestVerificationToken", token());
    fd.append("image", blob, "frame.jpg");

    var res = await fetch(scanUrl, { method: "POST", body: fd });
    return await res.json();
  }

  // =========================================================================
  // ENROLLMENT - ULTRA-OPTIMIZED with compression + parallel upload
  // -------------------------------------------------------------------------
  // SPEED OPTIMIZATIONS:
  //   1. COMPRESS images before upload (40-50% smaller)
  //   2. PARALLEL uploads (3x faster for multiple images)
  //   3. PROGRESS tracking per image
  //   4. CHUNKED processing on server
  // =========================================================================
  
  // Compress all blobs before upload
  async function compressBlobs(blobs) {
    var compressed = [];
    for (var i = 0; i < blobs.length; i++) {
      // If it's already a compressed blob from camera, use it
      if (blobs[i].size < 50000) { // < 50KB is already compressed
        compressed.push(blobs[i]);
        continue;
      }
      
      // Compress larger blobs
      try {
        var compressedBlob = await compressImageFile(
          new File([blobs[i]], "temp.jpg", { type: "image/jpeg" }),
          480, 360, UPLOAD_QUALITY
        );
        compressed.push(compressedBlob);
      } catch (e) {
        compressed.push(blobs[i]); // Fallback to original
      }
    }
    return compressed;
  }
  // Upload ALL images in ONE request (critical: must be single request to save all vectors together)
  async function postEnrollMany(blobs) {
    if (!blobs || !blobs.length) return { ok: false, error: "NO_IMAGE" };

    var startTime = Date.now();
    
    try {
      // STEP 1: Compress all images first
      setStatus(camStatus, "optimizing " + blobs.length + " image(s)...", "info");
      
      var processedBlobs = [];
      for (var i = 0; i < blobs.length; i++) {
        // Camera blobs are already small, skip re-compression
        if (blobs[i].size < 100000) { 
          processedBlobs.push(blobs[i]);
        } else {
          // Compress large uploaded files
          try {
            var compressed = await compressImageFile(
              new File([blobs[i]], "img_" + i + ".jpg", { type: "image/jpeg" }),
              480, 360, UPLOAD_QUALITY
            );
            processedBlobs.push(compressed);
          } catch (e) {
            processedBlobs.push(blobs[i]);
          }
        }
      }
      
      // STEP 2: Upload ALL images in ONE request (not parallel)
      setStatus(camStatus, "uploading " + processedBlobs.length + " sample(s)...", "info");
      
      var fd = new FormData();
      fd.append("__RequestVerificationToken", token());
      fd.append("employeeId", empId);
      
      // Add ALL images to same FormData
      for (var j = 0; j < processedBlobs.length; j++) {
        fd.append("image", processedBlobs[j], "enroll_" + (j + 1) + ".jpg");
      }
      
      var res = await fetch(enrollUrl, { method: "POST", body: fd });
      var r = await res.json();
      r.timeMs = Date.now() - startTime;
      
      console.log('[Enroll] Server response:', r);
      return r;
      
    } catch (e) {
      console.error('[Enroll] Failed:', e);
      return { ok: false, error: "NETWORK_ERROR", message: e.message };
    }
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
      var blob = await captureJpegBlob(UPLOAD_QUALITY); // Optimized quality for speed
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

      // =========================================================================
      // MULTI-FACE HANDLING: Use the main face (nearest/largest)
      // -------------------------------------------------------------------------
      // DATI: Reject kapag may multiple faces (lines 329-334)
      // NGAYON: Allow but warn - gamitin ang main face from server
      // 
      // Server returns:
      //   - count: actual number of faces detected
      //   - multiFaceWarning: true if count > 1
      //   - mainFaceIndex: which face was used
      // =========================================================================
      if (r.count > 1) {
        if (!multiFaceWarned) {
          multiFaceWarned = true;
          console.log('[Enroll] Multiple faces detected (' + r.count + '), using nearest face');
          // Trigger warning display via custom event
          document.dispatchEvent(new CustomEvent('fa:multiFaceWarning', { detail: { count: r.count } }));
        }
        // Continue processing - don't reject
      } else {
        // Clear warning if only one face now
        if (multiFaceWarned) {
          multiFaceWarned = false;
          document.dispatchEvent(new CustomEvent('fa:multiFaceWarning', { detail: { count: 1 } }));
        }
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
        
        // PREVIEW BEFORE SAVE: Show confirmation modal
        var confirmed = await showPreviewModal(frames);
        if (!confirmed) {
          // User clicked "Retake" - reset and continue
          return;
        }
        
        setStatus(camStatus, "saving enrollment (" + frames.length + " frame(s), please wait)...", "info");

        var saved = await postEnrollMany(frames);

        if (saved && saved.ok === true) {
          enrolled = true;
          var vecCount = (typeof saved.savedVectors === "number") ? saved.savedVectors : goodFrames.length;
          setStatus(camStatus, "enrollment saved successfully! [" + vecCount + " vector(s)]", "success");
          stopAuto();
          
          // SUCCESS: Show SweetAlert and redirect
          showSuccessMessage(vecCount);
          return;
        }

        // Show detailed error
        var errorMsg = describeEnrollError(saved);
        setStatus(camStatus, errorMsg, "danger");
        showErrorMessage(errorMsg);
        
        // Don't reset if it's a duplicate (user can try different angle)
        if (saved && saved.error === "FACE_ALREADY_ENROLLED") {
          // Keep the frames, let user try again
          return;
        }
        
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
  // CLIENT-SIDE PRE-CHECK for image quality
  // Returns: { ok: true } or { ok: false, error: "message" }
  async function precheckImage(file) {
    return new Promise(function(resolve) {
      // Check file size (max 5MB)
      if (file.size > 5 * 1024 * 1024) {
        resolve({ ok: false, error: "File too large (max 5MB): " + file.name });
        return;
      }
      
      // Check file type
      if (!file.type.match(/^image\/(jpeg|jpg|png)$/i)) {
        resolve({ ok: false, error: "Invalid file type (JPEG/PNG only): " + file.name });
        return;
      }
      
      // Check image dimensions
      var img = new Image();
      img.onload = function() {
        URL.revokeObjectURL(img.src);
        
        if (img.width < 200 || img.height < 200) {
          resolve({ ok: false, error: "Image too small (min 200x200): " + file.name });
          return;
        }
        if (img.width > 4096 || img.height > 4096) {
          resolve({ ok: false, error: "Image too large (max 4096x4096): " + file.name });
          return;
        }
        
        resolve({ ok: true, width: img.width, height: img.height });
      };
      img.onerror = function() {
        URL.revokeObjectURL(img.src);
        resolve({ ok: false, error: "Cannot read image: " + file.name });
      };
      img.src = URL.createObjectURL(file);
    });
  }

  async function enrollUploadAuto() {
    if (busy) return;

    if (!file || !file.files || !file.files[0]) {
      setStatus(upStatus, "choose an image first.", "warning");
      return;
    }

    busy = true;

    try {
      setStatus(upStatus, "verifying...", "info");
      
      // CLIENT-SIDE PRE-CHECK: Validate all files before uploading
      var imgs = Array.prototype.slice.call(file.files || [], 0, ENROLL_MAX_IMAGES);
      
      for (var i = 0; i < imgs.length; i++) {
        var check = await precheckImage(imgs[i]);
        if (!check.ok) {
          setStatus(upStatus, check.error, "danger");
          busy = false;
          return;
        }
      }
      
      // All files passed pre-check
      
      // PREVIEW BEFORE UPLOAD: Show confirmation modal
      var uploadConfirmed = await showPreviewModal(imgs);
      if (!uploadConfirmed) {
        // User clicked "Retake" - clear file input and return
        if (file) file.value = '';
        busy = false;
        setStatus(upStatus, "choose new image(s).", "info");
        return;
      }
      
      var fd = new FormData();
      fd.append("__RequestVerificationToken", token());
      fd.append("employeeId", empId);

      imgs.forEach(function (f, i) {
        fd.append("image", f, f.name || ("upload_" + (i + 1) + ".jpg"));
      });

      // OPTIMIZED: Compress before upload
      setStatus(upStatus, "compressing " + imgs.length + " image(s)...", "info");
      
      var compressedImgs = [];
      for (var j = 0; j < imgs.length; j++) {
        try {
          var compressed = await compressImageFile(imgs[j], 480, 360, UPLOAD_QUALITY);
          compressedImgs.push(compressed);
        } catch (e) {
          compressedImgs.push(imgs[j]); // Fallback
        }
      }
      
      var originalSize = imgs.reduce(function(sum, f) { return sum + f.size; }, 0);
      var compressedSize = compressedImgs.reduce(function(sum, b) { return sum + b.size; }, 0);
      var savings = ((originalSize - compressedSize) / originalSize * 100).toFixed(0);
      
      // Build form with compressed images
      var fd = new FormData();
      fd.append("__RequestVerificationToken", token());
      fd.append("employeeId", empId);
      
      compressedImgs.forEach(function(blob, i) {
        fd.append("image", blob, imgs[i].name || ("upload_" + (i + 1) + ".jpg"));
      });
      
      setStatus(upStatus, "uploading " + compressedImgs.length + " image(s)... (" + savings + "% smaller)", "info");
      
      var startTime = Date.now();
      var res = await fetch(enrollUrl, { method: "POST", body: fd });
      var r = await res.json();
      var elapsed = Date.now() - startTime;

      if (r && r.ok === true) {
        var vecCount = (typeof r.savedVectors === "number") ? r.savedVectors : imgs.length;
        setStatus(upStatus, "enrollment saved successfully! [" + vecCount + " vector(s)]", "success");
        
        // SUCCESS: Show SweetAlert and redirect
        showSuccessMessage(vecCount);
      } else {
        r.timeMs = elapsed;
        var errorMsg = describeEnrollError(r);
        setStatus(upStatus, errorMsg, "danger");
        showErrorMessage(errorMsg);
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

  // PREVIEW BEFORE SAVE functionality
  // Shows captured images for confirmation before enrolling
  // -------------------------------------------------------------------------
  var previewModal = document.getElementById('previewModal');
  var previewImages = document.getElementById('previewImages');
  var previewFaceCount = document.getElementById('previewFaceCount');
  var previewConfirmBtn = document.getElementById('previewConfirmBtn');
  var previewRetakeBtn = document.getElementById('previewRetakeBtn');
  
  // SWEETALERT PREVIEW MODAL
  async function showPreviewModal(blobs) {
    var imageHtml = '';
    
    // Create image previews
    for (var i = 0; i < blobs.length; i++) {
      var url = URL.createObjectURL(blobs[i]);
      imageHtml += '<img src="' + url + '" style="width:80px;height:80px;object-fit:cover;border-radius:8px;margin:5px;" />';
    }
    
    var result = await Swal.fire({
      title: 'Confirm Enrollment',
      html: 
        '<div style="margin-bottom:15px;">' +
        '<p><strong>' + blobs.length + '</strong> face sample(s) captured</p>' +
        '<div style="display:flex;flex-wrap:wrap;justify-content:center;">' + imageHtml + '</div>' +
        '</div>',
      showCancelButton: true,
      confirmButtonText: '<i class="fa-solid fa-check me-2"></i>Enroll Now',
      cancelButtonText: '<i class="fa-solid fa-rotate-left me-2"></i>Retake',
      confirmButtonColor: '#28a745',
      cancelButtonColor: '#6c757d',
      allowOutsideClick: false
    });
    
    return result.isConfirmed;
  }
  
  // SWEETALERT SUCCESS MESSAGE
  function showSuccessMessage(vectorCount) {
    Swal.fire({
      icon: 'success',
      title: 'Enrollment Complete!',
      html: 
        '<p>Face biometric successfully enrolled.</p>' +
        '<p class="text-muted"><strong>' + vectorCount + '</strong> face sample(s) saved</p>',
      confirmButtonText: 'Back to Employee List',
      confirmButtonColor: '#28a745',
      allowOutsideClick: false
    }).then(function(result) {
      if (result.isConfirmed) {
        var redirectUrl = root.getAttribute("data-redirect-url") || "/Admin/Employees";
        window.location.href = redirectUrl;
      }
    });
  }
  
  // SWEETALERT ERROR MESSAGE
  function showErrorMessage(errorText) {
    Swal.fire({
      icon: 'error',
      title: 'Enrollment Failed',
      text: errorText,
      confirmButtonText: 'Try Again',
      confirmButtonColor: '#dc3545'
    });
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
