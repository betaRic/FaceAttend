using System.Web.Mvc;
using FaceAttend.Filters;

namespace FaceAttend.Areas.Admin.Controllers
{
    public class UnlockController : Controller
    {
        [HttpGet]
        public ActionResult Index(string returnUrl)
        {
            // FIX (Open Redirect): sanitize returnUrl before embedding in redirect.
            var safe = AdminAuthorizeAttribute.SanitizeReturnUrl(returnUrl);
            return Redirect("/Kiosk?unlock=1&returnUrl=" + System.Web.HttpUtility.UrlEncode(safe));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Index(string pin, string returnUrl)
        {
            // FIX (Open Redirect): sanitize returnUrl before embedding in redirect.
            var safe = AdminAuthorizeAttribute.SanitizeReturnUrl(returnUrl);
            return Redirect("/Kiosk?unlock=1&returnUrl=" + System.Web.HttpUtility.UrlEncode(safe));
        }
    }
}
