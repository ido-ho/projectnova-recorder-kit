using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// Fresh audit of P5 (2026-09-27), the kit's safety findings: a take behind a declared start is refused before anything
    /// runs when a lever it needs is not ticked (K1), a refused setup or reset cheat never records (K1), a <c>Name@Label</c>
    /// close is pressable by the director (K3), a declared-start take is ONE attempt (K5), a lobby-told ready gate that never
    /// opens is a boot failure (K6), a visible close is not "the start is up" (K9), and a run stopped mid-hold lets go (K11).
    /// </summary>
    public class P5AuditSafetyTests
    {
        private sealed class FakeUi : IUiDriver
        {
            public readonly HashSet<string> Names = new();
            public bool Exists(string name) => Names.Contains(name);
            public bool IsInteractable(string name) => Names.Contains(name);
            public string? GetText(string name) => null;
            public bool Click(string name, int index = 0) => true;
            public void PointerDown(string name, int index = 0) { }
            public void PointerUp(string name, int index = 0) { }
        }

        private sealed class FakeCheats : ICheatBridge
        {
            public readonly List<string> Ran = new();
            public readonly HashSet<string> Refuse = new();
            public bool Run(string command) { Ran.Add(command); return !Refuse.Contains(command); }
        }

        private sealed class FakeDriver : IRecorderDriver
        {
            public string OutputDir { get; }
            public bool IsRecording { get; private set; }
            public readonly List<string> Started = new();
            public FakeDriver(string dir) { OutputDir = dir; Directory.CreateDirectory(dir); }
            public int OutputWidth => 0;
            public int OutputHeight => 0;
            public void Start(string clipName) { IsRecording = true; Started.Add(clipName); }
            public void Stop() => IsRecording = false;
        }

        private sealed class Gate : IReadyGate
        {
            public bool Opens = true;
            public IEnumerable WaitUntilReady(DirectorContext ctx)
            {
                ctx.LastOpSucceeded = Opens;
                yield break;
            }
        }

        private string _dir = "";
        private double _clock;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "p5-audit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RelayPaths.NovaDir(_dir));
            File.WriteAllBytes(SyncNova.AdapterFile(_dir), new UTF8Encoding(false).GetBytes(@"{ ""gameId"": ""g"" }"));
            _clock = 0;
        }

        [TearDown]
        public void TearDown()
        {
            AdDirector.Active?.Finish("test teardown");
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
            catch (IOException) { }
        }

        private (AdDirector D, FakeCheats Cheats, FakeDriver Driver) Build(AdShot shot, FakeReplayScreen screen, Action<AdDirector.Options> tune,
            IReadyGate? gate = null, FakeCheats? cheats = null)
        {
            cheats ??= new FakeCheats();
            var ui = new FakeUi();
            ui.Names.Add("Board");
            var driver = new FakeDriver(Path.Combine(_dir, "out"));
            var adapter = new GameAdapter
            {
                GameId = "g",
                Ui = ui,
                CheatBridge = cheats,
                ReadyGate = gate ?? new Gate(),
                Shots = () => new[] { shot },
            };
            var options = new AdDirector.Options
            {
                AutoPump = false,
                Now = () => _clock,
                Recorder = driver,
                ReleaseCameraHold = () => { },
                Screen = screen,
            };
            tune(options);
            var d = AdDirector.Run(adapter, new[] { shot }, options)!;
            Assert.IsNotNull(d);
            return (d, cheats, driver);
        }

        private void PumpToEnd(AdDirector d)
        {
            var ticks = 0;
            while (!d.IsFinished && ticks++ < 200_000) { d.PumpOnce(); _clock += 0.1; }
            Assert.IsTrue(d.IsFinished, "director never finished");
        }

        private static FakeReplayScreen Lobby()
        {
            var s = new FakeReplayScreen();
            s.Roots.Add("Ui/Lobby");
            s.Add("Equip");
            s.OnClick["Equip"] = () => { s.Roots.Add("Ui/Equipment"); s.Add("Equipment"); s.Add("EqGrid"); };
            return s;
        }

        private static RecipeFile.Recipe Equip() =>
            FakeReplayScreen.Recipe("r000000000000000a", new[] { "Equip" }, null, new[] { "EqGrid", "Equipment" },
                new[] { new[] { "Ui/Equipment", "Ui/Lobby" } });

        private static AdShot Shot(string[]? setup = null, params AdStep[] steps) =>
            new("clip", setup ?? Array.Empty<string>(), steps.Length > 0 ? steps : new[] { AdStep.Wait(0.2) }, WaitCondition.Present("Board"));

        // ---- K1 ----

        [Test]
        public void K1_ARefusedSetupCheat_BehindADeclaredStart_FailsTheTakeByName_NothingRecorded()
        {
            var cheats = new FakeCheats();
            cheats.Refuse.Add("Give hero");
            var (d, _, driver) = Build(Shot(new[] { "Give hero" }), Lobby(), o => o.StartPath = new[] { Equip() }, cheats: cheats);
            PumpToEnd(d);
            Assert.IsFalse(d.AllCaptured);
            CollectionAssert.IsEmpty(driver.Started, "a take without its setup state is never recorded");
            Assert.AreEqual(AdDirector.FailedKindSetup, d.FailedStepKindName);
            StringAssert.Contains("'Give hero'", d.FailedReason);
        }

        [Test]
        public void K1_ARefusedResetCheat_StopsTheStart_NothingPlayedOrRecorded()
        {
            var cheats = new FakeCheats();
            cheats.Refuse.Add("Reset user");
            var screen = Lobby();
            var (d, _, driver) = Build(Shot(), screen, o => { o.StartPath = new[] { Equip() }; o.ResetCheat = "Reset user"; }, cheats: cheats);
            PumpToEnd(d);
            CollectionAssert.IsEmpty(screen.Clicked, "the start is not played from wherever the last take left the game");
            CollectionAssert.IsEmpty(driver.Started);
            Assert.AreEqual(AdDirector.FailedKindStartPath, d.FailedStepKindName);
            StringAssert.Contains("reset cheat 'Reset user'", d.FailedReason);
            Assert.IsNotNull(CaptureStart.StartStop(d));
        }

        private StartPlan PlanWithCheat(string command)
        {
            var recipe = new JObject
            {
                ["id"] = "r000000000000000a",
                ["ask"] = "a level 1 hero",
                ["learnedBy"] = "studio",
                ["runId"] = "studio",
                ["taughtAt"] = "t",
                ["declaredStart"] = new JObject { ["kind"] = "lobby-after-boot" },
                ["stamp"] = new JObject { ["gameCommit"] = null, ["kitVersion"] = "0.13.0" },
                ["steps"] = new JArray(new JObject { ["kind"] = "cheat", ["command"] = command }),
                ["dismiss"] = new JArray(),
                ["arrival"] = new JArray(),
                ["confirm"] = new JObject { ["state"] = "confirmed", ["why"] = null, ["replayRan"] = false },
                ["screens"] = new JArray(),
                ["thumbnailKey"] = null,
            };
            var doc = new JObject { ["schemaVersion"] = 1, ["gameId"] = "g", ["updatedAt"] = "t", ["recipes"] = new JArray(recipe) }
                .ToString(Newtonsoft.Json.Formatting.None);
            var plan = StartPlan.Read(new StartClaim(doc, SyncNova.Sha256OfText(doc), new[] { "r000000000000000a" }, Array.Empty<string>()), "again");
            Assert.IsNull(plan.Refusal, plan.Refusal);
            return plan;
        }

        [Test]
        public void K1_BeforeADeclaredStartTake_EveryLeverItNeeds_IsAskedFirst()
        {
            var plan = PlanWithCheat("Skip 10 Levels");
            var shot = Shot(new[] { "Hero level 1" });
            // un-ticked: the start's cheat, then the reset, then the shot's setup — each refused by name before the take
            StringAssert.Contains("'Skip 10 Levels'", CaptureStart.RefusedBeforeTake(_dir, shot, plan, "Back home"));
            Assert.IsNull(Levers.SetApproved(_dir, "Skip 10 Levels", true));
            StringAssert.Contains("'Back home'", CaptureStart.RefusedBeforeTake(_dir, shot, plan, "Back home"));
            Assert.IsNull(Levers.SetApproved(_dir, "Back home", true));
            StringAssert.Contains("'Hero level 1'", CaptureStart.RefusedBeforeTake(_dir, shot, plan, "Back home"));
            Assert.IsNull(Levers.SetApproved(_dir, "Hero level 1", true));
            Assert.IsNull(CaptureStart.RefusedBeforeTake(_dir, shot, plan, "Back home"), "every lever ticked: the take may run");
        }

        [Test]
        public void K1_ATickedButRiskyLever_IsHeldBackBeforeTheTake_UntilTheNonProductionTick()
        {
            var plan = PlanWithCheat("DeleteSave");
            Assert.IsNull(Levers.SetApproved(_dir, "DeleteSave", true));
            var refused = CaptureStart.RefusedBeforeTake(_dir, Shot(), plan, null);
            Assert.IsNotNull(refused, "a risky ticked cheat waits for the non-production tick");
            StringAssert.Contains("DeleteSave", refused);
        }

        // ---- K3 ----

        [Test]
        public void K3_ANameAtLabelClose_IsPressedByTheDirector()
        {
            var screen = new FakeReplayScreen();
            screen.Roots.Add("Ui/Board");
            screen.Roots.Add("Ui/Offer");
            screen.Add("Close", label: "Later");
            screen.OnClick["Close"] = () => { screen.Roots.Remove("Ui/Offer"); screen.Remove("Close"); };
            var (d, _, _) = Build(Shot(), screen, o => o.Dismiss = new[] { "Close@Later" });
            PumpToEnd(d);
            CollectionAssert.AreEqual(new[] { "Close@Later" }, screen.Clicked, d.Summary);
        }

        // ---- K5 ----

        [Test]
        public void K5_ADeclaredStartTake_IsOneAttempt_NeverASecondFromWhereTheFirstEnded()
        {
            var options = CaptureStart.OptionsFor(_dir, PlanWithCheat("Skip 10 Levels"), null);
            Assert.AreEqual(1, options.MaxAttempts);
            // and in a director: a settle that never holds records ONE take
            var (d, _, driver) = Build(new AdShot("clip", Array.Empty<string>(), new[] { AdStep.Wait(0.2) }, WaitCondition.Present("NeverThere")),
                Lobby(), o =>
                {
                    o.StartPath = new[] { Equip() };
                    o.MaxAttempts = CaptureStart.OptionsFor(_dir, PlanWithCheat("x"), null).MaxAttempts;
                });
            PumpToEnd(d);
            Assert.AreEqual(1, driver.Started.Count);
        }

        // ---- K6 ----

        [Test]
        public void K6_ALobbyToldReadyGateThatNeverOpens_IsABootFailure_TheRestartRuleRetriesTheBoot()
        {
            var (d, _, driver) = Build(Shot(), Lobby(), o => o.StartPath = new[] { Equip() }, gate: new Gate { Opens = false });
            PumpToEnd(d);
            CollectionAssert.IsEmpty(driver.Started);
            Assert.AreEqual(StartPathRun.KindBootStuck, CaptureStart.StopKind(d, firstTakeAfterBoot: true));
            Assert.IsNotNull(CaptureStart.StartStop(d), "the take is failed in the start's words");
            Assert.AreEqual(RestartRule.Move.BootRetry, RestartRule.AfterStartStopped(CaptureStart.StopKind(d, firstTakeAfterBoot: true), 0));
        }

        // ---- K9 ----

        [Test]
        public void K9_AKnownCloseOnScreen_IsNotTheStart_ItsFirstStepMustBe()
        {
            var screen = new FakeReplayScreen();
            screen.Roots.Add("Ui/Hud");
            // a generic Close that is part of the screen (pressing it changes nothing); the recipe's first element never shows
            screen.Add("Close");
            var recipe = FakeReplayScreen.Recipe("r000000000000000a", new[] { "Equip" }, new[] { "Close" }, new[] { "EqGrid", "Equipment" });
            var run = new StartPathRun(new[] { recipe }, new PressLog());
            var t = 0.0;
            foreach (var _ in run.Run(screen, _ => true, () => t)) t += 0.5;
            Assert.AreEqual(StartPathRun.KindBootStuck, run.FailedKind, run.Why);
        }

        // ---- K2 ----

        /// <summary>A lobby whose Equipment panel has its OWN "Close" — the same name as the popup close the teach learned.</summary>
        private static (FakeReplayScreen Screen, RecipeFile.Recipe Recipe) DestinationWithItsOwnClose()
        {
            var s = new FakeReplayScreen();
            s.Roots.Add("Ui/Lobby");
            s.Add("Equip");
            s.OnClick["Equip"] = () => { s.Roots.Add("Ui/Equipment"); s.Add("Equipment"); s.Add("EqGrid"); s.Add("Close"); };
            s.OnClick["Close"] = () => { s.Roots.Remove("Ui/Equipment"); s.Remove("Equipment"); s.Remove("EqGrid"); s.Remove("Close"); };
            var r = FakeReplayScreen.Recipe("r000000000000000a", new[] { "Equip" }, new[] { "Close" }, new[] { "EqGrid", "Equipment" },
                new[] { new[] { "Ui/Equipment", "Ui/Lobby" } });
            r.Screens[0].Buttons.AddRange(new[] { "Close", "EqGrid" });
            return (s, r);
        }

        [Test]
        public void K2_TheStartPath_ChecksArrivalBeforeAnyClose_AndNeverClosesTheDestinationsOwnButton()
        {
            var (screen, recipe) = DestinationWithItsOwnClose();
            var run = new StartPathRun(new[] { recipe }, new PressLog());
            var t = 0.0;
            foreach (var _ in run.Run(screen, _ => true, () => t)) t += 0.1;
            Assert.IsTrue(run.Arrived, run.Why);
            CollectionAssert.AreEqual(new[] { "Equip" }, screen.Clicked, "the Equipment panel's own Close is never pressed");
        }

        [Test]
        public void K2_TheDirector_PressesNoCloseWhileRecording_AndNotTheDestinationsBeforeIt()
        {
            var (screen, recipe) = DestinationWithItsOwnClose();
            var raised = false;
            var (d, _, driver) = Build(Shot(null, AdStep.Wait(1.5), AdStep.Wait(0.3)), screen, o =>
            {
                o.StartPath = new[] { recipe };
                o.Dismiss = new[] { "StreakClose" };
            });
            var ticks = 0;
            while (!d.IsFinished && ticks++ < 200_000)
            {
                d.PumpOnce();
                // a popup with a known close rises once the recorder runs, and stays up through the take
                if (!raised && driver.IsRecording) { raised = true; screen.Popup("Ui/Streak", "StreakClose"); }
                _clock += 0.1;
            }
            Assert.IsTrue(raised, "the popup rose during the take");
            CollectionAssert.AreEqual(new[] { "Equip" }, screen.Clicked, d.Summary);
        }

        // ---- K4 ----

        [Test]
        public void K4_AStepThatOpensSomethingWithoutChangingTheRoots_IsSaid_ScreensBlind()
        {
            var s = new FakeReplayScreen();
            s.Roots.Add("Canvas/SafeArea");
            s.Add("Equip");
            s.OnClick["Equip"] = () => { s.Add("Equipment"); s.Add("EqGrid"); };
            var recipe = FakeReplayScreen.Recipe("r000000000000000a", new[] { "Equip" }, null, new[] { "EqGrid", "Equipment" });
            var run = new StartPathRun(new[] { recipe }, new PressLog());
            var t = 0.0;
            foreach (var _ in run.Run(s, _ => true, () => t)) t += 0.1;
            Assert.IsTrue(run.Arrived, run.Why);
            Assert.IsTrue(run.ScreensBlind);
            Assert.IsTrue(run.ToJson()["screensBlind"]!.Value<bool>());
            // and a game whose roots DO change is not blind
            var lobby = Lobby();
            var seeing = new StartPathRun(new[] { Equip() }, new PressLog());
            t = 0;
            foreach (var _ in seeing.Run(lobby, _ => true, () => t)) t += 0.1;
            Assert.IsFalse(seeing.ScreensBlind);
        }

        // ---- K7 / K8 ----

        private static string Big(char c) => new string(c, DirectorEdges.MaxKey);

        [Test]
        public void K7_ACapturesDoneReport_FitsTheApisJsonLimit_WhateverTheEdges_AndSaysWhatWasDropped()
        {
            var job = CaptureJob.FromJson(JObject.Parse(@"{ ""runId"": ""r1"", ""kind"": ""capture"", ""items"": [ { ""shot"": ""s"", ""take"": 1 } ] }"), out _)!;
            var p = CaptureProgress.Start(job);
            for (var take = 0; take < 3; take++)
            {
                var log = new PressLog();
                for (var i = 0; i < DirectorEdges.Max; i++) log.Edges.Add(Big('a'), "P" + i, Big('b'));
                Assert.LessOrEqual(log.Edges.Bytes, DirectorEdges.MaxBytes, "one run's edges are capped by bytes too");
                Assert.Greater(log.Edges.Dropped, 0);
                p.AddEdges(log);
            }
            var done = p.DoneReportJson();
            Assert.Less(Encoding.UTF8.GetByteCount(done), 100 * 1024, "under Express's 100 KB JSON default");
            Assert.Greater(JObject.Parse(done)["edgesDropped"]!.Value<int>(), 0, "what did not fit is counted");
        }

        [Test]
        public void K8_AnAutoTrysFacts_CarryTheStartsEdgesAndFlags_ThroughTheCaps_WithTheDroppedCount()
        {
            var request = AutoTryRequest.FromJson(new JObject
            {
                ["need"] = new JObject { ["kind"] = "screen", ["name"] = "Equipment" },
                ["candidates"] = new JArray("EquipBtn"),
                ["authoredNames"] = new JArray("EqGrid", "Equipment"),
                ["dismiss"] = new JArray(),
                ["start"] = null,
            })!;
            var run = new AutoTryRun(request);
            for (var i = 0; i < 150; i++) run.Presses.Edges.Add("a", "Run" + i, "b");
            var start = new PressLog();
            for (var i = 0; i < 150; i++) start.Edges.Add("a", "Start" + i, "b");
            start.Flags.Add(new DismissFlag { Dismiss = "DailyClose", Path = "Save.Gems", Before = "1", After = "2" });
            run.StartPresses = start;
            var facts = JObject.Parse(run.ToFactsJson("0.13.0", null, null, null));
            Assert.AreEqual(DirectorEdges.Max, ((JArray)facts["edges"]!).Count, "never past the cap");
            Assert.AreEqual("Start0", facts["edges"]![0]!["press"]!.Value<string>(), "the start's presses first");
            Assert.AreEqual(100, facts["edgesDropped"]!.Value<int>());
            Assert.AreEqual("DailyClose", facts["dismissFlags"]![0]!["dismiss"]!.Value<string>(), "the start's flags ride too");
            // a recipe Try says its dropped count too
            Assert.AreEqual(0, JObject.Parse(RecipeTryJob.FactsJson("0.13.0", null, PlanWithCheat("x"), null))["edgesDropped"]!.Value<int>());
        }

        [Test]
        public void K8_AnAutoTrysOwnCloses_AreWatched_WhenItStartsFromADeclaredStart()
        {
            var request = AutoTryRequest.FromJson(new JObject
            {
                ["need"] = new JObject { ["kind"] = "screen", ["name"] = "Equipment" },
                ["candidates"] = new JArray("EquipBtn"),
                ["authoredNames"] = new JArray("EqGrid", "Equipment"),
                ["dismiss"] = new JArray("DailyClose"),
                ["start"] = new JObject { ["recipes"] = "{}", ["sha256"] = "s", ["recipeIds"] = new JArray("r000000000000000a"), ["watch"] = new JArray("Save.Gems") },
            })!;
            Assert.IsNotNull(request.Start);
            var gems = 10;
            var screen = new FakeReplayScreen();
            screen.Roots.Add("Ui/Lobby");
            screen.Add("EquipBtn");
            screen.OnClick["EquipBtn"] = () => { screen.Add("Equipment"); screen.Add("EqGrid"); screen.Roots.Add("Ui/Equipment"); };
            screen.Popup("Ui/Daily", "DailyClose");
            var close = screen.OnClick["DailyClose"];
            screen.OnClick["DailyClose"] = () => { close(); gems = 60; };
            var run = new AutoTryRun(request, _ => gems.ToString());
            var t = 0.0;
            foreach (var _ in run.Run(screen, () => t, waitForBoot: false)) t += 0.1;
            Assert.AreEqual(1, run.Presses.Flags.Count, "a close that moved a watched value is flagged");
        }

        // ---- round 2 ----

        [Test]
        public void R1_ARefusedSetupCheat_OnAPlainTake_FailsItByName_NothingRecorded()
        {
            var cheats = new FakeCheats();
            cheats.Refuse.Add("Give hero");
            var (d, _, driver) = Build(Shot(new[] { "Give hero" }), new FakeReplayScreen(), _ => { }, cheats: cheats);
            PumpToEnd(d);
            Assert.IsFalse(d.AllCaptured);
            CollectionAssert.IsEmpty(driver.Started, "no declared start, and still never recorded without its setup state");
            Assert.AreEqual(AdDirector.FailedKindSetup, d.FailedStepKindName);
        }

        [Test]
        public void R3_ADestinationThatAnimatesIn_IsAwaited_NotFailedAtOneSample()
        {
            var s = new FakeReplayScreen();
            s.Roots.Add("Ui/Lobby");
            s.Add("Equip");
            var t = 0.0;
            double? clickedAt = null;
            s.OnClick["Equip"] = () => { s.Roots.Add("Ui/Equipment"); clickedAt = t; };
            // the panel's names appear 3 s after the click (after the step's 1.5 s settle)
            s.OnQuery = () => { if (clickedAt != null && t - clickedAt >= 3 && !s.Elements.Any(e => e.Name == "EqGrid")) { s.Add("Equipment"); s.Add("EqGrid"); } };
            var run = new StartPathRun(new[] { Equip() }, new PressLog());
            foreach (var _ in run.Run(s, _ => true, () => t)) t += 0.1;
            Assert.IsTrue(run.Arrived, run.Why);
            // …and one that never comes is still "did not arrive", within the bounded wait
            var never = Lobby();
            never.OnClick["Equip"] = () => never.Roots.Add("Ui/Equipment");
            var no = new StartPathRun(new[] { Equip() }, new PressLog());
            t = 0;
            foreach (var _ in no.Run(never, _ => true, () => t)) t += 0.1;
            Assert.AreEqual(StartPathRun.KindArrival, no.FailedKind);
            Assert.Less(t, StartPathRun.AfterStepSec + StartPathRun.ArrivalWaitSec + 5);
        }

        private sealed class OrderGate : IReadyGate
        {
            public FakeCheats? Cheats;
            public bool ResetBeforeGate;
            public bool Opens = true;
            public IEnumerable WaitUntilReady(DirectorContext ctx)
            {
                ResetBeforeGate = Cheats!.Ran.Contains("Back home");
                ctx.LastOpSucceeded = Opens;
                yield break;
            }
        }

        [Test]
        public void R4_TheInSessionReset_RunsBeforeTheReadyGate_AndALaterTakesClosedGateIsNotABootToRetry()
        {
            var cheats = new FakeCheats();
            var gate = new OrderGate { Cheats = cheats };
            var (d, _, _) = Build(Shot(), Lobby(), o => { o.StartPath = new[] { Equip() }; o.ResetCheat = "Back home"; }, gate: gate, cheats: cheats);
            PumpToEnd(d);
            Assert.IsTrue(gate.ResetBeforeGate, "the reset brings the game home before the lobby-told gate looks");
            Assert.IsTrue(d.StartPath!.Arrived, d.Summary);
            var closed = new OrderGate { Cheats = new FakeCheats(), Opens = false };
            var (d2, _, _) = Build(Shot(), Lobby(), o => o.StartPath = new[] { Equip() }, gate: closed, cheats: closed.Cheats);
            PumpToEnd(d2);
            Assert.IsNull(CaptureStart.StopKind(d2, firstTakeAfterBoot: false), "a later take's closed gate is not a boot stuck loading");
            Assert.AreEqual(RestartRule.Move.GiveUp, RestartRule.AfterStartStopped(CaptureStart.StopKind(d2, firstTakeAfterBoot: false), 0));
            Assert.AreEqual(StartPathRun.KindBootStuck, CaptureStart.StopKind(d2, firstTakeAfterBoot: true));
        }

        [Test]
        public void R6_AfterTheShotsOwnSetupCheats_NoCloseIsPressedBeforeRecording()
        {
            var screen = Lobby();
            screen.Popup("Ui/Streak", "StreakClose");
            // the teach saw the streak popup up after its step (so the start path does not stop on it)
            var recipe = FakeReplayScreen.Recipe("r000000000000000a", new[] { "Equip" }, null, new[] { "EqGrid", "Equipment" },
                new[] { new[] { "Ui/Equipment", "Ui/Lobby", "Ui/Streak" } });
            var (d, _, _) = Build(Shot(new[] { "Hero level 1" }), screen, o => { o.StartPath = new[] { recipe }; o.Dismiss = new[] { "StreakClose" }; });
            PumpToEnd(d);
            CollectionAssert.DoesNotContain(screen.Clicked, "StreakClose", d.Summary);
            StringAssert.Contains("no popup close before recording", d.Summary);
        }

        [Test]
        public void R11_TheTeachConfirmReplay_DoesNotCountAVisibleCloseAsItsStart()
        {
            var s = new FakeReplayScreen();
            s.Add("Close"); // a generic Close that is part of the screen; the recipe's first element never shows
            var replay = new RecipeReplay(new JArray(new JObject { ["kind"] = "click", ["name"] = "Equip" }), new[] { "Close" });
            Assert.IsFalse(replay.StartIsUp(s));
            var t = 0.0;
            foreach (var _ in replay.Run(s, _ => true, () => t)) t += 0.5;
            StringAssert.Contains("never came up", replay.Steps[0]!["refused"]!.Value<string>());
        }

        // ---- K11 ----

        [Test]
        public void K11_ARunStoppedMidHold_LetsGo()
        {
            var screen = Lobby();
            var hold = FakeReplayScreen.Recipe("r000000000000000a", new string[0], null, new[] { "EqGrid", "Equipment" });
            hold.Steps = new JArray(new JObject { ["kind"] = "hold", ["name"] = "Equip", ["seconds"] = 30 });
            var (d, _, _) = Build(Shot(), screen, o => o.StartPath = new[] { hold });
            var ticks = 0;
            while (!screen.Clicked.Contains("down:Equip") && ticks++ < 10_000) { d.PumpOnce(); _clock += 0.1; }
            CollectionAssert.Contains(screen.Clicked, "down:Equip");
            d.Finish("stopped from outside");
            CollectionAssert.Contains(screen.Clicked, "up:Equip", "a stopped hold lets go");
        }
    }
}
