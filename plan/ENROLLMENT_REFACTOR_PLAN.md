# FaceAttend Enrollment System — Complete Refactor Plan
## From Audit to Implementation: Every File, Every Line, Every Decision

---

# PART 1 — CURRENT STATE (What We Have Right Now)

## 1.1 The Core Problem In One Paragraph

The enrollment system was built incrementally. Admin enrollment was built first, then mobile enrollment was added as a copy-paste of the admin code with minor adjustments. Then more JS files were added for visual improvements. Each addition was made without removing what came before. The result is three separate JavaScript enrollment engines running simultaneously on the same page, two server endpoints doing the same thing with one skipping all security, threshold values hardcoded in five different places with five different numbers, and a visual guide that draws from a completely different AI model than the one that actually decides if a frame is good. The system works most of the time, but it is unreliable, unmaintainable, has a real security hole, and the code cannot be understood without reading all files simultaneously.

---

## 1.2 Complete File Inventory

### JavaScript Files (Client-Side)

#### `Scripts/modules/enrollment-core.js`
**What it is:** The main enrollment engine. This is the most important file.  
**What it does:**
- Defines the `FaceAttend.Enrollment` class with `create(config)` factory
- Manages camera access via `startCamera(videoEl)`
- Runs the main capture tick loop every 200ms via `_runOneTick()`
- Client-side quality gates: sharpness (Laplacian variance), brightness (mean gray), face area ratio, centering
- Sends JPEG frames to the server via `postScanFrame(blob)`
- Processes server responses via `processScanResult(result)`
- Manages the good-frames pool: `goodFrames[]`, max 30, quality-based replacement
- Quality formula: `liveness * 0.7 + normalizedSharpness * 0.3` — no face area
- Fires `onReadyToConfirm` when `minGoodFrames` is reached
- Sends all collected JPEG blobs to the enroll endpoint via `performEnrollment()`
- Contains a full adaptive fallback: after 20 consecutive zero-liveness frames, accepts frames based only on encoding quality (this exists to handle liveness model failures)
- Has a full upload-from-files path: `enrollFromFiles(files)`
- Exposes public API: `startAutoEnrollment()`, `stopAutoEnrollment()`, `stopCamera()`, `getEncodings()`, `describeEnrollError(result)`

**What is wrong with it:**
- `CONSTANTS` block at top has 18 hardcoded values — some match DB, some do not
- `MIN_GOOD_FRAMES: 25` but both inline scripts override this to 5 — the constant is meaningless
- `SHARPNESS_THRESHOLD_DESKTOP: 40` but DB has `Biometrics:Enroll:SharpnessThreshold: 80` — client accepts frames server will reject
- `MIN_FACE_AREA_RATIO_DESKTOP: 0.15` but DB has `Biometrics:Enroll:MinFaceAreaRatio: 0.08` — mismatch
- Quality formula ignores face area — larger faces produce better embeddings
- Reads `poseBucket`, `poseYaw`, `posePitch`, `landmarks` from server response but uses none of them
- `window.FaceAttendEnrollment = enroll` is assigned from outside before callbacks are wired
- `AUTO_CONFIRM_TIMEOUT_MS: 15000` — a 15 second forced auto-confirm that surprises users

#### `Scripts/enrollment-tracker.js`
**What it is:** MediaPipe BlazeFace real-time face detection at 60fps.  
**What it does:**
- Loads MediaPipe Tasks Vision WASM from `Scripts/vendor/mediapipe/`
- Creates a `FaceDetector` running in `VIDEO` mode
- On every animation frame, calls `detector.detectForVideo(video, timestamp)`
- Maps the BlazeFace bounding box from normalized [0,1] space to canvas CSS pixels
- Applies EMA smoothing (alpha 0.35) to the bounding box for smooth animation
- Calls `FaceAttend.FaceGuide.draw()` on every frame to draw the oval
- Writes `window.FaceAttendEnrollment.liveTrackingBox` — used by enrollment-core for centering gate
- Writes `window.FaceAttendEnrollment.liveFaceArea` — used by enrollment-core for area gate
- Calls `applyFocusPoint()` to nudge the camera's hardware focus toward the face
- Calls `enhanceCameraFocus()` to set continuous autofocus, auto white balance, auto exposure

**What is wrong with it:**
- `active` is set `false` only in `window.beforeunload` — never stops when user clicks Back
- No public `stop()` function exposed
- Depends on `face-guide.js` being loaded before it — tight coupling
- The oval it draws is from BlazeFace bounding box — completely different model from Dlib which makes the actual pass/fail decision
- Does not draw the server `faceBox` at all — the box that matters is never shown

#### `Scripts/enrollment-ui.js`
**What it is:** A wrapper that was meant to be the single enrollment UI controller.  
**What it does:**
- Checks for `document.getElementById('enrollRoot')` at the top
- Neither `Enroll.cshtml` nor `Enroll-mobile.cshtml` has an element with id `enrollRoot`
- Immediately returns on the first line — executes zero code
- Is loaded on both pages consuming parse + execute time doing nothing

**What is wrong with it:** It is 100% dead code. It has never executed on any page.

#### `Scripts/core/facescan.js`
**What it is:** A complete standalone face scanning module.  
**What it does:**
- Defines `window.FaceAttend.FaceScan` with `init()`, `start()`, `stop()`, `pause()`, `resume()`
- Has its own frame capture loop with diversity-aware capture
- Has its own `_handleScanResult()`, `_checkComplete()`, `_triggerComplete()`
- Uses `FaceAttend.API.scanFrame()` for server communication

**What is wrong with it:** It is never called from any enrollment page, any view, or any other script. `FaceAttend.FaceScan` is defined and immediately orphaned.

#### `Scripts/modules/face-guide.js`
**What it is:** The oval guide drawing module.  
**What it does:**
- Defines `FaceAttend.FaceGuide` with `getState()` and `draw()` functions
- `getState()` takes face area ratio and face box position, returns `'none' | 'too_close' | 'too_far' | 'off_center' | 'good'`
- `draw()` draws the oval using `ctx.ellipse()` with a dark vignette punch-through effect
- The oval position is hardcoded: `CX_RATIO: 0.50`, `CY_RATIO: 0.49`, `RX_RATIO: 0.20`, `ASPECT: 0.85`
- Called by `enrollment-tracker.js` on every animation frame

**What is wrong with it:**
- The oval is drawn from the BlazeFace bounding box (MediaPipe), which is a completely different model than Dlib HOG which the server uses for actual face detection and encoding
- A face can be perfectly inside the green oval while Dlib returns `NO_FACE` — opposite is also possible
- The oval gives the user false confidence about what the server will actually accept
- The punch-through vignette effect obscures the face while the user is trying to align — counterproductive

#### `Scripts/core/camera.js`
**What it is:** A camera abstraction module.  
**What it does:**
- Defines `FaceAttend.Camera` with `start()`, `stop()`, `capture()`, `captureAsBlob()`, `isActive()`, `getDimensions()`, `pause()`, `resume()`
- Wraps `navigator.mediaDevices.getUserMedia()` with proper error message mapping
- Used by enrollment-core when the high-level camera API is available

**Status:** KEEP. This is well-written and serves a real purpose.

#### `Scripts/core/fa-helpers.js`
**What it is:** DOM utilities.  
**What it does:** `FaceAttend.Utils` — `$()`, `$$()`, `el()`, `debounce()`, `throttle()`, `getCsrfToken()`, `fetchJson()`, `isMobile()`, `isDark()`, etc.

**Status:** KEEP. Solid utility module.

#### `Scripts/core/notify.js`
**What it is:** Toast and modal notification manager.  
**Status:** KEEP. Clean and functional.

#### `Scripts/core/api.js`
**What it is:** API client wrapper for server communication.  
**Status:** KEEP. Used by enrollment-core.

### Inline JavaScript (Embedded in Views)

#### `Views/Areas/Admin/Employees/Enroll.cshtml` — Inline Script
**Size:** Approximately 320 lines of JavaScript inside `@section scripts`  
**What it does:**
- Creates its own `enroll` instance: `FaceAttend.Enrollment.create({...})`
- Wires wizard step navigation: Method → Live/Upload → Done
- Defines `startCamera()` which guards against double-start with `_cameraStarted`
- Defines `startFaceOverlay()` — starts a **second rAF loop** on `#enrollFaceCanvas`
- This second loop assigns `overlayCanvas.width = cw` every frame — clears the canvas every frame (browser spec: assigning `.width` always resets canvas even if value unchanged)
- Wires `onReadyToConfirm` callback which shows Swal preview dialog
- Wires `onEnrollmentComplete` to navigate to success pane
- Defines the upload pane: dropzone, file cards, `processUpload()`
- `MIN_FRAMES = 5` — overrides enrollment-core's `minGoodFrames: 25`
- Sets `window.FaceAttendEnrollment = enroll` early, before callbacks are wired

#### `Views/MobileRegistration/Enroll-mobile.cshtml` — Inline Script
**Size:** Approximately 280 lines of JavaScript inside `@section scripts`  
**What it does:**
- Creates its own `enroll` instance (third instance on mobile)
- Same `startFaceOverlay()` with the same double-rAF bug
- Wizard: Details form → Face capture → Review → Submit
- On submit, collects `enroll.goodFrames`, extracts `encoding` strings from each frame, posts to `MobileRegistration/SubmitEnrollment`
- **CRITICAL BUG:** Posts the client-side `encoding` strings directly — these are base64 representations of the 128d vector that came back from the server. Mobile submit trusts these and saves them directly with no re-processing.
- `MIN_FRAMES = 5` — same override
- Form validation: regex patterns for each field, uppercase normalization

