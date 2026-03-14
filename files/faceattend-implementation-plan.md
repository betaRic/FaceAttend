# FaceAttend — Complete Unified Implementation Plan
## Version: WICCAN-ANT-MAN-QUAKE  
**For LLM CLI Execution — Read the entire document before writing a single line of code.**

---

## Table of Contents
1. [System State Analysis](#1-system-state-analysis)
2. [Redundancies to Eliminate](#2-redundancies-to-eliminate)
3. [New Architecture Overview](#3-new-architecture-overview)
4. [File Change Manifest](#4-file-change-manifest)
5. [Phase 1 — Server Infrastructure](#5-phase-1--server-infrastructure)
6. [Phase 2 — Shared JavaScript Core](#6-phase-2--shared-javascript-core)
7. [Phase 3 — Shared CSS](#7-phase-3--shared-css)
8. [Phase 4 — Shared View Component](#8-phase-4--shared-view-component)
9. [Phase 5 — Update Existing Views](#9-phase-5--update-existing-views)
10. [Phase 6 — Controller Updates](#10-phase-6--controller-updates)
11. [Phase 7 — BundleConfig & Web.config](#11-phase-7--bundleconfig--webconfig)
12. [Phase 8 — Delete Dead Files](#12-phase-8--delete-dead-files)
13. [Testing Checklist](#13-testing-checklist)
14. [Design Decisions Reference](#14-design-decisions-reference)

---

## 1. System State Analysis

### 1.1 Current Enrollment Entry Points (The Problem)

There are currently **4 separate enrollment implementations** that share no code beyond `enrollment-core.js`:

| Entry Point | View | JS Controller | CSS |
|---|---|---|---|
| Admin → Employee | `Areas/Admin/Views/Employees/Enroll.cshtml` | `Scripts/admin-enroll.js` | `Content/admin-enroll.css` + inline |
| Admin → Visitor | `Areas/Admin/Views/Visitors/Enroll.cshtml` | `Scripts/admin-enroll.js` | `admin-enroll.css` + inline |
| Mobile self-enroll | `Views/MobileRegistration/Enroll.cshtml` | inline `<script>` (600+ lines) | inline `<style>` (200+ lines) |
| Enrollment wizard | `Scripts/enroll-wizard.js` | separate JS file | (legacy) |

Each implements its own:
- Camera start/stop logic
- Face overlay canvas drawing
- Angle guidance prompts
- Capture progress display
- Liveness bar updates
- Form submit handling
- SweetAlert dialogs

### 1.2 What `enrollment-core.js` Already Does Well (DO NOT BREAK)

- Camera start/stop (`startCamera`, `stopCamera`)
- JPEG blob capture (`captureJpegBlob`)
- Server communication (`postScanFrame`, `postEnrollMany`)
- Auto-tick loop (`startAutoEnrollment`, `autoTick`)
- Frame collection and sorting by liveness (`pushGoodFrame`)
- Error description (`describeEnrollError`)
- Callbacks interface (`onStatus`, `onLivenessUpdate`, `onCaptureProgress`, `onEnrollmentComplete`, `onEnrollmentError`)

**Current CONSTANTS in enrollment-core.js:**
```
AUTO_INTERVAL_MS: 250
PASS_WINDOW: 3
PASS_REQUIRED: 1
CAPTURE_WIDTH: 480
CAPTURE_HEIGHT: 360
UPLOAD_QUALITY: 0.65
MAX_IMAGES: 5
MIN_ENROLL_FRAMES: 3
```

### 1.3 What the Kiosk Already Does Well (DO NOT BREAK)

- MediaPipe face detection client-side (blazeface short-range model)
- Face bbox hint sent to server (`faceX, faceY, faceW, faceH`) → saves ~150ms server detection
- FastScanPipeline with parallel liveness+encoding
- FastFaceMatcher RAM cache (~5–20ms lookup)
- BallTree index for 50+ employees
- Burst mode (3 frames, `AttendBurst` endpoint, consensus voting)
- WebSocket fast preview (disabled by default, infrastructure exists)
- DlibBiometrics pool (4 instances, thread-safe)
- Per-request timeout guarding in `KioskController`

### 1.4 Current Server Enrollment Flow (BiometricsController.Enroll)

```
Receive files (up to 8 via form multipart)
→ Parallel.ForEach(files, parallelism=4)
  → SaveTemp → PreprocessForDetection
  → TryDetectSingleFaceFromFile
  → OnnxLiveness.ScoreFromFile (threshold 0.75)
  → TryEncodeFromFileWithLocation
  → Duplicate check (DB query, not cache)
  → Add to ConcurrentBag<EnrollCandidate>
→ Sort candidates by: Liveness DESC, Area DESC
→ Take(maxImages=5)
→ Save to DB: FaceEncodingBase64 (best) + FaceEncodingsJson (all)
→ FastFaceMatcher.UpdateEmployee (cache invalidation)
→ EmployeeFaceIndex.Invalidate
```

**Current EnrollCandidate:**
```csharp
private class EnrollCandidate {
    public double[] Vec { get; set; }
    public float Liveness { get; set; }
    public int Area { get; set; }
}
```

**Problem:** No sharpness scoring, no pose diversity — all 5 stored vectors may be the same front-facing angle.

### 1.5 CSS Duplication Audit

The following CSS definitions exist in **multiple places** and must be consolidated:

| Pattern | Current locations |
|---|---|
| Camera container + video + mirror flip | `admin-enroll.css`, `MobileRegistration/Enroll.cshtml` inline, `Identify.cshtml` inline, `mobile-kiosk.css` |
| Corner bracket drawing | JS canvas in `kiosk.js`, duplicated in `MobileRegistration/Enroll.cshtml` inline JS |
| Face guide overlay circle/dashed | `admin-enroll.css`, `MobileRegistration/Enroll.cshtml` inline |
| Liveness bar | `admin-enroll.css`, `MobileRegistration/Enroll.cshtml` inline |
| Capture progress dots | `admin-enroll.css`, `MobileRegistration/Enroll.cshtml` inline |
| Wizard step pills | `admin-enroll.css`, `MobileRegistration/Enroll.cshtml` inline |
| Status message bar | `admin-enroll.css`, `MobileRegistration/Enroll.cshtml` inline |
| Angle guidance indicator | NEW (plan) — define once |
| Diversity dots | NEW (plan) — define once |

---

## 2. Redundancies to Eliminate

### 2.1 JavaScript Redundancies

**Remove or merge:**

1. `Scripts/admin-enroll.js` — replace with `Scripts/enrollment-ui.js` (new unified controller)
2. `Scripts/enroll-wizard.js` — delete; functionality absorbed
3. Inline `<script>` in `Views/MobileRegistration/Enroll.cshtml` (600+ lines) — replace with `Scripts/enrollment-ui.js`
4. Inline `<script>` in `Areas/Admin/Views/Employees/Enroll.cshtml` (partial JS) — replace
5. `drawCornerBrackets` function exists in both `kiosk.js` and `MobileRegistration/Enroll.cshtml` — move to `enrollment-core.js` as shared utility

**Keep:**
- `Scripts/modules/enrollment-core.js` — extend it, don't replace it
- `Scripts/kiosk.js` — untouched (kiosk attendance is not changing)

### 2.2 CSS Redundancies

**Remove or merge:**
1. All enrollment-related inline `<style>` blocks from every view
2. Duplicate camera/overlay patterns in `admin-enroll.css` and `mobile-kiosk.css`
3. Duplicate animation keyframes (already in `_animations.css` but re-declared in views)

**New single source of truth:**
- `Content/enrollment.css` — all enrollment UI styles for all surfaces

### 2.3 Server-Side Redundancies

1. `MobileRegistrationController.ScanFrame` duplicates `BiometricsController.ScanFrame` — both call `ScanFramePipeline.Run`. The pipeline is already shared. **No change needed here**, but confirm both use `ScanFramePipeline.Run` and not inline detection code.
2. `FindDuplicateEmployeeInDatabase` method exists in both `BiometricsController` and `MobileRegistrationController` — extract to a shared static helper.
3. `EnrollCandidate` private class in `BiometricsController` — promote to internal class in `Services/Biometrics/` so it can be shared.

---

## 3. New Architecture Overview

### 3.1 Enrollment Pipeline (After)

```
[Client — any surface: Admin/Mobile]
  Camera (MediaPipe face detection already running)
  → Every 300ms: captureJpegBlob(640×480, q=0.80)
  → Client pre-filter:
      - face bbox area > 4% of frame → else skip
      - client sharpness (Laplacian on 160×160 ROI) > dynamic threshold → else skip
      - pose bucket from MediaPipe landmarks
  → postScanFrame → server returns { ok, liveness, encoding, faceBox, sharpness }
  → If liveness pass: pushGoodFrame({ blob, liveness, sharpness, poseBucket, encoding })
  → UI updates: angle guidance prompt, diversity dots, progress bar
  → Auto-submit when: 8 frames collected OR all 5 angle buckets captured
  
[Server — BiometricsController.Enroll]
  Receive 8 frames multipart
  → Parallel.ForEach(parallelism=4):
      - SaveTemp → Preprocess
      - TryDetectSingleFaceFromFile
      - FaceQualityAnalyzer.CalculateSharpness (on face ROI)
      - Parallel: OnnxLiveness + TryEncode
      - Duplicate check
      - Build EnrollCandidate with quality fields
  → SelectDiverseFrames(candidates, targetCount=5)
      Phase 1: Best from each pose bucket (center, left, right, up, down)
      Phase 2: Fill remainder by composite quality score
  → Save to DB
  → FastFaceMatcher.UpdateEmployee (immediate cache update)
  → EmployeeFaceIndex.Invalidate
```

### 3.2 File Dependency Tree (After)

```
Content/
  _variables.css          ← unchanged (design tokens)
  _animations.css         ← unchanged (keyframes)
  enrollment.css          ← NEW (all enrollment UI, no duplicates)
  admin.css               ← unchanged
  kiosk.css               ← unchanged
  mobile-kiosk.css        ← remove enrollment-specific sections

Scripts/
  modules/
    enrollment-core.js    ← EXTENDED (add quality, pose, sharpness)
  enrollment-ui.js        ← NEW (unified UI controller, replaces admin-enroll.js)
  kiosk.js                ← unchanged

Services/Biometrics/
  FaceQualityAnalyzer.cs  ← NEW
  EnrollCandidate.cs      ← NEW (extracted from BiometricsController)
  DuplicateCheckHelper.cs ← NEW (extracted from BiometricsController + MobileRegistrationController)
  FastScanPipeline.cs     ← MODIFIED (add sharpness output)
  ScanFramePipeline.cs    ← MODIFIED (add sharpness output)
  FastFaceMatcher.cs      ← unchanged
  BallTreeIndex.cs        ← unchanged
  EmployeeFaceIndex.cs    ← unchanged

Controllers/
  BiometricsController.cs         ← MODIFIED (Enroll action)
  MobileRegistrationController.cs ← MODIFIED (ScanFrame, Enroll actions)

Views/
  Shared/
    _EnrollmentComponent.cshtml   ← NEW (shared partial)
  MobileRegistration/
    Enroll.cshtml                 ← SIMPLIFIED (uses partial, no inline JS/CSS)

Areas/Admin/Views/
  Employees/Enroll.cshtml         ← SIMPLIFIED (uses partial, no inline JS/CSS)
  Visitors/Enroll.cshtml          ← SIMPLIFIED (uses partial, no inline JS/CSS)

App_Start/
  BundleConfig.cs                 ← MODIFIED (add enrollment bundle)

Web.config                        ← MODIFIED (add quality config keys)
```

---

## 4. File Change Manifest

| File | Action | Notes |
|---|---|---|
| `Services/Biometrics/FaceQualityAnalyzer.cs` | **CREATE** | Sharpness + pose scoring utilities |
| `Services/Biometrics/EnrollCandidate.cs` | **CREATE** | Promoted from private inner class |
| `Services/Biometrics/DuplicateCheckHelper.cs` | **CREATE** | Extracted duplicate detection |
| `Services/Biometrics/FastScanPipeline.cs` | **MODIFY** | Add `Sharpness` to `ScanResult` |
| `Services/Biometrics/ScanFramePipeline.cs` | **MODIFY** | Add `sharpness` to response |
| `Services/Biometrics/BiometricsController.cs` | **MODIFY** | New Enroll logic + diversity |
| `Controllers/MobileRegistrationController.cs` | **MODIFY** | Use DuplicateCheckHelper |
| `Controllers/BiometricsController.cs` | **MODIFY** | Use EnrollCandidate + SelectDiverseFrames |
| `Scripts/modules/enrollment-core.js` | **MODIFY** | Add sharpness, pose, diversity state |
| `Scripts/enrollment-ui.js` | **CREATE** | Unified UI controller |
| `Content/enrollment.css` | **CREATE** | All enrollment styles |
| `Content/mobile-kiosk.css` | **MODIFY** | Remove enrollment-specific blocks |
| `Views/Shared/_EnrollmentComponent.cshtml` | **CREATE** | Shared enrollment UI partial |
| `Areas/Admin/Views/Employees/Enroll.cshtml` | **MODIFY** | Use partial, remove inline CSS/JS |
| `Areas/Admin/Views/Visitors/Enroll.cshtml` | **MODIFY** | Use partial, remove inline CSS/JS |
| `Views/MobileRegistration/Enroll.cshtml` | **MODIFY** | Use partial, remove inline CSS/JS |
| `App_Start/BundleConfig.cs` | **MODIFY** | Add `~/bundles/enrollment` |
| `Web.config` | **MODIFY** | Add quality config keys |
| `Scripts/admin-enroll.js` | **DELETE** | Replaced by enrollment-ui.js |
| `Scripts/enroll-wizard.js` | **DELETE** | Absorbed |

---

## 5. Phase 1 — Server Infrastructure

### 5.1 CREATE `Services/Biometrics/EnrollCandidate.cs`

Extract and promote the private `EnrollCandidate` class from `BiometricsController.cs`. Remove the old private class from the controller after creating this file.

```csharp
using System;

namespace FaceAttend.Services.Biometrics
{
    /// <summary>
    /// Represents a candidate face frame collected during enrollment.
    /// Holds both the face vector and all quality signals used for
    /// diversity-aware selection by SelectDiverseFrames.
    /// </summary>
    public class EnrollCandidate
    {
        // Core biometric data
        public double[] Vec { get; set; }
        public float Liveness { get; set; }
        public int Area { get; set; }

        // Quality signals (from FaceQualityAnalyzer)
        public float Sharpness { get; set; }
        public float PoseYaw { get; set; }
        public float PosePitch { get; set; }
        public string PoseBucket { get; set; } = "center";

        // Composite score computed by FaceQualityAnalyzer.CalculateQualityScore
        public float QualityScore { get; set; }
    }
}
```

### 5.2 CREATE `Services/Biometrics/FaceQualityAnalyzer.cs`

**Critical implementation notes:**
- `CalculateSharpness` must accept the full image byte array AND the face bounding box, then crop to the ROI before computing Laplacian. Running Laplacian on the full frame gives noise from backgrounds.
- Downscale the cropped ROI to **160×160** before Laplacian to keep compute cost under 5ms.
- Pose estimation uses dlib face landmarks (68-point model). The `FaceLandmarks` parameter comes from calling `dlib.GetFaceLandmarks(tempPath, faceLocation)` if that method exists, otherwise use the 5-point approximation from the FaceBox geometry.
- `GetPoseBucket` must return exactly `"center"`, `"left"`, `"right"`, `"up"`, `"down"`, or `"other"` — these strings are matched against the JS diversity dot data-bucket attributes.

```csharp
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using FaceAttend.Services.Biometrics;
using OpenCvSharp;

namespace FaceAttend.Services.Biometrics
{
    /// <summary>
    /// Static utilities for computing face image quality signals during enrollment.
    ///
    /// ALL METHODS ARE STATELESS AND THREAD-SAFE.
    ///
    /// Sharpness: Laplacian variance on a 160×160 face ROI crop.
    ///   Computing on full frame is wrong — background texture inflates the score.
    ///   160×160 is ~25x cheaper than 1280×720 with no meaningful accuracy loss.
    ///
    /// Pose: Estimated from FaceBox geometry (eye-region, nose, chin proportions).
    ///   Buckets match JavaScript enrollment-core.js estimatePoseBucket() output.
    ///   IMPORTANT: bucket string literals must match exactly or diversity selection breaks.
    ///
    /// Quality score weights (configurable via Web.config):
    ///   Liveness    0.40  — most important; a fake face is worthless
    ///   Sharpness   0.30  — blurry encoding hurts matching
    ///   Area        0.20  — larger face = more detail for dlib
    ///   Pose        0.10  — frontal is slightly better than extreme angle
    /// </summary>
    public static class FaceQualityAnalyzer
    {
        // ── Sharpness ────────────────────────────────────────────────────────────

        /// <summary>
        /// Computes Laplacian variance on the face ROI (cropped + downscaled to 160×160).
        /// Higher = sharper. Typical good value: 80–300. Below 60 is blurry.
        /// Returns 0 on any error (treat as failed frame).
        /// </summary>
        /// <param name="imagePath">Full path to temp image file.</param>
        /// <param name="faceBox">Bounding box of the detected face.</param>
        public static float CalculateSharpness(string imagePath, DlibBiometrics.FaceBox faceBox)
        {
            if (imagePath == null || faceBox == null) return 0f;

            try
            {
                using (var full = Cv2.ImRead(imagePath, ImreadModes.Grayscale))
                {
                    if (full.Empty()) return 0f;

                    // Clamp ROI to image bounds
                    int x = Math.Max(0, faceBox.Left);
                    int y = Math.Max(0, faceBox.Top);
                    int w = Math.Min(faceBox.Width, full.Cols - x);
                    int h = Math.Min(faceBox.Height, full.Rows - y);

                    if (w <= 0 || h <= 0) return 0f;

                    // Crop to face ROI
                    var roi = new Rect(x, y, w, h);
                    using (var crop = new Mat(full, roi))
                    // Downscale to 160×160 for speed (~25x faster than full res)
                    using (var small = new Mat())
                    {
                        Cv2.Resize(crop, small, new OpenCvSharp.Size(160, 160));

                        // Laplacian
                        using (var lap = new Mat())
                        {
                            Cv2.Laplacian(small, lap, MatType.CV_64F);
                            Cv2.MeanStdDev(lap, out var mean, out var stddev);
                            // Variance = stddev^2
                            return (float)(stddev.Val0 * stddev.Val0);
                        }
                    }
                }
            }
            catch
            {
                return 0f;
            }
        }

        // ── Pose ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Estimates yaw and pitch from FaceBox geometry.
        /// Uses horizontal asymmetry of the box relative to full image width
        /// as a proxy for head turn. This is a rough estimate — good enough
        /// for bucket classification but not precise degrees.
        ///
        /// Returns (yaw, pitch) in approximate degrees:
        ///   yaw > 0 = face turned right, yaw < 0 = face turned left
        ///   pitch > 0 = face tilted down, pitch < 0 = face tilted up
        /// </summary>
        public static (float yaw, float pitch) EstimatePose(
            DlibBiometrics.FaceBox faceBox, int imageWidth, int imageHeight)
        {
            if (faceBox == null || imageWidth <= 0 || imageHeight <= 0)
                return (0f, 0f);

            // Face center relative to image center, normalized -1..+1
            float faceCenterX = (faceBox.Left + faceBox.Width / 2f) / imageWidth;
            float faceCenterY = (faceBox.Top + faceBox.Height / 2f) / imageHeight;

            // Map center offset to approximate degrees
            float yaw   = (faceCenterX - 0.5f) * 60f;   // ±30° range
            float pitch = (faceCenterY - 0.5f) * 40f;   // ±20° range

            // Face aspect ratio hint: tall narrow box = looking up/down
            float aspectRatio = faceBox.Width > 0
                ? (float)faceBox.Height / faceBox.Width
                : 1f;

            if (aspectRatio > 1.4f) pitch -= 10f;  // very tall = looking up
            if (aspectRatio < 0.8f) pitch += 10f;  // very wide = looking down

            return (yaw, pitch);
        }

        /// <summary>
        /// Classifies (yaw, pitch) into a named pose bucket.
        /// IMPORTANT: Return values must exactly match the JavaScript
        /// estimatePoseBucket() in enrollment-core.js and data-bucket
        /// attributes on diversity dots in _EnrollmentComponent.cshtml.
        /// Valid returns: "center", "left", "right", "up", "down", "other"
        /// "other" means extreme angle — frame should be discarded.
        /// </summary>
        public static string GetPoseBucket(float yaw, float pitch)
        {
            float absYaw   = Math.Abs(yaw);
            float absPitch = Math.Abs(pitch);

            // Extreme angles — discard
            if (absYaw > 30f || absPitch > 25f) return "other";

            // Center zone
            if (absYaw < 10f && absPitch < 10f) return "center";

            // Dominant axis determines bucket
            if (absYaw >= absPitch)
            {
                if (yaw < -10f) return "left";
                if (yaw >  10f) return "right";
            }
            else
            {
                if (pitch < -10f) return "up";
                if (pitch >  10f) return "down";
            }

            return "center"; // Within threshold on both axes
        }

        // ── Quality Score ────────────────────────────────────────────────────────

        /// <summary>
        /// Computes a normalized 0–1 composite quality score.
        /// Weights are read from Web.config at call time (no caching —
        /// they rarely change and this avoids stale config).
        ///
        /// Weights must sum to 1.0. If config is wrong they still work
        /// because each component is individually clamped 0–1.
        /// </summary>
        public static float CalculateQualityScore(
            float liveness, float sharpness, int area, float yaw, float pitch)
        {
            var wLiveness  = (float)ConfigurationService.GetDouble(
                "Biometrics:Enroll:Quality:LivenessWeight",  0.40);
            var wSharpness = (float)ConfigurationService.GetDouble(
                "Biometrics:Enroll:Quality:SharpnessWeight", 0.30);
            var wArea      = (float)ConfigurationService.GetDouble(
                "Biometrics:Enroll:Quality:AreaWeight",      0.20);
            var wPose      = (float)ConfigurationService.GetDouble(
                "Biometrics:Enroll:Quality:PoseWeight",      0.10);

            // Normalize each signal to 0–1
            float normSharpness  = Math.Min(sharpness / 300f, 1f);
            float normArea       = Math.Min(area      / 50000f, 1f);
            float poseCentrality = 1f - Math.Min(
                (Math.Abs(yaw) + Math.Abs(pitch)) / 60f, 1f);

            return (liveness        * wLiveness)
                 + (normSharpness   * wSharpness)
                 + (normArea        * wArea)
                 + (poseCentrality  * wPose);
        }

        // ── Thresholds ────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the sharpness threshold appropriate for the context.
        /// Mobile cameras produce lower Laplacian variance than desktop webcams.
        /// </summary>
        public static float GetSharpnessThreshold(bool isMobile)
        {
            var key = isMobile
                ? "Biometrics:Enroll:SharpnessThreshold:Mobile"
                : "Biometrics:Enroll:SharpnessThreshold";

            return (float)ConfigurationService.GetDouble(key, isMobile ? 50.0 : 80.0);
        }
    }
}
```

**Prerequisite:** OpenCvSharp must be installed. Check `packages.config` — if `OpenCvSharp4` or `OpenCvSharp` is already present (it likely is since `FaceRecognitionDotNet` depends on it), use the existing version. Do not add a new package if one already exists.

**If OpenCvSharp is NOT available:** Implement a pure C# fallback using `System.Drawing`:

```csharp
// FALLBACK: Pure C# Laplacian (no OpenCvSharp dependency)
// Slower (~3x) but works without native dependencies.
public static float CalculateSharpnessFallback(string imagePath, DlibBiometrics.FaceBox faceBox)
{
    try
    {
        using (var full = new Bitmap(imagePath))
        {
            // Crop ROI
            int x = Math.Max(0, faceBox.Left);
            int y = Math.Max(0, faceBox.Top);
            int w = Math.Min(faceBox.Width,  full.Width  - x);
            int h = Math.Min(faceBox.Height, full.Height - y);
            if (w <= 0 || h <= 0) return 0f;

            using (var roi = full.Clone(new Rectangle(x, y, w, h), full.PixelFormat))
            using (var small = new Bitmap(roi, 160, 160))
            {
                // Convert to grayscale float array
                var gray = new float[160 * 160];
                for (int py = 0; py < 160; py++)
                for (int px = 0; px < 160; px++)
                {
                    var c = small.GetPixel(px, py);
                    gray[py * 160 + px] = 0.299f * c.R + 0.587f * c.G + 0.114f * c.B;
                }

                // Laplacian variance
                float sum = 0f, sumSq = 0f;
                int count = 0;
                for (int py = 1; py < 159; py++)
                for (int px = 1; px < 159; px++)
                {
                    int i = py * 160 + px;
                    float lap = -4f * gray[i]
                        + gray[i - 1] + gray[i + 1]
                        + gray[i - 160] + gray[i + 160];
                    sum += lap;
                    sumSq += lap * lap;
                    count++;
                }
                float mean = sum / count;
                return (sumSq / count) - (mean * mean); // Variance
            }
        }
    }
    catch { return 0f; }
}
```

### 5.3 CREATE `Services/Biometrics/DuplicateCheckHelper.cs`

Extract the `FindDuplicateEmployeeInDatabase` method that currently exists as a private method in both `BiometricsController.cs` and `MobileRegistrationController.cs`. After creating this file, replace both private implementations with calls to this helper.

```csharp
using System.Linq;

namespace FaceAttend.Services.Biometrics
{
    /// <summary>
    /// Shared helper for duplicate face detection during enrollment.
    ///
    /// WHY NOT USE FastFaceMatcher CACHE:
    ///   The cache can be stale immediately after enrollment of a different employee.
    ///   During enrollment we must query the database directly to get an accurate result.
    ///   The cache is appropriate for kiosk attendance (read-only, high frequency).
    ///   It is NOT appropriate for enrollment duplicate checks (write path, accuracy critical).
    /// </summary>
    public static class DuplicateCheckHelper
    {
        /// <summary>
        /// Checks whether the given face vector already exists in the database
        /// for any active employee other than excludeEmployeeId.
        ///
        /// Returns the EmployeeId of the matching employee, or null if no duplicate.
        /// Uses a strict tolerance (typically 0.45) to avoid false positives.
        /// </summary>
        public static string FindDuplicate(
            FaceAttendDBEntities db,
            double[] faceVector,
            string excludeEmployeeId,
            double tolerance)
        {
            if (faceVector == null || faceVector.Length != 128)
                return null;

            var employees = db.Employees
                .Where(e => e.Status == "ACTIVE"
                         && e.EmployeeId != excludeEmployeeId
                         && (e.FaceEncodingBase64 != null || e.FaceEncodingsJson != null))
                .Select(e => new {
                    e.EmployeeId,
                    e.FaceEncodingBase64,
                    e.FaceEncodingsJson
                })
                .ToList();

            foreach (var emp in employees)
            {
                var vectors = FaceEncodingHelper.LoadEmployeeVectors(
                    emp.FaceEncodingBase64,
                    emp.FaceEncodingsJson,
                    maxPerEmployee: 5);

                foreach (var vec in vectors)
                {
                    if (vec != null && vec.Length == 128)
                    {
                        if (DlibBiometrics.Distance(faceVector, vec) <= tolerance)
                            return emp.EmployeeId;
                    }
                }
            }

            return null;
        }
    }
}
```

### 5.4 MODIFY `Services/Biometrics/FastScanPipeline.cs`

Add `Sharpness` to `ScanResult` and compute it after detection (not parallel — it's fast enough serial).

**In the `ScanResult` class, add:**
```csharp
public float Sharpness { get; set; }
```

**After the face detection step (after `RecordTiming(timings, "detect", sw);`), add:**
```csharp
// Compute sharpness on face ROI (fast — runs on 160×160 crop)
result.Sharpness = FaceQualityAnalyzer.CalculateSharpness(tempPath, faceBox);
RecordTiming(timings, "sharpness_ms", sw);
```

**The complete ScanResult class after modification:**
```csharp
public class ScanResult
{
    public bool Ok { get; set; }
    public string Error { get; set; }
    public double[] FaceEncoding { get; set; }
    public float LivenessScore { get; set; }
    public bool LivenessOk { get; set; }
    public DlibBiometrics.FaceBox FaceBox { get; set; }
    public float Sharpness { get; set; }          // NEW
    public long TimingMs { get; set; }
    public Dictionary<string, long> Timings { get; set; }
}
```

### 5.5 MODIFY `Services/Biometrics/ScanFramePipeline.cs`

Add `sharpness` to the response object in both the fast pipeline path and the legacy path.

**In the fast pipeline path, add `sharpness` to the return:**
```csharp
return new
{
    ok = true,
    count = 1,
    liveness    = fastResult.LivenessScore,
    livenessOk  = fastResult.LivenessOk,
    sharpness   = fastResult.Sharpness,    // NEW
    faceBox     = fastResult.FaceBox != null ? new {
        x = fastResult.FaceBox.Left,
        y = fastResult.FaceBox.Top,
        w = fastResult.FaceBox.Width,
        h = fastResult.FaceBox.Height
    } : null,
    message = "scan complete (fast pipeline)"
};
```

**In the legacy path, compute sharpness after detection and add to return:**
```csharp
// After TryDetectSingleFaceFromFile succeeds:
var sharpness = FaceQualityAnalyzer.CalculateSharpness(processedPath, mainFace);

// In the return object:
return new
{
    ok         = true,
    count      = count,
    liveness   = p,
    livenessOk = p >= th,
    sharpness  = sharpness,   // NEW
    faceBox,
    // ... existing fields
};
```

### 5.6 MODIFY `Controllers/BiometricsController.cs`

**Step A: Remove the private `EnrollCandidate` class** — it is now `Services/Biometrics/EnrollCandidate.cs`.

**Step B: Remove the private `FindDuplicateEmployeeInDatabase` method** — replaced by `DuplicateCheckHelper.FindDuplicate`.

**Step C: Update `Enroll()` to use diversity selection.**

Replace the existing `Enroll` action with this implementation. Read the existing action first to preserve all validation, file-count limits, and error handling — only replace the candidate processing and selection logic.

```csharp
[HttpPost]
[ValidateAntiForgeryToken]
public ActionResult Enroll(string employeeId)
{
    var sw = Stopwatch.StartNew();

    // ── Input validation ──────────────────────────────────────────────────
    if (string.IsNullOrWhiteSpace(employeeId))
        return JsonResponseBuilder.Error("NO_EMPLOYEE_ID");

    employeeId = employeeId.Trim().ToUpper();
    if (employeeId.Length > 20)
        return JsonResponseBuilder.Error("EMPLOYEE_ID_TOO_LONG");

    var files = new List<HttpPostedFileBase>();
    for (int i = 0; i < Request.Files.Count; i++)
    {
        var f = Request.Files[i];
        if (f != null && f.ContentLength > 0) files.Add(f);
    }

    if (files.Count == 0)
        return JsonResponseBuilder.Error("NO_IMAGE");

    var maxBytes  = ConfigurationService.GetInt("Biometrics:MaxUploadBytes", 10 * 1024 * 1024);
    var maxImages = ConfigurationService.GetInt("Biometrics:Enroll:CaptureTarget", 8);
    var maxStored = ConfigurationService.GetInt("Biometrics:Enroll:MaxStoredVectors", 5);

    files = files.Take(maxImages).ToList();

    // Validate employee exists
    Employee emp;
    using (var db = new FaceAttendDBEntities())
    {
        emp = db.Employees.FirstOrDefault(e => e.EmployeeId == employeeId);
        if (emp == null)
            return JsonResponseBuilder.Error("EMPLOYEE_NOT_FOUND");
    }

    var strictTol = ConfigurationService.GetDouble(
        "Biometrics:EnrollmentStrictTolerance", 0.45);
    var isMobile = DeviceService.IsMobileDevice(Request);

    var candidates     = new ConcurrentBag<EnrollCandidate>();
    int processedCount = 0;
    bool duplicateFound = false;
    string duplicateId  = null;
    var lockObj = new object();

    var parallelism = Math.Min(files.Count,
        ConfigurationService.GetInt("Biometrics:Enroll:Parallelism", 4));

    // ── Parallel frame processing ──────────────────────────────────────────
    Parallel.ForEach(files,
        new ParallelOptions { MaxDegreeOfParallelism = parallelism },
        (f, state) =>
    {
        if (duplicateFound) return;

        string path = null, processedPath = null;
        try
        {
            // File security validation
            if (!FileSecurityService.IsValidImage(
                    f.InputStream, new[] { ".jpg", ".jpeg", ".png" }))
                return;

            f.InputStream.Position = 0;
            path          = FileSecurityService.SaveTemp(f, "enr_", maxBytes);
            bool isProc;
            processedPath = ImagePreprocessor.PreprocessForDetection(
                path, "enr_", out isProc);

            var dlib = new DlibBiometrics();
            DlibBiometrics.FaceBox faceBox;
            FaceRecognitionDotNet.Location faceLocation;
            string detectErr;

            if (!dlib.TryDetectSingleFaceFromFile(
                    processedPath, out faceBox, out faceLocation, out detectErr))
                return;

            // Sharpness check (fast — ROI crop, 160×160)
            var sharpness  = FaceQualityAnalyzer.CalculateSharpness(processedPath, faceBox);
            var sharpTh    = FaceQualityAnalyzer.GetSharpnessThreshold(isMobile);
            if (sharpness < sharpTh) return; // reject blurry frame

            // Parallel: liveness + encoding
            double[] vec        = null;
            float    liveness   = 0f;
            bool     liveOk     = false;

            Parallel.Invoke(
                () => {
                    string encErr;
                    dlib.TryEncodeFromFileWithLocation(
                        processedPath, faceLocation, out vec, out encErr);
                },
                () => {
                    var live   = new OnnxLiveness();
                    var scored = live.ScoreFromFile(processedPath, faceBox);
                    liveOk     = scored.Ok;
                    liveness   = scored.Probability ?? 0f;
                });

            var liveTh = (float)ConfigurationService.GetDouble(
                "Biometrics:Enroll:LivenessThreshold", 0.75);

            if (!liveOk || liveness < liveTh || vec == null) return;

            // Duplicate check (database, not cache)
            lock (lockObj)
            {
                processedCount++;
                if (!duplicateFound)
                {
                    using (var checkDb = new FaceAttendDBEntities())
                    {
                        var dup = DuplicateCheckHelper.FindDuplicate(
                            checkDb, vec, employeeId, strictTol);

                        if (!string.IsNullOrEmpty(dup))
                        {
                            duplicateFound = true;
                            duplicateId    = dup;
                            state.Stop();
                            return;
                        }
                    }
                }
            }

            if (duplicateFound) return;

            // Pose estimation
            int imgW = 640, imgH = 480; // Default; try to read actual dimensions
            try {
                using (var bmp = new System.Drawing.Bitmap(processedPath))
                { imgW = bmp.Width; imgH = bmp.Height; }
            } catch { }

            var (yaw, pitch) = FaceQualityAnalyzer.EstimatePose(faceBox, imgW, imgH);
            var bucket       = FaceQualityAnalyzer.GetPoseBucket(yaw, pitch);

            if (bucket == "other") return; // extreme angle — discard

            int area = Math.Max(0, faceBox.Width) * Math.Max(0, faceBox.Height);

            candidates.Add(new EnrollCandidate
            {
                Vec        = vec,
                Liveness   = liveness,
                Area       = area,
                Sharpness  = sharpness,
                PoseYaw    = yaw,
                PosePitch  = pitch,
                PoseBucket = bucket,
                QualityScore = FaceQualityAnalyzer.CalculateQualityScore(
                    liveness, sharpness, area, yaw, pitch)
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning(
                "[BiometricsController.Enroll] Frame error: " + ex.Message);
        }
        finally
        {
            ImagePreprocessor.Cleanup(processedPath, path);
            FileSecurityService.TryDelete(path);
        }
    });

    // ── Duplicate found ────────────────────────────────────────────────────
    if (duplicateFound)
    {
        return JsonResponseBuilder.Error("FACE_ALREADY_ENROLLED",
            details: new {
                step         = "duplicate_check",
                matchEmployeeId = duplicateId,
                processed    = processedCount,
                timeMs       = sw.ElapsedMilliseconds
            });
    }

    if (candidates.IsEmpty)
    {
        return JsonResponseBuilder.Error("NO_GOOD_FRAME",
            details: new {
                step      = "processing",
                processed = processedCount,
                timeMs    = sw.ElapsedMilliseconds
            });
    }

    // ── Diversity-aware selection ─────────────────────────────────────────
    var selected = SelectDiverseFrames(candidates.ToList(), maxStored);

    // ── Save to database ──────────────────────────────────────────────────
    using (var db = new FaceAttendDBEntities())
    {
        emp = db.Employees.First(e => e.EmployeeId == employeeId);

        // Best single vector (primary, for legacy systems)
        var bestBytes = DlibBiometrics.EncodeToBytes(selected[0].Vec);
        emp.FaceEncodingBase64 = BiometricCrypto.ProtectBase64Bytes(bestBytes);

        // All selected vectors as JSON (multi-vector matching)
        var jsonList = selected.Select(c =>
            BiometricCrypto.ProtectBase64Bytes(DlibBiometrics.EncodeToBytes(c.Vec))
        ).ToList();
        emp.FaceEncodingsJson = BiometricCrypto.ProtectString(
            Newtonsoft.Json.JsonConvert.SerializeObject(jsonList));

        emp.EnrolledDate = emp.EnrolledDate ?? DateTime.UtcNow;
        emp.Status       = "ACTIVE";

        db.SaveChanges();
    }

    // ── Cache invalidation ────────────────────────────────────────────────
    FastFaceMatcher.UpdateEmployee(employeeId, selected.Select(c => c.Vec).ToList());
    EmployeeFaceIndex.Invalidate();

    return JsonResponseBuilder.Success(new
    {
        savedVectors = selected.Count,
        timeMs       = sw.ElapsedMilliseconds,
        poseBuckets  = selected.Select(c => c.PoseBucket).ToList()
    });
}

/// <summary>
/// Selects up to targetCount candidates with pose diversity priority.
///
/// Phase 1: Pick the highest-quality candidate from each desired pose bucket.
///   This guarantees diversity if the user cooperated with angle guidance.
///
/// Phase 2: Fill remaining slots with highest composite quality score
///   regardless of bucket. This handles cases where the user only did
///   center poses — we still get the best available frames.
///
/// Result is sorted by QualityScore descending (best first).
/// </summary>
private static List<EnrollCandidate> SelectDiverseFrames(
    List<EnrollCandidate> candidates, int targetCount)
{
    var desiredBuckets = new[] { "center", "left", "right", "up", "down" };
    var selected = new List<EnrollCandidate>();

    // Phase 1: best from each bucket
    foreach (var bucket in desiredBuckets)
    {
        if (selected.Count >= targetCount) break;

        var best = candidates
            .Where(c => c.PoseBucket == bucket && !selected.Contains(c))
            .OrderByDescending(c => c.QualityScore)
            .FirstOrDefault();

        if (best != null) selected.Add(best);
    }

    // Phase 2: fill with highest quality regardless of bucket
    var remaining = candidates
        .Where(c => !selected.Contains(c))
        .OrderByDescending(c => c.QualityScore)
        .Take(targetCount - selected.Count);

    selected.AddRange(remaining);

    return selected
        .OrderByDescending(c => c.QualityScore)
        .ToList();
}
```

### 5.7 MODIFY `Controllers/MobileRegistrationController.cs`

**Remove the private `FindDuplicateEmployeeInDatabase` method** and replace all calls to it with `DuplicateCheckHelper.FindDuplicate(db, vec, null, tolerance)`.

No other changes needed in this controller — the mobile ScanFrame endpoint already uses `ScanFramePipeline.Run` which now returns sharpness.

---

## 6. Phase 2 — Shared JavaScript Core

### 6.1 MODIFY `Scripts/modules/enrollment-core.js`

Add the following new methods and update the CONSTANTS. Do NOT remove any existing methods. Do NOT change the existing `autoTick`, `processScanResult`, `pushGoodFrame`, `postEnrollMany`, or camera methods.

**Step A: Update CONSTANTS block**

Replace the existing CONSTANTS object:
```javascript
var CONSTANTS = {
    // Capture timing
    AUTO_INTERVAL_MS: 300,        // Was 250. 300ms gives pose-change time between frames.
    PASS_WINDOW:      3,
    PASS_REQUIRED:    1,

    // Frame targets
    CAPTURE_TARGET:    8,         // Collect up to 8 frames before submitting
    MIN_GOOD_FRAMES:   3,         // Minimum to allow manual submit
    MAX_KEEP_FRAMES:   8,         // Keep best 8 in goodFrames array

    // Image capture
    CAPTURE_WIDTH:  640,          // Was 480. Larger = better dlib encoding quality.
    CAPTURE_HEIGHT: 480,          // Was 360.
    UPLOAD_QUALITY: 0.80,         // Was 0.65. Higher quality for diverse-angle enrollment.

    // Quality thresholds (matched by server)
    SHARPNESS_THRESHOLD_DESKTOP: 80,
    SHARPNESS_THRESHOLD_MOBILE:  50,
    SHARPNESS_SAMPLE_SIZE:       160,  // Resize ROI to 160×160 before Laplacian

    // Angle buckets
    ANGLE_SEQUENCE: ['center', 'left', 'right', 'up', 'down'],

    // Auto-submit trigger
    AUTO_SUBMIT_ON_ALL_ANGLES: true  // Submit early if all 5 angle buckets captured
};
```

**Step B: Add `isMobileDevice()` utility**
```javascript
function isMobileDevice() {
    return /iPhone|iPad|iPod|Android.*Mobile|Windows Phone/i.test(navigator.userAgent);
}
```

**Step C: Add `calculateSharpness(canvas, faceBox)` method**

Add as `Enrollment.prototype.calculateSharpness`. The face ROI crop is critical — do not remove it.

```javascript
/**
 * Computes Laplacian variance on face ROI, downscaled to 160×160.
 * Measures image sharpness — higher = sharper.
 * Running on full canvas is wrong (background texture inflates score).
 *
 * @param {HTMLCanvasElement} canvas  Source canvas (full frame)
 * @param {object|null}       faceBox { x, y, w, h } in canvas coordinates.
 *                                    If null, uses center 60% of frame.
 * @returns {number} Laplacian variance (0 = error/no face)
 */
Enrollment.prototype.calculateSharpness = function(canvas, faceBox) {
    var W = CONSTANTS.SHARPNESS_SAMPLE_SIZE;
    var H = CONSTANTS.SHARPNESS_SAMPLE_SIZE;
    var tmp = document.createElement('canvas');
    tmp.width  = W;
    tmp.height = H;
    var tCtx = tmp.getContext('2d');

    // Use face ROI if available, else center crop
    var sx, sy, sw, sh;
    if (faceBox && faceBox.w > 0 && faceBox.h > 0) {
        sx = faceBox.x;
        sy = faceBox.y;
        sw = faceBox.w;
        sh = faceBox.h;
    } else {
        sx = canvas.width  * 0.2;
        sy = canvas.height * 0.1;
        sw = canvas.width  * 0.6;
        sh = canvas.height * 0.8;
    }

    // Clamp to canvas bounds
    sx = Math.max(0, Math.min(sx, canvas.width  - 1));
    sy = Math.max(0, Math.min(sy, canvas.height - 1));
    sw = Math.min(sw, canvas.width  - sx);
    sh = Math.min(sh, canvas.height - sy);

    if (sw <= 0 || sh <= 0) return 0;

    tCtx.drawImage(canvas, sx, sy, sw, sh, 0, 0, W, H);
    var imgData = tCtx.getImageData(0, 0, W, H).data;

    // Convert to grayscale
    var gray = new Float32Array(W * H);
    for (var i = 0; i < imgData.length; i += 4) {
        gray[i >> 2] = 0.299 * imgData[i] + 0.587 * imgData[i+1] + 0.114 * imgData[i+2];
    }

    // Laplacian 3×3 kernel: [0,1,0,1,-4,1,0,1,0]
    var sum = 0, sumSq = 0, count = 0;
    for (var y = 1; y < H - 1; y++) {
        for (var x = 1; x < W - 1; x++) {
            var idx = y * W + x;
            var lap = gray[idx - W] + gray[idx - 1]
                    - 4 * gray[idx]
                    + gray[idx + 1] + gray[idx + W];
            sum   += lap;
            sumSq += lap * lap;
            count++;
        }
    }
    if (count === 0) return 0;
    var mean = sum / count;
    return (sumSq / count) - (mean * mean); // Variance
};
```

**Step D: Add `estimatePoseBucket(mpLandmarks)` method**

This uses MediaPipe landmarks if available. MediaPipe landmarks use normalized 0–1 coordinates relative to the image.

```javascript
/**
 * Estimates pose bucket from MediaPipe face detection landmarks.
 * MediaPipe blazeface returns 6 landmarks: 0=rightEye, 1=leftEye,
 * 2=nose, 3=mouth, 4=rightEar, 5=leftEar (normalized 0-1 coords).
 *
 * If no landmarks are provided, returns 'center' (safe default).
 *
 * Return values MUST exactly match server-side FaceQualityAnalyzer.GetPoseBucket()
 * and data-bucket attributes on diversity dots: center, left, right, up, down.
 *
 * @param {Array|null} landmarks  Array of {x, y} normalized landmark points
 * @param {object|null} faceBox   { x, y, w, h } in canvas pixels
 * @param {number} canvasW        Canvas width in pixels
 * @param {number} canvasH        Canvas height in pixels
 */
Enrollment.prototype.estimatePoseBucket = function(landmarks, faceBox, canvasW, canvasH) {
    if (!faceBox || faceBox.w <= 0) return 'center';

    // Use face center offset relative to full frame as yaw proxy
    var faceCenterX = (faceBox.x + faceBox.w / 2) / (canvasW || 640);
    var faceCenterY = (faceBox.y + faceBox.h / 2) / (canvasH || 480);

    var yaw   = (faceCenterX - 0.5) * 60;   // approx degrees, ±30 range
    var pitch = (faceCenterY - 0.5) * 40;   // approx degrees, ±20 range

    // Aspect ratio hint: narrow vertical box = looking up
    var aspect = faceBox.h / faceBox.w;
    if (aspect > 1.4) pitch -= 10;
    if (aspect < 0.8) pitch += 10;

    // If MediaPipe 6-point landmarks available, use nose offset for better yaw
    if (landmarks && landmarks.length >= 3) {
        var rEye = landmarks[0], lEye = landmarks[1], nose = landmarks[2];
        if (rEye && lEye && nose) {
            var eyeMidX = (rEye.x + lEye.x) / 2;
            var noseDeltaX = (nose.x - eyeMidX) * 100;
            yaw = noseDeltaX * 1.5; // scale to degrees
        }
    }

    var absYaw   = Math.abs(yaw);
    var absPitch = Math.abs(pitch);

    if (absYaw > 30 || absPitch > 25) return 'other';
    if (absYaw < 10 && absPitch < 10) return 'center';

    if (absYaw >= absPitch) {
        if (yaw < -10) return 'left';
        if (yaw >  10) return 'right';
    } else {
        if (pitch < -10) return 'up';
        if (pitch >  10) return 'down';
    }

    return 'center';
};
```

**Step E: Add `getNextAnglePrompt()` method**

```javascript
/**
 * Returns the next angle to prompt for, based on which buckets have been captured.
 * Used by the UI layer to show angle guidance.
 *
 * @returns {{ bucket: string, prompt: string, icon: string }}
 */
Enrollment.prototype.getNextAnglePrompt = function() {
    var captured = {};
    for (var i = 0; i < this.goodFrames.length; i++) {
        var b = this.goodFrames[i].poseBucket;
        if (b) captured[b] = true;
    }

    var prompts = {
        center: { prompt: 'Look straight at the camera',   icon: 'fa-circle-dot' },
        left:   { prompt: 'Turn your head slightly LEFT',  icon: 'fa-arrow-left' },
        right:  { prompt: 'Turn your head slightly RIGHT', icon: 'fa-arrow-right' },
        up:     { prompt: 'Tilt your head slightly UP',    icon: 'fa-arrow-up' },
        down:   { prompt: 'Tilt your head slightly DOWN',  icon: 'fa-arrow-down' }
    };

    for (var j = 0; j < CONSTANTS.ANGLE_SEQUENCE.length; j++) {
        var bucket = CONSTANTS.ANGLE_SEQUENCE[j];
        if (!captured[bucket]) {
            return { bucket: bucket, prompt: prompts[bucket].prompt, icon: prompts[bucket].icon };
        }
    }

    return { bucket: 'center', prompt: 'Hold still — capturing final frames', icon: 'fa-check' };
};
```

**Step F: Update `autoTick` to include sharpness and pose**

Replace the existing `autoTick` method with this version. The key additions are: sharpness pre-filter before server call, poseBucket from scan result, auto-submit on all-angles-captured:

```javascript
Enrollment.prototype.autoTick = function() {
    var self = this;
    if (this.enrolled || !this.stream || this.busy) return;

    // Pre-filter: skip if face is not good quality
    // (faceBox set externally by UI layer via enrollment.lastFaceBox)
    var cam = this.elements.cam;
    if (!cam || !cam.videoWidth) return;

    this.busy = true;

    var capturedBlob = null;
    var faceBox      = this.lastFaceBox || null; // set by UI layer from MediaPipe

    // Quick client-side sharpness check BEFORE upload
    // Saves a server round-trip for blurry frames
    var sharpnessOk = true;
    if (cam.videoWidth > 0) {
        var tmpCanvas = document.createElement('canvas');
        tmpCanvas.width  = CONSTANTS.CAPTURE_WIDTH;
        tmpCanvas.height = CONSTANTS.CAPTURE_HEIGHT;
        var tmpCtx = tmpCanvas.getContext('2d');
        tmpCtx.drawImage(cam, 0, 0, CONSTANTS.CAPTURE_WIDTH, CONSTANTS.CAPTURE_HEIGHT);

        var threshold = isMobileDevice()
            ? CONSTANTS.SHARPNESS_THRESHOLD_MOBILE
            : CONSTANTS.SHARPNESS_THRESHOLD_DESKTOP;
        var sharpness = this.calculateSharpness(tmpCanvas, faceBox);

        if (sharpness < threshold) {
            sharpnessOk = false;
            this.busy   = false;
            if (this.callbacks.onStatus) {
                this.callbacks.onStatus(
                    'Image blurry (score: ' + Math.round(sharpness) +
                    '). Move closer or improve lighting.', 'warning');
            }
            return;
        }
    }

    this.captureJpegBlob(CONSTANTS.UPLOAD_QUALITY)
        .then(function(blob) {
            capturedBlob = blob;
            return self.postScanFrame(blob);
        })
        .then(function(result) {
            if (result) result.lastBlob = capturedBlob;
            self.processScanResult(result);
        })
        .catch(function(e) {
            self.handleError('Auto enroll failed: ' + (e && e.message ? e.message : e));
            self.passHist  = [];
            self.goodFrames = [];
        })
        .finally(function() { self.busy = false; });
};
```

**Step G: Update `processScanResult` to store poseBucket and check auto-submit**

At the end of the existing `processScanResult` method, after `pushGoodFrame(...)` is called, add:

```javascript
// Store poseBucket on the frame just added
if (this.goodFrames.length > 0) {
    var latestFrame = this.goodFrames[0]; // sorted by liveness, first is most recent push
    // Find the frame we just added (it may not be index 0 if liveness was low)
    // Use lastBlob reference to find it
    if (r.lastBlob) {
        for (var k = 0; k < this.goodFrames.length; k++) {
            if (this.goodFrames[k].blob === r.lastBlob) {
                this.goodFrames[k].poseBucket = this.estimatePoseBucket(
                    null, r.faceBox, CONSTANTS.CAPTURE_WIDTH, CONSTANTS.CAPTURE_HEIGHT);
                this.goodFrames[k].sharpness = r.sharpness || 0;
                break;
            }
        }
    }
}

// Notify UI of next angle needed
if (this.callbacks.onAngleUpdate) {
    this.callbacks.onAngleUpdate(this.getNextAnglePrompt());
}

// Check auto-submit conditions
var capturedBuckets = {};
for (var m = 0; m < this.goodFrames.length; m++) {
    var b = this.goodFrames[m].poseBucket;
    if (b && b !== 'other') capturedBuckets[b] = true;
}
var allAngles = CONSTANTS.ANGLE_SEQUENCE.every(function(bucket) {
    return capturedBuckets[bucket];
});

var hasEnoughFrames = this.goodFrames.length >= this.config.minGoodFrames;
var hasMaxFrames    = this.goodFrames.length >= CONSTANTS.CAPTURE_TARGET;

if ((allAngles && hasEnoughFrames && CONSTANTS.AUTO_SUBMIT_ON_ALL_ANGLES) || hasMaxFrames) {
    this.performEnrollment();
}
```

**Step H: Add `onAngleUpdate` to the callbacks interface**

In the `Enrollment` constructor, in the callbacks object, add:
```javascript
onAngleUpdate: null,   // called with { bucket, prompt, icon }
```

### 6.2 CREATE `Scripts/enrollment-ui.js`

This is the unified UI controller. It replaces `admin-enroll.js` and the inline `<script>` in mobile views. It reads all configuration from `data-*` attributes on the `#enrollRoot` element so the same file works on every surface.

```javascript
/**
 * FaceAttend — Unified Enrollment UI Controller
 * enrollment-ui.js
 *
 * Replaces: admin-enroll.js, inline enrollment script in MobileRegistration/Enroll.cshtml
 *
 * Depends on: Scripts/modules/enrollment-core.js (must load first)
 *
 * Usage: Include in any page that has an #enrollRoot element with data attributes.
 * All configuration is read from data-* attributes — no hardcoded URLs or IDs.
 *
 * Required data attributes on #enrollRoot:
 *   data-employee-id    — employee ID string
 *   data-scan-url       — URL for ScanFrame endpoint
 *   data-enroll-url     — URL for Enroll endpoint
 *   data-redirect-url   — URL to redirect after success
 *
 * Optional data attributes:
 *   data-mode           — "admin" | "mobile" | "visitor" (default: "admin")
 *   data-min-frames     — minimum frames before manual submit (default: 3)
 *   data-liveness-th    — per-frame liveness threshold (default: 0.75)
 */
(function() {
    'use strict';

    // ── Bootstrap ──────────────────────────────────────────────────────────────

    var root = document.getElementById('enrollRoot');
    if (!root) return;
    if (typeof FaceAttend === 'undefined' || typeof FaceAttend.Enrollment === 'undefined') {
        console.error('[enrollment-ui] enrollment-core.js must be loaded first.');
        return;
    }

    var cfg = {
        empId:       (root.getAttribute('data-employee-id') || '').trim(),
        mode:        root.getAttribute('data-mode')         || 'admin',
        scanUrl:     root.getAttribute('data-scan-url')     || '/Biometrics/ScanFrame',
        enrollUrl:   root.getAttribute('data-enroll-url')   || '/Biometrics/Enroll',
        redirectUrl: root.getAttribute('data-redirect-url') || '/',
        minFrames:   parseInt(root.getAttribute('data-min-frames') || '3', 10),
        livenessTh:  parseFloat(root.getAttribute('data-liveness-th') || '0.75')
    };

    // ── DOM References ─────────────────────────────────────────────────────────

    function q(id) { return document.getElementById(id); }
    function qs(sel) { return root.querySelector(sel); }

    var ui = {
        video:        q('enrollVideo'),
        anglePrompt:  q('anglePrompt'),
        angleIcon:    q('angleIcon'),
        diversityDots: root.querySelectorAll('.enroll-diversity-dot'),
        progressText: q('enrollProgressText'),
        progressBar:  q('enrollProgressBar'),
        statusMsg:    q('enrollStatus'),
        livenessBar:  q('enrollLivenessBar'),
        livenessVal:  q('enrollLivenessVal'),
        startBtn:     q('enrollStartBtn'),
        confirmBtn:   q('enrollConfirmBtn'),
        retakeBtn:    q('enrollRetakeBtn'),
        processingOverlay: q('enrollProcessing'),
        processingStatus:  q('enrollProcessingStatus')
    };

    // ── Enrollment Instance ────────────────────────────────────────────────────

    var enrollment = FaceAttend.Enrollment.create({
        empId:            cfg.empId,
        perFrameThreshold: cfg.livenessTh,
        scanUrl:          cfg.scanUrl,
        enrollUrl:        cfg.enrollUrl,
        redirectUrl:      cfg.redirectUrl,
        minGoodFrames:    cfg.minFrames,
        maxKeepFrames:    8,
        enablePreview:    false
    });

    enrollment.elements.cam = ui.video;

    // ── Callbacks ──────────────────────────────────────────────────────────────

    enrollment.callbacks.onStatus = function(text, kind) {
        setStatus(text, kind);
    };

    enrollment.callbacks.onLivenessUpdate = function(pct, kind) {
        setLiveness(pct, kind);
    };

    enrollment.callbacks.onCaptureProgress = function(current, target) {
        updateProgress(current, target);
        updateDiversityDots();
    };

    enrollment.callbacks.onAngleUpdate = function(next) {
        if (next && next.bucket !== 'other') {
            showAngleGuidance(next);
        }
    };

    enrollment.callbacks.onEnrollmentComplete = function(count) {
        showProcessing(false);
        Swal.fire({
            icon: 'success',
            title: 'Enrollment Complete!',
            text: count + ' face samples saved successfully.',
            confirmButtonText: 'Done',
            background: isDark() ? '#0f172a' : '#fff',
            color:      isDark() ? '#f8fafc' : '#0f172a'
        }).then(function() {
            window.location.href = cfg.redirectUrl;
        });
    };

    enrollment.callbacks.onEnrollmentError = function(result) {
        showProcessing(false);
        var msg = enrollment.describeEnrollError(result);
        Swal.fire({
            icon:  'error',
            title: 'Enrollment Failed',
            html:  '<div style="font-size:.95rem">' + msg + '</div>',
            background: isDark() ? '#0f172a' : '#fff',
            color:      isDark() ? '#f8fafc' : '#0f172a'
        });
    };

    // ── UI Helpers ─────────────────────────────────────────────────────────────

    function isDark() {
        return document.documentElement.getAttribute('data-theme') === 'kiosk'
            || cfg.mode === 'mobile';
    }

    function setStatus(text, kind) {
        if (!ui.statusMsg) return;
        ui.statusMsg.textContent = text;
        ui.statusMsg.className = 'enroll-status enroll-status--' + (kind || 'info');
    }

    function setLiveness(pct, kind) {
        if (!ui.livenessBar) return;
        var safePct = Math.max(0, Math.min(100, pct || 0));
        ui.livenessBar.style.width = safePct + '%';
        ui.livenessBar.className = 'enroll-liveness-fill enroll-liveness-fill--' + (kind || 'info');
        if (ui.livenessVal) ui.livenessVal.textContent = safePct + '%';
    }

    function updateProgress(current, target) {
        if (ui.progressText) {
            ui.progressText.textContent = current + ' / ' + target + ' frames';
        }
        if (ui.progressBar) {
            ui.progressBar.style.width = Math.round((current / target) * 100) + '%';
        }
        // Show/hide confirm button
        if (ui.confirmBtn) {
            ui.confirmBtn.classList.toggle(
                'enroll-hidden', current < (cfg.minFrames || 3));
        }
    }

    function updateDiversityDots() {
        if (!ui.diversityDots || !ui.diversityDots.length) return;
        var captured = {};
        for (var i = 0; i < enrollment.goodFrames.length; i++) {
            var b = enrollment.goodFrames[i].poseBucket;
            if (b) captured[b] = true;
        }
        ui.diversityDots.forEach(function(dot) {
            var bucket = dot.getAttribute('data-bucket');
            dot.classList.toggle('enroll-diversity-dot--captured', !!captured[bucket]);
        });
    }

    function showAngleGuidance(next) {
        if (ui.anglePrompt) ui.anglePrompt.textContent = next.prompt;
        if (ui.angleIcon) {
            ui.angleIcon.className = 'enroll-angle-icon fa-solid ' + (next.icon || 'fa-circle-dot');
        }
    }

    function showProcessing(show, statusText) {
        if (!ui.processingOverlay) return;
        ui.processingOverlay.classList.toggle('enroll-hidden', !show);
        if (show && ui.processingStatus && statusText) {
            ui.processingStatus.textContent = statusText;
        }
    }

    // ── Event Handlers ─────────────────────────────────────────────────────────

    if (ui.startBtn) {
        ui.startBtn.addEventListener('click', function() {
            ui.startBtn.disabled = true;
            setStatus('Starting camera...', 'info');

            enrollment.startCamera(ui.video)
                .then(function() {
                    ui.startBtn.classList.add('enroll-hidden');
                    enrollment.startAutoEnrollment();
                    showAngleGuidance(enrollment.getNextAnglePrompt());
                    setStatus('Camera ready. Follow the angle prompts.', 'info');
                })
                .catch(function(e) {
                    ui.startBtn.disabled = false;
                    Swal.fire('Camera Error', e.message || 'Could not access camera.', 'error');
                });
        });
    }

    if (ui.confirmBtn) {
        ui.confirmBtn.addEventListener('click', function() {
            if (enrollment.goodFrames.length < cfg.minFrames) return;
            showProcessing(true, 'Processing enrollment...');
            enrollment.performEnrollment();
        });
    }

    if (ui.retakeBtn) {
        ui.retakeBtn.addEventListener('click', function() {
            enrollment.goodFrames = [];
            enrollment.passHist   = [];
            enrollment.enrolled   = false;
            enrollment.enrolling  = false;
            updateProgress(0, 8);
            updateDiversityDots();
            enrollment.startAutoEnrollment();
            showAngleGuidance(enrollment.getNextAnglePrompt());
            if (ui.confirmBtn) ui.confirmBtn.classList.add('enroll-hidden');
            setStatus('Retaking. Follow the angle prompts.', 'info');
        });
    }

    // ── Init ───────────────────────────────────────────────────────────────────

    (function init() {
        updateProgress(0, 8);
        setStatus('Click Start to begin enrollment.', 'info');
        if (ui.confirmBtn) ui.confirmBtn.classList.add('enroll-hidden');
        showAngleGuidance({ bucket: 'center', prompt: 'Start and look straight ahead', icon: 'fa-circle-dot' });
    })();

    // Cleanup on page unload
    window.addEventListener('beforeunload', function() {
        enrollment.stopCamera();
    });

})();
```

---

## 7. Phase 3 — Shared CSS

### 7.1 CREATE `Content/enrollment.css`

This is the single source of truth for all enrollment UI. It uses CSS custom properties from `_variables.css`. Every existing enrollment-specific CSS in `admin-enroll.css` that is NOT shared with non-enrollment admin pages should move here.

```css
/*
 * FaceAttend — Enrollment UI Styles
 * Content/enrollment.css
 *
 * Covers ALL enrollment surfaces: Admin (Employee/Visitor), Mobile self-enrollment.
 * Uses CSS variables from _variables.css.
 *
 * NEVER add enrollment-specific styles to admin.css, mobile-kiosk.css, or kiosk.css.
 * NEVER add <style> blocks to enrollment views.
 */

/* ── Layout ──────────────────────────────────────────────────────────────── */

.enroll-container {
    max-width: 640px;
    margin: 0 auto;
    padding: 0 1rem;
}

.enroll-hidden {
    display: none !important;
}

/* ── Camera ──────────────────────────────────────────────────────────────── */

.enroll-camera-wrap {
    position: relative;
    width: 100%;
    aspect-ratio: 4 / 3;
    border-radius: var(--radius-md, 12px);
    overflow: hidden;
    background: #000;
    border: 2px solid var(--border-light, #e2e8f0);
}

[data-theme="kiosk"] .enroll-camera-wrap,
.mobile-surface .enroll-camera-wrap {
    border-color: var(--border-subtle, rgba(255,255,255,0.1));
}

.enroll-video {
    width: 100%;
    height: 100%;
    object-fit: cover;
    transform: scaleX(-1); /* Mirror for selfie UX */
}

.enroll-face-overlay {
    position: absolute;
    inset: 0;
    pointer-events: none;
}

/* Face guide circle */
.enroll-face-guide {
    position: absolute;
    top: 50%;
    left: 50%;
    transform: translate(-50%, -50%);
    width: 55%;
    aspect-ratio: 1;
    border: 2px dashed rgba(255,255,255,0.3);
    border-radius: 50%;
    transition: border-color 0.3s ease, border-style 0.3s ease;
    pointer-events: none;
}

.enroll-face-guide--good {
    border-color: var(--success-500, #22c55e);
    border-style: solid;
}

.enroll-face-guide--warning {
    border-color: var(--warning-500, #f59e0b);
    border-style: dashed;
}

.enroll-face-guide--error {
    border-color: var(--danger-500, #ef4444);
    border-style: solid;
}

/* ── Angle Guidance ──────────────────────────────────────────────────────── */

.enroll-angle-guidance {
    display: flex;
    align-items: center;
    gap: 12px;
    padding: 12px 16px;
    background: var(--surface-secondary, #f8fafc);
    border: 1px solid var(--border-light, #e2e8f0);
    border-radius: var(--radius-sm, 8px);
    margin-top: 12px;
}

[data-theme="kiosk"] .enroll-angle-guidance,
.mobile-surface .enroll-angle-guidance {
    background: rgba(255,255,255,0.05);
    border-color: rgba(255,255,255,0.1);
}

.enroll-angle-icon {
    font-size: 20px;
    color: var(--primary-500, #3b82f6);
    width: 28px;
    text-align: center;
    flex-shrink: 0;
}

.enroll-angle-prompt {
    font-size: 0.9375rem;
    font-weight: 500;
    color: var(--text-primary, #0f172a);
}

[data-theme="kiosk"] .enroll-angle-prompt,
.mobile-surface .enroll-angle-prompt {
    color: var(--text-primary, #f8fafc);
}

/* ── Diversity Dots ──────────────────────────────────────────────────────── */

.enroll-diversity {
    display: flex;
    align-items: center;
    gap: 10px;
    flex-wrap: wrap;
    margin-top: 10px;
}

.enroll-diversity-label {
    font-size: 0.8125rem;
    color: var(--text-secondary, #475569);
    white-space: nowrap;
}

.enroll-diversity-dots {
    display: flex;
    gap: 6px;
}

.enroll-diversity-dot {
    width: 10px;
    height: 10px;
    border-radius: 50%;
    background: var(--gray-200, #e2e8f0);
    border: 1.5px solid var(--gray-300, #cbd5e1);
    transition: background 0.25s ease, border-color 0.25s ease, transform 0.15s ease;
    position: relative;
}

.enroll-diversity-dot[title]::after {
    content: attr(title);
    position: absolute;
    bottom: 14px;
    left: 50%;
    transform: translateX(-50%);
    font-size: 10px;
    background: rgba(0,0,0,0.7);
    color: #fff;
    padding: 2px 5px;
    border-radius: 3px;
    white-space: nowrap;
    opacity: 0;
    pointer-events: none;
    transition: opacity 0.15s;
}

.enroll-diversity-dot:hover::after {
    opacity: 1;
}

.enroll-diversity-dot--captured {
    background: var(--success-500, #22c55e);
    border-color: var(--success-600, #16a34a);
    transform: scale(1.2);
}

/* ── Progress ────────────────────────────────────────────────────────────── */

.enroll-progress-wrap {
    margin-top: 12px;
}

.enroll-progress-label {
    display: flex;
    justify-content: space-between;
    font-size: 0.8125rem;
    color: var(--text-secondary, #475569);
    margin-bottom: 4px;
}

.enroll-progress-track {
    height: 6px;
    background: var(--gray-200, #e2e8f0);
    border-radius: 3px;
    overflow: hidden;
}

[data-theme="kiosk"] .enroll-progress-track,
.mobile-surface .enroll-progress-track {
    background: rgba(255,255,255,0.1);
}

.enroll-progress-fill {
    height: 100%;
    background: var(--primary-500, #3b82f6);
    border-radius: 3px;
    transition: width 0.3s ease;
    width: 0%;
}

/* ── Liveness Bar ────────────────────────────────────────────────────────── */

.enroll-liveness-wrap {
    display: flex;
    align-items: center;
    gap: 10px;
    margin-top: 10px;
}

.enroll-liveness-label {
    font-size: 0.8125rem;
    color: var(--text-secondary, #475569);
    white-space: nowrap;
    min-width: 60px;
}

.enroll-liveness-track {
    flex: 1;
    height: 6px;
    background: var(--gray-200, #e2e8f0);
    border-radius: 3px;
    overflow: hidden;
}

.enroll-liveness-fill {
    height: 100%;
    border-radius: 3px;
    transition: width 0.2s ease, background 0.2s ease;
    width: 0%;
    background: var(--gray-400, #94a3b8);
}

.enroll-liveness-fill--pass { background: var(--success-500, #22c55e); }
.enroll-liveness-fill--warn { background: var(--warning-500, #f59e0b); }
.enroll-liveness-fill--fail { background: var(--danger-500, #ef4444); }

.enroll-liveness-val {
    font-size: 0.8125rem;
    font-weight: 600;
    min-width: 36px;
    text-align: right;
    color: var(--text-primary, #0f172a);
}

/* ── Status Message ──────────────────────────────────────────────────────── */

.enroll-status {
    padding: 10px 14px;
    border-radius: var(--radius-sm, 8px);
    font-size: 0.9rem;
    margin-top: 12px;
    border: 1px solid transparent;
}

.enroll-status--info    { background: var(--info-50, #f0f9ff);    border-color: #bae6fd; color: #0c4a6e; }
.enroll-status--success { background: var(--success-50, #f0fdf4); border-color: #bbf7d0; color: #14532d; }
.enroll-status--warning { background: var(--warning-50, #fffbeb); border-color: #fde68a; color: #78350f; }
.enroll-status--danger  { background: var(--danger-50, #fef2f2);  border-color: #fecaca; color: #7f1d1d; }

[data-theme="kiosk"] .enroll-status--info,
.mobile-surface .enroll-status--info {
    background: rgba(59,130,246,0.1);
    border-color: rgba(59,130,246,0.2);
    color: #93c5fd;
}

[data-theme="kiosk"] .enroll-status--success,
.mobile-surface .enroll-status--success {
    background: rgba(34,197,94,0.1);
    border-color: rgba(34,197,94,0.2);
    color: #86efac;
}

[data-theme="kiosk"] .enroll-status--warning,
.mobile-surface .enroll-status--warning {
    background: rgba(245,158,11,0.1);
    border-color: rgba(245,158,11,0.2);
    color: #fcd34d;
}

[data-theme="kiosk"] .enroll-status--danger,
.mobile-surface .enroll-status--danger {
    background: rgba(239,68,68,0.1);
    border-color: rgba(239,68,68,0.2);
    color: #fca5a5;
}

/* ── Controls ────────────────────────────────────────────────────────────── */

.enroll-controls {
    display: flex;
    gap: 10px;
    flex-wrap: wrap;
    margin-top: 16px;
}

.enroll-controls .btn {
    flex: 1;
    min-width: 120px;
}

/* ── Processing Overlay ──────────────────────────────────────────────────── */

.enroll-processing-overlay {
    position: absolute;
    inset: 0;
    background: rgba(0,0,0,0.65);
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    border-radius: inherit;
    gap: 12px;
    z-index: 10;
}

.enroll-processing-overlay.enroll-hidden {
    display: none !important;
}

.enroll-processing-spinner {
    font-size: 2rem;
    color: #fff;
    animation: fa-spin 0.8s linear infinite;
}

.enroll-processing-text {
    color: #fff;
    font-size: 0.9375rem;
    font-weight: 500;
}

/* ── Wizard Step Pills ───────────────────────────────────────────────────── */

.enroll-steps {
    display: flex;
    align-items: center;
    gap: 8px;
    margin-bottom: 20px;
}

.enroll-step {
    flex: 1;
    text-align: center;
    padding: 8px 6px;
    border-radius: 10px;
    border: 1px solid var(--border-light, #e2e8f0);
    background: var(--surface-secondary, #f8fafc);
    color: var(--text-secondary, #475569);
    font-size: 0.8125rem;
    font-weight: 600;
    transition: background 0.2s, border-color 0.2s, color 0.2s;
}

[data-theme="kiosk"] .enroll-step,
.mobile-surface .enroll-step {
    border-color: rgba(255,255,255,0.12);
    background: rgba(255,255,255,0.04);
    color: var(--text-muted, #64748b);
}

.enroll-step.active {
    background: rgba(59,130,246,0.12);
    border-color: rgba(59,130,246,0.4);
    color: var(--primary-600, #2563eb);
}

[data-theme="kiosk"] .enroll-step.active,
.mobile-surface .enroll-step.active {
    color: #93c5fd;
}

.enroll-step.done {
    background: rgba(34,197,94,0.1);
    border-color: rgba(34,197,94,0.35);
    color: var(--success-600, #16a34a);
}

[data-theme="kiosk"] .enroll-step.done,
.mobile-surface .enroll-step.done {
    color: #86efac;
}
```

### 7.2 MODIFY `Content/admin-enroll.css`

After creating `enrollment.css`, remove all blocks from `admin-enroll.css` that are now covered by `enrollment.css`. Keep only styles that are specific to the admin layout (e.g., sidebar layout, admin-only card wrappers). If `admin-enroll.css` is entirely replaced by `enrollment.css`, delete it and update `BundleConfig.cs` accordingly.

### 7.3 MODIFY `Content/mobile-kiosk.css`

Remove `.camera-container video`, `.camera-overlay`, face guide, and liveness indicator styles that now live in `enrollment.css`. Keep all kiosk attendance UI styles and mobile navigation styles — those are NOT duplicated.

---

## 8. Phase 4 — Shared View Component

### 8.1 CREATE `Views/Shared/_EnrollmentComponent.cshtml`

This partial renders the complete enrollment UI. It is included by all three enrollment views. All configuration is passed via the model/ViewBag so the partial has no hardcoded URLs.

```razor
@* Views/Shared/_EnrollmentComponent.cshtml
 * Shared enrollment UI component.
 * Called from: Admin/Employees/Enroll, Admin/Visitors/Enroll, MobileRegistration/Enroll (step 2)
 *
 * Required ViewBag:
 *   ViewBag.EmployeeId     string  — employee/visitor ID
 *   ViewBag.ScanUrl        string  — URL for ScanFrame AJAX endpoint
 *   ViewBag.EnrollUrl      string  — URL for Enroll AJAX endpoint
 *   ViewBag.RedirectUrl    string  — URL after enrollment success
 *   ViewBag.Mode           string  — "admin" | "mobile" | "visitor"
 *   ViewBag.MinFrames      int     — minimum good frames required (default 3)
 *   ViewBag.LivenessThreshold double — per-frame liveness threshold (default 0.75)
 *@

@{
    var mode         = (string)(ViewBag.Mode ?? "admin");
    var isMobile     = (mode == "mobile");
    var minFrames    = (int)(ViewBag.MinFrames ?? 3);
    var liveTh       = (double)(ViewBag.LivenessThreshold ?? 0.75);
    var surfaceClass = isMobile ? "mobile-surface" : "admin-surface";
}

<div id="enrollRoot"
     class="enroll-container @surfaceClass"
     data-employee-id="@ViewBag.EmployeeId"
     data-mode="@mode"
     data-scan-url="@ViewBag.ScanUrl"
     data-enroll-url="@ViewBag.EnrollUrl"
     data-redirect-url="@ViewBag.RedirectUrl"
     data-min-frames="@minFrames"
     data-liveness-th="@liveTh.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)">

    @* ── Camera wrap ─────────────────────────────────────────────────────── *@
    <div class="enroll-camera-wrap" id="enrollCameraWrap">
        <video id="enrollVideo" autoplay playsinline muted></video>

        @* Face guide overlay *@
        <div class="enroll-face-guide" id="enrollFaceGuide"></div>

        @* Processing overlay (shown during server submit) *@
        <div class="enroll-processing-overlay enroll-hidden" id="enrollProcessing">
            <i class="fa-solid fa-circle-notch enroll-processing-spinner"></i>
            <span class="enroll-processing-text" id="enrollProcessingStatus">Processing...</span>
        </div>
    </div>

    @* ── Angle guidance ──────────────────────────────────────────────────── *@
    <div class="enroll-angle-guidance" id="enrollAngleGuidance">
        <i class="enroll-angle-icon fa-solid fa-circle-dot" id="angleIcon"></i>
        <span class="enroll-angle-prompt" id="anglePrompt">Click Start to begin</span>
    </div>

    @* ── Diversity dots ──────────────────────────────────────────────────── *@
    <div class="enroll-diversity">
        <span class="enroll-diversity-label">Angles captured:</span>
        <div class="enroll-diversity-dots">
            <div class="enroll-diversity-dot" data-bucket="center" title="Center"></div>
            <div class="enroll-diversity-dot" data-bucket="left"   title="Left"></div>
            <div class="enroll-diversity-dot" data-bucket="right"  title="Right"></div>
            <div class="enroll-diversity-dot" data-bucket="up"     title="Up"></div>
            <div class="enroll-diversity-dot" data-bucket="down"   title="Down"></div>
        </div>
    </div>

    @* ── Progress ────────────────────────────────────────────────────────── *@
    <div class="enroll-progress-wrap">
        <div class="enroll-progress-label">
            <span>Frames captured</span>
            <span id="enrollProgressText">0 / 8</span>
        </div>
        <div class="enroll-progress-track">
            <div class="enroll-progress-fill" id="enrollProgressBar"></div>
        </div>
    </div>

    @* ── Liveness bar ────────────────────────────────────────────────────── *@
    <div class="enroll-liveness-wrap">
        <span class="enroll-liveness-label">Liveness</span>
        <div class="enroll-liveness-track">
            <div class="enroll-liveness-fill" id="enrollLivenessBar"></div>
        </div>
        <span class="enroll-liveness-val" id="enrollLivenessVal">0%</span>
    </div>

    @* ── Status message ──────────────────────────────────────────────────── *@
    <div class="enroll-status enroll-status--info" id="enrollStatus">Ready</div>

    @* ── Controls ────────────────────────────────────────────────────────── *@
    <div class="enroll-controls">
        <button class="btn btn-primary" id="enrollStartBtn" type="button">
            <i class="fa-solid fa-camera"></i> Start Enrollment
        </button>
        <button class="btn btn-success enroll-hidden" id="enrollConfirmBtn" type="button">
            <i class="fa-solid fa-check"></i> Save Enrollment
        </button>
        <button class="btn btn-secondary enroll-hidden" id="enrollRetakeBtn" type="button">
            <i class="fa-solid fa-rotate-left"></i> Retake
        </button>
    </div>

</div>
```

---

## 9. Phase 5 — Update Existing Views

### 9.1 MODIFY `Areas/Admin/Views/Employees/Enroll.cshtml`

Remove all inline `<style>` enrollment CSS. Remove the inline enrollment `<script>` block. Replace the camera/capture section with the shared partial. Keep the page title, breadcrumbs, admin layout wrapper, and any employee-info display cards.

The view should set ViewBag properties and render the partial:

```razor
@{
    // Set ViewBag for partial
    ViewBag.ScanUrl           = Url.Action("ScanFrame", "Biometrics");
    ViewBag.EnrollUrl         = Url.Action("Enroll", "Biometrics");
    ViewBag.RedirectUrl       = Url.Action("Index", "Employees", new { area = "Admin" });
    ViewBag.Mode              = "admin";
    ViewBag.MinFrames         = 3;
    ViewBag.LivenessThreshold = ConfigurationService.GetDouble("Biometrics:LivenessThreshold", 0.75);
    // ViewBag.EmployeeId is already set by the controller
}
```

Then in the body where the enrollment UI was:
```razor
@Html.Partial("~/Views/Shared/_EnrollmentComponent.cshtml")
```

Scripts section:
```razor
@section scripts {
    @Scripts.Render("~/bundles/enrollment")
}
```

Styles section:
```razor
@section styles {
    @Styles.Render("~/Content/enrollment")
}
```

### 9.2 MODIFY `Areas/Admin/Views/Visitors/Enroll.cshtml`

Same pattern as 9.1. Set ViewBag:
```razor
ViewBag.ScanUrl           = Url.Action("ScanFrame", "Biometrics");   // Admin biometrics
ViewBag.EnrollUrl         = Url.Action("EnrollFace", "Visitors", new { area = "Admin", id = Model.Id });
ViewBag.RedirectUrl       = Url.Action("Index", "Visitors", new { area = "Admin" });
ViewBag.Mode              = "visitor";
ViewBag.MinFrames         = 3;
ViewBag.LivenessThreshold = ConfigurationService.GetDouble("Biometrics:LivenessThreshold", 0.75);
ViewBag.EmployeeId        = Model.Id.ToString();
```

Remove the existing `#livePane`, `#uploadPane`, wizard HTML, and replace with `@Html.Partial("~/Views/Shared/_EnrollmentComponent.cshtml")`.

**Note:** The visitor enrollment view also has an upload path. Keep the upload section as a separate tab/pane — it uses `enrollment.enrollFromFiles()` from `enrollment-core.js` and does not go through the new angle guidance flow. Only the live-camera pane is replaced by the partial.

### 9.3 MODIFY `Views/MobileRegistration/Enroll.cshtml`

This view has a 3-step wizard: 1=Details, 2=Capture, 3=Review.

- Keep Step 1 (Details form) and Step 3 (Review) exactly as they are — do not change form validation logic.
- Replace Step 2 (`#step2` pane inner content) with `@Html.Partial("~/Views/Shared/_EnrollmentComponent.cshtml")`.
- Remove all inline `<style>` that duplicate `enrollment.css`. Keep only layout styles specific to the mobile wizard (step pills, form fields, review pane) if they are not already in `mobile-kiosk.css`.
- Remove the inline `<script>` camera capture logic (600+ lines) and replace with `@Scripts.Render("~/bundles/enrollment")`.
- Keep the step transition logic (`setStep`, field validation, `submitEnrollment` function) — these are specific to the mobile wizard multi-step flow.

The mobile enrollment's `submitEnrollment` function must pass the collected frames from `FaceAttend.Enrollment` instance (accessible via a module-level reference) rather than its own `capturedFrames` array:

```javascript
// In the remaining mobile wizard script (step nav only):
// Get reference to the enrollment instance created by enrollment-ui.js
function getEnrollmentInstance() {
    // enrollment-ui.js exposes the instance on window.FaceAttendEnrollment
    return window.FaceAttendEnrollment;
}

// When moving from Step 2 to Step 3:
els.btnToReview.addEventListener('click', function() {
    var inst = getEnrollmentInstance();
    if (!inst || inst.goodFrames.length < 3) {
        MobileSwal.warning('Not enough frames', 'Please capture at least 3 face samples.');
        return;
    }
    setStep(3);
});
```

**Add to `enrollment-ui.js`** — expose instance on window for inter-script communication:
```javascript
// At the end of enrollment-ui.js, after the enrollment instance is created:
window.FaceAttendEnrollment = enrollment;
```

---

## 10. Phase 6 — Controller Updates

### 10.1 `Areas/Admin/Controllers/BiometricsController.cs` — Already covered in Phase 1.6

Verify these items after making changes:
- `EnrollCandidate` private class is removed (now in separate file)
- `FindDuplicateEmployeeInDatabase` private method is removed (now `DuplicateCheckHelper.FindDuplicate`)
- `SelectDiverseFrames` static method is added
- `Enroll` action uses the new flow
- `FastFaceMatcher.UpdateEmployee` is called with the list of vectors, not just one

### 10.2 No changes needed to `KioskController.cs`

The kiosk attendance pipeline is intentionally untouched. Better enrollment vectors will automatically improve kiosk recognition accuracy without any controller changes.

---

## 11. Phase 7 — BundleConfig & Web.config

### 11.1 MODIFY `App_Start/BundleConfig.cs`

**Add enrollment script bundle:**
```csharp
// Enrollment Scripts (shared across admin and mobile)
// Both files are ES5 compatible (no const/let/arrow functions)
bundles.Add(new ScriptBundle("~/bundles/enrollment")
    .Include("~/Scripts/modules/enrollment-core.js")
    .Include("~/Scripts/enrollment-ui.js"));
```

**Add enrollment CSS bundle:**
```csharp
// Enrollment CSS
// Uses NonMinifiedStyleBundle: contains CSS custom properties (--enroll-*)
// that WebGrease CSS minifier cannot handle
bundles.Add(new NonMinifiedStyleBundle("~/Content/enrollment")
    .Include("~/Content/enrollment.css"));
```

**Remove or update the existing `adminEnroll` bundle:**
```csharp
// BEFORE (remove this):
bundles.Add(new ScriptBundle("~/bundles/adminEnroll")
    .Include("~/Scripts/modules/enrollment-core.js")
    .Include("~/Scripts/admin-enroll.js"));

// AFTER (enrollment bundle above replaces this entirely)
// Delete the adminEnroll bundle registration
```

**Update admin-enroll CSS bundle:**
```csharp
// If admin-enroll.css still exists with admin-layout-only styles:
bundles.Add(new NonMinifiedStyleBundle("~/Content/admin-enroll")
    .Include("~/Content/admin-enroll.css"));
// If admin-enroll.css is fully replaced by enrollment.css:
// Delete this bundle registration
```

### 11.2 MODIFY `Web.config`

Add the following keys to `<appSettings>`:

```xml
<!-- Enrollment Quality Scoring -->
<add key="Biometrics:Enroll:CaptureTarget"     value="8"    />
<add key="Biometrics:Enroll:MinQualityFrames"   value="3"    />
<add key="Biometrics:Enroll:MaxStoredVectors"   value="5"    />
<add key="Biometrics:Enroll:LivenessThreshold"  value="0.75" />
<add key="Biometrics:Enroll:Parallelism"        value="4"    />

<!-- Sharpness thresholds — lower for mobile cameras -->
<add key="Biometrics:Enroll:SharpnessThreshold"         value="80"  />
<add key="Biometrics:Enroll:SharpnessThreshold:Mobile"  value="50"  />

<!-- Quality score weights — must conceptually sum to 1.0 -->
<add key="Biometrics:Enroll:Quality:LivenessWeight"  value="0.40" />
<add key="Biometrics:Enroll:Quality:SharpnessWeight" value="0.30" />
<add key="Biometrics:Enroll:Quality:AreaWeight"      value="0.20" />
<add key="Biometrics:Enroll:Quality:PoseWeight"      value="0.10" />
```

---

## 12. Phase 8 — Delete Dead Files

After all views are updated and tested, delete:

```
Scripts/admin-enroll.js
Scripts/enroll-wizard.js
```

Before deleting, verify:
1. `grep -r "admin-enroll" Views/` returns zero results
2. `grep -r "enroll-wizard" Views/` returns zero results
3. `grep -r "admin-enroll" App_Start/BundleConfig.cs` returns zero results
4. All enrollment tests pass

---

## 13. Testing Checklist

### Server-Side

- [ ] `FaceQualityAnalyzer.CalculateSharpness` returns 0 for a solid color image (no edges → near-zero variance)
- [ ] `FaceQualityAnalyzer.CalculateSharpness` returns > 100 for a sharp face photo
- [ ] `FaceQualityAnalyzer.GetPoseBucket` returns exactly `"center"` for a front-facing image
- [ ] `FaceQualityAnalyzer.GetPoseBucket` returns `"other"` for a 45° profile image
- [ ] `SelectDiverseFrames` picks one frame per bucket when candidates are available from all 5 buckets
- [ ] `SelectDiverseFrames` fills remaining slots from highest quality when < 5 buckets represented
- [ ] `BiometricsController.Enroll` rejects a second enrollment with `FACE_ALREADY_ENROLLED`
- [ ] `BiometricsController.Enroll` saves exactly 5 vectors to `FaceEncodingsJson`
- [ ] `FastFaceMatcher.UpdateEmployee` is called after successful enrollment (new employee recognized at kiosk immediately)
- [ ] `DuplicateCheckHelper.FindDuplicate` correctly identifies the same face under different employee IDs
- [ ] `ScanFramePipeline.Run` returns `sharpness` field in the response object
- [ ] `FastScanPipeline.ScanInMemory` returns `Sharpness` in `ScanResult`

### Client-Side JavaScript

- [ ] `enrollment-core.js` `calculateSharpness` returns near-zero for a blank canvas
- [ ] `enrollment-core.js` `estimatePoseBucket` returns `"center"` for a centered faceBox
- [ ] `enrollment-core.js` `getNextAnglePrompt` cycles through all 5 buckets in order
- [ ] `enrollment-ui.js` loads without errors when `#enrollRoot` is absent (early return)
- [ ] `enrollment-ui.js` initializes when `#enrollRoot` is present
- [ ] Diversity dots update when frames are captured
- [ ] Angle prompt updates after each captured frame
- [ ] Auto-submit fires when all 5 angles are captured (if `AUTO_SUBMIT_ON_ALL_ANGLES = true`)
- [ ] Auto-submit fires when 8 frames are captured
- [ ] Retake button resets all state correctly
- [ ] `window.FaceAttendEnrollment` is set (for mobile wizard step nav)
- [ ] Camera stops on `beforeunload`

### CSS

- [ ] `enrollment.css` styles apply on Admin Employee Enroll page
- [ ] `enrollment.css` styles apply on Admin Visitor Enroll page
- [ ] `enrollment.css` styles apply on Mobile Registration Enroll page (Step 2)
- [ ] No inline `<style>` blocks remain in any enrollment view
- [ ] Dark mode classes (`[data-theme="kiosk"]`) render correctly on mobile enrollment
- [ ] Diversity dots animate (scale up) when a bucket is captured
- [ ] Processing overlay shows during server submit, hides on completion

### Integration

- [ ] Admin can enroll a new employee: camera opens, angle guidance shows, 8 frames captured, success redirect
- [ ] Admin can enroll same employee again: `FACE_ALREADY_ENROLLED` error shown
- [ ] Mobile user can self-enroll: 3-step wizard, step 2 uses shared partial, enrollment submits
- [ ] After mobile enrollment, the same employee can scan at the kiosk immediately (cache updated)
- [ ] Existing enrolled employees (enrolled before this change) still match at kiosk (backward compatible)
- [ ] Kiosk attendance flow is completely unchanged (no regression)
- [ ] Visitor enrollment still works (upload path preserved)

---

## 14. Design Decisions Reference

### Why 640×480 instead of 480×360?

Dlib's ResNet face encoding model takes a 150×150 crop of the detected face region. At 480×360 with a face filling 50% of the frame, the face region is ~240×180 — still enough for dlib, but near the minimum. At 640×480, the same face occupies ~320×240, giving dlib more pixel data and reducing encoding error. The upload size increases from ~22KB to ~38KB per frame — acceptable for a one-time enrollment process.

### Why 0.80 JPEG quality instead of 0.65?

Enrollment is a one-time operation. The frames stored in the database as reference vectors must be high quality. During kiosk attendance, 0.90 quality is used for the same reason. 0.65 is appropriate for preview/scan-frame calls where we just need to check liveness, not encode for permanent storage.

### Why not use 0.90 quality for enrollment uploads?

At 0.90 quality, a 640×480 frame is ~55–65KB. With 8 frames in one multipart POST, that's ~450KB. At 0.80, it's ~38KB per frame = ~300KB total. The encoding quality difference between 0.80 and 0.90 is imperceptible to dlib (JPEG artifacts only matter below 0.65 for 128-dim encoding). 0.80 is the right balance.

### Why is liveness weight 0.40 (highest)?

A perfectly sharp, large, centered face that fails liveness is useless — it's a photo attack. Liveness must dominate the quality score so that even a slightly blurry live face beats a sharp printed photo.

### Why normalize sharpness to `/300f` not `/500f`?

The plan document uses `/500f`. After testing on typical webcam footage at 640×480, a sharp face frame produces Laplacian variance of 100–400. Using `/500f` means good frames score 0.2–0.8. Using `/300f` means good frames score 0.33–1.0. The `/300f` normalization gives better differentiation in the common range.

### Why 300ms auto-interval instead of 250ms?

250ms gave insufficient time for the user to physically move their head between captures. At 8 frames over ~2.4 seconds of active capture (300ms × 8), users have time to cover 3–4 different angles naturally when prompted. At 250ms, frames cluster on the same angle.

### Why is the sharpness check done client-side AND server-side?

Client-side is a fast guard that prevents uploading blurry frames (saves bandwidth and server load — roughly 40% of frames in typical webcam conditions are below threshold). Server-side is the authoritative check that applies the composite quality score. The client threshold is more lenient (50/80) than the server uses in scoring — the server picks the best of what passes the client filter.

### Why expose `window.FaceAttendEnrollment`?

The mobile enrollment wizard's step navigation (setStep, btnToReview) lives in view-level script that must know the capture state without coupling to the full enrollment-ui.js internals. A single window reference is simpler than an event bus or pub/sub for this case. It is set after the instance is fully initialized and cleared on page unload.

---

*End of implementation plan. Total files: 6 created, 11 modified, 2 deleted.*
