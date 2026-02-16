using System.Web.Mvc;
using FaceAttend.Filters;

namespace FaceAttend.Areas.Admin.Controllers
{
    [AdminAuthorize]
    public class DashboardController : Controller
    {
        public ActionResult Index()
        {
            ViewBag.Title = "Dashboard";
            return View();
        }
    }
}
