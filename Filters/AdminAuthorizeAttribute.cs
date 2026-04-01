using System;
using System.Collections.Concurrent;
using System.Configuration;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using System.Web.Mvc;
using System.Web.Security;
using System.Web.SessionState;
using FaceAttend.Services;
using FaceAttend.Services.Helpers;
using FaceAttend.Services.Security;

namespace FaceAttend.Filters
{
    /// <summary>
    /// Session-based admin authorization.
    /// Features: PIN brute-force lockout per IP, open-redirect prevention on return URLs,
    /// full session abandonment on lock, IP allowlist checked before the PIN layer.
    /// PIN hash is read from IIS env var FACEATTEND_ADMIN_PIN_HASH (not Web.config).
    /// Generate a hash with: AdminAuthorizeAttribute.HashPin("your-pin")
    /// </summary>
    public class AdminAuthorizeAttribute : AuthorizeAttribute
    {
        private const string SessionKeyAuthedUtc  = "AdminAuthedUtc";
        private const string SessionKeyAdminId    = "AdminId";
        private const string UnlockCookieName     = "fa_admin_unlock";
        private static readonly string[] UnlockCookiePurpose = { "FaceAttend.AdminUnlock.v1" };

        private const string PinHashEnvVar = "FACEATTEND_ADMIN_PIN_HASH";

        // ─────────────────────────────────────────────────────────────────────
        // Authorization entry point
        // ─────────────────────────────────────────────────────────────────────

        protected override bool AuthorizeCore(HttpContextBase httpContext)
        {
            try
            {
                if (httpContext == null) return false;

                var requestUrl = httpContext.Request?.RawUrl ?? "unknown";
                System.Diagnostics.Trace.TraceInformation("[AdminAuth] AuthorizeCore called for: " + requestUrl);

                // Step 1: IP allowlist check — deny immediately if not in list.
                var clientIp = StringHelper.NormalizeIp(httpContext.Request?.UserHostAddress);
                if (!AdminAccessControl.IsAllowed(clientIp))
                {
                    httpContext.Items["AdminBlockReason"] = "IP_NOT_ALLOWED";
                    System.Diagnostics.Trace.TraceWarning("[AdminAuth] IP not allowed: " + clientIp);
                    return false;
                }

                // Step 2: Check for valid session.
                var authedUtcObj = httpContext.Session?[SessionKeyAuthedUtc];
                if (!(authedUtcObj is DateTime authedUtc))
                {
                    // No session — try the one-time unlock cookie issued after successful PIN verification.
                    System.Diagnostics.Trace.TraceInformation("[AdminAuth] No active session, trying unlock cookie");
                    var ip = StringHelper.NormalizeIp(httpContext.Request?.UserHostAddress);
                    var result = TryConsumeUnlockCookie(httpContext, ip);
                    System.Diagnostics.Trace.TraceInformation("[AdminAuth] Unlock cookie result: " + result);
                    return result;
                }

                // Step 3: Check session age.
                var minutes = ConfigurationService.GetInt("Admin:SessionMinutes", 30);
                var elapsed = DateTime.UtcNow - authedUtc;
                var isValid = elapsed <= TimeSpan.FromMinutes(minutes);
                System.Diagnostics.Trace.TraceInformation("[AdminAuth] Session found, elapsed: " + elapsed.TotalMinutes + " min, valid: " + isValid);
                return isValid;
            }
            catch (Exception ex)
            {
                // CRITICAL FIX: Catch any unexpected exceptions during authorization
                // to prevent HandleAdminErrorAttribute from causing a redirect loop.
                // Log the error and return false to trigger normal unauthorized handling.
                System.Diagnostics.Trace.TraceError("[AdminAuth] Exception during authorization: " + ex);
                return false;
            }
        }

