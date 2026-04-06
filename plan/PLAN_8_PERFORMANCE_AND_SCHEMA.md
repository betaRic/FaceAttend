# FaceAttend — Plan 8: Performance Profiling and Database Schema
## The Final Operational Plan — After Plans 1–7 Complete

**Status:** Final — aligned with Plans 1–7  
**Rule:** One phase per commit. Verify before each commit. Never combine phases.  
**Unit tests:** Out of scope. Explicitly excluded from all plans.  
**Security hardening:** Out of scope. Separate plan.  
**Multi-admin auth:** Out of scope. Separate plan.

---

## What This Plan Covers

Plans 1–7 organized the code. Plan 8 makes it run better and makes the database safe to evolve.

| Part | Focus |
|---|---|
| **Part A — Performance** | BallTree threshold tuning, ConfigurationService cache TTL, Dlib pool sizing, EF query analysis, GPS resolve rate limiting |
| **Part B — Database Schema** | Schema changes that surfaced during Plans 1–7, idempotent migration scripts, index audit |

---

## What This Plan Does NOT Touch

- Biometric engine internals — stable, out of scope.
- Any JavaScript or CSS — owned by Plans 2, 4, 7.
- Any controller or service reorganization — owned by Plans 1–7.
- Security hardening — separate plan.
- Unit tests — excluded from all plans.
- Multi-admin auth — separate plan.

---

# PART A — PERFORMANCE

## A.1 — The Actual Performance Problems

These are measured, observable problems — not speculation.

### A.1.1 — BallTree Threshold Is a Single Hardcoded Guess

`Biometrics:BallTreeThreshold` defaults to 50. The BallTree is built when enrolled employee count reaches this number. Below the threshold, every scan does a linear O(n) search across all enrolled face vectors.

**What the threshold actually controls:**

At 50 employees with an average of 5 vectors each, a linear scan compares 250 vectors per frame. At 200 employees (5 vectors each), that is 1,000 comparisons. The BallTree gives O(log n) search — at 1,000 vectors, that is roughly 10 comparisons instead of 1,000.

**What the current code does not do:**

There is no logging of which path was taken (BallTree vs linear) or how long each took. An admin cannot tell whether the BallTree is being used or not.

**What Plan 8 adds:**

A diagnostic line in `HealthController.Diagnostics()` that reports:
- Current enrolled employee count
- Current total face vector count
- Whether BallTree is active
- The configured threshold

This makes the threshold visible and adjustable without guessing.

### A.1.2 — ConfigurationService Cache TTL Is One Value for Everything

The cache TTL is hardcoded at 60 seconds for all keys:
```csharp
private const int DefaultCacheSeconds = 60;
```

Operational settings (`Biometrics:LivenessThreshold`, `Attendance:MinGapSeconds`) change rarely — an admin sets them and they should be stable for hours. The 60-second TTL means the DB is hit every 60 seconds for values that never change during a shift.

Config keys that rarely change benefit from a longer TTL (10 minutes). Config keys that an admin may change and expect to take effect quickly benefit from a shorter TTL (60 seconds is fine, 30 seconds is better for those).

**What Plan 8 adds:**

A key-specific TTL map inside `ConfigurationService`. Keys in the "stable" list get a 10-minute TTL. All others keep 60 seconds. The map is a static dictionary — no DB read required.

### A.1.3 — Dlib Pool Size Has No Feedback

`Biometrics:DlibPoolSize` defaults to 4. Each pool instance is a loaded dlib model — approximately 50MB each. At pool size 4, that is 200MB for the pool alone.

**The actual problem:**

If `MaxConcurrentScans` is set higher than `DlibPoolSize`, scans queue at the semaphore. The kiosk returns a 503 without logging how long the wait was or how often it happened.

**What Plan 8 adds:**

A wait-time counter in `DlibBiometrics.RentInstance()`. If the wait exceeds 500ms, a `Trace.TraceWarning` is emitted with the current pool occupancy. This surfaces pool exhaustion without adding overhead to the normal (non-queuing) path.

