using System;
using System.Collections.Generic;
using System.Linq;

namespace FaceAttend.Services.Biometrics
{
    /// <summary>
    /// Ball-tree index for fast nearest-neighbor search on face vectors.
    /// Intended for 200-1000 employees.
    /// Build once, then query many times.
    /// </summary>
    public sealed class BallTreeIndex
    {
        private readonly Node _root;
        private readonly int _leafSize;

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

        /// <summary>
        /// Finds the nearest employee id within maxDistance.
        /// Returns null if none.
        /// </summary>
        public string FindNearest(double[] query, double maxDistance, out double distance)
        {
            distance = double.MaxValue;
            if (query == null) return null;

            var bestId = (string)null;
            var bestDist = maxDistance;

            Search(_root, query, ref bestId, ref bestDist);

            if (bestId == null)
            {
                distance = double.MaxValue;
                return null;
            }

            distance = bestDist;
            return bestId;
        }

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

            int mid = points.Count / 2;
            var left = points.Take(mid).ToList();
            var right = points.Skip(mid).ToList();

            node.IsLeaf = false;
            node.SplitDimension = splitDim;
            node.SplitValue = points[mid].Vector[splitDim];
            node.Left = BuildTree(left);
            node.Right = BuildTree(right);

            return node;
        }

        private static int FindMaxVarianceDimension(List<Point> points)
        {
            int dim = points[0].Vector.Length;
            int bestDim = 0;
            double bestVar = double.MinValue;

            for (int d = 0; d < dim; d++)
            {
                double mean = 0;
                for (int i = 0; i < points.Count; i++)
                    mean += points[i].Vector[d];
                mean /= points.Count;

                double var = 0;
                for (int i = 0; i < points.Count; i++)
                {
                    var diff = points[i].Vector[d] - mean;
                    var += diff * diff;
                }
                var /= points.Count;

                if (var > bestVar)
                {
                    bestVar = var;
                    bestDim = d;
                }
            }

            return bestDim;
        }

        private static double[] ComputeCenter(List<Point> points)
        {
            int dim = points[0].Vector.Length;
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

        private static void Search(Node node, double[] query, ref string bestId, ref double bestDist)
        {
            if (node == null) return;

            // Prune: if the closest possible point in this node is already worse than best.
            var centerDist = Distance(query, node.Center);
            if (centerDist - node.Radius > bestDist)
                return;

            if (node.IsLeaf)
            {
                for (int i = 0; i < node.Points.Count; i++)
                {
                    var p = node.Points[i];
                    var d = Distance(query, p.Vector);
                    if (d <= bestDist)
                    {
                        bestDist = d;
                        bestId = p.EmployeeId;
                    }
                }
                return;
            }

            // Search the side that is more likely to contain close points first.
            var qv = query[node.SplitDimension];
            var first = qv <= node.SplitValue ? node.Left : node.Right;
            var second = qv <= node.SplitValue ? node.Right : node.Left;

            Search(first, query, ref bestId, ref bestDist);
            Search(second, query, ref bestId, ref bestDist);
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

        private sealed class Point
        {
            public Point(string employeeId, double[] vector)
            {
                EmployeeId = employeeId;
                Vector = vector;
            }

            public string EmployeeId { get; private set; }
            public double[] Vector { get; private set; }
        }

        private sealed class Node
        {
            public bool IsLeaf;
            public List<Point> Points;

            public int SplitDimension;
            public double SplitValue;
            public Node Left;
            public Node Right;

            public double[] Center;
            public double Radius;
        }
    }
}
