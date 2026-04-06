# FaceAttend — Plan 6: View Layer, Dead Code Removal, Final Normalization
## Closing Every Open Item Left by Plans 1–5

**Status:** Final — aligned with Plans 1–5  
**Rule:** One phase per commit. Build after every phase. Never combine phases.  
**Goal:** After this plan, the codebase has no dead files, no inline page scripts over 50 lines, no Style 2 ConfigurationService calls, no stale comments, and every Razor view has exactly the ViewModel it needs.

---

## What This Plan Does NOT Touch

- Biometric engine internals: `DlibBiometrics`, `OnnxLiveness`, `FastScanPipeline`, `BallTreeIndex` — stable, focused, out of scope.
- `SettingsVm.cs` property count — 60+ properties is the correct shape for a settings form. Splitting it makes it harder to maintain, not easier.
- `OnnxLiveness.ScoreFromFile()` and `ScoreFromBitmap()` length — these are long because they do one well-defined thing. Length is not complexity here.
- Database access pattern — no repository layer. Direct `db.` in controllers is acceptable at this project's scale.
- Unit test coverage — separate plan.
- `fa-design-system.css` design tokens — single source of truth, untouched.
- Any kiosk.js module files created by Plan 4 — those are owned by Plan 4.
- Any enrollment JS files created by Plan 2 — owned by Plan 2.

---

## What Plans 1–5 Left Open

| Left Open By | Item |
|---|---|
| Plan 1 | Dead interface files identified but not deleted |
| Plan 1 | Dead service files identified but not deleted |
| Plan 1 | Comment policy defined but not applied |
| Plan 1 | Explicit MobileRegistration routes in RouteConfig — noted as removable after split |
| Plan 3 | `AudioManager.cs` identified for deletion, replacement JS file not created |
| Plan 3 | Views never addressed — inline scripts, ViewBag overuse remain |
| Plan 5 | ConfigurationService Style 2 remaining call sites after Settings helpers cleaned |
| Plan 5 | `VisitorsController.EnrollCandidate` nested dead class — identified, not deleted |

---

## Full Problem Inventory

### A — Dead Files

These files were identified across Plans 1–5 as unused or superseded. None have been deleted yet.

| File | Why Dead | Replacement |
|---|---|---|
| `Services/Interfaces/IConfigurationService.cs` | Never registered with DI; never injected anywhere | None needed |
| `Services/Interfaces/IAttendanceService.cs` | Never registered with DI; `new AttendanceService(db)` is used directly everywhere | None needed |
| `Services/EnrollmentAdaptiveService.cs` | Not referenced after enrollment refactor (Plan 2). `TryAddVector()` was the only method; its caller was removed | None needed |
| `Scripts/core/facescan.js` | Plan 2 identified this as a third enrollment engine that runs on pages using enrollment-core.js, doing the same job differently. Plan 2 removes it from the enrollment bundle. | `enrollment-core.js` handles all scanning |
| `Scripts/enrollment-ui.js` | The file guards on `document.getElementById('enrollRoot')` at line 1. Neither `Enroll.cshtml` nor `Enroll-mobile.cshtml` has that element. The file executes zero code on any page. | Dead |
| `Services/AudioManager.cs` | Generates a 100-line JavaScript string inside a C# string literal. Plan 3 creates a proper `Scripts/audio-manager.js` replacement. | `Scripts/audio-manager.js` (Plan 3) |

### B — ConfigurationService Style 2 Remaining Call Sites

Plan 5 cleaned the Settings pipeline (the largest Style 2 consumer). The following call sites remain and must be migrated to Style 1.

**Style 2 (legacy, bypasses cache):**
```csharp
ConfigurationService.GetInt(db, "Attendance:MinGapSeconds", 180)
```

**Style 1 (correct, uses cache):**
```csharp
ConfigurationService.GetInt("Attendance:MinGapSeconds", 180)
```

Remaining Style 2 call sites by file:

