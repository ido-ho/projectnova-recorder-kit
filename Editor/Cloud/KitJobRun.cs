using System.Collections.Generic;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Learn-and-drive v3 (spike A part 2; plan §3.3, A6 blocker 2) — THE ONE WAY A KIT-RUN JOB BUILDS ITS
    /// DIRECTOR SETTINGS. A job the kit runs on its own — a try, the cheat search, auto-try, a Teach replay — is
    /// never "local authorship": no person at this keyboard wrote what it runs. So its lever gate is forced ON,
    /// whatever the files on disk say (<see cref="Levers.GateActiveFor"/>). Without this, the gate is live only
    /// when a cloud-delivered <c>shots.json</c>/<c>adapter.json</c> is on disk (<see cref="Levers.GateActive"/>), and
    /// a job that ran before the first Send would run every candidate cheat un-ticked.
    ///
    /// With the gate on, the director's press guard (<see cref="PressGuard"/>) fences every click and hold too.
    /// </summary>
    public static class KitJobRun
    {
        /// <summary>Director settings for a kit-run job: the gate forced on, this project's root, and the
        /// press allowlist when the job presses only what a need matched (null = risky-word fence only).</summary>
        /// <remarks>
        /// Also set here, for every kit job: ONE attempt — a kit job answers "what does this do", and a retry would run
        /// its writes into the studio's game a second time to answer it — and the camera-pose Type rule asked with
        /// this run's cloud flag (<see cref="ProbeCameraTypeGuard"/>). The recorder is the CALLER's: a try records
        /// nothing, a capture records a take.
        /// </remarks>
        public static AdDirector.Options DirectorOptions(string projectRoot,
            IReadOnlyCollection<string>? pressAllowlist = null)
        {
            var options = new AdDirector.Options
            {
                CloudContent = true,
                ProjectRoot = projectRoot,
                PressAllowlist = pressAllowlist,
                MaxAttempts = 1,
            };
            options.WrapCheats = (gate, log) =>
                new ProbeCameraTypeGuard(gate, projectRoot, log, options.CloudContent);
            return options;
        }
    }
}