        protected override void HandleUnauthorizedRequest(AuthorizationContext filterContext)
        {
            var rawUrl  = filterContext.HttpContext.Request.RawUrl ?? "/Admin";
            var safeUrl = SanitizeReturnUrl(rawUrl);

            // CRITICAL FIX: Bypass MVC routing to avoid resolving Kiosk inside Admin area.
            // When inside the Admin area, url.Action("Index", "Kiosk", new { area = "" })
            // incorrectly resolves to "/Admin/Kiosk" instead of "/Kiosk", causing redirect loop.
            var kioskUrl = "/Kiosk?unlock=1&returnUrl=" + HttpUtility.UrlEncode(safeUrl);

            filterContext.Result = new RedirectResult(kioskUrl);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Session helpers
        // ─────────────────────────────────────────────────────────────────────

        public static void MarkAuthed(HttpSessionStateBase session)
        {
            if (session == null) return;
            session[SessionKeyAuthedUtc] = DateTime.UtcNow;
        }

        public static void MarkAuthed(HttpSessionStateBase session, int adminId)
        {
            if (session == null) return;
            session[SessionKeyAuthedUtc] = DateTime.UtcNow;
            session[SessionKeyAdminId] = adminId;
        }

        public static int GetAdminId(HttpSessionStateBase session)
        {
            if (session == null) return 1;
            var id = session[SessionKeyAdminId];
            if (id is int intId) return intId;
            return 1; // Default fallback
        }

        public static void ClearAuthed(HttpSessionStateBase session)
        {
            if (session == null) return;
            session.Remove(SessionKeyAuthedUtc);
            session.Remove(SessionKeyAdminId);
            session.Abandon();
        }

        /// <summary>
        /// Slides the admin session expiry window forward.
        /// </summary>
        public static bool RefreshSession(HttpSessionStateBase session)
        {
            if (session == null) return false;
            if (!(session[SessionKeyAuthedUtc] is DateTime)) return false;

            session[SessionKeyAuthedUtc] = DateTime.UtcNow;
            return true;
        }

        /// <summary>
        /// Returns remaining seconds before the admin session expires.
        /// </summary>
        public static int GetRemainingSessionSeconds(HttpSessionStateBase session)
        {
            if (session == null) return 0;
            if (!(session[SessionKeyAuthedUtc] is DateTime authedUtc)) return 0;

            var minutes = ConfigurationService.GetInt("Admin:SessionMinutes", 30);
            var elapsed = DateTime.UtcNow - authedUtc;
            var remaining = TimeSpan.FromMinutes(minutes) - elapsed;
            return remaining.TotalSeconds > 0 ? (int)remaining.TotalSeconds : 0;
        }


        /// <summary>
        /// Rotates the ASP.NET session ID cookie to reduce session fixation risk.
        /// Do not write auth data to the old session after this call — use the short-lived unlock cookie for the next request.
        /// </summary>
        public static void RotateSessionId(HttpContextBase httpContext)
        {
            if (httpContext?.ApplicationInstance?.Context == null) return;
            try
            {
                var manager = new SessionIDManager();
                bool redirected, cookieAdded;
                var newId = manager.CreateSessionID(httpContext.ApplicationInstance.Context);
                manager.SaveSessionID(
                    httpContext.ApplicationInstance.Context,
                    newId, out redirected, out cookieAdded);
            }
            catch
            {
                // Best effort.
            }
        }

        /// <summary>
        /// Issues a short-lived MachineKey-protected cookie. The next request consumes it to mark the new session as authenticated.
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
            var payload = nowUtc.Ticks.ToString() + "|" + ip + "|" + nonce;
            var plain   = Encoding.UTF8.GetBytes(payload);

            byte[] protectedBytes;
            try   { protectedBytes = MachineKey.Protect(plain, UnlockCookiePurpose); }
            catch (Exception ex) 
            { 
                System.Diagnostics.Trace.TraceError("[AdminAuth] Failed to protect unlock cookie: " + ex.Message);
                return; 
            }

            var cookie = new HttpCookie(UnlockCookieName, Convert.ToBase64String(protectedBytes))
            {
                HttpOnly = true,
                Secure = httpContext.Request.IsSecureConnection,
                SameSite = SameSiteMode.Lax,
                Path = "/",
                Expires = nowUtc.AddSeconds(seconds)
            };

            httpContext.Response.Cookies.Set(cookie);
            System.Diagnostics.Trace.TraceInformation("[AdminAuth] Unlock cookie issued for IP: " + ip + ", expires in " + seconds + " seconds, secure: " + httpContext.Request.IsSecureConnection);
        }

