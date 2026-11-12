using System;
using System.Collections.Generic;
using AgOpenGPS.AutoPlan;
using NUnit.Framework;

namespace AgOpenGPS.Tests.AutoPlan
{
    public class FieldPlannerTests
    {
        private static List<V2> Rectangle(double w, double h) =>
            new List<V2> { new V2(0, 0), new V2(w, 0), new V2(w, h), new V2(0, h) };

        private static List<V2> LShape() =>
            new List<V2> { new V2(0, 0), new V2(250, 0), new V2(250, 100), new V2(100, 100), new V2(100, 250), new V2(0, 250) };

        private static List<V2> CurvedField()
        {
            var f = new List<V2>();
            for (int i = 0; i <= 60; i++) { double x = i * 5; f.Add(new V2(x, 30 * Math.Sin(x / 300 * Math.PI))); }
            f.Add(new V2(300, 220)); f.Add(new V2(150, 260)); f.Add(new V2(0, 200));
            return f;
        }

        [Test]
        public void DubinsPath_EndsAtGoal()
        {
            var rnd = new Random(1);
            for (int i = 0; i < 500; i++)
            {
                var p0 = new V2(rnd.NextDouble() * 40 - 20, rnd.NextDouble() * 40 - 20);
                var p1 = new V2(rnd.NextDouble() * 40 - 20, rnd.NextDouble() * 40 - 20);
                double h0 = rnd.NextDouble() * 6.28, h1 = rnd.NextDouble() * 6.28;
                var path = FieldPlanner.DubinsPath(p0, h0, p1, h1, 6, 0.5);
                Assert.That(path, Is.Not.Null);
                Assert.That((path[path.Count - 1] - p1).Length, Is.LessThan(0.01));
            }
        }

        [Test]
        public void DubinsLength_UTurnIsHalfCircle()
        {
            double len = FieldPlanner.DubinsLength(new V2(0, 0), 0, new V2(12, 0), Math.PI, 6);
            Assert.That(len, Is.EqualTo(Math.PI * 6).Within(0.01));
        }

        [TestCase(3.0, true)]
        [TestCase(6.0, true)]
        [TestCase(12.0, true)]
        [TestCase(3.0, false)]
        [TestCase(6.0, false)]
        [TestCase(12.0, false)]
        public void Rectangle_PlanCoversFieldAndStaysInside(double width, bool headlandAllAround)
        {
            var fence = Rectangle(300, 200);
            var set = new FieldPlanSettings { WorkWidth = width, TurnRadius = 6, HeadlandOnLongSides = headlandAllAround };
            var res = FieldPlanner.Build(fence, set, new V2(-10, -10), 0);

            Assert.That(res.Ok, Is.True, res.Error);
            // Headland only at the ends: the outermost passes run along the long sides, a turn
            // next to a side can bulge out a little; the app warns about these.
            Assert.That(res.TurnsLeavingField, Is.LessThanOrEqualTo(headlandAllAround ? 0 : 2));
            Assert.That(res.Turns, Is.EqualTo(res.Passes - 1));
            Assert.That(res.HeadlandLaps, Is.GreaterThanOrEqualTo(1));

            // Passes plus headland must cover roughly the field area
            double covered = res.WorkLength * width;
            Assert.That(covered, Is.GreaterThan(300 * 200 * 0.85));

            if (headlandAllAround)
                foreach (PlanPoint p in res.Points)
                    Assert.That(FieldPlanner.PointInPolygon(fence, new V2(p.E, p.N)), Is.True);
        }

        [Test]
        public void EndsOnlyHeadland_PassesReachTheLongSides()
        {
            // 300 m long passes (east-west); the long sides are south and north
            var res = FieldPlanner.Build(Rectangle(300, 200), new FieldPlanSettings { WorkWidth = 6, TurnRadius = 6 }, new V2(-10, -10), 0);
            Assert.That(res.Ok, Is.True, res.Error);

            double minN = double.MaxValue, maxN = double.MinValue;
            foreach (PlanPoint p in res.Points)
            {
                if (!p.IsWork || p.IsHeadland) continue;
                minN = Math.Min(minN, p.N);
                maxN = Math.Max(maxN, p.N);
            }
            // first pass at half a tool width from the near long side; 200 m is not a multiple of
            // 6 m, so an extra overlapping pass puts the tool edge on the far side too
            Assert.That(minN, Is.EqualTo(3.0).Within(1.0));
            Assert.That(res.ExtraSidePass, Is.True);
            Assert.That(maxN, Is.EqualTo(197.0).Within(1.0));
        }

