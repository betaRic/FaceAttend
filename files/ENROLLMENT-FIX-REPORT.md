# Enrollment UI — Deep Dive Fix Report
## All 4 Screenshot Issues Traced to Root Cause and Fixed

---

## ISSUE 1 — Only 1 method card visible (Image 1)

**Root cause:** `fa-method-grid` uses `grid-template-columns: repeat(auto-fit, minmax(220px, 1fr))`. When the host `.card-body` padding reduces available width below 440px, `auto-fit` can only fit ONE column. Both cards exist in the HTML — they're just stacking vertically off-screen.

**Fix:** `enrollment-additions.css` adds:
```css
.card-body .fa-method-grid,
#enrollContainer .fa-method-grid {
    grid-template-columns: repeat(2, 1fr);
}
```
This forces 2 columns always. On very small screens (< 480px) it falls back to 1 column.

---

## ISSUE 2 — Camera black, tiny, at bottom. "Start Enrollment" button showing. (Image 2)

**Root cause — 3 interconnected bugs:**

### Bug 2A: Wrong video element ID (THE PRIMARY CAUSE)

`_Camera.cshtml` generates the video ID as `Model.Id + "Video"`.
`_EnrollmentComponent.cshtml` was building the CameraViewModel like this:

```csharp
var cameraModel = new CameraViewModel
{
    VideoId = "enrollVideo",  // ← sets VideoId property
    // Id NOT SET  ← this is the bug
    ShowGuide = true,
    ...
};
```

In `_Camera.cshtml`:
```csharp
var containerId = Model.Id;           // = null (not set)
var videoId     = Model.Id + "Video"; // = null + "Video" = "Video"
```

Result: video element rendered as `<video id="Video">` (NOT `<video id="enrollVideo">`).

Every call to `document.getElementById('enrollVideo')` returned `null`:
- The inline script in the old `_EnrollmentComponent.cshtml`: `els.video = null`  
- `enrollment-ui.js`: `ui.video = null`

Camera started but had no video element to attach to → black screen.

**Fix:** Set `Id = "enroll"` in CameraViewModel:
```csharp
var cameraModel = new CameraViewModel
{
    Id      = "enroll",         // ← NOW generates: containerId="enroll", videoId="enrollVideo"
    VideoId = "enrollVideo",    // explicit, matches enrollment-ui.js lookup
    ...
};
```

### Bug 2B: Camera component not filling the container

`.camera` div (from `_Camera.cshtml`) had no CSS rules making it fill `.enroll-camera-wrap`.

`.enroll-camera-wrap` has `aspect-ratio: 4/3; position: relative; overflow: hidden;`
But `.camera` inside it had no `position: absolute; inset: 0` — so it was 0 height or auto height, collapsing at the bottom of the page.

**Fix:** `enrollment-additions.css` adds:
```css
.enroll-camera-wrap .camera {
    position: absolute;
    inset: 0;
    width: 100%;
    height: 100%;
}
.enroll-camera-wrap .camera__video,
.enroll-camera-wrap video {
    width: 100%;
    height: 100%;
    object-fit: cover;
    transform: scaleX(-1);
}
```

### Bug 2C: enrollment-ui.js not loaded → no auto-start behavior

`Enroll.cshtml` @section scripts only loaded:
```razor
@Scripts.Render("~/bundles/facescan-core")
@Scripts.Render("~/bundles/facescan-ui")
```

`enrollment-ui.js` (which handles auto-start, auto-save, SweetAlert) was **never in any bundle for Enroll.cshtml**.

The old inline `<script>` inside `_EnrollmentComponent.cshtml` was the only enrollment controller running, and:
- It never hid the Start button
- It had the bug where `els.video = null` (from Bug 2A)
- It required a manual button click to start

**Fix:** 
1. Remove inline `<script>` from `_EnrollmentComponent.cshtml`
2. Add `@Scripts.Render("~/bundles/enrollment")` to `Enroll.cshtml`
3. `enrollment-ui.js` now handles everything:
   - Hides Start button immediately on load (`style="display:none"`)
   - Calls `startCamera()` when `showLive()` triggers `window.FaceAttendEnrollment.start()`
   - Auto-pauses when `minFrames` reached
   - Shows SweetAlert: "X samples captured — Save now?"
   - Resumes on cancel, saves on confirm

---

## ISSUE 3 — Red bar below upload dropzone (Images 3 & 4)

**Root cause:** `fa-uploader__error` div was rendering as a visible block element.

The `display: none` CSS rule in `uploader.css` was not applying because `uploader.css` was never in any bundle (our previous fix added it to `~/Content/fa-components`). Without the CSS, the div gets default block rendering.

