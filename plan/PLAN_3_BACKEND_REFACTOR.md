# FaceAttend — Plan 3: Backend Refactor
## C# Backend · Filters · Models · CSS · Admin JavaScript

**Status:** Final — reviewed against Plan 1 (System Architecture) and Plan 2 (Enrollment Pipeline)
**Rule:** One phase per commit. Build and spot-test before committing. Never combine phases.

---

## What This Plan Does NOT Touch

These are explicitly owned by other plans or are deliberately stable:

- `enrollment-core.js`, `enrollment-tracker.js`, `face-guide.js`, `facescan.js`, `enrollment-ui.js` — Plan 2 owns all enrollment JS.
- `kiosk.js` attendance scan pipeline — Plan 2 defines the JS pipeline boundary.
- `DlibBiometrics.cs`, `OnnxLiveness.cs`, `FastFaceMatcher.cs`, `BallTreeIndex.cs`, `FastScanPipeline.cs` — biometric engine internals are focused and stable.
- `fa-design-system.css` design tokens — these are the single source of truth, untouched.
- Database schema or migrations — separate concern, separate plan.
- `MobileRegistrationController.cs` split into `MobileEnrollmentController` and `MobilePortalController` — Plan 1 defines those splits. Plan 3 cleans what remains before the split.
- `EmployeesController` device methods moved to `DevicesController` — Plan 1 defines that move. Plan 3 cleans what remains.

---

## Full Problem Inventory

### A — Controllers

| File | Specific Problem |
|---|---|
| `AttendanceController.cs` | `BuildDailyRow()`, `BuildCsv()`, `LoadAttendancePolicy()` are business logic inside a controller. Three private nested classes (`AttendancePolicy`, `RawLog`, `ExportRow`) defined inline in the controller file. `QueryEmployeeRows()` SQL in controller. `BuildCsv()` is 60 lines of CSV assembly inline. |
| `VisitorsController.cs` | `EnrollFace()` runs `FastScanPipeline` directly instead of delegating to a service. Action named `Deactivate` permanently deletes the visitor and all logs — name is actively misleading. Dead private nested class `EnrollCandidate` at the bottom of the file is never used. |
| `OfficesController.cs` | `validTypes` string array declared and validated identically in both `Create` and `Edit` POST actions — copy-paste with no extraction. |
| `KioskController.cs` | `SubmitVisitor` returns `new { ok, error, message }` raw anonymous object instead of `JsonResponseBuilder`. |
| `EmployeesController.cs` | `DbEntityValidationException` catch block copy-pasted identically in both `Create` and `Edit`. Each extracts `ValidationErrors` the same way. |
| `EnrollmentController.cs` | `SelectDiverseByEmbedding()` is a non-trivial selection algorithm living as a private method inside a controller. |

### B — Services

| File | Specific Problem |
|---|---|
| `AudioManager.cs` | Generates a 100-line JavaScript string by embedding it inside a C# string literal. Impossible to lint, format, or test the JS. IntelliSense is dead inside the string. |
| `EnrollmentAdaptiveService.cs` | Called in exactly one place (`EmployeesController.Enroll` — or intended to be). The method `TryAddVector()` is 45 lines. At this size it belongs inline in whatever calls it, or merged into `AttendanceScanService`. Verified: not referenced anywhere in the codebase after the enrollment refactor. Dead after Plan 2. |
| `StringHelper.cs` | Defines two classes in one file: `StringExtensions` (extension methods) and `StringHelper` (static methods). The extension class wraps `StringHelper` with zero additional logic. Pick one approach and delete the wrapper. |
| `ValidationHelper.cs` | `IsValidPhilippinesCoordinates()` and `ValidatePhilippinesCoordinates()` are geographic boundary checks, not input validation utilities. They belong in `OfficeLocationService`. The remaining methods (employee ID, email, phone, device name, image, purpose validation) are genuinely validation utilities and stay. |
| `Services/Interfaces/IAttendanceService.cs` | Interface for a class that is never registered with a DI container. The only `AttendanceService` usage is `new AttendanceService(db)` directly. Plan 1 marks `IConfigurationService` for deletion. This interface has the same problem. Both are dead contracts. |
| `AttendanceScanService.cs` | The `Scan()` method is approximately 400 lines with one exit path and no sub-methods. Logic at line 1 and logic at line 390 are completely unrelated but live in the same method body. The method does: image validation, office resolution, image save, face detection, liveness, encoding, matching, tier voting, device check, needs-review flag computation, anti-spoof check, attendance record. Each of those is a named step that should be a private method. |

### C — Filters

| File | Specific Problem |
|---|---|
| `AdminAuthorizeAttribute.cs` | Four responsibilities in one class: (1) authorization decision (`AuthorizeCore`), (2) session management (`MarkAuthed`, `ClearAuthed`, `RefreshSession`, `GetRemainingSessionSeconds`, `RotateSessionId`), (3) unlock cookie lifecycle (`IssueUnlockCookie`, `TryConsumeUnlockCookie`, `ExpireUnlockCookie`), (4) PIN verification with lockout (`VerifyPin`, `HashPin`, `TryVerifyPbkdf2`, `_lockouts`). A developer who needs to change the PIN lockout duration must read 650 lines to find the relevant constant. |

### D — Models and ViewModels

