using System;
using System.Web.Mvc;

namespace FaceAttend.Areas.Admin.Controllers
{
    /// <summary>
    /// Admin error pages.
    /// Intentionally does NOT use AdminAuthorize so error pages remain accessible.
    /// </summary>
    public class ErrorController : Controller
    {
        [OutputCache(Duration = 0, NoStore = true, VaryByParam = "*")]
        public ActionResult Index()
        {
            return Build(500, "Something went wrong", "We couldn't process your request.");
        }

        [OutputCache(Duration = 0, NoStore = true, VaryByParam = "*")]
        public ActionResult NotFound()
        {
            return Build(404, "Page not found", "The page you requested does not exist.");
        }

        [OutputCache(Duration = 0, NoStore = true, VaryByParam = "*")]
        public ActionResult Forbidden()
        {
            return Build(403, "Access denied", "You do not have permission to view this page.");
        }

        [OutputCache(Duration = 0, NoStore = true, VaryByParam = "*")]
        public ActionResult BadRequest()
        {
            return Build(400, "Bad request", "The request was not valid.");
        }

        [OutputCache(Duration = 0, NoStore = true, VaryByParam = "*")]
        public ActionResult TooManyRequests()
        {
            return Build(429, "Too many requests", "Please wait a moment and try again.");
        }

        [OutputCache(Duration = 0, NoStore = true, VaryByParam = "*")]
        public ActionResult Unavailable()
        {
            return Build(503, "Service unavailable", "Please try again later.");
        }

        private ActionResult Build(int statusCode, string title, string message)
        {
            Response.StatusCode = statusCode;
            Response.TrySkipIisCustomErrors = true;

            ViewBag.StatusCode = statusCode;
            ViewBag.TitleText = title;
            ViewBag.MessageText = message;
            ViewBag.RequestId = GetRequestId();

            return View("ErrorPage");
        }

        private string GetRequestId()
        {
            try
            {
                var id = HttpContext?.TraceIdentifier;
                if (!string.IsNullOrWhiteSpace(id)) return id;
            }
            catch { }

            return Guid.NewGuid().ToString("N");
        }
    }
}
