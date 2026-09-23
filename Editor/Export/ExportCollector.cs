using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Walks the project and writes the export (spec §2-§6): `header.json` plus N `part-NNNN.zip`
    /// under <c>req.OutDir</c>.
    ///
    /// THE KIT EMITS FACTS, NEVER VERDICTS. Nothing here decides that a font is THE display font or
    /// that a class is a cheat menu. It reports which regex fired, how many assets list a font's
    /// guid, which colours a ScriptableObject serialises. Slot assignment happens hosted-side,
    /// where it can cite an export path and be disputed.
    ///
    /// TIME-SLICED. <see cref="Run"/> is an iterator: it yields null roughly every
    /// <c>req.SliceBudgetMs</c> so an `EditorApplication.update` pump keeps the editor alive. It
    /// never enters or exits Play Mode, never opens or closes a scene, never refreshes or saves the
    /// AssetDatabase, never touches an importer, and never writes a byte under `Assets/`.
    ///
    /// AND IT CHANGES NOTHING IT READS. Two ways it used to, both found by a fresh-context audit on
    /// 2026-09-21 and both MEASURED, not argued:
    ///  - it called <c>Resources.UnloadAsset</c> on every texture and audio clip it touched,
    ///    including ones the studio's OPEN SCENE was showing — which read back grey until something
    ///    re-bound them. Memory is now bounded ONLY by the periodic
    ///    <c>EditorUtility.UnloadUnusedAssetsImmediate</c> sweep, which by definition leaves a held
    ///    asset alone (the control that proved it: the sweep alone left the same texture red).
    ///  - it loaded every ScriptableObject, which RAN the studio's `Awake`, `OnEnable` and
    ///    `OnValidate`. Identity facts now come off the asset's YAML text instead.
    ///
    /// A PER-ASSET FAILURE IS A FACT, NOT A CRASH: it lands in `header.errors` and the export ships.
    /// Only a "cannot write to OutDir"-class failure, or an archive that can no longer be trusted
    /// (<see cref="ExportArchiveFatalException"/>), sets <see cref="ExportResult.FatalError"/>.
    /// </summary>
    public static class ExportCollector
    {
        /// <summary>Every guid reachable from <paramref name="roots"/> through the direct graph,
        /// roots included. Iterative, so a deep chain cannot overflow the stack.</summary>
        internal static HashSet<string> AddressableClosure(IEnumerable<string> roots, IReadOnlyDictionary<string, List<string>> graph)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<string>(roots);
            while (stack.Count > 0)
            {
                var g = stack.Pop();
                if (!seen.Add(g)) continue;
                if (graph.TryGetValue(g, out var deps))
                    foreach (var d in deps) if (!seen.Contains(d)) stack.Push(d);
            }
            return seen;
        }

        /// <summary>
        /// Drive this from an `EditorApplication.update` pump:
        /// <code>
        /// var e = ExportCollector.Run(req, result).GetEnumerator();
        /// // each update tick: if (!e.MoveNext()) { /* finished */ }
        /// </code>
        /// On success every part is on disk in <c>req.OutDir</c>, `header.json` is written there TOO
        /// (so an upload can resume after a domain reload), <see cref="ExportResult.Header"/> is set
        /// and <see cref="ExportResult.Done"/> is true.
        ///
        /// DISPOSING THE ENUMERATOR CLOSES THE OPEN PART. The `finally` below runs on Dispose as
        /// well as on completion, and the caller MUST dispose before it deletes the export
        /// directory: a part is held `FileShare.None`, and on Windows the delete would otherwise
        /// fail silently and leave a gigabyte behind.
        /// </summary>
        public static IEnumerable Run(ExportRequest req, ExportResult result)
        {
            var job = new Job(req, result);
            try
            {
                foreach (var _ in job.Steps())
                    yield return null!;
            }
            finally
            {
                job.Cleanup();
            }
        }

        // ==================================================================================

        private sealed class Row
        {
            public string Guid = "";
            public string Path = "";
            public string Type = "Unknown";
            public bool IsScriptableObject;
            public long Bytes;
            public bool InBuild;
            /// <summary>Spec §8.1: in the recursive closure of the Addressables group guids. Kept
            /// apart from <see cref="InBuild"/> (whose §4 meaning is pinned); "used by the game" is
            /// either.</summary>
            public bool Addressable;
            public bool Used => InBuild || Addressable;
        }

        private sealed class Item
        {
            public string Guid = "";
            public string Path = "";
            public string Entry = "";
            /// <summary>`file` or `thumb` — the art index's own `kind`.</summary>
            public string IndexKind = "file";
            public long Bytes;
            public string Source = "";
            /// <summary>The `overCap.kind` this item would be listed under, and the cap and reason
            /// that apply to it — carried from the PLAN so the WRITE can check the size again
            /// against the same rule (audit round 3, M6).</summary>
            public string OverKind = "";
            public long MaxBytes;
            public string OverReason = "";
        }

        private sealed class Job
        {
            private readonly ExportRequest _req;
            private readonly ExportResult _result;
            private readonly ExportCaps _caps;
            private readonly ExportHeader _header = new ExportHeader();
            private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();

            private readonly List<Row> _rows = new List<Row>();
            private readonly Dictionary<string, string> _pathToGuid = new Dictionary<string, string>(StringComparer.Ordinal);
            private readonly Dictionary<string, Row> _guidToRow = new Dictionary<string, Row>(StringComparer.Ordinal);
            private readonly List<KeyValuePair<string, List<string>>> _depRows = new List<KeyValuePair<string, List<string>>>();
            private readonly Dictionary<string, int> _refCount = new Dictionary<string, int>(StringComparer.Ordinal);
            private readonly List<ExportPatternHit> _hits = new List<ExportPatternHit>();
            private readonly List<Item> _docItems = new List<Item>();
            private int _configCount;
            /// <summary>Spec §8.6.</summary>
            private readonly List<Item> _stringItems = new List<Item>();
            /// <summary>Spec §8.7.</summary>
            private readonly List<ExportUiRow> _uiRows = new List<ExportUiRow>();
            private readonly List<Item> _artItems = new List<Item>();
            /// <summary>What the archive ACTUALLY holds — the only thing `art/index` and
            /// `totals.artFiles` / `totals.thumbnails` are ever built from (spec §2a).</summary>
            private readonly List<Item> _writtenArt = new List<Item>();
            private readonly List<Item> _writtenThumbs = new List<Item>();
            private readonly List<string> _enabledScenePaths = new List<string>();
            private readonly List<string> _iconGuids = new List<string>();
            /// <summary>Every guid the Addressables group files list (spec §8.1 roots).</summary>
            private readonly HashSet<string> _addressableRoots = new HashSet<string>(StringComparer.Ordinal);
            /// <summary>Doc entry names already claimed, compared case-insensitively.</summary>
            private readonly HashSet<string> _docEntryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            /// <summary>Asset paths that ARE a symlink or junction, found by the one walk this
            /// export already makes over the assets root. Everything under one is LISTED (Unity
            /// imported it, so it has a guid, a path and a size) and nothing under one is READ
            /// (audit round 3, M2).</summary>
            private readonly List<string> _linkedAssetPaths = new List<string>();

            private readonly string _projectDir;
            private readonly string _docRoot;
            private readonly string _assetsRoot;

            private JObject _buildJson = new JObject();
            private JObject _identityJson = new JObject();
            private ExportArchiveWriter? _writer;
            private long _artBytes;
            private int _binarySerialized;
            private bool _fatal;

            internal Job(ExportRequest req, ExportResult result)
            {
                _req = req;
                _result = result;
                _caps = req.Caps ?? new ExportCaps();

                // The Unity project folder — the only correct base for an AssetDatabase path. NOT
                // req.ProjectRoot: that one names where docs/config live, so a test can point it at
                // a temp tree without every asset's byte count going missing.
                _projectDir = Path.GetDirectoryName(Application.dataPath) ?? Directory.GetCurrentDirectory();
                _docRoot = string.IsNullOrEmpty(req.ProjectRoot) ? _projectDir : req.ProjectRoot;

                var root = string.IsNullOrEmpty(req.AssetsRoot) ? "Assets" : req.AssetsRoot.Replace('\\', '/').TrimEnd('/');
                _assetsRoot = root.Length == 0 ? "Assets" : root;

                _result.Done = false;
                _result.FatalError = null;
                _result.Header = null;
                _result.Progress01 = 0f;
                _result.Sweeps = 0;
                _result.Phase = "start";
            }

            // ---- the drive ------------------------------------------------------------

            internal IEnumerable<int> Steps()
            {
                if (!OpenOutDir()) yield break;

                foreach (var t in ScanAssets()) yield return t;
                foreach (var t in ReadBuild()) yield return t;
                foreach (var t in ReadDependencies()) yield return t;
                foreach (var t in ReadInBuild()) yield return t;
                foreach (var t in ReadIdentity()) yield return t;
                foreach (var t in ReadPatterns()) yield return t;
                foreach (var t in ReadUi()) yield return t;
                foreach (var t in ReadDocs()) yield return t;
                foreach (var t in ReadStrings()) yield return t;
                foreach (var t in PlanArt()) yield return t;

                foreach (var t in WriteArchive()) yield return t;
                if (_fatal) yield break;

                if (!WriteHeaderFile()) yield break;

                _result.Header = _header;
                _result.Phase = "done";
                _result.Progress01 = 1f;
                _result.Done = true;
            }

            internal void Cleanup()
            {
                try { _writer?.Dispose(); } catch (Exception) { /* a half-written part is already lost */ }
                _writer = null;
            }

            // ---- phase 0: can we write at all? ----------------------------------------

            private bool OpenOutDir()
            {
                _result.Phase = "open";
                try
                {
                    if (string.IsNullOrEmpty(_req.OutDir)) { Fatal("OutDir is empty"); return false; }
                    Directory.CreateDirectory(_req.OutDir);
                    var probe = Path.Combine(_req.OutDir, ".writable");
                    File.WriteAllBytes(probe, new byte[] { 1 });
                    File.Delete(probe);
                    return true;
                }
                catch (Exception e)
                {
                    Fatal("cannot write to OutDir '" + _req.OutDir + "': " + e.Message);
                    return false;
                }
            }

            // ---- phase 1: inventory ---------------------------------------------------

            private IEnumerable<int> ScanAssets()
            {
                _result.Phase = "scan";
                var guids = Array.Empty<string>();
                if (!AssetDatabase.IsValidFolder(_assetsRoot))
                    Err(_assetsRoot + ": assets root folder not found");
                else
                    guids = SafeFindAssets("", _assetsRoot);

                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var g in guids)
                {
                    // FindAssets can hand the same guid back more than once.
                    if (string.IsNullOrEmpty(g) || !seen.Add(g)) continue;
                    var path = AssetDatabase.GUIDToAssetPath(g);
                    if (string.IsNullOrEmpty(path)) continue;
                    if (!UnderRoot(path)) continue;
                    if (AssetDatabase.IsValidFolder(path)) continue;   // spec §4: folders excluded
                    _rows.Add(new Row { Guid = g, Path = path });
                }
                // Deterministic output order (both exports of one project must match).
                _rows.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
                if (Tick()) yield return 0;

                for (var i = 0; i < _rows.Count; i++)
                {
                    var r = _rows[i];
                    FillRow(r);
                    _pathToGuid[r.Path] = r.Guid;
                    _guidToRow[r.Guid] = r;
                    Progress(0f, 0.20f, i + 1, _rows.Count);
                    if (Tick()) yield return 0;
                }

                _header.Totals.Assets = _rows.Count;
                foreach (var r in _rows)
                {
                    _header.Totals.AssetsByType.TryGetValue(r.Type, out var n);
                    _header.Totals.AssetsByType[r.Type] = n + 1;
                    if (r.Type == "SceneAsset") _header.Totals.Scenes++;
                }
                _header.Totals.DirsTopLevel = TopLevelDirs();
                NoteBoundedTotals();
                _result.Progress01 = 0.20f;
            }

            /// <summary>`assetsByType` and `dirsTopLevel` are bounded on the wire like every other
            /// listing (see <see cref="ExportTotals"/>). A bound that BITES is never silent — the
            /// per-type sum still equals `totals.assets` because the remainder rides in
            /// `"(other)"`, and this says how much went there.</summary>
            private void NoteBoundedTotals()
            {
                // THE FOLD ITSELF ANSWERS whether anything was folded — see
                // <see cref="ExportTotals.TypeFoldNote"/>. Counting raw type names here instead was
                // a row that could be false (audit round 4, M12).
                var typeNote = ExportTotals.TypeFoldNote(_header.Totals.AssetsByType);
                if (typeNote != null) Err(typeNote);
                var dirs = _header.Totals.DirsTopLevel.Count;
                if (dirs > ExportFormat.DirsTopLevelMax)
                    Err((dirs - ExportFormat.DirsTopLevelMax).ToString(CultureInfo.InvariantCulture) +
                        " of the " + dirs.ToString(CultureInfo.InvariantCulture) +
                        " top-level folders are past the listing cap and are not in totals.dirsTopLevel");
            }

            private void FillRow(Row r)
            {
                try
                {
                    // GetMainAssetTypeAtPath reads the importer's record — it does NOT load the
                    // asset, so no ScriptableObject constructor or message runs here (K6).
                    var t = AssetDatabase.GetMainAssetTypeAtPath(r.Path);
                    r.Type = t != null ? t.Name : "Unknown";
                    r.IsScriptableObject = t != null && typeof(ScriptableObject).IsAssignableFrom(t);
                }
                catch (Exception e)
                {
                    r.Type = "Unknown";
                    Err(r.Path + ": could not read type: " + e.Message);
                }

                try { r.Bytes = new FileInfo(Abs(r.Path)).Length; }
                catch (Exception e) { r.Bytes = 0; Err(r.Path + ": could not read size: " + e.Message); }
            }

            private List<string> TopLevelDirs()
            {
                var dirs = new List<string>();
                try
                {
                    var abs = Abs(_assetsRoot);
                    if (!Directory.Exists(abs)) return dirs;
                    foreach (var d in Directory.GetDirectories(abs))
                    {
                        var name = Path.GetFileName(d);
                        if (string.IsNullOrEmpty(name)) continue;
                        if (name[0] == '.' || name[name.Length - 1] == '~') continue;   // Unity ignores these
                        dirs.Add(_assetsRoot + "/" + name);
                    }
                }
                catch (Exception e) { Err(_assetsRoot + ": could not list folders: " + e.Message); }
                dirs.Sort(StringComparer.Ordinal);
                return dirs;
            }

            // ---- phase 2: build.json --------------------------------------------------

            private IEnumerable<int> ReadBuild()
            {
                _result.Phase = "build";
                var scenes = new JArray();
                try
                {
                    foreach (var s in EditorBuildSettings.scenes)
                    {
                        var p = s.path ?? "";
                        scenes.Add(new JObject
                        {
                            ["path"] = p,
                            ["guid"] = p.Length > 0 ? AssetDatabase.AssetPathToGUID(p) : "",
                            ["enabled"] = s.enabled,
                        });
                        if (s.enabled && p.Length > 0)
                        {
                            _enabledScenePaths.Add(p);
                            _header.Totals.BuildScenes++;
                        }
                    }
                }
                catch (Exception e) { Err("EditorBuildSettings: could not read scenes: " + e.Message); }
                if (Tick()) yield return 0;

                // Both of these walk a tree, so both are DRIVEN rather than called: they used to sit
                // inside the object initializer below as single blocking `AllDirectories` calls
                // (fresh-context audit K7).
                var resourcesDirs = new JArray();
                foreach (var t in ResourcesDirs(resourcesDirs)) yield return t;
                var streaming = new JObject();
                foreach (var t in StreamingAssets(streaming)) yield return t;
                var addressables = new JObject();
                foreach (var t in Addressables(addressables)) yield return t;

                _buildJson = new JObject
                {
                    ["activeBuildTarget"] = SafeStr(() => EditorUserBuildSettings.activeBuildTarget.ToString()),
                    ["scenes"] = scenes,
                    ["playerSettings"] = ReadPlayerSettings(),
                    ["resourcesDirs"] = resourcesDirs,
                    ["streamingAssets"] = streaming,
                    ["addressables"] = addressables,
                };
                _result.Progress01 = 0.25f;
                if (Tick()) yield return 0;
            }

            private JObject ReadPlayerSettings()
            {
                var icons = new JArray();
                // Spec §8.4: the default slot AND every platform's icons. Rogue Legend sets its icon
                // only under iPhone (`m_BuildTargetPlatformIcons`), so the default slot alone read
                // `iconGuids: []` and the export shipped no app icon (2026-09-23). Each read is its
                // own try: one platform module missing must not cost the others.
                var seen = new HashSet<string>(StringComparer.Ordinal);
                void AddTextures(IEnumerable<Texture2D>? textures)
                {
                    if (textures == null) return;
                    foreach (var t in textures)
                    {
                        if (t == null) continue;
                        var p = AssetDatabase.GetAssetPath(t);
                        if (string.IsNullOrEmpty(p)) continue;
                        var g = AssetDatabase.AssetPathToGUID(p);
                        if (string.IsNullOrEmpty(g) || !seen.Add(g)) continue;
                        _iconGuids.Add(g);
                    }
                }
                try { AddTextures(PlayerSettings.GetIcons(NamedBuildTarget.Unknown, IconKind.Any)); }
                catch (Exception e) { Err("PlayerSettings: could not read icons: " + e.Message); }
                foreach (var target in new[] { NamedBuildTarget.iOS, NamedBuildTarget.Android, NamedBuildTarget.Standalone, NamedBuildTarget.WebGL })
                {
                    try
                    {
                        AddTextures(PlayerSettings.GetIcons(target, IconKind.Any));
                        foreach (var kind in PlayerSettings.GetSupportedIconKinds(target) ?? Array.Empty<UnityEditor.PlatformIconKind>())
                            foreach (var icon in PlayerSettings.GetPlatformIcons(target, kind) ?? Array.Empty<UnityEditor.PlatformIcon>())
                                AddTextures(icon?.GetTextures());
                    }
                    catch (Exception e) { Err("PlayerSettings: could not read " + target.TargetName + " icons: " + e.Message); }
                }
                _iconGuids.Sort(StringComparer.Ordinal);
                foreach (var g in _iconGuids) icons.Add(g);

                return new JObject
                {
                    ["productName"] = SafeStr(() => PlayerSettings.productName),
                    ["companyName"] = SafeStr(() => PlayerSettings.companyName),
                    ["bundleVersion"] = SafeStr(() => PlayerSettings.bundleVersion),
                    ["applicationIdentifier"] = SafeStr(() => PlayerSettings.applicationIdentifier),
                    ["runInBackground"] = SafeBool(() => PlayerSettings.runInBackground),
                    ["defaultOrientation"] = SafeStr(() => PlayerSettings.defaultInterfaceOrientation.ToString()),
                    ["iconGuids"] = icons,
                };
            }

            /// <summary>
            /// …and, because it is the ONE walk that covers the whole assets root, the pass that
            /// learns which folders under `Assets/` are symlinks. It runs in phase 2, well before
            /// identity (5) and art (8), which is what lets those two skip them.
            /// </summary>
            private IEnumerable<int> ResourcesDirs(JArray into)
            {
                var found = new List<string>();
                var notes = new List<string>();
                var links = new List<string>();
                foreach (var item in ExportScan.Walk(Abs(_assetsRoot), AssetWalkLimits(), notes, links))
                {
                    if (item.IsDirectory &&
                        string.Equals(Path.GetFileName(item.FullPath), "Resources", StringComparison.Ordinal))
                    {
                        var rel = ExportScan.Relative(_projectDir, item.FullPath);
                        if (rel.Length > 0) found.Add(rel);
                    }
                    if (Tick()) yield return 0;
                }
                AddNotes(notes);
                NoteLinkedDirs(links);
                found.Sort(StringComparer.Ordinal);
                foreach (var f in found) into.Add(f);
            }

            /// <summary>
            /// NEVER SILENT, AND NEVER OVERSTATED. The walker refuses to follow a link, but Unity
            /// does follow one: a folder symlinked under `Assets/` is imported, so its textures,
            /// audio and fonts have guids and WERE being read and uploaded — bytes from outside the
            /// project, while TRUST.md promised otherwise (audit round 3, M2).
            ///
            /// One `errors` row per link, not per asset: the assets themselves are all in the
            /// inventory with their sizes, so nothing is lost, and a link holding 4,000 textures
            /// must not spend the 500-row listing budget.
            /// </summary>
            private void NoteLinkedDirs(List<string> links)
            {
                foreach (var rel in links)
                {
                    if (rel.Length == 0) continue;
                    var assetPath = _assetsRoot + "/" + rel;
                    _linkedAssetPaths.Add(assetPath);
                    Err(assetPath + ": a symlink or junction — it is listed, and nothing reached " +
                        "through it is read");
                }
            }

            /// <summary>
            /// Is this asset one Unity imported THROUGH a link? Its facts and its bytes are not
            /// ours to read.
            ///
            /// TWO THINGS IT CANNOT SEE, said rather than left implied. The match is ORDINAL: on a
            /// case-insensitive volume, a path whose case or Unicode normalisation differs between
            /// what `AssetDatabase` reports and what the directory walk read would not match, and
            /// would be read through. And a link is only known if the walk FOUND it — one deeper
            /// than the walker's depth limit, or past its entry limit, is never discovered at all.
            /// Both are edge cases; neither is guarded.
            /// </summary>
            private bool IsThroughLink(string assetPath)
            {
                for (var i = 0; i < _linkedAssetPaths.Count; i++)
                {
                    var link = _linkedAssetPaths[i];
                    if (string.Equals(assetPath, link, StringComparison.Ordinal)) return true;
                    if (assetPath.StartsWith(link + "/", StringComparison.Ordinal)) return true;
                }
                return false;
            }

            private IEnumerable<int> StreamingAssets(JObject into)
            {
                var dir = Abs(_assetsRoot + "/StreamingAssets");
                var present = false;
                try { present = Directory.Exists(dir); }
                catch (Exception e) { Err("StreamingAssets: could not read: " + e.Message); }

                var files = 0;
                if (present)
                {
                    var notes = new List<string>();
                    foreach (var item in ExportScan.Walk(dir, AssetWalkLimits(), notes))
                    {
                        if (!item.IsDirectory &&
                            !item.FullPath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) files++;
                        if (Tick()) yield return 0;
                    }
                    AddNotes(notes);
                }
                into["present"] = present;
                into["files"] = files;
            }

            /// <summary>
            /// Every `m_GUID:` in every Addressables group asset — through the SHARED WALKER, like
            /// every other tree read in the export, and with the same size cap the identity text
            /// read uses.
            ///
            /// It was the one pass that was still a raw `Directory.GetFiles` + `File.ReadAllText`:
            /// no reparse-point check, so a symlinked group file was followed straight out of the
            /// project, and no cap at all, so one enormous `.asset` went into editor memory whole
            /// (audit round 3, M1). The TOP-LEVEL-ONLY rule is kept exactly as it was —
            /// `AssetGroups/Schemas/*.asset` are schema objects, not groups.
            /// </summary>
            private IEnumerable<int> Addressables(JObject into)
            {
                var groups = new JArray();
                var dataDir = Abs(_assetsRoot + "/AddressableAssetsData");
                var present = false;
                try { present = Directory.Exists(dataDir); }
                catch (Exception e) { Err("Addressables: could not read groups: " + e.Message); }

                var groupDir = present ? Path.Combine(dataDir, "AssetGroups") : "";
                var haveGroupDir = false;
                if (present)
                {
                    try { haveGroupDir = Directory.Exists(groupDir); }
                    catch (Exception e) { Err("Addressables: could not read groups: " + e.Message); }
                }

                // …AND THE ROOT ITSELF. The walker refuses to FOLLOW a link and a linked group FILE
                // is caught below, but neither looks at the folder the walk STARTS in. A shared
                // `AddressableAssetsData` — one folder, two projects — is exactly that, and its
                // group files were read straight through it (audit round 4, M5). The same
                // `_linkedAssetPaths` the rest of the collector consults; it is filled in phase 2,
                // and this runs later.
                if (haveGroupDir)
                {
                    var linkedRoot = IsThroughLink(_assetsRoot + "/AddressableAssetsData")
                        ? _assetsRoot + "/AddressableAssetsData"
                        : IsThroughLink(_assetsRoot + "/AddressableAssetsData/AssetGroups")
                            ? _assetsRoot + "/AddressableAssetsData/AssetGroups"
                            : null;
                    // NO SECOND ROW: `NoteLinkedDirs` already wrote one for this exact path in
                    // phase 2 ("a symlink or junction — it is listed, and nothing reached through
                    // it is read"), and one row per link is the rule the 500-row listing budget
                    // rests on. `groups` comes back empty with `present: true`, which is the fact.
                    if (linkedRoot != null) haveGroupDir = false;
                }

                if (haveGroupDir)
                {
                    var files = new List<string>();
                    var notes = new List<string>();
                    var links = new List<string>();
                    foreach (var item in ExportScan.Walk(groupDir, AssetWalkLimits(), notes, links))
                    {
                        if (!item.IsDirectory &&
                            item.Relative.IndexOf('/') < 0 &&
                            item.FullPath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                            files.Add(item.FullPath);
                        if (Tick()) yield return 0;
                    }
                    AddNotes(notes);
                    foreach (var rel in links)
                        Err(ExportScan.Relative(_projectDir, groupDir) + "/" + rel +
                            ": a symlink or junction — the group was not read");

                    files.Sort(StringComparer.Ordinal);
                    foreach (var f in files)
                    {
                        var guids = new JArray();
                        foreach (var g in GroupGuids(f)) { guids.Add(g); _addressableRoots.Add(g); }
                        groups.Add(new JObject
                        {
                            ["file"] = ExportScan.Relative(_projectDir, f),
                            ["guids"] = guids,
                        });
                        if (Tick()) yield return 0;
                    }
                }

                into["present"] = present;
                into["groups"] = groups;
            }

            /// <summary>The guids in ONE group file, or none with the reason. A group past the
            /// text cap still gets its `groups` entry — an empty list with an `errors` row is a
            /// fact; a missing entry would read as a project with fewer groups.</summary>
            private List<string> GroupGuids(string file)
            {
                var label = ExportScan.Relative(_projectDir, file);
                if (label.Length == 0) label = Path.GetFileName(file);
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length > _caps.AssetTextMaxBytes)
                    {
                        Err(label + ": Addressables group not read: the file is " +
                            info.Length.ToString(CultureInfo.InvariantCulture) + " bytes");
                        return new List<string>();
                    }
                    return ExportScan.ReadAddressableGuids(File.ReadAllText(file));
                }
                catch (Exception e)
                {
                    Err(label + ": could not read group: " + e.Message);
                    return new List<string>();
                }
            }

            // ---- phase 3: the direct reference graph ----------------------------------

            private IEnumerable<int> ReadDependencies()
            {
                _result.Phase = "dependencies";
                for (var i = 0; i < _rows.Count; i++)
                {
                    var r = _rows[i];
                    var deps = DepsOf(r);
                    if (deps.Count > 0)
                    {
                        _depRows.Add(new KeyValuePair<string, List<string>>(r.Guid, deps));
                        foreach (var g in deps)
                        {
                            _refCount.TryGetValue(g, out var n);
                            _refCount[g] = n + 1;
                        }
                    }
                    Progress(0.25f, 0.45f, i + 1, _rows.Count);
                    if (Tick()) yield return 0;
                }
                _header.Totals.DependencyRows = _depRows.Count;
                _result.Progress01 = 0.45f;
            }

            private List<string> DepsOf(Row r)
            {
                var result = new List<string>();
                string[] deps;
                try { deps = AssetDatabase.GetDependencies(r.Path, false); }
                catch (Exception e) { Err(r.Path + ": could not read dependencies: " + e.Message); return result; }

                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var d in deps)
                {
                    if (string.IsNullOrEmpty(d) || string.Equals(d, r.Path, StringComparison.Ordinal)) continue;
                    // Only guids that resolve to an inventory row. GetDependencies also hands back
                    // `Resources/unity_builtin_extra`, `Library/unity default resources` and package
                    // shaders — guids the hosted side could never join to a row.
                    if (!_pathToGuid.TryGetValue(d, out var g)) continue;
                    if (seen.Add(g)) result.Add(g);
                }
                result.Sort(StringComparer.Ordinal);
                return result;
            }

            // ---- phase 4: inBuild ------------------------------------------------------

            private IEnumerable<int> ReadInBuild()
            {
                _result.Phase = "inBuild";
                var closure = new HashSet<string>(StringComparer.Ordinal);
                for (var i = 0; i < _enabledScenePaths.Count; i++)
                {
                    // One call PER SCENE: the union is identical to one call over all of them, and a
                    // single 139-scene call is a multi-second stall no slice can interrupt.
                    AddClosure(_enabledScenePaths[i], closure);
                    Progress(0.45f, 0.50f, i + 1, _enabledScenePaths.Count);
                    if (Tick()) yield return 0;
                }

                var streaming = _assetsRoot + "/StreamingAssets/";
                foreach (var r in _rows)
                {
                    r.InBuild = closure.Contains(r.Path)
                                || r.Path.IndexOf("/Resources/", StringComparison.Ordinal) >= 0
                                || r.Path.StartsWith(streaming, StringComparison.Ordinal);
                }

                // Spec §8.1 — what the game loads through Addressables. Walked over the DIRECT graph
                // phase 3 already recorded (the same rows the hosted side reads), so no new
                // AssetDatabase call and no asset load. Before this the art cap treated every
                // Addressable screen as unused: on Rogue Legend 719 of the game's own textures (407 MB)
                // were left out while 117 MB of art nothing reaches was sent (2026-09-23, measured).
                if (_addressableRoots.Count > 0)
                {
                    // A group entry can be a FOLDER: everything under it is addressable, and a folder
                    // has no dependency edges to walk (audit of 586f7ea8; RL has none, other games do).
                    foreach (var root in new List<string>(_addressableRoots))
                    {
                        string folder;
                        try { folder = AssetDatabase.GUIDToAssetPath(root); } catch (Exception) { continue; }
                        if (string.IsNullOrEmpty(folder) || !AssetDatabase.IsValidFolder(folder)) continue;
                        var prefix = folder.TrimEnd('/') + "/";
                        foreach (var r in _rows)
                            if (r.Path.StartsWith(prefix, StringComparison.Ordinal)) _addressableRoots.Add(r.Guid);
                    }
                    var graph = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                    foreach (var kv in _depRows) graph[kv.Key] = kv.Value;
                    foreach (var g in ExportCollector.AddressableClosure(_addressableRoots, graph))
                        if (_guidToRow.TryGetValue(g, out var row)) row.Addressable = true;
                }
                _result.Progress01 = 0.50f;
                if (Tick()) yield return 0;
            }


            private void AddClosure(string scenePath, HashSet<string> into)
            {
                try
                {
                    foreach (var p in AssetDatabase.GetDependencies(new[] { scenePath }, true)) into.Add(p);
                }
                catch (Exception e) { Err(scenePath + ": could not read build closure: " + e.Message); }
            }

            // ---- phase 5: identity.json ------------------------------------------------

            private IEnumerable<int> ReadIdentity()
            {
                _result.Phase = "identity";

                var fonts = new JArray();
                var fontRows = FontRows();
                for (var i = 0; i < fontRows.Count; i++)
                {
                    var r = fontRows[i].Key;
                    var kind = fontRows[i].Value;
                    _refCount.TryGetValue(r.Guid, out var refs);
                    var family = FamilyOf(r, kind);
                    fonts.Add(new JObject
                    {
                        ["guid"] = r.Guid,
                        ["path"] = r.Path,
                        ["type"] = kind,
                        ["familyName"] = family == null ? (JToken)JValue.CreateNull() : new JValue(family),
                        // How many assets list this font's guid among their DIRECT dependencies —
                        // the 722-vs-0 control. Free: it comes off the same dependency pass.
                        ["refCount"] = refs,
                    });
                    if (Tick()) yield return 0;
                }

                var colorObjects = new JArray();
                var audio = new JArray();
                var clipsLoaded = 0;
                var clipsSinceSweep = 0;
                for (var i = 0; i < _rows.Count; i++)
                {
                    var r = _rows[i];
                    if (r.IsScriptableObject)
                    {
                        var o = ColorsOf(r);
                        if (o != null) colorObjects.Add(o);
                    }
                    else if (r.Type == "AudioClip")
                    {
                        var o = AudioFactsOf(r);
                        if (o != null) audio.Add(o);
                        // K1 rightly removed the per-object `Resources.UnloadAsset` — it turned a
                        // texture the open scene was showing grey — and left the periodic sweep as
                        // the only thing that gives memory back. But the sweep only ever ran in the
                        // THUMBNAIL pass, and THIS loop loads every AudioClip in the project. On a
                        // game whose clips are Preload/Decompress-on-load that is the whole audio
                        // library resident in the studio's editor (audit round 3, N3).
                        clipsLoaded++;
                        if (ExportSweepPolicy.ShouldSweep(clipsSinceSweep + 1, 0, AudioSweepEvery, 0))
                        {
                            SweepUnusedAssets();
                            clipsSinceSweep = 0;
                        }
                        else
                        {
                            clipsSinceSweep++;
                        }
                    }
                    Progress(0.50f, 0.62f, i + 1, _rows.Count);
                    if (Tick()) yield return 0;
                }
                // …and once at the end, so a project with fewer than AudioSweepEvery clips is
                // bounded too. Only when something was actually loaded: a sweep costs time.
                if (clipsLoaded > 0) SweepUnusedAssets();

                // A project set to FORCE BINARY (or Mixed) serialisation has no YAML to read. That
                // is a fact, not an empty palette: without this the hosted side would learn a game
                // with no colours at all and nothing would say why.
                if (_binarySerialized > 0)
                    Err(_binarySerialized.ToString(CultureInfo.InvariantCulture) +
                        " asset(s) are binary-serialized; their colour and font-family facts were not read");

                var icons = new JArray();
                foreach (var g in _iconGuids)
                {
                    var path = SafeStr(() => AssetDatabase.GUIDToAssetPath(g));
                    icons.Add(new JObject { ["guid"] = g, ["path"] = path });
                }

                _identityJson = new JObject
                {
                    ["fonts"] = fonts,
                    ["colorObjects"] = colorObjects,
                    ["audio"] = audio,
                    ["icons"] = icons,
                };
                _result.Progress01 = 0.62f;
            }

            /// <summary>
            /// TMP font assets are found by type NAME. There is no `using TMPro` here and there
            /// never will be — the kit MUST compile in a project with no TextMeshPro
            /// (`UguiDriver.cs:46` records the day that broke a scratch install). The filter reads
            /// the importer's type record; it loads nothing.
            /// </summary>
            private List<KeyValuePair<Row, string>> FontRows()
            {
                var found = new List<KeyValuePair<Row, string>>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var filters = new[]
                {
                    new KeyValuePair<string, string>("t:TMP_FontAsset", "TMP_FontAsset"),
                    new KeyValuePair<string, string>("t:Font", "Font"),
                };
                foreach (var f in filters)
                {
                    foreach (var g in SafeFindAssets(f.Key, _assetsRoot))
                    {
                        if (string.IsNullOrEmpty(g) || !seen.Add(g)) continue;
                        if (_guidToRow.TryGetValue(g, out var row))
                            found.Add(new KeyValuePair<Row, string>(row, f.Value));
                    }
                }
                found.Sort((a, b) => string.CompareOrdinal(a.Key.Path, b.Key.Path));
                return found;
            }

            private string? FamilyOf(Row r, string kind)
            {
                if (IsThroughLink(r.Path)) return null;

                // A TMP_FontAsset IS a ScriptableObject, so loading it would run the studio's code
                // (K6). `m_FamilyName` sits inside `m_FaceInfo` in the asset's YAML — read there,
                // A LINE AT A TIME.
                //
                // It used to go through `ReadAssetText`, which refuses a file over
                // `AssetTextMaxBytes` (8 MiB) because the COLOUR reader needs the whole thing. A
                // real TMP font with a 2048-square atlas is 8.4-8.5 MB of hex, so exactly the fonts
                // a studio ships came back `familyName: null` while the toy ones worked (audit
                // round 3, N1). The cap is right for colours; it was never the family name's cap.
                if (string.Equals(kind, "TMP_FontAsset", StringComparison.Ordinal))
                    return FamilyFromYamlHead(r);

                // A plain `Font` is the .ttf/.otf itself: a binary engine asset with no user script
                // of any kind attached, so loading it executes nothing and there is no YAML to read
                // instead. `m_FontNames[0]` is only reachable through the imported object.
                try
                {
                    var obj = AssetDatabase.LoadMainAssetAtPath(r.Path);
                    if (obj == null) return null;
                    var so = new SerializedObject(obj);
                    try
                    {
                        var names = so.FindProperty("m_FontNames");
                        if (names != null && names.isArray && names.arraySize > 0)
                        {
                            var first = names.GetArrayElementAtIndex(0);
                            var v = first != null ? first.stringValue : null;
                            return string.IsNullOrEmpty(v) ? null : v;
                        }
                        return null;
                    }
                    finally { so.Dispose(); }
                }
                catch (Exception e)
                {
                    Err(r.Path + ": could not read font family: " + e.Message);
                    return null;
                }
            }

            /// <summary>
            /// `m_FaceInfo` / `m_FamilyName` STREAMED off the asset file, a line at a time, with
            /// at most a few KiB of any one line ever held (see
            /// <see cref="ExportScan.ReadYamlNestedValue(TextReader, string, string)"/>).
            ///
            /// There is no byte budget in front of it: a TMP font's atlas is usually written ABOVE
            /// the MonoBehaviour, so `m_FaceInfo` can sit past 8 MB of hex, and measured over the
            /// two real games on this machine a 256 KiB head budget nulled 22 of 48 real families.
            /// The scan is bounded by the file, which is linear and cheap (audit round 4, S1).
            ///
            /// A font whose `m_FamilyName` is empty is a null with NO `errors` row, because that is
            /// the truth about it. (TMP SPRITE assets also carry an empty `m_FaceInfo`, but they never
            /// reach this function — `FontRows` selects `t:TMP_FontAsset` and `t:Font` only; the "13
            /// of 13 empty" in the measurement are files the export never reads for a family.) Only a read that FAILED gets a row, and
            /// there is at most one: this is called once per font.
            /// </summary>
            private string? FamilyFromYamlHead(Row r)
            {
                try
                {
                    var abs = Abs(r.Path);
                    if (!File.Exists(abs)) return null;
                    using (var stream = new FileStream(abs, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                        return ExportScan.ReadYamlNestedValue(reader, "m_FaceInfo", "m_FamilyName");
                }
                catch (Exception e)
                {
                    Err(r.Path + ": could not read font family: " + e.Message);
                    return null;
                }
            }

            /// <summary>
            /// Colours off the asset's YAML TEXT — the asset is never loaded, so none of the
            /// studio's `Awake`/`OnEnable`/`OnValidate` runs (K6). `name` is the YAML key that holds
            /// the colour, under its parent keys; it used to be a `SerializedProperty.propertyPath,
            /// which only a loaded object can produce.
            /// </summary>
            private JObject? ColorsOf(Row r)
            {
                if (IsThroughLink(r.Path)) return null;
                var yaml = ReadAssetText(r);
                if (yaml == null) return null;
                var colors = new JArray();
                var notes = new List<string>();
                foreach (var kv in ExportScan.ReadYamlColors(yaml, ExportFormat.ColorsPerObjectMax, notes))
                    colors.Add(new JObject { ["name"] = kv.Key, ["hex"] = kv.Value });
                foreach (var n in notes) Err(r.Path + ": " + n);
                notes.Clear();
                if (colors.Count == 0) return null;
                return new JObject
                {
                    ["guid"] = r.Guid,
                    ["path"] = r.Path,
                    ["type"] = r.Type,
                    ["colors"] = colors,
                };
            }

            /// <summary>The asset's text if it IS text, else null — with the binary-serialisation
            /// case counted rather than swallowed.</summary>
            private string? ReadAssetText(Row r)
            {
                try
                {
                    var abs = Abs(r.Path);
                    var info = new FileInfo(abs);
                    if (!info.Exists) return null;
                    if (info.Length > _caps.AssetTextMaxBytes)
                    {
                        // COLOURS ONLY. The colour reader needs the whole document, so this cap
                        // stays — but it is not the font family's cap any more (N1), and saying
                        // "identity facts" made it sound as though it were.
                        Err(r.Path + ": colour facts not read: the asset is " +
                            info.Length.ToString(CultureInfo.InvariantCulture) +
                            " bytes, over the identity text cap");
                        return null;
                    }
                    var text = File.ReadAllText(abs);
                    if (!ExportScan.LooksLikeYaml(text)) { _binarySerialized++; return null; }
                    return text;
                }
                catch (Exception e)
                {
                    Err(r.Path + ": could not read the asset text: " + e.Message);
                    return null;
                }
            }

            /// <summary>An `AudioClip` is an engine type: importing and loading one runs no code of
            /// the studio's. Its length, channel count and sample rate are only on the object.</summary>
            private JObject? AudioFactsOf(Row r)
            {
                if (IsThroughLink(r.Path)) return null;
                try
                {
                    var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(r.Path);
                    if (clip == null) return null;
                    return new JObject
                    {
                        ["guid"] = r.Guid,
                        ["path"] = r.Path,
                        ["lengthSec"] = Math.Round((double)clip.length, 3),
                        ["channels"] = clip.channels,
                        ["frequency"] = clip.frequency,
                    };
                }
                catch (Exception e)
                {
                    Err(r.Path + ": could not read audio facts: " + e.Message);
                    return null;
                }
            }

            // ---- phase 6: the .cs pattern scan ----------------------------------------

            private IEnumerable<int> ReadPatterns()
            {
                _result.Phase = "patterns";
                var scanRoot = string.IsNullOrEmpty(_req.PatternScanRoot) ? Abs(_assetsRoot) : _req.PatternScanRoot!;

                var files = new List<string>();
                var walkNotes = new List<string>();
                foreach (var item in ExportScan.Walk(scanRoot, AssetWalkLimits(), walkNotes))
                {
                    if (!item.IsDirectory && item.FullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                        files.Add(item.FullPath);
                    if (Tick()) yield return 0;
                }
                AddNotes(walkNotes);
                files.Sort(StringComparer.Ordinal);

                string? stoppedIn = null;
                var notScanned = 0;
                for (var i = 0; i < files.Count; i++)
                {
                    var label = LabelOf(files[i], scanRoot);
                    if (_hits.Count >= _caps.PatternHitsMax)
                    {
                        stoppedIn = label;
                        notScanned = files.Count - i;
                        break;
                    }
                    if (ScanOne(files[i], label))
                    {
                        // The cap landed INSIDE this file. The old code only noticed at the TOP of
                        // the next iteration, so a cap spent in the LAST file was completely silent
                        // — `patternHits=3, errors=[]` on the auditor's probe (K9).
                        stoppedIn = label;
                        notScanned = files.Count - i - 1;
                        break;
                    }
                    Progress(0.62f, 0.70f, i + 1, files.Count);
                    if (Tick()) yield return 0;
                }
                if (stoppedIn != null)
                    Err("pattern scan stopped at patternHitsMax (" +
                        _caps.PatternHitsMax.ToString(CultureInfo.InvariantCulture) + ") inside " + stoppedIn +
                        "; that file was not finished and " +
                        notScanned.ToString(CultureInfo.InvariantCulture) +
                        " further .cs file(s) were not scanned");

                _header.Totals.PatternHits = _hits.Count;
                _result.Progress01 = 0.70f;
            }

            private string LabelOf(string file, string scanRoot)
            {
                var label = ExportScan.Relative(_projectDir, file);
                if (label.Length == 0) label = ExportScan.Relative(scanRoot, file);
                if (label.Length == 0) label = Path.GetFileName(file);
                return label;
            }

            /// <summary>True when `patternHitsMax` cut this file short.</summary>
            private bool ScanOne(string file, string label)
            {
                try
                {
                    var length = new FileInfo(file).Length;
                    if (length > _caps.PatternFileMaxBytes)
                    {
                        Err(label + ": not scanned: over patternFileMaxBytes (" +
                            length.ToString(CultureInfo.InvariantCulture) + " bytes)");
                        return false;
                    }
                    var notes = new List<string>();
                    var hits = ExportScan.ScanText(label, File.ReadAllText(file), notes);
                    AddNotes(notes);
                    foreach (var h in hits)
                    {
                        if (_hits.Count >= _caps.PatternHitsMax) return true;
                        _hits.Add(h);
                    }
                    return false;
                }
                catch (Exception e) { Err(label + ": could not scan: " + e.Message); return false; }
            }

            // ---- phase 6b: clickable UI (spec §8.7) ------------------------------------
            //
            // The YAML of every prefab and ENABLED build scene, read as TEXT (never loaded). The page
            // itself told studios shots start "without a path" because the export held no button
            // names; this is those names, their parents, their visible label and what they call.

            private IEnumerable<int> ReadUi()
            {
                _result.Phase = "ui";
                var files = new List<string>();
                foreach (var r in _rows)
                    if (r.Path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) files.Add(r.Path);
                foreach (var sc in _enabledScenePaths) files.Add(sc);
                files.Sort(StringComparer.Ordinal);
                var scriptNames = new Dictionary<string, string?>(StringComparer.Ordinal);
                string? ScriptName(string guid)
                {
                    if (scriptNames.TryGetValue(guid, out var n)) return n;
                    string? name = null;
                    try
                    {
                        var p = AssetDatabase.GUIDToAssetPath(guid);
                        if (!string.IsNullOrEmpty(p)) name = Path.GetFileNameWithoutExtension(p);
                    }
                    catch (Exception) { }
                    scriptNames[guid] = name;
                    return name;
                }
                var capped = false;
                var notText = 0;
                for (var i = 0; i < files.Count && !capped; i++)
                {
                    var rel = files[i];
                    if (IsThroughLink(rel)) continue;
                    try
                    {
                        var abs = Abs(rel);
                        var length = new FileInfo(abs).Length;
                        if (length > _caps.AssetTextMaxBytes)
                        {
                            Err(rel + ": UI elements not read: over assetTextMaxBytes (" + length.ToString(CultureInfo.InvariantCulture) + " bytes)");
                        }
                        else
                        {
                            var notes = new List<string>();
                            foreach (var row in ExportUiScan.Parse(rel, File.ReadAllText(abs), ScriptName, notes))
                            {
                                if (_uiRows.Count >= _caps.UiRowsMax)
                                {
                                    Err("UI scan stopped at uiRowsMax (" + _caps.UiRowsMax.ToString(CultureInfo.InvariantCulture) +
                                        ") inside " + rel + "; " + (files.Count - i - 1).ToString(CultureInfo.InvariantCulture) + " further file(s) not read");
                                    capped = true;
                                    break;
                                }
                                _uiRows.Add(row);
                            }
                            // one "not text-serialized" line per project is the fact; thousands are noise
                            notText += notes.Count;
                        }
                    }
                    catch (Exception e) { Err(rel + ": could not read UI elements: " + e.Message); }
                    Progress(0.70f, 0.71f, i + 1, files.Count);
                    if (Tick()) yield return 0;
                }
                if (notText > 0)
                    Err(notText.ToString(CultureInfo.InvariantCulture) +
                        " prefab/scene file(s) are not text-serialized; their UI elements were not read (Project Settings › Editor › Asset Serialization › Force Text)");
            }

            // ---- phase 7b: the game's own words (spec §8.6) ---------------------------------

            private IEnumerable<int> ReadStrings()
            {
                _result.Phase = "strings";
                var found = new List<string>();
                var walkNotes = new List<string>();
                foreach (var item in ExportScan.Walk(Abs(_assetsRoot), AssetWalkLimits(), walkNotes))
                {
                    if (!item.IsDirectory)
                    {
                        var rel = _assetsRoot + "/" + item.Relative;
                        if (ExportScan.IsStringsFile(rel)) found.Add(rel);
                    }
                    if (Tick()) yield return 0;
                }
                AddNotes(walkNotes);
                found.Sort(StringComparer.Ordinal);
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var rel in found)
                {
                    if (IsThroughLink(rel)) continue;
                    var abs = Abs(rel);
                    long length;
                    try { length = new FileInfo(abs).Length; }
                    catch (Exception e) { Err(rel + ": could not read size: " + e.Message); continue; }
                    if (length > _caps.StringsFileMaxBytes) { Over(rel, length, ExportFormat.KindStrings, ExportFormat.ReasonOverStringsFile); continue; }
                    if (_stringItems.Count >= _caps.StringsMaxFiles) { Over(rel, length, ExportFormat.KindStrings, ExportFormat.ReasonOverStringsCount); continue; }
                    var entry = ExportFormat.UniqueEntryName(ExportFormat.StringsPrefix + ExportFormat.ToEntryPath(rel), names);
                    if (!ExportFormat.IsSafeEntryName(entry)) { Err(rel + ": unsafe entry name, not exported"); continue; }
                    _stringItems.Add(new Item
                    {
                        Path = rel,
                        Entry = entry,
                        Bytes = length,
                        Source = abs,
                        OverKind = ExportFormat.KindStrings,
                        MaxBytes = _caps.StringsFileMaxBytes,
                        OverReason = ExportFormat.ReasonOverStringsFile,
                    });
                    if (Tick()) yield return 0;
                }
                _result.Progress01 = 0.73f;
            }

            // ---- phase 7: docs/ and config/ -------------------------------------------

            private IEnumerable<int> ReadDocs()
            {
                _result.Phase = "docs";
                var notes = new List<string>();

                // A NULL IS "STILL WALKING" (audit round 3, N2). These two iterators used to yield
                // MATCHES only, so the `Tick` below could not fire during the walk itself — the
                // whole sweep of a studio's tree ran between two slices, 933 ms and 1,858 ms
                // measured on two real projects, with the editor's main thread inside it.
                var docs = new List<string>();
                foreach (var rel in ExportScan.ScanDocFiles(_docRoot, ExportScan.DocWalkLimits(), notes))
                {
                    if (rel != null) docs.Add(rel);
                    if (Tick()) yield return 0;
                }
                docs.Sort(StringComparer.Ordinal);

                var configs = new List<string>();
                foreach (var rel in ExportScan.ScanConfigFiles(_docRoot, ExportScan.ConfigWalkLimits(), notes))
                {
                    if (rel != null) configs.Add(rel);
                    if (Tick()) yield return 0;
                }
                configs.Sort(StringComparer.Ordinal);
                AddNotes(notes);

                foreach (var rel in docs)
                {
                    ConsiderDoc(rel, ExportFormat.DocsPrefix, ExportFormat.KindDoc);
                    if (Tick()) yield return 0;
                }
                foreach (var rel in configs)
                {
                    ConsiderDoc(rel, ExportFormat.ConfigPrefix, ExportFormat.KindConfig);
                    if (Tick()) yield return 0;
                }
                _result.Progress01 = 0.72f;
            }

            private void ConsiderDoc(string rel, string prefix, string kind)
            {
                var abs = Path.Combine(_docRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                long length;
                try { length = new FileInfo(abs).Length; }
                catch (Exception e) { Err(rel + ": could not read size: " + e.Message); return; }

                // spec §8.3: config is text with its own, larger cap
                var isConfig = kind == ExportFormat.KindConfig;
                var fileMax = isConfig ? _caps.ConfigFileMaxBytes : _caps.DocFileMaxBytes;
                var overReason = isConfig ? ExportFormat.ReasonOverConfigFile : ExportFormat.ReasonOverDocFile;
                if (length > fileMax) { Over(rel, length, kind, overReason); return; }
                if (isConfig && ++_configCount > _caps.ConfigMaxFiles) { Over(rel, length, kind, ExportFormat.ReasonOverConfigCount); return; }
                if (_docItems.Count >= _caps.DocMaxFiles) { Over(rel, length, kind, ExportFormat.ReasonOverDocCount); return; }

                var entry = prefix + ExportFormat.ToEntryPath(rel);
                if (!ExportFormat.IsSafeEntryName(entry)) { Err(rel + ": unsafe entry name, not exported"); return; }
                // `docs/a.md` and `Docs/a.md` differ ordinally, but a reader that folds case would
                // see one name twice. The second is renamed rather than DROPPED, and the rename is
                // said out loud (spec §6).
                var unique = ExportFormat.UniqueEntryName(entry, _docEntryNames);
                if (!string.Equals(unique, entry, StringComparison.Ordinal))
                    Err(rel + ": '" + entry + "' is already taken but for case; exported as '" + unique + "'");
                _docItems.Add(new Item
                {
                    Path = rel,
                    Entry = unique,
                    Bytes = length,
                    Source = abs,
                    OverKind = kind,
                    MaxBytes = fileMax,
                    OverReason = overReason,
                });
            }

            // ---- phase 8: which art originals ship ------------------------------------

            private IEnumerable<int> PlanArt()
            {
                _result.Phase = "art";
                var claimed = new HashSet<string>(StringComparer.Ordinal);
                // spec §3: icons, then fonts, then audio, then texture originals —
                // each group in-build first, then by path.
                var groups = new List<KeyValuePair<string, List<Row>>>
                {
                    new KeyValuePair<string, List<Row>>(ExportFormat.KindIcon, IconRows()),
                    new KeyValuePair<string, List<Row>>(ExportFormat.KindFont, FontOnlyRows()),
                    new KeyValuePair<string, List<Row>>(ExportFormat.KindAudio, RowsOfType("AudioClip")),
                    new KeyValuePair<string, List<Row>>(ExportFormat.KindTexture, RowsOfType("Texture2D")),
                };

                foreach (var group in groups)
                {
                    var list = group.Value;
                    list.Sort(CompareInBuildThenPath);
                    foreach (var r in list)
                    {
                        if (!claimed.Add(r.Guid)) continue;
                        ConsiderArt(r, group.Key);
                        if (Tick()) yield return 0;
                    }
                }
                // NOTE what is NOT set here: `totals.artFiles`. These are the files the caps ALLOW;
                // what is COUNTED is what the writer actually got into a part (spec §2a).
                _result.Progress01 = 0.88f;
            }

            private void ConsiderArt(Row r, string kind)
            {
                if (IsThroughLink(r.Path)) return;   // listed, never read (M2)
                var abs = Abs(r.Path);
                long length;
                try
                {
                    if (!File.Exists(abs)) { Err(r.Path + ": file missing, not exported"); return; }
                    length = new FileInfo(abs).Length;
                }
                catch (Exception e) { Err(r.Path + ": could not read size: " + e.Message); return; }

                // spec §8.3: one music track may be larger than one texture
                var isAudio = kind == ExportFormat.KindAudio;
                var fileMax = isAudio ? _caps.AudioFileMaxBytes : _caps.ArtFileMaxBytes;
                var overReason = isAudio ? ExportFormat.ReasonOverAudioFile : ExportFormat.ReasonOverArtFile;
                if (length > fileMax) { Over(r.Path, length, kind, overReason); return; }
                if (_artBytes + length > _caps.ArtTotalMaxBytes) { Over(r.Path, length, kind, ExportFormat.ReasonOverArtTotal); return; }

                var entry = ExportFormat.ArtFileEntry(r.Guid, Path.GetExtension(r.Path));
                if (!ExportFormat.IsSafeEntryName(entry)) { Err(r.Path + ": unsafe entry name, not exported"); return; }
                _artBytes += length;
                _artItems.Add(new Item
                {
                    Guid = r.Guid,
                    Path = r.Path,
                    Entry = entry,
                    IndexKind = "file",
                    Bytes = length,
                    Source = abs,
                    OverKind = kind,
                    MaxBytes = fileMax,
                    OverReason = overReason,
                });
            }

            private List<Row> IconRows()
            {
                var list = new List<Row>();
                foreach (var g in _iconGuids)
                    if (_guidToRow.TryGetValue(g, out var r)) list.Add(r);
                return list;
            }

            private List<Row> FontOnlyRows()
            {
                var list = new List<Row>();
                foreach (var kv in FontRows()) list.Add(kv.Key);
                return list;
            }

            private List<Row> RowsOfType(string type)
            {
                var list = new List<Row>();
                foreach (var r in _rows)
                    if (string.Equals(r.Type, type, StringComparison.Ordinal)) list.Add(r);
                return list;
            }

            /// <summary>Spec §8.2: used by the game (in-build OR Addressable) first, then by path.</summary>
            private static int CompareInBuildThenPath(Row a, Row b)
            {
                if (a.Used != b.Used) return a.Used ? -1 : 1;
                return string.CompareOrdinal(a.Path, b.Path);
            }

            // ---- phase 9: write the parts ---------------------------------------------
            //
            // ENTRY ORDER (spec §3). The art INDEX is written after the art it indexes, and that is
            // the point: `totals.artFiles`, `totals.thumbnails`, `totals.docs` and every art index
            // row are built from what the writer REALLY got into a part. When the index went first
            // it described the plan, so one file locked or deleted mid-export left the index and
            // the totals one ahead of the zips — and the server fails the WHOLE export on a count
            // difference (fresh-context audit K4). The hosted reconcile opens every part, so it does
            // not care where in the export the facts land.

            private IEnumerable<int> WriteArchive()
            {
                _result.Phase = "write";
                if (!OpenWriter()) yield break;

                if (!AddJson(ExportFormat.BuildEntry, _buildJson)) yield break;
                if (!AddJson(ExportFormat.IdentityEntry, _identityJson)) yield break;

                foreach (var t in WriteJsonl(ExportFormat.InventoryBase, _header.Inventory, InventoryRows())) yield return t;
                if (_fatal) yield break;
                foreach (var t in WriteJsonl(ExportFormat.DependenciesBase, _header.Dependencies, DependencyRows())) yield return t;
                if (_fatal) yield break;
                foreach (var t in WriteJsonl(ExportFormat.PatternsBase, _header.Patterns, PatternRows())) yield return t;
                if (_fatal) yield break;
                foreach (var t in WriteJsonl(ExportFormat.UiBase, _header.Ui, UiRows())) yield return t;
                if (_fatal) yield break;
                _header.Totals.UiElements = _header.Ui.Rows;

                // 0.88 → 0.95 is the files; 0.95 → 0.99 is the thumbnails, which report their own.
                var filesWritten = 0;
                var filesTotal = _docItems.Count + _stringItems.Count + _artItems.Count;

                var docsWritten = 0;
                foreach (var d in _docItems)
                {
                    if (AddFileEntry(d)) docsWritten++;
                    if (_fatal) yield break;
                    Progress(0.88f, 0.95f, ++filesWritten, filesTotal);
                    if (Tick()) yield return 0;
                }
                _header.Totals.Docs = docsWritten;

                var stringsWritten = 0;
                foreach (var d in _stringItems)
                {
                    if (AddFileEntry(d)) stringsWritten++;
                    if (_fatal) yield break;
                    Progress(0.88f, 0.95f, ++filesWritten, filesTotal);
                    if (Tick()) yield return 0;
                }
                _header.Totals.Strings = stringsWritten;

                foreach (var a in _artItems)
                {
                    if (AddFileEntry(a)) _writtenArt.Add(a);
                    if (_fatal) yield break;
                    Progress(0.88f, 0.95f, ++filesWritten, filesTotal);
                    if (Tick()) yield return 0;
                }
                _header.Totals.ArtFiles = _writtenArt.Count;

                foreach (var t in WriteThumbs()) yield return t;
                if (_fatal) yield break;

                foreach (var t in WriteJsonl(ExportFormat.ArtIndexBase, _header.ArtIndex, ArtIndexRows())) yield return t;
                if (_fatal) yield break;

                if (!FinishParts()) yield break;
                _result.Progress01 = 0.99f;
            }

            /// <summary>
            /// A 256-px PNG of every texture, rendered and put STRAIGHT into the zip.
            ///
            /// It used to be staged under `OutDir/.tmp/thumbs` and copied in afterwards, because
            /// `art/index` (which carries each thumb's byte count) had to be written BEFORE the
            /// thumb entries. The index now goes last, so the staging copy is gone with it — and so
            /// is the second copy of every thumbnail that sat on the studio's disk until cleanup.
            /// </summary>
            private IEnumerable<int> WriteThumbs()
            {
                var textures = RowsOfType("Texture2D");
                textures.Sort(CompareInBuildThenPath);

                var dropped = 0;
                var sinceSweep = 0;
                var bytesSinceSweep = 0L;
                var madeOne = false;
                for (var i = 0; i < textures.Count; i++)
                {
                    if (_writtenThumbs.Count >= _caps.ThumbnailMaxCount) { dropped = textures.Count - i; break; }
                    if (IsThroughLink(textures[i].Path)) continue;   // listed, never read (M2)
                    long sourceBytes;
                    var png = ThumbPng(textures[i], out sourceBytes);
                    if (png != null) AddThumbEntry(textures[i], png);
                    if (_fatal) yield break;
                    madeOne = true;

                    // MEASURED 2026-09-21: without a sweep, peak editor memory rises ~0.15 MB per
                    // thumbnail and never comes back down — 1,200 thumbnails cost 175 MB, while the
                    // same export with thumbnails off costs 1.5 MB. The allocator only gives the
                    // blocks back on a sweep, and the sweep releases ONLY what nothing holds, so a
                    // texture the studio's open scene is showing is untouched by it.
                    //
                    // COUNT **OR** BYTES (audit round 3, N4). ~0.15 MB each was measured on 16x16
                    // fixtures; a shipped game's textures are 2048 square, so 256 of them between
                    // sweeps is several GB and "every 256" bounds nothing. The byte figure is
                    // Unity's own estimate for each source texture, and the count trigger stays
                    // because an estimate that reads back 0 would never fire on its own.
                    sinceSweep++;
                    bytesSinceSweep += sourceBytes;
                    if (ExportSweepPolicy.ShouldSweep(sinceSweep, bytesSinceSweep,
                            ThumbnailSweepEvery, _caps.ThumbnailSweepBytes))
                    {
                        SweepUnusedAssets();
                        sinceSweep = 0;
                        bytesSinceSweep = 0;
                    }
                    Progress(0.95f, 0.99f, i + 1, textures.Count);
                    if (Tick()) yield return 0;
                }
                if (madeOne) SweepUnusedAssets();
                if (dropped > 0)
                    Err("thumbnails stopped at thumbnailMaxCount (" +
                        _caps.ThumbnailMaxCount.ToString(CultureInfo.InvariantCulture) + "); " +
                        dropped.ToString(CultureInfo.InvariantCulture) + " textures have no thumbnail");

                _header.Totals.Thumbnails = _writtenThumbs.Count;
            }

            /// <summary>
            /// Works on a NON-READABLE texture, which most shipped art is: blit to a temporary
            /// RenderTexture and read back. Nothing here changes an importer setting — that would
            /// dirty the studio's project to take a picture of it — and nothing unloads the source:
            /// the sweep above is what bounds the memory.
            /// </summary>
            /// <param name="sourceBytes">Unity's own estimate of what LOADING the source texture
            /// cost, which is what the byte-budget sweep counts. 0 when it could not be loaded or
            /// the profiler could not size it — the count trigger covers that case.</param>
            private byte[]? ThumbPng(Row r, out long sourceBytes)
            {
                sourceBytes = 0;
                Texture2D? readable = null;
                RenderTexture? rt = null;
                var previous = RenderTexture.active;
                try
                {
                    // A Texture2D is an engine type; loading one runs no code of the studio's.
                    var source = AssetDatabase.LoadAssetAtPath<Texture2D>(r.Path);
                    if (source == null) { Err(r.Path + ": could not load texture for a thumbnail"); return null; }
                    try { sourceBytes = UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(source); }
                    catch (Exception) { sourceBytes = 0; }

                    var w = Mathf.Max(1, source.width);
                    var h = Mathf.Max(1, source.height);
                    var scale = Mathf.Min(1f, _caps.ThumbnailPx / (float)Mathf.Max(w, h));
                    var tw = Mathf.Max(1, Mathf.RoundToInt(w * scale));
                    var th = Mathf.Max(1, Mathf.RoundToInt(h * scale));

                    rt = RenderTexture.GetTemporary(tw, th, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                    Graphics.Blit(source, rt);
                    RenderTexture.active = rt;
                    readable = new Texture2D(tw, th, TextureFormat.RGBA32, false);
                    readable.ReadPixels(new Rect(0, 0, tw, th), 0, 0, false);
                    readable.Apply(false, false);

                    var png = readable.EncodeToPNG();
                    if (png == null || png.Length == 0) { Err(r.Path + ": thumbnail encoded to nothing"); return null; }
                    return png;
                }
                catch (Exception e) { Err(r.Path + ": could not make a thumbnail: " + e.Message); return null; }
                finally
                {
                    RenderTexture.active = previous;
                    if (rt != null) RenderTexture.ReleaseTemporary(rt);
                    // The READBACK target is ours, created here. Destroying it is not a mutation of
                    // anything the studio owns.
                    if (readable != null) UnityEngine.Object.DestroyImmediate(readable);
                }
            }

            private void AddThumbEntry(Row r, byte[] png)
            {
                var entry = ExportFormat.ArtThumbEntry(r.Guid);
                if (!ExportFormat.IsSafeEntryName(entry)) { Err(r.Path + ": unsafe thumbnail entry name"); return; }
                try { _writer!.AddBytes(entry, png); }
                catch (ExportArchiveFatalException e) { Fatal("the export archive failed: " + e.Message); return; }
                catch (Exception e) { Err(r.Path + ": could not add '" + entry + "': " + e.Message); return; }
                _writtenThumbs.Add(new Item
                {
                    Guid = r.Guid,
                    Path = r.Path,
                    Entry = entry,
                    IndexKind = "thumb",
                    Bytes = png.LongLength,
                });
            }

            private bool OpenWriter()
            {
                try { _writer = new ExportArchiveWriter(_req.OutDir, _caps); return true; }
                catch (Exception e) { Fatal("cannot open the export archive in '" + _req.OutDir + "': " + e.Message); return false; }
            }

            private bool AddJson(string entry, JObject body)
            {
                try
                {
                    var bytes = new UTF8Encoding(false).GetBytes(body.ToString(Newtonsoft.Json.Formatting.Indented));
                    _writer!.AddBytes(entry, bytes);
                    return true;
                }
                catch (Exception e) { Fatal("cannot write '" + entry + "': " + e.Message); return false; }
            }

            private IEnumerable<int> WriteJsonl(string baseName, ExportFileSet set, IEnumerable<JObject> rows)
            {
                ExportJsonlWriter? w = null;
                try { w = _writer!.OpenJsonl(baseName); }
                catch (Exception e) { Fatal("cannot open '" + baseName + "': " + e.Message); }
                if (w == null) yield break;

                foreach (var row in rows)
                {
                    if (!TryWriteRow(w, baseName, row)) yield break;
                    if (Tick()) yield return 0;
                }

                try { w.Dispose(); }
                catch (Exception e) { Fatal("cannot close '" + baseName + "': " + e.Message); yield break; }

                set.Shards = new List<string>(w.Shards);
                set.Rows = w.Rows;
            }

            private bool TryWriteRow(ExportJsonlWriter w, string baseName, JObject row)
            {
                try { w.WriteRow(row); return true; }
                catch (Exception e) { Fatal("cannot write a row of '" + baseName + "': " + e.Message); return false; }
            }

            /// <summary>
            /// True when the entry really went into a part.
            ///
            /// A source file that vanished mid-export is a FACT, not a fatality — the writer is
            /// atomic per entry, so nothing half-written is left behind and the caller simply does
            /// not count it. An <see cref="ExportArchiveFatalException"/> is the other case
            /// entirely: the ARCHIVE is broken (a part that cannot be closed leaves a gap in the
            /// part indexes) and the hosted side refuses the whole export for it, so it must not be
            /// downgraded to an `errors` row.
            /// </summary>
            private bool AddFileEntry(Item item)
            {
                // THE SIZE IS CHECKED AGAIN, HERE. The plan stats every candidate; the write then
                // re-read the file with no second look, so a file that GREW in between (a Photoshop
                // save in flight, a texture re-exported at 4K) went into a part at its new size.
                // Every single entry being ≤ artFileMaxBytes is the whole reason no part can pass
                // the 32 MiB ceiling the server refuses — and the index reported the PLANNED byte
                // count either way (audit round 3, M6).
                if (item.MaxBytes > 0)
                {
                    long now;
                    try { now = new FileInfo(item.Source).Length; }
                    catch (Exception e) { Err(item.Path + ": could not re-read size: " + e.Message); return false; }
                    if (now > item.MaxBytes)
                    {
                        Over(item.Path, now, item.OverKind, item.OverReason);
                        return false;
                    }
                }

                long written;
                // …AND THE CAP GOES WITH IT. The re-stat above narrows the window to microseconds
                // but does not close it; only the bytes that actually came back are the last word
                // on how big the file was (audit round 4, M4).
                try { written = _writer!.AddFile(item.Entry, item.Source, item.MaxBytes); }
                catch (ExportArchiveFatalException e)
                {
                    Fatal("the export archive failed: " + e.Message);
                    return false;
                }
                catch (Exception e)
                {
                    Err(item.Path + ": could not add '" + item.Entry + "': " + e.Message);
                    return false;
                }
                // …and the index row carries what was WRITTEN, not what was planned (spec §2a).
                item.Bytes = written;
                return true;
            }

            private bool FinishParts()
            {
                try
                {
                    var parts = _writer!.Finish();
                    _header.Parts = new List<ExportPartInfo>(parts);
                    return true;
                }
                catch (Exception e) { Fatal("cannot close the last part: " + e.Message); return false; }
            }

            private IEnumerable<JObject> InventoryRows()
            {
                foreach (var r in _rows)
                {
                    var o = new JObject
                    {
                        ["guid"] = r.Guid,
                        ["path"] = r.Path,
                        ["type"] = r.Type,
                    };
                    // `base` is a TYPE-HIERARCHY fact, present only when it holds — absent, not false.
                    if (r.IsScriptableObject) o["base"] = "ScriptableObject";
                    o["bytes"] = r.Bytes;
                    o["inBuild"] = r.InBuild;
                    // spec §8.1 — present only when it holds, like `base`
                    if (r.Addressable) o["addressable"] = true;
                    yield return o;
                }
            }

            private IEnumerable<JObject> DependencyRows()
            {
                foreach (var kv in _depRows)
                {
                    var deps = new JArray();
                    foreach (var g in kv.Value) deps.Add(g);
                    yield return new JObject { ["guid"] = kv.Key, ["deps"] = deps };
                }
            }

            private IEnumerable<JObject> UiRows()
            {
                foreach (var r in _uiRows) yield return r.ToJson();
            }

            private IEnumerable<JObject> PatternRows()
            {
                foreach (var h in _hits) yield return h.ToJson();
            }

            private IEnumerable<JObject> ArtIndexRows()
            {
                foreach (var a in _writtenArt) yield return IndexRow(a);
                foreach (var a in _writtenThumbs) yield return IndexRow(a);
            }

            private static JObject IndexRow(Item a) => new JObject
            {
                ["guid"] = a.Guid,
                ["path"] = a.Path,
                ["entry"] = a.Entry,
                ["kind"] = a.IndexKind,
                ["bytes"] = a.Bytes,
            };

            // ---- phase 10: header.json --------------------------------------------------

            private bool WriteHeaderFile()
            {
                _result.Phase = "header";
                _header.SchemaVersion = ExportFormat.SchemaVersion;
                _header.KitVersion = _req.KitVersion ?? "";
                _header.UnityVersion = SafeStr(() => Application.unityVersion);
                _header.ExportedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                _header.WorkspaceId = _req.WorkspaceId ?? "";
                _header.EditorGameId = _req.EditorGameId;
                _header.AdapterJsonPresent = _req.AdapterJsonPresent;
                _header.ProductName = SafeStr(() => PlayerSettings.productName);
                _header.CompanyName = SafeStr(() => PlayerSettings.companyName);
                _header.Caps = _caps;

                try
                {
                    File.WriteAllText(Path.Combine(_req.OutDir, "header.json"),
                        _header.ToJsonString(), new UTF8Encoding(false));
                    return true;
                }
                catch (Exception e) { Fatal("cannot write header.json: " + e.Message); return false; }
            }

            // ---- small shared machinery --------------------------------------------------

            private bool Tick()
            {
                if (_sw.ElapsedMilliseconds < _req.SliceBudgetMs) return false;
                _sw.Restart();
                return true;
            }

            private void Progress(float from, float to, int done, int total)
            {
                if (_writer != null) { _result.PartsBuilt = _writer.PartCount; _result.BytesBuilt = _writer.BytesWritten; }
                if (total <= 0) { _result.Progress01 = to; return; }
                _result.Progress01 = Mathf.Clamp01(from + (to - from) * (done / (float)total));
            }

            /// <summary>The bounds every tree walk inside the project runs under. Nothing named
            /// here, because `Library/` and `Packages/` are not under `Assets/` in the first
            /// place — but a symlink, a dot-folder and a `Foo~` folder are all possible there.</summary>
            private static ExportWalkLimits AssetWalkLimits() => new ExportWalkLimits();

            /// <summary>Everything a pure helper could not read becomes an `errors` row. They are
            /// scrubbed on the way in like every other one.</summary>
            private void AddNotes(List<string> notes)
            {
                foreach (var n in notes) Err(n);
                notes.Clear();
            }

            /// <summary>This machine's absolute roots, and what each becomes in anything that
            /// leaves it. See <see cref="ExportFormat.ScrubPaths"/> for why this exists.</summary>
            private IEnumerable<KeyValuePair<string, string>> MachineRoots()
            {
                yield return new KeyValuePair<string, string>(_projectDir, "<project>");
                yield return new KeyValuePair<string, string>(_docRoot, "<project>");
                yield return new KeyValuePair<string, string>(_req.OutDir ?? "", "<export>");
                string home = "", temp = "";
                try { home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } catch (Exception) { }
                try { temp = Path.GetTempPath(); } catch (Exception) { }
                yield return new KeyValuePair<string, string>(temp, "<temp>");
                yield return new KeyValuePair<string, string>(home, "~");
            }

            private string Scrub(string message) => ExportFormat.ScrubPaths(message, MachineRoots());

            private void Fatal(string message)
            {
                _fatal = true;
                message = Scrub(message);
                _result.FatalError = message;
                _result.Phase = "failed";
                _result.Done = true;
            }

            // EVERY error goes through the scrub: these strings are built from exception messages,
            // and .NET writes the absolute path into those.
            private void Err(string message) => _header.Errors.Add(Scrub(message));

            private void Over(string path, long bytes, string kind, string reason) =>
                _header.OverCap.Add(new ExportOverCap { Path = path, Bytes = bytes, Kind = kind, Reason = reason });

            private bool UnderRoot(string path) =>
                string.Equals(path, _assetsRoot, StringComparison.Ordinal) ||
                path.StartsWith(_assetsRoot + "/", StringComparison.Ordinal);

            private string Abs(string assetPath) =>
                Path.Combine(_projectDir, assetPath.Replace('/', Path.DirectorySeparatorChar));

            private string[] SafeFindAssets(string filter, string root)
            {
                try { return AssetDatabase.FindAssets(filter, new[] { root }) ?? Array.Empty<string>(); }
                catch (Exception e) { Err("FindAssets('" + filter + "'): " + e.Message); return Array.Empty<string>(); }
            }

            private string SafeStr(Func<string?> read)
            {
                try { return read() ?? ""; }
                catch (Exception) { return ""; }
            }

            private bool SafeBool(Func<bool> read)
            {
                try { return read(); }
                catch (Exception) { return false; }
            }

            /// <summary>
            /// How many thumbnails between memory sweeps — ONE of the two triggers, not the bound.
            /// MEASURED 2026-09-21 at 1,200 thumbnails: no sweep = 174 MB of peak growth, sweeping
            /// every 256 = 39 MB. What is bounded by THIS NUMBER is how many SOURCE TEXTURES are
            /// loaded at once; how many BYTES that is depends entirely on the game, and on the
            /// 16x16 fixtures it was measured on it came to ~0.15 MB each. On a shipped game's
            /// 2048-square art it is thousands of times that, which is why
            /// <see cref="ExportCaps.ThumbnailSweepBytes"/> exists beside it (audit round 3, N4).
            ///
            /// Reusing one readback Texture2D instead of allocating one per thumbnail was tried and
            /// MEASURED NOT TO HELP (174.2 MB, unchanged): the memory is held by loading each
            /// source texture through the AssetDatabase, not by the readback target.
            /// </summary>
            private const int ThumbnailSweepEvery = 256;

            /// <summary>How many AudioClips the identity pass may hold between sweeps. Smaller than
            /// the thumbnail interval on purpose: a Decompress-on-load clip is megabytes, and
            /// unlike a texture there is no cheap per-item byte estimate to lean on here.</summary>
            private const int AudioSweepEvery = 64;

            /// <summary>
            /// Gives the blocks back. THE ONLY THING THAT RELEASES ANYTHING IN THIS FILE, and the
            /// only one that is safe to: it releases UNREFERENCED assets, so whatever the studio's
            /// open scene or running game holds stays exactly where it is.
            ///
            /// Per-object `Resources.UnloadAsset` used to run beside it and was NOT safe: a texture
            /// a live material was showing read back GREY (0.502) afterwards and stayed grey until
            /// something re-assigned it. The control that separated the two: the sweep on its own
            /// left the same texture red (fresh-context audit, 2026-09-21). The per-object unload
            /// was also measured not to bound memory on its own — this is what does.
            /// </summary>
            private void SweepUnusedAssets()
            {
                _result.Sweeps++;
                try { EditorUtility.UnloadUnusedAssetsImmediate(); }
                catch (Exception e) { Err("could not release unused assets: " + e.Message); }
            }
        }
    }
}
