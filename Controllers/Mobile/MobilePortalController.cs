using System;
using System.Data.Entity;
using System.Linq;
using System.Web.Mvc;
using FaceAttend.Models.ViewModels.Mobile;
using FaceAttend.Services;
using FaceAttend.Services.Helpers;
using FaceAttend.Services.Mobile;

namespace FaceAttend.Controllers.Mobile
{
    [RoutePrefix("MobileRegistration")]
    public class MobilePortalController : Controller
    {
        [HttpGet]
        [Route("")]
        public ActionResult Index()
        {
            if (!DeviceService.IsMobileDevice(Request))
                return RedirectToRoute(new { controller = "Kiosk", action = "Index", area = "" });

            ViewBag.DeviceFingerprint = DeviceService.GenerateFingerprint(Request);
            ViewBag.IsMobile = true;

            return View("~/Views/MobileRegistration/Index.cshtml");
        }

        [HttpGet]
        [Route("Success")]
        public ActionResult Success(bool isNewEmployee = false, int? employeeDbId = null)
        {
            ViewBag.IsNewEmployee = isNewEmployee;
            ViewBag.EmployeeDbId = employeeDbId;
            return View("~/Views/MobileRegistration/Success.cshtml");
        }

        [HttpGet]
        [Route("CheckStatus")]
        public ActionResult CheckStatus(int employeeDbId)
        {
            using (var db = new FaceAttendDBEntities())
            {
                var employee = db.Employees.Find(employeeDbId);
                if (employee == null)
                    return JsonResponseBuilder.NotFound("Enrollment");

                var status = DeviceService.GetEmployeeStatus(db, employee.Id);
                string message = status == "PENDING" ? "Waiting for admin approval..." :
                                status == "ACTIVE" ? "Approved! You can now use the system." :
                                "This enrollment is inactive. Please contact the admin.";

                return JsonResponseBuilder.Success(new
                {
                    status = status,
                    isApproved = status == "ACTIVE",
                    message
                });
            }
        }

        [HttpGet]
        [Route("Employee")]
        public ActionResult Employee()
        {
            return RedirectToRoute(new { controller = "Attendance", action = "MyMonth", area = "" });
        }

        [HttpGet]
        [Route("ExportAttendance")]
        public ActionResult ExportAttendance()
        {
            return RedirectToRoute(new { controller = "Attendance", action = "ExportMyMonth", area = "" });
        }

    }
}