### Server-Side Controllers

#### `Controllers/Api/ScanController.cs` — `POST /api/scan/frame`
**What it does:**
- Validates image (size, format via magic bytes)
- Builds client face box if `faceX/Y/W/H` params provided (skips server-side HOG detection — saves 200-500ms)
- Runs `FastScanPipeline.EnrollmentScanInMemory()`:
  - Loads JPEG → Bitmap (once)
  - Parallel: ONNX MiniFASNet liveness + Dlib HOG detect + ResNet 128d encode + 68-point landmarks
  - Sharpness from Laplacian variance on face crop
- Estimates pose (yaw/pitch) from 68-point landmarks using `FaceQualityAnalyzer`
- Computes `poseBucket`: "center", "left", "right", "up", "down"
- Returns: `liveness`, `livenessOk`, `sharpness`, `sharpnessOk`, `encoding`, `poseYaw`, `posePitch`, `poseBucket`, `landmarks`, `faceBox`, `timingMs`
- **What is wrong:** Returns `poseYaw`, `posePitch`, `poseBucket`, `landmarks` — none of these are used by the client in enrollment. Network overhead for zero benefit.

#### `Controllers/MobileRegistration/MobileRegistrationController.cs` — `POST /MobileRegistration/ScanFrame`
**What it does:**
- Identical pipeline to `ScanController.Frame()` — calls `FastScanPipeline.EnrollmentScanInMemory()`
- Adds mobile-specific logic: `poseYaw *= 0.8f; posePitch *= 0.8f` (20% reduction)
- Different sharpness threshold: uses `isMobile` flag for lower threshold
- Returns same response structure as ScanController
- **What is wrong:** This is a complete duplicate of ScanController. The mobile-specific liveness threshold and mobile flag already exist in ScanController via `DeviceService.IsMobileDevice(Request)`. The only difference (20% pose angle reduction) is for a value the client never uses anyway.

#### `Controllers/Api/EnrollmentController.cs` — `POST /api/enrollment/enroll`
**What it does:**
- Receives: `employeeId` + multiple JPEG files (`images` form field)
- Validates employee exists in DB
- Parallel processes all frames (max 4 workers):
  - Detects face from JPEG (Dlib HOG) — re-detects, does not trust any client data
  - Checks face area ratio (min 0.08 desktop, 0.05 mobile)
  - Checks sharpness (min 80 desktop, 50 mobile from DB)
  - Parallel: ONNX liveness + Dlib encode + landmarks
  - Filters by liveness threshold, pose angle limits (±45° yaw, ±55° pitch)
  - Computes quality score per frame
- Runs `SelectDiverseByEmbedding()` — greedy max-spread selection
- Runs `EnrollmentQualityGate.Validate()` — checks spread, self-consistency
- Runs `DuplicateCheckHelper.FindDuplicate()` — scans all active employees at strict tolerance 0.45
- DPAPI encrypts each vector: `BiometricCrypto.ProtectBase64Bytes()`
- Saves `FaceEncodingBase64` (primary, encrypted) and `FaceEncodingsJson` (all vectors, encrypted)
- Updates `FastFaceMatcher` in-memory cache
- Invalidates `EmployeeFaceIndex` for BallTree rebuild
- **Status:** This is correct. This is the gold standard. Everything should route through here.

#### `Controllers/MobileRegistration/MobileRegistrationController.cs` — `POST /MobileRegistration/SubmitEnrollment`
**What it does:**
- Receives: employee form data (name, office, etc.) + `vm.FaceEncoding` (base64 string) + `vm.AllFaceEncodingsJson` (JSON array of base64 strings)
- These encoding strings are what the client received back from the scan endpoint and stored locally — they are NOT re-processed from JPEGs
- Runs `DuplicateCheckHelper.FindDuplicate()` (this part is correct)
- Saves directly: `emp.FaceEncodingBase64 = Convert.ToBase64String(faceTemplate)` — **raw base64, no DPAPI encryption**
- Saves `emp.FaceEncodingsJson = vm.AllFaceEncodingsJson` — **trusts client-provided JSON, no DPAPI**
- **SECURITY BUGS:**
  1. Face vectors stored without DPAPI encryption — at-rest protection absent
  2. Trusts client-provided encoding strings — no server re-validation of the actual face from JPEG
  3. No `EnrollmentQualityGate.Validate()` — no quality assurance
  4. No greedy diversity selection — stores whatever the client sent
  5. No sharpness or liveness re-check on the server

### Server-Side Services

#### `Services/Biometrics/FastScanPipeline.cs`
**What it does:**
- `EnrollmentScanInMemory()` — single JPEG → detect → parallel liveness+encode+landmarks → sharpness
- `ScanInMemory()` — same for attendance scanning
- Called by both ScanController and MobileRegistrationController (correctly)
- Called by EnrollmentController for each frame (correctly)
- **Status:** KEEP. This is well-implemented.

#### `Services/Biometrics/EnrollmentQualityGate.cs`
**What it does:**
- `Validate(List<EnrollCandidate> selected)`:
  - Checks minimum vector count
  - Checks max pairwise distance (spread — ensures diversity)
  - Checks min pairwise distance (self-consistency — rejects multi-person sets)
  - Checks self-match (best vector vs all others must be close enough)
  - Checks average quality score
- **Status:** KEEP. Called only from EnrollmentController — never from mobile submit (bug).

#### `Services/Biometrics/DuplicateCheckHelper.cs`
**What it does:**
- Scans all active employees' stored vectors using Dlib distance
- Returns employee ID of anyone matching within strict tolerance (0.45)
- Excludes the current employee being enrolled
- **Status:** KEEP. Called correctly from EnrollmentController. Also called (correctly) from SubmitEnrollment, but the rest of SubmitEnrollment is broken.

#### `Services/Biometrics/FaceQualityAnalyzer.cs`
**What it does:**
- `CalculateSharpnessFromBitmap()` — Laplacian variance on face ROI
- `EstimatePoseFromLandmarks()` — yaw/pitch from 68-point landmark array
- `EstimatePose()` — fallback yaw/pitch from face box geometry
- `GetPoseBucket()` — maps yaw/pitch to "center/left/right/up/down"
- `CalculateQualityScore()` — weighted: liveness 40%, sharpness 30%, area 20%, pose 10%
- `GetSharpnessThreshold()` — reads DB: 80 desktop, 50 mobile
- **Status:** KEEP. Note: `CalculateQualityScore()` on the server includes face area (20%), but the client's equivalent formula does not include area at all.

#### `Services/Biometrics/DlibBiometrics.cs`
**What it does:**
- Pool of `FaceRecognition` instances (default 4, configurable)
- `TryDetectSingleFaceFromBitmap()` — Dlib HOG, returns FaceBox + Location
- `TryEncodeFromBitmapWithLocation()` — ResNet 128d encoding
- `TryEncodeWithLandmarksFromBitmap()` — encode + 68-point landmarks in one pool rent
- `TryEncodeWithLandmarksFromRgbData()` — same but from pre-extracted RGB bytes (for parallel use)
- `ExtractRgbData()` — converts Bitmap to RGB byte array for thread-safe parallel processing
- **Status:** KEEP. Core biometric engine.

#### `Services/Biometrics/OnnxLiveness.cs`
**What it does:**
- Singleton ONNX session for MiniFASNet anti-spoofing model
- `ScoreFromFile()` and `ScoreFromBitmap()` — returns probability [0,1]
- Circuit breaker: after N consecutive failures, blocks requests for M seconds
- Multi-scale crop support via `Biometrics:Liveness:MultiCropScales` config
- **Status:** KEEP. Well-implemented with proper circuit breaker.

#### `Services/Biometrics/BiometricCrypto.cs`
**What it does:**
- DPAPI LocalMachine scope encryption for face vectors stored in DB
- `ProtectString()`, `TryUnprotectString()`, `ProtectBase64Bytes()`, `TryGetBytesFromStoredBase64()`
- Prefix `"dpapi1:"` on all encrypted values — used to detect legacy plaintext
- **Status:** KEEP. This is the at-rest protection that mobile path bypasses.

### Configuration (Thresholds — Current State)

```
KEY                                         DB DEFAULT    JS CONSTANT       MISMATCH?
────────────────────────────────────────────────────────────────────────────────────
Biometrics:LivenessThreshold                0.75          0.65 (core)       YES — 0.10 gap
Biometrics:MobileLivenessThreshold          0.68          0.75 (mobile cshtml) YES
Biometrics:Enroll:SharpnessThreshold        80.0          40 (core CONST)   YES — 2x gap
Biometrics:Enroll:SharpnessThreshold:Mobile 50.0          15 (core CONST)   YES — 3x gap
Biometrics:Enroll:CaptureTarget             8             25 (core CONST)   YES — client over-collects
Biometrics:Enroll:MaxStoredVectors          5             5 (ok)            OK
Biometrics:Enroll:MinFaceAreaRatio          0.08          0.15 (core CONST) YES — client gates too aggressively
Biometrics:Enroll:MinFaceAreaRatio:Mobile   0.05          0.12 (core CONST) YES
Biometrics:EnrollmentStrictTolerance        0.45          not in JS         OK (server only)
Biometrics:AttendanceTolerance              0.50          not in JS         OK (server only)
```

**The practical impact of these mismatches:**
- Client sharpness gate (40) accepts frames that server (80) will reject → wasted frames in pool
- Client area gate (0.15) rejects frames that server (0.08) would accept → client too strict, user must move very close
- Client liveness (0.65) accepts frames that server re-check (0.75) may reject → wasted frames
- Client collects 25 frames, server only stores 5 vectors → 20 frames are re-processed and discarded

---

