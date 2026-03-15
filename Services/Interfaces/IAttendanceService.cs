using System;

namespace FaceAttend.Services.Interfaces
{
    /// <summary>
    /// Interface for attendance recording service
    /// </summary>
    public interface IAttendanceService
    {
        /// <summary>
        /// Records an attendance event (Time In or Time Out)
        /// </summary>
        /// <param name="log">The attendance log entry to record</param>
        /// <param name="attemptedAtUtc">Optional timestamp when the attempt was made</param>
        /// <returns>Result indicating success/failure with event details</returns>
        AttendanceService.RecordResult Record(AttendanceLog log, DateTime? attemptedAtUtc = null);
    }
}
