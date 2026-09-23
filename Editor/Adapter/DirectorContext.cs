using System;
using System.Collections;
using System.Collections.Generic;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// One upcoming shot as the ready gate sees it: its <see cref="Name"/> (for logging) and its
    /// <see cref="AdShot.Baseline"/> — where the shot must START. The gate reads Baseline to pick which
    /// baseline to establish, so a game no longer maintains a parallel name-list alongside its shots;
    /// the shot's own declaration is the single source of truth.
    /// </summary>
    public readonly struct UpcomingShot
    {
        public readonly string Name;
        public readonly string Baseline;
        public readonly bool? Resist;

        public UpcomingShot(string name, string baseline, bool? resist = null)
        {
            Name = name;
            Baseline = baseline;
            Resist = resist;
        }

        /// <summary>Name only, so a log that string.Joins a shot list still reads as a list of names.</summary>
        public override string ToString() => Name;
    }

    /// <summary>
    /// The iterator toolkit shared by the director and per-game policies: probes, clock, logging,
    /// and the wait/click primitives extracted from SNL's AdDirector. The clock is injected so
    /// every timeout is unit-testable with a fake. OnTick is a test-only hook (null in
    /// production) that tests use to advance their fake clock per yielded tick.
    /// </summary>
    public sealed class DirectorContext
    {
        public IUiDriver Ui { get; }
        public IStateProbe State { get; }
        public ICheatBridge Cheats { get; }
        public Func<double> Now { get; }
        public Action<string> Log { get; }

        /// <summary>Outcome of the last wait/click primitive (mirrors SNL's _lastWaitSucceeded).</summary>
        public bool LastOpSucceeded { get; set; }

        /// <summary>Test hook: tests set this to advance their fake clock per yielded tick.</summary>
        public Action? OnTick { get; set; }

        /// <summary>
        /// The shots this director run is about to execute — each with its name and its
        /// <see cref="AdShot.Baseline"/> — set by AdDirector before it calls the ready gate. Empty for a
        /// bare `ready` request (the gate is the whole job).
        ///
        /// WHY THE GATE NEEDS THIS AT ALL. `IReadyGate.WaitUntilReady` runs ONCE per director run,
        /// before any shot, and previously received no information about the work — so a gate could
        /// only ever implement ONE baseline. Rogue Legend's hardcodes "on the board with the roll
        /// button up", which is right for its board shots and actively wrong for its menu shots:
        /// those declare `setup: ensure-lobby` and must climb back OUT of a run the gate just started.
        /// Measured 2026-08-04: with the gate entering a fresh run, `modes_tour` failed both attempts
        /// on `arm condition All(Present 'Equip_Tab' AND Absent 'ClickableZone') never held in 20s`.
        ///
        /// WHY THE BASELINE FIELD AND NOT `expectState`. The obvious signal does not work: expectState
        /// is where a shot ENDS, and in a real game board shots like `king_run`/`boss_round` end at
        /// "Lobby" exactly like every menu shot, so it cannot separate them. What distinguishes them is
        /// where a shot must START — which no rule generically derives from the shot's steps. So each
        /// shot DECLARES it, as `AdShot.Baseline`, and the gate routes on that. What a given baseline
        /// MEANS (how to reach it) stays GAME knowledge in the game's own gate; the kit only carries the
        /// tag here. This retired the parallel name-list a gate used to keep in sync by hand.
        ///
        /// Additive and defaulted, so every existing adapter compiles and behaves identically —
        /// deliberately not a signature change to IReadyGate, which would break all three games'
        /// adapters for a value only one of them currently reads.
        /// </summary>
        public IReadOnlyList<UpcomingShot> UpcomingShots { get; set; } = Array.Empty<UpcomingShot>();

        public DirectorContext(IUiDriver ui, IStateProbe state, ICheatBridge cheats,
            Func<double> now, Action<string> log)
        {
            Ui = ui;
            State = state;
            Cheats = cheats;
            Now = now;
            Log = log;
        }

        /// <summary>
        /// Wait <paramref name="seconds"/>, one yield per editor tick — and ALWAYS AT LEAST ONE, whatever the number: zero,
        /// below zero, NaN (the twelfth audit, S1). A wait whose deadline had already passed used to yield nothing, so a loop
        /// that waits through it between tries (AdDirector's cheatUntil with retryEvery 0; InterruptSweep with a
        /// pollInterval of 0) ran its whole timeout inside one editor tick — 2,001 ms of frozen editor at timeout 2. A wait
        /// of real length yields exactly as it did: once per tick until the deadline.
        /// </summary>
        public IEnumerable WaitSeconds(double seconds)
        {
            var deadline = Now() + seconds;
            do
                yield return null;
            while (Now() < deadline);
        }

        /// <summary>
        /// Wait until <paramref name="predicate"/> holds or <paramref name="timeoutSec"/> passes. It YIELDS BEFORE EACH
        /// EVALUATION, THE FIRST INCLUDED, and evaluates once per yield; the last evaluation is the verdict (the thirteenth
        /// audit, S2). It used to evaluate before any yield and then again for its verdict: an already-met condition cost no
        /// editor tick at all, so a list of such steps ran whole inside one tick (1,091 of them, 981 ms at 3,000 UI nodes),
        /// and every exit paid for two evaluations. Now an already-met condition costs one tick and one evaluation.
        /// </summary>
        public IEnumerable WaitUntil(Func<bool> predicate, double timeoutSec)
        {
            var deadline = Now() + timeoutSec;
            bool met;
            do
            {
                yield return null;
                met = predicate();
            }
            while (!met && Now() < deadline);
            LastOpSucceeded = met;
        }

        /// <summary>
        /// Click by name; with an "until" condition, re-click every retryEvery seconds until it
        /// holds or timeout (handles UGUI clicks dropped before a screen's inputs subscribe).
        /// With no condition it is a single fire-once click.
        /// </summary>
        public IEnumerable ClickUntil(string name, WaitCondition? until, double timeout,
            double retryEvery, int index = 0)
        {
            var deadline = Now() + timeout;
            var nextClick = double.MinValue;
            var lastClickResult = false;
            while (true)
            {
                if (until.HasValue && until.Value.IsMet(Ui, State))
                    break;
                if (Now() >= nextClick)
                {
                    lastClickResult = Ui.Click(name, index);
                    nextClick = Now() + retryEvery;
                    if (!until.HasValue)
                        break;
                }
                if (Now() >= deadline)
                    break;
                yield return null;
            }
            LastOpSucceeded = until.HasValue ? until.Value.IsMet(Ui, State) : lastClickResult;
        }
    }
}
