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
