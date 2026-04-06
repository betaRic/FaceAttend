# FaceAttend — Plan 5: Settings Pipeline, Asset Pipeline, Error Handling
## The Infrastructure Concerns Deferred by Plans 1–4

**Status:** Final — aligned with Plans 1–4  
**Rule:** One phase per commit. Build after every phase. Never combine phases.

---

## What This Plan Covers

Plans 1–4 explicitly deferred these items. This plan addresses all of them:

| Deferred From | Item |
|---|---|
| Plan 3 | `SettingsViewModelBuilder.cs` still 250+ lines after Phase 3E |
| Plan 3 | `SettingsSaver.cs` still 350+ lines with 50 repeated `Upsert()` calls |
| Plan 1 | Error handling: empty `catch {}` blocks swallow exceptions silently |
| Plan 1 | `BundleConfig.cs` not addressed by any plan |
| Plan 3 | `SettingsValidator.cs` has duplicated `TryParseTime()` private method |
| Plan 1 | `VisitorFaceIndex.cs` vs `EmployeeFaceIndex.cs` encoding loading duplication |

---

## What This Plan Does NOT Touch

- Biometric engine internals: `DlibBiometrics`, `OnnxLiveness`, `FastScanPipeline` — stable
- Database schema or EF model — separate concern
- Any JavaScript files — owned by Plans 2 and 4
- Any CSS files — owned by Plan 3
- Unit test coverage — separate plan
- `kiosk.js` modules — owned by Plan 4

---

## Problem 1 — The Settings Pipeline

### Current State

The Settings system is a four-file pipeline:

```
SettingsController.cs          (GET Index, POST Save)
    └── SettingsViewModelBuilder.cs    (BuildVm, BuildSafeVm — 250+ lines)
    └── SettingsSaver.cs               (SaveSettings — 350+ lines)
    └── SettingsValidator.cs           (ValidateChoiceFields, ValidateRanges, etc.)
```

### Problem 1A — The Double-Call Pattern (50 occurrences)

Every single configuration read in `SettingsViewModelBuilder.cs` looks like this:

```csharp
var tol = ConfigurationService.GetDouble(
    db,
    "Biometrics:DlibTolerance",
    ConfigurationService.GetDouble(db, "DlibTolerance", tolFallback));
```

This is a legacy key migration guard — if the new key is missing, fall back to the old key, then fall back to the hardcoded default. The pattern appears ~50 times. It produces nested calls that are hard to read and makes the intent invisible.

The nested fallback also calls `ConfigurationService.GetDouble(db, ...)` twice — two DB reads for one value.

**What the pattern actually means in most cases:**

Most legacy keys (`DlibTolerance`, `DlibPoolSize`) were migrated years ago. The outer check for the old key is no longer needed. Only two keys have real migration guards that still matter.

### Problem 1B — SettingsSaver.cs Is One Method of 350 Lines

`SaveSettings()` calls `ConfigurationService.Upsert()` approximately 50 times, once per setting. The method is too long to scroll through to find any specific setting's save call.

### Problem 1C — `TryParseTime()` Is Duplicated

The private method `TryParseTime(string, out TimeSpan)` exists identically in both `SettingsViewModelBuilder.cs` and `SettingsValidator.cs`. It has the same three-format parse chain in both files.

---

## Problem 2 — BundleConfig.cs Is Never Mentioned

`BundleConfig.cs` registers every JavaScript and CSS bundle used in the application. None of the four previous plans touch it. After Plans 1–4 create new files (`Scripts/kiosk/kiosk-config.js`, `Scripts/admin/admin-datatable.js`, `Scripts/audio-manager.js`, etc.), BundleConfig must be updated or the new files are never served.

### Current Bundle Structure Issues

