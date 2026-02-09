using System.Collections.Generic;
using FaceAttend.Models;

namespace FaceAttend.Services.Storage
{
    public interface IEmployeeRepository
    {
        IReadOnlyList<EmployeeFaceRecord> GetAll();
        void Upsert(EmployeeFaceRecord record);
        void Delete(string employeeId);
    }
}
