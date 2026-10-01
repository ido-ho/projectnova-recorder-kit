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
    public static partial class ExportCollector
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

        private sealed partial class Job
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