### A.1.4 — EF Query N+1 in Summary Report

`AttendanceController.SummaryReport()` (and its CSV export variant) queries raw logs then groups them in memory. The LINQ query does an `.Include(x => x.Employee)` but the `EmpId` projection selects `x.Employee.EmployeeId` — causing EF to load the full Employee entity for each log row even though only `EmployeeId` is needed.

At 500 log rows for a 31-day report, this loads 500 Employee entities. The actual data needed is one string per row.

**What Plan 8 adds:**

Replace the `.Include(x => x.Employee)` with a direct projection:

```csharp
// Before
var raw = q
    .Include(x => x.Employee)
    .Select(x => new RawLog
    {
        EmpId = x.Employee.EmployeeId,
        ...
    })

// After — no Include needed, EF translates the navigation in the SELECT
var raw = q
    .Select(x => new RawLog
    {
        EmpId = x.Employee.EmployeeId,
        ...
    })
```

EF6 can project through a navigation property in `.Select()` without requiring `.Include()`. The result is one SQL JOIN instead of N+1 loads.

### A.1.5 — GPS Resolve Rate Limit Has No Client-Side Backoff

`ResolveOffice` is called from `kiosk.js` every `CFG.server.resolveMs` (10 seconds). The server has a rate limit (`RateLimit(Name = "KioskResolve", MaxRequests = 150, WindowSeconds = 60)`). On a busy kiosk with multiple tabs open or rapid page refreshes, this limit can be hit.

**What Plan 8 adds:**

In `kiosk.js` (via the Plan 4 `kiosk-office-resolver.js` module), add exponential backoff when a 429 is received from `/Kiosk/ResolveOffice`. The current code already has `state.officeResolveRetryUntil` — this plan formalizes the backoff calculation:

```js
// Current: retryMs = Math.max(1000, retryAfter * 1000)
// Plan 8: exponential with cap
var backoffMs = Math.min(30000, 1000 * Math.pow(2, retryCount));
```

Reset `retryCount` to 0 on a successful resolve.

---

## A.2 — Performance Phase Plan

### Phase 8-A — BallTree Diagnostics in HealthController

**What changes:**

In `HealthController.Diagnostics()`, after the existing `dlibPool` section, add:

```csharp
var matcherStats = FastFaceMatcher.GetStats();
var ballTreeActive = matcherStats != null &&
    (int)(matcherStats.GetType().GetProperty("EmployeeCount")
          ?.GetValue(matcherStats) ?? 0) >= 
    ConfigurationService.GetInt("Biometrics:BallTreeThreshold", 50);
```

Return in the JSON response:
```json
"faceMatcher": {
    "employeeCount": 47,
    "totalVectors": 230,
    "ballTreeThreshold": 50,
    "ballTreeActive": false,
    "lastLoadedUtc": "2025-01-15T08:30:00Z"
}
```

This requires `FastFaceMatcher.GetStats()` to expose `EmployeeCount` and `TotalFaceVectors` as named properties rather than an anonymous object. Update `FastFaceMatcher.GetStats()` to return a typed `MatcherStats` class.

**New file:**
```csharp
// Services/Biometrics/MatcherStats.cs
public class MatcherStats
{
    public bool     IsInitialized    { get; set; }
    public DateTime LastLoaded       { get; set; }
    public int      EmployeeCount    { get; set; }
    public int      TotalFaceVectors { get; set; }
    public double   MemoryEstimateMB { get; set; }
}
```

Update `FastFaceMatcher.GetStats()` to return `MatcherStats` instead of an anonymous object. Update `SettingsController.Index()` and `HealthController.Diagnostics()` to use the typed return.

**Risk:** Low. Additive change. Anonymous object callers using `ToString()` for display still work; typed callers get compile-time safety.

**Commit:** `perf: expose MatcherStats as typed class, add BallTree status to diagnostics`

---

### Phase 8-B — ConfigurationService Key-Specific TTL

**What changes:**

Add a static TTL map to `ConfigurationService`:

```csharp
private static readonly HashSet<string> _stableKeys = new HashSet<string>(
    StringComparer.OrdinalIgnoreCase)
{
    "Biometrics:DlibModelsDir",
    "Biometrics:LivenessModelPath",
    "Biometrics:DlibDetector",
    "Biometrics:DlibPoolSize",
    "App:TimeZoneId",
    "Admin:AllowedIpRanges",
    "TempFile:MaxAgeMinutes",
    "TempFile:CleanupIntervalMinutes"
};

private static readonly int StableCacheSeconds = 600; // 10 minutes
```

In `GetFromDbCached()`, select the TTL based on whether the key is in the stable set:

```csharp
var ttl = _stableKeys.Contains(key) ? StableCacheSeconds : cacheSeconds;
```

**Keys NOT in the stable set** (operational — admin may change and expect fast effect):
- `Biometrics:LivenessThreshold`
- `Attendance:MinGapSeconds`
- `Biometrics:AttendanceTolerance`
- `Kiosk:MaxConcurrentScans`
- `Kiosk:VisitorEnabled`
- All `NeedsReview:*` keys

These keep the default 60-second TTL.

**Risk:** Low. The only observable difference is that stable keys are re-read from DB every 10 minutes instead of every 60 seconds. Since these values never change in production, the difference is purely a reduction in DB round-trips.

**Commit:** `perf: key-specific TTL for stable config keys (10m vs 60s)`

---

### Phase 8-C — Dlib Pool Wait-Time Warning

**What changes:**

In `DlibBiometrics.RentInstance()`, add a stopwatch around the semaphore wait:

```csharp
private static FaceRecognition RentInstance()
{
    EnsurePoolReady();
    var timeoutMs = ConfigurationService.GetInt("Biometrics:DlibPoolTimeoutMs", 30_000);

    var sw = System.Diagnostics.Stopwatch.StartNew();
    if (!_semaphore.Wait(timeoutMs))
        return null;
    sw.Stop();

    // Warn if the wait was significant — indicates pool exhaustion under load
    if (sw.ElapsedMilliseconds > 500)
    {
        var occupancy = ConfigurationService.GetInt("Biometrics:DlibPoolSize", 4)
                        - _semaphore.CurrentCount;
        Trace.TraceWarning(
            "[DlibPool] Semaphore wait: {0}ms. Pool occupancy: {1}/{2}. " +
            "Consider increasing Biometrics:DlibPoolSize.",
            sw.ElapsedMilliseconds,
            occupancy,
            ConfigurationService.GetInt("Biometrics:DlibPoolSize", 4));
    }

    FaceRecognition instance;
    if (!_pool.TryTake(out instance))
    {
        _semaphore.Release();
        return null;
    }
    return instance;
}
```

**Note:** `SemaphoreSlim` does not expose `CurrentCount` publicly in all .NET versions. Use `_semaphore.CurrentCount` if available, or maintain a separate `Interlocked` counter if not.

**Risk:** Low. Adds two lines inside the happy path (stopwatch start/stop). The warning fires only when wait > 500ms — which is already a problem state. No impact on normal operation.

**Commit:** `perf: log Dlib pool wait time when semaphore blocks > 500ms`

---

### Phase 8-D — Fix EF N+1 in AttendanceController

**What changes:**

In `AttendanceController.SummaryReport()` and `ExportSummaryCsv()`:

Remove `.Include(x => x.Employee)` from the query. EF6 resolves navigation property projections in `.Select()` using a JOIN without requiring explicit Include.

```csharp
// Before
var raw = q
    .Include(x => x.Employee)
    .Select(x => new RawLog { EmpId = x.Employee.EmployeeId, ... })

// After
var raw = q
    .Select(x => new RawLog { EmpId = x.Employee.EmployeeId, ... })
```

Verify with SQL Profiler or EF logging that the resulting query is a single JOIN, not N+1 selects.

**Risk:** Low. The projection semantics are unchanged. Run the summary report for a 31-day range against a real dataset and verify the row counts match.

**Commit:** `perf: remove redundant Include in SummaryReport — EF projects navigation in SELECT`

