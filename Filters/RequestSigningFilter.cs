using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using System.Web.Mvc;
using FaceAttend.Services;

namespace FaceAttend.Filters
{
    /// <summary>
    /// Request signing middleware to prevent replay attacks.
    /// All POST/PUT requests must include: timestamp, nonce, signature.
    /// Signature = HMAC-SHA256(timestamp + nonce + body, secretKey)
    /// </summary>
    public class RequestSigningFilter : ActionFilterAttribute
    {
        private const string SignatureHeader = "X-Request-Signature";
        private const string TimestampHeader = "X-Request-Timestamp";
        private const string NonceHeader = "X-Request-Nonce";
        
        private static readonly ConcurrentDictionary<string, DateTime> _usedNonces =
            new ConcurrentDictionary<string, DateTime>();
        
        private static int _timestampToleranceSeconds = 300; // 5 minutes
        private static int _nonceExpiryHours = 24;

        public override void OnActionExecuting(ActionExecutingContext filterContext)
        {
            // Skip for GET requests (they're idempotent)
            if (filterContext.HttpContext.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
                return;

            // Skip for certain paths (health checks, static content)
            var path = filterContext.HttpContext.Request.Path;
            if (path.StartsWith("/health", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/content", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/scripts", StringComparison.OrdinalIgnoreCase))
                return;

            // Get secret key from configuration
            var secretKey = ConfigurationService.GetString("Security:RequestSigningSecret", "");
            
            // If no secret key configured, skip validation (backward compatibility)
            if (string.IsNullOrWhiteSpace(secretKey))
                return;

            try
            {
                var request = filterContext.HttpContext.Request;

                // Get headers
                var signature = request.Headers[SignatureHeader];
                var timestamp = request.Headers[TimestampHeader];
                var nonce = request.Headers[NonceHeader];

                // Validate all required headers present
                if (string.IsNullOrWhiteSpace(signature) || 
                    string.IsNullOrWhiteSpace(timestamp) || 
                    string.IsNullOrWhiteSpace(nonce))
                {
                    filterContext.Result = new HttpStatusCodeResult(401, "Missing required security headers");
                    return;
                }

                // Validate timestamp is within tolerance
                if (!long.TryParse(timestamp, out var timestampEpoch))
                {
                    filterContext.Result = new HttpStatusCodeResult(401, "Invalid timestamp format");
                    return;
                }

                var requestTime = DateTimeOffset.FromUnixTimeSeconds(timestampEpoch);
                var now = DateTimeOffset.UtcNow;
                var timeDiff = Math.Abs((now - requestTime).TotalSeconds);

                if (timeDiff > _timestampToleranceSeconds)
                {
                    filterContext.Result = new HttpStatusCodeResult(401, "Request timestamp expired");
                    return;
                }

                // Validate nonce hasn't been used
                if (_usedNonces.ContainsKey(nonce))
                {
                    filterContext.Result = new HttpStatusCodeResult(401, "Nonce already used - possible replay attack");
                    return;
                }

                // Read body for signature calculation
                string body = "";
                if (request.ContentType?.Contains("application/json") == true)
                {
                    request.InputStream.Position = 0;
                    using (var reader = new System.IO.StreamReader(request.InputStream, Encoding.UTF8, true, 1024, true))
                    {
                        body = reader.ReadToEnd();
                        request.InputStream.Position = 0;
                    }
                }

                // Calculate expected signature
                var dataToSign = timestamp + "." + nonce + "." + body;
                var expectedSignature = ComputeHmacSha256(dataToSign, secretKey);

                // Constant-time comparison to prevent timing attacks
                if (!ConstantTimeEquals(signature, expectedSignature))
                {
                    filterContext.Result = new HttpStatusCodeResult(401, "Invalid signature");
                    return;
                }

                // Store nonce for replay prevention
                _usedNonces.TryAdd(nonce, DateTime.UtcNow);

                // Clean up old nonces periodically (simple cleanup - 1% chance)
                if (new Random().Next(100) == 0)
                {
                    CleanupOldNonces();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("[RequestSigning] Error: " + ex.Message);
                // On error, allow request but log - don't block
            }
        }

        private static string ComputeHmacSha256(string data, string key)
        {
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key)))
            {
                var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
                return Convert.ToBase64String(hash);
            }
        }

        private static bool ConstantTimeEquals(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        private static void CleanupOldNonces()
        {
            var cutoff = DateTime.UtcNow.AddHours(-_nonceExpiryHours);
            foreach (var kvp in _usedNonces)
            {
                if (kvp.Value < cutoff)
                {
                    _usedNonces.TryRemove(kvp.Key, out _);
                }
            }
        }

        /// <summary>
        /// Generates a signed request URL for client-side use.
        /// </summary>
        public static string SignUrl(string url, string secretKey)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var nonce = Guid.NewGuid().ToString("N");
            
            var dataToSign = timestamp + "." + nonce + ".";
            var signature = ComputeHmacSha256(dataToSign, secretKey);
            
            var separator = url.Contains("?") ? "&" : "?";
            return url + separator + $"{TimestampHeader}={timestamp}&{NonceHeader}={nonce}&{SignatureHeader}={HttpUtility.UrlEncode(signature)}";
        }
    }
}