        [Test]
        public void CurvedAndLShapedFields_PlanSucceeds()
        {
            // TurnsLeavingField counts turns that bring the tool (half a width) over the boundary.
            // The concave L shape always has a few transfers passing close to its inner corner.
            var cases = new[] { Tuple.Create(CurvedField(), 1), Tuple.Create(LShape(), 5) };
            foreach (var c in cases)
            {
                var fence = c.Item1;
                var res = FieldPlanner.Build(fence, new FieldPlanSettings { WorkWidth = 6, TurnRadius = 6 }, fence[0], 0);
                Assert.That(res.Ok, Is.True, res.Error);
                Assert.That(res.Passes, Is.GreaterThan(20));
                Assert.That(res.TurnsLeavingField, Is.LessThanOrEqualTo(c.Item2));
            }
        }

        [Test]
        public void Turns_AreNotTighterThanTurnRadius()
        {
            var res = FieldPlanner.Build(CurvedField(), new FieldPlanSettings { WorkWidth = 6, TurnRadius = 6 }, new V2(0, 0), 0);
            Assert.That(res.Ok, Is.True, res.Error);

            var pts = res.Points;
            for (int i = 2; i < pts.Count - 2; i++)
            {
                double d = Math.Sqrt(Math.Pow(pts[i + 2].E - pts[i - 2].E, 2) + Math.Pow(pts[i + 2].N - pts[i - 2].N, 2));
                if (d < 0.5) continue;
                double k = Math.Abs(FieldPlanner.AngleDiff(pts[i + 2].Heading, pts[i - 2].Heading)) / d;
                Assert.That(k, Is.LessThan(1.25 / 6.0), $"point {i}");
            }
        }

        [Test]
        public void RunLabels_NumberPassesInDrivingOrderAndHeadlandLaps()
        {
            var res = FieldPlanner.Build(Rectangle(300, 200), new FieldPlanSettings { WorkWidth = 6, TurnRadius = 6 }, new V2(-10, -10), 0);
            Assert.That(res.Ok, Is.True, res.Error);

            var labels = FieldPlanner.GetRunLabels(res.Points);
            int passLabels = 0, lapLabels = 0;
            foreach (RunLabel l in labels)
            {
                Assert.That(l.StartIndex, Is.LessThan(l.EndIndex));
                Assert.That(res.Points[l.StartIndex].IsWork && res.Points[l.EndIndex].IsWork, Is.True);
                if (l.IsHeadland) { lapLabels++; Assert.That(l.Text, Is.EqualTo("U" + lapLabels)); }
                else { passLabels++; Assert.That(l.Text, Is.EqualTo(passLabels.ToString())); }
                // label in the middle of the field length (passes run east-west)
                if (!l.IsHeadland) Assert.That(l.E, Is.InRange(120.0, 180.0));
            }
            Assert.That(passLabels, Is.EqualTo(res.Passes));
            Assert.That(lapLabels, Is.EqualTo(res.HeadlandLaps));
        }

        [Test]
        public void Options_NoHeadlandWorkAndNoExtraPass()
        {
            var set = new FieldPlanSettings { WorkWidth = 6, TurnRadius = 6, WorkHeadland = false, AddExtraPass = false };
            var res = FieldPlanner.Build(Rectangle(300, 200), set, new V2(-10, -10), 0);
            Assert.That(res.Ok, Is.True, res.Error);
            Assert.That(res.HeadlandLaps, Is.EqualTo(0));
            Assert.That(res.ExtraSidePass, Is.False);
            foreach (PlanPoint p in res.Points) Assert.That(p.IsHeadland, Is.False);
            // the path ends with the last pass
            Assert.That(res.Points[res.Points.Count - 1].IsWork, Is.True);
        }

        [Test]
        public void PickedEdge_PassesFollowIt()
        {
            // pick the short west side: passes then run north-south
            var edge = new List<V2> { new V2(0, 0), new V2(0, 200) };
            var set = new FieldPlanSettings { WorkWidth = 6, TurnRadius = 6, ReferenceEdge = edge };
            var res = FieldPlanner.Build(Rectangle(300, 200), set, new V2(-10, -10), 0);
            Assert.That(res.Ok, Is.True, res.Error);
            Assert.That(res.Passes, Is.InRange(48, 52)); // 300 m / 6 m

            // the middle point of pass 1 lies near the west side
            var labels = FieldPlanner.GetRunLabels(res.Points);
            Assert.That(labels[0].E, Is.LessThan(10.0));
        }

