using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// v3 P4 (§3.5, §3.6a items 1 and 3) — THE CONFIRMING REPLAY and the 2-of-3 REBIND, over a fake screen: the dismiss
    /// loop before every goal step, every press through the press guard with the recipe's own names as the allowlist, a
    /// rebind that is a candidate only (pressed only on the allowlist, never written back), and the replay's JSON fed
    /// straight into <see cref="TeachAnalysis"/>.
    /// </summary>
    public class RecipeReplayTests
    {
        /// <summary>A screen of named elements; a click runs the element's scripted effect.</summary>
        private sealed class FakeScreen : IReplayScreen
        {
            public readonly List<ElementIdentity> Elements = new();
            public readonly HashSet<string> Roots = new(StringComparer.Ordinal);
            public readonly HashSet<string> ExtraNames = new(StringComparer.Ordinal);
            public readonly Dictionary<string, Action> OnClick = new(StringComparer.Ordinal);
            public readonly List<string> Clicked = new();

            public ElementIdentity Add(string name, string? label = null, string? handler = null)
            {
                var e = new ElementIdentity { Name = name, Label = label, Handler = handler };
                Elements.Add(e);
                return e;
            }

            public void Remove(string name) => Elements.RemoveAll(e => e.Name == name);

            private ElementIdentity? Find(string selector)
            {
                var (name, label) = UguiDriver.ParseSelector(selector);
                return Elements.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)
                    && (label == null || (e.Label != null && UguiDriver.LabelMatches(e.Label, label))));
            }

            public ScreenSignature.Observation Observe()
            {
                var o = new ScreenSignature.Observation { Roots = Roots.OrderBy(r => r, StringComparer.Ordinal).ToList() };
                foreach (var e in Elements) o.Names.Add(e.Name);
                foreach (var n in ExtraNames) o.Names.Add(n);
                return o;
            }

            public IReadOnlyList<ElementIdentity> Clickables() => Elements.ToList();
            public bool Exists(string selector) => Find(selector) != null;

            public bool Click(string selector)
            {
                var e = Find(selector);
                if (e == null) return false;
                Clicked.Add(selector);
                if (OnClick.TryGetValue(e.Name, out var effect)) effect();
                return true;
            }

            public void PointerDown(string selector) => Clicked.Add("down:" + selector);
            public void PointerUp(string selector) => Clicked.Add("up:" + selector);
        }

        private double _clock;

        private RecipeReplay Replay(FakeScreen screen, JArray steps, IEnumerable<string> dismiss, Func<string, bool>? cheat = null)
        {
            var r = new RecipeReplay(steps, dismiss);
            foreach (var _ in r.Run(screen, cheat ?? (_ => true), () => _clock)) _clock += 0.1;
            return r;
        }

        private static JObject Click(string name, ElementIdentity? el = null)
        {
            var o = new JObject { ["kind"] = "click", ["name"] = name };
            if (el != null) o["element"] = el.ToJson();
            return o;
        }

        /// <summary>The lobby with a play/equip path to the equipment screen.</summary>
        private static FakeScreen Lobby()
        {
            var s = new FakeScreen();
            s.Roots.Add("Ui/Lobby");
            s.Add("Lobby");
            s.Add("Equip", "Equipment", "LobbyView.OpenEquipment");
            s.OnClick["Equip"] = () =>
            {
                s.Roots.Add("Ui/Equipment");
                s.Add("Equipment");
                s.Add("EqGrid");
                s.Add("EqBack");
            };
            return s;
        }

        [Test]
        public void TheReplayPressesThePath_AndItsJsonConfirmsTheTeach()
        {
            var s = Lobby();
            var r = Replay(s, new JArray(Click("Equip")), new string[0]);
            Assert.IsTrue(r.Finished);
            var json = r.ToJson();
            Assert.IsTrue(json["ran"]!.Value<bool>());
            Assert.IsTrue(json["steps"]![0]!["pressed"]!.Value<bool>());
            var presses = new JArray(new JObject
            {
                ["fired"] = "click", ["selector"] = "Equip", ["pressRoot"] = "Ui/Lobby", ["shared"] = false,
                ["before"] = new JObject { ["roots"] = new JArray("Ui/Lobby") },
                ["after"] = new JObject { ["roots"] = new JArray("Ui/Equipment", "Ui/Lobby"), ["added"] = new JArray("EqBack", "EqGrid", "Equipment") },
            });
            var a = TeachAnalysis.Analyse(presses, new[] { "Equip", "Equipment", "EqGrid", "EqBack" }, json,
                new JObject { ["kind"] = TeachAnalysis.StartLobby });
            Assert.IsTrue(a.Confirmed, a.Why);
            CollectionAssert.AreEqual(new[] { "EqBack", "EqGrid", "Equipment" }, a.Arrival);
        }

        [Test]
        public void TheDismissLoop_TapsWhicheverPopupIsShowing_InAnyOrder_UntilTwoQuietPasses()
        {
            var s = Lobby();
            s.Roots.Add("Ui/Rating");
            s.Add("Later");
            s.OnClick["Later"] = () => { s.Remove("Later"); s.Roots.Remove("Ui/Rating"); s.Roots.Add("Ui/Offer"); s.Add("CloseOffer"); };
            s.OnClick["CloseOffer"] = () => { s.Remove("CloseOffer"); s.Roots.Remove("Ui/Offer"); };
            // the recipe learned them in the OTHER order, and knows a third that never shows this time
            var r = Replay(s, new JArray(Click("Equip")), new[] { "CloseOffer", "DailyClose", "Later" });
            CollectionAssert.AreEqual(new[] { "Later", "CloseOffer" }, r.Dismissed);
            CollectionAssert.AreEqual(new[] { "Later", "CloseOffer", "Equip" }, s.Clicked);
            Assert.IsTrue(r.Steps[0]!["pressed"]!.Value<bool>());
        }

        [Test]
        public void APopupThatAppearsAfterStepOne_IsDismissedBeforeStepTwo()
        {
            var s = Lobby();
            s.OnClick["Equip"] = () =>
            {
                s.Roots.Add("Ui/Equipment");
                s.Add("Weapons");
                s.Add("Tip"); // a popup that comes up on the equipment screen
            };
            s.OnClick["Tip"] = () => s.Remove("Tip");
            s.OnClick["Weapons"] = () => { s.Add("WeaponList"); s.Add("WeaponStats"); };
            var r = Replay(s, new JArray(Click("Equip"), Click("Weapons")), new[] { "Tip" });
            CollectionAssert.AreEqual(new[] { "Equip", "Tip", "Weapons" }, s.Clicked);
            Assert.AreEqual(2, r.Steps.Count);
            CollectionAssert.AreEquivalent(new[] { "WeaponList", "WeaponStats" }, r.Steps[1]!["after"]!["added"]!.Values<string>().ToList());
        }

        [Test]
        public void ThePopupLoopStopsAtItsCap()
        {
            var s = Lobby();
            s.Add("Again");
            var r = Replay(s, new JArray(Click("Equip")), new[] { "Again" }); // clicking never removes it
            Assert.AreEqual(RecipeReplay.MaxDismissPresses, r.Dismissed.Count);
        }

        [Test]
        public void AGoneElement_IsFoundByTwoOfItsThreeParts_AsACandidate_PressedBecauseItsNameIsAllowed_AndTheRecipeIsUntouched()
        {
            var s = Lobby();
            var taught = new ElementIdentity { Name = "Equip", Label = "Equipment", Handler = "LobbyView.OpenEquipment" };
            s.Remove("Equip");
            s.Add("Equip", "Gear", "LobbyView.OpenEquipment"); // relabelled by an update: name + handler still match
            s.OnClick["Equip"] = () => { s.Add("Equipment"); s.Add("EqGrid"); };
            var steps = new JArray(Click("Equip@Equipment", taught));
            var before = steps.ToString();
            var r = Replay(s, steps, new string[0]);
            Assert.IsTrue(r.Steps[0]!["pressed"]!.Value<bool>());
            var rebound = (JObject)r.Steps[0]!["rebound"]!;
            Assert.IsTrue(rebound["candidate"]!.Value<bool>());
            Assert.AreEqual(2, rebound["parts"]!.Value<int>());
            Assert.AreEqual("Gear", rebound["element"]!["label"]!.Value<string>());
            CollectionAssert.AreEqual(new[] { "Equip@Gear" }, s.Clicked);
            Assert.AreEqual(before, steps.ToString(), "the recipe is never rewritten to the candidate");
        }

        [Test]
        public void ACandidateWhoseNameIsNotOnTheAllowedNames_IsRefused_NeverPressed()
        {
            var s = Lobby();
            var taught = new ElementIdentity { Name = "Equip", Label = "Equipment", Handler = "LobbyView.OpenEquipment" };
            s.Remove("Equip");
            s.Add("GearBtn", "Equipment", "LobbyView.OpenEquipment"); // renamed: label + handler match, the NAME is new
            var r = Replay(s, new JArray(Click("Equip", taught)), new string[0]);
            Assert.IsFalse(r.Steps[0]!["pressed"]!.Value<bool>());
            StringAssert.Contains("not on this run's allowed names", r.Steps[0]!["refused"]!.Value<string>());
            CollectionAssert.IsEmpty(s.Clicked);
        }

        [Test]
        public void TwoCandidatesTiedAtTheBestMatch_AreRefusedAsAmbiguous()
        {
            var s = Lobby();
            var taught = new ElementIdentity { Name = "Equip", Label = "Equipment", Handler = "LobbyView.OpenEquipment" };
            s.Remove("Equip");
            s.Add("Equip", "A", "LobbyView.OpenEquipment");
            s.Add("Equip", "B", "LobbyView.OpenEquipment");
            var r = Replay(s, new JArray(Click("Equip@Equipment", taught)), new string[0]);
            StringAssert.Contains("does not pick one", r.Steps[0]!["refused"]!.Value<string>());
            CollectionAssert.IsEmpty(s.Clicked);
        }

        [Test]
        public void OnePartIsNotACandidate_AndANullPartNeverMatches()
        {
            var want = new ElementIdentity { Name = "Equip", Label = null, Handler = null };
            Assert.IsNull(RecipeRebind.Find(want, new[] { new ElementIdentity { Name = "Equip" } }).Candidate,
                "a code-wired, unlabelled element can match only its name — one part");
            var full = new ElementIdentity { Name = "Equip", Label = "Equipment", Handler = "V.Open" };
            Assert.IsNull(RecipeRebind.Find(full, new[] { new ElementIdentity { Name = "Other", Label = "Equipment" } }).Candidate);
            Assert.AreEqual(2, RecipeRebind.Find(full, new[] { new ElementIdentity { Name = "Other", Label = "equipment", Handler = "V.Open" } }).Parts);
        }

        [Test]
        public void ARiskyNameIsRefusedEvenWhenItIsTheRecipesOwn()
        {
            var s = new FakeScreen();
            s.Add("BuyGems");
            var r = Replay(s, new JArray(Click("BuyGems")), new string[0]);
            StringAssert.Contains("risky word root", r.Steps[0]!["refused"]!.Value<string>());
            CollectionAssert.IsEmpty(s.Clicked);
        }

        [Test]
        public void ARiskyDismissIsNotPressed_AndTheLoopGoesOn()
        {
            var s = Lobby();
            s.Add("PurchaseLater");
            var r = Replay(s, new JArray(Click("Equip")), new[] { "PurchaseLater" });
            CollectionAssert.IsEmpty(r.Dismissed);
            CollectionAssert.AreEqual(new[] { "Equip" }, s.Clicked);
        }

        [Test]
        public void ACheatStepRunsThroughTheRunner_AndARefusalStopsTheReplay()
        {
            var s = Lobby();
            var ran = new List<string>();
            var r = Replay(s, new JArray(new JObject { ["kind"] = "cheat", ["command"] = "Skip 10 Levels" }, Click("Equip")), new string[0],
                c => { ran.Add(c); return false; });
            CollectionAssert.AreEqual(new[] { "Skip 10 Levels" }, ran);
            StringAssert.Contains("was refused or failed", r.Steps[0]!["refused"]!.Value<string>());
            Assert.AreEqual(1, r.Steps.Count, "no step after a refusal");
            CollectionAssert.IsEmpty(s.Clicked);
        }

        [Test]
        public void TheReplayWaitsForTheScreenItStartsOn_ThenPresses()
        {
            var s = new FakeScreen(); // the game is still loading: nothing of the lobby yet
            var r = new RecipeReplay(new JArray(Click("Equip")), new string[0]);
            var ticks = 0;
            foreach (var _ in r.Run(s, _ => true, () => _clock))
            {
                _clock += 0.1;
                if (++ticks == 50) { s.Add("Equip"); s.OnClick["Equip"] = () => s.Add("Equipment"); }
            }
            Assert.IsTrue(r.Steps[0]!["pressed"]!.Value<bool>());
            CollectionAssert.AreEqual(new[] { "Equip" }, s.Clicked);
        }

        [Test]
        public void AStartScreenThatNeverComesUp_IsSaid_NotReadAsAGoneElement()
        {
            var r = Replay(new FakeScreen(), new JArray(Click("Equip")), new[] { "Close" });
            Assert.AreEqual(1, r.Steps.Count);
            StringAssert.Contains("never came up", r.Steps[0]!["refused"]!.Value<string>());
            Assert.GreaterOrEqual(_clock, RecipeReplay.ReadyWaitSec);
        }

        [Test]
        public void TheAllowlistIsTheRecipesOwnNames()
        {
            var allow = RecipeReplay.AllowlistOf(new JArray(Click("Tab@Gems"), new JObject { ["kind"] = "cheat", ["command"] = "x" }), new[] { "Close" });
            CollectionAssert.AreEquivalent(new[] { "Tab", "Close" }, allow);
        }

        [Test]
        public void ANotRunReplay_SaysWhy()
        {
            var j = RecipeReplay.NotRun("the game stayed on its loading screen");
            Assert.IsFalse(j["ran"]!.Value<bool>());
            Assert.AreEqual("the game stayed on its loading screen", j["why"]!.Value<string>());
        }

        /// <summary>Kit 0.14.0 (audit H1) — THE REPLAY READS ITS END AT THE TEACH'S OWN LAG. A fight that builds itself 4 s
        /// after the last press: a replay told the teach stopped 4.2 s after its last goal press sees it; one reading at
        /// today's 1.5 s settle does not — so the teach (read at Stop) and the replay are two observations of one moment.</summary>
        [Test]
        public void TheReplay_ReadsItsEndAtTheTeachsLagToStop_SoAScreenThatBuildsLateIsSeenByBoth()
        {
            RecipeReplay Played(double endWait, out double pressedAt)
            {
                var s = new FakeReplayScreen();
                s.Roots.Add("Ui/Lobby");
                s.Add("PlayButton");
                double? at = null;
                s.OnClick["PlayButton"] = () => at = _clock;
                s.OnQuery = () =>
                {
                    if (at is { } t && _clock >= t + 4 && !s.Roots.Contains("Ui/Fight"))
                    {
                        s.Roots.Add("Ui/Fight");
                        s.Add("FightHud");
                        s.Add("EnemyHp");
                    }
                };
                var r = new RecipeReplay(new JArray(Click("PlayButton")), new string[0], endWait);
                _clock = 0;
                foreach (var _ in r.Run(s, _ => true, () => _clock)) _clock += 0.1;
                pressedAt = at ?? -1;
                return r;
            }
            var lagged = Played(TeachAnalysis.ReplayEndWaitSec(new TeachAnalysis.Result { ArrivalFromStop = true },
                new JObject { ["afterLastGoalSec"] = 4.2 }), out _);
            Assert.AreEqual(4.2, lagged.EndWaitSec, 1e-9);
            CollectionAssert.IsSubsetOf(new[] { "EnemyHp", "FightHud" },
                lagged.Steps[0]!["after"]!["added"]!.Values<string>().ToList(), "the replay read its end before the teach's lag");
            // control: today's settle (no Stop screen) reads at 1.5 s and does not see the fight
            var today = Played(0, out _);
            CollectionAssert.DoesNotContain(today.Steps[0]!["after"]!["added"]!.Values<string>().ToList(), "FightHud");
            // the lag is used only when the arrival came from the Stop screen, and is capped
            Assert.AreEqual(0.0, TeachAnalysis.ReplayEndWaitSec(new TeachAnalysis.Result { ArrivalFromStop = false }, new JObject { ["afterLastGoalSec"] = 4.2 }));
            Assert.AreEqual(TeachAnalysis.MaxReplayEndWaitSec,
                TeachAnalysis.ReplayEndWaitSec(new TeachAnalysis.Result { ArrivalFromStop = true }, new JObject { ["afterLastGoalSec"] = 300 }));
            // fix 5b (invariant 185): a replay run for its PICTURE (no names at Stop) takes replay-stop.jpg at the same lag —
            // read off a real analysis, so the flag and the wait cannot drift apart
            var stop = new JObject
            {
                ["roots"] = new JArray("Ui/Board"), ["added"] = new JArray("EnemyModel(Clone)"), ["buttons"] = new JArray(),
                ["afterLastGoalSec"] = 4.2, ["timeLimit"] = false, ["thumbnail"] = TeachRecorder.StopThumbnail,
            };
            var press = new JObject
            {
                ["fired"] = "click", ["selector"] = "PlayButton", ["pressRoot"] = "Ui/Lobby", ["shared"] = false,
                ["before"] = new JObject { ["roots"] = new JArray("Ui/Lobby") },
                ["after"] = new JObject { ["roots"] = new JArray("Ui/Board"), ["added"] = new JArray(), ["buttons"] = new JArray() },
            };
            var forPicture = TeachAnalysis.Analyse(new JArray(press), new[] { "Lobby", "PlayButton" }, null,
                new JObject { ["kind"] = TeachAnalysis.StartLobby }, stop);
            Assert.IsTrue(forPicture.ReplayForPicture, forPicture.Why);
            Assert.IsNull(forPicture.ReplayBlocked, "too few names alone no longer blocks the replay when the Stop picture can be judged");
            Assert.AreEqual(4.2, TeachAnalysis.ReplayEndWaitSec(forPicture, stop), 1e-9);
            // control: the same teach with no Stop picture — blocked as before, and no lag
            stop["thumbnail"] = null;
            var noPicture = TeachAnalysis.Analyse(new JArray(press), new[] { "Lobby", "PlayButton" }, null,
                new JObject { ["kind"] = TeachAnalysis.StartLobby }, stop);
            Assert.IsFalse(noPicture.ReplayForPicture);
            Assert.IsNotNull(noPicture.ReplayBlocked);
            Assert.AreEqual(0.0, TeachAnalysis.ReplayEndWaitSec(noPicture, stop));
        }
    }
}
