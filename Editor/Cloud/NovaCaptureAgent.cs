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
        private static readonly string ProjectRoot = Directory.GetCurrentDirectory();

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

        public static void ForgetKey()
        {
            EditorPrefs.DeleteKey(Key("Key"));
            AccountName = null;
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
                        _routine = null;
                }
                catch (Exception e)
                {
                    Fail("agent aborted: " + e.Message);
                    Debug.LogError("[RecorderKit] Nova Capture agent aborted: " + e);
                    _routine = null;
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
            try
            {
                var o = JObject.Parse(res.Body);
                AccountName = o["accountName"]?.Value<string>() ?? o["accountId"]?.Value<string>();
                Status = "connected";
            }
            catch (Exception e)
            {
                Fail("connect: unreadable reply: " + e.Message);
            }
        }

        private static IEnumerator PollRoutine()
        {
            LastPollUtc = DateTime.UtcNow;
            var progressFile = CapturePaths.ProgressFile(ProjectRoot);
            var progress = CaptureProgress.Load(progressFile);
            if (progress == null)
            {
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
                var job = CaptureJob.ParseClaim(claim.Body, out var claimParse);
                if (job == null)
                {
                    Fail("claim: " + claimParse);
                    yield break;
                }
                progress = CaptureProgress.Start(job);
                progress.Save(progressFile);
            }
            else
            {
                // Resuming after a reload: refresh the lease so the server knows this editor is alive.
                using var claim = Http.PostJson(StudioEndpoints.Claim(BaseUrl, progress.RunId), StudioKey, "{}");
                while (!claim.IsDone) yield return null;
                if (!Ok(claim, out var claimWhy))
                {
                    // The run finished, was taken over after our lease lapsed, or is gone: drop it.
                    ReleaseStartScenePin(progress);
                    File.Delete(progressFile);
                    Fail($"run {progress.RunId} is no longer ours ({claimWhy}); dropped its progress file");
                    yield break;
                }
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

            var recorder = RecorderDrivers.For(adapter.Recorder);
            var outputDir = Path.Combine(ProjectRoot, recorder.OutputDir);

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
                    progress.RecordFailed("unknown shot in this project's shots.json");
                    progress.Save(progressFile);
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

                // Wait for the screen the shot is about (see ShotReadyWaitSec).
                var ready = shot.ArmCondition ?? shot.Settle;
                var ui = adapter.Ui ?? new UguiDriver();
                var readyDeadline = EditorApplication.timeSinceStartup + ShotReadyWaitSec;
                while (!ready.IsMet(ui, adapter.StateProbe) && EditorApplication.timeSinceStartup < readyDeadline)
                {
                    Status = $"waiting for {ready.Describe()} before {item.Shot} ({ItemIndex}/{ItemTotal})";
                    yield return null;
                }

                var startedAt = DateTime.UtcNow.AddSeconds(-1);
                var director = AdDirector.Run(adapter, new[] { shot });
                if (director == null)
                {
                    yield return null; // someone else started one in the same tick; retry
                    continue;
                }
                while (!director.IsFinished) yield return null;

                if (!director.AllCaptured)
                {
                    progress.RecordFailed("take failed: " + LastLine(director.Summary));
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

        /// <summary>AUDIT M7. How many times the result POST may fail before the job is refused
        /// whole and REPORTED. Retrying forever left the studio watching "Running in your editor…"
        /// with no end — the one outcome a diagnostic must never produce.</summary>
        private const int MaxResultAttempts = 3;

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
            string? frameNote = null;
            try
            {
                if (File.Exists(framePath)) File.Delete(framePath);
            }
            catch (Exception e)
            {
                // Catch-all, not IOException: UnauthorizedAccessException escaping here would leave
                // the run stuck reading "running in your editor" forever (audit M7).
                frameNote = "could not clear the previous frame: " + e.Message;
            }
            ScreenCapture.CaptureScreenshot(framePath);

            // ScreenCapture writes at the end of a frame. Wait for the file AND for its size to
            // stop changing — a half-written PNG uploads as a broken image, which on a Doctor
            // screen is indistinguishable from a broken game.
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
            var frameCaptured = false;
            try { frameCaptured = File.Exists(framePath) && new FileInfo(framePath).Length > 0; }
            catch (IOException) { frameCaptured = false; }
            if (!frameCaptured && frameNote == null)
                frameNote = $"ScreenCapture wrote no frame within {ScreenshotWaitSec:0}s";

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
                factsError: factsError);

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
                    try { if (File.Exists(framePath)) File.Delete(framePath); }
                    catch (Exception e)
                    {
                        Debug.LogWarning("[RecorderKit] self-test frame not cleaned up: " + e.Message);
                    }
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
                    // (~2-3 minutes of their editor) to reach the same answer. Only a 5xx or a
                    // transport failure is worth another attempt.
                    var deterministic = post.Status >= 400 && post.Status < 500;
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
                    Fail($"self-test result failed (attempt {progress.ResultAttempts}/{MaxResultAttempts}): "
                         + why + " — will retry on the next poll");
                    yield break;
                }
            }
            progress.Save(progressFile);
            foreach (var _ in ReportDone(progress, progressFile)) yield return _;
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
                // Keep the progress file: the next poll refreshes the lease and retries `done`
                // with the same counts instead of re-recording the whole job (audit W3).
                Fail("done failed: " + why + " — will retry on the next poll");
                yield break;
            }
            File.Delete(progressFile);
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

        private static string LastLine(string summary)
        {
            var lines = summary.Split('\n').Where(l => l.Trim().Length > 0).ToArray();
            return lines.Length == 0 ? "(no summary)" : lines[lines.Length - 1].Trim();
        }
    }
}
