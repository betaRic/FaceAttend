using System;
using System.Linq;
using System.Net;

namespace FaceAttend.Services.Security
{
    /// <summary>
    /// Optional IP allowlist para sa Admin area.
    ///
    /// Web.config:
    ///   <add key="Admin:AllowedIpRanges" value="" />
    ///
    /// Format: comma-separated na list ng IPv4 CIDR blocks o single IPv4s.
    /// Halimbawa:
    ///   "203.177.45.0/24, 10.0.0.50, 192.168.1.0/24"
    ///
    /// Kung walang nilista (empty string), lahat ng IP ay pinapayagan.
    ///
    /// SEC-03 FIX: Ang emergency bypass file (~/App_Data/emergency_bypass.txt)
    /// ay PERMANENTENG DISABLED. Tingnan ang CheckEmergencyBypass() para sa detalye.
    ///
    /// BUG FIX (CS0169 warning): Tinanggal ang mga dead fields na
    ///   _lastBypassResult, _lastBypassCheckUtc, _lock, at BypassFilePath
    ///   — hindi na ginagamit dahil permanenteng disabled ang bypass.
    ///   Ang mga field na ito ay nagdudulot ng CS0169 compiler warning
    ///   "field is never used" na nakalilito sa mga developer.
    /// </summary>
    public static class AdminAccessControl
    {
        private const string AllowedRangesEnvVar = "FACEATTEND_ADMIN_ALLOWED_IP_RANGES";
        // WALA NANG dead fields dito. Ang dating _lastBypassResult,
        // _lastBypassCheckUtc, _lock, at BypassFilePath ay tinanggal na
        // dahil hindi na ginagamit (SEC-03 fix).

        /// <summary>
        /// Sinisigurado kung ang given na IP address ay pinapayagan na mag-access
        /// ng admin area batay sa Admin:AllowedIpRanges config.
        ///
        /// Returns true kung:
        ///   - Walang nilista sa AllowedIpRanges (empty = allow all)
        ///   - Ang IP ay nasa loob ng isa sa mga listed na range
        ///
        /// Returns false kung:
        ///   - May lista ng allowed ranges AT wala ang IP sa alinman sa mga ito
        ///   - Ang clientIp ay null o whitespace (at may lista ng ranges)
        /// </summary>
        public static bool IsAllowed(string clientIp)
        {
            // Emergency bypass ay palaging disabled — tingnan ang CheckEmergencyBypass().
            // Config allowlist — kung walang nilista, pinapayagan ang lahat.
            var allowedRanges = GetAllowedRanges();
            if (string.IsNullOrEmpty(allowedRanges))
                return true;

            if (string.IsNullOrWhiteSpace(clientIp))
                return false;

            var ranges = allowedRanges
                .Split(',')
                .Select(r => (r ?? "").Trim())
                .Where(r => r.Length > 0);

            foreach (var r in ranges)
            {
                if (IsIpInRange(clientIp, r))
                    return true;
            }

            return false;
        }

        private static string GetAllowedRanges()
        {
            var env = Environment.GetEnvironmentVariable(AllowedRangesEnvVar);
            if (!string.IsNullOrWhiteSpace(env))
                return env.Trim();

            return (ConfigurationService.GetString("Admin:AllowedIpRanges", "") ?? "").Trim();
        }

        /// <summary>
        /// Sinisigurado kung ang isang IPv4 address ay nasa loob ng isang CIDR range
        /// o katumbas ng isang single IP address.
        ///
        /// Sinusuportahan:
        ///   - Single IP: "10.0.0.5"
        ///   - CIDR: "10.0.0.0/24", "192.168.1.0/16"
        ///
        /// IPv6 ay HINDI sinusuportahan — returns false para sa IPv6 inputs.
        /// </summary>
        private static bool IsIpInRange(string ip, string range)
        {
            if (string.IsNullOrWhiteSpace(ip) || string.IsNullOrWhiteSpace(range))
                return false;

            // Subukan kung single IP lang (walang CIDR notation).
            IPAddress single;
            if (IPAddress.TryParse(range, out single))
                return string.Equals(ip.Trim(), range.Trim(), StringComparison.OrdinalIgnoreCase);

            // CIDR notation: "base/prefix"
            var parts = range.Split('/');
            if (parts.Length != 2)
                return false;

            IPAddress baseIp;
            int prefixLen;
            if (!IPAddress.TryParse(parts[0].Trim(), out baseIp))
                return false;
            if (!int.TryParse(parts[1].Trim(), out prefixLen))
                return false;

            IPAddress clientIp;
            if (!IPAddress.TryParse(ip.Trim(), out clientIp))
                return false;

            var baseBytes   = baseIp.GetAddressBytes();
            var clientBytes = clientIp.GetAddressBytes();

            // IPv4 only — 4 bytes ang inaasahan.
            if (baseBytes.Length != 4 || clientBytes.Length != 4)
                return false;

            if (prefixLen < 0 || prefixLen > 32)
                return false;

            // Kalkulahin ang subnet mask at i-compare ang network portions.
            uint mask      = prefixLen == 0 ? 0u : ~(uint.MaxValue >> prefixLen);
            uint baseNet   = ToUint(baseBytes);
            uint clientNet = ToUint(clientBytes);

            return (baseNet & mask) == (clientNet & mask);
        }

        /// <summary>
        /// Kino-convert ang 4-byte IPv4 address array papunta sa uint
        /// para sa bitwise comparison ng subnet masks.
        /// </summary>
        private static uint ToUint(byte[] b)
        {
            return ((uint)b[0] << 24)
                 | ((uint)b[1] << 16)
                 | ((uint)b[2] << 8)
                 |  (uint)b[3];
        }
    }
}
