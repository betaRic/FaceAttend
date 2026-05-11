using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Text;
using FaceAttend.Models.ViewModels.Mobile;
using FaceAttend.Services.Helpers;

namespace FaceAttend.Services.Mobile
{
    /// <summary>
    /// Business logic for the employee self-service portal:
    /// monthly report assembly and CSV export generation.
    /// </summary>
    public static class EmployeePortalService
    {
        public static EmployeePortalVm BuildPortalVm(
            FaceAttendDBEntities db,
            Employee employee,
            DateTime todayLocal,
            DateTime? accessExpiresUtc = null)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (employee == null) throw new ArgumentNullException(nameof(employee));

            var todayRange = TimeZoneHelper.LocalDateRange(todayLocal);
            var todayLogs = db.AttendanceLogs
                .Include("Office")
                .Where(l => l.EmployeeId == employee.Id &&
                            !l.IsVoided &&
                            l.Timestamp >= todayRange.fromLocalInclusive &&
                            l.Timestamp < todayRange.toLocalExclusive)
                .OrderBy(l => l.Timestamp)
                .ToList();

            var firstDayOfMonth = new DateTime(todayLocal.Year, todayLocal.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
            var firstDayOfNextMonth = firstDayOfMonth.AddMonths(1);

            var monthLogs = db.AttendanceLogs
                .Include("Office")
                .Where(l => l.EmployeeId == employee.Id &&
                            !l.IsVoided &&
                            l.Timestamp >= firstDayOfMonth &&
                            l.Timestamp < firstDayOfNextMonth)
                .OrderBy(l => l.Timestamp)
                .ToList();

            var totalDaysPresent = monthLogs
                .Where(l => l.EventType == "IN")
                .Select(l => l.Timestamp.Date)
                .Distinct()
                .Count();

            var totalEstimatedHours = totalDaysPresent * 8.0;
            var lastAttendance = todayLogs.LastOrDefault();
            var recentLogs = monthLogs
                .OrderByDescending(l => l.Timestamp)
                .Take(10)
                .OrderBy(l => l.Timestamp)
                .Select(l => new RecentAttendanceVm
                {
                    Date = l.Timestamp.ToString("MMM dd"),
                    Time = l.Timestamp.ToString("h:mm tt"),
                    Type = l.EventType,
                    Office = l.Office != null ? l.Office.Name : "Unknown"
                })
                .ToList();

            return new EmployeePortalVm
            {
                EmployeeId = employee.EmployeeId,
                FullName = employee.FirstName + " " + employee.LastName,
                Position = StringHelper.SanitizeDisplayText(employee.Position),
                Department = StringHelper.SanitizeDisplayText(employee.Department),
                OfficeName = StringHelper.SanitizeDisplayText(employee.Office != null ? employee.Office.Name : null),
                RecordAccessMode = "Fresh scan",
                AccessExpiresAtUtc = accessExpiresUtc,

                TodayStatus = lastAttendance != null && lastAttendance.EventType == "IN" ? "Timed In" :
                              lastAttendance != null && lastAttendance.EventType == "OUT" ? "Timed Out" : "Not Yet",
                LastScanTime = lastAttendance != null ? lastAttendance.Timestamp.ToString("h:mm tt") : null,

                TotalDaysPresent = totalDaysPresent,
                TotalHours = Math.Round(totalEstimatedHours, 1),
                AverageHoursPerDay = totalDaysPresent > 0
                    ? Math.Round(totalEstimatedHours / totalDaysPresent, 1)
                    : 0,

                RecentEntries = recentLogs,
                MonthlyReport = BuildMonthlyAttendanceReport(monthLogs, todayLocal),

                CurrentMonth = todayLocal.ToString("yyyy_MM"),
                CurrentMonthDisplay = todayLocal.ToString("MMMM yyyy")
            };
        }

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
            csv.AppendLine(CsvHelper.JoinCsv(new[]
            {
                CsvHelper.SafeCell(employee.EmployeeId),
                CsvHelper.SafeCell((employee.FirstName + " " + employee.LastName).Trim()),
                CsvHelper.SafeCell(employee.Position),
                CsvHelper.SafeCell(employee.Department)
            }));
            csv.AppendLine();
            csv.AppendLine(CsvHelper.JoinCsv(new[] { "Attendance Report for " + targetMonth.ToString("MMMM yyyy") }));
            csv.AppendLine();
            csv.AppendLine("Date,Time,Event Type,Office,Duration (minutes)");

            foreach (var log in logList)
            {
                csv.AppendLine(CsvHelper.JoinCsv(new[]
                {
                    log.Timestamp.ToString("yyyy-MM-dd"),
                    log.Timestamp.ToString("HH:mm:ss"),
                    CsvHelper.SafeCell(log.EventType),
                    CsvHelper.SafeCell(log.Office?.Name),
                    "-"
                }));
            }

            csv.AppendLine();
            csv.AppendLine(CsvHelper.JoinCsv(new[] { "Total Entries: " + logList.Count }));
            csv.AppendLine(CsvHelper.JoinCsv(new[]
            {
                "Total Days Present: " + logList
                    .Where(l => l.EventType == "IN")
                    .Select(l => l.Timestamp.Date)
                    .Distinct()
                    .Count()
            }));
            csv.AppendLine(CsvHelper.JoinCsv(new[] { "Generated: " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + " UTC" }));

            return Encoding.UTF8.GetBytes(csv.ToString());
        }
    }
}
