using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using FaceAttend.Services;
using FaceAttend.Services.Helpers;

namespace FaceAttend.Services.Security
{
    /// <summary>
    /// Verifies the admin PIN with PBKDF2-primary / SHA256-fallback verification
    /// and per-IP brute-force lockout tracking.
    /// PIN hash is stored in the env var FACEATTEND_ADMIN_PIN_HASH (not Web.config).
    /// Generate a hash with: AdminPinService.HashPin("your-pin")
    /// </summary>
    public static class AdminPinService
    {
        private const string PinHashEnvVar = "FACEATTEND_ADMIN_PIN_HASH";

        private static readonly ConcurrentDictionary<string, LockoutEntry> _lockouts =
            new ConcurrentDictionary<string, LockoutEntry>(StringComparer.OrdinalIgnoreCase);

        public static bool VerifyPin(string pin, string ip)
        {
            pin = (pin ?? "").Trim();
            ip  = StringHelper.NormalizeIp(ip);
            
            // SECURITY: Require minimum 6 characters (was 4-digit PIN - too weak)
            if (pin.Length < 6) return false;

            var maxAttempts    = ConfigurationService.GetInt("Admin:PinMaxAttempts",    5);
            var lockoutSeconds = ConfigurationService.GetInt("Admin:PinLockoutSeconds", 300);

            if (!string.IsNullOrEmpty(ip) && _lockouts.TryGetValue(ip, out var lockout))
            {
                if (lockout.LockedUntil > DateTime.UtcNow)
                    return false;
            }

            var stored = (
                Environment.GetEnvironmentVariable(PinHashEnvVar)
                ?? Environment.GetEnvironmentVariable(PinHashEnvVar, EnvironmentVariableTarget.Process)
                ?? Environment.GetEnvironmentVariable(PinHashEnvVar, EnvironmentVariableTarget.User)
                ?? Environment.GetEnvironmentVariable(PinHashEnvVar, EnvironmentVariableTarget.Machine)
                ?? ""
            ).Trim();

            if (stored.Length == 0)
                return false;

            bool verified = TryVerifyPbkdf2(stored, pin)
                         || ConstantTimeEquals(stored, Sha256Base64(pin))
                         || ConstantTimeEquals(stored, Sha256Hex(pin));

            if (!string.IsNullOrEmpty(ip))
            {
                if (verified)
                    _lockouts.TryRemove(ip, out _);
                else
                {
                    // IMPROVED: Exponential backoff - each failed attempt increases lockout time
                    // 1st failure: 5 min, 2nd: 10 min, 3rd: 20 min, 4th: 40 min, etc.
                    var existingLockout = _lockouts.TryGetValue(ip, out var priorLockout) ? priorLockout : null;
                    var attemptCount = existingLockout?.Attempts ?? 0;
                    var scaledLockout = (int)(lockoutSeconds * Math.Pow(2, attemptCount));
                    scaledLockout = Math.Min(scaledLockout, 3600); // Cap at 1 hour max
                    
                    _lockouts.AddOrUpdate(
                        ip,
                        _ => new LockoutEntry(1, maxAttempts, scaledLockout),
                        (_, existing) => existing.Increment(maxAttempts, scaledLockout));
                }
            }

            return verified;
        }

        /// <summary>
        /// Returns a PBKDF2 hash string suitable for FACEATTEND_ADMIN_PIN_HASH.
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

        private static bool TryVerifyPbkdf2(string stored, string pin)
        {
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
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        private static string Sha256Base64(string input)
        {
            using (var sha = SHA256.Create())
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(input)));
        }

        private static string Sha256Hex(string input)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(input))).Replace("-", "").ToLowerInvariant();
        }

        private sealed class LockoutEntry
        {
            public int      Attempts    { get; private set; }
            public DateTime LockedUntil { get; private set; }

            public LockoutEntry(int attempts, int maxAttempts, int lockoutSeconds)
            {
                Attempts    = attempts;
                LockedUntil = attempts >= maxAttempts
                    ? DateTime.UtcNow.AddSeconds(lockoutSeconds)
                    : DateTime.MinValue;
            }

            public LockoutEntry Increment(int maxAttempts, int lockoutSeconds)
                => new LockoutEntry(Attempts + 1, maxAttempts, lockoutSeconds);
        }
    }
}
