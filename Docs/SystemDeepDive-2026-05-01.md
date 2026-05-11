# FaceAttend System Deep Dive - 2026-05-01

## Scope

I scanned the tracked project, key untracked files already referenced by the project, configuration, controllers, services, views, scripts, and deployment shape.

- Tracked files: 317
- Source/config/view files scanned: 253
- Approximate source/config/view lines: 77,806
- Requested `agent.md` / `claude.md`: not found in this repo or nearby parent directories.

This is an ASP.NET MVC 5 / .NET Framework 4.8 system using EF6, SQL Server, a localhost OpenVINO worker for embeddings/anti-spoof, MediaPipe/client face guidance, and server-side attendance logic.

## Executive Verdict

The app has the right broad idea: browser captures a still image, the worker produces anti-spoof and embedding signals, the matcher verifies identity, then attendance is recorded with device/location controls and review flags.

The current repo is not production-ready for a government attendance system yet.

The biggest blockers are:

1. The previous fast matching path could create employee A/B confusion once enough vectors existed. I fixed this by making attendance matching exact and employee-level.
2. Mobile anti-spoof was silently stricter than the configured threshold. I fixed the hidden `0.68` default that was likely blocking legitimate scans.
3. Mobile device-identification could issue a registration proof without server-side anti-spoof. I fixed that.
4. Production secrets are in `Web.config`. Rotate the database password. Do not deploy this as-is.
5. The app is Windows/.NET Framework/IIS-shaped, not Railway/Vercel-shaped. Free Railway is not a production target for this workload. Vercel serverless is the wrong target for this native biometric pipeline.
6. Build readiness depends on the external OpenVINO worker being deployed and healthy.

## Main User Flows

### 1. Public Kiosk / Personal Attendance

Entry point:

- `Controllers/KioskController.cs`
- `Services/Recognition/AttendanceScanService.cs`
- `Services/Biometrics/FastScanPipeline.cs`
- `Services/Biometrics/FastFaceMatcher.cs`
- `Services/AttendanceService.cs`

Flow:

1. Browser posts a photo, optional GPS, optional client face box, optional device token.
2. Server validates file size/type.
3. Office is resolved from GPS for mobile, or fallback office for kiosk.
4. Image is saved/preprocessed.
5. Face is detected, or trusted only as a hint from client box.
6. Anti-spoof and face encoding run in the fast pipeline.
7. Face vector is matched against enrolled employees.
8. Low-confidence/ambiguous matches are rejected.
9. Medium matches require a second consecutive same-device match.
10. Mobile scans require registered device validation.
11. Location anti-spoof checks run.
12. Attendance is written in a database transaction with gap checks.

Good:

- CSRF tokens exist on POST endpoints.
- Rate limiting exists on kiosk endpoints.
- Server, not client, decides anti-spoof and identity.
- There is a concurrency cap.
- Near-match, near-anti-spoof, and GPS warning review flags exist.

Bad:

- Public endpoints are intentionally exposed, so every client signal is untrusted.
- Device fingerprints are weak and spoofable. The secure identifier is the token, not the fingerprint.
- Device tokens are stored as bearer values. They should be hashed at rest.
- WFH/personal scan trust depends heavily on GPS plus device token. That is acceptable only with audit/review, not as "perfect proof".

### 2. Mobile Self Enrollment

Entry point:

- `Controllers/Mobile/MobileEnrollmentController.cs`
- `Services/Biometrics/EnrollmentCaptureService.cs`
- `Services/Biometrics/EnrollmentQualityGate.cs`
- `Services/Biometrics/DuplicateCheckHelper.cs`

Flow:

1. Employee enters employee data.
2. Client captures multiple face samples.
3. Server extracts embeddings, anti-spoof score, sharpness, pose, and quality.
4. Diverse embeddings are selected.
5. Duplicate face check runs against existing employees.
6. Employee is created as `PENDING`.
7. Pending device is created.
8. Admin must approve before active use.

Good:

- Pending status is correct for public registration.
- Multiple vectors per employee are a good idea.
- Diversity and quality gates reduce enrollment of near-identical samples.
- Duplicate detection exists.

Bad:

