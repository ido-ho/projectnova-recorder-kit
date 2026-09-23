using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Slice A2′ — what this editor did with the two files the website sent, reported through the
    /// same `result` door a self-test uses. The C# mirror of <c>SyncNovaFacts</c>
    /// (<c>apps/api/src/studio/studio-capture-job.ts:77-91</c>): field names ARE the wire.
    ///
    /// FACTS, NEVER A VERDICT (evidence plan 3B), about what WAS written: the names loaded, the shas on
    /// disk, the levers — and the sentence the studio reads is built on the server from these against
    /// what it sent (<c>syncNovaProblem</c>). What the kit's own readers would refuse is never written:
    /// a text they would decode differently (tenth audit, S1), and — since the eleventh audit, when the
    /// delivery check became the LOADER — a shots.json the loader refuses any part of, or an
    /// adapter.json the ready gate would fail on. So <see cref="ShotLoadErrors"/> is empty after every
    /// send that writes (the read-back is the positive control); it stays on the wire for the server.
    /// The kit's only failures here are the ones where it refused to write at all.
    /// </summary>
    public sealed class SyncNovaResult
    {
        /// <summary>Non-null = nothing was written and this is the reason, by name. No result is
        /// posted for a refusal: it goes up as the job's `done` error.</summary>
        public string? Refusal { get; set; }

        public IReadOnlyList<string> Shots { get; set; } = Array.Empty<string>();
        public IReadOnlyList<string> ShotLoadErrors { get; set; } = Array.Empty<string>();
        public string? GameId { get; set; }
        /// <summary>sha256 of the bytes ON DISK after the write — read back, never echoed from the
        /// input (invariant 100: a check that compares a value with itself can never fire).</summary>
        public string ShotsSha256 { get; set; } = "";
        public string AdapterSha256 { get; set; } = "";
        public IReadOnlyList<string> LeversNeeded { get; set; } = Array.Empty<string>();
        public IReadOnlyList<string> LeversApproved { get; set; } = Array.Empty<string>();
        public bool ReplacedLocalEdits { get; set; }

        /// <summary>The multipart `facts` field. `kitVersion` rides with the rest so the server can
        /// read what answered without trusting a header it also gates on.</summary>
        public string ToFactsJson(string kitVersion) =>
            new JObject
            {
                ["shots"] = new JArray(Shots),
                ["shotLoadErrors"] = new JArray(ShotLoadErrors),
                ["gameId"] = GameId == null ? JValue.CreateNull() : new JValue(GameId),
                ["shotsSha256"] = ShotsSha256,
                ["adapterSha256"] = AdapterSha256,
                ["leversNeeded"] = new JArray(LeversNeeded),
                ["leversApproved"] = new JArray(LeversApproved),
                ["replacedLocalEdits"] = ReplacedLocalEdits,
                ["kitVersion"] = kitVersion ?? "",
            }.ToString(Formatting.None);
    }

    /// <summary>
    /// Slice A2′ — "Send to my editor", the whole of it, as plain <c>System.IO</c>: verify the two
    /// texts against the shas that came with them, back up anything a PERSON edited, record the
    /// incoming pair in <c>Library/Nova/synced.json</c> BEFORE writing it (see <see cref="Run"/>
    /// for why that order is the safety property), write both files atomically, update the record,
    /// then read the result back through the REAL loader and report.
    ///
    /// NO UNITY API. The project root is passed in, so every rule here is covered by the kit's
    /// EditMode tests against a temp directory — the exact code that runs in production, not a
    /// re-implementation of it (invariant 101).
    ///
    /// IT NEVER THROWS. An exception anywhere becomes a named refusal, because the one outcome a
    /// job must never produce is a studio watching "Running in your editor…" forever.
    /// </summary>
    public static class SyncNova
    {
        /// <summary>This editor's process id, in every temp name: two Unity instances open on the
        /// same project must never write the same scratch file.</summary>
        private static readonly int Pid = Process.GetCurrentProcess().Id;

        public const string SyncedFileName = "synced.json";

        /// <summary>The version of <c>synced.json</c> this kit writes. v2 is what every released
        /// kit writes: no version of this package has ever shipped a v1 file, because the history
        /// lists were added before `sync-nova` was released at all. v1 — a record with the two shas
        /// and no lists — is still READ, so a hand-written or hand-trimmed record keeps the gate it
        /// describes rather than losing it (see <see cref="ReadSynced"/>).</summary>
        public const int SyncedSchemaVersion = 2;

        /// <summary>How many delivered shas each list keeps. Enough that a studio flipping between
        /// a few versions of their shot list stays gated the whole time, small enough that the file
        /// cannot grow without bound on a project that syncs every day.</summary>
        public const int MaxRemembered = 20;
        public const string ShotsFileName = "shots.json";
        public const string AdapterFileName = "adapter.json";

        /// <summary>The most bytes a send may carry, per file (ninth audit, S1): the API's own caps
        /// (<c>SYNC_NOVA_MAX_BYTES</c> in <c>apps/api/src/studio/studio-capture-job.ts</c>), applied again here because
        /// this kit cannot know that the server it talks to applied them. The renderer's
        /// <c>shots-store.spec.ts</c> reads both lines as text and holds them equal.</summary>
        public const int MaxShotsBytes = 512 * 1024;
        public const int MaxAdapterBytes = 64 * 1024;

        public static string SyncedFile(string projectRoot) =>
            Path.Combine(RelayPaths.NovaDir(projectRoot), SyncedFileName);

        public static string AdapterFile(string projectRoot) =>
            Path.Combine(RelayPaths.NovaDir(projectRoot), AdapterFileName);

        /// <summary>
        /// Null = this job may write. Otherwise the whole-job reason it may not, by name.
        ///
        /// The identity check is between the two things the SERVER said (invariant 100): the job's
        /// own <c>gameId</c> and the gameId inside the adapter.json it is delivering. It is never
        /// against this project's adapter.json — that file is what the job REPLACES.
        /// </summary>
        public static string? Refusal(SyncNovaFiles? files, string? jobGameId)
        {
            if (files == null) return SyncNovaFiles.MissingReason;
            // TENTH AUDIT, S1 — read as the kit's readers will read it, or refused by name, BEFORE its gameId is asked:
            // the agent asks this first, and an adapter.json the parser could not read used to come back as "names no
            // gameId" (a leading U+FEFF among them), which is a refusal for the wrong reason.
            if (ReadAdapter(files.Adapter, Utf8NoBom(files.Adapter), out var adapterRoot) is { } unreadable) return unreadable;
            var delivered = AdapterJson.From(adapterRoot).GameId;
            // NO gameId AT ALL is refused outright (audit m9d): the file this job exists to write
            // is the one that says which game this project is, and a delivered adapter.json that
            // names none can never be checked against anything — not by this job, not by the next
            // one, which compares its own game against what it finds here.
            if (string.IsNullOrWhiteSpace(delivered)) return "adapter.json names no gameId";
            if (string.IsNullOrWhiteSpace(jobGameId)) return null;
            if (string.Equals(delivered!.Trim(), jobGameId!.Trim(), StringComparison.Ordinal)) return null;
            return $"this job is for game '{jobGameId}' but the adapter.json it is sending says " +
                   $"'{delivered}' — refusing to write a mix-up (nothing was changed)";
        }

        /// <summary>
        /// Do it. <paramref name="runId"/> is recorded in synced.json so a later question ("which
        /// send put these bytes here?") has an answer on the studio's own disk.
        ///
        /// THE WRITE ORDER IS THE SAFETY PROPERTY (audit S2, 2026-09-21). Both shas are verified,
        /// then anything of the STUDIO's is copied, then <c>synced.json</c> records the incoming
        /// pair as DELIVERED — before either file is written. A failure after that point leaves
        /// cloud bytes on disk with the lever gate LIVE over them, which is the fail-CLOSED
        /// direction; the old order recorded the write afterwards, so a handled failure (or a
        /// crash) left the cloud's shots.json on disk looking like a local edit, ungated.
        /// </summary>
        public static SyncNovaResult Run(string projectRoot, SyncNovaFiles? files, string runId,
            string? jobGameId)
        {
            var result = new SyncNovaResult();
            var refusal = Refusal(files, jobGameId);
            if (refusal != null)
            {
                result.Refusal = refusal;
                return result;
            }
            try
            {
                // ONE array per file: the bytes that are hashed ARE the bytes that are written, so
                // "the sha of what I checked" and "the sha of what I wrote" cannot come apart.
                var shotsBytes = Utf8NoBom(files!.Shots);
                var adapterBytes = Utf8NoBom(files.Adapter);
                var shotsSha = Sha256OfBytes(shotsBytes);
                var adapterSha = Sha256OfBytes(adapterBytes);

                // BOTH shas before EITHER write: a truncated claim body must not become a truncated
                // shots.json, and a half-delivered pair must leave the project exactly as it was.
                if (!SameSha(shotsSha, files.ShotsSha256))
                {
                    result.Refusal = "the shots.json that arrived is not the one the website sent " +
                                     $"(sha256 {shotsSha}, expected {files.ShotsSha256}) — nothing was written; press Send again";
                    return result;
                }
                if (!SameSha(adapterSha, files.AdapterSha256))
                {
                    result.Refusal = "the adapter.json that arrived is not the one the website sent " +
                                     $"(sha256 {adapterSha}, expected {files.AdapterSha256}) — nothing was written; press Send again";
                    return result;
                }

                // THE INPUT IS BOUNDED HERE, before anything is written (ninth audit, S1, M1, M4): the files' sizes, the
                // number of levers they ask for, and the two shapes no tick can make safe.
                if (DeliveryRefusal(shotsBytes, adapterBytes, files.Shots, files.Adapter) is { } refused)
                {
                    result.Refusal = refused;
                    return result;
                }

                var dir = RelayPaths.NovaDir(projectRoot);
                Directory.CreateDirectory(dir);
                var synced = ReadSynced(projectRoot);
                // M5 (second audit) — THE SAME SEND, ASKED TWICE. The agent re-runs all of this
                // whenever the result POST has to be retried (a network failure, a resumed job),
                // and the second run sees its OWN bytes on disk, so it would answer "no, nothing of
                // yours was replaced" — and the studio is never told their file was backed up. The
                // answer is remembered beside the run that made it, in synced.json, so it survives
                // a domain reload too; it is per SEND, not per attempt.
                var replaced = synced.Parsed
                               && !string.IsNullOrEmpty(runId)
                               && string.Equals(synced.RunId, runId, StringComparison.Ordinal)
                               && synced.ReplacedLocalEdits;
                var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
                var shotsPath = RelayPaths.NovaShotsFile(projectRoot);
                var adapterPath = AdapterFile(projectRoot);

                // 1. LOOK AT BOTH LOCAL FILES FIRST. A file that is there but cannot be read is a
                //    person's file that cannot be copied — and replacing it would lose exactly what
                //    the copy exists to protect (audit m1). Refuse the whole sync, by name.
                if (!TryReadSha(shotsPath, ShotsFileName, out var shotsLocal, out var unreadable))
                {
                    result.Refusal = unreadable;
                    return result;
                }
                if (!TryReadSha(adapterPath, AdapterFileName, out var adapterLocal, out unreadable))
                {
                    result.Refusal = unreadable;
                    return result;
                }

                // M1 (second audit) — A RECORD THIS KIT CANNOT READ MEANS IT DOES NOT KNOW WHAT IT
                //    DELIVERED HERE. An unparsed synced.json used to read as an EMPTY history, and
                //    step 3 then wrote a record naming only the incoming pair: when the send failed
                //    after that, the CLOUD's earlier files were still on disk and in no history at
                //    all, so a project that was gated before the send was open after it.
                //
                //    Both files on disk therefore go into the history first — fail CLOSED for the
                //    gate. Their provenance is unknowable, so they are still COPIED before they are
                //    replaced (a spare .bak costs a few kilobytes; a lost edit cannot be undone) and
                //    they are NOT reported as a person's edit, which is the other half of the same
                //    defect: the cloud's own old file used to come back to the studio as "we
                //    replaced something you wrote".
                var recordUnreadable = synced.Exists && !synced.Parsed;
                var everShots = synced.CloudShots;
                var everAdapters = synced.CloudAdapters;
                if (recordUnreadable)
                {
                    if (shotsLocal != null) everShots = Remember(everShots, shotsLocal);
                    if (adapterLocal != null) everAdapters = Remember(everAdapters, adapterLocal);
                }

                // 2. THE BACKUPS, before a single byte is written: a copy that fails refuses the
                //    whole send rather than overwriting the file it was protecting.
                var error = Backup(shotsPath, shotsLocal, shotsSha, synced.CloudShots, stamp,
                    ref replaced, claimAsLocalEdit: !recordUnreadable);
                if (error != null)
                {
                    result.Refusal = $"could not write {ShotsFileName}: {error}";
                    return result;
                }
                error = Backup(adapterPath, adapterLocal, adapterSha, synced.CloudAdapters, stamp,
                    ref replaced, claimAsLocalEdit: !recordUnreadable);
                if (error != null)
                {
                    result.Refusal = $"could not write {AdapterFileName}: {error}";
                    return result;
                }

                // 3. RECORD THE DELIVERY BEFORE THE DELIVERY. The two shas go into the HISTORY now;
                //    the top-level pair still names what is actually on disk, so a send that dies
                //    here has changed nothing and a send that dies later is still gated.
                var cloudShots = Remember(everShots, shotsSha);
                var cloudAdapters = Remember(everAdapters, adapterSha);
                error = WriteSynced(projectRoot, synced.ShotsSha256 ?? "", synced.AdapterSha256 ?? "",
                    cloudShots, cloudAdapters, runId, replaced);
                if (error != null)
                {
                    result.Refusal = $"could not write {SyncedFileName}: {error} — nothing was written; press Send again";
                    return result;
                }

                // 4. The two files themselves.
                error = WriteOne(shotsPath, shotsBytes, shotsSha, shotsLocal);
                if (error != null)
                {
                    result.Refusal = $"could not write {ShotsFileName}: {error}";
                    return result;
                }
                error = WriteOne(adapterPath, adapterBytes, adapterSha, adapterLocal);
                if (error != null)
                {
                    // Said exactly: shots.json is already on disk, and this run leaves it there —
                    // gated, because step 3 already recorded it as the cloud's.
                    result.Refusal = $"could not write {AdapterFileName}: {error} — " +
                                     $"{ShotsFileName} was already written (and is gated); press Send again";
                    return result;
                }

                // 5. synced.json again, now naming the bytes ON DISK — read back, never echoed from
                //    the input (invariant 100).
                var shotsOnDisk = Sha256OfFile(shotsPath) ?? "";
                var adapterOnDisk = Sha256OfFile(adapterPath) ?? "";
                error = WriteSynced(projectRoot, shotsOnDisk, adapterOnDisk, cloudShots, cloudAdapters,
                    runId, replaced);
                if (error != null)
                {
                    result.Refusal = $"could not write {SyncedFileName}: {error} — both files were " +
                                     "written (and are gated); press Send again";
                    return result;
                }

                // Read it back through the REAL loader — the same call the adapter makes on every
                // relay command — so what is reported is what a capture job would actually get.
                var loaded = JsonShotLoader.LoadFromPath(shotsPath);
                result.Shots = loaded.Shots.Select(s => s.Name).ToArray();
                result.ShotLoadErrors = loaded.Errors.ToArray();
                result.GameId = AdapterJson.Load(projectRoot).GameId;
                result.ShotsSha256 = shotsOnDisk;
                result.AdapterSha256 = adapterOnDisk;
                result.LeversNeeded = Levers.Needed(projectRoot);
                result.LeversApproved = Levers.Approved(projectRoot);
                result.ReplacedLocalEdits = replaced;
                return result;
            }
            catch (Exception e)
            {
                // Catch-all on purpose: an exception out of here reaches the agent's coroutine,
                // which keeps the progress file and retries every 15 s — the studio watching
                // "Running in your editor…" with no end. A sentence is always better than that.
                result.Refusal = $"this editor could not write Library/Nova: {e.Message}";
                return result;
            }
        }

        /// <summary>
        /// NINTH AUDIT — WHAT THIS KIT TAKES IN, BOUNDED WHERE IT RECEIVES IT; TENTH — READ AS THE KIT WILL READ IT, FAILING
        /// CLOSED; ELEVENTH — THE DELIVERY CHECK IS THE LOADER. Null = the pair may be written; otherwise the reason, in one
        /// sentence, by name. In order: a file larger than the API's send cap (<see cref="MaxShotsBytes"/>,
        /// <see cref="MaxAdapterBytes"/>); a shots.json whose text is not the text the readers read back
        /// (<c>ReadBack</c>); a shots.json the LOADER refuses any part of — <c>JsonShotLoader.Read</c>, the function
        /// <c>LoadFromPath</c> runs after <c>File.ReadAllText</c>, refused with its own first error (not JSON, not an
        /// object, nested past <c>NovaJson.MaxNesting</c>, a <c>$schemaVersion</c> other than 1, no <c>shots</c> array, any
        /// shot it cannot load); an adapter.json read as its readers will (<c>ReadAdapter</c>: read back, parsed,
        /// its <c>ready</c> block's fields each a non-blank command string with no unsendable character, and not a block the
        /// ready gate refuses, <c>HygieneSpec.Problem</c>); more levers than <see cref="Levers.MaxLevers"/>; a lever
        /// holding a character the window cannot show as it runs (<see cref="Levers.UnsendableCharacter"/>); a lever
        /// longer than <see cref="Levers.MaxLeverLength"/>; a placeholder nothing can bind
        /// (<see cref="Levers.UnboundPlaceholder"/>).
        ///
        /// ROUNDS 8 TO 11 EACH FOUND A LOADER RULE A PARALLEL CHECKLIST HERE DID NOT MIRROR — a file written and then refused,
        /// or slow, on every load. So the checks that duplicated the loader's (a parse failure, no <c>shots</c> array)
        /// collapsed into the loader itself, and what stays here is only what the loader does not do: the byte caps, the
        /// lever count and length, unsendable characters and an undeclared placeholder. The levers are counted from the
        /// document the loader parsed (the <c>JObject</c> overloads of <see cref="Levers"/>), so no check here meets a parse
        /// failure and answers "nothing found" for it. The hosted lint refuses the same deliveries
        /// (<c>Editor/Tests/Fixtures/deliveries.cases.json</c>, run by both suites).
        /// </summary>
        internal static string? DeliveryRefusal(byte[] shotsBytes, byte[] adapterBytes, string? shots, string? adapter)
        {
            if (shotsBytes.Length > MaxShotsBytes)
                return $"{ShotsFileName} is {shotsBytes.Length} bytes, and a send may carry at most {MaxShotsBytes} — nothing was written";
            if (adapterBytes.Length > MaxAdapterBytes)
                return $"{AdapterFileName} is {adapterBytes.Length} bytes, and a send may carry at most {MaxAdapterBytes} — nothing was written";
            if (ReadBack(ShotsFileName, shots, shotsBytes) is { } unreadable) return unreadable;
            // THE LOADER ITSELF (the eleventh audit, ruling 1): the function LoadFromPath runs after File.ReadAllText, on the
            // text the read-back just showed File.ReadAllText will return — so a file it would refuse any part of is never
            // written, and the levers are counted from the document it parsed.
            var loaded = JsonShotLoader.Read(shots ?? "", out var shotsRoot);
            if (loaded.Errors.Count > 0)
                return $"{ShotsFileName} would not load as the kit loads it ({loaded.Errors[0]}" +
                       (loaded.Errors.Count > 1 ? $"; and {loaded.Errors.Count - 1} more" : "") + ") — nothing was written";
            if (ReadAdapter(adapter, adapterBytes, out var adapterRoot) is { } unreadableAdapter) return unreadableAdapter;
            var levers = Levers.NeededFromParsed(shotsRoot!, adapterRoot!);
            if (levers.Count > Levers.MaxLevers)
                return $"these files ask for {levers.Count} levers, and this kit takes at most {Levers.MaxLevers} in one send — nothing was written";
            foreach (var lever in levers)
                if (Levers.UnsendableCharacter(lever) is { } code)
                    return $"the lever {JsonConvert.ToString(lever)} holds {code}, a line break, control or invisible formatting " +
                           "character, so the row a person ticks would not be the command that runs — nothing was written";
            foreach (var lever in levers)
                if (lever.Length > Levers.MaxLeverLength)
                    return Levers.TooLongLeverRefusal(lever);
            return Levers.UnboundPlaceholderInParsed(shotsRoot!, adapterRoot!);
        }

        /// <summary>The one sentence every file this kit cannot read as its readers will is refused with (tenth audit).</summary>
        private static string Unreadable(string fileName, string why) =>
            $"{fileName} cannot be read as the kit will read it ({why}) — nothing was written";

        /// <summary>
        /// TENTH AUDIT, S1 — ONE DELIVERED FILE, READ AS THE KIT'S READERS WILL READ IT, or the reason it cannot be (since the
        /// eleventh audit, adapter.json's; shots.json's second half is the loader, <see cref="DeliveryRefusal"/>). First,
        /// its text must be the text they read back from the bytes about to be written (<see cref="ReadBackAsTheKitWill"/>):
        /// <c>File.ReadAllText</c> reads past a leading U+FEFF, and <see cref="Utf8NoBom"/> writes half of a surrogate pair
        /// as U+FFFD, so either one made every check here read a different text from the one the loader, the window and
        /// the director then read. The first character that differs is named by the kit's one set of characters it will
        /// not take (<see cref="Levers.UnsendableCharacter"/>), which holds both. Then it must parse as a JSON object with
        /// the reader every one of them uses (<c>NovaJson.ParseObject</c>); the parser's own message is the reason.
        /// </summary>
        internal static string? ReadAsTheKitWill(string fileName, string? text, byte[] bytes, out JObject? root)
        {
            root = null;
            if (ReadBack(fileName, text, bytes) is { } differs) return differs;
            try
            {
                root = NovaJson.ParseObject(text ?? "");
                return null;
            }
            catch (Exception e)
            {
                return Unreadable(fileName, e.Message);
            }
        }

        /// <summary>The first half of <see cref="ReadAsTheKitWill"/>: null when the text is the text the readers read back
        /// from these bytes, otherwise the reason, by name. shots.json's second half is the loader itself
        /// (<see cref="DeliveryRefusal"/>).</summary>
        private static string? ReadBack(string fileName, string? text, byte[] bytes)
        {
            text ??= "";
            var back = ReadBackAsTheKitWill(bytes);
            if (!string.Equals(back, text, StringComparison.Ordinal))
            {
                // A text that begins with U+FEFF is named by that character: the readers always drop the first mark
                // (EF BB BF), whatever follows it — a second mark included, which is why this is not the first difference.
                var at = 0;
                if (text[0] != '\uFEFF')
                    while (at < text.Length && at < back.Length && text[at] == back[at]) at++;
                if (at >= text.Length) return Unreadable(fileName, "its text is not the text the kit's readers read back");
                var code = Levers.UnsendableCharacter(text.Substring(at, 1)) ?? $"U+{(int)text[at]:X4}";
                return Unreadable(fileName, at == 0 && text[0] == '\uFEFF'
                    ? $"it begins with {code}, which the kit's readers drop as a byte-order mark"
                    : $"its character {at} is {code}, which the kit's readers read back as U+FFFD");
            }
            return null;
        }

        /// <summary>The ready block's four fields, each read by <c>HygieneReadyGate</c> as a command.</summary>
        private static readonly string[] ReadyFields = { "mute", "muteGet", "resistOn", "resistOff" };

        /// <summary>
        /// adapter.json, read as the kit will (<see cref="ReadAsTheKitWill"/>), and then its <c>ready</c> block (tenth audit,
        /// M1): <c>HygieneReadyGate</c> reads each of its four fields with a cast that renders a number or a bool as text —
        /// <c>"mute": 5</c> runs as the command "5" — while the lever list reads strings only, so no row named what the block
        /// would run. A <c>ready</c> that is not an object, or a field of it that is not a string, is refused by name, as the
        /// site refuses both (<c>lintAdapterJson</c>). The eleventh audit adds, each refused by name as the site refuses it:
        /// a field that is blank (the gate reads it as no command, while the lever list asked about " "); a field holding
        /// an unsendable character, <c>muteGet</c> included (it was no lever then, so no lever rule saw it); and a block the
        /// ready gate itself fails on, through the gate's own reader (<c>HygieneSpec.From</c>, <c>HygieneSpec.Problem</c>).
        /// The fourteenth audit adds: every field's command AS THE READY GATE SENDS IT — <c>muteGet</c> as
        /// <c>get &lt;muteGet&gt;</c> — held to <see cref="Levers.MaxLeverLength"/>, by name, in the lever cap's words.
        /// </summary>
        private static string? ReadAdapter(string? text, byte[] bytes, out JObject? root)
        {
            if (ReadAsTheKitWill(AdapterFileName, text, bytes, out root) is { } unreadable) return unreadable;
            var ready = root!["ready"];
            if (ready == null) return null;
            if (ready is not JObject block) return Unreadable(AdapterFileName, $"its \"ready\" is {Kind(ready)}, not an object");
            foreach (var field in ReadyFields)
            {
                if (block[field] is not { } value) continue;
                if (value.Type != JTokenType.String)
                    return Unreadable(AdapterFileName, $"its ready.{field} is {Kind(value)}, not a command string");
                var command = value.Value<string>();
                // the eleventh audit, TS2: the ready gate reads a blank write as no command (HygieneSpec.Str), while the
                // lever list asked a person to tick " "
                if (string.IsNullOrWhiteSpace(command))
                    return Unreadable(AdapterFileName, $"its ready.{field} is blank, not a command string");
                // the eleventh audit, M3: muteGet was no lever then, so no lever rule saw a line break in it — every field is
                // held to the kit's one set here
                if (Levers.UnsendableCharacter(command) is { } code)
                    return $"{AdapterFileName}'s ready.{field} holds {code}, a line break, control or invisible formatting character, " +
                           "so the command the ready gate sends is not the one written — nothing was written";
                // the fourteenth audit, S1: muteGet was capped by nothing but the 64 KB file, and the ready gate sent
                // `get` + it un-ticked (51 s in one pump at 2 KB). Every field is held to the lever cap as the command the
                // ready gate SENDS — muteGet as `get <muteGet>`, a lever since that audit — in the lever check's own words
                var sent = field == "muteGet" ? Levers.MuteGetLever(command!) : command!;
                if (sent.Length > Levers.MaxLeverLength)
                    return $"{AdapterFileName}'s ready.{field}: {Levers.TooLongLeverRefusal(sent)}";
            }
            // THE READY GATE'S OWN REFUSAL (the eleventh audit, ruling 1): a block that names no command it reads fails ready
            // on every capture, so it is refused here, in the gate's words, through the gate's own reader
            if (HygieneSpec.From(root!).Problem is { } problem)
                return $"{problem}, which fails ready on every capture — nothing was written";
            return null;
        }

        private static string Kind(JToken token) => token.Type switch
        {
            JTokenType.Integer or JTokenType.Float => "a number",
            JTokenType.Boolean => "a boolean",
            JTokenType.Null => "null",
            JTokenType.Array => "an array",
            JTokenType.Object => "an object",
            JTokenType.String => "a string",
            _ => token.Type.ToString().ToLowerInvariant(),
        };

        /// <summary>Two hex shas, whitespace and case aside. Null (absent, or never recorded) is
        /// never equal to anything — including another null.</summary>
        private static bool SameSha(string? a, string? b) =>
            a != null && b != null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

        private static bool Delivered(IReadOnlyList<string> everDelivered, string? sha) =>
            sha != null && everDelivered.Any(s => string.Equals(s, sha, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// The sha of a file that is there, null when it is NOT there — and a named refusal when it
        /// is there and cannot be read. The distinction is the whole of audit m1: Sha256OfFile
        /// answers null for both, so a chmod-000 file read as "absent" was replaced with no backup.
        /// </summary>
        private static bool TryReadSha(string path, string fileName, out string? sha, out string? refusal)
        {
            sha = null;
            refusal = null;
            try
            {
                if (!File.Exists(path)) return true;
                sha = Sha256OfBytes(File.ReadAllBytes(path));
                return true;
            }
            catch (Exception e)
            {
                refusal = $"{fileName} is on disk but this editor cannot read it ({e.Message}) — it " +
                          "could not be backed up, so nothing was written; fix its permissions and press Send again";
                return false;
            }
        }

        /// <summary>
        /// Copy a file that is the STUDIO's, before it is replaced. Theirs means: on disk, not
        /// already these exact bytes, and not a sha the cloud has ever delivered to this project —
        /// the LIST, not just the last pair, so the kit's own bytes from a send that half-failed
        /// are never re-reported to the studio as their edit. Returns null, or why nothing may be
        /// written.
        ///
        /// <paramref name="claimAsLocalEdit"/> is false when synced.json exists and cannot be read
        /// (second audit, M1): nothing then knows whose bytes these are, so the copy is still taken
        /// — it is the cheap half of the trade — but the studio is not told that a file they wrote
        /// was replaced, because that may well be this kit's own earlier delivery.
        /// </summary>
        private static string? Backup(string path, string? onDisk, string sha,
            IReadOnlyList<string> everDelivered, string stamp, ref bool replacedLocalEdits,
            bool claimAsLocalEdit)
        {
            if (onDisk == null) return null;
            if (SameSha(onDisk, sha)) return null;
            if (Delivered(everDelivered, onDisk)) return null;

            var backup = path + ".local-" + stamp + ".bak";
            try
            {
                File.Copy(path, backup, overwrite: true);
            }
            catch (Exception e)
            {
                return $"a local edit could not be backed up to {Path.GetFileName(backup)} " +
                       $"({e.Message}) — it has NOT been replaced";
            }
            if (claimAsLocalEdit) replacedLocalEdits = true;
            return null;
        }

        /// <summary>
        /// One file, already backed up if it needed to be. A file that already IS these bytes is
        /// left completely alone — not even its timestamp moves.
        ///
        /// Temp-then-replace, never truncate-in-place: a half-written shots.json is a shot list
        /// the game cannot load. <c>File.Replace</c> over an existing target keeps the old file
        /// until the new one is in place.
        /// </summary>
        private static string? WriteOne(string path, byte[] bytes, string sha, string? onDisk)
        {
            if (SameSha(onDisk, sha)) return null;

            var tmp = path + ".tmp-" + Pid;
            try
            {
                File.WriteAllBytes(tmp, bytes);
                AtomicFile.Swap(tmp, path);
                return null;
            }
            catch (Exception e)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { }
                return e.Message;
            }
        }

        /// <summary>The history, with <paramref name="sha"/> as its newest entry: distinct, oldest
        /// first, never longer than <see cref="MaxRemembered"/>. Re-sending a pair moves it to the
        /// end rather than duplicating it.</summary>
        private static IReadOnlyList<string> Remember(IReadOnlyList<string> sofar, string sha)
        {
            var list = new List<string>();
            foreach (var s in sofar)
            {
                if (string.IsNullOrWhiteSpace(s)) continue;
                if (string.Equals(s, sha, StringComparison.OrdinalIgnoreCase)) continue;
                if (list.Contains(s)) continue;
                list.Add(s);
            }
            list.Add(sha);
            while (list.Count > MaxRemembered) list.RemoveAt(0);
            return list;
        }

        /// <summary>Write synced.json (atomically — see <see cref="AtomicFile"/>). Returns null, or
        /// why it could not be written.</summary>
        private static string? WriteSynced(string projectRoot, string shotsSha, string adapterSha,
            IReadOnlyList<string> cloudShots, IReadOnlyList<string> cloudAdapters, string runId,
            bool replacedLocalEdits)
        {
            try
            {
                AtomicFile.Write(SyncedFile(projectRoot), new JObject
                {
                    ["$schemaVersion"] = SyncedSchemaVersion,
                    ["shotsSha256"] = shotsSha,
                    ["adapterSha256"] = adapterSha,
                    ["runId"] = runId ?? "",
                    ["at"] = DateTime.UtcNow.ToString("o"),
                    // Beside the run that made it, so a retry of the SAME send reports it again
                    // rather than answering about its own bytes (M5).
                    ["replacedLocalEdits"] = replacedLocalEdits,
                    ["cloudShots"] = new JArray(cloudShots),
                    ["cloudAdapters"] = new JArray(cloudAdapters),
                }.ToString(Formatting.Indented));
                return null;
            }
            catch (Exception e)
            {
                return e.Message;
            }
        }

        /// <summary>Lowercase hex sha256 of the UTF-8 bytes of <paramref name="text"/>, with no BOM
        /// — the same bytes that get written, hashed from the same array.</summary>
        public static string Sha256OfText(string text) => Sha256OfBytes(Utf8NoBom(text));

        public static byte[] Utf8NoBom(string text) =>
            new UTF8Encoding(false).GetBytes(text ?? "");

        /// <summary>
        /// THE TEXT THE KIT'S READERS WILL READ BACK from <paramref name="bytes"/> (tenth audit, S1): decoded exactly as
        /// <c>File.ReadAllText</c> decodes the file these bytes become — UTF-8, with a leading byte-order mark read past.
        /// Every reader of the two files goes through <c>File.ReadAllText</c> (<c>JsonShotLoader.LoadFromPath</c>,
        /// <c>Levers.Needed</c>, <c>AdapterJson.Load</c>, <c>HygieneReadyGate.Load</c>), so this is the text they read;
        /// <c>TheCheckDecodesTheBytesExactlyAsFileReadAllTextDoes</c> holds it to <c>File.ReadAllText</c> itself.
        /// </summary>
        internal static string ReadBackAsTheKitWill(byte[] bytes)
        {
            using (var reader = new StreamReader(new MemoryStream(bytes, writable: false), Encoding.UTF8,
                       detectEncodingFromByteOrderMarks: true))
                return reader.ReadToEnd();
        }

        public static string Sha256OfBytes(byte[] bytes)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var hash = sha.ComputeHash(bytes);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>Lowercase hex sha256 of a file, or null when it is absent or unreadable.</summary>
        public static string? Sha256OfFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return Sha256OfBytes(File.ReadAllBytes(path));
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// WHAT SYNCED.JSON SAYS. <c>Exists</c> false is a project that has never synced — the
        /// first-run state, not an error. <c>Exists</c> true with <c>Parsed</c> false is a file
        /// this kit cannot read, which the lever gate treats as GATED (fail closed): the one thing
        /// a damaged record must never do is open a project the cloud has written to.
        ///
        /// <c>CloudShots</c> / <c>CloudAdapters</c> are every sha the cloud has delivered here,
        /// oldest first. A record with NO history lists — the v1 shape; no released kit writes one,
        /// but a hand-edited or hand-trimmed file can be one — has its two top-level shas read as
        /// one-element lists, so it keeps exactly the gate it describes.
        ///
        /// <c>Parsed</c> IS NOT "IT IS JSON" (second audit, M2). <c>{}</c> and
        /// <c>{"cloudShots": 5}</c> parse perfectly and say nothing about any delivery, and reading
        /// them as an empty record turned the gate OFF over files the cloud had written — while the
        /// README and TRUST.md both said a synced.json this kit cannot read gates everything.
        ///
        /// A DOCUMENT CARRIES THE RECORD ONLY WHEN IT NAMES AT LEAST ONE SHA (third audit, U4) —
        /// a non-blank string at the top level or in either history list. The second audit's rule
        /// ("a sha as a string, or a history as an array") still let <c>{"cloudShots": [7]}</c>
        /// through: a list, of nothing usable, read as an empty record that ungated the cloud's
        /// files. Every record this kit writes names the incoming sha in its history, so nothing
        /// it wrote is lost by the stricter rule.
        /// </summary>
        public sealed class SyncedRecord
        {
            public bool Exists { get; set; }
            public bool Parsed { get; set; }
            public string? ShotsSha256 { get; set; }
            public string? AdapterSha256 { get; set; }
            public IReadOnlyList<string> CloudShots { get; set; } = Array.Empty<string>();
            public IReadOnlyList<string> CloudAdapters { get; set; } = Array.Empty<string>();
            /// <summary>The send that wrote this record, and what it did to a file of the studio's
            /// — so a RETRY of that same send says the same thing (M5).</summary>
            public string? RunId { get; set; }
            public bool ReplacedLocalEdits { get; set; }
        }

        public static SyncedRecord ReadSynced(string projectRoot)
        {
            var record = new SyncedRecord();
            string text;
            try
            {
                var path = SyncedFile(projectRoot);
                if (!File.Exists(path)) return record;
                record.Exists = true;
                text = File.ReadAllText(path);
            }
            catch (Exception)
            {
                // There IS a synced.json and this kit cannot read it: Exists without Parsed.
                record.Exists = true;
                return record;
            }

            try
            {
                var o = NovaJson.ParseObject(text);
                // M2 / U4 — PARSED means "this document carries the record", not "this document is
                // JSON". A file that names no sha at all — `{}`, `{"cloudShots": 5}`,
                // `{"cloudShots": [7]}`, only blank strings — says nothing about any delivery: it is
                // a record this kit cannot read, and it leaves here Exists-but-not-Parsed, which the
                // lever gate treats as GATED (fail closed). A top-level "" beside a history that
                // names the sha (what a first send writes) is still read normally.
                var shotsSha = Str(o["shotsSha256"]);
                var adapterSha = Str(o["adapterSha256"]);
                var cloudShots = History(o["cloudShots"], shotsSha);
                var cloudAdapters = History(o["cloudAdapters"], adapterSha);
                if (cloudShots.Count == 0 && cloudAdapters.Count == 0) return record;

                record.Parsed = true;
                record.ShotsSha256 = shotsSha;
                record.AdapterSha256 = adapterSha;
                record.CloudShots = cloudShots;
                record.CloudAdapters = cloudAdapters;
                record.RunId = Str(o["runId"]);
                record.ReplacedLocalEdits = o["replacedLocalEdits"]?.Type == JTokenType.Boolean
                                            && o["replacedLocalEdits"]!.Value<bool>();
                return record;
            }
            catch (Exception)
            {
                return record;
            }
        }

        /// <summary>One history list, with the top-level sha folded in — which is the whole of the
        /// v1 reader: a file with no list still delivered that one pair.</summary>
        private static IReadOnlyList<string> History(JToken? token, string? topLevel)
        {
            var list = new List<string>();
            if (token is JArray arr)
            {
                foreach (var t in arr)
                {
                    var s = Str(t);
                    if (s != null && !list.Contains(s)) list.Add(s);
                }
            }
            if (topLevel != null && !list.Contains(topLevel)) list.Add(topLevel);
            return list;
        }

        /// <summary>What the last sync wrote, as recorded in <c>Library/Nova/synced.json</c>: the
        /// two shas, or nulls when the file is absent or unreadable. Never throws — a project that
        /// has never synced is the first-run state, not an error.</summary>
        public static (string? ShotsSha256, string? AdapterSha256) LastSynced(string projectRoot)
        {
            var record = ReadSynced(projectRoot);
            return (record.ShotsSha256, record.AdapterSha256);
        }

        private static string? Str(JToken? t) =>
            t != null && t.Type == JTokenType.String && !string.IsNullOrWhiteSpace(t.Value<string>())
                ? t.Value<string>()!.Trim()
                : null;
    }
}
