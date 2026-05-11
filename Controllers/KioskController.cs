using System;
using System.Linq;
using System.Threading;
using System.Web.Mvc;
using FaceAttend.Filters;
using FaceAttend.Services;
using static FaceAttend.Services.DeviceService;
using FaceAttend.Services.Biometrics;
using FaceAttend.Services.Recognition;
using FaceAttend.Services.Security;
using static FaceAttend.Services.OfficeLocationService;
using System.Web;

namespace FaceAttend.Controllers
{
    public partial class KioskController : Controller
    {
        private static int _activeScanCount;

        private static int GetMaxConcurrentScans()
        {
            var v = ConfigurationService.GetInt("Kiosk:MaxConcurrentScans", 16);
            return v < 1 ? 1 : v;
        }

        // ── Actions ───────────────────────────────────────────────────────────

        [HttpGet]
        public ActionResult Index(string returnUrl, int? unlock)
        {
            ViewBag.ReturnUrl   = AdminAuthorizeAttribute.SanitizeReturnUrl(returnUrl);
            ViewBag.UnlockHint  = (unlock ?? 0) == 1;
            ViewBag.AllowUnlock = !DeviceService.IsMobileDevice(Request);

            using (var db = new FaceAttendDBEntities())
            {
                var today = TimeZoneHelper.NowLocal().Date;
                ViewBag.WfhDay = OfficeScheduleService.IsWfhPossibleAnywhere(db, today);
            }

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

                    var fallback = GetFallbackOffice(db);
                    return JsonResponseBuilder.OfficeResolved(
                        allowed: true, gpsRequired: false,
                        officeId: fallback?.Id, officeName: fallback?.Name);
                }