# PART 2 — TARGET STATE (What We Want)

## 2.1 Design Principles

1. **Server is authoritative.** All thresholds live in the database. The client reads them once on load and uses them. No hardcoded threshold in JavaScript.

2. **One scan endpoint.** Both admin and mobile use `POST /api/scan/frame`. Mobile detection happens via `DeviceService.IsMobileDevice()` which already works correctly.

3. **One enroll endpoint.** Both admin and mobile use `POST /api/enrollment/enroll`. Mobile path is identical to admin path. No exceptions, no shortcuts, no security bypasses.

4. **One canvas owner.** The canvas has exactly one rAF loop. `enrollment-tracker.js` owns it. Nothing else draws on `#enrollFaceCanvas`.

5. **One enrollment engine.** `enrollment-core.js` is the only enrollment logic. The two views provide minimal configuration and page-specific DOM wiring only.

6. **Draw what matters.** The visual guide shows the server's `faceBox` — not an oval from a different model. The color reflects the actual server-confirmed frame count.

7. **Encrypted at rest, always.** All face vectors go through `BiometricCrypto` before touching the database. No exceptions for mobile.

## 2.2 Final File Structure (After Refactor)

```
Scripts/
├── core/
│   ├── camera.js           KEEP — no changes
│   ├── fa-helpers.js       KEEP — no changes
│   ├── notify.js           KEEP — no changes
│   └── api.js              KEEP — no changes
├── modules/
│   ├── enrollment-core.js  REWRITE — the complete enrollment engine
│   └── enrollment-tracker.js  MODIFY — strip oval, add stop(), expose bbox drawing hook
└── kiosk.js                KEEP — no changes to kiosk

DELETE:
├── Scripts/modules/face-guide.js
├── Scripts/enrollment-ui.js
└── Scripts/core/facescan.js

Views (inline JS stripped to minimal wiring):
├── Areas/Admin/Views/Employees/Enroll.cshtml        MODIFY — reduce inline JS to ~60 lines
└── Views/MobileRegistration/Enroll-mobile.cshtml    MODIFY — reduce inline JS to ~60 lines

Controllers:
├── Controllers/Api/ScanController.cs                 MODIFY — remove pose from response, add mobile threshold
├── Controllers/Api/EnrollmentController.cs           KEEP — no changes
├── Controllers/MobileRegistration/MobileRegistrationController.cs
│   ├── ScanFrame()          DELETE
│   ├── SubmitEnrollment()   DELETE
│   └── All other actions    KEEP (Index, Enroll GET, Identify, Device, Success, Employee portal, etc.)

New endpoint:
└── Controllers/Api/EnrollmentController.cs          ADD — GET /api/enrollment/config
```

---

# PART 3 — DETAILED IMPLEMENTATION PLAN

## Phase 0 — Preparation (Before Writing Any Code)

### Step 0.1 — Verify DB Configuration Values

Before anything else, confirm the current values in `SystemConfiguration` table:

```sql
SELECT [Key], [Value] FROM SystemConfigurations
WHERE [Key] IN (
    'Biometrics:LivenessThreshold',
    'Biometrics:MobileLivenessThreshold',
    'Biometrics:Enroll:SharpnessThreshold',
    'Biometrics:Enroll:SharpnessThreshold:Mobile',
    'Biometrics:Enroll:CaptureTarget',
    'Biometrics:Enroll:MaxStoredVectors',
    'Biometrics:Enroll:MinFaceAreaRatio',
    'Biometrics:Enroll:MinFaceAreaRatio:Mobile',
    'Biometrics:EnrollmentStrictTolerance'
)
ORDER BY [Key];
```

If any of these are missing, insert them with correct defaults using the admin Settings page, which will create them via `ConfigurationService.Upsert()`.

### Step 0.2 — Decide Final Threshold Values

Based on production observation and the mismatch analysis, set these as the canonical values:

| Key | Value | Rationale |
|---|---|---|
| `Biometrics:LivenessThreshold` | 0.75 | Current DB default — keep |
| `Biometrics:MobileLivenessThreshold` | 0.68 | Current DB default — keep |
| `Biometrics:Enroll:SharpnessThreshold` | 80.0 | Server value — client will read this |
| `Biometrics:Enroll:SharpnessThreshold:Mobile` | 50.0 | Server value — client will read this |
| `Biometrics:Enroll:CaptureTarget` | 15 | Changed from 8 — client collects 15 good frames |
| `Biometrics:Enroll:MaxPoolSize` | 20 | New key — client keeps up to 20 frames in pool |
| `Biometrics:Enroll:MaxStoredVectors` | 5 | Keep — server stores 5 diverse vectors |
| `Biometrics:Enroll:MinFaceAreaRatio` | 0.08 | Server value — client will read this |
| `Biometrics:Enroll:MinFaceAreaRatio:Mobile` | 0.05 | Server value — client will read this |
| `Biometrics:EnrollmentStrictTolerance` | 0.45 | Keep — server only |

### Step 0.3 — Create a Feature Branch

All changes happen on a dedicated branch. Do not touch `kiosk.js` or any attendance-related code during this refactor. Scope is enrollment only.

---

## Phase 1 — New Server Endpoint: Enrollment Configuration

**File:** `Controllers/Api/EnrollmentController.cs`  
**Add:** `GET /api/enrollment/config`

### What This Endpoint Does

Returns all threshold values the client needs for enrollment. The client fetches this once on page load before starting the enrollment process. After this endpoint exists, zero threshold values are hardcoded in any JavaScript file.

### Implementation

```csharp
/// <summary>
/// Returns enrollment configuration thresholds so the client never hardcodes them.
/// All values come from SystemConfiguration table (DB) with Web.config fallbacks.
/// Called once per enrollment page load before camera starts.
/// </summary>
[HttpGet]
[Route("api/enrollment/config")]
[OutputCache(Duration = 30, VaryByParam = "none")]  // 30 second cache — settings rarely change
public ActionResult Config()
{
    var isMobile = DeviceService.IsMobileDevice(Request);

    // Read thresholds. Pattern: try DB key first, fall back to appsettings default.
    var liveTh = isMobile
        ? ConfigurationService.GetDoubleCached("Biometrics:MobileLivenessThreshold", 0.68)
        : ConfigurationService.GetDoubleCached("Biometrics:LivenessThreshold", 0.75);

    var sharpTh = isMobile
        ? ConfigurationService.GetDoubleCached("Biometrics:Enroll:SharpnessThreshold:Mobile", 50.0)
        : ConfigurationService.GetDoubleCached("Biometrics:Enroll:SharpnessThreshold", 80.0);

    var minAreaRatio = isMobile
        ? ConfigurationService.GetDoubleCached("Biometrics:Enroll:MinFaceAreaRatio:Mobile", 0.05)
        : ConfigurationService.GetDoubleCached("Biometrics:Enroll:MinFaceAreaRatio", 0.08);

    var captureTarget = ConfigurationService.GetIntCached("Biometrics:Enroll:CaptureTarget", 15);
    var maxPoolSize   = ConfigurationService.GetIntCached("Biometrics:Enroll:MaxPoolSize", 20);
    var maxStored     = ConfigurationService.GetIntCached("Biometrics:Enroll:MaxStoredVectors", 5);

    return Json(new
    {
        ok                  = true,
        livenessThreshold   = liveTh,
        sharpnessThreshold  = sharpTh,
        minFaceAreaRatio    = minAreaRatio,
        maxFaceAreaRatio    = 0.90,        // constant — too-close gate, no DB key needed
        captureTarget       = captureTarget,
        maxPoolSize         = maxPoolSize,
        maxStoredVectors    = maxStored,
        isMobile            = isMobile
    }, JsonRequestBehavior.AllowGet);
}
```

**Why `OutputCache(Duration = 30)`:** Settings change very rarely. Caching for 30 seconds prevents a DB hit on every page load while ensuring changes propagate within half a minute.

**Why this must be the first step:** Every subsequent JavaScript change depends on this endpoint existing.

---

## Phase 2 — Modify ScanController: Remove Pose, Fix Mobile Threshold

**File:** `Controllers/Api/ScanController.cs`

### 2.1 What Changes

**Remove from the return value:** `poseYaw`, `posePitch`, `poseBucket`, `landmarks`

**Reason:** These are computed from 68-point landmark data. The computation itself must stay because the landmarks guide the ResNet face crop, producing a better 128d encoding. But the results do not need to be sent to the client — enrollment-core.js does not use any of them for frame selection or UI feedback. Removing them reduces response payload by approximately 200 bytes per frame × 25 frames = 5KB saved per enrollment session.

**Keep:** `liveness`, `livenessOk`, `livenessThreshold`, `sharpness`, `sharpnessOk`, `sharpnessThreshold`, `encoding`, `faceBox`, `count`, `timingMs`

### 2.2 The Mobile Threshold Fix

`MobileRegistrationController.ScanFrame()` does `liveTh * 0.30` and other mobile-specific adjustments. Move this logic into `ScanController.Frame()`:

```csharp
// In ScanController.Frame(), replace the current liveTh calculation:
var isMobile = DeviceService.IsMobileDevice(Request);

var liveTh = isMobile
    ? (float)ConfigurationService.GetDoubleCached(
        "Biometrics:MobileLivenessThreshold",
        ConfigurationService.GetDouble("Biometrics:MobileLivenessThreshold", 0.68))
    : (float)ConfigurationService.GetDoubleCached(
        "Biometrics:LivenessThreshold",
        ConfigurationService.GetDouble("Biometrics:LivenessThreshold", 0.75));
```

### 2.3 Revised Return Value

