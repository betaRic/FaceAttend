using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace FaceAttend.Services.Biometrics
{
    public static class VisitorFaceIndex
    {
        public class Entry
        {
            public int VisitorId { get; set; }
            public string Name { get; set; }
            public double[] Vec { get; set; }
        }

        private static readonly object Sync = new object();
        private static volatile bool _loaded;
        private static List<Entry> _entries = new List<Entry>();

        public static void Invalidate()
        {
            _loaded = false;
        }

        public static IReadOnlyList<Entry> GetEntries(FaceAttendDBEntities db)
        {
            if (!_loaded)
            {
                lock (Sync)
                {
                    if (!_loaded)
                        RebuildCore(db);
                }
            }

            return _entries.ToList();
        }

        public static void Rebuild(FaceAttendDBEntities db)
        {
            lock (Sync)
            {
                RebuildCore(db);
            }
        }

        private static void RebuildCore(FaceAttendDBEntities db)
        {
            var list = new List<Entry>();

            foreach (var v in db.Visitors.Where(v => v.IsActive && v.FaceEncodingBase64 != null))
            {
                try
                {
                    byte[] bytes;
                    if (!BiometricCrypto.TryGetBytesFromStoredBase64(v.FaceEncodingBase64, out bytes))
                        continue;

                    var vec = FaceVectorCodec.DecodeFromBytes(bytes);
                    if (FaceVectorCodec.IsValidVector(vec))
                    {
                        list.Add(new Entry
                        {
                            VisitorId = v.Id,
                            Name = v.Name,
                            Vec = vec
                        });
                    }
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning("[VisitorFaceIndex] Skip visitor " + v.Id + ": " + ex.Message);
                }
            }

            _entries = list;
            _loaded = true;
        }
    }
}