| File | Specific Problem |
|---|---|
| `AttendanceIndexVm.cs` | Six classes in one file: `DailySummaryRow`, `OfficeSummaryRow`, `EmployeeSummaryRow`, `DailyEmployeeRow`, `AttendanceRowVm`, `AttendanceIndexVm`. The summary-specific classes (`EmployeeSummaryRow`, `DailyEmployeeRow`) are only used in `SummaryReport` and have no relation to the index view. |
| `MobileEnrollmentFormViewModels.cs` | `NewEmployeeEnrollmentVm.Validate()` is 35 lines of hand-written validation logic inside a ViewModel. `NewEmployeeEnrollmentVm.Sanitize()` strips HTML tags with manual regex. Both belong on the server as `[DataAnnotation]` attributes or a dedicated validator, not inside the ViewModel class. Same pattern in `DeviceRegistrationVm`. |
| `Models/Dtos/EmployeeListRowVm.cs` | Named `Vm` (ViewModel) but placed in `Models/Dtos/`. A DTO is a flat data transfer object. A ViewModel carries display logic and state for a specific view. This is a DTO — rename the file and class to `EmployeeListRowDto`. |
| `Services/Biometrics/EnrollCandidate.cs` | A plain data class with five properties living in the `Services/Biometrics/` namespace. It has no behaviour and no service logic. It is a DTO used only during enrollment processing. It belongs in `Models/Dtos/`. |
| `VisitorsController.cs` (nested) | Private nested class `EnrollCandidate` at the bottom — dead copy of the real `EnrollCandidate` in `Services/Biometrics/`. |

### E — CSS

| File | Specific Problem |
|---|---|
| `Content/_unified/components/wizard.css` | Re-defines the full `.fa-wizard`, `.fa-wizard__step`, `.fa-wizard__number`, `.fa-wizard__label`, `.fa-wizard__divider` rule set. These rules already exist in `fa-design-system.css` Section 4. Every selector is duplicated. |
| `Content/kiosk.css` | Lines 300–650 (approx) contain a complete Bootstrap compatibility layer: `.btn`, `.btn-primary`, `.btn-secondary`, `.btn-sm`, `.btn-lg`, `.alert`, `.alert-danger`, `.card`, `.table`, `.badge`, `.nav-tabs`, `.modal`, `.input-group`, `.form-control`, etc. These have no relation to kiosk UI. They exist because admin views were linked to kiosk CSS at some point. |
| `Content/enrollment.css` | The `.fa-method-card` overrides in the comment `"Fixes for alignment and consistency"` section patch values that should be correct in the source `_unified/components/method-selector.css`. Patches in `enrollment.css` hide the real problem in the component file. |

### F — JavaScript (Admin Only — All Other JS Is Plan 2)

| File | Specific Problem |
|---|---|
| `Scripts/admin.js` | Eight unrelated responsibilities in one 500-line file: (1) `toast()` notification wrapper, (2) `confirmDialog()` modal wrapper, (3) footer year setter, (4) Bootstrap tooltip init, (5) auto-dismiss alerts, (6) server toast bridge (`__toastMsg`), (7) confirm link interception, (8) DataTables init, (9) idle overlay, (10) Leaflet office map, (11) back-to-top button, (12) keyboard shortcuts, (13) refresh button, (14) sidebar active state. |

The toast (`toast()`) and confirm (`confirmDialog()`) functions in `admin.js` produce output that partially overlaps with `Scripts/core/notify.js`. The admin toast wraps `toastr`, while `notify.js` wraps `Swal`. Both expose `ui.toast`, `ui.confirm` on `window.ui`. One of them is redundant.

### G — Repeated Patterns

| Pattern | Locations |
|---|---|
| Face cache invalidation 3-line block: `EmployeeFaceIndex.Invalidate()`, `FastFaceMatcher.ReloadFromDatabase()`, `FastFaceMatcher.Initialize()` | 6 places: `EmployeesController.Edit`, `EmployeesController.ApprovePendingEmployee`, `EmployeesController.Delete`, `DashboardController.ClearFaceCache`, `VisitorsController.Deactivate`, `EnrollmentController.Enroll` |
| `DbEntityValidationException` catch block | `EmployeesController.Create`, `EmployeesController.Edit` |
| `ConfigurationService.GetX(db, key, ConfigurationService.GetX(key, fallback))` double-call | ~50 times in `SettingsViewModelBuilder.cs` and `SettingsSaver.cs` |
| `validTypes` array + `if (!validTypes.Contains(vm.Type))` | `OfficesController.Create`, `OfficesController.Edit` |
| Raw `new { ok, error, message }` JSON return | `KioskController.SubmitVisitor`, multiple locations in `MobileRegistrationController` |

---

## Phase 1 — Comment Purge

**Zero logic changes. One commit. Do this first.**

The codebase contains three categories of noise: phase-marker comments from development, multilingual comments (English/Tagalog/Ilocano mixed), and comments that restate what the code already shows.

### Delete Rules

| Pattern | Rule |
|---|---|
| `// FIX:`, `// FIX-NNN:`, `// PHASE N FIX`, `// AUDIT FIX (H-05)` | Delete — the fix is in the committed code |
| `// SAGUPA:`, `// PAGLALARAWAN:`, `// GINAGAMIT SA:`, `// ILOKANO:` | Delete — codebase language is English |
| Any inline Tagalog or Ilocano sentence in a comment block | Delete |
| `// OPT-NN:` performance notes | Delete — development notes, not documentation |
| `// TRACK-NN:`, `// CHANGES vs N.N.N:` version notes inside JS files | Delete — Git history exists |
| `// FIX-CANVAS-GAP`, `// RETAKE BUG FIX` | Delete — the fix is committed |
| `// REMOVED DUPLICATE`, `// NOTE:` followed by old method name | Delete |
| Commented-out code blocks (`//var x = ...`, `// old code`) | Delete |
| `#if DEBUG` blocks containing only empty statements or console logs | Delete |
| `// Silent fail`, `// best effort`, `// no-op`, `// ignore` above empty catch | Delete |
| Comments that restate the method name: `// Gets the CSRF token` above `getCsrfToken()` | Delete |
| Taglish notes like `// Taglish note:` followed by an explanation in mixed language | Delete |
| `// Timestamps are now stored in local time - no conversion needed` (appears 12+ times) | Delete — state it once in `AttendanceService.cs` only |