| File | Key Called With `db` Argument | Action |
|---|---|---|
| `AttendanceService.cs` | `Attendance:MinGapSeconds`, `Attendance:MinGap:InToOutSeconds`, `Attendance:MinGap:OutToInSeconds` | Replace with Style 1 |
| `AttendanceScanService.cs` | `Biometrics:AttendanceTolerance`, `Biometrics:MobileAttendanceTolerance`, `Kiosk:VisitorEnabled`, `Attendance:MinGapSeconds` | Replace with Style 1 |
| `EmployeesController.cs` | `Biometrics:LivenessThreshold` in `Enroll()` action | Replace with Style 1 |
| `VisitorsController.cs` | `Biometrics:LivenessThreshold` in `Edit()` action | Replace with Style 1 |
| `HealthController.cs` | `Biometrics:DlibModelsDir`, `Biometrics:LivenessModelPath` in diagnostics | Replace with Style 1 |
| `AttendanceController.cs` | Multiple keys in `LoadAttendancePolicy()` | Replace with Style 1 — this method already reads from `db` passed in; after replacement, the `db` parameter becomes unused in `LoadAttendancePolicy()` |

**The `db` parameter in `LoadAttendancePolicy()`:**

After replacing all Style 2 calls in this method, its signature becomes:

```csharp
// Before
private static AttendancePolicy LoadAttendancePolicy(FaceAttendDBEntities db)

// After
private static AttendancePolicy LoadAttendancePolicy()
```

Update both call sites in the same controller.

**Justification for Style 1 correctness here:**  
These configuration values (`Attendance:MinGapSeconds`, `Biometrics:LivenessThreshold`) are set once by an admin and cached. They do not need a fresh DB read on every scan. The cache TTL is 60 seconds. If an admin changes a value, it takes effect within 60 seconds — which is the correct behavior for operational settings.

### C — View Layer Problems

The Razor views have never been addressed by any plan. The specific problems are:

#### C1 — Massive Inline Script Blocks

| View | Inline Script Size (est.) | Problem |
|---|---|---|
| `Areas/Admin/Views/Employees/Enroll.cshtml` | ~300 lines of `<script>` | All page logic lives inline. Cannot be linted, cached, or reused. |
| `Views/MobileRegistration/Enroll-mobile.cshtml` | ~250 lines of `<script>` | Same problem. Two `(function(){...})()` blocks in `@section scripts`. |
| `Areas/Admin/Views/Employees/Index.cshtml` | ~40 lines of jQuery approval AJAX | Acceptable threshold; no action required. |

The 300-line inline block in `Enroll.cshtml` and the 250-line block in `Enroll-mobile.cshtml` each contain:
- `enrollment-core.js` callback wiring
- Wizard step navigation logic
- Camera start/stop lifecycle
- Face overlay rendering loop
- Upload pane file handling
- Form validation (mobile)
- Submit handlers

This code is not accessible to the script bundler, cannot be cached by the browser separately, and cannot be reused across pages.

#### C2 — ViewBag Used for Typed Data

ViewBag is appropriate for incidental display data (page title, flash messages). It is not appropriate for structured data that the view must operate on.

| View | ViewBag Key | Type Leak | Fix |
|---|---|---|---|
| `Enroll.cshtml` | `ViewBag.PerFrame` | `double` formatted as string | Add `LivenessThreshold` to a proper `EnrollViewModel` |
| `Enroll.cshtml` | `ViewBag.OfficeName` | `string` | Add `OfficeName` to `EnrollViewModel` |
| `Visitors/Edit.cshtml` | `ViewBag.PerFrame` | `double` formatted as string | Add `LivenessThreshold` to `VisitorEditViewModel` |
| `SettingsController.Index` | `ViewBag.FaceCacheStats` | `object.ToString()` | Add `FaceCacheStats` to `SettingsVm` |
| `SettingsController.Index` | `ViewBag.LivenessThreshold` | `double` | Add to `SettingsVm` |
| `SettingsController.Index` | `ViewBag.LivenessModelPath` | `string` | Add to `SettingsVm` |
| `SettingsController.Index` | `ViewBag.LivenessModelExists` | `bool` | Add to `SettingsVm` |

#### C3 — Partial Views With Layout = null Comments

`Areas/Admin/Views/Employees/_EmployeeForm.cshtml` has:
```razor
@{
    // Partial view: Layout must be null to prevent nested layout rendering
    Layout = null;
}
```

`Layout = null` is the default for partial views rendered with `Html.Partial()`. The comment and the explicit assignment are both unnecessary.

#### C4 — Enroll.cshtml ViewBag Offset Render

