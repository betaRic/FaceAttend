using System;
using System.Text.RegularExpressions;

namespace FaceAttend.Services.Helpers
{
    /// <summary>
    /// Server-side validation helpers for common data types.
    /// 
    /// PHASE 1 ENHANCEMENT (H3.x):
    ///   Centralized validation logic to ensure data integrity
    ///   and provide consistent error messages.
    /// </summary>
    public static class ValidationHelper
    {
        // Default pattern allows alphanumeric, hyphens, and underscores
        private const string DefaultEmployeeIdPattern = "^[a-zA-Z0-9\\-_]+$";
        private const int DefaultEmployeeIdMaxLen = 50;

        private static Regex GetEmployeeIdRegex()
        {
            var pattern = ConfigurationService.GetString("Biometrics:EmployeeIdPattern", DefaultEmployeeIdPattern);
            if (string.IsNullOrWhiteSpace(pattern))
                pattern = DefaultEmployeeIdPattern;
            return new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
        }

        private static int GetEmployeeIdMaxLen()
        {
            return ConfigurationService.GetInt("Biometrics:EmployeeIdMaxLen", DefaultEmployeeIdMaxLen);
        }

        private static readonly Regex EmailRegex = new Regex(
            @"^[^\s@]+@[^\s@]+\.[^\s@]+$", 
            RegexOptions.Compiled);

        private static readonly Regex PhoneRegex = new Regex(
            @"^[\+]?[\d\s\-\(\)]{7,20}$", 
            RegexOptions.Compiled);

        /// <summary>
        /// Validates an employee ID against the configured pattern.
        /// </summary>
        public static bool IsValidEmployeeId(string employeeId)
        {
            if (string.IsNullOrWhiteSpace(employeeId))
                return false;

            if (employeeId.Length > GetEmployeeIdMaxLen())
                return false;

            return GetEmployeeIdRegex().IsMatch(employeeId);
        }

        /// <summary>
        /// Validates an employee ID and returns an error message if invalid.
        /// </summary>
        public static (bool IsValid, string ErrorMessage) ValidateEmployeeId(string employeeId)
        {
            if (string.IsNullOrWhiteSpace(employeeId))
                return (false, "Employee ID is required.");

            var maxLen = GetEmployeeIdMaxLen();
            if (employeeId.Length > maxLen)
                return (false, $"Employee ID must not exceed {maxLen} characters.");

            if (!GetEmployeeIdRegex().IsMatch(employeeId))
                return (false, "Employee ID format is invalid. Use letters, numbers, hyphens, and underscores only.");

            return (true, null);
        }

        // Philippines coordinate validation is in OfficeLocationService.IsValidPhilippinesCoordinates()

        /// <summary>
        /// Validates an email address format.
        /// </summary>
        public static bool IsValidEmail(string email)
        {
            if (string.IsNullOrWhiteSpace(email))
                return false;

            return EmailRegex.IsMatch(email);
        }

        /// <summary>
        /// Validates a phone number format.
        /// </summary>
        public static bool IsValidPhone(string phone)
        {
            if (string.IsNullOrWhiteSpace(phone))
                return false;

            return PhoneRegex.IsMatch(phone);
        }

        /// <summary>
        /// Validates that a name contains only allowed characters.
        /// </summary>
        public static bool IsValidName(string name, int maxLength = 100)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            if (name.Length > maxLength)
                return false;

            // Allow letters, spaces, hyphens, apostrophes, and periods
            // Common in names like "Mary-Jane", "O'Connor", "Jr."
            return Regex.IsMatch(name, @"^[\p{L}\s\-\'\.]+$");
        }

        /// <summary>
        /// Sanitizes a string for safe display (prevents XSS).
        /// </summary>
        public static string SanitizeForDisplay(string input, int maxLength = 500)
        {
            if (string.IsNullOrWhiteSpace(input))
                return string.Empty;

            // Truncate if too long
            if (input.Length > maxLength)
                input = input.Substring(0, maxLength);

            // Remove potentially dangerous characters
            // Note: For full XSS protection, use proper HTML encoding in views
            input = input.Replace("<", "&lt;").Replace(">", "&gt;");
            input = input.Replace("\"", "&quot;").Replace("'", "&#x27;");

            return input.Trim();
        }

        /// <summary>
        /// Validates that a file is an acceptable image type.
        /// </summary>
        public static bool IsValidImageExtension(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return false;

            var ext = System.IO.Path.GetExtension(fileName).ToLowerInvariant();
            return ext == ".jpg" || ext == ".jpeg" || ext == ".png";
        }

        /// <summary>
        /// Validates image dimensions are acceptable for processing.
        /// </summary>
        public static (bool IsValid, string ErrorMessage) ValidateImageDimensions(int width, int height)
        {
            if (width <= 0 || height <= 0)
                return (false, "Invalid image dimensions.");

            // Minimum size for face detection (roughly 100x100 for a face)
            if (width < 200 || height < 200)
                return (false, "Image is too small. Minimum 200x200 pixels required.");

            // Maximum size (to prevent memory issues)
            const int maxDimension = 4096;
            if (width > maxDimension || height > maxDimension)
                return (false, $"Image is too large. Maximum dimension is {maxDimension} pixels.");

            return (true, null);
        }

        /// <summary>
        /// Validates a purpose/description string.
        /// </summary>
        public static (bool IsValid, string ErrorMessage) ValidatePurpose(string purpose, int maxLength = 500)
        {
            if (string.IsNullOrWhiteSpace(purpose))
                return (false, "Purpose is required.");

            if (purpose.Length > maxLength)
                return (false, $"Purpose must not exceed {maxLength} characters.");

            return (true, null);
        }
    }
}
