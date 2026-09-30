using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

// The fresh audit of 0.13.2 (2026-09-27): the shapes its findings named, as fixtures.
namespace NovaFixtures.AuditData
{
    /// <summary>A lazy singleton whose Instance GETTER makes a scene object — and a STATIC data root next to it. Reading
    /// the static root must never touch the instance (audit M1).</summary>
    public class FxLazyOwner
    {
        public const string MarkerName = "FxLazyOwner — made by a property getter";
        public static int GetterRuns;
        private static GameObject? _made;
        public static GameObject? Instance
        {
            get
            {
                GetterRuns++;
                if (_made == null) _made = new GameObject(MarkerName);
                return _made;
            }
        }
        public static FxWallet Data = new();

        public static void Reset()
        {
            GetterRuns = 0;
            if (_made != null) UnityEngine.Object.DestroyImmediate(_made);
            _made = null;
            Data = new FxWallet();
        }
    }

    /// <summary>A NON-static root behind a getter-only lazy Instance: there is no existing instance to find, so the read
    /// fails — and the getter never runs.</summary>
    public class FxLazyInstanceOnly
    {
        public static int GetterRuns;
        private static FxLazyInstanceOnly? _cache;
        public static FxLazyInstanceOnly Instance { get { GetterRuns++; return _cache ??= new FxLazyInstanceOnly(); } }
        public FxWallet Model = new();
        internal static void Reset() { GetterRuns = 0; _cache = null; }
    }

    /// <summary>A non-static root whose instance ALREADY EXISTS in a conventional static field behind a computed getter:
    /// found by the field, the getter still never runs.</summary>
    public class FxExistingSingleton
    {
        public static int GetterRuns;
        private static FxExistingSingleton? _instance;
        public static FxExistingSingleton? Instance { get { GetterRuns++; return _instance; } }
        public FxWallet Model = new();
        internal static void Make() { _instance = new FxExistingSingleton(); GetterRuns = 0; }
        internal static void Reset() { _instance = null; GetterRuns = 0; }
    }

    /// <summary>A base class carrying PRIVATE state and an auto-property (whose backing field is private) — what
    /// `GetFields(Instance|Public|NonPublic)` on the derived type misses (audit M3).</summary>
    [Serializable]
    public abstract class FxWalletBase
    {
        private int baseCounter = 4;
        public int Gems { get; set; } = 9;
        protected int shared = 1;
    }
    [Serializable]
    public class FxWallet : FxWalletBase
    {
        public int Coins = 50;
        public HashSet<int> Owned = new() { 1, 2, 3 };
        /// <summary>hides the base's `shared`: the most-derived one is read, once</summary>
        public new int shared = 2;
    }

    /// <summary>An object holding a credential-named field is not walked at all (the token must not even be counted).</summary>
    public class FxAuth { public string IdToken = "secret"; public string Uid = "u"; public HashSet<int> Providers = new() { 1 }; public int Logins = 3; }

    public class FxAuditRoot
    {
        public static FxAuditRoot? Root;
        public FxWallet Wallet = new();
        public FxAuth Auth = new();
        public Dictionary<string, int> Big = new();
    }
}

namespace ProjectNova.RecorderKit.Tests
{
    using NovaFixtures.AuditData;

    /// <summary>Kit 0.13.2, the fresh audit's findings, each red first: a static root never touches the owner's Instance
    /// (M1), a huge dictionary cannot outrun the time budget (M2), base-class fields are walked (M3), a set gives its count
    /// (L4), an object holding a credential is not read, and the grammar refuses a trailing newline (L7).</summary>
    public class SnapshotAuditTests
    {
        private const string AuditRoot = "NovaFixtures.AuditData.FxAuditRoot.Root";

        [SetUp]
        public void SetUp()
        {
            FxLazyOwner.Reset();
            FxLazyInstanceOnly.Reset();
            FxExistingSingleton.Reset();
            FxAuditRoot.Root = new FxAuditRoot();
            SnapshotRead.BudgetMsForTests = null;
        }

        [TearDown]
        public void TearDown()
        {
            FxLazyOwner.Reset();
            FxExistingSingleton.Reset();
            FxAuditRoot.Root = null;
            SnapshotRead.BudgetMsForTests = null;
        }

