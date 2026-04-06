using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Web;
using System.Web.Mvc;
using FaceAttend.Services;
using FaceAttend.Services.Biometrics;
using FaceAttend.Services.Helpers;
using FaceAttend.Services.Security;
using static FaceAttend.Services.Security.FileSecurityService;
using static FaceAttend.Services.OfficeLocationService;
using FaceRecognitionDotNet;

namespace FaceAttend.Services.Recognition
{
    /// <summary>
    /// Encapsulates the full attendance scan pipeline:
    /// image validation → office resolution → face detection → liveness →
    /// matching → tier voting → device check → attendance recording.
    ///
    /// KioskController.Attend() is a thin HTTP entry point that calls this service.
    /// </summary>
    public class AttendanceScanService
    {
        // ── Timeout helpers ───────────────────────────────────────────────────

        private static int GetRequestTimeoutMs()
        {
            var v = ConfigurationService.GetInt("Kiosk:RequestTimeoutMs", 28000);
            return v < 5000 ? 5000 : v;
        }

        private static bool IsTimedOut(Stopwatch sw)
            => sw != null && sw.ElapsedMilliseconds > GetRequestTimeoutMs();

        private static ActionResult TimeoutResult(HttpResponseBase response, bool perf, IDictionary<string, long> timings)
        {
            response.StatusCode = 503;
            response.AddHeader("Retry-After", "2");
            return JsonResponseBuilder.RequestTimeout(timings, perf);
        }

        private static ActionResult NoOfficesResult(bool perf, IDictionary<string, long> timings)
            => JsonResponseBuilder.NoOffices(timings, perf);

        // ── Face location helper ──────────────────────────────────────────────

        private static bool TryBuildFaceLocationFromClientBox(
            DlibBiometrics.FaceBox sourceBox,
            out DlibBiometrics.FaceBox faceBox,
            out Location faceLoc)
        {
            faceBox = null;
            faceLoc = default(Location);

            if (sourceBox == null || sourceBox.Width <= 20 || sourceBox.Height <= 20)
                return false;

            var padX   = Math.Max(6, (int)Math.Round(sourceBox.Width  * 0.10));
            var padY   = Math.Max(6, (int)Math.Round(sourceBox.Height * 0.12));
            var left   = Math.Max(0, sourceBox.Left - padX);
            var top    = Math.Max(0, sourceBox.Top  - padY);
            var width  = Math.Max(1, sourceBox.Width  + (padX * 2));
            var height = Math.Max(1, sourceBox.Height + (padY * 2));

            faceBox = new DlibBiometrics.FaceBox { Left = left, Top = top, Width = width, Height = height };
            faceLoc = new Location(faceBox.Left, faceBox.Top, faceBox.Left + faceBox.Width, faceBox.Top + faceBox.Height);
            return true;
        }

        // ── Main pipeline ─────────────────────────────────────────────────────

