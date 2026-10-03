using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.TestTools;

namespace ProjectNova.RecorderKit.Tests
{
    public class RelayServerTests
    {
        private string _root = "";
        private RelayEnv _env = new();
        private RelayServer _server = null!;
        private bool _playing;
        private int _recompiles;
        private bool _windowTeach;
        private bool _windowRecording;
        private bool _armed = true;
        private bool _sceneDirty;

        private sealed class RecordingCheatBridge : ICheatBridge
        {
            public readonly List<string> Run_ = new();
            public bool Result = true;
            public bool Run(string command) { Run_.Add(command); return Result; }
        }

        private RecordingCheatBridge _bridge = new();

        /// <summary>An already-finished handle — enough for tests that only care what StartShot was called with.</summary>
        private sealed class FakeHandle : IShotRunHandle
        {
            public bool IsFinished => true;
            public bool AllCaptured => true;
            public string Summary => "fake";
            public string? StepMarksJson => null;
        }

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "relay-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _playing = false;
            _recompiles = 0;
            _bridge = new RecordingCheatBridge();
            var adapter = new GameAdapter { GameId = "testgame", CheatBridge = _bridge };
            _env = new RelayEnv
            {
                IsPlaying = () => _playing,
                SetPlaying = v => _playing = v,
                RequestRecompile = () => _recompiles++,
                Adapter = () => adapter,
                CaptureScreenshot = path => File.WriteAllText(path, "png"),
                Now = () => 0,
                WindowTeachActive = () => _windowTeach,
                WindowTeachRecording = () => _windowRecording,
                // kit 0.14.3: the relay is armed by a person (RelayArm); these tests drive an armed one unless they say not
                Armed = () => _armed,
                AnySceneDirty = () => _sceneDirty,
            };
            _armed = true;
            _sceneDirty = false;
            _windowTeach = false;
            _windowRecording = false;
            _server = new RelayServer(_root, _env);
        }

        [TearDown]
        public void TearDown()
        {
            CameraPose.ResetForTests();
            TeachRecorder.ResetForTests();
            Directory.Delete(_root, recursive: true);
        }

        private void Send(string id, string action, object? args = null)
        {
            var o = new JObject { ["id"] = id, ["action"] = action };
            if (args != null) o["args"] = JObject.FromObject(args);
            AtomicFile.Write(RelayPaths.Command(_root, id), o.ToString());
        }

        private JObject? Result(string id)
        {
            var p = RelayPaths.Result(_root, id);
            return File.Exists(p) ? JObject.Parse(File.ReadAllText(p)) : null;
        }

        // v3 P4 (M3): while a teach job from the website is in the Nova Capture window it owns the one recorder — a relay
        // teach-stop would end the window's recording, a relay teach-start would record into its restart. Through the real
        // command path (invariant 101).
        [Test]
        public void RelayTeachCommands_AreRefusedWhileAWindowTeachJobIsActive_AndServedOtherwise()
        {
            TeachRecorder.ResetForTests();
            _playing = true;
            _windowTeach = true;
            _windowRecording = true;
            Assert.IsNull(TeachRecorder.Begin(null), "the window's recording is on");

            Send("stop1", "teach-stop");
            _server.PumpOnce();
            var r = Result("stop1")!;
            Assert.IsFalse(r["ok"]!.Value<bool>());
            StringAssert.Contains("Nova Capture window", r["error"]!.Value<string>()!);
            Assert.IsTrue(TeachRecorder.IsTeaching, "the window's recording was not stopped by the relay");

            Send("start1", "teach-start");
            _server.PumpOnce();
            StringAssert.Contains("Nova Capture window", Result("start1")!["error"]!.Value<string>()!);

            // a relay teach started BEFORE the website's request (the window waits, its recorder is not on): the relay can
            // still stop its own — the two refusals never point at each other
            _windowRecording = false;
            Send("stop0", "teach-stop");
            _server.PumpOnce();
            Assert.IsTrue(Result("stop0")!["ok"]!.Value<bool>(), Result("stop0")!.ToString());
            Assert.IsNull(TeachRecorder.Begin(null));

            // positive control: with no window teach the same commands reach the recorder
            _windowTeach = false;
            Send("stop2", "teach-stop");
            _server.PumpOnce();
            Assert.IsTrue(Result("stop2")!["ok"]!.Value<bool>(), Result("stop2")!.ToString());
            Assert.IsFalse(TeachRecorder.IsTeaching);
            Send("start2", "teach-start");
            _server.PumpOnce();
            var start2 = Result("start2")!["error"]!.Value<string>() ?? "";
            StringAssert.DoesNotContain("Nova Capture window", start2, "past the guard (an EditMode test has no Play Mode for the recorder itself)");
        }

