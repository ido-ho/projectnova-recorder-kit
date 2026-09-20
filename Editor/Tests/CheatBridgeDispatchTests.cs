using System.IO;
using NUnit.Framework;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// Pins how <see cref="ReflectionCheatBridge.Run"/> distinguishes the three ways a cheat can end:
    /// succeeded, HANDLED-but-false, and genuinely-unknown-verb.
    ///
    /// The middle case is the one that had no representation. `Run` used to be
    /// `RunGameCheat(...) || Fail("unknown", ...)`, so a game cheat that deliberately returns false —
    /// `auto-resolve` reporting nothing was open, and any future `assert-*` — had "unknown command"
    /// written over its own explanation in probe/last.txt. The operator then read that a verb which
    /// exists and ran correctly was unrecognised. Found by a red-team review of the parameterized-shots
    /// design, which depends on an assert cheat's false return being legible.
    /// </summary>
    public class CheatBridgeDispatchTests
    {
        private sealed class FakeUi : IUiDriver
        {
            public bool Exists(string name) => false;
            public bool IsInteractable(string name) => false;
            public string? GetText(string name) => null;
            public bool Click(string name, int index = 0) => false;
            public void PointerDown(string name, int index = 0) { }
            public void PointerUp(string name, int index = 0) { }
        }

        /// <summary>Three verbs, one per ending. `silent-false` returns false WITHOUT reporting, which
        /// is the documented contract for a verb the game does not recognise.</summary>
        private sealed class Bridge : ReflectionCheatBridge
        {
            public Bridge(string root) : base(root, new FakeUi()) { }

            protected override bool RunGameCheat(string verb, string[] args)
            {
                switch (verb)
                {
                    case "wins":         return Report("wins", new[] { "did the thing" });
                    case "handled-false": return Fail("handled-false", "nothing was open");
                    case "silent-false":  return false;
                    default:              return false;
                }
            }
        }

        private string _root = null!;
        private Bridge _bridge = null!;
        private string Probe(string name) => Path.Combine(RelayPaths.Root(_root), "probe", name);

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "novakit-dispatch-" + Path.GetRandomFileName());
            _bridge = new Bridge(_root);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }

        [Test]
        public void HandledButFalse_IsNotReportedAsAnUnknownVerb()
        {
            Assert.IsFalse(_bridge.Run("handled-false"), "a failing cheat still returns false");

            // The cheat's OWN reason must survive in last.txt — this is the regression.
            StringAssert.Contains("nothing was open", File.ReadAllText(Probe("last.txt")));
            Assert.IsFalse(File.Exists(Probe("FAILED-unknown.txt")),
                "a handled verb must never be recorded as an unknown command");
        }

        [Test]
        public void UnknownVerb_IsStillReportedAsUnknown()
        {
            Assert.IsFalse(_bridge.Run("no-such-verb"));
            Assert.IsTrue(File.Exists(Probe("FAILED-unknown.txt")),
                "a verb the game never answered for is genuinely unknown");
        }

        /// <summary>A false with no Report/Fail behind it is indistinguishable from an unhandled verb,
        /// which is exactly what RunGameCheat's contract says it means.</summary>
        [Test]
        public void SilentFalse_CountsAsUnknown()
        {
            Assert.IsFalse(_bridge.Run("silent-false"));
            Assert.IsTrue(File.Exists(Probe("FAILED-unknown.txt")));
        }

        [Test]
        public void Success_WritesTheReportAndNoFailure()
        {
            Assert.IsTrue(_bridge.Run("wins"));
            StringAssert.Contains("did the thing", File.ReadAllText(Probe("last.txt")));
            Assert.IsFalse(File.Exists(Probe("FAILED-unknown.txt")));
        }

        /// <summary>The reported-flag must not leak between commands: a successful call followed by an
        /// unknown one must still report the unknown.</summary>
        [Test]
        public void ReportedFlag_DoesNotLeakBetweenCommands()
        {
            Assert.IsTrue(_bridge.Run("wins"));
            Assert.IsFalse(_bridge.Run("no-such-verb"));
            Assert.IsTrue(File.Exists(Probe("FAILED-unknown.txt")));
        }

        /// <summary>A short argument list used to return a bare false with NOTHING written — the exact
        /// symptom Fail() exists to remove. `set` with one arg looked identical to `set` on a missing type.</summary>
        [Test]
        public void MissingArguments_ExplainTheShortfallInsteadOfFailingSilently()
        {
            Assert.IsFalse(_bridge.Run("set OnlyOneArg"));
            var text = File.ReadAllText(Probe("last.txt"));
            StringAssert.Contains("needs 2 argument", text);
            StringAssert.Contains("Usage: set", text);
        }

        /// <summary>
        /// `hide-ui` and `show-ui` are BUILT-INS, so they must never reach the game's cheat surface.
        /// A game that happens to define its own `hide-ui` would otherwise shadow the kit's — the same
        /// class of collision `raw` exists to escape, and worth pinning for a verb whose whole job is
        /// to run right before a capture.
        /// </summary>
        [Test]
        public void HideUi_IsABuiltIn_NotDelegatedToTheGame()
        {
            // The FakeUi has nothing on screen, so this correctly fails — but it must fail as a
            // HANDLED built-in with its own explanation, never as an unknown verb.
            Assert.IsFalse(_bridge.Run("hide-ui Nothing_Here"));
            Assert.IsFalse(File.Exists(Probe("FAILED-unknown.txt")),
                "hide-ui must be handled by the bridge, not passed through to the game");
            StringAssert.Contains("no active UI object", File.ReadAllText(Probe("last.txt")));
        }

        [Test]
        public void HideUi_WithNoArguments_ExplainsTheUsage()
        {
            Assert.IsFalse(_bridge.Run("hide-ui"));
            var text = File.ReadAllText(Probe("last.txt"));
            StringAssert.Contains("needs 1 argument", text);
            StringAssert.Contains("Usage: hide-ui", text);
        }

        /// <summary>`show-ui` takes no arguments and must succeed even when nothing was hidden —
        /// a recovery policy can call it unconditionally without having to track state.</summary>
        [Test]
        public void ShowUi_WithNothingHidden_StillSucceeds()
        {
            Assert.IsTrue(_bridge.Run("show-ui"));
            StringAssert.Contains("restored 0 object(s)", File.ReadAllText(Probe("last.txt")));
        }

        /// <summary>
        /// Found live: `hide-ui Settings Button` searched for "Settings". Unity names contain spaces
        /// constantly, and `click`/`invoke-button` shared the defect — they would silently target the
        /// first word. An index is always an integer, so the two cases never collide.
        /// </summary>
        [Test]
        public void SplitCommand_KeepsDoubleQuotedSpansAsOneToken()
        {
            CollectionAssert.AreEqual(
                new[] { "call", "Type.Method", "SetEnergy 25", "_" },
                ReflectionCheatBridge.SplitCommand("call Type.Method \"SetEnergy 25\" _"));
        }

        [Test]
        public void SplitCommand_UnquotedInputIsUnchanged()
        {
            CollectionAssert.AreEqual(
                new[] { "hide-ui", "Settings", "Button" },
                ReflectionCheatBridge.SplitCommand("hide-ui Settings Button"));
        }

        [Test]
        public void SplitCommand_EmptyQuotedSpanIsStillAToken()
        {
            CollectionAssert.AreEqual(
                new[] { "set", "Type.Name", "" },
                ReflectionCheatBridge.SplitCommand("set Type.Name \"\""));
        }

        [Test]
        public void Run_QuotesOnly_DoesNotThrow()
        {
            Assert.IsFalse(_bridge.Run("\"\""));
            StringAssert.Contains("empty command after split", File.ReadAllText(Probe("last.txt")));
        }

        [TestCase(new[] { "Button" }, "Button", 0)]
        [TestCase(new[] { "Button", "2" }, "Button", 2)]
        [TestCase(new[] { "Settings", "Button" }, "Settings Button", 0)]
        [TestCase(new[] { "Settings", "Button", "3" }, "Settings Button", 3)]
        [TestCase(new[] { "Button@Level", "1" }, "Button@Level 1", 0)]
        [TestCase(new[] { "Roll", "Button@Play", "Now" }, "Roll Button@Play Now", 0)]
        public void ParseTarget_ReassemblesSpacedNames_AndOnlyTreatsATrailingIntegerAsAnIndex(
            string[] args, string expectedSelector, int expectedIndex)
        {
            var (selector, index) = ReflectionCheatBridge.ParseTarget(args);
            Assert.AreEqual(expectedSelector, selector);
            Assert.AreEqual(expectedIndex, index);
        }

        [Test]
        public void BothVerbs_AreListedInHelp()
        {
            Assert.IsTrue(_bridge.Run("help"));
            var text = File.ReadAllText(Probe("last.txt"));
            StringAssert.Contains("hide-ui", text);
            StringAssert.Contains("show-ui", text);
        }

        /// <summary>
        /// `raycast-at`/`tap-at` are BUILT-INS (promoted from Rogue Legend's adapter 2026-08-05 — the
        /// diagnosis, and the fix, generalize to every game), so they must never reach the game's cheat
        /// surface, the same guarantee <see cref="HideUi_IsABuiltIn_NotDelegatedToTheGame"/> pins for
        /// hide-ui/show-ui.
        ///
        /// An EditMode test has no live scene, hence no live EventSystem, so `EventSystem.current` is
        /// reliably null here — which happens to be exactly the first diagnostic branch both verbs are
        /// FOR: "nothing in the scene can receive input at all". That makes the null-EventSystem path
        /// deterministic and free to test without Play Mode, unlike the raycast-hit path (needs a real
        /// Canvas + GraphicRaycaster + EventSystem all live).
        /// </summary>
        [Test]
        public void RaycastAt_IsABuiltIn_AndReportsNoLiveEventSystem()
        {
            Assert.IsTrue(_bridge.Run("raycast-at 10 10"),
                "raycast-at REPORTS (never fails) the no-EventSystem case — it is informative, not an error");
            Assert.IsFalse(File.Exists(Probe("FAILED-unknown.txt")),
                "raycast-at must be handled by the bridge, not passed through to the game");
            StringAssert.Contains("EventSystem.current is NULL", File.ReadAllText(Probe("last.txt")));
        }

        [Test]
        public void TapAt_IsABuiltIn_AndFailsCleanlyWithNoLiveEventSystem()
        {
            Assert.IsFalse(_bridge.Run("tap-at 10 10"));
            Assert.IsFalse(File.Exists(Probe("FAILED-unknown.txt")),
                "tap-at must be handled by the bridge, not passed through to the game");
            StringAssert.Contains("EventSystem.current is NULL", File.ReadAllText(Probe("last.txt")));
        }

        [Test]
        public void RaycastAt_WithNonNumericArgs_ExplainsTheUsage()
        {
            Assert.IsFalse(_bridge.Run("raycast-at abc def"));
            StringAssert.Contains("must be numbers", File.ReadAllText(Probe("last.txt")));
        }

        [Test]
        public void RaycastAtAndTapAt_AreListedInHelp()
        {
            Assert.IsTrue(_bridge.Run("help"));
            var text = File.ReadAllText(Probe("last.txt"));
            StringAssert.Contains("raycast-at", text);
            StringAssert.Contains("tap-at", text);
        }

        /// <summary>
        /// Same built-in guarantee the raycast-at/tap-at tests above pin, for the verb that is supposed
        /// to be run BEFORE authoring a shot. Two things make this deterministic in EditMode: there is
        /// no live EventSystem, and <c>InputTier.Classify</c> returns `unreachable` whenever the
        /// EventSystem is missing, whatever the backend says. So the tier line is fixed here even
        /// though KitTestHost's input-system package state is not.
        /// </summary>
        [Test]
        public void InputProbe_IsABuiltIn_AndReportsUnreachableWithNoLiveEventSystem()
        {
            Assert.IsTrue(_bridge.Run("input-probe 10 10"),
                "input-probe REPORTS (never fails) — it is a read-only diagnosis, not an action");
            Assert.IsFalse(File.Exists(Probe("FAILED-unknown.txt")),
                "input-probe must be handled by the bridge, not passed through to the game");
            var text = File.ReadAllText(Probe("last.txt"));
            StringAssert.Contains("EventSystem: NONE", text);
            StringAssert.Contains("tier: unreachable", text);
            // A point WAS given. Reporting "no point given" here blames the caller for the scene's
            // state — caught live on gem-match3 mid level-transition, where EventSystem.current is
            // briefly null and the probe accused the caller of omitting coordinates it had passed.
            StringAssert.DoesNotContain("no point given", text);
            StringAssert.Contains("NOT TESTED", text);
        }

        /// <summary>
        /// The point is optional by design: with no args the verb still answers what the SCENE can do,
        /// which is the half of the answer that does not depend on picking a pixel first. Unparseable
        /// args take the same path rather than failing, so a typo degrades to the scene report.
        /// </summary>
        [Test]
        public void InputProbe_WithNoArgsOrBadArgs_FallsBackToTheSceneReport()
        {
            Assert.IsTrue(_bridge.Run("input-probe"));
            StringAssert.Contains("no point given", File.ReadAllText(Probe("last.txt")));

            Assert.IsTrue(_bridge.Run("input-probe abc def"));
            StringAssert.Contains("no point given", File.ReadAllText(Probe("last.txt")));
        }

        [Test]
        public void InputProbe_IsListedInHelp()
        {
            Assert.IsTrue(_bridge.Run("help"));
            StringAssert.Contains("input-probe", File.ReadAllText(Probe("last.txt")));
        }

        /// <summary>
        /// The four pointer verbs are BUILT-INS and must never fall through to the game's cheat
        /// surface. Deliberately asserts nothing about whether the gesture was QUEUED: that depends on
        /// whether this host has the input-system package and a live Mouse, which is host state, not
        /// dispatch. What must hold on every host is that the bridge answered — either a queue
        /// confirmation or the injector's own explanation, never "unknown command".
        /// </summary>
        [TestCase("press-at 10 10")]
        [TestCase("release-at 10 10")]
        [TestCase("tap-through 10 10")]
        [TestCase("drag 10 10 200 200")]
        public void PointerVerbs_AreBuiltIns_NotDelegatedToTheGame(string command)
        {
            _bridge.Run(command);
            Assert.IsFalse(File.Exists(Probe("FAILED-unknown.txt")),
                $"'{command}' must be handled by the bridge, not passed through to the game");
            Assert.IsTrue(File.Exists(Probe("last.txt")), "the verb must report something either way");
        }

        [Test]
        public void PointerVerbs_AreListedInHelp()
        {
            Assert.IsTrue(_bridge.Run("help"));
            var text = File.ReadAllText(Probe("last.txt"));
            foreach (var verb in new[] { "press-at", "release-at", "tap-through", "drag" })
                StringAssert.Contains(verb, text);
        }

        [Test]
        public void Drag_WithNonNumericArgs_ExplainsTheUsage()
        {
            Assert.IsFalse(_bridge.Run("drag a b c d"));
            StringAssert.Contains("must be numbers", File.ReadAllText(Probe("last.txt")));
        }

        [Test]
        public void CameraPose_IsABuiltIn_NotDelegatedToTheGame()
        {
            Assert.IsFalse(_bridge.Run("camera-pose 1 2 3 40.84 135 0 20"));
            Assert.IsFalse(File.Exists(Probe("FAILED-unknown.txt")),
                "camera-pose must be handled by the bridge, not passed through to the game");
            StringAssert.Contains("Play Mode", File.ReadAllText(Probe("last.txt")));
        }

        [Test]
        public void CameraPose_WithNoArguments_ExplainsTheUsage()
        {
            Assert.IsFalse(_bridge.Run("camera-pose"));
            var text = File.ReadAllText(Probe("last.txt"));
            StringAssert.Contains("needs 7 argument", text);
            StringAssert.Contains("pitch yaw roll", text);
        }

        [Test]
        public void CameraRelease_WithNoFile_Succeeds()
        {
            Assert.IsTrue(_bridge.Run("camera-release"));
            Assert.IsFalse(File.Exists(Probe("FAILED-unknown.txt")),
                "camera-release must be handled by the bridge, not passed through to the game");
            StringAssert.Contains("nothing armed", File.ReadAllText(Probe("last.txt")));
        }

        [Test]
        public void CameraVerbs_AreListedInHelp()
        {
            Assert.IsTrue(_bridge.Run("help"));
            var text = File.ReadAllText(Probe("last.txt"));
            StringAssert.Contains("camera-pose", text);
            StringAssert.Contains("camera-release", text);
        }
    }
}