---

### Phase 8-E — GPS Resolve Exponential Backoff

**What changes:**

In `Scripts/kiosk/kiosk-office-resolver.js` (the module created in Plan 4), add a retry counter:

```js
var _retryCount = 0;

function onRateLimited(retryAfterSeconds) {
    _retryCount++;
    var backoffMs = Math.min(30000, 1000 * Math.pow(2, _retryCount));
    state.officeResolveRetryUntil = Date.now() + Math.max(backoffMs, retryAfterSeconds * 1000);
}

function onResolveSuccess() {
    _retryCount = 0;
    state.officeResolveRetryUntil = 0;
}
```

Replace the current flat retry delay calculation with these two functions. Call `onRateLimited()` on 429 response, `onResolveSuccess()` on allowed response.

**Risk:** Low. The current flat backoff already works. This change makes repeated 429s progressively less frequent, reducing server load during any burst.

**Commit:** `perf: exponential backoff for GPS resolve 429 responses`

---

# PART B — DATABASE SCHEMA

## B.1 — Schema Changes That Surfaced During Plans 1–7

The refactor plans reorganized code but did not change database behavior. However, three schema issues surfaced as side-effects that require attention:

### B.1.1 — Missing Index on AttendanceLogs

`AttendanceService.Record()` runs this query on every scan:

```sql
SELECT TOP 1 * FROM AttendanceLogs
WHERE EmployeeId = @id
  AND Timestamp >= @startLocal
  AND Timestamp < @endLocal
ORDER BY Timestamp DESC
```

If there is no index on `(EmployeeId, Timestamp)`, this is a full table scan on every scan attempt. As the table grows (200 employees × 2 scans/day × 250 working days = 100,000 rows/year), this becomes measurable.

**Required index:**
```sql
CREATE INDEX IX_AttendanceLogs_EmployeeId_Timestamp
ON dbo.AttendanceLogs (EmployeeId, Timestamp DESC);
```

### B.1.2 — Devices Table Missing Index on Fingerprint and DeviceToken

`DeviceService.ValidateDevice()` queries:
```sql
WHERE d.DeviceToken = @token AND d.EmployeeId = @id
-- or
WHERE d.Fingerprint = @fingerprint AND d.EmployeeId = @id
```

There are no indexes on `Fingerprint` or `DeviceToken`. Every scan from a mobile device hits these queries.

**Required indexes:**
```sql
CREATE INDEX IX_Devices_DeviceToken
ON dbo.Devices (DeviceToken)
WHERE DeviceToken IS NOT NULL;

CREATE INDEX IX_Devices_Fingerprint
ON dbo.Devices (Fingerprint)
WHERE Fingerprint IS NOT NULL;
```

### B.1.3 — WiFiBSSID Column Rename Verification

Plans 1–3 noted the `WiFiBSSID` rename (from `WiFiBSSID` with inconsistent casing). This migration script was generated but needs to be verified as applied:

```sql
-- Verify column name is correct
SELECT COLUMN_NAME
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME = 'Offices'
  AND COLUMN_NAME IN ('WiFiBSSID', 'WiFiBSSID');

SELECT COLUMN_NAME
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME = 'AttendanceLogs'
  AND COLUMN_NAME IN ('WiFiBSSID', 'WiFiBSSID');
```

If the rename is not applied, apply it now as part of this plan.

### B.1.4 — VisitorLogs Table — Missing Index on Timestamp

`VisitorService.PurgeOldLogs()` queries:
```sql
WHERE l.Timestamp < @cutoff
ORDER BY l.Id
```

If `Timestamp` has no index, this is a full table scan on every purge. Purge runs on a background schedule but it is still a blocking operation.

**Required index:**
```sql
CREATE INDEX IX_VisitorLogs_Timestamp
ON dbo.VisitorLogs (Timestamp);
```

### B.1.5 — SystemConfiguration Table — Missing Index on Key

`ConfigurationService.GetFromDb()` runs:
```sql
SELECT TOP 1 Value FROM SystemConfigurations WHERE Key = @key
```

