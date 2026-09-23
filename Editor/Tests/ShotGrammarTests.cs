using System.Collections.Generic;
using NUnit.Framework;

namespace ProjectNova.RecorderKit.Tests
{
    public class ShotGrammarTests
    {
        private sealed class FakeUi : IUiProbe
        {
            public readonly HashSet<string> Names = new();
            public bool Exists(string name) => Names.Contains(name);
            public bool IsInteractable(string name) => Names.Contains(name);
            public string? GetText(string name) => null;
        }

        private sealed class FakeState : IStateProbe
        {
            public string? CurrentStateName { get; set; }
        }

        [Test]
        public void IsCaptured_RequiresSettleCondition()
        {
            var shot = new AdShot("s", new string[0], new[] { AdStep.Wait(1) },
                WaitCondition.Present("RollBTN"));
            var ui = new FakeUi();
            var state = new FakeState();
            Assert.IsFalse(shot.IsCaptured(state, ui));
            ui.Names.Add("RollBTN");
            Assert.IsTrue(shot.IsCaptured(state, ui));
        }

        [Test]
        public void IsCaptured_ChecksExpectStateWhenDeclared()
        {
            var shot = new AdShot("s", new string[0], new[] { AdStep.Wait(1) },
                WaitCondition.Present("RollBTN"), expectState: "BoardState");
            var ui = new FakeUi();
            ui.Names.Add("RollBTN");
            var state = new FakeState { CurrentStateName = "ShopState" };
            Assert.IsFalse(shot.IsCaptured(state, ui));
            state.CurrentStateName = "BoardState";
            Assert.IsTrue(shot.IsCaptured(state, ui));
        }

        /// <summary>A state settle condition has to reach the state probe, or it can never hold.</summary>
        [Test]
        public void IsCaptured_PassesTheStateProbeToAStateSettleCondition()
        {
            var shot = new AdShot("s", new string[0], new[] { AdStep.Wait(1) },
                WaitCondition.State("Game/Idling"));
            var ui = new FakeUi();
            var state = new FakeState { CurrentStateName = "Game/Moving" };
            Assert.IsFalse(shot.IsCaptured(state, ui));
            state.CurrentStateName = "Game/Idling";
            Assert.IsTrue(shot.IsCaptured(state, ui));
        }

        [Test]
        public void SettleTimeout_DefaultsToEightSecondsAndIsPerShot()
        {
            var dflt = new AdShot("s", new string[0], new[] { AdStep.Wait(1) },
                WaitCondition.Present("X"));
            var slow = new AdShot("s", new string[0], new[] { AdStep.Wait(1) },
                WaitCondition.Present("X"), settleTimeout: 45);
            Assert.AreEqual(8, dflt.SettleTimeoutSec);
            Assert.AreEqual(45, slow.SettleTimeoutSec);
        }

        /// <summary>
        /// A shot constrains TWO frames: `arm` where the clip begins, `settle` where it ends. Shots
        /// authored before `arm` existed must keep behaving as before, so the default has to be null —
        /// a non-null default would make the director wait on every shot in every game.
        /// </summary>
        [Test]
        public void ArmCondition_IsNullByDefaultAndIndependentOfSettle()
        {
            var noArm = new AdShot("s", new string[0], new[] { AdStep.Wait(1) },
                WaitCondition.Present("Roster"));
            Assert.IsNull(noArm.ArmCondition);
            Assert.AreEqual(15, noArm.ArmTimeoutSec);

            var armed = new AdShot("s", new string[0], new[] { AdStep.Wait(1) },
                WaitCondition.Present("Roster"),
                arm: WaitCondition.All(WaitCondition.Present("Equip_Tab"),
                                       WaitCondition.Absent("ClickableZone")),
                armTimeout: 25);
            Assert.IsNotNull(armed.ArmCondition);
            Assert.AreEqual(25, armed.ArmTimeoutSec);
            // The arm condition must NOT leak into the captured verdict: a shot is judged on where it
            // ended, and by then the arm's Absent() half may legitimately be false again.
            var ui = new FakeUi();
            ui.Names.Add("Roster");
            ui.Names.Add("ClickableZone");
            Assert.IsTrue(armed.IsCaptured(new FakeState(), ui));
        }

        /// <summary>The arm condition is evaluated against the same probes as any other, so a
        /// destination-anchor-plus-no-overlay pairing has to hold only once the overlay is gone.</summary>
        [Test]
        public void ArmCondition_HoldsOnlyWhenDestinationIsSettled()
        {
            var arm = WaitCondition.All(WaitCondition.Present("Equip_Tab"),
                                        WaitCondition.Absent("ClickableZone"));
            var ui = new FakeUi();
            var state = new FakeState();

            Assert.IsFalse(arm.IsMet(ui, state), "nothing on screen yet — still loading");
            ui.Names.Add("ClickableZone");
            Assert.IsFalse(arm.IsMet(ui, state), "claim overlay up, nav not yet built");
            ui.Names.Add("Equip_Tab");
            Assert.IsFalse(arm.IsMet(ui, state), "nav built but the overlay still covers it");
            ui.Names.Remove("ClickableZone");
            Assert.IsTrue(arm.IsMet(ui, state), "settled Lobby — safe to start recording");
        }

        [Test]
        public void RequiresVision_TrueOnlyWhenShotHasVisionStep()
        {
            var plain = new AdShot("a", new string[0],
                new[] { AdStep.Click("X"), AdStep.Wait(1) }, WaitCondition.Present("X"));
            var vision = new AdShot("b", new string[0],
                new[] { AdStep.Click("X"), AdStep.Vision("the reward chest is open") },
                WaitCondition.Present("X"));
            Assert.IsFalse(plain.RequiresVision);
            Assert.IsTrue(vision.RequiresVision);
        }

        [Test]
        public void VisionStep_CarriesPromptAndDefaultTimeout()
        {
            var step = AdStep.Vision("boss visible on screen");
            Assert.AreEqual(AdStepKind.Vision, step.Kind);
            Assert.AreEqual("boss visible on screen", step.Text);
            Assert.AreEqual(60, step.TimeoutSec);
        }

        /// <summary>
        /// Shots is a PROVIDER, not a snapshot. The whole zero-recompile goal rests on this: a
        /// JSON-backed game must be able to return different shots on a later call within the same
        /// domain, which a plain field assigned once in [InitializeOnLoad] can never do.
        /// </summary>
        [Test]
        public void Shots_IsReEvaluatedOnEveryAccess()
        {
            var calls = 0;
            var adapter = new GameAdapter
            {
                GameId = "testgame",
                Shots = () =>
                {
                    calls++;
                    return new[] { new AdShot($"shot{calls}", new string[0],
                        new[] { AdStep.Wait(1) }, WaitCondition.Present("X")) };
                },
            };

            Assert.AreEqual("shot1", adapter.Shots()[0].Name);
            Assert.AreEqual("shot2", adapter.Shots()[0].Name);
            Assert.AreEqual(2, calls, "Shots must be invoked per access, not cached");
        }

        [Test]
        public void Shots_DefaultsToAnEmptyList()
        {
            Assert.AreEqual(0, new GameAdapter { GameId = "x" }.Shots().Count);
            Assert.AreEqual(0, GameAdapter.Null.Shots().Count);
        }
    }
}