```csharp
return JsonResponseBuilder.Success(new
{
    ok                 = true,
    count              = 1,
    liveness           = scan.LivenessScore,
    livenessOk         = scan.LivenessOk,
    livenessThreshold  = liveTh,      // echo back so client knows what threshold was used
    sharpness          = scan.Sharpness,
    sharpnessThreshold = scan.SharpnessThreshold,
    sharpnessOk        = scan.Sharpness >= scan.SharpnessThreshold,
    encoding           = scan.Base64Encoding,   // 128d vector as base64 — client stores for later
    faceBox = scan.FaceBox != null ? (object)new
    {
        x = scan.FaceBox.Left,
        y = scan.FaceBox.Top,
        w = scan.FaceBox.Width,
        h = scan.FaceBox.Height
    } : null,
    timingMs = scan.TimingMs
    // REMOVED: poseYaw, posePitch, poseBucket, landmarks
});
```

---

## Phase 3 — Delete MobileRegistrationController.ScanFrame()

**File:** `Controllers/MobileRegistration/MobileRegistrationController.cs`

### 3.1 What Gets Deleted

The entire `ScanFrame()` method — approximately 100 lines including:
- The method signature and HTTP attributes
- The `FastScanPipeline.EnrollmentScanInMemory()` call
- The `poseYaw *= 0.8f` mobile compensation (this value is unused by client)
- The `DuplicateCheckHelper.FindDuplicate()` call during scanning (this is a per-frame duplicate check — expensive and wrong placement; duplicate check should happen at final submit, not every frame)
- The entire return block

### 3.2 Update Mobile Enrollment View

In `Enroll-mobile.cshtml`, change the scan URL:
```javascript
// OLD:
scanUrl: '/MobileRegistration/ScanFrame',
// NEW:
scanUrl: '/api/scan/frame',
```

The `ScanController` already handles mobile devices correctly after Phase 2.

---

## Phase 4 — Delete MobileRegistrationController.SubmitEnrollment() and Replace With Secure Path

**File:** `Controllers/MobileRegistration/MobileRegistrationController.cs`

### 4.1 The Security Problem (Full Detail)

Current `SubmitEnrollment()` receives:
```
POST /MobileRegistration/SubmitEnrollment
  EmployeeId:            "DILG-2024-001"
  FirstName:             "JUAN"
  FaceEncoding:          "q3j8+dk2...==" (base64 of 1024 bytes)
  AllFaceEncodingsJson:  '["q3j8+dk2...==","pI7M...==",...]'
```

`vm.FaceEncoding` is a base64 string that was **sent by the client**. The server does:
```csharp
faceTemplate = Convert.FromBase64String(vm.FaceEncoding);
faceVector   = DlibBiometrics.DecodeFromBytes(faceTemplate);
// ... duplicate check (only correct part) ...
emp.FaceEncodingBase64 = Convert.ToBase64String(faceTemplate); // NO ENCRYPTION
emp.FaceEncodingsJson  = vm.AllFaceEncodingsJson;               // TRUST CLIENT, NO ENCRYPTION
```

A malicious client could:
1. Craft any 128-float array (1024 bytes) — perhaps the vector of someone else
2. Send it as `FaceEncoding`
3. The server stores it verbatim, unencrypted
4. The impersonated person's face now unlocks this new account

### 4.2 The New Secure Mobile Enrollment Flow

Split into two operations:

**Operation A: Create Employee Record**
```
POST /MobileRegistration/CreateEmployee
  Inputs: name, employeeId, office, position, department, deviceName
  Action: Validate all fields server-side
          Check for duplicate employeeId
          Create Employee row with Status="PENDING", FaceEncoding=null
          Create Device row with Status="PENDING"
          Return: { ok: true, employeeId: "DILG-2024-001", employeeDbId: 42 }
```

**Operation B: Enroll Face** (uses the existing correct endpoint)
```
POST /api/enrollment/enroll
  Inputs: employeeId="DILG-2024-001" + JPEG blobs (raw image files)
  Action: (all the secure server-side processing that already exists)
          Re-detects face from each JPEG
          Re-runs liveness from each JPEG
          EnrollmentQualityGate.Validate()
          DuplicateCheckHelper.FindDuplicate()
          DPAPI encrypt
          Save to DB
          Update FastFaceMatcher cache
  Return: { ok: true, savedVectors: 5 }
```

### 4.3 Implementation of CreateEmployee

```csharp
/// <summary>
/// Creates a PENDING employee record with no face data.
/// Face enrollment happens separately via POST /api/enrollment/enroll.
/// This separation ensures face vectors always go through the secure, 
/// encrypted enrollment path regardless of device type.
/// </summary>
[HttpPost]
[ValidateAntiForgeryToken]
public ActionResult CreateEmployee(NewEmployeeEnrollmentVm vm)
{
    // Server-side sanitization — always, even with client-side validation
    vm.Sanitize();
    
    var errors = vm.ValidateEmployeeDataOnly(); // New method — validates all fields except FaceEncoding
    if (errors.Count > 0)
        return JsonResponseBuilder.Error("VALIDATION_ERROR", string.Join(" | ", errors));

    var normalizedId = vm.EmployeeId.Trim().ToUpperInvariant();
    var fingerprint  = DeviceService.GenerateFingerprint(Request);

    using (var db = new FaceAttendDBEntities())
    {
        // Check for duplicate employee ID
        if (db.Employees.Any(e => e.EmployeeId == normalizedId &&
            (e.Status == "ACTIVE" || e.Status == "PENDING")))
            return JsonResponseBuilder.Error("EMPLOYEE_ID_EXISTS",
                "Employee ID already exists or is pending approval.");

        // Validate office exists
        if (!db.Offices.Any(o => o.Id == vm.OfficeId && o.IsActive))
            return JsonResponseBuilder.Error("INVALID_OFFICE", "Selected office not found.");

        var now = DateTime.UtcNow;
        var emp = new Employee
        {
            EmployeeId         = normalizedId,
            FirstName          = vm.FirstName.Trim(),
            MiddleName         = string.IsNullOrWhiteSpace(vm.MiddleName) ? null : vm.MiddleName.Trim(),
            LastName           = vm.LastName.Trim(),
            Position           = string.IsNullOrWhiteSpace(vm.Position) ? null : vm.Position.Trim(),
            Department         = string.IsNullOrWhiteSpace(vm.Department) ? null : vm.Department.Trim(),
            OfficeId           = vm.OfficeId,
            Status             = "PENDING",
            // Face fields intentionally null — filled in by /api/enrollment/enroll
            FaceEncodingBase64 = null,
            FaceEncodingsJson  = null,
            CreatedDate        = now,
            LastModifiedDate   = now,
            ModifiedBy         = "SELF_ENROLLMENT_MOBILE"
        };

        db.Employees.Add(emp);
        db.SaveChanges();

        // Create pending device record
        DeviceService.CreatePendingDevice(
            db, emp.Id, fingerprint,
            vm.DeviceName ?? "Mobile Device",
            Request.UserHostAddress);

        return JsonResponseBuilder.Success(new
        {
            employeeId   = emp.EmployeeId,
            employeeDbId = emp.Id
        }, "Employee record created. Please complete face enrollment.");
    }
}
```

### 4.4 Mobile View Flow Change

The mobile wizard becomes:

```
Step 1: Fill in details (name, ID, office, etc.)
         → POST /MobileRegistration/CreateEmployee
         ← { ok: true, employeeId: "DILG-2024-001" }
         
Step 2: Face capture (same as before — camera, liveness, frames)
         Each frame → POST /api/scan/frame
         
Step 3: Review and submit
         → POST /api/enrollment/enroll
           (standard enroll endpoint: employeeId + JPEG blobs)
         ← { ok: true, savedVectors: 5 }
```

The mobile view no longer sends `FaceEncoding` strings. It sends the raw JPEG blobs to the same endpoint that admin uses. All security, quality gates, and encryption happen automatically.

---

## Phase 5 — Rewrite enrollment-tracker.js

**File:** `Scripts/modules/enrollment-tracker.js`

### 5.1 What Stays

- MediaPipe initialization and `detectForVideo()` call on each frame
- EMA smoothing of the bounding box (alpha 0.35)
- Writing `window.FaceAttendEnrollment.liveTrackingBox` and `.liveFaceArea`
- Camera focus enhancement (`enhanceCameraFocus`, `applyFocusPoint`)
- The full detection loop

### 5.2 What Gets Removed

- The `FaceAttend.FaceGuide.draw()` call — oval is gone
- The oval guide visual entirely — replaced by bbox drawing from server response
- The dependency on `face-guide.js`

### 5.3 What Gets Added

**Public `stop()` function:**
```javascript
// At the bottom, before the closing IIFE:
window.enrollmentTracker = {
    stop: function() {
        active   = false;
        smoothed = null;
        currentFaceArea = 0;
        if (detector) {
            try { detector.close(); } catch(e) {}
            detector = null;
        }
    },
    isActive: function() { return active; }
};
```

**Server bbox drawing in the tick loop:**

Instead of drawing the oval, the tracker draws the server-confirmed `faceBox`. This requires reading `window.FaceAttendEnrollment.lastFaceBox` (already set by enrollment-core in `processScanResult`):

