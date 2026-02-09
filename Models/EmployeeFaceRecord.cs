namespace FaceAttend.Models
{
    public class EmployeeFaceRecord
    {
        public string EmployeeId { get; set; }          // uppercase expected later
        public string FaceTemplateBase64 { get; set; }  // Luxand template bytes
        public string CreatedUtc { get; set; }
    }
}
