using System;
using System.Web;
using System.Web.Mvc;

namespace FaceAttend.Controllers
{
    public class ErrorController : Controller
    {
        public ActionResult Index()
        {
            SetCommonErrorContext(500);
            return View("~/Views/Shared/Error.cshtml");
        }

        public ActionResult NotFound()
        {
            SetCommonErrorContext(404);
            return View("~/Views/Shared/Error.cshtml");
        }

        public ActionResult Forbidden()
        {
            SetCommonErrorContext(403);
            return View("~/Views/Shared/Error.cshtml");
        }

        private void SetCommonErrorContext(int statusCode)
        {
            Response.StatusCode = statusCode;
            Response.TrySkipIisCustomErrors = true;

            // Ensure a RequestId exists
            try
            {
                if (HttpContext.Items["RequestId"] == null)
                    HttpContext.Items["RequestId"] = Guid.NewGuid().ToString("N");
            }
            catch { }

#if DEBUG
            // Capture exception text for the debug panel
            try
            {
                var ex = Server.GetLastError();
                HttpContext.Items["LastError"] = ex?.ToString();
            }
            catch { }
#endif

            // Clear the error so MVC can render the view cleanly
            try
            {
                Server.ClearError();
            }
            catch { }
        }
    }
}