        [Test]
        public void TrackLine_IsFirstPassAcrossTheField()
        {
            var res = FieldPlanner.Build(Rectangle(300, 200), new FieldPlanSettings { WorkWidth = 6, TurnRadius = 6 }, new V2(-10, -10), 0);
            Assert.That(res.Ok, Is.True, res.Error);
            Assert.That(res.TrackLine.Count, Is.GreaterThan(100));
            double minE = double.MaxValue, maxE = double.MinValue;
            foreach (V2 p in res.TrackLine)
            {
                Assert.That(p.N, Is.EqualTo(3.0).Within(0.5)); // half a width from the south side
                minE = Math.Min(minE, p.E);
                maxE = Math.Max(maxE, p.E);
            }
            // whole field length, not cut at the headland
            Assert.That(minE, Is.LessThan(5.0));
            Assert.That(maxE, Is.GreaterThan(295.0));
        }

        [Test]
        public void PickedEdgeWithCorner_PassesHaveNoKinks()
        {
            // bottom edge straight for 150 m, then turning 35 degrees; picked across the corner
            double c = Math.Cos(35 * Math.PI / 180), sn = Math.Sin(35 * Math.PI / 180);
            var fence = new List<V2>
            {
                new V2(0, 0), new V2(150, 0), new V2(150 + 150 * c, 150 * sn),
                new V2(150 + 150 * c - 200 * sn, 150 * sn + 200 * c), new V2(0, 220)
            };
            var set = new FieldPlanSettings
            {
                WorkWidth = 6,
                TurnRadius = 6,
                ReferenceEdge = new List<V2> { fence[0], fence[1], fence[2] }
            };
            var res = FieldPlanner.Build(fence, set, new V2(0, 0), 0);
            Assert.That(res.Ok, Is.True, res.Error);

            // inside the passes no bend tighter than the minimum pass radius (1.5 x 6 m, at least 10 m)
            var p = res.Points;
            for (int i = 3; i < p.Count - 3; i++)
            {
                if (!p[i - 3].IsWork || !p[i + 3].IsWork || p[i].IsHeadland) continue;
                double d = Math.Sqrt(Math.Pow(p[i + 3].E - p[i - 3].E, 2) + Math.Pow(p[i + 3].N - p[i - 3].N, 2));
                if (d < 1) continue;
                double k = Math.Abs(FieldPlanner.AngleDiff(p[i + 3].Heading, p[i - 3].Heading)) / d;
                Assert.That(k, Is.LessThan(1.0 / 8.0), $"kink at point {i}");
            }
        }

        [Test]
        public void Turns_KeepTheToolInsideTheField()
        {
            // rectangle, headland only at the ends: every turn and transfer point at least half a
            // tool width (3 m, small tolerance) from the boundary
            var fence = Rectangle(300, 200);
            var res = FieldPlanner.Build(fence, new FieldPlanSettings { WorkWidth = 6, TurnRadius = 6, WorkHeadland = true }, new V2(-10, -10), 0);
            Assert.That(res.Ok, Is.True, res.Error);
            Assert.That(res.TurnsLeavingField, Is.EqualTo(0));
            foreach (PlanPoint p in res.Points)
            {
                if (p.IsWork) continue;
                double d = Math.Min(Math.Min(p.E, 300 - p.E), Math.Min(p.N, 200 - p.N));
                Assert.That(d, Is.GreaterThan(2.6), $"turn point ({p.E:F1},{p.N:F1})");
            }
        }

        [Test]
        public void ShiftWorkFlags_FollowsTheToolBehindThePivot()
        {
            // straight line 0..100 m, work from 20 m to 60 m
            var pts = new List<PlanPoint>();
            for (int i = 0; i <= 100; i++) pts.Add(new PlanPoint { E = 0, N = i, IsWork = i >= 20 && i <= 60 });

            var shifted = FieldPlanner.ShiftWorkFlags(pts, 5.0);
            // tool 5 m behind: sections on when the pivot is at 25 m, off after 65 m
            Assert.That(shifted[24].IsWork, Is.False);
            Assert.That(shifted[25].IsWork, Is.True);
            Assert.That(shifted[65].IsWork, Is.True);
            Assert.That(shifted[66].IsWork, Is.False);

            var front = FieldPlanner.ShiftWorkFlags(pts, -3.0);
            // tool 3 m in front: on at 17 m, off after 57 m
            Assert.That(front[16].IsWork, Is.False);
            Assert.That(front[17].IsWork, Is.True);
            Assert.That(front[57].IsWork, Is.True);
            Assert.That(front[58].IsWork, Is.False);
        }

