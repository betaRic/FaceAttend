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
    public class JsonAttendanceRepository : IAttendanceRepository
    {
        private static readonly object _inProcLock = new object();
        private static readonly Mutex _mutex = new Mutex(false, @"Local\FaceAttend_AttendanceJson_v1");

        private readonly string _path;
        private const int KeepBackups = 10;

        public JsonAttendanceRepository()
        {
            var dir = ResolvePath("~/App_Data");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "attendance.json");
        }

        public void Add(AttendanceLogRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));

            WithFileLock(() =>
            {
                lock (_inProcLock)
                {
                    var list = ReadAllUnsafe();
                    list.Insert(0, record); // newest first
                    WriteAllUnsafe(list);
                }
            });
        }

        public IReadOnlyList<AttendanceLogRecord> GetAll()
        {
            WithFileLock(() => { });
            lock (_inProcLock)
            {
                return ReadAllUnsafe();
            }
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
                    acquired = true;
                }

                if (!acquired) throw new TimeoutException("Could not lock attendance.json");
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

        private List<AttendanceLogRecord> ReadAllUnsafe()
        {
            if (!File.Exists(_path)) return new List<AttendanceLogRecord>();

            string json;
            try { json = File.ReadAllText(_path); }
            catch { return new List<AttendanceLogRecord>(); }

            if (string.IsNullOrWhiteSpace(json)) return new List<AttendanceLogRecord>();

            try
            {
                return JsonConvert.DeserializeObject<List<AttendanceLogRecord>>(json) ?? new List<AttendanceLogRecord>();
            }
            catch
            {
                var bak = _path + ".bak_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                try { File.Copy(_path, bak, true); } catch { }
                PruneBackups();
                return new List<AttendanceLogRecord>();
            }
        }

        private void WriteAllUnsafe(List<AttendanceLogRecord> list)
        {
            var json = JsonConvert.SerializeObject(list ?? new List<AttendanceLogRecord>(), Formatting.Indented);

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

                var files = Directory.EnumerateFiles(dir, Path.GetFileName(_path) + ".bak_*")
                    .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                for (int i = KeepBackups; i < files.Count; i++)
                {
                    try { File.Delete(files[i]); } catch { }
                }
            }
            catch { }
        }
    }
}
