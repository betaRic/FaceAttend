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

namespace FaceAttend.Services.Recognition
{
    /// <summary>
    /// Encapsulates the full attendance scan pipeline:
    /// image validation → office resolution → face detection → anti-spoof →
    /// matching -> tier voting -> attendance recording.
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

        // ── Main pipeline ─────────────────────────────────────────────────────

        public ActionResult Scan(
            double? lat, double? lon, double? accuracy,
            HttpPostedFileBase image,
            OpenVinoBiometrics.FaceBox clientFaceBox,
            DateTime requestedAtLocal,
            bool includePerfTimings,
            HttpContextBase httpContext,
            bool wfhMode = false)
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
                        if (!wfhMode)
                            return JsonResponseBuilder.ErrorWithTimings("GPS_REQUIRED", timings, includePerfTimings);
                        // wfhMode=true: defer office resolution until after face match
                    }
                    else
                    {
                        office           = GetFallbackOffice(db);
                        locationVerified = false;
                    }

                    if (office == null && !wfhMode)
                        return NoOfficesResult(includePerfTimings, timings);

                    var isMobileAttend = DeviceService.IsMobileDevice(request);
                    var biometricPolicy = BiometricPolicy.Current;
                    var antiSpoofThreshold = biometricPolicy.AntiSpoofClearThresholdFor(isMobileAttend);

                    double[] vec               = null;
                    float    p                 = 0f;
                    bool     antiSpoofConfirmed = false;
                    AntiSpoofPolicyResult antiSpoofResult = null;
                    int      actualImageWidth  = 1280;
                    int      actualImageHeight = 0;
                    float    sharpness         = 0f;
                    float    sharpnessThreshold = FaceQualityAnalyzer.GetSharpnessThreshold(isMobileAttend);

                    var scanFaceBox = isMobileAttend ? null : clientFaceBox;
                    var fastResult = FastScanPipeline.ScanInMemory(
                        image,
                        scanFaceBox,
                        includePerfTimings,
                        antiSpoofThreshold: antiSpoofThreshold,
                        isMobile: isMobileAttend);

                    if (fastResult.Timings != null)
                        foreach (var t in fastResult.Timings)
                            timings["openvino_" + t.Key] = t.Value;

                    if (!fastResult.Ok)
                    {
                        var debug = ConfigurationService.GetBool("Biometrics:Debug", false);
                        return JsonResponseBuilder.EncodingFail(fastResult.Error, timings, includePerfTimings, debug);
                    }

                    antiSpoofResult = biometricPolicy.EvaluateAntiSpoof(
                        fastResult.AntiSpoofModelOk,
                        fastResult.AntiSpoofScore,
                        isMobileAttend);

                    if (antiSpoofResult.Decision == AntiSpoofDecision.Block)
                        return JsonResponseBuilder.AntiSpoofFail(fastResult.AntiSpoofScore, antiSpoofThreshold,
                            antiSpoofResult.Decision.ToString().ToUpperInvariant(), timings, includePerfTimings);

                    if (antiSpoofResult.Decision == AntiSpoofDecision.Retry)
                        return JsonResponseBuilder.AntiSpoofRetry(fastResult.AntiSpoofScore, antiSpoofThreshold, timings, includePerfTimings);

                    vec                = fastResult.FaceEncoding;
                    p                  = fastResult.AntiSpoofScore;
                    actualImageWidth   = fastResult.ImageWidth;
                    actualImageHeight  = fastResult.ImageHeight;
                    sharpness          = fastResult.Sharpness;
                    sharpnessThreshold = fastResult.SharpnessThreshold;
                    antiSpoofConfirmed  = antiSpoofResult.Decision == AntiSpoofDecision.Pass;
                    mark("openvino_pipeline_ms");

                    if (vec == null)
                        return JsonResponseBuilder.ErrorWithTimings("ENCODING_FAIL", timings, includePerfTimings);

                    if (IsTimedOut(sw)) return TimeoutResult(response, includePerfTimings, timings);

                    var attendanceTol = biometricPolicy.AttendanceToleranceFor(isMobileAttend);

                    // ── Matching ──────────────────────────────────────────────────────
                    if (!FastFaceMatcher.IsInitialized)
                        FastFaceMatcher.Initialize();

                    var matchResult = FastFaceMatcher.FindBestMatch(vec, attendanceTol)
                                      ?? new FastFaceMatcher.MatchResult { IsMatch = false };

                    mark("match_ms");

                    // ── Unknown / visitor path ────────────────────────────────────────
                    if (!matchResult.IsMatch)
                    {
                        if (matchResult.WasAmbiguous)
                            return JsonResponseBuilder.NotRecognized(timings, includePerfTimings);

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

                        double vTol = ConfigurationService.GetDouble("Visitors:RecognitionTolerance", attendanceTol);

                        int?   bestVisitorId   = null;
                        string bestVisitorName = null;
                        double bestVisitorDist = double.PositiveInfinity;

                        try
                        {
                            var entries = VisitorFaceIndex.GetEntries(db);
                            foreach (var e in entries)
                            {
                                var d = FaceVectorCodec.Distance(vec, e.Vec);
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
                            ConfigurationService.GetBool("Kiosk:VisitorEnabled", false));

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

                    var bestEmpId = matchResult.Employee?.EmployeeId;
                    var bestDist  = matchResult.Distance;

                    if (matchResult.Tier == FastFaceMatcher.MatchTier.Medium)
                    {
                        var deviceKeyMed = DeviceService.GenerateFingerprint(request);

                        var existing = PendingScanService.Get(deviceKeyMed);

                        if (existing == null)
                        {
                            PendingScanService.Store(deviceKeyMed, new PendingScanService.Entry
                            {
                                EmployeeId   = bestEmpId,
                                EmployeeDbId = matchResult.Employee?.Id ?? 0,
                                Distance     = bestDist,
                                AntiSpoof     = p,
                                AmbiguityGap = matchResult.AmbiguityGap,
                                OfficeId     = office?.Id ?? 0,
                                OfficeName   = office?.Name ?? (wfhMode ? "WFH" : null),
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
                                    antiSpoofScore = p,
                                    threshold = antiSpoofThreshold
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

                    // ── WFH office resolution (deferred from pre-match) ───────────────
                    if (wfhMode && office == null)
                    {
                        var todayLocal = requestedAtLocal.Date;
                        var empOffice  = db.Offices.FirstOrDefault(o => o.Id == emp.OfficeId && o.IsActive);
                        if (empOffice == null || !OfficeScheduleService.IsWfhEnabledToday(empOffice, todayLocal))
                            return JsonResponseBuilder.ErrorWithTimings("GPS_REQUIRED", timings, includePerfTimings);
                        office           = empOffice;
                        locationVerified = false;  // WFH scan — location not verified
                    }

                    var displayName = emp.LastName + ", " + emp.FirstName +
                                      (string.IsNullOrWhiteSpace(emp.MiddleName) ? "" : " " + emp.MiddleName);

                    double? similarity = attendanceTol > 0
                        ? Math.Max(0.0, Math.Min(1.0, 1.0 - (bestDist / attendanceTol)))
                        : (double?)null;

                    var nearMatchRatio = ConfigurationService.GetDoubleCached("NeedsReview:NearMatchRatio", 0.90);
                    var antiSpoofMargin = ConfigurationService.GetDoubleCached(
                        "NeedsReview:AntiSpoofMargin",
                        0.03);
                    var gpsMargin      = ConfigurationService.GetIntCached("NeedsReview:GPSAccuracyMargin", 10);

                    bool needsReviewFlag = false;
                    var  reviewNotes     = new System.Text.StringBuilder();
                    var reviewCodes = new List<string>();

                    if (antiSpoofResult != null && antiSpoofResult.NeedsReview)
                    {
                        needsReviewFlag = true;
                        reviewCodes.Add(antiSpoofResult.Decision == AntiSpoofDecision.ModelError
                            ? "ANTI_SPOOF_MODEL_ERROR"
                            : "ANTI_SPOOF_REVIEW");
                        reviewNotes.Append("Anti-spoof ")
                            .Append(antiSpoofResult.Decision.ToString().ToUpperInvariant())
                            .Append(" score=")
                            .Append(p.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))
                            .Append(" clear=")
                            .Append(antiSpoofThreshold.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))
                            .Append(". ");
                    }

                    if (attendanceTol > 0 && bestDist >= (attendanceTol * nearMatchRatio))
                    {
                        needsReviewFlag = true;
                        reviewCodes.Add("NEAR_MATCH");
                        reviewNotes.Append("Near-match dist=")
                            .Append(bestDist.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))
                            .Append(" tol=").Append((attendanceTol * nearMatchRatio).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))
                            .Append(". ");
                    }

                    if (p < (antiSpoofThreshold + antiSpoofMargin))
                    {
                        if (reviewNotes.Length > 0) reviewNotes.Append(" ");
                        needsReviewFlag = true;
                        reviewCodes.Add("NEAR_ANTI_SPOOF");
                        reviewNotes.Append("Near anti-spoof. Score=")
                            .Append(p.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))
                            .Append(" (th=").Append(antiSpoofThreshold.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))
                            .Append(")");
                    }

                    if (gpsRequired && accuracy.HasValue && requiredAcc > 0)
                    {
                        var nearAcc = Math.Max(0, requiredAcc - gpsMargin);
                        if (accuracy.Value >= nearAcc)
                        {
                            if (reviewNotes.Length > 0) reviewNotes.Append(" ");
                            needsReviewFlag = true;
                            reviewCodes.Add("GPS_ACCURACY");
                            reviewNotes.Append("Near GPS accuracy. Acc=")
                                .Append(accuracy.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))
                                .Append("m (req=").Append(requiredAcc).Append("m)");
                        }
                    }

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
                            reviewCodes.Add("GPS_REPEAT");
                            if (reviewNotes.Length > 0) reviewNotes.Append(" ");
                            reviewNotes.Append("GPS repeat: ").Append(spoofCheck.Reason).Append(".");
                        }
                    }

                    var tierNote = string.Format("Tier={0} d={1:F3} gap={2}",
                        matchResult.Tier,
                        bestDist,
                        matchResult.AmbiguityGap == double.PositiveInfinity
                            ? "inf"
                            : matchResult.AmbiguityGap.ToString("F3"));
                    if (reviewNotes.Length > 0) reviewNotes.Insert(0, tierNote + ". ");
                    else reviewNotes.Append(tierNote);

                    bool isWfhScan = wfhMode && !locationVerified;
                    if (isWfhScan && reviewNotes.Length > 0)
                        reviewNotes.Append(" WFH");
                    else if (isWfhScan)
                        reviewNotes.Append("WFH");

                    var log = new AttendanceLog
                    {
                        EmployeeId       = emp.Id,
                        OfficeId         = office.Id,
                        EmployeeFullName = StringHelper.Truncate(displayName, 200),
                        Source           = isMobileAttend ? "MOBILE" : "KIOSK",
                        Department       = StringHelper.Truncate(emp.Department, 200),
                        OfficeType       = StringHelper.Truncate(office.Type, 40),
                        OfficeName       = StringHelper.Truncate(office.Name, 200),
                        GPSLatitude      = TruncateGpsCoordinate(lat),
                        GPSLongitude     = TruncateGpsCoordinate(lon),
                        GPSAccuracy      = accuracy,
                        LocationVerified = locationVerified,
                        FaceDistance     = bestDist,
                        FaceSimilarity   = similarity,
                        MatchThreshold   = attendanceTol,
                        AntiSpoofScore    = p,
                        AntiSpoofResult   = antiSpoofResult == null
                            ? (antiSpoofConfirmed ? "PASS" : "UNKNOWN")
                            : antiSpoofResult.Decision.ToString().ToUpperInvariant(),
                        ClientIP         = StringHelper.Truncate(request.UserHostAddress ?? "", 100),
                        UserAgent        = StringHelper.Truncate(request.UserAgent ?? "", 1000),
                        WiFiBSSID        = StringHelper.Truncate(office.WiFiBSSID, 200),
                        NeedsReview      = needsReviewFlag,
                        ReviewStatus     = needsReviewFlag ? "PENDING" : "NONE",
                        ReviewReasonCodes = reviewCodes.Count == 0 ? null : string.Join(",", reviewCodes.Distinct()),
                        Notes            = reviewNotes.ToString(),
                        IsWfh            = isWfhScan
                    };

                    if (IsTimedOut(sw)) return TimeoutResult(response, includePerfTimings, timings);

                    var svc = new AttendanceService(db);
                    var rec = svc.Record(log, requestedAtLocal);
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
                        "[MATCH] emp={0} event={1} tier={2} dist={3:F3} gap={4} antiSpoof={5:F3} tol={6:F3} ms={7}",
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

                    var attendanceAccess = AttendanceAccessReceiptService.Issue(
                        response,
                        request,
                        emp,
                        rec.AttendanceLogId,
                        rec.EventType,
                        biometricPolicy.ModelVersion,
                        request.IsSecureConnection);

                    var recognitionDecision = RecognitionDecisionFactory.FromAttendance(
                        "ATTENDANCE_ACCEPTED",
                        true,
                        isMobileAttend ? "MOBILE" : "KIOSK",
                        p,
                        antiSpoofThreshold,
                        antiSpoofResult,
                        sharpness,
                        sharpnessThreshold,
                        fastResult?.FaceBox,
                        actualImageWidth,
                        actualImageHeight,
                        matchResult,
                        attendanceTol,
                        sw.ElapsedMilliseconds);

                    return JsonResponseBuilder.AttendanceSuccess(
                        emp.EmployeeId, displayName, displayName, rec.EventType, rec.Message,
                        office.Id, office.Name, p, bestDist, requestedAtLocal,
                        timings, includePerfTimings, attendanceAccess, recognitionDecision);
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
            }
        }
    }
}
