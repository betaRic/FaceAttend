# FaceAttend — Master System Refactor Plan
## Controllers, Services, Helpers, Models, CSS
## Version 1.0 — Deep Analysis Before Any Code Is Written

---

# PART 1 — THE CORE PROBLEM

This is an ASP.NET MVC project. It should behave like one. The server owns the business logic, validates inputs, enforces security, and returns data. The client renders UI and sends requests.

Instead, the project has drifted into a pattern where:
- Business logic is duplicated in JavaScript constants that do not match server values
- Server-side C# files have grown into multi-responsibility god objects
- Services share no consistent API pattern (some are static, some use interfaces, some take a DbContext, some do not)
- Identical classes exist in multiple files under different names
- The `DeviceService.cs` alone does what should be five separate services
- Two error page views are byte-for-byte identical
- CSS tokens are defined in three different places and the component CSS duplicates what is already in the design system
- The only used "interface" files are never injected — they are dead contracts

The goal of this refactor is not to rewrite everything at once. It is to draw a clear map of where every piece of logic lives, identify what is duplicated or misplaced, and move things to where they belong. After this refactor, a developer can read any single file and understand its single purpose.

---

# PART 2 — CONTROLLERS

## 2.1 Current State

| Controller | Location | Lines (est.) | Problem |
|---|---|---|---|
| `KioskController.cs` | `Controllers/` | ~120 | Partial class — OK |
| `KioskController.Device.cs` | `Controllers/` | ~80 | Partial class — OK |
| `HealthController.cs` | `Controllers/` | ~150 | OK |
| `ErrorController.cs` | `Controllers/` | ~60 | OK |
| `ScanController.cs` | `Controllers/Api/` | ~180 | Addressed in enrollment plan |
| `EnrollmentController.cs` | `Controllers/Api/` | ~350 | Addressed in enrollment plan |
| `MobileRegistrationController.cs` | `Controllers/` | ~1,000+ | **Does 5 different things** |
| `EmployeesController.cs` | `Areas/Admin/Controllers/` | ~600 | Contains device management that is not about employees |

## 2.2 MobileRegistrationController — The Biggest Problem

This file does five completely unrelated things:

**Thing 1 — New Employee Enrollment (Steps 1-4):**
`Index`, `Enroll`, `ScanFrame`, `SubmitEnrollment`
These handle the wizard where a new employee registers their face and creates an account.

**Thing 2 — Existing Employee Identification:**
`Identify`, `IdentifyEmployee`
These handle face scanning to identify an existing employee before device registration.

**Thing 3 — Device Registration:**
`Device`, `RegisterDevice`
These handle the form where an employee names and registers their phone.

**Thing 4 — Completion and Status:**
`Success`, `CheckStatus`
These handle the confirmation page and polling for admin approval.

**Thing 5 — Employee Self-Service Portal:**
`Employee`, `ExportAttendance`
These render the attendance history page and CSV download. This has nothing to do with registration.

**Result:** Adding any feature to any one of these five areas requires reading 1,000 lines to avoid breaking the other four. None of them can be tested in isolation.

## 2.3 EmployeesController — Device Methods in the Wrong Place

`EmployeesController.cs` in the Admin area contains:
- `PendingDevices()` — lists devices awaiting approval
- `ApproveDevice(int id)` — approves a device
- `RejectDevice(int id)` — rejects a device
- `Devices()` — lists all devices
- `DisableDevice(int id)` — blocks a device

These are device management operations. They have no relation to creating or editing an Employee record. They exist here because device approval was added to the Employees area as an afterthought.

## 2.4 Target State — Controllers

**Split `MobileRegistrationController.cs` into two files:**

`Controllers/Mobile/MobileEnrollmentController.cs`
Responsibility: The registration wizard for a phone. Index, Enroll, ScanFrame (deleted — use `/api/scan/frame`), SubmitEnrollment (replaced — use `/api/enrollment/enroll`), Identify, IdentifyEmployee, Device, RegisterDevice, Success, CheckStatus.

`Controllers/Mobile/MobilePortalController.cs`
Responsibility: The post-registration self-service portal. Employee, ExportAttendance.

**Add `Areas/Admin/Controllers/DevicesController.cs`:**
Move PendingDevices, ApproveDevice, RejectDevice, Devices, DisableDevice out of EmployeesController.

**Result:** Every controller has one word that describes what it manages.

---

# PART 3 — SERVICES

## 3.1 Current State

