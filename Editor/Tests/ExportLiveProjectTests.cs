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
    /// Slice D — WHAT THE EXPORT DOES TO THE PROJECT IT IS READING.
    ///
    /// Everything else about the exporter is "did it write the right bytes". These two are the
    /// opposite question: after the export, is the studio's editor exactly as it was? Both were
    /// found by a fresh-context audit on 2026-09-21 and both were measured, not reasoned about:
    ///
    ///  - K1 — `Resources.UnloadAsset` on a texture the OPEN SCENE is showing turns it grey until
    ///    something re-binds it. The control that named the culprit is in this fixture too: the
    ///    periodic `UnloadUnusedAssetsImmediate` sweep ALONE leaves the same texture red.
    ///  - K6 — `LoadMainAssetAtPath` on a ScriptableObject RUNS THE STUDIO'S CODE
    ///    (`Awake`/`OnEnable`/`OnValidate`, and `OnDisable` on the sweep afterwards). An export is
    ///    a read; it must execute nothing.
    ///
    /// Its own asset root, deliberately: <see cref="ExportCollectorTests"/> exports ONCE in
    /// `OneTimeSetUp` and every one of its assertions reads that one result, so an asset appearing
    /// or vanishing under its root would break it depending on test order.
    /// </summary>
    public class ExportLiveProjectTests
    {
        private const string Root = "Assets/__NovaExportLive";
        private const string TexPath = Root + "/shown.png";
        private const string MatPath = Root + "/shown.mat";
        private const string ColorsPath = Root + "/colors.asset";

        private string _tempRoot = "";

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            LogAssert.ignoreFailingMessages = true;
            _tempRoot = Path.Combine(Path.GetTempPath(), "novakit-live-" + Path.GetRandomFileName());
            Directory.CreateDirectory(Path.Combine(_tempRoot, "project"));
            Directory.CreateDirectory(Path.Combine(_tempRoot, "scan"));
            CreateAssets();
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            DeleteRoot();
            try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true); }
            catch (Exception) { /* a temp dir that will not go is not a failed test */ }
        }

        [SetUp]
        public void SetUp() => LogAssert.ignoreFailingMessages = true;

        // ---- K1 ---------------------------------------------------------------------------

        [Test]
        public void AnExport_LeavesATextureTheOpenSceneIsShowing_ShowingIt()
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(MatPath);
            Assert.IsNotNull(material, "the fixture material");

            GameObject? quad = null;
            GameObject? camGo = null;
            RenderTexture? rt = null;
            try
            {
                quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                quad.transform.position = new Vector3(0, 0, 5);
                quad.transform.localScale = new Vector3(20, 20, 1);
                quad.GetComponent<MeshRenderer>().sharedMaterial = material;

                camGo = new GameObject("__NovaLiveCam");
                var cam = camGo.AddComponent<Camera>();
                cam.orthographic = true;
                cam.orthographicSize = 1;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.blue;
                rt = new RenderTexture(32, 32, 16);
                cam.targetTexture = rt;

                AssertRed(Shoot(cam, rt), "before anything: the quad shows the red texture");

                // THE CONTROL that named the culprit (invariant 50): the sweep the collector runs
                // every 256 thumbnails is, on its own, harmless — it only releases assets nothing
                // holds, and this one is held by the material the quad is rendering.
                EditorUtility.UnloadUnusedAssetsImmediate();
                AssertRed(Shoot(cam, rt), "the periodic sweep alone must leave a held texture alone");

                var result = new ExportResult();
                Pump(MakeRequest(Path.Combine(_tempRoot, "out-live")), result);
                Assert.IsNull(result.FatalError, "the export itself must still work");
                Assert.IsTrue(result.Done);

                AssertRed(Shoot(cam, rt),
                    "AFTER the export the studio's open scene must still show its own texture " +
                    "(grey 0.502 = Resources.UnloadAsset pulled the pixels out from under it)");
                // …and it stays right on the next frame, not only on the one we just drew.
                AssertRed(Shoot(cam, rt), "…and on the next render");
            }
            finally
            {
                if (quad != null) UnityEngine.Object.DestroyImmediate(quad);
                if (camGo != null) UnityEngine.Object.DestroyImmediate(camGo);
                if (rt != null) UnityEngine.Object.DestroyImmediate(rt);
            }
        }

        // ---- K6 ---------------------------------------------------------------------------

        [Test]
        public void AnExport_RunsNoneOfTheStudiosScriptableObjectCode_AndStillReadsItsColours()
        {
            // Unload first, THEN zero the counters: creating the fixture asset already ran its
            // Awake/OnEnable, and this sweep runs its OnDisable. What is counted below is what the
            // EXPORT did, and nothing else.
            EditorUtility.UnloadUnusedAssetsImmediate();
            ExportTestColorAsset.ResetCounters();

            var outDir = Path.Combine(_tempRoot, "out-so");
            var result = new ExportResult();
            Pump(MakeRequest(outDir), result);
            Assert.IsNull(result.FatalError);
            Assert.IsTrue(result.Done);

            Assert.AreEqual(0, ExportTestColorAsset.Awakes, "the export ran the studio's Awake()");
            Assert.AreEqual(0, ExportTestColorAsset.Enables, "the export ran the studio's OnEnable()");
            Assert.AreEqual(0, ExportTestColorAsset.Validates, "the export ran the studio's OnValidate()");
            Assert.AreEqual(0, ExportTestColorAsset.Disables, "the export ran the studio's OnDisable()");

            // POSITIVE CONTROL: not running their code is only a fix if the facts still arrive.
            var entries = ExportArchiveWriterTests.ReadAllEntries(outDir);
            var identity = JObject.Parse(System.Text.Encoding.UTF8.GetString(entries[ExportFormat.IdentityEntry]));
            JObject? row = null;
            foreach (JObject o in (JArray)identity["colorObjects"]!)
                if (o["path"]!.Value<string>() == ColorsPath) row = o;
            Assert.IsNotNull(row, "the ScriptableObject's colours are still reported:\n" + identity);

            var hexByName = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JObject c in (JArray)row!["colors"]!)
                hexByName[c["name"]!.Value<string>()!] = c["hex"]!.Value<string>()!;
            Assert.AreEqual("#FF0000FF", hexByName["primary"], "Color.red, as SetUp assigned it");
            Assert.AreEqual("#1A334DFF", hexByName["secondary"], "new Color(0.1f, 0.2f, 0.3f, 1f)");
            Assert.AreEqual("ExportTestColorAsset", row["type"]!.Value<string>());
        }

        // ---- fixture -----------------------------------------------------------------------

        private ExportRequest MakeRequest(string outDir) => new ExportRequest
        {
            ProjectRoot = Path.Combine(_tempRoot, "project"),
            OutDir = outDir,
            AssetsRoot = Root,
            PatternScanRoot = Path.Combine(_tempRoot, "scan"),
            WorkspaceId = "ws-live",
            KitVersion = "0.6.0-test",
            SliceBudgetMs = 4,
        };

        private static void Pump(ExportRequest request, ExportResult result)
        {
            var e = ExportCollector.Run(request, result).GetEnumerator();
            var guard = 0;
            while (e.MoveNext())
                if (++guard > 200000) Assert.Fail("the collector never finished");
        }

        private static Color Shoot(Camera cam, RenderTexture rt)
        {
            cam.Render();
            var previous = RenderTexture.active;
            RenderTexture.active = rt;
            var readback = new Texture2D(32, 32, TextureFormat.RGBA32, false);
            readback.ReadPixels(new Rect(0, 0, 32, 32), 0, 0);
            readback.Apply();
            RenderTexture.active = previous;
            var c = readback.GetPixel(16, 16);
            UnityEngine.Object.DestroyImmediate(readback);
            return c;
        }

        private static void AssertRed(Color c, string what)
        {
            Assert.Greater(c.r, 0.9f, what + " — red channel, got " + c);
            Assert.Less(c.g, 0.1f, what + " — green channel, got " + c);
            Assert.Less(c.b, 0.1f, what + " — blue channel, got " + c);
        }

        private void CreateAssets()
        {
            DeleteRoot();
            AssetDatabase.CreateFolder("Assets", "__NovaExportLive");

            var tex = new Texture2D(16, 16, TextureFormat.RGBA32, false);
            var pixels = new Color32[16 * 16];
            for (var i = 0; i < pixels.Length; i++) pixels[i] = new Color32(255, 0, 0, 255);
            tex.SetPixels32(pixels);
            tex.Apply(false, false);
            File.WriteAllBytes(AbsProject(TexPath), tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(TexPath, ImportAssetOptions.ForceSynchronousImport);

            var shader = Shader.Find("Unlit/Texture");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Standard");
            Assert.IsNotNull(shader, "a builtin shader for the fixture material");
            var material = new Material(shader);
            material.mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(TexPath);
            AssetDatabase.CreateAsset(material, MatPath);

            var colors = ScriptableObject.CreateInstance<ExportTestColorAsset>();
            colors.primary = Color.red;
            colors.secondary = new Color(0.1f, 0.2f, 0.3f, 1f);
            AssetDatabase.CreateAsset(colors, ColorsPath);
            AssetDatabase.SaveAssets();
        }

        private static void DeleteRoot()
        {
            AssetDatabase.DeleteAsset(Root);
            var abs = AbsProject(Root);
            if (Directory.Exists(abs)) Directory.Delete(abs, recursive: true);
            if (File.Exists(abs + ".meta")) File.Delete(abs + ".meta");
        }

        private static string AbsProject(string assetPath) =>
            Path.Combine(Path.GetDirectoryName(Application.dataPath)!,
                assetPath.Replace('/', Path.DirectorySeparatorChar));
    }
}