| Bundle Name | Problem |
|---|---|
| `~/bundles/fa-core` | No file list documented — it is unclear what `fa-core` contains |
| `~/bundles/enrollment` | Plan 2 adds/removes files; bundle not updated |
| `~/bundles/kiosk` | Plan 4 adds 13 new files; bundle must be updated |
| Admin bundles | Plan 3 Phase 9 adds 2 files; bundle must be updated |
| No shared bundle for `notify.js` + `fa-helpers.js` | Each layout includes them differently |

---

## Problem 3 — Silent Exception Swallowing

Across the codebase, exception handling follows this pattern:

```csharp
catch (Exception ex)
{
    // best effort
}
```

Or worse:

```csharp
catch { }
```

The kiosk requirement "never crash on scan" justifies catch-all patterns in the scan loop. But the same pattern is used in places where it hides real bugs:

| Location | Risk |
|---|---|
| `AuditHelper.Log()` — empty catch | An audit write failure is silently dropped. A compromised audit trail is not an audit trail. |
| `TempFileCleanupTask.CleanupOnce()` — per-file try/catch | OK — intentional, logs individual file errors |
| `ConfigurationService.GetFromDbCached()` | DB error drops a `TraceWarning` but returns null — callers get the hardcoded default silently |
| `BiometricCrypto.TryUnprotectString()` — outer `catch` | If DPAPI fails (e.g., machine key change after server migration), every attendance scan fails silently with wrong-employee matches |
| `FastScanPipeline.RunCore()` — parallel lambda `catch { return; }` | If liveness fails during parallel run, the frame is silently skipped. No log entry. Cannot distinguish "liveness skipped" from "liveness passed at 0.00" |
| `EmployeeFaceIndex.RebuildCore()` — `catch { _ballTree = null; }` | BallTree build failure produces linear scan fallback. No log. Performance degrades invisibly under load. |

---

## Problem 4 — VisitorFaceIndex and EmployeeFaceIndex Loading Duplication

`EmployeeFaceIndex.cs` and `VisitorFaceIndex.cs` both use `FaceIndexBase<T>`. The base class handles rebuild, BallTree construction, and linear search. This is good.

But `LoadEntriesFromDatabase()` in `EmployeeFaceIndex` is 60 lines that re-implements the same SQL-to-vector loading that `FaceEncodingHelper.LoadAllEmployeeFaces()` already does. The SQL query in `EmployeeFaceIndexImpl.TryLoadViaSql()` is a hand-written duplicate of the query in `FaceEncodingHelper`.

`VisitorFaceIndex.LoadEntriesFromDatabase()` is cleaner — it iterates over EF results and calls `BiometricCrypto.TryGetBytesFromStoredBase64()`. The employee version has a SQL path AND an EF fallback path, each implemented independently.

---

## Phase 5-A — Extract `TryParseTime` to Shared Location

**What moves:**

The `TryParseTime(string, out TimeSpan)` private method exists in both `SettingsViewModelBuilder.cs` and `SettingsValidator.cs`. Extract it into `Services/Helpers/TimeHelper.cs` as a public static method:

```csharp
public static class TimeHelper
{
    public static bool TryParseTime(string value, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(value)) return false;
        value = value.Trim();
        return
            TimeSpan.TryParseExact(value, @"hh\:mm",        CultureInfo.InvariantCulture, out result) ||
            TimeSpan.TryParseExact(value, @"hh\:mm\:ss",    CultureInfo.InvariantCulture, out result) ||
            TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out result);
    }
}
```

Remove both private copies. Update the two call sites.

**Risk:** Zero. Mechanical deduplication. Compiler verifies.

**Commit:** `refactor: extract TryParseTime to TimeHelper`

---

## Phase 5-B — Eliminate the Double-Call Pattern in SettingsViewModelBuilder

**What changes:**

Step 1: Audit the legacy key list. Identify which old keys still exist in any production database.

The two keys that still matter (migration incomplete in some deployments):
- `"DlibTolerance"` → migrated to `"Biometrics:DlibTolerance"`

All other nested fallbacks (`DlibPoolSize`, `MinGapSeconds` old keys, etc.) were migrated in version 1.0 and are safe to remove.

