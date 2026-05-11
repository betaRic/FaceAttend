using System;

namespace FaceAttend.Services.Helpers
{
    public static class StringHelper
    {
        public static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value))
                return value;
            
            return value.Length <= maxLength 
                ? value 
                : value.Substring(0, maxLength);
        }

        public static string TruncateAndTrim(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            
            value = value.Trim();
            return value.Length <= maxLength 
                ? value 
                : value.Substring(0, maxLength);
        }

        public static string NormalizeIp(string ip)
        {
            ip = (ip ?? "").Trim();

            if (ip.Length == 0)
                return "";

            if (ip.Equals("::1", StringComparison.OrdinalIgnoreCase) ||
                ip.Equals("0:0:0:0:0:0:0:1", StringComparison.OrdinalIgnoreCase))
                return "127.0.0.1";

            if (ip.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase))
                return ip.Substring(7);

            return ip;
        }

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
