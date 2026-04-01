using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FaceAttend.Models.ViewModels.Mobile
{
    public class NewEmployeeEnrollmentVm
    {
        public string EmployeeId          { get; set; }
        public string FirstName           { get; set; }
        public string MiddleName          { get; set; }
        public string LastName            { get; set; }
        public string Position            { get; set; }
        public string Department          { get; set; }
        public int    OfficeId            { get; set; }
        public string FaceEncoding        { get; set; }
        public string AllFaceEncodingsJson { get; set; }
        public string DeviceName          { get; set; }

        public List<string> Validate()
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(EmployeeId))
                errors.Add("Employee ID is required");
            else if (EmployeeId.Length < 5 || EmployeeId.Length > 20)
                errors.Add("Employee ID must be 5-20 characters");
            else if (!Regex.IsMatch(EmployeeId, @"^[A-Z0-9 -]+$"))
                errors.Add("Employee ID contains invalid characters");

            if (string.IsNullOrWhiteSpace(FirstName))
                errors.Add("First Name is required");
            else if (FirstName.Length < 2 || FirstName.Length > 50)
                errors.Add("First Name must be 2-50 characters");
            else if (!Regex.IsMatch(FirstName, @"^[A-Z .\-']+$"))
                errors.Add("First Name contains invalid characters");

            if (string.IsNullOrWhiteSpace(LastName))
                errors.Add("Last Name is required");
            else if (LastName.Length < 2 || LastName.Length > 50)
                errors.Add("Last Name must be 2-50 characters");
            else if (!Regex.IsMatch(LastName, @"^[A-Z .\-']+$"))
                errors.Add("Last Name contains invalid characters");

            if (!string.IsNullOrWhiteSpace(MiddleName))
            {
                if (MiddleName.Length > 50)
                    errors.Add("Middle Name must be 50 characters or less");
                else if (!Regex.IsMatch(MiddleName, @"^[A-Z .\-']*$"))
                    errors.Add("Middle Name contains invalid characters");
            }

            if (string.IsNullOrWhiteSpace(Position))
                errors.Add("Position is required");
            else if (Position.Length < 2 || Position.Length > 100)
                errors.Add("Position must be 2-100 characters");
            else if (!Regex.IsMatch(Position, @"^[A-Z0-9 .\-/(),]+$"))
                errors.Add("Position contains invalid characters");

            if (string.IsNullOrWhiteSpace(Department))
                errors.Add("Department is required");
            else if (Department.Length < 2 || Department.Length > 100)
                errors.Add("Department must be 2-100 characters");
            else if (!Regex.IsMatch(Department, @"^[A-Z0-9 .\-/(),]+$"))
                errors.Add("Department contains invalid characters");

            if (OfficeId <= 0)
                errors.Add("Office is required");

            if (string.IsNullOrWhiteSpace(DeviceName))
                errors.Add("Device Name is required");
            else if (DeviceName.Length < 2 || DeviceName.Length > 50)
                errors.Add("Device Name must be 2-50 characters");
            else if (!Regex.IsMatch(DeviceName, @"^[A-Z0-9 ._@]+$"))
                errors.Add("Device Name contains invalid characters");

            if (string.IsNullOrWhiteSpace(FaceEncoding))
                errors.Add("Face enrollment is required");

            return errors;
        }

        public void Sanitize()
        {
            EmployeeId = SanitizeInput(EmployeeId)?.ToUpperInvariant();
            FirstName  = SanitizeInput(FirstName)?.ToUpperInvariant();
            MiddleName = string.IsNullOrWhiteSpace(MiddleName) ? null : SanitizeInput(MiddleName)?.ToUpperInvariant();
            LastName   = SanitizeInput(LastName)?.ToUpperInvariant();
            Position   = SanitizeInput(Position)?.ToUpperInvariant();
            Department = SanitizeInput(Department)?.ToUpperInvariant();
            DeviceName = SanitizeInput(DeviceName)?.ToUpperInvariant();
        }

        private static string SanitizeInput(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return input;
            input = Regex.Replace(input, @"<[^>]+>", string.Empty);
            input = Regex.Replace(input, @"<script[^>]*>[\s\S]*?</script>", string.Empty, RegexOptions.IgnoreCase);
            input = Regex.Replace(input, @"javascript:", string.Empty, RegexOptions.IgnoreCase);
            input = Regex.Replace(input, @"on\w+\s*=", string.Empty, RegexOptions.IgnoreCase);
            return input.Trim();
        }
    }

    public class DeviceRegistrationVm
    {
        public string EmployeeId        { get; set; }
        public string EmployeeFullName  { get; set; }
        public string Department        { get; set; }
        public string Position          { get; set; }
        public bool   IsNewEmployee     { get; set; }
        public int?   EmployeeDbId      { get; set; }
        public string DeviceName        { get; set; }
        public string Fingerprint       { get; set; }
        public bool   HasExistingDevice { get; set; }
        public string ExistingDeviceName { get; set; }

        public List<string> Validate()
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(DeviceName))
                errors.Add("Device Name is required");
            else if (DeviceName.Length < 2 || DeviceName.Length > 50)
                errors.Add("Device Name must be 2-50 characters");
            else if (!Regex.IsMatch(DeviceName, @"^[A-Z0-9\s\.\-_@]+$"))
                errors.Add("Device Name contains invalid characters");

            return errors;
        }

        public void Sanitize()
        {
            EmployeeId       = SanitizeInput(EmployeeId)?.ToUpperInvariant();
            DeviceName       = SanitizeInput(DeviceName)?.ToUpperInvariant();
            EmployeeFullName = SanitizeInput(EmployeeFullName);
            Department       = SanitizeInput(Department);
            Position         = SanitizeInput(Position);
            ExistingDeviceName = SanitizeInput(ExistingDeviceName);
        }

        private static string SanitizeInput(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return input;
            input = Regex.Replace(input, @"<[^>]+>", string.Empty);
            input = Regex.Replace(input, @"<script[^>]*>[\s\S]*?</script>", string.Empty, RegexOptions.IgnoreCase);
            input = Regex.Replace(input, @"javascript:", string.Empty, RegexOptions.IgnoreCase);
            input = Regex.Replace(input, @"on\w+\s*=", string.Empty, RegexOptions.IgnoreCase);
            return input.Trim();
        }
    }
}
