using System.ComponentModel.DataAnnotations;

namespace FaceAttend.Models.ViewModels.Admin
{
    public class OfficeEditVm
    {
        public int Id { get; set; }

        [StringLength(20)]
        public string Code { get; set; }

        [Required, StringLength(150)]
        public string Name { get; set; }

        [StringLength(50)]
        public string Type { get; set; }

        [StringLength(100)]
        public string ProvinceName { get; set; }

        [StringLength(100)]
        public string HUCCity { get; set; }

        [Required]
        [Range(-90, 90, ErrorMessage = "Latitude must be between -90 and 90.")]
        public double Latitude { get; set; }

        [Required]
        [Range(-180, 180, ErrorMessage = "Longitude must be between -180 and 180.")]
        public double Longitude { get; set; }

        [Required]
        [Range(1, 5000)]
        public int RadiusMeters { get; set; } = 100;

        [StringLength(100)]
        public string WiFiBSSID { get; set; }

        public bool IsActive { get; set; } = true;

        // ── Schedule ──────────────────────────────────────────────────────────
        /// <summary>Comma-separated ISO day numbers (1=Mon…7=Sun). NULL/empty = Mon–Fri default.</summary>
        [StringLength(20)]
        public string WorkDays { get; set; }

        /// <summary>Comma-separated ISO day numbers for WFH days. Must be subset of WorkDays.</summary>
        [StringLength(20)]
        public string WfhDays { get; set; }

        public bool WfhEnabled { get; set; }
    }
}