- Enrollment extraction still accepts `scan.Ok` frames even when anti-spoof/sharpness are weak, then relies on aggregate quality gates. For production, use hard rejects for failed anti-spoof and sharpness after tuning the threshold.
- Duplicate detection was only loading 5 vectors per employee. I fixed it to use `Biometrics:Enroll:MaxStoredVectors`.
- The face index used an old config key. I fixed it.

### 3. Mobile Device Registration / Identify Employee

Entry point:

- `Controllers/Mobile/MobileEnrollmentController.cs`
- `Services/DeviceRegistrationProofService.cs`
- `Services/DeviceService.cs`

Flow:

1. Employee scans face on mobile.
2. Server identifies employee.
3. Server issues a short-lived proof cookie.
4. Device registration uses the proof.

Previous problem:

- This path used plain OpenVINO detection/encoding and did not run server-side antiSpoof.
- It also used a looser tolerance path than mobile attendance.

Fix applied:

- Identify now uses `FastScanPipeline.ScanInMemory`.
- It enforces mobile antiSpoof.
- It uses the mobile attendance tolerance clamp.
- It rejects ambiguous matches.

### 4. Admin

Entry point:

- `Areas/Admin/Controllers/*`
- `Filters/AdminAuthorizeAttribute.cs`
- `Services/Security/AdminPinService.cs`
- `Services/Security/TotpService.cs`

Good:

- Admin session/PIN/TOTP direction is reasonable.
- Audit helpers exist.
- Review, operations, attendance reports, and settings surfaces exist.

Bad:

- If this is government production, PIN-only style login is not enough unless it is backed by strong policy and device/location controls.
- Admin actions need stricter audit trail, immutable event logs, and export signing.

### 5. Visitor

Visitor support exists and shares face matching infrastructure. For an employee attendance system, visitor mode is extra blast radius. If not actively needed, disable it for first production rollout.

## Matching Deep Dive

### What was wrong

`FastFaceMatcher` used two different algorithms:

- Below the BallTree threshold: exact employee-level matching.
- Above the threshold: nearest single-vector lookup.

That is dangerous. At real employee counts, the behavior changed right when correctness matters more.

The old BallTree path:

- Returned the nearest vector, not the best employee-level score.
- Computed "second best" from the same employee's other vectors in common cases.
- Could accept a nearest vector while another employee was nearly tied.
- Could become stale after incremental employee updates because the BallTree was not rebuilt by `UpdateEmployee`.

For +300 employees, this optimization is not worth it. Even 300 employees x 25 vectors x 128 dimensions is tiny compared with OpenVINO/ONNX inference.

### What changed

Attendance matching now:

- Scores every employee exactly.
- Computes each employee score as average of top 3 vector distances.
- Tracks the true second-best employee.
- Rejects close/ambiguous matches.
- Keeps the high/medium/low tier model.

This is slower only in theory. In practice, it should be fast enough and more trustworthy.

### What still needs validation

You cannot honestly claim 99% accuracy until you measure:

- False accept rate by threshold.
- False reject rate by threshold.
- Ambiguous match rate.
- Same-day repeated scan reliability.
- Worst-case lookalike/coworker pair behavior.
- Performance p50/p95/p99 on the real hosting target.

The next high-value tool is a "candidate pair audit": for every employee vector, find the closest other employee and show risky pairs before rollout.

## AntiSpoof / Anti-Spoof Deep Dive

The old local ONNX anti-spoof scorer has been removed from MVC. Anti-spoof now belongs behind the localhost OpenVINO worker boundary.

The current architecture is right: one still image, server-side anti-spoof, no server-side live video stream.

Do not live-stream frames to the server. That is a bad idea here. It increases bandwidth, CPU, privacy exposure, storage risk, and abuse surface. It also does not solve mismatches. Use client-side camera guidance plus one server-verified still image, with a second still only for medium-confidence retry.

The likely mobile blocking bug was this:

- Global configured antiSpoof threshold: `0.45`
- Mobile attendance hidden fallback: `0.68`
- Missing config key: `Biometrics:MobileAntiSpoofThreshold`

That meant mobile could be much stricter than settings implied.

Fix applied:

- Added `Biometrics:MobileAntiSpoofThreshold=0.45`.
- Mobile attendance falls back to global threshold instead of `0.68`.
- Fast pipeline fallback defaults to `0.45`.
- Enrollment fallback defaults to `0.45`.

Still needed:

