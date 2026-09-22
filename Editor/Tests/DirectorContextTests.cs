using System.Collections.Generic;
using NUnit.Framework;

namespace ProjectNova.RecorderKit.Tests
{
    public class DirectorContextTests
    {
        private sealed class FakeUi : IUiDriver
        {
            public readonly HashSet<string> Names = new();
            public readonly List<string> Clicked = new();
            public bool ClickResult = true;
            public bool Exists(string name) => Names.Contains(name);
            public bool IsInteractable(string name) => Names.Contains(name);
            public string? GetText(string name) => null;
            public bool Click(string name, int index = 0) { Clicked.Add(name); return ClickResult; }
            public void PointerDown(string name, int index = 0) { }
            public void PointerUp(string name, int index = 0) { }
        }

        private static (DirectorContext ctx, FakeUi ui, System.Func<double> clockRef) Make()
        {
            var ui = new FakeUi();
            var clock = new double[] { 0 };
            var ctx = new DirectorContext(ui, NullStateProbe.Instance, NullCheatBridge.Instance,
                () => clock[0], _ => { });
            // advance the clock a half-second per polled tick so timeouts resolve
            ctx.OnTick = () => clock[0] += 0.5;
            return (ctx, ui, () => clock[0]);
        }

        private static void Drain(DirectorContext ctx, System.Collections.IEnumerable routine)
        {
            foreach (var _ in routine)
                ctx.OnTick?.Invoke();
        }

        [Test]
        public void WaitUntil_SetsLastOpSucceeded_OnPredicateTrue()
        {
            var (ctx, ui, _) = Make();
            ui.Names.Add("RollBTN");
            Drain(ctx, ctx.WaitUntil(() => ui.Exists("RollBTN"), 5));
            Assert.IsTrue(ctx.LastOpSucceeded);
        }

        [Test]
        public void WaitUntil_TimesOut_SetsLastOpFailed()
        {
            var (ctx, _, _) = Make();
            Drain(ctx, ctx.WaitUntil(() => false, 3));
            Assert.IsFalse(ctx.LastOpSucceeded);
        }

        [Test]
        public void ClickUntil_NoCondition_SingleClick_UsesClickResult()
        {
            var (ctx, ui, _) = Make();
            ui.ClickResult = true;
            Drain(ctx, ctx.ClickUntil("Btn", null, 5, 0.5));
            Assert.AreEqual(1, ui.Clicked.Count);
            Assert.IsTrue(ctx.LastOpSucceeded);
        }

        [Test]
        public void ClickUntil_WithCondition_ReclicksUntilMet()
        {
            var (ctx, ui, _) = Make();
            var routine = ctx.ClickUntil("Btn", WaitCondition.Present("Popup"), 10, 0.5);
            var n = 0;
            foreach (var _ in routine)
            {
                ctx.OnTick?.Invoke();
                if (++n == 4) ui.Names.Add("Popup"); // condition becomes true mid-run
            }
            Assert.Greater(ui.Clicked.Count, 1);
            Assert.IsTrue(ctx.LastOpSucceeded);
        }

        [Test]
        public void ClickUntil_WithCondition_TimesOut()
        {
            var (ctx, ui, _) = Make();
            Drain(ctx, ctx.ClickUntil("Btn", WaitCondition.Present("Never"), 3, 0.5));
            Assert.IsFalse(ctx.LastOpSucceeded);
        }

        /// <summary>
        /// The twelfth audit, S1: a wait of no time at all still hands the editor back ONCE. Before, a wait whose deadline
        /// had already passed yielded nothing, so a loop that waited through it (a cheatUntil with retryEvery 0) ran its
        /// whole timeout inside one editor tick. On a clock that never moves, so the one yield is the wait's own.
        /// </summary>
        [TestCase(0.0)]
        [TestCase(-1.0)]
        [TestCase(double.NaN)]
        public void WaitSeconds_OfNoTimeOrLessOrNaN_YieldsExactlyOnce(double seconds)
        {
            var ctx = new DirectorContext(new FakeUi(), NullStateProbe.Instance, NullCheatBridge.Instance, () => 7, _ => { });
            var yields = 0;
            foreach (var _ in ctx.WaitSeconds(seconds))
                if (++yields > 10) break;
            Assert.AreEqual(1, yields, $"WaitSeconds({seconds}) must hand the editor back exactly once");
        }

