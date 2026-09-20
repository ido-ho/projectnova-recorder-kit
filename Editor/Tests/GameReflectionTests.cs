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
    }
}
