/**
 * FaceAttend – Kiosk Fullscreen & Permission Bootstrap
 * =====================================================
 * Drop this ABOVE the closing </body> tag in Views/Kiosk/Index.cshtml,
 * BEFORE kiosk.js loads.
 *
 * What this does:
 *   1. Shows a branded permission prompt before any browser dialog fires.
 *   2. Requests camera + geolocation from the user in sequence.
 *   3. Enters fullscreen after the user grants permission (requires gesture).
 *   4. Auto-re-enters fullscreen if the user presses Escape.
 *   5. Handles mobile (PWA / Android / iOS) correctly.
 *   6. On desktop: signals kiosk.js to start via window.kioskPermissionsReady.
 *
 * Integration notes:
 *   - In kiosk.js init(), wrap startCamera() and startGpsIfAvailable() inside
 *     a check: await window.kioskPermissionsReady (a Promise set below).
 *   - The camera + GPS are pre-requested here; kiosk.js getUserMedia call will
 *     reuse the already-granted permission (no second prompt).
 */

(function () {
    'use strict';

    // ─── Detect mobile ───────────────────────────────────────────────────────
    const isMobile = /Mobi|Android|iPhone|iPad|iPod/i.test(navigator.userAgent)
        || (navigator.maxTouchPoints > 1 && /Macintosh/i.test(navigator.userAgent)); // iPadOS

    // ─── Resolve that kiosk.js can await ────────────────────────────────────
    let _resolve;
    window.kioskPermissionsReady = new Promise(function (res) { _resolve = res; });

    // ─── Fullscreen helpers ──────────────────────────────────────────────────
    function canFullscreen() {
        return !!(
            document.documentElement.requestFullscreen ||
            document.documentElement.webkitRequestFullscreen ||
            document.documentElement.mozRequestFullScreen ||
            document.documentElement.msRequestFullscreen
        );
    }

    function enterFullscreen() {
        var el = document.documentElement;
        try {
            var fn = el.requestFullscreen
                || el.webkitRequestFullscreen
                || el.mozRequestFullScreen
                || el.msRequestFullscreen;
            if (fn) fn.call(el, { navigationUI: 'hide' });
        } catch (e) {
            // iOS Safari will refuse outside of touch event — silently ignore
        }
    }

    function isFullscreen() {
        return !!(
            document.fullscreenElement ||
            document.webkitFullscreenElement ||
            document.mozFullScreenElement ||
            document.msFullscreenElement
        );
    }

    // Re-enter fullscreen when user exits (Esc key, swipe up, etc.)
    function watchFullscreen() {
        function onFsChange() {
            if (!isFullscreen()) {
                // Small delay so the browser state settles before re-requesting
                setTimeout(function () {
                    // Only re-enter if this page is still the active document
                    if (!document.hidden) enterFullscreen();
                }, 800);
            }
        }
        document.addEventListener('fullscreenchange', onFsChange);
        document.addEventListener('webkitfullscreenchange', onFsChange);
        document.addEventListener('mozfullscreenchange', onFsChange);
        document.addEventListener('MSFullscreenChange', onFsChange);
    }

    // ─── Permission modal ────────────────────────────────────────────────────
    // Build the UI entirely in JS so it works with MVC Razor without touching cshtml.
    function buildModal() {
        var overlay = document.createElement('div');
        overlay.id = 'fa-perm-overlay';
        overlay.innerHTML = [
            '<div id="fa-perm-card">',
            '  <div id="fa-perm-logo-wrap">',
            '    <img id="fa-perm-logo" src="/Content/images/dilg-logo.svg" alt="DILG" onerror="this.style.display=\'none\'">',
            '  </div>',
            '  <div id="fa-perm-brand">FaceAttend</div>',
            '  <div id="fa-perm-sub">DILG Region XII &bull; Face Attendance System</div>',
            '  <div id="fa-perm-divider"></div>',
            '  <div id="fa-perm-title">Permissions Required</div>',
            '  <div id="fa-perm-body">',
            '    <div class="fa-perm-item" id="fa-pi-cam">',
            '      <span class="fa-perm-icon">&#128247;</span>',
            '      <div class="fa-perm-detail">',
            '        <strong>Camera</strong>',
            '        <span>Required for face recognition</span>',
            '      </div>',
            '      <span class="fa-perm-status" id="fa-ps-cam">&#8203;</span>',
            '    </div>',
            '    <div class="fa-perm-item" id="fa-pi-loc">',
            '      <span class="fa-perm-icon">&#128205;</span>',
            '      <div class="fa-perm-detail">',
            '        <strong>Location</strong>',
            '        <span>Verify you are at a DILG office</span>',
            '      </div>',
            '      <span class="fa-perm-status" id="fa-ps-loc">&#8203;</span>',
            '    </div>',
            '    <div class="fa-perm-item" id="fa-pi-fs">',
            '      <span class="fa-perm-icon">&#9974;</span>',
            '      <div class="fa-perm-detail">',
            '        <strong>Full Screen</strong>',
            '        <span>Kiosk mode for dedicated use</span>',
            '      </div>',
            '      <span class="fa-perm-status" id="fa-ps-fs">&#8203;</span>',
            '    </div>',
            '  </div>',
            '  <div id="fa-perm-note">',
            '    Your face data is processed locally and is never shared externally.',
            '  </div>',
            '  <button id="fa-perm-btn" type="button">Allow &amp; Start</button>',
            '  <div id="fa-perm-error" class="hidden"></div>',
            '</div>',
        ].join('');
        document.body.appendChild(overlay);
        return overlay;
    }

    function setStatus(id, state) {
        // state: 'pending' | 'ok' | 'fail' | 'skip'
        var el = document.getElementById(id);
        if (!el) return;
        var map = { pending: '&#8987;', ok: '&#10003;', fail: '&#10007;', skip: '&#8212;' };
        var cls = { pending: 'perm-pending', ok: 'perm-ok', fail: 'perm-fail', skip: 'perm-skip' };
        el.innerHTML = map[state] || '';
        el.className = 'fa-perm-status ' + (cls[state] || '');
    }

    function setError(msg) {
        var el = document.getElementById('fa-perm-error');
        if (!el) return;
        el.textContent = msg;
        el.classList.remove('hidden');
    }

    function hideModal(overlay) {
        overlay.style.opacity = '0';
        overlay.style.transition = 'opacity 0.5s ease';
        setTimeout(function () {
            if (overlay.parentNode) overlay.parentNode.removeChild(overlay);
        }, 550);
    }

    // ─── Main bootstrap ──────────────────────────────────────────────────────
    async function bootstrap() {
        var overlay = buildModal();
        var btn = document.getElementById('fa-perm-btn');
        var camGranted = false;
        var locGranted = false;

        // Check pre-existing permissions if Permissions API available
        if (navigator.permissions && navigator.permissions.query) {
            try {
                var camState = await navigator.permissions.query({ name: 'camera' });
                if (camState.state === 'granted') {
                    camGranted = true;
                    setStatus('fa-ps-cam', 'ok');
                }
            } catch (_) {}
            try {
                var locState = await navigator.permissions.query({ name: 'geolocation' });
                if (locState.state === 'granted') {
                    locGranted = true;
                    setStatus('fa-ps-loc', 'ok');
                }
            } catch (_) {}
        }

        // If on desktop and all pre-granted, still show briefly then auto-proceed
        if (camGranted && locGranted) {
            setStatus('fa-ps-fs', 'ok');
            btn.textContent = 'Starting…';
            btn.disabled = true;
            setTimeout(function () {
                proceed(overlay, camGranted, locGranted);
            }, 900);
            return;
        }

        btn.addEventListener('click', function () {
            btn.disabled = true;
            btn.textContent = 'Requesting…';
            proceed(overlay, camGranted, locGranted);
        });
    }

    async function proceed(overlay, camAlreadyGranted, locAlreadyGranted) {
        var errMsg = null;

        // ── Camera ──────────────────────────────────────────────────────────
        if (!camAlreadyGranted) {
            setStatus('fa-ps-cam', 'pending');
            try {
                var stream = await navigator.mediaDevices.getUserMedia({
                    video: { facingMode: 'user', width: { ideal: 640 }, height: { ideal: 480 } },
                    audio: false,
                });
                // Stop the test stream — kiosk.js will open its own
                stream.getTracks().forEach(function (t) { t.stop(); });
                setStatus('fa-ps-cam', 'ok');
            } catch (e) {
                setStatus('fa-ps-cam', 'fail');
                errMsg = 'Camera access was denied. Please allow camera and reload.';
            }
        }

        // ── Location ─────────────────────────────────────────────────────────
        if (!locAlreadyGranted && 'geolocation' in navigator) {
            setStatus('fa-ps-loc', 'pending');
            try {
                await new Promise(function (res, rej) {
                    navigator.geolocation.getCurrentPosition(res, rej, {
                        enableHighAccuracy: true,
                        timeout: 10000,
                        maximumAge: 0,
                    });
                });
                setStatus('fa-ps-loc', 'ok');
            } catch (e) {
                setStatus('fa-ps-loc', 'fail');
                if (!errMsg) errMsg = 'Location access was denied. GPS is required for office verification.';
            }
        } else if (!('geolocation' in navigator)) {
            setStatus('fa-ps-loc', 'skip');
        }

        // ── Fullscreen ───────────────────────────────────────────────────────
        if (canFullscreen()) {
            setStatus('fa-ps-fs', 'pending');
            enterFullscreen();
            // Small delay to detect if it worked
            await new Promise(function (res) { setTimeout(res, 400); });
            setStatus('fa-ps-fs', isFullscreen() ? 'ok' : 'skip');
            watchFullscreen(); // start the re-enter guard
        } else {
            setStatus('fa-ps-fs', 'skip'); // iOS Safari doesn't support it
        }

        if (errMsg) {
            setError(errMsg);
            var btn = document.getElementById('fa-perm-btn');
            if (btn) {
                btn.disabled = false;
                btn.textContent = 'Try Again';
            }
            return;
        }

        // ── Done — resolve and tear down ─────────────────────────────────────
        await new Promise(function (res) { setTimeout(res, 400); });
        hideModal(overlay);
        _resolve(true); // kiosk.js can now start
    }

    // ─── Kick off when DOM is ready ──────────────────────────────────────────
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', bootstrap);
    } else {
        bootstrap();
    }

})();
