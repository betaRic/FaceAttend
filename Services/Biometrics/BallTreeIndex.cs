using System;
using System.Collections.Generic;
using System.Linq;

namespace FaceAttend.Services.Biometrics
{
    /// <summary>
    /// Ball-tree nearest-neighbor index for 128-d face vectors.
    /// Used when employee count exceeds Biometrics:BallTreeThreshold (default 50).
    /// O(log n) search vs O(n) linear scan. Returns double.PositiveInfinity when no match found.
    /// </summary>
    public sealed class BallTreeIndex
    {
        private readonly Node _root;
        private readonly int  _leafSize;

        public BallTreeIndex(IEnumerable<EmployeeFaceIndex.Entry> entries, int leafSize)
        {
            if (entries == null) throw new ArgumentNullException(nameof(entries));

            _leafSize = Math.Max(4, Math.Min(64, leafSize));

            var pts = entries
                .Where(e => e != null && e.Vec != null)
                .Select(e => new Point(e.EmployeeId, e.Vec))
                .ToList();

            if (pts.Count == 0)
                throw new InvalidOperationException("No points to index");

            _root = BuildTree(pts);
        }

        /// <summary>Returns the nearest employee ID within maxDistance, or null. Sets distance to PositiveInfinity if no match.</summary>
        public string FindNearest(double[] query, double maxDistance, out double distance)
        {
            distance = double.PositiveInfinity;
            if (query == null) return null;

            var bestId   = (string)null;
            var bestDist = maxDistance;
            Search(_root, query, ref bestId, ref bestDist);

            if (bestId == null) return null;
            distance = bestDist;
            return bestId;
        }

        // ---------------------------------------------------------------------------
        // Private — tree construction
        // ---------------------------------------------------------------------------

        private Node BuildTree(List<Point> points)
        {
            var node = new Node();
            node.Center = ComputeCenter(points);
            node.Radius = ComputeRadius(points, node.Center);

            if (points.Count <= _leafSize)
            {
                node.IsLeaf = true;
                node.Points = points;
                return node;
            }

            int splitDim = FindMaxVarianceDimension(points);
            points.Sort((a, b) => a.Vector[splitDim].CompareTo(b.Vector[splitDim]));

            int mid   = points.Count / 2;
            node.IsLeaf         = false;
            node.SplitDimension = splitDim;
            node.SplitValue     = points[mid].Vector[splitDim];
            node.Left           = BuildTree(points.Take(mid).ToList());
            node.Right          = BuildTree(points.Skip(mid).ToList());

            return node;
        }

        // ---------------------------------------------------------------------------
        // Private — tree search
        // ---------------------------------------------------------------------------

        private static void Search(Node node, double[] query, ref string bestId, ref double bestDist)
        {
            if (node == null) return;

            // Prune: skip this subtree if its closest possible point is farther than current best.
            if (Distance(query, node.Center) - node.Radius > bestDist) return;

            if (node.IsLeaf)
            {
                for (int i = 0; i < node.Points.Count; i++)
                {
                    var d = Distance(query, node.Points[i].Vector);
                    if (d <= bestDist) { bestDist = d; bestId = node.Points[i].EmployeeId; }
                }
                return;
            }

            var qv     = query[node.SplitDimension];
            var first  = qv <= node.SplitValue ? node.Left  : node.Right;
            var second = qv <= node.SplitValue ? node.Right : node.Left;
            Search(first,  query, ref bestId, ref bestDist);
            Search(second, query, ref bestId, ref bestDist);
        }

        // ---------------------------------------------------------------------------
        // Private — geometry helpers
        // ---------------------------------------------------------------------------

        private static int FindMaxVarianceDimension(List<Point> points)
        {
            int dim     = points[0].Vector.Length;
            int bestDim = 0;
            double bestVar = double.MinValue;

            for (int d = 0; d < dim; d++)
            {
                double mean = 0;
                for (int i = 0; i < points.Count; i++)
                    mean += points[i].Vector[d];
                mean /= points.Count;

                double variance = 0;
                for (int i = 0; i < points.Count; i++)
                {
                    var diff = points[i].Vector[d] - mean;
                    variance += diff * diff;
                }
                variance /= points.Count;

                if (variance > bestVar)
                {
                    bestVar = variance;
                    bestDim = d;
                }
            }

            return bestDim;
        }

        private static double[] ComputeCenter(List<Point> points)
        {
            int dim    = points[0].Vector.Length;
            var center = new double[dim];

            for (int d = 0; d < dim; d++)
            {
                double sum = 0;
                for (int i = 0; i < points.Count; i++)
                    sum += points[i].Vector[d];
                center[d] = sum / points.Count;
            }

            return center;
        }

        private static double ComputeRadius(List<Point> points, double[] center)
        {
            double max = 0;
            for (int i = 0; i < points.Count; i++)
            {
                var d = Distance(points[i].Vector, center);
                if (d > max) max = d;
            }
            return max;
        }

        private static double Distance(double[] a, double[] b)
        {
            double sum = 0;
            for (int i = 0; i < a.Length; i++)
            {
                var d = a[i] - b[i];
                sum += d * d;
            }
            return Math.Sqrt(sum);
        }

        // ---------------------------------------------------------------------------
        // Private nested types
        // ---------------------------------------------------------------------------

        private sealed class Point
        {
            public Point(string employeeId, double[] vector)
            {
                EmployeeId = employeeId;
                Vector     = vector;
            }

            public string   EmployeeId { get; private set; }
            public double[] Vector     { get; private set; }
        }

        /// <summary>Isang node sa BallTree — maaaring internal node o leaf node.</summary>
        private sealed class Node
        {
            // Para sa lahat ng nodes: bounding ball
            public double[] Center;
            public double   Radius;

            // Para sa leaf nodes: listahan ng points
            public bool        IsLeaf;
            public List<Point> Points;

            // Para sa internal nodes: split info at child nodes
            public int    SplitDimension;
            public double SplitValue;
            public Node   Left;
            public Node   Right;
        }
    }
}
