using System;
using System.Collections.Generic;

namespace AgOpenGPS.AutoPlan
{
    /// <summary>
    /// 2D point in local field coordinates (meters).
    /// </summary>
    public struct V2
    {
        public double E, N;
        public V2(double e, double n) { E = e; N = n; }

        public static V2 operator +(V2 a, V2 b) => new V2(a.E + b.E, a.N + b.N);
        public static V2 operator -(V2 a, V2 b) => new V2(a.E - b.E, a.N - b.N);
        public static V2 operator *(V2 a, double s) => new V2(a.E * s, a.N * s);
        public double Length => Math.Sqrt(E * E + N * N);
    }

    /// <summary>
    /// One point of the planned path. Heading uses the AgOpenGPS convention:
    /// radians, 0 = north, clockwise, direction vector = (sin h, cos h).
    /// </summary>
    public struct PlanPoint
    {
        public double E, N, Heading;
        public bool IsWork;
        public bool IsHeadland; // working point on a headland lap or headland strip
        public bool IsReverse;  // reached driving backwards (Heading is still where the vehicle faces)
    }

    /// <summary>Label for one working run of a path (pass number or headland lap).</summary>
    public struct RunLabel
    {
        public double E, N, Heading;
        public string Text;
        public bool IsHeadland;
        public int StartIndex, EndIndex; // point index range of the run in the path
    }

    public class FieldPlanSettings
    {
        /// <summary>Effective working width (tool width minus overlap), meters.</summary>
        public double WorkWidth = 6.0;

        /// <summary>Minimum turning radius of the vehicle, meters.</summary>
        public double TurnRadius = 6.0;

        /// <summary>Number of headland laps. 0 = automatic (enough room for the turns).</summary>
        public int HeadlandLaps = 0;

        /// <summary>Extra room at the end of the passes for the vehicle body during turns, meters.</summary>
        public double TurnMargin = 3.0;

        /// <summary>Drive the headland laps after all passes are done.</summary>
        public bool WorkHeadland = true;

        /// <summary>
        /// false (default): headland only at the ends of the passes, where the turns are; the first
        /// and last pass run along the long sides. true: headland all around the field.
        /// </summary>
        public bool HeadlandOnLongSides = false;

        /// <summary>Add an overlapping pass when a strip is left after the last pass.</summary>
        public bool AddExtraPass = true;

        /// <summary>Order of the passes: 0 = automatic (shortest), 1 = neighbour passes, 2 = skip passes.</summary>
        public int OrderMode = 0;

        /// <summary>
        /// true (default): turns are made out at the boundary (straight on, turn, parallel to the
        /// edge, turn back); false: shortest turn right at the pass end.
        /// </summary>
        public bool TurnsAtBoundary = true;

        /// <summary>
        /// Turns in the headland (pass to next pass, headland strip to strip) as forward/reverse
        /// manoeuvres (three point turns) into the neighbour pass instead of half turns or loops.
        /// </summary>
        public bool HeadlandReverseTurns = false;

        /// <summary>Turns and transfers never drive over worked passes (only headland and the outer pass tracks).</summary>
        public bool AvoidWorked = false;

        /// <summary>
        /// Split the field at strong inside bends into parts; every part gets passes along its own
        /// longest edge. The parts are joined by transfers.
        /// </summary>
        public bool SplitField = false;

        /// <summary>
        /// Along a gently slanted end of the field drive one pass parallel to the boundary
        /// (from one end of the slant to the other) instead of many short turns.
        /// </summary>
        public bool SlantEdgePass = false;

        /// <summary>
        /// Field in a wedge: the two long outer edges each get passes along them, worked from
        /// both sides towards the middle line between them; the passes there get shorter (gores).
        /// </summary>
        public bool WedgeField = false;

        /// <summary>Passes only inside this polygon (one half of a wedge field); null = everywhere.</summary>
        public List<V2> PassRegion;

        /// <summary>
        /// Middle line of a wedge field (a point and its normal): passes ending on it are gores,
        /// they are left out here and returned in FieldPlanResult.Gores.
        /// </summary>
        public V2? GoreLinePoint, GoreLineNormal;

        public FieldPlanSettings Clone() => (FieldPlanSettings)MemberwiseClone();

        /// <summary>
        /// Part of the boundary the passes follow (e.g. picked with 2 points on the map).
        /// null = the longest straight or gently curved edge is chosen automatically.
        /// </summary>
        public List<V2> ReferenceEdge;

        /// <summary>Distance between generated path points, meters.</summary>
        public double PointSpacing = 1.0;
    }

    public class FieldPlanResult
    {
        public bool Ok;
        public string Error;
        public List<PlanPoint> Points = new List<PlanPoint>();
        public int Passes;
        public int Turns;
        public int TurnsLeavingField;
        public int HeadlandLaps;
        public int SkippedPieces;
        public bool ExtraSidePass;     // an extra overlapping pass was added to cover the strip left after the last pass
        public double TotalLength;
        public double WorkLength;
        public string OrderDescription;

        // For debugging / visualisation
        public List<V2> WorkArea = new List<V2>();
        /// <summary>
        /// Line where the sections switch at the pass ends (the turns' room from the boundary),
        /// only at the ends; long sides stay on the boundary. Empty when the plan works the headland.
        /// </summary>
        public List<V2> SectionLine = new List<V2>();
        public List<V2> ReferenceEdge = new List<V2>();

        /// <summary>Centre line of the first pass across the whole field, for a guidance track.</summary>
        public List<V2> TrackLine = new List<V2>();

        /// <summary>Number of field parts with their own pass direction (1 = not split).</summary>
        public int PartCount = 1;

        /// <summary>Gores of a wedge half (passes ending on the middle line), see GoreLinePoint.</summary>
        public List<List<V2>> Gores = new List<List<V2>>();
    }

    /// <summary>
    /// Builds one continuous path that covers a whole field: parallel passes that follow the
    /// longest smooth edge of the boundary, Dubins turns between them in the headland
    /// (U or omega shape chosen automatically, pass order chosen to keep turns short),
    /// followed by headland laps along the boundary.
    /// Pure geometry, no dependencies on the rest of the application.
    /// </summary>
    public static class FieldPlanner
    {
        private const double TwoPi = Math.PI * 2.0;

        public static FieldPlanResult Build(IList<V2> fence, FieldPlanSettings set, V2 startPos, double startHeading)
        {
            var res = new FieldPlanResult();
            try
            {
                List<List<V2>> wedgeRefs = null, wedgeRegions = null;
                V2 midO = new V2(0, 0), midN = new V2(0, 0);
                List<List<V2>> parts = set.WedgeField ? SplitWedge(fence, WedgeTurnRoom(set), out wedgeRefs, out wedgeRegions, out midO, out midN)
                    : set.SplitField ? SplitAtBends(fence) : null;
                if (parts != null && parts.Count > 1)
                    BuildParts(parts, fence, set, startPos, startHeading, res, wedgeRefs, wedgeRegions, midO, midN);
                else
                    BuildInternal(fence, set, startPos, startHeading, res);
            }
            catch (Exception ex)
            {
                res.Ok = false;
                res.Error = ex.Message;
            }
            return res;
        }

        // =====================================================================
        // Field split into parts
        // =====================================================================

        /// <summary>
        /// Wedge field: the two longest edges of the boundary that run roughly against each other
        /// (the long outer sides) and the middle line between them (the bisector of the two edge
        /// lines). The field is cut along that line; every half gets its long edge as reference.
        /// Returns null when there is no such pair.
        /// </summary>
        // Room for the turns past the middle line of a wedge field.
        private static double WedgeTurnRoom(FieldPlanSettings set)
        {
            // the turn radius plus half the tool: sections switch right on the middle line
            double W = set.WorkWidth, R = Math.Max(0.5, set.TurnRadius);
            return R + W * 0.5 + 0.3;
        }

        public static List<List<V2>> SplitWedge(IList<V2> fenceIn, double turnRoom, out List<List<V2>> refs)
        {
            return SplitWedge(fenceIn, turnRoom, out refs, out _, out _, out _);
        }

        public static List<List<V2>> SplitWedge(IList<V2> fenceIn, double turnRoom, out List<List<V2>> refs, out List<List<V2>> regions,
            out V2 midPoint, out V2 midNormal)
        {
            refs = null;
            regions = null;
            midPoint = new V2(0, 0);
            midNormal = new V2(0, 0);
            var f = new List<V2>(fenceIn);
            if (f.Count < 4) return null;
            if (SignedArea(f) < 0) f.Reverse();
            // straight edges: corners where the direction changes by more than about 15 degrees
            var corners = new List<int>();
            int n = f.Count;
            for (int i = 0; i < n; i++)
            {
                V2 a = f[(i - 1 + n) % n], b = f[i], c = f[(i + 1) % n];
                if ((b - a).Length < 1e-6 || (c - b).Length < 1e-6) continue;
                V2 d1 = Dir(a, b), d2 = Dir(b, c);
                if (d1.E * d2.E + d1.N * d2.N < 0.966) corners.Add(i);
            }
            if (corners.Count < 3) return null;
            var edges = new List<(V2 a, V2 b, double len)>();
            for (int k = 0; k < corners.Count; k++)
            {
                V2 a = f[corners[k]], b = f[corners[(k + 1) % corners.Count]];
                edges.Add((a, b, (b - a).Length));
            }
            // the two longest edges running against each other (as two sides of a wedge)
            int e1 = -1, e2 = -1;
            double best = 0;
            for (int i = 0; i < edges.Count; i++)
                for (int j = i + 1; j < edges.Count; j++)
                {
                    V2 di = Dir(edges[i].a, edges[i].b), dj = Dir(edges[j].a, edges[j].b);
                    // on a counter clockwise boundary the two sides of a wedge point against each other
                    if (di.E * dj.E + di.N * dj.N > -0.7) continue;
                    double sc = Math.Min(edges[i].len, edges[j].len);
                    if (sc > best) { best = sc; e1 = i; e2 = j; }
                }
            if (e1 < 0 || best < 30.0) return null;

            V2 pA = edges[e1].a, dA = Dir(edges[e1].a, edges[e1].b);
            V2 pB = edges[e2].b, dB = Dir(edges[e2].b, edges[e2].a);   // same way as edge 1
            // middle line: through the middle between the edges, direction halfway between them
            V2 mDir = Dir(new V2(0, 0), dA + dB);
            V2 midA = (edges[e1].a + edges[e1].b) * 0.5, midB = (edges[e2].a + edges[e2].b) * 0.5;
            V2 m0 = (midA + midB) * 0.5;
            // lines of the two edges meet: the middle line goes through that point
            double den = dA.E * dB.N - dA.N * dB.E;
            if (Math.Abs(den) > 1e-6)
            {
                V2 w = pB - pA;
                double t = (w.E * dB.N - w.N * dB.E) / den;
                m0 = pA + dA * t;
            }
            V2 mN = new V2(-mDir.N, mDir.E);
            List<V2> left = ClipHalf(f, m0, mN, 1.0), right = ClipHalf(f, m0, mN, -1.0);
            if (left.Count < 3 || right.Count < 3) return null;
            double area = Math.Abs(SignedArea(f));
            if (Math.Abs(SignedArea(left)) < 0.15 * area || Math.Abs(SignedArea(right)) < 0.15 * area) return null;
            // every half reaches past the middle line by the turn room: its headland lies across
            // the line (turns there), its passes end on the line
            regions = new List<List<V2>> { ResampleLoop(left, 1.0), ResampleLoop(right, 1.0) };
            midPoint = m0;
            midNormal = mN;
            left = ClipHalf(f, m0 - mN * turnRoom, mN, 1.0);
            right = ClipHalf(f, m0 + mN * turnRoom, mN, -1.0);

            var parts = new List<List<V2>> { left, right };
            refs = new List<List<V2>>();
            foreach (var part in parts)
            {
                // the long edge that lies in this half
                bool aIn = SideOf(midA, m0, mN) == SideOf(Centroid(part), m0, mN);
                var e = aIn ? edges[e1] : edges[e2];
                refs.Add(new List<V2> { e.a, e.b });
            }
            return parts;
        }

        private static double SideOf(V2 p, V2 o, V2 nrm) => Math.Sign((p.E - o.E) * nrm.E + (p.N - o.N) * nrm.N);

        private static V2 Centroid(List<V2> poly)
        {
            double e = 0, nn = 0;
            foreach (V2 p in poly) { e += p.E; nn += p.N; }
            return new V2(e / poly.Count, nn / poly.Count);
        }

        // Part of the polygon on one side of the line through o with normal nrm (side = +1 / -1).
        private static List<V2> ClipHalf(List<V2> poly, V2 o, V2 nrm, double side)
        {
            var r = new List<V2>();
            int n = poly.Count;
            double D(V2 p) => side * ((p.E - o.E) * nrm.E + (p.N - o.N) * nrm.N);
            for (int i = 0; i < n; i++)
            {
                V2 a = poly[i], b = poly[(i + 1) % n];
                double da = D(a), db = D(b);
                if (da >= 0) r.Add(a);
                if ((da >= 0) != (db >= 0))
                {
                    double t = da / (da - db);
                    r.Add(a + (b - a) * t);
                }
            }
            return r;
        }

        /// <summary>
        /// Splits the boundary at strong inside bends with the shortest cut through the bend
        /// (continuing one of the two edges at the bend). At most 3 parts; each part at least 12 %
        /// of the area. Returns the boundary itself when there is no such bend.
        /// </summary>
        public static List<List<V2>> SplitAtBends(IList<V2> fenceIn)
        {
            var ring = new List<V2>(fenceIn);
            if (SignedArea(ring) < 0) ring.Reverse();
            double total = Math.Abs(SignedArea(ring));
            var parts = new List<List<V2>> { ring };
            for (int round = 0; round < 2; round++)
            {
                bool split = false;
                for (int k = 0; k < parts.Count && !split; k++)
                {
                    if (TryCut(parts[k], total, out List<V2> a, out List<V2> b))
                    {
                        parts.RemoveAt(k);
                        parts.Add(a);
                        parts.Add(b);
                        split = true;
                    }
                }
                if (!split) break;
            }
            return parts;
        }

        private static bool TryCut(List<V2> poly, double totalArea, out List<V2> partA, out List<V2> partB)
        {
            partA = partB = null;
            int n = poly.Count;
            if (n < 6) return false;
            List<int> corners = CornerIndices(poly);
            int m = corners.Count;
            if (m < 5) return false;

            double bestLen = double.MaxValue;
            for (int c = 0; c < m; c++)
            {
                V2 prev = poly[corners[(c - 1 + m) % m]], cur = poly[corners[c]], next = poly[corners[(c + 1) % m]];
                V2 din = Dir(prev, cur), dout = Dir(cur, next);
                double cross = din.E * dout.N - din.N * dout.E;
                double turn = Math.Atan2(cross, din.E * dout.E + din.N * dout.N);
                if (turn > -25.0 * Math.PI / 180.0) continue;   // not an inside (reflex) bend of a CCW boundary

                foreach (V2 dir in new[] { din, dout * -1.0 })
                {
                    if (!RayHit(poly, corners[c], dir, out int edge, out V2 hit, out double len)) continue;
                    if (len < 5.0 || len >= bestLen) continue;
                    List<V2> a = new List<V2>(), b = new List<V2>();
                    int ci = corners[c];
                    // a: corner .. start of the hit edge, hit point;  b: hit point, end of the hit edge .. corner
                    for (int i = ci; ; i = (i + 1) % n) { a.Add(poly[i]); if (i == edge) break; }
                    a.Add(hit);
                    b.Add(hit);
                    for (int i = (edge + 1) % n; ; i = (i + 1) % n) { b.Add(poly[i]); if (i == ci) break; }
                    double aa = Math.Abs(SignedArea(a)), ab = Math.Abs(SignedArea(b));
                    if (aa < 0.12 * totalArea || ab < 0.12 * totalArea) continue;
                    bestLen = len;
                    partA = a;
                    partB = b;
                }
            }
            return partA != null;
        }

        // First crossing of a ray from poly[from] with the boundary (not the edges at the start point)
        private static bool RayHit(List<V2> poly, int from, V2 dir, out int edge, out V2 hit, out double dist)
        {
            edge = -1; hit = default; dist = double.MaxValue;
            int n = poly.Count;
            V2 o = poly[from];
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                if (i == from || j == from || i == (from - 1 + n) % n) continue;
                V2 a = poly[i], b = poly[j];
                V2 e = b - a;
                double den = dir.E * e.N - dir.N * e.E;
                if (Math.Abs(den) < 1e-12) continue;
                V2 ao = a - o;
                double t = (ao.E * e.N - ao.N * e.E) / den;     // along the ray
                double u = (ao.E * dir.N - ao.N * dir.E) / den; // along the edge
                if (t <= 0.5 || u < 0 || u > 1) continue;
                if (t < dist) { dist = t; edge = i; hit = o + dir * t; }
            }
            return edge >= 0;
        }

        // Corners of the boundary (indices), found by simplifying it
        private static List<int> CornerIndices(List<V2> pts)
        {
            double tol = Math.Max(1.5, LoopLength(pts) / 300.0);
            int far = 0;
            double farD = 0;
            for (int i = 1; i < pts.Count; i++)
            {
                double d = (pts[i] - pts[0]).Length;
                if (d > farD) { farD = d; far = i; }
            }
            var keep = new bool[pts.Count];
            keep[0] = keep[far] = true;
            Simplify(pts, 0, far, tol, keep);
            var second = new List<V2>(pts.GetRange(far, pts.Count - far)) { pts[0] };
            var keep2 = new bool[second.Count];
            Simplify(second, 0, second.Count - 1, tol, keep2);
            for (int i = 1; i < second.Count - 1; i++) if (keep2[i]) keep[far + i] = true;
            var idx = new List<int>();
            for (int i = 0; i < pts.Count; i++) if (keep[i]) idx.Add(i);
            return idx;
        }

