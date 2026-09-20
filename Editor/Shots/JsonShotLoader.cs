using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace ProjectNova.RecorderKit
{
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
        public static ShotLoadResult LoadFrom(string json)
        {
            var errors = new List<string>();
            var shots = new List<AdShot>();

            JObject root;
            try
            {
                root = JObject.Parse(json);
            }
            catch (System.Exception e)
            {
                return Remember(new ShotLoadResult(System.Array.Empty<AdShot>(),
                    new[] { $"shots file is not valid JSON: {e.Message}" }));
            }

            if (!TryInt(root["$schemaVersion"], out var version) || version != SUPPORTED_SCHEMA_VERSION)
            {
                var shown = root["$schemaVersion"] == null ? "(missing)" : root["$schemaVersion"]!.ToString();
                return Remember(new ShotLoadResult(System.Array.Empty<AdShot>(),
                    new[] { $"unsupported $schemaVersion '{shown}' — " +
                            $"this kit reads version {SUPPORTED_SCHEMA_VERSION}" }));
            }

            if (root["shots"] is not JArray shotArray)
            {
                return Remember(new ShotLoadResult(System.Array.Empty<AdShot>(),
                    new[] { "shots file has no 'shots' array" }));
            }

            var seen = new HashSet<string>();
            for (var i = 0; i < shotArray.Count; i++)
            {
                if (TryParseShot(shotArray[i], $"shots[{i}]", errors, out var shot))
                {
                    if (!seen.Add(shot!.Name))
                        errors.Add($"shots[{i}]: duplicate shot name '{shot.Name}'");
                    else
                        shots.Add(shot);
                }
            }

            return Remember(new ShotLoadResult(shots, errors));
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

            if (obj["steps"] is not JArray stepArray || stepArray.Count == 0)
            {
                errors.Add($"{where} ('{name}'): shot needs a non-empty 'steps' array");
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

            shot = new AdShot(
                name!,
                StringArray(obj["setup"]),
                steps.ToArray(),
                settle,
                expectState,
                Num(obj, "settleTimeoutSec", 8, out _),
                arm,
                Num(obj, "armTimeoutSec", 15, out _),
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

        internal static bool TryParseCondition(JToken? token, string where, out WaitCondition cond,
            List<string> errors)
        {
            cond = default;
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
                if (obj["parts"] is not JArray parts || parts.Count == 0)
                {
                    // An empty All is vacuously TRUE (ShotGrammar documents that), which as a settle
                    // means "captured, always". Hand-authored, that is far likelier a mistake than an
                    // intent, so it is rejected rather than silently passing every shot.
                    errors.Add($"{where}: 'all' needs a non-empty 'parts' array");
                    return false;
                }
                var subs = new List<WaitCondition>(parts.Count);
                for (var i = 0; i < parts.Count; i++)
                {
                    if (!TryParseCondition(parts[i], $"{where}.parts[{i}]", out var sub, errors))
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
                    step = AdStep.Click(name!, until,
                        (int)Num(obj, "index", 0, out _),
                        Num(obj, "timeout", 6, out _),
                        Num(obj, "retryEvery", 0.6, out _));
                    return true;
                }
                case "hold":
                {
                    if (!Required(obj, "name", where, kind!, errors, out var name)) return false;
                    if (!RequiredNum(obj, "seconds", where, kind!, errors, out var seconds)) return false;
                    step = AdStep.Hold(name!, seconds, (int)Num(obj, "index", 0, out _));
                    return true;
                }
                case "wait":
                {
                    if (!RequiredNum(obj, "seconds", where, kind!, errors, out var seconds)) return false;
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
                    step = AdStep.WaitFor(c, Num(obj, "timeout", 8, out _));
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
                    step = AdStep.CheatUntil(command!, u,
                        Num(obj, "timeout", 30, out _), Num(obj, "retryEvery", 1.5, out _));
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
                    step = AdStep.Vision(prompt!, Num(obj, "timeout", 60, out _));
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

        /// <summary>Read a number, or the C# factory's own default when absent. NEVER Newtonsoft's
        /// zero-default — see the class remarks for why 0 is harmful for these fields.
        /// A token whose JSON type isn't actually numeric (a string, object, array, bool) is treated
        /// as absent rather than forced through Value&lt;double&gt;(), which throws on exactly that
        /// input — the loader must never throw on adversarial JSON.</summary>
        private static double Num(JObject obj, string field, double dflt, out bool present)
        {
            var token = obj[field];
            if (token == null || token.Type == JTokenType.Null)
            {
                present = false;
                return dflt;
            }
            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
            {
                present = false;
                return dflt;
            }
            present = true;
            return token.Value<double>();
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
            value = token.Value<int>();
            return true;
        }
    }
}
