(function () {
    'use strict';

    var MIN_FRAMES = 5;
    var MAX_FRAMES = 10;

    var state = {
        step:           1,
        capturedFrames: [],
        valid: {
            employeeId: false, firstName:  false, lastName:   false,
            middleName: true,  position:   false, department: false,
            officeId:   false
        }
    };

    var _cameraStarted = false;

    function el(id) { return document.getElementById(id); }

    function buildRequestHeaders(extraHeaders) {
        if (typeof FaceAttend !== 'undefined' &&
            FaceAttend.Utils &&
            typeof FaceAttend.Utils.mergeRequestHeaders === 'function') {
            return FaceAttend.Utils.mergeRequestHeaders(extraHeaders);
        }

        var headers = {};
        if (extraHeaders) {
            Object.keys(extraHeaders).forEach(function (key) {
                headers[key] = extraHeaders[key];
            });
        }
        return headers;
    }

    var els = {
        employeeId:       el('employeeId'),
        firstName:        el('firstName'),
        lastName:         el('lastName'),
        middleName:       el('middleName'),
        position:         el('position'),
        department:       el('department'),
        officeId:         el('officeId'),
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
            empId:         '',
            scanUrl:       '/MobileRegistration/ScanFrame',
            enrollUrl:     '/api/enrollment/enroll',
            minGoodFrames: MIN_FRAMES,
            maxKeepFrames: MAX_FRAMES,
            enablePreview: false
          })
        : null;

    if (enroll) enroll.loadServerConfig('/api/enrollment/config');

    window.FaceAttendEnrollment = enroll;

    function setStatusText(msg) {
        var e = el('cameraStatusText');
        if (e) e.textContent = msg || '';
    }

    function updateCaptureUI(frameCount, target) {
        var cnt = el('captureCount');
        if (cnt) cnt.textContent = frameCount;

        for (var i = 0; i < MAX_FRAMES; i++) {
            var dot = el('enDot' + i);
            if (dot) dot.classList.toggle('captured', i < frameCount);
        }
    }

    function resetCaptureUI() {
        updateCaptureUI(0, MIN_FRAMES);
        var bar = el('antiSpoofBar');
        if (bar) { bar.style.width = '0%'; bar.className = 'cam-fs-antiSpoof-fill'; }
        var txt = el('antiSpoofText');
        if (txt) txt.textContent = '0%';
        setStatusText('Initializing camera…');
    }

    function setAntiSpoofUI(pct) {
        var bar = el('antiSpoofBar');
        var txt = el('antiSpoofText');
        if (bar) {
            bar.style.width = pct + '%';
            bar.className = 'cam-fs-antiSpoof-fill' +
                (pct >= 65 ? ' high' : pct >= 35 ? ' mid' : ' low');
        }
        if (txt) txt.textContent = pct + '%';
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

        if (step !== 2) window.scrollTo(0, 0);
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
            reviewSampleCount: samples + ' / ' + MAX_FRAMES
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
        department : { min:2,  max:100, pat:/^[A-Z0-9 \.\-\/\(\),]+$/, label:'Department'  }
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

    if (els.officeId) {
        els.officeId.addEventListener('change', function () {
            state.valid.officeId = !!els.officeId.value;
            updateContinueBtn();
        });
    }

    function startCamera() {
        if (_cameraStarted) return;
        _cameraStarted = true;
        if (!enroll) return;
        var videoEl = el('enrollVideo');
        if (!videoEl) return;
        resetCaptureUI();
        enroll.startCamera(videoEl)
            .then(function () {
                setStatusText('Look straight at the camera');
                enroll.startAutoEnrollment();
            })
            .catch(function (e) {
                _cameraStarted = false;
                var msg = (e && e.message) || 'Could not access camera.';
                setStatusText('Camera error: ' + msg);
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
        var guideEl = el('enrollGuidePrompt');
        if (guideEl) guideEl.innerHTML = '<i class="fa-solid fa-circle-dot"></i> Position your face in the frame';
    }

    if (enroll) {
        enroll.callbacks.onStatus = function (msg) {
            setStatusText(msg || '');
        };

        enroll.callbacks.onAntiSpoofUpdate = function (pct) {
            setAntiSpoofUI(pct);
        };

        enroll.callbacks.onCaptureProgress = function (current, target) {
            updateCaptureUI(current, target);
        };

        enroll.callbacks.onDistanceFeedback = function (data) {
            var guideEl = el('enrollGuidePrompt');
            if (data.status === 'too_far' || data.status === 'borderline') {
                setStatusText('Move closer to the camera');
                if (guideEl)
                    guideEl.innerHTML = '<i class="fa-solid fa-arrow-up"></i> Move closer — face too small';
            } else if (data.status === 'too_close') {
                setStatusText('Back up a little');
                if (guideEl)
                    guideEl.innerHTML = '<i class="fa-solid fa-arrow-down"></i> Too close — back up slightly';
            } else if (data.status === 'off_center') {
                setStatusText('Center your face');
                if (guideEl)
                    guideEl.innerHTML = '<i class="fa-solid fa-arrows-up-down-left-right"></i> Center your face in the frame';
            }
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
                    return '<img src="' + url + '" style="width:72px;height:72px;object-fit:cover;' +
                           'border-radius:8px;border:2px solid rgba(255,255,255,0.15);margin:4px;" />';
                }).join('');

                Swal.fire({
                    title: 'Enrollment Complete!',
                    html:
                        '<div style="display:flex;gap:6px;justify-content:center;flex-wrap:wrap;margin-bottom:12px;">' +
                        thumbHtml + '</div>' +
                        '<div style="font-size:.9rem;">' +
                        '<p>' + data.frameCount + ' frames captured</p>' +
                        '<p>Best anti-spoof: ' + data.bestAntiSpoof + '%</p></div>',
                    showCancelButton:  true,
                    confirmButtonText: 'Continue to Review \u2192',
                    cancelButtonText:  'Retake',
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
                : ((result && (result.message || result.error)) || 'Scan error, retrying…');
            setStatusText(msg);
        };
    }

    if (els.btnToCapture)     els.btnToCapture.addEventListener('click',     function () { setStep(2); });
    if (els.btnBackToDetails) els.btnBackToDetails.addEventListener('click', function () { setStep(1); });
    if (els.btnBackToCapture) els.btnBackToCapture.addEventListener('click', function () { doRetake(); setStep(2); });

    if (els.submitBtn) {
        els.submitBtn.addEventListener('click', function () {
            var frames = [];
            (state.capturedFrames || []).forEach(function (frame) {
                if (frame && frame.blob) frames.push(frame.blob);
            });

            if (frames.length < MIN_FRAMES) {
                if (typeof Swal !== 'undefined') {
                    Swal.fire({ icon: 'error', title: 'Capture Incomplete',
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
            frames.forEach(function (blob, index) {
                form.append('images', blob, 'mobile_enroll_' + (index + 1) + '.jpg');
            });
            if (tokenInput) form.append('__RequestVerificationToken', tokenInput.value);

            els.submitBtn.disabled = true;
            els.submitBtn.innerHTML = '<i class="fa-solid fa-circle-notch fa-spin me-2"></i>Submitting…';

            fetch('/MobileRegistration/SubmitEnrollment', {
                method: 'POST',
                body: form,
                credentials: 'same-origin',
                headers: buildRequestHeaders({
                    'X-Requested-With': 'XMLHttpRequest'
                })
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

}());
