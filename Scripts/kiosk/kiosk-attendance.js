// kiosk-attendance.js
// Capture, submit, and handle attendance scan responses.
// Exposes window.KioskAttendance — must be loaded after kiosk-mediapipe.js.
(function () {
    'use strict';

    var _video          = null;
    var _canvas         = null;
    var _state          = null;
    var _token          = null;
    var _EP             = null;
    var _cfg            = null;
    var _appBase        = null;
    var _isMobile       = false;
    var _isPersonalMobile = false;

    // Callbacks wired in from kiosk.js
    var _setPrompt              = null;
    var _safeSetPrompt          = null;
    var _setAntiSpoof            = null;
    var _updateEta              = null;
    var _setIdleUi              = null;
    var _setMobileRegisterVisible = null;
    var _toastSuccess           = null;
    var _toastError             = null;
    var _isForcedKioskMode      = null;
    var _openVisitor            = null;

    // ── Capture canvas ─────────────────────────────────────────────────────────

    var _captureCanvas = document.createElement('canvas');
    var _captureCtx    = _captureCanvas.getContext('2d');

    // Capture at higher resolution to match enrollment quality
    var CAPTURE_W = 1280;
    var CAPTURE_H = 720;

    // ── Post-scan hold ─────────────────────────────────────────────────────────

    function armPostScanHold(ms) {
        var now  = Date.now();
        var hold = (typeof ms === 'number' && isFinite(ms) && ms > 0) ? ms : _cfg.postScan.holdMs;
        if (now < (_state.scanBlockUntil || 0)) { _safeSetPrompt('Please wait.', _state.blockMessage || 'Next scan ready soon.'); _updateEta(true); return; }
        _state.scanBlockUntil = now + hold;
    }

    // ── Capture ────────────────────────────────────────────────────────────────

    function captureFrameBlob(quality) {
        var q = (typeof quality === 'number') ? quality : 0.90;
        _captureCanvas.width  = CAPTURE_W;
        _captureCanvas.height = CAPTURE_H;
        _captureCtx.drawImage(_video, 0, 0, CAPTURE_W, CAPTURE_H);
        return new Promise(function (resolve) {
            _captureCanvas.toBlob(function (b) { resolve(b); }, 'image/jpeg', q);
        });
    }

    // ── Submit ─────────────────────────────────────────────────────────────────

    function submitAttendance(blob) {
        if (_state.submitInProgress) {
            return Promise.resolve({ ok: false, error: 'ALREADY_SUBMITTING' });
        }

        if (_state.attendAbortCtrl) {
            try { _state.attendAbortCtrl.abort(); } catch (e) {}
        }

        _state.submitInProgress = true;
        _state.attendAbortCtrl  = new AbortController();
        var signal = _state.attendAbortCtrl.signal;

        _state.lastCaptureAt = Date.now();
        _setPrompt('Scanning.', 'Hold still.');

        var fd = new FormData();
        fd.append('__RequestVerificationToken', _token);
        fd.append('image', blob, 'capture.jpg');
        if (_state.gps.lat      != null) fd.append('lat',      _state.gps.lat);
        if (_state.gps.lon      != null) fd.append('lon',      _state.gps.lon);
        if (_state.gps.accuracy != null) fd.append('accuracy', _state.gps.accuracy);

        if (window.KioskLocation && window.KioskLocation.getWfhMode()) {
            fd.append('wfhMode', 'true');
        }

        return fetch(_EP.attend, { method: 'POST', body: fd, credentials: 'same-origin', signal: signal })
            .then(function (r) {
                if (r.status === 429 || r.status === 503) {
                    return {
                        ok: false,
                        error: r.status === 429 ? 'RATE_LIMIT_EXCEEDED' : 'SYSTEM_BUSY',
                        retryAfter: Number(r.headers.get('Retry-After') || 0)
                    };
                }
                return r.json();
            })
            .then(function (j) {
                handleAttendanceResponse(j);
            })
            .catch(function (e) {
                if (e && e.name === 'AbortError') return;
                _state.backoffUntil = Date.now() + 2000;
                _setPrompt('System error.', 'Reload the page or check the server.');
            })
            .finally(function () {
                _state.submitInProgress = false;
            });
    }

    // ── Response handler ───────────────────────────────────────────────────────

    function handleAttendanceResponse(j, onSwalShownCallback) {
        if (!j) return;

        if (typeof j.antiSpoofScore === 'number') {
            var p  = Number(j.antiSpoofScore);
            var th = (j.threshold != null) ? Number(j.threshold) : null;
            var threshold = th !== null ? th : 0.45;
            var cls;
            if (p >= threshold) cls = 'live-pass';
            else if (p >= threshold * 0.80) cls = 'live-near';
            else cls = 'live-fail';

            _setAntiSpoof(p, th, cls);
            _state.latestAntiSpoof    = p;
            _state.antiSpoofThreshold = threshold;
        }

        var err     = j.error || '';
        var retryMs = Math.max(1500, Number(j.retryAfter || 0) * 1000);

        if (err === 'RATE_LIMIT_EXCEEDED' || err === 'SYSTEM_BUSY' || err === 'REQUEST_TIMEOUT') {
            _state.backoffUntil = Date.now() + retryMs;
        } else if (j.ok === true) {
            _state.backoffUntil = 0;
        }

        if (j.ok === true) {
            _state.consecutiveFailures = 0;

            if (j.mode !== 'VISITOR') {
                var evt      = (j.eventType || '').toUpperCase();
                var name     = j.displayName || j.name || 'Employee';
                var isTimeIn = evt === 'IN';

                if (window.FaceAttendAudio) FaceAttendAudio.playSuccess();

                var shouldRedirectMobile = _isMobile && !document.body.getAttribute('data-force-kiosk');
                var access = j.attendanceAccess || (j.data && j.data.attendanceAccess) || {};
                var recordUrl = String(access.recordUrl || 'Attendance/MyMonth').replace(/^\/+/, '');

                if (shouldRedirectMobile) {
                    if (window.Swal) {
                        Swal.fire({
                            title: '<i class="fa-solid fa-circle-check" style="color: #22c55e; font-size: 3rem; margin-bottom: 0.5rem;"></i>',
                            html: '<div style="font-size: 1.25rem; font-weight: 700; color: #1f2937; margin-bottom: 0.25rem;">' + (isTimeIn ? 'Time In' : 'Time Out') + '</div>' +
                                  '<div style="font-size: 1.5rem; font-weight: 600; color: #059669;">' + name + '</div>',
                            icon: null,
                            toast: false,
                            position: 'center',
                            showConfirmButton: false,
                            timer: 1500,
                            background: '#f0fdf4',
                            backdrop: 'rgba(0,0,0,0.3)',
                            didOpen: function () {
                                if (typeof onSwalShownCallback === 'function') onSwalShownCallback();
                            }
                        }).then(function () {
                            window.location.href = _appBase + recordUrl;
                        });
                    } else {
                        _toastSuccess((isTimeIn ? 'Time In' : 'Time Out') + ' -- ' + name);
                        setTimeout(function () {
                            window.location.href = _appBase + recordUrl;
                        }, 2500);
                    }

                    _setPrompt(isTimeIn ? 'Time In recorded.' : 'Time Out recorded.', name);
                    armPostScanHold(_cfg.postScan.holdMs);
                    _state.mpFaceSeenAt    = 0;
                    _state.mpReadyToFire   = false;
                    _state.mpStableStart   = 0;
                    _state.wasIdle         = true;
                } else {
                    if (window.Swal) {
                        var iconClass = isTimeIn ? 'fa-circle-check' : 'fa-circle-arrow-right';
                        var iconColor = isTimeIn ? '#22c55e' : '#3b82f6';
                        Swal.fire({
                            title: '<i class="fa-solid ' + iconClass + '" style="color: ' + iconColor + '; font-size: 3rem; margin-bottom: 0.5rem;"></i>',
                            html:  '<div style="font-size: 1.25rem; font-weight: 700; color: #1f2937; margin-bottom: 0.25rem;">' + (isTimeIn ? 'Time In' : 'Time Out') + '</div>' +
                                   '<div style="font-size: 1.5rem; font-weight: 600; color: #059669;">' + name + '</div>' +
                                   '<div style="font-size: 0.875rem; color: #6b7280; margin-top: 8px;">' + (j.message || 'Attendance recorded') + '</div>',
                            icon: null,
                            toast: false,
                            position: 'center',
                            showConfirmButton: false,
                            timer: 2500,
                            timerProgressBar: true,
                            background: isTimeIn ? '#f0fdf4' : '#eff6ff',
                            backdrop: 'rgba(0,0,0,0.3)',
                            width: '400px',
                            customClass: {
                                popup: 'attendance-success-popup',
                                title: 'attendance-success-title'
                            },
                            didOpen: function (popup) {
                                popup.style.borderRadius = '16px';
                                popup.style.boxShadow    = '0 20px 60px rgba(0,0,0,0.3)';
                            }
                        });
                    } else {
                        _toastSuccess((isTimeIn ? 'Time In' : 'Time Out') + ' -- ' + name);
                    }

                    _setPrompt(isTimeIn ? 'Time In recorded.' : 'Time Out recorded.', name);
                    armPostScanHold(_cfg.postScan.holdMs);
                    _state.mpFaceSeenAt  = 0;
                    _state.mpReadyToFire = false;
                    _state.mpStableStart = 0;
                    _state.wasIdle       = true;
                }
            } else {
                // VISITOR mode
                _state.consecutiveFailures = (_state.consecutiveFailures || 0) + 1;

                if (_state.consecutiveFailures >= 3) {
                    if (window.Swal) {
                        Swal.fire({
                            title: 'Having trouble?',
                            text:  'Make sure you are enrolled in the system. Try moving closer to the camera, look straight ahead, and ensure good lighting.',
                            icon:  'info',
                            confirmButtonText:  'Got it',
                            confirmButtonColor: '#3b82f6',
                            background: '#0f172a',
                            color:      '#f8fafc',
                            timer:           5000,
                            timerProgressBar: true
                        });
                    }
                    _state.consecutiveFailures = 0;
                }

                _openVisitor(j);
            }
            return;
        }

        var action = j.action || '';

        if (action === 'SELF_ENROLL') {
            var matchedEmployee = j.matchedEmployee || j.matchedEmployeeId || j.employeeName;

            if (_isPersonalMobile && !_isForcedKioskMode()) {
                if (matchedEmployee && matchedEmployee !== 'Unknown') {
                    _toastError('Face matched another employee. Please retry alone in the frame.');
                    if (window.Swal) {
                        Swal.fire({
                            title: 'Wrong Employee',
                            html:  '<div style="font-size: 1.1rem;">Face matched <strong>' + matchedEmployee + '</strong>.<br>Please retry with one face in the frame.</div>',
                            icon: 'warning',
                            confirmButtonText:  'Understood',
                            confirmButtonColor: '#f59e0b',
                            background: '#0f172a',
                            color:      '#f8fafc'
                        });
                    }
                    _setPrompt('Wrong employee detected.', 'Retry with one face in frame.');
                } else {
                    _setPrompt('Not recognized.', 'Retrying...');
                    setTimeout(function () {
                        _state.submitInProgress = false;
                        _state.liveInFlight     = false;
                        _state.mpReadyToFire    = true;
                    }, 1000);
                }
                armPostScanHold(3000);
            } else {
                _setPrompt('Enrollment required.', 'Please register to use the system.');
                armPostScanHold(5000);
            }
            return;
        }

        // ── Error codes ────────────────────────────────────────────────────────

        if (err === 'SCAN_CONFIRM_NEEDED') {
            if (_setPrompt) _setPrompt(j.message || 'Please look at the camera again.', '');
            armPostScanHold(500);
            return;
        }

        if (err === 'FACE_FAIL' || err === 'INVALID_IMAGE_FORMAT') {
            armPostScanHold(1000);
            return;
        }

        if (err === 'ENCODING_FAIL') {
            armPostScanHold(1000);
            return;
        }

        if (err === 'WRONG_DEVICE') {
            var matchedEmp = (j.details && j.details.matchedEmployee) || j.matchedEmployee || 'another employee';
            _toastError('Face matched ' + matchedEmp + '. Retry with one face in frame.');
            _setPrompt('Wrong employee.', 'Retry with one face in frame.');
            armPostScanHold(5000);
            return;
        }

        if (err === 'ALREADY_SCANNED' || err === 'TOO_SOON') {
            var tooSoonMsg = j.message || 'Already scanned. Please wait.';
            _toastError(tooSoonMsg);

            _state.blockMessage = tooSoonMsg;
            armPostScanHold(2000);

            setTimeout(function () {
                _state.blockMessage = null;
                if (_state.locationState === 'allowed') {
                    _setPrompt('Ready.', 'Stand still. One face only.');
                }
            }, 2500);
        } else if (err === 'ANTI_SPOOF_FAIL') {
            _setPrompt('Anti-spoof check failed.', 'Look directly at the camera in good lighting.');
            armPostScanHold(1500);
        } else if (err === 'NOT_RECOGNIZED') {
            _toastError('Face not recognized. Try moving closer or adjusting angle.');
            _setPrompt('Face not recognized.', 'Try moving closer or adjusting angle.');
            armPostScanHold(1500);
        } else if (err === 'RATE_LIMIT_EXCEEDED' || err === 'SYSTEM_BUSY') {
            _toastError('System busy. Please wait a moment and try again.');
            _setPrompt('System busy.', 'Please wait.');
            armPostScanHold(retryMs);
            return;
        } else if (err === 'REQUEST_TIMEOUT') {
            _toastError('Scan timed out. Please try again.');
            armPostScanHold(retryMs);
        } else {
            _toastError(j.message || err || 'Scan failed.');
            armPostScanHold(1500);
        }

        _setPrompt('Ready.', 'Stand still. One face only.');
    }

    // ── Init ───────────────────────────────────────────────────────────────────

    function init(videoEl, canvasEl, stateRef, tokenVal, epRef, cfgRef, deps) {
        _video          = videoEl;
        _canvas         = canvasEl;
        _state          = stateRef;
        _token          = tokenVal;
        _EP             = epRef;
        _cfg            = cfgRef;
        _appBase        = deps.appBase;
        _isMobile       = deps.isMobile;
        _isPersonalMobile = deps.isPersonalMobile;

        _setPrompt              = deps.setPrompt;
        _safeSetPrompt          = deps.safeSetPrompt;
        _setAntiSpoof            = deps.setAntiSpoof;
        _updateEta              = deps.updateEta;
        _setIdleUi              = deps.setIdleUi;
        _setMobileRegisterVisible = deps.setMobileRegisterVisible;
        _toastSuccess           = deps.toastSuccess;
        _toastError             = deps.toastError;
        _isForcedKioskMode      = deps.isForcedKioskMode;
        _openVisitor            = deps.openVisitor;
    }

    window.KioskAttendance = {
        init:             init,
        armPostScanHold:  armPostScanHold,
        captureFrameBlob: captureFrameBlob,
        submit:           submitAttendance
    };
})();
