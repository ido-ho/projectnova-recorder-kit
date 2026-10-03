using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// Slice D — the collector, against REAL assets this fixture creates.
    ///
    /// The test host's `Assets/` is empty, so an export of it has 0 assets — which is exactly the
    /// success-shaped "no data" answer the whole format exists to catch (spec §7.4). A suite run
    /// against it would pass while proving nothing. So SetUp writes real bytes: PNGs, two TTFs, a
    /// WAV, a material that references a texture, a scene that references the material, and a
    /// ScriptableObject with Colors and a Font.
    ///
    /// The assertions are POSITIVE CONTROLS AGAINST THE SOURCE — the literal things SetUp created —
    /// not against the collector's own second opinion.
    /// </summary>
    public class ExportCollectorTests
    {
        private const string Root = "Assets/__NovaExportTest";
        private const string TexAPath = Root + "/Textures/tex_a.png";
        private const string TexBPath = Root + "/Textures/tex_b.png";
        private const string TexBigPath = Root + "/Textures/tex_big.png";
        private const string ResTexPath = Root + "/Resources/res_tex.png";
        private const string MatPath = Root + "/Materials/mat.mat";
        private const string ColorsPath = Root + "/Data/colors.asset";
        private const string UsedFontPath = Root + "/Fonts/used.ttf";
        private const string UnusedFontPath = Root + "/Fonts/unused.ttf";
        private const string ClipPath = Root + "/Audio/clip.wav";
        private const string ScenePath = Root + "/Scenes/Box.unity";
        private const string GhostPath = Root + "/Misc/ghost.png";

        /// <summary>Exactly what SetUp creates — the number every total is checked against.</summary>
        private static readonly string[] CreatedAssets =
        {
            TexAPath, TexBPath, TexBigPath, ResTexPath, MatPath, ColorsPath,
            UsedFontPath, UnusedFontPath, ClipPath, ScenePath, GhostPath,
        };

        private static readonly string[] SubFolders =
            { "Textures", "Resources", "Materials", "Data", "Fonts", "Audio", "Scenes", "Misc" };

        private const int ClipHz = 22050;
        private const int ClipSamples = ClipHz / 2;   // half a second

        private string _tempRoot = "";
        private string _docRoot = "";
        private string _patternRoot = "";
        private string _outDir = "";
        private ExportResult _result = new ExportResult();
        private ExportHeader _header = new ExportHeader();
        private Dictionary<string, byte[]> _entries = new Dictionary<string, byte[]>();
        private List<string> _order = new List<string>();
        private EditorBuildSettingsScene[] _previousBuildScenes = new EditorBuildSettingsScene[0];

        // ================================================================================

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            LogAssert.ignoreFailingMessages = true;

            _tempRoot = Path.Combine(Path.GetTempPath(), "novakit-collector-" + Path.GetRandomFileName());
            _docRoot = Path.Combine(_tempRoot, "project");
            _patternRoot = Path.Combine(_tempRoot, "scripts");
            _outDir = Path.Combine(_tempRoot, "out");
            WriteDocFixtures();
            WritePatternFixtures();
            CreateAssets();

            _result = new ExportResult();
            Pump(MakeRequest(_outDir), _result);
            Assert.IsNull(_result.FatalError, "the fixture export must not be fatal");
            Assert.IsNotNull(_result.Header);
            _header = _result.Header!;
            _entries = ExportArchiveWriterTests.ReadAllEntries(_outDir);
            _order = ExportArchiveWriterTests.ReadEntryOrder(_outDir);
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            try { EditorBuildSettings.scenes = _previousBuildScenes; } catch (Exception) { }
            DeleteRoot();
            if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
        }

        [SetUp]
        public void SetUp() => LogAssert.ignoreFailingMessages = true;

        // ---- totals ---------------------------------------------------------------------

        [Test]
        public void Totals_AreTheAssetsSetUpCreated_CountedTwoIndependentWays()
        {
            Assert.IsTrue(_result.Done);
            Assert.AreEqual("done", _result.Phase);
            Assert.AreEqual(1f, _result.Progress01);

            Assert.AreEqual(CreatedAssets.Length, _header.Totals.Assets, "the literal file list SetUp wrote");

            // and again off the AssetDatabase, with folders removed the way the spec says
            var direct = new HashSet<string>(StringComparer.Ordinal);
            foreach (var g in AssetDatabase.FindAssets("", new[] { Root }))
            {
                var p = AssetDatabase.GUIDToAssetPath(g);
                if (!string.IsNullOrEmpty(p) && !AssetDatabase.IsValidFolder(p)) direct.Add(p);
            }
            Assert.AreEqual(direct.Count, _header.Totals.Assets, "AssetDatabase's own count");
            foreach (var path in CreatedAssets)
                Assert.IsTrue(direct.Contains(path), path + " is in the database");

            var sum = 0;
            foreach (var kv in _header.Totals.AssetsByType) sum += kv.Value;
            Assert.AreEqual(_header.Totals.Assets, sum, "assetsByType must sum to assets");

            Assert.AreEqual(1, _header.Totals.AssetsByType["Material"]);
            Assert.AreEqual(1, _header.Totals.AssetsByType["AudioClip"]);
            Assert.AreEqual(1, _header.Totals.AssetsByType["SceneAsset"]);
            Assert.AreEqual(2, _header.Totals.AssetsByType["Font"]);
            Assert.AreEqual(1, _header.Totals.AssetsByType["ExportTestColorAsset"]);

            Assert.AreEqual(1, _header.Totals.Scenes);
            Assert.AreEqual(1, _header.Totals.BuildScenes);

            var dirs = new List<string>();
            foreach (var s in SubFolders) dirs.Add(Root + "/" + s);
            dirs.Sort(StringComparer.Ordinal);
            CollectionAssert.AreEqual(dirs, _header.Totals.DirsTopLevel);
        }

        [Test]
        public void InventoryRows_EqualTheTotals_AndCarryTheSpecFields()
        {
            var rows = Rows(ExportFormat.InventoryBase);
            Assert.AreEqual(_header.Totals.Assets, rows.Count, "inventory rows == totals.assets");
            Assert.AreEqual(rows.Count, _header.Inventory.Rows, "files.inventory.rows");
            CollectionAssert.IsNotEmpty(_header.Inventory.Shards);

            var byPath = ByPath(rows);
            foreach (var path in CreatedAssets)
                Assert.IsTrue(byPath.ContainsKey(path), path + " has an inventory row");

            var a = byPath[TexAPath];
            Assert.AreEqual(AssetDatabase.AssetPathToGUID(TexAPath), a["guid"]!.Value<string>());
            Assert.AreEqual("Texture2D", a["type"]!.Value<string>());
            Assert.IsFalse(a.ContainsKey("base"), "`base` is present only when it holds");
            // `bytes` is the file on disk — proof that asset paths resolve against the Unity project
            // even though req.ProjectRoot points somewhere else entirely.
            Assert.AreEqual(new FileInfo(AbsProject(TexAPath)).Length, a["bytes"]!.Value<long>());

            Assert.AreEqual("ScriptableObject", byPath[ColorsPath]["base"]!.Value<string>());
            Assert.AreEqual("ExportTestColorAsset", byPath[ColorsPath]["type"]!.Value<string>());
            Assert.AreEqual(0L, byPath[GhostPath]["bytes"]!.Value<long>(), "a file we could not read is 0, not a guess");

            // rows are sorted by path: two exports of one project have to match
            var sorted = new List<string>();
            foreach (var r in rows) sorted.Add(r["path"]!.Value<string>()!);
            var expected = new List<string>(sorted);
            expected.Sort(StringComparer.Ordinal);
            CollectionAssert.AreEqual(expected, sorted);
        }

        [Test]
        public void InBuild_IsTheEnabledSceneClosureAndResources()
        {
            var byPath = ByPath(Rows(ExportFormat.InventoryBase));
            Assert.IsTrue(byPath[ResTexPath]["inBuild"]!.Value<bool>(), "under a Resources folder");
            Assert.IsTrue(byPath[MatPath]["inBuild"]!.Value<bool>(), "the enabled build scene references it");
            Assert.IsTrue(byPath[TexAPath]["inBuild"]!.Value<bool>(), "reached through the material");
            Assert.IsTrue(byPath[ScenePath]["inBuild"]!.Value<bool>(), "the scene itself");
            Assert.IsFalse(byPath[TexBPath]["inBuild"]!.Value<bool>(), "nothing reaches it");
            Assert.IsFalse(byPath[UnusedFontPath]["inBuild"]!.Value<bool>(), "nothing reaches it");
        }

        [Test]
        public void DependencyRows_AreTheEdgesSetUpCreated()
        {
            var rows = Rows(ExportFormat.DependenciesBase);
            Assert.AreEqual(rows.Count, _header.Totals.DependencyRows);
            Assert.AreEqual(rows.Count, _header.Dependencies.Rows);

            var deps = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var r in rows)
            {
                var list = new List<string>();
                foreach (var d in (JArray)r["deps"]!) list.Add(d.Value<string>()!);
                deps[r["guid"]!.Value<string>()!] = list;
            }

            var mat = AssetDatabase.AssetPathToGUID(MatPath);
            var texA = AssetDatabase.AssetPathToGUID(TexAPath);
            var colors = AssetDatabase.AssetPathToGUID(ColorsPath);
            var usedFont = AssetDatabase.AssetPathToGUID(UsedFontPath);
            var scene = AssetDatabase.AssetPathToGUID(ScenePath);

            CollectionAssert.Contains(deps[mat], texA, "the material references tex_a");
            CollectionAssert.Contains(deps[colors], usedFont, "the ScriptableObject references used.ttf");
            CollectionAssert.Contains(deps[scene], mat, "the scene references the material");

            // Only guids that resolve to an inventory row: no `unity_builtin_extra`, no package
            // shader, nothing the hosted side could not join.
            var known = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in Rows(ExportFormat.InventoryBase)) known.Add(r["guid"]!.Value<string>()!);
            foreach (var kv in deps)
            {
                Assert.IsTrue(known.Contains(kv.Key), "dependency row guid " + kv.Key + " has an inventory row");
                foreach (var d in kv.Value)
                {
                    Assert.IsTrue(known.Contains(d), "dep guid " + d + " has an inventory row");
                    Assert.AreNotEqual(kv.Key, d, "self-reference is removed");
                }
                CollectionAssert.IsNotEmpty(kv.Value, "rows with no deps are omitted");
            }
        }

        // ---- identity --------------------------------------------------------------------

        [Test]
        public void Identity_Fonts_CarryTheRefCountControl()
        {
            var identity = Json(ExportFormat.IdentityEntry);
            var fonts = (JArray)identity["fonts"]!;

            JObject? usedOrNull = null, unusedOrNull = null;
            foreach (JObject f in fonts)
            {
                var p = f["path"]!.Value<string>();
                if (p == UsedFontPath) usedOrNull = f;
                if (p == UnusedFontPath) unusedOrNull = f;
            }
            Assert.IsNotNull(usedOrNull, "used.ttf is in identity.fonts");
            Assert.IsNotNull(unusedOrNull, "unused.ttf is in identity.fonts");
            var used = usedOrNull!;
            var unused = unusedOrNull!;

            // the 722-vs-0 control, in miniature: one asset lists used.ttf among its DIRECT
            // dependencies and nothing lists unused.ttf.
            Assert.AreEqual(1, used["refCount"]!.Value<int>());
            Assert.AreEqual(0, unused["refCount"]!.Value<int>());

            Assert.AreEqual("Font", used["type"]!.Value<string>());
            Assert.AreEqual(AssetDatabase.AssetPathToGUID(UsedFontPath), used["guid"]!.Value<string>());
            Assert.IsTrue(used.ContainsKey("familyName"));
            var family = used["familyName"]!.Type == JTokenType.Null ? null : used["familyName"]!.Value<string>();
            Assert.IsFalse(string.IsNullOrEmpty(family), "the family name is read off the imported font");
            Assert.AreEqual(family, unused["familyName"]!.Value<string>(), "same bytes, same family");
        }

        [Test]
        public void Identity_ColoursAndAudio_AreWhatSetUpPutThere()
        {
            var identity = Json(ExportFormat.IdentityEntry);

            JObject? colorObjectOrNull = null;
            foreach (JObject o in (JArray)identity["colorObjects"]!)
                if (o["path"]!.Value<string>() == ColorsPath) colorObjectOrNull = o;
            Assert.IsNotNull(colorObjectOrNull, "the ScriptableObject with Colors is listed");
            var colorObject = colorObjectOrNull!;
            Assert.AreEqual("ExportTestColorAsset", colorObject["type"]!.Value<string>());

            var hexByName = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JObject c in (JArray)colorObject["colors"]!)
                hexByName[c["name"]!.Value<string>()!] = c["hex"]!.Value<string>()!;
            Assert.AreEqual("#FF0000FF", hexByName["primary"], "Color.red, as SetUp assigned it");
            Assert.IsTrue(hexByName.ContainsKey("secondary"));

            JObject? clipOrNull = null;
            foreach (JObject o in (JArray)identity["audio"]!)
                if (o["path"]!.Value<string>() == ClipPath) clipOrNull = o;
            Assert.IsNotNull(clipOrNull, "the wav is listed");
            var clip = clipOrNull!;
            Assert.AreEqual(ClipHz, clip["frequency"]!.Value<int>());
            Assert.AreEqual(1, clip["channels"]!.Value<int>());
            Assert.AreEqual(0.5, clip["lengthSec"]!.Value<double>(), 0.01);

            Assert.IsTrue(identity.ContainsKey("icons"), "present even with nothing in it");
        }

        // ---- art -------------------------------------------------------------------------

        [Test]
        public void AFileOverTheCap_IsNotInTheZip_AndIsNamedInOverCap()
        {
            var bigGuid = AssetDatabase.AssetPathToGUID(TexBigPath);
            var bigEntry = ExportFormat.ArtFileEntry(bigGuid, ".png");
            Assert.IsFalse(_entries.ContainsKey(bigEntry), "the over-cap original is not shipped");

            ExportOverCap? overOrNull = null;
            foreach (var o in _header.OverCap)
                if (o.Path == TexBigPath) overOrNull = o;
            Assert.IsNotNull(overOrNull, "and it is NEVER silent about it");
            var over = overOrNull!;
            Assert.AreEqual(ExportFormat.ReasonOverArtFile, over.Reason);
            Assert.AreEqual(ExportFormat.KindTexture, over.Kind);
            Assert.AreEqual(new FileInfo(AbsProject(TexBigPath)).Length, over.Bytes);

            // …while a file under the cap IS shipped, byte for byte
            var aGuid = AssetDatabase.AssetPathToGUID(TexAPath);
            var aEntry = ExportFormat.ArtFileEntry(aGuid, ".png");
            Assert.IsTrue(_entries.ContainsKey(aEntry), aEntry);
            CollectionAssert.AreEqual(File.ReadAllBytes(AbsProject(TexAPath)), _entries[aEntry]);
        }

        [Test]
        public void Thumbnails_AreValidPngsNoLargerThanTheCap()
        {
            var caps = _header.Caps;
            var thumbEntries = 0;
            foreach (var kv in _entries)
                if (kv.Key.StartsWith(ExportFormat.ArtThumbsPrefix, StringComparison.Ordinal))
                {
                    thumbEntries++;
                    AssertPng(kv.Value, out var w, out var h, kv.Key);
                    Assert.LessOrEqual(w, caps.ThumbnailPx, kv.Key + " width");
                    Assert.LessOrEqual(h, caps.ThumbnailPx, kv.Key + " height");
                }
            Assert.AreEqual(_header.Totals.Thumbnails, thumbEntries, "totals.thumbnails == art/thumbs entries");

            // a texture larger than the cap is scaled DOWN to it; one smaller is left alone
            AssertPng(_entries[ExportFormat.ArtThumbEntry(AssetDatabase.AssetPathToGUID(TexBigPath))],
                out var bw, out var bh, "tex_big thumb");
            Assert.AreEqual(caps.ThumbnailPx, bw);
            Assert.AreEqual(caps.ThumbnailPx, bh);

            AssertPng(_entries[ExportFormat.ArtThumbEntry(AssetDatabase.AssetPathToGUID(TexAPath))],
                out var aw, out var ah, "tex_a thumb");
            Assert.AreEqual(8, aw);
            Assert.AreEqual(8, ah);

            foreach (var p in new[] { TexBPath, ResTexPath })
                Assert.IsTrue(_entries.ContainsKey(ExportFormat.ArtThumbEntry(AssetDatabase.AssetPathToGUID(p))), p);
        }

        [Test]
        public void ArtIndex_MapsEveryArtEntryBackToItsAsset()
        {
            var rows = Rows(ExportFormat.ArtIndexBase);
            Assert.AreEqual(rows.Count, _header.ArtIndex.Rows);

            var files = 0;
            var thumbs = 0;
            foreach (var r in rows)
            {
                var entry = r["entry"]!.Value<string>()!;
                Assert.IsTrue(_entries.ContainsKey(entry), entry + " is an entry in a part");
                Assert.AreEqual(_entries[entry].LongLength, r["bytes"]!.Value<long>(), entry + " byte count");
                Assert.AreEqual(AssetDatabase.AssetPathToGUID(r["path"]!.Value<string>()), r["guid"]!.Value<string>());
                if (r["kind"]!.Value<string>() == "file") files++; else thumbs++;
            }
            Assert.AreEqual(_header.Totals.ArtFiles, files);
            Assert.AreEqual(_header.Totals.Thumbnails, thumbs);
        }

        // ---- patterns, docs, build -------------------------------------------------------

        [Test]
        public void Patterns_CarryTheRightFileAndLineForEveryId()
        {
            var rows = Rows(ExportFormat.PatternsBase);
            Assert.AreEqual(rows.Count, _header.Totals.PatternHits);
            Assert.AreEqual(rows.Count, _header.Patterns.Rows);

            var lineById = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var r in rows)
            {
                Assert.AreEqual("Fixture.cs", r["file"]!.Value<string>(),
                    "the near-miss file must not produce a single hit");
                lineById[r["pattern"]!.Value<string>()!] = r["line"]!.Value<int>();
            }

            Assert.AreEqual(7, rows.Count);
            Assert.AreEqual(2, lineById["unity-editor-ifdef"]);
            Assert.AreEqual(4, lineById["debug-class"]);
            Assert.AreEqual(6, lineById["random-seed"]);
            Assert.AreEqual(7, lineById["remote-config"]);
            Assert.AreEqual(8, lineById["tutorial-key"]);
            Assert.AreEqual(9, lineById["feature-unlock"]);
            Assert.AreEqual(10, lineById["playerprefs-key"]);
        }

        [Test]
        public void DocsAndConfig_ComeFromProjectRoot()
        {
            Assert.IsTrue(_entries.ContainsKey("docs/docs/onboarding.md"));
            Assert.IsTrue(_entries.ContainsKey("docs/README.md"));
            Assert.IsTrue(_entries.ContainsKey("config/remote_config_prod.json"));
            Assert.AreEqual(3, _header.Totals.Docs);
            Assert.AreEqual("onboarding", System.Text.Encoding.UTF8.GetString(_entries["docs/docs/onboarding.md"]));
        }

        [Test]
        public void BuildJson_CarriesTheScenesAndThePlayerSettings()
        {
            var build = Json(ExportFormat.BuildEntry);
            var scenes = (JArray)build["scenes"]!;
            Assert.AreEqual(1, scenes.Count);
            Assert.AreEqual(ScenePath, scenes[0]["path"]!.Value<string>());
            Assert.IsTrue(scenes[0]["enabled"]!.Value<bool>());
            Assert.AreEqual(AssetDatabase.AssetPathToGUID(ScenePath), scenes[0]["guid"]!.Value<string>());

            var ps = (JObject)build["playerSettings"]!;
            Assert.AreEqual(PlayerSettings.productName, ps["productName"]!.Value<string>());
            Assert.AreEqual(PlayerSettings.companyName, ps["companyName"]!.Value<string>());
            foreach (var key in new[]
                     {
                         "productName", "companyName", "bundleVersion", "applicationIdentifier",
                         "runInBackground", "defaultOrientation", "iconGuids",
                     })
                Assert.IsTrue(ps.ContainsKey(key), "playerSettings." + key);

            CollectionAssert.Contains(ToStrings((JArray)build["resourcesDirs"]!), Root + "/Resources");
            Assert.IsFalse(((JObject)build["streamingAssets"]!)["present"]!.Value<bool>());
            Assert.IsFalse(((JObject)build["addressables"]!)["present"]!.Value<bool>());
            Assert.IsNotEmpty(build["activeBuildTarget"]!.Value<string>());
        }

        // ---- the header on disk, and the entry order -------------------------------------

        [Test]
        public void HeaderJsonOnDisk_IsTheSameHeaderTheResultCarries()
        {
            var path = Path.Combine(_outDir, "header.json");
            Assert.IsTrue(File.Exists(path), "written so an upload can resume after a domain reload");
            Assert.AreEqual(_header.ToJsonString(), File.ReadAllText(path));

            var reread = ExportHeader.FromJson(File.ReadAllText(path));
            Assert.IsNotNull(reread);
            Assert.AreEqual(1, reread!.SchemaVersion);
            Assert.AreEqual("0.6.0-test", reread.KitVersion);
            Assert.AreEqual("ws-test", reread.WorkspaceId);
            Assert.IsNull(reread.EditorGameId);
            Assert.AreEqual(Application.unityVersion, reread.UnityVersion);
            StringAssert.IsMatch(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$", reread.ExportedAt);

            Assert.IsNotEmpty(reread.Parts);
            foreach (var part in reread.Parts)
            {
                var partPath = Path.Combine(_outDir, part.Name);
                Assert.IsTrue(File.Exists(partPath), part.Name);
                Assert.AreEqual(new FileInfo(partPath).Length, part.Bytes, part.Name + " size");
                StringAssert.IsMatch("^[0-9a-f]{64}$", part.Sha256);
            }

            Assert.IsFalse(Directory.Exists(Path.Combine(_outDir, ".tmp")), "the thumbnail scratch is swept");
        }

        [Test]
        public void EntryOrder_IsTheOrderTheSpecPins()
        {
            // 1 build, 2 identity, 3 inventory, 4 dependencies, 5 patterns then ui, 6 docs/config then strings,
            // 7 art/files, 8 art/thumbs, 9 art/index.
            //
            // THE INDEX COMES AFTER THE ART IT INDEXES (spec §3, changed 2026-09-21). It used to go
            // first, which meant it described the PLAN: one art file locked or deleted between the
            // plan and the copy left the index — and `totals.artFiles` — one ahead of what the zips
            // actually held, and the server fails the whole export on a count difference. The
            // hosted reconcile opens every part, so nothing depends on the facts arriving first.
            Assert.AreEqual(ExportFormat.BuildEntry, _order[0]);
            Assert.AreEqual(ExportFormat.IdentityEntry, _order[1]);

            var last = -1;
            foreach (var name in _order)
            {
                var rank = Rank(name);
                Assert.GreaterOrEqual(rank, last, name + " is out of the spec's entry order");
                last = rank;
            }
        }

        private static int Rank(string entry)
        {
            if (entry == ExportFormat.BuildEntry) return 1;
            if (entry == ExportFormat.IdentityEntry) return 2;
            if (entry.StartsWith("inventory.", StringComparison.Ordinal)) return 3;
            if (entry.StartsWith("dependencies.", StringComparison.Ordinal)) return 4;
            if (entry.StartsWith("patterns.", StringComparison.Ordinal)) return 5;
            if (entry.StartsWith(ExportFormat.UiBase + ".", StringComparison.Ordinal)) return 5;      // spec §8.7, after patterns
            if (entry.StartsWith(ExportFormat.DocsPrefix, StringComparison.Ordinal)) return 6;
            if (entry.StartsWith(ExportFormat.StringsPrefix, StringComparison.Ordinal)) return 6;     // spec §8.6, after docs
            if (entry.StartsWith(ExportFormat.ConfigPrefix, StringComparison.Ordinal)) return 6;
            if (entry.StartsWith(ExportFormat.ArtFilesPrefix, StringComparison.Ordinal)) return 7;
            if (entry.StartsWith(ExportFormat.ArtThumbsPrefix, StringComparison.Ordinal)) return 8;
            if (entry.StartsWith("art/index.", StringComparison.Ordinal)) return 9;
            return 99;
        }

        // ---- failure shapes ---------------------------------------------------------------

        [Test]
        public void AnAssetItCannotRead_IsAnErrorRow_NotAnException()
        {
            var found = false;
            foreach (var e in _header.Errors)
                if (e.IndexOf("ghost.png", StringComparison.Ordinal) >= 0) found = true;
            Assert.IsTrue(found, "the deleted-behind-the-database file is named in errors:\n  " +
                                 string.Join("\n  ", _header.Errors.ToArray()));
            Assert.IsNull(_result.FatalError, "…and the export still shipped");
            Assert.IsTrue(_result.Done);
        }

        [Test]
        public void TheCollectorYields_ItDoesNotRunToCompletionInOneMoveNext()
        {
            var dir = Path.Combine(_tempRoot, "yield");
            var request = MakeRequest(dir);
            request.SliceBudgetMs = 0;
            var result = new ExportResult();

            var e = ExportCollector.Run(request, result).GetEnumerator();
            Assert.IsTrue(e.MoveNext(), "the first slice yields");
            Assert.IsFalse(result.Done, "a non-trivial export cannot be finished in one MoveNext");

            var slices = 1;
            while (e.MoveNext()) slices++;
            Assert.Greater(slices, 5, "and it goes on yielding");
            Assert.IsTrue(result.Done);
            Assert.IsNull(result.FatalError);
        }

        [Test]
        public void TwoExportsOfTheSameProject_ProduceTheSameFacts()
        {
            var a = Path.Combine(_tempRoot, "det-a");
            var b = Path.Combine(_tempRoot, "det-b");
            var ra = new ExportResult();
            var rb = new ExportResult();
            Pump(MakeRequest(a), ra);
            Pump(MakeRequest(b), rb);

            var ea = ExportArchiveWriterTests.ReadAllEntries(a);
            var eb = ExportArchiveWriterTests.ReadAllEntries(b);
            CollectionAssert.AreEquivalent(new List<string>(ea.Keys), new List<string>(eb.Keys));
            foreach (var name in ea.Keys)
                CollectionAssert.AreEqual(ea[name], eb[name], name + " differs between two exports");
        }

        [Test]
        public void AnUnwritableOutDir_IsFatal_AndIsTheOnlyThingThatIs()
        {
            var blocker = Path.Combine(_tempRoot, "a-file-not-a-dir");
            File.WriteAllText(blocker, "x");

            var request = MakeRequest(Path.Combine(blocker, "export"));
            var result = new ExportResult();
            Pump(request, result);

            Assert.IsTrue(result.Done);
            Assert.IsNotNull(result.FatalError);
            StringAssert.Contains("cannot write to OutDir", result.FatalError);
            Assert.IsNull(result.Header, "no header means the hosted side says 'closed without a header'");
        }

        // ================================================================================
        [Test]
        public void NothingAbsoluteLeavesTheMachine_NotEvenInsideAnExceptionMessage()
        {
            // Shown RED on the real artifact first (invariant 40): the first export this suite
            // handed to the hosted side carried "Could not find file '/Users/<name>/…/ghost.png'"
            // in header.errors — .NET writes the absolute path into its own exception text. The
            // ghost asset below is the same one, so this is that exact message, scrubbed.
            Assert.IsNotEmpty(_header.Errors, "the ghost asset must still produce an error row (or this test proves nothing)");
            var projectDir = Path.GetDirectoryName(Application.dataPath)!;
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var headerText = File.ReadAllText(Path.Combine(_outDir, "header.json"));
            foreach (var leak in new[] { projectDir, home, _tempRoot })
            {
                if (string.IsNullOrEmpty(leak) || leak.Length < 4) continue;
                StringAssert.DoesNotContain(leak, headerText, "header.json carries an absolute path from this machine");
                foreach (var entry in _entries)
                {
                    if (entry.Key.StartsWith(ExportFormat.ArtFilesPrefix) || entry.Key.StartsWith(ExportFormat.ArtThumbsPrefix)) continue;
                    StringAssert.DoesNotContain(leak, System.Text.Encoding.UTF8.GetString(entry.Value),
                        entry.Key + " carries an absolute path from this machine");
                }
            }
            StringAssert.Contains("<project>", string.Join("\n", _header.Errors), "the path was relabelled, not just deleted");
        }

        // ---- the two production seams this export crosses (invariant 101) ---------------

        [Test]
        public void TheResumeSeam_ExportOnDiskAcceptsAFreshBuild_AndRefusesItOnceAPartIsShort()
        {
            // After a domain reload the agent does not trust its own "built" flag: it calls
            // ExportOnDisk.LoadVerified on the OutDir the collector wrote. That pairing — the
            // collector's header.json + part files read back by the agent's resume rule — is the
            // seam, and until this test nothing exercised the two halves together.
            var header = ExportOnDisk.LoadVerified(_outDir, out var why);
            Assert.IsNull(why);
            Assert.IsNotNull(header);
            Assert.AreEqual(_header.Parts.Count, header!.Parts.Count);
            Assert.AreEqual(_header.Totals.Assets, header.Totals.Assets);
            Assert.AreEqual(_header.ExportedAt, header.ExportedAt, "the ISO timestamp must survive the round trip");

            // The control: the SAME build with one part a byte short is debris, not a build.
            var copy = Path.Combine(_tempRoot, "resume-copy");
            Directory.CreateDirectory(copy);
            foreach (var f in Directory.GetFiles(_outDir))
                File.Copy(f, Path.Combine(copy, Path.GetFileName(f)), true);
            var part0 = Path.Combine(copy, ExportFormat.PartName(0));
            var bytes = File.ReadAllBytes(part0);
            Array.Resize(ref bytes, bytes.Length - 1);
            File.WriteAllBytes(part0, bytes);
            Assert.IsNull(ExportOnDisk.LoadVerified(copy, out var shortWhy));
            StringAssert.Contains("bytes on disk; the header says", shortWhy);
        }

        [Test]
        public void TheHostedSeam_WritesTheRealExportOut_WhenAsked()
        {
            // The hosted reconcile (apps/api/src/onboarding/export/) is TypeScript reading zips
            // written by .NET. Its own specs build their zips with a JS library, which proves the
            // reconcile's LOGIC and nothing about whether it can read what THIS code writes:
            // field names, sha256 casing, entry names, line endings, data descriptors. So this
            // hands a real export to the other side. Set NOVA_EXPORT_FIXTURE_OUT to a directory
            // and run the suite; the API spec `kit-export-seam.spec.ts` reads what lands there.
            var outDir = Environment.GetEnvironmentVariable("NOVA_EXPORT_FIXTURE_OUT");
            if (string.IsNullOrWhiteSpace(outDir))
                Assert.Ignore("NOVA_EXPORT_FIXTURE_OUT is not set — nothing to write (this is the normal run)");
            Directory.CreateDirectory(outDir!);
            foreach (var old in Directory.GetFiles(outDir!)) File.Delete(old);
            foreach (var f in Directory.GetFiles(_outDir))
                File.Copy(f, Path.Combine(outDir!, Path.GetFileName(f)), true);
            Assert.IsTrue(File.Exists(Path.Combine(outDir!, ExportOnDisk.HeaderFileName)));
            Assert.AreEqual(_header.Parts.Count, Directory.GetFiles(outDir!, "part-*.zip").Length);
        }

        // fixture

        private ExportRequest MakeRequest(string outDir) => new ExportRequest
        {
            // deliberately NOT the Unity project: docs/config come from here, asset paths do not
            ProjectRoot = _docRoot,
            OutDir = outDir,
            AssetsRoot = Root,
            PatternScanRoot = _patternRoot,
            WorkspaceId = "ws-test",
            EditorGameId = null,
            AdapterJsonPresent = false,
            KitVersion = "0.6.0-test",
            SliceBudgetMs = 4,
            Caps = new ExportCaps { ArtFileMaxBytes = 4096, ThumbnailPx = 16 },
        };

        private static void Pump(ExportRequest request, ExportResult result)
        {
            var e = ExportCollector.Run(request, result).GetEnumerator();
            var guard = 0;
            while (e.MoveNext())
                if (++guard > 200000) Assert.Fail("the collector never finished");
        }

        private void WriteDocFixtures()
        {
            WriteText(Path.Combine(_docRoot, "docs", "onboarding.md"), "onboarding");
            WriteText(Path.Combine(_docRoot, "README.md"), "# readme");
            WriteText(Path.Combine(_docRoot, "remote_config_prod.json"), "{}");
            WriteText(Path.Combine(_docRoot, "Library", "remote_config.json"), "{}");   // skipped
        }

        private void WritePatternFixtures()
        {
            WriteText(Path.Combine(_patternRoot, "Fixture.cs"),
                "// fixture\n" +
                "#if UNITY_EDITOR\n" +
                "#endif\n" +
                "public class CheatMenu { }\n" +
                "// spacer\n" +
                "Random.InitState(42);\n" +
                "RemoteConfig.Fetch();\n" +
                "var tutorialStep = 1;\n" +
                "const string FeatureUnlock = \"x\";\n" +
                "PlayerPrefs.SetInt(\"coins\", 1);\n");
            WriteText(Path.Combine(_patternRoot, "Near.cs"),
                "// near misses\n" +
                "#if UNITY_ANDROID\n" +
                "#endif\n" +
                "int debugClass = 0;\n" +
                "Random.Range(0, 10);\n" +
                "RemoteConfigs.Fetch();\n" +
                "int tutorialStep;\n" +
                "var featureLocked = true;\n" +
                "PlayerPrefs.SetInt(keyName, 1);\n");
        }

        private void CreateAssets()
        {
            DeleteRoot();
            AssetDatabase.CreateFolder("Assets", "__NovaExportTest");
            foreach (var sub in SubFolders) AssetDatabase.CreateFolder(Root, sub);

            WritePng(TexAPath, 8, 8, noise: false);
            WritePng(TexBPath, 16, 16, noise: false);
            WritePng(TexBigPath, 64, 64, noise: true);      // incompressible -> over artFileMaxBytes
            WritePng(ResTexPath, 8, 8, noise: false);

            CopySystemFont(UsedFontPath);
            CopySystemFont(UnusedFontPath);
            WriteWav(ClipPath);

            var shader = Shader.Find("Unlit/Texture");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Standard");
            Assert.IsNotNull(shader, "a builtin shader for the fixture material");
            var material = new Material(shader);
            material.mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(TexAPath);
            AssetDatabase.CreateAsset(material, MatPath);

            var colors = ScriptableObject.CreateInstance<ExportTestColorAsset>();
            colors.primary = Color.red;
            colors.secondary = new Color(0.1f, 0.2f, 0.3f, 1f);
            colors.font = AssetDatabase.LoadAssetAtPath<Font>(UsedFontPath);
            Assert.IsNotNull(colors.font, "the fixture font imported");
            AssetDatabase.CreateAsset(colors, ColorsPath);

            // A scene asset that genuinely references the material, so `inBuild` has a closure to
            // find. SaveScene with saveAsCopy writes the file WITHOUT adopting it as the open
            // scene — the kit must never open or close a scene, and a test that did would also
            // trip "cannot create a new scene additively with an untitled scene unsaved".
            var go = new GameObject("__NovaExportBox");
            go.AddComponent<MeshRenderer>().sharedMaterial = material;
            EditorSceneManager.SaveScene(SceneManager.GetActiveScene(), ScenePath, saveAsCopy: true);
            UnityEngine.Object.DestroyImmediate(go);
            AssetDatabase.ImportAsset(ScenePath, ImportAssetOptions.ForceSynchronousImport);

            _previousBuildScenes = EditorBuildSettings.scenes;
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };

            // Imported, then its bytes removed behind the AssetDatabase's back: the deterministic
            // "an asset the exporter cannot READ" case.
            WritePng(GhostPath, 8, 8, noise: false);
            File.Delete(AbsProject(GhostPath));
        }

        private static void DeleteRoot()
        {
            AssetDatabase.DeleteAsset(Root);
            var abs = AbsProject(Root);
            if (Directory.Exists(abs)) Directory.Delete(abs, recursive: true);
            if (File.Exists(abs + ".meta")) File.Delete(abs + ".meta");
        }

        private static void WritePng(string assetPath, int w, int h, bool noise)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var pixels = new Color32[w * h];
            var rng = new System.Random(w * 7919 + h);
            for (var i = 0; i < pixels.Length; i++)
                pixels[i] = noise
                    ? new Color32((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256), 255)
                    : new Color32(20, 120, 220, 255);
            tex.SetPixels32(pixels);
            tex.Apply(false, false);
            File.WriteAllBytes(AbsProject(assetPath), tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
        }

        private static void CopySystemFont(string assetPath)
        {
            string? source = null;
            foreach (var candidate in new[]
                     {
                         "/System/Library/Fonts/Supplemental/Andale Mono.ttf",
                         "/System/Library/Fonts/Supplemental/Arial.ttf",
                         "/Library/Fonts/Arial.ttf",
                         "C:/Windows/Fonts/arial.ttf",
                     })
                if (source == null && File.Exists(candidate)) source = candidate;

            if (source == null)
                foreach (var f in Directory.GetFiles("/System/Library/Fonts", "*.ttf"))
                    if (source == null) source = f;

            Assert.IsNotNull(source, "no system .ttf to build the font fixture from");
            File.Copy(source, AbsProject(assetPath), overwrite: true);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
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
                    w.Write((short)(Mathf.Sin(i * 0.05f) * 8000f));
                w.Flush();
                File.WriteAllBytes(AbsProject(assetPath), ms.ToArray());
            }
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
        }

        private static void WriteText(string path, string body)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, body);
        }

        private static string AbsProject(string assetPath) =>
            Path.Combine(Path.GetDirectoryName(Application.dataPath)!, assetPath.Replace('/', Path.DirectorySeparatorChar));

        // ---- reading the export back ------------------------------------------------------

        private List<JObject> Rows(string baseName)
        {
            var list = new List<JObject>();
            for (var i = 0; ; i++)
            {
                var name = ExportFormat.ShardName(baseName, i);
                if (!_entries.ContainsKey(name)) break;
                foreach (var line in ExportArchiveWriterTests.Lines(_entries[name]))
                    list.Add(JObject.Parse(line));
            }
            return list;
        }

        private JObject Json(string entry) =>
            JObject.Parse(System.Text.Encoding.UTF8.GetString(_entries[entry]));

        private static Dictionary<string, JObject> ByPath(List<JObject> rows)
        {
            var map = new Dictionary<string, JObject>(StringComparer.Ordinal);
            foreach (var r in rows) map[r["path"]!.Value<string>()!] = r;
            return map;
        }

        private static List<string> ToStrings(JArray arr)
        {
            var list = new List<string>();
            foreach (var t in arr) list.Add(t.Value<string>() ?? "");
            return list;
        }

        private static void AssertPng(byte[] bytes, out int width, out int height, string what)
        {
            Assert.Greater(bytes.Length, 24, what + " is too short to be a PNG");
            var magic = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
            for (var i = 0; i < magic.Length; i++)
                Assert.AreEqual(magic[i], bytes[i], what + " PNG signature byte " + i);
            width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
            height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
            Assert.Greater(width, 0, what + " width");
            Assert.Greater(height, 0, what + " height");
        }
    }
}
