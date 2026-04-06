# FaceAttend — Plan 7: CSS Consolidation, admin.js Full Split, SQL Query Extraction
## The Three Items Explicitly Deferred by Plan 6

**Status:** Final — aligned with Plans 1–6  
**Rule:** One phase per commit. Build after every phase. Never combine phases.  
**Unit tests:** Out of scope. Explicitly excluded from all plans.

---

## What This Plan Does NOT Touch

- Biometric engine internals — stable, out of scope.
- `fa-design-system.css` design tokens — single source of truth, untouched.
- Any JS modules created by Plans 2 and 4 — owned by those plans.
- Database schema — separate migration plan.
- `SettingsVm.cs` — correct shape, not a problem.
- Any file not listed in the phase breakdown below.

---

## What Plan 6 Explicitly Deferred to Plan 7

| Item | Deferred Because |
|---|---|
| CSS consolidation — `kiosk.css` Bootstrap compat ~400 lines | Plan 3 Phase 8B created `kiosk-bootstrap-compat.css` but did not clean up admin.css overlaps or document the final token usage map |
| `admin.js` full modularization | Plan 3 Phase 9 extracted only DataTables and the office map. The remaining ~600 lines still cover six unrelated responsibilities in one file |
| SQL query extraction | Plan 3 identified `QueryEmployeeRows()` and the attendance summary queries as problems but provided no extraction plan |

---

## Problem 1 — CSS State After Plan 3

Plan 3 Phase 8 created `kiosk-bootstrap-compat.css` and removed duplicate wizard rules from `wizard.css`. What remains:

### 1A — admin.css Token Overlap

`Content/admin.css` (not shown in the source files provided to this plan set, but referenced in the admin layout) defines sidebar colors, table header shading, and card borders using hardcoded hex values instead of the `--color-*`, `--bg-*`, and `--border-*` CSS variables from `fa-design-system.css`.

This means two things:
- The admin UI does not inherit dark/light theme switching if it is ever added.
- Any color change requires edits in two places.

The specific patterns to find and replace:

| Current (hardcoded) | Replacement (token) |
|---|---|
| `background: #0f172a` | `background: var(--bg-page)` |
| `background: #1e293b` | `background: var(--bg-elevated)` |
| `border-color: #334155` | `border-color: var(--border-default)` |
| `color: #94a3b8` | `color: var(--text-muted)` |
| `color: #f8fafc` | `color: var(--text-primary)` |
| `color: #3b82f6` | `color: var(--color-primary)` |
| `color: #22c55e` | `color: var(--color-success)` |
| `color: #ef4444` | `color: var(--color-error)` |

**Rule:** Only replace values that exactly match a design token. If a color is a one-off shade not in the token set, leave it as-is with a `/* intentional: no token */` comment.

### 1B — component CSS files still using hardcoded colors

The files in `Content/_unified/components/` — `modal.css`, `camera.css`, `uploader.css` — use `var(--color-bg-elevated)` and `var(--color-text-primary)` which are not the correct token names. The correct names from `fa-design-system.css` are `var(--bg-elevated)` and `var(--text-primary)`.

**Example:**
```css
/* Wrong token name */
background: var(--color-bg-elevated);

/* Correct token name from fa-design-system.css */
background: var(--bg-elevated);
```

These wrong-named tokens fall back to nothing, making those components use the browser default color instead of the design system value.

### 1C — kiosk.css After Plan 3

Plan 3 Phase 8B moved Bootstrap compat rules to `kiosk-bootstrap-compat.css`. After that extraction, `kiosk.css` should contain only:
- CSS variables for glow effects (`--glow-primary`, `--glow-success`, `--glow-warning`)
- Base reset
- Grid layout (`#kioskRoot`)
- HUD panels
- Liveness bar
- Idle overlay
- Modals
- Animations
- Responsive rules

Verify this is the case after Plan 3. If any Bootstrap-style rules (`col-*`, `.row`, `.btn`, `.form-control`) remain in `kiosk.css` after Plan 3, remove them in this phase.

---