        /// <summary>CONTROL: a wait of real length yields as many times as it did before — once per tick until the deadline
        /// (1 s at half a second a tick is two ticks; 1.5 s at an eighth of a second, exact in binary, is twelve).</summary>
        [TestCase(1.0, 0.5, 2)]
        [TestCase(1.5, 0.125, 12)]
        public void WaitSeconds_OfRealLength_YieldsOncePerTickUntilTheDeadline(double seconds, double tick, int expected)
        {
            var clock = new double[] { 0 };
            var ctx = new DirectorContext(new FakeUi(), NullStateProbe.Instance, NullCheatBridge.Instance, () => clock[0], _ => { });
            var yields = 0;
            foreach (var _ in ctx.WaitSeconds(seconds))
            {
                clock[0] += tick;
                if (++yields > 1000) break;
            }
            Assert.AreEqual(expected, yields);
        }

        /// <summary>
        /// The thirteenth audit, S2 — WaitUntil YIELDS BEFORE EACH EVALUATION OF ITS CONDITION, THE FIRST INCLUDED, and
        /// evaluates it once per yield. It used to evaluate the condition before any yield and then AGAIN for its verdict, so
        /// an already-met condition cost no pump at all — a step list of them ran whole inside one editor tick (1,091 of them:
        /// 981 ms at 3,000 UI nodes) — and each exit paid for two evaluations. On a clock that never moves.
        /// </summary>
        [Test]
        public void WaitUntil_AnAlreadyMetConditionCostsOnePumpAndOneEvaluation()
        {
            var ctx = new DirectorContext(new FakeUi(), NullStateProbe.Instance, NullCheatBridge.Instance, () => 7, _ => { });
            var evaluations = 0;
            var yields = 0;
            foreach (var _ in ctx.WaitUntil(() => { evaluations++; return true; }, 5))
                if (++yields > 10) break;
            Assert.AreEqual(1, yields, "an already-met condition must cost one pump, never zero");
            Assert.AreEqual(1, evaluations, "and be evaluated once, not again for the verdict");
            Assert.IsTrue(ctx.LastOpSucceeded);
        }

        /// <summary>The same rule over a wait that takes several ticks, both ways out: each evaluation comes after its own
        /// yield (the Nth evaluation after the Nth yield), so no pump ever evaluates the condition twice. A half-second tick.</summary>
        [TestCase(true)]
        [TestCase(false)]
        public void WaitUntil_EveryEvaluationComesAfterItsOwnYield(bool eventuallyMet)
        {
            var clock = new double[] { 0 };
            var ctx = new DirectorContext(new FakeUi(), NullStateProbe.Instance, NullCheatBridge.Instance, () => clock[0], _ => { });
            var yields = 0;
            var evaluatedAt = new List<int>();
            foreach (var _ in ctx.WaitUntil(() => { evaluatedAt.Add(yields); return eventuallyMet && evaluatedAt.Count == 4; }, 3))
            {
                clock[0] += 0.5;
                if (++yields > 100) break;
            }
            // met: evaluated after yields 1..4; never met: after yields 1..6, the sixth at the 3 s deadline
            var expected = eventuallyMet ? new[] { 1, 2, 3, 4 } : new[] { 1, 2, 3, 4, 5, 6 };
            CollectionAssert.AreEqual(expected, evaluatedAt, "evaluated after yields [" + string.Join(", ", evaluatedAt) + "]");
            Assert.AreEqual(eventuallyMet, ctx.LastOpSucceeded);
        }
    }
}
