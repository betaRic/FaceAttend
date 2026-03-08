using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Web.Hosting;

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
            if (CheckEmergencyBypass())
                return true;

            // Config allowlist — kung walang nilista, pinapayagan ang lahat.
            var allowedRanges = (AppSettings.GetString("Admin:AllowedIpRanges", "") ?? "").Trim();
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

        /// <summary>
        /// SEC-03 FIX: Emergency bypass ay PERMANENTENG DISABLED.
        ///
        /// Ang dati ay nagche-check ng ~/App_Data/emergency_bypass.txt file —
        /// kung nandoon ang file at may valid na expiry date, lahat ng
        /// IP restrictions ay nabe-bypass.
        ///
        /// BAKIT PINABABA:
        ///   Sinumang may write access sa App_Data (IIS worker process,
        ///   compromised file upload, atbp.) ay pwedeng gumawa ng file na ito
        ///   at makakuha ng admin access mula sa kahit saan sa internet.
        ///   Hindi ito acceptable para sa isang government system na nag-iingat
        ///   ng biometric at attendance data ng mga empleyado.
        ///
        /// ALTERNATIBO SA EMERGENCY:
        ///   Kapag hindi ma-access ang admin (IP restriction):
        ///   1. I-update ang Admin:AllowedIpRanges sa Web.config
        ///      mula sa server mismo (RDP/direct server access)
        ///   2. O mag-restart ng IIS app pool pagkatapos baguhin ang config
        ///
        /// HUWAG I-UNCOMMENT / I-RESTORE ANG LUMANG CODE.
        /// </summary>
        private static bool CheckEmergencyBypass()
        {
            // Palaging false — walang bypass ang allowed sa production.
            return false;
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