        /// <summary>A seam that throws — the shape of a freshly-authored, under-tested adapter.</summary>
        private sealed class ThrowingStateProbe : IStateProbe
        {
            public string? CurrentStateName => throw new InvalidOperationException("Non-static method requires a target.");
        }

        // Live-found onboarding roguelegend: the adapter's state probe threw in Edit Mode, the
        // exception escaped PumpOnce, and the command was never marked processed — so it was retried
        // every editor frame forever while the status heartbeat (a separate update handler) kept
        // reporting a healthy editor. The operator saw only a transport-blaming timeout.
        [Test]
        public void ThrowingAdapter_FailsThatOneCommand_AndDoesNotWedgeThePump()
        {
            var adapter = new GameAdapter
            {
                GameId = "testgame",
                CheatBridge = _bridge,
                StateProbe = new ThrowingStateProbe(),
            };
            _env.Adapter = () => adapter;

            Send("boom", "probe-state");
            LogAssert.ignoreFailingMessages = true; // the kit logs the adapter's exception by design
            _server.PumpOnce();

            var r = Result("boom");
            Assert.IsNotNull(r, "a throwing adapter must still produce a result, not silence");
            Assert.IsFalse(r!["ok"]!.Value<bool>());
            StringAssert.Contains("threw", r["error"]!.Value<string>()!);
            StringAssert.Contains("requires a target", r["error"]!.Value<string>()!);

            // The command is retired, so the pump moves on instead of retrying forever.
            Assert.IsFalse(File.Exists(RelayPaths.Command(_root, "boom")));
            Assert.IsTrue(File.Exists(RelayPaths.ProcessedMarker(_root, "boom")));

            // And a later, healthy command still gets served.
            Send("after", "ping");
            _server.PumpOnce();
            LogAssert.ignoreFailingMessages = false;
            Assert.IsTrue(Result("after")!["ok"]!.Value<bool>(), "pump still alive after an adapter threw");
        }

