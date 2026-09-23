using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// Locks the reflection rules that were each learned the hard way on a live game. Every test here
    /// corresponds to a failure that cost real session time; a regression would silently return the
    /// onboarding process to guessing.
    /// </summary>
    public class GameReflectionTests
    {
        /// <summary>
        /// The old coercion ladder covered int/long/float/double only, so a public editor API taking
        /// (uint, uint, string) was unreachable through `call` — it reported just "no overload accepted
        /// those argument values", which reads like a wrong method name rather than a missing type.
        /// </summary>
        [Test]
        public void TryCoerce_HandlesEveryNumericWidth_NotJustIntAndLong()
        {
            foreach (var (want, raw, expected) in new (System.Type, string, object)[]
                     {
                         (typeof(uint), "1080", 1080u),
                         (typeof(ulong), "9000000000", 9000000000UL),
                         (typeof(short), "-7", (short)-7),
                         (typeof(ushort), "7", (ushort)7),
                         (typeof(byte), "255", (byte)255),
                         (typeof(decimal), "1.25", 1.25m),
                     })
            {
                Assert.IsTrue(GameReflection.TryCoerce(raw, want, out var v), $"{want.Name} '{raw}'");
                Assert.AreEqual(expected, v, want.Name);
            }
        }

        /// <summary>
        /// Culture-invariant on purpose: with the current culture, "1.5" parses as 15 on a
        /// comma-decimal machine — a WRONG value that succeeds, which is worse than failing.
        /// </summary>
        [Test]
        public void TryCoerce_ParsesDecimalPointRegardlessOfMachineCulture()
        {
            var prev = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    new System.Globalization.CultureInfo("de-DE"); // comma is the decimal separator
                Assert.IsTrue(GameReflection.TryCoerce("1.5", typeof(float), out var v));
                Assert.AreEqual(1.5f, (float)v!, 0.0001f);
            }
            finally { System.Threading.Thread.CurrentThread.CurrentCulture = prev; }
        }

        [Test]
        public void TryCoerce_RejectsNonConvertibleTargets()
        {
            Assert.IsFalse(GameReflection.TryCoerce("x", typeof(System.Collections.IList), out _));
        }

        /// <summary>
        /// Studio cheat doors are often <c>TryExecuteCommand(string, out string)</c>.
        /// ParameterType is String&; without unwrapping, every such call reports
        /// "no overload accepted those argument values".
        /// </summary>
        [Test]
        public void TryCoerce_UnwrapsByRefTypes()
        {
            Assert.IsTrue(GameReflection.TryCoerce("hi", typeof(string).MakeByRefType(), out var v));
            Assert.AreEqual("hi", v);
            Assert.IsTrue(GameReflection.TryCoerce("4", typeof(int).MakeByRefType(), out var n));
            Assert.AreEqual(4, n);
        }

        [Test]
        public void TryCall_FillsOutParameters_AndSurfacesThemInTheResult()
        {
            Assert.IsTrue(
                GameReflection.TryCall(typeof(FakeGame), "TryEcho", new[] { "hi", "_" }, out var result, out var error),
                error);
            var text = result?.ToString() ?? "";
            StringAssert.Contains("True", text);
            StringAssert.Contains("got:hi", text);
        }

        [Test]
        public void TryCall_OutInt_DoesNotNeedACoercibleDummy()
        {
            Assert.IsTrue(
                GameReflection.TryCall(typeof(FakeGame), "TryCount", new[] { "abcd", "_" }, out var result, out var error),
                error);
            StringAssert.Contains("4", result?.ToString());
        }

        // Stands in for a game's model class: a plain object with an Id, reachable only by chaining.
        private sealed class FakeHero
        {
            public string Id = "hero.default";
            public int Stars;
        }

        private sealed class FakeRoster
        {
            public FakeHero Equipped = new();
            public List<FakeHero> All = new() { new FakeHero { Id = "hero.a" }, new FakeHero { Id = "hero.b" } };
        }

        private static class FakeGame
        {
            public static FakeRoster Roster = new();
            public static int NextRoll;
            public static string Echo(string s) => "echo:" + s;
            public static int AddTo(int a, int b) => a + b;
            public static bool Flag(bool v) => v;
            public static List<T> AllOf<T>() where T : new() => new() { new T(), new T() };
            public static bool TryEcho(string input, out string response)
            {
                response = "got:" + input;
                return true;
            }
            public static bool TryCount(string input, out int n)
            {
                n = input.Length;
                return true;
            }
        }

        [SetUp]
        public void SetUp()
        {
            FakeGame.Roster = new FakeRoster();
            FakeGame.NextRoll = 0;
        }

        [Test]
        public void FindType_PrefersExactFullNameOverSimpleName()
        {
            // The live failure: "PlayerData" resolved to a third-party ParserTestNamespace.PlayerData
            // — a TEST type with none of the game's members — because a simple-name match in an
            // earlier assembly won. An exact FullName must beat any simple-name match.
            var byFullName = GameReflection.FindType(typeof(FakeRoster).FullName!);
            Assert.AreSame(typeof(FakeRoster), byFullName);
        }

        [Test]
        public void FindType_ReturnsNullForNonsense_AndCachesIt()
        {
            Assert.IsNull(GameReflection.FindType("NoSuchTypeAnywhere_zzz"));
            Assert.IsNull(GameReflection.FindType("NoSuchTypeAnywhere_zzz"), "negative hits are cached, not re-scanned");
        }

        /// <summary>A type built at run time in an assembly of its own, loaded NOW — the one way a test can load an assembly
        /// after the type index already exists.</summary>
        private static System.Type DefineLateType(string fullName)
        {
            var asm = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(
                new System.Reflection.AssemblyName("NovaLate" + System.Guid.NewGuid().ToString("N")),
                System.Reflection.Emit.AssemblyBuilderAccess.Run);
            return asm.DefineDynamicModule("m").DefineType(fullName, System.Reflection.TypeAttributes.Public).CreateType()!;
        }

        /// <summary>
        /// The fourteenth audit, ruling 5 — FindType answers from a name index built once per domain, and an assembly that
        /// loads after the index was built must still be seen ("rebuilt when an assembly loads"). The index is built first
        /// (a lookup), then an assembly loads, then its type is asked for by simple name and by full name.
        /// </summary>
        [Test]
        public void FindType_SeesATypeFromAnAssemblyLoadedAfterTheIndexWasBuilt()
        {
            Assert.AreSame(typeof(FakeRoster), GameReflection.FindType(typeof(FakeRoster).FullName!), "the index exists");
            var name = "NovaLateType" + System.Guid.NewGuid().ToString("N");
            var late = DefineLateType("NovaLate.Ns." + name);
            Assert.AreSame(late, GameReflection.FindType("NovaLate.Ns." + name), "by full name");
            // the lookup above APPENDED the new assembly (the fifteenth audit); the full build is the editor's, once per domain
            NUnit.Framework.TestContext.Out.WriteLine($"FindType's index: last full build {GameReflection.LastIndexBuildMsForTests:0.0} ms " +
                $"over {System.AppDomain.CurrentDomain.GetAssemblies().Length} assemblies, first built by {GameReflection.WarmedByForTests}");
            Assert.AreSame(late, GameReflection.FindType(name), "by simple name");
        }

        /// <summary>
        /// THE FIFTEENTH AUDIT, S1 — AN ASSEMBLY LOAD IS AN APPEND, NOT A REBUILD. The index existed; one assembly loads; the
        /// first lookup after it finds the new type by full and by simple name, with NO full build — it appended one
        /// assembly. On ea016964 every load threw the whole index away and the next lookup paid 65–112 ms to rebuild it.
        /// </summary>
        [Test]
        public void FindType_AppendsAnAssemblyLoadedAfterTheBuild_ItDoesNotRebuild()
        {
            Assert.IsNotNull(GameReflection.FindType(typeof(FakeRoster).FullName!), "the index exists");
            var builds = GameReflection.FullBuildsForTests;
            var appends = GameReflection.AppendsForTests;
            var name = "NovaAppend" + System.Guid.NewGuid().ToString("N");
            var late = DefineLateType("NovaAppend.Ns." + name);
            Assert.AreSame(late, GameReflection.FindType("NovaAppend.Ns." + name), "the first lookup after the load finds it");
            Assert.AreEqual(builds, GameReflection.FullBuildsForTests, "the first lookup after a load rebuilt the whole index");
            Assert.AreEqual(appends + 1, GameReflection.AppendsForTests, "one assembly loaded, one assembly appended");
            Assert.AreSame(late, GameReflection.FindType(name), "by simple name");
            Assert.AreEqual(builds, GameReflection.FullBuildsForTests, "and no build after it either");
        }

        /// <summary>
        /// THE FIFTEENTH AUDIT, M5 (KM_STALE_AFTER) — an assembly that loads WHILE the index is being built is indexed. The
        /// build's own GetTypes() loads assemblies (5 in the test host's first build); they are not in the build's snapshot,
        /// so they are queued, and the build appends them before it returns: found, with one full build and no second.
        /// </summary>
        [Test]
        public void FindType_FindsATypeFromAnAssemblyLoadedDuringTheBuild()
        {
            var name = "NovaDuring" + System.Guid.NewGuid().ToString("N");
            System.Type? during = null;
            GameReflection.DuringScanForTests = () => during = DefineLateType("NovaDuring.Ns." + name);
            var builds = GameReflection.FullBuildsForTests;
            GameReflection.RebuildForTests();
            Assert.IsNotNull(during, "the hook ran inside the build");
            Assert.AreEqual(builds + 1, GameReflection.FullBuildsForTests, "one full build");
            Assert.AreSame(during, GameReflection.FindType("NovaDuring.Ns." + name), "by full name");
            Assert.AreSame(during, GameReflection.FindType(name), "by simple name");
            Assert.AreEqual(builds + 1, GameReflection.FullBuildsForTests, "found without a second build");
        }

        /// <summary>
        /// THE APPEND'S OWN TRAP (the fifteenth fold) — an assembly that is BOTH queued and in a build's snapshot (it loaded
        /// before the build, and no lookup has appended it yet: at a domain's start, everything loaded between this class's
        /// first use and the editor's delayCall) is indexed once. Twice, its every simple name would be "ambiguous across 2
        /// assemblies" — the same type twice — and a unique name would log a warning it never did.
        /// </summary>
        [Test]
        public void FindType_AnAssemblyQueuedAndInTheBuildsSnapshotIsIndexedOnce()
        {
            Assert.IsNotNull(GameReflection.FindType(typeof(FakeRoster).FullName!), "the index exists");
            var name = "NovaOnce" + System.Guid.NewGuid().ToString("N");
            var late = DefineLateType("NovaOnce.Ns." + name); // queued, not yet appended
            var warnings = new System.Collections.Generic.List<string>();
            void OnLog(string message, string stack, UnityEngine.LogType type)
            {
                if (message.Contains("is ambiguous across")) warnings.Add(message);
            }
            UnityEngine.Application.logMessageReceived += OnLog;
            try
            {
                GameReflection.RebuildForTests(); // its snapshot holds the new assembly, and so does the queue
                Assert.AreSame(late, GameReflection.FindType(name), "by simple name");
            }
            finally { UnityEngine.Application.logMessageReceived -= OnLog; }
            CollectionAssert.IsEmpty(warnings, "a unique name was reported ambiguous: its assembly was indexed twice");
        }

        /// <summary>
        /// THE FIFTEENTH AUDIT, M5 (KM_FULLNAME_LAST) — two assemblies defining the SAME full name: the first in load order
        /// wins, which is the answer the per-name scan gave. Through an append, and again through a full build.
        /// </summary>
        [Test]
        public void FindType_TwoAssembliesWithTheSameFullName_TheFirstInLoadOrderWins()
        {
            Assert.IsNotNull(GameReflection.FindType(typeof(FakeRoster).FullName!), "the index exists");
            var full = "NovaSame.Ns.Same" + System.Guid.NewGuid().ToString("N");
            var first = DefineLateType(full);
            var second = DefineLateType(full);
            Assert.AreNotSame(first, second, "control: two types");
            Assert.AreSame(first, GameReflection.FindType(full), "appended: the first in load order");
            GameReflection.RebuildForTests();
            Assert.AreSame(first, GameReflection.FindType(full), "rebuilt: the first in load order");
        }

        /// <summary>
        /// THE FIFTEENTH AUDIT, M5 (KM_AMBIG_NOCLEAR) — a cached ambiguous choice is dropped when a load can change it, and
        /// the Assembly-CSharp preference then holds (until now it was pinned only by reading: the test host has no game
        /// assembly). Two assemblies define a simple name → the first is chosen and cached; then an assembly NAMED
        /// Assembly-CSharp defines it too → the game's type is chosen, and the warning names all three.
        /// </summary>
        [Test]
        public void FindType_AnAmbiguousChoiceIsJudgedAgainWhenAnAssemblyAddsACandidate()
        {
            var name = "NovaAmb" + System.Guid.NewGuid().ToString("N");
            var a = DefineLateType("AmbA." + name);
            DefineLateType("AmbB." + name);
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning,
                new System.Text.RegularExpressions.Regex($"'{name}' is ambiguous across 2 assemblies; chose AmbA\\.{name}"));
            Assert.AreSame(a, GameReflection.FindType(name), "no game type yet: the first");
            var game = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(
                    new System.Reflection.AssemblyName("Assembly-CSharp"), System.Reflection.Emit.AssemblyBuilderAccess.Run)
                .DefineDynamicModule("m").DefineType("AmbGame." + name, System.Reflection.TypeAttributes.Public).CreateType()!;
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning,
                new System.Text.RegularExpressions.Regex($"'{name}' is ambiguous across 3 assemblies; chose AmbGame\\.{name} \\(Assembly-CSharp\\)"));
            Assert.AreSame(game, GameReflection.FindType(name), "the cached choice survived a load that added a game candidate");
        }

        /// <summary>
        /// The resolution ORDER survives the index (ruling 5): an exact full name wins over simple-name matches; two
        /// simple-name matches with none in Assembly-CSharp take the FIRST in assembly load order and name every candidate.
        /// (The Assembly-CSharp preference itself needs a game assembly, which the test host does not have: by reading.)
        /// </summary>
        [Test]
        public void FindType_KeepsFullNameFirstAndTheFirstSimpleNameMatchInLoadOrder()
        {
            var name = "NovaTwin" + System.Guid.NewGuid().ToString("N");
            var first = DefineLateType("TwinA." + name);
            var second = DefineLateType("TwinB." + name);
            Assert.AreSame(second, GameReflection.FindType("TwinB." + name), "an exact full name wins");
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning,
                new System.Text.RegularExpressions.Regex($"'{name}' is ambiguous across 2 assemblies; chose TwinA\\.{name}"));
            Assert.AreSame(first, GameReflection.FindType(name), "the first simple-name match in load order");
            Assert.AreSame(first, GameReflection.FindType(name), "the same answer again, with the warning said once");
        }

        [Test]
        public void TryGetPath_WalksChainedPaths()
        {
            // Chaining is REQUIRED, not a nicety: one game's master onboarding gate was three hops
            // from a singleton, ending on a plain field of a plain model class.
            var ok = GameReflection.TryGetPath($"{typeof(FakeGame).FullName}.Roster.Equipped.Id",
                out var value, out var error);
            Assert.IsTrue(ok, error);
            Assert.AreEqual("hero.default", value);
        }

        [Test]
        public void TrySetPath_WritesThroughAChain_AndReportsUnwritable()
        {
            Assert.IsTrue(GameReflection.TrySetPath($"{typeof(FakeGame).FullName}.Roster.Equipped.Stars",
                "5", out var error), error);
            Assert.AreEqual(5, FakeGame.Roster.Equipped.Stars);

            Assert.IsFalse(GameReflection.TrySetPath($"{typeof(FakeGame).FullName}.Roster.Equipped.Nope",
                "1", out var err2));
            StringAssert.Contains("no writable member", err2);
        }

        [Test]
        public void TryGetPath_NullMidChain_FailsWithAnExplanationRatherThanThrowing()
        {
            FakeGame.Roster.Equipped = null!;
            Assert.IsFalse(GameReflection.TryGetPath($"{typeof(FakeGame).FullName}.Roster.Equipped.Id",
                out _, out var error));
            StringAssert.Contains("is null while walking", error);
        }

        [Test]
        public void TryReadMember_InstanceMemberWithNoTarget_ReturnsFalseInsteadOfThrowing()
        {
            // This exact case threw "Non-static method requires a target" from inside the kit's state
            // probe in Edit Mode, escaped the relay pump and wedged it, retrying every frame forever.
            Assert.IsFalse(GameReflection.TryReadMember(null, typeof(FakeHero), "Id", out _));
        }

        [Test]
        public void TryCall_CoercesArgs_AndPicksTheOverloadThatFits()
        {
            Assert.IsTrue(GameReflection.TryCall(typeof(FakeGame), "Echo", new[] { "hi" }, out var r1, out var e1), e1);
            Assert.AreEqual("echo:hi", r1);
            Assert.IsTrue(GameReflection.TryCall(typeof(FakeGame), "AddTo", new[] { "2", "3" }, out var r2, out var e2), e2);
            Assert.AreEqual(5, r2);
            Assert.IsTrue(GameReflection.TryCall(typeof(FakeGame), "Flag", new[] { "true" }, out var r3, out var e3), e3);
            Assert.AreEqual(true, r3);
        }

        [Test]
        public void TryCall_UncoercibleArg_FailsWithTheReason()
        {
            Assert.IsFalse(GameReflection.TryCall(typeof(FakeGame), "AddTo", new[] { "two", "3" }, out _, out var error));
            StringAssert.Contains("no overload accepted", error);
        }

        [Test]
        public void TryCallGeneric_SuppliesTheTypeArgumentAtRuntime()
        {
            // Generic methods are unreachable from a string command — nowhere to put T — which is why
            // named cheats needing one (a board's GetAllTiles<T>) must route through here.
            Assert.IsTrue(GameReflection.TryCallGeneric(typeof(FakeGame), "AllOf",
                new[] { typeof(FakeHero) }, System.Array.Empty<object?>(), out var result, out var error), error);
            Assert.AreEqual(2, ((System.Collections.IEnumerable)result!).Cast<object>().Count());
        }

        [Test]
        public void DescribeObject_SurfacesAnIdentifyingMember_NotTheTypeName()
        {
            // Domain types rarely override ToString(), so a content list rendered as
            // "FakeHero, FakeHero" — a successful read carrying zero information.
            StringAssert.Contains("hero.default", GameReflection.DescribeObject(new FakeHero()));
            Assert.AreEqual("7", GameReflection.DescribeObject(7));
            Assert.AreEqual("null", GameReflection.DescribeObject(null));
        }

        [Test]
        public void Render_CountsACollectionAndIdentifiesItsElements()
        {
            var rendered = GameReflection.Render(FakeGame.Roster.All);
            StringAssert.StartsWith("[2]", rendered);
            StringAssert.Contains("hero.a", rendered);
            StringAssert.Contains("hero.b", rendered);
        }

        [Test]
        public void DescribeApi_ListsGameMembers_AndOmitsInheritedUnityNoise()
        {
            var api = GameReflection.DescribeApi(typeof(FakeHero)).ToList();
            // Fields must be listed: one game's whole master onboarding gate was a public FIELD, so a
            // properties-and-methods-only dump would have hidden the best lever available.
            Assert.IsTrue(api.Any(l => l.Contains("Id")), "game-declared FIELD must appear");
            Assert.IsTrue(api.Any(l => l.Contains("Stars")), "game-declared FIELD must appear");
            Assert.IsFalse(api.Any(l => l.Contains("GetComponentInChildren")),
                "inherited Unity members must be filtered — otherwise the dump buries the 3 methods that matter");
        }

        [Test]
        public void DescribeApi_MarksReadOnlyFields_SoAnUnwritableLeverIsObviousUpFront()
        {
            var api = string.Join("\n", GameReflection.DescribeApi(typeof(ReadOnlyHolder)));
            StringAssert.Contains("(read-only)", api);
        }

        private sealed class ReadOnlyHolder
        {
            public readonly int Fixed = 1;
        }

        /// <summary>
        /// THE SIXTEENTH AUDIT, M2 — THE INDEX IS BUILT BY THE FIRST delayCall, AHEAD OF A PUMP STARTED ON THE FIRST TICK.
        /// The relay's boot pumps on <c>EditorApplication.update</c>, so a waiting run-shot calls AdDirector.Run on update
        /// tick 1, which adds the director's pump to <c>update</c>; that pump first runs on tick 2. The free-verb pin counts
        /// builds inside pumps the TEST starts, after every delayCall has fired, so it could not tell the first delayCall from
        /// a later one (KM_R16_DOUBLE_DELAY survived it). <see cref="WarmupOrderRecorder"/> adds a handler the same way on
        /// tick 1 and notes the build count when it first runs: the index must already be built, once, by the delayCall.
        /// </summary>
        [Test]
        public void TheIndexIsBuiltByTheFirstDelayCall_BeforeAPumpAddedOnTheFirstEditorTickRuns()
        {
            var r = WarmupOrderRecorder.Snapshot;
            NUnit.Framework.TestContext.Out.WriteLine($"warmup order in this domain: {r}");
            // POSITIVE CONTROLS: the recorder ran, saw the index missing on tick 1, and its tick-1 handler ran later
            Assert.IsTrue(r.TickOneRan, "the recorder saw no update tick in this domain: " + r);
            Assert.AreEqual(0, r.BuildsAtTickOne, "the index already existed on update tick 1, so the recorder cannot see it missing: " + r);
            Assert.IsTrue(r.LateRan, "the handler added during update tick 1 never ran: " + r);
            Assert.AreEqual(1, r.BuildsWhenLateRan,
                "a pump added on the first editor tick ran before the index was built — the warm must be on the FIRST delayCall: " + r);
            Assert.AreEqual("delayCall", r.WarmedByWhenLateRan, "the index was built by a lookup, not by the editor's delayCall: " + r);
        }
    }

    /// <summary>
    /// Test assembly only (the sixteenth audit, M2): notes <see cref="GameReflection.FullBuildsForTests"/> on the first
    /// <c>update</c> tick after a domain load, and when a handler added DURING that tick first runs — the way the relay's
    /// tick-1 run-shot adds a director pump. Its state is this assembly's, never the kit's.
    /// </summary>
    [UnityEditor.InitializeOnLoad]
    internal static class WarmupOrderRecorder
    {
        internal readonly struct Record
        {
            public readonly bool TickOneRan, LateRan;
            public readonly int BuildsAtTickOne, BuildsWhenLateRan, LateTick;
            public readonly string? WarmedByWhenLateRan;
            public Record(bool tickOneRan, int buildsAtTickOne, bool lateRan, int buildsWhenLateRan, int lateTick, string? warmedBy)
            {
                TickOneRan = tickOneRan; BuildsAtTickOne = buildsAtTickOne; LateRan = lateRan;
                BuildsWhenLateRan = buildsWhenLateRan; LateTick = lateTick; WarmedByWhenLateRan = warmedBy;
            }
            public override string ToString() =>
                $"update#1 {(TickOneRan ? $"builds={BuildsAtTickOne}" : "not run")}; the handler added on update#1 " +
                (LateRan ? $"first ran on tick {LateTick}: builds={BuildsWhenLateRan} warmedBy={WarmedByWhenLateRan ?? "null"}" : "never ran");
        }

        private static int _ticks, _buildsAtTickOne = -1, _buildsWhenLateRan = -1, _lateTick = -1;
        private static bool _lateRan;
        private static string? _warmedByWhenLateRan;

        internal static Record Snapshot =>
            new(_buildsAtTickOne >= 0, _buildsAtTickOne, _lateRan, _buildsWhenLateRan, _lateTick, _warmedByWhenLateRan);

        static WarmupOrderRecorder()
        {
            UnityEditor.EditorApplication.update += OnUpdate; // added first, so it counts each tick before Late runs in it
        }

        private static void OnUpdate()
        {
            _ticks++;
            if (_ticks != 1) return;
            _buildsAtTickOne = GameReflection.FullBuildsForTests;
            UnityEditor.EditorApplication.update += Late;
        }

        private static void Late()
        {
            UnityEditor.EditorApplication.update -= Late;
            UnityEditor.EditorApplication.update -= OnUpdate;
            _lateRan = true;
            _lateTick = _ticks;
            _buildsWhenLateRan = GameReflection.FullBuildsForTests;
            _warmedByWhenLateRan = GameReflection.WarmedByForTests;
        }
    }
}
