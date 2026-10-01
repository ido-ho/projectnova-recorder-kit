using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ProjectNova.RecorderKit
{
    // Levers — the Nova Capture window rows: sources, notes, the rows memo. The class summary is in Levers.cs.
    public static partial class Levers
    {
        /// <summary>Where a row in the Nova Capture window came from — the sentence beside it.</summary>
        public enum LeverSource
        {
            /// <summary>One of the two JSON files on disk asks for it.</summary>
            Needed,
            /// <summary>Ticked here once; nothing on disk asks for it any more.</summary>
            Approved,
            /// <summary>The gate refused it in this editor session, and no file lists it.</summary>
            RefusedHere,
            /// <summary>v3 P2: the website PROPOSED it in <c>tick-list.json</c> (<see cref="TickList"/>); nothing asks
            /// for it yet and it runs nothing until it is ticked.</summary>
            Candidate,
        }

        /// <summary>The note beside a candidate row (<see cref="LeverSource.Candidate"/>).</summary>
        public static string CandidateNote(TickList.Candidate c) =>
            "proposed by the website" +
            (c.Kind != null ? $" (kind: {c.Kind}" + (c.Risk.Count > 0 ? $"; risk: {string.Join(", ", c.Risk)})" : ")")
                : c.Risk.Count > 0 ? $" (risk: {string.Join(", ", c.Risk)})" : "") +
            (c.Check != null ? $"; checked by: {c.Check.Describe()} — ticking it also ticks '{c.Check.Lever}'" : "") +
            " — it runs nothing until you tick it";

        /// <summary>
        /// v3 P3 (§3.3 step 2) — THE READS A TICK OF THIS ROW ALSO TICKS: the check lever of the delivered candidate of
        /// exactly this command (<c>get &lt;path&gt;</c> / <c>snapshot &lt;root&gt;</c> / <c>ui-text</c>) — "a `get` path ticked with the
        /// cheat". The window writes them with the row (it is levers.json's only writer); nothing is ever un-ticked by it:
        /// the read keeps its own row, and a read another cheat shares stays ticked. Empty for anything else.
        /// </summary>
        public static IReadOnlyList<string> CompanionTicks(string projectRoot, string command)
        {
            var list = new List<string>();
            foreach (var c in TickList.Read(projectRoot))
                if (string.Equals(c.Command, command, StringComparison.Ordinal) && c.Check?.Lever is { } lever
                    && NotTickableReason(lever) == null && !list.Contains(lever))
                    list.Add(lever);
            return list;
        }

        /// <summary>The note a risky row carries while the project is not ticked non-production (<see cref="RiskyNotYet"/>).</summary>
        public static string RiskyNote(string why) =>
            $"RISKY ({why}): refused even when ticked until \"{CheatRisk.NonProductionLabel}\" is ticked above";

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
            /// <summary>v3 P2: why the gate would hold this command back until the non-production tick
            /// (<see cref="RiskyNotYet"/>, the gate's own rule), or null — also null once the project is ticked.</summary>
            public string? RiskyNotYet { get; set; }
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
            // v3 P2: the tick list is a fourth file the rows are made of (its candidates, and the risk they raise).
            Part(SyncNova.Sha256OfFile(TickList.FilePath(projectRoot)));
            key.Append(RowsMemoOffForTests ? 'S' : 'F');
            foreach (var command in refused) Part(command);
            return key.ToString();
        }

        private static IReadOnlyList<LeverRow> ComputeRows(string projectRoot, IReadOnlyList<string> refusedHere)
        {
            var needed = Needed(projectRoot);
            var approved = ReadFile(projectRoot, out _, out var nonProduction);
            var candidates = TickList.Read(projectRoot);
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
                // kit 0.14.3 (KIT-2): …and a call/set whose type resolves outside the game's own code, said on its row
                var reason = memo.NotTickableReason(command) ?? ReflectionScope.ResolvedRefusal(command);
                // The P2 review, finding 4: a template ALREADY in levers.json (ticked before 0.10.0) is not "cannot be
                // approved here" — it was, and it now approves nothing. Say that, and what to tick instead.
                if (inFile && string.Equals(reason, FixedCommandsOnlyReason, StringComparison.Ordinal))
                {
                    // The example: a command on this machine — needed, refused here, or proposed — this template used to
                    // cover. First found, in that order.
                    string? example = null;
                    foreach (var c in needed.Concat(refusedHere).Concat(candidates.Select(x => x.Command)))
                        if (c != null && c.Length > 0 && memo.FormerTemplateIs(command, c))
                        {
                            example = c;
                            break;
                        }
                    reason = TickedTemplateNote(example);
                }
                // Seventh audit, M2: a REFUSED command that holds a placeholder is a template nobody authored (h=knight
                // of `SelectHero {{h}}` gives `SelectHero {knight}`), and a tick of it would approve every command of its
                // shape. It is not in levers.json, so there is nothing to take back: its box is disabled, and its note is
                // the ordinary one (ARefusedCommandThatHoldsAPlaceholderCannotBeTicked).
                var refusedTemplate = source == LeverSource.RefusedHere && !inFile && ShotBinding.HasPlaceholder(command);
                // v3 P2: the gate's own risk rule, so a row the gate would hold back says so (invariant 99).
                var risky = reason == null ? RiskyNotYet(command, nonProduction, candidates) : null;
                var baseNote = reason ?? (byAnother ? CoveredByTemplateNote(cover!) : note);
                // …and a fixed command a ticked template USED to cover says so on its own unticked row.
                if (reason == null && !inFile && cover == null && memo.FormerTemplate(command) is { } former)
                {
                    var migrate = "not ticked: " + TemplateNoLongerCovers(former, command);
                    baseNote = baseNote.Length == 0 ? migrate : baseNote + "; " + migrate;
                }
                return new LeverRow
                {
                    Command = command,
                    Approved = inFile || cover != null,
                    CoveredBy = cover,
                    CanTick = !byAnother && (reason == null || inFile) && !refusedTemplate,
                    Note = risky == null ? baseNote : baseNote.Length == 0 ? RiskyNote(risky) : baseNote + "; " + RiskyNote(risky),
                    Source = source,
                    RiskyNotYet = risky,
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

            // v3 P2 — WHAT THE WEBSITE PROPOSED (tick-list.json), after what the files ask for: one row per candidate
            // not already listed, unticked until the person ticks it. A candidate never becomes a tick by itself.
            foreach (var candidate in candidates)
                if (seen.Add(candidate.Command))
                    rows.Add(Row(candidate.Command, LeverSource.Candidate, CandidateNote(candidate)));

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

            private List<FormerTemplateShape>? _formerShapes;
            private readonly Dictionary<string, FormerTemplateShape?[]> _formerShapeOf = new(StringComparer.Ordinal);

            /// <summary><see cref="Levers.FormerTemplateCovering"/> over the ticked entries, by <see cref="NoteMatch"/> — each
            /// entry's shape read once per call.</summary>
            public string? FormerTemplate(string command)
            {
                _formerShapes ??= _approved.Select(FormerShape).Where(x => x != null).Select(x => x!).ToList();
                return FormerTemplateCovering(_formerShapes, command, NoteMatch);
            }

            /// <summary>Did the ticked <paramref name="template"/> cover <paramref name="command"/> before 0.10.0?</summary>
            public bool FormerTemplateIs(string template, string command)
            {
                if (!_formerShapeOf.TryGetValue(template, out var one))
                    _formerShapeOf[template] = one = new[] { FormerShape(template) };
                return FormerTemplateCovering(one, command, NoteMatch) != null;
            }

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
}
