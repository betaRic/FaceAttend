using System.ComponentModel.DataAnnotations;

namespace FaceAttend.Areas.Admin.Models
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
        public double Latitude { get; set; }

        [Required]
        public double Longitude { get; set; }

        [Required]
        [Range(1, 5000)]
        public int RadiusMeters { get; set; } = 100;

        [StringLength(100)]
        public string WiFiSSID { get; set; }

        public bool IsActive { get; set; } = true;
    }
}
