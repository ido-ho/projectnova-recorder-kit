using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

// A game shaped like a live-service one (the Rogue Legend shape, no name of it): a static root holding a nested player
// model, a dictionary of currency models by id, and a list of level models.
namespace NovaFixtures.PlayerData
{
    [Serializable] public class FxRegen { public long NextAt; }
    [Serializable] public class FxCurrency { public int Amount; public FxRegen Regen = new(); }
    [Serializable] public class FxCurrencies { public Dictionary<string, FxCurrency> Currencies = new(); }
    [Serializable] public class FxLevel { public bool DidClear; public int Stars; }
    [Serializable]
    public class FxLevels
    {
        public List<FxLevel> Levels = new();
        public Dictionary<string, FxLevels>? Worlds;
    }
    [Serializable] public class FxUser { public int MatchesPlayed; public string Name = "p1"; public float PlaySeconds; }
    public enum FxKey { Gold, Gems }

    /// <summary>A game-defined collection: the snapshot must never run its enumerator or its Count.</summary>
    public sealed class FxGameCollection : ICollection
    {
        public static int Enumerated;
        public int Hidden = 7;
        public int Count { get { Enumerated++; return 1; } }
        public bool IsSynchronized => false;
        public object SyncRoot => this;
        public void CopyTo(Array array, int index) => Enumerated++;
        public IEnumerator GetEnumerator() { Enumerated++; throw new InvalidOperationException("game code ran"); }
    }

    /// <summary>Everything a walk must survive.</summary>
    public class FxWeird
    {
        public FxWeird? Self;
        public int Throws => throw new InvalidOperationException("a getter that throws");
        public static int GetterRuns;
        public int Counted { get { GetterRuns++; return 1; } }
        public GameObject? Go;
        public Action? OnChange;
        public Dictionary<string, int> Odd = new() { ["has space"] = 1, ["ok"] = 2, ["a.b"] = 3 };
        public Dictionary<FxKey, int> ByEnum = new() { [FxKey.Gems] = 5 };
        public Dictionary<int, int> ById = new() { [42] = 9 };
        public FxGameCollection Custom = new();
        public int[] Scores = { 3, 4 };
        public List<List<int>> Nested = new() { new List<int> { 1 } };
    }

    [Serializable]
    public class FxUserData
    {
        public FxUser User = new();
        public FxCurrencies Currencies = new();
        public FxLevels MainLevels = new();
        public int Version { get; set; } = 3;
        public FxWeird Weird = new();
    }

    [Serializable] public class FxRemoteConfig { public int A; public int B; public int C; public int D; public int E; }

    public class FxBoot : MonoBehaviour { public int Hp; public int Mp; public int Xp; }
}

// a game whose namespace merely STARTS like a runtime one is still the game's own data
namespace MonopolyFixture.Save
{
    [Serializable] public class FxBoard { public int Houses = 2; public int Hotels; public int Cash = 1500; }
    public static class FxMonopoly { public static FxBoard Board = new(); }
}

namespace NovaFixtures.PlayerData
{

    public class FxFirebase
    {
        public static FxUserData? UserData { get; set; }
        public static FxFirebase? Instance;
        public static FxRemoteConfig RemoteConfig = new();
        public static FxBoot? Boot;
        public static GameObject? BootObject;
        public static int Counter;
        /// <summary>Computed: reading it runs code — never a root.</summary>
        public static FxUserData Computed => UserData!;
        public static FxUserData Boom => throw new InvalidOperationException("a root getter that throws");

        public static void Reset(int levels = 3)
        {
            var d = new FxUserData();
            d.Currencies.Currencies["topaz"] = new FxCurrency { Amount = 50 };
            d.Currencies.Currencies["gold"] = new FxCurrency { Amount = 1200 };
            for (var i = 0; i < levels; i++) d.MainLevels.Levels.Add(new FxLevel { DidClear = true, Stars = 3 });
            d.Weird.Self = d.Weird;
            UserData = d;
            FxGameCollection.Enumerated = 0;
            FxWeird.GetterRuns = 0;
        }
    }

    public static class FxCheats
    {
        public static void GiveTopaz()
        {
            FxFirebase.UserData!.Currencies.Currencies["topaz"].Amount += 10000;
            FxFirebase.UserData.User.PlaySeconds += 1; // an unrelated change the snapshot also sees
        }
        public static void SkipLevel() => FxFirebase.UserData!.MainLevels.Levels.Add(new FxLevel { DidClear = true, Stars = 1 });
        public static void Nothing() { }
    }
}

namespace ProjectNova.RecorderKit.Tests
{
    using NovaFixtures.PlayerData;

