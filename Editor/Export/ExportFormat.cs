using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace ProjectNova.RecorderKit
{
    /// <summary>One entry of the export's pattern table (spec §4). The id is a FACT — which regex
    /// fired. What it MEANS is the hosted intake lane's call, never the kit's.</summary>
    public sealed class ExportPattern
    {
        public string Id { get; }
        public Regex Regex { get; }

        /// <summary>How long ONE pattern may spend on ONE line before it is abandoned. A regex that
        /// backtracks pathologically over a generated or minified line must not freeze the studio's
        /// editor; the timeout becomes an `errors` row instead.</summary>
        internal static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

        internal ExportPattern(string id, string pattern)
        {
            Id = id;
            // NO Multiline, and `[ \t]` rather than `\s` in the table: the scanner applies each
            // pattern to ONE LINE at a time (ExportScan.ScanText). Over the whole file text under
            // Multiline, `^\s*` matched BACKWARDS across newlines — the hit came back on the first
            // blank line ABOVE the directive with an empty `text`, and the scan went quadratic
            // (32 KB of blank lines = 16.8 s). Line-by-line the line number is exact by
            // construction and nothing can match across a break.
            Regex = new Regex(pattern, RegexOptions.CultureInvariant, MatchTimeout);
        }
    }

    /// <summary>
    /// The wire contract for the <c>export</c> job kind — <c>schemaVersion 1</c>, pinned by
    /// <c>docs/design/2026-09-21-kit-export-format.md</c>. Field names in that document ARE the
    /// wire; this file and the TypeScript mirror (<c>apps/api/src/onboarding/export/</c>) change
    /// together or not at all.
    ///
    /// THE KIT EMITS FACTS, NEVER VERDICTS. Nothing here decides what an asset IS.
    ///
    /// No Unity API is touched in this file, so every rule in it is covered by EditMode tests.
    /// </summary>
    public static class ExportFormat
    {
        public const int SchemaVersion = 1;

        // ---- entry names (spec §3) -------------------------------------------------------
        public const string BuildEntry = "build.json";
        public const string IdentityEntry = "identity.json";
        public const string InventoryBase = "inventory";
        public const string DependenciesBase = "dependencies";
        public const string PatternsBase = "patterns";
        /// <summary>Spec §8.7 — clickable UI elements.</summary>
        public const string UiBase = "ui";
        public const string ArtIndexBase = "art/index";
        public const string ArtPrefix = "art/";
        public const string ArtFilesPrefix = "art/files/";
        public const string ArtThumbsPrefix = "art/thumbs/";
        public const string DocsPrefix = "docs/";
        public const string ConfigPrefix = "config/";
        /// <summary>Spec §8.6 — the game's source-language strings.</summary>
        public const string StringsPrefix = "strings/";

        /// <summary>The name the header (and the upload ticket) knows a part by.</summary>
        public static string PartName(int index) =>
            "part-" + index.ToString("0000", CultureInfo.InvariantCulture) + ".zip";

        /// <summary>`inventory` + 0 -> `inventory.0000.jsonl`; `art/index` + 1 -> `art/index.0001.jsonl`.</summary>
        public static string ShardName(string baseName, int index) =>
            baseName + "." + index.ToString("0000", CultureInfo.InvariantCulture) + ".jsonl";

        public static string ArtFileEntry(string guid, string extension)
        {
            var ext = extension ?? "";
            if (ext.Length > 0 && ext[0] != '.') ext = "." + ext;
            return ArtFilesPrefix + guid + ext;
        }

        public static string ArtThumbEntry(string guid) => ArtThumbsPrefix + guid + ".png";

        /// <summary>A project-relative path becomes a `docs/…` / `config/…` entry. The relative
        /// path is kept WHOLE (so `docs/a.md` and `Docs/a.md` cannot collide) — hence
        /// `docs/docs/a.md` for a file inside a project-root `docs/` folder.</summary>
        public static string DocEntry(string projectRelativePath) => DocsPrefix + ToEntryPath(projectRelativePath);

        public static string ConfigEntry(string projectRelativePath) => ConfigPrefix + ToEntryPath(projectRelativePath);

        /// <summary>Backslashes to forward slashes, leading `./` and `/` stripped. Does NOT make an
        /// unsafe name safe — <see cref="IsSafeEntryName"/> still decides.</summary>
        public static string ToEntryPath(string? path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            var s = path!.Replace('\\', '/');
            while (s.StartsWith("./", StringComparison.Ordinal)) s = s.Substring(2);
            s = s.TrimStart('/');
            return s;
        }

        /// <summary>
        /// Spec §3: "Zip entries use forward slashes, no leading slash, and never `..`. The reader
        /// refuses anything else." A pure function so BOTH sides can run the same check — a reader
        /// that trusts a writer is a zip-slip.
        /// </summary>
        public static bool IsSafeEntryName(string? name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            var s = name!;
            if (s.Length > 1000) return false;
            if (s.IndexOf('\\') >= 0) return false;           // a backslash is a separator on Windows
            if (s.IndexOf(':') >= 0) return false;            // `C:/…`, and an NTFS alternate stream
            if (s[0] == '/') return false;                    // never absolute
            if (s[s.Length - 1] == '/') return false;         // never a bare directory
            foreach (var c in s)
            {
                if (c < 0x20 || c == 0x7f) return false;      // control characters
            }
            var parts = s.Split('/');
            foreach (var p in parts)
            {
                if (p.Length == 0) return false;              // `//` or a leading/trailing slash
                if (p == "." || p == "..") return false;      // never an escape
            }
            return true;
        }

        /// <summary>
        /// <paramref name="entry"/>, or a `~2`/`~3`… variant of it, that no name in
        /// <paramref name="takenCaseInsensitive"/> equals even when case is folded. The chosen name
        /// is ADDED to that set.
        ///
        /// `docs/docs/a.md` and `docs/Docs/a.md` are different entries ordinally, and the spec says
        /// so — but the sweep that produced them used to de-duplicate case-insensitively and
        /// silently DROP the second file. Keeping the file and renaming the entry loses nothing;
        /// dropping it lost a document (fresh-context audit, 2026-09-21).
        /// </summary>
        public static string UniqueEntryName(string entry, HashSet<string> takenCaseInsensitive)
        {
            if (takenCaseInsensitive.Add(entry)) return entry;
            var slash = entry.LastIndexOf('/');
            var dot = entry.LastIndexOf('.');
            var stem = dot > slash + 1 ? entry.Substring(0, dot) : entry;
            var ext = dot > slash + 1 ? entry.Substring(dot) : "";
            for (var n = 2; n < 10000; n++)
            {
                var candidate = stem + "~" + n.ToString(CultureInfo.InvariantCulture) + ext;
                if (takenCaseInsensitive.Add(candidate)) return candidate;
            }
            return entry;   // 9,998 collisions on one name is not a case that happens
        }

        /// <summary>
        /// Art is STORED, never deflated: PNG/JPG/OGG/TTF bytes do not compress, and Deflate on a
        /// gigabyte of art is tens of seconds of main-thread stall for nothing. It also makes the
        /// writer's "will this entry still fit under partMaxBytes" check exact rather than hopeful.
        /// Everything else is Deflate-Fastest — json and jsonl still shrink ~5-8x, without the
        /// unsliceable second that Optimal costs on a 16 MiB shard.
        /// </summary>
        public static CompressionLevel CompressionFor(string entryName)
        {
            if (!string.IsNullOrEmpty(entryName) &&
                entryName.StartsWith(ArtFilesPrefix, StringComparison.Ordinal))
                return CompressionLevel.NoCompression;
            if (!string.IsNullOrEmpty(entryName) &&
                entryName.StartsWith(ArtThumbsPrefix, StringComparison.Ordinal))
                return CompressionLevel.NoCompression;
            return CompressionLevel.Fastest;
        }

        // ---- bounded listings (spec §2) --------------------------------------------------
        /// <summary>`overCap` and `errors` are each capped at this many entries; the LAST entry says
        /// how many more were dropped. The COUNTS stay true — only the listing is bounded.
        ///
        /// 500, not the 2,000 first pinned: the header is POSTed as one JSON body, and the hosted
        /// side must be able to bound that body. A real game with thousands of oversize PSDs
        /// produced a header several times the server's body limit and could not post it at all
        /// (fresh-context audit, 2026-09-21). Nothing is lost by the smaller listing — every asset,
        /// with its byte size, is in inventory.jsonl, so an over-cap file is always derivable.
        /// Mirror: <c>EXPORT_LISTING_MAX</c> in apps/api/src/onboarding/export/export-format.ts.</summary>
        public const int ListingMax = 500;

        /// <summary>No single listed string (an error, an over-cap path) is longer than this on the
        /// wire — an exception message can be arbitrarily long, and the hosted schema refuses a
        /// header by FIELD when one is, which would fail a whole export over one verbose error.
        /// Mirror: <c>EXPORT_LISTED_TEXT_MAX</c>.</summary>
        public const int ListedTextMax = 400;

        /// <summary>At most this many keys in `totals.assetsByType`; past it the remainder is folded
        /// into ONE <see cref="OtherTypeKey"/> key so the per-type counts STILL SUM to
        /// `totals.assets`, and an `errors` row says how many types were folded. A project can hold
        /// thousands of distinct ScriptableObject types, and the header is one bounded JSON
        /// body.</summary>
        public const int AssetTypeKeysMax = 2000;

        /// <summary>…and no type NAME longer than this on the wire.</summary>
        public const int AssetTypeKeyTextMax = 100;

        /// <summary>The synthetic key the folded-away types are counted under.</summary>
        public const string OtherTypeKey = "(other)";

        /// <summary>At most this many `totals.dirsTopLevel` entries (the first, sorted), each at
        /// most <see cref="DirTextMax"/> long. `Assets/` with thousands of direct children is
        /// unusual but the header must fit regardless (invariant 102).</summary>
        public const int DirsTopLevelMax = 500;

        public const int DirTextMax = 300;

        /// <summary>
        /// …and the three header fields that come from the PROJECT rather than from an exception:
        /// `productName`, `companyName` (200) and `editorGameId` (120). They went out RAW, and the
        /// hosted schema bounds all three AND refuses any of them holding a control character
        /// (`text(200)` / `text(120)` in `apps/api/src/onboarding/export/export-format.ts`) — so a
        /// newline in a product name, which Unity's Player Settings field allows, failed the WHOLE
        /// export at the header door, after the studio had uploaded a gigabyte (audit round 3, M7).
        ///
        /// Mirrors, not independent numbers: if the hosted caps move, these move with them.
        /// </summary>
        public const int NameTextMax = 200;

        public const int GameIdTextMax = 120;

        /// <summary>
        /// Bounded for the wire; the in-memory value is untouched.
        ///
        /// CONTROL CHARACTERS BECOME SPACES. The hosted schema refuses a listed string that holds
        /// one, and these strings are built from exception messages — a .NET exception with a
        /// newline in it would otherwise fail a WHOLE export over one unreadable file
        /// (fresh-context audit, 2026-09-21). Truncation never splits a surrogate pair either: half
        /// a pair is not valid UTF-16 and the JSON that carries it is not valid UTF-8.
        /// </summary>
        public static string Listed(string? text) => Listed(text, ListedTextMax);

        /// <summary>As <see cref="Listed(string)"/>, with an explicit bound — `assetsByType` keys
        /// and `dirsTopLevel` entries have their own.</summary>
        public static string Listed(string? text, int max)
        {
            var t = Scrubbed(text ?? "");
            if (t.Length <= max) return t;
            var keep = SafeCut(t, max - 1);
            return t.Substring(0, keep) + "…";
        }

        /// <summary>
        /// Every character below U+0020 and U+007F becomes a space — AND the three separators
        /// Newtonsoft escapes anyway: U+0085 NEL, U+2028 LINE SEPARATOR, U+2029 PARAGRAPH
        /// SEPARATOR. Nothing else moves; the point is a string the wire schema accepts and whose
        /// cost on the wire is KNOWN, not a sanitised one.
        ///
        /// THOSE THREE COST SIX BYTES EACH once Newtonsoft writes them (as a backslash-u escape),
        /// which made "3 bytes per character is the worst case" — the claim BOTH sides' body-size
        /// proofs rest on — false: a header of them is twice what either side measured (audit
        /// round 3, N5). The hosted schema would not have caught it at the door either: its
        /// control-character test is the C0 range plus U+007F, and these are none of those.
        /// </summary>
        // (char) casts, never literals: U+2028 and U+2029 END A LINE for the C# lexer itself, so a
        // source file holding one does not compile.
        private const char Nel = (char)0x0085;
        private const char LineSeparator = (char)0x2028;
        private const char ParagraphSeparator = (char)0x2029;

        private static bool IsScrubbed(char c) =>
            c < 0x20 || c == 0x7f || c == Nel || c == LineSeparator || c == ParagraphSeparator;

        private static string Scrubbed(string t)
        {
            var needs = false;
            foreach (var c in t)
                if (IsScrubbed(c)) { needs = true; break; }
            if (!needs) return t;
            var chars = t.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
                if (IsScrubbed(chars[i])) chars[i] = ' ';
            return new string(chars);
        }

        /// <summary>The largest cut at or below <paramref name="at"/> that does not land between a
        /// high and a low surrogate.</summary>
        private static int SafeCut(string t, int at)
        {
            if (at <= 0) return 0;
            if (at >= t.Length) return t.Length;
            return char.IsLowSurrogate(t[at]) ? at - 1 : at;
        }

        /// <summary>
        /// Takes this machine's absolute paths OUT of a message before it leaves the machine.
        ///
        /// Found on the FIRST real export, not by a test: .NET puts the absolute path inside its
        /// own exception text (<c>Could not find file '/Users/&lt;name&gt;/…/Assets/x.png'</c>), and
        /// every `errors` entry is built from an exception message. Unscrubbed, the header carried
        /// the studio's disk layout and the OS account name to the server, into a database row and
        /// onto a web page. The kit's TRUST.md promises that no file paths from the machine leave
        /// it by design; this is what makes the export keep that promise.
        ///
        /// Pure, so it is covered without an editor. Longest root first (a project under the home
        /// dir must become <c>&lt;project&gt;</c>, not <c>~/…/project</c>); both separator forms;
        /// case-insensitive, because macOS and Windows paths are. Hand-rolled replace on purpose:
        /// <c>string.Replace(string, string, StringComparison)</c> does not exist under the
        /// .NET Framework API level, and this kit must compile under both.
        /// </summary>
        public static string ScrubPaths(string? message, IEnumerable<KeyValuePair<string, string>> rootsToLabels)
        {
            var text = message ?? "";
            if (text.Length == 0) return text;
            var pairs = new List<KeyValuePair<string, string>>();
            foreach (var kv in rootsToLabels)
            {
                var root = (kv.Key ?? "").TrimEnd('/', '\\');
                // A root of "/" or "C:" would turn every slash into a label.
                if (root.Length < 4) continue;
                pairs.Add(new KeyValuePair<string, string>(root, kv.Value));
                var flipped = root.IndexOf('/') >= 0 ? root.Replace('/', '\\') : root.Replace('\\', '/');
                if (flipped != root) pairs.Add(new KeyValuePair<string, string>(flipped, kv.Value));
            }
            pairs.Sort((a, b) => b.Key.Length.CompareTo(a.Key.Length));
            foreach (var kv in pairs)
                text = ReplaceIgnoreCase(text, kv.Key, kv.Value);
            return text;
        }

        private static string ReplaceIgnoreCase(string text, string find, string with)
        {
            var at = text.IndexOf(find, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return text;
            var sb = new System.Text.StringBuilder(text.Length);
            var from = 0;
            while (at >= 0)
            {
                sb.Append(text, from, at - from).Append(with);
                from = at + find.Length;
                at = text.IndexOf(find, from, StringComparison.OrdinalIgnoreCase);
            }
            return sb.Append(text, from, text.Length - from).ToString();
        }

        public static string MoreTail(int dropped) => "… and " + dropped.ToString(CultureInfo.InvariantCulture) + " more";

        // ---- overCap `kind` values (pinned here so the TS mirror copies them) -------------
        public const string KindIcon = "icon";
        public const string KindFont = "font";
        public const string KindAudio = "audio";
        public const string KindTexture = "texture";
        public const string KindDoc = "doc";
        public const string KindConfig = "config";
        public const string KindStrings = "strings";
        /// <summary>The kind of the synthetic LAST entry of a truncated `overCap` listing.</summary>
        public const string KindMore = "more";

        public const string ReasonOverArtFile = "file over artFileMaxBytes";
        public const string ReasonOverArtTotal = "art total over artTotalMaxBytes";
        public const string ReasonOverDocFile = "file over docFileMaxBytes";
        public const string ReasonOverConfigFile = "file over configFileMaxBytes";
        public const string ReasonOverConfigCount = "over configMaxFiles";
        public const string ReasonOverAudioFile = "file over audioFileMaxBytes";
        public const string ReasonOverStringsFile = "file over stringsFileMaxBytes";
        public const string ReasonOverStringsCount = "over stringsMaxFiles";
        public const string ReasonOverDocCount = "over docMaxFiles";

        // ---- misc caps that are shapes, not sizes ----------------------------------------
        /// <summary>spec §4: `text` is the matched line, trimmed, ≤ 200 chars.</summary>
        public const int PatternTextMax = 200;

        /// <summary>spec §4: at most 256 colours per ScriptableObject.</summary>
        public const int ColorsPerObjectMax = 256;

        // ---- the pattern table (spec §4) -------------------------------------------------
        private static readonly ExportPattern[] TableValue =
        {
            new ExportPattern("unity-editor-ifdef", @"^[ \t]*#if[ \t]+.*\b(UNITY_EDITOR|DEVELOPMENT_BUILD|DEBUG)\b"),
            new ExportPattern("debug-class",        @"\bclass[ \t]+\w*(Debug|Cheat|DevMenu|DevTool|GMTool|Console)\w*"),
            new ExportPattern("random-seed",        @"\bRandom\.InitState[ \t]*\("),
            new ExportPattern("remote-config",      @"\b(RemoteConfig|RemoteSettings|FirebaseRemoteConfig)\b"),
            new ExportPattern("tutorial-key",       @"(?i)\b(ftue|tutorial|onboarding)\w*[ \t]*(=|\(|"")"),
            new ExportPattern("feature-unlock",     @"(?i)feature_?unlock"),
            new ExportPattern("playerprefs-key",    @"\bPlayerPrefs\.(Get|Set)\w+[ \t]*\([ \t]*"""),
            // spec §8.5 (kit 0.9.0): a debug action registered BY NAME — a ready-made lever. RL
            // registers 43 (`DevConsole.RegisterAction("Force Win", …)`) and the seven above caught
            // none of them: only the class names.
            new ExportPattern("dev-command",
                @"\b(Register(Action|Command|Cheat|Button)|Add(Command|Action))[ \t]*\([ \t]*[$@]?""|\[[ \t]*(Command|ConsoleMethod|ConsoleCommand|DevCommand|CheatCommand)[ \t]*\([ \t]*""|\bDevVar<[^>]+>[ \t]*\([ \t]*"""),
            // F2 fresh audit H1: a WRITE to Time.timeScale (`=`, `+=`, `-=`, `*=` — never `==`). The ready gate fails while
            // the scale is not 1, so the website drafts footage hygiene only for a game whose code never writes it; the
            // header's `patternIds` says this row was looked for, so zero hits means "none", not "not looked".
            new ExportPattern("time-scale",         @"\bTime\.timeScale[ \t]*[-+*]?=(?!=)"),
            // F2 47 (invariant 266) — DOES A CHEAT IN THE EDITOR REACH REAL PLAYERS? Four facts the website weighs (it
            // decides; these only report lines): an `#if` on a release-like define (RL: `#if RELEASE` picks the
            // shipped server path), a string literal naming a dev / staging / sandbox server (RL: "_dev",
            // "pocketroll-dev"), a guest or anonymous sign-in, and any online-service client at all — so "no online
            // service" can be a finding (the header's `patternIds` says these were looked for).
            new ExportPattern("release-define",     @"^[ \t]*#[ \t]*(if|elif)\b.*\b[A-Z0-9_]*(RELEASE|PROD|LIVE)[A-Z0-9_]*\b"),
            new ExportPattern("dev-backend",        @"""(?:[^""\\\r\n]|\\.)*?[_.\-\x2F]([Dd][Ee][Vv]|[Ss][Tt][Aa][Gg][Ii][Nn][Gg]|[Ss][Aa][Nn][Dd][Bb][Oo][Xx])(?![A-Za-z0-9])(?:[^""\\\r\n]|\\.)*"""),
            new ExportPattern("guest-login",        @"\b(SignInAnonymously|LoginWithCustomID|LoginWith(Android|IOS)?DeviceID|LoginAsGuest|GuestLogin|SignInAsGuest|AnonymousLogin|AuthenticateDevice)\w*"),
            new ExportPattern("backend-client",     @"\b(FirebaseApp|FirebaseAuth|FirebaseFirestore|FirebaseFunctions|FirebaseDatabase|PlayFab\w*API|UnityWebRequest|HttpClient|WebSocket|NakamaClient|PhotonNetwork|GameSparks\w*|cloudfunctions\.net)\b"),
        };

        /// <summary>The seven regexes of spec §4 plus `dev-command` (§8.5), `time-scale` (F2) and the four dev-server facts
        /// (F2 47), in table order.</summary>
        public static IReadOnlyList<ExportPattern> Patterns => TableValue;

        /// <summary>F2 47 audit L4: what a redacted secret is replaced by.</summary>
        public const string Redacted = "«redacted»";

        private static readonly Regex SecretQuery = new Regex(
            @"([?&](?:api[_-]?key|key|token|access[_-]?token|secret|sig|signature|password|auth)=)[^&""'\s]+",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, ExportPattern.MatchTimeout);
        private static readonly Regex SecretLiteral = new Regex(
            @"""(?:AIza[0-9A-Za-z_\-]{20,}|sk_(?:live|test)_[0-9A-Za-z]{10,}|(?=[A-Za-z0-9_\-+=]*[0-9])(?=[A-Za-z0-9_\-+=]*[A-Za-z])[A-Za-z0-9_\-+=]{32,})""",
            RegexOptions.CultureInvariant, ExportPattern.MatchTimeout);

        /// <summary>F2 47 audit L4 — a pattern row's text with secret-looking values taken out: a URL query value named
        /// key / token / secret / sig / password / auth, and a string literal that looks like a key (`AIza…`, `sk_live_…`,
        /// or 32+ characters of key alphabet mixing letters and digits). A simple rule, said in TRUST.md: it cannot know
        /// every secret. Never throws; a timeout sends the whole line redacted (fail closed).</summary>
        public static string RedactSecrets(string text)
        {
            try
            {
                var t = SecretQuery.Replace(text, "$1" + Redacted);
                return SecretLiteral.Replace(t, "\"" + Redacted + "\"");
            }
            catch (RegexMatchTimeoutException) { return Redacted; }
        }

        /// <summary>F2 47: the most scripting defines `build.json`'s `editorDefines` carries.</summary>
        public const int EditorDefinesMax = 200;

        /// <summary>F2 47: <c>build.json</c>'s <c>editorDefines</c> — the editor's active target and the defines it compiles
        /// with, from Unity's `;`-joined string: trimmed, distinct, in order, at most <see cref="EditorDefinesMax"/>.
        /// Pure.</summary>
        public static Newtonsoft.Json.Linq.JObject EditorDefinesJson(string target, string? joined)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var defines = new Newtonsoft.Json.Linq.JArray();
            foreach (var raw in (joined ?? "").Split(';'))
            {
                var d = raw.Trim();
                if (d.Length == 0 || !seen.Add(d) || defines.Count >= EditorDefinesMax) continue;
                defines.Add(d);
            }
            return new Newtonsoft.Json.Linq.JObject { ["target"] = target, ["defines"] = defines };
        }

        /// <summary>Lines longer than this are not scanned — one generated or minified line can
        /// cost more than the rest of a project put together. The count per file becomes an
        /// `errors` row, never silence (spec §4).</summary>
        public const int PatternLineMaxChars = 2000;
    }
}
