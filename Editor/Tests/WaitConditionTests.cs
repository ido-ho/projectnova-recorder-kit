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

        /// <summary>Plan v3.1 phase 6.4 — a value is matched exactly, never as a substring: "5" is inside "15".</summary>
        [Test]
        public void TextNumber_RefusesFifteenForFive()
        {
            var p = new FakeProbe();
            Assert.IsFalse(WaitCondition.TextNumber("Level", 5).IsMet(p)); // no element: unmet, never a guess
            p.Texts["Level"] = "Level 15";
            Assert.IsFalse(WaitCondition.TextNumber("Level", 5).IsMet(p));
            Assert.IsTrue(WaitCondition.TextContains("Level", "5").IsMet(p)); // the substring check this replaces says yes
            p.Texts["Level"] = "Level 5";
            Assert.IsTrue(WaitCondition.TextNumber("Level", 5).IsMet(p));
            p.Texts["Level"] = "5/10";
            Assert.IsTrue(WaitCondition.TextNumber("Level", 5).IsMet(p));
            p.Texts["Level"] = "Lv.5";
            Assert.IsTrue(WaitCondition.TextNumber("Level", 5).IsMet(p));
            p.Texts["Level"] = "Gold 50";
            Assert.IsFalse(WaitCondition.TextNumber("Level", 5).IsMet(p));
            // phase 6 audit: the FIRST number is the one shown — "3/5" and "next at 5" are level 3
            p.Texts["Level"] = "Floor 3/5";
            Assert.IsFalse(WaitCondition.TextNumber("Level", 5).IsMet(p));
            p.Texts["Level"] = "Level 3 (next at 5)";
            Assert.IsFalse(WaitCondition.TextNumber("Level", 5).IsMet(p));
            p.Texts["Level"] = "x2.5";
            Assert.IsTrue(WaitCondition.TextNumber("Level", 2.5).IsMet(p));
            Assert.IsFalse(WaitCondition.TextNumber("Level", 2).IsMet(p));
            // phase 6 audit round 2: a sign that starts a word, and thousands with commas
            p.Texts["Level"] = "-5";
            Assert.IsFalse(WaitCondition.TextNumber("Level", 5).IsMet(p));
            Assert.IsTrue(WaitCondition.TextNumber("Level", -5).IsMet(p));
            p.Texts["Level"] = "Level-5";
            Assert.IsTrue(WaitCondition.TextNumber("Level", 5).IsMet(p));
            p.Texts["Level"] = "Gold 1,000";
            Assert.IsTrue(WaitCondition.TextNumber("Level", 1000).IsMet(p));
            Assert.IsFalse(WaitCondition.TextNumber("Level", 1).IsMet(p));
            p.Texts["Level"] = "1,5";
            Assert.IsTrue(WaitCondition.TextNumber("Level", 1).IsMet(p));
            // round 3: a comma group must end there — "12,3456" is 12, never 12345
            p.Texts["Level"] = "12,3456";
            Assert.IsTrue(WaitCondition.TextNumber("Level", 12).IsMet(p));
        }

        [Test]
        public void TextEquals_IsTheWholeTextTrimmedAnyCase()
        {
            var p = new FakeProbe();
            p.Texts["Title"] = "  Victory ";
            Assert.IsTrue(WaitCondition.TextEquals("Title", "victory").IsMet(p));
            p.Texts["Title"] = "Victory!!";
            Assert.IsFalse(WaitCondition.TextEquals("Title", "Victory").IsMet(p));
            Assert.AreEqual("Title text is 'Victory'", WaitCondition.TextEquals("Title", "Victory").Describe());
            Assert.AreEqual("Level shows the number 5 first", WaitCondition.TextNumber("Level", 5).Describe());
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
