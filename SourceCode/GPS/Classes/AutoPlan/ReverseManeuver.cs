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
        /// <summary>
        /// The standard three point turn into the neighbour pass (as the K-turn symbol shows):
        /// forward on to the turn, forward quarter turn towards the next pass, straight back,
        /// forward quarter turn into the next pass. Null when the passes are not side by side
        /// (opposite headings, closer than two radii) or it does not fit.
        /// </summary>
        public static List<Pose> KTurn(V2 p0, double h0, V2 p1, double h1, double R, double spacing, Func<V2, bool> inside)
        {
            double dh = Math.IEEERemainder(h1 - h0 - Math.PI, 2.0 * Math.PI);
            if (Math.Abs(dh) > 0.2) return null;
            V2 d = new V2(Math.Sin(h0), Math.Cos(h0));
            V2 r = new V2(Math.Cos(h0), -Math.Sin(h0));        // to the right of the heading
            V2 rel = p1 - p0;
            double along = rel.E * d.E + rel.N * d.N;
            double lat = rel.E * r.E + rel.N * r.N;
            if (Math.Abs(lat) < 0.5 || Math.Abs(lat) >= 2.0 * R - 0.01) return null;
            double side = Math.Sign(lat);
            double top = Math.Max(0.0, along);
            double step = Math.Max(0.2, spacing);

            var pts = new List<Pose>();
            void Add(V2 p, bool rev) { pts.Add(new Pose { P = p, Reverse = rev }); }
            void Line(V2 a, V2 b, bool rev)
            {
                double len = (b - a).Length;
                int n = Math.Max(1, (int)Math.Ceiling(len / step));
                for (int i = 1; i <= n; i++) Add(a + (b - a) * (i / (double)n), rev);
            }
            int arcN = Math.Max(4, (int)Math.Ceiling(R * Math.PI / 2.0 / step));

            Add(p0, false);
            // on to the turn
            V2 a0 = p0 + d * top;
            if (top > 1e-6) Line(p0, a0, false);
            // forward quarter turn towards the next pass
            V2 c1 = a0 + r * (side * R);
            for (int i = 1; i <= arcN; i++)
            {
                double t = 0.5 * Math.PI * i / arcN;
                Add(c1 + r * (-side * R * Math.Cos(t)) + d * (R * Math.Sin(t)), false);
            }
            // straight back
            V2 e1 = a0 + d * R + r * (side * R);
            V2 s2 = a0 + d * R + r * (lat - side * R);
            Line(e1, s2, true);
            // forward quarter turn into the next pass
            V2 c2 = a0 + r * (lat - side * R);
            for (int i = 1; i <= arcN; i++)
            {
                double t = 0.5 * Math.PI * i / arcN;
                Add(c2 + d * (R * Math.Cos(t)) + r * (side * R * Math.Sin(t)), false);
            }
            V2 b2 = a0 + r * lat;
            if ((b2 - p1).Length > 1e-6) Line(b2, p1, false);

            foreach (Pose q in pts) if (!inside(q.P)) return null;
            return pts;
        }

        public static List<Pose> Find(V2 p0, double h0, V2 p1, double h1, double R, double spacing,
            Func<V2, bool> inside, int maxExpansions = 60000)
        {
            // the standard three point turn first, the search only where it does not fit
            List<Pose> k = KTurn(p0, h0, p1, h1, R, spacing, inside);
            if (k != null) return k;

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
