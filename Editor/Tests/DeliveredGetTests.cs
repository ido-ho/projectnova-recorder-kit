using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// A STUDIO TYPE WHOSE GETTER WRITES — the fourteenth audit's observation, made a test host. The lazy singleton is an
    /// ordinary shape in a Unity game (`Instance =&gt; _i ??= new GameObject().AddComponent&lt;Foo&gt;()`), and reading its
    /// property runs that code: it creates an object in the studio's scene. So a `get` is not a read; it is a call.
    /// ARMED only by the tests that use it, so no other test's reflection (a `singletons` listing reads every static
    /// Instance it finds) makes anything.
    /// </summary>
    public static class DeliveredGetLazySingleton
    {
        public const string MarkerName = "DeliveredGetLazySingleton — made by a property getter";
        internal static bool Armed;
        private static GameObject? _made;

        public static GameObject? Instance
        {
            get
            {
                if (Armed && _made == null) _made = new GameObject(MarkerName);
                return _made;
            }
        }

        /// <summary>What the getter made, if anything (Unity's null for a destroyed object).</summary>
        internal static GameObject? Made => _made == null ? null : _made;

        internal static void Reset()
        {
            if (_made != null) UnityEngine.Object.DestroyImmediate(_made);
            _made = null;
            Armed = false;
        }
    }

    /// <summary>A studio type with plain static fields: a ticked `set` the shot runs (proof the shot ran), and the bool a
    /// ready block's `muteGet` reads.</summary>
    public static class DeliveredGetTarget
    {
        public static int coins;
        public static bool muted;
    }

    /// <summary>
    /// The fourteenth audit (2026-09-22) — A DELIVERED `get` IS A LEVER. Thirteen rounds treated `get …` as a read and let
    /// it past a live gate with no tick (<c>Levers.IsReadOnly</c>). It is reflection into the studio's game: a property
    /// getter is code, and the lazy singleton above writes into the scene when it is read. So the free list is now the
    /// kit's own three fixed verbs — exactly <c>ui-dump</c>, <c>show-ui</c>, <c>hide-ui</c> — and a delivered `get`, from
    /// a shot or from adapter.json's <c>ready.muteGet</c>, needs a tick like any lever. Every test here runs the REAL
    /// <see cref="GenericCheatBridge"/> under the real director, on a project the cloud delivered to through
    /// <see cref="SyncNova.Run"/>, with the gate live.
    /// </summary>
    public class DeliveredGetTests
    {
        private const string LazyGet = "get DeliveredGetLazySingleton.Instance";
        private const string TickedSet = "set DeliveredGetTarget.coins 7";

        private readonly List<string> _roots = new();

        [SetUp]
        public void SetUp()
        {
            LeverGateBridge.ForgetRefused();
            Levers.ForgetRowsForTests();
            DeliveredGetLazySingleton.Reset();
            DeliveredGetTarget.coins = 0;
            DeliveredGetTarget.muted = false;
            Time.timeScale = 1f;
        }

        [TearDown]
        public void TearDown()
        {
            AdDirector.Active?.Finish("test teardown");
            DeliveredGetLazySingleton.Reset();
            foreach (var r in _roots)
                try { if (Directory.Exists(r)) Directory.Delete(r, true); } catch (IOException) { }
            _roots.Clear();
            Time.timeScale = 1f;
        }

        /// <summary>The real bridge, counted: every command that reaches it, in order.</summary>
        internal sealed class Counting : ICheatBridge
        {
            private readonly ICheatBridge _real;
            public readonly List<string> Calls = new();
            public Counting(ICheatBridge real) => _real = real;
            public bool Run(string command)
            {
                Calls.Add(command);
                return _real.Run(command);
            }
        }

        private sealed class NoState : IStateProbe
        {
            public string? CurrentStateName => null;
        }

        private sealed class NoVision : IVisionChannel
        {
            // Slice E.3's signature (the step's index), met in stack pass 4: this double came from A2′'s fourteenth fold.
            public string? Request(string prompt, int stepIndex) => null;
            public bool TryGetResult(string id, out bool match) { match = false; return false; }
        }

        private sealed class Driver : IRecorderDriver
        {
            public string OutputDir { get; }
            public bool IsRecording { get; private set; }
            public Driver(string dir) { OutputDir = dir; Directory.CreateDirectory(dir); }
            public int OutputWidth => 0;
            public int OutputHeight => 0;
            public void Start(string clipName) => IsRecording = true;
            public void Stop() => IsRecording = false;
        }

        private string Deliver(string shotsText, string adapterText)
        {
            var root = Path.Combine(Path.GetTempPath(), "delivered-get-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            _roots.Add(root);
            var r = SyncNova.Run(root, new SyncNovaFiles(shotsText, adapterText,
                SyncNova.Sha256OfText(shotsText), SyncNova.Sha256OfText(adapterText)), "run-1", "g");
            Assert.IsNull(r.Refusal, "the delivery was refused: " + r.Refusal);
            Assert.IsEmpty(r.ShotLoadErrors, string.Join(" | ", r.ShotLoadErrors));
            Assert.IsTrue(Levers.GateActive(root), "a delivered project is gated");
            return root;
        }

        private static string OneShot(params string[] setup) =>
            "{\"$schemaVersion\":1,\"shots\":[{\"name\":\"a\",\"setup\":[" +
            string.Join(",", setup.Select(s => Newtonsoft.Json.JsonConvert.ToString(s))) +
            "],\"steps\":[{\"kind\":\"wait\",\"seconds\":0.2}],\"settle\":{\"kind\":\"absent\",\"name\":\"Never\"}}]}";

        /// <summary>The default adapter's shape (<c>DefaultAdapterBoot.BuildDefault</c>) with the bridge counted: the real
        /// <see cref="GenericCheatBridge"/>, the real <see cref="UguiDriver"/>, and the kit's own ready gate reading this
        /// project's adapter.json.</summary>
        private static (GameAdapter Game, Counting Bridge) DefaultAdapter(string root)
        {
            var ui = new UguiDriver();
            var bridge = new Counting(new GenericCheatBridge(root, ui));
            return (new GameAdapter
            {
                GameId = "g", IsDefaultAdapter = true, Ui = ui, StateProbe = new NoState(), CheatBridge = bridge,
                ReadyGate = new HygieneReadyGate(() => HygieneSpec.Load(root), () => Time.timeScale,
                    v => Time.timeScale = v, () => DefaultAdapterBoot.ReadLastProbe(root)),
                Recovery = NullRecoveryPolicy.Instance,
            }, bridge);
        }

        private static AdDirector RunToTheEnd(GameAdapter game, string root)
        {
            var loaded = JsonShotLoader.LoadFromPath(RelayPaths.NovaShotsFile(root));
            Assert.IsEmpty(loaded.Errors, string.Join(" | ", loaded.Errors));
            var clock = 0.0;
            var d = AdDirector.Run(game, loaded.Shots, new AdDirector.Options
            {
                AutoPump = false, MaxAttempts = 1, Now = () => clock, Recorder = new Driver(Path.Combine(root, "rec")),
                Vision = new NoVision(), ProjectRoot = root, ReleaseCameraHold = () => { },
            });
            Assert.IsNotNull(d);
            for (var i = 0; i < 100_000 && !d!.IsFinished; i++)
            {
                d.PumpOnce();
                clock += 0.05;
            }
            Assert.IsTrue(d!.IsFinished, "the director did not finish");
            return d;
        }

        /// <summary>
        /// RULING 1, THE POSITIVE CONTROL (invariant 50). A delivered shot whose setup is `get` of the lazy singleton,
        /// levers.json empty but for an ordinary ticked write, the gate live: the getter must NOT run. On a37ebd5c it ran —
        /// `Levers.IsReadOnly` waved every `get …` through <c>LeverGateBridge.Run</c> — and the object existed afterwards:
        /// an un-ticked write into the studio's scene.
        /// </summary>
        [Test]
        public void AnUntickedDeliveredGetRunsNoGetter_ItIsRefusedByName()
        {
            var root = Deliver(OneShot(LazyGet, TickedSet), "{ \"gameId\": \"g\" }");
            Levers.SetApproved(root, TickedSet, true);
            DeliveredGetLazySingleton.Armed = true;
            var (game, bridge) = DefaultAdapter(root);
            var d = RunToTheEnd(game, root);

            Assert.AreEqual(7, DeliveredGetTarget.coins, "control: the shot ran, and its ticked write reached the game:\n" + d.Summary);
            Assert.IsNull(DeliveredGetLazySingleton.Made,
                "the un-ticked delivered `get` ran the getter: '" + DeliveredGetLazySingleton.MarkerName + "' exists in the scene");
            CollectionAssert.DoesNotContain(bridge.Calls, LazyGet, "the un-ticked `get` reached the inner bridge");
            StringAssert.Contains(Levers.NotApprovedLog(LazyGet), d.Summary, "refused, and by name");
            CollectionAssert.Contains(bridge.Calls, "show-ui", "the director's own show-ui still runs with no tick (ruling 4)");
        }

        /// <summary>CONTROL for the positive control: the mechanism is real. Ticked, the same `get` reaches the game, and the
        /// getter makes its object — which is exactly why the tick is needed.</summary>
        [Test]
        public void ATickedDeliveredGetRunsItsGetter()
        {
            var root = Deliver(OneShot(LazyGet, TickedSet), "{ \"gameId\": \"g\" }");
            Levers.SetApproved(root, TickedSet, true);
            Assert.IsNull(Levers.SetApproved(root, LazyGet, true), "a `get` can be ticked");
            DeliveredGetLazySingleton.Armed = true;
            var (game, bridge) = DefaultAdapter(root);
            var d = RunToTheEnd(game, root);

            Assert.AreEqual(7, DeliveredGetTarget.coins, d.Summary);
            Assert.IsNotNull(DeliveredGetLazySingleton.Made, "the ticked `get` ran the getter:\n" + d.Summary);
            CollectionAssert.Contains(bridge.Calls, LazyGet);
            StringAssert.DoesNotContain("not approved", d.Summary);
        }

        /// <summary>
        /// RULING 3 — `ready.muteGet` IS A LEVER, and through the SAME positive control: a delivered adapter.json whose
        /// ready block reads the lazy singleton, nothing ticked. The ready gate sends `get &lt;muteGet&gt;` through the gate
        /// (HygieneReadyGate), so un-ticked it is refused by name, the getter never runs, and the gate's verdict is NOT a
        /// reading: it says the read was not ticked, never "unmuted". On a37ebd5c the getter ran before any shot.
        /// </summary>
        [Test]
        public void AnUntickedMuteGetIsRefusedByNameAndTheReadyGateSaysNothingWasRead()
        {
            var root = Deliver(OneShot(TickedSet), "{ \"gameId\": \"g\", \"ready\": { \"muteGet\": \"DeliveredGetLazySingleton.Instance\" } }");
            Levers.SetApproved(root, TickedSet, true);
            DeliveredGetLazySingleton.Armed = true;
            var (game, bridge) = DefaultAdapter(root);
            var d = RunToTheEnd(game, root);

            Assert.IsNull(DeliveredGetLazySingleton.Made, "the un-ticked muteGet ran the getter:\n" + d.Summary);
            CollectionAssert.Contains(Levers.Needed(root).ToArray(), LazyGet, "the window lists the ready block's read as a lever");
            CollectionAssert.DoesNotContain(bridge.Calls, LazyGet, "the un-ticked muteGet reached the inner bridge");
            StringAssert.Contains(Levers.NotApprovedLog(LazyGet), d.Summary, "refused, and by name");
            StringAssert.Contains("ready: muteGet was not read — 'get DeliveredGetLazySingleton.Instance' is not ticked on this machine",
                d.Summary, "the ready gate says why it failed");
            StringAssert.DoesNotContain("unmuted", d.Summary, "no reading was taken, so there is no verdict on the bed");
            StringAssert.Contains("ABORT: ready gate never satisfied", d.Summary);
            Assert.AreEqual(0, DeliveredGetTarget.coins, "no shot ran after a ready gate that failed");
            CollectionAssert.Contains(bridge.Calls, "ui-dump", "the director's own ui-dump after a doubly failed gate still runs (ruling 4)");
        }

        /// <summary>A recovery that sends one un-ticked write through the director's gate — so the gate has just refused
        /// something when the ready gate asks again.</summary>
        private sealed class RefusedRecovery : IRecoveryPolicy
        {
            public System.Collections.IEnumerable Recover(DirectorContext ctx)
            {
                ctx.Cheats.Run("set DeliveredGetTarget.coins 99");
                yield break;
            }
        }

        /// <summary>
        /// THE FIFTEENTH AUDIT, M2 — THE OTHER DIRECTION. A TICKED muteGet the game cannot read (no such member) says "could
        /// not read", never "not ticked" — on both of the gate's tries, although between them the recovery had a write
        /// refused by the same gate (so a "last run refused" flag that is not reset by the next run would say "not
        /// ticked" on the second try). The twin above pins "not ticked" for the un-ticked read.
        /// </summary>
        [Test]
        public void ATickedMuteGetTheGameCannotReadSaysCouldNotRead_NotNotTicked()
        {
            const string read = "get DeliveredGetTarget.nope";
            var root = Deliver(OneShot(TickedSet), "{ \"gameId\": \"g\", \"ready\": { \"muteGet\": \"DeliveredGetTarget.nope\" } }");
            Levers.SetApproved(root, TickedSet, true);
            Assert.IsNull(Levers.SetApproved(root, read, true), "the muteGet is ticked");
            var (game, bridge) = DefaultAdapter(root);
            game.Recovery = new RefusedRecovery();
            var d = RunToTheEnd(game, root);

            Assert.AreEqual(2, bridge.Calls.Count(c => c == read), "control: the ticked read reached the game on both tries:\n" + d.Summary);
            StringAssert.Contains(Levers.NotApprovedLog("set DeliveredGetTarget.coins 99"), d.Summary, "control: the recovery's write was refused between the tries");
            var couldNot = d.Summary.Split('\n').Count(l => l.Contains("ready: could not read muteGet (DeliveredGetTarget.nope)"));
            Assert.AreEqual(2, couldNot, "each failed read says it could not read:\n" + d.Summary);
            StringAssert.DoesNotContain("is not ticked on this machine", d.Summary, "a ticked read is never reported as not ticked");
            StringAssert.Contains("ABORT: ready gate never satisfied", d.Summary);
        }

        /// <summary>The same with a plain bool: un-ticked, refused and not read (on a37ebd5c it was read, and the gate passed
        /// with nothing ticked); ticked, it is read, and the reading is the verdict.</summary>
        [Test]
        public void AMuteGetReadsOnlyWhenTicked()
        {
            const string adapter = "{ \"gameId\": \"g\", \"ready\": { \"muteGet\": \"DeliveredGetTarget.muted\" } }";
            DeliveredGetTarget.muted = true;

            var untickedRoot = Deliver(OneShot(TickedSet), adapter);
            Levers.SetApproved(untickedRoot, TickedSet, true);
            var (game, bridge) = DefaultAdapter(untickedRoot);
            var unticked = RunToTheEnd(game, untickedRoot);
            CollectionAssert.DoesNotContain(bridge.Calls, "get DeliveredGetTarget.muted", unticked.Summary);
            StringAssert.Contains(Levers.NotApprovedLog("get DeliveredGetTarget.muted"), unticked.Summary);
            StringAssert.Contains("ABORT: ready gate never satisfied", unticked.Summary);

            var tickedRoot = Deliver(OneShot(TickedSet), adapter);
            Levers.SetApproved(tickedRoot, TickedSet, true);
            Assert.IsNull(Levers.SetApproved(tickedRoot, "get DeliveredGetTarget.muted", true));
            var (game2, bridge2) = DefaultAdapter(tickedRoot);
            var ticked = RunToTheEnd(game2, tickedRoot);
            CollectionAssert.Contains(bridge2.Calls, "get DeliveredGetTarget.muted", ticked.Summary);
            StringAssert.DoesNotContain("not approved", ticked.Summary);
            Assert.AreEqual(7, DeliveredGetTarget.coins, "control: the ready gate passed and the shot ran:\n" + ticked.Summary);
        }
    }
}