        /// <summary>Plans every part with its own direction and joins them, nearest part next.</summary>
        private static void BuildParts(List<List<V2>> parts, IList<V2> fenceIn, FieldPlanSettings set, V2 startPos, double startHeading, FieldPlanResult res,
            List<List<V2>> partRefs = null, List<List<V2>> partRegions = null, V2 midO = default, V2 midN = default)
        {
            var goresOf = new Dictionary<int, List<List<V2>>>();
            double W = set.WorkWidth, R = Math.Max(0.5, set.TurnRadius);
            var full = new List<V2>(fenceIn);
            if (SignedArea(full) < 0) full.Reverse();
            double s = Math.Max(set.PointSpacing, LoopLength(full) / 6000.0);
            full = ResampleLoop(full, s);
            var fullGrid = new PolygonTester(full);
            fullGrid.SetClearance(full, Math.Max(0.0, W * 0.5 - 0.3));
            List<V2> loop = BuildTransferLoop(full, W, R, s, fullGrid);

            V2 pos = startPos;
            double heading = startHeading;
            var left = new List<List<V2>>(parts);
            var names = new List<string>();
            bool first = true;
            while (left.Count > 0)
            {
                // nearest part next
                int bi = 0;
                double bd = double.MaxValue;
                for (int k = 0; k < left.Count; k++)
                    foreach (V2 q in left[k]) { double d = (q - pos).Length; if (d < bd) { bd = d; bi = k; } }
                List<V2> part = left[bi];
                left.RemoveAt(bi);

                FieldPlanSettings ps = set.Clone();
                ps.ReferenceEdge = null;
                if (partRefs != null)
                {
                    int pi = parts.IndexOf(part);
                    if (pi >= 0 && pi < partRefs.Count) ps.ReferenceEdge = partRefs[pi];
                }
                if (partRegions != null)
                {
                    int pi = parts.IndexOf(part);
                    if (pi >= 0 && pi < partRegions.Count) ps.PassRegion = partRegions[pi];
                    // the far side of a half is the middle line: no extra pass along it
                    ps.AddExtraPass = false;
                    // passes ending on the middle line (gores) are driven at the end, both halves together
                    ps.GoreLinePoint = midO;
                    ps.GoreLineNormal = midN;
                    // few full passes in a half: skip one, so the turns at the ends fit without
                    // loops or transfers
                    if (ps.OrderMode == 0 && !ps.HeadlandReverseTurns) ps.OrderMode = 2;
                }
                ps.SplitField = false;
                ps.WedgeField = false;
                var r = new FieldPlanResult();
                BuildInternal(part, ps, pos, heading, r);
                if (r.Gores.Count > 0) goresOf[parts.IndexOf(part)] = r.Gores;
                if (!r.Ok || r.Points.Count < 2) continue;

                if (!first)
                {
                    PlanPoint l = res.Points[res.Points.Count - 1];
                    PlanPoint f = r.Points[0];
                    // wedge halves meet side by side: a three point turn instead of a loop
                    List<ReverseManeuver.Pose> man = partRegions != null
                        ? ReverseManeuver.KTurn(new V2(l.E, l.N), l.Heading, new V2(f.E, f.N), f.Heading, R, s, fullGrid.Clear, false)
                        : null;
                    if (man != null)
                    {
                        AddManoeuvre(res, man);
                        res.Turns++;
                    }
                    var conn = man != null ? null : Connect(new V2(l.E, l.N), l.Heading, new V2(f.E, f.N), f.Heading, R, W, s, fullGrid, loop);
                    if (conn != null)
                    {
                        if (CountOutside(conn, fullGrid) > 0) res.TurnsLeavingField++;
                        for (int i = 1; i < conn.Count - 1; i++)
                        {
                            V2 a = conn[i - 1], b = conn[i];
                            res.Points.Add(new PlanPoint { E = b.E, N = b.N, Heading = HeadingOf(a, b), IsWork = false });
                        }
                        res.Turns++;
                    }
                }
                else
                {
                    res.ReferenceEdge = r.ReferenceEdge;
                    res.WorkArea = r.WorkArea;
                    res.TrackLine = r.TrackLine;
                    first = false;
                }
                res.Points.AddRange(r.Points);
                res.Passes += r.Passes;
                res.Turns += r.Turns;
                res.TurnsLeavingField += r.TurnsLeavingField;
                res.HeadlandLaps += r.HeadlandLaps;
                res.SkippedPieces += r.SkippedPieces;
                res.ExtraSidePass |= r.ExtraSidePass;
                names.Add(r.OrderDescription);

                PlanPoint last = r.Points[r.Points.Count - 1];
                pos = new V2(last.E, last.N);
                heading = last.Heading;
            }
            if (goresOf.Count > 0) AddGores(goresOf, midO, midN, set, R, W, s, fullGrid, loop, res);
            if (res.Points.Count < 2) { res.Error = "No passes fit inside the field"; return; }

            res.OrderDescription = names.Count > 0 ? names[0] : "";
            for (int i = 1; i < res.Points.Count; i++)
            {
                double seg = Math.Sqrt(Sq(res.Points[i].E - res.Points[i - 1].E) + Sq(res.Points[i].N - res.Points[i - 1].N));
                res.TotalLength += seg;
                if (res.Points[i].IsWork && res.Points[i - 1].IsWork) res.WorkLength += seg;
            }
            res.PartCount = parts.Count;
            res.Ok = true;
        }

        // Points of a forward / backward manoeuvre (not the end points: they are the pass ends)
        private static void AddManoeuvre(FieldPlanResult res, List<ReverseManeuver.Pose> man)
        {
            for (int i = 1; i < man.Count - 1; i++)
            {
                V2 a = man[i - 1].P, b = man[i].P;
                double hd = HeadingOf(a, b);
                if (man[i].Reverse) hd = (hd + Math.PI) % TwoPi;
                res.Points.Add(new PlanPoint { E = b.E, N = b.N, Heading = hd, IsWork = false, IsReverse = man[i].Reverse });
            }
        }

        /// <summary>
        /// Gores of a wedge field, after all the full passes: from the shortest to the longest,
        /// one half up to the middle line, a three point turn there (over the unworked longer
        /// gores) into the gore of the other half, down it, along the headland to the next one.
        /// </summary>
        private static void AddGores(Dictionary<int, List<List<V2>>> goresOf, V2 midO, V2 midN, FieldPlanSettings set,
            double R, double W, double s, PolygonTester fullGrid, List<V2> loop, FieldPlanResult res)
        {
            double DistMid(V2 p) => Math.Abs((p.E - midO.E) * midN.E + (p.N - midO.N) * midN.N);
            // every gore from its outer end to its end on the middle line, shortest first
            List<List<V2>> Oriented(int key)
            {
                var list = new List<List<V2>>();
                if (!goresOf.TryGetValue(key, out var g)) return list;
                foreach (var gore in g)
                {
                    var pts = new List<V2>(gore);
                    if (DistMid(pts[0]) < DistMid(pts[pts.Count - 1])) pts.Reverse();
                    if (pts.Count < 2) continue;
                    // straight on to the middle line itself (the work area stops a bit before it)
                    V2 end = pts[pts.Count - 1];
                    V2 dir = Dir(pts[Math.Max(0, pts.Count - 4)], end);
                    double toward = -Math.Sign((end.E - midO.E) * midN.E + (end.N - midO.N) * midN.N);
                    double cos = Math.Abs(dir.E * midN.E + dir.N * midN.N);
                    if (cos > 0.02)
                    {
                        double ext = DistMid(end) / cos;
                        V2 dn = midN * toward;
                        if (dir.E * dn.E + dir.N * dn.N > 0)
                            for (double t = s; t <= ext; t += s) pts.Add(end + dir * t);
                    }
                    list.Add(pts);
                }
                list.Sort((a, b) => OpenLength(a).CompareTo(OpenLength(b)));
                return list;
            }
            List<List<V2>> up = Oriented(0), down = Oriented(1);
            if (up.Count == 0 && down.Count == 0) return;
            if (up.Count == 0) { up = down; down = new List<List<V2>>(); }

            void AddWork(List<V2> pts)
            {
                for (int i = 0; i < pts.Count; i++)
                {
                    V2 a = pts[Math.Max(0, i - 1)], b = pts[Math.Min(pts.Count - 1, Math.Max(1, i))];
                    res.Points.Add(new PlanPoint { E = pts[i].E, N = pts[i].N, Heading = HeadingOf(a, b), IsWork = true });
                }
                res.Passes++;
            }
            void AddTurn(List<V2> conn)
            {
                for (int i = 1; i < conn.Count - 1; i++)
                    res.Points.Add(new PlanPoint { E = conn[i].E, N = conn[i].N, Heading = HeadingOf(conn[i - 1], conn[i]), IsWork = false });
                if (CountOutside(conn, fullGrid) > 0) res.TurnsLeavingField++;
                res.Turns++;
            }
            bool ConnectTo(List<V2> pts)
            {
                if (res.Points.Count == 0) return true;
                PlanPoint l = res.Points[res.Points.Count - 1];
                double h1 = HeadingOf(pts[0], pts[Math.Min(3, pts.Count - 1)]);
                // side by side: three point turn
                var km = ReverseManeuver.KTurn(new V2(l.E, l.N), l.Heading, pts[0], h1, R, s, fullGrid.Clear);
                if (km != null) { AddManoeuvre(res, km); res.Turns++; return true; }
                var conn = Connect(new V2(l.E, l.N), l.Heading, pts[0], h1, R, W, s, fullGrid, loop, set.TurnsAtBoundary);
                if (conn == null) return false;
                AddTurn(conn);
                return true;
            }

            var order = new List<List<V2>>();
            for (int i = 0; i < Math.Max(up.Count, down.Count); i++)
            {
                if (i < up.Count) order.Add(up[i]);
                if (i < down.Count)
                {
                    var d = new List<V2>(down[i]);
                    d.Reverse();     // from the middle line down
                    order.Add(d);
                }
            }
            for (int k = 0; k < order.Count; k++)
            {
                List<V2> pts = order[k];
                bool fromMiddle = DistMid(pts[0]) < 2.0;
                bool done = false;
                if (fromMiddle && res.Points.Count > 0)
                {
                    // the previous gore ended on the middle line: three point turn into this one
                    PlanPoint l = res.Points[res.Points.Count - 1];
                    double h1 = HeadingOf(pts[0], pts[Math.Min(3, pts.Count - 1)]);
                    var man = ReverseManeuver.KTurn(new V2(l.E, l.N), l.Heading, pts[0], h1, R, s, fullGrid.Clear, false)
                        ?? ReverseManeuver.Find(new V2(l.E, l.N), l.Heading, pts[0], h1, R, s, fullGrid.Clear);
                    if (man != null)
                    {
                        AddManoeuvre(res, man);
                        res.Turns++;
                        done = true;
                    }
                }
                if (!done && !ConnectTo(pts)) continue;
                AddWork(pts);
            }
        }

