using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// v3 P5 (§3.6, §3.6a items 1, 4, 5) — THE DECLARED START AND THE DISMISS LOOP, IN THE DIRECTOR: the start path runs
    /// after the ready gate (told the work starts at the lobby) and before any shot, recording off; its stop is the run's
    /// stop; the dismiss loop runs once before the recorder starts and never while it records; every press is an edge;
    /// a close that moves a watched value is flagged; the in-session reset runs first; a tutorial gate AND a declared start
    /// are refused whole.
    /// </summary>
    public class DirectorStartPathTests
    {
        private sealed class FakeUi : IUiDriver
        {
            public readonly HashSet<string> Names = new();
            public readonly List<string> Clicked = new();
            public bool Exists(string name) => Names.Contains(name);
            public bool IsInteractable(string name) => Names.Contains(name);
            public string? GetText(string name) => null;
            public bool Click(string name, int index = 0) { Clicked.Add(name); return true; }
            public void PointerDown(string name, int index = 0) { }
            public void PointerUp(string name, int index = 0) { }
        }

        private sealed class FakeCheats : ICheatBridge
        {
            public readonly List<string> Ran = new();
            /// <summary>What a cheat does to the game (kit 0.14.1: the go-home lever puts it on the lobby).</summary>
            public readonly Dictionary<string, Action> Effects = new();
            public bool Run(string command)
            {
                Ran.Add(command);
                if (Effects.TryGetValue(command, out var e)) e();
                return true;
            }
        }

        private sealed class FakeDriver : IRecorderDriver
        {
            public string OutputDir { get; }
            public bool IsRecording { get; private set; }
            public readonly List<string> Started = new();
            public Action? OnStart;
            public FakeDriver(string dir) { OutputDir = dir; Directory.CreateDirectory(dir); }
            public int OutputWidth => 0;
            public int OutputHeight => 0;
            public void Start(string clipName)
            {
                OnStart?.Invoke();
                IsRecording = true;
                Started.Add(clipName);
                File.WriteAllText(Path.Combine(OutputDir, clipName + "_001.mp4"), "clip");
            }
            public void Stop() => IsRecording = false;
        }

        private sealed class SeeingGate : IReadyGate
        {
            public readonly List<string> Baselines = new();
            public IEnumerable WaitUntilReady(DirectorContext ctx)
            {
                foreach (var s in ctx.UpcomingShots) Baselines.Add(s.Name + ":" + s.Baseline);
                ctx.LastOpSucceeded = true;
                yield break;
            }
        }

        private string _outDir = "";
        private double _clock;

        [SetUp]
        public void SetUp()
        {
            _outDir = Path.Combine(Path.GetTempPath(), "director-start-" + Guid.NewGuid().ToString("N"));
            _clock = 0;
        }

        [TearDown]
        public void TearDown()
        {
            AdDirector.Active?.Finish("test teardown");
            if (Directory.Exists(_outDir)) Directory.Delete(_outDir, recursive: true);
        }

        private sealed class Rig
        {
            public AdDirector D = null!;
            public FakeUi Ui = null!;
            public FakeCheats Cheats = null!;
            public FakeDriver Driver = null!;
            public SeeingGate Gate = null!;
        }

        private Rig Start(AdShot shot, FakeReplayScreen screen, Action<AdDirector.Options>? tune = null)
        {
            var rig = new Rig { Ui = new FakeUi(), Cheats = new FakeCheats(), Driver = new FakeDriver(_outDir), Gate = new SeeingGate() };
            rig.Ui.Names.Add("Board");
            var adapter = new GameAdapter
            {
                GameId = "testgame",
                Ui = rig.Ui,
                CheatBridge = rig.Cheats,
                ReadyGate = rig.Gate,
                Shots = () => new[] { shot },
            };
            var options = new AdDirector.Options
            {
                AutoPump = false,
                Now = () => _clock,
                Recorder = rig.Driver,
                ReleaseCameraHold = () => { },
                Screen = screen,
            };
            tune?.Invoke(options);
            rig.D = AdDirector.Run(adapter, new[] { shot }, options)!;
            Assert.IsNotNull(rig.D);
            var ticks = 0;
            while (!rig.D.IsFinished && ticks++ < 200_000)
            {
                rig.D.PumpOnce();
                _clock += 0.1;
            }
            Assert.IsTrue(rig.D.IsFinished, "director never finished");
            return rig;
        }

        private static AdShot Shot(params AdStep[] steps) =>
            new AdShot("clip", Array.Empty<string>(), steps.Length > 0 ? steps : new[] { AdStep.Wait(0.2) }, WaitCondition.Present("Board"));

        private static FakeReplayScreen Lobby()
        {
            var s = new FakeReplayScreen();
            s.Roots.Add("Ui/Lobby");
            s.Add("Equip");
            s.OnClick["Equip"] = () =>
            {
                s.Roots.Add("Ui/Equipment");
                s.Add("Equipment");
                s.Add("EqGrid");
            };
            return s;
        }

        private static RecipeFile.Recipe EquipRecipe(string[]? dismiss = null) =>
            FakeReplayScreen.Recipe("r000000000000000a", new[] { "Equip" }, dismiss, new[] { "EqGrid", "Equipment" },
                new[] { new[] { "Ui/Equipment", "Ui/Lobby" } });

        [Test]
        public void TheDeclaredStart_RunsAfterTheLobbyToldGate_AndBeforeTheShot_RecordingOff()
        {
            var screen = Lobby();
            var clickedAtRecord = -1;
            var rig = Start(Shot(), screen, o =>
            {
                o.StartPath = new[] { EquipRecipe() };
                ((FakeDriver)o.Recorder!).OnStart = () => clickedAtRecord = screen.Clicked.Count;
            });
            Assert.IsTrue(rig.D.AllCaptured, rig.D.Summary);
            Assert.AreEqual(1, clickedAtRecord, "the start path is played before the recorder starts");
            CollectionAssert.AreEqual(new[] { AdDirector.StartPathShotName + ":" + AdShot.BaselineLobby }, rig.Gate.Baselines);
            Assert.IsTrue(rig.D.StartPath!.Arrived);
            CollectionAssert.AreEqual(new[] { "Equip" }, screen.Clicked);
            Assert.AreEqual(1, rig.D.Presses.Edges.List.Count, "the start path's press is an edge");
            StringAssert.Contains("OK   declared start reached", rig.D.Summary);
        }

        [Test]
        public void KitJobs_CheckTheDeclaredStartsScreenFirst_TheGoHomeLeverThroughTheLeverGate_ThenThePerson()
        {
            // the game resumed on the board: the lever (a ticked cheat, through the run's own bridge) puts it on the lobby
            var screen = new FakeReplayScreen();
            screen.Roots.Add("Ui/Board");
            screen.Add("Roll_Btn");
            var recipe = EquipRecipe();
            recipe.StartNames = new List<string> { "Equip", "Lobby_Banner", "Lobby_Chest" };
            void ToLobby()
            {
                var lobby = Lobby();
                screen.Elements.Clear();
                screen.Roots.Clear();
                foreach (var r in lobby.Roots) screen.Roots.Add(r);
                foreach (var e in lobby.Elements) screen.Elements.Add(e);
                screen.Add("Lobby_Banner");
                screen.Add("Lobby_Chest");
                screen.OnClick["Equip"] = () => { screen.Roots.Add("Ui/Equipment"); screen.Add("Equipment"); screen.Add("EqGrid"); };
            }
            var screen2 = screen;
            var rig2 = StartWith(Shot(), screen2, c => c.Effects["go-home"] = ToLobby, o =>
            {
                o.StartPath = new[] { recipe };
                CaptureStart.WithStartCheck(o, "go-home", new FakeAsk());
            });
            Assert.IsTrue(rig2.D.AllCaptured, rig2.D.Summary);
            CollectionAssert.Contains(rig2.Cheats.Ran, "go-home");
            Assert.IsTrue(rig2.D.StartPath!.CheckRun!.HomeRan);
            // without the lever and nobody at the editor: the run stops in the check's one sentence, nothing recorded
            _clock = 0;
            var screen3 = new FakeReplayScreen();
            screen3.Roots.Add("Ui/Board");
            screen3.Add("Roll_Btn");
            var rig3 = Start(Shot(), screen3, o =>
            {
                o.StartPath = new[] { recipe };
                CaptureStart.WithStartCheck(o, null, new FakeAsk());
            });
            Assert.AreEqual(StartPathRun.KindStartScreen, rig3.D.StartPath!.FailedKind, rig3.D.Summary);
            StringAssert.Contains("Nobody pressed Continue", rig3.D.FailedReason);
            CollectionAssert.IsEmpty(rig3.Driver.Started);
        }

        /// <summary>The rig, with the fake bridge's cheat effects wired before the director runs.</summary>
        private Rig StartWith(AdShot shot, FakeReplayScreen screen, Action<FakeCheats> wire, Action<AdDirector.Options>? tune = null)
        {
            var rig = new Rig { Ui = new FakeUi(), Cheats = new FakeCheats(), Driver = new FakeDriver(_outDir), Gate = new SeeingGate() };
            wire(rig.Cheats);
            rig.Ui.Names.Add("Board");
            var adapter = new GameAdapter { GameId = "testgame", Ui = rig.Ui, CheatBridge = rig.Cheats, ReadyGate = rig.Gate, Shots = () => new[] { shot } };
            var options = new AdDirector.Options { AutoPump = false, Now = () => _clock, Recorder = rig.Driver, ReleaseCameraHold = () => { }, Screen = screen };
            tune?.Invoke(options);
            rig.D = AdDirector.Run(adapter, new[] { shot }, options)!;
            Assert.IsNotNull(rig.D);
            var ticks = 0;
            while (!rig.D.IsFinished && ticks++ < 200_000)
            {
                rig.D.PumpOnce();
                _clock += 0.1;
            }
            Assert.IsTrue(rig.D.IsFinished, "director never finished");
            return rig;
        }

        [Test]
        public void AStopInTheDeclaredStart_IsTheRunsStop_AndNoShotIsRecorded()
        {
            var screen = Lobby();
            var equip = screen.OnClick["Equip"];
            screen.OnClick["Equip"] = () => { equip(); screen.Roots.Add("Ui/SpendOffer"); screen.Add("OfferBuy"); };
            var rig = Start(Shot(), screen, o => o.StartPath = new[] { EquipRecipe() });
            Assert.IsFalse(rig.D.AllCaptured);
            CollectionAssert.IsEmpty(rig.Driver.Started, "nothing is recorded behind a start that did not arrive");
            Assert.AreEqual(AdDirector.FailedKindStartPath, rig.D.FailedStepKindName);
            Assert.IsNull(rig.D.FailedStepIndex);
            Assert.AreEqual(StartPathRun.KindUnknownOverlay, rig.D.StartPath!.FailedKind);
            StringAssert.Contains("unknown-overlay", rig.D.FailedReason);
        }

        [Test]
        public void ATutorialGateAndADeclaredStart_AreRefusedWhole_NothingRuns()
        {
            var screen = Lobby();
            var gate = new AdShot("tutorial-gate", new[] { "SkipTutorial" }, new[] { AdStep.Wait(0.1) }, WaitCondition.Present("Board"));
            var rig = Start(Shot(), screen, o => { o.StartPath = new[] { EquipRecipe() }; o.Preamble = gate; });
            Assert.AreEqual(AdDirector.FailedKindStartPath, rig.D.FailedStepKindName);
            StringAssert.Contains("both a tutorial gate and a declared start", rig.D.FailedReason);
            CollectionAssert.IsEmpty(rig.Cheats.Ran);
            CollectionAssert.IsEmpty(screen.Clicked);
            CollectionAssert.IsEmpty(rig.Driver.Started);
            CollectionAssert.IsEmpty(rig.Gate.Baselines, "refused before the ready gate");
        }

        [Test]
        public void TheDismissLoop_RunsBeforeTheRecorderStarts_AndNeverWhileItRecords()
        {
            var screen = new FakeReplayScreen();
            screen.Roots.Add("Ui/Board");
            screen.Popup("Ui/Welcome", "WelcomeClose");
            var raised = false;
            var dismissedAtRecord = -1;
            var rig = Start(Shot(AdStep.Wait(1.5), AdStep.Wait(0.3)), screen, o =>
            {
                o.Dismiss = new[] { "WelcomeClose", "StreakClose" };
                // a second popup rises the moment the recorder runs, and is up through both steps
                ((FakeDriver)o.Recorder!).OnStart = () =>
                {
                    dismissedAtRecord = screen.Clicked.Count;
                    screen.Popup("Ui/Streak", "StreakClose");
                    raised = true;
                };
            });
            Assert.IsTrue(rig.D.AllCaptured, rig.D.Summary);
            Assert.AreEqual(1, dismissedAtRecord, "the welcome popup is closed before the recorder starts");
            // fresh audit of P5, K2: nothing is pressed while the recorder runs — the streak popup that rose mid-take stays
            Assert.IsTrue(raised);
            CollectionAssert.AreEqual(new[] { "WelcomeClose" }, rig.D.Presses.Dismissed);
        }

        [Test]
        public void ACloseWithARiskyWord_IsNeverPressedBeforeTheRecording()
        {
            var screen = new FakeReplayScreen();
            screen.Popup("Ui/Offer", "BuyNowClose");
            var rig = Start(Shot(AdStep.Wait(0.2), AdStep.Wait(0.2)), screen, o => o.Dismiss = new[] { "BuyNowClose" });
            CollectionAssert.IsEmpty(screen.Clicked);
            StringAssert.Contains("dismiss 'BuyNowClose' not pressed: PRESS REFUSED", rig.D.Summary);
        }

        [Test]
        public void EveryShotPressIsAnEdge_WhenTheRunRecordsEdges()
        {
            var screen = new FakeReplayScreen();
            screen.Roots.Add("Ui/Board");
            var rig = Start(Shot(AdStep.Click("Board")), screen, o => o.RecordEdges = true);
            Assert.AreEqual(1, rig.D.Presses.Edges.List.Count);
            Assert.AreEqual(("Ui/Board", "Board", "Ui/Board"), rig.D.Presses.Edges.List[0]);
            // and a run that does not record edges records none of the shot's
            var quiet = Start(Shot(AdStep.Click("Board")), new FakeReplayScreen());
            CollectionAssert.IsEmpty(quiet.D.Presses.Edges.List);
        }

        [Test]
        public void ACloseThatMovesAWatchedValue_IsFlaggedByTheDirector_AndTheShotStillRuns()
        {
            var screen = new FakeReplayScreen();
            var gems = 10;
            screen.Popup("Ui/Daily", "DailyClose");
            var close = screen.OnClick["DailyClose"];
            screen.OnClick["DailyClose"] = () => { close(); gems = 60; };
            var rig = Start(Shot(), screen, o =>
            {
                o.Dismiss = new[] { "DailyClose" };
                o.WatchGets = new[] { "Save.Gems" };
                o.ReadWatch = _ => gems.ToString();
            });
            Assert.IsTrue(rig.D.AllCaptured, rig.D.Summary);
            Assert.AreEqual(1, rig.D.Presses.Flags.Count);
            Assert.AreEqual("60", rig.D.Presses.Flags[0].After);
        }

        [Test]
        public void TheInSessionReset_RunsThroughTheGate_BeforeTheDeclaredStart()
        {
            var screen = Lobby();
            var clicksAtReset = -1;
            var rig = Start(Shot(), screen, o =>
            {
                o.StartPath = new[] { EquipRecipe() };
                o.ResetCheat = "Reset user";
            });
            clicksAtReset = rig.Cheats.Ran.IndexOf("Reset user");
            Assert.GreaterOrEqual(clicksAtReset, 0, rig.D.Summary);
            Assert.IsTrue(rig.D.StartPath!.Arrived);
            StringAssert.Contains("reset: 'Reset user' before the declared start", rig.D.Summary);
            Assert.Less(rig.D.Summary.IndexOf("reset: 'Reset user'", StringComparison.Ordinal),
                rig.D.Summary.IndexOf("declared start: ", StringComparison.Ordinal));
        }

        [Test]
        public void ACheatStepOfTheDeclaredStart_GoesThroughTheRunsGatedBridge()
        {
            var screen = Lobby();
            var state = FakeReplayScreen.Recipe("r000000000000000b", new string[0]);
            state.Steps = new Newtonsoft.Json.Linq.JArray(new Newtonsoft.Json.Linq.JObject { ["kind"] = "cheat", ["command"] = "Skip 10 Levels" });
            var rig = Start(Shot(), screen, o => o.StartPath = new[] { state });
            CollectionAssert.Contains(rig.Cheats.Ran, "Skip 10 Levels");
            Assert.IsTrue(rig.D.AllCaptured, rig.D.Summary);
        }
    }
}
