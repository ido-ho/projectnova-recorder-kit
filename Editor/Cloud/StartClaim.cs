using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Learn-and-drive v3 P5 (§3.6, §3.7) — THE DECLARED START a Try or a capture carries: the recipe chain (anchor first)
    /// as ONE recipes document, the sha256 of its UTF-8 bytes, the chain's ids, and the <c>get</c> paths a popup close must
    /// not change. The C# mirror of <c>StudioStartClaim</c> (<c>apps/api/src/studio/studio-capture-job.ts</c>): field
    /// names ARE the wire. It rides the CLAIM (never the poll) and is verified before any use (<see cref="StartPlan"/>).
    /// </summary>
    public sealed class StartClaim
    {
        public string Recipes { get; }
        public string Sha256 { get; }
        public IReadOnlyList<string> RecipeIds { get; }
        public IReadOnlyList<string> Watch { get; }

        public StartClaim(string recipes, string sha256, IReadOnlyList<string> recipeIds, IReadOnlyList<string> watch)
        {
            Recipes = recipes;
            Sha256 = sha256;
            RecipeIds = recipeIds;
            Watch = watch;
        }

        /// <summary>Null when not an object with the four fields in their shapes (then the claim carried no usable start —
        /// the caller treats a PRESENT but unusable block as no script, never as "no start").</summary>
        public static StartClaim? FromJson(JToken? t)
        {
            if (t is not JObject o) return null;
            var recipes = ProbeRequest.Str(o["recipes"]);
            var sha = ProbeRequest.Str(o["sha256"]);
            if (recipes == null || sha == null) return null;
            if (o["recipeIds"] is not JArray ids || ids.Count == 0 || ids.Any(x => x.Type != JTokenType.String)) return null;
            if (o["watch"] is not JArray watch || watch.Any(x => x.Type != JTokenType.String)) return null;
            return new StartClaim(recipes, sha, ids.Values<string>().Select(x => x!).ToList(), watch.Values<string>().Select(x => x!).ToList());
        }

        public JObject ToJson() => new()
        {
            ["recipes"] = Recipes,
            ["sha256"] = Sha256,
            ["recipeIds"] = new JArray(RecipeIds),
            ["watch"] = new JArray(Watch),
        };
    }

    /// <summary>P5 — THE RESTART RULE's in-session reset a capture names (a proven reset cheat's fixed command), or none.
    /// Mirror of <c>StudioStartReset</c>.</summary>
    public sealed class StartReset
    {
        public string? Cheat { get; }
        public StartReset(string? cheat) => Cheat = cheat;

        /// <summary>Null when absent, JSON null, or not the shape (a reset that did not arrive whole is never a reset).</summary>
        public static StartReset? FromJson(JToken? t)
        {
            if (t is not JObject o) return null;
            var c = o["cheat"];
            if (c == null || c.Type == JTokenType.Null) return new StartReset(null);
            return c.Type == JTokenType.String && !string.IsNullOrWhiteSpace(c.Value<string>()) ? new StartReset(c.Value<string>()) : null;
        }

        public JObject ToJson() => new() { ["cheat"] = Cheat == null ? JValue.CreateNull() : Cheat };
    }

    /// <summary>
    /// P5 — THE START THAT ARRIVED, read before any use: the sha of its UTF-8 bytes against the sender's (a truncated claim
    /// must not become a start that plays half a chain), the document through the kit's ONE recipe reader
    /// (<see cref="RecipeFile.Read"/> — the reader <c>recipe.cases.json</c> holds to the box's schema), the recipes it
    /// names in the order it names them, and a real chain from the anchor (<see cref="RecipeChain.Of"/>) — or refused by
    /// name. Pure: no Unity API, so the EditMode tests run the exact comparisons production runs.
    /// </summary>
    public sealed class StartPlan
    {
        public string? Refusal { get; private set; }
        public IReadOnlyList<RecipeFile.Recipe>? Chain { get; private set; }
        /// <summary>sha256 of the start text that ARRIVED — measured here, echoed in the facts.</summary>
        public string Sha256 { get; private set; } = "";
        public IReadOnlyList<string> Watch { get; private set; } = Array.Empty<string>();

        /// <summary>The sentence of a run that carries both a tutorial gate and a declared start.</summary>
        public const string BothRefused =
            "this job carries both a tutorial gate and a declared start — one question, two answers; nothing was run";

        public static StartPlan Read(StartClaim claim, string again)
        {
            var plan = new StartPlan { Sha256 = SyncNova.Sha256OfText(claim.Recipes), Watch = claim.Watch };
            if (!string.Equals(plan.Sha256, claim.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                plan.Refusal = ProbePlan.ShaMismatch("declared start", plan.Sha256, claim.Sha256, again);
                return plan;
            }
            var recipes = RecipeFile.Read(claim.Recipes, out var error);
            if (recipes == null)
            {
                plan.Refusal = "the declared start that arrived does not read (" + error + ") — nothing was run; " + again;
                return plan;
            }
            if (recipes.Count == 0 || !recipes.Select(r => r.Id).SequenceEqual(claim.RecipeIds))
            {
                plan.Refusal = "the declared start's recipes are not the ones it names — nothing was run; " + again;
                return plan;
            }
            var chain = RecipeChain.Of(recipes, recipes[recipes.Count - 1].Id, out var chainError);
            if (chain == null || !chain.Select(r => r.Id).SequenceEqual(claim.RecipeIds))
            {
                plan.Refusal = "the declared start is not a chain from \"lobby after boot\" (" + (chainError ?? "its order is not its starts'") + ") — nothing was run; " + again;
                return plan;
            }
            plan.Chain = chain;
            return plan;
        }
    }

    /// <summary>
    /// P5 — A RECORDING THAT PLAYS A DECLARED START, as the functions the capture agent calls, so the lines that decide it are
    /// tested here rather than trusted inside a Play-Mode coroutine: what the claim carried, the director's settings for a
    /// take, what a start that stopped a take means, and how long the restart rule's settle wait still has to run.
    /// </summary>
    public static class CaptureStart
    {
        /// <summary>What a recording tells the studio to do next after a refusal of its start.</summary>
        public const string RecordAgain = "start the recording again";

        /// <summary>The sentence a take stopped by its declared start fails with.</summary>
        public const string TakeStoppedPrefix = "the declared start stopped this take: ";

        /// <summary>A capture claim's <c>start</c> and <c>reset</c>. <paramref name="broken"/> non-null = a start was PRESENT
        /// but did not arrive whole — the job is refused by that sentence, never recorded from wherever the game stands.</summary>
        public static (StartClaim? Start, StartReset? Reset) FromClaimBody(string body, out string? broken)
        {
            broken = null;
            JObject root;
            try { root = NovaJson.ParseObject(body); }
            catch (Exception) { return (null, null); }
            var st = root["start"];
            if (st == null || st.Type == JTokenType.Null) return (null, null);
            var start = StartClaim.FromJson(st);
            if (start == null)
            {
                broken = "this recording's declared start arrived incomplete — nothing was recorded; " + RecordAgain;
                return (null, null);
            }
            return (start, StartReset.FromJson(root["reset"]));
        }

        /// <summary>Kit 0.14.1 (invariant 187) — a claim's <c>home</c> (the game's go-home lever, <c>{ cheat }</c> — the
        /// <see cref="StartReset"/> shape), for every kind: the command, or null when absent, JSON null or not the shape (a
        /// lever that did not arrive whole is no lever — the start check then asks the person).</summary>
        public static string? HomeFromClaimBody(string body)
        {
            try { return StartReset.FromJson(NovaJson.ParseObject(body)["home"])?.Cheat?.Trim(); }
            catch (Exception) { return null; }
        }

        /// <summary>Kit 0.14.0 (fix 2c) — a capture claim's <c>back</c> (the game's way back to the lobby,
        /// <see cref="BackToLobby"/>), or null when absent or JSON null. <paramref name="offered"/> = the claim carried the
        /// key at all (audit M4). <paramref name="broken"/> non-null = it was PRESENT but did not arrive whole — never the
        /// job's refusal (audit M5): only a lobby shot that would need it fails, saying so.</summary>
        public static StartClaim? BackFromClaimBody(string body, out bool offered, out string? broken)
        {
            broken = null;
            offered = false;
            JObject root;
            try { root = NovaJson.ParseObject(body); }
            catch (Exception) { return null; }
            offered = root.ContainsKey("back");
            var bk = root["back"];
            if (bk == null || bk.Type == JTokenType.Null) return null;
            var back = StartClaim.FromJson(bk);
            if (back == null) broken = ProbeRequest.BackBrokenReason;
            return back;
        }

        /// <summary>The director's settings for ONE take that plays the declared start first: the chain, the watched paths,
        /// every press an edge, the lever gate forced on (the start is cloud-delivered text, as a try's is) with the
        /// camera-pose Type guard, and — when the restart rule says so — the in-session reset first. ONE attempt (fresh audit
        /// of P5, K5): the start runs once per take, so a second attempt would start wherever the first one ended — the
        /// restart rule gives the NEXT take its fresh start instead.</summary>
        public static AdDirector.Options OptionsFor(string projectRoot, StartPlan plan, string? resetCheat,
            AdDirector.Options? tuning = null)
        {
            var options = tuning ?? new AdDirector.Options();
            options.StartPath = plan.Chain;
            options.WatchGets = plan.Watch;
            options.RecordEdges = true;
            options.ResetCheat = resetCheat;
            options.MaxAttempts = 1;
            options.CloudContent = true;
            options.ProjectRoot = projectRoot;
            options.WrapCheats = (gated, log) => new ProbeCameraTypeGuard(gated, projectRoot, log, options.CloudContent);
            return options;
        }

        /// <summary>Kit 0.14.1 (audit H3) — the sentence a job's remaining takes fail with when its start check ASKED and the
        /// game was not put there, or null (no ask, or it went on).</summary>
        public static string? AskUnanswered(StartCheck? check) =>
            check is { Asked: true, Ok: false } ? check.Failure ?? check.AskSentence : null;

        /// <summary>Kit 0.14.1 (invariant 187) — every kit job that plays a declared start checks its start screen first (the
        /// go-home lever, then the person in the Nova Capture window): one setting for the Try, the recording and the recipe
        /// Try, so none can forget it.</summary>
        public static AdDirector.Options WithStartCheck(AdDirector.Options options, string? home, IStartAsk? ask = null)
        {
            options.CheckStart = true;
            options.HomeCheat = home;
            options.Ask = ask ?? StartAsk.Window;
            return options;
        }

        /// <summary>Did the declared start stop this take? Then its sentence; otherwise null. A ready gate that never opened
        /// on a take that plays a declared start is the start's stop too (fresh audit of P5, K6): the gate runs first, told
        /// the work starts at the lobby, so a game stuck on its loading screen stops THERE — before the start path's own
        /// boot wait could say "boot-stuck".</summary>
        public static string? StartStop(AdDirector director) =>
            director.FailedStepKindName == AdDirector.FailedKindStartPath || director.FailedStepKindName == AdDirector.FailedKindReady
                ? TakeStoppedPrefix + (director.FailedReason ?? "no reason was recorded")
                : null;

        /// <summary>What kind of start stop this was, for <see cref="RestartRule.AfterStartStopped"/>: the start path's own
        /// kind, or <see cref="StartPathRun.KindBootStuck"/> when the lobby-told ready gate never opened on the FIRST take
        /// after Play was entered (K6) — only then can it be a boot that stayed on its loading screen. On a later take in
        /// the same Play session (round 2, R4) a closed gate is the take's stop, not a boot to retry.</summary>
        public static string? StopKind(AdDirector director, bool firstTakeAfterBoot) =>
            director.FailedStepKindName == AdDirector.FailedKindReady
                ? (firstTakeAfterBoot ? StartPathRun.KindBootStuck : null)
                : director.StartPath?.FailedKind;

        /// <summary>
        /// Fresh audit of P5, K1 — WHY A TAKE THAT PLAYS A DECLARED START IS REFUSED BEFORE ANYTHING RUNS, or null. Its
        /// director's lever gate is forced on (<see cref="OptionsFor"/>), so every command of the take meets it: the SHOT's
        /// (its <c>setup</c> and cheat steps) and the ADAPTER's (<see cref="CaptureGatePlan.TakeLeversNeeded"/>'s halves), the
        /// start's own cheat steps, and the restart rule's reset cheat. An un-ticked one would be SKIPPED at run time while the
        /// take still recorded — without the state it set up. Asked before every take, as a gated take's
        /// (<see cref="CaptureRun.RefusedBeforeTake"/>): the first un-ticked lever, then the first ticked one the risk rule
        /// holds back until the non-production tick — each said in the lever gate's own sentence, and a lever no file lists
        /// gets its row in the Nova Capture window.
        /// </summary>
        public static string? RefusedBeforeTake(string projectRoot, AdShot shot, StartPlan plan, string? resetCheat)
        {
            var needed = new List<string>();
            void Add(string? lever)
            {
                if (!string.IsNullOrWhiteSpace(lever) && !needed.Contains(lever!)) needed.Add(lever!);
            }
            foreach (var r in plan.Chain ?? Array.Empty<RecipeFile.Recipe>())
                foreach (var step in r.Steps.OfType<JObject>())
                    if (step["kind"]?.Value<string>() == "cheat") Add(step["command"]?.Value<string>());
            Add(resetCheat);
            foreach (var lever in Levers.NeededFrom(CaptureGatePlan.ShotDocument(shot), CaptureGatePlan.AdapterTextOn(projectRoot)))
                Add(lever);
            var approved = Levers.Approved(projectRoot);
            if (Levers.FirstUnticked(needed, approved) is { } unticked)
            {
                CaptureRun.ListInTheWindow(projectRoot, unticked);
                return Levers.UntickedLog(approved, unticked);
            }
            if (Levers.FirstRiskyNotYet(needed, projectRoot) is { } risky)
                return CheatRisk.NotYetLog(risky.Lever, risky.Why);
            return null;
        }

        /// <summary>THE RESTART RULE's settle wait: seconds still to wait after the last Play exit before Play is entered
        /// again (0 = enter now). No exit on record = no wait.</summary>
        public static double SettleRemainingSec(ClockStamp? lastExit, ClockStamp now, double settleSec)
        {
            if (lastExit == null) return 0;
            return Math.Max(0, settleSec - lastExit.SecondsTo(now));
        }
    }
}
