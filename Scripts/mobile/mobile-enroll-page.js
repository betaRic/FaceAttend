/*
 * Scripts/mobile/mobile-enroll-page.js
 * Enrollment page controller for Views/MobileRegistration/Enroll-mobile.cshtml.
 */
(function () {
    'use strict';

    var MIN_FRAMES = 5;

    var state = {
        step:           1,
        capturedFrames: [],
        valid: {
            employeeId: false, firstName:  false, lastName:   false,
            middleName: true,  position:   false, department: false,
            officeId:   false, deviceName: false
        }
    };

    var _cameraStarted = false;

    function el(id) { return document.getElementById(id); }

    var els = {
        employeeId:       el('employeeId'),
        firstName:        el('firstName'),
        lastName:         el('lastName'),
        middleName:       el('middleName'),
        position:         el('position'),
        department:       el('department'),
        officeId:         el('officeId'),
        deviceName:       el('deviceName'),
        btnToCapture:     el('btnToCapture'),
        btnBackToDetails: el('btnBackToDetails'),
        btnBackToCapture: el('btnBackToCapture'),
        submitBtn:        el('submitBtn')
    };

    if (typeof FaceAttend === 'undefined' || !FaceAttend.Enrollment) {
        console.error('[MobileEnroll] FaceAttend.Enrollment not available. Check bundle order.');
    }

    var enroll = (typeof FaceAttend !== 'undefined' && FaceAttend.Enrollment)
        ? FaceAttend.Enrollment.create({
            empId:             '',
            scanUrl:           '/MobileRegistration/ScanFrame',
            enrollUrl:         '/api/enrollment/enroll',
            minGoodFrames:     MIN_FRAMES,
            maxKeepFrames:     8,
            perFrameThreshold: 0.75,
            enablePreview:     false
          })
        : null;

    // Allow enrollment-tracker.js to publish livePose to this instance
    window.FaceAttendEnrollment = enroll;

    function updateCaptureUI(frameCount, target) {
        var c = el('captureCount');
        if (c) c.textContent = frameCount;
        var p = el('captureProgress');
        if (p) p.style.width = Math.min(100, Math.round((frameCount / (target || MIN_FRAMES)) * 100)) + '%';
    }

    function resetCaptureUI() {
        updateCaptureUI(0, MIN_FRAMES);
        var bar = el('livenessBar');  if (bar) bar.style.width = '0%';
        var txt = el('livenessText'); if (txt) txt.textContent = '0%';
    }

    // Note: enrollment-tracker.js owns enrollFaceCanvas. It draws the oval
    // guide (FaceGuide) using real-time MediaPipe detection. No separate
    // canvas overlay needed here — enrollment-tracker handles it.

    function startCamera() {
        if (_cameraStarted) return;
        _cameraStarted = true;
        if (!enroll) return;
        var videoEl = el('enrollVideo');
        if (!videoEl) return;
        resetCaptureUI();
        var statusEl = el('cameraStatusText');
        enroll.startCamera(videoEl)
            .then(function () {
                if (statusEl) statusEl.textContent = 'Camera active \u2014 look straight ahead';
                enroll.startAutoEnrollment();
            })
            .catch(function (e) {
                _cameraStarted = false;
                var msg = (e && e.message) || 'Could not access camera.';
                if (statusEl) statusEl.textContent = 'Camera error: ' + msg;
                if (typeof Swal !== 'undefined') {
                    Swal.fire({ icon: 'error', title: 'Camera Error', text: msg,
                        background: '#0f172a', color: '#f8fafc' });
                }
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

    if (enroll) {
        enroll.callbacks.onStatus = function (msg) {
            var e = el('cameraStatusText'); if (e) e.textContent = msg || '';
        };
        enroll.callbacks.onLivenessUpdate = function (pct) {
            var bar = el('livenessBar');  if (bar) bar.style.width = pct + '%';
            var txt = el('livenessText'); if (txt) txt.textContent = pct + '%';
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
                var thumbHtml = dataUrls.filter(Boolean).map(function (url) {
                    return '<img src="' + url + '" style="width:80px;height:80px;object-fit:cover;' +
                           'border-radius:8px;border:2px solid rgba(255,255,255,0.15);flex-shrink:0;margin:4px;" />';
                }).join('');

                Swal.fire({
                    title: 'Enrollment Complete!',
                    html:
                        '<div style="display:flex;gap:8px;justify-content:center;flex-wrap:wrap;margin-bottom:12px;">' +
                        thumbHtml + '</div>' +
                        '<div style="font-size:.9rem;">' +
                        '<p>' + data.frameCount + ' frames captured</p>' +
                        '<p>Best quality: ' + data.bestLiveness + '%</p></div>',
                    showCancelButton:  true,
                    confirmButtonText: 'Continue to Review \u2192',
                    cancelButtonText:  'Retake Photos',
                    confirmButtonColor: '#22c55e', background: '#0f172a', color: '#f8fafc',
                    allowOutsideClick: false
                }).then(function (result) {
                    if (result.isConfirmed) {
                        state.capturedFrames = enroll.goodFrames.slice();
                        setStep(3);
                    } else {
                        doRetake();
                    }
                });
            });
        };

        enroll.callbacks.onEnrollmentError = function (result) {
            var msg = typeof enroll.describeEnrollError === 'function'
                ? enroll.describeEnrollError(result)
                : ((result && (result.message || result.error)) || 'Scan error, retrying...');
            var e = el('cameraStatusText'); if (e) e.textContent = msg;
        };
    }

    function setStep(step) {
        state.step = step;
        [1, 2, 3].forEach(function (n) {
            var pane   = el('step' + n);
            var stepEl = document.querySelector('.wizard-step[data-step="' + n + '"]');
            if (pane)   pane.classList.toggle('active', n === step);
            if (stepEl) {
                stepEl.classList.remove('active', 'done');
                if (n === step) stepEl.classList.add('active');
                if (n < step)   stepEl.classList.add('done');
            }
        });
        if (step === 2) startCamera(); else stopCamera();
        if (step === 3) populateReview();
        window.scrollTo(0, 0);
    }

    function populateReview() {
        var sel      = els.officeId;
        var officeTx = (sel && sel.selectedIndex >= 0) ? sel.options[sel.selectedIndex].text : '-';
        var samples  = state.capturedFrames ? state.capturedFrames.length : 0;
        var map = {
            reviewEmployeeId:         els.employeeId ? els.employeeId.value : '-',
            reviewFullName:           [
                els.firstName  ? els.firstName.value  : '',
                els.middleName ? els.middleName.value : '',
                els.lastName   ? els.lastName.value   : ''
            ].filter(Boolean).join(' ') || '-',
            reviewPositionDepartment: [
                els.position   ? els.position.value   : '',
                els.department ? els.department.value : ''
            ].filter(Boolean).join(' / ') || '-',
            reviewOffice:      officeTx,
            reviewDeviceName:  els.deviceName ? els.deviceName.value : '-',
            reviewSampleCount: samples + ' / 5'
        };
        Object.keys(map).forEach(function (id) {
            var e = el(id); if (e) e.textContent = map[id];
        });
    }

    var Rules = {
        employeeId : { min:5,  max:20,  pat:/^[A-Z0-9 \-]+$/,          label:'Employee ID' },
        firstName  : { min:2,  max:50,  pat:/^[A-Z \.\-']+$/,           label:'First Name'  },
        lastName   : { min:2,  max:50,  pat:/^[A-Z \.\-']+$/,           label:'Last Name'   },
        middleName : { min:0,  max:50,  pat:/^[A-Z \.\-']*$/,           label:'Middle Name', opt:true },
        position   : { min:2,  max:100, pat:/^[A-Z0-9 \.\-\/\(\),]+$/, label:'Position'    },
        department : { min:2,  max:100, pat:/^[A-Z0-9 \.\-\/\(\),]+$/, label:'Department'  },
        deviceName : { min:2,  max:50,  pat:/^[A-Z0-9 \._]+$/,         label:'Device Name' }
    };

    function validateField(inputEl, rules, msgId) {
        var val   = (inputEl ? inputEl.value : '').trim().toUpperCase();
        var msgEl = el(msgId);
        var ok    = false;
        if (!val && rules.opt) {
            ok = true;
        } else if (!val) {
            if (msgEl) msgEl.textContent = rules.label + ' is required';
        } else if (val.length < rules.min || val.length > rules.max) {
            if (msgEl) msgEl.textContent = rules.label + ' must be ' + rules.min + '\u2013' + rules.max + ' chars';
        } else if (!rules.pat.test(val)) {
            if (msgEl) msgEl.textContent = rules.label + ' contains invalid characters';
        } else {
            ok = true;
            if (msgEl) msgEl.textContent = '';
        }
        if (inputEl) {
            inputEl.classList.toggle('valid',   ok);
            inputEl.classList.toggle('invalid', !ok && !!val);
        }
        return ok;
    }

    function updateContinueBtn() {
        if (els.btnToCapture)
            els.btnToCapture.disabled = !Object.keys(state.valid).every(function (k) { return state.valid[k]; });
    }

    function wire(inputEl, ruleKey, msgId, stateKey) {
        if (!inputEl) return;
        inputEl.addEventListener('input', function () {
            state.valid[stateKey] = validateField(inputEl, Rules[ruleKey], msgId);
            updateContinueBtn();
        });
    }

    wire(els.employeeId, 'employeeId', 'employeeIdValidation', 'employeeId');
    wire(els.firstName,  'firstName',  'firstNameValidation',  'firstName');
    wire(els.lastName,   'lastName',   'lastNameValidation',   'lastName');
    wire(els.middleName, 'middleName', 'middleNameValidation', 'middleName');
    wire(els.position,   'position',   'positionValidation',   'position');
    wire(els.department, 'department', 'departmentValidation', 'department');
    wire(els.deviceName, 'deviceName', 'deviceNameValidation', 'deviceName');

    if (els.officeId) {
        els.officeId.addEventListener('change', function () {
            state.valid.officeId = !!els.officeId.value;
            updateContinueBtn();
        });
    }

    if (els.btnToCapture)     els.btnToCapture.addEventListener('click',     function () { setStep(2); });
    if (els.btnBackToDetails) els.btnBackToDetails.addEventListener('click', function () { setStep(1); });
    if (els.btnBackToCapture) els.btnBackToCapture.addEventListener('click', function () { setStep(2); });

    if (els.submitBtn) {
        els.submitBtn.addEventListener('click', function () {
            var encodings = [];
            (state.capturedFrames || []).forEach(function (f) {
                var enc = f.encoding || f.enc;
                if (enc) encodings.push(enc);
            });
            if (encodings.length === 0) {
                if (typeof Swal !== 'undefined') {
                    Swal.fire({ icon: 'error', title: 'No Face Data',
                        text: 'Please complete face capture before submitting.',
                        background: '#0f172a', color: '#f8fafc' });
                }
                return;
            }
            var tokenInput = document.querySelector('input[name="__RequestVerificationToken"]');
            var form = new FormData();
            form.append('EmployeeId',           els.employeeId ? els.employeeId.value.trim().toUpperCase() : '');
            form.append('FirstName',            els.firstName  ? els.firstName.value.trim().toUpperCase()  : '');
            form.append('MiddleName',           els.middleName ? els.middleName.value.trim().toUpperCase() : '');
            form.append('LastName',             els.lastName   ? els.lastName.value.trim().toUpperCase()   : '');
            form.append('Position',             els.position   ? els.position.value.trim().toUpperCase()   : '');
            form.append('Department',           els.department ? els.department.value.trim().toUpperCase() : '');
            form.append('OfficeId',             els.officeId   ? els.officeId.value                        : '');
            form.append('DeviceName',           els.deviceName ? els.deviceName.value.trim().toUpperCase() : '');
            form.append('FaceEncoding',         encodings[0]);
            form.append('AllFaceEncodingsJson', JSON.stringify(encodings));
            if (tokenInput) form.append('__RequestVerificationToken', tokenInput.value);

            els.submitBtn.disabled = true;
            els.submitBtn.innerHTML = '<i class="fa-solid fa-circle-notch fa-spin me-2"></i>Submitting...';

            fetch('/MobileRegistration/SubmitEnrollment', {
                method: 'POST', body: form, credentials: 'same-origin'
            })
            .then(function (r) { return r.json(); })
            .then(function (data) {
                if (data && (data.ok || data.success)) {
                    var empDbId = data.data && data.data.employeeDbId;
                    window.location.href = '/MobileRegistration/Success?isNewEmployee=true' +
                        (empDbId ? '&employeeDbId=' + empDbId : '');
                } else {
                    throw new Error((data && (data.message || data.error)) || 'Submission failed.');
                }
            })
            .catch(function (err) {
                els.submitBtn.disabled = false;
                els.submitBtn.innerHTML = '<i class="fa-solid fa-paper-plane me-2"></i>Submit Enrollment';
                if (typeof Swal !== 'undefined') {
                    Swal.fire({ icon: 'error', title: 'Error',
                        text: err.message || 'Could not submit.',
                        background: '#0f172a', color: '#f8fafc' });
                }
            });
        });
    }

    window.addEventListener('beforeunload', stopCamera);

    // Portrait orientation lock
    if (screen.orientation && screen.orientation.lock) {
        screen.orientation.lock('portrait-primary').catch(function () {});
    }

}());
