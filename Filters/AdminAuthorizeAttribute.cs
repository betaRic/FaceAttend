using System;
using System.Collections.Concurrent;
using System.Configuration;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using System.Web.Mvc;
using System.Web.Security;
using System.Web.SessionState;
using FaceAttend.Services.Security;

namespace FaceAttend.Filters
{
    /// <summary>
    /// Session-based admin authorization.
    ///
    /// Mga fix na inilapat:
    ///   1. PIN brute-force lockout per client IP.
    ///   2. Return-URL validation — pinipigilan ang open-redirect attacks.
    ///   3. Session abandonment sa clear — nililinis ang buong session sa Lock().
    ///   4. IP allowlist — AdminAccessControl.IsAllowed() tinatawag bago pa man
    ///      maabot ng request ang PIN layer.
    ///
    /// PHASE 1 FIX (S-03):
    ///   Ang Admin:PinHash ay HINDI na binabasa mula sa Web.config.
    ///   Binabasa na ito mula sa IIS Environment Variable na "FACEATTEND_ADMIN_PIN_HASH"
    ///   para hindi makita sa source control.
    ///
    ///   Paano mag-set ng environment variable:
    ///     PowerShell (as Admin):
    ///       [System.Environment]::SetEnvironmentVariable(
    ///         "FACEATTEND_ADMIN_PIN_HASH",
    ///         "PBKDF2$120000$...",   ← ang hash ng iyong PIN
    ///         "Machine")
    ///
    ///   Para makuha ang hash ng bagong PIN, gamitin ang:
    ///     AdminAuthorizeAttribute.HashPin("iyong-pin-dito")
    ///
    ///   Kung talagang hindi maiwasang nasa Web.config, gamitin ang IIS Manager:
    ///     Sites > [site] > Configuration Editor > appSettings
    ///     (hindi kasama ito sa .csproj at source control kapag ginawa sa IIS)
    /// </summary>
    public class AdminAuthorizeAttribute : AuthorizeAttribute
    {
        private const string SessionKeyAuthedUtc  = "AdminAuthedUtc";
        private const string UnlockCookieName     = "fa_admin_unlock";
        private static readonly string[] UnlockCookiePurpose = { "FaceAttend.AdminUnlock.v1" };

        // Pangalan ng environment variable na naglalaman ng PIN hash.
        // Itago ang actual na hash sa labas ng source control.
        private const string PinHashEnvVar = "FACEATTEND_ADMIN_PIN_HASH";

        // ─────────────────────────────────────────────────────────────────────
        // Authorization entry point
        // ─────────────────────────────────────────────────────────────────────

        protected override bool AuthorizeCore(HttpContextBase httpContext)
        {
            if (httpContext == null) return false;

            // Hakbang 1: IP allowlist check — blocked agad kung hindi naka-list ang IP.
            // Kapag walang nilista sa Admin:AllowedIpRanges, lahat ng IP ay pinapayagan.
            var clientIp = httpContext.Request?.UserHostAddress;
            if (!AdminAccessControl.IsAllowed(clientIp))
            {
                httpContext.Items["AdminBlockReason"] = "IP_NOT_ALLOWED";
                return false;
            }

            // Hakbang 2: Tingnan kung may valid na session na.
            var authedUtcObj = httpContext.Session?[SessionKeyAuthedUtc];
            if (!(authedUtcObj is DateTime authedUtc))
            {
                // Walang session — subukang gamitin ang one-time unlock cookie
                // na ini-issue pagkatapos ng matagumpay na PIN verification.
                var ip = httpContext.Request?.UserHostAddress;
                return TryConsumeUnlockCookie(httpContext, ip);
            }

            // Hakbang 3: Tingnan kung expired na ang session.
            var minutes = GetInt("Admin:SessionMinutes", 30);
            return (DateTime.UtcNow - authedUtc) <= TimeSpan.FromMinutes(minutes);
        }