        public ActionResult Scan(
            double? lat, double? lon, double? accuracy,
            HttpPostedFileBase image,
            DlibBiometrics.FaceBox clientFaceBox,
            DateTime requestedAtUtc,
            bool includePerfTimings,
            string deviceToken,
            HttpContextBase httpContext)
        {
            var request  = httpContext.Request;
            var response = httpContext.Response;

            var sw      = Stopwatch.StartNew();
            var timings = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            Action<string> mark = key => { if (includePerfTimings) timings[key] = sw.ElapsedMilliseconds; };

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
                    bool   gpsRequired      = DeviceService.IsMobileDevice(request);
                    bool   locationVerified = false;
                    int    requiredAcc      = 0;

                    if (lat.HasValue && lon.HasValue)
                    {
                        var pick = PickOffice(db, lat.Value, lon.Value, accuracy);
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
                        office           = GetFallbackOffice(db);
                        locationVerified = false;
                    }

                    if (office == null)
                        return NoOfficesResult(includePerfTimings, timings);

                    // ── Image save + preprocess ───────────────────────────────────────
                    path = SaveTemp(image, "k_", max);
                    mark("saved_ms");
                    if (IsTimedOut(sw)) return TimeoutResult(response, includePerfTimings, timings);

                    bool isProcessed;
                    processedPath = ImagePreprocessor.PreprocessForDetection(path, "k_", out isProcessed);
                    mark("preprocess_ms");
                    if (IsTimedOut(sw)) return TimeoutResult(response, includePerfTimings, timings);

                    var dlib = new DlibBiometrics();

                    // ── Face detection ────────────────────────────────────────────────
                    DlibBiometrics.FaceBox faceBox;
                    Location               faceLoc;
                    string                 faceErr;
                    bool                   usedClientBox = false;

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
                    if (IsTimedOut(sw)) return TimeoutResult(response, includePerfTimings, timings);

                    // ── Liveness threshold (read once) ────────────────────────────────
                    var isMobileAttend = DeviceService.IsMobileDevice(request);

                    var liveTh = isMobileAttend
                        ? (float)ConfigurationService.GetDouble("Biometrics:MobileLivenessThreshold", 0.68)
                        : (float)ConfigurationService.GetDoubleCached(
                            "Biometrics:LivenessThreshold",
                            ConfigurationService.GetDouble("Biometrics:LivenessThreshold", 0.65));

                    // ── Embedding + liveness ──────────────────────────────────────────
                    double[] vec               = null;
                    float    p                 = 0f;
                    bool     livenessConfirmed = false;
                    int      actualImageWidth  = 1280;

                    FastScanPipeline.ScanResult fastResult = null;

                    if (!usedClientBox && ConfigurationService.GetBool("Kiosk:UseFastPipeline", true))
                    {
                        image.InputStream.Position = 0;
                        fastResult = FastScanPipeline.ScanInMemory(image, includePerfTimings);

                        if (fastResult.Timings != null)
                            foreach (var t in fastResult.Timings)
                                timings["fast_" + t.Key] = t.Value;

                        if (!fastResult.Ok)
                        {
                            var debug = ConfigurationService.GetBool("Biometrics:Debug", false);
                            return JsonResponseBuilder.EncodingFail(fastResult.Error, timings, includePerfTimings, debug);
                        }

                        if (!fastResult.LivenessOk)
                            return JsonResponseBuilder.LivenessFail(fastResult.LivenessScore, liveTh, timings, includePerfTimings);

                        vec               = fastResult.FaceEncoding;
                        p                 = fastResult.LivenessScore;
                        actualImageWidth  = fastResult.ImageWidth;
                        livenessConfirmed = true;
                        mark("fast_pipeline_ms");
                    }
                    else
                    {
                        // Legacy sequential path
                        var live   = new OnnxLiveness();
                        var scored = live.ScoreFromFile(processedPath, faceBox);
                        mark("liveness_ms");
                        if (IsTimedOut(sw)) return TimeoutResult(response, includePerfTimings, timings);

                        var skipLiveness = ConfigurationService.GetBool("Biometrics:SkipLiveness", false);

                        if (!scored.Ok && !skipLiveness)
                            return JsonResponseBuilder.ErrorWithTimings(scored.Error, timings, includePerfTimings);

                        p = scored.Probability ?? 0f;

                        if (p < liveTh && !skipLiveness)
                            return JsonResponseBuilder.LivenessFail(p, liveTh, timings, includePerfTimings);

                        livenessConfirmed = !skipLiveness;

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

                    if (IsTimedOut(sw)) return TimeoutResult(response, includePerfTimings, timings);

                    var attendanceTol = ConfigurationService.GetDouble("Biometrics:AttendanceTolerance", 0.50);
                    if (isMobileAttend)
                    {
                        var mobileTol = ConfigurationService.GetDouble("Biometrics:MobileAttendanceTolerance", 0.48);
                        attendanceTol = Math.Max(0.40, Math.Min(FastFaceMatcher.MedDistThresholdPublic, mobileTol));
                    }
                    else
                    {
                        attendanceTol = Math.Max(0.40, Math.Min(FastFaceMatcher.MedDistThresholdPublic, attendanceTol));
                    }

                    // ── Matching ──────────────────────────────────────────────────────
                    if (!FastFaceMatcher.IsInitialized)
                        FastFaceMatcher.Initialize();

                    var matchResult = FastFaceMatcher.FindBestMatch(vec, attendanceTol)
                                      ?? new FastFaceMatcher.MatchResult { IsMatch = false };

                    mark("match_ms");

                    // ── Unknown / visitor path ────────────────────────────────────────
                    if (!matchResult.IsMatch)
                    {
                        var fingerprint    = DeviceService.GenerateFingerprint(request);
                        var isMobile       = DeviceService.IsMobileDevice(request);
                        var kioskCookieUnk = request.Cookies["ForceKioskMode"];
                        var forceKioskUnk  = (kioskCookieUnk != null && kioskCookieUnk.Value == "true");

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
                                    bestVisitorDist = d;
                                    bestVisitorId   = e.VisitorId;
                                    bestVisitorName = e.Name;
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

                        var scanId = VisitorScanService.Store(
                            vec, office.Id,
                            isKnownVisitor ? bestVisitorId   : (int?)null,
                            isKnownVisitor ? bestVisitorName : null,
                            DeviceService.GetVisitorSessionBinding(httpContext));

                        return JsonResponseBuilder.VisitorScan(
                            scanId, isKnownVisitor, bestVisitorName, bestVisitorDist, vTol, p,
                            timings, includePerfTimings);
                    }

                    // ── Tier-based acceptance ─────────────────────────────────────────
                    var bestEmpId = matchResult.Employee?.EmployeeId;
                    var bestDist  = matchResult.Distance;

                    if (matchResult.Tier == FastFaceMatcher.MatchTier.Medium)
                    {
                        var deviceKeyMed = deviceToken
                            ?? DeviceService.GetDeviceTokenFromCookie(request)
                            ?? DeviceService.GenerateFingerprint(request);

                        var existing = PendingScanService.Get(deviceKeyMed);

                        if (existing == null)
                        {
                            PendingScanService.Store(deviceKeyMed, new PendingScanService.Entry
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
                            });

                            Trace.TraceInformation(
                                "[SCAN] MEDIUM_PENDING | emp={0} d={1:F3} gap={2:F3} — awaiting confirm",
                                bestEmpId, bestDist, matchResult.AmbiguityGap);

                            return new JsonResult
                            {
                                Data = new
                                {
                                    ok        = false,
                                    error     = "SCAN_CONFIRM_NEEDED",
                                    message   = "Almost there — please look at the camera one more time.",
                                    liveness  = p,
                                    threshold = liveTh
                                }
                            };
                        }

                        // Second frame — verify same employee
                        PendingScanService.Remove(deviceKeyMed);

                        if (!string.Equals(existing.EmployeeId, bestEmpId, StringComparison.OrdinalIgnoreCase))
                        {
                            Trace.TraceInformation(
                                "[SCAN] MEDIUM_MISMATCH | frame1={0} frame2={1} — both rejected",
                                existing.EmployeeId, bestEmpId);
                            return JsonResponseBuilder.NotRecognized(timings, includePerfTimings);
                        }

                        var avgDist   = (existing.Distance + bestDist) / 2.0;
                        var bestOfTwo = Math.Min(existing.Distance, bestDist);

                        Trace.TraceInformation(
                            "[SCAN] MEDIUM_CONFIRMED | emp={0} d1={1:F3} d2={2:F3} avg={3:F3} best={4:F3}",
                            bestEmpId, existing.Distance, bestDist, avgDist, bestOfTwo);

                        if (avgDist > FastFaceMatcher.MedDistThresholdPublic ||
                            bestOfTwo > FastFaceMatcher.HighDistThresholdPublic * 1.15)
                        {
                            Trace.TraceInformation(
                                "[SCAN] MEDIUM_REJECTED_WEAK | avg={0:F3} best={1:F3}", avgDist, bestOfTwo);
                            return JsonResponseBuilder.NotRecognized(timings, includePerfTimings);
                        }

                        bestDist = bestOfTwo;
                    }

                    if (IsTimedOut(sw)) return TimeoutResult(response, includePerfTimings, timings);

                    // ── Employee lookup ───────────────────────────────────────────────
                    var emp = db.Employees.FirstOrDefault(x => x.EmployeeId == bestEmpId && x.Status == "ACTIVE");
                    if (emp == null)
                        return JsonResponseBuilder.ErrorWithTimings("EMPLOYEE_NOT_FOUND", timings, includePerfTimings);

                    // ── Device check (mobile only) ────────────────────────────────────
                    var deviceFingerprint = DeviceService.GenerateFingerprint(request);
                    var deviceIsMobile    = DeviceService.IsMobileDevice(request);

                    var kioskCookie    = request.Cookies["ForceKioskMode"];
                    var chUaMobile     = request.Headers["Sec-CH-UA-Mobile"];
                    bool isHardwareMobile = (chUaMobile == "?1");
                    var forceKiosk     = !isHardwareMobile && (
                        (kioskCookie != null && kioskCookie.Value == "true") ||
                        (request.Headers["X-Kiosk-Mode"] == "true"));

                    var tokenFromCookie   = DeviceService.GetDeviceTokenFromCookie(request);
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
                            DeviceService.SetDeviceTokenCookie(response, deviceTokenFromCheck, request.IsSecureConnection);
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

                    if (attendanceTol > 0 && bestDist >= (attendanceTol * nearMatchRatio))
                    {
                        needsReviewFlag = true;
                        reviewNotes.Append("Near-match dist=")
                            .Append(bestDist.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))
                            .Append(" tol=").Append((attendanceTol * nearMatchRatio).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))
                            .Append(". ");
                    }