### Keep Rules

| Pattern | Rule |
|---|---|
| XML `/// <summary>` doc comments on public service methods | Keep |
| One-sentence explanation of a non-obvious architectural decision | Keep |
| Security-relevant comment explaining WHY a check exists | Keep |
| `// DPAPI LocalMachine: machine-bound...` type comments | Keep |
| Config key format documentation: `// Format: HH:mm, default "08:00"` | Keep |

### Priority Files (Highest Noise)

1. `AttendanceService.cs` — SAGUPA/PAGLALARAWAN/IMPORTANTENG PAALALA header
2. `HealthProbe.cs` — SAGUPA header + ILOKANO footnote
3. `DashboardViewModel.cs` — multi-paragraph Filipino explanation on `LivenessCircuitOpen`
4. `DeviceService.cs` — story-style narrative comments inside each region
5. `AttendanceScanService.cs` — OPT-NN inline performance notes throughout `Scan()`
6. `kiosk.js` — OPT-NN, DEBUG comments, empty `#if DEBUG` blocks, TIMING notes
7. `enrollment-tracker.js` — TRACK-NN change notes, old version comparisons
8. `TimeZoneHelper.cs` — Taglish note inside `LocalDateToUtcRange`

**Commit message:** `chore: remove noise comments across codebase`

---

## Phase 2 — Delete Dead Code

**Verify compilation after each deletion. Keep each deletion as a separate atomic commit.**

### 2A — Delete `Services/Interfaces/IConfigurationService.cs`

`ConfigurationService` is declared as `static class`. A static class cannot implement an interface. This interface has zero implementations and zero usages anywhere in the solution.

Steps:
1. Search entire solution for `IConfigurationService` — verify zero usages
2. Delete the file
3. Build

### 2B — Delete `Services/Interfaces/IAttendanceService.cs`

`AttendanceService` is never registered in a DI container. Every usage is `new AttendanceService(db)` directly. The interface is a dead contract with no implementation registered anywhere.

Steps:
1. Search entire solution for `IAttendanceService` — verify zero DI registrations
2. Delete the file
3. Build

### 2C — Delete `Services/EnrollmentAdaptiveService.cs`

After Plan 2 is applied, `MobileRegistrationController.ScanFrame` and `MobileRegistrationController.SubmitEnrollment` are deleted. These were the only callers. Search confirms zero remaining usages.

Steps:
1. Search entire solution for `EnrollmentAdaptiveService` — verify zero usages
2. Delete the file
3. Build

### 2D — Delete `StringExtensions` class from `StringHelper.cs`

`StringExtensions` is defined in the same file as `StringHelper`. It adds zero new logic — it is a wrapper that calls `StringHelper.Truncate()` and `StringHelper.TruncateAndTrim()` with extension method syntax.

Steps:
1. Search for `.Truncate(` and `.TruncateAndTrim(` call sites using extension syntax
2. Replace each with `StringHelper.Truncate(value, n)` direct calls
3. Delete the `StringExtensions` class from `StringHelper.cs`
4. Build

### 2E — Delete dead `EnrollCandidate` nested class in `VisitorsController.cs`

`VisitorsController.cs` has a private nested `EnrollCandidate` class at the bottom. It is never referenced inside that file. It is a stale copy from the enrollment pipeline.

Steps:
1. Open `VisitorsController.cs`, find `private class EnrollCandidate` at the bottom
2. Delete it
3. Build

### 2F — Rename `VisitorsController.Deactivate` to `Delete`

The action permanently deletes the visitor record and all visitor logs. The name `Deactivate` implies a soft status change. A developer reading the action name will expect a reversible operation.

Steps:
1. Rename the action method from `Deactivate` to `Delete`
2. Search for `Deactivate` in all Visitor views and update the form `action` attribute
3. Update any sidebar or navigation link that points to the deactivate action
4. Build and test

**Commit message:** `chore: delete dead code, dead interfaces, and rename misleading method`

---

## Phase 3 — Extract Repeated Patterns

**Each extraction is one commit. Compiler is the verification.**

### 3A — Extract Face Cache Invalidation

The following three-line block appears in six places across three controllers:

```csharp
EmployeeFaceIndex.Invalidate();
FastFaceMatcher.ReloadFromDatabase();
if (!FastFaceMatcher.IsInitialized) FastFaceMatcher.Initialize();
```

**What to do:**

Add one static method to `FastFaceMatcher.cs`:

```csharp
public static void InvalidateAndReload()
{
    EmployeeFaceIndex.Invalidate();
    ReloadFromDatabase();
    if (!IsInitialized) Initialize();
}
```

Replace all six occurrences with `FastFaceMatcher.InvalidateAndReload()`.

