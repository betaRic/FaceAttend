# FaceAttend — Plan 4: kiosk.js Refactor
## Splitting a 2,000-Line File Into Focused Modules

**Status:** Final — aligned with Plan 1 (architecture), Plan 2 (enrollment pipeline), Plan 3 (backend C#)  
**Rule:** One phase per commit. Build and spot-test before each commit. Never combine phases.

---

## What This Plan Does NOT Touch

- `enrollment-core.js`, `enrollment-tracker.js`, `face-guide.js` — owned by Plan 2.
- `admin.js` — owned by Plan 3.
- `notify.js`, `fa-helpers.js`, `camera.js`, `api.js`, `facescan.js` — already focused files.
- Any C# server files — owned by Plans 1, 2, 3.
- `kiosk.css`, `fa-design-system.css` — owned by Plan 3 CSS phases.

---

## Current State — What kiosk.js Does

The file is one IIFE (~2,000 lines) with no module boundaries. It handles:

| Responsibility | Approx Lines | Description |
|---|---|---|
| **A — Config + DOM refs** | ~80 | `CFG` object, `ui` refs, `EP` endpoints |
| **B — Server warm-up polling** | ~25 | `pollServerReady()` — polls `/Health` until models load |
| **C — State object** | ~50 | Single `state` object with 40+ fields |
| **D — GPS + office resolution** | ~250 | `startGpsIfAvailable()`, `resolveOfficeIfNeeded()`, `resolveOfficeDesktopOnce()`, EMA smoothing, dead-band filter, drift detection |
| **E — Client-side office check** | ~80 | `resolveOfficeClientSide()` — Haversine math for mobile |
| **F — Idle map (Leaflet)** | ~300 | `initIdleMap()`, `updateIdleMap()`, `drawRoute()`, office marker/circle, user dot, cache |
| **G — Location state UI** | ~60 | `setLocationState()`, `applyLocationUi()` |
| **H — Device registration** | ~80 | `registerDevice()`, `doRegisterDevice()`, device token cookie read/write |
| **I — Visitor modal** | ~80 | `openVisitorModal()`, `closeVisitorModal()`, `submitVisitorForm()`, `wireVisitorUi()` |
| **J — Admin unlock modal** | ~150 | `openUnlock()`, `closeUnlock()`, `submitUnlock()`, `showUnlockSuccess()`, `wireUnlockUi()` |
| **K — Canvas overlay** | ~100 | `drawLoop()`, `drawCornerBrackets()`, `drawScanLine()`, `mapVideoBoxToCanvas()` |
| **L — MediaPipe face detection** | ~150 | `mp` object with `init()`, `tick()`, stable tracking |
| **M — Attendance submit** | ~150 | `captureFrameBlob()`, `submitAttendance()`, `handleAttendanceResponse()` |
| **N — Main loop + clock + ETA** | ~150 | `loop()`, `startClock()`, `updateEta()`, `localSenseLoop()` |
| **O — Fullscreen + visibility** | ~60 | `enterFullscreen()`, `initAutoFullscreen()`, page visibility handlers |
| **P — WebSocket fast preview** | ~80 | `initFastPreview()`, `doFastPreview()` — disabled, `CFG.fastPreview.enabled: false` |

---

## The Problems

### Problem 1 — State Pollution
The single `state` object has 40+ fields. GPS state, device state, face detection state, office state, and modal state are all mixed together. A change to any one area risks accidentally reading or writing a field owned by another area.

### Problem 2 — Impossible to Test
No function can be called in isolation. `handleAttendanceResponse()` reads from `state.liveInFlight`, `state.deviceStatus`, `state.submitInProgress`, `state.wasIdle` — all from the global state. You cannot test it without recreating the entire state machine.

### Problem 3 — Dead Code in Production
The WebSocket fast preview (`initFastPreview`, `doFastPreview`) is disabled via `CFG.fastPreview.enabled: false` but ships ~80 lines of live code to every browser. It creates a WebSocket constructor call attempt that always fails.

### Problem 4 — The Map Is Half the File
The Leaflet idle map (`F`) is ~300 lines, which is 15% of the entire file. It has its own cache (`_officesCache`), its own EMA state (`_gpsSmoothed`), its own marker references. It is a complete sub-application that happens to share the enclosing IIFE's scope.

### Problem 5 — GPS Logic Duplicated
GPS distance calculation exists in:
- `kiosk.js`: `gpsDistanceMeters()` (local function, ~10 lines)
- `Services/OfficeLocationService.cs`: `CalculateDistanceMeters()` (server, Haversine)

The JS version is correct but is a re-implementation of server logic with no shared test.

### Problem 6 — Admin unlock is 150 lines for one feature
The admin unlock modal (`J`) — PIN entry, cookie verification, success modal with two buttons — is embedded in the main kiosk file and mixed with face detection state. A change to the PIN UI requires reading the scan loop to avoid side effects.

---

## Target State — After Split

```
Scripts/
  kiosk.js                        ← Main loop only (~350 lines after split)
  kiosk/
    kiosk-config.js               ← CFG + EP + DOM ref builder
    kiosk-state.js                ← State factory, named sub-objects
    kiosk-clock.js                ← Clock + ETA display
    kiosk-location.js             ← GPS watch, office resolution, state transitions
    kiosk-map.js                  ← Leaflet idle map, route drawing, office cache
    kiosk-canvas.js               ← Canvas overlay, corner brackets, scan line
    kiosk-mediapipe.js            ← mp.init(), mp.tick(), stable tracking
    kiosk-attendance.js           ← captureFrameBlob(), submitAttendance(), handleAttendanceResponse()
    kiosk-visitor.js              ← Visitor modal, wire-up
    kiosk-unlock.js               ← Admin PIN unlock modal, wire-up
    kiosk-device.js               ← Device token cookie, registerDevice(), device state check
    kiosk-warmup.js               ← pollServerReady()
    kiosk-fullscreen.js           ← enterFullscreen(), visibility handlers
```

`kiosk.js` after split contains only: `init()`, the main `loop()`, wiring calls to each module's init function, and the final IIFE.

---

## Phase Breakdown

### Phase 4-A — Extract kiosk-config.js

**What moves:**
- The `CFG` object (all configuration constants)
- The `EP` endpoints object
- The `appBase` computation
- The `validateConfig()` function
- The DOM ref builder (`ui` object construction)

**What stays in kiosk.js:**
- The `var ui = { ... }` assignment is called from `kiosk-config.js` but returned to kiosk.js scope

**Result:** Any developer changing a timeout or endpoint URL opens one 60-line file, not 2,000.

**Risk:** Low. Pure extraction. The `CFG` and `EP` objects are read-only after creation.

---

### Phase 4-B — Extract kiosk-state.js

**What moves:**
The `state` object, split into named sub-objects:

```javascript
var KioskState = {
    server:   { ready: false },
    location: { state: 'pending', allowedArea: false, banner: '', title: '', sub: '',
                currentOffice: { id: null, name: null },
                lastResolveAt: 0, officeVerifiedUntil: 0, officeResolveRetryUntil: 0,
                lastVerifiedByGPS: false, lastVerifiedLat: null, lastVerifiedLon: null },
    gps:      { lat: null, lon: null, accuracy: null },
    device:   { status: 'unknown', checked: false },
    face:     { status: 'none', readyToFire: false, stableStart: 0, faceSeenAt: 0,
                boxCanvas: null, prevCenter: null, smoothedBox: null,
                rawCount: 0, acceptedCount: 0, scanLineProgress: 0,
                latestLiveness: null, livenessThreshold: 0.75 },
    scan:     { submitInProgress: false, liveInFlight: false, attendAbortCtrl: null,
                backoffUntil: 0, lastCaptureAt: 0, scanBlockUntil: 0, blockMessage: null },
    modal:    { unlockOpen: false, adminModalOpen: false, visitorOpen: false, pendingVisitor: null },
    idle:     { wasIdle: true },
    motion:   { diffNow: null, frameDiffs: [], localSeenAt: 0, localPresent: false }
};
```

**Why this matters:** `loop()` reads `state.liveInFlight`. After this change it reads `KioskState.scan.liveInFlight`. Any developer knows exactly which domain owns that flag.

**Risk:** Medium. Every `state.xxx` reference in the 2,000 lines must be updated to the new sub-object path. Use find-replace with verification — the old `state` name is retired completely.

---

### Phase 4-C — Extract kiosk-clock.js

**What moves:** `startClock()`, `nowText()`, `updateEta()`, `setEta()`

**Why separate:** These functions touch only DOM elements and `KioskState.location.state` / `KioskState.face`. They have no side effects on scan or GPS state.

**Risk:** Low.

---

### Phase 4-D — Extract kiosk-warmup.js + kiosk-fullscreen.js

**What moves:**
- `kiosk-warmup.js`: `pollServerReady()`
- `kiosk-fullscreen.js`: `enterFullscreen()`, `exitFullscreen()`, `isFullscreen()`, `initAutoFullscreen()`, the `pageshow` + `visibilitychange` + `focus` handlers

**Risk:** Low. These are self-contained.

---

### Phase 4-E — Extract kiosk-unlock.js

**What moves:** `openUnlock()`, `closeUnlock()`, `submitUnlock()`, `showUnlockSuccess()`, `closeUnlockSuccess()`, `goToAdmin()`, `stayInKiosk()`, `wireUnlockUi()`, `pendingReturnUrl`

**What it reads from shared state:** `KioskState.modal.unlockOpen`, `KioskState.modal.adminModalOpen`

**Interface contract:**
```javascript
// kiosk-unlock.js exports
KioskUnlock.init(uiRefs, stateRef, config)
KioskUnlock.isOpen()        // → boolean
KioskUnlock.open()
KioskUnlock.close()
```

**Risk:** Low. The unlock modal is fully isolated from scan and GPS logic. The only shared state is two boolean flags.

---

### Phase 4-F — Extract kiosk-visitor.js

**What moves:** `openVisitorModal()`, `closeVisitorModal()`, `submitVisitorForm()`, `wireVisitorUi()`

**Interface contract:**
```javascript
KioskVisitor.init(uiRefs, stateRef, config)
KioskVisitor.isOpen()       // → boolean
KioskVisitor.open(payload)
KioskVisitor.close()
```

**Risk:** Low. Same pattern as unlock.

---

### Phase 4-G — Extract kiosk-device.js

**What moves:**
- `getCookieValue()`, `isForcedKioskMode()`
- `getDeviceToken()`, `setDeviceToken()`, `clearDeviceToken()`
- `registerDevice()`, `doRegisterDevice()`
- `checkCurrentMobileDeviceState()`
- `getMobileRegisterBtn()`, `setMobileRegisterVisible()`
- The `DEVICE_TOKEN_KEY` and `DEVICE_TOKEN_COOKIE` constants

**Why separate:** Device token management is a complete mini-service. It reads cookies, writes cookies, calls `MobileRegistration/RegisterDevice`, and manages a single UI button. None of this touches face detection.

**Risk:** Low-medium. `doRegisterDevice()` calls `toast()` and `setPrompt()` — these become parameters or injected callbacks.

---

### Phase 4-H — Extract kiosk-location.js

**What moves:**
- `startGpsIfAvailable()`
- `resolveOfficeIfNeeded()`
- `resolveOfficeDesktopOnce()`
- `resolveOfficeClientSide()`
- `gpsDistanceMeters()`
- `humanizeResolveError()`
- `setLocationState()`
- `applyLocationUi()`
- GPS EMA state: `_gpsSmoothed`, `_lastProcessedGps`
- Office cache: `_officesCache`, `_officesCacheExpiry`, `_officesFetching`, `fetchOfficesOnce()`

**Interface contract:**
```javascript
KioskLocation.init(stateRef, config, uiRefs)
KioskLocation.start()           // begins GPS watch
KioskLocation.resolveIfNeeded() // called from main loop
KioskLocation.getState()        // → 'pending' | 'allowed' | 'blocked'
```

**Why this is the largest move:** GPS, office cache, and client-side Haversine check are tightly coupled but entirely independent of face detection. After extraction, the main `loop()` calls `KioskLocation.resolveIfNeeded()` — one line, no GPS details visible.

**Risk:** Medium. Many read/write paths to `state.location.*` and `state.gps.*`. Verify compile-equivalent (no JS errors) and test location flow end-to-end after this phase.

---

### Phase 4-I — Extract kiosk-map.js

**What moves:**
- `initIdleMap()`
- `updateIdleMap()`
- `drawRoute()`
- All `_idleLeafletMap`, `_idleUserMarker`, `_idleOfficeCircles`, `_idleRouteLayer`, `_idleNearestOffice`, `_idleMapBoundsFitted`, `_idleMapVisible` private state
- The `ui.idleMapContainer` and `ui.idleMap` reference usage

**Dependency on Phase 4-H:** `kiosk-map.js` calls `fetchOfficesOnce()` — this must be a parameter:
```javascript
KioskMap.init(mapEl, containerEl, fetchOfficesFn)
KioskMap.update(gpsLat, gpsLon, gpsAccuracy, locationState)
KioskMap.hide()
```

**Why separate from location:** The map is a pure display concern. Location state transitions (`setLocationState`) determine whether the map shows, but the map itself knows nothing about why.

**Risk:** Medium. Leaflet map must be invalidated on container resize and on first show — verify map renders correctly after split.

---

### Phase 4-J — Extract kiosk-mediapipe.js

**What moves:**
- The `mp` object: `mp.init()`, `mp.tick()`, `mp.vision`, `mp.detector`, `mp.failStreak`
- `updateStableTracking()`
- `isTooSmallFaceNorm()`
- `toVideoBox()`
- `boxFullyVisibleCanvas()`

**Interface contract:**
```javascript
KioskMediapipe.init(videoEl, config) // → Promise
KioskMediapipe.tick(canvasState)     // updates canvasState.faceBox, canvasState.faceStatus
KioskMediapipe.isReady()             // → boolean
```

**Risk:** Medium. `mp.tick()` currently writes directly to `state.mpBoxCanvas`, `state.faceStatus`, etc. After split, it returns a result object that the main loop applies to `KioskState.face`.

---

### Phase 4-K — Extract kiosk-canvas.js

**What moves:**
- `drawLoop()` — the `requestAnimationFrame` draw loop
- `drawCornerBrackets()`
- `drawScanLine()`
- `resizeCanvas()`
- `mapVideoBoxToCanvas()`

**Interface contract:**
```javascript
KioskCanvas.init(videoEl, canvasEl)
KioskCanvas.start()   // begins rAF loop
KioskCanvas.stop()
```

**The canvas loop reads:** `KioskState.face.boxCanvas`, `KioskState.scan.liveInFlight`, `KioskState.face.status`, `ui.livenessLine.className` — all passed as a read-only snapshot or as a getter callback.

**Risk:** Low-medium. The rAF loop must not hold a stale reference to state after a refactor. Pass a `getState` callback rather than a direct state reference.

---

### Phase 4-L — Extract kiosk-attendance.js

**What moves:**
- `captureFrameBlob()`
- `getFaceBoxForServer()`
- `submitAttendance()`
- `handleAttendanceResponse()`
- `armPostScanHold()`
- The `captureCanvas` and `captureCtx` locals
- `CAPTURE_W`, `CAPTURE_H` constants (move to config)

**Interface contract:**
```javascript
KioskAttendance.init(videoEl, stateRef, config, callbacks)
// callbacks: { onSuccess, onTooSoon, onVisitor, onDeviceAction, onError }
KioskAttendance.capture()   // → Promise<Blob>
KioskAttendance.submit(blob) // → Promise
KioskAttendance.isInFlight() // → boolean
```

**Why this is the most important extraction:** `handleAttendanceResponse()` is currently 150 lines with 12 different response paths. Each path calls `toast()`, `setPrompt()`, `armPostScanHold()`, and modifies `state` directly. After extraction, each path fires a named callback. The callbacks in `kiosk.js` update shared state. The response handler becomes testable.

**Risk:** High. Most business logic is here. Extract last, after all other modules are stable. Test every response code: success, TOO_SOON, LIVENESS_FAIL, NOT_RECOGNIZED, REGISTER_DEVICE, DEVICE_PENDING, DEVICE_BLOCKED, VISITOR, SCAN_CONFIRM_NEEDED, RATE_LIMIT_EXCEEDED.

---

### Phase 4-M — Delete Dead Code

**What is deleted:**
- `initFastPreview()` — ~40 lines. `CFG.fastPreview.enabled` is `false`. This code has never run in production. If WebSocket preview is ever built, it will be a separate feature file.
- `doFastPreview()` — ~40 lines. Same reason.
- `CFG.fastPreview` config block — 5 lines.

**Risk:** Zero. This code is unreachable by configuration.

---

### Phase 4-N — Slim kiosk.js

After all extractions, `kiosk.js` contains only:

```javascript
(function () {
    'use strict';

    var ui     = KioskConfig.buildUiRefs();
    var config = KioskConfig.get();

    KioskState.init();
    KioskClock.init(ui);
    KioskWarmup.init(config, KioskState.server);
    KioskUnlock.init(ui, KioskState.modal, config);
    KioskVisitor.init(ui, KioskState.modal, config);
    KioskDevice.init(ui, KioskState.device, config);
    KioskLocation.init(KioskState.location, KioskState.gps, config, ui);
    KioskMap.init(ui.idleMap, ui.idleMapContainer, KioskLocation.fetchOfficesOnce);
    KioskCanvas.init(ui.kioskVideo, document.getElementById('overlayCanvas'));
    KioskMediapipe.init(ui.kioskVideo, config).then(function () {
        KioskCanvas.start();
        localSenseLoop();
        loop();
    }).catch(function (e) { /* handle */ });

    KioskLocation.start();
    startCamera().then(function () { KioskWarmup.start(); });

    function loop() {
        // ~80 lines: read module states, decide to scan, call KioskAttendance.submit()
    }

})();
```

Target: `kiosk.js` under 200 lines after all phases.

---

## Load Order in Layout

```html
<!-- Modules load before kiosk.js -->
<script src="~/Scripts/kiosk/kiosk-config.js"></script>
<script src="~/Scripts/kiosk/kiosk-state.js"></script>
<script src="~/Scripts/kiosk/kiosk-clock.js"></script>
<script src="~/Scripts/kiosk/kiosk-warmup.js"></script>
<script src="~/Scripts/kiosk/kiosk-fullscreen.js"></script>
<script src="~/Scripts/kiosk/kiosk-unlock.js"></script>
<script src="~/Scripts/kiosk/kiosk-visitor.js"></script>
<script src="~/Scripts/kiosk/kiosk-device.js"></script>
<script src="~/Scripts/kiosk/kiosk-location.js"></script>
<script src="~/Scripts/kiosk/kiosk-map.js"></script>
<script src="~/Scripts/kiosk/kiosk-canvas.js"></script>
<script src="~/Scripts/kiosk/kiosk-mediapipe.js"></script>
<script src="~/Scripts/kiosk/kiosk-attendance.js"></script>
<script src="~/Scripts/kiosk.js"></script>
```

These should be bundled into one `~/bundles/kiosk` bundle in `BundleConfig.cs` — no HTTP overhead, same as today.

---

## Phase Summary Table

| Phase | Files Changed | Risk | What You Can Test Immediately |
|---|---|---|---|
| 4-A | Extract kiosk-config.js | Low | Config loads, no console errors |
| 4-B | Extract kiosk-state.js | Medium | All `state.*` refs updated, no undefined errors |
| 4-C | Extract kiosk-clock.js | Low | Clock displays, ETA updates |
| 4-D | Extract kiosk-warmup.js, kiosk-fullscreen.js | Low | Warmup polls, fullscreen works |
| 4-E | Extract kiosk-unlock.js | Low | PIN modal opens/closes, admin redirect works |
| 4-F | Extract kiosk-visitor.js | Low | Visitor modal opens/closes, submission works |
| 4-G | Extract kiosk-device.js | Low-medium | Device registration, token cookie |
| 4-H | Extract kiosk-location.js | Medium | GPS watch, office resolution, location UI |
| 4-I | Extract kiosk-map.js | Medium | Idle map renders, route draws, office markers |
| 4-J | Extract kiosk-mediapipe.js | Medium | Face detection box visible, stable tracking |
| 4-K | Extract kiosk-canvas.js | Low-medium | Canvas overlay draws correctly |
| 4-L | Extract kiosk-attendance.js | High | Full scan cycle, all response codes |
| 4-M | Delete fast preview dead code | Zero | Nothing changes at runtime |
| 4-N | Slim kiosk.js | Low | Full end-to-end kiosk flow |

---

## What This Plan Does Not Cover

**Bundle optimization:** `BundleConfig.cs` — addressed in Plan 3 if needed.  
**kiosk.css cleanup:** Owned by Plan 3.  
**Server-side attendance scan pipeline:** Owned by Plans 1 and 2.  
**kiosk.js → ES module conversion:** Not in scope. The codebase uses IIFE + `window.*` globals. Converting to ES modules is a larger architectural change that would require changes to the MVC bundle pipeline.