Step 2: Replace the nested pattern with a simple lookup:

```csharp
// Before
var tol = ConfigurationService.GetDouble(
    db,
    "Biometrics:DlibTolerance",
    ConfigurationService.GetDouble(db, "DlibTolerance", 0.60));

// After
var tol = ConfigurationService.GetDouble(db, "Biometrics:DlibTolerance", 0.60);
```

Step 3: For the one key that genuinely still needs a legacy fallback, extract a local helper:

```csharp
private static double GetWithLegacyFallback(FaceAttendDBEntities db,
    string newKey, string legacyKey, double defaultValue)
{
    var val = ConfigurationService.GetDouble(db, newKey, double.NaN);
    return double.IsNaN(val)
        ? ConfigurationService.GetDouble(db, legacyKey, defaultValue)
        : val;
}
```

Use this for the one key that still needs it. Use plain `GetDouble` for the other 49.

**Result:** `SettingsViewModelBuilder.cs` drops from ~250 lines to ~180 lines. Every setting read is one line.

**Risk:** Low. The legacy key values are already migrated. If a key is missing, the default is used — same as today.

**Commit:** `refactor: simplify SettingsViewModelBuilder config reads`

---

## Phase 5-C — Split SettingsSaver by Section

**Current state:** `SaveSettings()` is one method with 50 `Upsert()` calls. Sections are separated only by comments.

**What changes:**

Split into private section methods called from `SaveSettings()`:

```csharp
public static void SaveSettings(FaceAttendDBEntities db, SettingsVm vm,
    TimeSpan workStart, TimeSpan workEnd, TimeSpan lunchStart, TimeSpan lunchEnd,
    string by)
{
    SaveBiometricSettings(db, vm, by);
    SaveLivenessSettings(db, vm, by);
    SavePerformanceSettings(db, vm, by);
    SaveLocationSettings(db, vm, by);
    SaveAttendanceSettings(db, vm, workStart, workEnd, lunchStart, lunchEnd, by);
    SaveReviewQueueSettings(db, vm, by);
    SaveVisitorSettings(db, vm, by);
    CleanupLegacyKeys(db);
}

private static void SaveBiometricSettings(FaceAttendDBEntities db, SettingsVm vm, string by) { /* 8 Upsert calls */ }
private static void SaveLivenessSettings(FaceAttendDBEntities db, SettingsVm vm, string by)  { /* 12 Upsert calls */ }
private static void SavePerformanceSettings(FaceAttendDBEntities db, SettingsVm vm, string by) { /* 4 Upsert calls */ }
private static void SaveLocationSettings(FaceAttendDBEntities db, SettingsVm vm, string by)  { /* 3 Upsert calls */ }
private static void SaveAttendanceSettings(FaceAttendDBEntities db, SettingsVm vm, TimeSpan ws, TimeSpan we, TimeSpan ls, TimeSpan le, string by) { /* 14 Upsert calls */ }
private static void SaveReviewQueueSettings(FaceAttendDBEntities db, SettingsVm vm, string by) { /* 3 Upsert calls */ }
private static void SaveVisitorSettings(FaceAttendDBEntities db, SettingsVm vm, string by)   { /* 3 Upsert calls */ }
private static void CleanupLegacyKeys(FaceAttendDBEntities db)                               { /* 2 Delete calls */ }
```

**Result:** `SaveSettings()` becomes 10 lines. Finding where `"Biometrics:LivenessThreshold"` is saved means opening `SaveBiometricSettings` — the section name is the guide.

**Risk:** Low. Pure extraction — no logic changes. Each private method is called once.

**Commit:** `refactor: split SettingsSaver.SaveSettings into section methods`

---

## Phase 5-D — Simplify SettingsViewModelBuilder — BuildVm Sections

Apply the same section pattern to `BuildVm()`:

