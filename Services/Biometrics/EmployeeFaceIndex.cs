using System;
using System.Collections.Generic;
using System.Linq;
using FaceAttend.Models;
using FaceAttend.Services.Storage;

namespace FaceAttend.Services.Biometrics
{
    // In-process cache of decoded face vectors (per IIS worker process)
    public static class EmployeeFaceIndex
    {
        public sealed class Entry
        {
            public string EmployeeId { get; set; }
            public double[] Vec { get; set; }
            public string CreatedUtc { get; set; }
        }

        private static readonly object _lock = new object();
        private static volatile bool _loaded = false;
        private static List<Entry> _entries = new List<Entry>();

        public static void Invalidate()
        {
            _loaded = false;
        }

        public static IReadOnlyList<Entry> GetEntries(IEmployeeRepository repo)
        {
            EnsureLoaded(repo);
            lock (_lock)
            {
                // return a snapshot
                return _entries.ToList();
            }
        }

        public static void EnsureLoaded(IEmployeeRepository repo)
        {
            if (_loaded) return;
            Rebuild(repo);
        }

        public static void Rebuild(IEmployeeRepository repo)
        {
            if (repo == null) throw new ArgumentNullException(nameof(repo));

            lock (_lock)
            {
                var list = new List<Entry>();

                foreach (var r in repo.GetAll() ?? new List<EmployeeFaceRecord>())
                {
                    if (r == null) continue;
                    if (string.IsNullOrWhiteSpace(r.EmployeeId)) continue;
                    if (string.IsNullOrWhiteSpace(r.FaceTemplateBase64)) continue;

                    try
                    {
                        var bytes = Convert.FromBase64String(r.FaceTemplateBase64);
                        var vec = DlibBiometrics.DecodeFromBytes(bytes);
                        if (vec == null || vec.Length != 128) continue;

                        list.Add(new Entry
                        {
                            EmployeeId = r.EmployeeId.Trim().ToUpperInvariant(),
                            Vec = vec,
                            CreatedUtc = r.CreatedUtc
                        });
                    }
                    catch
                    {
                        // skip bad records
                    }
                }

                _entries = list;
                _loaded = true;
            }
        }
    }
}
