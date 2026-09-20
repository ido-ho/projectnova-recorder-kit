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
    }
}
