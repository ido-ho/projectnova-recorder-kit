using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace ProjectNova.RecorderKit
{
    /// <summary>One clickable UI element (spec §8.7).</summary>
    public sealed class ExportUiRow
    {
        public string File = "";
        /// <summary>The GameObject's name chain from the file's root, `/`-joined.</summary>
        public string Path = "";
        public string Name = "";
        /// <summary>The clickable component's script type, when it resolves.</summary>
        public string? Component;
        /// <summary>The first `m_text`/`m_Text` on the element or its children, cut to 200 chars.</summary>
        public string? Label;
        public List<string> OnClick = new List<string>();

        public JObject ToJson()
        {
            var o = new JObject { ["file"] = File, ["path"] = Path, ["name"] = Name };
            if (Component != null) o["component"] = Component;
            if (Label != null) o["label"] = Label;
            o["onClick"] = new JArray(OnClick);
            return o;
        }
    }

    /// <summary>
    /// Spec §8.7 — the clickable elements of ONE text-serialized prefab or scene, read from its YAML.
    ///
    /// WHY TEXT, NOT LOADING: loading a prefab runs the studio's `OnValidate`/`Awake`-adjacent editor
    /// code and costs seconds on a big project (the identity pass stopped loading assets for the
    /// same reason — audit K6). The YAML already holds everything needed: GameObject names,
    /// Transform parents, each MonoBehaviour's script guid, its `m_OnClick` persistent calls and any
    /// `m_text`. "Clickable" = a component with an `m_OnClick:` block, which is uGUI's `Button` and
    /// every class derived from it (a studio's own buttons included), with no list of type names.
    ///
    /// WHAT IT CANNOT SEE, stated: the children of a NESTED prefab instance live in that prefab's own
    /// file (and are read there); a runtime-built UI has no YAML; a non-uGUI stack has no `m_OnClick`.
    /// </summary>
    public static class ExportUiScan
    {
        public const int LabelMax = 200;
        public const int NameMax = 200;
        public const int PathMax = 400;
        public const int OnClickMax = 8;

        private static readonly Regex DocHeader =
            new Regex(@"^--- !u!(\d+) &(-?\d+)( stripped)?", RegexOptions.CultureInvariant);
        private static readonly Regex FileIdRef =
            new Regex(@"\{fileID: (-?\d+)", RegexOptions.CultureInvariant);
        private static readonly Regex ScriptGuid =
            new Regex(@"guid: ([0-9a-fA-F]{32})", RegexOptions.CultureInvariant);

        private sealed class Doc
        {
            public int ClassId;
            public string Id = "";
            /// <summary>A `stripped` stand-in for an object that lives in a NESTED prefab's own file.</summary>
            public bool Stripped;
            public List<string> Lines = new List<string>();
        }

        /// <summary>
        /// The rows of one file. <paramref name="scriptName"/> maps a script guid to its type name
        /// (null when it does not resolve). A file that is not text YAML yields nothing and a note.
        /// </summary>
        public static List<ExportUiRow> Parse(string file, string? text, Func<string, string?> scriptName, List<string>? notes = null)
        {
            var rows = new List<ExportUiRow>();
            if (string.IsNullOrEmpty(text)) return rows;
            if (!text!.StartsWith("%YAML", StringComparison.Ordinal))
            {
                notes?.Add(file + ": not text-serialized, UI elements not read");
                return rows;
            }

            var docs = SplitDocs(text);
            var goName = new Dictionary<string, string>(StringComparer.Ordinal);
            var goTransform = new Dictionary<string, string>(StringComparer.Ordinal);
            var transformGo = new Dictionary<string, string>(StringComparer.Ordinal);
            var transformParent = new Dictionary<string, string>(StringComparer.Ordinal);
            var behaviours = new List<Doc>();
            var behaviourGo = new Dictionary<string, string>(StringComparer.Ordinal);
            var behaviourScript = new Dictionary<string, string>(StringComparer.Ordinal);
            var goText = new Dictionary<string, string>(StringComparer.Ordinal);

            // NESTED PREFABS (audit of 0a8addb6: 96 of RL's 967 buttons lost their path here). An object
            // that comes from a nested prefab appears in THIS file as a `stripped` stand-in pointing at
            // its PrefabInstance (class 1001), and the instance holds the two facts the stand-in lacks:
            // where it hangs (`m_TransformParent`) and its name (an `m_Name` modification).
            var instanceParent = new Dictionary<string, string>(StringComparer.Ordinal);
            var instanceName = new Dictionary<string, string>(StringComparer.Ordinal);
            var strippedInstance = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var d in docs)
            {
                if (d.ClassId == 1001) ReadInstance(d, instanceParent, instanceName);
                else if (d.Stripped)
                {
                    var inst = Ref(Field(d, "m_PrefabInstance"));
                    if (inst != null) strippedInstance[d.Id] = inst;
                }
            }
            var instanceTransform = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var d in docs)
                if (d.Stripped && (d.ClassId == 4 || d.ClassId == 224) && strippedInstance.TryGetValue(d.Id, out var i4))
                    instanceTransform[i4] = d.Id;

            foreach (var d in docs)
            {
                if (d.Stripped)
                {
                    if (!strippedInstance.TryGetValue(d.Id, out var inst)) continue;
                    var nameOf = instanceName.TryGetValue(inst, out var inm) ? inm : null;
                    if (d.ClassId == 1)
                    {
                        if (nameOf != null) goName[d.Id] = nameOf;
                        if (instanceTransform.TryGetValue(inst, out var itr)) { goTransform[d.Id] = itr; transformGo[itr] = d.Id; }
                    }
                    else if (d.ClassId == 4 || d.ClassId == 224)
                    {
                        if (instanceParent.TryGetValue(inst, out var par) && par != "0") transformParent[d.Id] = par;
                        if (!transformGo.ContainsKey(d.Id))
                        {
                            // no stripped GameObject in this file: stand in for it, named after the instance
                            var synthetic = "pi:" + inst;
                            transformGo[d.Id] = synthetic;
                            goTransform[synthetic] = d.Id;
                            if (nameOf != null) goName[synthetic] = nameOf;
                        }
                    }
                    continue;
                }
                switch (d.ClassId)
                {
                    case 1:   // GameObject
                        var n = Field(d, "m_Name");
                        if (n != null) goName[d.Id] = Cut(Unquote(n), NameMax);
                        break;
                    case 4:   // Transform
                    case 224: // RectTransform
                        var go = Ref(Field(d, "m_GameObject"));
                        if (go != null) { transformGo[d.Id] = go; goTransform[go] = d.Id; }
                        var father = Ref(Field(d, "m_Father"));
                        if (father != null && father != "0") transformParent[d.Id] = father;
                        break;
                    case 114: // MonoBehaviour
                        var owner = Ref(Field(d, "m_GameObject"));
                        if (owner == null) break;
                        behaviourGo[d.Id] = owner;
                        var script = Field(d, "m_Script");
                        var g = script == null ? null : ScriptGuid.Match(script);
                        if (g != null && g.Success) behaviourScript[d.Id] = g.Groups[1].Value.ToLowerInvariant();
                        behaviours.Add(d);
                        var t = FieldText(d, "m_text") ?? FieldText(d, "m_Text");
                        if (t != null && !goText.ContainsKey(owner))
                        {
                            var label = t.Trim();
                            if (label.Length > 0) goText[owner] = Cut(label, LabelMax);
                        }
                        break;
                }
            }
            // a GameObject that is itself a stripped stand-in may carry an override Button: its
            // transform is the instance's
            foreach (var kv in strippedInstance)
                if (!goTransform.ContainsKey(kv.Key) && instanceTransform.TryGetValue(kv.Value, out var st) && !transformGo.ContainsKey(st))
                { goTransform[kv.Key] = st; transformGo[st] = kv.Key; }

            var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var kv in transformParent)
            {
                if (!children.TryGetValue(kv.Value, out var list)) children[kv.Value] = list = new List<string>();
                list.Add(kv.Key);
            }

            foreach (var b in behaviours)
            {
                var clickAt = IndexOfKey(b, "m_OnClick");
                if (clickAt < 0) continue;
                var go = behaviourGo[b.Id];
                var row = new ExportUiRow
                {
                    File = file,
                    Name = goName.TryGetValue(go, out var nm) ? nm : "",
                    Path = Cut(PathOf(go, goName, goTransform, transformGo, transformParent), PathMax),
                    Component = behaviourScript.TryGetValue(b.Id, out var sg) ? scriptName(sg) : null,
                    Label = LabelOf(go, goText, goTransform, transformGo, children),
                };
                foreach (var call in PersistentCalls(b, clickAt))
                {
                    var target = call.Target != null && behaviourScript.TryGetValue(call.Target, out var tg) ? scriptName(tg) : null;
                    target ??= call.TypeName;
                    var entry = target != null ? target + "." + call.Method : call.Method;
                    if (row.OnClick.Count < OnClickMax && !row.OnClick.Contains(entry)) row.OnClick.Add(entry);
                }
                rows.Add(row);
            }
            rows.Sort((x, y) => string.CompareOrdinal(x.Path, y.Path));
            return rows;
        }

        private static List<Doc> SplitDocs(string text)
        {
            var docs = new List<Doc>();
            Doc? cur = null;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.EndsWith("\r", StringComparison.Ordinal) ? raw.Substring(0, raw.Length - 1) : raw;
                var m = DocHeader.Match(line);
                if (m.Success)
                {
                    cur = new Doc { ClassId = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), Id = m.Groups[2].Value, Stripped = m.Groups[3].Success };
                    docs.Add(cur);
                    continue;
                }
                cur?.Lines.Add(line);
            }
            return docs;
        }

        /// <summary>A top-level field of the object (two-space indent), its raw value.</summary>
        private static string? Field(Doc d, string key)
        {
            var prefix = "  " + key + ":";
            foreach (var l in d.Lines)
                if (l.StartsWith(prefix, StringComparison.Ordinal))
                    return l.Substring(prefix.Length).Trim();
            return null;
        }

        private static int IndexOfKey(Doc d, string key)
        {
            var prefix = "  " + key + ":";
            for (var i = 0; i < d.Lines.Count; i++)
                if (d.Lines[i].StartsWith(prefix, StringComparison.Ordinal)) return i;
            return -1;
        }

        private static string? Ref(string? value)
        {
            if (value == null) return null;
            var m = FileIdRef.Match(value);
            return m.Success ? m.Groups[1].Value : null;
        }

        private sealed class Call
        {
            public string? Target;
            public string? TypeName;
            public string Method = "";
        }

        /// <summary>The persistent calls inside the `m_OnClick` block starting at <paramref name="at"/>.
        /// A call whose target is `{fileID: 0}` is dropped — Unity skips it at runtime (it is a
        /// listener whose object was deleted), so exporting it would name a click that does nothing.</summary>
        private static IEnumerable<Call> PersistentCalls(Doc d, int at)
        {
            Call? cur = null;
            for (var i = at + 1; i < d.Lines.Count; i++)
            {
                var l = d.Lines[i];
                // the block ends at the next top-level field
                if (l.Length > 2 && l[0] == ' ' && l[1] == ' ' && l[2] != ' ' && l[2] != '-') break;
                var t = l.TrimStart();
                if (t.StartsWith("- m_Target:", StringComparison.Ordinal) || t.StartsWith("m_Target:", StringComparison.Ordinal))
                {
                    if (cur != null && cur.Method.Length > 0 && cur.Target != "0") yield return cur;
                    cur = new Call { Target = Ref(t) };
                }
                else if (cur != null && t.StartsWith("m_TargetAssemblyTypeName:", StringComparison.Ordinal))
                {
                    // "Namespace.Type, Assembly" -> "Type"
                    var tn = Unquote(t.Substring("m_TargetAssemblyTypeName:".Length).Trim());
                    var comma = tn.IndexOf(',');
                    if (comma >= 0) tn = tn.Substring(0, comma);
                    var dot = tn.LastIndexOf('.');
                    if (dot >= 0) tn = tn.Substring(dot + 1);
                    if (tn.Length > 0) cur.TypeName = Cut(tn, NameMax);
                }
                else if (cur != null && t.StartsWith("m_MethodName:", StringComparison.Ordinal))
                    cur.Method = Cut(Unquote(t.Substring("m_MethodName:".Length).Trim()), NameMax);
            }
            if (cur != null && cur.Method.Length > 0 && cur.Target != "0") yield return cur;
        }

        /// <summary>The PrefabInstance's parent transform and the name its root was given.</summary>
        private static void ReadInstance(Doc d, Dictionary<string, string> parent, Dictionary<string, string> name)
        {
            for (var i = 0; i < d.Lines.Count; i++)
            {
                var t = d.Lines[i].TrimStart();
                if (t.StartsWith("m_TransformParent:", StringComparison.Ordinal))
                {
                    var r = Ref(t);
                    if (r != null) parent[d.Id] = r;
                }
                else if (t == "propertyPath: m_Name" && i + 1 < d.Lines.Count && !name.ContainsKey(d.Id))
                {
                    var v = d.Lines[i + 1].TrimStart();
                    if (v.StartsWith("value:", StringComparison.Ordinal))
                    {
                        var nm = Unquote(v.Substring("value:".Length).Trim());
                        if (nm.Length > 0) name[d.Id] = Cut(nm, NameMax);
                    }
                }
            }
        }

        /// <summary>A top-level TEXT field, joined across the continuation lines YAML folds a long or
        /// quoted scalar onto, with its quoting decoded (audit: `'100` for a label that ran over two
        /// lines).</summary>
        private static string? FieldText(Doc d, string key)
        {
            var at = IndexOfKey(d, key);
            if (at < 0) return null;
            var first = d.Lines[at].Substring(("  " + key + ":").Length).Trim();
            if (first.Length == 0) return null;
            var q = first[0];
            if (q != '"' && q != '\'')
            {
                return first;
            }
            var sb = new System.Text.StringBuilder(first);
            for (var i = at + 1; i < d.Lines.Count && !Closed(sb.ToString(), q); i++)
            {
                var l = d.Lines[i];
                if (l.Length > 2 && l[0] == ' ' && l[1] == ' ' && l[2] != ' ') break;
                sb.Append(' ').Append(l.Trim());
                if (sb.Length > LabelMax * 4) break;
            }
            // YAML folds a line break to a space and a blank line to a newline; a label is one line
            return Regex.Replace(DecodeQuoted(sb.ToString(), q), @"\s+", " ");
        }

        private static bool Closed(string v, char q)
        {
            if (v.Length < 2 || v[v.Length - 1] != q) return false;
            if (q == '\'')
            {
                // an odd run of trailing quotes closes it ('' is an escaped quote)
                var n = 0;
                for (var i = v.Length - 1; i > 0 && v[i] == '\''; i--) n++;
                return n % 2 == 1;
            }
            var back = 0;
            for (var i = v.Length - 2; i >= 0 && v[i] == '\\'; i--) back++;
            return back % 2 == 0;
        }

        private static string DecodeQuoted(string v, char q)
        {
            if (v.Length >= 2 && v[v.Length - 1] == q) v = v.Substring(1, v.Length - 2);
            else v = v.Substring(1);
            if (q == '\'') return v.Replace("''", "'");
            var sb = new System.Text.StringBuilder(v.Length);
            for (var i = 0; i < v.Length; i++)
            {
                var c = v[i];
                if (c != '\\' || i + 1 >= v.Length) { sb.Append(c); continue; }
                var e = v[++i];
                switch (e)
                {
                    case 'n': case 'r': case 't': sb.Append(' '); break;
                    case 'u' when i + 4 < v.Length && int.TryParse(v.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code):
                        sb.Append((char)code); i += 4; break;
                    default: sb.Append(e); break;
                }
            }
            return sb.ToString();
        }

        private static string PathOf(string go, Dictionary<string, string> goName, Dictionary<string, string> goTransform,
            Dictionary<string, string> transformGo, Dictionary<string, string> transformParent)
        {
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var cur = go;
            while (cur != null && seen.Add(cur))
            {
                names.Add(goName.TryGetValue(cur, out var n) ? n : "?");
                if (!goTransform.TryGetValue(cur, out var tr) || !transformParent.TryGetValue(tr, out var parent) ||
                    !transformGo.TryGetValue(parent, out var parentGo)) break;
                cur = parentGo;
            }
            names.Reverse();
            return string.Join("/", names);
        }

        private static string? LabelOf(string go, Dictionary<string, string> goText, Dictionary<string, string> goTransform,
            Dictionary<string, string> transformGo, Dictionary<string, List<string>> children)
        {
            // breadth-first: the button's own text, then the nearest child's
            var queue = new Queue<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            queue.Enqueue(go);
            while (queue.Count > 0)
            {
                var g = queue.Dequeue();
                if (!seen.Add(g)) continue;
                if (goText.TryGetValue(g, out var t)) return t;
                if (goTransform.TryGetValue(g, out var tr) && children.TryGetValue(tr, out var kids))
                    foreach (var k in kids)
                        if (transformGo.TryGetValue(k, out var kg)) queue.Enqueue(kg);
            }
            return null;
        }

        private static string Unquote(string v)
        {
            v = v.Trim();
            if (v.Length >= 2 && ((v[0] == '"' && v[v.Length - 1] == '"') || (v[0] == '\'' && v[v.Length - 1] == '\'')))
                v = v.Substring(1, v.Length - 2);
            return v.Replace("\\n", " ").Replace("\\\"", "\"");
        }

        private static string Cut(string v, int max) => v.Length <= max ? v : v.Substring(0, max);
    }
}