- Log antiSpoof score distributions for real employees for at least several days.
- Separate "model failed" from "face looks spoofed" in user/admin diagnostics.
- Keep antiSpoof fail-closed for public BYOD once the model is stable.
- Add admin threshold calibration UI using real p50/p95 scores.

## Trust Boundaries

### Browser/device boundary

Untrusted:

- Uploaded image
- Client face box
- GPS coordinates
- GPS accuracy
- User-agent/mobile headers
- Screen dimensions
- Device name
- Local storage
- Any client timing data

Trusted only after server verification:

- Face embedding generated server-side
- AntiSpoof score generated server-side
- Device token if found in HttpOnly cookie and matched server-side
- Admin session after server-side auth

### Database boundary

Database contains:

- Employees
- Face embeddings
- Attendance logs
- Archived legacy devices
- Admin/audit data

Initial critical problem:

- `Web.config` contained live-looking SQL credentials.
- Connection string used a certificate trust bypass.

Local config has been cleaned, but this is not production proof. Rotate the exposed DB password on the real SQL Server and keep production secrets in environment/IIS protected configuration.

### Server/model boundary

Model files under `App_Data/models` are part of the trust boundary. If someone can replace the ONNX model or OpenVINO model files, they can change security behavior.

Production needs:

- Locked ACLs on model files.
- Hash verification at startup.
- Deployment artifact integrity checks.

## Security Findings

Critical:

- Hardcoded DB credentials in `Web.config`.
- HTTPS enforcement is commented out in `FilterConfig`.
- `compilation debug="true"` in base `Web.config`.
- Device token values are stored in DB as raw bearer secrets.
- Build artifacts/native dependencies are missing, so deployment is not reproducible.

High:

- Request signing filter is fail-open and not globally registered.
- CSP defaults to report-only unless release transform/config says otherwise.
- Public registration can be spammed without stronger abuse controls.
- DPAPI/LocalMachine biometric encryption can lock data to one Windows machine. That is dangerous if you move hosts without a key migration plan.

Medium:

- Visitor mode increases scope if not needed.
- InProc session is fragile behind multiple instances.
- GPS anti-spoofing is helpful but not proof. Treat it as risk scoring.
- Admin auth should move toward stronger identity and audit if this becomes official government use.

## Performance Findings

The slow parts are:

1. Image decode/preprocess.
2. OpenVINO detection/encoding.
3. Worker detection/recognition/anti-spoof inference.
4. Database write/report queries.

The matcher is not the bottleneck at 300 employees. Exact matching is the right trade.

Recommended production targets:

- p50 scan: under 2s
- p95 scan: under 5s
- p99 scan: under 8s
- Reject ambiguous matches quickly and clearly.
- Never let one slow anti-spoof/model request block all workers.

Needed:

- Real p95/p99 metrics dashboard.
- Model warm-up at app start.
- Health endpoint that checks model load, DB, cache age, and write readiness.
- Queue/report jobs for heavy admin exports.
- Avoid scaling out until session, cache, and DPAPI/key handling are designed for it.

## Deployment Reality

### Vercel

Bad fit for this repo.

Official Vercel function runtimes are Node.js, Bun, Python, Rust, Go, Ruby, Wasm, and Edge. This repo is .NET Framework 4.8 MVC with native Windows biometric dependencies.

Even if rewritten, serverless cold starts and native model loading are why you saw slow verification. Vercel is good for frontend and lightweight APIs, not this current biometric worker.

### Railway

Railway builds/deploys services into containers through Railpack or Dockerfile. This repo has no Dockerfile/Railway config and is .NET Framework/IIS/Windows-oriented.

Railway Free is not acceptable for production biometric attendance. Current public pricing shows Free starts with a 30-day $5 credit, then $1/month, with up to 1 vCPU / 0.5 GB RAM per service after trial. That is demo territory for this workload.

### Recommended

Best near-term path:

- Windows Server/IIS VPS or on-prem Windows Server.
- SQL Server with proper backups.
- HTTPS termination.
- Secrets outside repo.
- Scheduled backup and restore drill.
- One app instance until session/key handling is ready.

Best long-term path:

- Rewrite/migrate to .NET 8 Linux container or split biometric worker service.
- Use a managed database.
- Store biometric templates with portable envelope encryption.
- Deploy with health checks, logs, metrics, and rollback.

