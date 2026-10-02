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
    // ExportCollector.Job — phases 1–4: the asset inventory, build.json, the direct reference graph, inBuild. The class summary is in ExportCollector.cs.
    public static partial class ExportCollector
    {
        private sealed partial class Job
        {
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
                // F2 47 (invariant 266): the scripting defines the EDITOR compiles with (its active build target's group)
                // — with the `release-define` rows, the website tells whether the editor runs the shipped game's server
                // path. Absent when it cannot be read: the website then says "not sent", never "no defines".
                var editorDefines = ReadEditorDefines();
                if (editorDefines != null) _buildJson["editorDefines"] = editorDefines;
                _result.Progress01 = 0.25f;
                if (Tick()) yield return 0;
            }

            /// <summary>F2 47: <c>{ target, defines }</c> for the active build target's group, or null when Unity would not
            /// say (one failed read is an `errors` row, never a guess).</summary>
            private JObject? ReadEditorDefines()
            {
                try
                {
                    var group = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget);
                    var target = NamedBuildTarget.FromBuildTargetGroup(group);
                    return ExportFormat.EditorDefinesJson(target.TargetName, PlayerSettings.GetScriptingDefineSymbols(target));
                }
                catch (Exception e)
                {
                    Err("PlayerSettings: could not read the editor's scripting defines: " + e.Message);
                    return null;
                }
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
        }
    }
}
