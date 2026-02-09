using System.Collections.Generic;
using FaceAttend.Models;

namespace FaceAttend.Services.Storage
{
    public interface IVisitorRepository
    {
        void Add(VisitorLogRecord record);
        IReadOnlyList<VisitorLogRecord> GetAll();
    }
}
