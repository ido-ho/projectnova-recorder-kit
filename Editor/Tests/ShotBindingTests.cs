using System.Collections.Generic;
using NUnit.Framework;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// Substitution is the one place a mis-bound shot could silently film the wrong content, so
    /// every failure mode here is a HARD error rather than a best-effort replacement.
    /// </summary>
    public class ShotBindingTests
    {
        private static readonly IReadOnlyList<string> Declared = new[] { "hero" };
        private static IReadOnlyDictionary<string, string> Bind(params string[] kv)
        {
            var d = new Dictionary<string, string>();
            for (var i = 0; i < kv.Length; i += 2) d[kv[i]] = kv[i + 1];
            return d;
        }

        [Test]
        public void Substitutes_ADeclaredBoundPlaceholder()
        {
            Assert.IsTrue(ShotBinding.TryApply("stage-hero {hero}", Declared,
                Bind("hero", "hero.draco"), out var result, out var error));
            Assert.AreEqual("stage-hero hero.draco", result);
            Assert.IsEmpty(error);
        }

        [Test]
        public void Substitutes_EveryOccurrence()
        {
            Assert.IsTrue(ShotBinding.TryApply("a {hero} b {hero}", Declared,
                Bind("hero", "hero.draco"), out var result, out _));
            Assert.AreEqual("a hero.draco b hero.draco", result);
        }

        /// <summary>The whole class of bug this design must avoid: never substitute an empty string.</summary>
        [Test]
        public void UnboundPlaceholder_IsAHardErrorNamingTheParameter()
        {
            Assert.IsFalse(ShotBinding.TryApply("stage-hero {hero}", Declared,
                Bind(), out _, out var error));
            StringAssert.Contains("hero", error);
            StringAssert.Contains("no value", error);
        }

        [Test]
        public void UndeclaredPlaceholder_IsAHardError()
        {
            Assert.IsFalse(ShotBinding.TryApply("stage-hero {villain}", Declared,
                Bind("villain", "x"), out _, out var error));
            StringAssert.Contains("villain", error);
            StringAssert.Contains("not declared", error);
        }

        [Test]
        public void BindingForAnUndeclaredParameter_IsAHardError()
        {
            Assert.IsFalse(ShotBinding.TryApply("stage-hero {hero}", Declared,
                Bind("hero", "hero.draco", "extra", "y"), out _, out var error));
            StringAssert.Contains("extra", error);
            StringAssert.Contains("not declared", error);
        }

        [Test]
        public void TextWithNoPlaceholders_PassesThroughUnchanged()
        {
            Assert.IsTrue(ShotBinding.TryApply("sweep-pointers", Declared,
                Bind("hero", "hero.draco"), out var result, out _));
            Assert.AreEqual("sweep-pointers", result);
        }

        // Run splits commands on spaces, so a value with whitespace or '@' could inject a second
        // command or forge a label selector.
        [TestCase("hero.draco", true)]
        [TestCase("hero-draco_2", true)]
        [TestCase("hero draco", false)]
        [TestCase("hero;rm", false)]
        [TestCase("Button@Label", false)]
        [TestCase("", false)]
        public void ValidateValue_RejectsInjectableValues(string value, bool expected)
        {
            Assert.AreEqual(expected, ShotBinding.ValidateValue(value));
        }

        [TestCase("hero", true)]
        [TestCase("heroId", true)]
        [TestCase("Hero", false)]     // must start lowercase
        [TestCase("hero_id", false)]  // no underscores
        [TestCase("", false)]
        public void ValidateName_EnforcesTheCharset(string name, bool expected)
        {
            Assert.AreEqual(expected, ShotBinding.ValidateName(name));
        }

        [Test]
        public void RejectedValue_IsAHardErrorNotASubstitution()
        {
            Assert.IsFalse(ShotBinding.TryApply("stage-hero {hero}", Declared,
                Bind("hero", "hero draco"), out _, out var error));
            StringAssert.Contains("hero draco", error);
        }

        // The lint that catches a forgotten `parameters:` declaration. v2's pattern was {[a-z]+},
        // which missed the camelCase names this project's own vocabulary uses.
        [TestCase("stage-hero {hero}", true)]
        [TestCase("apply-loadout {heroLevel}", true)]
        [TestCase("sweep-pointers", false)]
        [TestCase("a {Hero} b", false)]      // not a valid param name → not a placeholder
        [TestCase("json {\"k\":1}", false)]
        public void HasPlaceholder_MatchesTheParameterNameCharset(string text, bool expected)
        {
            Assert.AreEqual(expected, ShotBinding.HasPlaceholder(text));
        }
    }
}
