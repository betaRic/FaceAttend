using System;
using System.Linq;
using System.Data.Entity;
using System.Web.Mvc;
using FaceAttend.Services;
using static FaceAttend.Services.DeviceService;

namespace FaceAttend.Controllers
{
    /// <summary>
    /// DEVICE REGISTRATION
    /// Partial class for KioskController - device registration for existing employees
    /// </summary>
    public partial class KioskController
    {
        /// <summary>
        /// Register a device for an existing employee (called from kiosk when in personal device mode)
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult RegisterDevice(int employeeId, string deviceName)
        {
            var fingerprint = DeviceService.GenerateFingerprint(Request);
            
            using (var db = new FaceAttendDBEntities())
            {
                var result = DeviceService.RegisterDevice(
                    db, employeeId, fingerprint, deviceName, Request.UserHostAddress);
                
                return Json(new
                {
                    ok = result.Success,
                    deviceId = result.Data,
                    error = result.ErrorCode,
                    message = result.Message
                });
            }
        }



        /// <summary>
        /// Resolve current personal-phone device state for kiosk idle gating.
        /// This only applies to real mobile phones. Tablets, kiosks, and desktops bypass this gate.
        /// ENHANCED: Uses Sec-CH-UA-Mobile to prevent "Desktop site" bypass
        /// </summary>
        [HttpGet]
        public ActionResult GetCurrentMobileDeviceState()
        {
            // Sec-CH-UA-Mobile overrides cookie - if hardware is mobile, NEVER treat as kiosk
            var kioskCookie = Request.Cookies["ForceKioskMode"];
            var chUaMobile  = Request.Headers["Sec-CH-UA-Mobile"];
            bool isHardwareMobile = (chUaMobile == "?1");
            
            // If hardware is mobile, NEVER treat as kiosk even if cookie says so
            var forceKiosk = !isHardwareMobile && (
                (kioskCookie != null && kioskCookie.Value == "true") ||
                (Request.Headers["X-Kiosk-Mode"] == "true")
            );
            
            var isMobile = DeviceService.IsMobileDevice(Request);

            if (!isMobile || forceKiosk)
            {
                return Json(new
                {
                    ok = true,
                    deviceStatus = "ACTIVE",
                    gateApplies = false
                }, JsonRequestBehavior.AllowGet);
            }

            var fingerprint = DeviceService.GenerateFingerprint(Request);
            var deviceToken = DeviceService.GetDeviceTokenFromCookie(Request);

            using (var db = new FaceAttendDBEntities())
            {
                Device device = null;

                if (!string.IsNullOrWhiteSpace(deviceToken))
                {
                    device = db.Devices
                        .Include("Employee")
                        .FirstOrDefault(d => d.DeviceToken == deviceToken);

                    if (device?.TokenExpiresAt != null && device.TokenExpiresAt < DateTime.UtcNow)
                    {
                        device = null;
                    }
                }

                if (device == null && !string.IsNullOrWhiteSpace(fingerprint))
                {
                    device = db.Devices
                        .Include("Employee")
                        .FirstOrDefault(d => d.Fingerprint == fingerprint);
                }

                if (device == null)
                {
                    return Json(new
                    {
                        ok = true,
                        deviceStatus = "NOT_REGISTERED",
                        gateApplies = true
                    }, JsonRequestBehavior.AllowGet);
                }

                var status = (device.Status ?? "NOT_REGISTERED").Trim().ToUpperInvariant();
                if (status != "ACTIVE" && status != "PENDING" && status != "BLOCKED")
                {
                    status = "NOT_REGISTERED";
                }

                return Json(new
                {
                    ok = true,
                    deviceStatus = status,
                    gateApplies = true,
                    employeeId = device.Employee?.EmployeeId,
                    employeeName = device.Employee != null
                        ? $"{device.Employee.FirstName} {device.Employee.LastName}"
                        : null
                }, JsonRequestBehavior.AllowGet);
            }
        }

        /// <summary>
        /// Check if current device is already registered
        /// </summary>
        [HttpGet]
        public ActionResult CheckDeviceStatus()
        {
            var fingerprint = DeviceService.GenerateFingerprint(Request);
            
            using (var db = new FaceAttendDBEntities())
            {
                var device = db.Devices.FirstOrDefault(d => d.Fingerprint == fingerprint);
                
                if (device == null)
                {
                    return Json(new { registered = false }, JsonRequestBehavior.AllowGet);
                }

                return Json(new
                {
                    registered = true,
                    status = device.Status,
                    employeeId = device.Employee?.EmployeeId,
                    employeeName = device.Employee != null 
                        ? $"{device.Employee.FirstName} {device.Employee.LastName}" 
                        : null,
                    isActive = device.Status == "ACTIVE"
                }, JsonRequestBehavior.AllowGet);
            }
        }
    }
}
