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
        public List<V2> ReferenceEdge = new List<V2>();

        /// <summary>Centre line of the first pass across the whole field, for a guidance track.</summary>
        public List<V2> TrackLine = new List<V2>();
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
                BuildInternal(fence, set, startPos, startHeading, res);
            }
            catch (Exception ex)
            {
                res.Ok = false;
                res.Error = ex.Message;
            }
            return res;
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
            if (laps <= 0) laps = Math.Max(1, (int)Math.Ceiling((R + set.TurnMargin) / W));
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
                    work = InsetEnds(fence, endRunId, headWidth, s);
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
            List<Step> order = ChooseOrder(passes, R, W, fenceGrid, startPos, startHeading, s, out string orderName);
            res.OrderDescription = orderName;

            var path = new List<PlanPoint>();
            for (int i = 0; i < order.Count; i++)
            {
                Step st = order[i];
                List<V2> pts = st.Pass.Pts;
                if (st.Reverse) { pts = new List<V2>(pts); pts.Reverse(); }

                if (i > 0)
                {
                    LastPose(path, s, out V2 lastP, out double lastH);
                    var turn = DubinsPath(lastP, lastH, pts[0], HeadingOf(pts[0], pts[Math.Min(3, pts.Count - 1)]), R, s);
                    if (turn == null) { res.Error = "Turn could not be built"; return; }
                    if (CountOutside(turn, fenceGrid) > 0) res.TurnsLeavingField++;
                    AppendPoints(path, turn, false, true);
                    res.Turns++;
                }
                AppendPoints(path, pts, true, i > 0);
            }
            res.Passes = order.Count;

            // --- Headland: strips at the pass ends, or laps all around ---
            if (set.WorkHeadland && endsOnly)
            {
                BuildEndStrips(fence, endRunId, laps, W, R, s, fenceGrid, path, res);
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

                    var conn = DubinsPath(cur, curH, loop[0], HeadingOf(loop[0], loop[Math.Min(3, loop.Count - 1)]), R, s);
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
        /// Work area with the headland only at the ends: end points are moved inwards by the
        /// headland width, long side points stay on the boundary.
        /// </summary>
        private static List<V2> InsetEnds(List<V2> loop, int[] endRunId, double dist, double s)
        {
            int n = loop.Count;

            // Side points inside the headland band of an end belong to the end
            var endPts = new List<V2>();
            for (int i = 0; i < n; i++) if (endRunId[i] >= 0) endPts.Add(loop[i]);
            var isEnd = new bool[n];
            double d2 = dist * dist;
            for (int i = 0; i < n; i++)
            {
                if (endRunId[i] >= 0) { isEnd[i] = true; continue; }
                foreach (V2 e in endPts)
                    if (Sq(e.E - loop[i].E) + Sq(e.N - loop[i].N) < d2) { isEnd[i] = true; break; }
            }

            var grid = new SegmentGrid(loop, true, Math.Max(5.0, dist));
            var tester = new PolygonTester(loop);
            var r = new List<V2>(n);
            for (int i = 0; i < n; i++)
            {
                if (!isEnd[i]) { r.Add(loop[i]); continue; }
                V2 d = Dir(loop[(i - 1 + n) % n], loop[(i + 1) % n]);
                V2 p = loop[i] + new V2(-d.N, d.E) * dist;
                if (grid.MinDistance(p, dist) < dist * 0.98) continue;
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
            PolygonTester fenceGrid, List<PlanPoint> path, FieldPlanResult res)
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
                        var conn = DubinsPath(cp, ch, pts[0], HeadingOf(pts[0], pts[Math.Min(3, pts.Count - 1)]), R, s);
                        if (conn == null) continue;
                        double cost = OpenLength(conn) + 1000.0 * CountOutside(conn, fenceGrid);
                        if (cost < bestCost) { bestCost = cost; best = pts; bestConn = conn; }
                    }
                    if (best == null) continue;
                    if (CountOutside(bestConn, fenceGrid) > 0) res.TurnsLeavingField++;
                    AppendPoints(path, bestConn, false, true);
                    AppendPoints(path, best, true, true, true);
                    res.HeadlandLaps++;
                }
            }
        }

        private static void AddStrip(Dictionary<int, List<List<V2>>> groups, int id, List<V2> pts, double W, double R, double s, PolygonTester fence)
        {
            if (pts.Count < 3) return;

            // Stop short of the long sides so the turn to the next strip fits inside the field
            int trim = (int)Math.Ceiling((R + 3.0) / s);
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
            V2 startPos, double startHeading, double s, out string name)
        {
            double[] weights = { 0.0, 0.25, 0.5, 1.0, 2.0, 1e6 };
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
                        for (int r = 0; r < 2; r++)
                        {
                            EntryPose(ps, r == 1, out V2 p, out double h);
                            double len = DubinsLength(ep, eh, p, h, R);
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
                        var turn = DubinsPath(ep, eh, p, h, R, Math.Max(2.0, s));
                        int outside = turn == null ? 1000 : CountOutside(turn, fence);
                        double cost = cands[c].Key + 1000.0 * outside;
                        if (cost < pickCost) { pickCost = cost; pick = cand; }
                        if (outside == 0) break;
                    }
                    if (pick == null) break;
                    plan.Add(pick);
                    used.Add(pick.Pass);
                }

                // Score: real turn lengths plus penalty for leaving the field
                for (int i = 1; i < plan.Count; i++)
                {
                    ExitPose(plan[i - 1], out V2 ep, out double eh);
                    EntryPose(plan[i].Pass, plan[i].Reverse, out V2 p, out double h);
                    var turn = DubinsPath(ep, eh, p, h, R, Math.Max(2.0, s));
                    if (turn == null) { score += 1e6; continue; }
                    score += OpenLength(turn) + 1000.0 * CountOutside(turn, fence);
                }

                if (score < bestScore)
                {
                    bestScore = score;
                    bestPlan = plan;
                    name = w >= 1e5 ? "neighbour passes" : (w == 0 ? "shortest turns" : "skip passes, weight " + w.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            return bestPlan;
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
            foreach (V2 p in pts) if (!fence.Inside(p)) n++;
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
                PlanPoint a = r[Math.Max(0, i - 1)], b = r[Math.Min(r.Count - 1, i + 1)];
                PlanPoint p = r[i];
                p.Heading = HeadingOf(new V2(a.E, a.N), new V2(b.E, b.N));
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