```javascript
function drawServerBbox(cssW, cssH) {
    var enroll = window.FaceAttendEnrollment;
    if (!enroll) return;
    
    // Use live tracking box from MediaPipe for POSITION (smooth 60fps movement)
    if (!smoothed) return;
    
    var done   = enroll.goodFrames ? enroll.goodFrames.length : 0;
    var target = enroll.config    ? enroll.config.minGoodFrames : 15;
    var isBusy = !!enroll._tickRunning;
    
    // Color reflects actual server-confirmed frame count
    // gray    = no frames yet (scanning but nothing kept)
    // amber   = accumulating frames (1 to target-1)
    // green   = target reached — ready to confirm
    var color, glowColor;
    if (done >= target) {
        color     = '#22c55e'; // green
        glowColor = 'rgba(34,197,94,0.55)';
    } else if (done > 0) {
        color     = '#f59e0b'; // amber
        glowColor = 'rgba(245,158,11,0.50)';
    } else if (isBusy) {
        color     = '#4f9cf9'; // blue — scanning
        glowColor = 'rgba(79,156,249,0.45)';
    } else {
        color     = 'rgba(255,255,255,0.5)'; // gray — idle
        glowColor = 'transparent';
    }
    
    // Map smoothed (MediaPipe CSS-space) box for position
    // Use server faceBox aspect for size if available, tracker box for position
    var bx = smoothed.x, by = smoothed.y, bw = smoothed.w, bh = smoothed.h;
    
    // If server returned a faceBox, use its aspect ratio but keep tracker's position
    // This makes the box reflect what Dlib actually found while keeping it smooth
    var serverBox = enroll.lastFaceBox;
    if (serverBox && serverBox.w > 0 && video.videoWidth > 0) {
        // Scale server box from image-space to canvas-space for size reference
        var scaleX = cssW / video.videoWidth;
        var scaleY = cssH / video.videoHeight;
        var serverW = serverBox.w * scaleX;
        var serverH = serverBox.h * scaleY;
        // Blend: keep tracker position (smooth) but use server size (accurate)
        var BLEND = 0.3;
        bw = bw * (1 - BLEND) + serverW * BLEND;
        bh = bh * (1 - BLEND) + serverH * BLEND;
    }
    
    // Animated scan line while capturing
    if (isBusy) {
        _scanLinePos = (_scanLinePos + 0.016) % 1.0;
        var scanY = by + bh * _scanLinePos;
        var grad  = ctx.createLinearGradient(bx, scanY, bx + bw, scanY);
        grad.addColorStop(0,    'rgba(0,0,0,0)');
        grad.addColorStop(0.5,  color);
        grad.addColorStop(1,    'rgba(0,0,0,0)');
        ctx.save();
        ctx.globalAlpha = 0.55;
        ctx.strokeStyle = grad;
        ctx.lineWidth   = 1.5;
        ctx.shadowColor = color;
        ctx.shadowBlur  = 8;
        ctx.beginPath();
        ctx.moveTo(bx, scanY);
        ctx.lineTo(bx + bw, scanY);
        ctx.stroke();
        ctx.restore();
    } else {
        _scanLinePos = 0;
    }
    
    // Corner brackets
    var cLen = Math.min(bw, bh) * 0.20;
    var lw   = isBusy ? 3.5 : 2.5;
    ctx.save();
    ctx.strokeStyle = color;
    ctx.lineWidth   = lw;
    ctx.lineCap     = 'round';
    ctx.lineJoin    = 'round';
    ctx.shadowColor = glowColor;
    ctx.shadowBlur  = 14;
    
    function bracket(ax, ay, mx, my, ex, ey) {
        ctx.beginPath();
        ctx.moveTo(ax, ay); ctx.lineTo(mx, my); ctx.lineTo(ex, ey);
        ctx.stroke();
    }
    bracket(bx + cLen,      by,      bx,      by,      bx,      by + cLen);
    bracket(bx + bw - cLen, by,      bx + bw, by,      bx + bw, by + cLen);
    bracket(bx + cLen,      by + bh, bx,      by + bh, bx,      by + bh - cLen);
    bracket(bx + bw - cLen, by + bh, bx + bw, by + bh, bx + bw, by + bh - cLen);
    
    // Subtle filled box
    ctx.globalAlpha = 0.04;
    ctx.fillStyle   = color;
    ctx.fillRect(bx, by, bw, bh);
    ctx.restore();
}
```

The `tick()` function changes from:
```javascript
// OLD: draw oval
if (FaceAttend.FaceGuide) {
    FaceAttend.FaceGuide.draw(ctx, cssW, cssH, guideState, done / target, isBusy);
}
```
To:
```javascript
// NEW: draw server bbox
drawServerBbox(cssW, cssH);
```

### 5.4 Guide Prompt Text (Keep This Part)

The `#enrollGuidePrompt` text updates are useful UX and should stay. The text logic moves from being tied to the oval state to being tied to actual server results:

```javascript
// Update guide prompt based on server-confirmed state
var enroll  = window.FaceAttendEnrollment;
var done    = enroll && enroll.goodFrames ? enroll.goodFrames.length : 0;
var target  = enroll && enroll.config    ? enroll.config.minGoodFrames : 15;
var promptEl = document.getElementById('enrollGuidePrompt');

if (promptEl) {
    if (!smoothed) {
        promptEl.innerHTML = '<i class="fa-solid fa-circle-dot"></i> Look at the camera';
    } else if (guideState === 'too_close') {
        promptEl.innerHTML = '<i class="fa-solid fa-arrow-down"></i> Too close — back up slightly';
    } else if (guideState === 'too_far') {
        promptEl.innerHTML = '<i class="fa-solid fa-arrow-up"></i> Move a bit closer';
    } else if (guideState === 'off_center') {
        promptEl.innerHTML = '<i class="fa-solid fa-arrows-up-down-left-right"></i> Center your face';
    } else if (done >= target) {
        promptEl.innerHTML = '<i class="fa-solid fa-check text-success"></i> Capture complete';
    } else if (done > 0) {
        promptEl.innerHTML = '<i class="fa-solid fa-circle-notch fa-spin"></i> Capturing... ' + done + '/' + target;
    } else {
        promptEl.innerHTML = '<i class="fa-solid fa-circle-check"></i> Hold still';
    }
}

// State for guide color (still useful even without oval)
var guideState = (smoothed)
    ? FaceGuideState.get(currentFaceArea, smoothed, cssW, cssH)
    : 'none';
```

Note: `FaceAttend.FaceGuide.getState()` is still used for the prompt — the STATE logic is useful, just not the OVAL DRAWING. So the `getState()` function should be inlined into tracker.js directly rather than depending on the deleted `face-guide.js`:

```javascript
// Inline the state logic (was in face-guide.js):
var TOO_CLOSE = 0.90, TOO_FAR = 0.09, CENTER_MARGIN = 0.15;
function getFaceState(faceArea, box, cW, cH) {
    if (!box || !(faceArea > 0)) return 'none';
    if (faceArea > TOO_CLOSE)   return 'too_close';
    if (faceArea < TOO_FAR)     return 'too_far';
    var cx = box.x + box.w / 2, cy = box.y + box.h / 2;
    var mx = cW * CENTER_MARGIN, my = cH * CENTER_MARGIN;
    if (cx < mx || cx > cW - mx || cy < my || cy > cH - my) return 'off_center';
    return 'good';
}
```

---

## Phase 6 — Rewrite enrollment-core.js

This is the largest and most important change. The new `enrollment-core.js` is the single source of truth for all enrollment logic.

### 6.1 Remove All Hardcoded Threshold Constants

**Current CONSTANTS block (DELETE ALL OF THESE):**
```javascript
// DELETE ENTIRE BLOCK:
var CONSTANTS = {
    MIN_GOOD_FRAMES:   25,       // → reads from server config
    MAX_KEEP_FRAMES:   30,       // → reads from server config
    CAPTURE_TARGET:    25,       // → reads from server config
    SHARPNESS_THRESHOLD_DESKTOP: 40,  // → reads from server config
    SHARPNESS_THRESHOLD_MOBILE:  15,  // → reads from server config
    MIN_FACE_AREA_RATIO_DESKTOP: 0.15, // → reads from server config
    MIN_FACE_AREA_RATIO_MOBILE:  0.12, // → reads from server config
    MAX_FACE_AREA_RATIO:         0.90, // → reads from server config
    ...
};
```

**Keep only true UI constants (not thresholds):**
```javascript
var UI_CONSTANTS = {
    AUTO_INTERVAL_MS:     200,    // camera capture frequency — UI concern
    PASS_WINDOW:          3,      // rolling liveness window — UI concern
    UPLOAD_QUALITY:       0.92,   // JPEG quality for capture — UI concern
    SHARPNESS_SAMPLE_SIZE: 256,   // canvas size for sharpness — UI concern
    BRIGHTNESS_MIN:       40,     // true constant — below this liveness is always 0
    AUTO_CONFIRM_TIMEOUT_MS: 20000 // 20s auto-confirm fallback — UI concern
};
```

### 6.2 The Enrollment Constructor — Config Comes From Server

```javascript
function Enrollment(serverConfig) {
    // serverConfig is the response from GET /api/enrollment/config
    // All thresholds come from here — no hardcoding
    this.config = {
        empId:              serverConfig.empId || '',
        perFrameThreshold:  serverConfig.livenessThreshold  || 0.75,
        sharpnessThreshold: serverConfig.sharpnessThreshold || 80.0,
        minFaceAreaRatio:   serverConfig.minFaceAreaRatio   || 0.08,
        maxFaceAreaRatio:   serverConfig.maxFaceAreaRatio   || 0.90,
        minGoodFrames:      serverConfig.captureTarget      || 15,
        maxKeepFrames:      serverConfig.maxPoolSize        || 20,
        scanUrl:            serverConfig.scanUrl            || '/api/scan/frame',
        enrollUrl:          serverConfig.enrollUrl          || '/api/enrollment/enroll',
        isMobile:           serverConfig.isMobile           || false
    };
    
    // ... rest of constructor
}
```

### 6.3 The Quality Formula — Include Face Area

