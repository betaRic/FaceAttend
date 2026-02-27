using System;
using System.Collections.Concurrent;
using System.Configuration;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using System.Web.Mvc;
using System.Web.Security;
using System.Web.SessionState;
using FaceAttend.Services.Security;  // P1-F1: required for AdminAccessControl

namespace FaceAttend.Filters
{
    /// <summary>
    /// Session-based admin authorization.
    ///
    /// Fixes applied vs. original:
    ///   1. PIN attempt lockout — brute-force protection per client IP.
    ///   2. Return-URL validation — prevents open-redirect via malicious returnUrl.
    ///   3. Session abandonment on clear — cleans up the entire session on Lock().
    ///   4. [P1-F1] IP allowlist — AdminAccessControl.IsAllowed() called at the top
    ///      of AuthorizeCore so blocked IPs never even reach the PIN layer.
    /// </summary>
    public class AdminAuthorizeAttribute : AuthorizeAttribute
    {
        private const string SessionKeyAuthedUtc = "AdminAuthedUtc";
        private const string UnlockCookieName = "fa_admin_unlock";
        private static readonly string[] UnlockCookiePurpose = new[] { "FaceAttend.AdminUnlock.v1" };

        // -------------------------------------------------------------------
        // Authorization
        // -------------------------------------------------------------------

        protected override bool AuthorizeCore(HttpContextBase httpContext)
        {
            if (httpContext == null) return false;

            // P1-F1 — IP allowlist check.
            // Admin:AllowedIpRanges in Web.config (empty string = allow all IPs).
            // AdminAccessControl.IsAllowed() also supports an emergency bypass file
            // at ~/App_Data/emergency_bypass.txt for locked-out admins.
            var clientIp = httpContext.Request?.UserHostAddress;
            if (!AdminAccessControl.IsAllowed(clientIp))
            {
                // Store the block reason so HandleUnauthorizedRequest can log it
                // or return a more informative response if needed.
                httpContext.Items["AdminBlockReason"] = "IP_NOT_ALLOWED";
                return false;
            }

            var authedUtcObj = httpContext.Session?[SessionKeyAuthedUtc];
            if (!(authedUtcObj is DateTime authedUtc))
            {
                // If the session isn't authed yet, allow a one-time unlock cookie
                // (issued at PIN success) to mark the NEW session as authed.
                var ip = httpContext.Request?.UserHostAddress;
                return TryConsumeUnlockCookie(httpContext, ip);
            }

            var minutes = GetInt("Admin:SessionMinutes", 30);
            return (DateTime.UtcNow - authedUtc) <= TimeSpan.FromMinutes(minutes);
        }

        protected override void HandleUnauthorizedRequest(AuthorizationContext filterContext)
        {
            // FIX (Open Redirect): validate and sanitize returnUrl before embedding it
            // in the redirect so an attacker cannot craft a URL that sends the victim
            // to an external site after "authenticating".
            var rawUrl = filterContext.HttpContext.Request.RawUrl ?? "/Admin";
            var safeUrl = SanitizeReturnUrl(rawUrl);

            // Do not hardcode "/Kiosk".
            // Under an IIS virtual directory, "/Kiosk" points to the domain root and
            // breaks admin navigation.
            var url = new UrlHelper(filterContext.RequestContext);
            var kioskUrl = url.Action(
                "Index",
                "Kiosk",
                new { area = "", unlock = 1, returnUrl = safeUrl }
            );

            filterContext.Result = new RedirectResult(kioskUrl);
        }

        // -------------------------------------------------------------------
        // Session helpers
        // -------------------------------------------------------------------

        public static void MarkAuthed(HttpSessionStateBase session)
        {
            if (session == null) return;
            session[SessionKeyAuthedUtc] = DateTime.UtcNow;
        }

        /// <summary>
        /// Clears only the auth marker on the current session (does not abandon).
        /// Use this when you are about to rotate the session ID.
        /// </summary>
        public static void ClearAuthedMarker(HttpSessionStateBase session)
        {
            if (session == null) return;
            session.Remove(SessionKeyAuthedUtc);
        }

        /// <summary>
        /// Clears the auth marker and abandons the entire session so the
        /// old session ID cannot be reused after locking.
        /// </summary>
        public static void ClearAuthed(HttpSessionStateBase session)
        {
            if (session == null) return;
            session.Remove(SessionKeyAuthedUtc);
            session.Abandon();
        }

        /// <summary>
        /// Rotates the ASP.NET Session ID cookie. This reduces session fixation.
        ///
        /// Important: do NOT write sensitive auth data into the old session after
        /// calling this. Prefer issuing a short-lived unlock cookie and letting
        /// the next request mark the new session as authed.
        /// </summary>
        public static void RotateSessionId(HttpContextBase httpContext)
        {
            if (httpContext?.ApplicationInstance?.Context == null) return;
            try
            {
                var manager = new SessionIDManager();
                var newId = manager.CreateSessionID(httpContext.ApplicationInstance.Context);
                bool redirected, cookieAdded;
                manager.SaveSessionID(httpContext.ApplicationInstance.Context, newId, out redirected, out cookieAdded);
            }
            catch
            {
                // best effort
            }
        }

