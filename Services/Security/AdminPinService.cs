using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using FaceAttend.Services;
using FaceAttend.Services.Helpers;

namespace FaceAttend.Services.Security
{
    /// <summary>
    /// Verifies the admin PIN with PBKDF2-primary / SHA256-fallback verification
    /// and per-IP brute-force lockout tracking.
    /// The active PIN hash is stored in SystemConfigurations as Admin:PinHash.
    /// FACEATTEND_ADMIN_PIN_HASH is kept only as a legacy bootstrap fallback.
    /// </summary>
    public static class AdminPinService
    {
        private const string PinHashEnvVar = "FACEATTEND_ADMIN_PIN_HASH";
        public const int MinimumPinLength = 6;

        private static readonly ConcurrentDictionary<string, LockoutEntry> _lockouts =
            new ConcurrentDictionary<string, LockoutEntry>(StringComparer.OrdinalIgnoreCase);

        public static bool VerifyPin(string pin, string ip)
        {
            pin = (pin ?? "").Trim();
            ip  = StringHelper.NormalizeIp(ip);
            
            if (pin.Length < MinimumPinLength) return false;

            var maxAttempts    = ConfigurationService.GetInt("Admin:PinMaxAttempts",    5);
            var lockoutSeconds = ConfigurationService.GetInt("Admin:PinLockoutSeconds", 300);

            if (!string.IsNullOrEmpty(ip) && _lockouts.TryGetValue(ip, out var lockout))
            {
                if (lockout.LockedUntil > DateTime.UtcNow)
                    return false;
            }

            var stored = GetStoredPinHash();

            if (stored.Length == 0)
                return false;

            bool verified = TryVerifyPbkdf2(stored, pin)
                         || SecureCompare.FixedEquals(stored, Sha256Base64(pin))
                         || SecureCompare.FixedEquals(stored, Sha256Hex(pin));

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

        public static bool HasDatabasePinHash()
        {
            return !string.IsNullOrWhiteSpace(GetDatabasePinHash());
        }

        public static bool HasLegacyEnvironmentPinHash()
        {
            return !string.IsNullOrWhiteSpace(GetLegacyEnvironmentPinHash());
        }

        /// <summary>Returns a PBKDF2 hash string for Admin:PinHash.</summary>
        public static string HashPin(string pin)
        {
            if (string.IsNullOrWhiteSpace(pin))
                throw new ArgumentException("PIN is required.", nameof(pin));

            pin = pin.Trim();
            if (pin.Length < MinimumPinLength)
                throw new ArgumentException("PIN must be at least " + MinimumPinLength + " characters.", nameof(pin));

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

        private static string GetStoredPinHash()
        {
            var dbHash = GetDatabasePinHash();
            return !string.IsNullOrWhiteSpace(dbHash)
                ? dbHash
                : GetLegacyEnvironmentPinHash();
        }

        private static string GetDatabasePinHash()
        {
            try
            {
                using (var db = new FaceAttendDBEntities())
                {
                    return (db.SystemConfigurations
                        .Where(x => x.Key == "Admin:PinHash")
                        .Select(x => x.Value)
                        .FirstOrDefault() ?? "").Trim();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "[AdminPinService] Could not read Admin:PinHash from database: {0}",
                    ex.GetBaseException().Message);
                return "";
            }
        }

        private static string GetLegacyEnvironmentPinHash()
        {
            return (
                Environment.GetEnvironmentVariable(PinHashEnvVar)
                ?? Environment.GetEnvironmentVariable(PinHashEnvVar, EnvironmentVariableTarget.Process)
                ?? Environment.GetEnvironmentVariable(PinHashEnvVar, EnvironmentVariableTarget.User)
                ?? Environment.GetEnvironmentVariable(PinHashEnvVar, EnvironmentVariableTarget.Machine)
                ?? ""
            ).Trim();
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

            return SecureCompare.FixedEquals(actualHash, expectedHash);
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