        [Test]
        public void Ping_DoesNotReleaseArmedHold()
        {
            Directory.CreateDirectory(RelayPaths.NovaDir(_root));
            File.WriteAllText(Path.Combine(RelayPaths.NovaDir(_root), "adapter.json"),
                @"{""camera"":{""viewType"":""CameraView"",""projection"":""perspective""}}");
            var go = new GameObject("armed-hold-cam");
            try
            {
                var cam = go.AddComponent<Camera>();
                var posed = CameraPose.Pose(
                    new[] { "1", "2", "3", "40.84", "135", "0", "20" },
                    new CameraPose.Host(
                        _root,
                        isPlaying: () => true,
                        findType: _ => typeof(Camera),
                        findInstances: _ => new object[] { go },
                        loadAdapter: () => AdapterJson.Load(_root),
                        readLive: _ => new CameraPose.Snapshot(
                            Vector3.zero, new Vector3(40.84f, 135f, 0f), 20f, "CameraView"),
                        subscribeBegin: _ => { },
                        unsubscribeBegin: _ => { },
                        resolveCamera: _ => cam,
                        apply: (_, _, _) => null));
                Assert.IsTrue(posed.Ok, posed.Error);
                Assert.IsTrue(CameraPose.HoldArmed);
                Assert.IsTrue(File.Exists(CameraPose.SnapshotPath(_root)));

                Send("armed-ping", "ping");
                _server.PumpOnce();

                Assert.IsTrue(Result("armed-ping")!["ok"]!.Value<bool>());
                Assert.IsTrue(CameraPose.HoldArmed, "ping must not drop an armed stills hold");
                Assert.IsTrue(File.Exists(CameraPose.SnapshotPath(_root)));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Ping_SweepsOrphanedCameraHoldJson()
        {
            CameraPose.WriteSnapshot(_root, new CameraPose.Snapshot(
                Vector3.zero, new Vector3(40.84f, 135f, 0f), 20f, "CameraView"));
            Assert.IsTrue(File.Exists(CameraPose.SnapshotPath(_root)));

            Send("c1", "ping");
            _server.PumpOnce();

            Assert.IsTrue(Result("c1")!["ok"]!.Value<bool>(), "ping must still answer if the sweep runs");
            Assert.IsFalse(File.Exists(CameraPose.SnapshotPath(_root)),
                "first ping must drop leftover camera-hold.json so the next pose snapshots live");
        }

        [Test]
        public void Ping_AnswersWithIdentity_AndMovesToProcessed()
        {
            Send("c1", "ping");
            _server.PumpOnce();
            var r = Result("c1");
            Assert.IsNotNull(r);
            Assert.IsTrue(r!["ok"]!.Value<bool>());
            Assert.AreEqual(KitInfo.Version, r["data"]!["kitVersion"]!.Value<string>());
            Assert.AreEqual("testgame", r["data"]!["gameId"]!.Value<string>());
            Assert.IsFalse(File.Exists(RelayPaths.Command(_root, "c1")));
            Assert.IsTrue(File.Exists(RelayPaths.ProcessedMarker(_root, "c1")));
        }

        [Test]
        public void ExistingResult_IsNeverReExecuted()
        {
            Send("c2", "run-cheat", new { command = "SetCoins 5" });
            _playing = true;
            _server.PumpOnce();
            Assert.AreEqual(1, _bridge.Run_.Count);
            // simulate a stale replay of the same command file after a reload
            Send("c2", "run-cheat", new { command = "SetCoins 5" });
            _server.PumpOnce();
            Assert.AreEqual(1, _bridge.Run_.Count, "duplicate id must not re-run");
        }

        [Test]
        public void RunCheat_NeverRunsATemplate_AFilledCommandRuns()
        {
            // the Fix 4 re-audit (invariant 182): the relay is exempt from the lever gate, but no template runs anywhere
            _playing = true;
            Send("c9", "run-cheat", new { command = "set Game.Board.NextRoll {v}" });
            _server.PumpOnce();
            var r = Result("c9");
            Assert.IsFalse(r!["ok"]!.Value<bool>());
            StringAssert.Contains(Levers.TemplateNeverRunsWords, r["error"]!.Value<string>());
            Assert.AreEqual(0, _bridge.Run_.Count, "nothing reached the bridge");
            Send("c10", "run-cheat", new { command = "set Game.Board.NextRoll 3" });
            _server.PumpOnce();
            Assert.IsTrue(Result("c10")!["ok"]!.Value<bool>(), "POSITIVE CONTROL: a filled command runs");
            CollectionAssert.AreEqual(new[] { "set Game.Board.NextRoll 3" }, _bridge.Run_);
        }

        [Test]
        public void RunCheat_RefusedOutsidePlayMode()
        {
            Send("c3", "run-cheat", new { command = "SetCoins 5" });
            _playing = false;
            _server.PumpOnce();
            var r = Result("c3");
            Assert.IsFalse(r!["ok"]!.Value<bool>());
            StringAssert.Contains("Play Mode", r["error"]!.Value<string>());
            Assert.AreEqual(0, _bridge.Run_.Count);
        }

        [Test]
        public void RunCheat_ReportsBridgeFailure()
        {
            _playing = true;
            _bridge.Result = false;
            Send("c4", "run-cheat", new { command = "Nope" });
            _server.PumpOnce();
            var r = Result("c4");
            Assert.IsTrue(r!["ok"]!.Value<bool>()); // transport ok…
            Assert.IsFalse(r["data"]!["cheatOk"]!.Value<bool>()); // …cheat itself failed
        }

        [Test]
        public void Recompile_RefusedWhilePlaying_DoesNotRecompile()
        {
            _playing = true;
            Send("c5", "recompile");
            _server.PumpOnce();
            var r = Result("c5");
            Assert.IsFalse(r!["ok"]!.Value<bool>());
            StringAssert.Contains("exit Play Mode", r["error"]!.Value<string>());
            Assert.AreEqual(0, _recompiles);
        }

        [Test]
        public void Recompile_WhileStopped_MarksProcessedThenAccepts()
        {
            _playing = false;
            Send("c6", "recompile");
            _server.PumpOnce();
            var r = Result("c6");
            Assert.IsTrue(r!["ok"]!.Value<bool>());
            Assert.IsTrue(r["data"]!["accepted"]!.Value<bool>());
            Assert.AreEqual(1, _recompiles);
            Assert.IsTrue(File.Exists(RelayPaths.ProcessedMarker(_root, "c6")));
        }

        [Test]
        public void Play_AcceptsThenFlipsPlaying()
        {
            Send("c7", "play");
            _server.PumpOnce();
            Assert.IsTrue(Result("c7")!["ok"]!.Value<bool>());
            Assert.IsTrue(_playing);
        }

        [Test]
        public void Screenshot_DefersResultUntilFileExists()
        {
            _playing = true;
            Send("c8", "screenshot");
            _server.PumpOnce(); // capture requested; env fake writes synchronously
            _server.PumpOnce(); // pending check sees the file
            var r = Result("c8");
            Assert.IsTrue(r!["ok"]!.Value<bool>());
            StringAssert.Contains("c8.png", r["data"]!["path"]!.Value<string>());
        }

        [Test]
        public void RunShot_WithoutRunnerWired_Errors()
        {
            _playing = true;
            Send("c9", "run-shot", new { name = "board_bigwin" });
            _server.PumpOnce();
            var r = Result("c9");
            Assert.IsFalse(r!["ok"]!.Value<bool>());
        }

        [Test]
        public void RunShot_PassesTheParamsObjectToTheShotRunner()
        {
            var shot = new AdShot("s", new string[0], new[] { AdStep.Wait(1) }, WaitCondition.Present("X"));
            _env.Adapter = () => new GameAdapter { GameId = "testgame", CheatBridge = _bridge, Shots = () => new[] { shot } };
            IReadOnlyDictionary<string, string>? seen = null;
            _env.StartShot = (_, bindings) => { seen = bindings; return new FakeHandle(); };
            _playing = true;

            Send("c13", "run-shot", new { name = "s", @params = new { hero = "hero.draco" } });
            _server.PumpOnce();

            Assert.IsNotNull(seen);
            Assert.AreEqual("hero.draco", seen!["hero"]);
        }

        [Test]
        public void RunShot_WithNoParams_PassesAnEmptyDictionary()
        {
            var shot = new AdShot("s", new string[0], new[] { AdStep.Wait(1) }, WaitCondition.Present("X"));
            _env.Adapter = () => new GameAdapter { GameId = "testgame", CheatBridge = _bridge, Shots = () => new[] { shot } };
            IReadOnlyDictionary<string, string>? seen = null;
            _env.StartShot = (_, bindings) => { seen = bindings; return new FakeHandle(); };
            _playing = true;

            Send("c14", "run-shot", new { name = "s" });
            _server.PumpOnce();

            Assert.IsNotNull(seen);
            Assert.AreEqual(0, seen!.Count);
        }

        // A params value that's itself a JSON object/array (not a scalar) must not throw — it's
        // meant to reach the binding layer unvalidated and be rejected there, with a proper
        // message, not blow up in the relay's own coercion.
        [Test]
        public void RunShot_WithANestedParamsValue_DoesNotThrow()
        {
            var shot = new AdShot("s", new string[0], new[] { AdStep.Wait(1) }, WaitCondition.Present("X"));
            _env.Adapter = () => new GameAdapter { GameId = "testgame", CheatBridge = _bridge, Shots = () => new[] { shot } };
            IReadOnlyDictionary<string, string>? seen = null;
            _env.StartShot = (_, bindings) => { seen = bindings; return new FakeHandle(); };
            _playing = true;

            Send("c15", "run-shot", new { name = "s", @params = new { hero = new { nested = 1 } } });
            _server.PumpOnce();

            Assert.IsNotNull(seen);
            Assert.IsNotNull(seen!["hero"]);
        }

        /// <summary>
        /// A malformed shots.json has to be visible from the CLI. It goes on ping — never on
        /// status.json, whose TS mirror (relay-protocol.ts) is documented as needing byte-for-byte
        /// parity with RelayStatus.cs.
        /// </summary>
        [Test]
        public void Ping_ReportsShotLoadErrors()
        {
            JsonShotLoader.LoadFrom("{ not valid json");
            _env.Adapter = () => new GameAdapter { GameId = "testgame", CheatBridge = _bridge };

            Send("ping-1", "ping");
            _server.PumpOnce();

            var result = Result("ping-1");
            var errors = (JArray)result!["data"]!["shotLoadErrors"]!;
            Assert.GreaterOrEqual(errors.Count, 1);
        }

        [Test]
        public void Ping_ReportsNoShotLoadErrorsAfterACleanLoad()
        {
            JsonShotLoader.LoadFrom(@"{ ""$schemaVersion"": 1, ""shots"": [] }");
            _env.Adapter = () => new GameAdapter { GameId = "testgame", CheatBridge = _bridge };

            Send("ping-2", "ping");
            _server.PumpOnce();

            var result = Result("ping-2");
            Assert.AreEqual(0, ((JArray)result!["data"]!["shotLoadErrors"]!).Count);
        }

        [Test]
        public void UnknownAction_Errors()
        {
            Send("c10", "frobnicate");
            _server.PumpOnce();
            var r = Result("c10");
            Assert.IsFalse(r!["ok"]!.Value<bool>());
            StringAssert.Contains("unknown action", r["error"]!.Value<string>());
        }

        [Test]
        public void MalformedCommand_ErrorsInsteadOfThrowing()
        {
            AtomicFile.Write(RelayPaths.Command(_root, "c11"), "{broken");
            _server.PumpOnce();
            var r = Result("c11");
            Assert.IsFalse(r!["ok"]!.Value<bool>());
        }

        [Test]
        public void Writes_LeaveNoTmpFiles()
        {
            Send("c12", "ping");
            _server.PumpOnce();
            Assert.IsEmpty(Directory.GetFiles(RelayPaths.Results(_root), "*.tmp"));
            Assert.IsEmpty(Directory.GetFiles(RelayPaths.Commands(_root), "*.tmp"));
        }
    
        // ---- kit 0.14.3 (deep review; invariant 192) ------------------------------------------------------------------

        private sealed class NoUi : IUiDriver
        {
            public bool Exists(string name) => false;
            public bool IsInteractable(string name) => false;
            public string? GetText(string name) => null;
            public bool Click(string name, int index = 0) => false;
            public void PointerDown(string name, int index = 0) { }
            public void PointerUp(string name, int index = 0) { }
        }

        /// <summary>F4 / KIT-1: a relay nobody armed ANSWERS every dropped command — refused, with how to arm it — and runs
        /// none of them: not a cheat, not Play.</summary>
        [Test]
        public void ADisarmedRelayAnswersEveryCommandAndRunsNone()
        {
            _armed = false;
            _playing = true;
            Send("d1", "run-cheat", new { command = "give 999" });
            Send("d2", "stop");
            _server.PumpOnce();
            CollectionAssert.IsEmpty(_bridge.Run_, "a cheat ran through a disarmed relay");
            Assert.IsTrue(_playing, "Play Mode was stopped through a disarmed relay");
            foreach (var id in new[] { "d1", "d2" })
            {
                var r = Result(id);
                Assert.IsNotNull(r, $"{id} was never answered — the client would only see a timeout");
                Assert.IsFalse(r!["ok"]!.Value<bool>());
                Assert.AreEqual(RelayArm.DisarmedAnswer, r["error"]!.Value<string>());
            }
            Assert.IsEmpty(Directory.GetFiles(RelayPaths.Commands(_root), "*.json"), "an answered command is not re-read");
            // CONTROL: armed, the same command runs
            _armed = true;
            Send("d3", "run-cheat", new { command = "give 999" });
            _server.PumpOnce();
            CollectionAssert.AreEqual(new[] { "give 999" }, _bridge.Run_);
        }

        [Test]
        public void TheArmSwitchIsOffUnlessARecentArmSaysSo()
        {
            var now = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
            Assert.IsFalse(RelayArm.IsArmed((string?)null, now), "never armed");
            Assert.IsFalse(RelayArm.IsArmed("", now));
            Assert.IsFalse(RelayArm.IsArmed("not a date", now), "unreadable = disarmed");
            var armedAt = RelayArm.UntilFrom(now);
            Assert.IsTrue(RelayArm.IsArmed(armedAt, now.AddMinutes(RelayArm.IdleMinutes - 1)));
            Assert.IsFalse(RelayArm.IsArmed(armedAt, now.AddMinutes(RelayArm.IdleMinutes)), "idle past the window disarms");
            Assert.IsFalse(new RelayEnv().Armed(), "a RelayEnv nobody wired is disarmed (fail closed)");
        }

        /// <summary>KIT-2 (the relay half): armed, and on a project with no delivery (the gate is off) — a `call` into the
        /// runtime still never runs. Before 0.14.3 this wrote the file.</summary>
        [Test]
        public void ARelayCheatNeverReachesTheRuntime_EvenArmedAndUngated()
        {
            var target = Path.Combine(_root, "pwned.txt");
            var adapter = new GameAdapter { GameId = "testgame", CheatBridge = new GenericCheatBridge(_root, new NoUi()) };
            _env.Adapter = () => adapter;
            _playing = true;
            Send("e1", "run-cheat", new { command = $"call System.IO.File.WriteAllText {target} pwned" });
            _server.PumpOnce();
            Assert.IsFalse(File.Exists(target), "the relay ran System.IO.File.WriteAllText");
            var r = Result("e1");
            Assert.IsNotNull(r);
            Assert.IsFalse(r!["ok"]!.Value<bool>() && r["data"]?["cheatOk"]?.Value<bool>() == true, "reported as run");
        }

        /// <summary>KIT-4: open-scene opens in Single mode, which throws unsaved edits away without asking — refused
        /// instead; and only a scene of this project's Assets/.</summary>
        [Test]
        public void OpenSceneRefusesOverUnsavedEditsAndOutsideAssets()
        {
            var opened = new List<string>();
            _env.OpenScene = p => { opened.Add(p); return p; };
            _sceneDirty = true;
            Send("s1", "open-scene", new { path = "Assets/Scenes/Boot.unity" });
            _server.PumpOnce();
            CollectionAssert.IsEmpty(opened, "a scene was opened over unsaved edits");
            Assert.AreEqual(RelayServer.OpenSceneDirtyRefusal, Result("s1")!["error"]!.Value<string>());

            _sceneDirty = false;
            var i = 0;
            foreach (var bad in new[] { "../Other/Assets/X.unity", "Assets/../../X.unity", "/Users/x/X.unity", "Packages/p/X.unity", "Assets/X.txt" })
            {
                Send("b" + i, "open-scene", new { path = bad });
                _server.PumpOnce();
                StringAssert.StartsWith("refused: open-scene takes a scene", Result("b" + i)!["error"]!.Value<string>(), bad);
                i++;
            }
            CollectionAssert.IsEmpty(opened);
            // CONTROL: saved, and under Assets/ — it opens
            Send("s2", "open-scene", new { path = "Assets/Scenes/Boot.unity" });
            _server.PumpOnce();
            CollectionAssert.AreEqual(new[] { "Assets/Scenes/Boot.unity" }, opened);
        }

        private sealed class BadMarksHandle : IShotRunHandle
        {
            public bool IsFinished => true;
            public bool AllCaptured => true;
            public string Summary => "done";
            public string? StepMarksJson => "{broken";
        }

        /// <summary>KIT-3: a finished shot whose step marks do not parse still gets its answer, and the relay keeps
        /// working — before, the parse threw out of PumpOnce on every pump and every later command waited for ever.</summary>
        [Test]
        public void AShotWhoseMarksDoNotParseIsStillAnswered_AndTheRelayKeepsWorking()
        {
            _playing = true;
            var shot = new AdShot("s", new string[0], new[] { AdStep.Wait(1) }, WaitCondition.Present("X"));
            var adapter = new GameAdapter { GameId = "testgame", CheatBridge = _bridge, Shots = () => new[] { shot } };
            _env.Adapter = () => adapter;
            _env.StartShot = (_, _) => new BadMarksHandle();
            Send("m1", "run-shot", new { name = "s" });
            _server.PumpOnce();
            Assert.DoesNotThrow(() => _server.PumpOnce());
            Assert.IsTrue(Result("m1")!["ok"]!.Value<bool>());
            Send("m2", "ping");
            _server.PumpOnce();
            Assert.IsNotNull(Result("m2"), "the relay stopped answering after the bad marks");
        }
}
}
