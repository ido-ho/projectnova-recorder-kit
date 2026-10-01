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
        /// <summary>Invariant 190 — the box can confirm this teach by picture (its account's Claude key was usable at the
        /// press, invariant 185): only then is a teach whose NAMES cannot confirm it uploaded by itself. Absent on the claim
        /// (a box from before invariant 190) reads as true, as the box then always tried.</summary>
        public bool PictureRead { get; set; } = true;
        /// <summary>Plan v3.1 phase 4.1 — the shot this demonstration FILMS (the story beat's own shot, named by the box):
        /// video is recorded while the person plays and uploaded as that shot's take. Null = presses only, as before.</summary>
        public string? FilmShot { get; set; }

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
            if (kind == TeachAnalysis.StartLobby || kind == TeachAnalysis.StartHere) start = new JObject { ["kind"] = kind };
            else if (kind == TeachAnalysis.StartRecipe && ds["recipeId"]?.Type == JTokenType.String
                     && System.Text.RegularExpressions.Regex.IsMatch(ds["recipeId"]!.Value<string>()!, "^r[0-9a-f]{16}$"))
                start = new JObject { ["kind"] = kind, ["recipeId"] = ds["recipeId"]!.Value<string>() };
            else return null;
            if (o["authoredNames"] is not JArray names || names.Count > MaxNames) return null;
            if (names.Any(n => n.Type != JTokenType.String)) return null;
            return new TeachRequest(ask, start, names.Select(n => n.Value<string>()!))
            {
                PictureRead = o["pictureRead"]?.Type != JTokenType.Boolean || o["pictureRead"]!.Value<bool>(),
                FilmShot = o["filmShot"]?.Type == JTokenType.String
                           && System.Text.RegularExpressions.Regex.IsMatch(o["filmShot"]!.Value<string>()!, "^[A-Za-z0-9._-]{1,120}$")
                    ? o["filmShot"]!.Value<string>() : null,
            };
        }

        public JObject ToJson()
        {
            var o = new JObject
            {
                ["ask"] = Ask,
                ["declaredStart"] = DeclaredStart.DeepClone(),
                ["authoredNames"] = new JArray(AuthoredNames.OrderBy(n => n, StringComparer.Ordinal)),
                ["pictureRead"] = PictureRead,
            };
            if (FilmShot != null) o["filmShot"] = FilmShot; // plan v3.1 phase 4.1
            return o;
        }
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
        /// <summary>Kit 0.14.0 (fix 5a) — the screen at Stop (<see cref="TeachRecorder.StopJson"/>), or null (none recorded).
        /// The recipe's arrival is read from it (<see cref="TeachAnalysis.ArrivalSource"/>).</summary>
        public JObject? Stop { get; set; }
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
        /// <summary>Invariant 190 (kit 0.14.2) — the kit already started this recording's confirming replay by itself
        /// (<see cref="TeachJob.NextAutomatic"/>): it never starts it by itself twice (a replay that went back to review —
        /// cancelled, interrupted, a start that went stale — waits for a person's press).</summary>
        public bool AutoReplayed { get; set; }
        /// <summary>Invariant 190, audit F2 — the editor session (<c>SessionState</c>) in which the person pressed Stop, set by
        /// <see cref="TeachJob.AfterStop"/>. The kit starts the confirming replay by itself ONLY in that session: Stop is the
        /// go-ahead for this sitting, never for an editor opened later (TRUST.md: entering Play Mode only ever follows a press
        /// in the SAME editor session). After a restart the recipe waits for the person's Replay.</summary>
        public string? StopSession { get; set; }
        /// <summary>Kit 0.14.1 (invariant 187) — the person pressed "Teach from here": the recipe starts on the screen the game
        /// was on then (no Play restart), and its confirming replay starts there too. Always so for a request that asked for it.</summary>
        public bool StartHere { get; set; }
        /// <summary>Kit 0.14.1 (invariant 187) — the names on screen when the recording began that were gone at Stop, raw
        /// (<see cref="TeachRecorder.StartNamesGone"/>): the recipe's start names are the authored ones among them
        /// (<see cref="TeachAnalysis.StartNamesOf"/>, on both sides).</summary>
        public List<string> StartNames { get; set; } = new();
        /// <summary>Plan v3.1 phase 4.1 — when the film started (the clip is the newest one for the shot after it), and the
        /// clip found at Stop, or null.</summary>
        public DateTime? FilmStartedUtc { get; set; }
        public string? FilmClip { get; set; }

        /// <summary>Invariant 187 — where this teach starts: "from here" (the person's press, or the request's), else the
        /// request's declared start.</summary>
        public JObject EffectiveStart =>
            StartHere || Request.StartKind == TeachAnalysis.StartHere ? new JObject { ["kind"] = TeachAnalysis.StartHere } : Request.DeclaredStart;

        /// <summary>This teach starts on the screen the game is on — no Play restart, for the recording or the replay.</summary>
        public bool IsFromHere => StartHere || Request.StartKind == TeachAnalysis.StartHere;

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
            if (Stop != null) o["stop"] = Stop.DeepClone();
            if (Note != null) o["note"] = Note;
            if (Outcome != null) o["outcome"] = Outcome;
            if (PlayStarts > 0) o["playStarts"] = PlayStarts;
            if (Session != null) o["session"] = Session;
            if (LastPlayExit != null) o["lastPlayExit"] = LastPlayExit.ToJson();
            if (EnteredPlay) o["enteredPlay"] = true;
            if (EnteredPlayAt != null) o["enteredPlayAt"] = EnteredPlayAt.ToJson();
            if (StartHere) o["startHere"] = true;
            if (AutoReplayed) o["autoReplayed"] = true;
            if (StopSession != null) o["stopSession"] = StopSession;
            if (StartNames.Count > 0) o["startNames"] = new JArray(StartNames);
            if (FilmStartedUtc != null) o["filmStartedUtc"] = FilmStartedUtc.Value.ToString("o");
            if (FilmClip != null) o["filmClip"] = FilmClip;
            return o;
        }

        private static DateTime? Utc(JToken? t) =>
            t?.Type == JTokenType.Date ? t.Value<DateTime>().ToUniversalTime()
            : t?.Type == JTokenType.String && DateTime.TryParse(t.Value<string>(), null,
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
                    Stop = o["stop"] as JObject,
                    Note = o["note"]?.Type == JTokenType.String ? o["note"]!.Value<string>() : null,
                    Outcome = o["outcome"]?.Type == JTokenType.String ? o["outcome"]!.Value<string>() : null,
                    FilmStartedUtc = Utc(o["filmStartedUtc"]),
                    FilmClip = o["filmClip"]?.Type == JTokenType.String ? o["filmClip"]!.Value<string>() : null,
                    PlayStarts = Math.Max(0, o["playStarts"]?.Value<int>() ?? 0),
                    Session = o["session"]?.Type == JTokenType.String ? o["session"]!.Value<string>() : null,
                    LastPlayExit = ClockStamp.FromJson(o["lastPlayExit"]),
                    EnteredPlay = o["enteredPlay"]?.Type == JTokenType.Boolean && o["enteredPlay"]!.Value<bool>(),
                    EnteredPlayAt = ClockStamp.FromJson(o["enteredPlayAt"]),
                    StartHere = o["startHere"]?.Type == JTokenType.Boolean && o["startHere"]!.Value<bool>(),
                    AutoReplayed = o["autoReplayed"]?.Type == JTokenType.Boolean && o["autoReplayed"]!.Value<bool>(),
                    StopSession = o["stopSession"]?.Type == JTokenType.String ? o["stopSession"]!.Value<string>() : null,
                    StartNames = (o["startNames"] as JArray)?.Where(x => x.Type == JTokenType.String).Select(x => x.Value<string>()!).ToList() ?? new List<string>(),
                };
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The analysis of what was recorded (and replayed) — what the window shows in review.</summary>
        public TeachAnalysis.Result Analysis() =>
            TeachAnalysis.Analyse(Presses, Request.AuthoredNames, Replay, EffectiveStart, Stop);

        /// <summary>Invariant 187 — the recipe's start names as the box will keep them: the authored names among the raw ones.</summary>
        public List<string> StartScreenNames() => TeachAnalysis.StartNamesOf(new JArray(StartNames), Request.AuthoredNames);
    }

    /// <summary>The person's buttons in the Nova Capture window, and what each does to a teach.</summary>
    public enum TeachAction { None, Teach, Stop, Upload, Discard, TeachAgain, NotNow, Cancel, Replay, TeachHere }

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
                // kit 0.14.1 (invariant 187): a request for "from where the game is now" is taught in the person's Play session
                : s.Request.StartKind == TeachAnalysis.StartHere && !playing ? HereNeedsPlay
                // a start from another recipe is reached by the person (a teach does not replay chains — deferred; a Try and a capture do): Teach records from where they are
                : s.Request.StartKind == TeachAnalysis.StartRecipe && !playing
                    ? "this recipe starts where another one ends — enter Play Mode and play to that point first, then press Teach"
                    : null,
            // kit 0.14.1 (invariant 187): "Teach from here" — no Play restart; the recording starts on the screen the game is on
            TeachAction.TeachHere => s.Phase != TeachState.Waiting ? "Teach from here starts a recording — it is not waiting"
                : s.Request.StartKind == TeachAnalysis.StartRecipe ? "this request starts where another recipe ends — play there, then press Teach"
                : recorderBusy ? RelayRecordingReason
                : !playing ? HereNeedsPlay
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
                // kit 0.14.1 (invariant 187): a teach from here is replayed from the same screen, in the person's Play session
                : s.IsFromHere && !playing ? ReplayHereNeedsPlay
                : null,
            _ => "no action",
        };

        /// <summary>
        /// Invariant 190 (kit 0.14.2) — TEACH FINISHES ITSELF. What the kit presses by itself in review, or
        /// <see cref="TeachAction.None"/>: the confirming <see cref="TeachAction.Replay"/> — once per recording
        /// (<see cref="TeachState.AutoReplayed"/>, set here), and only when <see cref="Refusal"/> allows it now (a teach from
        /// here outside Play Mode waits for Play, then replays); then <see cref="TeachAction.Upload"/> when the replay
        /// CONFIRMED it by names, or when names could not but the box can by picture (<see cref="TeachAnalysis.Result.PictureCheck"/>
        /// — the box's one picture read decides; no usable key there and it says why on the website). Anything else — not
        /// confirmed, not replayable, nothing pressed — is None: the window shows the choice (Teach again · Upload anyway ·
        /// Discard) with one sentence why. A person's own press always goes first (the agent asks this only with no press).
        /// Audit F2: the replay (it enters Play Mode) starts by itself only in the editor session the person pressed Stop in
        /// (<see cref="TeachState.StopSession"/> == <paramref name="session"/>); after an editor restart it waits for Replay.
        /// </summary>
        public static TeachAction NextAutomatic(TeachState s, bool playing, bool recorderBusy, string session)
        {
            if (s.Phase != TeachState.Review || s.Presses.Count == 0) return TeachAction.None;
            if (s.Replay == null)
            {
                if (s.AutoReplayed || !StoppedIn(s, session) || Refusal(s, TeachAction.Replay, playing, recorderBusy) != null)
                    return TeachAction.None;
                s.AutoReplayed = true;
                return TeachAction.Replay;
            }
            var a = s.Analysis();
            // a picture confirmation only when the box said, at the press, that its account's key can run the read
            return (a.Confirmed || (a.PictureCheck && s.Request.PictureRead)) && Refusal(s, TeachAction.Upload, playing, recorderBusy) == null
                ? TeachAction.Upload
                : TeachAction.None;
        }

        /// <summary>Audit F2 — Stop was pressed in THIS editor session (the only one whose Stop may start a replay by itself).</summary>
        public static bool StoppedIn(TeachState s, string session) =>
            s.StopSession != null && !string.IsNullOrEmpty(session) && s.StopSession == session;

        /// <summary>Invariant 190 — the one sentence the window shows beside the choice when the kit did not finish by itself.</summary>
        public static string NotFinishedWhy(TeachState s, bool playing, bool recorderBusy, string session)
        {
            if (s.Presses.Count == 0) return "Nothing was pressed while it recorded — Teach again, or Discard.";
            if (s.Replay == null)
            {
                var why = Refusal(s, TeachAction.Replay, playing, recorderBusy);
                var mine = StoppedIn(s, session);
                if (why == null)
                    return s.AutoReplayed
                        ? "Not checked yet: the replay stopped before it pressed anything — Replay to confirm, or choose below."
                        : mine
                            ? "Checking it: replaying once to confirm."
                            : "Not checked yet: the editor was restarted after Stop, so it is not replayed by itself — Replay to confirm, or choose below.";
                return "Not checked yet: " + why + (s.AutoReplayed ? "." : mine ? " — it replays by itself once it can." : " — then press Replay to confirm.");
            }
            var a = s.Analysis();
            if (a.PictureCheck && !s.Request.PictureRead)
                return "Not confirmed: the names on screen cannot tell this screen apart, and the website cannot compare the pictures " +
                       "without a checked Claude key on Setup › AI keys.";
            return "Not confirmed: " + (a.Why ?? "the replay did not arrive the same way") + ".";
        }

        /// <summary>
        /// Invariant 190, audits F2 + F6 — THE REVIEW STEP the agent runs each pass, whole: the person's press when there is
        /// one, else what the kit presses by itself (<see cref="NextAutomatic"/>, in THIS editor session only), and the status
        /// line — when nothing is automatic, <see cref="NotFinishedWhy"/>'s sentence (the window's), never a blanket "not
        /// confirmed". One function, so the agent loop and its tests read the same decision.
        /// </summary>
        public static (TeachAction Action, bool Automatic, string Status) ReviewDecision(TeachState s, TeachAction pressed,
            bool playing, bool recorderBusy, string session, string ask)
        {
            var action = pressed;
            var automatic = false;
            if (action == TeachAction.None)
            {
                action = NextAutomatic(s, playing, recorderBusy, session);
                automatic = action != TeachAction.None;
            }
            var status = automatic
                ? (action == TeachAction.Replay ? "teach: replaying once to confirm it (by itself)" : "teach: confirmed — uploading it")
                : $"teach: “{ask}” — " + NotFinishedWhy(s, playing, recorderBusy, session);
            return (action, automatic, status);
        }

        /// <summary>Kit 0.14.1 (invariant 187) — the confirming replay's start check: the teach's start in the ask's words, the
        /// go-home lever only for a start on the lobby (home is the lobby), and where to ask.</summary>
        public static (string Where, string? HomeCheat, IStartAsk? Ask) ReplayCheckOf(TeachState s, string? home, IStartAsk? ask) =>
            s.IsFromHere || s.Request.StartKind == TeachAnalysis.StartRecipe
                ? (StartCheck.WhereTaught, null, ask)
                : (StartCheck.WhereLobby, home, ask);

        /// <summary>Kit 0.14.1 (invariant 187) — "Teach from here" outside Play Mode.</summary>
        public const string HereNeedsPlay = "press Play and go to the screen you want to start from, then press Teach from here";
        /// <summary>Kit 0.14.1 — the replay of a teach from here outside Play Mode.</summary>
        public const string ReplayHereNeedsPlay = "this teach started on the screen the game was on — press Play, put the game back on that screen, then press Replay";

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
            if ((action == TeachAction.Teach || action == TeachAction.TeachHere) && s.Phase == TeachState.Waiting)
            {
                s.Note = null;
                s.NextAfterBoot = TeachState.AfterBootRecord;
                // kit 0.14.1 (invariant 187): the person's choice — from here, or the request's start
                s.StartHere = action == TeachAction.TeachHere || s.Request.StartKind == TeachAnalysis.StartHere;
                if (s.Request.StartKind == TeachAnalysis.StartRecipe || s.IsFromHere)
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
                s.NextAfterBoot = TeachState.AfterBootReplay;
                if (s.IsFromHere)
                {
                    // kit 0.14.1 (invariant 187): never a Play restart — the replay starts on the screen the teach started on,
                    // which the start check asks for by its names before anything is pressed
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
            // kit 0.14.1 (invariant 187): a teach from here cannot be honoured by a fresh Play either — back, never a restart
            if (s.IsFromHere)
            {
                if (s.NextAfterBoot == TeachState.AfterBootReplay)
                {
                    s.Phase = TeachState.Review;
                    s.PhaseStartedUtc = nowUtc;
                    s.StartedAt = null;
                    s.Session = null;
                    s.Note = "Play Mode ended before the replay began — nothing was pressed; " + ReplayHereNeedsPlay;
                }
                else
                    BackToWaiting(s, "Play Mode ended before the recording began — nothing was recorded; " + HereNeedsPlay, nowUtc);
                return null;
            }
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
            string? gameCommit, DateTime nowUtc, JObject? stop = null, IEnumerable<string>? startNames = null, string? session = null)
        {
            s.Presses = presses;
            // audit F2: the session whose Stop is the go-ahead for the automatic replay (none known → a person's Replay only)
            s.StopSession = string.IsNullOrEmpty(session) ? null : session;
            // kit 0.14.1 (invariant 187): the names that left with the start screen — the recipe's start names come from them
            s.StartNames = startNames?.ToList() ?? new List<string>();
            // kit 0.14.0 (fix 5a): the Stop screen — the analysis below already reads its arrival from it
            s.Stop = stop;
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
                // the confirming replay presses the studio's game. Invariant 190 (kit 0.14.2): the kit starts it BY ITSELF from
                // review, once (`NextAutomatic` — the person's Stop is the go-ahead; Cancel still stops it before anything is
                // pressed); until it starts, the recipe waits in review, where Discard ends it without a replay
                s.Replay = null;
                s.AutoReplayed = false;
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
            s.Stop = null;
            s.StartNames = new List<string>();
            s.StartHere = false;
            s.Replay = null;
            s.AutoReplayed = false;
            s.StopSession = null;
            s.Outcome = null;
            s.Note = note;
            s.WaitStartedUtc = nowUtc;
            s.PhaseStartedUtc = nowUtc;
            s.SettleUntil = null;
            s.StartedAt = null;
            s.Session = null;
            s.NextAfterBoot = TeachState.AfterBootRecord;
            // plan v3.1 4.1 (fresh audit): an earlier attempt's film never rides a new attempt
            s.FilmClip = null;
            s.FilmStartedUtc = null;
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

        /// <summary>Kit 0.14.0 (audit M6) — THE BOX'S DOOR for a teach's facts: <c>TEACH_FACTS_MAX_BYTES</c>
        /// (<c>apps/api/src/game-capture/teach-jobs.ts</c>), measured there as <c>JSON.stringify(facts).length</c> (UTF-16
        /// units, as a C# string's Length). A document past it is refused WHOLE.</summary>
        public const int FactsDoorChars = 900 * 1024;
        /// <summary>What the budget keeps back beyond the facts measured without pictures (the picture names that become
        /// non-null, number formatting).</summary>
        public const int DoorMarginChars = 16 * 1024;

        /// <summary>Kit 0.14.0 (audit M6) — the characters ONE TEACH's pictures may take inside the facts: the door minus
        /// the facts as serialised WITHOUT pictures (<paramref name="factsWithoutPicturesChars"/>, measured by the caller on
        /// <see cref="FactsJson"/> with no thumbnails) minus a margin. Past it the earliest steps' pictures are left out first
        /// (<see cref="Thumbnails"/>), so the teach always fits the door.</summary>
        public static int PictureBudgetChars(int factsWithoutPicturesChars) =>
            Math.Max(0, FactsDoorChars - factsWithoutPicturesChars - DoorMarginChars);

        /// <summary>What one picture costs inside the facts JSON: its base64 and its <c>"name":"",</c>.</summary>
        public static int CharsOf(string name, int bytes) => 4 * ((bytes + 2) / 3) + name.Length + 6;

        /// <summary>The thumbnails the presses name — and, kit 0.14.0, the Stop picture the Stop screen names and the
        /// confirming replay's end picture (<see cref="TeachRecorder.ReplayStopThumbnail"/>, only when a replay ran) — read
        /// from <paramref name="thumbDir"/> as base64. A missing or oversized file is left out (the step then has no
        /// thumbnail), never an error. Past <paramref name="budgetChars"/> (<see cref="PictureBudgetChars"/>, counted as
        /// <see cref="CharsOf"/>) the pictures are kept in this order: the Stop picture, the replay's end picture, then the
        /// steps from the LAST back (the arrival end of the path matters most). The object lists them in the presses' order,
        /// then the Stop picture, then the replay's.</summary>
        public static JObject Thumbnails(JArray presses, string? thumbDir, int maxBytes = 64 * 1024, JObject? stop = null,
            bool replayRan = false, int budgetChars = FactsDoorChars)
        {
            var o = new JObject();
            if (thumbDir == null) return o;
            var stepNames = new List<string>();
            foreach (var p in presses.OfType<JObject>())
            {
                var name = p["thumbnail"]?.Type == JTokenType.String ? p["thumbnail"]!.Value<string>() : null;
                if (name != null && ThumbNameRe.IsMatch(name) && !stepNames.Contains(name)) stepNames.Add(name);
            }
            var stopName = stop?["thumbnail"]?.Type == JTokenType.String ? stop["thumbnail"]!.Value<string>() : null;
            if (stopName != TeachRecorder.StopThumbnail) stopName = null;
            // the keep order under the budget
            var byPriority = new List<string>();
            if (stopName != null) byPriority.Add(stopName);
            if (replayRan) byPriority.Add(TeachRecorder.ReplayStopThumbnail);
            for (var i = stepNames.Count - 1; i >= 0; i--) byPriority.Add(stepNames[i]);
            var kept = new Dictionary<string, string>(StringComparer.Ordinal);
            var used = 0;
            foreach (var name in byPriority)
            {
                try
                {
                    var path = Path.Combine(thumbDir, name);
                    if (!File.Exists(path)) continue;
                    var bytes = File.ReadAllBytes(path);
                    var cost = CharsOf(name, bytes.Length);
                    if (bytes.Length == 0 || bytes.Length > maxBytes || used + cost > budgetChars) continue;
                    used += cost;
                    kept[name] = Convert.ToBase64String(bytes);
                }
                catch (Exception)
                {
                    // a thumbnail is best-effort
                }
            }
            foreach (var name in stepNames.Concat(new[] { stopName, replayRan ? TeachRecorder.ReplayStopThumbnail : null }))
                if (name != null && kept.TryGetValue(name, out var b64) && o[name] == null) o[name] = b64;
            return o;
        }

        /// <summary>A step's picture name as the recorder writes it (<c>step-N.jpg</c>) — it becomes part of a storage key.</summary>
        private static readonly System.Text.RegularExpressions.Regex ThumbNameRe = new(@"^step-\d{1,2}\.jpg$");

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
            // kit 0.14.0 (fix 5a): the Stop screen, its picture named only when the picture goes up too
            var stop = taught && s.Stop != null ? (JObject)s.Stop.DeepClone() : null;
            if (stop != null && stop["thumbnail"]?.Type == JTokenType.String && thumbnails[stop["thumbnail"]!.Value<string>()!] == null)
                stop["thumbnail"] = JValue.CreateNull();
            var o = new JObject
            {
                ["kitVersion"] = kitVersion,
                ["outcome"] = outcome,
                ["why"] = why == null ? JValue.CreateNull() : why,
                ["gameCommit"] = s.GameCommit == null ? JValue.CreateNull() : s.GameCommit,
                ["presses"] = presses,
                ["stop"] = stop == null ? JValue.CreateNull() : stop,
                // kit 0.14.1 (invariant 187): where it started — the person's choice — and the start screen's names, raw (the box
                // keeps the authored ones among them, by the rule both sides run)
                ["start"] = taught ? new JObject { ["kind"] = s.EffectiveStart["kind"]!.Value<string>(), ["names"] = new JArray(s.StartNames) } : JValue.CreateNull(),
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
