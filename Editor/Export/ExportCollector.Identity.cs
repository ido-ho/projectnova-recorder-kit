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
    // ExportCollector.Job — phase 5: identity.json (fonts, colours, audio facts off the assets). The class summary is in ExportCollector.cs.
    public static partial class ExportCollector
    {
        private sealed partial class Job
        {
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
        }
    }
}
