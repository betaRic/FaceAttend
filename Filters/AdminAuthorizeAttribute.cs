using System;
using System.Configuration;
using System.Web;
using System.Web.Mvc;

namespace FaceAttend.Filters
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public sealed class AdminAuthorizeAttribute : AuthorizeAttribute
    {
        public override void OnAuthorization(AuthorizationContext filterContext)
        {
            if (filterContext == null) throw new ArgumentNullException(nameof(filterContext));

            // Allow Unlock actions
            if (IsAllowAnonymous(filterContext))
                return;

            var ctx = filterContext.HttpContext;
            if (ctx == null || ctx.Session == null)
            {
                Deny(filterContext);
                return;
            }

            var unlockedObj = ctx.Session["AdminUnlocked"];
            var unlocked = unlockedObj is bool b && b;

            var unlockedUtcObj = ctx.Session["AdminUnlockedUtc"];
            var unlockedUtc = unlockedUtcObj is DateTime dt ? (DateTime?)dt : null;

            if (unlocked && unlockedUtc.HasValue)
            {
                var minutes = 30;
                var s = ConfigurationManager.AppSettings["Admin:SessionMinutes"];
                if (int.TryParse(s, out var m) && m > 0) minutes = m;

                if ((DateTime.UtcNow - unlockedUtc.Value) <= TimeSpan.FromMinutes(minutes)) { ctx.Session["AdminUnlockedUtc"] = DateTime.UtcNow; return; }
            }

            Deny(filterContext);
        }

        private static void Deny(AuthorizationContext filterContext)
        {
            var req = filterContext.HttpContext.Request;
            var returnUrl = req != null ? req.RawUrl : "/Admin/Enrolled";
            var url = "/Admin/Unlock?returnUrl=" + HttpUtility.UrlEncode(returnUrl);
            filterContext.Result = new RedirectResult(url);
        }

        private static bool IsAllowAnonymous(AuthorizationContext filterContext)
        {
            return filterContext.ActionDescriptor.IsDefined(typeof(AllowAnonymousAttribute), true)
                   || filterContext.ActionDescriptor.ControllerDescriptor.IsDefined(typeof(AllowAnonymousAttribute), true);
        }
    }
}

