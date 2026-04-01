using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FaceAttend.Models.ViewModels.Mobile;

namespace FaceAttend.Services.Mobile
{
    /// <summary>
    /// Business logic for the employee self-service portal:
    /// monthly report assembly and CSV export generation.
    /// </summary>
    public static class EmployeePortalService
    {
        /// <summary>
        /// Builds a day-by-day attendance report for the month containing <paramref name="todayLocal"/>.
        /// </summary>
        public static List<DailyAttendanceVm> BuildMonthlyAttendanceReport(
            List<AttendanceLog> monthLogs,
            DateTime todayLocal)
        {
            var report      = new List<DailyAttendanceVm>();
            var currentYear  = todayLocal.Year;
            var currentMonth = todayLocal.Month;
            var daysInMonth  = DateTime.DaysInMonth(currentYear, currentMonth);

            var logsByDate = monthLogs
                .GroupBy(l => l.Timestamp.Date)
                .ToDictionary(g => g.Key, g => g.OrderBy(l => l.Timestamp).ToList());

            for (int day = 1; day <= daysInMonth; day++)
            {
                var date      = new DateTime(currentYear, currentMonth, day);
                var isWeekend = date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday;
                var isFuture  = date > todayLocal;

                var dayRecord = new DailyAttendanceVm
                {
                    Date        = date,
                    DayOfWeek   = date.ToString("ddd"),
                    DateDisplay = date.ToString("MMM dd"),
                    IsWeekend   = isWeekend,
                    Status      = isFuture ? "-" : (isWeekend ? "Weekend" : "Absent")
                };

                if (logsByDate.TryGetValue(date, out var dayLogs))
                {
                    var timeIn  = dayLogs.FirstOrDefault(l => l.EventType == "IN");
                    var timeOut = dayLogs.LastOrDefault(l => l.EventType == "OUT");

                    if (timeIn != null)
                    {
                        dayRecord.TimeIn = timeIn.Timestamp.ToString("h:mm tt");
                        dayRecord.Office = timeIn.Office?.Name;
                    }

                    if (timeOut != null && timeOut != timeIn)
                        dayRecord.TimeOut = timeOut.Timestamp.ToString("h:mm tt");

                    if (timeIn != null && timeOut != null && timeOut != timeIn)
                    {
                        var duration = timeOut.Timestamp - timeIn.Timestamp;
                        dayRecord.HoursWorked = Math.Round(duration.TotalHours, 2);
                        dayRecord.Status      = "Present";
                    }
                    else if (timeIn != null)
                    {
                        dayRecord.Status = date.Date == todayLocal.Date ? "In Progress" : "Incomplete";
                    }
                }

                report.Add(dayRecord);
            }

            return report.OrderByDescending(r => r.Date).ToList();
        }

        /// <summary>
        /// Builds a CSV export of attendance logs for the given month.
        /// Returns the UTF-8 encoded bytes ready to stream as a file download.
        /// </summary>
        public static byte[] BuildCsvBytes(
            Employee employee,
            IEnumerable<AttendanceLog> logs,
            DateTime targetMonth)
        {
            var logList = logs.ToList();
            var csv     = new StringBuilder();

            csv.AppendLine("Employee ID,Full Name,Position,Department");
            csv.AppendLine($"\"{employee.EmployeeId}\",\"{employee.FirstName} {employee.LastName}\",\"{employee.Position}\",\"{employee.Department}\"");
            csv.AppendLine();
            csv.AppendLine($"Attendance Report for {targetMonth.ToString("MMMM yyyy")}");
            csv.AppendLine();
            csv.AppendLine("Date,Time,Event Type,Office,Duration (minutes)");

            foreach (var log in logList)
                csv.AppendLine($"\"{log.Timestamp:yyyy-MM-dd}\",\"{log.Timestamp:HH:mm:ss}\",\"{log.EventType}\",\"{log.Office?.Name}\",\"-\"");

            csv.AppendLine();
            csv.AppendLine($"Total Entries: {logList.Count}");
            csv.AppendLine($"Total Days Present: {logList.Where(l => l.EventType == "IN").Select(l => l.Timestamp.Date).Distinct().Count()}");
            csv.AppendLine($"Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");

            return Encoding.UTF8.GetBytes(csv.ToString());
        }
    }
}
