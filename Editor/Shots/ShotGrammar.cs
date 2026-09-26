using System;
using System.Collections.Generic;
using System.Linq;

namespace ProjectNova.RecorderKit
{
    public enum WaitKind { Present, Absent, TextContains, State, All, Interactable }

    public readonly struct WaitCondition
    {
        public readonly WaitKind Kind;
        public readonly string ElementName;
        public readonly string Substring;

        /// <summary>Sub-conditions for WaitKind.All; null for every other kind.</summary>
        public readonly WaitCondition[]? Parts;

        private WaitCondition(WaitKind kind, string elementName, string substring,
            WaitCondition[]? parts = null)
        {
            Kind = kind;
            ElementName = elementName;
            Substring = substring;
            Parts = parts;
        }

        public static WaitCondition Present(string name) => new(WaitKind.Present, name, "");
        public static WaitCondition Absent(string name) => new(WaitKind.Absent, name, "");

        /// <summary>
        /// Present and actually actionable — the best available "the game is ready for input" signal,
        /// and usually the right settle for a shot that ends on a playable screen.
        ///
        /// Prefer this over Present for a primary button: buttons stay in the hierarchy while disabled,
        /// so Present is true throughout the period the player cannot act. Prefer it over a state
        /// condition when the game's state machine is unreliable — one board game never returned its
        /// state to Idling after a battle, while the roll button correctly went from locked to unlocked.
        /// </summary>
        public static WaitCondition Interactable(string name) => new(WaitKind.Interactable, name, "");
        public static WaitCondition TextContains(string name, string substring) =>
            new(WaitKind.TextContains, name, substring);

        /// <summary>
        /// Wait for the game's own state machine to report this state — the authoritative "is the
        /// action over" signal whenever the game has one, and strictly better than a UI element's
        /// presence, which persists across state changes. Prefer this for any settle on a game
        /// that exposes a state name through IStateProbe.
        /// </summary>
        public static WaitCondition State(string stateName) => new(WaitKind.State, stateName, "");

        /// <summary>
        /// Every sub-condition must hold. The capture check a real shot usually wants is a conjunction —
        /// "my anchor is present AND no celebration overlay is covering it" — because presence alone
        /// says nothing about visibility: a full-screen overlay leaves everything under it present and
        /// reporting normally. Without this, a shot had to spend its single settle on one half and hope,
        /// which is how a take was accepted with a "NEW FEATURE!" banner across its subject.
        /// </summary>
        public static WaitCondition All(params WaitCondition[] parts) =>
            new(WaitKind.All, "", "", parts ?? System.Array.Empty<WaitCondition>());

        /// <summary>
        /// Evaluate. <paramref name="state"/> is required only by State conditions; passing null
        /// leaves such a condition permanently unmet rather than silently true.
        /// </summary>
        public bool IsMet(IUiProbe probe, IStateProbe? state = null) => Kind switch
        {
            WaitKind.Present => probe.Exists(ElementName),
            WaitKind.Absent => !probe.Exists(ElementName),
            WaitKind.Interactable => probe.IsInteractable(ElementName),
            WaitKind.TextContains => probe.GetText(ElementName) is { } t &&
                                     t.Contains(Substring, StringComparison.OrdinalIgnoreCase),
            WaitKind.State => state?.CurrentStateName == ElementName,
            // An empty All is vacuously true, matching "no constraints were asked for".
            WaitKind.All => Parts != null && System.Linq.Enumerable.All(Parts, p => p.IsMet(probe, state)),
            _ => false,
        };

        /// <summary>Human-readable form, so a timeout can say WHICH condition never resolved.</summary>
        public string Describe() => Kind switch
        {
            WaitKind.Present => $"Present '{ElementName}'",
            WaitKind.Absent => $"Absent '{ElementName}'",
            WaitKind.Interactable => $"Interactable '{ElementName}'",
            WaitKind.TextContains => $"{ElementName} text contains '{Substring}'",
            WaitKind.State => $"State '{ElementName}'",
            WaitKind.All => Parts == null || Parts.Length == 0
                ? "All(<nothing>)"
                : "All(" + string.Join(" AND ", System.Linq.Enumerable.Select(Parts, p => p.Describe())) + ")",
            _ => Kind.ToString(),
        };
    }

    public enum AdStepKind { Click, Hold, Wait, WaitFor, Cheat, TimeScale, Vision, CheatUntil }

