using System;
using System.Collections.Generic;

namespace AgOpenGPS.AutoPlan
{
    /// <summary>
    /// Short manoeuvre with forward and reverse parts (like a three point turn), found with a small
    /// Hybrid A* search: arcs of the turning radius and straight pieces, driven forward or in
    /// reverse, every point at least the clearance inside the field.
    /// </summary>
    public static class ReverseManeuver
    {
        public struct Pose
        {
            public V2 P;
            public bool Reverse;   // this point is reached driving backwards
        }

        private class Node
        {
            public double X, Y, H;     // H = heading the vehicle faces (clockwise from north)
            public double G, F;
            public bool Reverse;
            public Node Parent;
            public List<Pose> Piece;   // points from the parent to this node
        }

        /// <summary>
        /// Manoeuvre from p0 (facing h0) to p1 (facing h1). inside: true where the vehicle may be.
        /// Null if nothing was found within the search limit.
        /// </summary>
        public static List<Pose> Find(V2 p0, double h0, V2 p1, double h1, double R, double spacing,
            Func<V2, bool> inside, int maxExpansions = 60000)
        {
            double step = Math.Max(1.0, R * Math.PI / 8.0);   // length of one primitive
            double cell = Math.Max(0.5, step * 0.5);
            const int headingBins = 32;
            const double reversePenalty = 1.3, switchPenalty = 8.0;
            double goalDist = Math.Max(0.6, step * 0.35);
            double goalHeading = 12.0 * Math.PI / 180.0;

            var open = new SortedSet<Tuple<double, int>>();
            var nodes = new List<Node>();
            var closed = new HashSet<long>();

            var start = new Node { X = p0.E, Y = p0.N, H = FieldPlanner.Mod2Pi(h0), G = 0, Reverse = false };
            start.F = Heuristic(start, p1, h1, R);
            nodes.Add(start);
            open.Add(Tuple.Create(start.F, 0));

            int expansions = 0;
            while (open.Count > 0 && expansions++ < maxExpansions)
            {
                var first = open.Min;
                open.Remove(first);
                Node n = nodes[first.Item2];

                long key = Key(n, cell, headingBins);
                if (!closed.Add(key)) continue;

                double dGoal = Math.Sqrt((n.X - p1.E) * (n.X - p1.E) + (n.Y - p1.N) * (n.Y - p1.N));
                if (dGoal < goalDist && Math.Abs(FieldPlanner.AngleDiff(n.H, h1)) < goalHeading && !n.Reverse)
                    return Build(n, p1);

                foreach (int dir in new[] { 1, -1 })
                {
                    foreach (int steer in new[] { -1, 0, 1 })
                    {
                        List<Pose> piece = Simulate(n, dir, steer, R, step, spacing, out double x, out double y, out double h);
                        bool ok = true;
                        foreach (Pose q in piece)
                            if (!inside(q.P)) { ok = false; break; }
                        if (!ok) continue;

                        double g = n.G + step * (dir < 0 ? reversePenalty : 1.0)
                            + (n.Parent != null && (dir < 0) != n.Reverse ? switchPenalty : 0.0)
                            + (n.Parent == null && dir < 0 ? switchPenalty : 0.0);
                        var c = new Node { X = x, Y = y, H = h, G = g, Reverse = dir < 0, Parent = n, Piece = piece };
                        if (closed.Contains(Key(c, cell, headingBins))) continue;
                        c.F = g + Heuristic(c, p1, h1, R);
                        nodes.Add(c);
                        open.Add(Tuple.Create(c.F + nodes.Count * 1e-9, nodes.Count - 1));
                    }
                }
            }
            return null;
        }

        private static double Heuristic(Node n, V2 p1, double h1, double R)
        {
            double d = Math.Sqrt((n.X - p1.E) * (n.X - p1.E) + (n.Y - p1.N) * (n.Y - p1.N));
            return Math.Max(d, R * Math.Abs(FieldPlanner.AngleDiff(n.H, h1)));
        }

        private static long Key(Node n, double cell, int bins)
        {
            long x = (long)Math.Floor(n.X / cell), y = (long)Math.Floor(n.Y / cell);
            long hb = (long)Math.Floor(FieldPlanner.Mod2Pi(n.H) / (2 * Math.PI) * bins) % bins;
            return ((x & 0xFFFFF) << 40) | ((y & 0xFFFFF) << 20) | (hb << 1) | (n.Reverse ? 1L : 0L);
        }

        // Drives one primitive from node n: dir +1 forward / -1 reverse, steer -1 left, 0, +1 right
        private static List<Pose> Simulate(Node n, int dir, int steer, double R, double length, double spacing,
            out double x, out double y, out double h)
        {
            x = n.X; y = n.Y; h = n.H;
            int k = Math.Max(2, (int)Math.Ceiling(length / Math.Max(0.3, spacing)));
            double ds = length / k;
            var pts = new List<Pose>(k);
            for (int i = 0; i < k; i++)
            {
                // heading change for a front-steered vehicle: forward and reverse turn the same way
                double dh = steer * ds / R;
                double hm = h + dh * 0.5 * dir;
                x += dir * ds * Math.Sin(hm);
                y += dir * ds * Math.Cos(hm);
                h = FieldPlanner.Mod2Pi(h + dh * dir);
                pts.Add(new Pose { P = new V2(x, y), Reverse = dir < 0 });
            }
            return pts;
        }

        private static List<Pose> Build(Node n, V2 goal)
        {
            var pieces = new List<List<Pose>>();
            for (Node c = n; c.Parent != null; c = c.Parent) pieces.Add(c.Piece);
            pieces.Reverse();
            var r = new List<Pose>();
            foreach (var p in pieces) r.AddRange(p);
            r.Add(new Pose { P = goal, Reverse = false });
            return r;
        }
    }
}
