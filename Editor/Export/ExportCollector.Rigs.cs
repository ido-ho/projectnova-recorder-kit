using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace ProjectNova.RecorderKit
{
    // ExportCollector.Job — video-first plan step 2 (§7): the game's Spine rigs and particle effects, planned as ART.
    // The class summary is in ExportCollector.cs. Kept in its own file so the art plan's other phases stay untouched.
    //
    // FACTS, NEVER VERDICTS. This file does not decide that a rig is a hero or a boss — it sends the files a rig is MADE
    // OF (the skeleton, the atlas text, the atlas pages) for every SkeletonDataAsset the game uses, and the prefabs that
    // hold a ParticleSystem. The server names them (apps/renderer/src/game-ads/art/art-cards.ts).
    //
    // "USED" IS THE SERVER'S RULE (invariant 274): the inBuild/addressable rows and the app icons, then their dependency
    // closure. The kit's own `Row.Used` misses a picture a Resources asset depends on outside Resources, so the closure is
    // walked here the same way `used-by-game.ts` walks it.
    public static partial class ExportCollector
    {
        /// <summary>The files that make up the Spine rigs the game uses, in plan order: per rig (by path), its skeleton
        /// file(s), then per atlas its text and its page pictures. Pure over the inventory's TYPES and the direct
        /// dependency graph, so a test drives it without an AssetDatabase. `typeOf`/`pathOf` are by guid.</summary>
        internal static List<string> SpineFileGuids(
            IReadOnlyDictionary<string, string> typeOf,
            IReadOnlyDictionary<string, string> pathOf,
            IReadOnlyDictionary<string, List<string>> deps,
            ICollection<string> used)
        {
            var rigs = new List<string>();
            foreach (var kv in typeOf)
                if (kv.Value == "SkeletonDataAsset" && used.Contains(kv.Key)) rigs.Add(kv.Key);
            rigs.Sort((a, b) => string.CompareOrdinal(PathOr(pathOf, a), PathOr(pathOf, b)));
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var outList = new List<string>();
            void Add(string g) { if (seen.Add(g)) outList.Add(g); }
            List<string> OfType(string guid, string type)
            {
                var l = new List<string>();
                if (deps.TryGetValue(guid, out var ds))
                    foreach (var d in ds)
                        if (typeOf.TryGetValue(d, out var t) && t == type) l.Add(d);
                l.Sort((a, b) => string.CompareOrdinal(PathOr(pathOf, a), PathOr(pathOf, b)));
                return l;
            }
            foreach (var rig in rigs)
            {
                foreach (var s in OfType(rig, "TextAsset")) Add(s);
                foreach (var atlas in OfType(rig, "SpineAtlasAsset"))
                {
                    foreach (var t in OfType(atlas, "TextAsset")) Add(t);
                    foreach (var m in OfType(atlas, "Material"))
                        foreach (var p in OfType(m, "Texture2D")) Add(p);
                }
            }
            return outList;
        }

        private static string PathOr(IReadOnlyDictionary<string, string> pathOf, string guid) =>
            pathOf.TryGetValue(guid, out var p) ? p : guid;

        /// <summary>Count a prefab's own YAML documents. Unity writes each object as its own document headed
        /// `--- !u!&lt;classID&gt; &amp;&lt;fileID&gt;`: a ParticleSystem is class 198, a GameObject 1, a nested prefab
        /// instance 1001, a RectTransform (UI) 224. A ` stripped` document is a nested prefab's object referenced here,
        /// not one of this file's own, and is not counted. Read as TEXT, like the UI scan: the prefab is never loaded,
        /// so none of the studio's code runs.</summary>
        internal static PrefabCounts CountPrefabDocs(string yaml)
        {
            var c = new PrefabCounts();
            if (string.IsNullOrEmpty(yaml)) return c;
            var i = 0;
            while (i < yaml.Length)
            {
                var end = yaml.IndexOf('\n', i);
                if (end < 0) end = yaml.Length;
                if (end - i > 7 && string.CompareOrdinal(yaml, i, "--- !u!", 0, 7) == 0)
                {
                    var line = yaml.Substring(i, end - i).TrimEnd('\r');
                    var j = 7;
                    while (j < line.Length && char.IsDigit(line[j])) j++;
                    if (j > 7 && j + 1 < line.Length && line[j] == ' ' && line[j + 1] == '&' && !line.EndsWith(" stripped", StringComparison.Ordinal))
                    {
                        switch (line.Substring(7, j - 7))
                        {
                            case "198": c.ParticleSystems++; break;
                            case "1": c.GameObjects++; break;
                            case "1001": c.PrefabInstances++; break;
                            case "224": c.RectTransforms++; break;
                        }
                    }
                }
                i = end + 1;
            }
            return c;
        }

        /// <summary>AN EFFECT is a prefab that is mostly particles: at least one ParticleSystem, no more RectTransforms
        /// than ParticleSystems, and ParticleSystems at least 1/5 of its GameObjects plus nested prefabs. A UI screen
        /// with one sparkle is not an effect. Twin of the renderer's `isEffectPrefab` (art-cards.ts), both held to
        /// apps/renderer/src/game-ads/art/effect-rule.cases.json.</summary>
        internal static bool IsEffectPrefab(PrefabCounts c) =>
            c.ParticleSystems > 0
            && c.RectTransforms <= c.ParticleSystems
            && c.ParticleSystems * 5 >= c.GameObjects + c.PrefabInstances;

        private sealed partial class Job
        {
            private HashSet<string>? _usedClosure;
            /// <summary>Spec §3 (video-first): effect prefabs to send, found by <see cref="ReadEffects"/>.</summary>
            private readonly List<Row> _vfxRows = new List<Row>();
            /// <summary>What decided each one, for its art index row.</summary>
            private readonly Dictionary<string, PrefabCounts> _vfxCounts = new Dictionary<string, PrefabCounts>(StringComparer.Ordinal);

            /// <summary>The server's "used by the game" (invariant 274): roots = inBuild/addressable rows + app icons,
            /// then the direct-dependency closure.</summary>
            private HashSet<string> UsedClosure()
            {
                if (_usedClosure != null) return _usedClosure;
                var roots = new List<string>();
                foreach (var r in _rows) if (r.Used) roots.Add(r.Guid);
                roots.AddRange(_iconGuids);
                var graph = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                foreach (var kv in _depRows) graph[kv.Key] = kv.Value;
                _usedClosure = AddressableClosure(roots, graph);
                return _usedClosure;
            }

            /// <summary>The rows behind <see cref="SpineFileGuids"/>, for the art plan.</summary>
            private List<Row> SpineRows()
            {
                var typeOf = new Dictionary<string, string>(StringComparer.Ordinal);
                var pathOf = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var r in _rows) { typeOf[r.Guid] = r.Type; pathOf[r.Guid] = r.Path; }
                var graph = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                foreach (var kv in _depRows) graph[kv.Key] = kv.Value;
                var list = new List<Row>();
                foreach (var g in SpineFileGuids(typeOf, pathOf, graph, UsedClosure()))
                    if (_guidToRow.TryGetValue(g, out var row)) list.Add(row);
                return list;
            }

            /// <summary>The particle prefabs the plan sends — found in their own phase so a project with thousands of
            /// prefabs is read in slices, never in one frame. Bounded by <see cref="ExportCaps.VfxMaxFiles"/> and
            /// <see cref="ExportCaps.VfxFileMaxBytes"/>; what a bound stops is LISTED in `overCap`, never dropped.</summary>
            private IEnumerable<int> ReadEffects()
            {
                _result.Phase = "effects";
                var used = UsedClosure();
                var prefabs = new List<Row>();
                foreach (var r in _rows)
                    if (r.Path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) && used.Contains(r.Guid)) prefabs.Add(r);
                prefabs.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
                for (var i = 0; i < prefabs.Count; i++)
                {
                    var r = prefabs[i];
                    if (IsThroughLink(r.Path)) continue;   // listed, never read (M2)
                    try
                    {
                        var abs = Abs(r.Path);
                        var length = new FileInfo(abs).Length;
                        if (length > _caps.VfxFileMaxBytes)
                        {
                            // too big to read for the check: said, never guessed either way
                            Err(r.Path + ": not checked for a particle system: over vfxFileMaxBytes (" + length.ToString(CultureInfo.InvariantCulture) + " bytes)");
                        }
                        else
                        {
                            var counts = CountPrefabDocs(File.ReadAllText(abs));
                            if (IsEffectPrefab(counts))
                            {
                                if (_vfxRows.Count >= _caps.VfxMaxFiles) Over(r.Path, length, ExportArtKinds.Vfx, ExportArtKinds.ReasonOverVfxCount);
                                else
                                {
                                    _vfxRows.Add(r);
                                    _vfxCounts[r.Guid] = counts;
                                }
                            }
                        }
                    }
                    catch (Exception e) { Err(r.Path + ": could not check for a particle system: " + e.Message); }
                    if (Tick()) yield return 0;
                }
            }

            private List<Row> VfxRows() => new List<Row>(_vfxRows);
        }
    }
}