| File | Lines (est.) | Problem |
|---|---|---|
| `ConfigurationService.cs` | ~380 | 22+ methods, 3 parallel API styles |
| `DeviceService.cs` | ~600 | 5 unrelated responsibilities |
| `JsonResponseBuilder.cs` | ~430 | Generic + Kiosk-specific mixed |
| `AttendanceService.cs` | ~200 | OK |
| `AuditHelper.cs` | ~80 | OK |
| `AudioManager.cs` | ~170 | C# file generates inline JavaScript |
| `OfficeLocationService.cs` | ~150 | OK |
| `TimeZoneHelper.cs` | ~100 | OK |
| `VisitorService.cs` | ~130 | OK |
| `HealthProbe.cs` | ~90 | OK |
| `EnrollmentAdaptiveService.cs` | ~45 | Used in one place, 45 lines |
| `EmployeePortalService.cs` | ~100 | OK |
| `Background/TempFileCleanupTask.cs` | ~120 | OK |
| `Interfaces/IConfigurationService.cs` | ~15 | **Dead — never injected** |
| `Interfaces/IAttendanceService.cs` | ~15 | **Dead — never injected** |

## 3.2 ConfigurationService — Three Parallel APIs

The problem is that the same value can be fetched three different ways:

```csharp
// Style 1: priority chain (env > db cache > web.config)
ConfigurationService.GetString("MyKey", "default")

// Style 2: direct DB query (bypasses cache, legacy)
ConfigurationService.GetString(db, "MyKey", "default")

// Style 3: explicit cache with custom TTL
ConfigurationService.GetStringCached("MyKey", "default", 120)
```

All three exist because Style 2 was the original API, Style 1 was added as a unified replacement, and Style 3 was added for performance-sensitive paths. The result is that callers pick arbitrarily, the cache is bypassed inconsistently, and new developers cannot tell which style to use.

**What the code actually uses:**
- Style 1 is used in `SecurityHeadersAttribute`, `RateLimitAttribute`, `BiometricCrypto`, most services
- Style 2 is used in `AttendanceService`, `KioskController`, `EmployeesController`, `AttendanceScanService`
- Style 3 is used in `AttendanceScanService`, `OnnxLiveness`

**Style 2 justification vs. reality:** The comment says "use when you already have a DbContext open." But in most call sites, the DbContext is not shared — each call opens a new connection. The cache exists precisely to avoid this. Style 2 undermines the cache.

**Target:** Keep Style 1 as the only public API. Add one helper that reads from an open DbContext only when the value is not yet in cache. Remove Style 2 from all call sites.

## 3.3 DeviceService — Five Services in One File

The file currently handles:

**Responsibility A — HTTP Request Analysis:**
`GenerateFingerprint`, `GetStableUserAgent`, `GetScreenInfo`, `IsMobileDevice`
These inspect the HTTP request and extract device characteristics.

**Responsibility B — Session Utilities:**
`GetVisitorSessionBinding`, `GetShortDeviceFingerprint`
These interact with ASP.NET session state.

**Responsibility C — Persistent Device Token:**
`GenerateDeviceToken`, `GetDeviceTokenFromCookie`, `SetDeviceTokenCookie`, `ClearDeviceTokenCookie`
These manage a long-lived cookie for device re-identification.

**Responsibility D — Device CRUD:**
`RegisterDevice`, `ValidateDevice`, `ApproveDevice`, `SanitizeDeviceName`
These perform database operations on the Devices table.

**Responsibility E — Employee Status:**
`GetEmployeeStatus`, `SetEmployeeStatus`, `ApprovePendingEmployee`, `RejectPendingEmployee`, `CreatePendingDevice`
These modify Employee and Device records together during the approval workflow.

**Target Split:**

`Services/Request/ClientDetectionService.cs`
Contains: `IsMobileDevice`, `GenerateFingerprint`, `GetStableUserAgent`, `GetScreenInfo`, `GetShortDeviceFingerprint`

`Services/Request/DeviceTokenService.cs`
Contains: `GenerateDeviceToken`, `GetDeviceTokenFromCookie`, `SetDeviceTokenCookie`, `ClearDeviceTokenCookie`

`Services/DeviceService.cs` (slimmed)
Contains: `RegisterDevice`, `ValidateDevice`, `ApproveDevice`, `SanitizeDeviceName`, `GetVisitorSessionBinding`

`Services/EmployeeApprovalService.cs`
Contains: `GetEmployeeStatus`, `SetEmployeeStatus`, `ApprovePendingEmployee`, `RejectPendingEmployee`, `CreatePendingDevice`

## 3.4 JsonResponseBuilder — Generic vs. Kiosk-Specific

The file has two distinct sections:

**Section 1 — Generic (lines ~1-180):**
`Success`, `Error`, `ValidationError`, `NotFound`, `SystemBusy`, `RateLimited`, `Unauthorized`, `Forbidden`
These are useful across the entire application.

**Section 2 — Kiosk-Specific (lines ~181-430):**
`ErrorWithTimings`, `LivenessFail`, `EncodingFail`, `TooSoon`, `AttendanceSuccess`, `VisitorScan`, `RegisterDeviceRequired`, `DevicePending`, `DeviceBlocked`, `SelfEnrollOffer`, `SuspiciousLocation`, `ScanError`, `OfficeResolved`, `RequestTimeout`, `NoOffices`, `NotRecognized`

