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
    // ExportCollector.Job — phases 9–10: writing the parts and header.json. The class summary is in ExportCollector.cs.
    public static partial class ExportCollector
    {
        private sealed partial class Job
        {
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

            private static JObject IndexRow(Item a)
            {
                var o = new JObject
                {
                    ["guid"] = a.Guid,
                    ["path"] = a.Path,
                    ["entry"] = a.Entry,
                    ["kind"] = a.IndexKind,
                    ["bytes"] = a.Bytes,
                };
                // format doc §9.3: an effect prefab's row says what decided it — the server re-checks the same rule
                if (a.Prefab is PrefabCounts c)
                {
                    o["particleSystems"] = c.ParticleSystems;
                    o["gameObjects"] = c.GameObjects;
                    o["prefabInstances"] = c.PrefabInstances;
                    o["rectTransforms"] = c.RectTransforms;
                }
                return o;
            }

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
                _header.ArtKinds = new List<string>(ExportArtKinds.Sent);

                try
                {
                    File.WriteAllText(Path.Combine(_req.OutDir, "header.json"),
                        _header.ToJsonString(), new UTF8Encoding(false));
                    return true;
                }
                catch (Exception e) { Fatal("cannot write header.json: " + e.Message); return false; }
            }
        }
    }
}