        protected override void HandleUnauthorizedRequest(AuthorizationContext filterContext)
        {
            // FIX: validate at sanitize ang returnUrl bago i-embed sa redirect
            // para hindi magamit ng attacker para ma-redirect ang user sa external site.
            var rawUrl  = filterContext.HttpContext.Request.RawUrl ?? "/Admin";
            var safeUrl = SanitizeReturnUrl(rawUrl);

            var url      = new UrlHelper(filterContext.RequestContext);
            var kioskUrl = url.Action("Index", "Kiosk",
                new { area = "", unlock = 1, returnUrl = safeUrl });

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

        public static void ClearAuthedMarker(HttpSessionStateBase session)
        {
            if (session == null) return;
            session.Remove(SessionKeyAuthedUtc);
        }

        public static void ClearAuthed(HttpSessionStateBase session)
        {
            if (session == null) return;
            session.Remove(SessionKeyAuthedUtc);
            session.Abandon();
        }

        /// <summary>
        /// Nagro-rotate ng ASP.NET Session ID cookie para mabawasan ang session fixation risk.
        /// IMPORTANTENG TALA: Huwag isulat ang sensitive auth data sa lumang session
        /// pagkatapos tawagin ito. Gumamit ng short-lived unlock cookie para sa next request.
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
                // Best effort — hindi dapat mag-crash ang app kapag nag-fail ang rotation.
            }
        }

        /// <summary>
        /// Nag-iisyu ng short-lived, protected unlock cookie na gagamitin sa susunod na request
        /// para ma-mark ang BAGONG session bilang authenticated.
        /// </summary>
        public static void IssueUnlockCookie(HttpContextBase httpContext, string clientIp)
        {
            if (httpContext == null) return;

            var seconds = GetInt("Admin:UnlockCookieSeconds", 120);
            if (seconds < 30)  seconds = 30;
            if (seconds > 600) seconds = 600;

            var nowUtc  = DateTime.UtcNow;
            var ip      = (clientIp ?? "").Trim();
            var nonce   = Guid.NewGuid().ToString("N");
            var payload = nowUtc.Ticks.ToString() + "|" + ip + "|" + nonce;
            var plain   = Encoding.UTF8.GetBytes(payload);

            byte[] protectedBytes;
            try   { protectedBytes = MachineKey.Protect(plain, UnlockCookiePurpose); }
            catch { return; } // Kung nag-fail ang protect, huwag mag-issue ng cookie.

            var cookie = new HttpCookie(UnlockCookieName, Convert.ToBase64String(protectedBytes))
            {
                HttpOnly = true,
                // PHASE 1 FIX (S-08): Secure flag — ang cookie ay ipapadala lang
                // sa HTTPS connections. Kapag HTTP pa rin, hindi magtatakbo ito
                // pero acceptable iyon dahil dapat HTTPS na lahat.
                Secure   = true,
                SameSite = SameSiteMode.Strict,
                Path     = "/",
                Expires  = nowUtc.AddSeconds(seconds)
            };

            httpContext.Response.Cookies.Set(cookie);
        }

        /// <summary>
        /// Kinokonsyumo ang unlock cookie (kung present at valid) at minumarka
        /// ang kasalukuyang session bilang authenticated.
        /// </summary>
        public static bool TryConsumeUnlockCookie(HttpContextBase httpContext, string clientIp)
        {
            if (httpContext == null) return false;

            var cookie = httpContext.Request.Cookies[UnlockCookieName];
            if (cookie == null) return false;

            var seconds = GetInt("Admin:UnlockCookieSeconds", 120);
            if (seconds < 30)  seconds = 30;
            if (seconds > 600) seconds = 600;

            byte[] protectedBytes;
            try   { protectedBytes = Convert.FromBase64String(cookie.Value ?? ""); }
            catch { ExpireUnlockCookie(httpContext); return false; }

            byte[] plain;
            try   { plain = MachineKey.Unprotect(protectedBytes, UnlockCookiePurpose); }
            catch { ExpireUnlockCookie(httpContext); return false; }

            if (plain == null || plain.Length == 0)
            { ExpireUnlockCookie(httpContext); return false; }

            var s     = Encoding.UTF8.GetString(plain);
            var parts = s.Split('|');
            if (parts.Length < 3) { ExpireUnlockCookie(httpContext); return false; }

            if (!long.TryParse(parts[0], out var ticks))
            { ExpireUnlockCookie(httpContext); return false; }

            var issuedUtc = new DateTime(ticks, DateTimeKind.Utc);
            if ((DateTime.UtcNow - issuedUtc) > TimeSpan.FromSeconds(seconds))
            { ExpireUnlockCookie(httpContext); return false; }

            var cookieIp = parts[1];
            var ip       = (clientIp ?? "").Trim();
            if (!string.Equals(cookieIp, ip, StringComparison.OrdinalIgnoreCase))
            { ExpireUnlockCookie(httpContext); return false; }

            // Valid ang cookie — i-consume (i-expire agad) at i-mark ang session.
            ExpireUnlockCookie(httpContext);
            MarkAuthed(httpContext.Session);
            return true;
        }