The second section is called exclusively by `AttendanceScanService.cs`. It exists as a builder because the method would otherwise have 15 different return paths inline. But mixing generic utilities with domain-specific builders is why the file is 430 lines.

**Target:**
- Keep `JsonResponseBuilder.cs` with only the 8 generic methods (~100 lines)
- Move the 15 kiosk-specific methods into `AttendanceScanService.cs` as private static helpers, or extract them into `Services/Recognition/AttendanceScanResponses.cs`

## 3.5 AudioManager.cs — A C# File That Generates JavaScript

`AudioManager.cs` generates a 150-line JavaScript block as a C# string and returns it as `IHtmlString`. This is called from `_MobileLayout.cshtml` and `Views/Kiosk/Index.cshtml`.

The reason this exists as C# is that it generates the URLs using `VirtualPathUtility.ToAbsolute()` so the audio paths work regardless of deployment root. This is a legitimate concern.

However, the same result is achievable with a standard `<audio>` tag and a `@Url.Content()` call directly in the layout, with a small static JS file that references `document.getElementById()`. No C# string generation needed.

**Target:** Replace `AudioManager.cs` with a static `Scripts/audio-manager.js` and a partial view `Views/Shared/_AudioManager.cshtml` that renders the `<audio>` tags with correct server-side paths. Delete `AudioManager.cs`.

## 3.6 EnrollmentAdaptiveService.cs — 45 Lines With One Caller

This file exists to add a face encoding vector to an existing employee's stored set. It is called from exactly one place: nowhere visible in the provided code. It was likely used in an earlier version of the attendance pipeline and was never removed when the pipeline changed.

**Action:** Verify no callers exist. Delete if confirmed unused.

## 3.7 Dead Interfaces

`Services/Interfaces/IConfigurationService.cs` defines an interface that `ConfigurationService` does not implement (ConfigurationService is static). Nothing in the codebase injects `IConfigurationService` as a dependency.

`Services/Interfaces/IAttendanceService.cs` defines an interface that `AttendanceService` does implement. But `AttendanceService` is instantiated directly (`new AttendanceService(db)`) in every call site — the interface is never used for injection or mocking.

**Action:** Delete both interface files. If DI is introduced in a future refactor, the interfaces will be written at that time to match what is actually needed.

---

# PART 4 — MODELS AND VIEW MODELS

## 4.1 Current State

| File | Classes Inside | Problem |
|---|---|---|
| `ViewModels/Admin/AttendanceIndexVm.cs` | 6 classes | One file defines the entire attendance reporting data model |
| `ViewModels/Mobile/MobileEnrollmentFormViewModels.cs` | 2 classes | Manual regex validation embedded in model |
| `ViewModels/Mobile/MobilePortalViewModels.cs` | 3 classes | OK |
| `ViewModels/Mobile/MobileEnrollmentViewModel.cs` | 1 class | OK |
| `ViewModels/Admin/AttendanceDetailsVm.cs` | 1 class | OK |
| `ViewModels/Admin/DashboardViewModel.cs` | 2 classes | OK — `RecentAttendanceRow` nested logically |
| `ViewModels/Admin/EmployeeEditVm.cs` | 1 class | Uses DataAnnotations correctly |
| `ViewModels/Admin/OfficeEditVm.cs` | 1 class | OK |
| `ViewModels/Admin/SettingsVm.cs` | 1 class | Very long but all settings fields — acceptable |
| `ViewModels/Admin/WizardViewModel.cs` | 3 classes | OK — enums grouped with model |
| `ViewModels/Shared/CameraViewModel.cs` | 1 class | OK |
| `ViewModels/Shared/FaceProgressViewModel.cs` | 1 class | OK |
| `ViewModels/Shared/FileUploaderViewModel.cs` | 2 classes | OK — enum grouped with model |
| `ViewModels/Shared/ProcessingModalViewModel.cs` | 1 class | OK |
| `ViewModels/Shared/MethodSelectorViewModel.cs` | 2 classes | OK — simple data class |
| `Dtos/EmployeeListRowVm.cs` | 1 class | In `Dtos/` but named `Vm` — naming inconsistency |
| `Dtos/OfficeDto.cs` | 1 class | OK |

## 4.2 AttendanceIndexVm.cs — Six Classes in One File

The file defines:
- `DailySummaryRow` — data for a day row in the chart
- `OfficeSummaryRow` — data for an office row in the breakdown
- `EmployeeSummaryRow` — data for a per-employee monthly summary
- `DailyEmployeeRow` — data for a per-employee per-day row (this alone is ~60 lines)
- `AttendanceRowVm` — data for a row in the main attendance table
- `AttendanceIndexVm` — the full ViewModel for the Index page

`DailyEmployeeRow` has 20+ properties and 6 display helper properties. It calculates AM/PM split for DILG-specific time tracking. This is real complexity that should be documented clearly, not buried 80 lines into a file that starts with `DailySummaryRow`.