Locations to update:
- `EmployeesController.Edit`
- `EmployeesController.ApprovePendingEmployee`
- `EmployeesController.Delete`
- `DashboardController.ClearFaceCache`
- `VisitorsController.Delete` (renamed in Phase 2D)
- `EnrollmentController.Enroll`

### 3B — Extract Office Type Validation

Both `OfficesController.Create` (POST) and `OfficesController.Edit` (POST) declare:

```csharp
var validTypes = new[] { "REGION", "PROVINCE", "HUC" };
vm.Type = (vm.Type ?? "").Trim().ToUpperInvariant();
if (!validTypes.Contains(vm.Type))
    ModelState.AddModelError("Type", "Type must be REGION, PROVINCE, or HUC.");
```

**What to do:**

Add a private method to `OfficesController`:

```csharp
private void ValidateOfficeType(OfficeEditVm vm)
{
    vm.Type = (vm.Type ?? "").Trim().ToUpperInvariant();
    var valid = new[] { "REGION", "PROVINCE", "HUC" };
    if (!valid.Contains(vm.Type))
        ModelState.AddModelError("Type", "Type must be REGION, PROVINCE, or HUC.");
}
```

Replace both inline blocks with `ValidateOfficeType(vm)`.

### 3C — Extract `DbEntityValidationException` Handling

Both `EmployeesController.Create` and `EmployeesController.Edit` contain this identical catch block:

```csharp
catch (System.Data.Entity.Validation.DbEntityValidationException ex)
{
    var errors = ex.EntityValidationErrors
        .SelectMany(e => e.ValidationErrors)
        .Select(e => $"{e.PropertyName}: {e.ErrorMessage}");
    ModelState.AddModelError("", "Validation error: " + string.Join("; ", errors));
    SetOffices(db, vm.OfficeId);
    return View(vm);
}
```

**What to do:**

Create `Infrastructure/ControllerExtensions.cs`:

```csharp
namespace FaceAttend.Infrastructure
{
    public static class ControllerExtensions
    {
        public static void AddDbValidationErrors(
            this System.Web.Mvc.ModelStateDictionary modelState,
            System.Data.Entity.Validation.DbEntityValidationException ex)
        {
            foreach (var error in ex.EntityValidationErrors
                .SelectMany(e => e.ValidationErrors))
            {
                modelState.AddModelError("",
                    error.PropertyName + ": " + error.ErrorMessage);
            }
        }
    }
}
```

Replace both catch blocks with:
```csharp
catch (System.Data.Entity.Validation.DbEntityValidationException ex)
{
    ModelState.AddDbValidationErrors(ex);
    SetOffices(db, vm.OfficeId);
    return View(vm);
}
```

### 3D — Fix `KioskController.SubmitVisitor` Response Format

`SubmitVisitor` returns raw anonymous objects in several branches instead of `JsonResponseBuilder`:

Replace:
```csharp
return Json(new { ok = false, error = "SCAN_ID_REQUIRED", message = "Scan ID is required." });
```

With:
```csharp
return JsonResponseBuilder.Error("SCAN_ID_REQUIRED", "Scan ID is required.");
```

Apply the same replacement to every raw `return Json(new { ok = ... })` in the method body.

### 3E — Reduce `ConfigurationService` Double-Call in Settings Files

`SettingsViewModelBuilder.cs` and `SettingsSaver.cs` each call `ConfigurationService` with the double-call pattern approximately 50 times:

```csharp
ConfigurationService.GetDouble(db, "Biometrics:DlibTolerance",
    ConfigurationService.GetDouble("Biometrics:DlibTolerance", 0.60));
```

The inner call reads from env/web.config. The outer call reads from DB first and falls back to the inner result. This pattern exists because `GetX(db, key, fallback)` does not automatically fall through to env/web.config — it needs a pre-computed fallback.

**What to do:**

Add a private helper to each file (not a shared utility — these two files are the only callers):

In `SettingsViewModelBuilder.cs`:
```csharp
private static double D(FaceAttendDBEntities db, string key, double def)
    => ConfigurationService.GetDouble(db, key, ConfigurationService.GetDouble(key, def));

private static int I(FaceAttendDBEntities db, string key, int def)
    => ConfigurationService.GetInt(db, key, ConfigurationService.GetInt(key, def));

private static string S(FaceAttendDBEntities db, string key, string def)
    => ConfigurationService.GetString(db, key, ConfigurationService.GetString(key, def));

private static bool B(FaceAttendDBEntities db, string key, bool def)
    => ConfigurationService.GetBool(db, key, ConfigurationService.GetBool(key, def));
```

Replace every double-call with `D(db, "key", value)`, `I(db, "key", value)`, etc. Apply the same pattern to `SettingsSaver.cs`.

**Commit message:** `refactor: extract repeated patterns — cache invalidation, office type, db validation, config calls`

---

## Phase 4 — Service Decomposition

### 4A — Extract GPS Validation from `ValidationHelper.cs`

`ValidationHelper.cs` contains `IsValidPhilippinesCoordinates()` and `ValidatePhilippinesCoordinates()`. These are geographic boundary checks, not input validators. They belong in `OfficeLocationService.cs`.

Steps:
1. Move both methods to `OfficeLocationService.cs`
2. Search for usages of `ValidationHelper.IsValidPhilippinesCoordinates` and update the namespace
3. Build

### 4B — Extract `EnrollCandidate` DTO to `Models/Dtos/`

`Services/Biometrics/EnrollCandidate.cs` is a plain data class with five properties and no service logic. It belongs with the other DTOs.