        /// <summary>
        /// Consumes the unlock cookie (if present and valid) and marks the current session as authenticated.
        /// </summary>
        public static bool TryConsumeUnlockCookie(HttpContextBase httpContext, string clientIp)
        {
            if (httpContext == null) return false;

            var cookie = httpContext.Request.Cookies[UnlockCookieName];
            if (cookie == null) 
            {
                System.Diagnostics.Trace.TraceInformation("[AdminAuth] No unlock cookie found");
                return false;
            }

            var seconds = ConfigurationService.GetInt("Admin:UnlockCookieSeconds", 120);
            if (seconds < 30)  seconds = 30;
            if (seconds > 600) seconds = 600;

            byte[] protectedBytes;
            try   { protectedBytes = Convert.FromBase64String(cookie.Value ?? ""); }
            catch 
            { 
                System.Diagnostics.Trace.TraceWarning("[AdminAuth] Failed to decode unlock cookie");
                ExpireUnlockCookie(httpContext); 
                return false; 
            }

            byte[] plain;
            try   { plain = MachineKey.Unprotect(protectedBytes, UnlockCookiePurpose); }
            catch (Exception ex) 
            { 
                System.Diagnostics.Trace.TraceWarning("[AdminAuth] Failed to unprotect unlock cookie: " + ex.Message);
                ExpireUnlockCookie(httpContext); 
                return false; 
            }

            if (plain == null || plain.Length == 0)
            { 
                System.Diagnostics.Trace.TraceWarning("[AdminAuth] Unlock cookie plaintext is empty");
                ExpireUnlockCookie(httpContext); 
                return false; 
            }

            var s     = Encoding.UTF8.GetString(plain);
            var parts = s.Split('|');
            if (parts.Length < 3) 
            { 
                System.Diagnostics.Trace.TraceWarning("[AdminAuth] Unlock cookie has invalid format");
                ExpireUnlockCookie(httpContext); 
                return false; 
            }

            if (!long.TryParse(parts[0], out var ticks))
            { 
                System.Diagnostics.Trace.TraceWarning("[AdminAuth] Unlock cookie has invalid timestamp");
                ExpireUnlockCookie(httpContext); 
                return false; 
            }

            var issuedUtc = new DateTime(ticks, DateTimeKind.Utc);
            if ((DateTime.UtcNow - issuedUtc) > TimeSpan.FromSeconds(seconds))
            { 
                System.Diagnostics.Trace.TraceWarning("[AdminAuth] Unlock cookie expired");
                ExpireUnlockCookie(httpContext); 
                return false; 
            }

            var cookieIp = StringHelper.NormalizeIp(parts[1]);
            var ip       = StringHelper.NormalizeIp(clientIp);
            if (!string.Equals(cookieIp, ip, StringComparison.OrdinalIgnoreCase))
            { 
                System.Diagnostics.Trace.TraceWarning("[AdminAuth] Unlock cookie IP mismatch. Cookie: " + cookieIp + ", Current: " + ip);
                ExpireUnlockCookie(httpContext); 
                return false; 
            }

            // Valid — consume (expire immediately) and mark session authenticated.
            System.Diagnostics.Trace.TraceInformation("[AdminAuth] Unlock cookie valid, marking session authenticated");
            ExpireUnlockCookie(httpContext);
            MarkAuthed(httpContext.Session);
            return true;
        }

        private static void ExpireUnlockCookie(HttpContextBase httpContext)
        {
            var expired = new HttpCookie(UnlockCookieName, "")
            {
                HttpOnly = true,
                Secure   = httpContext?.Request?.IsSecureConnection ?? true,
                SameSite = SameSiteMode.Lax,
                Path     = "/",
                Expires  = DateTime.UtcNow.AddDays(-1)
            };
            httpContext.Response.Cookies.Set(expired);
        }

        // ─────────────────────────────────────────────────────────────────────
        // PIN verification
        // ─────────────────────────────────────────────────────────────────────

        private static readonly ConcurrentDictionary<string, LockoutEntry> _lockouts =
            new ConcurrentDictionary<string, LockoutEntry>(StringComparer.OrdinalIgnoreCase);

