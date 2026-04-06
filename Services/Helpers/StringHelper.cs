using System;

namespace FaceAttend.Services.Helpers
{
    /// <summary>
    /// Centralized string utility methods.
    /// </summary>
    public static class StringHelper
    {
        /// <summary>
        /// Truncates a string to the specified maximum length.
        /// Returns the original string if it's null, empty, or within the limit.
        /// </summary>
        /// <param name="value">The string to truncate</param>
        /// <param name="maxLength">Maximum allowed length</param>
        /// <returns>Truncated string or original if within limit</returns>
        public static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value))
                return value;
            
            return value.Length <= maxLength 
                ? value 
                : value.Substring(0, maxLength);
        }

        /// <summary>
        /// Truncates and trims a string to the specified maximum length.
        /// Useful for database fields that require trimmed input.
        /// </summary>
        /// <param name="value">The string to truncate and trim</param>
        /// <param name="maxLength">Maximum allowed length</param>
        /// <returns>Trimmed and truncated string</returns>
        public static string TruncateAndTrim(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            
            value = value.Trim();
            return value.Length <= maxLength 
                ? value 
                : value.Substring(0, maxLength);
        }

        /// <summary>
        /// Normalizes an IP address for consistent comparison.
        /// Converts IPv6 localhost to IPv4, extracts IPv4 from IPv6-mapped addresses.
        /// </summary>
        /// <param name="ip">The IP address to normalize</param>
        /// <returns>Normalized IP string</returns>
        public static string NormalizeIp(string ip)
        {
            ip = (ip ?? "").Trim();

            if (ip.Length == 0)
                return "";

            // Convert IPv6 localhost to IPv4
            if (ip.Equals("::1", StringComparison.OrdinalIgnoreCase) ||
                ip.Equals("0:0:0:0:0:0:0:1", StringComparison.OrdinalIgnoreCase))
                return "127.0.0.1";

            // Extract IPv4 from IPv6-mapped address (::ffff:192.168.1.1)
            if (ip.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase))
                return ip.Substring(7);

            return ip;
        }

        /// <summary>
        /// Checks if a string is null, empty, or consists only of whitespace.
        /// </summary>
        public static bool IsNullOrWhiteSpace(string value)
        {
            return string.IsNullOrWhiteSpace(value);
        }

        /// <summary>
        /// Safely converts an object to string, returning empty string if null.
        /// </summary>
        public static string ToSafeString(object value)
        {
            return value?.ToString() ?? "";
        }

        /// <summary>
        /// Sanitizes a display string for rendering in views.
        /// Decodes HTML entities, normalises Unicode punctuation, removes control characters,
        /// and replaces any remaining non-printable characters with a hyphen.
        /// </summary>
        public static string SanitizeDisplayText(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return input;

            var decoded = System.Net.WebUtility.HtmlDecode(input);

            var result = decoded
                .Replace("\u2013", "-")   // En dash
                .Replace("\u2014", "-")   // Em dash
                .Replace("\u2212", "-")   // Minus sign
                .Replace("\u2018", "'")   // Left single quote
                .Replace("\u2019", "'")   // Right single quote
                .Replace("\u201C", "\"")  // Left double quote
                .Replace("\u201D", "\"")  // Right double quote
                .Replace("\u2026", "...") // Ellipsis
                .Replace("\u00A0", " ")   // Non-breaking space
                .Replace("\u0000", "")    // Null character
                .Replace("\uFFFD", "");   // Replacement character

            result = System.Text.RegularExpressions.Regex.Replace(
                result, @"[^\p{L}\p{N}\s\-\.\/\\\(\)\,\&\#\@\']", "-");
            result = System.Text.RegularExpressions.Regex.Replace(result, @"-+", "-");

            return result.Trim('-', ' ');
        }
    }
}
