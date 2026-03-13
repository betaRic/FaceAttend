/**
 * FaceAttend - Admin Enrollment (Refactored)
 * Uses FaceAttend.Enrollment core module for shared logic
 * 
 * This file provides:
 * - Enhanced error messages with step information
 * - Image compression before upload
 * - Preview modal with retake option
 */

(function () {
    'use strict';

    var root = document.getElementById("enrollRoot");
    if (!root) return;

    // Configuration
    var config = {
        empId: (root.getAttribute("data-employee-id") || "").trim(),
        perFrame: parseFloat(root.getAttribute("data-per-frame") || "0.75"),
        scanUrl: root.getAttribute("data-scan-url") || "/Biometrics/ScanFrame",
        enrollUrl: root.getAttribute("data-enroll-url") || "/Biometrics/Enroll",
        redirectUrl: root.getAttribute("data-redirect-url") || "/Admin/Employees"
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
        file: el("file")
    };

    // Create enrollment instance
    var enrollment = FaceAttend.Enrollment.create({
        empId: config.empId,
        perFrameThreshold: config.perFrame,
        scanUrl: config.scanUrl,
        enrollUrl: config.enrollUrl,
        redirectUrl: config.redirectUrl,
        minGoodFrames: 10,  // Admin enrollment requires 10 frames
        maxKeepFrames: 10,
        enablePreview: true
    });

    // Set DOM elements
    enrollment.elements.cam = elements.cam;

    // -------------------------------------------------------------------------
    // UI CALLBACKS
    // -------------------------------------------------------------------------

    enrollment.callbacks.onStatus = function (text, kind) {
        setStatus(elements.camStatus || elements.upStatus, text, kind);
    };

    enrollment.callbacks.onEnrollmentComplete = function (vectorCount, result) {
        showSuccessMessage(vectorCount);
    };

    enrollment.callbacks.onEnrollmentError = function (result) {
        showErrorMessage(enrollment.describeEnrollError(result));
    };

    enrollment.callbacks.onMultiFaceWarning = function (count) {
        if (count > 1) {
            console.log('[Enroll] Multiple faces detected (' + count + '), using nearest face');
        }
        document.dispatchEvent(new CustomEvent('fa:multiFaceWarning', {
            detail: { count: count }
        }));
    };

    // -------------------------------------------------------------------------
    // STATUS HELPERS
    // -------------------------------------------------------------------------
    function setStatus(target, html, kind) {
        if (!target) return;
        target.innerHTML = '<div class="alert alert-' + kind + ' py-2 mb-0">' + html + '</div>';
    }

    function clearStatus(target) {
        if (target) target.innerHTML = "";
    }

    function showWizard(show) {
        if (elements.wizard) elements.wizard.classList.toggle("d-none", !show);
    }

    function showPane(pane, show) {
        if (pane) pane.classList.toggle("d-none", !show);
    }

    // -------------------------------------------------------------------------
    // SWEETALERT MODALS
    // -------------------------------------------------------------------------
    async function showPreviewModal(blobs) {
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
                html: '<div style="margin-bottom:15px;">' +
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
                html: '<p>Face biometric successfully enrolled.</p>' +
                    '<p class="text-muted"><strong>' + vectorCount + '</strong> face sample(s) saved</p>',
                confirmButtonText: 'Back to Employee List',
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
    // ENHANCED ERROR MESSAGES
    // -------------------------------------------------------------------------
    function describeEnrollError(r) {
        return enrollment.describeEnrollError(r);
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
                    setStatus(elements.camStatus, "Camera ready, auto enroll running, hold still.", "info");
                })
                .catch(function (e) {
                    enrollment.stopCamera();
                    showWizard(true);
                    showPane(elements.livePane, false);
                    setStatus(elements.camStatus, "Camera blocked: " + (e && e.message ? e.message : e) + ", try again.", "danger");
                });
            return;
        }

        if (mode === "upload") {
            enrollment.stopCamera();
            showPane(elements.livePane, false);
            showPane(elements.uploadPane, true);
            clearStatus(elements.upStatus);

            if (elements.file) {
                try { elements.file.focus(); } catch { }
            }
        }
    }

    // -------------------------------------------------------------------------
    // UPLOAD ENROLL WITH COMPRESSION
    // -------------------------------------------------------------------------
    async function enrollUploadAuto() {
        if (!elements.file || !elements.file.files || !elements.file.files[0]) {
            setStatus(elements.upStatus, "Choose an image first.", "warning");
            return;
        }

        try {
            await enrollment.enrollFromFiles(elements.file.files, {
                maxImages: 5,
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
                if (elements.file) elements.file.value = '';
                setStatus(elements.upStatus, "Choose new image(s).", "info");
            } else {
                setStatus(elements.upStatus, e.message, "danger");
            }
        }
    }

    // -------------------------------------------------------------------------
    // EVENT WIRING
    // -------------------------------------------------------------------------
    function wireWizard() {
        if (!elements.wizard) return;

        var cards = elements.wizard.querySelectorAll("[data-enroll-mode]");
        for (var i = 0; i < cards.length; i++) {
            (function (card) {
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
            })(cards[i]);
        }
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
        showWizard(true);
        showPane(elements.livePane, false);
        showPane(elements.uploadPane, false);
        setStatus(elements.camStatus, "Choose a method above.", "info");
        wireWizard();
        wireUpload();
    }

    // Start
    init();
})();
