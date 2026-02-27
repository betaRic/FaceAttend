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
            var kioskUrl = Url.Action("Index", "Kiosk", new { area = "", unlock = 1, returnUrl = safe });
            return Redirect(kioskUrl);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Index(string pin, string returnUrl)
        {
            // FIX (Open Redirect): sanitize returnUrl before embedding in redirect.
            var safe = AdminAuthorizeAttribute.SanitizeReturnUrl(returnUrl);
            var kioskUrl = Url.Action("Index", "Kiosk", new { area = "", unlock = 1, returnUrl = safe });
            return Redirect(kioskUrl);
        }
    }
}
