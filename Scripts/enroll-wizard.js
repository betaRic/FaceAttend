/**
 * FaceAttend - Enrollment Wizard (Refactored)
 * Uses FaceAttend.Enrollment core module for shared logic
 * 
 * This file now focuses on wizard-specific UI:
 * - Step indicators
 * - Liveness bar and side panel
 * - Capture progress dots
 * - Video wrap glow effects
 */

(function () {
    'use strict';

    // Configuration from data attributes
    var root = document.getElementById("enrollRoot");
    if (!root) return;

    var config = {
        empId: (root.getAttribute("data-employee-id") || "").trim(),
        perFrame: parseFloat(root.getAttribute("data-per-frame") || "0.75"),
        scanUrl: root.getAttribute("data-scan-url") || "/Biometrics/ScanFrame",
        enrollUrl: root.getAttribute("data-enroll-url") || "/Biometrics/Enroll",
        redirectUrl: root.getAttribute("data-redirect-url") || "/Admin/Employees",
        minGoodFrames: parseInt(root.getAttribute("data-min-good-frames") || "3", 10),
        maxKeepFrames: parseInt(root.getAttribute("data-max-keep-frames") || "5", 10)
    };

    if (!isFinite(config.perFrame) || config.perFrame <= 0) config.perFrame = 0.75;

    // DOM Elements
    function el(id) { return document.getElementById(id); }

    var elements = {
        wizard: el("enrollWizard"),
        livePane: el("livePane"),
        uploadPane: el("uploadPane"),
        cam: el("cam"),
        cap: el("cap"),
        camStatus: el("camStatus"),
        upStatus: el("upStatus"),
        file: el("file"),
        liveBarFill: el("liveBarFill"),
        liveBarPct: el("liveBarPct"),
        sideScore: el("sideScore"),
        sideLabel: el("sideScoreLabel"),
        sideBadge: el("sidePassBadge"),
        sideBadgeText: el("sideBadgeText"),
        capProgressBar: el("capProgressBar"),
        sideCaptures: el("sideCaptures"),
        videoWrap: el("videoWrap"),
        stepEls: document.querySelectorAll('.fa-step'),
        divider1: el('divider1'),
        divider2: el('divider2'),
        dots: [el('dot1'), el('dot2'), el('dot3'), el('dot4'), el('dot5')]
    };

    // Create enrollment instance
    var enrollment = FaceAttend.Enrollment.create({
        empId: config.empId,
        perFrameThreshold: config.perFrame,
        scanUrl: config.scanUrl,
        enrollUrl: config.enrollUrl,
        redirectUrl: config.redirectUrl,
        minGoodFrames: config.minGoodFrames,
        maxKeepFrames: config.maxKeepFrames,
        enablePreview: true
    });

    // Set DOM elements
    enrollment.elements.cam = elements.cam;

    // -------------------------------------------------------------------------
    // UI CALLBACKS
    // -------------------------------------------------------------------------

    enrollment.callbacks.onStatus = function (text, kind) {
        renderCamStatus(text, kind);
    };

    enrollment.callbacks.onLivenessUpdate = function (pct, kind) {
        updateLiveness(pct, kind);
    };

    enrollment.callbacks.onCaptureProgress = function (current, target) {
        setCaptures(current);
    };

    enrollment.callbacks.onEnrollmentComplete = function (vectorCount, result) {
        renderCamStatus("Enrollment saved!", "success");
        setStep(3);
        setCaptures(config.minGoodFrames);
        setVideoGlow('success');
        if (elements.sideLabel) {
            elements.sideLabel.textContent = 'Enrollment complete!';
        }
        showSuccessMessage(vectorCount);
    };

    enrollment.callbacks.onEnrollmentError = function (result) {
        if (result.error === 'FACE_ALREADY_ENROLLED') {
            showErrorMessage(enrollment.describeEnrollError(result));
        }
    };

    enrollment.callbacks.onMultiFaceWarning = function (count) {
        document.dispatchEvent(new CustomEvent('fa:multiFaceWarning', {
            detail: { count: count }
        }));
    };

    // -------------------------------------------------------------------------
    // STEP INDICATOR
    // -------------------------------------------------------------------------
    function setStep(n) {
        elements.stepEls.forEach(function (s) {
            var sn = parseInt(s.getAttribute('data-step'), 10);
            s.classList.remove('active', 'done');
            if (sn < n) s.classList.add('done');
            if (sn === n) s.classList.add('active');
        });
        if (elements.divider1) elements.divider1.classList.toggle('done', n > 1);
        if (elements.divider2) elements.divider2.classList.toggle('done', n > 2);
    }

    // -------------------------------------------------------------------------
    // LIVENESS BAR & SIDE PANEL
    // -------------------------------------------------------------------------
    function updateLiveness(pct, kind) {
        if (elements.liveBarFill) {
            elements.liveBarFill.style.width = pct + '%';
            elements.liveBarFill.className = 'fa-live-bar-fill' +
                (kind === 'pass' ? ' live-pass' : kind === 'warn' ? ' live-warn' : kind === 'fail' ? ' live-fail' : '');
        }
        if (elements.liveBarPct) elements.liveBarPct.textContent = pct + '%';
        if (elements.sideScore) {
            elements.sideScore.textContent = pct + '%';
            elements.sideScore.className = 'fa-live-score-num' +
                (kind === 'pass' ? ' pass' : kind === 'fail' ? ' fail' : '');
        }
        if (elements.sideBadge && elements.sideBadgeText && kind) {
            elements.sideBadge.style.display = 'flex';
            elements.sideBadgeText.className = kind === 'pass' ? 'badge-pass' : 'badge-fail';
            elements.sideBadgeText.textContent = kind === 'pass' ? 'PASS' : kind === 'fail' ? 'FAIL' : 'LOW';
        }
    }

    // -------------------------------------------------------------------------
    // CAPTURE PROGRESS DOTS
    // -------------------------------------------------------------------------
    function setCaptures(n) {
        n = Math.max(0, parseInt(n, 10) || 0);
        var progressCount = Math.min(n, config.minGoodFrames);

        elements.dots.forEach(function (d, i) {
            if (!d) return;
            d.classList.remove('filled', 'active');
            if (i >= config.minGoodFrames) {
                d.style.display = 'none';
                return;
            }
            d.style.display = '';
            if (i < progressCount) d.classList.add('filled');
            else if (i === progressCount && progressCount < config.minGoodFrames) d.classList.add('active');
        });

        if (elements.capProgressBar) {
            elements.capProgressBar.style.width = ((progressCount / config.minGoodFrames) * 100) + '%';
        }
        if (elements.sideCaptures) {
            elements.sideCaptures.textContent = n;
        }
    }

    // -------------------------------------------------------------------------
    // VIDEO WRAP GLOW
    // -------------------------------------------------------------------------
    function setVideoGlow(kind) {
        if (!elements.videoWrap) return;
        elements.videoWrap.classList.remove('border-success-glow', 'border-danger-glow', 'pulse-border');
        if (kind === 'success') elements.videoWrap.classList.add('border-success-glow');
        else if (kind === 'danger') elements.videoWrap.classList.add('border-danger-glow');
        else if (kind === 'scan') elements.videoWrap.classList.add('pulse-border');
    }

    // -------------------------------------------------------------------------
    // STATUS RENDERERS
    // -------------------------------------------------------------------------
    function iconFor(k) {
        if (k === 'success') return '<i class="fa-solid fa-circle-check" aria-hidden="true"></i>';
        if (k === 'danger') return '<i class="fa-solid fa-circle-xmark" aria-hidden="true"></i>';
        if (k === 'warning') return '<i class="fa-solid fa-triangle-exclamation" aria-hidden="true"></i>';
        return '<span class="fa-status-spinner" aria-hidden="true"></span>';
    }

    function parseLivenessFromText(text, kind) {
        var m = text.match(/liveness[:\s]+([0-9]+\.?[0-9]*)/i);
        if (m) {
            var val = parseFloat(m[1]);
            var pct = val <= 1 ? Math.round(val * 100) : Math.round(val);
            var lvKind = kind === 'success' ? 'pass' : kind === 'warning' ? 'warn' : 'fail';
            updateLiveness(Math.min(100, pct), lvKind);
        }

        var good = text.match(/good frames:\s*(\d+)\s*\/\s*(\d+)/i);
        if (good) setCaptures(parseInt(good[1], 10));

        var coll = text.match(/saving enrollment\s*\((\d+)\s*frame/i);
        if (coll) setCaptures(parseInt(coll[1], 10));

        if (/enrollment saved/i.test(text)) {
            setStep(3);
            setCaptures(config.minGoodFrames);
            setVideoGlow('success');
            if (elements.sideLabel) elements.sideLabel.textContent = 'Enrollment complete!';
        }
    }

    function renderCamStatus(text, kind) {
        var k = kind || 'info';
        var extraCls = (k !== 'info') ? ' is-' + k : '';
        var safeText = text.replace(/<[^>]+>/g, '');
        if (elements.camStatus) {
            elements.camStatus.innerHTML = '<div class="fa-status-msg' + extraCls + '">' +
                iconFor(k) + '<span>' + safeText + '</span></div>';
        }
        setVideoGlow(k === 'success' ? 'success' : k === 'danger' ? 'danger' : 'scan');
        parseLivenessFromText(safeText, k);
        if (elements.sideLabel && !/liveness/i.test(safeText)) {
            elements.sideLabel.textContent = safeText.substring(0, 60);
        }
    }

    function renderUpStatus(text, kind) {
        var k = kind || 'info';
        var safeText = text.replace(/<[^>]+>/g, '');
        if (elements.upStatus) {
            elements.upStatus.innerHTML = '<div class="fa-upstatus-box is-' + k + '">' +
                iconFor(k) + '<span>' + safeText + '</span></div>';
        }
    }

    // -------------------------------------------------------------------------
    // UI WIZARD
    // -------------------------------------------------------------------------
    function showWizard(show) {
        if (elements.wizard) elements.wizard.classList.toggle("d-none", !show);
    }

    function showPane(pane, show) {
        if (pane) pane.classList.toggle("d-none", !show);
    }

    function clearStatus(target) {
        if (target) target.innerHTML = "";
    }

    // -------------------------------------------------------------------------
    // SWEETALERT MODALS
    // -------------------------------------------------------------------------
    async function showPreviewModal(blobs) {
        // Safety check for SweetAlert2
        if (typeof Swal === 'undefined' || !Swal.fire) {
            console.error('[Enroll] SweetAlert2 not loaded');
            return confirm('Enroll with ' + blobs.length + ' face sample(s)?');
        }
        
        var imageHtml = '';
        var createdUrls = [];
        for (var i = 0; i < blobs.length; i++) {
            var url = URL.createObjectURL(blobs[i]);
            createdUrls.push(url);
            imageHtml += '<img src="' + url + '" style="width:80px;height:80px;object-fit:cover;border-radius:8px;margin:5px;" />';
        }
        
        try {
            var result = await Swal.fire({
                title: 'Confirm Enrollment',
                html: '<div style="margin-bottom:15px;"><p><strong>' + blobs.length + '</strong> face sample(s) captured</p><div style="display:flex;flex-wrap:wrap;justify-content:center;">' + imageHtml + '</div></div>',
                showCancelButton: true,
                confirmButtonText: '<i class="fa-solid fa-check me-2"></i>Enroll Now',
                cancelButtonText: '<i class="fa-solid fa-rotate-left me-2"></i>Retake',
                confirmButtonColor: '#28a745',
                cancelButtonColor: '#6c757d',
                allowOutsideClick: false
            });
            createdUrls.forEach(function (url) { URL.revokeObjectURL(url); });
            return result && result.isConfirmed;
        } catch (e) {
            console.error('[Enroll] SweetAlert error:', e);
            createdUrls.forEach(function (url) { URL.revokeObjectURL(url); });
            return confirm('Enroll with ' + blobs.length + ' face sample(s)?');
        }
    }

    function showSuccessMessage(vectorCount) {
        if (typeof Swal === 'undefined' || !Swal.fire) {
            alert('Enrollment complete! ' + vectorCount + ' face sample(s) saved.');
            window.location.href = config.redirectUrl;
            return;
        }
        
        try {
            Swal.fire({
                icon: 'success',
                title: 'Enrollment Complete!',
                html: '<p>Face biometric successfully enrolled.</p><p class="text-muted"><strong>' + vectorCount + '</strong> face sample(s) saved</p>',
                confirmButtonText: 'Back to List',
                confirmButtonColor: '#28a745',
                allowOutsideClick: false
            }).then(function (result) {
                if (result && result.isConfirmed) {
                    window.location.href = config.redirectUrl;
                }
            }).catch(function(e) {
                console.error('[Enroll] SweetAlert error:', e);
                window.location.href = config.redirectUrl;
            });
        } catch (e) {
            console.error('[Enroll] SweetAlert error:', e);
            alert('Enrollment complete! ' + vectorCount + ' face sample(s) saved.');
            window.location.href = config.redirectUrl;
        }
    }

    function showErrorMessage(errorText) {
        if (typeof Swal === 'undefined' || !Swal.fire) {
            alert('Enrollment failed: ' + errorText);
            return;
        }
        
        try {
            Swal.fire({
                icon: 'error',
                title: 'Enrollment Failed',
                text: errorText,
                confirmButtonText: 'Try Again',
                confirmButtonColor: '#dc3545'
            }).catch(function(e) {
                console.error('[Enroll] SweetAlert error:', e);
                alert('Enrollment failed: ' + errorText);
            });
        } catch (e) {
            console.error('[Enroll] SweetAlert error:', e);
            alert('Enrollment failed: ' + errorText);
        }
    }

    // -------------------------------------------------------------------------
    // MODE SELECTION
    // -------------------------------------------------------------------------
    function chooseMode(mode) {
        showWizard(false);

        if (mode === "live") {
            showPane(elements.uploadPane, false);
            showPane(elements.livePane, true);

            enrollment.startCamera(elements.cam)
                .then(function () {
                    enrollment.startAutoEnrollment();
                    setStep(2);
                    renderCamStatus("Camera ready. Hold still for auto-enroll.", "info");
                })
                .catch(function (e) {
                    enrollment.stopCamera();
                    showWizard(true);
                    showPane(elements.livePane, false);
                    renderCamStatus("Camera blocked: " + (e && e.message ? e.message : e), "danger");
                });
            return;
        }

        if (mode === "upload") {
            enrollment.stopCamera();
            showPane(elements.livePane, false);
            showPane(elements.uploadPane, true);
            setStep(2);
            clearStatus(elements.upStatus);
            if (elements.file) {
                try { elements.file.focus(); } catch { }
            }
        }
    }

    // -------------------------------------------------------------------------
    // UPLOAD ENROLL
    // -------------------------------------------------------------------------
    async function enrollUploadAuto() {
        if (!elements.file || !elements.file.files || !elements.file.files[0]) {
            renderUpStatus("Choose an image first.", "warning");
            return;
        }

        try {
            await enrollment.enrollFromFiles(elements.file.files, {
                maxImages: config.maxKeepFrames,
                showPreview: showPreviewModal,
                precheck: {
                    maxSize: 5 * 1024 * 1024,
                    minWidth: 200,
                    minHeight: 200,
                    maxDimension: 4096
                }
            });
        } catch (e) {
            if (e.message === 'CANCELLED') {
                elements.file.value = '';
                renderUpStatus("Choose new image(s).", "info");
            } else {
                renderUpStatus(e.message, "danger");
            }
        }
    }

    // -------------------------------------------------------------------------
    // EVENT WIRING
    // -------------------------------------------------------------------------
    function wireWizard() {
        if (!elements.wizard) return;
        var cards = elements.wizard.querySelectorAll("[data-enroll-mode]");
        cards.forEach(function (card) {
            function go() {
                var mode = (card.getAttribute("data-enroll-mode") || "").trim();
                chooseMode(mode);
            }
            card.addEventListener("click", go);
            card.addEventListener("keydown", function (e) {
                if (e.key === "Enter" || e.key === " ") {
                    e.preventDefault();
                    go();
                }
            });
        });
    }

    function wireUpload() {
        if (!elements.file) return;
        elements.file.addEventListener("change", enrollUploadAuto);
    }

    // Cleanup
    document.addEventListener("fa:stopCam", function () {
        enrollment.stopCamera();
    });
    window.addEventListener("beforeunload", function () {
        enrollment.stopCamera();
    });

    // Initialize
    function init() {
        setStep(1);
        setCaptures(0);
        showWizard(true);
        showPane(elements.livePane, false);
        showPane(elements.uploadPane, false);
        renderCamStatus("Choose a method above.", "info");
        wireWizard();
        wireUpload();
    }

    // Start
    init();
})();