        private static void BuildInternal(IList<V2> fenceIn, FieldPlanSettings set, V2 startPos, double startHeading, FieldPlanResult res)
        {
            double W = set.WorkWidth;
            double R = set.TurnRadius;
            if (fenceIn == null || fenceIn.Count < 3) { res.Error = "No boundary"; return; }
            if (W < 0.5) { res.Error = "Work width too small"; return; }
            if (R < 0.5) R = 0.5;

            // --- Boundary, resampled and counter clockwise ---
            var fenceRaw = new List<V2>(fenceIn);
            if (SignedArea(fenceRaw) < 0) fenceRaw.Reverse();
            double perimeter = LoopLength(fenceRaw);
            double s = Math.Max(set.PointSpacing, perimeter / 6000.0);
            List<V2> fence = ResampleLoop(fenceRaw, s);
            if (fence.Count < 10) { res.Error = "Boundary too small"; return; }

            GetBounds(fence, out V2 bbMin, out V2 bbMax);
            double diag = (bbMax - bbMin).Length;

            // --- Headland width ---
            int laps = set.HeadlandLaps;
            // the turn reaches R past the pass end and the tool sticks out half a width more
            if (laps <= 0) laps = Math.Max(1, (int)Math.Ceiling((R + Math.Max(set.TurnMargin, W * 0.5 + 1.0)) / W));
            // three point turns into the neighbour pass: straight on, back, forward into the pass
            // lined up - they need room for the turns coming back (2R sin b, cos b = W / 2R)
            if (set.HeadlandReverseTurns && W < 2.0 * R)
            {
                double need = ReverseManeuver.KTurnReach(W, R) + W * 0.5 + 1.0;
                laps = Math.Max(laps, (int)Math.Ceiling(need / W));
            }
            double headWidth = laps * W;

            // --- Reference edge: longest smooth part of the boundary ---
            List<V2> refEdge = set.ReferenceEdge != null && set.ReferenceEdge.Count >= 2
                ? ResampleOpen(new List<V2>(set.ReferenceEdge), s)
                : FindReferenceEdge(fence, s);
            if (refEdge.Count < 5) { res.Error = "No usable edge found"; return; }
            refEdge = SmoothOpen(refEdge, 7, 2);
            res.ReferenceEdge = refEdge;

            // Extend straight at both ends so offsets cross the whole field
            List<V2> refExt = ExtendOpen(refEdge, diag, s);

            // Which side of the edge is inside the field? (fence is CCW, so normally left)
            int mid = refEdge.Count / 2;
            V2 midDir = Dir(refEdge[Math.Max(0, mid - 2)], refEdge[Math.Min(refEdge.Count - 1, mid + 2)]);
            V2 leftN = new V2(-midDir.N, midDir.E);
            double side = PointInPolygon(fence, refEdge[mid] + leftN * (W * 0.5)) ? 1.0 : -1.0;

            // Pass centre lines: each one is offset from the previous one by the work width and
            // rounded to a minimum radius, so bends of the edge straighten out towards the inside
            // of the field instead of folding into sharp kinks
            double minRadius = Math.Max(1.5 * R, 10.0);
            var lanesEnds = new LaneLines(refExt, side, W, W * 0.5, minRadius, s);

            // --- Work area: headland only at the pass ends, or all around ---
            bool endsOnly = !set.HeadlandOnLongSides;
            int[] endRunId = null;
            List<V2> work = null;
            if (endsOnly)
            {
                endRunId = ClassifyEnds(fence, lanesEnds, W, diag, s);
                bool anyEnd = false, anySide = false;
                foreach (int id in endRunId) { if (id >= 0) anyEnd = true; else anySide = true; }
                if (anyEnd && anySide)
                    // slanted ends get a wider headland, so turns between passes fit without loops
                    work = InsetEnds(fence, endRunId, headWidth, s, midDir, set.WorkHeadland || set.HeadlandReverseTurns ? 0.0 : Math.Max(2.0 * R, W));
                else
                    endsOnly = false; // no clear long sides (round field): headland all around
            }
            if (!endsOnly) work = InsetLoop(fence, headWidth, s);

            if (work == null || work.Count < 10 || Math.Abs(SignedArea(work)) < W * W * 4)
            {
                res.Error = "Field too small for headland";
                return;
            }
            res.WorkArea = work;

            // --- Passes ---
            LaneLines lanes = endsOnly ? lanesEnds : new LaneLines(refExt, side, W, headWidth + W * 0.5, minRadius, s);
            var passes = new List<Pass>();
            int emptyInRow = 0;
            for (int lane = 0; lane < 10000; lane++)
            {
                double d = (endsOnly ? 0.0 : headWidth) + W * (lane + 0.5);
                if (d > diag + headWidth) break;

                List<V2> off = lanes.Get(lane);
                if (off.Count < 2) break;
                List<List<V2>> runs = InsideRuns(off, work);
                if (set.PassRegion != null && set.PassRegion.Count >= 3)
                {
                    var clipped = new List<List<V2>>();
                    foreach (var run in runs) clipped.AddRange(InsideRuns(run, set.PassRegion));
                    runs = clipped;
                }
                double minLen = Math.Max(2.0 * W, 5.0);
                List<V2> best = null;
                int pieces = 0;
                foreach (var run in runs)
                {
                    if (OpenLength(run) < minLen) continue;
                    pieces++;
                    if (best == null || run.Count > best.Count) best = run;
                }
                if (best == null)
                {
                    if (passes.Count > 0 && ++emptyInRow > 3) break;
                    continue;
                }
                emptyInRow = 0;
                res.SkippedPieces += pieces - 1;
                if (set.GoreLinePoint.HasValue && set.GoreLineNormal.HasValue)
                {
                    V2 go = set.GoreLinePoint.Value, gn = set.GoreLineNormal.Value;
                    double DistMid(V2 p) => Math.Abs((p.E - go.E) * gn.E + (p.N - go.N) * gn.N);
                    if (DistMid(best[0]) < W * 0.6 || DistMid(best[best.Count - 1]) < W * 0.6)
                    {
                        res.Gores.Add(ResampleOpen(best, s));
                        continue;
                    }
                }
                passes.Add(new Pass { Lane = lane, Pts = ResampleOpen(best, s) });
            }

            if (passes.Count == 0) { res.Error = "No passes fit inside the field"; return; }

            // Guidance track: first pass line over the whole field (not cut at the headland)
            {
                List<V2> line = lanes.Get(0);
                List<V2> bestRun = null;
                foreach (var run in InsideRuns(line, fence))
                    if (bestRun == null || run.Count > bestRun.Count) bestRun = run;
                if (bestRun != null && bestRun.Count > 2) res.TrackLine = bestRun;
            }

            // Headland only at the ends: if a strip is left along the far long side, add one more
            // pass (overlapping the previous one) with the tool edge on the boundary
            if (endsOnly && set.AddExtraPass)
            {
                Pass last = passes[passes.Count - 1];
                double lastLen = OpenLength(last.Pts);
                // distance to the work area edge: equal to the boundary along a long side, the inner
                // edge of the headland at the ends (the extra pass must not run into the headland)
                var fenceSeg = new SegmentGrid(work, true, Math.Max(5.0, W));
                for (double f = 1.0; f >= 0.25; f -= 0.05)
                {
                    List<V2> off = lanes.Beyond(last.Lane, f);
                    List<V2> best = null;
                    foreach (var run in InsideRuns(off, work))
                        if (best == null || run.Count > best.Count) best = run;
                    if (best == null || OpenLength(best) < 0.5 * lastLen) continue;

                    // tool must stay inside: centre at least half a width from the boundary
                    var dists = new List<double>(best.Count);
                    foreach (V2 q in best) dists.Add(fenceSeg.MinDistance(q, W));
                    dists.Sort();
                    if (dists[dists.Count / 2] < W * 0.5 - 0.2) continue;

                    passes.Add(new Pass { Lane = last.Lane + 1, Pts = ResampleOpen(best, s) });
                    res.ExtraSidePass = true;
                    break;
                }
            }

            // --- Order passes and build turns ---
            var fenceGrid = new PolygonTester(fence);
            // turns and transfers must keep the tool inside: half a width from the boundary
            fenceGrid.SetClearance(fence, Math.Max(0.0, W * 0.5 - 0.3));
            // pass along gently slanted ends, parallel to the boundary: driven first, from its end
            // nearest to the start, then the other passes from its far end
            Step slantFirst = null;
            if (endsOnly && set.SlantEdgePass)
            {
                List<Pass> slant = SlantEdgePasses(fence, endRunId, midDir, W, R, s, passes);
                Pass firstSlant = null;
                double fd = double.MaxValue;
                foreach (Pass sp in slant)
                {
                    double d = Math.Min((sp.A - startPos).Length, (sp.B - startPos).Length);
                    if (d < fd) { fd = d; firstSlant = sp; }
                }
                foreach (Pass sp in slant) if (sp != firstSlant) passes.Add(sp);
                if (firstSlant != null)
                    slantFirst = new Step { Pass = firstSlant, Reverse = (firstSlant.B - startPos).Length < (firstSlant.A - startPos).Length };
            }

            // no driving over worked passes: the work area without its outer pass strip is off limits
            if (set.AvoidWorked)
            {
                List<V2> core = InsetLoop(work, W, s);
                if (core.Count >= 10) fenceGrid.SetForbidden(core);
            }

            // drive line along the boundary (outer headland lap / first pass) for long transfers
            List<V2> transferLoop = BuildTransferLoop(fence, W, R, s, fenceGrid);

            // with reverse turns the neighbour pass is next (no skipping needed)
            int orderMode = set.HeadlandReverseTurns && set.OrderMode == 0 ? 1 : set.OrderMode;
            // after the pass along the slanted edge the other passes are done with skipping
            if (slantFirst != null && set.OrderMode == 0 && !set.HeadlandReverseTurns) orderMode = 2;
            V2 orderStart = startPos;
            double orderHeading = startHeading;
            if (slantFirst != null) ExitPose(slantFirst, out orderStart, out orderHeading);
            // a wedge half: the neighbour pass (only one left) is reached with a three point turn
            bool wedgeKTurns = !set.HeadlandReverseTurns && set.GoreLinePoint.HasValue;
            List<Step> order = ChooseOrder(passes, R, W, fenceGrid, orderStart, orderHeading, s, transferLoop, orderMode, out string orderName,
                set.HeadlandReverseTurns || wedgeKTurns);
            if (slantFirst != null) order.Insert(0, slantFirst);
            else if (!set.HeadlandReverseTurns) order = AlternateSlant(order, passes, fenceGrid, R);
            res.OrderDescription = orderName;

            var path = new List<PlanPoint>();
            // farthest point from the boundary where a pass still runs straight at a turn: the
            // sections' switch line goes there, so every pass reaches it (a straight end)
            var straightEndByEnd = new Dictionary<int, double>();
            // every straight piece of an end (split at its corners) gets its own distance
            int[] edgeKey = endRunId == null ? null : EdgePieces(fence, endRunId);
            void NoteStraightEnd(V2 p, double d)
            {
                if (endRunId == null) return;
                int best = -1;
                double bd = double.MaxValue;
                for (int k = 0; k < fence.Count; k++)
                {
                    if (endRunId[k] < 0) continue;
                    double q = Sq(fence[k].E - p.E) + Sq(fence[k].N - p.N);
                    if (q < bd) { bd = q; best = k; }
                }
                if (best < 0) return;
                int id = edgeKey[best];
                straightEndByEnd[id] = Math.Max(straightEndByEnd.TryGetValue(id, out double v) ? v : R + W * 0.5, d);
            }
            for (int i = 0; i < order.Count; i++)
            {
                Step st = order[i];
                List<V2> pts = st.Pass.Pts;
                if (st.Reverse) { pts = new List<V2>(pts); pts.Reverse(); }

                if (i > 0 && set.HeadlandReverseTurns)
                {
                    LastPose(path, s, out V2 lastP, out double lastH);
                    double entryH = HeadingOf(pts[0], pts[Math.Min(3, pts.Count - 1)]);
                    if ((pts[0] - lastP).Length <= 2.0 * R + 4.0 * W + 40.0)
                    {
                        // the three point turn; near corners the pass may run on too close to the
                        // boundary for it: then it starts a bit earlier on the pass, but never before
                        // the headland line (the pass is worked up to there anyway)
                        double lineRev = ReverseManeuver.KTurnReach(W, R) + W * 0.5 + 0.3;
                        List<ReverseManeuver.Pose> man = null;
                        int cut = path.Count;
                        for (int j = path.Count - 1; j >= 0; j--)
                        {
                            V2 pj = new V2(path[j].E, path[j].N);
                            if ((pj - lastP).Length > 2.0 * R) break;
                            if (j < path.Count - 1 && AheadEdgeDistance(fenceGrid, pj, lastH, 100.0) > lineRev + 0.5) break;
                            man = ReverseManeuver.KTurn(pj, lastH, pts[0], entryH, R, s, fenceGrid.Clear);
                            if (man != null) { cut = j + 1; break; }
                        }
                        if (man == null) man = ReverseManeuver.Find(lastP, lastH, pts[0], entryH, R, s, fenceGrid.Clear);
                        else if (cut < path.Count) path.RemoveRange(cut, path.Count - cut);
                        if (man != null)
                        {
                            foreach (var q in man)
                                path.Add(new PlanPoint { E = q.P.E, N = q.P.N, IsWork = false, IsReverse = q.Reverse });
                            res.Turns++;
                            AppendPoints(path, pts, true, true);
                            continue;
                        }
                    }
                }
                if (i > 0 && wedgeKTurns)
                {
                    LastPose(path, s, out V2 lp, out double lh);
                    double eh = HeadingOf(pts[0], pts[Math.Min(3, pts.Count - 1)]);
                    V2 dd = pts[0] - lp;
                    double lat = Math.Abs(dd.E * Math.Cos(lh) - dd.N * Math.Sin(lh));
                    if (lat < 2.0 * R - 0.5 && Math.Abs(AngleDiff(eh, lh + Math.PI)) < 0.3)
                    {
                        var man = ReverseManeuver.KTurn(lp, lh, pts[0], eh, R, s, fenceGrid.Clear)
                            ?? ReverseManeuver.Find(lp, lh, pts[0], eh, R, s, fenceGrid.Clear);
                        if (man != null)
                        {
                            foreach (var q in man)
                                path.Add(new PlanPoint { E = q.P.E, N = q.P.N, IsWork = false, IsReverse = q.Reverse });
                            res.Turns++;
                            AppendPoints(path, pts, true, true);
                            continue;
                        }
                    }
                }
                if (i > 0)
                {
                    LastPose(path, s, out V2 lastP, out double lastH);
                    double entryH = HeadingOf(pts[0], pts[Math.Min(3, pts.Count - 1)]);
                    var turn = Connect(lastP, lastH, pts[0], entryH, R, W, s, fenceGrid, transferLoop, set.TurnsAtBoundary);
                    if (turn == null) { res.Error = "Turn could not be built"; return; }
                    if (CountOutside(turn, fenceGrid) > 0) res.TurnsLeavingField++;
                    if (set.TurnsAtBoundary)
                    {
                        // the straight run on to the edge and back is worked too (sections stay on
                        // until the vehicle starts to turn), so only a narrow strip is left unworked
                        int k0 = StraightCount(turn, lastH, false), k1 = StraightCount(turn, entryH, true);
                        if (k0 + k1 >= turn.Count) { k0 = 0; k1 = 0; }
                        {
                            double d0 = AheadEdgeDistance(fenceGrid, turn[Math.Max(0, k0 - 1)], lastH, 100.0);
                            double d1 = AheadEdgeDistance(fenceGrid, turn[turn.Count - Math.Max(1, k1)], entryH, 100.0);
                            // only real turns at the ends, not transfers far from the edge
                            if (d0 < R + W * 0.5 + 2.0 * R) { NoteStraightEnd(turn[Math.Max(0, k0 - 1)], d0); }
                            if (d1 < R + W * 0.5 + 2.0 * R) { NoteStraightEnd(turn[turn.Count - Math.Max(1, k1)], d1); }
                        }

                        // sections switch off and on always at the same distance from the boundary:
                        // the room the turn needs (turning radius plus half the tool)
                        // (and for both legs of a turn the same, the leg that must start turning
                        // farther from the boundary sets it), so on one edge they line up
                        double switchDist = R + W * 0.5;
                        if (k0 > 1) switchDist = Math.Max(switchDist, AheadEdgeDistance(fenceGrid, turn[k0 - 1], lastH, 100.0));
                        if (k1 > 1) switchDist = Math.Max(switchDist, AheadEdgeDistance(fenceGrid, turn[turn.Count - k1], entryH, 100.0));
                        for (int j = 0; j < k0; j++)
                            if (AheadEdgeDistance(fenceGrid, turn[j], lastH, switchDist + 1.0) < switchDist) { k0 = j; break; }
                        for (int j = 0; j < k1; j++)
                            if (AheadEdgeDistance(fenceGrid, turn[turn.Count - 1 - j], entryH, switchDist + 1.0) < switchDist) { k1 = j; break; }
                        if (k0 > 1) AppendPoints(path, turn.GetRange(0, k0), true, true);
                        AppendPoints(path, turn.GetRange(Math.Max(0, k0 - 1), turn.Count - Math.Max(0, k0 - 1) - Math.Max(0, k1 - 1)), false, true);
                        if (k1 > 1) AppendPoints(path, turn.GetRange(turn.Count - k1, k1), true, true);
                    }
                    else AppendPoints(path, turn, false, true);
                    res.Turns++;
                }
                AppendPoints(path, pts, true, i > 0);
            }
            res.Passes = order.Count;

            if (endsOnly && !set.WorkHeadland)
            {
                var byEnd = new Dictionary<int, double>();
                foreach (var kv in straightEndByEnd) byEnd[kv.Key] = kv.Value + 0.3;
                // with three point turns the work goes on up to where the turn starts
                double lineDist = set.HeadlandReverseTurns ? ReverseManeuver.KTurnReach(W, R) + W * 0.5 + 0.3 : R + W * 0.5 + 0.3;
                // (three point turns all start at the same distance: one line for every end)
                List<V2> line = InsetEnds(fence, endRunId, lineDist, s, default, 0.0, set.HeadlandReverseTurns ? null : byEnd, edgeKey, true);
                if (line != null && line.Count >= 10) res.SectionLine = line;
            }

            // --- Headland: strips at the pass ends, or laps all around ---
            if (set.WorkHeadland && endsOnly)
            {
                BuildEndStrips(fence, endRunId, laps, W, R, s, fenceGrid, path, res, transferLoop, set.HeadlandReverseTurns);
            }
            else if (set.WorkHeadland)
            {
                for (int j = 0; j < laps; j++)
                {
                    double d = headWidth - W * (j + 0.5);
                    List<V2> lap = InsetLoop(fence, d, s);
                    if (lap.Count < 10) continue;
                    List<V2> sharp = lap;
                    lap = RoundLoop(lap, R, s);
                    // Rounding must never push the path over the boundary
                    for (int k = 0; k < lap.Count; k++)
                        if (!fenceGrid.Inside(lap[k])) lap[k] = sharp[NearestIndex(sharp, lap[k])];

                    LastPose(path, s, out V2 cur, out double curH);

                    // Nearest point, then go a bit ahead so the approach is a smooth S-curve
                    int idx = NearestIndex(lap, cur);
                    double hAtIdx = HeadingOf(lap[idx], lap[(idx + 1) % lap.Count]);
                    bool forward = Math.Abs(AngleDiff(hAtIdx, curH)) < Math.PI / 2;
                    if (!forward) lap.Reverse();
                    idx = NearestIndex(lap, cur);
                    double ahead = 2.0 * Math.Sqrt(R * W) + 2.0 * R;
                    int shift = (int)Math.Ceiling(ahead / s);
                    idx = (idx + shift) % lap.Count;

                    var loop = new List<V2>(lap.Count + 1);
                    for (int k = 0; k <= lap.Count; k++) loop.Add(lap[(idx + k) % lap.Count]);

                    var conn = Connect(cur, curH, loop[0], HeadingOf(loop[0], loop[Math.Min(3, loop.Count - 1)]), R, W, s, fenceGrid, transferLoop);
                    if (conn != null)
                    {
                        if (CountOutside(conn, fenceGrid) > 0) res.TurnsLeavingField++;
                        AppendPoints(path, conn, false, true);
                    }
                    AppendPoints(path, loop, true, true, true);
                    res.HeadlandLaps++;
                }
            }

            // --- Final resampling and headings ---
            res.Points = Finish(path, s);
            for (int i = 1; i < res.Points.Count; i++)
            {
                double seg = Math.Sqrt(Sq(res.Points[i].E - res.Points[i - 1].E) + Sq(res.Points[i].N - res.Points[i - 1].N));
                res.TotalLength += seg;
                if (res.Points[i].IsWork && res.Points[i - 1].IsWork) res.WorkLength += seg;
            }
            res.Ok = true;
        }

        /// <summary>
        /// Moves the work (section on/off) flags along the path by the distance the tool is
        /// behind the vehicle pivot (negative: tool in front). The path is for the pivot, so
        /// without this the sections switch off when the vehicle, not the tool, reaches the end
        /// of a pass, leaving an unworked gap of that length at every pass end and start.
        /// </summary>
        /// <summary>
        /// Straight edges of the boundary (corners found by simplifying it), longest first, as
        /// two point lines: choices for the edge the passes follow.
        /// </summary>
        public static List<List<V2>> EdgeCandidates(IList<V2> fenceIn, int max)
        {
            var result = new List<List<V2>>();
            if (fenceIn == null || fenceIn.Count < 3) return result;
            var pts = new List<V2>(fenceIn);
            double tol = Math.Max(1.5, LoopLength(pts) / 300.0);

            // corners: Douglas-Peucker on the ring opened at point 0 and at the farthest point
            int far = 0;
            double farD = 0;
            for (int i = 1; i < pts.Count; i++)
            {
                double d = (pts[i] - pts[0]).Length;
                if (d > farD) { farD = d; far = i; }
            }
            var keep = new bool[pts.Count];
            keep[0] = keep[far] = true;
            Simplify(pts, 0, far, tol, keep);
            var second = new List<V2>(pts.GetRange(far, pts.Count - far)) { pts[0] };
            var keep2 = new bool[second.Count];
            Simplify(second, 0, second.Count - 1, tol, keep2);
            for (int i = 1; i < second.Count - 1; i++) if (keep2[i]) keep[far + i] = true;

            var corners = new List<V2>();
            for (int i = 0; i < pts.Count; i++) if (keep[i]) corners.Add(pts[i]);
            for (int i = 0; i < corners.Count; i++)
            {
                V2 a = corners[i], b = corners[(i + 1) % corners.Count];
                if ((b - a).Length > 10.0) result.Add(new List<V2> { a, b });
            }
            result.Sort((x, y) => (y[1] - y[0]).Length.CompareTo((x[1] - x[0]).Length));
            if (result.Count > max) result.RemoveRange(max, result.Count - max);
            return result;
        }

        private static void Simplify(List<V2> pts, int i0, int i1, double tol, bool[] keep)
        {
            if (i1 <= i0 + 1) return;
            V2 a = pts[i0], b = pts[i1];
            double best = -1;
            int idx = -1;
            for (int i = i0 + 1; i < i1; i++)
            {
                double d = SegDistance(pts[i], a, b);
                if (d > best) { best = d; idx = i; }
            }
            if (best > tol)
            {
                keep[idx] = true;
                Simplify(pts, i0, idx, tol, keep);
                Simplify(pts, idx, i1, tol, keep);
            }
        }

        private static double SegDistance(V2 p, V2 a, V2 b)
        {
            V2 ab = b - a;
            double l2 = ab.E * ab.E + ab.N * ab.N;
            double t = l2 > 1e-12 ? Math.Max(0, Math.Min(1, ((p.E - a.E) * ab.E + (p.N - a.N) * ab.N) / l2)) : 0;
            return (p - (a + ab * t)).Length;
        }

        /// <summary>
        /// Working runs go on straight ahead (and start straight before) by up to dist, so the
        /// master switch stays on while each section switches at the headland line by itself.
        /// </summary>
        public static List<PlanPoint> ExtendWorkStraight(IList<PlanPoint> pts, double dist)
        {
            var r = new List<PlanPoint>(pts);
            int n = r.Count;
            if (n < 3 || dist <= 0) return r;
            double Step(int a, int b) => Math.Sqrt(Sq(pts[b].E - pts[a].E) + Sq(pts[b].N - pts[a].N));
            double Hd(int a, int b) => Math.Atan2(pts[b].E - pts[a].E, pts[b].N - pts[a].N);
            for (int i = 1; i < n; i++)
            {
                // end of a run: on along the straight
                if (pts[i - 1].IsWork && !pts[i].IsWork && i >= 2)
                {
                    double h = Hd(i - 2, i - 1), acc = 0;
                    for (int k = i; k < n && !pts[k].IsWork && !pts[k].IsReverse; k++)
                    {
                        acc += Step(k - 1, k);
                        if (acc > dist || Math.Abs(AngleDiff(Hd(k - 1, k), h)) > 0.035) break;
                        PlanPoint p = r[k]; p.IsWork = true; p.IsHeadland = false; r[k] = p;
                    }
                }
                // start of a run: on already along the straight before it
                if (!pts[i - 1].IsWork && pts[i].IsWork && i + 1 < n)
                {
                    double h = Hd(i, i + 1), acc = 0;
                    for (int k = i - 1; k >= 0 && !pts[k].IsWork && !pts[k].IsReverse; k--)
                    {
                        acc += Step(k, k + 1);
                        if (acc > dist || Math.Abs(AngleDiff(Hd(k, k + 1), h)) > 0.035) break;
                        PlanPoint p = r[k]; p.IsWork = true; p.IsHeadland = false; r[k] = p;
                    }
                }
            }
            return r;
        }

