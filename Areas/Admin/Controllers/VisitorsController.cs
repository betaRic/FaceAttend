using System.Web.Mvc;
using FaceAttend.Filters;

namespace FaceAttend.Areas.Admin.Controllers
{
    [AdminAuthorize]
    public class VisitorsController : Controller
    {
        public ActionResult Index()
        {
            ViewBag.Title = "Visitors";
            return View();
        }
    }
}