                    if (p < (liveTh + livenessMargin))
                    {
                        if (reviewNotes.Length > 0) reviewNotes.Append(" ");
                        needsReviewFlag = true;
                        reviewNotes.Append("Near liveness. Score=")
                            .Append(p.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))
                            .Append(" (th=").Append(liveTh.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))
                            .Append(")");
                    }

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
                    var deviceFp = DeviceService.GetShortDeviceFingerprint(httpContext);
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
                        GPSLatitude      = TruncateGpsCoordinate(lat),
                        GPSLongitude     = TruncateGpsCoordinate(lon),
                        GPSAccuracy      = accuracy,
                        LocationVerified = locationVerified,
                        FaceDistance     = bestDist,
                        FaceSimilarity   = similarity,
                        MatchThreshold   = attendanceTol,
                        LivenessScore    = p,
                        LivenessResult   = "PASS",
                        ClientIP         = StringHelper.Truncate(request.UserHostAddress ?? "", 100),
                        UserAgent        = StringHelper.Truncate(request.UserAgent ?? "", 1000),
                        WiFiBSSID        = StringHelper.Truncate(office.WiFiBSSID, 200),
                        NeedsReview      = needsReviewFlag,
                        Notes            = reviewNotes.ToString()
                    };

                    if (IsTimedOut(sw)) return TimeoutResult(response, includePerfTimings, timings);

                    var svc = new AttendanceService(db);
                    var rec = svc.Record(log, requestedAtUtc);
                    mark("db_ms");

                    if (!rec.Ok)
                    {
                        if (string.Equals(rec.Code, "TOO_SOON", StringComparison.OrdinalIgnoreCase))
                        {
                            var enforcedGap = rec.ApplicableGapSeconds > 0
                                ? rec.ApplicableGapSeconds
                                : ConfigurationService.GetInt("Attendance:MinGapSeconds", 180);
                            return JsonResponseBuilder.TooSoon(rec.Message, enforcedGap, timings, includePerfTimings);
                        }
                        return JsonResponseBuilder.ErrorWithTimings(rec.Code, timings, includePerfTimings, rec.Message);
                    }

                    // Distance log — grep "[MATCH]" to build threshold tuning dataset.
                    Trace.TraceInformation(
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
                Trace.TraceError("[Kiosk.Attend] ScanAttendanceCore failed: " + ex);
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
    }
}
