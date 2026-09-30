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
            // kit 0.14.1 (invariant 187): the game not on its start screen is never a boot retry — a fresh Play resumes there
            Assert.AreEqual(RestartRule.Move.GiveUp, RestartRule.AfterStartStopped(StartPathRun.KindStartScreen, 0));
        }

        // ---- kit 0.14.1 (invariant 187): THE START CHECK — go on, the go-home lever, the person, or one sentence -----------

        private static StartCheck Check(Func<bool> isUp, string? home, IStartAsk? ask, Func<string, bool>? cheat = null)
        {
            var c = new StartCheck("the lobby", home, ask);
            var t = 0.0;
            var ticks = 0;
            foreach (var _ in c.Run(isUp, cheat ?? (_ => true), () => t))
            {
                t += 0.5;
                Assert.Less(++ticks, 100_000, "the start check never finished");
            }
            Assert.IsTrue(c.Finished);
            return c;
        }

        [Test]
        public void AnUnansweredAsk_EndsTheJobsRemainingTakes_InItsSentence_AndSurvivesAReload()
        {
            var unanswered = Check(() => false, null, new FakeAsk());
            Assert.AreEqual(unanswered.Failure, CaptureStart.AskUnanswered(unanswered));
            Assert.IsNull(CaptureStart.AskUnanswered(Check(() => true, null, new FakeAsk())), "went on: nothing to remember");
            Assert.IsNull(CaptureStart.AskUnanswered(Check(() => false, "go-home", null, _ => false)), "never asked: nothing to remember");
            Assert.IsNull(CaptureStart.AskUnanswered(null));
            var job = CaptureJob.FromJson(JObject.Parse(@"{ ""runId"": ""r1"", ""kind"": ""capture"", ""items"": [ { ""shot"": ""s"", ""take"": 1 } ] }"), out _)!;
            var p = CaptureProgress.Start(job);
            p.AskUnanswered = unanswered.Failure;
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ask-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                p.Save(path);
                Assert.AreEqual(unanswered.Failure, CaptureProgress.Load(path)!.AskUnanswered);
            }
            finally { System.IO.File.Delete(path); }
        }

        [Test]
        public void TheStartCheck_OnTheStartScreen_GoesOn_PressingNothing_AskingNobody()
        {
            var ask = new FakeAsk();
            var ran = new List<string>();
            var c = Check(() => true, "go-home", ask, cmd => { ran.Add(cmd); return true; });
            Assert.IsTrue(c.Ok);
            Assert.IsFalse(c.HomeRan);
            Assert.IsFalse(c.Asked);
            CollectionAssert.IsEmpty(ran);
            CollectionAssert.IsEmpty(ask.Asked);
        }

        [Test]
        public void TheStartCheck_RunsTheGoHomeLeverOnce_ThenAsks_ThenStops_WithOneSentence()
        {
            // the lever brings it home: no ask
            var home = false;
            var ask = new FakeAsk();
            var ran = new List<string>();
            var viaLever = Check(() => home, "go-home", ask, cmd => { ran.Add(cmd); home = true; return true; });
            Assert.IsTrue(viaLever.Ok);
            Assert.IsTrue(viaLever.HomeRan);
            CollectionAssert.AreEqual(new[] { "go-home" }, ran, "the lever ran more than once, or not at all");
            CollectionAssert.IsEmpty(ask.Asked);

            // the lever does not help: the person is asked, puts it there, presses Continue
            home = false;
            var person = new FakeAsk { Person = () => home = true };
            ran.Clear();
            var viaPerson = Check(() => home, "go-home", person, cmd => { ran.Add(cmd); return true; });
            Assert.IsTrue(viaPerson.Ok, viaPerson.Failure);
            CollectionAssert.AreEqual(new[] { "go-home" }, ran);
            CollectionAssert.AreEqual(new[] { "Put the game on the lobby and press Continue." }, person.Asked);
            Assert.IsTrue(person.Ended, "the window kept the ask after it was answered");

            // no lever chosen: straight to the person
            home = false;
            var noLever = Check(() => home, null, new FakeAsk { Person = () => home = true });
            Assert.IsTrue(noLever.Ok);
            Assert.IsFalse(noLever.HomeRan);

            // Continue, but the game is still elsewhere: one sentence
            home = false;
            var wrong = Check(() => home, null, new FakeAsk { Person = () => { } });
            Assert.IsFalse(wrong.Ok);
            Assert.AreEqual("Continue was pressed, but the game is still not on the lobby — put it there and try again.", wrong.Failure);

            // plan v3.1 M0.6: nobody answers — the ask WAITS past the old 300 s clock (no time limit on a person) until the
            // person presses Stop, then ends in the ask's own sentence
            var nobody = new FakeAsk();
            var waitedTo = 0.0;
            var unanswered = new StartCheck("the lobby", null, nobody);
            var clock = 0.0;
            foreach (var _ in unanswered.Run(() => false, _ => true, () => clock)) { clock += 0.5; waitedTo = clock; }
            Assert.Greater(waitedTo, 450.0, "the ask gave up on a clock (the old one stopped at 300 s)");
            Assert.AreEqual("Put the game on the lobby and press Continue. You pressed Stop in the Nova Capture window.", unanswered.Failure);
            Assert.IsTrue(nobody.Ended);

            // no ask at all (a caller with nobody to ask) and a lever that is refused: said, never a hang
            var refused = Check(() => false, "go-home", null, _ => false);
            StringAssert.StartsWith("the game was not on the lobby (the go-home lever did not bring it there)", refused.Failure);
        }

        [Test]
        public void ADeclaredStart_WithTheStartCheck_UsesTheLeverOnlyForALobbyStart_AndChecksTheStartScreensNames()
        {
            // the game resumed on the board (it remembers where it was left); the recipe starts on the lobby
            FakeReplayScreen BoardThatGoesHome()
            {
                var s = new FakeReplayScreen();
                s.Roots.Add("Ui/Board");
                s.Add("Roll_Btn");
                return s;
            }
            void ToLobby(FakeReplayScreen s)
            {
                s.Elements.Clear();
                s.Roots.Clear();
                s.Roots.Add("Ui/Lobby");
                s.Add("Equip");
                s.Add("Lobby_Banner");
                s.OnClick["Equip"] = () => { s.Add("Equipment"); s.Add("EqGrid"); };
            }
            var recipe = FakeReplayScreen.Recipe("r000000000000000a", new[] { "Equip" }, null, new[] { "EqGrid", "Equipment" });
            recipe.StartNames = new List<string> { "Equip", "Lobby_Banner", "Lobby_Chest" };
            var board = BoardThatGoesHome();
            var run = new StartPathRun(new[] { recipe }, new PressLog()) { Check = ("go-home", new FakeAsk()) };
            var t = 0.0;
            foreach (var _ in run.Run(board, cmd => { if (cmd == "go-home") ToLobby(board); return true; }, () => t)) t += 0.5;
            Assert.IsTrue(run.Arrived, run.Why);
            Assert.IsTrue(run.CheckRun!.HomeRan);
            Assert.IsTrue(run.ToJson()["startCheck"]!["homeRan"]!.Value<bool>());

            // a recipe taught FROM HERE: its start is not home, so the lever is never run — the person is asked
            var here = FakeReplayScreen.Recipe("r000000000000000b", new[] { "Equip" }, null, new[] { "EqGrid", "Equipment" });
            here.StartKind = TeachAnalysis.StartHere;
            here.StartNames = new List<string> { "Equip", "Lobby_Banner", "Lobby_Chest" };
            var board2 = BoardThatGoesHome();
            var ask = new FakeAsk { Person = () => ToLobby(board2) };
            var ran = new List<string>();
            var hereRun = new StartPathRun(new[] { here }, new PressLog()) { Check = ("go-home", ask) };
            t = 0;
            foreach (var _ in hereRun.Run(board2, cmd => { ran.Add(cmd); return true; }, () => t)) t += 0.5;
            Assert.IsTrue(hereRun.Arrived, hereRun.Why);
            CollectionAssert.IsEmpty(ran, "the go-home lever ran for a start that is not home");
            // audit M2: the ask names no object and no recipe id
            Assert.AreEqual("Put the game on the screen you started the teach on and press Continue.", ask.Asked.Single());

            // the first step alone is not the start screen when its names are missing: never played blindly
            var lookalike = new FakeReplayScreen();
            lookalike.Add("Equip"); // a same-named button on another screen, without the lobby's names
            var blind = new StartPathRun(new[] { recipe }, new PressLog()) { Check = (null, new FakeAsk()) };
            t = 0;
            foreach (var _ in blind.Run(lookalike, _ => true, () => t)) t += 5;
            Assert.AreEqual(StartPathRun.KindStartScreen, blind.FailedKind, blind.Why);
            CollectionAssert.IsEmpty(lookalike.Clicked);
            // audit H4: after the full boot wait, a screen with NOTHING clickable is still loading — a boot (retried), not an ask
            var loading = new FakeReplayScreen();
            var ask2 = new FakeAsk();
            var boot = new StartPathRun(new[] { recipe }, new PressLog()) { Check = ("go-home", ask2) };
            var ran2 = new List<string>();
            t = 0;
            foreach (var _ in boot.Run(loading, c => { ran2.Add(c); return true; }, () => t)) t += 5;
            Assert.AreEqual(StartPathRun.KindBootStuck, boot.FailedKind, boot.Why);
            Assert.GreaterOrEqual(t, StartPathRun.BootWaitSec, "the boot was not given its full wait");
            CollectionAssert.IsEmpty(ran2, "the go-home lever fired into a loading game");
            CollectionAssert.IsEmpty(ask2.Asked);
            // control: with no check (an older caller) and no names recorded, the first step alone is the start, as before
            var old = FakeReplayScreen.Recipe("r000000000000000c", new[] { "Equip" }, null, new[] { "EqGrid", "Equipment" });
            var lobby = new FakeReplayScreen();
            ToLobby(lobby);
            var asBefore = new StartPathRun(new[] { old }, new PressLog());
            t = 0;
            foreach (var _ in asBefore.Run(lobby, _ => true, () => t)) t += 0.5;
            Assert.IsTrue(asBefore.Arrived, asBefore.Why);
            Assert.AreEqual(JTokenType.Null, asBefore.ToJson()["startCheck"]!.Type);
        }
    }
}
