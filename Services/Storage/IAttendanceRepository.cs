using System.Collections.Generic;
using FaceAttend.Models;

namespace FaceAttend.Services.Storage
{
    public interface IAttendanceRepository
    {
        void Add(AttendanceLogRecord record);
        IReadOnlyList<AttendanceLogRecord> GetAll();
    }
}