    /// <summary>
    /// THE SHOTS.JSON SPELLING OF A STEP KIND — <c>waitFor</c>, not <c>WaitFor</c>.
    ///
    /// The enum's name is C#'s; this is the grammar's — the word a person typed in the file, and the
    /// word the website's lint knows (<c>JsonShotLoader.TryParseStep</c> reads exactly these). It
    /// lives beside the enum so that anything reporting a step kind OUT of this kit — the
    /// <c>probe</c> job's facts today — cannot invent a second spelling.
    /// </summary>
    public static class ShotKind
    {
        public static string Name(AdStepKind kind) => kind switch
        {
            AdStepKind.Click => "click",
            AdStepKind.Hold => "hold",
            AdStepKind.Wait => "wait",
            AdStepKind.WaitFor => "waitFor",
            AdStepKind.Cheat => "cheat",
            AdStepKind.CheatUntil => "cheatUntil",
            AdStepKind.TimeScale => "timeScale",
            AdStepKind.Vision => "vision",
            // Unreachable while the switch covers the enum, and the EditMode suite walks every value
            // to keep it so. A kind added later still reports SOMETHING rather than throwing.
            _ => kind.ToString(),
        };
    }

    /// <summary>One director action. Built via the static factories. Vision is an async check
    /// answered by a reasoning agent over the CommandRelay — never a per-tick WaitCondition.</summary>
    public readonly struct AdStep
    {
        public readonly AdStepKind Kind;
        public readonly string Text;          // element name (Click/Hold), cheat command (Cheat), or vision prompt (Vision)
        public readonly int Index;
        public readonly double Number;        // Hold seconds / Wait seconds / TimeScale factor
        public readonly WaitCondition? Until;  // Click "click-until" target / WaitFor condition
        public readonly double TimeoutSec;
        public readonly double RetryEverySec;

        private AdStep(AdStepKind kind, string text, int index, double number,
            WaitCondition? until, double timeoutSec, double retryEverySec)
        {
            Kind = kind;
            Text = text;
            Index = index;
            Number = number;
            Until = until;
            TimeoutSec = timeoutSec;
            RetryEverySec = retryEverySec;
        }

        public static AdStep Click(string name, WaitCondition? until = null, int index = 0,
            double timeout = 6, double retryEvery = 0.6) =>
            new(AdStepKind.Click, name, index, 0, until, timeout, retryEvery);

        public static AdStep Hold(string name, double seconds, int index = 0) =>
            new(AdStepKind.Hold, name, index, seconds, null, 0, 0);

        public static AdStep Wait(double seconds) =>
            new(AdStepKind.Wait, "", 0, seconds, null, 0, 0);

        public static AdStep WaitFor(WaitCondition condition, double timeout = 8) =>
            new(AdStepKind.WaitFor, "", 0, 0, condition, timeout, 0);

        public static AdStep Cheat(string command) =>
            new(AdStepKind.Cheat, command, 0, 0, null, 0, 0);

        /// <summary>
        /// Re-run a cheat until a condition holds (or timeout) — the cheat equivalent of a click-until.
        ///
        /// Exists because a one-shot cleanup step has to guess WHEN to fire, and guessing is wrong in
        /// both directions: fire too early and the interrupt has not appeared yet, so the cheat no-ops
        /// and nothing ever clears it; fire too late and the take has already ended. A real case: a
        /// battle's rewards raise a level-up skill pick at an unpredictable moment, sometimes not at all,
        /// and clearing it at a fixed 6s missed it entirely and hung the shot.
        ///
        /// Use it for anything shaped "keep resolving whatever is in the way until the game is ready":
        /// draining a queue of celebrations, resolving nested sequences, dismissing stacked popups. The
        /// cheat should be IDEMPOTENT, since it may run several times or zero times.
        /// </summary>
        public static AdStep CheatUntil(string command, WaitCondition until, double timeout = 30,
            double retryEvery = 1.5) =>
            new(AdStepKind.CheatUntil, command, 0, 0, until, timeout, retryEvery);

        public static AdStep TimeScale(double factor) =>
            new(AdStepKind.TimeScale, "", 0, factor, null, 0, 0);

        public static AdStep Vision(string prompt, double timeout = 60) =>
            new(AdStepKind.Vision, prompt, 0, 0, null, timeout, 0);
    }

    public sealed class AdShot
    {
        /// <summary>The two baselines a shot can declare it starts from. These are the vocabulary the
        /// portfolio's games actually use — a menu/home surface ("lobby") versus the primary play
        /// surface ("board") — and they are named here, in the kit, so the JSON loader and every game's
        /// ready gate validate and compare against ONE source of truth rather than scattered string
        /// literals. A third baseline, if a future game needs one, is a one-line addition here.</summary>
        public const string BaselineLobby = "lobby";
        public const string BaselineBoard = "board";

        public string Name { get; }
        public IReadOnlyList<string> Setup { get; }
        public IReadOnlyList<AdStep> Steps { get; }
        public WaitCondition Settle { get; }
        public string? ExpectState { get; }

