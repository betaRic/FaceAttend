using System.Collections.Generic;

namespace FaceAttend.Models.ViewModels.Admin
{
    public class OfficeBulkScheduleVm
    {
        public List<OfficeBulkScheduleRowVm> Offices { get; set; }
    }

    public class OfficeBulkScheduleRowVm
    {
        public int    Id           { get; set; }
        public string Name         { get; set; }
        public string ProvinceName { get; set; }
        public bool   WfhEnabled   { get; set; }

        /// <summary>Working day flags [0]=Mon … [6]=Sun.</summary>
        public bool[] WorkDays { get; set; }

        /// <summary>WFH day flags [0]=Mon … [6]=Sun.</summary>
        public bool[] WfhDays  { get; set; }
    }
}
