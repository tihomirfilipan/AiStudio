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
        // Turns switched off in the options: set by the planner for the thread that is building a plan, so a
        // turn that is off is not used at any place (all false = all on)
        [ThreadStatic] public static bool DisableK, DisableKLV, DisableKK, DisableFind, DisablePear, DisableTear;

        public static void ResetSwitches() { DisableK = DisableKLV = DisableKK = DisableFind = DisablePear = DisableTear = false; }

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
        public static List<Pose> KTurn(V2 p0, double h0, V2 p1, double h1, double R, double spacing, Func<V2, bool> inside,
            bool nearBoundary = true)
        {
            if (DisableK) return null;
            double dh = Math.IEEERemainder(h1 - h0 - Math.PI, 2.0 * Math.PI);
            // gores meeting on the middle line of a wedge are not quite parallel
            if (Math.Abs(dh) > (nearBoundary ? 0.2 : 0.5)) return null;
            V2 d = new V2(Math.Sin(h0), Math.Cos(h0));
            V2 r = new V2(Math.Cos(h0), -Math.Sin(h0));        // to the right of the heading
            V2 rel = p1 - p0;
            double along = rel.E * d.E + rel.N * d.N;
            double lat = rel.E * r.E + rel.N * r.N;
            double L = Math.Abs(lat);
            if (L >= 2.0 * R - 0.01 || (nearBoundary && L < 0.5)) return null;
            // the side of the next pass: where it runs, a bit along it (its start may be right here)
            V2 d1 = new V2(Math.Sin(h1), Math.Cos(h1));
            V2 ahead = p1 + d1 * 5.0 - p0;
            double sideLat = L >= 0.5 ? lat : ahead.E * r.E + ahead.N * r.N;
            if (Math.Abs(sideLat) < 1e-6) return null;
            V2 u = r * Math.Sign(sideLat);                      // towards the next pass
            double alpha = Math.Acos(-L / (2.0 * R));           // forward turn (over 90 degrees)
            double step = Math.Max(0.2, spacing);
            int Steps(double len) => Math.Max(2, (int)Math.Ceiling(len / step));

            // local frame: x away from the next pass (next pass at x = -L), y on along the pass
            List<Pose> Make(double e)
            {
                V2 a0 = p0 + d * e;
                V2 Wp(double x, double y) => a0 + u * (-x) + d * y;
                var pts = new List<Pose>();
                void Add(V2 p, bool rev) { pts.Add(new Pose { P = p, Reverse = rev }); }
                Add(p0, false);
                int n = Steps(e);
                for (int i = 1; i <= n && e > 1e-6; i++) Add(p0 + d * (e * i / n), false);
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
                // lined up on the next pass: straight forward into it
                V2 q = Wp(cx + R, cy);
                double len = (p1 - q).Length;
                n = Steps(len);
                for (int i = 1; i <= n; i++) Add(q + (p1 - q) * (i / (double)n), false);
                foreach (Pose pp in pts) if (!inside(pp.P)) return null;
                return pts;
            }

            // as close to the boundary as it fits
            double eMin = Math.Max(0.0, along - 2.0 * R * Math.Sin(alpha));
            if (!nearBoundary)
            {
                // right where the pass ends (top of a gore): as soon as it fits
                for (double e = eMin; e <= eMin + 40.0; e += 0.5)
                {
                    List<Pose> k = Make(e);
                    if (k != null) return k;
                }
                return null;
            }
            for (double e = eMin + 40.0; e >= eMin - 1e-9; e -= 0.5)
            {
                List<Pose> k = Make(e);
                if (k != null) return k;
            }
            return null;
        }

        /// <summary>
        /// KLV1: forward arc of radius R turning away from the next pass (angle acos(L/2R)), then a
        /// reversing arc of the same radius turning the other way, which brings the vehicle onto
        /// the next pass facing against the first pass; then straight forward into the pass.
        /// The reversing arc ends at the same height as the forward arc's cusp reach (2R sin a),
        /// so the room needed equals KTurnReach. Null when the passes are not side by side, closer
        /// than two radii, or it does not fit inside.
        /// </summary>
        public static List<Pose> KLV1(V2 p0, double h0, V2 p1, double h1, double R, double spacing, Func<V2, bool> inside,
            bool nearBoundary = true)
        {
            if (DisableKLV) return null;
            double dh = Math.IEEERemainder(h1 - h0 - Math.PI, 2.0 * Math.PI);
            if (Math.Abs(dh) > (nearBoundary ? 0.2 : 0.5)) return null;
            V2 d = new V2(Math.Sin(h0), Math.Cos(h0));
            V2 r = new V2(Math.Cos(h0), -Math.Sin(h0));        // to the right of the heading
            V2 rel = p1 - p0;
            double along = rel.E * d.E + rel.N * d.N;
            double lat = rel.E * r.E + rel.N * r.N;
            double L = Math.Abs(lat);
            if (L >= 2.0 * R - 0.01 || (nearBoundary && L < 0.5)) return null;
            V2 d1 = new V2(Math.Sin(h1), Math.Cos(h1));
            V2 ahead = p1 + d1 * 5.0 - p0;
            double sideLat = L >= 0.5 ? lat : ahead.E * r.E + ahead.N * r.N;
            if (Math.Abs(sideLat) < 1e-6) return null;
            V2 u = r * Math.Sign(sideLat);                      // towards the next pass
            double alpha = Math.Acos(L / (2.0 * R));            // forward arc, under 90 degrees
            double yc = 2.0 * R * Math.Sin(alpha);              // height of the reversing arc's top
            double step = Math.Max(0.2, spacing);
            int Steps(double len) => Math.Max(2, (int)Math.Ceiling(len / step));

            // local frame: x away from the next pass (next pass at x = -L), y along the pass
            List<Pose> Make(double e)
            {
                V2 a0 = p0 + d * e;
                V2 Wp(double x, double y) => a0 + u * (-x) + d * y;
                var pts = new List<Pose>();
                void Add(V2 p, bool rev) { pts.Add(new Pose { P = p, Reverse = rev }); }
                Add(p0, false);
                int n = Steps(e);
                for (int i = 1; i <= n && e > 1e-6; i++) Add(p0 + d * (e * i / n), false);
                // forward arc away from the next pass, centre (R, 0)
                n = Steps(R * alpha);
                for (int i = 1; i <= n; i++)
                {
                    double t = alpha * i / n;
                    Add(Wp(R - R * Math.Cos(t), R * Math.Sin(t)), false);
                }
                // reversing arc of the same radius, centre (R - L, yc), from the cusp round to the next pass
                double cx = R - L, cy = yc;
                double ph0 = -alpha, ph1 = -Math.PI;
                n = Steps(R * (ph0 - ph1));
                for (int i = 1; i <= n; i++)
                {
                    double ph = ph0 + (ph1 - ph0) * i / n;
                    Add(Wp(cx + R * Math.Cos(ph), cy + R * Math.Sin(ph)), true);
                }
                // straight forward into the next pass, facing against the first pass
                V2 q = Wp(-L, yc);
                V2 toP = p1 - q;
                if (toP.E * d1.E + toP.N * d1.N < -0.01) return null;
                double len = toP.Length;
                n = Steps(len);
                for (int i = 1; i <= n; i++) Add(q + toP * (i / (double)n), false);
                foreach (Pose pp in pts) if (!inside(pp.P)) return null;
                return pts;
            }

            // the cusp must come before the next pass starts: e >= along - yc
            double eMin = Math.Max(0.0, along - yc);
            if (!nearBoundary)
            {
                for (double e = eMin; e <= eMin + 40.0; e += 0.5)
                {
                    List<Pose> k = Make(e);
                    if (k != null) return k;
                }
                return null;
            }
            for (double e = eMin + 40.0; e >= eMin - 1e-9; e -= 0.5)
            {
                List<Pose> k = Make(e);
                if (k != null) return k;
            }
            return null;
        }

        /// <summary>
        /// KK, the short three point turn: straight on from the pass end until the front end of the
        /// vehicle is at the boundary, backwards at full lock (the rear swings towards the next
        /// pass) by the angle acos(-L / 2R), then forward at full lock the other way, which ends on
        /// the next pass facing along it; straight on into the pass. R is the full lock radius.
        /// Null when the passes are not side by side, two radii or more apart, or it does not fit.
        /// </summary>
        public static List<Pose> KK(V2 p0, double h0, V2 p1, double h1, double R, double spacing, Func<V2, bool> inside,
            Func<V2, bool> noseInside, double frontReach, double wheelbase = 0.0)
        {
            if (DisableKK) return null;
            double dh = Math.IEEERemainder(h1 - h0 - Math.PI, 2.0 * Math.PI);
            if (Math.Abs(dh) > 0.2) return null;
            V2 d = new V2(Math.Sin(h0), Math.Cos(h0));
            V2 r = new V2(Math.Cos(h0), -Math.Sin(h0));        // to the right of the heading
            V2 rel = p1 - p0;
            double along = rel.E * d.E + rel.N * d.N;
            double lat = rel.E * r.E + rel.N * r.N;
            double L = Math.Abs(lat);
            if (L < 0.5 || L >= 2.0 * R - 0.05) return null;
            V2 u = r * Math.Sign(lat);                          // towards the next pass
            double phi = Math.Acos(-L / (2.0 * R));             // reversing arc landing exactly on the next pass
            if (wheelbase > 0.5)
            {
                // reverse until the FRONT wheels are on the line of the next pass, then stop: the
                // front axle is wheelbase * sin(phi) ahead of the rear axle and swings the other way
                double Front(double a) => R * (1.0 - Math.Cos(a)) - wheelbase * Math.Sin(a) - L;
                double lo = phi, hi = Math.PI - 0.02;
                if (Front(hi) > 0)
                {
                    for (int k = 0; k < 40; k++)
                    {
                        double mid = 0.5 * (lo + hi);
                        if (Front(mid) < 0) lo = mid; else hi = mid;
                    }
                    phi = 0.5 * (lo + hi);
                }
            }
            double back = 2.0 * R * Math.Sin(phi);              // the turn ends this far back from its start
            double xEnd = -2.0 * R * Math.Cos(phi);             // where the forward arc ends (next pass at L)
            double step = Math.Max(0.2, Math.Min(spacing, 0.5));
            int Steps(double len) => Math.Max(2, (int)Math.Ceiling(len / step));

            // straight on as far as the front end stays inside
            double eMax = -1.0;
            for (double e = 0.0; e <= 60.0; e += 0.25)
            {
                if (!noseInside(p0 + d * (e + frontReach))) break;
                eMax = e;
            }
            // the turn must end before the next pass starts
            double eMin = Math.Max(0.0, along + back);
            for (double e = eMax; e >= eMin - 1e-9; e -= 0.25)
            {
                V2 a0 = p0 + d * e;
                V2 Wp(double x, double y) => a0 + u * x + d * y;
                var pts = new List<Pose>();
                void Add(V2 p, bool rev) { pts.Add(new Pose { P = p, Reverse = rev }); }
                Add(p0, false);
                int n = Steps(e);
                for (int i = 1; i <= n && e > 1e-6; i++) Add(p0 + d * (e * i / n), false);
                // backwards at full lock, centre (R, 0)
                n = Steps(R * phi);
                for (int i = 1; i <= n; i++)
                {
                    double t = phi * i / n;
                    Add(Wp(R - R * Math.Cos(t), -R * Math.Sin(t)), true);
                }
                // forward at full lock the other way, onto the next pass
                double cx = R - 2.0 * R * Math.Cos(phi), cy = -back;
                n = Steps(R * (Math.PI - phi));
                for (int i = 1; i <= n; i++)
                {
                    double t = phi + (Math.PI - phi) * i / n;
                    Add(Wp(cx + R * Math.Cos(t), cy + R * Math.Sin(t)), false);
                }
                V2 q = Wp(xEnd, -back);
                double len = (p1 - q).Length;
                n = Steps(len);
                for (int i = 1; i <= n && len > 1e-6; i++) Add(q + (p1 - q) * (i / (double)n), false);
                bool ok = true;
                foreach (Pose pp in pts) if (!inside(pp.P)) { ok = false; break; }
                if (ok) return pts;
            }
            return null;
        }

        /// <summary>
        /// Loop turns that are driven forward only, away from the next pass and back onto it: the pear (KRU:
        /// away 270 degrees, straight on towards the pass, back 90 degrees) and the teardrop (SUZ: away 225
        /// degrees, straight on, back 45 degrees). The straight part is as long as it takes to land on the
        /// next pass; the turn is made as close to the boundary as it fits. Null when the passes are not side
        /// by side in opposite directions or it does not fit inside.
        /// </summary>
        public static List<Pose> LoopTurn(V2 p0, double h0, V2 p1, double h1, double R, double spacing, Func<V2, bool> inside,
            double awayDeg, bool nearBoundary = true)
        {
            if (awayDeg > 250.0 ? DisablePear : DisableTear) return null;
            double dh = Math.IEEERemainder(h1 - h0 - Math.PI, 2.0 * Math.PI);
            if (Math.Abs(dh) > (nearBoundary ? 0.2 : 0.5)) return null;
            V2 d = new V2(Math.Sin(h0), Math.Cos(h0));
            V2 r = new V2(Math.Cos(h0), -Math.Sin(h0));
            V2 rel = p1 - p0;
            double along = rel.E * d.E + rel.N * d.N;
            double lat = rel.E * r.E + rel.N * r.N;
            double L = Math.Abs(lat);
            if (L < 0.5 || L >= 4.0 * R) return null;
            V2 u = r * Math.Sign(lat);                          // towards the next pass
            double away = awayDeg * Math.PI / 180.0;
            double back = away - Math.PI;                       // the way back: the turn that ends facing the pass
            double step = Math.Max(0.2, spacing);
            int Steps(double len) => Math.Max(2, (int)Math.Ceiling(len / step));

            // local frame: x towards the next pass (at x = L), y along the pass; heading phi from +y towards +x
            double x = 0, y = 0, phi = 0;
            void Arc(double sgn, double theta, List<(double x, double y)> pts)
            {
                double cx = x + sgn * R * Math.Cos(phi), cy = y - sgn * R * Math.Sin(phi);
                int n = Steps(R * theta);
                for (int i = 1; i <= n; i++)
                {
                    double ph = phi + sgn * theta * i / n;
                    pts.Add((cx - sgn * R * Math.Cos(ph), cy + sgn * R * Math.Sin(ph)));
                }
                double ph2 = phi + sgn * theta;
                x = cx - sgn * R * Math.Cos(ph2); y = cy + sgn * R * Math.Sin(ph2); phi = ph2;
            }
            // dry run to find the straight part that lands on the next pass
            var tmp = new List<(double x, double y)>();
            Arc(-1.0, away, tmp);
            double hx = Math.Sin(phi), hy = Math.Cos(phi);
            if (hx < 0.2) return null;                           // it must go on towards the next pass
            double x0 = x, y0 = y, phi0 = phi;
            double straight = (L - x0) / hx;
            if (straight < 0) return null;
            // the end of the turn: straight, then the arc back
            double xs = x0 + hx * straight, ys = y0 + hy * straight;
            x = xs; y = ys; phi = phi0;
            var tmp2 = new List<(double x, double y)>();
            Arc(1.0, back, tmp2);
            double yEnd = y;                                     // relative to the start of the loop

            double eMin = Math.Max(0.0, along - yEnd);
            List<Pose> Make(double e)
            {
                V2 a0 = p0 + d * e;
                V2 Wp(double lx, double ly) => a0 + u * lx + d * ly;
                var pts = new List<Pose>();
                void Add(V2 q) { pts.Add(new Pose { P = q, Reverse = false }); }
                Add(p0);
                int n = Steps(e);
                for (int i = 1; i <= n && e > 1e-6; i++) Add(p0 + d * (e * i / n));
                x = 0; y = 0; phi = 0;
                var arc1 = new List<(double x, double y)>();
                Arc(-1.0, away, arc1);
                foreach (var q in arc1) Add(Wp(q.x, q.y));
                int ns = Steps(straight);
                for (int i = 1; i <= ns; i++) Add(Wp(x + hx * straight * i / ns, y + hy * straight * i / ns));
                x += hx * straight; y += hy * straight;
                var arc2 = new List<(double x, double y)>();
                Arc(1.0, back, arc2);
                foreach (var q in arc2) Add(Wp(q.x, q.y));
                V2 qEnd = Wp(x, y);
                V2 toP = p1 - qEnd;
                V2 d1 = new V2(Math.Sin(h1), Math.Cos(h1));
                if (toP.E * d1.E + toP.N * d1.N < -0.01) return null;
                double len = toP.Length;
                int ne = Steps(len);
                for (int i = 1; i <= ne && len > 1e-6; i++) Add(qEnd + toP * (i / (double)ne));
                foreach (Pose pp in pts) if (!inside(pp.P)) return null;
                return pts;
            }
            if (!nearBoundary)
            {
                for (double e = eMin; e <= eMin + 40.0; e += 0.5)
                {
                    List<Pose> k = Make(e);
                    if (k != null) return k;
                }
                return null;
            }
            for (double e = eMin + 40.0; e >= eMin - 1e-9; e -= 0.5)
            {
                List<Pose> k = Make(e);
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
            if (DisableFind) return null;
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
