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
            };
            _server = new RelayServer(_root, _env);
        }

        [TearDown]
        public void TearDown()
        {
            CameraPose.ResetForTests();
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
    }
}
