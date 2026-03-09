using System;
using System.Security.Cryptography;
using System.Text;
using FaceAttend.Services;

namespace FaceAttend.Services.Biometrics
{
    /// <summary>
    /// Encrypt/decrypt helper para sa biometric payloads na naka-store sa DB.
    ///
    /// Bakit DPAPI LocalMachine?
    /// - Simple at built-in sa Windows / IIS
    /// - Walang hiwalay na key file na kailangang bantayan
    /// - Parehong machine lang ang kailangan para mag-decrypt
    ///
    /// Important:
    /// - Ito ay para sa at-rest protection sa central server.
    /// - Kapag lumipat ng bagong server/machine, kailangan i-migrate muna ang data
    ///   habang accessible pa ang lumang machine, o mag-decrypt + re-encrypt.
    /// </summary>
    public static class BiometricCrypto
    {
        private const string Prefix = "dpapi1:";

        public static bool IsEnabled()
        {
            return AppSettings.GetBool("Biometrics:Crypto:Enabled", true);
        }

        public static bool IsProtectedValue(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   value.StartsWith(Prefix, StringComparison.Ordinal);
        }

        public static bool NeedsMigration(string value)
        {
            return IsEnabled() &&
                   !string.IsNullOrWhiteSpace(value) &&
                   !IsProtectedValue(value);
        }

        public static string ProtectString(string plainText)
        {
            if (string.IsNullOrWhiteSpace(plainText))
                return plainText;

            if (!IsEnabled())
                return plainText;

            if (IsProtectedValue(plainText))
                return plainText;

            try
            {
                var plainBytes = Encoding.UTF8.GetBytes(plainText);
                var cipherBytes = ProtectedData.Protect(
                    plainBytes,
                    GetEntropyBytes(),
                    DataProtectionScope.LocalMachine);

                return Prefix + Convert.ToBase64String(cipherBytes);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("[BiometricCrypto] ProtectString failed: " + ex.Message);
                throw;
            }
        }

        public static bool TryUnprotectString(string storedValue, out string plainText)
        {
            plainText = null;

            if (string.IsNullOrWhiteSpace(storedValue))
            {
                plainText = storedValue;
                return true;
            }

            if (!IsProtectedValue(storedValue))
            {
                // Legacy/plain row pa ito.
                plainText = storedValue;
                return true;
            }

            try
            {
                var raw = storedValue.Substring(Prefix.Length);
                var cipherBytes = Convert.FromBase64String(raw);
                var plainBytes = ProtectedData.Unprotect(
                    cipherBytes,
                    GetEntropyBytes(),
                    DataProtectionScope.LocalMachine);

                plainText = Encoding.UTF8.GetString(plainBytes);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("[BiometricCrypto] TryUnprotectString failed: " + ex.Message);
                return false;
            }
        }

        public static string ProtectBase64Bytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return null;

            var b64 = Convert.ToBase64String(bytes);
            return ProtectString(b64);
        }

        public static bool TryGetBytesFromStoredBase64(string storedValue, out byte[] bytes)
        {
            bytes = null;

            string plain;
            if (!TryUnprotectString(storedValue, out plain))
                return false;

            if (string.IsNullOrWhiteSpace(plain))
                return false;

            try
            {
                bytes = Convert.FromBase64String(plain);
                return bytes != null && bytes.Length > 0;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning("[BiometricCrypto] Stored base64 parse failed: " + ex.Message);
                return false;
            }
        }

        private static byte[] GetEntropyBytes()
        {
            var entropy = AppSettings.GetString("Biometrics:Crypto:Entropy", "");
            return string.IsNullOrEmpty(entropy) ? null : Encoding.UTF8.GetBytes(entropy);
        }
    }
}