## Problem 2 — admin.js After Plan 3

Plan 3 Phase 9 created `admin-datatable.js` and `admin-map.js`. After that extraction, `admin.js` still contains approximately 600 lines covering:

| Section | Lines (approx) | Can be separated |
|---|---|---|
| `toast()` wrapper | 40 | Redundant with `notify.js` — see below |
| `confirmDialog()` wrapper | 50 | Redundant with `notify.js` — see below |
| `initFooterYear()` | 8 | Too small to separate — keep |
| `initTooltips()` | 12 | Too small to separate — keep |
| `initAutoDismissAlerts()` | 18 | Keep |
| `initServerToast()` | 20 | Keep |
| `initConfirmLinks()` | 45 | **Separate** — event delegation on `[data-confirm]` |
| `initIdleOverlay()` | 35 | **Separate** — standalone idle detection |
| `initBackToTop()` | 50 | **Separate** — standalone scroll button |
| `initKeyboardShortcuts()` | 22 | Keep — 22 lines, not worth a file |
| `initRefreshButton()` | 14 | Keep |
| `initSidebarActiveState()` | 25 | Keep |
| `window.ui` assignments | 12 | Remove (Plan 3 Phase 9 noted these are duplicated with `notify.js`) |

### The toast/confirm Duplication Problem (Still Open After Plan 3)

Plan 3 Phase 9 noted the `window.ui` duplication but left the `toast()` and `confirmDialog()` functions inside `admin.js`. These are still there.

The correct resolution:
- `notify.js` provides `window.ui.toast`, `window.ui.confirm`, etc.
- `admin.js` internal `toast()` and `confirmDialog()` functions are only needed for `initServerToast()` to call on page load.
- After Plan 3 Phase 9 removed the `window.ui` re-assignment, the internal functions became orphaned — `initServerToast()` calls `toast()` but `toast()` now calls `toastr` which may or may not be loaded on every admin page.

**Resolution for Plan 7:** Remove `toast()` and `confirmDialog()` from `admin.js` entirely. Replace the `initServerToast()` call with `window.ui.toast()` (which comes from `notify.js`). `notify.js` is loaded first in the admin bundle, so it is always available.

---

## Problem 3 — SQL Queries in Controllers

Two controllers contain raw SQL strings that should not be in controller files.

### 3A — `EmployeesController.QueryEmployeeRows()`

```csharp
private List<EmployeeListRowDto> QueryEmployeeRows(FaceAttendDBEntities db, string q, string status)
{
    var term = (q ?? "").Trim();
    var like = "%" + term + "%";

    return db.Database.SqlQuery<EmployeeListRowDto>(@"
SELECT e.Id,
       e.EmployeeId,
       ...
FROM dbo.Employees e
LEFT JOIN dbo.Offices o ON o.Id = e.OfficeId
WHERE (@term = ''
       OR e.EmployeeId LIKE @like ...)
  AND (@status = 'ALL'
       OR ISNULL(e.[Status], 'INACTIVE') = @status)
ORDER BY ...",
        new SqlParameter("@term", term),
        new SqlParameter("@like", like),
        new SqlParameter("@status", status)).ToList();
}
```

This is a 40-line SQL string inside a controller. The controller calls it from `Index()` only.

### 3B — `AttendanceController` Raw SQL in `SummaryReport()`

The `SummaryReport()` action passes a hardcoded cap to LINQ queries and assembles the `RawLog` projection inline. While not raw SQL strings, the query construction logic is embedded directly in the action method. The nested `RawLog` private class and the `BuildDailyRow()` business logic method (moved to `AttendanceReportService` by Plan 3 Phase 4) should leave no SQL-adjacent logic in the controller after that move.

Verify Plan 3 Phase 4 extracted all business logic from `AttendanceController`. If `BuildDailyRow()` still exists in the controller after Plan 3, flag it for cleanup here.

---

## Phase 7-A — Fix Component CSS Token Names

**What changes:**

In each of these files, find all `var(--color-bg-*)` and `var(--color-text-*)` references and replace with the correct token names from `fa-design-system.css`:

