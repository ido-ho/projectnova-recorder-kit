using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// A6 step 10c — the capture JOB the hosted API hands this editor, and the on-disk progress
    /// record that lets a job survive a domain reload.
    ///
    /// This file is the C# mirror of <c>apps/api/src/studio/studio-capture-job.ts</c>: field names
    /// ARE the wire. It contains no Unity API on purpose, so every rule in it is covered by the
    /// kit's EditMode tests without an editor session.
    /// </summary>
    public sealed class CaptureJobItem
    {
        /// <summary>A shot NAME in this project's Library/Nova/shots.json.</summary>
        public string Shot { get; }
        /// <summary>1-based take number for that shot.</summary>
        public int Take { get; }
        /// <summary>2026-09-12 (plan step 6): the feature STAGE this recording is asked to reach —
        /// a <c>raided</c> from a <c>board-attack-toast@raided</c> footage ask, or null for an
        /// owner-driven capture. Optional on the wire, so an older API that does not send it still
        /// parses. The agent may start the recorder ahead of the step that owns the stage and
        /// settle on its <c>shows</c>; until that lands it is recorded for reporting only.</summary>
        public string? TargetStage { get; }

        public CaptureJobItem(string shot, int take, string? targetStage = null)
        {
            Shot = shot;
            Take = take;
            TargetStage = string.IsNullOrWhiteSpace(targetStage) ? null : targetStage!.Trim();
        }

        public JObject ToJson()
        {
            var o = new JObject { ["shot"] = Shot, ["take"] = Take };
            if (TargetStage != null) o["targetStage"] = TargetStage;
            return o;
        }

        public static CaptureJobItem? FromJson(JToken? t)
        {
            if (t is not JObject o) return null;
            var shot = o["shot"]?.Value<string>();
            var take = o["take"]?.Type == JTokenType.Integer ? o["take"]!.Value<int>() : 0;
            if (string.IsNullOrWhiteSpace(shot) || take < 1) return null;
            // optional; a wrong-typed value is treated as absent, never a parse failure of the job
            var targetStage = o["targetStage"]?.Type == JTokenType.String
                ? o["targetStage"]!.Value<string>()
                : null;
            return new CaptureJobItem(shot!, take, targetStage);
        }
    }

    public sealed class CaptureJob
    {
        /// <summary>Slice C — a job with no <c>kind</c> on the wire is a capture (an older API that
        /// predates the field, or an ordinary capture). The agent refuses a kind it does not
        /// handle rather than silently reporting it done.</summary>
        public const string CaptureKind = "capture";

        /// <summary>Slice B/C commit 2 — the Phase 0 Doctor: play, pin the boot scene, take ONE
        /// real frame, report what this editor IS. Records nothing and carries no items.</summary>
        public const string SelfTestKind = "self-test";

        /// <summary>Every kind THIS kit has a handler for. Anything else is refused whole, before
        /// Play Mode. ONE list, so the guard and the dispatch cannot disagree about what is
        /// handled — the untested inline check is the seam that let the identity bug regress.</summary>
        public static bool IsHandled(string kind) =>
            kind == CaptureKind || kind == SelfTestKind;

        public string RunId { get; }
        public string WorkspaceId { get; }
        /// <summary>Slice C: the kind of work asked for; <see cref="CaptureKind"/> by default.</summary>
        public string Kind { get; }
        /// <summary>Slice B (invariant 100): the game the API says this workspace is bound to. The
        /// agent refuses the job when this is set and differs from this editor's own
        /// <c>Library/Nova/adapter.json</c> gameId. Null when the API sent none (older API, or no
        /// bound pack) — the agent then cannot check and proceeds.</summary>
        public string? GameId { get; }
        public IReadOnlyList<CaptureJobItem> Items { get; }
        public int LeaseSec { get; }

        public CaptureJob(string runId, string workspaceId, IReadOnlyList<CaptureJobItem> items,
            int leaseSec, string? kind = null, string? gameId = null)
        {
            RunId = runId;
            WorkspaceId = workspaceId;
            Items = items;
            LeaseSec = leaseSec;
            Kind = string.IsNullOrWhiteSpace(kind) ? CaptureKind : kind!.Trim();
            GameId = string.IsNullOrWhiteSpace(gameId) ? null : gameId!.Trim();
        }

        /// <summary>One job object. A malformed item makes the WHOLE job invalid — a job with a
        /// silently-dropped item would report "done" for work it never saw.</summary>
        public static CaptureJob? FromJson(JToken? t, out string? error)
        {
            error = null;
            if (t is not JObject o) { error = "job is not an object"; return null; }
            var runId = o["runId"]?.Value<string>();
            var workspaceId = o["workspaceId"]?.Value<string>() ?? "";
            if (string.IsNullOrWhiteSpace(runId)) { error = "job has no runId"; return null; }
            // Slice C: absent kind = capture (older API). Slice B: gameId may be absent.
            var kindRaw = o["kind"]?.Type == JTokenType.String ? o["kind"]!.Value<string>() : null;
            var kind = string.IsNullOrWhiteSpace(kindRaw) ? CaptureKind : kindRaw!.Trim();
            var gameId = o["gameId"]?.Type == JTokenType.String ? o["gameId"]!.Value<string>() : null;
            var items = new List<CaptureJobItem>();
            if (o["items"] is JArray arr)
            {
                for (var i = 0; i < arr.Count; i++)
                {
                    var item = CaptureJobItem.FromJson(arr[i]);
                    if (item == null) { error = $"job {runId}: item {i} is malformed"; return null; }
                    items.Add(item);
                }
            }
            else if (kind == CaptureKind)
            {
                // A capture job with a silently-dropped items array would report "done" for work it
                // never saw. A non-capture kind (self-test) legitimately carries none.
                error = $"job {runId} has no items array";
                return null;
            }
            var lease = o["leaseSec"]?.Type == JTokenType.Integer ? o["leaseSec"]!.Value<int>() : 0;
            return new CaptureJob(runId!, workspaceId, items, lease, kind, gameId);
        }

        /// <summary>The body of <c>GET /studio/capture-jobs</c>: <c>{ "jobs": [ … ] }</c>.</summary>
        public static IReadOnlyList<CaptureJob> ParseList(string json, out string? error)
        {
            error = null;
            var result = new List<CaptureJob>();
            JObject root;
            try { root = JObject.Parse(json); }
            catch (JsonException e) { error = "jobs list is not JSON: " + e.Message; return result; }
            if (root["jobs"] is not JArray arr) { error = "jobs list has no jobs array"; return result; }
            foreach (var t in arr)
            {
                var job = FromJson(t, out var itemError);
                if (job == null) { error = itemError; return new List<CaptureJob>(); }
                result.Add(job);
            }
            return result;
        }

        /// <summary>The body of <c>POST …/claim</c>: <c>{ "job": { … }, "leaseUntil": "…" }</c>.</summary>
        public static CaptureJob? ParseClaim(string json, out string? error)
        {
            error = null;
            JObject root;
            try { root = JObject.Parse(json); }
            catch (JsonException e) { error = "claim reply is not JSON: " + e.Message; return null; }
            return FromJson(root["job"], out error);
        }
    }

    /// <summary>
    /// The whole-job admission rules, kept free of any Unity API so the kit's EditMode tests cover
    /// the EXACT comparison that runs in production — the seam that (before this) refused every SNL
    /// job by comparing the wrong identity source (slice B/C, invariants 100/101).
    /// </summary>
    public static class JobGuard
    {
        /// <summary>This editor's identity for the wrong-game check. The onboarding-authored
        /// <c>Library/Nova/adapter.json</c> gameId is authoritative; a registered custom adapter's
        /// GameId is only a fallback for a project that authored no adapter.json (SnlAdapter
        /// hardcodes "snl" while the pack and adapter.json are "snl-scratch"). Null when neither
        /// declares one — the caller then cannot verify the job's game.</summary>
        public static string? EditorGameId(string? adapterJsonGameId, string? registeredGameId)
        {
            if (!string.IsNullOrWhiteSpace(adapterJsonGameId)) return adapterJsonGameId!.Trim();
            if (!string.IsNullOrWhiteSpace(registeredGameId)) return registeredGameId!.Trim();
            return null;
        }

        /// <summary>Null = the kit may run this job; otherwise the whole-job reason it is refused.
        /// <paramref name="jobGameId"/> is what the API stamped (the SENDER, invariant 100);
        /// <paramref name="editorGameId"/> is this editor's own identity (the RECEIVER). An absent
        /// jobGameId (older API) is not checked; an absent editor identity cannot be verified and is
        /// refused, never passed.</summary>
        public static string? RefusalReason(string kind, string? jobGameId, string? editorGameId)
        {
            if (!CaptureJob.IsHandled(kind))
                return $"unsupported job kind '{kind}' — update the Recorder Kit to a version that handles it";
            // A SELF-TEST is never refused on identity, and that is deliberate. It is the Doctor:
            // it runs on a project that has not been onboarded yet, so there is no
            // Library/Nova/adapter.json and this editor's identity is the generic fallback
            // ("unregistered", DefaultAdapterBoot). Refusing "cannot verify" there would refuse the
            // ONE job that exists to be run there, and the studio would see nothing at all.
            //
            // Identity is not dropped — it is REPORTED. The self-test's facts carry this editor's
            // own gameId, and the WEB compares it against the workspace's bound game (which it
            // holds from the workspace, not from anything the kit echoed back — invariant 100: a
            // check that compares a value with itself can never fire). So a studio running the
            // Doctor in the wrong Unity project SEES that, named, instead of a silent pass.
            // The kit emits FACTS, never verdicts (evidence plan 3B, owner-ruled 2026-08-30).
            if (kind == CaptureJob.SelfTestKind) return null;
            if (jobGameId == null)
                return null; // older API sent no gameId -> cannot check, proceed (rollout-safe)
            if (editorGameId == null)
                return $"job is for game '{jobGameId}' but this project declares no gameId in Library/Nova/adapter.json — cannot verify it is the right game";
            if (editorGameId != jobGameId)
                return $"job is for game '{jobGameId}' but this editor is game '{editorGameId}' — refusing (wrong game / wrong workspace)";
            return null;
        }
    }

    /// <summary>
    /// What this editor has done so far on ONE job, written to
    /// <c>Library/AdRelay/capture-status.json</c> after every item. A domain reload (entering Play
    /// Mode is one) wipes every static, so the agent that comes up afterwards reads THIS to continue
    /// at <see cref="NextIndex"/> instead of re-recording from the top — the same discipline the
    /// relay uses for <c>bootId</c> / <c>compileGeneration</c>.
    /// </summary>
    public sealed class CaptureProgress
    {
        public string RunId { get; }
        /// <summary>Slice C: the job kind (<c>capture</c> by default). Persisted so the agent that
        /// resumes after the Play Mode reload still knows what to run.</summary>
        public string Kind { get; }
        /// <summary>Slice B (inv 100): the game the API bound this job to, or null. Persisted for
        /// the same reason — the gameId check runs after the reload wipes every static.</summary>
        public string? GameId { get; }
        public IReadOnlyList<CaptureJobItem> Items { get; }
        public int NextIndex { get; private set; }
        public int Captured { get; private set; }
        public int Failed { get; private set; }
        public IReadOnlyList<string> Errors => _errors;
        private readonly List<string> _errors;

        /// <summary>True when the agent set <c>EditorSceneManager.playModeStartScene</c> for this
        /// job (it was unset). Persisted so the agent that finishes the job — after the Play Mode
        /// reload wiped every static — knows to clear it again.</summary>
        public bool PlayModeStartSceneSet { get; set; }

        /// <summary>Slice B/C commit 2 — whether this job's RESULT has been accepted by the API.
        /// Only a non-capture kind posts one, and only a self-test does today.
        ///
        /// LOAD-BEARING ACROSS THE RELOAD: a self-test carries no items, so <see cref="Done"/>
        /// would be true the instant the job started. Entering Play Mode reloads the domain and
        /// wipes every static; the agent that comes back reads this file to learn what it still
        /// owes. Without this flag it would come up, see "done", and report the run complete
        /// having never taken the frame — the silent-`done` shape, one layer in.</summary>
        public bool ResultPosted { get; set; }

        /// <summary>Slice B/C commit 2 — this job was refused WHOLE (wrong game, unhandled kind,
        /// no adapter). It owes nothing further, including a result it was never able to gather.
        /// Persisted with the rest: the refusal may be recorded before the Play Mode reload.</summary>
        public bool Refused { get; set; }

        /// <summary>AUDIT M7 — how many times posting this job's RESULT has failed. Persisted, so
        /// the count survives the domain reload and a genuinely unreachable server ends the job
        /// with a reported reason instead of an unbounded "running" the studio cannot escape.</summary>
        public int ResultAttempts { get; set; }

        /// <summary>Every ITEM accounted for. Separate from <see cref="Done"/> because a self-test
        /// has no items and still owes work — and because <see cref="Current"/> indexes the list.</summary>
        public bool ItemsDone => NextIndex >= Items.Count;

        /// <summary>Nothing left to do. A job that owes a RESULT is not done until it has posted
        /// one (or was refused whole), however few items it has — a self-test has none, so without
        /// this it would read done the instant it started.</summary>
        public bool Done =>
            ItemsDone && (Kind == CaptureJob.CaptureKind || ResultPosted || Refused);
        // ITEMS-done, not job-done: a self-test is not `Done` until it posts a result, and asking
        // a 0-item job for Items[0] would throw.
        public CaptureJobItem? Current => ItemsDone ? null : Items[NextIndex];

        private CaptureProgress(string runId, IReadOnlyList<CaptureJobItem> items, int nextIndex,
            int captured, int failed, List<string> errors, string kind, string? gameId)
        {
            RunId = runId;
            Items = items;
            NextIndex = nextIndex;
            Captured = captured;
            Failed = failed;
            _errors = errors;
            Kind = string.IsNullOrWhiteSpace(kind) ? CaptureJob.CaptureKind : kind;
            GameId = gameId;
        }

        public static CaptureProgress Start(CaptureJob job) =>
            new(job.RunId, job.Items, 0, 0, 0, new List<string>(), job.Kind, job.GameId);

        /// <summary>The current item's take is on the server: advance.</summary>
        public void RecordCaptured()
        {
            if (ItemsDone) return;
            Captured++;
            NextIndex++;
        }

        /// <summary>The current item did not yield a take (unknown shot, failed capture, failed
        /// upload): record why and advance — a job never re-runs an item, so it always ends.</summary>
        public void RecordFailed(string error)
        {
            // ITEMS-done, not job-done: a self-test has no items at all, and `Done` stays false
            // until its result is posted — so guarding on `Done` here would index Items[0] on an
            // empty list. A per-item record on a job with no items is a caller bug; it no-ops.
            if (ItemsDone) return;
            Failed++;
            var item = Items[NextIndex];
            _errors.Add($"{item.Shot} take {item.Take}: {error}");
            NextIndex++;
        }

        /// <summary>A whole-job refusal that is not tied to one item — a job for the wrong game
        /// (inv 100) or a kind this kit does not handle. Records the reason and marks the job done,
        /// failing every remaining item (at least one, even for a job with no items) so the API,
        /// which reads a job that landed nothing and captured nothing but reports a failure as
        /// <c>failed</c> and never <c>completed</c>, cannot mistake a refusal for success.</summary>
        public void RecordJobFailure(string error)
        {
            // Idempotent on a `done`-POST retry: the same refusal re-runs after the reload and must
            // not inflate the count or repeat the reason.
            if (_errors.Contains(error)) { NextIndex = Items.Count; Refused = true; return; }
            var remaining = Items.Count - NextIndex;
            Failed += remaining > 0 ? remaining : 1;
            _errors.Add(error);
            NextIndex = Items.Count; // every item accounted for
            Refused = true;          // …and no result is owed: it was never gatherable
        }

        public string ToJson()
        {
            var o = new JObject
            {
                ["runId"] = RunId,
                ["kind"] = Kind,
                ["nextIndex"] = NextIndex,
                ["captured"] = Captured,
                ["failed"] = Failed,
                ["errors"] = new JArray(_errors),
                ["playModeStartSceneSet"] = PlayModeStartSceneSet,
                ["resultPosted"] = ResultPosted,
                ["refused"] = Refused,
                ["resultAttempts"] = ResultAttempts,
                ["items"] = new JArray(Items.Select(i => i.ToJson())),
            };
            if (GameId != null) o["gameId"] = GameId;
            return o.ToString(Formatting.Indented);
        }

        /// <summary>The body of <c>POST …/done</c> (<c>CaptureDoneReport</c> on the API side).
        /// <paramref name="extraErrors"/> carries job-wide notes that are not per-item — the
        /// shots.json load errors (slice B), so a malformed file's parse reason reaches the run's
        /// progress instead of only N× "unknown shot".</summary>
        public string DoneReportJson(IEnumerable<string>? extraErrors = null)
        {
            var errors = new List<string>(_errors);
            if (extraErrors != null)
                foreach (var e in extraErrors)
                    if (!string.IsNullOrWhiteSpace(e)) errors.Add(e);
            return new JObject
            {
                ["captured"] = Captured,
                ["failed"] = Failed,
                ["errors"] = new JArray(errors),
            }.ToString(Formatting.None);
        }

        /// <summary>Null when the file is absent or unreadable — never throws. A malformed progress
        /// file is a lost job, not a crashed editor; the server's lease expiry re-offers the run.</summary>
        public static CaptureProgress? Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var o = JObject.Parse(File.ReadAllText(path));
                var runId = o["runId"]?.Value<string>();
                if (string.IsNullOrWhiteSpace(runId)) return null;
                if (o["items"] is not JArray arr) return null;
                var items = new List<CaptureJobItem>();
                foreach (var t in arr)
                {
                    var item = CaptureJobItem.FromJson(t);
                    if (item == null) return null;
                    items.Add(item);
                }
                var next = o["nextIndex"]?.Value<int>() ?? 0;
                var errors = (o["errors"] as JArray)?.Select(e => e.Value<string>() ?? "").ToList() ?? new List<string>();
                var kind = o["kind"]?.Value<string>();
                var gameId = o["gameId"]?.Value<string>();
                return new CaptureProgress(runId!, items, Math.Clamp(next, 0, items.Count),
                    o["captured"]?.Value<int>() ?? 0, o["failed"]?.Value<int>() ?? 0, errors,
                    string.IsNullOrWhiteSpace(kind) ? CaptureJob.CaptureKind : kind!, gameId)
                {
                    PlayModeStartSceneSet = o["playModeStartSceneSet"]?.Value<bool>() ?? false,
                    ResultPosted = o["resultPosted"]?.Value<bool>() ?? false,
                    Refused = o["refused"]?.Value<bool>() ?? false,
                    ResultAttempts = o["resultAttempts"]?.Value<int>() ?? 0,
                };
            }
            catch (Exception)
            {
                return null;
            }
        }

        public void Save(string path) => AtomicFile.Write(path, ToJson());
    }

    /// <summary>Where the recorder driver put a shot's take. The kit names takes
    /// <c>&lt;shot&gt;_&lt;Take&gt;.mp4</c> in the driver's output folder and quarantines bad ones as
    /// <c>_FAILED_</c>, so "the newest live file for this shot, written after the shot started" is
    /// the take — the same rule <c>AdDirector.NewestClip</c> uses to mark a bad take.</summary>
    public static class ClipFinder
    {
        public static string? Newest(string outputDir, string shotName, DateTime notBeforeUtc)
        {
            if (!Directory.Exists(outputDir)) return null;
            string? newest = null;
            var newestTime = DateTime.MinValue;
            foreach (var f in Directory.GetFiles(outputDir, shotName + "_*.mp4"))
            {
                if (Path.GetFileName(f).Contains("_FAILED_")) continue;
                var t = File.GetLastWriteTimeUtc(f);
                if (t < notBeforeUtc) continue;
                if (t > newestTime)
                {
                    newestTime = t;
                    newest = f;
                }
            }
            return newest;
        }
    }

    /// <summary>The five studio routes, built from the base URL the studio pasted. Trailing
    /// slashes on the base are tolerated; nothing else is guessed.</summary>
    public static class StudioEndpoints
    {
        public static string Join(string baseUrl, string path) =>
            baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');

        public static string Me(string baseUrl) => Join(baseUrl, "studio/me");
        public static string Jobs(string baseUrl) => Join(baseUrl, "studio/capture-jobs");
        public static string Claim(string baseUrl, string runId) => Join(baseUrl, $"studio/capture-jobs/{runId}/claim");
        public static string Clips(string baseUrl, string runId) => Join(baseUrl, $"studio/capture-jobs/{runId}/clips");
        public static string Done(string baseUrl, string runId) => Join(baseUrl, $"studio/capture-jobs/{runId}/done");

        /// <summary>Slice B/C commit 2 — where a NON-capture job posts what it measured: multipart
        /// `facts` (JSON text) plus, when there is one, `file` (the PNG frame).</summary>
        public static string Result(string baseUrl, string runId) => Join(baseUrl, $"studio/capture-jobs/{runId}/result");
    }

    public static class CapturePaths
    {
        public const string ProgressFileName = "capture-status.json";

        public static string ProgressFile(string projectRoot) =>
            Path.Combine(RelayPaths.Root(projectRoot), ProgressFileName);
    }
}