```csharp
public static SettingsVm BuildVm(FaceAttendDBEntities db)
{
    return new SettingsVm
    {
        // Biometrics
        DlibTolerance             = ReadDouble(db, "Biometrics:DlibTolerance",              0.60),
        LivenessThreshold         = ReadDouble(db, "Biometrics:LivenessThreshold",           0.75),
        AttendanceTolerance       = ReadDouble(db, "Biometrics:AttendanceTolerance",         0.50),
        // ... all other properties in order matching SettingsVm property order
        WarningMessage  = BuildWarningMessages(db),
        OfficeOptions   = AdminQueryHelper.BuildOfficeOptionsWithAuto(db, fallbackOfficeId)
    };
}
```

One line per property. No intermediate variables for values that are only used once. The property name and the config key are on the same line — visible at a glance.

The `BuildSafeVm()` method follows the same pattern.

**Risk:** Low. Pure reformatting. Compiler verifies no property is missed.

**Commit:** `refactor: simplify SettingsViewModelBuilder.BuildVm inline reads`

---

## Phase 5-E — Update BundleConfig.cs

After Plans 1–4 add and remove files, BundleConfig must reflect the new file locations.

### Changes Required After Plan 2 (Enrollment)

The enrollment bundle (`~/bundles/enrollment`) should include:
```csharp
new ScriptBundle("~/bundles/enrollment").Include(
    "~/Scripts/modules/face-guide.js",
    "~/Scripts/modules/enrollment-core.js",
    "~/Scripts/enrollment-tracker.js",
    "~/Scripts/enrollment-ui.js"
)
```

Any file removed by Plan 2 is removed from the bundle. Any new file added by Plan 2 is added.

### Changes Required After Plan 3 (Backend)

The admin bundle should include the two new files:
```csharp
// Add to admin scripts bundle
"~/Scripts/admin/admin-datatable.js",
"~/Scripts/admin/admin-map.js",
"~/Scripts/audio-manager.js"
```

### Changes Required After Plan 4 (kiosk.js)

The kiosk scripts bundle replaces the single `kiosk.js` with the module load order:
```csharp
new ScriptBundle("~/bundles/kiosk-scripts").Include(
    "~/Scripts/kiosk/kiosk-config.js",
    "~/Scripts/kiosk/kiosk-state.js",
    "~/Scripts/kiosk/kiosk-clock.js",
    "~/Scripts/kiosk/kiosk-warmup.js",
    "~/Scripts/kiosk/kiosk-fullscreen.js",
    "~/Scripts/kiosk/kiosk-unlock.js",
    "~/Scripts/kiosk/kiosk-visitor.js",
    "~/Scripts/kiosk/kiosk-device.js",
    "~/Scripts/kiosk/kiosk-location.js",
    "~/Scripts/kiosk/kiosk-map.js",
    "~/Scripts/kiosk/kiosk-canvas.js",
    "~/Scripts/kiosk/kiosk-mediapipe.js",
    "~/Scripts/kiosk/kiosk-attendance.js",
    "~/Scripts/kiosk.js"
)
```

### CSS Bundles

After Plan 3 Phase 8B creates `kiosk-bootstrap-compat.css`:
```csharp
new StyleBundle("~/Content/kiosk-styles").Include(
    "~/Content/fa-design-system.css",
    "~/Content/kiosk.css",
    "~/Content/kiosk-bootstrap-compat.css"
)
```

### Rule for This Phase

**Do this phase last, after all other plans are complete.** BundleConfig is the integration point — it verifies the file structure created by all previous plans is coherent. A build error in BundleConfig after this phase means a file was referenced in a plan but not actually created.

**Risk:** Low. BundleConfig errors are caught at compile/startup time.

**Commit:** `build: update BundleConfig for all new and moved files`

---

## Phase 5-F — Add Logging to Silent Exception Paths

This phase does not change any business logic. It adds `Trace.TraceError()` or `Trace.TraceWarning()` to catch blocks that currently swallow exceptions silently.

### Specific Changes

