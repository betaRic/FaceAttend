# 🚀 FaceAttend Deployment Checklist

## Pre-Deployment Checklist

### 1. Database Setup
- [ ] Run database optimization script
  ```sql
  -- Execute: Scripts/Sql/DatabaseOptimization.sql
  ```
- [ ] Run biometric template metadata migration
  ```sql
  -- Execute: Docs/Database/20260501_biometric_template_metadata.sql
  ```
- [ ] Remove obsolete legacy device bearer tokens
  ```sql
  -- Execute: Docs/Database/20260501_remove_legacy_device_tokens.sql
  ```
- [ ] Verify stabilization migrations
  ```sql
  -- Execute: Docs/Database/20260501_verify_stabilization_migrations.sql
  ```
- [ ] Verify all indexes created
- [ ] Test connection to database
- [ ] Verify AdminAuditLogs table exists
- [ ] Verify BiometricTemplates table exists and backfill count matches enrolled employees
- [ ] Verify no remaining non-empty `Devices.DeviceToken` values
- [ ] Verify `/Kiosk/RegisterDevice`, `/Kiosk/CheckDeviceStatus`, and `/MobileRegistration/RegisterDevice` are not live product routes

### 2. Environment Configuration

#### IIS Application Pool
- [ ] Managed Runtime: `.NET CLR Version v4.0`
- [ ] Enable 32-bit: `false`
- [ ] Maximum Worker Processes: `1` (for in-memory face index)
- [ ] Idle Timeout: `20 minutes`

#### Environment Variables (IIS → Configuration Variables)
| Variable | Value | Notes |
|----------|-------|-------|
| `FACEATTEND_ADMIN_PIN_HASH` | `<pbkdf2 hash>` | **REQUIRED** - Generate via AdminPinService.HashPin() |

#### Web.config Settings
- [ ] Connection String: Pooling=True, Min Pool Size=5, Max Pool Size=100
- [ ] Admin:SessionMinutes = 30
- [ ] Admin:TotpEnabled = false (or true after testing)
- [ ] Biometrics:Worker:BaseUrl points to localhost worker
- [ ] Biometrics:Worker:AnalyzeTimeoutMs = 5000 or tuned from pilot latency
- [ ] Biometrics:AntiSpoof:*Threshold values are calibrated from pilot samples
- [ ] Biometrics:ModelHashes = `<filename>=<sha256>;...` after model files are finalized
- [ ] Biometrics:RequireModelReadOnlyAcl = true after model folder ACLs are locked
- [ ] Database:RequireStabilizationMigrations = true after the migration verification script is clean

### 3. File & Folder Permissions
- [ ] App_Data folder: Read/Write for application pool
- [ ] Logs folder: Write for application pool
- [ ] Temp folder: Read/Write for application pool
- [ ] App_Data\models folder and model files: Read-only for application pool, no write/modify/delete rights

### 4. SSL/TLS Configuration
- [ ] HTTPS enabled on site
- [ ] TLS 1.2 minimum
- [ ] Valid SSL certificate installed
- [ ] HTTP to HTTPS redirect configured

---

## Post-Deployment Checklist

### 1. Initial Verification
- [ ] Health endpoint returns 200: `GET /health`
- [ ] Response includes: database: true, biometricWorkerReady: true, worker.healthy: true
- [ ] Response includes: writeReady: true, modelIntegrity.ok: true, modelIntegrity.aclOk: true, and model hash entries
- [ ] Response includes: databaseMigrations.ok: true with zero missing templates and zero legacy device tokens
- [ ] Disk space check passes

### 2. Admin Access Test
- [ ] Navigate to `/Kiosk?unlock=1`
- [ ] Enter PIN → Should redirect to admin dashboard
- [ ] Check dashboard KPIs load correctly

### 3. TOTP 2FA Setup (Optional but Recommended)
- [ ] Go to Admin → Settings
- [ ] Click "Enable 2FA"
- [ ] Scan QR with Google Authenticator
- [ ] Save recovery codes securely
- [ ] Test login with TOTP code

### 4. Employee Registration Test
- [ ] Register new employee via mobile enrollment
- [ ] Approve pending employee in Admin panel
- [ ] Verify employee appears in employee list

### 5. Attendance Scan Test
- [ ] Test kiosk scan with registered employee
- [ ] Verify attendance logged in Admin → Attendance
- [ ] Test IN/OUT toggle logic

### 6. Performance Check
- [ ] Verify scan time < 500ms for 100 employees
- [ ] Check /health endpoint shows face index loaded
- [ ] Monitor memory usage under load

---

## Configuration Reference

### Key Configuration Keys

| Category | Key | Default | Description |
|----------|-----|---------|-------------|
| **Security** | `Admin:PinMaxAttempts` | 5 | Max failed PIN attempts before lockout |
| | `Admin:PinLockoutSeconds` | 300 | Lockout duration after max attempts |
| | `Admin:SessionMinutes` | 30 | Admin session expiry |
| | `Admin:TotpEnabled` | false | Enable TOTP 2FA |
| **Biometrics** | `Biometrics:Worker:AnalyzeTimeoutMs` | 5000 | Worker scan timeout in milliseconds |
| | `Biometrics:AntiSpoof:ClearThreshold` | 0.45 | Anti-spoof clear-pass threshold |
| | `Biometrics:AttendanceTolerance` | 0.60 | Max distance for match |
| | `Biometrics:BallTreeThreshold` | 50 | Employee count to enable BallTree |
| **Attendance** | `Attendance:MinGapSeconds` | 180 | Min seconds between scans |
| **Location** | `Kiosk:GPSAccuracyRequired` | 50 | Required GPS accuracy (meters) |
| | `Kiosk:GPSRadiusDefault` | 100 | Default office radius (meters) |

---

## Troubleshooting

### Common Issues

| Issue | Solution |
|-------|----------|
| Health returns 503 | Check DB connection and OpenVINO worker health |
| Scan timeout | Increase `Kiosk:RequestTimeoutMs` |
| High worker latency | Profile worker model load/inference and lower kiosk concurrency |
| Anti-spoof failures | Check worker model health and review pilot calibration samples |
| Face index not loaded | Check FastFaceMatcher initialization in logs |

### Log Locations
- Windows Event Log → Application → "FaceAttend"
- IIS logs: `C:\inetpub\logs\LogFiles\W3SVC*`

---

## Security Hardening Checklist

For production government deployment:

- [ ] Enable TOTP 2FA (Admin → Settings)
- [ ] Set PIN to 8+ characters
- [ ] Configure IP allowlist (Admin:AllowedIpRanges)
- [ ] Enable request signing (set Security:RequestSigningSecret)
- [ ] Review audit logs regularly
- [ ] Set up monitoring/alerting for health endpoint

---

## Scaling Considerations

### For 2000+ Employees
1. Consider Redis for face index backup
2. Add horizontal scaling with load balancer
3. Implement proper logging (Serilog)
4. Add application performance monitoring

### Current Limits
- **Max concurrent scans**: Configurable (default: 20 via OpenVINO pool)
- **Face index**: In-memory (lost on restart, single server)
- **Database**: Supports 10,000+ employees

---

*Last Updated: @DateTime.UtcNow.ToString("yyyy-MM-dd")*