        private static void ExpireUnlockCookie(HttpContextBase httpContext)
        {
            var expired = new HttpCookie(UnlockCookieName, "")
            {
                HttpOnly = true,
                Secure   = true,
                SameSite = SameSiteMode.Strict,
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
            if (pin.Length == 0) return false;

            var maxAttempts    = GetInt("Admin:PinMaxAttempts",    5);
            var lockoutSeconds = GetInt("Admin:PinLockoutSeconds", 300);

            // Hakbang 1: Tingnan kung naka-lockout ang IP.
            if (!string.IsNullOrEmpty(ip) && _lockouts.TryGetValue(ip, out var lockout))
            {
                if (lockout.LockedUntil > DateTime.UtcNow)
                    return false; // Nasa lockout period pa — tanggihan agad.
            }

            // Hakbang 2: Basahin ang stored hash.
            // PHASE 1 FIX (S-03): Binabasa muna sa environment variable,
            // pagkatapos sa Web.config (backwards compat para sa dev environments).
            var stored = (
                Environment.GetEnvironmentVariable(PinHashEnvVar)
                ?? ConfigurationManager.AppSettings["Admin:PinHash"]
                ?? ""
            ).Trim();

            if (stored.Length == 0)
            {
                // Walang PIN hash na naka-configure — tanggihan ang lahat ng attempts.
                // Huwag i-log ang PIN attempt details para sa security.
                return false;
            }

            // Hakbang 3: I-verify ang PIN gamit ang PBKDF2 (pangunahin) o SHA256 (fallback).
            bool verified = TryVerifyPbkdf2(stored, pin)
                         || ConstantTimeEquals(stored, Sha256Base64(pin))
                         || ConstantTimeEquals(stored, Sha256Hex(pin));

            // Hakbang 4: I-update ang lockout state.
            if (!string.IsNullOrEmpty(ip))
            {
                if (verified)
                {
                    // Matagumpay — i-clear ang anumang lockout entry para sa IP na ito.
                    _lockouts.TryRemove(ip, out _);
                }
                else
                {
                    // Nabigo — dagdagan ang counter; i-lock kung na-reach na ang threshold.
                    _lockouts.AddOrUpdate(
                        ip,
                        _ => new LockoutEntry(1, maxAttempts, lockoutSeconds),
                        (_, existing) => existing.Increment(maxAttempts, lockoutSeconds));
                }
            }

            return verified;
        }

        /// <summary>
        /// Gumagawa ng PBKDF2 hash string na pwedeng i-set sa environment variable.
        /// I-call ito sa development para makuha ang hash ng bagong PIN:
        ///   var hash = AdminAuthorizeAttribute.HashPin("iyong-pin");
        ///   // Pagkatapos: set FACEATTEND_ADMIN_PIN_HASH=hash sa IIS
        /// </summary>
        public static string HashPin(string pin)
        {
            if (string.IsNullOrWhiteSpace(pin))
                throw new ArgumentException("Kailangan ng PIN.", nameof(pin));

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
            // Tanggapin lang ang relative URLs para maiwasan ang open-redirect.
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

        private static int GetInt(string key, int fallback)
        {
            var v = ConfigurationManager.AppSettings[key];
            return int.TryParse(v, out var n) ? n : fallback;
        }

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
