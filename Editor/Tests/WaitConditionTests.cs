using System.Collections.Generic;
using NUnit.Framework;

namespace ProjectNova.RecorderKit.Tests
{
    public class WaitConditionTests
    {
        private sealed class FakeProbe : IUiProbe
        {
            public readonly HashSet<string> Names = new();
            public readonly Dictionary<string, string> Texts = new();
            public bool Exists(string name) => Names.Contains(name);
            public readonly HashSet<string> Locked = new();
            public bool IsInteractable(string name) => Names.Contains(name) && !Locked.Contains(name);
            public string? GetText(string name) => Texts.TryGetValue(name, out var t) ? t : null;
        }

        [Test]
        public void Present_MetOnlyWhenNameExists()
        {
            var p = new FakeProbe();
            Assert.IsFalse(WaitCondition.Present("RollBTN").IsMet(p));
            p.Names.Add("RollBTN");
            Assert.IsTrue(WaitCondition.Present("RollBTN").IsMet(p));
        }

        [Test]
        public void Absent_MetOnlyWhenNameMissing()
        {
            var p = new FakeProbe();
            Assert.IsTrue(WaitCondition.Absent("Popup").IsMet(p));
            p.Names.Add("Popup");
            Assert.IsFalse(WaitCondition.Absent("Popup").IsMet(p));
        }

        [Test]
        public void TextContains_IsCaseInsensitive()
        {
            var p = new FakeProbe();
            Assert.IsFalse(WaitCondition.TextContains("Header", "attack").IsMet(p));
            p.Texts["Header"] = "ATTACK SUCCEED";
            Assert.IsTrue(WaitCondition.TextContains("Header", "attack").IsMet(p));
        }

        [Test]
        public void TextContains_FalseWhenElementMissing()
        {
            var p = new FakeProbe();
            Assert.IsFalse(WaitCondition.TextContains("Header", "x").IsMet(p));
        }

        private sealed class FakeState : IStateProbe
        {
            public string? CurrentStateName { get; set; }
        }

        /// <summary>
        /// The signal a game's own state machine gives is authoritative in a way UI presence is
        /// not: a button stays in the hierarchy across state changes. Found live on roguelegend,
        /// where Present("ButtonRoll") held during 'Game/Moving' and every board shot raced the
        /// token animation.
        /// </summary>
        [Test]
        public void State_MetOnlyWhenTheStateProbeMatches()
        {
            var ui = new FakeProbe();
            var state = new FakeState { CurrentStateName = "Game/Moving" };
            Assert.IsFalse(WaitCondition.State("Game/Idling").IsMet(ui, state));
            state.CurrentStateName = "Game/Idling";
            Assert.IsTrue(WaitCondition.State("Game/Idling").IsMet(ui, state));
        }

        [Test]
        public void State_IsNeverMetWithoutAStateProbe()
        {
            // Callers that only have a UI probe must not read a state condition as satisfied.
            Assert.IsFalse(WaitCondition.State("Game/Idling").IsMet(new FakeProbe()));
        }

        /// <summary>
        /// The distinction that finally gave one board game a reliable "ready for input" settle: its roll
        /// button stays in the hierarchy but DISABLED through moves and encounters, so Present was true
        /// for the whole window in which the player could not act — and its state machine could not be
        /// used instead, because it did not always return to Idling after a battle.
        /// </summary>
        [Test]
        public void Interactable_DistinguishesPresentFromActionable()
        {
            var p = new FakeProbe();
            Assert.IsFalse(WaitCondition.Interactable("ButtonRoll").IsMet(p), "missing");

            p.Names.Add("ButtonRoll");
            p.Locked.Add("ButtonRoll");
            Assert.IsTrue(WaitCondition.Present("ButtonRoll").IsMet(p), "present while disabled");
            Assert.IsFalse(WaitCondition.Interactable("ButtonRoll").IsMet(p), "…but not actionable");

            p.Locked.Remove("ButtonRoll");
            Assert.IsTrue(WaitCondition.Interactable("ButtonRoll").IsMet(p));
        }

        /// <summary>
        /// The conjunction a real shot needs: "my anchor is present AND no overlay covers it". Presence
        /// alone is not visibility — a full-screen celebration leaves everything under it present, which
        /// let a take be accepted with a banner across its subject.
        /// </summary>
        [Test]
        public void All_RequiresEverySubConditionToHold()
        {
            var ui = new FakeProbe();
            var both = WaitCondition.All(
                WaitCondition.Present("Hero_Enhance_Button"),
                WaitCondition.Absent("BlackScreen"));

            Assert.IsFalse(both.IsMet(ui), "anchor missing");
            ui.Names.Add("Hero_Enhance_Button");
            Assert.IsTrue(both.IsMet(ui), "anchor present, no overlay");
            ui.Names.Add("BlackScreen");
            Assert.IsFalse(both.IsMet(ui), "overlay covering the anchor must fail the check");
        }

        [Test]
        public void All_ThreadsTheStateProbeIntoSubConditions()
        {
            var ui = new FakeProbe { Names = { "Choice_Panel" } };
            var state = new FakeState { CurrentStateName = "Game/Moving" };
            var cond = WaitCondition.All(
                WaitCondition.Present("Choice_Panel"),
                WaitCondition.State("Game/InEvent"));
            Assert.IsFalse(cond.IsMet(ui, state));
            state.CurrentStateName = "Game/InEvent";
            Assert.IsTrue(cond.IsMet(ui, state));
        }

        [Test]
        public void All_WithNoParts_IsVacuouslyTrue()
        {
            Assert.IsTrue(WaitCondition.All().IsMet(new FakeProbe()));
        }

        [Test]
        public void Describe_NamesTheConditionSoAFailureMessageCanIdentifyIt()
        {
            Assert.AreEqual("Present 'ButtonRoll'", WaitCondition.Present("ButtonRoll").Describe());
            Assert.AreEqual("Absent 'Popup'", WaitCondition.Absent("Popup").Describe());
            Assert.AreEqual("State 'Game/Idling'", WaitCondition.State("Game/Idling").Describe());
            Assert.AreEqual("Header text contains 'win'",
                WaitCondition.TextContains("Header", "win").Describe());
            Assert.AreEqual("Interactable 'ButtonRoll'", WaitCondition.Interactable("ButtonRoll").Describe());
            Assert.AreEqual("All(Present 'Anchor' AND Absent 'Overlay')",
                WaitCondition.All(WaitCondition.Present("Anchor"), WaitCondition.Absent("Overlay")).Describe());
        }
    }
}
