using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;

namespace ProjectNova.RecorderKit.Tests
{
    public class InterruptSweepTests
    {
        private sealed class FakeUi : IUiDriver
        {
            public readonly HashSet<string> Names = new();
            public bool Exists(string name) => Names.Contains(name);
            public bool IsInteractable(string name) => Names.Contains(name);
            public string? GetText(string name) => null;
            public bool Click(string name, int index = 0) => Names.Contains(name);
            public void PointerDown(string name, int index = 0) { }
            public void PointerUp(string name, int index = 0) { }
        }

        private sealed class FakeState : IStateProbe
        {
            public string? CurrentStateName { get; set; }
        }

        private double _clock;
        private DirectorContext _ctx = null!;
        private FakeUi _ui = null!;
        private FakeState _state = null!;

        [SetUp]
        public void SetUp()
        {
            _clock = 0;
            _ui = new FakeUi();
            _state = new FakeState();
            _ctx = new DirectorContext(_ui, _state, NullCheatBridge.Instance, () => _clock, _ => { })
            {
                OnTick = () => _clock += 0.1,
            };
        }

        private void PumpToEnd(IEnumerable routine, int maxTicks = 100_000)
        {
            var ticks = 0;
            foreach (var _ in routine)
            {
                _clock += 0.1;
                if (ticks++ > maxTicks) Assert.Fail("sweep never finished");
            }
        }

        private static (Func<DirectorContext, bool>, Func<DirectorContext, IEnumerable>) Branch(
            Func<DirectorContext, bool> when, Action<DirectorContext> clear) =>
            (when, ctx => { clear(ctx); return System.Linq.Enumerable.Empty<object>(); });

        /// <summary>The first matching branch wins and no later branch runs that tick — every real
        /// policy is an if/else-if chain, and that priority IS the design.</summary>
        [Test]
        public void RunsOnlyTheFirstMatchingBranchPerTick()
        {
            var order = new List<string>();
            _ui.Names.Add("A");
            _ui.Names.Add("B");
            var done = false;

            var branches = new[]
            {
                Branch(c => c.Ui.Exists("A"), _ => { order.Add("A"); _ui.Names.Remove("A"); }),
                Branch(c => c.Ui.Exists("B"), _ => { order.Add("B"); _ui.Names.Remove("B"); done = true; }),
            };

            PumpToEnd(InterruptSweep.Run(_ctx, branches, _ => done, deadline: 100));

            CollectionAssert.AreEqual(new[] { "A", "B" }, order);
            Assert.IsTrue(_ctx.LastOpSucceeded);
        }

        /// <summary>
        /// A clear action must be able to WAIT. RlRecoveryPolicy's MOVING branch waits out a transient
        /// state and its auto-resolve branch waits up to 25s for idle; GemRecoveryPolicy's scene reload
        /// waits 2s. A non-yielding Action cannot express any of them.
        /// </summary>
        [Test]
        public void ABranchCanYieldWhileClearing()
        {
            var cleared = false;
            var branches = new[]
            {
                ((Func<DirectorContext, bool>)(_ => !cleared),
                 (Func<DirectorContext, IEnumerable>)(c => WaitThenSet(c, () => cleared = true))),
            };

            PumpToEnd(InterruptSweep.Run(_ctx, branches, _ => cleared, deadline: 100));

            Assert.IsTrue(cleared);
            Assert.GreaterOrEqual(_clock, 2.0, "the branch's own wait must have elapsed");
        }

        private static IEnumerable WaitThenSet(DirectorContext ctx, Action set)
        {
            foreach (var _ in ctx.WaitSeconds(2)) yield return null;
            set();
        }

        /// <summary>Verify is its own predicate, NOT the negation of the branch list.
        /// RlRecoveryPolicy checks RESUME_ABANDON while its branch clicks RESUME_CONFIRM, and omits
        /// several branch conditions entirely — deriving verify from branches would break it.</summary>
        [Test]
        public void VerifyIsIndependentOfTheBranchConditions()
        {
            _ui.Names.Add("Popup");
            var branches = new[]
            {
                Branch(c => c.Ui.Exists("Popup"), _ => { _ui.Names.Remove("Popup"); _ui.Names.Add("Board"); }),
            };

            PumpToEnd(InterruptSweep.Run(_ctx, branches, c => c.Ui.Exists("Board"), deadline: 100));

            Assert.IsTrue(_ctx.LastOpSucceeded);
        }

        [Test]
        public void ExitsImmediatelyWhenAlreadyVerified()
        {
            var ran = false;
            var branches = new[] { Branch(_ => true, _ => ran = true) };

            PumpToEnd(InterruptSweep.Run(_ctx, branches, _ => true, deadline: 100));

            Assert.IsFalse(ran, "no branch should run when verify already holds");
            Assert.IsTrue(_ctx.LastOpSucceeded);
        }

        [Test]
        public void GivesUpAtTheDeadlineAndReportsFailure()
        {
            var attempts = 0;
            var branches = new[] { Branch(_ => true, _ => attempts++) };

            PumpToEnd(InterruptSweep.Run(_ctx, branches, _ => false, deadline: 3));

            Assert.IsFalse(_ctx.LastOpSucceeded);
            Assert.Greater(attempts, 0);
            Assert.Less(_clock, 10, "must stop near the deadline, not run forever");
        }

        /// <summary>A branch that matches nothing must not stall the loop — it just waits and retries,
        /// which is how a policy sits out a transient state it has no branch for.</summary>
        [Test]
        public void NoMatchingBranch_StillPollsUntilVerifiedOrDeadline()
        {
            var ticks = 0;
            var branches = new[] { Branch(_ => false, _ => { }) };

            PumpToEnd(InterruptSweep.Run(_ctx, branches, _ => ++ticks >= 3, deadline: 100));

            Assert.IsTrue(_ctx.LastOpSucceeded);
        }

        /// <summary>Once-only semantics stay the CALLER's business, via an ordinary captured bool —
        /// exactly how RlRecoveryPolicy's triedResolve and GemRecoveryPolicy's reloaded already work.
        /// The primitive deliberately knows nothing about it.</summary>
        [Test]
        public void ACallerCanMakeABranchFireOnce()
        {
            var fired = 0;
            var tried = false;
            var branches = new[]
            {
                Branch(_ => !tried, _ => { tried = true; fired++; }),
            };

            PumpToEnd(InterruptSweep.Run(_ctx, branches, _ => false, deadline: 5));

            Assert.AreEqual(1, fired);
        }
    }
}
