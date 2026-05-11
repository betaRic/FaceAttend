# FaceAttend Maintainability Audit

Date: 2026-05-06

This audit is intentionally blunt. The current system is deployable only if the codebase stops growing sideways. The project has useful services, but too many files claim to be "centralized" while parallel logic still exists elsewhere.

## Executive Verdict

The biggest risks are not MVC itself or OpenVINO itself. The biggest risks are:

- duplicated camera/enrollment JavaScript,
- oversized biometric services doing multiple jobs,
- old "device registration" vocabulary leaking into the new public/mobile model,
- stale compatibility wording in docs and API examples,
- view files carrying too much layout/script behavior,
- compatibility files and vendor duplicates making the project look larger than it is.

The code should move toward this rule:

> Browser code guides capture. Server code decides identity, quality, anti-spoof, and attendance.

## Product Surface Boundaries

### Kiosk

Entry points:

- `Controllers/KioskController.cs`
- `Views/Kiosk/Index.cshtml`
- `Scripts/kiosk*.js`
- `Scripts/kiosk/*.js`
- `Services/Recognition/AttendanceScanService.cs`

What it should own:

- walk-up attendance,
- visitor sign-in if enabled,
- kiosk-only admin unlock,
- camera guidance only.

What it must not own:

- employee registration,
- device registration,
- trusted browser recognition.

Current quality:

- Better after mobile face-box trust was removed.
- Still too much client-side "readiness" state. The kiosk scripts should not look like a second attendance engine.
- `Scripts/kiosk.js` still includes brightness/motion anti-spoof hints. Those are UI guidance only. Do not turn them into authority.

### Admin

Entry points:

- `Areas/Admin/Controllers/*.cs`
- `Areas/Admin/Views/**/*.cshtml`
- `Areas/Admin/Helpers/*.cs`
- `Models/ViewModels/Admin/*.cs`

What it should own:

- employee lifecycle,
- pending registration approval,
- re-enrollment,
- review queue,
- settings,
- operations/health.

Current quality:

- Controllers are mostly understandable but too large in places.
- Settings is bloated. `Areas/Admin/Views/Settings/Index.cshtml` is over 900 lines. That is not an admin settings page; that is a junk drawer with tabs.
- Settings now uses `AntiSpoof*` identifiers for code and anti-spoof wording for users. Keep that split: identifiers are code contracts; UI text should be human-readable.

### Mobile/Public

Entry points:

- `Controllers/Mobile/MobileEnrollmentController.cs`
- `Controllers/Mobile/MobilePortalController.cs`
- `Views/MobileRegistration/*.cshtml`
- `Scripts/mobile/mobile-enroll-page.js`
- `Services/Mobile/EmployeePortalService.cs`
- `Services/Security/AttendanceAccessReceiptService.cs`

What it should own:

- public registration,
- personal attendance scan,
- short-lived attendance receipt,
- employee attendance view/export after receipt.

What it must not own:

- persistent device registration,
- bypass identity trust,
- browser-side identity decisions.

Current quality:

- The concept is now clearer.
- The naming is still polluted by `DeviceService`. That service now does mobile detection, fingerprints, visitor session binding, and pending employee status. That name is lying.

## File-by-File Findings

### High Risk / High Complexity