**Target:**
```
Models/ViewModels/Admin/Attendance/
    AttendanceIndexVm.cs         (AttendanceIndexVm only)
    AttendanceRowVm.cs           (AttendanceRowVm only)
    AttendanceDetailsVm.cs       (already separate — keep)
    AttendanceSummaryRows.cs     (DailySummaryRow, OfficeSummaryRow)
    EmployeeSummaryReport.cs     (EmployeeSummaryRow, DailyEmployeeRow)
```

## 4.3 MobileEnrollmentFormViewModels.cs — Validation in the Model

`NewEmployeeEnrollmentVm` contains a `Validate()` method with manual regex checking for every field. `DeviceRegistrationVm` does the same. This is the pattern when DataAnnotations are not flexible enough, and it is acceptable.

The problem is not the approach — it is the implementation. The regex patterns in `Validate()` are not the same as the patterns in the JavaScript form validation in `Enroll-mobile.cshtml`. They are close but not identical. When a field fails JavaScript validation, it cannot reach the server. When it fails C# validation, it returns an error code. The two implementations can diverge silently.

**Target:** Document both sets of patterns explicitly and make them identical. Add a constant or comment in the C# model that references where the JS equivalent lives, so they are maintained together.

## 4.4 Dto vs ViewModel Naming

`Models/Dtos/EmployeeListRowVm.cs` — The file is in the `Dtos/` folder but the class is named `EmployeeListRowVm`. This is a minor inconsistency but it matters when someone searches for "Vm" files and misses this one.

**Target:** Rename to `EmployeeListRowDto.cs` and update references, OR move to `ViewModels/Admin/` and keep the name. Either is correct as long as it is consistent.

---

# PART 5 — CSS

## 5.1 Current State

| File | Purpose | Problem |
|---|---|---|
| `Content/fa-design-system.css` | Design tokens + shared micro-components | Contains `.fa-wizard`, `.fa-btn`, `.fa-progress` — duplicated elsewhere |
| `Content/admin.css` | Admin area styles | Uses Bootstrap classes alongside custom `.ea-*` classes — mixing systems |
| `Content/kiosk.css` | Kiosk page styles | 300+ lines at the bottom duplicating Bootstrap |
| `Content/fa-mobile.css` | Mobile registration styles | Redefines tokens using `var(--color-primary)` which comes from design system |
| `Content/enrollment.css` | Enrollment page styles | Duplicates some wizard styles from design system |
| `Content/_unified/components/wizard.css` | Wizard component | **Exact duplicate** of `.fa-wizard` in `fa-design-system.css` |
| `Content/_unified/components/camera.css` | Camera component | OK |
| `Content/_unified/components/modal.css` | Modal component | OK |
| `Content/_unified/components/uploader.css` | Uploader component | OK |
| `Content/_unified/components/method-selector.css` | Method selector | OK |

## 5.2 The Duplication Problem

**Wizard styles exist in two places:**

`fa-design-system.css` defines:
```css
.fa-wizard { ... }
.fa-wizard__step { ... }
.fa-wizard__step.is-active { ... }
.fa-wizard__number { ... }
.fa-wizard__divider { ... }
```

`_unified/components/wizard.css` defines **the same classes** with nearly identical rules. Both files are loaded on enrollment pages via `~/Content/fa-system` and `~/Content/fa-components`. The second definition silently overrides the first for any rule that differs.

**Action:** Delete the wizard block from `fa-design-system.css`. `_unified/components/wizard.css` is the authoritative source. Load it via the main bundle.

**Bootstrap duplication in `kiosk.css`:**

`kiosk.css` ends with approximately 300 lines of Bootstrap-compatible utility classes:
- `.form-control`, `.btn`, `.btn-primary`, `.btn-sm`, `.btn-lg`
- `.alert`, `.alert-danger`, `.alert-success`
- `.table`, `.table th`, `.table td`
- `.badge`, `.modal`, `.modal-dialog`, `.modal-content`
- `.tab-content`, `.nav-tabs`
- `.row`, `.col`, `.col-md-6`
- Text utilities: `.text-success`, `.text-danger`, `.font-weight-bold`
- Spacing: `.mt-1`, `.mt-2`, `.mt-3`, `.mb-1`, etc.

Bootstrap is already loaded via the `~/Content/css` bundle (`bootstrap.min.css`). These definitions are redundant and can cause conflicts when Bootstrap is updated.

**Action:** Delete the Bootstrap compatibility section from `kiosk.css`. If any kiosk-specific overrides of Bootstrap components are needed, move only those specific overrides to a clearly labeled section.

**Google Fonts import location:**

`kiosk.css` starts with:
```css
@import url('https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600;700&family=JetBrains+Mono...');
```