`Enroll.cshtml` opens a `using (Html.BeginForm(...))` block with `id = "hiddenForm"` and `@class = "d-none"` purely to render an `AntiForgeryToken`. The form itself is hidden — it exists only to produce the token input. This pattern is used because `@Html.AntiForgeryToken()` without a form context is valid in MVC but not immediately obvious. The pattern is fine architecturally but the comment `@* CSRF token — read by enrollment-core.js getCsrfToken() *@` is more useful than the form wrapper. Replace the hidden form with a direct `@Html.AntiForgeryToken()` call and keep the comment.

### D — Comment Policy Execution

Plan 1 defined the comment policy. No plan has applied it. These categories of comments must be removed:

**Remove: Phase/ticket references to completed work**
```csharp
// PHASE 2 FIX (P-03, WC-07): Dinagdagan ang circuit breaker status fields
// PHASE 1 FIX (S-09): In-enable na ang RequireHttpsAttribute.
// FIX-004: Use null (not "-") when Position is blank.
// FIX: Ensure face matcher is fully reloaded after any employee edit
// OPTIMIZATION NEEDED
```

**Remove: Comments that restate the code**
```csharp
// Check for null
if (x == null) return;

// Get CSRF token from page
var input = document.querySelector('input[name="__RequestVerificationToken"]');

// Stop any existing stream
this.stop();
```

**Remove: Language-switched blocks that describe completed changes**
```csharp
/// SAGUPA: Mabilis na health/readiness probe para sa IIS at load balancers.
/// PAGLALARAWAN (Description):
///   Nagche-check ng system health nang mabilis para malaman ng:
```
Replace with clean English XML doc summary.

**Remove: Usage documentation in C# for non-existent JS APIs**
```csharp
// Usage:
//   <div class="fa-modal">
//     <div class="fa-modal__backdrop"></div>
```
This is CSS documentation inside a CSS file header. It is redundant with what the CSS itself shows.

**Keep: WHY comments, security notes, non-obvious decisions**
```csharp
// DPAPI LocalMachine: machine-bound, no separate key file to manage.
// Migrating servers requires decrypt + re-encrypt while old machine is accessible.

// RepeatableRead holds a shared read lock on rows read until commit.
// Two concurrent scans for the same employee cannot both read before either writes.
```

**Scope of comment cleanup:** Apply to all files touched in previous plan phases. Do not open clean files solely to edit comments — that generates noise in the diff.

### E — Route and Namespace Normalization

#### E1 — Explicit MobileRegistration Routes

`RouteConfig.cs` has 12 explicit `routes.MapRoute()` calls for `MobileRegistration/*` paths. These were added because the controller was in the root `Controllers/` namespace and route resolution was ambiguous.

After Plan 1 Phase E splits `MobileRegistrationController` into:
- `Controllers/Mobile/MobileEnrollmentController.cs` with `[RoutePrefix("MobileRegistration")]`
- `Controllers/Mobile/MobilePortalController.cs` with `[RoutePrefix("MobileRegistration")]`

...the 12 explicit routes are no longer needed. `routes.MapMvcAttributeRoutes()` handles them. Remove all 12 and move the `MapMvcAttributeRoutes()` call before the default route.

**Before:**
```csharp
routes.MapRoute("MobileRegistration_Index", "MobileRegistration", ...);
routes.MapRoute("MobileRegistration_Identify", "MobileRegistration/Identify", ...);
// ... 10 more
routes.MapMvcAttributeRoutes();
routes.MapRoute("Default", "{controller}/{action}/{id}", ...);
```

**After:**
```csharp
routes.MapMvcAttributeRoutes();
routes.MapRoute("Default", "{controller}/{action}/{id}", ...);
```

**Risk:** Only applies after Plan 1 Phase E is complete. Do not run Phase 6-E before Plan 1 Phase E.

#### E2 — Namespace Consistency

After Plan 1 moves files to new directories, the namespaces must match the directory structure:

