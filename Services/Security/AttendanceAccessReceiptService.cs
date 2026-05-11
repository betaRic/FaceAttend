using System;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using System.Web.Security;
using Newtonsoft.Json;

namespace FaceAttend.Services.Security
{
    public static class AttendanceAccessReceiptService
    {
        public const string CookieName = "FaceAttend_AttendanceReceipt";
        private static readonly string[] Purpose = { "FaceAttend", "AttendanceAccessReceipt", "v1" };

        public class ReceiptPayload
        {
            public int EmployeeDbId { get; set; }
            public string EmployeePublicId { get; set; }
            public long AttendanceLogId { get; set; }
            public string EventType { get; set; }
            public DateTime IssuedUtc { get; set; }
            public DateTime ExpiresUtc { get; set; }
            public string IpHash { get; set; }
            public string UserAgentHash { get; set; }
            public string ModelVersion { get; set; }
        }

        public static object Issue(
            HttpResponseBase response,
            HttpRequestBase request,
            Employee employee,
            long attendanceLogId,
            string eventType,
            string modelVersion,
            bool secure)
        {
            if (response == null) throw new ArgumentNullException(nameof(response));
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (employee == null) throw new ArgumentNullException(nameof(employee));

            var now = DateTime.UtcNow;
            var minutes = ConfigurationService.GetInt("AttendanceAccess:ReceiptMinutes", 10);
            if (minutes < 1) minutes = 1;
            if (minutes > 60) minutes = 60;

            var payload = new ReceiptPayload
            {
                EmployeeDbId = employee.Id,
                EmployeePublicId = employee.EmployeeId,
                AttendanceLogId = attendanceLogId,
                EventType = eventType,
                IssuedUtc = now,
                ExpiresUtc = now.AddMinutes(minutes),
                IpHash = Hash(GetClientIp(request)),
                UserAgentHash = Hash(request.UserAgent ?? string.Empty),
                ModelVersion = modelVersion
            };

            var json = JsonConvert.SerializeObject(payload);
            var protectedBytes = MachineKey.Protect(Encoding.UTF8.GetBytes(json), Purpose);
            var token = HttpServerUtility.UrlTokenEncode(protectedBytes);

            var cookie = new HttpCookie(CookieName, token)
            {
                HttpOnly = true,
                Secure = secure,
                SameSite = SameSiteMode.Lax,
                Expires = payload.ExpiresUtc
            };
            response.Cookies.Remove(CookieName);
            response.Cookies.Add(cookie);

            return new
            {
                receiptIssued = true,
                recordUrl = "/Attendance/MyMonth",
                expiresAtUtc = payload.ExpiresUtc
            };
        }

        public static bool TryValidate(HttpRequestBase request, out ReceiptPayload payload, out string error)
        {
            payload = null;
            error = null;

            if (request == null)
            {
                error = "NO_REQUEST";
                return false;
            }

            var cookie = request.Cookies[CookieName];
            if (cookie == null || string.IsNullOrWhiteSpace(cookie.Value))
            {
                error = "RECEIPT_REQUIRED";
                return false;
            }

            try
            {
                var protectedBytes = HttpServerUtility.UrlTokenDecode(cookie.Value);
                if (protectedBytes == null || protectedBytes.Length == 0)
                {
                    error = "RECEIPT_INVALID";
                    return false;
                }

                var bytes = MachineKey.Unprotect(protectedBytes, Purpose);
                if (bytes == null || bytes.Length == 0)
                {
                    error = "RECEIPT_INVALID";
                    return false;
                }

                payload = JsonConvert.DeserializeObject<ReceiptPayload>(Encoding.UTF8.GetString(bytes));
                if (payload == null || payload.EmployeeDbId <= 0 || payload.AttendanceLogId <= 0)
                {
                    error = "RECEIPT_INVALID";
                    return false;
                }

                if (DateTime.UtcNow > payload.ExpiresUtc)
                {
                    error = "RECEIPT_EXPIRED";
                    return false;
                }

                var bindIp = ConfigurationService.GetBool("AttendanceAccess:BindIp", true);
                if (bindIp && !FixedEquals(payload.IpHash, Hash(GetClientIp(request))))
                {
                    error = "RECEIPT_IP_MISMATCH";
                    return false;
                }

                if (!FixedEquals(payload.UserAgentHash, Hash(request.UserAgent ?? string.Empty)))
                {
                    error = "RECEIPT_UA_MISMATCH";
                    return false;
                }

                return true;
            }
            catch
            {
                error = "RECEIPT_INVALID";
                payload = null;
                return false;
            }
        }

        public static void Clear(HttpResponseBase response, bool secure)
        {
            if (response == null) return;
            response.Cookies.Add(new HttpCookie(CookieName, string.Empty)
            {
                HttpOnly = true,
                Secure = secure,
                SameSite = SameSiteMode.Lax,
                Expires = DateTime.UtcNow.AddDays(-1)
            });
        }

        private static string GetClientIp(HttpRequestBase request)
        {
            return (request.UserHostAddress ?? string.Empty).Trim();
        }

        private static string Hash(string value)
        {
            using (var sha = SHA256.Create())
            {
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)));
            }
        }

        private static bool FixedEquals(string left, string right)
        {
            var a = Encoding.UTF8.GetBytes(left ?? string.Empty);
            var b = Encoding.UTF8.GetBytes(right ?? string.Empty);
            return a.Length == b.Length && CryptographicOperationsEquals(a, b);
        }

        private static bool CryptographicOperationsEquals(byte[] a, byte[] b)
        {
            var diff = 0;
            for (var i = 0; i < a.Length; i++)
                diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