```javascript
Enrollment.prototype.pushGoodFrame = function(blob, liveness, encoding, sharpness, faceBox, imageW, imageH) {
    if (!blob || !encoding) return;

    // Normalized sharpness (0-1): server threshold as the reference max
    var normSharp = this.config.sharpnessThreshold > 0
        ? Math.min(sharpness / (this.config.sharpnessThreshold * 2), 1.0)
        : Math.min(sharpness / 200, 1.0);

    // Normalized face area (0-1): 0.25 = "perfect" — face fills quarter of frame
    // Below 0.25 area = getting worse, above 0.25 = diminishing returns
    var faceArea = (faceBox && imageW && imageH && faceBox.w > 0)
        ? (faceBox.w * faceBox.h) / (imageW * imageH)
        : 0;
    var normArea = Math.min(faceArea / 0.25, 1.0);

    // Quality formula — matches server-side FaceQualityAnalyzer.CalculateQualityScore()
    // liveness: 55% weight (most important — determines if real face)
    // sharpness: 30% weight (determines encoding accuracy)
    // face area: 15% weight (larger face = better crop = better ResNet embedding)
    var quality = liveness * 0.55 + normSharp * 0.30 + normArea * 0.15;

    var newFrame = {
        blob:      blob,
        encoding:  encoding,
        p:         liveness,
        sharpness: sharpness,
        faceArea:  faceArea,
        quality:   quality
    };

    if (this.goodFrames.length < this.config.maxKeepFrames) {
        this.goodFrames.push(newFrame);
    } else {
        // Find lowest quality frame and replace if new frame is better
        var worstIdx = 0;
        for (var i = 1; i < this.goodFrames.length; i++) {
            if (this.goodFrames[i].quality < this.goodFrames[worstIdx].quality) {
                worstIdx = i;
            }
        }
        if (quality > this.goodFrames[worstIdx].quality) {
            this.goodFrames[worstIdx] = newFrame;
        }
    }

    // Keep sorted: best frames first (server's greedy selection starts from these)
    this.goodFrames.sort(function(a, b) { return b.quality - a.quality; });

    if (this.callbacks.onCaptureProgress) {
        this.callbacks.onCaptureProgress(this.goodFrames.length, this.config.minGoodFrames);
    }
};
```

### 6.4 processScanResult — Remove All Unused Fields

```javascript
Enrollment.prototype.processScanResult = function(r) {
    // r.poseYaw, r.posePitch, r.poseBucket, r.landmarks — all removed from server response
    // Only use: r.liveness, r.livenessOk, r.sharpness, r.sharpnessOk, r.encoding, r.faceBox
    
    if (!r || r.ok !== true) {
        // ... error handling (unchanged)
        return;
    }

    if (r.faceBox) {
        this.lastFaceBox    = r.faceBox;
        this.lastImageWidth  = r.imageWidth  || 1280;
        this.lastImageHeight = r.imageHeight || 720;
    }

    var p    = typeof r.liveness === 'number' ? r.liveness : 0;
    var pass = (r.livenessOk === true) && (p >= this.config.perFrameThreshold);

    if (this.callbacks.onLivenessUpdate) {
        this.callbacks.onLivenessUpdate(Math.round(p * 100), pass ? 'pass' : 'fail');
    }

    if (pass) {
        var serverSharpOk = (r.sharpnessOk === true || r.sharpnessOk === undefined);
        if (serverSharpOk) {
            // Pass faceBox and image dimensions so quality formula includes area
            this.pushGoodFrame(
                r.lastBlob || null,
                p,
                r.encoding || null,
                r.sharpness || 0,
                r.faceBox,
                this.lastImageWidth,
                this.lastImageHeight
            );
        }
        // ... rest unchanged
    }
    // ... rest unchanged
};
```

### 6.5 The Create Factory — Async Configuration Loading

The factory function must now be async because it fetches config from the server:

```javascript
// New factory — loads config from server before creating instance
FaceAttend.Enrollment.createFromServer = function(options) {
    // options: { empId, scanUrl, enrollUrl } — page-specific, not thresholds
    return fetch('/api/enrollment/config', {
        method: 'GET',
        credentials: 'same-origin',
        headers: { 'X-Requested-With': 'XMLHttpRequest' }
    })
    .then(function(r) { return r.json(); })
    .then(function(serverCfg) {
        if (!serverCfg || !serverCfg.ok) {
            // Use conservative defaults if config endpoint fails
            serverCfg = {
                livenessThreshold:  0.75,
                sharpnessThreshold: 80.0,
                minFaceAreaRatio:   0.08,
                maxFaceAreaRatio:   0.90,
                captureTarget:      15,
                maxPoolSize:        20,
                isMobile:           false
            };
        }
        // Merge with page-specific options
        var merged = Object.assign({}, serverCfg, options || {});
        return new Enrollment(merged);
    });
};

// Keep sync create() for backward compatibility but deprecated
FaceAttend.Enrollment.create = function(config) {
    return new Enrollment(config);
};
```

---

## Phase 7 — Rewrite Admin Enroll.cshtml Inline JavaScript

**Goal:** Reduce from ~320 lines to ~60 lines of pure page wiring.

### 7.1 What the Inline Script Must Do (Only These Things)

1. Read page-specific data from the view (employeeId, URLs from `Url.Content()`)
2. Call `FaceAttend.Enrollment.createFromServer()` with page data
3. Wire the wizard step navigation (show/hide panes)
4. Wire the 5 callbacks to update page DOM elements
5. Set `window.FaceAttendEnrollment` AFTER callbacks are wired
6. Wire the upload pane (admin only — mobile has no upload)
7. Wire the Back button to call `enrollmentTracker.stop()`

### 7.2 The Complete New Inline Script

```javascript
@section scripts {
@Scripts.Render("~/bundles/fa-core")
@Scripts.Render("~/bundles/enrollment")
<script>
(function() {
    'use strict';
    
    // ── Page-specific configuration (only things the view knows) ──────────────
    var PAGE_CFG = {
        empId:      '@Model.EmployeeId',
        enrollUrl:  '@Url.Content("~/api/enrollment/enroll")',
        redirectUrl: '@Url.Action("Index", "Employees", new { area = "Admin" })'
    };

    var enroll = null;  // Created after server config loads
    var _cameraActive = false;

    // ── Wizard navigation ──────────────────────────────────────────────────────
    function showPane(name) {
        ['method', 'live', 'upload', 'success'].forEach(function(id) {
            var el = document.getElementById(id + 'Pane');
            if (el) el.classList.toggle('enroll-hidden', id !== name);
        });
        updateWizardSteps(name);
        window.scrollTo(0, 0);
    }

    function updateWizardSteps(activeName) {
        var steps = { method:1, live:2, upload:2, success:3 };
        var active = steps[activeName] || 1;
        document.querySelectorAll('.enroll-wizard-step').forEach(function(el, i) {
            var n = i + 1;
            el.classList.remove('is-active', 'is-complete');
            if (n === active) el.classList.add('is-active');
            if (n < active)   el.classList.add('is-complete');
        });
    }

    // ── Camera lifecycle ───────────────────────────────────────────────────────
    function startCamera() {
        if (_cameraActive || !enroll) return;
        _cameraActive = true;
        var videoEl = document.getElementById('enrollVideo');
        enroll.startCamera(videoEl)
            .then(function() {
                setTimeout(function() { enroll.startAutoEnrollment(); }, 1500);
            })
            .catch(function(e) {
                _cameraActive = false;
                var s = document.getElementById('cameraStatusText');
                if (s) s.textContent = 'Camera error: ' + (e && e.message || e);
            });
    }

    function stopCamera() {
        _cameraActive = false;
        if (window.enrollmentTracker) window.enrollmentTracker.stop();
        if (enroll) enroll.stopCamera();
    }

    // ── Public navigation (called by onclick in HTML) ──────────────────────────
    window.showMethod = function() { stopCamera(); showPane('method'); };
    window.showLive   = function() { showPane('live');   startCamera(); };
    window.showUpload = function() { stopCamera(); showPane('upload'); };

    // ── Upload pane ────────────────────────────────────────────────────────────
    (function initUpload() {
        var dropzone  = document.getElementById('dropzone');
        var fileInput = document.getElementById('fileInput');
        var uploadBtn = document.getElementById('uploadBtn');
        var fileGrid  = document.getElementById('fileGrid');
        var selectedFiles = [];

        if (!dropzone) return;

        dropzone.addEventListener('click',    function() { fileInput.click(); });
        dropzone.addEventListener('dragover', function(e) { e.preventDefault(); dropzone.classList.add('is-dragover'); });
        dropzone.addEventListener('dragleave', function()  { dropzone.classList.remove('is-dragover'); });
        dropzone.addEventListener('drop',     function(e) {
            e.preventDefault(); dropzone.classList.remove('is-dragover');
            addFiles(e.dataTransfer.files);
        });
        fileInput.addEventListener('change', function() { addFiles(this.files); });

        function addFiles(files) {
            Array.from(files).forEach(function(f) {
                if (selectedFiles.length >= 5 || !f.type.startsWith('image/')) return;
                selectedFiles.push(f);
                renderCard(f, selectedFiles.length - 1);
            });
            if (uploadBtn) uploadBtn.disabled = selectedFiles.length === 0;
        }

        function renderCard(f, i) {
            // (same card rendering as before — minor DOM creation)
        }

        window.removeFile = function(i) {
            selectedFiles.splice(i, 1);
            if (fileGrid) fileGrid.innerHTML = '';
            selectedFiles.forEach(function(f, idx) { renderCard(f, idx); });
            if (uploadBtn) uploadBtn.disabled = selectedFiles.length === 0;
        };

        window.processUpload = function() {
            if (!selectedFiles.length || !enroll) return;
            enroll.enrollFromFiles(selectedFiles, { maxImages: 5 })
                .then(function() { showPane('success'); })
                .catch(function(e) { /* enroll.callbacks.onEnrollmentError handles UI */ });
        };
    })();

    // ── Initialize enrollment engine from server config ─────────────────────────
    FaceAttend.Enrollment.createFromServer(PAGE_CFG)
        .then(function(instance) {
            enroll = instance;

            // Wire callbacks BEFORE setting window.FaceAttendEnrollment
            enroll.callbacks.onStatus = function(msg) {
                var el = document.getElementById('cameraStatusText');
                if (el) el.textContent = msg || '';
            };

            enroll.callbacks.onLivenessUpdate = function(pct) {
                var bar = document.getElementById('livenessBar');
                var txt = document.getElementById('livenessText');
                if (bar) bar.style.width = pct + '%';
                if (txt) txt.textContent = pct + '%';
            };

            enroll.callbacks.onCaptureProgress = function(current, target) {
                var c = document.getElementById('captureCount');
                var p = document.getElementById('captureProgress');
                if (c) c.textContent = current;
                if (p) p.style.width = Math.min(100, Math.round(current / target * 100)) + '%';
            };

            enroll.callbacks.onReadyToConfirm = function(data) {
                // Build preview thumbnails and show Swal dialog
                // (same Swal logic as before — ~30 lines)
                // On confirm: enroll.performEnrollment()
                // On cancel:  enroll.startAutoEnrollment()
            };

            enroll.callbacks.onEnrollmentComplete = function(count) {
                showPane('success');
                if (PAGE_CFG.redirectUrl) {
                    setTimeout(function() { window.location.href = PAGE_CFG.redirectUrl; }, 2000);
                }
            };

            enroll.callbacks.onEnrollmentError = function(result) {
                var msg = enroll.describeEnrollError(result);
                var el  = document.getElementById('cameraStatusText');
                if (el) el.textContent = msg;
                // Re-enable save button if it was disabled
                var saveBtn = document.getElementById('saveBtn');
                if (saveBtn) { saveBtn.disabled = false; saveBtn.innerHTML = '<i class="fa-solid fa-check me-2"></i>Save Enrollment'; }
            };

            // IMPORTANT: Set window reference AFTER all callbacks are wired
            window.FaceAttendEnrollment = enroll;
        })
        .catch(function(e) {
            console.error('[Enroll] Failed to load config:', e);
        });

    window.addEventListener('beforeunload', stopCamera);
})();
</script>
}
```

