using System;
using System.IO;
using System.Linq;
using System.Web;
using System.Web.Hosting;
using System.Web.Mvc;
using FaceAttend.Filters;
using FaceAttend.Services;
using FaceAttend.Services.Biometrics;

namespace FaceAttend.Controllers
{
    public class KioskController : Controller
    {
        [HttpGet]
        public ActionResult Index(string returnUrl, int? unlock)
        {
            // FIX (Open Redirect): sanitize before storing in ViewBag.
            ViewBag.ReturnUrl = AdminAuthorizeAttribute.SanitizeReturnUrl(returnUrl);
            ViewBag.UnlockHint = (unlock ?? 0) == 1;
            return View();
        }

        // FIX: Was public unauthenticated GET — exposed full GPS coordinates of
        // every active office to anonymous clients.  Now requires admin auth and
        // returns only what the kiosk UI actually needs.
        [HttpGet]
        [AdminAuthorize]
        public ActionResult ActiveOffices()
        {
            using (var db = new FaceAttendDBEntities())
            {
                var list = db.Offices
                    .Where(o => o.IsActive)
                    .OrderBy(o => o.Name)
                    .Select(o => new { id = o.Id, name = o.Name })   // omit radius/GPS from response
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
                if (!IsGpsRequired())
                {
                    var fallback = GetFallbackOffice(db);
                    return Json(new
                    {
                        ok = true,
                        gpsRequired = false,
                        allowed = true,
                        officeId = fallback == null ? (int?)null : fallback.Id,
                        officeName = fallback == null ? null : fallback.Name,
                        office = fallback == null ? null : new
                        {
                            id = fallback.Id,
                            name = fallback.Name,
                            radiusMeters = fallback.RadiusMeters
                        }
                    });
                }

                if (!lat.HasValue || !lon.HasValue)
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

                var pick = PickOffice(db, lat.Value, lon.Value, accuracy);
                if (!pick.Allowed)
                {
                    return Json(new
                    {
                        ok = true,
                        gpsRequired = true,
                        allowed = false,
                        reason = pick.Reason,
                        requiredAccuracy = pick.RequiredAccuracy,
                        accuracy = accuracy
                    });
                }

                return Json(new
                {
                    ok = true,
                    gpsRequired = true,
                    allowed = true,
                    reason = "OK",
                    officeId = pick.Office.Id,
                    officeName = pick.Office.Name,
                    office = new
                    {
                        id = pick.Office.Id,
                        name = pick.Office.Name,
                        radiusMeters = pick.RadiusMeters,
                        distanceMeters = pick.DistanceMeters
                    }
                });
            }
        }

        // FIX: Rate-limited — 30 requests per minute per IP.
        [HttpPost]
        [ValidateAntiForgeryToken]
        [RateLimit(Name = "KioskScanFrame", MaxRequests = 30, WindowSeconds = 60)]
        public ActionResult ScanFrame(
            double? lat, double? lon, double? accuracy, HttpPostedFileBase image)
        {
            if (image == null || image.ContentLength <= 0)
                return Json(new { ok = false, error = "NO_IMAGE" });

            var max = AppSettings.GetInt("Biometrics:MaxUploadBytes", 10 * 1024 * 1024);
            if (image.ContentLength > max)
                return Json(new { ok = false, error = "TOO_LARGE" });

            string path = null;
            try
            {
                using (var db = new FaceAttendDBEntities())
                {
                    bool gpsRequired = IsGpsRequired();

                    Office office = null;
                    int radius = 0;
                    double dist = 0;

                    if (gpsRequired)
                    {
                        if (!lat.HasValue || !lon.HasValue)
                        {
                            return Json(new
                            {
                                ok = true,
                                gpsRequired = true,
                                allowed = false,
                                reason = "GPS_REQUIRED",
                                faceCount = 0, count = 0,
                                livenessScore = (float?)null, liveness = (float?)null,
                                livenessPass = false, livenessOk = false,
                                faceBox = (object)null
                            });
                        }

                        var pick = PickOffice(db, lat.Value, lon.Value, accuracy);
                        if (!pick.Allowed)
                        {
                            return Json(new
                            {
                                ok = true,
                                gpsRequired = true,
                                allowed = false,
                                reason = pick.Reason,
                                requiredAccuracy = pick.RequiredAccuracy,
                                accuracy = accuracy,
                                faceCount = 0, count = 0,
                                livenessScore = (float?)null, liveness = (float?)null,
                                livenessPass = false, livenessOk = false,
                                faceBox = (object)null
                            });
                        }

                        office = pick.Office;
                        radius = pick.RadiusMeters;
                        dist = pick.DistanceMeters;
                    }

                    path = SaveTemp(image, "k_");

                    int imgW = 0, imgH = 0;
                    try
                    {
                        using (var bmp = new System.Drawing.Bitmap(path))
                        {
                            imgW = bmp.Width;
                            imgH = bmp.Height;
                        }
                    }
                    catch { /* non-fatal — UI box will just be absent */ }

                    var dlib = new DlibBiometrics();
                    var faces = dlib.DetectFacesFromFile(path);
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
                            office = office == null ? null : new { id = office.Id, name = office.Name, radiusMeters = radius, distanceMeters = dist },
                            faceCount = count, count,
                            livenessScore = (float?)null, liveness = (float?)null,
                            livenessPass = false, livenessOk = false,
                            faceBox
                        });
                    }

