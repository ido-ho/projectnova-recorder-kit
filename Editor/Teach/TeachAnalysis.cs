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
    /// <b>DISMISS vs GOAL, by what the press did to the screen and what it pressed</b> (structure first; a word list only as
    /// a second signal):
    /// <list type="bullet">
    /// <item>a press that OPENED a new root (a root in its after that was not in its before) is a GOAL step — even if it
    /// also removed its own root: that is navigation;</item>
    /// <item>a press that REMOVED THE ROOT IT WAS PRESSED ON and opened none is a DISMISS — its selector goes to
    /// <c>dismiss[]</c>, which the replay taps whenever it shows (a loop), never as a fixed step;</item>
    /// <item>kit 0.14.1 (invariant 186): a press on a SCREEN-WIDE CATCHER, or a close-named press inside a MODAL, is a
    /// dismiss too — measured structurally at the press (<see cref="OverlayFacts"/>), a name only ever the second signal
    /// (<see cref="PopupCloseKind"/>). A game whose popups all live under one persistent root never removes a root when
    /// one closes, so rule one alone kept its "tap anywhere to claim" as a path step;</item>
    /// <item>anything else (a tab inside one screen, a press whose screen did not change at root level, a gesture, a
    /// cheat) stays a GOAL step, in order — dropping it could break the path.</item>
    /// </list>
    ///
    /// <b>ARRIVAL</b> = the names new after the LAST GOAL press (its after's <c>added</c>, measured against the screen at
    /// that press — kit 0.14.0: on the screen the person STOPPED on, when the teach carries one AND it holds at least
    /// <see cref="MinArrival"/> authored names, <see cref="ArrivalSource"/>) that are AUTHORED — present in the export's names, which the server sent with the job. A runtime clone,
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
        /// <summary>Kit 0.14.1 (invariant 187) — "Teach from here": the recipe starts on the screen the game was on when the
        /// person pressed Teach (no Play restart); its declared start carries that screen's names.</summary>
        public const string StartHere = "screen-here";
        /// <summary>Invariant 187 — the most start names a recipe keeps.</summary>
        public const int MaxStartNames = 40;

        /// <summary>
        /// Invariant 187 — THE START SCREEN'S NAMES a recipe keeps: of the names on screen when the teach began that were
        /// GONE when the person pressed Stop (the kit sends them raw — a name the whole game shows, a HUD, cancels out), the
        /// AUTHORED ones (the export's names the server sent), distinct, ordinal-sorted, at most <see cref="MaxStartNames"/>.
        /// No screen name is known to the rule: "which screen is this" is whatever the teach saw leave. The renderer's
        /// <c>startNamesOf</c>.
        /// </summary>
        public static List<string> StartNamesOf(JToken? raw, ICollection<string> authored) =>
            Strings(raw).Where(authored.Contains).Distinct().OrderBy(n => n, StringComparer.Ordinal).Take(MaxStartNames).ToList();

        /// <summary>Audit M3 — fewer start names than this say too little about a screen: they are ignored (the first press
        /// alone then says the start is up).</summary>
        public const int MinStartNames = 3;

        /// <summary>Invariant 187 — is a start screen with these names up: at least <see cref="MinArrival"/> of them on screen,
        /// or fewer than <see cref="MinStartNames"/> are known (then only the first press says so).</summary>
        public static bool StartNamesUp(IReadOnlyCollection<string> names, ICollection<string> onScreen) =>
            names.Count < MinStartNames || names.Count(onScreen.Contains) >= MinArrival;

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
            /// <summary>Kit 0.14.0 — the teach's arrival was read on the Stop screen (not one beat after the last press).</summary>
            public bool ArrivalFromStop;
            /// <summary>The recipe's arrival: the names BOTH observations saw when confirmed, else the teach's.</summary>
            public List<string> Arrival = new();
            public bool Confirmed;
            /// <summary>Why it is not confirmed, or null.</summary>
            public string? Why;
            /// <summary>Why a replay must not even be tried (the teach itself is not replayable), or null.</summary>
            public string? ReplayBlocked;
            /// <summary>Fix 5b (invariant 185) — too few arrival names is the ONLY refusal AND the teach carries a person's Stop
            /// picture (<see cref="PictureStop"/>): the replay still runs, reading its end at the teach's lag, so the box can
            /// compare the two Stop pictures. The renderer's <c>replayForPicture</c>.</summary>
            public bool ReplayForPicture;
            /// <summary>Fix 5b — <see cref="ReplayForPicture"/> AND the replay ran with every step pressed: the box may confirm
            /// this teach from its two pictures. Names alone still leave it unconfirmed, with the too-few why.</summary>
            public bool PictureCheck;
        }

        private static string Str(JToken? t) => t?.Type == JTokenType.String ? t.Value<string>() ?? "" : "";

        private static List<string> Strings(JToken? t) =>
            t is JArray a ? a.Where(x => x.Type == JTokenType.String).Select(x => x.Value<string>() ?? "").ToList() : new List<string>();

        /// <summary>Is this press a dismiss (a popup close), by <see cref="PopupCloseKind"/>. The split a teach runs — with
        /// the one exception <see cref="GoalIndices"/> makes — is this over every press.</summary>
        public static bool IsDismiss(JObject press) => PopupCloseKind(press) != null;

        /// <summary>Invariant 186 — a pressed object covering at least this share of the screen is screen-wide.</summary>
        public const double CoverMin = 0.8;

        /// <summary>Invariant 186 — the WORD HINTS of a popup close, a SECOND signal only (never enough alone): whole words of
        /// the pressed object's name after the kit's one split (<see cref="PressGuard.Words"/>: camelCase humps and
        /// non-letters, so <c>TapToContinue</c>, <c>tap_anywhere</c>, <c>Btn_Close</c>, <c>BG_Dimmer</c> all split), matched
        /// exactly (so <c>x</c> never matches <c>xp</c>). Two adjacent words also count joined (<c>Screen</c>+<c>Wide</c>,
        /// <c>Full</c>+<c>Screen</c>). <c>back</c> is deliberately absent: a Back button is navigation. The renderer's
        /// <c>CLOSE_HINTS</c> is the same list (<c>recipe.cases.json</c>'s <c>closeHints</c> pins both).</summary>
        public static readonly IReadOnlyList<string> CloseHints = new[]
        {
            "close", "dismiss", "skip", "continue", "exit", "cancel", "x", "ok", "okay", "later", "hide", "done",
            "claim", "collect", "tap", "anywhere", "outside",
            "blocker", "dimmer", "dim", "shade", "overlay", "backdrop", "background", "bg", "curtain", "veil",
            "fullscreen", "screenwide",
        };

        /// <summary>The pressed object's name carries a close hint (<see cref="CloseHints"/>) — its selector's name part, or
        /// its recorded element name.</summary>
        public static bool HasCloseHint(JObject press)
        {
            foreach (var text in new[] { PressGuard.NameOf(Str(press["selector"])), Str(press["element"]?["name"]) })
            {
                var words = PressGuard.Words(text).ToList();
                for (var i = 0; i < words.Count; i++)
                {
                    if (CloseHints.Contains(words[i])) return true;
                    if (i + 1 < words.Count && CloseHints.Contains(words[i] + words[i + 1])) return true;
                }
            }
            return false;
        }

        public const string CloseByRoot = "root";
        public const string CloseByCatcher = "catcher";
        public const string CloseByModal = "modal";

        private static bool Flag(JToken? t) => t?.Type == JTokenType.Boolean && t.Value<bool>();

        /// <summary>The press opened a panel root that was not on screen before it.</summary>
        public static bool OpenedRoot(JObject press)
        {
            if (press["after"] is not JObject after) return false;
            var before = new HashSet<string>(Strings(press["before"]?["roots"]), StringComparer.Ordinal);
            return Strings(after["roots"]).Any(r => !before.Contains(r));
        }

        /// <summary>
        /// Invariant 186 — IS THIS PRESS A POPUP CLOSE, and by which rule (null = a path step). Structural first, for any
        /// game; a word is only ever the second signal:
        /// <list type="number">
        /// <item><c>root</c> (unchanged since P4): it removed the panel root it was pressed on and opened none;</item>
        /// <item><c>catcher</c>: the pressed object covers the screen (<c>overlay.cover</c> ≥ <see cref="CoverMin"/>) AND is
        /// invisible (<c>overlay.clear</c>) or carries a close hint AND was gone a beat later (<c>overlay.gone</c>, audit M4 —
        /// one that stays is a gameplay tap surface) — a "tap anywhere" / dimmer-that-closes. Even when it opened a root (the
        /// next reward screen): tapping a screen-wide catcher moves past a popup;</item>
        /// <item><c>modal</c>: it carries a close hint AND sits in a modal — a translucent screen-covering shade behind it
        /// (<c>overlay.shade</c>) or a canvas drawn above the main one (<c>overlay.above</c>) — AND opened no new root.</item>
        /// </list>
        /// A press with no <c>overlay</c> facts (a kit before 0.14.1) is judged by rule 1 alone, exactly as before. A word
        /// with no structural signal is never a close: a "Claim" button on a flat panel stays a step.
        /// </summary>
        public static string? PopupCloseKind(JObject press)
        {
            var fired = Str(press["fired"]);
            if (fired != TeachRecorder.FiredClick && fired != TeachRecorder.FiredInvokeButton) return null;
            if (Str(press["selector"]).Length == 0) return null;
            var root = Str(press["pressRoot"]);
            if (root.Length > 0 && press["after"] is JObject after && !OpenedRoot(press))
            {
                var before = new HashSet<string>(Strings(press["before"]?["roots"]), StringComparer.Ordinal);
                if (before.Contains(root) && !Strings(after["roots"]).Contains(root)) return CloseByRoot;
            }
            if (press["overlay"] is not JObject o) return null;
            var cover = NovaJson.TryNumber(o["cover"], out var c) ? c : 0;
            var hint = HasCloseHint(press);
            // audit M4: a screen-wide catcher that is still there after the press is a gameplay tap surface (tap to roll) — a step
            if (cover >= CoverMin && (Flag(o["clear"]) || hint) && Flag(o["gone"])) return CloseByCatcher;
            if (hint && (Flag(o["shade"]) || Flag(o["above"])) && !OpenedRoot(press)) return CloseByModal;
            return null;
        }

        /// <summary>
        /// Invariant 186 — THE GOAL PRESSES of a teach, in order: every press that is not a popup close — except that when
        /// NO press would be a goal, the screen-wide catchers that opened a new root are the path after all (a result
        /// screen's "tap to continue" that IS the way on). The dismiss presses are the rest.
        /// </summary>
        public static List<int> GoalIndices(JArray presses)
        {
            var goal = new List<int>();
            var catchersThatOpened = new List<int>();
            for (var i = 0; i < presses.Count; i++)
            {
                if (presses[i] is not JObject p) continue;
                var kind = PopupCloseKind(p);
                if (kind == null) goal.Add(i);
                else if (kind == CloseByCatcher && OpenedRoot(p)) catchersThatOpened.Add(i);
            }
            return goal.Count > 0 ? goal : catchersThatOpened;
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
        /// Kit 0.14.0 (fix 5a) — THE TEACH'S ARRIVAL NAMES: the names new on the screen the person STOPPED on
        /// (<paramref name="stop"/>'s <c>added</c> — the recorder measures them against the last goal press's own "before",
        /// the baseline that press's <c>after.added</c> uses), when the teach carries a Stop screen; else, as before, the names
        /// new one beat after the last goal press (its <c>after.added</c>). A teach from a kit before 0.14.0 carries no Stop
        /// screen and reads exactly as it always did. Why: a destination that builds itself after the one-second beat (RL's
        /// fight, seconds after PlayButton → ButtonRoll) showed the screen before it, and its names were never counted.
        /// </summary>
        /// <remarks>The Stop screen is used only when it yields at least <see cref="MinArrival"/> AUTHORED names (audit H2):
        /// a person who closed the destination (a trailing dismiss) before Stop keeps today's arrival, the names one beat
        /// after the last goal press. <paramref name="fromStop"/> says which screen the arrival came from — the confirming
        /// replay then reads its own end at the SAME lag (<see cref="ReplayEndWaitSec"/>). Returns the authored names,
        /// distinct, ordinal-sorted.</remarks>
        public static List<string> ArrivalSource(JObject? lastGoal, JObject? stop, ICollection<string> authored, out bool fromStop)
        {
            List<string> Of(JToken? t) => Strings(t).Where(authored.Contains).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
            fromStop = false;
            // audit L4: a stop at the recording's time limit shows whatever idle screen is up — never the arrival
            if (stop is { } s && s["added"] is JArray && !StoppedAtTimeLimit(s))
            {
                var atStop = Of(s["added"]);
                if (atStop.Count >= MinArrival)
                {
                    fromStop = true;
                    return atStop;
                }
            }
            return Of(lastGoal?["after"]?["added"]);
        }

        /// <summary>Kit 0.14.0 (audit L4) — the Stop screen was taken when the recording hit its time limit.</summary>
        public static bool StoppedAtTimeLimit(JObject? stop) =>
            stop?["timeLimit"]?.Type == JTokenType.Boolean && stop["timeLimit"]!.Value<bool>();

        /// <summary>What the recipe's why says of a teach stopped at its time limit (audit L4).</summary>
        public const string TimeLimitWhy =
            "stopped at the time limit, not by Stop — the arrival was read one beat after the last press, not on the screen at the stop";

        /// <summary>The longest the confirming replay waits after its last step to read its end (audit H1).</summary>
        public const double MaxReplayEndWaitSec = 20;

        /// <summary>Kit 0.14.0 (audit H1) — how long the confirming replay waits after its LAST step before it reads the
        /// screen: the teach's own lag from its last goal press to Stop (<c>stop.afterLastGoalSec</c>, capped at
        /// <see cref="MaxReplayEndWaitSec"/>) when the arrival came from the Stop screen; else 0 (today's one settle).</summary>
        /// <remarks>Fix 5b (invariant 185): also when the replay runs for its PICTURE (<see cref="Result.ReplayForPicture"/>)
        /// — the box compares <c>replay-stop.jpg</c> with <c>stop.jpg</c>, so both are taken the same time after the last
        /// press (a world-space fight builds itself seconds later; at one settle the replay's picture shows the board).</remarks>
        public static double ReplayEndWaitSec(Result r, JObject? stop) =>
            (r.ArrivalFromStop || r.ReplayForPicture) && NovaJson.TryNumber(stop?["afterLastGoalSec"], out var lag) && lag > 0
                ? Math.Min(lag, MaxReplayEndWaitSec)
                : 0;

        /// <summary>
        /// THE RULE. <paramref name="presses"/> as the recorder wrote them; <paramref name="authored"/> = the export's names
        /// the SERVER sent; <paramref name="replay"/> = the confirming replay's facts, or null when none ran;
        /// <paramref name="declaredStart"/> = where the recipe starts; <paramref name="stop"/> = the screen at Stop
        /// (<see cref="TeachRecorder.StopJson"/>), or null (a kit before 0.14.0) — see <see cref="ArrivalSource"/>.
        /// </summary>
        public static Result Analyse(JArray presses, IReadOnlyCollection<string> authored, JObject? replay, JObject? declaredStart,
            JObject? stop = null)
        {
            var r = AnalyseCore(presses, authored, replay, declaredStart, stop);
            // audit L4: a stop at the time limit is said in the why, whatever the verdict
            if (StoppedAtTimeLimit(stop)) r.Why = r.Why == null ? TimeLimitWhy : r.Why + " (" + TimeLimitWhy + ")";
            return r;
        }

        private static Result AnalyseCore(JArray presses, IReadOnlyCollection<string> authored, JObject? replay, JObject? declaredStart,
            JObject? stop)
        {
            var r = new Result();
            var authoredSet = authored as HashSet<string> ?? new HashSet<string>(authored, StringComparer.Ordinal);
            var goals = new HashSet<int>(GoalIndices(presses));
            for (var i = 0; i < presses.Count; i++)
            {
                if (presses[i] is not JObject p) continue;
                if (!goals.Contains(i))
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
            {
                r.TeachArrival = ArrivalSource(last, stop, authoredSet, out var fromStop);
                r.ArrivalFromStop = fromStop;
            }
            r.Arrival = r.TeachArrival;

            var namesWhy = Blocked(presses, r, declaredStart);
            // fix 5b (invariant 185): too few arrival names is the ONLY refusal, and the Stop picture can be judged — the
            // replay runs anyway (for its end picture); the names' why stands, and the box decides from the pictures
            r.ReplayForPicture = namesWhy != null && Blocked(presses, r, declaredStart, allowTooFew: true) == null && PictureStop(stop);
            r.ReplayBlocked = r.ReplayForPicture ? null : namesWhy;
            if (namesWhy != null)
            {
                r.Why = namesWhy;
                r.PictureCheck = r.ReplayForPicture && ReplayFault(replay, r.Goal.Count) == null;
                return r;
            }
            var fault = ReplayFault(replay, r.Goal.Count);
            if (fault != null)
            {
                r.Why = fault;
                return r;
            }
            var steps = (JArray)replay!["steps"]!;
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

        /// <summary>Fix 5b (invariant 185) — a Stop screen the box may judge from its PICTURE: a person's Stop (never the
        /// time limit's) that named its <c>stop.jpg</c>. A kit before 0.14.0 sends none. The renderer's <c>pictureStop</c>.</summary>
        public static bool PictureStop(JObject? stop) =>
            stop != null && !StoppedAtTimeLimit(stop) && stop["thumbnail"]?.Type == JTokenType.String && Str(stop["thumbnail"]).Length > 0;

        /// <summary>Why the confirming replay does not count (it did not run, reported another step count, or could not
        /// press a step), or null when it ran every step.</summary>
        private static string? ReplayFault(JObject? replay, int goalCount)
        {
            if (replay == null || replay["ran"]?.Type != JTokenType.Boolean || !replay["ran"]!.Value<bool>())
            {
                var why = Str(replay?["why"]);
                return "the confirming replay did not run" + (why.Length > 0 ? ": " + why : "");
            }
            var steps = replay["steps"] as JArray ?? new JArray();
            if (steps.Count != goalCount) return $"the replay reported {steps.Count} step(s) for a recipe of {goalCount}";
            for (var k = 0; k < steps.Count; k++)
            {
                var s = steps[k] as JObject;
                if (s?["pressed"]?.Type == JTokenType.Boolean && s["pressed"]!.Value<bool>()) continue;
                var why = Str(s?["refused"]);
                return $"the replay could not press step {k + 1}" + (why.Length > 0 ? ": " + why : "");
            }
            return null;
        }

        /// <summary>Why this teach cannot be confirmed by its NAMES, or null. Checked BEFORE a replay is run, so the kit
        /// never presses a path it already knows it cannot confirm. <paramref name="allowTooFew"/> skips the too-few-names
        /// refusal (fix 5b: it no longer blocks the replay when the Stop picture can be judged).</summary>
        private static string? Blocked(JArray presses, Result r, JObject? declaredStart, bool allowTooFew = false)
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
            if (!allowTooFew && r.TeachArrival.Count < MinArrival)
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