| New File | Required Namespace |
|---|---|
| `Controllers/Mobile/MobileEnrollmentController.cs` | `FaceAttend.Controllers.Mobile` |
| `Controllers/Mobile/MobilePortalController.cs` | `FaceAttend.Controllers.Mobile` |
| `Areas/Admin/Controllers/DevicesController.cs` | `FaceAttend.Areas.Admin.Controllers` |
| `Services/Request/ClientDetectionService.cs` | `FaceAttend.Services.Request` |
| `Services/Request/DeviceTokenService.cs` | `FaceAttend.Services.Request` |
| `Services/EmployeeApprovalService.cs` | `FaceAttend.Services` |
| `Services/Recognition/AttendanceScanResponses.cs` | `FaceAttend.Services.Recognition` |
| `Services/Helpers/TimeHelper.cs` | `FaceAttend.Services.Helpers` |

Add `using FaceAttend.Services.Request;` to any controller that calls `ClientDetectionService` or `DeviceTokenService`. Add `using FaceAttend.Services.Recognition;` to `KioskController` if it calls `AttendanceScanResponses`.

---

## Phase Plan

### Phase 6-A — Dead File Deletion

Delete the six dead files in order. Build after each deletion to confirm no compile error.

1. Delete `Services/Interfaces/IConfigurationService.cs`  
   — No references. Direct deletion.

2. Delete `Services/Interfaces/IAttendanceService.cs`  
   — No references. Direct deletion.

3. Delete `Services/EnrollmentAdaptiveService.cs`  
   — No references after Plan 2. Direct deletion.

4. Delete `Scripts/enrollment-ui.js`  
   — No references after Plan 2 confirms it. Remove from any `@Scripts.Render` call if present.

5. Delete `Services/AudioManager.cs`  
   — Requires Plan 3 Phase 7 (audio-manager.js) to be complete first. Confirm `Scripts/audio-manager.js` exists. Remove all `AudioManager.*` calls from layout views. Replace with `<script src="@Url.Content("~/Scripts/audio-manager.js")"></script>`.

6. Delete `Scripts/core/facescan.js`  
   — Requires Plan 2 bundle cleanup to be complete first. Remove from `~/bundles/fa-core` in `BundleConfig.cs`.

**Commit per deletion.** Each is a separate commit.  
**Risk:** Zero after prerequisites from Plans 2 and 3 are verified.

---

### Phase 6-B — ConfigurationService Style 2 Final Cleanup

**Order matters.** Do `AttendanceService.cs` first — it is the most critical and has the most usage.

**Step 1:** `Services/AttendanceService.cs`

Replace:
```csharp
int minGapSeconds = ConfigurationService.GetInt(
    _db, "Attendance:MinGapSeconds",
    ConfigurationService.GetInt("Attendance:MinGapSeconds", 180));
```

With:
```csharp
int minGapSeconds = ConfigurationService.GetInt("Attendance:MinGapSeconds", 180);
```

Apply same pattern to `Attendance:MinGap:InToOutSeconds` and `Attendance:MinGap:OutToInSeconds`.

Remove the `_db` argument from these three calls only. The `_db` field is still used for `SaveChanges()` — do not remove it from the class.

**Step 2:** `Services/Recognition/AttendanceScanService.cs`

Replace all `ConfigurationService.GetX(db, "Key", ...)` calls inside `Scan()` where the value is an operational setting (thresholds, tolerances, mode flags). The `db` variable inside this method is the open EF context — removing it from config calls does not close the context.

Keys to migrate:
- `Biometrics:AttendanceTolerance`
- `Biometrics:MobileAttendanceTolerance`
- `Biometrics:SkipLiveness`
- `Kiosk:VisitorEnabled`
- `Biometrics:Debug`
- `NeedsReview:NearMatchRatio`
- `NeedsReview:LivenessMargin`
- `NeedsReview:GPSAccuracyMargin`

**Step 3:** `Areas/Admin/Controllers/EmployeesController.cs`

In the `Enroll(int id)` action:
```csharp
// Before
var perFrame = ConfigurationService.GetDouble(db, "Biometrics:LivenessThreshold", 0.75);

// After
var perFrame = ConfigurationService.GetDouble("Biometrics:LivenessThreshold", 0.75);
```

**Step 4:** `Areas/Admin/Controllers/AttendanceController.cs`

`LoadAttendancePolicy(FaceAttendDBEntities db)` calls Style 2 for every attendance policy key. After replacing:

