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
        }

        public static AdDirector? Active { get; private set; }

        private readonly GameAdapter _adapter;
        private readonly IReadOnlyList<AdShot> _shots;
        private readonly Options _options;
        private readonly DirectorContext _ctx;
        private readonly IRecorderDriver _recorder;
        private readonly IVisionChannel _vision;
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

        public bool IsFinished { get; private set; }
        public bool AllCaptured => _outcomes.Count > 0 && _outcomes.All(o => o.Captured);
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
            _ctx = new DirectorContext(adapter.Ui ?? new UguiDriver(), adapter.StateProbe,
                adapter.CheatBridge, now, s => _log.Add(s));
            _releaseHold = options.ReleaseCameraHold ?? ReleaseHoldForCurrentProject;
        }

        /// <summary>
        /// Production camera-release. Leftover yaw poisons later takes — follow
        /// never writes rotation — so complete, aborted, and failed shots all land here.
        /// </summary>
        private void ReleaseHoldForCurrentProject()
        {
            var outcome = CameraPose.Release(
                CameraPose.Host.ForProject(Directory.GetCurrentDirectory()));
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
            if (_recorder.IsRecording)
                _recorder.Stop();
            _overlays?.Restore();
            _overlays = null;
            // Finish is the catch-all for complete and aborted; RunShot also releases
            // after Stop so a later shot in the same run does not inherit leftover yaw.
            ReleaseHold();
            Time.timeScale = 1f;
            IsFinished = true;
            if (Active == this)
                Active = null;
            Debug.Log($"[RecorderKit] {reason}.\n{Summary}");
        }

        private IEnumerable RunAll()
        {
            // Leftover camera-hold.json from an aborted session: next pose would
            // treat the stale euler as home. Armed holds are left alone (stills).
            try
            {
                var sweep = CameraPose.SweepOrphaned(
                    CameraPose.Host.ForProject(Directory.GetCurrentDirectory()));
                if (!sweep.Ok)
                    _log.Add($"WARN leftover camera-hold: {sweep.Error}");
            }
            catch (Exception e)
            {
                _log.Add($"WARN leftover camera-hold sweep threw: {e.Message}");
            }

            _overlays = OverlayHider.Hide(AdapterJson.OverlayNamesFor(
                AdapterJson.Load(Directory.GetCurrentDirectory()).OverlayTypeNames,
                _adapter.OverlayTypeNames));
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
            _ctx.UpcomingShots = _shots.Select(s => new UpcomingShot(s.Name, s.Baseline, s.Resist)).ToList();

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
                _log.Add("ABORT: ready gate never satisfied, even after recovery");
                yield break;
            }

            Time.timeScale = 1f;

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
        private bool TryBind(AdShot shot, string text, out string bound)
        {
            bound = text;
            if (shot.Parameters.Count == 0 && _options.Bindings.Count == 0) return true;
            if (!ShotBinding.TryApply(text, shot.Parameters, _options.Bindings, out bound, out var error))
            {
                _log.Add($"ABORT {shot.Name}: {error}");
                return false;
            }
            return true;
        }

        private IEnumerable RunShot(AdShot shot)
        {
            for (var attempt = 1; attempt <= _options.MaxAttempts; attempt++)
            {
                Time.timeScale = 1f;
                foreach (var _ in _adapter.Recovery.Recover(_ctx)) yield return null;

                var bindingFailed = false;
                foreach (var cmd in shot.Setup)
                {
                    if (!TryBind(shot, cmd, out var bound)) { bindingFailed = true; break; }
                    if (!_ctx.Cheats.Run(bound))
                        _log.Add($"  {shot.Name}: setup cheat failed: '{bound}'");
                }
                if (bindingFailed)
                {
                    _outcomes.Add((shot.Name, false));
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
                    if (!TryBind(shot, shot.Steps[s].Text, out boundText[s])) { bindingFailed = true; break; }
                }
                if (bindingFailed)
                {
                    _outcomes.Add((shot.Name, false));
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
                    foreach (var _ in DoStep(step, boundText[s])) yield return null;
                    if (!_ctx.LastOpSucceeded)
                    {
                        _log.Add($"  {shot.Name} attempt {attempt}: {Describe(step)} did not resolve");
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

                _recorder.Stop();
                // After Stop so the posed still is already in the file. Success, fail,
                // and retry all pass here — do not wait for Finish, the next attempt
                // must not inherit this take's hold.
                ReleaseHold();

                if (!stepFailed && shot.IsCaptured(_ctx.State, _ctx.Ui))
                {
                    _log.Add($"OK   {shot.Name}" + (attempt > 1 ? $" (attempt {attempt})" : ""));
                    _outcomes.Add((shot.Name, true));
                    Time.timeScale = 1f;
                    yield break;
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

        private IEnumerable DoStep(AdStep step, string text)
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
                    foreach (var _ in VisionWait(step)) yield return null;
                    break;
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

        private IEnumerable VisionWait(AdStep step)
        {
            var id = _vision.Request(step.Text);
            if (id == null)
            {
                _ctx.LastOpSucceeded = false;
                yield break;
            }
            var deadline = _ctx.Now() + step.TimeoutSec;
            bool? match = null;
            while (_ctx.Now() < deadline)
            {
                if (_vision.TryGetResult(id, out var m))
                {
                    match = m;
                    break;
                }
                yield return null;
            }
            if (match == null)
                _log.Add($"  vision '{step.Text}' timed out after {step.TimeoutSec}s");
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