A CSS `@import` inside a stylesheet that is loaded via a bundle is slower than a `<link>` tag in the `<head>`. It forces a synchronous CSS fetch after the bundle loads. This should be in the Kiosk layout's `<head>` as a `<link>` tag, or removed entirely if Inter is not actually needed (the design system falls back to `-apple-system` which looks the same on macOS/iOS).

**Action:** Move `@import` to the layout's `<head>`. Remove from the CSS file.

## 5.3 CSS Token Scope

The design system defines tokens in `:root {}`. This is correct. The mobile surface overrides them in `.mobile-app, [data-theme="mobile"]`. This is correct.

The issue is that `fa-mobile.css` defines its own non-token values inline (specific pixel values, specific colors) instead of using the token variables. For example:
```css
/* In fa-mobile.css */
background: rgba(239, 68, 68, 0.1);  /* instead of var(--color-error) at 10% opacity */
border-left-color: var(--color-warning);
```

This inconsistency means light/dark theme switching would not work on the mobile surface even if it were added.

**Action:** Review `fa-mobile.css` and replace hardcoded color values with the appropriate design system tokens.

---

# PART 6 — VIEWS

## 6.1 Duplicate Error Pages

`Views/Error/ErrorPage.cshtml` and `Views/Shared/ErrorPage.cshtml` are byte-for-byte identical files. `ErrorController.cs` explicitly references `~/Views/Shared/ErrorPage.cshtml`. The file in `Views/Error/` is never referenced by anything.

**Action:** Delete `Views/Error/ErrorPage.cshtml`.

## 6.2 View Comments

Every partial view starts with a block comment explaining usage, JavaScript API, and examples. These comments are valuable when the partial is first written. After six months, they go stale and mislead developers.

The existing comments in:
- `Views/Shared/Components/_Wizard.cshtml`
- `Views/Shared/Components/_Camera.cshtml`
- `Views/Shared/Components/_FileUploader.cshtml`
- `Views/Shared/Components/_ProcessingModal.cshtml`
- `Views/Shared/Components/_FaceProgress.cshtml`
- `Views/Shared/Components/_MethodSelector.cshtml`

...describe JavaScript APIs (`FaceAttend.UI.Wizard.init`, `FaceAttend.UI.Modal.show`) that do not exist in the codebase. These API names were planned but not implemented. A developer reading these comments would look for these functions and not find them.

**Action:** Remove the usage documentation from the top of each partial view. Documentation for how to use these partials belongs in a separate `COMPONENTS.md` file that is maintained alongside the code, not embedded in the view files where it cannot be easily updated.

---

# PART 7 — ROUTE CONFIGURATION

## 7.1 Explicit MobileRegistration Routes

`RouteConfig.cs` defines 12 explicit named routes for `MobileRegistration`:

```csharp
routes.MapRoute("MobileRegistration_Index", "MobileRegistration", ...)
routes.MapRoute("MobileRegistration_Identify", "MobileRegistration/Identify", ...)
routes.MapRoute("MobileRegistration_IdentifyEmployee", "MobileRegistration/IdentifyEmployee", ...)
// ... 9 more
```

These exist because `MobileRegistrationController` is in the root namespace (`FaceAttend.Controllers`) but there is an Admin area, and without explicit routes the default route pattern `{controller}/{action}/{id}` sometimes resolves to the Admin area's version of a controller when one exists.

The correct fix is attribute routing on the controller class, not 12 explicit entries in `RouteConfig`.

**Target:**
```csharp
[RoutePrefix("MobileRegistration")]
public class MobileEnrollmentController : Controller
{
    [Route("")]
    public ActionResult Index() { ... }

    [Route("Identify")]
    public ActionResult Identify() { ... }
    // ...
}
```

With attribute routing, the 12 explicit routes in `RouteConfig.cs` can be removed, and `routes.MapMvcAttributeRoutes()` handles the rest. The default route for `Kiosk` still works because it is not ambiguous.

---

# PART 8 — WHAT TO DELETE

The following can be deleted with zero functional impact:

| File | Reason |
|---|---|
| `Views/Error/ErrorPage.cshtml` | Exact duplicate of `Views/Shared/ErrorPage.cshtml`, never referenced |
| `Services/Interfaces/IConfigurationService.cs` | Never used for injection anywhere |
| `Services/Interfaces/IAttendanceService.cs` | Never used for injection anywhere |
| `Services/AudioManager.cs` | C# generating JavaScript — replace with static JS + partial view |
| `Services/EnrollmentAdaptiveService.cs` | Verify zero callers, then delete |
| All comments in `Views/Shared/Components/*.cshtml` | Document non-existent APIs |
| Bootstrap compatibility section in `kiosk.css` (~300 lines) | Bootstrap already loaded |
| Wizard block in `fa-design-system.css` | Duplicated in `_unified/components/wizard.css` |
| `@import` Google Fonts from `kiosk.css` | Move to layout `<head>` |