ADDITIONALLY: the `_FileUploader.cshtml` had `data-on-selected="onUploadFilesSelected"` on the container. The Uploader JS was reading this attribute and attempting a second initialization from it, which triggered a validation check with 0 files, which added `is-visible` to the error div.

**Fix 1:** `_FileUploader.cshtml` now has inline `style="display:none"` on the error div:
```html
<div class="fa-uploader__error" id="@(Model.Id)_error" style="display:none;" ...></div>
```
This is a hard guarantee regardless of CSS bundle state.

**Fix 2:** `_FileUploader.cshtml` removes `data-on-selected` attribute — only explicit `FaceAttend.UI.Uploader.init()` initialization is allowed, no attribute-based double-init.

**Fix 3:** `enrollment-additions.css` adds:
```css
.fa-uploader__error {
    display: none !important;
}
.fa-uploader__error.is-visible {
    display: block !important;
    /* proper error styling */
}
```

---

## ISSUE 4 — File "5.png" shows without chip styling (Image 4)

**Root cause:** `uploader.css` was not in any bundle, so `.fa-uploader__chip` had no styling. The file chip rendered as a bare `<div>` with an icon and text but no background, border, or padding.

**Fix:** `uploader.css` is now in `~/Content/fa-components` bundle (from our previous fix set). Once the bundle is loaded, chips get their full styling:
```css
.fa-uploader__chip {
    display: inline-flex;
    align-items: center;
    gap: var(--space-2);
    background: var(--bg-elevated);
    border: 1px solid var(--border-default);
    border-radius: var(--radius-lg);
    padding: var(--space-2) var(--space-3);
    font-size: var(--text-sm);
    color: var(--text-secondary);
}
```

---

## FILES IN THIS OUTPUT PACKAGE

| File | Action | Fixes |
|---|---|---|
| `Views/Shared/_EnrollmentComponent.cshtml` | REPLACE | Bug 2A (Id="enroll"), Bug 2C (remove inline script) |
| `Scripts/enrollment-ui.js` | REPLACE | Bug 2C (auto-start, auto-save, SweetAlert) |
| `Views/Shared/Components/_FileUploader.cshtml` | REPLACE | Issue 3 (inline display:none, remove data-on-selected) |
| `Content/enrollment-additions.css` | APPEND to enrollment.css | Issue 1 (method grid), Bug 2B (camera sizing), Issue 3 (error display) |
| `Views/Enroll-scripts-patch.txt` | FOLLOW instructions | Bug 2C (add enrollment bundle) |

---

## EXECUTION ORDER

```
Step 1. Replace _EnrollmentComponent.cshtml
        (fixes camera ID mismatch — the root cause of everything in Image 2)

Step 2. Append enrollment-additions.css to Content/enrollment.css
        (fixes camera sizing, method grid, error div)

Step 3. Replace enrollment-ui.js
        (fixes auto-start + auto-save + SweetAlert flow)

Step 4. Replace _FileUploader.cshtml
        (fixes red bar, double-init)

Step 5. Add @Scripts.Render("~/bundles/enrollment") to:
        - Areas/Admin/Views/Employees/Enroll.cshtml @section scripts
        - Areas/Admin/Views/Visitors/Enroll.cshtml @section scripts
        (before ~/bundles/facescan-ui)

Step 6. Verify ~/Content/fa-components bundle is in BundleConfig.cs
        and loaded in _AdminLayout.cshtml
        (required for chip styling + dropzone styles)
```

---

## EXPECTED RESULT AFTER ALL FIXES

**Method selection page:**
- Both "Live Camera" and "Upload Photos" cards visible side by side
- Cards have proper borders, icon boxes, hover effect, "Recommended" badge

**Live Camera pane:**
- Camera starts AUTOMATICALLY when "Live Camera" is selected (no Start button)
- Video fills the camera container, properly mirrored
- Face circle guide visible in center
- Angle prompts update as scanning progresses
- Liveness bar fills as a real face is detected
- Progress counter shows X / 5 (or X / N from config)
- When minFrames reached: SweetAlert appears:
  ```
  "Capture Complete!
   5 face samples captured. Save enrollment now?
   [Save] [Capture More]"
  ```
- Clicking Save → processing overlay → success SweetAlert → redirect
- Clicking Capture More → resumes scanning

**Upload pane:**
- Dropzone shows with dashed border, cloud icon, drop text
- NO red bar below the dropzone
- Clicking dropzone or dragging files opens file picker
- Selected files appear as styled chips (file icon + truncated name)
- "Upload & Enroll" button enables when files selected
```
