using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// The fresh review of learn-and-drive v3 P2 (2026-09-26) — its minor findings, each through the REAL gate on a real temp
    /// project: (1) a <c>camera-spec …</c> lever meets the risk rule; (2) the ready gate says WHY a muteGet was refused;
    /// (4) a template ticked before 0.10.0 is named, with what to do, wherever an uncovered command is refused or listed.
    /// Finding 3 (a gated take's risky pre-check) is in <see cref="CaptureGateTests"/>, finding 2's director run in
    /// <see cref="DeliveredGetTests"/>, finding 1's pose in <see cref="CameraPoseTests"/>.
    /// </summary>
    public class KitSafetyP2ReviewTests
    {
        private string _root = "";

        private const string RiskyCamera =
            @"{ ""gameId"": ""g"", ""camera"": { ""viewType"": ""Rig"", ""projection"": ""perspective"", ""setRotation"": ""DeleteSave"" } }";
        private const string QuietCamera =
            @"{ ""gameId"": ""g"", ""camera"": { ""viewType"": ""Rig"", ""projection"": ""perspective"" } }";
        private const string Pose = "camera-pose 1 2 3 4 5 6 60";

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "p2r-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RelayPaths.NovaDir(_root));
            LeverGateBridge.ForgetRefused();
            Levers.ForgetRowsForTests();
        }

        [TearDown]
        public void TearDown()
        {
            LeverGateBridge.ForgetRefused();
            Levers.ForgetRowsForTests();
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }

        private sealed class RecordingBridge : ICheatBridge
        {
            public readonly List<string> Ran = new();
            public bool Run(string command) { Ran.Add(command); return true; }
        }

        private (LeverGateBridge gate, RecordingBridge inner, List<string> log) Gate()
        {
            var inner = new RecordingBridge();
            var log = new List<string>();
            return (new LeverGateBridge(inner, _root, log.Add, cloudContent: true), inner, log);
        }

        private void Tick(string command) => Assert.IsNull(Levers.SetApproved(_root, command, true), "the tick was written");

        private void WriteAdapter(string json) => File.WriteAllText(SyncNova.AdapterFile(_root), json);

        private string SpecLever() => Levers.CameraSpecLever(AdapterJson.Load(_root).Camera!.Value);

        // ---- finding 1: camera-spec meets the risk rule ------------------------------------------------------------

        [Test]
        public void ATickedCameraSpecWhoseMethodNamesAreRiskyIsHeldBackUntilTheNonProductionTick()
        {
            WriteAdapter(RiskyCamera);
            var spec = SpecLever();
            Assert.AreEqual("camera-spec Rig SetPosition DeleteSave SetFov", spec, "control: the lever the gate asks for");
            Tick(Pose);
            Tick(spec);
            var (gate, inner, log) = Gate();

            Assert.IsFalse(gate.Run(Pose), "a ticked pose ran methods a risky ticked spec names, with no non-production tick");
            CollectionAssert.IsEmpty(inner.Ran);
            CollectionAssert.Contains(log, CheatRisk.NotYetLog(spec, "the word root 'delet'"));
            Assert.AreEqual(GateRefusal.RiskyNotYet, gate.LastRefusal);
            Assert.AreEqual(spec, gate.LastRefusedLever, "the refusal names the spec, not the pose");

            // the pre-check a try and a take ask, and the window's row, read the same rule
            Assert.AreEqual(spec, Levers.FirstRiskyNotYet(new[] { Pose, spec }, _root)?.Lever);
            Assert.AreEqual(CheatRisk.NotYetLog(spec, "the word root 'delet'"), Levers.LeverRefusal(_root, spec, cloudContent: true));
            var row = Levers.Rows(_root, null).Single(r => r.Command == spec);
            Assert.IsNotNull(row.RiskyNotYet, "the window's row does not say the spec is held back");

            Assert.IsNull(Levers.SetNonProduction(_root, true));
            Assert.IsTrue(gate.Run(Pose), "the person said this editor talks to a non-production server");
            CollectionAssert.AreEqual(new[] { Pose }, inner.Ran);
            Assert.IsNull(Levers.LeverRefusal(_root, spec, cloudContent: true));
        }

        [Test]
        public void AQuietCameraSpecRunsWithoutTheNonProductionTick_AndHideOverlayIsStillNeverRisky()
        {
            // CONTROL: the refusal above is the spec's RISKY WORD, not camera-spec in general
            WriteAdapter(QuietCamera);
            var spec = SpecLever();
            Tick(Pose);
            Tick(spec);
            var (gate, inner, _) = Gate();
            Assert.IsTrue(gate.Run(Pose));
            CollectionAssert.AreEqual(new[] { Pose }, inner.Ran);
            Assert.IsNull(Levers.RiskyNotYet(Levers.HideOverlayLever("PurchasePopup"), false, null),
                "an overlay's type name is not a method the kit calls");
        }

        [Test]
        public void AnUntickedCameraSpecIsSaidAsNotTicked_NotAsRisky()
        {
            WriteAdapter(RiskyCamera);
            var spec = SpecLever();
            Tick(Pose);
            var (gate, _, log) = Gate();
            Assert.IsFalse(gate.Run(Pose));
            Assert.AreEqual(GateRefusal.NotTicked, gate.LastRefusal);
            CollectionAssert.Contains(log, Levers.NotApprovedLog(spec));
            Assert.AreEqual(Levers.NotApprovedLog(spec), Levers.LeverRefusal(_root, spec, cloudContent: true));
        }

        // ---- finding 2: the ready gate's sentence carries the refusal's own reason ----------------------------------

        [Test]
        public void TheMuteGetSentenceSaysTheRefusalsOwnReason()
        {
            const string read = "get Audio.serverMuted";
            var notTicked = HygieneReadyGate.MuteGetRefusedLog(read, GateRefusal.NotTicked, read, null);
            var risky = HygieneReadyGate.MuteGetRefusedLog(read, GateRefusal.RiskyNotYet, read, "the word root 'server'");
            var press = HygieneReadyGate.MuteGetRefusedLog(read, GateRefusal.PressGuard, read, "refused: 'Reset'");

            StringAssert.Contains("is not ticked on this machine", notTicked);
            StringAssert.Contains("tick it in Nova Capture", notTicked);
            StringAssert.DoesNotContain("is not ticked", risky, "a ticked read held back as risky is not 'not ticked'");
            StringAssert.Contains("held back as risky (the word root 'server')", risky);
            StringAssert.Contains(CheatRisk.NonProductionLabel, risky);
            StringAssert.DoesNotContain("is not ticked", press);
            StringAssert.Contains("press guard (refused: 'Reset')", press);
            StringAssert.Contains("no tick changes that", press);
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

        /// <summary>A gated take and a try wrap the gate in the camera-pose Type guard (<c>CaptureRun.OptionsFor</c>); the
        /// ready gate looks through it, so a risky muteGet refused there is said as risky, never as "could not read".</summary>
        [TestCase(true)]
        [TestCase(false)]
        public void TheMuteGetReasonIsSaidThroughTheCameraTypeGuardToo(bool wrapped)
        {
            const string read = "get Audio.serverMuted";
            Tick(read);
            var (bridge, inner, _) = Gate();
            ICheatBridge cheats = wrapped ? new ProbeCameraTypeGuard(bridge, _root, null, cloudContent: true) : bridge;
            var log = new List<string>();
            var ctx = new DirectorContext(new NoUi(), NullStateProbe.Instance, cheats, () => 0, log.Add);
            var ready = new HygieneReadyGate(() => new HygieneSpec { MuteGet = "Audio.serverMuted", Declared = true },
                () => 1f, _ => { }, () => null);
            foreach (var _ in ready.WaitUntilReady(ctx)) { }

            CollectionAssert.IsEmpty(inner.Ran, "the risky read reached the game");
            Assert.IsFalse(ctx.LastOpSucceeded);
            Assert.IsTrue(log.Any(l => l.Contains($"'{read}' is ticked but held back as risky (the word root 'server')")),
                string.Join("\n", log));
            Assert.IsFalse(log.Any(l => l.Contains("could not read muteGet")), string.Join("\n", log));
        }

        [Test]
        public void TheGateRecordsWhyItRefused_AndForgetsItOnTheNextRun()
        {
            Tick("raw ResetUser");
            Tick("click Reset Progress");
            Levers.SetNonProduction(_root, false);
            var (gate, _, _) = Gate();
            Assert.IsFalse(gate.Run("raw ResetUser"));
            Assert.AreEqual(GateRefusal.RiskyNotYet, gate.LastRefusal);
            Assert.AreEqual("the word root 'reset'", gate.LastRefusalDetail);
            Assert.IsFalse(gate.Run("raw Nope"));
            Assert.AreEqual(GateRefusal.NotTicked, gate.LastRefusal);
            Assert.IsNull(gate.LastRefusalDetail);
            Assert.IsNull(Levers.SetNonProduction(_root, true));
            Assert.IsFalse(gate.Run("click Reset Progress"), "a ticked press by name with a risky word is fenced");
            Assert.AreEqual(GateRefusal.PressGuard, gate.LastRefusal);
            Assert.IsTrue(gate.Run("raw ResetUser"));
            Assert.AreEqual(GateRefusal.None, gate.LastRefusal);
            Assert.IsFalse(gate.LastRunRefused);
        }

        // ---- finding 4: a template ticked before 0.10.0 is named, with what to do -----------------------------------

        private void WriteOldLevers(params string[] approved) =>
            File.WriteAllText(Levers.FilePath(_root),
                "{ \"$schemaVersion\": 1, \"approved\": [" + string.Join(", ", approved.Select(a => "\"" + a + "\"")) + "] }");

        [Test]
        public void TheBridgesRefusalNamesTheOldTemplateAndSaysToTickEachFixedCommand()
        {
            WriteOldLevers("SelectHero {hero}");
            var (gate, inner, log) = Gate();
            Assert.IsFalse(gate.Run("SelectHero Knight"), "a template covers nothing since 0.10.0");
            CollectionAssert.IsEmpty(inner.Ran);
            var line = log.Single();
            StringAssert.StartsWith(Levers.NotApprovedLog("SelectHero Knight"), line, "the gate's own sentence still leads");
            StringAssert.Contains("the ticked template `SelectHero {hero}` no longer covers filled-in commands", line);
            StringAssert.Contains("tick each fixed command in Nova Capture, e.g. `SelectHero Knight`", line);

            // CONTROL: a command no ticked template ever covered is said plainly
            log.Clear();
            Assert.IsFalse(gate.Run("raw Other"));
            CollectionAssert.AreEqual(new[] { Levers.NotApprovedLog("raw Other") }, log);
            // …and one of another verb, or with too many words, was never covered by that template either
            Assert.IsNull(Levers.FormerTemplateCovering(new[] { "SelectHero {hero}" }, "PickHero Knight"));
            Assert.IsNull(Levers.FormerTemplateCovering(new[] { "SelectHero {hero}" }, "SelectHero Knight Extra"));
        }

        [Test]
        public void TheWindowSaysTheOldTemplateNoLongerCovers_OnBothRows()
        {
            File.WriteAllText(RelayPaths.NovaShotsFile(_root),
                @"{ ""$schemaVersion"": 1, ""shots"": [ { ""name"": ""s"", ""steps"": [ { ""kind"": ""cheat"", ""command"": ""SelectHero Knight"" } ] } ] }");
            WriteOldLevers("SelectHero {hero}");
            var rows = Levers.Rows(_root, null);

            var knight = rows.Single(r => r.Command == "SelectHero Knight");
            Assert.IsFalse(knight.Approved);
            Assert.IsTrue(knight.CanTick, "the fix is to tick this row");
            StringAssert.Contains(Levers.TemplateNoLongerCovers("SelectHero {hero}", "SelectHero Knight"), knight.Note);

            var template = rows.Single(r => r.Command == "SelectHero {hero}");
            Assert.AreEqual(Levers.TickedTemplateNote("SelectHero Knight"), template.Note);
            StringAssert.DoesNotContain("cannot be approved here", template.Note, "it WAS approved; it now approves nothing");
            Assert.IsTrue(template.CanTick, "whatever is in the file can be unticked");

            // CONTROL: ticked as a fixed command, the row is approved and the migration note is gone
            Tick("SelectHero Knight");
            knight = Levers.Rows(_root, null).Single(r => r.Command == "SelectHero Knight");
            Assert.IsTrue(knight.Approved);
            StringAssert.DoesNotContain("no longer covers", knight.Note);
        }
    }
}
