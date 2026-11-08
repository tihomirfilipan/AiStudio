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
            foreach (var fence in new[] { CurvedField(), LShape() })
            {
                var res = FieldPlanner.Build(fence, new FieldPlanSettings { WorkWidth = 6, TurnRadius = 6 }, fence[0], 0);
                Assert.That(res.Ok, Is.True, res.Error);
                Assert.That(res.Passes, Is.GreaterThan(20));
                Assert.That(res.TurnsLeavingField, Is.LessThanOrEqualTo(3));
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
        public void TooSmallField_ReturnsError()
        {
            var res = FieldPlanner.Build(Rectangle(10, 10), new FieldPlanSettings { WorkWidth = 6, TurnRadius = 6 }, new V2(0, 0), 0);
            Assert.That(res.Ok, Is.False);
            Assert.That(res.Error, Is.Not.Empty);
        }
    }
}
