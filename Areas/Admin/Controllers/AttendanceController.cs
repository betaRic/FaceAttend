using System.Web.Mvc;
using FaceAttend.Filters;

namespace FaceAttend.Areas.Admin.Controllers
{
    [AdminAuthorize]
    public class AttendanceController : Controller
    {
        public ActionResult Index()
        {
            ViewBag.Title = "Attendance";
            return View();
        }
    }
}
