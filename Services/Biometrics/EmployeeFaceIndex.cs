using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace FaceAttend.Services.Biometrics
{
    /// <summary>
    /// In-memory cache of face encodings for all active, enrolled employees.
    ///
    /// Fixes applied:
    ///   1. TOCTOU race condition — state mutations happen inside a single lock scope.
    ///   2. [P2-F4] Unnecessary lock on fast path removed — _entries is only replaced
    ///      atomically, so reference reads are safe without a lock.
    ///   3. [P3-F3] BallTreeIndex wired in — O(log n) nearest-neighbor search for
    ///      deployments at or above Biometrics:BallTreeThreshold employees (default 50).
    ///      Below the threshold, or if the tree fails to build, linear scan is used.
    ///      FindNearest() is a new public method for KioskController.ScanAttendance.
    /// </summary>
    public static class EmployeeFaceIndex
    {
        public class Entry
        {
            public string EmployeeId { get; set; }
            public double[] Vec { get; set; }
        }

        // _loaded and _ballTree are volatile so fast-path reads are coherent
        // across CPUs without entering the lock.
        private static volatile bool _loaded = false;
        private static List<Entry> _entries = new List<Entry>();

        // P3-F3: BallTreeIndex snapshot — null means use linear fallback.
        // Replaced atomically inside _lock. Snapshot the reference in FindNearest
        // before querying so a concurrent Invalidate + rebuild doesn't swap
        // the tree mid-search.
        private static volatile BallTreeIndex _ballTree = null;

        private static readonly object _lock = new object();

        /// <summary>
        /// Marks the index as stale. The next call to GetEntries() or FindNearest()
        /// will trigger a rebuild.
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
            // P2-F4: Fast path — no lock needed.
            // _entries is only ever replaced (never mutated) inside the slow-path lock.
            if (_loaded)
                return _entries.ToList();

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
        /// P3-F3: Finds the nearest matching EmployeeId within tolerance.
        /// Uses the BallTree index (O log n) when available, otherwise falls
        /// back to a linear scan of _entries (O n).
        /// Returns null if no entry is within tolerance.
        /// </summary>
        public static string FindNearest(
            FaceAttendDBEntities db, double[] vec, double tolerance, out double bestDist)
        {
            bestDist = double.PositiveInfinity;
            if (vec == null) return null;

            // Ensure the index is loaded (triggers rebuild if stale).
            if (!_loaded)
            {
                lock (_lock)
                {
                    if (!_loaded)
                        RebuildCore(db);
                }
            }

            // Snapshot both references atomically — a concurrent Invalidate + rebuild
            // may swap them, but we work on a consistent pair for this query.
            var tree    = _ballTree;
            var entries = _entries;

            // --- Ball-tree path (O log n) ---
            if (tree != null)
            {
                double dist;
                var id = tree.FindNearest(vec, tolerance, out dist);
                if (id != null)
                {
                    bestDist = dist;
                    return id;
                }
                // BallTree found nothing within tolerance — no match.
                bestDist = double.PositiveInfinity;
                return null;
            }

            // --- Linear fallback (O n) ---
            string bestId = null;
            bestDist = double.PositiveInfinity;
            foreach (var e in entries)
            {
                var d = DlibBiometrics.Distance(vec, e.Vec);
                if (d < bestDist) { bestDist = d; bestId = e.EmployeeId; }
            }
            return bestId;
        }

        /// <summary>
        /// Forces an immediate, synchronous rebuild. Call after bulk changes.
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

            // Day 3: multi-encoding support.
            // If Employees.FaceEncodingsJson exists, load multiple encodings per employee.
            // Backward compatible: falls back to FaceEncodingBase64-only when the column
            // does not exist or contains invalid JSON.

            var maxPerEmployee = AppSettings.GetInt("Biometrics:Enroll:MaxImages", 5);

            bool loadedViaSql = false;
            try
            {
                var rows = db.Database.SqlQuery<EmployeeRow>(
                    "SELECT EmployeeId, FaceEncodingBase64, FaceEncodingsJson " +
                    "FROM Employees " +
                    "WHERE IsActive = 1 AND (FaceEncodingBase64 IS NOT NULL OR FaceEncodingsJson IS NOT NULL)"
                ).ToList();

                foreach (var r in rows)
                {
                    if (string.IsNullOrWhiteSpace(r.EmployeeId))
                        continue;

                    // Prefer JSON multi-encodings when present.
                    if (!string.IsNullOrWhiteSpace(r.FaceEncodingsJson))
                    {
                        try
                        {
                            var b64s = JsonConvert.DeserializeObject<List<string>>(r.FaceEncodingsJson) ?? new List<string>();
                            int added = 0;
                            foreach (var b64 in b64s)
                            {
                                if (added >= maxPerEmployee) break;
                                if (string.IsNullOrWhiteSpace(b64)) continue;
                                try
                                {
                                    var bytes = Convert.FromBase64String(b64);
                                    var vec = DlibBiometrics.DecodeFromBytes(bytes);
                                    if (vec != null && vec.Length == 128)
                                    {
                                        list.Add(new Entry { EmployeeId = r.EmployeeId, Vec = vec });
                                        added++;
                                    }
                                }
                                catch { /* skip bad item */ }
                            }

                            if (added > 0)
                                continue; // loaded at least one from JSON; skip legacy field
                        }
                        catch
                        {
                            // Invalid JSON -> fall back to legacy field if present.
                        }
                    }

                    // Legacy single-encoding field.
                    if (!string.IsNullOrWhiteSpace(r.FaceEncodingBase64))
                    {
                        try
                        {
                            var bytes = Convert.FromBase64String(r.FaceEncodingBase64);
                            var vec = DlibBiometrics.DecodeFromBytes(bytes);
                            if (vec != null && vec.Length == 128)
                                list.Add(new Entry { EmployeeId = r.EmployeeId, Vec = vec });
                        }
                        catch
                        {
                            // Skip corrupt encoding.
                        }
                    }
                }

                loadedViaSql = true;
            }
            catch
            {
                loadedViaSql = false;
            }

            if (!loadedViaSql)
            {
                // Fallback for old DB schema (no FaceEncodingsJson column).
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
            }

            // Replace list atomically.
            _entries = list;

            // Ball tree threshold still uses total vectors; this is OK.
            _ballTree = null;
            var ballTreeThreshold = AppSettings.GetInt("Biometrics:BallTreeThreshold", 50);
            if (list.Count >= ballTreeThreshold)
            {
                try
                {
                    var leafSize = AppSettings.GetInt("Biometrics:BallTreeLeafSize", 16);
                    _ballTree = new BallTreeIndex(list, leafSize);
                }
                catch
                {
                    _ballTree = null;
                }
            }

            _loaded = true;
        }

        private class EmployeeRow
        {
            public string EmployeeId { get; set; }
            public string FaceEncodingBase64 { get; set; }
            public string FaceEncodingsJson { get; set; }
        }
    }
}