## UI / UX Findings

The boss/regional director requirement is valid: administrative pages should not feel like a long website. They should be fixed-shell operational screens.

Changes applied:

- Mobile layout now has a real header, constrained content area, and footer.
- Registration page Step 1 and Step 3 are denser and constrained to the viewport.
- Registration form spacing, labels, inputs, review cards, and actions are compacted.
- Camera capture remains full-screen.

Still needed:

- Admin dashboard should be one-screen, with drill-down pages only when needed.
- Reports should default to summary cards/tables, not long stacked panels.
- Registration should remain step-based, not one giant form.
- Kiosk should stay full-screen and distraction-free.

## Quality-of-Life Additions Worth Building

High-value:

- Near-match review queue with face distance, second-best employee, anti-spoof score, GPS, device, and photo thumbnail.
- Risky employee pair detector after enrollment.
- Admin anti-spoof calibration dashboard.
- "Why rejected?" operator diagnostics: no face, anti-spoof low, ambiguous, device mismatch, GPS fail, duplicate.
- Device replacement workflow with old/new device comparison.
- Daily health dashboard: DB, model loaded, matcher cache age, scan p95, failure rates.
- Employee self-service status page: enrolled, pending, device pending, last successful scan.
- Bulk import employees/offices with validation before commit.
- Audit export with signed CSV/PDF hash.
- Rollback-safe deployment checklist.

Avoid:

- Server live-streaming camera frames.
- Free-tier production deployment.
- More thresholds without measurement.
- More UI screens before fixing trust, metrics, and deployment.
- Treating GPS or browser fingerprint as proof.

## Changes Applied In This Pass

- `Services/Biometrics/FastFaceMatcher.cs`
  - Removed attendance BallTree shortcut.
  - Exact employee-level scoring with true second-best employee.
  - Added raw best and second-best metadata.

- `Services/Recognition/AttendanceScanService.cs`
  - Mobile antiSpoof threshold now uses configured mobile threshold or global threshold.
  - Ambiguous mobile scans no longer offer self-enrollment.
  - Medium WFH pending scan no longer crashes on null office.

- `Web.config`
  - Added `Biometrics:MobileAntiSpoofThreshold=0.45`.

- `Services/Biometrics/FastScanPipeline.cs`
  - Default antiSpoof threshold aligned to `0.45`.

- `Services/Biometrics/EnrollmentCaptureService.cs`
  - Enrollment fallback antiSpoof threshold aligned to `0.45`.

- `Services/Biometrics/EmployeeFaceIndex.cs`
  - Fixed stale config key to use `MaxStoredVectors`.

- `Services/Biometrics/DuplicateCheckHelper.cs`
  - Duplicate checks now inspect the configured number of stored vectors.

- `Controllers/Mobile/MobileEnrollmentController.cs`
  - Mobile identify/device proof now requires server antiSpoof.
  - Mobile identify uses stricter mobile tolerance.
  - Ambiguous identify is rejected.

- `Services/AttendanceService.cs`
  - Attendance transaction now uses `Serializable`, matching the code's stated intent.

- `Views/Shared/_MobileLayout.cshtml`
  - Added mobile header/footer and constrained main content.

- `Views/MobileRegistration/Enroll-mobile.cshtml`
  - Made registration denser and less scroll-heavy.

## Verification

Passed:

- `git diff --check` on edited files.

Blocked:

- Full Visual Studio MSBuild fails because these native files are missing from `packages/OpenVINO worker.1.3.0.7/runtimes/win-AnyCPU/native/`:
  - `OpenVINO workerNativeDnnAgeClassification.dll`
  - `OpenVINO workerNativeDnnGenderClassification.dll`

This is a deployability blocker unrelated to the logic changes. Do not deploy until the package restore/native dependency story is reproducible.

## Go / No-Go

No-go for production today.

Minimum before real employees:

1. Rotate leaked DB credentials and move secrets out of repo.
2. Fix reproducible build/package restore.
3. Enforce HTTPS.
4. Test 30 to 50 real employees over several days.
5. Generate threshold report and risky-pair report.
6. Validate antiSpoof false rejects on real devices.
7. Run restore drill for DB and biometric templates.
8. Freeze features. Stop adding screens until reliability is measured.
