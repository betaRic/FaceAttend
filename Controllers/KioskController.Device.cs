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
