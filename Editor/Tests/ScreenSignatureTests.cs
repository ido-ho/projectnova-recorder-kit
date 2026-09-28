using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>v3 P4 (§3.6a item 2) — the panel-root screen signature. UI built in code; names carry an <c>Ss</c> prefix;
    /// the root-canvas scan is filtered to this test's canvases.</summary>
    public class ScreenSignatureTests
    {
        private readonly List<GameObject> _made = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _made)
                if (go != null) Object.DestroyImmediate(go);
            _made.Clear();
        }

        private Canvas Root(string name)
        {
            var go = new GameObject(name, typeof(Canvas));
            _made.Add(go);
            return go.GetComponent<Canvas>();
        }

        private static GameObject Child(Component parent, string name) => Child(parent.gameObject, name);

        private static GameObject Child(GameObject parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent.transform, false);
            return go;
        }

        private List<Canvas> Mine() => ScreenSignature.RootCanvases().Where(c => c.name.StartsWith("Ss")).ToList();

        [Test]
        public void TheRootsAreTheActiveDirectChildrenOfEachRootCanvas_NamedCanvasSlashChild_Sorted()
        {
            var ui = Root("SsUi");
            Child(ui, "SsLobby");
            var hidden = Child(ui, "SsShop");
            hidden.SetActive(false);
            var hud = Root("SsHud");
            Child(hud, "SsBar");
            var o = ScreenSignature.Observe(Mine());
            CollectionAssert.AreEqual(new[] { "SsHud/SsBar", "SsUi/SsLobby" }, o.Roots);
            Assert.AreEqual("SsHud/SsBar|SsUi/SsLobby", o.Key);
        }

        [Test]
        public void ANestedCanvasIsPartOfItsRootsScreen_NotAScreenOfItsOwn()
        {
            var ui = Root("SsUi");
            var lobby = Child(ui, "SsLobby");
            var nested = Child(lobby, "SsNested");
            nested.AddComponent<Canvas>();
            Child(nested, "SsDeep");
            var mine = Mine();
            Assert.AreEqual(1, mine.Count, "only the root canvas is scanned");
            var o = ScreenSignature.Observe(mine);
            CollectionAssert.AreEqual(new[] { "SsUi/SsLobby" }, o.Roots);
            Assert.IsTrue(o.Names.Contains("SsDeep"), "its names are still on the screen");
            Assert.AreEqual("SsUi/SsLobby", ScreenSignature.RootOf(nested.transform.GetChild(0).gameObject));
        }

        [Test]
        public void APopupIsAnExtraRoot_TheBaseScreensRootsUnchanged()
        {
            var ui = Root("SsUi");
            Child(ui, "SsLobby");
            var popup = Child(ui, "SsOffer");
            popup.SetActive(false);
            var before = ScreenSignature.Observe(Mine());
            popup.SetActive(true);
            var after = ScreenSignature.Observe(Mine());
            CollectionAssert.IsSubsetOf(before.Roots, after.Roots);
            CollectionAssert.AreEqual(new[] { "SsUi/SsOffer" }, after.Roots.Except(before.Roots).ToList());
        }

        [Test]
        public void TheSameScreenGivesTheSameKey_ADifferentScreenADifferentOne()
        {
            var ui = Root("SsUi");
            var lobby = Child(ui, "SsLobby");
            var equip = Child(ui, "SsEquipment");
            equip.SetActive(false);
            var a = ScreenSignature.Observe(Mine()).Key;
            Child(lobby, "SsBadge"); // a change INSIDE the lobby is not another screen
            var b = ScreenSignature.Observe(Mine()).Key;
            lobby.SetActive(false);
            equip.SetActive(true);
            var c = ScreenSignature.Observe(Mine()).Key;
            Assert.AreEqual(a, b);
            Assert.AreNotEqual(a, c);
        }

        [Test]
        public void RootOf_IsThePanelAnObjectLivesUnder_NullForTheCanvasItselfAndForNoCanvas()
        {
            var ui = Root("SsUi");
            var lobby = Child(ui, "SsLobby");
            var btn = Child(Child(lobby, "SsRow"), "SsPlay");
            Assert.AreEqual("SsUi/SsLobby", ScreenSignature.RootOf(btn));
            Assert.AreEqual("SsUi/SsLobby", ScreenSignature.RootOf(lobby));
            Assert.IsNull(ScreenSignature.RootOf(ui.gameObject));
            var loose = new GameObject("SsLoose");
            _made.Add(loose);
            Assert.IsNull(ScreenSignature.RootOf(loose));
        }

        [Test]
        public void AddedIsTheNamesNewSinceTheBefore_AndButtonsAreTheClickableNames()
        {
            var ui = Root("SsUi");
            var lobby = Child(ui, "SsLobby");
            var before = ScreenSignature.Observe(Mine());
            var panel = Child(ui, "SsEquipment");
            var back = Child(panel, "SsBack");
            back.AddComponent<Image>();
            back.AddComponent<Button>();
            var after = ScreenSignature.Observe(Mine());
            CollectionAssert.AreEqual(new[] { "SsBack", "SsEquipment" }, ScreenSignature.Added(before, after));
            CollectionAssert.AreEqual(new[] { "SsBack" }, after.Buttons);
            Assert.IsTrue(after.Names.Contains(lobby.name));
        }
    }
}
