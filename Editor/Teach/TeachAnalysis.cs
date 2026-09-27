using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Learn-and-drive v3 P4 (§3.4, §3.6a item 2) — WHAT A TEACH MEANS: which presses were closing popups, which were the
    /// path, where it arrived, and whether one replay arrived the same way. Pure over the recorded JSON (the presses as
    /// <see cref="TeachRecorder.PressJson"/> writes them, the replay as <see cref="RecipeReplay"/> writes it), so the kit
    /// and the server run the SAME rule over the SAME document: the server re-grades every teach with its twin
    /// (<c>analyseTeach</c> in <c>apps/renderer/src/game-ads/learn/recipes.ts</c>) against the names IT sent, and
    /// <c>Editor/Tests/Fixtures/recipe.cases.json</c> holds both to one verdict per case.
    ///
    /// <b>DISMISS vs GOAL, by what the press did to the screen</b> (no word list, any language):
    /// <list type="bullet">
    /// <item>a press that OPENED a new root (a root in its after that was not in its before) is a GOAL step — even if it
    /// also removed its own root: that is navigation;</item>
    /// <item>a press that REMOVED THE ROOT IT WAS PRESSED ON and opened none is a DISMISS — its selector goes to
    /// <c>dismiss[]</c>, which the replay taps whenever it shows (a loop), never as a fixed step;</item>
    /// <item>anything else (a tab inside one screen, a press whose screen did not change at root level, a gesture, a
    /// cheat) stays a GOAL step, in order — dropping it could break the path.</item>
    /// </list>
    ///
    /// <b>ARRIVAL</b> = the names new after the LAST GOAL press (its after's <c>added</c>, measured against the screen at
    /// that press) that are AUTHORED — present in the export's names, which the server sent with the job. A runtime clone,
    /// a tween object or a tooltip is not in the export and never counts. At least <see cref="MinArrival"/> are needed.
    /// A trailing dismiss does not move the baseline.
    ///
    /// <b>CONFIRMED</b> = one immediate replay arrived the same way: at least <see cref="MinArrival"/> of those names were
    /// new after the replay's last goal press too (two observations). Anything less is UNCONFIRMED, with the reason — the
    /// recipe is kept, shown and re-taught, never silently trusted.
    /// </summary>
    public static class TeachAnalysis
    {
        public const int MinArrival = 2;

        public const string StartLobby = "lobby-after-boot";
        public const string StartRecipe = "recipe";

        public sealed class Result
        {
            /// <summary>Indices into the presses of the goal steps, in order.</summary>
            public List<int> Goal = new();
            /// <summary>Indices of the dismiss presses.</summary>
            public List<int> DismissPresses = new();
            /// <summary>The distinct dismiss selectors, in first-seen order.</summary>
            public List<string> Dismiss = new();
            /// <summary>The recipe's steps (the goal presses in the step grammar).</summary>
            public JArray Steps = new();
            /// <summary>Authored names new after the last goal press, at the teach.</summary>
            public List<string> TeachArrival = new();
            /// <summary>The recipe's arrival: the names BOTH observations saw when confirmed, else the teach's.</summary>
            public List<string> Arrival = new();
            public bool Confirmed;
            /// <summary>Why it is not confirmed, or null.</summary>
            public string? Why;
            /// <summary>Why a replay must not even be tried (the teach itself is not replayable), or null.</summary>
            public string? ReplayBlocked;
        }

        private static string Str(JToken? t) => t?.Type == JTokenType.String ? t.Value<string>() ?? "" : "";

        private static List<string> Strings(JToken? t) =>
            t is JArray a ? a.Where(x => x.Type == JTokenType.String).Select(x => x.Value<string>() ?? "").ToList() : new List<string>();

        /// <summary>Is this press a dismiss: it removed the root it was pressed on and opened no root.</summary>
        public static bool IsDismiss(JObject press)
        {
            var fired = Str(press["fired"]);
            if (fired != TeachRecorder.FiredClick && fired != TeachRecorder.FiredInvokeButton) return false;
            if (Str(press["selector"]).Length == 0) return false;
            var root = Str(press["pressRoot"]);
            if (root.Length == 0 || press["after"] is not JObject after) return false;
            var before = new HashSet<string>(Strings(press["before"]?["roots"]), StringComparer.Ordinal);
            var afterRoots = new HashSet<string>(Strings(after["roots"]), StringComparer.Ordinal);
            if (afterRoots.Any(r => !before.Contains(r))) return false; // it opened something: navigation, a goal
            return before.Contains(root) && !afterRoots.Contains(root);
        }

        /// <summary>A goal press as a recipe step, in the step grammar.</summary>
        public static JObject StepOf(JObject press)
        {
            var fired = Str(press["fired"]);
            var sel = Str(press["selector"]);
            JObject s;
            switch (fired)
            {
                case TeachRecorder.FiredCheat:
                    return new JObject { ["kind"] = "cheat", ["command"] = Str(press["command"]) };
                case TeachRecorder.FiredTapAt:
                    return new JObject { ["kind"] = "tap-at", ["x"] = press["x"] ?? 0, ["y"] = press["y"] ?? 0 };
                case TeachRecorder.FiredDrag:
                    return new JObject
                    {
                        ["kind"] = "drag", ["x"] = press["x"] ?? 0, ["y"] = press["y"] ?? 0,
                        ["x2"] = press["x2"] ?? 0, ["y2"] = press["y2"] ?? 0, ["seconds"] = press["seconds"] ?? 0,
                    };
                case TeachRecorder.FiredHold when sel.Length == 0:
                    return new JObject { ["kind"] = "hold-at", ["x"] = press["x"] ?? 0, ["y"] = press["y"] ?? 0, ["seconds"] = press["seconds"] ?? 0 };
                case TeachRecorder.FiredHold:
                    s = new JObject { ["kind"] = "hold", ["name"] = sel, ["seconds"] = press["seconds"] ?? 0 };
                    break;
                case TeachRecorder.FiredInvokeButton:
                    s = new JObject { ["kind"] = "invoke-button", ["name"] = sel };
                    break;
                default:
                    s = new JObject { ["kind"] = "click", ["name"] = sel };
                    break;
            }
            if (press["element"] is JObject el) s["element"] = el.DeepClone();
            return s;
        }

        /// <summary>
        /// THE RULE. <paramref name="presses"/> as the recorder wrote them; <paramref name="authored"/> = the export's names
        /// the SERVER sent; <paramref name="replay"/> = the confirming replay's facts, or null when none ran;
        /// <paramref name="declaredStart"/> = where the recipe starts.
        /// </summary>
        public static Result Analyse(JArray presses, IReadOnlyCollection<string> authored, JObject? replay, JObject? declaredStart)
        {
            var r = new Result();
            var authoredSet = authored as HashSet<string> ?? new HashSet<string>(authored, StringComparer.Ordinal);
            for (var i = 0; i < presses.Count; i++)
            {
                if (presses[i] is not JObject p) continue;
                if (IsDismiss(p))
                {
                    r.DismissPresses.Add(i);
                    var sel = Str(p["selector"]);
                    if (!r.Dismiss.Contains(sel)) r.Dismiss.Add(sel);
                }
                else
                {
                    r.Goal.Add(i);
                    r.Steps.Add(StepOf(p));
                }
            }
            if (r.Goal.Count > 0 && presses[r.Goal[r.Goal.Count - 1]] is JObject last)
                r.TeachArrival = Strings(last["after"]?["added"]).Where(authoredSet.Contains)
                    .Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
            r.Arrival = r.TeachArrival;

            r.ReplayBlocked = Blocked(presses, r, declaredStart);
            if (r.ReplayBlocked != null)
            {
                r.Why = r.ReplayBlocked;
                return r;
            }
            if (replay == null || replay["ran"]?.Type != JTokenType.Boolean || !replay["ran"]!.Value<bool>())
            {
                var why = Str(replay?["why"]);
                r.Why = "the confirming replay did not run" + (why.Length > 0 ? ": " + why : "");
                return r;
            }
            var steps = replay["steps"] as JArray ?? new JArray();
            if (steps.Count != r.Goal.Count)
            {
                r.Why = $"the replay reported {steps.Count} step(s) for a recipe of {r.Goal.Count}";
                return r;
            }
            for (var k = 0; k < steps.Count; k++)
            {
                var s = steps[k] as JObject;
                if (s?["pressed"]?.Type == JTokenType.Boolean && s["pressed"]!.Value<bool>()) continue;
                var why = Str(s?["refused"]);
                r.Why = $"the replay could not press step {k + 1}" + (why.Length > 0 ? ": " + why : "");
                return r;
            }
            var replayAdded = new HashSet<string>(Strings(steps[steps.Count - 1]["after"]?["added"]), StringComparer.Ordinal);
            var both = r.TeachArrival.Where(replayAdded.Contains).ToList();
            if (both.Count < MinArrival)
            {
                r.Why = $"the replay did not arrive the same way: {both.Count} of the {r.TeachArrival.Count} arrival names " +
                        $"appeared again (need {MinArrival})" + (both.Count > 0 ? ": " + string.Join(", ", both) : "");
                return r;
            }
            r.Arrival = both;
            r.Confirmed = true;
            return r;
        }

        /// <summary>Why this teach cannot be confirmed by a replay at all, or null. Checked BEFORE a replay is run, so the
        /// kit never presses a path it already knows it cannot confirm.</summary>
        private static string? Blocked(JArray presses, Result r, JObject? declaredStart)
        {
            if (presses.Count == 0) return "nothing was pressed between Teach and Stop";
            if (r.Goal.Count == 0) return "every press closed something — no press opened a screen";
            for (var k = 0; k < r.Goal.Count; k++)
            {
                var p = (JObject)presses[r.Goal[k]];
                if (p["shared"]?.Type == JTokenType.Boolean && p["shared"]!.Value<bool>())
                    return $"step {k + 1} '{Str(p["selector"])}' shares its name with other objects on that screen and no label " +
                           "tells it apart — refused as unstable (a replay could press another one)";
            }
            if (r.TeachArrival.Count < MinArrival)
                return $"after the last step only {r.TeachArrival.Count} name(s) from the game's own files appeared " +
                       $"(need {MinArrival}) — the screen it reached cannot be told from the one before" +
                       (r.TeachArrival.Count > 0 ? ": " + string.Join(", ", r.TeachArrival) : "");
            if (Str(declaredStart?["kind"]) == StartRecipe)
                return "it starts from another recipe — a teach's confirming replay does not play recipe chains yet (deferred; a Try of this recipe plays its chain)";
            for (var k = 0; k < r.Goal.Count; k++)
            {
                var fired = Str(((JObject)presses[r.Goal[k]])["fired"]);
                if (fired == TeachRecorder.FiredInvokeButton)
                    return $"step {k + 1} fired invoke-button (a Button reached past the pointer), which a replay does not press — " +
                           "it is a bridge verb outside the press guard";
                if (fired == TeachRecorder.FiredTapAt || fired == TeachRecorder.FiredDrag
                    || (fired == TeachRecorder.FiredHold && Str(((JObject)presses[r.Goal[k]])["selector"]).Length == 0))
                    return $"step {k + 1} is a gesture on the game world ({fired}), which a replay does not press yet";
            }
            return null;
        }
    }
}