**Note:** The Save button (`window.saveEnrollment`) now calls `_fireReadyToConfirm()` instead of `performEnrollment()` directly:

```javascript
window.saveEnrollment = function() {
    if (!enroll || enroll.goodFrames.length < enroll.config.minGoodFrames) return;
    enroll.stopAutoEnrollment();
    enroll._fireReadyToConfirm();  // Shows preview dialog — consistent with auto path
};

window.retake = function() {
    if (!enroll) return;
    enroll.startAutoEnrollment();
    // Do NOT restart face tracker — it keeps running
};
```

---

## Phase 8 — Rewrite Mobile Enroll-mobile.cshtml Inline JavaScript

**Goal:** Same reduction — ~280 lines to ~70 lines.

### 8.1 The New Mobile Flow (Three Steps)

```
Step 1 — Details Form
  User fills name, ID, office, device name
  Client validates format
  On Next: POST /MobileRegistration/CreateEmployee
  Server creates Employee (PENDING) + Device (PENDING)
  Receives: { employeeId, employeeDbId }

Step 2 — Face Capture
  Same camera + enrollment-core as admin
  Scan URL: /api/scan/frame
  Enroll URL: /api/enrollment/enroll (with employeeId from Step 1)
  Min frames: from server config

Step 3 — Submission
  User reviews details
  Clicks Submit → enrollment-core sends JPEG blobs to /api/enrollment/enroll
  Server handles everything: detect, liveness, quality gate, encrypt, save
  On success: redirect to /MobileRegistration/Success?isNewEmployee=true
```

### 8.2 The Complete New Inline Script

```javascript
<script>
(function() {
    'use strict';

    var state = {
        step:        1,
        employeeId:  null,
        employeeDbId: null,
        formValid:   false
    };

    var enroll = null;
    var _cameraActive = false;

    // ── Step navigation ────────────────────────────────────────────────────────
    function setStep(n) {
        state.step = n;
        [1,2,3].forEach(function(i) {
            var pane    = document.getElementById('step' + i);
            var stepBtn = document.querySelector('.wizard-step[data-step="' + i + '"]');
            if (pane)    pane.classList.toggle('active', i === n);
            if (stepBtn) {
                stepBtn.classList.remove('active', 'done');
                if (i === n) stepBtn.classList.add('active');
                if (i <  n) stepBtn.classList.add('done');
            }
        });
        if (n === 2 && !_cameraActive) startCamera();
        if (n !== 2) stopCamera();
        window.scrollTo(0, 0);
    }

    // ── Step 1: Create employee record ─────────────────────────────────────────
    document.getElementById('btnToCapture').addEventListener('click', function() {
        if (!state.formValid) return;
        
        var btn = this;
        btn.disabled = true;
        btn.innerHTML = '<i class="fa-solid fa-circle-notch fa-spin me-2"></i>Creating record...';

        var fd = new FormData();
        fd.append('__RequestVerificationToken', document.querySelector('input[name="__RequestVerificationToken"]').value);
        ['employeeId','firstName','middleName','lastName','position','department','officeId','deviceName'].forEach(function(id) {
            var el = document.getElementById(id);
            if (el) fd.append(id.charAt(0).toUpperCase() + id.slice(1), el.value.trim().toUpperCase());
        });

        fetch('/MobileRegistration/CreateEmployee', { method:'POST', body:fd, credentials:'same-origin' })
            .then(function(r) { return r.json(); })
            .then(function(j) {
                if (!j.ok) throw new Error(j.message || j.error || 'Failed to create record');
                state.employeeId   = j.data.employeeId;
                state.employeeDbId = j.data.employeeDbId;

                // NOW create enrollment engine with the known employeeId
                return FaceAttend.Enrollment.createFromServer({
                    empId:      state.employeeId,
                    enrollUrl:  '/api/enrollment/enroll'
                });
            })
            .then(function(instance) {
                enroll = instance;
                wireCallbacks();
                window.FaceAttendEnrollment = enroll;  // After callbacks
                setStep(2);
            })
            .catch(function(e) {
                btn.disabled = false;
                btn.innerHTML = '<i class="fa-solid fa-arrow-right me-2"></i>Continue to Face Capture';
                // Show error to user
            });
    });

    // ── Step 2: Camera ─────────────────────────────────────────────────────────
    function startCamera() {
        if (_cameraActive || !enroll) return;
        _cameraActive = true;
        var videoEl = document.getElementById('enrollVideo');
        enroll.startCamera(videoEl)
            .then(function() { setTimeout(function() { enroll.startAutoEnrollment(); }, 1500); })
            .catch(function(e) { _cameraActive = false; });
    }

    function stopCamera() {
        _cameraActive = false;
        if (window.enrollmentTracker) window.enrollmentTracker.stop();
        if (enroll) enroll.stopCamera();
    }

    // ── Callbacks ──────────────────────────────────────────────────────────────
    function wireCallbacks() {
        enroll.callbacks.onLivenessUpdate = function(pct) {
            var bar = document.getElementById('livenessBar');
            var txt = document.getElementById('livenessText');
            if (bar) bar.style.width = pct + '%';
            if (txt) txt.textContent = pct + '%';
        };

        enroll.callbacks.onCaptureProgress = function(current, target) {
            var c = document.getElementById('captureCount');
            var p = document.getElementById('captureProgress');
            if (c) c.textContent = current;
            if (p) p.style.width = Math.min(100, Math.round(current/target*100)) + '%';
        };

        enroll.callbacks.onReadyToConfirm = function(data) {
            // Show Swal preview with thumbnails
            // On confirm: enroll.performEnrollment()
            // On cancel:  enroll.startAutoEnrollment()
        };

        enroll.callbacks.onEnrollmentComplete = function() {
            window.location.href = '/MobileRegistration/Success?isNewEmployee=true'
                + (state.employeeDbId ? '&employeeDbId=' + state.employeeDbId : '');
        };

        enroll.callbacks.onEnrollmentError = function(result) {
            var msg = enroll.describeEnrollError(result);
            if (typeof Swal !== 'undefined') {
                Swal.fire({ icon:'error', title:'Enrollment Failed', html:msg,
                    background:'#0f172a', color:'#f8fafc' });
            }
        };
    }

    // ── Form validation ────────────────────────────────────────────────────────
    // (Same validation rules as before — ~40 lines, no change needed)
    // Update state.formValid when all fields pass
    // Enable/disable btnToCapture based on state.formValid

    window.addEventListener('beforeunload', stopCamera);
})();
</script>
```

---

## Phase 9 — Delete Dead Files

### 9.1 Files to Delete (Fully)

```
Scripts/enrollment-ui.js
Scripts/modules/face-guide.js
Scripts/core/facescan.js
```

### 9.2 Remove From All Bundle Registrations

**File:** `App_Start/BundleConfig.cs`

Find and remove these from every bundle they appear in:
- `"~/Scripts/enrollment-ui.js"`
- `"~/Scripts/modules/face-guide.js"`
- `"~/Scripts/core/facescan.js"`

### 9.3 Remove From Layout Files

