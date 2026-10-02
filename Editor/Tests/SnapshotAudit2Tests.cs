using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

// The second fresh check of 0.13.2 (2026-09-27): a path THROUGH `.Instance`, runtime wrappers over game collections,
// an unbounded hashed-key scan, the salt's handling, and the credential words.
namespace NovaFixtures.Audit2Data
{
    [Serializable] public class FxPlayer { public int Coins = 5; }

    /// <summary>A lazy singleton reached THROUGH `.Instance` in a path (`FxLazyManager.Instance.Player.Coins`): its getter
    /// builds the manager — it must never run.</summary>
    public class FxLazyManager
    {
        public static int GetterRuns;
        public static int ComputedRuns;
        private static FxLazyManager? _instance;
        public static FxLazyManager Instance { get { GetterRuns++; return _instance ??= new FxLazyManager(); } }
        /// <summary>a static property with a body that is not called Instance: never run either</summary>
        public static FxPlayer Computed { get { ComputedRuns++; return new FxPlayer(); } }
        /// <summary>a static AUTO-property: read through its backing field</summary>
        public static FxPlayer? Auto { get; set; }
        public FxPlayer Player = new();
        internal static void Reset() { GetterRuns = 0; ComputedRuns = 0; _instance = null; Auto = null; }
        internal static void Make() { _instance = new FxLazyManager(); }
    }

    /// <summary>A game-defined list: every member counts, so any call from the kit shows.</summary>
    public sealed class FxGameList : IList<int>, IList
    {
        public static int Calls;
        private readonly List<int> _items = new() { 1, 2, 3 };
        public int this[int index] { get { Calls++; return _items[index]; } set { Calls++; } }
        object? IList.this[int index] { get { Calls++; return _items[index]; } set { Calls++; } }
        public int Count { get { Calls++; return _items.Count; } }
        public bool IsReadOnly { get { Calls++; return true; } }
        public bool IsFixedSize { get { Calls++; return true; } }
        public bool IsSynchronized { get { Calls++; return false; } }
        public object SyncRoot { get { Calls++; return this; } }
        public void Add(int item) => Calls++;
        public int Add(object? value) { Calls++; return 0; }
        public void Clear() => Calls++;
        public bool Contains(int item) { Calls++; return false; }
        public bool Contains(object? value) { Calls++; return false; }
        public void CopyTo(int[] array, int arrayIndex) => Calls++;
        public void CopyTo(Array array, int index) => Calls++;
        public IEnumerator<int> GetEnumerator() { Calls++; return _items.GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() { Calls++; return _items.GetEnumerator(); }
        public int IndexOf(int item) { Calls++; return 0; }
        public int IndexOf(object? value) { Calls++; return 0; }
        public void Insert(int index, int item) => Calls++;
        public void Insert(int index, object? value) => Calls++;
        public bool Remove(int item) { Calls++; return false; }
        public void Remove(object? value) => Calls++;
        public void RemoveAt(int index) => Calls++;
    }

    public class FxSession { public string SessionKey = "s"; public int Level = 3; }
    public class FxPinned { public bool Pinned = true; public int Stars = 2; }
    public class FxPin { public string PinCode = "1234"; public int Tries = 1; }
    public class FxJwtHolder { public string Jwt = "x"; public int N = 1; }

    public class FxRoot2
    {
        public static FxRoot2? Data;
        public ReadOnlyCollection<int> WrapsGame = new(new FxGameList());
        public ReadOnlyCollection<int> WrapsRuntime = new(new List<int> { 7, 8 });
        public ReadOnlyDictionary<string, int> WrapsRuntimeDict = new(new Dictionary<string, int> { ["a"] = 1 });
        public FxSession Session = new();
        public FxPinned Pins = new();
        public FxPin Pin = new();
        public FxJwtHolder Jwt = new();
        public Dictionary<string, FxSession> Sessions = new() { ["p1"] = new FxSession() };
        public List<FxSession> SessionList = new() { new FxSession() };
        public Dictionary<string, int> Big = new();
    }
}

namespace ProjectNova.RecorderKit.Tests
{
    using NovaFixtures.Audit2Data;

    public class SnapshotAudit2Tests
    {
        private const string Root = "NovaFixtures.Audit2Data.FxRoot2.Data";
        private string _project = "";

