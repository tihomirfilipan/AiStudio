using System;
using System.Collections.Generic;
using System.Linq;

namespace AgOpenGPS.AutoPlan
{
    /// <summary>
    /// RACE v2: independent centre-out coverage search. Generates lanes directly
    /// from polygon geometry and uses forward-only kinodynamic A* for transitions.
    /// Never calls FieldPlanner.Build or its legacy turn-order routines.
    /// Rejects plans that cannot satisfy the constraints.
    /// </summary>
    public static class RacePlanner
    {
        private sealed class Lane
        {
            public V2 A, B;
            public double Offset;
            public double Length => (B - A).Length;
        }

        private static FieldPlanResult Fail(string reason)
        {
            return new FieldPlanResult { Ok = false, Error = reason };
        }

        private static double Dot(V2 a, V2 b) => a.E * b.E + a.N * b.N;
        private static double Dist(V2 a, V2 b) => (a - b).Length;
        private static double Heading(V2 a, V2 b) =>
            Math.Atan2(b.E - a.E, b.N - a.N);
        private static V2 Pos(PlanPoint p) => new V2(p.E, p.N);

        private static bool Inside(List<V2> polygon, V2 p, double margin)
        {
            if (!FieldPlanner.PointInPolygon(polygon, p)) return false;
            for (int i = 0; i < polygon.Count; i++)
            {
                V2 a = polygon[i], b = polygon[(i + 1) % polygon.Count];
                V2 v = b - a, w = p - a;
                double len2 = Dot(v, v);
                double t = len2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, Dot(v, w) / len2));
                if (Dist(p, a + v * t) < margin) return false;
            }
            return true;
        }

        private static List<Lane> Generate(List<V2> fence, V2 origin, double angle,
            double width, double margin)
        {
            V2 d = new V2(Math.Sin(angle), Math.Cos(angle));
            V2 n = new V2(d.N, -d.E);
            double min = double.MaxValue, max = double.MinValue;
            foreach (V2 p in fence)
            {
                double q = Dot(p - origin, n);
                min = Math.Min(min, q);
                max = Math.Max(max, q);
            }
            var lanes = new List<Lane>();
            int lo = (int)Math.Ceiling(min / width);
            int hi = (int)Math.Floor(max / width);
            for (int k = lo; k <= hi; k++)
            {
                double offset = k * width;
                var cuts = new List<double>();
                for (int i = 0; i < fence.Count; i++)
                {
                    V2 a = fence[i], b = fence[(i + 1) % fence.Count];
                    double da = Dot(a - origin, n) - offset;
                    double db = Dot(b - origin, n) - offset;
                    if ((da < 0 && db >= 0) || (db < 0 && da >= 0))
                    {
                        double t = da / (da - db);
                        cuts.Add(Dot(a + (b - a) * t - origin, d));
                    }
                }
                cuts.Sort();
                for (int j = 0; j + 1 < cuts.Count; j += 2)
                {
                    double a = cuts[j] + margin + 0.25;
                    double b = cuts[j + 1] - margin - 0.25;
                    if (b - a < Math.Max(4, 2 * width)) continue;
                    V2 pa = origin + n * offset + d * a;
                    V2 pb = origin + n * offset + d * b;
                    if (Inside(fence, pa, margin) && Inside(fence, pb, margin))
                        lanes.Add(new Lane { A = pa, B = pb, Offset = offset });
                }
            }
            return lanes.OrderBy(l => Math.Abs(l.Offset)).ThenByDescending(l => l.Length).ToList();
        }

        private static bool TryConnect(List<V2> fence, PlanPoint from, PlanPoint to,
            double radius, double margin, out List<PlanPoint> points)
        {
            var options = new RaceAiSearch.Options
            {
                Radius = radius,
                Clearance = margin,
                Step = 0.75,
                PositionTolerance = 0.9,
                HeadingTolerance = 0.18,
                MaxExpansions = 25000
            };
            var path = RaceAiSearch.Connect(fence, from, to, options);
            points = path.Points;
            return path.Success;
        }

        public static FieldPlanResult Build(IList<V2> boundary, FieldPlanSettings settings,
            V2 start, double heading)
        {
            if (boundary == null || boundary.Count < 3 || settings == null)
                return Fail("RACE AI: missing field or settings");
            double width = settings.WorkWidth, radius = settings.TurnRadius;
            if (width < 0.5 || radius < 0.5)
                return Fail("RACE AI: invalid width or radius");

            var fence = new List<V2>(boundary);
            double margin = width * 0.5;
            // Area-weighted centroid, not the average of boundary vertices (which
            // depends on how densely each edge was recorded).
            double twiceArea = 0, cx = 0, cy = 0;
            for (int i = 0; i < fence.Count; i++)
            {
                V2 a = fence[i], b = fence[(i + 1) % fence.Count];
                double cross = a.E * b.N - b.E * a.N;
                twiceArea += cross;
                cx += (a.E + b.E) * cross;
                cy += (a.N + b.N) * cross;
            }
            V2 centre = Math.Abs(twiceArea) > 1e-8
                ? new V2(cx / (3 * twiceArea), cy / (3 * twiceArea))
                : new V2(fence.Average(p => p.E), fence.Average(p => p.N));
            if (!Inside(fence, centre, margin))
            {
                // Concave polygons can have their centroid outside the field.
                // Find the nearest usable interior point instead of rejecting it.
                V2 best = centre;
                double bestDistance = double.MaxValue;
                double minE = fence.Min(p => p.E), maxE = fence.Max(p => p.E);
                double minN = fence.Min(p => p.N), maxN = fence.Max(p => p.N);
                for (int i = 0; i <= 30; i++)
                    for (int j = 0; j <= 30; j++)
                    {
                        V2 candidate = new V2(minE + (maxE - minE) * i / 30.0,
                            minN + (maxN - minN) * j / 30.0);
                        double distance = Dist(candidate, centre);
                        if (distance < bestDistance && Inside(fence, candidate, margin))
                        {
                            best = candidate;
                            bestDistance = distance;
                        }
                    }
                if (bestDistance == double.MaxValue)
                    return Fail("RACE AI: no safe interior centre at the required clearance");
                centre = best;
            }

            // Search orientations independently; no legacy reference-edge ordering.
            var candidates = new List<Tuple<double, List<Lane>>>();
            for (int k = 0; k < 36; k++)
            {
                double a = k * Math.PI / 36.0;
                List<Lane> lanes = Generate(fence, centre, a, width, margin);
                double length = lanes.Sum(l => l.Length);
                candidates.Add(Tuple.Create(length, lanes));
            }
            candidates.Sort((a, b) => b.Item1.CompareTo(a.Item1));
            if (candidates.Count == 0 || candidates[0].Item2.Count == 0)
                return Fail("RACE AI: no usable centre-out lanes");

            // Work only on fresh lanes. Search both approach directions for each lane.
            // Fail closed if a forward-only connector cannot be found.
            var lanesBest = candidates[0].Item2;
            var result = new FieldPlanResult();
            var current = new PlanPoint { E = start.E, N = start.N, Heading = heading };
            if (!Inside(fence, start, margin))
                return Fail("RACE AI: tractor start is outside safe field clearance");
            var worked = new List<Lane>();
            foreach (Lane lane in lanesBest)
            {
                PlanPoint bestEntry = default;
                PlanPoint bestExit = default;
                List<PlanPoint> bestTurn = null;
                double bestCost = double.MaxValue;
                for (int direction = 0; direction < 2; direction++)
                {
                    V2 a = direction == 0 ? lane.A : lane.B;
                    V2 b = direction == 0 ? lane.B : lane.A;
                    double h = Heading(a, b);
                    var entry = new PlanPoint { E = a.E, N = a.N, Heading = h };
                    var exit = new PlanPoint { E = b.E, N = b.N, Heading = h };
                    if (!TryConnect(fence, current, entry, radius, margin, out List<PlanPoint> turn))
                        continue;
                    // A* connector must not traverse an already worked strip.
                    bool overlap = false;
                    foreach (PlanPoint p in turn)
                    {
                        V2 q = Pos(p);
                        foreach (Lane old in worked)
                        {
                            V2 v = old.B - old.A;
                            double t = Math.Max(0, Math.Min(1, Dot(q - old.A, v) / Dot(v, v)));
                            if (Dist(q, old.A + v * t) < margin)
                            {
                                overlap = true;
                                break;
                            }
                        }
                        if (overlap) break;
                    }
                    if (overlap) continue;
                    double cost = turn.Count * 0.75;
                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        bestTurn = turn;
                        bestEntry = entry;
                        bestExit = exit;
                    }
                }
                if (bestTurn == null)
                    return Fail("RACE AI: no safe forward-only turn to lane " + (worked.Count + 1));
                foreach (PlanPoint p in bestTurn)
                    result.Points.Add(p);
                result.Turns += worked.Count > 0 ? 1 : 0;
                double length = Dist(Pos(bestEntry), Pos(bestExit));
                int count = Math.Max(2, (int)Math.Ceiling(length / 0.75));
                for (int j = 0; j <= count; j++)
                {
                    double t = (double)j / count;
                    result.Points.Add(new PlanPoint
                    {
                        E = bestEntry.E + (bestExit.E - bestEntry.E) * t,
                        N = bestEntry.N + (bestExit.N - bestEntry.N) * t,
                        Heading = bestEntry.Heading,
                        IsWork = true
                    });
                }
                result.WorkLength += length;
                result.Passes++;
                worked.Add(lane);
                current = bestExit;
            }
            for (int i = 1; i < result.Points.Count; i++)
                result.TotalLength += Dist(Pos(result.Points[i - 1]), Pos(result.Points[i]));
            result.Ok = true;
            result.OrderDescription = "RACE AI v2: centre-out lanes, forward-only A* transitions";
            result.WorkArea = fence;
            return result;
        }
    }
}
