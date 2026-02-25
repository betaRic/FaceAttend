using System.Globalization;
using System.Linq;
using System.Web.Mvc;
using FaceAttend.Areas.Admin.Models;
using FaceAttend.Filters;
using FaceAttend.Services;

namespace FaceAttend.Areas.Admin.Controllers
{
    [AdminAuthorize]
    public class SettingsController : Controller
    {
        [HttpGet]
        public ActionResult Index()
        {
            ViewBag.Title = "Settings";

            using (var db = new FaceAttendDBEntities())
            {
                var vm = BuildVm(db);
                if (TempData["msg"] != null) vm.SavedMessage = TempData["msg"].ToString();
                return View(vm);
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Save(SettingsVm vm)
        {
            ViewBag.Title = "Settings";

            if (vm == null)
                return RedirectToAction("Index");

            // ── String field validation ──────────────────────────────────────────────

            if (vm.LivenessDecision != null)
            {
                var d = vm.LivenessDecision.Trim().ToLowerInvariant();
                if (d != "max" && d != "avg")
                    ModelState.AddModelError("LivenessDecision", "Use max or avg.");
            }

            if (vm.LivenessOutputType != null)
            {
                var t = vm.LivenessOutputType.Trim().ToLowerInvariant();
                if (t != "logits" && t != "probs")
                    ModelState.AddModelError("LivenessOutputType", "Use logits or probs.");
            }

            if (vm.LivenessNormalize != null)
            {
                var n = vm.LivenessNormalize.Trim().ToLowerInvariant();
                if (n != "0_1" && n != "minus1_1" && n != "imagenet" && n != "none")
                    ModelState.AddModelError("LivenessNormalize", "Use 0_1, minus1_1, imagenet, or none.");
            }

            if (vm.LivenessChannelOrder != null)
            {
                var c = vm.LivenessChannelOrder.Trim().ToUpperInvariant();
                if (c != "RGB" && c != "BGR")
                    ModelState.AddModelError("LivenessChannelOrder", "Use RGB or BGR.");
            }

            // ── Performance field validation ──────────────────────────────────────────

            if (vm.BallTreeLeafSize < 4 || vm.BallTreeLeafSize > 64)
                ModelState.AddModelError("BallTreeLeafSize", "Must be between 4 and 64.");

            if (vm.MaxImageDimension < 320)
                ModelState.AddModelError("MaxImageDimension", "Minimum is 320 pixels.");

            using (var db = new FaceAttendDBEntities())
            {
                // Validate fallback office exists (allow 0 = auto)
                if (vm.FallbackOfficeId > 0)
                {
                    var ok = db.Offices.Any(o => o.Id == vm.FallbackOfficeId && o.IsActive);
                    if (!ok) ModelState.AddModelError("FallbackOfficeId", "Select an active office.");
                }

                if (!ModelState.IsValid)
                {
                    vm.OfficeOptions = BuildOfficeOptions(db, vm.FallbackOfficeId);
                    return View("Index", vm);
                }

                var by = (Request != null ? (Request.UserHostAddress ?? "") : "").Trim();
                if (string.IsNullOrWhiteSpace(by)) by = "ADMIN";

                // ── Biometrics ────────────────────────────────────────────────────────

                SystemConfigService.Upsert(db, "Biometrics:DlibTolerance",
                    vm.DlibTolerance.ToString(CultureInfo.InvariantCulture), "double",
                    "Dlib face distance tolerance. Lower = stricter match.", by);

                SystemConfigService.Upsert(db, "Biometrics:LivenessThreshold",
                    vm.LivenessThreshold.ToString(CultureInfo.InvariantCulture), "double",
                    "Minimum liveness score to accept a scan.", by);

                // ── Location ──────────────────────────────────────────────────────────

                SystemConfigService.Upsert(db, "Location:GPSAccuracyRequired",
                    vm.GPSAccuracyRequired.ToString(CultureInfo.InvariantCulture), "int",
                    "Max GPS accuracy (meters). Higher means less strict.", by);

                SystemConfigService.Upsert(db, "Location:GPSRadiusDefault",
                    vm.GPSRadiusDefault.ToString(CultureInfo.InvariantCulture), "int",
                    "Default office geofence radius (meters) when office radius is not set.", by);

                SystemConfigService.Upsert(db, "Kiosk:FallbackOfficeId",
                    vm.FallbackOfficeId.ToString(CultureInfo.InvariantCulture), "int",
                    "Office used for desktop kiosks when GPS is skipped. 0 = first active office.", by);

                // ── Attendance ────────────────────────────────────────────────────────

                SystemConfigService.Upsert(db, "Attendance:MinGapSeconds",
                    vm.MinGapSeconds.ToString(CultureInfo.InvariantCulture), "int",
                    "Minimum seconds between scans to prevent double taps.", by);

                // ── Review queue ──────────────────────────────────────────────────────

                SystemConfigService.Upsert(db, "NeedsReview:NearMatchRatio",
                    vm.NeedsReviewNearMatchRatio.ToString(CultureInfo.InvariantCulture), "double",
                    "If distance is within this ratio of the threshold, mark record as NeedsReview.", by);

                SystemConfigService.Upsert(db, "NeedsReview:LivenessMargin",
                    vm.NeedsReviewLivenessMargin.ToString(CultureInfo.InvariantCulture), "double",
                    "If liveness is within this margin above threshold, mark record as NeedsReview.", by);

                SystemConfigService.Upsert(db, "NeedsReview:GPSAccuracyMargin",
                    vm.NeedsReviewGpsMargin.ToString(CultureInfo.InvariantCulture), "int",
                    "If GPS accuracy is within this margin of the required limit, mark record as NeedsReview.", by);

                // ── Advanced liveness ─────────────────────────────────────────────────

                var decision = (vm.LivenessDecision ?? "max").Trim();
                SystemConfigService.Upsert(db, "Biometrics:Liveness:Decision", decision, "string",
                    "How to combine multi-crop liveness results: max or avg.", by);

                var scales = (vm.LivenessMultiCropScales ?? "").Trim();
                SystemConfigService.Upsert(db, "Biometrics:Liveness:MultiCropScales", scales, "string",
                    "Comma-separated crop scales for liveness tuning (example: 2.3,2.7,3.1).", by);

                SystemConfigService.Upsert(db, "Biometrics:LivenessInputSize",
                    vm.LivenessInputSize.ToString(CultureInfo.InvariantCulture), "int",
                    "Liveness model input size (pixels). Must match the model.", by);

                SystemConfigService.Upsert(db, "Biometrics:Liveness:CropScale",
                    vm.LivenessCropScale.ToString(CultureInfo.InvariantCulture), "double",
                    "Crop scale around detected face before liveness inference.", by);

                SystemConfigService.Upsert(db, "Biometrics:Liveness:RealIndex",
                    vm.LivenessRealIndex.ToString(CultureInfo.InvariantCulture), "int",
                    "Index of the REAL class in model output.", by);

                var outType = (vm.LivenessOutputType ?? "logits").Trim();
                SystemConfigService.Upsert(db, "Biometrics:Liveness:OutputType", outType, "string",
                    "Model output type: logits or probs.", by);

                var norm = (vm.LivenessNormalize ?? "0_1").Trim();
                SystemConfigService.Upsert(db, "Biometrics:Liveness:Normalize", norm, "string",
                    "Input normalize mode: 0_1, minus1_1, imagenet, none.", by);

                var order = (vm.LivenessChannelOrder ?? "RGB").Trim();
                SystemConfigService.Upsert(db, "Biometrics:Liveness:ChannelOrder", order, "string",
                    "Channel order for tensor: RGB or BGR.", by);

                SystemConfigService.Upsert(db, "Biometrics:Liveness:RunTimeoutMs",
                    vm.LivenessRunTimeoutMs.ToString(CultureInfo.InvariantCulture), "int",
                    "Max ONNX run time before the circuit breaker trips.", by);

                SystemConfigService.Upsert(db, "Biometrics:Liveness:SlowMs",
                    vm.LivenessSlowMs.ToString(CultureInfo.InvariantCulture), "int",
                    "Milliseconds considered slow for liveness inference.", by);

                SystemConfigService.Upsert(db, "Biometrics:Liveness:GateWaitMs",
                    vm.LivenessGateWaitMs.ToString(CultureInfo.InvariantCulture), "int",
                    "Max wait time to enter the single-flight liveness gate.", by);

                SystemConfigService.Upsert(db, "Biometrics:Liveness:CircuitFailStreak",
                    vm.LivenessCircuitFailStreak.ToString(CultureInfo.InvariantCulture), "int",
                    "How many failures before circuit opens.", by);

                SystemConfigService.Upsert(db, "Biometrics:Liveness:CircuitDisableSeconds",
                    vm.LivenessCircuitDisableSeconds.ToString(CultureInfo.InvariantCulture), "int",
                    "How long to disable liveness after failures.", by);

                // ── Performance (Phase 3 keys) ─────────────────────────────────────────

                SystemConfigService.Upsert(db, "Biometrics:BallTreeThreshold",
                    vm.BallTreeThreshold.ToString(CultureInfo.InvariantCulture), "int",
                    "Build BallTree face index when enrolled employee count >= this value.", by);

                SystemConfigService.Upsert(db, "Biometrics:BallTreeLeafSize",
                    vm.BallTreeLeafSize.ToString(CultureInfo.InvariantCulture), "int",
                    "BallTree leaf size. Default 16. Range 4–64.", by);

                SystemConfigService.Upsert(db, "Biometrics:MaxImageDimension",
                    vm.MaxImageDimension.ToString(CultureInfo.InvariantCulture), "int",
                    "Resize images larger than this on either axis before face detection.", by);

                SystemConfigService.Upsert(db, "Biometrics:PreprocessJpegQuality",
                    vm.PreprocessJpegQuality.ToString(CultureInfo.InvariantCulture), "int",
                    "JPEG quality of the resized temp image. Range 40–95.", by);

                SystemConfigService.Upsert(db, "Biometrics:FaceMatchTunerEnabled",
                    vm.FaceMatchTunerEnabled ? "true" : "false", "bool",
                    "Enable adaptive face match tolerance tuning based on image quality.", by);

                // ── Visitors ──────────────────────────────────────────────────────────

                SystemConfigService.Upsert(db, "Visitors:MaxRecords",
                    vm.VisitorMaxRecords.ToString(CultureInfo.InvariantCulture), "int",
                    "Max visitor log records to retain (soft cap).", by);

                SystemConfigService.Upsert(db, "Visitors:RetentionYears",
                    vm.VisitorRetentionYears.ToString(CultureInfo.InvariantCulture), "int",
                    "Delete visitor logs older than this many years when cleanup is run.", by);

                TempData["msg"] = "Settings saved.";
                return RedirectToAction("Index");
            }
        }

        // ── BuildVm ─────────────────────────────────────────────────────────────────

        private SettingsVm BuildVm(FaceAttendDBEntities db)
        {
            // Biometrics
            var tolFallback   = AppSettings.GetDouble("Biometrics:DlibTolerance", 0.60);
            var tol           = SystemConfigService.GetDouble(db, "Biometrics:DlibTolerance",
                                    SystemConfigService.GetDouble(db, "DlibTolerance", tolFallback));

            var liveFallback  = AppSettings.GetDouble("Biometrics:LivenessThreshold", 0.75);
            var live          = SystemConfigService.GetDouble(db, "Biometrics:LivenessThreshold", liveFallback);

            // Location
            var accFallback   = AppSettings.GetInt("Location:GPSAccuracyRequired", 50);
            var acc           = SystemConfigService.GetInt(db, "Location:GPSAccuracyRequired", accFallback);

            var radFallback   = AppSettings.GetInt("Location:GPSRadiusDefault", 100);
            var rad           = SystemConfigService.GetInt(db, "Location:GPSRadiusDefault", radFallback);

            var fbFallback    = AppSettings.GetInt("Kiosk:FallbackOfficeId", 0);
            var fb            = SystemConfigService.GetInt(db, "Kiosk:FallbackOfficeId", fbFallback);

            // Attendance
            var gapFallback   = AppSettings.GetInt("Attendance:MinGapSeconds", 10);
            var gap           = SystemConfigService.GetInt(db, "Attendance:MinGapSeconds", gapFallback);

            // Review queue
            var nearMatch     = SystemConfigService.GetDouble(db, "NeedsReview:NearMatchRatio", 0.90);
            var liveMargin    = SystemConfigService.GetDouble(db, "NeedsReview:LivenessMargin", 0.03);
            var gpsMargin     = SystemConfigService.GetInt(db, "NeedsReview:GPSAccuracyMargin", 10);

            // Advanced liveness
            var decision      = SystemConfigService.GetString(db, "Biometrics:Liveness:Decision",
                                    AppSettings.GetString("Biometrics:Liveness:Decision", "max"));
            var scales        = SystemConfigService.GetString(db, "Biometrics:Liveness:MultiCropScales",
                                    AppSettings.GetString("Biometrics:Liveness:MultiCropScales", ""));
            var inputSize     = SystemConfigService.GetInt(db, "Biometrics:LivenessInputSize",
                                    AppSettings.GetInt("Biometrics:LivenessInputSize", 128));
            var cropScale     = SystemConfigService.GetDouble(db, "Biometrics:Liveness:CropScale",
                                    AppSettings.GetDouble("Biometrics:Liveness:CropScale", 2.7));
            var realIndex     = SystemConfigService.GetInt(db, "Biometrics:Liveness:RealIndex",
                                    AppSettings.GetInt("Biometrics:Liveness:RealIndex", 1));
            var outputType    = SystemConfigService.GetString(db, "Biometrics:Liveness:OutputType",
                                    AppSettings.GetString("Biometrics:Liveness:OutputType", "logits"));
            var normalize     = SystemConfigService.GetString(db, "Biometrics:Liveness:Normalize",
                                    AppSettings.GetString("Biometrics:Liveness:Normalize", "0_1"));
            var chanOrder     = SystemConfigService.GetString(db, "Biometrics:Liveness:ChannelOrder",
                                    AppSettings.GetString("Biometrics:Liveness:ChannelOrder", "RGB"));
            var timeoutMs     = SystemConfigService.GetInt(db, "Biometrics:Liveness:RunTimeoutMs",
                                    AppSettings.GetInt("Biometrics:Liveness:RunTimeoutMs", 1500));
            var slowMs        = SystemConfigService.GetInt(db, "Biometrics:Liveness:SlowMs",
                                    AppSettings.GetInt("Biometrics:Liveness:SlowMs", 1200));
            var gateWaitMs    = SystemConfigService.GetInt(db, "Biometrics:Liveness:GateWaitMs",
                                    AppSettings.GetInt("Biometrics:Liveness:GateWaitMs", 300));
            var failStreak    = SystemConfigService.GetInt(db, "Biometrics:Liveness:CircuitFailStreak",
                                    AppSettings.GetInt("Biometrics:Liveness:CircuitFailStreak", 3));
            var disableSec    = SystemConfigService.GetInt(db, "Biometrics:Liveness:CircuitDisableSeconds",
                                    AppSettings.GetInt("Biometrics:Liveness:CircuitDisableSeconds", 30));

            // Performance (Phase 3 keys)
            var ballTreeTh    = SystemConfigService.GetInt(db, "Biometrics:BallTreeThreshold",
                                    AppSettings.GetInt("Biometrics:BallTreeThreshold", 50));
            var ballTreeLeaf  = SystemConfigService.GetInt(db, "Biometrics:BallTreeLeafSize",
                                    AppSettings.GetInt("Biometrics:BallTreeLeafSize", 16));
            var maxDim        = SystemConfigService.GetInt(db, "Biometrics:MaxImageDimension",
                                    AppSettings.GetInt("Biometrics:MaxImageDimension", 1280));
            var jpegQ         = SystemConfigService.GetInt(db, "Biometrics:PreprocessJpegQuality",
                                    AppSettings.GetInt("Biometrics:PreprocessJpegQuality", 85));
            var tunerEnabled  = SystemConfigService.GetBool(db, "Biometrics:FaceMatchTunerEnabled",
                                    AppSettings.GetBool("Biometrics:FaceMatchTunerEnabled", false));

            // Visitors
            var visMaxRec     = SystemConfigService.GetInt(db, "Visitors:MaxRecords",
                                    AppSettings.GetInt("Visitors:MaxRecords", 10000));
            var visRetYears   = SystemConfigService.GetInt(db, "Visitors:RetentionYears",
                                    AppSettings.GetInt("Visitors:RetentionYears", 2));

            var vm = new SettingsVm
            {
                // Biometrics
                DlibTolerance                = tol,
                LivenessThreshold            = live,

                // Advanced liveness
                LivenessDecision             = (decision ?? "max").Trim(),
                LivenessMultiCropScales      = (scales ?? "").Trim(),
                LivenessInputSize            = inputSize,
                LivenessCropScale            = cropScale,
                LivenessRealIndex            = realIndex,
                LivenessOutputType           = (outputType ?? "logits").Trim(),
                LivenessNormalize            = (normalize ?? "0_1").Trim(),
                LivenessChannelOrder         = (chanOrder ?? "RGB").Trim(),
                LivenessRunTimeoutMs         = timeoutMs,
                LivenessSlowMs               = slowMs,
                LivenessGateWaitMs           = gateWaitMs,
                LivenessCircuitFailStreak    = failStreak,
                LivenessCircuitDisableSeconds = disableSec,

                // Performance
                BallTreeThreshold            = ballTreeTh,
                BallTreeLeafSize             = ballTreeLeaf,
                MaxImageDimension            = maxDim,
                PreprocessJpegQuality        = jpegQ,
                FaceMatchTunerEnabled        = tunerEnabled,

                // Location
                GPSAccuracyRequired          = acc,
                GPSRadiusDefault             = rad,
                FallbackOfficeId             = fb,

                // Attendance
                MinGapSeconds                = gap,

                // Review queue
                NeedsReviewNearMatchRatio    = nearMatch,
                NeedsReviewLivenessMargin    = liveMargin,
                NeedsReviewGpsMargin         = gpsMargin,

                // Visitors
                VisitorMaxRecords            = visMaxRec,
                VisitorRetentionYears        = visRetYears,

                OfficeOptions = BuildOfficeOptions(db, fb)
            };

            // Warn if legacy key exists so it can be cleaned up.
            if (SystemConfigService.HasKey(db, "DlibTolerance") &&
                !SystemConfigService.HasKey(db, "Biometrics:DlibTolerance"))
            {
                vm.WarningMessage = "Legacy key DlibTolerance exists in SystemConfiguration. "
                    + "New key Biometrics:DlibTolerance is preferred. "
                    + "Save settings once to migrate.";
            }

            return vm;
        }

        private static System.Collections.Generic.List<SelectListItem> BuildOfficeOptions(
            FaceAttendDBEntities db, int selected)
        {
            var list = new System.Collections.Generic.List<SelectListItem>
            {
                new SelectListItem
                {
                    Text     = "Auto (first active office)",
                    Value    = "0",
                    Selected = selected <= 0
                }
            };

            db.Offices.AsNoTracking()
              .Where(o => o.IsActive)
              .OrderBy(o => o.Name)
              .ToList()
              .ForEach(o => list.Add(new SelectListItem
              {
                  Text     = o.Name,
                  Value    = o.Id.ToString(),
                  Selected = selected == o.Id
              }));

            return list;
        }
    }
}
