using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// v3 P5 (§3.6, §3.6a items 1, 4, 5) — A RECIPE'S DECLARED START, PLAYED, over a fake screen: the dismiss loop before,
    /// between and after every step through the press guard; an unknown overlay stops the run; arrival counts NEW names
    /// only; chains check the next start; stuck on Loading is a boot failure; every press is an edge; a close that moves a
    /// watched value is flagged. And the restart rule's table.
    /// </summary>
    public class StartPathTests
    {
        private double _clock;

        private StartPathRun Play(FakeReplayScreen screen, IReadOnlyList<RecipeFile.Recipe> chain, PressLog? log = null,
            Func<string, bool>? cheat = null)
        {
            var run = new StartPathRun(chain, log ?? new PressLog());
            var ticks = 0;
            foreach (var _ in run.Run(screen, cheat ?? (_ => true), () => _clock))
            {
                _clock += 0.1;
                Assert.Less(++ticks, 200_000, "the start path never finished");
            }
            Assert.IsTrue(run.Finished);
            return run;
        }

        /// <summary>The lobby, where Equip opens the equipment screen and Sword (on it) opens the sword details.</summary>
        private static FakeReplayScreen Lobby()
        {
            var s = new FakeReplayScreen();
            s.Roots.Add("Ui/Lobby");
            s.Add("Lobby");
            s.Add("Equip", "Equipment");
            s.OnClick["Equip"] = () =>
            {
                s.Roots.Add("Ui/Equipment");
                s.Add("Equipment");
                s.Add("EqGrid");
                s.Add("Sword");
            };
            s.OnClick["Sword"] = () =>
            {
                s.Roots.Add("Ui/Sword");
                s.Add("SwordDetails");
                s.Add("SwordStats");
            };
            return s;
        }

        private static readonly string[] AfterEquip = { "Ui/Equipment", "Ui/Lobby" };
        private static readonly string[] AfterSword = { "Ui/Equipment", "Ui/Lobby", "Ui/Sword" };

        private static RecipeFile.Recipe Equip(string[]? dismiss = null) =>
            FakeReplayScreen.Recipe("r000000000000000a", new[] { "Equip" }, dismiss, new[] { "EqGrid", "Equipment" }, new[] { AfterEquip });

        [SetUp]
        public void SetUp() => _clock = 0;

        [Test]
        public void OneRecipe_IsPlayed_Arrives_AndEveryPressIsAnEdge()
        {
            var s = Lobby();
            var log = new PressLog();
            var run = Play(s, new[] { Equip() }, log);
            Assert.IsTrue(run.Arrived, run.Why);
            CollectionAssert.AreEqual(new[] { "Equip" }, s.Clicked);
            Assert.AreEqual(1, log.Edges.List.Count);
            Assert.AreEqual(("Ui/Lobby", "Equip", "Ui/Equipment|Ui/Lobby"), log.Edges.List[0]);
            Assert.AreEqual(true, run.Recipes[0]!["arrived"]!.Value<bool>());
        }

        [Test]
        public void APopupThatRisesAfterStepOneOfAThreeStepPath_IsClosedBeforeStepTwo()
        {
            // §10's open question: spike B's loop ran only before the goal steps — a popup after step 1 got past it
            var s = Lobby();
            var equip = s.OnClick["Equip"];
            s.OnClick["Equip"] = () => { equip(); s.Popup("Ui/DailyGift", "GiftClose", "Gift"); };
            s.Add("Back");
            s.OnClick["SwordStats"] = () => { s.Add("StatsPanel"); s.Add("StatsGraph"); };
            var r = FakeReplayScreen.Recipe("r000000000000000b", new[] { "Equip", "Sword", "SwordStats" }, new[] { "GiftClose" },
                new[] { "StatsGraph", "StatsPanel" }, new[] { AfterEquip.Append("Ui/DailyGift").ToArray(), AfterSword, AfterSword });
            var log = new PressLog();
            var run = Play(s, new[] { r }, log);
            Assert.IsTrue(run.Arrived, run.Why);
            CollectionAssert.AreEqual(new[] { "Equip", "GiftClose", "Sword", "SwordStats" }, s.Clicked);
            CollectionAssert.AreEqual(new[] { "GiftClose" }, log.Dismissed);
            // the close is an edge too, between the two goal steps
            CollectionAssert.AreEqual(new[] { "Equip", "GiftClose", "Sword", "SwordStats" }, log.Edges.List.Select(e => e.Press).ToArray());
        }

        [Test]
        public void AnOverlayNobodyTaught_StopsTheRun_AndNothingMoreIsPressed()
        {
            var s = Lobby();
            var equip = s.OnClick["Equip"];
            // a spend screen with no close the recipe knows
            s.OnClick["Equip"] = () => { equip(); s.Roots.Add("Ui/StarterPack"); s.Add("StarterPackGet"); };
            var r = FakeReplayScreen.Recipe("r000000000000000b", new[] { "Equip", "Sword" }, new string[0],
                new[] { "SwordDetails", "SwordStats" }, new[] { AfterEquip, AfterSword });
            var run = Play(s, new[] { r });
            Assert.IsFalse(run.Arrived);
            Assert.AreEqual(StartPathRun.KindUnknownOverlay, run.FailedKind);
            CollectionAssert.AreEqual(new[] { "Ui/StarterPack" }, run.UnknownRoots);
            Assert.AreEqual(0, run.FailedStep);
            CollectionAssert.AreEqual(new[] { "Equip" }, s.Clicked, "step 2 must never be pressed under an unknown overlay");
            StringAssert.Contains("never guessed", run.Why);
        }

        [Test]
        public void ACloseWithARiskyWord_IsRefusedByTheGuard_ThePopupStays_AndTheRunStops()
        {
            // the close the teach recorded says BUY — the guard refuses it, so the spend popup is never "dismissed" by buying
            var s = Lobby();
            var equip = s.OnClick["Equip"];
            s.OnClick["Equip"] = () => { equip(); s.Popup("Ui/Offer", "BuyAndClose"); };
            var r = FakeReplayScreen.Recipe("r000000000000000b", new[] { "Equip" }, new[] { "BuyAndClose" },
                new[] { "EqGrid", "Equipment" }, new[] { AfterEquip });
            var log = new PressLog();
            var run = Play(s, new[] { r }, log);
            CollectionAssert.DoesNotContain(s.Clicked, "BuyAndClose");
            Assert.AreEqual(StartPathRun.KindUnknownOverlay, run.FailedKind);
            Assert.IsTrue(log.Lines.Any(l => l.Contains("dismiss 'BuyAndClose' not pressed: PRESS REFUSED")), string.Join("\n", log.Lines));
        }

        [Test]
        public void AnOverlayAlreadyUpBeforeStepOne_IsNotExcused_ItStopsTheRunAfterTheStep()
        {
            var s = Lobby();
            s.Roots.Add("Ui/SeasonPass");
            s.Add("SeasonPassBuy");
            var run = Play(s, new[] { Equip() });
            Assert.AreEqual(StartPathRun.KindUnknownOverlay, run.FailedKind);
            CollectionAssert.AreEqual(new[] { "Ui/SeasonPass" }, run.UnknownRoots);
        }

        [Test]
        public void AStepWithNoTaughtScreen_IsSaid_AndNotChecked()
        {
            var s = Lobby();
            var r = FakeReplayScreen.Recipe("r000000000000000c", new[] { "Equip" }, null, new[] { "EqGrid", "Equipment" });
            var log = new PressLog();
            var run = Play(s, new[] { r }, log);
            Assert.IsTrue(run.Arrived, run.Why);
            Assert.IsTrue(log.Lines.Any(l => l.Contains("no taught screen to compare")));
        }

        [Test]
        public void StuckOnLoading_IsABootFailure_NotAFailedReplay()
        {
            var s = new FakeReplayScreen();
            s.Roots.Add("Ui/Loading");
            var run = Play(s, new[] { Equip() });
            Assert.AreEqual(StartPathRun.KindBootStuck, run.FailedKind);
            Assert.IsNull(run.FailedStep);
            Assert.GreaterOrEqual(_clock, StartPathRun.BootWaitSec);
            Assert.AreEqual(RestartRule.Move.BootRetry, RestartRule.AfterStartStopped(run.FailedKind, 0));
        }

        [Test]
        public void AChainOfTwo_PlaysTheAnchorRecipeFirst_ThenTheOneThatStartsFromIt()
        {
            var s = Lobby();
            var sword = FakeReplayScreen.Recipe("r000000000000000d", new[] { "Sword" }, null, new[] { "SwordDetails", "SwordStats" },
                new[] { AfterSword }, startsFrom: "r000000000000000a");
            var run = Play(s, new[] { Equip(), sword });
            Assert.IsTrue(run.Arrived, run.Why);
            CollectionAssert.AreEqual(new[] { "Equip", "Sword" }, s.Clicked);
            Assert.AreEqual(2, run.Recipes.Count);
        }

        [Test]
        public void AChainWhoseNextStartIsNotTheLastEnd_IsAChainFailure_NeverABootFailure()
        {
            var s = Lobby();
            var shop = FakeReplayScreen.Recipe("r000000000000000e", new[] { "ShopTab" }, null, new[] { "Shop", "ShopGrid" },
                new[] { new[] { "Ui/Lobby", "Ui/Shop" } }, startsFrom: "r000000000000000a");
            var run = Play(s, new[] { Equip(), shop });
            Assert.AreEqual(StartPathRun.KindChain, run.FailedKind);
            StringAssert.Contains("its start is not the last one's end", run.Why);
            Assert.AreEqual(RestartRule.Move.GiveUp, RestartRule.AfterStartStopped(run.FailedKind, 0));
        }

        [Test]
        public void Arrival_CountsOnlyNamesNewAfterTheLastStep_NotNamesAlreadyThere()
        {
            // the lobby already shows both arrival names; the press opens nothing — a presence check would pass
            var s = Lobby();
            s.Add("EqGrid");
            s.Add("Equipment");
            s.OnClick["Equip"] = () => { };
            var run = Play(s, new[] { FakeReplayScreen.Recipe("r000000000000000a", new[] { "Equip" }, null, new[] { "EqGrid", "Equipment" }, new[] { new[] { "Ui/Lobby" } }) });
            Assert.AreEqual(StartPathRun.KindArrival, run.FailedKind);
            StringAssert.Contains("0 of its arrival names appeared", run.Why);
        }

        /// <summary>Kit 0.14.0 (re-audit MED) — a destination that builds itself ~10 s after the last press (it passed the
        /// teach's replay, which waits up to 20 s) passes a re-proof too; one that never builds fails at ~20 s, not forever.</summary>
        [Test]
        public void Arrival_WaitsAsLongAsTheTeachsReplayMay_ALateDestinationPasses_ANeverOneFailsInTime()
        {
            Assert.AreEqual(TeachAnalysis.MaxReplayEndWaitSec, StartPathRun.AfterStepSec + StartPathRun.ArrivalWaitSec, 1e-9);
            FakeReplayScreen LateLobby(double lateSec)
            {
                var s = Lobby();
                double? pressed = null;
                s.OnClick["Equip"] = () => pressed = _clock;
                s.OnQuery = () =>
                {
                    if (pressed is { } t && _clock >= t + lateSec && !s.Roots.Contains("Ui/Equipment"))
                    {
                        s.Roots.Add("Ui/Equipment");
                        s.Add("Equipment");
                        s.Add("EqGrid");
                    }
                };
                return s;
            }
            var late = Play(LateLobby(10), new[] { Equip() });
            Assert.IsTrue(late.Arrived, late.Why);
            Assert.Less(_clock, 15, "it did not return as soon as the names appeared");

            _clock = 0;
            var never = Play(LateLobby(1000), new[] { Equip() });
            Assert.AreEqual(StartPathRun.KindArrival, never.FailedKind);
            Assert.GreaterOrEqual(_clock, TeachAnalysis.MaxReplayEndWaitSec);
            Assert.Less(_clock, TeachAnalysis.MaxReplayEndWaitSec + 5, "the arrival wait did not end near 20 s");
        }

        /// <summary>Fix 5b audit H1 (invariant 185) — a recipe confirmed by picture has no arrival names: the run waits the
        /// lag its teach's replay waited before it goes on (and before a recording starts), never one settle.</summary>
        [Test]
        public void APictureRecipeWithNoArrivalNames_WaitsItsTaughtLag_BeforeGoingOn()
        {
            var s = Lobby();
            double? pressed = null;
            s.OnClick["Equip"] = () => pressed = _clock;
            var r = FakeReplayScreen.Recipe("r000000000000000a", new[] { "Equip" }, null, new string[0], new[] { new[] { "Ui/Lobby" } });
            r.ByPicture = true;
            r.ArrivalLagSec = 4.2;
            var run = Play(s, new[] { r });
            Assert.IsTrue(run.Arrived, run.Why);
            Assert.IsNotNull(pressed);
            Assert.GreaterOrEqual(_clock - pressed!.Value, 4.2 - 0.11, "it went on before the taught lag");
            // control: the same recipe with no lag goes on after one settle
            _clock = 0;
            pressed = null;
            var s2 = Lobby();
            s2.OnClick["Equip"] = () => pressed = _clock;
            var r2 = FakeReplayScreen.Recipe("r000000000000000a", new[] { "Equip" }, null, new string[0], new[] { new[] { "Ui/Lobby" } });
            var quick = Play(s2, new[] { r2 });
            Assert.IsTrue(quick.Arrived, quick.Why);
            Assert.Less(_clock - pressed!.Value, 3.0, "a recipe with no lag waited as if it had one");
        }

        [Test]
        public void ACloseThatMovesAWatchedValue_IsFlagged_NotStopped()
        {
            var s = Lobby();
            var gems = 100;
            var equip = s.OnClick["Equip"];
            s.OnClick["Equip"] = () =>
            {
                equip();
                s.Popup("Ui/DailyClaim", "ClaimClose");
                var close = s.OnClick["ClaimClose"];
                s.OnClick["ClaimClose"] = () => { close(); gems += 50; };
            };
            var r = FakeReplayScreen.Recipe("r000000000000000b", new[] { "Equip" }, new[] { "ClaimClose" }, new[] { "EqGrid", "Equipment" },
                new[] { AfterEquip.Append("Ui/DailyClaim").ToArray() });
            var log = new PressLog { Watch = new[] { "Save.Wallet.Gems" }, ReadWatch = _ => gems.ToString() };
            var run = Play(s, new[] { r }, log);
            Assert.IsTrue(run.Arrived, run.Why);
            Assert.AreEqual(1, log.Flags.Count);
            Assert.AreEqual(("ClaimClose", "Save.Wallet.Gems", "100", "150"), (log.Flags[0].Dismiss, log.Flags[0].Path, log.Flags[0].Before, log.Flags[0].After));
        }

        [Test]
        public void ACheatStep_RunsThroughTheCallersGate_AndARefusedOneStopsTheRun()
        {
            var s = Lobby();
            var r = FakeReplayScreen.Recipe("r000000000000000f", new string[0]);
            r.Steps = new JArray(new JObject { ["kind"] = "cheat", ["command"] = "Reset user" });
            var ran = new List<string>();
            Assert.IsTrue(Play(s, new[] { r }, cheat: c => { ran.Add(c); return true; }).Arrived);
            CollectionAssert.AreEqual(new[] { "Reset user" }, ran);
            var refused = Play(Lobby(), new[] { r }, cheat: _ => false);
            Assert.AreEqual(StartPathRun.KindStep, refused.FailedKind);
            StringAssert.Contains("the cheat 'Reset user' was refused or failed", refused.Why);
        }

        [Test]
        public void EveryPress_IsOnTheChainsOwnNames_ANameTheRecipeDoesNotHoldIsNeverPressed()
        {
            var s = Lobby();
            var r = FakeReplayScreen.Recipe("r000000000000000a", new[] { "Equip" }, new[] { "Close" }, new[] { "EqGrid", "Equipment" }, new[] { AfterEquip });
            var run = new StartPathRun(new[] { r }, new PressLog());
            CollectionAssert.AreEquivalent(new[] { "Equip", "Close" }, run.Allowlist);
        }

        [Test]
        public void AGoalStepWithARiskyWord_IsRefusedByTheGuard_EvenThoughTheRecipeHoldsIt()
        {
            var s = Lobby();
            s.Add("BuyGems");
            var r = FakeReplayScreen.Recipe("r000000000000000a", new[] { "BuyGems" }, null, new[] { "Shop", "Gems" }, new[] { new[] { "Ui/Lobby" } });
            var run = Play(s, new[] { r });
            Assert.AreEqual(StartPathRun.KindStep, run.FailedKind);
            StringAssert.Contains("PRESS REFUSED", run.Why);
            CollectionAssert.IsEmpty(s.Clicked);
        }

        [Test]
        public void TheDismissLoop_StopsAtItsCap()
        {
            var s = new FakeReplayScreen();
            s.Add("Again");
            var log = new PressLog();
            var ticks = 0;
            foreach (var _ in DismissLoop.Run(s, new[] { "Again" }, new[] { "Again" }, () => _clock, log))
            {
                _clock += 0.1;
                // a loop without its cap never ends on a popup that never goes — fail, never hang
                if (++ticks > 10_000) Assert.Fail("the dismiss loop has no cap: it pressed " + log.Dismissed.Count + " times");
            }
            Assert.AreEqual(DismissLoop.MaxPresses, log.Dismissed.Count);
            Assert.IsTrue(log.Lines.Any(l => l.Contains("cap of 12")));
        }

        [Test]
        public void Edges_AreCapped_AndCutToTheBoxsCaps()
        {
            var e = new DirectorEdges();
            e.Add(new string('r', 3000), "P", "a");
            Assert.AreEqual(DirectorEdges.MaxKey, e.List[0].Before.Length);
            for (var i = 0; i < DirectorEdges.Max + 4; i++) e.Add("r", "P" + i, "a");
            Assert.AreEqual(DirectorEdges.Max, e.List.Count);
            Assert.AreEqual(5, e.Dropped);
            Assert.LessOrEqual(DirectorEdges.Max, 400, "the box keeps at most 400 per run (edge-log.ts EDGE_CAPS.perRun)");
        }

        [Test]
        public void TheRestartRule_ResetsInsideOnePlaySessionFirst()
        {
            Assert.AreEqual(RestartRule.Move.None, RestartRule.BeforeTake(0, true, "Reset user", true), "the first take starts from a fresh boot");
            Assert.AreEqual(RestartRule.Move.None, RestartRule.BeforeTake(3, false, null, false), "no declared start: today's takes");
            Assert.AreEqual(RestartRule.Move.ResetCheat, RestartRule.BeforeTake(1, true, "Reset user", true));
            Assert.AreEqual(RestartRule.Move.BackRecipe, RestartRule.BeforeTake(1, true, null, true));
            Assert.AreEqual(RestartRule.Move.PlayRestart, RestartRule.BeforeTake(1, true, "  ", false));
            Assert.AreEqual(RestartRule.Move.BootRetry, RestartRule.AfterStartStopped(StartPathRun.KindBootStuck, RestartRule.MaxBootRetries - 1));
            Assert.AreEqual(RestartRule.Move.GiveUp, RestartRule.AfterStartStopped(StartPathRun.KindBootStuck, RestartRule.MaxBootRetries));
            Assert.AreEqual(RestartRule.Move.GiveUp, RestartRule.AfterStartStopped(StartPathRun.KindStep, 0), "a failed step is never retried by a reboot");
            Assert.AreEqual(30, RestartRule.DefaultSettleSec);
        }
    }
}
