using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Slice A2′ (F16) — WHICH COMMANDS A SHOT THE WEBSITE SENT MAY RUN IN THIS GAME.
    ///
    /// A shot is data, and one of the things that data can carry is a `cheat`: a reflection write
    /// into the studio's own game. Locally that is fine — the person who typed it is at the
    /// keyboard. A shot DELIVERED from the website is not that, so each state-writing command it
    /// uses has to be ticked once, by a person, in the Nova Capture window. The ticks live in
    /// <c>Library/Nova/levers.json</c>, which is written ONLY by that window: never by the agent,
    /// never by a job, never from anything the server sent.
    ///
    /// Pure <c>System.IO</c>, no Unity API — the project root is passed in, so the kit's EditMode
    /// tests cover the exact comparisons that run in production.
    ///
    /// NOTHING HERE THROWS. A missing or malformed levers.json is NOTHING APPROVED (fail closed),
    /// which stops shots rather than crashing an editor — and a malformed one is not written over
    /// (<see cref="SetApproved"/>, eighth audit, S2).
    /// </summary>
    public static class Levers
    {
        public const string FileName = "levers.json";
        public const int SchemaVersion = 1;

        /// <summary>
        /// THE MOST LEVERS ONE DELIVERY MAY ASK FOR (ninth audit, S1). <see cref="SyncNova"/> refuses a pair of files whose
        /// <see cref="NeededFrom"/> list is longer, before writing anything, and the hosted lint refuses to send one with
        /// the same number (<c>MAX_LEVERS</c>; <c>Editor/Tests/Fixtures/deliveries.cases.json</c> holds both to it). A bound
        /// on the input is what ends the editor-stall class: the window's cost grows with needed × ticked, and a server
        /// decides how many levers a file asks for. The worst shape at the cap, timed cold
        /// (<c>RowsAtTheLeverCapWithTheWorstShapeTakeUnderASecond</c>): 300 needed templates sharing their literal text
        /// before the first placeholder, 300 stale ticked templates of the same kind and 50 refused commands. The ninth fold
        /// timed it with levers of about 22 characters (190–268 ms), and the eleventh audit showed that was not the worst:
        /// the byte cap let each lever reach about 1.7 KB, and the same shape took 925–980 ms. Since the eleventh fold every
        /// lever is also capped at <see cref="MaxLeverLength"/> characters, and the test builds all three lists at that
        /// length: 305–328 ms over five runs, a margin of about 3× against one second. The ticks in levers.json are the
        /// person's own and accumulate across sends; they are outside this bound.
        /// </summary>
        public const int MaxLevers = 300;

        /// <summary>
        /// THE LONGEST LEVER ONE DELIVERY MAY ASK FOR, in characters of the lever's own text, prefix and all (the eleventh
        /// audit, M1). <see cref="SyncNova"/> refuses a pair of files holding a longer one, before writing anything, and the
        /// hosted lint refuses to send one (<c>MAX_LEVER_LENGTH</c>; <c>deliveries.cases.json</c>'s <c>maxLeverLength</c>
        /// holds both to it). The window's cost grows with needed × ticked × lever length: <see cref="MaxLevers"/> bounded
        /// the first, and the byte cap alone let a lever reach about 1.7 KB, where the worst shape at the cap took 925–980 ms.
        /// </summary>
        public const int MaxLeverLength = 256;

        /// <summary>The line a gated command leaves in the director's log. Named here so the gate
        /// and its test cannot drift (invariant 99's corollary).</summary>
        public static string NotApprovedLog(string command) =>
            $"lever not approved on this machine: '{command}'";

        /// <summary>The lever a shot's `timeScale` step needs: exactly that word, whatever the
        /// factor. A factor is a number, not a command — approving "slow this game down" once is
        /// the question a person can actually answer, and the hosted lint computes the same
        /// string.</summary>
        public const string TimeScaleLever = "timeScale";

        /// <summary>The lever an adapter.json `overlayTypeNames` entry needs. One per type name,
        /// verbatim as the file spells it.</summary>
        public static string HideOverlayLever(string typeName) => HideOverlayPrefix + typeName;

        private const string HideOverlayPrefix = "hide-overlay ";

        /// <summary>The line the director leaves when it does NOT hide an overlay the cloud's
        /// adapter.json asked it to hide. Named here for the same reason as
        /// <see cref="NotApprovedLog"/>.</summary>
        public static string OverlayLeftVisibleLog(string typeName) =>
            $"overlay '{typeName}' left visible — lever not approved on this machine: " +
            HideOverlayLever(typeName);

        /// <summary>
        /// WHICH OVERLAYS MAY BE DISABLED on this project. <paramref name="wanted"/> is the list
        /// the director is about to hide; <paramref name="fromAdapterJson"/> is what the cloud's
        /// adapter.json asked for. When the gate is live, a name that came from a JSON file is
        /// kept only if `hide-overlay &lt;name&gt;` is ticked — exactly the strings
        /// <see cref="Needed"/> asks about, so the gate can never enforce a rule the person was
        /// never shown for the names it CAN list (invariant 99; a compiled adapter's own commands
        /// are listed too, through <see cref="Rows"/>).
        ///
        /// <paramref name="adapterIsJsonDriven"/> IS THE WHOLE OF AUDIT S1 (second audit,
        /// 2026-09-21). "This name is the studio's own compiled code, so it is never gated" is only
        /// true of an adapter a studio COMPILED. The default adapter — the one every UI-onboarded
        /// studio has — fills its own overlay list from adapter.json once, at registration, and the
        /// director falls back to that field when today's adapter.json lists none. So a type name
        /// the cloud delivered yesterday read as "the studio's own" today and was disabled in a
        /// running game with nothing ticked and no line in the log. For the default adapter every
        /// name came from a JSON file, so every name is gated while the gate is live; a name that
        /// only a studio's own compiled adapter carries is still never gated.
        ///
        /// AN OVERLAY LEVER IS NEVER A TEMPLATE (third audit, S1). The approval is the EXACT text
        /// <c>hide-overlay &lt;TypeName&gt;</c>, compared ordinal. It used to go through the
        /// template matcher, and a type name is a DATA field nothing declares: an adapter.json
        /// naming the overlay <c>{t}</c> offered <c>hide-overlay {t}</c> as an ordinary row, and one
        /// tick of it switched off every overlay type any later adapter.json named — while the
        /// window showed each of those rows unticked. <see cref="CoveringApproval"/> applies the
        /// same rule, so the window and this gate cannot disagree about an overlay.
        /// </summary>
        public static IReadOnlyList<string> OverlaysAllowed(string projectRoot,
            IReadOnlyList<string>? wanted, IReadOnlyList<string>? fromAdapterJson, Action<string>? log,
            bool adapterIsJsonDriven, bool cloudContent = false)
        {
            var all = wanted ?? (IReadOnlyList<string>)Array.Empty<string>();
            if (all.Count == 0 || !GateActiveFor(projectRoot, cloudContent)) return all;

            // ONE PASS OVER DISTINCT NAMES, EACH LOOKED UP IN A SET (the fourteenth audit, M1): adapter.json may list one
            // name thousands of times in 64 KB, and each copy was a scan of the other lists and a "left visible" line.
            var approved = new HashSet<string>(Approved(projectRoot), StringComparer.Ordinal);
            var fromJson = fromAdapterJson == null ? null : new HashSet<string>(fromAdapterJson, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var kept = new List<string>();
            foreach (var name in all)
            {
                if (!seen.Add(name)) continue;
                var delivered = adapterIsJsonDriven || (fromJson != null && fromJson.Contains(name));
                if (!delivered || approved.Contains(HideOverlayLever(name)))
                {
                    kept.Add(name);
                    continue;
                }
                // Skipped, never thrown and never silent: an overlay left up is visible in the
                // footage, and the reason has to be in the run's log beside it.
                log?.Invoke(OverlayLeftVisibleLog(name));
            }
            return kept;
        }

        /// <summary>
        /// May this lever run on this project right now? The question the gate asks, afresh, for a
        /// write that does NOT go through <see cref="ICheatBridge"/>: a camera pose's spec
        /// (<c>CameraPose.Pose</c>). A shot's <c>timeScale</c> step asks the same question under
        /// the liveness its attempt holds (<see cref="LeverGateBridge.LeverAllowed"/>, the thirteenth
        /// audit), and hiding an overlay asks it once for the run (<see cref="OverlaysAllowed"/>).
        /// True whenever the gate is not live (locally authored files are the person's own).
        /// </summary>
        public static bool LeverAllowed(string projectRoot, string lever, bool cloudContent = false) =>
            !GateActiveFor(projectRoot, cloudContent) || Allows(Approved(projectRoot), lever);

        public static string FilePath(string projectRoot) =>
            Path.Combine(RelayPaths.NovaDir(projectRoot), FileName);

        /// <summary>
        /// The commands ticked on THIS machine, in the order the file lists them (duplicates and
        /// blanks dropped). Missing file, unreadable file, not JSON, wrong shape → EMPTY. Never
        /// throws, and never guesses: an unreadable approval list approves nothing.
        ///
        /// ANYTHING IN <c>approved</c> THAT IS NOT A COMMAND makes the whole file unreadable
        /// (second audit, K31). A number, an object or a null where a command should be is a file
        /// this kit does not understand, and half-understanding an approval list is how a list gets
        /// read as shorter — or longer — than the person who ticked it believes. It leaves here as
        /// the exception the one <c>catch</c> below turns into "nothing is approved", which is the
        /// only exit that cannot publish a half-read list.
        /// </summary>
        public static IReadOnlyList<string> Approved(string projectRoot) => ReadApproved(projectRoot, out _);

        /// <summary><see cref="Approved"/>, also saying why an EXISTING levers.json could not be read — null when it was
        /// read or is not there. An <c>approved</c> that is absent or not a list is a file this kit does not understand
        /// too. <see cref="SetApproved"/> will not write over such a file (eighth audit, S2).</summary>
        private static List<string> ReadApproved(string projectRoot, out string? unreadable)
        {
            unreadable = null;
            var list = new List<string>();
            try
            {
                var path = FilePath(projectRoot);
                if (!System.IO.File.Exists(path)) return list;
                var root = NovaJson.ParseObject(System.IO.File.ReadAllText(path));
                // Ninth audit, M3: a version this kit did not write is a file it does not understand. A newer kit's
                // levers.json may say things this one would drop on the next tick (a list of denials), so it approves
                // nothing and is not written over, as an unreadable file (ATickOverALeversFileOfAnotherVersionLeavesIt).
                var version = root["$schemaVersion"];
                if (version == null || version.Type != JTokenType.Integer || !NovaJson.TryNumber(version, out var number)
                    || number != SchemaVersion)
                    throw new FormatException($"{FileName}: $schemaVersion is " +
                                              (version == null ? "missing" : version.ToString(Formatting.None)) +
                                              $", not {SchemaVersion}, so it was written by a newer kit or by hand");
                if (root["approved"] is not JArray arr)
                    throw new FormatException($"{FileName}: 'approved' is not a list of commands");
                foreach (var t in arr)
                {
                    if (t.Type != JTokenType.String)
                        throw new FormatException(
                            $"{FileName}: 'approved' holds a {t.Type} where a command should be");
                    var s = t.Value<string>();
                    if (string.IsNullOrEmpty(s) || list.Contains(s!)) continue;
                    list.Add(s!);
                }
            }
            catch (Exception e)
            {
                // Fail CLOSED: an approval file that cannot be read approves nothing. Returning
                // what had been collected so far would be a half-read list read as a whole one.
                unreadable = e.Message;
                return new List<string>();
            }
            return list;
        }

        /// <summary>
        /// Tick or untick one command. The ONLY writer of levers.json, called from the Nova Capture
        /// window. Returns null on success, or the reason it could not be written.
        ///
        /// AN EXISTING levers.json THIS KIT CANNOT READ IS NOT WRITTEN OVER (eighth audit, S2). It approves
        /// nothing (<see cref="Approved"/>), and a tick used to rewrite it from that empty list, so every other
        /// approval in it was gone without a word. The reason comes back instead, which the window shows as it shows
        /// a failed write (<c>ATickOverALeversFileTheKitCannotReadLeavesItByteForByteAndSaysWhy</c>).
        /// </summary>
        public static string? SetApproved(string projectRoot, string command, bool approved)
        {
            if (string.IsNullOrWhiteSpace(command)) return "a blank command cannot be approved";
            // Tickable one way only (second audit, M3): an entry whose verb is a {placeholder}
            // cannot be ticked, but one already in the file can always be UNticked — a person must
            // be able to take back anything that is in there, however it got there.
            if (approved && NotTickableReason(command) is { } why) return why;
            try
            {
                var list = ReadApproved(projectRoot, out var unreadable);
                if (unreadable != null)
                    return $"{FilePath(projectRoot)} cannot be read ({unreadable}); fix or delete it first. Nothing was " +
                           "written: writing it now would replace every approval in it with this one.";
                if (approved)
                {
                    if (!list.Contains(command)) list.Add(command);
                }
                else
                {
                    list.RemoveAll(c => string.Equals(c, command, StringComparison.Ordinal));
                }
                AtomicFile.Write(FilePath(projectRoot), new JObject
                {
                    ["$schemaVersion"] = SchemaVersion,
                    ["approved"] = new JArray(list),
                }.ToString(Formatting.Indented));
                return null;
            }
            catch (Exception e)
            {
                return $"could not write {FilePath(projectRoot)}: {e.Message}";
            }
        }

        /// <summary>
        /// The distinct levers the files currently on disk can pull, sorted ordinal
        /// — every shot's `setup` entry, every `cheat` / `cheatUntil` command, the adapter's
        /// `ready` `mute` / `resistOn` / `resistOff` and `get &lt;muteGet&gt;` (a `get` runs property getters, which is
        /// code — the fourteenth audit), plus the two
        /// writes that are not ICheatBridge commands at all: `timeScale` once if any shot changes
        /// it, and `hide-overlay &lt;TypeName&gt;` per adapter.json `overlayTypeNames` entry.
        /// Commands are
        /// listed AS AUTHORED: a `{param}` template stays a template, because that is what a person
        /// is being asked to approve.
        ///
        /// The hosted lint computes the same list from the same two files
        /// (<c>apps/renderer/src/game-ads/shots/shots-lint.ts</c>, <c>levers</c>) — this mirrors it.
        /// </summary>
        public static IReadOnlyList<string> Needed(string projectRoot) =>
            NeededFrom(ReadOrNull(RelayPaths.NovaShotsFile(projectRoot)),
                ReadOrNull(SyncNova.AdapterFile(projectRoot)));

        private static string? ReadOrNull(string path)
        {
            try { return System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : null; }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// THE SHOTS HALF, FROM ONE DOCUMENT — the shared definition the hosted lint mirrors
        /// (<c>apps/renderer/src/game-ads/shots/shots-lint.ts</c>), pinned case by case in
        /// <c>Editor/Tests/Fixtures/shots-grammar.cases.json</c> (`levers`).
        ///
        /// A RAW TRAVERSAL, deliberately: it does not care whether a shot LOADS, or what
        /// <c>$schemaVersion</c> says, because a shot the loader refuses still has to have its
        /// commands shown to the person deciding. Not JSON, not an object, or no <c>shots</c>
        /// array → nothing, which is the window's and the gate's reading of a file on disk; a DELIVERY
        /// of such a file is refused instead (<see cref="SyncNova.DeliveryRefusal"/>, tenth audit). For each shot object: every non-empty string in <c>setup</c>; the
        /// <c>command</c> of every <c>cheat</c> / <c>cheatUntil</c> step; and
        /// <see cref="TimeScaleLever"/> once for any <c>timeScale</c> step. Nothing else — a
        /// vision prompt, a condition and an element name are not levers. Sorted ordinal,
        /// distinct.
        /// </summary>
        public static IReadOnlyList<string> NeededFromShotsJson(string? shotsText)
        {
            JObject? root = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(shotsText)) root = NovaJson.ParseObject(shotsText!);
            }
            catch (Exception)
            {
                // A shots file that is not JSON needs no levers HERE — the window and the gate read the file on disk, and
                // the kit cannot load a shot out of it either (the loader says why). A DELIVERY is never read this way:
                // SyncNova's delivery check runs the loader on it, which refuses what it cannot parse (tenth audit, S1; since
                // the eleventh, the check IS the loader).
            }
            return root == null ? new List<string>() : NeededFromShots(root);
        }

        /// <summary>The same list, from a document already parsed — what <see cref="SyncNova"/>'s delivery check reads
        /// (tenth audit, S1), so it never meets a parse failure to answer "nothing" for.</summary>
        internal static List<string> NeededFromShots(JObject root)
        {
            var found = new List<string>();
            // A set beside the list (ninth audit): SyncNova counts this list for every delivery before it writes, and a
            // 512 KB file of short distinct commands made List.Contains quadratic. The answer is the same list.
            var distinct = new HashSet<string>(StringComparer.Ordinal);
            void Add(JToken? t)
            {
                if (t == null || t.Type != JTokenType.String) return;
                var s = t.Value<string>();
                if (string.IsNullOrEmpty(s) || !distinct.Add(s!)) return;
                found.Add(s!);
            }

            if (root["shots"] is JArray shots)
            {
                foreach (var shotToken in shots)
                {
                    if (shotToken is not JObject shot) continue;
                    if (shot["setup"] is JArray setup)
                        foreach (var cmd in setup) Add(cmd);
                    if (shot["steps"] is not JArray steps) continue;
                    foreach (var stepToken in steps)
                    {
                        if (stepToken is not JObject step) continue;
                        var kind = step["kind"]?.Type == JTokenType.String
                            ? step["kind"]!.Value<string>()
                            : null;
                        if (kind == "cheat" || kind == "cheatUntil") Add(step["command"]);
                        // A `timeScale` step writes `Time.timeScale` directly — not through the
                        // cheat bridge, so not through the bridge's decorator. It is ONE lever
                        // whatever the factor: a number is not a command, and "may a delivered
                        // shot slow this game down" is the question a person can answer.
                        else if (kind == "timeScale") Add(new JValue(TimeScaleLever));
                    }
                }
            }

            found.Sort(StringComparer.Ordinal);
            return found;
        }

        /// <summary>The same list, from texts rather than from disk (the sync handler has them in
        /// hand before they are written).</summary>
        public static IReadOnlyList<string> NeededFrom(string? shotsText, string? adapterText) =>
            Union(NeededFromShotsJson(shotsText), NeededFromAdapterJson(adapterText));

        /// <summary>The same list, from two documents already parsed (tenth audit, S1: <see cref="SyncNova"/>'s delivery
        /// check).</summary>
        internal static IReadOnlyList<string> NeededFromParsed(JObject shots, JObject? adapter) =>
            Union(NeededFromShots(shots), NeededFromAdapter(adapter));

        private static IReadOnlyList<string> Union(IReadOnlyList<string> shotsLevers, IReadOnlyList<string> adapterLevers)
        {
            var found = new List<string>(shotsLevers);
            var distinct = new HashSet<string>(found, StringComparer.Ordinal);
            foreach (var lever in adapterLevers)
                if (distinct.Add(lever))
                    found.Add(lever);
            found.Sort(StringComparer.Ordinal);
            return found;
        }

        /// <summary>
        /// THE ADAPTER HALF, FROM ONE DOCUMENT — everything an <c>adapter.json</c> asks to write
        /// into the studio's running game, sorted ordinal and distinct. The hosted lint computes
        /// the same list from the same document, and
        /// <c>Editor/Tests/Fixtures/adapter-levers.cases.json</c> is the file BOTH sides are run
        /// against, case by case: a lever this side names differently is a send the website can
        /// never complete ("DIFFERENT lever lists"), so the two definitions are pinned to shared
        /// data rather than to each other's source.
        ///
        /// Three things are in it:
        /// <list type="bullet">
        /// <item>the <c>ready</c> block's commands — <c>mute</c>, <c>resistOn</c>, <c>resistOff</c>, and, since the
        /// fourteenth audit, <c>muteGet</c> as the command the ready gate sends, <see cref="MuteGetLever"/>: a `get` runs
        /// property getters in the studio's game, which is code, so it is a lever like any other.</item>
        /// <item><c>hide-overlay &lt;TypeName&gt;</c>, one per <c>overlayTypeNames</c> entry — a
        /// MonoBehaviour disabled by type name, which is a write that never touches
        /// <see cref="ICheatBridge"/>.</item>
        /// <item><see cref="CameraSpecLever"/> when the <c>camera</c> block says anything (second
        /// audit, M9). That block is cloud content that decides WHICH METHODS a
        /// <c>camera-pose</c> step invokes and on which Component — `camera-pose 1 2 3 …` is one
        /// tick, and it said nothing about `DeleteSave` being the method it calls.</item>
        /// </list>
        /// </summary>
        public static IReadOnlyList<string> NeededFromAdapterJson(string? adapterText)
        {
            JObject? root = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(adapterText)) root = NovaJson.ParseObject(adapterText!);
            }
            catch (Exception)
            {
                // Not JSON: no levers HERE (the window, the gate). A delivery is refused instead (tenth audit, S1).
            }
            return NeededFromAdapter(root);
        }

        /// <summary>The same list, from a document already parsed, or none (tenth audit, S1). Overlays and the camera
        /// block are read through <see cref="AdapterJson.From"/>, the reader the director and CameraPose use.</summary>
        internal static List<string> NeededFromAdapter(JObject? root)
        {
            var found = new List<string>();
            // A set beside the list, as NeededFromShots has (the eleventh audit, M2): List.Contains made a 64 KB adapter.json
            // of 10,905 short overlay names take 860 ms to count. The answer is the same list.
            var distinct = new HashSet<string>(StringComparer.Ordinal);
            void Add(string? s)
            {
                if (string.IsNullOrEmpty(s) || !distinct.Add(s!)) return;
                found.Add(s!);
            }
            void AddToken(JToken? t)
            {
                if (t == null || t.Type != JTokenType.String) return;
                Add(t.Value<string>());
            }

            if (root?["ready"] is JObject ready)
            {
                AddToken(ready["mute"]);
                // the fourteenth audit: the read too, as the command the ready gate sends — exactly "get " + the text
                // (HygieneReadyGate.WaitUntilReady), because the tick is compared with what runs, ordinal
                if (ready["muteGet"] is { Type: JTokenType.String } muteGet && muteGet.Value<string>() is { Length: > 0 } read)
                    Add(MuteGetLever(read));
                AddToken(ready["resistOn"]);
                AddToken(ready["resistOff"]);
            }

            // Read through the same parser the director and CameraPose use, so the list asked
            // about and the list acted on cannot differ.
            var adapter = AdapterJson.From(root);
            foreach (var type in adapter.OverlayTypeNames)
                Add(HideOverlayLever(type));
            if (adapter.Camera is { } camera && CameraSpecNeeded(camera))
                Add(CameraSpecLever(camera));

            found.Sort(StringComparer.Ordinal);
            return found;
        }

        /// <summary>The lever a <c>ready.muteGet</c> is — the command <c>HygieneReadyGate</c> sends, exactly: <c>get </c> and
        /// the text as written (the fourteenth audit). The hosted lint builds the same string (<c>lintAdapterJson</c>).</summary>
        public static string MuteGetLever(string muteGet) => "get " + muteGet;

        /// <summary>The one sentence a lever over <see cref="MaxLeverLength"/> is refused with — by <see cref="SyncNova"/>'s
        /// lever check and, prefixed with the field, its ready-block check: the lever's first 40 characters (never half a
        /// surrogate pair), then its length.</summary>
        internal static string TooLongLeverRefusal(string lever)
        {
            var shown = char.IsHighSurrogate(lever[39]) ? 39 : 40; // never half a pair
            return $"the lever {Newtonsoft.Json.JsonConvert.ToString(lever.Substring(0, shown) + "…")} is {lever.Length} characters long, " +
                   $"and this kit takes levers of at most {MaxLeverLength} — nothing was written";
        }

        /// <summary>The verb of <see cref="CameraSpecLever"/>, named once so the kit, the window
        /// and the hosted lint cannot spell it differently.</summary>
        public const string CameraSpecPrefix = "camera-spec";

        /// <summary>
        /// THE LEVER AN adapter.json <c>camera</c> BLOCK NEEDS — exactly
        /// <c>camera-spec &lt;viewType&gt; &lt;setPosition&gt; &lt;setRotation&gt; &lt;setFov&gt;</c>,
        /// single spaces, every value trimmed, a blank or absent <c>viewType</c> written <c>*</c>
        /// and a blank or absent method name written as its own default. The website computes the
        /// same string character for character; a mismatch fails every send, which is why the shape
        /// is spelled out here rather than formatted at each call site.
        ///
        /// WHY IT IS A LEVER AT ALL: those four values decide which three METHODS a <c>camera-pose</c>
        /// step invokes, and — when the step names no Type of its own — which Component it finds. A
        /// step that DOES name a Type is refused on a delivered project unless that Type is this
        /// view type (fourth audit, M2: the step's own Type used to win silently; a spec with no view
        /// type, <c>*</c>, leaves the step's Type standing). A tick on `camera-pose 1 2 3 …` approves
        /// a framing, not `DeleteSave` being the method that framing calls.
        /// </summary>
        public static string CameraSpecLever(CameraAdapter camera) =>
            CameraSpecPrefix + " " + Token(camera.ViewType, "*")
            + " " + Token(camera.SetPosition, "SetPosition")
            + " " + Token(camera.SetRotation, "SetRotation")
            + " " + Token(camera.SetFov, "SetFov");

        /// <summary>
        /// Does this <c>camera</c> block need a tick? Only when it says something a default does
        /// not: a view type, or a method name that is not the one the kit would have called anyway.
        /// An absent or empty block, and a block that spells the three defaults out, ask for
        /// nothing — there is nothing there for a person to decide.
        /// </summary>
        public static bool CameraSpecNeeded(CameraAdapter camera) =>
            !string.IsNullOrWhiteSpace(camera.ViewType)
            || !string.Equals(Token(camera.SetPosition, "SetPosition"), "SetPosition", StringComparison.Ordinal)
            || !string.Equals(Token(camera.SetRotation, "SetRotation"), "SetRotation", StringComparison.Ordinal)
            || !string.Equals(Token(camera.SetFov, "SetFov"), "SetFov", StringComparison.Ordinal);

        private static string Token(string? value, string whenBlank) =>
            string.IsNullOrWhiteSpace(value) ? whenBlank : value!.Trim();

        /// <summary>
        /// THE KIT'S OWN THREE FIXED VERBS — exactly <c>ui-dump</c>, <c>show-ui</c> and <c>hide-ui</c>, with no argument.
        /// They pass a live gate without a tick, whatever file they came from: they run no game code but the UI's own text
        /// getters, which `ui-dump` reads labels through (a game's Text subclass that overrides <c>text</c> runs there), and
        /// the OnEnable of what `show-ui` re-enables — only objects a TICKED `hide-ui &lt;name&gt;` hid; a bare `hide-ui`
        /// fails before it touches anything (corrected by the fifteenth audit, M1: this said "the kit's code is all they
        /// run"). Delivered clicks and UI conditions run game handlers and getters un-ticked anyway, outside the bridge. The director
        /// itself runs the first two (<c>AdDirector.RunAll</c>, <c>ReachReadyState</c>). Deliberately literal: <c>hide-ui
        /// &lt;name&gt;</c> (with an argument) needs a tick — fail closed, since it changes what is on screen.
        ///
        /// `get …` IS NOT ON THE LIST (the fourteenth audit, 2026-09-22). It was, for thirteen rounds, as "a read". It is
        /// reflection into the studio's game: resolving its path reads each type's static <c>Instance</c> and each member
        /// on the way, and a property getter is code — a lazy singleton creates an object in the scene when it is read
        /// (<c>DeliveredGetTests</c>). So a delivered `get`, from a shot or from adapter.json's <c>ready.muteGet</c>, needs a
        /// tick like any lever, and what it costs is the studio's own code.
        /// </summary>
        public static bool IsReadOnly(string? command) =>
            command == "ui-dump" || command == "show-ui" || command == "hide-ui";

        /// <summary>
        /// WHICH TICKED ENTRY COVERS THIS COMMAND? — the ONE covering rule (third audit, S1). The
        /// gate (<see cref="Allows"/>), the window (<see cref="Rows"/>) and slice E's probe
        /// (<see cref="IsTicked"/>) all ask it, because the defect it closes was two functions
        /// answering the same question: the window showed <c>SelectHero Knight</c> UNTICKED (it
        /// compared exact text) while the gate RAN it (it matched the ticked template
        /// <c>SelectHero {hero}</c>), and unticking that row could revoke nothing.
        ///
        /// In order: an entry that IS the command's exact text (ordinal); else the FIRST entry,
        /// in file order, whose template the command is a legal binding of
        /// (<see cref="ShotBinding.MatchesTemplate"/> — <c>SelectHero {hero}</c> covers
        /// <c>SelectHero Knight</c> and refuses <c>SelectHero Knight Extra</c> or
        /// <c>SelectHero Button@Label</c>); else null. Exact first, because a template ticked
        /// verbatim must cover itself and <c>{hero}</c> is not a legal bound value.
        ///
        /// TWO KINDS OF ENTRY COVER NOTHING, whatever they match:
        /// <list type="bullet">
        /// <item>an entry whose FIRST WORD is not literal (<see cref="FirstWordHasPlaceholder"/> —
        /// a <c>{</c> or a quote in it; second audit M3, third audit M2). The verb is what a person
        /// answers about, so <c>{a} {b}</c> or <c>s{a} {b} {c}</c>, ticked once, would approve
        /// every command of that shape — and the bridge reads a quoted first word as the span
        /// inside the quotes, so <c>" {a}" {b}</c> is a placeholder verb in disguise;</item>
        /// <item>as a TEMPLATE, a lever read out of a data field — <c>hide-overlay &lt;Type&gt;</c>
        /// and <c>camera-spec …</c>. Those are names, not commands with parameters: they match
        /// their exact text only (see <see cref="OverlaysAllowed"/>).</item>
        /// </list>
        ///
        /// A COMMAND THAT IS ITSELF A TEMPLATE (fourth audit, S1) — what the window and slice E's
        /// probe ask about for a shot's <c>SelectHero {hero}</c> — is covered, after the exact pass, by
        /// the first ticked template of the SAME SHAPE (<see cref="ShotBinding.SameShape"/>: equal once
        /// every placeholder is one fixed token, with the same placeholders sharing a name — fifth
        /// audit, M4). A re-draft that renames <c>{x}</c> to <c>{hero}</c>
        /// used to leave the needed row unticked while the ticked <c>SelectHero {x}</c> ran every
        /// binding. A LITERAL entry never covers a template, and a BROADER template does not either:
        /// ticked <c>set Player.{a} {b}</c> lets <c>set Player.coins {v}</c>'s commands through but is
        /// not its shape, so that row stays unticked, and names the entry when a witness shows the
        /// overlap (<see cref="Rows"/>).
        ///
        /// A TARGET PLACEHOLDER'S VALUE HOLDS NO DOT (fourth audit, M5) — a check AFTER the match, not
        /// a change to <see cref="ShotBinding.MatchesTemplate"/>, whose value class is the binder's:
        /// ticked <c>set Player.{s} {v}</c> covers <c>set Player.coins 0</c> and not
        /// <c>set Player.Instance.save.coins 0</c> (<see cref="TargetValuesHoldNoDot"/>).
        /// </summary>
        public static string? CoveringApproval(IEnumerable<string>? approved, string? command) =>
            Covering(approved, command, null);

        /// <summary>How many times the covering rule has run in this editor session — for the tests only (ninth audit,
        /// S1: an unchanged <see cref="Rows"/> read must run it zero times). Counted in the one body both callers share.</summary>
        internal static int CoveringApprovalCallsForTests => System.Threading.Volatile.Read(ref _coveringApprovalCalls);

        private static int _coveringApprovalCalls;

        /// <summary><see cref="CoveringApproval"/>'s body. <paramref name="memo"/> is given only by <see cref="Rows"/>, and
        /// answers the same question from the same rule with each text's checks and match done once per call
        /// (<see cref="RowsMemo.CoveringFast"/>).</summary>
        private static string? Covering(IEnumerable<string>? approved, string? command, RowsMemo? memo)
        {
            System.Threading.Interlocked.Increment(ref _coveringApprovalCalls);
            if (approved == null || string.IsNullOrEmpty(command)) return null;
            if (memo != null) return memo.CoveringFast(command!);
            foreach (var entry in approved)
                if (!NotLiteralEnough(entry) && string.Equals(entry, command, StringComparison.Ordinal))
                    return entry;
            if (NeverATemplate(command!)) return null;
            if (ShotBinding.HasPlaceholder(command!))
            {
                // template → template: the same shape only. The HasPlaceholder guard inside
                // UsableTemplate is the rule "a literal never covers a template", said in code (the
                // piece-wise shape test would refuse one too). Two templates of the same shape are
                // equally literal — every rule NotLiteralEnough applies reads braces, quotes, word
                // and dot positions, none of which a renamed placeholder moves — so a command that is
                // not literal enough finds no usable entry of its shape here.
                foreach (var entry in approved)
                    if (UsableTemplate(entry) && ShotBinding.SameShape(entry, command))
                        return entry;
                return null;
            }
            foreach (var entry in approved)
                if (TemplateLetsThrough(entry, command!))
                    return entry;
            return null;
        }

        /// <summary>A ticked entry the gate reads as a TEMPLATE: it holds a placeholder, its verb and
        /// target are literal enough, and it is not a lever read out of a data field.</summary>
        private static bool UsableTemplate(string? entry) =>
            entry != null && ShotBinding.HasPlaceholder(entry) && !NotLiteralEnough(entry) && !NeverATemplate(entry);

        /// <summary>Does the ticked template <paramref name="entry"/> let the concrete
        /// <paramref name="command"/> through? — <see cref="CoveringApproval"/>'s template pass, and the
        /// same test the window's "already lets some of its commands through" notes are built on.</summary>
        private static bool TemplateLetsThrough(string? entry, string command) =>
            TemplateLetsThrough(entry, command, (t, c) => ShotBinding.MatchesTemplate(t, c));

        /// <summary><see cref="TemplateLetsThrough(string?, string)"/> with the match <see cref="Rows"/> makes: one call's
        /// memo of the templates that ran out of time.</summary>
        private static bool TemplateLetsThrough(string? entry, string command, Func<string, string, bool> match) =>
            UsableTemplate(entry) && match(entry!, command) && TargetValuesHoldNoDot(entry!, command);

        /// <summary>
        /// Does the ticked <paramref name="entry"/> let through some command that a tick of the needed
        /// TEMPLATE <paramref name="needed"/> would approve too? Returns one such command — a WITNESS —
        /// or null. BEST-EFFORT, BY EXAMPLE (fifth audit, S1): it tries two witnesses (<see cref="Witnesses"/>:
        /// the needed template and the ENTRY, each with every placeholder bound to <c>a</c>; a literal entry
        /// is its own witness) and returns one that BOTH pass by the gate's own template pass
        /// (<see cref="TemplateLetsThrough"/>). <c>SelectHero K{x}</c> is found for <c>SelectHero {hero}</c>
        /// only through the entry's witness (<c>SelectHero Ka</c>). THE ORDER THEY ARE TRIED IN DOES NOT
        /// MATTER (sixth audit, Q1): when both pass they are the same text
        /// (<c>WhenBothWitnessesPassTheyAreOneTextSoTheirOrderDoesNotMatter</c>). A null is not a proof of no
        /// overlap: one the two witnesses miss is not found. Only the window's notes ask it; it decides no box
        /// and no gate.
        /// </summary>
        private static string? SomeCommandLetThrough(string entry, string needed, Func<string, string, bool> match)
        {
            if (!UsableTemplate(needed)) return null;
            if (!ShotBinding.HasPlaceholder(entry))
                return !NotLiteralEnough(entry) && TemplateLetsThrough(needed, entry, match) ? entry : null;
            foreach (var witness in Witnesses(entry, needed))
                if (TemplateLetsThrough(entry, witness, match) && TemplateLetsThrough(needed, witness, match))
                    return witness;
            return null;
        }

        /// <summary>The two examples <see cref="SomeCommandLetThrough"/> tries: the needed template, then the
        /// entry, each with every placeholder bound to <c>a</c>.</summary>
        internal static IReadOnlyList<string> Witnesses(string entry, string needed) => new[]
        {
            ShotBinding.WithEveryPlaceholderAs(needed, "a"),
            ShotBinding.WithEveryPlaceholderAs(entry, "a"),
        };

        /// <summary>Could the files' template <paramref name="needed"/>, bound to values, BE the ticked
        /// literal <paramref name="literal"/>? One match by the binder's own grammar
        /// (<see cref="ShotBinding.MatchesTemplate"/>, whose value class is the one <c>TryApply</c> checks, both
        /// <c>\A…\z</c> since the sixth audit); a match that runs out of time
        /// (<see cref="ShotBinding.MatchTimeout"/>) counts as no match, and so, for the rest of that <see cref="Rows"/> call,
        /// does every later match against that template (<c>RowsWithFiftyTickedLiteralsAndOneSlowTemplateTakeWellUnderASecond</c>).
        /// A text whose verb is <c>hide-overlay</c> or
        /// <c>camera-spec</c> is never a template (<see cref="NeverATemplate"/>), so it can be nothing but itself
        /// (<c>ATickedLiteralBesideANeededDataFieldLeverIsNotNeeded</c>,
        /// <c>TheBinderNeverWritesAHideOverlayOrCameraSpecCommandFromAPlaceholder</c>).</summary>
        private static bool CanBeBoundTo(string needed, string literal, Func<string, string, bool> match) =>
            ShotBinding.HasPlaceholder(needed) && !NeverATemplate(needed) && match(needed, literal);

        /// <summary>The FIRST lever of <paramref name="needed"/>, in its own order, that
        /// <see cref="IsTicked"/> refuses — or null when every one is covered.</summary>
        public static string? FirstUnticked(IEnumerable<string>? needed, IReadOnlyList<string>? approved)
        {
            foreach (var lever in needed ?? Array.Empty<string>())
                if (!IsTicked(approved, lever))
                    return lever;
            return null;
        }

        /// <summary>
        /// Fourth audit, M5 — no value bound into a TARGET placeholder holds a <c>.</c>. Run only after
        /// <see cref="ShotBinding.MatchesTemplate"/> said yes, on an entry that passed
        /// <see cref="NotLiteralEnough"/>, so every placeholder in its target is a whole segment
        /// (<see cref="TargetPlaceholderIsGlued"/>). A bound value holds no whitespace and the literal
        /// text is the entry's own, so the command's words line up one to one with the entry's, and
        /// the command's TARGET is the entry's target with each placeholder replaced by its value.
        /// Its dot-separated segment count is therefore the entry's plus the dots in those values:
        /// the same count means no value held a dot.
        /// </summary>
        private static bool TargetValuesHoldNoDot(string entry, string command)
        {
            if (!TargetingVerbs.Contains(FirstWordOf(entry))) return true;
            var target = SecondWordOf(entry);
            if (!ShotBinding.HasPlaceholder(target)) return true;
            return SecondWordOf(command).Split('.').Length == target.Split('.').Length;
        }

        /// <summary>A text whose verb is <c>hide-overlay</c> or <c>camera-spec</c>, wherever it came from: its words are
        /// C# type and method names. Exact text only, never a template — and the binder writes no command from one that
        /// holds a placeholder (<see cref="ShotBinding.TryApply"/>, seventh audit;
        /// <c>TheBinderNeverWritesAHideOverlayOrCameraSpecCommandFromAPlaceholder</c>).</summary>
        internal static bool NeverATemplate(string? lever) =>
            lever != null
            && (lever.StartsWith(HideOverlayPrefix, StringComparison.Ordinal)
                || lever.StartsWith(CameraSpecPrefix + " ", StringComparison.Ordinal));

        /// <summary>
        /// Does <paramref name="command"/> pass the gate against <paramref name="approved"/>? True
        /// exactly when an entry covers it (<see cref="CoveringApproval"/>) — there is no second
        /// rule here to drift from the one the window shows.
        /// </summary>
        public static bool Allows(IEnumerable<string>? approved, string? command) =>
            CoveringApproval(approved, command) != null;

        /// <summary>
        /// IS THIS LEVER, AS AUTHORED, COVERED BY WHAT IS TICKED? The question slice E's
        /// <c>probe</c> asks of every lever a shot needs BEFORE it runs (so a probe that would be
        /// refused half-way is refused whole). Built on <see cref="CoveringApproval"/>, so the probe,
        /// the gate and the window share one covering rule: false for a null or empty lever; true
        /// for one of the kit's three fixed verbs (<see cref="IsReadOnly"/> — not a `get`, since the fourteenth audit),
        /// which needs no tick — the gate and the window's row say the same of it; false when it is not literal enough
        /// (<see cref="NotLiteralEnough"/>); otherwise whether an entry covers it — a template is
        /// covered by itself ticked verbatim, or by a ticked template of the same shape (placeholder
        /// names aside, the same placeholders sharing a name), never by a literal or by a broader template.
        /// </summary>
        public static bool IsTicked(IReadOnlyList<string>? approved, string? lever)
        {
            if (string.IsNullOrEmpty(lever)) return false;
            if (IsReadOnly(lever)) return true;
            if (NotLiteralEnough(lever)) return false;
            return CoveringApproval(approved, lever) != null;
        }

        /// <summary>
        /// Is this command's FIRST whitespace-delimited word NOT literal — does it carry a
        /// <c>{</c>, a <c>"</c> or a <c>'</c>? (The name is kept from when a placeholder was the
        /// only case; slice E calls it.) Leading whitespace is skipped first: <c> {a} {b}</c> is
        /// the same entry as <c>{a} {b}</c> with a space in front of it, and reading only the first
        /// CHARACTER let that one through.
        ///
        /// A QUOTE COUNTS (third audit, M2): the bridge splits a command WITH quotes
        /// (<c>ReflectionCheatBridge.SplitCommand</c>), so the verb of <c>" DeleteSave" now</c> is
        /// <c> DeleteSave</c> — and <c>" {a}" {b}</c>, whose first word looks literal to a
        /// whitespace split, is a placeholder verb. A quote in a later ARGUMENT
        /// (<c>call Dialog.Say "Some Arg"</c>) is ordinary; a quote in a targeting verb's TARGET is
        /// not (<see cref="TargetHasPlaceholder"/>), and neither is one anywhere in a camera-pose
        /// (<see cref="CameraPoseTypeNotLiteral"/>).
        /// </summary>
        public static bool FirstWordHasPlaceholder(string? command) =>
            FirstWordOf(command).IndexOfAny(NotLiteralInAFirstWord) >= 0;

        /// <summary>
        /// The bridge's verbs whose FIRST ARGUMENT names what they act on: the field `set` writes, the
        /// method `call` runs, the game cheat `raw` forwards, the object `click` / `invoke-button` /
        /// `hide-ui` presses or hides. For these a template whose TARGET begins with a placeholder —
        /// `raw {a} {b}`, `set {a} {b}`, `call {t}.Run` — has a literal first word, and ticked once it
        /// would approve any game cheat, any field, any method. The first-word rule could not see it
        /// (reported after the third audit). A placeholder in a target must be a WHOLE dot-separated
        /// segment (`set Player.{stat} 5`, never `call S{a}` or `set Player.x{a} 5` — fourth audit, M5,
        /// <see cref="TargetPlaceholderIsGlued"/>), and the value bound into it holds no dot
        /// (<see cref="TargetValuesHoldNoDot"/>): what it names begins with something the person read,
        /// and names one member of it. `camera-pose` names what it acts on too, in an optional first
        /// argument — its Type slot has its own rule (<see cref="CameraPoseTypeNotLiteral"/>).
        /// </summary>
        private static readonly HashSet<string> TargetingVerbs = new(StringComparer.OrdinalIgnoreCase)
            { "set", "call", "raw", "click", "invoke-button", "hide-ui" };

        public static bool TargetHasPlaceholder(string? command)
        {
            if (!TargetingVerbs.Contains(FirstWordOf(command))) return false;
            var target = SecondWordOf(command);
            return target.Length > 0 && Array.IndexOf(NotLiteralInAFirstWord, target[0]) >= 0;
        }

        /// <summary>
        /// Fourth audit, M5 — is a placeholder in a targeting verb's TARGET glued to other text, so
        /// that it is not a whole dot-separated segment? <c>call S{a}</c> would approve every type
        /// whose name starts with S, and <c>set Player.x{s} 1</c> every member starting with x. The
        /// target rule used to read the first CHARACTER only — the first-word rule's old mistake.
        /// </summary>
        public static bool TargetPlaceholderIsGlued(string? command)
        {
            if (!TargetingVerbs.Contains(FirstWordOf(command))) return false;
            foreach (var segment in SecondWordOf(command).Split('.'))
                if (ShotBinding.HasPlaceholder(segment) && !ShotBinding.IsOnePlaceholder(segment))
                    return true;
            return false;
        }

        /// <summary>The kit verb that poses the camera, <c>camera-pose [Type] x y z pitch yaw roll fov</c>.</summary>
        private const string CameraPoseVerb = "camera-pose";

        /// <summary>
        /// Does a <c>camera-pose</c> hold a QUOTE anywhere, or name its TYPE with a placeholder? With
        /// eight arguments the first is the Type the pose looks up and invokes the camera-spec's methods
        /// on, so <c>camera-pose {t} 1 2 3 4 5 6 60</c>, ticked once, approved posing any Component
        /// (fourth audit, M2). A quote ANYWHERE is refused (fifth audit, M1): the bridge splits a command
        /// at its quotes (<c>ReflectionCheatBridge.SplitCommand</c>), so <c>camera-pose {t} 1 2 3 4 5 "6"60</c>
        /// has nine words but eight arguments, and its Type slot is the placeholder. Neither a type nor a
        /// number needs a quote. Without a quote, words are arguments: the count is read with any
        /// whitespace, padded or doubled. Seven arguments have no Type slot:
        /// <c>camera-pose {x} {y} {z} 0 0 0 60</c> is an ordinary template.
        /// </summary>
        public static bool CameraPoseTypeNotLiteral(string? command)
        {
            if (!string.Equals(FirstWordOf(command), CameraPoseVerb, StringComparison.OrdinalIgnoreCase)) return false;
            if (command!.IndexOf('"') >= 0 || command.IndexOf('\'') >= 0) return true;
            return SecondWordOf(command).IndexOf('{') >= 0 && WordCount(command) == 9;
        }

        /// <summary>
        /// The verb, and for a targeting verb its target, are written out — or this entry approves
        /// commands nobody was shown. ONE predicate for the gate (<see cref="CoveringApproval"/> skips
        /// an entry it refuses), the window (<see cref="NotTickableReason"/> gives each part its
        /// sentence) and a try (<see cref="IsTicked"/>); the shared fixture
        /// <c>Editor/Tests/Fixtures/lever-shapes.cases.json</c> holds the two to the same verdicts.
        /// Refused: a first word that is not literal; a target that begins with a placeholder or a
        /// quote, or holds a placeholder that is not a whole segment; two placeholders with nothing
        /// between them that a value cannot hold, anywhere (fourth and fifth audits, M3); a camera-pose
        /// holding a quote, or a placeholder in its Type (fourth audit M2, fifth audit M1); in a command with a
        /// placeholder, a brace that is not part of one (sixth audit, M5) — a lever read out of a data field aside,
        /// which has its own brace sentence; a character the site refuses to send (<see cref="UnsendableCharacter"/>, ninth
        /// audit, M4), so one ticked by hand approves nothing, not even its own text.
        /// </summary>
        public static bool NotLiteralEnough(string? command) =>
            FirstWordHasPlaceholder(command) || TargetHasPlaceholder(command) || TargetPlaceholderIsGlued(command)
            || ShotBinding.HasAdjacentPlaceholders(command) || CameraPoseTypeNotLiteral(command)
            || TemplateHasBraceOutsideAPlaceholder(command) || UnsendableCharacter(command) != null;

        /// <summary>
        /// Ninth audit, M4 — the first character in <paramref name="text"/> that the hosted lint refuses to send
        /// (<c>UNSENDABLE</c> in <c>apps/renderer/src/game-ads/shots/shots-lint.ts</c>, the same class written the same
        /// way), as <c>U+XXXX</c>, or null. A control character (a line break among them), U+180E, U+FEFF, a zero-width
        /// mark, a line or paragraph separator, a direction embedding, override or isolate, or half of a surrogate pair:
        /// the window draws a lever as one line, and the bridge splits a command at a line break, so the row a person
        /// ticks is not the command that runs. ONE set for the kit: <see cref="NotTickableReason"/> and
        /// <see cref="NotLiteralEnough"/> read it, and <see cref="SyncNova"/> refuses a delivery whose lever holds one — or
        /// whose ready block does, <c>muteGet</c> included (the eleventh audit, M3).
        /// </summary>
        public static string? UnsendableCharacter(string? text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var m = Unsendable.Match(text);
            return m.Success ? $"U+{(int)m.Value[0]:X4}" : null;
        }

        private static readonly System.Text.RegularExpressions.Regex Unsendable = new(
            @"[\u0000-\u001F\u007F-\u009F\u180E\uFEFF\u200B-\u200F\u2028\u2029\u202A-\u202E\u2066-\u2069]" +
            @"|[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>
        /// Ninth audit, M1 — THE FIRST PLACEHOLDER NOTHING CAN BIND in a delivery, as the sentence <see cref="SyncNova"/>
        /// refuses it with, or null. The binder fills a shot's <c>{name}</c> only from that shot's own <c>parameters</c>,
        /// and a shot with none is not bound at all (<c>AdDirector.TryBind</c>), so its <c>SelectHero {knight}</c> reached
        /// the gate as text, where a tick of <c>SelectHero {k}</c> covered it by shape. The same raw traversal as
        /// <see cref="NeededFromShotsJson"/> (setup entries, <c>cheat</c> / <c>cheatUntil</c> commands); a click or hold
        /// name is not a lever. adapter.json's levers are never bound — the ready block is sent as written
        /// (<c>HygieneReadyGate</c>), and an overlay type or camera name is never a template — so a placeholder in any of
        /// them is refused too. The hosted lint refuses the same shapes (<c>deliveries.cases.json</c>).
        /// </summary>
        public static string? UnboundPlaceholder(string? shotsText, string? adapterText)
        {
            JObject? shots = null, adapter = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(shotsText)) shots = NovaJson.ParseObject(shotsText!);
            }
            catch (Exception)
            {
                // Not JSON: nothing in it can be bound or run; the loader says why. (A delivery never gets here: SyncNova
                // refuses what it cannot parse, and asks the overload below — tenth audit, S1.)
            }
            try
            {
                if (!string.IsNullOrWhiteSpace(adapterText)) adapter = NovaJson.ParseObject(adapterText!);
            }
            catch (Exception)
            {
            }
            return UnboundPlaceholderInParsed(shots, adapter);
        }

        /// <summary>The same question of two documents already parsed (tenth audit, S1: <see cref="SyncNova"/>'s delivery
        /// check), so it never meets a parse failure to answer "nothing" for.</summary>
        internal static string? UnboundPlaceholderInParsed(JObject? shotsRoot, JObject? adapterRoot)
        {
            if (shotsRoot?["shots"] is JArray shots)
                for (var i = 0; i < shots.Count; i++)
                {
                    if (shots[i] is not JObject shot) continue;
                    var name = shot["name"]?.Type == JTokenType.String ? "'" + shot["name"]!.Value<string>() + "'" : $"shots[{i}]";
                    var declared = shot["parameters"] is JArray ps
                        ? ps.Where(p => p.Type == JTokenType.String).Select(p => p.Value<string>()!).ToList()
                        : new List<string>();
                    var texts = new List<string>();
                    if (shot["setup"] is JArray setup)
                        texts.AddRange(setup.Where(t => t.Type == JTokenType.String).Select(t => t.Value<string>()!));
                    if (shot["steps"] is JArray steps)
                        foreach (var step in steps.OfType<JObject>())
                            if (step["kind"]?.Type == JTokenType.String
                                && (step["kind"]!.Value<string>() is "cheat" or "cheatUntil")
                                && step["command"]?.Type == JTokenType.String)
                                texts.Add(step["command"]!.Value<string>()!);
                    foreach (var text in texts)
                        foreach (var placeholder in ShotBinding.PlaceholderNames(text))
                            if (!declared.Contains(placeholder))
                                return $"shot {name} declares no parameter '{placeholder}', so its '{text}' would reach the " +
                                       "game as text — nothing was written";
                }
            foreach (var lever in NeededFromAdapter(adapterRoot))
                if (ShotBinding.HasPlaceholder(lever))
                    return $"adapter.json binds no placeholder, so its '{lever}' would reach the game as text — nothing was written";
            return null;
        }

        /// <summary>
        /// Sixth audit, M5 — a template holding a <c>{</c> or <c>}</c> outside its placeholders
        /// (<see cref="ShotBinding.HasBraceOutsideAPlaceholder"/>). The sixth audit's probe Q5, at 77d481d7: a
        /// ticked <c>SelectHero {{h}}</c> ran h=Knight's binding and refused h=knight's, because
        /// <c>SelectHero {knight}</c> reads as a template and the gate compares a command that holds a placeholder
        /// by its shape (<see cref="CoveringApproval"/>) — while the row showed one tick and no note. A lever read out of a data field is left to its own brace sentence in
        /// <see cref="NotTickableReason"/>: it is never a template, and approves only its exact text.
        /// </summary>
        private static bool TemplateHasBraceOutsideAPlaceholder(string? command) =>
            !NeverATemplate(command) && ShotBinding.HasBraceOutsideAPlaceholder(command);

        private static int WordCount(string? command)
        {
            if (string.IsNullOrEmpty(command)) return 0;
            var count = 0;
            var inWord = false;
            foreach (var c in command!)
            {
                var white = char.IsWhiteSpace(c);
                if (!white && !inWord) count++;
                inWord = !white;
            }
            return count;
        }

        private static string SecondWordOf(string? command)
        {
            if (string.IsNullOrEmpty(command)) return "";
            var i = 0;
            while (i < command!.Length && char.IsWhiteSpace(command[i])) i++;
            while (i < command.Length && !char.IsWhiteSpace(command[i])) i++;
            while (i < command.Length && char.IsWhiteSpace(command[i])) i++;
            var start = i;
            while (i < command.Length && !char.IsWhiteSpace(command[i])) i++;
            return command.Substring(start, i - start);
        }

        private static readonly char[] NotLiteralInAFirstWord = { '{', '"', '\'' };

        private static string FirstWordOf(string? command)
        {
            if (string.IsNullOrEmpty(command)) return "";
            var i = 0;
            while (i < command!.Length && char.IsWhiteSpace(command[i])) i++;
            var start = i;
            while (i < command.Length && !char.IsWhiteSpace(command[i])) i++;
            return command.Substring(start, i - start);
        }

        /// <summary>
        /// Why this lever cannot be ticked, in words a person can act on — or null when it can.
        /// Shown beside the row rather than hidden, because a lever the files ask for and the window
        /// refuses to show at all is a gate that cannot be passed and cannot be understood. The two
        /// first-word sentences end the same way the website's do ("…first word must be literal").
        /// </summary>
        public static string? NotTickableReason(string? command)
        {
            if (UnsendableCharacter(command) is { } code)
                return $"this command holds {code}, a line break, control or invisible formatting character, so the row you " +
                       "would tick is not the command that would run, and it cannot be approved here.";
            var first = FirstWordOf(command);
            if (first.IndexOf('{') >= 0)
                return "this command's first word is a {placeholder}, so ticking it would approve any " +
                       "command of that shape — including ones nothing has shown you. It cannot be " +
                       "approved here: a command's first word must be literal.";
            if (first.IndexOf('"') >= 0 || first.IndexOf('\'') >= 0)
                return "this command's first word holds a quote, and the game reads a quoted first word " +
                       "as whatever the quotes enclose — not what this row shows. It cannot be approved " +
                       "here: a command's first word must be literal.";
            if (TargetHasPlaceholder(command))
                return "this command's TARGET (what it sets, calls, forwards or presses) is a {placeholder} " +
                       "or quoted, so ticking it would approve that verb on anything. It cannot be approved " +
                       "here: a command's verb and its target must be literal.";
            if (TemplateHasBraceOutsideAPlaceholder(command))
                return "this command holds a '{' or '}' that is not part of a {placeholder} (SelectHero {{h}}, " +
                       "Player.{a}}): a value bound inside such braces makes a command that reads as a {placeholder} " +
                       "itself (h=knight gives SelectHero {knight}). It cannot be approved here: in a command with a " +
                       "{placeholder}, write '{' and '}' only as parts of placeholders.";
            if (TargetPlaceholderIsGlued(command))
                return "a {placeholder} in this command's TARGET is glued to other letters (S{a}, Player.x{a}), " +
                       "so ticking it would approve every name that starts or ends that way. It cannot be " +
                       "approved here: a placeholder in a target must be a whole name between dots (Player.{stat}).";
            if (ShotBinding.HasAdjacentPlaceholders(command))
                return "this command has two {placeholders} with nothing between them that a value cannot hold " +
                       "({a}{b}, {a}.{b}, {a}_{b}, {a}-{b}) — a value may hold letters, digits, '.', '_' and '-' — so a " +
                       "command that fits it cannot be read back into one value per placeholder: nothing says where " +
                       "one ends and the next begins. It cannot be approved here: separate them with a space or " +
                       "another character a value cannot hold ({a} {b}, {a}:{b}).";
            if (CameraPoseTypeNotLiteral(command))
                return "this camera-pose holds a quote, or names its Type with a {placeholder}. The game splits a " +
                       "command at its quotes, so a quote anywhere can move which argument is the Type, and ticking " +
                       "it would approve posing any Component with the camera-spec's methods. It cannot be approved " +
                       "here: write the Type out, with no quotes, or leave it off to pose adapter.json's view type.";
            // Third audit, S1: an overlay type or camera method name never holds a brace, so a lever
            // that does names nothing real — and it is never a template (CoveringApproval).
            if (NeverATemplate(command) && (command!.IndexOf('{') >= 0 || command.IndexOf('}') >= 0))
                return "no C# type or method name holds '{' or '}', so this lever names nothing that can " +
                       "exist. It cannot be approved here; ask for adapter.json to name the real one.";
            return null;
        }

        /// <summary>
        /// IS THE GATE LIVE FOR *THIS RUN*? The one question every enforcement point of a director
        /// run asks — the <see cref="LeverGateBridge"/> (and, at the bridge, the camera-spec lever
        /// a <c>camera-pose</c> needs), a shot's <c>timeScale</c> step and overlay hiding — so a run
        /// cannot be gated at one of them and open at another. (<see cref="CameraPose.Pose"/>'s own
        /// camera-spec check asks <see cref="LeverAllowed"/> without the flag; for a director run
        /// the bridge has already asked WITH it — audit M3.)
        ///
        /// <paramref name="cloudContent"/> IS SLICE E, AND IT IS WHY THIS FUNCTION EXISTS. A
        /// <c>probe</c> runs a shot the website sent and WRITES NO FILE — so nothing about it
        /// changes the sha of anything under <c>Library/Nova/</c>, and <see cref="GateActive"/>,
        /// which is entirely derived from those shas, answers FALSE on a project whose own files are
        /// on disk. The cloud's script would then run every un-ticked cheat it carries: the
        /// fail-open shape both A2′ audits kept finding, one layer further along. So a probe forces
        /// the gate ON, unconditionally, for the content it is running — the state of the studio's
        /// files says nothing about where THIS script came from, and the answer must not depend on
        /// it.
        /// </summary>
        public static bool GateActiveFor(string projectRoot, bool cloudContent) =>
            cloudContent || GateActive(projectRoot);

        /// <summary>
        /// IS THE GATE LIVE IN THIS PROJECT? Whenever EITHER file on disk is one the cloud has
        /// delivered here: <c>sha256(Library/Nova/shots.json)</c> is in <c>cloudShots</c>, or
        /// <c>sha256(Library/Nova/adapter.json)</c> is in <c>cloudAdapters</c>.
        ///
        /// EITHER, not the shots file alone (audit S3): one byte appended to shots.json used to
        /// turn the gate off while adapter.json was still byte-for-byte the cloud's — and the cloud
        /// adapter's `ready` block then wrote into the game with nothing ticked. The two files are
        /// one delivery, and they are gated as one.
        ///
        /// THE LISTS, not the last pair: a send that half-failed leaves a file this kit wrote on
        /// disk without it being the CURRENT pair, and that file must stay gated (audit S2).
        ///
        /// No synced.json at all, or neither file one the cloud sent, is LOCAL AUTHORSHIP — the
        /// person who wrote those shots is the person F16 trusts. Every studio that used this kit
        /// before this release stays exactly as it was; taking a delivered project back means
        /// editing (or deleting) BOTH files on your own machine.
        /// </summary>
        public static bool GateActive(string projectRoot)
        {
            System.Threading.Interlocked.Increment(ref _gateActiveCalls);
            var synced = SyncNova.ReadSynced(projectRoot);
            if (!synced.Exists) return false;
            // A synced.json this kit cannot read is a project the cloud HAS written to, with the
            // one record of what it wrote now unreadable. Fail CLOSED.
            if (!synced.Parsed) return true;

            var shots = SyncNova.Sha256OfFile(RelayPaths.NovaShotsFile(projectRoot));
            if (Delivered(synced.CloudShots, shots)) return true;
            var adapter = SyncNova.Sha256OfFile(SyncNova.AdapterFile(projectRoot));
            return Delivered(synced.CloudAdapters, adapter);
        }

        private static bool Delivered(IReadOnlyList<string> everDelivered, string? sha) =>
            sha != null && everDelivered.Any(s => string.Equals(s, sha, StringComparison.OrdinalIgnoreCase));

        /// <summary>Where a row in the Nova Capture window came from — the sentence beside it.</summary>
        public enum LeverSource
        {
            /// <summary>One of the two JSON files on disk asks for it.</summary>
            Needed,
            /// <summary>Ticked here once; nothing on disk asks for it any more.</summary>
            Approved,
            /// <summary>The gate refused it in this editor session, and no file lists it.</summary>
            RefusedHere,
        }

        /// <summary>One line of "Levers the cloud may use": a command, its tick, and whether the
        /// tick can be changed.</summary>
        public sealed class LeverRow
        {
            public string Command { get; set; } = "";
            /// <summary>The checkbox. True when an entry of levers.json covers this command
            /// (<see cref="CoveringApproval"/> — the gate's own rule), for one of the kit's three fixed verbs (it needs no
            /// tick; a `get` is not one since the fourteenth audit),
            /// and for an entry that is in the file as this exact text even when the gate ignores it
            /// (not literal enough — shown ticked, with the reason, so it can be taken back).</summary>
            public bool Approved { get; set; }
            /// <summary>The entry of levers.json that covers this command — the command itself, a
            /// ticked TEMPLATE it is a binding of, or null when nothing covers it.</summary>
            public string? CoveredBy { get; set; }
            /// <summary>False = the checkbox is disabled: a command nothing may approve (and nothing
            /// in the file approves), a fixed verb that is not in the file (there is no tick to give),
            /// one covered by a DIFFERENT entry — this row is not in levers.json, so unticking it
            /// could revoke nothing; the covering entry's own row can — or a command the gate refused
            /// that holds a placeholder (a tick of it would approve a template nobody authored).</summary>
            public bool CanTick { get; set; } = true;
            /// <summary>Empty for an ordinary row; otherwise why this row is here, or why its box
            /// is disabled, in plain words.</summary>
            public string Note { get; set; } = "";
            public LeverSource Source { get; set; }
        }

        /// <summary>The note beside a ticked LITERAL that no file on disk lists and that no template the files
        /// ask for can be bound to (<see cref="CanBeBoundTo"/>, by the binder's grammar). That includes a command a
        /// compiled adapter runs from code: the FILES do not need it
        /// (<c>TheWindowListsACommandTheGateRefusedThatNoFileNames</c>). A ticked template never gets it
        /// (<see cref="TemplateNote"/>).</summary>
        public const string NotNeededNote =
            "not needed by the files on disk — it stays approved until you untick it";

        /// <summary>The note beside one of the kit's three fixed verbs (<see cref="IsReadOnly"/>): the gate runs it
        /// without a tick, so the row is shown approved and there is no tick to give (fourth audit,
        /// M1 — it used to be an empty box the gate ran anyway). A `get` row is an ordinary lever row since the fourteenth
        /// audit.</summary>
        public const string ReadNeedsNoTickNote = "a read — needs no tick";

        /// <summary>The note beside an entry of levers.json that covers commands the files ask for
        /// but is not itself one of them — a ticked template whose shape a needed template or a
        /// needed command has (fourth audit, S1: it used to say "not needed by the files on disk").</summary>
        public static string CoversNeededNote(IReadOnlyList<string> covered) =>
            "approves " + string.Join(", ", covered.Take(3).Select(c => $"`{c}`")) +
            (covered.Count > 3 ? $" and {covered.Count - 3} more" : "") + ", which the files ask for";

        /// <summary>The note beside a ticked LITERAL that is not in the files as such but that a template
        /// the files ask for can be bound to — <c>SelectHero Knight</c> of <c>SelectHero {hero}</c>
        /// (<see cref="CanBeBoundTo"/>). Since the fifth audit a ticked TEMPLATE never gets it: a template gets
        /// <see cref="TemplateNote"/>. A literal approves its own text and nothing else (<see cref="CoveringApproval"/>
        /// reads an entry with no placeholder by exact text only), which is what the note says since the sixth
        /// audit (M4; <c>TheTemplateNoteAndTheLiteralNoteSayWhatTheGateApproves</c>).</summary>
        public const string BroaderTemplateNote =
            "not asked for in this form by the files; it approves only this command, as written";

        /// <summary>The note beside a ticked TEMPLATE that covers nothing the files ask for (fifth audit,
        /// S1 + M2). Whether the files could still need it cannot be known without intersecting two templates,
        /// so a template is not called "not needed" (<see cref="NotNeededNote"/> is for a literal). What it approves
        /// is said as the gate decides it (sixth audit, M4): a command that fits it (<see cref="ShotBinding.MatchesTemplate"/>)
        /// is refused when a value in its target holds a '.' (<see cref="TargetValuesHoldNoDot"/>;
        /// <c>TheTemplateNoteAndTheLiteralNoteSayWhatTheGateApproves</c>).</summary>
        public const string TemplateNote =
            "a template — approves any command that fits it, except one whose value in a target holds a '.'; " +
            "not asked for in this exact form";

        /// <summary>The note beside a needed template that is not ticked in its own shape while a witness
        /// shows another ticked entry letting some of its commands through (<see cref="SomeCommandLetThrough"/>)
        /// — the entry named, with the witness. It says that it was found by example.</summary>
        public static string PartlyCoveredNote(string entry, string example) =>
            $"not ticked in this form — but the ticked entry `{entry}` already lets some, possibly all, of its " +
            $"commands through (`{example}`, for one). Found by trying examples, not proved: an overlap they miss is not named";

        /// <summary>
        /// The note beside a command the gate refused that no file on disk names. ONLY WHAT THE
        /// GATE KNOWS (third audit, M1): it used to say "asked for by your own adapter code", and
        /// the gate cannot know that — a command a cloud shot sent, refused, and then dropped from
        /// the next send read as the studio's own code.
        /// </summary>
        public const string RefusedThisSessionNote =
            "refused in this editor session; no file on disk asks for it now";

        /// <summary>The note beside a refused command that FITS a template the files ask for, but
        /// that a tick of that template would still refuse (a value in the target may hold no '.').
        /// Folding it into the template's row would say "ticking this approves that" — false.</summary>
        public static string RefusedBeyondTemplateNote(string template) =>
            "refused in this editor session — it fits `" + template +
            "`, but ticking that would still refuse it: a value in a command's target may not hold a '.'";

        /// <summary>The note beside a row a DIFFERENT entry covers — a ticked template. Its own
        /// checkbox is disabled: it is not in levers.json, so unticking it could revoke nothing.</summary>
        public static string CoveredByTemplateNote(string template) =>
            $"approved by the ticked template `{template}` — untick that to revoke";

        /// <summary>The note beside a template the files ask for, when the gate refused bindings of
        /// it in this session (third audit, M1: those are folded into this row, not listed again).</summary>
        public static string RefusedAsNote(IReadOnlyList<string> bound) =>
            "refused in this editor session as " +
            string.Join(", ", bound.Take(3).Select(b => $"`{b}`")) +
            (bound.Count > 3 ? $" and {bound.Count - 3} more" : "") +
            " — ticking this approves that";

        /// <summary>
        /// EVERY ROW THE NOVA CAPTURE WINDOW SHOWS, as data — pure, so the decision is covered by
        /// EditMode tests and not only by looking at an IMGUI panel (invariant 101 as far as it can
        /// be taken here: the drawing itself is still untested).
        ///
        /// It is three lists, and the second audit added the last two (M8):
        /// <list type="number">
        /// <item>what the two JSON files on disk ask for (<see cref="Needed"/>);</item>
        /// <item>EVERY OTHER ENTRY OF levers.json. An approval whose command has since left the
        /// files used to disappear from this window — while still being honoured by the gate for
        /// ever, with no way to revoke it from here;</item>
        /// <item>commands the gate REFUSED in this editor session that nothing lists
        /// (<see cref="LeverGateBridge.RefusedThisSession"/>). A studio with its own COMPILED
        /// adapter has its ready gate's and recovery's commands gated the moment one cloud file
        /// lands, and no JSON file names them — so before this they were refused with nothing to
        /// tick, which is a gate that cannot be passed.</item>
        /// </list>
        ///
        /// THE CHECKBOX ANSWERS FOR THE ROW'S OWN SHAPE (third audit S1, fourth audit S1 and M1): a
        /// row is ticked when an entry covers it by <see cref="CoveringApproval"/>, the rule the gate
        /// itself runs — its exact text, a ticked template it is a binding of, or, for a row that is
        /// itself a template, a ticked template of the same shape (placeholder names aside, the same
        /// placeholders sharing a name). A row
        /// covered by a DIFFERENT entry says which one, and its own box is disabled; the covering
        /// entry's row names the rows it covers. One of the kit's three fixed verbs (<see cref="IsReadOnly"/>) is shown
        /// ticked — the gate runs it without one — and says it needs none; a `get` is an ordinary row (the fourteenth audit).
        ///
        /// WHERE THE BOX AND THE GATE STILL DIFFER. The rows say what a witness finds, not every case (sixth
        /// audit, M3 — this used to read "two places, each said on the row", and the fifth fold's own
        /// <c>AnOverlapNeitherWitnessHitsIsNotNamed</c> is a difference no row states). (1) A hand-edited entry
        /// the gate IGNORES (not literal enough) is shown ticked, with the reason, so it can be taken
        /// out of the file — the direction that runs nothing. (2) A ticked entry that PARTLY covers a
        /// needed template — a broader template (ticked <c>set Player.{a} {b}</c>, the files asking
        /// for <c>set Player.coins {v}</c>), a narrower or overlapping one (<c>SelectHero K{x}</c>
        /// against <c>SelectHero {hero}</c>), or one of its bindings ticked as a literal
        /// (<c>SelectHero Knight</c>) — is not that shape, so the template's box stays empty although
        /// the gate lets some, possibly all, of its commands through. Ticking this row approves every
        /// command of this form (except one whose value in a target holds a '.'), whatever other entries let
        /// through. What the rows say about it: a needed row names the FIRST ticked entry, in levers.json's
        /// order, for which a witness (<see cref="SomeCommandLetThrough"/>) passes both that entry and a tick
        /// of the needed row — so a row that cannot be ticked names none — appended to a refused-as note
        /// (<c>ThePartlyCoveredNoteNamesOnlyTheFirstSuchEntryInFileOrder</c>,
        /// <c>ThePartlyCoveredNoteNeedsAWitnessTheNeededTickWouldApproveToo</c>,
        /// <c>ARowThatCannotBeTickedNamesNoEntryEvenWhileOneRunsItsCommands</c>); the witness is not a proof,
        /// and an overlap it misses is not named. A ticked template's row does not claim the files don't need
        /// it: one that covers nothing the files ask for says <see cref="TemplateNote"/>. A ticked literal a
        /// template the files ask for can be bound to says <see cref="BroaderTemplateNote"/>; a literal none can
        /// be bound to (<see cref="CanBeBoundTo"/>) says <see cref="NotNeededNote"/>.
        ///
        /// A REFUSED BINDING OF A TEMPLATE THE FILES ASK FOR is that template's row, not a row of
        /// its own (third audit, M1): <c>SelectHero Knight</c>, refused while
        /// <c>SelectHero {hero}</c> is listed, is named in the template's note.
        /// </summary>
        public static IReadOnlyList<LeverRow> Rows(string projectRoot, IEnumerable<string>? refusedHere)
        {
            // NINTH AUDIT, S1 — COMPUTED AGAIN ONLY WHEN AN INPUT CHANGES. The window reads this once a second while it is
            // open, and its four inputs are three small files and the refused list: while those are the same, so is every
            // row. The key is read BEFORE the files are parsed, so a file that changes in between leaves a key that no
            // longer matches it, and the next read computes again. The list is handed back as it is: the window changes a
            // row's box only after SetApproved has written levers.json, which changes that file's sha
            // (RowsIsComputedAgainOnlyWhenAnInputChanges).
            var refused = (refusedHere ?? Array.Empty<string>()).ToArray();
            var key = RowsInputKey(projectRoot, refused);
            lock (RowsCacheLock)
                if (_rowsCache != null && string.Equals(_rowsCacheKey, key, StringComparison.Ordinal))
                    return _rowsCache;
            var rows = ComputeRows(projectRoot, refused);
            lock (RowsCacheLock)
            {
                _rowsCacheKey = key;
                _rowsCache = rows;
            }
            return rows;
        }

        private static readonly object RowsCacheLock = new();
        private static string? _rowsCacheKey;
        private static IReadOnlyList<LeverRow>? _rowsCache;

        /// <summary>For the tests: how many times <see cref="GateActive"/> has run in this editor session — each run reads
        /// synced.json and hashes the delivered files (the thirteenth audit, S1: the director asks it once per shot attempt
        /// now, not once per write). Counting only; nothing reads it but the tests.</summary>
        internal static int GateActiveCallsForTests => System.Threading.Volatile.Read(ref _gateActiveCalls);
        private static int _gateActiveCalls;

        /// <summary>For the tests: forget the last <see cref="Rows"/> result.</summary>
        internal static void ForgetRowsForTests()
        {
            lock (RowsCacheLock)
            {
                _rowsCacheKey = null;
                _rowsCache = null;
            }
        }

        /// <summary>For the tests: compute <see cref="Rows"/> with the plain functions — no prefilter, nothing kept per
        /// call but the time-limit memo, as before the ninth audit. <c>RowsAreByteForByteTheSameWithTheMemoOff</c> holds
        /// the two to one answer. Part of the input key, so switching it computes again.</summary>
        internal static bool RowsMemoOffForTests { get; set; }

        /// <summary>The four inputs of <see cref="Rows"/> as one string: the project, the sha of shots.json, adapter.json
        /// and levers.json, and each refused command — every part written with its length in front (a missing or
        /// unreadable file as '-'), so no two sets of inputs make the same key.</summary>
        private static string RowsInputKey(string projectRoot, IReadOnlyList<string> refused)
        {
            var key = new StringBuilder();
            void Part(string? text)
            {
                if (text == null) key.Append('-');
                else key.Append(text.Length).Append(':').Append(text);
            }
            Part(projectRoot);
            Part(SyncNova.Sha256OfFile(RelayPaths.NovaShotsFile(projectRoot)));
            Part(SyncNova.Sha256OfFile(SyncNova.AdapterFile(projectRoot)));
            Part(SyncNova.Sha256OfFile(FilePath(projectRoot)));
            key.Append(RowsMemoOffForTests ? 'S' : 'F');
            foreach (var command in refused) Part(command);
            return key.ToString();
        }

        private static IReadOnlyList<LeverRow> ComputeRows(string projectRoot, IReadOnlyList<string> refusedHere)
        {
            var needed = Needed(projectRoot);
            var approved = Approved(projectRoot);
            var rows = new List<LeverRow>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            // Each text's checks, each template's match, each witness and each command's covering entry, once per call
            // (ninth audit, S1; RowsMemo). A template whose match ran out of time (ShotBinding.MatchTimeout) is read as no
            // match for the rest of THIS call, for the notes only, so one slow template costs one time limit per call, not
            // one per ticked literal. Nothing is kept across calls but the result itself
            // (RowsWithFiftyTickedLiteralsAndOneSlowTemplateTakeWellUnderASecond).
            var memo = new RowsMemo(approved, fast: !RowsMemoOffForTests);

            // Templates a person could tick from this window: a placeholder in a LATER word, a
            // first word that is literal, and not a lever read out of a data field.
            var templates = needed.Where(n => ShotBinding.HasPlaceholder(n) && memo.NotTickableReason(n) == null
                                              && !NeverATemplate(n)).ToList();
            var refused = new List<string>();
            var foldedInto = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            // A refused command that FITS a needed template but that the template's tick would still
            // refuse (a dotted value in a target placeholder, fourth audit M5) is NOT folded: its
            // row would say "ticking this approves that", which is false. It is listed on its own.
            var beyond = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var command in refusedHere)
            {
                if (string.IsNullOrWhiteSpace(command) || refused.Contains(command)) continue;
                if (needed.Contains(command, StringComparer.Ordinal)) continue; // its own Needed row
                // the SAME test a tick is judged by — `CoveringApproval`'s template pass
                var template = templates.FirstOrDefault(t => memo.LetsThrough(t, command));
                if (template != null)
                {
                    if (!foldedInto.TryGetValue(template, out var bound))
                        foldedInto[template] = bound = new List<string>();
                    if (!bound.Contains(command)) bound.Add(command);
                    continue;
                }
                var fits = templates.FirstOrDefault(t => memo.NoteMatch(t, command));
                if (fits != null) beyond[command] = fits;
                refused.Add(command);
            }

            LeverRow Row(string command, LeverSource source, string note)
            {
                var inFile = memo.InFile(command);
                // ONE OF THE KIT'S FIXED VERBS (fourth audit, M1; `get` left the list in the fourteenth): the gate runs it
                // with no tick and IsTicked says ticked, so the box says so too. Nothing to tick — but one already in the file (ticked before
                // this was fixed) can be taken back, like anything else in there.
                if (IsReadOnly(command))
                    return new LeverRow
                    {
                        Command = command, Approved = true, CoveredBy = null, CanTick = inFile,
                        Note = ReadNeedsNoTickNote, Source = source,
                    };
                var cover = memo.Covering(command);
                var byAnother = !inFile && cover != null;
                // The reason is computed whatever the tick says. A command whose first word is not
                // literal that is ALREADY in levers.json (hand-edited — this window refuses to tick
                // one) is ignored by the gate; it is shown ticked, with the reason, and can be
                // unticked — whatever is in the file can always be taken back.
                var reason = memo.NotTickableReason(command);
                // Seventh audit, M2: a REFUSED command that holds a placeholder is a template nobody authored (h=knight
                // of `SelectHero {{h}}` gives `SelectHero {knight}`), and a tick of it would approve every command of its
                // shape. It is not in levers.json, so there is nothing to take back: its box is disabled, and its note is
                // the ordinary one (ARefusedCommandThatHoldsAPlaceholderCannotBeTicked).
                var refusedTemplate = source == LeverSource.RefusedHere && !inFile && ShotBinding.HasPlaceholder(command);
                return new LeverRow
                {
                    Command = command,
                    Approved = inFile || cover != null,
                    CoveredBy = cover,
                    CanTick = !byAnother && (reason == null || inFile) && !refusedTemplate,
                    Note = reason ?? (byAnother ? CoveredByTemplateNote(cover!) : note),
                    Source = source,
                };
            }

            foreach (var command in needed)
                if (seen.Add(command))
                {
                    var row = Row(command, LeverSource.Needed, "");
                    if (!row.Approved && row.Note.Length == 0 && foldedInto.TryGetValue(command, out var bound))
                        row.Note = RefusedAsNote(bound);
                    // PARTLY COVERED (fourth and fifth audits, S1): a witness shows a ticked entry letting
                    // some of this template's commands through without being its shape, so this box stays
                    // empty — and the row names the entry and the witness. Appended to a refused-as note,
                    // never skipped because of one (fifth audit). ONE entry, the first in file order: the
                    // `break` (sixth audit, Q2).
                    if (!row.Approved)
                        foreach (var entry in approved)
                            if (memo.SomeCommandLetThrough(entry, command) is { } example)
                            {
                                var partly = PartlyCoveredNote(entry, example);
                                row.Note = row.Note.Length == 0 ? partly : row.Note + "; " + partly;
                                break;
                            }
                    rows.Add(row);
                }

            // The needed commands each ticked entry COVERS, by the gate's own rule (CoveringApproval's answer, never the
            // time-limit memo),
            // in the files' order — computed ONCE per call (eighth audit, S1). The loop below used to ask it of every
            // needed command again for each ticked entry, and each ask reads every ticked entry: about half a minute
            // after a re-draft left 150 stale ticks (RowsWithTicksAReDraftLeftStaleStayFarUnderTheOldCost).
            var coveredByEntry = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var n in needed)
                if (memo.Covering(n) is { } coveringEntry)
                {
                    if (!coveredByEntry.TryGetValue(coveringEntry, out var coveredList))
                        coveredByEntry[coveringEntry] = coveredList = new List<string>();
                    coveredList.Add(n);
                }

            foreach (var command in approved)
                if (seen.Add(command))
                {
                    // What this entry does for the files on disk, said only as far as it is known (fifth
                    // audit, S1): the needed commands it COVERS (the gate's own rule); else, for a
                    // TEMPLATE, TemplateNote; else, for a LITERAL, whether a template the files ask for can
                    // be bound to it (CanBeBoundTo) — and only when none can, "not needed".
                    var covers = coveredByEntry.TryGetValue(command, out var coveredHere)
                        ? coveredHere
                        : new List<string>();
                    var note = covers.Count > 0
                        ? CoversNeededNote(covers)
                        : ShotBinding.HasPlaceholder(command)
                            ? TemplateNote
                            : needed.Any(n => memo.CanBeBoundTo(n, command))
                                ? BroaderTemplateNote
                                : NotNeededNote;
                    rows.Add(Row(command, LeverSource.Approved, note));
                }

            refused.Sort(StringComparer.Ordinal);
            foreach (var command in refused)
                if (seen.Add(command))
                    rows.Add(Row(command, LeverSource.RefusedHere,
                        beyond.TryGetValue(command, out var fits) ? RefusedBeyondTemplateNote(fits) : RefusedThisSessionNote));

            return rows;
        }

        /// <summary>
        /// NINTH AUDIT, S1 — ONE <see cref="Rows"/> CALL'S WORK, DONE ONCE PER TEXT. Rows asks the same few questions of the
        /// same texts over and over — needed × ticked for the partly-covered notes, refused × templates for the fold: is it
        /// literal enough, why can it not be ticked, what is its witness, does this template let that command through, which
        /// entry covers it. Each answer here is the plain function's, kept for the call, plus two things that change no
        /// answer: the kept regex behind its PREFILTER (<see cref="ShotBinding.TemplateMatcher"/>), and, for the
        /// partly-covered notes, no witness when neither template's literal text before its first placeholder begins the
        /// other's — a witness both let through begins with both. With <c>fast</c> false every question goes to the plain
        /// function, which is how <c>RowsAreByteForByteTheSameWithTheMemoOff</c> holds the two to one answer.
        ///
        /// TWO MATCHES, ON PURPOSE: <see cref="NoteMatch"/> keeps the seventh fold's time-limit memo (a template that ran
        /// out of time once is no match for the rest of the call) and serves the notes only; the box's covering entry
        /// (<see cref="CoveringFast"/>) never reads that memo, so the box and the gate cannot disagree about a template
        /// that ran out of time on one command and matches the next.
        /// </summary>
        private sealed class RowsMemo
        {
            private readonly IReadOnlyList<string> _approved;
            private readonly bool _fast;
            private readonly HashSet<string> _inFile;
            private readonly HashSet<string> _timedOut = new(StringComparer.Ordinal);
            private readonly Dictionary<string, bool> _notLiteralEnough = new(StringComparer.Ordinal);
            private readonly Dictionary<string, string?> _reason = new(StringComparer.Ordinal);
            private readonly Dictionary<string, ShotBinding.TemplateMatcher> _matchers = new(StringComparer.Ordinal);
            private readonly Dictionary<string, string> _witness = new(StringComparer.Ordinal);
            private readonly Dictionary<string, bool> _letsItsWitnessThrough = new(StringComparer.Ordinal);
            private readonly Dictionary<string, string?> _cover = new(StringComparer.Ordinal);
            private List<string>? _usableTicked;
            private Dictionary<string, string>? _firstTickedOfShape;

            public RowsMemo(IReadOnlyList<string> approved, bool fast)
            {
                _approved = approved;
                _fast = fast;
                _inFile = new HashSet<string>(approved, StringComparer.Ordinal);
            }

            public bool InFile(string command) =>
                _fast ? _inFile.Contains(command) : _approved.Contains(command, StringComparer.Ordinal);

            private bool NotLiteralEnough(string text)
            {
                if (!_fast) return Levers.NotLiteralEnough(text);
                if (!_notLiteralEnough.TryGetValue(text, out var answer))
                    _notLiteralEnough[text] = answer = Levers.NotLiteralEnough(text);
                return answer;
            }

            public string? NotTickableReason(string text)
            {
                if (!_fast) return Levers.NotTickableReason(text);
                if (!_reason.TryGetValue(text, out var reason)) _reason[text] = reason = Levers.NotTickableReason(text);
                return reason;
            }

            private bool UsableTemplate(string? entry) =>
                _fast
                    ? entry != null && ShotBinding.HasPlaceholder(entry) && !NotLiteralEnough(entry) && !NeverATemplate(entry)
                    : Levers.UsableTemplate(entry);

            private ShotBinding.TemplateMatcher Matcher(string template)
            {
                if (!_matchers.TryGetValue(template, out var matcher))
                    _matchers[template] = matcher = new ShotBinding.TemplateMatcher(template);
                return matcher;
            }

            /// <summary>The notes' match: the seventh fold's time-limit memo, in front of the plain match or the kept one.</summary>
            public bool NoteMatch(string template, string candidate)
            {
                if (_timedOut.Contains(template)) return false;
                bool ranOut;
                var matched = _fast
                    ? Matcher(template).TryMatch(candidate, out ranOut)
                    : ShotBinding.TryMatchTemplate(template, candidate, out ranOut);
                if (ranOut) _timedOut.Add(template);
                return matched;
            }

            /// <summary><see cref="TemplateLetsThrough(string?, string, Func{string, string, bool})"/>, by <see cref="NoteMatch"/>.</summary>
            public bool LetsThrough(string? entry, string command) =>
                _fast
                    ? UsableTemplate(entry) && NoteMatch(entry!, command) && TargetValuesHoldNoDot(entry!, command)
                    : TemplateLetsThrough(entry, command, NoteMatch);

            public bool CanBeBoundTo(string needed, string literal) => Levers.CanBeBoundTo(needed, literal, NoteMatch);

            private string Witness(string text)
            {
                if (!_witness.TryGetValue(text, out var witness))
                    _witness[text] = witness = ShotBinding.WithEveryPlaceholderAs(text, "a");
                return witness;
            }

            private bool LetsItsWitnessThrough(string template)
            {
                if (!_letsItsWitnessThrough.TryGetValue(template, out var answer))
                    _letsItsWitnessThrough[template] = answer = LetsThrough(template, Witness(template));
                return answer;
            }

            /// <summary><see cref="Levers.SomeCommandLetThrough"/>: the same two witnesses (<see cref="Witnesses"/>), tried
            /// in the same order, each test in the same order.</summary>
            public string? SomeCommandLetThrough(string entry, string needed)
            {
                if (!_fast) return Levers.SomeCommandLetThrough(entry, needed, NoteMatch);
                if (!UsableTemplate(needed)) return null;
                if (!ShotBinding.HasPlaceholder(entry))
                    return !NotLiteralEnough(entry) && LetsThrough(needed, entry) ? entry : null;
                var entryPrefix = Matcher(entry).Prefix;
                var neededPrefix = Matcher(needed).Prefix;
                if (!entryPrefix.StartsWith(neededPrefix, StringComparison.Ordinal)
                    && !neededPrefix.StartsWith(entryPrefix, StringComparison.Ordinal))
                    return null;
                var byNeeded = Witness(needed);
                if (LetsThrough(entry, byNeeded) && LetsItsWitnessThrough(needed)) return byNeeded;
                var byEntry = Witness(entry);
                if (LetsItsWitnessThrough(entry) && LetsThrough(needed, byEntry)) return byEntry;
                return null;
            }

            /// <summary><see cref="CoveringApproval"/>'s answer for <paramref name="command"/>, once per command per call.</summary>
            public string? Covering(string command)
            {
                if (!_fast) return CoveringApproval(_approved, command);
                if (!_cover.TryGetValue(command, out var entry)) _cover[command] = entry = Levers.Covering(_approved, command, this);
                return entry;
            }

            /// <summary>
            /// The covering rule's three passes over this call's ticked entries, each answered as its loop answers it. The
            /// exact pass is a set look-up: the list holds each text once, so the entry equal to the command IS the command.
            /// The template → template pass is the FIRST usable ticked template, in file order, whose
            /// <see cref="ShotBinding.ShapeKey"/> is the command's — equal keys exactly when <see cref="ShotBinding.SameShape"/>.
            /// The template pass tries the usable ticked templates in file order, through the kept regex behind its
            /// prefilter, and never through the time-limit memo.
            /// </summary>
            internal string? CoveringFast(string command)
            {
                if (_inFile.Contains(command) && !NotLiteralEnough(command)) return command;
                if (NeverATemplate(command)) return null;
                if (ShotBinding.HasPlaceholder(command))
                    return FirstTickedOfShape().TryGetValue(ShotBinding.ShapeKey(command), out var same) ? same : null;
                foreach (var entry in UsableTicked())
                    if (Matcher(entry).TryMatch(command, out _) && TargetValuesHoldNoDot(entry, command))
                        return entry;
                return null;
            }

            private List<string> UsableTicked() => _usableTicked ??= _approved.Where(e => UsableTemplate(e)).ToList();

            private Dictionary<string, string> FirstTickedOfShape()
            {
                if (_firstTickedOfShape != null) return _firstTickedOfShape;
                var first = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var entry in UsableTicked())
                {
                    var shape = ShotBinding.ShapeKey(entry);
                    if (!first.ContainsKey(shape)) first[shape] = entry;
                }
                return _firstTickedOfShape = first;
            }
        }
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
            return new LivenessHold(this);
        }

        private sealed class LivenessHold : IDisposable
        {
            private readonly LeverGateBridge _bridge;
            public LivenessHold(LeverGateBridge bridge) => _bridge = bridge;
            public void Dispose() => _bridge._liveThisAttempt = null;
        }

        /// <summary>
        /// May this lever run now? <see cref="Levers.LeverAllowed"/>'s question — for a write that does not come through
        /// <see cref="Run"/>, a shot's <c>timeScale</c> step — asked under the liveness held for this attempt.
        /// </summary>
        internal bool LeverAllowed(string lever) => !Live || Levers.Allows(Levers.Approved(_projectRoot), lever);

        /// <summary>Whether the last <see cref="Run"/> was refused by this gate (not by the game): the ready gate reads it to
        /// say "not ticked" rather than "could not read" (the fourteenth audit, ruling 3).</summary>
        internal bool LastRunRefused { get; private set; }

        public bool Run(string command)
        {
            LastRunRefused = false;
            if (!Live) return _inner.Run(command);
            if (Levers.IsReadOnly(command)) return _inner.Run(command);
            var approved = Levers.Approved(_projectRoot);
            if (Levers.Allows(approved, command))
            {
                var spec = CameraSpecNotTicked(command, approved);
                if (spec == null) return _inner.Run(command);
                Remember(spec);
                _log?.Invoke(Levers.NotApprovedLog(spec));
                return false;
            }
            // FALSE, not an exception: the director fails the step, logs it and carries on, which
            // is how every other refused write behaves here.
            LastRunRefused = true;
            Remember(command);
            _log?.Invoke(Levers.NotApprovedLog(command ?? ""));
            return false;
        }

        /// <summary>
        /// Audit M3 — A <c>camera-pose</c> ALSO RUNS THE METHODS adapter.json's <c>camera</c> BLOCK
        /// NAMES, and those need their own <c>camera-spec</c> lever (<see cref="Levers.CameraSpecLever"/>).
        /// <see cref="CameraPose.Pose"/> checks it, but through <see cref="Levers.LeverAllowed"/>
        /// without this run's cloud flag — so during a try on a project with its own files it
        /// answered "ungated". Checked here, where the flag is, against the same adapter.json
        /// <c>CameraPose</c> reads. Null = nothing more is needed.
        /// </summary>
        private string? CameraSpecNotTicked(string? command, IReadOnlyList<string> approved)
        {
            var verb = (command ?? "").TrimStart().Split(' ', '\t')[0];
            if (!string.Equals(verb, "camera-pose", StringComparison.OrdinalIgnoreCase)) return null;
            var camera = AdapterJson.Load(_projectRoot).Camera;
            if (camera == null || !Levers.CameraSpecNeeded(camera.Value)) return null;
            var lever = Levers.CameraSpecLever(camera.Value);
            return Levers.Allows(approved, lever) ? null : lever;
        }
    }
}
