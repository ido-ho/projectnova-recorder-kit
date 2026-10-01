using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Learn-and-drive v3 P4 (§3.6) — THE RECIPE BOOK as the kit reads it: <c>learn/recipes.json</c>, written by the box
    /// (<c>RecipesFileSchema</c>, <c>apps/renderer/src/game-ads/learn/recipe-book.ts</c>). NEVER THROWS: a document that is
    /// not JSON, not the shape, or past a cap is refused WHOLE with the reason, and the caller gets no recipes — the same
    /// verdict the server's zod schema gives, held together by <c>recipe.cases.json</c>'s <c>documents[]</c>. Unknown keys
    /// are read past (a newer box may add fields), as zod reads past them.
    /// </summary>
    public static class RecipeFile
    {
        public const int SchemaVersion = 1;
        public const int MaxRecipes = 500;
        public const int MaxSteps = 40;
        public const int MaxNames = 300;
        public const int MaxNameChars = 200;
        public const int MaxAsk = 200;

        private static readonly Regex IdRe = new("^r[0-9a-f]{16}$", RegexOptions.CultureInvariant);

        public sealed class Recipe
        {
            public string Id = "";
            public string Ask = "";
            public string StartKind = TeachAnalysis.StartLobby;
            public string? StartRecipeId;
            /// <summary>Kit 0.14.1 (invariant 187) — the start screen's names (<see cref="TeachAnalysis.StartNamesOf"/>): the
            /// start check (<see cref="StartCheck"/>) plays the recipe only when they are up. Empty = none recorded (a recipe
            /// taught before 0.14.1: its first step alone says the start is up, as before).</summary>
            public List<string> StartNames = new();
            public string? GameCommit;
            public string KitVersion = "";
            public JArray Steps = new();
            public List<string> Dismiss = new();
            public List<string> Arrival = new();
            public bool Confirmed;
            public string? Why;
            /// <summary>P5 — how it was learned: <c>teach</c>, <c>auto</c> (auto-try) or <c>studio</c> (a ticked cheat).</summary>
            public string LearnedBy = "teach";
            /// <summary>P5 (§3.6a item 1) — the screen after each recorded press: its step, its role (goal or dismiss) and
            /// its panel roots. The unknown-overlay check reads the roots a goal step was taught to leave on screen.</summary>
            public List<Screen> Screens = new();
            /// <summary>P5 — the story need it was made for (<c>kind</c>, <c>name</c>), or null.</summary>
            public (string Kind, string Name)? Need;
            /// <summary>P5 — the newest re-proof's state (<c>proven</c> | <c>failed</c> | <c>pressed</c>), or null when never
            /// re-proven. <c>pressed</c> (fix 5b audit H1): a Try of a recipe confirmed by picture pressed every step.</summary>
            public string? ProofState;
            /// <summary>Fix 5b (invariant 185) — confirmed by one picture read (<c>confirm.by: picture</c>), not by names.</summary>
            public bool ByPicture;
            /// <summary>Fix 5b audit H1 — how long after its last step the teach's destination was up (the Stop lag, capped
            /// 20 s), kept when names cannot tell the arrival; 0 = none. <see cref="StartPathRun"/> waits it.</summary>
            public double ArrivalLagSec;
            /// <summary>Fix 5b — the recipe carries <c>arrivalLagSec</c> at all (0 included).</summary>
            public bool HasArrivalLag;
        }

        /// <summary>One entry of a recipe's <c>screens</c>.</summary>
        public sealed class Screen
        {
            public int Step;
            public string Role = "goal";
            public List<string> Roots = new();
            /// <summary>P5 (fresh audit, K2) — the clickable names on screen after that press, as the teach saw them.</summary>
            public List<string> Buttons = new();
        }

        /// <summary>P5 — the ways a recipe is learned (<c>LEARNED_BY</c> on the box).</summary>
        public static readonly IReadOnlyList<string> LearnedByValues = new[] { "teach", "auto", "studio" };
        /// <summary>P5 — the kinds of story need (<c>NEED_KINDS</c> on the box).</summary>
        public static readonly IReadOnlyList<string> NeedKinds = new[] { "screen", "state", "cheat" };

        /// <summary>The recipes, or null with <paramref name="error"/>. Never throws.</summary>
        public static List<Recipe>? Read(string? text, out string? error)
        {
            error = null;
            JToken root;
            // the kit's one parser: dates stay strings, numbers are doubles, nesting is capped (NovaJson)
            try { root = NovaJson.ParseObject(text ?? ""); }
            catch (Exception e) { error = "recipes.json is not JSON: " + e.Message; return null; }
            try { return Read(root, out error); }
            catch (Exception e) { error = "recipes.json could not be read: " + e.Message; return null; }
        }

        public static List<Recipe>? Read(JToken root, out string? error)
        {
            error = null;
            if (root is not JObject o) return Fail(out error, "the document is not an object");
            if (!NovaJson.TryNumber(o["schemaVersion"], out var version) || version != SchemaVersion)
                return Fail(out error, $"schemaVersion is not {SchemaVersion}");
            if (!Str(o["gameId"], 1, int.MaxValue)) return Fail(out error, "gameId is not a string");
            if (!Str(o["updatedAt"], 1, int.MaxValue)) return Fail(out error, "updatedAt is not a string");
            if (o["recipes"] is not JArray list || list.Count > MaxRecipes) return Fail(out error, $"recipes is not a list of at most {MaxRecipes}");
            var recipes = new List<Recipe>();
            for (var i = 0; i < list.Count; i++)
            {
                var r = RecipeOf(list[i], out var why);
                if (r == null) return Fail(out error, $"recipes[{i}]: {why}");
                recipes.Add(r);
            }
            return recipes;
        }

        private static List<Recipe>? Fail(out string? error, string why)
        {
            error = "recipes.json does not parse: " + why;
            return null;
        }

        private static bool Str(JToken? t, int min, int max) =>
            t?.Type == JTokenType.String && t.Value<string>()!.Length >= min && t.Value<string>()!.Length <= max;

        private static bool StrOrNull(JToken? t, int max) => t?.Type == JTokenType.Null || Str(t, 0, max);

        private static bool Num(JToken? t, double min, double max) =>
            NovaJson.TryNumber(t, out var v) && v >= min && v <= max;

        private static bool Names(JToken? t, int cap) =>
            t is JArray a && a.Count <= cap && a.All(x => Str(x, 1, MaxNameChars));

        private static bool ElementOk(JToken? t) =>
            t == null || (t is JObject e && Str(e["name"], 1, MaxNameChars) && StrOrNull(e["label"], 400) && StrOrNull(e["handler"], 400));

        /// <summary>One step, in the recipe grammar — the kinds and fields <c>RecipeStepSchema</c> allows.</summary>
        public static string? StepRefusal(JToken? t)
        {
            if (t is not JObject s) return "a step is not an object";
            var kind = s["kind"]?.Type == JTokenType.String ? s["kind"]!.Value<string>() : null;
            switch (kind)
            {
                case "click":
                case "invoke-button":
                    return Str(s["name"], 1, MaxNameChars) && ElementOk(s["element"]) ? null : $"a {kind} step needs a name";
                case "hold":
                    return Str(s["name"], 1, MaxNameChars) && Num(s["seconds"], 0, 60) && ElementOk(s["element"]) ? null : "a hold step needs a name and 0–60 seconds";
                case "hold-at":
                    return Num(s["x"], 0, 1) && Num(s["y"], 0, 1) && Num(s["seconds"], 0, 60) ? null : "a hold-at step needs x, y in 0–1 and 0–60 seconds";
                case "tap-at":
                    return Num(s["x"], 0, 1) && Num(s["y"], 0, 1) ? null : "a tap-at step needs x, y in 0–1";
                case "drag":
                    return Num(s["x"], 0, 1) && Num(s["y"], 0, 1) && Num(s["x2"], 0, 1) && Num(s["y2"], 0, 1) && Num(s["seconds"], 0, 60)
                        ? null : "a drag step needs x, y, x2, y2 in 0–1 and 0–60 seconds";
                case "cheat":
                    return Str(s["command"], 1, 256) ? null : "a cheat step needs a command";
                default:
                    return $"'{kind}' is not a step kind";
            }
        }

        private static Recipe? RecipeOf(JToken t, out string? why)
        {
            why = null;
            if (t is not JObject r) { why = "not an object"; return null; }
            if (!Str(r["id"], 1, 64) || !IdRe.IsMatch(r["id"]!.Value<string>()!)) { why = "id is not r + 16 hex"; return null; }
            if (!Str(r["ask"], 1, MaxAsk)) { why = $"ask is not 1–{MaxAsk} characters"; return null; }
            if (r["learnedBy"]?.Type != JTokenType.String || !LearnedByValues.Contains(r["learnedBy"]!.Value<string>()!))
            { why = "learnedBy is not teach, auto or studio"; return null; }
            if (!Str(r["runId"], 1, 64)) { why = "runId is not a string"; return null; }
            if (!Str(r["taughtAt"], 1, int.MaxValue)) { why = "taughtAt is not a string"; return null; }
            var recipe = new Recipe { Id = r["id"]!.Value<string>()!, Ask = r["ask"]!.Value<string>()!, LearnedBy = r["learnedBy"]!.Value<string>()! };
            if (r["declaredStart"] is not JObject ds) { why = "declaredStart is not an object"; return null; }
            var startKind = ds["kind"]?.Type == JTokenType.String ? ds["kind"]!.Value<string>() : null;
            // kit 0.14.1 (invariant 187): the start screen's names — optional on lobby-after-boot, required on screen-here
            var namesTok = ds["names"];
            if (startKind == TeachAnalysis.StartLobby && (namesTok == null || Names(namesTok, TeachAnalysis.MaxStartNames)))
                recipe.StartKind = startKind;
            else if (startKind == TeachAnalysis.StartRecipe && Str(ds["recipeId"], 1, 64) && IdRe.IsMatch(ds["recipeId"]!.Value<string>()!))
            {
                recipe.StartKind = startKind;
                recipe.StartRecipeId = ds["recipeId"]!.Value<string>();
            }
            else if (startKind == TeachAnalysis.StartHere && Names(namesTok, TeachAnalysis.MaxStartNames))
                recipe.StartKind = startKind;
            else { why = "declaredStart is neither lobby-after-boot, a recipe with its id, nor screen-here with its names"; return null; }
            if (startKind != TeachAnalysis.StartRecipe && namesTok is JArray startNames)
                recipe.StartNames = startNames.Values<string>().Select(x => x!).ToList();
            if (r["stamp"] is not JObject stamp || !StrOrNull(stamp["gameCommit"], 64) || !Str(stamp["kitVersion"], 1, 40))
            { why = "stamp needs a kitVersion and a gameCommit (or null)"; return null; }
            recipe.GameCommit = stamp["gameCommit"]!.Type == JTokenType.String ? stamp["gameCommit"]!.Value<string>() : null;
            recipe.KitVersion = stamp["kitVersion"]!.Value<string>()!;
            if (r["steps"] is not JArray steps || steps.Count > MaxSteps) { why = $"steps is not a list of at most {MaxSteps}"; return null; }
            for (var k = 0; k < steps.Count; k++)
                if (StepRefusal(steps[k]) is { } bad) { why = $"steps[{k}]: {bad}"; return null; }
            recipe.Steps = steps;
            if (!Names(r["dismiss"], MaxSteps)) { why = "dismiss is not a short list of names"; return null; }
            if (!Names(r["arrival"], MaxNames)) { why = "arrival is not a list of names"; return null; }
            recipe.Dismiss = r["dismiss"]!.Values<string>().Select(x => x!).ToList();
            recipe.Arrival = r["arrival"]!.Values<string>().Select(x => x!).ToList();
            if (r["confirm"] is not JObject c || c["state"]?.Type != JTokenType.String
                || (c["state"]!.Value<string>() != "confirmed" && c["state"]!.Value<string>() != "unconfirmed")
                || !StrOrNull(c["why"], 1000) || c["replayRan"]?.Type != JTokenType.Boolean)
            { why = "confirm needs a state (confirmed | unconfirmed), a why (or null) and replayRan"; return null; }
            // fix 5b (invariant 185): which way it was confirmed — optional (absent on a recipe saved before), replay | picture
            if (c["by"] != null && (c["by"]!.Type != JTokenType.String
                || (c["by"]!.Value<string>() != "replay" && c["by"]!.Value<string>() != "picture")))
            { why = "confirm.by is neither replay nor picture"; return null; }
            recipe.Confirmed = c["state"]!.Value<string>() == "confirmed";
            // after Confirmed is read (a first version read it before, so ByPicture was always false — caught by the wayBack cases)
            recipe.ByPicture = recipe.Confirmed && c["by"]?.Type == JTokenType.String && c["by"]!.Value<string>() == "picture";
            recipe.Why = c["why"]!.Type == JTokenType.String ? c["why"]!.Value<string>() : null;
            if (r["screens"] is not JArray screens || screens.Count > MaxSteps) { why = "screens is not a short list"; return null; }
            foreach (var sc in screens)
            {
                if (sc is not JObject so || so["step"]?.Type != JTokenType.Integer || !NovaJson.TryNumber(so["step"], out var st) || st < 0
                    || so["role"]?.Type != JTokenType.String || (so["role"]!.Value<string>() != "goal" && so["role"]!.Value<string>() != "dismiss")
                    || !Names(so["roots"], MaxNames) || !Names(so["added"], MaxNames) || !Names(so["buttons"], MaxNames)
                    || !StrOrNull(so["thumbnailKey"], 300))
                { why = "a screen has the wrong shape"; return null; }
                recipe.Screens.Add(new Screen
                {
                    Step = so["step"]!.Value<int>(),
                    Role = so["role"]!.Value<string>()!,
                    Roots = so["roots"]!.Values<string>().Select(x => x!).ToList(),
                    Buttons = so["buttons"]!.Values<string>().Select(x => x!).ToList(),
                });
            }
            if (!StrOrNull(r["thumbnailKey"], 300)) { why = "thumbnailKey is not a string or null"; return null; }
            // P5 — two optional fields, read as zod reads `.optional()`: absent is fine, anything else must be the shape
            // (a JSON null is NOT absent — zod refuses it, so the kit does too).
            if (r.TryGetValue("need", out var needTok))
            {
                if (needTok is not JObject n || n["kind"]?.Type != JTokenType.String || !NeedKinds.Contains(n["kind"]!.Value<string>()!)
                    || !Str(n["name"], 1, MaxNameChars))
                { why = "need is not { kind: screen | state | cheat, name }"; return null; }
                recipe.Need = (n["kind"]!.Value<string>()!, n["name"]!.Value<string>()!);
            }
            if (r.TryGetValue("proof", out var proofTok))
            {
                if (proofTok is not JObject p || p["state"]?.Type != JTokenType.String
                    || (p["state"]!.Value<string>() != "proven" && p["state"]!.Value<string>() != "failed" && p["state"]!.Value<string>() != "pressed")
                    || !Str(p["at"], 1, int.MaxValue) || !Str(p["runId"], 1, 64)
                    || p["stamp"] is not JObject ps || !StrOrNull(ps["gameCommit"], 64) || !Str(ps["kitVersion"], 1, 40)
                    || !StrOrNull(p["why"], 1000))
                { why = "proof needs a state (proven | failed | pressed), at, a runId, a stamp and a why (or null)"; return null; }
                recipe.ProofState = p["state"]!.Value<string>();
            }
            // fix 5b audit H1 — optional, a number from 0 to 20 (zod's `.min(0).max(20).optional()`)
            if (r.TryGetValue("arrivalLagSec", out var lagTok))
            {
                if ((lagTok.Type != JTokenType.Integer && lagTok.Type != JTokenType.Float) || lagTok.Value<double>() < 0 || lagTok.Value<double>() > 20)
                { why = "arrivalLagSec is not a number from 0 to 20"; return null; }
                recipe.ArrivalLagSec = lagTok.Value<double>();
                recipe.HasArrivalLag = true;
            }
            return recipe;
        }
    }

    /// <summary>
    /// Learn-and-drive v3 P5 (§3.6a item 4) — A RECIPE'S CHAIN: a recipe's end is another's start, so to play recipe C
    /// that starts from B that starts from A, the kit plays A, then B, then C — A being the one that starts at the anchor,
    /// "lobby after boot". The server's <c>chainOf</c> (<c>recipe-book.ts</c>) is the twin, held to this by
    /// <c>recipe.cases.json</c>'s <c>chains</c>. Refused WHOLE — never a partial chain — when a start is missing, the starts
    /// loop, or the chain is deeper than <see cref="Max"/>.
    /// </summary>
    public static class RecipeChain
    {
        public const int Max = 8;

        /// <summary>The recipes to play, anchor first and <paramref name="id"/> last, or null with the reason.</summary>
        public static List<RecipeFile.Recipe>? Of(IReadOnlyList<RecipeFile.Recipe> book, string id, out string? error)
        {
            error = null;
            var byId = new Dictionary<string, RecipeFile.Recipe>(StringComparer.Ordinal);
            foreach (var r in book) byId[r.Id] = r;
            var chain = new List<RecipeFile.Recipe>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var at = id;
            while (true)
            {
                if (!byId.TryGetValue(at, out var r))
                {
                    error = chain.Count == 0
                        ? $"no recipe {at} in this game's recipe book"
                        : $"recipe {chain[0].Id} starts from recipe {at}, which is not in the recipe book";
                    return null;
                }
                if (!seen.Add(at))
                {
                    error = $"the starts loop back to recipe {at} — a chain must begin at \"lobby after boot\" or at a screen it was taught from";
                    return null;
                }
                chain.Insert(0, r);
                if (chain.Count > Max)
                {
                    error = $"recipe {id}'s chain is deeper than {Max} recipes — teach a shorter start";
                    return null;
                }
                // kit 0.14.1 (invariant 187): a recipe taught "from here" is an anchor too — it starts on the screen it was
                // taught from, which the start check asks for (by its names) before anything is pressed, never from boot blindly
                if (r.StartKind == TeachAnalysis.StartLobby || r.StartKind == TeachAnalysis.StartHere) return chain;
                at = r.StartRecipeId ?? "";
            }
        }
    }
}
