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
    public static partial class Levers
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

        /// <summary>The line a delivered command holding a {placeholder} leaves: a template is approved, never run (the Fix 4
        /// audit, H1 — invariant 182).</summary>
        public static string TemplateNeverRunsLog(string command) =>
            $"{TemplateNeverRunsWords} (a ticked template approves its filled-in commands, not its own text): '{command}'";

        /// <summary>The one sentence for <see cref="GateRefusal.Template"/>, wherever a refusal is worded.</summary>
        public const string TemplateNeverRunsWords = "a cheat with a value slot never runs as-is — it needs a value";

        /// <summary>
        /// THE MIGRATION SENTENCE (the P2 review, finding 4). A levers.json written before Recorder Kit 0.10.0 may hold a
        /// ticked TEMPLATE (<c>SelectHero {hero}</c>) that used to cover every command of its shape. Since P2 a template
        /// covers nothing (<see cref="IsTemplate"/>), so a studio upgrading the kit sees <c>SelectHero Knight</c> refused
        /// with its template still ticked — and the generic <see cref="NotApprovedLog"/> does not say why, or what to do.
        /// This names the template and the fix.
        /// </summary>
        public static string TemplateNoLongerCovers(string template, string command) =>
            $"the ticked template `{template}` no longer covers filled-in commands (since Recorder Kit 0.10.0 only a " +
            $"FIXED command is approved, exactly as ticked) — tick each fixed command in Nova Capture, e.g. `{command}`";

        /// <summary>
        /// The ticked template in <paramref name="approved"/> that covered the fixed <paramref name="command"/> BEFORE
        /// Recorder Kit 0.10.0 — the pre-P2 covering rule (a placeholder in a later word, the entry's first word literal and
        /// equal to the command's, <see cref="ShotBinding.MatchesTemplate"/>, <see cref="TargetValuesHoldNoDot"/>; never a
        /// <c>hide-overlay</c> / <c>camera-spec</c> lever) — or null. It decides nothing: it only picks the sentence a
        /// refusal is said in (<see cref="UntickedLog"/>). Entries are prefiltered by first word, so a refusal costs a match
        /// only against templates of the command's own verb.
        /// </summary>
        public static string? FormerTemplateCovering(IEnumerable<string>? approved, string? command)
        {
            if (approved == null || string.IsNullOrEmpty(command)) return null;
            // The bridge asks this on every refused write, so the entries are cut down with string scans before any shape
            // is read: only entries holding a brace, of the command's own verb. A levers.json with no template pays one
            // character scan per entry — the order of the exact-membership scan the refusal already made.
            var verb = FirstWordOf(command);
            return FormerTemplateCovering(
                approved.Where(e => e != null && e.IndexOf('{') >= 0 && string.Equals(FirstWordOf(e), verb, StringComparison.Ordinal))
                    .Select(FormerShape),
                command, (t, c) => ShotBinding.MatchesTemplate(t, c));
        }

        /// <summary>A ticked entry the pre-P2 gate read as a template (<see cref="WasATemplateBeforeP2"/>), with the three
        /// literal pieces a command it covered must share: its verb, the text before its first placeholder and the text
        /// after its last. Null for any other entry.</summary>
        private sealed class FormerTemplateShape
        {
            public string Entry = "", Verb = "", Prefix = "", Suffix = "";
        }

        private static FormerTemplateShape? FormerShape(string? entry)
        {
            if (!WasATemplateBeforeP2(entry)) return null;
            ShotBinding.LiteralEnds(entry!, out var prefix, out var suffix);
            return new FormerTemplateShape { Entry = entry!, Verb = FirstWordOf(entry), Prefix = prefix, Suffix = suffix };
        }

        /// <summary>The body: the first shape whose verb, literal prefix and literal suffix the command shares (necessary
        /// conditions of the match, read without a regex — the literal pieces sit right after <c>\A</c> and right before
        /// <c>\z</c> of the pattern), and then the binder's match and <see cref="TargetValuesHoldNoDot"/>. The cheap checks
        /// first keep a <see cref="Rows"/> call at the lever cap under its bound.</summary>
        private static string? FormerTemplateCovering(IEnumerable<FormerTemplateShape?> shapes, string? command,
            Func<string, string, bool> match)
        {
            if (string.IsNullOrEmpty(command) || ShotBinding.HasPlaceholder(command!) || NeverATemplate(command)) return null;
            var verb = FirstWordOf(command!);
            foreach (var shape in shapes)
                if (shape != null && string.Equals(shape.Verb, verb, StringComparison.Ordinal)
                    && command!.Length >= shape.Prefix.Length + shape.Suffix.Length
                    && command.StartsWith(shape.Prefix, StringComparison.Ordinal)
                    && command.EndsWith(shape.Suffix, StringComparison.Ordinal)
                    && match(shape.Entry, command) && TargetValuesHoldNoDot(shape.Entry, command))
                    return shape.Entry;
            return null;
        }

        /// <summary>An entry the pre-P2 gate read as a template: it holds a placeholder, it is literal enough by every rule
        /// but <see cref="IsTemplate"/>, and it is not a lever read out of a data field.</summary>
        private static bool WasATemplateBeforeP2(string? entry) =>
            entry != null && ShotBinding.HasPlaceholder(entry) && !NeverATemplate(entry)
            && !FirstWordHasPlaceholder(entry) && !TargetHasPlaceholder(entry) && !TargetPlaceholderIsGlued(entry)
            && !ShotBinding.HasAdjacentPlaceholders(entry) && !CameraPoseTypeNotLiteral(entry)
            && !TemplateHasBraceOutsideAPlaceholder(entry) && UnsendableCharacter(entry) == null;

        /// <summary>" — " and <see cref="TemplateNoLongerCovers"/> when a ticked template used to cover this command
        /// (<see cref="FormerTemplateCovering"/>); else empty.</summary>
        public static string TemplateSuffix(IEnumerable<string>? approved, string command) =>
            FormerTemplateCovering(approved, command) is { } template ? " — " + TemplateNoLongerCovers(template, command) : "";

        /// <summary>The line an UN-TICKED command leaves: <see cref="NotApprovedLog"/>, plus the migration sentence when a
        /// ticked template used to cover it (<see cref="TemplateSuffix"/>) — the bridge's refusal, a try's and a take's.</summary>
        public static string UntickedLog(IEnumerable<string>? approved, string command) =>
            NotApprovedLog(command) + TemplateSuffix(approved, command);

        /// <summary>The note on a ticked TEMPLATE's own row in the window: it is in levers.json from before 0.10.0, it
        /// approves nothing now, and what to tick instead — with a real example when a command on this machine fits it.</summary>
        public static string TickedTemplateNote(string? example) =>
            "a template ticked before Recorder Kit 0.10.0 — a template tick no longer covers filled-in commands and " +
            "approves nothing now (only a FIXED command is approved, exactly as ticked). Tick each fixed command instead" +
            (example != null ? $", e.g. `{example}`" : ", one per value") + "; untick this to tidy the list";

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

        /// <summary>
        /// WHY THIS LEVER MAY NOT RUN NOW, in the gate's own sentence — or null when it may. <see cref="LeverAllowed"/>'s
        /// question plus the risk rule (<see cref="RiskyNotYet"/>) the bridge asks of every ticked command, for a write that
        /// does not come through the bridge: a camera pose's spec (<see cref="CameraPose.Pose"/>; the P2 review, finding 1 —
        /// it used to ask only whether the spec was ticked). Null whenever the gate is not live; else
        /// <see cref="NotApprovedLog"/> when nothing ticks it, <see cref="CheatRisk.NotYetLog"/> when it is ticked but risky
        /// and the project is not ticked non-production, null otherwise. Words first, the tick list only when they are quiet.
        /// </summary>
        public static string? LeverRefusal(string projectRoot, string lever, bool cloudContent = false)
        {
            if (!GateActiveFor(projectRoot, cloudContent)) return null;
            var approved = ReadFile(projectRoot, out _, out var nonProduction);
            if (!Allows(approved, lever)) return NotApprovedLog(lever);
            if (nonProduction) return null;
            var why = RiskyNotYet(lever, false, null) ?? RiskyNotYet(lever, false, TickList.Read(projectRoot));
            return why == null ? null : CheatRisk.NotYetLog(lever, why);
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
    }
}