                var pick = PickOffice(db, lat.Value, lon.Value, accuracy);
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
            System.Web.HttpPostedFileBase image,
            bool wfhMode = false)
        {
            var requestedAtLocal = TimeZoneHelper.NowLocal();
            var scanSw = System.Diagnostics.Stopwatch.StartNew();

            var activeScans = Interlocked.Increment(ref _activeScanCount);
            try
            {
                if (activeScans > GetMaxConcurrentScans())
                {
                    OperationalMetricsService.RecordBusy();
                    Response.StatusCode = 503;
                    Response.AddHeader("Retry-After", "2");
                    var busy = JsonResponseBuilder.SystemBusy(2);
                    PublicAuditService.RecordScan(Request, busy, "KIOSK", scanSw.ElapsedMilliseconds);
                    return busy;
                }

                var result = new AttendanceScanService().Scan(
                    lat, lon, accuracy, image, requestedAtLocal,
                    includePerfTimings: ConfigurationService.GetBool("Kiosk:EnablePerfTimings", false),
                    httpContext: HttpContext,
                    wfhMode: wfhMode);
                OperationalMetricsService.RecordScan(scanSw.ElapsedMilliseconds, result);
                PublicAuditService.RecordScan(Request, result, "KIOSK", scanSw.ElapsedMilliseconds);
                return result;
            }
            finally
            {
                Interlocked.Decrement(ref _activeScanCount);
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RateLimit(Name = "KioskSubmitVisitor", MaxRequests = 30, WindowSeconds = 60, Burst = 10)]
        public ActionResult SubmitVisitor(string scanId, string name, string purpose)
        {
            if (!ConfigurationService.GetBool("Kiosk:VisitorEnabled", false))
                return Json(new { ok = false, error = "VISITOR_DISABLED", message = "Visitor registration is disabled." });

            scanId = (scanId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(scanId))
                return Json(new { ok = false, error = "SCAN_ID_REQUIRED", message = "Scan ID is required." });

            var item = VisitorScanService.Get(scanId);

            if (item == null || !FaceVectorCodec.IsValidVector(item.Vec))
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

                        var nowLocal = TimeZoneHelper.NowLocal();
                        var bytes = FaceVectorCodec.EncodeToBytes(item.Vec);
                        var b64   = BiometricCrypto.ProtectBase64Bytes(bytes);

                        if (string.IsNullOrWhiteSpace(b64))
                            return Json(new { ok = false, error = "ENCODE_ERROR", message = "Could not save face." });

                        var v = new Visitor
                        {
                            Name               = name,
                            FaceEncodingBase64 = b64,
                            VisitCount         = 0,
                            FirstVisitDate     = nowLocal,
                            LastVisitDate      = nowLocal,
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
                    VisitorScanService.Remove(scanId);
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
            {
                // Audit failed login attempt
                using (var db = new FaceAttendDBEntities())
                {
                    AuditHelper.LogLogin(db, ip, false, "PIN verification failed");
                }
                return Json(new { ok = false, error = "INVALID_PIN" });
            }

            // Audit successful login
            using (var db = new FaceAttendDBEntities())
            {
                AuditHelper.LogLogin(db, ip, true);
            }

            // Mark the current session directly — most reliable path.
            // RotateSessionId + cookie bridge was causing silent failures because
            // MachineKey.Unprotect on the unlock cookie failed after the session rotated.
            AdminAuthorizeAttribute.MarkAuthed(Session);
            // Issue long-lived bypass cookie so the admin can skip PIN for the rest of the workday
            AdminPersistCookieService.IssuePersistCookie(new HttpContextWrapper(System.Web.HttpContext.Current));

            return Json(new { ok = true, returnUrl = safeReturn });
        }

        /// <summary>
        /// Returns admin to the kiosk WITHOUT clearing the session or persist cookie,
        /// so the shortcut can bypass PIN on the next admin visit.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Lock()
        {
            // Do NOT abandon session — admin can return via shortcut without re-entering PIN.
            // Use FullLock() to explicitly sign out at end of day.
            return RedirectToAction("Index");
        }

        /// <summary>
        /// Full sign-out: clears the admin session AND the workday bypass cookie.
        /// Use this at end of day or when handing the kiosk to a different admin.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult FullLock()
        {
            // Audit logout before clearing
            var ip = Request.UserHostAddress;
            using (var db = new FaceAttendDBEntities())
            {
                AuditHelper.LogLogout(db, ip);
            }

            AdminAuthorizeAttribute.ClearAuthed(Session);
            AdminPersistCookieService.ExpirePersistCookie(new HttpContextWrapper(System.Web.HttpContext.Current));
            return RedirectToAction("Index");
        }

        /// <summary>
        /// Kiosk shortcut pre-check: returns whether the current session is already admin-authed.
        /// No CSRF needed — read-only, no side effects.
        /// </summary>
        [HttpGet]
        public ActionResult CheckAdminAuthed()
        {
            if (DeviceService.IsMobileDevice(Request))
                return Json(new { authed = false }, JsonRequestBehavior.AllowGet);
            return Json(new { authed = AdminSessionService.IsAuthed(Session) },
                        JsonRequestBehavior.AllowGet);
        }

        /// <summary>
        /// Kiosk shortcut bypass: if the workday persist cookie is valid, issues a new
        /// unlock cookie so the admin can enter the panel without re-entering PIN.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult AutoAdmin(string returnUrl)
        {
            if (DeviceService.IsMobileDevice(Request))
                return Json(new { ok = false, error = "UNLOCK_DISABLED_ON_MOBILE" });

            var ctx = new HttpContextWrapper(System.Web.HttpContext.Current);
            if (!AdminPersistCookieService.IsValid(ctx))
                return Json(new { ok = false });

            var safeReturn = AdminAuthorizeAttribute.SanitizeReturnUrl(returnUrl);
            // Mark the session directly — same fix as UnlockPin.
            AdminAuthorizeAttribute.MarkAuthed(Session);

            return Json(new { ok = true, returnUrl = safeReturn });
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
    }
}
