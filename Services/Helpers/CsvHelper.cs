// CSV export utilities used by AttendanceController and VisitorsController.

using System.Collections.Generic;
using System.Linq;

namespace FaceAttend.Services.Helpers
{
    public static class CsvHelper
    {
        /// <summary>
        /// Joins CSV cells with proper escaping.
        /// </summary>
        public static string JoinCsv(IEnumerable<string> cells)
            => string.Join(",", cells.Select(EscapeCsv));

        /// <summary>
        /// Escapes a CSV value - handles quotes, commas, and newlines.
        /// </summary>
        public static string EscapeCsv(string s)
        {
            if (s == null) s = "";
            var needsQuote = s.Contains(",") || s.Contains("\"") ||
                             s.Contains("\n") || s.Contains("\r");
            s = s.Replace("\"", "\"\"");
            return needsQuote ? "\"" + s + "\"" : s;
        }

        /// <summary>
        /// Sanitizes a cell value to prevent CSV formula injection.
        /// Prepends apostrophe to values starting with =, +, -, or @
        /// </summary>
        public static string SafeCell(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var t = s.Trim();
            if (t.StartsWith("=") || t.StartsWith("+") ||
                t.StartsWith("-") || t.StartsWith("@"))
                return "'" + t;
            return t;
        }

        /// <summary>
        /// Combines SafeCell and EscapeCsv for a complete sanitization.
        /// </summary>
        public static string SanitizeAndEscape(string s)
            => EscapeCsv(SafeCell(s));
    }
}