Search all `.cshtml` files for:
- `Scripts.Render("~/bundles/enrollment")` — check this bundle no longer includes deleted files
- Any direct `<script src="...face-guide.js">` or similar

### 9.4 Check for Any Other References

Run a solution-wide search (Ctrl+Shift+F) for:
- `FaceAttend.FaceScan`
- `FaceAttend.FaceGuide`
- `face-guide.js`
- `facescan.js`
- `enrollment-ui.js`
- `enrollRoot`

Every result must be removed or updated.

### 9.5 `_EnrollmentComponent.cshtml` — Check and Delete

**File:** `Areas/Admin/Views/Shared/_EnrollmentComponent.cshtml`  
This file creates `<div id="enrollRoot">` — the element that `enrollment-ui.js` looks for. Since `enrollment-ui.js` is being deleted, this component can also be deleted. But first verify it is not included anywhere:

Search all `.cshtml` files for `@Html.Partial("_EnrollmentComponent")` and `@{ Html.RenderPartial("_EnrollmentComponent"); }`. If zero results: delete the file. If any results: investigate — those pages may need separate attention.

---

## Phase 10 — Update Bundle Registration

**File:** `App_Start/BundleConfig.cs`

The enrollment bundle after cleanup:

```csharp
// Enrollment bundle — loaded on both enrollment pages
bundles.Add(new ScriptBundle("~/bundles/enrollment").Include(
    "~/Scripts/modules/enrollment-core.js",     // The engine
    "~/Scripts/modules/enrollment-tracker.js"   // The 60fps tracker
    // REMOVED: face-guide.js, enrollment-ui.js
));
```

The fa-core bundle (no changes):
```csharp
bundles.Add(new ScriptBundle("~/bundles/fa-core").Include(
    "~/Scripts/core/fa-helpers.js",
    "~/Scripts/core/camera.js",
    "~/Scripts/core/notify.js",
    "~/Scripts/core/api.js"
    // REMOVE: facescan.js
));
```

---

## Phase 11 — Testing Protocol

### 11.1 Server-Side Verification

After Phase 1-4 (server changes):

1. Open browser DevTools → Network tab
2. Load `/Admin/Employees/Enroll/[id]`
3. Verify `GET /api/enrollment/config` returns correct values
4. Capture one frame — verify response has NO `poseYaw`, `posePitch`, `poseBucket`, `landmarks`
5. Complete enrollment — verify DB has `"dpapi1:"` prefix on `FaceEncodingBase64`
6. Load `/MobileRegistration/Enroll` — verify scan goes to `/api/scan/frame` (not ScanFrame)
7. Complete mobile enrollment — verify DB has `"dpapi1:"` prefix (not raw base64)
8. Verify `MobileRegistration/ScanFrame` and `MobileRegistration/SubmitEnrollment` return 404

### 11.2 Client-Side Verification

After Phase 5-9 (client changes):

1. Load enrollment page — verify no console errors
2. Open DevTools → Sources — confirm `face-guide.js`, `enrollment-ui.js`, `facescan.js` are NOT loaded
3. Open DevTools → Performance — start recording, run enrollment, stop. Verify only ONE rAF loop on the canvas (no duplicate animation frames)
4. The canvas should show corner brackets (bbox style), NOT an oval
5. The bracket color should be gray when no frames are kept, amber when accumulating, green when target reached
6. Verify `window.enrollmentTracker.stop()` is callable from console
7. Click Back during capture — verify camera stops and canvas clears
8. Click Method → Live again — verify camera restarts without duplicate loops

### 11.3 Threshold Verification

1. Log to console: `window.FaceAttendEnrollment.config` — verify values match DB
2. Test with poor lighting — verify brightness gate fires (not sharpness, not liveness)
3. Test very close to camera — verify too-close gate fires
4. Test far from camera — verify too-far prompt appears

### 11.4 Security Verification

1. Enroll a mobile employee
2. Query DB: `SELECT FaceEncodingBase64 FROM Employees WHERE EmployeeId = '...'`
3. Verify it starts with `dpapi1:` — NOT raw base64
4. Try to manually call `POST /MobileRegistration/SubmitEnrollment` with Postman — should return 404

---

# PART 4 — IMPLEMENTATION ORDER AND DEPENDENCIES

## Critical Path (Must Be Done In This Order)

```
Phase 0   → Verify DB config
Phase 1   → Add /api/enrollment/config endpoint
Phase 2   → Modify ScanController (remove pose, fix mobile threshold)
Phase 3   → Delete MobileRegistrationController.ScanFrame
Phase 4   → Replace SubmitEnrollment with CreateEmployee + route through /api/enrollment/enroll
Phase 5   → Rewrite enrollment-tracker.js (remove oval, add stop(), draw bbox)
Phase 6   → Rewrite enrollment-core.js (remove constants, add area to quality, remove pose handling)
Phase 7   → Rewrite Admin Enroll.cshtml inline JS
Phase 8   → Rewrite Mobile Enroll-mobile.cshtml inline JS
Phase 9   → Delete dead files (face-guide.js, enrollment-ui.js, facescan.js)
Phase 10  → Update bundle registration
Phase 11  → Test everything
```

**Why this order:**
- Phase 1 must be before Phases 5-8 (JS reads from it)
- Phase 2-4 must be before Phase 8 (mobile view uses new endpoint)
- Phase 5-6 must be before Phase 7-8 (views depend on them)
- Phase 9 must be after Phase 7-8 (views no longer reference deleted files)
- Phase 10 must be after Phase 9 (cannot reference deleted files in bundles)

## What NOT to Touch During This Refactor

- `kiosk.js` — attendance scanning is a separate system
- `AttendanceScanService.cs` — attendance pipeline untouched
- `KioskController.cs` — attendance controller untouched
- `FastFaceMatcher.cs` — face matching unchanged
- `BallTreeIndex.cs` — search index unchanged
- `AttendanceController.cs` — admin attendance reports untouched
- All visitor-related code — explicitly out of scope
- All admin area controllers except EnrollmentController — untouched
- `ConfigurationService.cs` — configuration layer untouched
- `BiometricCrypto.cs` — encryption layer untouched (we just USE it correctly now)

---

# PART 5 — FINAL STATE SUMMARY

## What Each File Does After Refactor

| File | Lines (approx) | Single Responsibility |
|---|---|---|
| `enrollment-core.js` | ~400 | Camera capture loop, frame quality assessment, server communication, frame pool management |
| `enrollment-tracker.js` | ~220 | MediaPipe 60fps detection, bbox drawing, camera focus, expose stop() |
| `camera.js` | ~150 | Camera API wrapper |
| `fa-helpers.js` | ~200 | DOM utilities |
| `notify.js` | ~300 | Toast and dialog notifications |
| `api.js` | ~250 | Server API client |
| `Enroll.cshtml` inline | ~60 | Admin page wiring: wizard nav, callbacks, upload pane |
| `Enroll-mobile.cshtml` inline | ~70 | Mobile page wiring: wizard nav, form submit, callbacks |
| `ScanController.cs` | ~80 | Single scan frame endpoint for all clients |
| `EnrollmentController.cs` | ~250 | Enroll from JPEGs with full security and quality pipeline |
| `MobileRegistrationController.cs` | ~350 | Mobile UI flow (CreateEmployee, Device, Success, Employee portal) |

## What No Longer Exists

| Deleted | Was Doing | Now Handled By |
|---|---|---|
| `face-guide.js` | Drawing oval from wrong model | `enrollment-tracker.js` draws bbox from server response |
| `enrollment-ui.js` | Nothing — always exited immediately | Nothing — it was dead |
| `facescan.js` | Nothing — never called | Nothing — it was dead |
| `MobileRegistrationController.ScanFrame()` | Duplicate scan endpoint | `ScanController.Frame()` handles mobile via `IsMobileDevice()` |
| `MobileRegistrationController.SubmitEnrollment()` | Insecure face vector bypass | `EnrollmentController.Enroll()` with DPAPI encryption |
| All JS threshold constants | Wrong values, caused frame rejection | Server returns thresholds via `/api/enrollment/config` |
| Double rAF loops | Canvas flicker | Single loop in `enrollment-tracker.js` |
| Unencrypted mobile vectors | Security vulnerability | All vectors through `BiometricCrypto.Protect*()` |

## The Single Data Flow After Refactor

```
Page loads
    │
    ├──→ GET /api/enrollment/config
    │         Returns: livenessThreshold, sharpnessThreshold,
    │                  minFaceAreaRatio, captureTarget, maxPoolSize
    │
    ├──→ enrollment-tracker.js starts 60fps MediaPipe detection
    │         Writes: liveTrackingBox, liveFaceArea to window.FaceAttendEnrollment
    │         Draws:  bbox with gray/amber/green color on canvas
    │
    └──→ enrollment-core.js starts 200ms capture tick
              Every tick:
                Client gates: sharpness, brightness, area, centering
                POST /api/scan/frame (same endpoint: admin and mobile)
                Server: detect + liveness + encode (parallel)
                Returns: liveness, sharpness, encoding, faceBox
                Client: pushGoodFrame() with quality = liveness*0.55 + sharpness*0.30 + area*0.15
                Color updates on tracker canvas

    When captureTarget frames collected:
        onReadyToConfirm() → Swal preview dialog
        User confirms
        POST /api/enrollment/enroll (same endpoint: admin and mobile)
        Server: re-detect all JPEGs + liveness + encode + diversity select + quality gate + duplicate check + DPAPI encrypt + save
        onEnrollmentComplete() → navigate to success
```

This is the complete picture. Every current problem has a defined fix. Every fix has a defined location. Every deletion has a defined replacement or explicit "no replacement needed". The order is defined. The test protocol is defined. Nothing is left ambiguous.