    /// <summary>
    /// Kit 0.13.2 — a proof with no check chosen: WHERE the player's data lives (<see cref="DataRoots"/>, by type only),
    /// the SNAPSHOT under it (<see cref="SnapshotRead"/>: dictionaries by key, lists by index, bounded, fields only,
    /// never throwing), the ranked diff it proposes, and the <c>get</c> that reads each proposal back
    /// (<see cref="DataPath"/>). A fixture shaped like a live-service game's: a static root, a nested model, a dictionary
    /// of currency models, a list of level models.
    /// </summary>
    public class SnapshotDataTests
    {
        private const string Root = "NovaFixtures.PlayerData.FxFirebase.UserData";
        private const string Topaz = Root + ".Currencies.Currencies[topaz].Amount";
        private string _root = "";
        private double _clock;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "snapdata-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RelayPaths.NovaDir(_root));
            LeverGateBridge.ForgetRefused();
            Levers.ForgetRowsForTests();
            FxFirebase.Reset();
            SnapshotRead.BudgetMsForTests = null;
            _clock = 0;
        }

        [TearDown]
        public void TearDown()
        {
            SnapshotRead.BudgetMsForTests = null;
            LeverGateBridge.ForgetRefused();
            Levers.ForgetRowsForTests();
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }

        private static IEnumerable<Type> FixtureTypes() =>
            typeof(FxFirebase).Assembly.GetTypes().Where(t => t.Namespace == "NovaFixtures.PlayerData");

        // ---- where the player's data lives --------------------------------------------------------------------------

        [Test]
        public void TheDataRootIsTheStaticModelNotAComputedGetterAUnityObjectOrAConfig()
        {
            var game = new HashSet<System.Reflection.Assembly> { typeof(FxFirebase).Assembly };
            var found = DataRoots.FindAmong(FixtureTypes(), game);
            var paths = found.Roots.Select(r => r.Path).ToList();
            Assert.AreEqual(Root, paths.FirstOrDefault(), string.Join(", ", paths));
            CollectionAssert.DoesNotContain(paths, "NovaFixtures.PlayerData.FxFirebase.Computed", "a computed getter runs code: never a root");
            CollectionAssert.DoesNotContain(paths, "NovaFixtures.PlayerData.FxFirebase.Boom");
            CollectionAssert.DoesNotContain(paths, "NovaFixtures.PlayerData.FxFirebase.Boot", "a MonoBehaviour is a scene object, not data");
            CollectionAssert.DoesNotContain(paths, "NovaFixtures.PlayerData.FxFirebase.Counter", "a number is not a model");
            var config = found.Roots.FirstOrDefault(r => r.Path.EndsWith(".RemoteConfig"));
            Assert.IsNotNull(config, "a config is still a model — ranked, not hidden");
            Assert.Less(config!.Score, found.Roots[0].Score);
            Assert.IsFalse(found.CutByBudget);
            Assert.AreEqual(0, FxWeird.GetterRuns, "finding roots reads types only");
        }

        [Test]
        public void ANamespaceThatOnlyStartsLikeTheRuntimesIsStillTheGamesData()
        {
            var game = new HashSet<System.Reflection.Assembly> { typeof(FxFirebase).Assembly };
            var owners = typeof(FxFirebase).Assembly.GetTypes().Where(t => t.Namespace == "MonopolyFixture.Save");
            Assert.AreEqual("MonopolyFixture.Save.FxMonopoly.Board", DataRoots.FindAmong(owners, game).Roots.FirstOrDefault()?.Path);
            var leaves = SnapshotRead.Read("MonopolyFixture.Save.FxMonopoly.Board", out var error);
            Assert.IsNull(error);
            Assert.AreEqual(1500, leaves!["MonopolyFixture.Save.FxMonopoly.Board.Cash"]);
            Assert.IsTrue(SnapshotRead.IsRuntimeType(typeof(System.Collections.Generic.List<int>)));
            Assert.IsTrue(SnapshotRead.IsRuntimeType(typeof(UnityEngine.Vector3)));
            Assert.IsFalse(SnapshotRead.IsRuntimeType(typeof(MonopolyFixture.Save.FxBoard)));
        }

        // ---- the snapshot ------------------------------------------------------------------------------------------