```csharp
// Before
private static AttendancePolicy LoadAttendancePolicy(FaceAttendDBEntities db)
{
    var sStart = ConfigurationService.GetString(db, "Attendance:WorkStart", ...);
    ...
}

// After — db parameter removed
private static AttendancePolicy LoadAttendancePolicy()
{
    var sStart = ConfigurationService.GetString("Attendance:WorkStart", ...);
    ...
}
```

Update both call sites: `SummaryReport()` and `ExportSummaryCsv()` — remove `db` argument from `LoadAttendancePolicy(db)`.

**Step 5:** `Controllers/HealthController.cs`

`Diagnostics()` action reads model paths with Style 2. Replace with Style 1.

**Verification after Phase 6-B:**  
Search the entire solution for `ConfigurationService.Get\w+\(db,` — zero results expected.

**Commit:** `refactor: complete ConfigurationService Style 1 migration`  
**Risk:** Low. The cache correctly serves operational settings. The 60-second TTL is appropriate.

---

### Phase 6-C — View Layer: Extract Inline Scripts

#### Step 1 — `Areas/Admin/Views/Employees/Enroll.cshtml`

Extract the entire `(function () { ... })()` block from `@section scripts` into a new file:

`Scripts/admin/enroll-page.js`

The file receives its configuration from `data-*` attributes on `#enrollRoot` (already used by `enrollment-ui.js`) plus two new attributes on `body` or a config element:

```html
<div id="enrollPageConfig"
     data-employee-id="@Model.EmployeeId"
     data-employee-int-id="@Model.Id"
     data-scan-url="@Url.Content("~/api/scan/frame")"
     data-enroll-url="@Url.Content("~/api/enrollment/enroll")"
     data-redirect-url="@Url.Action("Index", "Employees", new { area = "Admin" })"
     data-min-frames="5">
</div>
```

The `enroll-page.js` file reads these attributes at the top of its IIFE instead of hardcoding them. The resulting `@section scripts` block in the view becomes:

```razor
@section scripts {
    @Scripts.Render("~/bundles/enrollment")
    @Scripts.Render("~/bundles/enroll-page")
}
```

No logic remains inline.

**What stays in the view:** The AntiForgeryToken call, the config element, and the `@section scripts` render calls. That is all.

#### Step 2 — `Views/MobileRegistration/Enroll-mobile.cshtml`

Same extraction pattern. Target file:

`Scripts/mobile/mobile-enroll-page.js`

Configuration element:
```html
<div id="mobileEnrollPageConfig"
     data-scan-url="/MobileRegistration/ScanFrame"
     data-submit-url="/MobileRegistration/SubmitEnrollment"
     data-success-url="/MobileRegistration/Success"
     data-min-frames="5">
</div>
```

The mobile-specific form validation rules (`Rules` object, `wire()` function, step navigation) all move into this file.

**What stays in the view:** Office `<option>` list rendered by Razor (requires server data), the form field markup, the AntiForgeryToken, and the `@section scripts` render calls. No `<script>` block over 5 lines.

#### Step 3 — Add New Bundles to BundleConfig.cs

```csharp
bundles.Add(new ScriptBundle("~/bundles/enroll-page")
    .Include("~/Scripts/admin/enroll-page.js"));

bundles.Add(new ScriptBundle("~/bundles/mobile-enroll-page")
    .Include("~/Scripts/mobile/mobile-enroll-page.js"));
```

**Risk:** Medium. The inline scripts read CSRF tokens, call server endpoints, and interact with enrollment-core callbacks. The extraction is mechanical but the data-attribute wiring must be tested end-to-end after extraction.

**Commit:** `refactor: extract Enroll.cshtml inline scripts to enroll-page.js`  
**Commit:** `refactor: extract Enroll-mobile.cshtml inline scripts to mobile-enroll-page.js`

---

### Phase 6-D — View Layer: ViewBag → ViewModel

#### Step 1 — Create `EnrollViewModel`

```csharp
// Models/ViewModels/Admin/EnrollViewModel.cs
namespace FaceAttend.Models.ViewModels.Admin
{
    public class EnrollViewModel
    {
        public Employee Employee       { get; set; }
        public string   OfficeName     { get; set; }
        public double   LivenessThreshold { get; set; }
    }
}
```

Update `EmployeesController.Enroll(int id)` to return `View(new EnrollViewModel { ... })` instead of `View(emp)` with ViewBag.

