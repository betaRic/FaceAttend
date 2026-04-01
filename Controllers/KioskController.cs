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
            int? faceX, int? faceY, int? faceW, int? faceH,
            string deviceToken = null)
        {
            var requestedAtUtc = TimeZoneHelper.NowLocal();

            var activeScans = Interlocked.Increment(ref _activeScanCount);
            try
            {
                if (activeScans > GetMaxConcurrentScans())
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

                return new AttendanceScanService().Scan(
                    lat, lon, accuracy, image, clientFaceBox, requestedAtUtc,
                    includePerfTimings: ConfigurationService.GetBool("Kiosk:EnablePerfTimings", false),
                    deviceToken: deviceToken,
                    httpContext: HttpContext);
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
            scanId = (scanId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(scanId))
                return Json(new { ok = false, error = "SCAN_ID_REQUIRED", message = "Scan ID is required." });

            var item = VisitorScanService.Get(scanId);

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
    }
}