| File | Wrong Token | Correct Token |
|---|---|---|
| `Content/_unified/components/modal.css` | `var(--color-bg-elevated)` | `var(--bg-elevated)` |
| `Content/_unified/components/modal.css` | `var(--color-text-primary)` | `var(--text-primary)` |
| `Content/_unified/components/modal.css` | `var(--color-text-secondary)` | `var(--text-secondary)` |
| `Content/_unified/components/modal.css` | `var(--color-text-muted)` | `var(--text-muted)` |
| `Content/_unified/components/modal.css` | `var(--color-border-subtle)` | `var(--border-subtle)` |
| `Content/_unified/components/modal.css` | `var(--color-bg-tertiary)` | `var(--bg-surface)` |
| `Content/_unified/components/camera.css` | `var(--color-brand-primary)` | `var(--color-primary)` |
| `Content/_unified/components/camera.css` | `var(--color-brand-gradient)` | `var(--color-primary)` |
| `Content/_unified/components/uploader.css` | `var(--color-bg-elevated)` | `var(--bg-elevated)` |
| `Content/_unified/components/uploader.css` | `var(--color-error-light)` | `var(--color-error-light)` — verify this token exists; if not use `rgba(239,68,68,0.1)` |

**How to find them all:** Search the entire `Content/` directory for `var(--color-bg-` and `var(--color-text-`. Every result is a wrong token name. Fix each one.

**Test:** Load the enrollment page and visitor modal. Verify they render with correct background and text colors, not transparent or browser-default white.

**Risk:** Low. If a token name is wrong and falls back to nothing, the element currently renders incorrectly. This fix makes it render correctly. No regression possible.

**Commit:** `fix: correct CSS variable names in component files to match fa-design-system tokens`

---

## Phase 7-B — admin.css Token Replacement

**What changes:**

Open `Content/admin.css`. Replace all hardcoded hex color values with the corresponding design system tokens from Phase 1A above.

**Process:**
1. Search `admin.css` for `#0f172a`, `#1e293b`, `#334155`, `#94a3b8`, `#f8fafc`, `#3b82f6`, `#22c55e`, `#ef4444`
2. Replace each with the corresponding token from the table in Problem 1A
3. If a color does not match any token, leave it with a `/* no token */` comment

**Test:** Load the admin dashboard. Verify sidebar, table headers, card borders, and text colors render identically to before.

**Risk:** Low. Colors are being replaced with tokens that resolve to the same values.

**Commit:** `style: replace hardcoded hex colors in admin.css with design system tokens`

---

## Phase 7-C — Verify kiosk.css After Plan 3

**What changes:**

Open `kiosk.css`. Confirm the Bootstrap compat section was moved to `kiosk-bootstrap-compat.css` by Plan 3 Phase 8B.

If any of these class patterns remain in `kiosk.css`, move them:
- `.btn`, `.btn-*`
- `.form-control`, `.form-select`
- `.table`, `.table-*`
- `.badge`
- `.alert`, `.alert-*`
- `.card`, `.card-*`
- `.modal`, `.modal-*`
- `.nav-tabs`, `.tab-content`
- `.row`, `.col-*`, `.container`

If Plan 3 Phase 8B is complete, this phase is a verification pass only with no file changes. Document the result in the commit message.

**Commit:** `style: verify kiosk.css Bootstrap compat extraction from Plan 3 is complete`

---

## Phase 7-D — Remove toast() and confirmDialog() From admin.js

**What changes:**

1. Delete the `toast()` function from `admin.js` (approximately lines 25–70)
2. Delete the `confirmDialog()` function from `admin.js` (approximately lines 75–115)
3. In `initServerToast()`, replace the internal `toast(type, window.__toastMsg)` call with `window.ui.toast(window.__toastMsg, { type: type })`
4. Verify `notify.js` is loaded before `admin.js` in the admin bundle. In `BundleConfig.cs`, `notify.js` must precede `admin.js`.
5. Remove any remaining `window.ui.toast = toast` or `window.ui.confirm = confirmDialog` assignments from `admin.js` — these were noted in Plan 3 Phase 9 but may still exist.

