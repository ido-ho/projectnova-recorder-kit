using System.Collections.Generic;
using NUnit.Framework;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// Spec §8.7 / §8.6 — the pure parsers behind "button names" and "the game's own words".
    /// The YAML below is the SHAPE Unity writes for a uGUI prefab: a root Canvas, a Shop panel, a
    /// BuyButton with a Button component whose m_OnClick calls ShopUI.Buy, and a TMP child label.
    /// </summary>
    public class ExportUiScanTests
    {
        private const string ButtonGuid = "4e29b1a8efbd4b44bb3f3716e73f07ff";
        private const string ShopUiGuid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string TmpGuid = "f4688fdb7df04437aeb418b961361dc5";

        private const string Prefab =
            "%YAML 1.1\n" +
            "%TAG !u! tag:unity3d.com,2011:\n" +
            "--- !u!1 &100\nGameObject:\n  m_Name: Canvas\n" +
            "--- !u!224 &101\nRectTransform:\n  m_GameObject: {fileID: 100}\n  m_Father: {fileID: 0}\n" +
            "--- !u!1 &200\nGameObject:\n  m_Name: Shop\n" +
            "--- !u!224 &201\nRectTransform:\n  m_GameObject: {fileID: 200}\n  m_Father: {fileID: 101}\n" +
            "--- !u!114 &202\nMonoBehaviour:\n  m_GameObject: {fileID: 200}\n  m_Script: {fileID: 11500000, guid: " + ShopUiGuid + ", type: 3}\n" +
            "--- !u!1 &300\nGameObject:\n  m_Name: BuyButton\n" +
            "--- !u!224 &301\nRectTransform:\n  m_GameObject: {fileID: 300}\n  m_Father: {fileID: 201}\n" +
            "--- !u!114 &302\nMonoBehaviour:\n  m_GameObject: {fileID: 300}\n" +
            "  m_Script: {fileID: 11500000, guid: " + ButtonGuid + ", type: 3}\n" +
            "  m_Interactable: 1\n" +
            "  m_OnClick:\n    m_PersistentCalls:\n      m_Calls:\n" +
            "      - m_Target: {fileID: 202}\n        m_TargetAssemblyTypeName: ShopUI, Assembly-CSharp\n        m_MethodName: Buy\n        m_Mode: 1\n" +
            "  m_Transition: 1\n" +
            "--- !u!1 &400\nGameObject:\n  m_Name: Label\n" +
            "--- !u!224 &401\nRectTransform:\n  m_GameObject: {fileID: 400}\n  m_Father: {fileID: 301}\n" +
            "--- !u!114 &402\nMonoBehaviour:\n  m_GameObject: {fileID: 400}\n  m_Script: {fileID: 11500000, guid: " + TmpGuid + ", type: 3}\n  m_text: Buy 100 gems\n";

        private static string? Names(string guid) => guid switch
        {
            ButtonGuid => "Button",
            ShopUiGuid => "ShopUI",
            TmpGuid => "TextMeshProUGUI",
            _ => null,
        };

        [Test]
        public void AButtonIsFound_WithItsPath_ItsChildLabel_AndWhatItCalls()
        {
            var rows = ExportUiScan.Parse("Assets/UI/Shop.prefab", Prefab, Names);
            Assert.AreEqual(1, rows.Count, "one clickable element — the panel and the label are not buttons");
            var r = rows[0];
            Assert.AreEqual("Canvas/Shop/BuyButton", r.Path);
            Assert.AreEqual("BuyButton", r.Name);
            Assert.AreEqual("Button", r.Component);
            Assert.AreEqual("Buy 100 gems", r.Label, "the nearest child's text");
            CollectionAssert.AreEqual(new[] { "ShopUI.Buy" }, r.OnClick);
        }

        [Test]
        public void AStudioOwnButton_IsFoundByItsOnClickBlock_NotByAListOfTypeNames()
        {
            var own = Prefab.Replace(ButtonGuid, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var rows = ExportUiScan.Parse("Assets/UI/Shop.prefab", own, g => g == "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" ? "BetterButton" : Names(g));
            Assert.AreEqual("BetterButton", rows[0].Component);
        }

        [Test]
        public void ABinaryFile_YieldsNothing_AndSaysSo()
        {
            var notes = new List<string>();
            CollectionAssert.IsEmpty(ExportUiScan.Parse("Assets/UI/Bin.prefab", "\u0000\u0001binary", Names, notes));
            Assert.AreEqual(1, notes.Count);
            CollectionAssert.IsEmpty(ExportUiScan.Parse("Assets/UI/Empty.prefab", null, Names));
        }

        [Test]
        public void ACycleInParents_DoesNotHang()
        {
            var cyc = Prefab.Replace("m_GameObject: {fileID: 100}\n  m_Father: {fileID: 0}", "m_GameObject: {fileID: 100}\n  m_Father: {fileID: 301}");
            Assert.AreEqual(1, ExportUiScan.Parse("Assets/UI/Shop.prefab", cyc, Names).Count);
        }

        // ---- audit of 0a8addb6: nested prefabs, dead targets, folded labels -----------------------

        private const string Nested =
            "%YAML 1.1\n" +
            "--- !u!1 &100\nGameObject:\n  m_Name: Popup\n" +
            "--- !u!224 &101\nRectTransform:\n  m_GameObject: {fileID: 100}\n  m_Father: {fileID: 0}\n" +
            // a nested prefab instance hung under Popup, renamed Button_Continue
            "--- !u!1001 &500\nPrefabInstance:\n  m_Modification:\n    serializedVersion: 3\n    m_TransformParent: {fileID: 101}\n    m_Modifications:\n" +
            "    - target: {fileID: 1, guid: cccccccccccccccccccccccccccccccc, type: 3}\n      propertyPath: m_Name\n      value: Button_Continue\n      objectReference: {fileID: 0}\n" +
            "--- !u!224 &501 stripped\nRectTransform:\n  m_CorrespondingSourceObject: {fileID: 2, guid: cccccccccccccccccccccccccccccccc, type: 3}\n  m_PrefabInstance: {fileID: 500}\n" +
            "--- !u!1 &502 stripped\nGameObject:\n  m_CorrespondingSourceObject: {fileID: 1, guid: cccccccccccccccccccccccccccccccc, type: 3}\n  m_PrefabInstance: {fileID: 500}\n" +
            // a Button ADDED as an override on the nested instance's root
            "--- !u!114 &503\nMonoBehaviour:\n  m_GameObject: {fileID: 502}\n  m_Script: {fileID: 11500000, guid: " + ButtonGuid + ", type: 3}\n" +
            "  m_OnClick:\n    m_PersistentCalls:\n      m_Calls:\n" +
            "      - m_Target: {fileID: 0}\n        m_TargetAssemblyTypeName: Gone, Assembly-CSharp\n        m_MethodName: Dead\n" +
            "      - m_Target: {fileID: 777}\n        m_TargetAssemblyTypeName: PocketRoll.UI.AnimatedUIElement, Assembly-CSharp\n        m_MethodName: Play\n" +
            "  m_Transition: 1\n" +
            "--- !u!114 &504\nMonoBehaviour:\n  m_GameObject: {fileID: 502}\n  m_Script: {fileID: 11500000, guid: " + TmpGuid + ", type: 3}\n" +
            "  m_text: 'Lucky\n\n    Chest ''x2'''\n  m_isRightToLeft: 0\n";

        [Test]
        public void ANestedPrefabsButton_KeepsItsWholePath_AndDeadOrUnresolvedTargetsAreHandled()
        {
            var rows = ExportUiScan.Parse("Assets/UI/Popup.prefab", Nested, Names);
            Assert.AreEqual(1, rows.Count);
            var r = rows[0];
            Assert.AreEqual("Popup/Button_Continue", r.Path, "the instance's name, hung under its m_TransformParent");
            Assert.AreEqual("Button_Continue", r.Name);
            CollectionAssert.AreEqual(new[] { "AnimatedUIElement.Play" }, r.OnClick,
                "a {fileID: 0} target is dropped (Unity skips it); an unresolved one falls back to m_TargetAssemblyTypeName");
            Assert.AreEqual("Lucky Chest 'x2'", r.Label, "a folded single-quoted label is joined and its '' decoded");
        }

        [Test]
        public void ADoubleQuotedLabel_DecodesItsEscapes()
        {
            var p = Prefab.Replace("m_text: Buy 100 gems", "m_text: \"Buy \\u00e9\\\"gems\\\"\"");
            Assert.AreEqual("Buy \u00e9\"gems\"", ExportUiScan.Parse("a.prefab", p, Names)[0].Label);
        }

        [TestCase("Assets/Resources/Localization/Text/translations_en.json", true)]   // RL, verbatim
        [TestCase("Assets/Libraries/ankonoankoLocalizationTool/Editor/Resources/Lang/en.json", false)]   // a tool's own UI
        [TestCase("Assets/Localization/English.csv", true)]
        [TestCase("Assets/i18n/strings.en-us.json", true)]
        [TestCase("Assets/Localisation/base.po", true)]
        [TestCase("Assets/Resources/Localization/Text/translations_ru.json", false)]
        [TestCase("Assets/Resources/Localization/Text/translations_meta.json", false)]
        [TestCase("Assets/Scripts/Localization/Translation.cs", false)]
        [TestCase("Assets/Data/en.json", false)]                                      // no localization marker
        [TestCase("Assets/Localization/Tokens/enemy.json", false)]                    // 'en' only inside a word
        public void IsStringsFile_TakesTheSourceLanguageOnly(string path, bool expected)
        {
            Assert.AreEqual(expected, ExportScan.IsStringsFile(path), path);
        }
    }
}
