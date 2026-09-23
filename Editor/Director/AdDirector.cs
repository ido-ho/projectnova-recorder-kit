using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Autonomous shot capture, generalized from SNL's hand-built director. Drives the adapter's
    /// shots in Play Mode via an editor-pumped coroutine: ready gate first (with one recovery
    /// retry — see ReachReadyState), then per shot recover → setup cheats → record → steps →
    /// settle → verify (IsCaptured) with retry and quarantine of failed takes. All game specifics
    /// come from the GameAdapter; all timing from the injected clock (unit-testable).
    /// KNOWN LIMITATION: a domain reload that exits Play Mode (the common case) tears down the
    /// whole scene, so nothing is left dangling. A reload that does NOT exit Play Mode (only
    /// possible with Unity's non-default "Recompile And Continue Playing" setting) can orphan a
    /// disabled OverlayHider Behaviour, since Active and the relay's pending state both reset on
    /// reload but the live scene doesn't — cosmetic only, never affects capture correctness.
    /// </summary>
    public sealed class AdDirector : IShotRunHandle
    {
        public sealed class Options
        {
            public int MaxAttempts = 2;
            /// <summary>Skip shots that need a reasoning agent (fully-autonomous runs).</summary>
            public bool SkipVisionShots;
            /// <summary>False in tests: pump manually via PumpOnce().</summary>
            public bool AutoPump = true;
            public Func<double>? Now;
            public IRecorderDriver? Recorder;
            public IVisionChannel? Vision;

            /// <summary>
            /// Values for the shot's declared `{name}` placeholders, supplied per run. Empty is the
            /// norm: a shot declaring no parameters never consults this.
            /// </summary>
            public IReadOnlyDictionary<string, string> Bindings =
                new Dictionary<string, string>();

            /// <summary>
            /// Disarm an armed camera-pose. Defaults to <c>camera-release</c> on the current
            /// project. Tests inject a counter. Must not run while the recorder is still open —
            /// stills stay posed through the take.
            /// </summary>
            public Action? ReleaseCameraHold;

            /// <summary>
            /// Slice A2′ — where this project's <c>Library/Nova/</c> lives: the LEVER GATE reads
            /// its levers.json and synced.json here, and this run reads its adapter.json here too,
            /// so the file that is gated and the file that is obeyed are the same file. Null (the
            /// norm) means the current directory, which is the Unity project root — the same root
            /// <c>FileVisionChannel</c> and the camera release already use. Tests point it at a
            /// temp project so the gate's real seam can be driven without writing into the test
            /// host's own Library.
            /// </summary>
            public string? ProjectRoot;

            /// <summary>
            /// Slice E.1 — THE SHOTS IN THIS RUN CAME FROM THE CLOUD, so the lever gate applies
            /// whatever the files on disk say (<see cref="Levers.GateActiveFor"/>).
            ///
            /// It exists because a <c>probe</c> runs a shot the website sent and writes NO file: the
            /// gate's ordinary answer is derived from the sha of <c>Library/Nova/shots.json</c> and
            /// <c>adapter.json</c>, and on a project whose own files are on disk that answer is
            /// "local authorship, ungated" — which would run every un-ticked cheat the delivered
            /// script carries. Read once, into a readonly field, at construction: there is nothing
            /// to reset and so nothing a failure path can fail to reset.
            /// </summary>
            public bool CloudContent;

            /// <summary>
            /// Second audit of E.1, M1 — WRAP THE LEVER GATE for this run: given the gated bridge
            /// and this run's log, return the bridge every command goes through
            /// (<c>ctx.Cheats</c>). Null (the norm) leaves the gate as it is. A probe wraps it in
            /// <see cref="ProbeCameraTypeGuard"/>, the camera-pose Type rule asked with the run's
            /// cloud flag, without a second copy of the gate. Applied once, at construction, like
            /// the gate itself: the bridge cannot change during the run.
            /// </summary>
            public Func<ICheatBridge, Action<string>, ICheatBridge>? WrapCheats;

            /// <summary>
            /// Slice E.1 — run ONCE, at the moment the run stops, and PUMPED: the director yields
            /// until the enumerable ends, then carries on. It is where a probe reads the names on
            /// screen, the time scale and the screen size, and asks for its one frame — because a
            /// moment later the camera hold is released, the overlay hider has put the HUD back and
            /// <see cref="Finish"/> has reset <c>Time.timeScale</c>, and the evidence would describe
            /// a screen the shot never stopped on. Pumped rather than called, so the frame (which
            /// Unity writes at the END of a frame) is taken of the stop and not of what came next.
            ///
            /// WHERE "THE STOP" IS: before the recorder stops on the run's LAST attempt — every step
            /// ran (reached, or the settle did not hold) or one step did not resolve; on a
            /// placeholder that cannot be bound; on a ready gate that never opened. And a FALLBACK
            /// in <see cref="Finish"/> for a run that ends any other way (an exception, a stop from
            /// outside): there the hook's FIRST step runs synchronously and the rest is abandoned,
            /// because <see cref="Finish"/> cannot wait. Meant for a single-shot run (a probe runs
            /// one shot, one attempt); in a longer run it fires at the first of those points only.
            ///
            /// Exceptions out of it are logged, never thrown: gathering evidence must not abort the
            /// run it is evidence about.
            /// </summary>
            public Func<IEnumerable>? OnStopped;

            /// <summary>
            /// Slice E.4 — THE PREAMBLE: a shot run FIRST, once, with the recorder OFF, in the same
            /// Play session and through the same lever gate (<c>ctx.Cheats</c>, and a
            /// <c>timeScale</c> step gated as any), before <see cref="RunAll"/> runs the shots. A try
            /// sets it to the TUTORIAL GATE the claim carried (<see cref="ProbeRun.Start"/>, the one
            /// door); null (the norm) runs nothing first.
            ///
            /// THE ORDER: the ready gate, told of the PREAMBLE ALONE → the preamble as its own try ran
            /// it — recovery, its <c>setup</c>, its arm wait, its steps, its settle — WITHOUT the
            /// recorder (never started, no step marks, no outcome) → the ready gate AGAIN, told of the
            /// shots (first audit of E.4, M1: a game whose ready writes need the board cannot land them
            /// on the tutorial screen the preamble gets past) → then each shot exactly as without one
            /// (its own recovery, setup, recording, steps, settle). A
            /// studio's compiled recovery policy runs before the shot as it always has; one that
            /// returned the game to the start of its tutorial would undo the preamble — the default
            /// JSON adapter has none.
            ///
            /// A PREAMBLE THAT STOPS IS THE RUN'S STOP: a step that does not resolve, or its end state
            /// not holding within its settle timeout, ends the run there — no shot is started, its
            /// steps never run. The stop is <see cref="FailedKindPreamble"/> with no step of the shot
            /// (<see cref="FailedStepIndex"/> null), the preamble's own step in
            /// <see cref="PreambleFailedStepIndex"/>, and the stop hook (<see cref="OnStopped"/>) reads
            /// the screen there. A <c>vision</c> step in it fails without being asked: a screen check
            /// is asked of the site by its index in the SENT shot's steps, and the preamble's index
            /// would name a step of another shot.
            /// </summary>
            public AdShot? Preamble;
        }

        public static AdDirector? Active { get; private set; }

        private readonly GameAdapter _adapter;
        private readonly IReadOnlyList<AdShot> _shots;
        private readonly Options _options;
        private readonly DirectorContext _ctx;
        private readonly IRecorderDriver _recorder;
        private readonly IVisionChannel _vision;
        /// <summary>The vision channel this run asks (tests: a capture keeps the file channel, a try
        /// with vision steps asks the site).</summary>
        internal IVisionChannel VisionChannel => _vision;
        private readonly List<string> _log = new();
        private readonly List<(string Shot, bool Captured)> _outcomes = new();
        /// <summary>
        /// Step 4 (2026-09-08): WHEN each shots.json step ran, in seconds since the recorder started,
        /// for the take that was kept. A story cites a beat's start as a moment by NAME
        /// (`board-news-inbox@click-newsbtn`) instead of a guessed second that breaks the day the
        /// take is re-recorded. Reset per attempt; the last mark is the settle. The capture agent
        /// writes it beside the take as `<shot>_<take>.steps.json` and uploads it with the clip.
        /// </summary>
        public IReadOnlyList<StepMark> StepMarks => _stepMarks;
        private readonly List<StepMark> _stepMarks = new();
        private IEnumerator? _routine;
        private OverlayHider? _overlays;
        private readonly bool _autoPump;
        private readonly Action _releaseHold;
        /// <summary>Where this project's <c>Library/Nova/</c> is — the LEVER GATE's root, and the
        /// root every read of adapter.json in this run uses, so the file that is gated and the file
        /// that is obeyed are the same file (<see cref="Options.ProjectRoot"/>).</summary>
        private readonly string _projectRoot;
        /// <summary>Slice E.1 — this run's shots came from the cloud, so the lever gate is live
        /// whatever is on disk. See <see cref="Options.CloudContent"/>.</summary>
        private readonly bool _cloudContent;
        /// <summary>THE LEVER GATE ITSELF, typed — the bridge this run built, NOT <c>_ctx.Cheats</c>. A probe's try wraps
        /// the gate (<see cref="Options.WrapCheats"/> → <c>ProbeCameraTypeGuard</c>, slice E), so <c>_ctx.Cheats</c> is the
        /// wrapper there, and a cast of it to <see cref="LeverGateBridge"/> threw on every try (stack pass 3: 17 of the 60
        /// ProbeTests red with InvalidCastException). The attempt's held liveness (the thirteenth audit, S1) and the
        /// timeScale step's question are asked of this; the wrapper's writes reach it underneath.</summary>
        private readonly LeverGateBridge _gate;
        /// <summary>The stop hook fires once (see <see cref="Options.OnStopped"/>).</summary>
        private bool _stopNotified;

        public bool IsFinished { get; private set; }
        public bool AllCaptured => _outcomes.Count > 0 && _outcomes.All(o => o.Captured);

        /// <summary>
        /// Slice E.1 — WHICH STEP STOPPED THE RUN, as data rather than as a sentence to grep for.
        /// The 0-based index into the shot's own <c>steps</c> array, or null when no single step is
        /// to blame: the shot reached the end, the settle never held, the ready gate never opened,
        /// or a <c>{placeholder}</c> could not be bound. <see cref="FailedStepKindName"/> says which
        /// of those it was; <see cref="FailedReason"/> is the director's own sentence, verbatim.
        ///
        /// Set by the LAST attempt that ran, so a multi-attempt capture reports the failure the run
        /// ended on. A probe runs with <c>MaxAttempts = 1</c>, so there is exactly one.
        /// </summary>
        public int? FailedStepIndex { get; private set; }

        /// <summary>The grammar spelling of the failed step's kind (<c>waitFor</c>), or
        /// <c>settle</c> / <c>ready</c> / <c>binding</c> / <c>aborted</c> for the failures that
        /// are not one step. Null when nothing failed.</summary>
        public string? FailedStepKindName { get; private set; }

        /// <summary>The director's own sentence for that failure — the same line it wrote to the
        /// log, without the log's two-space indent. Null when nothing failed.</summary>
        public string? FailedReason { get; private set; }

        /// <summary>Slice E.4 — did this run RUN its preamble (<see cref="Options.Preamble"/>)? True from
        /// the moment the director STARTS it — its recovery, then its setup, then its steps — so every stop
        /// the preamble can cause (a setup placeholder it cannot bind included) is a stop with it run.
        /// False with no preamble, or when the run stopped before it (the first ready gate). A try
        /// reports it as <c>gateRan</c>.</summary>
        public bool PreambleRan { get; private set; }

        /// <summary>Slice E.4 — the 0-based index into the PREAMBLE's own steps of the step that stopped
        /// the run, or null — no preamble, it got through, or every step of it ran and its end state
        /// did not hold (then <see cref="FailedStepKindName"/> is still
        /// <see cref="FailedKindPreamble"/>). A try reports it as <c>gateFailedStep</c>.</summary>
        public int? PreambleFailedStepIndex { get; private set; }

        /// <summary>Did the preamble get through (every step, and its end state held)? Read by
        /// <see cref="RunAll"/> right after it.</summary>
        private bool _preambleHeld;

        /// <summary>The run log, line by line — <see cref="Summary"/> is the same thing joined. A
        /// reader that wants the lines back out of the string cannot tell a newline in a game's
        /// error message from a line break.</summary>
        public IReadOnlyList<string> LogLines => _log;

        /// <summary><see cref="FailedStepKindName"/> when every step ran and the shot's settle
        /// condition never held.</summary>
        public const string FailedKindSettle = "settle";
        /// <summary><see cref="FailedStepKindName"/> when the ready gate never opened, so no step
        /// ever ran.</summary>
        public const string FailedKindReady = "ready";
        /// <summary><see cref="FailedStepKindName"/> when a <c>{placeholder}</c> could not be bound,
        /// which aborts the shot before it starts.</summary>
        public const string FailedKindBinding = "binding";
        /// <summary><see cref="FailedStepKindName"/> when the director itself threw and the run was
        /// aborted mid-way.</summary>
        public const string FailedKindAborted = "aborted";
        /// <summary>Slice E.4 — <see cref="FailedStepKindName"/> when the PREAMBLE stopped the run (a
        /// try's tutorial gate): no step of the shot ran. The wire's word, <c>tutorial-gate</c>.</summary>
        public const string FailedKindPreamble = "tutorial-gate";

        /// <summary>Record WHAT stopped this run. Last writer wins: with more than one attempt the
        /// run ends on the last one's failure, and a success clears it through
        /// <see cref="ClearFailure"/>.</summary>
        private void NoteFailure(int? stepIndex, string? kind, string? reason)
        {
            FailedStepIndex = stepIndex;
            FailedStepKindName = kind;
            FailedReason = reason;
        }

        /// <summary>An attempt that captured leaves nothing to explain.</summary>
        private void ClearFailure() => NoteFailure(null, null, null);
        /// <summary>The kept take's step times as the sidecar JSON (IShotRunHandle, Step 4).</summary>
        public string? StepMarksJson => _stepMarks.Count > 0 && _outcomes.Count > 0
            ? StepMark.ToJson(_outcomes[_outcomes.Count - 1].Shot, _stepMarks)
            : null;
        public string Summary => string.Join("\n", _log);

        public static AdDirector? Run(GameAdapter adapter, IReadOnlyList<AdShot> shots,
            Options? options = null)
        {
            if (Active != null)
            {
                Debug.LogWarning("[RecorderKit] Director already running.");
                return null;
            }
            var d = new AdDirector(adapter, shots, options ?? new Options());
            Active = d;
            d.Start();
            return d;
        }

        private AdDirector(GameAdapter adapter, IReadOnlyList<AdShot> shots, Options options)
        {
            _adapter = adapter;
            _shots = shots;
            _options = options;
            _autoPump = options.AutoPump;
            var now = options.Now ?? (() => EditorApplication.timeSinceStartup);
            _recorder = options.Recorder ?? RecorderDrivers.For(adapter.Recorder);
            _vision = options.Vision ?? new FileVisionChannel(Directory.GetCurrentDirectory());
            // Slice A2′ — THE LEVER GATE. This decorator covers every write that goes through
            // ICheatBridge: the shot's steps, its `setup` and the `ready` block all run through
            // this same `ctx.Cheats`, so they are one seam rather than three call sites that can
            // grow a fourth. TWO WRITES DO NOT COME THROUGH HERE and are gated where they happen
            // instead (audit m9): a `timeScale` step (DoStep) and `overlayTypeNames` hiding
            // (RunAll). Their levers are `timeScale` and `hide-overlay <TypeName>`.
            // Inert unless one of the two files under Library/Nova is a file the cloud delivered
            // to this project (synced.json's history, either file — Levers.GateActive). Locally
            // authored files are ungated, which is every project that used this kit before A2′.
            var log = new Action<string>(s => _log.Add(s));
            // ONE resolver for "which project is this" (audit M4): the delivery writes the cloud's
            // two files under the Editor's own project, so a gate that looked them up under the
            // process's working directory could be pointed somewhere else — by any script in the
            // editor — and find no synced.json at all, which reads as "nothing here came from the
            // cloud" and runs every delivered cheat un-ticked.
            _projectRoot = options.ProjectRoot ?? KitProject.Root();
            // Slice E.1: read here, into a readonly field, so the answer to "is this content the
            // cloud's" is fixed for the life of the run and no later code path can change it.
            _cloudContent = options.CloudContent;
            var gated = new LeverGateBridge(adapter.CheatBridge, _projectRoot, log, _cloudContent);
            _gate = gated;
            var cheats = options.WrapCheats != null ? options.WrapCheats(gated, log) : gated;
            _ctx = new DirectorContext(adapter.Ui ?? new UguiDriver(), adapter.StateProbe,
                cheats, now, log);
            _releaseHold = options.ReleaseCameraHold ?? ReleaseHoldForCurrentProject;
        }

        /// <summary>The folder this run's lever gate reads — named so a test can see WHICH project
        /// the gate resolved, rather than inferring it from a passing gate (audit M4).</summary>
        internal string ProjectRoot => _projectRoot;

        /// <summary>
        /// Production camera-release. Leftover yaw poisons later takes — follow
        /// never writes rotation — so complete, aborted, and failed shots all land here.
        ///
        /// THIS PROJECT'S ROOT, NEVER THE WORKING DIRECTORY (third audit, M5): the default
        /// adapter's bridge writes camera-hold.json under the project root (KitProject.Root), so a
        /// release looking under a moved working directory found nothing, left the snapshot on disk,
        /// and the next pose read that stale one as "home".
        /// </summary>
        private void ReleaseHoldForCurrentProject()
        {
            var outcome = CameraPose.Release(CameraPose.Host.ForProject(_projectRoot));
            if (!outcome.Ok)
                _log.Add($"WARN camera-release: {outcome.Error}");
        }

        /// <summary>
        /// Insertion points: after <c>_recorder.Stop()</c>, both RunShot bindingFailed
        /// <c>yield break</c>s, and <see cref="Finish"/>. Not a <c>finally</c> — stills
        /// must stay posed until the take is on disk.
        /// </summary>
        private void ReleaseHold()
        {
            try
            {
                _releaseHold();
            }
            catch (Exception e)
            {
                _log.Add($"WARN camera-release threw: {e.Message}");
            }
        }

        private void Start()
        {
            // A shot containing `{hero}` but no `parameters:` declaration would film the literal
            // string. Catch it at run start rather than in the edit.
            foreach (var shot in _shots)
            {
                if (shot.Parameters.Count > 0) continue;
                foreach (var text in shot.Setup.Concat(shot.Steps
                             .Where(s => s.Kind != AdStepKind.Vision).Select(s => s.Text)))
                {
                    if (ShotBinding.HasPlaceholder(text))
                        _log.Add($"WARN {shot.Name}: '{text}' contains a {{placeholder}} but the shot " +
                                 "declares no parameters — it will be sent literally. Add `parameters:`.");
                }
            }

            _routine = RunAll().GetEnumerator();
            if (_autoPump)
                EditorApplication.update += Pump;
            Debug.Log($"[RecorderKit] Running {_shots.Count} shot(s) for '{_adapter.GameId}'.");
        }

        private void Pump() => PumpOnce();

        public void PumpOnce()
        {
            if (IsFinished)
                return;
            try
            {
                if (_routine == null || !_routine.MoveNext())
                    Finish("complete");
            }
            catch (Exception e)
            {
                Debug.LogError($"[RecorderKit] director aborted: {e}");
                // Into the run's own log too, and as the run's failure: the console is not what a
                // capture's summary or a probe's facts carry, and "not reached, no reason" is the
                // one answer a run must never give.
                var sentence = $"ABORT: the director threw {e.GetType().Name}: {e.Message}";
                _log.Add(sentence);
                NoteFailure(null, FailedKindAborted, sentence);
                Finish("aborted");
            }
        }

        public void Finish(string reason)
        {
            if (IsFinished)
                return;
            if (_autoPump)
                EditorApplication.update -= Pump;
            _routine = null;
            // Before anything is stopped or restored — see Options.OnStopped.
            NotifyStoppedNow();
            if (_recorder.IsRecording)
                _recorder.Stop();
            _overlays?.Restore();
            _overlays = null;
            // Finish is the catch-all for complete and aborted; RunShot also releases
            // after Stop so a later shot in the same run does not inherit leftover yaw.
            ReleaseHold();
            // Slice E.3: a channel with a request in flight (a try's screen check, over HTTP) is
            // closed with the run — a lost lease finishes the director mid-question, and the
            // request must not outlive the run it was asked for. The file channel holds nothing.
            try { (_vision as IDisposable)?.Dispose(); }
            catch (Exception e) { _log.Add($"WARN the vision channel threw on close: {e.Message}"); }
            Time.timeScale = 1f;
            IsFinished = true;
            if (Active == this)
                Active = null;
            Debug.Log(FinishMessage(reason));
        }

        /// <summary>The most summary lines <see cref="Finish"/> puts in the console (the fourteenth audit, M2).</summary>
        internal const int SummaryConsoleLines = 200;

        /// <summary>Where <see cref="Finish"/> writes the whole summary, relative to this project's root.</summary>
        internal const string SummaryFileRelative = RelayPaths.PreferredRootRelative + "/director-summary.txt";

        /// <summary>
        /// THE FOURTEENTH AUDIT, M2 — ONE Debug.Log OF THE WHOLE RUN was up to about 500,000 lines ("Run all shots" over a
        /// delivered 512 KB file of refused setup lists; 205 ms for the log call alone in batch mode, the interactive
        /// console's own cost unmeasured). The console gets the summary's last <see cref="SummaryConsoleLines"/> lines,
        /// with how many there were; the whole summary is written to <see cref="SummaryFileRelative"/> under THIS project's
        /// root (never the working directory — the third audit's M5), each run replacing the last. A summary that fits is
        /// logged whole, as before. The file is best-effort: a write that fails is said in the console, never thrown.
        /// </summary>
        private string FinishMessage(string reason)
        {
            var summary = Summary;
            string? wrote = null;
            try
            {
                var file = Path.Combine(RelayPaths.Root(_projectRoot), "director-summary.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, summary);
                wrote = SummaryFileRelative;
            }
            catch (Exception e)
            {
                wrote = null;
                summary += $"\nWARN could not write the summary file: {e.Message}";
            }
            var lines = summary.Split('\n');
            if (lines.Length <= SummaryConsoleLines)
                return $"[RecorderKit] {reason}.\n{summary}";
            var tail = string.Join("\n", lines, lines.Length - SummaryConsoleLines, SummaryConsoleLines);
            return $"[RecorderKit] {reason}. The summary is {lines.Length} lines; the last {SummaryConsoleLines} follow" +
                   (wrote != null ? $", and all of them are in {wrote}" : " (the summary file could not be written)") +
                   $".\n{tail}";
        }

        private IEnumerable RunAll()
        {
            // Leftover camera-hold.json from an aborted session: next pose would
            // treat the stale euler as home. Armed holds are left alone (stills). Swept under THIS
            // project's root — where the bridge writes it — never the working directory (M5).
            try
            {
                var sweep = CameraPose.SweepOrphaned(CameraPose.Host.ForProject(_projectRoot));
                if (!sweep.Ok)
                    _log.Add($"WARN leftover camera-hold: {sweep.Error}");
            }
            catch (Exception e)
            {
                _log.Add($"WARN leftover camera-hold sweep threw: {e.Message}");
            }

            // Overlay hiding DISABLES MonoBehaviours in the studio's running game — a write, and
            // not one that goes through the cheat bridge. On a gated project each type name the
            // cloud's adapter.json asked for needs its own `hide-overlay <TypeName>` tick; a name
            // that exists only in a studio's own compiled adapter is their own code and is never
            // gated (nothing would ever list it to be ticked).
            var overlaysFromJson = AdapterJson.Load(_projectRoot).OverlayTypeNames;
            var overlaysWanted = AdapterJson.OverlayNamesFor(overlaysFromJson, _adapter.OverlayTypeNames);
            _overlays = OverlayHider.Hide(
                Levers.OverlaysAllowed(_projectRoot, overlaysWanted, overlaysFromJson, _log.Add,
                        adapterIsJsonDriven: _adapter.IsDefaultAdapter, cloudContent: _cloudContent)
                    .ToArray());
            // Do not force timescale 1 here. HygieneReadyGate must see a leftover 0
            // (cards / pause / death) and fail ready. After the gate succeeds we
            // restore 1 so custom adapters still start shots at normal speed.

            // Warn ONCE per run, into the summary the operator actually reads. A mismatch here yields
            // clips that pass every automated check with their subject missing from frame, so the only
            // defence is saying so out loud — see CaptureAspect for the full failure mode.
            if (CaptureAspect.Mismatch(_recorder.OutputWidth, _recorder.OutputHeight) is { } warning)
                _log.Add($"WARN {warning}");

            // Tell the gate what work is coming BEFORE it runs — each shot's name and its declared
            // baseline — so it can establish the right baseline instead of hardcoding one. See
            // DirectorContext.UpcomingShots for why a gate that cannot see the work can only ever be
            // right for some of it.
            // Slice E.4 (first audit, M1): with a preamble, the gate is told of the PREAMBLE ALONE — what
            // runs next — and runs again for the shots once it held (below). A game whose ready writes need
            // the board cannot land them on a tutorial screen: told of the shot here, the gate would stop
            // the try before the preamble that gets past the tutorial ever ran.
            _ctx.UpcomingShots = Upcoming(_options.Preamble == null ? _shots : new[] { _options.Preamble });

            // Restore anything a PREVIOUS run's `hide-ui` left hidden (2026-09-07). A shot that hides
            // the HUD to pose a still leaves it hidden for the rest of the Play session, and the next
            // run's ready gate then waits for a button that is inactive by our own hand. Measured live
            // on SNL from the box: board-attack-toast hid BoardHud, board-news-inbox's gate waited 30 s
            // for RollBTN, recovery found "nothing active on screen", the shot aborted. Only the ledger
            // `hide-ui` keeps is restored, so this is a no-op when nothing was hidden.
            if (!_ctx.Cheats.Run("show-ui"))
                _log.Add("WARN show-ui before the ready gate failed (continuing)");

            foreach (var _ in ReachReadyState()) yield return null;

            // A run with NO shots is the "just reach the baseline" request (the relay's `ready`
            // action). Record the gate as the outcome so AllCaptured reports the gate's verdict
            // rather than the vacuous false of an empty outcome list.
            if (_shots.Count == 0)
                _outcomes.Add(("ready-gate", _ctx.LastOpSucceeded));

            if (!_ctx.LastOpSucceeded)
            {
                const string readySentence = "ABORT: ready gate never satisfied, even after recovery";
                _log.Add(readySentence);
                NoteFailure(null, FailedKindReady, readySentence);
                foreach (var _ in NotifyStopped()) yield return null;
                yield break;
            }

            Time.timeScale = 1f;

            // Slice E.4 — the preamble (a try's tutorial gate) FIRST; if it stops, the run stops there.
            if (_options.Preamble is { } preamble)
            {
                foreach (var _ in RunPreamble(preamble)) yield return null;
                if (!_preambleHeld) yield break;
                // …then the ready gate AGAIN, told of the shots — the baseline they need, now reachable
                // (first audit of E.4, M1). Its stop is the run's `ready` stop, with the preamble run.
                _ctx.UpcomingShots = Upcoming(_shots);
                foreach (var _ in ReachReadyState()) yield return null;
                if (!_ctx.LastOpSucceeded)
                {
                    const string readySentence = "ABORT: ready gate never satisfied, even after recovery";
                    _log.Add(readySentence);
                    NoteFailure(null, FailedKindReady, readySentence);
                    foreach (var _ in NotifyStopped()) yield return null;
                    yield break;
                }
                Time.timeScale = 1f;
            }

            foreach (var shot in _shots)
            {
                if (_options.SkipVisionShots && shot.RequiresVision)
                {
                    _log.Add($"SKIP {shot.Name}: requires vision (semi-interactive)");
                    _outcomes.Add((shot.Name, false));
                    continue;
                }
                Time.timeScale = 1f;
                foreach (var _ in RunShot(shot)) yield return null;
            }
        }

        /// <summary>What the ready gate is told is coming: each shot's name, baseline and resist.</summary>
        private static List<UpcomingShot> Upcoming(IEnumerable<AdShot> shots) =>
            shots.Select(s => new UpcomingShot(s.Name, s.Baseline, s.Resist)).ToList();

        /// <summary>
        /// Satisfy the ready gate, running the recovery policy once and retrying if the first
        /// attempt fails.
        ///
        /// Why the retry exists: in a batch run the gate only ever meets a freshly-started
        /// session, so it passed first try and RunShot's per-attempt recovery covered everything
        /// after it. Driving shots ONE AT A TIME over the relay broke that assumption — each
        /// run-shot builds a new director against whatever screen the PREVIOUS shot left up, and
        /// the gate ran before any recovery. A shot settling on a reward popup therefore stranded
        /// every later single-shot call until a manual stop+play. Recovery is the policy that
        /// knows how to get back to the baseline, so the gate has to be allowed to use it.
        /// Found live 2026-07-28 driving SNL over the relay.
        /// </summary>
        private IEnumerable ReachReadyState()
        {
            foreach (var _ in _adapter.ReadyGate.WaitUntilReady(_ctx)) yield return null;
            if (_ctx.LastOpSucceeded)
                yield break;
            _log.Add("ready gate unsatisfied — running recovery, then retrying the gate once");
            foreach (var _ in _adapter.Recovery.Recover(_ctx)) yield return null;
            // The gate's own verdict is the only one that counts here: Recover() also writes
            // LastOpSucceeded, and a recovery that "succeeded" without reaching the baseline
            // must not be mistaken for a satisfied gate.
            foreach (var _ in _adapter.ReadyGate.WaitUntilReady(_ctx)) yield return null;

            // A gate that fails TWICE is almost always the game parked on a screen the gate has no
            // route through — and the name of that screen is the one fact needed to fix it. Dump it
            // here, while it is still up: every such failure so far has cost the operator a manual
            // screenshot + `run-cheat ui-dump` round trip to learn something the director was
            // standing right in front of. Do it before returning, because the next shot's recovery
            // will clear the evidence.
            if (!_ctx.LastOpSucceeded)
                _log.Add(_ctx.Cheats.Run("ui-dump")
                    ? $"the blocking screen is dumped in {RelayPaths.PreferredRootRelative}/probe/ui-dump.txt"
                    : "could not ui-dump the blocking screen, so it is unidentified");
        }

        /// <summary>
        /// Bind a command string for the shot being run. Returns false and logs when the binding
        /// cannot be satisfied; callers must abort the attempt rather than proceed with the raw text.
        ///
        /// Skipped entirely for a shot that declares no parameters, which is what keeps every
        /// pre-existing shot byte-identical.
        /// </summary>
        private bool TryBind(AdShot shot, string text, bool isLever, out string bound)
        {
            bound = text;
            if (shot.Parameters.Count == 0 && _options.Bindings.Count == 0) return true;
            if (!ShotBinding.TryApply(text, shot.Parameters, _options.Bindings, isLever, out bound, out var error))
            {
                var sentence = $"ABORT {shot.Name}: {error}";
                _log.Add(sentence);
                NoteFailure(null, FailedKindBinding, sentence);
                return false;
            }
            return true;
        }

        private IEnumerable RunShot(AdShot shot)
        {
            for (var attempt = 1; attempt <= _options.MaxAttempts; attempt++)
            {
                // ONE HASH PER ATTEMPT, NOT PER WRITE (the thirteenth audit, S1): whether the lever gate is live is asked
                // here, once, and held until this attempt ends; each command's approval is still read as it runs. A change
                // to whether the gate is live (a delivery landing mid-attempt) takes effect at the next attempt.
                using var liveness = _gate.HoldLiveness();
                Time.timeScale = 1f;
                foreach (var _ in _adapter.Recovery.Recover(_ctx)) yield return null;

                var bindingFailed = false;
                foreach (var cmd in shot.Setup)
                {
                    if (!TryBind(shot, cmd, true, out var bound)) { bindingFailed = true; break; }
                    if (!_ctx.Cheats.Run(bound))
                        _log.Add($"  {shot.Name}: setup cheat failed: '{bound}'");
                    // ONE YIELD PER UNIT OF WORK (the thirteenth audit, S1): a setup list had no yield at all, and 16,355
                    // copies of one delivered command ran inside one editor tick.
                    yield return null;
                }
                if (bindingFailed)
                {
                    _outcomes.Add((shot.Name, false));
                    foreach (var _ in NotifyStopped()) yield return null;
                    // Setup may already have posed; we never reach recorder.Stop().
                    ReleaseHold();
                    yield break;
                }
                foreach (var _ in _ctx.WaitSeconds(0.4)) yield return null;

                // Hold the recorder until the shot is ARMED, so a navigating setup's loading screen and
                // any popup the destination raises on load stay out of the file. Warn but record anyway
                // on timeout: a dirty head can be trimmed, a missing clip cannot.
                if (shot.ArmCondition is { } arm)
                {
                    foreach (var _ in _ctx.WaitUntil(() => arm.IsMet(_ctx.Ui, _ctx.State), shot.ArmTimeoutSec))
                        yield return null;
                    if (!_ctx.LastOpSucceeded)
                        _log.Add($"  {shot.Name}: arm condition {arm.Describe()} never held in "
                            + $"{shot.ArmTimeoutSec}s — recording anyway, expect a dirty head");
                }

                // Bind ALL step text up front: a placeholder failure on a later step must not start
                // a recording that can only be thrown away.
                var boundText = new string[shot.Steps.Count];
                for (var s = 0; s < shot.Steps.Count; s++)
                {
                    // Vision prompts are prose and may legitimately contain braces.
                    if (shot.Steps[s].Kind == AdStepKind.Vision) { boundText[s] = shot.Steps[s].Text; continue; }
                    // A cheat or cheatUntil command is a lever, which reaches the gate; a click or hold name is not (ninth
                    // audit, M2: ShotBinding.TryApply's brace guard reads levers only).
                    var isLever = shot.Steps[s].Kind is AdStepKind.Cheat or AdStepKind.CheatUntil;
                    if (!TryBind(shot, shot.Steps[s].Text, isLever, out boundText[s])) { bindingFailed = true; break; }
                }
                if (bindingFailed)
                {
                    _outcomes.Add((shot.Name, false));
                    foreach (var _ in NotifyStopped()) yield return null;
                    // Setup may already have posed; we never reach recorder.Stop().
                    ReleaseHold();
                    yield break;
                }

                _recorder.Start(shot.Name);
                _stepMarks.Clear();
                var recordingStartedAt = _ctx.Now();

                var stepFailed = false;
                for (var s = 0; s < shot.Steps.Count; s++)
                {
                    var step = shot.Steps[s];
                    _stepMarks.Add(new StepMark(s, step.Kind.ToString(), Describe(step), _ctx.Now() - recordingStartedAt));
                    foreach (var _ in DoStep(step, boundText[s], s)) yield return null;
                    // ONE YIELD PER UNIT OF WORK (the thirteenth audit, S1): a timeScale, a click with no `until`, or a waitFor
                    // or cheatUntil already met finishes without yielding, and a list of them ran inside one editor tick.
                    // (Nothing between here and the check below writes `_visionFailure`, which VisionWait set.)
                    yield return null;
                    if (!_ctx.LastOpSucceeded)
                    {
                        var stepSentence = $"{shot.Name} attempt {attempt}: {Describe(step)} did not resolve";
                        // Slice E.3: a vision step says WHY — a verdict ("did not match"), a
                        // refusal in the site's own words, a timeout, no reply — because "did not
                        // resolve" alone reads, on a try's result, like a screen that did not match.
                        if (step.Kind == AdStepKind.Vision && _visionFailure != null)
                            stepSentence += " — " + _visionFailure;
                        _log.Add("  " + stepSentence);
                        // Slice E.1: WHICH step, as data — its index in the shot's own `steps`
                        // array and the grammar's word for its kind — beside the same sentence.
                        NoteFailure(s, ShotKind.Name(step.Kind), stepSentence);
                        stepFailed = true;
                        break;
                    }
                }

                if (!stepFailed)
                {
                    // Wait for the FULL captured condition, not just the settle half: ExpectState
                    // is a target to wait for, never an assertion to race. A UI settle condition
                    // routinely holds before the state has caught up (a roll button that is present
                    // throughout a token's move), which made every such shot a coin flip.
                    foreach (var _ in _ctx.WaitUntil(() => shot.IsCaptured(_ctx.State, _ctx.Ui),
                                 shot.SettleTimeoutSec)) yield return null;
                    _stepMarks.Add(new StepMark(shot.Steps.Count, "settled", "settled", _ctx.Now() - recordingStartedAt));
                    foreach (var _ in _ctx.WaitSeconds(0.8)) yield return null;
                }

                // Slice E.1: the run's LAST attempt stops here, reached or not — the moment a probe
                // reads the screen, BEFORE the recorder, the camera hold, the overlays or the time
                // scale are touched. A no-op unless a hook was given (a capture never gives one).
                if (attempt == _options.MaxAttempts)
                    foreach (var _ in NotifyStopped()) yield return null;

                _recorder.Stop();
                // After Stop so the posed still is already in the file. Success, fail,
                // and retry all pass here — do not wait for Finish, the next attempt
                // must not inherit this take's hold.
                ReleaseHold();

                if (!stepFailed && shot.IsCaptured(_ctx.State, _ctx.Ui))
                {
                    ClearFailure();
                    _log.Add($"OK   {shot.Name}" + (attempt > 1 ? $" (attempt {attempt})" : ""));
                    _outcomes.Add((shot.Name, true));
                    Time.timeScale = 1f;
                    yield break;
                }

                if (!stepFailed)
                {
                    // Every step resolved and the shot still did not END where it says it ends. Said
                    // in its own sentence — the FAIL line below reports only the state machine, and
                    // for a shot with no expectState that is `expected=''`, which names nothing.
                    var settleSentence = $"{shot.Name} attempt {attempt}: every step ran, but "
                        + SettleWanted(shot) + $" did not hold within {shot.SettleTimeoutSec}s";
                    _log.Add("  " + settleSentence);
                    NoteFailure(null, FailedKindSettle, settleSentence);
                }

                // Bad take: let the file finalize, then move it out of the deliverable folder.
                Time.timeScale = 1f;
                foreach (var _ in _ctx.WaitSeconds(0.5)) yield return null;
                MarkFailed(shot.Name, attempt);
                _log.Add($"{(attempt < _options.MaxAttempts ? "RETRY" : "FAIL ")} {shot.Name}: "
                    + $"state='{_ctx.State.CurrentStateName}' expected='{shot.ExpectState}'");
            }
            _outcomes.Add((shot.Name, false));
        }

        /// <summary>
        /// Slice E.4 — run <see cref="Options.Preamble"/> (see there): the shot's recovery, setup, arm
        /// wait, steps and settle, as <see cref="RunShot"/> runs them, but ONCE and with the recorder
        /// never started. Sets <see cref="_preambleHeld"/>; on a stop, notes it as the run's failure
        /// (<see cref="FailedKindPreamble"/>) and runs the stop hook before the camera hold is
        /// released, as a shot's stop does.
        ///
        /// "AS RUNSHOT RUNS THEM" INCLUDES A2′'s THIRTEENTH FOLD (stack pass 3, the E.3 → E.4 merge): one unit
        /// of work per pump — a yield after each setup command and after each step — and whether the lever
        /// gate is live asked ONCE for the preamble and held for it (<see cref="LeverGateBridge.HoldLiveness"/>),
        /// so its setup writes and its <c>timeScale</c> steps read the held answer, as one attempt of a shot
        /// does. The hold ends with the preamble: the ready gate that runs again after it asks per write, as
        /// before any attempt, and the shot's attempt holds its own. A gate's setup and steps are delivered
        /// content like a shot's (a try's claim, a recording's claim) under the same loader caps.
        /// </summary>
        private IEnumerable RunPreamble(AdShot gate)
        {
            PreambleRan = true;
            _preambleHeld = false;
            // ONE HASH FOR THE PREAMBLE, NOT PER WRITE (A2′'s thirteenth audit, S1, as RunShot's attempt holds it).
            using var liveness = _gate.HoldLiveness();
            Time.timeScale = 1f;
            _log.Add($"{gate.Name}: runs first, not recorded");
            foreach (var _ in _adapter.Recovery.Recover(_ctx)) yield return null;

            string? stop = null;
            foreach (var cmd in gate.Setup)
            {
                if (!TryBind(gate, cmd, true, out var bound)) { stop = FailedReason; break; }
                if (!_ctx.Cheats.Run(bound))
                    _log.Add($"  {gate.Name}: setup cheat failed: '{bound}'");
                // ONE YIELD PER UNIT OF WORK (A2′'s thirteenth audit, S1, as RunShot's setup loop).
                yield return null;
            }
            if (stop == null)
            {
                foreach (var _ in _ctx.WaitSeconds(0.4)) yield return null;
                if (gate.ArmCondition is { } arm)
                {
                    foreach (var _ in _ctx.WaitUntil(() => arm.IsMet(_ctx.Ui, _ctx.State), gate.ArmTimeoutSec))
                        yield return null;
                    if (!_ctx.LastOpSucceeded)
                        _log.Add($"  {gate.Name}: arm condition {arm.Describe()} never held in "
                            + $"{gate.ArmTimeoutSec}s — running it anyway");
                }
            }
            for (var s = 0; stop == null && s < gate.Steps.Count; s++)
            {
                var step = gate.Steps[s];
                string text;
                if (step.Kind == AdStepKind.Vision)
                {
                    // Never asked: the site answers a screen check by the step's index in the SENT
                    // shot's steps, and this index is the preamble's.
                    _ctx.LastOpSucceeded = false;
                    _visionFailure = PreambleVisionNotAsked;
                }
                else if (!TryBind(gate, step.Text, step.Kind is AdStepKind.Cheat or AdStepKind.CheatUntil, out text))
                {
                    stop = FailedReason;
                    PreambleFailedStepIndex = s;
                    break;
                }
                else
                {
                    foreach (var _ in DoStep(step, text, s)) yield return null;
                }
                // ONE YIELD PER UNIT OF WORK (A2′'s thirteenth audit, S1, as RunShot's step loop): a timeScale, a click
                // with no `until`, a waitFor or cheatUntil already met, and a vision step (never asked here) finish
                // without yielding. Nothing between here and the check writes `_visionFailure`.
                yield return null;
                if (_ctx.LastOpSucceeded) continue;
                stop = $"{gate.Name}: {Describe(step)} did not resolve"
                       + (step.Kind == AdStepKind.Vision && _visionFailure != null ? " — " + _visionFailure : "");
                PreambleFailedStepIndex = s;
            }
            if (stop == null)
            {
                foreach (var _ in _ctx.WaitUntil(() => gate.IsCaptured(_ctx.State, _ctx.Ui),
                             gate.SettleTimeoutSec)) yield return null;
                foreach (var _ in _ctx.WaitSeconds(0.8)) yield return null;
                if (!gate.IsCaptured(_ctx.State, _ctx.Ui))
                    stop = $"{gate.Name}: every step ran, but " + SettleWanted(gate)
                           + $" did not hold within {gate.SettleTimeoutSec}s";
            }
            if (stop != null)
            {
                _log.Add("  " + stop);
                _log.Add($"STOP {gate.Name}: the shot after it was not run");
                NoteFailure(null, FailedKindPreamble, stop);
                foreach (var _ in NotifyStopped()) yield return null;
                ReleaseHold();
                yield break;
            }
            _log.Add($"OK   {gate.Name} (ran first, not recorded)");
            // the shot after it must not inherit the preamble's camera hold (RunShot's rule)
            ReleaseHold();
            Time.timeScale = 1f;
            _preambleHeld = true;
        }

        /// <summary>Slice E.4 — the sentence of a <c>vision</c> step in a preamble, which is never
        /// asked (<see cref="Options.Preamble"/>).</summary>
        internal const string PreambleVisionNotAsked =
            "a screen check in the tutorial gate is not asked during a try of another shot";

        private IEnumerable DoStep(AdStep step, string text, int stepIndex)
        {
            switch (step.Kind)
            {
                case AdStepKind.Cheat:
                    _ctx.LastOpSucceeded = _ctx.Cheats.Run(text);
                    foreach (var _ in _ctx.WaitSeconds(0.4)) yield return null;
                    break;
                case AdStepKind.CheatUntil:
                {
                    // Check the condition BEFORE the first run: the desired state may already hold, and
                    // an idempotent cleanup should cost nothing when there is nothing to clean.
                    var deadline = _ctx.Now() + step.TimeoutSec;
                    while (!step.Until!.Value.IsMet(_ctx.Ui, _ctx.State) && _ctx.Now() < deadline)
                    {
                        _ctx.Cheats.Run(text);
                        foreach (var _ in _ctx.WaitSeconds(step.RetryEverySec)) yield return null;
                    }
                    _ctx.LastOpSucceeded = step.Until!.Value.IsMet(_ctx.Ui, _ctx.State);
                    break;
                }
                case AdStepKind.TimeScale:
                    // Time.timeScale is a write into the studio's game that does not pass the cheat
                    // bridge, so the gate is asked here, for the lever the Nova Capture window
                    // lists: `timeScale`, once, whatever the factor (audit m9). Under the liveness this attempt holds (the
                    // thirteenth audit, S1: this hashed the delivered file on every such step), with this run's cloud flag
                    // (slice E.1) — the gate was built with it, so a try's timeScale is gated whatever the disk says.
                    if (!_gate.LeverAllowed(Levers.TimeScaleLever))
                    {
                        _log.Add(Levers.NotApprovedLog(Levers.TimeScaleLever));
                        _ctx.LastOpSucceeded = false;
                        break;
                    }
                    Time.timeScale = (float)step.Number;
                    _ctx.LastOpSucceeded = true;
                    break;
                case AdStepKind.Wait:
                    _ctx.LastOpSucceeded = true;
                    foreach (var _ in _ctx.WaitSeconds(step.Number)) yield return null;
                    break;
                case AdStepKind.WaitFor:
                    foreach (var _ in _ctx.WaitUntil(
                                 () => step.Until!.Value.IsMet(_ctx.Ui, _ctx.State), step.TimeoutSec))
                        yield return null;
                    break;
                case AdStepKind.Hold:
                    _ctx.Ui.PointerDown(text, step.Index);
                    foreach (var _ in _ctx.WaitSeconds(step.Number)) yield return null;
                    _ctx.Ui.PointerUp(text, step.Index);
                    _ctx.LastOpSucceeded = true;
                    break;
                case AdStepKind.Click:
                    foreach (var _ in _ctx.ClickUntil(text, step.Until, step.TimeoutSec,
                                 step.RetryEverySec, step.Index)) yield return null;
                    break;
                case AdStepKind.Vision:
                    foreach (var _ in VisionWait(step, stepIndex)) yield return null;
                    break;
            }
        }

        /// <summary>The whole of what "settled" means for this shot: its settle condition, and its
        /// expected state when it declares one (<see cref="AdShot.IsCaptured"/> is both).</summary>
        private string SettleWanted(AdShot shot) =>
            $"settle {shot.Settle.Describe()}"
            + (shot.ExpectState == null
                ? ""
                : $" in state '{shot.ExpectState}' (the state is '{_ctx.State.CurrentStateName}')");

        /// <summary>
        /// Run <see cref="Options.OnStopped"/> — once per run, pumped, every exception caught. See
        /// that field for where "the stop" is and why it is pumped rather than called.
        /// </summary>
        private IEnumerable NotifyStopped()
        {
            if (_stopNotified || _options.OnStopped == null) yield break;
            _stopNotified = true;
            IEnumerator? hook = null;
            try { hook = _options.OnStopped()?.GetEnumerator(); }
            catch (Exception e) { _log.Add($"WARN the stop hook threw: {e.Message}"); }
            if (hook == null) yield break;
            try
            {
                while (true)
                {
                    bool more;
                    try { more = hook.MoveNext(); }
                    catch (Exception e)
                    {
                        _log.Add($"WARN the stop hook threw: {e.Message}");
                        break;
                    }
                    if (!more) break;
                    yield return null;
                }
            }
            finally
            {
                try { (hook as IDisposable)?.Dispose(); }
                catch (Exception e) { _log.Add($"WARN the stop hook threw on cleanup: {e.Message}"); }
            }
        }

        /// <summary>The <see cref="Finish"/> fallback: a run that ended without passing a stop point
        /// (an exception, a stop from outside) still gets its evidence — the hook's first step,
        /// synchronously, while nothing has been restored yet. Never throws.</summary>
        private void NotifyStoppedNow()
        {
            if (_stopNotified || _options.OnStopped == null) return;
            _stopNotified = true;
            try
            {
                var hook = _options.OnStopped()?.GetEnumerator();
                hook?.MoveNext();
                (hook as IDisposable)?.Dispose();
            }
            catch (Exception e)
            {
                _log.Add($"WARN the stop hook threw: {e.Message}");
            }
        }

        /// <summary>
        /// Name a step in a failure message. Steps whose subject is a condition rather than an
        /// element carry an empty Text, which used to log the unreadable "WaitFor '' did not
        /// resolve" — an operator could not tell which of a shot's waits had timed out.
        /// </summary>
        private static string Describe(AdStep step) => step.Kind switch
        {
            AdStepKind.WaitFor => $"WaitFor {step.Until?.Describe()}",
            AdStepKind.CheatUntil => $"Cheat '{step.Text}' until {step.Until?.Describe()}",
            AdStepKind.Click when step.Until.HasValue =>
                $"Click '{step.Text}' until {step.Until.Value.Describe()}",
            AdStepKind.Vision => $"Vision '{step.Text}'",
            _ => $"{step.Kind} '{step.Text}'",
        };

        /// <summary>Slice E.3 — the sentence a vision step that did NOT get <c>match: true</c> adds
        /// to its failure, or null. Set by <see cref="VisionWait"/>, read by the step loop right
        /// after it. ONLY a real answer of "no" reads "did not match"
        /// (<see cref="VisionNoMatch"/>); a refusal, a timeout or a question nobody could be asked
        /// says that instead — a 0.8.0 kit must not report a verdict nobody made.</summary>
        private string? _visionFailure;

        /// <summary>The one sentence a vision step's failure says when the question WAS answered,
        /// and the answer was no.</summary>
        internal const string VisionNoMatch = "the screen check answered: the screen did not match";

        /// <summary>The sentence of a vision step whose question got no answer before the step's own
        /// <c>timeout</c> ran out.</summary>
        internal static string VisionTimedOut(double timeoutSec) =>
            $"no answer to the screen check within the step's {timeoutSec}s timeout";

        /// <summary>The sentence of a vision step whose channel could not even ask.</summary>
        internal const string VisionNotAsked = "the screen check could not be asked (the channel made no request)";

        private IEnumerable VisionWait(AdStep step, int stepIndex)
        {
            _visionFailure = null;
            var id = _vision.Request(step.Text, stepIndex);
            if (id == null)
            {
                _visionFailure = VisionNotAsked;
                _ctx.LastOpSucceeded = false;
                yield break;
            }
            // Slice E.3: a channel that can end a question WITHOUT a verdict is asked through
            // IVisionOutcome — the HTTP channel, and the file channel since the first audit of E.3
            // (K-S1); a channel that implements only IVisionChannel is asked through TryGetResult.
            var outcome = _vision as IVisionOutcome;
            var deadline = _ctx.Now() + step.TimeoutSec;
            bool? match = null;
            string? failure = null;
            while (_ctx.Now() < deadline)
            {
                if (outcome != null)
                {
                    var poll = outcome.Poll(id);
                    if (poll.Match != null)
                    {
                        match = poll.Match;
                        break;
                    }
                    if (poll.Failure != null)
                    {
                        failure = poll.Failure;
                        break;
                    }
                }
                else if (_vision.TryGetResult(id, out var m))
                {
                    match = m;
                    break;
                }
                yield return null;
            }
            if (match == null && failure == null)
            {
                _log.Add($"  vision '{step.Text}' timed out after {step.TimeoutSec}s");
                failure = VisionTimedOut(step.TimeoutSec);
            }
            _visionFailure = match == false ? VisionNoMatch : failure;
            _ctx.LastOpSucceeded = match == true;
        }

        /// <summary>
        /// Move the just-written take out of the deliverable folder so only verified clips
        /// remain. Best-effort: a filesystem failure is logged and swallowed — renaming a debug
        /// artifact must never abort the capture run.
        /// </summary>
        private void MarkFailed(string shotName, int attempt)
        {
            var clip = NewestClip(shotName);
            if (clip == null)
                return;
            try
            {
                var failed = Path.Combine(_recorder.OutputDir,
                    $"{shotName}_FAILED_{attempt}{Path.GetExtension(clip)}");
                if (File.Exists(failed))
                    File.Delete(failed);
                File.Move(clip, failed);
            }
            catch (IOException e)
            {
                _log.Add($"  (could not mark {shotName} take failed: {e.Message})");
            }
        }

        private string? NewestClip(string shotName)
        {
            if (!Directory.Exists(_recorder.OutputDir))
                return null;
            string? newest = null;
            var newestTime = DateTime.MinValue;
            foreach (var f in Directory.GetFiles(_recorder.OutputDir, shotName + "_*.mp4"))
            {
                // Exclude already-quarantined takes — the newest LIVE recording is always the
                // one to mark failed next, never a prior attempt's already-renamed file.
                if (Path.GetFileName(f).Contains("_FAILED_"))
                    continue;
                var t = File.GetLastWriteTimeUtc(f);
                if (t > newestTime)
                {
                    newestTime = t;
                    newest = f;
                }
            }
            return newest;
        }
    }

    /// <summary>One shots.json step and when it ran, seconds since the recorder started (Step 4).</summary>
    public readonly struct StepMark
    {
        public readonly int Index;
        public readonly string Kind;
        public readonly string Label;
        public readonly double AtSec;

        public StepMark(int index, string kind, string label, double atSec)
        {
            Index = index;
            Kind = kind;
            Label = label;
            AtSec = atSec;
        }

        /// <summary>
        /// The sidecar the renderer reads (`take-moments.ts`): `{"$schemaVersion":1,"shot":…,
        /// "marks":[{"i","kind","label","atSec"}]}`. Hand-built JSON on purpose — the kit must
        /// stay compilable in a project without Newtonsoft in this assembly's runtime path.
        /// </summary>
        public static string ToJson(string shot, IReadOnlyList<StepMark> marks)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"$schemaVersion\":1,\"shot\":").Append(Quote(shot)).Append(",\"marks\":[");
            for (var i = 0; i < marks.Count; i++)
            {
                var m = marks[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"i\":").Append(m.Index)
                  .Append(",\"kind\":").Append(Quote(m.Kind))
                  .Append(",\"label\":").Append(Quote(m.Label))
                  .Append(",\"atSec\":").Append(m.AtSec.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))
                  .Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static string Quote(string s)
        {
            var sb = new System.Text.StringBuilder("\"");
            foreach (var c in s ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }
    }
}