        public static bool VerifyPin(string pin, string ip)
        {
            pin = (pin ?? "").Trim();
            
            ip  = StringHelper.NormalizeIp(ip);
            if (pin.Length == 0) return false;

            var maxAttempts    = ConfigurationService.GetInt("Admin:PinMaxAttempts",    5);
            var lockoutSeconds = ConfigurationService.GetInt("Admin:PinLockoutSeconds", 300);

            // Step 1: Check lockout.
            if (!string.IsNullOrEmpty(ip) && _lockouts.TryGetValue(ip, out var lockout))
            {
                if (lockout.LockedUntil > DateTime.UtcNow)
                    return false;
            }

            // Step 2: Read stored hash (env var preferred over Web.config).
            var stored = (
                Environment.GetEnvironmentVariable(PinHashEnvVar)
                ?? Environment.GetEnvironmentVariable(PinHashEnvVar, EnvironmentVariableTarget.Process)
                ?? Environment.GetEnvironmentVariable(PinHashEnvVar, EnvironmentVariableTarget.User)
                ?? Environment.GetEnvironmentVariable(PinHashEnvVar, EnvironmentVariableTarget.Machine)
                ?? ""
            ).Trim();

            if (stored.Length == 0)
                return false; // No PIN hash configured — deny all.

            // Step 3: Verify PIN (PBKDF2 primary, SHA256 fallback).
            bool verified = TryVerifyPbkdf2(stored, pin)
                         || ConstantTimeEquals(stored, Sha256Base64(pin))
                         || ConstantTimeEquals(stored, Sha256Hex(pin));

            // Step 4: Update lockout state.
            if (!string.IsNullOrEmpty(ip))
            {
                if (verified)
                {
                    _lockouts.TryRemove(ip, out _);
                }
                else
                {
                    _lockouts.AddOrUpdate(
                        ip,
                        _ => new LockoutEntry(1, maxAttempts, lockoutSeconds),
                        (_, existing) => existing.Increment(maxAttempts, lockoutSeconds));
                }
            }

            return verified;
        }

        /// <summary>
        /// Returns a PBKDF2 hash string suitable for the FACEATTEND_ADMIN_PIN_HASH env var.
        /// Example: var hash = AdminAuthorizeAttribute.HashPin("your-pin");
        /// </summary>
        public static string HashPin(string pin)
        {
            if (string.IsNullOrWhiteSpace(pin))
                throw new ArgumentException("PIN is required.", nameof(pin));

            const int iterations = 120_000;
            var salt = new byte[16];
            using (var rng = new RNGCryptoServiceProvider())
                rng.GetBytes(salt);

            using (var pbkdf2 = new Rfc2898DeriveBytes(pin, salt, iterations, HashAlgorithmName.SHA256))
            {
                var hash = pbkdf2.GetBytes(32);
                return $"PBKDF2${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────────────

        public static string SanitizeReturnUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "/Admin";
            url = url.Trim();
            if (!url.StartsWith("/") || url.StartsWith("//"))
                return "/Admin";
            return url;
        }

        private static bool TryVerifyPbkdf2(string stored, string pin)
        {
            // Format: PBKDF2$iterations$base64salt$base64hash
            var parts = stored.Split('$');
            if (parts.Length < 4 || !parts[0].Equals("PBKDF2", StringComparison.OrdinalIgnoreCase))
                return false;

            if (!int.TryParse(parts[1], out var iterations) || iterations < 1000)
                return false;

            byte[] salt, expectedHash;
            try
            {
                salt         = Convert.FromBase64String(parts[2]);
                expectedHash = Convert.FromBase64String(parts[3]);
            }
            catch { return false; }

            byte[] actualHash;
            try
            {
                using (var pbkdf2 = new Rfc2898DeriveBytes(pin, salt, iterations, HashAlgorithmName.SHA256))
                    actualHash = pbkdf2.GetBytes(expectedHash.Length);
            }
            catch { return false; }

            return ConstantTimeEquals(actualHash, expectedHash);
        }

        private static bool ConstantTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        private static bool ConstantTimeEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            if (a.Length != b.Length)   return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
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
                return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
            }
        }


        // Note: Using AppSettings.GetInt() from FaceAttend.Services for consistency

        // ─────────────────────────────────────────────────────────────────────
        // Lockout tracking
        // ─────────────────────────────────────────────────────────────────────

        private sealed class LockoutEntry
        {
            public int      Attempts   { get; private set; }
            public DateTime LockedUntil { get; private set; }

            public LockoutEntry(int attempts, int maxAttempts, int lockoutSeconds)
            {
                Attempts = attempts;
                LockedUntil = attempts >= maxAttempts
                    ? DateTime.UtcNow.AddSeconds(lockoutSeconds)
                    : DateTime.MinValue;
            }

            public LockoutEntry Increment(int maxAttempts, int lockoutSeconds)
            {
                return new LockoutEntry(Attempts + 1, maxAttempts, lockoutSeconds);
            }
        }
    }
}
