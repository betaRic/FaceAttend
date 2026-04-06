using System.Web;
using System.Web.Mvc;
using FaceAttend.Services;
using FaceAttend.Services.Helpers;
using FaceAttend.Services.Security;

namespace FaceAttend.Filters
{
    /// <summary>
    /// Session-based admin authorization filter.
    /// All logic is delegated to AdminSessionService, AdminPinService, and AdminUnlockCookieService.
    /// </summary>
    public class AdminAuthorizeAttribute : AuthorizeAttribute
    {
        protected override bool AuthorizeCore(HttpContextBase httpContext)
        {
            try
            {
                if (httpContext == null) return false;

                System.Diagnostics.Trace.TraceInformation(
                    "[AdminAuth] AuthorizeCore: " + (httpContext.Request?.RawUrl ?? "unknown"));

                var clientIp = StringHelper.NormalizeIp(httpContext.Request?.UserHostAddress);
                if (!AdminAccessControl.IsAllowed(clientIp))
                {
                    httpContext.Items["AdminBlockReason"] = "IP_NOT_ALLOWED";
                    System.Diagnostics.Trace.TraceWarning("[AdminAuth] IP not allowed: " + clientIp);
                    return false;
                }

                if (AdminSessionService.IsAuthed(httpContext.Session))
                    return true;

                // No valid session — try the one-time unlock cookie.
                System.Diagnostics.Trace.TraceInformation("[AdminAuth] No active session, trying unlock cookie");
                return AdminUnlockCookieService.TryConsumeUnlockCookie(httpContext, clientIp);
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Trace.TraceError("[AdminAuth] Exception during authorization: " + ex);
                return false;
            }
        }

        protected override void HandleUnauthorizedRequest(AuthorizationContext filterContext)
        {
            var rawUrl  = filterContext.HttpContext.Request.RawUrl ?? "/Admin";
            var safeUrl = SanitizeReturnUrl(rawUrl);
            filterContext.Result = new RedirectResult("/Kiosk?unlock=1&returnUrl=" + HttpUtility.UrlEncode(safeUrl));
        }

        // ── Public static API — forwarded to service classes ─────────────────

        public static void MarkAuthed(HttpSessionStateBase session)
            => AdminSessionService.MarkAuthed(session);

        public static void MarkAuthed(HttpSessionStateBase session, int adminId)
            => AdminSessionService.MarkAuthed(session, adminId);

        public static int GetAdminId(HttpSessionStateBase session)
            => AdminSessionService.GetAdminId(session);

        public static void ClearAuthed(HttpSessionStateBase session)
            => AdminSessionService.ClearAuthed(session);

        public static bool RefreshSession(HttpSessionStateBase session)
            => AdminSessionService.RefreshSession(session);

        public static int GetRemainingSessionSeconds(HttpSessionStateBase session)
            => AdminSessionService.GetRemainingSessionSeconds(session);

        public static void RotateSessionId(HttpContextBase httpContext)
            => AdminSessionService.RotateSessionId(httpContext);

        public static void IssueUnlockCookie(HttpContextBase httpContext, string clientIp)
            => AdminUnlockCookieService.IssueUnlockCookie(httpContext, clientIp);

        public static bool TryConsumeUnlockCookie(HttpContextBase httpContext, string clientIp)
            => AdminUnlockCookieService.TryConsumeUnlockCookie(httpContext, clientIp);

        public static bool VerifyPin(string pin, string ip)
            => AdminPinService.VerifyPin(pin, ip);

        public static string HashPin(string pin)
            => AdminPinService.HashPin(pin);

        public static string SanitizeReturnUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "/Admin";
            url = url.Trim();
            if (!url.StartsWith("/") || url.StartsWith("//")) return "/Admin";
            return url;
        }
    }
}
