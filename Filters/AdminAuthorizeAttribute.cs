using System;
using System.Configuration;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using System.Web.Mvc;

namespace FaceAttend.Filters
{
    public class AdminAuthorizeAttribute : AuthorizeAttribute
    {
        private const string SessionKeyAuthedUtc = "AdminAuthedUtc";

        protected override bool AuthorizeCore(HttpContextBase httpContext)
        {
            if (httpContext == null) return false;

            var authedUtcObj = httpContext.Session?[SessionKeyAuthedUtc];
            if (!(authedUtcObj is DateTime authedUtc)) return false;

            var minutes = GetInt("Admin:SessionMinutes", 30);
            return (DateTime.UtcNow - authedUtc) <= TimeSpan.FromMinutes(minutes);
        }

        protected override void HandleUnauthorizedRequest(AuthorizationContext filterContext)
        {
            // Admin unlock is handled from the Kiosk screen (Ctrl + Shift + Space).
            // If an admin page is accessed without auth, send the user back to Kiosk
            // with a returnUrl so Kiosk can redirect back after unlocking.
            var url = filterContext.HttpContext.Request.RawUrl ?? "/Admin";
            filterContext.Result = new RedirectResult("/Kiosk?unlock=1&returnUrl=" + HttpUtility.UrlEncode(url));
        }

        public static bool VerifyPin(string pin)
        {
            pin = (pin ?? "").Trim();
            if (pin.Length == 0) return false;

            var stored = (ConfigurationManager.AppSettings["Admin:PinHash"] ?? "").Trim();
            if (stored.Length == 0) return false;

            // Try Base64 SHA256
            var b64 = Sha256Base64(pin);
            if (ConstantTimeEquals(stored, b64)) return true;

            // Also accept hex SHA256 (legacy)
            var hex = Sha256Hex(pin);
            if (ConstantTimeEquals(stored, hex)) return true;

            return false;
        }

        public static void MarkAuthed(HttpSessionStateBase session)
        {
            if (session == null) return;
            session[SessionKeyAuthedUtc] = DateTime.UtcNow;
        }

        public static void ClearAuthed(HttpSessionStateBase session)
        {
            if (session == null) return;
            session.Remove(SessionKeyAuthedUtc);
        }

        private static int GetInt(string key, int fallback)
        {
            var s = ConfigurationManager.AppSettings[key];
            return int.TryParse(s, out var v) ? v : fallback;
        }

        private static string Sha256Base64(string input)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
                return Convert.ToBase64String(bytes);
            }
        }

        private static string Sha256Hex(string input)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
                var sb = new StringBuilder(bytes.Length * 2);
                for (int i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2"));
                return sb.ToString().ToUpperInvariant();
            }
        }

        private static bool ConstantTimeEquals(string a, string b)
        {
            if (a == null || b == null) return false;

            var aa = Encoding.UTF8.GetBytes(a);
            var bb = Encoding.UTF8.GetBytes(b);

            int diff = aa.Length ^ bb.Length;
            int len = Math.Min(aa.Length, bb.Length);
            for (int i = 0; i < len; i++) diff |= aa[i] ^ bb[i];

            return diff == 0;
        }
    }
}
