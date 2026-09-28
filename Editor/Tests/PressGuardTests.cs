using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// Learn-and-drive v3, spike A part 2 — the press guard and the kit-job gate. The director seam tests drive
    /// the REAL director (AdDirector.Run → DoStep), because the guard lives at the press, not beside it.
    /// </summary>
    public class PressGuardTests
    {
        private string _root = "";

        [SetUp]
        public void SetUp()
        {
            // A project with NO Library/Nova files at all — the shape where the ordinary gate is OFF (local
            // authorship) and a kit job must still be gated.
            _root = Path.Combine(Path.GetTempPath(), "pressguard-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }

        // ---- the word fence ----

        [TestCase("DeleteAccount", "delet")]
        [TestCase("Buy_All_Packs@Buy All Packs!", "buy")]
        [TestCase("RestorePurchases", "restor")]
        [TestCase("Settings@Log out", "logout")]
        [TestCase("ResetDevUser", "reset")]
        [TestCase("IAPButton", "iap")]
        [TestCase("Btn@Subscribe", "subscri")]
        [TestCase("AccountPopup", "account")]
        // Review finding 1: every spelling of "log out" / "sign out", not only the spaced one.
        [TestCase("LogOutButton", "logout")]
        [TestCase("SignOut", "signout")]
        [TestCase("Sign-out", "signout")]
        [TestCase("Log_out", "logout")]
        [TestCase("Btn_Sign_out", "signout")]
        [TestCase("Settings@Sign Out", "signout")]
        [TestCase("Pay_Button", "pay")]
        [TestCase("SpendGems", "spend")]
        public void ARiskyTarget_IsNamedByItsRoot(string target, string root) =>
            Assert.AreEqual(root, PressGuard.RiskyRoot(target));

        /// <summary>CONTROL: ordinary lobby buttons from Rogue Legend's real ui-dump (spike A part 1) and words that
        /// only CONTAIN a root mid-word are not refused.</summary>
        [TestCase("Close_Button")]
        [TestCase("Equip_Tab@Equip")]
        [TestCase("CloseArea")]
        [TestCase("Tab_Daily_Button@Daily")]
        [TestCase("Preset")]
        [TestCase("Display")]
        [TestCase("Adventure_Tab@Adventure")]
        [TestCase("LoginButton")]
        [TestCase("Signal_Tab")]
        [TestCase("LogsOutbox")]
        [TestCase("Designer@Sign in")]
        public void AnOrdinaryTarget_IsNotRisky(string target) =>
            Assert.IsNull(PressGuard.RiskyRoot(target));

        [Test]
        public void TheAllowlist_MatchesTheNamePartOnly()
        {
            var allow = new[] { "Equip_Tab" };
            Assert.IsNull(PressGuard.Refusal("Equip_Tab@Equip", allow));
            StringAssert.Contains("not on this run's allowed names", PressGuard.Refusal("Shop_Tab", allow));
        }

        [Test]
        public void TheAllowlistName_IsTrimmedLikeTheDriverTrimsIt() =>
            Assert.IsNull(PressGuard.Refusal("Equip_Tab @Equip", new[] { "Equip_Tab" }));

        [Test]
        public void TheWordFence_HoldsEvenForAnAllowlistedName()
        {
            // An allowlist cannot open the second fence: a need that "matched" a buy button still does not press it.
            StringAssert.Contains("risky word root 'buy'", PressGuard.Refusal("BuyButton", new[] { "BuyButton" }));
        }

        // ---- the director seam ----

        private sealed class FakeUi : IUiDriver
        {
            public readonly HashSet<string> Names = new();
            public readonly List<string> Pressed = new();
            public bool Exists(string name) => Names.Contains(name);
            public bool IsInteractable(string name) => Names.Contains(name);
            public string? GetText(string name) => null;
            public bool Click(string name, int index = 0) { Pressed.Add("click " + name); return true; }
            public void PointerDown(string name, int index = 0) => Pressed.Add("down " + name);
            public void PointerUp(string name, int index = 0) => Pressed.Add("up " + name);
        }

        private sealed class RecordingBridge : ICheatBridge
        {
            public readonly List<string> Run_ = new();
            public bool Run(string command) { Run_.Add(command); return true; }
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

        private (AdDirector d, FakeUi ui, RecordingBridge cheats) Run(AdStep[] steps, AdDirector.Options options)
        {
            var ui = new FakeUi();
            ui.Names.UnionWith(new[] { "Board", "BuyButton", "Close_Button", "Shop_Tab", "Equip_Tab" });
            var cheats = new RecordingBridge();
            var shot = new AdShot("s", setup: Array.Empty<string>(), steps: steps, settle: WaitCondition.Present("Board"));
            var adapter = new GameAdapter { GameId = "g", Ui = ui, CheatBridge = cheats, Shots = () => new[] { shot } };
            var clock = 0.0;
            options.AutoPump = false;
            options.MaxAttempts = 1;
            options.Now = () => clock;
            options.Recorder = new FakeDriver(Path.Combine(_root, "takes"));
            options.ReleaseCameraHold = () => { };
            options.ProjectRoot ??= _root;
            var d = AdDirector.Run(adapter, new[] { shot }, options);
            Assert.IsNotNull(d);
            var ticks = 0;
            while (!d!.IsFinished && ticks++ < 100_000) { d.PumpOnce(); clock += 0.1; }
            Assert.IsTrue(d.IsFinished, "director never finished");
            return (d, ui, cheats);
        }

        // A refused press FAILS its step, and a failed step ends the attempt — so each positive control is its own run.
        [Test]
        public void AKitJob_NeverPressesARiskyButton()
        {
            var (d, ui, _) = Run(new[] { AdStep.Click("BuyButton") }, KitJobRun.DirectorOptions(_root));
            CollectionAssert.DoesNotContain(ui.Pressed, "click BuyButton");
            StringAssert.Contains("PRESS REFUSED: 'BuyButton'", d.Summary);
        }

        /// <summary>CONTROL: the same kit job presses a safe button — so the refusal above is the word fence.</summary>
        [Test]
        public void AKitJob_PressesASafeButton()
        {
            var (_, ui, _) = Run(new[] { AdStep.Click("Close_Button") }, KitJobRun.DirectorOptions(_root));
            CollectionAssert.Contains(ui.Pressed, "click Close_Button");
        }

        [Test]
        public void AKitJob_NeverHoldsARiskyButton()
        {
            var (_, ui, _) = Run(new[] { AdStep.Hold("BuyButton", 0.2) }, KitJobRun.DirectorOptions(_root));
            Assert.IsFalse(ui.Pressed.Any(p => p.EndsWith("BuyButton")), string.Join(", ", ui.Pressed));
        }

        /// <summary>CONTROL for the hold refusal: a safe hold is held (down, then up).</summary>
        [Test]
        public void AKitJob_HoldsASafeButton()
        {
            var (_, ui, _) = Run(new[] { AdStep.Hold("Close_Button", 0.2) }, KitJobRun.DirectorOptions(_root));
            CollectionAssert.AreEqual(new[] { "down Close_Button", "up Close_Button" }, ui.Pressed);
        }

        // ---- review finding 2: the DELIVERED-project path (no CloudContent) — the menu Record-all, the relay run-shot and
        // an ungated capture are fenced by this path in production, not by the cloud flag.

        private static void WriteUtf8(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new System.Text.UTF8Encoding(false).GetBytes(text));
        }

        /// <summary>Put the temp project in the state the ordinary gate is live in: the shots on disk ARE the ones the last
        /// sync wrote (the same shape as LeversTests.MarkSynced).</summary>
        private void MarkDelivered()
        {
            WriteUtf8(RelayPaths.NovaShotsFile(_root), "{\"shots\":[]}");
            WriteUtf8(SyncNova.SyncedFile(_root), new Newtonsoft.Json.Linq.JObject
            {
                ["shotsSha256"] = SyncNova.Sha256OfFile(RelayPaths.NovaShotsFile(_root)) ?? "",
                ["adapterSha256"] = SyncNova.Sha256OfFile(SyncNova.AdapterFile(_root)) ?? "",
                ["runId"] = "run-1",
                ["at"] = DateTime.UtcNow.ToString("o"),
            }.ToString());
        }

        [Test]
        public void ADeliveredProject_ItsOwnRunIsFenced()
        {
            MarkDelivered();
            Assert.IsTrue(Levers.GateActive(_root), "precondition: the ordinary gate is live on a delivered project");
            var (d, ui, _) = Run(new[] { AdStep.Click("BuyButton") }, new AdDirector.Options());
            CollectionAssert.DoesNotContain(ui.Pressed, "click BuyButton");
            StringAssert.Contains("PRESS REFUSED: 'BuyButton'", d.Summary);
        }

        /// <summary>CONTROL: on the same delivered project a safe press still runs.</summary>
        [Test]
        public void ADeliveredProject_StillPressesASafeButton()
        {
            MarkDelivered();
            var (_, ui, _) = Run(new[] { AdStep.Click("Close_Button") }, new AdDirector.Options());
            CollectionAssert.Contains(ui.Pressed, "click Close_Button");
        }

        [Test]
        public void AKitJobWithAnAllowlist_RefusesANameNotOnIt()
        {
            var (d, ui, _) = Run(new[] { AdStep.Click("Shop_Tab") }, KitJobRun.DirectorOptions(_root, new[] { "Equip_Tab" }));
            CollectionAssert.DoesNotContain(ui.Pressed, "click Shop_Tab");
            StringAssert.Contains("not on this run's allowed names", d.Summary);
        }

        /// <summary>CONTROL: the allowlisted name is pressed under the same allowlist.</summary>
        [Test]
        public void AKitJobWithAnAllowlist_PressesItsName()
        {
            var (_, ui, _) = Run(new[] { AdStep.Click("Equip_Tab") }, KitJobRun.DirectorOptions(_root, new[] { "Equip_Tab" }));
            CollectionAssert.Contains(ui.Pressed, "click Equip_Tab");
        }

        /// <summary>CONTROL: a person's own local run (no cloud content, nothing delivered) keeps today's behaviour —
        /// the person at the keyboard wrote it. Without this, the guard could be passing by refusing everything.</summary>
        [Test]
        public void ALocalRun_IsNotFenced()
        {
            var (_, ui, _) = Run(new[] { AdStep.Click("BuyButton") }, new AdDirector.Options());
            CollectionAssert.Contains(ui.Pressed, "click BuyButton");
        }

        /// <summary>A6 blocker 2: on a project with NO delivered files the ordinary gate is off — a kit job must still
        /// refuse an un-ticked cheat. Red if KitJobRun stopped forcing CloudContent.</summary>
        [Test]
        public void AKitJob_OnAProjectWithNothingDelivered_RunsNoUntickedCheat()
        {
            Assert.IsFalse(Levers.GateActive(_root), "precondition: nothing delivered, the ordinary gate is off");
            var (_, _, cheats) = Run(new[] { AdStep.Cheat("SelectHero Knight") }, KitJobRun.DirectorOptions(_root));
            CollectionAssert.DoesNotContain(cheats.Run_, "SelectHero Knight");
        }

        /// <summary>CONTROL for the test above: the same cheat in a local run does run — so the refusal above is the
        /// kit-job flag, not a broken fake.</summary>
        [Test]
        public void ALocalRun_OnTheSameProject_RunsTheCheat()
        {
            var (_, _, cheats) = Run(new[] { AdStep.Cheat("SelectHero Knight") }, new AdDirector.Options());
            CollectionAssert.Contains(cheats.Run_, "SelectHero Knight");
        }

        [Test]
        public void TheProbe_BuildsItsSettingsFromTheKitJobBuilder()
        {
            var o = ProbeRun.DirectorOptions(_root, null);
            Assert.IsTrue(o.CloudContent);
            Assert.AreEqual(_root, o.ProjectRoot);
            Assert.AreEqual(1, o.MaxAttempts);
            Assert.IsNotNull(o.WrapCheats, "the camera-pose Type rule is wrapped round the gate");
            Assert.IsInstanceOf<NoRecordingDriver>(o.Recorder);
        }

        /// <summary>Review finding 4: every kit job gets ONE attempt and the camera-pose Type rule from the builder itself —
        /// a future cheat-search job must not inherit MaxAttempts = 2 (a retry re-runs writes).</summary>
        [Test]
        public void TheKitJobBuilder_GivesOneAttemptAndTheCameraRule()
        {
            var o = KitJobRun.DirectorOptions(_root);
            Assert.IsTrue(o.CloudContent);
            Assert.AreEqual(1, o.MaxAttempts);
            Assert.IsNotNull(o.WrapCheats);
            Assert.IsNull(o.Recorder, "the recorder is the caller's");
        }
    }
}
