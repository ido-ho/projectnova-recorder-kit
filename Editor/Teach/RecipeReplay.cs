using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.EventSystems;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Learn-and-drive v3 P4 (§3.6a item 3) — FINDING AN ELEMENT THAT MOVED: when the exact element of a step is gone
    /// (a game update renamed or relabelled it), an element on screen that matches TWO of its THREE parts (name, label,
    /// handler — <see cref="ElementIdentity"/>) is a CANDIDATE. Only a candidate:
    /// <list type="bullet">
    /// <item>it is pressed only when its NAME is on the run's allowed names (<see cref="PressGuard.Refusal"/> — the same
    /// fence every gated press passes), so a relabelled button with a new name is refused, never guessed at;</item>
    /// <item>it is proven only by ARRIVAL (<see cref="TeachAnalysis"/>): a candidate that presses but does not arrive
    /// leaves the teach unconfirmed;</item>
    /// <item>the recipe is never rewritten to it, and the cloud never composes the command — the candidate is an element
    /// the kit itself found on this screen.</item>
    /// </list>
    /// Two candidates tied at the best match are refused as ambiguous: the kit does not pick one.
    /// </summary>
    public static class RecipeRebind
    {
        public const int MinParts = 2;

        public sealed class Found
        {
            public ElementIdentity? Candidate;
            public int Parts;
            /// <summary>Why there is no candidate (none matches, a tie), or null.</summary>
            public string? Why;
        }

        public static Found Find(ElementIdentity want, IEnumerable<ElementIdentity> onScreen)
        {
            var scored = onScreen.Select(e => (e, n: want.PartsMatching(e))).Where(x => x.n >= MinParts)
                .OrderByDescending(x => x.n).ToList();
            if (scored.Count == 0)
                return new Found { Why = $"nothing on screen matches {MinParts} of the three parts of '{want}'" };
            if (scored.Count > 1 && scored[1].n == scored[0].n)
                return new Found { Why = $"{scored.Count(x => x.n == scored[0].n)} elements match {scored[0].n} of the three parts of '{want}' — the kit does not pick one" };
            return new Found { Candidate = scored[0].e, Parts = scored[0].n };
        }

        /// <summary>The selector a candidate is pressed by: <c>Name@Label</c> when it has a label, else its name.</summary>
        public static string SelectorOf(ElementIdentity e) => e.Label != null ? e.Name + "@" + e.Label : e.Name;
    }

    /// <summary>What the replay reads and presses. The kit's is <see cref="UguiReplayScreen"/>; the tests hand a fake.</summary>
    public interface IReplayScreen
    {
        ScreenSignature.Observation Observe();
        /// <summary>Every clickable element on screen, by its three parts (for the 2-of-3 rebind).</summary>
        IReadOnlyList<ElementIdentity> Clickables();
        bool Exists(string selector);
        bool Click(string selector);
        void PointerDown(string selector);
        void PointerUp(string selector);
    }

    /// <summary>
    /// Learn-and-drive v3 P4 — THE CONFIRMING REPLAY: the just-taught recipe played ONCE from its declared start, so the
    /// teach is confirmed by a second observation (<see cref="TeachAnalysis"/>). Pure over <see cref="IReplayScreen"/>, a
    /// clock and a cheat runner, so the tests pump it with a fake screen.
    ///
    /// Before EVERY goal step, the DISMISS LOOP: tap whichever <c>dismiss[]</c> name is showing, in any order, until two
    /// quiet passes (cap <see cref="MaxDismissPresses"/>) — spike B's 5/5 rule, not a fixed list. Every press — a dismiss,
    /// a step, a candidate — passes <see cref="PressGuard.Refusal"/> with the run's allowlist (the recipe's own names) first:
    /// this is a kit job, so the fence is the one a gated director press passes. A cheat step runs through the kit job's
    /// gate (<see cref="KitJobRun.Gate"/>: ticked, exact membership). Gestures and <c>invoke-button</c> are not pressed (the
    /// analysis blocks such a teach before any replay).
    ///
    /// It never retries a step: one replay answers "does it arrive the same way" (a retry would press the studio's game a
    /// second time to answer it — the kit-job rule of one attempt).
    /// </summary>
    public sealed class RecipeReplay
    {
        public const int MaxDismissPresses = 12;
        public const int QuietPassesNeeded = 2;
        public const double QuietPassSec = 0.5;
        public const double AfterDismissSec = 0.8;
        public const double AfterStepSec = 1.5;

        public bool Ran { get; private set; }
        public string? Why { get; private set; }
        public List<string> Dismissed { get; } = new();
        public JArray Steps { get; } = new();
        public List<string> Log { get; } = new();
        public bool Finished { get; private set; }

        private readonly JArray _steps;
        private readonly List<string> _dismiss;
        private readonly IReadOnlyCollection<string> _allowlist;

        /// <param name="steps">the recipe's steps (<see cref="TeachAnalysis.Result.Steps"/>)</param>
        /// <param name="dismiss">the recipe's dismiss names</param>
        public RecipeReplay(JArray steps, IEnumerable<string> dismiss)
        {
            _steps = steps;
            _dismiss = dismiss.ToList();
            _allowlist = AllowlistOf(steps, _dismiss);
        }

        /// <summary>The names this replay may press: the NAME part of every step's and every dismiss selector — nothing the
        /// recipe does not already hold.</summary>
        public static HashSet<string> AllowlistOf(JArray steps, IEnumerable<string> dismiss)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in steps.OfType<JObject>())
                if (s["name"]?.Type == JTokenType.String) set.Add(PressGuard.NameOf(s["name"]!.Value<string>()!));
            foreach (var d in dismiss) set.Add(PressGuard.NameOf(d));
            set.Remove("");
            return set;
        }

        public IReadOnlyCollection<string> Allowlist => _allowlist;

        /// <summary>A replay that never started (the game did not boot, the teach was blocked) — said, never "0 steps".</summary>
        public static JObject NotRun(string why) => new()
        {
            ["ran"] = false, ["why"] = why, ["dismissed"] = new JArray(), ["steps"] = new JArray(),
        };

        public JObject ToJson() => new()
        {
            ["ran"] = Ran,
            ["why"] = Why == null ? JValue.CreateNull() : Why,
            ["dismissed"] = new JArray(Dismissed),
            ["steps"] = new JArray(Steps),
        };

        /// <summary>How long the replay waits for the screen it starts on (a freshly booted game needs seconds to reach its
        /// lobby — the try's own rule, <c>ShotReadyWaitSec</c>).</summary>
        public const double ReadyWaitSec = 60;

        /// <summary>Is the screen the recipe starts on up: its first step's element (or an element matching two of its three
        /// parts), or any popup close the recipe knows. A recipe that starts with a cheat is ready at once.</summary>
        public bool StartIsUp(IReplayScreen screen)
        {
            if (_steps.Count == 0 || _steps[0] is not JObject first) return true;
            var kind = first["kind"]?.Value<string>() ?? "";
            if (kind != "click" && kind != "hold") return true;
            var sel = first["name"]?.Value<string>() ?? "";
            if (sel.Length > 0 && screen.Exists(sel)) return true;
            if (ElementIdentity.FromJson(first["element"]) is { } want
                && screen.Clickables().Any(e => want.PartsMatching(e) >= RecipeRebind.MinParts)) return true;
            return _dismiss.Any(screen.Exists);
        }

        /// <summary>THE REPLAY. Yield-driven over <paramref name="now"/> (seconds).</summary>
        public IEnumerable Run(IReplayScreen screen, Func<string, bool> runCheat, Func<double> now)
        {
            Ran = true;
            // wait for the screen the recipe starts on — pressing into a loading screen would read as "the element is gone"
            var readyBy = now() + ReadyWaitSec;
            while (!StartIsUp(screen) && now() < readyBy) yield return null;
            if (!StartIsUp(screen))
            {
                var first = (_steps[0] as JObject)?["name"]?.Value<string>() ?? "its first step";
                Steps.Add(new JObject
                {
                    ["pressed"] = false,
                    ["refused"] = $"the screen the recipe starts on never came up ('{first}' was not on screen after {ReadyWaitSec:0} s — the game may still be loading)",
                    ["rebound"] = JValue.CreateNull(),
                    ["after"] = JValue.CreateNull(),
                });
                Finished = true;
                yield break;
            }
            foreach (var token in _steps)
            {
                var step = token as JObject ?? new JObject();
                foreach (var _ in DismissLoop(screen, now)) yield return null;
                var before = screen.Observe();
                var result = new JObject { ["pressed"] = false, ["refused"] = JValue.CreateNull(), ["rebound"] = JValue.CreateNull(), ["after"] = JValue.CreateNull() };
                Steps.Add(result);
                var kind = step["kind"]?.Value<string>() ?? "";
                string? refused = null;
                if (kind == "cheat")
                {
                    var command = step["command"]?.Value<string>() ?? "";
                    if (!runCheat(command)) refused = $"the cheat '{command}' was refused or failed";
                }
                else if (kind == "click" || kind == "hold")
                {
                    var target = Target(screen, step, result, out refused);
                    if (target != null)
                    {
                        refused = PressGuard.Refusal(target, _allowlist);
                        if (refused == null)
                        {
                            if (kind == "click")
                            {
                                if (!screen.Click(target)) refused = $"'{target}' is on screen but the click did not land (not interactable, or covered)";
                            }
                            else
                            {
                                screen.PointerDown(target);
                                var until = now() + Math.Max(0, step["seconds"]?.Value<double>() ?? 0);
                                while (now() < until) yield return null;
                                screen.PointerUp(target);
                            }
                        }
                    }
                }
                else refused = $"a '{kind}' step is not replayed";
                if (refused != null)
                {
                    result["refused"] = refused;
                    Log.Add($"step {Steps.Count}: {refused}");
                    Finished = true;
                    yield break;
                }
                result["pressed"] = true;
                var wait = now() + AfterStepSec;
                while (now() < wait) yield return null;
                result["after"] = ScreenSignature.AfterJson(before, screen.Observe());
            }
            Finished = true;
        }

        /// <summary>The selector to press for a click/hold step: the recorded one when it is on screen, else a 2-of-3
        /// candidate (recorded in <c>rebound</c>) — or null with the reason.</summary>
        private static string? Target(IReplayScreen screen, JObject step, JObject result, out string? why)
        {
            why = null;
            var sel = step["name"]?.Value<string>() ?? "";
            if (sel.Length > 0 && screen.Exists(sel)) return sel;
            var want = ElementIdentity.FromJson(step["element"]);
            if (want == null)
            {
                why = $"'{sel}' is not on screen (and the step carries no identity to look for it by)";
                return null;
            }
            var found = RecipeRebind.Find(want, screen.Clickables());
            if (found.Candidate == null)
            {
                why = $"'{sel}' is not on screen, and {found.Why}";
                return null;
            }
            result["rebound"] = new JObject { ["element"] = found.Candidate.ToJson(), ["parts"] = found.Parts, ["candidate"] = true };
            return RecipeRebind.SelectorOf(found.Candidate);
        }

        private IEnumerable DismissLoop(IReplayScreen screen, Func<double> now)
        {
            if (_dismiss.Count == 0) yield break;
            var quiet = 0;
            var presses = 0;
            var refusedOnce = new HashSet<string>(StringComparer.Ordinal);
            while (quiet < QuietPassesNeeded && presses < MaxDismissPresses)
            {
                var showing = _dismiss.FirstOrDefault(d => !refusedOnce.Contains(d) && screen.Exists(d));
                double until;
                if (showing == null)
                {
                    quiet++;
                    until = now() + QuietPassSec;
                }
                else
                {
                    var refusal = PressGuard.Refusal(showing, _allowlist);
                    if (refusal != null || !screen.Click(showing))
                    {
                        refusedOnce.Add(showing);
                        Log.Add($"dismiss '{showing}' not pressed: {refusal ?? "the click did not land"}");
                        continue;
                    }
                    Dismissed.Add(showing);
                    presses++;
                    quiet = 0;
                    until = now() + AfterDismissSec;
                }
                while (now() < until) yield return null;
            }
        }
    }

    /// <summary>The kit's live screen for a replay: the kit's own uGUI finder (<see cref="UguiDriver"/>) and the panel-root
    /// observation. Presses go through the SAME driver the director's click step uses.</summary>
    public sealed class UguiReplayScreen : IReplayScreen
    {
        private readonly IUiDriver _ui;
        public UguiReplayScreen(IUiDriver? ui = null) => _ui = ui ?? new UguiDriver();

        public ScreenSignature.Observation Observe() => ScreenSignature.Observe();

        public IReadOnlyList<ElementIdentity> Clickables()
        {
            var list = new List<ElementIdentity>();
            foreach (var canvas in ScreenSignature.RootCanvases())
                foreach (var h in canvas.GetComponentsInChildren<MonoBehaviour>(false))
                    if (h is IPointerClickHandler && h.isActiveAndEnabled && list.Count < 500)
                        list.Add(ElementIdentity.Of(h.gameObject));
            return list;
        }

        public bool Exists(string selector) => _ui.Exists(selector);
        public bool Click(string selector) => _ui.Click(selector);
        public void PointerDown(string selector) => _ui.PointerDown(selector);
        public void PointerUp(string selector) => _ui.PointerUp(selector);
    }
}