**Test:** Load any admin page with a TempData flash message (e.g., save settings). The toast should appear via `notify.js`/Swal rather than `admin.js`/toastr.

**Risk:** Low-medium. The only behavior change is which library renders the server flash toast. Verify Swal is loaded on admin pages before committing.

**Commit:** `refactor: remove duplicate toast/confirm from admin.js, delegate to notify.js`

---

## Phase 7-E — Extract admin-confirm-links.js

**What moves:**

The `initConfirmLinks()` function in `admin.js` (~45 lines) intercepts clicks on any element with a `[data-confirm]` attribute and shows a confirmation dialog before proceeding. It is the largest remaining extractable section.

**Create `Scripts/admin/admin-confirm-links.js`:**

```javascript
(function () {
    'use strict';

    function initConfirmLinks() {
        document.addEventListener('click', function (e) {
            var el = e.target && e.target.closest('[data-confirm]');
            if (!el) return;
            if (el.dataset.confirmed === '1') {
                el.dataset.confirmed = '0';
                return;
            }
            e.preventDefault();
            e.stopPropagation();

            var title  = el.getAttribute('data-confirm')      || 'Are you sure?';
            var text   = el.getAttribute('data-confirm-text') || '';
            var icon   = el.getAttribute('data-confirm-icon') || 'warning';

            window.ui.confirmDialog({ title: title, text: text, icon: icon })
                .then(function (ok) {
                    if (!ok) return;
                    if (el.tagName === 'A' && el.href && el.href !== '#') {
                        location.href = el.href;
                        return;
                    }
                    var form = el.closest('form');
                    if (form) { form.submit(); return; }
                    el.dataset.confirmed = '1';
                    el.click();
                });
        });
    }

    if (document.readyState === 'loading')
        document.addEventListener('DOMContentLoaded', initConfirmLinks);
    else
        initConfirmLinks();
})();
```

Remove `initConfirmLinks()` and its `onReady` call from `admin.js`. Add `admin-confirm-links.js` to the admin bundle in `BundleConfig.cs`.

**Risk:** Low. The function is self-contained — it reads only DOM attributes and calls `window.ui.confirmDialog`.

**Commit:** `refactor: extract initConfirmLinks to admin-confirm-links.js`

---

## Phase 7-F — Extract admin-idle-overlay.js and admin-back-to-top.js

**What moves:**

**`initIdleOverlay()`** — 35 lines. Shows a dim overlay after 10 minutes of inactivity. Zero dependencies on other admin.js functions.

Create `Scripts/admin/admin-idle-overlay.js` with the extracted function and its own `DOMContentLoaded` init.

**`initBackToTop()`** — 50 lines. Creates and manages a scroll-to-top button. Zero dependencies.

Create `Scripts/admin/admin-back-to-top.js` with the extracted function and its own `DOMContentLoaded` init.

Add both to the admin bundle.

Remove both functions and their `onReady` calls from `admin.js`.

**After this phase, what remains in `admin.js`:**
- `onReady()` helper (8 lines — local, used by the init calls below)
- `initFooterYear()` — 8 lines
- `initTooltips()` — 12 lines
- `initAutoDismissAlerts()` — 18 lines
- `initServerToast()` — 20 lines (now calls `window.ui.toast`)
- `initKeyboardShortcuts()` — 22 lines
- `initRefreshButton()` — 14 lines
- `initSidebarActiveState()` — 25 lines
- `window.ui.initDataTables = initDataTables` assignment (1 line — kept for page scripts)
- `onReady()` boot block — 12 lines

**Target size after this phase:** ~140 lines. Every remaining section is under 25 lines. No further extraction is warranted.

**Risk:** Low. Both functions are self-contained.

**Commit:** `refactor: extract initIdleOverlay and initBackToTop from admin.js`

---

## Phase 7-G — Extract QueryEmployeeRows to a Query Helper

**What moves:**