**`AuditHelper.Log()` — inner catch:**
```csharp
catch (Exception ex)
{
    // Before: nothing
    // After:
    Trace.TraceWarning("[AuditHelper] Audit write failed for action={0}: {1}", action, ex.Message);
}
```

**`BiometricCrypto.TryUnprotectString()` — outer catch:**
```csharp
catch (Exception ex)
{
    // Before: return false
    // After:
    Trace.TraceError("[BiometricCrypto] Unprotect failed — DPAPI key may have changed: {0}", ex.Message);
    return false;
}
```

This is the most important one. A DPAPI failure after a server migration would silently produce corrupted face vectors, causing all scans to fail with NOT_RECOGNIZED. The log entry makes this diagnosable.

**`FastScanPipeline.RunCore()` — parallel lambda catches:**
```csharp
// Liveness thread
() =>
{
    try
    {
        var live = new OnnxLiveness();
        var r = live.ScoreFromBitmap(bitmap, faceBox);
        liveOk = r.Ok; liveProb = r.Probability;
    }
    catch (Exception ex)
    {
        Trace.TraceWarning("[FastScanPipeline] Liveness thread failed: {0}", ex.Message);
        // liveOk stays false — frame rejected downstream
    }
}
```

**`EmployeeFaceIndexImpl` and `VisitorFaceIndex` — `catch { }` in load loops:**

Any exception during a single employee's vector load currently causes that employee to be silently excluded from the index. They will never match on scan. Add:
```csharp
catch (Exception ex)
{
    Trace.TraceWarning("[FaceIndex] Failed to load vectors for employee {0}: {1}",
        r.EmployeeId, ex.Message);
}
```

**`FaceIndexBase.RebuildCore()` — BallTree build catch:**
```csharp
catch (Exception ex)
{
    _ballTree = null;
    Trace.TraceWarning("[FaceIndex] BallTree build failed, using linear scan: {0}", ex.Message);
}
```

**Rule for this phase:** Only add logging. Never change the `return`, `throw`, or recovery path. The behavior is unchanged — only visibility improves.

**Risk:** Zero behavior change. `Trace.TraceWarning` writes to the application event log and IIS logs. No new dependencies.

**Commit:** `chore: add trace logging to silent exception paths`

---

## Phase 5-G — Remove EmployeeFaceIndex SQL Duplication

`EmployeeFaceIndexImpl.TryLoadViaSql()` contains a hand-written SQL query:

```sql
SELECT EmployeeId, FaceEncodingBase64, FaceEncodingsJson
FROM Employees
WHERE Status = 'ACTIVE'
AND (FaceEncodingBase64 IS NOT NULL OR FaceEncodingsJson IS NOT NULL)
```

This is functionally identical to what `FaceEncodingHelper.LoadAllEmployeeFaces()` already does, including the dual-key loading and DPAPI unprotect calls.

**What changes:**

Replace `TryLoadViaSql()` and `LoadViaEF()` in `EmployeeFaceIndexImpl` with a single call to `FaceEncodingHelper.LoadAllEmployeeFaces()`:

```csharp
protected override List<Entry> LoadEntriesFromDatabase(FaceAttendDBEntities db)
{
    var maxPerEmployee = ConfigurationService.GetInt("Biometrics:Enroll:MaxImages", 5);
    var employees = FaceEncodingHelper.LoadAllEmployeeFaces(db, maxPerEmployee);
    var list = new List<Entry>();

    foreach (var emp in employees)
        foreach (var vec in emp.FaceVectors)
            list.Add(new Entry { EmployeeId = emp.EmployeeId, Vec = vec });

    return list;
}
```

This removes approximately 60 lines from `EmployeeFaceIndex.cs` and centralizes all employee face loading in `FaceEncodingHelper`.

**Verify:** `FaceEncodingHelper.LoadAllEmployeeFaces()` already handles:
- SQL-first load attempt
- EF fallback if SQL fails
- `BiometricCrypto` unprotect on all values
- `maxPerEmployee` limit
- Null/empty filtering

