using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web;
using FaceAttend.Models;
using Newtonsoft.Json;

namespace FaceAttend.Services.Storage
{
    public class JsonVisitorRepository : IVisitorRepository
    {
        private static readonly object _lock = new object();

        private static string ResolvePath(string pathOrVirtual)
        {
            if (pathOrVirtual.StartsWith("~/"))
                return HttpContext.Current.Server.MapPath(pathOrVirtual);
            return pathOrVirtual;
        }

        private readonly string _path;

        public JsonVisitorRepository()
        {
            var dir = ResolvePath("~/App_Data");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "visitors.json");
        }

        public void Add(VisitorLogRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));

            lock (_lock)
            {
                var list = ReadAllUnsafe();
                list.Insert(0, record); // newest first
                WriteAllUnsafe(list);
            }
        }

        public IReadOnlyList<VisitorLogRecord> GetAll()
        {
            lock (_lock)
            {
                return ReadAllUnsafe();
            }
        }

        private List<VisitorLogRecord> ReadAllUnsafe()
        {
            if (!File.Exists(_path)) return new List<VisitorLogRecord>();

            var json = File.ReadAllText(_path);
            if (string.IsNullOrWhiteSpace(json)) return new List<VisitorLogRecord>();

            try
            {
                return JsonConvert.DeserializeObject<List<VisitorLogRecord>>(json) ?? new List<VisitorLogRecord>();
            }
            catch
            {
                // If file is corrupted, keep a backup and start fresh
                var bak = _path + ".bak_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                try { File.Copy(_path, bak, true); } catch { }
                return new List<VisitorLogRecord>();
            }
        }

        private void WriteAllUnsafe(List<VisitorLogRecord> list)
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
