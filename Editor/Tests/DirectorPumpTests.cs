using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using UnityEngine;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// The thirteenth audit (2026-09-22) — A DIRECTOR PUMP DOES ONE UNIT OF WORK. Rounds 7 to 13 each found another way to
    /// freeze the editor inside ONE pump of the director, each a loop nobody had bounded, each fixed one shape at a time.
    /// These tests pin the property instead: they run the director the way the editor does — one <c>PumpOnce</c> per
    /// editor tick, on the editor's own clock, over a project the cloud delivered to through the real
    /// <see cref="SyncNova.Run"/> — and time every pump. A failure here means a loop that does more than one unit of work
    /// between two yields.
    ///
    /// The fourteenth audit (2026-09-22) showed what the thirteenth fold's version could not see: it ran a stub bridge, so
    /// no command's OWN cost was in it, and a ticked-or-free command whose cost is reflection into the game (a 255-character
    /// `get`, 6 s in one pump; `ui-dump` at 300 Selectables, 150 ms) passed it. So the property test now runs the REAL
    /// <see cref="GenericCheatBridge"/>, counts what each pump does (bridge calls, gate questions) as its noise-free half,
    /// and runs on a fake clock, so its wall bound is taken over about 1,200 pumps of real work instead of millions of idle
    /// ones. The bridge's own verbs have their own pinned bounds below.
    /// </summary>
    public class DirectorPumpTests
    {
        /// <summary>The longest one pump may take. A frame at 10 fps; the audits' stalls were 1–22 s.</summary>
        private const double MaxPumpMs = 100;

        /// <summary>The UI the worst shot is evaluated against: the auditor's large synthetic canvas (round 13, S2).</summary>
        private const int CanvasNodes = 3000;

        private readonly List<string> _roots = new();
        private readonly List<GameObject> _objects = new();

        [SetUp]
        public void SetUp()
        {
            LeverGateBridge.ForgetRefused();
            Levers.ForgetRowsForTests();
        }

        [TearDown]
        public void TearDown()
        {
            AdDirector.Active?.Finish("test teardown");
            foreach (var o in _objects)
                if (o != null) UnityEngine.Object.DestroyImmediate(o);
            _objects.Clear();
            foreach (var r in _roots)
                try { if (Directory.Exists(r)) Directory.Delete(r, true); } catch (IOException) { }
            _roots.Clear();
            Time.timeScale = 1f;
        }

        /// <summary>The game's own bridge, under the gate: what reaches it, and how many times the gate had asked whether it
        /// is live by then.</summary>
        private sealed class Inner : ICheatBridge
        {
            public readonly List<(string Command, int GateActiveCalls)> Calls = new();
            public bool Run(string command)
            {
                Calls.Add((command, Levers.GateActiveCallsForTests));
                return true;
            }
        }

        private sealed class NoState : IStateProbe
        {
            public string? CurrentStateName => null;
        }

        private sealed class NoVision : IVisionChannel
        {
            // Slice E.3's signature (the step's index), met in stack pass 3: this double came from A2′'s side.
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

        private sealed class NoUi : IUiDriver
        {
            public bool Exists(string name) => false;
            public bool IsInteractable(string name) => false;
            public string? GetText(string name) => null;
            public bool Click(string name, int index = 0) => false;
            public void PointerDown(string name, int index = 0) { }
            public void PointerUp(string name, int index = 0) { }
        }

        /// <summary>Deliver <paramref name="shotsText"/> into a fresh temp project through the production delivery, which
        /// is the loader: refused content never reaches a director. Returns the project root, gated.</summary>
        private string Deliver(string shotsText)
        {
            var root = Path.Combine(Path.GetTempPath(), "director-pump-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            _roots.Add(root);
            const string adapter = "{ \"gameId\": \"g\" }";
            var r = SyncNova.Run(root, new SyncNovaFiles(shotsText, adapter,
                SyncNova.Sha256OfText(shotsText), SyncNova.Sha256OfText(adapter)), "run-1", "g");
            Assert.IsNull(r.Refusal, "the delivery was refused: " + r.Refusal);
            Assert.IsEmpty(r.ShotLoadErrors, "the delivered file does not load: " + string.Join(" | ", r.ShotLoadErrors));
            Assert.IsTrue(Levers.GateActive(root), "a delivered project is gated");
            return root;
        }

        private static AdShot Loaded(string root, string name)
        {
            var loaded = JsonShotLoader.LoadFromPath(RelayPaths.NovaShotsFile(root));
            Assert.IsEmpty(loaded.Errors, string.Join(" | ", loaded.Errors));
            return loaded.Shots.Single(s => s.Name == name);
        }

        private static string Document(IEnumerable<string> shots) =>
            "{\"$schemaVersion\":1,\"shots\":[" + string.Join(",", shots) + "]}";

        /// <summary>Pad a document with loadable shots to just under the delivery's byte cap, so every hash of it costs
        /// what the largest delivered file costs.</summary>
        private static string PaddedToTheCap(string mainShot)
        {
            var shots = new List<string> { mainShot };
            var note = new string('p', 4000);
            for (var i = 0; ; i++)
            {
                var pad = "{\"name\":\"pad-" + i + "\",\"note\":\"" + note + "\",\"steps\":[{\"kind\":\"wait\",\"seconds\":1}]," +
                          "\"settle\":{\"kind\":\"present\",\"name\":\"X\"}}";
                var next = Document(shots.Concat(new[] { pad }));
                if (Encoding.UTF8.GetByteCount(next) > SyncNova.MaxShotsBytes - 256) break;
                shots.Add(pad);
            }
            return Document(shots);
        }

        /// <summary>Run <paramref name="shot"/> to its end, one PumpOnce per editor tick, timing every pump.</summary>
        private static (AdDirector Director, double LongestMs, int LongestAt, int Pumps) RunTimed(GameAdapter game, AdShot shot,
            string root, int maxAttempts = 1, Func<double>? now = null, Action<int>? afterPump = null, AdShot? preamble = null)
        {
            var d = AdDirector.Run(game, new[] { shot }, new AdDirector.Options
            {
                AutoPump = false, MaxAttempts = maxAttempts, Now = now, Recorder = new Driver(Path.Combine(root, "rec")),
                Vision = new NoVision(), ProjectRoot = root, ReleaseCameraHold = () => { },
                // slice E.4 — a run's tutorial gate, run first (stack pass 3: the preamble under the same property)
                Preamble = preamble,
            });
            Assert.IsNotNull(d);
            var wall = Stopwatch.StartNew();
            double longest = 0;
            var longestAt = 0;
            var pumps = 0;
            while (!d!.IsFinished && wall.Elapsed.TotalSeconds < 120)
            {
                var t0 = wall.Elapsed.TotalMilliseconds;
                d.PumpOnce();
                var ms = wall.Elapsed.TotalMilliseconds - t0;
                pumps++;
                if (ms > longest) { longest = ms; longestAt = pumps; }
                afterPump?.Invoke(pumps);
            }
            Assert.IsTrue(d.IsFinished, $"the director did not finish in 120 s ({pumps} pumps)");
            return (d, longest, longestAt, pumps);
        }

        /// <summary>A canvas of <paramref name="nodes"/> active objects, each with a uGUI Image, ten to a branch (the auditor's
        /// shape), every <paramref name="buttonEvery"/>-th a Button (a Selectable), plus the object the worst shot clicks.
        /// The uGUI types are reached by name: this test assembly does not reference UnityEngine.UI.</summary>
        private void BuildCanvasWithSelectables(int nodes, int buttonEvery)
        {
            var imageType = Type.GetType("UnityEngine.UI.Image, UnityEngine.UI", true)!;
            var buttonType = Type.GetType("UnityEngine.UI.Button, UnityEngine.UI", true)!;
            var canvas = new GameObject("PumpCanvas", typeof(RectTransform), typeof(Canvas));
            _objects.Add(canvas);
            var parent = canvas.transform;
            for (var i = 0; i < nodes; i++)
            {
                var g = new GameObject("n" + i, typeof(RectTransform), imageType);
                if (buttonEvery > 0 && i % buttonEvery == 0) g.AddComponent(buttonType);
                g.transform.SetParent(i % 10 == 0 ? canvas.transform : parent, false);
                if (i % 10 == 0) parent = g.transform;
            }
            new GameObject("Btn", typeof(RectTransform)).transform.SetParent(canvas.transform, false);
        }

        /// <summary>The number of Selectables the reference canvas holds, and every how many nodes one sits.</summary>
        private const int ReferenceSelectables = 300;
        private const int ButtonEvery = CanvasNodes / ReferenceSelectables;

        /// <summary>A dotted path of exactly <paramref name="length"/> characters, <paramref name="head"/> then one-letter
        /// segments: no leading part of it names a type, so every prefix is a lookup that finds nothing — the auditor's
        /// `get zqN.a.a.…` shape.</summary>
        internal static string DottedPath(string head, int length)
        {
            var sb = new StringBuilder(head);
            while (sb.Length + 2 <= length) sb.Append(".a");
            if (sb.Length < length) sb.Append('b');
            Assert.AreEqual(length, sb.Length);
            return sb.ToString();
        }

        /// <summary>A path head no other test (or run) has asked about, so nothing cached can hide a lookup's cost.</summary>
        internal static string FreshHead() => "zq" + Guid.NewGuid().ToString("N");

        /// <summary>The real bridge under the gate, counted: every command that reaches it, in order.</summary>
        private sealed class CountingReal : ICheatBridge
        {
            private readonly ICheatBridge _real;
            public readonly List<string> Calls = new();
            public CountingReal(ICheatBridge real) => _real = real;
            public bool Run(string command)
            {
                Calls.Add(command);
                return _real.Run(command);
            }
        }

        private static (GameAdapter Game, CountingReal Bridge) RealBridgeGame(string root)
        {
            var ui = new UguiDriver();
            var bridge = new CountingReal(new GenericCheatBridge(root, ui));
            return (new GameAdapter { GameId = "g", Ui = ui, StateProbe = new NoState(), CheatBridge = bridge,
                ReadyGate = NullReadyGate.Instance, Recovery = NullRecoveryPolicy.Instance }, bridge);
        }

        /// <summary>
        /// THE PROPERTY, AT THE LOADER'S CAPS, WITH THE REAL BRIDGE (the fourteenth audit, M3). A shot at every cap the loader
        /// has — 500 setup commands, 500 steps (in turn `timeScale`, ticked; an already-met `waitFor`; a click with no
        /// `until`), one `waitFor` and the settle each a 64-part `all` of absent names — padded to the delivery's byte cap,
        /// delivered through <see cref="SyncNova.Run"/>, and run by the real director over the real
        /// <see cref="UguiDriver"/> and the real <see cref="GenericCheatBridge"/> (under the gate) on a 3,000-node canvas
        /// holding 300 Selectables. Its setup holds the two commands the fourteenth audit measured through the bridge: a
        /// TICKED 255-character `get` whose every leading part is a type lookup that finds nothing (6.0 s in one pump before
        /// this fold), and a delivered `ui-dump`, which needs no tick (150 ms). The rest are ticked `set`s of a real field.
        ///
        /// This is NOT "the worst shot the loader accepts" (the thirteenth fold's words, false): a command's own cost is
        /// whatever the studio's code costs — a getter, a method — and no shot the loader accepts bounds it. What this pins
        /// is the director's half: each pump makes at most TWO bridge calls and asks the gate at most TWICE (pump 1 is
        /// RunAll's `show-ui` and the attempt's first setup command, and the ready region's question beside the attempt's
        /// own) — counts, so machine noise cannot move them — and, as the backstop, no pump over 100 ms of wall time on a
        /// fake clock advanced 0.05 s a pump, about 1,200 pumps of real work. The counters are asserted first: the
        /// thirteenth fold's P1 mutant (no setup yield, nothing held) is red through them alone (501 calls in pump 1).
        /// </summary>
        [Test]
        public void EachPumpOfTheShotAtTheLoadersCapsMakesAtMostTwoBridgeCallsAndTakesUnder100Milliseconds()
        {
            BuildCanvasWithSelectables(CanvasNodes, ButtonEvery);
            const string set = "set DeliveredGetTarget.coins 5";

            // warm-up: the same path, on a small delivered shot, so the JIT is not in the measurement — with a DIFFERENT
            // `get` path from the measured one, so nothing a lookup cached can hide the measured one's cost
            var warmGet = "get " + DottedPath(FreshHead(), 40);
            var warm = Deliver(Document(new[] { "{\"name\":\"warm\",\"setup\":[\"" + set + "\",\"" + warmGet + "\",\"ui-dump\"]," +
                "\"steps\":[{\"kind\":\"timeScale\",\"factor\":1},{\"kind\":\"waitFor\",\"condition\":{\"kind\":\"absent\",\"name\":\"Nowhere\"}}," +
                "{\"kind\":\"click\",\"name\":\"Btn\"}],\"settle\":{\"kind\":\"absent\",\"name\":\"Nowhere\"}}" }));
            Levers.SetApproved(warm, set, true);
            Levers.SetApproved(warm, warmGet, true);
            Levers.SetApproved(warm, Levers.TimeScaleLever, true);
            var (warmGame, _) = RealBridgeGame(warm);
            RunTimed(warmGame, Loaded(warm, "warm"), warm, now: FakeClock(out var warmTick), afterPump: _ => warmTick());

            var longGet = "get " + DottedPath(FreshHead(), Levers.MaxLeverLength - 4 - 1); // 255 characters
            Assert.AreEqual(Levers.MaxLeverLength - 1, longGet.Length);
            var atCap = "{\"kind\":\"all\",\"parts\":[" + string.Join(",", Enumerable.Range(0, JsonShotLoader.MaxConditionParts)
                .Select(i => "{\"kind\":\"absent\",\"name\":\"Nowhere" + i + "\"}")) + "]}";
            var steps = new List<string>();
            for (var i = 0; i < JsonShotLoader.MaxSteps; i++)
                steps.Add((i % 3) switch
                {
                    0 => "{\"kind\":\"timeScale\",\"factor\":1}",
                    1 => "{\"kind\":\"waitFor\",\"condition\":" + (i == 1 ? atCap : "{\"kind\":\"absent\",\"name\":\"Nowhere\"}") + "}",
                    _ => "{\"kind\":\"click\",\"name\":\"Btn\"}",
                });
            var setup = new List<string> { "\"" + longGet + "\"", "\"ui-dump\"" };
            setup.AddRange(Enumerable.Repeat("\"" + set + "\"", JsonShotLoader.MaxSetup - 2));
            var worst = "{\"name\":\"worst\",\"setup\":[" + string.Join(",", setup) +
                        "],\"steps\":[" + string.Join(",", steps) + "],\"settle\":" + atCap + "}";
            var text = PaddedToTheCap(worst);
            var root = Deliver(text);
            Assert.Greater(new FileInfo(RelayPaths.NovaShotsFile(root)).Length, SyncNova.MaxShotsBytes - 8192,
                "the delivered file is at the byte cap, so each hash of it is the dearest one");
            Levers.SetApproved(root, set, true);
            Assert.IsNull(Levers.SetApproved(root, longGet, true), "a 255-character `get` can be ticked");
            Levers.SetApproved(root, Levers.TimeScaleLever, true);
            var shot = Loaded(root, "worst");
            Assert.AreEqual(JsonShotLoader.MaxSetup, shot.Setup.Count);
            Assert.AreEqual(JsonShotLoader.MaxSteps, shot.Steps.Count);

            DeliveredGetTarget.coins = 0;
            var (game, bridge) = RealBridgeGame(root);
            var callsBefore = 0;
            var gateBefore = Levers.GateActiveCallsForTests;
            var maxCalls = 0; var maxCallsAt = 0; var maxGate = 0; var maxGateAt = 0;
            var (d, longest, at, pumps) = RunTimed(game, shot, root, now: FakeClock(out var tick), afterPump: pump =>
            {
                tick();
                var calls = bridge.Calls.Count - callsBefore;
                callsBefore = bridge.Calls.Count;
                if (calls > maxCalls) { maxCalls = calls; maxCallsAt = pump; }
                var gate = Levers.GateActiveCallsForTests - gateBefore;
                gateBefore = Levers.GateActiveCallsForTests;
                if (gate > maxGate) { maxGate = gate; maxGateAt = pump; }
            });

            TestContext.Out.WriteLine($"the shot at the caps, real bridge: {pumps} pumps, the longest {longest:0.0} ms (pump {at}); " +
                                      $"at most {maxCalls} bridge calls (pump {maxCallsAt}) and {maxGate} gate questions (pump {maxGateAt}) " +
                                      $"in one pump; {new FileInfo(RelayPaths.NovaShotsFile(root)).Length} bytes delivered");
            // THE NOISE-FREE HALF, first: what one pump does, counted
            Assert.LessOrEqual(maxCalls, 2, $"pump {maxCallsAt} made {maxCalls} bridge calls — a loop ran more than one command between two yields");
            Assert.LessOrEqual(maxGate, 2, $"pump {maxGateAt} asked the gate {maxGate} times whether it is live");
            // POSITIVE CONTROLS: the whole shot ran through the real bridge — every write reached the game, the long `get`
            // and the `ui-dump` reached it, every step resolved, it was captured, nothing was refused
            Assert.AreEqual(JsonShotLoader.MaxSetup - 2, bridge.Calls.Count(c => c == set), d.Summary);
            Assert.AreEqual(5, DeliveredGetTarget.coins, "the ticked `set` wrote the game's field");
            Assert.AreEqual(1, bridge.Calls.Count(c => c == longGet), "the ticked 255-character `get` reached the bridge");
            Assert.AreEqual(1, bridge.Calls.Count(c => c == "ui-dump"), "the delivered `ui-dump` reached the bridge");
            Assert.IsTrue(d.AllCaptured, "the shot was not captured:\n" + d.Summary);
            Assert.AreEqual(JsonShotLoader.MaxSteps + 1, d.StepMarks.Count, "every step and the settle");
            StringAssert.DoesNotContain("not approved", d.Summary);
            Assert.Greater(pumps, 1000, "the fake clock ran the shot's waits as pumps of real work");
            // THE BACKSTOP: wall time
            Assert.Less(longest, MaxPumpMs,
                $"pump {at} of {pumps} took {longest:0} ms — one unit of work cost more than a frame");
        }

        /// <summary>A clock the test moves: 0.05 s per pump, the step <c>TheGateIsAsked…</c> uses.</summary>
        private static Func<double> FakeClock(out Action tick)
        {
            var clock = new double[] { 0 };
            tick = () => clock[0] += 0.05;
            return () => clock[0];
        }

        /// <summary>
        /// RULING 5 — A TICKED LEVER-LENGTH `get` OR `set` IS ONE CHEAP PUMP. Every leading part of the dotted path is a type
        /// lookup (<c>GameReflection.TryResolveOwner</c>), and before this fold each one that missed scanned every type in
        /// every loaded assembly: the fourteenth audit measured 46.7 ms a scan and 6.0 s for one 255-character `get` in one
        /// pump. <c>GameReflection.FindType</c> now answers from a name index built once per domain, so the cost of a ticked
        /// command's path is a dictionary lookup a part. The real bridge, under the gate, on a delivered project; a warm-up
        /// on a different path first.
        /// </summary>
        [Test]
        public void ATickedLeverLengthGetOrSetRunsInOnePumpUnder100Milliseconds()
        {
            var warmGet = "get " + DottedPath(FreshHead(), 40);
            var warmSet = "set " + DottedPath(FreshHead(), 40) + " 1";
            var longGet = "get " + DottedPath(FreshHead(), Levers.MaxLeverLength - 4 - 1);
            var longSet = "set " + DottedPath(FreshHead(), Levers.MaxLeverLength - 4 - 2 - 1) + " 1";
            Assert.AreEqual(255, longGet.Length);
            Assert.AreEqual(255, longSet.Length);
            var shot = "{\"name\":\"long\",\"setup\":[\"" + warmGet + "\",\"" + warmSet + "\",\"" + longGet + "\",\"" + longSet + "\"]," +
                       "\"steps\":[{\"kind\":\"wait\",\"seconds\":0.2}],\"settle\":{\"kind\":\"absent\",\"name\":\"Never\"}}";
            var root = Deliver(Document(new[] { shot }));
            foreach (var lever in new[] { warmGet, warmSet, longGet, longSet })
                Assert.IsNull(Levers.SetApproved(root, lever, true), lever);
            var (game, bridge) = RealBridgeGame(root);
            var pumpOf = new Dictionary<string, int>();
            var times = new List<double>();
            var wall = Stopwatch.StartNew();
            var d = AdDirector.Run(game, new[] { Loaded(root, "long") }, new AdDirector.Options
            {
                AutoPump = false, MaxAttempts = 1, Now = FakeClock(out var tick), Recorder = new Driver(Path.Combine(root, "rec")),
                Vision = new NoVision(), ProjectRoot = root, ReleaseCameraHold = () => { },
            });
            while (!d!.IsFinished && times.Count < 10_000)
            {
                var before = bridge.Calls.Count;
                var t0 = wall.Elapsed.TotalMilliseconds;
                d.PumpOnce();
                times.Add(wall.Elapsed.TotalMilliseconds - t0);
                tick();
                for (var i = before; i < bridge.Calls.Count; i++) pumpOf[bridge.Calls[i]] = times.Count - 1;
            }
            Assert.IsTrue(d.IsFinished);
            foreach (var lever in new[] { longGet, longSet })
            {
                Assert.IsTrue(pumpOf.ContainsKey(lever), "the ticked command reached the real bridge:\n" + d.Summary);
                var ms = times[pumpOf[lever]];
                TestContext.Out.WriteLine($"{lever.Substring(0, 3)} of {lever.Length} characters: its pump took {ms:0.0} ms");
                Assert.Less(ms, MaxPumpMs, $"the ticked {lever.Length}-character `{lever.Substring(0, 3)}` took {ms:0} ms in one pump");
            }
            StringAssert.DoesNotContain("not approved", d.Summary);
        }

        /// <summary>
        /// RULING 5 AND THE ROUND-15 CONDITION — EACH OF THE KIT'S THREE FREE VERBS IS ONE CHEAP PUMP AT THE REFERENCE SIZE.
        /// The free list is exactly `ui-dump`, `show-ui` and a bare `hide-ui` (<see cref="Levers.IsReadOnly"/>), and each is
        /// delivered here with no tick, in its own pump, on the real bridge over the reference canvas (3,000 nodes, 300
        /// Selectables). `ui-dump` was the costly one — the fourteenth audit's S2: for every candidate the dump walked every
        /// active canvas again to number it (<c>IndexSuffix</c> → <c>UguiDriver.MatchesInClickOrder</c>), 0.5 ms a
        /// Selectable, 150 ms at 300; now one walk per dump indexes every name in click order. `show-ui` restores what a
        /// ticked `hide-ui Btn` hid just before it (one object), and a bare `hide-ui` names nothing and fails in the bridge.
        /// Three dumps.
        ///
        /// THE FIFTEENTH AUDIT, S1 — ORDER-INDEPENDENT. This pin passed in the full suite only because earlier tests had
        /// built <see cref="GameReflection"/>'s name index; run ALONE in a fresh domain (a <c>-testFilter</c> run of it), the
        /// first dump built the index inside its pump and then rebuilt it, because the build's own scan loaded 5 assemblies
        /// (206 ms on ea016964; the auditor's 220–270). Now the editor builds it on a delayCall, outside any pump, and a load
        /// appends: so it asserts, first, that NO full index build happens inside any pump — a count, which noise cannot move.
        /// </summary>
        [Test]
        public void EachOfTheThreeFreeVerbsOverTheReferenceCanvasTakesUnder100MillisecondsInItsPump()
        {
            BuildCanvasWithSelectables(CanvasNodes, ButtonEvery);
            var setup = new[] { "hide-ui Btn", "show-ui", "hide-ui", "ui-dump", "ui-dump", "ui-dump" };
            var shot = "{\"name\":\"free\",\"setup\":[" + string.Join(",", setup.Select(c => "\"" + c + "\"")) + "]," +
                       "\"steps\":[{\"kind\":\"wait\",\"seconds\":0.2}],\"settle\":{\"kind\":\"absent\",\"name\":\"Never\"}}";
            var root = Deliver(Document(new[] { shot }));
            Assert.IsNull(Levers.SetApproved(root, "hide-ui Btn", true), "hide-ui with an argument is a lever: ticked here");
            var (game, bridge) = RealBridgeGame(root);
            var pumpMs = new List<double>();
            var callPump = new List<int>(); // the pump each bridge call ran in, by call index
            var buildsBefore = GameReflection.FullBuildsForTests;
            var appendsBefore = GameReflection.AppendsForTests;
            var wall = Stopwatch.StartNew();
            var d = AdDirector.Run(game, new[] { Loaded(root, "free") }, new AdDirector.Options
            {
                AutoPump = false, MaxAttempts = 1, Now = FakeClock(out var tick), Recorder = new Driver(Path.Combine(root, "rec")),
                Vision = new NoVision(), ProjectRoot = root, ReleaseCameraHold = () => { },
            });
            while (!d!.IsFinished && pumpMs.Count < 10_000)
            {
                var t0 = wall.Elapsed.TotalMilliseconds;
                d.PumpOnce();
                pumpMs.Add(wall.Elapsed.TotalMilliseconds - t0);
                while (callPump.Count < bridge.Calls.Count) callPump.Add(pumpMs.Count - 1);
                tick();
            }
            Assert.IsTrue(d.IsFinished);
            TestContext.Out.WriteLine($"the name index: built first by {GameReflection.WarmedByForTests ?? "nothing"}, " +
                $"{GameReflection.FullBuildsForTests - buildsBefore} full build(s) and {GameReflection.AppendsForTests - appendsBefore} " +
                $"appended assembly(ies) inside the pumps; the last full build took {GameReflection.LastIndexBuildMsForTests:0.0} ms");
            Assert.AreEqual(buildsBefore, GameReflection.FullBuildsForTests,
                $"the name index was built inside a director pump (first built by {GameReflection.WarmedByForTests ?? "nothing"}) — " +
                "it must be built once per domain, on the editor's delayCall, outside any pump");
            // POSITIVE CONTROLS: RunAll's own show-ui, then the six setup commands in order, each in a pump of its own after
            // the first; nothing refused; the setup's show-ui restored what `hide-ui Btn` hid; the dump listed every Selectable
            CollectionAssert.AreEqual(new[] { "show-ui" }.Concat(setup).ToArray(), bridge.Calls.ToArray(), d.Summary);
            Assert.AreEqual(bridge.Calls.Count - 1, callPump.Skip(1).Distinct().Count(), "one setup command a pump");
            StringAssert.DoesNotContain("not approved", d.Summary);
            StringAssert.Contains("restored 1 object(s)", File.ReadAllText(Path.Combine(RelayPaths.Root(root), "probe", "show-ui.txt")));
            var listing = File.ReadAllText(Path.Combine(RelayPaths.Root(root), "probe", "ui-dump.txt"));
            Assert.AreEqual(ReferenceSelectables, listing.Split('\n').Count(l => l.Contains("[Button]")),
                "the dump listed every Selectable on the reference canvas");
            var lines = new List<string>();
            for (var i = 0; i < bridge.Calls.Count; i++)
            {
                var command = bridge.Calls[i];
                if (!Levers.IsReadOnly(command)) continue;
                var ms = pumpMs[callPump[i]];
                lines.Add($"{command} {ms:0.0}");
                Assert.Less(ms, MaxPumpMs, $"the free `{command}` (call {i}) took {ms:0} ms in its pump at {ReferenceSelectables} Selectables");
            }
            Assert.AreEqual(6, lines.Count, "RunAll's show-ui, the setup's show-ui, the bare hide-ui and three dumps were timed");
            TestContext.Out.WriteLine("the free verbs at 300 Selectables, 3,000 nodes (ms in their pump): " + string.Join(", ", lines));
        }

        /// <summary>
        /// RULING 5's GUARD — ONE WALK, THE SAME NUMBERS. The dump's index suffix must be the number `click name i` selects,
        /// so the one-walk index (<see cref="UguiDriver.ClickOrderIndex"/>) must list every selector's matches in exactly
        /// the order <see cref="UguiDriver.MatchesInClickOrder"/> does — duplicates included: a canvas nested in another is
        /// walked from both, so its objects are listed twice, and the old walk numbered them that way. A canvas of repeated
        /// names, repeated labels, a nested canvas and a name holding '@'; every object's name and name@label compared.
        /// </summary>
        [Test]
        public void TheOneWalkIndexListsEverySelectorsMatchesInTheClickOrder()
        {
            var textType = Type.GetType("UnityEngine.UI.Text, UnityEngine.UI", true)!;
            var buttonType = Type.GetType("UnityEngine.UI.Button, UnityEngine.UI", true)!;
            var root = new GameObject("EqCanvas", typeof(RectTransform), typeof(Canvas));
            _objects.Add(root);
            GameObject Add(Transform parent, string name, string? label, bool button)
            {
                var g = new GameObject(name, typeof(RectTransform));
                g.transform.SetParent(parent, false);
                if (button) g.AddComponent(buttonType);
                if (label != null)
                {
                    var t = new GameObject("Label", typeof(RectTransform));
                    t.transform.SetParent(g.transform, false);
                    textType.GetProperty("text")!.SetValue(t.AddComponent(textType), label);
                }
                return g;
            }
            Add(root.transform, "Button", "Level 1", true);
            Add(root.transform, "Button", "Spin", true);
            Add(root.transform, "button", "Spin", true);
            var inner = new GameObject("Inner", typeof(RectTransform), typeof(Canvas));
            inner.transform.SetParent(root.transform, false);
            Add(inner.transform, "Button", "Level 1", true);
            Add(inner.transform, "Toggle", null, false);
            Add(root.transform, "Toggle", null, false);
            Add(root.transform, "Odd@Name", "X", true);
            var hidden = Add(root.transform, "Button", "Hidden", true);
            hidden.SetActive(false);
            for (var i = 0; i < 30; i++) Add(i % 2 == 0 ? root.transform : inner.transform, "n" + (i % 7), i % 3 == 0 ? "L" + (i % 4) : null, i % 5 == 0);

            var index = UguiDriver.ClickOrderIndex.Build();
            var selectors = new HashSet<string>();
            foreach (var t in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsSortMode.None))
            {
                selectors.Add(t.gameObject.name);
                selectors.Add(t.gameObject.name.ToUpperInvariant());
                if (UguiDriver.LabelOf(t.gameObject) is { } label) selectors.Add(t.gameObject.name + "@" + label);
            }
            selectors.Add("Button@spin");
            selectors.Add("Nowhere");
            foreach (var selector in selectors)
                CollectionAssert.AreEqual(UguiDriver.MatchesInClickOrder(selector).ToArray(), index.Matches(selector).ToArray(),
                    $"'{selector}': the one-walk index lists different objects, or in a different order");
            Assert.AreEqual(5, index.Matches("Button").Count(g => g.transform.IsChildOf(root.transform)),
                "positive control: three on the outer canvas (case aside), the nested canvas's one twice, the hidden one never");
        }

        /// <summary>
        /// THE FOURTEENTH AUDIT, M1 — duplicate overlay names cost every enabled behaviour a scan of every name. adapter.json
        /// may list one name 7,276 times in 64 KB; with one tick of it, <c>OverlayHider.Hide</c> compared every enabled
        /// MonoBehaviour with every copy (131.7 ms in one pump at 6,000 behaviours, the auditor's run). It now looks each
        /// behaviour's type name up in a set of the names: one lookup a behaviour. And duplicates still hide a matching
        /// behaviour once, and restore it.
        /// </summary>
        [Test]
        public void HidingOverlaysIsOneLookupPerBehaviourWhateverTheDuplicates()
        {
            var imageType = Type.GetType("UnityEngine.UI.Image, UnityEngine.UI", true)!;
            var holder = new GameObject("OverlayHolder");
            _objects.Add(holder);
            var images = new List<Behaviour>();
            for (var i = 0; i < 6000; i++)
            {
                var g = new GameObject("o" + i, typeof(RectTransform), imageType);
                g.transform.SetParent(holder.transform, false);
                images.Add((Behaviour)g.GetComponent(imageType));
            }
            var names = Enumerable.Repeat("A", 7276).ToArray();
            OverlayHider.Hide(names).Restore(); // warm-up
            // COUNTED (the fifteenth audit, M4): name comparisons per behaviour judged, which noise cannot move. The set is
            // built from the 7,276 names (one hash and at most one comparison each), then each behaviour is one hash and at
            // most one comparison. A scan of every name per behaviour is 7,276 comparisons a behaviour — 43 million here.
            var judged0 = OverlayHider.BehavioursJudgedForTests;
            var compared0 = OverlayHider.NameComparisonsForTests;
            var sw = Stopwatch.StartNew();
            var none = OverlayHider.Hide(names);
            sw.Stop();
            none.Restore();
            var judged = OverlayHider.BehavioursJudgedForTests - judged0;
            var compared = OverlayHider.NameComparisonsForTests - compared0;
            TestContext.Out.WriteLine($"OverlayHider.Hide, 7,276 copies of one name over 6,000 behaviours: {sw.Elapsed.TotalMilliseconds:0.0} ms, " +
                                      $"{judged} behaviours judged, {compared} name comparisons");
            Assert.GreaterOrEqual(judged, 6000, "control: every enabled behaviour was judged");
            Assert.LessOrEqual(compared, 2 * names.Length + 2 * judged, "COUNTED: one lookup per behaviour, not one scan of every name");
            // the backstop, loose: the quadratic scan it replaced was 131.7 ms
            Assert.Less(sw.Elapsed.TotalMilliseconds, 100, "BACKSTOP (wall time): one lookup per behaviour, not one scan of every name");

            var hider = OverlayHider.Hide(Enumerable.Repeat(imageType.Name, 3).Concat(names).ToArray());
            Assert.IsTrue(images.All(b => !b.enabled), "every behaviour of a listed type is hidden, duplicates or not");
            hider.Restore();
            Assert.IsTrue(images.All(b => b.enabled), "and restored");
        }

        /// <summary>
        /// THE FOURTEENTH AUDIT, M2 — <c>Finish</c> logged the whole summary in one Debug.Log: "Run all shots" over a 512 KB
        /// file of refused setup lists builds about 500,000 lines. It now logs the summary's LAST 200 lines, says how many
        /// there were, and writes the whole summary to <c>Library/AdRelay/director-summary.txt</c> under this project's root.
        /// A summary of 200 lines or fewer is logged whole, as before.
        /// </summary>
        [Test]
        public void FinishLogsTheSummarysLast200LinesAndWritesTheWholeSummaryToAFile()
        {
            var shot = "{\"name\":\"loud\",\"setup\":[" + string.Join(",", Enumerable.Repeat("\"set Nope.x 1\"", 300)) + "]," +
                       "\"steps\":[{\"kind\":\"wait\",\"seconds\":0.2}],\"settle\":{\"kind\":\"absent\",\"name\":\"Never\"}}";
            var root = Deliver(Document(new[] { shot }));
            var game = new GameAdapter { GameId = "g", Ui = new NoUi(), StateProbe = new NoState(), CheatBridge = new Inner(),
                ReadyGate = NullReadyGate.Instance, Recovery = NullRecoveryPolicy.Instance };
            var logged = new List<string>();
            void OnLog(string message, string stack, LogType type)
            {
                if (message.StartsWith("[RecorderKit] complete", StringComparison.Ordinal)) logged.Add(message);
            }
            Application.logMessageReceived += OnLog;
            AdDirector d;
            try
            {
                (d, _, _, _) = RunTimed(game, Loaded(root, "loud"), root, now: FakeClock(out var tick), afterPump: _ => tick());
            }
            finally
            {
                Application.logMessageReceived -= OnLog;
            }
            var summaryLines = d.Summary.Split('\n');
            Assert.Greater(summaryLines.Length, 600, "positive control: 300 refused writes make a long summary");
            Assert.AreEqual(1, logged.Count, "Finish logged once");
            var consoleLines = logged[0].Split('\n');
            Assert.LessOrEqual(consoleLines.Length, 200 + 2, "the console gets the summary's tail, not all of it");
            StringAssert.EndsWith(summaryLines[^1], logged[0], "the tail ends where the summary ends");
            var file = Path.Combine(RelayPaths.Root(root), "director-summary.txt");
            Assert.IsTrue(File.Exists(file), "the whole summary is written beside the probe files");
            Assert.AreEqual(d.Summary, File.ReadAllText(file));
            StringAssert.Contains("Library/AdRelay/director-summary.txt", logged[0], "and the console says where");
            StringAssert.Contains($"{summaryLines.Length} lines", logged[0]);
        }

        private static string GatedShot(int n) =>
            "{\"name\":\"gated\",\"settleTimeoutSec\":0.2,\"setup\":[" +
            string.Join(",", Enumerable.Range(0, n).Select(i => i == n / 2 ? "\"set Player.gems 9\"" : "\"set Player.coins 5\"")) +
            "],\"steps\":[{\"kind\":\"timeScale\",\"factor\":1},{\"kind\":\"wait\",\"seconds\":0.2},{\"kind\":\"timeScale\",\"factor\":1}]," +
            "\"settle\":{\"kind\":\"present\",\"name\":\"Never\"}}";

        /// <summary>
        /// The thirteenth audit, S1 (ruling 2) — ONE HASH PER SHOT ATTEMPT, NOT PER WRITE. Whether the gate is live (synced.json
        /// read, the delivered files hashed) is asked once at the top of each attempt and held for that attempt; the gate's
        /// DECISION on each command is unchanged, read per command. Two attempts (the settle never holds), N gated setup
        /// commands with one un-ticked among them: every write in an attempt sees the same count, and the count moves by
        /// exactly one between attempts, and not at all over the attempt's ticked `timeScale` steps (a write that does not go
        /// through the bridge, asked under the same hold). The un-ticked command is refused in both.
        /// Counted around the attempts only: before them the ready gate's region asks per write as it always did — here
        /// that is RunAll's one `show-ui` (NullReadyGate writes nothing, and with no overlays OverlaysAllowed returns before
        /// asking).
        /// </summary>
        [TestCase(5)]
        [TestCase(500)]
        public void TheGateIsAskedWhetherItIsLiveOncePerAttemptNotOncePerWrite(int n)
        {
            var root = Deliver(Document(new[] { GatedShot(n) }));
            Levers.SetApproved(root, "set Player.coins 5", true);
            Levers.SetApproved(root, Levers.TimeScaleLever, true);
            var inner = new Inner();
            var game = new GameAdapter { GameId = "g", Ui = new NoUi(), StateProbe = new NoState(), CheatBridge = inner,
                ReadyGate = NullReadyGate.Instance, Recovery = NullRecoveryPolicy.Instance };
            var clock = new double[] { 0 };
            var before = Levers.GateActiveCallsForTests;
            var (d, _, _, _) = RunTimed(game, Loaded(root, "gated"), root, maxAttempts: 2, now: () => clock[0],
                afterPump: _ => clock[0] += 0.05);
            var after = Levers.GateActiveCallsForTests;

            var writes = inner.Calls.Where(c => c.Command == "set Player.coins 5").ToList();
            Assert.AreEqual(2 * (n - 1), writes.Count, "the ticked command reached the game in both attempts:\n" + d.Summary);
            Assert.IsFalse(inner.Calls.Any(c => c.Command == "set Player.gems 9"), "the un-ticked command was refused");
            Assert.AreEqual(2, d.Summary.Split('\n').Count(l => l.Contains("not approved") && l.Contains("set Player.gems 9")),
                "refused once in each attempt, and said so");
            StringAssert.DoesNotContain("'timeScale'", d.Summary, "the ticked timeScale steps ran");
            var first = writes.Take(n - 1).Select(w => w.GateActiveCalls).Distinct().ToList();
            var second = writes.Skip(n - 1).Select(w => w.GateActiveCalls).Distinct().ToList();
            Assert.AreEqual(1, first.Count, "the gate was asked between writes of attempt 1: [" + string.Join(", ", first) + "]");
            Assert.AreEqual(1, second.Count, "the gate was asked between writes of attempt 2: [" + string.Join(", ", second) + "]");
            Assert.AreEqual(first[0] + 1, second[0], "once between the two attempts — attempt 2's own question");
            Assert.AreEqual(second[0], after, "and never after attempt 2's");
            Assert.AreEqual(before + 2, first[0], "before attempt 1's writes: RunAll's show-ui, then attempt 1's own question");
        }

        /// <summary>CONTROL for the snapshot: a TICK still takes effect on the next command, not the next shot — only
        /// "is the gate live" is held for the attempt; the approval list is read for every command, as before.</summary>
        [Test]
        public void ATickGivenMidShotTakesEffectOnTheNextCommand()
        {
            var shot = "{\"name\":\"mid\",\"setup\":[\"set Player.coins 5\",\"set Player.gems 9\",\"set Player.gems 9\"]," +
                       "\"steps\":[{\"kind\":\"wait\",\"seconds\":0.2}],\"settle\":{\"kind\":\"absent\",\"name\":\"Never\"}}";
            var root = Deliver(Document(new[] { shot }));
            Levers.SetApproved(root, "set Player.coins 5", true);
            var inner = new Inner();
            var game = new GameAdapter { GameId = "g", Ui = new NoUi(), StateProbe = new NoState(), CheatBridge = inner,
                ReadyGate = NullReadyGate.Instance, Recovery = NullRecoveryPolicy.Instance };
            var clock = new double[] { 0 };
            var ticked = false;
            var (d, _, _, _) = RunTimed(game, Loaded(root, "mid"), root, now: () => clock[0], afterPump: _ =>
            {
                clock[0] += 0.05;
                // the first gems write has just been refused: tick it, between two pumps of the same attempt
                if (!ticked && GemsRefusedSoFar()) { Levers.SetApproved(root, "set Player.gems 9", true); ticked = true; }
            });
            Assert.IsTrue(ticked, "the tick was given mid-shot");
            Assert.AreEqual(1, inner.Calls.Count(c => c.Command == "set Player.gems 9"),
                "refused before the tick, run after it, in the same attempt:\n" + d.Summary);
        }

        private static bool GemsRefusedSoFar() => LeverGateBridge.RefusedThisSession.Contains("set Player.gems 9");

        // ---- THE PREAMBLE (slice E.4's tutorial gate), under the same property — stack pass 3 ----------------------------
        //
        // E.4's `AdDirector.RunPreamble` runs a tutorial gate's recovery, setup, arm wait, steps and settle "as RunShot runs
        // them", once, before a try or a recorded take. A2′'s thirteenth fold changed how RunShot runs them (one unit of work
        // per pump; the gate's liveness held for the attempt), and the merge of the two had to carry that into the preamble.
        // Each test below was SEEN RED on the merged tree before the hand fix. A gate records no step marks, so where its steps
        // ran is read off the UI and the bridge instead.

        /// <summary>A project the cloud never delivered to: the lever gate is not live, so every write reaches the game.</summary>
        private string UngatedRoot()
        {
            var root = Path.Combine(Path.GetTempPath(), "director-pump-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            _roots.Add(root);
            Assert.IsFalse(Levers.GateActive(root), "positive control: nothing was delivered here");
            return root;
        }

        /// <summary>A UI where everything is present except <c>Never</c>, which notes in which pump each name was first
        /// asked about and each click was made (<see cref="Pump"/> is set by the test between pumps).</summary>
        private sealed class TracedUi : IUiDriver
        {
            public int Pump = 1;
            public readonly Dictionary<string, int> FirstSeen = new();
            private bool See(string what) { if (!FirstSeen.ContainsKey(what)) FirstSeen[what] = Pump; return true; }
            public bool Exists(string name) => See("exists " + name) && name != "Never";
            public bool IsInteractable(string name) => Exists(name);
            public string? GetText(string name) => null;
            public bool Click(string name, int index = 0) => See("click " + name);
            public void PointerDown(string name, int index = 0) { }
            public void PointerUp(string name, int index = 0) { }
        }

        private static readonly AdShot PlainShot =
            new("shot", setup: Array.Empty<string>(), steps: new[] { AdStep.Wait(0.2) }, settle: WaitCondition.Absent("Never"));

        private static void AssertThePreambleHeldAndTheShotRan(AdDirector d)
        {
            Assert.IsTrue(d.PreambleRan, "positive control: the preamble ran");
            StringAssert.Contains("OK   gate (ran first, not recorded)", d.Summary, "positive control: the preamble held");
            Assert.IsTrue(d.AllCaptured, "positive control: the shot after it ran and was captured:\n" + d.Summary);
        }

        /// <summary>
        /// The preamble's SETUP, one command per pump — RunShot's rule since the thirteenth fold (a setup list had no yield,
        /// and 16,355 copies of one delivered command ran inside one editor tick). A gate's setup is the same kind of list,
        /// delivered the same way (a try's claim, a recording's claim), and E.4's RunPreamble ran it with no yield.
        /// </summary>
        [Test]
        public void Preamble_ItsSetup_EachCommandRunsInItsOwnPump()
        {
            var root = UngatedRoot();
            var setup = Enumerable.Range(0, 20).Select(i => "gate-cmd" + i).ToArray();
            var gate = new AdShot("gate", setup: setup, steps: new[] { AdStep.Wait(0.2) }, settle: WaitCondition.Absent("Never"));
            var inner = new Inner();
            var game = new GameAdapter { GameId = "g", Ui = new NoUi(), StateProbe = new NoState(), CheatBridge = inner,
                ReadyGate = NullReadyGate.Instance, Recovery = NullRecoveryPolicy.Instance };
            var clock = new double[] { 0 };
            var pumpOf = new Dictionary<string, int>();
            var seen = 0;
            var (d, _, _, _) = RunTimed(game, PlainShot, root, now: () => clock[0], preamble: gate, afterPump: pump =>
            {
                clock[0] += 0.05;
                for (; seen < inner.Calls.Count; seen++) pumpOf[inner.Calls[seen].Command] = pump;
            });
            AssertThePreambleHeldAndTheShotRan(d);
            var ranIn = setup.Select(c => pumpOf.TryGetValue(c, out var p) ? p : -1).ToList();
            CollectionAssert.DoesNotContain(ranIn, -1, "every command of the gate's setup reached the game");
            Assert.AreEqual(setup.Length, ranIn.Distinct().Count(), "the gate's setup ran in pumps [" + string.Join(", ", ranIn) + "]");
        }

        /// <summary>
        /// The preamble's STEPS, each starting in its own pump — RunShot's rule since the thirteenth fold. A `timeScale`, a
        /// click with no `until`, and a waitFor or cheatUntil already met finish without yielding, and E.4's RunPreamble ran
        /// its steps back to back: several of them started in one pump. Read off the UI: the pump each click was made in, and
        /// the pump each condition was first asked about.
        /// </summary>
        [Test]
        public void Preamble_ItsStepsThatNeverWait_EachStartInTheirOwnPump()
        {
            var root = UngatedRoot();
            var steps = Enumerable.Range(0, 5).SelectMany(i => new[]
            {
                AdStep.TimeScale(1),
                AdStep.Click("P" + i),
                AdStep.WaitFor(WaitCondition.Present("W" + i), 1),
                AdStep.CheatUntil("fix" + i, WaitCondition.Present("C" + i)),
            }).ToArray();
            var gate = new AdShot("gate", setup: Array.Empty<string>(), steps: steps, settle: WaitCondition.Absent("Never"));
            var ui = new TracedUi();
            var game = new GameAdapter { GameId = "g", Ui = ui, StateProbe = new NoState(), CheatBridge = new Inner(),
                ReadyGate = NullReadyGate.Instance, Recovery = NullRecoveryPolicy.Instance };
            var clock = new double[] { 0 };
            var (d, _, _, _) = RunTimed(game, PlainShot, root, now: () => clock[0], preamble: gate, afterPump: pump =>
            {
                clock[0] += 0.05;
                ui.Pump = pump + 1;
            });
            AssertThePreambleHeldAndTheShotRan(d);
            var traced = Enumerable.Range(0, 5).SelectMany(i => new[] { "click P" + i, "exists W" + i, "exists C" + i }).ToList();
            var at = traced.Select(t => ui.FirstSeen.TryGetValue(t, out var p) ? p : -1).ToList();
            CollectionAssert.DoesNotContain(at, -1, "every step of the gate ran: " + string.Join(", ", traced.Zip(at, (t, p) => t + "@" + p)));
            Assert.AreEqual(traced.Count, at.Distinct().Count(),
                "the gate's steps ran at pumps " + string.Join(", ", traced.Zip(at, (t, p) => t + "@" + p)) + " — two of them in one pump");
        }

        /// <summary>
        /// The preamble's LIVENESS, asked ONCE for the preamble, not once per write — RunShot's rule since the thirteenth fold
        /// (ruling 2: asking on every write hashed the delivered files each time; 21.9 s over one delivered setup list). Same
        /// shape as <see cref="TheGateIsAskedWhetherItIsLiveOncePerAttemptNotOncePerWrite"/>, with the gated list as the
        /// run's preamble: N gated setup commands, one un-ticked, and two ticked `timeScale` steps (the question a write that
        /// does not go through the bridge asks, under the same hold). Counted on a DISK-gated project with the cloud flag off,
        /// because a try's and a gated take's flag answers "live" without asking <see cref="Levers.GateActive"/> at all.
        /// Before the preamble: RunAll's one `show-ui`; the shot's attempt after it asks once more, for itself.
        /// </summary>
        [TestCase(5)]
        [TestCase(500)]
        public void Preamble_TheGateIsAskedWhetherItIsLiveOnceForThePreambleNotOncePerWrite(int n)
        {
            var gateJson = "{\"name\":\"gate\",\"settleTimeoutSec\":0.2,\"setup\":[" +
                string.Join(",", Enumerable.Range(0, n).Select(i => i == n / 2 ? "\"set Player.gems 9\"" : "\"set Player.coins 5\"")) +
                "],\"steps\":[{\"kind\":\"timeScale\",\"factor\":1},{\"kind\":\"wait\",\"seconds\":0.2},{\"kind\":\"timeScale\",\"factor\":1}]," +
                "\"settle\":{\"kind\":\"absent\",\"name\":\"Never\"}}";
            var shotJson = "{\"name\":\"shot\",\"setup\":[\"set Player.coins 6\"],\"steps\":[{\"kind\":\"wait\",\"seconds\":0.2}]," +
                "\"settle\":{\"kind\":\"absent\",\"name\":\"Never\"}}";
            var root = Deliver(Document(new[] { gateJson, shotJson }));
            Levers.SetApproved(root, "set Player.coins 5", true);
            Levers.SetApproved(root, "set Player.coins 6", true);
            Levers.SetApproved(root, Levers.TimeScaleLever, true);
            var inner = new Inner();
            var game = new GameAdapter { GameId = "g", Ui = new NoUi(), StateProbe = new NoState(), CheatBridge = inner,
                ReadyGate = NullReadyGate.Instance, Recovery = NullRecoveryPolicy.Instance };
            var clock = new double[] { 0 };
            var before = Levers.GateActiveCallsForTests;
            var (d, _, _, _) = RunTimed(game, Loaded(root, "shot"), root, now: () => clock[0], preamble: Loaded(root, "gate"),
                afterPump: _ => clock[0] += 0.05);
            var after = Levers.GateActiveCallsForTests;

            AssertThePreambleHeldAndTheShotRan(d);
            var writes = inner.Calls.Where(c => c.Command == "set Player.coins 5").ToList();
            Assert.AreEqual(n - 1, writes.Count, "the ticked command of the gate reached the game:\n" + d.Summary);
            Assert.IsFalse(inner.Calls.Any(c => c.Command == "set Player.gems 9"), "the un-ticked command of the gate was refused");
            Assert.AreEqual(1, d.Summary.Split('\n').Count(l => l.Contains("not approved") && l.Contains("set Player.gems 9")),
                "refused once, and said so");
            StringAssert.DoesNotContain("'timeScale'", d.Summary, "the gate's ticked timeScale steps ran");
            var inThePreamble = writes.Select(w => w.GateActiveCalls).Distinct().ToList();
            Assert.AreEqual(1, inThePreamble.Count,
                "the gate was asked whether it is live between writes of the preamble: [" + string.Join(", ", inThePreamble) + "]");
            Assert.AreEqual(before + 2, inThePreamble[0], "before the preamble's writes: RunAll's show-ui, then the preamble's own question");
            var shotWrite = inner.Calls.Single(c => c.Command == "set Player.coins 6").GateActiveCalls;
            Assert.AreEqual(inThePreamble[0] + 1, shotWrite,
                "once between the preamble's writes and the shot's — the shot attempt's own question (the gate's timeScale steps asked none)");
            Assert.AreEqual(shotWrite, after, "and never after the shot attempt's");
        }
    }
}