        /// <summary>
        /// Where this shot must START — the baseline the ready gate has to establish before the shot's
        /// own setup runs. Never null: an unset baseline normalises to <see cref="BaselineBoard"/>, the
        /// kit's long-standing meaning of a bare `ready` (the primary play surface).
        ///
        /// This is a per-shot DECLARATION, not something the kit derives, because it cannot be derived:
        /// <see cref="ExpectState"/> is where a shot ENDS, and in a real game menu shots and board shots
        /// that return home both end at the same state — what separates them is where they must begin.
        /// It replaces the hand-maintained name-list a game's gate used to keep in sync with its shots:
        /// the shot itself now says where it starts, so no parallel list can silently fall out of date.
        /// The kit only carries the value (surfaced to the gate via <see cref="DirectorContext.UpcomingShots"/>);
        /// what a baseline MEANS — how to reach it — is the game's ready gate's business.
        /// </summary>
        public string Baseline { get; }

        /// <summary>
        /// Stay-alive resist for the ready gate. Omitted / <c>true</c> on a board shot applies
        /// <c>ready.resistOn</c>. Death takes must set <c>false</c> (<c>ready.resistOff</c>);
        /// omitting it on a kill shot makes ApplyDamage a no-op. Lobby shots ignore this.
        /// </summary>
        public bool? Resist { get; }

        /// <summary>
        /// How long the director waits for this shot to settle into its captured state. The
        /// default suits a screen that is already resolving; raise it for a beat whose own
        /// animation runs long (an auto-resolving battle, a multi-tile move).
        /// </summary>
        public double SettleTimeoutSec { get; }

        /// <summary>
        /// Optional: hold the recorder until this holds, AFTER setup has run. Nothing between setup
        /// and this condition reaches the file.
        ///
        /// Exists because setup gets a flat 0.4s before recording starts, which is fine for a cheat
        /// that only writes state and wrong for any setup that NAVIGATES. A setup like
        /// `call GameManager.ReturnToMainMenu` begins a scene transition, so the first second of the
        /// clip was a black "Loading…" frame plus whatever popup the destination screen raises on
        /// load — and because the shot's own settle only inspects the FINAL frame, the take was
        /// scored OK and the junk was found later, by eye, in the edit.
        ///
        /// The rule this encodes: a shot's recording window should open on the screen the shot is
        /// ABOUT. Any shot whose setup changes screens wants an arm condition naming an element of
        /// the destination — and, where the destination greets you with a claim/reward overlay, an
        /// Absent() on that overlay too.
        /// </summary>
        public WaitCondition? ArmCondition { get; }

        /// <summary>How long to wait for <see cref="ArmCondition"/> before recording anyway. Recording
        /// anyway (rather than failing the shot) is deliberate: a missed arm yields a clip with a
        /// dirty head, which is recoverable in the edit, whereas a hard failure yields nothing.</summary>
        public double ArmTimeoutSec { get; }

        /// <summary>
        /// Parameter names this shot accepts. Empty (the default) means the shot takes none, and
        /// substitution is skipped entirely — so every existing shot behaves byte-identically.
        ///
        /// Declared here so `admiral validate` can reject a script naming a parameter this shot
        /// does not accept with no Unity session open.
        /// </summary>
        public IReadOnlyList<string> Parameters { get; }

        /// <summary>True when any step needs a reasoning agent in the loop; fully-autonomous
        /// runs can skip such shots (Options.SkipVisionShots).</summary>
        public bool RequiresVision => Steps.Any(s => s.Kind == AdStepKind.Vision);

        public AdShot(string name, string[] setup, AdStep[] steps, WaitCondition settle,
            string? expectState = null, double settleTimeout = 8,
            WaitCondition? arm = null, double armTimeout = 15,
            string[]? parameters = null, string? baseline = null, bool? resist = null)
        {
            Name = name;
            Setup = setup;
            Steps = steps;
            Settle = settle;
            ExpectState = expectState;
            SettleTimeoutSec = settleTimeout;
            ArmCondition = arm;
            ArmTimeoutSec = armTimeout;
            Parameters = parameters ?? System.Array.Empty<string>();
            // Normalise here so nothing downstream — the gate, the context, RL's routing — ever has to
            // treat null as a third case: an unset baseline simply IS the board. The `!` is the null-
            // forgiving idiom used across this kit (JsonShotLoader's `name!`): IsNullOrEmpty already
            // rules out null in this branch, but the nullable analyser (warnings-as-errors here) does
            // not track that through the ternary, so it must be told.
            Baseline = string.IsNullOrEmpty(baseline) ? BaselineBoard : baseline!;
            Resist = resist;
        }

        /// <summary>
        /// True when the recording captured the intended state: the game is in ExpectState (if
        /// the shot declares one) AND the settle condition holds. A pure predicate over the
        /// probes — unit-testable with fakes.
        /// </summary>
        public bool IsCaptured(IStateProbe state, IUiProbe ui) =>
            (ExpectState == null || state.CurrentStateName == ExpectState) && Settle.IsMet(ui, state);
    }
}
