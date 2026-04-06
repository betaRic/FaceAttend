// kiosk-state.js
// Centralized mutable runtime state for the kiosk.
// Exposes window.KioskState — must be loaded before kiosk.js.
(function () {
    'use strict';

    var ua             = navigator.userAgent || '';
    var isMobile       = /Android|iPhone|iPad|iPod|Mobile|Tablet/i.test(ua);
    var isPersonalMobile = /iPhone|iPod|Windows Phone|IEMobile|BlackBerry|Android.+Mobile/i.test(ua);
    var allowUnlock    = (document.body.getAttribute('data-allow-unlock') || 'false') === 'true';
    var pageLoadTime   = Date.now();

    var state = {
        unlockOpen:       false,
        adminModalOpen:   false,
        serverReady:      false,
        lastVerifiedByGPS: false,
        wasIdle:          true,
        visitorOpen:      false,
        pendingVisitor:   null,
        scanBlockUntil:   0,
        blockMessage:     null,
        submitInProgress: false,
        deviceStatus:     'unknown',
        deviceChecked:    false,
        consecutiveFailures: 0,

        gps:             { lat: null, lon: null, accuracy: null },
        allowedArea:     false,
        locationState:   'pending',
        locationBanner:  'Checking location...',
        locationTitle:   'Preparing kiosk',
        locationSub:     'Please wait while the kiosk verifies the current office location.',
        currentOffice:   { id: null, name: null },
        lastResolveAt:   0,
        officeVerifiedUntil:      0,
        officeResolveRetryUntil:  0,
        lastVerifiedLat:  null,
        lastVerifiedLon:  null,
        backoffUntil:    0,
        lastCaptureAt:   0,

        mpMode:          'none',
        mpReadyToFire:   false,
        mpStableStart:   0,
        mpFaceSeenAt:    0,
        faceStatus:      'none',
        mpRawCount:      0,
        mpAcceptedCount: 0,
        mpBoxCanvas:     null,
        mpPrevCenter:    null,
        smoothedBox:     null,

        latestLiveness:    null,
        livenessThreshold: 0.75,

        motionDiffNow:   null,
        frameDiffs:      [],

        liveInFlight:    false,
        attendAbortCtrl: null,

        localSeenAt:     0,
        localPresent:    false,

        scanLineProgress: 0,

        // Fast preview (WebSocket, disabled by default)
        fastWs:              null,
        fastPreviewLastAt:   0,
        fastPreviewResult:   null,
        fastPreviewScanning: false,
        fastPreviewFailCount: 0,

        // Idle map
        idleMap:       null,
        idleMapLayer:  null,
        idleMapMarkers: [],
    };

    window.KioskState = {
        state:           state,
        ua:              ua,
        isMobile:        isMobile,
        isPersonalMobile: isPersonalMobile,
        allowUnlock:     allowUnlock,
        pageLoadTime:    pageLoadTime
    };
})();
