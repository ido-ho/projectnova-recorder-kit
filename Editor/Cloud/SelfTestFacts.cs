using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Slice B/C commit 2 — what a <c>self-test</c> job reports back: the Phase 0 Doctor's reading
    /// of this editor, as FACTS.
    ///
    /// THE KIT EMITS FACTS, NEVER VERDICTS (evidence plan 3B, owner-ruled 2026-08-30). Nothing here
    /// says "healthy" or "ready". Whether an editor is fit to record is decided on the web, from
    /// these values, by the side that is not the one being examined — a machine grading its own
    /// health is the shape that produces a green check over a broken setup.
    ///
    /// Identity in particular: this reports THIS editor's own gameId and nothing else. It does not
    /// compare it to the job's. The web holds the workspace's bound game independently and does the
    /// comparison there (invariant 100 — a check that asks the answering side which game it should
    /// have been compares a value with itself and can never fire).
    ///
    /// No Unity API is touched in this file, so every rule in it is covered by the kit's EditMode
    /// tests without an editor session.
    /// </summary>
    public static class SelfTestFacts
    {
        /// <summary>The facts document, as the multipart `facts` part. Every field is always
        /// present — an ABSENT field and a false one read the same on a screen, and "the kit could
        /// not tell" is a different answer from "no". Unknowable strings are null, never "".</summary>
        public static JObject Build(
            string kitVersion,
            string unityVersion,
            double readyWaitedSec,
            int framesDrawn,
            float playingSec,
            float timeScale,
            bool runInBackground,
            bool playerSettingsRunInBackground,
            bool unityRecorderPresent,
            string recorderDriver,
            string? editorGameId,
            bool adapterJsonPresent,
            string? bootScene,
            int shotCount,
            IReadOnlyList<string>? shotLoadErrors,
            bool frameCaptured,
            string? frameNote,
            string? factsError = null,
            // spec §8.10 (kit 0.9.0) — null = the kit could not tell
            string? activeScene = null,
            int? consoleErrors = null,
            string? firstConsoleError = null,
            string? gitBranch = null,
            string? gitCommit = null,
            int enabledSceneCount = 0,
            double? leftBootAfterSec = null)
        {
            // "Stuck on the boot scene": still on it after the whole wait (kit 0.9.1: including up to
            // 45 s for it to LEAVE — the 0.9.0 photo at ~4 s called a healthy slow boot stuck), AND the game logged
            // errors, AND the build HAS another scene to move to. All three, because a one-scene game
            // legitimately stays on its only scene, and a slow boot with a clean console is not a
            // fault (plan item 1 — a check must not fail a game that is fine).
            bool? stuck = activeScene == null || bootScene == null || consoleErrors == null
                ? (bool?)null
                : string.Equals(activeScene, bootScene, StringComparison.Ordinal) && consoleErrors > 0 && enabledSceneCount > 1;
            return new JObject
            {
                // what this editor IS
                ["kitVersion"] = kitVersion,
                ["unityVersion"] = unityVersion,
                // Application.runInBackground, read INSIDE Play Mode. With it false an unfocused
                // editor stops rendering entirely and every recording is frozen frames — the kit
                // forces it on Play Mode entry (RelayBoot), so a false here means the force failed.
                ["runInBackground"] = runInBackground,
                // AUDIT m5: the BUILD setting, a different number from the runtime property above
                // (which the kit forces true on Play Mode entry). This one bites outside
                // kit-driven play, and nothing else reports it.
                ["playerSettingsRunInBackground"] = playerSettingsRunInBackground,
                // AUDIT M3 (redone after the re-audit): what the game actually DID before the
                // photo. Frames, not a verdict — a blank frame with 0 frames drawn after 30 s is a
                // hung game; a blank frame with 400 frames drawn is a game that is running fine
                // and showing nothing, which is a completely different fault. The earlier version
                // reported an adapter ready-gate result, which is footage hygiene, not "is it up".
                ["readyWaitedSec"] = Math.Round(readyWaitedSec, 1),
                ["framesDrawn"] = framesDrawn,
                // UNSCALED seconds since Play Mode began. Not `timeSinceLevelLoad`: that is scaled
                // by `timeScale` and resets per scene, so a game that pauses itself while the
                // editor is unfocused — which is the Doctor's normal condition, the studio is in
                // the browser — reported "240 frames in 4.2s · scene up 0s".
                ["playingSec"] = Math.Round(playingSec, 1),
                // A game that paused itself draws frames and advances no game time: it looks like
                // a healthy editor and records unusable footage. That is worth one number.
                ["timeScale"] = Math.Round(timeScale, 3),
                ["unityRecorderPresent"] = unityRecorderPresent,
                ["recorderDriver"] = recorderDriver,

                // who this editor THINKS it is — reported, not judged (see the class comment)
                ["editorGameId"] = editorGameId == null ? JValue.CreateNull() : new JValue(editorGameId),
                ["adapterJsonPresent"] = adapterJsonPresent,

                // what it booted, and what it can see
                ["bootScene"] = bootScene == null ? JValue.CreateNull() : new JValue(bootScene),
                ["shotCount"] = shotCount,
                // A malformed shots.json loads as an EMPTY list rather than throwing (a kit
                // invariant). Without the parse reason travelling with the count, "0 shots" and
                // "your shots.json is broken on line 12" look identical on the row.
                ["shotLoadErrors"] = new JArray((shotLoadErrors ?? new List<string>())
                    .Where(e => !string.IsNullOrWhiteSpace(e))),

                // the frame. "No frame" is the single most important thing this job can report,
                // so it is a fact with a reason, never a missing field.
                ["frameCaptured"] = frameCaptured,
                ["frameNote"] = frameNote == null ? JValue.CreateNull() : new JValue(frameNote),

                // Something in THIS GAME'S code threw while the facts were being gathered (a custom
                // adapter's `Shots()`, say). The game's own code failing is a measurement, not a
                // crashed Doctor — so it rides up as a fact and the result is still posted. Without
                // it the exception reached the agent's catch and the job retried forever with the
                // run stuck on "Running in your editor…".
                ["factsError"] = factsError == null ? JValue.CreateNull() : new JValue(factsError),

                // spec §8.10 — where the game actually IS, and what it said about it
                ["activeScene"] = activeScene == null ? JValue.CreateNull() : new JValue(activeScene),
                ["stuckOnBootScene"] = stuck == null ? JValue.CreateNull() : new JValue(stuck.Value),
                ["consoleErrors"] = consoleErrors == null ? JValue.CreateNull() : new JValue(consoleErrors.Value),
                ["firstConsoleError"] = firstConsoleError == null ? JValue.CreateNull() : new JValue(firstConsoleError),
                ["gitBranch"] = gitBranch == null ? JValue.CreateNull() : new JValue(gitBranch),
                ["gitCommit"] = gitCommit == null ? JValue.CreateNull() : new JValue(gitCommit),
                // kit 0.9.1: seconds after Play when the game left its boot scene; null = it did not
                // (or had nowhere to go). The check waits up to 45 s for this before deciding "stuck".
                ["leftBootAfterSec"] = leftBootAfterSec == null ? JValue.CreateNull() : new JValue(leftBootAfterSec.Value),
            };
        }

        public static string ToJson(JObject facts) => facts.ToString(Formatting.None);
    }
}