Files addressed by the existing Enrollment Refactor Plan (already documented):
| File | Plan Reference |
|---|---|
| `Scripts/modules/face-guide.js` | Enrollment Plan Phase 9 |
| `Scripts/enrollment-ui.js` | Enrollment Plan Phase 9 |
| `Scripts/core/facescan.js` | Enrollment Plan Phase 9 |
| `MobileRegistrationController.ScanFrame()` | Enrollment Plan Phase 3 |
| `MobileRegistrationController.SubmitEnrollment()` | Enrollment Plan Phase 4 |

---

# PART 9 — WHAT TO KEEP EXACTLY AS IS

Do not touch the following during this refactor:

**Biometrics pipeline (Services/Biometrics/):**
`DlibBiometrics.cs`, `OnnxLiveness.cs`, `FastScanPipeline.cs`, `FastFaceMatcher.cs`, `FaceEncodingHelper.cs`, `FaceQualityAnalyzer.cs`, `BiometricCrypto.cs`, `BallTreeIndex.cs`, `EmployeeFaceIndex.cs`, `VisitorFaceIndex.cs`, `FaceIndexBase.cs`, `DuplicateCheckHelper.cs`, `EnrollmentQualityGate.cs`, `EnrollCandidate.cs`, `ImagePreprocessor.cs`

**Recognition pipeline (Services/Recognition/):**
`AttendanceScanService.cs`, `VisitorScanService.cs`, `PendingScanService.cs`

**Security (Services/Security/):**
`FileSecurityService.cs`, `LocationAntiSpoof.cs`, `AdminAccessControl.cs`

**Focused services:**
`AttendanceService.cs`, `AuditHelper.cs`, `OfficeLocationService.cs`, `TimeZoneHelper.cs`, `VisitorService.cs`, `HealthProbe.cs`, `EmployeePortalService.cs`, `Background/TempFileCleanupTask.cs`

**Helpers:**
`Services/Helpers/StringHelper.cs`, `ValidationHelper.cs`, `FileSystemHelper.cs`, `CsvHelper.cs`

**Core JS:**
`Scripts/core/camera.js`, `Scripts/core/fa-helpers.js`, `Scripts/core/notify.js`, `Scripts/core/api.js`

**Infrastructure:**
`Filters/SecurityHeadersAttribute.cs`, `Filters/RateLimitAttribute.cs`, `Filters/AdminAuthorizeAttribute.cs`

**CSS (keep, do not modify):**
`Content/_unified/components/camera.css`, `modal.css`, `uploader.css`, `method-selector.css`

---

# PART 10 — IMPLEMENTATION ORDER

These phases are independent of the Enrollment Refactor Plan phases. Coordinate sequencing if both are in progress simultaneously.

## Phase A — Delete Dead Code (No Risk, Do First)

1. Delete `Views/Error/ErrorPage.cshtml`
2. Delete `Services/Interfaces/IConfigurationService.cs`
3. Delete `Services/Interfaces/IAttendanceService.cs`
4. Verify `EnrollmentAdaptiveService.cs` has no callers → Delete
5. Remove Bootstrap compatibility block from `kiosk.css`
6. Remove `@import` Google Fonts from `kiosk.css`, add `<link>` to Kiosk layout
7. Remove duplicate `.fa-wizard` block from `fa-design-system.css`
8. Remove stale API documentation comments from all `Views/Shared/Components/*.cshtml` files

**Risk:** Zero. Nothing calling deleted files, no behavior changes.

## Phase B — CSS Token Cleanup

1. Audit `fa-mobile.css` for hardcoded hex colors → replace with token variables
2. Add wizard component CSS to the main bundle so pages that drop `~/Content/fa-components` still get it
3. Document the bundle loading order in `BundleConfig.cs` as a comment block at the top

**Risk:** Low. Visual regression on mobile pages only. Test on mobile registration flow.

## Phase C — ConfigurationService Simplification

1. Remove all `GetString(FaceAttendDBEntities db, ...)` overloads from the public API
2. Update all call sites that use Style 2 to use Style 1
3. Keep `GetStringCached` as an alias for `GetString` (same behavior — Style 1 already caches)
4. Remove duplicate aliases: `Upsert` → `SetInDb` (keep one name only), `Delete` → `DeleteFromDb` (keep one name only), `Invalidate` → `InvalidateCache` (keep one name only)

**Call sites that use Style 2 and need updating:**
- `AttendanceService.cs` — uses `GetInt(db, ...)` in 4 places
- `KioskController.cs` — uses `GetInt(db, ...)` in ResolveOffice
- `AttendanceScanService.cs` — uses `GetDouble(db, ...)`, `GetInt(db, ...)`, `GetBool(db, ...)` in ~8 places
- `EmployeesController.cs` — uses `GetInt(db, ...)` in 1 place

**Risk:** Low. The values returned are identical — only the lookup path changes from direct DB to cached.