The `QueryEmployeeRows()` private method in `EmployeesController` is a 40-line SQL query. Move it to `Areas/Admin/Helpers/EmployeeQueryHelper.cs`:

```csharp
namespace FaceAttend.Areas.Admin.Helpers
{
    public static class EmployeeQueryHelper
    {
        public static List<EmployeeListRowDto> QueryRows(
            FaceAttendDBEntities db, string searchTerm, string status)
        {
            var term = (searchTerm ?? "").Trim();
            var like = "%" + term + "%";
            var normalizedStatus = NormalizeStatus(status);

            return db.Database.SqlQuery<EmployeeListRowDto>(@"
SELECT e.Id,
       e.EmployeeId,
       e.FirstName,
       e.MiddleName,
       e.LastName,
       e.Position,
       e.Department,
       e.OfficeId,
       ISNULL(o.Name, '-') AS OfficeName,
       e.IsFlexi,
       CASE WHEN e.FaceEncodingBase64 IS NOT NULL
                 OR e.FaceEncodingsJson IS NOT NULL
            THEN CAST(1 AS bit)
            ELSE CAST(0 AS bit)
       END AS HasFace,
       ISNULL(e.[Status], 'INACTIVE') AS [Status],
       e.CreatedDate
FROM dbo.Employees e
LEFT JOIN dbo.Offices o ON o.Id = e.OfficeId
WHERE (@term = ''
       OR e.EmployeeId LIKE @like
       OR e.FirstName  LIKE @like
       OR e.LastName   LIKE @like
       OR ISNULL(e.MiddleName,  '') LIKE @like
       OR ISNULL(e.Department,  '') LIKE @like
       OR ISNULL(e.Position,    '') LIKE @like)
  AND (@status = 'ALL'
       OR ISNULL(e.[Status], 'INACTIVE') = @status)
ORDER BY CASE WHEN ISNULL(e.[Status], 'INACTIVE') = 'PENDING' THEN 0 ELSE 1 END,
         e.LastName, e.FirstName, e.EmployeeId",
                new SqlParameter("@term", term),
                new SqlParameter("@like", like),
                new SqlParameter("@status", normalizedStatus)).ToList();
        }

        private static string NormalizeStatus(string status)
        {
            var s = (status ?? "ACTIVE").Trim().ToUpperInvariant();
            return (s == "ACTIVE" || s == "PENDING" || s == "INACTIVE" || s == "ALL")
                ? s : "ACTIVE";
        }
    }
}
```

In `EmployeesController.Index()`, replace:
```csharp
var allRows = QueryEmployeeRows(db, q, normalizedStatus);
```
With:
```csharp
var allRows = EmployeeQueryHelper.QueryRows(db, q, status);
```

Remove `QueryEmployeeRows()` and the private `NormalizeStatus()` method from `EmployeesController`.

**Note:** `NormalizeStatus()` was also used elsewhere in `EmployeesController`. Move it to `EmployeeQueryHelper` and call it from the controller where needed, or keep a local copy in the controller for non-query uses. Do not remove the controller's own status normalization for POST actions — those have different validation contexts.

**Risk:** Low. Pure extraction. SQL string is unchanged. Run the employee list page with all filter combinations to verify.

**Commit:** `refactor: extract QueryEmployeeRows to EmployeeQueryHelper`

---

## Phase 7-H — Verify AttendanceController After Plan 3

**What to verify:**

Plan 3 Phase 4 created `AttendanceReportService` and moved `BuildDailyRow()`, `LoadAttendancePolicy()`, and `BuildCsv()` out of `AttendanceController`. This phase verifies that move is complete.

Open `AttendanceController.cs` and confirm:
- No `BuildDailyRow()` method exists in the controller
- No `LoadAttendancePolicy()` method exists in the controller
- No `RawLog`, `AttendancePolicy`, or `ExportRow` nested classes remain in the controller
- The `SummaryReport()` action calls `AttendanceReportService.BuildDailyRow()` (or equivalent)

If any of these still exist in the controller, move them now. This is a completion pass for Plan 3 Phase 4 that was deferred.