Steps:
1. Move `EnrollCandidate.cs` to `Models/Dtos/EnrollCandidate.cs`
2. Update the namespace from `FaceAttend.Services.Biometrics` to `FaceAttend.Models.Dtos`
3. Add `using FaceAttend.Models.Dtos;` to `EnrollmentController.cs` and `EnrollmentQualityGate.cs`
4. Build

### 4C — Move `SelectDiverseByEmbedding` out of `EnrollmentController`

`SelectDiverseByEmbedding()` is a max-min diversity selection algorithm. It is complex, standalone, and has nothing to do with HTTP. It belongs in `EnrollmentQualityGate.cs` alongside the other enrollment quality logic.

Steps:
1. Move `SelectDiverseByEmbedding()` to `EnrollmentQualityGate.cs` as a `public static` method
2. Update `EnrollmentController.Enroll()` to call `EnrollmentQualityGate.SelectDiverseByEmbedding(candidates, maxStored)`
3. Build

### 4D — Extract Business Logic from `AttendanceController`

Three items live inside `AttendanceController.cs` that are not controller responsibilities:

**Item 1:** Three private nested classes — `AttendancePolicy`, `RawLog`, `ExportRow`

Create `Services/AttendanceReportService.cs`. Move all three nested classes into it as internal classes. Move `LoadAttendancePolicy()` and `BuildDailyRow()` as static methods.

The controller keeps `BuildCsv()` only if it is truly view-specific formatting. Otherwise move it to `AttendanceReportService` as well.

**Item 2:** `BuildDailyRow()` — the core daily attendance computation

This method is 80+ lines computing late minutes, undertime, status labels, and status badge classes. It is business logic. Move it to `AttendanceReportService.BuildDailyRow()`.

**Item 3:** `LoadAttendancePolicy()` — reads configuration for attendance rules

Move to `AttendanceReportService.LoadPolicy()`.

The controller's actions (`Index`, `SummaryReport`, `ExportSummaryCsv`) become thin: they build a query, call `AttendanceReportService`, and map results to the ViewModel.

### 4E — Decompose `AttendanceScanService.Scan()` into Private Methods

The `Scan()` method is approximately 400 lines with a single exit path. Break it into named private methods that correspond to the logical pipeline steps:

```csharp
private ActionResult ValidateImage(HttpPostedFileBase image, int maxBytes)
private (Office office, bool locationVerified, int requiredAcc) ResolveOffice(...)
private string SaveAndPreprocess(HttpPostedFileBase image, out string processedPath)
private bool DetectFace(string processedPath, DlibBiometrics.FaceBox clientBox, ...)
private (float score, bool passed) RunLiveness(Bitmap bitmap, DlibBiometrics.FaceBox box, ...)
private double[] RunEncoding(...)
private FastFaceMatcher.MatchResult MatchFace(double[] vec, double tolerance)
private bool CheckDeviceForMobile(Employee emp, ...)
private (bool needsReview, string notes) BuildReviewFlags(...)
```

`Scan()` becomes an orchestrator that calls these in sequence, checking `IsTimedOut(sw)` between each step.

**Commit message:** `refactor: extract business logic from controllers into services`

---

## Phase 5 — Split `AdminAuthorizeAttribute`

`AdminAuthorizeAttribute.cs` is 650+ lines with four completely separate responsibilities. A developer who needs to adjust the PIN lockout should not have to read session and cookie management code.

### Target Structure

**Keep in `Filters/AdminAuthorizeAttribute.cs`:**
- Only `AuthorizeCore()` and `HandleUnauthorizedRequest()`
- These call the three services below

**Create `Services/Security/AdminSessionService.cs`:**
```
Methods: MarkAuthed, ClearAuthed, RefreshSession, GetRemainingSessionSeconds,
         RotateSessionId, GetAdminId
```

**Create `Services/Security/AdminPinService.cs`:**
```
Methods: VerifyPin, HashPin (public static for tooling)
Private: TryVerifyPbkdf2, ConstantTimeEquals, Sha256Base64, Sha256Hex
State:   _lockouts ConcurrentDictionary, LockoutEntry nested class
```

**Create `Services/Security/AdminUnlockCookieService.cs`:**
```
Methods: IssueUnlockCookie, TryConsumeUnlockCookie, ExpireUnlockCookie
```

### Updated `AuthorizeCore`

```csharp
protected override bool AuthorizeCore(HttpContextBase httpContext)
{
    var ip = StringHelper.NormalizeIp(httpContext.Request?.UserHostAddress);
    if (!AdminAccessControl.IsAllowed(ip)) return false;

    var authedUtc = httpContext.Session?[AdminSessionService.SessionKey] as DateTime?;
    if (authedUtc == null)
        return AdminUnlockCookieService.TryConsume(httpContext, ip);

    var minutes = ConfigurationService.GetInt("Admin:SessionMinutes", 30);
    return (DateTime.UtcNow - authedUtc.Value) <= TimeSpan.FromMinutes(minutes);
}
```

Steps:
1. Create the three service files
2. Move methods into each service
3. Update `AdminAuthorizeAttribute` to call services
4. Update all callers of `AdminAuthorizeAttribute.MarkAuthed()`, `AdminAuthorizeAttribute.VerifyPin()`, etc. to call the service directly
5. Build

**Commit message:** `refactor: split AdminAuthorizeAttribute into three focused security services`

---

## Phase 6 — AudioManager Extraction

`AudioManager.cs` generates a JavaScript string in C# using string interpolation. The JavaScript is untestable, unformattable, and invisible to the JS linter.

