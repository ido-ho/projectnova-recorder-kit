using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Learn-and-drive v3 P4 — what a <c>teach</c> CLAIM hands the kit: the request a person reads in the Nova Capture
    /// window, where the recipe starts, and the export's names arrival is counted against. Mirror of
    /// <c>StudioTeachClaim</c> (<c>apps/api/src/studio/studio-capture-job.ts</c>). Read by one function, never throws.
    /// </summary>
    public sealed class TeachRequest
    {
        public const int MaxAsk = 200;
        public const int MaxNames = 20000;

        public string Ask { get; }
        public JObject DeclaredStart { get; }
        public HashSet<string> AuthoredNames { get; }

        public TeachRequest(string ask, JObject declaredStart, IEnumerable<string> authoredNames)
        {
            Ask = ask;
            DeclaredStart = declaredStart;
            AuthoredNames = new HashSet<string>(authoredNames, StringComparer.Ordinal);
        }

        public const string MissingReason =
            "this teach arrived without its request (no complete `teach` block on the claim) — nothing was recorded";

        public string StartKind => DeclaredStart["kind"]?.Value<string>() ?? TeachAnalysis.StartLobby;

        /// <summary>The <c>teach</c> block of a claim reply, or null.</summary>
        public static TeachRequest? FromClaim(string claimBody)
        {
            try { return FromJson(NovaJson.ParseObject(claimBody)["teach"]); }
            catch (Exception) { return null; }
        }

        public static TeachRequest? FromJson(JToken? t)
        {
            if (t is not JObject o) return null;
            var ask = o["ask"]?.Type == JTokenType.String ? o["ask"]!.Value<string>()!.Trim() : "";
            if (ask.Length == 0 || ask.Length > MaxAsk) return null;
            if (o["declaredStart"] is not JObject ds) return null;
            var kind = ds["kind"]?.Type == JTokenType.String ? ds["kind"]!.Value<string>() : null;
            JObject start;
            if (kind == TeachAnalysis.StartLobby) start = new JObject { ["kind"] = kind };
            else if (kind == TeachAnalysis.StartRecipe && ds["recipeId"]?.Type == JTokenType.String
                     && System.Text.RegularExpressions.Regex.IsMatch(ds["recipeId"]!.Value<string>()!, "^r[0-9a-f]{16}$"))
                start = new JObject { ["kind"] = kind, ["recipeId"] = ds["recipeId"]!.Value<string>() };
            else return null;
            if (o["authoredNames"] is not JArray names || names.Count > MaxNames) return null;
            if (names.Any(n => n.Type != JTokenType.String)) return null;
            return new TeachRequest(ask, start, names.Select(n => n.Value<string>()!));
        }

        public JObject ToJson() => new()
        {
            ["ask"] = Ask,
            ["declaredStart"] = DeclaredStart.DeepClone(),
            ["authoredNames"] = new JArray(AuthoredNames.OrderBy(n => n, StringComparer.Ordinal)),
        };
    }

    /// <summary>
    /// A moment read off the editor's two clocks (v3 P4): the wall clock, and <c>EditorApplication.timeSinceStartup</c>
    /// (<see cref="Mono"/>) with the editor session it was read in. Waits measured inside one editor session use the
    /// monotonic clock, so a wall-clock jump (NTP, a sleep/wake, a person changing the time) can neither skip the
    /// restart rule's settle wait nor stretch a bound; across sessions (timeSinceStartup restarts at 0) the wall clock is
    /// all there is. Rides the progress file; never throws.
    /// </summary>
    public sealed class ClockStamp
    {
        public DateTime Utc { get; }
        /// <summary><c>timeSinceStartup</c> when read, or NaN when unknown (then only <see cref="Utc"/> counts).</summary>
        public double Mono { get; }
        /// <summary>The editor session <see cref="Mono"/> was read in, or null.</summary>
        public string? Session { get; }

        public ClockStamp(DateTime utc, double mono = double.NaN, string? session = null)
        {
            Utc = utc;
            Mono = mono;
            Session = session;
        }

        public ClockStamp AddSeconds(double sec) => new(Utc.AddSeconds(sec), Mono + sec, Session);

        private bool SameClock(ClockStamp o) =>
            Session != null && Session == o.Session && IsReading(Mono) && IsReading(o.Mono);

        private static bool IsReading(double d) => !double.IsNaN(d) && !double.IsInfinity(d);

        /// <summary>Seconds from this moment to <paramref name="later"/> (negative when it is earlier): the monotonic clock
        /// when both were read in the same editor session, else the wall clock.</summary>
        public double SecondsTo(ClockStamp later) => SameClock(later) ? later.Mono - Mono : (later.Utc - Utc).TotalSeconds;

        public JObject ToJson()
        {
            var o = new JObject { ["utc"] = Utc.ToString("o") };
            if (IsReading(Mono)) o["mono"] = Mono;
            if (Session != null) o["session"] = Session;
            return o;
        }

        public static ClockStamp? FromJson(JToken? t)
        {
            try
            {
                if (t is not JObject o || o["utc"]?.Type != JTokenType.String) return null;
                if (!DateTime.TryParse(o["utc"]!.Value<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var utc))
                    return null;
                var mono = o["mono"]?.Type is JTokenType.Float or JTokenType.Integer ? o["mono"]!.Value<double>() : double.NaN;
                var session = o["session"]?.Type == JTokenType.String ? o["session"]!.Value<string>() : null;
                return new ClockStamp(utc.ToUniversalTime(), mono, session);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Learn-and-drive v3 P4 — WHERE A TEACH IS, persisted on the job's progress file, because entering and leaving Play
    /// Mode reloads the domain and the agent that comes back must carry on from the same step. Pure data + transitions;
    /// the agent (<c>NovaCaptureAgent.RunTeach</c>) does the Unity half.
    ///
    /// The phases, in order:
    /// <list type="number">
    /// <item><c>waiting</c> — the request is shown in the Nova Capture window; nothing happens in the game until a person
    /// presses Teach (or Not now, or the wait runs out: <see cref="TeachJob.MaxWaitSec"/>).</item>
    /// <item><c>restart</c> — "lobby after boot" is a FRESH Play session: leave Play Mode if it is on, wait the settle time
    /// after the LAST Play Mode exit, whoever caused it (the restart rule, v3 §3.6 — native plugins crash on fast
    /// restarts), enter Play Mode.</item>
    /// <item><c>boot</c> — wait for the game to come up, then start recording (<see cref="NextAfterBoot"/> = record) or
    /// run the confirming replay (= replay).</item>
    /// <item><c>recording</c> — the person plays to the screen asked for and presses Stop.</item>
    /// <item><c>review</c> — the recipe is SHOWN in the window (steps, popup closes, arrival, confirmed or not and why);
    /// the person presses Replay (the confirming replay runs only on this press), Upload, Discard, or Teach again.</item>
    /// <item><c>posting</c> — the facts go up (<see cref="FactsJson"/>) and the job reports done.</item>
    /// </list>
    /// Restart and boot happen only because a person pressed Teach or Replay IN THIS EDITOR SESSION (<see cref="Session"/>):
    /// a teach found there after an editor restart, or after longer than <see cref="TeachJob.StartBoundSec"/>, goes back to
    /// waiting or review and never enters Play Mode on its own (<see cref="TeachJob.StaleStart"/>).
    /// </summary>
    public sealed class TeachState
    {
        public const string Waiting = "waiting";
        public const string Restart = "restart";
        public const string Boot = "boot";
        public const string Recording = "recording";
        public const string Review = "review";
        public const string Posting = "posting";

        public const string AfterBootRecord = "record";
        public const string AfterBootReplay = "replay";

        public TeachRequest Request { get; }
        public string Phase { get; set; } = Waiting;
        public string NextAfterBoot { get; set; } = AfterBootRecord;
        public DateTime WaitStartedUtc { get; set; }
        public DateTime? PhaseStartedUtc { get; set; }
        /// <summary>When the restart may enter Play Mode (set when it left Play Mode).</summary>
        public ClockStamp? SettleUntil { get; set; }
        /// <summary>When the current restart or boot began, on both clocks — what the stale bound and the boot's own waits
        /// count from (<see cref="TeachJob.StaleStart"/>). Set at every entry into restart or boot.</summary>
        public ClockStamp? StartedAt { get; set; }
        public JArray Presses { get; set; } = new();
        public List<string> StartRoots { get; set; } = new();
        public int Cancelled { get; set; }
        public int Dropped { get; set; }
        public string? GameCommit { get; set; }
        public JObject? Replay { get; set; }
        /// <summary>A sentence for the window (why the recording ended, what the replay met).</summary>
        public string? Note { get; set; }
        /// <summary>The person's answer, set in review: taught (Upload) or discarded.</summary>
        public string? Outcome { get; set; }
        /// <summary>Play Mode entries since the game last came up — a project that cannot enter Play Mode (compile
        /// errors) would otherwise restart forever (<see cref="TeachJob.MaxPlayStarts"/>).</summary>
        public int PlayStarts { get; set; }
        /// <summary>The editor session (<c>SessionState</c>: it survives Play Mode's domain reloads, not an editor restart)
        /// whose press put the teach into restart/boot. Stamped by <see cref="TeachJob.StartFromPress"/>.</summary>
        public string? Session { get; set; }
        /// <summary>The last time Play Mode ended in this editor, as the teach last saw it — the settle wait counts from it.</summary>
        public ClockStamp? LastPlayExit { get; set; }
        /// <summary>The Play session now on is the one this teach entered (not the person's own) — it is left when the teach
        /// ends or is cancelled. Trusted only with <see cref="EnteredPlayAt"/> from THIS editor session and no Play Mode exit
        /// noted since (<see cref="TeachJob.ForgetForeignPlay"/>).</summary>
        public bool EnteredPlay { get; set; }
        /// <summary>When (and in which editor session) the teach entered the Play session <see cref="EnteredPlay"/> names.</summary>
        public ClockStamp? EnteredPlayAt { get; set; }

        public TeachState(TeachRequest request) => Request = request;

        public JObject ToJson()
        {
            var o = new JObject
            {
                ["request"] = Request.ToJson(),
                ["phase"] = Phase,
                ["nextAfterBoot"] = NextAfterBoot,
                ["waitStartedUtc"] = WaitStartedUtc.ToString("o"),
                ["presses"] = Presses.DeepClone(),
                ["startRoots"] = new JArray(StartRoots),
                ["cancelled"] = Cancelled,
                ["dropped"] = Dropped,
            };
            if (PhaseStartedUtc != null) o["phaseStartedUtc"] = PhaseStartedUtc.Value.ToString("o");
            if (SettleUntil != null) o["settleUntil"] = SettleUntil.ToJson();
            if (StartedAt != null) o["startedAt"] = StartedAt.ToJson();
            if (GameCommit != null) o["gameCommit"] = GameCommit;
            if (Replay != null) o["replay"] = Replay.DeepClone();
            if (Note != null) o["note"] = Note;
            if (Outcome != null) o["outcome"] = Outcome;
            if (PlayStarts > 0) o["playStarts"] = PlayStarts;
            if (Session != null) o["session"] = Session;
            if (LastPlayExit != null) o["lastPlayExit"] = LastPlayExit.ToJson();
            if (EnteredPlay) o["enteredPlay"] = true;
            if (EnteredPlayAt != null) o["enteredPlayAt"] = EnteredPlayAt.ToJson();
            return o;
        }

        private static DateTime? Utc(JToken? t) =>
            t?.Type == JTokenType.String && DateTime.TryParse(t.Value<string>(), null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d.ToUniversalTime() : null;

        private static readonly HashSet<string> Phases = new() { Waiting, Restart, Boot, Recording, Review, Posting };

        /// <summary>Never throws: anything unreadable is null (the agent then refuses the job by name).</summary>
        public static TeachState? FromJson(JToken? t)
        {
            try
            {
                if (t is not JObject o) return null;
                var req = TeachRequest.FromJson(o["request"]);
                if (req == null) return null;
                var phase = o["phase"]?.Value<string>() ?? Waiting;
                if (!Phases.Contains(phase)) return null;
                return new TeachState(req)
                {
                    Phase = phase,
                    NextAfterBoot = o["nextAfterBoot"]?.Value<string>() == AfterBootReplay ? AfterBootReplay : AfterBootRecord,
                    WaitStartedUtc = Utc(o["waitStartedUtc"]) ?? DateTime.UtcNow,
                    PhaseStartedUtc = Utc(o["phaseStartedUtc"]),
                    SettleUntil = ClockStamp.FromJson(o["settleUntil"]),
                    StartedAt = ClockStamp.FromJson(o["startedAt"]),
                    Presses = o["presses"] as JArray ?? new JArray(),
                    StartRoots = (o["startRoots"] as JArray)?.Select(x => x.Value<string>() ?? "").ToList() ?? new List<string>(),
                    Cancelled = o["cancelled"]?.Value<int>() ?? 0,
                    Dropped = o["dropped"]?.Value<int>() ?? 0,
                    GameCommit = o["gameCommit"]?.Type == JTokenType.String ? o["gameCommit"]!.Value<string>() : null,
                    Replay = o["replay"] as JObject,
                    Note = o["note"]?.Type == JTokenType.String ? o["note"]!.Value<string>() : null,
                    Outcome = o["outcome"]?.Type == JTokenType.String ? o["outcome"]!.Value<string>() : null,
                    PlayStarts = Math.Max(0, o["playStarts"]?.Value<int>() ?? 0),
                    Session = o["session"]?.Type == JTokenType.String ? o["session"]!.Value<string>() : null,
                    LastPlayExit = ClockStamp.FromJson(o["lastPlayExit"]),
                    EnteredPlay = o["enteredPlay"]?.Type == JTokenType.Boolean && o["enteredPlay"]!.Value<bool>(),
                    EnteredPlayAt = ClockStamp.FromJson(o["enteredPlayAt"]),
                };
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The analysis of what was recorded (and replayed) — what the window shows in review.</summary>
        public TeachAnalysis.Result Analysis() =>
            TeachAnalysis.Analyse(Presses, Request.AuthoredNames, Replay, Request.DeclaredStart);
    }

    /// <summary>The person's buttons in the Nova Capture window, and what each does to a teach.</summary>
    public enum TeachAction { None, Teach, Stop, Upload, Discard, TeachAgain, NotNow, Cancel, Replay }

    /// <summary>
    /// Learn-and-drive v3 P4 — THE TEACH JOB'S RULES, pure (the agent runs the Unity half): which button is allowed in which
    /// phase, what Stop leads to, the caps, and the facts that go up.
    /// </summary>
    public static class TeachJob
    {
        /// <summary>How long a claimed request waits for a person to press Teach before the job ends (the editor runs one
        /// job at a time; a request nobody answers must not hold it for the day).</summary>
        public const double MaxWaitSec = 30 * 60;
        /// <summary>A recording nobody stopped is stopped here and goes to review (the person can still Discard).</summary>
        public const double MaxRecordingSec = 20 * 60;
        /// <summary>How long after entering Play Mode the game is given to come up before the recording or replay starts,
        /// when no adapter says it is ready.</summary>
        public const double BootSettleSec = 8;
        /// <summary>The restart rule's default settle wait after leaving Play Mode (v3 §3.6); the window can change it.</summary>
        public const double DefaultRestartSettleSec = 30;
        /// <summary>How long past <see cref="BootSettleSec"/> the teach waits for a game adapter to register before it
        /// goes on without one (a project before its first Send often has none — the teach needs none).</summary>
        public const double BootAdapterWaitSec = 20;
        /// <summary>Play Mode entries in a row that never brought the game up before the job ends — the probe's rule for a
        /// project that cannot enter Play Mode (compile errors), which would otherwise restart on every poll.</summary>
        public const int MaxPlayStarts = 3;

        /// <summary>Why Play Mode must not be entered again for this teach, or null (the count is of entries since the game
        /// last came up).</summary>
        public static string? PlayStartRefusal(TeachState s) =>
            s.PlayStarts >= MaxPlayStarts
                ? $"Play Mode was entered {s.PlayStarts} times and the game never came up (compile errors, or Play Mode ending by itself) — fix that and ask again"
                : null;

        /// <summary>The game came up: the count of failed entries starts again.</summary>
        public static void Booted(TeachState s) => s.PlayStarts = 0;

        public const string NotNowReason = "the person pressed Not now in the Nova Capture window — nothing was recorded";
        public static string WaitExpiredReason =>
            $"nobody pressed Teach in the Nova Capture window within {MaxWaitSec / 60:0} minutes — ask again when someone is at the editor";

        /// <summary>Why <paramref name="action"/> cannot be taken in the state's phase, or null. The window greys a button
        /// with the same function the agent asks, so the two cannot disagree.</summary>
        /// <paramref name="recorderBusy"/> is <c>TeachRecorder.IsTeaching</c>: in waiting and review this teach's own
        /// recorder is never on, so busy there is a teach started from the relay (a terminal session) — the two never share
        /// the one recorder.</summary>
        public static string? Refusal(TeachState s, TeachAction action, bool playing = true, bool recorderBusy = false) => action switch
        {
            TeachAction.Teach => s.Phase != TeachState.Waiting ? "Teach starts a recording from the request's start — it is not waiting"
                : recorderBusy ? RelayRecordingReason
                // a start from another recipe is reached by the person (a teach does not replay chains — deferred; a Try and a capture do): Teach records from where they are
                : s.Request.StartKind == TeachAnalysis.StartRecipe && !playing
                    ? "this recipe starts where another one ends — enter Play Mode and play to that point first, then press Teach"
                    : null,
            TeachAction.NotNow => s.Phase == TeachState.Waiting ? null : "Not now answers a request that is waiting",
            TeachAction.Stop => s.Phase == TeachState.Recording ? null : "nothing is being recorded",
            TeachAction.Upload => s.Phase != TeachState.Review ? "there is no recipe to upload yet"
                : s.Presses.Count == 0 ? "nothing was pressed — Teach again or Discard" : null,
            TeachAction.Discard => s.Phase == TeachState.Review ? null : "there is no recipe to discard",
            TeachAction.TeachAgain => s.Phase == TeachState.Review ? null : "there is no recipe to redo",
            // before anything is pressed in the game: restart, or boot before its replay began (a replay runs to its end)
            TeachAction.Cancel => s.Phase == TeachState.Restart || (s.Phase == TeachState.Boot && s.Replay == null) ? null
                : "Cancel stops a teach that is starting, before anything is recorded or pressed",
            TeachAction.Replay => s.Phase != TeachState.Review ? "there is no recipe to replay"
                : s.Replay != null ? "this recipe was already replayed, or cannot be (a replay is run once): " + (s.Analysis().Why ?? "")
                : s.Presses.Count == 0 ? "nothing was pressed — Teach again or Discard"
                : s.Analysis().ReplayBlocked is { } blocked ? "not replayed: " + blocked
                : recorderBusy ? RelayRecordingReason
                : null,
            _ => "no action",
        };

        public const string RelayRecordingReason =
            "a teach started from the relay (a terminal session's teach-start) is recording — stop it there (teach-stop) first";

        /// <summary>Why a relay <c>teach-start</c> is refused, or null: while a teach job from the website is in this editor
        /// it owns the one recorder — a relay start would record into its restart or its recording.</summary>
        public static string? RelayTeachRefusal(bool windowTeachActive) => windowTeachActive
            ? "a teach job from the website is in the Nova Capture window (Tools › Recorder Kit › Nova Capture) — finish, " +
              "discard or cancel it there (Not now ends a waiting one) first; the relay's teach-start would share its recorder"
            : null;

        /// <summary>Why a relay <c>teach-stop</c> is refused, or null: only while the window's OWN recording is on (a relay
        /// stop would end it). A relay teach started before the website's request stays stoppable from the relay, so the
        /// two refusals can never point at each other.</summary>
        public static string? RelayTeachStopRefusal(bool windowRecording) => windowRecording
            ? "the Nova Capture window's teach is recording — press Stop there; the relay's teach-stop would end its recording"
            : null;

        /// <summary>How long restart or boot may last before the teach stops trusting it and goes back (<see cref="StaleStart"/>):
        /// the settle wait, or the boot's own waits, plus <see cref="StartSlackSec"/> for the domain reloads between.</summary>
        public const double StartSlackSec = 120;

        public static double StartBoundSec(TeachState s, double settleSec) =>
            (s.Phase == TeachState.Restart ? Math.Max(0, settleSec) : BootSettleSec + BootAdapterWaitSec) + StartSlackSec;

        /// <summary>
        /// A press (Teach in waiting, Replay in review) starts a fresh Play session: restart (or, for a start from another
        /// recipe, boot at once in the person's own Play session), stamped with THIS editor session and the time — the two
        /// things <see cref="StaleStart"/> checks. Returns false when the press does not start anything.
        /// </summary>
        public static bool StartFromPress(TeachState s, TeachAction action, string session, DateTime nowUtc,
            double nowMono = double.NaN)
        {
            var now = new ClockStamp(nowUtc, nowMono, session);
            if (action == TeachAction.Teach && s.Phase == TeachState.Waiting)
            {
                s.Note = null;
                s.NextAfterBoot = TeachState.AfterBootRecord;
                if (s.Request.StartKind == TeachAnalysis.StartRecipe)
                {
                    // a start from another recipe: the person brought the game there; record from here
                    s.Phase = TeachState.Boot;
                    s.PhaseStartedUtc = nowUtc.AddSeconds(-BootSettleSec);
                    s.StartedAt = now.AddSeconds(-BootSettleSec);
                }
                else
                {
                    s.Phase = TeachState.Restart;
                    s.PhaseStartedUtc = nowUtc;
                    s.StartedAt = now;
                }
            }
            else if (action == TeachAction.Replay && s.Phase == TeachState.Review && s.Replay == null)
            {
                s.Note = null;
                s.Phase = TeachState.Restart;
                s.NextAfterBoot = TeachState.AfterBootReplay;
                s.PhaseStartedUtc = nowUtc;
                s.StartedAt = now;
            }
            else return false;
            s.SettleUntil = null;
            s.Session = session;
            return true;
        }

        /// <summary>
        /// THE CRASH-RESUME RULE (S1). A teach in restart or boot enters Play Mode and then records or replays — which must
        /// only ever follow a press. Found there after the editor restarted (its session is not the one that pressed), or
        /// after longer than <see cref="StartBoundSec"/> (the editor hung, or sat on a modal), it goes BACK: a recording to
        /// waiting (press Teach again), a replay to review (press Replay, Upload or Discard). Returns the sentence said, or
        /// null when the start may go on.
        /// </summary>
        public static string? StaleStart(TeachState s, string session, DateTime nowUtc, double settleSec,
            double nowMono = double.NaN)
        {
            if (s.Phase != TeachState.Restart && s.Phase != TeachState.Boot) return null;
            string why;
            if (s.Session == null || s.Session != session)
                why = "the editor was restarted while the teach was starting";
            else if (SinceStartSec(s, new ClockStamp(nowUtc, nowMono, session)) is not { } since || since > StartBoundSec(s, settleSec))
                why = $"the teach's {s.Phase} step took longer than {StartBoundSec(s, settleSec):0} s (the editor stalled)";
            else return null;
            GoBack(s, why, nowUtc);
            return s.Note;
        }

        /// <summary>Seconds since the current restart or boot began (<see cref="TeachState.StartedAt"/>, on the monotonic
        /// clock within one editor session), or null when the state carries no start.</summary>
        public static double? SinceStartSec(TeachState s, ClockStamp now)
        {
            var start = s.StartedAt ?? (s.PhaseStartedUtc is { } utc ? new ClockStamp(utc) : null);
            return start?.SecondsTo(now);
        }

        /// <summary>Back to before the press: a recording to waiting, a replay to review (an interrupted replay keeps its
        /// reason and is not run again — Cancel is refused once a replay began, so only <see cref="StaleStart"/> meets
        /// one). Nothing is recorded or pressed. <see cref="StaleStart"/> and Cancel.</summary>
        public static void GoBack(TeachState s, string why, DateTime nowUtc)
        {
            if (s.NextAfterBoot == TeachState.AfterBootReplay)
            {
                s.Phase = TeachState.Review;
                s.PhaseStartedUtc = nowUtc;
                s.SettleUntil = null;
                s.StartedAt = null;
                s.Note = s.Replay != null
                    ? why + " — the replay was interrupted and is not run again; Teach again, or Upload it unconfirmed / Discard"
                    : why + " — nothing was pressed in the game; press Replay to confirm, or Upload / Discard";
            }
            else
                BackToWaiting(s, why + " — nothing was recorded; press Teach again", nowUtc);
            s.Session = null;
            s.PlayStarts = 0;
        }

        /// <summary>
        /// Play Mode ended at boot, before the step ran. A start from another recipe CANNOT be honoured by entering Play
        /// Mode from the lobby (M5): the teach fails with the reason (returned) rather than record under the wrong start.
        /// A replay that had begun is not run again (one attempt); otherwise a fresh restart.
        /// </summary>
        public static string? PlayEndedAtBoot(TeachState s, DateTime nowUtc, double nowMono = double.NaN)
        {
            s.EnteredPlay = false;
            s.EnteredPlayAt = null;
            if (s.NextAfterBoot == TeachState.AfterBootRecord && s.Request.StartKind == TeachAnalysis.StartRecipe)
                return "this recipe starts where another one ends, and Play Mode was off when the recording was to begin — " +
                       "the lobby cannot stand in for that start, so nothing was recorded; ask again, and press Teach from that point in Play Mode";
            if (s.NextAfterBoot == TeachState.AfterBootReplay && s.Replay != null) s.Phase = TeachState.Review;
            else s.Phase = TeachState.Restart;
            s.PhaseStartedUtc = nowUtc;
            s.StartedAt = new ClockStamp(nowUtc, nowMono, s.Session);
            return null;
        }

        /// <summary>The restart enters Play Mode: boot begins now, and the Play session about to start is the teach's own —
        /// stamped with this editor session and both clocks (<see cref="ForgetForeignPlay"/>).</summary>
        public static void EnteringPlay(TeachState s, ClockStamp now)
        {
            s.PlayStarts++;
            s.Phase = TeachState.Boot;
            s.PhaseStartedUtc = now.Utc;
            s.StartedAt = now;
            s.EnteredPlay = true;
            s.EnteredPlayAt = now;
        }

        /// <summary>The restart left Play Mode itself: Play Mode is not entered again before the settle time from now.</summary>
        public static void TeachLeftPlay(TeachState s, ClockStamp now, double settleSec) =>
            s.SettleUntil = now.AddSeconds(Math.Max(0, settleSec));

        /// <summary>
        /// THE SETTLE RULE (B1, v3 §3.6): Play Mode is entered again no sooner than the settle time after the LAST exit —
        /// whoever ended it (the teach, the person seconds before pressing Teach, the game itself at boot). Null when there
        /// is nothing to wait for. Returns the seconds still to wait (zero or less: go), measured on the monotonic clock
        /// within one editor session so a wall-clock jump cannot skip it; the wall clock only across sessions.
        /// </summary>
        public static double? SettleRemainingSec(TeachState s, ClockStamp? lastExit, double settleSec, ClockStamp now)
        {
            double? left = null;
            if (Later(s.LastPlayExit, lastExit) is { } exit)
                left = Math.Max(0, settleSec) - exit.SecondsTo(now);
            if (s.SettleUntil != null)
            {
                var toUntil = now.SecondsTo(s.SettleUntil);
                left = left == null ? toUntil : Math.Max(left.Value, toUntil);
            }
            return left;
        }

        private static ClockStamp? Later(ClockStamp? a, ClockStamp? b) =>
            a == null ? b : b == null ? a : a.SecondsTo(b) > 0 ? b : a;

        /// <summary>The teach saw a Play Mode exit (the editor's own record of the last one): kept on the state so it rides
        /// the progress file. True when that changed the state.</summary>
        public static bool NotePlayExit(TeachState s, ClockStamp? lastExit)
        {
            var later = Later(s.LastPlayExit, lastExit);
            if (ReferenceEquals(later, s.LastPlayExit)) return false;
            s.LastPlayExit = later;
            return true;
        }

        /// <summary>
        /// <see cref="TeachState.EnteredPlay"/> outlives the Play session it names when the editor restarts (crash, reopen)
        /// or when the person stops and restarts Play between two polls. It is forgotten unless it was stamped in THIS
        /// editor session and no Play Mode exit has been noted since (<see cref="NotePlayExit"/> first) — otherwise the
        /// teach would end, cancel or go stale by leaving the PERSON's Play session. Forgetting wrongly only means the teach
        /// leaves its own session on (the person stops it); trusting wrongly throws the person out, so doubt forgets.
        /// True when that changed the state.
        /// </summary>
        public static bool ForgetForeignPlay(TeachState s, string session)
        {
            if (!s.EnteredPlay) return false;
            var at = s.EnteredPlayAt;
            var ours = at != null && at.Session != null && at.Session == session
                       && (s.LastPlayExit == null || at.SecondsTo(s.LastPlayExit) <= 0);
            if (ours) return false;
            s.EnteredPlay = false;
            s.EnteredPlayAt = null;
            return true;
        }

        /// <summary>When the teach job ends (done, failed, uploaded, discarded, lost), leave Play Mode if the session on is
        /// the one the teach entered — a person playing on their own when they press Not now is not thrown out (M2), nor
        /// one who started Play again after the teach's session ended, or after an editor restart
        /// (<see cref="ForgetForeignPlay"/>, applied here because some ends never pass the teach loop).</summary>
        public static bool LeavePlayOnEnd(TeachState? s, bool playing, string session, ClockStamp? lastExit)
        {
            if (s == null) return false;
            NotePlayExit(s, lastExit);
            ForgetForeignPlay(s, session);
            return playing && s.EnteredPlay;
        }

        /// <summary>The recording has stopped: keep what it recorded, and decide whether a confirming replay is worth running
        /// (<see cref="TeachAnalysis.Result.ReplayBlocked"/> — a teach that cannot be confirmed is never replayed into the
        /// studio's game) — the next phase is review either way: with the reason, or with the replay waiting for the
        /// person's Replay press.</summary>
        public static void AfterStop(TeachState s, JArray presses, IEnumerable<string> startRoots, int cancelled, int dropped,
            string? gameCommit, DateTime nowUtc)
        {
            s.Presses = presses;
            s.StartRoots = startRoots.ToList();
            s.Cancelled = cancelled;
            s.Dropped = dropped;
            s.GameCommit = gameCommit;
            s.Replay = null;
            var a = s.Analysis();
            if (a.ReplayBlocked != null)
            {
                s.Replay = RecipeReplay.NotRun("not replayed: " + a.ReplayBlocked);
                s.Phase = TeachState.Review;
            }
            else
            {
                // the confirming replay presses the studio's game: it runs only when the person presses Replay (M1) — until
                // then the recipe waits in review, where Discard ends it without a replay
                s.Replay = null;
                s.Phase = TeachState.Review;
                s.NextAfterBoot = TeachState.AfterBootReplay;
            }
            s.SettleUntil = null;
            s.StartedAt = null;
            s.Session = null;
            s.PhaseStartedUtc = nowUtc;
        }

        /// <summary>Back to waiting — Teach again, or a recording a domain reload cut short.</summary>
        public static void BackToWaiting(TeachState s, string? note, DateTime nowUtc)
        {
            s.Phase = TeachState.Waiting;
            s.Presses = new JArray();
            s.Replay = null;
            s.Outcome = null;
            s.Note = note;
            s.WaitStartedUtc = nowUtc;
            s.PhaseStartedUtc = nowUtc;
            s.SettleUntil = null;
            s.StartedAt = null;
            s.Session = null;
            s.NextAfterBoot = TeachState.AfterBootRecord;
        }

        /// <summary>The game's git commit — the recipe's stamp (v3 §3.6: a recipe is re-proven when the game changes). The
        /// kit's own reader (<see cref="GitHead"/>, `.git` read as text, git never run) asked at the Unity project and then
        /// at up to three parents, for a Unity project that lives in a folder of its repo. Null when none is a checkout.</summary>
        public static string? GameCommit(string projectRoot)
        {
            try
            {
                var dir = new DirectoryInfo(projectRoot);
                for (var i = 0; i < 4 && dir != null; i++, dir = dir.Parent)
                {
                    if (!Directory.Exists(Path.Combine(dir.FullName, ".git")) && !File.Exists(Path.Combine(dir.FullName, ".git"))) continue;
                    return GitHead.Read(dir.FullName).commit;
                }
            }
            catch (Exception)
            {
                // a stamp is best-effort: none is said as null
            }
            return null;
        }

        public static bool WaitExpired(TeachState s, DateTime nowUtc) =>
            s.Phase == TeachState.Waiting && (nowUtc - s.WaitStartedUtc).TotalSeconds >= MaxWaitSec;

        public static bool RecordingExpired(TeachState s, DateTime nowUtc) =>
            s.Phase == TeachState.Recording && s.PhaseStartedUtc != null && (nowUtc - s.PhaseStartedUtc.Value).TotalSeconds >= MaxRecordingSec;

        /// <summary>The thumbnails the presses name, read from <paramref name="thumbDir"/> as base64 — a missing or oversized
        /// file is left out (the step then has no thumbnail), never an error.</summary>
        public static JObject Thumbnails(JArray presses, string? thumbDir, int maxBytes = 64 * 1024)
        {
            var o = new JObject();
            if (thumbDir == null) return o;
            foreach (var p in presses.OfType<JObject>())
            {
                var name = p["thumbnail"]?.Type == JTokenType.String ? p["thumbnail"]!.Value<string>() : null;
                if (name == null || !System.Text.RegularExpressions.Regex.IsMatch(name, @"^step-\d{1,2}\.jpg$")) continue;
                try
                {
                    var path = Path.Combine(thumbDir, name);
                    if (!File.Exists(path)) continue;
                    var bytes = File.ReadAllBytes(path);
                    if (bytes.Length == 0 || bytes.Length > maxBytes) continue;
                    o[name] = Convert.ToBase64String(bytes);
                }
                catch (Exception)
                {
                    // a thumbnail is best-effort
                }
            }
            return o;
        }

        /// <summary>
        /// THE FACTS A TEACH POSTS — mirror of <c>TeachFactsSchema</c> (<c>apps/api/src/game-capture/teach-jobs.ts</c>);
        /// <c>recipe.cases.json</c>'s <c>facts</c> example holds the two to one shape. The kit's own verdict rides as
        /// <c>kitAnalysis</c> (what the person was shown); the box grades for itself against what it sent.
        /// </summary>
        public static string FactsJson(TeachState s, string outcome, string? why, string kitVersion, JObject thumbnails)
        {
            var taught = outcome == "taught";
            var a = s.Analysis();
            var presses = taught ? (JArray)s.Presses.DeepClone() : new JArray();
            // a press whose thumbnail did not make it goes up with none, so no key points at nothing
            foreach (var p in presses.OfType<JObject>())
                if (p["thumbnail"]?.Type == JTokenType.String && thumbnails[p["thumbnail"]!.Value<string>()!] == null)
                    p["thumbnail"] = JValue.CreateNull();
            var o = new JObject
            {
                ["kitVersion"] = kitVersion,
                ["outcome"] = outcome,
                ["why"] = why == null ? JValue.CreateNull() : why,
                ["gameCommit"] = s.GameCommit == null ? JValue.CreateNull() : s.GameCommit,
                ["presses"] = presses,
                ["cancelled"] = s.Cancelled,
                ["dropped"] = s.Dropped,
                ["replay"] = taught && s.Replay != null ? s.Replay : JValue.CreateNull(),
                ["kitAnalysis"] = new JObject { ["confirmed"] = a.Confirmed, ["why"] = a.Why == null ? JValue.CreateNull() : a.Why },
                ["thumbnails"] = taught ? thumbnails : new JObject(),
            };
            return o.ToString(Formatting.None);
        }
    }

}
