using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ProjectNova.RecorderKit
{
    /// <summary>Why the lever gate refused a command (<see cref="LeverGateBridge.LastRefusal"/>).</summary>
    internal enum GateRefusal
    {
        /// <summary>Not refused by the gate.</summary>
        None,
        /// <summary>Nothing in levers.json ticks the lever.</summary>
        NotTicked,
        /// <summary>Ticked, but risky, and the project is not ticked non-production (<see cref="Levers.RiskyNotYet"/>).</summary>
        RiskyNotYet,
        /// <summary>Ticked, but a press by name whose words the press guard fences (<see cref="PressGuard.Refusal"/>).</summary>
        PressGuard,
        /// <summary>The command holds a {placeholder}: a cheat with a value slot never runs as-is, ticked or not — it needs a
        /// value (the Fix 4 re-audit; never "not ticked", invariant 99).</summary>
        Template,
    }

    /// <summary>
    /// THE GATE ON ICheatBridge: one decorator on the seam every CHEAT a shot can run goes through
    /// (<c>ICheatBridge.Run</c> — <c>AdDirector</c>'s steps and `setup`, and <c>HygieneReadyGate</c>'s
    /// `ready` block, all run through <c>ctx.Cheats</c>).
    ///
    /// WHY THE BRIDGE AND NOT THE THREE CALL SITES: one seam is fail-closed against a fourth call
    /// site somebody adds later.
    ///
    /// IT IS NOT EVERY WRITE, AND SAYING SO WAS THE DEFECT (audit m9). Two writes into the
    /// studio's game never touch this interface, and are gated at their own call sites in
    /// <c>AdDirector</c> against the same <c>levers.json</c>: a shot's <c>timeScale</c> step
    /// (<see cref="TimeScaleLever"/>) and disabling a MonoBehaviour named in adapter.json's
    /// <c>overlayTypeNames</c> (<see cref="HideOverlayLever"/>). Driving the game's UI by NAME —
    /// a <c>click</c> or <c>hold</c> step — is not gated at all and never has been.
    ///
    /// WHAT IS DELIBERATELY NOT DECORATED: the relay's own <c>run-cheat</c>
    /// (<c>RelayServer.cs</c>), which calls <c>_env.Adapter().CheatBridge.Run</c> directly. That
    /// command was typed by the operator at the keyboard of that machine — exactly who F16 trusts.
    /// The relay's <c>run-shot</c> and <c>ready</c> are NOT exempt: they go through
    /// <c>AdDirector.Run</c>, which builds the decorated context.
    ///
    /// ONE LEVER STILL APPLIES TO THE OPERATOR, AND SAYING OTHERWISE WAS WRONG (third audit, M6):
    /// <c>camera-spec …</c>. Its check lives inside <c>CameraPose.Pose</c>, not in this decorator,
    /// because what it guards is not the typed command but the adapter.json <c>camera</c> block
    /// the pose READS — cloud content. So while the gate is live (either file under
    /// <c>Library/Nova/</c> is one the cloud delivered), an operator's own
    /// <c>run-cheat camera-pose …</c> is refused with <c>lever not approved on this machine:
    /// 'camera-spec …'</c> until that tick is given, whenever the block names a view type or a
    /// non-default method. Fail closed, and named.
    ///
    /// It re-reads levers.json on every call rather than caching: a tick in the window takes effect
    /// on the next command without a domain reload. WHETHER THE GATE IS LIVE is another question
    /// (the thirteenth audit, S1): answering it reads synced.json and hashes the delivered files —
    /// 3 ms at 512 KB, against 0.004 ms for the approval read — and asking it on every write froze
    /// the editor for 21.9 s over one delivered setup list. The director asks it once at the top of
    /// each shot attempt and holds the answer for that attempt (<see cref="HoldLiveness"/>), and once for a
    /// run's preamble (slice E.4's tutorial gate, stack pass 3); before any attempt (the ready gate, `show-ui`,
    /// `ui-dump`) it is asked on every write, as before.
    ///
    /// WHAT RUNS WITHOUT A TICK on a live gate: the kit's own three fixed verbs, exactly (<see cref="Levers.IsReadOnly"/>).
    /// Every other command a delivered file names — a `get` included, since the fourteenth audit — is refused here
    /// unless an entry of levers.json covers it, before it reaches the game's bridge.
    /// </summary>
    public sealed class LeverGateBridge : ICheatBridge
    {
        private readonly ICheatBridge _inner;
        private readonly string _projectRoot;
        private readonly Action<string>? _log;

        /// <summary>
        /// THIS RUN IS CLOUD CONTENT — gate it whatever the files on disk say (slice E.1).
        ///
        /// READONLY, AND SET WHERE THE OBJECT IS BUILT. There is no method that turns it on and
        /// none that turns it off, so no failure path, no exception and no early return can clear
        /// it: the value cannot come apart from the run it belongs to, because it IS the run's
        /// construction. A domain reload does not weaken that either — it destroys this object, and
        /// the agent that comes back builds a new director for the same probe on the same
        /// unconditional line.
        /// </summary>
        private readonly bool _cloudContent;

        /// <summary>How many refused commands this editor session remembers. A bound, because this
        /// is a static that lives until the domain reloads and a shot loop must not be able to grow
        /// it without end; the newest are kept.</summary>
        public const int MaxRefusedRemembered = 50;

        private static readonly List<string> Refused = new();

        /// <summary>
        /// THE COMMANDS THIS EDITOR SESSION REFUSED, newest last, distinct (second audit, M8).
        ///
        /// It exists so the Nova Capture window can list a command NOTHING on disk names: a studio
        /// that compiled its own adapter has that adapter's ready-gate and recovery commands gated
        /// as soon as one cloud file lands, and neither JSON file mentions them, so there was no
        /// row to tick and no way out except editing levers.json by hand. Memory only — it is never
        /// written to disk, and it says what was ASKED for, never what is allowed.
        /// </summary>
        public static IReadOnlyList<string> RefusedThisSession
        {
            get { lock (Refused) return Refused.ToArray(); }
        }

        /// <summary>Forget the session's refusals — for the tests, which share one editor.</summary>
        internal static void ForgetRefused()
        {
            lock (Refused) Refused.Clear();
        }

        private static void Remember(string? command)
        {
            if (string.IsNullOrWhiteSpace(command)) return;
            lock (Refused)
            {
                Refused.Remove(command!);
                Refused.Add(command!);
                while (Refused.Count > MaxRefusedRemembered) Refused.RemoveAt(0);
            }
        }

        public LeverGateBridge(ICheatBridge inner, string projectRoot, Action<string>? log,
            bool cloudContent = false)
        {
            _inner = inner;
            _projectRoot = projectRoot;
            _log = log;
            _cloudContent = cloudContent;
        }

        /// <summary>The bridge underneath, for the seam's own tests.</summary>
        public ICheatBridge Inner => _inner;

        /// <summary>Whether the gate is live, as held for this shot attempt, or asked now — with this run's cloud flag
        /// (slice E.1): <see cref="Levers.GateActiveFor"/>, so a cloud-content run is gated whatever the files say, held
        /// or not.</summary>
        private bool? _liveThisAttempt;

        private bool Live => _liveThisAttempt ?? Levers.GateActiveFor(_projectRoot, _cloudContent);

        /// <summary>Learn-and-drive v3 (spike A part 2): the same liveness, for the director's press guard
        /// (<see cref="PressGuard"/>) — a press is gated exactly when a write would be.</summary>
        internal bool IsLive => Live;

        /// <summary>
        /// ONE HASH PER SHOT ATTEMPT, NOT PER WRITE (the thirteenth audit, S1). Asks <see cref="Levers.GateActiveFor"/> (with
        /// this run's cloud flag, which answers without a hash) once and holds the answer until the returned scope is
        /// disposed; <see cref="AdDirector"/> holds it for each attempt of a shot, and for a run's preamble (slice E.4, stack
        /// pass 3). The holds never nest (the preamble ends before any shot starts): disposing one clears the held answer.
        /// Only "is the gate live" is held: the approval list is still read for every command, so a tick or an untick takes
        /// effect on the next command, as before. What waits for the next attempt is a change to whether the gate is live at
        /// all — a delivery landing, or synced.json, shots.json or adapter.json edited by hand, mid-attempt.
        /// </summary>
        internal IDisposable HoldLiveness()
        {
            _liveThisAttempt = Levers.GateActiveFor(_projectRoot, _cloudContent);
            _holding = true;
            _tickListThisAttempt = null;
            return new LivenessHold(this);
        }

        private sealed class LivenessHold : IDisposable
        {
            private readonly LeverGateBridge _bridge;
            public LivenessHold(LeverGateBridge bridge) => _bridge = bridge;
            public void Dispose()
            {
                _bridge._liveThisAttempt = null;
                _bridge._holding = false;
                _bridge._tickListThisAttempt = null;
            }
        }

        /// <summary>
        /// v3 P2 — THE TICK LIST, READ ONCE PER ATTEMPT (the thirteenth audit's rule, S1: a per-write file read and hash froze
        /// the editor for 21.9 s over one delivered setup list). The risk rule asks it for a ticked command whose own words
        /// are quiet; while an attempt holds its liveness (<see cref="HoldLiveness"/>) the list is parsed at most once and
        /// kept to the end of that attempt, and outside a hold it is read per command, as the liveness is. A new tick list
        /// landing mid-attempt takes effect at the next attempt — it can only RAISE a risk, so what waits is a refusal.
        /// </summary>
        private bool _holding;
        private IReadOnlyList<TickList.Candidate>? _tickListThisAttempt;

        private IReadOnlyList<TickList.Candidate> Delivered() =>
            _holding ? _tickListThisAttempt ??= TickList.Read(_projectRoot) : TickList.Read(_projectRoot);

        /// <summary>
        /// May this lever run now? <see cref="Levers.LeverAllowed"/>'s question — for a write that does not come through
        /// <see cref="Run"/>, a shot's <c>timeScale</c> step — asked under the liveness held for this attempt.
        /// </summary>
        internal bool LeverAllowed(string lever) => !Live || Levers.Allows(Levers.Approved(_projectRoot), lever);

        /// <summary>Whether the last <see cref="Run"/> was refused by this gate (not by the game): the ready gate reads it to
        /// say "not ticked" rather than "could not read" (the fourteenth audit, ruling 3).</summary>
        internal bool LastRunRefused => LastRefusal != GateRefusal.None;

        /// <summary>WHY the last <see cref="Run"/> was refused by this gate, or <see cref="GateRefusal.None"/> (the P2 review,
        /// finding 2): a reader that says why must not say "not ticked" of a command that IS ticked and was held back as
        /// risky, or fenced by the press guard (invariant 99).</summary>
        internal GateRefusal LastRefusal { get; private set; }

        /// <summary>The lever the last refusal named — the command itself, or the <c>camera-spec …</c> a
        /// <c>camera-pose</c> needs — or null.</summary>
        internal string? LastRefusedLever { get; private set; }

        /// <summary>The last refusal's own words: the risk's why (<see cref="CheatRisk.Why"/>) or the press guard's sentence;
        /// null for a lever that is not ticked.</summary>
        internal string? LastRefusalDetail { get; private set; }

        private bool Refuse(GateRefusal kind, string? lever, string? detail, string logLine)
        {
            LastRefusal = kind;
            LastRefusedLever = lever;
            LastRefusalDetail = detail;
            _log?.Invoke(logLine);
            return false;
        }

        public bool Run(string command)
        {
            LastRefusal = GateRefusal.None;
            LastRefusedLever = null;
            LastRefusalDetail = null;
            if (!Live) return _inner.Run(command);
            if (Levers.IsReadOnly(command)) return _inner.Run(command);
            var approved = Levers.ApprovedAndNonProduction(_projectRoot, out var nonProduction);
            // v3 P2: `Allows` is EXACT MEMBERSHIP — the command is an entry of levers.json, character for character. A
            // template covers nothing since P2 (Levers.IsTemplate), so the cloud cannot fill in a ticked command's
            // arguments; that holds for the bridge's own press verbs too (`tap-at`, `drag`, `click` …), which are
            // cheats here and need their own exact tick. ONE exception since Fix 4 (invariant 182): a ticked set-value
            // template `set Type.Member {v}` lets `set Type.Member <one value>` through (Levers.IsSetValueTemplate).
            // A COMMAND THAT RUNS NEVER HOLDS A PLACEHOLDER (the Fix 4 audit, H1): a ticked set-value template covers its
            // own text and every template of its shape, so without this `set A.b {v}` itself would run and write the
            // text "{v}" into a string member. Refused before any covering is asked — a template is approved, never run.
            if (ShotBinding.HasPlaceholder(command))
            {
                // remembered as before: its row in the window shows why it cannot be ticked
                Remember(command);
                return Refuse(GateRefusal.Template, command, null, Levers.TemplateNeverRunsLog(command));
            }
            if (Levers.Allows(approved, command))
            {
                // v3 P2 (§3.1): a RISKY ticked cheat waits for the project's non-production tick.
                if (RiskyHere(command, nonProduction) is { } why)
                    return Refuse(GateRefusal.RiskyNotYet, command, why, CheatRisk.NotYetLog(command, why));
                // v3 P2 (§4): a bridge press BY NAME is fenced like a `click` step — a risky word is never pressed in a
                // gated run, ticked or not (PressGuard). Coordinate verbs have no name to read: exact membership only.
                if (PressVerbTarget(command) is { } target && PressGuard.Refusal(target, null) is { } pressRefused)
                    return Refuse(GateRefusal.PressGuard, command, pressRefused, pressRefused);
                var spec = CameraSpecLeverFor(command);
                if (spec == null) return _inner.Run(command);
                if (!Levers.Allows(approved, spec))
                {
                    Remember(spec);
                    return Refuse(GateRefusal.NotTicked, spec, null, Levers.NotApprovedLog(spec));
                }
                // The P2 review, finding 1: the spec's METHOD names meet the risk rule too (a ticked
                // `camera-spec Rig SetPosition DeleteSave SetFov` waits for the non-production tick).
                if (RiskyHere(spec, nonProduction) is { } specWhy)
                    return Refuse(GateRefusal.RiskyNotYet, spec, specWhy, CheatRisk.NotYetLog(spec, specWhy));
                return _inner.Run(command);
            }
            // FALSE, not an exception: the director fails the step, logs it and carries on, which
            // is how every other refused write behaves here.
            Remember(command);
            return Refuse(GateRefusal.NotTicked, command, null, Levers.UntickedLog(approved, command ?? ""));
        }

        /// <summary>The risk rule for a TICKED lever on this project now: words first — no disk — and the tick list (held
        /// for the attempt) only when the words are quiet. Null when the project is ticked non-production.</summary>
        private string? RiskyHere(string lever, bool nonProduction) =>
            nonProduction ? null : Levers.RiskyNotYet(lever, false, null) ?? Levers.RiskyNotYet(lever, false, Delivered());

        /// <summary>
        /// Audit M3 — A <c>camera-pose</c> ALSO RUNS THE METHODS adapter.json's <c>camera</c> BLOCK
        /// NAMES, and those need their own <c>camera-spec</c> lever (<see cref="Levers.CameraSpecLever"/>).
        /// <see cref="CameraPose.Pose"/> checks it, but through <see cref="Levers.LeverAllowed"/>
        /// without this run's cloud flag — so during a try on a project with its own files it
        /// answered "ungated". Checked here, where the flag is, against the same adapter.json
        /// <c>CameraPose</c> reads. Null = nothing more is needed.
        /// </summary>
        /// <summary>The target of a bridge press BY NAME — <c>click …</c> or <c>invoke-button …</c> — as the words after the
        /// verb (a trailing index is digits, which hold no word), or null for any other verb.</summary>
        internal static string? PressVerbTarget(string? command)
        {
            var text = (command ?? "").Trim();
            var space = text.IndexOfAny(new[] { ' ', '\t' });
            if (space < 0) return null;
            var verb = text.Substring(0, space);
            if (!string.Equals(verb, "click", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(verb, "invoke-button", StringComparison.OrdinalIgnoreCase))
                return null;
            return text.Substring(space + 1).Trim();
        }

        /// <summary>The <c>camera-spec …</c> lever a <c>camera-pose</c> command needs on this project, or null (another verb,
        /// or a camera block that says nothing).</summary>
        private string? CameraSpecLeverFor(string? command)
        {
            var verb = (command ?? "").TrimStart().Split(' ', '\t')[0];
            if (!string.Equals(verb, "camera-pose", StringComparison.OrdinalIgnoreCase)) return null;
            var camera = AdapterJson.Load(_projectRoot).Camera;
            if (camera == null || !Levers.CameraSpecNeeded(camera.Value)) return null;
            return Levers.CameraSpecLever(camera.Value);
        }
    }
}