### What to Do

**Create `Scripts/audio-manager.js`:**

Extract the entire JavaScript object `window.FaceAttendAudio` from the C# string literal into a proper `.js` file. The paths (`/Content/audio/success.mp3`, `/Content/audio/notif.mp3`) become configurable via `data-*` attributes on the `<body>` tag.

```javascript
(function () {
    'use strict';
    var body = document.body;
    window.FaceAttendAudio = {
        paths: {
            success: body.getAttribute('data-audio-success') || '/Content/audio/success.mp3',
            notif:   body.getAttribute('data-audio-notif')   || '/Content/audio/notif.mp3'
        },
        // ... rest of the object
    };
    // Auto-init
    if (document.readyState === 'loading')
        document.addEventListener('DOMContentLoaded', function () { FaceAttendAudio.init(); });
    else
        FaceAttendAudio.init();
})();
```

**Reduce `AudioManager.cs` to path constants only:**

```csharp
public static class AudioManager
{
    public const string SuccessSoundVirtualPath = "~/Content/audio/success.mp3";
    public const string NotificationSoundVirtualPath = "~/Content/audio/notif.mp3";
}
```

**In the layout that currently calls `AudioManager.GetAudioManagerScript(context)`:**

Replace with a `<script src="...audio-manager.js">` tag and two `data-audio-*` attributes on `<body>`.

**Register `audio-manager.js` in `BundleConfig.cs`** in the appropriate kiosk bundle.

Steps:
1. Create `Scripts/audio-manager.js` with the extracted JS
2. Add `data-audio-success` and `data-audio-notif` attributes to kiosk layout body tag
3. Reduce `AudioManager.cs` to constants
4. Remove the `GetAudioManagerScript()` and `PlaySuccessScript()` calls from layout
5. Add script tag for `audio-manager.js` in kiosk layout
6. Build and test audio plays on attendance success

**Commit message:** `refactor: extract AudioManager JS out of C# string literal`

---

## Phase 7 — Model and ViewModel Cleanup

### 7A — Split `AttendanceIndexVm.cs`

The file has six classes. Two of them (`EmployeeSummaryRow`, `DailyEmployeeRow`) are only used in `SummaryReport`. The other four are used by the main `Index` view.

**Create `Models/ViewModels/Admin/AttendanceSummaryVm.cs`:**
Move `EmployeeSummaryRow` and `DailyEmployeeRow` into it.

**Keep in `AttendanceIndexVm.cs`:**
`DailySummaryRow`, `OfficeSummaryRow`, `AttendanceRowVm`, `AttendanceIndexVm`.

Update the `using` in `SummaryReport.cshtml` and `AttendanceController.cs`.

### 7B — Rename `EmployeeListRowVm` to `EmployeeListRowDto`

The file is in `Models/Dtos/` — the name should reflect what it is.

Steps:
1. Rename the class from `EmployeeListRowVm` to `EmployeeListRowDto`
2. Rename the file to `EmployeeListRowDto.cs`
3. Search for all usages and update the type name
4. Build

### 7C — Remove Validation Logic from `MobileEnrollmentFormViewModels.cs`

`NewEmployeeEnrollmentVm.Validate()` and `NewEmployeeEnrollmentVm.Sanitize()` are 70+ lines of business logic inside a ViewModel. ViewModels should carry display data, not validation rules.

**What to do:**

Replace `Validate()` with `[DataAnnotation]` attributes on each property:

```csharp
[Required, StringLength(20, MinimumLength = 5)]
[RegularExpression(@"^[A-Z0-9 \-]+$", ErrorMessage = "Employee ID contains invalid characters.")]
public string EmployeeId { get; set; }
```

Apply the same for `FirstName`, `LastName`, `MiddleName`, `Position`, `Department`, `DeviceName`.

The server controller that receives the POST should call `if (!ModelState.IsValid)` and return the error — which is standard ASP.NET MVC.

Remove the `Validate()` method and the `Sanitize()` method from the ViewModel.

Move sanitization (the XSS stripping regex) into the controller action as a one-line call to `StringHelper.TruncateAndTrim()` before saving to the database. The regex-based HTML stripping is redundant when `[RegularExpression]` already restricts characters to a safe set.

**Commit message:** `refactor: split attendance VMs, rename EmployeeListRowDto, remove logic from ViewModels`

---

## Phase 8 — CSS Cleanup

### 8A — Fix `wizard.css` Duplication

`Content/_unified/components/wizard.css` re-declares every `.fa-wizard*` rule that already exists in `fa-design-system.css` Section 4.

Steps:
1. Open `wizard.css`
2. Remove every rule that is already defined in `fa-design-system.css` — the base `.fa-wizard`, `.fa-wizard__step`, `.fa-wizard__step.is-active`, `.fa-wizard__step.is-done`, `.fa-wizard__number`, `.fa-wizard__label`, `.fa-wizard__divider`, `.fa-wizard__divider.is-done`, `@keyframes wizard-step-active`
3. Keep only variant rules that are genuinely additive: `.fa-wizard--sm`, `.fa-wizard--lg`, `.fa-wizard--minimal`, and the responsive breakpoint
4. Test enrollment page visually

### 8B — Extract Bootstrap Compat from `kiosk.css`

`kiosk.css` contains approximately 350 lines of Bootstrap compatibility utilities at the bottom of the file. These are component classes (`.btn`, `.form-control`, `.table`, `.modal`, `.nav-tabs`, etc.) that were added to make admin-area HTML render correctly on the kiosk page. They have no relation to kiosk camera or HUD styles.

