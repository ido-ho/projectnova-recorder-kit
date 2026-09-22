using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Slice E.4, part 2 — WHAT A RECORDING'S CLAIM SAYS ABOUT THE TUTORIAL GATE: the top-level
    /// <c>tutorialGate</c> of a <c>capture</c> claim (the mirror of <c>StudioCaptureClaim</c>,
    /// <c>apps/api/src/studio/studio-capture-job.ts</c>). The website decides it — once per job, by the rule
    /// a try's gate is decided by — and every take of the job runs it first, recording off. Nothing on this
    /// machine decides whether it runs.
    ///
    /// Absent or JSON null is NO GATE (a claim from before part 2 says nothing, and records as it always
    /// did). A whole gate is <see cref="Gate"/>. ANYTHING ELSE is a gate that did not arrive whole —
    /// <see cref="Gate"/> null — and the job is refused by <see cref="MissingReason"/>: a recording must
    /// not run its takes without the gate that was sent, behind whatever the gate gets past.
    /// </summary>
    public sealed class CaptureGateClaim
    {
        /// <summary>The gate, or null when it arrived but not whole.</summary>
        public ProbeGate? Gate { get; }

        private CaptureGateClaim(ProbeGate? gate) { Gate = gate; }

        /// <summary>The refusal of a recording whose gate did not arrive whole.</summary>
        public const string MissingReason =
            "this recording's tutorial gate arrived incomplete (no complete `tutorialGate` on the claim) — " +
            "nothing was run; " + CaptureGatePlan.RecordAgain;

        /// <summary>Null = no gate (absent, or JSON null). Otherwise the gate — or, when the token is not a
        /// whole gate (<see cref="ProbeGate.FromJson"/>), a claim whose <see cref="Gate"/> is null.</summary>
        public static CaptureGateClaim? FromJson(JToken? t) =>
            t == null || t.Type == JTokenType.Null ? null : new CaptureGateClaim(ProbeGate.FromJson(t));

        /// <summary>As persisted on the job's progress file — the gate, or <c>{}</c> for one that did not
        /// arrive whole — and read back by <see cref="FromJson"/>, the reader the claim goes through, so a
        /// resumed job reads what the claim said.</summary>
        public JToken ToJson() => Gate == null ? new JObject() : Gate.ToJson();
    }

    /// <summary>
    /// Slice E.4, part 2 — EVERYTHING A RECORDING DECIDES ABOUT ITS TUTORIAL GATE BEFORE IT TOUCHES THE GAME,
    /// as pure functions (no Unity API — the kit's EditMode tests cover the comparisons that run in
    /// production, invariant 101). The gate is read by the ONE reader a try's gate is read by
    /// (<see cref="ProbePlan.ReadGate"/>: its sha, one JSON object, the shot named <c>tutorial-gate</c>) and
    /// loaded by the real loader. The SHOT is the studio's own, from <c>Library/Nova/shots.json</c>, as every
    /// recording's has been.
    /// </summary>
    public sealed class CaptureGatePlan
    {
        /// <summary>What a recording's refusal tells the studio to do next.</summary>
        public const string RecordAgain = "start the recording again";

        /// <summary>Non-null = no take of this job may run: the gate did not arrive whole, is not the text its
        /// sha says, is not the shot named <c>tutorial-gate</c>, does not load, or holds a screen check. A
        /// whole-job refusal.</summary>
        public string? Refusal { get; private set; }

        /// <summary>The gate, read by the real loader, or null when it did not load.</summary>
        public AdShot? Gate { get; private set; }

        /// <summary>sha256 of the gate text that ARRIVED — measured here, never copied from the claim.</summary>
        public string? GateSha256 { get; private set; }

        /// <summary>The levers the GATE's own commands need — its <c>setup</c>, its <c>cheat</c> /
        /// <c>cheatUntil</c> steps, <c>timeScale</c> (<see cref="Levers.NeededFromShotsJson"/> over the
        /// one-shot document it was loaded from), sorted ordinal.</summary>
        public IReadOnlyList<string> LeversNeeded { get; private set; } = Array.Empty<string>();

        /// <summary>
        /// Read the gate the claim carried. Null when it carried none — the recording is today's, and nothing
        /// here is consulted.
        /// </summary>
        public static CaptureGatePlan? Prepare(CaptureGateClaim? claim)
        {
            if (claim == null) return null;
            var plan = new CaptureGatePlan();
            if (claim.Gate == null)
            {
                plan.Refusal = CaptureGateClaim.MissingReason;
                return plan;
            }
            plan.Refusal = ProbePlan.ReadGate(claim.Gate, RecordAgain, out var sha, out var gateObject);
            plan.GateSha256 = sha;
            if (plan.Refusal != null) return plan;
            var document = ProbePlan.DocumentFor(gateObject!);
            // remember: false — this is not the studio's shots.json (JsonShotLoader.LoadFrom)
            var loaded = JsonShotLoader.LoadFrom(document, remember: false);
            if (loaded.Errors.Count > 0)
            {
                plan.Refusal = "this recording's tutorial gate does not load: " + string.Join(" · ", loaded.Errors);
                return plan;
            }
            // First audit of E.4, S1 — a gate cannot hold a screen check (the director never asks a `vision`
            // step in the preamble, so it would stop every take there): refused whole, by the sentence a try's
            // gate is refused with (ProbePlan.GateHasScreenCheck). The site never carries such a gate on a
            // recording's claim; this is the kit's own half of the same rule.
            if (loaded.Shots[0].RequiresVision)
            {
                plan.Refusal = ProbePlan.GateHasScreenCheck;
                return plan;
            }
            plan.Gate = loaded.Shots[0];
            plan.LeversNeeded = Levers.NeededFromShotsJson(document);
            return plan;
        }

        /// <summary>
        /// Second audit of E.4, M1 — THE LEVERS ONE GATED TAKE NEEDS: the tries' union, the GATE's
        /// (<see cref="LeversNeeded"/>) ∪ the SHOT's ∪ the ADAPTER's, sorted ordinal, distinct. One director has
        /// ONE lever gate and a gated take's is forced on (<see cref="CaptureRun.OptionsFor"/>), so every command
        /// of the take meets it — the shot's <c>setup</c> included, which the lever gate would SKIP un-ticked while
        /// the take still captured. The shot's and the adapter's halves are the lever rule's own function
        /// (<see cref="Levers.NeededFrom"/>, a try's), over the one-shot document of what the LOADED shot runs
        /// (<see cref="ShotDocument"/> — a shot compiled into the game has no shots.json to read) and the
        /// <c>adapter.json</c> text on this disk (the one the director reads), null when there is none.
        /// </summary>
        public IReadOnlyList<string> TakeLeversNeeded(AdShot shot, string? adapterText)
        {
            var found = new List<string>(LeversNeeded);
            foreach (var lever in Levers.NeededFrom(ShotDocument(shot), adapterText))
                if (!found.Contains(lever))
                    found.Add(lever);
            found.Sort(StringComparer.Ordinal);
            return found;
        }

        /// <summary>
        /// The first lever a GATED TAKE needs that is not ticked on this machine NOW (<see cref="Levers.FirstUnticked"/>
        /// over <c>levers.json</c> as <see cref="Levers.Approved"/> reads it, fail closed), or null — the GATE's own
        /// first (its refusal is the gate's sentence), then the rest of the take's union
        /// (<see cref="TakeLeversNeeded"/>: the shot's, the adapter's). Asked before EVERY take, before a director
        /// exists: un-ticked, the take is refused before anything runs — not the ready gate, not the recovery, not
        /// the gate's own <c>setup</c>. The gate is cloud content, so these levers are demanded whatever the files
        /// on disk say (a try's rule).
        /// </summary>
        public string? LeverRefused(string projectRoot, AdShot shot)
        {
            var approved = Levers.Approved(projectRoot);
            return Levers.FirstUnticked(LeversNeeded, approved)
                   ?? Levers.FirstUnticked(TakeLeversNeeded(shot, AdapterTextOn(projectRoot)), approved);
        }

        /// <summary>The ONE-SHOT DOCUMENT of what a loaded shot RUNS, for the lever rule's own reader
        /// (<see cref="Levers.NeededFromShotsJson"/>): its name, its <c>setup</c>, and each step as its kind's
        /// shots.json word (<see cref="ShotKind.Name"/>) with the step's text as <c>command</c> — the rule decides
        /// which kinds are levers (a <c>cheat</c> / <c>cheatUntil</c>'s command, one <c>timeScale</c> lever), and
        /// ignores the text of every other kind.</summary>
        internal static string ShotDocument(AdShot shot) =>
            ProbePlan.DocumentFor(new JObject
            {
                ["name"] = shot.Name,
                ["setup"] = new JArray(shot.Setup.Cast<object>().ToArray()),
                ["steps"] = new JArray(shot.Steps.Select(step => (object)new JObject
                {
                    ["kind"] = ShotKind.Name(step.Kind),
                    ["command"] = step.Text,
                }).ToArray()),
            });

        /// <summary>This project's adapter.json text, or null when there is none or it cannot be read.</summary>
        private static string? AdapterTextOn(string projectRoot)
        {
            var path = SyncNova.AdapterFile(projectRoot);
            try { return System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : null; }
            catch (Exception) { return null; }
        }
    }

    /// <summary>
    /// Second audit of E.4, M2 — WHAT A TAKE MAY START ON: the screen the agent waits for before it builds the
    /// take's director. NO GATE = TODAY: the shot's own <c>arm</c>, else its settle — one condition, said as it
    /// always was. With a gate: the GATE's (its <c>arm</c>, else its settle) OR the SHOT's — a take behind the
    /// tutorial starts on the tutorial's screen, and a take after the tutorial has gone (the game remembers it)
    /// starts on the shot's own, so it does not wait out the whole wait for a screen that never comes back.
    /// </summary>
    public sealed class TakeStart
    {
        private readonly WaitCondition[] _screens;

        internal TakeStart(params WaitCondition[] screens) { _screens = screens; }

        /// <summary>The conditions, the gate's first — any one of them is enough.</summary>
        public IReadOnlyList<WaitCondition> Screens => _screens;

        /// <summary>True when ANY of <see cref="Screens"/> holds now.</summary>
        public bool IsMet(IUiProbe ui, IStateProbe? state) => _screens.Any(c => c.IsMet(ui, state));

        /// <summary>Said for the status line: one condition as it describes itself; two joined by " or ".</summary>
        public string Describe() => string.Join(" or ", _screens.Select(c => c.Describe()));
    }

    /// <summary>
    /// Slice E.4, part 2 — HOW A RECORDING'S TAKE IS HANDED TO THE DIRECTOR, and how it failed, as the functions
    /// the capture agent calls — so the lines that decide them are tested here rather than trusted inside a
    /// Play-Mode coroutine.
    /// </summary>
    public static class CaptureRun
    {
        /// <summary>
        /// The director's settings for a take. NO GATE = TODAY: <c>null</c>, so the take is
        /// <c>AdDirector.Run(adapter, new[] { shot }, null)</c> — the call a recording made before part 2, byte
        /// for byte. With a gate: it is the run's PREAMBLE (<see cref="AdDirector.Options.Preamble"/> — after
        /// the ready gate, recording off, the camera hold released after it), under the lever gate a try's
        /// gate runs under: <see cref="AdDirector.Options.CloudContent"/> forced on (the gate is cloud-delivered
        /// text) with the camera-pose Type guard (<see cref="ProbeCameraTypeGuard"/>). One director has ONE
        /// lever gate, so the shot after it runs under it too — which is why every lever of the take (the gate's,
        /// the shot's, the adapter's) is asked BEFORE the take (<see cref="RefusedBeforeTake"/>): un-ticked, the
        /// take is refused, rather than run with that command skipped.
        /// Every other setting is the default a recording has always had (two attempts, the file vision
        /// channel, the recorder its adapter names).
        ///
        /// <paramref name="tuning"/> is the tests' (a hand-pumped clock, a fake recorder); the agent passes
        /// none. Without a gate it is handed back UNTOUCHED — so the tests' no-gate run is today's call with
        /// their tuning, and the agent's is today's call.
        /// </summary>
        public static AdDirector.Options? OptionsFor(string projectRoot, AdShot? gate,
            AdDirector.Options? tuning = null)
        {
            if (gate == null) return tuning;
            var options = tuning ?? new AdDirector.Options();
            options.Preamble = gate;
            options.CloudContent = true;
            options.ProjectRoot = projectRoot;
            options.WrapCheats = (gated, log) =>
                new ProbeCameraTypeGuard(gated, projectRoot, log, options.CloudContent);
            return options;
        }

        /// <summary>
        /// Build the director for ONE take — or build NOTHING. With a gate that cannot run (refused, not
        /// loaded) or a lever the take needs that is not ticked here now (the gate's, the shot's or the
        /// adapter's — <see cref="CaptureGatePlan.LeverRefused"/>), null before a director exists, so not
        /// one command — not the ready gate's, not the gate's <c>setup</c> — reaches the game. The agent
        /// asks <see cref="RefusedBeforeTake"/> first and fails the take by name; this is the same
        /// rule at the last door (<see cref="ProbeRun.Start"/>'s). Without a gate:
        /// <c>AdDirector.Run(adapter, new[] { shot }, OptionsFor(projectRoot, null, tuning))</c> — today's
        /// call. Null also when another director is active (<see cref="AdDirector.Run"/>).
        /// </summary>
        public static AdDirector? Start(GameAdapter adapter, AdShot shot, CaptureGatePlan? gate,
            string projectRoot, AdDirector.Options? tuning = null)
        {
            if (gate != null && (gate.Refusal != null || gate.Gate == null || gate.LeverRefused(projectRoot, shot) != null))
                return null;
            return AdDirector.Run(adapter, new[] { shot }, OptionsFor(projectRoot, gate?.Gate, tuning));
        }

        /// <summary>
        /// Second audit of E.4, M1 — WHY A GATED TAKE IS REFUSED BEFORE ANYTHING RUNS, in words, or null (and null
        /// always without a gate: a take without one is today's, and nothing here is asked). The agent asks it
        /// before every take; <see cref="Start"/> asks the same <see cref="CaptureGatePlan.LeverRefused"/> at the
        /// last door. A lever of the GATE is said in the gate's sentence (<see cref="GateLeverRefused"/>); a lever
        /// of the shot or the adapter in the lever gate's own (<see cref="Levers.NotApprovedLog"/>). The refused
        /// lever is also LISTED IN THE NOVA CAPTURE WINDOW when no file on disk lists it (<see cref="ListInTheWindow"/>,
        /// the third audit's M1), so the studio has a row to tick.
        /// </summary>
        public static string? RefusedBeforeTake(CaptureGatePlan? gate, string projectRoot, AdShot shot)
        {
            if (gate?.LeverRefused(projectRoot, shot) is not { } lever) return null;
            ListInTheWindow(projectRoot, lever);
            return gate.LeversNeeded.Contains(lever) ? GateLeverRefused(lever) : Levers.NotApprovedLog(lever);
        }

        /// <summary>
        /// Third audit of E.4, M1 — A REFUSED LEVER NO FILE ON DISK LISTS GETS A ROW TO TICK. The window's rows are
        /// the two JSON files' levers (<see cref="Levers.Needed"/>) plus the commands the lever gate refused in this
        /// editor session (<see cref="LeverGateBridge.RefusedThisSession"/>). A shot COMPILED into the game (one a
        /// studio's own adapter returns from <c>Shots()</c>) is in neither file, and the check above refuses its take
        /// before any bridge runs — so its lever was refused with no row anywhere. Here it is refused through the
        /// lever gate itself, the path every refusal in the window comes from: a <see cref="LeverGateBridge"/> over
        /// a bridge that RUNS NOTHING (<see cref="RunsNothing"/>), with the run's cloud flag (a gated take's gate is
        /// forced on). The lever is un-ticked (<see cref="Levers.IsTicked"/> said so), so the gate refuses it and
        /// remembers it; nothing reaches the game whatever it answers, and no line is logged (the agent says the
        /// refusal). A lever a file lists already has its row, and is not remembered again.
        /// </summary>
        private static void ListInTheWindow(string projectRoot, string lever)
        {
            if (Levers.Needed(projectRoot).Contains(lever, StringComparer.Ordinal)) return;
            new LeverGateBridge(RunsNothing.Instance, projectRoot, log: null, cloudContent: true).Run(lever);
        }

        /// <summary>A cheat bridge that runs nothing and says so — what <see cref="ListInTheWindow"/>'s gate wraps.</summary>
        private sealed class RunsNothing : ICheatBridge
        {
            public static readonly RunsNothing Instance = new();
            public bool Run(string command) => false;
        }

        /// <summary>Second audit of E.4, M2 — what the agent waits for before this take's director is built
        /// (<see cref="TakeStart"/>): the shot's own screen without a gate, the gate's OR the shot's with one.</summary>
        public static TakeStart StartsOn(AdShot shot, AdShot? gate) =>
            gate == null
                ? new TakeStart(shot.ArmCondition ?? shot.Settle)
                : new TakeStart(gate.ArmCondition ?? gate.Settle, shot.ArmCondition ?? shot.Settle);

        /// <summary>
        /// Why a finished take is FAILED — null when it captured, and only then is its clip looked for and
        /// uploaded. A stop at the tutorial gate is said in the ONE sentence (<see cref="ProbeGate.StopSentence"/>);
        /// the recorder never started, so there is no clip to find, upload or quarantine. Every other failure
        /// is said as it always was: <c>take failed: </c> and the run log's last line.
        /// </summary>
        public static string? TakeFailure(AdDirector director)
        {
            if (director.AllCaptured) return null;
            if (director.PreambleRan && director.FailedStepKindName == AdDirector.FailedKindPreamble)
                return ProbeGate.StopSentence(director.PreambleFailedStepIndex, director.FailedReason);
            return "take failed: " + LastLine(director.Summary);
        }

        /// <summary>A take refused before anything ran because a lever only its tutorial gate needs is not
        /// ticked here — in the one sentence, naming the lever the way the lever gate does.</summary>
        public static string GateLeverRefused(string lever) =>
            ProbeGate.StopSentence(null, Levers.NotApprovedLog(lever));

        /// <summary>The run log's last non-blank line (the capture agent's, unchanged).</summary>
        internal static string LastLine(string summary)
        {
            var lines = summary.Split('\n').Where(l => l.Trim().Length > 0).ToArray();
            return lines.Length == 0 ? "(no summary)" : lines[lines.Length - 1].Trim();
        }
    }
}