        [Test]
        public void TheSnapshotWalksDictionariesByKeyListsByIndexAndEveryPathReadsBack()
        {
            var r = SnapshotRead.ReadFull(Root, out var error);
            Assert.IsNull(error);
            Assert.IsNotNull(r);
            var leaves = r!.Leaves;
            Assert.AreEqual(50, leaves[Topaz]);
            Assert.AreEqual(2, leaves[Root + ".Currencies.Currencies.Count"]);
            Assert.AreEqual(3, leaves[Root + ".MainLevels.Levels.Count"]);
            Assert.AreEqual(1, leaves[Root + ".MainLevels.Levels[2].DidClear"]);
            Assert.AreEqual(3, leaves[Root + ".Version"], "an auto-property is read through its backing field, named by the property");
            Assert.AreEqual(5, leaves[Root + ".Weird.ByEnum[Gems]"]);
            Assert.AreEqual(9, leaves[Root + ".Weird.ById[42]"]);
            Assert.AreEqual(2, leaves[Root + ".Weird.Scores.Count"]);
            Assert.AreEqual(4, leaves[Root + ".Weird.Scores[1]"]);
            Assert.AreEqual(1, leaves[Root + ".Weird.Nested[0][0]"]);
            Assert.AreEqual(2, leaves[Root + ".Weird.Odd[ok]"]);
            // a key with a space or a dot cannot be written: since the audit's M6 it is sent as its salted short hash
            Assert.AreEqual(2, leaves.Keys.Count(k => System.Text.RegularExpressions.Regex.IsMatch(k, @"\.Weird\.Odd\[#[0-9a-f]{12}\]$")));
            Assert.IsFalse(leaves.Keys.Any(k => k.Contains("has space") || k.Contains("a.b")));
            Assert.IsNull(r.Cut);
            foreach (var kv in leaves)
            {
                Assert.IsTrue(GameReflection.TryGetPath(kv.Key, out var v, out var err), $"{kv.Key}: a `get` cannot read it back ({err})");
                Assert.AreEqual(kv.Value, SnapshotRead.Number(v!), kv.Key);
                Assert.IsTrue(DataPath.IsPath(kv.Key), kv.Key);
            }
        }

