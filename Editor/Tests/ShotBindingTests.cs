using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
        // the sixth audit, S1: .NET's `$` also matches BEFORE a final "\n", so `^…$` let "Knight\n" through the
        // binder while the gate's `\z` refused the command it made — two grammars. `\A…\z` is one.
        [TestCase("Knight\n", false)]
        public void ValidateValue_RejectsInjectableValues(string value, bool expected)
        {
            Assert.AreEqual(expected, ShotBinding.ValidateValue(value));
        }

        [TestCase("hero", true)]
        [TestCase("heroId", true)]
        [TestCase("Hero", false)]     // must start lowercase
        [TestCase("hero_id", false)]  // no underscores
        [TestCase("", false)]
        [TestCase("hero\n", false)]  // the sixth audit: `\A…\z`, not `^…$`
        public void ValidateName_EnforcesTheCharset(string name, bool expected)
        {
            Assert.AreEqual(expected, ShotBinding.ValidateName(name));
        }

        /// <summary>The sixth audit, S1 (the auditor's Q3, kept): a relay caller that reads names line by line binds
        /// "Knight\n". The binder wrote <c>SelectHero Knight\n</c>, which the gate's <c>\z</c> refused even with the
        /// template ticked. Now the binder refuses it by name, as it refuses any value outside the value class.</summary>
        [Test]
        public void TryApply_AValueEndingInANewlineIsRefusedByTheBinder()
        {
            Assert.IsFalse(ShotBinding.TryApply("SelectHero {hero}", Declared, Bind("hero", "Knight\n"), out var result, out var error));
            StringAssert.Contains("is not allowed", error);
            Assert.AreEqual("SelectHero {hero}", result, "a refused binding substitutes nothing");
            // CONTROL: the same value without the newline binds
            Assert.IsTrue(ShotBinding.TryApply("SelectHero {hero}", Declared, Bind("hero", "Knight"), out var ok, out _));
            Assert.AreEqual("SelectHero Knight", ok);
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

        /// <summary>
        /// K7 — the `\z` in MatchesTemplate's pattern, pinned. In .NET `$` ALSO matches just before
        /// a trailing newline, so with `$` an approved `SelectHero {hero}` would match
        /// "SelectHero Knight\n" — and the bridge underneath splits a command on whitespace. A
        /// mutation run flipped `\z` to `$` and every test stayed green.
        /// </summary>
        [Test]
        public void AValueWithATrailingNewlineIsNotAMatch()
        {
            Assert.IsFalse(ShotBinding.MatchesTemplate("SelectHero {hero}", "SelectHero Knight\n"));
            Assert.IsFalse(ShotBinding.MatchesTemplate("SelectHero {hero}", "SelectHero Knight\r\n"));
            Assert.IsFalse(ShotBinding.MatchesTemplate("SelectHero {hero}", "SelectHero Knight\n\n"));
            Assert.IsTrue(ShotBinding.MatchesTemplate("SelectHero {hero}", "SelectHero Knight"));
            // …and through the gate that asks the question for real.
            Assert.IsFalse(Levers.Allows(new[] { "SelectHero {hero}" }, "SelectHero Knight\n"));
        }

        // ---- M3 (the fourth audit, 2026-09-21): a template match is bounded in time --------------------

        /// <summary>k ADJACENT placeholders against a candidate that fails at its last character: the
        /// auditor measured 7.5 s for k = 11 (×6.4 per placeholder). Adjacent templates can no longer be
        /// ticked, and the match itself now has a time limit that reads as NO MATCH — fail closed.</summary>
        private static (string template, string candidate) Pathological(int k) =>
            ("SelectHero " + string.Concat(Enumerable.Range(0, k).Select(i => "{p" + i + "}")) + "Z",
             "SelectHero " + new string('a', 3 * k));

        /// <summary>
        /// THE BOUND IS 2,000 ms, 20 TIMES THE 100 ms MATCH TIMEOUT (the twelfth audit, M2). The mechanism is pinned without a
        /// clock: the match TIMED OUT, and read as no match. The wall bound only says the time limit is the one that ended
        /// it. .NET checks a regex's time limit periodically, so on a busy machine the match overshoots its 100 ms: 99–101 ms
        /// alone (twenty runs), 592 ms beside three other Unity runs (the r11 flake, against the old 500 ms bound). 2,000 ms
        /// is over three times that worst case, and far under what the same match takes with no limit at all: 7.5 s in the
        /// fourth audit, about 10.8 s a match in this fold's run with the limit removed (KT_NONE).
        /// </summary>
        [Test]
        public void MatchesTemplate_APathologicalTemplateFailsClosedQuickly()
        {
            ShotBinding.ResetTimeoutLogForTests(_ => { });
            try
            {
                var (template, candidate) = Pathological(11);
                var sw = Stopwatch.StartNew();
                var matched = ShotBinding.TryMatchTemplate(template, candidate, out var timedOut);
                sw.Stop();
                Assert.IsTrue(timedOut, "the match must end on its time limit — the mechanism, not the machine's speed");
                Assert.IsFalse(matched, "a timed-out match must read as NO match");
                Assert.IsFalse(ShotBinding.MatchesTemplate(template, candidate), "the public question fails closed too");
                // A NUMBER, not a multiple of MatchTimeout: a bound that grew with the limit could not see the limit grow
                Assert.Less(sw.ElapsedMilliseconds, 2000, $"one template match took {sw.ElapsedMilliseconds} ms");
                // CONTROL: the same shape still matches a candidate that fits it
                Assert.IsTrue(ShotBinding.MatchesTemplate("SelectHero {a}{b}Z", "SelectHero abZ"));
            }
            finally
            {
                ShotBinding.ResetTimeoutLogForTests(null);
            }
        }

        [Test]
        public void MatchesTemplate_ATimeoutIsLoggedOnceNotPerCall()
        {
            var logs = new List<string>();
            ShotBinding.ResetTimeoutLogForTests(m => logs.Add(m));
            try
            {
                var (template, candidate) = Pathological(11);
                Assert.IsFalse(ShotBinding.MatchesTemplate(template, candidate));
                Assert.IsFalse(ShotBinding.MatchesTemplate(template, candidate));
                Assert.AreEqual(1, logs.Count, "a timed-out match is logged once, not on every repaint");
                StringAssert.Contains("timed out", logs[0]);
            }
            finally
            {
                ShotBinding.ResetTimeoutLogForTests(null);
            }
        }

        // ---- M4 (the fifth audit, 2026-09-21): a repeated placeholder name is ONE value ----------------

        [Test]
        public void MatchesTemplate_ARepeatedNameIsOneValueExactlyAsTheBinderWritesIt()
        {
            Assert.IsTrue(ShotBinding.TryApply("set Player.{a} {a}", new[] { "a" }, Bind("a", "coins"), out var bound, out _));
            Assert.AreEqual("set Player.coins coins", bound, "control: the binder writes one value into both");
            Assert.IsTrue(ShotBinding.MatchesTemplate("set Player.{a} {a}", bound), "the binder's own output is a legal binding");
            Assert.IsFalse(ShotBinding.MatchesTemplate("set Player.{a} {a}", "set Player.coins 999"),
                "no binding of `{a} {a}` holds two different values");
            Assert.IsTrue(ShotBinding.MatchesTemplate("SelectHero {x} {y} {x}", "SelectHero a b a"));
            Assert.IsFalse(ShotBinding.MatchesTemplate("SelectHero {x} {y} {x}", "SelectHero a b c"));
            Assert.IsTrue(ShotBinding.MatchesTemplate("set Player.{s} {v}", "set Player.coins 999"), "control: two names, two values");
        }

        [Test]
        public void SameShape_ComparesWhichPlaceholdersShareAName()
        {
            Assert.IsFalse(ShotBinding.SameShape("set Player.{a} {a}", "set Player.{s} {v}"));
            Assert.IsTrue(ShotBinding.SameShape("set Player.{a} {a}", "set Player.{x} {x}"));
            Assert.IsTrue(ShotBinding.SameShape("set Player.{s} {v}", "set Player.{a} {b}"), "control: names aside");
            Assert.IsFalse(ShotBinding.SameShape("SelectHero {x} {y} {x}", "SelectHero {x} {y} {y}"));
        }

        /// <summary>The seventh audit, B14 (a surviving mutant: the gate's value class widened to take ':'). The binder
        /// (<c>ValidateValue</c>, <c>TryApply</c>) and the gate's match (<c>MatchesTemplate</c>) must give ONE answer
        /// for every value at the edge of the value class, in both directions.</summary>
        [Test]
        public void TheBinderAndTheGateGiveOneAnswerAtTheValueClassBoundary()
        {
            var edges = new[] { ":", "@", "\r", "\n", "\u2028", "\u0085", " ", "\u00A0", "\u00C4" };
            var values = new List<string> { "Knight", "Kni.ght-1_x" }; // the positive controls
            foreach (var c in edges)
            {
                values.Add("Knight" + c);
                values.Add("Kni" + c + "ght");
            }
            var valid = 0;
            foreach (var v in values)
            {
                var shown = string.Concat(v.Select(ch => ch < 32 || ch > 126 ? $"<U+{(int)ch:X4}>" : ch.ToString()));
                var byValue = ShotBinding.ValidateValue(v);
                var byBinder = ShotBinding.TryApply("SelectHero {hero}", new[] { "hero" },
                    new Dictionary<string, string> { ["hero"] = v }, out var written, out _);
                var byGate = ShotBinding.MatchesTemplate("SelectHero {hero}", "SelectHero " + v);
                Assert.AreEqual(byValue, byBinder, $"'{shown}': ValidateValue={byValue}, TryApply={byBinder}");
                Assert.AreEqual(byBinder, byGate, $"'{shown}': the binder says {byBinder}, the gate's match says {byGate}");
                if (byBinder) Assert.IsTrue(ShotBinding.MatchesTemplate("SelectHero {hero}", written), shown);
                if (byValue) valid++;
            }
            Assert.AreEqual(2, valid, "control: the two plain values are values, and no edge character is");
        }
    }
}
