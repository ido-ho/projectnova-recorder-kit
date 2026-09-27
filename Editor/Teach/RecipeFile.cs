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
            public string? GameCommit;
            public string KitVersion = "";
            public JArray Steps = new();
            public List<string> Dismiss = new();
            public List<string> Arrival = new();
            public bool Confirmed;
            public string? Why;
        }

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
            if (r["learnedBy"]?.Type != JTokenType.String || r["learnedBy"]!.Value<string>() != "teach") { why = "learnedBy is not teach"; return null; }
            if (!Str(r["runId"], 1, 64)) { why = "runId is not a string"; return null; }
            if (!Str(r["taughtAt"], 1, int.MaxValue)) { why = "taughtAt is not a string"; return null; }
            var recipe = new Recipe { Id = r["id"]!.Value<string>()!, Ask = r["ask"]!.Value<string>()! };
            if (r["declaredStart"] is not JObject ds) { why = "declaredStart is not an object"; return null; }
            var startKind = ds["kind"]?.Type == JTokenType.String ? ds["kind"]!.Value<string>() : null;
            if (startKind == TeachAnalysis.StartLobby) recipe.StartKind = startKind;
            else if (startKind == TeachAnalysis.StartRecipe && Str(ds["recipeId"], 1, 64) && IdRe.IsMatch(ds["recipeId"]!.Value<string>()!))
            {
                recipe.StartKind = startKind;
                recipe.StartRecipeId = ds["recipeId"]!.Value<string>();
            }
            else { why = "declaredStart is neither lobby-after-boot nor a recipe with its id"; return null; }
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
            recipe.Confirmed = c["state"]!.Value<string>() == "confirmed";
            recipe.Why = c["why"]!.Type == JTokenType.String ? c["why"]!.Value<string>() : null;
            if (r["screens"] is not JArray screens || screens.Count > MaxSteps) { why = "screens is not a short list"; return null; }
            foreach (var sc in screens)
            {
                if (sc is not JObject so || so["step"]?.Type != JTokenType.Integer || !NovaJson.TryNumber(so["step"], out var st) || st < 0
                    || so["role"]?.Type != JTokenType.String || (so["role"]!.Value<string>() != "goal" && so["role"]!.Value<string>() != "dismiss")
                    || !Names(so["roots"], MaxNames) || !Names(so["added"], MaxNames) || !Names(so["buttons"], MaxNames)
                    || !StrOrNull(so["thumbnailKey"], 300))
                { why = "a screen has the wrong shape"; return null; }
            }
            if (!StrOrNull(r["thumbnailKey"], 300)) { why = "thumbnailKey is not a string or null"; return null; }
            return recipe;
        }
    }
}