Update `Enroll.cshtml` to `@model EnrollViewModel` and replace `@ViewBag.OfficeName` and `@ViewBag.PerFrame` with `@Model.OfficeName` and `@Model.LivenessThreshold`.

#### Step 2 — Visitor Edit ViewModel

```csharp
// Models/ViewModels/Admin/VisitorEditViewModel.cs
namespace FaceAttend.Models.ViewModels.Admin
{
    public class VisitorEditViewModel
    {
        public Visitor Visitor          { get; set; }
        public double  LivenessThreshold { get; set; }
    }
}
```

Update `VisitorsController.Edit(int id)` and `Views/Admin/Visitors/Edit.cshtml`.

#### Step 3 — SettingsVm Diagnostics Fields

Add to existing `SettingsVm.cs` (do not create a new file — this is an additive change only):

```csharp
// ── Diagnostics (read-only, populated by controller) ───────────────────────
public string FaceCacheStats       { get; set; }
public double LivenessModelThreshold { get; set; }
public string LivenessModelPath    { get; set; }
public bool   LivenessModelExists  { get; set; }
```

Update `SettingsController.Index()` to populate these properties instead of setting ViewBag keys. Update `Views/Admin/Settings/Index.cshtml` to use `@Model.*` instead of `@ViewBag.*`.

**Risk:** Low. Purely additive to SettingsVm. Compiler catches every reference.

**Commit:** `refactor: EnrollViewModel and VisitorEditViewModel replace ViewBag`  
**Commit:** `refactor: SettingsVm gains diagnostics fields, removes ViewBag`

---

### Phase 6-E — Comment Policy Execution

Apply Plan 1 comment rules to all files modified by Plans 1–6. Do not open unmodified files solely to edit comments.

**Files to clean (by plan phase they were touched):**

| File | Comments to Remove |
|---|---|
| `DashboardViewModel.cs` | Tagalog inline comments, phase-reference comments (`PHASE 2 FIX`) |
| `EmployeesController.cs` | `FIX-004` comments, `FIX:` prefixed comments describing completed changes |
| `FilterConfig.cs` | The entire Tagalog explanation block about HTTPS. Replace with 2 lines. |
| `HealthProbe.cs` | Tagalog block comment (`SAGUPA`, `PAGLALARAWAN`, `ILOKANO`). Replace with clean English XML doc. |
| `AttendanceService.cs` | Tagalog inline comments, `SWITCHED TO LOCAL TIME` comment block |
| `BiometricCrypto.cs` | Tagalog explanation block. Keep: the DPAPI key rotation note. |
| `DeviceService.cs` | `PHASE 1:`, `PHASE 2:`, `PHASE 3:`, `PHASE 4:` section comments in `RegisterDevice()` |
| `AdminAuditLog` / `AuditHelper.cs` | Tagalog `// Audit write failed - silent` — replace with English |
| `kiosk.css` | Header comment `/* Uses fa-design-system.css for tokens (loaded before this file) */` — already implied by bundle order |

**Replacement template for SAGUPA blocks in C# services:**

```csharp
/// <summary>
/// Fast health/readiness probe for IIS and load balancers.
/// Checks database connectivity, model file presence, and warm-up state.
/// Returns a <see cref="HealthSnapshot"/> with all component statuses.
/// </summary>
```

**Commit:** `cleanup: apply comment policy from Plan 1 — remove phase tags and Tagalog blocks`  
**Risk:** Zero. No logic changes. Compiler output is identical.

---

### Phase 6-F — Route and Namespace Normalization

**Prerequisites:** Plan 1 Phase E (MobileRegistrationController split) must be complete.

**Step 1 — Remove Explicit MobileRegistration Routes**

In `App_Start/RouteConfig.cs`, delete the 12 explicit route registrations (lines covering `MobileRegistration_Index` through `MobileRegistration_Generic`).

Ensure `routes.MapMvcAttributeRoutes()` is the first call in `RegisterRoutes()`, before the Default route.

**Step 2 — Apply Correct Namespaces**

For each new file created by Plans 1–5, verify the namespace matches the directory path:

