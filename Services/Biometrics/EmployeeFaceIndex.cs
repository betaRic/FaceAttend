using System;
using System.Collections.Generic;
using System.Linq;

namespace FaceAttend.Services.Biometrics
{
    public static class EmployeeFaceIndex
    {
        public class Entry
        {
            public string EmployeeId { get; set; }
            public double[] Vec { get; set; }
        }

        private static volatile bool _loaded = false;
        private static List<Entry> _entries = new List<Entry>();
        private static readonly object _lock = new object();

        public static void Invalidate() { _loaded = false; }

        public static IReadOnlyList<Entry> GetEntries(FaceAttendDBEntities db)
        {
            if (!_loaded) Rebuild(db);
            lock (_lock) { return _entries.ToList(); }
        }

        public static void Rebuild(FaceAttendDBEntities db)
        {
            lock (_lock)
            {
                var list = new List<Entry>();

                foreach (var emp in db.Employees.Where(e => e.IsActive && e.FaceEncodingBase64 != null))
                {
                    try
                    {
                        var bytes = Convert.FromBase64String(emp.FaceEncodingBase64);
                        var vec = DlibBiometrics.DecodeFromBytes(bytes);
                        if (vec != null && vec.Length == 128)
                            list.Add(new Entry { EmployeeId = emp.EmployeeId, Vec = vec });
                    }
                    catch
                    {
                        // skip corrupt records
                    }
                }

                _entries = list;
                _loaded = true;
            }
        }
    }
}