**Risk:** Low. `FaceEncodingHelper` is already used by `FastFaceMatcher` for the same data. Behavior is identical. Run a scan after this change to confirm matching still works.

**Commit:** `refactor: replace EmployeeFaceIndex duplicate SQL with FaceEncodingHelper`

---

## Phase Summary Table

| Phase | What | Risk | Test |
|---|---|---|---|
| 5-A | Extract `TryParseTime` to `TimeHelper` | Zero | Settings page loads, times parse correctly |
| 5-B | Remove double-call pattern in `BuildVm` | Low | Settings page shows correct values |
| 5-C | Split `SettingsSaver.SaveSettings` into sections | Low | Save settings, verify all keys written to DB |
| 5-D | Simplify `BuildVm` property assignment inline | Low | Settings page shows correct values |
| 5-E | Update `BundleConfig.cs` for all new files | Low | All pages load, no 404 on JS/CSS |
| 5-F | Add logging to silent exception paths | Zero | No behavior change — verify log entries appear |
| 5-G | Remove `EmployeeFaceIndex` SQL duplication | Low | Full attendance scan still matches correctly |

---

## Files Changed

| File | Change |
|---|---|
| `Areas/Admin/Helpers/SettingsViewModelBuilder.cs` | Phases 5-B, 5-D — simplified reads, inline properties |
| `Areas/Admin/Helpers/SettingsSaver.cs` | Phase 5-C — split into private section methods |
| `Areas/Admin/Helpers/SettingsValidator.cs` | Phase 5-A — remove local `TryParseTime`, use `TimeHelper` |
| `Services/Helpers/TimeHelper.cs` | Phase 5-A — **new file**, `TryParseTime` |
| `Services/Biometrics/EmployeeFaceIndex.cs` | Phase 5-G — remove duplicate SQL, use `FaceEncodingHelper` |
| `Services/AuditHelper.cs` | Phase 5-F — add `TraceWarning` in catch |
| `Services/Biometrics/BiometricCrypto.cs` | Phase 5-F — add `TraceError` in unprotect catch |
| `Services/Biometrics/FastScanPipeline.cs` | Phase 5-F — add `TraceWarning` in parallel lambda catches |
| `Services/Biometrics/FaceIndexBase.cs` | Phase 5-F — add `TraceWarning` on BallTree build fail |
| `Services/Biometrics/EmployeeFaceIndex.cs` | Phase 5-F — add `TraceWarning` on per-employee load fail |
| `Services/Biometrics/VisitorFaceIndex.cs` | Phase 5-F — add `TraceWarning` on per-visitor load fail |
| `App_Start/BundleConfig.cs` | Phase 5-E — updated bundle contents |

## Files Created

| File | Purpose |
|---|---|
| `Services/Helpers/TimeHelper.cs` | Shared `TryParseTime()` — 20 lines |

---

## What This Plan Does Not Cover

**Unit tests.** This plan improves observability and reduces duplication. Writing tests against the cleaned code is the natural next step but is a separate plan.

**`SettingsVm.cs` property count.** The ViewModel has 60+ properties. Each maps to one config key. This is the correct shape for a settings form — splitting it would make the settings page harder to maintain, not easier.

**`OnnxLiveness.cs` method length.** `ScoreFromFile()` and `ScoreFromBitmap()` are long but they are doing one well-defined thing. Length is not the same as complexity here.

**Database access patterns.** Direct `db.` calls in controllers remain. A repository layer is not justified at this project's scale.

**ConfigurationService Style 2 removal.** Plan 1 identified this as the goal. The actual migration of all `GetX(db, ...)` call sites to `GetX(...)` is a large mechanical change. After Plan 5 the Settings helpers are the biggest consumers — cleaning them first (Phases 5-B, 5-C) reduces the remaining Style 2 call count significantly. A follow-on commit can handle the remainder.