```csharp
// Controllers/Mobile/MobileEnrollmentController.cs
namespace FaceAttend.Controllers.Mobile { ... }

// Services/Request/ClientDetectionService.cs
namespace FaceAttend.Services.Request { ... }

// Services/Helpers/TimeHelper.cs
namespace FaceAttend.Services.Helpers { ... }
```

**Step 3 — Remove Unused `using` Statements**

After all moves and deletions, run a solution-wide unused-using cleanup (Visual Studio: Analyze → Code Cleanup, or Resharper: Clean Code). Do not commit individual files for this — run it once and commit as one cleanup pass.

**Commit:** `refactor: remove explicit MobileRegistration routes after attribute route split`  
**Commit:** `cleanup: normalize namespaces and remove unused using directives`  
**Risk:** Low. The attribute routes on the new controllers preserve every existing URL. Namespace changes are compiler-verified.

---

## Phase Summary

| Phase | What It Does | Prerequisites | Risk |
|---|---|---|---|
| **6-A** | Delete 6 dead files | Plans 2, 3 complete | Zero |
| **6-B** | Style 2 → Style 1 for remaining ConfigurationService callers | Plan 5 complete | Low |
| **6-C** | Extract ~550 lines of inline view scripts to JS files | Plans 2, 4 complete (bundle infrastructure) | Medium |
| **6-D** | Replace ViewBag with typed properties on ViewModels | None | Low |
| **6-E** | Apply comment policy across all modified files | Plans 1–5 phases complete | Zero |
| **6-F** | Remove explicit routes, normalize namespaces, remove unused usings | Plan 1 Phase E complete | Low |

---

## Final File Manifest Added by Plan 6

| New File | Purpose |
|---|---|
| `Scripts/admin/enroll-page.js` | Admin enrollment page script extracted from `Enroll.cshtml` |
| `Scripts/mobile/mobile-enroll-page.js` | Mobile enrollment page script extracted from `Enroll-mobile.cshtml` |
| `Models/ViewModels/Admin/EnrollViewModel.cs` | Typed model for the Enroll view |
| `Models/ViewModels/Admin/VisitorEditViewModel.cs` | Typed model for the Visitor Edit view |
| `Services/Helpers/TimeHelper.cs` | Shared `TryParseTime()` (if not already created in Plan 5-A) |

## Files Deleted by Plan 6

| Deleted File | Reason |
|---|---|
| `Services/Interfaces/IConfigurationService.cs` | Dead contract, never injected |
| `Services/Interfaces/IAttendanceService.cs` | Dead contract, never injected |
| `Services/EnrollmentAdaptiveService.cs` | Not referenced after Plan 2 |
| `Scripts/enrollment-ui.js` | Executes zero code on any page |
| `Scripts/core/facescan.js` | Superseded by enrollment-core.js |
| `Services/AudioManager.cs` | Superseded by Scripts/audio-manager.js |

---

## What This Plan Achieves

After Plan 6 is complete:

1. **No dead files.** Every file in the project is referenced by at least one other file.
2. **One ConfigurationService API.** `GetX(key, default)` everywhere. No `GetX(db, key, default)` call sites remain outside the `SettingsViewModelBuilder` legacy helper.
3. **No inline script blocks over 50 lines in any Razor view.** All page logic is in `.js` files served by the bundle pipeline, cached by the browser, and lintable.
4. **No ViewBag for structured data.** ViewBag carries only page title and flash messages.
5. **No stale phase-tag or Tagalog comments in service files.** WHY comments and security notes are preserved.
6. **Route table is clean.** Attribute routes on controllers are the single source of truth. `RouteConfig.cs` has two calls: `MapMvcAttributeRoutes()` and the default route.
7. **Namespaces match directory paths.** Any developer can find a class by reading its namespace.

---

## What Is Still Out of Scope (Separate Plans)

- **Unit test coverage** — separate plan, explicitly excluded from Plans 1–6.
- **Database schema changes** — separate migration plan.
- **Repository layer** — not justified at this scale.
- **`SettingsVm.cs` property count** — correct shape for a settings form. Not a problem.
- **`OnnxLiveness` method length** — one well-defined responsibility. Not a problem.
- **Security hardening** — separate on-site plan (IP restrictions, DPAPI entropy, admin exposure).
- **Authentication multi-admin** — out of scope for single-admin kiosk deployment.
