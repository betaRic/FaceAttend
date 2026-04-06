using System.ComponentModel.DataAnnotations;

namespace FaceAttend.Models.ViewModels.Mobile
{
    public class NewEmployeeEnrollmentVm
    {
        [Required(ErrorMessage = "Employee ID is required")]
        [StringLength(20, MinimumLength = 5, ErrorMessage = "Employee ID must be 5-20 characters")]
        [RegularExpression(@"^[A-Za-z0-9 \-]+$", ErrorMessage = "Employee ID contains invalid characters")]
        public string EmployeeId { get; set; }

        [Required(ErrorMessage = "First Name is required")]
        [StringLength(50, MinimumLength = 2, ErrorMessage = "First Name must be 2-50 characters")]
        [RegularExpression(@"^[A-Za-z .\-']+$", ErrorMessage = "First Name contains invalid characters")]
        public string FirstName { get; set; }

        [StringLength(50, ErrorMessage = "Middle Name must be 50 characters or less")]
        [RegularExpression(@"^[A-Za-z .\-']*$", ErrorMessage = "Middle Name contains invalid characters")]
        public string MiddleName { get; set; }

        [Required(ErrorMessage = "Last Name is required")]
        [StringLength(50, MinimumLength = 2, ErrorMessage = "Last Name must be 2-50 characters")]
        [RegularExpression(@"^[A-Za-z .\-']+$", ErrorMessage = "Last Name contains invalid characters")]
        public string LastName { get; set; }

        [Required(ErrorMessage = "Position is required")]
        [StringLength(100, MinimumLength = 2, ErrorMessage = "Position must be 2-100 characters")]
        [RegularExpression(@"^[A-Za-z0-9 .\-/(),]+$", ErrorMessage = "Position contains invalid characters")]
        public string Position { get; set; }

        [Required(ErrorMessage = "Department is required")]
        [StringLength(100, MinimumLength = 2, ErrorMessage = "Department must be 2-100 characters")]
        [RegularExpression(@"^[A-Za-z0-9 .\-/(),]+$", ErrorMessage = "Department contains invalid characters")]
        public string Department { get; set; }

        [Range(1, int.MaxValue, ErrorMessage = "Office is required")]
        public int OfficeId { get; set; }

        [Required(ErrorMessage = "Face enrollment is required")]
        public string FaceEncoding { get; set; }

        public string AllFaceEncodingsJson { get; set; }

        [Required(ErrorMessage = "Device Name is required")]
        [StringLength(50, MinimumLength = 2, ErrorMessage = "Device Name must be 2-50 characters")]
        [RegularExpression(@"^[A-Za-z0-9 ._@]+$", ErrorMessage = "Device Name contains invalid characters")]
        public string DeviceName { get; set; }
    }

    public class DeviceRegistrationVm
    {
        public string EmployeeId       { get; set; }
        public string EmployeeFullName { get; set; }
        public string Department       { get; set; }
        public string Position         { get; set; }
        public bool   IsNewEmployee    { get; set; }
        public int?   EmployeeDbId     { get; set; }
        public string Fingerprint      { get; set; }
        public bool   HasExistingDevice  { get; set; }
        public string ExistingDeviceName { get; set; }

        [Required(ErrorMessage = "Device Name is required")]
        [StringLength(50, MinimumLength = 2, ErrorMessage = "Device Name must be 2-50 characters")]
        [RegularExpression(@"^[A-Za-z0-9\s.\-_@]+$", ErrorMessage = "Device Name contains invalid characters")]
        public string DeviceName { get; set; }
    }
}
