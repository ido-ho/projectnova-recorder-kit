using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// HOW THIS KIT READS JSON — one reader, used everywhere a Nova file is parsed (the shot
    /// loader, adapter.json, levers.json, synced.json), because the two settings below are not
    /// preferences: they decide what a document MEANS.
    ///
    /// <c>DateParseHandling.None</c> — Newtonsoft turns any string that looks like a date into a
    /// Date token, and every reader here asks "is this a String?". A UI element called
    /// "2026-09-21T10:00:00Z", a textContains substring that is a timestamp, a cheat command that
    /// is one: all silently read as MISSING before this (audit S1b). The website accepts them.
    ///
    /// <c>FloatParseHandling.Double</c> — the kit's numbers are doubles; a decimal would mean two
    /// numeric types to defend against downstream.
    ///
    /// <c>MaxDepth</c> — Newtonsoft's own limit (64) is lifted, so a deeply nested document fails on
    /// THIS kit's own rule with a sentence a person can act on ("conditions are nested more than 8
    /// deep") rather than on the parser's, which takes the whole file with it. The kit's own ceiling
    /// is <see cref="MaxNesting"/>, far above that rule.
    /// </summary>
    internal static class NovaJson
    {
        /// <summary>
        /// THE DEEPEST NESTING OF ARRAYS AND OBJECTS ANY NOVA FILE MAY HOLD (the eleventh audit, ruling 2): an array or
        /// object inside more than this many others (the root counts) is refused, by name, while the file is read. Before
        /// it, nothing bounded the depth, and a <c>$schemaVersion</c> nested 20,000 deep cost 1.7 s and an
        /// 800,000,056-character error on every load. 256, because a valid shot nests about 20 deep (a condition 8 deep
        /// inside a step inside a shot), the site's editor refuses a shot nested past 24, and the kit's own sentence for
        /// over-nested conditions must stay the one a person reads well past Newtonsoft's own 64 (70 conditions deep is
        /// about 145); and 256 keeps every recursive walk of a token (<c>ToString</c>, <c>DeepClone</c>) shallow. Containers
        /// only, as the site's <c>jsonDepthOver</c> counts them: <c>deliveries.cases.json</c> holds the two to one boundary
        /// (<c>maxNesting</c>), and the site's lint refuses the same documents.
        /// </summary>
        public const int MaxNesting = 256;

        /// <summary>The one sentence a document nested past <see cref="MaxNesting"/> is refused with.</summary>
        internal static readonly string TooDeep = $"it nests arrays and objects more than {MaxNesting} deep";

        /// <summary>A reader that refuses an array or object nested past <see cref="MaxNesting"/> the moment it opens.</summary>
        private sealed class CappedReader : JsonTextReader
        {
            public CappedReader(System.IO.TextReader reader) : base(reader) { }

            public override bool Read()
            {
                var more = base.Read();
                if (more && (TokenType == JsonToken.StartObject || TokenType == JsonToken.StartArray) && Depth > MaxNesting)
                    throw new JsonReaderException(TooDeep);
                return more;
            }
        }

        /// <summary>Parse an object document. Throws exactly what <c>JObject.Parse</c> throws (not
        /// an object, trailing content, malformed), and <see cref="TooDeep"/> past <see cref="MaxNesting"/> — every caller
        /// here already handles a <c>JsonReaderException</c>.</summary>
        public static JObject ParseObject(string json)
        {
            using (var reader = new CappedReader(new System.IO.StringReader(json)))
            {
                reader.DateParseHandling = DateParseHandling.None;
                reader.FloatParseHandling = FloatParseHandling.Double;
                reader.MaxDepth = int.MaxValue;
                var o = JObject.Load(reader);
                // JObject.Parse's own trailing-content check, kept so "content after the document"
                // stays the refusal it has always been.
                while (reader.Read())
                    if (reader.TokenType != JsonToken.Comment)
                        throw new JsonReaderException(
                            "Additional text found in JSON string after finishing deserializing object.");
                return o;
            }
        }

        /// <summary>
        /// Read a token as a double when it is a NUMBER of any shape, including the one shape
        /// <c>Value&lt;double&gt;()</c> throws on: an integer past <c>long.MaxValue</c>, which
        /// Newtonsoft holds as a <c>BigInteger</c> — a type that implements no <c>IConvertible</c>,
        /// so it goes through its own text. Thrown inside the loader that "never throws", that one
        /// input took every shot in the file with it (audit S1a).
        /// </summary>
        public static bool TryNumber(JToken? token, out double value)
        {
            value = 0;
            if (token == null) return false;
            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float) return false;
            var raw = (token as JValue)?.Value;
            switch (raw)
            {
                case null: return false;
                case double d: value = d; return true;
                case float f: value = f; return true;
                case long l: value = l; return true;
                case int i: value = i; return true;
                case ulong u: value = u; return true;
                case decimal m: value = (double)m; return true;
            }
            try
            {
                value = System.Convert.ToDouble(raw, System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }
            catch (System.Exception)
            {
            }
            try
            {
                value = double.Parse(raw.ToString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }
            catch (System.Exception)
            {
                // A number this kit cannot hold is not a number it can use: the caller's "absent,
                // take the default" rule applies, which is the same answer it gives a string.
                return false;
            }
        }
    }

    /// <summary>Outcome of loading a shots file: whatever parsed, plus every reason something didn't.
    /// Never an exception — see JsonShotLoader.Load's remarks.</summary>
    public sealed class ShotLoadResult
    {
        public IReadOnlyList<AdShot> Shots { get; }
        public IReadOnlyList<string> Errors { get; }
        public bool Ok => Errors.Count == 0;

        public ShotLoadResult(IReadOnlyList<AdShot> shots, IReadOnlyList<string> errors)
        {
            Shots = shots;
            Errors = errors;
        }
    }

    /// <summary>
    /// Reads a game's shot list from JSON instead of hand-written C#, so editing a shot never edits a
    /// .cs file and therefore never triggers a domain reload. This type is generic across every game
    /// and never changes when a shot's content changes — that invariance IS the zero-recompile
    /// mechanism.
    ///
    /// Every step and condition is constructed through ShotGrammar's own static factories
    /// (AdStep.Click, AdStep.CheatUntil, new AdShot, …) rather than deserialized onto fields. That is
    /// deliberate: Newtonsoft defaults a missing double to 0, and 0 is actively harmful for these
    /// fields (retryEvery 0 on a click-until re-clicks every pump; timeout 0 on a cheatUntil never
    /// fires the cheat). Going through the factories means the JSON defaults and the C# defaults are
    /// the same defaults, declared once, in ShotGrammar.
    /// </summary>
    public static class JsonShotLoader
    {
        /// <summary>The only schema version this loader understands. Bumped only by a breaking format
        /// change, and a file declaring anything else is rejected rather than half-read.</summary>
        private const int SUPPORTED_SCHEMA_VERSION = 1;

        /// <summary>
        /// THE FEWEST SECONDS ANY FIELD READ AS SECONDS MAY HOLD (the twelfth audit, S1) — `retryEvery`, `timeout`,
        /// `seconds`, `settleTimeoutSec`, `armTimeoutSec`. A number below it, or one that is not finite, refuses the shot
        /// by name. A cheatUntil with retryEvery 0 re-ran its cheat on every pass of a loop that never handed the editor
        /// back — 29,538 refused writes logged in one tick of the auditor's probe at timeout 3. The wait between tries is a
        /// yield now, whatever its length, and this is the rule on top of it. 0.1 s: a retry every 0.1 s is at most ten
        /// writes a second, about one every six frames at 60 fps; a timeout or wait under it is too short to watch anything
        /// happen; and the smallest such number in any file this repo tracked before the rule was 0.25. The site's lint
        /// takes the same number (<c>MIN_SECONDS</c>; <c>deliveries.cases.json</c>, <c>minSeconds</c>).
        /// </summary>
        internal const double MinSeconds = 0.1;

        /// <summary>
        /// THE LONGEST SHOT NAME, IN CHARACTERS (the twelfth audit, M1) — the site's own cap (<c>MAX_SHOT_NAME</c>, the
        /// shot-name rule's 80), held by <c>deliveries.cases.json</c> (<c>maxShotName</c>).
        /// </summary>
        internal const int MaxShotName = 80;

        /// <summary>
        /// THE MOST SETUP COMMANDS ONE SHOT MAY LIST (the thirteenth audit, S1). Refused by count, at the shot, before any
        /// step, in one sentence the site's lint says too (<c>MAX_SETUP</c>; <c>deliveries.cases.json</c>, <c>maxSetup</c>).
        /// The lever cap counts DISTINCT commands, so 16,355 copies of one command were one lever in one shot. The director
        /// yields after each setup command now, so a long list is many short editor ticks rather than one long one; this
        /// bounds how many. 500: the longest setup of any shot this repo has held is 5 (SNL's <c>build_complete</c>).
        /// </summary>
        internal const int MaxSetup = 500;

        /// <summary>
        /// THE MOST STEPS ONE SHOT MAY HOLD (the thirteenth audit, S1). Refused by count, at the shot, before any step
        /// (<c>MAX_STEPS</c>; <c>maxSteps</c>). The director yields after each step now; this bounds how many there are.
        /// 500: the longest shot this repo has held has 9 steps (SNL's <c>hero_roster_flex</c>).
        /// </summary>
        internal const int MaxSteps = 500;

        /// <summary>
        /// THE MOST PARTS ONE CONDITION MAY HOLD, COUNTED OVER THE WHOLE TREE (the thirteenth audit, S2): the parts of every
        /// <c>all</c> in it, nested ones included (<c>MAX_CONDITION_PARTS</c>; <c>maxConditionParts</c>). One evaluation of a
        /// condition happens inside one editor tick and asks the UI about every part, so no yield can split it: a 2,255-part
        /// <c>all</c> took 1,984 ms of one tick on a 3,000-node canvas. A count over the tree, not over one <c>parts</c>
        /// array, because nesting multiplies what one array allows. 64: the most parts any condition in this repo has held
        /// is 2.
        /// </summary>
        internal const int MaxConditionParts = 64;

        /// <summary>
        /// THE MOST SECONDS ANY FIELD READ AS SECONDS MAY HOLD (the thirteenth audit, M2), refused by name, after the floor
        /// (<c>MAX_SECONDS</c>; <c>maxSeconds</c>). A delivered wait of 1e300 s never ended, and the capture agent waited
        /// for the director for ever. 600 s, ten minutes: ten times the kit's longest default (a vision step's 60 s
        /// timeout) and twenty times the most seconds any shot in this repo has held (30).
        /// </summary>
        internal const double MaxSeconds = 600;

        private static IReadOnlyList<string> _lastErrors = System.Array.Empty<string>();

        /// <summary>Errors from the most recent Load/LoadFrom, for the relay's ping to report so a
        /// malformed edit is visible without opening Unity's console.</summary>
        public static IReadOnlyList<string> LastErrors => _lastErrors;

        /// <summary>
        /// Read and parse a shots file at <paramref name="assetsRelativePath"/>, relative to the
        /// project's Assets folder (e.g. "Tools/AdRecorder/shots.json").
        ///
        /// NEVER THROWS. A missing or malformed file yields an empty shot list plus errors, for two
        /// reasons: a caught exception surfaces as an opaque type name ("InvalidCastException")
        /// instead of a specific, actionable message ("shots[3]: unknown step kind 'clcik'"); and
        /// throwing mid-parse would abort the whole array, discarding every shot that DID parse
        /// correctly along with the one that didn't — partial loading depends on never throwing out
        /// of the loop.
        /// </summary>
        public static ShotLoadResult Load(string assetsRelativePath)
        {
            string json;
            try
            {
                var full = System.IO.Path.Combine(UnityEngine.Application.dataPath, assetsRelativePath);
                json = System.IO.File.ReadAllText(full);
            }
            catch (System.Exception e)
            {
                return Remember(new ShotLoadResult(System.Array.Empty<AdShot>(),
                    new[] { $"could not read '{assetsRelativePath}': {e.Message}" }));
            }
            return LoadFrom(json);
        }

        /// <summary>
        /// Read a shots file at an absolute path (Library/Nova/shots.json, a test temp file).
        /// A missing file is an empty catalog with no errors — that is the first-run state after
        /// adding the kit, before any shots have been learned. A present-but-unreadable file is
        /// an error. Never throws (see Load).
        /// </summary>
        public static ShotLoadResult LoadFromPath(string fullPath)
        {
            if (!System.IO.File.Exists(fullPath))
                return Remember(new ShotLoadResult(System.Array.Empty<AdShot>(), System.Array.Empty<string>()));
            try
            {
                return LoadFrom(System.IO.File.ReadAllText(fullPath));
            }
            catch (System.Exception e)
            {
                return Remember(new ShotLoadResult(System.Array.Empty<AdShot>(),
                    new[] { $"could not read '{fullPath}': {e.Message}" }));
            }
        }

        /// <summary>Parse a shots document already in memory. Never throws (see Load).</summary>
        public static ShotLoadResult LoadFrom(string json) => LoadFrom(json, remember: true);

        /// <summary>
        /// The same parse (<see cref="Read"/>), with a choice about <see cref="LastErrors"/>.
        ///
        /// <paramref name="remember"/> is false for exactly one caller: a <c>probe</c> job, which
        /// loads ONE shot the website sent and must not change what this machine says about its
        /// OWN shots.json. <see cref="LastErrors"/> is what the relay's ping and a capture job's
        /// done report quote as "your shots file's errors", so a probe's refused shot left there
        /// would be read as the studio's file being broken. Same parser, same grammar, same
        /// sentences — only the side effect differs.
        /// </summary>
        public static ShotLoadResult LoadFrom(string json, bool remember)
        {
            var result = Read(json, out _);
            return remember ? Remember(result) : result;
        }

        /// <summary>
        /// THE LOADER'S WHOLE VERDICT ON A TEXT, remembering nothing — what <see cref="LoadFrom(string)"/> returns, and so what
        /// <see cref="LoadFromPath"/> returns after <c>File.ReadAllText</c>. <paramref name="root"/> is the document it
        /// parsed, or null when there was none. <see cref="SyncNova"/>'s delivery check runs THIS on the text it is about
        /// to write, refuses on its first error and counts levers from <paramref name="root"/> (the eleventh audit, ruling
        /// 1): a check that mirrors the loader is a second checklist, and rounds 8 to 11 each found a loader rule it missed.
        /// It does not touch <see cref="LastErrors"/>, which the relay reports: a refused delivery leaves the file on disk,
        /// and its errors, as they were. Never throws.
        /// </summary>
        internal static ShotLoadResult Read(string json, out JObject? root)
        {
            var errors = new List<string>();
            var shots = new List<AdShot>();

            root = null;
            JObject doc;
            try
            {
                doc = NovaJson.ParseObject(json);
            }
            catch (System.Exception e)
            {
                return new ShotLoadResult(System.Array.Empty<AdShot>(),
                    new[] { $"shots file is not valid JSON: {e.Message}" });
            }
            root = doc;

            if (!TryInt(doc["$schemaVersion"], out var version) || version != SUPPORTED_SCHEMA_VERSION)
            {
                return new ShotLoadResult(System.Array.Empty<AdShot>(),
                    new[] { $"unsupported $schemaVersion '{ShownVersion(doc["$schemaVersion"])}' — " +
                            $"this kit reads version {SUPPORTED_SCHEMA_VERSION}" });
            }

            if (doc["shots"] is not JArray shotArray)
            {
                return new ShotLoadResult(System.Array.Empty<AdShot>(),
                    new[] { "shots file has no 'shots' array" });
            }

            var seen = new HashSet<string>();
            for (var i = 0; i < shotArray.Count; i++)
            {
                // A THROW MUST NOT LEAVE THIS LOOP. Partial loading is the whole contract: one
                // unreadable shot may not take the shots that DID parse with it (audit S1a, where
                // one oversized integer emptied a whole file). Nothing known reaches this catch
                // after the number and date fixes — it is the backstop for the next one.
                try
                {
                    if (!TryParseShot(shotArray[i], $"shots[{i}]", errors, out var shot)) continue;
                    if (!seen.Add(shot!.Name))
                        errors.Add($"shots[{i}]: duplicate shot name '{shot.Name}'");
                    else
                        shots.Add(shot);
                }
                catch (System.Exception e)
                {
                    errors.Add($"shots[{i}]: could not be read ({e.GetType().Name}: {e.Message})");
                }
            }

            return new ShotLoadResult(shots, errors);
        }

        /// <summary>The most characters of a refused <c>$schemaVersion</c> an error shows (the eleventh audit, ruling 2).</summary>
        internal const int MaxShownVersion = 64;

        /// <summary>
        /// A refused <c>$schemaVersion</c> as the error shows it: "(missing)", a scalar as its plain text (the shared
        /// grammar's <c>'2'</c> and <c>'1'</c>), an array or object COMPACT — and any of them cut to its first
        /// <see cref="MaxShownVersion"/> characters. The indented rendering this replaced grew with the square of the
        /// nesting (the eleventh audit, S1: 800,000,056 characters at 20,000 deep). A cut never leaves half of a surrogate
        /// pair: it stops one short instead. The site's <c>shownVersion</c> is the same rule (<c>shots-grammar.cases.json</c>).
        /// </summary>
        private static string ShownVersion(JToken? token)
        {
            if (token == null) return "(missing)";
            var shown = token is JContainer ? token.ToString(Formatting.None) : token.ToString();
            if (shown.Length <= MaxShownVersion) return shown;
            return shown.Substring(0, char.IsHighSurrogate(shown[MaxShownVersion - 1]) ? MaxShownVersion - 1 : MaxShownVersion); // never half a pair
        }

        private static ShotLoadResult Remember(ShotLoadResult result)
        {
            _lastErrors = result.Errors;
            return result;
        }

        private static bool TryParseShot(JToken token, string where, List<string> errors,
            out AdShot? shot)
        {
            shot = null;
            if (token is not JObject obj)
            {
                errors.Add($"{where}: expected a shot object");
                return false;
            }

            var name = SafeString(obj["name"]);
            if (string.IsNullOrEmpty(name))
            {
                errors.Add($"{where}: shot is missing 'name'");
                return false;
            }
            // THE NAME'S LENGTH, BEFORE ANY STEP, AND WITHOUT THE NAME (the twelfth audit, M1): every address below repeats
            // the name, so a 263,799-character name over 9,300 steps cost 0.85 s on every load.
            if (name!.Length > MaxShotName)
            {
                errors.Add($"{where}: the shot name is {name.Length} characters long, and this kit takes names of at most {MaxShotName}");
                return false;
            }

            if (obj["steps"] is not JArray stepArray || stepArray.Count == 0)
            {
                errors.Add($"{where} ('{name}'): shot needs a non-empty 'steps' array");
                return false;
            }
            // THE COUNTS, BEFORE ANY STEP, STEPS FIRST (the thirteenth audit, S1) — the site's lint says the same words at
            // the same point. The lever cap counts distinct commands, so it never saw 16,355 copies of one.
            if (stepArray.Count > MaxSteps)
            {
                errors.Add($"{where} ('{name}'): the shot has {stepArray.Count} steps, and this kit takes at most {MaxSteps}");
                return false;
            }
            if (obj["setup"] is JArray setupArray && setupArray.Count > MaxSetup)
            {
                errors.Add($"{where} ('{name}'): the shot has {setupArray.Count} setup commands, and this kit takes at most {MaxSetup}");
                return false;
            }

            var steps = new List<AdStep>(stepArray.Count);
            for (var i = 0; i < stepArray.Count; i++)
            {
                if (!TryParseStep(stepArray[i], $"{where} ('{name}').steps[{i}]", out var step, errors))
                    return false;
                steps.Add(step);
            }

            // settle is REQUIRED and deliberately has no default: WaitCondition is a non-nullable
            // struct, so an omitted settle would become Present(""), which nothing ever satisfies —
            // every shot would fail its capture check silently, with no error anywhere.
            if (obj["settle"] == null)
            {
                errors.Add($"{where} ('{name}'): shot is missing 'settle' (required — there is no " +
                           "sane default; an omitted settle would never match anything)");
                return false;
            }
            if (!TryParseCondition(obj["settle"], $"{where} ('{name}').settle", out var settle, errors))
                return false;

            WaitCondition? arm = null;
            if (obj["arm"] != null)
            {
                if (!TryParseCondition(obj["arm"], $"{where} ('{name}').arm", out var a, errors))
                    return false;
                arm = a;
            }

            // expectState is optional, but if it's present at all it must actually be a string —
            // SafeString alone can't distinguish "absent" from "present but the wrong JSON type",
            // and silently treating a malformed value as absent would hide the mistake entirely.
            string? expectState = null;
            if (obj["expectState"] != null)
            {
                expectState = SafeString(obj["expectState"]);
                if (expectState == null)
                {
                    errors.Add($"{where} ('{name}'): 'expectState' must be a string");
                    return false;
                }
            }

            // baseline is optional and defaults to "board" (a bare shot starts on the primary play
            // surface, the kit's long-standing meaning). If present it must be a string naming a
            // baseline this kit understands — an unknown value is a typo that would silently mis-route
            // the shot, which is the exact hazard this field exists to remove, so it is rejected loudly
            // in the loader's own idiom (see UnknownStepKind) rather than accepted and mis-routed.
            string? baseline = null;
            if (obj["baseline"] != null)
            {
                baseline = SafeString(obj["baseline"]);
                if (baseline == null)
                {
                    errors.Add($"{where} ('{name}'): 'baseline' must be a string");
                    return false;
                }
                if (baseline != AdShot.BaselineLobby && baseline != AdShot.BaselineBoard)
                {
                    errors.Add($"{where} ('{name}'): unknown baseline '{baseline}' — this kit " +
                               $"understands '{AdShot.BaselineLobby}' or '{AdShot.BaselineBoard}'");
                    return false;
                }
            }

            bool? resist = null;
            var resistTok = obj["resist"];
            if (resistTok != null)
            {
                if (resistTok.Type != JTokenType.Boolean)
                {
                    errors.Add($"{where} ('{name}'): 'resist' must be a boolean");
                    return false;
                }
                resist = (bool)resistTok;
            }

            if (!Seconds(obj, "settleTimeoutSec", 8, $"{where} ('{name}')", errors, out var settleTimeout)) return false;
            if (!Seconds(obj, "armTimeoutSec", 15, $"{where} ('{name}')", errors, out var armTimeout)) return false;

            shot = new AdShot(
                name!,
                StringArray(obj["setup"]),
                steps.ToArray(),
                settle,
                expectState,
                settleTimeout,
                arm,
                armTimeout,
                StringArray(obj["parameters"]),
                baseline,
                resist);
            return true;
        }

        private static string[] StringArray(JToken? token) =>
            token is JArray arr
                ? System.Linq.Enumerable.ToArray(
                    System.Linq.Enumerable.Select(arr, t => SafeString(t) ?? ""))
                : System.Array.Empty<string>();

        /// <summary>
        /// HOW DEEP A CONDITION MAY NEST. A shared grammar rule (the hosted lint's
        /// <c>MAX_CONDITION_DEPTH</c> is the same number, and the shared fixture pins both): a
        /// top-level condition is depth 1 and each <c>all</c> part is one deeper. It exists so a
        /// document the parser itself would refuse — or recurse through — fails on a sentence a
        /// person can act on instead.
        /// </summary>
        internal const int MaxConditionDepth = 8;

        internal static bool TryParseCondition(JToken? token, string where, out WaitCondition cond,
            List<string> errors)
        {
            var parts = 0;
            return TryParseCondition(token, where, 1, out cond, errors, where, ref parts);
        }

        /// <param name="root">the address of the condition this token is part of, where a count over the cap is refused</param>
        /// <param name="parts">the parts of every <c>all</c> read so far in that condition (<see cref="MaxConditionParts"/>)</param>
        private static bool TryParseCondition(JToken? token, string where, int depth,
            out WaitCondition cond, List<string> errors, string root, ref int parts)
        {
            cond = default;
            // FIRST, before the shape and before 'kind': the sentence and the path must be the
            // same on both sides of the wire, and the hosted lint cannot read a C# object model to
            // decide which check ran first.
            if (depth > MaxConditionDepth)
            {
                errors.Add($"{where}: conditions are nested more than {MaxConditionDepth} deep");
                return false;
            }
            if (token is not JObject obj)
            {
                errors.Add($"{where}: expected a condition object");
                return false;
            }

            var kind = SafeString(obj["kind"]);
            if (string.IsNullOrEmpty(kind))
            {
                errors.Add($"{where}: condition is missing 'kind'");
                return false;
            }

            // 'all' first: it is the only kind that recurses, and the only one with no 'name'.
            if (kind == "all")
            {
                if (obj["parts"] is not JArray partsArray || partsArray.Count == 0)
                {
                    // An empty All is vacuously TRUE (ShotGrammar documents that), which as a settle
                    // means "captured, always". Hand-authored, that is far likelier a mistake than an
                    // intent, so it is rejected rather than silently passing every shot.
                    errors.Add($"{where}: 'all' needs a non-empty 'parts' array");
                    return false;
                }
                // THE WHOLE TREE'S PARTS (the thirteenth audit, S2): one evaluation asks the UI about every part inside one
                // editor tick, so the cap counts every `all`'s parts, nested ones too, and refuses at the condition itself.
                parts += partsArray.Count;
                if (parts > MaxConditionParts)
                {
                    errors.Add($"{root}: the condition holds more than {MaxConditionParts} parts, counting the parts of every 'all' inside it");
                    return false;
                }
                var subs = new List<WaitCondition>(partsArray.Count);
                for (var i = 0; i < partsArray.Count; i++)
                {
                    if (!TryParseCondition(partsArray[i], $"{where}.parts[{i}]", depth + 1, out var sub, errors, root, ref parts))
                        return false;
                    subs.Add(sub);
                }
                cond = WaitCondition.All(subs.ToArray());
                return true;
            }

            var name = SafeString(obj["name"]);
            if (string.IsNullOrEmpty(name))
            {
                errors.Add($"{where}: condition '{kind}' is missing 'name'");
                return false;
            }

            switch (kind)
            {
                case "present":
                    cond = WaitCondition.Present(name!);
                    return true;
                case "absent":
                    cond = WaitCondition.Absent(name!);
                    return true;
                case "interactable":
                    cond = WaitCondition.Interactable(name!);
                    return true;
                case "state":
                    cond = WaitCondition.State(name!);
                    return true;
                case "textContains":
                {
                    var sub = SafeString(obj["substring"]);
                    if (sub == null)
                    {
                        errors.Add($"{where}: 'textContains' is missing 'substring'");
                        return false;
                    }
                    cond = WaitCondition.TextContains(name!, sub);
                    return true;
                }
                default:
                    errors.Add($"{where}: unknown condition kind '{kind}'");
                    return false;
            }
        }

        internal static bool TryParseStep(JToken? token, string where, out AdStep step,
            List<string> errors)
        {
            step = default;
            if (token is not JObject obj)
            {
                errors.Add($"{where}: expected a step object");
                return false;
            }

            var kind = SafeString(obj["kind"]);
            if (string.IsNullOrEmpty(kind))
            {
                errors.Add($"{where}: step is missing 'kind'");
                return false;
            }

            switch (kind)
            {
                case "click":
                {
                    if (!Required(obj, "name", where, kind!, errors, out var name)) return false;
                    WaitCondition? until = null;
                    if (obj["until"] != null)
                    {
                        if (!TryParseCondition(obj["until"], $"{where}.until", out var u, errors))
                            return false;
                        until = u;
                    }
                    if (!Seconds(obj, "timeout", 6, where, errors, out var timeout)) return false;
                    if (!Seconds(obj, "retryEvery", 0.6, where, errors, out var retryEvery)) return false;
                    step = AdStep.Click(name!, until, (int)Num(obj, "index", 0, out _), timeout, retryEvery);
                    return true;
                }
                case "hold":
                {
                    if (!Required(obj, "name", where, kind!, errors, out var name)) return false;
                    if (!RequiredNum(obj, "seconds", where, kind!, errors, out var seconds)) return false;
                    if (!Seconds(obj, "seconds", seconds, where, errors, out _)) return false;
                    step = AdStep.Hold(name!, seconds, (int)Num(obj, "index", 0, out _));
                    return true;
                }
                case "wait":
                {
                    if (!RequiredNum(obj, "seconds", where, kind!, errors, out var seconds)) return false;
                    if (!Seconds(obj, "seconds", seconds, where, errors, out _)) return false;
                    step = AdStep.Wait(seconds);
                    return true;
                }
                case "waitFor":
                {
                    if (obj["condition"] == null)
                    {
                        errors.Add($"{where}: 'waitFor' is missing 'condition'");
                        return false;
                    }
                    if (!TryParseCondition(obj["condition"], $"{where}.condition", out var c, errors))
                        return false;
                    if (!Seconds(obj, "timeout", 8, where, errors, out var timeout)) return false;
                    step = AdStep.WaitFor(c, timeout);
                    return true;
                }
                case "cheat":
                {
                    if (!Required(obj, "command", where, kind!, errors, out var command)) return false;
                    step = AdStep.Cheat(command!);
                    return true;
                }
                case "cheatUntil":
                {
                    if (!Required(obj, "command", where, kind!, errors, out var command)) return false;
                    if (obj["until"] == null)
                    {
                        errors.Add($"{where}: 'cheatUntil' is missing 'until'");
                        return false;
                    }
                    if (!TryParseCondition(obj["until"], $"{where}.until", out var u, errors))
                        return false;
                    if (!Seconds(obj, "timeout", 30, where, errors, out var timeout)) return false;
                    if (!Seconds(obj, "retryEvery", 1.5, where, errors, out var retryEvery)) return false;
                    step = AdStep.CheatUntil(command!, u, timeout, retryEvery);
                    return true;
                }
                case "timeScale":
                {
                    if (!RequiredNum(obj, "factor", where, kind!, errors, out var factor)) return false;
                    step = AdStep.TimeScale(factor);
                    return true;
                }
                case "vision":
                {
                    if (!Required(obj, "prompt", where, kind!, errors, out var prompt)) return false;
                    if (!Seconds(obj, "timeout", 60, where, errors, out var timeout)) return false;
                    step = AdStep.Vision(prompt!, timeout);
                    return true;
                }
                default:
                    errors.Add($"{where}: unknown step kind '{kind}'");
                    return false;
            }
        }

        private static bool Required(JObject obj, string field, string where, string kind,
            List<string> errors, out string? value)
        {
            value = SafeString(obj[field]);
            if (!string.IsNullOrEmpty(value)) return true;
            errors.Add($"{where}: '{kind}' is missing '{field}'");
            return false;
        }

        private static bool RequiredNum(JObject obj, string field, string where, string kind,
            List<string> errors, out double value)
        {
            value = Num(obj, field, double.NaN, out var present);
            if (present) return true;
            errors.Add($"{where}: '{kind}' is missing '{field}'");
            return false;
        }

        /// <summary>
        /// A field read as SECONDS: its number, or <paramref name="dflt"/> when it is absent (a token of another JSON type
        /// is absent, as <see cref="Num"/> reads it). A number that is not finite, or below <see cref="MinSeconds"/>, is
        /// refused by name, in one sentence (the twelfth audit, S1); a finite one over <see cref="MaxSeconds"/> after that, in
        /// another (the thirteenth audit, M2). The site's lint says the same words.
        /// </summary>
        private static bool Seconds(JObject obj, string field, double dflt, string where, List<string> errors,
            out double value)
        {
            value = Num(obj, field, dflt, out var present);
            if (!present) return true;
            if (!(value >= MinSeconds && !double.IsInfinity(value)))
            {
                errors.Add($"{where}: '{field}' must be a finite number of seconds, at least " +
                           MinSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return false;
            }
            // THE CEILING, AFTER THE FLOOR, IN ITS OWN SENTENCE (the thirteenth audit, M2): a wait of 1e300 s never ended
            if (value > MaxSeconds)
            {
                errors.Add($"{where}: '{field}' must be at most " +
                           MaxSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + " seconds");
                return false;
            }
            return true;
        }

        /// <summary>Read a number, or the C# factory's own default when absent. NEVER Newtonsoft's
        /// zero-default — see the class remarks for why 0 is harmful for these fields.
        /// A token whose JSON type isn't actually numeric (a string, object, array, bool) is treated
        /// as absent; a token that IS numeric is read through <see cref="NovaJson.TryNumber"/>,
        /// which handles every shape Newtonsoft can hold a number in — the loader must never throw
        /// on adversarial JSON, and <c>Value&lt;double&gt;()</c> throws on two of them.</summary>
        private static double Num(JObject obj, string field, double dflt, out bool present)
        {
            present = NovaJson.TryNumber(obj[field], out var value);
            return present ? value : dflt;
        }

        /// <summary>Read a token as a string only when it actually IS a JSON string. Newtonsoft's
        /// Value&lt;string&gt;() throws InvalidCastException for an object/array token, so callers must
        /// never call it directly on an unvalidated token — the loader must never throw on adversarial
        /// JSON.</summary>
        private static string? SafeString(JToken? token) =>
            token != null && token.Type == JTokenType.String ? token.Value<string>() : null;

        /// <summary>Read a token as an int only when it actually IS a JSON integer. The same adversarial-
        /// JSON hazard as SafeString: Value&lt;int&gt;() throws for an object/array/string token, so
        /// callers must never call it directly on an unvalidated token.</summary>
        private static bool TryInt(JToken? token, out int value)
        {
            value = 0;
            if (token == null || token.Type != JTokenType.Integer) return false;
            // Value<int>() throws on an integer too big for an int (and on one past long.MaxValue,
            // which is not even IConvertible) — inside the loader that never throws.
            if (!NovaJson.TryNumber(token, out var number)) return false;
            if (number < int.MinValue || number > int.MaxValue) return false;
            value = (int)number;
            return true;
        }
    }
}
