// kiosk-fullscreen.js
// Fullscreen entry/exit helpers and auto-fullscreen on load.
// Also handles page visibility changes (camera resume after bfcache restore).
// Exposes window.KioskFullscreen — no dependencies beyond the browser APIs.
(function () {
    'use strict';

    function enterFullscreen() {
        var elem = document.documentElement;
        if (elem.requestFullscreen)        return elem.requestFullscreen();
        if (elem.webkitRequestFullscreen)  return elem.webkitRequestFullscreen();
        if (elem.msRequestFullscreen)      return elem.msRequestFullscreen();
        return Promise.reject('Fullscreen not supported');
    }

    function exitFullscreen() {
        if (document.exitFullscreen)        return document.exitFullscreen();
        if (document.webkitExitFullscreen)  return document.webkitExitFullscreen();
        if (document.msExitFullscreen)      return document.msExitFullscreen();
        return Promise.reject('Fullscreen not supported');
    }

    function isFullscreen() {
        return !!(document.fullscreenElement ||
                  document.webkitFullscreenElement ||
                  document.msFullscreenElement);
    }

    function initAutoFullscreen() {
        var tryFullscreen = function () {
            if (!isFullscreen()) {
                enterFullscreen().catch(function () {});
            }
        };

        tryFullscreen();

        var onFirstInteraction = function () {
            if (!isFullscreen()) {
                enterFullscreen().catch(function () {});
            }
            document.removeEventListener('click',      onFirstInteraction);
            document.removeEventListener('touchstart', onFirstInteraction);
        };

        document.addEventListener('click',      onFirstInteraction);
        document.addEventListener('touchstart', onFirstInteraction);

        document.addEventListener('fullscreenchange', function () {
            // Auto-reenter disabled to allow admin escape
        });
    }

    // Wires up visibility/pageshow/focus handlers to resume camera when returning from admin.
    // Call this after the video element is available.
    function initVisibilityHandling(video, startCamera) {
        function resumeCameraIfNeeded() {
            if (video.paused || video.ended) {
                video.play().catch(function () {
                    startCamera().catch(function () {});
                });
            }
        }

        document.addEventListener('visibilitychange', function () {
            if (!document.hidden) resumeCameraIfNeeded();
        });

        window.addEventListener('pageshow', function (e) {
            if (e.persisted) resumeCameraIfNeeded();
        });

        window.addEventListener('focus', function () {
            if (video.paused) resumeCameraIfNeeded();
        });
    }

    window.KioskFullscreen = {
        enterFullscreen:         enterFullscreen,
        exitFullscreen:          exitFullscreen,
        isFullscreen:            isFullscreen,
        initAutoFullscreen:      initAutoFullscreen,
        initVisibilityHandling:  initVisibilityHandling
    };
})();
