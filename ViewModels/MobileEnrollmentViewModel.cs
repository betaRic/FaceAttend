using System.Collections.Generic;

namespace FaceAttend.ViewModels
{
    /// <summary>
    /// View model for mobile enrollment V2
    /// </summary>
    public class MobileEnrollmentViewModel
    {
        /// <summary>
        /// Current wizard step (1, 2, or 3)
        /// </summary>
        public int Step { get; set; } = 1;

        /// <summary>
        /// Employee ID
        /// </summary>
        public string EmployeeId { get; set; }

        /// <summary>
        /// First name
        /// </summary>
        public string FirstName { get; set; }

        /// <summary>
        /// Last name
        /// </summary>
        public string LastName { get; set; }

        /// <summary>
        /// Middle name
        /// </summary>
        public string MiddleName { get; set; }

        /// <summary>
        /// Office ID
        /// </summary>
        public int? OfficeId { get; set; }

        /// <summary>
        /// Device name
        /// </summary>
        public string DeviceName { get; set; } = "MY PHONE";

        /// <summary>
        /// List of offices for dropdown
        /// </summary>
        public List<OfficeViewModel> Offices { get; set; } = new List<OfficeViewModel>();
    }

    /// <summary>
    /// Simplified office view model
    /// </summary>
    public class OfficeViewModel
    {
        public int Id { get; set; }
        public string Name { get; set; }
    }
}
