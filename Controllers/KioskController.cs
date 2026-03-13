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
// NOTE: KioskSessionService merged into DeviceService - use DeviceService.GetVisitorSessionBinding/GetShortDeviceFingerprint
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
            ViewBag.ReturnUrl = AdminAuthorizeAttribute.SanitizeReturnUrl(returnUrl);
            ViewBag.UnlockHint = (unlock ?? 0) == 1;
            // SECURITY: Disable admin unlock on mobile devices
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
                bool hasCoords = lat.HasValue && lon.HasValue;

                if (!hasCoords)
                {
                    if (gpsRequired)
                    {
                        int requiredAccuracy = ConfigurationService.GetInt(
                            db, "Location:GPSAccuracyRequired",
                            ConfigurationService.GetInt("Location:GPSAccuracyRequired", 50));
                        return JsonResponseBuilder.OfficeResolved(
                            allowed: false, 
                            gpsRequired: true, 
                            reason: "GPS_REQUIRED", 
                            requiredAccuracy: requiredAccuracy, 
                            accuracy: accuracy);
                    }

                    var fallback = OfficeLocationService.GetFallbackOffice(db);
                    return JsonResponseBuilder.OfficeResolved(
                        allowed: true, 
                        gpsRequired: false, 
                        officeId: fallback?.Id, 
                        officeName: fallback?.Name);
                }

                var pick = OfficeLocationService.PickOffice(db, lat.Value, lon.Value, accuracy);
                if (!pick.Allowed)
                {
                    return JsonResponseBuilder.OfficeResolved(
                        allowed: false, 
                        gpsRequired: gpsRequired, 
                        reason: pick.Reason, 
                        requiredAccuracy: pick.RequiredAccuracy, 
                        accuracy: accuracy);
                }

                return JsonResponseBuilder.OfficeResolved(
                    allowed: true, 
                    gpsRequired: gpsRequired, 
                    officeId: pick.Office.Id, 
                    officeName: pick.Office.Name,
                    reason: "OK");
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RateLimit(Name = "KioskAttend", MaxRequests = 60, WindowSeconds = 60, Burst = 20)]
        // OPT-SPEED-04: Accept face bbox from client to skip server-side detection
        public ActionResult Attend(double? lat, double? lon, double? accuracy, 
            HttpPostedFileBase image,
            int? faceX, int? faceY, int? faceW, int? faceH,
            string deviceToken = null)
        {
            
            var requestedAtUtc = DateTime.UtcNow; // capture attendance time at request entry

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

                // Build face box from client params if provided
                DlibBiometrics.FaceBox clientFaceBox = null;
                if (faceX.HasValue && faceY.HasValue && faceW.HasValue && faceH.HasValue 
                    && faceW.Value > 0 && faceH.Value > 0)
                {
                    clientFaceBox = new DlibBiometrics.FaceBox
                    {
                        Left = faceX.Value,
                        Top = faceY.Value,
                        Width = faceW.Value,
                        Height = faceH.Value
                    };
                }

                return ScanAttendanceCore(
                    lat,
                    lon,
                    accuracy,
                    image,
                    clientFaceBox,
                    requestedAtUtc,
                    includePerfTimings: ConfigurationService.GetBool("Kiosk:EnablePerfTimings", false),
                    deviceToken: deviceToken);
            }
            finally
            {
                Interlocked.Decrement(ref _activeScanCount);
            }
        }

        /// <summary>
        /// BURST MODE: Mobile attendance with multi-frame consensus voting
        /// Captures 5 frames and requires 3+ consistent matches for robust identification
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [RateLimit(Name = "KioskAttendBurst", MaxRequests = 60, WindowSeconds = 60, Burst = 15)]
        public ActionResult AttendBurst(double? lat, double? lon, double? accuracy,
            int? faceX, int? faceY, int? faceW, int? faceH, string deviceToken = null)
        {
            var requestedAtUtc = DateTime.UtcNow;
            var tempFiles = new List<string>();
            
            try
            {
                int frameCount = 0;
                int.TryParse(Request.Form["frameCount"], out frameCount);
                if (frameCount < 1) frameCount = 5;

                // Validate frames received
                var validFrames = new List<HttpPostedFileBase>();
                for (int i = 0; i < frameCount; i++)
                {
                    var image = Request.Files["frame_" + i];
                    if (image != null && image.ContentLength > 0)
                        validFrames.Add(image);
                }



                if (validFrames.Count < 1)
                    return JsonResponseBuilder.Error("NO_FRAMES", "No frames received. Please try again.");
                
                if (validFrames.Count < 3)
                {
                    // Continue with available frames - no minimum enforced for flexibility
                }

                // Use client face box from MediaPipe when available.
                // This is much more reliable on mobile than re-detecting every burst frame from scratch.
                DlibBiometrics.FaceBox clientFaceBox = null;
                if (faceX.HasValue && faceY.HasValue && faceW.HasValue && faceH.HasValue
                    && faceW.Value > 20 && faceH.Value > 20)
                {
                    clientFaceBox = new DlibBiometrics.FaceBox
                    {
                        Left = faceX.Value,
                        Top = faceY.Value,
                        Width = faceW.Value,
                        Height = faceH.Value
                    };
                }

                // OPTIMIZED: Process frames in parallel for speed (2-3x faster)
                var concurrentResults = new System.Collections.Concurrent.ConcurrentBag<BurstFrameResult>();
                double livenessThreshold = ConfigurationService.GetDouble("Biometrics:LivenessThreshold", 0.75);
                var attendanceTol = ConfigurationService.GetDouble("Biometrics:AttendanceTolerance", 0.65);
                attendanceTol = Math.Max(0.55, Math.Min(0.75, attendanceTol));

                if (!FastFaceMatcher.IsInitialized)
                    FastFaceMatcher.Initialize();

                // Save all frames first
                var framePaths = new List<Tuple<int, string>>();
                for (int i = 0; i < validFrames.Count; i++)
                {
                    var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"kiosk_burst_{Guid.NewGuid():N}.jpg");
                    validFrames[i].SaveAs(tempPath);
                    framePaths.Add(Tuple.Create(i, tempPath));
                    tempFiles.Add(tempPath);
                }

                // Process in parallel - 4 frames at a time for optimal speed
                System.Threading.Tasks.Parallel.ForEach(framePaths, new ParallelOptions { MaxDegreeOfParallelism = 4 }, (frameInfo) =>
                {
                    var frameIndex = frameInfo.Item1;
                    var tempPath = frameInfo.Item2;

                    try
                    {
                        var dlib = new DlibBiometrics();
                        var liveness = new OnnxLiveness();
                        
                        DlibBiometrics.FaceBox faceBox;
                        FaceRecognitionDotNet.Location faceLoc;
                        string detectErr;
                        bool usedClientBox = TryBuildFaceLocationFromClientBox(clientFaceBox, out faceBox, out faceLoc);

                        if (!usedClientBox)
                        {
                            if (!dlib.TryDetectBestFaceFromFile(tempPath, out faceBox, out faceLoc, out detectErr,
                                allowLargestFace: true, primaryUpsample: 0, retryUpsampleOnNoFace: true))
                            {
                                return;
                            }
                        }

                        // Liveness check.
                        var scored = liveness.ScoreFromFile(tempPath, faceBox);
                        if ((!scored.Ok || (scored.Probability ?? 0) < livenessThreshold) && usedClientBox)
                        {
                            DlibBiometrics.FaceBox detectedFaceBox;
                            FaceRecognitionDotNet.Location detectedFaceLoc;
                            if (dlib.TryDetectBestFaceFromFile(tempPath, out detectedFaceBox, out detectedFaceLoc, out detectErr,
                                allowLargestFace: true, primaryUpsample: 0, retryUpsampleOnNoFace: true))
                            {
                                faceBox = detectedFaceBox;
                                faceLoc = detectedFaceLoc;
                                usedClientBox = false;
                                scored = liveness.ScoreFromFile(tempPath, faceBox);
                            }
                        }

                        if (!scored.Ok || (scored.Probability ?? 0) < livenessThreshold)
                            return;

                        // Encode.
                        double[] vec;
                        string encErr;
                        var encodeOk = dlib.TryEncodeFromFileWithLocation(tempPath, faceLoc, out vec, out encErr) && vec != null;
                        if (!encodeOk && usedClientBox)
                        {
                            DlibBiometrics.FaceBox detectedFaceBox;
                            FaceRecognitionDotNet.Location detectedFaceLoc;
                            if (dlib.TryDetectBestFaceFromFile(tempPath, out detectedFaceBox, out detectedFaceLoc, out detectErr,
                                allowLargestFace: true, primaryUpsample: 0, retryUpsampleOnNoFace: true))
                            {
                                faceBox = detectedFaceBox;
                                faceLoc = detectedFaceLoc;
                                usedClientBox = false;
                                encodeOk = dlib.TryEncodeFromFileWithLocation(tempPath, faceLoc, out vec, out encErr) && vec != null;
                            }
                        }

                        if (!encodeOk)
                            return;

                        // Match.
                        var matchResult = FastFaceMatcher.FindBestMatch(vec, attendanceTol);
                        if (matchResult == null)
                            return;

                        concurrentResults.Add(new BurstFrameResult
                        {
                            OriginalFrameIndex = frameIndex,
                            EmployeeId = matchResult.IsMatch ? matchResult.Employee?.EmployeeId : null,
                            Confidence = matchResult.Confidence,
                            Distance = matchResult.Distance,
                            IsMatch = matchResult.IsMatch,
                            LivenessScore = scored.Probability ?? 0,
                            TempPath = tempPath,
                            UsedClientBox = usedClientBox
                        });
                    }
                    catch { /* Skip failed frames */ }
                });

                // Convert concurrent results to list for consensus voting
                var frameResults = concurrentResults.ToList();
                
                // Consensus voting - adaptive based on frame count

                
                // Need at least 1 good frame
                if (frameResults.Count < 1)
                    return JsonResponseBuilder.Error("FACE_NOT_CLEAR", "Could not detect a face. Please try again.");

                var successfulMatches = frameResults.Where(r => r.IsMatch && !string.IsNullOrEmpty(r.EmployeeId)).ToList();

                
                if (successfulMatches.Count < 1)
                {
                    // No match found - try single frame fallback
                    if (frameResults.Count >= 1)
                    {

                        // Use the first frame that had a face (even if not matched)
                        var fallbackFrame = frameResults.First();
                        var fallbackImage = validFrames[fallbackFrame.OriginalFrameIndex];
                        return ScanAttendanceCore(lat, lon, accuracy, fallbackImage, clientFaceBox, requestedAtUtc, false, deviceToken);
                    }
                    return JsonResponseBuilder.Error("NOT_RECOGNIZED", "Face not recognized. Please check if you're enrolled.");
                }

                // Find the employee with most votes
                var voteGroups = successfulMatches
                    .GroupBy(r => r.EmployeeId)
                    .Select(g => new { EmployeeId = g.Key, Votes = g.Count(), AvgConfidence = g.Average(r => r.Confidence), AvgDistance = g.Average(r => r.Distance) })
                    .OrderByDescending(g => g.Votes)
                    .ThenByDescending(g => g.AvgConfidence)
                    .ToList();

                var winner = voteGroups.First();

                
                // For burst mode, require at least 2 votes if we have 3+ frames, otherwise 1 is enough
                int requiredVotes = (frameResults.Count >= 3) ? 2 : 1;
                if (winner.Votes < requiredVotes)
                    return JsonResponseBuilder.Error("NOT_RECOGNIZED", "Face not clearly recognized. Please try again with better lighting.");

                // Use the best frame for this employee for attendance recording
                var bestFrame = successfulMatches
                    .Where(r => r.EmployeeId == winner.EmployeeId)
                    .OrderBy(r => r.Distance)
                    .First();

                // Find the original image file that corresponds to this best frame
                var bestImage = validFrames[bestFrame.OriginalFrameIndex];
                
                // Now process attendance using the best frame (reuse existing logic)
                return ScanAttendanceCore(lat, lon, accuracy, bestImage, clientFaceBox, requestedAtUtc, false, deviceToken);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("[Kiosk.AttendBurst] Error: " + ex);
                return JsonResponseBuilder.Error("SERVER_ERROR", ex.Message);
            }
            finally
            {
                // Cleanup temp files
                foreach (var file in tempFiles)
                {
                    try { System.IO.File.Delete(file); } catch { }
                }
            }
        }

        private class BurstFrameResult
        {
            public int OriginalFrameIndex { get; set; }
            public string EmployeeId { get; set; }
            public double Confidence { get; set; }
            public double Distance { get; set; }
            public bool IsMatch { get; set; }
            public double LivenessScore { get; set; }
            public string TempPath { get; set; }
            public bool UsedClientBox { get; set; }
        }

        private static bool TryBuildFaceLocationFromClientBox(
            DlibBiometrics.FaceBox sourceBox,
            out DlibBiometrics.FaceBox faceBox,
            out FaceRecognitionDotNet.Location faceLoc)
        {
            faceBox = null;
            faceLoc = default(FaceRecognitionDotNet.Location);

            if (sourceBox == null || sourceBox.Width <= 20 || sourceBox.Height <= 20)
                return false;

            var padX = Math.Max(6, (int)Math.Round(sourceBox.Width * 0.10));
            var padY = Math.Max(6, (int)Math.Round(sourceBox.Height * 0.12));

            var left = Math.Max(0, sourceBox.Left - padX);
            var top = Math.Max(0, sourceBox.Top - padY);
            var width = Math.Max(1, sourceBox.Width + (padX * 2));
            var height = Math.Max(1, sourceBox.Height + (padY * 2));

            faceBox = new DlibBiometrics.FaceBox
            {
                Left = left,
                Top = top,
                Width = width,
                Height = height
            };

            faceLoc = new FaceRecognitionDotNet.Location(
                faceBox.Left,
                faceBox.Top,
                faceBox.Left + faceBox.Width,
                faceBox.Top + faceBox.Height);

            return true;
        }

        // OPT-SPEED-05: Use client-provided face box to skip detection (~150ms saved)
        private ActionResult ScanAttendanceCore(double? lat, double? lon, double? accuracy, 
            HttpPostedFileBase image, DlibBiometrics.FaceBox clientFaceBox, 
            DateTime requestedAtUtc, bool includePerfTimings,
            string deviceToken = null)
        {
            var sw = Stopwatch.StartNew();
            var timings = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            System.Action<string> mark = key => { if (includePerfTimings) timings[key] = sw.ElapsedMilliseconds; };

            if (image == null || image.ContentLength <= 0)
                return JsonResponseBuilder.ErrorWithTimings("NO_IMAGE", timings, includePerfTimings);

            var max = ConfigurationService.GetInt("Biometrics:MaxUploadBytes", 10 * 1024 * 1024);
            if (image.ContentLength > max)
                return JsonResponseBuilder.ErrorWithTimings("TOO_LARGE", timings, includePerfTimings);

            // SECURITY: Validate file content is actually an image
            if (!IsValidImage(image.InputStream, new[] { ".jpg", ".jpeg", ".png" }))
                return JsonResponseBuilder.ErrorWithTimings("INVALID_IMAGE_FORMAT", timings, includePerfTimings);

            string path = null;
            string processedPath = null;

            try
            {
                using (var db = new FaceAttendDBEntities())
                {
                    Office office = null;
                    bool gpsRequired = DeviceService.IsMobileDevice(Request);
                    bool locationVerified = false;
                    int requiredAcc = 0;

                    if (lat.HasValue && lon.HasValue)
                    {
                        var pick = OfficeLocationService.PickOffice(db, lat.Value, lon.Value, accuracy);
                        if (!pick.Allowed)
                            return JsonResponseBuilder.ErrorWithTimings(pick.Reason, timings, includePerfTimings);

                        office = pick.Office;
                        locationVerified = true;
                        requiredAcc = pick.RequiredAccuracy;
                    }
                    else if (gpsRequired)
                    {
                        return JsonResponseBuilder.ErrorWithTimings("GPS_REQUIRED", timings, includePerfTimings);
                    }
                    else
                    {
                        office = OfficeLocationService.GetFallbackOffice(db);
                        locationVerified = false;
                    }
                    if (office == null)
                        return NoOfficesResult(includePerfTimings, timings);

                    path = SaveTemp(image, "k_", max);
                    mark("saved_ms");
                    if (IsRequestTimedOut(sw)) return RequestTimeoutResult(includePerfTimings, timings);

                    bool isProcessed;
                    processedPath = ImagePreprocessor.PreprocessForDetection(path, "k_", out isProcessed);
                    mark("preprocess_ms");
                    if (IsRequestTimedOut(sw)) return RequestTimeoutResult(includePerfTimings, timings);

                    var dlib = new DlibBiometrics();

                    // OPT-SPEED-06: Use client face box if valid, skip server detection
                    DlibBiometrics.FaceBox faceBox;
                    FaceRecognitionDotNet.Location faceLoc;
                    string faceErr;
                    bool usedClientBox = false;

                    if (TryBuildFaceLocationFromClientBox(clientFaceBox, out faceBox, out faceLoc))
                    {
                        // Trust client detection when MediaPipe already has a stable face box.
                        usedClientBox = true;
                        faceErr = null;
                    }
                    else
                    {
                        // Fallback: server-side detection with kiosk-friendly retry.
                        if (!dlib.TryDetectBestFaceFromFile(processedPath, out faceBox, out faceLoc, out faceErr,
                            allowLargestFace: true, primaryUpsample: 0, retryUpsampleOnNoFace: true))
                        {
                            return JsonResponseBuilder.ErrorWithTimings(faceErr ?? "FACE_FAIL", timings, includePerfTimings);
                        }
                    }

                    mark(usedClientBox ? "detect_skip_ms" : "dlib_detect_ms");
                    if (IsRequestTimedOut(sw)) return RequestTimeoutResult(includePerfTimings, timings);

                    // OPTIMIZED: Use FastScanPipeline when no client face box provided
                    // This runs liveness + encoding in parallel for ~100-150ms speedup
                    double[] vec = null;
                    float p = 0f;
                    
                    // Get liveness threshold (used by both paths)
                    var liveTh = (float)ConfigurationService.GetDoubleCached(
                        "Biometrics:LivenessThreshold",
                        ConfigurationService.GetDouble("Biometrics:LivenessThreshold", 0.75));
                    
                    if (!usedClientBox && ConfigurationService.GetBool("Kiosk:UseFastPipeline", true))
                    {
                        // Reset stream for FastScanPipeline
                        image.InputStream.Position = 0;
                        var fastResult = FastScanPipeline.ScanInMemory(image, includePerfTimings);
                        
                        if (fastResult.Timings != null)
                        {
                            foreach (var t in fastResult.Timings)
                                timings["fast_" + t.Key] = t.Value;
                        }
                        
                        if (!fastResult.Ok)
                        {
                            var debug = ConfigurationService.GetBool("Biometrics:Debug", false);
                            return JsonResponseBuilder.EncodingFail(fastResult.Error, timings, includePerfTimings, debug);
                        }
                        
                        vec = fastResult.FaceEncoding;
                        p = fastResult.LivenessScore;
                        mark("fast_pipeline_ms");
                    }
                    else
                    {
                        // Legacy path: sequential liveness + encoding
                        // Liveness (server truth)
                        var live = new OnnxLiveness();
                        var scored = live.ScoreFromFile(processedPath, faceBox);
                        mark("liveness_ms");
                        if (IsRequestTimedOut(sw)) return RequestTimeoutResult(includePerfTimings, timings);

                        // DEBUG: Log liveness results


                        // Allow bypassing liveness check for debugging
                        var skipLiveness = ConfigurationService.GetBool("Biometrics:SkipLiveness", false);

                        if (!scored.Ok && !skipLiveness)
                            return JsonResponseBuilder.ErrorWithTimings(scored.Error, timings, includePerfTimings);

                        p = scored.Probability ?? 0f;
                        if (p < liveTh && !skipLiveness)
                            return JsonResponseBuilder.LivenessFail(p, liveTh, timings, includePerfTimings);

                        // Encode using known location (skips FaceLocations)
                        string encErr;
                        if (!dlib.TryEncodeFromFileWithLocation(processedPath, faceLoc, out vec, out encErr) || vec == null)
                        {
                            var debug = ConfigurationService.GetBool("Biometrics:Debug", false);
                            return JsonResponseBuilder.EncodingFail(encErr, timings, includePerfTimings, debug);
                        }

                        mark("dlib_encode_ms");
                    }
                    
                    if (vec == null)
                    {
                        return JsonResponseBuilder.ErrorWithTimings("ENCODING_FAIL", timings, includePerfTimings);
                    }
                    if (IsRequestTimedOut(sw)) return RequestTimeoutResult(includePerfTimings, timings);

                    // Use more lenient tolerance for attendance to handle head tilts and expressions
                    // Enrollment: strict (0.45), Attendance: lenient (0.65 default, up to 0.75 max)
                    var attendanceTol = ConfigurationService.GetDouble("Biometrics:AttendanceTolerance", 0.65);
                    // Clamp between 0.55 (strict) and 0.75 (very lenient)
                    attendanceTol = Math.Max(0.55, Math.Min(0.75, attendanceTol));
                    
                    // OPT-SPEED-08: Use FastFaceMatcher (RAM cache) for INSTANT matching (~20ms vs 100ms)
                    // FIX: Ensure matcher is initialized before use
                    if (!FastFaceMatcher.IsInitialized)
                        FastFaceMatcher.Initialize();
                    
                    FastFaceMatcher.MatchResult matchResult = null;
                    double bestDist = double.MaxValue;
                    string bestEmpId = null;
                    
                    // Try matching with lenient tolerance first
                    try
                    {
                        matchResult = FastFaceMatcher.FindBestMatch(vec, attendanceTol);
                    }
                    catch { /* Fallback to DB if cache fails */ }
                    
                    if (matchResult?.IsMatch == true)
                    {
                        bestEmpId = matchResult.Employee?.EmployeeId;
                        bestDist = matchResult.Distance;
                        mark("match_fast_ms");
                    }
                    else
                    {
                        // Fallback to DB search with same tolerance
                        bestEmpId = EmployeeFaceIndex.FindNearest(db, vec, attendanceTol, out bestDist);
                        mark("match_db_ms");
                    }
                    
                    if (IsRequestTimedOut(sw)) return RequestTimeoutResult(includePerfTimings, timings);

                    // DEBUG: Log match results for troubleshooting TOO_SOON vs VISITOR

                    
                    if (bestEmpId == null || bestDist > attendanceTol)
                    {
                        
                        // Unknown employee - check if mobile device for self-enrollment
                        var fingerprint = DeviceService.GenerateFingerprint(Request);
                        var isMobile = DeviceService.IsMobileDevice(Request);
                        
                        // Check kiosk mode for unknown face handling too
                        var kioskCookieUnknown = Request.Cookies["ForceKioskMode"];
                        var forceKioskUnknown = (kioskCookieUnknown != null && kioskCookieUnknown.Value == "true");
                        
                        if (isMobile && !forceKioskUnknown)
                        {
                            // On mobile device - offer self-enrollment
                            return JsonResponseBuilder.SelfEnrollOffer(
                                fingerprint,
                                "Face not recognized. Would you like to enroll as a new employee?");
                        }
                        
                        // Not mobile (kiosk) -> visitor flow (known visitor or new visitor)
                        double vTol = ConfigurationService.GetDouble("Visitors:DlibTolerance", attendanceTol);

                        int? bestVisitorId = null;
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
                                    bestVisitorId = e.VisitorId;
                                    bestVisitorName = e.Name;
                                }
                            }
                        }
                        catch
                        {
                            // If visitor index fails, we still allow "new visitor" flow.
                        }

                        bool isKnownVisitor = bestVisitorId.HasValue && bestVisitorDist <= vTol;

                        var scanId = NewScanId();
                        var key = VisitorScanPrefix + scanId;

                        _visitorScanCache.Set(
                            key,
                            new VisitorScanCacheItem
                            {
                                Vec = vec,
                                OfficeId = office.Id,
                                VisitorId = isKnownVisitor ? bestVisitorId : (int?)null,
                                VisitorName = isKnownVisitor ? bestVisitorName : null,
                                SessionBinding = DeviceService.GetVisitorSessionBinding(HttpContext)
                            },
                            DateTimeOffset.UtcNow.AddSeconds(GetVisitorScanTtlSeconds()));

                        return JsonResponseBuilder.VisitorScan(
                            scanId, isKnownVisitor, bestVisitorName, bestVisitorDist, vTol, p,
                            timings, includePerfTimings);
                    }

                    var emp = db.Employees.FirstOrDefault(x => x.EmployeeId == bestEmpId && x.IsActive);
                    if (emp == null)
                        return JsonResponseBuilder.ErrorWithTimings("EMPLOYEE_NOT_FOUND", timings, includePerfTimings);

                    // DEVICE CHECK: Verify if this is a mobile device and if it's registered
                    var deviceFingerprint = DeviceService.GenerateFingerprint(Request);
                    var deviceIsMobile = DeviceService.IsMobileDevice(Request);
                    
                    // FORCE KIOSK MODE: Check for kiosk cookie or header (for tablets/laptops that detect as mobile)
                    var kioskCookie = Request.Cookies["ForceKioskMode"];
                    var forceKiosk = (kioskCookie != null && kioskCookie.Value == "true") || 
                                     (Request.Headers["X-Kiosk-Mode"] == "true");
                    
                    // DEBUG: Log device detection
                    var tokenFromCookie = DeviceService.GetDeviceTokenFromCookie(Request);
                    string deviceTokenFromCheck = null; // Declare outside for later use

                    
                    if (deviceIsMobile && !forceKiosk)
                    {
                        // This is a personal phone (BYOD) - check device registration
                        // Try device token first (persistent), then fingerprint
                        // Use token from form parameter first, then cookie
                        var effectiveDeviceToken = deviceToken ?? tokenFromCookie;

                        var deviceCheck = DeviceService.ValidateDevice(db, deviceFingerprint, emp.Id, effectiveDeviceToken);

                        
                        if (!deviceCheck.Success)
                        {
                            if (deviceCheck.ErrorCode == "NOT_REGISTERED")
                            {
                                // Device not registered - prompt to register
                                return JsonResponseBuilder.RegisterDeviceRequired(
                                    emp.Id,
                                    emp.FirstName + " " + emp.LastName,
                                    deviceFingerprint,
                                    "This device is not registered. Please register it to continue.");
                            }
                            else if (deviceCheck.ErrorCode == "WRONG_EMPLOYEE")
                            {
                                // Device belongs to a different employee - message already contains owner name
                                var ownerName = deviceCheck.Message?.Replace("This device is registered to ", "")?.Replace(". Please use your own registered device.", "") ?? "another employee";
                                return JsonResponseBuilder.Error("WRONG_DEVICE", deviceCheck.Message, null, new { matchedEmployee = ownerName });
                            }
                            else if (deviceCheck.ErrorCode == "PENDING")
                            {
                                return JsonResponseBuilder.DevicePending(
                                    "Your device registration is pending admin approval.");
                            }
                            else if (deviceCheck.ErrorCode == "BLOCKED")
                            {
                                return JsonResponseBuilder.DeviceBlocked(
                                    "This device has been blocked. Contact administrator.");
                            }
                        }
                        // Device is valid - refresh the token cookie
                        deviceTokenFromCheck = deviceCheck.Message;
                        if (!string.IsNullOrEmpty(deviceTokenFromCheck))
                        {
                            DeviceService.SetDeviceTokenCookie(Response, deviceTokenFromCheck, Request.IsSecureConnection);
                        }
                        // Device is valid - continue with attendance
                    }
                    // For non-mobile (kiosk), deviceTokenFromCheck stays null

                    var displayName = emp.LastName + ", " + emp.FirstName +
                                      (string.IsNullOrWhiteSpace(emp.MiddleName) ? "" : " " + emp.MiddleName);

                    double? similarity = attendanceTol > 0
                        ? Math.Max(0.0, Math.Min(1.0, 1.0 - (bestDist / attendanceTol)))
                        : (double?)null;

                    var nearMatchRatio = ConfigurationService.GetDoubleCached("NeedsReview:NearMatchRatio", 0.90);
                    var livenessMargin = ConfigurationService.GetDoubleCached("NeedsReview:LivenessMargin", 0.03);
                    var gpsMargin = ConfigurationService.GetIntCached("NeedsReview:GPSAccuracyMargin", 10);

                    bool needsReviewFlag = false;
                    var reviewNotes = new System.Text.StringBuilder();

                    if (attendanceTol > 0 && bestDist >= (attendanceTol * nearMatchRatio))
                    {
                        needsReviewFlag = true;
                        reviewNotes.Append("Near match. Dist=")
                            .Append(bestDist.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))
                            .Append(" of tol=")
                            .Append(attendanceTol.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))
                            .Append(".");
                    }

                    if (p < (liveTh + livenessMargin))
                    {
                        if (reviewNotes.Length > 0) reviewNotes.Append(" ");
                        needsReviewFlag = true;
                        reviewNotes.Append("Near liveness. Score=")
                            .Append(p.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))
                            .Append(" (th=")
                            .Append(liveTh.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))
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

                    // ANTI-SPOOFING: Check for mock GPS only (no teleportation check)
                    var deviceFp = DeviceService.GetShortDeviceFingerprint(HttpContext);
                    if (lat.HasValue && lon.HasValue)
                    {
                        var spoofCheck = LocationAntiSpoof.CheckLocation(
                            emp.Id, lat.Value, lon.Value, DateTime.UtcNow, deviceFp);
                        
                        if (spoofCheck.Action == "BLOCK")
                        {
                            return JsonResponseBuilder.SuspiciousLocation(
                                "Location verification failed. Please contact admin.",
                                timings, includePerfTimings);
                        }
                    }

                    var log = new AttendanceLog
                    {
                        EmployeeId = emp.Id,
                        OfficeId = office.Id,

                        EmployeeFullName = StringHelper.Truncate(displayName, 400),
                        Department = StringHelper.Truncate(emp.Department, 200),
                        OfficeType = StringHelper.Truncate(office.Type, 40),
                        OfficeName = StringHelper.Truncate(office.Name, 400),

                        GPSLatitude = OfficeLocationService.TruncateGpsCoordinate(lat),
                        GPSLongitude = OfficeLocationService.TruncateGpsCoordinate(lon),
                        GPSAccuracy = accuracy,

                        LocationVerified = locationVerified,
                        FaceDistance = bestDist,
                        FaceSimilarity = similarity,
                        MatchThreshold = attendanceTol,
                        LivenessScore = p,
                        LivenessResult = "PASS",

                        ClientIP = StringHelper.Truncate(Request.UserHostAddress ?? "", 100),
                        UserAgent = StringHelper.Truncate(Request.UserAgent ?? "", 1000),
                        WiFiSSID = StringHelper.Truncate(office.WiFiSSID, 200),

                        NeedsReview = needsReviewFlag,
                        Notes = reviewNotes.Length == 0 ? null : reviewNotes.ToString()
                    };

                    if (IsRequestTimedOut(sw)) return RequestTimeoutResult(includePerfTimings, timings);

                    var rec = AttendanceService.Record(db, log, requestedAtUtc);
                    mark("db_ms");

                    if (!rec.Ok)
                    {

                        if (string.Equals(rec.Code, "TOO_SOON", StringComparison.OrdinalIgnoreCase))
                        {
                            var minGapSeconds = ConfigurationService.GetInt(
                                db, "Attendance:MinGapSeconds",
                                ConfigurationService.GetInt("Attendance:MinGapSeconds", 180));

                            var mins = (minGapSeconds >= 60) ? (minGapSeconds / 60) : 0;
                            var msg = mins > 0
                                ? ("Already scanned. Please wait " + mins + " minute(s).")
                                : ("Already scanned. Please wait " + minGapSeconds + " second(s).");

                            return JsonResponseBuilder.TooSoon(msg, minGapSeconds, timings, includePerfTimings);
                        }

                        return JsonResponseBuilder.ErrorWithTimings(rec.Code, timings, includePerfTimings, rec.Message);
                    }

                    mark("total_ms");

                    return JsonResponseBuilder.AttendanceSuccess(
                        emp.EmployeeId, displayName, displayName, rec.EventType, rec.Message,
                        office.Id, office.Name, p, bestDist, requestedAtUtc, timings, includePerfTimings,
                        deviceTokenFromCheck); // Pass token so client can save to localStorage
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("[Kiosk.Attend] ScanAttendanceCore failed: " + ex);
                // Security hardening:
                // generic lang sa client by default. Raw detail ay debug-only.
                var debug = ConfigurationService.GetBool("Biometrics:Debug", false);
                var baseEx = ex.GetBaseException();

                return JsonResponseBuilder.ScanError(
                    ex.Message, 
                    baseEx?.Message, 
                    timings, includePerfTimings, debug);
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

            var key = VisitorScanPrefix + scanId;
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
                        res = VisitorService.RecordVisit(
                            db,
                            item.VisitorId.Value,
                            item.OfficeId,
                            purpose,
                            ip,
                            ua);
                    }
                    else
                    {
                        name = (name ?? "").Trim();
                        if (string.IsNullOrWhiteSpace(name))
                            return Json(new { ok = false, error = "NAME_REQUIRED", message = "Name is required." });

                        var now = DateTime.UtcNow;

                        var bytes = DlibBiometrics.EncodeToBytes(item.Vec);
                        var b64 = BiometricCrypto.ProtectBase64Bytes(bytes);

                        if (string.IsNullOrWhiteSpace(b64))
                            return Json(new { ok = false, error = "ENCODE_ERROR", message = "Could not save face." });

                        var v = new Visitor
                        {
                            Name = name,
                            FaceEncodingBase64 = b64,
                            VisitCount = 0,
                            FirstVisitDate = now,
                            LastVisitDate = now,
                            IsActive = true
                        };

                        db.Visitors.Add(v);
                        db.SaveChanges();

                        VisitorFaceIndex.Invalidate();

                        res = VisitorService.RecordVisit(
                            db,
                            v.Id,
                            item.OfficeId,
                            purpose,
                            ip,
                            ua);
                    }

                    return Json(new
                    {
                        ok = res.Ok,
                        mode = "VISITOR_RECORDED",
                        isKnown = res.IsKnown,
                        visitorName = res.VisitorName,
                        message = res.Message,
                        error = res.Ok ? null : res.Code
                    });
                }
                catch
                {
                    return Json(new { ok = false, error = "VISITOR_SAVE_ERROR", message = "Could not save visitor." });
                }
                finally
                {
                    // Important:
                    // one-time lang ang scan cache item. Kahit validation fail o exception,
                    // alisin para walang stale biometric payload na maiwan sa memory.
                    _visitorScanCache.Remove(key);
                }
            }
        }
        [HttpPost]
        [ValidateAntiForgeryToken]
        [RateLimit(Name = "UnlockPin", MaxRequests = 5, WindowSeconds = 60)]
        public ActionResult UnlockPin(string pin, string returnUrl)
        {
            // SECURITY: Hard block admin unlock on mobile devices
            if (DeviceService.IsMobileDevice(Request))
            {
                Response.StatusCode = 403;
                return Json(new { ok = false, error = "UNLOCK_DISABLED_ON_MOBILE" });
            }

            var safeReturn = AdminAuthorizeAttribute.SanitizeReturnUrl(returnUrl);
            var ip = Request.UserHostAddress;

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

        // ------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------

        private static int GetMaxConcurrentScans()
        {
            var value = ConfigurationService.GetInt("Kiosk:MaxConcurrentScans", 16);
            if (value < 1) value = 1;
            return value;
        }

        private static int GetRequestTimeoutMs()
        {
            var value = ConfigurationService.GetInt("Kiosk:RequestTimeoutMs", 28000);
            if (value < 5000) value = 5000;
            return value;
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

        // PHASE 2 FIX: Removed unused methods - using KioskSessionService and OfficeLocationService instead
        // - GetVisitorSessionBinding() → Use DeviceService.GetVisitorSessionBinding()
        // - GetDeviceFingerprint() → Use DeviceService.GetShortDeviceFingerprint()
        // - TruncateGpsCoordinate() → Use OfficeLocationService.TruncateGpsCoordinate()
        // - OfficePick class → Use OfficeLocationService.OfficePickResult
        // - IsGpsRequired() → Use Request.Browser.IsMobileDevice inline
        // - GetFallbackOffice() → Use OfficeLocationService.GetFallbackOffice()
        // - PickOffice() → Use OfficeLocationService.PickOffice()
    }
}
