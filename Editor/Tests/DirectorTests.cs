using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace ProjectNova.RecorderKit.Tests
{
    public class DirectorTests
    {
        private sealed class FakeUi : IUiDriver
        {
            public readonly HashSet<string> Names = new();
            public readonly List<string> Clicked = new();
            public bool Exists(string name) => Names.Contains(name);
            public readonly HashSet<string> Locked = new();
            public bool IsInteractable(string name) => Names.Contains(name) && !Locked.Contains(name);
            public string? GetText(string name) => null;
            public bool Click(string name, int index = 0) { Clicked.Add(name); return true; }
            public void PointerDown(string name, int index = 0) { }
            public void PointerUp(string name, int index = 0) { }
        }

        private sealed class FakeState : IStateProbe
        {
            public string? CurrentStateName { get; set; }
        }

        /// <summary>A state that catches up on its own schedule, like an animation finishing.</summary>
        private sealed class LaggingState : IStateProbe
        {
            public Func<double> Now = () => 0;
            public double SettlesAt;
            public string Before = "";
            public string After = "";
            public string? CurrentStateName => Now() >= SettlesAt ? After : Before;
        }

        private sealed class FakeCheats : ICheatBridge
        {
            public readonly List<string> Run_ = new();
            public bool Run(string command) { Run_.Add(command); return true; }
        }

        private sealed class FakeDriver : IRecorderDriver
        {
            public string OutputDir { get; }
            public bool IsRecording { get; private set; }
            public readonly List<string> Started = new();
            public FakeDriver(string dir) { OutputDir = dir; Directory.CreateDirectory(dir); }
            // 0x0 keeps CaptureAspect quiet in tests (it treats non-positive sizes as "unknown"),
            // so director tests assert on shot logic rather than on an editor's window size.
            public int OutputWidth => 0;
            public int OutputHeight => 0;
            public void Start(string clipName)
            {
                IsRecording = true;
                Started.Add(clipName);
                File.WriteAllText(Path.Combine(OutputDir, clipName + "_001.mp4"), "clip");
            }
            public void Stop() => IsRecording = false;
        }

        /// <summary>Ready gate that only passes once Recover() has run (the stranded-screen case).</summary>
        private sealed class GateNeedingRecovery : IReadyGate
        {
            public bool BaselineReached;
            public int Attempts;
            public IEnumerable WaitUntilReady(DirectorContext ctx)
            {
                Attempts++;
                ctx.LastOpSucceeded = BaselineReached;
                yield break;
            }
        }

        /// <summary>A gate that routes on each upcoming shot's declared baseline — the seam that lets a
        /// game establish where a shot must START without a parallel name-list to keep in sync. This is
        /// the exact shape of RlReadyGate's WantsLobbyBaseline, reduced to the one fact under test.</summary>
        private sealed class BaselineRoutingGate : IReadyGate
        {
            public readonly List<(string Name, string Baseline)> Seen = new();
            public bool WantsLobby;
            public IEnumerable WaitUntilReady(DirectorContext ctx)
            {
                foreach (var s in ctx.UpcomingShots)
                    Seen.Add((s.Name, s.Baseline));
                WantsLobby = ctx.UpcomingShots.Count > 0
                    && ctx.UpcomingShots.All(s => s.Baseline == AdShot.BaselineLobby);
                ctx.LastOpSucceeded = true;
                yield break;
            }
        }

        private sealed class CountingRecovery : IRecoveryPolicy
        {
            public int Calls;
            public Action? OnRecover;
            public IEnumerable Recover(DirectorContext ctx)
            {
                Calls++;
                OnRecover?.Invoke();
                ctx.LastOpSucceeded = true;
                yield break;
            }
        }

        private sealed class FakeVision : IVisionChannel
        {
            public bool? Answer; // null = never answers
            public string? LastPrompt;
            public int? LastStep;
            public string? Request(string prompt, int stepIndex) { LastPrompt = prompt; LastStep = stepIndex; return "v1"; }
            public bool TryGetResult(string id, out bool match)
            {
                match = Answer ?? false;
                return Answer != null;
            }
        }

        private string _outDir = "";
        private double _clock;

        [SetUp]
        public void SetUp()
        {
            _outDir = Path.Combine(Path.GetTempPath(), "director-test-" + Guid.NewGuid().ToString("N"));
            _clock = 0;
        }

        [TearDown]
        public void TearDown()
        {
            AdDirector.Active?.Finish("test teardown");
            Directory.Delete(_outDir, recursive: true);
        }

        private (AdDirector d, FakeUi ui, FakeCheats cheats, FakeDriver driver, FakeVision vision)
            Start(AdShot[] shots, IStateProbe? state = null, bool skipVision = false,
                IReadyGate? readyGate = null, IRecoveryPolicy? recovery = null,
                Action? releaseCameraHold = null)
        {
            var ui = new FakeUi();
            var cheats = new FakeCheats();
            var driver = new FakeDriver(_outDir);
            var vision = new FakeVision();
            var adapter = new GameAdapter
            {
                GameId = "testgame",
                Ui = ui,
                StateProbe = state ?? new FakeState(),
                CheatBridge = cheats,
                ReadyGate = readyGate ?? NullReadyGate.Instance,
                Recovery = recovery ?? NullRecoveryPolicy.Instance,
                Shots = () => shots,
            };
            var d = AdDirector.Run(adapter, shots, new AdDirector.Options
            {
                AutoPump = false,
                Now = () => _clock,
                Recorder = driver,
                Vision = vision,
                SkipVisionShots = skipVision,
                // Non-hold tests must not CameraPose.Release the KitTestHost project.
                ReleaseCameraHold = releaseCameraHold ?? (() => { }),
            });
            Assert.IsNotNull(d);
            return (d!, ui, cheats, driver, vision);
        }

        private void PumpToEnd(AdDirector d, int maxTicks = 100_000)
        {
            var ticks = 0;
            while (!d.IsFinished && ticks++ < maxTicks)
            {
                d.PumpOnce();
                _clock += 0.1;
            }
            Assert.IsTrue(d.IsFinished, "director never finished");
        }

        [Test]
        public void HappyPath_RunsSetupSteps_RecordsAndCaptures()
        {
            var ui0 = new AdShot("clip",
                setup: new[] { "PrepThing" },
                steps: new[] { AdStep.Wait(0.5) },
                settle: WaitCondition.Present("Board"));
            var (d, ui, cheats, driver, _) = Start(new[] { ui0 });
            ui.Names.Add("Board");
            PumpToEnd(d);
            Assert.IsTrue(d.AllCaptured);
            CollectionAssert.Contains(cheats.Run_, "PrepThing");
            CollectionAssert.Contains(driver.Started, "clip");
            Assert.IsFalse(driver.IsRecording, "recording must stop at finish");
        }

        [Test]
        public void Gate_SeesEachUpcomingShotsBaseline()
        {
            var menu = new AdShot("menu", Array.Empty<string>(),
                new[] { AdStep.Wait(0.1) }, WaitCondition.Present("Board"),
                baseline: AdShot.BaselineLobby);
            var board = new AdShot("board", Array.Empty<string>(),
                new[] { AdStep.Wait(0.1) }, WaitCondition.Present("Board")); // unset → board
            var gate = new BaselineRoutingGate();
            var (d, ui, _, _, _) = Start(new[] { menu, board }, readyGate: gate);
            ui.Names.Add("Board");
            PumpToEnd(d);
            CollectionAssert.AreEqual(
                new[] { ("menu", AdShot.BaselineLobby), ("board", AdShot.BaselineBoard) }, gate.Seen);
        }

        [Test]
        public void Gate_WantsLobby_OnlyWhenEveryUpcomingShotIsLobby()
        {
            AdShot Shot(string name, string? baseline) => new AdShot(name, Array.Empty<string>(),
                new[] { AdStep.Wait(0.1) }, WaitCondition.Present("Board"), baseline: baseline);

            var all = new BaselineRoutingGate();
            var (d1, ui1, _, _, _) = Start(
                new[] { Shot("a", AdShot.BaselineLobby), Shot("b", AdShot.BaselineLobby) }, readyGate: all);
            ui1.Names.Add("Board");
            PumpToEnd(d1);
            Assert.IsTrue(all.WantsLobby, "an all-lobby run must route to the lobby baseline");

            var mixed = new BaselineRoutingGate();
            var (d2, ui2, _, _, _) = Start(
                new[] { Shot("a", AdShot.BaselineLobby), Shot("b", null) }, readyGate: mixed);
            ui2.Names.Add("Board");
            PumpToEnd(d2);
            Assert.IsFalse(mixed.WantsLobby, "a board shot in the mix must keep the board baseline");
        }

        [Test]
        public void FailedShot_RetriesThenQuarantinesTakes()
        {
            var shot = new AdShot("bad",
                setup: Array.Empty<string>(),
                steps: new[] { AdStep.WaitFor(WaitCondition.Present("NeverAppears"), timeout: 2) },
                settle: WaitCondition.Present("NeverAppears"));
            var (d, _, _, driver, _) = Start(new[] { shot });
            PumpToEnd(d);
            Assert.IsFalse(d.AllCaptured);
            Assert.AreEqual(2, driver.Started.Count, "MaxAttempts=2 means two takes");
            var failed = Directory.GetFiles(_outDir, "*_FAILED_*");
            Assert.AreEqual(2, failed.Length, "both takes quarantined out of the deliverable set");
        }

        [Test]
        public void ExpectState_Mismatch_FailsCapture()
        {
            var shot = new AdShot("statey",
                setup: Array.Empty<string>(),
                steps: new[] { AdStep.Wait(0.2) },
                settle: WaitCondition.Present("Board"),
                expectState: "BoardState");
            var state = new FakeState { CurrentStateName = "ShopState" };
            var (d, ui, _, _, _) = Start(new[] { shot }, state);
            ui.Names.Add("Board");
            PumpToEnd(d);
            Assert.IsFalse(d.AllCaptured);
        }

        /// <summary>
        /// The live roguelegend failure: the settle condition was a roll button that stays in the
        /// hierarchy throughout the token's move, so it held instantly while the state machine
        /// still said 'Game/Moving' — and the capture check ran against a mid-animation frame.
        /// The director has to wait for the WHOLE captured condition, ExpectState included.
        /// </summary>
        [Test]
        public void Settle_WaitsForExpectState_NotJustTheUiCondition()
        {
            var state = new LaggingState
            {
                Now = () => _clock, SettlesAt = 3, Before = "Game/Moving", After = "Game/Idling",
            };
            var shot = new AdShot("board",
                setup: Array.Empty<string>(),
                steps: new[] { AdStep.Wait(0.2) },
                settle: WaitCondition.Present("Board"),
                expectState: "Game/Idling",
                settleTimeout: 20);
            var (d, ui, _, driver, _) = Start(new[] { shot }, state);
            ui.Names.Add("Board"); // present from the very start, exactly like the roll button
            PumpToEnd(d);
            Assert.IsTrue(d.AllCaptured, d.Summary);
            Assert.AreEqual(1, driver.Started.Count, "captured first try — no retry needed");
        }

        [Test]
        public void Settle_GivesUpAtTheShotsSettleTimeout()
        {
            var shot = new AdShot("slowpoke",
                setup: Array.Empty<string>(),
                steps: new[] { AdStep.Wait(0.2) },
                settle: WaitCondition.State("NeverReached"),
                settleTimeout: 3);
            var (d, _, _, driver, _) = Start(new[] { shot }, new FakeState { CurrentStateName = "Other" });
            PumpToEnd(d);
            Assert.IsFalse(d.AllCaptured);
            Assert.AreEqual(2, driver.Started.Count, "bounded by MaxAttempts, not hung on settle");
        }

        [Test]
        public void FailureMessage_NamesTheConditionThatDidNotResolve()
        {
            var shot = new AdShot("bad",
                setup: Array.Empty<string>(),
                steps: new[] { AdStep.WaitFor(WaitCondition.Present("NeverAppears"), timeout: 2) },
                settle: WaitCondition.Present("NeverAppears"));
            var (d, _, _, _, _) = Start(new[] { shot });
            PumpToEnd(d);
            StringAssert.Contains("WaitFor Present 'NeverAppears'", d.Summary);
        }

        /// <summary>
        /// The live case: a battle's rewards raise a level-up pick at an unpredictable moment, so a
        /// fixed-time cleanup fires before the interrupt exists, no-ops, and the shot hangs. CheatUntil
        /// keeps applying the cleanup until the game reports ready.
        /// </summary>
        [Test]
        public void CheatUntil_RetriesUntilTheConditionHolds()
        {
            var shot = new AdShot("battle",
                setup: Array.Empty<string>(),
                steps: new[]
                {
                    AdStep.CheatUntil("auto-resolve-any", WaitCondition.Present("Board"),
                        timeout: 30, retryEvery: 1),
                },
                settle: WaitCondition.Present("Board"));
            var (d, ui, cheats, _, _) = Start(new[] { shot });

            var ticks = 0;
            while (!d.IsFinished && ticks++ < 100_000)
            {
                d.PumpOnce();
                _clock += 0.1;
                // The interrupt only clears after the cleanup has been applied three times.
                if (cheats.Run_.Count >= 3) ui.Names.Add("Board");
            }
            Assert.IsTrue(d.IsFinished, "director never finished");
            Assert.IsTrue(d.AllCaptured, d.Summary);
            Assert.GreaterOrEqual(cheats.Run_.Count, 3, "kept retrying until the condition held");
        }

        [Test]
        public void CheatUntil_DoesNotRunWhenTheConditionAlreadyHolds()
        {
            var shot = new AdShot("noop",
                setup: Array.Empty<string>(),
                steps: new[] { AdStep.CheatUntil("auto-resolve-any", WaitCondition.Present("Board")) },
                settle: WaitCondition.Present("Board"));
            var (d, ui, cheats, _, _) = Start(new[] { shot });
            ui.Names.Add("Board");
            PumpToEnd(d);
            Assert.IsTrue(d.AllCaptured, d.Summary);
            CollectionAssert.DoesNotContain(cheats.Run_, "auto-resolve-any");
        }

        /// <summary>
        /// The twelfth audit, S1 — NO DIRECTOR LOOP SPINS WITHOUT YIELDING. A cheatUntil whose retryEvery is 0, below 0 or NaN,
        /// with an until that never holds, ran its whole timeout inside ONE editor tick: the wait between tries yielded
        /// nothing, so the loop never handed the editor back (2,001 ms in one PumpOnce at timeout 2, on the production
        /// clock). The loader refuses such a retryEvery now; this is the belt under it, built through the step's own factory
        /// because no delivered file can carry the value any more. The clock is the director's own default — the editor's
        /// real one, as the auditor's probe ran it — so a spin is measured in real milliseconds, and it ends by itself.
        /// </summary>
        [TestCase(0.0)]
        [TestCase(-1.0)]
        [TestCase(double.NaN)]
        public void CheatUntil_WithRetryEveryOfNoTime_HandsTheEditorBackEveryTry(double retryEvery)
        {
            var shot = new AdShot("spin",
                setup: Array.Empty<string>(),
                steps: new[] { AdStep.CheatUntil("set Player.coins 5", WaitCondition.Present("Never"), timeout: 0.5, retryEvery: retryEvery) },
                settle: WaitCondition.Present("Board"));
            var ui = new FakeUi();
            ui.Names.Add("Board");
            var cheats = new FakeCheats();
            var adapter = new GameAdapter
            {
                GameId = "testgame", Ui = ui, StateProbe = new FakeState(), CheatBridge = cheats,
                ReadyGate = NullReadyGate.Instance, Recovery = NullRecoveryPolicy.Instance, Shots = () => new[] { shot },
            };
            // Options.Now is left unset: the director reads EditorApplication.timeSinceStartup, as it does in production.
            var d = AdDirector.Run(adapter, new[] { shot }, new AdDirector.Options
            {
                AutoPump = false, MaxAttempts = 1, Recorder = new FakeDriver(_outDir), Vision = new FakeVision(),
                ReleaseCameraHold = () => { },
            });
            Assert.IsNotNull(d);
            // COUNTED, NOT TIMED (the fourteenth fold's coordinator): the wall-clock form of this test timed ~12 million ticks
            // and failed from machine noise beside another Unity run (one tick of 338 ms). "Hands the editor back every try" is
            // a count: at most ONE try of the cheat in any one tick — a cheatUntil that spun inside a tick ran it thousands of times.
            var wall = System.Diagnostics.Stopwatch.StartNew();
            var mostTriesInOneTick = 0;
            var pumps = 0;
            while (!d!.IsFinished && wall.Elapsed.TotalSeconds < 10)
            {
                var before = cheats.Run_.Count(c => c == "set Player.coins 5");
                d.PumpOnce();
                mostTriesInOneTick = Math.Max(mostTriesInOneTick, cheats.Run_.Count(c => c == "set Player.coins 5") - before);
                pumps++;
            }
            Assert.IsTrue(d.IsFinished, "the director never finished");
            Assert.Greater(pumps, 1, "the whole cheatUntil ran inside one editor tick");
            Assert.Greater(cheats.Run_.Count(c => c == "set Player.coins 5"), 1, "the cheat was tried more than once (positive control)");
            Assert.LessOrEqual(mostTriesInOneTick, 1, $"one editor tick ran the cheat {mostTriesInOneTick} times of a 500 ms cheatUntil ({pumps} ticks)");
            StringAssert.Contains("did not resolve", d.Summary);
        }

        /// <summary>CONTROL for the belt above: the default retryEvery, 1.5 s, runs on exactly the schedule it always did —
        /// in each attempt the cheat, twelve ticks of an eighth of a second (exact in binary), the cheat again, and the 2 s
        /// timeout is spent.</summary>
        [Test]
        public void CheatUntil_TheDefaultRetryEveryKeepsItsSchedule()
        {
            var shot = new AdShot("battle",
                setup: Array.Empty<string>(),
                steps: new[] { AdStep.CheatUntil("auto-resolve-any", WaitCondition.Present("Never"), timeout: 2) },
                settle: WaitCondition.Present("Board"));
            var (d, _, cheats, _, _) = Start(new[] { shot });
            var ranAt = new List<int>();
            var ticks = 0;
            while (!d.IsFinished && ticks++ < 100_000)
            {
                var before = cheats.Run_.Count(c => c == "auto-resolve-any");
                d.PumpOnce();
                _clock += 0.125;
                if (cheats.Run_.Count(c => c == "auto-resolve-any") > before) ranAt.Add(ticks);
            }
            Assert.IsTrue(d.IsFinished, "director never finished");
            // two attempts (MaxAttempts' default), each: the cheat at the start, once 1.5 s later, then the 2 s are spent
            Assert.AreEqual(4, ranAt.Count, string.Join(", ", ranAt));
            Assert.AreEqual(12, ranAt[1] - ranAt[0], "1.5 s between tries is twelve ticks of 0.125 s");
            Assert.AreEqual(12, ranAt[3] - ranAt[2], "1.5 s between tries is twelve ticks of 0.125 s");
        }

        /// <summary>
        /// The thirteenth audit, S1 — ONE YIELD PER UNIT OF WORK: the director hands the editor back after EACH setup
        /// command. The setup loop had no yield at all, so 16,355 copies of one delivered command ran inside one editor tick
        /// (21.9 s on the production clock, the gate hashing the delivered file on every one).
        /// </summary>
        [Test]
        public void Setup_EachCommandRunsInItsOwnPump()
        {
            var setup = Enumerable.Range(0, 20).Select(i => "cmd" + i).ToArray();
            var shot = new AdShot("setup", setup: setup, steps: new[] { AdStep.Wait(0.5) }, settle: WaitCondition.Present("Board"));
            var (d, ui, cheats, _, _) = Start(new[] { shot });
            ui.Names.Add("Board");
            var pumpOf = new Dictionary<string, int>();
            var pumps = 0;
            while (!d.IsFinished && pumps < 100_000)
            {
                var before = cheats.Run_.Count;
                d.PumpOnce();
                pumps++;
                _clock += 0.1;
                for (var i = before; i < cheats.Run_.Count; i++) pumpOf[cheats.Run_[i]] = pumps;
            }
            Assert.IsTrue(d.AllCaptured, "positive control: the shot ran to its end and was captured");
            var ranIn = setup.Select(c => pumpOf[c]).ToList();
            Assert.AreEqual(setup.Length, ranIn.Distinct().Count(), "the setup ran in pumps [" + string.Join(", ", ranIn) + "]");
        }

        /// <summary>
        /// The thirteenth audit, S1 — and after EACH step. Four kinds of step can finish without yielding once — a
        /// `timeScale`, a click with no `until`, a waitFor or a cheatUntil whose condition already holds — and a list of them
        /// ran whole inside one editor tick (2,045 `timeScale` steps: 2,042 ms). Each step starts in its own pump now, which
        /// the step marks show: on this clock, two steps in one pump would start at the same second.
        /// </summary>
        [Test]
        public void Steps_ThatNeverWait_EachStartInTheirOwnPump()
        {
            var steps = Enumerable.Range(0, 5).SelectMany(_ => new[]
            {
                AdStep.TimeScale(1),
                AdStep.Click("Btn"),
                AdStep.WaitFor(WaitCondition.Present("Board"), 1),
                AdStep.CheatUntil("fix", WaitCondition.Present("Board")),
            }).ToArray();
            var shot = new AdShot("zero-yield", setup: Array.Empty<string>(), steps: steps, settle: WaitCondition.Present("Board"));
            var (d, ui, _, _, _) = Start(new[] { shot });
            ui.Names.Add("Board");
            PumpToEnd(d);
            Assert.IsTrue(d.AllCaptured, "positive control: every step resolved and the shot was captured");
            var startedAt = d.StepMarks.Where(m => m.Index < steps.Length).Select(m => m.AtSec).ToList();
            Assert.AreEqual(steps.Length, startedAt.Count);
            Assert.AreEqual(steps.Length, startedAt.Distinct().Count(),
                "the steps started at [" + string.Join(", ", startedAt.Select(t => t.ToString("0.0"))) + "] s — two of them in one pump");
        }

        [Test]
        public void VisionStep_SucceedsOnMatch()
        {
            var shot = new AdShot("v",
                setup: Array.Empty<string>(),
                steps: new[] { AdStep.Vision("chest open", timeout: 5) },
                settle: WaitCondition.Present("Board"));
            var (d, ui, _, _, vision) = Start(new[] { shot });
            ui.Names.Add("Board");
            vision.Answer = true;
            PumpToEnd(d);
            Assert.IsTrue(d.AllCaptured);
            Assert.AreEqual("chest open", vision.LastPrompt);
        }

        [Test]
        public void VisionStep_TimesOut_FailsShot()
        {
            var shot = new AdShot("v2",
                setup: Array.Empty<string>(),
                steps: new[] { AdStep.Vision("never answered", timeout: 2) },
                settle: WaitCondition.Present("Board"));
            var (d, ui, _, _, vision) = Start(new[] { shot });
            ui.Names.Add("Board");
            vision.Answer = null;
            PumpToEnd(d);
            Assert.IsFalse(d.AllCaptured);
            // Slice E.3: a timeout is a NAMED reason — never "did not match", a verdict nobody made
            // (MaxAttempts is 2 here: the failure reported is the last attempt's)
            Assert.AreEqual("v2 attempt 2: Vision 'never answered' did not resolve — "
                            + AdDirector.VisionTimedOut(2), d.FailedReason);
            StringAssert.DoesNotContain("did not match", d.FailedReason);
        }

        [Test]
        public void VisionStep_TheStepIndexReachesTheChannel()
        {
            // Slice E.3: the site answers a try's screen check from the shot it SENT, by the step's
            // index in `steps` — so the vision step must NOT be first here, or 0 would pass by luck.
            var shot = new AdShot("v4",
                setup: Array.Empty<string>(),
                steps: new[] { AdStep.Wait(0.2), AdStep.Wait(0.2), AdStep.Vision("chest open", timeout: 5) },
                settle: WaitCondition.Present("Board"));
            var (d, ui, _, _, vision) = Start(new[] { shot });
            ui.Names.Add("Board");
            vision.Answer = true;
            PumpToEnd(d);
            Assert.AreEqual(2, vision.LastStep, "the 0-based index of the vision step in the shot's own steps");
            Assert.IsTrue(d.AllCaptured, d.Summary);
        }

        [Test]
        public void VisionStep_ANoFromTheFileChannelSaysDidNotMatch()
        {
            // The positive control for every "never reads as did not match" assertion: a REAL answer
            // of no DOES say it, in the director's one sentence for it.
            var shot = new AdShot("v5",
                setup: Array.Empty<string>(),
                steps: new[] { AdStep.Vision("chest open", timeout: 5) },
                settle: WaitCondition.Present("Board"));
            var (d, ui, _, _, vision) = Start(new[] { shot });
            ui.Names.Add("Board");
            vision.Answer = false;
            PumpToEnd(d);
            Assert.IsFalse(d.AllCaptured);
            Assert.AreEqual(0, d.FailedStepIndex);
            Assert.AreEqual("v5 attempt 2: Vision 'chest open' did not resolve — " + AdDirector.VisionNoMatch,
                d.FailedReason);
        }

        // ---- the FILE channel (a capture's): only a JSON true/false `match` is an answer ----------
        // The first audit of E.3, K-S1: a response with no boolean `match` was read as an answer of
        // "no" (or, coerced, "yes") — and the director then said "the screen check answered: the
        // screen did not match", a verdict nobody made.

        private const string NoTrueFalse = "the response file has no true/false match";

        /// <summary>A run of ONE vision shot through the REAL file channel, rooted in this test's own
        /// folder: every request it writes is answered with <paramref name="response"/> ("ID" = the
        /// request's id), the way a person or agent answering the relay would.</summary>
        private AdDirector RunWithFileChannel(string name, string response)
        {
            var root = Path.Combine(_outDir, "project");
            var shot = new AdShot(name,
                setup: Array.Empty<string>(),
                steps: new[] { AdStep.Vision("chest open", timeout: 5) },
                settle: WaitCondition.Present("Board"));
            var ui = new FakeUi();
            ui.Names.Add("Board");
            var adapter = new GameAdapter
            {
                GameId = "testgame",
                Ui = ui,
                StateProbe = new FakeState(),
                CheatBridge = new FakeCheats(),
                ReadyGate = NullReadyGate.Instance,
                Recovery = NullRecoveryPolicy.Instance,
                Shots = () => new[] { shot },
            };
            var d = AdDirector.Run(adapter, new[] { shot }, new AdDirector.Options
            {
                AutoPump = false,
                Now = () => _clock,
                Recorder = new FakeDriver(_outDir),
                Vision = new FileVisionChannel(root),
                ReleaseCameraHold = () => { },
            });
            Assert.IsNotNull(d);
            var requests = RelayPaths.VisionRequests(root);
            var responses = RelayPaths.VisionResponses(root);
            var ticks = 0;
            while (!d!.IsFinished && ticks++ < 100_000)
            {
                d.PumpOnce();
                _clock += 0.1;
                if (!Directory.Exists(requests)) continue;
                foreach (var req in Directory.GetFiles(requests, "*.json"))
                {
                    var id = Path.GetFileNameWithoutExtension(req);
                    var answer = Path.Combine(responses, id + ".json");
                    if (File.Exists(answer)) continue;
                    Directory.CreateDirectory(responses);
                    File.WriteAllText(answer, response.Replace("ID", id));
                }
            }
            Assert.IsTrue(d.IsFinished, "director never finished");
            return d;
        }

        [TestCase("{\"id\":\"ID\",\"error\":\"cannot tell\"}")]
        [TestCase("{\"id\":\"ID\",\"match\":0}")]
        [TestCase("{\"id\":\"ID\",\"match\":\"true\"}")]
        public void TheFileChannel_AResponseWithNoTrueFalseMatch_FailsTheStepWithThatReason_NeverAsDidNotMatch(string response)
        {
            var d = RunWithFileChannel("v6", response);
            Assert.IsFalse(d.AllCaptured, "not a verdict — never a pass");
            Assert.AreEqual(0, d.FailedStepIndex);
            Assert.AreEqual("v6 attempt 2: Vision 'chest open' did not resolve — " + NoTrueFalse, d.FailedReason);
            StringAssert.DoesNotContain("did not match", d.FailedReason);
        }

        [Test]
        public void TheFileChannel_AFalseMatchStillSaysDidNotMatch()
        {
            var d = RunWithFileChannel("v7", "{\"id\":\"ID\",\"match\":false}");
            Assert.IsFalse(d.AllCaptured);
            Assert.AreEqual("v7 attempt 2: Vision 'chest open' did not resolve — " + AdDirector.VisionNoMatch,
                d.FailedReason);
        }

        [Test]
        public void TheFileChannel_ATrueMatchPasses()
        {
            var d = RunWithFileChannel("v8", "{\"id\":\"ID\",\"match\":true}");
            Assert.IsTrue(d.AllCaptured, d.Summary);
            Assert.IsNull(d.FailedReason);
        }

        [Test]
        public void TheFileChannel_IsAskedThroughIVisionOutcome_AndTryGetResultIsTrueOnlyForATrueOrFalse()
        {
            var root = Path.Combine(_outDir, "project");
            var responses = RelayPaths.VisionResponses(root);
            Directory.CreateDirectory(responses);
            var ch = new FileVisionChannel(root);
            var outcome = (object)ch as IVisionOutcome;
            Assert.IsNotNull(outcome, "the file channel can end a question WITHOUT a verdict");
            var cases = new (string id, string? body, bool? match, string? failure)[]
            {
                ("aaaaaaaaaaaa", null, null, null), // not written yet: pending
                ("bbbbbbbbbbbb", "{\"id\":\"bbbbbbbbbbbb\",\"mat", null, null), // mid-write: pending
                ("cccccccccccc", "{\"id\":\"cccccccccccc\",\"error\":\"cannot tell\"}", null, NoTrueFalse),
                ("dddddddddddd", "{\"id\":\"dddddddddddd\",\"match\":0}", null, NoTrueFalse),
                ("eeeeeeeeeeee", "{\"id\":\"eeeeeeeeeeee\",\"match\":\"true\"}", null, NoTrueFalse),
                ("ffffffffffff", "{\"id\":\"ffffffffffff\",\"match\":false}", false, null),
                ("gggggggggggg", "{\"id\":\"gggggggggggg\",\"match\":true}", true, null),
            };
            foreach (var (id, body, match, failure) in cases)
            {
                if (body != null) File.WriteAllText(Path.Combine(responses, id + ".json"), body);
                var poll = outcome!.Poll(id);
                Assert.AreEqual(match, poll.Match, id);
                Assert.AreEqual(failure, poll.Failure, id);
                Assert.AreEqual(match != null, ch.TryGetResult(id, out var m), id + ": TryGetResult is true only for an answer");
                Assert.AreEqual(match == true, m, id);
            }
        }

        [Test]
        public void ACaptureKeepsTheFileChannel()
        {
            // A capture builds its director with NO vision channel (NovaCaptureAgent: `AdDirector.Run
            // (adapter, new[] { shot })`, KitMenus, RelayBoot) — the director's own default must stay
            // the FILE channel; only a try with vision steps asks the site.
            var shot = new AdShot("c", setup: Array.Empty<string>(), steps: new[] { AdStep.Wait(0.1) },
                settle: WaitCondition.Present("Board"));
            var adapter = new GameAdapter { GameId = "testgame", Ui = new FakeUi(), CheatBridge = new FakeCheats() };
            var d = AdDirector.Run(adapter, new[] { shot }, new AdDirector.Options
            {
                AutoPump = false,
                Now = () => _clock,
                Recorder = new FakeDriver(_outDir),
                ReleaseCameraHold = () => { },
            });
            Assert.IsNotNull(d);
            Assert.IsInstanceOf<FileVisionChannel>(d!.VisionChannel);
        }

        [Test]
        public void SkipVisionShots_SkipsWithoutRecording()
        {
            var visionShot = new AdShot("v3",
                setup: Array.Empty<string>(),
                steps: new[] { AdStep.Vision("x") },
                settle: WaitCondition.Present("Board"));
            var plain = new AdShot("p",
                setup: Array.Empty<string>(),
                steps: new[] { AdStep.Wait(0.2) },
                settle: WaitCondition.Present("Board"));
            var (d, ui, _, driver, _) = Start(new[] { visionShot, plain }, skipVision: true);
            ui.Names.Add("Board");
            PumpToEnd(d);
            CollectionAssert.AreEqual(new[] { "p" }, driver.Started, "vision shot skipped entirely");
            StringAssert.Contains("SKIP", d.Summary);
        }

        // A plain shot used by the ready-gate tests: no setup, one short wait, settles on "Board".
        private static AdShot PlainShot(string name) => new AdShot(name,
            setup: Array.Empty<string>(),
            steps: new[] { AdStep.Wait(0.2) },
            settle: WaitCondition.Present("Board"));

        [Test]
        public void ReadyGate_Unsatisfied_RecoversThenRetriesAndProceeds()
        {
            // The single-shot relay case: the previous shot left a popup up, so the gate fails
            // until recovery clears it. Before the fix the gate ran before any recovery and the
            // whole run aborted here.
            var gate = new GateNeedingRecovery();
            var recovery = new CountingRecovery { OnRecover = () => gate.BaselineReached = true };
            var (d, ui, _, driver, _) = Start(new[] { PlainShot("clip") },
                readyGate: gate, recovery: recovery);
            ui.Names.Add("Board");
            PumpToEnd(d);
            Assert.IsTrue(d.AllCaptured, d.Summary);
            Assert.AreEqual(2, gate.Attempts, "gate is retried once after recovery");
            CollectionAssert.Contains(driver.Started, "clip");
        }

        [Test]
        public void ReadyGate_StillUnsatisfiedAfterRecovery_AbortsWithoutRecording()
        {
            var gate = new GateNeedingRecovery(); // recovery never reaches the baseline
            var recovery = new CountingRecovery();
            var (d, ui, _, driver, _) = Start(new[] { PlainShot("clip") },
                readyGate: gate, recovery: recovery);
            ui.Names.Add("Board");
            PumpToEnd(d);
            Assert.IsFalse(d.AllCaptured);
            Assert.AreEqual(2, gate.Attempts, "gate tried exactly twice, not forever");
            Assert.AreEqual(1, recovery.Calls, "recovery is the gate's one retry, not a loop");
            CollectionAssert.IsEmpty(driver.Started, "nothing is recorded when the gate never opens");
            StringAssert.Contains("ABORT", d.Summary);
        }

        [Test]
        public void ReadyGate_SatisfiedFirstTry_DoesNotInvokeGateRecovery()
        {
            // Guards the batch path: a gate that passes immediately must leave recovery to
            // RunShot's per-attempt call (exactly one for one shot on the first attempt).
            var gate = new GateNeedingRecovery { BaselineReached = true };
            var recovery = new CountingRecovery();
            var (d, ui, _, _, _) = Start(new[] { PlainShot("clip") },
                readyGate: gate, recovery: recovery);
            ui.Names.Add("Board");
            PumpToEnd(d);
            Assert.IsTrue(d.AllCaptured, d.Summary);
            Assert.AreEqual(1, gate.Attempts);
            Assert.AreEqual(1, recovery.Calls, "only RunShot's recovery ran, not a gate retry");
        }

        [Test]
        public void BindingFailed_ReleasesBeforeTheNextShotInTheSameRun()
        {
            // Finish() also releases, so a single-shot `released >= 1` would still
            // pass if the bindingFailed yield-break insertion were deleted. The
            // insertion exists so a later shot does not inherit leftover yaw.
            var events = new List<string>();
            var cheats = new EventCheats(events);
            var shot1 = new AdShot("bad",
                setup: new[] { "camera-pose 1 2 3 40.84 135 0 20", "stage-hero {hero}" },
                steps: new[] { AdStep.Wait(0.1) },
                settle: WaitCondition.Present("Board"),
                parameters: new[] { "hero" });
            var shot2 = new AdShot("ok",
                setup: new[] { "second-setup" },
                steps: new[] { AdStep.Wait(0.1) },
                settle: WaitCondition.Present("Board"));
            var ui = new FakeUi();
            ui.Names.Add("Board");
            var driver = new FakeDriver(_outDir);
            var adapter = new GameAdapter
            {
                GameId = "testgame",
                Ui = ui,
                StateProbe = new FakeState(),
                CheatBridge = cheats,
                ReadyGate = NullReadyGate.Instance,
                Recovery = NullRecoveryPolicy.Instance,
                Shots = () => new[] { shot1, shot2 },
            };
            var d = AdDirector.Run(adapter, new[] { shot1, shot2 }, new AdDirector.Options
            {
                AutoPump = false,
                Now = () => _clock,
                Recorder = driver,
                ReleaseCameraHold = () => events.Add("release"),
            });
            Assert.IsNotNull(d);
            PumpToEnd(d!);
            CollectionAssert.Contains(events, "cheat:camera-pose 1 2 3 40.84 135 0 20");
            CollectionAssert.Contains(events, "cheat:second-setup");
            CollectionAssert.Contains(events, "release");
            Assert.Less(events.IndexOf("release"), events.IndexOf("cheat:second-setup"),
                "bindingFailed must release before the next shot's setup, not only at Finish");
            CollectionAssert.Contains(driver.Started, "ok");
        }

        [Test]
        public void FailedShot_ReleasesCameraHold()
        {
            // Setup pose is a cheat the fake bridge records; the director must still
            // call camera-release after the take fails (and on each retry after Stop).
            var released = 0;
            var shot = new AdShot("bad",
                setup: new[] { "camera-pose 1 2 3 40.84 135 0 20" },
                steps: new[] { AdStep.WaitFor(WaitCondition.Present("NeverAppears"), timeout: 2) },
                settle: WaitCondition.Present("NeverAppears"));
            var (d, _, cheats, driver, _) = Start(new[] { shot },
                releaseCameraHold: () => released++);
            PumpToEnd(d);
            Assert.IsFalse(d.AllCaptured);
            CollectionAssert.Contains(cheats.Run_, "camera-pose 1 2 3 40.84 135 0 20");
            Assert.AreEqual(2, driver.Started.Count, "MaxAttempts=2 means two takes");
            Assert.GreaterOrEqual(released, 2,
                "each failed take must release after Stop so the retry does not inherit leftover yaw");
        }

        [Test]
        public void CameraHold_NotReleasedWhileRecorderIsRunning()
        {
            var events = new List<string>();
            var shot = new AdShot("clip",
                setup: new[] { "camera-pose 1 2 3 40.84 135 0 20" },
                steps: new[] { AdStep.Wait(0.5) },
                settle: WaitCondition.Present("Board"));
            var ui = new FakeUi();
            ui.Names.Add("Board");
            var cheats = new FakeCheats();
            var driver = new EventDriver(_outDir, events);
            var adapter = new GameAdapter
            {
                GameId = "testgame",
                Ui = ui,
                StateProbe = new FakeState(),
                CheatBridge = cheats,
                ReadyGate = NullReadyGate.Instance,
                Recovery = NullRecoveryPolicy.Instance,
                Shots = () => new[] { shot },
            };
            var d = AdDirector.Run(adapter, new[] { shot }, new AdDirector.Options
            {
                AutoPump = false,
                Now = () => _clock,
                Recorder = driver,
                ReleaseCameraHold = () =>
                {
                    Assert.IsFalse(driver.IsRecording,
                        "stills stay posed until the take is on disk — do not release while recording");
                    events.Add("release");
                },
            });
            Assert.IsNotNull(d);
            PumpToEnd(d!);
            Assert.IsTrue(d!.AllCaptured, d.Summary);
            CollectionAssert.Contains(cheats.Run_, "camera-pose 1 2 3 40.84 135 0 20");
            Assert.AreEqual("start", events[0]);
            Assert.AreEqual("stop", events[1]);
            Assert.AreEqual("release", events[2], "first release is immediately after Stop");
            CollectionAssert.Contains(events, "release");
            for (var i = 0; i < events.Count; i++)
            {
                if (events[i] != "start") continue;
                Assert.AreEqual("stop", events[i + 1], "Stop must precede release for each take");
            }
        }

        [Test]
        public void ReadyGateAbort_ReleasesHold()
        {
            var released = 0;
            var gate = new GateNeedingRecovery();
            var recovery = new CountingRecovery();
            var (d, ui, _, driver, _) = Start(new[] { PlainShot("clip") },
                readyGate: gate, recovery: recovery, releaseCameraHold: () => released++);
            ui.Names.Add("Board");
            PumpToEnd(d);
            Assert.IsFalse(d.AllCaptured);
            CollectionAssert.IsEmpty(driver.Started);
            Assert.GreaterOrEqual(released, 1, "Finish() must release even when no take ran");
        }

        private sealed class EventCheats : ICheatBridge
        {
            private readonly List<string> _events;
            public EventCheats(List<string> events) { _events = events; }
            public bool Run(string command)
            {
                _events.Add("cheat:" + command);
                return true;
            }
        }

        private sealed class EventDriver : IRecorderDriver
        {
            public string OutputDir { get; }
            public bool IsRecording { get; private set; }
            public int OutputWidth => 0;
            public int OutputHeight => 0;
            private readonly List<string> _events;

            public EventDriver(string dir, List<string> events)
            {
                OutputDir = dir;
                Directory.CreateDirectory(dir);
                _events = events;
            }

            public void Start(string clipName)
            {
                IsRecording = true;
                _events.Add("start");
                File.WriteAllText(Path.Combine(OutputDir, clipName + "_001.mp4"), "clip");
            }

            public void Stop()
            {
                _events.Add("stop");
                IsRecording = false;
            }
        }

        [Test]
        public void SecondRun_RefusedWhileActive()
        {
            var shot = new AdShot("s",
                setup: Array.Empty<string>(),
                steps: new[] { AdStep.Wait(5) },
                settle: WaitCondition.Present("Board"));
            var (d, _, _, _, _) = Start(new[] { shot });
            var second = AdDirector.Run(new GameAdapter(), new[] { shot },
                new AdDirector.Options { AutoPump = false, Now = () => _clock });
            Assert.IsNull(second);
            d.Finish("test");
        }
    }
}
