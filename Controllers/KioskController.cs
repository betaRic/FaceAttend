using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Runtime.Caching;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Mvc;
using FaceAttend.Filters;
using FaceAttend.Services;
using static FaceAttend.Services.DeviceService;
using FaceAttend.Services.Biometrics;
using FaceAttend.Services.Helpers;
using FaceAttend.Services.Security;
using static FaceAttend.Services.Security.FileSecurityService;
using static FaceAttend.Services.OfficeLocationService;
using FaceRecognitionDotNet;

namespace FaceAttend.Controllers
{
    public partial class KioskController : Controller
    {
        // --- Visitor scan cache ---

        private class VisitorScanCacheItem
        {
            public double[] Vec { get; set; }
            public int OfficeId { get; set; }
            public int? VisitorId { get; set; }
            public string VisitorName { get; set; }
            public string SessionBinding { get; set; }
        }

        private static readonly MemoryCache _visitorScanCache = MemoryCache.Default;
        private static int _activeScanCount;
        private const string VisitorScanPrefix = "VISITORSCAN::";

        // --- Multi-frame voting: MEDIUM-tier matches need a second confirming frame ---
        // A PendingScan is stored when the first frame is MEDIUM-tier.
        // The second scan from the same device (within TTL) checks whether it matches
        // the same employee. If yes → record. If not → reject both.
        private class PendingScan
        {
            public string   EmployeeId   { get; set; }
            public int      EmployeeDbId { get; set; }
            public double   Distance     { get; set; }
            public float    Liveness     { get; set; }
            public double   AmbiguityGap { get; set; }
            public int      OfficeId     { get; set; }
            public string   OfficeName   { get; set; }
            public string   DeviceKey    { get; set; }
            public DateTime CreatedUtc   { get; set; }
        }

        private static readonly MemoryCache _pendingScans   = MemoryCache.Default;
        private const string PendingScanPrefix = "PENDINGSCAN::";

        private static int GetPendingScanTtlSeconds()
        {
            var s = ConfigurationService.GetInt("Kiosk:PendingScanTtlSeconds", 8);
            return s < 3 ? 3 : (s > 30 ? 30 : s);
        }

        private static int GetVisitorScanTtlSeconds()
        {
            var s = ConfigurationService.GetInt("Kiosk:VisitorScanTtlSeconds", 180);
            return s < 30 ? 30 : s;
        }

        private static string NewScanId()
        {
            return Guid.NewGuid().ToString("N");
        }

