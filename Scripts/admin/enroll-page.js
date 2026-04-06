/*
 * Scripts/admin/enroll-page.js
 * Enrollment page controller for Areas/Admin/Views/Employees/Enroll.cshtml.
 * Reads server-side values from #enrollPageConfig data attributes.
 */
(function () {
    'use strict';

    var cfg     = document.getElementById('enrollPageConfig');
    var empId   = cfg ? cfg.dataset.employeeId : '';
    var scanUrl  = cfg ? cfg.dataset.scanUrl    : '';
    var enrollUrl = cfg ? cfg.dataset.enrollUrl  : '';

    var MIN_FRAMES = 5;

    // ── State ────────────────────────────────────────────────────────────
    var selectedFiles  = [];
    var _cameraStarted = false;

    // ── Single enrollment instance ────────────────────────────────────────
    if (typeof FaceAttend === 'undefined' || typeof FaceAttend.Enrollment === 'undefined') {
        console.error('[Employees/Enroll] FaceAttend.Enrollment not available. Check bundle order.');
    }

    var enroll = (typeof FaceAttend !== 'undefined' && FaceAttend.Enrollment)
        ? FaceAttend.Enrollment.create({
            empId:         empId,
            scanUrl:       scanUrl,
            enrollUrl:     enrollUrl,
            minGoodFrames: MIN_FRAMES,
            maxKeepFrames: MIN_FRAMES,
            enablePreview: false
          })
        : null;

    // Sync liveness + sharpness + face-area thresholds from server config so client
    // and server gates always match, even after Web.config edits.
    if (enroll) enroll.loadServerConfig('/api/enrollment/config');

    // Allow enrollment-tracker.js to publish livePose to this instance
    window.FaceAttendEnrollment = enroll;

    // ── Wizard navigation ─────────────────────────────────────────────────

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

    function showPane(active) {
        ['methodPane', 'livePane', 'uploadPane', 'successPane'].forEach(function (id) {
            var el = document.getElementById(id);
            if (el) el.classList.toggle('enroll-hidden', id !== active);
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

    // ── Live capture UI helpers ───────────────────────────────────────────

    function resetCaptureUI() {
        var countEl    = document.getElementById('captureCount');
        var progressEl = document.getElementById('captureProgress');
        var saveBtn    = document.getElementById('saveBtn');
        var livenessBar = document.getElementById('livenessBar');
        var livenessTxt = document.getElementById('livenessText');

        if (countEl)    countEl.textContent = '0';
        if (progressEl) progressEl.style.width = '0%';
        if (saveBtn) {
            saveBtn.disabled = true;
            saveBtn.innerHTML = '<i class="fa-solid fa-check me-2"></i>Save Enrollment';
        }
        if (livenessBar) livenessBar.style.width = '0%';
        if (livenessTxt) livenessTxt.textContent = '0%';
    }

    function updateCaptureUI(frameCount, target) {
        var countEl    = document.getElementById('captureCount');
        var progressEl = document.getElementById('captureProgress');
        var saveBtn    = document.getElementById('saveBtn');

        if (countEl)    countEl.textContent = frameCount;
        if (progressEl) progressEl.style.width = Math.min(100, Math.round((frameCount / (target || MIN_FRAMES)) * 100)) + '%';
        if (saveBtn)    saveBtn.disabled = frameCount < MIN_FRAMES;
    }

    // ── Enrollment callbacks ──────────────────────────────────────────────

    if (enroll) {
        enroll.callbacks.onStatus = function (msg) {
            var el = document.getElementById('cameraStatusText');
            if (el) el.textContent = msg || '';
        };

        enroll.callbacks.onLivenessUpdate = function (pct) {
            var bar = document.getElementById('livenessBar');
            var txt = document.getElementById('livenessText');
            if (bar) bar.style.width = pct + '%';
            if (txt) txt.textContent = pct + '%';
        };

        enroll.callbacks.onCaptureProgress = function (current, target) {
            updateCaptureUI(current, target);
        };

        enroll.callbacks.onReadyToConfirm = function (data) {
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
                var thumbHtml = dataUrls
                    .filter(Boolean)
                    .map(function (url) {
                        return '<img src="' + url + '" style="width:80px;height:80px;object-fit:cover;' +
                               'border-radius:8px;border:2px solid rgba(255,255,255,0.25);margin:4px;" />';
                    })
                    .join('');

                var summaryHtml =
                    '<div style="display:flex;justify-content:center;gap:8px;margin-bottom:14px;flex-wrap:wrap;">' +
                        thumbHtml +
                    '</div>' +
                    '<div style="background:rgba(255,255,255,0.05);border-radius:10px;padding:12px 16px;' +
                         'text-align:left;font-size:0.875rem;line-height:2;">' +
                        '<div><i class="fa-solid fa-layer-group" style="margin-right:8px;color:#3b82f6;"></i>' +
                            '<strong>' + data.frameCount + '</strong> frames captured</div>' +
                        '<div><i class="fa-solid fa-shield-heart" style="margin-right:8px;color:#22c55e;"></i>' +
                            'Best liveness: <strong>' + data.bestLiveness + '%</strong></div>' +
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
                        enroll.performEnrollment();
                    } else {
                        doRetake();
                    }
                });
            });
        };

        enroll.callbacks.onEnrollmentComplete = function () {
            showSuccess();
        };

        enroll.callbacks.onEnrollmentError = function (result) {
            var saveBtn = document.getElementById('saveBtn');
            if (saveBtn) {
                saveBtn.disabled = false;
                saveBtn.innerHTML = '<i class="fa-solid fa-check me-2"></i>Save Enrollment';
            }
            var msg = typeof enroll.describeEnrollError === 'function'
                ? enroll.describeEnrollError(result)
                : ((result && (result.message || result.error)) || 'Enrollment failed.');
            Swal.fire({
                icon:               'error',
                title:              'Enrollment Failed',
                html:               '<div style="font-size:.9rem;text-align:left;">' + msg + '</div>',
                confirmButtonText:  'Try Again',
                confirmButtonColor: '#ef4444',
                background:        '#0f172a',
                color:             '#f8fafc'
            });
        };
    }

    // ── Camera lifecycle ──────────────────────────────────────────────────
    // Note: enrollment-tracker.js owns enrollFaceCanvas. It draws the oval
    // guide (FaceGuide) using real-time MediaPipe detection. No separate
    // canvas overlay needed here — enrollment-tracker handles it.

    function startCamera() {
        if (_cameraStarted) return;
        _cameraStarted = true;

        if (!enroll) {
            console.error('[Employees/Enroll] enroll instance not initialized.');
            return;
        }
        var videoEl = document.getElementById('enrollVideo');
        if (!videoEl) {
            console.error('[Employees/Enroll] #enrollVideo not found.');
            return;
        }

        resetCaptureUI();
        var statusEl = document.getElementById('cameraStatusText');

        enroll.startCamera(videoEl)
            .then(function () {
                if (statusEl) statusEl.textContent = 'Camera active, look straight ahead';
                setTimeout(function () { enroll.startAutoEnrollment(); }, 1500);
            })
            .catch(function (e) {
                _cameraStarted = false;
                var msg = (e && e.message) || 'Could not access camera.';
                if (statusEl) statusEl.textContent = 'Camera error: ' + msg;
                Swal.fire({
                    icon:      'error',
                    title:     'Camera Error',
                    text:      msg,
                    background: '#0f172a',
                    color:     '#f8fafc'
                });
            });
    }

    function stopCamera() {
        _cameraStarted = false;
        if (enroll) enroll.stopCamera();
    }

    function doRetake() {
        if (!enroll) return;
        enroll.startAutoEnrollment();
        resetCaptureUI();
    }

    window.retake = function () {
        doRetake();
    };

    window.saveEnrollment = function () {
        if (!enroll) return;
        if (!enroll.goodFrames || enroll.goodFrames.length < MIN_FRAMES) {
            Swal.fire({
                icon:       'warning',
                title:      'Not Ready',
                text:       'Please wait for at least ' + MIN_FRAMES + ' frames to be captured.',
                background: '#0f172a',
                color:      '#f8fafc'
            });
            return;
        }
        enroll.stopAutoEnrollment();
        enroll.performEnrollment();
    };

    window.addEventListener('beforeunload', stopCamera);

    // ── Upload pane ───────────────────────────────────────────────────────

    var dropzone  = document.getElementById('dropzone');
    var fileInput = document.getElementById('fileInput');

    if (dropzone) {
        dropzone.addEventListener('click', function () { fileInput.click(); });
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
            renderFileCard(file, selectedFiles.length - 1);
        });
        var uploadBtn = document.getElementById('uploadBtn');
        if (uploadBtn) uploadBtn.disabled = selectedFiles.length === 0;
    }

    function renderFileCard(file, index) {
        var fileGrid = document.getElementById('fileGrid');
        if (!fileGrid) return;
        var card = document.createElement('div');
        card.className = 'enroll-file-card';
        card.dataset.index = index;
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
        selectedFiles.forEach(function (file, i) { renderFileCard(file, i); });
        var uploadBtn = document.getElementById('uploadBtn');
        if (uploadBtn) uploadBtn.disabled = selectedFiles.length === 0;
    };

    function formatSize(bytes) {
        if (bytes === 0) return '0 B';
        var k = 1024, sizes = ['B', 'KB', 'MB'];
        var i = Math.floor(Math.log(bytes) / Math.log(k));
        return parseFloat((bytes / Math.pow(k, i)).toFixed(1)) + ' ' + sizes[i];
    }

    window.processUpload = function () {
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

        var btn = document.getElementById('uploadBtn');
        var originalHtml = btn.innerHTML;
        btn.disabled = true;
        btn.innerHTML = '<i class="fa-solid fa-circle-notch fa-spin me-2"></i>Uploading...';

        var uploadEnroll = FaceAttend.Enrollment.create({
            empId:         empId,
            enrollUrl:     enrollUrl,
            enablePreview: false
        });

        uploadEnroll.callbacks.onEnrollmentComplete = function () {
            btn.disabled = false;
            btn.innerHTML = originalHtml;
            showSuccess();
        };

        uploadEnroll.callbacks.onEnrollmentError = function (result) {
            btn.disabled = false;
            btn.innerHTML = originalHtml;
            var msg = typeof uploadEnroll.describeEnrollError === 'function'
                ? uploadEnroll.describeEnrollError(result)
                : ((result && (result.message || result.error)) || 'Upload failed.');
            Swal.fire({
                icon:               'error',
                title:              'Enrollment Failed',
                html:               msg,
                confirmButtonText:  'Try Again',
                confirmButtonColor: '#ef4444'
            });
        };

        uploadEnroll.enrollFromFiles(selectedFiles, { maxImages: 5 })
            .catch(function (e) {
                btn.disabled = false;
                btn.innerHTML = originalHtml;
                Swal.fire({
                    icon:               'error',
                    title:              'Upload Error',
                    html:               e && e.message ? e.message : 'Upload failed.',
                    confirmButtonText:  'OK',
                    confirmButtonColor: '#ef4444'
                });
            });
    };

    // Portrait orientation lock
    if (screen.orientation && screen.orientation.lock) {
        screen.orientation.lock('portrait-primary').catch(function () {});
    }

})();
