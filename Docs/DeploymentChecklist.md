# 🚀 FaceAttend Deployment Checklist

## Pre-Deployment Checklist

### 1. Database Setup
- [ ] Run database optimization script
  ```sql
  -- Execute: Scripts/Sql/DatabaseOptimization.sql
  ```
- [ ] Verify all indexes created
- [ ] Test connection to database
- [ ] Verify AdminAuditLogs table exists

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
- [ ] Biometrics:DlibPoolSize = 20 (adjust based on CPU cores)
- [ ] Biometrics:LivenessThreshold = 0.45 (or tune based on testing)

### 3. File & Folder Permissions
- [ ] App_Data folder: Read/Write for application pool
- [ ] Logs folder: Write for application pool
- [ ] Temp folder: Read/Write for application pool

### 4. SSL/TLS Configuration
- [ ] HTTPS enabled on site
- [ ] TLS 1.2 minimum
- [ ] Valid SSL certificate installed
- [ ] HTTP to HTTPS redirect configured

---

## Post-Deployment Checklist

### 1. Initial Verification
- [ ] Health endpoint returns 200: `GET /health`
- [ ] Response includes: database: true, dlibModelsPresent: true, livenessModelPresent: true
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
| **Biometrics** | `Biometrics:DlibPoolSize` | 20 | Concurrent face scans |
| | `Biometrics:LivenessThreshold` | 0.45 | Liveness detection threshold |
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
| Health returns 503 | Check DB connection, model files exist |
| Scan timeout | Increase `Kiosk:RequestTimeoutMs` |
| High memory usage | Reduce `Biometrics:DlibPoolSize` |
| Liveness circuit open | Check ONNX model file, reset in Settings |
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
- **Max concurrent scans**: Configurable (default: 20 via dlib pool)
- **Face index**: In-memory (lost on restart, single server)
- **Database**: Supports 10,000+ employees

---

*Last Updated: @DateTime.UtcNow.ToString("yyyy-MM-dd")*