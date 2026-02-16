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
            ViewBag.ReturnUrl = returnUrl;
            ViewBag.UnlockHint = (unlock ?? 0) == 1;
            return View();
        }

        // Legacy endpoint (kept for debugging).
        [HttpGet]
        public ActionResult ActiveOffices()
        {
            using (var db = new FaceAttendDBEntities())
            {
                var list = db.Offices
                    .Where(o => o.IsActive)
                    .OrderBy(o => o.Name)
                    .Select(o => new { id = o.Id, name = o.Name, radius = o.RadiusMeters, lat = o.Latitude, lon = o.Longitude })
                    .ToList();

                return Json(new { ok = true, offices = list }, JsonRequestBehavior.AllowGet);
            }
        }

        // New: Resolve office automatically based on GPS.
        // GPS enforcement is active only on phones/tablets.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult ResolveOffice(double lat, double lon, double? accuracy)
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
                        office = fallback == null ? null : new
                        {
                            id = fallback.Id,
                            name = fallback.Name,
                            radiusMeters = fallback.RadiusMeters
                        }
                    });
                }

                var pick = PickOffice(db, lat, lon, accuracy);
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

        // New: Lightweight scan for kiosk (face count + liveness).
        // This is used for hands-free scanning before calling ScanAttendance.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult ScanFrame(double? lat, double? lon, double? accuracy, HttpPostedFileBase image)
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
                    // GPS gate (mobile/tablet only)
                    Office office = null;
                    int radius = 0;
                    double dist = 0;

                    if (IsGpsRequired())
                    {
                        if (!lat.HasValue || !lon.HasValue)
                        {
                            return Json(new
                            {
                                ok = true,
                                gpsRequired = true,
                                allowed = false,
                                reason = "GPS_REQUIRED"
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

                        office = pick.Office;
                        radius = pick.RadiusMeters;
                        dist = pick.DistanceMeters;
                    }

                    path = SaveTemp(image);

                    var dlib = new DlibBiometrics();
                    var faces = dlib.DetectFacesFromFile(path);
                    var count = faces == null ? 0 : faces.Length;

                    if (count != 1)
                    {
                        return Json(new
                        {
                            ok = true,
                            gpsRequired = IsGpsRequired(),
                            allowed = IsGpsRequired() ? true : true,
                            office = office == null ? null : new { id = office.Id, name = office.Name, radiusMeters = radius, distanceMeters = dist },
                            count = count,
                            liveness = (float?)null,
                            livenessOk = false
                        });
                    }

                    var live = new OnnxLiveness();
                    var scored = live.ScoreFromFile(path, faces[0]);
                    if (!scored.Ok)
                        return Json(new { ok = false, error = scored.Error, count = 1 });

                    var th = (float)AppSettings.GetDouble("Biometrics:LivenessThreshold", 0.75);
                    var p = scored.Probability ?? 0f;

                    return Json(new
                    {
                        ok = true,
                        gpsRequired = IsGpsRequired(),
                        allowed = true,
                        office = office == null ? null : new { id = office.Id, name = office.Name, radiusMeters = radius, distanceMeters = dist },
                        count = 1,
                        liveness = p,
                        livenessOk = p >= th
                    });
                }
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, error = "SCAN_ERROR", detail = ex.Message });
            }
            finally
            {
                TryDelete(path);
            }
        }

        // Single shot attendance scan:
        // - (mobile/tablet) validates GPS and picks office automatically
        // - ensures 1 face
        // - runs liveness
        // - runs face match
        // - records AttendanceLog
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult ScanAttendance(double? lat, double? lon, double? accuracy, HttpPostedFileBase image)
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

                    if (gpsRequired)
                    {
                        if (!lat.HasValue || !lon.HasValue)
                            return Json(new { ok = false, error = "GPS_REQUIRED" });

                        var pick = PickOffice(db, lat.Value, lon.Value, accuracy);
                        if (!pick.Allowed)
                            return Json(new { ok = false, error = pick.Reason });

                        office = pick.Office;
                        locationVerified = true;
                    }
                    else
                    {
                        // Desktop/laptop: skip GPS, but still assign an office for logs.
                        office = GetFallbackOffice(db);
                        locationVerified = false;
                    }

                    if (office == null)
                        return Json(new { ok = false, error = "NO_OFFICES" });

                    path = SaveTemp(image);

                    // Face count
                    var dlib = new DlibBiometrics();
                    var faces = dlib.DetectFacesFromFile(path);
                    var count = faces == null ? 0 : faces.Length;
                    if (count == 0) return Json(new { ok = false, error = "NO_FACE" });
                    if (count > 1) return Json(new { ok = false, error = "MULTI_FACE" });

                    // Liveness
                    var live = new OnnxLiveness();
                    var scored = live.ScoreFromFile(path, faces[0]);
                    if (!scored.Ok)
                        return Json(new { ok = false, error = scored.Error });

                    var liveTh = (float)AppSettings.GetDouble("Biometrics:LivenessThreshold", 0.75);
                    var p = scored.Probability ?? 0f;
                    if (p < liveTh)
                        return Json(new { ok = false, error = "LIVENESS_FAIL", liveness = p, threshold = liveTh });

                    // Encoding
                    string encErr;
                    var vec = dlib.GetSingleFaceEncodingFromFile(path, out encErr);
                    if (vec == null)
                        return Json(new { ok = false, error = "ENCODING_FAIL", detail = encErr });

                    // Match
                    var tolFallback = AppSettings.GetDouble("Biometrics:DlibTolerance", 0.60);
                    var tol = SystemConfigService.GetDouble(db, "DlibTolerance", tolFallback);

                    var entries = EmployeeFaceIndex.GetEntries(db);
                    if (entries == null || entries.Count == 0)
                        return Json(new { ok = false, error = "NO_ENROLLED_EMPLOYEES" });

                    EmployeeFaceIndex.Entry best = null;
                    double bestDist = double.PositiveInfinity;

                    foreach (var e in entries)
                    {
                        var d = DlibBiometrics.Distance(vec, e.Vec);
                        if (d < bestDist)
                        {
                            bestDist = d;
                            best = e;
                        }
                    }

                    if (best == null || bestDist > tol)
                        return Json(new { ok = false, error = "NO_MATCH", distance = bestDist, threshold = tol });

                    var emp = db.Employees.FirstOrDefault(x => x.EmployeeId == best.EmployeeId && x.IsActive);
                    if (emp == null)
                        return Json(new { ok = false, error = "EMPLOYEE_NOT_FOUND" });

                    var displayName = emp.LastName + ", " + emp.FirstName + (string.IsNullOrWhiteSpace(emp.MiddleName) ? "" : " " + emp.MiddleName);
                    var similarity = tol > 0 ? Math.Max(0.0, Math.Min(1.0, 1.0 - (bestDist / tol))) : (double?)null;

                    // Record
                    var log = new AttendanceLog
                    {
                        EmployeeId = emp.Id,
                        OfficeId = office.Id,
                        EmployeeFullName = displayName,
                        Department = emp.Department,
                        OfficeType = office.Type,
                        OfficeName = office.Name,

                        GPSLatitude = lat,
                        GPSLongitude = lon,
                        GPSAccuracy = accuracy,
                        LocationVerified = locationVerified,

                        FaceDistance = bestDist,
                        FaceSimilarity = similarity,
                        MatchThreshold = tol,
                        LivenessScore = p,
                        LivenessResult = "PASS",

                        ClientIP = (Request.UserHostAddress ?? ""),
                        UserAgent = (Request.UserAgent ?? ""),

                        NeedsReview = false
                    };

                    var rec = AttendanceService.Record(db, log);
                    if (!rec.Ok)
                        return Json(new { ok = false, error = rec.Code, message = rec.Message });

                    return Json(new
                    {
                        ok = true,
                        employeeId = emp.EmployeeId,
                        name = displayName,
                        eventType = rec.EventType,
                        message = rec.Message,
                        liveness = p,
                        distance = bestDist
                    });
                }
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, error = "SCAN_ERROR", detail = ex.Message });
            }
            finally
            {
                TryDelete(path);
            }
        }

        // Called by Kiosk hotkey modal.
        // Sets the same session marker used by AdminAuthorizeAttribute.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult UnlockPin(string pin, string returnUrl)
        {
            if (!AdminAuthorizeAttribute.VerifyPin(pin))
                return Json(new { ok = false, error = "INVALID_PIN" });

            AdminAuthorizeAttribute.MarkAuthed(Session);
            return Json(new { ok = true, returnUrl = (returnUrl ?? "").Trim() });
        }

        // Used by the admin layout "Lock" link.
        [HttpGet]
        public ActionResult Lock()
        {
            AdminAuthorizeAttribute.ClearAuthed(Session);
            return RedirectToAction("Index");
        }

        // --- Helpers ---

        private class OfficePick
        {
            public bool Allowed { get; set; }
            public string Reason { get; set; }
            public Office Office { get; set; }
            public double DistanceMeters { get; set; }
            public int RadiusMeters { get; set; }
            public int RequiredAccuracy { get; set; }
        }

        private bool IsGpsRequired()
        {
            if (Request != null && Request.Browser != null && Request.Browser.IsMobileDevice)
                return true;

            var ua = (Request != null ? (Request.UserAgent ?? "") : "").ToLowerInvariant();
            if (ua.Contains("android") || ua.Contains("iphone") || ua.Contains("ipad") || ua.Contains("ipod"))
                return true;
            if (ua.Contains("tablet"))
                return true;

            return false;
        }

        private Office GetFallbackOffice(FaceAttendDBEntities db)
        {
            int preferred = AppSettings.GetInt("Kiosk:FallbackOfficeId", 0);
            if (preferred > 0)
            {
                var chosen = db.Offices.FirstOrDefault(o => o.Id == preferred && o.IsActive);
                if (chosen != null) return chosen;
            }

            return db.Offices.Where(o => o.IsActive).OrderBy(o => o.Name).FirstOrDefault();
        }

        private OfficePick PickOffice(FaceAttendDBEntities db, double lat, double lon, double? accuracy)
        {
            int requiredAcc = AppSettings.GetInt("Location:GPSAccuracyRequired", 50);

            if (accuracy.HasValue && accuracy.Value > requiredAcc)
            {
                return new OfficePick
                {
                    Allowed = false,
                    Reason = "GPS_ACCURACY",
                    RequiredAccuracy = requiredAcc
                };
            }

            int defaultRadius = AppSettings.GetInt("Location:GPSRadiusDefault", 100);

            var offices = db.Offices.Where(o => o.IsActive).ToList();
            if (offices == null || offices.Count == 0)
            {
                return new OfficePick
                {
                    Allowed = false,
                    Reason = "NO_OFFICES"
                };
            }

            Office best = null;
            double bestDist = double.PositiveInfinity;
            int bestRadius = 0;

            foreach (var o in offices)
            {
                int radius = o.RadiusMeters > 0 ? o.RadiusMeters : defaultRadius;
                double dist = GeoUtil.DistanceMeters(lat, lon, o.Latitude, o.Longitude);

                if (dist <= radius && dist < bestDist)
                {
                    best = o;
                    bestDist = dist;
                    bestRadius = radius;
                }
            }

            if (best == null)
            {
                return new OfficePick
                {
                    Allowed = false,
                    Reason = "NO_OFFICE_NEARBY",
                    RequiredAccuracy = requiredAcc
                };
            }

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

        private static string SaveTemp(HttpPostedFileBase image)
        {
            var tmpRel = "~/App_Data/tmp";
            var tmp = HostingEnvironment.MapPath(tmpRel);
            if (string.IsNullOrWhiteSpace(tmp))
                throw new InvalidOperationException("TMP_DIR_NOT_FOUND");

            Directory.CreateDirectory(tmp);

            var ext = ".jpg";
            var name = (image.FileName ?? "").ToLowerInvariant();
            if (name.EndsWith(".png")) ext = ".png";

            var file = Path.Combine(tmp, "k_" + Guid.NewGuid().ToString("N") + ext);
            image.SaveAs(file);
            return file;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
                    System.IO.File.Delete(path);
            }
            catch { }
        }
    }
}
