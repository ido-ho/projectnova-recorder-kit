using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using ProjectNova.RecorderKit.Tests.CheatFixtures;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// Learn-and-drive v3 P3 (§3.3 sources 1, 2, 4; §5) — the deep cheat search's kit half, on the fixtures of
    /// <c>CheatSearchFixtures.cs</c>: an IngameDebugConsole-shaped known console and a custom console, neither of them
    /// any one game's. The search is run over THIS test assembly (the production job passes the game's —
    /// <see cref="CheatScanAssemblies.Game"/>, whose rule is held by name below).
    /// </summary>
    public class CheatSearchScanTests
    {
        private static readonly Assembly Fixtures = typeof(Tripwire).Assembly;
        private static CheatStaticSearch Search() => CheatStaticSearch.Run(new[] { Fixtures });

        [SetUp]
        public void SetUp()
        {
            IngameDebugConsole.DebugLogConsole.ClearForTests();
            NovaFixtures.CustomConsole.FixtureDevActions.ClearForTests();
            NovaFixtures.CustomConsole.FixtureCommandTable.ClearForTests();
            Tripwire.Fired.Clear();
        }

        private static List<RegistryRef> ReadableRegistries(CheatStaticSearch s) =>
            // never the untouched one: reading it would run its static constructor, which the tripwire test watches
            s.Registries.Where(r => r.Type != typeof(NovaFixtures.CustomConsole.FixtureUntouchedRegistry)).ToList();

        // ---- source 2: the tag scan ------------------------------------------------------------------------------

        [Test]
        public void AKnownConsolesAttributeIsFoundByItsTypeNameAndProposedThroughItsExecutor()
        {
            var s = Search();
            var cube = s.Scan.Findings.Single(f => f.Name == "fixture-cube");
            Assert.AreEqual(CheatFinding.TagScanSource, cube.Source);
            Assert.AreEqual("ingame-debug-console", cube.Console);
            Assert.AreEqual("call IngameDebugConsole.DebugLogConsole.ExecuteCommand \"fixture-cube\"", cube.Command);
            Assert.AreEqual("NovaFixtures.KnownConsoleGame.FixtureConsoleCommands.Cube", cube.Where);
        }

        [Test]
        public void AConsoleCommandThatTakesValuesIsListedWithItsSignatureAndNotProposed()
        {
            var give = Search().Scan.Findings.Single(f => f.Name == "fixture-give");
            Assert.IsNull(give.Command, "a tick is ONE fixed command — the studio picks the values");
            Assert.AreEqual("(Int32 amount)", give.Signature);
        }

        [Test]
        public void ARegisterCallSiteWithALiteralNameIsFoundAndLedToItsCustomConsole()
        {
            var s = Search();
            var skip = s.Scan.Findings.Single(f => f.Source == CheatFinding.TagScanSource && f.Name == "Skip 5 Levels");
            Assert.AreEqual("custom", skip.Console);
            Assert.AreEqual("call NovaFixtures.CustomConsole.FixtureDevActions.Invoke \"Skip 5 Levels\"", skip.Command);
            StringAssert.Contains("FixtureDevActions.RegisterAction", skip.Where);
            Assert.IsTrue(s.Registries.Any(r => r.Type == typeof(NovaFixtures.CustomConsole.FixtureDevActions)
                                                && r.Field.Name == "Entries" && r.ItemName == "Name"),
                "the class that registers is the custom console source 1 reads — found by its SHAPE, not a name");
        }

        [Test]
        public void ANameBuiltAtRunTimeIsANamelessSiteNeverAGuessedSpelling()
        {
            var s = Search();
            var built = s.Scan.Findings.Where(f => f.Source == CheatFinding.TagScanSource
                                                   && f.Where.Contains("FixtureDevBoot") && f.Name == null).ToList();
            Assert.AreEqual(1, built.Count, "the $\"Jump to level {n}\" registration: one site, no name");
            Assert.IsNull(built[0].Command);
            Assert.IsFalse(s.Scan.Findings.Any(f => f.Name != null && f.Name.StartsWith("Jump to level")),
                "the format string is never passed off as the name");
        }

        [Test]
        public void AKnownConsolesOwnAddCommandCallSiteIsItsConsoleNotACustomOne()
        {
            var s = Search();
            var win = s.Scan.Findings.Single(f => f.Source == CheatFinding.TagScanSource && f.Name == "fixture-runtime-win");
            Assert.AreEqual("ingame-debug-console", win.Console);
            Assert.AreEqual("call IngameDebugConsole.DebugLogConsole.ExecuteCommand \"fixture-runtime-win\"", win.Command);
            Assert.AreEqual(1, s.Registries.Count(r => r.Type == typeof(IngameDebugConsole.DebugLogConsole)),
                "the known row's registry, once — not also a custom one");
            Assert.AreEqual("ingame-debug-console", s.Registries.Single(r => r.Type == typeof(IngameDebugConsole.DebugLogConsole)).Console);
        }

        [Test]
        public void ADictionaryKeyedConsoleIsACustomRegistryReadByItsKeys()
        {
            var r = Search().Registries.Single(x => x.Type == typeof(NovaFixtures.CustomConsole.FixtureCommandTable));
            Assert.IsNull(r.ItemName, "a dictionary's names are its keys");
            Assert.AreEqual("NovaFixtures.CustomConsole.FixtureCommandTable.Execute", r.Executor);
        }

        [Test]
        public void TheStaticSearchRunsNoCodeOfTheGame()
        {
            Tripwire.AttributeCtorRan = false;
            var s = Search();
            Assert.IsFalse(Tripwire.AttributeCtorRan, "an attribute's constructor ran — attribute DATA only, never the attribute");
            Assert.IsTrue(s.Registries.Any(r => r.Type == typeof(NovaFixtures.CustomConsole.FixtureUntouchedRegistry)),
                "control: the untouched console IS found by the search");
            Assert.IsFalse(Tripwire.StaticCtorRan, "a static constructor ran — the scan touched a static member");
            CollectionAssert.IsEmpty(Tripwire.Fired, "a cheat ran");
        }

        // ---- a thin forwarder: the executor is one class further in (0.13.1) ------------------------------------------

        private const string Forwarded = "NovaFixtures.ForwardedConsole.FixtureForwardedActions";

        private static CheatFinding TagFinding(CheatStaticSearch s, string name) =>
            s.Scan.Findings.Single(f => f.Source == CheatFinding.TagScanSource && f.Name == name);

        [Test]
        public void ANameRegisteredOnAThinForwarderIsProposedThroughTheRegistryItForwardsTo()
        {
            var s = Search();
            var skip = TagFinding(s, "Skip 1 Level");
            Assert.AreEqual($"call {Forwarded}.Invoke \"Skip 1 Level\"", skip.Command,
                "the global forwarder has no executor; the class it hands the name to does");
            Assert.AreEqual("custom", skip.Console);
            Assert.AreEqual("NovaFixtures.ForwardedConsole.FixtureForwardedBoot.Boot → NovaFixtureForwardingConsole.RegisterAction",
                skip.Where, "WHERE is still the call site the game wrote — a re-run must join the finding it listed before");
            StringAssert.Contains($"{Forwarded}.Register", skip.Note!, "the forward is said beside the proposal");
            Assert.AreEqual($"call {Forwarded}.Invoke \"Reset user\"", TagFinding(s, "Reset user").Command);
            Assert.AreEqual($"call {Forwarded}.Invoke \"Give me 10k topaz pls\"", TagFinding(s, "Give me 10k topaz pls").Command);
            Assert.AreEqual($"call {Forwarded}.Invoke \"Max Affinity Tree: Warrior\"", TagFinding(s, "Max Affinity Tree: Warrior").Command);
            var reg = s.Registries.Single(r => r.Type == typeof(NovaFixtures.ForwardedConsole.FixtureForwardedActions));
            Assert.AreEqual($"{Forwarded}.Invoke", reg.Executor, "and the registry source 1 reads is that class");
        }

        [Test]
        public void AChainOfForwardersIsFollowedToTheRegistryAtItsEnd()
        {
            Assert.AreEqual($"call {Forwarded}.Invoke \"Chained cheat\"", TagFinding(Search(), "Chained cheat").Command);
        }

        /// <summary>
        /// THE FORWARD LIMIT HOLDS WHATEVER THE SCAN ORDER (fix/kit-lows-1): the forward cache was keyed by method alone,
        /// so an answer worked out with one forward left was reused with three left, and the reverse. Reading HopB's own
        /// call site first cached its full-depth "found", and HopA — one forward further — reused it: four forwards,
        /// proposed. Reading HopA first cached HopB's too-shallow "not found", and HopB's own site lost its proposal. Both
        /// are asserted, so the old cache fails in either order.
        /// </summary>
        [Test]
        public void ThreeForwardsAreFollowedAndAFourthIsNotInAnyScanOrder()
        {
            var s = Search();
            Assert.AreEqual($"call {Forwarded}.Invoke \"Three deep cheat\"", TagFinding(s, "Three deep cheat").Command,
                "three forwards to the registry is the limit, and is followed");
            var tooDeep = TagFinding(s, "Too deep cheat");
            Assert.IsNull(tooDeep.Command, "a fourth forward is past the limit — not proposed");
            StringAssert.Contains("listed, not proposed", tooDeep.Note!);
        }

        [Test]
        public void AForwarderToARegistryWithNoExecutorStaysListed()
        {
            var dead = TagFinding(Search(), "Dead end cheat");
            Assert.IsNull(dead.Command, "no class on the way runs a name — nothing is proposed");
            StringAssert.Contains("listed, not proposed", dead.Note!);
            StringAssert.Contains("FixtureDeadEndStore", dead.Note!, "the class it forwards to is named");
        }

        [Test]
        public void AForwarderToTwoRegistriesIsNotGuessed()
        {
            var two = TagFinding(Search(), "Two way cheat");
            Assert.IsNull(two.Command, "two classes could run it — which one is not guessed");
            StringAssert.Contains("listed, not proposed", two.Note!);
        }

        [Test]
        public void TheForwardersOwnPassThroughIsNotANamelessCheat()
        {
            var s = Search();
            Assert.IsFalse(s.Scan.Findings.Any(f => f.Where.StartsWith("NovaFixtureForwardingConsole.RegisterAction →", StringComparison.Ordinal)),
                "the forwarder's body hands on its caller's name — it is plumbing, not a cheat whose name is built at run time");
            Assert.IsTrue(s.Notes.Any(n => n.Contains("NovaFixtureForwardingConsole.RegisterAction") && n.Contains(Forwarded)),
                "the forward is said in the notes");
        }

        [Test]
        public void AForwardedProposalFiresThatCheatThroughTheKitsBridge()
        {
            NovaFixtures.ForwardedConsole.FixtureForwardedActions.Clear();
            NovaFixtures.ForwardedConsole.FixtureForwardedBoot.Level = 10;
            NovaFixtures.ForwardedConsole.FixtureForwardedBoot.Topaz = 0;
            NovaFixtures.ForwardedConsole.FixtureForwardedBoot.Boot();
            var s = Search();
            var root = Path.Combine(Path.GetTempPath(), "p3fwd-" + Guid.NewGuid().ToString("N"));
            try
            {
                var bridge = new GenericCheatBridge(root, new UguiDriver());
                Assert.IsTrue(bridge.Run(TagFinding(s, "Skip 1 Level").Command!));
                Assert.AreEqual(11, NovaFixtures.ForwardedConsole.FixtureForwardedBoot.Level, "POSITIVE CONTROL: that cheat ran, once");
                Assert.IsTrue(bridge.Run(TagFinding(s, "Give me 10k topaz pls").Command!));
                Assert.AreEqual(10000, NovaFixtures.ForwardedConsole.FixtureForwardedBoot.Topaz);
                Assert.AreEqual(11, NovaFixtures.ForwardedConsole.FixtureForwardedBoot.Level, "and no other");
            }
            finally
            {
                NovaFixtures.ForwardedConsole.FixtureForwardedActions.Clear();
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
        }

        /// <summary>fix/kit-lows-1: the shots gate and the lever-shapes fixture take `call Ns.Outer+Inner.Invoke "..."` — this
        /// is the proof the kit's bridge RUNS that shape (the type is found by its full name, `+` and all).</summary>
        [Test]
        public void ACallOnANestedClassByItsPlusFullNameRunsThroughTheKitsBridge()
        {
            NovaFixtures.ForwardedConsole.FixtureNestedHost.Actions.Ran = 0;
            var root = Path.Combine(Path.GetTempPath(), "nested-" + Guid.NewGuid().ToString("N"));
            try
            {
                var bridge = new GenericCheatBridge(root, new UguiDriver());
                Assert.AreEqual("NovaFixtures.ForwardedConsole.FixtureNestedHost+Actions", typeof(NovaFixtures.ForwardedConsole.FixtureNestedHost.Actions).FullName);
                Assert.IsTrue(bridge.Run("call NovaFixtures.ForwardedConsole.FixtureNestedHost+Actions.Invoke \"Nested cheat\""));
                Assert.AreEqual(1, NovaFixtures.ForwardedConsole.FixtureNestedHost.Actions.Ran, "the nested class's Invoke ran, once");
            }
            finally
            {
                NovaFixtures.ForwardedConsole.FixtureNestedHost.Actions.Ran = 0;
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
        }

        [Test]
        public void TheForwardedRegistryIsReadByNameWhenTheGameHasRegistered()
        {
            NovaFixtures.ForwardedConsole.FixtureForwardedActions.Clear();
            NovaFixtures.ForwardedConsole.FixtureForwardedBoot.Boot();
            try
            {
                var reg = Search().Registries.Single(r => r.Type == typeof(NovaFixtures.ForwardedConsole.FixtureForwardedActions));
                var found = CheatRegistry.Findings(CheatRegistry.ReadOnce(new[] { reg }));
                CollectionAssert.AreEquivalent(new[] { "Give me 10k topaz pls", "Max Affinity Tree: Warrior", "Reset user", "Skip 1 Level" },
                    found.Select(f => f.Name).ToList());
                Assert.AreEqual($"call {Forwarded}.Invoke \"Skip 1 Level\"", found.Single(f => f.Name == "Skip 1 Level").Command,
                    "source 1 and source 2 propose the SAME command for one name — one row, one key");
            }
            finally
            {
                NovaFixtures.ForwardedConsole.FixtureForwardedActions.Clear();
            }
        }

        // ---- source 4: debug methods -----------------------------------------------------------------------------

        [Test]
        public void TheDebugClassRuleReadsWordsNotLetterRuns()
        {
            foreach (var yes in new[] { "GMFixtureTools", "DebugFixturePanel", "CheatFixtureNoInstance", "DevMenu", "TestCheats", "Cheats" })
                Assert.IsTrue(CheatTagScan.IsDebugClassName(yes), yes);
            foreach (var no in new[] { "DeviceFixtureInfo", "LatestFixtureNews", "Contest", "Developer", "Gmail", "Debugger2Hud" })
                Assert.IsFalse(CheatTagScan.IsDebugClassName(no), no);
        }

        [Test]
        public void DebugMethodsAreListedAndOnlyAReachableParameterlessOneIsProposed()
        {
            var f = Search().Scan.Findings.Where(x => x.Source == CheatFinding.DebugMethodSource).ToList();
            var max = f.Single(x => x.Name == "MaxEverything");
            Assert.AreEqual("call NovaFixtures.DebugMethodsGame.GMFixtureTools.MaxEverything", max.Command,
                "an instance method of a class with a static Instance — the bridge's `call` reaches it");
            var add = f.Single(x => x.Name == "AddCoins");
            Assert.IsNull(add.Command);
            Assert.AreEqual("(Int32 amount)", add.Signature);
            Assert.AreEqual("call NovaFixtures.DebugMethodsGame.DebugFixturePanel.UnlockAllLevels",
                f.Single(x => x.Name == "UnlockAllLevels").Command);
            Assert.IsNull(f.Single(x => x.Name == "GiveGems").Command, "no live object of it can be reached");
            Assert.IsFalse(f.Any(x => x.Name == "SpawnAt"), "a Vector3 is no value a command can hold");
            Assert.IsFalse(f.Any(x => x.Name == "Update"), "a Unity message is not a cheat");
            Assert.IsFalse(f.Any(x => x.Name == "get_CoinsProperty"), "a property getter is not a method a person named");
            Assert.IsFalse(f.Any(x => x.Name == "WipeAll" || x.Name == "Refresh"), "Device… and Latest… are not debug classes");
        }

        // ---- Fix 4: a value the game's own dev UI sets (source `dev-setter`) --------------------------------------------

        private const string BoardGame = "NovaFixtures.DevSetterGame.FixtureBoardGame";

        private static List<CheatFinding> DevSetterFindings() =>
            Search().Scan.Findings.Where(x => x.Source == CheatFinding.DevSetterSource).ToList();

        [Test]
        public void TheReleaseFlagRuleFindsRogueLegendsShapeAndADebugNamedDevUi_NotGameplay()
        {
            // the rule itself, class by class — two positives by the release flag (a property, a field), one by name
            StringAssert.Contains("FixturePlatform.IsRelease", DevSetters.DevUiWhy(typeof(NovaFixtures.DevSetterGame.FixtureRollPicker)));
            StringAssert.Contains("FixtureBuildFlags.IsDevBuild", DevSetters.DevUiWhy(typeof(NovaFixtures.DevSetterGame.FixtureTileOverride)));
            Assert.IsNotNull(DevSetters.DevUiWhy(typeof(NovaFixtures.DevSetterGame.FixtureDevBoardPanel)));
            Assert.IsNull(DevSetters.DevUiWhy(typeof(NovaFixtures.DevSetterGame.FixtureDiceRoller)), "NEGATIVE: turns itself off under no flag");
            Assert.IsNull(DevSetters.DevUiWhy(typeof(NovaFixtures.DevSetterGame.FixtureBannerToggle)), "NEGATIVE: reads the flag but turns itself ON");
            Assert.IsNull(DevSetters.DevUiWhy(typeof(NovaFixtures.DevSetterGame.FixtureFpsHud)),
                "NEGATIVE (audit M5): hides a CHILD under Debug.isDebugBuild — never itself");

            var found = DevSetterFindings();
            var names = found.Select(f => f.Name).ToList();
            // POSITIVE CONTROL — RL's shape: no debug word in the name, off under a static IsRelease, a singleton's auto-property
            var roll = found.Single(f => f.Name == "FixtureBoardGame.NextRoll");
            Assert.AreEqual($"set {BoardGame}.NextRoll {{v}}", roll.Command);
            Assert.AreEqual($"{BoardGame}.NextRoll", roll.Where);
            Assert.AreEqual("(Int32 value)", roll.Signature);
            StringAssert.Contains("FixtureRollPicker", roll.Note, "the note names the dev UI it came from");
            Assert.IsNull(Levers.NotTickableReason(roll.Command), "the set-value template can be ticked");
            // the lambda's body (a compiler-made nested class) is read with its dev UI
            Assert.AreEqual($"set {BoardGame}.RollSpeed {{v}}", found.Single(f => f.Name == "FixtureBoardGame.RollSpeed").Command);
            Assert.AreEqual("(Single value)", found.Single(f => f.Name == "FixtureBoardGame.RollSpeed").Signature);
            CollectionAssert.Contains(names, "FixtureBoardGame.ForcedTile", "the field-flag variant (enabled = false)");
            CollectionAssert.Contains(names, "FixtureBoardGame.StartTile", "the debug-named variant");
            // NEGATIVE CONTROLS: gameplay setting the same singleton is not a dev switch; a Vector3 is no value a set takes
            CollectionAssert.DoesNotContain(names, "FixtureBoardGame.LastRoll");
            CollectionAssert.DoesNotContain(names, "FixtureBoardGame.BannerRoll");
            CollectionAssert.DoesNotContain(names, "FixtureBoardGame.HudFps");
            CollectionAssert.DoesNotContain(names, "FixtureBoardGame.SpawnPoint");
            // an engine type's setter (gameObject.SetActive's cousin `enabled`) is never a finding
            Assert.IsFalse(found.Any(f => f.Where.StartsWith("UnityEngine.", StringComparison.Ordinal)));
        }

        [Test]
        public void ATickedDevSetterTemplateSetsTheValueAndItsGetBecomesItThroughTheRealBridge()
        {
            var root = Path.Combine(Path.GetTempPath(), "devsetter-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RelayPaths.NovaDir(root));
            LeverGateBridge.ForgetRefused();
            var game = new NovaFixtures.DevSetterGame.FixtureBoardGame();
            NovaFixtures.DevSetterGame.FixtureBoardGame.Instance = game;
            try
            {
                var template = DevSetterFindings().Single(f => f.Name == "FixtureBoardGame.NextRoll").Command!;
                Assert.IsNull(Levers.SetApproved(root, template, true), "the template is ticked");
                Assert.IsNull(Levers.SetApproved(root, CheatCheck.GetLever($"{BoardGame}.NextRoll"), true), "and its read");

                // the gate's own rule: the ticked template lets ONE value through, for that member only
                var approved = Levers.Approved(root);
                Assert.IsTrue(Levers.Allows(approved, $"set {BoardGame}.NextRoll 3"));
                Assert.IsTrue(Levers.Allows(approved, $"set {BoardGame}.NextRoll -2"));
                Assert.IsFalse(Levers.Allows(approved, $"set {BoardGame}.NextRoll 3 4"), "never a second value");
                Assert.IsFalse(Levers.Allows(approved, $"set {BoardGame}.ForcedTile 3"), "never another member");
                Assert.IsFalse(Levers.Allows(approved, $"set {BoardGame}.NextRoll.Other 3"), "never a deeper path");

                // POSITIVE CONTROL, end to end: `set … 3` then `get … becomes 3`, through the real ReflectionCheatBridge
                var request = CheatProofRequest.FromJson(new Newtonsoft.Json.Linq.JObject
                {
                    ["command"] = $"set {BoardGame}.NextRoll 3",
                    ["check"] = new Newtonsoft.Json.Linq.JObject { ["get"] = $"{BoardGame}.NextRoll", ["expect"] = CheatCheck.Becomes, ["value"] = 3 },
                    ["settleSec"] = 2,
                })!;
                var run = new CheatProofRun(request);
                var clock = 0.0;
                var frames = 0;
                foreach (var _ in CheatProofRun.Run(run, root, new GenericCheatBridge(root, new UguiDriver()), () => clock))
                {
                    clock += 0.25;
                    Assert.Less(frames++, 200);
                }
                Assert.IsNull(run.LeverRefused);
                Assert.IsTrue(run.Ran);
                Assert.AreEqual(3, game.NextRoll, "the value was written");
                Assert.AreEqual(0, run.Before!.Number, "it was not 3 before");
                Assert.AreEqual(3, run.After!.Number, "the read-back becomes 3");
                Assert.AreEqual("moved", run.StoppedBy);

                // and an UNticked dev setter stays refused: the template tick covers its own member only
                var other = new CheatProofRun(CheatProofRequest.FromJson(new Newtonsoft.Json.Linq.JObject
                {
                    ["command"] = $"set {BoardGame}.ForcedTile 3",
                    ["check"] = new Newtonsoft.Json.Linq.JObject { ["get"] = $"{BoardGame}.NextRoll", ["expect"] = CheatCheck.Becomes, ["value"] = 3 },
                    ["settleSec"] = 1,
                })!);
                foreach (var _ in CheatProofRun.Run(other, root, new GenericCheatBridge(root, new UguiDriver()), () => clock)) clock += 0.25;
                Assert.IsNotNull(other.LeverRefused);
                Assert.AreEqual(0, game.ForcedTile);
            }
            finally
            {
                NovaFixtures.DevSetterGame.FixtureBoardGame.Instance = null;
                LeverGateBridge.ForgetRefused();
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
        }

        [Test]
        public void RogueLegendsExactShapeIsFound_AConstFlagIsAKnownLimit()
        {
            // POSITIVE: a `static readonly bool` field (ldsfld), this.gameObject.SetActive(false), a lambda setting the
            // singleton's auto-property
            StringAssert.Contains("FixtureRlFlags.IsRelease", DevSetters.DevUiWhy(typeof(NovaFixtures.DevSetterGame.FixtureRlPredeterminedRoll)));
            var roll = DevSetterFindings().Single(f => f.Name == "FixtureBoardGame.RlNextRoll");
            Assert.AreEqual($"set {BoardGame}.RlNextRoll {{v}}", roll.Command);
            StringAssert.Contains("FixtureRlPredeterminedRoll", roll.Note);
            // KNOWN LIMIT (invariant 182): a `const bool` flag is inlined — `if (false) {…}` is compiled away, so the IL
            // holds no flag read and no SetActive(false). The rule has nothing to read, and the class is NOT found. Pinned
            // here so a change that starts finding it is noticed (and the limit taken off the invariant).
            Assert.IsNull(DevSetters.DevUiWhy(typeof(NovaFixtures.DevSetterGame.FixtureRlConstFlagRoll)));
            Assert.IsFalse(DevSetterFindings().Any(f => f.Name == "FixtureBoardGame.ConstNextRoll"));
        }

        [Test]
        public void ATemplatesOwnTextNeverRuns_OnlyAFilledCommandDoes()
        {
            // the audit's H1: a ticked template covers its own text and every template of its shape — the gate must still
            // refuse to RUN one, or `set A.b {v}` writes the text "{v}" into a string member
            var root = Path.Combine(Path.GetTempPath(), "devsetter-h1-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RelayPaths.NovaDir(root));
            LeverGateBridge.ForgetRefused();
            var game = new NovaFixtures.DevSetterGame.FixtureBoardGame();
            NovaFixtures.DevSetterGame.FixtureBoardGame.Instance = game;
            try
            {
                Assert.IsNull(Levers.SetApproved(root, $"set {BoardGame}.Label {{v}}", true));
                var log = new List<string>();
                var gate = KitJobRun.Gate(new GenericCheatBridge(root, new UguiDriver()), root, log.Add);
                Assert.IsFalse(gate.Run($"set {BoardGame}.Label {{v}}"), "the template's own text");
                Assert.IsFalse(gate.Run($"set {BoardGame}.Label {{w}}"), "a template of its shape");
                Assert.AreEqual("", game.Label, "nothing was written");
                StringAssert.Contains(Levers.TemplateNeverRunsWords, log.Last());
                // the re-audit: its reason is its own — never "not ticked" of a ticked template (invariant 99)
                Assert.AreEqual(GateRefusal.Template, gate.LastRefusal);
                var worded = HygieneReadyGate.MuteGetRefusedLog("get X.y", gate.LastRefusal, $"set {BoardGame}.Label {{v}}", null);
                StringAssert.Contains(Levers.TemplateNeverRunsWords, worded);
                StringAssert.DoesNotContain("not ticked", worded);
                Assert.IsTrue(gate.Run($"set {BoardGame}.Label hello"), "POSITIVE CONTROL: a filled command runs");
                Assert.AreEqual("hello", game.Label);
            }
            finally
            {
                NovaFixtures.DevSetterGame.FixtureBoardGame.Instance = null;
                LeverGateBridge.ForgetRefused();
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
        }

        [Test]
        public void ASetValueTemplateTickedByAnOlderKitApprovesNothing_ATickOnThisKitDoes_AndTheOldEntryIsKept()
        {
            // the audit's M3: before 0.14.0 a template in levers.json approved nothing — it must not revive on upgrade
            var root = Path.Combine(Path.GetTempPath(), "devsetter-m3-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RelayPaths.NovaDir(root));
            var template = $"set {BoardGame}.NextRoll {{v}}";
            try
            {
                File.WriteAllText(Levers.FilePath(root),
                    new Newtonsoft.Json.Linq.JObject { ["$schemaVersion"] = 1, ["approved"] = new Newtonsoft.Json.Linq.JArray(template, "raw Other") }.ToString());
                Assert.IsFalse(Levers.Allows(Levers.Approved(root), $"set {BoardGame}.NextRoll 3"), "an old-kit tick approves nothing");
                CollectionAssert.DoesNotContain(Levers.Approved(root), template);
                // another tick keeps the old entry in the file exactly as it was
                Assert.IsNull(Levers.SetApproved(root, "raw Third", true));
                var afterOther = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Levers.FilePath(root)));
                CollectionAssert.Contains(afterOther["approved"]!.Values<string>().ToList(), template);
                Assert.IsFalse(Levers.Allows(Levers.Approved(root), $"set {BoardGame}.NextRoll 3"));
                // POSITIVE CONTROL: a tick on this kit is stamped and approves
                Assert.IsNull(Levers.SetApproved(root, template, true));
                var stamped = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Levers.FilePath(root)));
                CollectionAssert.Contains(stamped[Levers.SetValueApprovedField]!.Values<string>().ToList(), template);
                Assert.IsTrue(Levers.Allows(Levers.Approved(root), $"set {BoardGame}.NextRoll 3"));
                // unticking takes it out of both places
                Assert.IsNull(Levers.SetApproved(root, template, false));
                var gone = File.ReadAllText(Levers.FilePath(root));
                StringAssert.DoesNotContain("NextRoll", gone);
                Assert.IsFalse(Levers.Allows(Levers.Approved(root), $"set {BoardGame}.NextRoll 3"));
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
        }

        [Test]
        public void TheWindowSaysWhatASetValueTemplateGrants()
        {
            StringAssert.StartsWith(Levers.SetsAnyValueWords, Levers.SetValueGrantLine($"set {BoardGame}.NextRoll {{v}}"));
            Assert.IsNull(Levers.SetValueGrantLine($"set {BoardGame}.NextRoll 3"));
            Assert.IsNull(Levers.SetValueGrantLine("raw SkipLevel"));
        }

        [Test]
        public void ADeliveredSetValueTemplatesRiskHoldsForTheCommandsItLetsThrough()
        {
            var template = $"set {BoardGame}.NextRoll {{v}}";
            var delivered = new List<TickList.Candidate> { new() { Command = template, Kind = "give" } };
            Assert.IsNotNull(CheatRisk.Why($"set {BoardGame}.NextRoll 3", delivered), "the row's kind raises the filled command");
            Assert.IsNull(CheatRisk.Why($"set {BoardGame}.ForcedTile 3", delivered), "and no other member's");
            Assert.IsNull(CheatRisk.Why($"set {BoardGame}.NextRoll 3", null), "the words alone say nothing risky");
        }

        // ---- source 1: the registry, read until stable -------------------------------------------------------------

        [Test]
        public void TheRegistryReadFindsNamesRegisteredAtRunTimeAndTheirCommandsFireThemThroughTheBridge()
        {
            NovaFixtures.CustomConsole.FixtureDevBoot.Level = 10;
            NovaFixtures.CustomConsole.FixtureDevBoot.Boot();
            NovaFixtures.KnownConsoleGame.FixtureConsoleCommands.Boot();
            var reads = CheatRegistry.ReadOnce(ReadableRegistries(Search()));
            var found = CheatRegistry.Findings(reads);

            CollectionAssert.IsSubsetOf(new[] { "Skip 5 Levels", "Reset progress", "Jump to level 1", "Jump to level 2" },
                found.Where(x => x.Console == "custom").Select(x => x.Name).ToList(),
                "the names the code built at run time are read off the running registry (what the static scan cannot name)");
            var jump = found.Single(x => x.Name == "Jump to level 2");
            Assert.AreEqual(CheatFinding.RegistrySource, jump.Source);
            Assert.AreEqual("NovaFixtures.CustomConsole.FixtureDevActions.Entries", jump.Where);

            // POSITIVE CONTROL: the proposed command, run through the kit's own bridge, fires THAT cheat
            var root = Path.Combine(Path.GetTempPath(), "p3-" + Guid.NewGuid().ToString("N"));
            try
            {
                var bridge = new GenericCheatBridge(root, new UguiDriver());
                Assert.IsTrue(bridge.Run(jump.Command!), jump.Command);
                Assert.AreEqual(2, NovaFixtures.CustomConsole.FixtureDevBoot.Level);
                var win = found.Single(x => x.Name == "fixture-runtime-win");
                Assert.IsTrue(bridge.Run(win.Command!), win.Command);
                CollectionAssert.AreEqual(new[] { "win" }, Tripwire.Fired);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
        }

        [Test]
        public void ARegistryThatGrowsAfterBootIsReadUntilTwoReadsAgree()
        {
            var registries = ReadableRegistries(Search())
                .Where(r => r.Type == typeof(NovaFixtures.CustomConsole.FixtureDevActions)).ToList();
            var stable = new CheatRegistry.StableRead();
            NovaFixtures.CustomConsole.FixtureDevBoot.Boot();
            Assert.IsFalse(stable.Offer(CheatRegistry.ReadOnce(registries)), "one read is never stable");
            NovaFixtures.CustomConsole.FixtureDevBoot.LateBoot();
            Assert.IsFalse(stable.Offer(CheatRegistry.ReadOnce(registries)), "4 then 5: still growing");
            Assert.IsTrue(stable.Offer(CheatRegistry.ReadOnce(registries)), "5 then 5: stable");
            Assert.IsTrue(stable.Stable);
            CollectionAssert.AreEqual(new[] { 4, 5, 5 }, stable.Counts);
            Assert.IsTrue(stable.Last!.Single().Names.Contains("Late cheat"));
        }

        [Test]
        public void AnEmptyRegistryIsNotStableBeforeTheGraceReadsAndAFullOneStopsAtTheCap()
        {
            var empty = new CheatRegistry.StableRead();
            var none = new List<CheatRegistry.ConsoleRead>();
            for (var i = 1; i < CheatRegistry.StableRead.EmptyGraceReads; i++)
                Assert.IsFalse(empty.Offer(none), $"read {i}: a console may still be filling itself");
            Assert.IsTrue(empty.Offer(none));
            Assert.IsTrue(empty.Stable, "empty after the grace reads is a stable, empty answer");

            var growing = new CheatRegistry.StableRead();
            var reg = ReadableRegistries(Search()).First();
            for (var i = 0; i < CheatRegistry.StableRead.MaxReads; i++)
                growing.Offer(new List<CheatRegistry.ConsoleRead>
                    { new() { Registry = reg, Names = Enumerable.Range(0, i + 1).Select(n => "c" + n).ToList() } });
            Assert.IsTrue(growing.Done);
            Assert.IsFalse(growing.Stable, "never agreed — said as not stable, not passed off as the full list");
        }

        [Test]
        public void ANameHoldingAQuoteIsReadButNeverProposedAsACommand()
        {
            Assert.IsNull(CheatFinding.ExecutorCommand("X.Invoke", "say \"hi\"", out var why));
            StringAssert.Contains("double quote", why!);
            Assert.IsNull(CheatFinding.ExecutorCommand("X.Invoke", "a\nb", out why), "a line break: not tickable");
            Assert.IsNotNull(why);
            Assert.AreEqual("call X.Invoke \"Give me 10k\"", CheatFinding.ExecutorCommand("X.Invoke", "Give me 10k", out _));
        }

        [Test]
        public void EveryProposedCommandIsOneTheWindowCanTick()
        {
            NovaFixtures.CustomConsole.FixtureDevBoot.Boot();
            var s = Search();
            var all = s.Scan.Findings.Concat(CheatRegistry.Findings(CheatRegistry.ReadOnce(ReadableRegistries(s))))
                .Where(f => f.Command != null).ToList();
            Assert.Greater(all.Count, 5);
            foreach (var f in all)
            {
                Assert.IsNull(Levers.NotTickableReason(f.Command), f.Command);
                Assert.LessOrEqual(f.Command!.Length, Levers.MaxLeverLength);
            }
        }

        // ---- which assemblies ------------------------------------------------------------------------------------

        [Test]
        public void TheAssemblyRuleReadsTheGamesPlayerCodeAndItsPackagesOnly()
        {
            Assert.IsTrue(CheatScanAssemblies.IsGameAssembly("Assembly-CSharp", true));
            Assert.IsTrue(CheatScanAssemblies.IsGameAssembly("IngameDebugConsole.Runtime", true), "a console package is the game's");
            Assert.IsFalse(CheatScanAssemblies.IsGameAssembly("Assembly-CSharp-Editor", false), "editor-only: an editor tool");
            foreach (var no in new[] { "UnityEngine.UI", "Unity.TextMeshPro", "System.Core", "ProjectNova.RecorderKit.Editor", "Newtonsoft.Json", "mscorlib" })
                Assert.IsFalse(CheatScanAssemblies.IsGameAssembly(no, true), no);
        }

        [Test]
        public void AKnownConsoleWhoseFieldIsMissingIsSaidNotThrown()
        {
            var row = new KnownConsoles.ConsoleRow { Id = "x", RegistryType = typeof(IngameDebugConsole.DebugLogConsole).FullName, RegistryField = "noSuchField" };
            var r = RegistryRef.FromKnown(row, CheatScanAssemblies.FinderOver(new[] { Fixtures }), out var why);
            Assert.IsNull(r);
            StringAssert.Contains("noSuchField", why!);
        }
    }
}