        [HttpGet]
        public ActionResult Index(string returnUrl, int? unlock)
        {
            ViewBag.ReturnUrl   = AdminAuthorizeAttribute.SanitizeReturnUrl(returnUrl);
            ViewBag.UnlockHint  = (unlock ?? 0) == 1;
            ViewBag.AllowUnlock = !DeviceService.IsMobileDevice(Request);
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RateLimit(Name = "KioskResolve", MaxRequests = 150, WindowSeconds = 60, Burst = 20)]
        public ActionResult ResolveOffice(double? lat, double? lon, double? accuracy)
        {
            using (var db = new FaceAttendDBEntities())
            {
                bool gpsRequired = DeviceService.IsMobileDevice(Request);
                bool hasCoords   = lat.HasValue && lon.HasValue;

                if (!hasCoords)
                {
                    if (gpsRequired)
                    {
                        int requiredAccuracy = ConfigurationService.GetInt(
                            db, "Location:GPSAccuracyRequired",
                            ConfigurationService.GetInt("Location:GPSAccuracyRequired", 50));
                        return JsonResponseBuilder.OfficeResolved(
                            allowed: false, gpsRequired: true,
                            reason: "GPS_REQUIRED", requiredAccuracy: requiredAccuracy, accuracy: accuracy);
                    }

                    var fallback = OfficeLocationService.GetFallbackOffice(db);
                    return JsonResponseBuilder.OfficeResolved(
                        allowed: true, gpsRequired: false,
                        officeId: fallback?.Id, officeName: fallback?.Name);
                }

                var pick = OfficeLocationService.PickOffice(db, lat.Value, lon.Value, accuracy);
                if (!pick.Allowed)
                {
                    return JsonResponseBuilder.OfficeResolved(
                        allowed: false, gpsRequired: gpsRequired,
                        reason: pick.Reason, requiredAccuracy: pick.RequiredAccuracy, accuracy: accuracy);
                }

                return JsonResponseBuilder.OfficeResolved(
                    allowed: true, gpsRequired: gpsRequired,
                    officeId: pick.Office.Id, officeName: pick.Office.Name, reason: "OK");
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RateLimit(Name = "KioskAttend", MaxRequests = 60, WindowSeconds = 60, Burst = 20)]
        public ActionResult Attend(double? lat, double? lon, double? accuracy,
            HttpPostedFileBase image,
            int? faceX, int? faceY, int? faceW, int? faceH,
            string deviceToken = null)
        {
            var requestedAtUtc = TimeZoneHelper.NowLocal();

            var activeScans = Interlocked.Increment(ref _activeScanCount);
            try
            {
                var maxConcurrentScans = GetMaxConcurrentScans();
                if (activeScans > maxConcurrentScans)
                {
                    Response.StatusCode = 503;
                    Response.AddHeader("Retry-After", "2");
                    return JsonResponseBuilder.SystemBusy(2);
                }

                DlibBiometrics.FaceBox clientFaceBox = null;
                if (faceX.HasValue && faceY.HasValue && faceW.HasValue && faceH.HasValue
                    && faceW.Value > 0 && faceH.Value > 0)
                {
                    clientFaceBox = new DlibBiometrics.FaceBox
                    {
                        Left   = faceX.Value,
                        Top    = faceY.Value,
                        Width  = faceW.Value,
                        Height = faceH.Value
                    };
                }

                return ScanAttendanceCore(
                    lat, lon, accuracy, image, clientFaceBox, requestedAtUtc,
                    includePerfTimings: ConfigurationService.GetBool("Kiosk:EnablePerfTimings", false),
                    deviceToken: deviceToken);
            }
            finally
            {
                Interlocked.Decrement(ref _activeScanCount);
            }
        }

        private static bool TryBuildFaceLocationFromClientBox(
            DlibBiometrics.FaceBox sourceBox,
            out DlibBiometrics.FaceBox faceBox,
            out FaceRecognitionDotNet.Location faceLoc)
        {
            faceBox  = null;
            faceLoc  = default(FaceRecognitionDotNet.Location);

            if (sourceBox == null || sourceBox.Width <= 20 || sourceBox.Height <= 20)
                return false;

            var padX   = Math.Max(6, (int)Math.Round(sourceBox.Width  * 0.10));
            var padY   = Math.Max(6, (int)Math.Round(sourceBox.Height * 0.12));
            var left   = Math.Max(0, sourceBox.Left - padX);
            var top    = Math.Max(0, sourceBox.Top  - padY);
            var width  = Math.Max(1, sourceBox.Width  + (padX * 2));
            var height = Math.Max(1, sourceBox.Height + (padY * 2));

            faceBox = new DlibBiometrics.FaceBox
                { Left = left, Top = top, Width = width, Height = height };

            faceLoc = new FaceRecognitionDotNet.Location(
                faceBox.Left, faceBox.Top,
                faceBox.Left + faceBox.Width,
                faceBox.Top  + faceBox.Height);

            return true;
        }

        private ActionResult ScanAttendanceCore(
            double? lat, double? lon, double? accuracy,
            HttpPostedFileBase image,
            DlibBiometrics.FaceBox clientFaceBox,
            DateTime requestedAtUtc,
            bool includePerfTimings,
            string deviceToken = null)
        {
            var sw      = Stopwatch.StartNew();
            var timings = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            System.Action<string> mark = key => { if (includePerfTimings) timings[key] = sw.ElapsedMilliseconds; };

            if (image == null || image.ContentLength <= 0)
                return JsonResponseBuilder.ErrorWithTimings("NO_IMAGE", timings, includePerfTimings);

            var max = ConfigurationService.GetInt("Biometrics:MaxUploadBytes", 10 * 1024 * 1024);
            if (image.ContentLength > max)
                return JsonResponseBuilder.ErrorWithTimings("TOO_LARGE", timings, includePerfTimings);

            if (!IsValidImage(image.InputStream, new[] { ".jpg", ".jpeg", ".png" }))
                return JsonResponseBuilder.ErrorWithTimings("INVALID_IMAGE_FORMAT", timings, includePerfTimings);

            string path          = null;
            string processedPath = null;

            try
            {
                using (var db = new FaceAttendDBEntities())
                {
                    // ── Office resolution ─────────────────────────────────────────────
                    Office office           = null;
                    bool   gpsRequired      = DeviceService.IsMobileDevice(Request);
                    bool   locationVerified = false;
                    int    requiredAcc      = 0;

                    if (lat.HasValue && lon.HasValue)
                    {
                        var pick = OfficeLocationService.PickOffice(db, lat.Value, lon.Value, accuracy);
                        if (!pick.Allowed)
                            return JsonResponseBuilder.ErrorWithTimings(pick.Reason, timings, includePerfTimings);

                        office           = pick.Office;
                        locationVerified = true;
                        requiredAcc      = pick.RequiredAccuracy;
                    }
                    else if (gpsRequired)
                    {
                        return JsonResponseBuilder.ErrorWithTimings("GPS_REQUIRED", timings, includePerfTimings);
                    }
                    else
                    {
                        office           = OfficeLocationService.GetFallbackOffice(db);
                        locationVerified = false;
                    }

                    if (office == null)
                        return NoOfficesResult(includePerfTimings, timings);

                    // ── Image save + preprocess ───────────────────────────────────────
                    path = SaveTemp(image, "k_", max);
                    mark("saved_ms");
                    if (IsRequestTimedOut(sw)) return RequestTimeoutResult(includePerfTimings, timings);

                    bool isProcessed;
                    processedPath = ImagePreprocessor.PreprocessForDetection(path, "k_", out isProcessed);
                    mark("preprocess_ms");
                    if (IsRequestTimedOut(sw)) return RequestTimeoutResult(includePerfTimings, timings);

                    var dlib = new DlibBiometrics();

                    // ── Face detection ────────────────────────────────────────────────
                    DlibBiometrics.FaceBox          faceBox;
                    FaceRecognitionDotNet.Location  faceLoc;
                    string                          faceErr;
                    bool                            usedClientBox = false;

                    if (TryBuildFaceLocationFromClientBox(clientFaceBox, out faceBox, out faceLoc))
                    {
                        usedClientBox = true;
                        faceErr       = null;
                    }
                    else
                    {
                        if (!dlib.TryDetectBestFaceFromFile(processedPath, out faceBox, out faceLoc, out faceErr,
                            allowLargestFace: true, primaryUpsample: 0, retryUpsampleOnNoFace: true))
                        {
                            return JsonResponseBuilder.ErrorWithTimings(faceErr ?? "FACE_FAIL", timings, includePerfTimings);
                        }
                    }

                    mark(usedClientBox ? "detect_skip_ms" : "dlib_detect_ms");
                    if (IsRequestTimedOut(sw)) return RequestTimeoutResult(includePerfTimings, timings);

                    // ── Liveness threshold (read once) ────────────────────────────────
                    var liveTh = (float)ConfigurationService.GetDoubleCached(
                        "Biometrics:LivenessThreshold",
                        ConfigurationService.GetDouble("Biometrics:LivenessThreshold", 0.75));

                    // ── Embedding + liveness ──────────────────────────────────────────
                    double[] vec              = null;
                    float    p                = 0f;
                    bool     livenessConfirmed = false;   // FIX 2: explicit flag
                    int      actualImageWidth  = 1280;

                    if (!usedClientBox && ConfigurationService.GetBool("Kiosk:UseFastPipeline", true))
                    {
                        image.InputStream.Position = 0;
                        var fastResult = FastScanPipeline.ScanInMemory(image, includePerfTimings);

                        if (fastResult.Timings != null)
                            foreach (var t in fastResult.Timings)
                                timings["fast_" + t.Key] = t.Value;

                        if (!fastResult.Ok)
                        {
                            var debug = ConfigurationService.GetBool("Biometrics:Debug", false);
                            return JsonResponseBuilder.EncodingFail(fastResult.Error, timings, includePerfTimings, debug);
                        }

                        // FIX 1: Liveness gate was missing from the fast pipeline path.
                        // The legacy path returned LivenessFail here; the fast path did not.
                        if (!fastResult.LivenessOk)
                            return JsonResponseBuilder.LivenessFail(fastResult.LivenessScore, liveTh, timings, includePerfTimings);

                        vec               = fastResult.FaceEncoding;
                        p                 = fastResult.LivenessScore;
                        actualImageWidth  = fastResult.ImageWidth;
                        livenessConfirmed = true;   // FIX 2: liveness confirmed
                        mark("fast_pipeline_ms");
                    }
                    else
                    {
                        // Legacy sequential path
                        var live   = new OnnxLiveness();
                        var scored = live.ScoreFromFile(processedPath, faceBox);
                        mark("liveness_ms");
                        if (IsRequestTimedOut(sw)) return RequestTimeoutResult(includePerfTimings, timings);

                        var skipLiveness = ConfigurationService.GetBool("Biometrics:SkipLiveness", false);

                        if (!scored.Ok && !skipLiveness)
                            return JsonResponseBuilder.ErrorWithTimings(scored.Error, timings, includePerfTimings);

                        p = scored.Probability ?? 0f;

                        if (p < liveTh && !skipLiveness)
                            return JsonResponseBuilder.LivenessFail(p, liveTh, timings, includePerfTimings);

                        livenessConfirmed = !skipLiveness;   // FIX 2: only confirmed if not skipped

                        string encErr;
                        if (!dlib.TryEncodeFromFileWithLocation(processedPath, faceLoc, out vec, out encErr) || vec == null)
                        {
                            var debug = ConfigurationService.GetBool("Biometrics:Debug", false);
                            return JsonResponseBuilder.EncodingFail(encErr, timings, includePerfTimings, debug);
                        }

                        try
                        {
                            if (!string.IsNullOrEmpty(processedPath) && System.IO.File.Exists(processedPath))
                                using (var img = System.Drawing.Image.FromFile(processedPath))
                                    actualImageWidth = img.Width;
                        }
                        catch { }

                        mark("dlib_encode_ms");
                    }

                    if (vec == null)
                        return JsonResponseBuilder.ErrorWithTimings("ENCODING_FAIL", timings, includePerfTimings);

                    if (IsRequestTimedOut(sw)) return RequestTimeoutResult(includePerfTimings, timings);

                    // ── FIX 3: Tolerance capped at 0.60. Angle-relax removed. ────────
                    var attendanceTol = ConfigurationService.GetDouble("Biometrics:AttendanceTolerance", 0.60);
                    // Mobile cameras produce higher cross-device Dlib distances
                    // (~0.62-0.70) vs same-device (~0.35-0.55) due to different
                    // sensor/focal-length/ISP. Use a separate config key with a
                    // higher cap so the match even reaches the tier logic.
                    // Tier system (HighDist/MedDist/gap) provides the real security gate.
                    var isMobileAttend = DeviceService.IsMobileDevice(Request);
                    if (isMobileAttend)
                    {
                        var mobileTol = ConfigurationService.GetDouble(
                            "Biometrics:MobileAttendanceTolerance", 0.68);
                        attendanceTol = Math.Max(0.55, Math.Min(0.72, mobileTol));
                    }
                    else
                    {
                        attendanceTol = Math.Max(0.50, Math.Min(0.60, attendanceTol));
                    }

                    // ── Matching (single authority — FastFaceMatcher) ─────────────────
                    if (!FastFaceMatcher.IsInitialized)
                        FastFaceMatcher.Initialize();

                    var matchResult = FastFaceMatcher.FindBestMatch(vec, attendanceTol);
                    mark("match_ms");

                    // ── Unknown / visitor path ────────────────────────────────────────
                    if (!matchResult.IsMatch)
                    {
                        var fingerprint     = DeviceService.GenerateFingerprint(Request);
                        var isMobile        = DeviceService.IsMobileDevice(Request);
                        var kioskCookieUnk  = Request.Cookies["ForceKioskMode"];
                        var forceKioskUnk   = (kioskCookieUnk != null && kioskCookieUnk.Value == "true");

                        if (isMobile && !forceKioskUnk)
                        {
                            return JsonResponseBuilder.SelfEnrollOffer(
                                fingerprint,
                                "Face not recognized. Would you like to enroll as a new employee?");
                        }

                        double vTol = ConfigurationService.GetDouble("Visitors:DlibTolerance", attendanceTol);

                        int?   bestVisitorId   = null;
                        string bestVisitorName = null;
                        double bestVisitorDist = double.PositiveInfinity;

                        try
                        {
                            var entries = VisitorFaceIndex.GetEntries(db);
                            foreach (var e in entries)
                            {
                                var d = DlibBiometrics.Distance(vec, e.Vec);
                                if (d < bestVisitorDist)
                                {
                                    bestVisitorDist  = d;
                                    bestVisitorId    = e.VisitorId;
                                    bestVisitorName  = e.Name;
                                }
                            }
                        }
                        catch { }

                        bool isKnownVisitor = bestVisitorId.HasValue && bestVisitorDist <= vTol;

                        bool visitorEnabled = ConfigurationService.GetBool(
                            db, "Kiosk:VisitorEnabled",
                            ConfigurationService.GetBool("Kiosk:VisitorEnabled", true));

                        if (!visitorEnabled)
                            return JsonResponseBuilder.NotRecognized(timings, includePerfTimings);

                        var scanId = NewScanId();
                        var key    = VisitorScanPrefix + scanId;

                        _visitorScanCache.Set(
                            key,
                            new VisitorScanCacheItem
                            {
                                Vec            = vec,
                                OfficeId       = office.Id,
                                VisitorId      = isKnownVisitor ? bestVisitorId    : (int?)null,
                                VisitorName    = isKnownVisitor ? bestVisitorName  : null,
                                SessionBinding = DeviceService.GetVisitorSessionBinding(HttpContext)
                            },
                            DateTimeOffset.UtcNow.AddSeconds(GetVisitorScanTtlSeconds()));

                        return JsonResponseBuilder.VisitorScan(
                            scanId, isKnownVisitor, bestVisitorName, bestVisitorDist, vTol, p,
                            timings, includePerfTimings);
                    }

                    // ── Tier-based acceptance ─────────────────────────────────────────
                    //
                    // HIGH   → accept immediately.
                    // MEDIUM → first frame stores a pending confirmation; second frame
                    //          from the same device must match the same employee.
                    //          This catches accidental single-frame false positives.
                    // LOW    → reject (FindBestMatch already returns IsMatch=false for Low,
                    //          so this branch is a safety backstop only).

                    var bestEmpId = matchResult.Employee?.EmployeeId;
                    var bestDist  = matchResult.Distance;

                    if (matchResult.Tier == FastFaceMatcher.MatchTier.Medium)
                    {
                        // Device key: use token if available, fall back to fingerprint
                        var deviceKeyMed = deviceToken
                            ?? DeviceService.GetDeviceTokenFromCookie(Request)
                            ?? DeviceService.GenerateFingerprint(Request);

                        var pendingKey = PendingScanPrefix + deviceKeyMed;
                        var existing   = _pendingScans.Get(pendingKey) as PendingScan;

                        if (existing == null)
                        {
                            // First MEDIUM frame — store and ask the client to scan again
                            _pendingScans.Set(
                                pendingKey,
                                new PendingScan
                                {
                                    EmployeeId   = bestEmpId,
                                    EmployeeDbId = matchResult.Employee?.Id ?? 0,
                                    Distance     = bestDist,
                                    Liveness     = p,
                                    AmbiguityGap = matchResult.AmbiguityGap,
                                    OfficeId     = office.Id,
                                    OfficeName   = office.Name,
                                    DeviceKey    = deviceKeyMed,
                                    CreatedUtc   = DateTime.UtcNow
                                },
                                DateTimeOffset.UtcNow.AddSeconds(GetPendingScanTtlSeconds()));

                            System.Diagnostics.Trace.TraceInformation(
                                "[SCAN] MEDIUM_PENDING | emp={0} d={1:F3} gap={2:F3} — awaiting confirm",
                                bestEmpId, bestDist, matchResult.AmbiguityGap);

                            return Json(new
                            {
                                ok      = false,
                                error   = "SCAN_CONFIRM_NEEDED",
                                message = "Almost there — please look at the camera one more time.",
                                liveness  = p,
                                threshold = liveTh
                            });
                        }

                        // Second frame — check it matches the same employee
                        _pendingScans.Remove(pendingKey);

                        if (!string.Equals(existing.EmployeeId, bestEmpId, StringComparison.OrdinalIgnoreCase))
                        {
                            System.Diagnostics.Trace.TraceInformation(
                                "[SCAN] MEDIUM_MISMATCH | frame1={0} frame2={1} — both rejected",
                                existing.EmployeeId, bestEmpId);
                            return JsonResponseBuilder.NotRecognized(timings, includePerfTimings);
                        }

                        // INTEGRITY CHECK: average of both frames must still be tight
                        var avgDist = (existing.Distance + bestDist) / 2.0;
                        var bestOfTwo = Math.Min(existing.Distance, bestDist);

                        System.Diagnostics.Trace.TraceInformation(
                            "[SCAN] MEDIUM_CONFIRMED | emp={0} d1={1:F3} d2={2:F3} avg={3:F3} best={4:F3}",
                            bestEmpId, existing.Distance, bestDist, avgDist, bestOfTwo);

                        // Reject if average is too high OR best frame still above HIGH threshold
                        // Two borderline frames should not confirm each other
                        if (avgDist > FastFaceMatcher.MedDistThresholdPublic ||
                            bestOfTwo > FastFaceMatcher.HighDistThresholdPublic * 1.15)
                        {
                            System.Diagnostics.Trace.TraceInformation(
                                "[SCAN] MEDIUM_REJECTED_WEAK | avg={0:F3} best={1:F3}", avgDist, bestOfTwo);
                            return JsonResponseBuilder.NotRecognized(timings, includePerfTimings);
                        }

                        bestDist = bestOfTwo;
                    }

                    if (IsRequestTimedOut(sw)) return RequestTimeoutResult(includePerfTimings, timings);

                    // ── Look up employee record ───────────────────────────────────────
                    var emp = db.Employees.FirstOrDefault(x => x.EmployeeId == bestEmpId && x.Status == "ACTIVE");
                    if (emp == null)
                        return JsonResponseBuilder.ErrorWithTimings("EMPLOYEE_NOT_FOUND", timings, includePerfTimings);

                    // ── Device check (mobile only) ────────────────────────────────────
                    var deviceFingerprint = DeviceService.GenerateFingerprint(Request);
                    var deviceIsMobile    = DeviceService.IsMobileDevice(Request);

                    var kioskCookie    = Request.Cookies["ForceKioskMode"];
                    var chUaMobile     = Request.Headers["Sec-CH-UA-Mobile"];
                    bool isHardwareMobile = (chUaMobile == "?1");
                    var forceKiosk     = !isHardwareMobile && (
                        (kioskCookie != null && kioskCookie.Value == "true") ||
                        (Request.Headers["X-Kiosk-Mode"] == "true"));

                    var tokenFromCookie   = DeviceService.GetDeviceTokenFromCookie(Request);
                    string deviceTokenFromCheck = null;

                    if (deviceIsMobile && !forceKiosk)
                    {
                        var effectiveDeviceToken = deviceToken ?? tokenFromCookie;
                        var activeDevice = db.Devices.FirstOrDefault(d =>
                            d.Status == "ACTIVE" && (
                                (!string.IsNullOrEmpty(effectiveDeviceToken) && d.DeviceToken == effectiveDeviceToken)
                                || d.Fingerprint == deviceFingerprint));

                        if (activeDevice == null)
                        {
                            var anyDevice = db.Devices.FirstOrDefault(d =>
                                (!string.IsNullOrEmpty(effectiveDeviceToken) && d.DeviceToken == effectiveDeviceToken)
                                || d.Fingerprint == deviceFingerprint);

                            if (anyDevice != null && anyDevice.Status == "PENDING")
                                return JsonResponseBuilder.DevicePending("Your device registration is pending admin approval.");

                            if (anyDevice != null && anyDevice.Status == "BLOCKED")
                                return JsonResponseBuilder.DeviceBlocked("This device has been blocked. Contact administrator.");

                            return JsonResponseBuilder.RegisterDeviceRequired(
                                emp.Id,
                                emp.FirstName + " " + emp.LastName,
                                deviceFingerprint,
                                "This device is not registered. Please register it to continue.");
                        }

                        activeDevice.LastUsedAt = DateTime.UtcNow;
                        activeDevice.UseCount   = activeDevice.UseCount + 1;
                        deviceTokenFromCheck    = activeDevice.DeviceToken;
                        if (!string.IsNullOrEmpty(deviceTokenFromCheck))
                            DeviceService.SetDeviceTokenCookie(Response, deviceTokenFromCheck, Request.IsSecureConnection);
                    }

                    var displayName = emp.LastName + ", " + emp.FirstName +
                                      (string.IsNullOrWhiteSpace(emp.MiddleName) ? "" : " " + emp.MiddleName);

                    double? similarity = attendanceTol > 0
                        ? Math.Max(0.0, Math.Min(1.0, 1.0 - (bestDist / attendanceTol)))
                        : (double?)null;

                    // ── NeedsReview flags ─────────────────────────────────────────────
                    var nearMatchRatio = ConfigurationService.GetDoubleCached("NeedsReview:NearMatchRatio", 0.90);
                    var livenessMargin = ConfigurationService.GetDoubleCached("NeedsReview:LivenessMargin", 0.03);
                    var gpsMargin      = ConfigurationService.GetIntCached("NeedsReview:GPSAccuracyMargin", 10);

                    bool needsReviewFlag = false;
                    var  reviewNotes     = new System.Text.StringBuilder();

                    // FIX 5: Near-match is now a REJECTION, not a flag-and-accept.
                    // In a government attendance system a borderline match must not be
                    // recorded — it must be re-attempted with better positioning/lighting.
                    if (attendanceTol > 0 && bestDist >= (attendanceTol * nearMatchRatio))
                        return JsonResponseBuilder.NotRecognized(timings, includePerfTimings);

                    // Liveness margin — flag only, liveness itself was already gated
                    if (p < (liveTh + livenessMargin))
                    {
                        needsReviewFlag = true;
                        reviewNotes.Append("Near liveness. Score=")
                            .Append(p.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))
                            .Append(" (th=").Append(liveTh.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))
                            .Append(")");
                    }

                    // GPS accuracy margin
                    if (gpsRequired && accuracy.HasValue && requiredAcc > 0)
                    {
                        var nearAcc = Math.Max(0, requiredAcc - gpsMargin);
                        if (accuracy.Value >= nearAcc)
                        {
                            if (reviewNotes.Length > 0) reviewNotes.Append(" ");
                            needsReviewFlag = true;
                            reviewNotes.Append("Near GPS accuracy. Acc=")
                                .Append(accuracy.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))
                                .Append("m (req=").Append(requiredAcc).Append("m)");
                        }
                    }

                    // ── Anti-spoofing (GPS) ───────────────────────────────────────────
                    var deviceFp = DeviceService.GetShortDeviceFingerprint(HttpContext);
                    if (lat.HasValue && lon.HasValue)
                    {
                        var spoofCheck = LocationAntiSpoof.CheckLocation(
                            emp.Id, lat.Value, lon.Value, DateTime.UtcNow, deviceFp);

                        if (spoofCheck.Action == "BLOCK")
                            return JsonResponseBuilder.SuspiciousLocation(
                                "Location verification failed. Please contact admin.",
                                timings, includePerfTimings);

                        if (spoofCheck.Action == "WARN")
                        {
                            needsReviewFlag = true;
                            if (reviewNotes.Length > 0) reviewNotes.Append(" ");
                            reviewNotes.Append("GPS repeat: ").Append(spoofCheck.Reason).Append(".");
                        }
                    }

                    // ── Record attendance ─────────────────────────────────────────────
                    // Prepend tier + gap to notes so admins can filter by confidence level
                    var tierNote = string.Format("Tier={0} d={1:F3} gap={2}",
                        matchResult.Tier,
                        bestDist,
                        matchResult.AmbiguityGap == double.PositiveInfinity
                            ? "inf"
                            : matchResult.AmbiguityGap.ToString("F3"));
                    if (reviewNotes.Length > 0) reviewNotes.Insert(0, tierNote + ". ");
                    else reviewNotes.Append(tierNote);

                    var log = new AttendanceLog
                    {
                        EmployeeId       = emp.Id,
                        OfficeId         = office.Id,
                        EmployeeFullName = StringHelper.Truncate(displayName, 400),
                        Department       = StringHelper.Truncate(emp.Department, 200),
                        OfficeType       = StringHelper.Truncate(office.Type, 40),
                        OfficeName       = StringHelper.Truncate(office.Name, 400),
                        GPSLatitude      = OfficeLocationService.TruncateGpsCoordinate(lat),
                        GPSLongitude     = OfficeLocationService.TruncateGpsCoordinate(lon),
                        GPSAccuracy      = accuracy,
                        LocationVerified = locationVerified,
                        FaceDistance     = bestDist,
                        FaceSimilarity   = similarity,
                        MatchThreshold   = attendanceTol,
                        LivenessScore    = p,
                        LivenessResult   = "PASS",
                        ClientIP         = StringHelper.Truncate(Request.UserHostAddress ?? "", 100),
                        UserAgent        = StringHelper.Truncate(Request.UserAgent ?? "", 1000),
                        WiFiBSSID        = StringHelper.Truncate(office.WiFiBSSID, 200),
                        NeedsReview      = needsReviewFlag,
                        Notes            = reviewNotes.ToString()
                    };

                    if (IsRequestTimedOut(sw)) return RequestTimeoutResult(includePerfTimings, timings);

                    var svc = new AttendanceService(db);
                    var rec = svc.Record(log, requestedAtUtc);
                    mark("db_ms");

                    if (!rec.Ok)
                    {
                        if (string.Equals(rec.Code, "TOO_SOON", StringComparison.OrdinalIgnoreCase))
                        {
                            var enforcedGap = rec.ApplicableGapSeconds > 0
                                ? rec.ApplicableGapSeconds
                                : ConfigurationService.GetInt(db, "Attendance:MinGapSeconds",
                                    ConfigurationService.GetInt("Attendance:MinGapSeconds", 180));
                            return JsonResponseBuilder.TooSoon(rec.Message, enforcedGap, timings, includePerfTimings);
                        }
                        return JsonResponseBuilder.ErrorWithTimings(rec.Code, timings, includePerfTimings, rec.Message);
                    }

                    // Adaptive enrollment REMOVED.
                    // Any mechanism that writes face vectors from attendance scans is a
                    // database poisoning vector: a spoofed or misidentified scan silently
                    // overwrites legitimate enrollment data. Vectors are set only via the
                    // admin-supervised EnrollmentController enrollment flow.

                    // Distance log — grep "[MATCH]" to build threshold tuning dataset.
                    System.Diagnostics.Trace.TraceInformation(
                        "[MATCH] emp={0} event={1} tier={2} dist={3:F3} gap={4} liveness={5:F3} tol={6:F3} ms={7}",
                        emp.EmployeeId,
                        rec.EventType,
                        matchResult.Tier,
                        bestDist,
                        matchResult.AmbiguityGap == double.PositiveInfinity
                            ? "inf"
                            : matchResult.AmbiguityGap.ToString("F3"),
                        p,
                        attendanceTol,
                        sw.ElapsedMilliseconds);

                    mark("total_ms");

                    return JsonResponseBuilder.AttendanceSuccess(
                        emp.EmployeeId, displayName, displayName, rec.EventType, rec.Message,
                        office.Id, office.Name, p, bestDist, requestedAtUtc,
                        timings, includePerfTimings, deviceTokenFromCheck);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("[Kiosk.Attend] ScanAttendanceCore failed: " + ex);
                var debug  = ConfigurationService.GetBool("Biometrics:Debug", false);
                var baseEx = ex.GetBaseException();
                return JsonResponseBuilder.ScanError(ex.Message, baseEx?.Message, timings, includePerfTimings, debug);
            }
            finally
            {
                ImagePreprocessor.Cleanup(processedPath, path);
                TryDelete(path);
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RateLimit(Name = "KioskSubmitVisitor", MaxRequests = 30, WindowSeconds = 60, Burst = 10)]
        public ActionResult SubmitVisitor(string scanId, string name, string purpose)
        {
            scanId = (scanId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(scanId))
                return Json(new { ok = false, error = "SCAN_ID_REQUIRED", message = "Scan ID is required." });

            var key  = VisitorScanPrefix + scanId;
            var item = _visitorScanCache.Get(key) as VisitorScanCacheItem;

            if (item == null || item.Vec == null || item.Vec.Length != 128)
                return Json(new { ok = false, error = "SCAN_EXPIRED", message = "Scan expired. Please scan again." });

            if (!string.Equals(item.SessionBinding ?? "", DeviceService.GetVisitorSessionBinding(HttpContext), StringComparison.Ordinal))
                return Json(new { ok = false, error = "SCAN_SESSION_MISMATCH", message = "Scan expired. Please scan again." });

            using (var db = new FaceAttendDBEntities())
            {
                var ip = Request.UserHostAddress ?? "";
                var ua = Request.UserAgent ?? "";

                try
                {
                    VisitorService.RecordResult res;

                    if (item.VisitorId.HasValue)
                    {
                        res = VisitorService.RecordVisit(db, item.VisitorId.Value, item.OfficeId, purpose, ip, ua);
                    }
                    else
                    {
                        name = (name ?? "").Trim();
                        if (string.IsNullOrWhiteSpace(name))
                            return Json(new { ok = false, error = "NAME_REQUIRED", message = "Name is required." });

                        var now   = DateTime.UtcNow;
                        var bytes = DlibBiometrics.EncodeToBytes(item.Vec);
                        var b64   = BiometricCrypto.ProtectBase64Bytes(bytes);

                        if (string.IsNullOrWhiteSpace(b64))
                            return Json(new { ok = false, error = "ENCODE_ERROR", message = "Could not save face." });

                        var v = new Visitor
                        {
                            Name               = name,
                            FaceEncodingBase64 = b64,
                            VisitCount         = 0,
                            FirstVisitDate     = now,
                            LastVisitDate      = now,
                            IsActive           = true
                        };

                        db.Visitors.Add(v);
                        db.SaveChanges();
                        VisitorFaceIndex.Invalidate();

                        res = VisitorService.RecordVisit(db, v.Id, item.OfficeId, purpose, ip, ua);
                    }

                    return Json(new
                    {
                        ok          = res.Ok,
                        mode        = "VISITOR_RECORDED",
                        isKnown     = res.IsKnown,
                        visitorName = res.VisitorName,
                        message     = res.Message,
                        error       = res.Ok ? null : res.Code
                    });
                }
                catch
                {
                    return Json(new { ok = false, error = "VISITOR_SAVE_ERROR", message = "Could not save visitor." });
                }
                finally
                {
                    _visitorScanCache.Remove(key);
                }
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RateLimit(Name = "UnlockPin", MaxRequests = 5, WindowSeconds = 60)]
        public ActionResult UnlockPin(string pin, string returnUrl)
        {
            if (DeviceService.IsMobileDevice(Request))
            {
                Response.StatusCode = 403;
                return Json(new { ok = false, error = "UNLOCK_DISABLED_ON_MOBILE" });
            }

            var safeReturn = AdminAuthorizeAttribute.SanitizeReturnUrl(returnUrl);
            var ip         = Request.UserHostAddress;

            if (!AdminAuthorizeAttribute.VerifyPin(pin, ip))
                return Json(new { ok = false, error = "INVALID_PIN" });

            AdminAuthorizeAttribute.RotateSessionId(HttpContext);
            AdminAuthorizeAttribute.IssueUnlockCookie(HttpContext, ip);

            return Json(new { ok = true, returnUrl = safeReturn });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Lock()
        {
            AdminAuthorizeAttribute.ClearAuthed(Session);
            return RedirectToAction("Index");
        }

        [HttpGet]
        public ActionResult GetOfficesForMap()
        {
            using (var db = new FaceAttendDBEntities())
            {
                var offices = db.Offices
                    .Where(o => o.IsActive)
                    .Select(o => new
                    {
                        id     = o.Id,
                        name   = o.Name,
                        lat    = o.Latitude,
                        lon    = o.Longitude,
                        radius = o.RadiusMeters > 0 ? o.RadiusMeters : 100
                    })
                    .ToList();

                return Json(new { offices }, JsonRequestBehavior.AllowGet);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static int GetMaxConcurrentScans()
        {
            var value = ConfigurationService.GetInt("Kiosk:MaxConcurrentScans", 16);
            return value < 1 ? 1 : value;
        }

        private static int GetRequestTimeoutMs()
        {
            var value = ConfigurationService.GetInt("Kiosk:RequestTimeoutMs", 28000);
            return value < 5000 ? 5000 : value;
        }

        private static bool IsRequestTimedOut(Stopwatch sw)
        {
            return sw != null && sw.ElapsedMilliseconds > GetRequestTimeoutMs();
        }

        private ActionResult RequestTimeoutResult(bool includePerfTimings, IDictionary<string, long> timings)
        {
            Response.StatusCode = 503;
            Response.AddHeader("Retry-After", "2");
            return JsonResponseBuilder.RequestTimeout(timings, includePerfTimings);
        }

        private ActionResult NoOfficesResult(bool includePerfTimings, IDictionary<string, long> timings)
        {
            return JsonResponseBuilder.NoOffices(timings, includePerfTimings);
        }
    }
}