        [Test]
        public void LongTransfers_DriveAlongTheBoundaryNotAcrossTheField()
        {
            // strip along the bottom with two legs going up (like a real field): moving from the
            // bottom strip to a leg must use the boundary drive line, not a straight line across
            // worked passes
            var fence = new List<V2>
            {
                new V2(0, 0), new V2(330, 0), new V2(330, 40), new V2(190, 40), new V2(190, 200),
                new V2(110, 200), new V2(110, 40), new V2(95, 40), new V2(95, 200), new V2(70, 200),
                new V2(70, 40), new V2(0, 40)
            };
            var res = FieldPlanner.Build(fence, new FieldPlanSettings { WorkWidth = 6, TurnRadius = 6, WorkHeadland = true }, new V2(-10, -10), 0);
            Assert.That(res.Ok, Is.True, res.Error);

            // driving along a pass line is fine, crossing the worked passes of the bottom strip on
            // the way is not (joining and leaving a pass at both ends of a transfer is allowed)
            var pts = res.Points;
            int worst = 0;
            for (int i = 0; i < pts.Count;)
            {
                int j = i;
                while (j < pts.Count && pts[j].IsWork == pts[i].IsWork) j++;
                if (!pts[i].IsWork)
                {
                    int crossings = 0;
                    for (int k = i + 1; k < j; k++)
                    {
                        PlanPoint a = pts[k - 1], b = pts[k];
                        if (a.E < 30 || a.E > 300) continue;   // headland at the strip ends
                        double fromStart = Math.Sqrt(Math.Pow(a.E - pts[i].E, 2) + Math.Pow(a.N - pts[i].N, 2));
                        double toEnd = Math.Sqrt(Math.Pow(a.E - pts[j - 1].E, 2) + Math.Pow(a.N - pts[j - 1].N, 2));
                        if (fromStart < 20 || toEnd < 20) continue;
                        for (int lane = 0; lane <= 6; lane++)
                        {
                            double y = 3 + 6 * lane;
                            if ((a.N - y) * (b.N - y) < 0) crossings++;
                        }
                    }
                    worst = Math.Max(worst, crossings);
                }
                i = j;
            }
            Assert.That(worst, Is.LessThanOrEqualTo(1), "a transfer cuts across the worked bottom strip");
        }

        [Test]
        public void SlantedEnds_TurnsWithoutLoops()
        {
            // both ends slanted (39 and 63 degrees): every turn between passes is about half a
            // circle (part turn, straight along the slant, part turn), never a loop
            var fence = new List<V2> { new V2(0, 0), new V2(400, 0), new V2(250, 120), new V2(60, 120) };
            var res = FieldPlanner.Build(fence, new FieldPlanSettings { WorkWidth = 6, TurnRadius = 6, WorkHeadland = false }, new V2(-10, -10), 0);
            Assert.That(res.Ok, Is.True, res.Error);
            Assert.That(res.TurnsLeavingField, Is.EqualTo(0));

            var pts = res.Points;
            for (int i = 1; i < pts.Count;)
            {
                int j = i;
                while (j < pts.Count && pts[j].IsWork == pts[i].IsWork) j++;
                if (!pts[i].IsWork)
                {
                    double rotation = 0;
                    for (int k = i + 1; k < j; k++) rotation += Math.Abs(FieldPlanner.AngleDiff(pts[k].Heading, pts[k - 1].Heading));
                    Assert.That(rotation, Is.LessThan(1.3 * Math.PI), $"loop turn at ({pts[i].E:F0},{pts[i].N:F0})");
                }
                i = j;
            }
        }

        [Test]
        public void TooSmallField_ReturnsError()
        {
            var res = FieldPlanner.Build(Rectangle(10, 10), new FieldPlanSettings { WorkWidth = 6, TurnRadius = 6 }, new V2(0, 0), 0);
            Assert.That(res.Ok, Is.False);
            Assert.That(res.Error, Is.Not.Empty);
        }
    }
}