        [Test]
        public void TheSnapshotNeverThrowsAndRunsNoGameCode()
        {
            // cycles, a getter that throws, a counting getter, a Unity object, a delegate, a game-defined collection
            FxFirebase.UserData!.Weird.Go = null;
            FxFirebase.UserData.Weird.OnChange = () => throw new Exception("never called");
            var r = SnapshotRead.ReadFull(Root, out var error);
            Assert.IsNull(error);
            Assert.AreEqual(0, FxWeird.GetterRuns, "the walk reads fields, never a property getter");
            Assert.AreEqual(0, FxGameCollection.Enumerated, "a game-defined collection is never enumerated or counted");
            Assert.AreEqual(7, r!.Leaves[Root + ".Weird.Custom.Hidden"], "…its fields are walked like any object's");
            Assert.IsFalse(r.Leaves.Keys.Any(k => k.Contains(".Self.")), "a cycle is walked once");
            Assert.IsFalse(r.Leaves.Keys.Any(k => k.Contains(".Go") || k.Contains(".OnChange") || k.Contains(".Throws")));

            // a root behind a getter body (here one that throws), a root that is null, a root that does not exist: an
            // error, never an exception — and since the second fresh check, a static getter body is never run at all
            Assert.IsNull(SnapshotRead.ReadFull("NovaFixtures.PlayerData.FxFirebase.Boom", out error));
            StringAssert.Contains("never runs one", error);
            FxFirebase.UserData = null;
            Assert.IsNull(SnapshotRead.ReadFull(Root, out error));
            StringAssert.Contains("is null", error);
            Assert.IsNull(SnapshotRead.ReadFull("No.Such.Root", out error));
            Assert.IsNotNull(error);
            // a Unity object as the root: nothing under it is walked
            var go = new GameObject("snapdata");
            try
            {
                FxFirebase.BootObject = go;
                var boot = SnapshotRead.ReadFull("NovaFixtures.PlayerData.FxFirebase.BootObject", out error);
                Assert.IsNull(error);
                Assert.AreEqual(0, boot!.Leaves.Count);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); FxFirebase.BootObject = null; }
        }

        [Test]
        public void TheSnapshotIsBounded()
        {
            // a long list: its count, and only the tail by index
            FxFirebase.Reset(levels: 100);
            var r = SnapshotRead.ReadFull(Root, out _)!;
            Assert.AreEqual(100, r.Leaves[Root + ".MainLevels.Levels.Count"]);
            Assert.IsTrue(r.Leaves.ContainsKey(Root + ".MainLevels.Levels[99].Stars"));
            Assert.IsTrue(r.Leaves.ContainsKey(Root + $".MainLevels.Levels[{100 - SnapshotRead.TailEntries}].Stars"));
            Assert.IsFalse(r.Leaves.ContainsKey(Root + $".MainLevels.Levels[{100 - SnapshotRead.TailEntries - 1}].Stars"));
            Assert.IsFalse(r.Leaves.ContainsKey(Root + ".MainLevels.Levels[0].Stars"));
            // a very deep chain stops at the depth cap
            var deep = FxFirebase.UserData!.MainLevels;
            for (var i = 0; i < 20; i++)
            {
                deep.Worlds = new Dictionary<string, FxLevels> { ["w"] = new FxLevels() };
                deep = deep.Worlds["w"];
            }
            r = SnapshotRead.ReadFull(Root, out _)!;
            Assert.IsFalse(r.Leaves.Keys.Any(k => k.Split('[').Length > SnapshotRead.MaxDepth + 2), "no path deeper than the cap");
            // the time budget: a read stopped by it SAYS so
            SnapshotRead.BudgetMsForTests = -1;
            r = SnapshotRead.ReadFull(Root, out var error)!;
            Assert.IsNull(error);
            StringAssert.Contains("time budget", r.Cut);
        }

        [Test]
        public void ACutReadsDiffComparesOnlyValuesInBothReads()
        {
            var before = new Dictionary<string, double> { ["A.b"] = 1, ["A.c"] = 1 };
            var after = new Dictionary<string, double> { ["A.b"] = 2, ["A.d"] = 1 };
            Assert.AreEqual(3, SnapshotRead.Diff(before, after).Count);
            var cut = SnapshotRead.Diff(before, after, anyCut: true);
            Assert.AreEqual(1, cut.Count);
            Assert.AreEqual("A.b", cut[0].Path);
        }

        [Test]
        public void TheDiffIsRankedAMovedValueFirstTheBiggestMoveFirst()
        {
            var before = new Dictionary<string, double> { ["A.timer"] = 10, ["A.topaz"] = 50, ["A.z"] = 1 };
            var after = new Dictionary<string, double> { ["A.timer"] = 11, ["A.topaz"] = 10050, ["A.new"] = 1 };
            var d = SnapshotRead.Diff(before, after);
            Assert.AreEqual(new[] { "A.topaz", "A.timer", "A.new", "A.z" }, d.Select(x => x.Path).ToArray());
        }

        // ---- `get` addressing ---------------------------------------------------------------------------------------

        [Test]
        public void GetReadsAnEntryByKeyAnItemByIndexAndACount()
        {
            Assert.IsTrue(GameReflection.TryGetPath(Topaz, out var v, out var err), err);
            Assert.AreEqual(50, v);
            Assert.IsTrue(GameReflection.TryGetPath(Root + ".MainLevels.Levels.Count", out v, out err), err);
            Assert.AreEqual(3, v);
            Assert.IsTrue(GameReflection.TryGetPath(Root + ".Weird.Scores.Count", out v, out err), "an array's Count reads back: " + err);
            Assert.AreEqual(2, v);
            Assert.IsTrue(GameReflection.TryGetPath(Root + ".MainLevels.Levels[0].DidClear", out v, out err), err);
            Assert.AreEqual(true, v);
            Assert.IsTrue(GameReflection.TryGetPath(Root + ".Weird.ByEnum[Gems]", out v, out err), err);
            Assert.AreEqual(5, v);

            Assert.IsFalse(GameReflection.TryGetPath(Root + ".Currencies.Currencies[ruby].Amount", out _, out err));
            StringAssert.Contains("no key 'ruby'", err);
            Assert.IsFalse(GameReflection.TryGetPath(Root + ".MainLevels.Levels[9].DidClear", out _, out err));
            StringAssert.Contains("past the end", err);
            Assert.IsFalse(GameReflection.TryGetPath(Root + ".MainLevels.Levels[x].DidClear", out _, out err));
            Assert.IsFalse(GameReflection.TryGetPath(Root + ".User[0]", out _, out err));
            StringAssert.Contains("not a dictionary, list or array", err);
            Assert.IsFalse(GameReflection.TrySetPath(Topaz, "5", out err), "set never writes through [key]");
            Assert.AreEqual(50, FxFirebase.UserData!.Currencies.Currencies["topaz"].Amount);
        }

        [Test]
        public void TheGrammarIsNamesDotsAndBareKeys()
        {
            Assert.IsTrue(DataPath.IsPath(Topaz));
            Assert.IsTrue(DataPath.IsPath("Save.Levels[3].DidClear"));
            Assert.IsTrue(DataPath.IsPath("Save.ById[-1]"));
            Assert.IsFalse(DataPath.IsPath("Save.Currencies[\"topaz\"]"), "a quoted key would be split at its quotes");
            Assert.IsFalse(DataPath.IsPath("Save.Currencies[top az]"));
            Assert.IsFalse(DataPath.IsPath("Save.Currencies[]"));
            Assert.IsFalse(DataPath.IsPath("Save[0].Coins"), "the type and its first member are names");
            Assert.IsFalse(DataPath.IsPath("Save.Currencies[a.b]"));
            Assert.IsFalse(DataPath.IsPath("Save.Currencies[{k}]"));
            Assert.IsFalse(DataPath.IsPath("Save.Coins raw DeleteSave"));
        }

        // ---- the proof run, end to end -------------------------------------------------------------------------------

        private CheatProofRun Pump(CheatProofRequest request)
        {
            var run = new CheatProofRun(request);
            var frame = 0;
            foreach (var _ in CheatProofRun.Run(run, _root, new GenericCheatBridge(_root, new UguiDriver()), () => _clock))
            {
                _clock += 0.25;
                Assert.Less(frame++, 1000);
            }
            return run;
        }

        private static CheatProofRequest Snapshot(string command) =>
            CheatProofRequest.FromJson(new JObject { ["command"] = command, ["check"] = new JObject { ["snapshot"] = Root }, ["settleSec"] = 1 })!;

        private void Tick(string lever) => Assert.IsNull(Levers.SetApproved(_root, lever, true));

        [Test]
        public void ASnapshotProofOfAGiveProposesTheCurrencyEntryFirstAndItReadsBack()
        {
            const string give = "call NovaFixtures.PlayerData.FxCheats.GiveTopaz";
            Tick(give);
            Tick(CheatCheck.SnapshotLever(Root));
            // a give is risky: a snapshot check changes nothing about that — it waits for the non-production tick
            var held = Pump(Snapshot(give));
            Assert.IsFalse(held.Ran);
            StringAssert.Contains(CheatRisk.NonProductionLabel, held.LeverRefused!);
            Assert.AreEqual(50, FxFirebase.UserData!.Currencies.Currencies["topaz"].Amount);
            Assert.IsNull(Levers.SetNonProduction(_root, true));
            var run = Pump(Snapshot(give));
            Assert.IsTrue(run.Ran && run.RunOk, run.LeverRefused + " | " + string.Join(" | ", run.Log));
            var facts = JObject.Parse(run.ToFactsJson("0.13.2"));
            var changed = (JArray)facts["changed"]!;
            Assert.AreEqual(Topaz, changed[0]!["path"]!.Value<string>());
            Assert.AreEqual(50, changed[0]!["before"]!.Value<double>());
            Assert.AreEqual(10050, changed[0]!["after"]!.Value<double>());
            Assert.AreEqual(2, facts["changedTotal"]!.Value<int>(), "the unrelated timer is there too — a snapshot only proposes");
            Assert.IsTrue(facts["before"]!["cut"]!.Type == JTokenType.Null);
            // the proposal is a check the kit can run: its `get` lever is tickable, and it reads the value back
            Assert.IsNull(Levers.NotTickableReason(CheatCheck.GetLever(Topaz)));
            Assert.IsNotNull(CheatCheck.FromJson(new JObject { ["get"] = Topaz, ["expect"] = "increase" }, out var why), why);
        }

        [Test]
        public void ASnapshotProofOfALevelSkipProposesTheLevelCount()
        {
            const string skip = "call NovaFixtures.PlayerData.FxCheats.SkipLevel";
            Tick(skip);
            Tick(CheatCheck.SnapshotLever(Root));
            var facts = JObject.Parse(Pump(Snapshot(skip)).ToFactsJson("0.13.2"));
            var changed = ((JArray)facts["changed"]!).Select(c => c["path"]!.Value<string>()).ToList();
            Assert.AreEqual(Root + ".MainLevels.Levels.Count", changed[0]);
        }

        [Test]
        public void ASnapshotThatSawNothingChangeSaysSoAndTheReadGoesThroughTheSameGateAsAGet()
        {
            const string nothing = "call NovaFixtures.PlayerData.FxCheats.Nothing";
            Tick(nothing);
            // not ticked: the snapshot's read is refused by the gate, and the cheat is not run
            var refused = Pump(Snapshot(nothing));
            Assert.IsFalse(refused.Ran);
            StringAssert.Contains(CheatCheck.SnapshotLever(Root), refused.LeverRefused);
            Tick(CheatCheck.SnapshotLever(Root));
            var facts = JObject.Parse(Pump(Snapshot(nothing)).ToFactsJson("0.13.2"));
            Assert.AreEqual(0, ((JArray)facts["changed"]!).Count);
            Assert.AreEqual(0, facts["changedTotal"]!.Value<int>());
        }
    }
}
