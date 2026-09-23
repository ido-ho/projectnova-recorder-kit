using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// Slice D — THE TMP FONT PATH, EXERCISED.
    ///
    /// The spec used to say "the kit's test host has no TextMeshPro, so this path compiles and has
    /// never executed". That was FALSE: `com.unity.ugui` 2.x ships TextMesh Pro, and
    /// `Unity.TextMeshPro.dll` is in the test host's ScriptAssemblies (fresh-context audit,
    /// 2026-09-21). So it is executed here, on a REAL `TMP_FontAsset`.
    ///
    /// The type is reached by NAME through reflection and its family name is set through
    /// `SerializedObject` — there is no `using TMPro` in this file and there is none in the kit,
    /// because the kit must compile in a project without TextMesh Pro (`UguiDriver.cs:46` records
    /// the day that broke a scratch install). Referencing the type in the TESTS would defeat the
    /// point of the rule it is testing.
    /// </summary>
    public class ExportTmpFontTests
    {
        private const string Root = "Assets/__NovaExportTmp";
        private const string FontPath = Root + "/probe SDF.asset";
        private const string BigRoot = "Assets/__NovaExportTmpBig";
        private const string BigFontPath = BigRoot + "/big SDF.asset";
        private const string TailRoot = "Assets/__NovaExportTmpTail";
        private const string TailFontPath = TailRoot + "/tail SDF.asset";
        private const string Family = "NovaProbeFamily";

        /// <summary>Past `ExportCaps.AssetTextMaxBytes` (8 MiB). Two real TMP fonts with a 2048²
        /// atlas measured 8,446,940 and 8,505,637 bytes — this is that file, in miniature only in
        /// that its hex is repetitive.</summary>
        private const long BigBytes = 8L * 1024 * 1024 + 256 * 1024;

        private string _tempRoot = "";
        private readonly List<string> _created = new List<string>();

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            LogAssert.ignoreFailingMessages = true;
            _tempRoot = Path.Combine(Path.GetTempPath(), "novakit-tmp-" + Path.GetRandomFileName());
            Directory.CreateDirectory(Path.Combine(_tempRoot, "project"));
            Directory.CreateDirectory(Path.Combine(_tempRoot, "scan"));
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            DeleteRoot(Root);
            DeleteRoot(BigRoot);
            DeleteRoot(TailRoot);
            try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true); }
            catch (Exception) { /* a temp dir that will not go is not a failed test */ }
        }

        [SetUp]
        public void SetUp() => LogAssert.ignoreFailingMessages = true;

        [Test]
        public void ARealTmpFontAsset_ReportsItsFamilyName_WithoutTheKitEverLoadingIt()
        {
            // TMP's own `OnValidate` throws on a font asset with no material — which is itself the
            // K6 finding in miniature: loading or touching a ScriptableObject RUNS ITS CODE.
            LogAssert.ignoreFailingMessages = true;

            var type = TmpFontAssetType();
            if (type == null)
                Assert.Ignore("no TMPro.TMP_FontAsset type is loaded in this editor — nothing to exercise");

            CreateFontAsset(type!, Root, FontPath, padTo: 0, atlasFirst: false);

            var outDir = Path.Combine(_tempRoot, "out");
            var result = new ExportResult();
            Pump(Root, outDir, result);

            Assert.IsNull(result.FatalError);
            Assert.IsTrue(result.Done);

            var entries = ExportArchiveWriterTests.ReadAllEntries(outDir);
            var identity = JObject.Parse(System.Text.Encoding.UTF8.GetString(entries[ExportFormat.IdentityEntry]));
            JObject? row = null;
            foreach (JObject f in (JArray)identity["fonts"]!)
                if (f["path"]!.Value<string>() == FontPath) row = f;
            Assert.IsNotNull(row, "the TMP font asset is in identity.fonts:\n" + identity["fonts"]);

            Assert.AreEqual("TMP_FontAsset", row!["type"]!.Value<string>(),
                "found by type NAME, with no compile-time reference to TMPro");
            Assert.AreEqual(Family, row["familyName"]!.Value<string>(),
                "m_FaceInfo.m_FamilyName, read off the asset's YAML rather than by loading it");
            Assert.AreEqual(AssetDatabase.AssetPathToGUID(FontPath), row["guid"]!.Value<string>());

            // Its ORIGINAL ships too, so the hosted side can re-read anything this missed.
            var artEntry = ExportFormat.ArtFileEntry(AssetDatabase.AssetPathToGUID(FontPath), ".asset");
            Assert.IsTrue(entries.ContainsKey(artEntry), artEntry);
        }

        // ---- N1 / S1: the font a studio actually ships ---------------------------------------

        /// <summary>
        /// THE PRODUCTION SHAPE, MEASURED (audit round 4, S1). Unity writes a file's documents in
        /// ascending SIGNED fileID order, and a TMP font's atlas `Texture2D` very often has a
        /// NEGATIVE one — so the multi-megabyte `_typelessdata:` line comes BEFORE the
        /// `&amp;11400000` MonoBehaviour and `m_FaceInfo` sits after all of it.
        ///
        /// The round-3 repair read only the first 256 KiB of a font, on the premise that
        /// "`m_FamilyName` is on about line 25 whatever the atlas weighs". Measured over the two
        /// real games on this machine — 61 `.asset` files holding an `m_FaceInfo:`, of which 48
        /// carry a non-empty family name — that premise held for 26 of them and the budget ran out
        /// on 22. This fixture is the shape that broke it; the old one padded the file BELOW the
        /// face info, which is the author's assumption encoded as a gate (invariants 40, 101).
        /// </summary>
        [Test]
        public void AFontWhoseATLASComesFirst_STILLReportsItsFamilyName()
        {
            LogAssert.ignoreFailingMessages = true;
            var type = TmpFontAssetType();
            if (type == null)
                Assert.Ignore("no TMPro.TMP_FontAsset type is loaded in this editor — nothing to exercise");

            var small = CreateFontAsset(type!, BigRoot, BigFontPath, padTo: BigBytes, atlasFirst: true);
            try
            {
                var onDisk = new FileInfo(AbsProject(BigFontPath)).Length;
                Assert.Greater(onDisk, new ExportCaps().AssetTextMaxBytes,
                    "the fixture must really be past the cap (" + onDisk + " bytes)");
                Assert.Greater(FaceInfoByte(BigFontPath), 8L * 1024 * 1024,
                    "…and m_FaceInfo must really sit past 8 MiB of atlas, which is the whole point");

                var outDir = Path.Combine(_tempRoot, "out-big");
                var result = new ExportResult();
                Pump(BigRoot, outDir, result);
                Assert.IsNull(result.FatalError);

                Assert.AreEqual(Family, FamilyOf(outDir, BigFontPath),
                    "the atlas is above the face info, so where the family name sits in the FILE " +
                    "is not what decides whether it is read");

                // The 8 MiB cap still holds for the COLOUR reader, which really does need the whole
                // document — and it says so per asset rather than reporting an empty palette.
                // EXACTLY ONE errors row may name this font: a read that answered has nothing to
                // report, and the budget row it used to write is gone with the budget.
                var mine = new List<string>();
                foreach (var e in result.Header!.Errors) if (e.Contains("big SDF.asset")) mine.Add(e);
                Assert.AreEqual(1, mine.Count,
                    "one row for the colour cap and nothing else:\n  " + string.Join("\n  ", mine.ToArray()));
                StringAssert.Contains("colour facts not read", mine[0], mine[0]);

                // …and the original is over artFileMaxBytes, so it is listed, never quietly dropped.
                ExportOverCap? over = null;
                foreach (var o in result.Header.OverCap)
                    if (o.Path == BigFontPath) over = o;
                Assert.IsNotNull(over, "an 8 MB original is over artFileMaxBytes and is listed");
                Assert.AreEqual(ExportFormat.ReasonOverArtFile, over!.Reason);
            }
            finally
            {
                // Put the small file back, so nothing can ever re-import 8 MB of made-up atlas.
                File.WriteAllText(AbsProject(BigFontPath), small);
            }
        }

        [Test]
        public void AFontWhoseATLASComesLast_ReportsItsFamilyNameWithoutReadingTheAtlas()
        {
            // THE OTHER ORDER, kept as the positive control: a positive fileID puts the atlas
            // BELOW the face info, and there the read must stop at the match rather than walking
            // the hex. Both orders are real; only one of them was ever tested.
            LogAssert.ignoreFailingMessages = true;
            var type = TmpFontAssetType();
            if (type == null)
                Assert.Ignore("no TMPro.TMP_FontAsset type is loaded in this editor — nothing to exercise");

            var small = CreateFontAsset(type!, TailRoot, TailFontPath, padTo: BigBytes, atlasFirst: false);
            try
            {
                Assert.Less(FaceInfoByte(TailFontPath), 64L * 1024,
                    "in this order the face info really is near the front");

                var outDir = Path.Combine(_tempRoot, "out-tail");
                var result = new ExportResult();
                Pump(TailRoot, outDir, result);
                Assert.IsNull(result.FatalError);
                Assert.AreEqual(Family, FamilyOf(outDir, TailFontPath));
            }
            finally
            {
                File.WriteAllText(AbsProject(TailFontPath), small);
            }
        }

        /// <summary>The `familyName` this export reported for one font path.</summary>
        private static string? FamilyOf(string outDir, string fontPath)
        {
            var entries = ExportArchiveWriterTests.ReadAllEntries(outDir);
            var identity = JObject.Parse(System.Text.Encoding.UTF8.GetString(entries[ExportFormat.IdentityEntry]));
            foreach (JObject f in (JArray)identity["fonts"]!)
                if (f["path"]!.Value<string>() == fontPath)
                    return f["familyName"]!.Type == JTokenType.Null ? null : f["familyName"]!.Value<string>();
            Assert.Fail("the TMP font asset is not in identity.fonts at all:\n" + identity["fonts"]);
            return null;
        }

        /// <summary>Where `m_FaceInfo:` really starts, in bytes — so the fixture's own premise is
        /// measured rather than assumed.</summary>
        private static long FaceInfoByte(string fontPath)
        {
            var needle = System.Text.Encoding.UTF8.GetBytes("m_FaceInfo:");
            using (var fs = File.OpenRead(AbsProject(fontPath)))
            {
                var buf = new byte[1 << 16];
                var at = 0L;
                var carry = new byte[needle.Length - 1];
                var carried = 0;
                while (true)
                {
                    var n = fs.Read(buf, 0, buf.Length);
                    if (n <= 0) return -1;
                    var window = new byte[carried + n];
                    Array.Copy(carry, 0, window, 0, carried);
                    Array.Copy(buf, 0, window, carried, n);
                    for (var i = 0; i + needle.Length <= window.Length; i++)
                    {
                        var hit = true;
                        for (var j = 0; j < needle.Length; j++) if (window[i + j] != needle[j]) { hit = false; break; }
                        if (hit) return at - carried + i;
                    }
                    at += n;
                    carried = Math.Min(needle.Length - 1, window.Length);
                    Array.Copy(window, window.Length - carried, carry, 0, carried);
                }
            }
        }

        // ---- fixture -----------------------------------------------------------------------

        private void Pump(string assetsRoot, string outDir, ExportResult result)
        {
            var e = ExportCollector.Run(new ExportRequest
            {
                ProjectRoot = Path.Combine(_tempRoot, "project"),
                OutDir = outDir,
                AssetsRoot = assetsRoot,
                PatternScanRoot = Path.Combine(_tempRoot, "scan"),
                WorkspaceId = "ws-tmp",
                KitVersion = "0.6.0-test",
                SliceBudgetMs = 4,
            }, result).GetEnumerator();
            var guard = 0;
            while (e.MoveNext())
                if (++guard > 200000) Assert.Fail("the collector never finished");
        }

        private static string AbsProject(string assetPath) =>
            Path.Combine(Path.GetDirectoryName(Application.dataPath)!,
                assetPath.Replace('/', Path.DirectorySeparatorChar));

        private static Type? TmpFontAssetType()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type? t = null;
                try { t = asm.GetType("TMPro.TMP_FontAsset", false); }
                catch (Exception) { /* a reflection-only or broken assembly */ }
                if (t != null) return t;
            }
            return null;
        }

        /// <summary>Builds the asset and returns the SMALL yaml it had before any atlas was added,
        /// so the test can put it back.</summary>
        private string CreateFontAsset(Type type, string root, string fontPath, long padTo, bool atlasFirst)
        {
            DeleteRoot(root);
            AssetDatabase.CreateFolder("Assets", root.Substring("Assets/".Length));
            _created.Add(root);

            var asset = ScriptableObject.CreateInstance(type);
            Assert.IsNotNull(asset, "TMPro.TMP_FontAsset would not instantiate");
            AssetDatabase.CreateAsset(asset, fontPath);
            AssetDatabase.SaveAssets();

            // The family name is set in the SERIALISED FILE rather than through `SerializedObject`,
            // because applying a property runs `TMP_FontAsset.OnValidate`, which throws on an asset
            // with no material. That is the finding this whole lane is about, from the other side:
            // touching a ScriptableObject executes its code. The kit reads this file; so does this
            // fixture.
            var abs = AbsProject(fontPath);
            var yaml = File.ReadAllText(abs);
            Assert.IsTrue(ExportScan.LooksLikeYaml(yaml),
                "this project serialises assets as binary, so there is no YAML for the kit to read");
            var face = yaml.IndexOf("m_FaceInfo:", StringComparison.Ordinal);
            Assert.Greater(face, 0, "a real TMP_FontAsset serialises m_FaceInfo:\n" + Head(yaml));
            var key = yaml.IndexOf("m_FamilyName:", face, StringComparison.Ordinal);
            Assert.Greater(key, 0, "…with an m_FamilyName in it:\n" + Head(yaml));
            var eol = yaml.IndexOf('\n', key);
            if (eol < 0) eol = yaml.Length;
            File.WriteAllText(abs, yaml.Substring(0, key) + "m_FamilyName: " + Family + yaml.Substring(eol));
            AssetDatabase.ImportAsset(fontPath, ImportAssetOptions.ForceSynchronousImport);

            // THE ATLAS, AS A DOCUMENT OF ITS OWN, on the side of the face info the caller asked
            // for. Written after the import so nothing re-reads 8 MB — the kit reads the FILE, and
            // the file is what has to be the real shape.
            var small = File.ReadAllText(abs);
            if (padTo <= 0) return small;
            using (var w = new StreamWriter(abs, append: false))
            {
                if (atlasFirst)
                {
                    var firstDoc = small.IndexOf("--- !u!", StringComparison.Ordinal);
                    Assert.Greater(firstDoc, 0, "a real .asset starts with its %YAML directives:\n" + Head(small));
                    w.Write(small.Substring(0, firstDoc));
                    WriteAtlasDocument(w, padTo, negativeId: true);
                    w.Write(small.Substring(firstDoc));
                }
                else
                {
                    w.Write(small);
                    WriteAtlasDocument(w, padTo, negativeId: false);
                }
            }
            return small;
        }

        /// <summary>
        /// ONE `Texture2D` document holding ONE line of megabytes of hex — which is what a TMP
        /// font's atlas really is. A NEGATIVE fileID is what puts it ABOVE the MonoBehaviour: Unity
        /// orders documents by ascending signed id, and `&amp;11400000` is positive.
        /// </summary>
        private static void WriteAtlasDocument(StreamWriter w, long hexChars, bool negativeId)
        {
            w.Write("--- !u!28 &" + (negativeId ? "-5942297626794167620" : "8400000") + "\n");
            w.Write("Texture2D:\n");
            w.Write("  m_ObjectHideFlags: 0\n");
            w.Write("  m_Name: probe SDF Atlas\n");
            w.Write("  m_Width: 2048\n");
            w.Write("  m_Height: 2048\n");
            w.Write("  m_CompleteImageSize: " + (hexChars / 2) + "\n");
            w.Write("  image data: " + (hexChars / 2) + "\n");
            w.Write("  _typelessdata: ");
            var chunk = new string('7', 64 * 1024);      // never the whole line in memory at once
            for (long n = 0; n < hexChars; n += chunk.Length) w.Write(chunk);
            w.Write("\n");
        }

        private static string Head(string text) =>
            text.Length <= 600 ? text : text.Substring(0, 600) + "…";

        private void DeleteRoot(string root)
        {
            if (!_created.Contains(root) && !AssetDatabase.IsValidFolder(root)) return;
            AssetDatabase.DeleteAsset(root);
            var abs = AbsProject(root);
            if (Directory.Exists(abs)) Directory.Delete(abs, recursive: true);
            if (File.Exists(abs + ".meta")) File.Delete(abs + ".meta");
            _created.Remove(root);
        }
    }
}
