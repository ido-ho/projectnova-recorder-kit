using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Slice E.1 — "Try this shot": the ONE shot a <c>probe</c> job carries, exactly as the API sent
    /// it. The C# mirror of <c>StudioProbeScript</c>
    /// (<c>apps/api/src/studio/studio-capture-job.ts</c>): field names ARE the wire.
    ///
    /// It rides the CLAIM response like <c>sync-nova</c>'s two files — there is no studio route that
    /// serves it, so the claim's own gates (version skew, workspace binding, the lease) are the only
    /// door — and it is verified against the sender's shas before a single character of it is used.
    ///
    /// NOTHING HERE IS WRITTEN TO DISK. A probe must not change what the studio's machine will
    /// record later, so the shot and the adapter live in memory and on the job's own progress file;
    /// <c>Library/Nova/shots.json</c> and <c>adapter.json</c> are not touched.
    /// </summary>
    public sealed class ProbeRequest
    {
        /// <summary>The canonical JSON text of ONE shot object — the same object a shots.json
        /// <c>shots</c> array holds, not a whole document.</summary>
        public string Shot { get; }

        /// <summary>The adapter.json text this shot was authored against. It is NOT written: it is
        /// what the levers this content needs are computed from, beside the shot. It is not what
        /// RUNS either — the director reads this project's own <c>Library/Nova/adapter.json</c> —
        /// which is why the try is refused unless that file is byte-for-byte this text
        /// (<see cref="ProbePlan.DiskAdapterMismatch"/>).</summary>
        public string Adapter { get; }

        public string ShotSha256 { get; }
        public string AdapterSha256 { get; }

        /// <summary>Slice E.4 — the TUTORIAL GATE this try runs FIRST, exactly as the claim carried it
        /// (<c>probe.tutorialGate</c>), or null when it carried none. The website decides whether a try
        /// runs it; this editor runs it iff the claim says so, from THIS text, checked against THIS sha
        /// (<see cref="ProbePlan.Prepare"/>) — no file on this machine decides it.</summary>
        public ProbeGate? TutorialGate { get; }

        public ProbeRequest(string shot, string adapter, string shotSha256, string adapterSha256,
            ProbeGate? tutorialGate = null)
        {
            Shot = shot;
            Adapter = adapter;
            ShotSha256 = shotSha256;
            AdapterSha256 = adapterSha256;
            TutorialGate = tutorialGate;
        }

        /// <summary>What the agent reports when a <c>probe</c> arrives with no usable script. A job
        /// that tries nothing and reports done is the silent-`done` shape this wire has closed
        /// twice already (mirror of <see cref="SyncNovaFiles.MissingReason"/>).</summary>
        public const string MissingReason =
            "this \"Try this shot\" job arrived without the shot to try " +
            "(no complete `probe` block on the claim) — nothing was run; press Try this shot again";

        /// <summary>Null when the object is absent, not an object, or missing any of the four
        /// strings. Empty text is NOT a script: an empty shot would read as a probe that ran.
        /// Slice E.4: <c>tutorialGate</c> absent or JSON null = no gate; present but not a complete gate
        /// (<see cref="ProbeGate.FromJson"/>) = NO SCRIPT AT ALL — a try whose gate did not arrive
        /// whole must not run the shot without it, behind whatever the gate gets past.</summary>
        public static ProbeRequest? FromJson(JToken? t)
        {
            if (t is not JObject o) return null;
            var shot = Str(o["shot"]);
            var adapter = Str(o["adapter"]);
            var shotSha = Str(o["shotSha256"]);
            var adapterSha = Str(o["adapterSha256"]);
            if (shot == null || adapter == null || shotSha == null || adapterSha == null) return null;
            var g = o["tutorialGate"];
            ProbeGate? gate = null;
            if (g != null && g.Type != JTokenType.Null)
            {
                gate = ProbeGate.FromJson(g);
                if (gate == null) return null;
            }
            return new ProbeRequest(shot, adapter, shotSha, adapterSha, gate);
        }

        /// <summary>The block as it is persisted on the job's progress file, so the agent that comes
        /// back after the Play Mode domain reload still has the shot to run. Round-trips through
        /// <see cref="FromJson"/> — the same reader the claim goes through, so a resumed job can
        /// never be running a shape the claim would have refused.</summary>
        public JObject ToJson() => new()
        {
            ["shot"] = Shot,
            ["adapter"] = Adapter,
            ["shotSha256"] = ShotSha256,
            ["adapterSha256"] = AdapterSha256,
            // Slice E.4: the gate survives the Play Mode reload with the rest of the script — a resumed
            // try that lost it would run the shot without it
            ["tutorialGate"] = TutorialGate == null ? JValue.CreateNull() : TutorialGate.ToJson(),
        };

        internal static string? Str(JToken? t) =>
            t != null && t.Type == JTokenType.String && !string.IsNullOrEmpty(t.Value<string>())
                ? t.Value<string>()
                : null;
    }

    /// <summary>
    /// Slice E.4 — the TUTORIAL GATE a try carries: the canonical text of the pack's shot named
    /// exactly <c>tutorial-gate</c>, and the sha256 of its UTF-8 bytes. The C# mirror of
    /// <c>StudioProbeGate</c> (<c>apps/api/src/studio/studio-capture-job.ts</c>): field names ARE the
    /// wire. It is run FIRST, recording off, in the same Play session, under the same forced lever
    /// gate as the try (<see cref="AdDirector.Options.Preamble"/>); a stop in it is the try's stop.
    /// </summary>
    public sealed class ProbeGate
    {
        /// <summary>The one shot name a tutorial gate has (the website's <c>TUTORIAL_GATE_SHOT</c>).</summary>
        public const string ShotName = "tutorial-gate";

        public string Shot { get; }
        public string Sha256 { get; }

        public ProbeGate(string shot, string sha256)
        {
            Shot = shot;
            Sha256 = sha256;
        }

        /// <summary>Null unless an object with both strings, non-empty.</summary>
        public static ProbeGate? FromJson(JToken? t)
        {
            if (t is not JObject o) return null;
            var shot = ProbeRequest.Str(o["shot"]);
            var sha = ProbeRequest.Str(o["sha256"]);
            return shot == null || sha == null ? null : new ProbeGate(shot, sha);
        }

        public JObject ToJson() => new() { ["shot"] = Shot, ["sha256"] = Sha256 };

        /// <summary>
        /// Slice E.4 — THE SENTENCE FOR A STOP AT THE TUTORIAL GATE: "the tutorial gate stopped it at step N:
        /// &lt;reason&gt;" (N 1-based, a step of the GATE), or "the tutorial gate stopped it: &lt;reason&gt;" when
        /// no step of it is to blame (every step ran and its end state did not hold; a lever it needs is not
        /// ticked). The words the site's own sentence for a try's gate stop begins with (the API's
        /// <c>stopRefusalOf</c>) — a TypeScript function this kit cannot call, so this is the kit's ONE: a
        /// recording's failed take says it (<see cref="CaptureRun.TakeFailure"/>), and so does a try's status
        /// line when its gate stopped it.
        /// </summary>
        public static string StopSentence(int? gateFailedStep, string? reason) =>
            "the tutorial gate stopped it" + (gateFailedStep is { } step ? $" at step {step + 1}" : "") + ": "
            + (reason ?? "no reason was recorded");
    }

    /// <summary>
    /// Slice E.1 — EVERYTHING A PROBE DECIDES BEFORE IT TOUCHES THE GAME, as one pure function.
    ///
    /// No Unity API: the project root is passed in, so the kit's EditMode tests cover the exact
    /// comparisons that run in production (invariant 101). It reads <c>levers.json</c> and writes
    /// NOTHING — pinned by a test that runs it (and the director after it) against a temp project
    /// and asserts <c>Library/Nova/</c> is byte-for-byte as it was.
    /// </summary>
    public sealed class ProbePlan
    {
        /// <summary>Non-null = this probe may not run, and this is the reason by name. A refusal
        /// here is a whole-job failure: the script is missing, the shas did not match, or the shot
        /// does not load.</summary>
        public string? Refusal { get; private set; }

        /// <summary>The ONE shot, read by the REAL loader, or null when it did not load.</summary>
        public AdShot? Shot { get; private set; }

        /// <summary>Slice E.4 — the TUTORIAL GATE the claim carried, read by the same loader in the same
        /// document as the shot, or null when the claim carried none. Run first
        /// (<see cref="ProbeRun.Start"/> hands it to the director as its preamble).</summary>
        public AdShot? Gate { get; private set; }

        /// <summary>Slice E.4 — the shot the try starts with: the gate when there is one, else the shot.
        /// The agent waits for THIS one's screen before the director starts (a tutorial up means the
        /// shot's own screen is not there yet, as it was not for the gate's own try).</summary>
        public AdShot? FirstToRun => Gate ?? Shot;

        /// <summary>The one-shot document the loader was given — also what the shot half of the
        /// lever list is computed from, so the text that is judged and the text that is run are
        /// the same text.</summary>
        public string ShotsDocument { get; private set; } = "";

        /// <summary>sha256 of the UTF-8 bytes of the shot / adapter text that ARRIVED — measured
        /// here, never copied from the claim. Equal to the sender's (the plan refuses otherwise),
        /// and echoed in the facts as what ARRIVED. The shot runs from exactly this text; the
        /// adapter that runs is this project's own adapter.json, whose sha the plan requires to be
        /// this one before anything runs (<see cref="DiskAdapterMismatch"/>).</summary>
        public string ShotSha256 { get; private set; } = "";
        public string AdapterSha256 { get; private set; } = "";
        /// <summary>Slice E.4 — sha256 of the tutorial gate text that ARRIVED (null: none arrived).</summary>
        public string? GateSha256 { get; private set; }

        /// <summary>Every lever the shot and the adapter it came with need, sorted ordinal —
        /// <see cref="Levers.NeededFrom"/> over the one-shot document and the adapter text, the
        /// SAME definition the website computes the list it compares against with.</summary>
        public IReadOnlyList<string> LeversNeeded { get; private set; } = Array.Empty<string>();

        /// <summary>What <c>levers.json</c> on this machine ticks, as <see cref="Levers.Approved"/>
        /// reads it (fail closed: unreadable = nothing).</summary>
        public IReadOnlyList<string> LeversApproved { get; private set; } = Array.Empty<string>();

        /// <summary>The FIRST lever of <see cref="LeversNeeded"/> that is not ticked here
        /// (<see cref="Levers.FirstUnticked"/>), or null. Non-null means the shot is not run at
        /// all.</summary>
        public string? LeverRefused { get; private set; }

        /// <summary>May the shot be handed to the director? Only with no refusal, a loaded shot and
        /// every lever ticked.</summary>
        public bool MayRun => Refusal == null && Shot != null && LeverRefused == null;

        /// <summary>The document a one-shot probe is read as. The shot is PARSED as one object and
        /// re-serialised into a real shots document, never concatenated into one: a text join
        /// would let a second object (or trailing content) ride in the same string and be loaded
        /// as a shot the server never sent.</summary>
        internal static string DocumentFor(params JObject[] shots) =>
            new JObject
            {
                ["$schemaVersion"] = 1,
                ["shots"] = new JArray(shots.Cast<object>().ToArray()),
            }.ToString(Formatting.None);

        /// <summary>
        /// Audit S3 — THE TRY RUNS WITH THE ADAPTER.JSON ON THIS EDITOR'S DISK: its ready block, its
        /// overlays, its camera block (<c>DefaultAdapterBoot</c>, <c>AdDirector</c>,
        /// <c>CameraPose</c> all read the file). The website lists the levers, and grades the
        /// answer, against the adapter it SENT. So the two must be the same bytes, or the studio is
        /// asked to tick one adapter's levers while another one runs — and on a project that was
        /// never sent one, the job used to enter Play Mode and be refused as "wrong game". Refused
        /// whole, by this sentence, before Play Mode; only a SEND (<c>sync-nova</c>) writes the file.
        /// </summary>
        public const string DiskAdapterMismatch =
            "your editor's adapter.json is not the one the site sent — press Send to my editor, " +
            "then try again (a try runs with the adapter.json in this project's Library/Nova/)";

        /// <summary>First audit of E.4, S1 — why a try whose tutorial gate holds a <c>vision</c> step is
        /// refused whole (the site's page says the same fact, and its try check refuses such a gate's own
        /// try). A recording's gate is refused by the same sentence (<see cref="CaptureGatePlan.Prepare"/>).</summary>
        public const string GateHasScreenCheck =
            "your `tutorial-gate` shot has a screen check, which a gate cannot ask — nothing was run; remove it";

        /// <summary>The sentence a sha mismatch is refused with — one shape for every text. <paramref name="again"/>
        /// is what the studio does next: a try's is <see cref="TryAgain"/>; a recording's is
        /// <see cref="CaptureGatePlan.RecordAgain"/> (slice E.4, part 2).</summary>
        internal static string ShaMismatch(string what, string measured, string sent, string again = TryAgain) =>
            $"the {what} that arrived is not the one the website sent (sha256 {measured}, expected " +
            $"{sent}) — nothing was run; {again}";

        /// <summary>What a try's refusal tells the studio to do next.</summary>
        internal const string TryAgain = "press Try this shot again";

        /// <summary>
        /// Slice E.4 — THE TUTORIAL GATE THAT ARRIVED, read before any use: the sha of its UTF-8 bytes against
        /// the sender's (a truncated claim must not become a gate that runs half of what was authored), then
        /// ONE JSON object through the kit's one reader, then the shot named exactly <c>tutorial-gate</c> — or
        /// it is not a tutorial gate. THE ONE READER OF A GATE: a try's (<see cref="Prepare"/>) and a
        /// recording's (<see cref="CaptureGatePlan.Prepare"/>). Null = the gate may be loaded
        /// (<paramref name="gateObject"/> is it); otherwise the refusal, by name. <paramref name="sha"/> is
        /// the sha MEASURED here, whatever the answer.
        /// </summary>
        internal static string? ReadGate(ProbeGate gate, string again, out string sha, out JObject? gateObject)
        {
            gateObject = null;
            sha = SyncNova.Sha256OfText(gate.Shot);
            if (!SameSha(sha, gate.Sha256))
                return ShaMismatch("tutorial gate", sha, gate.Sha256, again);
            JObject parsed;
            try { parsed = NovaJson.ParseObject(gate.Shot); }
            catch (Exception e)
            {
                return "the tutorial gate that arrived is not one JSON object: " + e.Message;
            }
            // It is the pack's `tutorial-gate` shot, or it is not a tutorial gate.
            if (parsed["name"]?.Type != JTokenType.String
                || parsed["name"]!.Value<string>() != ProbeGate.ShotName)
                return $"the tutorial gate that arrived is not the shot named '{ProbeGate.ShotName}' — nothing was run";
            gateObject = parsed;
            return null;
        }

        /// <summary>
        /// Verify the shas, load the shot through the SAME loader and grammar a shots file goes
        /// through, refuse a <c>camera-pose</c> the run would refuse for its Type, and decide whether
        /// every lever this content needs is ticked here.
        ///
        /// THE LEVER RULE COVERS AS THE RUNTIME GATE COVERS (<see cref="Levers.IsTicked"/>): a
        /// command the gate lets through without a tick — exactly <c>ui-dump</c>, <c>show-ui</c>,
        /// <c>hide-ui</c> (a <c>get …</c> is a lever since A2′'s fourteenth audit) — is not demanded here either. But it is STRICTER than the
        /// run about WHICH levers it demands (second audit of E.1, M5): EVERY entry of
        /// <see cref="Levers.NeededFrom"/>, read-only ones aside — including a
        /// <c>hide-overlay &lt;Type&gt;</c> a capture would only SKIP (the overlay left visible,
        /// with a log line — <see cref="Levers.OverlaysAllowed"/>), and the <c>camera-spec</c> lever
        /// a capture asks for only when a <c>camera-pose</c> runs. It fails closed, and every lever
        /// it demands is a row the Nova Capture window can tick. The gate is deliberately NOT
        /// opened to match the run. What it cannot see is a compiled adapter's own ready-gate and
        /// recovery commands: those meet the gate during the run (TRUST.md).
        /// <see cref="LeversNeeded"/> is reported WHOLE, read-only entries included, because the
        /// server recomputes that list from what it sent and compares the two exactly.
        /// </summary>
        public static ProbePlan Prepare(string projectRoot, ProbeRequest? request)
        {
            var plan = new ProbePlan();
            if (request == null)
            {
                plan.Refusal = ProbeRequest.MissingReason;
                return plan;
            }

            // BOTH shas before ANY use, against the UTF-8 bytes of the texts that arrived — a
            // truncated claim body must not become a shot that runs half of what was authored.
            plan.ShotSha256 = SyncNova.Sha256OfText(request.Shot);
            if (!SameSha(plan.ShotSha256, request.ShotSha256))
            {
                plan.Refusal = ShaMismatch("shot", plan.ShotSha256, request.ShotSha256);
                return plan;
            }
            plan.AdapterSha256 = SyncNova.Sha256OfText(request.Adapter);
            if (!SameSha(plan.AdapterSha256, request.AdapterSha256))
            {
                plan.Refusal = ShaMismatch("adapter.json", plan.AdapterSha256, request.AdapterSha256);
                return plan;
            }
            // …and the adapter that will RUN is the one that was sent (audit S3). The bytes on disk,
            // exactly as SyncNova wrote them (UTF-8, no BOM); absent or unreadable is a mismatch.
            if (!SameSha(SyncNova.Sha256OfFile(SyncNova.AdapterFile(projectRoot)), request.AdapterSha256))
            {
                plan.Refusal = DiskAdapterMismatch;
                return plan;
            }
            // Slice E.4 — the tutorial gate before ANY use too, by the ONE reader of a gate (its sha, one JSON
            // object, the shot named `tutorial-gate`): the gate that runs is exactly the text the claim
            // carried, never a truncated one and never one from this machine.
            var gate = request.TutorialGate;
            JObject? gateObject = null;
            if (gate != null)
            {
                var gateRefusal = ReadGate(gate, TryAgain, out var gateSha, out gateObject);
                plan.GateSha256 = gateSha;
                if (gateRefusal != null)
                {
                    plan.Refusal = gateRefusal;
                    return plan;
                }
            }

            JObject shotObject;
            try
            {
                // The kit's ONE reader (no date parsing; trailing content refused) — the same one
                // the loader uses on a whole file.
                shotObject = NovaJson.ParseObject(request.Shot);
            }
            catch (Exception e)
            {
                plan.Refusal = "the shot that arrived is not one JSON object: " + e.Message;
                return plan;
            }

            // Slice E.4: the gate and the shot are ONE document, the gate first — loaded together by the
            // same loader (a duplicate name is its error), and the lever list below is BOTH shots'
            // (ruling 4): so a lever only the gate needs refuses the whole try before anything runs.
            plan.ShotsDocument = gateObject == null ? DocumentFor(shotObject) : DocumentFor(gateObject, shotObject);
            // remember: false — this is not the studio's shots.json, and the relay's ping must not
            // start quoting a probe's errors as theirs (JsonShotLoader.LoadFrom).
            var loaded = JsonShotLoader.LoadFrom(plan.ShotsDocument, remember: false);
            if (loaded.Errors.Count > 0)
            {
                // The loader's own sentences, verbatim: they are the ones the website's lint mirrors
                // (the shared grammar fixture), so the studio reads the same words on both sides.
                plan.Refusal = (gateObject == null ? "this shot does not load: " : "this shot and its tutorial gate do not load: ")
                    + string.Join(" · ", loaded.Errors);
                return plan;
            }
            // One shot per object: DocumentFor wraps what it is given, in order, and the loader either
            // loads each or says why (above). A count check that stood here could never fire
            // (fresh-context audit of E.1: a mutant that disabled it survived every test) and was
            // removed rather than kept as a branch nothing can reach.
            // First audit of E.4, S1 — a tutorial gate cannot hold a screen check: the director never asks
            // one there (its index would name a step of another shot), so it would stop every try at that
            // step. The site never makes such a gate active; refused whole here too, before any of its
            // earlier steps could run.
            if (gateObject != null && loaded.Shots[0].RequiresVision)
            {
                plan.Refusal = GateHasScreenCheck;
                return plan;
            }
            plan.Gate = gateObject == null ? null : loaded.Shots[0];
            plan.Shot = loaded.Shots[gateObject == null ? 0 : 1];

            plan.LeversNeeded = Levers.NeededFrom(plan.ShotsDocument, request.Adapter);
            plan.LeversApproved = Levers.Approved(projectRoot);
            // Second audit of E.1, M1 — a camera-pose whose own Type is not the camera-spec's view
            // type is refused by the run (ProbeCameraTypeGuard), and no tick can change that: so it
            // is refused HERE, whole, before any lever is asked for — the pre-check and the run
            // agree, and the studio is never sent to tick a lever that cannot help. Every command
            // this content runs is in LeversNeeded (setup, cheat / cheatUntil, the ready block's
            // writes); the camera block is the adapter that arrived — byte-for-byte the one on disk
            // that the run reads (checked above).
            var camera = AdapterJson.ParseText(request.Adapter).Camera;
            foreach (var command in plan.LeversNeeded)
            {
                var typeRefusal = CameraTypeRefusal(command, camera);
                if (typeRefusal == null) continue;
                plan.Refusal = typeRefusal;
                return plan;
            }
            plan.LeverRefused = Levers.FirstUnticked(plan.LeversNeeded, plan.LeversApproved);
            return plan;
        }

        /// <summary>
        /// Second audit of E.1, M1 — THE CAMERA-POSE TYPE RULE AS A TRY APPLIES IT. The ticked
        /// <c>camera-spec</c> approves calling its methods on ONE view type, so a
        /// <c>camera-pose</c> that names a different Type of its own is refused
        /// (<see cref="CameraPose.TypeIsNotTheSpecsViewType"/>, the sentence the pose itself uses).
        /// <see cref="CameraPose.Pose"/> asks this only when the files ON DISK are gated
        /// (<see cref="Levers.GateActive"/>), and a try writes no file — so on a project whose
        /// synced.json is gone, a try used to pose the Type the spec did not name. A try gates as
        /// cloud content always; this is the rule without the disk's answer, for
        /// <see cref="ProbePlan.Prepare"/> and <see cref="ProbeCameraTypeGuard"/> alike.
        ///
        /// Null = nothing to refuse: not a <c>camera-pose</c>, a pose that does not parse (the pose
        /// itself says why), a pose naming no Type, a camera block naming no view type (a
        /// <c>camera-spec * …</c> leaves the command's Type standing), or the same Type. The
        /// command is split the way the command bridge splits it (quotes kept together); a
        /// <c>{placeholder}</c> Type is a Type — a try carries no bindings and sends it literally.
        /// </summary>
        internal static string? CameraTypeRefusal(string? command, CameraAdapter? camera)
        {
            if (camera == null || string.IsNullOrWhiteSpace(command)) return null;
            var viewType = (camera.Value.ViewType ?? "").Trim();
            if (viewType.Length == 0) return null;
            var parts = ReflectionCheatBridge.SplitCommand(command!);
            if (parts.Length == 0 || !string.Equals(parts[0], "camera-pose", StringComparison.OrdinalIgnoreCase))
                return null;
            if (!CameraPose.TryParse(parts.Skip(1).ToArray(), out var pose, out _) || pose.TypeName == null)
                return null;
            return string.Equals(pose.TypeName, viewType, StringComparison.Ordinal)
                ? null
                : CameraPose.TypeIsNotTheSpecsViewType(pose.TypeName, viewType);
        }

        private static bool SameSha(string? a, string? b) =>
            a != null && b != null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Slice E.1 — HOW A PROBE'S SHOT IS HANDED TO THE DIRECTOR, as the one function the agent
    /// calls — so the two lines that make a probe safe are tested here rather than trusted inside a
    /// Play-Mode coroutine nothing can unit-test:
    /// <list type="bullet">
    /// <item><c>CloudContent = true</c>, UNCONDITIONALLY. A probe writes no file, so the lever
    /// gate's ordinary answer (<see cref="Levers.GateActive"/>, derived from the shas of the files
    /// ON DISK) is "local authorship, ungated" on a project whose own files are there — and the
    /// cloud's shot would run every un-ticked cheat it carries. The flag makes every enforcement
    /// point gate this run whatever the disk says (<see cref="Levers.GateActiveFor"/>): the command
    /// bridge — and, at the bridge, the <c>camera-spec</c> lever a <c>camera-pose</c> needs — a
    /// <c>timeScale</c> step, and overlay hiding; and, wrapped round the bridge
    /// (<see cref="ProbeCameraTypeGuard"/>, second audit M1), a <c>camera-pose</c>'s own Type,
    /// which <see cref="CameraPose"/> checks only against the disk's answer.</item>
    /// <item><c>Recorder = NoRecordingDriver</c> — no take, no file under any recorder's
    /// folder.</item>
    /// </list>
    /// </summary>
    public static class ProbeRun
    {
        /// <summary>The director's settings for a probe. <paramref name="onStopped"/> is where the
        /// agent reads the screen (see <see cref="AdDirector.Options.OnStopped"/>).</summary>
        public static AdDirector.Options DirectorOptions(string projectRoot, Func<IEnumerable>? onStopped)
        {
            var options = new AdDirector.Options
            {
                // ONE attempt: a probe answers "what does this path do", and a retry would run the
                // shot's writes into the studio's game a second time to answer it.
                MaxAttempts = 1,
                CloudContent = true,
                Recorder = new NoRecordingDriver(projectRoot),
                ProjectRoot = projectRoot,
                OnStopped = onStopped,
            };
            // Second audit M1: the camera-pose Type rule, asked with THIS run's cloud flag — read
            // when the director is built, from the same options object that forces the gate on.
            options.WrapCheats = (gate, log) =>
                new ProbeCameraTypeGuard(gate, projectRoot, log, options.CloudContent);
            return options;
        }

        /// <summary>
        /// Slice E.3 — the director's settings for a try of <paramref name="shot"/>: the settings
        /// above, and — when the shot has <c>vision</c> steps — its screen checks ASKED OF THE SITE
        /// (<see cref="HttpVisionChannel"/>) on behalf of the job this editor claimed
        /// (<paramref name="vision"/>: that run's id, the key that claimed it, the kit's own HTTP).
        /// A shot with none leaves <see cref="AdDirector.Options.Vision"/> unset, exactly as E.1 did.
        /// A capture never comes here: it keeps the file channel.
        /// </summary>
        public static AdDirector.Options DirectorOptions(string projectRoot, Func<IEnumerable>? onStopped,
            AdShot shot, ProbeVisionWire vision)
        {
            var options = DirectorOptions(projectRoot, onStopped);
            if (shot.RequiresVision)
                options.Vision = new HttpVisionChannel(vision);
            return options;
        }

        /// <summary>The file names a try's frame is written under, in <c>Library/AdRelay/</c>. A
        /// screen check's frame starts with it too (<see cref="HttpVisionChannel.FramePath"/>).</summary>
        public const string FramePrefix = "probe-frame-";

        /// <summary>Where the try of run <paramref name="runId"/> writes its ONE frame.</summary>
        public static string FramePath(string projectRoot, string runId) =>
            Path.Combine(RelayPaths.Root(projectRoot), FramePrefix + CapturePaths.SafeSegment(runId) + ".png");

        /// <summary>
        /// Second audit of E.1, M6 — A LOST LEASE CAN LEAVE A FRAME BEHIND FOR GOOD. When a try's
        /// lease is lost mid-run the director is finished at once, its stop hook asks
        /// <c>ScreenCapture</c> for the frame, and the agent deletes the file straight away —
        /// before Unity has written it (it lands at the end of a frame). The file then appears
        /// with no job left to delete it, and each one is per-run. So every probe frame that is NOT
        /// the run in flight's is deleted here: at the start of every probe, at every claim, and
        /// before polling for a new job. <paramref name="keepRunId"/> null = no run is in flight.
        /// The in-flight run's frame is KEPT — a retried result POST reads it from disk. Only
        /// <c>probe-frame-*.png</c>; a self-test's frame and every other file are left alone. A
        /// screen check's frame (<c>probe-frame-&lt;run&gt;-vision-&lt;id&gt;.png</c>, slice E.3) is
        /// swept too, the in-flight run's included: it is never needed once its director is gone.
        /// Best-effort: a file that cannot be deleted now is tried again next time. Returns the
        /// paths it deleted.
        /// </summary>
        public static IReadOnlyList<string> SweepStrayFrames(string projectRoot, string? keepRunId)
        {
            var deleted = new List<string>();
            var dir = RelayPaths.Root(projectRoot);
            if (!Directory.Exists(dir)) return deleted;
            var keep = keepRunId == null ? null : Path.GetFileName(FramePath(projectRoot, keepRunId));
            string[] found;
            try { found = Directory.GetFiles(dir, FramePrefix + "*"); }
            catch (Exception) { return deleted; }
            foreach (var path in found.OrderBy(p => p, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(path);
                // the pattern gave the prefix; a frame is exactly a .png
                if (!name.EndsWith(".png", StringComparison.Ordinal)) continue;
                if (keep != null && string.Equals(name, keep, StringComparison.Ordinal)) continue;
                try
                {
                    File.Delete(path);
                    deleted.Add(path);
                }
                catch (Exception)
                {
                    // held open for a moment (the upload, a virus scanner): the next sweep has it
                }
            }
            return deleted;
        }

        /// <summary>
        /// Build the director for a plan — or build NOTHING. A plan that may not run (refused, not
        /// loaded, or a lever un-ticked) returns null before a director exists, so not one of the
        /// shot's commands — not its `setup`, not the ready gate's — reaches the game. The agent
        /// already branches on <see cref="ProbePlan.MayRun"/>; this is the same rule at the last
        /// door, so a later edit to the agent cannot run a refused probe by accident.
        /// Null also when another director is active (<see cref="AdDirector.Run"/>).
        /// </summary>
        public static AdDirector? Start(GameAdapter adapter, ProbePlan plan, AdDirector.Options options)
        {
            if (!plan.MayRun || plan.Shot == null) return null;
            // Slice E.4 — THE TUTORIAL GATE RUNS FIRST iff the CLAIM carried one: set here, at the one
            // door every try passes, from the plan the claim's own text was checked into — never left
            // to whatever the options held, so no caller can run a try with a gate the claim did not
            // carry, or without the one it did.
            options.Preamble = plan.Gate;
            return AdDirector.Run(adapter, new[] { plan.Shot }, options);
        }

        /// <summary>
        /// Audit M5 — EVERY WAIT OF A TRY KEEPS ITS LEASE. A probe's lease is ten minutes, and the
        /// waits before its director alone (another director, the shot's screen) can reach
        /// minutes, then the ready gate, the steps and the frame. A lease that lapses mid-try lets
        /// the page offer Try again while this editor is still running the shot, and the answer is
        /// then refused. So this waits while <paramref name="keepWaiting"/> holds, running
        /// <paramref name="refreshLease"/> (the export's <c>RefreshLeaseIfDue</c>) on every step,
        /// and stops the moment <paramref name="leaseLost"/> says the run is no longer this
        /// editor's — calling <paramref name="onLeaseLost"/> once (the agent finishes the director
        /// there). Pure: the agent passes the Unity clock and the HTTP call in.
        /// </summary>
        public static IEnumerable WhileRefreshing(Func<bool> keepWaiting, Func<IEnumerable> refreshLease,
            Func<bool> leaseLost, Action onLeaseLost)
        {
            while (keepWaiting())
            {
                foreach (var step in refreshLease()) yield return step;
                if (leaseLost())
                {
                    onLeaseLost();
                    yield break;
                }
                yield return null;
            }
        }
    }

    /// <summary>
    /// Second audit of E.1, M1 — A TRY POSES ONLY THE VIEW TYPE ITS CAMERA-SPEC NAMES.
    ///
    /// <see cref="CameraPose.Pose"/> refuses a <c>camera-pose</c> whose own Type is not the ticked
    /// camera-spec's view type — but only when the files ON DISK are gated
    /// (<see cref="Levers.GateActive"/>). A try writes no file, so on a project whose synced.json is
    /// gone (the adapter byte-for-byte the sent one) the pose looked up the command's Type and ran
    /// the spec's methods on it. This decorator is wrapped round the director's lever gate for a
    /// try only (<see cref="ProbeRun.DirectorOptions"/> → <see cref="AdDirector.Options.WrapCheats"/>)
    /// and asks the same rule with the run's cloud flag (<see cref="Levers.GateActiveFor"/>).
    ///
    /// It asks FIRST, before the gate asks for levers: no tick can make a pose of the wrong Type
    /// run, so the studio is not sent to tick one, try again, and only then read the real reason.
    /// <see cref="ProbePlan.Prepare"/> refuses the same shot up front; this is the run's own half,
    /// for what the pre-check cannot see (a compiled adapter's own commands). A refusal is logged
    /// into the director's run log the way the gate logs its own, and returns FALSE, so the step
    /// fails by name and the command never reaches the game.
    /// </summary>
    public sealed class ProbeCameraTypeGuard : ICheatBridge
    {
        private readonly ICheatBridge _inner;
        private readonly string _projectRoot;
        private readonly Action<string>? _log;
        private readonly bool _cloudContent;

        public ProbeCameraTypeGuard(ICheatBridge inner, string projectRoot, Action<string>? log, bool cloudContent)
        {
            _inner = inner;
            _projectRoot = projectRoot;
            _log = log;
            _cloudContent = cloudContent;
        }

        /// <summary>The bridge underneath (the director's lever gate), for the seam's tests.</summary>
        public ICheatBridge Inner => _inner;

        public bool Run(string command)
        {
            if (Levers.GateActiveFor(_projectRoot, _cloudContent))
            {
                // the adapter.json the pose itself reads (CameraPose.Host.LoadAdapter)
                var refusal = ProbePlan.CameraTypeRefusal(command, AdapterJson.Load(_projectRoot).Camera);
                if (refusal != null)
                {
                    _log?.Invoke(refusal);
                    return false;
                }
            }
            return _inner.Run(command);
        }
    }

    /// <summary>
    /// Slice E.1 — THE RECORDER A PROBE USES: none.
    ///
    /// A probe answers "does this path still reach the end", and it must leave the studio's takes
    /// exactly as it found them. This driver starts nothing, holds no encoder and is never
    /// recording, and its <see cref="OutputDir"/> is a folder the kit never creates — which is
    /// load-bearing, not cosmetic: on a failed attempt the director quarantines "the newest clip for
    /// this shot" out of the driver's output folder (<c>AdDirector.MarkFailed</c>), so a driver
    /// pointed at the real output dir would rename a studio's REAL take of that shot to
    /// <c>_FAILED_</c>. With a folder that does not exist there is no newest clip and the
    /// quarantine is a no-op.
    ///
    /// 0x0 reports "size unknown" to <see cref="CaptureAspect"/>, which is the truth here and keeps
    /// a footage-shape warning out of a run that produces no footage.
    /// </summary>
    public sealed class NoRecordingDriver : IRecorderDriver
    {
        /// <summary>The folder name that is never created. Under the kit's own Library/ root, so
        /// that if some later change ever does create it, it is still not the studio's repo.</summary>
        public const string DirName = "probe-no-recording";

        public NoRecordingDriver(string projectRoot)
        {
            OutputDir = System.IO.Path.Combine(RelayPaths.Root(projectRoot), DirName);
        }

        public string OutputDir { get; }
        public bool IsRecording => false;
        public int OutputWidth => 0;
        public int OutputHeight => 0;

        /// <summary>How many times the director asked for a take. Reported by nothing — it exists so
        /// a test can say "the director asked, and no recorder answered".</summary>
        public int StartsAsked { get; private set; }

        public void Start(string clipName) => StartsAsked++;
        public void Stop() { }
    }
}
