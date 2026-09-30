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
    // ExportCollector.Job — phases 6–8: the .cs pattern scan, clickable UI, the game's own words, docs/config, which art ships. The class summary is in ExportCollector.cs.
    public static partial class ExportCollector
    {
        private sealed partial class Job
        {
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
        }
    }
}