| File | Problem | Verdict |
|---|---|---|
| `Services/Recognition/AttendanceScanService.cs` | 550+ lines, performs upload validation, office resolution, preprocessing, detection, anti-spoof, matching, visitor fallback, review flags, DB write, receipt issue, response building. | Split later into `AttendanceScanOrchestrator`, `ScanQualityService`, `AttendanceMatchService`, and `AttendanceReviewFlagger`. Do not add more logic here. |
| `Services/Biometrics/OpenVINOBiometrics.cs` | 800+ lines, owns model loading, pool management, detection, encoding, RGB conversion, landmark extraction, serialization helpers. | Needs internal split after worker migration: `OpenVINOModelPool`, `OpenVINOFaceDetector`, `OpenVINOFaceEncoder`. |
| `Services/Recognition/BiometricWorkerClient.cs` + `Services/Biometrics/OpenVinoBiometrics.cs` | Localhost worker client boundary for model inference. | Keep MVC focused on business decisions; do not reintroduce local model loading here. |
| `Areas/Admin/Views/Settings/Index.cshtml` | 900+ lines of settings UI, diagnostics, scripts, tabs. | Break into partials. This is too big for sane maintenance. |
| `Scripts/modules/enrollment-core.js` | 640+ lines and owns capture, scoring, good-frame selection, upload, server config, status UI callbacks. | It should own capture orchestration only. Server quality policy should drive thresholds. |
| `Scripts/kiosk/kiosk-location.js` | 580+ lines. Location permissions, map handling, route UI, and GPS state are packed together. | Split map UI from location acquisition. |
| `Scripts/admin/enroll-page.js` and `Scripts/admin/visitor-enroll-page.js` | Both 444 lines; 333 exact duplicate lines. | Consolidate immediately. This is copy-paste bloat. |
| `Views/MobileRegistration/Employee.cshtml` | 690+ lines; page, styles, display logic together. | Needs layout/partial extraction. Executive-friendly UI should not require a 700-line view. |

### Medium Risk / Needs Tightening

| File | Problem | Verdict |
|---|---|---|
| `Services/Biometrics/BiometricPolicy.cs` | Correct direction, but should become the only threshold API. | Keep. Move remaining policy reads here. |
| `Services/Biometrics/FastScanPipeline.cs` | Good performance path, now using worker anti-spoof result names consistently. | Keep. Do not add local model inference back into MVC. |
| `Services/Biometrics/FastFaceMatcher.cs` | Exact employee-level scoring is safer than nearest-vector indexing for government attendance. | Keep exact scoring. Do not reintroduce BallTree for attendance identity decisions without proof. |
| `Services/Biometrics/FaceIndexBase.cs` | Uses BallTree for face indexes. Fine for lookup tooling, not final attendance authority. | Keep but do not use it for authoritative attendance matching. |
| `Services/DeviceService.cs` | Name is now wrong. It handles fingerprinting, mobile detection, visitor binding, and employee status approval. | Rename/split later. The old "device registration" idea is dead and should not reappear. |
| `Services/JsonResponseBuilder.cs` | Central response builder is useful but has kiosk-specific methods. | Keep; avoid growing it into a dumping ground. |
| `Controllers/Api/ScanController.cs` | Looks like shared scan endpoint, but overlaps with mobile enrollment frame scan. | Consolidate with enrollment frame endpoint later. |
| `Controllers/Mobile/MobileEnrollmentController.cs` | Does too much: form rendering, frame scan, submit registration, identify. | Split public registration from public identify later. |
| `Areas/Admin/Controllers/EmployeesController.cs` | Handles CRUD, approval, status, enrollment route. | Acceptable short term, but approval logic belongs in a service with honest naming. |

### Low Risk / Mostly Fine

| File | Verdict |
|---|---|
| `Services/Biometrics/BiometricCrypto.cs` | Good single-purpose service. |
| `Services/Biometrics/BiometricTemplateMetadataService.cs` | Useful. Keep. |
| `Services/Biometrics/EnrollmentQualityGate.cs` | Small and readable. Keep. |
| `Services/Biometrics/DuplicateCheckHelper.cs` | Small and understandable. Keep. |
| `Services/Biometrics/RiskyPairAuditService.cs` | Useful admin safety tool. Keep. |
| `Services/Security/AdminPinService.cs` | Good direction: PBKDF2, env var, lockout. Keep. |
| `Services/Security/AttendanceAccessReceiptService.cs` | Correct concept for public post-scan access. Keep. |
| `Services/Security/PublicAuditService.cs` | Keep. Public endpoints need audit trails. |
| `Services/OperationalMetricsService.cs` | Good start. Extend with anti-spoof review/block counts later. |
| `Services/Recognition/BiometricWorkerContracts.cs` | Good contract layer for the MVC-to-worker client. Keep it focused on `/analyze-face`; do not add MVC-hosted worker stubs. |

### Dead / Cut

| File | Action |
|---|---|
| `Services/ErrorCodes.cs` | Removed. It was unused and created fake centralization. |

## Duplication Findings

### Enrollment JavaScript

`Scripts/admin/enroll-page.js` and `Scripts/admin/visitor-enroll-page.js`:

