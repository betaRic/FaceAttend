using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace FaceAttend.Services.Biometrics
{
    /// <summary>
    /// Visitor face index - REFACTORED to use FaceIndexBase
    /// </summary>
    public static class VisitorFaceIndex
    {
        public class Entry
        {
            public int VisitorId { get; set; }
            public string Name { get; set; }
            public double[] Vec { get; set; }
        }

        // Inner class implementing the base
        private class VisitorFaceIndexImpl : FaceIndexBase<Entry>
        {
            public VisitorFaceIndexImpl() : base(ConfigurationService.GetInt("Biometrics:BallTreeThreshold", 50))
            {
            }

            protected override List<Entry> LoadEntriesFromDatabase(FaceAttendDBEntities db)
            {
                var list = new List<Entry>();

                foreach (var v in db.Visitors
                    .Where(v => v.IsActive && v.FaceEncodingBase64 != null))
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

                return list;
            }

            protected override double[] GetVectorFromEntry(Entry entry) => entry.Vec;
            protected override string GetIdFromEntry(Entry entry) => entry.VisitorId.ToString();
        }

        // Singleton instance
        private static readonly VisitorFaceIndexImpl _instance = new VisitorFaceIndexImpl();

        // Public API - delegates to instance
        public static void Invalidate() => _instance.Invalidate();
        public static IReadOnlyList<Entry> GetEntries(FaceAttendDBEntities db) => _instance.GetEntries(db);
        public static void Rebuild(FaceAttendDBEntities db) => _instance.Rebuild(db);
    }
}
