using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Web.Hosting;

namespace FaceAttend.Services.Security
{
    /// <summary>
    /// Optional IP allowlist for the Admin area.
    ///
    /// Web.config:
    ///   <add key="Admin:AllowedIpRanges" value="" />
    ///
    /// Value is a comma-separated list of IPv4 CIDR blocks or single IPv4s.
    /// Example:
    ///   "203.177.45.0/24, 10.0.0.50, 192.168.1.0/24"
    ///
    /// Emergency bypass:
    ///   Create ~/App_Data/emergency_bypass.txt with:
    ///     EXPIRES: 2026-02-21T08:00:00Z
    ///     REASON: Emergency maintenance
    ///
    /// If the file exists and the expiry is in the future, all IPs are allowed.
    /// If the file is invalid or expired, the code deletes it.
    /// </summary>
    public static class AdminAccessControl
    {
        private const string BypassFilePath = "~/App_Data/emergency_bypass.txt";
        private static readonly object _lock = new object();
        private static DateTime _lastBypassCheckUtc = DateTime.MinValue;
        private static bool _lastBypassResult;

        public static bool IsAllowed(string clientIp)
        {
            // 1) Emergency bypass
            if (CheckEmergencyBypass())
                return true;

            // 2) Config allowlist (empty = allow all)
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

        private static bool CheckEmergencyBypass()
        {
            // SEC-03 FIX: Emergency bypass ay PERMANENTENG DISABLED.
            //
            // Ang dati ay nagche-check ng ~/App_Data/emergency_bypass.txt file —
            // kung nandoon ang file at may valid na expiry date, lahat ng
            // IP restrictions ay nabe-bypass.
            //
            // BAKIT PINABABA:
            //   Sinumang may write access sa App_Data (IIS worker process,
            //   compromised file upload, atbp.) ay pwedeng gumawa ng file na ito
            //   at makakuha ng admin access mula sa kahit saan sa internet.
            //   Hindi ito acceptable para sa isang government system na nag-iingat
            //   ng biometric at attendance data ng mga empleyado.
            //
            // ALTERNATIBO SA EMERGENCY:
            //   Kapag hindi ma-access ang admin (IP restriction):
            //   1. I-update ang Admin:AllowedIpRanges sa Web.config
            //      mula sa server mismo (RDP access)
            //   2. O mag-restart ng IIS app pool pagkatapos baguhin ang config
            //
            // HUWAG I-UNCOMMENT / I-RESTORE ANG LUMANG CODE.
            return false;
        }

        private static bool IsIpInRange(string ip, string range)
        {
            if (string.IsNullOrWhiteSpace(ip) || string.IsNullOrWhiteSpace(range))
                return false;

            IPAddress single;
            if (IPAddress.TryParse(range, out single))
                return string.Equals(ip.Trim(), range.Trim(), StringComparison.OrdinalIgnoreCase);

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

            var baseBytes = baseIp.GetAddressBytes();
            var clientBytes = clientIp.GetAddressBytes();
            if (baseBytes.Length != 4 || clientBytes.Length != 4)
                return false; // IPv4 only

            if (prefixLen < 0 || prefixLen > 32)
                return false;

            uint mask = prefixLen == 0 ? 0u : ~(uint.MaxValue >> prefixLen);

            uint baseNet = ((uint)baseBytes[0] << 24) | ((uint)baseBytes[1] << 16) | ((uint)baseBytes[2] << 8) | baseBytes[3];
            uint clientNet = ((uint)clientBytes[0] << 24) | ((uint)clientBytes[1] << 16) | ((uint)clientBytes[2] << 8) | clientBytes[3];

            return (baseNet & mask) == (clientNet & mask);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    File.Delete(path);
            }
            catch { }
        }
    }
}
