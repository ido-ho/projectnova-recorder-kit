using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// Video-first plan step 2 (§7) — the export sends what the art library needs and could not get
    /// before: the files a Spine rig is made of, and the particle-system prefabs, only for what the game
    /// USES; and the header says which art kinds this kit sends (`artKinds`), so "no Spine file" is
    /// never read as "no rigs" (invariant 50).
    ///
    /// The Spine half is driven through the pure planner: the test host has no Spine runtime, so no
    /// `SkeletonDataAsset` can be imported here — Rogue Legend's shape (rig → skeleton TextAsset +
    /// SpineAtlasAsset → atlas TextAsset + Material → page Texture2D) is reproduced as data instead.
    /// </summary>
    public class ExportRigsTests
    {
        private static Dictionary<string, string> Map(params string[] kv)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < kv.Length; i += 2) d[kv[i]] = kv[i + 1];
            return d;
        }

        [Test]
        public void SpineFileGuids_SendsEachUsedRigsSkeleton_AtlasText_AndPages_InPathOrder_AndNothingOfAnUnusedRig()
        {
            var typeOf = Map(
                "rigB", "SkeletonDataAsset", "skB", "TextAsset", "atB", "SpineAtlasAsset", "txB", "TextAsset", "mtB", "Material", "pgB", "Texture2D",
                "rigA", "SkeletonDataAsset", "skA", "TextAsset", "atA", "SpineAtlasAsset", "txA", "TextAsset", "mtA", "Material", "pgA", "Texture2D", "pgA2", "Texture2D",
                "rigU", "SkeletonDataAsset", "skU", "TextAsset",
                "cs", "MonoScript");
            var pathOf = Map(
                "rigA", "Assets/A/A_SkeletonData.asset", "rigB", "Assets/B/B_SkeletonData.asset", "rigU", "Assets/U/U_SkeletonData.asset",
                "pgA", "Assets/A/A.png", "pgA2", "Assets/A/A2.png");
            var deps = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["rigA"] = new List<string> { "atA", "cs", "skA" },
                ["atA"] = new List<string> { "mtA", "txA" },
                ["mtA"] = new List<string> { "pgA2", "pgA" },
                ["rigB"] = new List<string> { "skB", "atB" },
                ["atB"] = new List<string> { "txB", "mtB" },
                ["mtB"] = new List<string> { "pgB" },
                ["rigU"] = new List<string> { "skU" },
            };
            var used = new HashSet<string> { "rigA", "rigB" };
            CollectionAssert.AreEqual(
                new[] { "skA", "txA", "pgA", "pgA2", "skB", "txB", "pgB" },
                ExportCollector.SpineFileGuids(typeOf, pathOf, deps, used),
                "rig A before rig B (by path); skeleton, then the atlas text, then its pages; the script is not art; the unused rig sends nothing");
        }

        [Test]
        public void SpineFileGuids_AFileTwoRigsShare_IsSentOnce()
        {
            var typeOf = Map("r1", "SkeletonDataAsset", "r2", "SkeletonDataAsset", "sk", "TextAsset");
            var deps = new Dictionary<string, List<string>> { ["r1"] = new List<string> { "sk" }, ["r2"] = new List<string> { "sk" } };
            CollectionAssert.AreEqual(new[] { "sk" }, ExportCollector.SpineFileGuids(typeOf, Map(), deps, new HashSet<string> { "r1", "r2" }));
        }

        [Test]
        public void CountPrefabDocs_CountsTheFilesOwnDocumentsByClass_NotWordsAndNotStripped()
        {
            var c = ExportCollector.CountPrefabDocs(
                "%YAML 1.1\n--- !u!1 &1\nGameObject:\n  m_Name: ParticleSystem\n--- !u!198 &2\nParticleSystem:\n" +
                "--- !u!224 &3\nRectTransform:\n--- !u!1001 &4\nPrefabInstance:\n--- !u!1 &5 stripped\nGameObject:\n" +
                "--- !u!1980 &6\nX:\n--- !u!1 &7\r\nGameObject:\r\n");
            Assert.AreEqual(1, c.ParticleSystems, "one class-198 document; a name is not a component; 1980 is not 198");
            Assert.AreEqual(2, c.GameObjects, "the stripped one is a nested prefab's object, not this file's");
            Assert.AreEqual(1, c.PrefabInstances);
            Assert.AreEqual(1, c.RectTransforms);
            Assert.AreEqual(0, ExportCollector.CountPrefabDocs("").ParticleSystems);
        }

        /// <summary>The SAME rows as apps/renderer/src/game-ads/art/effect-rule.cases.json, word for word (the renderer's
        /// art-cards.spec reads that file; this list must stay equal to it).</summary>
        [TestCase("Saga_Panel (a UI screen with one sparkle)", 1, 17, 34, 17, false)]
        [TestCase("GameEndHUD", 1, 20, 5, 20, false)]
        [TestCase("TowerChallenge_Panel", 2, 46, 9, 46, false)]
        [TestCase("Lobby_Chapter_1_BG (a scene with one sparkle)", 1, 16, 0, 0, false)]
        [TestCase("True_Fire_Idle_VFX (UI-space)", 1, 2, 0, 2, false)]
        [TestCase("ThunderstormVFX (built of nested prefabs)", 1, 1, 7, 0, false)]
        [TestCase("no ParticleSystem", 0, 1, 0, 0, false)]
        [TestCase("Ignite Battle Animation (boundary: 2 x 5 = 10 = 9 + 1)", 2, 9, 1, 0, true)]
        [TestCase("Lobby_Chapter_StageLava_BG", 4, 18, 0, 0, true)]
        [TestCase("Shurikan_energy", 1, 1, 0, 0, true)]
        public void IsEffectPrefab_HoldsTheSharedCases(string name, int ps, int go, int nested, int rt, bool effect)
        {
            var c = new PrefabCounts { ParticleSystems = ps, GameObjects = go, PrefabInstances = nested, RectTransforms = rt };
            Assert.AreEqual(effect, ExportCollector.IsEffectPrefab(c), name);
        }

        [Test]
        public void TheKitsArtKinds_AreInPlanOrder_EffectsAfterTextures()
        {
            CollectionAssert.AreEqual(new[] { "icon", "font", "audio", "spine", "texture", "vfx" }, ExportArtKinds.Sent);
        }

        [Test]
        public void TheHeader_WritesArtKindsOnlyWhenItsBuildSetThem_AndReadsThemBack()
        {
            Assert.IsNull(new ExportHeader().ToJson()["artKinds"], "a header nobody set the kinds on says nothing");
            var built = new ExportHeader { ArtKinds = new List<string>(ExportArtKinds.Sent) };
            var back = ExportHeader.FromJson(built.ToJsonString())!;
            CollectionAssert.AreEqual(ExportArtKinds.Sent, back.ArtKinds);
            CollectionAssert.AreEqual(ExportArtKinds.Sent, ((JArray)back.ToJson()["artKinds"]!).Select(t => (string)t!).ToArray());
        }

        [Test]
        public void AResumedHeaderFromAnOlderBuild_StaysWithoutArtKinds_NeverClaimsTheCurrentKitsKinds()
        {
            // review M3: NovaCaptureAgent reloads a header with FromJson and posts ToJson — an older build's header must not
            // come back claiming the kinds THIS kit sends
            var older = new ExportHeader().ToJson();
            older.Remove("artKinds");
            var back = ExportHeader.FromJson(older.ToString())!;
            Assert.IsNull(back.ArtKinds);
            Assert.IsNull(back.ToJson()["artKinds"]);
            // and an older build that sent fewer kinds keeps exactly those
            var fewer = new ExportHeader { ArtKinds = new List<string> { "icon", "font" } };
            CollectionAssert.AreEqual(new[] { "icon", "font" }, ExportHeader.FromJson(fewer.ToJsonString())!.ArtKinds);
        }
    }

    /// <summary>The effects half end to end, through a real export of a temp folder: a USED particle prefab is sent,
    /// a used prefab without one is not, and one past `vfxMaxFiles` is LISTED in overCap rather than dropped.</summary>
    public class ExportEffectsTests
    {
        private const string Root = "Assets/__NovaEffectsTest";
        private const string FxA = Root + "/Resources/Fx/A_Explosion.prefab";
        private const string FxB = Root + "/Resources/Fx/B_Sparks.prefab";
        private const string Plain = Root + "/Resources/Fx/C_Plain.prefab";
        private const string Unused = Root + "/Unused/D_Smoke.prefab";
        private const string Panel = Root + "/Resources/Fx/E_Panel.prefab";

        /// <summary>A UI screen with one sparkle: six RectTransform GameObjects and one ParticleSystem — not an effect.</summary>
        private static string PanelYaml()
        {
            var y = "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n";
            for (var i = 0; i < 6; i++)
                y += "--- !u!1 &" + (100 + i) + "\nGameObject:\n  m_ObjectHideFlags: 0\n  serializedVersion: 6\n  m_Component:\n  - component: {fileID: " + (200 + i) + "}\n  m_Layer: 5\n  m_Name: Item" + i + "\n  m_IsActive: 1\n" +
                     "--- !u!224 &" + (200 + i) + "\nRectTransform:\n  m_ObjectHideFlags: 0\n  m_GameObject: {fileID: " + (100 + i) + "}\n  m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}\n  m_LocalPosition: {x: 0, y: 0, z: 0}\n  m_LocalScale: {x: 1, y: 1, z: 1}\n  m_Children: []\n  m_Father: {fileID: 0}\n  m_AnchorMin: {x: 0, y: 0}\n  m_AnchorMax: {x: 1, y: 1}\n  m_AnchoredPosition: {x: 0, y: 0}\n  m_SizeDelta: {x: 0, y: 0}\n  m_Pivot: {x: 0.5, y: 0.5}\n";
            y += "--- !u!198 &19800\nParticleSystem:\n  m_ObjectHideFlags: 0\n  m_GameObject: {fileID: 100}\n";
            return y;
        }

        private string _tempRoot = "";
        private ExportResult _result = new ExportResult();
        private Dictionary<string, byte[]> _entries = new Dictionary<string, byte[]>();

        /// <summary>Text-serialized prefab YAML, written by hand so the test needs no particle module: Unity writes a
        /// ParticleSystem as its own `--- !u!198` document, and that header is all the kit reads.</summary>
        private static string PrefabYaml(string name, bool particles) =>
            "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n" +
            "--- !u!1 &100\nGameObject:\n  m_ObjectHideFlags: 0\n  serializedVersion: 6\n  m_Component:\n  - component: {fileID: 400}\n" +
            (particles ? "  - component: {fileID: 19800}\n" : "") +
            "  m_Layer: 0\n  m_Name: " + name + "\n  m_IsActive: 1\n" +
            "--- !u!4 &400\nTransform:\n  m_ObjectHideFlags: 0\n  m_GameObject: {fileID: 100}\n  m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}\n  m_LocalPosition: {x: 0, y: 0, z: 0}\n  m_LocalScale: {x: 1, y: 1, z: 1}\n  m_Children: []\n  m_Father: {fileID: 0}\n" +
            (particles ? "--- !u!198 &19800\nParticleSystem:\n  m_ObjectHideFlags: 0\n  m_GameObject: {fileID: 100}\n" : "");

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            LogAssert.ignoreFailingMessages = true;
            _tempRoot = Path.Combine(Path.GetTempPath(), "novakit-effects-" + Path.GetRandomFileName());
            Directory.CreateDirectory(Path.Combine(_tempRoot, "project"));
            foreach (var d in new[] { "Resources/Fx", "Unused" })
                Directory.CreateDirectory(Path.Combine(Application.dataPath, "..", Root, d));
            File.WriteAllText(Path.Combine(Application.dataPath, "..", FxA), PrefabYaml("A_Explosion", true));
            File.WriteAllText(Path.Combine(Application.dataPath, "..", FxB), PrefabYaml("B_Sparks", true));
            File.WriteAllText(Path.Combine(Application.dataPath, "..", Plain), PrefabYaml("C_Plain", false));
            File.WriteAllText(Path.Combine(Application.dataPath, "..", Unused), PrefabYaml("D_Smoke", true));
            File.WriteAllText(Path.Combine(Application.dataPath, "..", Panel), PanelYaml());
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            _result = new ExportResult();
            var request = new ExportRequest
            {
                ProjectRoot = Path.Combine(_tempRoot, "project"),
                OutDir = Path.Combine(_tempRoot, "out"),
                AssetsRoot = Root,
                PatternScanRoot = Path.Combine(_tempRoot, "project"),
                WorkspaceId = "ws-test",
                KitVersion = "0.17.0-test",
                SliceBudgetMs = 4,
                // room for ONE effect: the second used one must be listed, not dropped
                Caps = new ExportCaps { ThumbnailPx = 16, VfxMaxFiles = 1 },
            };
            var e = ExportCollector.Run(request, _result).GetEnumerator();
            var guard = 0;
            while (e.MoveNext()) if (++guard > 200000) Assert.Fail("the collector never finished");
            Assert.IsNull(_result.FatalError, "the fixture export must not be fatal");
            _entries = ExportArchiveWriterTests.ReadAllEntries(Path.Combine(_tempRoot, "out"));
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            AssetDatabase.DeleteAsset(Root);
            if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
        }

        private bool Sent(string assetPath) =>
            _entries.Keys.Any(k => k == ExportFormat.ArtFileEntry(AssetDatabase.AssetPathToGUID(assetPath), ".prefab"));

        [Test]
        public void AUsedParticlePrefab_IsSent_AsAnArtFile_WithAnArtIndexRow()
        {
            Assert.IsTrue(Sent(FxA), "the first used effect (by path) is sent");
            var guid = AssetDatabase.AssetPathToGUID(FxA);
            var index = _entries.Where(kv => kv.Key.StartsWith("art/index", StringComparison.Ordinal))
                .SelectMany(kv => System.Text.Encoding.UTF8.GetString(kv.Value).Split('\n'))
                .Where(l => l.Trim().Length > 0).Select(JObject.Parse).ToList();
            var row = index.Single(o => (string?)o["guid"] == guid && (string?)o["kind"] == "file");
            // format doc §9.3: the counts that decided it ride on its index row (1 GameObject, 1 ParticleSystem)
            Assert.AreEqual(1, (int)row["particleSystems"]!);
            Assert.AreEqual(1, (int)row["gameObjects"]!);
            Assert.AreEqual(0, (int)row["prefabInstances"]!);
            Assert.AreEqual(0, (int)row["rectTransforms"]!);
        }

        [Test]
        public void APrefabWithoutAParticleSystem_AnUnusedEffect_AndAScreenWithOneSparkle_AreNotSent()
        {
            Assert.IsFalse(Sent(Plain), "a used prefab with no ParticleSystem is not an effect");
            Assert.IsFalse(Sent(Unused), "an effect nothing the game uses reaches is not sent");
            Assert.IsFalse(Sent(Panel), "a UI screen with one sparkle is not mostly particles");
        }

        [Test]
        public void AnEffectPastVfxMaxFiles_IsListedInOverCap_NotDropped()
        {
            Assert.IsFalse(Sent(FxB));
            Assert.IsTrue(_result.Header!.OverCap.Any(o => o.Path == FxB && o.Kind == ExportArtKinds.Vfx && o.Reason == ExportArtKinds.ReasonOverVfxCount));
        }

        [Test]
        public void TheWrittenHeader_CarriesArtKinds()
        {
            var header = JObject.Parse(File.ReadAllText(Path.Combine(_tempRoot, "out", "header.json")));
            CollectionAssert.Contains(((JArray)header["artKinds"]!).Select(t => (string)t!).ToArray(), "vfx");
        }
    }
}
