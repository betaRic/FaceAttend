# FaceAttend Full System Deep Dive - 2026-05-06

## Scope

This pass reviewed the repo instructions, docs, controllers, services, biometric flow, public scan flow, mobile registration, kiosk path, admin surfaces, database script, configuration, UI layout, and deployment posture.

Requested `agent.md` / `claude.md`: not present in this repo in any casing found by scan.

Build verification after changes:

```powershell
dotnet build .\FaceAttend.sln --no-restore -v:minimal
```

Result: build passed with 0 warnings and 0 errors.

## Executive Verdict

The system is much better than a toy demo, but it is not automatically production-trustworthy just because recognition now works on your phone and PC.

The core architecture is basically right:

- Browser captures a still image.
- Server performs antiSpoof and embedding.
- Server matches against enrolled templates.
- Server records attendance with review metadata.
- Admin sees health, operations, review, calibration, and attendance reports.

The biggest risk is not "cannot recognize employees." The biggest risk is a false accept: employee A recorded as employee B. For a government attendance system, a false accept is worse than a false reject. A false reject annoys someone. A false accept corrupts an official record.

The matching path has already moved in the right direction: exact employee-level matching, true second-best tracking, ambiguity rejection, and medium-confidence two-scan confirmation. That is the correct tradeoff for 300 employees. Do not chase micro-optimizations that weaken identity correctness.

## Main Data Flow

### Public mobile / kiosk attendance

Files:

- `Controllers/KioskController.cs`
- `Controllers/AttendanceController.cs`
- `Services/Recognition/AttendanceScanService.cs`
- `Services/Biometrics/FastScanPipeline.cs`
- `Services/Biometrics/FastFaceMatcher.cs`
- `Services/AttendanceService.cs`

Flow:

1. Browser captures one JPEG frame.
2. POST includes image, anti-forgery token, optional GPS, optional client face box.
3. Server validates image type and size.
4. Server resolves office from GPS for mobile/personal use, or fallback office for kiosk.
5. Server decodes image and detects face.
6. Server sends the frame to the OpenVINO worker for detection, embedding, recognition candidates, and anti-spoof scoring.
7. Server matches employee vector exactly across all employees and stored vectors.
8. Ambiguous/low matches are rejected.
9. Medium tier requires a second same-device scan.
10. Location anti-spoof checks add block or review flags.
11. Attendance record is written with biometric decision metadata.
12. A short-lived attendance receipt allows the employee to view/export their own month.

Blunt take:

- One still image is the right unit. Do not live-stream frames to the server. It wastes bandwidth, increases privacy risk, makes abuse easier, and does not solve false accepts.
- The server must distrust every browser signal except the posted pixels after server-side validation.
- Mobile/personal scans must reject multiple faces. Guessing largest face is not acceptable for personal attendance.

Change made in this pass:

- Mobile/personal scans now ignore client-provided face boxes and force server-side detection.
- Mobile/personal scans now reject multi-face frames instead of choosing the largest face.
- Kiosk can still use client face box as a speed hint.

### Mobile self-enrollment

Files:

- `Controllers/Mobile/MobileEnrollmentController.cs`
- `Services/Biometrics/EnrollmentCaptureService.cs`
- `Services/Biometrics/EnrollmentQualityGate.cs`
- `Services/Biometrics/DuplicateCheckHelper.cs`
- `Services/Biometrics/BiometricTemplateMetadataService.cs`

Flow:

1. Employee enters identity details.
2. Browser captures multiple face frames.
3. Server extracts anti-spoof score, sharpness, face area, pose, landmarks, and embedding.
4. Server rejects failed anti-spoof, low sharpness, tiny face area, and extreme pose.
5. Server selects diverse embeddings.
6. Server checks closest existing active/pending employee.
7. Duplicate/risky pairs are rejected.
8. Employee is saved as `PENDING`.
9. Admin approves before live use.

Blunt take:

- Public self-enrollment is necessary for convenience, but it is an abuse surface. Rate limits and pending approval are non-negotiable.
- Do not accept a self-enrolled employee as production-ready without admin review and risky-pair audit.
- Keep public registration compact, but not at the expense of clear failure reasons.

### Mobile identify

Files:

- `Views/MobileRegistration/Identify.cshtml`
- `Controllers/Mobile/MobileEnrollmentController.cs`

Flow:

1. Employee opens identify page.
2. Browser captures one frame.
3. Server runs the same antiSpoof and matching path.
4. Active employee receives scan URL.

Change made in this pass:

- Removed unused jsDelivr MediaPipe loader from identify page. It was dead code, violated the self-hosted deployment posture, and conflicted with CSP.

### Admin

Files:

- `Areas/Admin/Controllers/*`
- `Filters/AdminAuthorizeAttribute.cs`
- `Services/Security/AdminPinService.cs`
- `Services/Security/TotpService.cs`
- `Services/AuditHelper.cs`

Current posture:

- PIN hash uses PBKDF2.
- IP allowlist exists.
- TOTP exists but is disabled by default.
- Admin audit exists.
- Security headers exist.
- HTTPS is enabled by config.

Blunt take:

- PIN-only is weak for a government production system. Turn on TOTP before real rollout.
- `Admin:AllowedIpRanges` is empty in local config. For production, set it to the actual office/admin subnets.
- InProc session requires one IIS worker. Do not scale horizontally until session, cache, and biometric encryption keys are redesigned.

## Matching Deep Dive

Current matcher:

- Loads active employee vectors into memory.
- Scores each employee by average of top 3 vector distances.
- Tracks true second-best employee.
- Rejects ambiguous matches.
- Classifies High, Medium, Low.
- Medium requires two consistent scans.

This is the right direction.

Why A/B mismatch happens:

- Loose thresholds.
- Similar employees/risky pairs.
- Poor enrollment vectors.
- Multiple faces in a mobile frame.
- Over-trusting client face boxes.
- Treating a single nearest vector as identity.
- Not measuring second-best gap.

What is fixed or improved:

- Exact employee-level scoring beats nearest-vector shortcuts.
- Ambiguity gap blocks close A/B pairs.
- Risky-pair audit exists.
- Public enrollment rejects duplicate/risky faces.
- Mobile/personal scans now reject multi-face frames.

What still needs real validation:

- False accept rate at current thresholds.
- False reject rate per office/device.
- AntiSpoof false reject rate.
- Closest-pair list for all active/pending employees.
- p50/p95/p99 scan latency on the real IIS machine.

Do not claim 99% accuracy until this is measured on real employees. That would be bullshit. The honest claim today is: the architecture now has the controls needed to measure and reduce false accepts.

## Trust Boundaries

Untrusted:

- Browser face box.
- Browser mobile/desktop detection.
- GPS coordinate.
- GPS accuracy.
- User agent.
- Device fingerprint.
- Uploaded filename/content type.
- Any client-side face detection result.

Partially trusted after server checks:

- Image bytes after validation.
- Server-generated antiSpoof score.
- Server-generated embedding.
- Server-side match decision.
- Anti-forgery token for browser-origin POST protection.
- Attendance receipt only for short-lived self-viewing, not identity proof.

High-value production controls:

- HTTPS only.
- Admin TOTP.
- Admin IP allowlist.
- Model hash verification.
- Read-only model ACL.
- SQL backups and restore drill.
- Immutable audit export.
- Calibration report before rollout.

## Security Findings

Critical before production:

- Set production DB connection outside the repo via IIS/environment/protected config.
- Rotate any password that was ever committed or pasted into earlier configs.
- Turn on TOTP.
- Set admin IP allowlist.
- Lock model folder ACLs and enable `Biometrics:RequireModelReadOnlyAcl=true`.
- Run stabilization migrations and set `Database:RequireStabilizationMigrations=true`.

High:

- `Biometrics:SkipAntiSpoof` exists in code. It must remain false in production. Ideally remove or make it impossible outside local debug.
- Public scan/enrollment endpoints are open by design. Keep rate limits conservative.
- GPS is not proof. It is only one risk signal.
- Browser fingerprint is not proof. It is only one risk signal.
- DPAPI-style protected biometric storage must have a backup/migration plan before moving servers.

Medium:

- Visitor mode is disabled by default. Keep it disabled for first employee rollout unless truly required.
- CSP still needs browser QA after every UI change because inline scripts are common.
- InProc rate limiting and sessions reset on app recycle.

## Performance Findings

The bottleneck is not matching 300 employees. The bottleneck is image decode, OpenVINO, ONNX, and database writes.

For 300 employees:

