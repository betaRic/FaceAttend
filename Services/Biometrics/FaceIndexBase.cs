using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace FaceAttend.Services.Biometrics
{
    /// <summary>
    /// GENERIC base class for face indexes (Employee and Visitor)
    /// ELIMINATES code duplication between EmployeeFaceIndex and VisitorFaceIndex
    /// </summary>
    public abstract class FaceIndexBase<TEntry> where TEntry : class
    {
        // Volatile for thread-safe reads without lock
        protected volatile bool _loaded = false;
        protected List<TEntry> _entries = new List<TEntry>();
        protected readonly object _lock = new object();
        
        // Optional BallTree for fast O(log n) search
        protected BallTreeIndex _ballTree = null;
        protected readonly int _ballTreeThreshold;

        protected FaceIndexBase(int ballTreeThreshold = 50)
        {
            _ballTreeThreshold = ballTreeThreshold;
        }

        /// <summary>
        /// Mark index as stale - triggers rebuild on next access
        /// </summary>
        public void Invalidate()
        {
            _loaded = false;
        }

        /// <summary>
        /// Get all entries, rebuilding if necessary
        /// </summary>
        public IReadOnlyList<TEntry> GetEntries(FaceAttendDBEntities db)
        {
            // Fast path - no lock needed
            if (_loaded)
                return _entries.ToList();

            // Slow path - rebuild inside lock
            lock (_lock)
            {
                if (!_loaded)
                    RebuildCore(db);

                return _entries.ToList();
            }
        }

        /// <summary>
        /// Force immediate rebuild
        /// </summary>
        public void Rebuild(FaceAttendDBEntities db)
        {
            lock (_lock)
            {
                RebuildCore(db);
            }
        }

        /// <summary>
        /// Abstract method - implement in derived class to load entries from DB
        /// </summary>
        protected abstract List<TEntry> LoadEntriesFromDatabase(FaceAttendDBEntities db);

        /// <summary>
        /// Abstract method - get vector from entry for BallTree
        /// </summary>
        protected abstract double[] GetVectorFromEntry(TEntry entry);

        /// <summary>
        /// Abstract method - get ID from entry
        /// </summary>
        protected abstract string GetIdFromEntry(TEntry entry);

        /// <summary>
        /// Core rebuild logic
        /// </summary>
        private void RebuildCore(FaceAttendDBEntities db)
        {
            var list = LoadEntriesFromDatabase(db);
            
            // Replace atomically
            _entries = list;
            
            // Build BallTree if enough entries
            _ballTree = null;
            if (list.Count >= _ballTreeThreshold)
            {
                try
                {
                    var leafSize = ConfigurationService.GetInt("Biometrics:BallTreeLeafSize", 16);
                    _ballTree = new BallTreeIndex(
                        list.Select(e => new EmployeeFaceIndex.Entry 
                        { 
                            EmployeeId = GetIdFromEntry(e), 
                            Vec = GetVectorFromEntry(e) 
                        }).ToList(), 
                        leafSize);
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning("[FaceIndexBase] BallTree build failed: " + ex.Message);
                    _ballTree = null;
                }
            }

            _loaded = true;
        }

        /// <summary>
        /// Find nearest match using BallTree or linear scan
        /// </summary>
        public string FindNearest(FaceAttendDBEntities db, double[] vec, double tolerance, out double bestDist)
        {
            bestDist = double.PositiveInfinity;
            if (vec == null) return null;

            // Ensure loaded
            if (!_loaded)
            {
                lock (_lock)
                {
                    if (!_loaded)
                        RebuildCore(db);
                }
            }

            // Snapshot references
            var tree = _ballTree;
            var entries = _entries;

            // BallTree path (O log n)
            if (tree != null)
            {
                double dist;
                var id = tree.FindNearest(vec, tolerance, out dist);
                if (id != null)
                {
                    bestDist = dist;
                    return id;
                }
                return null;
            }

            // Linear fallback (O n)
            return FindNearestLinear(entries, vec, tolerance, out bestDist);
        }

        /// <summary>
        /// Linear scan fallback
        /// </summary>
        private string FindNearestLinear(List<TEntry> entries, double[] vec, double tolerance, out double bestDist)
        {
            string bestId = null;
            bestDist = double.PositiveInfinity;

            foreach (var entry in entries)
            {
                var entryVec = GetVectorFromEntry(entry);
                if (entryVec == null) continue;

                var d = DlibBiometrics.Distance(vec, entryVec);
                if (d < bestDist)
                {
                    bestDist = d;
                    bestId = GetIdFromEntry(entry);
                }
            }

            return bestDist <= tolerance ? bestId : null;
        }
    }
}
