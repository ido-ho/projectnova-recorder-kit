using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ProjectNova.RecorderKit
{
    /// <summary>One entry of the header's `files` block: where a facts file's shards are, and how
    /// many rows they hold.</summary>
    public sealed class ExportFileSet
    {
        public List<string> Shards = new List<string>();
        public int Rows;

        public JObject ToJson()
        {
            var arr = new JArray();
            foreach (var s in Shards) arr.Add(s);
            return new JObject { ["shards"] = arr, ["rows"] = Rows };
        }

        public static ExportFileSet FromJson(JObject? o)
        {
            var f = new ExportFileSet { Rows = ExportJson.Int(o, "rows") };
            var arr = ExportJson.Arr(o, "shards");
            if (arr != null)
                foreach (var t in arr)
                {
                    var s = t?.Value<string>();
                    if (s != null) f.Shards.Add(s);
                }
            return f;
        }
    }

    /// <summary>One thing a cap kept OUT of the export, by name. NEVER SILENT: the hosted side must
    /// be able to say "12 textures over the cap, listed" instead of quietly learning a smaller
    /// game.</summary>
    public sealed class ExportOverCap
    {
        public string Path = "";
        public long Bytes;
        /// <summary>One of the `ExportFormat.Kind*` constants.</summary>
        public string Kind = "";
        public string Reason = "";

        public JObject ToJson() => new JObject
        {
            ["path"] = ExportFormat.Listed(Path),
            ["bytes"] = Bytes,
            ["kind"] = Kind,
            ["reason"] = Reason,
        };

        public static ExportOverCap FromJson(JObject? o) => new ExportOverCap
        {
            Path = ExportJson.Str(o, "path"),
            Bytes = ExportJson.Long(o, "bytes"),
            Kind = ExportJson.Str(o, "kind"),
            Reason = ExportJson.Str(o, "reason"),
        };
    }

    /// <summary>
    /// The totals the hosted side RECONCILES against (spec §2/§7). Counted by the kit from the
    /// AssetDatabase, not from what it managed to write — so a row that failed to serialise shows
    /// up as a difference instead of as a smaller game.
    ///
    /// The exception is anything a CAP can drop (`patternHits`, `artFiles`, `thumbnails`, `docs`):
    /// those are the WRITTEN counts, because §7 compares them against what it unzips and a capped
    /// export would otherwise fail reconcile for doing exactly what the caps told it to. What the
    /// cap dropped is named in `overCap` / `errors`.
    /// </summary>
    public sealed class ExportTotals
    {
        public int Assets;
        public SortedDictionary<string, int> AssetsByType = new SortedDictionary<string, int>(StringComparer.Ordinal);
        public int Scenes;
        /// <summary>ENABLED scenes in EditorBuildSettings.</summary>
        public int BuildScenes;
        /// <summary>Every direct child DIR of the assets root.</summary>
        public List<string> DirsTopLevel = new List<string>();
        public int DependencyRows;
        public int PatternHits;
        public int ArtFiles;
        public int Thumbnails;
        public int Docs;

        public JObject ToJson()
        {
            return new JObject
            {
                ["assets"] = Assets,
                ["assetsByType"] = ByTypeJson(),
                ["scenes"] = Scenes,
                ["buildScenes"] = BuildScenes,
                ["dirsTopLevel"] = DirsJson(),
                ["dependencyRows"] = DependencyRows,
                ["patternHits"] = PatternHits,
                ["artFiles"] = ArtFiles,
                ["thumbnails"] = Thumbnails,
                ["docs"] = Docs,
            };
        }

        /// <summary>
        /// BOUNDED LIKE EVERY OTHER LISTING (invariant 102 — a header at every cap at once must
        /// still fit the body the server accepts). `errors`, `overCap` and `parts` were bounded
        /// from the start; these two were not, and a project with thousands of distinct
        /// ScriptableObject types is ordinary (fresh-context audit, 2026-09-21).
        ///
        /// THE SUM STAYS TRUE: what the cap folds away is counted under `"(other)"`, so the
        /// per-type counts still add up to `totals.assets` and §7's reconcile cannot be broken by
        /// the bound. Two type names that are equal once truncated merge their counts for the same
        /// reason. The collector adds an `errors` row saying how many were folded.
        /// </summary>
        private JObject ByTypeJson() => FoldByType(AssetsByType, out _);

        /// <summary>
        /// THE FOLD, IN ONE FUNCTION, so the header and the `errors` row that DESCRIBES the header
        /// cannot drift apart (invariant 99's corollary). <paramref name="foldedKeys"/> is how many
        /// LISTED keys went into <see cref="ExportFormat.OtherTypeKey"/> — 0 when the cap did not
        /// bite, whatever the raw type count was.
        ///
        /// LIST FIRST, THEN FOLD, because that is the only order the server can mirror. The hosted
        /// reconcile lists each type that LANDED and asks "is that key in the header?", counting it
        /// there if it is and under `"(other)"` if it is not. Deciding "fold" before listing broke
        /// the mirror whenever a folded raw name listed to a key some KEPT name also listed to —
        /// through truncation at 100 units, or through the control-character scrub — and a
        /// perfectly valid export then failed to reconcile (audit round 4, S2).
        ///
        /// THE SUM STAYS TRUE either way: the per-type counts still add up to `totals.assets`.
        /// </summary>
        internal static JObject FoldByType(SortedDictionary<string, int> byRawType, out int foldedKeys)
        {
            // PASS 1 — every raw name through `Listed`, merged onto the key it produces. Ordinal
            // order, which is also JS's default string order, so both sides cut the same tail.
            var listed = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var kv in byRawType)
            {
                var key = ExportFormat.Listed(kv.Key, ExportFormat.AssetTypeKeyTextMax);
                if (key.Length == 0) key = ExportFormat.OtherTypeKey;
                listed.TryGetValue(key, out var n);
                listed[key] = n + kv.Value;
            }

            foldedKeys = 0;
            var byType = new JObject();
            if (listed.Count <= ExportFormat.AssetTypeKeysMax)
            {
                foreach (var kv in listed) byType[kv.Key] = kv.Value;
                return byType;
            }

            // PASS 2 — the cap bites on KEYS, never on raw names. One slot is left for `"(other)"`
            // so the whole listing still fits `AssetTypeKeysMax`.
            var kept = 0;
            var folded = 0;
            foreach (var kv in listed)
            {
                if (kept >= ExportFormat.AssetTypeKeysMax - 1) { folded += kv.Value; foldedKeys++; continue; }
                byType[kv.Key] = kv.Value;
                kept++;
            }
            if (folded > 0)
            {
                var existing = byType[ExportFormat.OtherTypeKey];
                byType[ExportFormat.OtherTypeKey] = (existing == null ? 0 : existing.Value<int>()) + folded;
            }
            return byType;
        }

        /// <summary>The `errors` row that says the type cap BIT, or null when it did not. It asks
        /// the fold itself rather than counting raw types, because the two are not the same
        /// question: 2,001 raw type names that LIST to 1,502 keys fold nothing, and the row said
        /// two of them had been counted under a key that is not even in the header (M12).</summary>
        internal static string? TypeFoldNote(SortedDictionary<string, int> byRawType)
        {
            FoldByType(byRawType, out var foldedKeys);
            if (foldedKeys <= 0) return null;
            return foldedKeys.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                   " type key(s) past the " +
                   ExportFormat.AssetTypeKeysMax.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                   "-key cap were counted under \"" + ExportFormat.OtherTypeKey +
                   "\" in totals.assetsByType";
        }

        private JArray DirsJson()
        {
            var dirs = new JArray();
            var keep = DirsTopLevel.Count < ExportFormat.DirsTopLevelMax
                ? DirsTopLevel.Count
                : ExportFormat.DirsTopLevelMax;
            for (var i = 0; i < keep; i++)
                dirs.Add(ExportFormat.Listed(DirsTopLevel[i], ExportFormat.DirTextMax));
            return dirs;
        }

        public static ExportTotals FromJson(JObject? o)
        {
            var t = new ExportTotals
            {
                Assets = ExportJson.Int(o, "assets"),
                Scenes = ExportJson.Int(o, "scenes"),
                BuildScenes = ExportJson.Int(o, "buildScenes"),
                DependencyRows = ExportJson.Int(o, "dependencyRows"),
                PatternHits = ExportJson.Int(o, "patternHits"),
                ArtFiles = ExportJson.Int(o, "artFiles"),
                Thumbnails = ExportJson.Int(o, "thumbnails"),
                Docs = ExportJson.Int(o, "docs"),
            };
            var byType = ExportJson.Obj(o, "assetsByType");
            if (byType != null)
                foreach (var p in byType)
                {
                    var v = p.Value;
                    if (v == null) continue;
                    try { t.AssetsByType[p.Key] = v.Value<int>(); } catch (Exception) { /* not a count */ }
                }
            var dirs = ExportJson.Arr(o, "dirsTopLevel");
            if (dirs != null)
                foreach (var d in dirs)
                {
                    var s = d?.Value<string>();
                    if (s != null) t.DirsTopLevel.Add(s);
                }
            return t;
        }
    }

    /// <summary>
    /// `header.json` (spec §2) — posted as the JSON body of
    /// `POST /studio/capture-jobs/:runId/export/header` BEFORE any part is uploaded, and also
    /// written next to the parts in OutDir so an upload can resume after a domain reload.
    ///
    /// WHO ANSWERED is reported, never judged (invariant 100): `workspaceId` is this kit window's
    /// OWN per-project binding, not an echo of the job, so the API compares two independent values.
    /// </summary>
    public sealed class ExportHeader
    {
        public int SchemaVersion = ExportFormat.SchemaVersion;
        public string KitVersion = "";
        public string UnityVersion = "";
        /// <summary>ISO-8601, UTC, `Z`.</summary>
        public string ExportedAt = "";

        public string WorkspaceId = "";
        /// <summary>`Library/Nova/adapter.json` gameId, or null before onboarding.</summary>
        public string? EditorGameId;
        public bool AdapterJsonPresent;
        public string ProductName = "";
        public string CompanyName = "";

        public ExportCaps Caps = new ExportCaps();
        public ExportTotals Totals = new ExportTotals();

        public ExportFileSet Inventory = new ExportFileSet();
        public ExportFileSet Dependencies = new ExportFileSet();
        public ExportFileSet Patterns = new ExportFileSet();
        public ExportFileSet ArtIndex = new ExportFileSet();
        public string BuildFile = ExportFormat.BuildEntry;
        public string IdentityFile = ExportFormat.IdentityEntry;

        public List<ExportOverCap> OverCap = new List<ExportOverCap>();
        public List<string> Errors = new List<string>();
        public List<ExportPartInfo> Parts = new List<ExportPartInfo>();

        public JObject ToJson()
        {
            var files = new JObject
            {
                ["inventory"] = Inventory.ToJson(),
                ["dependencies"] = Dependencies.ToJson(),
                ["patterns"] = Patterns.ToJson(),
                ["artIndex"] = ArtIndex.ToJson(),
                ["build"] = BuildFile,
                ["identity"] = IdentityFile,
            };

            var parts = new JArray();
            foreach (var p in Parts) parts.Add(p.ToJson());

            return new JObject
            {
                ["schemaVersion"] = SchemaVersion,
                ["kitVersion"] = KitVersion,
                ["unityVersion"] = UnityVersion,
                ["exportedAt"] = ExportedAt,
                ["workspaceId"] = WorkspaceId,
                // SCRUBBED AND BOUNDED like every other listed string (M7). These three come from
                // the studio's Player Settings and `adapter.json`, not from the kit — and NULL
                // STAYS NULL: the hosted identity gate compares `editorGameId` against the game
                // this workspace is bound to, and "" is a value it would compare as one.
                ["editorGameId"] = EditorGameId == null
                    ? (JToken)JValue.CreateNull()
                    : new JValue(ExportFormat.Listed(EditorGameId, ExportFormat.GameIdTextMax)),
                ["adapterJsonPresent"] = AdapterJsonPresent,
                ["productName"] = ExportFormat.Listed(ProductName, ExportFormat.NameTextMax),
                ["companyName"] = ExportFormat.Listed(CompanyName, ExportFormat.NameTextMax),
                ["caps"] = Caps.ToJson(),
                ["totals"] = Totals.ToJson(),
                ["files"] = files,
                ["overCap"] = CappedOverCap(),
                ["errors"] = CappedErrors(),
                ["parts"] = parts,
            };
        }

        /// <summary>The JSON exactly as it is POSTed and as it is written to OutDir.</summary>
        public string ToJsonString() => ToJson().ToString(Formatting.Indented);

        public static ExportHeader? FromJson(string json)
        {
            JObject o;
            try
            {
                using (var reader = new JsonTextReader(new System.IO.StringReader(json ?? "")))
                {
                    // Every header field is TEXT. Newtonsoft's DEFAULT turns an ISO-8601-shaped
                    // string into a DateTime, and `exportedAt` then reads back as
                    // "09/21/2026 10:00:00" in whatever culture the machine is set to — a header
                    // that no longer round-trips and a timestamp the server cannot parse. It is
                    // this class that `ExportOnDisk` re-reads after a domain reload, so the bug
                    // would have surfaced only on a resumed upload.
                    reader.DateParseHandling = DateParseHandling.None;
                    var t = JToken.ReadFrom(reader);
                    var asObj = t as JObject;
                    if (asObj == null) return null;
                    o = asObj;
                }
            }
            catch (Exception)
            {
                return null;
            }

            var h = new ExportHeader
            {
                SchemaVersion = ExportJson.Int(o, "schemaVersion"),
                KitVersion = ExportJson.Str(o, "kitVersion"),
                UnityVersion = ExportJson.Str(o, "unityVersion"),
                ExportedAt = ExportJson.Str(o, "exportedAt"),
                WorkspaceId = ExportJson.Str(o, "workspaceId"),
                EditorGameId = ExportJson.StrOrNull(o, "editorGameId"),
                AdapterJsonPresent = ExportJson.Bool(o, "adapterJsonPresent"),
                ProductName = ExportJson.Str(o, "productName"),
                CompanyName = ExportJson.Str(o, "companyName"),
                Caps = ExportCaps.FromJson(ExportJson.Obj(o, "caps")),
                Totals = ExportTotals.FromJson(ExportJson.Obj(o, "totals")),
            };

            var files = ExportJson.Obj(o, "files");
            if (files != null)
            {
                h.Inventory = ExportFileSet.FromJson(ExportJson.Obj(files, "inventory"));
                h.Dependencies = ExportFileSet.FromJson(ExportJson.Obj(files, "dependencies"));
                h.Patterns = ExportFileSet.FromJson(ExportJson.Obj(files, "patterns"));
                h.ArtIndex = ExportFileSet.FromJson(ExportJson.Obj(files, "artIndex"));
                h.BuildFile = ExportJson.Str(files, "build", ExportFormat.BuildEntry);
                h.IdentityFile = ExportJson.Str(files, "identity", ExportFormat.IdentityEntry);
            }

            var over = ExportJson.Arr(o, "overCap");
            if (over != null)
                foreach (var t in over)
                {
                    var obj = t as JObject;
                    if (obj != null) h.OverCap.Add(ExportOverCap.FromJson(obj));
                }

            var errs = ExportJson.Arr(o, "errors");
            if (errs != null)
                foreach (var t in errs)
                {
                    var s = t?.Value<string>();
                    if (s != null) h.Errors.Add(s);
                }

            var parts = ExportJson.Arr(o, "parts");
            if (parts != null)
                foreach (var t in parts)
                {
                    var obj = t as JObject;
                    if (obj != null) h.Parts.Add(ExportPartInfo.FromJson(obj));
                }

            return h;
        }

        // ---- bounded listings (spec §2) --------------------------------------------------
        // "`overCap` and `errors` are each capped at ListingMax entries; past that the LAST entry says
        // how many more were dropped." The in-memory lists stay whole — only the emitted JSON is
        // bounded, and `totals` never comes from these lists.

        private JArray CappedErrors()
        {
            var arr = new JArray();
            var keep = Errors.Count <= ExportFormat.ListingMax ? Errors.Count : ExportFormat.ListingMax - 1;
            for (var i = 0; i < keep; i++) arr.Add(ExportFormat.Listed(Errors[i]));
            if (Errors.Count > ExportFormat.ListingMax)
                arr.Add(ExportFormat.MoreTail(Errors.Count - keep));
            return arr;
        }

        private JArray CappedOverCap()
        {
            var arr = new JArray();
            var keep = OverCap.Count <= ExportFormat.ListingMax ? OverCap.Count : ExportFormat.ListingMax - 1;
            for (var i = 0; i < keep; i++) arr.Add(OverCap[i].ToJson());
            if (OverCap.Count > ExportFormat.ListingMax)
            {
                var dropped = OverCap.Count - keep;
                long droppedBytes = 0;
                for (var i = keep; i < OverCap.Count; i++) droppedBytes += OverCap[i].Bytes;
                // The tail of an OBJECT array has to be an object. Shape pinned here so the
                // TypeScript mirror copies it rather than inventing a second one.
                arr.Add(new ExportOverCap
                {
                    Path = ExportFormat.MoreTail(dropped),
                    Bytes = droppedBytes,
                    Kind = ExportFormat.KindMore,
                    Reason = ExportFormat.MoreTail(dropped),
                }.ToJson());
            }
            return arr;
        }
    }
}
