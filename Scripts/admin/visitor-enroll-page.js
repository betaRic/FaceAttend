/*
 * Scripts/admin/visitor-enroll-page.js
 * Enrollment page controller for Areas/Admin/Views/Visitors/Enroll.cshtml.
 * Reads server-side values from #visitorEnrollConfig data attributes.
 */
(function () {
    'use strict';

    var cfg       = document.getElementById('visitorEnrollConfig');
    var visitorId = cfg ? cfg.dataset.visitorId  : '';
    var scanUrl   = cfg ? cfg.dataset.scanUrl    : '';
    var enrollUrl = cfg ? cfg.dataset.enrollUrl  : '';
    var uploadUrl = cfg ? cfg.dataset.uploadUrl  : '';

    var MIN_FRAMES = 5;
    var MAX_FRAMES = 10;

    var capturedFrames = [];
    var selectedFiles  = [];

    // ── Guard ─────────────────────────────────────────────────────────────────

    if (typeof FaceAttend === 'undefined' || typeof FaceAttend.Enrollment === 'undefined') {
        console.error('[Visitors/Enroll] FaceAttend.Enrollment not available. Check bundle order.');
    }

    // ── Live enrollment instance ──────────────────────────────────────────────

    var liveEnrollment = (typeof FaceAttend !== 'undefined' && FaceAttend.Enrollment)
        ? FaceAttend.Enrollment.create({
            empId:         visitorId,
            scanUrl:       scanUrl,
            enrollUrl:     enrollUrl,
            redirectUrl:   '',
            minGoodFrames: MIN_FRAMES,
            maxKeepFrames: MAX_FRAMES,
            enablePreview: false
          })
        : null;

    // Sync antiSpoof + sharpness + face-area thresholds from server config.
    if (liveEnrollment) liveEnrollment.loadServerConfig('/api/enrollment/config');

    // Allow enrollment-tracker.js to publish livePose to this instance
    window.FaceAttendEnrollment = liveEnrollment;

    // ── Wizard / pane navigation ──────────────────────────────────────────────

    function showPane(active) {
        ['methodPane', 'livePane', 'uploadPane', 'successPane'].forEach(function (id) {
            var el = document.getElementById(id);
            if (el) el.classList.toggle('enroll-hidden', id !== active);
        });
    }

    function updateWizard(step) {
        document.querySelectorAll('.enroll-wizard-step').forEach(function (el, idx) {
            var n = idx + 1;
            el.classList.remove('is-active', 'is-complete');
            var numEl = el.querySelector('.enroll-wizard-step__num');
            if (n === step) {
                el.classList.add('is-active');
                if (numEl) numEl.textContent = n;
            } else if (n < step) {
                el.classList.add('is-complete');
                if (numEl) numEl.innerHTML = '<i class="fa-solid fa-check"></i>';
            } else {
                if (numEl) numEl.textContent = n;
            }
        });
    }

    window.showMethod = function () {
        stopCamera();
        showPane('methodPane');
        updateWizard(1);
    };

    window.showLive = function () {
        showPane('livePane');
        updateWizard(2);
        startCamera();
    };

    window.showUpload = function () {
        stopCamera();
        showPane('uploadPane');
        updateWizard(2);
    };

    function showSuccess() {
        stopCamera();
        showPane('successPane');
        updateWizard(3);
    }

    // ── Live capture UI helpers ───────────────────────────────────────────────

    function updateLiveCaptureUI(frameCount, target) {
        var countEl    = document.getElementById('captureCount');
        var progressEl = document.getElementById('captureProgress');
        var saveBtn    = document.getElementById('saveBtn');

        if (countEl)    countEl.textContent = frameCount;
        if (progressEl) progressEl.style.width = Math.min(100, Math.round((frameCount / (target || MIN_FRAMES)) * 100)) + '%';
        if (saveBtn)    saveBtn.disabled = frameCount < MIN_FRAMES;
    }

    // ── Enrollment callbacks ──────────────────────────────────────────────────

    if (liveEnrollment) {
        liveEnrollment.callbacks.onStatus = function (msg) {
            var el = document.getElementById('cameraStatusText');
            if (el) el.textContent = msg || '';
        };

        liveEnrollment.callbacks.onAntiSpoofUpdate = function (pct) {
            var bar = document.getElementById('antiSpoofBar');
            var txt = document.getElementById('antiSpoofText');
            if (bar) bar.style.width = pct + '%';
            if (txt) txt.textContent = pct + '%';
        };

        liveEnrollment.callbacks.onCaptureProgress = function (current, target) {
            capturedFrames = liveEnrollment.goodFrames.slice();
            updateLiveCaptureUI(current, target);
        };

        liveEnrollment.callbacks.onReadyToConfirm = function (data) {
            Promise.all(
                (data.frames || []).slice(0, 4).map(function (frame) {
                    return new Promise(function (resolve) {
                        if (!frame || !frame.blob) { resolve(null); return; }
                        var reader = new FileReader();
                        reader.onload  = function (e) { resolve(e.target.result); };
                        reader.onerror = function ()  { resolve(null); };
                        reader.readAsDataURL(frame.blob);
                    });
                })
            ).then(function (dataUrls) {
                var thumbHtml = dataUrls.filter(Boolean).map(function (url) {
                    return '<img src="' + url + '" style="width:80px;height:80px;object-fit:cover;' +
                           'border-radius:8px;border:2px solid rgba(255,255,255,0.25);margin:4px;" />';
                }).join('');

                var summaryHtml =
                    '<div style="display:flex;justify-content:center;gap:8px;margin-bottom:14px;flex-wrap:wrap;">' +
                        thumbHtml +
                    '</div>' +
                    '<div style="background:rgba(255,255,255,0.05);border-radius:10px;padding:12px 16px;' +
                         'text-align:left;font-size:0.875rem;line-height:2;">' +
                        '<div><i class="fa-solid fa-layer-group" style="margin-right:8px;color:#3b82f6;"></i>' +
                            '<strong>' + data.frameCount + '</strong> frames captured</div>' +
                        '<div><i class="fa-solid fa-shield-heart" style="margin-right:8px;color:#22c55e;"></i>' +
                            'Best anti-spoof: <strong>' + data.bestAntiSpoof + '%</strong></div>' +
                    '</div>';

                Swal.fire({
                    title:             'Ready to Enroll',
                    html:              summaryHtml,
                    icon:              'success',
                    showCancelButton:  true,
                    confirmButtonText: '<i class="fa-solid fa-check me-1"></i>Confirm Enrollment',
                    cancelButtonText:  '<i class="fa-solid fa-rotate-left me-1"></i>Retake',
                    confirmButtonColor: '#22c55e',
                    cancelButtonColor:  '#475569',
                    background:        '#0f172a',
                    color:             '#f8fafc',
                    allowOutsideClick: false,
                    allowEscapeKey:    false
                }).then(function (result) {
                    if (result.isConfirmed) {
                        var saveBtn = document.getElementById('saveBtn');
                        if (saveBtn) {
                            saveBtn.disabled = true;
                            saveBtn.innerHTML = '<i class="fa-solid fa-circle-notch fa-spin me-2"></i>Saving...';
                        }
                        liveEnrollment.performEnrollment();
                    } else {
                        liveEnrollment.startAutoEnrollment();
                        updateLiveCaptureUI(0, MIN_FRAMES);
                        var saveBtn = document.getElementById('saveBtn');
                        if (saveBtn) {
                            saveBtn.disabled = true;
                            saveBtn.innerHTML = '<i class="fa-solid fa-check me-2"></i>Save Enrollment';
                        }
                        var statusEl = document.getElementById('cameraStatusText');
                        if (statusEl) statusEl.textContent = 'Retaking — look straight ahead';
                    }
                });
            });
        };

        liveEnrollment.callbacks.onEnrollmentComplete = function () {
            showSuccess();
        };

        liveEnrollment.callbacks.onEnrollmentError = function (result) {
            var saveBtn = document.getElementById('saveBtn');
            if (saveBtn) {
                saveBtn.disabled = false;
                saveBtn.innerHTML = '<i class="fa-solid fa-check me-2"></i>Save Enrollment';
            }
            var msg = typeof liveEnrollment.describeEnrollError === 'function'
                ? liveEnrollment.describeEnrollError(result)
                : ((result && (result.message || result.error)) || 'Enrollment failed.');
            Swal.fire({
                icon:              'error',
                title:             'Enrollment Failed',
                html:              '<div style="font-size:.9rem;text-align:left;">' + msg + '</div>',
                confirmButtonText: 'Try Again',
                confirmButtonColor: '#ef4444',
                background:        '#0f172a',
                color:             '#f8fafc'
            });
        };
    }

    // ── Camera lifecycle ──────────────────────────────────────────────────────
    // Note: enrollment-tracker.js owns enrollFaceCanvas. It draws the oval
    // guide (FaceGuide) using real-time MediaPipe detection. No separate
    // canvas overlay needed here — enrollment-tracker handles it.

    function startCamera() {
        if (!liveEnrollment) {
            console.error('[Visitors/Enroll] liveEnrollment not initialized.');
            return;
        }
        var videoEl = document.getElementById('enrollVideo');
        if (!videoEl) {
            console.error('[Visitors/Enroll] #enrollVideo element not found.');
            return;
        }

        updateLiveCaptureUI(0, MIN_FRAMES);
        capturedFrames = [];

        var statusEl = document.getElementById('cameraStatusText');

        liveEnrollment.startCamera(videoEl)
            .then(function () {
                if (statusEl) statusEl.textContent = 'Camera active — look straight ahead';
                liveEnrollment.startAutoEnrollment();
            })
            .catch(function (e) {
                var msg = (e && e.message) || 'Could not access camera.';
                if (statusEl) statusEl.textContent = 'Camera error: ' + msg;
                Swal.fire({
                    icon:    'error',
                    title:   'Camera Error',
                    text:    msg,
                    background: '#0f172a',
                    color:   '#f8fafc'
                });
            });
    }

    function stopCamera() {
        if (liveEnrollment) liveEnrollment.stopCamera();
        capturedFrames = [];
    }

    window.retake = function () {
        if (!liveEnrollment) return;
        liveEnrollment.startAutoEnrollment();
        updateLiveCaptureUI(0, MIN_FRAMES);
        capturedFrames = [];
        var saveBtn = document.getElementById('saveBtn');
        if (saveBtn) {
            saveBtn.disabled = true;
            saveBtn.innerHTML = '<i class="fa-solid fa-check me-2"></i>Save Enrollment';
        }
        var statusEl = document.getElementById('cameraStatusText');
        if (statusEl) statusEl.textContent = 'Retaking — look straight ahead';
    };

    window.saveEnrollment = function () {
        if (!liveEnrollment) return;
        if (!liveEnrollment.goodFrames || liveEnrollment.goodFrames.length < MIN_FRAMES) {
            Swal.fire({
                icon:       'warning',
                title:      'Not Ready',
                text:       'Please wait for at least ' + MIN_FRAMES + ' frames to be captured.',
                background: '#0f172a',
                color:      '#f8fafc'
            });
            return;
        }
        liveEnrollment.stopAutoEnrollment();
        liveEnrollment.performEnrollment();
    };

    window.addEventListener('beforeunload', stopCamera);

    // ── Upload pane ───────────────────────────────────────────────────────────

    var dropzone  = document.getElementById('dropzone');
    var fileInput = document.getElementById('fileInput');

    if (dropzone) {
        dropzone.addEventListener('click', function () { if (fileInput) fileInput.click(); });
        dropzone.addEventListener('dragover', function (e) {
            e.preventDefault();
            this.classList.add('is-dragover');
        });
        dropzone.addEventListener('dragleave', function () {
            this.classList.remove('is-dragover');
        });
        dropzone.addEventListener('drop', function (e) {
            e.preventDefault();
            this.classList.remove('is-dragover');
            handleFiles(e.dataTransfer.files);
        });
    }

    if (fileInput) {
        fileInput.addEventListener('change', function () { handleFiles(this.files); });
    }

    function handleFiles(files) {
        Array.from(files).forEach(function (file) {
            if (selectedFiles.length >= 5) return;
            if (!file.type.startsWith('image/')) return;
            selectedFiles.push(file);
            addFileCard(file, selectedFiles.length - 1);
        });
        var uploadBtn = document.getElementById('uploadBtn');
        if (uploadBtn) uploadBtn.disabled = selectedFiles.length === 0;
    }

    function addFileCard(file, index) {
        var fileGrid = document.getElementById('fileGrid');
        if (!fileGrid) return;
        var card = document.createElement('div');
        card.className = 'enroll-file-card';
        card.innerHTML =
            '<div class="enroll-file-card__thumb"><img id="preview-' + index + '"></div>' +
            '<div class="enroll-file-card__info">' +
                '<div class="enroll-file-card__name">' + file.name + '</div>' +
                '<div class="enroll-file-card__size">' + formatSize(file.size) + '</div>' +
            '</div>' +
            '<button type="button" class="enroll-file-card__remove" onclick="removeFile(' + index + ')">' +
                '<i class="fa-solid fa-times"></i>' +
            '</button>';
        fileGrid.appendChild(card);

        var reader = new FileReader();
        reader.onload = function (e) {
            var img = document.getElementById('preview-' + index);
            if (img) img.src = e.target.result;
        };
        reader.readAsDataURL(file);
    }

    window.removeFile = function (index) {
        selectedFiles.splice(index, 1);
        var fileGrid = document.getElementById('fileGrid');
        if (fileGrid) fileGrid.innerHTML = '';
        selectedFiles.forEach(function (file, i) { addFileCard(file, i); });
        var uploadBtn = document.getElementById('uploadBtn');
        if (uploadBtn) uploadBtn.disabled = selectedFiles.length === 0;
    };

    function formatSize(bytes) {
        if (bytes === 0) return '0 B';
        var k = 1024, sizes = ['B', 'KB', 'MB'];
        var i = Math.floor(Math.log(bytes) / Math.log(k));
        return parseFloat((bytes / Math.pow(k, i)).toFixed(1)) + ' ' + sizes[i];
    }

    // Visitor upload pane POSTs to /Admin/Visitors/EnrollFace.
    // NOTE: The EnrollmentController.Enroll endpoint (/api/enrollment/enroll)
    // only queries db.Employees and will return EMPLOYEE_NOT_FOUND for visitor IDs.
    // A separate VisitorsController.EnrollFace action is required for upload enrollment.
    window.processUpload = function () {
        var btn = document.getElementById('uploadBtn');

        if (!selectedFiles || selectedFiles.length === 0) {
            Swal.fire({
                icon: 'warning',
                title: 'No Files Selected',
                text: 'Please select at least one photo before uploading.',
                confirmButtonColor: '#3b82f6'
            });
            return;
        }

        if (typeof FaceAttend === 'undefined' || typeof FaceAttend.Enrollment === 'undefined') {
            console.error('[processUpload] FaceAttend.Enrollment not available.');
            return;
        }

        var originalHtml = btn ? btn.innerHTML : '';
        if (btn) {
            btn.disabled = true;
            btn.innerHTML = '<i class="fa-solid fa-circle-notch fa-spin me-2"></i>Uploading...';
        }

        var uploadEnrollment = FaceAttend.Enrollment.create({
            empId:         visitorId,
            enrollUrl:     uploadUrl,
            enablePreview: false
        });

        uploadEnrollment.callbacks.onEnrollmentComplete = function (vecCount) {
            if (btn) { btn.disabled = false; btn.innerHTML = originalHtml; }
            Swal.fire({
                icon:              'success',
                title:             'Enrollment Complete!',
                html:              '<b>' + vecCount + '</b> face sample' + (vecCount !== 1 ? 's' : '') + ' saved successfully.',
                timer:             1800,
                timerProgressBar:  true,
                showConfirmButton: false
            }).then(function () { showSuccess(); });
        };

        uploadEnrollment.callbacks.onEnrollmentError = function (result) {
            if (btn) { btn.disabled = false; btn.innerHTML = originalHtml; }
            var msg = typeof uploadEnrollment.describeEnrollError === 'function'
                ? uploadEnrollment.describeEnrollError(result)
                : ((result && (result.message || result.error)) || 'Upload failed.');
            Swal.fire({
                icon:              'error',
                title:             'Enrollment Failed',
                html:              msg,
                confirmButtonText: 'Try Again',
                confirmButtonColor: '#ef4444'
            });
        };

        uploadEnrollment.enrollFromFiles(selectedFiles, { maxImages: 5 })
            .catch(function (e) {
                if (btn) { btn.disabled = false; btn.innerHTML = originalHtml; }
                Swal.fire({
                    icon:              'error',
                    title:             'Upload Error',
                    html:              e && e.message ? e.message : 'Upload failed.',
                    confirmButtonText: 'OK',
                    confirmButtonColor: '#ef4444'
                });
            });
    };

}());
