# FaceAttend Stabilization Tracker - 2026-05-01

## Current Status

Local stabilization is in progress. The app now builds locally, device registration is no longer part of attendance authorization, public monthly attendance access is receipt-gated, public enrollment has harder biometric rejection, and the admin operations area has a risky-pair audit.

This does not yet prove production readiness or 99% accuracy. That requires real employee calibration, spoof/antiSpoof testing, IIS deployment drills, backup restore proof, and shadow pilot metrics.

## Completed Locally

- Removed leaked SQL credential from `Web.config`; production connection strings must come from IIS/environment config.
- Enabled production `debug=false`, HTTPS enforcement config, enforcing CSP, and `TrustServerCertificate=False`.
- Removed seeded employee/device/attendance sample inserts from the DB script.
- Restored the local OpenVINO native DLL build blocker so `dotnet build` succeeds.
- Removed live attendance dependency on public device registration and obsolete proof cookies.
- Added receipt-gated `POST /Attendance/Scan`, `GET /Attendance/MyMonth`, and `GET /Attendance/ExportMyMonth`.
- Added `RecognitionDecisionDto` metadata on attendance success and enrollment frame scan responses.
- Added exact employee-level matching with second-best employee and ambiguity gap tracking.
- Added `RiskyPairAuditService` and admin `Operations/RiskyPairs` view for closest cross-employee vector audit.
- Added hard public enrollment rejection for faces too close to an existing active/pending employee.
- Added rate limits for mobile enrollment/identify public endpoints.
- Added public audit events for scan success/fail, enrollment submission, monthly view, and monthly export.
- Added readiness visibility for DB write readiness, model version, matcher cache age, and optional worker health.
- Visitor mode remains disabled by default in config.
- Added idempotent `BiometricTemplates` metadata migration without regenerating the EF model.
- Added raw-SQL template metadata writes for new enrollments and status changes.
- Added model file SHA-256 integrity reporting to readiness and operations.
- Added localhost-only biometric worker contract endpoints for health/model-info plus explicit 501 placeholders for extract/match/antiSpoof.
- Added admin calibration CSV export combining risky pairs and attendance match distances.
- Removed obsolete device-name requirement from public mobile enrollment.
- Added model hash/integrity plumbing for the OpenVINO worker model directory in `Web.config`; hashes should be pinned after final model selection.
- Added model ACL validation to health and operations so writable model files/folders are visible before deployment.
- Added attendance `Source` recording for kiosk/mobile scans and a 14-day threshold calibration summary on Operations.
- Added admin rate limits for operations, employee mutation/approval, attendance review/export, and review queue actions.
- Added admin audit events for attendance CSV, summary CSV, and review queue exports.
- Added `Docs/Database/20260501_remove_legacy_device_tokens.sql` to wipe obsolete plaintext device bearer tokens.
- Removed obsolete `DeviceToken` parameters and kiosk JavaScript token helpers from the live scan path.
- Added `DatabaseMigrationStatusService` and health/Operations visibility for stabilization migration readiness.
- Added `Docs/Database/20260501_verify_stabilization_migrations.sql` for post-migration SQL proof.
- Added `Database:RequireStabilizationMigrations`; leave warn-only locally, set true on IIS after migrations are applied.
- Removed obsolete kiosk/mobile device-registration endpoints instead of keeping 410 stubs.
- Deleted obsolete `kiosk-device.js` and removed it from kiosk bundles/project content.
- Removed device-registration view model, unused mobile enrollment view model, stale device status client state, and stale device-registration copy.
- Centralized recognition DTO construction in `RecognitionDecisionFactory` so attendance and enrollment frame checks use one metadata builder.

## Still Open

- Rotate the exposed production SQL credential outside the repo.
- Make native biometric runtime reproducible from a clean clone or replace OpenVINO with the worker path.
- Verify IIS HTTPS binding, HSTS, app pool identity, protected config, ACLs, and release transform on the real server.
- Drop the archived `Devices` table/entity from schema and EF model if the department accepts no rollback archive.
- Formalize the worker `Extract`, `Match`, `AntiSpoof`, `Health`, and `ModelInfo` API and move inference behind localhost-only auth.
- Benchmark OpenVINO detector/recognizer/anti-spoof candidates, including ArcFace-class ONNX where useful, then test worker latency on the real IIS CPU.
- Run the biometric template metadata migration on the IIS SQL database.
- Run the legacy device token cleanup migration on the IIS SQL database.
- Calibrate kiosk/mobile thresholds with real employee captures.
- Build a shadow-mode pilot report: zero wrong auto-records, retry rate, p95 kiosk latency, p95 mobile latency.
- Finish fixed-shell/no-scroll UI cleanup for registration and executive dashboard.
- Verify production model hashes after final model files are deployed, lock model ACLs, then set `Biometrics:RequireModelReadOnlyAcl=true`.
- Run backup restore, app pool recycle, power/network outage, and rollback drills.

## Next Batch

1. Run `Docs/Database/20260501_biometric_template_metadata.sql` and `Docs/Database/20260501_remove_legacy_device_tokens.sql` against a staging copy.
2. Run `Docs/Database/20260501_verify_stabilization_migrations.sql` and confirm Operations/health show database migrations OK.
3. Lock model folder ACLs on IIS, then turn on `Biometrics:RequireModelReadOnlyAcl=true` and `Database:RequireStabilizationMigrations=true`.
4. Decide whether to physically drop `Devices` from SQL/EF now that live routes and scripts are gone.
5. Implement the real localhost worker process behind the contract endpoints, then continue fixed-shell/no-scroll UI cleanup.

## Verification

- `dotnet build .\FaceAttend.sln --no-restore -v:minimal` passes with 0 warnings and 0 errors.
- Static source scan found no old leaked SQL credential markers, no insecure trust-certificate setting, no obsolete device-proof service, no removed device-required response helper, and no live device-token validation calls.
- Static source scan found no remaining live `KioskDevice`, device-registration route stubs, device-status client state, or duplicate recognition DTO construction outside `RecognitionDecisionFactory`.
