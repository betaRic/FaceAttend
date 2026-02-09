using System;
using System.Configuration;
using System.Security.Cryptography;
using System.Text;
using System.Web.Mvc;
using FaceAttend.Filters;
using FaceAttend.Models;
using FaceAttend.Services.Storage;
using Newtonsoft.Json;

namespace FaceAttend.Controllers
{
    [AdminAuthorize]
    public class AdminController : Controller
    {
        private readonly IEmployeeRepository _employees = new JsonEmployeeRepository();
        private readonly IVisitorRepository _visitors = new JsonVisitorRepository();

        // ----- UNLOCK -----

        [AllowAnonymous]
        [HttpGet]
        public ActionResult Unlock(string returnUrl)
        {
            ViewBag.ReturnUrl = string.IsNullOrWhiteSpace(returnUrl) ? "/Admin/Enrolled" : returnUrl;
            return View();
        }

        [AllowAnonymous]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Unlock(string pin, string returnUrl)
        {
            ViewBag.ReturnUrl = string.IsNullOrWhiteSpace(returnUrl) ? "/Admin/Enrolled" : returnUrl;

            if (!IsPinValid(pin))
            {
                ViewBag.Error = "Invalid PIN";
                return View();
            }

            Session["AdminUnlocked"] = true;
            Session["AdminUnlockedUtc"] = DateTime.UtcNow;

            return Redirect(ViewBag.ReturnUrl);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Logout()
        {
            Session["AdminUnlocked"] = false;
            Session.Remove("AdminUnlockedUtc");
            return RedirectToAction("Unlock", new { returnUrl = "/Admin/Enrolled" });
        }

        // ----- PAGES -----

        [HttpGet]
        public ActionResult Enrolled()
        {
            var list = _employees.GetAll();
            return View(list);
        }

        [HttpGet]
        public ActionResult Visitors()
        {
            var list = _visitors.GetAll();
            return View(list);
        }

        [HttpGet]
        public ActionResult ExportVisitorsJson()
        {
            var list = _visitors.GetAll();
            var json = JsonConvert.SerializeObject(list, Formatting.Indented);
            var bytes = Encoding.UTF8.GetBytes(json);

            var name = "visitors_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json";
            return File(bytes, "application/json", name);
        }

        // ----- ACTIONS -----

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult DeleteEnrollment(string employeeId)
        {
            if (string.IsNullOrWhiteSpace(employeeId))
                return Json(new { ok = false, error = "NO_EMPLOYEE_ID" });

            _employees.Delete(employeeId.Trim().ToUpperInvariant());
            return Json(new { ok = true });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult ReEnroll(string employeeId)
        {
            if (string.IsNullOrWhiteSpace(employeeId))
                return Json(new { ok = false, error = "NO_EMPLOYEE_ID" });

            var id = employeeId.Trim().ToUpperInvariant();
            _employees.Delete(id);

            // If you have a real enrollment wizard later, keep this route.
            return Redirect("/Admin/Enroll?employeeId=" + Uri.EscapeDataString(id));
        }

        [HttpGet]
        public ActionResult Enroll(string employeeId)
        {
            ViewBag.EmployeeId = (employeeId ?? "").Trim().ToUpperInvariant();
            return View();
        }

        // ----- PIN -----

        private static bool IsPinValid(string pin)
        {
            pin = (pin ?? "").Trim();
            if (pin.Length == 0) return false;

            var hashCfg = (ConfigurationManager.AppSettings["Admin:PinHash"] ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(hashCfg))
            {
                var got = Sha256Base64(pin);
                return FixedTimeEquals(got, hashCfg);
            }

            var plainCfg = (ConfigurationManager.AppSettings["Admin:Pin"] ?? "").Trim();
            if (string.IsNullOrWhiteSpace(plainCfg))
                return false;

            return string.Equals(pin, plainCfg, StringComparison.Ordinal);
        }

        private static string Sha256Base64(string s)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(s ?? "");
                var hash = sha.ComputeHash(bytes);
                return Convert.ToBase64String(hash);
            }
        }

        private static bool FixedTimeEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;

            var diff = 0;
            for (int i = 0; i < a.Length; i++)
                diff |= a[i] ^ b[i];

            return diff == 0;
        }
    }
}