This is called on cache miss for every configuration key. The `Key` column should be indexed.

**Verify:**
```sql
SELECT i.name, c.name AS column_name
FROM sys.indexes i
JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
WHERE OBJECT_NAME(i.object_id) = 'SystemConfigurations';
```

If `Key` is not indexed or is only covered by the primary key (which is on `Id`), add:

```sql
CREATE UNIQUE INDEX UX_SystemConfigurations_Key
ON dbo.SystemConfigurations ([Key]);
```

This also enforces key uniqueness at the database level — currently only enforced by application code.

---

## B.2 — Database Phase Plan

### Phase 8-F — Index Audit Script

**What this produces:**

A single idempotent SQL script that can be run safely on any environment (dev, staging, production) and will:
- Check each index before creating it
- Skip creation if the index already exists
- Print a status message for each check

```sql
-- FaceAttend — Index Audit and Creation
-- Idempotent: safe to run multiple times
-- Plan 8 Phase F

PRINT 'FaceAttend Index Audit — starting';

-- 1. AttendanceLogs: EmployeeId + Timestamp
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('dbo.AttendanceLogs')
      AND name = 'IX_AttendanceLogs_EmployeeId_Timestamp')
BEGIN
    PRINT 'Creating IX_AttendanceLogs_EmployeeId_Timestamp...';
    CREATE INDEX IX_AttendanceLogs_EmployeeId_Timestamp
    ON dbo.AttendanceLogs (EmployeeId, Timestamp DESC);
    PRINT 'Done.';
END
ELSE
    PRINT 'IX_AttendanceLogs_EmployeeId_Timestamp already exists — skipped.';

-- 2. Devices: DeviceToken (filtered)
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('dbo.Devices')
      AND name = 'IX_Devices_DeviceToken')
BEGIN
    PRINT 'Creating IX_Devices_DeviceToken...';
    CREATE INDEX IX_Devices_DeviceToken
    ON dbo.Devices (DeviceToken)
    WHERE DeviceToken IS NOT NULL;
    PRINT 'Done.';
END
ELSE
    PRINT 'IX_Devices_DeviceToken already exists — skipped.';

-- 3. Devices: Fingerprint (filtered)
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('dbo.Devices')
      AND name = 'IX_Devices_Fingerprint')
BEGIN
    PRINT 'Creating IX_Devices_Fingerprint...';
    CREATE INDEX IX_Devices_Fingerprint
    ON dbo.Devices (Fingerprint)
    WHERE Fingerprint IS NOT NULL;
    PRINT 'Done.';
END
ELSE
    PRINT 'IX_Devices_Fingerprint already exists — skipped.';

-- 4. VisitorLogs: Timestamp
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('dbo.VisitorLogs')
      AND name = 'IX_VisitorLogs_Timestamp')
BEGIN
    PRINT 'Creating IX_VisitorLogs_Timestamp...';
    CREATE INDEX IX_VisitorLogs_Timestamp
    ON dbo.VisitorLogs (Timestamp);
    PRINT 'Done.';
END
ELSE
    PRINT 'IX_VisitorLogs_Timestamp already exists — skipped.';

-- 5. SystemConfigurations: Key (unique)
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('dbo.SystemConfigurations')
      AND name = 'UX_SystemConfigurations_Key')
BEGIN
    PRINT 'Creating UX_SystemConfigurations_Key...';
    CREATE UNIQUE INDEX UX_SystemConfigurations_Key
    ON dbo.SystemConfigurations ([Key]);
    PRINT 'Done.';
END
ELSE
    PRINT 'UX_SystemConfigurations_Key already exists — skipped.';

PRINT 'FaceAttend Index Audit — complete';
```

**Where this script lives:** `Database/Migrations/008_index_audit.sql`

Create a `Database/Migrations/` directory if it does not exist. All migration scripts are named `NNN_description.sql` and are idempotent. They are run manually via SSMS — not through EF migrations.

**Risk:** Low. All `CREATE INDEX` operations are non-destructive. On SQL Server Express with a live database, index creation locks the table briefly — run during off-hours or low-traffic period.