**Risk:** Low if Plan 3 Phase 4 is complete, medium if it is not. Verify Plan 3 Phase 4 completion first.

**Commit:** `refactor: complete AttendanceController cleanup from Plan 3 Phase 4 (if needed)`

---

## Phase Summary Table

| Phase | What | Files Changed | Risk | Test |
|---|---|---|---|---|
| **7-A** | Fix wrong CSS variable names in component files | `modal.css`, `camera.css`, `uploader.css` | Low | Modal and camera render correctly |
| **7-B** | Replace hardcoded hex in admin.css with tokens | `admin.css` | Low | Admin UI unchanged visually |
| **7-C** | Verify kiosk.css Bootstrap compat extraction | `kiosk.css` (verify only) | Zero | Kiosk UI unchanged |
| **7-D** | Remove toast/confirm from admin.js, delegate to notify.js | `admin.js`, `BundleConfig.cs` | Low-medium | Server flash toast appears on admin pages |
| **7-E** | Extract `initConfirmLinks` | `admin.js`, new `admin-confirm-links.js` | Low | `[data-confirm]` links still prompt |
| **7-F** | Extract idle overlay and back-to-top | `admin.js`, 2 new files | Low | Idle overlay and scroll button work |
| **7-G** | Extract `QueryEmployeeRows` to `EmployeeQueryHelper` | `EmployeesController.cs`, new helper | Low | Employee list with all filter/status combinations |
| **7-H** | Verify AttendanceController cleanup from Plan 3 | `AttendanceController.cs` | Low | Summary report and CSV export |

---

## Files Created by Plan 7

| File | Purpose |
|---|---|
| `Areas/Admin/Helpers/EmployeeQueryHelper.cs` | Employee list SQL query + status normalization |
| `Scripts/admin/admin-confirm-links.js` | `[data-confirm]` click interception |
| `Scripts/admin/admin-idle-overlay.js` | 10-minute idle overlay |
| `Scripts/admin/admin-back-to-top.js` | Scroll-to-top button |

## Files Significantly Changed

| File | Change |
|---|---|
| `Scripts/admin.js` | Removes ~250 lines: `toast()`, `confirmDialog()`, `initConfirmLinks()`, `initIdleOverlay()`, `initBackToTop()` |
| `Content/_unified/components/modal.css` | Wrong token names corrected |
| `Content/_unified/components/camera.css` | Wrong token names corrected |
| `Content/_unified/components/uploader.css` | Wrong token names corrected |
| `Content/admin.css` | Hardcoded hex replaced with design tokens |
| `Areas/Admin/Controllers/EmployeesController.cs` | `QueryEmployeeRows()` and `NormalizeStatus()` removed |
| `App_Start/BundleConfig.cs` | 3 new admin JS files added to admin bundle |

---

## After Plan 7 Is Complete

Together, Plans 1–7 leave the codebase with:

- **Controllers:** Single responsibility. No business logic. No SQL. No inline scripts.
- **Services:** Named by what they do. One public API (`ConfigurationService.GetX(key, default)`). Failures logged.
- **JavaScript:** Each file has one named responsibility. `admin.js` is ~140 lines of initialization glue. `kiosk.js` is ~200 lines of loop and wiring.
- **CSS:** All colors from design tokens. No duplicate rule sets. Component files use correct token names.
- **Views:** No ViewBag for structured data. No inline scripts over 50 lines. No dead files.
- **SQL:** No raw SQL strings in controllers. Query logic in named helpers.

---

## What Remains After Plan 7

These items are real but are outside the scope of a code-organization plan:

- **Unit test coverage** — explicitly excluded from all plans.
- **Security hardening** — IP restrictions, DPAPI entropy configuration, admin PIN rotation policy. Separate security plan.
- **Multi-admin authentication** — PIN-based single admin is correct for the current deployment. Scaling to multi-admin requires a separate architecture plan.
- **Performance profiling** — BallTree vs linear scan thresholds, connection pool sizing. Separate performance plan.
- **Database schema** — migration plan for any schema changes that surface during the refactor.
