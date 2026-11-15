using System;
using System.Collections.Generic;

namespace AgOpenGPS.AutoPlan
{
    /// <summary>RACE: conservative forward-only plan with central gores.</summary>
    public static class RacePlanner
    {
        public static FieldPlanResult Build(IList<V2> boundary, FieldPlanSettings input, V2 start, double heading)
        {
            if (input == null || boundary == null || boundary.Count < 3)
                return Fail("RACE: missing settings or boundary");
            if (input.WorkWidth <= 0 || input.TurnRadius <= 0)
                return Fail("RACE: invalid machine parameters");

            FieldPlanSettings settings = input.Clone();
            settings.WedgeField = true;
            settings.StartFromMiddle = true;
            settings.RequireMiddleWedge = true;
            settings.SplitField = false;
            settings.HeadlandReverseTurns = false;
            settings.AvoidWorked = true;
            settings.OrderMode = 2;
            settings.AddExtraPass = false;

            FieldPlanResult result = FieldPlanner.Build(boundary, settings, start, heading);
            if (!result.Ok) return result;
            if (result.PartCount < 2 && (result.Gores == null || result.Gores.Count == 0))
                return Fail("RACE: central wedge geometry was not generated");
            if (result.Points == null || result.Points.Count < 2 ||
                result.TurnsLeavingField != 0 || result.SkippedPieces != 0)
                return Fail("RACE: incomplete plan or turn outside field");

            var polygon = new List<V2>(boundary);
            foreach (PlanPoint p in result.Points)
            {
                if (p.IsReverse) return Fail("RACE: reverse segment detected");
                if (!FieldPlanner.PointInPolygon(polygon, new V2(p.E, p.N)))
                    return Fail("RACE: point outside field");
            }

            for (int i = 1; i + 1 < result.Points.Count; i++)
            {
                PlanPoint a = result.Points[i - 1];
                PlanPoint b = result.Points[i];
                PlanPoint c = result.Points[i + 1];
                double ab = Distance(a, b), bc = Distance(b, c), ac = Distance(a, c);
                if (ab < 0.05 || bc < 0.05 || ac < 0.05) continue;
                double cross = Math.Abs((b.E - a.E) * (c.N - a.N) -
                    (b.N - a.N) * (c.E - a.E));
                if (cross < 1e-8) continue;
                double radius = ab * bc * ac / (2.0 * cross);
                if (radius + 0.05 < settings.TurnRadius)
                    return Fail("RACE: turn radius below minimum");
            }

            result.OrderDescription = "RACE - central gores, forward only";
            return result;
        }

        private static double Distance(PlanPoint a, PlanPoint b)
        {
            double dx = a.E - b.E, dy = a.N - b.N;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static FieldPlanResult Fail(string error)
        {
            return new FieldPlanResult { Ok = false, Error = error };
        }
    }
}