- 444 lines each.
- 333 exact duplicate lines.
- Difference is mostly wording, URLs, target count, and result handling.

This should be one page controller factory:

```text
createEnrollmentPage({
  subject: "employee" | "visitor",
  minFrames,
  saveUrl,
  successRedirect,
  labels
})
```

Do not create a third enrollment script. The next script should delete two.

### Registration / Scan Endpoints

Current overlap:

- `MobileEnrollmentController.ScanFrame`
- `Api/ScanController.Frame`
- `EnrollmentCaptureService.ExtractCandidates`
- `FastScanPipeline.EnrollmentScanInMemory`

This is too many ways to ask "is this frame good enough?" The server should expose one canonical frame-analysis service and controllers should only adapt HTTP.

### AntiSpoof / Anti-spoof Naming

Current reality:

- UI says anti-spoof.
- DB/API/code contracts use `AntiSpoof*` identifiers.
- Historical pre-rename biometric names are gone from active runtime files; they remain only in the database migration script that renames old columns/keys.

This is now the right split. Do not reintroduce old terminology unless the system actually performs a calibrated real-presence challenge.

## Trust Boundary Rules

### Browser

Allowed:

- camera start/stop,
- capture still JPEG,
- draw guide box,
- show progress,
- send client hints,
- show server result.

Not allowed:

- identity match,
- final face count authority,
- final anti-spoof authority,
- accepting browser face box for public/mobile scan.

### MVC Server

Allowed:

- attendance business rules,
- DB writes,
- review queue,
- receipts,
- audit,
- policy resolution.

Should not own long-term:

- direct model inference once worker is real.

### Biometric Worker

Allowed:

- detection,
- landmarks,
- embeddings,
- anti-spoof scoring,
- quality metrics.

Not allowed:

- attendance record writes,
- employee approval,
- admin state changes.

## Performance Findings

- Exact matching over 300 employees is fine. Stop overengineering this. 300 employees x 25 vectors x 128 dimensions is cheap on a real server.
- BallTree is not needed for authoritative attendance at this scale and can increase wrong-accept risk if used naively.
- The p95 target should be dominated by image decode, OpenVINO/recognizer inference, and anti-spoof inference, not DB.
- The biggest latency risk is doing too many model passes per scan, not the employee count.
- Live streaming frames would be dumb here. Submit selected still frames; retry if needed.

## Security Findings

- Browser face boxes are now correctly distrusted for mobile/public scans.
- Admin PIN is correctly env-based and hashed.
- Public attendance receipt is the right direction, but it must stay short-lived.
- Device registration tables and wording should be removed from user-facing flows. The new public model is not device registration.
- `DeviceService` fingerprinting is not identity. Treat it only as a rate-limit/session/audit helper.

## Cleanup Done In This Pass

- Removed unused `Services/ErrorCodes.cs`.
- Moved admin anti-spoof threshold persistence to `Biometrics:AntiSpoof:ClearThreshold`; old flat keys are cleaned on settings save.
- Updated admin settings labels from code-style names to anti-spoof wording where safe.
- Updated mobile enrollment threshold display to use `BiometricPolicy`.
- Verified build after each cleanup.

## Next Surgical Cuts

1. Consolidate `admin/enroll-page.js` and `admin/visitor-enroll-page.js`.
2. Split `Settings/Index.cshtml` into partials.
3. Rename/split `DeviceService` into:
   - `ClientFingerprintService`
   - `MobileRequestClassifier`
   - `EmployeeApprovalService`
   - `VisitorSessionBindingService`
4. Split `AttendanceScanService` into smaller internal services.
5. Run the database rename migration before deploying any build that expects `AntiSpoof*` columns.
6. Create a single server `FrameAnalysisService` used by public registration, admin enrollment, visitor enrollment, and scan preview.
7. Remove unused full/slim/esm vendor duplicates from the project file and publish output after confirming bundles only use minified files.

## Non-Negotiable Rules Going Forward

- No new JS page controller for enrollment.
- No new threshold outside `BiometricPolicy`.
- No browser-side identity authority.
- No live frame streaming.
- No new service unless an existing service cannot honestly own the behavior.
- No "centralized" helper unless at least two active callers use it immediately.
