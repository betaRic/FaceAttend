using System;
using System.Linq;
using System.Diagnostics;
using System.Runtime.Caching;
using System.Web;
using System.Web.Mvc;
using FaceAttend;
using FaceAttend.Filters;
using FaceAttend.Services;
using FaceAttend.Services.Biometrics;
using FaceAttend.Services.Security;

namespace FaceAttend.Controllers
{
    public class KioskController : Controller
    {
        

        private class VisitorScanCacheItem
        {
            public double[] Vec { get; set; }
            public int OfficeId { get; set; }
            public int? VisitorId { get; set; }
            public string VisitorName { get; set; }
        }

        private static readonly MemoryCache _visitorScanCache = MemoryCache.Default;
        private const string VisitorScanPrefix = "VISITORSCAN::";

        private static int GetVisitorScanTtlSeconds()
        {
            var s = AppSettings.GetInt("Kiosk:VisitorScanTtlSeconds", 180);
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
            return View();
        }

        [HttpGet]
        [AdminAuthorize]
        public ActionResult ActiveOffices()
        {
            using (var db = new FaceAttendDBEntities())
            {
                var list = db.Offices
                    .Where(o => o.IsActive)
                    .OrderBy(o => o.Name)
                    .Select(o => new { id = o.Id, name = o.Name })
                    .ToList();

                return Json(new { ok = true, offices = list }, JsonRequestBehavior.AllowGet);
            }
        }

