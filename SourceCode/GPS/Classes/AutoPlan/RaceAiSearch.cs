using System;
using System.Collections.Generic;

namespace AgOpenGPS.AutoPlan
{
    /// <summary>
    /// Independent forward-only kinodynamic A* search. This is an algorithmic
    /// search (AI planning), not a neural network and not FieldPlanner's turn table.
    /// Coordinates are metres; heading is 0=north, clockwise.
    /// </summary>
    public static class RaceAiSearch
    {
        public sealed class Options
        {
            public double Radius = 5.6;
            public double Step = 0.75;
            public double Clearance = 1.5;
            public double PositionTolerance = 1.0;
            public double HeadingTolerance = 0.18;
            public int MaxExpansions = 150000;
        }

        public sealed class Result
        {
            public bool Success;
            public string Reason;
            public int Expansions;
            public List<PlanPoint> Points = new List<PlanPoint>();
        }

        private sealed class Node
        {
            public double X, Y, H, G, F, Curvature;
            public int Parent;
        }

        private static double Wrap(double a)
        {
            while (a > Math.PI) a -= 2 * Math.PI;
            while (a < -Math.PI) a += 2 * Math.PI;
            return a;
        }

        private static double Dist(double x, double y, double xx, double yy)
        {
            double dx = x - xx, dy = y - yy;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static string Key(double x, double y, double h, double step)
        {
            int ix = (int)Math.Floor(x / step);
            int iy = (int)Math.Floor(y / step);
            int ih = (int)Math.Floor((Wrap(h) + Math.PI) * 36.0 / (2 * Math.PI));
            return ix + ":" + iy + ":" + ih;
        }

        private static double SegmentDistance(V2 a, V2 b, V2 p)
        {
            double dx = b.E - a.E, dy = b.N - a.N;
            double d = dx * dx + dy * dy;
            double t = d < 1e-12 ? 0 : Math.Max(0, Math.Min(1,
                ((p.E - a.E) * dx + (p.N - a.N) * dy) / d));
            return Dist(p.E, p.N, a.E + t * dx, a.N + t * dy);
        }

        private static bool Clear(List<V2> boundary, double x, double y, double clearance)
        {
            V2 p = new V2(x, y);
            if (!FieldPlanner.PointInPolygon(boundary, p)) return false;
            for (int i = 0; i < boundary.Count; i++)
                if (SegmentDistance(boundary[i], boundary[(i + 1) % boundary.Count], p) < clearance)
                    return false;
            return true;
        }

        /// <summary>Search one forward-only connection. No reverse or unsafe fallback.</summary>
        public static Result Connect(IList<V2> boundary, PlanPoint start, PlanPoint goal, Options options = null)
        {
            var result = new Result();
            Options o = options ?? new Options();
            if (boundary == null || boundary.Count < 3 || o.Radius <= 0 ||
                o.Step <= 0 || o.Clearance < 0 || o.MaxExpansions < 1)
            {
                result.Reason = "Invalid search input";
                return result;
            }
            var fence = new List<V2>(boundary);
            if (!Clear(fence, start.E, start.N, o.Clearance) ||
                !Clear(fence, goal.E, goal.N, o.Clearance))
            {
                result.Reason = "Start or goal violates boundary clearance";
                return result;
            }

            var nodes = new List<Node>();
            var open = new SortedSet<Tuple<double, long, int>>();
            var best = new Dictionary<string, double>();
            long serial = 0;
            double h0 = Wrap(start.Heading);
            nodes.Add(new Node
            {
                X = start.E,
                Y = start.N,
                H = h0,
                G = 0,
                F = Dist(start.E, start.N, goal.E, goal.N),
                Parent = -1
            });
            open.Add(Tuple.Create(nodes[0].F, serial++, 0));
            best[Key(start.E, start.N, h0, o.Step)] = 0;

            while (open.Count > 0 && result.Expansions < o.MaxExpansions)
            {
                var entry = open.Min;
                open.Remove(entry);
                int index = entry.Item3;
                Node n = nodes[index];
                string nk = Key(n.X, n.Y, n.H, o.Step);
                if (best.TryGetValue(nk, out double known) && n.G > known + 1e-8)
                    continue;
                result.Expansions++;
                if (Dist(n.X, n.Y, goal.E, goal.N) <= o.PositionTolerance &&
                    Math.Abs(Wrap(n.H - goal.Heading)) <= o.HeadingTolerance)
                {
                    var reverse = new List<PlanPoint>();
                    for (int at = index; at >= 0; at = nodes[at].Parent)
                    {
                        Node q = nodes[at];
                        reverse.Add(new PlanPoint
                        {
                            E = q.X,
                            N = q.Y,
                            Heading = q.H,
                            IsReverse = false,
                            IsWork = false
                        });
                    }
                    reverse.Reverse();
                    result.Points = reverse;
                    result.Success = true;
                    result.Reason = "Forward-only path found";
                    return result;
                }

                foreach (int steer in new[] { -1, 0, 1 })
                {
                    double curvature = steer / o.Radius;
                    double dh = curvature * o.Step;
                    double mid = n.H + dh * 0.5;
                    double x = n.X + Math.Sin(mid) * o.Step;
                    double y = n.Y + Math.Cos(mid) * o.Step;
                    double h = Wrap(n.H + dh);
                    // Check the swept centreline, not just the endpoint.
                    bool clear = true;
                    for (int j = 1; j <= 4; j++)
                    {
                        double ds = o.Step * j / 4.0;
                        double hh = n.H + curvature * ds * 0.5;
                        if (!Clear(fence, n.X + Math.Sin(hh) * ds,
                            n.Y + Math.Cos(hh) * ds, o.Clearance))
                        {
                            clear = false;
                            break;
                        }
                    }
                    if (!clear) continue;
                    double cost = n.G + o.Step + (steer == 0 ? 0 : 0.015 * o.Step) +
                        Math.Abs(curvature - n.Curvature) * 0.12;
                    string key = Key(x, y, h, o.Step);
                    if (best.TryGetValue(key, out double previous) && previous <= cost)
                        continue;
                    best[key] = cost;
                    double heuristic = Math.Max(Dist(x, y, goal.E, goal.N),
                        o.Radius * Math.Abs(Wrap(h - goal.Heading)));
                    var next = new Node
                    {
                        X = x,
                        Y = y,
                        H = h,
                        G = cost,
                        F = cost + heuristic,
                        Curvature = curvature,
                        Parent = index
                    };
                    int id = nodes.Count;
                    nodes.Add(next);
                    open.Add(Tuple.Create(next.F, serial++, id));
                }
            }
            result.Reason = result.Expansions >= o.MaxExpansions
                ? "Search budget exhausted" : "No feasible forward-only connection";
            return result;
        }
    }
}
