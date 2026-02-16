using System.ComponentModel.DataAnnotations;

namespace FaceAttend.Areas.Admin.Models
{
    public class EmployeeEditVm
    {
        public int Id { get; set; }

        [Required, StringLength(20)]
        [RegularExpression(@"^[A-Z0-9_-]{1,20}$", ErrorMessage = "Use A-Z, 0-9, _ or - (max 20).")]
        public string EmployeeId { get; set; }

        [Required, StringLength(100)]
        public string FirstName { get; set; }

        [StringLength(100)]
        public string MiddleName { get; set; }

        [Required, StringLength(100)]
        public string LastName { get; set; }

        [StringLength(120)]
        public string Position { get; set; }

        [StringLength(100)]
        public string Department { get; set; }

        [Required]
        public int OfficeId { get; set; }

        public bool IsFlexi { get; set; }
        public bool IsActive { get; set; } = true;
    }
}
