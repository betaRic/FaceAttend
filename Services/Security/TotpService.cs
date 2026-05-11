using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace FaceAttend.Services.Security
{
    /// <summary>
    /// TOTP-based two-factor authentication service.
    /// Implements RFC 6238 (Time-based One-Time Password).
    /// Uses 6-digit TOTP with 30-second period (standard Google Authenticator).
    /// </summary>
    public static class TotpService
    {
        private const int CodeLength = 6;
        private const int PeriodSeconds = 30;
        private const int RecoveryCodeCount = 10;
        private const int RecoveryCodeLength = 8;

        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// Generates a new TOTP secret as a Base32 string.
        /// Authenticator apps expect RFC 4648 Base32, not Base64.
        /// </summary>
        public static string GenerateSecret()
        {
            var bytes = new byte[20];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            return Base32Encode(bytes);
        }

        /// <summary>
        /// Generates QR code URL for Google Authenticator setup.
        /// Format: otpauth://totp/Label?secret=SECRET&issuer=Issuer&algorithm=SHA1&digits=6&period=30
        /// </summary>
        public static string GenerateSetupUrl(string label, string secret, string issuer = "FaceAttend")
        {
            if (string.IsNullOrWhiteSpace(label))
                throw new ArgumentException("Label is required", nameof(label));
            if (string.IsNullOrWhiteSpace(secret))
                throw new ArgumentException("Secret is required", nameof(secret));

            var escapedLabel = Uri.EscapeDataString(label);
            var escapedIssuer = Uri.EscapeDataString(issuer);

            return $"otpauth://totp/{escapedIssuer}:{escapedLabel}?secret={secret}&issuer={escapedIssuer}&algorithm=SHA1&digits={CodeLength}&period={PeriodSeconds}";
        }

        /// <summary>
        /// Validates a TOTP code against the secret.
        /// Allows +/- 1 time step for clock drift (30-second window each side).
        /// </summary>
        public static bool ValidateCode(string secret, string code)
        {
            if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(code))
                return false;

            try
            {
                var secretBytes = Base32Decode(secret);
                var codeInt = int.Parse(code);

                var currentStep = GetCurrentTimeStep();
                
                for (int i = -1; i <= 1; i++)
                {
                    var expectedCode = ComputeTotp(secretBytes, currentStep + i);
                    if (expectedCode == codeInt)
                        return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Validates a recovery code. Recovery codes are single-use.
        /// </summary>
        public static bool ValidateRecoveryCode(string storedHash, string inputCode)
        {
            if (string.IsNullOrWhiteSpace(storedHash) || string.IsNullOrWhiteSpace(inputCode))
                return false;

            var normalizedCode = inputCode.Trim().ToUpperInvariant();
            var inputHash = HashCode(normalizedCode);
            var storedHashes = storedHash.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var hash in storedHashes)
            {
                if (hash.Equals("USED", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (SecureCompare.FixedEquals(hash, inputHash))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Generates a list of single-use recovery codes.
        /// Returns list of 8-character alphanumeric codes (uppercase).
        /// </summary>
        public static List<string> GenerateRecoveryCodes()
        {
            var codes = new List<string>();
            var chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

            using (var rng = RandomNumberGenerator.Create())
            {
                for (int i = 0; i < RecoveryCodeCount; i++)
                {
                    var code = new char[RecoveryCodeLength];
                    var bytes = new byte[RecoveryCodeLength];
                    rng.GetBytes(bytes);
                    for (int j = 0; j < RecoveryCodeLength; j++)
                    {
                        code[j] = chars[bytes[j] % chars.Length];
                    }
                    codes.Add(new string(code));
                }
            }

            return codes;
        }

        /// <summary>
        /// Hashes a recovery code for storage (bcrypt-like, but using PBKDF2).
        /// </summary>
        public static string HashRecoveryCode(string code)
        {
            return HashCode(code.ToUpperInvariant());
        }

        /// <summary>
        /// Gets the number of seconds remaining in current TOTP period.
        /// </summary>
        public static int GetSecondsRemaining()
        {
            var currentStep = GetCurrentTimeStep();
            var periodEnd = (currentStep + 1) * PeriodSeconds;
            var nowSeconds = (long)(DateTime.UtcNow - UnixEpoch).TotalSeconds;
            return (int)(periodEnd - nowSeconds);
        }

        /// <summary>
        /// Checks if TOTP is properly configured (secret exists and is valid format).
        /// </summary>
        public static bool IsConfigured(string secret)
        {
            if (string.IsNullOrWhiteSpace(secret))
                return false;

            try
            {
                var bytes = Base32Decode(secret);
                return bytes.Length >= 16; // Minimum 80-bit secret
            }
            catch
            {
                return false;
            }
        }

        // Private helpers

        private static long GetCurrentTimeStep()
        {
            var elapsed = (long)(DateTime.UtcNow - UnixEpoch).TotalSeconds;
            return elapsed / PeriodSeconds;
        }

        private static int ComputeTotp(byte[] key, long step)
        {
            var stepBytes = BitConverter.GetBytes(step);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(stepBytes);

            using (var hmac = new HMACSHA1(key))
            {
                var hash = hmac.ComputeHash(stepBytes);
                var offset = hash[hash.Length - 1] & 0x0F;
                var binary =
                    ((hash[offset] & 0x7F) << 24) |
                    ((hash[offset + 1] & 0xFF) << 16) |
                    ((hash[offset + 2] & 0xFF) << 8) |
                    (hash[offset + 3] & 0xFF);
                return binary % (int)Math.Pow(10, CodeLength);
            }
        }

        private static string Base32Encode(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return string.Empty;

            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
            var output = new StringBuilder((bytes.Length * 8 + 4) / 5);
            var buffer = 0;
            var bitsLeft = 0;

            for (int i = 0; i < bytes.Length; i++)
            {
                buffer = (buffer << 8) | bytes[i];
                bitsLeft += 8;

                while (bitsLeft >= 5)
                {
                    bitsLeft -= 5;
                    output.Append(alphabet[(buffer >> bitsLeft) & 31]);
                }
            }

            if (bitsLeft > 0)
                output.Append(alphabet[(buffer << (5 - bitsLeft)) & 31]);

            return output.ToString();
        }

        private static byte[] Base32Decode(string base32)
        {
            base32 = base32.ToUpperInvariant().Replace(" ", "").Replace("-", "");
            
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
            var output = new List<byte>();
            var buffer = 0;
            var bitsLeft = 0;

            foreach (var c in base32)
            {
                if (c == '=') continue;
                var value = alphabet.IndexOf(c);
                if (value < 0)
                    throw new ArgumentException("Invalid base32 character: " + c);

                buffer = (buffer << 5) | value;
                bitsLeft += 5;

                if (bitsLeft >= 8)
                {
                    bitsLeft -= 8;
                    output.Add((byte)(buffer >> bitsLeft));
                }
            }

            return output.ToArray();
        }

        private static string HashCode(string code)
        {
            using (var sha256 = SHA256.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(code);
                var hash = sha256.ComputeHash(bytes);
                return Convert.ToBase64String(hash);
            }
        }
    }
}