Steps:
1. Create `Content/kiosk-bootstrap-compat.css`
2. Move all Bootstrap compatibility rules from `kiosk.css` into the new file
3. The boundary: anything named `.btn`, `.form-control`, `.table`, `.badge`, `.alert`, `.card`, `.modal`, `.nav-tabs`, `.tab-content`, `.input-group`, `.col-*`, `.row`, `.container`, `.form-check`, `.close` belongs in the compat file
4. Update the kiosk layout bundle to include `kiosk-bootstrap-compat.css`
5. Visual test: kiosk UI, unlock modal, visitor modal

### 8C — Fix `enrollment.css` Method Selector Overrides

`enrollment.css` has a section commented `"Fixes for alignment and consistency"` that patches `.fa-method-card` with `justify-content: flex-start` and `padding-top: var(--space-8)`. These patches exist because the base rules in `method-selector.css` produce misalignment.

Fix the source, not the patch:
1. Open `Content/_unified/components/method-selector.css`
2. Apply the correct `padding` and `align-items` directly to `.fa-method-card`
3. Remove the override section from `enrollment.css`
4. Test method selector cards visually on the enrollment page

**Commit message:** `style: fix wizard.css duplication, extract kiosk bootstrap compat, fix method-selector source`

---

## Phase 9 — Admin JavaScript Separation

`Scripts/admin.js` contains fourteen unrelated responsibilities in one file. This is not about rewriting it — it is about separating concerns so each can be found and modified independently.

### Current Responsibilities

| Section | Lines (approx) | Description |
|---|---|---|
| `toast()` | 1–60 | Notification wrapper around toastr |
| `confirmDialog()` | 60–110 | Modal wrapper around Swal |
| `initFooterYear()` | 115–120 | Sets current year in footer |
| `initTooltips()` | 123–130 | Bootstrap tooltip init |
| `initAutoDismissAlerts()` | 133–145 | Auto-hides success alerts |
| `initServerToast()` | 148–160 | Reads `__toastMsg` from server TempData |
| `initConfirmLinks()` | 163–200 | Intercepts `[data-confirm]` clicks |
| `initDataTables()` | 205–340 | DataTables initialization with responsive fix |
| `initIdleOverlay()` | 345–375 | Idle detection overlay |
| `initOfficeMap()` | 380–445 | Leaflet map on office create/edit pages |
| `initBackToTop()` | 450–490 | Back-to-top scroll button |
| `initKeyboardShortcuts()` | 495–515 | Ctrl+/ focus search, Escape dismiss |
| `initRefreshButton()` | 520–530 | Topbar refresh button |
| `initSidebarActiveState()` | 535–555 | Marks active nav link |

### The Notification Overlap Problem

`admin.js` exposes `window.ui.toast`, `window.ui.toastSuccess`, etc. on `window.ui`.
`Scripts/core/notify.js` also exposes `window.ui.toast`, `window.ui.toastSuccess`, etc. on `window.ui`.

Both are loaded on admin pages. `notify.js` assigns to `window.ui` first, then `admin.js` overwrites it. The admin toast uses `toastr`, the notify toast uses `Swal`. The last one loaded wins.

**Resolution:** Remove the `window.ui` assignments from `admin.js`. `notify.js` is the canonical `window.ui` source. Admin pages load `notify.js` first. The `toast()` function inside `admin.js` becomes a local function used only by `initServerToast()` internally.

### What to Do

**Option A (Minimum Change):** Split by moving sections into separate files.

Create the following files, each containing only the section it describes:

| New File | Contains |
|---|---|
| `Scripts/admin/admin-datatable.js` | `initDataTables()` only — largest and most complex section |
| `Scripts/admin/admin-map.js` | `initOfficeMap()` only — Leaflet dependency |

Keep everything else in `admin.js` but remove the duplicate `window.ui` assignments.

**Why only two new files:** The other sections (footer year, tooltips, alerts, confirm links, idle, back-to-top, keyboard, refresh, sidebar) are each 10–20 lines. Splitting each into its own file would create 10 tiny files with more overhead than value. They are small enough to read sequentially.

Steps:
1. Remove `window.ui.toast`, `window.ui.toastSuccess`, `window.ui.toastInfo`, `window.ui.toastWarning`, `window.ui.toastError`, `window.ui.confirm` from `admin.js` bottom section — `notify.js` provides these
2. Keep `window.ui.initDataTables = initDataTables` — this is used by page scripts to re-init after AJAX
3. Create `Scripts/admin/admin-datatable.js` and move `initDataTables()` into it
4. Create `Scripts/admin/admin-map.js` and move `initOfficeMap()` into it
5. Register both new files in the admin bundle in `BundleConfig.cs`
6. Remove `initDataTables` and `initOfficeMap` calls from `admin.js` `onReady` block
7. Each new file adds its own `document.addEventListener('DOMContentLoaded', ...)` initialization
8. Build and test: DataTables on employee list, Leaflet map on office create/edit

**Commit message:** `refactor: split admin.js DataTables and map into separate files, remove duplicate window.ui assignments`

---

## Final File Manifest After All Phases

### Files Deleted

| File | Reason |
|---|---|
| `Services/Interfaces/IConfigurationService.cs` | Static class cannot implement interface — dead |
| `Services/Interfaces/IAttendanceService.cs` | Never registered in DI — dead contract |
| `Services/EnrollmentAdaptiveService.cs` | Only caller deleted in Plan 2 — unused |
| `Services/Biometrics/EnrollCandidate.cs` | Moved to `Models/Dtos/` |