        [SetUp]
        public void SetUp()
        {
            FxLazyManager.Reset();
            FxGameList.Calls = 0;
            FxRoot2.Data = new FxRoot2();
            FxGameList.Calls = 0;
            _project = Path.Combine(Path.GetTempPath(), "audit2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_project);
            DataKeys.ProjectRootForTests = _project;
            DataKeys.ForgetSaltForTests();
        }

        [TearDown]
        public void TearDown()
        {
            FxLazyManager.Reset();
            FxRoot2.Data = null;
            DataKeys.ProjectRootForTests = null;
            DataKeys.ForgetSaltForTests();
            try { Directory.Delete(_project, true); } catch (IOException) { }
        }

        // ---- HIGH: a path THROUGH .Instance ------------------------------------------------------------------------

        [Test]
        public void APathThroughInstanceNeverRunsTheGetter()
        {
            const string p = "NovaFixtures.Audit2Data.FxLazyManager.Instance.Player.Coins";
            Assert.IsFalse(GameReflection.TryGetPath(p, out _, out var err), "no manager exists yet: refused");
            StringAssert.Contains("FxLazyManager.Instance", err);
            StringAssert.Contains("never", err);
            Assert.IsNull(SnapshotRead.ReadFull("NovaFixtures.Audit2Data.FxLazyManager.Instance.Player", out var serr));
            Assert.IsNotNull(serr);
            Assert.IsFalse(GameReflection.TryGetPath("NovaFixtures.Audit2Data.FxLazyManager.Instance", out _, out _));
            Assert.AreEqual(0, FxLazyManager.GetterRuns, "the Instance getter ran — a lazy singleton would be built");
            // positive control: the manager EXISTS (in its private static field) → found there, the getter still never runs
            FxLazyManager.Make();
            Assert.IsTrue(GameReflection.TryGetPath(p, out var v, out err), err);
            Assert.AreEqual(5, v);
            Assert.AreEqual(0, FxLazyManager.GetterRuns);
        }

        [Test]
        public void AStaticPropertyWithABodyIsNeverRunAndAnAutoPropertyIsReadByItsField()
        {
            Assert.IsFalse(GameReflection.TryGetPath("NovaFixtures.Audit2Data.FxLazyManager.Computed.Coins", out _, out var err));
            StringAssert.Contains("Computed", err);
            Assert.AreEqual(0, FxLazyManager.ComputedRuns);
            FxLazyManager.Auto = new FxPlayer { Coins = 9 };
            Assert.IsTrue(GameReflection.TryGetPath("NovaFixtures.Audit2Data.FxLazyManager.Auto.Coins", out var v, out err), err);
            Assert.AreEqual(9, v);
        }

        // ---- MED: runtime wrappers over game collections -------------------------------------------------------------

        [Test]
        public void ARuntimeWrapperOverAGameCollectionIsNotOpened()
        {
            var r = SnapshotRead.ReadFull(Root, out var error)!;
            Assert.IsNull(error);
            Assert.AreEqual(0, FxGameList.Calls, "a ReadOnlyCollection over a game list called the game's code");
            Assert.IsFalse(r.Leaves.Keys.Any(k => k.Contains(".WrapsGame")));
            Assert.AreEqual(2, r.Leaves[Root + ".WrapsRuntime.Count"], "a wrapper over a runtime list is read");
            Assert.AreEqual(8, r.Leaves[Root + ".WrapsRuntime[1]"]);
            Assert.AreEqual(1, r.Leaves[Root + ".WrapsRuntimeDict[a]"]);
            Assert.IsFalse(GameReflection.TryGetPath(Root + ".WrapsGame.Count", out _, out _));
            Assert.IsFalse(GameReflection.TryGetPath(Root + ".WrapsGame[0]", out _, out _));
            Assert.AreEqual(0, FxGameList.Calls);
        }

        // ---- MED: the hashed-key scan is bounded ------------------------------------------------------------------

        [Test]
        public void AHashedKeyLookupIsBoundedInTime()
        {
            for (var i = 0; i < 1_000_000; i++) FxRoot2.Data!.Big["key" + i] = i;
            var watch = Stopwatch.StartNew();
            Assert.IsFalse(GameReflection.TryGetPath(Root + ".Big[#000000000000]", out _, out var err));
            watch.Stop();
            Assert.Less(watch.ElapsedMilliseconds, 500, $"a hashed lookup took {watch.ElapsedMilliseconds} ms");
            StringAssert.Contains("no key matching", err);
        }

        // ---- the salt -----------------------------------------------------------------------------------------------

        [Test]
        public void TheSaltLivesUnderLibraryAndAnUnreadableOneIsNeverRotated()
        {
            // a pre-0.3 project with a LEGACY project-root AdRelay/ session: the salt still goes under Library/
            Directory.CreateDirectory(Path.Combine(_project, "AdRelay"));
            File.WriteAllText(Path.Combine(_project, "AdRelay", "status.json"), "{}");
            var h1 = DataKeys.Hash("Xy3kP9qLmN2vB7tR5wZ1aC4dE6fG");
            var salt = Path.Combine(_project, "Library", "AdRelay", "snapshot-key");
            Assert.IsTrue(File.Exists(salt), "the salt is under Library/AdRelay");
            Assert.IsFalse(File.Exists(Path.Combine(_project, "AdRelay", "snapshot-key")), "never in the legacy root git may commit");
            Assert.IsFalse(File.Exists(salt + ".tmp"));
            Assert.IsNull(DataKeys.SaltNote);
            DataKeys.ForgetSaltForTests();
            Assert.AreEqual(h1, DataKeys.Hash("Xy3kP9qLmN2vB7tR5wZ1aC4dE6fG"), "stable across reloads");
            // a salt file that cannot be read is NOT replaced: a session salt, said
            File.WriteAllText(salt, "not a salt");
            DataKeys.ForgetSaltForTests();
            var h2 = DataKeys.Hash("Xy3kP9qLmN2vB7tR5wZ1aC4dE6fG");
            Assert.AreNotEqual(h1, h2);
            Assert.AreEqual("not a salt", File.ReadAllText(salt), "the unreadable salt file was rotated");
            StringAssert.Contains("snapshot-key", DataKeys.SaltNote);
        }

        // ---- the credential words ----------------------------------------------------------------------------------

        [Test]
        public void TheCredentialWordsAreWholeWordsAndACredentialsDictionarySendsNothing()
        {
            var r = SnapshotRead.ReadFull(Root, out _)!;
            string[] Under(string m) => r.Leaves.Keys.Where(k => k.StartsWith(Root + "." + m)).ToArray();
            CollectionAssert.IsEmpty(Under("Session."), "a session key");
            CollectionAssert.IsEmpty(Under("Pin."), "a PIN code");
            CollectionAssert.IsEmpty(Under("Jwt."), "a JWT");
            CollectionAssert.IsNotEmpty(Under("Pins."), "'Pinned' is not a PIN");
            CollectionAssert.IsEmpty(Under("Sessions"), "a dictionary of credential holders: not even its count");
            CollectionAssert.IsEmpty(Under("SessionList"), "a list of credential holders: not even its count");
        }
    }
}