        public static List<PlanPoint> ShiftWorkFlags(IList<PlanPoint> pts, double toolBehind)
        {
            var r = new List<PlanPoint>(pts);
            if (pts.Count < 2 || Math.Abs(toolBehind) < 0.05) return r;

            var cum = new double[pts.Count];
            for (int i = 1; i < pts.Count; i++)
                cum[i] = cum[i - 1] + Math.Sqrt(Sq(pts[i].E - pts[i - 1].E) + Sq(pts[i].N - pts[i - 1].N));

            int j = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                // flags of the point where the pivot was (or will be) when the tool is here
                double target = cum[i] - toolBehind;
                if (target <= 0) j = 0;
                else if (target >= cum[pts.Count - 1]) j = pts.Count - 1;
                else
                {
                    if (toolBehind > 0) { while (j + 1 < pts.Count && cum[j + 1] <= target) j++; }
                    else { if (j < i) j = i; while (j + 1 < pts.Count && cum[j + 1] <= target) j++; }
                }
                PlanPoint p = r[i];
                p.IsWork = pts[j].IsWork;
                p.IsHeadland = pts[j].IsHeadland;
                r[i] = p;
            }
            return r;
        }

        /// <summary>
        /// Labels for the working runs of a path, in driving order: passes get 1, 2, 3...,
        /// closed loops (headland laps) get U1, U2... Each label sits at the middle of its run.
        /// </summary>
        public static List<RunLabel> GetRunLabels(IList<PlanPoint> pts)
        {
            var labels = new List<RunLabel>();
            int pass = 0, lap = 0;
            int i = 0;
            while (i < pts.Count)
            {
                if (!pts[i].IsWork) { i++; continue; }
                int start = i;
                while (i < pts.Count && pts[i].IsWork) i++;
                int end = i - 1;
                if (end - start < 2) continue;

                // Arc length of the run, then the point at half of it
                double len = 0;
                for (int k = start + 1; k <= end; k++) len += Math.Sqrt(Sq(pts[k].E - pts[k - 1].E) + Sq(pts[k].N - pts[k - 1].N));
                if (len < 1.0) continue;

                double half = len * 0.5, acc = 0;
                int m = start;
                for (int k = start + 1; k <= end; k++)
                {
                    acc += Math.Sqrt(Sq(pts[k].E - pts[k - 1].E) + Sq(pts[k].N - pts[k - 1].N));
                    if (acc >= half) { m = k; break; }
                }

                double gap = Math.Sqrt(Sq(pts[end].E - pts[start].E) + Sq(pts[end].N - pts[start].N));
                bool isLoop = (len > 50.0 && gap < len * 0.05) || pts[m].IsHeadland;
                labels.Add(new RunLabel
                {
                    E = pts[m].E,
                    N = pts[m].N,
                    Heading = pts[m].Heading,
                    IsHeadland = isLoop,
                    StartIndex = start,
                    EndIndex = end,
                    Text = isLoop ? "U" + (++lap) : (++pass).ToString(System.Globalization.CultureInfo.InvariantCulture)
                });
            }
            return labels;
        }

        // =====================================================================
        // Headland only at the pass ends
        // =====================================================================

        /// <summary>
        /// For every boundary point: -1 if it is part of a long side (roughly parallel to the passes),
        /// otherwise the number of the end (0, 1, ...) it belongs to.
        /// </summary>
        private static int[] ClassifyEnds(List<V2> fence, LaneLines lanes, double W, double diag, double s)
        {
            // The ends are where passes run into the boundary: lay out passes over the whole
            // field (no headland) and mark the boundary around every pass end.
            int n = fence.Count;
            var isEnd = new bool[n];
            int mark = Math.Max(1, (int)Math.Ceiling(W * 0.75 / s));
            double minLen = Math.Max(2.0 * W, 5.0);
            int emptyInRow = 0;
            bool anyPass = false;
            for (int lane = 0; lane < 10000; lane++)
            {
                double d = W * (lane + 0.5);
                if (d > diag) break;
                List<V2> off = lanes.Get(lane);
                if (off.Count < 2) break;
                bool found = false;
                foreach (var piece in InsideRuns(off, fence))
                {
                    if (OpenLength(piece) < minLen) continue;
                    found = true;
                    foreach (V2 endPt in new[] { piece[0], piece[piece.Count - 1] })
                    {
                        int f = NearestIndex(fence, endPt);
                        for (int q = -mark; q <= mark; q++) isEnd[((f + q) % n + n) % n] = true;
                    }
                }
                if (found) { anyPass = true; emptyInRow = 0; }
                else if (anyPass && ++emptyInRow > 3) break;
            }

            // Only real long sides stay without headland: side pieces shorter than 40 m or 30 % of
            // the longest side piece (gaps between pass ends on slanted edges, corners) become ends
            KeepLongSidesOnly(isEnd, s);

            var id = new int[n];
            for (int i = 0; i < n; i++) id[i] = -1;
            int startSide = Array.IndexOf(isEnd, false);
            if (startSide < 0) { for (int i = 0; i < n; i++) id[i] = 0; return id; }
            int run = -1;
            for (int c = 1; c <= n; c++)
            {
                int i = (startSide + c) % n;
                if (isEnd[i])
                {
                    if (!isEnd[(i - 1 + n) % n]) run++;
                    id[i] = run;
                }
            }
            return id;
        }

        private static void KeepLongSidesOnly(bool[] isEnd, double s)
        {
            int n = isEnd.Length;
            int start = Array.IndexOf(isEnd, true);
            if (start < 0) return; // no ends at all

            // side runs as (first index, length), walking once around from an end point
            var runs = new List<int[]>();
            int c = 1;
            while (c <= n)
            {
                int i = (start + c) % n;
                if (isEnd[i]) { c++; continue; }
                int len = 0;
                while (c + len <= n && !isEnd[(start + c + len) % n]) len++;
                runs.Add(new[] { i, len });
                c += len;
            }
            int longest = 0;
            foreach (var r in runs) longest = Math.Max(longest, r[1]);
            int minLen = (int)Math.Max(40.0 / s, 0.3 * longest);
            foreach (var r in runs)
                if (r[1] < minLen)
                    for (int q = 0; q < r[1]; q++) isEnd[(r[0] + q) % n] = true;
        }

        /// <summary>
        /// Ends moved inwards by their width, each offset line running straight on until it meets
        /// the side boundary; side points behind such a line (in the headland) are left out.
        /// </summary>
        private static List<V2> InsetEndsStraight(List<V2> loop, int[] endRunId, double[] width, double dist, double s)
        {
            int n = loop.Count;
            var grid = new SegmentGrid(loop, true, Math.Max(5.0, dist));
            var tester = new PolygonTester(loop);

            // offset end points (with their inward normal), raw side points
            var pts = new List<V2>(n);
            var nrm = new List<V2>(n);   // inward normal for offset points, zero for side points
            for (int i = 0; i < n; i++)
            {
                if (endRunId[i] < 0) { pts.Add(loop[i]); nrm.Add(new V2(0, 0)); continue; }
                V2 d = Dir(loop[(i - 1 + n) % n], loop[(i + 1) % n]);
                V2 inN = new V2(-d.N, d.E);
                double wi = width[i];
                V2 p = loop[i] + inN * wi;
                if (grid.MinDistance(p, wi) < Math.Min(dist, wi) * 0.98) continue;
                if (!tester.Inside(p)) continue;
                pts.Add(p);
                nrm.Add(inN);
            }
            int m = pts.Count;
            if (m < 3) return new List<V2>();
            bool IsOff(int k) => nrm[k].E != 0 || nrm[k].N != 0;

            // runs of offset points: their straight line at both ends
            var offLines = new List<(V2 a, V2 dir, V2 inN)>();
            var r = new List<V2>(m + 8);
            for (int k = 0; k < m; k++)
            {
                if (!IsOff(k)) continue;
                int prev = (k - 1 + m) % m, next = (k + 1) % m;
                if (!IsOff(prev))
                {
                    // start of a run: line backwards from its first points to the side
                    int k2 = Math.Min(k + 3, m - 1);
                    V2 dir = Dir(pts[k2], pts[k]);
                    offLines.Add((pts[k], dir, nrm[k]));
                }
                if (!IsOff(next))
                {
                    int k2 = Math.Max(k - 3, 0);
                    V2 dir = Dir(pts[k2], pts[k]);
                    offLines.Add((pts[k], dir, nrm[k]));
                }
            }
            bool Behind(V2 x)
            {
                foreach (var l in offLines)
                {
                    // in the headland: on the boundary side of an offset line and near it along the line
                    V2 rel = x - l.a;
                    double across = rel.E * l.inN.E + rel.N * l.inN.N;
                    double along = rel.E * l.dir.E + rel.N * l.dir.N;
                    if (across < -0.01 && along > -0.5 && across > -3.0 * dist) return true;
                }
                return false;
            }
            V2? HitFence(V2 a, V2 dir)
            {
                double best = double.MaxValue;
                V2? hit = null;
                for (int i = 0; i < n; i++)
                {
                    V2 p = loop[i], q = loop[(i + 1) % n];
                    V2 e = q - p;
                    double den = dir.E * e.N - dir.N * e.E;
                    if (Math.Abs(den) < 1e-9) continue;
                    V2 w = p - a;
                    double t = (w.E * e.N - w.N * e.E) / den;
                    double u = (w.E * dir.N - w.N * dir.E) / den;
                    if (t > 1e-6 && u >= 0 && u <= 1 && t < best) { best = t; hit = a + dir * t; }
                }
                return hit;
            }

            for (int k = 0; k < m; k++)
            {
                if (IsOff(k))
                {
                    int prev = (k - 1 + m) % m;
                    if (!IsOff(prev))
                    {
                        int k2 = Math.Min(k + 3, m - 1);
                        V2? h = HitFence(pts[k], Dir(pts[k2], pts[k]));
                        if (h.HasValue) r.Add(h.Value);
                    }
                    r.Add(pts[k]);
                    int next = (k + 1) % m;
                    if (!IsOff(next))
                    {
                        int k2 = Math.Max(k - 3, 0);
                        V2? h = HitFence(pts[k], Dir(pts[k2], pts[k]));
                        if (h.HasValue) r.Add(h.Value);
                    }
                }
                else if (!Behind(pts[k])) r.Add(pts[k]);
            }
            if (r.Count < 3) return new List<V2>();
            return ResampleLoop(r, s);
        }

        /// <summary>
        /// Key of the straight piece of an end every boundary point belongs to: a new piece
        /// starts at each corner of an end (direction change over about 20 degrees). Side points: -1.
        /// </summary>
        private static int[] EdgePieces(List<V2> loop, int[] endRunId)
        {
            int n = loop.Count;
            var key = new int[n];
            for (int i = 0; i < n; i++) key[i] = -1;
            int start = Array.IndexOf(endRunId, -1);
            if (start < 0) start = 0;
            const int w = 4;   // points each side for the direction
            int piece = -1;
            bool prevEnd = false;
            for (int c = 0; c < n; c++)
            {
                int i = (start + c) % n;
                if (endRunId[i] < 0) { prevEnd = false; continue; }
                V2 a = Dir(loop[(i - w + n) % n], loop[i]);
                V2 b = Dir(loop[i], loop[(i + w) % n]);
                bool corner = a.E * b.E + a.N * b.N < 0.94;
                // one corner gives several neighbouring corner points: new piece only at its first
                if (!prevEnd || (corner && (c == 0 || key[(i - 1 + n) % n] >= 0) && !CornerAt(loop, (i - 1 + n) % n, w)))
                    piece++;
                key[i] = piece;
                prevEnd = true;
            }
            return key;
        }

        private static bool CornerAt(List<V2> loop, int i, int w)
        {
            int n = loop.Count;
            V2 a = Dir(loop[(i - w + n) % n], loop[i]);
            V2 b = Dir(loop[i], loop[(i + w) % n]);
            return a.E * b.E + a.N * b.N < 0.94;
        }

        /// <summary>
        /// Work area with the headland only at the ends: end points are moved inwards by the
        /// headland width, long side points stay on the boundary.
        /// </summary>
        private static List<V2> InsetEnds(List<V2> loop, int[] endRunId, double dist, double s,
            V2 laneDir = default, double slantGap = 0.0, Dictionary<int, double> endDist = null, int[] distKey = null, bool straightToSides = false)
        {
            int n = loop.Count;
            // every end (or piece of an end) may have its own distance (the largest one sets the side band)
            double EndDist(int i) => endDist != null && distKey != null && endDist.TryGetValue(distKey[i], out double v) ? v : dist;
            if (endDist != null) foreach (double v in endDist.Values) dist = Math.Max(dist, v);

            // headland width per point: on an end that is slanted against the passes, neighbouring
            // pass ends are staggered; extra width (gap * cos of the angle) gives room to turn
            var width = new double[n];
            for (int i = 0; i < n; i++)
            {
                width[i] = endRunId[i] >= 0 ? EndDist(i) : dist;
                if (slantGap <= 0 || endRunId[i] < 0) continue;
                V2 t1 = Dir(loop[(i - 1 + n) % n], loop[i]);
                V2 t2 = Dir(loop[i], loop[(i + 1) % n]);
                // corners keep the normal width, only straight slanted parts get more
                if (t1.E * t2.E + t1.N * t2.N < 0.94) continue;
                double cos = Math.Abs(t1.E * laneDir.E + t1.N * laneDir.N);
                // end almost square to the passes, or an edge running along them (not an end)
                if (cos < 0.2 || cos > 0.985) continue;
                width[i] = dist + Math.Min(dist, slantGap * cos * 0.4);
            }

            if (straightToSides) return InsetEndsStraight(loop, endRunId, width, dist, s);

            // Side points inside the headland band of an end belong to the end
            var endIdx = new List<int>();
            for (int i = 0; i < n; i++) if (endRunId[i] >= 0) endIdx.Add(i);
            var isEnd = new bool[n];
            for (int i = 0; i < n; i++)
            {
                if (endRunId[i] >= 0) { isEnd[i] = true; continue; }
                foreach (int k in endIdx)
                {
                    double wk = width[k];
                    if (Sq(loop[k].E - loop[i].E) + Sq(loop[k].N - loop[i].N) < wk * wk) { isEnd[i] = true; width[i] = wk; break; }
                }
            }

            var grid = new SegmentGrid(loop, true, Math.Max(5.0, dist));
            var tester = new PolygonTester(loop);
            var r = new List<V2>(n);
            for (int i = 0; i < n; i++)
            {
                if (!isEnd[i]) { r.Add(loop[i]); continue; }
                V2 d = Dir(loop[(i - 1 + n) % n], loop[(i + 1) % n]);
                double wi = width[i];
                V2 p = loop[i] + new V2(-d.N, d.E) * wi;
                if (grid.MinDistance(p, wi) < Math.Min(dist, wi) * 0.98) continue;
                if (!tester.Inside(p)) continue;
                r.Add(p);
            }
            if (r.Count < 3) return new List<V2>();
            return ResampleLoop(r, s);
        }

        /// <summary>
        /// Headland strips along the ends of the field, inner strip first, each end in turn.
        /// The direction of every strip is chosen so that the connecting turn is short and inside.
        /// </summary>
        private static void BuildEndStrips(List<V2> fence, int[] endRunId, int laps, double W, double R, double s,
            PolygonTester fenceGrid, List<PlanPoint> path, FieldPlanResult res, List<V2> transferLoop, bool reverseTurns)
        {
            var groups = new Dictionary<int, List<List<V2>>>(); // end id -> strips, inner first
            for (int j = laps - 1; j >= 0; j--)
            {
                List<V2> lap = InsetLoop(fence, W * (j + 0.5), s);
                if (lap.Count < 10) continue;

                // end id of each lap point from its nearest boundary point
                int m = lap.Count;
                var ids = new int[m];
                for (int i = 0; i < m; i++) ids[i] = endRunId[NearestIndex(fence, lap[i])];

                int start = Array.IndexOf(ids, -1);
                if (start < 0) continue;
                List<V2> cur = null;
                int curId = -1;
                for (int c = 1; c <= m; c++)
                {
                    int i = (start + c) % m;
                    if (ids[i] != curId)
                    {
                        if (cur != null) AddStrip(groups, curId, cur, W, R, s, fenceGrid);
                        cur = null;
                        curId = ids[i];
                        if (curId >= 0) cur = new List<V2>();
                    }
                    if (cur != null) cur.Add(lap[i]);
                }
                if (cur != null) AddStrip(groups, curId, cur, W, R, s, fenceGrid);
            }

            var remaining = new List<int>(groups.Keys);
            while (remaining.Count > 0)
            {
                LastPose(path, s, out V2 p0, out double h0);

                // nearest end first
                int bestG = remaining[0];
                double bestD = double.MaxValue;
                foreach (int g in remaining)
                    foreach (var st in groups[g])
                    {
                        double d = Math.Min((st[0] - p0).Length, (st[st.Count - 1] - p0).Length);
                        if (d < bestD) { bestD = d; bestG = g; }
                    }
                remaining.Remove(bestG);

                foreach (var strip in groups[bestG])
                {
                    LastPose(path, s, out V2 cp, out double ch);
                    List<V2> best = null, bestConn = null;
                    double bestCost = double.MaxValue;
                    for (int r = 0; r < 2; r++)
                    {
                        var pts = new List<V2>(strip);
                        if (r == 1) pts.Reverse();
                        var conn = Connect(cp, ch, pts[0], HeadingOf(pts[0], pts[Math.Min(3, pts.Count - 1)]), R, W, s, fenceGrid, transferLoop);
                        if (conn == null) continue;
                        double cost = OpenLength(conn) + 1000.0 * CountOutside(conn, fenceGrid);
                        if (cost < bestCost) { bestCost = cost; best = pts; bestConn = conn; }
                    }
                    if (best == null) continue;

                    // forward / reverse manoeuvre instead of a half turn or loop, where one fits
                    if (reverseTurns && TryReverseManeuver(path, cp, ch, strip, R, s, fenceGrid, out List<V2> stripDir))
                    {
                        AppendPoints(path, stripDir, true, true, true);
                        res.HeadlandLaps++;
                        continue;
                    }

                    if (CountOutside(bestConn, fenceGrid) > 0) res.TurnsLeavingField++;
                    AppendPoints(path, bestConn, false, true);
                    AppendPoints(path, best, true, true, true);
                    res.HeadlandLaps++;
                }
            }
        }

        /// <summary>
        /// Joins the next headland strip (either direction) with the shortest forward/reverse
        /// manoeuvre and appends it; stripDir is the strip in the chosen direction.
        /// </summary>
        /// <summary>
        /// One pass half a work width inside every gently slanted end (angle to the passes
        /// under about 50 degrees), parallel to the boundary. It gets the lane of the pass whose
        /// end is nearest, so the order takes it on the way.
        /// </summary>
        private static List<Pass> SlantEdgePasses(List<V2> fence, int[] endRunId, V2 laneDir, double W, double R, double s, List<Pass> passes)
        {
            var result = new List<Pass>();
            int n = fence.Count;

            // how slanted is every end run
            var sum = new Dictionary<int, double>();
            var cnt = new Dictionary<int, int>();
            for (int i = 0; i < n; i++)
            {
                int id = endRunId[i];
                if (id < 0) continue;
                V2 t = Dir(fence[(i - 1 + n) % n], fence[(i + 1) % n]);
                double c = Math.Abs(t.E * laneDir.E + t.N * laneDir.N);
                sum[id] = (sum.TryGetValue(id, out double v) ? v : 0) + c;
                cnt[id] = (cnt.TryGetValue(id, out int k) ? k : 0) + 1;
            }

            List<V2> lap = InsetLoop(fence, W * 0.5, s);
            if (lap.Count < 10) return result;
            int m = lap.Count;
            var ids = new int[m];
            for (int i = 0; i < m; i++)
            {
                int f = NearestIndex(fence, lap[i]);
                // only where the boundary itself runs at a gentle angle to the passes
                V2 t = Dir(fence[(f - 3 + n) % n], fence[(f + 3) % n]);
                double c = Math.Abs(t.E * laneDir.E + t.N * laneDir.N);
                ids[i] = c >= 0.85 ? endRunId[f] : -1;
            }

            foreach (int id in sum.Keys)
            {
                double meanCos = sum[id] / cnt[id];
                if (meanCos < 0.6) continue;   // steep end: normal turns

                // longest contiguous piece of the lap along this end
                List<V2> best = null, cur = null;
                int start = Array.IndexOf(ids, -1);
                if (start < 0) start = 0;
                for (int c = 1; c <= m; c++)
                {
                    int i = (start + c) % m;
                    if (ids[i] == id) { if (cur == null) cur = new List<V2>(); cur.Add(lap[i]); }
                    else if (cur != null) { if (best == null || cur.Count > best.Count) best = cur; cur = null; }
                }
                if (cur != null && (best == null || cur.Count > best.Count)) best = cur;
                if (best == null || OpenLength(best) < 4.0 * W) continue;

                // round the line to the turning radius so it can be driven
                List<V2> line = RoundOpen(ResampleOpen(best, s), R, s);
                if (line.Count < 4) continue;

                // lane of the pass with the nearest end, so the order picks it up on the way
                int lane = 0;
                double bd = double.MaxValue;
                foreach (Pass ps in passes)
                {
                    double d = Math.Min(Math.Min((ps.A - line[0]).Length, (ps.B - line[0]).Length),
                        Math.Min((ps.A - line[line.Count - 1]).Length, (ps.B - line[line.Count - 1]).Length));
                    if (d < bd) { bd = d; lane = ps.Lane; }
                }
                result.Add(new Pass { Lane = lane, Pts = line });
            }
            return result;
        }

        private static bool TryReverseManeuver(List<PlanPoint> path, V2 cp, double ch, List<V2> strip, double R, double s,
            PolygonTester fence, out List<V2> stripDir)
        {
            stripDir = null;
            List<ReverseManeuver.Pose> best = null;
            double bestLen = double.MaxValue;
            for (int r = 0; r < 2; r++)
            {
                var pts = new List<V2>(strip);
                if (r == 1) pts.Reverse();
                if ((pts[0] - cp).Length > 60.0) continue;   // far away: a normal transfer
                var man = ReverseManeuver.Find(cp, ch, pts[0], HeadingOf(pts[0], pts[Math.Min(3, pts.Count - 1)]), R, s, fence.Clear);
                if (man == null) continue;
                double len = 0;
                V2 prev = cp;
                foreach (var q in man) { len += (q.P - prev).Length * (q.Reverse ? 1.3 : 1.0); prev = q.P; }
                if (len < bestLen) { bestLen = len; best = man; stripDir = pts; }
            }
            if (best == null) return false;
            foreach (var q in best)
                path.Add(new PlanPoint { E = q.P.E, N = q.P.N, IsWork = false, IsReverse = q.Reverse });
            return true;
        }

        private static void AddStrip(Dictionary<int, List<List<V2>>> groups, int id, List<V2> pts, double W, double R, double s, PolygonTester fence)
        {
            if (pts.Count < 3) return;

            // Stop short of the long sides so the turn to the next strip fits inside the field
            int trim = (int)Math.Ceiling((R + W * 0.5 + 3.0) / s);
            if (pts.Count - 2 * trim < 3 || (pts.Count - 2 * trim) * s < 2.0 * W) return;
            pts = pts.GetRange(trim, pts.Count - 2 * trim);

            List<V2> sharp = pts;
            List<V2> round = RoundOpen(pts, R, s);
            for (int k = 0; k < round.Count; k++)
                if (!fence.Inside(round[k])) round[k] = sharp[NearestIndex(sharp, round[k])];
            if (!groups.TryGetValue(id, out var list)) groups[id] = list = new List<List<V2>>();
            list.Add(round);
        }

        /// <summary>Smooths an open line until no bend is tighter than the turning radius.</summary>
        private static List<V2> RoundOpen(List<V2> line, double R, double s)
        {
            int win = 2 * Math.Max(2, (int)Math.Round(R / (2.0 * s))) + 1;
            int span = Math.Max(2, (int)Math.Round(2.0 / s));
            var cur = line;
            for (int pass = 0; pass < 40; pass++)
            {
                double maxK = 0;
                for (int i = span; i < cur.Count - span; i++)
                {
                    V2 a = cur[i - span], b = cur[i], c = cur[i + span];
                    double d = (c - a).Length;
                    if (d < 1e-6) continue;
                    double k = Math.Abs(AngleDiff(HeadingOf(b, c), HeadingOf(a, b))) / (d * 0.5);
                    if (k > maxK) maxK = k;
                }
                if (maxK <= 1.0 / R) break;
                cur = ResampleOpen(SmoothOpen(cur, win, 1), s);
            }
            return cur;
        }

        // =====================================================================
        // Pass ordering
        // =====================================================================

        private class Pass
        {
            public int Lane;
            public List<V2> Pts;
            public V2 A => Pts[0];
            public V2 B => Pts[Pts.Count - 1];
            public double HeadingA => HeadingOf(Pts[0], Pts[Math.Min(3, Pts.Count - 1)]);
            public double HeadingB => HeadingOf(Pts[Pts.Count - 1], Pts[Math.Max(0, Pts.Count - 4)]);
        }

        private class Step
        {
            public Pass Pass;
            public bool Reverse;
        }

        // Exit pose of a pass driven in the given direction
        private static void ExitPose(Step st, out V2 p, out double h)
        {
            if (!st.Reverse) { p = st.Pass.B; h = Mod2Pi(st.Pass.HeadingB + Math.PI); }
            else { p = st.Pass.A; h = Mod2Pi(st.Pass.HeadingA + Math.PI); }
        }

        // Entry pose of a pass driven in the given direction
        private static void EntryPose(Pass ps, bool reverse, out V2 p, out double h)
        {
            if (!reverse) { p = ps.A; h = ps.HeadingA; }
            else { p = ps.B; h = ps.HeadingB; }
        }

        /// <summary>
        /// Greedy ordering with different weights for "keep working from one side".
        /// Weight 0 gives the shortest next turn (skip rows, U turns), a huge weight gives
        /// strictly neighbouring passes (omega turns). The plan with the lowest total turn
        /// length, plus a big penalty for turns leaving the boundary, wins.
        /// </summary>
        private static List<Step> ChooseOrder(List<Pass> passes, double R, double W, PolygonTester fence,
            V2 startPos, double startHeading, double s, List<V2> transferLoop, int orderMode, out string name, bool reverseTurns = false)
        {
            // weight of the lane penalty: 1e6 = strictly neighbour passes, small = skipping allowed
            double[] weights = orderMode == 1 ? new[] { 1e6 }
                : orderMode == 2 ? new[] { 0.25, 0.5, 1.0, 2.0 }
                : new[] { 0.0, 0.25, 0.5, 1.0, 2.0, 1e6 };
            List<Step> bestPlan = null;
            double bestScore = double.MaxValue;
            name = "";

            // Start at the first or last lane, at the end closest to the vehicle
            var starts = new List<Step>();
            foreach (Pass ps in new[] { passes[0], passes[passes.Count - 1] })
            {
                starts.Add(new Step { Pass = ps, Reverse = false });
                starts.Add(new Step { Pass = ps, Reverse = true });
            }
            Step start = null;
            double bestStart = double.MaxValue;
            foreach (Step st in starts)
            {
                EntryPose(st.Pass, st.Reverse, out V2 p, out double h);
                double len = DubinsLength(startPos, startHeading, p, h, R);
                if (len < bestStart) { bestStart = len; start = st; }
            }

            bool lowFirst = start.Pass == passes[0];

            foreach (double w in weights)
            {
                var plan = new List<Step> { start };
                var used = new HashSet<Pass> { start.Pass };
                double score = 0;

                while (plan.Count < passes.Count)
                {
                    ExitPose(plan[plan.Count - 1], out V2 ep, out double eh);

                    // Lane of the first unworked pass seen from the starting side
                    int edgeLane = lowFirst ? int.MaxValue : int.MinValue;
                    foreach (Pass ps in passes)
                    {
                        if (used.Contains(ps)) continue;
                        edgeLane = lowFirst ? Math.Min(edgeLane, ps.Lane) : Math.Max(edgeLane, ps.Lane);
                    }

                    // Candidates sorted by turn length plus lane penalty
                    var cands = new List<KeyValuePair<double, Step>>();
                    foreach (Pass ps in passes)
                    {
                        if (used.Contains(ps)) continue;
                        double lanePenalty = w * W * Math.Abs(ps.Lane - edgeLane);
                        // skipping 0 to 5 passes; more only when nothing else is left
                        if (Math.Abs(ps.Lane - plan[plan.Count - 1].Pass.Lane) > 6) lanePenalty += 500.0;
                        for (int r = 0; r < 2; r++)
                        {
                            EntryPose(ps, r == 1, out V2 p, out double h);
                            double len = TurnLength(ep, eh, p, h, R, orderMode != 1);
                            cands.Add(new KeyValuePair<double, Step>(len + lanePenalty, new Step { Pass = ps, Reverse = r == 1 }));
                        }
                    }
                    cands.Sort((x, y) => x.Key.CompareTo(y.Key));

                    // Take the cheapest turn that stays inside the boundary (check the best few)
                    Step pick = null;
                    double pickCost = double.MaxValue;
                    for (int c = 0; c < Math.Min(40, cands.Count); c++)
                    {
                        Step cand = cands[c].Value;
                        EntryPose(cand.Pass, cand.Reverse, out V2 p, out double h);
                        int outside;
                        V2 dd = p - ep;
                        double lat = Math.Abs(dd.E * Math.Cos(eh) - dd.N * Math.Sin(eh));
                        if (reverseTurns && Math.Abs(AngleDiff(h, eh + Math.PI)) < 0.3 && lat < 2.0 * R && dd.Length < 4.0 * R)
                            outside = 0;   // neighbour pass: a three point turn, it fits in the headland
                        else
                        {
                            var turn = ClearDubins(ep, eh, p, h, R, Math.Max(2.0, s), fence);
                            outside = turn == null ? 1000 : CountOutside(turn, fence);
                        }
                        double cost = cands[c].Key + 1000.0 * outside;
                        if (cost < pickCost) { pickCost = cost; pick = cand; }
                        if (outside == 0) break;
                    }
                    if (pick == null) break;
                    plan.Add(pick);
                    used.Add(pick.Pass);
                }

                score = ScoreOrder(plan);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestPlan = plan;
                    name = w >= 1e5 ? "neighbour passes" : (w == 0 ? "shortest turns" : "skip passes, weight " + w.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }

            // Skip optimizer: fixed patterns skipping 0 to 5 passes, from either end and in
            // either direction; the best of everything wins
            if (orderMode != 1 && passes.Count >= 3)
            {
                var sorted = new List<Pass>(passes);
                sorted.Sort((x, y) => x.Lane.CompareTo(y.Lane));
                for (int skip = 0; skip <= 5; skip++)
                {
                    List<int> idx = SkipPattern(sorted.Count, skip + 1);
                    for (int flip = 0; flip < 2; flip++)
                    {
                        for (int firstRev = 0; firstRev < 2; firstRev++)
                        {
                            var plan = new List<Step>(idx.Count);
                            for (int k = 0; k < idx.Count; k++)
                            {
                                int i = flip == 0 ? idx[k] : sorted.Count - 1 - idx[k];
                                plan.Add(new Step { Pass = sorted[i], Reverse = (k % 2 == 1) ^ (firstRev == 1) });
                            }
                            // passes of one lane may not all be parallel: fix each direction to
                            // continue from the previous end
                            for (int k = 1; k < plan.Count; k++)
                            {
                                ExitPose(plan[k - 1], out V2 ep, out double _);
                                EntryPose(plan[k].Pass, false, out V2 pa, out double _);
                                EntryPose(plan[k].Pass, true, out V2 pb, out double _);
                                plan[k].Reverse = (pb - ep).Length < (pa - ep).Length;
                            }
                            EntryPose(plan[0].Pass, plan[0].Reverse, out V2 sp, out double sh);
                            double sc = ScoreOrder(plan) + DubinsLength(startPos, startHeading, sp, sh, R);
                            if (sc < bestScore)
                            {
                                bestScore = sc;
                                bestPlan = plan;
                                name = "skip passes, " + skip;
                            }
                        }
                    }
                }
            }
            return bestPlan;

            double ScoreOrder(List<Step> plan)
            {
                double score = 0;
                // real turn lengths plus penalty for leaving the field
                for (int i = 1; i < plan.Count; i++)
                {
                    ExitPose(plan[i - 1], out V2 ep, out double eh);
                    EntryPose(plan[i].Pass, plan[i].Reverse, out V2 p, out double h);
                    var turn = ClearDubins(ep, eh, p, h, R, Math.Max(2.0, s), fence);
                    int outside = turn == null ? 1000 : CountOutside(turn, fence);
                    if (outside > 0 && transferLoop != null)
                    {
                        // such a transfer will drive along the boundary line instead
                        var along = LoopTransfer(ep, eh, p, h, transferLoop, R, Math.Max(2.0, s), fence, out int alongOut, out double alongCost);
                        if (along != null && alongOut < outside)
                        {
                            score += alongCost;
                            continue;
                        }
                    }
                    if (turn == null) { score += 1e6; continue; }
                    score += TurnLength(ep, eh, p, h, R, orderMode != 1) + 1000.0 * outside;
                }
                return score;
            }
        }

        /// <summary>
        /// Order of n lanes going out with a step and filling the skipped ones on the way back
        /// (step 1 = neighbours): blocks of 2*step lanes, 0, step, 1, step+1, ... so passes in a
        /// row are step or step-1 lanes apart.
        /// </summary>
        private static List<int> SkipPattern(int n, int step)
        {
            var r = new List<int>(n);
            if (step <= 1)
            {
                for (int i = 0; i < n; i++) r.Add(i);
                return r;
            }
            for (int b = 0; b < n; b += 2 * step)
            {
                for (int i = 0; i < step; i++)
                {
                    if (b + i < n) r.Add(b + i);
                    if (b + i + step < n) r.Add(b + i + step);
                }
            }
            return r;
        }

        // =====================================================================
        // Dubins paths (minimum radius R, forward only)
        // =====================================================================

        // Math frame: x = easting, y = northing, theta = math angle (CCW from east).
        private static double ToTheta(double heading) => Mod2Pi(Math.PI / 2 - heading);
        private static double ToHeading(double theta) => Mod2Pi(Math.PI / 2 - theta);

        private struct DubinsSolution
        {
            public double T, P, Q;   // normalized segment lengths
            public int Type;         // index into Types
            public double Length => T + P + Q;
        }

        // Segment directions: +1 left, -1 right, 0 straight
        private static readonly int[][] Types =
        {
            new[] { 1, 0, 1 },   // LSL
            new[] { -1, 0, -1 }, // RSR
            new[] { 1, 0, -1 },  // LSR
            new[] { -1, 0, 1 },  // RSL
            new[] { -1, 1, -1 }, // RLR
            new[] { 1, -1, 1 },  // LRL
        };

        /// <summary>All valid Dubins solutions, shortest first.</summary>
        private static List<DubinsSolution> AllDubins(V2 p0, double h0, V2 p1, double h1, double R)
        {
            var list = new List<DubinsSolution>();
            double th0 = ToTheta(h0), th1 = ToTheta(h1);
            double dx = p1.E - p0.E, dy = p1.N - p0.N;
            double d = Math.Sqrt(dx * dx + dy * dy) / R;
            double theta = d > 1e-9 ? Mod2Pi(Math.Atan2(dy, dx)) : 0;
            double a = Mod2Pi(th0 - theta), b = Mod2Pi(th1 - theta);
            double sa = Math.Sin(a), sb = Math.Sin(b), ca = Math.Cos(a), cb = Math.Cos(b), cab = Math.Cos(a - b);
            for (int type = 0; type < 6; type++)
            {
                if (!DubinsWord(type, d, a, b, sa, sb, ca, cb, cab, out double t, out double p, out double q)) continue;
                Simulate(p0, th0, R, type, t, p, q, out V2 endP, out double endTh);
                if ((endP - p1).Length > 0.05 * Math.Max(1.0, R) || Math.Abs(AngleDiff(endTh, th1)) > 0.02) continue;
                list.Add(new DubinsSolution { T = t, P = p, Q = q, Type = type });
            }
            list.Sort((x, y) => x.Length.CompareTo(y.Length));
            return list;
        }

        /// <summary>
        /// Closed drive line half a work width inside the boundary (outer headland lap, and the
        /// first pass along the long sides), rounded to the turning radius.
        /// </summary>
        private static List<V2> BuildTransferLoop(List<V2> fence, double W, double R, double s, PolygonTester fenceGrid)
        {
            List<V2> lap = InsetLoop(fence, W * 0.5, s);
            if (lap.Count < 10) return null;
            List<V2> sharp = lap;
            lap = RoundLoop(lap, R, s);
            for (int k = 0; k < lap.Count; k++)
                if (!fenceGrid.Clear(lap[k])) lap[k] = sharp[NearestIndex(sharp, lap[k])];
            return lap;
        }

        /// <summary>
        /// Connection from the end of one run to the start of the next. A normal turn is a Dubins
        /// path; a long transfer (or one that would leave the field) drives along the boundary
        /// line instead of cutting straight across the field.
        /// </summary>
        private static List<V2> Connect(V2 p0, double h0, V2 p1, double h1, double R, double W, double s,
            PolygonTester fence, List<V2> loop, bool wideTurn = false)
        {
            List<V2> direct = ClearDubins(p0, h0, p1, h1, R, s, fence);

            int directOut = direct == null ? int.MaxValue : CountOutside(direct, fence);
            double directLen = direct == null ? double.MaxValue : OpenLength(direct);
            // turn to a nearby pass (also skipping a few): out at the boundary, ends lined up.
            // Nearby means the sideways gap between the passes; along a slanted edge the pass
            // ends can be far apart lengthwise and it is still a turn, not a transfer
            V2 dir0 = new V2(Math.Sin(h0), Math.Cos(h0));
            V2 rel = p1 - p0;
            double lateral = Math.Abs(rel.E * dir0.N - rel.N * dir0.E);
            double lengthwise = Math.Abs(rel.E * dir0.E + rel.N * dir0.N);
            bool near = lateral <= 2.0 * R + 5.0 * W && lengthwise <= 150.0;
            // pass ends on a slanted edge: turn along the headland, up the slant, into the next pass
            // every turn between passes: along the boundary line (the way the driver wants it)
            // a straight end between the passes (no corner): straight to R from the edge,
            // along it, R into the pass - it starts turning at the least room from the boundary
            if (wideTurn && near)
            {
                List<V2> plain = BoundaryTurn(p0, h0, p1, h1, R, s, fence);
                if (plain != null && AlongBoundaryLine(plain, h0, h1, fence))
                {
                    // both passes end on the same straight piece of the boundary
                    V2? ea = AheadEdgeDirection(fence, p0, h0), eb = AheadEdgeDirection(fence, p1, h1 + Math.PI);
                    if (ea.HasValue && eb.HasValue && Math.Abs(ea.Value.E * eb.Value.E + ea.Value.N * eb.Value.N) > 0.985) return plain;
                }
            }
            if (wideTurn && loop != null)
            {
                V2 d0 = new V2(Math.Sin(h0), Math.Cos(h0));
                double reach = fence.Clearance + 0.3 + 1.5;
                V2 q = p0;
                for (double e = 0.5; e <= 200.0; e += 0.5)
                {
                    V2 c = p0 + d0 * e;
                    if (!fence.Clear(c) || AheadEdgeDistance(fence, c, h0, reach + 1.0) < reach) break;
                    q = c;
                }
                // the next pass starts before the boundary is reached: plain turn into it there,
                // not up to the boundary and back over the own track
                double reachLen = (q - p0).Length;
                double targetLen = (p1 - p0).E * d0.E + (p1 - p0).N * d0.N;
                // (a pass further aside is still reached along the boundary: no own track there)
                if (targetLen < reachLen - 2.0 * R && lateral < 2.0 * R + 1.0) goto NoHug;

                // the next pass starts as close to its edge as the turn into it allows
                V2 d1 = new V2(Math.Sin(h1), Math.Cos(h1));
                // (not closer to the edge than the turn from the boundary line needs)
                double keep = fence.Clearance + 0.3 + R;
                double maxBack = 0;
                for (double e = 0.5; e <= 60.0; e += 0.5)
                {
                    V2 c = p1 - d1 * e;
                    if (!fence.Clear(c) || fence.EdgeDistance(c, keep + 1.0) < keep) break;
                    maxBack = e;
                }
                // where the boundary runs across the pass the turn onto the line starts earlier
                // corners on the line's arc; only where the pass ends too close to a corner for
                // that, the turn may run a little inside the arc
                V2 q0 = q;
                List<V2> TryHug(double back, double e, double tol)
                {
                    V2 qq = q0 - d0 * back;
                    V2 entry = p1 - d1 * e;
                    List<V2> hug = LoopTransfer(qq, h0, entry, h1, loop, R, s, fence, out int hugOut, out double hugCost, true, tol);
                    if (hug == null || hugOut != 0 || hugCost >= 400.0) return null;
                    var pts = new List<V2>();
                    AddStraight(pts, p0, qq, s);
                    for (int k = 1; k < hug.Count; k++) pts.Add(hug[k]);
                    AddStraight(pts, entry, p1, s);
                    return pts;
                }
                // coarse search first, then the turn is fine tuned to start and end as close
                // to the boundary as it fits (the least room from the boundary)
                double backStep = 0.5 * R, eStep = Math.Max(2.0, maxBack / 3.0);
                foreach (double tol in new[] { 1.0, 0.8 * R })
                {
                    for (double back = 0; back <= reachLen && back <= 3.0 * R; back += backStep)
                    {
                        for (double e = maxBack; e >= 0; e -= eStep)
                        {
                            List<V2> found = TryHug(back, e, tol);
                            if (found == null) continue;
                            for (double bf = Math.Max(0, back - backStep + 1.0); bf < back; bf += 1.0)
                            {
                                for (double ef = Math.Min(maxBack, e + eStep - 0.5); ef >= e - 1e-9; ef -= 0.5)
                                {
                                    List<V2> fine = TryHug(bf, ef, tol);
                                    if (fine != null) return fine;
                                }
                            }
                            for (double ef = Math.Min(maxBack, e + eStep - 0.5); ef > e; ef -= 0.5)
                            {
                                List<V2> fine = TryHug(back, ef, tol);
                                if (fine != null) return fine;
                            }
                            return found;
                        }
                    }
                }
            }
        NoHug:

            if (wideTurn && near)
            {
                List<V2> wide = WideTurn(p0, h0, p1, h1, R, s, fence);
                if (wide != null) return wide;
            }

            // a loop (figure-8) only if nothing else fits: try the longer way along the headland
            if (near && direct != null && directOut == 0 && loop != null && TotalRotation(direct) > 1.3 * Math.PI)
            {
                List<V2> round = LoopTransfer(p0, h0, p1, h1, loop, R, s, fence, out int roundOut);
                if (round != null && roundOut == 0) return round;
            }

            // anything longer is a transfer
            bool isTransfer = directOut > 0 || (!near && directLen > Math.PI * R + 2.0 * R + 4.0 * W);
            if (!isTransfer || loop == null) return direct;

            List<V2> along = LoopTransfer(p0, h0, p1, h1, loop, R, s, fence, out int alongOut);
            if (along == null) return direct;
            // along the boundary (headland) even when it is a longer way round
            if (alongOut <= directOut) return along;
            return direct;
        }

        /// <summary>
        /// Length of a turn as it will be driven: back into a parallel pass the two ends are
        /// lined up first (see WideTurn), so a staggered end costs its straight part, not a loop.
        /// </summary>
        private static double TurnLength(V2 p0, double h0, V2 p1, double h1, double R, bool skipClose = true)
        {
            V2 d0 = new V2(Math.Sin(h0), Math.Cos(h0));
            V2 d1 = new V2(Math.Sin(h1), Math.Cos(h1));
            // loops (figure-8 turns) only when nothing else is possible: a very high cost
            const double loopPenalty = 500.0;
            double dot = d0.E * d1.E + d0.N * d1.N;
            bool far = (p1 - p0).Length > 6.0 * R + 40.0;
            if (dot > -0.7 || far)
                return DubinsLength(p0, h0, p1, h1, R) + (dot > 0.0 && !far ? loopPenalty : 0.0);

            double l0 = p0.E * d0.E + p0.N * d0.N;
            double l1 = p1.E * d0.E + p1.N * d0.N;
            double level = Math.Max(l0, l1);
            V2 a = p0 + d0 * (level - l0);
            V2 b = p1 - d1 * (level - l1);
            // sideways gap smaller than two radii: only a loop (omega) turn fits
            double gap = Math.Abs((p1 - p0).E * d0.N - (p1 - p0).N * d0.E);
            // a gap under two radii is turned without a loop at the boundary (two part turns), small extra cost
            // turns go out along the boundary (part turn, along it, part turn into the pass): that
            // needs two turning radii sideways, so closer passes are skipped (smart skipping)
            return (level - l0) + (level - l1) + DubinsLength(a, h0, b, h1, R) + (skipClose && gap < 1.9 * R ? 300.0 : 0.0);
        }

        /// <summary>
        /// Turn between passes made out at the boundary. Both pass ends may go straight on
        /// first (each by its own length); of all turns that keep the tool inside and do not
        /// loop (about half a circle in total) the one that follows the boundary most closely
        /// wins. On a slanted end of the field this gives: part turn, straight parallel to the
        /// boundary, part turn.
        /// Null when no such turn fits.
        /// </summary>
        private static List<V2> WideTurn(V2 p0, double h0, V2 p1, double h1, double R, double s, PolygonTester fence)
        {
            V2 d0 = new V2(Math.Sin(h0), Math.Cos(h0));   // out of the field
            V2 d1 = new V2(Math.Sin(h1), Math.Cos(h1));   // into the field
            // only for turning back into a (roughly) parallel pass
            if (d0.E * d1.E + d0.N * d1.N > -0.7) return null;

            // the turn as driven: straight on until the turn circle is R (plus half the tool)
            // from the boundary, part turn, straight parallel to the boundary, part turn
            List<V2> exact = BoundaryTurn(p0, h0, p1, h1, R, s, fence);
            if (exact != null) return exact;

            double step = Math.Max(2.0, s);
            const double maxExt = 40.0;
            double maxRotation = Math.PI * 1.2;

            List<V2> best = null;
            double bestHug = double.MaxValue, bestLen = double.MaxValue;
            for (double e0 = 0; e0 <= maxExt; e0 += step)
            {
                V2 a = p0 + d0 * e0;     // turn starts here
                if (!fence.Inside(a)) break;
                for (double e1 = 0; e1 <= maxExt; e1 += step)
                {
                    V2 b = p1 - d1 * e1; // and ends here, on the next pass line
                    if (!fence.Inside(b)) break;

                    // every Dubins word, not only the shortest: the shortest may loop where a
                    // part turn - straight - part turn fits
                    foreach (DubinsSolution sol in AllDubins(a, h0, b, h1, R))
                    {
                        List<V2> arc = PathFromSolution(a, h0, b, sol, R, s);
                        if (arc == null || arc.Count < 2) continue;
                        if (TotalRotation(arc) > maxRotation) continue;
                        var pts = new List<V2>();
                        AddStraight(pts, p0, a, s);
                        for (int k = 1; k < arc.Count; k++) pts.Add(arc[k]);
                        AddStraight(pts, b, p1, s);
                        if (CountOutside(pts, fence) > 0) continue;

                        // how close the turn itself runs along the boundary (mean distance)
                        double hug = 0;
                        foreach (V2 q in arc) hug += fence.EdgeDistance(q, 40.0);
                        hug /= arc.Count;
                        double len = OpenLength(pts);
                        // closest to the boundary first (within 0.3 m), then the shorter one
                        if (hug < bestHug - 0.3 || (hug < bestHug + 0.3 && len < bestLen))
                        {
                            bestHug = Math.Min(hug, bestHug);
                            bestLen = len;
                            best = pts;
                        }
                    }
                }
            }
            return best;
        }

        /// <summary>
        /// Passes that end on a gently slanted boundary are done in turn with the neighbouring
        /// passes that end on the normal edge on the same side: up the slanted pass, along the
        /// headland up the slant to the top, down a top pass, turn at the bottom, up the next
        /// slanted pass, and so on. The block goes where it joins the rest of the plan best.
        /// </summary>
        private static List<Step> AlternateSlant(List<Step> order, List<Pass> passes, PolygonTester fence, double R)
        {
            var upEnd = new Dictionary<Pass, bool>();   // true: B is the end on the slant
            var slanted = new List<Pass>();
            foreach (Pass ps in passes)
            {
                if (ps.Pts.Count < 4) continue;
                int n = ps.Pts.Count;
                bool sb = IsSlantedEnd(ps.B, HeadingOf(ps.Pts[n - 3], ps.B), fence);
                bool sa = IsSlantedEnd(ps.A, HeadingOf(ps.Pts[2], ps.A), fence);
                if (sa == sb) continue;
                upEnd[ps] = sb;
                slanted.Add(ps);
            }
            if (slanted.Count < 2) return order;

            var byLane = new Dictionary<int, Pass>();
            foreach (Pass ps in passes) byLane[ps.Lane] = ps;
            int minLane = int.MaxValue, maxLane = int.MinValue;
            foreach (Pass ps in slanted) { minLane = Math.Min(minLane, ps.Lane); maxLane = Math.Max(maxLane, ps.Lane); }

            // neighbouring passes ending on the normal edge, on the side next to the slanted ones
            List<Pass> TopSide(int startLane, int step)
            {
                var list = new List<Pass>();
                for (int l = startLane; list.Count < slanted.Count && byLane.TryGetValue(l, out Pass ps); l += step)
                {
                    if (upEnd.ContainsKey(ps)) break;
                    list.Add(ps);
                }
                return list;
            }
            List<Pass> below = TopSide(minLane - 1, -1), above = TopSide(maxLane + 1, 1);

            // the top passes are on the side the slant leads up to: their end on that side lies
            // furthest in the direction the slanted passes leave the field
            double ux = 0, uy = 0;
            foreach (Pass ps in slanted)
            {
                int n = ps.Pts.Count;
                double h = upEnd[ps] ? HeadingOf(ps.Pts[n - 3], ps.B) : HeadingOf(ps.Pts[2], ps.A);
                ux += Math.Sin(h); uy += Math.Cos(h);
            }
            double Reach(List<Pass> group)
            {
                if (group.Count == 0) return double.MinValue;
                double sum = 0;
                foreach (Pass ps in group) sum += Math.Max(ps.A.E * ux + ps.A.N * uy, ps.B.E * ux + ps.B.N * uy);
                return sum / group.Count;
            }
            bool useBelow = Reach(below) >= Reach(above);
            List<Pass> tops = useBelow ? below : above;
            if (tops.Count == 0) return order;

            // slanted passes starting next to the top ones
            slanted.Sort((x, y) => useBelow ? x.Lane.CompareTo(y.Lane) : y.Lane.CompareTo(x.Lane));

            // the lines up to the slant counted from the far side (1 = the top pass furthest
            // from the slant, the last one = the last slanted pass): up 1, turn along the top
            // into 5, down 5, into 2, up 2, into 6, down 6, into 3, ...
            var lines = new List<Pass>(tops);
            lines.Reverse();
            var slantOrder = new List<Pass>(slanted);
            lines.AddRange(slantOrder);
            bool UpIsB(Pass p) => p.B.E * ux + p.B.N * uy > p.A.E * ux + p.A.N * uy;
            var block = new List<Step>();
            int[] seq = { 1, 5, 2, 6, 3, 7, 4, 9 };
            for (int k = 0; k < seq.Length; k++)
            {
                if (seq[k] > lines.Count) break;
                Pass p = lines[seq[k] - 1];
                bool up = k % 2 == 0;
                block.Add(new Step { Pass = p, Reverse = up ? !UpIsB(p) : UpIsB(p) });
            }
            var inBlock = new HashSet<Pass>();
            foreach (Step st in block) inBlock.Add(st.Pass);
            var rest = new List<Step>();
            foreach (Step st in order) if (!inBlock.Contains(st.Pass)) rest.Add(st);

            // put the block where the joins are shortest
            double Join(Step a, Step b)
            {
                ExitPose(a, out V2 p, out double h);
                EntryPose(b.Pass, b.Reverse, out V2 q, out double hq);
                return DubinsLength(p, h, q, hq, R);
            }
            int bestPos = rest.Count;
            double bestCost = double.MaxValue;
            for (int pos = 0; pos <= rest.Count; pos++)
            {
                double c = 0;
                if (pos > 0) c += Join(rest[pos - 1], block[0]);
                if (pos < rest.Count) c += Join(block[block.Count - 1], rest[pos]);
                if (pos > 0 && pos < rest.Count) c -= Join(rest[pos - 1], rest[pos]);
                if (c < bestCost) { bestCost = c; bestPos = pos; }
            }
            rest.InsertRange(bestPos, block);

            // after the block go on into the rest of the field: a neighbour of the last block
            // pass next (only a loop fits there) is moved to where it fits best
            int after = bestPos + block.Count;
            Pass lastBlock = block[block.Count - 1].Pass;
            if (after < rest.Count && Math.Abs(rest[after].Pass.Lane - lastBlock.Lane) <= 1)
            {
                Step moved = rest[after];
                rest.RemoveAt(after);
                double TL(Step a, Step b)
                {
                    ExitPose(a, out V2 p, out double h);
                    EntryPose(b.Pass, b.Reverse, out V2 q, out double hq);
                    return TurnLength(p, h, q, hq, R);
                }
                int bestAt = rest.Count;
                bool bestRev = moved.Reverse;
                double bestAdd = double.MaxValue;
                for (int pos = 0; pos <= rest.Count; pos++)
                {
                    // not inside the block
                    if (pos > bestPos && pos <= bestPos + block.Count - 1) continue;
                    if (pos == after) continue;
                    foreach (bool rev in new[] { false, true })
                    {
                        var st = new Step { Pass = moved.Pass, Reverse = rev };
                        double c = 0;
                        if (pos > 0) c += TL(rest[pos - 1], st);
                        if (pos < rest.Count) c += TL(st, rest[pos]);
                        if (pos > 0 && pos < rest.Count) c -= TL(rest[pos - 1], rest[pos]);
                        if (c < bestAdd) { bestAdd = c; bestAt = pos; bestRev = rev; }
                    }
                }
                rest.Insert(bestAt, new Step { Pass = moved.Pass, Reverse = bestRev });
            }
            return rest;
        }

        /// <summary>The pass meets the boundary at a gentle angle (slanted end).</summary>
        private static bool IsSlantedEnd(V2 p, double h, PolygonTester fence)
        {
            V2 d = new V2(Math.Sin(h), Math.Cos(h));
            V2 edge = fence.EdgeDirection(p);
            double c = Math.Abs(edge.E * d.E + edge.N * d.N);
            return c >= 0.85 && c <= 0.995;   // gently slanted (under about 30 degrees)
        }

        /// <summary>
        /// Straight on from the pass end until the centre of the turn circle is R plus the
        /// clearance from the boundary, the same before the next pass; the two circles are then
        /// joined by a straight line that runs parallel to the boundary. Null if it does not fit.
        /// </summary>
        private static List<V2> BoundaryTurn(V2 p0, double h0, V2 p1, double h1, double R, double s, PolygonTester fence)
        {
            V2 d0 = new V2(Math.Sin(h0), Math.Cos(h0));
            V2 d1 = new V2(Math.Sin(h1), Math.Cos(h1));
            V2 left0 = new V2(-d0.N, d0.E), left1 = new V2(-d1.N, d1.E);
            V2 rel = p1 - p0;
            bool turnLeft = rel.E * left0.E + rel.N * left0.N > 0;
            V2 n0 = turnLeft ? left0 : left0 * -1.0;
            V2 n1 = turnLeft ? left1 : left1 * -1.0;
            double target = fence.Clearance + 0.3 + R;   // centre distance from the boundary

            double e0 = FurthestCentre(p0, d0, n0, R, target, fence);
            double e1 = FurthestCentre(p1, d1 * -1.0, n1, R, target, fence);
            if (e0 < 0 || e1 < 0) return null;

            V2 a = p0 + d0 * e0, b = p1 - d1 * e1;
            List<V2> best = null;
            double bestLen = double.MaxValue;
            foreach (DubinsSolution sol in AllDubins(a, h0, b, h1, R))
            {
                List<V2> arc = PathFromSolution(a, h0, b, sol, R, s);
                if (arc == null || arc.Count < 2 || TotalRotation(arc) > Math.PI * 1.2) continue;
                double len = OpenLength(arc);
                if (len < bestLen) { bestLen = len; best = arc; }
            }
            if (best == null) return null;

            var pts = new List<V2>();
            AddStraight(pts, p0, a, s);
            for (int k = 1; k < best.Count; k++) pts.Add(best[k]);
            AddStraight(pts, b, p1, s);
            return CountOutside(pts, fence) > 0 ? null : pts;
        }

        /// <summary>Direction of the boundary where it is met driving straight on from p.</summary>
        private static V2? AheadEdgeDirection(PolygonTester fence, V2 p, double heading)
        {
            List<V2> poly = fence.Polygon;
            V2 d = new V2(Math.Sin(heading), Math.Cos(heading));
            int n = poly.Count, hitI = -1;
            double best = double.MaxValue;
            for (int i = 0; i < n; i++)
            {
                V2 a = poly[i], e = poly[(i + 1) % n] - a;
                double den = d.E * e.N - d.N * e.E;
                if (Math.Abs(den) < 1e-9) continue;
                V2 w = a - p;
                double t = (w.E * e.N - w.N * e.E) / den;
                double u = (w.E * d.N - w.N * d.E) / den;
                if (t > 1e-6 && u >= 0 && u <= 1 && t < best) { best = t; hitI = i; }
            }
            if (hitI < 0) return null;
            return Dir(poly[(hitI - 2 + n) % n], poly[(hitI + 3) % n]);
        }

        /// <summary>
        /// True when the part of a turn that runs across the passes keeps to the boundary line
        /// (about the clearance from the boundary): it does not cut across a corner.
        /// </summary>
        private static bool AlongBoundaryLine(List<V2> turn, double h0, double h1, PolygonTester fence)
        {
            double limit = fence.Clearance + 0.3 + 1.5;
            for (int i = 1; i < turn.Count; i++)
            {
                double h = HeadingOf(turn[i - 1], turn[i]);
                if (Math.Abs(AngleDiff(h, h0)) < 1.05 || Math.Abs(AngleDiff(h, h1)) < 1.05) continue;
                if (fence.EdgeDistance(turn[i], limit + 1.0) > limit) return false;
            }
            return true;
        }

        /// <summary>
        /// Distance from p to the boundary edges it drives towards: edges running along the
        /// heading (a long side next to the first pass) do not count, only the end ahead or behind.
        /// </summary>
        private static double AheadEdgeDistance(PolygonTester fence, V2 p, double heading, double radius)
        {
            List<V2> poly = fence.Polygon;
            V2 d = new V2(Math.Sin(heading), Math.Cos(heading));
            double best = radius;
            int n = poly.Count;
            for (int i = 0; i < n; i++)
            {
                V2 a = poly[i], b = poly[(i + 1) % n];
                // quick reject
                if (Math.Abs(a.E - p.E) > best + 50.0 && Math.Abs(b.E - p.E) > best + 50.0) continue;
                if (Math.Abs(a.N - p.N) > best + 50.0 && Math.Abs(b.N - p.N) > best + 50.0) continue;
                V2 e = b - a;
                double len = e.Length;
                if (len < 1e-9) continue;
                // edge (nearly) parallel to the driving direction: a side, not an end
                if (Math.Abs((e.E * d.N - e.N * d.E) / len) < 0.02) continue;
                double u = Math.Max(0, Math.Min(1, ((p.E - a.E) * e.E + (p.N - a.N) * e.N) / (len * len)));
                double dist = (a + e * u - p).Length;
                if (dist < best) best = dist;
            }
            return best;
        }

        // Longest straight run from p along dir with the turn centre (p + n * R) still at least
        // target from the boundary; -1 if not even at the start.
        private static double FurthestCentre(V2 p, V2 dir, V2 n, double R, double target, PolygonTester fence)
        {
            double best = -1;
            for (double e = 0; e <= 160.0; e += 0.25)
            {
                V2 c = p + dir * e + n * R;
                if (!fence.Inside(c) || fence.EdgeDistance(c, target + 1.0) < target) break;
                best = e;
            }
            return best;
        }

        private static double TotalRotation(List<V2> pts)
        {
            double total = 0;
            for (int i = 2; i < pts.Count; i++)
            {
                if ((pts[i] - pts[i - 1]).Length < 1e-6 || (pts[i - 1] - pts[i - 2]).Length < 1e-6) continue;
                total += Math.Abs(AngleDiff(HeadingOf(pts[i - 1], pts[i]), HeadingOf(pts[i - 2], pts[i - 1])));
            }
            return total;
        }

        /// <summary>
        /// Number of points at the start (or end) of a turn that still run straight on in the pass
        /// direction (within 3 degrees).
        /// </summary>
        private static int StraightCount(List<V2> turn, double heading, bool fromEnd)
        {
            int n = turn.Count, k = 1;
            const double tol = 3.0 * Math.PI / 180.0;
            while (k < n)
            {
                V2 a = fromEnd ? turn[n - k - 1] : turn[k - 1];
                V2 b = fromEnd ? turn[n - k] : turn[k];
                if ((b - a).Length < 1e-6) { k++; continue; }
                if (Math.Abs(AngleDiff(HeadingOf(a, b), heading)) > tol) break;
                k++;
            }
            return k;
        }

        private static void AddStraight(List<V2> pts, V2 from, V2 to, double s)
        {
            double len = (to - from).Length;
            if (len < 0.01)
            {
                if (pts.Count == 0) pts.Add(from);
                return;
            }
            int n = Math.Max(1, (int)Math.Ceiling(len / s));
            for (int i = 0; i <= n; i++)
            {
                if (pts.Count > 0 && i == 0) continue;
                pts.Add(from + (to - from) * (i / (double)n));
            }
        }

        private static List<V2> LoopTransfer(V2 p0, double h0, V2 p1, double h1, List<V2> loop, double R, double s,
            PolygonTester fence, out int outside)
        {
            return LoopTransfer(p0, h0, p1, h1, loop, R, s, fence, out outside, out _);
        }

        private static List<V2> LoopTransfer(V2 p0, double h0, V2 p1, double h1, List<V2> loop, double R, double s,
            PolygonTester fence, out int outside, out double cost, bool shortJoins = false, double cornerTol = -1.0)
        {
            List<V2> best = null;
            double bestCost = double.MaxValue;
            outside = int.MaxValue;
            cost = double.MaxValue;
            // transfers: joins may run a little inside a corner arc of the line
            if (cornerTol < 0) cornerTol = 0.8 * R;
            for (int dir = 0; dir < 2; dir++)
            {
                List<V2> lp = loop;
                if (dir == 1) { lp = new List<V2>(loop); lp.Reverse(); }
                int m = lp.Count;

                // where the line is met: straight ahead of the pass end when that is close,
                // else the nearest point (the same backwards from the next pass)
                int ia = RayIndex(lp, p0, h0, 2.5 * R), ib = RayIndex(lp, p1, h1 + Math.PI, 2.5 * R);
                double da = Math.Max(1.0, (lp[ia] - p0).Length), db = Math.Max(1.0, (lp[ib] - p1).Length);
                // join and leave the line a bit ahead / before, so both are smooth S-curves;
                // a few distances are tried, near inner corners the shortest may not fit
                int shA0 = (int)Math.Ceiling((2.0 * Math.Sqrt(R * da) + 2.0 * R) / s);
                int shB0 = (int)Math.Ceiling((2.0 * Math.Sqrt(R * db) + 2.0 * R) / s);

                // points of the line outside (forbidden area), summed for quick counts
                var outSum = new int[m + 1];
                for (int k = 0; k < m; k++) outSum[k + 1] = outSum[k] + (fence.Clear(lp[k]) ? 0 : 1);
                int OutAlong(int from, int count)
                {
                    if (from + count <= m) return outSum[from + count] - outSum[from];
                    return outSum[m] - outSum[from] + outSum[from + count - m];
                }

                double[] fs = { 0.1, 0.2, 0.4, 0.7, 1.0, 1.6, 2.5 };
                var joins = new List<(int idx, List<V2> path, int o, double cost)>();
                var leaves = new List<(int idx, List<V2> path, int o, double cost)>();
                foreach (double fa in fs)
                {
                    int a = (ia + (int)(shA0 * fa)) % m;
                    double ha = HeadingOf(lp[a], lp[(a + 1) % m]);
                    // join the boundary line only where it runs at most about 175 degrees from the pass
                    if (Math.Abs(AngleDiff(ha, h0)) > 3.05) continue;
                    List<V2> c1 = ClearDubins(p0, h0, lp[a], ha, R, s, fence);
                    if (c1 == null) continue;
                    // never cut a corner of the line short: where the skipped part of the line
                    // turns, the join must run close along it (corners are driven on the arc)
                    if (TurnAlong(lp, ia, a) > 0.6 && FarestFrom(lp, ia, a, c1) > cornerTol) continue;
                    // join the line with a short turn, not diagonally across
                    if (shortJoins && OpenLength(c1) > Math.PI * R + 2.0 * R) continue;
                    joins.Add((a, c1, CountOutside(c1, fence), OpenLength(c1) + (TotalRotation(c1) > 1.3 * Math.PI ? 500.0 : 0.0)));
                }
                foreach (double fb in fs)
                {
                    int b = ((ib - (int)(shB0 * fb)) % m + m) % m;
                    double hb = HeadingOf(lp[b], lp[(b + 1) % m]);
                    if (Math.Abs(AngleDiff(h1, hb)) > 3.05) continue;
                    List<V2> c2 = ClearDubins(lp[b], hb, p1, h1, R, s, fence);
                    if (c2 == null) continue;
                    if (TurnAlong(lp, b, ib) > 0.6 && FarestFrom(lp, b, ib, c2) > cornerTol) continue;
                    // leave the line with a short turn into the pass, not diagonally across
                    if (shortJoins && OpenLength(c2) > Math.PI * R + 2.0 * R) continue;
                    leaves.Add((b, c2, CountOutside(c2, fence), OpenLength(c2) + (TotalRotation(c2) > 1.3 * Math.PI ? 500.0 : 0.0)));
                }

                foreach (var j in joins)
                {
                    foreach (var l in leaves)
                    {
                        int steps = ((l.idx - j.idx) % m + m) % m;
                        if (steps < 2 || steps > m * 0.95) continue;
                        int o = j.o + l.o + OutAlong((j.idx + 1) % m, steps - 1);
                        double total = j.cost + l.cost + steps * s + 1000.0 * o;
                        if (total >= bestCost) continue;
                        var pts = new List<V2>(j.path.Count + steps + l.path.Count);
                        pts.AddRange(j.path);
                        for (int k = 1; k < steps; k++) pts.Add(lp[(j.idx + k) % m]);
                        pts.AddRange(l.path);
                        bestCost = total; best = pts; outside = o;
                    }
                }
            }
            cost = bestCost;
            return best;
        }

        /// <summary>
        /// Index of the line point met straight ahead of p (heading h) within maxDist,
        /// else the nearest point of the line.
        /// </summary>
        private static int RayIndex(List<V2> lp, V2 p, double h, double maxDist)
        {
            V2 d = new V2(Math.Sin(h), Math.Cos(h));
            for (double t = 0; t <= maxDist; t += 0.5)
            {
                int i = NearestIndex(lp, p + d * t);
                if ((lp[i] - (p + d * t)).Length < 0.6) return i;
            }
            return NearestIndex(lp, p);
        }

        /// <summary>
        /// Largest distance of the line's points from index i forward to index j from the path.
        /// </summary>
        private static double FarestFrom(List<V2> lp, int i, int j, List<V2> path)
        {
            int m = lp.Count;
            int steps = ((j - i) % m + m) % m;
            double worst = 0;
            for (int k = 0; k <= steps; k++)
            {
                V2 p = lp[(i + k) % m];
                double best = double.MaxValue;
                foreach (V2 q in path) best = Math.Min(best, (q - p).Length);
                worst = Math.Max(worst, best);
            }
            return worst;
        }

        /// <summary>
        /// Total heading change along a closed line from index i forward to index j.
        /// </summary>
        private static double TurnAlong(List<V2> lp, int i, int j)
        {
            int m = lp.Count;
            int steps = ((j - i) % m + m) % m;
            double sum = 0;
            for (int k = 0; k + 1 < steps; k++)
            {
                int u = (i + k) % m;
                double h = HeadingOf(lp[u], lp[(u + 1) % m]);
                double hn = HeadingOf(lp[(u + 1) % m], lp[(u + 2) % m]);
                sum += Math.Abs(AngleDiff(hn, h));
            }
            return sum;
        }

        /// <summary>
        /// Shortest Dubins path that keeps the tool inside the field; if none does, the one with the
        /// fewest points too close to the boundary.
        /// </summary>
        private static List<V2> ClearDubins(V2 p0, double h0, V2 p1, double h1, double R, double spacing, PolygonTester fence)
        {
            // inside and without a loop first (shortest), then inside with a loop,
            // else the one with the fewest points outside
            List<V2> best = null, insideLoop = null;
            int bestOut = int.MaxValue;
            foreach (DubinsSolution sol in AllDubins(p0, h0, p1, h1, R))
            {
                List<V2> pts = PathFromSolution(p0, h0, p1, sol, R, spacing);
                int o = CountOutside(pts, fence);
                if (o == 0)
                {
                    if (TotalRotation(pts) <= 1.3 * Math.PI) return pts;
                    if (insideLoop == null) insideLoop = pts;
                    continue;
                }
                if (o < bestOut) { bestOut = o; best = pts; }
            }
            return insideLoop ?? best;
        }

        private static List<V2> PathFromSolution(V2 p0, double h0, V2 p1, DubinsSolution sol, double R, double spacing)
        {
            var pts = new List<V2>();
            double[] seg = { sol.T, sol.P, sol.Q };
            V2 pos = p0;
            double th = ToTheta(h0);
            double stepNorm = spacing / R;
            for (int k = 0; k < 3; k++)
            {
                double remaining = seg[k];
                int dir = Types[sol.Type][k];
                while (remaining > 1e-9)
                {
                    double step = Math.Min(stepNorm, remaining);
                    Advance(ref pos, ref th, dir, step, R);
                    remaining -= step;
                    pts.Add(pos);
                }
            }
            if (pts.Count == 0 || (pts[pts.Count - 1] - p1).Length > 1e-6) pts.Add(p1);
            return pts;
        }

        private static bool SolveDubins(V2 p0, double h0, V2 p1, double h1, double R, out DubinsSolution best)
        {
            best = new DubinsSolution { T = double.MaxValue };
            double th0 = ToTheta(h0), th1 = ToTheta(h1);
            double dx = p1.E - p0.E, dy = p1.N - p0.N;
            double d = Math.Sqrt(dx * dx + dy * dy) / R;
            double theta = d > 1e-9 ? Mod2Pi(Math.Atan2(dy, dx)) : 0;
            double a = Mod2Pi(th0 - theta), b = Mod2Pi(th1 - theta);
            double sa = Math.Sin(a), sb = Math.Sin(b), ca = Math.Cos(a), cb = Math.Cos(b), cab = Math.Cos(a - b);
            bool found = false;

            for (int type = 0; type < 6; type++)
            {
                double t, p, q;
                if (!DubinsWord(type, d, a, b, sa, sb, ca, cb, cab, out t, out p, out q)) continue;
                double len = t + p + q;
                if (len >= best.T + best.P + best.Q && found) continue;

                // Verify the end point (guards against numerical problems)
                Simulate(p0, th0, R, type, t, p, q, out V2 endP, out double endTh);
                if ((endP - p1).Length > 0.05 * Math.Max(1.0, R) || Math.Abs(AngleDiff(endTh, th1)) > 0.02) continue;

                best = new DubinsSolution { T = t, P = p, Q = q, Type = type };
                found = true;
            }
            return found;
        }

        private static bool DubinsWord(int type, double d, double a, double b, double sa, double sb, double ca, double cb, double cab,
            out double t, out double p, out double q)
        {
            t = p = q = 0;
            double tmp0, tmp1, psq;
            switch (type)
            {
                case 0: // LSL
                    tmp0 = d + sa - sb;
                    psq = 2 + d * d - 2 * cab + 2 * d * (sa - sb);
                    if (psq < 0) return false;
                    tmp1 = Math.Atan2(cb - ca, tmp0);
                    t = Mod2Pi(tmp1 - a); p = Math.Sqrt(psq); q = Mod2Pi(b - tmp1);
                    return true;
                case 1: // RSR
                    tmp0 = d - sa + sb;
                    psq = 2 + d * d - 2 * cab + 2 * d * (sb - sa);
                    if (psq < 0) return false;
                    tmp1 = Math.Atan2(ca - cb, tmp0);
                    t = Mod2Pi(a - tmp1); p = Math.Sqrt(psq); q = Mod2Pi(tmp1 - b);
                    return true;
                case 2: // LSR
                    psq = -2 + d * d + 2 * cab + 2 * d * (sa + sb);
                    if (psq < 0) return false;
                    p = Math.Sqrt(psq);
                    tmp1 = Math.Atan2(-ca - cb, d + sa + sb) - Math.Atan2(-2.0, p);
                    t = Mod2Pi(tmp1 - a); q = Mod2Pi(tmp1 - b);
                    return true;
                case 3: // RSL
                    psq = d * d - 2 + 2 * cab - 2 * d * (sa + sb);
                    if (psq < 0) return false;
                    p = Math.Sqrt(psq);
                    tmp1 = Math.Atan2(ca + cb, d - sa - sb) - Math.Atan2(2.0, p);
                    t = Mod2Pi(a - tmp1); q = Mod2Pi(b - tmp1);
                    return true;
                case 4: // RLR
                    tmp0 = (6.0 - d * d + 2 * cab + 2 * d * (sa - sb)) / 8.0;
                    if (Math.Abs(tmp0) > 1) return false;
                    tmp1 = Math.Atan2(ca - cb, d - sa + sb);
                    p = Mod2Pi(TwoPi - Math.Acos(tmp0));
                    t = Mod2Pi(a - tmp1 + p / 2.0);
                    q = Mod2Pi(a - b - t + p);
                    return true;
                case 5: // LRL
                    tmp0 = (6.0 - d * d + 2 * cab + 2 * d * (sb - sa)) / 8.0;
                    if (Math.Abs(tmp0) > 1) return false;
                    tmp1 = Math.Atan2(ca - cb, d + sa - sb);
                    p = Mod2Pi(TwoPi - Math.Acos(tmp0));
                    t = Mod2Pi(-a - tmp1 + p / 2.0);
                    q = Mod2Pi(b - a - t + p);
                    return true;
            }
            return false;
        }

        private static void Advance(ref V2 pos, ref double th, int dir, double len, double R)
        {
            if (dir == 0)
            {
                pos = new V2(pos.E + Math.Cos(th) * len * R, pos.N + Math.Sin(th) * len * R);
            }
            else if (dir > 0) // left
            {
                V2 c = new V2(pos.E - Math.Sin(th) * R, pos.N + Math.Cos(th) * R);
                th += len;
                pos = new V2(c.E + Math.Sin(th) * R, c.N - Math.Cos(th) * R);
            }
            else // right
            {
                V2 c = new V2(pos.E + Math.Sin(th) * R, pos.N - Math.Cos(th) * R);
                th -= len;
                pos = new V2(c.E - Math.Sin(th) * R, c.N + Math.Cos(th) * R);
            }
        }

        private static void Simulate(V2 p0, double th0, double R, int type, double t, double p, double q, out V2 pos, out double th)
        {
            pos = p0; th = th0;
            Advance(ref pos, ref th, Types[type][0], t, R);
            Advance(ref pos, ref th, Types[type][1], p, R);
            Advance(ref pos, ref th, Types[type][2], q, R);
        }

        public static double DubinsLength(V2 p0, double h0, V2 p1, double h1, double R)
        {
            return SolveDubins(p0, h0, p1, h1, R, out DubinsSolution sol) ? sol.Length * R : double.MaxValue / 4;
        }

        /// <summary>Points of the shortest Dubins path, excluding the start point, including the goal.</summary>
        public static List<V2> DubinsPath(V2 p0, double h0, V2 p1, double h1, double R, double spacing)
        {
            if (!SolveDubins(p0, h0, p1, h1, R, out DubinsSolution sol)) return null;
            return PathFromSolution(p0, h0, p1, sol, R, spacing);
        }

        // =====================================================================
        // Geometry helpers
        // =====================================================================

        private static double Sq(double x) => x * x;

        public static double Mod2Pi(double a)
        {
            a %= TwoPi;
            if (a < 0) a += TwoPi;
            return a;
        }

        public static double AngleDiff(double a, double b)
        {
            double d = Mod2Pi(a - b);
            if (d > Math.PI) d -= TwoPi;
            return d;
        }

        /// <summary>AgOpenGPS heading from a to b.</summary>
        public static double HeadingOf(V2 a, V2 b) => Mod2Pi(Math.Atan2(b.E - a.E, b.N - a.N));

        private static V2 Dir(V2 a, V2 b)
        {
            V2 d = b - a;
            double l = d.Length;
            return l > 1e-12 ? d * (1.0 / l) : new V2(0, 1);
        }

        private static double SignedArea(List<V2> poly)
        {
            double a = 0;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
                a += (poly[j].E * poly[i].N) - (poly[i].E * poly[j].N);
            return a * 0.5;
        }

        private static double LoopLength(List<V2> loop)
        {
            double l = 0;
            for (int i = 0; i < loop.Count; i++) l += (loop[(i + 1) % loop.Count] - loop[i]).Length;
            return l;
        }

        private static double OpenLength(List<V2> line)
        {
            double l = 0;
            for (int i = 1; i < line.Count; i++) l += (line[i] - line[i - 1]).Length;
            return l;
        }

        private static void GetBounds(List<V2> pts, out V2 min, out V2 max)
        {
            min = new V2(double.MaxValue, double.MaxValue);
            max = new V2(double.MinValue, double.MinValue);
            foreach (V2 p in pts)
            {
                if (p.E < min.E) min.E = p.E;
                if (p.N < min.N) min.N = p.N;
                if (p.E > max.E) max.E = p.E;
                if (p.N > max.N) max.N = p.N;
            }
        }

        public static bool PointInPolygon(List<V2> poly, V2 p)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                if (((poly[i].N > p.N) != (poly[j].N > p.N)) &&
                    (p.E < (poly[j].E - poly[i].E) * (p.N - poly[i].N) / (poly[j].N - poly[i].N) + poly[i].E))
                    inside = !inside;
            }
            return inside;
        }

        private static int CountOutside(List<V2> pts, PolygonTester fence)
        {
            int n = 0;
            foreach (V2 p in pts) if (!fence.Clear(p)) n++;
            return n;
        }

        private static List<V2> ResampleLoop(List<V2> loop, double s)
        {
            var closed = new List<V2>(loop) { loop[0] };
            List<V2> r = ResampleOpen(closed, s);
            if (r.Count > 1 && (r[r.Count - 1] - r[0]).Length < s * 0.5) r.RemoveAt(r.Count - 1);
            return r;
        }

        private static List<V2> ResampleOpen(List<V2> line, double s)
        {
            var r = new List<V2>();
            if (line.Count == 0) return r;
            r.Add(line[0]);
            double carry = 0;
            for (int i = 1; i < line.Count; i++)
            {
                V2 a = line[i - 1], b = line[i];
                double segLen = (b - a).Length;
                if (segLen < 1e-9) continue;
                double pos = s - carry;
                while (pos <= segLen)
                {
                    r.Add(a + (b - a) * (pos / segLen));
                    pos += s;
                }
                carry = segLen - (pos - s);
            }
            if ((r[r.Count - 1] - line[line.Count - 1]).Length > s * 0.25) r.Add(line[line.Count - 1]);
            return r;
        }

        private static List<V2> SmoothOpen(List<V2> line, int window, int passes)
        {
            var cur = new List<V2>(line);
            int h = window / 2;
            for (int pass = 0; pass < passes; pass++)
            {
                var next = new List<V2>(cur.Count);
                for (int i = 0; i < cur.Count; i++)
                {
                    int a = Math.Max(0, i - h), b = Math.Min(cur.Count - 1, i + h);
                    double e = 0, n = 0;
                    for (int k = a; k <= b; k++) { e += cur[k].E; n += cur[k].N; }
                    int c = b - a + 1;
                    next.Add(new V2(e / c, n / c));
                }
                next[0] = cur[0];
                next[next.Count - 1] = cur[cur.Count - 1];
                cur = next;
            }
            return cur;
        }

        private static List<V2> SmoothLoop(List<V2> loop, int half, int passes)
        {
            var cur = new List<V2>(loop);
            int n = cur.Count;
            for (int pass = 0; pass < passes; pass++)
            {
                var next = new List<V2>(n);
                for (int i = 0; i < n; i++)
                {
                    double e = 0, no = 0;
                    for (int k = -half; k <= half; k++) { V2 p = cur[((i + k) % n + n) % n]; e += p.E; no += p.N; }
                    int c = 2 * half + 1;
                    next.Add(new V2(e / c, no / c));
                }
                cur = next;
            }
            return cur;
        }

        /// <summary>
        /// Smooths a loop until no corner is tighter than the turning radius (max 40 passes).
        /// </summary>
        private static List<V2> RoundLoop(List<V2> loop, double R, double s)
        {
            int half = Math.Max(2, (int)Math.Round(R / (2.0 * s)));
            int span = Math.Max(2, (int)Math.Round(2.0 / s));
            var cur = loop;
            for (int pass = 0; pass < 40; pass++)
            {
                double maxK = 0;
                int n = cur.Count;
                for (int i = 0; i < n; i++)
                {
                    V2 a = cur[(i - span + n) % n], b = cur[i], c = cur[(i + span) % n];
                    double d = (c - a).Length;
                    if (d < 1e-6) continue;
                    double k = Math.Abs(AngleDiff(HeadingOf(b, c), HeadingOf(a, b))) / (d * 0.5);
                    if (k > maxK) maxK = k;
                }
                if (maxK <= 1.0 / R) break;
                cur = ResampleLoop(SmoothLoop(cur, half, 1), s);
            }
            return cur;
        }

        private static int NearestIndex(List<V2> pts, V2 p)
        {
            int best = 0;
            double bd = double.MaxValue;
            for (int i = 0; i < pts.Count; i++)
            {
                double d = Sq(pts[i].E - p.E) + Sq(pts[i].N - p.N);
                if (d < bd) { bd = d; best = i; }
            }
            return best;
        }

        /// <summary>
        /// Longest part of the boundary that is straight or gently curved.
        /// </summary>
        private static List<V2> FindReferenceEdge(List<V2> fence, double s)
        {
            int n = fence.Count;
            var head = new double[n];
            int k = Math.Max(1, (int)Math.Round(3.0 / s)); // heading over a few meters
            for (int i = 0; i < n; i++) head[i] = HeadingOf(fence[(i - k + n) % n], fence[(i + k) % n]);

            double maxTotal = 40.0 * Math.PI / 180.0;  // total bend along the edge
            double maxLocal = 20.0 * Math.PI / 180.0;  // bend over ~10 m
            int localPts = Math.Max(2, (int)Math.Round(10.0 / s));

            int bestStart = 0, bestLen = 0;
            int step = Math.Max(1, n / 1500);
            for (int st = 0; st < n; st += step)
            {
                double minRel = 0, maxRel = 0;
                int len = 1;
                while (len < n)
                {
                    int e = (st + len) % n;
                    double rel = AngleDiff(head[e], head[st]);
                    if (rel < minRel) minRel = rel;
                    if (rel > maxRel) maxRel = rel;
                    if (maxRel - minRel > maxTotal) break;
                    if (len >= localPts && Math.Abs(AngleDiff(head[e], head[(e - localPts + n) % n])) > maxLocal) break;
                    len++;
                }
                if (len > bestLen) { bestLen = len; bestStart = st; }
            }

            var edge = new List<V2>(bestLen);
            for (int i = 0; i < bestLen; i++) edge.Add(fence[(bestStart + i) % n]);
            return edge;
        }

        private static List<V2> ExtendOpen(List<V2> line, double ext, double s)
        {
            int k = Math.Min(line.Count - 1, Math.Max(1, (int)(5.0 / s)));
            V2 d0 = Dir(line[k], line[0]);
            V2 d1 = Dir(line[line.Count - 1 - k], line[line.Count - 1]);
            int m = (int)Math.Ceiling(ext / s);
            var r = new List<V2>(line.Count + 2 * m);
            for (int i = m; i >= 1; i--) r.Add(line[0] + d0 * (i * s));
            r.AddRange(line);
            for (int i = 1; i <= m; i++) r.Add(line[line.Count - 1] + d1 * (i * s));
            return r;
        }

        /// <summary>
        /// Offset an open line sideways (positive = left of travel direction). Points that end up
        /// closer than the offset distance to the original line (loops at inside bends) are removed.
        /// </summary>
        private static List<V2> OffsetOpen(List<V2> line, double dist, SegmentGrid grid, double s)
        {
            var r = new List<V2>(line.Count);
            double ad = Math.Abs(dist);
            for (int i = 0; i < line.Count; i++)
            {
                V2 d = Dir(line[Math.Max(0, i - 1)], line[Math.Min(line.Count - 1, i + 1)]);
                V2 p = line[i] + new V2(-d.N, d.E) * dist;
                if (grid.MinDistance(p, ad) < ad * 0.98) continue;
                r.Add(p);
            }
            return r.Count > 1 ? ResampleOpen(r, s) : r;
        }

        /// <summary>
        /// Offset a counter clockwise loop inwards. Points closer than the distance to the original
        /// loop (cut corners) are removed.
        /// </summary>
        private static List<V2> InsetLoop(List<V2> loop, double dist, double s)
        {
            int n = loop.Count;
            var grid = new SegmentGrid(loop, true, Math.Max(5.0, dist));
            var tester = new PolygonTester(loop);
            var r = new List<V2>(n);
            for (int i = 0; i < n; i++)
            {
                V2 d = Dir(loop[(i - 1 + n) % n], loop[(i + 1) % n]);
                V2 p = loop[i] + new V2(-d.N, d.E) * dist; // left = inside for CCW
                if (grid.MinDistance(p, dist) < dist * 0.98) continue;
                if (!tester.Inside(p)) continue;
                r.Add(p);
            }
            if (r.Count < 3) return new List<V2>();
            return ResampleLoop(r, s);
        }

        /// <summary>Parts of a line that lie inside a polygon.</summary>
        private static List<List<V2>> InsideRuns(List<V2> line, List<V2> poly)
        {
            var tester = new PolygonTester(poly);
            var runs = new List<List<V2>>();
            List<V2> cur = null;
            foreach (V2 p in line)
            {
                if (tester.Inside(p))
                {
                    if (cur == null) { cur = new List<V2>(); runs.Add(cur); }
                    cur.Add(p);
                }
                else cur = null;
            }
            return runs;
        }

        /// <summary>Position and travel heading at the end of the path built so far.</summary>
        private static void LastPose(List<PlanPoint> path, double s, out V2 p, out double h)
        {
            PlanPoint l = path[path.Count - 1];
            p = new V2(l.E, l.N);
            int back = Math.Max(1, (int)Math.Ceiling(2.0 / s));
            PlanPoint a = path[Math.Max(0, path.Count - 1 - back)];
            h = HeadingOf(new V2(a.E, a.N), p);
        }

        private static void AppendPoints(List<PlanPoint> path, List<V2> pts, bool work, bool skipFirstIfSame, bool headland = false)
        {
            for (int i = 0; i < pts.Count; i++)
            {
                if (i == 0 && skipFirstIfSame && path.Count > 0)
                {
                    PlanPoint l = path[path.Count - 1];
                    if (Sq(l.E - pts[0].E) + Sq(l.N - pts[0].N) < 0.01) continue;
                }
                path.Add(new PlanPoint { E = pts[i].E, N = pts[i].N, IsWork = work, IsHeadland = work && headland });
            }
        }

        private static List<PlanPoint> Finish(List<PlanPoint> path, double s)
        {
            // Drop points that are too close together, then set headings
            var r = new List<PlanPoint>(path.Count);
            foreach (PlanPoint p in path)
            {
                if (r.Count > 0)
                {
                    PlanPoint l = r[r.Count - 1];
                    if (Sq(l.E - p.E) + Sq(l.N - p.N) < Sq(s * 0.3)) continue;
                }
                r.Add(p);
            }
            for (int i = 0; i < r.Count; i++)
            {
                PlanPoint p = r[i];
                // neighbours driven in the same direction (a direction change is a cusp)
                int ia = i > 0 && r[i - 1].IsReverse == p.IsReverse ? i - 1 : i;
                int ib = i + 1 < r.Count && r[i + 1].IsReverse == p.IsReverse ? i + 1 : i;
                if (ia == ib) { ia = Math.Max(0, i - 1); ib = i; }
                PlanPoint a = r[ia], b = r[ib];
                double motion = HeadingOf(new V2(a.E, a.N), new V2(b.E, b.N));
                p.Heading = p.IsReverse ? Mod2Pi(motion + Math.PI) : motion;
                r[i] = p;
            }
            return r;
        }

        // =====================================================================
        // Spatial helpers
        // =====================================================================

        /// <summary>
        /// Pass centre lines, built one from the other: lane 0 is the reference edge offset by the
        /// first distance, every next lane the previous one offset by the work width. Each line is
        /// rounded to a minimum radius the vehicle can follow.
        /// </summary>
        private class LaneLines
        {
            private readonly List<List<V2>> lines = new List<List<V2>>();
            private readonly List<V2> refLine;
            private readonly double side, width, firstDist, minRadius, spacing;

            public LaneLines(List<V2> refLine, double side, double width, double firstDist, double minRadius, double spacing)
            {
                this.refLine = refLine;
                this.side = side;
                this.width = width;
                this.firstDist = firstDist;
                this.minRadius = minRadius;
                this.spacing = spacing;
            }

            public List<V2> Get(int lane)
            {
                while (lines.Count <= lane)
                {
                    List<V2> prev = lines.Count == 0 ? refLine : lines[lines.Count - 1];
                    double d = lines.Count == 0 ? firstDist : width;
                    lines.Add(Next(prev, d));
                }
                return lines[lane];
            }

            /// <summary>Line a fraction of a width beyond the given lane (for the extra pass).</summary>
            public List<V2> Beyond(int lane, double fraction) => Next(Get(lane), width * fraction);

            private List<V2> Next(List<V2> prev, double d)
            {
                if (prev.Count < 3) return new List<V2>();
                var grid = new SegmentGrid(prev, false, Math.Max(5.0, width));
                List<V2> off = OffsetOpen(prev, d * side, grid, spacing);
                if (off.Count < 3) return off;
                return RoundOpen(off, minRadius, spacing);
            }
        }

        /// <summary>Fast minimum distance from a point to a polyline using a uniform grid.</summary>
        private class SegmentGrid
        {
            private readonly Dictionary<long, List<int>> cells = new Dictionary<long, List<int>>();
            private readonly List<V2> pts;
            private readonly int segCount;
            private readonly double cell;

            public SegmentGrid(List<V2> points, bool closed, double cellSize)
            {
                pts = points;
                cell = cellSize;
                segCount = closed ? pts.Count : pts.Count - 1;
                for (int i = 0; i < segCount; i++)
                {
                    V2 a = pts[i], b = pts[(i + 1) % pts.Count];
                    int x0 = Cell(Math.Min(a.E, b.E)), x1 = Cell(Math.Max(a.E, b.E));
                    int y0 = Cell(Math.Min(a.N, b.N)), y1 = Cell(Math.Max(a.N, b.N));
                    for (int x = x0; x <= x1; x++)
                        for (int y = y0; y <= y1; y++)
                        {
                            long key = Key(x, y);
                            if (!cells.TryGetValue(key, out List<int> list)) cells[key] = list = new List<int>();
                            list.Add(i);
                        }
                }
            }

            private int Cell(double v) => (int)Math.Floor(v / cell);
            private static long Key(int x, int y) => ((long)x << 32) ^ (uint)y;

            /// <summary>Minimum distance, searched within radius (returns radius+ if nothing closer).</summary>
            public double MinDistance(V2 p, double radius)
            {
                double best = double.MaxValue;
                int r = (int)Math.Ceiling(radius / cell);
                int cx = Cell(p.E), cy = Cell(p.N);
                for (int x = cx - r; x <= cx + r; x++)
                    for (int y = cy - r; y <= cy + r; y++)
                    {
                        if (!cells.TryGetValue(Key(x, y), out List<int> list)) continue;
                        foreach (int i in list)
                        {
                            double d = SegDist(p, pts[i], pts[(i + 1) % pts.Count]);
                            if (d < best) best = d;
                        }
                    }
                return best;
            }

            private static double SegDist(V2 p, V2 a, V2 b)
            {
                V2 ab = b - a;
                double l2 = ab.E * ab.E + ab.N * ab.N;
                double t = l2 > 1e-12 ? ((p.E - a.E) * ab.E + (p.N - a.N) * ab.N) / l2 : 0;
                if (t < 0) t = 0; else if (t > 1) t = 1;
                V2 c = a + ab * t;
                return (p - c).Length;
            }
        }

        /// <summary>Point in polygon test with bounding box and per-row edge buckets.</summary>
        private class PolygonTester
        {
            private readonly List<V2> poly;
            private readonly V2 min, max;
            private readonly List<int>[] rows;
            private readonly double rowH;

            public PolygonTester(List<V2> polygon)
            {
                poly = polygon;
                GetBounds(poly, out min, out max);
                int rowCount = Math.Max(1, Math.Min(2000, poly.Count / 4));
                rowH = Math.Max(1e-6, (max.N - min.N) / rowCount);
                rows = new List<int>[rowCount];
                for (int r = 0; r < rowCount; r++) rows[r] = new List<int>();
                for (int i = 0; i < poly.Count; i++)
                {
                    V2 a = poly[i], b = poly[(i + 1) % poly.Count];
                    int r0 = Row(Math.Min(a.N, b.N)), r1 = Row(Math.Max(a.N, b.N));
                    for (int r = r0; r <= r1; r++) rows[r].Add(i);
                }
            }

            private int Row(double n)
            {
                int r = (int)((n - min.N) / rowH);
                return r < 0 ? 0 : (r >= rows.Length ? rows.Length - 1 : r);
            }

            private SegmentGrid clearGrid;
            private double clearance;

            public double Clearance => clearance;

            public List<V2> Polygon => poly;

            /// <summary>Unit direction of the boundary segment nearest to p (polygon order).</summary>
            public V2 EdgeDirection(V2 p)
            {
                double best = double.MaxValue;
                int bi = 0;
                for (int i = 0; i < poly.Count; i++)
                {
                    V2 a = poly[i], b = poly[(i + 1) % poly.Count];
                    V2 ab = b - a;
                    double l2 = ab.E * ab.E + ab.N * ab.N;
                    double t = l2 > 1e-12 ? Math.Max(0, Math.Min(1, ((p.E - a.E) * ab.E + (p.N - a.N) * ab.N) / l2)) : 0;
                    double d = (p - (a + ab * t)).Length;
                    if (d < best) { best = d; bi = i; }
                }
                // a few segments around for a steady direction
                int n = poly.Count;
                return Dir(poly[(bi - 2 + n) % n], poly[(bi + 3) % n]);
            }

            /// <summary>Points must also be this far from the polygon edge to count as clear.</summary>
            public void SetClearance(List<V2> polygon, double distance)
            {
                clearance = distance;
                clearGrid = distance > 0 ? new SegmentGrid(polygon, true, Math.Max(5.0, distance * 2)) : null;
            }

            /// <summary>Distance to the polygon edge, searched within radius (radius+ if farther).</summary>
            public double EdgeDistance(V2 p, double radius)
            {
                if (clearGrid == null) clearGrid = new SegmentGrid(poly, true, 5.0);
                return Math.Min(radius, clearGrid.MinDistance(p, radius));
            }

            private PolygonTester forbidden;

            /// <summary>Area the vehicle must not drive through in turns and transfers.</summary>
            public void SetForbidden(List<V2> polygon) => forbidden = new PolygonTester(polygon);

            /// <summary>Inside, at least the clearance distance from the edge, and not in the forbidden area.</summary>
            public bool Clear(V2 p)
            {
                if (!Inside(p)) return false;
                if (forbidden != null && forbidden.Inside(p)) return false;
                return clearGrid == null || clearGrid.MinDistance(p, clearance) >= clearance;
            }

            public bool Inside(V2 p)
            {
                if (p.E < min.E || p.E > max.E || p.N < min.N || p.N > max.N) return false;
                bool inside = false;
                foreach (int i in rows[Row(p.N)])
                {
                    V2 a = poly[i], b = poly[(i + 1) % poly.Count];
                    if (((a.N > p.N) != (b.N > p.N)) &&
                        (p.E < (b.E - a.E) * (p.N - a.N) / (b.N - a.N) + a.E))
                        inside = !inside;
                }
                return inside;
            }
        }
    }
}
