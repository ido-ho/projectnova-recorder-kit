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
    /// Slice A2′ (F16) — the lever list, the approval file, and the ONE decorator that enforces it.
    /// Real files in a real temp project root, and the gate driven through the REAL director, so
    /// the seam under test is the seam that runs (invariant 101).
    /// </summary>
    public class LeversTests
    {
        private string _root = "";

        private const string ShotsWithLevers = @"{
  ""$schemaVersion"": 1,
  ""shots"": [
    { ""name"": ""a"", ""setup"": [""RollTargetType LargeCoin"", ""set Coins 5""],
      ""steps"": [ { ""kind"": ""cheat"", ""command"": ""SelectHero {hero}"" },
                   { ""kind"": ""cheatUntil"", ""command"": ""call Wave.Next"",
                     ""until"": { ""kind"": ""state"", ""name"": ""Boss"" } },
                   { ""kind"": ""click"", ""name"": ""RollBTN"" } ],
      ""settle"": { ""kind"": ""present"", ""name"": ""RollBTN"" } },
    { ""name"": ""b"", ""setup"": [""set Coins 5""],
      ""steps"": [ { ""kind"": ""wait"", ""seconds"": 1 } ],
      ""settle"": { ""kind"": ""present"", ""name"": ""X"" } }
  ]
}";

        private const string AdapterWithReady = @"{
  ""gameId"": ""g"",
  ""ready"": {
    ""mute"": ""set Music.mute true"",
    ""muteGet"": ""Music.mute"",
    ""resistOn"": ""call Player.Resist 1"",
    ""resistOff"": ""set Player.Resist 0""
  }
}";

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "levers-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RelayPaths.NovaDir(_root));
            // Another suite's gate may have refused something in this editor session; the rows
            // these tests read must hold only this test's refusals.
            LeverGateBridge.ForgetRefused();
            CameraPose.ResetForTests();
        }

        [TearDown]
        public void TearDown()
        {
            AdDirector.Active?.Finish("test teardown");
            // The gate remembers what it refused for the life of the editor session (M8): one
            // test's refusal must not turn up in another test's window rows.
            LeverGateBridge.ForgetRefused();
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
        }

        private void WriteShots(string text) =>
            File.WriteAllBytes(RelayPaths.NovaShotsFile(_root), new UTF8Encoding(false).GetBytes(text));

        private void WriteAdapter(string text) =>
            File.WriteAllBytes(SyncNova.AdapterFile(_root), new UTF8Encoding(false).GetBytes(text));

        /// <summary>Put this project in the state the gate is live in: the shots on disk ARE the
        /// ones the last sync wrote.</summary>
        private void MarkSynced()
        {
            var shots = SyncNova.Sha256OfFile(RelayPaths.NovaShotsFile(_root)) ?? "";
            var adapter = SyncNova.Sha256OfFile(SyncNova.AdapterFile(_root)) ?? "";
            File.WriteAllText(SyncNova.SyncedFile(_root), new JObject
            {
                ["shotsSha256"] = shots,
                ["adapterSha256"] = adapter,
                ["runId"] = "run-1",
                ["at"] = DateTime.UtcNow.ToString("o"),
            }.ToString());
        }

        // ---- what a set of files NEEDS ---------------------------------------------------------

        /// <summary>The fourteenth audit, ruling 3: the ready block's `muteGet` is a lever too, as the command the ready gate
        /// sends — `get &lt;muteGet&gt;`, exactly (HygieneReadyGate) — because a `get` runs a property getter, which is code.</summary>
        [Test]
        public void NeededIsEverySetupCheatAndReadyCommandMuteGetAsTheGetItSends()
        {
            WriteShots(ShotsWithLevers);
            WriteAdapter(AdapterWithReady);
            CollectionAssert.AreEqual(
                new[]
                {
                    "RollTargetType LargeCoin", "SelectHero {hero}", "call Player.Resist 1",
                    "call Wave.Next", "get Music.mute", "set Coins 5", "set Music.mute true", "set Player.Resist 0",
                },
                Levers.Needed(_root).ToArray());
            CollectionAssert.DoesNotContain(Levers.Needed(_root), "Music.mute", "the read as it is SENT, not as it is written");
        }

        [Test]
        public void NeededKeepsATemplateAsAuthored()
        {
            WriteShots(ShotsWithLevers);
            CollectionAssert.Contains(Levers.Needed(_root), "SelectHero {hero}");
        }

        [Test]
        public void NeededIsEmptyAndNeverThrowsOnFilesThatAreNotJson()
        {
            WriteShots("}{ not json");
            WriteAdapter("also not json");
            CollectionAssert.IsEmpty(Levers.Needed(_root));
            CollectionAssert.IsEmpty(Levers.NeededFrom(null, null));
        }

        [Test]
        public void NeededIsEmptyOnAProjectWithNoFilesAtAll()
        {
            CollectionAssert.IsEmpty(Levers.Needed(Path.Combine(_root, "nowhere")));
        }

        // ---- the approval file -------------------------------------------------------------------

        [Test]
        public void ApprovedIsEmptyWhenTheFileIsMissingOrMalformed()
        {
            CollectionAssert.IsEmpty(Levers.Approved(_root));
            File.WriteAllText(Levers.FilePath(_root), "{ not json");
            CollectionAssert.IsEmpty(Levers.Approved(_root));
            File.WriteAllText(Levers.FilePath(_root), @"{ ""approved"": ""set Coins 5"" }");
            CollectionAssert.IsEmpty(Levers.Approved(_root), "a non-array 'approved' must approve nothing");
        }

        [Test]
        public void SetApprovedTicksAndUnticksAndWritesTheSchemaVersion()
        {
            Assert.IsNull(Levers.SetApproved(_root, "set Coins 5", true));
            Assert.IsNull(Levers.SetApproved(_root, "SelectHero {hero}", true));
            CollectionAssert.AreEquivalent(new[] { "set Coins 5", "SelectHero {hero}" },
                Levers.Approved(_root).ToArray());

            var o = JObject.Parse(File.ReadAllText(Levers.FilePath(_root)));
            Assert.AreEqual(Levers.SchemaVersion, o["$schemaVersion"]!.Value<int>());

            Assert.IsNull(Levers.SetApproved(_root, "set Coins 5", false));
            CollectionAssert.AreEqual(new[] { "SelectHero {hero}" }, Levers.Approved(_root).ToArray());
        }

        [Test]
        public void TickingTheSameCommandTwiceDoesNotDuplicateIt()
        {
            Levers.SetApproved(_root, "set Coins 5", true);
            Levers.SetApproved(_root, "set Coins 5", true);
            Assert.AreEqual(1, Levers.Approved(_root).Count);
        }

        // ---- the matching rule ---------------------------------------------------------------------

        /// <summary>The fourteenth audit, ruling 2: the free list is the kit's own three fixed verbs, exactly. A `get` is
        /// reflection into the studio's game — a property getter is code — so it needs a tick like any lever.</summary>
        [Test]
        public void OnlyTheKitsThreeFixedVerbsAreReadOnly()
        {
            Assert.IsTrue(Levers.IsReadOnly("ui-dump"));
            Assert.IsTrue(Levers.IsReadOnly("show-ui"));
            Assert.IsTrue(Levers.IsReadOnly("hide-ui"));
            Assert.IsFalse(Levers.IsReadOnly("get Player.Coins"), "a `get` runs a getter: a lever");
            Assert.IsFalse(Levers.IsReadOnly("ui-dump "), "exactly the verb");
            Assert.IsFalse(Levers.IsReadOnly("UI-DUMP"), "exactly the verb");
            Assert.IsFalse(Levers.IsReadOnly("hide-ui SettingsGear"), "an argument makes it a write");
            Assert.IsFalse(Levers.IsReadOnly("get"));
            Assert.IsFalse(Levers.IsReadOnly("getCoins"));
            Assert.IsFalse(Levers.IsReadOnly("set Coins 5"));
            Assert.IsFalse(Levers.IsReadOnly(""));
            Assert.IsFalse(Levers.IsReadOnly(null));
        }

        [Test]
        public void AnApprovedTemplateMatchesABoundValueAndNothingElse()
        {
            var approved = new[] { "SelectHero {hero}" };
            Assert.IsTrue(Levers.Allows(approved, "SelectHero Knight"));
            Assert.IsTrue(Levers.Allows(approved, "SelectHero rogue-2.b_c"));
            Assert.IsFalse(Levers.Allows(approved, "SelectHero Knight Extra"), "a space injects a second argument");
            Assert.IsFalse(Levers.Allows(approved, "SelectHero Button@Label"), "'@' forges a label selector");
            Assert.IsFalse(Levers.Allows(approved, "SelectHero "), "an empty value is not a value");
            Assert.IsFalse(Levers.Allows(approved, "SelectHeroKnight"));
            Assert.IsFalse(Levers.Allows(approved, "set Coins 5"));
        }

        [Test]
        public void AnApprovedLiteralMatchesOnlyItself()
        {
            var approved = new[] { "set Coins 5" };
            Assert.IsTrue(Levers.Allows(approved, "set Coins 5"));
            Assert.IsFalse(Levers.Allows(approved, "set Coins 6"));
            Assert.IsFalse(Levers.Allows(approved, "set coins 5"), "ordinal, never case-insensitive");
            Assert.IsFalse(Levers.Allows(approved, "set Coins 5 "));
            Assert.IsFalse(Levers.Allows(Array.Empty<string>(), "set Coins 5"));
            Assert.IsFalse(Levers.Allows(null, "set Coins 5"));
        }

        [Test]
        public void MatchesTemplateIsShotBindingsOwnGrammar()
        {
            Assert.IsTrue(ShotBinding.MatchesTemplate("call X.Y {a} {b}", "call X.Y 1 2"));
            Assert.IsFalse(ShotBinding.MatchesTemplate("call X.Y {a} {b}", "call X.Y 1"));
            // `{NotAName}` is not a placeholder by ShotBinding's own rule, so it stays literal.
            Assert.IsTrue(ShotBinding.MatchesTemplate("call {Nope}", "call {Nope}"));
            Assert.IsFalse(ShotBinding.MatchesTemplate("call {Nope}", "call x"));
            // regex metacharacters in the literal part are literal
            Assert.IsTrue(ShotBinding.MatchesTemplate("set A.B[0] 1", "set A.B[0] 1"));
            Assert.IsFalse(ShotBinding.MatchesTemplate("set A.B[0] 1", "set AxB[0] 1"));
        }

        // ---- when the gate is live ------------------------------------------------------------------

        [Test]
        public void TheGateIsOffWithoutASyncedFile()
        {
            WriteShots(ShotsWithLevers);
            Assert.IsFalse(Levers.GateActive(_root));
        }

        [Test]
        public void TheGateIsOnWhenTheShotsOnDiskAreTheOnesTheCloudSent()
        {
            WriteShots(ShotsWithLevers);
            MarkSynced();
            Assert.IsTrue(Levers.GateActive(_root));
        }

        [Test]
        public void TheGateGoesOffAgainWhenTheFileIsEditedHere()
        {
            WriteShots(ShotsWithLevers);
            MarkSynced();
            WriteShots(ShotsWithLevers + "\n");
            Assert.IsFalse(Levers.GateActive(_root), "an edited file is local authorship — never gated");
        }

        [Test]
        public void TheGateIsOffWhenTheShotsFileIsGone()
        {
            WriteShots(ShotsWithLevers);
            MarkSynced();
            File.Delete(RelayPaths.NovaShotsFile(_root));
            Assert.IsFalse(Levers.GateActive(_root));
        }

        // ---- the decorator ----------------------------------------------------------------------------

        private sealed class RecordingBridge : ICheatBridge
        {
            public readonly List<string> Run_ = new();
            public bool Result = true;
            public bool Run(string command) { Run_.Add(command); return Result; }
        }

        private (LeverGateBridge gate, RecordingBridge inner, List<string> log) Gate()
        {
            var inner = new RecordingBridge();
            var log = new List<string>();
            return (new LeverGateBridge(inner, _root, s => log.Add(s)), inner, log);
        }

        [Test]
        public void AnUnapprovedCommandIsRefusedAndTheInnerBridgeIsNeverCalled()
        {
            WriteShots(ShotsWithLevers);
            MarkSynced();
            var (gate, inner, log) = Gate();

            Assert.IsFalse(gate.Run("set Coins 5"), "a gated command must answer false, not throw");
            CollectionAssert.IsEmpty(inner.Run_, "the command reached the game anyway");
            CollectionAssert.Contains(log, "lever not approved on this machine: 'set Coins 5'");
        }

        [Test]
        public void AnApprovedCommandGoesStraightThrough()
        {
            WriteShots(ShotsWithLevers);
            MarkSynced();
            Levers.SetApproved(_root, "set Coins 5", true);
            var (gate, inner, log) = Gate();

            Assert.IsTrue(gate.Run("set Coins 5"));
            CollectionAssert.AreEqual(new[] { "set Coins 5" }, inner.Run_);
            CollectionAssert.IsEmpty(log);
        }

        [Test]
        public void TheKitsFixedVerbsNeedNoTick_AGetNeedsOne()
        {
            WriteShots(ShotsWithLevers);
            MarkSynced();
            var (gate, inner, log) = Gate();

            Assert.IsTrue(gate.Run("ui-dump"));
            Assert.IsTrue(gate.Run("show-ui"));
            Assert.IsFalse(gate.Run("get Player.Coins"), "the fourteenth audit: a `get` is a lever");
            CollectionAssert.AreEqual(new[] { "ui-dump", "show-ui" }, inner.Run_);
            CollectionAssert.Contains(log, "lever not approved on this machine: 'get Player.Coins'");

            // CONTROL: ticked, it runs
            Assert.IsNull(Levers.SetApproved(_root, "get Player.Coins", true));
            Assert.IsTrue(gate.Run("get Player.Coins"));
            CollectionAssert.AreEqual(new[] { "ui-dump", "show-ui", "get Player.Coins" }, inner.Run_);
        }

        [Test]
        public void NothingIsGatedOnAProjectThatNeverSynced()
        {
            WriteShots(ShotsWithLevers);
            var (gate, inner, _) = Gate();
            Assert.IsTrue(gate.Run("set Coins 5"));
            CollectionAssert.AreEqual(new[] { "set Coins 5" }, inner.Run_);
        }

        [Test]
        public void AMalformedLeversFileApprovesNothing()
        {
            WriteShots(ShotsWithLevers);
            MarkSynced();
            File.WriteAllText(Levers.FilePath(_root), "{ oops");
            var (gate, inner, _) = Gate();
            Assert.IsFalse(gate.Run("set Coins 5"), "an unreadable approval file must fail CLOSED");
            CollectionAssert.IsEmpty(inner.Run_);
        }

        [Test]
        public void ATickTakesEffectWithoutRebuildingTheBridge()
        {
            WriteShots(ShotsWithLevers);
            MarkSynced();
            var (gate, inner, _) = Gate();
            Assert.IsFalse(gate.Run("set Coins 5"));
            Levers.SetApproved(_root, "set Coins 5", true);
            Assert.IsTrue(gate.Run("set Coins 5"), "the gate cached the approvals across an edit");
            CollectionAssert.AreEqual(new[] { "set Coins 5" }, inner.Run_);
        }

        [Test]
        public void TheGateReportsWhatTheGameAnsweredWhenItLetsACommandThrough()
        {
            WriteShots(ShotsWithLevers);
            MarkSynced();
            Levers.SetApproved(_root, "set Coins 5", true);
            var (gate, inner, _) = Gate();
            inner.Result = false;
            Assert.IsFalse(gate.Run("set Coins 5"), "a failed game write must stay a failed game write");
        }

        // ---- the seam in production -------------------------------------------------------------------

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

        private sealed class FakeDriver : IRecorderDriver
        {
            public string OutputDir { get; }
            public bool IsRecording { get; private set; }
            public FakeDriver(string dir) { OutputDir = dir; Directory.CreateDirectory(dir); }
            public int OutputWidth => 0;
            public int OutputHeight => 0;
            public void Start(string clipName)
            {
                IsRecording = true;
                File.WriteAllText(Path.Combine(OutputDir, clipName + "_001.mp4"), "clip");
            }
            public void Stop() => IsRecording = false;
        }

        /// <summary>
        /// THE SEAM THAT RUNS IN PRODUCTION: a shot's `setup` and its `cheat` steps go through
        /// `ctx.Cheats`, which AdDirector builds. This drives the REAL director over a gated
        /// project and asserts the game was never touched.
        /// </summary>
        private (AdDirector d, RecordingBridge cheats) RunDirector(AdShot shot,
            Action<GameAdapter>? describeAdapter = null, bool productionCameraRelease = false,
            IReadOnlyDictionary<string, string>? bindings = null)
        {
            var ui = new FakeUi();
            ui.Names.Add("Board");
            var cheats = new RecordingBridge();
            var clock = 0.0;
            var adapter = new GameAdapter
            {
                GameId = "g",
                Ui = ui,
                CheatBridge = cheats,
                Shots = () => new[] { shot },
            };
            describeAdapter?.Invoke(adapter);
            var d = AdDirector.Run(adapter, new[] { shot }, new AdDirector.Options
            {
                AutoPump = false,
                Now = () => clock,
                Recorder = new FakeDriver(Path.Combine(_root, "takes")),
                ReleaseCameraHold = productionCameraRelease ? null : () => { },
                ProjectRoot = _root,
                Bindings = bindings ?? new Dictionary<string, string>(),
            });
            Assert.IsNotNull(d);
            var ticks = 0;
            while (!d!.IsFinished && ticks++ < 100_000)
            {
                d.PumpOnce();
                clock += 0.1;
            }
            Assert.IsTrue(d.IsFinished, "director never finished");
            return (d, cheats);
        }

        private static AdShot CheatShot() =>
            new("gated", setup: new[] { "set Coins 5" },
                steps: new[] { AdStep.Cheat("SelectHero Knight") },
                settle: WaitCondition.Present("Board"));

        /// <summary>Everything the director sent the game that was not one of the kit's three fixed
        /// read-only verbs. The director itself opens every run with `show-ui` (AdDirector.cs, "restore
        /// anything a previous run's hide-ui left hidden"), and that one must keep working on a
        /// gated project — which is exactly why the read-only exemption exists.</summary>
        private static string[] Writes(RecordingBridge b) =>
            b.Run_.Where(c => !Levers.IsReadOnly(c)).ToArray();

        [Test]
        public void TheDirectorsSetupAndCheatStepsAreGated()
        {
            WriteShots(ShotsWithLevers);
            MarkSynced();
            var (d, cheats) = RunDirector(CheatShot());
            CollectionAssert.IsEmpty(Writes(cheats), "a delivered shot wrote into the game with no tick");
            CollectionAssert.Contains(cheats.Run_, "show-ui",
                "the director's own read-only restore must still reach the game");
            StringAssert.Contains("lever not approved on this machine: 'set Coins 5'", d.Summary);
            StringAssert.Contains("lever not approved on this machine: 'SelectHero Knight'", d.Summary);
        }

        [Test]
        public void TheDirectorsCheatsRunOnceTheyAreTicked()
        {
            WriteShots(ShotsWithLevers);
            MarkSynced();
            Levers.SetApproved(_root, "set Coins 5", true);
            Levers.SetApproved(_root, "SelectHero {hero}", true);
            var (_, cheats) = RunDirector(CheatShot());
            CollectionAssert.AreEqual(new[] { "set Coins 5", "SelectHero Knight" }, Writes(cheats));
        }

        [Test]
        public void ALocallyAuthoredShotRunsExactlyAsItAlwaysHas()
        {
            WriteShots(ShotsWithLevers); // …and never synced
            var (_, cheats) = RunDirector(CheatShot());
            CollectionAssert.AreEqual(new[] { "set Coins 5", "SelectHero Knight" }, Writes(cheats));
        }

        /// <summary>
        /// The `ready` block's writes go through the same `ctx.Cheats`, so they are gated by the
        /// same decorator — no second call site to keep in sync.
        /// </summary>
        [Test]
        public void TheReadyBlocksWritesGoThroughTheSameGate()
        {
            WriteShots(ShotsWithLevers);
            WriteAdapter(AdapterWithReady);
            MarkSynced();
            var (gate, inner, log) = Gate();
            var ctx = new DirectorContext(new FakeUi(), NullStateProbe.Instance, gate, () => 0, s => log.Add(s))
            {
                UpcomingShots = new[] { new UpcomingShot("a", AdShot.BaselineBoard) },
            };
            var scale = 1f;
            var ready = new HygieneReadyGate(() => HygieneSpec.Load(_root), () => scale, v => scale = v, () => "true");
            foreach (var _ in ready.WaitUntilReady(ctx)) { }
            Assert.IsFalse(ctx.LastOpSucceeded, "ready passed while its mute write was refused");
            CollectionAssert.IsEmpty(inner.Run_);
            CollectionAssert.Contains(log, "lever not approved on this machine: 'set Music.mute true'");
        }

        /// <summary>
        /// THE RELAY IS EXEMPT, DELIBERATELY: `run-cheat` is typed by the operator at the keyboard
        /// of this machine — exactly who the approval gate trusts. It calls the ADAPTER's own
        /// bridge, which is never the decorated one (the decorator is built in AdDirector's
        /// constructor and nowhere else). One lever still applies to the operator: `camera-spec`,
        /// checked inside CameraPose.Pose because it guards the adapter.json block the pose reads
        /// (third audit, M6). The check itself is pinned in CameraPoseTests
        /// (Pose_OnADeliveredProject_*); the relay → bridge → Pose path is not, because the
        /// bridge builds a Host that needs Play Mode.
        /// </summary>
        [Test]
        public void TheRelaysRunCheatIsNeverGated()
        {
            WriteShots(ShotsWithLevers);
            MarkSynced(); // fully gated project, nothing approved
            var inner = new RecordingBridge();
            var adapter = new GameAdapter { GameId = "g", CheatBridge = inner };
            var relayRoot = Path.Combine(_root, "Library", "AdRelay");
            Directory.CreateDirectory(relayRoot);
            var env = new RelayEnv
            {
                IsPlaying = () => true,
                Adapter = () => adapter,
                Now = () => 0,
            };
            var server = new RelayServer(relayRoot, env);
            AtomicFile.Write(RelayPaths.Command(relayRoot, "c1"),
                new JObject
                {
                    ["id"] = "c1", ["action"] = "run-cheat",
                    ["args"] = new JObject { ["command"] = "set Coins 5" },
                }.ToString());
            server.PumpOnce();

            Assert.IsFalse(adapter.CheatBridge is LeverGateBridge,
                "the relay's adapter bridge must be the raw one");
            CollectionAssert.AreEqual(new[] { "set Coins 5" }, inner.Run_,
                "the operator's own command was gated");
        }

        // ---- K2: the gate is keyed on EITHER file, and fails CLOSED -----------------------------

        [Test]
        public void EditingShotsAloneDoesNotUngateTheCloudsAdapter()
        {
            WriteShots(ShotsWithLevers);
            WriteAdapter(AdapterWithReady);
            MarkSynced();
            WriteShots(ShotsWithLevers + " ");
            Assert.IsTrue(Levers.GateActive(_root),
                "one byte appended to shots.json turned the gate off while adapter.json was still " +
                "byte-for-byte the cloud's — its ready block would run un-ticked");
        }

        [Test]
        public void EditingBOTHFilesTakesTheProjectBack()
        {
            WriteShots(ShotsWithLevers);
            WriteAdapter(AdapterWithReady);
            MarkSynced();
            WriteShots(ShotsWithLevers + " ");
            WriteAdapter(AdapterWithReady + " ");
            Assert.IsFalse(Levers.GateActive(_root),
                "neither file is one the cloud delivered — this is local authorship again");
        }

        [Test]
        public void ASyncedFileThatCannotBeParsedFailsCLOSED()
        {
            WriteShots(ShotsWithLevers);
            File.WriteAllText(SyncNova.SyncedFile(_root), "{ not json");
            Assert.IsTrue(Levers.GateActive(_root),
                "a synced.json this kit cannot read must gate, not open");
        }

        [Test]
        public void AV2SyncedFileGatesANYPairItEverDelivered()
        {
            WriteShots(ShotsWithLevers);
            var sha = SyncNova.Sha256OfFile(RelayPaths.NovaShotsFile(_root))!;
            File.WriteAllText(SyncNova.SyncedFile(_root), new JObject
            {
                ["$schemaVersion"] = 2,
                // The TOP-LEVEL pair is a later send; this one is only in the history.
                ["shotsSha256"] = new string('0', 64),
                ["adapterSha256"] = new string('0', 64),
                ["cloudShots"] = new JArray(new string('1', 64), sha),
                ["cloudAdapters"] = new JArray(),
            }.ToString());
            Assert.IsTrue(Levers.GateActive(_root));
        }

        // ---- K3c: the two levers that are not ICheatBridge commands -----------------------------

        private const string ShotsWithTimeScale = @"{
  ""$schemaVersion"": 1,
  ""shots"": [
    { ""name"": ""fast"",
      ""steps"": [ { ""kind"": ""timeScale"", ""factor"": 0.5 },
                   { ""kind"": ""timeScale"", ""factor"": 2 } ],
      ""settle"": { ""kind"": ""present"", ""name"": ""Board"" } }
  ]
}";

        private const string AdapterWithOverlays = @"{
  ""gameId"": ""g"",
  ""overlayTypeNames"": [ ""FpsCounter"", ""DebugHud"" ]
}";

        [Test]
        public void NeededListsTimeScaleOnceAndEveryOverlayType()
        {
            WriteShots(ShotsWithTimeScale);
            WriteAdapter(AdapterWithOverlays);
            CollectionAssert.AreEqual(
                new[] { "hide-overlay DebugHud", "hide-overlay FpsCounter", "timeScale" },
                Levers.Needed(_root).ToArray());
        }

        [Test]
        public void NeededSaysNothingAboutTimeScaleWhenNoShotChangesIt()
        {
            WriteShots(ShotsWithLevers);
            CollectionAssert.DoesNotContain(Levers.Needed(_root), Levers.TimeScaleLever);
        }

        // ---- K3b: overlays the cloud's adapter.json asks to hide ---------------------------------

        [Test]
        public void OverlaysAllowedKeepsOnlyTheTickedTypesAndNamesTheRest()
        {
            WriteShots(ShotsWithLevers);
            WriteAdapter(AdapterWithOverlays);
            MarkSynced();
            Levers.SetApproved(_root, "hide-overlay FpsCounter", true);
            var json = new[] { "FpsCounter", "DebugHud" };
            var log = new List<string>();

            var allowed = Levers.OverlaysAllowed(_root, json, json, s => log.Add(s),
                adapterIsJsonDriven: false);

            CollectionAssert.AreEqual(new[] { "FpsCounter" }, allowed.ToArray());
            CollectionAssert.AreEqual(
                new[] { "overlay 'DebugHud' left visible — lever not approved on this machine: hide-overlay DebugHud" },
                log);
        }

        /// <summary>The fourteenth audit, M1 — adapter.json may list one overlay name thousands of times in 64 KB (7,276 copies
        /// of "A" in the auditor's run): each copy was a scan of the other lists, and each un-ticked copy its own "left visible"
        /// line. On a gated project each DISTINCT name is judged once, in first-seen order: kept once, or named once.</summary>
        [Test]
        public void OverlaysAllowedJudgesEachDistinctNameOnce()
        {
            WriteShots(ShotsWithLevers);
            WriteAdapter(AdapterWithOverlays);
            MarkSynced();
            Levers.SetApproved(_root, "hide-overlay FpsCounter", true);
            var json = new[] { "DebugHud", "FpsCounter", "DebugHud", "FpsCounter", "DebugHud" };
            var log = new List<string>();

            var allowed = Levers.OverlaysAllowed(_root, json, json, s => log.Add(s), adapterIsJsonDriven: true);

            CollectionAssert.AreEqual(new[] { "FpsCounter" }, allowed.ToArray(), "kept once");
            CollectionAssert.AreEqual(new[] { Levers.OverlayLeftVisibleLog("DebugHud") }, log, "named once");
        }

        [Test]
        public void OverlaysAreNotFilteredOnAnUngatedProject()
        {
            WriteShots(ShotsWithLevers); // never synced
            WriteAdapter(AdapterWithOverlays);
            var json = new[] { "FpsCounter", "DebugHud" };
            var log = new List<string>();
            CollectionAssert.AreEqual(json,
                Levers.OverlaysAllowed(_root, json, json, s => log.Add(s), adapterIsJsonDriven: false).ToArray());
            CollectionAssert.IsEmpty(log);
        }

        [Test]
        public void AnOverlayNameFromTheStudiosOwnAdapterIsNeverGated()
        {
            WriteShots(ShotsWithLevers);
            MarkSynced();
            var log = new List<string>();
            // Nothing in adapter.json asked for it, so nobody could ever tick it: it is compiled
            // into the studio's own adapter, which is the studio's own code.
            CollectionAssert.AreEqual(new[] { "StudioHud" },
                Levers.OverlaysAllowed(_root, new[] { "StudioHud" }, Array.Empty<string>(), s => log.Add(s),
                    adapterIsJsonDriven: false).ToArray());
            CollectionAssert.IsEmpty(log);
        }

        // ---- K3a/K3b in the REAL director --------------------------------------------------------

        private static AdShot TimeScaleShot() =>
            new("fast", setup: Array.Empty<string>(),
                steps: new[] { AdStep.TimeScale(0.5) },
                settle: WaitCondition.Present("Board"));

        [Test]
        public void ATimeScaleStepNeedsItsOwnLever()
        {
            WriteShots(ShotsWithTimeScale);
            MarkSynced();
            var (d, _) = RunDirector(TimeScaleShot());
            StringAssert.Contains(Levers.NotApprovedLog(Levers.TimeScaleLever), d.Summary);
            Assert.IsFalse(d.AllCaptured, "a refused timeScale must fail its step, not pass quietly");
        }

        [Test]
        public void ATimeScaleStepRunsOnceTimeScaleIsTicked()
        {
            WriteShots(ShotsWithTimeScale);
            MarkSynced();
            Levers.SetApproved(_root, Levers.TimeScaleLever, true);
            var (d, _) = RunDirector(TimeScaleShot());
            StringAssert.DoesNotContain("lever not approved", d.Summary);
            Assert.IsTrue(d.AllCaptured, d.Summary);
        }

        [Test]
        public void TheDirectorLeavesAnUntickedOverlayVisibleAndSaysSo()
        {
            WriteShots(ShotsWithLevers);
            WriteAdapter(AdapterWithOverlays);
            MarkSynced();
            var (d, _) = RunDirector(CheatShot());
            StringAssert.Contains(Levers.OverlayLeftVisibleLog("FpsCounter"), d.Summary);
            StringAssert.Contains(Levers.OverlayLeftVisibleLog("DebugHud"), d.Summary);
        }

        [Test]
        public void TheDirectorHidesAnOverlayOnceItIsTicked()
        {
            WriteShots(ShotsWithLevers);
            WriteAdapter(AdapterWithOverlays);
            MarkSynced();
            Levers.SetApproved(_root, Levers.HideOverlayLever("FpsCounter"), true);
            Levers.SetApproved(_root, Levers.HideOverlayLever("DebugHud"), true);
            var (d, _) = RunDirector(CheatShot());
            StringAssert.DoesNotContain("left visible", d.Summary);
        }

        // ---- S1 (second audit): an overlay the CLOUD delivered, through the default adapter -------

        private static SyncNovaFiles Sent(string shots, string adapter) =>
            new(shots, adapter, SyncNova.Sha256OfText(shots), SyncNova.Sha256OfText(adapter));

        private static string ShotsNamed(string name) =>
            "{ \"$schemaVersion\": 1, \"shots\": [ { \"name\": \"" + name + "\", " +
            "\"steps\": [ { \"kind\": \"wait\", \"seconds\": 1 } ], " +
            "\"settle\": { \"kind\": \"present\", \"name\": \"Board\" } } ] }";

        private const string AdapterWithOneOverlay =
            "{ \"gameId\": \"g\", \"overlayTypeNames\": [\"PlayerHealthUI\"] }";
        private const string AdapterWithNoOverlays = "{ \"gameId\": \"g\" }";

        /// <summary>
        /// S1 — the default adapter caches adapter.json's <c>overlayTypeNames</c> at registration and
        /// the director falls back to that field when today's adapter.json lists none, while
        /// <see cref="Levers.OverlaysAllowed"/> gates only names that are in adapter.json ON DISK NOW.
        /// A type the cloud delivered yesterday was therefore disabled in the studio's running game
        /// with nothing ticked, and no line in the log.
        /// </summary>
        [Test]
        public void AnOverlayTypeTheCloudDeliveredIsNeverHiddenWithNothingTicked()
        {
            Assert.IsNull(SyncNova.Run(_root, Sent(ShotsNamed("one"), AdapterWithOneOverlay), "run-1", "g").Refusal);
            // exactly what DefaultAdapterBoot.RegisterIfNeeded stores in GameAdapter.OverlayTypeNames
            var cachedAtRegistration = AdapterJson.Load(_root).OverlayTypeNames;

            // CONTROL: while that adapter.json is still on disk, the un-ticked overlay is refused.
            var json1 = AdapterJson.Load(_root).OverlayTypeNames;
            CollectionAssert.IsEmpty(
                Levers.OverlaysAllowed(_root, AdapterJson.OverlayNamesFor(json1, cachedAtRegistration),
                    json1, null, adapterIsJsonDriven: true).ToArray(),
                "control: a delivered overlay is gated while adapter.json still lists it");

            // The cloud sends again with no overlayTypeNames; no domain reload happens in between.
            Assert.IsNull(SyncNova.Run(_root, Sent(ShotsNamed("one"), AdapterWithNoOverlays), "run-2", "g").Refusal);
            Assert.IsTrue(Levers.GateActive(_root), "control: the gate is live");
            CollectionAssert.IsEmpty(Levers.Approved(_root).ToArray(), "control: nothing is ticked");

            var json2 = AdapterJson.Load(_root).OverlayTypeNames;
            var wanted2 = AdapterJson.OverlayNamesFor(json2, cachedAtRegistration);
            CollectionAssert.IsEmpty(
                Levers.OverlaysAllowed(_root, wanted2, json2, null, adapterIsJsonDriven: true).ToArray(),
                "a type name the CLOUD delivered was disabled with nothing ticked");
        }

        /// <summary>
        /// S1, the other half: the default adapter must not CARRY adapter.json's overlay names at
        /// all. It cached them at registration, the director falls back to that field whenever
        /// today's adapter.json lists none, and nothing re-reads it until a domain reload.
        /// </summary>
        [Test]
        public void TheDefaultAdapterCachesNoOverlayNamesFromAdapterJson()
        {
            WriteShots(ShotsWithLevers);
            WriteAdapter(AdapterWithOneOverlay);

            var built = DefaultAdapterBoot.BuildDefault(_root);

            Assert.AreEqual("g", built.GameId, "control: it did read this project's adapter.json");
            Assert.IsTrue(built.IsDefaultAdapter, "the gate reads this flag to decide what is gated");
            CollectionAssert.IsEmpty(built.OverlayTypeNames,
                "the default adapter cached a cloud-delivered overlay name; it outlives the file");
        }

        /// <summary>
        /// S1 in the REAL director: a project whose adapter.json no longer lists the overlay, an
        /// adapter that still carries the name (a cache, or anything else JSON-driven), and the
        /// generic adapter — the overlay stays visible and the reason is in the run's log.
        /// </summary>
        [Test]
        public void TheDirectorGatesEveryOverlayNameAJsonDrivenAdapterCarries()
        {
            WriteShots(ShotsWithLevers);
            WriteAdapter(AdapterWithNoOverlays);
            MarkSynced();

            var (d, _) = RunDirector(CheatShot(), adapter =>
            {
                adapter.IsDefaultAdapter = true;
                adapter.OverlayTypeNames = new[] { "PlayerHealthUI" };
            });

            StringAssert.Contains(Levers.OverlayLeftVisibleLog("PlayerHealthUI"), d.Summary);
        }

        // ---- M3 (second audit): an approval whose first word is half a placeholder ----------------

        [Test]
        public void ATemplateWhoseFirstWordIsHalfLiteralApprovesNothing()
        {
            Assert.IsFalse(Levers.Allows(new[] { "s{a} {b} {c}" }, "set Player.coins 0"),
                "ticked once, 's{a} {b} {c}' would approve every three-word command there is");
            Assert.IsFalse(Levers.Allows(new[] { "{a} {b}" }, "set Coins 5"),
                "an entry that is placeholders all the way down approves anything");
        }

        [Test]
        public void ATemplateWithALiteralFirstWordStillWorks()
        {
            Assert.IsFalse(Levers.Allows(new[] { "call Shop.{a} {b}" }, "set Player.coins 0"),
                "control: a literal verb still refuses another verb");
            // the target must begin literal (the target rule): the ordinary template is `set Player.{a} {b}`
            Assert.IsTrue(Levers.Allows(new[] { "set Player.{a} {b}" }, "set Player.coins 0"),
                "control: an ordinary template must keep approving its own shape");
        }

        // ---- M2 (second audit): a synced.json that parses but says nothing ------------------------

        [Test]
        public void ASyncedJsonThatParsesButSaysNothingStillGates()
        {
            WriteShots(ShotsWithLevers);
            MarkSynced();
            Assert.IsTrue(Levers.GateActive(_root), "control");

            File.WriteAllText(SyncNova.SyncedFile(_root), "{}");
            Assert.IsTrue(Levers.GateActive(_root),
                "an empty synced.json is a record this kit cannot read — README: it gates everything");

            File.WriteAllText(SyncNova.SyncedFile(_root), "{\"cloudShots\": 5}");
            Assert.IsTrue(Levers.GateActive(_root),
                "a synced.json whose history is not a list is a record this kit cannot read");

            File.WriteAllText(SyncNova.SyncedFile(_root), "[]");
            Assert.IsTrue(Levers.GateActive(_root), "a synced.json that is not an object at all");
        }

        [Test]
        public void ASyncedJsonThatSaysOnlyItsOwnShaIsStillReadNormally()
        {
            WriteShots(ShotsWithLevers);
            // The v1 shape: the two shas, no history lists. It carries the record, so it is read —
            // the M2 rule is "says nothing", not "says less than the newest kit writes".
            File.WriteAllText(SyncNova.SyncedFile(_root), new JObject
            {
                ["shotsSha256"] = SyncNova.Sha256OfFile(RelayPaths.NovaShotsFile(_root)),
                ["adapterSha256"] = new string('0', 64),
            }.ToString());
            Assert.IsTrue(Levers.GateActive(_root));
            WriteShots(ShotsWithLevers + " ");
            Assert.IsFalse(Levers.GateActive(_root),
                "a record this kit CAN read still ungates when neither file is one it delivered");
        }

        // ---- M3: a lever whose verb is a placeholder cannot be ticked ---------------------------

        [Test]
        public void APlaceholderFirstLeverCannotBeTickedButCanAlwaysBeUnticked()
        {
            var why = Levers.SetApproved(_root, "{a} {b}", true);
            Assert.IsNotNull(why, "a command whose first word is a placeholder was ticked");
            StringAssert.Contains("first word", why!);
            CollectionAssert.IsEmpty(Levers.Approved(_root).ToArray());

            Assert.IsNull(Levers.SetApproved(_root, "set Coins 5", true), "control: an ordinary tick works");

            // One that got in anyway (a hand-edited levers.json) approves nothing, and can be taken back.
            File.WriteAllText(Levers.FilePath(_root), new JObject
            {
                ["$schemaVersion"] = Levers.SchemaVersion,
                ["approved"] = new JArray("s{a} {b} {c}", "set Coins 5"),
            }.ToString());
            Assert.IsFalse(Levers.Allows(Levers.Approved(_root), "set Player.coins 0"));
            Assert.IsNull(Levers.SetApproved(_root, "s{a} {b} {c}", false), "it must be revocable");
            CollectionAssert.AreEqual(new[] { "set Coins 5" }, Levers.Approved(_root).ToArray());
        }

        [Test]
        public void TheWindowShowsAPlaceholderFirstLeverAsUntickableWithTheReason()
        {
            WriteShots(@"{ ""$schemaVersion"": 1, ""shots"": [
                { ""name"": ""a"", ""setup"": [""{a} {b}"", ""set Coins 5""],
                  ""steps"": [ { ""kind"": ""wait"", ""seconds"": 1 } ],
                  ""settle"": { ""kind"": ""present"", ""name"": ""X"" } } ] }");
            MarkSynced();

            var rows = Levers.Rows(_root, null);
            var bad = rows.Single(r => r.Command == "{a} {b}");
            Assert.IsFalse(bad.CanTick, "the window would offer a tick that approves anything");
            StringAssert.Contains("first word", bad.Note);
            Assert.IsTrue(rows.Single(r => r.Command == "set Coins 5").CanTick, "control");

            // One that got into levers.json by hand is TICKED and does nothing (the gate ignores
            // it). It has to say so, and it has to stay revocable.
            File.WriteAllText(Levers.FilePath(_root), new JObject
            {
                ["$schemaVersion"] = Levers.SchemaVersion,
                ["approved"] = new JArray("{a} {b}"),
            }.ToString());
            var ticked = Levers.Rows(_root, null).Single(r => r.Command == "{a} {b}");
            Assert.IsTrue(ticked.Approved);
            Assert.IsTrue(ticked.CanTick, "an approval must always be revocable from this window");
            StringAssert.Contains("first word", ticked.Note,
                "a tick that approves nothing was shown as an ordinary approved lever");
        }

        // ---- K31: an approval list with something that is not a command in it -------------------

        [Test]
        public void AnApprovedEntryThatIsNotACommandMakesTheWHOLEFileUnreadable()
        {
            WriteShots(ShotsWithLevers);
            MarkSynced();
            File.WriteAllText(Levers.FilePath(_root),
                @"{ ""$schemaVersion"": 1, ""approved"": [ ""set Coins 5"", 7 ] }");

            CollectionAssert.IsEmpty(Levers.Approved(_root).ToArray(),
                "the entries read before the bad one were returned as if the file were understood");
            var (gate, inner, _) = Gate();
            Assert.IsFalse(gate.Run("set Coins 5"), "a half-read approval file must approve NOTHING");
            CollectionAssert.IsEmpty(inner.Run_);
        }

        // ---- M8: every row the Nova Capture window shows ------------------------------------------

        [Test]
        public void TheWindowListsAnApprovalNoFileAsksForAnyMoreSoItCanBeRevoked()
        {
            WriteShots(ShotsWithLevers);
            MarkSynced();
            Levers.SetApproved(_root, "call Wave.Next", true);
            Levers.SetApproved(_root, "set Player.Godmode true", true); // a lever from an older send

            var rows = Levers.Rows(_root, null);

            var stale = rows.SingleOrDefault(r => r.Command == "set Player.Godmode true");
            Assert.IsNotNull(stale,
                "an approval whose command has left the files vanished from the window and was " +
                "honoured for ever, with no way to revoke it from here");
            Assert.AreEqual(Levers.LeverSource.Approved, stale!.Source);
            Assert.IsTrue(stale.Approved);
            Assert.IsTrue(stale.CanTick, "it has to be untickable-able — that IS the revoke");
            StringAssert.Contains("untick", stale.Note);
            Assert.AreEqual(Levers.LeverSource.Needed, rows.Single(r => r.Command == "call Wave.Next").Source);
        }

        [Test]
        public void TheWindowListsACommandTheGateRefusedThatNoFileNames()
        {
            // A studio with its OWN compiled adapter: its ready gate's command is gated as soon as
            // one cloud file lands, and neither JSON file mentions it — so nothing listed it to tick.
            WriteShots(ShotsWithLevers);
            MarkSynced();
            var (gate, inner, _) = Gate();
            Assert.IsFalse(gate.Run("call MyGame.Ready"), "control: the gate refused it");
            CollectionAssert.IsEmpty(inner.Run_);

            var rows = Levers.Rows(_root, LeverGateBridge.RefusedThisSession);

            var own = rows.SingleOrDefault(r => r.Command == "call MyGame.Ready");
            Assert.IsNotNull(own, "a command the gate refused was nowhere to be ticked");
            Assert.AreEqual(Levers.LeverSource.RefusedHere, own!.Source);
            Assert.IsFalse(own.Approved);
            Assert.IsTrue(own.CanTick);
            // Third audit, M1: only what the gate KNOWS — never "your own adapter code".
            Assert.AreEqual(Levers.RefusedThisSessionNote, own.Note);

            // …and ticking it is what lets that adapter work.
            Assert.IsNull(Levers.SetApproved(_root, "call MyGame.Ready", true));
            Assert.IsTrue(gate.Run("call MyGame.Ready"));

            // The sixth audit: from then on the row says the FILES do not need it — true of the files, and what
            // TRUST.md and README.md say of a command your own compiled adapter runs from code.
            var ticked = Levers.Rows(_root, LeverGateBridge.RefusedThisSession).Single(r => r.Command == "call MyGame.Ready");
            Assert.AreEqual(Levers.LeverSource.Approved, ticked.Source);
            Assert.AreEqual(Levers.NotNeededNote, ticked.Note);
            CollectionAssert.DoesNotContain(Levers.Needed(_root), "call MyGame.Ready", "control: no file on disk names it");
        }

        [Test]
        public void ARefusedCommandThatAFileDoesAskForIsListedOnceAsNeeded()
        {
            WriteShots(ShotsWithLevers);
            MarkSynced();
            var (gate, _, _) = Gate();
            Assert.IsFalse(gate.Run("set Coins 5"));

            var rows = Levers.Rows(_root, LeverGateBridge.RefusedThisSession);
            Assert.AreEqual(1, rows.Count(r => r.Command == "set Coins 5"), "the same lever twice");
            Assert.AreEqual(Levers.LeverSource.Needed, rows.Single(r => r.Command == "set Coins 5").Source);
        }

        [Test]
        public void TheGateRemembersOnlyTheNewestRefusals()
        {
            WriteShots(ShotsWithLevers);
            MarkSynced();
            var (gate, _, _) = Gate();
            for (var i = 0; i < LeverGateBridge.MaxRefusedRemembered + 5; i++)
                gate.Run("set Thing" + i + " 1");

            var remembered = LeverGateBridge.RefusedThisSession;
            Assert.AreEqual(LeverGateBridge.MaxRefusedRemembered, remembered.Count,
                "a shot loop must not be able to grow a static without end");
            CollectionAssert.Contains(remembered, "set Thing" + (LeverGateBridge.MaxRefusedRemembered + 4) + " 1");
            CollectionAssert.DoesNotContain(remembered, "set Thing0 1");
        }

        // ---- M9: the camera block is cloud content, and it names methods ------------------------

        [Test]
        public void TheCameraSpecLeverIsExactlyTheTextBothSidesCompute()
        {
            Assert.AreEqual("camera-spec BoardCameraRig SetPosition SetRotation SetFov",
                Levers.CameraSpecLever(new CameraAdapter("BoardCameraRig", null, null, null, null)));
            Assert.AreEqual("camera-spec * SetPosition DeleteSave SetFov",
                Levers.CameraSpecLever(new CameraAdapter(null, null, null, "DeleteSave", null)));
            Assert.AreEqual("camera-spec Rig Move SetRotation SetFov",
                Levers.CameraSpecLever(new CameraAdapter("  Rig  ", null, " Move ", null, null)));

            Assert.IsFalse(Levers.CameraSpecNeeded(new CameraAdapter(null, "orthographic", null, null, null)),
                "an empty camera block asks for nothing");
            Assert.IsFalse(Levers.CameraSpecNeeded(
                    new CameraAdapter(null, null, "SetPosition", "SetRotation", "SetFov")),
                "the defaults spelled out ask for nothing");
            Assert.IsTrue(Levers.CameraSpecNeeded(new CameraAdapter(null, null, null, "DeleteSave", null)));
            Assert.IsTrue(Levers.CameraSpecNeeded(new CameraAdapter("Rig", null, null, null, null)));
        }

        [Test]
        public void NeededListsTheCameraSpecWhenAdapterJsonCarriesOne()
        {
            WriteShots(ShotsWithLevers);
            WriteAdapter(@"{ ""gameId"": ""g"", ""camera"": { ""viewType"": ""BoardCameraRig"",
                ""projection"": ""perspective"", ""setRotation"": ""DeleteSave"" } }");

            CollectionAssert.Contains(Levers.Needed(_root),
                "camera-spec BoardCameraRig SetPosition DeleteSave SetFov");

            WriteAdapter(@"{ ""gameId"": ""g"", ""camera"": {} }");
            Assert.IsFalse(Levers.Needed(_root).Any(l => l.StartsWith(Levers.CameraSpecPrefix)),
                "an empty camera block asks for nothing, so nothing is listed to tick");
        }

        // ---- M4: one answer to "which folder is this project" ------------------------------------

        /// <summary>
        /// M4 — the delivery writes the cloud's two files under the Editor's own project
        /// (Application.dataPath/..), while the gate, the window and the default adapter read them
        /// from the process's working directory, which any script in the editor can move. Each of
        /// the three is pinned on its own, against a whole DECOY project sitting in the working
        /// directory: one resolver, or three answers to "which game is this".
        /// </summary>
        private string Decoy()
        {
            var decoy = Path.Combine(_root, "decoy-project");
            Directory.CreateDirectory(RelayPaths.NovaDir(decoy));
            File.WriteAllText(RelayPaths.NovaShotsFile(decoy), @"{ ""$schemaVersion"": 1, ""shots"": [
                { ""name"": ""decoy"", ""setup"": [""set Decoy 1""],
                  ""steps"": [ { ""kind"": ""wait"", ""seconds"": 1 } ],
                  ""settle"": { ""kind"": ""present"", ""name"": ""X"" } } ] }");
            File.WriteAllText(SyncNova.AdapterFile(decoy), @"{ ""gameId"": ""decoy-game"" }");
            File.WriteAllText(SyncNova.SyncedFile(decoy), new JObject
            {
                ["$schemaVersion"] = 2,
                ["shotsSha256"] = SyncNova.Sha256OfFile(RelayPaths.NovaShotsFile(decoy)),
                ["adapterSha256"] = SyncNova.Sha256OfFile(SyncNova.AdapterFile(decoy)),
            }.ToString());
            Assert.IsTrue(Levers.GateActive(decoy), "control: the decoy IS a gated project");
            return decoy;
        }

        private static string OpenProject => Path.GetDirectoryName(UnityEngine.Application.dataPath)!;

        /// <summary>Run <paramref name="body"/> with the process somewhere else entirely. The
        /// working directory is process-wide, so it is put back whatever happens.</summary>
        private static void WithWorkingDirectoryAt(string dir, Action body)
        {
            var before = Directory.GetCurrentDirectory();
            try
            {
                Directory.SetCurrentDirectory(dir);
                body();
            }
            finally
            {
                Directory.SetCurrentDirectory(before);
            }
        }

        [Test]
        public void TheResolverAnswersWithTheProjectTheEditorHasOpen()
        {
            WithWorkingDirectoryAt(Decoy(), () =>
                Assert.AreEqual(OpenProject, KitProject.Root(),
                    "the kit's idea of 'this project' followed the working directory"));
        }

        [Test]
        public void TheGateLooksForSyncedJsonUnderTheProjectNotTheWorkingDirectory()
        {
            WithWorkingDirectoryAt(Decoy(), () =>
            {
                // A director built the way every production caller builds one: no ProjectRoot passed.
                var d = AdDirector.Run(
                    new GameAdapter { GameId = "g", Ui = new FakeUi(), CheatBridge = new RecordingBridge() },
                    Array.Empty<AdShot>(),
                    new AdDirector.Options
                    {
                        AutoPump = false,
                        Now = () => 0,
                        Recorder = new FakeDriver(Path.Combine(_root, "takes")),
                        ReleaseCameraHold = () => { },
                    });
                Assert.IsNotNull(d);
                Assert.AreEqual(OpenProject, d!.ProjectRoot,
                    "the gate looked for synced.json in whatever folder the process happened to be in — " +
                    "it finds none, reads that as local authorship, and runs every delivered cheat un-ticked");
                d.Finish("test");
            });
        }

        [Test]
        public void TheWindowListsTheOpenProjectsLeversNotTheWorkingDirectorys()
        {
            WithWorkingDirectoryAt(Decoy(), () =>
                CollectionAssert.DoesNotContain(
                    NovaCaptureWindow.RowsForThisProject().Select(r => r.Command).ToArray(),
                    "set Decoy 1",
                    "the window offered ticks for another folder's shots.json"));
        }

        [Test]
        public void TheDefaultAdapterIsBuiltForTheProjectTheEditorHasOpen()
        {
            WithWorkingDirectoryAt(Decoy(), () =>
                Assert.AreNotEqual("decoy-game", DefaultAdapterBoot.BuildForThisProject().GameId,
                    "the generic adapter took its game id from the working directory"));
        }

        // ==== THE THIRD AUDIT (fresh context, 2026-09-21) ==========================================

        private static string ShotsWithSetup(string setupJsonArray, string parameters = "[]") =>
            "{ \"$schemaVersion\": 1, \"shots\": [ { \"name\": \"a\", \"parameters\": " + parameters +
            ", \"setup\": " + setupJsonArray + ", \"steps\": [ { \"kind\": \"wait\", \"seconds\": 1 } ], " +
            "\"settle\": { \"kind\": \"present\", \"name\": \"X\" } } ] }";

        private void WriteApprovedByHand(params string[] entries) =>
            File.WriteAllText(Levers.FilePath(_root), new JObject
            {
                ["$schemaVersion"] = Levers.SchemaVersion,
                ["approved"] = new JArray(entries.Cast<object>().ToArray()),
            }.ToString());

        // ---- S1: ONE function decides which approval covers a command -----------------------------

        [Test]
        public void CoveringApprovalIsExactTextFirstThenTheFirstTemplateThenNothing()
        {
            // exact beats a template listed before it — a template ticked verbatim must cover itself
            Assert.AreEqual("SelectHero Knight",
                Levers.CoveringApproval(new[] { "SelectHero {hero}", "SelectHero Knight" }, "SelectHero Knight"));
            Assert.AreEqual("SelectHero {hero}",
                Levers.CoveringApproval(new[] { "SelectHero {hero}" }, "SelectHero {hero}"),
                "a template ticked verbatim did not cover itself ({hero} is not a legal bound value)");
            Assert.AreEqual("SelectHero {hero}", Levers.CoveringApproval(new[] { "SelectHero {hero}" }, "SelectHero Knight"));
            Assert.AreEqual("call Shop.{a}", Levers.CoveringApproval(new[] { "call Shop.{a}", "call Shop.{b}" }, "call Shop.X"),
                "the FIRST template in file order");
            Assert.IsNull(Levers.CoveringApproval(new[] { "SelectHero {hero}" }, "SelectHero Knight Extra"));
            Assert.IsNull(Levers.CoveringApproval(null, "set Coins 5"));
            Assert.IsNull(Levers.CoveringApproval(new[] { "set Coins 5" }, null));
            Assert.IsNull(Levers.CoveringApproval(new[] { "set Coins 5" }, ""));
            // every refusal the gate had is kept: a first word that is not literal covers NOTHING,
            // not even its own exact text
            Assert.IsNull(Levers.CoveringApproval(new[] { "{a} {b}" }, "{a} {b}"));
            Assert.IsNull(Levers.CoveringApproval(new[] { "{a} {b}" }, "set Coins"));
            Assert.IsNull(Levers.CoveringApproval(new[] { "s{a} {b} {c}" }, "set Player.coins 0"));
        }

        [Test]
        public void AllowsIsExactlyCoveringApprovalAndNothingElse()
        {
            var approved = new[] { "SelectHero {hero}", "set Coins 5", "{a} {b}", "hide-overlay {t}", "call {x} {y}" };
            foreach (var command in new[]
                     {
                         "SelectHero {hero}", "SelectHero Knight", "SelectHero Knight Extra", "set Coins 5",
                         "set Coins 6", "{a} {b}", "hide-overlay {t}", "hide-overlay Hud", "call Save.DeleteAll 1",
                         "timeScale",
                     })
                Assert.AreEqual(Levers.CoveringApproval(approved, command) != null, Levers.Allows(approved, command),
                    $"Allows and CoveringApproval disagree on '{command}'");
            Assert.IsTrue(Levers.Allows(approved, "SelectHero {hero}"),
                "the gate refused a template's own text that the window shows ticked");
        }

        /// <summary>
        /// P7 (the auditor's probe, kept): a cloud shot's template is ticked — which is what
        /// `parameters:` are for — and a later send carries a LITERAL binding of it. The window
        /// showed that row with an EMPTY box while the gate ran it, and unticking it could revoke
        /// nothing: it is not in levers.json.
        /// </summary>
        [Test]
        public void ACommandATickedTemplateCoversIsShownTickedAndNamesTheTemplate()
        {
            Assert.IsNull(SyncNova.Run(_root, Sent(ShotsWithSetup("[\"SelectHero {hero}\"]", "[\"hero\"]"),
                AdapterWithNoOverlays), "run-1", "g").Refusal);
            Assert.IsNull(Levers.SetApproved(_root, "SelectHero {hero}", true), "control: the template is ticked");
            Assert.IsNull(SyncNova.Run(_root, Sent(ShotsWithSetup("[\"SelectHero Knight\"]"),
                AdapterWithNoOverlays), "run-2", "g").Refusal);
            Assert.IsTrue(Levers.GateActive(_root), "control: gated");

            var row = Levers.Rows(_root, null).Single(r => r.Command == "SelectHero Knight");
            Assert.IsTrue(row.Approved, "the window shows UNTICKED a command the gate runs");
            Assert.AreEqual("SelectHero {hero}", row.CoveredBy);
            Assert.IsFalse(row.CanTick, "unticking a row that is not in levers.json revokes nothing");
            Assert.AreEqual(Levers.CoveredByTemplateNote("SelectHero {hero}"), row.Note);
            StringAssert.Contains("untick that to revoke", row.Note);
            var (gate, inner, _) = Gate();
            Assert.IsTrue(gate.Run("SelectHero Knight"), "control: the gate does run it");

            // …and the template's OWN row is where it is revoked.
            var template = Levers.Rows(_root, null).Single(r => r.Command == "SelectHero {hero}");
            Assert.IsTrue(template.Approved);
            Assert.IsTrue(template.CanTick);
            Assert.IsNull(Levers.SetApproved(_root, "SelectHero {hero}", false));
            var after = Levers.Rows(_root, null).Single(r => r.Command == "SelectHero Knight");
            Assert.IsFalse(after.Approved);
            Assert.IsTrue(after.CanTick);
            Assert.IsFalse(gate.Run("SelectHero Knight"), "control: revoked at the template");
            CollectionAssert.AreEqual(new[] { "SelectHero Knight" }, inner.Run_);
        }

        /// <summary>
        /// "The checkbox and the gate agree on every row": a mixed project — exact ticks, templates,
        /// overlays, a camera block, timeScale, an approval nothing asks for, a refused command —
        /// and for every row the box is what the gate does: <see cref="Levers.Allows"/>, and the
        /// REAL enforcement point for the row's kind (the bridge for a command, the overlay filter
        /// for an overlay, <see cref="Levers.LeverAllowed"/> for timeScale / camera-spec). The one
        /// row excluded is the one with a not-tickable reason: a hand-edited entry the gate ignores,
        /// shown ticked so it can be taken out of the file (asserted on its own below). The fixture
        /// holds no READ on purpose: a read's box is ticked and the gate runs it, but
        /// <see cref="Levers.Allows"/> — the covering rule — is false for it (the gate lets a read
        /// through before it asks); reads are held to the gate in
        /// <see cref="AReadOnlyRowIsApprovedCannotBeTickedAndSaysItNeedsNoTick"/>.
        /// </summary>
        [Test]
        public void TheCheckboxAndTheGateAgreeOnEveryRow()
        {
            WriteShots(@"{ ""$schemaVersion"": 1, ""shots"": [
                { ""name"": ""a"", ""parameters"": [""hero""],
                  ""setup"": [""SelectHero {hero}"", ""SelectHero Knight"", ""set Coins 5"", ""call Shop.Buy 3"", ""set Gems 9""],
                  ""steps"": [ { ""kind"": ""timeScale"", ""factor"": 0.5 } ],
                  ""settle"": { ""kind"": ""present"", ""name"": ""X"" } } ] }");
            WriteAdapter(@"{ ""gameId"": ""g"", ""overlayTypeNames"": [""PlayerHealthUI"", ""FpsCounter""],
                ""camera"": { ""viewType"": ""BoardRig"", ""projection"": ""perspective"" } }");
            MarkSynced();
            WriteApprovedByHand("SelectHero {hero}", "set Coins 5", "call Shop.{m} {n}", "hide-overlay FpsCounter",
                "hide-overlay {t}", "camera-spec {v} SetPosition SetRotation SetFov", "set Player.Godmode true",
                "{a} {b}");
            var (gate, _, _) = Gate();
            Assert.IsFalse(gate.Run("call MyGame.Ready 1 2"), "control: refused (and remembered)");

            var approved = Levers.Approved(_root);
            var rows = Levers.Rows(_root, LeverGateBridge.RefusedThisSession);
            CollectionAssert.IsSubsetOf(
                new[] { "SelectHero Knight", "call Shop.Buy 3", "hide-overlay PlayerHealthUI", "hide-overlay FpsCounter",
                        "camera-spec BoardRig SetPosition SetRotation SetFov", "timeScale", "set Gems 9",
                        "set Player.Godmode true", "call MyGame.Ready 1 2" },
                rows.Select(r => r.Command).ToArray(), "control: the fixture reaches every kind of row");

            foreach (var row in rows.Where(r => Levers.NotTickableReason(r.Command) == null))
            {
                Assert.AreEqual(Levers.Allows(approved, row.Command), row.Approved,
                    $"the box and Levers.Allows disagree on '{row.Command}'");
                bool gateRuns;
                if (row.Command.StartsWith("hide-overlay "))
                {
                    var name = row.Command.Substring("hide-overlay ".Length);
                    gateRuns = Levers.OverlaysAllowed(_root, new[] { name }, new[] { name }, null,
                        adapterIsJsonDriven: true).Contains(name);
                }
                else if (row.Command == Levers.TimeScaleLever || row.Command.StartsWith(Levers.CameraSpecPrefix + " "))
                    gateRuns = Levers.LeverAllowed(_root, row.Command);
                else
                    gateRuns = gate.Run(row.Command);
                Assert.AreEqual(gateRuns, row.Approved, $"the box says {row.Approved}, the gate does {gateRuns}: '{row.Command}'");
            }

            // the rows this fixture is FOR
            Assert.IsTrue(rows.Single(r => r.Command == "SelectHero Knight").Approved);
            Assert.IsTrue(rows.Single(r => r.Command == "call Shop.Buy 3").Approved, "covered by the ticked `call Shop.{m} {n}`");
            Assert.IsFalse(rows.Single(r => r.Command == "hide-overlay PlayerHealthUI").Approved,
                "`hide-overlay {t}` is not a template");
            Assert.IsFalse(rows.Single(r => r.Command == "camera-spec BoardRig SetPosition SetRotation SetFov").Approved,
                "`camera-spec {v} …` is not a template");
            // …and the one row outside the rule: in the file, ignored by the gate, shown ticked with its reason
            var dead = rows.Single(r => r.Command == "{a} {b}");
            Assert.IsTrue(dead.Approved);
            Assert.IsTrue(dead.CanTick, "whatever is in the file can always be taken back");
            Assert.IsFalse(Levers.Allows(approved, "{a} {b}"));
            StringAssert.Contains("first word", dead.Note);
        }

        /// <summary>
        /// P6 (the auditor's probe, reworked for the fix): an adapter.json overlay type name is a
        /// DATA field nothing declares, and `hide-overlay {t}` used to be an ordinary tickable row
        /// that the gate then read as a TEMPLATE — ticked once, it switched off every overlay a
        /// later adapter.json named, each shown unticked. Now: it cannot be ticked, and one that got
        /// into levers.json by hand still covers only its own exact text.
        /// </summary>
        [Test]
        public void AnOverlayLeverIsNeverATemplate()
        {
            var shots = ShotsNamed("one");
            // Written straight to disk, and marked delivered: since the ninth audit SyncNova refuses a delivery naming the
            // overlay {t} (a placeholder nothing binds), but the window and the gate still have to read one that is here.
            WriteShots(shots);
            WriteAdapter("{ \"gameId\": \"g\", \"overlayTypeNames\": [\"{t}\"] }");
            MarkSynced();
            var offered = Levers.Rows(_root, null).Single(r => r.Command == "hide-overlay {t}");
            Assert.IsFalse(offered.CanTick, "a type name with a brace was offered as an ordinary tickable lever");
            StringAssert.Contains("'{'", offered.Note);
            Assert.IsNotNull(Levers.SetApproved(_root, "hide-overlay {t}", true), "a brace-holding overlay lever was ticked");
            CollectionAssert.IsEmpty(Levers.Approved(_root).ToArray());

            // …it got into the file by hand anyway, and the cloud then names a real type
            WriteApprovedByHand("hide-overlay {t}");
            Assert.IsNull(SyncNova.Run(_root, Sent(shots, "{ \"gameId\": \"g\", \"overlayTypeNames\": [\"PlayerHealthUI\"] }"),
                "run-2", "g").Refusal);
            Assert.IsTrue(Levers.GateActive(_root), "control: gated");
            Assert.IsFalse(Levers.Rows(_root, null).Single(r => r.Command == "hide-overlay PlayerHealthUI").Approved);
            var json = AdapterJson.Load(_root).OverlayTypeNames;
            CollectionAssert.IsEmpty(Levers.OverlaysAllowed(_root, json, json, null, adapterIsJsonDriven: true).ToArray(),
                "an overlay the window shows as NOT ticked was switched off by the gate");
            Assert.IsFalse(Levers.Allows(Levers.Approved(_root), "hide-overlay PlayerHealthUI"));

            // CONTROL: its own exact tick still hides it
            Assert.IsNull(Levers.SetApproved(_root, "hide-overlay PlayerHealthUI", true));
            CollectionAssert.AreEqual(new[] { "PlayerHealthUI" },
                Levers.OverlaysAllowed(_root, json, json, null, adapterIsJsonDriven: true).ToArray());
        }

        /// <summary>The camera block's names are the same kind of data field (the third audit's
        /// advisor named it): a hand-edited `camera-spec {v} …` must not approve every view type.</summary>
        [Test]
        public void ACameraSpecLeverIsNeverATemplate()
        {
            WriteShots(ShotsWithLevers);
            WriteAdapter(@"{ ""gameId"": ""g"", ""camera"": { ""viewType"": ""BoardRig"", ""projection"": ""perspective"" } }");
            MarkSynced();
            Assert.IsNotNull(Levers.SetApproved(_root, "camera-spec {v} SetPosition SetRotation SetFov", true));
            WriteApprovedByHand("camera-spec {v} SetPosition SetRotation SetFov");
            Assert.IsFalse(Levers.LeverAllowed(_root, "camera-spec BoardRig SetPosition SetRotation SetFov"),
                "a camera-spec template approved a view type nobody ticked");
            Assert.IsNull(Levers.SetApproved(_root, "camera-spec BoardRig SetPosition SetRotation SetFov", true));
            Assert.IsTrue(Levers.LeverAllowed(_root, "camera-spec BoardRig SetPosition SetRotation SetFov"), "control");
        }

        // ---- IsTicked: slice E's question, on the same rule -----------------------------------------

        [Test]
        public void IsTickedIsTheCoveringRuleWithReadOnlyAndTheFirstWordInFront()
        {
            Assert.IsFalse(Levers.IsTicked(new[] { "set X 1" }, null));
            Assert.IsFalse(Levers.IsTicked(new[] { "set X 1" }, ""));
            Assert.IsFalse(Levers.IsTicked(null, "set X 1"));
            Assert.IsTrue(Levers.IsTicked(null, "ui-dump"), "a read-only command needs no tick");
            Assert.IsFalse(Levers.IsTicked(Array.Empty<string>(), "get Player.Coins"), "a `get` is a lever (the fourteenth audit)");
            Assert.IsTrue(Levers.IsTicked(new[] { "get Player.Coins" }, "get Player.Coins"));
            Assert.IsTrue(Levers.IsTicked(new[] { "set X 1" }, "set X 1"));
            Assert.IsFalse(Levers.IsTicked(new[] { "set X 1" }, "set X 2"));
            Assert.IsTrue(Levers.IsTicked(new[] { "SelectHero {hero}" }, "SelectHero {hero}"),
                "a template ticked verbatim is ticked — the window shows it so");
            Assert.IsTrue(Levers.IsTicked(new[] { "SelectHero {hero}" }, "SelectHero Knight"));
            Assert.IsFalse(Levers.IsTicked(new[] { "{a} {b}" }, "{a} {b}"), "a placeholder first word is never ticked");
            Assert.IsFalse(Levers.IsTicked(new[] { "\"x\" {a}" }, "\"x\" {a}"), "nor a quoted one");
            Assert.IsFalse(Levers.IsTicked(new[] { "hide-overlay {t}" }, "hide-overlay Hud"));
            Assert.IsTrue(Levers.IsTicked(new[] { "hide-overlay Hud" }, "hide-overlay Hud"));
        }

        // ---- M1: the note says only what the gate knows; a bound command is its template's row ----

        /// <summary>P3 (a), kept: a cloud shot's template, run with a binding, is refused — and the
        /// refused bound command was listed AGAIN, as "asked for by your own adapter code".</summary>
        [Test]
        public void ARefusedBindingOfANeededTemplateIsFoldedIntoTheTemplatesRow()
        {
            Assert.IsNull(SyncNova.Run(_root, Sent(ShotsWithSetup("[\"SelectHero {hero}\"]", "[\"hero\"]"),
                AdapterWithNoOverlays), "run-1", "g").Refusal);
            Assert.IsTrue(ShotBinding.TryApply("SelectHero {hero}", new[] { "hero" },
                new Dictionary<string, string> { ["hero"] = "Knight" }, out var bound, out var err), err);
            var (gate, _, _) = Gate();
            Assert.IsFalse(gate.Run(bound), "control: refused");
            Assert.IsFalse(gate.Run("call MyGame.Ready"), "control: refused, and no template matches it");

            var rows = Levers.Rows(_root, LeverGateBridge.RefusedThisSession);
            CollectionAssert.DoesNotContain(rows.Select(r => r.Command).ToArray(), "SelectHero Knight",
                "a binding of a template the files ask for was listed again, as a row of its own");
            var template = rows.Single(r => r.Command == "SelectHero {hero}");
            Assert.AreEqual(Levers.LeverSource.Needed, template.Source);
            Assert.AreEqual(Levers.RefusedAsNote(new[] { "SelectHero Knight" }), template.Note);
            StringAssert.Contains("`SelectHero Knight`", template.Note);
            // CONTROL: a refusal nothing on disk matches is still listed, with the true sentence
            Assert.AreEqual(Levers.RefusedThisSessionNote, rows.Single(r => r.Command == "call MyGame.Ready").Note);
        }

        /// <summary>Fourth-audit fold, follow-up to M5: a refused binding that fits a template the
        /// files ask for, but that the template's tick would STILL refuse (a dotted value in a target
        /// placeholder), was folded under "ticking this approves that" — false. It is listed on its
        /// own, saying exactly why the tick would not help.</summary>
        [Test]
        public void ARefusedBindingTheTemplatesTickWouldStillRefuseIsNotFoldedIntoItsRow()
        {
            Assert.IsNull(SyncNova.Run(_root, Sent(ShotsWithSetup("[\"set Player.{s} {v}\"]", "[\"s\", \"v\"]"),
                AdapterWithNoOverlays), "run-1", "g").Refusal);
            var (gate, _, _) = Gate();
            Assert.IsFalse(gate.Run("set Player.Instance.save.coins 0"), "control: refused");
            Assert.IsFalse(gate.Run("set Player.coins 0"), "control: refused");
            // the facts the notes must agree with
            Assert.IsTrue(Levers.Allows(new[] { "set Player.{s} {v}" }, "set Player.coins 0"));
            Assert.IsFalse(Levers.Allows(new[] { "set Player.{s} {v}" }, "set Player.Instance.save.coins 0"));

            var rows = Levers.Rows(_root, LeverGateBridge.RefusedThisSession);
            var template = rows.Single(r => r.Command == "set Player.{s} {v}");
            Assert.AreEqual(Levers.RefusedAsNote(new[] { "set Player.coins 0" }), template.Note,
                "the template's row promised that ticking it approves a command its tick would still refuse");
            var beyond = rows.Single(r => r.Command == "set Player.Instance.save.coins 0");
            Assert.AreEqual(Levers.LeverSource.RefusedHere, beyond.Source);
            Assert.AreEqual(Levers.RefusedBeyondTemplateNote("set Player.{s} {v}"), beyond.Note);
        }

        /// <summary>P3 (b), kept: a command the WEBSITE sent, refused by a capture, then dropped by
        /// the next send, was labelled as the studio's own adapter code.</summary>
        [Test]
        public void ACloudCommandThatLeftTheFilesIsNotLabelledAsTheStudiosOwnCode()
        {
            Assert.IsNull(SyncNova.Run(_root, Sent(ShotsWithSetup("[\"call Save.DeleteAll\"]"), AdapterWithNoOverlays),
                "run-1", "g").Refusal);
            var (gate, _, _) = Gate();
            Assert.IsFalse(gate.Run("call Save.DeleteAll"), "control: refused while the cloud's file asks for it");
            Assert.IsNull(SyncNova.Run(_root, Sent(ShotsWithSetup("[]"), AdapterWithNoOverlays), "run-2", "g").Refusal);

            var row = Levers.Rows(_root, LeverGateBridge.RefusedThisSession).Single(r => r.Command == "call Save.DeleteAll");
            Assert.AreEqual(Levers.LeverSource.RefusedHere, row.Source, "control");
            StringAssert.DoesNotContain("your own adapter code", row.Note,
                "a command the WEBSITE sent is labelled as the studio's own adapter code");
            Assert.AreEqual("refused in this editor session; no file on disk asks for it now", row.Note);
        }

        // ---- M2: a quote in the first word ---------------------------------------------------------

        [Test]
        public void AFirstWordWithAQuoteApprovesNothingAndCannotBeTicked()
        {
            // CONTROL: the bridge really does read the quoted span as the verb
            Assert.AreEqual(" DeleteSave", ReflectionCheatBridge.SplitCommand("\" DeleteSave\" now")[0]);
            Assert.IsTrue(ShotBinding.MatchesTemplate("\" {a}\" {b}", "\" DeleteSave\" now"));

            Assert.IsFalse(Levers.Allows(new[] { "\" {a}\" {b}" }, "\" DeleteSave\" now"),
                "an approval whose VERB, as the bridge splits it, is a placeholder approved a command");
            Assert.IsFalse(Levers.Allows(new[] { "'x' {a}" }, "'x' y"));
            Assert.IsFalse(Levers.Allows(new[] { "\"set\" Coins 5" }, "\"set\" Coins 5"), "not even its own text");
            var why = Levers.SetApproved(_root, "\" {a}\" {b}", true);
            Assert.IsNotNull(why, "a lever whose verb is a placeholder in quotes was ticked");
            StringAssert.Contains("first word must be literal", why!);
            Assert.IsNotNull(Levers.SetApproved(_root, "'x' {a}", true));
            CollectionAssert.IsEmpty(Levers.Approved(_root).ToArray());

            // CONTROL: a quote AFTER the first word is an ordinary argument
            Assert.IsNull(Levers.SetApproved(_root, "call Dialog.Say \"Some Arg\"", true));
            Assert.IsTrue(Levers.Allows(Levers.Approved(_root), "call Dialog.Say \"Some Arg\""));
            StringAssert.Contains("first word must be literal", Levers.NotTickableReason("{a} {b}")!,
                "the placeholder sentence is the same family");
        }

        // ---- S3 (surviving mutant): the leading-whitespace skip ---------------------------------------

        [Test]
        public void APaddedPlaceholderFirstEntryApprovesNothing()
        {
            Assert.IsFalse(Levers.Allows(new[] { " {a} {b} {c}" }, " set Player.coins 0"),
                "ticked once, ' {a} {b} {c}' approves ' set Player.coins 0', which the bridge runs as `set`");
            Assert.IsNotNull(Levers.SetApproved(_root, " {a} {b} {c}", true));
            // control: a padded literal verb — with a target that BEGINS literal (the target rule below)
            Assert.IsTrue(Levers.Allows(new[] { " set Player.{stat} {v}" }, " set Player.coins 0"), "control: a padded literal verb");
        }

        /// <summary>
        /// A template whose TARGET begins with a placeholder has a literal first word — and ticked
        /// once, `raw {a} {b}` approves any game cheat, `set {a} {b}` any field, `call {t}.Run` any
        /// type's Run. Reported after the third audit; the site's lint refuses the same shapes.
        /// </summary>
        [Test]
        public void ATemplateWhoseTargetIsAPlaceholderApprovesNothingAndCannotBeTicked()
        {
            var cases = new[]
            {
                ("raw {a} {b}", "raw DeleteSave now"),
                ("set {a} {b}", "set Player.coins 0"),
                ("call {t}.Run", "call Save.Run"),
                ("CALL {t}.Run", "CALL Save.Run"),
                ("click {name}", "click BuyAll"),
                ("invoke-button {name}", "invoke-button BuyAll"),
                ("hide-ui {name}", "hide-ui Paywall"),
            };
            foreach (var (template, command) in cases)
            {
                Assert.IsFalse(Levers.Allows(new[] { template }, command),
                    $"ticked once, '{template}' approves '{command}'");
                Assert.IsFalse(Levers.IsTicked(new[] { template }, command), $"IsTicked: '{template}'");
                Assert.IsNotNull(Levers.NotTickableReason(template), $"'{template}' was offered as tickable");
                Assert.IsNotNull(Levers.SetApproved(_root, template, true), $"'{template}' was ticked");
            }
            // CONTROLS: a target that BEGINS literal may end in a placeholder; a verb that names no
            // target keeps its templates; a literal command is itself
            Assert.IsTrue(Levers.Allows(new[] { "set Player.{stat} {v}" }, "set Player.coins 0"));
            Assert.IsTrue(Levers.Allows(new[] { "call Save.{m}" }, "call Save.Run"));
            Assert.IsTrue(Levers.Allows(new[] { "SelectHero {hero}" }, "SelectHero Knight"));
            Assert.IsTrue(Levers.Allows(new[] { "raw DeleteSave now" }, "raw DeleteSave now"));
            Assert.IsNull(Levers.NotTickableReason("set Player.{stat} {v}"));
        }

        // ---- U4: a synced.json that names no sha at all -------------------------------------------

        /// <summary>P5 (the auditor's probe, kept): <c>{"cloudShots":[7]}</c> parses, holds a list,
        /// names no sha — and it read as an empty record that turned the gate OFF.</summary>
        [Test]
        public void ASyncedJsonThatNamesNoShaAtAllStillGates()
        {
            Assert.IsNull(SyncNova.Run(_root, Sent(ShotsNamed("one"), AdapterWithNoOverlays), "run-1", "g").Refusal);
            Assert.IsTrue(Levers.GateActive(_root), "control");
            foreach (var says in new[]
                     {
                         "{\"cloudShots\": [7]}",
                         "{\"cloudShots\": [], \"cloudAdapters\": [null, 3, {}]}",
                         "{\"shotsSha256\": \"\", \"adapterSha256\": \"   \"}",
                     })
            {
                File.WriteAllText(SyncNova.SyncedFile(_root), says);
                Assert.IsTrue(Levers.GateActive(_root), $"a record naming no sha read as an empty one: {says}");
            }
            // CONTROL: a record that names a sha IS read — and it ungates when that sha is not on disk
            File.WriteAllText(SyncNova.SyncedFile(_root), "{\"cloudShots\": [\"" + new string('1', 64) + "\"]}");
            Assert.IsFalse(Levers.GateActive(_root), "control: a readable record of another delivery");
        }

        // ---- M5: the camera hold lives under the PROJECT, not the working directory ----------------

        private static string PlantHold(string projectRoot)
        {
            CameraPose.WriteSnapshot(projectRoot,
                new CameraPose.Snapshot(UnityEngine.Vector3.zero, new UnityEngine.Vector3(0, 90, 0), 40f, "Rig"));
            return CameraPose.SnapshotPath(projectRoot);
        }

        [Test]
        public void TheDirectorsLeftoverHoldSweepIsTheProjectsNotTheWorkingDirectorys()
        {
            var decoy = Decoy();
            var mine = PlantHold(_root);
            var theirs = PlantHold(decoy);
            WithWorkingDirectoryAt(decoy, () => RunDirector(CheatShot())); // release stubbed: only the sweep runs
            Assert.IsFalse(File.Exists(mine),
                "this project's stale camera-hold.json survived the sweep — the next pose reads it as home");
            Assert.IsTrue(File.Exists(theirs), "the sweep deleted the working directory's camera-hold.json");
        }

        [Test]
        public void TheDirectorsCameraReleaseIsTheProjectsNotTheWorkingDirectorys()
        {
            var decoy = Decoy();
            string mine = "", theirs = "";
            WithWorkingDirectoryAt(decoy, () =>
            {
                var d = AdDirector.Run(
                    new GameAdapter { GameId = "g", Ui = new FakeUi(), CheatBridge = new RecordingBridge() },
                    Array.Empty<AdShot>(),
                    new AdDirector.Options
                    {
                        AutoPump = false,
                        Now = () => 0,
                        Recorder = new FakeDriver(Path.Combine(_root, "takes")),
                        ProjectRoot = _root, // ReleaseCameraHold NOT set: the production release runs
                    });
                Assert.IsNotNull(d);
                // planted after the director started and never pumped: the sweep cannot be what deletes it
                mine = PlantHold(_root);
                theirs = PlantHold(decoy);
                d!.Finish("test");
            });
            Assert.IsFalse(File.Exists(mine), "the release left this project's camera-hold.json on disk");
            Assert.IsTrue(File.Exists(theirs), "the release deleted the working directory's camera-hold.json");
        }

        [Test]
        public void ThePlayModeSweepIsTheOpenProjectsNotTheWorkingDirectorys()
        {
            var decoy = Decoy();
            var theirs = PlantHold(decoy);
            var open = CameraPose.SnapshotPath(OpenProject);
            var relayDir = Path.GetDirectoryName(open)!;
            var hadRelayDir = Directory.Exists(relayDir);
            Assert.IsFalse(File.Exists(open), "precondition: the test host holds no camera-hold.json of its own");
            try
            {
                PlantHold(OpenProject);
                WithWorkingDirectoryAt(decoy, RelayBoot.SweepLeftoverCameraHold);
                Assert.IsFalse(File.Exists(open), "the Play-Mode sweep left the open project's camera-hold.json");
                Assert.IsTrue(File.Exists(theirs), "the Play-Mode sweep deleted the working directory's camera-hold.json");
            }
            finally
            {
                if (File.Exists(open)) File.Delete(open);
                if (!hadRelayDir && Directory.Exists(relayDir))
                    try { Directory.Delete(relayDir, recursive: true); } catch (IOException) { }
            }
        }

        // ==== THE FOURTH AUDIT (fresh context, 2026-09-21) =========================================

        // ---- S1: a template the files ask for, covered by a ticked template of the SAME shape --------

        /// <summary>
        /// Defect_C2 (the auditor's probe, kept): the studio ticked <c>SelectHero {x}</c> for an earlier
        /// send; a re-draft renamed the parameter, so the files now ask for <c>SelectHero {hero}</c>.
        /// The window showed the needed row UNTICKED and the stale entry as "not needed by the files on
        /// disk", while the gate ran every binding through the stale template.
        /// </summary>
        [Test]
        public void ANeededTemplateCoveredByATickedTemplateOfTheSameShapeIsShownTickedAndNamed()
        {
            Assert.IsNull(SyncNova.Run(_root, Sent(ShotsWithSetup("[\"SelectHero {hero}\"]", "[\"hero\"]"),
                AdapterWithNoOverlays), "run-1", "g").Refusal);
            WriteApprovedByHand("SelectHero {x}");
            Assert.IsTrue(Levers.GateActive(_root), "control: gated");
            var (gate, _, _) = Gate();
            Assert.IsFalse(gate.Run("set Coins 5"), "control: the gate is live and refuses an unticked write");
            Assert.IsTrue(gate.Run("SelectHero Knight"), "control: the ticked template runs every binding");

            var approved = Levers.Approved(_root);
            Assert.AreEqual("SelectHero {x}", Levers.CoveringApproval(approved, "SelectHero {hero}"));
            Assert.IsTrue(Levers.Allows(approved, "SelectHero {hero}"));
            Assert.IsTrue(Levers.IsTicked(approved, "SelectHero {hero}"),
                "slice E's IsTicked says a template is unticked while the gate runs every binding of it");

            var rows = Levers.Rows(_root, null);
            var needed = rows.Single(r => r.Command == "SelectHero {hero}");
            Assert.IsTrue(needed.Approved, "the window shows UNTICKED a template whose every binding the gate runs");
            Assert.AreEqual("SelectHero {x}", needed.CoveredBy);
            Assert.IsFalse(needed.CanTick, "this row is not in levers.json: unticking it could revoke nothing");
            Assert.AreEqual(Levers.CoveredByTemplateNote("SelectHero {x}"), needed.Note);

            var stale = rows.Single(r => r.Command == "SelectHero {x}");
            Assert.IsTrue(stale.Approved);
            Assert.IsTrue(stale.CanTick, "the entry in the file is where it is revoked");
            Assert.AreEqual(Levers.CoversNeededNote(new[] { "SelectHero {hero}" }), stale.Note);
            StringAssert.Contains("approves `SelectHero {hero}`, which the files ask for", stale.Note);
            StringAssert.DoesNotContain("not needed", stale.Note);

            // …and unticking the stale entry takes both boxes and the gate down together
            Assert.IsNull(Levers.SetApproved(_root, "SelectHero {x}", false));
            Assert.IsFalse(Levers.Rows(_root, null).Single(r => r.Command == "SelectHero {hero}").Approved);
            Assert.IsFalse(gate.Run("SelectHero Knight"));
        }

        [Test]
        public void ATemplateCoversATemplateOnlyWhenTheShapesAreEqualAndALiteralNeverDoes()
        {
            // the same shape, placeholder names aside
            Assert.AreEqual("call Shop.{a} {b}", Levers.CoveringApproval(new[] { "call Shop.{a} {b}" }, "call Shop.{m} {n}"));
            Assert.AreEqual("SelectHero {x}", Levers.CoveringApproval(new[] { "SelectHero {x}", "SelectHero {y}" }, "SelectHero {hero}"),
                "the FIRST same-shaped template in file order");
            Assert.AreEqual("SelectHero {hero}", Levers.CoveringApproval(new[] { "SelectHero {x}", "SelectHero {hero}" }, "SelectHero {hero}"),
                "exact text first");
            // a different literal around the placeholders is a different shape
            Assert.IsNull(Levers.CoveringApproval(new[] { "SelectHero {x} now" }, "SelectHero {hero}"));
            Assert.IsNull(Levers.CoveringApproval(new[] { "selecthero {x}" }, "SelectHero {hero}"));
            Assert.IsNull(Levers.CoveringApproval(new[] { "SelectHero {x}" }, "SelectHero {a} {b}"));
            // a LITERAL entry never covers a template — including one whose text holds braces that are
            // not a placeholder, or the token a naive "replace every placeholder" would have used
            foreach (var literal in new[] { "SelectHero Knight", "SelectHero {Hero}", "SelectHero {}", "SelectHero \0", "SelectHero {x" })
            {
                Assert.IsNull(Levers.CoveringApproval(new[] { literal }, "SelectHero {hero}"), $"the literal '{literal}' covered a template");
                Assert.IsFalse(Levers.IsTicked(new[] { literal }, "SelectHero {hero}"), $"IsTicked: '{literal}'");
            }
            // an entry that is not literal enough covers nothing, whatever its shape
            Assert.IsNull(Levers.CoveringApproval(new[] { "set {x} {y}" }, "set {a} {b}"));
            // template → literal is unchanged
            Assert.AreEqual("SelectHero {x}", Levers.CoveringApproval(new[] { "SelectHero {x}" }, "SelectHero Knight"));
        }

        /// <summary>
        /// A ticked template BROADER than the one the files ask for is not the same shape, so the
        /// narrower row stays unticked — and both rows say only what is true: the narrower one that a
        /// ticked entry already lets some, possibly all, of its commands through (naming it, with the
        /// witness, and saying it was found by example), the broader one the template note — never
        /// "not needed" (fifth audit, S1 + M2: ticked `set Player.{a} {b}` lets ALL of
        /// `set Player.coins {v}`'s commands through, so "ticking that row is what lets all of them
        /// through" was false).
        /// </summary>
        [Test]
        public void ABroaderTickedTemplateIsNamedOnBothRowsAndTheNarrowerRowStaysUnticked()
        {
            Assert.IsNull(SyncNova.Run(_root, Sent(ShotsWithSetup("[\"set Player.coins {v}\"]", "[\"v\"]"),
                AdapterWithNoOverlays), "run-1", "g").Refusal);
            WriteApprovedByHand("set Player.{a} {b}");
            var (gate, _, _) = Gate();
            Assert.IsTrue(gate.Run("set Player.coins 5"), "control: the broader entry lets a binding through");
            var approved = Levers.Approved(_root);
            Assert.IsFalse(Levers.IsTicked(approved, "set Player.coins {v}"), "not the same shape: not covered");
            Assert.IsFalse(Levers.Allows(approved, "set Player.coins {v}"));

            var rows = Levers.Rows(_root, null);
            var narrow = rows.Single(r => r.Command == "set Player.coins {v}");
            Assert.IsFalse(narrow.Approved);
            Assert.IsTrue(narrow.CanTick);
            Assert.AreEqual(Levers.PartlyCoveredNote("set Player.{a} {b}", "set Player.coins a"), narrow.Note);
            StringAssert.Contains("`set Player.{a} {b}`", narrow.Note);
            StringAssert.Contains("`set Player.coins a`", narrow.Note);
            StringAssert.Contains("some, possibly all, of its commands", narrow.Note);
            StringAssert.Contains("Found by trying examples, not proved: an overlap they miss is not named", narrow.Note);
            var broad = rows.Single(r => r.Command == "set Player.{a} {b}");
            Assert.IsTrue(broad.Approved);
            Assert.AreEqual(Levers.TemplateNote, broad.Note);
            // the sixth audit (M4): the note says the dot rule — a command can fit a ticked template and be refused
            StringAssert.Contains("a template — approves any command that fits it, except one whose value in a target " +
                                  "holds a '.'; not asked for in this exact form", broad.Note);
            StringAssert.DoesNotContain("not needed", broad.Note);

            // CONTROL: a ticked template that lets NOTHING of the needed one through names nothing on the
            // needed row — and is still never called "not needed": that is not knowable for a template
            WriteApprovedByHand("set Enemy.{a} {b}");
            var none = Levers.Rows(_root, null);
            Assert.AreEqual("", none.Single(r => r.Command == "set Player.coins {v}").Note);
            Assert.AreEqual(Levers.TemplateNote, none.Single(r => r.Command == "set Enemy.{a} {b}").Note);

            // "ticking this row approves every command of this form, whatever other entries let through":
            // tick the narrower row, take the broader one away — its commands still run
            WriteApprovedByHand("set Player.coins {v}");
            Assert.IsTrue(Levers.IsTicked(Levers.Approved(_root), "set Player.coins {v}"));
            Assert.IsTrue(gate.Run("set Player.coins 7"));
            Assert.IsTrue(gate.Run("set Player.coins 0.5"));
            // K5 (fifth audit): a row that IS ticked gets no partly-covered note, whatever else is ticked
            WriteApprovedByHand("set Player.coins {v}", "set Player.{a} {b}");
            var both = Levers.Rows(_root, null);
            var own = both.Single(r => r.Command == "set Player.coins {v}");
            Assert.IsTrue(own.Approved);
            Assert.AreEqual("", own.Note, "a ticked row was told another entry partly covers it");
            Assert.AreEqual(Levers.TemplateNote, both.Single(r => r.Command == "set Player.{a} {b}").Note);
        }

        /// <summary>
        /// The other way a ticked entry PARTLY covers a needed template: a ticked LITERAL that is one of
        /// its bindings. A literal never covers a template, so the template's box stays empty — while
        /// the gate runs that one binding. The rows say so, by name, as for a broader template.
        /// </summary>
        [Test]
        public void ATickedBindingOfANeededTemplateIsNamedOnBothRows()
        {
            Assert.IsNull(SyncNova.Run(_root, Sent(ShotsWithSetup("[\"SelectHero {hero}\"]", "[\"hero\"]"),
                AdapterWithNoOverlays), "run-1", "g").Refusal);
            WriteApprovedByHand("SelectHero Knight");
            var (gate, _, _) = Gate();
            Assert.IsTrue(gate.Run("SelectHero Knight"), "control: the literal tick runs that one binding");
            Assert.IsFalse(gate.Run("SelectHero Mage"), "control: and no other");
            Assert.IsFalse(Levers.IsTicked(Levers.Approved(_root), "SelectHero {hero}"), "a literal never covers a template");

            var rows = Levers.Rows(_root, null);
            var template = rows.Single(r => r.Command == "SelectHero {hero}");
            Assert.IsFalse(template.Approved);
            Assert.AreEqual(Levers.PartlyCoveredNote("SelectHero Knight", "SelectHero Knight"), template.Note,
                "the template's box is empty while the gate runs one of its commands, and the row said nothing");
            Assert.AreEqual(Levers.BroaderTemplateNote, rows.Single(r => r.Command == "SelectHero Knight").Note);

            // CONTROL (fifth audit): a ticked LITERAL no template on disk can be bound to IS "not needed" —
            // for a literal that is exact — and the template's row names only the entry that overlaps it
            WriteApprovedByHand("set Enemy.hp 0", "SelectHero Knight");
            var two = Levers.Rows(_root, null);
            Assert.AreEqual(Levers.NotNeededNote, two.Single(r => r.Command == "set Enemy.hp 0").Note);
            Assert.AreEqual(Levers.BroaderTemplateNote, two.Single(r => r.Command == "SelectHero Knight").Note);
            Assert.AreEqual(Levers.PartlyCoveredNote("SelectHero Knight", "SelectHero Knight"),
                two.Single(r => r.Command == "SelectHero {hero}").Note);
        }

        // ---- M1: a read-only command is approved, needs no tick, and says so ----------------------

        /// <summary>Defect_C1 (the fourth audit's probe, kept) and the fourteenth audit's ruling 2: a delivered shot's
        /// `get Player.coins` used to be shown as a read — ticked, "needs no tick" — because the gate ran it with none. The
        /// gate now refuses it un-ticked, so its row is an ordinary lever row: empty, tickable, no note; ticking it approves
        /// it. The kit's own fixed verbs keep the read row.</summary>
        [Test]
        public void AGetIsAnOrdinaryLeverRow_TheKitsFixedVerbsKeepTheReadRow()
        {
            Assert.IsNull(SyncNova.Run(_root, Sent(ShotsWithSetup("[\"get Player.coins\", \"ui-dump\", \"set Coins 5\"]"),
                AdapterWithNoOverlays), "run-1", "g").Refusal);
            CollectionAssert.Contains(Levers.Needed(_root).ToArray(), "get Player.coins", "control: the window lists it");
            var (gate, _, _) = Gate();
            Assert.IsFalse(gate.Run("get Player.coins"), "an un-ticked `get` is refused");
            Assert.IsFalse(Levers.IsTicked(Levers.Approved(_root), "get Player.coins"), "IsTicked agrees with the gate");

            var row = Levers.Rows(_root, null).Single(r => r.Command == "get Player.coins");
            Assert.IsFalse(row.Approved, "the box is empty, as the gate refuses it");
            Assert.IsTrue(row.CanTick, "and it can be ticked");
            Assert.AreEqual("", row.Note);
            var read = Levers.Rows(_root, null).Single(r => r.Command == "ui-dump");
            Assert.IsTrue(read.Approved);
            Assert.IsFalse(read.CanTick, "a fixed verb needs no tick, so there is none to give");
            Assert.AreEqual(Levers.ReadNeedsNoTickNote, read.Note);

            Assert.IsNull(Levers.SetApproved(_root, "get Player.coins", true));
            var ticked = Levers.Rows(_root, null).Single(r => r.Command == "get Player.coins");
            Assert.IsTrue(ticked.Approved);
            Assert.IsTrue(ticked.CanTick, "and it can be taken back");
            Assert.IsTrue(gate.Run("get Player.coins"), "ticked, the gate runs it");

            // a fixed verb already in levers.json by hand: still the read row, and it can be taken out
            WriteApprovedByHand("get Player.coins", "ui-dump");
            var inFile = Levers.Rows(_root, null).Single(r => r.Command == "ui-dump");
            Assert.IsTrue(inFile.Approved);
            Assert.IsTrue(inFile.CanTick, "whatever is in the file can always be taken back");
            Assert.AreEqual(Levers.ReadNeedsNoTickNote, inFile.Note);
        }

        // ---- M2: camera-pose's own Type ----------------------------------------------------------------

        [Test]
        public void ACameraPoseTypeSlotMustBeLiteral()
        {
            foreach (var template in new[]
                     {
                         "camera-pose {t} 1 2 3 4 5 6 60", "camera-pose Board{t} 1 2 3 4 5 6 60",
                         "camera-pose \"{t}\" 1 2 3 4 5 6 60", "CAMERA-POSE {t} 1 2 3 4 5 6 60",
                         // fifth audit, M1: a quote ANYWHERE — the bridge splits at it, so the Type slot moves
                         "camera-pose {t} 1 2 3 4 5 \"6\"60", "camera-pose {t} 1 2 3 4 5 6 \"60 \"",
                         "camera-pose {t} \"1\"2 3 4 5 6 60",
                     })
            {
                Assert.IsNotNull(Levers.NotTickableReason(template), $"'{template}' was offered as tickable");
                Assert.IsTrue(Levers.NotLiteralEnough(template), $"'{template}'");
                Assert.IsNotNull(Levers.SetApproved(_root, template, true), $"'{template}' was ticked");
            }
            StringAssert.Contains("Type", Levers.NotTickableReason("camera-pose {t} 1 2 3 4 5 6 60")!);
            Assert.IsFalse(Levers.Allows(new[] { "camera-pose {t} 1 2 3 4 5 6 60" }, "camera-pose SaveManager 1 2 3 4 5 6 60"),
                "a hand-edited Type template approved posing any Component");
            // the auditor's end-to-end shape: nine words, eight ARGUMENTS once the bridge splits the quote
            Assert.AreEqual(9, ReflectionCheatBridge.SplitCommand("camera-pose SaveManager 1 2 3 4 5 \"6\"60").Length,
                "control: the bridge reads the quoted span as its own argument");
            Assert.IsFalse(Levers.Allows(new[] { "camera-pose {t} 1 2 3 4 5 \"6\"60" }, "camera-pose SaveManager 1 2 3 4 5 \"6\"60"),
                "a quote in a later argument moved the Type slot to a placeholder, and the entry approved it");
            // CONTROLS: seven arguments have no Type slot; a literal Type keeps coordinate placeholders
            Assert.IsNull(Levers.NotTickableReason("camera-pose {x} {y} {z} 0 0 0 60"));
            Assert.IsTrue(Levers.Allows(new[] { "camera-pose {x} {y} {z} 0 0 0 60" }, "camera-pose 1 2 3 0 0 0 60"));
            Assert.IsNull(Levers.NotTickableReason("camera-pose BoardRig {x} {y} {z} 0 0 0 60"));
            Assert.IsTrue(Levers.Allows(new[] { "camera-pose BoardRig {x} {y} {z} 0 0 0 60" }, "camera-pose BoardRig 1 2 3 0 0 0 60"));
        }

        // ---- M3: adjacent placeholders ---------------------------------------------------------------

        [Test]
        public void AdjacentPlaceholdersCannotBeTickedAndApproveNothing()
        {
            foreach (var (template, binding) in new[]
                     {
                         ("SelectHero {a}{b}", "SelectHero KnightBlue"),
                         ("call Shop.Buy {a}{b}", "call Shop.Buy 12"),
                         ("SelectHero {p0}{p1}{p2}Z", "SelectHero abcZ"),
                         // fifth audit, M3: joined only by what a value may hold is as ambiguous
                         ("SelectHero {a}_{b}", "SelectHero Knight_Blue"),
                         ("SelectHero {x}-{y}", "SelectHero Knight-1"),
                         ("SelectHero {a}.{b}", "SelectHero Knight.Blue"),
                         ("set Player.{a}.{b} {c}", "set Player.save.coins 0"),
                         ("R {a}.{b}.{c}.{d}.{e}-{f}", "R a.a.a.a.a-a"),
                     })
            {
                var why = Levers.NotTickableReason(template);
                Assert.IsNotNull(why, $"'{template}' was offered as tickable");
                StringAssert.Contains("nothing between them that a value cannot hold", why!);
                StringAssert.Contains("cannot be read back", why!);
                Assert.IsTrue(Levers.NotLiteralEnough(template));
                Assert.IsNotNull(Levers.SetApproved(_root, template, true), $"'{template}' was ticked");
                Assert.IsFalse(Levers.Allows(new[] { template }, binding), $"'{template}' approved '{binding}'");
                Assert.IsFalse(Levers.IsTicked(new[] { template }, template));
            }
            // CONTROLS: a space, or another character no value can hold, separates two placeholders
            foreach (var (template, binding) in new[]
                     {
                         ("SelectHero {a}:{b}", "SelectHero Knight:Blue"),
                         ("set Player.{s} {v}", "set Player.coins 5"),
                         ("call Shop.{m} {n}", "call Shop.Buy 3"),
                         ("hide-ui Settings {name}", "hide-ui Settings Close"),
                     })
            {
                Assert.IsNull(Levers.NotTickableReason(template), $"control: '{template}'");
                Assert.IsTrue(Levers.Allows(new[] { template }, binding), $"control: '{template}' approves '{binding}'");
            }
        }

        // ---- M5: a target placeholder is a whole segment, and its value holds no dot ------------------

        [Test]
        public void ATargetPlaceholderMustBeAWholeSegmentAndItsValueHoldsNoDot()
        {
            foreach (var (template, command) in new[]
                     {
                         ("call S{a}", "call SaveSystem.DeleteAll"),
                         ("set P{a} {b}", "set PlayerData.coins 0"),
                         ("set Player.x{s} 1", "set Player.xcoins 1"),
                         ("set Player.{s}x 1", "set Player.coinsx 1"),
                     })
            {
                Assert.IsNotNull(Levers.NotTickableReason(template), $"'{template}' was offered as tickable");
                Assert.IsTrue(Levers.NotLiteralEnough(template), $"'{template}'");
                Assert.IsNotNull(Levers.SetApproved(_root, template, true), $"'{template}' was ticked");
                Assert.IsFalse(Levers.Allows(new[] { template }, command), $"ticked once, '{template}' approves '{command}'");
            }
            StringAssert.Contains("whole name between dots", Levers.NotTickableReason("call S{a}")!);

            var approved = new[] { "set Player.{s} {v}" };
            Assert.IsFalse(Levers.Allows(approved, "set Player.Instance.save.coins 0"),
                "a target placeholder bound to a dotted value walked to any member reachable from Player");
            Assert.IsFalse(Levers.IsTicked(approved, "set Player.Instance.save.coins 0"));
            Assert.IsTrue(Levers.Allows(approved, "set Player.coins 0"));
            Assert.IsTrue(Levers.IsTicked(approved, "set Player.coins 0"));
            Assert.IsTrue(Levers.Allows(approved, "set Player.coins 1.5"), "a dot in a VALUE word is not a target");
            Assert.IsTrue(Levers.Allows(new[] { "call Shop.{m}.Run" }, "call Shop.Buy.Run"));
            Assert.IsFalse(Levers.Allows(new[] { "call Shop.{m}.Run" }, "call Shop.A.B.Run"));
            // K4 (fifth audit): the dot check reads the verb in any case, as the target rule does
            Assert.IsNull(Levers.NotTickableReason("SET Player.{s} {v}"), "control: tickable");
            Assert.IsFalse(Levers.Allows(new[] { "SET Player.{s} {v}" }, "SET Player.Instance.save.coins 0"),
                "a verb in capitals skipped the dot check");
            Assert.IsTrue(Levers.Allows(new[] { "SET Player.{s} {v}" }, "SET Player.coins 0"));

            // …and the window agrees: a ticked `set Player.{s} {v}` does not tick the dotted command
            Assert.IsNull(SyncNova.Run(_root, Sent(ShotsWithSetup("[\"set Player.Instance.save.coins 0\", \"set Player.coins 0\"]"),
                AdapterWithNoOverlays), "run-1", "g").Refusal);
            WriteApprovedByHand("set Player.{s} {v}");
            var rows = Levers.Rows(_root, null);
            Assert.IsFalse(rows.Single(r => r.Command == "set Player.Instance.save.coins 0").Approved);
            Assert.IsTrue(rows.Single(r => r.Command == "set Player.coins 0").Approved);
            var (gate, _, _) = Gate();
            Assert.IsFalse(gate.Run("set Player.Instance.save.coins 0"));
            Assert.IsTrue(gate.Run("set Player.coins 0"));
        }

        // ---- surviving mutants K1 / K2: a quoted target and a padded one ------------------------------

        [Test]
        public void AQuotedTargetAndAPaddedTargetPlaceholderAreRefused()
        {
            foreach (var (template, command) in new[]
                     {
                         ("set \"{a}\" 1", "set \"Player.coins\" 1"),
                         ("set \"Player.coins\" {v}", "set \"Player.coins\" 5"),
                         (" set {a} {b}", " set Player.coins 0"),
                     })
            {
                Assert.IsNotNull(Levers.NotTickableReason(template), $"'{template}' was offered as tickable");
                Assert.IsNotNull(Levers.SetApproved(_root, template, true), $"'{template}' was ticked");
                Assert.IsFalse(Levers.Allows(new[] { template }, command), $"ticked once, '{template}' approves '{command}'");
            }
            // CONTROL: the bridge really does read a quoted target as its contents
            Assert.AreEqual("Player.coins", ReflectionCheatBridge.SplitCommand("set \"Player.coins\" 1")[1]);
        }

        // ==== THE FIFTH AUDIT (fresh context, 2026-09-21) ==========================================

        // ---- S1: the partly-covered note looks both ways, and a template is never "not needed" --------

        /// <summary>
        /// The auditor's P1 (kept): the studio ticked a NARROWER or overlapping template for an earlier
        /// send; a re-draft generalised the files to <c>SelectHero {hero}</c>. The gate runs a binding of
        /// it through the old tick, and the needed row said nothing while the ticked row said "not needed
        /// by the files on disk". The witness <c>SelectHero a</c> (needed bound to a) misses both; the
        /// ENTRY bound to a (<c>SelectHero Ka</c>, <c>SelectHero a-1</c>) finds them.
        /// </summary>
        [Test]
        public void ANarrowerTickedTemplateIsNamedOnTheNeededRowAndIsNeverCalledNotNeeded()
        {
            Assert.IsNull(SyncNova.Run(_root, Sent(ShotsWithSetup("[\"SelectHero {hero}\"]", "[\"hero\"]"),
                AdapterWithNoOverlays), "run-1", "g").Refusal);
            var (gate, _, _) = Gate();
            foreach (var (ticked, runs, witness) in new[]
                     {
                         ("SelectHero K{x}", "SelectHero Knight", "SelectHero Ka"),
                         // the auditor's `SelectHero {x}-{y}` is refused since the fifth audit's M3 (below);
                         // `{x}-1` is the same overlap through the entry's own witness, and tickable
                         ("SelectHero {x}-1", "SelectHero Knight-1", "SelectHero a-1"),
                     })
            {
                WriteApprovedByHand(ticked);
                Assert.IsTrue(gate.Run(runs), $"control: ticked '{ticked}' runs '{runs}'");
                Assert.IsTrue(Levers.Allows(new[] { "SelectHero {hero}" }, runs), "control: the needed tick would approve it too");
                var rows = Levers.Rows(_root, null);
                var needed = rows.Single(r => r.Command == "SelectHero {hero}");
                Assert.IsFalse(needed.Approved);
                Assert.AreEqual(Levers.PartlyCoveredNote(ticked, witness), needed.Note,
                    $"'{ticked}' lets '{runs}' through and the needed row said nothing");
                var entry = rows.Single(r => r.Command == ticked);
                Assert.AreEqual(Levers.TemplateNote, entry.Note);
                StringAssert.DoesNotContain("not needed", entry.Note);
            }

            // `SelectHero {x}-{y}` cannot be ticked any more, the gate ignores it by hand, and so no row
            // claims it lets anything through — a note there would be false
            WriteApprovedByHand("SelectHero {x}-{y}");
            Assert.IsFalse(gate.Run("SelectHero Knight-1"), "the gate ignores an entry it would not let you tick");
            var after = Levers.Rows(_root, null);
            Assert.AreEqual("", after.Single(r => r.Command == "SelectHero {hero}").Note);
            var dead = after.Single(r => r.Command == "SelectHero {x}-{y}");
            Assert.AreEqual(Levers.NotTickableReason("SelectHero {x}-{y}"), dead.Note);
            StringAssert.DoesNotContain("not needed", dead.Note);
        }

        /// <summary>The auditor's P7a (kept): a needed row that already carries a refused-as note never got
        /// the partly-covered note. Now it is appended.</summary>
        [Test]
        public void APartlyCoveredNoteIsAppendedToARefusedAsNoteNotSkipped()
        {
            Assert.IsNull(SyncNova.Run(_root, Sent(ShotsWithSetup("[\"SelectHero {a} {b}\"]", "[\"a\",\"b\"]"),
                AdapterWithNoOverlays), "run-1", "g").Refusal);
            WriteApprovedByHand("SelectHero {x} a");
            var (gate, _, _) = Gate();
            Assert.IsFalse(gate.Run("SelectHero Knight Mage"), "control: refused");
            Assert.IsTrue(gate.Run("SelectHero Knight a"), "control: the ticked entry lets this binding through");

            var rows = Levers.Rows(_root, LeverGateBridge.RefusedThisSession);
            var needed = rows.Single(r => r.Command == "SelectHero {a} {b}");
            Assert.IsFalse(needed.Approved);
            Assert.AreEqual(Levers.RefusedAsNote(new[] { "SelectHero Knight Mage" }) + "; " +
                            Levers.PartlyCoveredNote("SelectHero {x} a", "SelectHero a a"), needed.Note);
            Assert.AreEqual(Levers.TemplateNote, rows.Single(r => r.Command == "SelectHero {x} a").Note);
        }

        /// <summary>The auditor's P7b (kept): a second tick of the same shape as the covering one was
        /// labelled "not asked for in this form by the files" — but it is the same form. It gets the one
        /// template note, which says nothing about the form beyond its exact text.</summary>
        [Test]
        public void ASecondTickOfTheSameShapeGetsTheTemplateNote()
        {
            Assert.IsNull(SyncNova.Run(_root, Sent(ShotsWithSetup("[\"SelectHero {hero}\", \"SelectHero Knight\"]", "[\"hero\"]"),
                AdapterWithNoOverlays), "run-1", "g").Refusal);
            WriteApprovedByHand("SelectHero {x}", "SelectHero {y}");
            var rows = Levers.Rows(_root, null);
            Assert.AreEqual(Levers.CoversNeededNote(new[] { "SelectHero Knight", "SelectHero {hero}" }),
                rows.Single(r => r.Command == "SelectHero {x}").Note, "control: the first same-shaped tick covers both");
            var second = rows.Single(r => r.Command == "SelectHero {y}");
            Assert.AreEqual(Levers.TemplateNote, second.Note);
            StringAssert.DoesNotContain("not asked for in this form by the files", second.Note);
            StringAssert.DoesNotContain("not needed", second.Note);
        }

        /// <summary>
        /// K19 (fifth audit): the note names an entry only when the GATE's own template pass lets the
        /// witness through by that entry. A raw match would name a hand-edited entry the gate ignores.
        /// </summary>
        [Test]
        public void ThePartlyCoveredNoteNeverNamesAnEntryTheGateIgnores()
        {
            Assert.IsNull(SyncNova.Run(_root, Sent(ShotsWithSetup("[\"set Player.coins {v}\"]", "[\"v\"]"),
                AdapterWithNoOverlays), "run-1", "g").Refusal);
            WriteApprovedByHand("set {a} {b}");
            var (gate, _, _) = Gate();
            Assert.IsFalse(gate.Run("set Player.coins 5"), "control: the gate ignores `set {a} {b}`");
            Assert.IsTrue(ShotBinding.MatchesTemplate("set {a} {b}", "set Player.coins a"), "control: a raw match says yes");
            var rows = Levers.Rows(_root, null);
            Assert.AreEqual("", rows.Single(r => r.Command == "set Player.coins {v}").Note);
            Assert.AreEqual(Levers.NotTickableReason("set {a} {b}"), rows.Single(r => r.Command == "set {a} {b}").Note);
        }

        /// <summary>The needed side, a TEMPLATE entry (fifth audit): the witness must be one a tick of the
        /// needed row would approve too. What <c>set Player.{a}.coins {b}</c> lets through, a tick of
        /// <c>set Player.{s} {v}</c> refuses (a dotted value in a target) — gate-level disjoint, no note.</summary>
        [Test]
        public void ThePartlyCoveredNoteNeedsAWitnessTheNeededTickWouldApproveToo()
        {
            Assert.IsNull(SyncNova.Run(_root, Sent(ShotsWithSetup("[\"set Player.{s} {v}\"]", "[\"s\",\"v\"]"),
                AdapterWithNoOverlays), "run-1", "g").Refusal);
            WriteApprovedByHand("set Player.{a}.coins {b}");
            var (gate, _, _) = Gate();
            Assert.IsTrue(gate.Run("set Player.x.coins 5"), "control: the entry lets it through");
            Assert.IsFalse(Levers.Allows(new[] { "set Player.{s} {v}" }, "set Player.x.coins 5"), "control: the needed tick would not");
            var rows = Levers.Rows(_root, null);
            Assert.AreEqual("", rows.Single(r => r.Command == "set Player.{s} {v}").Note);
            Assert.AreEqual(Levers.TemplateNote, rows.Single(r => r.Command == "set Player.{a}.coins {b}").Note);
        }

        /// <summary>The needed side, a LITERAL entry (fifth audit): the files CAN produce
        /// <c>set Player.Instance.coins 5</c> (s = Instance.coins), so its row is not "not needed" — the
        /// binder's own grammar decides that — but a tick of the needed row would refuse it, so the
        /// needed row names nothing.</summary>
        [Test]
        public void ATickedLiteralTheFilesCanProduceIsNotNotNeededEvenWhenTheNeededTickWouldRefuseIt()
        {
            Assert.IsNull(SyncNova.Run(_root, Sent(ShotsWithSetup("[\"set Player.{s} {v}\"]", "[\"s\",\"v\"]"),
                AdapterWithNoOverlays), "run-1", "g").Refusal);
            WriteApprovedByHand("set Player.Instance.coins 5");
            var (gate, _, _) = Gate();
            Assert.IsTrue(gate.Run("set Player.Instance.coins 5"), "control: the literal runs itself");
            Assert.IsTrue(ShotBinding.MatchesTemplate("set Player.{s} {v}", "set Player.Instance.coins 5"), "control: a binding of it");
            var rows = Levers.Rows(_root, null);
            Assert.AreEqual("", rows.Single(r => r.Command == "set Player.{s} {v}").Note);
            Assert.AreEqual(Levers.BroaderTemplateNote, rows.Single(r => r.Command == "set Player.Instance.coins 5").Note);
        }

        /// <summary>
        /// THE LIMIT, pinned so the sentence about it stays true (fifth audit): the note is found by two
        /// witnesses, not proved. Files ask for <c>SelectHero {h}x</c>; ticked <c>SelectHero y{k}</c>.
        /// Both let <c>SelectHero yx</c> through, but neither witness (<c>SelectHero ax</c>,
        /// <c>SelectHero ya</c>) fits both — so this overlap is NOT named, as TRUST.md says.
        /// </summary>
        [Test]
        public void AnOverlapNeitherWitnessHitsIsNotNamed()
        {
            Assert.IsNull(SyncNova.Run(_root, Sent(ShotsWithSetup("[\"SelectHero {h}x\"]", "[\"h\"]"),
                AdapterWithNoOverlays), "run-1", "g").Refusal);
            WriteApprovedByHand("SelectHero y{k}");
            var (gate, _, _) = Gate();
            Assert.IsTrue(gate.Run("SelectHero yx"), "control: the ticked entry lets it through");
            Assert.IsTrue(Levers.Allows(new[] { "SelectHero {h}x" }, "SelectHero yx"), "control: the needed tick would too");
            var rows = Levers.Rows(_root, null);
            Assert.AreEqual("", rows.Single(r => r.Command == "SelectHero {h}x").Note);
            Assert.AreEqual(Levers.TemplateNote, rows.Single(r => r.Command == "SelectHero y{k}").Note);
        }

        // ---- M4: a repeated placeholder name is one value ------------------------------------------------

        [Test]
        public void ARepeatedPlaceholderNameIsOneValueAtTheGateAndInTheShape()
        {
            var approved = new[] { "set Player.{a} {a}" };
            Assert.IsFalse(Levers.Allows(approved, "set Player.coins 999"), "a tick of `{a} {a}` approved what no binding of it can be");
            Assert.IsTrue(Levers.Allows(approved, "set Player.coins coins"));
            Assert.IsNull(Levers.CoveringApproval(approved, "set Player.{s} {v}"), "not the same shape: {a} {a} is one value");
            Assert.IsFalse(Levers.IsTicked(approved, "set Player.{s} {v}"));
            Assert.AreEqual("set Player.{x} {x}", Levers.CoveringApproval(new[] { "set Player.{x} {x}" }, "set Player.{a} {a}"),
                "control: the same repeat, renamed, is the same shape");
        }

        // ---- the sixth audit (2026-09-22) -------------------------------------------------------------

        /// <summary>
        /// S1 (the auditor's Q3, kept): the binder's <c>^…$</c> let a value end in "\n", so a relay run-shot bound
        /// <c>SelectHero Knight\n</c>; the gate's <c>\z</c> refused it even with the template ticked, and the window
        /// listed that binding of the files' own template as "no file on disk asks for it now". The binder and the
        /// gate now read one grammar (<c>\A…\z</c>): every command the binder writes is a binding the window folds
        /// into the template's row, and a tick of the template runs.
        /// </summary>
        [Test]
        public void EveryCommandTheBinderWritesIsABindingTheWindowFoldsIntoItsTemplatesRow()
        {
            Assert.IsNull(SyncNova.Run(_root, Sent(ShotsWithSetup("[\"SelectHero {hero}\"]", "[\"hero\"]"),
                AdapterWithNoOverlays), "run-1", "g").Refusal);
            var (gate, inner, _) = Gate();
            var written = new List<string>();
            foreach (var value in new[] { "Knight", "Knight\n", "Mage", "Mage\n" })
                if (ShotBinding.TryApply("SelectHero {hero}", new[] { "hero" },
                        new Dictionary<string, string> { ["hero"] = value }, out var bound, out _))
                    written.Add(bound);
            foreach (var command in written) Assert.IsFalse(gate.Run(command), "control: nothing is ticked yet");

            var rows = Levers.Rows(_root, LeverGateBridge.RefusedThisSession);
            CollectionAssert.IsEmpty(rows.Where(r => r.Source == Levers.LeverSource.RefusedHere).Select(r => r.Command),
                "a command the binder wrote from the files' own template was listed as one no file on disk asks for");
            CollectionAssert.AreEqual(new[] { "SelectHero Knight", "SelectHero Mage" }, written,
                "the binder wrote a value ending in a newline");
            Assert.AreEqual(Levers.RefusedAsNote(written), rows.Single(r => r.Command == "SelectHero {hero}").Note);

            // …and a tick of the template runs every command the binder writes
            Assert.IsNull(Levers.SetApproved(_root, "SelectHero {hero}", true));
            foreach (var command in written) Assert.IsTrue(gate.Run(command));
            CollectionAssert.AreEqual(written, inner.Run_);
        }

        /// <summary>
        /// M4: the template note said "approves any command that fits it", and a command can fit a ticked template
        /// and be refused — a value in a target may hold no '.'. Both notes say what the gate does, word for word.
        /// </summary>
        [Test]
        public void TheTemplateNoteAndTheLiteralNoteSayWhatTheGateApproves()
        {
            Assert.IsNull(SyncNova.Run(_root, Sent(ShotsWithSetup("[\"SelectHero {hero}\"]", "[\"hero\"]"),
                AdapterWithNoOverlays), "run-1", "g").Refusal);
            WriteApprovedByHand("set Player.{s} {v}", "SelectHero Knight");
            var (gate, _, _) = Gate();
            var rows = Levers.Rows(_root, null);

            Assert.AreEqual(Levers.TemplateNote, rows.Single(r => r.Command == "set Player.{s} {v}").Note);
            Assert.AreEqual("a template — approves any command that fits it, except one whose value in a target " +
                            "holds a '.'; not asked for in this exact form", Levers.TemplateNote);
            Assert.IsTrue(gate.Run("set Player.coins 5"), "it fits: approved");
            Assert.IsTrue(ShotBinding.MatchesTemplate("set Player.{s} {v}", "set Player.Instance.save.coins 0"), "control: it FITS");
            Assert.IsFalse(gate.Run("set Player.Instance.save.coins 0"), "…and is refused: a value in its target holds a '.'");

            Assert.AreEqual(Levers.BroaderTemplateNote, rows.Single(r => r.Command == "SelectHero Knight").Note);
            Assert.AreEqual("not asked for in this form by the files; it approves only this command, as written",
                Levers.BroaderTemplateNote);
            Assert.IsTrue(gate.Run("SelectHero Knight"));
            Assert.IsFalse(gate.Run("SelectHero Mage"));
            Assert.IsFalse(gate.Run("SelectHero Knight2"));
        }

        /// <summary>
        /// M5 (the auditor's Q5, kept): a ticked <c>SelectHero {{h}}</c> ran h=Knight's binding and refused h=knight's
        /// (<c>SelectHero {knight}</c> reads as a template, which the gate compares by shape), while its row showed one
        /// tick and no note. In a command with a placeholder a brace outside one now makes it not literal enough.
        /// </summary>
        [Test]
        public void ABraceOutsideAPlaceholderCannotBeTickedAndTheGateIgnoresIt()
        {
            Assert.IsNull(SyncNova.Run(_root, Sent(ShotsWithSetup("[\"SelectHero {{h}}\"]", "[\"h\"]"),
                AdapterWithNoOverlays), "run-1", "g").Refusal);
            // h=knight would bind it to a command that reads as a placeholder itself; since the eighth audit (M1) the binder
            // refuses to (TheBinderWritesNoCommandFromATextWithABraceOutsideAPlaceholder)
            Assert.IsFalse(ShotBinding.TryApply("SelectHero {{h}}", new[] { "h" },
                new Dictionary<string, string> { ["h"] = "knight" }, out _, out _));
            Assert.IsTrue(ShotBinding.HasPlaceholder("SelectHero {knight}"), "a value bound inside braces reads as a placeholder itself");

            foreach (var command in new[] { "SelectHero {{h}}", "set Player.{a}} 1", "call Shop.{{m}}" })
            {
                var why = Levers.NotTickableReason(command);
                Assert.IsNotNull(why, command);
                StringAssert.Contains("that is not part of a {placeholder}", why, command);
                StringAssert.Contains("reads as a {placeholder} itself", why!);
                Assert.IsTrue(Levers.NotLiteralEnough(command), command);
                Assert.IsNotNull(Levers.SetApproved(_root, command, true), command);
            }

            // in the file by hand: ignored by the gate, shown ticked with the reason, and it can be taken back
            WriteApprovedByHand("SelectHero {{h}}");
            var (gate, _, _) = Gate();
            Assert.IsFalse(gate.Run("SelectHero {Knight}"));
            Assert.IsFalse(gate.Run("SelectHero {knight}"));
            var row = Levers.Rows(_root, null).Single(r => r.Command == "SelectHero {{h}}");
            Assert.IsTrue(row.Approved);
            Assert.IsTrue(row.CanTick);
            Assert.AreEqual(Levers.NotTickableReason("SelectHero {{h}}"), row.Note);

            // CONTROLS: every brace part of a placeholder; a LITERAL command may hold braces; and a lever read out
            // of a data field keeps its own brace sentence and its exact-text approval
            foreach (var fine in new[] { "SelectHero {h}", "set Player.{a} 1", "call Shop.{m}", "SelectHero {B}{C}" })
                Assert.IsNull(Levers.NotTickableReason(fine), fine);
            StringAssert.Contains("no C# type or method name holds", Levers.NotTickableReason("hide-overlay {{t}}"));
            Assert.IsFalse(Levers.NotLiteralEnough("hide-overlay {{t}}"));
        }

        /// <summary>
        /// Q1 (a surviving mutant: the two witnesses swapped). Their ORDER does not matter: when both pass both
        /// gates they are the same text, so the note names the same example whichever is tried first. Both bind
        /// every placeholder to the one-character <c>a</c>; a witness that fits the other template binds each of
        /// ITS placeholders to one character too (the lengths leave no room), and wherever the two templates
        /// differ, both witnesses hold <c>a</c>. Pinned over examples that include pairs where both pass.
        /// </summary>
        [Test]
        public void WhenBothWitnessesPassTheyAreOneTextSoTheirOrderDoesNotMatter()
        {
            var both = 0;
            foreach (var (entry, needed) in new[]
                     {
                         ("SelectHero {y}", "SelectHero {h}"),
                         ("SelectHero {x} {y}", "SelectHero {a} {a}"),
                         ("SelectHero {x} a", "SelectHero {a} {b}"),
                         ("SelectHero a{y}", "SelectHero {h}a"),
                         ("set Player.{a} {b}", "set Player.{s} {v}"),
                         ("SelectHero K{x}", "SelectHero {hero}"),
                         ("SelectHero a {k}", "SelectHero {h} x"),
                         ("SelectHero y{k}", "SelectHero {h}x"),
                     })
            {
                var witnesses = Levers.Witnesses(entry, needed);
                Assert.AreEqual(2, witnesses.Count);
                var passing = witnesses.Where(w => Levers.Allows(new[] { entry }, w) && Levers.Allows(new[] { needed }, w)).ToList();
                if (passing.Count == 2) both++;
                Assert.LessOrEqual(passing.Distinct().Count(), 1,
                    $"'{entry}' against '{needed}': two different witnesses passed ({string.Join(", ", passing)}) — " +
                    "which one is named would depend on the order they are tried in");
            }
            Assert.GreaterOrEqual(both, 4, "control: pairs where BOTH witnesses pass — the only case the order could matter in");
        }

        /// <summary>Q2 (a surviving mutant: the <c>break</c> dropped). The needed row names ONE entry — the first in
        /// levers.json's order that a witness shows letting its commands through (the auditor's Q6).</summary>
        [Test]
        public void ThePartlyCoveredNoteNamesOnlyTheFirstSuchEntryInFileOrder()
        {
            Assert.IsNull(SyncNova.Run(_root, Sent(ShotsWithSetup("[\"SelectHero {hero}\"]", "[\"hero\"]"),
                AdapterWithNoOverlays), "run-1", "g").Refusal);
            WriteApprovedByHand("SelectHero Knight", "SelectHero K{x}");
            Assert.AreEqual(Levers.PartlyCoveredNote("SelectHero Knight", "SelectHero Knight"),
                Levers.Rows(_root, null).Single(r => r.Command == "SelectHero {hero}").Note);
            WriteApprovedByHand("SelectHero K{x}", "SelectHero Knight");
            Assert.AreEqual(Levers.PartlyCoveredNote("SelectHero K{x}", "SelectHero Ka"),
                Levers.Rows(_root, null).Single(r => r.Command == "SelectHero {hero}").Note);
        }

        /// <summary>Q3 (a surviving mutant: <c>!NeverATemplate(needed)</c> dropped from <c>CanBeBoundTo</c>; the
        /// auditor's Q7). A lever read out of a data field is never a template, so it can be bound to nothing:
        /// a ticked <c>hide-overlay Foo</c> beside the files' <c>hide-overlay {t}</c> is not needed by them.</summary>
        [Test]
        public void ATickedLiteralBesideANeededDataFieldLeverIsNotNeeded()
        {
            // straight to disk: SyncNova refuses such a delivery since the ninth audit, and the window still reads one
            WriteShots(ShotsWithSetup("[\"SelectHero Mage\"]"));
            WriteAdapter("{ \"gameId\": \"g\", \"overlayTypeNames\": [\"{t}\"] }");
            CollectionAssert.Contains(Levers.Needed(_root), "hide-overlay {t}");
            WriteApprovedByHand("hide-overlay Foo");
            Assert.IsTrue(ShotBinding.MatchesTemplate("hide-overlay {t}", "hide-overlay Foo"), "control: the raw grammar says it fits");
            Assert.AreEqual(Levers.NotNeededNote, Levers.Rows(_root, null).Single(r => r.Command == "hide-overlay Foo").Note);
        }

        /// <summary>M2 (the auditor's Q4, kept): a needed row names an entry only when a tick of that row would approve
        /// the witness too — so a row the window will not let you tick names none, even while a ticked entry runs
        /// some of its commands. The docs say so; this pins it.</summary>
        [Test]
        public void ARowThatCannotBeTickedNamesNoEntryEvenWhileOneRunsItsCommands()
        {
            Assert.IsNull(SyncNova.Run(_root, Sent(ShotsWithSetup("[\"set Player.x{s} 5\"]", "[\"s\"]"),
                AdapterWithNoOverlays), "run-1", "g").Refusal);
            WriteApprovedByHand("set Player.{a} {b}");
            var (gate, _, _) = Gate();
            Assert.IsTrue(gate.Run("set Player.xcoins 5"), "control: the ticked entry runs one of its commands");
            var rows = Levers.Rows(_root, null);
            Assert.AreEqual(Levers.NotTickableReason("set Player.x{s} 5"), rows.Single(r => r.Command == "set Player.x{s} 5").Note);
            Assert.AreEqual(Levers.TemplateNote, rows.Single(r => r.Command == "set Player.{a} {b}").Note);
        }

        // ==== THE SEVENTH AUDIT (fresh context, 2026-09-22) ========================================

        /// <summary>S2 (the auditor's Q4, kept): one slow template in the files and 50 refused commands made
        /// <c>Levers.Rows</c> take about 5 s on the editor's main thread, because every refused command was matched
        /// against every needed template the window will not tick.</summary>
        [Test]
        public void RowsWithFiftyRefusedCommandsAndOneSlowTemplateTakeWellUnderASecond()
        {
            var names = Enumerable.Range(0, 11).Select(i => "p" + i).ToArray();
            var slow = "SelectHero " + string.Concat(names.Select(n => "{" + n + "}")) + "Z";
            Assert.IsNull(SyncNova.Run(_root, Sent(ShotsWithSetup("[\"" + slow + "\"]",
                "[" + string.Join(",", names.Select(n => "\"" + n + "\"")) + "]"), AdapterWithNoOverlays), "run-1", "g").Refusal);
            Assert.IsNotNull(Levers.NotTickableReason(slow), "control: the window will not tick it");
            var timeouts = new List<string>();
            ShotBinding.ResetTimeoutLogForTests(timeouts.Add);
            try
            {
                var (gate, _, _) = Gate();
                for (var i = 0; i < 50; i++) Assert.IsFalse(gate.Run("SelectHero " + new string('b', 28) + i.ToString("D2")));
                Assert.AreEqual(50, LeverGateBridge.RefusedThisSession.Count, "control: 50 refused commands");
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var rows = Levers.Rows(_root, LeverGateBridge.RefusedThisSession);
                watch.Stop();
                Assert.AreEqual(51, rows.Count, "control: the template's row and the 50 refused ones");
                Assert.Less(watch.ElapsedMilliseconds, 1000,
                    $"Levers.Rows took {watch.ElapsedMilliseconds} ms ({timeouts.Count} timeout line(s) logged)");
            }
            finally
            {
                ShotBinding.ResetTimeoutLogForTests(null);
            }
        }

        /// <summary>S1 + M1 (the auditor's Q6, kept): a SHOT's <c>hide-overlay {t}</c> or <c>camera-spec * {a} …</c>,
        /// with its parameter declared, was bound by the binder, while the gate reads a text whose verb is
        /// <c>hide-overlay</c> or <c>camera-spec</c> as never a template. The window then called the files' own binding
        /// "no file on disk asks for it now", and once ticked "not needed by the files on disk".</summary>
        [Test]
        public void TheBinderNeverWritesAHideOverlayOrCameraSpecCommandFromAPlaceholder()
        {
            foreach (var (template, name, value, verb) in new[]
                     {
                         ("hide-overlay {t}", "t", "Foo", "hide-overlay"),
                         ("camera-spec * {a} SetRotation SetFov", "a", "DeleteSave", "camera-spec"),
                     })
            {
                LeverGateBridge.ForgetRefused();
                Assert.IsNull(SyncNova.Run(_root, Sent(ShotsWithSetup("[\"" + template + "\"]", "[\"" + name + "\"]"),
                    AdapterWithNoOverlays), "run-" + name, "g").Refusal);
                CollectionAssert.Contains(Levers.Needed(_root), template);
                var bindings = new Dictionary<string, string> { [name] = value };
                Assert.IsFalse(ShotBinding.TryApply(template, new[] { name }, bindings, out var bound, out var error), template);
                Assert.AreEqual(template, bound, "nothing is written");
                Assert.AreEqual("'" + template + "' holds a placeholder, and a '" + verb +
                                "' command takes none: its words name a C# type or method", error);
                // CONTROL: the same binding into another verb is written
                Assert.IsTrue(ShotBinding.TryApply("SelectHero {" + name + "}", new[] { name }, bindings, out var other, out _));
                Assert.AreEqual("SelectHero " + value, other);
                // the literal a person might tick beside it: no file on disk can write it now, and its row says so
                var literal = ShotBinding.WithEveryPlaceholderAs(template, value);
                WriteApprovedByHand(literal);
                Assert.AreEqual(Levers.NotNeededNote, Levers.Rows(_root, null).Single(r => r.Command == literal).Note);
            }
            // CONTROL: such a verb with no placeholder is left as it is, in a shot that declares parameters
            Assert.IsTrue(ShotBinding.TryApply("hide-overlay Foo", new[] { "t" },
                new Dictionary<string, string> { ["t"] = "Foo" }, out var plain, out _));
            Assert.AreEqual("hide-overlay Foo", plain);
        }

        /// <summary>M2 (the auditor's Q3): h=knight of the files' <c>SelectHero {{h}}</c> was <c>SelectHero {knight}</c> (the
        /// binder refuses it since the eighth audit, M1; a compiled adapter can still send such a text), which the gate
        /// refuses and the window lists as a refused row. That command holds a placeholder, so a tick of it
        /// would approve a TEMPLATE nobody authored: every <c>SelectHero &lt;value&gt;</c>. Its box is disabled and its
        /// note is the ordinary one.</summary>
        [Test]
        public void ARefusedCommandThatHoldsAPlaceholderCannotBeTicked()
        {
            Assert.IsNull(SyncNova.Run(_root, Sent(ShotsWithSetup("[\"SelectHero {{h}}\"]", "[\"h\"]"),
                AdapterWithNoOverlays), "run-1", "g").Refusal);
            // Since the eighth audit (M1) the binder writes no such command; the text still reaches the gate from code,
            // as a compiled adapter's command
            Assert.IsFalse(ShotBinding.TryApply("SelectHero {{h}}", new[] { "h" },
                new Dictionary<string, string> { ["h"] = "knight" }, out _, out _));
            const string bound = "SelectHero {knight}";
            var (gate, inner, _) = Gate();
            Assert.IsFalse(gate.Run(bound));
            Assert.IsFalse(gate.Run("SelectHero Knight"), "control: a refused literal");
            var rows = Levers.Rows(_root, LeverGateBridge.RefusedThisSession);
            var row = rows.Single(r => r.Command == bound);
            Assert.AreEqual(Levers.LeverSource.RefusedHere, row.Source);
            Assert.IsFalse(row.CanTick, "a tick of 'SelectHero {knight}' would approve every SelectHero <value>");
            Assert.AreEqual(Levers.RefusedThisSessionNote, row.Note);
            var literal = rows.Single(r => r.Command == "SelectHero Knight");
            Assert.AreEqual(Levers.LeverSource.RefusedHere, literal.Source);
            Assert.IsTrue(literal.CanTick, "control: a refused literal with no placeholder can be ticked");
            // Tick every box the window lets a person tick, as its draw loop does (it calls SetApproved only when CanTick).
            foreach (var r in rows)
                if (r.CanTick && !r.Approved)
                    Assert.IsNull(Levers.SetApproved(_root, r.Command, true), r.Command);
            Assert.IsFalse(gate.Run("SelectHero Mage"), "no box the window offers approves 'SelectHero Mage'");
            Assert.IsTrue(gate.Run("SelectHero Knight"), "control: the ticked literal runs");
            CollectionAssert.AreEqual(new[] { "SelectHero Knight" }, inner.Run_);
        }

        /// <summary>The same stall's second path: a ticked LITERAL's note asks whether a template the files ask for can
        /// be bound to it (<c>CanBeBoundTo</c>), and it asked the slow templates too, once per ticked literal. A template
        /// whose match ran out of time once is read as no match for the rest of that <c>Rows</c> call: only that template,
        /// and only that call.</summary>
        [Test]
        public void RowsWithFiftyTickedLiteralsAndOneSlowTemplateTakeWellUnderASecond()
        {
            var names = Enumerable.Range(0, 11).Select(i => "p" + i).ToArray();
            var slow = "SelectHero " + string.Concat(names.Select(n => "{" + n + "}")) + "Z";
            Assert.IsNull(SyncNova.Run(_root, Sent(ShotsWithSetup("[\"" + slow + "\", \"set Coins {v}\"]",
                "[" + string.Join(",", names.Concat(new[] { "v" }).Select(n => "\"" + n + "\"")) + "]"),
                AdapterWithNoOverlays), "run-1", "g").Refusal);
            CollectionAssert.AreEqual(new[] { slow, "set Coins {v}" }, Levers.Needed(_root).ToArray(),
                "control: the slow template is asked first");
            var literals = Enumerable.Range(0, 50).Select(i => "SelectHero " + new string('b', 28) + i.ToString("D2")).ToList();
            WriteApprovedByHand(literals.Concat(new[] { "set Coins 5" }).ToArray());
            var timeouts = new List<string>();
            ShotBinding.ResetTimeoutLogForTests(timeouts.Add);
            try
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var rows = Levers.Rows(_root, null);
                watch.Stop();
                Assert.Less(watch.ElapsedMilliseconds, 1000,
                    $"Levers.Rows took {watch.ElapsedMilliseconds} ms ({timeouts.Count} timeout line(s) logged)");
                Assert.AreEqual(Levers.NotNeededNote, rows.Single(r => r.Command == literals[49]).Note);
                // CONTROL, per template: an ordinary template still matches after the slow one ran out of time.
                Assert.AreEqual(Levers.BroaderTemplateNote, rows.Single(r => r.Command == "set Coins 5").Note);
                // CONTROL, per call: the next call matches the slow template again (its own binding matches in no time).
                var binding = ShotBinding.WithEveryPlaceholderAs(slow, "ab");
                WriteApprovedByHand(binding);
                Assert.AreEqual(Levers.BroaderTemplateNote, Levers.Rows(_root, null).Single(r => r.Command == binding).Note);
            }
            finally
            {
                ShotBinding.ResetTimeoutLogForTests(null);
            }
        }

        // ==== THE EIGHTH AUDIT (fresh context, 2026-09-22) =========================================

        /// <summary>S1 (the auditor's Q8, kept): the approved-entries loop asked <c>CoveringApproval</c> of every needed
        /// lever once for EACH ticked entry no file asks for, and each of those calls reads every ticked entry. A re-draft
        /// leaves the old ticks in levers.json by design, so an ordinary project paid (stale ticks × needed × ticks) on the
        /// editor's main thread: 2.0 s, 3.5 s and 30.7 s for these three shapes in the auditor's run. Each needed lever's
        /// covering entry is now computed once per <c>Rows</c> call. Each bound sits far above the fixed code's time (so a
        /// busy machine does not fail it) and far below the old code's: 500 ms (old 988 ms), 1.5 s (old 3.1 s), 5 s (old
        /// 26.6 s).</summary>
        [TestCase(70, 30, 50, 50, 500)]    // 100 levers, 50 still ticked, 50 stale
        [TestCase(100, 50, 75, 75, 1500)]   // 150 levers, 75 still ticked, 75 stale
        [TestCase(100, 50, 0, 150, 5000)]  // 150 levers, a re-draft replaced every command: 150 stale
        public void RowsWithTicksAReDraftLeftStaleStayFarUnderTheOldCost(int literals, int templates, int current, int stale,
            int boundMs)
        {
            var setup = Enumerable.Range(0, literals).Select(i => "SelectHero H" + i)
                .Concat(Enumerable.Range(0, templates).Select(i => "call Shop" + i + ".Buy {x} 1"));
            Assert.IsNull(SyncNova.Run(_root, Sent(ShotsWithSetup(new JArray(setup.Cast<object>().ToArray()).ToString(), "[\"x\"]"),
                AdapterWithNoOverlays), "run-1", "g").Refusal);
            Assert.IsTrue(Levers.GateActive(_root), "control: the gate is live");
            var needed = Levers.Needed(_root);
            Assert.AreEqual(literals + templates, needed.Count, "control: every lever is asked for");
            var staleTicks = Enumerable.Range(0, stale)
                .Select(i => i % 2 == 0 ? "call Old" + i + ".Run {y} 2" : "SelectHero Old" + i).ToList();
            WriteApprovedByHand(needed.Take(current).Concat(staleTicks).ToArray());

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var rows = Levers.Rows(_root, null);
            watch.Stop();
            TestContext.WriteLine($"Levers.Rows, {needed.Count} needed, {current} ticked, {stale} stale: {watch.ElapsedMilliseconds} ms");
            Assert.Less(watch.ElapsedMilliseconds, boundMs,
                $"Levers.Rows took {watch.ElapsedMilliseconds} ms ({needed.Count} needed, {current} ticked, {stale} stale)");
            // CONTROLS: the rows are the ones the slow code built
            Assert.AreEqual(needed.Count + stale, rows.Count, "every needed lever and every stale tick has a row");
            Assert.AreEqual(current, rows.Count(r => r.Source == Levers.LeverSource.Needed && r.Approved), "the ticked boxes");
            Assert.AreEqual(Levers.TemplateNote, rows.Single(r => r.Command == staleTicks[0]).Note);
            Assert.AreEqual(Levers.NotNeededNote, rows.Single(r => r.Command == staleTicks[1]).Note);
        }

        /// <summary>S2 (the auditor's Q7, kept): a levers.json the kit cannot read approves nothing (fail closed, the
        /// second audit's K31), and a tick over it rewrote the file from that empty list, so every other approval in it
        /// was gone without a word. <c>SetApproved</c> now refuses to write over an EXISTING file it cannot read, and says
        /// so in its return, which the window shows as it shows a failed write.</summary>
        [Test]
        public void ATickOverALeversFileTheKitCannotReadLeavesItByteForByteAndSaysWhy()
        {
            Assert.IsNull(SyncNova.Run(_root, Sent(ShotsWithSetup("[\"SelectHero Knight\", \"set Coins 5\", \"call Shop.Buy {x} 1\"]",
                "[\"x\"]"), AdapterWithNoOverlays), "run-1", "g").Refusal);
            var path = Levers.FilePath(_root);
            foreach (var unreadable in new[]
                     {
                         "{\"$schemaVersion\":1,\"approved\":[\"SelectHero Knight\",\"set Coins 5\",\"call Shop.Buy {x} 1\",7]}",
                         "{ not json",
                         "{\"$schemaVersion\":1,\"approved\":\"set Coins 5\"}",
                         "{\"$schemaVersion\":1}",
                     })
            {
                File.WriteAllText(path, unreadable);
                var before = File.ReadAllBytes(path);
                CollectionAssert.IsEmpty(Levers.Approved(_root), "control: the kit cannot read it, so it approves nothing");
                foreach (var tick in new[] { true, false })
                {
                    var why = Levers.SetApproved(_root, "set Coins 5", tick);
                    Assert.IsNotNull(why, $"{(tick ? "a tick" : "an untick")} over a levers.json the kit cannot read was written: {unreadable}");
                    StringAssert.Contains(path + " cannot be read", why);
                    StringAssert.Contains("fix or delete it first", why);
                    StringAssert.Contains("Nothing was written: writing it now would replace every approval in it with this one", why);
                    CollectionAssert.AreEqual(before, File.ReadAllBytes(path), "the file changed: " + unreadable);
                }
            }
            // CONTROL: a readable file is updated as today
            WriteApprovedByHand("SelectHero Knight", "call Shop.Buy {x} 1");
            Assert.IsNull(Levers.SetApproved(_root, "set Coins 5", true));
            CollectionAssert.AreEqual(new[] { "SelectHero Knight", "call Shop.Buy {x} 1", "set Coins 5" }, Levers.Approved(_root).ToArray());
            // CONTROL: a missing file is created as today
            File.Delete(path);
            Assert.IsNull(Levers.SetApproved(_root, "set Coins 5", true));
            CollectionAssert.AreEqual(new[] { "set Coins 5" }, Levers.Approved(_root).ToArray());
        }

        /// <summary>M1 (the auditor's Q1, kept): the files' <c>SelectHero {{h}}</c> cannot be ticked, but h=knight bound it
        /// to <c>SelectHero {knight}</c>, a text that reads as a template, and the gate covers a template by a ticked template
        /// of the same shape, so an ordinary tick of <c>SelectHero {k}</c> ran it, and no row said so. The binder now refuses a
        /// text with a brace outside a placeholder (<c>ShotBinding.HasBraceOutsideAPlaceholder</c>, the window's brace rule,
        /// called, not copied).</summary>
        [Test]
        public void TheBinderWritesNoCommandFromATextWithABraceOutsideAPlaceholder()
        {
            const string shots = "{ \"$schemaVersion\": 1, \"shots\": [ " +
                                 "{ \"name\": \"a\", \"parameters\": [\"h\"], \"setup\": [\"SelectHero {{h}}\"], " +
                                 "\"steps\": [ { \"kind\": \"wait\", \"seconds\": 1 } ], \"settle\": { \"kind\": \"present\", \"name\": \"X\" } }, " +
                                 "{ \"name\": \"b\", \"parameters\": [\"k\"], \"setup\": [\"SelectHero {k}\"], " +
                                 "\"steps\": [ { \"kind\": \"wait\", \"seconds\": 1 } ], \"settle\": { \"kind\": \"present\", \"name\": \"X\" } } ] }";
            Assert.IsNull(SyncNova.Run(_root, Sent(shots, AdapterWithNoOverlays), "run-1", "g").Refusal);
            Assert.IsTrue(Levers.GateActive(_root), "control: the gate is live");
            var rows = Levers.Rows(_root, null);
            Assert.IsFalse(rows.Single(r => r.Command == "SelectHero {{h}}").CanTick, "control: the window will not tick it");
            Assert.IsTrue(rows.Single(r => r.Command == "SelectHero {k}").CanTick, "control: the window will tick this one");
            Assert.IsNull(Levers.SetApproved(_root, "SelectHero {k}", true), "the one tickable box, ticked as the window does");

            // THE SEAM THAT RUNS: the real director, shot a, h=knight
            var knight = new Dictionary<string, string> { ["h"] = "knight" };
            var shotA = new AdShot("a", setup: new[] { "SelectHero {{h}}" }, steps: new[] { AdStep.Wait(0.1) },
                settle: WaitCondition.Present("Board"), parameters: new[] { "h" });
            var (d, cheats) = RunDirector(shotA, bindings: knight);
            CollectionAssert.IsEmpty(Writes(cheats), "the director sent the game a binding of the files' untickable template");
            CollectionAssert.IsEmpty(LeverGateBridge.RefusedThisSession, "nothing reached the gate");
            // …because the binder refused it, by name, and wrote nothing
            Assert.IsFalse(ShotBinding.TryApply("SelectHero {{h}}", new[] { "h" }, knight, out var bound, out var error));
            Assert.AreEqual("SelectHero {{h}}", bound, "nothing is written");
            Assert.AreEqual("the lever 'SelectHero {{h}}' holds a '{' or '}' that is not part of a placeholder, so a bound " +
                            "value would make a command that reads as a placeholder itself", error);
            StringAssert.Contains("ABORT a: " + error, d.Summary);

            // CONTROL: the tickable template binds, and its binding runs under its tick, through the same director
            Assert.IsTrue(ShotBinding.TryApply("SelectHero {k}", new[] { "k" },
                new Dictionary<string, string> { ["k"] = "knight" }, out var byK, out _));
            Assert.AreEqual("SelectHero knight", byK);
            var shotB = new AdShot("b", setup: new[] { "SelectHero {k}" }, steps: new[] { AdStep.Wait(0.1) },
                settle: WaitCondition.Present("Board"), parameters: new[] { "k" });
            var (_, cheatsB) = RunDirector(shotB, bindings: new Dictionary<string, string> { ["k"] = "knight" });
            CollectionAssert.AreEqual(new[] { "SelectHero knight" }, Writes(cheatsB));
        }

        // ==== THE NINTH AUDIT (fresh context, 2026-09-22) ==========================================

        private static string ShowRow(Levers.LeverRow r) =>
            $"[{r.Source}] '{r.Command}' Approved={r.Approved} CoveredBy={(r.CoveredBy == null ? "null" : "'" + r.CoveredBy + "'")} " +
            $"CanTick={r.CanTick} Note='{r.Note}'";

        private static string Dump(IEnumerable<Levers.LeverRow> rows) => string.Join("\n", rows.Select(ShowRow));

        /// <summary>S1, ruling 2: the window reads <c>Rows</c> once a second, and it used to compute every row again each
        /// time. Now a read whose four inputs are unchanged runs the covering rule ZERO times, counted rather than timed;
        /// a change to any one of the four is read on the next call.</summary>
        [TestCase("shots.json")]
        [TestCase("adapter.json")]
        [TestCase("levers.json")]
        [TestCase("the refused list")]
        public void RowsIsComputedAgainOnlyWhenAnInputChanges(string input)
        {
            Assert.IsNull(SyncNova.Run(_root, Sent(ShotsWithSetup("[\"SelectHero {hero}\", \"set Coins 5\"]", "[\"hero\"]"),
                AdapterWithNoOverlays), "run-1", "g").Refusal);
            WriteApprovedByHand("set Coins 5", "call Old.Run {y} 2");
            var refused = new List<string> { "SelectHero Knight" };
            Levers.ForgetRowsForTests();
            var before = Levers.CoveringApprovalCallsForTests;
            var first = Levers.Rows(_root, refused);
            Assert.Greater(Levers.CoveringApprovalCallsForTests - before, 0, "control: a first read runs the covering rule");

            before = Levers.CoveringApprovalCallsForTests;
            var again = Levers.Rows(_root, new List<string>(refused));
            Assert.AreEqual(0, Levers.CoveringApprovalCallsForTests - before,
                "a read with nothing changed ran the covering rule again");
            Assert.AreEqual(Dump(first), Dump(again));

            switch (input)
            {
                case "shots.json":
                    WriteShots(ShotsWithSetup("[\"SelectHero {hero}\", \"set Coins 6\"]", "[\"hero\"]"));
                    break;
                case "adapter.json":
                    WriteAdapter("{ \"gameId\": \"g\", \"ready\": { \"mute\": \"set Music.mute true\" } }");
                    break;
                case "levers.json":
                    Assert.IsNull(Levers.SetApproved(_root, "SelectHero {hero}", true));
                    break;
                default:
                    refused.Add("SelectHero Mage");
                    break;
            }
            before = Levers.CoveringApprovalCallsForTests;
            var after = Levers.Rows(_root, refused);
            Assert.Greater(Levers.CoveringApprovalCallsForTests - before, 0, $"a change to {input} was not read");
            Assert.AreNotEqual(Dump(first), Dump(after), $"control: the change to {input} shows in the rows");
        }

        /// <summary>S1, rulings 1 and 3: THE WORST SHAPE THIS BUILD COULD MAKE AT THE CAP, timed once, cold. Levers.MaxLevers
        /// needed templates (delivered: a file at the cap is taken), as many stale ticked templates, and the 50 refused
        /// commands the gate remembers — all sharing their literal text before the first placeholder, so the prefilter
        /// passes every pair and each one costs a regex match. The auditor's H3 (templates with distinct prefixes) is
        /// cheaper at the same size. Before the memo this shape took 3.5 s at 200 of each.
        ///
        /// The eleventh audit, M1: every one of the three lists is now as LONG as a lever may be
        /// (<see cref="Levers.MaxLeverLength"/>) — the pair cost grows with lever length, and the auditor measured 925–980 ms
        /// with 1,690-character levers, which the byte cap alone allowed. A lever one character longer is refused at
        /// delivery (the control below), which is what makes this the worst shape at the cap.</summary>
        [Test]
        public void RowsAtTheLeverCapWithTheWorstShapeTakeUnderASecond()
        {
            var n = Levers.MaxLevers;
            // padded with x's before a unique suffix, to exactly the longest lever a delivery may hold
            string Long(string head, string tail) => head + new string('x', Levers.MaxLeverLength - head.Length - tail.Length) + tail;
            var needed = Enumerable.Range(0, n).Select(i => Long("set Player.{s} {v} ", " n" + i)).ToList();
            Assert.IsTrue(needed.All(l => l.Length == Levers.MaxLeverLength), "control: every lever is at the length cap");
            var tooLong = needed.Select(l => l + "x").ToList();
            Assert.IsNotNull(SyncNova.Run(_root, Sent(ShotsWithSetup(new JArray(tooLong.Cast<object>().ToArray()).ToString(),
                    "[\"s\", \"v\"]"), AdapterWithNoOverlays), "run-0", "g").Refusal,
                "control: one character longer is refused at delivery — the length cap is what bounds this shape");
            Assert.IsNull(SyncNova.Run(_root, Sent(ShotsWithSetup(new JArray(needed.Cast<object>().ToArray()).ToString(),
                "[\"s\", \"v\"]"), AdapterWithNoOverlays), "run-1", "g").Refusal, "control: a delivery at the cap is taken");
            Assert.AreEqual(n, Levers.Needed(_root).Count, "control: every lever is asked for");
            var stale = Enumerable.Range(0, n).Select(i => Long("set Player.{a} {b} ", " o" + i)).ToArray();
            WriteApprovedByHand(stale);
            var refused = Enumerable.Range(0, LeverGateBridge.MaxRefusedRemembered).Select(i => Long("set Player.q 1 ", " z" + i)).ToList();

            Levers.ForgetRowsForTests();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var rows = Levers.Rows(_root, refused);
            watch.Stop();
            TestContext.WriteLine($"Levers.Rows at the cap ({n} needed, {n} stale, {refused.Count} refused): {watch.ElapsedMilliseconds} ms");
            Assert.Less(watch.ElapsedMilliseconds, 1000,
                $"Levers.Rows took {watch.ElapsedMilliseconds} ms at the cap ({n} needed, {n} stale, {refused.Count} refused)");
            // CONTROLS: every row is there, and each kind says what the plain code says of it
            Assert.AreEqual(2 * n + refused.Count, rows.Count);
            Assert.AreEqual("", rows.Single(r => r.Command == needed[0]).Note);
            Assert.AreEqual(Levers.TemplateNote, rows.Single(r => r.Command == stale[0]).Note);
            Assert.AreEqual(Levers.RefusedThisSessionNote, rows.Single(r => r.Command == refused[0]).Note);
        }

        /// <summary>S1, ruling 3: the prefilter and the per-call memo change no row. The auditor's five shapes, the fourth
        /// scaled to the cap (the auditor's own 3,000-template H3 was compared once, byte for byte, in the fold's report),
        /// each computed with the memo off — the code before this fold — and on.</summary>
        [Test]
        public void RowsAreByteForByteTheSameWithTheMemoOff()
        {
            var n = Levers.MaxLevers;
            var shapes = new List<(string label, List<string> needed, List<string> ticks, List<string> refused)>
            {
                ("A150", Enumerable.Range(0, 150).Select(i => "call Shop" + i + ".Buy {x} 1").ToList(),
                    Enumerable.Range(0, 150).Select(i => "call Old" + i + ".Run {y} 2").ToList(), new List<string>()),
                ("A200", Enumerable.Range(0, 200).Select(i => "call Shop" + i + ".Buy {x} 1").ToList(),
                    Enumerable.Range(0, 200).Select(i => "call Old" + i + ".Run {y} 2").ToList(), new List<string>()),
                ("R150", Enumerable.Range(0, 100).Select(i => "SelectHero H" + i)
                        .Concat(Enumerable.Range(0, 50).Select(i => "call Shop" + i + ".Buy {x} 1")).ToList(),
                    Enumerable.Range(0, 150).Select(i => i % 2 == 0 ? "call Old" + i + ".Run {y} 2" : "SelectHero Old" + i).ToList(),
                    new List<string>()),
                ("H3 at the cap", Enumerable.Range(0, n).Select(i => "call Shop" + i + ".Buy {x} 1").ToList(),
                    Enumerable.Range(0, 50).Select(i => "call Old" + i + ".Run {y} 2").ToList(),
                    Enumerable.Range(0, 50).Select(i => "call Shop" + (i * 59 % n) + ".Buy 5 1").ToList()),
                ("M", new List<string> { "SelectHero {hero}", "set Player.{s} {v}", "call Shop.Buy {x} 1", "set Player.coins {v}", "SelectHero Knight" },
                    new List<string> { "SelectHero K{x}", "set Player.{a} {b}", "call Shop.Buy 5 1", "SelectHero {h}", "call Shop.{m} {x} 1", "set Player.coins 5" },
                    new List<string> { "SelectHero Queen", "set Player.hp 3", "call Shop.Buy 7 1", "set Player.a.b 1" }),
            };
            try
            {
                foreach (var (label, needed, ticks, refused) in shapes)
                {
                    WriteShots(ShotsWithSetup(new JArray(needed.Cast<object>().ToArray()).ToString(), "[\"x\", \"hero\", \"s\", \"v\"]"));
                    WriteApprovedByHand(ticks.ToArray());
                    Levers.RowsMemoOffForTests = true;
                    var plain = Levers.Rows(_root, refused);
                    Levers.RowsMemoOffForTests = false;
                    var memo = Levers.Rows(_root, refused);
                    Assert.AreEqual(needed.Count + ticks.Count, plain.Count(r => r.Source != Levers.LeverSource.RefusedHere),
                        $"control, {label}: every needed lever and every tick has a row");
                    Assert.AreEqual(Dump(plain), Dump(memo), label);
                }
            }
            finally
            {
                Levers.RowsMemoOffForTests = false;
            }
        }

        /// <summary>M1 (the auditor's P2, kept): a shot with no parameters is never bound, so its <c>SelectHero {knight}</c>
        /// reached the gate as text, and a tick of the other shot's <c>SelectHero {k}</c> covered it by shape and ran it.
        /// The delivery is now refused, by name, before anything is written; the project keeps the shots it had, and the
        /// director never sends that text.</summary>
        [Test]
        public void AShotWithNoParametersCannotDeliverATextATickOfAnotherShapeWouldRun()
        {
            const string shotB = "{ \"name\": \"b\", \"parameters\": [\"k\"], \"setup\": [\"SelectHero {k}\"], " +
                                 "\"steps\": [ { \"kind\": \"wait\", \"seconds\": 1 } ], \"settle\": { \"kind\": \"present\", \"name\": \"X\" } }";
            const string shotA = "{ \"name\": \"a\", \"setup\": [\"SelectHero {knight}\"], " +
                                 "\"steps\": [ { \"kind\": \"wait\", \"seconds\": 1 } ], \"settle\": { \"kind\": \"present\", \"name\": \"X\" } }";
            Assert.IsNull(SyncNova.Run(_root, Sent("{ \"$schemaVersion\": 1, \"shots\": [ " + shotB + " ] }", AdapterWithNoOverlays),
                "run-1", "g").Refusal, "control: shot b alone is delivered");
            Assert.IsNull(Levers.SetApproved(_root, "SelectHero {k}", true), "its template ticked, as the window does");
            var shotsBefore = File.ReadAllBytes(RelayPaths.NovaShotsFile(_root));
            var syncedBefore = File.ReadAllBytes(SyncNova.SyncedFile(_root));

            var result = SyncNova.Run(_root, Sent("{ \"$schemaVersion\": 1, \"shots\": [ " + shotA + ", " + shotB + " ] }",
                AdapterWithNoOverlays), "run-2", "g");
            Assert.AreEqual("shot 'a' declares no parameter 'knight', so its 'SelectHero {knight}' would reach the game as text " +
                            "— nothing was written", result.Refusal);
            CollectionAssert.AreEqual(shotsBefore, File.ReadAllBytes(RelayPaths.NovaShotsFile(_root)), "nothing was written");
            CollectionAssert.AreEqual(syncedBefore, File.ReadAllBytes(SyncNova.SyncedFile(_root)), "nothing was recorded");

            // THE SEAM THAT RUNS: every shot this project holds, through the real director, under the tick
            var loaded = JsonShotLoader.LoadFromPath(RelayPaths.NovaShotsFile(_root));
            CollectionAssert.AreEqual(new[] { "b" }, loaded.Shots.Select(s => s.Name).ToArray());
            var sent = new List<string>();
            foreach (var shot in loaded.Shots)
                sent.AddRange(Writes(RunDirector(shot, bindings: new Dictionary<string, string> { ["k"] = "knight" }).cheats));
            CollectionAssert.DoesNotContain(sent, "SelectHero {knight}");
            // (the shot's settle never holds in this fake game, so the director tries it again: the same command twice)
            CollectionAssert.AreEqual(new[] { "SelectHero knight" }, sent.Distinct().ToArray(),
                "control: the delivered shot runs under its tick");

            // …and adapter.json's half, where nothing binds a placeholder: the ready block is sent as written
            Assert.AreEqual("adapter.json binds no placeholder, so its 'set Audio.{v} 0' would reach the game as text — " +
                            "nothing was written",
                SyncNova.Run(_root, Sent("{ \"$schemaVersion\": 1, \"shots\": [ " + shotB + " ] }",
                    "{ \"gameId\": \"g\", \"ready\": { \"mute\": \"set Audio.{v} 0\" } }"), "run-3", "g").Refusal);
            CollectionAssert.AreEqual(shotsBefore, File.ReadAllBytes(RelayPaths.NovaShotsFile(_root)));
        }

        /// <summary>KE1 and KE2 (surviving mutants: Regex.Escape dropped for the text before a placeholder, or after the
        /// last one, with every test green). Each template below can be ticked; its literal text is text, not a pattern.</summary>
        [Test]
        public void TheGatesMatchReadsEveryLiteralPieceAsText()
        {
            foreach (var (template, own, other) in new[]
                     {
                         ("SelectHero a|{x} 1", "SelectHero a|abc 1", "SelectHero abc DeleteSave"),
                         ("call Shop.Buy {x} 1", "call Shop.Buy 5 1", "call ShopXBuy 5 1"),
                         ("call Shop.{m}.Run", "call Shop.Buy.Run", "call Shop.BuyXRun"),
                     })
            {
                Assert.IsNull(Levers.NotTickableReason(template), "control: " + template + " can be ticked");
                Assert.IsTrue(ShotBinding.MatchesTemplate(template, own), "control: " + own);
                Assert.IsTrue(Levers.Allows(new[] { template }, own), "control: " + own);
                Assert.IsFalse(ShotBinding.MatchesTemplate(template, other), $"'{template}' matched '{other}'");
                Assert.IsFalse(Levers.Allows(new[] { template }, other), $"a tick of '{template}' approved '{other}'");
            }
        }

        /// <summary>KO (a surviving mutant: the overlay lever compared ignoring case). A tick names the type as written.</summary>
        [Test]
        public void ATickOfAnOverlayLeverNamesItsTypeCaseAndAll()
        {
            Assert.IsNull(SyncNova.Run(_root, Sent(ShotsWithSetup("[\"set Coins 5\"]"),
                "{ \"gameId\": \"g\", \"overlayTypeNames\": [\"DebugHud\"] }"), "run-1", "g").Refusal);
            Assert.IsTrue(Levers.GateActive(_root), "control: the gate is live");
            WriteApprovedByHand("hide-overlay debughud");
            var log = new List<string>();
            var hidden = Levers.OverlaysAllowed(_root, new[] { "DebugHud" }, new[] { "DebugHud" }, log.Add, adapterIsJsonDriven: true);
            CollectionAssert.IsEmpty(hidden, "a tick of 'hide-overlay debughud' hid DebugHud");
            CollectionAssert.AreEqual(new[] { Levers.OverlayLeftVisibleLog("DebugHud") }, log);
            // CONTROL: the tick as written hides it
            WriteApprovedByHand("hide-overlay DebugHud");
            CollectionAssert.AreEqual(new[] { "DebugHud" },
                Levers.OverlaysAllowed(_root, new[] { "DebugHud" }, new[] { "DebugHud" }, null, adapterIsJsonDriven: true));
        }

        /// <summary>M3 (the auditor's P4): a levers.json of another version was read as version 1 and written back as
        /// version 1, dropping whatever else it said (a newer kit's list of denials). It is now read like an unreadable
        /// file — nothing approved — and a tick or an untick leaves it byte for byte and says why.</summary>
        [Test]
        public void ATickOverALeversFileOfAnotherVersionLeavesItByteForByteAndSaysWhy()
        {
            Assert.IsNull(SyncNova.Run(_root, Sent(ShotsWithSetup("[\"set Coins 5\"]"), AdapterWithNoOverlays), "run-1", "g").Refusal);
            var path = Levers.FilePath(_root);
            foreach (var (file, shown) in new[]
                     {
                         ("{\"$schemaVersion\":2,\"approved\":[\"set Coins 5\"],\"denied\":[\"raw wipe\"]}", "2"),
                         ("{\"approved\":[\"set Coins 5\"]}", "missing"),
                         ("{\"$schemaVersion\":\"1\",\"approved\":[\"set Coins 5\"]}", "\"1\""),
                         ("{\"$schemaVersion\":1.0,\"approved\":[\"set Coins 5\"]}", "1.0"),
                     })
            {
                File.WriteAllText(path, file);
                var before = File.ReadAllBytes(path);
                CollectionAssert.IsEmpty(Levers.Approved(_root), "approves nothing: " + file);
                foreach (var tick in new[] { true, false })
                {
                    Assert.AreEqual($"{path} cannot be read (levers.json: $schemaVersion is {shown}, not 1, so it was written " +
                                    "by a newer kit or by hand); fix or delete it first. Nothing was written: writing it now " +
                                    "would replace every approval in it with this one.",
                        Levers.SetApproved(_root, "set Coins 5", tick), file);
                    CollectionAssert.AreEqual(before, File.ReadAllBytes(path), "written over: " + file);
                }
            }
            // CONTROL: version 1 is read and written as before
            File.WriteAllText(path, "{\"$schemaVersion\":1,\"approved\":[\"set Coins 5\"]}");
            CollectionAssert.AreEqual(new[] { "set Coins 5" }, Levers.Approved(_root).ToArray());
            Assert.IsNull(Levers.SetApproved(_root, "set Coins 5", false));
            CollectionAssert.IsEmpty(Levers.Approved(_root));
        }

        /// <summary>M4 (the auditor's P9): a lever holding a line break was delivered, could be ticked, and ran with the
        /// part after the break as more arguments. It cannot be ticked, the gate ignores one ticked by hand, and a delivery
        /// holding one is refused before anything is written.</summary>
        [Test]
        public void ALeverHoldingALineBreakCannotBeTickedRunsNothingAndIsNeverDelivered()
        {
            Assert.IsNull(SyncNova.Run(_root, Sent(ShotsWithSetup("[\"set Coins 5\"]"), AdapterWithNoOverlays), "run-1", "g").Refusal);
            const string lf = "raw give {item}\n9999";
            Assert.AreEqual("this command holds U+000A, a line break, control or invisible formatting character, so the row " +
                            "you would tick is not the command that would run, and it cannot be approved here.",
                Levers.NotTickableReason(lf));
            StringAssert.StartsWith("this command holds U+2028,", Levers.NotTickableReason("raw give {item}\u20289999"));
            Assert.AreEqual(Levers.NotTickableReason(lf), Levers.SetApproved(_root, lf, true), "the window will not tick it");

            WriteApprovedByHand("raw give sword\n9999", "set Coins 5");
            var (gate, inner, _) = Gate();
            Assert.IsFalse(gate.Run("raw give sword\n9999"), "a hand-ticked line break approves nothing");
            Assert.IsTrue(gate.Run("set Coins 5"), "control");
            CollectionAssert.AreEqual(new[] { "set Coins 5" }, inner.Run_);

            var shotsBefore = File.ReadAllBytes(RelayPaths.NovaShotsFile(_root));
            Assert.AreEqual("the lever \"raw give {item}\\n9999\" holds U+000A, a line break, control or invisible formatting " +
                            "character, so the row a person ticks would not be the command that runs — nothing was written",
                SyncNova.Run(_root, Sent(ShotsWithSetup(new JArray(lf).ToString(), "[\"item\"]"), AdapterWithNoOverlays),
                    "run-2", "g").Refusal);
            CollectionAssert.AreEqual(shotsBefore, File.ReadAllBytes(RelayPaths.NovaShotsFile(_root)), "nothing was written");
        }

        /// <summary>Kr (the auditor's surviving mutant): the gate's match made case-insensitive failed no test. The literal
        /// text around a placeholder is compared as the binder writes it, case and all.</summary>
        [Test]
        public void TheGatesMatchReadsTheLiteralTextCaseSensitively()
        {
            Assert.IsTrue(ShotBinding.MatchesTemplate("SelectHero {hero}", "SelectHero Knight"), "control");
            Assert.IsFalse(ShotBinding.MatchesTemplate("SelectHero {hero}", "selecthero Knight"));
            Assert.IsFalse(ShotBinding.MatchesTemplate("SelectHero {hero}", "SELECTHERO Knight"));
            Assert.IsTrue(Levers.Allows(new[] { "SelectHero {hero}" }, "SelectHero Knight"), "control");
            Assert.IsFalse(Levers.Allows(new[] { "SelectHero {hero}" }, "selecthero Knight"));
        }

        // ==== THE TENTH AUDIT (fresh context, 2026-09-22) ==========================================

        /// <summary>Rows with the memo off and on, each computed cold.</summary>
        private (IReadOnlyList<Levers.LeverRow> plain, IReadOnlyList<Levers.LeverRow> memo) RowsBothWays(IEnumerable<string>? refused)
        {
            try
            {
                Levers.RowsMemoOffForTests = true;
                Levers.ForgetRowsForTests();
                var plain = Levers.Rows(_root, refused);
                Levers.RowsMemoOffForTests = false;
                Levers.ForgetRowsForTests();
                return (plain, Levers.Rows(_root, refused));
            }
            finally
            {
                Levers.RowsMemoOffForTests = false;
            }
        }

        /// <summary>The gate's own answer for a row's text — as a template (<see cref="Levers.IsTicked"/>) or as a command
        /// (<see cref="Levers.Allows"/>) — or null for the three rows <see cref="Levers.Rows"/> documents as showing
        /// something else ("where the box and the gate still differ"): a ticked entry's own row, a read, and a hand-ticked
        /// entry the gate ignores.</summary>
        private static bool? GateSays(Levers.LeverRow row, IReadOnlyList<string> approved)
        {
            if (row.Source == Levers.LeverSource.Approved || Levers.IsReadOnly(row.Command)) return null;
            if (approved.Contains(row.Command) && Levers.NotLiteralEnough(row.Command)) return null;
            return ShotBinding.HasPlaceholder(row.Command) ? Levers.IsTicked(approved, row.Command) : Levers.Allows(approved, row.Command);
        }

        /// <summary>KA1 (a surviving mutant: <see cref="ShotBinding.ShapeKey"/> without the length in front of each literal
        /// piece). <c>raw give {a} :{b}</c> and <c>raw give  {a}:{b}</c> are different shapes — their literal pieces differ
        /// and only run together into one text — so a tick of one covers no binding of the other at the gate, and the
        /// window's memo must not show one as covered by the other.</summary>
        [Test]
        public void TwoTemplatesWhoseLiteralPiecesRunTogetherAreNotOneShape()
        {
            const string needed = "raw give {a} :{b}";
            const string ticked = "raw give  {a}:{b}";
            Assert.IsNull(Levers.NotTickableReason(needed), "control: '" + needed + "' can be ticked");
            Assert.IsNull(Levers.NotTickableReason(ticked), "control: '" + ticked + "' can be ticked");
            Assert.IsFalse(ShotBinding.SameShape(needed, ticked));
            Assert.AreNotEqual(ShotBinding.ShapeKey(needed), ShotBinding.ShapeKey(ticked));
            Assert.IsFalse(Levers.IsTicked(new[] { ticked }, needed), "the gate's own answer for the template");
            Assert.IsFalse(Levers.Allows(new[] { ticked }, "raw give x :y"), "the gate's own answer for a binding of it");

            WriteShots(ShotsWithSetup(new JArray(needed).ToString(), "[\"a\", \"b\"]"));
            WriteApprovedByHand(ticked);
            var (plain, memo) = RowsBothWays(null);
            Assert.AreEqual(Dump(plain), Dump(memo));
            var row = memo.Single(r => r.Command == needed);
            Assert.IsNull(row.CoveredBy, Dump(memo));
            Assert.IsFalse(row.Approved, Dump(memo));

            // CONTROL: the needed template's own shape, ticked under other names, covers it
            WriteApprovedByHand("raw give {x} :{y}");
            Assert.AreEqual("raw give {x} :{y}", RowsBothWays(null).memo.Single(r => r.Command == needed).CoveredBy);
        }

        /// <summary>KA3 and KA6 (surviving mutants of the window's memo: its exact pass without the not-literal-enough
        /// check, and its template pass over every ticked template instead of the ones the gate would use). A hand-ticked
        /// <c>SelectHero {a}{b}</c> is not an entry the gate uses, nor is a hand-ticked <c>camera-pose {a}.{b}</c>, so
        /// neither covers anything: the rows are byte for byte the rows with the memo off, <c>camera-pose a.b</c> is not
        /// shown approved, and every row's box is the gate's own answer for it.</summary>
        [Test]
        public void AHandTickTheGateIgnoresCoversNoRowWithTheMemoOnOrOff()
        {
            var needed = new[] { "SelectHero {a}{b}", "set Player.{s} {v}" };
            var ticks = new[] { "SelectHero {a}{b}", "camera-pose {a}.{b}", "set Player.{x} {y}" };
            var refused = new List<string> { "camera-pose a.b", "SelectHero Knight" };
            Assert.IsNotNull(Levers.NotTickableReason("SelectHero {a}{b}"), "control: the window will not tick it");
            Assert.IsNotNull(Levers.NotTickableReason("camera-pose {a}.{b}"), "control: the window will not tick it");
            Assert.IsFalse(Levers.Allows(ticks, "camera-pose a.b"), "control: the gate refuses camera-pose a.b under these ticks");
            Assert.IsTrue(Levers.IsTicked(ticks, "set Player.{s} {v}"), "control: the usable template covers its shape");

            WriteShots(ShotsWithSetup(new JArray(needed.Cast<object>().ToArray()).ToString(), "[\"a\", \"b\", \"s\", \"v\"]"));
            WriteApprovedByHand(ticks);
            var (plain, memo) = RowsBothWays(refused);
            Assert.AreEqual(Dump(plain), Dump(memo));
            var pose = memo.Single(r => r.Command == "camera-pose a.b");
            Assert.IsFalse(pose.Approved, Dump(memo));
            Assert.IsNull(pose.CoveredBy, Dump(memo));
            Assert.IsNull(memo.Single(r => r.Command == "SelectHero {a}{b}").CoveredBy, Dump(memo));
            var approved = Levers.Approved(_root);
            foreach (var row in memo)
                if (GateSays(row, approved) is { } gate)
                    Assert.AreEqual(gate, row.Approved, "the box and the gate differ on " + ShowRow(row));
            Assert.AreEqual("set Player.{x} {y}", memo.Single(r => r.Command == "set Player.{s} {v}").CoveredBy, "control");
        }

        /// <summary>The tenth audit, S1 — THE AUDITOR'S TWO DELIVERIES, each led by U+FEFF. The first is round 9's M1 again:
        /// shot b (parameters [k], setup <c>SelectHero {k}</c>) delivered and its template ticked, then shot a (no parameters,
        /// setup <c>SelectHero {knight}</c>) beside it — written with the mark, the real director sent
        /// <c>SelectHero {knight}</c> under the tick of <c>SelectHero {k}</c>. The second carried 3,000 levers past the cap.
        /// Both are refused by name, nothing is written, and the director never sends the text; without the mark each is
        /// refused as it was.</summary>
        [Test]
        public void ByteOrderMarkLedDeliveriesCannotReopenTheNinthAuditsM1OrPassTheLeverCap()
        {
            const string bom = "\uFEFF";
            const string refusal = "shots.json cannot be read as the kit will read it (it begins with U+FEFF, which the kit's " +
                                   "readers drop as a byte-order mark) — nothing was written";
            const string shotB = "{ \"name\": \"b\", \"parameters\": [\"k\"], \"setup\": [\"SelectHero {k}\"], " +
                                 "\"steps\": [ { \"kind\": \"wait\", \"seconds\": 1 } ], \"settle\": { \"kind\": \"present\", \"name\": \"X\" } }";
            const string shotA = "{ \"name\": \"a\", \"setup\": [\"SelectHero {knight}\"], " +
                                 "\"steps\": [ { \"kind\": \"wait\", \"seconds\": 1 } ], \"settle\": { \"kind\": \"present\", \"name\": \"X\" } }";
            Assert.IsNull(SyncNova.Run(_root, Sent("{ \"$schemaVersion\": 1, \"shots\": [ " + shotB + " ] }", AdapterWithNoOverlays),
                "run-1", "g").Refusal, "control: shot b alone is delivered");
            Assert.IsNull(Levers.SetApproved(_root, "SelectHero {k}", true), "its template ticked, as the window does");
            var shotsBefore = File.ReadAllBytes(RelayPaths.NovaShotsFile(_root));
            var syncedBefore = File.ReadAllBytes(SyncNova.SyncedFile(_root));
            var leversBefore = File.ReadAllBytes(Levers.FilePath(_root));
            void NothingWasWritten(string what)
            {
                CollectionAssert.AreEqual(shotsBefore, File.ReadAllBytes(RelayPaths.NovaShotsFile(_root)), what + ": shots.json was written");
                CollectionAssert.AreEqual(syncedBefore, File.ReadAllBytes(SyncNova.SyncedFile(_root)), what + ": synced.json was written");
                CollectionAssert.AreEqual(leversBefore, File.ReadAllBytes(Levers.FilePath(_root)), what + ": levers.json was written");
            }

            var two = "{ \"$schemaVersion\": 1, \"shots\": [ " + shotA + ", " + shotB + " ] }";
            Assert.AreEqual(refusal, SyncNova.Run(_root, Sent(bom + two, AdapterWithNoOverlays), "run-2", "g").Refusal);
            NothingWasWritten("round 9's M1, led by U+FEFF");
            StringAssert.StartsWith("shot 'a' declares no parameter 'knight'",
                SyncNova.Run(_root, Sent(two, AdapterWithNoOverlays), "run-3", "g").Refusal, "control: without the mark, refused as before");

            // THE SEAM THAT RUNS: every shot this project holds, through the real director, under the tick
            var loaded = JsonShotLoader.LoadFromPath(RelayPaths.NovaShotsFile(_root));
            CollectionAssert.AreEqual(new[] { "b" }, loaded.Shots.Select(s => s.Name).ToArray());
            var sent = new List<string>();
            foreach (var shot in loaded.Shots)
                sent.AddRange(Writes(RunDirector(shot, bindings: new Dictionary<string, string> { ["k"] = "knight" }).cheats));
            CollectionAssert.DoesNotContain(sent, "SelectHero {knight}");
            CollectionAssert.AreEqual(new[] { "SelectHero knight" }, sent.Distinct().ToArray(), "control: the delivered shot runs under its tick");

            // the cap: 3,000 levers — over six shots of 500, since the thirteenth audit caps one shot's setup at 500
            var big = "{ \"$schemaVersion\": 1, \"shots\": [ " + string.Join(", ", Enumerable.Range(0, 6).Select(s =>
                "{ \"name\": \"a" + s + "\", \"parameters\": [\"s\", \"v\"], \"setup\": " +
                new JArray(Enumerable.Range(s * 500, 500).Select(i => (object)("set Player.{s} {v} n" + i)).ToArray()).ToString() +
                ", \"steps\": [ { \"kind\": \"wait\", \"seconds\": 1 } ], \"settle\": { \"kind\": \"present\", \"name\": \"X\" } }")) + " ] }";
            Assert.AreEqual(refusal, SyncNova.Run(_root, Sent(bom + big, AdapterWithNoOverlays), "run-4", "g").Refusal);
            NothingWasWritten("3,000 levers, led by U+FEFF");
            CollectionAssert.AreEqual(new[] { "SelectHero {k}" }, Levers.Needed(_root).ToArray(), "the window lists what was delivered");
            StringAssert.StartsWith("these files ask for 3000 levers",
                SyncNova.Run(_root, Sent(big, AdapterWithNoOverlays), "run-5", "g").Refusal, "control: without the mark, refused by the cap");
        }

        /// <summary>KA9 (a surviving mutant: the kit's one set of characters it will not take, without its two alternatives
        /// for half of a surrogate pair). A command holding a lone half cannot be ticked and the gate ignores one ticked by
        /// hand: its bytes carry U+FFFD in its place, so the row is not the command that runs. Pinned here in C# rather than
        /// in <c>lever-shapes.cases.json</c>, because this kit's JSON reader turns a <c>\ud800</c> escape into U+FFFD — the
        /// shared fixture can never hand the kit a lone half (the site's JSON.parse keeps one, and refuses it).</summary>
        [Test]
        public void ALeverHoldingHalfASurrogatePairCannotBeTickedAndApprovesNothing()
        {
            foreach (var (command, code) in new[] { ("raw give {item}\uD8009999", "U+D800"), ("raw give {item}\uDC009999", "U+DC00"),
                                                     ("SelectHero \uDBFF", "U+DBFF"), ("\uDFFFSelectHero Knight", "U+DFFF") })
            {
                Assert.AreEqual(code, Levers.UnsendableCharacter(command), command);
                StringAssert.StartsWith($"this command holds {code},", Levers.NotTickableReason(command));
                Assert.IsTrue(Levers.NotLiteralEnough(command), code + ": the gate would use it");
                Assert.IsFalse(Levers.Allows(new[] { command }, command), code + ": ticked by hand, it approves its own text");
            }
            // CONTROLS: a whole pair (an emoji) is one character and fine; and why this is not in the shared fixture
            Assert.IsNull(Levers.NotTickableReason("SelectHero \uD83D\uDE00 {hero}"));
            Assert.IsNull(Levers.UnsendableCharacter("SelectHero \uD83D\uDE00"));
            Assert.AreEqual("a\uFFFDb", NovaJson.ParseObject("{ \"c\": \"a\\ud800b\" }")["c"]!.Value<string>(),
                "this kit's JSON reader reads a lone-surrogate escape as U+FFFD");
        }

        /// <summary>
        /// The tenth audit, ruling 4 — THE AUDITOR'S SEEDED RANDOM-SHAPE COMPARISON, kept as a test: the same seed
        /// (20260922) and the same 400 cases the audit ran. Each case writes a random shots.json (1–11 commands drawn from
        /// verbs, targets and values chosen to hit every not-literal-enough rule), a random levers.json (random texts,
        /// bindings of the needed commands, and sometimes a needed command itself) and a random refused list, and asks two
        /// questions: are the rows byte for byte the same with the memo off and on; and does each row's box say what the
        /// gate says of that row's text (<see cref="GateSays"/>). Under the audit's surviving mutants it found 318 rows
        /// that differ and 2 boxes the gate contradicts; it is the net under <c>RowsAreByteForByteTheSameWithTheMemoOff</c>'s
        /// five fixed shapes.
        /// </summary>
        [Test]
        public void TheMemoAndTheBoxAgreeWithThePlainRuleAndTheGateOnSeededRandomShapes()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var rng = new Random(20260922);
            string[] verbs = { "set", "call", "raw", "SelectHero", "hide-overlay", "camera-spec", "click", "get", "camera-pose", "Set" };
            string[] targets = { "Player", "Player.coins", "Shop.Buy", "Shop", "A.b.c", "Player.x", "K" };
            string[] vals = { "{a}", "{b}", "{x}", "{hero}", "1", "5", "Knight", "a", "a.b", "{a}.{b}", "K{x}", "{a}{b}", "\"q\"", "{{h}}", "x}",
                              "Player.{s}", "{s}.coins", "a:b", "{a}:{b}" };
            string Word(string[] from) => from[rng.Next(from.Length)];
            string Text()
            {
                var n = rng.Next(1, 4);
                var parts = new List<string> { Word(verbs) };
                if (rng.Next(3) > 0) parts.Add(rng.Next(3) == 0 ? Word(vals) : Word(targets) + (rng.Next(2) == 0 ? "" : "." + Word(vals)));
                for (var i = 0; i < n; i++) parts.Add(Word(vals));
                return string.Join(" ", parts);
            }
            const int cases = 400;
            var differ = new List<string>();
            var boxGate = new List<string>();
            for (var k = 0; k < cases; k++)
            {
                var needed = Enumerable.Range(0, rng.Next(1, 12)).Select(_ => Text()).Distinct().ToList();
                var ticks = Enumerable.Range(0, rng.Next(0, 12)).Select(_ => rng.Next(4) == 0
                    ? ShotBinding.WithEveryPlaceholderAs(Word(needed.ToArray()), rng.Next(2) == 0 ? "a" : "Knight")
                    : Text()).Distinct().ToList();
                if (rng.Next(3) == 0 && needed.Count > 0) ticks.Add(needed[0]);
                var refused = Enumerable.Range(0, rng.Next(0, 6))
                    .Select(_ => ShotBinding.WithEveryPlaceholderAs(Text(), rng.Next(2) == 0 ? "a" : "a.b")).ToList();
                WriteShots(ShotsWithSetup(new JArray(needed.Cast<object>().ToArray()).ToString(),
                    "[\"a\", \"b\", \"x\", \"hero\", \"s\", \"h\"]"));
                WriteApprovedByHand(ticks.Distinct().ToArray());
                var (plain, memo) = RowsBothWays(refused);
                if (Dump(plain) != Dump(memo))
                    differ.Add($"case {k}:\n plain:\n{Dump(plain)}\n memo:\n{Dump(memo)}");
                var approved = Levers.Approved(_root);
                foreach (var row in memo)
                    if (GateSays(row, approved) is { } gate && gate != row.Approved)
                        boxGate.Add($"case {k}: {ShowRow(row)} gate={gate} ticks=[{string.Join(" | ", approved)}]");
            }
            watch.Stop();
            TestContext.WriteLine($"{cases} seeded cases in {watch.ElapsedMilliseconds} ms: {differ.Count} differ with the memo off, " +
                                  $"{boxGate.Count} boxes the gate contradicts");
            Assert.IsEmpty(differ, $"{differ.Count} of {cases} cases differ with the memo off; the first:\n" + differ.FirstOrDefault());
            Assert.IsEmpty(boxGate, $"{boxGate.Count} boxes the gate contradicts; the first five:\n" + string.Join("\n", boxGate.Take(5)));
        }
    }
}