        /// <summary>
        /// Issues a short-lived, protected unlock cookie that will be consumed
        /// on the next request to mark the NEW session as authed.
        /// </summary>
        public static void IssueUnlockCookie(HttpContextBase httpContext, string clientIp)
        {
            if (httpContext == null) return;

            var seconds = GetInt("Admin:UnlockCookieSeconds", 120);
            if (seconds < 30) seconds = 30;
            if (seconds > 600) seconds = 600;

            var nowUtc = DateTime.UtcNow;
            var ip = (clientIp ?? "").Trim();
            var nonce = Guid.NewGuid().ToString("N");

            var payload = nowUtc.Ticks.ToString() + "|" + ip + "|" + nonce;
            var plain = Encoding.UTF8.GetBytes(payload);

            byte[] protectedBytes;
            try
            {
                protectedBytes = MachineKey.Protect(plain, UnlockCookiePurpose);
            }
            catch
            {
                return;
            }

            var cookie = new HttpCookie(UnlockCookieName, Convert.ToBase64String(protectedBytes))
            {
                HttpOnly = true,
                Secure = httpContext.Request.IsSecureConnection,
                Path = "/",
                Expires = nowUtc.AddSeconds(seconds)
            };

            httpContext.Response.Cookies.Set(cookie);
        }

        /// <summary>
        /// Consumes the unlock cookie (if present and valid) and marks the
        /// current session as authed.
        /// </summary>
        public static bool TryConsumeUnlockCookie(HttpContextBase httpContext, string clientIp)
        {
            if (httpContext == null) return false;

            var cookie = httpContext.Request.Cookies[UnlockCookieName];
            if (cookie == null) return false;

            var seconds = GetInt("Admin:UnlockCookieSeconds", 120);
            if (seconds < 30) seconds = 30;
            if (seconds > 600) seconds = 600;

            byte[] protectedBytes;
            try
            {
                protectedBytes = Convert.FromBase64String(cookie.Value ?? "");
            }
            catch
            {
                ExpireUnlockCookie(httpContext);
                return false;
            }

            byte[] plain;
            try
            {
                plain = MachineKey.Unprotect(protectedBytes, UnlockCookiePurpose);
            }
            catch
            {
                ExpireUnlockCookie(httpContext);
                return false;
            }

            if (plain == null || plain.Length == 0)
            {
                ExpireUnlockCookie(httpContext);
                return false;
            }

            var s = Encoding.UTF8.GetString(plain);
            var parts = s.Split('|');
            if (parts.Length < 3)
            {
                ExpireUnlockCookie(httpContext);
                return false;
            }

            if (!long.TryParse(parts[0], out var ticks))
            {
                ExpireUnlockCookie(httpContext);
                return false;
            }

            var issuedUtc = new DateTime(ticks, DateTimeKind.Utc);
            if ((DateTime.UtcNow - issuedUtc) > TimeSpan.FromSeconds(seconds))
            {
                ExpireUnlockCookie(httpContext);
                return false;
            }

            var cookieIp = parts[1];
            var ip = (clientIp ?? "").Trim();
            if (!string.Equals(cookieIp, ip, StringComparison.OrdinalIgnoreCase))
            {
                ExpireUnlockCookie(httpContext);
                return false;
            }

            // Cookie is valid — consume it (expire immediately) and mark session.
            ExpireUnlockCookie(httpContext);
            MarkAuthed(httpContext.Session);
            return true;
        }

        private static void ExpireUnlockCookie(HttpContextBase httpContext)
        {
            var expired = new HttpCookie(UnlockCookieName, "")
            {
                HttpOnly = true,
                Path = "/",
                Expires = DateTime.UtcNow.AddDays(-1)
            };
            httpContext.Response.Cookies.Set(expired);
        }

        // -------------------------------------------------------------------
        // PIN verification
        // -------------------------------------------------------------------

        private static readonly ConcurrentDictionary<string, LockoutEntry> _lockouts =
            new ConcurrentDictionary<string, LockoutEntry>(StringComparer.OrdinalIgnoreCase);

        public static bool VerifyPin(string pin, string ip)
        {
            pin = (pin ?? "").Trim();
            if (pin.Length == 0) return false;

            var maxAttempts    = GetInt("Admin:PinMaxAttempts",    5);
            var lockoutSeconds = GetInt("Admin:PinLockoutSeconds", 300);

            // --- Check lockout ---
            if (!string.IsNullOrEmpty(ip) && _lockouts.TryGetValue(ip, out var lockout))
            {
                if (lockout.LockedUntil > DateTime.UtcNow)
                    return false; // still in lockout period
            }

            // --- Verify ---
            var stored = (ConfigurationManager.AppSettings["Admin:PinHash"] ?? "").Trim();
            if (stored.Length == 0) return false;

            bool verified = TryVerifyPbkdf2(stored, pin)
                         || ConstantTimeEquals(stored, Sha256Base64(pin))
                         || ConstantTimeEquals(stored, Sha256Hex(pin));

            // --- Update lockout state ---
            if (!string.IsNullOrEmpty(ip))
            {
                if (verified)
                {
                    // Success: clear any existing lockout entry.
                    _lockouts.TryRemove(ip, out _);
                }
                else
                {
                    // Failure: increment counter; lock if threshold reached.
                    _lockouts.AddOrUpdate(
                        ip,
                        _ => new LockoutEntry(1, maxAttempts, lockoutSeconds),
                        (_, existing) => existing.Increment(maxAttempts, lockoutSeconds));
                }
            }

            return verified;
        }

