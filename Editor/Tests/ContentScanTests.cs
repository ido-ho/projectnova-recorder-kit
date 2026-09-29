using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// The id must come from the asset's DATA, never its filename. Rogue Legend's hero asset is
    /// `Rogue.asset` holding `<Id>k__BackingField: hero.default` — a filename fallback would invent
    /// the id "Rogue", which `validate` would then bless as real.
    /// </summary>
    public class ContentScanTests
    {
        private class PlainIdField : ScriptableObject { public string Id = "id.plain"; }
        private class AutoProperty : ScriptableObject { [field: SerializeField] public string Id { get; private set; } = "id.auto"; }
        private class UnderscoreField : ScriptableObject { public string _id = "id.underscore"; }
        private class NoId : ScriptableObject { public int Unrelated = 1; }

        private class ShadowedPropertyBase : ScriptableObject { public int Id { get; set; } }
        private class ShadowedPropertyDerived : ShadowedPropertyBase
        {
            public new string Id { get; set; } = "id.shadowed";
        }

        [Test]
        public void ResolvesAPlainIdField()
        {
            var a = ScriptableObject.CreateInstance<PlainIdField>();
            Assert.AreEqual("id.plain", ContentScan.ResolveId(a));
            Object.DestroyImmediate(a);
        }

        /// <summary>The shape roguelegend actually uses — serialized as &lt;Id&gt;k__BackingField.</summary>
        [Test]
        public void ResolvesAnAutoPropertyBackingField()
        {
            var a = ScriptableObject.CreateInstance<AutoProperty>();
            Assert.AreEqual("id.auto", ContentScan.ResolveId(a));
            Object.DestroyImmediate(a);
        }

        [Test]
        public void ResolvesAnUnderscoreIdField()
        {
            var a = ScriptableObject.CreateInstance<UnderscoreField>();
            Assert.AreEqual("id.underscore", ContentScan.ResolveId(a));
            Object.DestroyImmediate(a);
        }

        [Test]
        public void ReturnsNullRatherThanFallingBackToTheAssetName()
        {
            var a = ScriptableObject.CreateInstance<NoId>();
            a.name = "SomeAssetName";
            Assert.IsNull(ContentScan.ResolveId(a), "a filename is not an id");
            Object.DestroyImmediate(a);
        }

        [Test]
        public void ReportsAnUnknownTypeAsUnreadableRatherThanOmittingIt()
        {
            var lines = string.Join("\n", ContentScan.Report(new[] { "NoSuchTypeAnywhere" }));
            StringAssert.Contains("NoSuchTypeAnywhere", lines);
            StringAssert.Contains("unreadable", lines);
        }

        /// <summary>
        /// `Type.GetProperty(name, ANY_INSTANCE)` throws AmbiguousMatchException — not null — when
        /// a derived class shadows a same-named base property of a different type (here: base
        /// `int Id`, derived `new string Id`). ResolveId must not let that escape and abort the
        /// whole Report() scan for every other declared type over one pathological asset.
        /// </summary>
        [Test]
        public void DoesNotThrowWhenAPropertyIsShadowedByADifferentTypeInADerivedClass()
        {
            var a = ScriptableObject.CreateInstance<ShadowedPropertyDerived>();
            Assert.DoesNotThrow(() => ContentScan.ResolveId(a));
            Object.DestroyImmediate(a);
        }

        /// <summary>
        /// Regression test for a real defect: Report() used to cap each source's id list at 8 and
        /// summarize the rest as "… N more". That was fine for a human-reading-a-sample probe, but
        /// `admiral content-index` machine-parses this exact text into the authoritative id list
        /// `admiral validate` checks against — a cap meant any source over 8 ids (roguelegend's
        /// LocalPetData has 25) produced an INCOMPLETE index that then falsely rejected legitimate
        /// ids. This test exercises the extracted formatting helper directly rather than going
        /// through Report() itself: Report() only reaches this code path after
        /// AssetDatabase.FindAssets finds real on-disk assets, and no test in this file (including
        /// ReportsAnUnknownTypeAsUnreadableRatherThanOmittingIt above, which hits the "no assets
        /// matched" branch precisely to avoid this) creates or cleans up real project assets — doing
        /// so here for the first time, just to prove a formatting fix, would add asset-creation/
        /// cleanup machinery to a suite that has never needed it, and risk leaking stray assets into
        /// the shared test project. FormatIdLines is the exact, and only, code that used to truncate,
        /// so testing it in isolation with 9+ ids covers the defect precisely.
        /// </summary>
        [Test]
        public void FormatIdLinesEmitsEveryIdWithNoCapAndNoMoreLine()
        {
            var ids = Enumerable.Range(0, 25).Select(i => $"pets.{i:D2}").Reverse().ToList();

            var lines = ContentScan.FormatIdLines(ids).ToList();

            Assert.AreEqual(25, lines.Count, "every id must be emitted — none dropped past a cap");
            Assert.IsFalse(lines.Any(l => l.Contains("more")),
                "no truncation/summary line should ever be emitted");
            CollectionAssert.AreEqual(
                ids.OrderBy(x => x).Select(id => $"    {id}"),
                lines,
                "lines must be sorted and 4-space indented regardless of input order");
        }
    }
}