### Files Created

| File | Contains |
|---|---|
| `Infrastructure/ControllerExtensions.cs` | `AddDbValidationErrors()` extension |
| `Services/AttendanceReportService.cs` | `LoadPolicy()`, `BuildDailyRow()`, `BuildCsv()`, nested DTO classes |
| `Services/Security/AdminSessionService.cs` | Session read/write, rotation |
| `Services/Security/AdminPinService.cs` | PIN verification, lockout |
| `Services/Security/AdminUnlockCookieService.cs` | Unlock cookie lifecycle |
| `Scripts/audio-manager.js` | `window.FaceAttendAudio` object |
| `Scripts/admin/admin-datatable.js` | `initDataTables()` |
| `Scripts/admin/admin-map.js` | `initOfficeMap()` |
| `Content/kiosk-bootstrap-compat.css` | Bootstrap compat rules extracted from `kiosk.css` |
| `Models/ViewModels/Admin/AttendanceSummaryVm.cs` | `EmployeeSummaryRow`, `DailyEmployeeRow` |

### Files Renamed or Moved

| From | To | Reason |
|---|---|---|
| `Models/Dtos/EmployeeListRowVm.cs` | `Models/Dtos/EmployeeListRowDto.cs` | Correct naming |
| `Services/Biometrics/EnrollCandidate.cs` | `Models/Dtos/EnrollCandidate.cs` | Plain DTO, wrong folder |

### Files Significantly Changed

| File | What Changed |
|---|---|
| `Filters/AdminAuthorizeAttribute.cs` | Authorization logic only — calls three new security services |
| `Scripts/admin.js` | Removed `initDataTables`, `initOfficeMap`, duplicate `window.ui` assignments |
| `Services/Helpers/StringHelper.cs` | `StringExtensions` class deleted |
| `Services/Helpers/ValidationHelper.cs` | GPS methods moved to `OfficeLocationService` |
| `Services/AudioManager.cs` | Reduced to path constants only |
| `Services/Biometrics/FastFaceMatcher.cs` | Added `InvalidateAndReload()` method |
| `Services/Biometrics/EnrollmentQualityGate.cs` | Added `SelectDiverseByEmbedding()` moved from controller |
| `Services/Recognition/AttendanceScanService.cs` | `Scan()` decomposed into private step methods |
| `Controllers/Api/EnrollmentController.cs` | `SelectDiverseByEmbedding()` removed, calls `EnrollmentQualityGate` |
| `Areas/Admin/Controllers/AttendanceController.cs` | Business logic removed, calls `AttendanceReportService` |
| `Areas/Admin/Controllers/VisitorsController.cs` | `Deactivate` renamed to `Delete`, dead nested class removed |
| `Areas/Admin/Controllers/OfficesController.cs` | `ValidateOfficeType()` extracted, used in both actions |
| `Areas/Admin/Controllers/EmployeesController.cs` | Double `DbEntityValidationException` catch removed, uses extension |
| `Models/ViewModels/Admin/AttendanceIndexVm.cs` | Summary classes moved to `AttendanceSummaryVm.cs` |
| `Models/ViewModels/Mobile/MobileEnrollmentFormViewModels.cs` | `Validate()` and `Sanitize()` removed, DataAnnotations added |
| `Content/_unified/components/wizard.css` | Duplicate design system rules removed |
| `Content/kiosk.css` | Bootstrap compat section removed |
| `Content/enrollment.css` | Method selector patch section removed |

---

## Execution Order Summary

| Phase | What | Commit Message | Risk |
|---|---|---|---|
| 1 | Comment purge | `chore: remove noise comments` | Zero |
| 2 | Delete dead code | `chore: delete dead code` | Low |
| 3 | Extract repeated patterns | `refactor: extract repeated patterns` | Low |
| 4 | Service decomposition | `refactor: extract business logic from controllers` | Medium |
| 5 | Split AdminAuthorizeAttribute | `refactor: split admin auth into security services` | Medium |
| 6 | AudioManager extraction | `refactor: extract AudioManager JS from C#` | Low |
| 7 | Model and ViewModel cleanup | `refactor: clean models and viewmodels` | Low |
| 8 | CSS cleanup | `style: CSS deduplication and separation` | Low |
| 9 | Admin JS separation | `refactor: split admin.js datatable and map` | Low |

**Rule:** One phase per commit. Build after every phase. Never skip a build.

---

## What This Plan Does Not Fix

These are real problems that are out of scope. They need their own plans.

- **`SettingsViewModelBuilder.cs` length.** After Phase 3E it will be cleaner but still 250+ lines. A settings-section split is future work.
- **`kiosk.js` architecture.** It is 3500+ lines. Phase 8 extracts the Bootstrap compat CSS it once needed. The JS architecture itself is not touched here — Plan 2 defines the JS pipeline boundary.
- **`VisitorFaceIndex.cs` vs `EmployeeFaceIndex.cs` loading duplication.** Both use `FaceIndexBase<T>` but the encoding loading path has its own duplication. Biometric internals are stable and out of scope.
- **Database access pattern.** Direct `db.` calls in controllers are acceptable at this project's scale. A repository layer would be over-engineering.
- **Unit test coverage.** This plan reorganizes — it does not add tests. Tests are a separate plan.
- **`OnnxLiveness.cs` size.** The file is large but focused on one thing. It is not touched here.