                    var live = new OnnxLiveness();
                    var scored = live.ScoreFromFile(path, faces[0]);
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
                        office = office == null ? null : new { id = office.Id, name = office.Name, radiusMeters = radius, distanceMeters = dist },
                        faceCount = 1, count = 1,
                        livenessScore = p, liveness = p,
                        livenessPass = p >= th, livenessOk = p >= th,
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
                TryDelete(path);
            }
        }

        // FIX: Rate-limited — 10 requests per minute per IP.
        [HttpPost]
        [ValidateAntiForgeryToken]
        [RateLimit(Name = "KioskScanAttendance", MaxRequests = 10, WindowSeconds = 60)]
        public ActionResult ScanAttendance(
            double? lat, double? lon, double? accuracy, HttpPostedFileBase image)
        {
            if (image == null || image.ContentLength <= 0)
                return Json(new { ok = false, error = "NO_IMAGE" });

            var max = AppSettings.GetInt("Biometrics:MaxUploadBytes", 10 * 1024 * 1024);
            if (image.ContentLength > max)
                return Json(new { ok = false, error = "TOO_LARGE" });

            string path = null;
            try
            {
                using (var db = new FaceAttendDBEntities())
                {
                    Office office = null;
                    bool gpsRequired = IsGpsRequired();
                    bool locationVerified = false;
                    int requiredAcc = 0;

                    if (gpsRequired)
                    {
                        if (!lat.HasValue || !lon.HasValue)
                            return Json(new { ok = false, error = "GPS_REQUIRED" });

                        var pick = PickOffice(db, lat.Value, lon.Value, accuracy);
                        if (!pick.Allowed)
                            return Json(new { ok = false, error = pick.Reason });

                        office = pick.Office;
                        locationVerified = true;
                        requiredAcc = pick.RequiredAccuracy;
                    }
                    else
                    {
                        office = GetFallbackOffice(db);
                        locationVerified = false;
                    }

                    if (office == null)
                        return Json(new { ok = false, error = "NO_OFFICES" });

                    path = SaveTemp(image, "k_");

                    var dlib = new DlibBiometrics();
                    var faces = dlib.DetectFacesFromFile(path);
                    var count = faces == null ? 0 : faces.Length;
                    if (count == 0) return Json(new { ok = false, error = "NO_FACE" });
                    if (count > 1) return Json(new { ok = false, error = "MULTI_FACE" });

                    var live = new OnnxLiveness();
                    var scored = live.ScoreFromFile(path, faces[0]);
                    if (!scored.Ok)
                        return Json(new { ok = false, error = scored.Error });

                    var liveTh = (float)SystemConfigService.GetDouble(
                        db, "Biometrics:LivenessThreshold",
                        AppSettings.GetDouble("Biometrics:LivenessThreshold", 0.75));
                    var p = scored.Probability ?? 0f;
                    if (p < liveTh)
                        return Json(new { ok = false, error = "LIVENESS_FAIL", liveness = p, threshold = liveTh });

                    string encErr;
                    var vec = dlib.GetSingleFaceEncodingFromFile(path, out encErr);
                    if (vec == null)
                        return Json(new { ok = false, error = "ENCODING_FAIL", detail = encErr });

                    var tolFallback = AppSettings.GetDouble("Biometrics:DlibTolerance", 0.60);
                    var tol = SystemConfigService.GetDouble(db, "Biometrics:DlibTolerance",
                                  SystemConfigService.GetDouble(db, "DlibTolerance", tolFallback));

                    var entries = EmployeeFaceIndex.GetEntries(db);
                    if (entries == null || entries.Count == 0)
                        return Json(new { ok = false, error = "NO_ENROLLED_EMPLOYEES" });

                    EmployeeFaceIndex.Entry best = null;
                    double bestDist = double.PositiveInfinity;
                    foreach (var e in entries)
                    {
                        var d = DlibBiometrics.Distance(vec, e.Vec);
                        if (d < bestDist) { bestDist = d; best = e; }
                    }

                    if (best == null || bestDist > tol)
                        return Json(new { ok = false, error = "NO_MATCH", distance = bestDist, threshold = tol });

                    var emp = db.Employees.FirstOrDefault(x => x.EmployeeId == best.EmployeeId && x.IsActive);
                    if (emp == null)
                        return Json(new { ok = false, error = "EMPLOYEE_NOT_FOUND" });

                    var displayName = emp.LastName + ", " + emp.FirstName +
                                      (string.IsNullOrWhiteSpace(emp.MiddleName) ? "" : " " + emp.MiddleName);

                    double? similarity = tol > 0 ? Math.Max(0.0, Math.Min(1.0, 1.0 - (bestDist / tol))) : (double?)null;

                    var nearMatchRatio = SystemConfigService.GetDouble(db, "NeedsReview:NearMatchRatio", 0.90);
                    var livenessMargin = SystemConfigService.GetDouble(db, "NeedsReview:LivenessMargin", 0.03);
                    var gpsMargin = SystemConfigService.GetInt(db, "NeedsReview:GPSAccuracyMargin", 10);

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

                    // DB safety: truncate strings to match column sizes.
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
                    if (!rec.Ok)
                        return Json(new { ok = false, error = rec.Code, message = rec.Message });

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
                        distance = bestDist
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
                TryDelete(path);
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RateLimit(Name = "UnlockPin", MaxRequests = 5, WindowSeconds = 60)]
        public ActionResult UnlockPin(string pin, string returnUrl)
        {
            // FIX: sanitize returnUrl before echoing it back to the client.
            var safeReturn = AdminAuthorizeAttribute.SanitizeReturnUrl(returnUrl);
            var ip = Request.UserHostAddress;

            if (!AdminAuthorizeAttribute.VerifyPin(pin, ip))
                return Json(new { ok = false, error = "INVALID_PIN" });

            // Session-fixation hardening:
            //  1) Clear any existing auth marker on the current session.
            //  2) Rotate the Session ID cookie.
            //  3) Issue a one-time unlock cookie; the next request consumes it and
            //     marks the NEW session as authed.
            AdminAuthorizeAttribute.ClearAuthedMarker(Session);
            AdminAuthorizeAttribute.RotateSessionId(HttpContext);
            AdminAuthorizeAttribute.IssueUnlockCookie(HttpContext, ip);

            return Json(new { ok = true, returnUrl = safeReturn });
        }

        // FIX: Changed from [HttpGet] to [HttpPost] + CSRF token.
        // GET actions that change server state are vulnerable to CSRF.
        // The admin layout _AdminLayout.cshtml must be updated to POST to this action
        // (see the corresponding fix in that file).
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Lock()
        {
            AdminAuthorizeAttribute.ClearAuthed(Session);
            return RedirectToAction("Index");
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

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

        private OfficePick PickOffice(
            FaceAttendDBEntities db, double lat, double lon, double? accuracy)
        {
            int requiredAcc = SystemConfigService.GetInt(
                db, "Location:GPSAccuracyRequired",
                AppSettings.GetInt("Location:GPSAccuracyRequired", 50));

            if (accuracy.HasValue && accuracy.Value > requiredAcc)
                return new OfficePick { Allowed = false, Reason = "GPS_ACCURACY", RequiredAccuracy = requiredAcc };

            int defaultRadius = SystemConfigService.GetInt(
                db, "Location:GPSRadiusDefault",
                AppSettings.GetInt("Location:GPSRadiusDefault", 100));

            var offices = db.Offices.Where(o => o.IsActive).ToList();
            if (offices == null || offices.Count == 0)
                return new OfficePick { Allowed = false, Reason = "NO_OFFICES" };

            Office best = null;
            double bestDist = double.PositiveInfinity;
            int bestRadius = 0;

            foreach (var o in offices)
            {
                int radius = o.RadiusMeters > 0 ? o.RadiusMeters : defaultRadius;
                double d = GeoUtil.DistanceMeters(lat, lon, o.Latitude, o.Longitude);
                if (d <= radius && d < bestDist)
                {
                    best = o; bestDist = d; bestRadius = radius;
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

        /// <summary>
        /// Saves an uploaded image to a secure temp directory.
        ///
        /// FIX (Path Traversal): the file name is generated entirely from a GUID —
        /// the original filename from the client is never used in the path.
        /// The resolved path is then verified to be inside the expected directory
        /// as a defense-in-depth measure.
        /// </summary>
        private static string SaveTemp(HttpPostedFileBase image, string prefix = "t_")
        {
            var tmpRel = "~/App_Data/tmp";
            var tmp = HostingEnvironment.MapPath(tmpRel);
            if (string.IsNullOrWhiteSpace(tmp))
                throw new InvalidOperationException("TMP_DIR_NOT_FOUND");

            // Normalise the expected base path so Path.GetFullPath comparisons work.
            var expectedBase = Path.GetFullPath(tmp);
            Directory.CreateDirectory(expectedBase);

            // Determine extension from MIME type, not from the client-supplied filename.
            string ext = ".jpg";
            var ct = (image.ContentType ?? "").ToLowerInvariant().Trim();
            if (ct == "image/png") ext = ".png";
            // Any other MIME type is treated as JPEG — SaveAs will just write the bytes.

            var fileName = prefix + Guid.NewGuid().ToString("N") + ext;
            var fullPath = Path.Combine(expectedBase, fileName);

            // Verify the resolved path is strictly inside the temp directory.
            var resolvedPath = Path.GetFullPath(fullPath);
            if (!resolvedPath.StartsWith(
                    expectedBase + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("PATH_TRAVERSAL_DETECTED");
            }

            image.SaveAs(resolvedPath);
            return resolvedPath;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
                    System.IO.File.Delete(path);
            }
            catch { /* best effort */ }
        }
    }
}
