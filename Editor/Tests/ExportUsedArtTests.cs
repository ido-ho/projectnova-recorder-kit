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
    /// Spec §8.1 / §8.2 — art the game loads through Addressables counts as USED, and used art ships
    /// before unused art when the art cap is tight.
    ///
    /// The regression this pins, measured on Rogue Legend (2026-09-23): the cap was spent "in-build
    /// first, then by path", Addressables were not in-build, so 719 of the game's own screens were
    /// left out while art nothing reaches was sent. The fixture reproduces the SHAPE: an unused
    /// texture that sorts FIRST by path (`a_unused`) and an Addressable one that sorts last
    /// (`z_screen`), with an art cap that fits exactly one of them.
    /// </summary>
    public class ExportUsedArtTests
    {
        private const string Root = "Assets/__NovaUsedArtTest";
        private const string UnusedPath = Root + "/Textures/a_unused.png";
        private const string ScreenPath = Root + "/Textures/z_screen.png";
        private const string ScreenMatPath = Root + "/Materials/screen.mat";
        private const string GroupFile = "/AddressableAssetsData/AssetGroups/Screens.asset";

        private string _tempRoot = "";
        private ExportResult _result = new ExportResult();
        private Dictionary<string, byte[]> _entries = new Dictionary<string, byte[]>();
        private string _unusedGuid = "", _screenGuid = "", _matGuid = "";

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            LogAssert.ignoreFailingMessages = true;
            _tempRoot = Path.Combine(Path.GetTempPath(), "novakit-usedart-" + Path.GetRandomFileName());
            Directory.CreateDirectory(Path.Combine(_tempRoot, "project"));

            foreach (var d in new[] { "Textures", "Materials", "AddressableAssetsData/AssetGroups" })
                Directory.CreateDirectory(Path.Combine(Application.dataPath, "..", Root, d));
            WritePng(UnusedPath);
            WritePng(ScreenPath);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            // The Addressable ROOT is a material; the texture is reached only THROUGH it, so the
            // closure must be walked transitively (the recorded graph is one level deep).
            var mat = new Material(Shader.Find("Unlit/Texture")) { mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(ScreenPath) };
            AssetDatabase.CreateAsset(mat, ScreenMatPath);
            AssetDatabase.SaveAssets();
            _unusedGuid = AssetDatabase.AssetPathToGUID(UnusedPath);
            _screenGuid = AssetDatabase.AssetPathToGUID(ScreenPath);
            _matGuid = AssetDatabase.AssetPathToGUID(ScreenMatPath);
            File.WriteAllText(Path.Combine(Application.dataPath, "..", Root + GroupFile),
                "%YAML 1.1\n--- !u!114 &1\nMonoBehaviour:\n  m_SerializeEntries:\n  - m_GUID: " + _matGuid + "\n    m_Address: screen\n");
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            var bytes = new FileInfo(Path.Combine(Application.dataPath, "..", ScreenPath)).Length;
            var unusedBytes = new FileInfo(Path.Combine(Application.dataPath, "..", UnusedPath)).Length;
            _result = new ExportResult();
            var request = new ExportRequest
            {
                ProjectRoot = Path.Combine(_tempRoot, "project"),
                OutDir = Path.Combine(_tempRoot, "out"),
                AssetsRoot = Root,
                PatternScanRoot = Path.Combine(_tempRoot, "project"),
                WorkspaceId = "ws-test",
                KitVersion = "0.9.0-test",
                SliceBudgetMs = 4,
                // room for ONE of the two textures, whichever is considered first
                Caps = new ExportCaps { ThumbnailPx = 16, ArtTotalMaxBytes = Math.Max(bytes, unusedBytes) + 1 },
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

        private Dictionary<string, JObject> Inventory() =>
            _entries.Where(kv => kv.Key.StartsWith(ExportFormat.InventoryBase, StringComparison.Ordinal))
                .SelectMany(kv => System.Text.Encoding.UTF8.GetString(kv.Value).Split('\n'))
                .Where(l => l.Trim().Length > 0)
                .Select(JObject.Parse)
                .ToDictionary(o => o["path"]!.Value<string>()!, o => o);

        [Test]
        public void AnAssetReachedOnlyThroughAnAddressableRoot_IsMarkedAddressable_NotInBuild()
        {
            var inv = Inventory();
            Assert.IsTrue(inv[ScreenMatPath]["addressable"]?.Value<bool>() == true, "the group's own guid");
            Assert.IsTrue(inv[ScreenPath]["addressable"]?.Value<bool>() == true, "reached through the material — transitive");
            Assert.IsFalse(inv[ScreenPath]["inBuild"]!.Value<bool>(), "inBuild keeps its §4 meaning");
            Assert.IsNull(inv[UnusedPath]["addressable"], "absent = false, like `base`");
        }

        [Test]
        public void UnderATightArtCap_TheUsedTextureShips_AndTheUnusedOneIsListedOverCap()
        {
            Assert.IsTrue(_entries.Keys.Any(k => k.StartsWith(ExportFormat.ArtFilesPrefix + _screenGuid, StringComparison.Ordinal)),
                "the Addressable screen is sent although it sorts last by path");
            Assert.IsFalse(_entries.Keys.Any(k => k.StartsWith(ExportFormat.ArtFilesPrefix + _unusedGuid, StringComparison.Ordinal)),
                "the unused texture waits");
            Assert.IsTrue(_result.Header!.OverCap.Any(o => o.Path == UnusedPath), "and it is LISTED, never silently dropped");
        }

        [Test]
        public void AddressableClosure_WalksTheDirectGraphTransitively_AndSurvivesCycles()
        {
            var graph = new Dictionary<string, List<string>>
            {
                ["root"] = new List<string> { "a" },
                ["a"] = new List<string> { "b" },
                ["b"] = new List<string> { "a", "c" },   // a cycle
            };
            CollectionAssert.AreEquivalent(new[] { "root", "a", "b", "c" }, ExportCollector.AddressableClosure(new[] { "root" }, graph));
            CollectionAssert.IsEmpty(ExportCollector.AddressableClosure(Array.Empty<string>(), graph));
        }

        private static void WritePng(string assetPath)
        {
            var tex = new Texture2D(8, 8, TextureFormat.RGBA32, false);
            File.WriteAllBytes(Path.Combine(Application.dataPath, "..", assetPath), tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
        }
    }
}
