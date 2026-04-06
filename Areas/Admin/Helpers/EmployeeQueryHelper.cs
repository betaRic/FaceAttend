using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using FaceAttend.Models.Dtos;

namespace FaceAttend.Areas.Admin.Helpers
{
    public static class EmployeeQueryHelper
    {
        public static List<EmployeeListRowDto> QueryRows(FaceAttendDBEntities db, string searchTerm, string status)
        {
            var term = (searchTerm ?? "").Trim();
            var like = "%" + term + "%";

            return db.Database.SqlQuery<EmployeeListRowDto>(@"
SELECT e.Id,
       e.EmployeeId,
       e.FirstName,
       e.MiddleName,
       e.LastName,
       e.Position,
       e.Department,
       e.OfficeId,
       ISNULL(o.Name, '-') AS OfficeName,
       e.IsFlexi,
       CASE WHEN e.FaceEncodingBase64 IS NOT NULL OR e.FaceEncodingsJson IS NOT NULL THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS HasFace,
       ISNULL(e.[Status], 'INACTIVE') AS [Status],
       e.CreatedDate
FROM dbo.Employees e
LEFT JOIN dbo.Offices o ON o.Id = e.OfficeId
WHERE (@term = ''
       OR e.EmployeeId LIKE @like
       OR e.FirstName LIKE @like
       OR e.LastName LIKE @like
       OR ISNULL(e.MiddleName, '') LIKE @like
       OR ISNULL(e.Department, '') LIKE @like
       OR ISNULL(e.Position, '') LIKE @like)
  AND (@status = 'ALL'
       OR ISNULL(e.[Status], 'INACTIVE') = @status)
ORDER BY CASE WHEN ISNULL(e.[Status], 'INACTIVE') = 'PENDING' THEN 0 ELSE 1 END,
         e.LastName,
         e.FirstName,
         e.EmployeeId",
                new SqlParameter("@term", term),
                new SqlParameter("@like", like),
                new SqlParameter("@status", status)).ToList();
        }

        public static string NormalizeStatus(string status)
        {
            var normalized = (status ?? "ACTIVE").Trim().ToUpperInvariant();
            if (normalized != "ACTIVE" && normalized != "PENDING" && normalized != "INACTIVE" && normalized != "ALL")
                normalized = "ACTIVE";
            return normalized;
        }
    }
}
