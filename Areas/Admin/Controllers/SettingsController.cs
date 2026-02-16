using System.Web.Mvc;
using FaceAttend.Filters;

namespace FaceAttend.Areas.Admin.Controllers
{
    [AdminAuthorize]
    public class SettingsController : Controller
    {
        public ActionResult Index()
        {
            ViewBag.Title = "Settings";
            return View();
        }
    }
}