- Exact matching is acceptable.
- Worker concurrency should be tied to CPU cores, not set blindly high.
- Start with `Kiosk:MaxConcurrentScans=4` and worker-side concurrency sized to the real CPU, then measure.
- `Kiosk:MaxConcurrentScans=4` is sane for a first rollout.

Targets:

- p50 attendance scan under 2 seconds.
- p95 under 5 seconds.
- p99 under 8 seconds.
- zero known false accepts during pilot.
- retry rate visible by office/device.

## OpenVINO / Vladmandic Human

Do not swap models because the name sounds faster.

OpenVINO is worth testing as an inference backend or worker path if the deployment CPU supports it. It is strongest when you move to ONNX/OpenVINO-friendly detection/recognition models, not when trying to patch a .NET Framework/OpenVINO stack in place.

`vladmandic/human` is strong for browser-side detection/landmarks, but it is not a good source of truth for government attendance identity. Browser-side recognition is easier to tamper with and harder to audit. Use browser models for guidance only, not final identity.

Best path:

1. Keep current OpenVINO path stable for pilot.
2. Add a localhost-only biometric worker behind the existing worker contract.
3. Benchmark candidate stack: SCRFD/RetinaFace detection plus ArcFace-class recognition, ONNX Runtime CPU, then OpenVINO execution provider.
4. Run side-by-side shadow decisions against the current OpenVINO decisions.
5. Migrate only if false accept/false reject and latency improve on real employee data.

## UI / UX Findings

Good:

- Mobile layout now has header, content shell, and footer.
- Registration is step-based.
- Camera capture is full-screen.
- Visitor/device-registration bloat has been reduced.

Still needs work:

- Admin dashboard should become a fixed operational console, not a scroll page.
- Registration Step 1 still scrolls on small phones, but it is bounded inside the content shell. That is acceptable only if the boss requirement is mostly for admin/executive screens.
- Identify success still scrolls to result. This is okay on mobile, but for executive pages avoid it.

Executive-friendly admin target:

- Top nav fixed.
- KPI strip fixed.
- Main table area scrolls internally.
- Footer fixed with health/build/version.
- No giant cards.
- No marketing hero layouts.
- Reports open as filtered tables/export views, not long pages.

## Quality-of-Life Additions

Build these before adding cute features:

- Risky pair dashboard with employee A/B, distance, second-best gap, enrollment date, and re-enroll action.
- Calibration dashboard showing distance/anti-spoof distributions for accepted and rejected scans.
- "Why rejected?" panel for employees and admins: no face, multiple faces, anti-spoof low, ambiguous, GPS fail, too soon, system busy.
- Shadow-mode pilot report: attempts, accepts, rejects, retries, ambiguous, anti-spoof fails, GPS fails, p95 latency.
- Daily operations screen: DB write ready, model hashes, model ACL, cache age, pending review count, p95 latency.
- Bulk employee import with validation preview.
- One-click re-enrollment workflow for risky employees.
- Export signing/hash for attendance CSVs.
- Backup and restore drill checklist with last successful proof timestamp.

Avoid:

- Server-side live frame streaming.
- Browser-side recognition as final identity.
- Free-tier hosting.
- More thresholds without measurement.
- More pages before metrics and pilot proof.

## Changes Made In This Pass

- `Services/Biometrics/FastScanPipeline.cs`
  - Mobile/personal scans ignore client face boxes.
  - Mobile/personal scans reject multi-face frames instead of choosing largest face.

- `Services/Recognition/AttendanceScanService.cs`
  - Mobile attendance no longer uses client face box as detection authority.
  - Fast pipeline receives client box only for non-mobile kiosk scans.

- `Views/MobileRegistration/Identify.cshtml`
  - Removed unused external MediaPipe CDN loader.

## Current Go / No-Go

No-go for full production today.

Go for controlled pilot only if:

- Real IIS HTTPS is configured.
- Admin TOTP and IP allowlist are enabled.
- Migrations are applied and verified.
- Model ACLs are locked.
- Real employee calibration is run.
- Risky-pair audit is reviewed.
- Backup restore is proven.
- Pilot starts with shadow metrics and manual review queue.

Minimum pilot success metric:

- 0 confirmed wrong-employee records.
- p95 scan latency under 5 seconds.
- Ambiguous/retry rate understood and acceptable.
- AntiSpoof false reject rate low enough for daily use.
