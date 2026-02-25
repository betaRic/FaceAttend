using System;
using System.Collections.Generic;
using System.Linq;

namespace FaceAttend.Services.Biometrics
{
    /// <summary>
    /// In-memory cache of face encodings for all active, enrolled Visitor profiles.
    ///
    /// Mirrors EmployeeFaceIndex pattern exactly:
    ///   - Volatile _loaded flag for cheap fast-path check without lock.
    ///   - Single _lock protects all state mutations.
    ///   - Double-checked locking prevents redundant rebuilds under contention.
    ///   - _entries is replaced atomically (never mutated in place) so concurrent
    ///     readers always get a consistent snapshot.
    ///
    /// Deliberately separated from EmployeeFaceIndex so that:
    ///   1. Visitor enrollment does not invalidate the employee index and vice versa.
    ///   2. Visitor tolerance can be tuned independently in a future phase.
    ///   3. Linear O(n) scan is fine for typical visitor counts (under ~200).
    ///      If the visitor roster grows larger, add BallTree here following
    ///      the same pattern as the employee FaceMatchTuner service.
    /// </summary>
    public static class VisitorFaceIndex
    {
        // ── Entry ────────────────────────────────────────────────────────────────

        public class Entry
        {
            public int      VisitorId { get; set; }
            public string   Name      { get; set; }
            public double[] Vec       { get; set; }
        }

        // ── State ────────────────────────────────────────────────────────────────

        // Volatile so the fast-path read in GetEntries() is coherent across CPUs
        // without always entering the lock.
        private static volatile bool _loaded  = false;
        private static List<Entry>   _entries = new List<Entry>();
        private static readonly object _lock  = new object();

        // ── Public API ───────────────────────────────────────────────────────────

        /// <summary>
        /// Marks the index as stale.
        /// Call after any write to Visitor.FaceEncodingBase64 or Visitor.IsActive.
        /// The next call to GetEntries() will trigger a rebuild from the database.
        /// </summary>
        public static void Invalidate()
        {
            _loaded = false;   // volatile write — visible to all threads immediately
        }

        /// <summary>
        /// Returns a snapshot of the current visitor face index.
        /// Rebuilds from the database if the index is stale.  Thread-safe.
        /// </summary>
        public static IReadOnlyList<Entry> GetEntries(FaceAttendDBEntities db)
        {
            // Fast path: already loaded — return without taking the lock.
            if (_loaded)
            {
                lock (_lock) { return _entries.ToList(); }
            }

            // Slow path: rebuild inside the lock so only one thread does the work.
            lock (_lock)
            {
                // Double-check: another thread may have rebuilt while we waited.
                if (!_loaded)
                    RebuildCore(db);

                return _entries.ToList();
            }
        }

        /// <summary>
        /// Forces an immediate synchronous rebuild.  Call after bulk changes.
        /// </summary>
        public static void Rebuild(FaceAttendDBEntities db)
        {
            lock (_lock)
            {
                RebuildCore(db);
            }
        }

        // ── Private ──────────────────────────────────────────────────────────────

        /// <summary>Must only be called with _lock held.</summary>
        private static void RebuildCore(FaceAttendDBEntities db)
        {
            var list = new List<Entry>();

            foreach (var v in db.Visitors
                         .Where(v => v.IsActive && v.FaceEncodingBase64 != null))
            {
                try
                {
                    var bytes = Convert.FromBase64String(v.FaceEncodingBase64);
                    var vec   = DlibBiometrics.DecodeFromBytes(bytes);
                    if (vec != null && vec.Length == 128)
                        list.Add(new Entry
                        {
                            VisitorId = v.Id,
                            Name      = v.Name,
                            Vec       = vec
                        });
                }
                catch
                {
                    // Skip corrupt encodings.  Log to application event log in production.
                }
            }

            // Replace the reference atomically.  Readers receive either the old or
            // the new snapshot — no torn reads because reference assignment is atomic
            // on all .NET-supported architectures.
            _entries = list;
            _loaded  = true;
        }
    }
}