                [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult ResolveOffice(double? lat, double? lon, double? accuracy)
        {
            using (var db = new FaceAttendDBEntities())
            {
                // GPS is required only on mobile devices, but we still use GPS on desktop
                // when the browser provides coordinates.
                bool gpsRequired = IsGpsRequired();
                bool hasCoords = lat.HasValue && lon.HasValue;

                if (!hasCoords)
                {
                    if (gpsRequired)
                    {
                        return Json(new
                        {
                            ok = true,
                            gpsRequired = true,
                            allowed = false,
                            reason = "GPS_REQUIRED",
                            requiredAccuracy = SystemConfigService.GetInt(
                                db, "Location:GPSAccuracyRequired",
                                AppSettings.GetInt("Location:GPSAccuracyRequired", 50)),
                            accuracy = accuracy
                        });
                    }

                    var fallback = GetFallbackOffice(db);
                    return Json(new
                    {
                        ok = true,
                        gpsRequired = false,
                        allowed = true,
                        officeId = fallback == null ? (int?)null : fallback.Id,
                        officeName = fallback == null ? null : fallback.Name
                    });
                }

                var pick = PickOffice(db, lat.Value, lon.Value, accuracy);
                if (!pick.Allowed)
                {
                    return Json(new
                    {
                        ok = true,
                        gpsRequired = gpsRequired,
                        allowed = false,
                        reason = pick.Reason,
                        requiredAccuracy = pick.RequiredAccuracy,
                        accuracy = accuracy
                    });
                }

                return Json(new
                {
                    ok = true,
                    gpsRequired = gpsRequired,
                    allowed = true,
                    reason = "OK",
                    officeId = pick.Office.Id,
                    officeName = pick.Office.Name
                });
            }
        }


        // ------------------------------------------------------------
        // Cheap face detection endpoint (no liveness)
        // ------------------------------------------------------------
        [HttpPost]
        [ValidateAntiForgeryToken]
        [RateLimit(Name = "KioskDetectFace", MaxRequests = 180, WindowSeconds = 60, Burst = 60)]
        public ActionResult DetectFace(double? lat, double? lon, double? accuracy, HttpPostedFileBase image)
        {
            if (image == null || image.ContentLength <= 0)
                return Json(new { ok = false, error = "NO_IMAGE" });

            var max = AppSettings.GetInt("Biometrics:MaxUploadBytes", 10 * 1024 * 1024);
            if (image.ContentLength > max)
                return Json(new { ok = false, error = "TOO_LARGE" });

            string path = null;
            string processedPath = null;

            try
            {
                using (var db = new FaceAttendDBEntities())
                {
                    bool gpsRequired = IsGpsRequired();
                    Office office = null;

                    // Use GPS on any device if coords are provided.
                    // Only require GPS when gpsRequired == true (mobile devices).
                    if (lat.HasValue && lon.HasValue)
                    {
                        var pick = PickOffice(db, lat.Value, lon.Value, accuracy);
                        if (!pick.Allowed)
                        {
                            return Json(new
                            {
                                ok = true,
                                gpsRequired = gpsRequired,
                                allowed = false,
                                reason = pick.Reason,
                                requiredAccuracy = pick.RequiredAccuracy,
                                accuracy = accuracy,
                                faceCount = 0,
                                count = 0,
                                livenessScore = (float?)null,
                                liveness = (float?)null,
                                livenessPass = false,
                                livenessOk = false,
                                faceBox = (object)null
                            });
                        }

                        office = pick.Office;
                    }
                    else if (gpsRequired)
                    {
                        return Json(new
                        {
                            ok = true,
                            gpsRequired = true,
                            allowed = false,
                            reason = "GPS_REQUIRED",
                            requiredAccuracy = SystemConfigService.GetInt(
                                db, "Location:GPSAccuracyRequired",
                                AppSettings.GetInt("Location:GPSAccuracyRequired", 50)),
                            accuracy = accuracy,
                            faceCount = 0,
                            count = 0,
                            livenessScore = (float?)null,
                            liveness = (float?)null,
                            livenessPass = false,
                            livenessOk = false,
                            faceBox = (object)null
                        });
                    }
                    path = SecureFileUpload.SaveTemp(image, "k_", max);

                    bool isProcessed;
                    processedPath = ImagePreprocessor.PreprocessForDetection(path, "k_", out isProcessed);

                    int imgW = 0, imgH = 0;
                    try
                    {
                        using (var bmp = new System.Drawing.Bitmap(processedPath))
                        {
                            imgW = bmp.Width;
                            imgH = bmp.Height;
                        }
                    }
                    catch { }

                    var dlib = new DlibBiometrics();
                    var faces = dlib.DetectFacesFromFile(processedPath);
                    var count = faces == null ? 0 : faces.Length;

                    DlibBiometrics.FaceBox best = null;
                    if (faces != null && faces.Length > 0)
                        best = faces.OrderByDescending(f => (long)f.Width * f.Height).FirstOrDefault();

                    object faceBox = null;
                    if (best != null)
                        faceBox = new { x = best.Left, y = best.Top, w = best.Width, h = best.Height, imgW, imgH };

                    return Json(new
                    {
                        ok = true,
                        gpsRequired,
                        allowed = true,
                        officeId = office == null ? (int?)null : office.Id,
                        officeName = office == null ? null : office.Name,
                        faceCount = count,
                        count,
                        livenessScore = (float?)null,
                        liveness = (float?)null,
                        livenessPass = false,
                        livenessOk = false,
                        faceBox
                    });
                }
            }
            catch (Exception ex)
            {
                var baseEx = ex.GetBaseException();
                return Json(new
                {
                    ok = false,
                    error = "SCAN_ERROR",
                    detail = ex.Message,
                    inner = baseEx == null ? null : baseEx.Message
                });
            }
            finally
            {
                ImagePreprocessor.Cleanup(processedPath, path);
                SecureFileUpload.TryDelete(path);
            }
        }

        // ------------------------------------------------------------
        // Liveness endpoint (heavier). Called only when face is stable.
        // ------------------------------------------------------------
        [HttpPost]
        [ValidateAntiForgeryToken]
        [RateLimit(Name = "KioskScanFrame", MaxRequests = 120, WindowSeconds = 60, Burst = 40)]
        public ActionResult ScanFrame(double? lat, double? lon, double? accuracy, HttpPostedFileBase image)
        {
            if (image == null || image.ContentLength <= 0)
                return Json(new { ok = false, error = "NO_IMAGE" });

            var max = AppSettings.GetInt("Biometrics:MaxUploadBytes", 10 * 1024 * 1024);
            if (image.ContentLength > max)
                return Json(new { ok = false, error = "TOO_LARGE" });

            string path = null;
            string processedPath = null;

            try
            {
                using (var db = new FaceAttendDBEntities())
                {
                    bool gpsRequired = IsGpsRequired();
                    Office office = null;

                    // Use GPS on any device if coords are provided.
                    // Only require GPS when gpsRequired == true (mobile devices).
                    if (lat.HasValue && lon.HasValue)
                    {
                        var pick = PickOffice(db, lat.Value, lon.Value, accuracy);
                        if (!pick.Allowed)
                        {
                            return Json(new
                            {
                                ok = true,
                                gpsRequired = gpsRequired,
                                allowed = false,
                                reason = pick.Reason,
                                requiredAccuracy = pick.RequiredAccuracy,
                                accuracy = accuracy,
                                faceCount = 0,
                                count = 0,
                                livenessScore = (float?)null,
                                liveness = (float?)null,
                                livenessPass = false,
                                livenessOk = false,
                                livenessThreshold = (float?)null,
                                faceBox = (object)null
                            });
                        }

                        office = pick.Office;
                    }
                    else if (gpsRequired)
                    {
                        return Json(new
                        {
                            ok = true,
                            gpsRequired = true,
                            allowed = false,
                            reason = "GPS_REQUIRED",
                            requiredAccuracy = SystemConfigService.GetInt(
                                db, "Location:GPSAccuracyRequired",
                                AppSettings.GetInt("Location:GPSAccuracyRequired", 50)),
                            accuracy = accuracy,
                            faceCount = 0,
                            count = 0,
                            livenessScore = (float?)null,
                            liveness = (float?)null,
                            livenessPass = false,
                            livenessOk = false,
                            livenessThreshold = (float?)null,
                            faceBox = (object)null
                        });
                    }
                    path = SecureFileUpload.SaveTemp(image, "k_", max);

                    bool isProcessed;
                    processedPath = ImagePreprocessor.PreprocessForDetection(path, "k_", out isProcessed);

                    int imgW = 0, imgH = 0;
                    try
                    {
                        using (var bmp = new System.Drawing.Bitmap(processedPath))
                        {
                            imgW = bmp.Width;
                            imgH = bmp.Height;
                        }
                    }
                    catch { }

                    var dlib = new DlibBiometrics();
                    var faces = dlib.DetectFacesFromFile(processedPath);
                    var count = faces == null ? 0 : faces.Length;

                    DlibBiometrics.FaceBox best = null;
                    if (faces != null && faces.Length > 0)
                        best = faces.OrderByDescending(f => (long)f.Width * f.Height).FirstOrDefault();

                    object faceBox = null;
                    if (best != null)
                        faceBox = new { x = best.Left, y = best.Top, w = best.Width, h = best.Height, imgW, imgH };

                    if (count != 1)
                    {
                        return Json(new
                        {
                            ok = true,
                            gpsRequired,
                            allowed = true,
                            officeId = office == null ? (int?)null : office.Id,
                            officeName = office == null ? null : office.Name,
                            faceCount = count,
                            count,
                            livenessScore = (float?)null,
                            liveness = (float?)null,
                            livenessPass = false,
                            livenessOk = false,
                            livenessThreshold = (float?)null,
                            faceBox
                        });
                    }

                    var live = new OnnxLiveness();
                    var scored = live.ScoreFromFile(processedPath, faces[0]);
                    if (!scored.Ok)
                        return Json(new { ok = false, error = scored.Error, count = 1 });

                    var th = (float)SystemConfigService.GetDouble(
                        db, "Biometrics:LivenessThreshold",
                        AppSettings.GetDouble("Biometrics:LivenessThreshold", 0.75));

                    var p = scored.Probability ?? 0f;

                    return Json(new
                    {
                        ok = true,
                        gpsRequired,
                        allowed = true,
                        officeId = office == null ? (int?)null : office.Id,
                        officeName = office == null ? null : office.Name,
                        faceCount = 1,
                        count = 1,
                        livenessScore = p,
                        liveness = p,
                        livenessPass = p >= th,
                        livenessOk = p >= th,
                        livenessThreshold = th,
                        faceBox
                    });
                }
            }
            catch (Exception ex)
            {
                var baseEx = ex.GetBaseException();
                return Json(new
                {
                    ok = false,
                    error = "SCAN_ERROR",
                    detail = ex.Message,
                    inner = baseEx == null ? null : baseEx.Message
                });
            }
            finally
            {
                ImagePreprocessor.Cleanup(processedPath, path);
                SecureFileUpload.TryDelete(path);
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RateLimit(Name = "KioskAttend", MaxRequests = 20, WindowSeconds = 60, Burst = 10)]
        public ActionResult Attend(double? lat, double? lon, double? accuracy, HttpPostedFileBase image)
        {
            // Feature flag / rollback
            if (!AppSettings.GetBool("Kiosk:UseNextGen", false))
                return Json(new { ok = false, error = "NEXTGEN_DISABLED" });

            return ScanAttendanceCore(lat, lon, accuracy, image, includePerfTimings: AppSettings.GetBool("Kiosk:EnablePerfTimings", false));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult ScanAttendance(double? lat, double? lon, double? accuracy, HttpPostedFileBase image)
        {
            return ScanAttendanceCore(lat, lon, accuracy, image, includePerfTimings: AppSettings.GetBool("Kiosk:EnablePerfTimings", false));

        }

        
        private ActionResult ScanAttendanceCore(double? lat, double? lon, double? accuracy, HttpPostedFileBase image, bool includePerfTimings)
        {
            var sw = Stopwatch.StartNew();
            var timings = new System.Collections.Generic.Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            System.Action<string> mark = key => { if (includePerfTimings) timings[key] = sw.ElapsedMilliseconds; };

            if (image == null || image.ContentLength <= 0)
                return Json(new { ok = false, error = "NO_IMAGE", timings = includePerfTimings ? timings : null });

            var max = AppSettings.GetInt("Biometrics:MaxUploadBytes", 10 * 1024 * 1024);
            if (image.ContentLength > max)
                return Json(new { ok = false, error = "TOO_LARGE", timings = includePerfTimings ? timings : null });

            string path = null;
            string processedPath = null;

            try
            {
                using (var db = new FaceAttendDBEntities())
                {
                    Office office = null;
                    bool gpsRequired = IsGpsRequired();
                    bool locationVerified = false;
                    int requiredAcc = 0;

                    // If GPS coords are provided (mobile or desktop), prefer picking the nearest office.
                    // GPS is required only on mobile devices.
                    if (lat.HasValue && lon.HasValue)
                    {
                        var pick = PickOffice(db, lat.Value, lon.Value, accuracy);
                        if (!pick.Allowed)
                            return Json(new { ok = false, error = pick.Reason, timings = includePerfTimings ? timings : null });

                        office = pick.Office;
                        locationVerified = true;
                        requiredAcc = pick.RequiredAccuracy;
                    }
                    else if (gpsRequired)
                    {
                        return Json(new { ok = false, error = "GPS_REQUIRED", timings = includePerfTimings ? timings : null });
                    }
                    else
                    {
                        office = GetFallbackOffice(db);
                        locationVerified = false;
                    }
                    if (office == null)
                        return Json(new { ok = false, error = "NO_OFFICES", timings = includePerfTimings ? timings : null });

                    path = SecureFileUpload.SaveTemp(image, "k_", max);
                    mark("saved_ms");

                    bool isProcessed;
                    processedPath = ImagePreprocessor.PreprocessForDetection(path, "k_", out isProcessed);
                    mark("preprocess_ms");

                    var dlib = new DlibBiometrics();

                    // Detect exactly one face (no double detection later)
                    DlibBiometrics.FaceBox faceBox;
                    FaceRecognitionDotNet.Location faceLoc;
                    string faceErr;

                    if (!dlib.TryDetectSingleFaceFromFile(processedPath, out faceBox, out faceLoc, out faceErr))
                        return Json(new { ok = false, error = faceErr ?? "FACE_FAIL", timings = includePerfTimings ? timings : null });

                    mark("dlib_detect_ms");

                    // Liveness (server truth)
                    var live = new OnnxLiveness();
                    var scored = live.ScoreFromFile(processedPath, faceBox);
                    mark("liveness_ms");

                    if (!scored.Ok)
                        return Json(new { ok = false, error = scored.Error, timings = includePerfTimings ? timings : null });

                    var liveTh = (float)SystemConfigService.GetDoubleCached(
                        "Biometrics:LivenessThreshold",
                        AppSettings.GetDouble("Biometrics:LivenessThreshold", 0.75));

                    var p = scored.Probability ?? 0f;
                    if (p < liveTh)
                        return Json(new { ok = false, error = "LIVENESS_FAIL", liveness = p, threshold = liveTh, timings = includePerfTimings ? timings : null });

                    // Encode using known location (skips FaceLocations)
                    double[] vec;
                    string encErr;
                    if (!dlib.TryEncodeFromFileWithLocation(processedPath, faceLoc, out vec, out encErr) || vec == null)
                        return Json(new { ok = false, error = "ENCODING_FAIL", detail = encErr, timings = includePerfTimings ? timings : null });

                    mark("dlib_encode_ms");

                    var tolFallback = AppSettings.GetDouble("Biometrics:DlibTolerance", 0.60);
                    var tol = SystemConfigService.GetDoubleCached(
                        "Biometrics:DlibTolerance",
                        SystemConfigService.GetDoubleCached("DlibTolerance", tolFallback));

                    double brightness = 128.0;
                    int imgWidth = 0;
                    try
                    {
                        using (var bmp = new System.Drawing.Bitmap(processedPath))
                        {
                            imgWidth = bmp.Width;
                            long sum = 0;
                            int samples = 0;
                            for (int y = 0; y < bmp.Height; y += 8)
                            for (int x = 0; x < bmp.Width; x += 8)
                            {
                                var px = bmp.GetPixel(x, y);
                                sum += (px.R + px.G + px.B) / 3;
                                samples++;
                            }
                            if (samples > 0) brightness = (double)sum / samples;
                        }
                    }
                    catch { }

                    var adaptiveTolEnabled = AppSettings.GetBool("Biometrics:FaceMatchTunerEnabled", false);
                    if (adaptiveTolEnabled)
                    {
                        var adaptive = FaceMatchTuner.CalculateAdaptiveThreshold(
                            baseTolerance: tol,
                            imageBrightness: brightness,
                            faceDetectionScore: 1.0,
                            isMobile: IsGpsRequired(),
                            imageWidth: imgWidth);

                        tol = adaptive.AdjustedTolerance;
                    }

                    double bestDist;
                    var bestEmpId = EmployeeFaceIndex.FindNearest(db, vec, tol, out bestDist);
                    mark("match_ms");

                    if (bestEmpId == null || bestDist > tol)
                    {
                        // Unknown employee -> visitor flow (known visitor or new visitor)
                        double vTol = AppSettings.GetDouble("Visitors:DlibTolerance", tol);

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
                                VisitorName = isKnownVisitor ? bestVisitorName : null
                            },
                            DateTimeOffset.UtcNow.AddSeconds(GetVisitorScanTtlSeconds()));

                        return Json(new
                        {
                            ok = true,
                            mode = "VISITOR",
                            scanId,
                            isKnown = isKnownVisitor,
                            visitorName = isKnownVisitor ? bestVisitorName : null,
                            distance = (double.IsInfinity(bestVisitorDist) || double.IsNaN(bestVisitorDist))
                            ? (double?)null
                            : bestVisitorDist,
                            threshold = vTol,
                            liveness = p,
                            timings = includePerfTimings ? timings : null
                        });
                    }

                    var emp = db.Employees.FirstOrDefault(x => x.EmployeeId == bestEmpId && x.IsActive);
                    if (emp == null)
                        return Json(new { ok = false, error = "EMPLOYEE_NOT_FOUND", timings = includePerfTimings ? timings : null });

                    var displayName = emp.LastName + ", " + emp.FirstName +
                                      (string.IsNullOrWhiteSpace(emp.MiddleName) ? "" : " " + emp.MiddleName);

                    double? similarity = tol > 0
                        ? Math.Max(0.0, Math.Min(1.0, 1.0 - (bestDist / tol)))
                        : (double?)null;

                    var nearMatchRatio = SystemConfigService.GetDoubleCached("NeedsReview:NearMatchRatio", 0.90);
                    var livenessMargin = SystemConfigService.GetDoubleCached("NeedsReview:LivenessMargin", 0.03);
                    var gpsMargin = SystemConfigService.GetIntCached("NeedsReview:GPSAccuracyMargin", 10);

                    bool needsReviewFlag = false;
                    var reviewNotes = new System.Text.StringBuilder();

                    if (tol > 0 && bestDist >= (tol * nearMatchRatio))
                    {
                        needsReviewFlag = true;
                        reviewNotes.Append("Near match. Dist=")
                            .Append(bestDist.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))
                            .Append(" of tol=")
                            .Append(tol.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))
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

                    var log = new AttendanceLog
                    {
                        EmployeeId = emp.Id,
                        OfficeId = office.Id,

                        EmployeeFullName = Trunc(displayName, 400),
                        Department = Trunc(emp.Department, 200),
                        OfficeType = Trunc(office.Type, 40),
                        OfficeName = Trunc(office.Name, 400),

                        GPSLatitude = lat,
                        GPSLongitude = lon,
                        GPSAccuracy = accuracy,

                        LocationVerified = locationVerified,
                        FaceDistance = bestDist,
                        FaceSimilarity = similarity,
                        MatchThreshold = tol,
                        LivenessScore = p,
                        LivenessResult = "PASS",

                        ClientIP = Trunc(Request.UserHostAddress ?? "", 100),
                        UserAgent = Trunc(Request.UserAgent ?? "", 1000),
                        WiFiSSID = Trunc(office.WiFiSSID, 200),

                        NeedsReview = needsReviewFlag,
                        Notes = reviewNotes.Length == 0 ? null : reviewNotes.ToString()
                    };

                    var rec = AttendanceService.Record(db, log);
                    mark("db_ms");

                    if (!rec.Ok)
                    {
                        if (string.Equals(rec.Code, "TOO_SOON", StringComparison.OrdinalIgnoreCase))
                        {
                            var minGapSeconds = SystemConfigService.GetInt(
                                db, "Attendance:MinGapSeconds",
                                AppSettings.GetInt("Attendance:MinGapSeconds", 180));

                            var mins = (minGapSeconds >= 60) ? (minGapSeconds / 60) : 0;
                            var msg = mins > 0
                                ? ("Already scanned. Please wait " + mins + " minute(s).")
                                : ("Already scanned. Please wait " + minGapSeconds + " second(s).");

                            return Json(new { ok = false, error = rec.Code, message = msg, timings = includePerfTimings ? timings : null });
                        }

                        return Json(new { ok = false, error = rec.Code, message = rec.Message, timings = includePerfTimings ? timings : null });
                    }

                    mark("total_ms");

                    return Json(new
                    {
                        ok = true,
                        employeeId = emp.EmployeeId,
                        name = displayName,
                        displayName,
                        eventType = rec.EventType,
                        message = rec.Message,
                        officeId = office.Id,
                        officeName = office.Name,
                        liveness = p,
                        distance = bestDist,
                        timings = includePerfTimings ? timings : null
                    });
                }
            }
            catch (Exception ex)
            {
                var baseEx = ex.GetBaseException();
                return Json(new
                {
                    ok = false,
                    error = "SCAN_ERROR",
                    detail = ex.Message,
                    inner = baseEx == null ? null : baseEx.Message,
                    timings = includePerfTimings ? timings : null
                });
            }
            finally
            {
                ImagePreprocessor.Cleanup(processedPath, path);
                SecureFileUpload.TryDelete(path);
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
                        var b64 = bytes == null ? null : Convert.ToBase64String(bytes);

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

                    _visitorScanCache.Remove(key);

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
            }
        }

[HttpPost]
        [ValidateAntiForgeryToken]
        [RateLimit(Name = "UnlockPin", MaxRequests = 5, WindowSeconds = 60)]
        public ActionResult UnlockPin(string pin, string returnUrl)
        {
            var safeReturn = AdminAuthorizeAttribute.SanitizeReturnUrl(returnUrl);
            var ip = Request.UserHostAddress;

            if (!AdminAuthorizeAttribute.VerifyPin(pin, ip))
                return Json(new { ok = false, error = "INVALID_PIN" });

            AdminAuthorizeAttribute.ClearAuthedMarker(Session);
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

        private class OfficePick
        {
            public bool Allowed { get; set; }
            public string Reason { get; set; }
            public Office Office { get; set; }
            public double DistanceMeters { get; set; }
            public int RadiusMeters { get; set; }
            public int RequiredAccuracy { get; set; }
        }

        private static string Trunc(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Length <= max ? s : s.Substring(0, max);
        }

        private bool IsGpsRequired()
        {
            if (Request?.Browser != null && Request.Browser.IsMobileDevice)
                return true;

            var ua = (Request?.UserAgent ?? "").ToLowerInvariant();
            return ua.Contains("android") || ua.Contains("iphone") ||
                   ua.Contains("ipad") || ua.Contains("ipod") ||
                   ua.Contains("tablet");
        }

        private Office GetFallbackOffice(FaceAttendDBEntities db)
        {
            int preferred = SystemConfigService.GetInt(
                db, "Kiosk:FallbackOfficeId",
                AppSettings.GetInt("Kiosk:FallbackOfficeId", 0));

            if (preferred > 0)
            {
                var chosen = db.Offices.FirstOrDefault(o => o.Id == preferred && o.IsActive);
                if (chosen != null) return chosen;
            }

            return db.Offices.Where(o => o.IsActive).OrderBy(o => o.Name).FirstOrDefault();
        }

        private OfficePick PickOffice(FaceAttendDBEntities db, double lat, double lon, double? accuracy)
        {
            int requiredAcc = SystemConfigService.GetInt(
                db, "Location:GPSAccuracyRequired",
                AppSettings.GetInt("Location:GPSAccuracyRequired", 50));

            if (!accuracy.HasValue)
                return new OfficePick { Allowed = false, Reason = "GPS_ACCURACY", RequiredAccuracy = requiredAcc };

            if (accuracy.Value > requiredAcc)
                return new OfficePick { Allowed = false, Reason = "GPS_ACCURACY", RequiredAccuracy = requiredAcc };

            int defaultRadius = SystemConfigService.GetInt(
                db, "Location:GPSRadiusDefault",
                AppSettings.GetInt("Location:GPSRadiusDefault", 100));

            var offices = db.Offices.Where(o => o.IsActive).ToList();
            if (offices == null || offices.Count == 0)
                return new OfficePick { Allowed = false, Reason = "NO_OFFICES", RequiredAccuracy = requiredAcc };

            Office best = null;
            double bestDist = double.PositiveInfinity;
            int bestRadius = 0;

            foreach (var o in offices)
            {
                int radius = o.RadiusMeters > 0 ? o.RadiusMeters : defaultRadius;
                double d = GeoUtil.DistanceMeters(lat, lon, o.Latitude, o.Longitude);
                if (d <= radius && d < bestDist)
                {
                    best = o;
                    bestDist = d;
                    bestRadius = radius;
                }
            }

            if (best == null)
                return new OfficePick { Allowed = false, Reason = "NO_OFFICE_NEARBY", RequiredAccuracy = requiredAcc };

            return new OfficePick
            {
                Allowed = true,
                Reason = "OK",
                Office = best,
                DistanceMeters = bestDist,
                RadiusMeters = bestRadius,
                RequiredAccuracy = requiredAcc
            };
        }
    }
}
