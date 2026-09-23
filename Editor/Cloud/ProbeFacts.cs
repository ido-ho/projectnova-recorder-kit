using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Slice E.1 — what a <c>probe</c> job reports back: what the ONE shot DID in this game, as
    /// FACTS.
    ///
    /// THE KIT EMITS FACTS, NEVER VERDICTS (evidence plan 3B, owner-ruled 2026-08-30). Nothing here
    /// says the path is wrong, or that the studio should change it. It says which step stopped, what
    /// the director's own sentence was, what was on screen at that moment, and what the run log
    /// said. Whether that means the shot is broken — and what to propose instead — is decided on the
    /// web, against the script the server SENT (invariant 100).
    ///
    /// No Unity API is touched in this file, so every rule in it is covered by the kit's EditMode
    /// tests without an editor session.
    /// </summary>
    public static class ProbeFacts
    {
        /// <summary>How many names from the screen ride back, and how long each may be. A ui-dump of
        /// a busy game is hundreds of rows; this is evidence for a person reading one row of a web
        /// page, not a scene dump.</summary>
        public const int MaxUiNames = 200;
        public const int MaxUiNameChars = 120;

        /// <summary>How much of the director's log rides back. The LAST lines, not the first: the
        /// sentence that says why it stopped is at the end.</summary>
        public const int MaxLogLines = 200;
        public const int MaxLogLineChars = 300;

        /// <summary>`failedStepKind` when every step ran and the shot's settle condition never held
        /// — there is no step index to blame, and "null, null" would be indistinguishable from a
        /// shot that reached the end. The director is what sets it, so the value is aliased from
        /// there rather than spelled twice.</summary>
        public const string SettleStepKind = AdDirector.FailedKindSettle;

        /// <summary>`failedStepKind` when the run never got as far as the first step because the
        /// adapter's ready gate could not reach the baseline, even after recovery.</summary>
        public const string ReadyStepKind = AdDirector.FailedKindReady;

        /// <summary>`failedStepKind` when a <c>{placeholder}</c> in the shot could not be bound,
        /// which aborts the shot before any step runs.</summary>
        public const string BindingStepKind = AdDirector.FailedKindBinding;

        /// <summary>
        /// DID THIS SHOT REACH THE END? True only when every step ran AND the shot's settle
        /// condition held (<see cref="AdDirector.AllCaptured"/> is exactly that), with no failed step
        /// and no lever refusal. The last clause is belt and braces — a refused lever means the
        /// director was never built, so <paramref name="allCaptured"/> is false — and it is here
        /// because "reached" is the one field the website turns into a sentence about the studio's
        /// game, and it must fail closed.
        /// </summary>
        public static bool Reached(bool allCaptured, int? failedStep, string? leverRefused) =>
            allCaptured && failedStep == null && string.IsNullOrEmpty(leverRefused);

        /// <summary>The shots.json spelling of a step kind — <c>waitFor</c>, not <c>WaitFor</c>. The
        /// website reads this against the step kinds in the shot IT sent, so it is the grammar's
        /// word, never the C# enum's. ONE definition, beside the enum
        /// (<see cref="ShotKind.Name"/>), so the facts and the loader cannot disagree.</summary>
        public static string StepKindName(AdStepKind kind) => ShotKind.Name(kind);

        /// <summary>The first <paramref name="maxItems"/> entries, each cut to
        /// <paramref name="maxChars"/> characters exactly (no ellipsis — an ellipsis would either
        /// overflow the cap or eat a character of evidence). Nulls and blanks are dropped.</summary>
        public static IReadOnlyList<string> CapFirst(IEnumerable<string>? lines, int maxItems, int maxChars) =>
            Cap(lines, maxItems, maxChars, fromEnd: false).Kept;

        /// <summary>The LAST <paramref name="maxItems"/> entries, same per-line cut. For the
        /// director's log, whose last lines are the ones that say what happened.</summary>
        public static IReadOnlyList<string> CapLast(IEnumerable<string>? lines, int maxItems, int maxChars) =>
            Cap(lines, maxItems, maxChars, fromEnd: true).Kept;

        /// <summary>The kept entries, and how many entries past <paramref name="maxItems"/> were DROPPED
        /// (blanks are not entries, so they are not counted). One function, so the count is of the
        /// very cut that produced the list.</summary>
        private static (List<string> Kept, int Cut) Cap(IEnumerable<string>? lines, int maxItems, int maxChars,
            bool fromEnd)
        {
            var kept = (lines ?? Array.Empty<string>())
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();
            var cut = 0;
            if (kept.Count > maxItems)
            {
                cut = kept.Count - maxItems;
                kept = fromEnd
                    ? kept.GetRange(kept.Count - maxItems, maxItems)
                    : kept.GetRange(0, maxItems);
            }
            for (var i = 0; i < kept.Count; i++)
                if (kept[i].Length > maxChars)
                    kept[i] = kept[i].Substring(0, maxChars);
            return (kept, cut);
        }

        /// <summary>
        /// The facts document, as the multipart `facts` part. Every field is always present — an
        /// ABSENT field and a false one read the same on a screen, and "the kit could not tell" is a
        /// different answer from "no". Unknowable strings are null, never "".
        /// </summary>
        public static JObject Build(
            string? shot,
            bool allCaptured,
            int? failedStep,
            string? failedStepKind,
            string? reason,
            IEnumerable<string>? log,
            IEnumerable<string>? uiDump,
            int screenWidth,
            int screenHeight,
            string shotSha256,
            string adapterSha256,
            IReadOnlyList<string>? leversNeeded,
            IReadOnlyList<string>? leversApproved,
            string? leverRefused,
            float timeScale,
            bool frameCaptured,
            string? frameNote,
            string kitVersion,
            string? factsError = null,
            bool gateRan = false,
            int? gateFailedStep = null,
            string? gateSha256 = null,
            string uiDriver = UiDriverKit)
        {
            var ui = Cap(uiDump, MaxUiNames, MaxUiNameChars, fromEnd: false);
            return new JObject
            {
                // WHICH shot this is about, as the loader read its name. The website compares it
                // with the shot it SENT — it never asks this document which shot it should be
                // (invariant 100: a check that asks the answering side is comparing a value with
                // itself and can never fire).
                ["shot"] = shot == null ? JValue.CreateNull() : new JValue(shot),

                ["reached"] = Reached(allCaptured, failedStep, leverRefused),
                // 0-based index into the shot's OWN steps array. Null when no single step is to
                // blame — the settle, the ready gate, a binding, or a shot that reached the end.
                ["failedStep"] = failedStep == null ? JValue.CreateNull() : new JValue(failedStep.Value),
                ["failedStepKind"] = failedStepKind == null ? JValue.CreateNull() : new JValue(failedStepKind),
                // The director's own sentence, verbatim — never a sentence this file composed.
                ["reason"] = reason == null ? JValue.CreateNull() : new JValue(reason),

                ["log"] = new JArray(CapLast(log, MaxLogLines, MaxLogLineChars)),
                // The names that were on screen AT THE MOMENT IT STOPPED — the same list the
                // `ui-dump` command writes, taken before anything was restored.
                ["uiDump"] = new JArray(ui.Kept),
                // E.2's fifth audit: how many rows the cap above DROPPED (0 = none). Without it a list
                // of exactly MaxUiNames rows cannot say whether a row past the cut shares a name with
                // one of them — the website reads this instead of guessing from the count.
                ["uiDumpCut"] = ui.Cut,
                ["screen"] = new JObject { ["width"] = screenWidth, ["height"] = screenHeight },

                // What ARRIVED, echoed so the server can check the answer is about the script it
                // sent. The shot runs from exactly that text; the adapter that runs is this
                // project's own adapter.json, which ProbePlan.Prepare refused to run unless its
                // sha is this one (audit S3).
                ["shotSha256"] = shotSha256 ?? "",
                ["adapterSha256"] = adapterSha256 ?? "",

                ["leversNeeded"] = new JArray(leversNeeded ?? Array.Empty<string>()),
                ["leversApproved"] = new JArray(leversApproved ?? Array.Empty<string>()),
                // Non-null = the shot was NOT run at all: this lever is not ticked on this machine.
                ["leverRefused"] = leverRefused == null ? JValue.CreateNull() : new JValue(leverRefused),

                // A game that paused itself draws frames and advances no game time — it looks fine
                // and nothing in the shot can ever complete.
                ["timeScale"] = Math.Round(timeScale, 3),

                // The frame is the point, exactly as it is for the Doctor: "no frame" is a fact with
                // a reason, never a missing field.
                ["frameCaptured"] = frameCaptured,
                ["frameNote"] = frameNote == null ? JValue.CreateNull() : new JValue(frameNote),

                ["kitVersion"] = kitVersion ?? "",
                // A READING the kit could not take (the names on screen threw, or the screen was
                // read after the run instead of at the stop) — said, never silently absent.
                ["factsError"] = factsError == null ? JValue.CreateNull() : new JValue(factsError),

                // Slice E.4 — the TUTORIAL GATE the claim carried: did it RUN (the director started it —
                // its recovery, then its setup, then its steps; first audit of E.4, M2), and which of ITS
                // steps stopped the try (null: none did, or its end state did not hold — `failedStepKind`
                // then says `tutorial-gate`). Always present, so a site can tell this kit from one that
                // never heard of the gate.
                ["gateRan"] = gateRan,
                ["gateFailedStep"] = gateFailedStep == null ? JValue.CreateNull() : new JValue(gateFailedStep.Value),
                // First audit of E.4, M5 — the sha256 of the gate text that ARRIVED (ProbePlan.GateSha256:
                // the text the gate runs from — Prepare refuses any other), null when the claim carried
                // none. Echoed as the two shas above are, so the site checks the gate it SENT is the one.
                ["gateSha256"] = gateSha256 == null ? JValue.CreateNull() : new JValue(gateSha256),

                // E.2's sixth audit (M3) — WHOSE finder read the screen (<see cref="UiDriverOf"/>): the
                // kit's own, whose click reaches what `uiDump` prints by the printed name, or the game
                // adapter's own `Ui` driver, which may not. Always present; the website refuses an
                // answer without it.
                ["uiDriver"] = uiDriver,
            };
        }

        /// <summary>
        /// THE FACTS OF ONE TRY, from what the try holds when it ends — its plan, its director (null: a lever was
        /// refused before one was built, so the reason is the lever gate's own sentence for that lever) and the
        /// game adapter — plus what the screen read gave. The second audit of E.4 (KM4, KM5): this call stood
        /// inside the capture agent's Play-Mode coroutine, where no EditMode test runs it, and two of its
        /// arguments have defaults a dropped argument silently takes — <c>gateSha256</c> (null: every gated try
        /// then refused as another gate) and <c>uiDriver</c> (<c>"kit"</c>: a custom driver's try said to be the
        /// kit's, silently). Every argument is the expression the agent passed, moved here unchanged.
        /// </summary>
        public static JObject OfTry(ProbePlan plan, AdDirector? director, GameAdapter adapter,
            IEnumerable<string>? uiDump, int screenWidth, int screenHeight, float timeScale,
            bool frameCaptured, string? frameNote, string? factsError) =>
            Build(
                shot: plan.Shot!.Name,
                allCaptured: director?.AllCaptured ?? false,
                failedStep: director?.FailedStepIndex,
                failedStepKind: director?.FailedStepKindName,
                // Not run: the gate's own sentence for the lever that stopped it.
                reason: director != null ? director.FailedReason : Levers.NotApprovedLog(plan.LeverRefused!),
                log: director?.LogLines,
                uiDump: uiDump,
                screenWidth: screenWidth,
                screenHeight: screenHeight,
                shotSha256: plan.ShotSha256,
                adapterSha256: plan.AdapterSha256,
                leversNeeded: plan.LeversNeeded,
                leversApproved: plan.LeversApproved,
                leverRefused: plan.LeverRefused,
                timeScale: timeScale,
                frameCaptured: frameCaptured,
                frameNote: frameNote,
                kitVersion: KitVersion.Current,
                factsError: factsError,
                // Slice E.4 — the tutorial gate, as the director ran it (a refused lever ran nothing)
                gateRan: director?.PreambleRan ?? false,
                gateFailedStep: director?.PreambleFailedStepIndex,
                // First audit of E.4, M5 — the sha of the gate text that ARRIVED (null: none did)
                gateSha256: plan.GateSha256,
                // E.2's sixth audit (M3) — whose finder read (and clicks) the screen
                uiDriver: UiDriverOf(adapter));

        /// <summary><c>uiDriver</c> when the screen was read with the kit's own <see cref="UguiDriver"/>.</summary>
        public const string UiDriverKit = "kit";

        /// <summary><c>uiDriver</c> when the game adapter drives the UI with its OWN driver.</summary>
        public const string UiDriverCustom = "custom";

        /// <summary>
        /// E.2's sixth audit (M3), the kit half — WHOSE UI DRIVER A TRY READS AND CLICKS WITH:
        /// <see cref="UiDriverCustom"/> when the game adapter sets its own <see cref="GameAdapter.Ui"/>
        /// (anything but the kit's <see cref="UguiDriver"/>), else <see cref="UiDriverKit"/> — the adapter
        /// that sets none gets the kit's (<c>adapter.Ui ?? new UguiDriver()</c>, the director's own rule), and
        /// so does the JSON-driven default adapter, which sets the kit's.
        /// </summary>
        public static string UiDriverOf(GameAdapter adapter) =>
            adapter.Ui == null || adapter.Ui is UguiDriver ? UiDriverKit : UiDriverCustom;

        public static string ToJson(JObject facts) => facts.ToString(Formatting.None);

        /// <summary>The note a RETRIED post carries when the frame that was taken is no longer on
        /// disk to send.</summary>
        public const string FrameLostNote =
            "a frame was taken, but it was no longer on disk when the result was sent again";

        /// <summary>
        /// A retried result POST re-sends the facts that were GATHERED (the shot is not run again —
        /// see <c>CaptureProgress.ProbeFactsJson</c>), and those facts say whether a frame was
        /// taken. If the file has gone since, the facts must not claim a frame the upload does not
        /// carry: <c>frameCaptured</c> becomes false and the note says why. Everything else is left
        /// exactly as it was gathered. Text that is not a facts object is returned unchanged.
        /// </summary>
        public static string ReconcileFrame(string factsJson, bool frameOnDisk)
        {
            JObject facts;
            try { facts = NovaJson.ParseObject(factsJson); }
            catch (Exception) { return factsJson; }
            if (frameOnDisk || facts["frameCaptured"]?.Type != JTokenType.Boolean
                || !facts["frameCaptured"]!.Value<bool>())
                return factsJson;
            facts["frameCaptured"] = false;
            facts["frameNote"] = FrameLostNote;
            return ToJson(facts);
        }
    }
}
