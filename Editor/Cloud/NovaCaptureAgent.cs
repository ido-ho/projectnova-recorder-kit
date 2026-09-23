using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// A6 step 10c — the capture agent that lives INSIDE the kit, so the studio installs nothing
    /// else. While "run capture jobs" is on it polls the hosted API with the studio key, claims a
    /// pending run, records each shot with the existing <see cref="AdDirector"/>, uploads the take,
    /// and reports done. Everything is outbound HTTPS started from this editor; nothing dials in.
    ///
    /// The credential it holds can do exactly the studio routes on its own account's runs — no
    /// database URL, no storage key, no model key ever reaches this class.
    ///
    /// Design rules, each learned elsewhere in this kit:
    ///  - pumped from <c>EditorApplication.update</c> like <see cref="RelayBoot"/>; never blocks;
    ///  - no work in batch mode (headless test runs must not talk to a server);
    ///  - progress is on disk (<see cref="CaptureProgress"/>) BEFORE anything that can reload the
    ///    domain — entering Play Mode is one — so the agent that comes up afterwards resumes;
    ///  - <see cref="AdDirector.Run"/> returns null while another director is active: wait, retry;
    ///  - the take is read from disk only after the director reports finished plus a beat — an
    ///    unfinalised MP4 has no moov atom and looks exactly like a corrupt file.
    /// </summary>
    [InitializeOnLoad]
    public static class NovaCaptureAgent
    {
        public const double PollIntervalSec = 15;
        private const double AdapterWaitSec = 90;
        private const double FileSettleSec = 1.5;
        /// <summary>How long to wait, before EACH shot, for the screen the shot is about (its arm
        /// condition, else its settle) to be on — a freshly booted game needs seconds to reach its
        /// board, and a shot started on the loading screen fails twice and quarantines two takes
        /// (live-found 2026-09-05 on SNL: "arm condition Present 'RollBTN' never held in 15s").
        /// Not met in time → the director runs anyway and applies its own retry rules.</summary>
        private const double ShotReadyWaitSec = 60;
        /// <summary>A director started by something else (the relay, the menu) blocks a shot; wait
        /// this long, then fail the item rather than hold the job forever (audit W7).</summary>
        private const double ForeignDirectorWaitSec = 120;

        // ---- what the window shows ----
        public static string Status { get; private set; } = "idle";
        public static string? LastError { get; private set; }
        public static string? AccountName { get; private set; }
        public static string? CurrentRunId { get; private set; }
        public static int ItemIndex { get; private set; }
        public static int ItemTotal { get; private set; }
        public static DateTime? LastPollUtc { get; private set; }
        public static bool Busy => _routine != null;

        /// <summary>The transport. Swappable so a test can drive the loop against a fake server.</summary>
        public static IStudioHttp Http = new UnityStudioHttp();

        private static IEnumerator? _routine;
        private static double _nextPoll;
        private static readonly string ProjectRoot = ResolveProjectRoot();

        /// <summary>
        /// THE UNITY PROJECT FOLDER — the one this Editor actually has open.
        ///
        /// It used to be <c>Directory.GetCurrentDirectory()</c>, which is *usually* the same thing
        /// and is not the same PROMISE: the working directory is process-wide state any script in
        /// the editor can change, and since 2026-09-21 a RECURSIVE DELETE hangs off this value
        /// (<see cref="ExportOnDisk.SweepOrphans"/>). It now lives in <see cref="KitProject"/>,
        /// because the second audit found the same question answered two different ways in the same
        /// process: this agent WRITES the cloud's files under the Editor's project while the lever
        /// gate, the Nova Capture window and the default adapter READ them from the working
        /// directory (audit M4).
        /// </summary>
        internal static string ResolveProjectRoot() => KitProject.Root();

        static NovaCaptureAgent()
        {
            if (Application.isBatchMode)
                return;
            // A progress file means a job was mid-flight when the domain went down: resume at once.
            _nextPoll = File.Exists(CapturePaths.ProgressFile(ProjectRoot)) ? 0 : EditorApplication.timeSinceStartup + 2;
            EditorApplication.update += Tick;
        }

        // ---- settings: EditorPrefs, keyed per project, never a file in the studio's repo ----
        private static string Key(string name) => "NovaCapture." + name + "." + ProjectKey;
        private static readonly string ProjectKey = ProjectKeyOf(ProjectRoot);

        /// <summary>
        /// The per-project suffix on every EditorPrefs key. IT IS A HASH OF THE PATH STRING, and
        /// that is a real limitation, not a detail: open the same project through a differently
        /// spelled path — a symlinked checkout, `/tmp` versus `/private/tmp`, a renamed parent
        /// folder — and the key changes, so the BaseUrl, the studio key, `Enabled` and the
        /// WORKSPACE BINDING all read back empty and have to be entered again. Nothing is lost
        /// from disk; the settings are simply keyed to the other spelling. Moving the source of
        /// this value from `Directory.GetCurrentDirectory()` to `Application.dataPath`'s parent
        /// (2026-09-21) can do the same thing once, on upgrade, if those two differ as strings on
        /// a given machine — which has not been observed here and has not been ruled out.
        /// </summary>
        internal static string ProjectKeyOf(string projectRoot)
        {
            using var sha = System.Security.Cryptography.SHA1.Create();
            var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(projectRoot));
            return BitConverter.ToString(bytes, 0, 6).Replace("-", "").ToLowerInvariant();
        }

        public static string BaseUrl
        {
            get => EditorPrefs.GetString(Key("BaseUrl"), "");
            set => EditorPrefs.SetString(Key("BaseUrl"), value.Trim());
        }

        public static string StudioKey
        {
            get => EditorPrefs.GetString(Key("Key"), "");
            set => EditorPrefs.SetString(Key("Key"), value.Trim());
        }

        public static bool Enabled
        {
            get => EditorPrefs.GetBool(Key("Enabled"), false);
            set
            {
                EditorPrefs.SetBool(Key("Enabled"), value);
                if (value) _nextPoll = 0;
            }
        }

        public static bool Configured => !string.IsNullOrEmpty(BaseUrl) && !string.IsNullOrEmpty(StudioKey);

        // ---- slice D part 1 (owner decision D10): which workspace THIS Unity project is ----
        //
        // The studio key is account-scoped; a studio with two games has two workspaces and, often,
        // two Unity projects sharing one key. Before a game is onboarded there is no
        // Library/Nova/adapter.json, so nothing on this machine says which project is which game —
        // and the first job a new game gets (an export) is exactly the one that must not be
        // answered by the wrong project. So the studio says it, once per project, here. Stored in
        // EditorPrefs under the PROJECT key like the URL and the studio key: never a file in the
        // studio's repo, and never shared between two projects on one machine.

        /// <summary>The workspace this Unity project is bound to, or "" when the studio has not
        /// picked one. Sent on every studio call as <c>X-Nova-Workspace</c>.</summary>
        public static string BoundWorkspaceId
        {
            get => EditorPrefs.GetString(Key("WorkspaceId"), "");
            private set => EditorPrefs.SetString(Key("WorkspaceId"), (value ?? "").Trim());
        }

        /// <summary>The bound workspace's display name as it was when picked — so the window can
        /// say what this project is bound to before (or without) a Connect.</summary>
        public static string BoundWorkspaceName
        {
            get => EditorPrefs.GetString(Key("WorkspaceName"), "");
            private set => EditorPrefs.SetString(Key("WorkspaceName"), (value ?? "").Trim());
        }

        /// <summary>The account's workspaces, as of the last Connect. Empty until then.</summary>
        public static IReadOnlyList<StudioWorkspace> Workspaces { get; private set; } = new List<StudioWorkspace>();

        /// <summary>Why the workspace list could not be read, or null. NOT a connect failure: an
        /// API older than this kit has no such route, and capture still works unbound.</summary>
        public static string? WorkspacesError { get; private set; }

        /// <summary>Bind this Unity project to a workspace (null = unbind). An explicit act, never
        /// inferred: even a single-workspace account is not auto-bound, because "this project is
        /// that game" is the statement the binding exists to record, and two projects can share
        /// the one workspace's key.</summary>
        public static void BindWorkspace(StudioWorkspace? workspace)
        {
            BoundWorkspaceId = workspace?.Id ?? "";
            BoundWorkspaceName = workspace?.Label ?? "";
            _nextPoll = 0; // the jobs this project may see just changed
        }

        public static void ForgetKey()
        {
            EditorPrefs.DeleteKey(Key("Key"));
            AccountName = null;
            // A binding names a workspace of the account the KEY belonged to. A different key may
            // be a different account, where that id matches nothing — so it goes with the key.
            EditorPrefs.DeleteKey(Key("WorkspaceId"));
            EditorPrefs.DeleteKey(Key("WorkspaceName"));
            Workspaces = new List<StudioWorkspace>();
            WorkspacesError = null;
        }

        /// <summary>Poll on the next tick instead of waiting out the interval.</summary>
        public static void PollNow() => _nextPoll = 0;

        /// <summary>"Connect": GET /studio/me — proves the key and names the account.</summary>
        public static void Connect()
        {
            if (_routine != null) return;
            _routine = ConnectRoutine();
        }

        // ---- the pump ----
        private static void Tick()
        {
            if (_routine != null)
            {
                try
                {
                    if (!_routine.MoveNext())
                        EndRoutine();
                }
                catch (Exception e)
                {
                    Fail("agent aborted: " + e.Message);
                    Debug.LogError("[RecorderKit] Nova Capture agent aborted: " + e);
                    EndRoutine();
                    // The job's progress file stays (the next poll resumes it), but a pin this
                    // agent set must not outlive the job in the studio's editor (audit W4).
                    var aborted = CaptureProgress.Load(CapturePaths.ProgressFile(ProjectRoot));
                    ReleaseStartScenePin(aborted);
                    // RE-AUDIT of M7: the counter belongs HERE, not around one HTTP call. The
                    // earlier fix bounded the result POST, but every OTHER throw — a locked PNG in
                    // `PostMultipart`'s `File.ReadAllBytes`, a null `ReadyGate`, anything a custom
                    // adapter does — landed in this catch, which kept the progress file and
                    // retried in 15 s, forever, with the run reading "Running in your editor…" on
                    // the web. This catch sees them all, so counting here bounds the whole class.
                    NoteAbortedAttempt(aborted);
                }
                return;
            }
            if (!Enabled || !Configured)
                return;
            if (EditorApplication.timeSinceStartup < _nextPoll)
                return;
            _nextPoll = EditorApplication.timeSinceStartup + PollIntervalSec;
            _routine = PollRoutine();
        }

        private static void Fail(string message)
        {
            LastError = message;
            Status = "error";
        }

        /// <summary>
        /// "Will retry on the next poll" has to MEAN the next poll.
        ///
        /// <see cref="_nextPoll"/> is set when a routine STARTS, so by the time a long one — an
        /// export build, a part upload — reports a transient failure it is already in the past and
        /// the retry fires on the very next editor tick. A momentary network blip then burned the
        /// whole three-attempt budget in milliseconds and ended the job (fresh-context audit K3,
        /// 2026-09-21). Every "will retry" path goes through here.
        /// </summary>
        private static void FailAndBackOff(string message)
        {
            _nextPoll = EditorApplication.timeSinceStartup + PollIntervalSec;
            Fail(message);
        }

        /// <summary>
        /// The routine is over — dispose it, always.
        ///
        /// A compiler-generated iterator runs its `finally` blocks only when it is DISPOSED, and
        /// this chain (poll → job → export → collector) is where the export's open zip part is
        /// closed and its scratch removed. Dropping the reference alone left the part's handle open
        /// with `FileShare.None`, which on Windows makes the build directory undeletable.
        /// </summary>
        private static void EndRoutine()
        {
            var routine = _routine;
            _routine = null;
            try { (routine as IDisposable)?.Dispose(); }
            catch (Exception e) { Debug.LogWarning("[RecorderKit] Nova Capture: routine cleanup: " + e.Message); }
        }

        /// <summary>
        /// Drop this job's progress — AND its export build.
        ///
        /// An export build is up to a gigabyte under `Library/AdRelay/`. It used to be removed only
        /// when the server accepted `done`, so every other ending (the owner cancels, the `done`
        /// answer is lost, the lease is taken over) left it on the studio's disk for good
        /// (fresh-context audit K2, 2026-09-21). The progress file is what says a job is in flight,
        /// so the two go together.
        /// </summary>
        private static void DropProgress(string progressFile)
        {
            try { File.Delete(progressFile); }
            catch (Exception e) { Debug.LogWarning("[RecorderKit] Nova Capture: progress file not removed: " + e.Message); }
            ExportOnDisk.TryDeleteDir(CapturePaths.ExportRoot(ProjectRoot));
        }

        private static IEnumerator ConnectRoutine()
        {
            Status = "connecting";
            LastError = null;
            using var res = Http.Get(StudioEndpoints.Me(BaseUrl), StudioKey);
            while (!res.IsDone) yield return null;
            if (!Ok(res, out var why))
            {
                AccountName = null;
                Fail("connect failed: " + why);
                yield break;
            }
            var connected = false;
            try
            {
                var o = JObject.Parse(res.Body);
                AccountName = o["accountName"]?.Value<string>() ?? o["accountId"]?.Value<string>();
                Status = "connected";
                connected = true;
            }
            catch (Exception e)
            {
                Fail("connect: unreadable reply: " + e.Message);
            }
            if (!connected) yield break;

            // Slice D part 1 (D10): the account's workspaces, for the picker. A failure here does
            // NOT fail the connect — the key is proven, and an API that predates this route (404)
            // still serves capture to an unbound project exactly as before.
            using var wsRes = Http.Get(StudioEndpoints.Workspaces(BaseUrl), StudioKey);
            while (!wsRes.IsDone) yield return null;
            if (!Ok(wsRes, out var wsWhy))
            {
                Workspaces = new List<StudioWorkspace>();
                WorkspacesError = wsRes.Status == 404
                    ? "this server does not list workspaces yet — capture works without picking one"
                    : "could not read the workspaces: " + wsWhy;
                yield break;
            }
            Workspaces = StudioWorkspace.ParseList(wsRes.Body, out var wsParse);
            WorkspacesError = wsParse;
        }

        private static IEnumerator PollRoutine()
        {
            LastPollUtc = DateTime.UtcNow;
            var progressFile = CapturePaths.ProgressFile(ProjectRoot);
            var progress = CaptureProgress.Load(progressFile);
            if (progress == null)
            {
                // THE ORPHAN SWEEP (K2). One job runs at a time and the progress file is what says
                // one is in flight — so with none, nothing under `export/` belongs to anybody and a
                // build left by a cancelled or lost run goes before we ask for more work. The check
                // is on the FILE, not on `progress`: an unreadable progress file still means a job.
                if (ExportOnDisk.SweepOrphans(CapturePaths.ExportRoot(ProjectRoot), File.Exists(progressFile)))
                    Debug.Log("[RecorderKit] Nova Capture: removed an export build left behind by a run that never finished");
                // …and so does every try's frame (second audit M6) — unless a progress file that
                // could not be read still names a job, whose frame this cannot tell apart.
                if (!File.Exists(progressFile)) SweepStrayProbeFrames(null);

                Status = "polling";
                using var res = Http.Get(StudioEndpoints.Jobs(BaseUrl), StudioKey);
                while (!res.IsDone) yield return null;
                if (!Ok(res, out var why))
                {
                    Fail("poll failed: " + why);
                    yield break;
                }
                var jobs = CaptureJob.ParseList(res.Body, out var parseError);
                if (parseError != null)
                {
                    Fail("poll: " + parseError);
                    yield break;
                }
                if (jobs.Count == 0)
                {
                    Status = "no jobs";
                    LastError = null;
                    yield break;
                }
                // Claim BEFORE writing progress: a claim the server refuses is not our job.
                var first = jobs[0];
                using var claim = Http.PostJson(StudioEndpoints.Claim(BaseUrl, first.RunId), StudioKey, "{}");
                while (!claim.IsDone) yield return null;
                if (!Ok(claim, out var claimWhy))
                {
                    // 409 = another editor holds it; try again next poll. Anything else is an error.
                    if (claim.Status == 409) { Status = "job held elsewhere"; yield break; }
                    Fail("claim failed: " + claimWhy);
                    yield break;
                }
                var job = CaptureJob.ParseClaim(claim.Body, out var claimParse, out var claimFiles,
                    out var claimProbe, out var claimGate);
                if (job == null)
                {
                    Fail("claim: " + claimParse);
                    yield break;
                }
                RememberSyncNovaFiles(job.RunId, claimFiles);
                // The lease was taken just now — RefreshLeaseIfDue's clock starts here, not at 0.
                _leaseRefreshedAt = EditorApplication.timeSinceStartup;
                // Slice E.1: a probe's ONE shot goes onto the progress file with the job, BEFORE
                // anything can reload the domain — it enters Play Mode, and the agent that comes
                // back runs the shot from THIS copy (the resume branch below keeps it; it does not
                // re-read it from the re-claim).
                // Slice E.4, part 2: …and so does a recording's tutorial gate, for the same reason.
                progress = CaptureProgress.Start(job, claimProbe, claimGate);
                progress.Save(progressFile);
            }
            else
            {
                // Resuming after a reload: refresh the lease so the server knows this editor is alive.
                using var claim = Http.PostJson(StudioEndpoints.Claim(BaseUrl, progress.RunId), StudioKey, "{}");
                while (!claim.IsDone) yield return null;
                if (!Ok(claim, out var claimWhy))
                {
                    // ONLY AN ANSWER ENDS A JOB (K3). A transport failure or a 5xx is nobody
                    // answering: the job is still ours, and dropping it here was how a single
                    // network blip silently abandoned a run — leaving the web reading "running in
                    // your editor" until the 30-minute lease expired, and for an export throwing
                    // away a build that cost minutes. Same function the lease refresher uses.
                    if (!JobGuard.ClaimRefusalEndsJob(claim.Status, claim.TransportError))
                    {
                        FailAndBackOff($"could not refresh the lease on run {progress.RunId} ({claimWhy})" +
                                       " — the job is kept and will retry on the next poll");
                        yield break;
                    }
                    // The run finished, was taken over after our lease lapsed, or is gone: drop it.
                    ReleaseStartScenePin(progress);
                    DropProgress(progressFile);
                    Fail($"run {progress.RunId} is no longer ours ({claimWhy}); dropped its progress file and any export build");
                    yield break;
                }
                // Slice A2′ — the two files a `sync-nova` writes ride the CLAIM, and this branch IS
                // a claim. A domain reload (a script saved while the job was in flight) wipes every
                // static including the ones below, so the resume has to read them out of THIS reply
                // rather than trust what an earlier body left in memory. Parsed for every kind; only
                // a sync-nova ever carries them.
                CaptureJob.ParseClaim(claim.Body, out _, out var resumedFiles);
                RememberSyncNovaFiles(progress.RunId, resumedFiles);
                // Second audit M6: the run in flight keeps its frame (a retried POST reads it).
                SweepStrayProbeFrames(progress.RunId);
                // The lease was refreshed just now — and a domain reload reset the clock to 0, so a
                // try's first RefreshLeaseIfDue would otherwise re-claim at once, for nothing.
                _leaseRefreshedAt = EditorApplication.timeSinceStartup;
            }

            foreach (var _ in RunJob(progress, progressFile)) yield return _;
        }

        private static IEnumerable RunJob(CaptureProgress progress, string progressFile)
        {
            CurrentRunId = progress.RunId;
            ItemTotal = progress.Items.Count;
            LastError = null;

            // Refuse a kind this kit does not handle BEFORE booting the game (no Play Mode, no
            // adapter needed) — otherwise an ahead-of-us API's kind boots, waits ~90s for an
            // adapter, then gets misdiagnosed as "no adapter". Same pure JobGuard, kind-only: null
            // gameIds skip the game check, which needs the adapter and runs post-boot below.
            var kindRefusal = JobGuard.RefusalReason(progress.Kind, null, null);
            if (kindRefusal != null)
            {
                progress.RecordJobFailure(kindRefusal);
                progress.Save(progressFile);
                foreach (var _ in ReportDone(progress, progressFile)) yield return _;
                yield break;
            }

            // Slice D part 1 (D10): a BOUND project refuses a job for another workspace — every
            // kind, before anything boots. The API already withheld it (poll) and refused it
            // (claim); this is the receiver's own half, against its own state (invariant 100).
            var workspaceRefusal = WorkspaceGuard.Refusal(progress.WorkspaceId, BoundWorkspaceId);
            if (workspaceRefusal != null)
            {
                progress.RecordJobFailure(workspaceRefusal);
                progress.Save(progressFile);
                foreach (var _ in ReportDone(progress, progressFile)) yield return _;
                yield break;
            }

            // Slice D part 1 — an EXPORT parts ways HERE, before the play-mode preamble. It reads
            // AssetDatabase, build settings and player settings: Edit Mode work. Falling through
            // would press Play on the studio's editor, wait 90 s for a game adapter, and — on the
            // not-yet-onboarded project this job exists for — refuse "no GameAdapter registered".
            if (progress.Kind == CaptureJob.ExportKind)
            {
                foreach (var _ in RunExport(progress, progressFile)) yield return _;
                yield break;
            }

            // Slice A2′ — a SYNC-NOVA parts ways here for the same reason: it writes two files and
            // reads them back, which is Edit Mode work. Falling through would press Play on the
            // studio's editor and wait 90 s for an adapter this job has no use for — and on the
            // fresh project this job exists for, refuse "no GameAdapter registered".
            if (progress.Kind == CaptureJob.SyncNovaKind)
            {
                foreach (var _ in RunSyncNova(progress, progressFile)) yield return _;
                yield break;
            }

            // Slice E.1 — a PROBE that can never run is refused HERE, before Play Mode: no script on
            // the claim, a sha that does not match, a shot the loader refuses. Pressing Play on the
            // studio's editor and waiting 90 s for an adapter would be work for a job that tries
            // nothing. (Only the WHOLE-JOB refusals: an un-ticked lever is a result with facts, and
            // RunProbe reports it once the game is up.) And one that owes only `done` goes straight
            // there.
            if (progress.Kind == CaptureJob.ProbeKind)
            {
                if (!progress.Done && progress.ProbeFactsJson == null)
                {
                    var early = ProbePlan.Prepare(ProjectRoot, progress.Probe).Refusal;
                    if (early != null)
                    {
                        progress.RecordJobFailure(early);
                        progress.Save(progressFile);
                    }
                }
                if (progress.Done)
                {
                    foreach (var _ in ReportDone(progress, progressFile)) yield return _;
                    yield break;
                }
                // Facts already gathered — a result POST that failed is being retried. That needs
                // no game: post again, never run the shot again (CaptureProgress.ProbeFactsJson).
                if (progress.ProbeFactsJson != null)
                {
                    foreach (var _ in PostProbeResult(progress, progressFile)) yield return _;
                    yield break;
                }
            }

            // Slice E.4, part 2 — a RECORDING whose tutorial gate cannot run (it did not arrive whole, is not
            // the text its sha says, is not the `tutorial-gate` shot, or does not load) is refused HERE,
            // before Play Mode: every take would run it first. No gate on the claim: nothing is asked.
            if (progress.Kind == CaptureJob.CaptureKind && progress.TutorialGate != null && !progress.Done
                && CaptureGatePlan.Prepare(progress.TutorialGate)!.Refusal is { } gateRefusal)
            {
                progress.RecordJobFailure(gateRefusal);
                progress.Save(progressFile);
                foreach (var _ in ReportDone(progress, progressFile)) yield return _;
                yield break;
            }

            if (!EditorApplication.isPlaying)
            {
                // Play Mode starts from whatever scene the editor happens to have open — on the
                // first live run that was an EMPTY scene, so the game never booted and the shot
                // failed. Unless the project already pins a play-mode start scene, pin the first
                // enabled build-settings scene (the game's boot scene by Unity convention) for this
                // job and clear it again when the job ends.
                if (EnsurePlayModeStartScene(out var pinned))
                {
                    progress.PlayModeStartSceneSet = true;
                    progress.Save(progressFile);
                    Debug.Log($"[RecorderKit] Nova Capture: Play Mode will start from {pinned}");
                }
                // Progress is already on disk. Entering Play Mode reloads the domain (by default) and
                // this static class is rebuilt; its constructor sees the file and resumes at once.
                Status = "entering Play Mode";
                _nextPoll = 0;
                EditorApplication.isPlaying = true;
                yield break;
            }

            var deadline = EditorApplication.timeSinceStartup + AdapterWaitSec;
            while (!AdapterRegistry.IsRegistered && EditorApplication.timeSinceStartup < deadline)
            {
                Status = "waiting for the game adapter";
                yield return null;
            }
            if (!AdapterRegistry.IsRegistered)
            {
                // Whole-job failure, not per-item: a job with no items (a self-test kind) would
                // otherwise report 0 captured / 0 failed here and read `completed` — but "no adapter
                // in Play Mode" is exactly the failure such a job exists to catch.
                progress.RecordJobFailure("no GameAdapter registered in Play Mode");
                progress.Save(progressFile);
                foreach (var _ in ReportDone(progress, progressFile)) yield return _;
                yield break;
            }

            var adapter = AdapterRegistry.Current;

            // Slice B/C (invariant 100): admit the job or refuse it whole. The RECEIVER checks the
            // SENDER's stamp against its OWN identity — and its own identity is the
            // onboarding-authored Library/Nova/adapter.json gameId, NOT a registered custom
            // adapter's GameId (SnlAdapter hardcodes "snl" while the pack + adapter.json are
            // "snl-scratch"; comparing the adapter field refused every SNL job). The registered
            // adapter's GameId is only a fallback for a project that authored no adapter.json. The
            // decision is a pure function (JobGuard) so the kit's EditMode tests cover the exact
            // comparison that runs here — the seam an untested inline check let regress.
            var editorGameId = JobGuard.EditorGameId(
                DefaultAdapterBoot.ReadGameId(ProjectRoot), adapter.GameId);
            var refusal = JobGuard.RefusalReason(progress.Kind, progress.GameId, editorGameId);
            if (refusal != null)
            {
                progress.RecordJobFailure(refusal);
                progress.Save(progressFile);
                foreach (var _ in ReportDone(progress, progressFile)) yield return _;
                yield break;
            }

            // Slice B/C commit 2 — dispatch on the kind. Everything above this point is shared by
            // every kind that needs a running game: the play-mode entry, the boot-scene pin and the
            // adapter wait. Below it the kinds part ways.
            if (progress.Kind == CaptureJob.SelfTestKind)
            {
                foreach (var _ in RunSelfTest(progress, progressFile, adapter)) yield return _;
                yield break;
            }

            // Slice E.1 — "Try this shot". Here, after the wrong-game check on purpose: a probe
            // runs a script in the studio's game, so it is admitted by the capture's identity rule
            // (JobGuard.RefusalReason does not exempt it).
            if (progress.Kind == CaptureJob.ProbeKind)
            {
                foreach (var _ in RunProbe(progress, progressFile, adapter)) yield return _;
                yield break;
            }

            var recorder = RecorderDrivers.For(adapter.Recorder);
            var outputDir = Path.Combine(ProjectRoot, recorder.OutputDir);
            // Slice E.4, part 2 — the tutorial gate the CLAIM carried, read once (pure: after the reload it
            // is the plan it was before). Null = none: every take below is today's.
            var gatePlan = CaptureGatePlan.Prepare(progress.TutorialGate);
            if (gatePlan?.Refusal != null)
            {
                progress.RecordJobFailure(gatePlan.Refusal);
                progress.Save(progressFile);
                foreach (var _ in ReportDone(progress, progressFile)) yield return _;
                yield break;
            }

            while (!progress.Done)
            {
                if (!Enabled)
                {
                    // Unticked mid-job: stop at the item boundary and KEEP the progress file — the
                    // job resumes when re-ticked, or another editor takes it once the lease lapses.
                    Status = $"paused — capture jobs are off ({progress.NextIndex}/{ItemTotal} done, progress kept)";
                    yield break;
                }
                var item = progress.Current!;
                ItemIndex = progress.NextIndex + 1;
                Status = $"recording {item.Shot} take {item.Take} ({ItemIndex}/{ItemTotal})";

                var shot = adapter.Shots().FirstOrDefault(s => s.Name == item.Shot);
                if (shot == null)
                {
                    foreach (var _ in FailItem(progress, progressFile, "unknown shot in this project's shots.json")) yield return _;
                    continue;
                }

                // Slice E.4, part 2 — a lever the gated take needs, not ticked here NOW: the take is refused
                // before anything runs (no director, no ready gate, not the gate's setup). The second audit of
                // E.4, M1: the tries' union — the gate's, the shot's and the adapter's (CaptureRun.RefusedBeforeTake).
                if (CaptureRun.RefusedBeforeTake(gatePlan, ProjectRoot, shot) is { } leverRefusal)
                {
                    // Through FailItem, which hands the editor back once (A2′'s thirteenth audit, M1 — met at stack pass 3's
                    // E.3 → E.4 merge): this refusal comes before any wait that yields, so a job of K takes refused by one
                    // un-ticked lever ran K shots.json loads, K lever reads and K progress writes inside one editor tick.
                    foreach (var _ in FailItem(progress, progressFile, leverRefusal)) yield return _;
                    continue;
                }

                var directorDeadline = EditorApplication.timeSinceStartup + ForeignDirectorWaitSec;
                while (AdDirector.Active != null && EditorApplication.timeSinceStartup < directorDeadline)
                {
                    Status = $"waiting for another director to finish before {item.Shot}";
                    yield return null;
                }
                if (AdDirector.Active != null)
                {
                    progress.RecordFailed("another director stayed active for " + ForeignDirectorWaitSec + " s");
                    progress.Save(progressFile);
                    continue;
                }

                // Wait for the screen the shot is about (see ShotReadyWaitSec) — slice E.4, part 2: the screen
                // the take STARTS on. The second audit of E.4, M2: with a tutorial gate, its screen OR the
                // shot's (CaptureRun.StartsOn) — behind a tutorial the shot's own screen is not up until the gate
                // has run, and once the tutorial has gone the gate's screen never comes back.
                var ready = CaptureRun.StartsOn(shot, gatePlan?.Gate);
                var ui = adapter.Ui ?? new UguiDriver();
                var readyDeadline = EditorApplication.timeSinceStartup + ShotReadyWaitSec;
                while (!ready.IsMet(ui, adapter.StateProbe) && EditorApplication.timeSinceStartup < readyDeadline)
                {
                    Status = $"waiting for {ready.Describe()} before {item.Shot} ({ItemIndex}/{ItemTotal})";
                    yield return null;
                }

                var startedAt = DateTime.UtcNow.AddSeconds(-1);
                // Slice E.4, part 2: NO GATE = TODAY — `AdDirector.Run(adapter, new[] { shot }, null)`, the call
                // this always was; with a gate, it runs first (CaptureRun.OptionsFor). Null here is another
                // director (retry) — or a lever of the take un-ticked since the check above, which the next
                // pass of this item refuses by name.
                var director = CaptureRun.Start(adapter, shot, gatePlan, ProjectRoot);
                if (director == null)
                {
                    yield return null; // someone else started one in the same tick; retry
                    continue;
                }
                while (!director.IsFinished) yield return null;

                // A failed take is recorded and never looked for on disk — a stop at the tutorial gate in the
                // one sentence (the recorder never started: no clip, nothing to upload or quarantine), every
                // other failure as it always was (CaptureRun.TakeFailure).
                if (CaptureRun.TakeFailure(director) is { } takeFailure)
                {
                    progress.RecordFailed(takeFailure);
                    progress.Save(progressFile);
                    continue;
                }

                var settleUntil = EditorApplication.timeSinceStartup + FileSettleSec;
                while (EditorApplication.timeSinceStartup < settleUntil) yield return null;
                var clip = ClipFinder.Newest(outputDir, shot.Name, startedAt);
                if (clip == null)
                {
                    progress.RecordFailed($"no take on disk under {recorder.OutputDir}/");
                    progress.Save(progressFile);
                    continue;
                }

                Status = $"uploading {Path.GetFileName(clip)} ({ItemIndex}/{ItemTotal})";
                var fields = new Dictionary<string, string>
                {
                    ["shot"] = item.Shot,
                    ["take"] = item.Take.ToString(),
                };
                // Step 4 (2026-09-08): the director's step times ride beside the take — on this
                // machine as `<clip>.steps.json` (a laptop compile reads it there) and to the box
                // as the `steps` field (the API lands it beside the take it keeps). A sidecar that
                // cannot be written is logged, never a failed take.
                if (director.StepMarks.Count > 0)
                {
                    var stepsJson = StepMark.ToJson(item.Shot, director.StepMarks);
                    fields["steps"] = stepsJson;
                    try
                    {
                        File.WriteAllText(Path.ChangeExtension(clip, ".steps.json"), stepsJson);
                    }
                    catch (IOException e)
                    {
                        UnityEngine.Debug.LogWarning($"[nova] steps sidecar not written beside {clip}: {e.Message}");
                    }
                }
                using (var up = Http.PostFile(StudioEndpoints.Clips(BaseUrl, progress.RunId), StudioKey, clip, "file", fields))
                {
                    while (!up.IsDone) yield return null;
                    if (Ok(up, out var upWhy))
                        progress.RecordCaptured();
                    else
                        progress.RecordFailed("upload failed: " + upWhy);
                }
                progress.Save(progressFile);
            }

            // Slice B: a malformed shots.json yields an empty shot list (loading never throws), so
            // every item failed "unknown shot"; the parse reason itself rides here so the run's
            // progress says WHY, not just that shots were unknown. LastErrors reflects the most
            // recent Shots() call, which the item loop above made.
            foreach (var _ in ReportDone(progress, progressFile, JsonShotLoader.LastErrors)) yield return _;
        }

        /// <summary>How long to wait for ScreenCapture to actually write the PNG. It completes at
        /// the end of a frame, not on the call, and a Play Mode that is compiling or paused can
        /// take several. Timing out is a FACT the result reports, never a thrown job.</summary>
        private const double ScreenshotWaitSec = 10;

        /// <summary>AUDIT M3. How long the Doctor waits for the game to DRAW before photographing
        /// it. The adapter wait above only proves an adapter is REGISTERED, and the generic one
        /// registers synchronously at domain load — so without this the screenshot fires within a
        /// round trip of pressing Play and the frame the studio is told to recognise is the splash
        /// screen. Exceeding this is a FACT the result reports, never a failure.</summary>
        private const double SelfTestDrawWaitSec = 30;

        /// <summary>How many rendered frames count as "this game is drawing". Frames, not seconds:
        /// a game that is hung renders none however long you wait, and that difference is the
        /// whole point of measuring it.</summary>
        private const int SelfTestMinFrames = 30;

        /// <summary>A floor under the wait even once frames are flowing — the first frames of a
        /// scene are a splash or a fade-in, and the studio is about to be asked "is this your
        /// game?". Also the entire wait for a game that draws instantly.</summary>
        private const double SelfTestSettleSec = 4;

        /// <summary>Spec §8.10 (kit 0.9.1). A game still on its BOOT scene after the settle is given
        /// up to this long to leave it before the picture is taken and "stuck" is decided. Rogue
        /// Legend logs in from `Loading.unity` and moves on after ~10–20 s; 0.9.0 took its photo at
        /// ~4 s and told the owner his healthy game "stayed on its loading screen" (2026-09-23, the
        /// first real run of the check — invariant 40 rule 1: a gate must be shown on the REAL artifact).
        /// It also makes the frame the studio is asked to recognise the GAME, not the loader.</summary>
        private const double SelfTestLeaveBootWaitSec = 45;

        /// <summary>Kit 0.9.2 — once the game has left its boot scene, the scene it went to is given
        /// this long before the photo (its own loader, a fade-in, a popup animating in).</summary>
        private const double SelfTestNewSceneSettleSec = 8;

        /// <summary>AUDIT M7. How many times the result POST may fail before the job is refused
        /// whole and REPORTED. Retrying forever left the studio watching "Running in your editor…"
        /// with no end — the one outcome a diagnostic must never produce.</summary>
        private const int MaxResultAttempts = 3;


        // ---- spec §8.9: progress while an export builds and uploads -------------------------------
        //
        // BEST EFFORT, never a failure: at most one request in flight and one every
        // ProgressPostEverySec; an API that does not know the route (404/405 — an older site) stops
        // the posting for the rest of this export. The site shows "Your editor is working…" without it.
        internal const double ProgressPostEverySec = 5.0;
        private static IStudioResponse? _progressReq;
        private static double _progressNextAt;
        private static bool _progressUnsupported;

        internal static void ResetProgressPost()
        {
            try { _progressReq?.Dispose(); } catch (Exception) { }
            _progressReq = null;
            _progressNextAt = 0;
            _progressUnsupported = false;
        }

        internal static JObject ProgressBody(string phase, float pct01, int partsBuilt, long bytesBuilt, int partsUploaded, int? partsTotal)
        {
            var o = new JObject
            {
                ["phase"] = phase.Length > 40 ? phase.Substring(0, 40) : phase,
                ["pct"] = Mathf.RoundToInt(Mathf.Clamp01(pct01) * 100),
                ["partsBuilt"] = Math.Max(0, partsBuilt),
                ["bytesBuilt"] = Math.Max(0L, bytesBuilt),
                ["partsUploaded"] = Math.Max(0, partsUploaded),
            };
            if (partsTotal.HasValue) o["partsTotal"] = Math.Max(0, partsTotal.Value);
            return o;
        }

        private static long TotalBytes(ExportHeader header)
        {
            long n = 0;
            foreach (var p in header.Parts) n += p.Bytes;
            return n;
        }

        private static void MaybePostProgress(string runId, JObject body)
        {
            if (_progressUnsupported) return;
            if (_progressReq != null)
            {
                if (!_progressReq.IsDone) return;
                var st = _progressReq.Status;
                try { _progressReq.Dispose(); } catch (Exception) { }
                _progressReq = null;
                if (st == 404 || st == 405) { _progressUnsupported = true; return; }
            }
            var now = EditorApplication.timeSinceStartup;
            if (now < _progressNextAt) return;
            _progressNextAt = now + ProgressPostEverySec;
            try { _progressReq = Http.PostJson(StudioEndpoints.Progress(BaseUrl, runId), StudioKey, body.ToString(Newtonsoft.Json.Formatting.None)); }
            catch (Exception) { _progressReq = null; }
        }

        /// <summary>
        /// Slice B/C commit 2 — the Phase 0 Doctor, run from the web.
        ///
        /// The game is already playing and its adapter is registered (the shared preamble in
        /// <see cref="RunJob"/> did both). This measures what this editor IS, takes ONE real frame,
        /// and posts both. It records nothing and it judges nothing: every reading goes up as a
        /// fact and the web decides what they mean (evidence plan 3B).
        ///
        /// THE FRAME IS THE POINT. A fresh project plays whatever scene was last saved, so a green
        /// "the kit answered" over a blank editor is exactly the false pass this job exists to
        /// prevent — the studio must SEE the frame. A frame that could not be taken is reported as
        /// `frameCaptured: false` with the reason, and the facts still go up: "no frame" is the
        /// most important thing this run can say, and dropping the POST would leave the run
        /// `failed` with nothing attached to read.
        /// </summary>
        private static IEnumerable RunSelfTest(CaptureProgress progress, string progressFile,
            GameAdapter adapter)
        {
            // RE-AUDIT (a): guard on `Done`, not on `ResultPosted`. The attempt cap below records a
            // whole-job refusal INSIDE this routine — a refusal the old guard did not recognise. So
            // a refused job whose `done` POST then failed re-entered here and re-ran the entire
            // self-test: another 30 s wait, another frame, a fourth attempt, and a SECOND
            // `RecordJobFailure` with a different string ("after 4 attempts"), which defeats that
            // method's own idempotence and double-counts the failure. The capture loop guards on
            // `Done`; this must too.
            if (progress.Done)
            {
                // Resumed after the reload with nothing left to do: only `done` is owed.
                foreach (var _ in ReportDone(progress, progressFile)) yield return _;
                yield break;
            }

            // AUDIT M3, REDONE after the re-audit. The first fix drove the adapter's
            // `ReadyGate`, and that was wrong twice over:
            //
            //  1. IT WROTE INTO THE STUDIO'S GAME. `HygieneReadyGate.WantsBoard` answers TRUE for
            //     an empty `UpcomingShots`, so a Doctor with no shots took the board path: it ran
            //     the game's `mute` cheat and `RunResist(on: true)` — with no matching off — on a
            //     screen that tells the studio "Records nothing and costs nothing". A diagnostic
            //     that mutates the thing it is diagnosing is not a diagnostic.
            //  2. IT MEASURED THE WRONG THING. That gate is FOOTAGE HYGIENE (mute, resist,
            //     timeScale) for a recording. The Doctor records nothing, and "is the game up" is
            //     not a question it answers — so the fact built on it could not say what it
            //     claimed to say.
            //
            // What the Doctor actually needs is: HAS THIS GAME DRAWN A FRAME YET. That is
            // observable without touching anything — frames since the scene loaded, and how long
            // it has been up — so this waits for real rendered frames plus a settle, and reports
            // both as numbers. Nothing here writes.
            Status = "self-test: waiting for the game to draw";
            var waitStarted = EditorApplication.timeSinceStartup;
            var startFrame = Time.frameCount;
            while (EditorApplication.timeSinceStartup - waitStarted < SelfTestDrawWaitSec
                   && Time.frameCount - startFrame < SelfTestMinFrames)
                yield return null;
            // A floor even once frames are flowing: the first frames of a scene are the splash or
            // a fade-in, and the studio is about to be asked "is this your game?".
            while (EditorApplication.timeSinceStartup - waitStarted < SelfTestSettleSec)
                yield return null;
            // Still on the boot scene? Give it time to leave (spec §8.10, kit 0.9.1). Only when the build
            // HAS another scene: a one-scene game never leaves, and waiting would only slow its check.
            var bootPath = EditorBuildSettings.scenes.FirstOrDefault(sc => sc.enabled)?.path;
            var enabledScenes = EditorBuildSettings.scenes.Count(sc => sc.enabled);
            double? leftBootAfterSec = null;
            if (bootPath != null && enabledScenes > 1)
            {
                var bootWaitStarted = EditorApplication.timeSinceStartup;
                while (string.Equals(UnityEngine.SceneManagement.SceneManager.GetActiveScene().path, bootPath, StringComparison.Ordinal)
                       && EditorApplication.timeSinceStartup - bootWaitStarted < SelfTestLeaveBootWaitSec)
                {
                    Status = $"self-test: waiting for the game to leave {Path.GetFileNameWithoutExtension(bootPath)} ({Mathf.RoundToInt((float)(EditorApplication.timeSinceStartup - bootWaitStarted))}s)";
                    yield return null;
                }
                if (!string.Equals(UnityEngine.SceneManagement.SceneManager.GetActiveScene().path, bootPath, StringComparison.Ordinal))
                {
                    leftBootAfterSec = Math.Round(EditorApplication.timeSinceStartup - waitStarted, 1);
                    // kit 0.9.2: the NEW scene gets its own settle before the photo — 0.9.1 caught RL's
                    // Lobby the instant it opened, still showing its own "Loading…" overlay.
                    var sceneSettleStarted = EditorApplication.timeSinceStartup;
                    while (EditorApplication.timeSinceStartup - sceneSettleStarted < SelfTestNewSceneSettleSec)
                    {
                        Status = "self-test: letting " + Path.GetFileNameWithoutExtension(UnityEngine.SceneManagement.SceneManager.GetActiveScene().path) + " settle";
                        yield return null;
                    }
                }
            }
            var framesDrawn = Time.frameCount - startFrame;
            var readyWaitedSec = EditorApplication.timeSinceStartup - waitStarted;
            // ROUND-3 AUDIT (numeric): this was `Time.timeSinceLevelLoad`, which is WRONG here in
            // two ways. It is scaled by `Time.timeScale` — and the Doctor runs with the editor
            // UNFOCUSED by design (the studio is in the browser), which is exactly when a mobile
            // game pauses itself with `timeScale = 0`. It would then report "240 frames in 4.2s ·
            // scene up 0s": two numbers on one row contradicting each other. It also resets on
            // every scene load, so a boot→menu game reported time since the MENU, not since it
            // came up. `unscaledTime` is the number actually meant: seconds since Play Mode began.
            var playingSec = Time.unscaledTime;
            // …and `timeScale` itself, because 0 is a FACT worth seeing: a game that paused itself
            // is drawing frames and advancing no game time, which looks like a healthy editor and
            // records unusable footage.
            var timeScale = Time.timeScale;

            Status = "self-test: taking a frame";
            // AUDIT m3 — a filename unique to this run, so a frame left behind by an earlier
            // self-test can never be uploaded as this one's. (The previous fixed name meant a
            // failed delete silently re-sent a stale picture, which on a Doctor screen is worse
            // than no picture: it looks like proof.)
            var framePath = Path.Combine(RelayPaths.Root(ProjectRoot),
                "self-test-frame-" + progress.RunId + ".png");
            var frameNote = ClearFrame(framePath);
            // spec §8.10: WHERE the game is when its picture is taken
            var activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().path;
            ScreenCapture.CaptureScreenshot(framePath);
            foreach (var _ in WaitForFrame(framePath)) yield return null;
            var frameCaptured = FrameOnDisk(framePath);
            if (!frameCaptured && frameNote == null)
                frameNote = NoFrameNote;

            // Read the shot list through the adapter, so `shotLoadErrors` reflects the SAME loader
            // a capture job would use — a self-test that read shots a different way could pass over
            // a shots.json the real path cannot parse.
            //
            // AUDIT M7 (completeness): a CUSTOM adapter's `Shots()` is game code, and game code can
            // throw. Uncaught it reached `Tick`'s catch, which keeps the progress file and retries
            // — every 15 s, forever, with the run stuck reading "Running in your editor…". A game's
            // own code failing IS a measurement (3B), so it becomes a fact and the result is still
            // posted: the studio gets the exception text instead of a spinner.
            var shotCount = 0;
            string? factsError = null;
            try
            {
                shotCount = adapter.Shots().Count;
            }
            catch (Exception e)
            {
                factsError = $"this game's adapter threw while listing its shots: {e.Message}";
                Debug.LogWarning("[RecorderKit] self-test: " + factsError);
            }

            string? adapterJsonGameId = null;
            try
            {
                adapterJsonGameId = DefaultAdapterBoot.ReadGameId(ProjectRoot);
            }
            catch (Exception e)
            {
                factsError = (factsError == null ? "" : factsError + " · ")
                             + $"could not read Library/Nova/adapter.json: {e.Message}";
            }
            var git = GitHead.Read(ProjectRoot);
            var facts = SelfTestFacts.Build(
                kitVersion: KitVersion.Current,
                unityVersion: Application.unityVersion,
                // AUDIT M3: what the game actually DID before the photo — frames rendered, how
                // long that took, how long Play Mode has run, and the time scale. Reported, not
                // judged: a blank frame with 0 frames drawn means UNITY IS NOT STEPPING (paused,
                // unfocused with the runInBackground force not taking, compiling) — precise
                // wording, because a script blocking the main thread would block this coroutine
                // too and produce no facts at all. A blank frame with 400 frames drawn is a game
                // running and showing nothing, a completely different fault.
                readyWaitedSec: readyWaitedSec,
                framesDrawn: framesDrawn,
                playingSec: playingSec,
                timeScale: timeScale,
                // Read INSIDE Play Mode: this is the runtime property the kit forces on entry
                // (RelayBoot), not PlayerSettings.runInBackground, which is the BUILD setting.
                runInBackground: Application.runInBackground,
                // The Unity Recorder sub-assembly compiles only when com.unity.recorder is
                // installed and registers itself from [InitializeOnLoad]; a null registration IS
                // the absence of the package.
                unityRecorderPresent: RecorderDrivers.Registered != null,
                recorderDriver: RecorderDrivers.For(adapter.Recorder).GetType().Name,
                // AUDIT m5: the RUNTIME property above is forced true by RelayBoot on Play Mode
                // entry, so its red branch only fires if that force failed. The BUILD setting is
                // the one that bites outside kit-driven play, and it is a different number.
                playerSettingsRunInBackground: PlayerSettings.runInBackground,
                editorGameId: JobGuard.EditorGameId(adapterJsonGameId, adapter.GameId),
                adapterJsonPresent: !string.IsNullOrWhiteSpace(adapterJsonGameId),
                bootScene: EditorBuildSettings.scenes.FirstOrDefault(sc => sc.enabled)?.path,
                shotCount: shotCount,
                shotLoadErrors: JsonShotLoader.LastErrors,
                frameCaptured: frameCaptured,
                frameNote: frameNote,
                factsError: factsError,
                activeScene: string.IsNullOrEmpty(activeScene) ? null : activeScene,
                consoleErrors: PlayConsoleWatch.Errors,
                firstConsoleError: PlayConsoleWatch.First,
                gitBranch: git.branch,
                gitCommit: git.commit,
                enabledSceneCount: enabledScenes,
                leftBootAfterSec: leftBootAfterSec);

            Status = "self-test: reporting";
            var fields = new Dictionary<string, string> { ["facts"] = SelfTestFacts.ToJson(facts) };
            using (var post = Http.PostMultipart(StudioEndpoints.Result(BaseUrl, progress.RunId),
                       StudioKey, fields, frameCaptured ? framePath : null, "file", "image/png"))
            {
                while (!post.IsDone) yield return null;
                if (Ok(post, out var why))
                {
                    progress.ResultPosted = true;
                    // RE-AUDIT minor: the frame is named per run so a stale one can never be sent
                    // as this one's — which means they accumulate in Library/AdRelay/ unless the
                    // one that landed is cleared. The server has it now.
                    DeleteFrame(framePath, "self-test");
                }
                else
                {
                    // AUDIT M7 — retry, but not forever. Retrying alone left the run reading
                    // "Running in your editor…" on the web indefinitely: the studio pressed a
                    // button to find out what was wrong and got a spinner that never resolves.
                    // After the cap the job is refused WHOLE, which reports `done` with the reason,
                    // and the run reads `failed` with something to read.
                    progress.ResultAttempts++;
                    // RE-AUDIT minor: a 4xx is the server saying "not like that", and it will say
                    // it again — retrying costs the studio three more full self-test cycles
                    // (~2-3 minutes of their editor) to reach the same answer. Only a 5xx, a 408 or
                    // 429 ("not now"), or a transport failure is worth another attempt — and it is
                    // JobGuard that owns that rule, so a retry policy cannot drift from a refusal
                    // policy (audit round 3, K3 follow-up).
                    var deterministic = JobGuard.IsDeterministicAnswer(post.Status);
                    if (deterministic || progress.ResultAttempts >= MaxResultAttempts)
                    {
                        progress.RecordJobFailure(deterministic
                            ? $"the server refused the self-test result ({post.Status}): {why}"
                            : $"could not post the self-test result after {progress.ResultAttempts} attempts: {why}");
                        progress.Save(progressFile);
                        foreach (var _ in ReportDone(progress, progressFile)) yield return _;
                        yield break;
                    }
                    progress.Save(progressFile);
                    FailAndBackOff($"self-test result failed (attempt {progress.ResultAttempts}/{MaxResultAttempts}): "
                                   + why + " — will retry on the next poll");
                    yield break;
                }
            }
            progress.Save(progressFile);
            foreach (var _ in ReportDone(progress, progressFile)) yield return _;
        }

        // ---- one frame: shared by the self-test and the probe ----

        /// <summary>The note a result carries when ScreenCapture produced nothing in time.</summary>
        private static string NoFrameNote => $"ScreenCapture wrote no frame within {ScreenshotWaitSec:0}s";

        /// <summary>Remove an earlier frame at this (run-unique) path. Null when there was none or
        /// it went; otherwise why not. Catch-all, not IOException: an UnauthorizedAccessException
        /// escaping here would leave the run stuck reading "running in your editor" forever
        /// (audit M7).</summary>
        private static string? ClearFrame(string framePath)
        {
            try
            {
                if (File.Exists(framePath)) File.Delete(framePath);
                return null;
            }
            catch (Exception e)
            {
                return "could not clear the previous frame: " + e.Message;
            }
        }

        /// <summary>ScreenCapture writes at the end of a frame. Wait for the file AND for its size
        /// to stop changing — a half-written PNG uploads as a broken image, which on a result
        /// screen is indistinguishable from a broken game. Bounded by
        /// <see cref="ScreenshotWaitSec"/>; timing out is a fact the caller reports.</summary>
        private static IEnumerable WaitForFrame(string framePath)
        {
            var deadline = EditorApplication.timeSinceStartup + ScreenshotWaitSec;
            long lastSize = -1;
            var stableFrames = 0;
            while (EditorApplication.timeSinceStartup < deadline)
            {
                long size = 0;
                try { if (File.Exists(framePath)) size = new FileInfo(framePath).Length; }
                catch (IOException) { size = 0; }
                if (size > 0 && size == lastSize) { if (++stableFrames >= 3) break; }
                else stableFrames = 0;
                lastSize = size;
                yield return null;
            }
        }

        private static bool FrameOnDisk(string framePath)
        {
            try { return File.Exists(framePath) && new FileInfo(framePath).Length > 0; }
            catch (IOException) { return false; }
        }

        /// <summary>The server has the frame: clear it, so run-unique frames do not pile up under
        /// Library/AdRelay/. Best-effort.</summary>
        private static void DeleteFrame(string framePath, string what)
        {
            try { if (File.Exists(framePath)) File.Delete(framePath); }
            catch (Exception e)
            {
                Debug.LogWarning($"[RecorderKit] {what} frame not cleaned up: " + e.Message);
            }
        }

        // ---- slice E.1: "Try this shot" ----

        /// <summary>How many times a probe's shot may be handed to the director. A script
        /// recompile (or leaving Play Mode) kills a run with the domain and the agent that comes
        /// back starts it again; past this the job is refused whole rather than re-running the
        /// cloud's shot in the studio's game for as long as they keep saving scripts.</summary>
        private const int MaxProbeRunStarts = 3;

        /// <summary>What a probe saw at the moment its shot stopped. Filled by
        /// <see cref="GatherProbeEvidence"/>, read once the director is finished.</summary>
        private sealed class ProbeEvidence
        {
            public bool Gathered;
            public IReadOnlyList<string> UiNames = Array.Empty<string>();
            public int ScreenWidth;
            public int ScreenHeight;
            public float TimeScale = 1f;
            public string? FrameNote;
            public string? Error;
        }

        private static string ProbeFramePath(string runId) => ProbeRun.FramePath(ProjectRoot, runId);

        /// <summary>Second audit M6: delete every probe frame but the in-flight run's
        /// (<see cref="ProbeRun.SweepStrayFrames"/>), and say so when there was one.</summary>
        private static void SweepStrayProbeFrames(string? keepRunId)
        {
            var gone = ProbeRun.SweepStrayFrames(ProjectRoot, keepRunId);
            if (gone.Count > 0)
                Debug.Log($"[RecorderKit] Nova Capture: removed {gone.Count} try frame(s) left behind by a run that lost its lease");
        }

        /// <summary>
        /// READ THE SCREEN AS IT IS NOW, and ask for the frame. The names come from the SAME walk
        /// the `ui-dump` command prints (<see cref="ReflectionCheatBridge.OnScreenRows"/>) — read
        /// directly, not through the game's cheat bridge, so a custom bridge cannot change them and
        /// no probe file is written. Nothing here writes into the game. Never throws: a reading that
        /// fails becomes <see cref="ProbeEvidence.Error"/>, and the rest is still reported.
        /// </summary>
        private static void GatherProbeEvidence(ProbeEvidence evidence, string framePath)
        {
            evidence.Gathered = true;
            evidence.TimeScale = Time.timeScale;
            // The Game view's RENDER size — what the frame is a picture of — not Screen.width/height,
            // which in the Editor report the window (937x991 against a 1080x1920 view, the case
            // CaptureAspect records). Falls back to Screen.* only when there is no game view.
            var (width, height) = CaptureAspect.GameViewSize();
            evidence.ScreenWidth = width;
            evidence.ScreenHeight = height;
            try
            {
                evidence.UiNames = ReflectionCheatBridge.OnScreenRows();
            }
            catch (Exception e)
            {
                evidence.UiNames = Array.Empty<string>();
                evidence.Error = "could not list the names on screen: " + e.Message;
            }
            evidence.FrameNote = ClearFrame(framePath);
            try
            {
                ScreenCapture.CaptureScreenshot(framePath);
            }
            catch (Exception e)
            {
                evidence.FrameNote = "ScreenCapture refused the frame: " + e.Message;
            }
        }

        /// <summary>The director's stop hook for a probe: read the screen, then HOLD the director
        /// until the frame is on disk, so the frame is of the stop and not of what the director
        /// restores next (<see cref="AdDirector.Options.OnStopped"/>).</summary>
        private static IEnumerable ProbeStopHook(ProbeEvidence evidence, string framePath)
        {
            GatherProbeEvidence(evidence, framePath);
            foreach (var _ in WaitForFrame(framePath)) yield return null;
        }

        /// <summary>
        /// Slice E.1 — "Try this shot". Run the ONE shot the website sent, from memory, with the
        /// recorder OFF, and post what it DID: which step stopped it and the director's own sentence,
        /// the names on screen at that moment, the screen size, the time scale, the director's log
        /// and ONE frame. FACTS, never a verdict (evidence plan 3B).
        ///
        /// It writes no shots.json and no adapter.json (a probe must not change what this machine
        /// records later). It runs only levers this machine has TICKED: first by refusing the whole
        /// shot before a director exists when any lever it needs is not ticked
        /// (<see cref="ProbePlan.LeverRefused"/>), then by running the director with the gate
        /// FORCED ON (<see cref="ProbeRun.DirectorOptions"/>) — because the gate's ordinary answer
        /// comes from the files on disk, and a probe changes none of them.
        ///
        /// Everything it needs after a domain reload is on the progress file: the script
        /// (<see cref="CaptureProgress.Probe"/>), how often it has been started, and — once
        /// gathered — the facts, so a failed POST is retried by posting again, never by running the
        /// shot again.
        /// </summary>
        private static IEnumerable RunProbe(CaptureProgress progress, string progressFile, GameAdapter adapter)
        {
            ItemTotal = 0;
            // The self-test's guard, for the self-test's reason (re-audit (a)): a refusal recorded
            // inside this routine must not re-run the probe when only `done` is owed.
            if (progress.Done)
            {
                foreach (var _ in ReportDone(progress, progressFile)) yield return _;
                yield break;
            }

            var framePath = ProbeFramePath(progress.RunId);
            // Second audit M6: a frame a LOST lease left behind goes now; this run's is kept.
            SweepStrayProbeFrames(progress.RunId);
            if (progress.ProbeFactsJson == null)
            {
                // Re-derived from the persisted script on every entry — pure, so the plan after a
                // reload is the plan before it.
                var plan = ProbePlan.Prepare(ProjectRoot, progress.Probe);
                if (plan.Refusal != null)
                {
                    progress.RecordJobFailure(plan.Refusal);
                    progress.Save(progressFile);
                    foreach (var _ in ReportDone(progress, progressFile)) yield return _;
                    yield break;
                }

                var evidence = new ProbeEvidence();
                AdDirector? director = null;
                if (plan.LeverRefused != null)
                {
                    // NOT RUN AT ALL. No director is built, so not one command of this shot — not
                    // its setup, not a ready gate — reaches the game. What is on screen right now
                    // still goes up: it is where the shot WOULD have started.
                    Status = $"try this shot: not run — '{plan.LeverRefused}' is not ticked on this machine";
                    foreach (var _ in ProbeStopHook(evidence, framePath)) yield return null;
                }
                else
                {
                    if (progress.ProbeRunStarts >= MaxProbeRunStarts)
                    {
                        progress.RecordJobFailure(
                            $"the shot was started {progress.ProbeRunStarts} times and interrupted each time — a script " +
                            "recompile (or leaving Play Mode) stops it. Leave the editor idle and press Try this shot again");
                        progress.Save(progressFile);
                        foreach (var _ in ReportDone(progress, progressFile)) yield return _;
                        yield break;
                    }
                    var shot = plan.Shot!;

                    // Audit M5: every wait below keeps the ten-minute lease, the way the export
                    // does; the moment the run is no longer this editor's, the try stops — and a
                    // director that is running is finished (restoring what it changed) first.
                    var leaseLost = false;
                    IEnumerable Refresh() => RefreshLeaseIfDue(progress, progressFile,
                        "the try was stopped and nothing it saw was sent");
                    bool Lost() => !File.Exists(progressFile);
                    // Every other end of a job releases the play-mode start scene this agent pinned
                    // (through ReportDone); a lost lease ends it without `done`, so it does it here.
                    void LeaseLost()
                    {
                        leaseLost = true;
                        ReleaseStartScenePin(progress);
                    }

                    // As for a capture of that shot: wait out a director something else started…
                    var directorDeadline = EditorApplication.timeSinceStartup + ForeignDirectorWaitSec;
                    foreach (var _ in ProbeRun.WhileRefreshing(() =>
                             {
                                 Status = $"try this shot: waiting for another director to finish before {shot.Name}";
                                 return AdDirector.Active != null && EditorApplication.timeSinceStartup < directorDeadline;
                             }, Refresh, Lost, LeaseLost)) yield return _;
                    if (leaseLost) yield break;
                    // …and wait for the screen the try STARTS on (see ShotReadyWaitSec): the shot's — or,
                    // slice E.4, its tutorial gate's, which runs first (behind a tutorial, the shot's
                    // own screen is not up until the gate has run).
                    var first = plan.FirstToRun!;
                    var ready = first.ArmCondition ?? first.Settle;
                    var ui = adapter.Ui ?? new UguiDriver();
                    var readyDeadline = EditorApplication.timeSinceStartup + ShotReadyWaitSec;
                    foreach (var _ in ProbeRun.WhileRefreshing(() =>
                             {
                                 Status = $"try this shot: waiting for {ready.Describe()} before {first.Name}";
                                 return !ready.IsMet(ui, adapter.StateProbe) && EditorApplication.timeSinceStartup < readyDeadline;
                             }, Refresh, Lost, LeaseLost)) yield return _;
                    if (leaseLost) yield break;

                    // Counted BEFORE the run: a reload mid-run must find it counted.
                    progress.ProbeRunStarts++;
                    progress.Save(progressFile);
                    Status = plan.Gate == null
                        ? $"try this shot: running {shot.Name} (nothing is recorded)"
                        : $"try this shot: running {plan.Gate.Name}, then {shot.Name} (nothing is recorded)";
                    // Slice E.3: a shot with `vision` steps asks the SITE its screen checks, as THIS
                    // claimed run (its id, the key that claimed it) through the kit's own HTTP.
                    var options = ProbeRun.DirectorOptions(ProjectRoot, () => ProbeStopHook(evidence, framePath),
                        shot, new ProbeVisionWire(Http, BaseUrl, StudioKey, progress.RunId, RelayPaths.Root(ProjectRoot)));
                    var startDeadline = EditorApplication.timeSinceStartup + ForeignDirectorWaitSec;
                    // someone else may start one in the same tick: try again until the deadline
                    foreach (var _ in ProbeRun.WhileRefreshing(
                                 () => (director ??= ProbeRun.Start(adapter, plan, options)) == null
                                       && EditorApplication.timeSinceStartup < startDeadline,
                                 Refresh, Lost, LeaseLost)) yield return _;
                    if (leaseLost) yield break;
                    if (director == null)
                    {
                        progress.RecordJobFailure(
                            $"another director stayed active for {ForeignDirectorWaitSec} s — the shot was not run");
                        progress.Save(progressFile);
                        foreach (var _ in ReportDone(progress, progressFile)) yield return _;
                        yield break;
                    }
                    AdDirector running = director;
                    foreach (var _ in ProbeRun.WhileRefreshing(() => !running.IsFinished, Refresh, Lost,
                                 () =>
                                 {
                                     running.Finish("the try's lease was lost");
                                     // Finish asked ScreenCapture for a frame that lands at the END
                                     // of a frame — usually after this delete. The sweep at the next
                                     // probe or claim takes it then (second audit M6).
                                     DeleteFrame(framePath, "probe");
                                     LeaseLost();
                                 })) yield return _;
                    if (leaseLost) yield break;
                    // Slice E.4 — a stop AT THE GATE, in the one sentence a recording's failed take says too
                    if (running.FailedStepKindName == AdDirector.FailedKindPreamble)
                        Status = "try this shot: "
                                 + ProbeGate.StopSentence(running.PreambleFailedStepIndex, running.FailedReason);
                    // The director's Finish fallback reads the screen when no stop point did; this
                    // is the belt to that brace, and it says so rather than passing it off as the
                    // moment of the stop.
                    if (!evidence.Gathered)
                    {
                        GatherProbeEvidence(evidence, framePath);
                        evidence.Error = (evidence.Error == null ? "" : evidence.Error + " · ")
                                         + "the screen was read after the run ended, not at the stop";
                    }
                    foreach (var _ in WaitForFrame(framePath)) yield return null;
                }

                var frameCaptured = FrameOnDisk(framePath);
                // The second audit of E.4 (KM4, KM5): everything the plan, the director and the adapter say is
                // read by ProbeFacts.OfTry — tested — so this coroutine hands over only what the screen read gave.
                var facts = ProbeFacts.OfTry(plan, director, adapter,
                    uiDump: evidence.UiNames,
                    screenWidth: evidence.ScreenWidth,
                    screenHeight: evidence.ScreenHeight,
                    timeScale: evidence.TimeScale,
                    frameCaptured: frameCaptured,
                    frameNote: frameCaptured ? null : evidence.FrameNote ?? NoFrameNote,
                    factsError: evidence.Error);
                progress.ProbeFactsJson = ProbeFacts.ToJson(facts);
                progress.Save(progressFile);
            }

            foreach (var _ in PostProbeResult(progress, progressFile)) yield return _;
        }

        /// <summary>
        /// Post a probe's GATHERED facts — and its frame, when that is still on disk — through the
        /// self-test's door with the self-test's retry rule, then `done`. This is the whole of a
        /// retry: once facts exist the shot is never run again, and posting needs no game, so a
        /// retry does not even enter Play Mode (<see cref="RunJob"/>).
        /// </summary>
        private static IEnumerable PostProbeResult(CaptureProgress progress, string progressFile)
        {
            var framePath = ProbeFramePath(progress.RunId);
            Status = "try this shot: reporting";
            var frameOnDisk = FrameOnDisk(framePath);
            var fields = new Dictionary<string, string>
            {
                ["facts"] = ProbeFacts.ReconcileFrame(progress.ProbeFactsJson!, frameOnDisk),
            };
            using (var post = Http.PostMultipart(StudioEndpoints.Result(BaseUrl, progress.RunId),
                       StudioKey, fields, frameOnDisk ? framePath : null, "file", "image/png"))
            {
                while (!post.IsDone) yield return null;
                if (Ok(post, out var why))
                {
                    progress.ResultPosted = true;
                    DeleteFrame(framePath, "probe");
                }
                else
                {
                    // The self-test's rule (audit M7): a 4xx is the server saying "not like that" and
                    // it will say it again; anything else gets three attempts — of the POST only.
                    progress.ResultAttempts++;
                    var deterministic = JobGuard.IsDeterministicAnswer(post.Status);
                    if (deterministic || progress.ResultAttempts >= MaxResultAttempts)
                    {
                        progress.RecordJobFailure(deterministic
                            ? $"the server refused the Try this shot result ({post.Status}): {why}"
                            : $"could not post the Try this shot result after {progress.ResultAttempts} attempts: {why}");
                        progress.Save(progressFile);
                        DeleteFrame(framePath, "probe");
                        foreach (var _ in ReportDone(progress, progressFile)) yield return _;
                        yield break;
                    }
                    progress.Save(progressFile);
                    FailAndBackOff($"Try this shot result failed (attempt {progress.ResultAttempts}/{MaxResultAttempts}): "
                                   + why + " — will retry on the next poll (the shot is not run again)");
                    yield break;
                }
            }
            progress.Save(progressFile);
            foreach (var _ in ReportDone(progress, progressFile)) yield return _;
        }

        /// <summary>Slice A2′ — the two files the CLAIM handed this editor, and the run they came
        /// with. Not in the progress file: they are the server's bytes, they can be half a megabyte,
        /// and every resume re-claims — which re-delivers them. Keyed by run so a stale pair from an
        /// earlier job can never be written as this one's (the same rule the export's per-run build
        /// directory follows).</summary>
        private static string? _syncNovaRunId;
        private static SyncNovaFiles? _syncNovaFiles;

        private static void RememberSyncNovaFiles(string runId, SyncNovaFiles? files)
        {
            _syncNovaRunId = files == null ? null : runId;
            _syncNovaFiles = files;
        }

        private static SyncNovaFiles? SyncNovaFilesFor(string runId) =>
            _syncNovaRunId == runId ? _syncNovaFiles : null;

        /// <summary>
        /// Slice A2′ — "Send to my editor". Write the two files the website authored into
        /// <c>Library/Nova/</c>, read them back through the REAL loader, and report what this
        /// project made of them.
        ///
        /// EDIT MODE. It never presses Play, opens no scene, needs no adapter and RUNS NO COMMAND —
        /// a shot it delivers only ever runs later, from a capture job or the relay, and its
        /// REFLECTION WRITES (cheats, the ready block, a timeScale step, hiding an overlay) only
        /// after a person ticks their levers in the Nova Capture window. Driving the game's UI by
        /// name is not gated — see TRUST.md.
        ///
        /// FACTS, NEVER A VERDICT (evidence plan 3B), about what WAS written: the names loaded, the
        /// game id, the shas on disk and the levers, for the server to hold against what it sent
        /// (`syncNovaProblem`). What the kit's own loader or readers would refuse is not written
        /// at all (the eleventh audit: the delivery check is the loader) — that is a refusal, by
        /// name, and it goes up as the job's failure.
        /// </summary>
        private static IEnumerable RunSyncNova(CaptureProgress progress, string progressFile)
        {
            ItemTotal = 0;
            if (progress.Done)
            {
                // Resumed with nothing left to do: only `done` is owed.
                foreach (var _ in ReportDone(progress, progressFile)) yield return _;
                yield break;
            }

            var files = SyncNovaFilesFor(progress.RunId);
            var refusal = SyncNova.Refusal(files, progress.GameId);
            SyncNovaResult? result = null;
            if (refusal == null)
            {
                Status = "send to my editor: writing Library/Nova";
                result = SyncNova.Run(ProjectRoot, files, progress.RunId, progress.GameId);
                refusal = result.Refusal;
            }
            if (refusal != null)
            {
                // Nothing was written. It goes up as the job's failure, not as a result document:
                // there is no measurement to report.
                progress.RecordJobFailure(refusal);
                progress.Save(progressFile);
                foreach (var _ in ReportDone(progress, progressFile)) yield return _;
                yield break;
            }

            Status = "send to my editor: reporting";
            var fields = new Dictionary<string, string>
            {
                ["facts"] = result!.ToFactsJson(KitVersion.Current),
            };
            using (var post = Http.PostMultipart(StudioEndpoints.Result(BaseUrl, progress.RunId),
                       StudioKey, fields, null, "file", "application/json"))
            {
                while (!post.IsDone) yield return null;
                if (Ok(post, out var why))
                {
                    progress.ResultPosted = true;
                }
                else
                {
                    // The self-test's rule, for the self-test's reason (audit M7): a 4xx is the
                    // server saying "not like that" and it will say it again, so it ends the job
                    // with something to read; anything else gets three attempts. JobGuard owns the
                    // rule so a retry policy cannot drift from a refusal policy.
                    progress.ResultAttempts++;
                    var deterministic = JobGuard.IsDeterministicAnswer(post.Status);
                    if (deterministic || progress.ResultAttempts >= MaxResultAttempts)
                    {
                        // The FILES ARE ON DISK either way — this is the report failing, not the
                        // write, and the sentence says so rather than implying nothing happened.
                        progress.RecordJobFailure(deterministic
                            ? $"the shots were written to Library/Nova but the server refused the report ({post.Status}): {why}"
                            : $"the shots were written to Library/Nova but the report could not be posted after {progress.ResultAttempts} attempts: {why}");
                        progress.Save(progressFile);
                        foreach (var _ in ReportDone(progress, progressFile)) yield return _;
                        yield break;
                    }
                    progress.Save(progressFile);
                    FailAndBackOff($"send-to-my-editor report failed (attempt {progress.ResultAttempts}/{MaxResultAttempts}): "
                                   + why + " — will retry on the next poll");
                    yield break;
                }
            }
            progress.Save(progressFile);
            foreach (var _ in ReportDone(progress, progressFile)) yield return _;
        }

        /// <summary>How many times an export BUILD may start before the job is refused whole. A
        /// script recompile reloads the domain and kills the build; a studio that keeps saving
        /// scripts would otherwise restart it forever with the web reading "Your editor is
        /// working…" — the unbounded spinner a job must never produce.</summary>
        private const int MaxExportBuildStarts = 5;

        /// <summary>The lease is 30 minutes and a real game's export (build + a gigabyte of parts
        /// on a studio line) can outlast it. An expired lease makes the run claimable by ANOTHER
        /// editor bound to the same workspace, which would start a second export over this one.
        /// So it is refreshed on a clock while the export works.</summary>
        private const double LeaseRefreshSec = 8 * 60;
        private static double _leaseRefreshedAt;

        /// <summary>
        /// Slice D part 1 — "Learn my game". Build the export under Library/AdRelay/export/&lt;run&gt;/,
        /// post its header, PUT each part to a signed URL, report done. Three resumable stages
        /// (<see cref="CaptureProgress.ExportBuilt"/>, <c>ExportHeaderPosted</c>,
        /// <c>ExportPartsUploaded</c>), because a domain reload can land between any two lines.
        ///
        /// EDIT MODE. This never presses Play, opens no scene, and writes nothing under Assets/.
        /// THE KIT EMITS FACTS, NEVER VERDICTS (evidence plan 3B): nothing here decides what a
        /// font, a colour or a pattern hit MEANS — and nothing here decides whether the export
        /// "worked" either. The API sizes and hashes what landed and reconciles it against the
        /// totals in the header; this routine only reports what it did.
        /// </summary>
        private static IEnumerable RunExport(CaptureProgress progress, string progressFile)
        {
            ItemTotal = 0;
            var outDir = CapturePaths.ExportDir(ProjectRoot, progress.RunId);
            if (progress.Done)
            {
                // Resumed with nothing left to do: only `done` is owed (and the build dir, if the
                // earlier `done` failed after the uploads finished).
                foreach (var _ in FinishExport(progress, progressFile, outDir)) yield return _;
                yield break;
            }

            // Identity, for THIS kind: the workspace the studio bound this project to. Not the
            // capture rule — there is no adapter.json before onboarding, and no adapter in Edit Mode.
            string? adapterJsonGameId = null;
            try { adapterJsonGameId = DefaultAdapterBoot.ReadGameId(ProjectRoot); }
            catch (Exception e) { Debug.LogWarning("[RecorderKit] export: could not read adapter.json: " + e.Message); }
            var refusal = WorkspaceGuard.ExportRefusal(progress.WorkspaceId, BoundWorkspaceId,
                progress.GameId, adapterJsonGameId);
            if (refusal != null)
            {
                progress.RecordJobFailure(refusal);
                progress.Save(progressFile);
                foreach (var _ in FinishExport(progress, progressFile, outDir)) yield return _;
                yield break;
            }

            _leaseRefreshedAt = EditorApplication.timeSinceStartup; // the claim that got us here

            // ---- 1. BUILD — or keep a finished build that survived a reload ----
            string? resumeWhy = null;
            var header = progress.ExportBuilt ? ExportOnDisk.LoadVerified(outDir, out resumeWhy) : null;
            if (progress.ExportBuilt && header == null)
                Debug.Log("[RecorderKit] export: the earlier build cannot be resumed (" + resumeWhy + ") — rebuilding");
            if (header == null)
            {
                if (progress.ExportBuildStarts >= MaxExportBuildStarts)
                {
                    progress.RecordJobFailure(
                        $"the export was interrupted {progress.ExportBuildStarts} times before it finished — a script " +
                        "recompile (or entering/leaving Play Mode) restarts it. Leave the editor idle and press Learn my game again");
                    progress.Save(progressFile);
                    foreach (var _ in FinishExport(progress, progressFile, outDir)) yield return _;
                    yield break;
                }
                // Recorded BEFORE the work, like everything else here: if the domain goes down
                // mid-build, the agent that comes back must know a build was started.
                progress.ExportBuilt = false;
                progress.ExportHeaderPosted = false;
                progress.ExportPartsUploaded = 0;
                progress.ExportBuildStarts++;
                progress.Save(progressFile);
                // Every earlier export's debris goes — this run's half-build included. One job
                // runs at a time, so nothing under export/ belongs to anyone else.
                ExportOnDisk.TryDeleteDir(CapturePaths.ExportRoot(ProjectRoot));

                var request = new ExportRequest
                {
                    ProjectRoot = ProjectRoot,
                    OutDir = outDir,
                    WorkspaceId = BoundWorkspaceId,
                    EditorGameId = adapterJsonGameId,
                    AdapterJsonPresent = !string.IsNullOrWhiteSpace(adapterJsonGameId),
                    KitVersion = KitVersion.Current,
                };
                var result = new ExportResult();
                ResetProgressPost();
                // Driven through an EXPLICIT enumerator so the `finally` can dispose it before
                // anything deletes the build. The collector holds the open part `FileShare.None`
                // and closes it in its own `finally`, which runs on Dispose — delete first and on
                // Windows the delete fails silently, leaving the gigabyte behind (audit minor).
                var pump = ExportCollector.Run(request, result).GetEnumerator();
                var leaseLost = false;
                try
                {
                    while (pump.MoveNext())
                    {
                        Status = $"export: {result.Phase} ({Mathf.RoundToInt(Mathf.Clamp01(result.Progress01) * 100)}%)";
                        MaybePostProgress(progress.RunId, ProgressBody(result.Phase, result.Progress01, result.PartsBuilt, result.BytesBuilt, 0, null));
                        if (!Enabled)
                        {
                            Status = "paused — capture jobs are off (the export restarts when re-ticked)";
                            yield break;
                        }
                        foreach (var __ in RefreshLeaseIfDue(progress, progressFile)) yield return __;
                        if (!File.Exists(progressFile)) { leaseLost = true; yield break; }
                        yield return null;
                    }
                }
                finally
                {
                    (pump as IDisposable)?.Dispose();
                    if (leaseLost) ExportOnDisk.TryDeleteDir(CapturePaths.ExportRoot(ProjectRoot));
                }
                if (result.FatalError != null || result.Header == null)
                {
                    progress.RecordJobFailure("the export could not be built: " +
                        (result.FatalError ?? "the exporter finished without a header"));
                    progress.Save(progressFile);
                    foreach (var _ in FinishExport(progress, progressFile, outDir)) yield return _;
                    yield break;
                }
                header = result.Header;
                progress.ExportBuilt = true;
                progress.Save(progressFile);
            }

            // ---- 2. HEADER — the API hands out upload tickets only for parts a header named ----
            if (!progress.ExportHeaderPosted)
            {
                Status = "export: sending the header";
                using var post = Http.PostJson(StudioEndpoints.ExportHeader(BaseUrl, progress.RunId), StudioKey,
                    header.ToJson().ToString(Newtonsoft.Json.Formatting.None));
                while (!post.IsDone) yield return null;
                if (!Ok(post, out var why))
                {
                    // A 4xx is the server saying "not like that", and it will say it again.
                    var deterministic = JobGuard.IsDeterministicAnswer(post.Status);
                    foreach (var _ in ExportStepFailed(progress, progressFile, outDir, deterministic,
                                 $"the server refused the export header ({post.Status}): {why}",
                                 "could not send the export header: " + why)) yield return _;
                    yield break;
                }
                progress.ExportHeaderPosted = true;
                progress.ResultAttempts = 0;
                progress.Save(progressFile);
            }

            // ---- 3. PARTS — one signed URL each, streamed from disk, NO studio key on the PUT ----
            while (progress.ExportPartsUploaded < header.Parts.Count)
            {
                if (!Enabled)
                {
                    Status = $"paused — capture jobs are off ({progress.ExportPartsUploaded}/{header.Parts.Count} parts uploaded, progress kept)";
                    yield break;
                }
                foreach (var _ in RefreshLeaseIfDue(progress, progressFile)) yield return _;
                if (!File.Exists(progressFile))
                {
                    // The lease was lost between parts. No writer is open here, so the build can go
                    // at once (K2 — it must go SOMEWHERE, and nothing will ever claim it again).
                    ExportOnDisk.TryDeleteDir(CapturePaths.ExportRoot(ProjectRoot));
                    yield break;
                }

                var part = header.Parts[progress.ExportPartsUploaded];
                var partPath = Path.Combine(outDir, part.Name);
                ItemIndex = progress.ExportPartsUploaded + 1;
                ItemTotal = header.Parts.Count;
                Status = $"export: uploading part {ItemIndex}/{ItemTotal}";
                MaybePostProgress(progress.RunId, ProgressBody("upload", 1f, header.Parts.Count, TotalBytes(header), progress.ExportPartsUploaded, header.Parts.Count));

                UploadTicket? ticket;
                using (var ask = Http.PostJson(StudioEndpoints.ExportUploadUrl(BaseUrl, progress.RunId), StudioKey,
                           new JObject { ["part"] = part.Index }.ToString(Newtonsoft.Json.Formatting.None)))
                {
                    while (!ask.IsDone) yield return null;
                    if (!Ok(ask, out var askWhy))
                    {
                        var deterministic = JobGuard.IsDeterministicAnswer(ask.Status);
                        foreach (var _ in ExportStepFailed(progress, progressFile, outDir, deterministic,
                                     $"the server refused an upload URL for {part.Name} ({ask.Status}): {askWhy}",
                                     $"could not get an upload URL for {part.Name}: {askWhy}")) yield return _;
                        yield break;
                    }
                    ticket = UploadTicket.Parse(ask.Body, BaseUrl, out var ticketError);
                    if (ticket == null)
                    {
                        foreach (var _ in ExportStepFailed(progress, progressFile, outDir, true,
                                     $"the upload URL for {part.Name} was unreadable: {ticketError}", "")) yield return _;
                        yield break;
                    }
                }

                using (var put = Http.PutFile(ticket.Url, partPath, ticket.Headers))
                {
                    while (!put.IsDone) yield return null;
                    if (!Ok(put, out var putWhy))
                    {
                        // NEVER "deterministic" here, whatever the status: every attempt asks for a
                        // FRESH ticket, so a 403 from one that expired mid-upload is cured by the
                        // retry. Bounded by the attempt cap like every other step.
                        foreach (var _ in ExportStepFailed(progress, progressFile, outDir, false, "",
                                     $"uploading {part.Name} failed: {putWhy}")) yield return _;
                        yield break;
                    }
                }
                progress.ExportPartsUploaded++;
                progress.ResultAttempts = 0; // the cap is per step, not per export
                progress.Save(progressFile);
            }

            // the last part is up: say so (the throttle may have held the previous line)
            _progressNextAt = 0;
            MaybePostProgress(progress.RunId, ProgressBody("upload", 1f, header.Parts.Count, TotalBytes(header), progress.ExportPartsUploaded, header.Parts.Count));

            // Everything this job owes is on the server. What it MEANS is decided there.
            progress.ResultPosted = true;
            progress.Save(progressFile);
            foreach (var _ in FinishExport(progress, progressFile, outDir)) yield return _;
        }

        /// <summary>One export step failed. Deterministic (the server said "not like that") or out
        /// of attempts → the job is refused WHOLE and reported, so the run ends `failed` with a
        /// sentence. Otherwise the progress is kept and the next poll resumes at the same step.</summary>
        private static IEnumerable ExportStepFailed(CaptureProgress progress, string progressFile, string outDir,
            bool deterministic, string deterministicReason, string transientReason)
        {
            progress.ResultAttempts++;
            if (deterministic || progress.ResultAttempts >= MaxResultAttempts)
            {
                progress.RecordJobFailure(deterministic
                    ? deterministicReason
                    : $"{transientReason} (after {progress.ResultAttempts} attempts)");
                progress.Save(progressFile);
                foreach (var _ in FinishExport(progress, progressFile, outDir)) yield return _;
                yield break;
            }
            progress.Save(progressFile);
            FailAndBackOff($"{transientReason} (attempt {progress.ResultAttempts}/{MaxResultAttempts}) — will retry on the next poll");
        }

        /// <summary>Report done. <see cref="ReportDone"/> removes the build with the progress file
        /// once the server has ACCEPTED it; if `done` fails, both stay — deleting the parts first
        /// would leave a resumed job with nothing to upload.</summary>
        private static IEnumerable FinishExport(CaptureProgress progress, string progressFile, string outDir)
        {
            foreach (var _ in ReportDone(progress, progressFile)) yield return _;
        }

        /// <summary>Re-claim (= refresh the lease) when it is due. A refusal means the run is no
        /// longer ours — finished elsewhere, taken over, or gone — so the progress is dropped and
        /// the caller stops; it checks for the progress file to find out. It does NOT delete the
        /// build here: this runs INSIDE the collector's pump, which is holding a zip part open, so
        /// the caller deletes once it has disposed that enumerator.</summary>
        private static IEnumerable RefreshLeaseIfDue(CaptureProgress progress, string progressFile,
            string stopped = "the export was stopped and its files removed")
        {
            if (EditorApplication.timeSinceStartup - _leaseRefreshedAt < LeaseRefreshSec) yield break;
            _leaseRefreshedAt = EditorApplication.timeSinceStartup;
            using var claim = Http.PostJson(StudioEndpoints.Claim(BaseUrl, progress.RunId), StudioKey, "{}");
            while (!claim.IsDone) yield return null;
            if (Ok(claim, out var why)) yield break;
            // Only an ANSWER from the server ends the job — the same rule, and the same function,
            // as the poll's resume branch (K3). A dropped connection is not the server saying no:
            // the export carries on and the next refresh tries again.
            if (!JobGuard.ClaimRefusalEndsJob(claim.Status, claim.TransportError)) yield break;
            try { File.Delete(progressFile); }
            catch (Exception e) { Debug.LogWarning("[RecorderKit] Nova Capture: progress file not removed: " + e.Message); }
            CurrentRunId = null;
            Fail($"run {progress.RunId} is no longer ours ({why}); {stopped}");
        }

        /// <summary>
        /// Record the current item as failed, save the progress, and HAND THE EDITOR BACK ONCE (the thirteenth audit, M1).
        /// The unknown-shot path used to go straight on to the next item, which re-reads shots.json: a job naming K shots
        /// the delivered file no longer holds ran K loads and K file writes inside one editor tick.
        /// </summary>
        internal static IEnumerable FailItem(CaptureProgress progress, string progressFile, string reason)
        {
            progress.RecordFailed(reason);
            progress.Save(progressFile);
            yield return null;
        }

        private static IEnumerable ReportDone(CaptureProgress progress, string progressFile,
            IEnumerable<string>? shotLoadErrors = null)
        {
            Status = "reporting done";
            using var done = Http.PostJson(StudioEndpoints.Done(BaseUrl, progress.RunId), StudioKey, progress.DoneReportJson(shotLoadErrors));
            while (!done.IsDone) yield return null;
            ReleaseStartScenePin(progress);
            if (!Ok(done, out var why))
            {
                // Keep the progress file — and the build with it: the next poll refreshes the lease
                // and retries `done` with the same counts instead of re-recording the whole job
                // (audit W3), and a resumed job with its parts deleted would have nothing to upload.
                FailAndBackOff("done failed: " + why + " — will retry on the next poll");
                yield break;
            }
            // The one place a job ENDS cleanly, so the one place both go (K2).
            DropProgress(progressFile);
            CurrentRunId = null;
            Status = $"done: {progress.Captured} captured, {progress.Failed} failed";
            if (progress.Errors.Count > 0)
                LastError = string.Join("\n", progress.Errors);
            Debug.Log($"[RecorderKit] Nova Capture: run {progress.RunId} {Status}");
            _nextPoll = 0; // there may be another job waiting
        }

        /// <summary>RE-AUDIT of M7 — one aborted attempt at a job that owes a RESULT, counted and
        /// persisted. At the cap the job is refused whole so the next poll REPORTS it (the
        /// `Done` guard sends it straight to `done`) and the run ends `failed` with a reason,
        /// rather than retrying every 15 s with a spinner the studio cannot escape.
        ///
        /// Only for kinds that owe a result: a CAPTURE aborting mid-job must still resume and
        /// finish its remaining items, which is the behaviour the capture loop was built for.
        /// Best-effort throughout — this runs inside a catch, and throwing out of it would be the
        /// bug it exists to prevent.</summary>
        private static void NoteAbortedAttempt(CaptureProgress? progress)
        {
            if (progress == null || progress.Kind == CaptureJob.CaptureKind || progress.Done) return;
            try
            {
                progress.ResultAttempts++;
                if (progress.ResultAttempts >= MaxResultAttempts)
                {
                    progress.RecordJobFailure(
                        $"the {progress.Kind} job failed {progress.ResultAttempts} times in this editor — "
                        + (LastError ?? "see the Unity console for the error"));
                }
                progress.Save(CapturePaths.ProgressFile(ProjectRoot));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RecorderKit] could not record the aborted attempt: " + e.Message);
            }
        }

        /// <summary>Clears the play-mode start scene IF this agent pinned it for this job. Idempotent;
        /// a null progress is a no-op.</summary>
        private static void ReleaseStartScenePin(CaptureProgress? progress)
        {
            if (progress == null || !progress.PlayModeStartSceneSet) return;
            UnityEditor.SceneManagement.EditorSceneManager.playModeStartScene = null;
            progress.PlayModeStartSceneSet = false;
        }

        /// <summary>Pins the first enabled build-settings scene as the play-mode start scene when
        /// none is pinned. Returns false (and pins nothing) when one is already set or the project
        /// lists no enabled scene.</summary>
        private static bool EnsurePlayModeStartScene(out string scenePath)
        {
            scenePath = "";
            if (UnityEditor.SceneManagement.EditorSceneManager.playModeStartScene != null)
                return false;
            foreach (var scene in EditorBuildSettings.scenes)
            {
                if (!scene.enabled || string.IsNullOrEmpty(scene.path)) continue;
                var asset = AssetDatabase.LoadAssetAtPath<SceneAsset>(scene.path);
                if (asset == null) continue;
                UnityEditor.SceneManagement.EditorSceneManager.playModeStartScene = asset;
                scenePath = scene.path;
                return true;
            }
            return false;
        }

        private static bool Ok(IStudioResponse res, out string why)
        {
            if (res.TransportError != null)
            {
                why = res.TransportError;
                return false;
            }
            if (res.Status < 200 || res.Status >= 300)
            {
                var body = res.Body;
                why = $"HTTP {res.Status}" + (string.IsNullOrEmpty(body) ? "" : ": " + Truncate(body, 300));
                return false;
            }
            why = "";
            return true;
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";

    }
}
