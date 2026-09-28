using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Kit 0.14.0 (fix 2c, the RL test 2026-09-28) — THE GAME'S TAUGHT WAY "BACK TO THE LOBBY", as a claim carries it
    /// (<c>back</c>: one recipes document, its sha256, its id — the <see cref="StartClaim"/> shape, verified by the SAME
    /// reader a declared start is, <see cref="StartPlan.Read"/>). The kit keeps no recipe book of its own: a recipe reaches
    /// this machine only on a claim, so a game has a way back only when the box sends one.
    ///
    /// A recipe is "back to the lobby" when it was made FOR the story need <c>{ kind: 'screen', name: 'lobby' }</c>
    /// (<see cref="RecipeFile.Recipe.Need"/>). It must be ONE recipe (it is played from wherever the game stands, never
    /// from the anchor), CONFIRMED and not failed on its newest re-proof. Anything else is NOT USED (<see cref="Problem"/>)
    /// — audit M5: it never refuses the job; only a lobby shot that would need it fails, and says why.
    /// </summary>
    public sealed class BackToLobby
    {
        public const string NeedKind = "screen";
        public const string NeedName = "lobby";

        /// <summary>Why the way back that arrived cannot be used, or null.</summary>
        public string? Problem { get; private set; }
        public RecipeFile.Recipe? Recipe { get; private set; }
        /// <summary>sha256 of the text that ARRIVED — measured here.</summary>
        public string Sha256 { get; private set; } = "";

        /// <summary>Is this recipe the game's way back to the lobby (made for the need <c>screen: lobby</c>)?</summary>
        public static bool IsBackToLobby(RecipeFile.Recipe r) =>
            r.Need is { } n && n.Kind == NeedKind && string.Equals(n.Name.Trim(), NeedName, StringComparison.OrdinalIgnoreCase);

        public static BackToLobby Read(StartClaim claim)
        {
            var start = StartPlan.Read(claim, "");
            var b = new BackToLobby { Sha256 = start.Sha256 };
            if (start.Refusal != null)
            {
                var why = start.Refusal.Replace("declared start", "way back to the lobby");
                var cut = why.IndexOf(" — nothing was run", StringComparison.Ordinal);
                b.Problem = cut > 0 ? why.Substring(0, cut) : why;
                return b;
            }
            var chain = start.Chain!;
            if (chain.Count != 1)
            {
                b.Problem = $"the way back to the lobby that arrived is a chain of {chain.Count} recipes — it is played from wherever the game stands, so it must be one recipe";
                return b;
            }
            var r = chain[0];
            if (!IsBackToLobby(r))
            {
                b.Problem = $"the way back to the lobby that arrived (recipe {r.Id}) was not made for the lobby (its need is not screen: lobby)";
                return b;
            }
            if (!r.Confirmed || r.ProofState == "failed")
            {
                // fix 5b audit M3 (invariant 185): say which way it was confirmed — by names twice, or by picture
                b.Problem = r.ByPicture
                    ? $"the way back to the lobby that arrived (recipe {r.Id}) was confirmed by picture (the Stop and replay pictures showed the asked screen), but it failed on its newest Try — show it again once"
                    : $"the way back to the lobby that arrived (recipe {r.Id}) is {(r.ProofState == "failed" ? "failed on its newest re-proof" : "not confirmed")} — the kit plays only a way that arrived the same way twice";
                return b;
            }
            // fix 5b (invariant 185): a way back confirmed by picture has no names to say it arrived — it is played only
            // when it carries its taught wait (StartPath waits it); the box's `qualifiesAsWayBack` is the twin
            if (r.ByPicture && !r.HasArrivalLag)
            {
                b.Problem = $"the way back to the lobby that arrived (recipe {r.Id}) was confirmed by picture but carries no taught wait, so the kit could not tell when it arrived — show it again once";
                return b;
            }
            b.Recipe = r;
            return b;
        }
    }

    /// <summary>
    /// Kit 0.14.0 (audits M4, M5) — WHAT THE CLAIM SAID ABOUT A WAY BACK TO THE LOBBY, for both loops: whether it carried a
    /// <c>back</c> key at all (<see cref="Offered"/> — the box supports it, so "teach the way back" is a promise it can
    /// keep), the usable recipe, or why the one that arrived is not used (<see cref="Problem"/>: not whole, or
    /// <see cref="BackToLobby.Read"/>'s reason). Nothing here refuses a job.
    /// </summary>
    public sealed class BackOffer
    {
        public bool Offered { get; }
        public RecipeFile.Recipe? Recipe { get; }
        public string? Problem { get; }

        private BackOffer(bool offered, RecipeFile.Recipe? recipe, string? problem)
        {
            Offered = offered;
            Recipe = recipe;
            Problem = problem;
        }

        public static readonly BackOffer None = new(false, null, null);

        /// <param name="offered">the claim carried a <c>back</c> key (even JSON null)</param>
        /// <param name="claim">the block, when it arrived whole</param>
        /// <param name="broken">why it did not arrive whole, or null</param>
        public static BackOffer Of(bool offered, StartClaim? claim, string? broken)
        {
            if (!offered) return None;
            if (broken != null) return new BackOffer(true, null, broken);
            if (claim == null) return new BackOffer(true, null, null);
            var read = BackToLobby.Read(claim);
            return new BackOffer(true, read.Recipe, read.Problem);
        }

        /// <summary>The line the job's log says when a way back arrived but is not used.</summary>
        public string? LogLine => Problem == null ? null : "[nova] the way back to the lobby is not used: " + Problem;
    }

    /// <summary>
    /// Kit 0.14.0 (fixes 2b and 2c, the RL test 2026-09-28) — THE SHOT'S FIRST SCREEN, ONE STEP FOR THE TRY AND THE
    /// CAPTURE (invariant 101: both loops build it here — <see cref="ForTake"/> / <see cref="ForTry"/>, which differ ONLY
    /// in the wait's condition behind a tutorial gate — and pump the same <see cref="Run"/>).
    ///
    /// THE STEP. Wait for the shot's screen exactly as before (same condition, same seconds). Then:
    /// <list type="bullet">
    /// <item>it came up, or the shot's first screen cannot be told from here (<see cref="FirstPresses"/> null — see
    /// <see cref="FirstPressOf"/>), or the first press IS on screen → run the take as always;</item>
    /// <item>the first press is not on screen: first a GRACE for the shot's own leading <c>wait</c> seconds (audit M2 —
    /// the director would have waited them before its press);</item>
    /// <item>still not there, a lobby shot, and a usable way back (<see cref="BackOffer"/>) → play it once and wait again
    /// (<see cref="BackWaitSec"/>);</item>
    /// <item>otherwise → FAIL now, naming the missing press and what the screen showed.</item>
    /// </list>
    ///
    /// WHEN THE FIRST SCREEN CAN BE TOLD (a fail-fast must never refuse a take that would have worked): the adapter moves
    /// nothing before the steps (no recovery policy; no ready gate or the kit's hygiene gate), the shot has no
    /// <c>setup</c>, nothing before its first press but <c>wait</c>, <c>timeScale</c> or <c>vision</c> (a <c>waitFor</c>
    /// is a shot saying "the game will get there", audit M2), and that press is a plain named <c>click</c> — a click with
    /// an <c>until</c> that already holds succeeds without its button, and a <c>hold</c> on an absent target succeeds
    /// (audit M1), so neither is judged.
    /// </summary>
    public sealed class FirstScreen
    {
        /// <summary>A try's <c>failedStepKind</c> when the shot's first screen was not up.</summary>
        public const string FailedKind = "first-screen";
        /// <summary>How long the kit waits for the first press after playing the way back to the lobby.</summary>
        public const double BackWaitSec = 15;
        /// <summary>The most leading <c>wait</c> seconds granted as grace.</summary>
        public const double MaxGraceSec = 60;
        /// <summary>The most on-screen names the sentence lists.</summary>
        public const int MaxShownNames = 8;
        /// <summary>A lobby shot, the box offers a way back, and the game has none taught yet.</summary>
        public const string TeachTheWayBack = "Teach the way back to the lobby once on Setup › Show me once.";
        /// <summary>A lobby shot and the box offers no way back (audit M4: never promise what the box cannot do).</summary>
        public const string StartOnTheLobby = "Start the game on its lobby screen and try again.";

        public TakeStart Ready { get; }
        /// <summary>The presses any one of which means the take can start here (the gate's, then the shot's), or null when
        /// this shot's first screen cannot be told before its director runs.</summary>
        public IReadOnlyList<string>? FirstPresses { get; }
        /// <summary>The shot's own leading wait seconds (capped) — granted before the take is failed.</summary>
        public double GraceSec { get; }
        /// <summary>The shot starts in the lobby (<c>baseline: lobby</c>; an unset baseline is the board).</summary>
        public bool Lobby { get; }
        public BackOffer Back { get; }

        public bool ReadyMet { get; private set; }
        public bool BackPlayed { get; private set; }
        public bool Finished { get; private set; }
        /// <summary>Non-null = the take must not run: the sentence it fails with.</summary>
        public string? Failure { get; private set; }
        public string Phase { get; private set; } = "";
        public PressLog Log { get; } = new();
        public StartPathRun? BackRun { get; private set; }

        private FirstScreen(TakeStart ready, IReadOnlyList<string>? presses, double grace, bool lobby, BackOffer back)
        {
            Ready = ready;
            FirstPresses = presses;
            GraceSec = grace;
            Lobby = lobby;
            Back = back;
        }

        /// <summary>A capture's take: waits for the gate's screen OR the shot's (<see cref="CaptureRun.StartsOn"/>).</summary>
        public static FirstScreen ForTake(AdShot shot, AdShot? gate, GameAdapter adapter, BackOffer back) =>
            Build(CaptureRun.StartsOn(shot, gate), shot, gate, adapter, back);

        /// <summary>A try: behind a tutorial gate it waits for the GATE's screen, as it always did (audit M3 — a shot whose
        /// settle holds at boot before the tutorial shows must not start the try early); without one, the take's rule.</summary>
        public static FirstScreen ForTry(AdShot shot, AdShot? gate, GameAdapter adapter, BackOffer back) =>
            Build(gate == null ? CaptureRun.StartsOn(shot, null) : new TakeStart(gate.ArmCondition ?? gate.Settle), shot, gate, adapter, back);

        private static FirstScreen Build(TakeStart ready, AdShot shot, AdShot? gate, GameAdapter adapter, BackOffer back)
        {
            IReadOnlyList<string>? presses = null;
            double grace = 0;
            if (AdapterMovesNothing(adapter) && FirstPressOf(shot, out var mine) is { } m)
            {
                grace = mine;
                if (gate == null) presses = new[] { m };
                else if (FirstPressOf(gate, out var theirs) is { } g)
                {
                    presses = g == m ? new[] { m } : new[] { g, m };
                    grace = Math.Max(grace, theirs);
                }
            }
            return new FirstScreen(ready, presses, grace, shot.Baseline == AdShot.BaselineLobby, back);
        }

        internal static bool AdapterMovesNothing(GameAdapter adapter) =>
            adapter.Recovery is NullRecoveryPolicy && (adapter.ReadyGate is NullReadyGate || adapter.ReadyGate is HygieneReadyGate);

        /// <summary>The shot's first press when it can be judged, else null (see the class summary).</summary>
        public static string? FirstPressOf(AdShot shot) => FirstPressOf(shot, out _);

        /// <summary>…and the shot's leading <c>wait</c> seconds before it (capped at <see cref="MaxGraceSec"/>).</summary>
        public static string? FirstPressOf(AdShot shot, out double graceSec)
        {
            graceSec = 0;
            if (shot.Setup.Count > 0) return null;
            foreach (var step in shot.Steps)
            {
                switch (step.Kind)
                {
                    case AdStepKind.Wait:
                        graceSec = Math.Min(MaxGraceSec, graceSec + Math.Max(0, step.Number));
                        continue;
                    case AdStepKind.TimeScale:
                    case AdStepKind.Vision:
                        continue;
                    case AdStepKind.Click:
                        return step.Until != null || string.IsNullOrWhiteSpace(step.Text) || step.Text.Contains('{') ? null : step.Text;
                    default:
                        // waitFor (the shot says the game will get there), hold (succeeds on an absent target), cheats
                        return null;
                }
            }
            return null;
        }

        public IEnumerable Run(IUiProbe ui, IStateProbe? state, IReplayScreen screen, Func<IReadOnlyList<string>> buttons,
            Func<string, bool> runCheat, Func<double> now, double waitSec)
        {
            Phase = $"waiting for {Ready.Describe()}";
            var by = now() + waitSec;
            while (!Ready.IsMet(ui, state) && now() < by) yield return null;
            if (Ready.IsMet(ui, state))
            {
                ReadyMet = true;
                Finished = true;
                yield break;
            }
            if (FirstPresses == null)
            {
                Finished = true;
                yield break;
            }
            bool Pressable() => FirstPresses!.Any(ui.Exists);
            if (!Pressable() && GraceSec > 0)
            {
                Phase = $"waiting {GraceSec:0.#} s more for {Missing} (the shot's own waits)";
                var grace = now() + GraceSec;
                while (!Pressable() && now() < grace) yield return null;
            }
            if (Pressable())
            {
                Finished = true;
                yield break;
            }
            var shown = Shown(buttons);
            if (!Lobby)
            {
                Fail($"{Opened(shown)} — the shot's first press is not on screen, so it cannot start here.");
                yield break;
            }
            var recipe = Back.Recipe;
            if (recipe == null)
            {
                Fail($"{Opened(shown)} — this shot starts in the lobby. " + (Back.Problem != null
                    ? $"The way back to the lobby could not be used ({Back.Problem}). {StartOnTheLobby}"
                    : Back.Offered ? TeachTheWayBack : StartOnTheLobby));
                yield break;
            }
            if (!StartPathRun.StartIsUp(recipe, screen))
            {
                Fail($"{Opened(shown)} — this shot starts in the lobby. The way back to the lobby (recipe {recipe.Id}) was not played: its first step is not on this screen either — teach it again from this screen on Setup › Show me once.");
                yield break;
            }
            BackPlayed = true;
            Phase = $"playing the way back to the lobby (recipe {recipe.Id}; only its own names are pressed)";
            Log.Lines.Add($"first screen: {Missing} not on screen — playing the way back to the lobby (recipe {recipe.Id})");
            BackRun = new StartPathRun(new[] { recipe }, Log);
            foreach (var _ in BackRun.Run(screen, runCheat, now)) yield return null;
            Phase = $"waiting for {Missing} after the way back to the lobby";
            var again = now() + BackWaitSec;
            bool Up() => Ready.IsMet(ui, state) || Pressable();
            while (!Up() && now() < again) yield return null;
            if (Up())
            {
                Log.Lines.Add($"first screen: {Missing} is on screen after the way back to the lobby");
                Finished = true;
                yield break;
            }
            var how = BackRun.Arrived ? "was played" : $"was played and stopped ({BackRun.Why})";
            Fail($"{Opened(Shown(buttons))} — this shot starts in the lobby. The way back to the lobby (recipe {recipe.Id}) {how}, and {Missing} still was not on screen after {BackWaitSec:0} s — teach it again on Setup › Show me once.");
        }

        private string Missing => string.Join(" or ", FirstPresses ?? Array.Empty<string>());

        private string Opened(IReadOnlyList<string> shown) =>
            $"The game opened on a screen without {Missing} (it showed: {(shown.Count == 0 ? "no clickable names" : string.Join(", ", shown))})";

        private static IReadOnlyList<string> Shown(Func<IReadOnlyList<string>> buttons)
        {
            try { return buttons().Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().Take(MaxShownNames).ToList(); }
            catch (Exception) { return Array.Empty<string>(); }
        }

        private void Fail(string sentence)
        {
            Failure = sentence;
            Log.Lines.Add("first screen: " + sentence);
            Finished = true;
        }
    }
}
