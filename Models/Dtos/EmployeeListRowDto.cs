using System;

namespace FaceAttend.Models.Dtos
{
    /// <summary>
    /// Slim employee row for list views (SQL query result)
    /// </summary>
    public class EmployeeListRowDto
    {
        public int Id { get; set; }
        public string EmployeeId { get; set; }
        public string FirstName { get; set; }
        public string MiddleName { get; set; }
        public string LastName { get; set; }
        public string Position { get; set; }
        public string Department { get; set; }
        public int OfficeId { get; set; }
        public string OfficeName { get; set; }
        public bool IsFlexi { get; set; }
        public bool HasFace { get; set; }
        public string Status { get; set; }
        public DateTime CreatedDate { get; set; }
    }
}
