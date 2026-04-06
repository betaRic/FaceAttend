using System;
using System.Text;
using System.Web;
using System.Web.Security;
using FaceAttend.Services;
using FaceAttend.Services.Helpers;

namespace FaceAttend.Services.Security
{
    /// <summary>
    /// Issues and consumes the short-lived MachineKey-protected unlock cookie used after
    /// session rotation. The cookie bridges the gap between PIN verification (old session)
    /// and the first authenticated request (new session).
    /// </summary>
    public static class AdminUnlockCookieService
    {
        private const string CookieName = "fa_admin_unlock";
        private static readonly string[] CookiePurpose = { "FaceAttend.AdminUnlock.v1" };

        /// <summary>
        /// Issues a short-lived MachineKey-protected cookie.
        /// The next request consumes it to mark the new session as authenticated.
        /// </summary>
        public static void IssueUnlockCookie(HttpContextBase httpContext, string clientIp)
        {
            if (httpContext == null) return;

            var seconds = ConfigurationService.GetInt("Admin:UnlockCookieSeconds", 120);
            if (seconds < 30)  seconds = 30;
            if (seconds > 600) seconds = 600;

            var nowUtc  = DateTime.UtcNow;
            var ip      = StringHelper.NormalizeIp(clientIp);
            var nonce   = Guid.NewGuid().ToString("N");
            var payload = nowUtc.Ticks + "|" + ip + "|" + nonce;
            var plain   = Encoding.UTF8.GetBytes(payload);

            byte[] protectedBytes;
            try   { protectedBytes = MachineKey.Protect(plain, CookiePurpose); }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("[AdminUnlockCookie] Failed to protect cookie: " + ex.Message);
                return;
            }

            var cookie = new HttpCookie(CookieName, Convert.ToBase64String(protectedBytes))
            {
                HttpOnly = true,
                Secure   = httpContext.Request.IsSecureConnection,
                SameSite = SameSiteMode.Lax,
                Path     = "/",
                Expires  = nowUtc.AddSeconds(seconds)
            };

            httpContext.Response.Cookies.Set(cookie);
            System.Diagnostics.Trace.TraceInformation(
                "[AdminUnlockCookie] Issued for IP: " + ip + ", expires in " + seconds + "s");
        }

        /// <summary>
        /// Consumes the unlock cookie (if present and valid) and marks the current session as authenticated.
        /// </summary>
        public static bool TryConsumeUnlockCookie(HttpContextBase httpContext, string clientIp)
        {
            if (httpContext == null) return false;

            var cookie = httpContext.Request.Cookies[CookieName];
            if (cookie == null)
            {
                System.Diagnostics.Trace.TraceInformation("[AdminUnlockCookie] No unlock cookie found");
                return false;
            }

            var seconds = ConfigurationService.GetInt("Admin:UnlockCookieSeconds", 120);
            if (seconds < 30)  seconds = 30;
            if (seconds > 600) seconds = 600;

            byte[] protectedBytes;
            try   { protectedBytes = Convert.FromBase64String(cookie.Value ?? ""); }
            catch
            {
                System.Diagnostics.Trace.TraceWarning("[AdminUnlockCookie] Failed to decode cookie value");
                ExpireUnlockCookie(httpContext);
                return false;
            }

            byte[] plain;
            try   { plain = MachineKey.Unprotect(protectedBytes, CookiePurpose); }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning("[AdminUnlockCookie] Failed to unprotect cookie: " + ex.Message);
                ExpireUnlockCookie(httpContext);
                return false;
            }

            if (plain == null || plain.Length == 0)
            {
                System.Diagnostics.Trace.TraceWarning("[AdminUnlockCookie] Cookie plaintext is empty");
                ExpireUnlockCookie(httpContext);
                return false;
            }

            var s     = Encoding.UTF8.GetString(plain);
            var parts = s.Split('|');
            if (parts.Length < 3)
            {
                System.Diagnostics.Trace.TraceWarning("[AdminUnlockCookie] Invalid cookie format");
                ExpireUnlockCookie(httpContext);
                return false;
            }

            if (!long.TryParse(parts[0], out var ticks))
            {
                System.Diagnostics.Trace.TraceWarning("[AdminUnlockCookie] Invalid timestamp in cookie");
                ExpireUnlockCookie(httpContext);
                return false;
            }

            var issuedUtc = new DateTime(ticks, DateTimeKind.Utc);
            if ((DateTime.UtcNow - issuedUtc) > TimeSpan.FromSeconds(seconds))
            {
                System.Diagnostics.Trace.TraceWarning("[AdminUnlockCookie] Cookie expired");
                ExpireUnlockCookie(httpContext);
                return false;
            }

            var cookieIp = StringHelper.NormalizeIp(parts[1]);
            var ip       = StringHelper.NormalizeIp(clientIp);
            if (!string.Equals(cookieIp, ip, StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Trace.TraceWarning(
                    "[AdminUnlockCookie] IP mismatch. Cookie: " + cookieIp + ", Current: " + ip);
                ExpireUnlockCookie(httpContext);
                return false;
            }

            // Valid — consume and mark session authenticated.
            System.Diagnostics.Trace.TraceInformation("[AdminUnlockCookie] Valid, marking session authenticated");
            ExpireUnlockCookie(httpContext);
            AdminSessionService.MarkAuthed(httpContext.Session);
            return true;
        }

        public static void ExpireUnlockCookie(HttpContextBase httpContext)
        {
            var expired = new HttpCookie(CookieName, "")
            {
                HttpOnly = true,
                Secure   = httpContext?.Request?.IsSecureConnection ?? true,
                SameSite = SameSiteMode.Lax,
                Path     = "/",
                Expires  = DateTime.UtcNow.AddDays(-1)
            };
            httpContext.Response.Cookies.Set(expired);
        }
    }
}
