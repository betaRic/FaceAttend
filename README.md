# FaceAttend — DILG Region XII Face Attendance System

<p align="center">
  <img src="Content/images/dilg-logo.svg" alt="DILG Region XII" width="80" />
</p>

<p align="center">
  A biometric face-recognition attendance system built for the Department of the Interior and Local Government (DILG) Region XII. Supports walk-by kiosk scanning, mobile self-service attendance, visitor logging, and a full admin management panel — all running on-premises on a Windows IIS server.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/.NET%20Framework-4.8-blue" alt=".NET 4.8" />
  <img src="https://img.shields.io/badge/ASP.NET%20MVC-5.3-blue" alt="ASP.NET MVC 5" />
  <img src="https://img.shields.io/badge/SQL%20Server-Express-lightgrey" alt="SQL Server Express" />
  <img src="https://img.shields.io/badge/ONNX%20Runtime-biometric%20engine-orange" alt="ONNX Runtime" />
  <img src="https://img.shields.io/badge/anti--spoof-review--first-green" alt="Anti-spoof policy" />
  <img src="https://img.shields.io/badge/platform-Windows%20%2F%20IIS-informational" alt="Windows IIS" />
</p>

---

## Table of Contents

1. [Overview](#overview)
2. [Key Features](#key-features)
3. [System Architecture](#system-architecture)
4. [Technology Stack](#technology-stack)
5. [Project Structure](#project-structure)
6. [Prerequisites](#prerequisites)
7. [Installation & Setup](#installation--setup)
8. [Configuration Reference](#configuration-reference)
9. [Security Setup](#security-setup)
10. [ML Models](#ml-models)
11. [Usage Guide](#usage-guide)
12. [Admin Panel](#admin-panel)
13. [Mobile & Kiosk Flows](#mobile--kiosk-flows)
14. [API & Health Endpoints](#api--health-endpoints)
15. [Database Schema](#database-schema)
16. [Performance & Tuning](#performance--tuning)
17. [Troubleshooting](#troubleshooting)
18. [Contributing](#contributing)
19. [License](#license)

---

## Overview

FaceAttend is a production-grade attendance system deployed for DILG Region XII offices across General Santos City, provinces, and HUC offices. It uses real-time face recognition to record employee time-in and time-out events without any physical card or PIN input.

The system operates in two modes:

- **Kiosk mode** — a shared desktop or tablet placed at the office entrance. Employees walk past the camera; the system recognizes their face and records attendance automatically.
- **Mobile/public mode** — employees can self-register and scan attendance from their own device. The server remains the only authority for identity decisions.

Visitor logging is integrated into both modes. Unrecognized faces trigger a visitor entry form instead of failing silently.

---

## Key Features

### Biometrics
- Real-time face detection via **MediaPipe** (client-side, zero-latency)
- Face recognition via an in-process **ONNX Runtime biometric engine** with policy-configured embeddings (`Biometrics:EmbeddingDim`, default 128 for OpenCV SFace)
- **Anti-spoof scoring** via the biometric engine; treated as a calibrated risk signal, not proof of real presence
- **BallTree face index** for O(log n) matching when employee count exceeds configured threshold
- **Angle-aware tolerance** — relaxes face match threshold when the face is off-center
- **Sharpness scoring** (Laplacian variance) during enrollment to reject blurry frames
- **Multi-vector enrollment** — up to 5 face vectors stored per employee for pose diversity
- **Server-owned inference** — MVC receives one still JPEG per scan; browser detection is UI guidance only
- **In-memory scan pipeline** — single JPEG decode reused across all operations (no temp files)

### Kiosk
- 60ms detection loop with 20ms stable-face hold before firing
- Walk-by scanning — no button press required
- GPS-based office verification with Haversine distance calculation
- GPS drift detection — re-verifies location if device moves >60m mid-session
- Anti-spoof GPS validation — blocks null-island coordinates and exact repeat coordinates
- Admin PIN unlock from kiosk via `Ctrl+Shift+Space` or double-click brand logo
- Server warm-up gate — displays "System starting..." while the biometric engine and face indexes initialize
- Idle overlay with live clock, office map, and location status

### Mobile Self-Service
- Employee self-enrollment wizard (10 frames, pose diversity, sharpness filter)
- Personal device registration with admin approval workflow
- Dual-layer device identity: cryptographic token (1-year cookie) + browser fingerprint fallback
- 1-device-per-employee policy — registering a new device automatically replaces the old one
- Wrong-device detection shows the registered owner's name
- After a successful scan, employees are redirected to their personal attendance portal
- Employee portal: today's status, monthly summary, total hours, average hours/day, CSV export

### Admin Panel
- Dashboard with live KPI cards (active employees, time-ins today, time-outs today, known visitors)
- Employee management: create, edit, deactivate, re-enroll
- Admin enrollment supports both live camera capture and photo upload
- Attendance log with filtering by date, office, department, employee, event type, and `NeedsReview` flag
- Attendance summary report: per-employee daily first-IN / last-OUT / hours worked (31-day cap)
- Visitor log with known/unknown tracking and CSV export
- Office management: GPS coordinates, radius, WiFi SSID, type (REGION / PROVINCE / HUC)
- Device management: approve, reject, block registered mobile devices
- Settings panel: all biometric, attendance, location, anti-spoof, and performance parameters configurable at runtime
- Full audit log: every admin action written with IP, timestamp, old/new values as JSON

### Security
- Admin PIN authentication with **PBKDF2** (120,000 iterations, SHA-256, random salt)
- Per-IP brute-force lockout (5 attempts → 300s lockout, configurable)
- PIN hash stored in database configuration as `Admin:PinHash` — never plaintext
- IP allowlist for admin panel (configurable LAN subnet)
- CSRF protection on all POST endpoints (`ValidateAntiForgeryToken`)
- Security headers on every response: `X-Content-Type-Options`, `X-Frame-Options`, `X-XSS-Protection`, `Referrer-Policy`, CSP
- Open-redirect prevention on admin return URLs
- HTTPS enforcement filter (activate after binding SSL certificate)

---

## System Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                     Client Layer                             │
│                                                              │
│  ┌──────────────────┐    ┌──────────────────────────────┐   │
│  │   Kiosk Browser  │    │  Employee Mobile Browser     │   │
│  │  (Desktop/Tablet)│    │  (Android / iOS)             │   │
│  │                  │    │                              │   │
│  │  MediaPipe WASM  │    │  MediaPipe WASM              │   │
│  │  Face Detection  │    │  Face Detection              │   │
│  └────────┬─────────┘    └─────────────┬────────────────┘   │
│           │ HTTPS POST /Kiosk/Attend   │ HTTPS POST         │
└───────────┼────────────────────────────┼────────────────────┘
            │                            │
┌───────────▼────────────────────────────▼────────────────────┐
│                    IIS / ASP.NET MVC 5                       │
│                                                              │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────────┐  │
│  │KioskController│ │MobileRegistr-│  │  Areas/Admin     │  │
│  │              │  │ationController│  │  Controllers     │  │
│  └──────┬───────┘  └──────┬───────┘  └────────┬─────────┘  │
│         │                  │                   │             │
│  ┌──────▼──────────────────▼───────────────────▼──────────┐ │
│  │                   Service Layer                         │ │
│  │                                                         │ │
│  │  FastScanPipeline  │  DeviceService  │  AttendanceService│ │
│  │  FastFaceMatcher   │  ConfigService  │  AuditHelper      │ │
│  │  BiometricEngine    │  BiometricPolicy │ TimeZoneHelper   │ │
│  │  BiometricPolicy    │  LocationAntiSpoof                    │ │
│  └──────────────────────────────────────────────────────── ┘ │
│                                                              │
│  ┌─────────────────────────────────────────────────────────┐ │
│  │              In-Memory Cache (RAM)                      │ │
│  │  FastFaceMatcher: employee face vectors loaded at start │ │
│  │  BallTree index: O(log n) search for 50+ employees      │ │
│  │  BiometricEngine: in-process ONNX inference boundary        │ │
│  └─────────────────────────────────────────────────────────┘ │
└─────────────────────────────────┬───────────────────────────┘
                                  │ Entity Framework 6
┌─────────────────────────────────▼───────────────────────────┐
│               SQL Server Express (FaceAttendDB)              │
│                                                              │
│  Employees  │  Devices  │  AttendanceLogs  │  VisitorLogs   │
│  Offices    │  Visitors │  Configurations  │  AdminAuditLogs│
└─────────────────────────────────────────────────────────────┘
```

---

## Technology Stack

| Layer | Technology |
|---|---|
| Framework | ASP.NET MVC 5.3 / .NET Framework 4.8 |
| Language | C# (server), JavaScript ES5+ (client) |
| Database | SQL Server Express (Entity Framework 6.4) |
| Face Detection (client) | MediaPipe Face Detection (WASM) |
| Face Recognition (server) | In-process ONNX Runtime biometric engine |
| Anti-spoof (server) | Engine-owned ONNX model, review-first policy |
| Face Encoding (server) | Engine-owned embedding model |
| UI | Bootstrap 5.3, SweetAlert2, Font Awesome 6 |
| Web Server | IIS (Windows Server / Windows 10+) |
| Build | Visual Studio 2022, MSBuild, TypeScript 5.9 |

---

## Project Structure

```
FaceAttend/
├── App_Data/
│   ├── models/
│   │   └── onnx/                              # Deployed ONNX model files/hashes
│   │       └── *.onnx
│   └── tmp/                               # Temporary file processing
├── Areas/
│   └── Admin/
│       ├── Controllers/                   # Admin area controllers
│       │   ├── AttendanceController.cs
│       │   ├── DashboardController.cs
│       │   ├── DevicesController.cs
│       │   ├── EmployeesController.cs
│       │   ├── OfficesController.cs
│       │   ├── SettingsController.cs
│       │   └── VisitorsController.cs
│       ├── Helpers/                       # SettingsViewModelBuilder, SettingsSaver
│       └── Views/                         # Admin Razor views
├── Controllers/
│   ├── KioskController.cs                 # Walk-by attendance scanning
│   ├── MobileRegistrationController.cs    # Mobile enrollment & employee portal
│   ├── HealthController.cs                # /Health, /Health/live, /Health/diagnostics
│   └── ErrorController.cs
├── Filters/
│   ├── AdminAuthorizeAttribute.cs         # PIN auth, IP allowlist, PBKDF2
│   └── SecurityHeadersAttribute.cs        # CSP, X-Frame-Options, HSTS, etc.
├── Models/
│   ├── FaceAttendDBEntities.edmx          # Entity Framework model
│   └── ViewModels/                        # Strongly-typed view models
├── Services/
│   ├── Biometrics/
│   │   ├── BiometricEngine.cs             # MVC-facing in-process biometric engine
│   │   ├── FastFaceMatcher.cs             # RAM-cache matching (~5–20ms)
│   │   ├── FastScanPipeline.cs            # In-memory engine-backed scan pipeline
│   │   ├── BallTreeIndex.cs               # O(log n) nearest-neighbor search
│   │   ├── EmployeeFaceIndex.cs           # Employee face cache with BallTree
│   │   ├── BiometricPolicy.cs             # Canonical thresholds/model policy
│   │   └── FaceVectorCodec.cs             # Shared vector loading/decoding
│   ├── Security/
│   │   └── LocationAntiSpoof.cs           # GPS mock detection, repeat-coordinate check
│   ├── AttendanceService.cs               # SERIALIZABLE transaction attendance recording
│   ├── ConfigurationService.cs            # DB-backed config with Web.config fallback
│   ├── DeviceService.cs                   # Device token, fingerprint, approval
│   ├── OfficeLocationService.cs           # GPS office matching (Haversine)
│   ├── TimeZoneHelper.cs                  # Asia/Manila timezone normalization
│   ├── AuditHelper.cs                     # Admin audit log writer
│   └── HealthProbe.cs                     # System readiness check
├── Scripts/
│   ├── kiosk.js                           # Main kiosk engine (MediaPipe + scan loop)
│   ├── modules/
│   │   └── enrollment-core.js             # Enrollment pipeline (sharpness, pose, anti-spoof guidance)
│   ├── enrollment-ui.js                   # Enrollment wizard UI controller
│   └── core/
│       └── api.js                         # Fetch wrapper with timeout + abort
├── Content/
│   ├── kiosk.css                          # Kiosk layout and idle overlay
│   └── images/
│       └── dilg-logo.svg
├── Views/
│   ├── Kiosk/Index.cshtml                 # Kiosk page
│   └── MobileRegistration/               # Enrollment, identify, device, employee portal
├── Global.asax.cs                         # App startup, warm-up pipeline
└── Web.config                             # Connection string, app settings, security
```

---

## Prerequisites

### Server Requirements

| Component | Minimum | Recommended |
|---|---|---|
| OS | Windows 10 / Server 2016 | Windows Server 2019/2022 |
| RAM | 4 GB | 8 GB |
| CPU | x64, 4 cores | x64, 8 cores |
| Disk | 5 GB free | 10 GB free (SSD) |
| .NET Framework | 4.8 | 4.8 |
| IIS | 10.0 | 10.0 |
| SQL Server | Express 2019 | Express 2022 or Standard |
| Visual C++ Runtime | 2015–2022 x64 | 2015–2022 x64 |

> **Important:** ONNX Runtime requires the Visual C++ 2015–2022 x64 Redistributable. Install from [Microsoft's download page](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist) before deploying.

### Development Requirements

- Visual Studio 2022 (with ASP.NET and web development workload)
- SQL Server Express with SQL Server Management Studio
- Git

---

## Installation & Setup

### 1. Clone the Repository

```bash
git clone https://github.com/your-org/FaceAttend.git
cd FaceAttend
```

### 2. Prepare ONNX Models

The controlled-deployment candidate ONNX files are installed under `App_Data/models/onnx`:

| File | Model | SHA-256 |
|---|---|---|
| `face-detector.onnx` | OpenCV YuNet 2023mar detector with 5-point landmarks | `8f2383e4dd3cfbb4553ea8718107fc0423210dc964f9f4280604804ed2552fa4` |
| `face-recognizer.onnx` | OpenCV SFace 2021dec recognizer | `0ba9fbfa01b5270c96627c4ef784da859931e02f04419c829e83484087c34e79` |
| `anti-spoof.onnx` | OpenVINO anti-spoof-mn3 ONNX classifier | `c4c99af04603b62d7e44f6f4daeb33e0daeccc696008c0b1d62f6f5cebbb3262` |

`Biometrics:ModelHashes` is stored in `SystemConfigurations` and editable from Admin Settings. If any model file changes, treat the model version as changed and run pilot calibration before enrolling employees.

### 3. Configure the Database

```sql
-- Run in SQL Server Management Studio
CREATE DATABASE FaceAttendDB;
```

Update the connection string in `Web.config`:

```xml
<add name="FaceAttendDBEntities"
     connectionString="...data source=localhost\SQLEXPRESS;initial catalog=FaceAttendDB;..." />
```

Run the Entity Framework migrations or apply the included SQL scripts to create the schema.

### 4. Restore NuGet Packages

```bash
# In Visual Studio: Tools > NuGet Package Manager > Restore
# Or from command line:
nuget restore FaceAttend.sln
```

### 5. Build the Solution

Build in Visual Studio (`Ctrl+Shift+B`) targeting **x64**. The project requires x64 because ONNX Runtime native binaries are x64.

### 6. Configure IIS

1. Create a new IIS site pointing to the published output folder.
2. Set the application pool to **.NET CLR v4.0**, **Integrated pipeline**, **x64**.
3. Ensure the app pool identity has **read/write** access to `App_Data/`.
4. (Optional) Bind an SSL certificate to enable HTTPS and activate the `Secure` flag on cookies.

### 7. Set Database Configuration

Runtime settings live in the `SystemConfigurations` table. After the first admin login, change the admin PIN from:

`Admin → Settings → Security/Ops → Admin PIN`

The stored value is `Admin:PinHash`, a PBKDF2 hash. Existing installs should log in with the current PIN once, change it in Admin Settings, and then operate from the database value.

### 8. First Launch

Navigate to `https://your-server/` — the kiosk page loads. Navigate to `https://your-server/Admin` to access the admin panel and enter the PIN you configured.

> **Cold start note:** On first load after server restart, the system warms the biometric engine and face indexes. The kiosk displays "System starting..." during this window and begins scanning automatically once ready.

---

## Configuration Reference

Most settings are configurable via Admin → Settings at runtime. They are stored in the `SystemConfigurations` database table. `Web.config` values serve as initial defaults only.

### Biometrics

| Key | Default | Description |
|---|---|---|
| `Biometrics:ModelDir` | `~/App_Data/models/onnx` | Secured ONNX model directory. |
| `Biometrics:Engine:Runtime` | `ONNX_RUNTIME_CPU` | In-process inference runtime. |
| `Biometrics:Engine:AnalyzeTimeoutMs` | `5000` | Maximum engine analysis time per submitted frame. |
| `Biometrics:ModelVersion` | `yunet-sface-antispoofmn3-v1-pending-calibration` | Model identity written into decisions/receipts/templates. Fresh enrollment must happen after this is final. |
| `Biometrics:EmbeddingDim` | `128` | Expected SFace embedding length. |
| `Biometrics:ModelHashes` | database value | SHA-256 pins for deployed ONNX files, managed from Admin Settings. |
| `Biometrics:AttendanceTolerance` | `0.60` | Face match tolerance for attendance scanning. |
| `Biometrics:AntiSpoof:ClearThreshold` | `0.45` | Anti-spoof clear-pass threshold applied by MVC policy. |
| `Biometrics:AntiSpoof:ReviewThreshold` | `0.30` | Gray-zone threshold; retry or mark `NeedsReview`. |
| `Biometrics:AntiSpoof:BlockThreshold` | `0.15` | Critical spoof threshold. |
| `Biometrics:EnrollmentStrictTolerance` | `0.45` | Duplicate check tolerance during new employee enrollment. |
| `Biometrics:BallTreeThreshold` | `50` | Employee count above which BallTree index is used instead of linear scan. |
| `Biometrics:BallTreeLeafSize` | `16` | BallTree leaf size (4–64). |
| `Biometrics:MaxImageDimension` | `1280` | Images larger than this are resized before processing. |
| `Biometrics:Enroll:CaptureTarget` | `8` | Target frames to capture during enrollment. |
| `Biometrics:Enroll:MaxStoredVectors` | `5` | Maximum face vectors stored per employee. |

### Attendance

| Key | Default | Description |
|---|---|---|
| `Attendance:MinGapSeconds` | `180` | Minimum seconds between any two scans for the same employee. |
| `Attendance:MinGap:InToOutSeconds` | `1800` | Minimum gap from Time In to Time Out (30 minutes). |
| `Attendance:MinGap:OutToInSeconds` | `300` | Minimum gap from Time Out back to Time In (5 minutes). |
| `Attendance:WorkStart` | `08:00` | Standard work start time (used in reports). |
| `Attendance:WorkEnd` | `17:00` | Standard work end time. |
| `Attendance:LunchStart` | `12:00` | Lunch break start. |

### Location

| Key | Default | Description |
|---|---|---|
| `Location:GPSRadiusDefault` | `100` | Default office radius in meters (overridden per office). |
| `Location:GPSAccuracyRequired` | `50` | Maximum GPS accuracy error in meters. Higher = more permissive. |

### Anti-spoof Policy

| Key | Default | Description |
|---|---|---|
| `Biometrics:AntiSpoof:ClearThreshold` | `0.45` | Score at or above this value is treated as a clear anti-spoof pass. |
| `Biometrics:AntiSpoof:ReviewThreshold` | `0.35` | Gray-zone score that records but flags `NeedsReview`. |
| `Biometrics:Engine:AnalyzeTimeoutMs` | `5000` | Timeout for biometric engine analysis. |

### Admin Security

| Key | Default | Description |
|---|---|---|
| `Admin:SessionMinutes` | `30` | Admin session duration after PIN unlock. |
| `Admin:PinMaxAttempts` | `5` | Failed PIN attempts before IP lockout. |
| `Admin:PinLockoutSeconds` | `300` | Lockout duration in seconds (5 minutes). |

---

## Security Setup

### Admin PIN

The admin PIN is **never stored in plain text or in source control**. Only its PBKDF2 hash is stored in `SystemConfigurations` as `Admin:PinHash`. Change it from Admin Settings.

### IP Allowlist

Set `FACEATTEND_ADMIN_ALLOWED_IP_RANGES` to the LAN subnet of the Regional Office server (e.g. `192.168.1.0/24`). The admin panel will be unreachable from any IP outside this range.

Leave blank to disable IP restriction (not recommended for production).

### HTTPS

1. Bind an SSL certificate to your IIS site.
2. Uncomment `filters.Add(new RequireHttpsAttribute());` in `App_Start/FilterConfig.cs`.
3. This activates the `Secure` flag on the device token cookie and the admin unlock cookie.

---

## ML Models

### Face Recognition — Biometric Engine

FaceAttend sends one still JPEG to the in-process biometric engine. The engine boundary owns authoritative face detection, landmark extraction, face alignment, embedding generation, and anti-spoof scoring. Matching still happens in `FastFaceMatcher` against active employee vectors. Embedding length is controlled by `Biometrics:EmbeddingDim` and defaults to 128.

- Enrollment uses a **strict** tolerance (0.45) to prevent near-duplicate enrollments.
- Attendance uses the policy tolerance (`Biometrics:AttendanceTolerance`, capped by the medium-distance threshold) and must be calibrated from local pilot distance distributions.

### Face Index

Face vectors for all active employees are loaded into RAM at startup via `FastFaceMatcher`. When the employee count exceeds `Biometrics:BallTreeThreshold` (default 50), a `BallTreeIndex` is built for O(log n) nearest-neighbor search. Below the threshold, a linear scan is used.

Matching time: ~5–20ms (RAM), compared to ~100–200ms for a database query.

### Anti-spoof

Each scan sends a still JPEG to the biometric engine. The engine returns an anti-spoof score plus model/version metadata. MVC applies the canonical `BiometricPolicy`: clear pass records normally, gray zone retries or flags review, and critical spoof blocks. Do not treat this as proof of life until local pilot calibration supports it.

### Warm-up Pipeline

On application start, `Global.asax` runs these steps in a background thread:

1. **Biometric engine health** — verifies ONNX Runtime, model files, and scan adapters are ready.
2. **Employee face index** — reads all active employee face vectors from the database into RAM.
3. **Visitor face index** — same for known visitors.

Total cold-start time: **15–20 seconds** with pool size 4, **8–10 seconds** with pool size 2.

The kiosk polls `/Health` every 2 seconds and gates all scans until `warmUpState: 1` is returned.

---

## Usage Guide

### Kiosk (Walk-by Mode)

1. Open the kiosk URL in a browser (fullscreen recommended: `F11`).
2. The system resolves the GPS location and verifies the office radius. For desktop/tablet kiosks, location is resolved via the registered office profile instead of GPS.
3. Once location is verified, the idle overlay disappears and the camera activates.
4. Employees look at the camera. Recognition fires automatically — no button press needed.
5. A success or failure notification appears and the system resets within 3 seconds.

**Admin access from kiosk:** Press `Ctrl+Shift+Space` or double-click the brand logo to open the PIN unlock dialog.

**Reset device mode:** Navigate to `/?reset=1` to clear any stuck device mode selection.

### Mobile (Personal Device)

#### New Employee Enrollment

1. Open the kiosk URL on a mobile phone.
2. Tap "New Employee" → fill in employee details → capture 10 face frames.
3. Submit for admin approval. The employee waits on the success screen while polling for approval status.
4. Admin approves in Admin → Employees → Pending. The employee's device is automatically activated.

#### Existing Employee — Register Device

1. Open the kiosk URL on a mobile phone.
2. Tap "Existing Employee" → look at the camera to identify yourself.
3. Complete device registration. Admin approves the device in Admin → Devices.
4. Once approved, the employee can scan attendance from their phone.

#### Daily Attendance (Mobile)

1. Open the kiosk URL on the registered phone.
2. Look at the camera — the system identifies, checks anti-spoof risk, verifies GPS, and records attendance.
3. After a successful scan, the employee is redirected to `/MobileRegistration/Employee` — their personal attendance portal.

---

## Admin Panel

Access the admin panel at `/Admin`. Enter the configured PIN to unlock.

| Section | Description |
|---|---|
| **Dashboard** | Live KPI cards, recent attendance log, pending review alerts, system health |
| **Employees** | List, create, edit, deactivate employees. Enroll or re-enroll faces. |
| **Attendance** | Full log with filters. Mark records as reviewed. Delete records. Summary report. |
| **Visitors** | Visitor log with known/unknown tracking. Export CSV. |
| **Offices** | Manage office GPS coordinates, radius, WiFi SSID, type. |
| **Devices** | Approve, reject, or block mobile device registrations. |
| **Settings** | All biometric, attendance, location, anti-spoof, and performance settings. |
| **Audit Log** | Every admin action with IP, timestamp, and change details. |

### Attendance NeedsReview

Records are automatically flagged for review (`NeedsReview = true`) when:
- GPS repeat coordinates are detected (possible GPS spoofing)
- Low anti-spoof score (near threshold)
- Other suspicious patterns detected by `LocationAntiSpoof`

Review flagged records in Attendance → filter by "Needs Review". Mark as reviewed with a timestamp and optional note.

---

## Mobile & Kiosk Flows

```
Mobile device opens /Kiosk
         │
         ├─ Desktop / Tablet ──────────────────────────────────────────►
         │   Resolve office by IP/registration (no GPS required)        │
         │                                                               │
         └─ Personal Mobile Phone ──────────────────────────────────►   │
             Get GPS → Haversine check against all active offices       │
                   │                                                     │
             Outside radius? → Show "Outside allowed office area"       │
                   │                                                     │
             Inside radius? ──────────────────────────────────────────► │
                                                                         │
                         Location verified ──────────────────────────────┘
                                │
                         Warm-up check: /Health?warmUpState==1
                                │
                         Face detected by MediaPipe
                                │
                         POST /Kiosk/Attend (JPEG frame + face box + GPS)
                                │
                    Server: BiometricEngine -> FastFaceMatcher
                                │
                    ┌───────────┴──────────┐
                    │ Detector + alignment  │
                    │ SFace + anti-spoof    │
                    └───────────┬──────────┘
                                │
                    Match found? ─── No ──► Visitor modal
                                │
                    Device check (mobile only)
                    ├─ NOT_REGISTERED ──► Register device prompt
                    ├─ PENDING         ──► Wait for approval
                    ├─ BLOCKED         ──► Contact admin
                    └─ ACTIVE          ──► Record attendance
                                │
                    Record IN or OUT (SERIALIZABLE transaction)
                                │
                    Success ──► Toast + redirect to /MobileRegistration/Employee
                                         (mobile only)
```

---

## API & Health Endpoints

### Health Check

```
GET /Health
```

Returns system readiness. The kiosk polls this on startup to gate scanning until models are loaded.

```json
{
  "ok": true,
  "app": true,
  "database": true,
  "biometricEngineReady": true,
  "antiSpoofModelPresent": true,
  "antiSpoofCircuitOpen": false,
  "antiSpoofCircuitStuck": false,
  "warmUpState": 1,
  "warmUpMessage": "COMPLETE",
  "disk": { "ok": true, "status": "ok (863.4 GB free)" }
}
```

`warmUpState` values: `0` = running, `1` = complete, `-1` = failed or timeout.

```
GET /Health/live
```

Lightweight health probe (no DB or model check). Returns `{ "ok": true }`. Used by upstream proxies and monitoring tools.

```
GET /Health/diagnostics
```

Detailed diagnostics including biometric engine status, individual model file presence, DB connection test, and per-step warm-up results. Useful for troubleshooting deployment issues.

### Kiosk Endpoints

| Endpoint | Method | Description |
|---|---|---|
| `POST /Kiosk/Attend` | POST | Submit a face frame for attendance |
| `POST /Kiosk/ResolveOffice` | POST | Resolve current office by GPS or IP |
| `POST /Kiosk/RegisterDevice` | POST | Register a device for an employee |
| `POST /Kiosk/GetCurrentMobileDeviceState` | POST | Check device approval status |
| `POST /Kiosk/UnlockPin` | POST | Verify admin PIN from kiosk |
| `POST /Kiosk/SubmitVisitor` | POST | Submit a visitor entry form |

### Mobile Registration Endpoints

| Endpoint | Method | Description |
|---|---|---|
| `GET /MobileRegistration` | GET | Entry point (new vs existing employee) |
| `GET /MobileRegistration/Enroll` | GET | New employee enrollment wizard |
| `POST /MobileRegistration/ScanFrame` | POST | Per-frame anti-spoof/quality check during enrollment |
| `POST /MobileRegistration/Submit` | POST | Submit completed enrollment |
| `GET /MobileRegistration/Identify` | GET | Existing employee face identification |
| `POST /MobileRegistration/IdentifyFace` | POST | Submit face for identification |
| `GET /MobileRegistration/Device` | GET | Device registration form |
| `POST /MobileRegistration/RegisterDevice` | POST | Submit device registration |
| `GET /MobileRegistration/Employee` | GET | Employee portal (attendance summary) |
| `GET /MobileRegistration/ExportAttendance` | GET | Download monthly attendance CSV |
| `GET /MobileRegistration/CheckStatus` | GET | Poll enrollment/device approval status |

---

## Database Schema

### Core Tables

**`Employees`** — Employee records and face encodings.

| Column | Type | Notes |
|---|---|---|
| `Id` | int PK | Auto-increment |
| `EmployeeId` | nvarchar(20) | Unique employee number |
| `FirstName`, `LastName`, `MiddleName` | nvarchar | Display name |
| `Position`, `Department` | nvarchar | Org structure |
| `OfficeId` | int FK | Assigned office |
| `IsFlexi` | bit | Flexi-schedule flag (no fixed time-in/out) |
| `FaceEncodingBase64` | nvarchar(max) | Primary 128-dim face vector |
| `FaceEncodingsJson` | nvarchar(max) | All captured vectors (up to MaxStoredVectors) |
| `Status` | nvarchar(20) | `ACTIVE` / `PENDING` / `INACTIVE` |

**`Devices`** — Registered mobile devices.

| Column | Type | Notes |
|---|---|---|
| `Id` | int PK | Auto-increment |
| `EmployeeId` | int FK | Owning employee |
| `Fingerprint` | nvarchar(64) | Browser fingerprint hash |
| `DeviceToken` | nvarchar(64) | Cryptographic persistent token (1-year expiry) |
| `DeviceName` | nvarchar(100) | User-supplied name |
| `Status` | nvarchar(20) | `PENDING` / `ACTIVE` / `REPLACED` / `BLOCKED` |

**`AttendanceLogs`** — Time-in and time-out events.

| Column | Type | Notes |
|---|---|---|
| `Id` | bigint PK | Auto-increment |
| `EmployeeId` | int FK | Employee |
| `OfficeId` | int FK | Office at time of scan |
| `EventType` | nvarchar(10) | `IN` or `OUT` |
| `Timestamp` | datetime2 | UTC timestamp |
| `AntiSpoofScore` | float | Current anti-spoof score column (0.0–1.0) |
| `FaceDistance` | float | Euclidean distance to matched vector |
| `LocationVerified` | bit | GPS within office radius |
| `GPSLatitude`, `GPSLongitude` | float | Truncated to 4 decimal places |
| `GPSAccuracy` | float | GPS accuracy in meters |
| `WiFiBSSID` | nvarchar(200) | Office WiFi SSID at time of scan |
| `NeedsReview` | bit | Flagged for admin review |
| `Notes` | nvarchar(max) | Review notes, GPS repeat reason, etc. |

**`Offices`** — Office locations.

| Column | Type | Notes |
|---|---|---|
| `Id` | int PK | Auto-increment |
| `Name`, `Code` | nvarchar | Display name and short code |
| `Type` | nvarchar(20) | `REGION` / `PROVINCE` / `HUC` |
| `Latitude`, `Longitude` | float | GPS center of office |
| `RadiusMeters` | int | Allowed radius for GPS verification |
| `WiFiBSSID` | nvarchar(100) | Expected WiFi network (logged, not enforced) |
| `IsActive` | bit | Soft-delete flag |

---

## Performance & Tuning

### Cold-Start Time

MVC cold start should be dominated by database readiness, biometric engine model load, and face-index cache loading. If scans are slow immediately after restart, profile model-load and inference time from Admin → Operations before changing thresholds.

### Face Matching Speed

| Employee Count | Algorithm | Typical Match Time |
|---|---|---|
| < 50 | Linear scan | ~5–10ms |
| 50–500 | BallTree | ~8–20ms |
| 500+ | BallTree | ~15–30ms |

Adjust `Biometrics:BallTreeThreshold` for your deployment size.

### Connection Pool

`Web.config` sets `Max Pool Size=100` on the SQL Server connection string. This is appropriate for up to ~300 concurrent employees during peak hours. Increase if you see connection pool timeout errors in IIS logs.

### Scan Loop Timing

The kiosk loop runs every 60ms. Key timing constants (configurable in `kiosk.js` `CFG` object):

| Parameter | Value | Description |
|---|---|---|
| `loopMs` | 60ms | Detection loop interval |
| `stableNeededMs` | 20ms | Face must be stable for this long before firing |
| `faceLostMs` | 1800ms | Face considered gone after this many ms without detection |
| `captureCooldownMs` | 900ms | Minimum time between server scan submissions |

---

## Troubleshooting

### Kiosk shows "System starting..." indefinitely

The server warm-up is complete but the kiosk is not detecting it. Check:
1. Navigate to `/Health` in the browser — confirm `warmUpState: 1`.
2. In `kiosk.js`, confirm `pollServerReady` checks `j.warmUpState === 1` (not `j.ready`).
3. Check browser console for CORS or network errors on the `/Health` fetch.

### Warm-up fails or times out

Navigate to `/Health/diagnostics` for detailed step-by-step status. Common causes:
- Biometric engine not ready — check `biometricEngine` in the diagnostics response.
- Missing ONNX model files — check `Biometrics:ModelDir` and model hash/integrity output.
- Database not reachable — check `database` and `error` fields.
- Visual C++ Redistributable not installed — ONNX Runtime may fail to load.

### Face not recognized

1. Check `FaceDistance` in the attendance log — if > 0.65, the face is too far from enrolled vectors.
2. Re-enroll the employee (Admin → Employees → Enroll) with better lighting and more pose variation.
3. Increase `Biometrics:AttendanceTolerance` slightly (max 0.75) in Settings.
4. Verify `Biometrics:Enroll:MaxStoredVectors` is at least 5 and the employee was enrolled with enough frames.

### Anti-spoof always failing

1. Check `AntiSpoofScore`/anti-spoof score in the attendance log.
2. Check biometric engine health and model version from `/Health/diagnostics`.
3. Adjust `Biometrics:AntiSpoof:*Threshold` only after reviewing real failed samples; lowering thresholds blindly is how spoofing sneaks into production.

### GPS location never resolves on mobile

1. The page must be served over HTTPS. GPS API is blocked on HTTP for non-localhost origins.
2. The user must grant location permission in the browser.
3. Check GPS accuracy — if accuracy > `Location:GPSAccuracyRequired` (default 50m), the system waits for a better fix.

### Admin panel inaccessible

1. Check `Admin:PinHash` exists in `SystemConfigurations`.
2. Check `FACEATTEND_ADMIN_ALLOWED_IP_RANGES` — if set, your IP must be in the allowed subnet.
3. Check IIS application pool is running and the app pool identity has correct file permissions.

---

## Contributing

This project is developed for internal use by DILG Region XII. If you are contributing:

1. Create a feature branch: `git checkout -b feature/your-feature-name`
2. Build and test locally against a development database.
3. Ensure no secrets, connection strings, or PIN hashes are committed. The `Web.config` is included in `.gitignore` for this reason — only `Web.config.example` should be tracked.
4. Submit a pull request with a clear description of the change.

### Code Standards

- All C# follows standard .NET naming conventions.
- Service classes are stateless where possible; stateful services use thread-safe patterns.
- Biometric operations must use `BiometricEngine` and `BiometricPolicy`; browser-side face detection is UI guidance only.
- All admin actions must call `AuditHelper.Log()`.
- Security-sensitive operations (PIN verify, device approval, IP check) must not add logging that reveals sensitive values.

---

## License

This software was developed for the **Department of the Interior and Local Government (DILG) Region XII**. All rights reserved.

For licensing inquiries, contact the DILG Region XII IT division.

---

<p align="center">
  Built with ❤️ for DILG Region XII &nbsp;•&nbsp; General Santos City, Philippines
</p>
