using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web;
using FaceAttend.Models;
using Newtonsoft.Json;

namespace FaceAttend.Services.Storage
{
    public class JsonEmployeeRepository : IEmployeeRepository
    {
        private static readonly object _inProcLock = new object();

        // Named mutex = cross-process safety (IIS multi-worker scenarios)
        private static readonly Mutex _mutex = new Mutex(false, @"Local\FaceAttend_EmployeesJson_v1");

        private readonly string _path;
        private const int KeepBackups = 10;

        public JsonEmployeeRepository()
        {
            var dir = ResolvePath("~/App_Data");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "employees.json");
        }

        public IReadOnlyList<EmployeeFaceRecord> GetAll()
        {
            WithFileLock(() => { });
            lock (_inProcLock)
            {
                return ReadAllUnsafe();
            }
        }

        public void Upsert(EmployeeFaceRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            if (string.IsNullOrWhiteSpace(record.EmployeeId)) throw new ArgumentException("EmployeeId required");

            record.EmployeeId = record.EmployeeId.Trim().ToUpperInvariant();

            WithFileLock(() =>
            {
                lock (_inProcLock)
                {
                    var list = ReadAllUnsafe();
                    var idx = list.FindIndex(x => string.Equals(x.EmployeeId, record.EmployeeId, StringComparison.OrdinalIgnoreCase));

                    if (idx >= 0) list[idx] = record;
                    else list.Insert(0, record);

                    WriteAllUnsafe(list);
                }
            });
        }

        public void Delete(string employeeId)
        {
            employeeId = (employeeId ?? "").Trim().ToUpperInvariant();
            if (employeeId.Length == 0) return;

            WithFileLock(() =>
            {
                lock (_inProcLock)
                {
                    var list = ReadAllUnsafe();
                    list = list.Where(x => !string.Equals(x.EmployeeId, employeeId, StringComparison.OrdinalIgnoreCase)).ToList();
                    WriteAllUnsafe(list);
                }
            });
        }

        private void WithFileLock(Action action)
        {
            bool acquired = false;
            try
            {
                try
                {
                    acquired = _mutex.WaitOne(TimeSpan.FromSeconds(10));
                }
                catch (AbandonedMutexException)
                {
                    acquired = true; // treat as acquired
                }

                if (!acquired) throw new TimeoutException("Could not lock employees.json");

                action();
            }
            finally
            {
                if (acquired)
                {
                    try { _mutex.ReleaseMutex(); } catch { }
                }
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

            string json;
            try
            {
                json = File.ReadAllText(_path);
            }
            catch
            {
                return new List<EmployeeFaceRecord>();
            }

            if (string.IsNullOrWhiteSpace(json)) return new List<EmployeeFaceRecord>();

            try
            {
                return JsonConvert.DeserializeObject<List<EmployeeFaceRecord>>(json) ?? new List<EmployeeFaceRecord>();
            }
            catch
            {
                var bak = _path + ".bak_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                try { File.Copy(_path, bak, true); } catch { }
                PruneBackups();
                return new List<EmployeeFaceRecord>();
            }
        }

        private void WriteAllUnsafe(List<EmployeeFaceRecord> list)
        {
            var json = JsonConvert.SerializeObject(list ?? new List<EmployeeFaceRecord>(), Formatting.Indented);

            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);

            if (File.Exists(_path))
            {
                var bak = _path + ".bak_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                File.Replace(tmp, _path, bak, true);
                PruneBackups();
                return;
            }

            File.Move(tmp, _path);
        }

        private void PruneBackups()
        {
            try
            {
                var dir = Path.GetDirectoryName(_path);
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;

                var baseName = Path.GetFileName(_path) + ".bak_";
                var files = Directory.EnumerateFiles(dir, Path.GetFileName(_path) + ".bak_*")
                    .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // keep newest N
                for (int i = KeepBackups; i < files.Count; i++)
                {
                    try { File.Delete(files[i]); } catch { }
                }
            }
            catch { }
        }
    }
}
