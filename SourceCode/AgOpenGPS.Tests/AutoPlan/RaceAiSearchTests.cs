using System.Collections.Generic;
using AgOpenGPS.AutoPlan;
using NUnit.Framework;

namespace AgOpenGPS.Tests.AutoPlan
{
    [TestFixture]
    public class RaceAiSearchTests
    {
        private static List<V2> Rectangle()
        {
            return new List<V2>
            {
                new V2(0, 0),
                new V2(60, 0),
                new V2(60, 80),
                new V2(0, 80)
            };
        }

        [Test]
        public void StraightConnection_IsForwardOnly()
        {
            var start = new PlanPoint { E = 30, N = 20, Heading = 0 };
            var goal = new PlanPoint { E = 30, N = 40, Heading = 0 };
            var result = RaceAiSearch.Connect(Rectangle(), start, goal);
            Assert.That(result.Success, Is.True, result.Reason);
            Assert.That(result.Points, Has.Count.GreaterThan(1));
            Assert.That(result.Points, Has.All.Matches<PlanPoint>(p => !p.IsReverse));
        }

        [Test]
        public void OutsideGoal_IsRejected()
        {
            var start = new PlanPoint { E = 30, N = 20, Heading = 0 };
            var goal = new PlanPoint { E = 70, N = 40, Heading = 0 };
            var result = RaceAiSearch.Connect(Rectangle(), start, goal);
            Assert.That(result.Success, Is.False);
        }

        [Test]
        public void InvalidRadius_IsRejected()
        {
            var start = new PlanPoint { E = 30, N = 20, Heading = 0 };
            var goal = new PlanPoint { E = 30, N = 40, Heading = 0 };
            var result = RaceAiSearch.Connect(Rectangle(), start, goal,
                new RaceAiSearch.Options { Radius = 0 });
            Assert.That(result.Success, Is.False);
        }
    }
}
