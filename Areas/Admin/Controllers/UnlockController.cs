using System.Web.Mvc;
using FaceAttend.Filters;

namespace FaceAttend.Areas.Admin.Controllers
{
    public class UnlockController : Controller
    {
        [HttpGet]
        public ActionResult Index(string returnUrl)
        {
            // Unlock UI lives in Kiosk.
            return Redirect("/Kiosk?unlock=1&returnUrl=" + System.Web.HttpUtility.UrlEncode(returnUrl ?? "/Admin"));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Index(string pin, string returnUrl)
        {
            // Keep POST endpoint as a safe redirect.
            return Redirect("/Kiosk?unlock=1&returnUrl=" + System.Web.HttpUtility.UrlEncode(returnUrl ?? "/Admin"));
        }
    }
}
