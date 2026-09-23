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

        /// <summary>Slice D part 1 — "Learn my game": read the project (AssetDatabase, build settings,
        /// player settings, fonts, colours, code patterns), pack the FACTS and the art into zip
        /// parts, upload them. Edit Mode work: it never enters Play Mode, records nothing, carries
        /// no items, and writes nothing under Assets/.</summary>
        public const string ExportKind = "export";

        /// <summary>Slice A2′ — "Send to my editor": the website hands this editor the two files it
        /// authored (<c>Library/Nova/shots.json</c> and <c>adapter.json</c>) on the CLAIM response,
        /// the kit writes them and reports what its own loader made of them. Edit Mode work: it
        /// never enters Play Mode, records nothing, runs no command and carries no items.</summary>
        public const string SyncNovaKind = "sync-nova";

        /// <summary>Slice E.1 — "Try this shot": run ONE shot the website sends, with the recorder
        /// OFF, and report what it DID — which step stopped it, the director's own sentence, the
        /// names that were on screen at that moment and one frame. Needs Play Mode and the adapter;
        /// it writes no file, records nothing, and runs only levers this machine has ticked. Mirror
        /// of <c>'probe'</c> in <c>apps/api/src/studio/studio-capture-job.ts</c>.</summary>
        public const string ProbeKind = "probe";

        /// <summary>Every kind THIS kit has a handler for. Anything else is refused whole, before
        /// Play Mode. ONE list, so the guard and the dispatch cannot disagree about what is
        /// handled — the untested inline check is the seam that let the identity bug regress.</summary>
        public static bool IsHandled(string kind) =>
            kind == CaptureKind || kind == SelfTestKind || kind == ExportKind || kind == SyncNovaKind
            || kind == ProbeKind;

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
        public static CaptureJob? ParseClaim(string json, out string? error) =>
            ParseClaim(json, out error, out _, out _);

        /// <summary>The same body, for a caller that only cares about a <c>sync-nova</c>'s files.</summary>
        public static CaptureJob? ParseClaim(string json, out string? error, out SyncNovaFiles? files) =>
            ParseClaim(json, out error, out files, out _);

        /// <summary>
        /// Slice A2′ — the same body, plus the two FILES a <c>sync-nova</c> carries:
        /// <c>{ "job": …, "leaseUntil": …, "files": { shots, adapter, shotsSha256, adapterSha256 } }</c>.
        ///
        /// The files ride the CLAIM and nothing else — there is no studio route that serves them,
        /// so the claim's own gates (version skew, workspace binding, the lease) are the only door.
        /// <paramref name="files"/> is null for every other kind, and null for a <c>sync-nova</c>
        /// whose <c>files</c> object is absent or incomplete: THAT is a refusal with a name
        /// (<see cref="SyncNovaFiles.MissingReason"/>), never a job that writes nothing and reports
        /// done — the silent-`done` shape this wire has closed twice already.
        ///
        /// Slice E.1 — and the ONE shot a <c>probe</c> carries, on the same claim and by the same
        /// rule: <c>{ "job": …, "leaseUntil": …, "probe": { shot, adapter, shotSha256,
        /// adapterSha256 } }</c>. <paramref name="probe"/> is null for every other kind, and null
        /// for a <c>probe</c> whose block is absent or incomplete: THAT is a refusal with a name
        /// (<see cref="ProbeRequest.MissingReason"/>), never a job that tries nothing and reports
        /// done.
        /// </summary>
        public static CaptureJob? ParseClaim(string json, out string? error, out SyncNovaFiles? files,
            out ProbeRequest? probe) =>
            ParseClaim(json, out error, out files, out probe, out _);

        /// <summary>
        /// Slice E.4, part 2 — the same body, plus the TUTORIAL GATE a <c>capture</c> carries: <c>{ "job": …,
        /// "leaseUntil": …, "tutorialGate": { shot, sha256 } | null }</c>. <paramref name="tutorialGate"/> is
        /// null for every other kind, and for a capture whose claim says none (absent or JSON null); a gate
        /// that did not arrive whole is a claim whose <see cref="CaptureGateClaim.Gate"/> is null — refused by
        /// name (<see cref="CaptureGateClaim.MissingReason"/>), never a recording that runs without it.
        /// </summary>
        public static CaptureJob? ParseClaim(string json, out string? error, out SyncNovaFiles? files,
            out ProbeRequest? probe, out CaptureGateClaim? tutorialGate)
        {
            error = null;
            files = null;
            probe = null;
            tutorialGate = null;
            JObject root;
            try { root = JObject.Parse(json); }
            catch (JsonException e) { error = "claim reply is not JSON: " + e.Message; return null; }
            var job = FromJson(root["job"], out error);
            if (job == null) return null;
            // A malformed `files` (or `probe`) on a kind that carries none is ignored, not a parse
            // failure: an API that grows a field must never break a kit that has no use for it.
            if (job.Kind == SyncNovaKind) files = SyncNovaFiles.FromJson(root["files"]);
            if (job.Kind == ProbeKind) probe = ProbeRequest.FromJson(root["probe"]);
            if (job.Kind == CaptureKind) tutorialGate = CaptureGateClaim.FromJson(root["tutorialGate"]);
            return job;
        }
    }

    /// <summary>
    /// Slice A2′ — the two files a <c>sync-nova</c> delivers, exactly as the API sent them. The C#
    /// mirror of <c>StudioSyncNovaFiles</c> (<c>apps/api/src/studio/studio-capture-job.ts:63-68</c>):
    /// field names ARE the wire.
    ///
    /// The shas are the SENDER's, and they are what the kit verifies the texts against before it
    /// writes anything — a truncated claim body must not become a truncated shots.json on a
    /// studio's disk.
    /// </summary>
    public sealed class SyncNovaFiles
    {
        public string Shots { get; }
        public string Adapter { get; }
        public string ShotsSha256 { get; }
        public string AdapterSha256 { get; }

        public SyncNovaFiles(string shots, string adapter, string shotsSha256, string adapterSha256)
        {
            Shots = shots;
            Adapter = adapter;
            ShotsSha256 = shotsSha256;
            AdapterSha256 = adapterSha256;
        }

        /// <summary>What the agent reports when a <c>sync-nova</c> arrives with no usable files.</summary>
        public const string MissingReason =
            "this \"Send to my editor\" job arrived without the two files to write " +
            "(no complete `files` object on the claim) — nothing was written; press Send again";

        /// <summary>Null when the object is absent, not an object, or missing any of the four
        /// strings. Empty text is NOT a file: an empty shots.json would wipe a studio's shot list
        /// and read as a successful send.</summary>
        public static SyncNovaFiles? FromJson(JToken? t)
        {
            if (t is not JObject o) return null;
            var shots = Str(o["shots"]);
            var adapter = Str(o["adapter"]);
            var shotsSha = Str(o["shotsSha256"]);
            var adapterSha = Str(o["adapterSha256"]);
            if (shots == null || adapter == null || shotsSha == null || adapterSha == null) return null;
            return new SyncNovaFiles(shots, adapter, shotsSha, adapterSha);
        }

        private static string? Str(JToken? t) =>
            t != null && t.Type == JTokenType.String && !string.IsNullOrEmpty(t.Value<string>())
                ? t.Value<string>()
                : null;
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

        /// <summary>
        /// DOES A REFUSED CLAIM MEAN THE RUN IS NO LONGER OURS?
        ///
        /// Only an ANSWER from the server ends a job. A transport failure (the studio's wifi, a
        /// proxy, a sleeping laptop) and a 5xx are the server not answering, and the run carries on.
        /// ANY OTHER 4xx — not just 409/403/404 — IS the answer: the run finished, was taken over
        /// after the lease lapsed, or is gone. Said as the RANGE because that is what the code does;
        /// naming three statuses in the comment and testing three in the suite left the other
        /// ninety-odd undescribed (audit round 3).
        ///
        /// TWO 4xx ARE NOT ANSWERS. A 408 (request timeout) and a 429 (rate limit) both mean "not
        /// now", not "no" — and this decision throws away an export build that cost a studio
        /// minutes of editor time and up to a gigabyte of disk. They are treated like a 5xx.
        ///
        /// The resume branch of the poll used to treat a transport failure, a 5xx and a 4xx the same
        /// and DELETE the progress file, so a network blip silently dropped a job mid-flight. The
        /// lease refresher in the same file already had this rule right; this is that rule, made
        /// into a function every caller shares so they cannot drift (fresh-context audit K3,
        /// 2026-09-21; invariant 99's corollary).
        /// </summary>
        public static bool ClaimRefusalEndsJob(long status, string? transportError)
        {
            if (!string.IsNullOrEmpty(transportError)) return false;   // nobody answered
            return IsDeterministicAnswer(status);
        }

        /// <summary>
        /// Is this STATUS the server deciding, rather than the server being unavailable? The one
        /// place the 4xx rule lives, so a retry policy cannot drift from a refusal policy. The
        /// callers are the claim (<c>NovaCaptureAgent.cs</c> lease refresh, both sites), the header
        /// POST, the upload-ticket request and the self-test RESULT post. The `done` POST does NOT
        /// ask here — it never has; the comment that said it did was wrong (audit round 4).
        ///
        /// A PERSISTENT 429 DOES NOT ALWAYS END THE JOB, and saying it did was the other half of
        /// the same wrong sentence. Bounded (3 attempts, 15 s apart, no `Retry-After`): the
        /// self-test result, the header POST, the upload-ticket request, each part PUT. UNBOUNDED:
        /// the claim/lease refresh and `done`, which retry every 15 s for as long as the Editor is
        /// open. "Transient" means "do not give up on the FIRST one"; for those two it does mean
        /// "retry indefinitely", by design — a job whose lease cannot be refreshed lapses at the
        /// server instead.
        /// </summary>
        public static bool IsDeterministicAnswer(long status)
        {
            if (status == 408 || status == 429) return false;          // "not now", not "no"
            if (status >= 500) return false;                           // the server is unwell, not deciding
            return status >= 400 && status < 500;                      // and a 2xx is not a refusal at all
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
            // An EXPORT is not admitted by THIS rule either, for the same reason and one more: it is
            // the FIRST job a new game ever gets, so there is no adapter.json yet, and it runs in
            // Edit Mode where no adapter is registered at all. Its identity is the workspace the
            // STUDIO bound this Unity project to — see ExportRefusalReason, which the agent calls
            // for this kind. Returning null here only means "this rule has nothing to say".
            if (kind == CaptureJob.ExportKind) return null;
            // A SYNC-NOVA is not admitted by this rule either — and here the reason is the OPPOSITE
            // of a missing identity: this job DELIVERS `Library/Nova/adapter.json`. On a fresh
            // project there is none to compare against, and on a re-send the file being written IS
            // the identity being changed, so comparing the job against the file it is about to
            // replace would refuse exactly the job that fixes a wrong one. What protects it is the
            // WORKSPACE BINDING (the server only hands this kind to an editor that sent
            // `X-Nova-Workspace`, and `WorkspaceGuard.Refusal` checks it again here) plus
            // `SyncNova.Refusal`, which compares the DELIVERED adapter.json's gameId against the
            // job's own — sender against sender, never against something this editor echoed.
            if (kind == CaptureJob.SyncNovaKind) return null;
            // A PROBE is deliberately NOT exempted. It runs a script in the studio's game, and by
            // the time this rule runs `Library/Nova/adapter.json` exists and is byte-for-byte the
            // one the website sent with the try: ProbePlan.Prepare refused the job before Play Mode
            // otherwise ("press Send to my editor" — audit S3; a project never sent one used to
            // reach this line and be refused as "wrong game"). So this editor HAS an identity, and
            // the ordinary rule below is the right one.
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
    /// Slice D part 1 (owner decision D10) — WHICH UNITY PROJECT IS WHICH GAME.
    ///
    /// The studio key is account-scoped, so two Unity projects under one key both see every job
    /// for the account. Slice B's gameId check closes that for a game that has been onboarded —
    /// but an <c>export</c> is the first job a new game gets, and at that moment no
    /// <c>Library/Nova/adapter.json</c> exists to compare against. So the binding is a statement
    /// the STUDIO makes, once per Unity project, in the Nova Capture window; it is stored per
    /// project, sent on every call (<c>X-Nova-Workspace</c>, where the API gates on it), and
    /// checked again HERE against what the job says — the receiver checks the sender's stamp
    /// against its own state (invariant 100), never against anything it echoed.
    ///
    /// Pure functions, no Unity API: the kit's EditMode tests cover the exact comparisons that run
    /// in production.
    /// </summary>
    public static class WorkspaceGuard
    {
        /// <summary>Every kind: an editor that IS bound refuses a job for another workspace. An
        /// unbound editor cannot check and proceeds — that is every kit installed before this
        /// release, and the kinds they run (capture, self-test) have their own identity rule.</summary>
        public static string? Refusal(string? jobWorkspaceId, string? boundWorkspaceId)
        {
            if (string.IsNullOrWhiteSpace(boundWorkspaceId)) return null;
            if (string.IsNullOrWhiteSpace(jobWorkspaceId)) return null; // an older API sent none
            return jobWorkspaceId!.Trim() == boundWorkspaceId!.Trim()
                ? null
                : "this job is for a different workspace than the one this Unity project is bound to " +
                  "(Tools > Recorder Kit > Nova Capture > Workspace) — refusing";
        }

        /// <summary>An EXPORT is admitted only by a BOUND editor whose binding matches the job — an
        /// unbound project answering it is exactly the wrong-project upload D10 exists to stop.
        /// When this project HAS been onboarded (adapter.json present) and the job names a game,
        /// the two must agree too; before onboarding there is nothing to compare, by design.</summary>
        public static string? ExportRefusal(string? jobWorkspaceId, string? boundWorkspaceId,
            string? jobGameId, string? adapterJsonGameId)
        {
            if (string.IsNullOrWhiteSpace(boundWorkspaceId))
                return "this Unity project is not bound to a workspace — open Tools > Recorder Kit > " +
                       "Nova Capture, press Connect and pick the workspace for THIS project";
            if (string.IsNullOrWhiteSpace(jobWorkspaceId))
                return "the export job names no workspace — cannot verify it is for this project";
            var mismatch = Refusal(jobWorkspaceId, boundWorkspaceId);
            if (mismatch != null) return mismatch;
            if (!string.IsNullOrWhiteSpace(jobGameId) && !string.IsNullOrWhiteSpace(adapterJsonGameId)
                && jobGameId!.Trim() != adapterJsonGameId!.Trim())
                return $"the export is for game '{jobGameId}' but this project's Library/Nova/adapter.json " +
                       $"says it is '{adapterJsonGameId}' — refusing (wrong game / wrong workspace)";
            return null;
        }
    }

    /// <summary>One workspace the studio key's account holds — a row of the Nova Capture window's
    /// "which game is THIS Unity project" picker (<c>GET /studio/workspaces</c>).</summary>
    public sealed class StudioWorkspace
    {
        public string Id { get; }
        public string Name { get; }
        public string? GameId { get; }
        public StudioWorkspace(string id, string name, string? gameId) { Id = id; Name = name; GameId = gameId; }

        public string Label => string.IsNullOrWhiteSpace(GameId) ? Name : $"{Name}  ({GameId})";

        /// <summary>Never throws: a malformed reply is an empty list with the reason.</summary>
        public static IReadOnlyList<StudioWorkspace> ParseList(string json, out string? error)
        {
            error = null;
            var result = new List<StudioWorkspace>();
            JObject root;
            try { root = JObject.Parse(json); }
            catch (JsonException e) { error = "workspaces reply is not JSON: " + e.Message; return result; }
            if (root["workspaces"] is not JArray arr) { error = "workspaces reply has no workspaces array"; return result; }
            foreach (var t in arr)
            {
                if (t is not JObject o) continue;
                var id = o["id"]?.Type == JTokenType.String ? o["id"]!.Value<string>() : null;
                if (string.IsNullOrWhiteSpace(id)) continue;
                var name = o["name"]?.Type == JTokenType.String ? o["name"]!.Value<string>() : null;
                var gameId = o["gameId"]?.Type == JTokenType.String ? o["gameId"]!.Value<string>() : null;
                result.Add(new StudioWorkspace(id!, string.IsNullOrWhiteSpace(name) ? id! : name!, gameId));
            }
            return result;
        }
    }

    /// <summary>Where to PUT one export part (<c>POST …/export/upload-url</c>). The URL IS the
    /// credential — it is asked for at upload time and is never part of a job. It is either an
    /// absolute URL on the storage provider or a PATH on the API the kit already talks to; the
    /// kit cannot tell which lane it is on and must not need to. <see cref="Headers"/> is
    /// EVERYTHING to send with the PUT: in particular, never the studio key — under the bucket
    /// lane that host is a third party.</summary>
    public sealed class UploadTicket
    {
        public string Url { get; }
        public IReadOnlyDictionary<string, string> Headers { get; }
        public UploadTicket(string url, IReadOnlyDictionary<string, string> headers) { Url = url; Headers = headers; }

        public static UploadTicket? Parse(string json, string baseUrl, out string? error)
        {
            error = null;
            JObject o;
            try { o = JObject.Parse(json); }
            catch (JsonException e) { error = "upload ticket is not JSON: " + e.Message; return null; }
            var url = o["url"]?.Type == JTokenType.String ? o["url"]!.Value<string>() : null;
            if (string.IsNullOrWhiteSpace(url)) { error = "upload ticket has no url"; return null; }
            var method = o["method"]?.Type == JTokenType.String ? o["method"]!.Value<string>() : "PUT";
            if (!string.Equals(method, "PUT", StringComparison.OrdinalIgnoreCase))
            { error = $"upload ticket asks for {method}; this kit only knows PUT"; return null; }
            var headers = new Dictionary<string, string>();
            if (o["headers"] is JObject h)
                foreach (var p in h.Properties())
                    if (p.Value.Type == JTokenType.String) headers[p.Name] = p.Value.Value<string>() ?? "";
            var resolved = Resolve(url!.Trim(), baseUrl, out var resolveError);
            if (resolved == null) { error = resolveError; return null; }
            return new UploadTicket(resolved, headers);
        }

        /// <summary>
        /// An absolute `https://` URL is used as given; a path is joined onto the API base the
        /// studio pasted. Anything else (a scheme-relative `//host`, `file:`, …) is NOT guessed at
        /// — it resolves to the API base + the literal text and will simply 404.
        ///
        /// AN `http://` TICKET IS REFUSED WHEN THE BASE IS `https://`. The part is up to 32 MiB of
        /// the studio's project and the signed URL IS the credential; a plaintext hop is a
        /// downgrade nobody asked for, and a server that means it can say so over TLS. A studio on
        /// a plain-http box (a LAN install) still works: the check only fires on a downgrade.
        /// </summary>
        public static string? Resolve(string url, string baseUrl, out string? error)
        {
            error = null;
            if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return url;
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                if ((baseUrl ?? "").TrimStart().StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    error = "upload ticket points at http:// while this API is https:// — refusing the downgrade";
                    return null;
                }
                return url;
            }
            return StudioEndpoints.Join(baseUrl ?? "", url);
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
        /// <summary>Slice D part 1 (D10): the workspace the API says this job belongs to. Persisted
        /// because the workspace check runs on every resume, after a reload wiped the statics.</summary>
        public string? WorkspaceId { get; }
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

        /// <summary>Slice D part 1 — an EXPORT's three resumable stages. A domain reload (the studio
        /// saves a script) kills the routine mid-flight; the agent that comes back reads these to
        /// know whether the parts on disk are a finished build it can keep uploading, or debris.
        /// <see cref="ExportBuilt"/> is set only after header.json is on disk — the exporter writes
        /// it LAST, so "built" can never be true over a half-written export.</summary>
        public bool ExportBuilt { get; set; }
        /// <summary>The API accepted this export's header (upload tickets can now be asked for).</summary>
        public bool ExportHeaderPosted { get; set; }
        /// <summary>How many parts, from index 0, are confirmed uploaded.</summary>
        public int ExportPartsUploaded { get; set; }

        /// <summary>
        /// Slice E.1 — the ONE shot a <c>probe</c> was handed, persisted with the rest of the job.
        ///
        /// A probe ENTERS PLAY MODE, and entering Play Mode reloads the domain and wipes every
        /// static — including anything the claim's reply left in memory. So the script the job is
        /// about lives here, on disk, beside the run it belongs to: the agent that comes up
        /// afterwards has everything it needs to run the shot without asking the server again. It is
        /// a few kilobytes, unlike a <c>sync-nova</c>'s pair of files, which is why those are kept
        /// in memory and re-read from the resumed claim instead.
        /// </summary>
        public ProbeRequest? Probe { get; set; }

        /// <summary>
        /// Slice E.1 — the facts a probe GATHERED, as the JSON text it posts, once the shot has
        /// run. Persisted so a result POST that failed is retried by POSTING AGAIN, not by running
        /// the shot again: a re-run would send the shot's ticked writes into the studio's game a
        /// second time to answer the same question (the self-test re-measures on a retry; it
        /// writes nothing, so that costs it only time).
        /// </summary>
        public string? ProbeFactsJson { get; set; }

        /// <summary>
        /// Slice E.1 — how many times this probe's shot was HANDED TO THE DIRECTOR. A script
        /// recompile (or leaving Play Mode) mid-run kills the run with the domain, and the agent
        /// that comes back starts it again; a studio that keeps saving scripts would otherwise
        /// re-run the cloud's shot in their game without end. Counted BEFORE the run, persisted,
        /// and the job is refused whole at the cap — the export's rule for its build.
        /// </summary>
        public int ProbeRunStarts { get; set; }

        /// <summary>
        /// Slice E.4, part 2 — the TUTORIAL GATE a recording's claim carried (null: none), persisted with the
        /// job for the probe's reason: entering Play Mode reloads the domain, and the agent that comes back
        /// runs every take from THIS copy — the gate its first claim carried (the website hands every later
        /// claim of the job the same one). Written only when there is one, so a recording without a gate
        /// leaves the progress file it always did.
        /// </summary>
        public CaptureGateClaim? TutorialGate { get; set; }

        /// <summary>How many times the BUILD was started. A recompile restarts it, and a studio that
        /// keeps saving scripts would restart it forever — so it is counted, persisted, and the job
        /// is refused whole at the cap with a sentence that says what to do.</summary>
        public int ExportBuildStarts { get; set; }

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
            int captured, int failed, List<string> errors, string kind, string? gameId,
            string? workspaceId = null)
        {
            WorkspaceId = string.IsNullOrWhiteSpace(workspaceId) ? null : workspaceId!.Trim();
            RunId = runId;
            Items = items;
            NextIndex = nextIndex;
            Captured = captured;
            Failed = failed;
            _errors = errors;
            Kind = string.IsNullOrWhiteSpace(kind) ? CaptureJob.CaptureKind : kind;
            GameId = gameId;
        }

        public static CaptureProgress Start(CaptureJob job, ProbeRequest? probe = null,
            CaptureGateClaim? tutorialGate = null) =>
            new(job.RunId, job.Items, 0, 0, 0, new List<string>(), job.Kind, job.GameId, job.WorkspaceId)
            {
                Probe = probe,
                TutorialGate = tutorialGate,
            };

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
                ["exportBuilt"] = ExportBuilt,
                ["exportHeaderPosted"] = ExportHeaderPosted,
                ["exportPartsUploaded"] = ExportPartsUploaded,
                ["exportBuildStarts"] = ExportBuildStarts,
                ["items"] = new JArray(Items.Select(i => i.ToJson())),
            };
            if (GameId != null) o["gameId"] = GameId;
            if (WorkspaceId != null) o["workspaceId"] = WorkspaceId;
            if (Probe != null) o["probe"] = Probe.ToJson();
            if (ProbeFactsJson != null) o["probeFacts"] = ProbeFactsJson;
            if (ProbeRunStarts > 0) o["probeRunStarts"] = ProbeRunStarts;
            if (TutorialGate != null) o["tutorialGate"] = TutorialGate.ToJson();
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
                // The kit's ONE reader, for the reason NovaJson gives: Newtonsoft turns any string
                // that LOOKS like a date into a Date token, and every read below asks "is this a
                // String?". A shot named `2026-09-21` came back from a resumed job renamed (or, in
                // the probe block, as no script at all). Nothing else about this file changes.
                var o = NovaJson.ParseObject(File.ReadAllText(path));
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
                var workspaceId = o["workspaceId"]?.Value<string>();
                return new CaptureProgress(runId!, items, Math.Clamp(next, 0, items.Count),
                    o["captured"]?.Value<int>() ?? 0, o["failed"]?.Value<int>() ?? 0, errors,
                    string.IsNullOrWhiteSpace(kind) ? CaptureJob.CaptureKind : kind!, gameId, workspaceId)
                {
                    ExportBuilt = o["exportBuilt"]?.Value<bool>() ?? false,
                    ExportHeaderPosted = o["exportHeaderPosted"]?.Value<bool>() ?? false,
                    ExportPartsUploaded = Math.Max(0, o["exportPartsUploaded"]?.Value<int>() ?? 0),
                    ExportBuildStarts = Math.Max(0, o["exportBuildStarts"]?.Value<int>() ?? 0),
                    PlayModeStartSceneSet = o["playModeStartSceneSet"]?.Value<bool>() ?? false,
                    ResultPosted = o["resultPosted"]?.Value<bool>() ?? false,
                    Refused = o["refused"]?.Value<bool>() ?? false,
                    ResultAttempts = o["resultAttempts"]?.Value<int>() ?? 0,
                    // Read through the SAME reader the claim goes through, so a hand-edited or
                    // half-written block is "no script" — the named refusal — rather than a probe
                    // that runs something the server never sent.
                    Probe = ProbeRequest.FromJson(o["probe"]),
                    ProbeFactsJson = o["probeFacts"]?.Type == JTokenType.String
                        ? o["probeFacts"]!.Value<string>()
                        : null,
                    ProbeRunStarts = Math.Max(0, o["probeRunStarts"]?.Value<int>() ?? 0),
                    // Slice E.4, part 2 — through the SAME reader the claim goes through
                    TutorialGate = CaptureGateClaim.FromJson(o["tutorialGate"]),
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

        /// <summary>Slice E.3 — ONE screen check of a running try: multipart <c>frame</c> (one image
        /// of the screen) + <c>step</c> (the 0-based index of the <c>vision</c> step in the shot
        /// that was sent) + <c>requestId</c>. Answered <c>{ requestId, step, match }</c>.</summary>
        public static string Vision(string baseUrl, string runId) => Join(baseUrl, $"studio/capture-jobs/{runId}/vision");

        /// <summary>Slice D part 1 (D10) — the account's workspaces, for the window's picker.</summary>
        public static string Workspaces(string baseUrl) => Join(baseUrl, "studio/workspaces");
        /// <summary>Slice D part 1 — an export's header (JSON), posted BEFORE any part.</summary>
        public static string ExportHeader(string baseUrl, string runId) => Join(baseUrl, $"studio/capture-jobs/{runId}/export/header");
        /// <summary>Slice D part 1 — asks where to PUT one export part: body <c>{ "part": n }</c>.</summary>
        public static string ExportUploadUrl(string baseUrl, string runId) => Join(baseUrl, $"studio/capture-jobs/{runId}/export/upload-url");
        /// <summary>Spec §8.9 — best-effort progress while an export builds and uploads.</summary>
        public static string Progress(string baseUrl, string runId) => Join(baseUrl, $"studio/capture-jobs/{runId}/progress");
    }

    public static class CapturePaths
    {
        public const string ProgressFileName = "capture-status.json";

        public static string ProgressFile(string projectRoot) =>
            Path.Combine(RelayPaths.Root(projectRoot), ProgressFileName);

        /// <summary>Slice D part 1 — where ONE export is built before it is uploaded. Under
        /// Library/ (never Assets/, never the studio's repo) and named by run so a stale build
        /// from an earlier job can never be uploaded as this one's.</summary>
        public static string ExportDir(string projectRoot, string runId) =>
            Path.Combine(ExportRoot(projectRoot), SafeSegment(runId));

        /// <summary>
        /// ALWAYS UNDER `Library/`, NEVER through <see cref="RelayPaths.Root"/>.
        ///
        /// `ResolveRoot` keeps a pre-0.3 project's relay traffic in the PROJECT-ROOT `AdRelay/`
        /// folder so an in-flight session is not split across two trees — which is right for a few
        /// kilobytes of command JSON and wrong for this: an export build is up to a gigabyte, it is
        /// removed by a RECURSIVE DELETE, and TRUST.md promises "everything lands in Library/,
        /// gitignored". In one of those projects the whole build was landing in the studio's repo
        /// (audit round 3, M5).
        /// </summary>
        public static string ExportRoot(string projectRoot) =>
            Path.Combine(projectRoot, "Library", RelayPaths.RootDirName, "export");

        /// <summary>A run id is server-issued, but it becomes a DIRECTORY NAME on the studio's disk,
        /// so it is reduced to characters that cannot walk anywhere.</summary>
        internal static string SafeSegment(string s)
        {
            var chars = (s ?? "").Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray();
            return chars.Length == 0 ? "run" : new string(chars);
        }
    }
}