        /// <summary>
        /// Creates a PBKDF2 hash string you can paste into Web.config.
        /// Format: PBKDF2$&lt;iterations&gt;$&lt;saltBase64&gt;$&lt;hashBase64&gt;
        /// </summary>
        public static string HashPinPbkdf2(string pin, int iterations = 120000, int saltBytes = 16, int hashBytes = 32)
        {
            pin = (pin ?? "").Trim();
            if (pin.Length == 0) throw new ArgumentException("PIN is required", nameof(pin));

            if (iterations < 10000) iterations = 10000;
            if (saltBytes < 16) saltBytes = 16;
            if (hashBytes < 32) hashBytes = 32;

            var salt = new byte[saltBytes];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(salt);

            byte[] hash;
            using (var derive = new Rfc2898DeriveBytes(pin, salt, iterations))
                hash = derive.GetBytes(hashBytes);

            return "PBKDF2$" + iterations + "$" + Convert.ToBase64String(salt) + "$" + Convert.ToBase64String(hash);
        }

        private static bool TryVerifyPbkdf2(string stored, string pin)
        {
            if (string.IsNullOrWhiteSpace(stored)) return false;
            if (!stored.StartsWith("PBKDF2$", StringComparison.OrdinalIgnoreCase)) return false;

            var parts = stored.Split('$');
            if (parts.Length != 4) return false;

            if (!int.TryParse(parts[1], out var iterations) || iterations < 10000) return false;

            byte[] salt;
            byte[] expected;
            try
            {
                salt     = Convert.FromBase64String(parts[2]);
                expected = Convert.FromBase64String(parts[3]);
            }
            catch
            {
                return false;
            }

            if (salt == null     || salt.Length < 16)     return false;
            if (expected == null || expected.Length < 16) return false;

            byte[] actual;
            using (var derive = new Rfc2898DeriveBytes(pin, salt, iterations))
                actual = derive.GetBytes(expected.Length);

            return FixedTimeEquals(actual, expected);
        }

        // -------------------------------------------------------------------
        // Return-URL validation
        // -------------------------------------------------------------------

        /// <summary>
        /// Ensures the returnUrl is a local, relative path so it cannot redirect
        /// users to an external site (open-redirect vulnerability).
        /// </summary>
        public static string SanitizeReturnUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "/Admin";
            url = url.Trim();

            // Must start with a single '/' — no protocol-relative URLs (//)
            // and no absolute URLs (https://evil.com).
            if (!url.StartsWith("/") || url.StartsWith("//"))
                return "/Admin";

            // Uri.IsWellFormedUriString with Relative rejects anything that
            // looks like an absolute URI smuggled in relative form.
            if (!Uri.IsWellFormedUriString(url, UriKind.Relative))
                return "/Admin";

            return url;
        }

        // -------------------------------------------------------------------
        // Crypto helpers (unchanged from original)
        // -------------------------------------------------------------------

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
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString().ToUpperInvariant();
            }
        }

        /// <summary>
        /// Constant-time string comparison to prevent timing attacks.
        /// </summary>
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

        private static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null) return false;
            int diff = a.Length ^ b.Length;
            int len = Math.Min(a.Length, b.Length);
            for (int i = 0; i < len; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        // -------------------------------------------------------------------
        // Inner types
        // -------------------------------------------------------------------

        private sealed class LockoutEntry
        {
            public int FailCount { get; private set; }
            public DateTime LockedUntil { get; private set; }

            public LockoutEntry(int failCount, DateTime lockedUntilUtc)
            {
                FailCount  = failCount;
                LockedUntil = lockedUntilUtc;
            }

            public LockoutEntry(int failCount, int maxAttempts, int lockoutSeconds)
                : this(failCount,
                    failCount >= maxAttempts
                        ? DateTime.UtcNow.AddSeconds(lockoutSeconds)
                        : DateTime.MinValue)
            { }

            public LockoutEntry Increment(int maxAttempts, int lockoutSeconds)
            {
                var now = DateTime.UtcNow;

                // Keep existing lockout if it is still active.
                var lockedUntil = LockedUntil > now ? LockedUntil : DateTime.MinValue;

                var nextCount = FailCount + 1;

                // Start a new lockout only when we cross the threshold.
                if (lockedUntil == DateTime.MinValue && nextCount >= maxAttempts)
                    lockedUntil = now.AddSeconds(lockoutSeconds);

                return new LockoutEntry(nextCount, lockedUntil);
            }
        }
    }
}
