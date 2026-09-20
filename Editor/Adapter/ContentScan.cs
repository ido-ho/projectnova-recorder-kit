using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Discovers a game's enumerable CONTENT — the ids a script can name (heroes, pets, skins) —
    /// by scanning ScriptableObject assets.
    ///
    /// Reports; never decides. The adapter declares which types are real content axes
    /// (GameAdapter.ContentSources), because a project's SO types also include configs, audio
    /// libraries and view settings that have no ad meaning.
    ///
    /// An id ALWAYS comes from the asset's data. There is deliberately no filename fallback:
    /// roguelegend's hero asset is `Rogue.asset` holding `hero.default`, so a fallback would invent
    /// a plausible wrong id that downstream validation would then treat as real.
    /// </summary>
    public static class ContentScan
    {
        private const BindingFlags ANY_INSTANCE =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private static readonly string[] IdNames = { "Id", "id", "_id" };

        /// <summary>The id this asset declares, or null when it declares none.</summary>
        public static string? ResolveId(UnityEngine.Object asset)
        {
            if (asset == null) return null;
            var t = asset.GetType();

            foreach (var name in IdNames)
            {
                PropertyInfo? prop;
                try
                {
                    prop = t.GetProperty(name, ANY_INSTANCE);
                }
                catch (AmbiguousMatchException)
                {
                    // A derived type shadows a same-named property of a DIFFERENT TYPE declared on
                    // a base class (e.g. base `public int Id`, derived `public new string Id`) —
                    // GetProperty throws here instead of returning null. Treat that the same as "no
                    // such property" for this candidate name rather than letting the exception
                    // escape ResolveId: an unusual shape like this should degrade to "no id
                    // resolved", never crash the whole Report() scan for every other declared type.
                    prop = null;
                }
                if (prop != null && prop.PropertyType == typeof(string) && prop.CanRead)
                {
                    if (prop.GetValue(asset) is string s && s.Length > 0) return s;
                }
            }

            foreach (var name in IdNames)
            {
                var field = t.GetField(name, ANY_INSTANCE);
                if (field != null && field.FieldType == typeof(string))
                {
                    if (field.GetValue(asset) is string s && s.Length > 0) return s;
                }
            }

            // Defense-in-depth only — NOT what protects roguelegend's actual asset today. An
            // auto-property like `public string Id { get; private set; }` is already resolved by
            // the property pass above, via its live public getter: reflection exposes a public
            // getter as public regardless of the setter's visibility, and ANY_INSTANCE includes
            // NonPublic anyway. That's why roguelegend's on-disk `<Id>k__BackingField` data resolves
            // correctly without ever reaching this pass. This backing-field lookup only matters for
            // a property with no reflectable accessor at all — a genuinely unusual shape — so a
            // future maintainer touching this block should not assume it's live protection for the
            // documented real-world case.
            foreach (var name in IdNames)
            {
                var field = t.GetField($"<{name}>k__BackingField", ANY_INSTANCE);
                if (field != null && field.FieldType == typeof(string))
                {
                    if (field.GetValue(asset) is string s && s.Length > 0) return s;
                }
            }

            return null;
        }

        /// <summary>
        /// One block per declared type: the count of assets found, how many yielded ids, and EVERY
        /// resolved id (sorted) — not a sample. This text is machine-parsed by `admiral
        /// content-index` (apps/renderer/src/admiral/cli.ts) into content-index.json, which
        /// `admiral validate` then treats as the authoritative list of valid ids for that source.
        /// An id resolved here but missing from that index is a real id `validate` will wrongly
        /// REJECT — a false-rejection failure, not a cosmetic one. Do NOT reintroduce a cap on the
        /// id list (e.g. "just show the first N for readability") without also changing the
        /// downstream index/validate contract: a prior version of this method capped at 8 samples,
        /// which was fine for a human-readable probe but silently produced an incomplete index once
        /// a later task started parsing this output as ground truth (roguelegend's own
        /// `LocalPetData` has 25 ids — a cap of 8 would drop 17 of them from every game's index). A
        /// type that cannot be resolved is reported as `unreadable` rather than left out — an
        /// invisible source is worse than a named gap.
        /// </summary>
        public static IEnumerable<string> Report(string[] typeNames)
        {
            if (typeNames == null || typeNames.Length == 0)
            {
                yield return "no ContentSources declared on the adapter — nothing to scan";
                yield break;
            }

            foreach (var typeName in typeNames)
            {
                var guids = AssetDatabase.FindAssets($"t:{typeName}");
                if (guids == null || guids.Length == 0)
                {
                    yield return $"{typeName}: unreadable — no assets matched `t:{typeName}` " +
                                 "(is the type name right, and is it a ScriptableObject?)";
                    continue;
                }

                var ids = new List<string>();
                var missing = 0;
                foreach (var guid in guids)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    var asset = AssetDatabase.LoadMainAssetAtPath(path);
                    var id = ResolveId(asset);
                    if (id == null) missing++; else ids.Add(id);
                }

                yield return $"{typeName}: {guids.Length} asset(s), {ids.Count} with ids" +
                             (missing > 0 ? $", {missing} unreadable (no Id property/field)" : "");
                foreach (var line in FormatIdLines(ids))
                    yield return line;
            }
        }

        /// <summary>
        /// Formats a source's resolved ids as sorted, 4-space-indented lines — one per id, with no
        /// cap. Split out of Report() so this exact formatting (in particular: does it ever drop or
        /// summarize ids past some count?) is unit-testable without needing real on-disk,
        /// AssetDatabase-indexed assets just to exercise it — Report() itself can only be driven
        /// through AssetDatabase.FindAssets, which none of this suite's other tests do either.
        /// Internal, not private: ContentScanTests exercises it directly via
        /// [assembly: InternalsVisibleTo] (see Editor/AssemblyInfo.cs).
        /// </summary>
        internal static IEnumerable<string> FormatIdLines(IEnumerable<string> ids)
        {
            foreach (var id in ids.OrderBy(x => x))
                yield return $"    {id}";
        }
    }
}
