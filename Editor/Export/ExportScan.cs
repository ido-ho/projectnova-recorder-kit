using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace ProjectNova.RecorderKit
{
    /// <summary>One raw row of `patterns.NNNN.jsonl` (spec §4). WHICH regex fired is a fact; what
    /// it MEANS is the hosted intake lane's call.</summary>
    public sealed class ExportPatternHit
    {
        public string File = "";
        public int Line;
        public string Pattern = "";
        public string Text = "";
        /// <summary>The regex's index in the table — used only to order hits on the same line
        /// deterministically. Not on the wire.</summary>
        internal int Order;

        public JObject ToJson() => new JObject
        {
            ["file"] = File,
            ["line"] = Line,
            ["pattern"] = Pattern,
            ["text"] = Text,
        };
    }

    /// <summary>What <see cref="ExportScan.Walk"/> may do. Every bound has a reason and every bound
    /// that BITES becomes an `errors` row — a walk that quietly stopped would describe a smaller
    /// game, which is the failure the whole export format exists to prevent.</summary>
    public sealed class ExportWalkLimits
    {
        /// <summary>Folders deeper than this are not entered.</summary>
        public int MaxDepth = 32;
        /// <summary>Files + folders visited, in total.</summary>
        public int MaxEntries = 500000;
        /// <summary>Folder NAMES never entered (case-insensitive).</summary>
        public HashSet<string> SkipDirNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>`.git` is the expensive one.</summary>
        public bool SkipDotDirs = true;
        /// <summary>Unity itself ignores a folder whose name ends in `~`.</summary>
        public bool SkipTildeDirs = true;
    }

    /// <summary>One thing <see cref="ExportScan.Walk"/> found.</summary>
    public sealed class ExportWalkItem
    {
        public string FullPath = "";
        /// <summary>Forward slashes, relative to the walk root. Empty for the root itself.</summary>
        public string Relative = "";
        public bool IsDirectory;
    }

    /// <summary>
    /// The parts of the export that are pure text and disk walking: the `.cs` pattern scan, the
    /// Addressables group YAML read, the ScriptableObject YAML read, and the doc/config sweep.
    ///
    /// They live apart from <see cref="ExportCollector"/> because they touch NO Unity API, which is
    /// what lets them be tested against a temp directory instead of against whatever happens to be
    /// in the test host's `Assets/`.
    /// </summary>
    public static class ExportScan
    {
        // ---- the one tree walk (spec §6; fresh-context audit K7, 2026-09-21) --------------
        //
        // EVERY tree walk in the export goes through here. The five that did not each had the same
        // two defects, measured on a probe project:
        //
        //  - THEY LEFT THE PROJECT. `Assets/shared` symlinked to a folder elsewhere on the disk and
        //    the export packed `config/Assets/shared/remote_config_SECRET.json` with the OUTSIDE
        //    file's contents — while TRUST.md promised "under the project root".
        //  - THEY DID NOT TERMINATE. One symlink cycle under `Assets/` and
        //    `Directory.GetFiles(…, AllDirectories)` ran past 120 SECONDS on the main thread.
        //
        // So: manual recursion, never follow a reparse point (symlink or junction), a depth cap and
        // an entry cap that both report themselves, and an ITERATOR so the collector can drive it
        // under its slice budget instead of disappearing into one blocking call.

        /// <summary>
        /// Files and folders under <paramref name="root"/>, symlink-safe and bounded. Directories
        /// are yielded BEFORE their contents so a caller pumping this can take a slice per folder.
        /// Never throws: an unreadable folder is skipped.
        /// </summary>
        /// <param name="links">Collects the walk-relative path of every directory or file that was
        /// skipped because it IS a reparse point. The walk refusing to follow one is only half the
        /// promise: the art passes take whatever Unity IMPORTED, and Unity follows a symlinked
        /// folder under `Assets/` — so the collector needs to know which paths those are, to stop
        /// reading their contents too (audit round 3, M2). Only a REAL link is listed; a folder
        /// whose attributes cannot be read at all is skipped, and is not called a symlink.</param>
        public static IEnumerable<ExportWalkItem> Walk(string root, ExportWalkLimits? limits = null,
            ICollection<string>? notes = null, ICollection<string>? links = null)
        {
            var lim = limits ?? new ExportWalkLimits();
            var canonicalRoot = CanonicalDir(root);
            if (canonicalRoot == null) yield break;

            var stack = new Stack<KeyValuePair<string, int>>();
            stack.Push(new KeyValuePair<string, int>(canonicalRoot, 0));
            var entries = 0;
            var capped = false;
            var tooDeep = 0;

            while (stack.Count > 0 && !capped)
            {
                var top = stack.Pop();
                var dir = top.Key;
                var depth = top.Value;

                if (++entries > lim.MaxEntries) { capped = true; break; }
                yield return new ExportWalkItem
                {
                    FullPath = dir,
                    Relative = Relative(canonicalRoot, dir),
                    IsDirectory = true,
                };

                var files = SafeChildren(dir, wantFiles: true);
                foreach (var f in files)
                {
                    if (++entries > lim.MaxEntries) { capped = true; break; }
                    if (IsLinkOrUnreadable(f, out var fileIsLink))    // a symlinked FILE is not ours to read
                    {
                        if (fileIsLink && links != null) links.Add(Relative(canonicalRoot, f));
                        continue;
                    }
                    var rel = Relative(canonicalRoot, f);
                    if (rel.Length == 0) continue;           // it is not under the root after all
                    yield return new ExportWalkItem { FullPath = f, Relative = rel, IsDirectory = false };
                }
                if (capped) break;

                var subs = SafeChildren(dir, wantFiles: false);
                // Pushed in reverse so the pop order is the ordinal one: two exports of one project
                // must produce the same bytes.
                for (var i = subs.Length - 1; i >= 0; i--)
                {
                    var sub = subs[i];
                    var name = Path.GetFileName(sub);
                    if (string.IsNullOrEmpty(name)) continue;
                    if (lim.SkipDotDirs && name[0] == '.') continue;
                    if (lim.SkipTildeDirs && name[name.Length - 1] == '~') continue;
                    if (lim.SkipDirNames.Contains(name)) continue;
                    // THE LINE THAT MAKES THE CYCLE AND THE ESCAPE IMPOSSIBLE.
                    if (IsLinkOrUnreadable(sub, out var subIsLink))
                    {
                        if (subIsLink && links != null) links.Add(Relative(canonicalRoot, sub));
                        continue;
                    }
                    if (Relative(canonicalRoot, sub).Length == 0) continue;
                    if (depth + 1 > lim.MaxDepth) { tooDeep++; continue; }
                    stack.Push(new KeyValuePair<string, int>(sub, depth + 1));
                }
            }

            if (notes != null && capped)
                notes.Add("the folder walk under '" + root + "' stopped at " +
                          lim.MaxEntries.ToString(CultureInfo.InvariantCulture) +
                          " entries; the rest of that tree was not read");
            if (notes != null && tooDeep > 0)
                notes.Add(tooDeep.ToString(CultureInfo.InvariantCulture) + " folder(s) under '" + root +
                          "' are deeper than " + lim.MaxDepth.ToString(CultureInfo.InvariantCulture) +
                          " levels and were not read");
        }

        /// <summary>True for a symlink or junction — and for anything whose attributes cannot be
        /// read at all, which a dangling link is. Both are "do not follow". The two are told apart
        /// by <paramref name="isLink"/>, because only the first is a thing the export may CALL a
        /// symlink in its `errors`: "this folder is a symlink" and "this folder could not be read"
        /// are different sentences and only one of them is a fact here.</summary>
        private static bool IsLinkOrUnreadable(string path, out bool isLink)
        {
            try
            {
                isLink = (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
                return isLink;
            }
            catch (Exception)
            {
                isLink = false;
                return true;
            }
        }

        private static bool IsLinkOrUnreadable(string path) => IsLinkOrUnreadable(path, out _);

        private static string? CanonicalDir(string root)
        {
            if (string.IsNullOrEmpty(root)) return null;
            try
            {
                if (!Directory.Exists(root)) return null;
                var full = Path.GetFullPath(root);
                return full.TrimEnd(Path.DirectorySeparatorChar, '/');
            }
            catch (Exception) { return null; }
        }

        private static string[] SafeChildren(string dir, bool wantFiles)
        {
            string[] found;
            try { found = wantFiles ? Directory.GetFiles(dir) : Directory.GetDirectories(dir); }
            catch (Exception) { return Array.Empty<string>(); }
            Array.Sort(found, StringComparer.Ordinal);
            return found;
        }

        // ---- patterns (spec §4) ----------------------------------------------------------

        /// <summary>
        /// Runs the table over ONE LINE AT A TIME, so a hit's line number is exact by construction
        /// and no pattern can match across a break. Hits come back ordered by line, then by the
        /// regex's position in the table — that is the order they are produced in, so nothing is
        /// sorted afterwards.
        ///
        /// <paramref name="notes"/> collects what this file could NOT be told about: lines past
        /// <see cref="ExportFormat.PatternLineMaxChars"/>, and any pattern that hit its match
        /// timeout. They become `errors` rows. Silence is the one answer a scan may not give.
        /// </summary>
        public static List<ExportPatternHit> ScanText(string file, string? text,
            ICollection<string>? notes = null)
        {
            var hits = new List<ExportPatternHit>();
            if (string.IsNullOrEmpty(text)) return hits;
            var body = text!;

            var line = 0;
            var from = 0;
            var longLines = 0;
            var timeouts = 0;
            while (from <= body.Length)
            {
                var nl = body.IndexOf('\n', from);
                var end = nl < 0 ? body.Length : nl;
                var len = end - from;
                if (len > 0 && body[end - 1] == '\r') len--;   // a CRLF file's `\r` is not content
                line++;

                if (len > ExportFormat.PatternLineMaxChars) longLines++;
                else if (len > 0) ScanLine(file, body.Substring(from, len), line, hits, ref timeouts);

                if (nl < 0) break;
                from = nl + 1;
            }

            if (notes != null && longLines > 0)
                notes.Add(file + ": " + longLines.ToString(CultureInfo.InvariantCulture) +
                          " line(s) longer than " +
                          ExportFormat.PatternLineMaxChars.ToString(CultureInfo.InvariantCulture) +
                          " characters were not scanned");
            if (notes != null && timeouts > 0)
                notes.Add(file + ": " + timeouts.ToString(CultureInfo.InvariantCulture) +
                          " pattern match(es) timed out after " +
                          ((int)ExportPattern.MatchTimeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) +
                          " ms and were not scanned");
            return hits;
        }

        private static void ScanLine(string file, string lineText, int lineNumber,
            List<ExportPatternHit> hits, ref int timeouts)
        {
            var shown = "";
            var haveShown = false;
            for (var pi = 0; pi < ExportFormat.Patterns.Count; pi++)
            {
                var p = ExportFormat.Patterns[pi];
                bool matched;
                try { matched = p.Regex.IsMatch(lineText); }
                catch (RegexMatchTimeoutException) { timeouts++; continue; }
                if (!matched) continue;

                if (!haveShown)
                {
                    var t = lineText.Trim();
                    if (t.Length > ExportFormat.PatternTextMax) t = t.Substring(0, ExportFormat.PatternTextMax);
                    shown = t;
                    haveShown = true;
                }
                hits.Add(new ExportPatternHit
                {
                    File = file,
                    Line = lineNumber,
                    Pattern = p.Id,
                    Text = shown,
                    Order = pi,
                });
            }
        }

        // ---- Addressables (spec §4 build.json) -------------------------------------------

        // The group's own guid is a plain mapping key (`  m_GUID: …`); every ENTRY's is a YAML
        // LIST item (`  - m_GUID: …`). A pattern that only allows whitespace before the key finds
        // the group and none of its contents — which reads as an empty Addressables setup.
        // `[ \t]` rather than `\s`: under Multiline, `\s*` would span the newline into the next line.
        private static readonly Regex GuidLine =
            new Regex(@"^[ \t]*(?:-[ \t]*)?m_GUID:[ \t]*([0-9a-fA-F]{32})[ \t]*\r?$",
                RegexOptions.Multiline | RegexOptions.CultureInvariant);

        /// <summary>
        /// Every `m_GUID:` in an Addressables group `.asset`, read off the YAML on disk — there is
        /// deliberately NO compile-time dependency on the Addressables package, the same rule that
        /// keeps the kit compiling in a project with no TextMeshPro.
        ///
        /// A group file carries the GROUP's own `m_GUID` as well as each entry's. Both are reported:
        /// separating them would be a judgement about what a line MEANS, made in the one place the
        /// hosted side cannot re-run.
        /// </summary>
        public static List<string> ReadAddressableGuids(string? yaml)
        {
            var found = new List<string>();
            if (string.IsNullOrEmpty(yaml)) return found;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var matches = GuidLine.Matches(yaml!);
            for (var i = 0; i < matches.Count; i++)
            {
                var g = matches[i].Groups[1].Value;
                if (seen.Add(g)) found.Add(g);
            }
            found.Sort(StringComparer.Ordinal);
            return found;
        }

        // ---- asset YAML (spec §4 identity.json; fresh-context audit K6, 2026-09-21) -------
        //
        // THE EXPORT MUST NOT EXECUTE THE STUDIO'S CODE. `AssetDatabase.LoadMainAssetAtPath` on a
        // ScriptableObject runs its `Awake`, `OnEnable` and `OnValidate` (measured: 1 each), and
        // the memory sweep afterwards runs `OnDisable`. A read that runs arbitrary game code inside
        // a studio's editor is not a read, and TRUST.md never said it happened. So the identity
        // facts that used to come from a loaded object are read off the asset's YAML TEXT instead.

        /// <summary>A Unity asset file serialised as text starts with the YAML directive. A project
        /// set to BINARY (or Mixed) serialisation does not, and there is nothing here to read — a
        /// fact the caller reports rather than silently emitting no colours.</summary>
        public static bool LooksLikeYaml(string? text) =>
            text != null && text.StartsWith("%YAML", StringComparison.Ordinal);

        /// <summary>
        /// Every serialised colour in an asset's YAML, as (name, `#RRGGBBAA`). `name` is the YAML
        /// key that holds the colour, prefixed by its parent keys — the document's own root mapping
        /// (`MonoBehaviour:`) is not a field and is left off. Both shapes Unity writes are read:
        /// a `Color` (`{r: 1, g: 0, b: 0, a: 1}`) and a `Color32` (`{rgba: 4278190335}`).
        ///
        /// The hex is computed the way `ColorUtility.ToHtmlStringRGBA` computes it — the multiply
        /// in FLOAT, rounded half-to-even — so it is identical to what loading the asset produced.
        /// </summary>
        public static List<KeyValuePair<string, string>> ReadYamlColors(string? yaml, int max,
            ICollection<string>? notes = null)
        {
            var found = new List<KeyValuePair<string, string>>();
            if (string.IsNullOrEmpty(yaml) || max <= 0) return found;
            var body = yaml!;
            var path = new List<YamlKey>();
            var outerList = new YamlKey(-1, "");
            var sawRoot = false;
            var inDocument = false;
            var unclosed = 0;

            var from = 0;
            while (from <= body.Length && found.Count < max)
            {
                var line = NextLine(body, ref from);

                // A NEW DOCUMENT. A `.asset` file is often several: a TMP font is its
                // MonoBehaviour, then the Material and the atlas Texture2D it owns. Read as ONE
                // document every TMP font came back as a `colorObjects` row full of SHADER colours
                // (`m_SavedProperties.m_Colors._GlowColor`) — facts about a Unity shader reported
                // as facts about the studio's brand (audit S3, 2026-09-21).
                if (IsDocumentStart(line))
                {
                    path.Clear();
                    outerList.ListCount = 0;
                    sawRoot = false;
                    inDocument = false;
                    continue;
                }

                var i = 0;
                while (i < line.Length && line[i] == ' ') i++;
                // A list item's `- ` is structure, not part of the name. Its position is what the
                // ITEM is indented at; what follows it is the item's own content.
                var markerAt = -1;
                if (i < line.Length && line[i] == '-' && (i + 1 == line.Length || line[i + 1] == ' '))
                {
                    markerAt = i;
                    while (i + 1 < line.Length && line[i] == '-' && line[i + 1] == ' ') i += 2;
                }
                if (i >= line.Length) continue;
                if (line[i] == '#' || line[i] == '%' || line[i] == '-') continue;

                var isFlow = line[i] == '{';
                var indent = i;
                string key = "";
                if (!isFlow)
                {
                    var colon = line.IndexOf(':', i);
                    if (colon < 0) continue;
                    key = line.Substring(i, colon - i).Trim();
                    if (key.Length == 0) continue;
                    // THE ROOT MAPPING DECIDES WHETHER THIS DOCUMENT IS OURS.
                    if (!sawRoot)
                    {
                        sawRoot = true;
                        inDocument = indent == 0 &&
                                     string.Equals(key, MonoBehaviourRoot, StringComparison.Ordinal);
                    }
                }
                if (!inDocument) continue;

                // THE LIST COUNTER LIVES ON THE PARENT, so `rows[0]` restarts under the next
                // parent instead of continuing from the previous list's count. Popping the parent
                // resets it for free. Every item counts, colour or not, so the index is the
                // POSITION in the list.
                var index = -1;
                if (markerAt >= 0)
                {
                    while (path.Count > 0 && path[path.Count - 1].Indent > markerAt)
                        path.RemoveAt(path.Count - 1);
                    var parent = path.Count > 0 ? path[path.Count - 1] : outerList;
                    index = parent.ListCount++;
                }

                if (isFlow)
                {
                    // A BARE flow mapping under a list marker: one element of a `Color[]` or a
                    // `List<Color>` — the shape a brand-palette asset is made of, and the one the
                    // reader dropped silently (audit S3). It has no key of its own, so it is named
                    // by the key that ENCLOSES it plus its index.
                    if (index < 0) continue;
                    var item = Joined(body, ref from, line.Substring(i).Trim(), ref unclosed);
                    var itemHex = ColorHexFromYaml(item);
                    if (itemHex == null) continue;
                    var stem = JoinStack(path);
                    found.Add(new KeyValuePair<string, string>(
                        stem + "[" + index.ToString(CultureInfo.InvariantCulture) + "]", itemHex));
                    continue;
                }

                while (path.Count > 0 && path[path.Count - 1].Indent >= indent) path.RemoveAt(path.Count - 1);

                var rest = ValueOf(line, indent);
                if (rest.Length == 0)
                {
                    path.Add(new YamlKey(indent, key));
                    continue;
                }
                var hex = ColorHexFromYaml(Joined(body, ref from, rest, ref unclosed));
                if (hex == null) continue;
                found.Add(new KeyValuePair<string, string>(JoinPath(path, key), hex));
            }

            if (notes != null && unclosed > 0)
                notes.Add(unclosed.ToString(CultureInfo.InvariantCulture) +
                          " flow mapping(s) did not close within " +
                          WrappedMaxLines.ToString(CultureInfo.InvariantCulture) +
                          " lines and were not read");
            // A CAP THAT BITES IS NEVER SILENT (M7). `from <= body.Length` is what the loop uses to
            // mean "there is another line", so a read that ended with text still under it stopped
            // because of the cap — and a file whose LAST line held the max-th colour did not.
            if (notes != null && found.Count >= max && from < body.Length)
                notes.Add("the colour cap of " + max.ToString(CultureInfo.InvariantCulture) +
                          " was reached; the rest of the asset was not read for colours");
            return found;
        }

        /// <summary>The root mapping of a ScriptableObject document. Unity writes every other
        /// document in the same file under its own engine type (`Material:`, `Texture2D:`).</summary>
        private const string MonoBehaviourRoot = "MonoBehaviour";

        /// <summary>How many continuation lines one flow mapping may span before the reader gives
        /// up on it. Unity wraps a long `{…}` at about 80 columns, so a Color is one line and the
        /// longest thing Unity actually wraps is a handful; past this the text is left alone (the
        /// lines are read as ordinary keys again) and the give-up is COUNTED, never silent.</summary>
        private const int WrappedMaxLines = 8;

        private static bool IsDocumentStart(string line) =>
            line.Length >= 3 && line[0] == '-' && line[1] == '-' && line[2] == '-';

        /// <summary>A flow mapping Unity WRAPPED across lines, put back together. Anything that is
        /// not an unbalanced `{` comes back untouched, so this costs nothing on the common
        /// case.</summary>
        private static string Joined(string body, ref int from, string text, ref int unclosed)
        {
            if (text.Length == 0 || text[0] != '{' || Balance(text) <= 0) return text;
            var save = from;
            var sb = new StringBuilder(text);
            for (var n = 0; n < WrappedMaxLines && from <= body.Length; n++)
            {
                sb.Append(' ').Append(NextLine(body, ref from).Trim());
                if (Balance(sb.ToString()) <= 0) return sb.ToString();
            }
            from = save;          // not a wrapped mapping after all — leave those lines to the loop
            unclosed++;
            return "";
        }

        private static int Balance(string s)
        {
            var depth = 0;
            foreach (var c in s)
            {
                if (c == '{') depth++;
                else if (c == '}') depth--;
            }
            return depth;
        }

        /// <summary>One key on the path from the document root to the value being read, and how
        /// many list items have been seen UNDER it.</summary>
        private sealed class YamlKey
        {
            internal readonly int Indent;
            internal readonly string Key;
            internal int ListCount;

            internal YamlKey(int indent, string key)
            {
                Indent = indent;
                Key = key;
            }
        }

        /// <summary>The scalar under <paramref name="parentKey"/> named <paramref name="childKey"/>
        /// — `m_FaceInfo` / `m_FamilyName` in a TMP font asset. Null when either is absent.</summary>
        public static string? ReadYamlNestedValue(string? yaml, string parentKey, string childKey)
        {
            if (string.IsNullOrEmpty(yaml)) return null;
            using (var reader = new StringReader(yaml!))
                return ReadYamlNestedValue(reader, parentKey, childKey);
        }

        /// <summary>How much of the file is held at once. Sixteen KiB of UTF-16, refilled.</summary>
        private const int ReadBufferChars = 8192;

        /// <summary>
        /// How much of ONE line is kept. A TMP font's atlas is a single `_typelessdata:` line of
        /// 8.4 M characters (a 4096-square atlas is ~33 M), and the rest of such a line is thrown
        /// away as it streams past — never built. Everything this reader looks at is a key, an
        /// indent and a short scalar, so 4,096 CHARACTERS is a very wide margin; a value longer than
        /// this comes back cut there, and a real font family name is a few dozen characters.
        /// (`identity.fonts[].familyName` goes to the wire as read, so this is also the only bound
        /// on it — there was none before.)
        /// </summary>
        private const int LineKeepChars = 4096;

        /// <summary>
        /// THE SAME READ, WITHOUT THE FILE IN MEMORY AND WITHOUT A BYTE BUDGET IN FRONT OF IT.
        ///
        /// Unity writes a file's documents in ascending SIGNED fileID order, and a TMP font's atlas
        /// `Texture2D` very often has a NEGATIVE one — so the multi-megabyte `_typelessdata:` line
        /// comes BEFORE the `&amp;11400000` MonoBehaviour and `m_FaceInfo` sits after all of it.
        /// Measured over the two real games on this machine: of 61 `.asset` files holding an
        /// `m_FaceInfo:` (48 with a non-empty family; the other 13 are TMP sprite assets, correctly
        /// empty), a 256 KiB head budget read 26 and ran out on 22. "`m_FamilyName` is on about
        /// line 25" was the premise of that budget, and it is false (audit round 4, S1).
        ///
        /// So there is no byte budget: the scan is bounded by the FILE, which is linear and cheap
        /// (the auditor measured 13 ms for one 7 MiB hex line), it stops at the first match, and it
        /// never holds more than <see cref="ReadBufferChars"/> plus <see cref="LineKeepChars"/>
        /// characters — the over-long remainder of a line is skipped, not allocated.
        ///
        /// A READ THAT FAILS THROWS. It used to be caught here and answered as a null family name,
        /// which is a silent null for a fact nobody established (invariant 50); the caller catches
        /// it and writes one `errors` row.
        ///
        /// ONE BEHAVIOUR CHANGE BESIDES: an EMPTY `childKey` scalar used to end the read and answer
        /// null there; it now keeps reading, so a later document holding a real value wins. A TMP
        /// SPRITE asset — 13 of the 61 measured — has an empty `m_FamilyName` and no other, so it
        /// still answers null, having read the (small) file to the end.
        /// </summary>
        public static string? ReadYamlNestedValue(TextReader? reader, string parentKey, string childKey)
        {
            if (reader == null) return null;
            var parentIndent = -1;
            var buf = new char[ReadBufferChars];
            var line = new StringBuilder(LineKeepChars);
            var n = 0;
            var at = 0;
            while (true)
            {
                if (at >= n)
                {
                    n = reader.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    at = 0;
                }
                var nl = Array.IndexOf(buf, '\n', at, n - at);
                var end = nl < 0 ? n : nl;
                var room = LineKeepChars - line.Length;
                if (room > 0 && end > at) line.Append(buf, at, Math.Min(room, end - at));
                at = nl < 0 ? n : nl + 1;
                if (nl < 0) continue;                       // the line runs into the next chunk
                var value = NestedValueOfLine(line, parentKey, childKey, ref parentIndent);
                if (value != null) return value;
                line.Length = 0;
            }
            // A last line with no newline after it is still a line.
            return line.Length > 0 ? NestedValueOfLine(line, parentKey, childKey, ref parentIndent) : null;
        }

        /// <summary>One line of <see cref="ReadYamlNestedValue"/>: it either IS the value, or it
        /// moves the parent cursor. Null means "keep reading".</summary>
        private static string? NestedValueOfLine(StringBuilder buffer, string parentKey, string childKey,
            ref int parentIndent)
        {
            var len = buffer.Length;
            if (len > 0 && buffer[len - 1] == '\r') len--;
            if (len == 0) return null;
            var line = buffer.ToString(0, len);

            int indent;
            var key = KeyOf(line, out indent);
            if (key == null) return null;

            if (parentIndent >= 0 && indent <= parentIndent) parentIndent = -1;
            if (parentIndent < 0)
            {
                if (string.Equals(key, parentKey, StringComparison.Ordinal)) parentIndent = indent;
                return null;
            }
            if (!string.Equals(key, childKey, StringComparison.Ordinal)) return null;

            var value = ValueOf(line, indent);
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') &&
                value[value.Length - 1] == value[0])
                value = value.Substring(1, value.Length - 2);
            return value.Length == 0 ? null : value;
        }

        private static string NextLine(string body, ref int from)
        {
            var nl = body.IndexOf('\n', from);
            var end = nl < 0 ? body.Length : nl;
            var len = end - from;
            if (len > 0 && body[end - 1] == '\r') len--;
            var line = body.Substring(from, len);
            from = nl < 0 ? body.Length + 1 : nl + 1;
            return line;
        }

        /// <summary>The mapping key on a YAML line and the indent it sits at, or null when the line
        /// carries none. A list item's `- ` is structure, not part of the name, and counts toward
        /// the indent so `- color:` nests under `entries:`.</summary>
        private static string? KeyOf(string line, out int indent)
        {
            indent = 0;
            var i = 0;
            while (i < line.Length && line[i] == ' ') i++;
            while (i + 1 < line.Length && line[i] == '-' && line[i + 1] == ' ') i += 2;
            if (i >= line.Length) return null;
            if (line[i] == '#' || line[i] == '%' || line[i] == '-') return null;   // comment, directive, `---`
            var colon = line.IndexOf(':', i);
            if (colon < 0) return null;
            var key = line.Substring(i, colon - i).Trim();
            if (key.Length == 0) return null;
            indent = i;
            return key;
        }

        /// <summary>Everything after the key's colon, trimmed. Empty for a mapping parent.</summary>
        private static string ValueOf(string line, int indent)
        {
            var colon = line.IndexOf(':', indent);
            if (colon < 0) return "";
            return line.Substring(colon + 1).Trim();
        }

        private static string JoinPath(List<YamlKey> path, string key)
        {
            var stem = JoinStack(path);
            return stem.Length == 0 ? key : stem + "." + key;
        }

        /// <summary>Every key on the path, joined — the name of the thing the path itself points
        /// at. The document's own root mapping (`MonoBehaviour:`, at indent 0) is not a field and
        /// is left off.</summary>
        private static string JoinStack(List<YamlKey> path)
        {
            var sb = new StringBuilder();
            foreach (var k in path)
            {
                if (k.Indent <= 0) continue;
                if (sb.Length > 0) sb.Append('.');
                sb.Append(k.Key);
            }
            return sb.ToString();
        }

        private static string? ColorHexFromYaml(string rest)
        {
            if (rest.Length < 3 || rest[0] != '{') return null;
            var close = rest.IndexOf('}');
            if (close < 1) return null;
            var inner = rest.Substring(1, close - 1);

            float r = 0f, g = 0f, b = 0f, a = 0f;
            var seen = 0;
            foreach (var pair in inner.Split(','))
            {
                var colon = pair.IndexOf(':');
                if (colon < 0) return null;
                var name = pair.Substring(0, colon).Trim();
                var text = pair.Substring(colon + 1).Trim();

                // Color32 is one packed uint, little-endian r,g,b,a.
                if (string.Equals(name, "rgba", StringComparison.Ordinal))
                {
                    ulong packed;
                    if (!ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out packed))
                        return null;
                    return "#" + Hex2((int)(packed & 0xFF)) + Hex2((int)((packed >> 8) & 0xFF)) +
                           Hex2((int)((packed >> 16) & 0xFF)) + Hex2((int)((packed >> 24) & 0xFF));
                }

                float v;
                if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return null;
                if (name == "r") { r = v; seen |= 1; }
                else if (name == "g") { g = v; seen |= 2; }
                else if (name == "b") { b = v; seen |= 4; }
                else if (name == "a") { a = v; seen |= 8; }
                else return null;    // a Vector4 is {x:, y:, z:, w:} — not a colour
            }
            if (seen != 15) return null;
            return "#" + Byte255(r) + Byte255(g) + Byte255(b) + Byte255(a);
        }

        private static string Byte255(float v) =>
            Hex2((int)Math.Round((double)(v * 255f), MidpointRounding.ToEven));

        private static string Hex2(int n)
        {
            if (n < 0) n = 0;
            if (n > 255) n = 255;
            return n.ToString("X2", CultureInfo.InvariantCulture);
        }

        // ---- docs and config (spec §6) ---------------------------------------------------

        /// <summary>Folder names never walked when hunting for `remote_config*.json`.</summary>
        private static readonly string[] ConfigSkipDirNames =
            { "Library", "Temp", "Logs", "obj", "Packages", "node_modules" };

        private static readonly string[] DocDirNames = { "docs", "Documentation" };

        public static ExportWalkLimits ConfigWalkLimits()
        {
            var lim = new ExportWalkLimits();
            foreach (var n in ConfigSkipDirNames) lim.SkipDirNames.Add(n);
            return lim;
        }

        public static ExportWalkLimits DocWalkLimits() => new ExportWalkLimits();

        /// <summary>
        /// `.md` and `.txt` under a project-root `docs/`, `Docs/` or `Documentation/`, plus
        /// `README*` sitting at the project root. Project-relative, forward slashes. LAZY, so the
        /// collector can take a slice per folder instead of blocking on the whole tree.
        ///
        /// The doc folders are matched against the directories that ACTUALLY EXIST rather than
        /// probed by name. The old code probed all three spellings and de-duplicated the FILES
        /// case-insensitively — which on a case-insensitive volume was the only thing stopping
        /// `docs/` and `Docs/` (one and the same folder there) being read twice, and on a
        /// case-sensitive one silently DROPPED `Docs/a.md` because `docs/a.md` had been seen
        /// (fresh-context audit, 2026-09-21). Matching real folders is right on both.
        /// </summary>
        public static IEnumerable<string?> ScanDocFiles(string projectRoot, ExportWalkLimits? limits = null,
            ICollection<string>? notes = null)
        {
            if (CanonicalDir(projectRoot) == null) yield break;
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var dir in DocDirectories(projectRoot))
            {
                var prefix = Relative(projectRoot, dir);
                if (prefix.Length == 0) continue;
                foreach (var item in Walk(dir, limits ?? DocWalkLimits(), notes))
                {
                    // NULL IS "STILL WALKING" (audit N2). The collector can only take a slice on
                    // something this YIELDS, and yielding matches only meant the whole walk of a
                    // studio's tree ran between two slices — 933 ms and 1,858 ms measured on two
                    // real projects, with the editor's main thread inside it the whole time.
                    if (item.IsDirectory) { yield return null; continue; }
                    var ext = Path.GetExtension(item.FullPath);
                    if (!string.Equals(ext, ".md", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(ext, ".txt", StringComparison.OrdinalIgnoreCase))
                    {
                        yield return null;
                        continue;
                    }
                    var rel = prefix + "/" + item.Relative;
                    yield return seen.Add(rel) ? rel : null;
                }
            }

            // spec §8.8: the studio's own notes for AI tools sit at the root next to README.
            foreach (var pattern in new[] { "README*", "AGENTS.md", "CLAUDE.md", "GEMINI.md" })
                foreach (var f in SafeTopFiles(projectRoot, pattern))
                {
                    if (IsLinkOrUnreadable(f)) { yield return null; continue; }
                    var rel = Relative(projectRoot, f);
                    yield return rel.Length > 0 && seen.Add(rel) ? rel : null;
                }
        }

        private static readonly string[] StringsDirMarkers = { "locali", "translat", "i18n", "l10n" };
        private static readonly string[] StringsExtensions = { ".json", ".csv", ".tsv", ".txt", ".xml", ".po", ".yaml", ".yml" };
        private static readonly Regex SourceLanguageToken =
            new Regex(@"(^|[_\-. ])(en|eng|english|en-us|en_us|base|source)([_\-. ]|$)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// Spec §8.6 — is this project-relative path the game's SOURCE-LANGUAGE strings file? The
        /// path must say localization (`locali`, `translat`, `i18n`, `l10n`), the extension must be a
        /// text format, and the FILE NAME must carry an English/source token as a whole word — so
        /// `translations_en.json` is in and `translations_ru.json`, `Translation.cs` are out.
        /// </summary>
        public static bool IsStringsFile(string relPath)
        {
            if (string.IsNullOrEmpty(relPath)) return false;
            var lower = relPath.Replace('\\', '/').ToLowerInvariant();
            var ext = Path.GetExtension(lower);
            if (Array.IndexOf(StringsExtensions, ext) < 0) return false;
            var hasMarker = false;
            foreach (var m in StringsDirMarkers) if (lower.Contains(m)) { hasMarker = true; break; }
            if (!hasMarker) return false;
            // an editor tool's own UI strings (`…/Editor/…`) are not the game's words (audit: a
            // localization tool's en.json matched on RL)
            if (lower.Contains("/editor/")) return false;
            var name = Path.GetFileNameWithoutExtension(lower);
            return SourceLanguageToken.IsMatch(name);
        }

        /// <summary>Files named `remote_config*.json` anywhere under the project root, skipping
        /// `Library/`, `Temp/`, `Logs/`, `obj/`, `Packages/`, `node_modules/`, any dot-folder
        /// (`.git` is the expensive one) and anything reached through a symlink. LAZY.</summary>
        public static IEnumerable<string?> ScanConfigFiles(string projectRoot, ExportWalkLimits? limits = null,
            ICollection<string>? notes = null)
        {
            if (CanonicalDir(projectRoot) == null) yield break;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in Walk(projectRoot, limits ?? ConfigWalkLimits(), notes))
            {
                // …and null here for the same reason: this one walks the WHOLE project root.
                if (item.IsDirectory) { yield return null; continue; }
                var name = Path.GetFileName(item.FullPath);
                if (!name.StartsWith("remote_config", StringComparison.OrdinalIgnoreCase) ||
                    !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    yield return null;
                    continue;
                }
                yield return seen.Add(item.Relative) ? item.Relative : null;
            }
        }

        /// <summary>Sorted ordinal, so two exports of the same project match.</summary>
        public static List<string> FindDocFiles(string projectRoot) => Sorted(ScanDocFiles(projectRoot));

        /// <summary>Sorted ordinal, so two exports of the same project match.</summary>
        public static List<string> FindConfigFiles(string projectRoot) => Sorted(ScanConfigFiles(projectRoot));

        /// <summary>The MATCHES only — the "still walking" markers are not files.</summary>
        private static List<string> Sorted(IEnumerable<string?> items)
        {
            var list = new List<string>();
            foreach (var i in items)
                if (i != null) list.Add(i);
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        /// <summary>The project-root folders that ARE a doc folder, by their real on-disk names.</summary>
        private static List<string> DocDirectories(string projectRoot)
        {
            var found = new List<string>();
            string[] subs;
            try { subs = Directory.GetDirectories(projectRoot); }
            catch (Exception) { return found; }
            Array.Sort(subs, StringComparer.Ordinal);
            foreach (var d in subs)
            {
                var name = Path.GetFileName(d);
                if (string.IsNullOrEmpty(name)) continue;
                if (IsLinkOrUnreadable(d)) continue;
                foreach (var want in DocDirNames)
                    if (string.Equals(name, want, StringComparison.OrdinalIgnoreCase)) { found.Add(d); break; }
            }
            return found;
        }

        private static IEnumerable<string> SafeTopFiles(string dir, string pattern)
        {
            try { return Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly); }
            catch (Exception) { return Array.Empty<string>(); }
        }

        /// <summary>A path under <paramref name="root"/> as a forward-slash relative path. Empty
        /// when it is not under the root.</summary>
        public static string Relative(string root, string full)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(full)) return "";
            var r = root.Replace('\\', '/').TrimEnd('/');
            var f = full.Replace('\\', '/');
            if (!f.StartsWith(r + "/", StringComparison.Ordinal)) return "";
            return f.Substring(r.Length + 1);
        }
    }
}
