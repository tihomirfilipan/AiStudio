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
        /// Three point turn into the neighbour pass as the K-turn symbol shows: from the pass
        /// end straight on to the turn, a big forward turn towards the next pass, a short turn
        /// backwards until the vehicle stands on the line of the next pass facing along it, then
        /// straight forward into the pass (lined up). Made as close to the boundary as it fits.
        /// Null when the passes are not side by side (opposite headings, closer than two radii)
        /// or it does not fit.
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
            double L = Math.Abs(lat);
            if (L < 0.5 || L >= 2.0 * R - 0.01) return null;
            V2 u = r * Math.Sign(lat);                          // towards the next pass
            double alpha = Math.Acos(-L / (2.0 * R));           // forward turn (over 90 degrees)
            double step = Math.Max(0.2, spacing);
            int Steps(double len) => Math.Max(2, (int)Math.Ceiling(len / step));

            // local frame: x away from the next pass (next pass at x = -L), y on along the pass
            // mirrored: the forward turn goes away from the next pass, the longer turn back
            // brings the vehicle onto its line (same room)
            List<Pose> Make(double e, bool mirrored)
            {
                V2 a0 = p0 + d * e;
                V2 Wp(double x, double y) => a0 + u * (-x) + d * y;
                var pts = new List<Pose>();
                void Add(V2 p, bool rev) { pts.Add(new Pose { P = p, Reverse = rev }); }
                Add(p0, false);
                int n = Steps(e);
                for (int i = 1; i <= n && e > 1e-6; i++) Add(p0 + d * (e * i / n), false);
                V2 q;
                if (!mirrored)
                {
                    // forward turn towards the next pass, centre (-R, 0)
                    n = Steps(R * alpha);
                    for (int i = 1; i <= n; i++)
                    {
                        double t = alpha * i / n;
                        Add(Wp(-R + R * Math.Cos(t), R * Math.Sin(t)), false);
                    }
                    // backwards with the opposite steering: the heading keeps turning the same way
                    double cx = -R + 2.0 * R * Math.Cos(alpha), cy = 2.0 * R * Math.Sin(alpha);
                    double th0 = Math.PI / 2 + alpha, th1 = 1.5 * Math.PI;
                    n = Steps(R * (th1 - th0));
                    for (int i = 1; i <= n; i++)
                    {
                        double th = th0 + (th1 - th0) * i / n;
                        Add(Wp(cx - R * Math.Sin(th), cy + R * Math.Cos(th)), true);
                    }
                    q = Wp(cx + R, cy);
                }
                else
                {
                    // forward turn away from the next pass, centre (R, 0)
                    double am = Math.PI - alpha;
                    n = Steps(R * am);
                    for (int i = 1; i <= n; i++)
                    {
                        double t = am * i / n;
                        Add(Wp(R - R * Math.Cos(t), R * Math.Sin(t)), false);
                    }
                    // backwards, steering the other way, onto the line of the next pass
                    double cx = R - 2.0 * R * Math.Cos(am), cy = 2.0 * R * Math.Sin(am);
                    double th0 = Math.PI / 2 - am, th1 = -Math.PI / 2;
                    n = Steps(R * (th0 - th1));
                    for (int i = 1; i <= n; i++)
                    {
                        double th = th0 + (th1 - th0) * i / n;
                        Add(Wp(cx + R * Math.Sin(th), cy - R * Math.Cos(th)), true);
                    }
                    q = Wp(cx - R, cy);
                }
                // lined up on the next pass: straight forward into it
                double len = (p1 - q).Length;
                n = Steps(len);
                for (int i = 1; i <= n; i++) Add(q + (p1 - q) * (i / (double)n), false);
                foreach (Pose pp in pts) if (!inside(pp.P)) return null;
                return pts;
            }

            // as close to the boundary as it fits
            double eMin = Math.Max(0.0, along - 2.0 * R * Math.Sin(alpha));
            // the turn towards the next pass first, else the other way round (as close to the
            // boundary as it fits, both need the same room)
            for (double e = eMin + 40.0; e >= eMin - 1e-9; e -= 0.5)
            {
                List<Pose> k = Make(e, false) ?? Make(e, true);
                if (k != null) return k;
            }
            return null;
        }

        /// <summary>Room the three point turn needs past its start (towards the boundary).</summary>
        public static double KTurnReach(double W, double R)
        {
            if (W >= 2.0 * R) return R;
            return 2.0 * R * Math.Sin(Math.Acos(-W / (2.0 * R)));
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
