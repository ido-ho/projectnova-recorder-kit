using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// E.2's fifth audit, the kit half — THE DUMP MARKER. A row of the on-screen dump
    /// (<see cref="ReflectionCheatBridge.OnScreenRows"/>, the rows `ui-dump` prints and a probe
    /// reports) says when the name and index it prints do NOT reach that object in the kit's own
    /// click search: the row ends with "  (not what this name reaches)". Before it, the kit printed
    /// the same text for an object its click reaches and one it does not (the audit's rows A2, A7,
    /// A8, A15 and A18), so no rule over the text could tell them apart. And the facts say how many
    /// rows the kit cut past its row cap (<c>uiDumpCut</c>).
    ///
    /// The UI is built in code, in the edit-mode scene, the way
    /// <c>ProbeTests.OnScreenRows_AreExactlyTheRowsUiDumpPrints</c> builds its objects; every object
    /// name carries this test's own prefix (except A18's `Btn@Play` and `Btn`, which ARE the case, under
    /// a prefixed root), each test reads only the rows of its own names, and every root is destroyed.
    /// </summary>
    public class DumpMarkerTests
    {
        /// <summary>The contract's text, spelled here and not read from the kit's constant: the
        /// renderer reads exactly these bytes, so a change to them must turn a test red.</summary>
        private const string Mark = "  (not what this name reaches)";
        private const string Unstable = " (unstable — index order is not guaranteed)";

        private readonly List<GameObject> _made = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _made)
                if (go != null)
                    UnityEngine.Object.DestroyImmediate(go);
            _made.Clear();
        }

        private GameObject Root(string name, bool canvas)
        {
            var go = canvas ? new GameObject(name, typeof(Canvas)) : new GameObject(name);
            _made.Add(go);
            return go;
        }

        private static GameObject Child(GameObject parent, string name, string? label = null)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            if (label != null)
                Label(go, label);
            return go;
        }

        /// <summary>The label is a child's uGUI Text, as it usually is on a button. The child's name
        /// matches none of Tab|Button|Btn|Toggle, so it is never a row of its own.</summary>
        private static void Label(GameObject go, string text)
        {
            var l = new GameObject("DmLabelText", typeof(RectTransform));
            l.transform.SetParent(go.transform, false);
            l.AddComponent<Text>().text = text;
        }

        private static List<string> RowsOf(string prefix) =>
            ReflectionCheatBridge.OnScreenRows().Where(r => r.StartsWith(prefix, StringComparison.Ordinal)).ToList();

        // ---- the marker: rows whose printed name does not reach that object ---------------------------

        /// <summary>A2: a `[candidate]` object outside every canvas, and no object of that name in the
        /// click search — `click` on the printed name acts on nothing.</summary>
        [Test]
        public void A2_ACandidateOutsideEveryCanvas_WithNoObjectOfThatNameInTheClickSearch_IsMarked()
        {
            Root("DmA2Tab", canvas: false);
            CollectionAssert.AreEqual(new[] { "DmA2Tab [candidate: no components]" + Mark }, RowsOf("DmA2Tab"));
        }

        /// <summary>A7: the printed row is a Selectable under no canvas; the one object of its name in
        /// the click search is a canvas object the dump never prints (no Selectable, a name matching
        /// no Tab|Button|Btn|Toggle). `click` on the printed name presses that unlisted object.</summary>
        [Test]
        public void A7_ARowWhoseSameNamedCanvasTwinIsNotPrinted_IsMarked()
        {
            var canvas = Root("DmA7Root", canvas: true);
            Child(canvas, "DmA7Foo");
            Root("DmA7Foo", canvas: false).AddComponent<Button>();
            CollectionAssert.AreEqual(new[] { "DmA7Foo [Button]" + Mark }, RowsOf("DmA7Foo"),
                "the printed row names an object the click search does not reach — the click presses the canvas one");
        }

        /// <summary>A8: two objects that printed ONE identical row (one in the click search, one not)
        /// collapsed in the dump's SortedSet. Now two rows, and only the one the finder does not reach
        /// is marked. They sort by their full text, as every row does: the unmarked row is a prefix of
        /// the marked one, so it comes first.</summary>
        [Test]
        public void A8_TwoObjectsThatPrintedOneIdenticalRow_AreTwoRows_AndOnlyTheUnreachedOneIsMarked()
        {
            var canvas = Root("DmA8Root", canvas: true);
            Child(canvas, "DmA8Btn");
            Root("DmA8Btn", canvas: false);
            CollectionAssert.AreEqual(new[]
            {
                "DmA8Btn [candidate: no components]",
                "DmA8Btn [candidate: no components]" + Mark,
            }, RowsOf("DmA8Btn"));
        }

        /// <summary>A15: an "@L" row whose ONE match is another object. The printed object (a Selectable
        /// under no canvas, labelled "Go") is not in the click search; two canvas objects of its name
        /// are, so the kit prints "@Go" because exactly one of them — "Go now", a substring match — is
        /// found by `name@Go`. Neither canvas object is printed.</summary>
        [Test]
        public void A15_AnAtLabelRowWhoseOneMatchIsAnotherObject_IsMarked()
        {
            var canvas = Root("DmA15Root", canvas: true);
            Child(canvas, "DmA15Play", "Stop");
            Child(canvas, "DmA15Play", "Go now");
            var printed = Root("DmA15Play", canvas: false);
            printed.AddComponent<Button>();
            Label(printed, "Go");
            CollectionAssert.AreEqual(new[] { "DmA15Play@Go [Button]  \"Go\"" + Mark }, RowsOf("DmA15Play"));
        }

        /// <summary>A18: an object literally named `Btn@Play`. The kit's finder reads what follows "@"
        /// as a label, so the printed name reaches objects named "Btn" — none at first, then another
        /// object. The "Btn" object's own row is the control: its name reaches it.</summary>
        [Test]
        public void A18_AnObjectNamedBtnAtPlay_IsMarked_WhetherItsNameReachesNothingOrAnotherObject()
        {
            var canvas = Root("DmA18Root", canvas: true);
            Child(canvas, "Btn@Play", "Play");
            CollectionAssert.AreEqual(new[] { "Btn@Play [candidate: no components]  \"Play\"" + Mark },
                RowsOf("Btn@Play"), "no object is named \"Btn\": the printed name reaches nothing");

            Child(canvas, "Btn", "Play now");
            CollectionAssert.AreEqual(new[] { "Btn@Play [candidate: no components]  \"Play\"" + Mark },
                RowsOf("Btn@Play"), "the printed name now reaches the object named \"Btn\"");
            const string btnRow = "Btn [candidate: no components]  \"Play now\"";
            var btnRows = RowsOf("Btn ");
            CollectionAssert.Contains(btnRows, btnRow, "control: the object named \"Btn\" is printed");
            CollectionAssert.DoesNotContain(btnRows, btnRow + Mark, "control: the object named \"Btn\" is reached by its own name");
        }

        // ---- controls: rows the finder reaches carry no marker -----------------------------------------

        [Test]
        public void Control_AUniqueCanvasButton_IsNotMarked()
        {
            var canvas = Root("DmC1Root", canvas: true);
            Child(canvas, "DmC1Btn").AddComponent<Button>();
            CollectionAssert.AreEqual(new[] { "DmC1Btn [Button]" }, RowsOf("DmC1Btn"));
        }

        /// <summary>Every " #i" row: the kit printed i because `Find(name, i)` is that object.</summary>
        [Test]
        public void Control_EveryHashIndexRow_IsNotMarked()
        {
            var canvas = Root("DmC2Root", canvas: true);
            Child(canvas, "DmC2Btn");
            Child(canvas, "DmC2Btn");
            Child(canvas, "DmC2Btn");
            CollectionAssert.AreEqual(new[]
            {
                "DmC2Btn #0" + Unstable + " [candidate: no components]",
                "DmC2Btn #1" + Unstable + " [candidate: no components]",
                "DmC2Btn #2" + Unstable + " [candidate: no components]",
            }, RowsOf("DmC2Btn"));
        }

        /// <summary>An "@L" row whose one match is the object itself (A11): not marked. Two such rows,
        /// so a marker computed from the bare name at index 0 would mark the second.</summary>
        [Test]
        public void Control_AnAtLabelRowWhoseOneMatchIsItself_IsNotMarked()
        {
            var canvas = Root("DmC3Root", canvas: true);
            Child(canvas, "DmC3Btn", "Play");
            Child(canvas, "DmC3Btn", "Shop");
            CollectionAssert.AreEqual(new[]
            {
                "DmC3Btn@Play [candidate: no components]  \"Play\"",
                "DmC3Btn@Shop [candidate: no components]  \"Shop\"",
            }, RowsOf("DmC3Btn"));
        }

        // ---- the cut count ---------------------------------------------------------------------------

        private static JObject FactsOf(IEnumerable<string>? ui) =>
            ProbeFacts.Build("s", true, null, null, null, null, ui, 1, 1, "aa", "bb",
                Array.Empty<string>(), Array.Empty<string>(), null, 1f, true, null, "9.9.9");

        private static IEnumerable<string> Rows(int n) => Enumerable.Range(0, n).Select(i => "n" + i.ToString("000"));

        [Test]
        public void Cut_250RowsKeep200_AndSayTheyCut50()
        {
            var f = FactsOf(Rows(250));
            Assert.AreEqual(200, ((JArray)f["uiDump"]!).Count);
            Assert.AreEqual(JTokenType.Integer, f["uiDumpCut"]!.Type, "the cut is a whole number");
            Assert.AreEqual(50, f["uiDumpCut"]!.Value<int>());
        }

        [Test]
        public void Cut_150RowsKeepAll_AndSayTheyCutNone()
        {
            var f = FactsOf(Rows(150));
            Assert.AreEqual(150, ((JArray)f["uiDump"]!).Count);
            Assert.AreEqual(0, f["uiDumpCut"]!.Value<int>());
        }

        [TestCase(200, 0)]
        [TestCase(201, 1)]
        [TestCase(0, 0)]
        public void Cut_AtTheCapBoundary(int rows, int cut)
        {
            Assert.AreEqual(cut, FactsOf(Rows(rows))["uiDumpCut"]!.Value<int>());
        }

        /// <summary>Blank rows are dropped before the cap and are not rows the cap cut; no list is a
        /// cut of 0, never an absent field.</summary>
        [Test]
        public void Cut_CountsOnlyRowsPastTheCap_NotBlanks_AndIsPresentWithNoList()
        {
            // indexed, not `Value<int>("uiDumpCut")`: that reads an ABSENT field as 0
            Assert.AreEqual(0, FactsOf(Rows(200).Concat(new[] { "", " ", null! }))["uiDumpCut"]!.Value<int>());
            Assert.AreEqual(50, FactsOf(Rows(250).Concat(new[] { "", " ", null! }))["uiDumpCut"]!.Value<int>(),
                "past the cap, the blanks are still not rows the cap cut");
            Assert.AreEqual(0, FactsOf(null)["uiDumpCut"]!.Value<int>());
        }
    }
}
