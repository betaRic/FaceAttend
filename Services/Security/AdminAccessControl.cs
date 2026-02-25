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
            lock (_lock)
            {
                // Cache for 5 seconds to reduce disk I/O.
                if ((DateTime.UtcNow - _lastBypassCheckUtc) < TimeSpan.FromSeconds(5))
                    return _lastBypassResult;

                _lastBypassCheckUtc = DateTime.UtcNow;
                _lastBypassResult = false;

                var path = HostingEnvironment.MapPath(BypassFilePath);
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    return false;

                try
                {
                    var lines = File.ReadAllLines(path);
                    var expiresLine = lines.FirstOrDefault(l =>
                        (l ?? "").TrimStart().StartsWith("EXPIRES:", StringComparison.OrdinalIgnoreCase));

                    if (expiresLine == null)
                    {
                        TryDelete(path);
                        return false;
                    }

                    var expiresStr = expiresLine.Substring("EXPIRES:".Length).Trim();
                    DateTime expiresUtc;
                    if (!DateTime.TryParseExact(
                            expiresStr,
                            "yyyy-MM-ddTHH:mm:ssZ",
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.AssumeUniversal |
                            System.Globalization.DateTimeStyles.AdjustToUniversal,
                            out expiresUtc))
                    {
                        TryDelete(path);
                        return false;
                    }

                    if (DateTime.UtcNow > expiresUtc)
                    {
                        TryDelete(path);
                        return false;
                    }

                    _lastBypassResult = true;
                    return true;
                }
                catch
                {
                    return false;
                }
            }
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