## Phase D — AudioManager Replacement

1. Create `Views/Shared/_AudioManager.cshtml` — renders `<audio id="audio-success" ...>` and `<audio id="audio-notif" ...>` with server-side `@Url.Content()` paths
2. Create `Scripts/audio-manager.js` — reads `document.getElementById('audio-success')` and `document.getElementById('audio-notif')`, exposes `window.FaceAttendAudio.playSuccess()` and `window.FaceAttendAudio.playNotification()`. Same API, no behavior change.
3. Add `Scripts/audio-manager.js` to the kiosk bundle
4. Replace `@FaceAttend.Services.AudioManager.GetAudioManagerScript(Context)` calls in both layouts with `@Html.Partial("~/Views/Shared/_AudioManager.cshtml")`
5. Delete `Services/AudioManager.cs`

**Risk:** Low. Audio playback behavior is unchanged. Test unlock sound and success notification on kiosk.

## Phase E — Split MobileRegistrationController

1. Create `Controllers/Mobile/` directory
2. Create `Controllers/Mobile/MobileEnrollmentController.cs` with `[RoutePrefix("MobileRegistration")]` — move enrollment and registration methods
3. Create `Controllers/Mobile/MobilePortalController.cs` with `[RoutePrefix("MobileRegistration")]` — move Employee and ExportAttendance
4. Remove the 12 explicit MobileRegistration routes from `RouteConfig.cs`
5. Ensure `routes.MapMvcAttributeRoutes()` call comes first in `RegisterRoutes()`
6. Update view paths to match new controller names if any views rely on convention-based routing
7. Delete `Controllers/MobileRegistrationController.cs`

**Risk:** Medium. URL paths must not change — clients have bookmarked `/MobileRegistration/Employee`. The `[Route("")]` attributes must preserve every existing URL exactly.

## Phase F — Split DeviceService

1. Create `Services/Request/ClientDetectionService.cs`
2. Move `IsMobileDevice`, `GenerateFingerprint`, `GetStableUserAgent`, `GetScreenInfo`, `GetShortDeviceFingerprint` into it
3. Create `Services/Request/DeviceTokenService.cs`
4. Move `GenerateDeviceToken`, `GetDeviceTokenFromCookie`, `SetDeviceTokenCookie`, `ClearDeviceTokenCookie` into it
5. Create `Services/EmployeeApprovalService.cs`
6. Move `GetEmployeeStatus`, `SetEmployeeStatus`, `ApprovePendingEmployee`, `RejectPendingEmployee`, `CreatePendingDevice` into it
7. Update all call sites across controllers
8. Keep `DeviceService.cs` with only: `RegisterDevice`, `ValidateDevice`, `ApproveDevice`, `SanitizeDeviceName`, `GetVisitorSessionBinding`

**Risk:** Medium. Many call sites across 4-5 controllers. Compile-verify after each move.

## Phase G — JsonResponseBuilder Cleanup

1. Remove the 15 kiosk-specific methods from `JsonResponseBuilder.cs`
2. Move them to `Services/Recognition/AttendanceScanResponses.cs` as a new static class
3. Update `AttendanceScanService.cs` to call `AttendanceScanResponses.*` instead of `JsonResponseBuilder.*`
4. Add `internal` modifier to the new class — it is not a general utility

**Risk:** Low. Mechanical move. Compile-verify.

## Phase H — Admin DevicesController

1. Create `Areas/Admin/Controllers/DevicesController.cs`
2. Move `PendingDevices`, `ApproveDevice`, `RejectDevice`, `Devices`, `DisableDevice` from `EmployeesController` into it
3. Update views: `PendingDevices.cshtml`, `Devices.cshtml` — move from `Areas/Admin/Views/Employees/` to `Areas/Admin/Views/Devices/`
4. Update navigation links in admin sidebar layout

**Risk:** Low. Isolated admin area change. Navigation links must be updated.

## Phase I — ViewModel Split

1. Create `Models/ViewModels/Admin/Attendance/` directory
2. Split `AttendanceIndexVm.cs` into 4 files as described in Part 4
3. Fix naming: rename `Models/Dtos/EmployeeListRowVm.cs` to `EmployeeListRowDto.cs`
4. Update all using references

**Risk:** Low. Mechanical split. Compiler will catch all missing references.

---

# PART 11 — FINAL FILE MANIFEST AFTER ALL PHASES

## Controllers After Refactor

| File | Single Responsibility |
|---|---|
| `Controllers/KioskController.cs` | Kiosk page and attendance scanning |
| `Controllers/KioskController.Device.cs` | Kiosk device state endpoint |
| `Controllers/HealthController.cs` | Health and diagnostics |
| `Controllers/ErrorController.cs` | Error pages |
| `Controllers/Api/ScanController.cs` | Single frame scan for enrollment |
| `Controllers/Api/EnrollmentController.cs` | Enroll from JPEG files |
| `Controllers/Mobile/MobileEnrollmentController.cs` | Mobile registration wizard |
| `Controllers/Mobile/MobilePortalController.cs` | Employee self-service portal |
| `Areas/Admin/Controllers/EmployeesController.cs` | Employee CRUD + face enrollment |
| `Areas/Admin/Controllers/DevicesController.cs` | Device approval and management |

