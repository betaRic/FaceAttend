using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web;
using FaceAttend.Models;
using Newtonsoft.Json;

namespace FaceAttend.Services.Storage
{
    public class JsonEmployeeRepository : IEmployeeRepository
    {
        private static readonly object _lock = new object();
        private readonly string _path;

        public JsonEmployeeRepository()
        {
            var dir = ResolvePath("~/App_Data");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "employees.json");
        }

        public IReadOnlyList<EmployeeFaceRecord> GetAll()
        {
            lock (_lock)
            {
                return ReadAllUnsafe();
            }
        }

        public void Upsert(EmployeeFaceRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            if (string.IsNullOrWhiteSpace(record.EmployeeId)) throw new ArgumentException("EmployeeId required");

            record.EmployeeId = record.EmployeeId.Trim().ToUpperInvariant();

            lock (_lock)
            {
                var list = ReadAllUnsafe();
                var idx = list.FindIndex(x => string.Equals(x.EmployeeId, record.EmployeeId, StringComparison.OrdinalIgnoreCase));

                if (idx >= 0) list[idx] = record;
                else list.Insert(0, record);

                WriteAllUnsafe(list);
            }
        }

        public void Delete(string employeeId)
        {
            employeeId = (employeeId ?? "").Trim().ToUpperInvariant();
            if (employeeId.Length == 0) return;

            lock (_lock)
            {
                var list = ReadAllUnsafe();
                list = list.Where(x => !string.Equals(x.EmployeeId, employeeId, StringComparison.OrdinalIgnoreCase)).ToList();
                WriteAllUnsafe(list);
            }
        }

        private static string ResolvePath(string pathOrVirtual)
        {
            if (pathOrVirtual.StartsWith("~/"))
                return HttpContext.Current.Server.MapPath(pathOrVirtual);
            return pathOrVirtual;
        }

        private List<EmployeeFaceRecord> ReadAllUnsafe()
        {
            if (!File.Exists(_path)) return new List<EmployeeFaceRecord>();

            var json = File.ReadAllText(_path);
            if (string.IsNullOrWhiteSpace(json)) return new List<EmployeeFaceRecord>();

            try
            {
                return JsonConvert.DeserializeObject<List<EmployeeFaceRecord>>(json) ?? new List<EmployeeFaceRecord>();
            }
            catch
            {
                var bak = _path + ".bak_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                try { File.Copy(_path, bak, true); } catch { }
                return new List<EmployeeFaceRecord>();
            }
        }

        private void WriteAllUnsafe(List<EmployeeFaceRecord> list)
        {
            var json = JsonConvert.SerializeObject(list, Formatting.Indented);

            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);

            if (File.Exists(_path))
                File.Delete(_path);

            File.Move(tmp, _path);
        }
    }
}
