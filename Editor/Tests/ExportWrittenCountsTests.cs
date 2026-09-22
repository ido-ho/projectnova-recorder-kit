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
    /// Slice D — THE TOTALS ARE WHAT WAS WRITTEN, AND A CAP IS NEVER SILENT.
    ///
    /// Spec §2a: `artFiles`, `thumbnails`, `docs` and `patternHits` are the WRITTEN counts, because
    /// §7 fails the whole export on any count difference. Two ways that promise was broken, both
    /// found by the fresh-context audit of 2026-09-21:
    ///
    ///  - K4 — the counts (and `art/index`) were the PLANNED ones. A file that vanished between the
    ///    plan and the copy became an `errors` row while still being counted, so the server failed
    ///    the entire export over one unreadable PNG.
    ///  - K9 — `patternHitsMax` landing inside a file stopped the scan with `errors` empty: the
    ///    export simply described a game with fewer cheat hooks than it has.
    /// </summary>
    public class ExportWrittenCountsTests
    {
        private const string Root = "Assets/__NovaExportWrite";
        private const string KeptPath = Root + "/kept.png";
        private const string DoomedPath = Root + "/doomed.png";
        private const string ThirdPath = Root + "/third.png";
        /// <summary>The one that GROWS between the plan and the copy (M6).</summary>
        private const string GrowPath = Root + "/grow.png";

        private string _tempRoot = "";

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            LogAssert.ignoreFailingMessages = true;
            _tempRoot = Path.Combine(Path.GetTempPath(), "novakit-written-" + Path.GetRandomFileName());
            Directory.CreateDirectory(Path.Combine(_tempRoot, "project"));
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

        // ---- K4 ---------------------------------------------------------------------------

        [Test]
        public void AnArtFileThatVanishesBetweenThePlanAndTheCopy_IsInNoCountAndNoIndexRow()
        {
            var scan = Path.Combine(_tempRoot, "scan-k4");
            Directory.CreateDirectory(scan);
            var outDir = Path.Combine(_tempRoot, "out-k4");
            var doomedAbs = AbsProject(DoomedPath);
            var doomedBytes = File.ReadAllBytes(doomedAbs);

            var result = new ExportResult();
            var removed = false;
            try
            {
                // Exactly the auditor's repro: pull one planned original out from under the writer
                // the moment the write phase begins. (A locked file on a studio's machine — an
                // antivirus scan, a Photoshop save in flight — is the same shape.)
                Pump(MakeRequest(outDir, scan, new ExportCaps()), result, r =>
                {
                    if (removed || r.Phase != "write") return;
                    File.Delete(doomedAbs);
                    removed = true;
                });
            }
            finally
            {
                if (removed) File.WriteAllBytes(doomedAbs, doomedBytes);
            }

            Assert.IsTrue(removed, "the fixture never reached the write phase — this test proves nothing");
            Assert.IsNull(result.FatalError, "one unreadable file is a fact, not a fatality");
            Assert.IsTrue(result.Done);
            var header = result.Header!;
            Assert.IsNotNull(header);

            var entries = ExportArchiveWriterTests.ReadAllEntries(outDir);
            var artFileEntries = 0;
            foreach (var kv in entries)
                if (kv.Key.StartsWith(ExportFormat.ArtFilesPrefix, StringComparison.Ordinal)) artFileEntries++;

            var indexFileRows = 0;
            foreach (var row in Rows(entries, ExportFormat.ArtIndexBase))
            {
                var entry = row["entry"]!.Value<string>()!;
                if (row["kind"]!.Value<string>() != "file") continue;
                indexFileRows++;
                Assert.IsTrue(entries.ContainsKey(entry),
                    "art/index names " + entry + ", which is in no part — a style reference could never resolve");
            }

            Assert.AreEqual(artFileEntries, header.Totals.ArtFiles,
                "totals.artFiles must be what was WRITTEN (spec §2a), not what was planned");
            Assert.AreEqual(artFileEntries, indexFileRows,
                "art/index must hold a row for each art file and no more");

            // …and it is NEVER SILENT about the one that went.
            var named = false;
            foreach (var e in header.Errors)
                if (e.IndexOf("doomed.png", StringComparison.Ordinal) >= 0) named = true;
            Assert.IsTrue(named, "the file that vanished is named in errors:\n  " +
                                 string.Join("\n  ", header.Errors.ToArray()));

            // POSITIVE CONTROL: the files that did NOT vanish shipped, byte for byte.
            var keptEntry = ExportFormat.ArtFileEntry(AssetDatabase.AssetPathToGUID(KeptPath), ".png");
            Assert.IsTrue(entries.ContainsKey(keptEntry), keptEntry);
            CollectionAssert.AreEqual(File.ReadAllBytes(AbsProject(KeptPath)), entries[keptEntry]);
            Assert.Greater(artFileEntries, 0, "an export with no art at all would pass this vacuously");
        }

        // ---- M6: the size is re-checked AT WRITE TIME --------------------------------------

        [Test]
        public void AnArtFileThatGREWSinceItWasPlanned_IsNotWritten_AndIsListedInOverCap()
        {
            // The plan stats every candidate and drops anything over `artFileMaxBytes`; the WRITE
            // then re-read the file with no second look. A file that grew in between (a Photoshop
            // save in flight, a texture re-exported at 4K) went into a part at its NEW size — which
            // can push the part past the 32 MiB ceiling the server refuses, failing the whole
            // export — while `art/index` reported the PLANNED byte count (audit round 3, M6).
            var scan = Path.Combine(_tempRoot, "scan-m6");
            Directory.CreateDirectory(scan);
            var outDir = Path.Combine(_tempRoot, "out-m6");
            var growAbs = AbsProject(GrowPath);
            var small = File.ReadAllBytes(growAbs);
            Assert.Less(small.Length, 4096, "the fixture is under the cap when it is PLANNED");

            var result = new ExportResult();
            var grew = false;
            try
            {
                Pump(MakeRequest(outDir, scan, new ExportCaps { ArtFileMaxBytes = 4096 }), result, r =>
                {
                    if (grew || r.Phase != "write") return;
                    File.WriteAllBytes(growAbs, new byte[40960]);
                    grew = true;
                });
            }
            finally
            {
                if (grew) File.WriteAllBytes(growAbs, small);
            }

            Assert.IsTrue(grew, "the fixture never reached the write phase — this test proves nothing");
            Assert.IsNull(result.FatalError, "one over-cap file is a fact, not a fatality");
            var header = result.Header!;
            var entries = ExportArchiveWriterTests.ReadAllEntries(outDir);
            var growEntry = ExportFormat.ArtFileEntry(AssetDatabase.AssetPathToGUID(GrowPath), ".png");

            Assert.IsFalse(entries.ContainsKey(growEntry),
                "a file that outgrew artFileMaxBytes between the plan and the copy must not be written");

            ExportOverCap? over = null;
            foreach (var o in header.OverCap)
                if (o.Path == GrowPath) over = o;
            Assert.IsNotNull(over, "and it is NEVER silent about it:\n  " +
                                   string.Join("\n  ", header.Errors.ToArray()));
            Assert.AreEqual(40960L, over!.Bytes, "the size that was refused is the size at WRITE time");
            Assert.AreEqual(ExportFormat.ReasonOverArtFile, over.Reason);

            // …and every index row's `bytes` is the size of the entry that is really in the part.
            var fileRows = 0;
            foreach (var row in Rows(entries, ExportFormat.ArtIndexBase))
            {
                if (row["kind"]!.Value<string>() != "file") continue;
                fileRows++;
                var entry = row["entry"]!.Value<string>()!;
                Assert.IsTrue(entries.ContainsKey(entry), entry + " is in art/index and in no part");
                Assert.AreEqual(entries[entry].LongLength, row["bytes"]!.Value<long>(),
                    "art/index `bytes` must be the WRITTEN size, not the planned one (" + entry + ")");
            }
            Assert.AreEqual(fileRows, header.Totals.ArtFiles);
            Assert.Greater(fileRows, 0, "an export with no art at all would pass this vacuously");
        }

        // ---- N4: the thumbnail sweep is bounded in BYTES as well as in count ---------------

        [Test]
        public void TheThumbnailSweep_AlsoFiresOnAByteBudget_NotOnlyOnACountOf256()
        {
            // "the peak is bounded by THIS NUMBER" was true of 16x16 fixtures at ~0.15 MB each. On
            // a shipped game 256 x 2048² RGBA between sweeps is several GB, so the count alone is
            // not a bound on memory at all (audit round 3, N4).
            var scan = Path.Combine(_tempRoot, "scan-n4");
            Directory.CreateDirectory(scan);
            var result = new ExportResult();
            Pump(MakeRequest(Path.Combine(_tempRoot, "out-n4"), scan,
                new ExportCaps { ThumbnailSweepBytes = 1 }), result, null);

            Assert.IsNull(result.FatalError);
            var thumbs = result.Header!.Totals.Thumbnails;
            Assert.GreaterOrEqual(thumbs, 3, "the fixture has textures to make thumbnails of");
            Assert.GreaterOrEqual(result.Sweeps, thumbs,
                "with a 1-byte budget every thumbnail is past it — " + result.Sweeps +
                " sweeps for " + thumbs + " thumbnails");

            // POSITIVE CONTROL: the ordinary budget does NOT sweep per thumbnail.
            var quiet = new ExportResult();
            Pump(MakeRequest(Path.Combine(_tempRoot, "out-n4-quiet"), scan, new ExportCaps()), quiet, null);
            Assert.Less(quiet.Sweeps, thumbs, "a 512 MiB budget must not fire on a handful of 8x8 PNGs");
            Assert.Greater(quiet.Sweeps, 0, "…and the end-of-pass sweep still runs");
        }

        // ---- K9 ---------------------------------------------------------------------------

        [Test]
        public void PatternHitsStoppingInsideAFile_SaysSoAndSaysWhatWasNotScanned()
        {
            var scan = Path.Combine(_tempRoot, "scan-k9");
            Directory.CreateDirectory(scan);
            var body = "";
            for (var i = 0; i < 10; i++) body += "Random.InitState(" + i + ");\n";
            File.WriteAllText(Path.Combine(scan, "AFirst.cs"), body);
            File.WriteAllText(Path.Combine(scan, "BSecond.cs"), body);
            File.WriteAllText(Path.Combine(scan, "CThird.cs"), body);

            var result = new ExportResult();
            Pump(MakeRequest(Path.Combine(_tempRoot, "out-k9"), scan,
                new ExportCaps { PatternHitsMax = 3 }), result, null);

            Assert.IsNull(result.FatalError);
            var header = result.Header!;
            Assert.AreEqual(3, header.Totals.PatternHits, "the cap held");
            Assert.AreEqual(3, Rows(ExportArchiveWriterTests.ReadAllEntries(Path.Combine(_tempRoot, "out-k9")),
                ExportFormat.PatternsBase).Count);

            var all = string.Join("\n  ", header.Errors.ToArray());
            StringAssert.Contains("patternHitsMax", all,
                "a cap that truncates inside a file must still say so:\n  " + all);
            StringAssert.Contains("AFirst.cs", all, "…and name the file it stopped in:\n  " + all);
            // THE REAL NUMBER, PARSED. `StringAssert.Contains("2", …)` passed on the "2" inside
            // "patternHitsMax (3)"… or on any other digit anywhere in any message: it could not
            // tell 2 unscanned files from 20 or from none (audit round 3, M10).
            Assert.AreEqual(2, NotScannedCount(all),
                "two of the three .cs files were never opened:\n  " + all);
        }

        /// <summary>The count the truncation notice states, as a number. -1 when no notice says one,
        /// so a missing notice fails loudly instead of matching a digit somewhere else.</summary>
        private static int NotScannedCount(string errors)
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                errors, @"and (\d+) further \.cs file\(s\) were not scanned");
            return m.Success ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : -1;
        }

        [Test]
        public void PatternHitsCappedInsideTheLastFile_AreStillNeverSilent()
        {
            // The auditor's exact probe: ONE .cs file with 10 hits against a cap of 3 reported
            // `patternHits=3, errors=[]`. The old truncation notice was written only when the NEXT
            // iteration of the file loop found the cap already spent — with nothing after the file
            // the cap landed in, there was no next iteration and the export said nothing at all.
            var scan = Path.Combine(_tempRoot, "scan-k9-single");
            Directory.CreateDirectory(scan);
            var body = "";
            for (var i = 0; i < 10; i++) body += "Random.InitState(" + i + ");\n";
            File.WriteAllText(Path.Combine(scan, "Only.cs"), body);

            var result = new ExportResult();
            Pump(MakeRequest(Path.Combine(_tempRoot, "out-k9-single"), scan,
                new ExportCaps { PatternHitsMax = 3 }), result, null);

            var header = result.Header!;
            Assert.AreEqual(3, header.Totals.PatternHits);
            var all = string.Join("\n  ", header.Errors.ToArray());
            StringAssert.Contains("patternHitsMax", all,
                "the only file was cut short and the export said nothing:\n  " + all);
            StringAssert.Contains("Only.cs", all, all);

            // POSITIVE CONTROL: the same file under a cap it fits says nothing about a cap.
            var wide = new ExportResult();
            Pump(MakeRequest(Path.Combine(_tempRoot, "out-k9-wide"), scan, new ExportCaps()), wide, null);
            Assert.AreEqual(10, wide.Header!.Totals.PatternHits);
            foreach (var e in wide.Header.Errors)
                Assert.IsFalse(e.IndexOf("patternHitsMax", StringComparison.Ordinal) >= 0,
                    "an uncapped scan must not claim a cap fired: " + e);
        }

        // ---- fixture -----------------------------------------------------------------------

        private ExportRequest MakeRequest(string outDir, string scanRoot, ExportCaps caps) => new ExportRequest
        {
            ProjectRoot = Path.Combine(_tempRoot, "project"),
            OutDir = outDir,
            AssetsRoot = Root,
            PatternScanRoot = scanRoot,
            WorkspaceId = "ws-written",
            KitVersion = "0.6.0-test",
            SliceBudgetMs = 0,
            Caps = caps,
        };

        private static void Pump(ExportRequest request, ExportResult result, Action<ExportResult>? onSlice)
        {
            var e = ExportCollector.Run(request, result).GetEnumerator();
            var guard = 0;
            while (e.MoveNext())
            {
                onSlice?.Invoke(result);
                if (++guard > 200000) Assert.Fail("the collector never finished");
            }
        }

        private static List<JObject> Rows(Dictionary<string, byte[]> entries, string baseName)
        {
            var list = new List<JObject>();
            for (var i = 0; ; i++)
            {
                var name = ExportFormat.ShardName(baseName, i);
                if (!entries.ContainsKey(name)) break;
                foreach (var line in ExportArchiveWriterTests.Lines(entries[name]))
                    list.Add(JObject.Parse(line));
            }
            return list;
        }

        private static void CreateAssets()
        {
            DeleteRoot();
            AssetDatabase.CreateFolder("Assets", "__NovaExportWrite");
            foreach (var p in new[] { KeptPath, DoomedPath, ThirdPath, GrowPath }) WritePng(p);
        }

        private static void WritePng(string assetPath)
        {
            var tex = new Texture2D(8, 8, TextureFormat.RGBA32, false);
            var pixels = new Color32[64];
            for (var i = 0; i < pixels.Length; i++) pixels[i] = new Color32(20, 120, 220, 255);
            tex.SetPixels32(pixels);
            tex.Apply(false, false);
            File.WriteAllBytes(AbsProject(assetPath), tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
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