## Services After Refactor

| File | Single Responsibility |
|---|---|
| `Services/ConfigurationService.cs` | Single-API config with env/cache/web.config priority |
| `Services/AttendanceService.cs` | Record attendance with gap enforcement |
| `Services/AuditHelper.cs` | Write audit log entries |
| `Services/OfficeLocationService.cs` | GPS → office resolution |
| `Services/TimeZoneHelper.cs` | UTC ↔ local time for PH timezone |
| `Services/VisitorService.cs` | Record visitor log entries |
| `Services/HealthProbe.cs` | Collect system health snapshot |
| `Services/EmployeePortalService.cs` | Build monthly report and CSV |
| `Services/DeviceService.cs` | Device registration, validation, approval |
| `Services/EmployeeApprovalService.cs` | Employee status and pending enrollment approval |
| `Services/Request/ClientDetectionService.cs` | Mobile detection, browser fingerprinting |
| `Services/Request/DeviceTokenService.cs` | Persistent device cookie management |
| `Services/JsonResponseBuilder.cs` | 8 generic JSON response helpers only |
| `Services/Recognition/AttendanceScanService.cs` | Full attendance scan pipeline |
| `Services/Recognition/AttendanceScanResponses.cs` | Kiosk-specific response builders |
| `Services/Recognition/VisitorScanService.cs` | Visitor scan result cache |
| `Services/Recognition/PendingScanService.cs` | Medium-tier match confirmation cache |
| `Services/Background/TempFileCleanupTask.cs` | Orphaned temp file cleanup |
| `Services/OperationResult.cs` | Generic result wrapper |

## Files Deleted

| File | Replaced By |
|---|---|
| `Controllers/MobileRegistrationController.cs` | `MobileEnrollmentController` + `MobilePortalController` |
| `Views/Error/ErrorPage.cshtml` | `Views/Shared/ErrorPage.cshtml` (already exists) |
| `Services/Interfaces/IConfigurationService.cs` | Nothing — was unused |
| `Services/Interfaces/IAttendanceService.cs` | Nothing — was unused |
| `Services/AudioManager.cs` | `Scripts/audio-manager.js` + `_AudioManager.cshtml` |
| `Services/EnrollmentAdaptiveService.cs` | Nothing — was unused |

---

# PART 12 — WHAT THIS PLAN DOES NOT COVER

These are real issues that are deliberately out of scope for this plan. They would each require a separate plan of similar depth.

**Database layer:** Entity Framework is used directly in controllers and services. No repository pattern. This is acceptable for this project size but limits testability.

**Authentication architecture:** Admin auth is PIN-based with session. This is fine for a single-admin kiosk deployment. Scaling to multiple admins would require a different model.

**Error handling:** Most service methods have empty `catch { }` blocks that silently swallow exceptions. The kiosk requirement is to never crash, which justifies this, but audit logging on exceptions would improve diagnostics.

**Test coverage:** Zero tests exist. Writing tests before this refactor would reveal which interfaces are actually needed. This plan proceeds without tests.

**Visitor enrollment flow:** Not touched — deliberately out of scope per the enrollment refactor plan.

---

# PART 13 — COMMENT POLICY

After this refactor, the following comment rules apply to all files:

**Delete:**
- Any comment that restates what the code clearly shows: `// Check if null` above `if (x == null)`
- Any comment describing a method name: `// Gets the employee ID` above `GetEmployeeId()`
- Any comment with a language switch for no reason (English and Filipino intermixed in the same comment block)
- Any comment referencing a phase, ticket, or sprint that is now complete: `// PHASE 1 FIX (S-09)`
- Usage documentation in partial view files that documents non-existent JavaScript APIs

**Keep:**
- XML doc comments (`/// <summary>`) on public methods that are not self-explanatory
- Comments explaining WHY a decision was made, not WHAT the code does
- Security-relevant comments: explaining why DPAPI scope is LocalMachine, why serializable isolation is used
- Config key comments: showing the expected value format and default

**Example of a comment to delete:**
```csharp
// PHASE 1 FIX (S-09): In-enable na ang RequireHttpsAttribute.
// Ang filter na ito ay nagre-redirect ng lahat ng HTTP request papunta sa HTTPS.
```
This describes a completed task. The code is self-explanatory. Delete it.

**Example of a comment to keep:**
```csharp
// DPAPI LocalMachine: machine-bound, no separate key file to manage.
// Migrating servers requires decrypt + re-encrypt while old machine is accessible.
```
This explains a non-obvious architectural decision with real implications. Keep it.
