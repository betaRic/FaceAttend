using System;
using System.Collections.Generic;
using System.Linq;

namespace FaceAttend.Services.Biometrics
{
    /// <summary>
    /// Ball-tree index para sa mabilis na nearest-neighbor search sa 128-dimensional face vectors.
    ///
    /// Ginagamit kapag ang bilang ng empleyado ay umabot sa Biometrics:BallTreeThreshold (default: 50).
    /// Mas mabilis kaysa sa linear scan sa malalaking sets: O(log n) vs O(n).
    ///
    /// Paraan ng paggamit:
    ///   var tree = new BallTreeIndex(entries, leafSize: 16);
    ///   double dist;
    ///   string id = tree.FindNearest(queryVec, tolerance: 0.6, out dist);
    ///   // Kung null ang id, walang nahanap sa loob ng tolerance.
    ///
    /// FIX (HIGH-03): Pinalitan ang double.MaxValue ng double.PositiveInfinity bilang
    ///   "walang match" sentinel. Ang EmployeeFaceIndex.FindNearest() ay gumagamit ng
    ///   double.PositiveInfinity, kaya inconsistent ang dalawa. Maaaring magdulot ng
    ///   bugs sa code na nagko-compare ng mga distance values mula sa magkaibang source.
    /// </summary>
    public sealed class BallTreeIndex
    {
        private readonly Node _root;
        private readonly int  _leafSize;

        /// <summary>
        /// Nagtatayo ng BallTree mula sa listahan ng employee face entries.
        /// </summary>
        /// <param name="entries">Listahan ng employee face index entries na may Vec field.</param>
        /// <param name="leafSize">
        ///   Bilang ng points sa isang leaf node (4–64).
        ///   Mas maliit = mas mabilis ang query ngunit mas malaki ang memory.
        /// </param>
        public BallTreeIndex(IEnumerable<EmployeeFaceIndex.Entry> entries, int leafSize)
        {
            if (entries == null) throw new ArgumentNullException(nameof(entries));

            // I-clamp ang leafSize sa 4–64 para maiwasan ang degenerate trees
            _leafSize = Math.Max(4, Math.Min(64, leafSize));

            var pts = entries
                .Where(e => e != null && e.Vec != null)
                .Select(e => new Point(e.EmployeeId, e.Vec))
                .ToList();

            if (pts.Count == 0)
                throw new InvalidOperationException("No points to index");

            _root = BuildTree(pts);
        }

        /// <summary>
        /// Hinahanap ang pinakamalapit na employee id sa loob ng maxDistance.
        /// Nagbabalik ng null kung walang nahanap.
        ///
        /// FIX (HIGH-03): Pinalitan ang double.MaxValue ng double.PositiveInfinity
        ///   para consistent sa EmployeeFaceIndex.FindNearest() na gumagamit ng
        ///   double.PositiveInfinity bilang "walang match" sentinel value.
        /// </summary>
        /// <param name="query">128-dimensional face vector ng query.</param>
        /// <param name="maxDistance">Maximum na pinapayagang distansya (tolerance).</param>
        /// <param name="distance">
        ///   Output: aktwal na distansya ng pinakamalapit na match.
        ///   double.PositiveInfinity kung walang nahanap.
        /// </param>
        /// <returns>EmployeeId ng pinakamalapit na match, o null kung wala.</returns>
        public string FindNearest(double[] query, double maxDistance, out double distance)
        {
            // FIX: double.PositiveInfinity (hindi double.MaxValue) para consistent
            // sa EmployeeFaceIndex at DlibBiometrics.Distance() return values
            distance = double.PositiveInfinity;

            if (query == null) return null;

            var bestId   = (string)null;
            var bestDist = maxDistance;

            Search(_root, query, ref bestId, ref bestDist);

            if (bestId == null)
            {
                // FIX: double.PositiveInfinity bilang "walang match" sentinel
                distance = double.PositiveInfinity;
                return null;
            }

            distance = bestDist;
            return bestId;
        }

        // ---------------------------------------------------------------------------
        // Private — tree construction
        // ---------------------------------------------------------------------------

        private Node BuildTree(List<Point> points)
        {
            var node = new Node();

            // Kalkulahin ang center at radius ng ball na nakabalot sa lahat ng points
            node.Center = ComputeCenter(points);
            node.Radius = ComputeRadius(points, node.Center);

            // Base case: kung sapat na maliit ang bilang ng points, gawing leaf node
            if (points.Count <= _leafSize)
            {
                node.IsLeaf = true;
                node.Points = points;
                return node;
            }

            // I-split ang points sa dalawa gamit ang maximum variance dimension
            int splitDim = FindMaxVarianceDimension(points);
            points.Sort((a, b) => a.Vector[splitDim].CompareTo(b.Vector[splitDim]));

            int mid   = points.Count / 2;
            var left  = points.Take(mid).ToList();
            var right = points.Skip(mid).ToList();

            node.IsLeaf        = false;
            node.SplitDimension = splitDim;
            node.SplitValue    = points[mid].Vector[splitDim];
            node.Left          = BuildTree(left);
            node.Right         = BuildTree(right);

            return node;
        }

        // ---------------------------------------------------------------------------
        // Private — tree search
        // ---------------------------------------------------------------------------

        private static void Search(Node node, double[] query, ref string bestId, ref double bestDist)
        {
            if (node == null) return;

            // Pag-prune: kung ang pinakamalapit na posibleng point sa node na ito ay
            // mas malayo pa kaysa sa kasalukuyang pinakamabilis, laktawan
            var centerDist = Distance(query, node.Center);
            if (centerDist - node.Radius > bestDist)
                return;

            if (node.IsLeaf)
            {
                // I-check ang bawat point sa leaf node
                for (int i = 0; i < node.Points.Count; i++)
                {
                    var p = node.Points[i];
                    var d = Distance(query, p.Vector);
                    if (d <= bestDist)
                    {
                        bestDist = d;
                        bestId   = p.EmployeeId;
                    }
                }
                return;
            }

            // I-search muna ang side na mas malamang may malapit na points
            var qv    = query[node.SplitDimension];
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

        /// <summary>
        /// Euclidean distance sa pagitan ng dalawang vector.
        /// Katulad ng DlibBiometrics.Distance() para consistent ang mga comparison.
        /// </summary>
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

        /// <summary>Isang punto sa tree — may EmployeeId at face vector.</summary>
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
