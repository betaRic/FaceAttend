using System;
using System.Text;
using System.Web;
using System.Web.Security;
using FaceAttend.Services;

namespace FaceAttend.Services.Security
{
    /// <summary>
    /// Issues and validates a long-lived MachineKey-protected cookie that allows
    /// the admin to skip PIN re-entry when returning to admin from the kiosk,
    /// for as long as the workday bypass window is active (default 8 hours).
    ///
    /// Unlike the one-time unlock cookie, this cookie is NOT consumed on use —
    /// it persists until it expires or FullLock() is called.
    ///
    /// Config key: Admin:BypassCookieHours  (default 8, range 1-24)
    /// </summary>
    public static class AdminPersistCookieService
    {
        private const string CookieName = "fa_admin_persist";
        private static readonly string[] CookiePurpose = { "FaceAttend.AdminPersist.v1" };

        public static void IssuePersistCookie(HttpContextBase httpContext)
        {
            if (httpContext == null) return;

            var hours = GetBypassHours();
            var nowUtc  = DateTime.UtcNow;
            var nonce   = Guid.NewGuid().ToString("N");
            var payload = nowUtc.Ticks + "|" + nonce;
            var plain   = Encoding.UTF8.GetBytes(payload);

            byte[] protectedBytes;
            try   { protectedBytes = MachineKey.Protect(plain, CookiePurpose); }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("[AdminPersist] Failed to protect cookie: " + ex.Message);
                return;
            }

            var cookie = new HttpCookie(CookieName, Convert.ToBase64String(protectedBytes))
            {
                HttpOnly = true,
                Secure   = httpContext.Request.IsSecureConnection,
                SameSite = SameSiteMode.Lax,
                Path     = "/",
                Expires  = nowUtc.AddHours(hours)
            };

            httpContext.Response.Cookies.Set(cookie);
            System.Diagnostics.Trace.TraceInformation(
                "[AdminPersist] Issued, valid for " + hours + "h");
        }

        /// <summary>
        /// Returns true if a valid, unexpired persist cookie is present.
        /// Does NOT consume the cookie — it remains valid for subsequent uses.
        /// </summary>
        public static bool IsValid(HttpContextBase httpContext)
        {
            if (httpContext == null) return false;

            var cookie = httpContext.Request.Cookies[CookieName];
            if (cookie == null) return false;

            byte[] protectedBytes;
            try   { protectedBytes = Convert.FromBase64String(cookie.Value ?? ""); }
            catch { return false; }

            byte[] plain;
            try   { plain = MachineKey.Unprotect(protectedBytes, CookiePurpose); }
            catch { return false; }

            if (plain == null || plain.Length == 0) return false;

            var s     = Encoding.UTF8.GetString(plain);
            var parts = s.Split('|');
            if (parts.Length < 2) return false;

            if (!long.TryParse(parts[0], out var ticks)) return false;

            var issuedUtc = new DateTime(ticks, DateTimeKind.Utc);
            var hours     = GetBypassHours();
            return (DateTime.UtcNow - issuedUtc) <= TimeSpan.FromHours(hours);
        }

        public static void ExpirePersistCookie(HttpContextBase httpContext)
        {
            if (httpContext == null) return;
            var expired = new HttpCookie(CookieName, "")
            {
                HttpOnly = true,
                Secure   = httpContext.Request?.IsSecureConnection ?? true,
                SameSite = SameSiteMode.Lax,
                Path     = "/",
                Expires  = DateTime.UtcNow.AddDays(-1)
            };
            httpContext.Response.Cookies.Set(expired);
        }

        private static int GetBypassHours()
        {
            var h = ConfigurationService.GetInt("Admin:BypassCookieHours", 8);
            if (h < 1)  h = 1;
            if (h > 24) h = 24;
            return h;
        }
    }
}
