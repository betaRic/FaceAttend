using System;
using System.Collections.Generic;

namespace FaceAttend.Models.ViewModels.Mobile
{
    public class EmployeePortalVm
    {
        public string EmployeeId  { get; set; }
        public string FullName    { get; set; }
        public string Position    { get; set; }
        public string Department  { get; set; }
        public string OfficeName  { get; set; }
        public string DeviceName  { get; set; }

        public string TodayStatus  { get; set; }
        public string LastScanTime { get; set; }

        public int    TotalDaysPresent   { get; set; }
        public double TotalHours         { get; set; }
        public double AverageHoursPerDay { get; set; }

        public List<RecentAttendanceVm> RecentEntries { get; set; }
        public List<DailyAttendanceVm>  MonthlyReport { get; set; }

        public string CurrentMonth        { get; set; }
        public string CurrentMonthDisplay { get; set; }
    }

    public class RecentAttendanceVm
    {
        public string Date   { get; set; }
        public string Time   { get; set; }
        public string Type   { get; set; }
        public string Office { get; set; }
    }

    public class DailyAttendanceVm
    {
        public DateTime Date        { get; set; }
        public string   DayOfWeek   { get; set; }
        public string   DateDisplay { get; set; }
        public string   TimeIn      { get; set; }
        public string   TimeOut     { get; set; }
        public double?  HoursWorked { get; set; }
        public string   Status      { get; set; }
        public bool     IsWeekend   { get; set; }
        public string   Office      { get; set; }
    }
}
