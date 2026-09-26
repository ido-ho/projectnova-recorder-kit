using System;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// Slice D — WHAT THE IDENTITY PHASE COSTS THE EDITOR IN MEMORY (audit round 3, N3).
    ///
    /// The K1 repair removed the per-object `Resources.UnloadAsset` — rightly, it turned a texture
    /// the open scene was showing grey — and left the periodic sweep as the only thing that gives
    /// memory back. But the sweep only ever ran in the THUMBNAIL pass, and the identity phase loads
    /// every `AudioClip` in the project to read its length, channels and sample rate. On a game
    /// whose clips are Preload/Decompress-on-load that is the whole audio library resident in the
    /// studio's editor, for the length of the export and after it.
    ///
    /// Its own asset root, holding audio and NOTHING ELSE: with a texture anywhere in it the
    /// thumbnail pass's end-of-pass sweep would make `Sweeps` non-zero and this would pass whatever
    /// the identity phase did.
    /// </summary>
    public class ExportAudioSweepTests
    {
        private const string Root = "Assets/__NovaExportAudio";
        private const int ClipHz = 22050;
        private const int ClipSamples = ClipHz / 4;

        private static readonly string[] Clips =
        {
            Root + "/one.wav", Root + "/two.wav", Root + "/three.wav",
        };

        private string _tempRoot = "";

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            LogAssert.ignoreFailingMessages = true;
            _tempRoot = Path.Combine(Path.GetTempPath(), "novakit-audio-" + Path.GetRandomFileName());
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

        [Test]
        public void TheIdentityPhase_ReleasesTheAudioItLoaded_NotOnlyTheThumbnailPass()
        {
            var result = new ExportResult();
            Pump(Path.Combine(_tempRoot, "out-audio"), result);

            Assert.IsNull(result.FatalError);
            Assert.IsTrue(result.Done);

            // POSITIVE CONTROL FIRST: it really did read every clip, so "no sweeps" cannot mean
            // "there was nothing to sweep".
            var entries = ExportArchiveWriterTests.ReadAllEntries(Path.Combine(_tempRoot, "out-audio"));
            var identity = Newtonsoft.Json.Linq.JObject.Parse(
                System.Text.Encoding.UTF8.GetString(entries[ExportFormat.IdentityEntry]));
            Assert.AreEqual(Clips.Length, ((Newtonsoft.Json.Linq.JArray)identity["audio"]!).Count,
                "every clip's facts were read, which means every clip was LOADED");
            Assert.AreEqual(0, result.Header!.Totals.Thumbnails, "no textures here — no thumbnail sweep to borrow");

            Assert.Greater(result.Sweeps, 0,
                "the identity phase loaded " + Clips.Length + " AudioClips and released none of them");
        }

        [Test]
        public void ThatSweep_LeavesAClipAnAudioSourceInTheOpenSceneIsUsing_Playable()
        {
            // The same control that separated the sweep from the per-object unload for textures
            // (`ExportLiveProjectTests`), for audio: `UnloadUnusedAssetsImmediate` releases only
            // what nothing holds, so a clip a live `AudioSource` references must come through the
            // export byte for byte. Without this, adding sweeps to the identity phase would be the
            // K1 defect again in a different asset type.
            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(Clips[0]);
            Assert.IsNotNull(clip, "the fixture clip");

            GameObject? go = null;
            try
            {
                go = new GameObject("__NovaAudioHolder");
                var src = go.AddComponent<AudioSource>();
                src.clip = clip;

                AssertPlayable(src.clip!, "before anything: the fixture clip has audio in it");
                EditorUtility.UnloadUnusedAssetsImmediate();
                AssertPlayable(src.clip!, "the sweep alone must leave a clip an AudioSource holds alone");

                var result = new ExportResult();
                Pump(Path.Combine(_tempRoot, "out-held"), result);
                Assert.IsNull(result.FatalError);
                Assert.Greater(result.Sweeps, 0, "…and the export really did sweep");

                Assert.IsNotNull(src.clip, "AFTER the export the AudioSource still has its clip");
                AssertPlayable(src.clip!, "AFTER the export the clip still holds its samples");
            }
            finally
            {
                if (go != null) UnityEngine.Object.DestroyImmediate(go);
            }
        }

        // ---- fixture -----------------------------------------------------------------------

        private static void AssertPlayable(AudioClip clip, string what)
        {
            Assert.AreEqual(ClipSamples, clip.samples, what + " — sample count");
            if (clip.loadState != AudioDataLoadState.Loaded) clip.LoadAudioData();
            var data = new float[Math.Min(clip.samples, 4096) * clip.channels];
            Assert.IsTrue(clip.GetData(data, 0), what + " — GetData");
            var peak = 0f;
            foreach (var v in data) peak = Mathf.Max(peak, Mathf.Abs(v));
            Assert.Greater(peak, 0.01f, what + " — the samples read back silent (peak " + peak + ")");
        }

        private void Pump(string outDir, ExportResult result)
        {
            var e = ExportCollector.Run(new ExportRequest
            {
                ProjectRoot = Path.Combine(_tempRoot, "project"),
                OutDir = outDir,
                AssetsRoot = Root,
                PatternScanRoot = Path.Combine(_tempRoot, "scan"),
                WorkspaceId = "ws-audio",
                KitVersion = "0.6.0-test",
                SliceBudgetMs = 0,
            }, result).GetEnumerator();
            var guard = 0;
            while (e.MoveNext())
                if (++guard > 200000) Assert.Fail("the collector never finished");
        }

        private static void CreateAssets()
        {
            DeleteRoot();
            AssetDatabase.CreateFolder("Assets", "__NovaExportAudio");
            foreach (var c in Clips) WriteWav(c);
        }

        private static void WriteWav(string assetPath)
        {
            const int bits = 16;
            const int channels = 1;
            var dataBytes = ClipSamples * channels * (bits / 8);
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(new[] { 'R', 'I', 'F', 'F' });
                w.Write(36 + dataBytes);
                w.Write(new[] { 'W', 'A', 'V', 'E' });
                w.Write(new[] { 'f', 'm', 't', ' ' });
                w.Write(16);
                w.Write((short)1);                                    // PCM
                w.Write((short)channels);
                w.Write(ClipHz);
                w.Write(ClipHz * channels * (bits / 8));              // byte rate
                w.Write((short)(channels * (bits / 8)));              // block align
                w.Write((short)bits);
                w.Write(new[] { 'd', 'a', 't', 'a' });
                w.Write(dataBytes);
                for (var i = 0; i < ClipSamples; i++)
                    w.Write((short)(Mathf.Sin(i * 0.05f) * 12000f));
                w.Flush();
                File.WriteAllBytes(AbsProject(assetPath), ms.ToArray());
            }
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
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