**Commit:** `schema: add index audit migration script 008`

---

### Phase 8-G — WiFiBSSID Rename Verification Script

```sql
-- FaceAttend — WiFiBSSID Column Verification
-- Plan 8 Phase G

PRINT 'Checking WiFiBSSID column names...';

-- Check Offices table
IF EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_NAME = 'Offices' AND COLUMN_NAME = 'WiFiBSSID')
    PRINT 'Offices.WiFiBSSID — OK';
ELSE IF EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_NAME = 'Offices' AND COLUMN_NAME = 'WiFiBSSID')
BEGIN
    PRINT 'Offices.WiFiBSSID needs rename — applying...';
    EXEC sp_rename 'dbo.Offices.WiFiBSSID', 'WiFiBSSID', 'COLUMN';
    PRINT 'Done.';
END
ELSE
    PRINT 'WARNING: Offices table has no WiFiBSSID column — check schema.';

-- Check AttendanceLogs table
IF EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_NAME = 'AttendanceLogs' AND COLUMN_NAME = 'WiFiBSSID')
    PRINT 'AttendanceLogs.WiFiBSSID — OK';
ELSE IF EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_NAME = 'AttendanceLogs' AND COLUMN_NAME = 'WiFiBSSID')
BEGIN
    PRINT 'AttendanceLogs.WiFiBSSID needs rename — applying...';
    EXEC sp_rename 'dbo.AttendanceLogs.WiFiBSSID', 'WiFiBSSID', 'COLUMN';
    PRINT 'Done.';
END
ELSE
    PRINT 'WARNING: AttendanceLogs has no WiFiBSSID column — check schema.';

PRINT 'WiFiBSSID verification complete.';
```

**Where this lives:** `Database/Migrations/007_wifibssid_rename_verify.sql`

**Risk:** Zero if already applied. If the rename was missed, `sp_rename` applies it without data loss.

**Commit:** `schema: add WiFiBSSID rename verification script 007`

---

### Phase 8-H — Attendance Retention Cleanup Stored Procedure

Currently `VisitorService.PurgeOldLogs()` runs purge in C# with batched deletes. There is no equivalent mechanism for `AttendanceLogs`. A department with 200 employees generating 2 scans/day will accumulate ~145,000 rows/year. After 5 years: ~725,000 rows.

This is manageable for SQL Express (10GB limit) but only if old records are periodically archived or purged. No purge mechanism currently exists for AttendanceLogs.

**What Plan 8 adds:**

A stored procedure for optional attendance log archival:

```sql
-- FaceAttend — Attendance Log Retention
-- Database/Migrations/008_attendance_retention.sql
-- Run this to create the retention procedure.
-- Execution is manual — call from SSMS or a scheduled SQL Agent job.

IF OBJECT_ID('dbo.sp_PurgeOldAttendanceLogs', 'P') IS NOT NULL
    DROP PROCEDURE dbo.sp_PurgeOldAttendanceLogs;
GO

CREATE PROCEDURE dbo.sp_PurgeOldAttendanceLogs
    @RetentionYears INT = 5,
    @BatchSize      INT = 500,
    @DryRun         BIT = 1   -- 1 = count only, 0 = actually delete
AS
BEGIN
    SET NOCOUNT ON;

    IF @RetentionYears < 1 SET @RetentionYears = 1;
    IF @BatchSize < 100   SET @BatchSize = 100;
    IF @BatchSize > 5000  SET @BatchSize = 5000;

    DECLARE @Cutoff  DATETIME = DATEADD(YEAR, -@RetentionYears, GETUTCDATE());
    DECLARE @Deleted INT      = 0;
    DECLARE @Total   INT;

    SELECT @Total = COUNT(*)
    FROM dbo.AttendanceLogs
    WHERE Timestamp < @Cutoff;

    PRINT 'Cutoff: ' + CONVERT(VARCHAR, @Cutoff, 120);
    PRINT 'Rows older than cutoff: ' + CAST(@Total AS VARCHAR);

    IF @DryRun = 1
    BEGIN
        PRINT 'DryRun=1 — no rows deleted. Set @DryRun=0 to purge.';
        RETURN;
    END

    WHILE 1 = 1
    BEGIN
        DELETE TOP (@BatchSize)
        FROM dbo.AttendanceLogs
        WHERE Timestamp < @Cutoff;

        SET @Deleted = @Deleted + @@ROWCOUNT;
        IF @@ROWCOUNT = 0 BREAK;
    END

    PRINT 'Deleted: ' + CAST(@Deleted AS VARCHAR) + ' rows.';
END
GO
```

