using System;
using System.Collections.Generic;
using System.Linq;

namespace FaceAttend.Services.Biometrics
{
    /// <summary>
    /// In-memory cache of face encodings for all active, enrolled employees.
    ///
    /// Fix applied vs. original:
    ///   TOCTOU race condition — the original checked <c>_loaded</c> outside the
    ///   lock and then called Rebuild() which re-acquired the same lock, allowing
    ///   two threads to both observe <c>!_loaded</c> and both invoke Rebuild()
    ///   simultaneously.  All state mutations now happen inside a single lock scope.
    /// </summary>
    public static class EmployeeFaceIndex
    {
        public class Entry
        {
            public string EmployeeId { get; set; }
            public double[] Vec { get; set; }
        }

        // _loaded is still volatile so the fast-path read in GetEntries()
        // is coherent across CPUs without always entering the lock.
        private static volatile bool _loaded = false;
        private static List<Entry> _entries = new List<Entry>();
        private static readonly object _lock = new object();

        /// <summary>
        /// Marks the index as stale.  The next call to GetEntries() will rebuild it.
        /// </summary>
        public static void Invalidate()
        {
            // Volatile write — visible to all threads immediately.
            _loaded = false;
        }

        /// <summary>
        /// Returns a snapshot of the current face index, rebuilding if stale.
        /// Thread-safe.
        /// </summary>
        public static IReadOnlyList<Entry> GetEntries(FaceAttendDBEntities db)
        {
            // Fast path: already loaded — avoid lock overhead.
            // Safe because _loaded is volatile and _entries is only replaced
            // (never mutated in place) inside the lock.
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
        /// Forces an immediate, synchronous rebuild.  Call after bulk changes.
        /// </summary>
        public static void Rebuild(FaceAttendDBEntities db)
        {
            lock (_lock)
            {
                RebuildCore(db);
            }
        }

        // -------------------------------------------------------------------
        // Private — must be called with _lock held.
        // -------------------------------------------------------------------

        private static void RebuildCore(FaceAttendDBEntities db)
        {
            var list = new List<Entry>();

            foreach (var emp in db.Employees
                         .Where(e => e.IsActive && e.FaceEncodingBase64 != null))
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
                    // Skip corrupt encodings; log in production.
                }
            }

            // Replace the list atomically.  Readers get either the old or new
            // snapshot — no torn reads because reference assignment is atomic
            // on all .NET-supported architectures.
            _entries = list;
            _loaded = true;
        }
    }
}