        [Test]
        public void AStaticRootNeverTouchesTheOwnersInstance()
        {
            var r = SnapshotRead.ReadFull("NovaFixtures.AuditData.FxLazyOwner.Data", out var error);
            Assert.IsNull(error);
            Assert.AreEqual(50, r!.Leaves["NovaFixtures.AuditData.FxLazyOwner.Data.Coins"]);
            Assert.IsTrue(GameReflection.TryGetPath("NovaFixtures.AuditData.FxLazyOwner.Data.Coins", out var v, out var err), err);
            Assert.AreEqual(50, v);
            Assert.AreEqual(0, FxLazyOwner.GetterRuns, "the owner's Instance getter ran for a static root");
            Assert.IsNull(GameObject.Find(FxLazyOwner.MarkerName), "a read made an object in the scene");
        }

        [Test]
        public void ANonStaticRootUsesOnlyAnInstanceThatAlreadyExists()
        {
            // no existing instance: refused by name, and the lazy getter never ran
            Assert.IsFalse(GameReflection.TryGetPath("NovaFixtures.AuditData.FxLazyInstanceOnly.Model.Coins", out _, out var err));
            Assert.AreEqual(0, FxLazyInstanceOnly.GetterRuns);
            StringAssert.Contains("no live instance", err);
            // an instance that exists in a conventional static field: found there, the getter still never ran
            FxExistingSingleton.Make();
            Assert.IsTrue(GameReflection.TryGetPath("NovaFixtures.AuditData.FxExistingSingleton.Model.Coins", out var v, out err), err);
            Assert.AreEqual(50, v);
            Assert.AreEqual(0, FxExistingSingleton.GetterRuns);
        }

        [Test]
        public void AHugeDictionaryCannotOutrunTheTimeBudget()
        {
            var big = FxAuditRoot.Root!.Big;
            for (var i = 0; i < 1_000_000; i++) big["k" + i] = i;
            SnapshotRead.BudgetMsForTests = 20;
            var watch = Stopwatch.StartNew();
            var r = SnapshotRead.ReadFull(AuditRoot, out var error);
            watch.Stop();
            Assert.IsNull(error);
            Assert.Less(watch.ElapsedMilliseconds, 250, $"the 20 ms budget took {watch.ElapsedMilliseconds} ms");
            Assert.AreEqual(1_000_000, r!.Leaves[AuditRoot + ".Big.Count"]);
            Assert.LessOrEqual(r.Leaves.Keys.Count(k => k.StartsWith(AuditRoot + ".Big[")), SnapshotRead.MaxEntries);
            // a budget already spent stops INSIDE the enumeration, and says so
            SnapshotRead.BudgetMsForTests = -1;
            watch.Restart();
            r = SnapshotRead.ReadFull(AuditRoot, out error);
            Assert.IsNotNull(r!.Cut);
            Assert.Less(watch.ElapsedMilliseconds, 250);
        }

        [Test]
        public void BaseClassFieldsAndAutoPropertiesAreWalkedAndReadBack()
        {
            var r = SnapshotRead.ReadFull(AuditRoot, out var error)!;
            Assert.IsNull(error);
            var w = AuditRoot + ".Wallet";
            Assert.AreEqual(50, r.Leaves[w + ".Coins"]);
            Assert.AreEqual(9, r.Leaves[w + ".Gems"], "an auto-property declared on a base class");
            Assert.AreEqual(4, r.Leaves[w + ".baseCounter"], "a private field declared on a base class");
            Assert.AreEqual(2, r.Leaves[w + ".shared"], "a hidden base field: the most-derived one, once");
            Assert.AreEqual(3, r.Leaves[w + ".Owned.Count"], "a HashSet gives its count");
            foreach (var path in new[] { w + ".Gems", w + ".baseCounter", w + ".Owned.Count" })
                Assert.IsTrue(GameReflection.TryGetPath(path, out _, out var err), $"{path}: a `get` cannot read it back ({err})");
        }

        [Test]
        public void AnObjectHoldingACredentialIsNotReadAtAll()
        {
            var r = SnapshotRead.ReadFull(AuditRoot, out _)!;
            Assert.IsFalse(r.Leaves.Keys.Any(k => k.Contains(".Auth")), string.Join(", ", r.Leaves.Keys.Where(k => k.Contains(".Auth"))));
        }

        [Test]
        public void TheGrammarRefusesATrailingNewline()
        {
            Assert.IsFalse(DataPath.IsPath("Game.Save.Coins\n"));
            Assert.IsFalse(DataPath.IsKey("topaz\n"));
            Assert.IsTrue(DataPath.IsPath("Game.Save.Coins"));
        }
    }
}