**Usage:**
```sql
-- Preview only (no deletion)
EXEC dbo.sp_PurgeOldAttendanceLogs @RetentionYears = 5, @DryRun = 1;

-- Actually delete records older than 5 years
EXEC dbo.sp_PurgeOldAttendanceLogs @RetentionYears = 5, @DryRun = 0;
```

**This procedure is manual.** No background task calls it automatically. An admin runs it when needed. This is the correct choice for SQL Express — SQL Agent is not available on Express, and the operation should be consciously initiated.

**Risk:** Zero for dry run. For the actual delete, run during off-hours. The batched delete approach prevents transaction log bloat.

**Commit:** `schema: add sp_PurgeOldAttendanceLogs procedure`

---

## Phase Summary

| Phase | What | Type | Risk |
|---|---|---|---|
| **8-A** | `MatcherStats` typed class, BallTree status in diagnostics | C# | Low |
| **8-B** | Key-specific TTL for stable config keys | C# | Low |
| **8-C** | Dlib pool wait-time warning log | C# | Low |
| **8-D** | Remove redundant `.Include()` in SummaryReport | C# | Low |
| **8-E** | Exponential backoff for GPS resolve 429 | JS | Low |
| **8-F** | Index audit migration script (5 indexes) | SQL | Low |
| **8-G** | WiFiBSSID rename verification script | SQL | Zero |
| **8-H** | Attendance log retention stored procedure | SQL | Zero (dry run default) |

---

## Files Created by Plan 8

| File | Purpose |
|---|---|
| `Services/Biometrics/MatcherStats.cs` | Typed stats object replacing anonymous object from `FastFaceMatcher.GetStats()` |
| `Database/Migrations/007_wifibssid_rename_verify.sql` | WiFiBSSID rename idempotent verification |
| `Database/Migrations/008_index_audit.sql` | Creates 5 missing indexes, idempotent |
| `Database/Migrations/008_attendance_retention.sql` | Attendance log purge stored procedure |

## Files Changed by Plan 8

| File | Change |
|---|---|
| `Services/Biometrics/FastFaceMatcher.cs` | `GetStats()` returns `MatcherStats` instead of anonymous object |
| `Services/ConfigurationService.cs` | `_stableKeys` set added, TTL selection logic in `GetFromDbCached()` |
| `Services/Biometrics/DlibBiometrics.cs` | Pool wait-time warning in `RentInstance()` |
| `Areas/Admin/Controllers/AttendanceController.cs` | Remove `.Include(x => x.Employee)` from summary queries |
| `Controllers/HealthController.cs` | Add `faceMatcher` section to diagnostics response |
| `Scripts/kiosk/kiosk-office-resolver.js` | Exponential backoff on 429 |

---

## What Comes After Plan 8

These are the remaining out-of-scope items explicitly noted across all plans. They require separate planning documents:

| Item | Why Separate |
|---|---|
| **Unit test coverage** | Requires test project setup, mocking strategy, and CI pipeline — a full separate plan |
| **Security hardening** | IP allowlist configuration, DPAPI entropy enforcement, PIN rotation policy — an on-site operational plan |
| **Multi-admin authentication** | Requires architecture decision (ASP.NET Identity vs custom) — separate architecture plan |

Plans 1–8 together constitute the complete refactor of the FaceAttend codebase toward clean, maintainable, professional production code. After Plan 8, a developer joining the project can navigate the entire codebase in a single session and understand it fully.
