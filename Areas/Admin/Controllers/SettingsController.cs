using FaceAttend.Areas.Admin.Helpers;
using FaceAttend.Areas.Admin.Models;
using FaceAttend.Filters;
using FaceAttend.Services;
using FaceAttend.Services.Biometrics;
using System;
using System.Globalization;
using System.Linq;
using System.Web.Mvc;

namespace FaceAttend.Areas.Admin.Controllers
{
    [AdminAuthorize]
    public class SettingsController : Controller
    {
        [HttpGet]
        public ActionResult Index()
        {
            ViewBag.Title = "Settings";

            try
            {
                using (var db = new FaceAttendDBEntities())
                {
                    var vm = BuildVm(db);
                    vm.SavedMessage = TempData["msg"] as string;
                    return View(vm);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("[Settings] BuildVm failed: " + ex);

                var safeVm = BuildSafeVm(ex.Message);
                safeVm.SavedMessage = TempData["msg"] as string;
                return View(safeVm);
            }
        }

        
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Heartbeat()
        {
            var refreshed = AdminAuthorizeAttribute.RefreshSession(Session);
            return Json(new
            {
                ok = refreshed,
                expiresIn = refreshed
                    ? AdminAuthorizeAttribute.GetRemainingSessionSeconds(Session)
                    : 0
            });
        }
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult MigrateBiometricEncryption()
        {
            try
            {
                using (var db = new FaceAttendDBEntities())
                {
                    int employeePrimary = 0;
                    int employeeJson = 0;
                    int visitorPrimary = 0;

                    var employees = db.Employees
                        .Where(e => e.FaceEncodingBase64 != null || e.FaceEncodingsJson != null)
                        .ToList();

                    foreach (var emp in employees)
                    {
                        if (BiometricCrypto.NeedsMigration(emp.FaceEncodingBase64))
                        {
                            emp.FaceEncodingBase64 = BiometricCrypto.ProtectString(emp.FaceEncodingBase64);
                            employeePrimary++;
                        }

                        if (BiometricCrypto.NeedsMigration(emp.FaceEncodingsJson))
                        {
                            emp.FaceEncodingsJson = BiometricCrypto.ProtectString(emp.FaceEncodingsJson);
                            employeeJson++;
                        }
                    }

                    var visitors = db.Visitors
                        .Where(v => v.FaceEncodingBase64 != null)
                        .ToList();

                    foreach (var v in visitors)
                    {
                        if (BiometricCrypto.NeedsMigration(v.FaceEncodingBase64))
                        {
                            v.FaceEncodingBase64 = BiometricCrypto.ProtectString(v.FaceEncodingBase64);
                            visitorPrimary++;
                        }
                    }

                    db.SaveChanges();

                    if (employeePrimary > 0 || employeeJson > 0)
                        EmployeeFaceIndex.Invalidate();

                    if (visitorPrimary > 0)
                        VisitorFaceIndex.Invalidate();

                    AuditHelper.Log(
                        db,
                        Request,
                        AuditHelper.ActionSettingChange,
                        "System",
                        "BiometricEncryptionMigration",
                        "Nag-run ng one-time biometric at-rest encryption migration.",
                        null,
                        new
                        {
                            employeePrimary,
                            employeeJson,
                            visitorPrimary
                        });

                    TempData["msg"] =
                        "Biometric encryption migration done. " +
                        "Employees(primary): " + employeePrimary + ", " +
                        "Employees(json): " + employeeJson + ", " +
                        "Visitors: " + visitorPrimary + ".";
                }
            }
            catch (Exception ex)
            {
                TempData["msg"] = "Biometric migration failed: " + ex.Message;
            }

            return RedirectToAction("Index");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Save(SettingsVm vm)
        {
            ViewBag.Title = "Settings";

            if (vm == null)
                return RedirectToAction("Index");

            ValidateChoiceFields(vm);

            TimeSpan workStartTs;
            TimeSpan workEndTs;
            TimeSpan lunchStartTs;
            TimeSpan lunchEndTs;

            ValidateAttendanceFields(
                vm,
                out workStartTs,
                out workEndTs,
                out lunchStartTs,
                out lunchEndTs);

            if (vm.BallTreeLeafSize < 4 || vm.BallTreeLeafSize > 64)
                ModelState.AddModelError("BallTreeLeafSize", "Must be between 4 and 64.");

            if (vm.MaxImageDimension < 320)
                ModelState.AddModelError("MaxImageDimension", "Minimum is 320 pixels.");

            using (var db = new FaceAttendDBEntities())
            {
                if (vm.FallbackOfficeId > 0)
                {
                    var exists = db.Offices.Any(o => o.Id == vm.FallbackOfficeId && o.IsActive);
                    if (!exists)
                        ModelState.AddModelError("FallbackOfficeId", "Select an active office.");
                }

                if (!ModelState.IsValid)
                {
                    vm.OfficeOptions = AdminQueryHelper.BuildOfficeOptionsWithAuto(db, vm.FallbackOfficeId);
                    return View("Index", vm);
                }

                var clientIp = Request != null ? (Request.UserHostAddress ?? "").Trim() : "";
                var by = string.IsNullOrWhiteSpace(clientIp) ? "ADMIN" : "ADMIN@" + clientIp;

                // ── Biometrics ────────────────────────────────────────────────────

                SystemConfigService.Upsert(
                    db,
                    "Biometrics:DlibTolerance",
                    vm.DlibTolerance.ToString(CultureInfo.InvariantCulture),
                    "double",
                    "Dlib face distance tolerance. Lower = stricter match.",
                    by);

                SystemConfigService.Upsert(
                    db,
                    "Biometrics:LivenessThreshold",
                    vm.LivenessThreshold.ToString(CultureInfo.InvariantCulture),
                    "double",
                    "Minimum liveness score to accept a scan.",
                    by);

                // ── Location ──────────────────────────────────────────────────────

                SystemConfigService.Upsert(
                    db,
                    "Location:GPSAccuracyRequired",
                    vm.GPSAccuracyRequired.ToString(CultureInfo.InvariantCulture),
                    "int",
                    "Max GPS accuracy (meters). Higher means less strict.",
                    by);

                SystemConfigService.Upsert(
                    db,
                    "Location:GPSRadiusDefault",
                    vm.GPSRadiusDefault.ToString(CultureInfo.InvariantCulture),
                    "int",
                    "Default office geofence radius (meters) when office radius is not set.",
                    by);

                SystemConfigService.Upsert(
                    db,
                    "Kiosk:FallbackOfficeId",
                    vm.FallbackOfficeId.ToString(CultureInfo.InvariantCulture),
                    "int",
                    "Office used for desktop kiosks when GPS is skipped. 0 = first active office.",
                    by);

                // ── Attendance ────────────────────────────────────────────────────

                SystemConfigService.Upsert(
                    db,
                    "Attendance:MinGapSeconds",
                    vm.MinGapSeconds.ToString(CultureInfo.InvariantCulture),
                    "int",
                    "Minimum seconds between scans to prevent double taps.",
                    by);

                SystemConfigService.Upsert(
                    db,
                    "Attendance:WorkStart",
                    workStartTs.ToString(@"hh\:mm", CultureInfo.InvariantCulture),
                    "string",
                    "Official work start time in HH:mm.",
                    by);

                SystemConfigService.Upsert(
                    db,
                    "Attendance:WorkEnd",
                    workEndTs.ToString(@"hh\:mm", CultureInfo.InvariantCulture),
                    "string",
                    "Official work end time in HH:mm.",
                    by);

                SystemConfigService.Upsert(
                    db,
                    "Attendance:LunchStart",
                    string.IsNullOrWhiteSpace(vm.LunchStart)
                        ? ""
                        : lunchStartTs.ToString(@"hh\:mm", CultureInfo.InvariantCulture),
                    "string",
                    "Lunch break start time in HH:mm. Blank means no lunch window.",
                    by);

                SystemConfigService.Upsert(
                    db,
                    "Attendance:LunchEnd",
                    string.IsNullOrWhiteSpace(vm.LunchEnd)
                        ? ""
                        : lunchEndTs.ToString(@"hh\:mm", CultureInfo.InvariantCulture),
                    "string",
                    "Lunch break end time in HH:mm. Blank means no lunch window.",
                    by);

                SystemConfigService.Upsert(
                    db,
                    "Attendance:FlexiRequiredHours",
                    vm.FlexiRequiredHours.ToString(CultureInfo.InvariantCulture),
                    "double",
                    "Required work hours for flexi employees.",
                    by);

                SystemConfigService.Upsert(
                    db,
                    "Attendance:NoGracePeriod",
                    vm.NoGracePeriod ? "true" : "false",
                    "bool",
                    "If true, any time after work start is late.",
                    by);

                // ── Review queue ──────────────────────────────────────────────────

                SystemConfigService.Upsert(
                    db,
                    "NeedsReview:NearMatchRatio",
                    vm.NeedsReviewNearMatchRatio.ToString(CultureInfo.InvariantCulture),
                    "double",
                    "If distance is within this ratio of the threshold, mark record as NeedsReview.",
                    by);

                SystemConfigService.Upsert(
                    db,
                    "NeedsReview:LivenessMargin",
                    vm.NeedsReviewLivenessMargin.ToString(CultureInfo.InvariantCulture),
                    "double",
                    "If liveness is within this margin above threshold, mark record as NeedsReview.",
                    by);

                SystemConfigService.Upsert(
                    db,
                    "NeedsReview:GPSAccuracyMargin",
                    vm.NeedsReviewGpsMargin.ToString(CultureInfo.InvariantCulture),
                    "int",
                    "If GPS accuracy is within this margin of the required limit, mark record as NeedsReview.",
                    by);

                // ── Advanced liveness ─────────────────────────────────────────────

                var decision = NormalizeOrDefault(vm.LivenessDecision, "max");
                var scales = (vm.LivenessMultiCropScales ?? "").Trim();
                var outputType = NormalizeOrDefault(vm.LivenessOutputType, "logits");
                var normalize = NormalizeOrDefault(vm.LivenessNormalize, "0_1");
                var channelOrder = NormalizeOrDefault(vm.LivenessChannelOrder, "RGB");

                SystemConfigService.Upsert(
                    db,
                    "Biometrics:Liveness:Decision",
                    decision,
                    "string",
                    "How to combine multi-crop liveness results: max or avg.",
                    by);

                SystemConfigService.Upsert(
                    db,
                    "Biometrics:Liveness:MultiCropScales",
                    scales,
                    "string",
                    "Comma-separated crop scales for liveness tuning (example: 2.3,2.7,3.1).",
                    by);

                SystemConfigService.Upsert(
                    db,
                    "Biometrics:LivenessInputSize",
                    vm.LivenessInputSize.ToString(CultureInfo.InvariantCulture),
                    "int",
                    "Liveness model input size (pixels). Must match the model.",
                    by);

                SystemConfigService.Upsert(
                    db,
                    "Biometrics:Liveness:CropScale",
                    vm.LivenessCropScale.ToString(CultureInfo.InvariantCulture),
                    "double",
                    "Crop scale around detected face before liveness inference.",
                    by);

                SystemConfigService.Upsert(
                    db,
                    "Biometrics:Liveness:RealIndex",
                    vm.LivenessRealIndex.ToString(CultureInfo.InvariantCulture),
                    "int",
                    "Index of the REAL class in model output.",
                    by);

                SystemConfigService.Upsert(
                    db,
                    "Biometrics:Liveness:OutputType",
                    outputType,
                    "string",
                    "Model output type: logits or probs.",
                    by);

                SystemConfigService.Upsert(
                    db,
                    "Biometrics:Liveness:Normalize",
                    normalize,
                    "string",
                    "Input normalize mode: 0_1, minus1_1, imagenet, none.",
                    by);

                SystemConfigService.Upsert(
                    db,
                    "Biometrics:Liveness:ChannelOrder",
                    channelOrder,
                    "string",
                    "Channel order for tensor: RGB or BGR.",
                    by);

                SystemConfigService.Upsert(
                    db,
                    "Biometrics:Liveness:RunTimeoutMs",
                    vm.LivenessRunTimeoutMs.ToString(CultureInfo.InvariantCulture),
                    "int",
                    "Max ONNX run time before the circuit breaker trips.",
                    by);

                SystemConfigService.Upsert(
                    db,
                    "Biometrics:Liveness:SlowMs",
                    vm.LivenessSlowMs.ToString(CultureInfo.InvariantCulture),
                    "int",
                    "Milliseconds considered slow for liveness inference.",
                    by);
                // Clean up removed / legacy keys while saving the current settings set.
                SystemConfigService.Delete(db, "Biometrics:Liveness:GateWaitMs");
                SystemConfigService.Delete(db, "Biometrics:FaceMatchTuner:Enabled");

                SystemConfigService.Upsert(
                    db,
                    "Biometrics:Liveness:CircuitFailStreak",
                    vm.LivenessCircuitFailStreak.ToString(CultureInfo.InvariantCulture),
                    "int",
                    "How many failures before circuit opens.",
                    by);

                SystemConfigService.Upsert(
                    db,
                    "Biometrics:Liveness:CircuitDisableSeconds",
                    vm.LivenessCircuitDisableSeconds.ToString(CultureInfo.InvariantCulture),
                    "int",
                    "How long to disable liveness after failures.",
                    by);

                // ── Performance ───────────────────────────────────────────────────

                SystemConfigService.Upsert(
                    db,
                    "Biometrics:BallTreeThreshold",
                    vm.BallTreeThreshold.ToString(CultureInfo.InvariantCulture),
                    "int",
                    "Build BallTree face index when enrolled employee count >= this value.",
                    by);

                SystemConfigService.Upsert(
                    db,
                    "Biometrics:BallTreeLeafSize",
                    vm.BallTreeLeafSize.ToString(CultureInfo.InvariantCulture),
                    "int",
                    "BallTree leaf size. Default 16. Range 4–64.",
                    by);

                SystemConfigService.Upsert(
                    db,
                    "Biometrics:MaxImageDimension",
                    vm.MaxImageDimension.ToString(CultureInfo.InvariantCulture),
                    "int",
                    "Resize images larger than this on either axis before face detection.",
                    by);

                SystemConfigService.Upsert(
                    db,
                    "Biometrics:PreprocessJpegQuality",
                    vm.PreprocessJpegQuality.ToString(CultureInfo.InvariantCulture),
                    "int",
                    "JPEG quality of the resized temp image. Range 40–95.",
                    by);

                SystemConfigService.Upsert(
                    db,
                    "Biometrics:FaceMatchTunerEnabled",
                    vm.FaceMatchTunerEnabled ? "true" : "false",
                    "bool",
                    "Enable adaptive face match tolerance tuning based on image quality.",
                    by);

                // ── Visitors ──────────────────────────────────────────────────────

                SystemConfigService.Upsert(
                    db,
                    "Visitors:MaxRecords",
                    vm.VisitorMaxRecords.ToString(CultureInfo.InvariantCulture),
                    "int",
                    "Max visitor log records to retain (soft cap).",
                    by);

                SystemConfigService.Upsert(
                    db,
                    "Visitors:RetentionYears",
                    vm.VisitorRetentionYears.ToString(CultureInfo.InvariantCulture),
                    "int",
                    "Delete visitor logs older than this many years when cleanup is run.",
                    by);

                // Extra safety: may mga config reads na naka-cache sa ibang code path.
                // I-flush lahat pagkatapos ng bulk save para walang stale thresholds.
                SystemConfigService.InvalidateAll();

                AuditHelper.Log(
                    db,
                    Request,
                    AuditHelper.ActionSettingChange,
                    "SystemConfiguration",
                    "bulk-save",
                    "Nag-save ng admin settings.",
                    null,
                    new
                    {
                        vm.DlibTolerance,
                        vm.LivenessThreshold,
                        vm.GPSAccuracyRequired,
                        vm.GPSRadiusDefault,
                        vm.MinGapSeconds,
                        vm.NeedsReviewNearMatchRatio,
                        vm.NeedsReviewLivenessMargin,
                        vm.NeedsReviewGpsMargin,
                        vm.VisitorMaxRecords,
                        vm.VisitorRetentionYears,
                        savedBy = by
                    });

                TempData["msg"] = "Settings saved.";
                return RedirectToAction("Index");
            }
        }

        private SettingsVm BuildVm(FaceAttendDBEntities db)
        {
            // Biometrics
            var tolFallback = AppSettings.GetDouble("Biometrics:DlibTolerance", 0.60);
            var tol = SystemConfigService.GetDouble(
                db,
                "Biometrics:DlibTolerance",
                SystemConfigService.GetDouble(db, "DlibTolerance", tolFallback));

            var liveFallback = AppSettings.GetDouble("Biometrics:LivenessThreshold", 0.75);
            var live = SystemConfigService.GetDouble(db, "Biometrics:LivenessThreshold", liveFallback);

            // Location
            var accFallback = AppSettings.GetInt("Location:GPSAccuracyRequired", 50);
            var acc = SystemConfigService.GetInt(db, "Location:GPSAccuracyRequired", accFallback);

            var radFallback = AppSettings.GetInt("Location:GPSRadiusDefault", 100);
            var rad = SystemConfigService.GetInt(db, "Location:GPSRadiusDefault", radFallback);

            var fbFallback = AppSettings.GetInt("Kiosk:FallbackOfficeId", 0);
            var fb = SystemConfigService.GetInt(db, "Kiosk:FallbackOfficeId", fbFallback);

            // Attendance
            var gapFallback = AppSettings.GetInt("Attendance:MinGapSeconds", 10);
            var gap = SystemConfigService.GetInt(db, "Attendance:MinGapSeconds", gapFallback);

            var workStart = SystemConfigService.GetString(
                db,
                "Attendance:WorkStart",
                AppSettings.GetString("Attendance:WorkStart", "08:00"));

            var workEnd = SystemConfigService.GetString(
                db,
                "Attendance:WorkEnd",
                AppSettings.GetString("Attendance:WorkEnd", "17:00"));

            var lunchStart = SystemConfigService.GetString(
                db,
                "Attendance:LunchStart",
                AppSettings.GetString("Attendance:LunchStart", "12:00"));

            var lunchEnd = SystemConfigService.GetString(
                db,
                "Attendance:LunchEnd",
                AppSettings.GetString("Attendance:LunchEnd", "13:00"));

            var flexiRequiredHours = SystemConfigService.GetDouble(
                db,
                "Attendance:FlexiRequiredHours",
                AppSettings.GetDouble("Attendance:FlexiRequiredHours", 8.0));

            var noGracePeriod = SystemConfigService.GetBool(
                db,
                "Attendance:NoGracePeriod",
                AppSettings.GetBool("Attendance:NoGracePeriod", true));

            // Review queue
            var nearMatch = SystemConfigService.GetDouble(db, "NeedsReview:NearMatchRatio", 0.90);
            var liveMargin = SystemConfigService.GetDouble(db, "NeedsReview:LivenessMargin", 0.03);
            var gpsMargin = SystemConfigService.GetInt(db, "NeedsReview:GPSAccuracyMargin", 10);

            // Advanced liveness
            var decision = SystemConfigService.GetString(
                db,
                "Biometrics:Liveness:Decision",
                AppSettings.GetString("Biometrics:Liveness:Decision", "max"));

            var scales = SystemConfigService.GetString(
                db,
                "Biometrics:Liveness:MultiCropScales",
                AppSettings.GetString("Biometrics:Liveness:MultiCropScales", ""));

            var inputSize = SystemConfigService.GetInt(
                db,
                "Biometrics:LivenessInputSize",
                AppSettings.GetInt("Biometrics:LivenessInputSize", 128));

            var cropScale = SystemConfigService.GetDouble(
                db,
                "Biometrics:Liveness:CropScale",
                AppSettings.GetDouble("Biometrics:Liveness:CropScale", 2.7));

            var realIndex = SystemConfigService.GetInt(
                db,
                "Biometrics:Liveness:RealIndex",
                AppSettings.GetInt("Biometrics:Liveness:RealIndex", 1));

            var outputType = SystemConfigService.GetString(
                db,
                "Biometrics:Liveness:OutputType",
                AppSettings.GetString("Biometrics:Liveness:OutputType", "logits"));

            var normalize = SystemConfigService.GetString(
                db,
                "Biometrics:Liveness:Normalize",
                AppSettings.GetString("Biometrics:Liveness:Normalize", "0_1"));

            var chanOrder = SystemConfigService.GetString(
                db,
                "Biometrics:Liveness:ChannelOrder",
                AppSettings.GetString("Biometrics:Liveness:ChannelOrder", "RGB"));

            var timeoutMs = SystemConfigService.GetInt(
                db,
                "Biometrics:Liveness:RunTimeoutMs",
                AppSettings.GetInt("Biometrics:Liveness:RunTimeoutMs", 1500));

            var slowMs = SystemConfigService.GetInt(
                db,
                "Biometrics:Liveness:SlowMs",
                AppSettings.GetInt("Biometrics:Liveness:SlowMs", 1200));
var failStreak = SystemConfigService.GetInt(
                db,
                "Biometrics:Liveness:CircuitFailStreak",
                AppSettings.GetInt("Biometrics:Liveness:CircuitFailStreak", 3));

            var disableSec = SystemConfigService.GetInt(
                db,
                "Biometrics:Liveness:CircuitDisableSeconds",
                AppSettings.GetInt("Biometrics:Liveness:CircuitDisableSeconds", 30));

            // Performance
            var ballTreeTh = SystemConfigService.GetInt(
                db,
                "Biometrics:BallTreeThreshold",
                AppSettings.GetInt("Biometrics:BallTreeThreshold", 50));

            var ballTreeLeaf = SystemConfigService.GetInt(
                db,
                "Biometrics:BallTreeLeafSize",
                AppSettings.GetInt("Biometrics:BallTreeLeafSize", 16));

            var maxDim = SystemConfigService.GetInt(
                db,
                "Biometrics:MaxImageDimension",
                AppSettings.GetInt("Biometrics:MaxImageDimension", 1280));

            var jpegQ = SystemConfigService.GetInt(
                db,
                "Biometrics:PreprocessJpegQuality",
                AppSettings.GetInt("Biometrics:PreprocessJpegQuality", 85));
            var tunerEnabled = SystemConfigService.HasKey(db, "Biometrics:FaceMatchTunerEnabled")
                ? SystemConfigService.GetBool(
                    db,
                    "Biometrics:FaceMatchTunerEnabled",
                    AppSettings.GetBool("Biometrics:FaceMatchTunerEnabled", false))
                : SystemConfigService.GetBool(
                    db,
                    "Biometrics:FaceMatchTuner:Enabled",
                    AppSettings.GetBool("Biometrics:FaceMatchTunerEnabled", false));

            // Visitors
            var visMaxRec = SystemConfigService.GetInt(
                db,
                "Visitors:MaxRecords",
                AppSettings.GetInt("Visitors:MaxRecords", 10000));

            var visRetYears = SystemConfigService.GetInt(
                db,
                "Visitors:RetentionYears",
                AppSettings.GetInt("Visitors:RetentionYears", 2));

            var vm = new SettingsVm
            {
                // Biometrics
                DlibTolerance = tol,
                LivenessThreshold = live,

                // Advanced liveness
                LivenessDecision = NormalizeOrDefault(decision, "max"),
                LivenessMultiCropScales = (scales ?? "").Trim(),
                LivenessInputSize = inputSize,
                LivenessCropScale = cropScale,
                LivenessRealIndex = realIndex,
                LivenessOutputType = NormalizeOrDefault(outputType, "logits"),
                LivenessNormalize = NormalizeOrDefault(normalize, "0_1"),
                LivenessChannelOrder = NormalizeOrDefault(chanOrder, "RGB"),
                LivenessRunTimeoutMs = timeoutMs,
                LivenessSlowMs = slowMs,
                LivenessCircuitFailStreak = failStreak,
                LivenessCircuitDisableSeconds = disableSec,

                // Performance
                BallTreeThreshold = ballTreeTh,
                BallTreeLeafSize = ballTreeLeaf,
                MaxImageDimension = maxDim,
                PreprocessJpegQuality = jpegQ,
                FaceMatchTunerEnabled = tunerEnabled,

                // Location
                GPSAccuracyRequired = acc,
                GPSRadiusDefault = rad,
                FallbackOfficeId = fb,

                // Attendance
                MinGapSeconds = gap,
                WorkStart = NormalizeTimeOrDefault(workStart, "08:00"),
                WorkEnd = NormalizeTimeOrDefault(workEnd, "17:00"),
                LunchStart = NormalizeTimeOrDefault(lunchStart, "12:00"),
                LunchEnd = NormalizeTimeOrDefault(lunchEnd, "13:00"),
                FlexiRequiredHours = flexiRequiredHours,
                NoGracePeriod = noGracePeriod,

                // Review queue
                NeedsReviewNearMatchRatio = nearMatch,
                NeedsReviewLivenessMargin = liveMargin,
                NeedsReviewGpsMargin = gpsMargin,

                // Visitors
                VisitorMaxRecords = visMaxRec,
                VisitorRetentionYears = visRetYears,

                OfficeOptions = AdminQueryHelper.BuildOfficeOptionsWithAuto(db, fb)
            };

            if (SystemConfigService.HasKey(db, "DlibTolerance") &&
                !SystemConfigService.HasKey(db, "Biometrics:DlibTolerance"))
            {
                vm.WarningMessage =
                    "Legacy key DlibTolerance exists in SystemConfiguration. " +
                    "New key Biometrics:DlibTolerance is preferred. " +
                    "Save settings once to migrate.";
            }

            if (SystemConfigService.HasKey(db, "Biometrics:FaceMatchTuner:Enabled") &&
                !SystemConfigService.HasKey(db, "Biometrics:FaceMatchTunerEnabled"))
            {
                vm.WarningMessage = string.IsNullOrWhiteSpace(vm.WarningMessage)
                    ? "Legacy key Biometrics:FaceMatchTuner:Enabled exists in SystemConfiguration. Save settings once to migrate."
                    : vm.WarningMessage + " Legacy key Biometrics:FaceMatchTuner:Enabled also exists. Save settings once to migrate.";
            }

            if (SystemConfigService.HasKey(db, "Biometrics:Liveness:GateWaitMs"))
            {
                vm.WarningMessage = string.IsNullOrWhiteSpace(vm.WarningMessage)
                    ? "Removed key Biometrics:Liveness:GateWaitMs still exists in SystemConfiguration. Save settings once to clean it up."
                    : vm.WarningMessage + " Removed key Biometrics:Liveness:GateWaitMs still exists. Save settings once to clean it up.";
            }

            return vm;
        }

        private SettingsVm BuildSafeVm(string errorMessage)
        {
            return new SettingsVm
            {
                WarningMessage =
                    "Could not load settings from the database: " + errorMessage +
                    " — Defaults are shown below. Save to persist them.",

                DlibTolerance = AppSettings.GetDouble("Biometrics:DlibTolerance", 0.60),
                LivenessThreshold = AppSettings.GetDouble("Biometrics:LivenessThreshold", 0.75),

                GPSAccuracyRequired = AppSettings.GetInt("Location:GPSAccuracyRequired", 50),
                GPSRadiusDefault = AppSettings.GetInt("Location:GPSRadiusDefault", 100),
                FallbackOfficeId = AppSettings.GetInt("Kiosk:FallbackOfficeId", 0),

                MinGapSeconds = AppSettings.GetInt("Attendance:MinGapSeconds", 10),
                WorkStart = NormalizeTimeOrDefault(AppSettings.GetString("Attendance:WorkStart", "08:00"), "08:00"),
                WorkEnd = NormalizeTimeOrDefault(AppSettings.GetString("Attendance:WorkEnd", "17:00"), "17:00"),
                LunchStart = NormalizeTimeOrDefault(AppSettings.GetString("Attendance:LunchStart", "12:00"), "12:00"),
                LunchEnd = NormalizeTimeOrDefault(AppSettings.GetString("Attendance:LunchEnd", "13:00"), "13:00"),
                FlexiRequiredHours = AppSettings.GetDouble("Attendance:FlexiRequiredHours", 8.0),
                NoGracePeriod = AppSettings.GetBool("Attendance:NoGracePeriod", true),

                BallTreeThreshold = AppSettings.GetInt("Biometrics:BallTreeThreshold", 50),
                BallTreeLeafSize = AppSettings.GetInt("Biometrics:BallTreeLeafSize", 16),
                MaxImageDimension = AppSettings.GetInt("Biometrics:MaxImageDimension", 1280),
                PreprocessJpegQuality = AppSettings.GetInt("Biometrics:PreprocessJpegQuality", 85),
                FaceMatchTunerEnabled = AppSettings.GetBool("Biometrics:FaceMatchTunerEnabled", false),

                LivenessDecision = NormalizeOrDefault(
                    AppSettings.GetString("Biometrics:Liveness:Decision", "max"),
                    "max"),
                LivenessMultiCropScales = AppSettings.GetString("Biometrics:Liveness:MultiCropScales", ""),
                LivenessOutputType = NormalizeOrDefault(
                    AppSettings.GetString("Biometrics:Liveness:OutputType", "logits"),
                    "logits"),
                LivenessNormalize = NormalizeOrDefault(
                    AppSettings.GetString("Biometrics:Liveness:Normalize", "0_1"),
                    "0_1"),
                LivenessChannelOrder = NormalizeOrDefault(
                    AppSettings.GetString("Biometrics:Liveness:ChannelOrder", "RGB"),
                    "RGB"),
                LivenessInputSize = AppSettings.GetInt("Biometrics:LivenessInputSize", 128),
                LivenessCropScale = AppSettings.GetDouble("Biometrics:Liveness:CropScale", 2.7),
                LivenessRealIndex = AppSettings.GetInt("Biometrics:Liveness:RealIndex", 1),
                LivenessRunTimeoutMs = AppSettings.GetInt("Biometrics:Liveness:RunTimeoutMs", 1500),
                LivenessSlowMs = AppSettings.GetInt("Biometrics:Liveness:SlowMs", 1200),
                LivenessCircuitFailStreak = AppSettings.GetInt("Biometrics:Liveness:CircuitFailStreak", 3),
                LivenessCircuitDisableSeconds = AppSettings.GetInt("Biometrics:Liveness:CircuitDisableSeconds", 30),

                VisitorMaxRecords = AppSettings.GetInt("Visitors:MaxRecords", 10000),
                VisitorRetentionYears = AppSettings.GetInt("Visitors:RetentionYears", 2),

                OfficeOptions = new System.Collections.Generic.List<SelectListItem>
                {
                    new SelectListItem
                    {
                        Text = "Auto (first active office)",
                        Value = "0",
                        Selected = true
                    }
                }
            };
        }

        private void ValidateChoiceFields(SettingsVm vm)
        {
            if (vm.LivenessDecision != null)
            {
                var value = vm.LivenessDecision.Trim().ToLowerInvariant();
                if (value != "max" && value != "avg")
                    ModelState.AddModelError("LivenessDecision", "Use max or avg.");
            }

            if (vm.LivenessOutputType != null)
            {
                var value = vm.LivenessOutputType.Trim().ToLowerInvariant();
                if (value != "logits" && value != "probs")
                    ModelState.AddModelError("LivenessOutputType", "Use logits or probs.");
            }

            if (vm.LivenessNormalize != null)
            {
                var value = vm.LivenessNormalize.Trim().ToLowerInvariant();
                if (value != "0_1" && value != "minus1_1" && value != "imagenet" && value != "none")
                    ModelState.AddModelError("LivenessNormalize", "Use 0_1, minus1_1, imagenet, or none.");
            }

            if (vm.LivenessChannelOrder != null)
            {
                var value = vm.LivenessChannelOrder.Trim().ToUpperInvariant();
                if (value != "RGB" && value != "BGR")
                    ModelState.AddModelError("LivenessChannelOrder", "Use RGB or BGR.");
            }
        }

        private void ValidateAttendanceFields(
            SettingsVm vm,
            out TimeSpan workStartTs,
            out TimeSpan workEndTs,
            out TimeSpan lunchStartTs,
            out TimeSpan lunchEndTs)
        {
            workStartTs = TimeSpan.Zero;
            workEndTs = TimeSpan.Zero;
            lunchStartTs = TimeSpan.Zero;
            lunchEndTs = TimeSpan.Zero;

            if (!TryParseTime(vm.WorkStart, out workStartTs))
                ModelState.AddModelError("WorkStart", "Use HH:mm.");

            if (!TryParseTime(vm.WorkEnd, out workEndTs))
                ModelState.AddModelError("WorkEnd", "Use HH:mm.");

            var hasLunchStart = !string.IsNullOrWhiteSpace(vm.LunchStart);
            var hasLunchEnd = !string.IsNullOrWhiteSpace(vm.LunchEnd);

            if (hasLunchStart != hasLunchEnd)
            {
                ModelState.AddModelError("LunchStart", "Set both lunch start and lunch end, or leave both blank.");
                ModelState.AddModelError("LunchEnd", "Set both lunch start and lunch end, or leave both blank.");
            }
            else if (hasLunchStart && hasLunchEnd)
            {
                if (!TryParseTime(vm.LunchStart, out lunchStartTs))
                    ModelState.AddModelError("LunchStart", "Use HH:mm.");

                if (!TryParseTime(vm.LunchEnd, out lunchEndTs))
                    ModelState.AddModelError("LunchEnd", "Use HH:mm.");
            }

            if (vm.FlexiRequiredHours < 1.0 || vm.FlexiRequiredHours > 12.0)
                ModelState.AddModelError("FlexiRequiredHours", "Must be between 1 and 12.");

            if (ModelState.IsValid && workEndTs <= workStartTs)
                ModelState.AddModelError("WorkEnd", "Work end must be later than work start.");

            if (ModelState.IsValid && hasLunchStart && hasLunchEnd && lunchEndTs <= lunchStartTs)
                ModelState.AddModelError("LunchEnd", "Lunch end must be later than lunch start.");
        }

        private static bool TryParseTime(string value, out TimeSpan result)
        {
            result = TimeSpan.Zero;

            if (string.IsNullOrWhiteSpace(value))
                return false;

            value = value.Trim();

            return
                TimeSpan.TryParseExact(value, @"hh\:mm", CultureInfo.InvariantCulture, out result) ||
                TimeSpan.TryParseExact(value, @"hh\:mm\:ss", CultureInfo.InvariantCulture, out result) ||
                TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out result);
        }

        private static string NormalizeTimeOrDefault(string value, string fallback)
        {
            TimeSpan time;
            return TryParseTime(value, out time)
                ? time.ToString(@"hh\:mm", CultureInfo.InvariantCulture)
                : fallback;
        }

        private static string NormalizeOrDefault(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }
    }
}