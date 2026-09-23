using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// Slice E.4, part 2 — THE TUTORIAL GATE RUNS FIRST, for RECORDINGS: the capture claim's gate, the
    /// progress file, the gate read by the one reader a try's is, the lever refused before anything runs,
    /// the gated take (the gate first, recording off, then the shot recorded), the take a gate stop fails
    /// (the one sentence, no clip, nothing quarantined), the step marks (the shot's only) — and a take
    /// WITHOUT a gate, which must be today's call byte for byte. Real temp project roots, the real loader
    /// and the real director throughout (invariant 101).
    /// </summary>
    public class CaptureGateTests
    {
        private string _root = "";
        private double _clock;

        /// <summary>The studio's own shot: one setup write, one cheat, one click, settles on Board. Its
        /// levers: "SelectHero Knight", "set Coins 5".</summary>
        private const string ShotText = @"{ ""name"": ""rec-me"", ""setup"": [""set Coins 5""],
  ""steps"": [ { ""kind"": ""cheat"", ""command"": ""SelectHero Knight"" },
               { ""kind"": ""click"", ""name"": ""Go"" } ],
  ""settle"": { ""kind"": ""present"", ""name"": ""Board"" } }";

        /// <summary>The pack's `tutorial-gate` shot as the claim carries it. Its levers: "SkipIntro",
        /// "SkipTutorial".</summary>
        private const string GateText = @"{ ""name"": ""tutorial-gate"", ""setup"": [""SkipIntro""],
  ""steps"": [ { ""kind"": ""cheat"", ""command"": ""SkipTutorial"" } ],
  ""settle"": { ""kind"": ""present"", ""name"": ""Board"" } }";

        private const string PlainAdapter = @"{ ""gameId"": ""g"" }";

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "capgate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RelayPaths.NovaDir(_root));
            _clock = 0;
        }

        [TearDown]
        public void TearDown()
        {
            AdDirector.Active?.Finish("test teardown");
            LeverGateBridge.ForgetRefused();
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
        }

        // ---- helpers -----------------------------------------------------------------------------

        private void Tick(params string[] levers)
        {
            foreach (var l in levers)
                Assert.IsNull(Levers.SetApproved(_root, l, true), "could not tick " + l);
        }

        private static AdShot Load(string text)
        {
            var loaded = JsonShotLoader.LoadFrom(@"{ ""$schemaVersion"": 1, ""shots"": [ " + text + " ] }",
                remember: false);
            Assert.IsEmpty(loaded.Errors, string.Join(" · ", loaded.Errors));
            return loaded.Shots[0];
        }

        private static CaptureGateClaim Claimed(string text, string? sha = null) =>
            CaptureGateClaim.FromJson(new ProbeGate(text, sha ?? SyncNova.Sha256OfText(text)).ToJson())!;

        private static CaptureGatePlan Planned(string text)
        {
            var plan = CaptureGatePlan.Prepare(Claimed(text))!;
            Assert.IsNull(plan.Refusal, plan.Refusal);
            return plan;
        }

        private sealed class FakeUi : IUiDriver
        {
            public readonly HashSet<string> Names = new() { "Board", "Go" };
            public bool Exists(string name) => Names.Contains(name);
            public bool IsInteractable(string name) => Names.Contains(name);
            public string? GetText(string name) => null;
            public bool Click(string name, int index = 0) => Names.Contains(name);
            public void PointerDown(string name, int index = 0) { }
            public void PointerUp(string name, int index = 0) { }
        }

        private sealed class OtherUi : IUiDriver
        {
            public bool Exists(string name) => false;
            public bool IsInteractable(string name) => false;
            public string? GetText(string name) => null;
            public bool Click(string name, int index = 0) => false;
            public void PointerDown(string name, int index = 0) { }
            public void PointerUp(string name, int index = 0) { }
        }

        /// <summary>The game's commands, and the recorder's starts, in ONE list.</summary>
        private sealed class RecordingBridge : ICheatBridge
        {
            public readonly List<string> Run_ = new();
            public bool Run(string command) { Run_.Add(command); return true; }
        }

        /// <summary>A recorder that WRITES A TAKE the way a real one does — `&lt;clip&gt;_NNN.mp4` in its output
        /// folder, on Start — so "no clip was made" and "nothing was quarantined" are read off the disk.</summary>
        private sealed class FileRecorder : IRecorderDriver
        {
            private readonly List<string>? _events;
            public readonly List<string> Starts = new();
            public FileRecorder(string dir, List<string>? events = null)
            {
                OutputDir = dir;
                _events = events;
                Directory.CreateDirectory(dir);
            }
            public string OutputDir { get; }
            public bool IsRecording { get; private set; }
            public int OutputWidth => 0;
            public int OutputHeight => 0;
            public void Start(string clipName)
            {
                Starts.Add(clipName);
                _events?.Add("REC " + clipName);
                File.WriteAllText(Path.Combine(OutputDir, $"{clipName}_{Starts.Count + 100:000}.mp4"), "take");
                IsRecording = true;
            }
            public void Stop() => IsRecording = false;
        }

        private static string[] Writes(RecordingBridge b) => b.Run_.Where(c => !Levers.IsReadOnly(c)).ToArray();

        private GameAdapter Adapter(RecordingBridge cheats, FakeUi? ui = null) => new()
        {
            GameId = "g",
            Ui = ui ?? new FakeUi(),
            CheatBridge = cheats,
            ReadyGate = NullReadyGate.Instance,
            Recovery = NullRecoveryPolicy.Instance,
        };

        /// <summary>The tests' tuning: a hand-pumped clock, a fake recorder, this temp project.</summary>
        private AdDirector.Options Tuning(IRecorderDriver recorder, Action? onRelease = null) => new()
        {
            AutoPump = false,
            Now = () => _clock,
            ReleaseCameraHold = onRelease ?? (() => { }),
            Recorder = recorder,
            ProjectRoot = _root,
        };

        private AdDirector Pump(AdDirector? d)
        {
            Assert.IsNotNull(d, "no director was built");
            var ticks = 0;
            while (!d!.IsFinished && ticks++ < 100_000)
            {
                d.PumpOnce();
                _clock += 0.1;
            }
            Assert.IsTrue(d.IsFinished, "director never finished");
            return d;
        }

        private static string[] Files(string dir) =>
            Directory.Exists(dir)
                ? Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray()!
                : Array.Empty<string>();

        // ---- the claim ---------------------------------------------------------------------------

        private static CaptureGateClaim? ParseCapture(string gate, string kind = "capture")
        {
            var job = CaptureJob.ParseClaim(
                @"{ ""job"": { ""runId"": ""r1"", ""workspaceId"": ""w"", ""kind"": """ + kind + @""", ""gameId"": ""g"",
                    ""items"": [ { ""shot"": ""rec-me"", ""take"": 1 } ] }, ""leaseUntil"": ""x""" + gate + " }",
                out var error, out _, out _, out var tutorialGate);
            Assert.IsNull(error, error);
            Assert.IsNotNull(job);
            return tutorialGate;
        }

        [Test]
        public void Claim_ARecordingsGateRidesItsClaim_NullOrAbsentIsNone_AnythingElseDidNotArriveWhole()
        {
            var with = ParseCapture(@", ""tutorialGate"": { ""shot"": ""G"", ""sha256"": ""gg"" }");
            Assert.AreEqual("G", with!.Gate!.Shot);
            Assert.AreEqual("gg", with.Gate.Sha256);
            Assert.IsNull(ParseCapture(@", ""tutorialGate"": null"), "JSON null is no gate");
            Assert.IsNull(ParseCapture(""), "absent is no gate — a claim from before part 2 records as it always did");
            foreach (var bad in new[]
                     {
                         @", ""tutorialGate"": { ""shot"": ""G"" }",
                         @", ""tutorialGate"": { ""shot"": """", ""sha256"": ""gg"" }",
                         @", ""tutorialGate"": ""G""",
                         @", ""tutorialGate"": []",
                         @", ""tutorialGate"": false",
                     })
            {
                var claim = ParseCapture(bad);
                Assert.IsNotNull(claim, "a gate that did not arrive whole was read as NO gate: " + bad);
                Assert.IsNull(claim!.Gate, bad);
                Assert.AreEqual(CaptureGateClaim.MissingReason, CaptureGatePlan.Prepare(claim)!.Refusal, bad);
            }
            // another kind's claim is not a recording's: a top-level gate there is not read
            Assert.IsNull(ParseCapture(@", ""tutorialGate"": { ""shot"": ""G"", ""sha256"": ""gg"" }", "self-test"));
        }

        [Test]
        public void Progress_TheGateSurvivesThePlayModeReload_AndARecordingWithoutOneLeavesTheFileAsItWas()
        {
            var job = new CaptureJob("r", "w", new[] { new CaptureJobItem("rec-me", 1) }, 60, "capture", "g");
            var file = Path.Combine(_root, "capture-status.json");
            var gated = CaptureProgress.Start(job, null, Claimed(GateText));
            gated.Save(file);
            var back = CaptureProgress.Load(file)!;
            Assert.AreEqual(GateText, back.TutorialGate!.Gate!.Shot, "the gate did not survive the reload");
            Assert.AreEqual(SyncNova.Sha256OfText(GateText), back.TutorialGate.Gate.Sha256);
            Assert.AreEqual("tutorial-gate", CaptureGatePlan.Prepare(back.TutorialGate)!.Gate!.Name);
            // a gate that did not arrive whole reloads as one that did not arrive whole — never as none
            CaptureProgress.Start(job, null, CaptureGateClaim.FromJson(new JValue("G"))).Save(file);
            var broken = CaptureProgress.Load(file)!;
            Assert.IsNotNull(broken.TutorialGate);
            Assert.IsNull(broken.TutorialGate!.Gate);
            // no gate: no key — the progress file a recording always wrote
            var none = CaptureProgress.Start(job);
            none.Save(file);
            Assert.IsFalse(JObject.Parse(File.ReadAllText(file)).ContainsKey("tutorialGate"));
            Assert.AreEqual(none.ToJson(), CaptureProgress.Start(job, null, null).ToJson());
            Assert.IsNull(CaptureProgress.Load(file)!.TutorialGate);
        }

        // ---- the gate, read by the ONE reader ----------------------------------------------------

        [Test]
        public void Plan_TheGateIsReadByTheOneReaderATrysIs_ItsShaFirst_ThenOneObject_ThenItsName_ThenTheLoader()
        {
            Assert.IsNull(CaptureGatePlan.Prepare(null), "no gate on the claim: nothing to plan — today's recording");

            var wrong = CaptureGatePlan.Prepare(Claimed(GateText, new string('0', 64)))!;
            StringAssert.StartsWith("the tutorial gate that arrived is not the one the website sent", wrong.Refusal);
            StringAssert.EndsWith("— nothing was run; start the recording again", wrong.Refusal);
            Assert.AreEqual(SyncNova.Sha256OfText(GateText), wrong.GateSha256, "the sha is MEASURED, never copied");
            Assert.IsNull(wrong.Gate);
            StringAssert.StartsWith("the tutorial gate that arrived is not one JSON object",
                CaptureGatePlan.Prepare(Claimed("not json"))!.Refusal);
            Assert.AreEqual("the tutorial gate that arrived is not the shot named 'tutorial-gate' — nothing was run",
                CaptureGatePlan.Prepare(Claimed(GateText.Replace("tutorial-gate", "intro")))!.Refusal);
            StringAssert.StartsWith("this recording's tutorial gate does not load: ",
                CaptureGatePlan.Prepare(Claimed(@"{ ""name"": ""tutorial-gate"", ""steps"": [ { ""kind"": ""wait"" } ] }"))!.Refusal);

            var plan = Planned(GateText);
            Assert.AreEqual("tutorial-gate", plan.Gate!.Name);
            Assert.AreEqual(SyncNova.Sha256OfText(GateText), plan.GateSha256);
            CollectionAssert.AreEqual(new[] { "SkipIntro", "SkipTutorial" }, plan.LeversNeeded,
                "the GATE's own levers — its setup and its cheat");
        }

        [Test]
        public void Plan_ATryAndARecordingRefuseTheSameBrokenGateInTheSameWords_OnlyWhatToDoNextDiffers()
        {
            File.WriteAllBytes(SyncNova.AdapterFile(_root), new UTF8Encoding(false).GetBytes(PlainAdapter));
            ProbeRequest Try(string gate, string? sha = null) => new(ShotText, PlainAdapter,
                SyncNova.Sha256OfText(ShotText), SyncNova.Sha256OfText(PlainAdapter),
                new ProbeGate(gate, sha ?? SyncNova.Sha256OfText(gate)));
            foreach (var (gate, sha) in new[]
                     {
                         ("not json", (string?)null),
                         (GateText.Replace("tutorial-gate", "intro"), null),
                         (@"[ 1 ]", null),
                     })
                Assert.AreEqual(ProbePlan.Prepare(_root, Try(gate, sha)).Refusal,
                    CaptureGatePlan.Prepare(Claimed(gate, sha))!.Refusal, gate);
            var zero = new string('0', 64);
            var tried = ProbePlan.Prepare(_root, Try(GateText, zero)).Refusal!;
            var recorded = CaptureGatePlan.Prepare(Claimed(GateText, zero))!.Refusal!;
            StringAssert.EndsWith("— nothing was run; press Try this shot again", tried, "a try's words changed");
            Assert.AreEqual(tried.Replace("press Try this shot again", "start the recording again"), recorded);
        }

        /// <summary>First audit of E.4, S1, the kit's half for a RECORDING: a gate that holds a screen check is
        /// refused whole by the words a try's gate is refused with — after the one reader (its sha is still
        /// measured), before any take is built.</summary>
        [Test]
        public void Plan_AGateWithAScreenCheck_IsRefusedWhole_InTheWordsATrysIs_AndNoTakeIsBuilt()
        {
            const string visionGate = @"{ ""name"": ""tutorial-gate"", ""setup"": [""SkipIntro""],
  ""steps"": [ { ""kind"": ""cheat"", ""command"": ""SkipTutorial"" },
               { ""kind"": ""vision"", ""prompt"": ""the tutorial is gone"", ""timeout"": 5 } ],
  ""settle"": { ""kind"": ""present"", ""name"": ""Board"" } }";
            var plan = CaptureGatePlan.Prepare(Claimed(visionGate))!;
            Assert.AreEqual(ProbePlan.GateHasScreenCheck, plan.Refusal);
            Assert.IsNull(plan.Gate, "a gate with a screen check was handed on to be run");
            Assert.AreEqual(SyncNova.Sha256OfText(visionGate), plan.GateSha256, "the one reader ran first");
            // the SAME words a try refuses the same gate with
            File.WriteAllBytes(SyncNova.AdapterFile(_root), new UTF8Encoding(false).GetBytes(PlainAdapter));
            var tried = ProbePlan.Prepare(_root, new ProbeRequest(ShotText, PlainAdapter,
                SyncNova.Sha256OfText(ShotText), SyncNova.Sha256OfText(PlainAdapter),
                new ProbeGate(visionGate, SyncNova.Sha256OfText(visionGate))));
            Assert.AreEqual(tried.Refusal, plan.Refusal);
            // …and no take is built from it: every lever ticked, the last door still builds nothing
            Tick("set Coins 5", "SelectHero Knight", "SkipIntro", "SkipTutorial");
            var cheats = new RecordingBridge();
            var recorder = new FileRecorder(Path.Combine(_root, "takes"));
            Assert.IsNull(CaptureRun.Start(Adapter(cheats), Load(ShotText), plan, _root, Tuning(recorder)),
                "a director was built for a take whose gate holds a screen check");
            CollectionAssert.IsEmpty(cheats.Run_, "a command reached the game: " + string.Join(", ", cheats.Run_));
            CollectionAssert.IsEmpty(recorder.Starts);
            // CONTROL: the same gate without its screen check plans
            Assert.IsNull(CaptureGatePlan.Prepare(Claimed(GateText))!.Refusal);
        }

        // ---- ruling 6: a lever only the gate needs --------------------------------------------------

        [Test]
        public void Lever_WithoutThePreCheck_AnUntickedLeverOnlyTheGateNeeds_DoesNotStopTheTakeBeforeItRuns()
        {
            // VERIFIED, not assumed: the director alone (the last door bypassed) does not refuse a take whose
            // gate needs an un-ticked `setup` lever — the lever gate SKIPS that one command and the rest runs:
            // the gate's step, the shot's setup, the recording.
            Tick("set Coins 5", "SelectHero Knight", "SkipTutorial");
            var plan = Planned(GateText);
            var cheats = new RecordingBridge();
            var recorder = new FileRecorder(Path.Combine(_root, "takes"));
            var d = Pump(AdDirector.Run(Adapter(cheats), new[] { Load(ShotText) },
                CaptureRun.OptionsFor(_root, plan.Gate, Tuning(recorder))));
            CollectionAssert.DoesNotContain(cheats.Run_, "SkipIntro");
            CollectionAssert.IsSubsetOf(new[] { "SkipTutorial", "set Coins 5", "SelectHero Knight" }, Writes(cheats));
            CollectionAssert.AreEqual(new[] { "rec-me" }, recorder.Starts, "the take ran and recorded");
            CollectionAssert.Contains(d.LogLines, Levers.NotApprovedLog("SkipIntro"));
        }

        [Test]
        public void Lever_AnUntickedLeverOnlyTheGateNeeds_RefusesTheTakeBeforeAnythingRuns_ByName()
        {
            Tick("set Coins 5", "SelectHero Knight", "SkipTutorial");
            var plan = Planned(GateText);
            Assert.AreEqual("SkipIntro", plan.LeverRefused(_root, Load(ShotText)), "the shot's own levers are ticked — only the gate's is not");
            Assert.AreEqual("the tutorial gate stopped it: lever not approved on this machine: 'SkipIntro'",
                CaptureRun.GateLeverRefused("SkipIntro"));
            Assert.AreEqual(CaptureRun.GateLeverRefused("SkipIntro"), CaptureRun.RefusedBeforeTake(plan, _root, Load(ShotText)),
                "a lever of the GATE is refused in the gate's sentence");
            var cheats = new RecordingBridge();
            var recorder = new FileRecorder(Path.Combine(_root, "takes"));
            Assert.IsNull(CaptureRun.Start(Adapter(cheats), Load(ShotText), plan, _root, Tuning(recorder)),
                "a director was built for a take whose gate needs an un-ticked lever");
            CollectionAssert.IsEmpty(cheats.Run_, "a command reached the game: " + string.Join(", ", cheats.Run_));
            CollectionAssert.IsEmpty(recorder.Starts);
            // the second audit of E.4, M1 — a lever only the SHOT needs refuses the gated take too (the tries'
            // union), in the lever gate's own sentence: the gate's levers ticked, the shot's not
            LeverGateBridge.ForgetRefused();
            var shotOnly = Path.Combine(_root, "shot-only");
            Directory.CreateDirectory(RelayPaths.NovaDir(shotOnly));
            Assert.IsNull(Levers.SetApproved(shotOnly, "SkipIntro", true));
            Assert.IsNull(Levers.SetApproved(shotOnly, "SkipTutorial", true));
            Assert.AreEqual("SelectHero Knight", plan.LeverRefused(shotOnly, Load(ShotText)));
            Assert.AreEqual("lever not approved on this machine: 'SelectHero Knight'",
                CaptureRun.RefusedBeforeTake(plan, shotOnly, Load(ShotText)));
            // ticked: the take is built
            Tick("SkipIntro");
            Assert.IsNull(plan.LeverRefused(_root, Load(ShotText)));
            Assert.IsNull(CaptureRun.RefusedBeforeTake(plan, _root, Load(ShotText)));
            Pump(CaptureRun.Start(Adapter(new RecordingBridge()), Load(ShotText), plan, _root, Tuning(recorder)));
        }

        // ---- the second audit of E.4 --------------------------------------------------------------

        private static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);

        /// <summary>Second audit of E.4, M1 — THE AUDITOR'S CASE: on a project whose files are its own, a gated take
        /// whose SHOT needs an un-ticked `setup` lever is refused before anything runs — not the ready gate, not
        /// the gate, not the recording. (It used to run: the forced lever gate skipped `set Coins 5`, and the take
        /// captured and uploaded as the shot's.) The no-gate control is today's take: ungated files, the setup
        /// runs, the take records.</summary>
        [Test]
        public void Lever_SecondAuditM1_AnUntickedSetupOfTheSHOT_RefusesAGatedTakeBeforeAnythingRuns_TheNoGateTakeRunsIt()
        {
            Assert.IsFalse(Levers.GateActive(_root), "precondition: this project's files are its own");
            Tick("SkipIntro", "SkipTutorial", "SelectHero Knight"); // NOT "set Coins 5"
            var plan = Planned(GateText);
            var cheats = new RecordingBridge();
            var recorder = new FileRecorder(Path.Combine(_root, "takes"));
            Assert.AreEqual("lever not approved on this machine: 'set Coins 5'",
                CaptureRun.RefusedBeforeTake(plan, _root, Load(ShotText)), "the take is refused BY NAME, before it runs");
            Assert.IsNull(CaptureRun.Start(Adapter(cheats), Load(ShotText), plan, _root, Tuning(recorder)),
                "a director was built for a gated take whose shot needs an un-ticked lever");
            CollectionAssert.IsEmpty(cheats.Run_, "a command reached the game: " + string.Join(", ", cheats.Run_));
            CollectionAssert.IsEmpty(recorder.Starts, "the take recorded");
            CollectionAssert.IsEmpty(Files(recorder.OutputDir), "a clip was made");

            // CONTROL — NO GATE: today's take. The setup runs (these files are ungated) and the take records.
            var c0 = new RecordingBridge();
            var r0 = new FileRecorder(Path.Combine(_root, "t0"));
            var d0 = Pump(CaptureRun.Start(Adapter(c0), Load(ShotText), null, _root, Tuning(r0)));
            CollectionAssert.Contains(c0.Run_, "set Coins 5", d0.Summary);
            Assert.IsTrue(d0.AllCaptured, d0.Summary);
            CollectionAssert.AreEqual(new[] { "rec-me" }, r0.Starts);
            Assert.IsNull(CaptureRun.RefusedBeforeTake(null, _root, Load(ShotText)), "without a gate nothing is asked");
        }

        /// <summary>Second audit of E.4, M1 — the union's third part: a lever the ADAPTER needs (its adapter.json's
        /// `overlayTypeNames`), un-ticked, refuses a gated take before anything runs, as it refuses a try. Ticked,
        /// the take runs.</summary>
        [Test]
        public void Lever_SecondAuditM1_AnUntickedLeverOfTheADAPTER_RefusesAGatedTake_TickedItRuns()
        {
            File.WriteAllBytes(SyncNova.AdapterFile(_root),
                Utf8(@"{ ""gameId"": ""g"", ""overlayTypeNames"": [""TutorialArrow""] }"));
            Assert.IsFalse(Levers.GateActive(_root), "precondition: this project's files are its own");
            Tick("SkipIntro", "SkipTutorial", "SelectHero Knight", "set Coins 5"); // NOT "hide-overlay TutorialArrow"
            var plan = Planned(GateText);
            var cheats = new RecordingBridge();
            var recorder = new FileRecorder(Path.Combine(_root, "takes"));
            Assert.AreEqual(Levers.NotApprovedLog(Levers.HideOverlayLever("TutorialArrow")),
                CaptureRun.RefusedBeforeTake(plan, _root, Load(ShotText)));
            Assert.IsNull(CaptureRun.Start(Adapter(cheats), Load(ShotText), plan, _root, Tuning(recorder)),
                "a director was built for a gated take whose adapter needs an un-ticked lever");
            CollectionAssert.IsEmpty(cheats.Run_, "a command reached the game: " + string.Join(", ", cheats.Run_));
            CollectionAssert.IsEmpty(recorder.Starts);
            // ticked: the take is built and records
            Tick(Levers.HideOverlayLever("TutorialArrow"));
            var d = Pump(CaptureRun.Start(Adapter(new RecordingBridge()), Load(ShotText), plan, _root, Tuning(recorder)));
            Assert.IsTrue(d.AllCaptured, d.Summary);
        }

        /// <summary>Second audit of E.4, M1 — the union is the TRIES' union, by the lever rule's own function: a
        /// loaded shot's half is exactly <see cref="Levers.NeededFromShotsJson"/> over the text it was loaded from
        /// (setup, cheat, cheatUntil, one timeScale lever — and nothing for a click, a wait, a hold or a screen
        /// check), and the take's list is the gate's ∪ the shot's ∪ the adapter's on this disk.</summary>
        [Test]
        public void Lever_SecondAuditM1_TheTakesListIsTheTriesUnion_ByTheLeverRulesOwnFunction()
        {
            const string busy = @"{ ""name"": ""busy"", ""setup"": [""set Coins 5"", ""call Shop.Open""],
  ""steps"": [ { ""kind"": ""cheat"", ""command"": ""SelectHero Knight"" },
               { ""kind"": ""cheatUntil"", ""command"": ""SkipPopup"", ""until"": { ""kind"": ""absent"", ""name"": ""Popup"" } },
               { ""kind"": ""timeScale"", ""factor"": 0.5 },
               { ""kind"": ""click"", ""name"": ""Go"" },
               { ""kind"": ""hold"", ""name"": ""Go"", ""seconds"": 1 },
               { ""kind"": ""wait"", ""seconds"": 1 },
               { ""kind"": ""vision"", ""prompt"": ""set Coins 99"" } ],
  ""settle"": { ""kind"": ""present"", ""name"": ""Board"" } }";
            foreach (var text in new[] { ShotText, GateText, busy })
                CollectionAssert.AreEqual(
                    Levers.NeededFromShotsJson(@"{ ""$schemaVersion"": 1, ""shots"": [ " + text + " ] }"),
                    Levers.NeededFromShotsJson(CaptureGatePlan.ShotDocument(Load(text))), text);
            CollectionAssert.AreEqual(new[] { "SelectHero Knight", "SkipPopup", "call Shop.Open", "set Coins 5", "timeScale" },
                Levers.NeededFromShotsJson(CaptureGatePlan.ShotDocument(Load(busy))));
            const string adapter = @"{ ""gameId"": ""g"", ""overlayTypeNames"": [""TutorialArrow""] }";
            CollectionAssert.AreEqual(
                new[] { "SelectHero Knight", "SkipIntro", "SkipTutorial", "hide-overlay TutorialArrow", "set Coins 5" },
                Planned(GateText).TakeLeversNeeded(Load(ShotText), adapter));
            CollectionAssert.AreEqual(new[] { "SelectHero Knight", "SkipIntro", "SkipTutorial", "set Coins 5" },
                Planned(GateText).TakeLeversNeeded(Load(ShotText), null), "no adapter.json: no adapter levers");
        }

        /// <summary>Second audit of E.4, M2 — WHAT A GATED TAKE WAITS FOR before its director is built: the gate's
        /// screen (its <c>arm</c>) OR the shot's own (its settle). The tutorial already gone — only the shot's
        /// screen up — is enough (it used to wait out the whole wait for the gate's screen, every take); the
        /// tutorial up — only the gate's — is enough too; neither is not. NO GATE = TODAY: the shot's arm, else
        /// its settle, one condition, said as it describes itself.</summary>
        [Test]
        public void Ready_SecondAuditM2_AGatedTakeStartsOnTheGatesScreenOrTheShots_NoGateOnTheShotsAlone()
        {
            const string armedGate = @"{ ""name"": ""tutorial-gate"", ""setup"": [""SkipIntro""],
  ""arm"": { ""kind"": ""present"", ""name"": ""TutorialPanel"" },
  ""steps"": [ { ""kind"": ""cheat"", ""command"": ""SkipTutorial"" } ],
  ""settle"": { ""kind"": ""present"", ""name"": ""Board"" } }";
            var shot = Load(ShotText);
            var gate = Planned(armedGate).Gate!;
            var start = CaptureRun.StartsOn(shot, gate);
            FakeUi Screen(params string[] names)
            {
                var ui = new FakeUi();
                ui.Names.Clear();
                foreach (var n in names) ui.Names.Add(n);
                return ui;
            }
            Assert.IsTrue(start.IsMet(Screen("Board"), null), "the tutorial is gone and the shot's screen is up: the take waited anyway");
            Assert.IsTrue(start.IsMet(Screen("TutorialPanel"), null), "the tutorial is up: the take did not start on it");
            Assert.IsFalse(start.IsMet(Screen("Loading"), null), "neither screen is up");
            Assert.AreEqual("Present 'TutorialPanel' or Present 'Board'", start.Describe());
            // NO GATE: exactly today's one condition — the shot's arm, else its settle
            var none = CaptureRun.StartsOn(shot, null);
            Assert.AreEqual(1, none.Screens.Count);
            var today = shot.ArmCondition ?? shot.Settle;
            Assert.AreEqual(today.Describe(), none.Describe());
            foreach (var ui in new[] { Screen("Board"), Screen("TutorialPanel"), Screen() })
                Assert.AreEqual(today.IsMet(ui), none.IsMet(ui, null));

            // THE THIRD AUDIT OF E.4, KS1 — a shot that declares an `arm` (the shot above has none). NO GATE: it
            // starts on its ARM, one condition, as it always did — never on its settle, which would wait out the
            // whole 60 s for a screen that comes only after the take. GATED: the gate's screen OR the shot's ARM.
            const string armedShot = @"{ ""name"": ""rec-armed"", ""arm"": { ""kind"": ""present"", ""name"": ""Lobby"" },
  ""steps"": [ { ""kind"": ""click"", ""name"": ""Go"" } ],
  ""settle"": { ""kind"": ""present"", ""name"": ""Board"" } }";
            var armed = Load(armedShot);
            var armedToday = CaptureRun.StartsOn(armed, null);
            Assert.AreEqual(1, armedToday.Screens.Count);
            Assert.AreEqual("Present 'Lobby'", armedToday.Describe(), "without a gate an armed shot starts on its arm");
            Assert.IsTrue(armedToday.IsMet(Screen("Lobby"), null));
            Assert.IsFalse(armedToday.IsMet(Screen("Board"), null), "the shot's settle alone is not its arm");
            var armedGated = CaptureRun.StartsOn(armed, gate);
            Assert.AreEqual("Present 'TutorialPanel' or Present 'Lobby'", armedGated.Describe());
            Assert.IsTrue(armedGated.IsMet(Screen("Lobby"), null), "the tutorial is gone and the shot's arm is up");
            Assert.IsTrue(armedGated.IsMet(Screen("TutorialPanel"), null));
            Assert.IsFalse(armedGated.IsMet(Screen("Board"), null), "the shot's settle alone is not its arm");
        }

        // ---- the third audit of E.4 ---------------------------------------------------------------

        /// <summary>Third audit of E.4, KU9 — THE GATE'S LEVERS FIRST: a gate lever AND a shot lever both un-ticked,
        /// and the take is refused in the GATE's sentence — although in the take's sorted union the shot's lever
        /// (`SelectHero Knight`) comes before the gate's (`SkipIntro`). The gate's ticked, the shot's is said in the
        /// lever gate's own sentence.</summary>
        [Test]
        public void Lever_ThirdAuditKU9_AGateLeverAndAShotLeverBothUnticked_TheTakeIsRefusedInTheGATEsSentence()
        {
            Tick("SkipTutorial", "set Coins 5"); // NOT "SkipIntro" (the gate's), NOT "SelectHero Knight" (the shot's)
            var plan = Planned(GateText);
            Assert.AreEqual("SelectHero Knight",
                Levers.FirstUnticked(plan.TakeLeversNeeded(Load(ShotText), null), Levers.Approved(_root)),
                "precondition: in the sorted union the SHOT's lever comes first");
            Assert.AreEqual("SkipIntro", plan.LeverRefused(_root, Load(ShotText)));
            Assert.AreEqual("the tutorial gate stopped it: lever not approved on this machine: 'SkipIntro'",
                CaptureRun.RefusedBeforeTake(plan, _root, Load(ShotText)));
            Tick("SkipIntro");
            Assert.AreEqual("lever not approved on this machine: 'SelectHero Knight'",
                CaptureRun.RefusedBeforeTake(plan, _root, Load(ShotText)));
        }

        /// <summary>A shot a studio's own COMPILED adapter returns from <c>Shots()</c> — built in C#, in no file on
        /// disk. The auditor's: the same commands as <see cref="ShotText"/>.</summary>
        private static AdShot Compiled() => new AdShot("rec-me", new[] { "set Coins 5" },
            new[] { AdStep.Cheat("SelectHero Knight"), AdStep.Click("Go") }, WaitCondition.Present("Board"));

        /// <summary>Third audit of E.4, M1 — A TAKE REFUSED FOR A LEVER NO FILE ON DISK LISTS (a compiled shot's) leaves
        /// the studio A ROW TO TICK: the Nova Capture window's rows (<c>Levers.Rows(root,
        /// LeverGateBridge.RefusedThisSession)</c>, the window's own call) list it, un-ticked and tickable, as a
        /// command refused in this session — and ticked, the take is no longer refused. The auditor's case: the take
        /// is still refused before anything runs.</summary>
        [Test]
        public void Lever_ThirdAuditM1_ACompiledShotsRefusedLever_IsARowToTick_TickedTheTakeIsNoLongerRefused()
        {
            LeverGateBridge.ForgetRefused();
            Tick("SkipIntro", "SkipTutorial", "SelectHero Knight"); // NOT "set Coins 5"
            var plan = Planned(GateText);
            var shot = Compiled();
            CollectionAssert.DoesNotContain(Levers.Needed(_root).ToList(), "set Coins 5", "precondition: no file lists it");
            Assert.AreEqual("lever not approved on this machine: 'set Coins 5'", CaptureRun.RefusedBeforeTake(plan, _root, shot));
            var cheats = new RecordingBridge();
            var recorder = new FileRecorder(Path.Combine(_root, "takes"));
            Assert.IsNull(CaptureRun.Start(Adapter(cheats), shot, plan, _root, Tuning(recorder)));
            CollectionAssert.IsEmpty(cheats.Run_, "a command reached the game: " + string.Join(", ", cheats.Run_));
            CollectionAssert.IsEmpty(recorder.Starts);
            var rows = Levers.Rows(_root, LeverGateBridge.RefusedThisSession).Where(r => r.Command == "set Coins 5").ToList();
            Assert.AreEqual(1, rows.Count, "no row to tick for the lever the take was refused for");
            Assert.AreEqual(Levers.LeverSource.RefusedHere, rows[0].Source);
            Assert.IsFalse(rows[0].Approved);
            Assert.IsTrue(rows[0].CanTick);
            Assert.AreEqual(Levers.RefusedThisSessionNote, rows[0].Note);
            // ticked from that row (the window's own call): the take is no longer refused
            Assert.IsNull(Levers.SetApproved(_root, rows[0].Command, true));
            Assert.IsNull(CaptureRun.RefusedBeforeTake(plan, _root, shot));
        }

        /// <summary>Third audit of E.4, M1 — the other side: a JSON shot's lever (its file lists it) keeps the files' ONE
        /// row, and the session's list of refused commands is not touched.</summary>
        [Test]
        public void Lever_ThirdAuditM1_AJsonShotsRefusedLever_KeepsItsOneRow_TheSessionsListIsNotTouched()
        {
            LeverGateBridge.ForgetRefused();
            var plan = Planned(GateText);
            File.WriteAllBytes(RelayPaths.NovaShotsFile(_root), Utf8(@"{ ""$schemaVersion"": 1, ""shots"": [ " + ShotText + " ] }"));
            Tick("SkipIntro", "SkipTutorial", "SelectHero Knight"); // NOT "set Coins 5"
            Assert.AreEqual("lever not approved on this machine: 'set Coins 5'",
                CaptureRun.RefusedBeforeTake(plan, _root, Load(ShotText)));
            CollectionAssert.IsEmpty(LeverGateBridge.RefusedThisSession.ToList(), "a lever a file lists was noted again");
            var rows = Levers.Rows(_root, LeverGateBridge.RefusedThisSession).Where(r => r.Command == "set Coins 5").ToList();
            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual(Levers.LeverSource.Needed, rows[0].Source);
            // …and without a gate nothing is asked, and nothing is noted
            Assert.IsNull(CaptureRun.RefusedBeforeTake(null, _root, Load(ShotText)));
            CollectionAssert.IsEmpty(LeverGateBridge.RefusedThisSession.ToList());
        }

        /// <summary>Fourth audit of E.4, KM2 — A GATE'S lever no file on disk lists (a gate text other than the synced
        /// shots.json's) gets a row to tick too, not only a compiled shot's: the take is refused in the gate's sentence,
        /// and the window's rows list `SkipIntro` as refused in this session, un-ticked and tickable. Then the gate is
        /// listed in shots.json and the session's list forgotten: refused again, the lever is NOT noted — the file's
        /// row is its one row. The auditor's K2.</summary>
        [Test]
        public void Lever_FourthAuditKM2_AGateLeverNoFileLists_IsARowToTick_OnceAFileListsIt_NotNotedAgain()
        {
            LeverGateBridge.ForgetRefused();
            Tick("SkipTutorial", "set Coins 5", "SelectHero Knight"); // NOT "SkipIntro" (the gate's)
            var plan = Planned(GateText);
            CollectionAssert.DoesNotContain(Levers.Needed(_root).ToList(), "SkipIntro", "precondition: no file lists it");
            Assert.AreEqual(CaptureRun.GateLeverRefused("SkipIntro"), CaptureRun.RefusedBeforeTake(plan, _root, Load(ShotText)));
            CollectionAssert.AreEqual(new[] { "SkipIntro" }, LeverGateBridge.RefusedThisSession.ToList(),
                "the gate's lever was not noted");
            var rows = Levers.Rows(_root, LeverGateBridge.RefusedThisSession).Where(r => r.Command == "SkipIntro").ToList();
            Assert.AreEqual(1, rows.Count, "no row to tick for the gate's lever");
            Assert.AreEqual(Levers.LeverSource.RefusedHere, rows[0].Source);
            Assert.IsFalse(rows[0].Approved);
            Assert.IsTrue(rows[0].CanTick);
            // the gate listed in shots.json, the session's list forgotten: refused again, and NOT noted
            LeverGateBridge.ForgetRefused();
            File.WriteAllBytes(RelayPaths.NovaShotsFile(_root),
                Utf8(@"{ ""$schemaVersion"": 1, ""shots"": [ " + GateText + ", " + ShotText + " ] }"));
            Assert.AreEqual(CaptureRun.GateLeverRefused("SkipIntro"), CaptureRun.RefusedBeforeTake(plan, _root, Load(ShotText)));
            CollectionAssert.IsEmpty(LeverGateBridge.RefusedThisSession.ToList(), "a gate lever a file lists was noted again");
            rows = Levers.Rows(_root, LeverGateBridge.RefusedThisSession).Where(r => r.Command == "SkipIntro").ToList();
            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual(Levers.LeverSource.Needed, rows[0].Source);
        }

        /// <summary>Fourth audit of E.4, KM4 — the "a file lists it" guard compares ORDINAL, as the window's rows do
        /// (<c>Levers.Rows</c> dedupes Ordinal): a COMPILED shot's `set coins 5`, while shots.json lists `set Coins 5`,
        /// is another command. Refused, it gets its OWN row beside the file's, un-ticked and tickable — and ticked from
        /// that row, the take is no longer refused.</summary>
        [Test]
        public void Lever_FourthAuditKM4_ACompiledLeverDifferingOnlyInCaseFromAFilesLever_GetsItsOwnRow()
        {
            LeverGateBridge.ForgetRefused();
            File.WriteAllBytes(RelayPaths.NovaShotsFile(_root), Utf8(@"{ ""$schemaVersion"": 1, ""shots"": [ " + ShotText + " ] }"));
            Tick("SkipIntro", "SkipTutorial", "SelectHero Knight"); // NOT "set coins 5" (nor the file's "set Coins 5")
            var plan = Planned(GateText);
            var shot = new AdShot("rec-me", new[] { "set coins 5" },
                new[] { AdStep.Cheat("SelectHero Knight"), AdStep.Click("Go") }, WaitCondition.Present("Board"));
            var needed = Levers.Needed(_root).ToList();
            CollectionAssert.Contains(needed, "set Coins 5", "precondition: the file lists the other case");
            CollectionAssert.DoesNotContain(needed, "set coins 5", "precondition: no file lists this one");
            Assert.AreEqual("lever not approved on this machine: 'set coins 5'", CaptureRun.RefusedBeforeTake(plan, _root, shot));
            CollectionAssert.AreEqual(new[] { "set coins 5" }, LeverGateBridge.RefusedThisSession.ToList(),
                "a lever differing only in case from a file's was taken for the file's");
            var rows = Levers.Rows(_root, LeverGateBridge.RefusedThisSession).Where(r => r.Command == "set coins 5").ToList();
            Assert.AreEqual(1, rows.Count, "no row of its own for the case-differing lever");
            Assert.AreEqual(Levers.LeverSource.RefusedHere, rows[0].Source);
            Assert.IsFalse(rows[0].Approved);
            Assert.IsTrue(rows[0].CanTick);
            Assert.AreEqual(1, Levers.Rows(_root, LeverGateBridge.RefusedThisSession).Count(r => r.Command == "set Coins 5"),
                "the file's row is still its one row");
            Assert.IsNull(Levers.SetApproved(_root, rows[0].Command, true));
            Assert.IsNull(CaptureRun.RefusedBeforeTake(plan, _root, shot));
        }

        private const string BoardRigAdapter =
            @"{ ""gameId"": ""g"", ""camera"": { ""viewType"": ""BoardRig"", ""projection"": ""perspective"" } }";
        private const string PoseOtherType = "camera-pose SaveManager 1 2 3 4 5 6 60";
        private const string PoseSpecsType = "camera-pose BoardRig 1 2 3 4 5 6 60";

        /// <summary>Second audit of E.4, KM1 — a gated take's camera-pose Type guard is asked with THIS RUN's
        /// cloud flag (the gate is cloud content), not with the disk's answer: on a project whose files are its
        /// own, a pose of another Type than the camera block's view type is refused. The pose of the view type's
        /// own Type goes through.</summary>
        [Test]
        public void GatedTake_TheCameraPoseTypeGuard_IsAskedWithTheRunsCloudFlag_NotTheDisks()
        {
            File.WriteAllBytes(SyncNova.AdapterFile(_root), Utf8(BoardRigAdapter));
            Assert.IsFalse(Levers.GateActive(_root), "precondition: the disk says ungated");
            var options = CaptureRun.OptionsFor(_root, Planned(GateText).Gate)!;
            var inner = new RecordingBridge();
            var log = new List<string>();
            var wrapped = options.WrapCheats!(inner, log.Add);
            Assert.IsFalse(wrapped.Run(PoseOtherType), "a gated take's guard answered with the disk's 'ungated'");
            CollectionAssert.AreEqual(new[] { CameraPose.TypeIsNotTheSpecsViewType("SaveManager", "BoardRig") }, log);
            CollectionAssert.IsEmpty(inner.Run_, "the refused pose reached the game");
            // CONTROL: the view type's own pose goes through to the lever gate underneath
            Assert.IsTrue(wrapped.Run(PoseSpecsType));
            CollectionAssert.AreEqual(new[] { PoseSpecsType }, inner.Run_);
        }

        // ---- ruling 2: the gated take ---------------------------------------------------------------

        [Test]
        public void GatedTake_RunsTheGateFirst_RecordingOff_ThenRecordsTheShot_AndReleasesTheCameraHoldAfterTheGate()
        {
            Tick("set Coins 5", "SelectHero Knight", "SkipIntro", "SkipTutorial");
            var cheats = new RecordingBridge();
            var recorder = new FileRecorder(Path.Combine(_root, "takes"), cheats.Run_);
            var releases = 0;
            var d = Pump(CaptureRun.Start(Adapter(cheats), Load(ShotText), Planned(GateText), _root,
                Tuning(recorder, () => releases++)));
            CollectionAssert.AreEqual(
                new[] { "SkipIntro", "SkipTutorial", "set Coins 5", "REC rec-me", "SelectHero Knight" },
                Writes(cheats), d.Summary);
            CollectionAssert.AreEqual(new[] { "rec-me" }, recorder.Starts, "the gate was recorded, or the shot was not");
            Assert.IsTrue(d.PreambleRan);
            Assert.IsTrue(d.AllCaptured, d.Summary);
            Assert.IsNull(CaptureRun.TakeFailure(d), "a captured take is looked for and uploaded");
            // the gate's release, the shot's, Finish's
            Assert.AreEqual(3, releases);
        }

        [Test]
        public void GatedTake_RunsUnderTheForcedLeverGate_ATrysGate_WhateverTheFilesOnDiskSay()
        {
            var options = CaptureRun.OptionsFor(_root, Planned(GateText).Gate)!;
            Assert.IsTrue(options.CloudContent);
            Assert.AreEqual("tutorial-gate", options.Preamble!.Name);
            Assert.IsInstanceOf<ProbeCameraTypeGuard>(options.WrapCheats!(new RecordingBridge(), _ => { }));
            Assert.AreEqual(2, options.MaxAttempts, "every other setting is a recording's default");
            Assert.IsNull(options.OnStopped);
            Assert.IsNull(options.Vision);
            Assert.IsNull(options.Recorder);
            // this project's files are its own (no synced.json): the disk says ungated — and the gate's
            // un-ticked cheat is refused anyway (the last door bypassed to reach the run's own gate)
            Assert.IsFalse(Levers.GateActive(_root), "precondition: the disk says ungated");
            Tick("set Coins 5", "SelectHero Knight", "SkipIntro");
            var cheats = new RecordingBridge();
            var d = Pump(AdDirector.Run(Adapter(cheats), new[] { Load(ShotText) },
                CaptureRun.OptionsFor(_root, Planned(GateText).Gate,
                    Tuning(new FileRecorder(Path.Combine(_root, "takes"))))));
            CollectionAssert.DoesNotContain(cheats.Run_, "SkipTutorial");
            Assert.AreEqual(AdDirector.FailedKindPreamble, d.FailedStepKindName, d.Summary);
            Assert.AreEqual(0, d.PreambleFailedStepIndex);
        }

        // ---- ruling 4: a stop at the gate fails THAT take -------------------------------------------

        [Test]
        public void GateStop_FailsTheTakeInTheOneSentence_NoClipIsMade_NothingIsQuarantined_NothingToUpload()
        {
            Tick("set Coins 5", "SelectHero Knight", "SkipIntro", "SkipTutorial");
            var dir = Path.Combine(_root, "takes");
            var recorder = new FileRecorder(dir);
            // an EARLIER real take of the shot is in the folder: a quarantine would rename it
            File.WriteAllText(Path.Combine(dir, "rec-me_001.mp4"), "yesterday's take");
            File.SetLastWriteTimeUtc(Path.Combine(dir, "rec-me_001.mp4"), DateTime.UtcNow.AddHours(-1));
            var before = Files(dir);
            var startedAt = DateTime.UtcNow.AddSeconds(-1);
            const string stops = @"{ ""name"": ""tutorial-gate"", ""setup"": [""SkipIntro""],
  ""steps"": [ { ""kind"": ""wait"", ""seconds"": 0.2 },
               { ""kind"": ""click"", ""name"": ""SkipBTN"", ""timeout"": 1 } ],
  ""settle"": { ""kind"": ""present"", ""name"": ""Board"" } }";
            var d = Pump(CaptureRun.Start(Adapter(new RecordingBridge()), Load(ShotText), Planned(stops), _root,
                Tuning(recorder)));
            Assert.AreEqual("the tutorial gate stopped it at step 2: tutorial-gate: Click 'SkipBTN' did not resolve",
                CaptureRun.TakeFailure(d), d.Summary);
            Assert.AreNotEqual("take failed: " + CaptureRun.LastLine(d.Summary), CaptureRun.TakeFailure(d),
                "the gate's stop said as the run log's last line");
            CollectionAssert.IsEmpty(recorder.Starts, "the recorder started");
            CollectionAssert.AreEqual(before, Files(dir), "the take folder changed: a clip was made, or one was quarantined");
            Assert.IsNull(ClipFinder.Newest(dir, "rec-me", startedAt), "a clip of this take would be uploaded");

            // its END STATE not holding: the one sentence with no step to name
            const string never = @"{ ""name"": ""tutorial-gate"", ""setup"": [""SkipIntro""],
  ""steps"": [ { ""kind"": ""cheat"", ""command"": ""SkipTutorial"" } ],
  ""settle"": { ""kind"": ""present"", ""name"": ""Never"" }, ""settleTimeoutSec"": 1 }";
            var e = Pump(CaptureRun.Start(Adapter(new RecordingBridge()), Load(ShotText), Planned(never), _root,
                Tuning(recorder)));
            Assert.AreEqual(
                "the tutorial gate stopped it: tutorial-gate: every step ran, but settle Present 'Never' did not hold within 1s",
                CaptureRun.TakeFailure(e), e.Summary);
            CollectionAssert.AreEqual(before, Files(dir));

            // CONTROL — the same rig WITHOUT a gate, the shot failing on its own: today's sentence, and the
            // quarantine is live here (so "nothing was quarantined" above is a reading, not a blind rig)
            var failing = Load(ShotText.Replace(@"""name"": ""Go""", @"""name"": ""Gone"", ""timeout"": 1"));
            var f = Pump(CaptureRun.Start(Adapter(new RecordingBridge()), failing, null, _root, Tuning(recorder)));
            Assert.AreEqual("take failed: " + CaptureRun.LastLine(f.Summary), CaptureRun.TakeFailure(f));
            Assert.IsTrue(Files(dir).Any(n => n.StartsWith("rec-me_FAILED_", StringComparison.Ordinal)),
                "control: the quarantine did not run on a failed recorded take: " + string.Join(", ", Files(dir)));
        }

        // ---- ruling 5: the step marks are the shot's --------------------------------------------------

        [Test]
        public void GatedTake_TheStepMarksTheCompilerReadsAreTheShotsOnly()
        {
            Tick("set Coins 5", "SelectHero Knight", "SkipIntro", "SkipTutorial");
            var d = Pump(CaptureRun.Start(Adapter(new RecordingBridge()), Load(ShotText), Planned(GateText), _root,
                Tuning(new FileRecorder(Path.Combine(_root, "takes")))));
            Assert.IsTrue(d.PreambleRan);
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, d.StepMarks.Select(m => m.Index).ToArray(), d.StepMarksJson);
            CollectionAssert.AreEqual(new[] { "Cheat", "Click", "settled" }, d.StepMarks.Select(m => m.Kind).ToArray());
            Assert.IsFalse(d.StepMarks.Any(m => m.Label.Contains("SkipTutorial")), "a mark of the GATE's step: " + d.StepMarksJson);
            Assert.AreEqual(0.0, d.StepMarks[0].AtSec, 1e-9, "the first mark is not at the recording's start");
            Assert.AreEqual("rec-me", (string?)JObject.Parse(d.StepMarksJson!)["shot"]);
        }

        // ---- ruling 3: NO GATE = TODAY, byte for byte ---------------------------------------------------

        private sealed class Outcome
        {
            public string Summary = "";
            public string[] Commands = Array.Empty<string>();
            public string? Marks;
            public string[] Files = Array.Empty<string>();
            public string? Failure;
            public bool Captured;
        }

        private Outcome Recorded(AdShot shot, bool today, string dir)
        {
            var cheats = new RecordingBridge();
            var recorder = new FileRecorder(dir);
            _clock = 0;
            var d = Pump(today
                ? AdDirector.Run(Adapter(cheats), new[] { shot }, Tuning(recorder))
                : CaptureRun.Start(Adapter(cheats), shot, null, _root, Tuning(recorder)));
            var o = new Outcome
            {
                Summary = d.Summary,
                Commands = cheats.Run_.ToArray(),
                Marks = d.StepMarksJson,
                Files = Files(dir),
                Failure = CaptureRun.TakeFailure(d),
                Captured = d.AllCaptured,
            };
            AdDirector.Active?.Finish("next run");
            LeverGateBridge.ForgetRefused();
            return o;
        }

        [Test]
        public void NoGate_TheTakeIsTodaysCall_TheSameSummary_StepMarks_LeverBehaviour_AndClipLookup()
        {
            Assert.IsNull(CaptureRun.OptionsFor(_root, null), "a recording without a gate must hand the director NO options");
            var tuning = Tuning(new FileRecorder(Path.Combine(_root, "t")));
            Assert.AreSame(tuning, CaptureRun.OptionsFor(_root, null, tuning));
            Assert.IsNull(tuning.Preamble);
            Assert.IsFalse(tuning.CloudContent);
            Assert.IsNull(tuning.WrapCheats);
            Assert.AreEqual(2, tuning.MaxAttempts);

            // this project's files are its own: un-ticked commands run, as they always did here
            Assert.IsFalse(Levers.GateActive(_root), "precondition: the disk says ungated");
            var scenarios = new (string Name, string Text)[]
            {
                ("reaches", ShotText),
                ("settle never holds (two attempts, each quarantined)",
                    ShotText.Replace(@"""name"": ""Board"" } }", @"""name"": ""Never"" }, ""settleTimeoutSec"": 1 }")),
                ("a step fails", ShotText.Replace(@"""name"": ""Go""", @"""name"": ""Gone"", ""timeout"": 1")),
            };
            var i = 0;
            foreach (var (name, text) in scenarios)
            {
                i++;
                var a = Recorded(Load(text), today: true, Path.Combine(_root, $"today-{i}"));
                var b = Recorded(Load(text), today: false, Path.Combine(_root, $"now-{i}"));
                Assert.AreEqual(a.Summary, b.Summary, name);
                CollectionAssert.AreEqual(a.Commands, b.Commands, name);
                Assert.AreEqual(a.Marks, b.Marks, name);
                CollectionAssert.AreEqual(a.Files, b.Files, name);
                Assert.AreEqual(a.Failure, b.Failure, name);
                Assert.AreEqual(a.Captured, b.Captured, name);
            }
            // …and what "today" IS, so the comparison is not of two things equally wrong
            var reached = Recorded(Load(ShotText), today: false, Path.Combine(_root, "anchor-1"));
            Assert.IsTrue(reached.Captured);
            Assert.IsNull(reached.Failure);
            CollectionAssert.Contains(reached.Commands, "SelectHero Knight", "an un-ticked command of the studio's own files ran");
            StringAssert.Contains(@"""shot"":""rec-me""", reached.Marks);
            var retried = Recorded(Load(scenarios[1].Text), today: false, Path.Combine(_root, "anchor-2"));
            StringAssert.Contains("RETRY rec-me", retried.Summary, "two attempts");
            CollectionAssert.AreEqual(new[] { "rec-me_FAILED_1.mp4", "rec-me_FAILED_2.mp4" }, retried.Files);
            StringAssert.StartsWith("take failed: FAIL  rec-me", retried.Failure);
        }

        // ---- the one sentence, and uiDriver ------------------------------------------------------------

        [Test]
        public void StopSentence_IsTheWordsTheSitesOwnSentenceForAGateStopBeginsWith()
        {
            // the API's `stopRefusalOf` for a gate stop: "the tutorial gate stopped it at step N: <reason> — fix …"
            Assert.AreEqual("the tutorial gate stopped it at step 2: r", ProbeGate.StopSentence(1, "r"));
            Assert.AreEqual("the tutorial gate stopped it: r", ProbeGate.StopSentence(null, "r"));
            Assert.AreEqual("the tutorial gate stopped it at step 1: no reason was recorded", ProbeGate.StopSentence(0, null));
        }

        [Test]
        public void UiDriver_TheKitsOwnFinder_OrTheAdaptersOwn_AlwaysInTheFacts()
        {
            Assert.AreEqual("kit", ProbeFacts.UiDriverOf(new GameAdapter()), "no driver set: the kit's is used");
            Assert.AreEqual("kit", ProbeFacts.UiDriverOf(new GameAdapter { Ui = new UguiDriver() }), "the JSON adapter sets the kit's");
            Assert.AreEqual("custom", ProbeFacts.UiDriverOf(new GameAdapter { Ui = new OtherUi() }));
            JObject Built(string? driver) => driver == null
                ? ProbeFacts.Build("s", true, null, null, null, null, null, 1, 1, "aa", "bb", null, null, null, 1f, true,
                    null, "0.8.0")
                : ProbeFacts.Build("s", true, null, null, null, null, null, 1, 1, "aa", "bb", null, null, null, 1f, true,
                    null, "0.8.0", uiDriver: driver);
            Assert.AreEqual("kit", Built(null)["uiDriver"]!.Value<string>());
            Assert.AreEqual("custom", Built(ProbeFacts.UiDriverCustom)["uiDriver"]!.Value<string>());
            Assert.AreEqual(JTokenType.String, Built(null)["uiDriver"]!.Type);
        }
    }
}
