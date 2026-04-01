using System;
using System.Linq;
using System.Net;

namespace FaceAttend.Services.Security
{
    /// <summary>
    /// Optional IPv4 allowlist for the Admin area.
    /// Config key: Admin:AllowedIpRanges — comma-separated CIDR blocks or single IPs.
    /// Example: "203.177.45.0/24, 10.0.0.50, 192.168.1.0/24"
    /// If empty, all IPs are allowed (relying on PIN + rate limiting only).
    /// Emergency bypass file is permanently disabled.
    /// </summary>
    public static class AdminAccessControl
    {
        private const string AllowedRangesEnvVar = "FACEATTEND_ADMIN_ALLOWED_IP_RANGES";

        /// <summary>
        /// Returns true if the given IP is allowed by Admin:AllowedIpRanges config.
        /// Empty config = allow all. Null/whitespace IP = deny when ranges are configured.
        /// </summary>
        public static bool IsAllowed(string clientIp)
        {
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
        /// Returns true if ip falls within range (single IPv4 or CIDR block).
        /// IPv6 is not supported — always returns false for IPv6 inputs.
        /// </summary>
        private static bool IsIpInRange(string ip, string range)
        {
            if (string.IsNullOrWhiteSpace(ip) || string.IsNullOrWhiteSpace(range))
                return false;

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

            // IPv4 only.
            if (baseBytes.Length != 4 || clientBytes.Length != 4)
                return false;

            if (prefixLen < 0 || prefixLen > 32)
                return false;

            uint mask      = prefixLen == 0 ? 0u : ~(uint.MaxValue >> prefixLen);
            uint baseNet   = ToUint(baseBytes);
            uint clientNet = ToUint(clientBytes);

            return (baseNet & mask) == (clientNet & mask);
        }

        /// <summary>Converts a 4-byte IPv4 address to uint for subnet mask comparison.</summary>
        private static uint ToUint(byte[] b)
        {
            return ((uint)b[0] << 24)
                 | ((uint)b[1] << 16)
                 | ((uint)b[2] << 8)
                 |  (uint)b[3];
        }
    }
}
