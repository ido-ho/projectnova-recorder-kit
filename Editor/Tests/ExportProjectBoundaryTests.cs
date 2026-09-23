using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// Slice D — THE EDGE OF THE PROJECT, AND THE EDGE OF A SLICE (audit round 3: M1, M2, N2).
    ///
    /// TRUST.md promises two things a studio is entitled to check: nothing outside your project is
    /// read, and the Editor stays usable while the export runs. Three passes broke one or the other:
    ///
    ///  - M1 — the Addressables group read was a raw `Directory.GetFiles` + `File.ReadAllText`: it
    ///    followed a symlinked group file straight out of the project and had no size cap at all.
    ///  - M2 — the art passes take whatever Unity IMPORTED, and Unity follows a symlinked folder
    ///    under `Assets/`. The walker refuses to leave the project; the art pass never asked it.
    ///  - N2 — the docs and config sweeps yielded MATCHES only, so the collector could not take a
    ///    slice during the walk itself, however long it ran.
    /// </summary>
    public class ExportProjectBoundaryTests
    {
        private const string Root = "Assets/__NovaExportBoundary";
        private const string RealTexPath = Root + "/real/tex.png";
        private const string LinkedDirPath = Root + "/linked";
        private const string LinkedTexPath = LinkedDirPath + "/linked.png";
        private const string GroupDirPath = Root + "/AddressableAssetsData/AssetGroups";
        private const string GoodGroupPath = GroupDirPath + "/Default.asset";
        private const string HugeGroupPath = GroupDirPath + "/Huge.asset";
        private const string NestedGroupPath = GroupDirPath + "/Schemas/Nested.asset";
        private const string LinkedGroupPath = GroupDirPath + "/Linked.asset";

        private const string InProjectGuid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string EntryGuid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        private const string HugeGuid = "cccccccccccccccccccccccccccccccc";
        private const string NestedGuid = "dddddddddddddddddddddddddddddddd";
        private const string OutsideGuid = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";

        // ---- audit round 4, M5: the Addressables DATA FOLDER itself reached through a link ----
        private const string LinkedAdrRoot = "Assets/__NovaExportLinkedAdr";
        private const string LinkedAdrDataPath = LinkedAdrRoot + "/AddressableAssetsData";
        private const string LinkedAdrGuid = "ffffffffffffffffffffffffffffffff";

        private string _tempRoot = "";
        private string _outside = "";
        private bool _dirLinked;
        private bool _fileLinked;
        private bool _adrDataLinked;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            LogAssert.ignoreFailingMessages = true;
            // THE SYMLINK TARGETS LIVE HERE, and nowhere near anything that matters: a recursive
            // delete that followed a link would take the target with it.
            _tempRoot = Path.Combine(Path.GetTempPath(), "novakit-bound-" + Path.GetRandomFileName());
            _outside = Path.Combine(_tempRoot, "outside");
            Directory.CreateDirectory(Path.Combine(_tempRoot, "project"));
            Directory.CreateDirectory(Path.Combine(_tempRoot, "scan"));
            Directory.CreateDirectory(_outside);
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

        // ---- M2 -----------------------------------------------------------------------------

        [Test]
        public void ArtReachedThroughASymlinkUnderAssets_IsListedAndNeverREAD()
        {
            if (!_dirLinked) Assert.Ignore("this platform would not create a directory symlink");
            var guid = AssetDatabase.AssetPathToGUID(LinkedTexPath);
            if (string.IsNullOrEmpty(guid))
                Assert.Ignore("this Unity did not import through the symlink — nothing to exercise");

            var outDir = Path.Combine(_tempRoot, "out-m2");
            var result = new ExportResult();
            Pump(outDir, result, new ExportCaps { ThumbnailPx = 16 });
            Assert.IsNull(result.FatalError);

            var entries = ExportArchiveWriterTests.ReadAllEntries(outDir);
            var errors = string.Join("\n  ", result.Header!.Errors.ToArray());

            // POSITIVE CONTROL: Unity really did import it, so it IS in the inventory — the promise
            // is "listed and never read", not "invisible".
            var paths = new List<string>();
            foreach (var row in Rows(entries, ExportFormat.InventoryBase))
                paths.Add(row["path"]!.Value<string>()!);
            CollectionAssert.Contains(paths, LinkedTexPath,
                "the asset Unity imported is still LISTED:\n  " + string.Join("\n  ", paths.ToArray()));

            // …and its BYTES — which live outside the project — are not in the export.
            Assert.IsFalse(entries.ContainsKey(ExportFormat.ArtFileEntry(guid, ".png")),
                "the original of a file OUTSIDE the project was uploaded");
            Assert.IsFalse(entries.ContainsKey(ExportFormat.ArtThumbEntry(guid)),
                "a picture of a file OUTSIDE the project was uploaded");
            StringAssert.Contains("linked", errors, "and the folder that was skipped is named:\n  " + errors);

            // POSITIVE CONTROL: the REAL folder beside it ships, original and thumbnail.
            var realGuid = AssetDatabase.AssetPathToGUID(RealTexPath);
            Assert.IsTrue(entries.ContainsKey(ExportFormat.ArtFileEntry(realGuid, ".png")),
                "a texture genuinely inside the project is still exported");
            Assert.IsTrue(entries.ContainsKey(ExportFormat.ArtThumbEntry(realGuid)));
        }

        // ---- M1 -----------------------------------------------------------------------------

        [Test]
        public void TheAddressablesRead_IsBoundedAndStaysInTheProject()
        {
            var outDir = Path.Combine(_tempRoot, "out-m1");
            var result = new ExportResult();
            // A tiny text cap, so the "huge" group file is a few KB rather than a few MB.
            Pump(outDir, result, new ExportCaps { ThumbnailPx = 16, AssetTextMaxBytes = 1024 });
            Assert.IsNull(result.FatalError);

            var entries = ExportArchiveWriterTests.ReadAllEntries(outDir);
            var build = JObject.Parse(System.Text.Encoding.UTF8.GetString(entries[ExportFormat.BuildEntry]));
            var addressables = (JObject)build["addressables"]!;
            Assert.IsTrue(addressables["present"]!.Value<bool>());

            var guids = new List<string>();
            var files = new List<string>();
            foreach (JObject g in (JArray)addressables["groups"]!)
            {
                files.Add(g["file"]!.Value<string>()!);
                foreach (var t in (JArray)g["guids"]!) guids.Add(t.Value<string>()!);
            }
            var listed = string.Join(", ", files.ToArray()) + " -> " + string.Join(", ", guids.ToArray());
            var errors = string.Join("\n  ", result.Header!.Errors.ToArray());

            // POSITIVE CONTROL: the ordinary group is read, group guid and entry guid both.
            CollectionAssert.Contains(guids, InProjectGuid, listed);
            CollectionAssert.Contains(guids, EntryGuid, listed);

            // A group file past the text cap is not read into memory, and says so.
            CollectionAssert.DoesNotContain(guids, HugeGuid, "an unbounded ReadAllText of a group file: " + listed);
            StringAssert.Contains("Huge.asset", errors, "…and the group it could not read is named:\n  " + errors);

            // Top level only, as it always was: a schema file in a SUBFOLDER is not a group.
            CollectionAssert.DoesNotContain(guids, NestedGuid, listed);

            if (_fileLinked)
                CollectionAssert.DoesNotContain(guids, OutsideGuid,
                    "a symlinked group file was followed OUT of the project: " + listed);
        }

        // ---- M5 (audit round 4) ---------------------------------------------------------------

        [Test]
        public void AddressablesReachedThroughALINKEDDataFolder_IsReportedPresentAndNeverREAD()
        {
            if (!_adrDataLinked) Assert.Ignore("this platform would not create a directory symlink");

            var outDir = Path.Combine(_tempRoot, "out-m5");
            var result = new ExportResult();
            PumpRoot(LinkedAdrRoot, outDir, result, new ExportCaps { ThumbnailPx = 16 });
            Assert.IsNull(result.FatalError);

            var entries = ExportArchiveWriterTests.ReadAllEntries(outDir);
            var build = JObject.Parse(System.Text.Encoding.UTF8.GetString(entries[ExportFormat.BuildEntry]));
            var addressables = (JObject)build["addressables"]!;
            var errors = string.Join("\n  ", result.Header!.Errors.ToArray());

            var guids = new List<string>();
            foreach (JObject g in (JArray)addressables["groups"]!)
                foreach (var t in (JArray)g["guids"]!) guids.Add(t.Value<string>()!);

            // The walker refuses to FOLLOW a link, and the group-file check catches a linked
            // GROUP — but neither looks at the root it is handed. `Assets/AddressableAssetsData`
            // is exactly the kind of folder a studio shares between two projects.
            CollectionAssert.DoesNotContain(guids, LinkedAdrGuid,
                "the group data folder is a symlink and its contents were read through it: " +
                string.Join(", ", guids.ToArray()));

            // NEVER SILENT, AND NEVER OVERSTATED: the folder IS there, so `present` stays true and
            // the reason it was not read is an `errors` row — EXACTLY ONE, the link's own, because
            // one row per link is what the 500-row listing budget rests on.
            Assert.IsTrue(addressables["present"]!.Value<bool>(),
                "the folder exists — 'present: false' would read as a project without Addressables");
            var named = new List<string>();
            foreach (var e in result.Header.Errors) if (e.Contains("AddressableAssetsData")) named.Add(e);
            Assert.AreEqual(1, named.Count,
                "one row for the link, not one per pass that declined to read through it:\n  " +
                string.Join("\n  ", named.ToArray()));
            StringAssert.Contains("symlink or junction", named[0], named[0]);
        }

        // ---- N2 -----------------------------------------------------------------------------

        [Test]
        public void TheDocAndConfigSweeps_TakeASlicePerEntryWalked_NotPerMatch()
        {
            // THE SEAM THAT RUNS IN PRODUCTION (invariant 101), not the iterator on its own: with a
            // slice budget of 0 the collector yields on every `Tick`, so the number of pumps IS the
            // number of slices it could have taken. A tree of 300 files that match nothing must
            // cost ~300 more of them than an empty one; before the repair it cost none at all, and
            // the whole walk ran between two slices.
            var bare = Path.Combine(_tempRoot, "docroot-bare");
            var big = Path.Combine(_tempRoot, "docroot-big");
            Directory.CreateDirectory(bare);
            for (var i = 0; i < 300; i++) WriteText(Path.Combine(big, "tree", i.ToString(), "filler.dat"), "x");

            var barePumps = PumpCount(bare, Path.Combine(_tempRoot, "out-n2-bare"));
            var bigPumps = PumpCount(big, Path.Combine(_tempRoot, "out-n2-big"));

            Assert.GreaterOrEqual(bigPumps - barePumps, 300,
                "600 more entries to walk cost " + (bigPumps - barePumps) + " more slices (" +
                barePumps + " -> " + bigPumps + ")");
        }

        // ---- fixture -------------------------------------------------------------------------

        private int PumpCount(string docRoot, string outDir)
        {
            var result = new ExportResult();
            var e = ExportCollector.Run(Request(docRoot, outDir, new ExportCaps { ThumbnailPx = 16 }), result)
                .GetEnumerator();
            var pumps = 0;
            while (e.MoveNext())
                if (++pumps > 200000) Assert.Fail("the collector never finished");
            Assert.IsNull(result.FatalError);
            return pumps;
        }

        private ExportRequest Request(string docRoot, string outDir, ExportCaps caps) =>
            Request(docRoot, outDir, caps, Root);

        private ExportRequest Request(string docRoot, string outDir, ExportCaps caps, string assetsRoot) => new ExportRequest
        {
            ProjectRoot = docRoot,
            OutDir = outDir,
            AssetsRoot = assetsRoot,
            PatternScanRoot = Path.Combine(_tempRoot, "scan"),
            WorkspaceId = "ws-bound",
            KitVersion = "0.6.0-test",
            SliceBudgetMs = 0,
            Caps = caps,
        };

        private void Pump(string outDir, ExportResult result, ExportCaps caps) =>
            PumpRoot(Root, outDir, result, caps);

        private void PumpRoot(string assetsRoot, string outDir, ExportResult result, ExportCaps caps)
        {
            var e = ExportCollector
                .Run(Request(Path.Combine(_tempRoot, "project"), outDir, caps, assetsRoot), result)
                .GetEnumerator();
            var guard = 0;
            while (e.MoveNext())
                if (++guard > 200000) Assert.Fail("the collector never finished");
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

        private void CreateAssets()
        {
            DeleteRoot();
            AssetDatabase.CreateFolder("Assets", "__NovaExportBoundary");
            AssetDatabase.CreateFolder(Root, "real");
            AssetDatabase.CreateFolder(Root, "AddressableAssetsData");
            AssetDatabase.CreateFolder(Root + "/AddressableAssetsData", "AssetGroups");
            AssetDatabase.CreateFolder(GroupDirPath, "Schemas");

            WritePng(RealTexPath);

            WriteText(AbsProject(GoodGroupPath), GroupYaml(InProjectGuid, EntryGuid));
            WriteText(AbsProject(NestedGroupPath), GroupYaml(NestedGuid, NestedGuid));
            // Past a 1 KiB text cap, and nothing like the 8 MiB default — the point is the cap, not
            // the megabytes.
            WriteText(AbsProject(HugeGroupPath), GroupYaml(HugeGuid, HugeGuid) + Filler(4096));

            // OUTSIDE the project: a folder of art, and a group file that names a guid nothing in
            // this project has.
            WritePngAt(Path.Combine(_outside, "art", "linked.png"));
            WriteText(Path.Combine(_outside, "Secret.asset"), GroupYaml(OutsideGuid, OutsideGuid));

            _dirLinked = TrySymlink(AbsProject(LinkedDirPath), Path.Combine(_outside, "art"));
            _fileLinked = TrySymlink(AbsProject(LinkedGroupPath), Path.Combine(_outside, "Secret.asset"));

            // M5: a SECOND assets root whose whole `AddressableAssetsData` folder is a link out of
            // the project — the shape the group-file check cannot see, because it never looks at
            // the root it walks.
            AssetDatabase.CreateFolder("Assets", "__NovaExportLinkedAdr");
            WriteText(Path.Combine(_outside, "adr", "AssetGroups", "Shared.asset"),
                GroupYaml(LinkedAdrGuid, LinkedAdrGuid));
            _adrDataLinked = TrySymlink(AbsProject(LinkedAdrDataPath), Path.Combine(_outside, "adr"));

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }

        private static string GroupYaml(string groupGuid, string entryGuid) =>
            "%YAML 1.1\n" +
            "%TAG !u! tag:unity3d.com,2011:\n" +
            "--- !u!114 &11400000\n" +
            "MonoBehaviour:\n" +
            "  m_Name: Group\n" +
            "  m_GUID: " + groupGuid + "\n" +
            "  m_SerializeEntries:\n" +
            "  - m_GUID: " + entryGuid + "\n";

        /// <summary>VALID YAML, and a lot of it. A comment block is not: Unity's own parser reads
        /// every `.asset` under `Assets/` on import and logs a parse failure, which NUnit then
        /// reports as an unhandled log message.</summary>
        private static string Filler(int bytes)
        {
            var sb = new System.Text.StringBuilder("  m_Filler:\n");
            var line = "  - " + new string('x', 200) + "\n";
            while (sb.Length < bytes) sb.Append(line);
            return sb.ToString();
        }

        private static void WritePng(string assetPath)
        {
            WritePngAt(AbsProject(assetPath));
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
        }

        private static void WritePngAt(string absolutePath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
            var tex = new Texture2D(8, 8, TextureFormat.RGBA32, false);
            var pixels = new Color32[64];
            for (var i = 0; i < pixels.Length; i++) pixels[i] = new Color32(200, 30, 60, 255);
            tex.SetPixels32(pixels);
            tex.Apply(false, false);
            File.WriteAllBytes(absolutePath, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
        }

        private static void WriteText(string path, string body)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, body);
        }

        /// <summary>`ln -s` through the shell: .NET Standard 2.1 has no CreateSymbolicLink and the
        /// kit must compile under both API compatibility levels.</summary>
        private static bool TrySymlink(string linkPath, string targetPath)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
                var psi = new ProcessStartInfo("/bin/ln", "-s \"" + targetPath + "\" \"" + linkPath + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return false;
                    p.WaitForExit(10000);
                    if (p.ExitCode != 0) return false;
                }
                return File.Exists(linkPath) || Directory.Exists(linkPath);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>THE LINK GOES FIRST, with `rm` on the link itself. A recursive delete that
        /// followed it would empty the target, and Mono's does follow one.</summary>
        private static void RemoveLink(string absolutePath)
        {
            try
            {
                var psi = new ProcessStartInfo("/bin/rm", "-f \"" + absolutePath + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                };
                using (var p = Process.Start(psi)) p?.WaitForExit(10000);
            }
            catch (Exception) { /* best effort */ }
            try { if (File.Exists(absolutePath + ".meta")) File.Delete(absolutePath + ".meta"); }
            catch (Exception) { /* best effort */ }
        }

        private void DeleteRoot()
        {
            RemoveLink(AbsProject(LinkedDirPath));
            RemoveLink(AbsProject(LinkedGroupPath));
            RemoveLink(AbsProject(LinkedAdrDataPath));
            foreach (var root in new[] { Root, LinkedAdrRoot })
            {
                AssetDatabase.DeleteAsset(root);
                var abs = AbsProject(root);
                if (Directory.Exists(abs)) Directory.Delete(abs, recursive: true);
                if (File.Exists(abs + ".meta")) File.Delete(abs + ".meta");
            }
        }

        private static string AbsProject(string assetPath) =>
            Path.Combine(Path.GetDirectoryName(Application.dataPath)!,
                assetPath.Replace('/', Path.DirectorySeparatorChar));
    }
}
