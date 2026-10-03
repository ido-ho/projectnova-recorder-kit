using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ProjectNova.RecorderKit
{
    // Levers — the lever grammar: read-only verbs, covering and templates, risk, tickability. The class summary is in Levers.cs.
    public static partial class Levers
    {
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
        /// v3 P2 (§3.1) — would the gate hold this TICKED lever back until the non-production tick? The ONE rule, asked
        /// by <see cref="LeverGateBridge.Run"/> for each command and by a try's pre-check (<see cref="FirstRiskyNotYet"/>):
        /// null when the project is ticked non-production, for the kit's fixed verbs (<see cref="IsReadOnly"/>, which run
        /// before the check), and for the two levers that call nothing a person named (<see cref="TimeScaleLever"/>,
        /// <c>hide-overlay …</c>, which disables a MonoBehaviour); otherwise why it is risky (<see cref="CheatRisk.Why"/>),
        /// or null. A <c>camera-spec …</c> IS asked (the P2 review, finding 1): its words are the METHOD names a pose
        /// invokes in the studio's game, so <c>camera-spec Rig SetPosition DeleteSave SetFov</c> is risky by its words and
        /// waits for the non-production tick like any cheat — at the bridge, in <see cref="CameraPose.Pose"/>
        /// (<see cref="LeverRefusal"/>), in a try's and a take's pre-check, and on its window row.
        /// </summary>
        public static string? RiskyNotYet(string? lever, bool nonProduction, IReadOnlyList<TickList.Candidate>? delivered)
        {
            if (nonProduction || string.IsNullOrEmpty(lever) || IsReadOnly(lever)
                || lever!.StartsWith(HideOverlayPrefix, StringComparison.Ordinal)
                || string.Equals(lever, TimeScaleLever, StringComparison.Ordinal))
                return null;
            return CheatRisk.Why(lever, delivered);
        }

        /// <summary>The first lever of <paramref name="needed"/>, in its own order, that <see cref="RiskyNotYet"/> holds back
        /// on this project now, with why — or null. A try asks it after <see cref="FirstUnticked"/>, so a try that the gate
        /// would stop half-way is refused whole, with the gate's own sentence (<see cref="CheatRisk.NotYetLog"/>).</summary>
        public static (string Lever, string Why)? FirstRiskyNotYet(IEnumerable<string>? needed, string projectRoot)
        {
            ReadFile(projectRoot, out _, out var nonProduction);
            if (nonProduction) return null;
            var delivered = TickList.Read(projectRoot);
            foreach (var lever in needed ?? Array.Empty<string>())
                if (RiskyNotYet(lever, false, delivered) is { } why)
                    return (lever, why);
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
            || TemplateHasBraceOutsideAPlaceholder(command) || UnsendableCharacter(command) != null
            || (IsTemplate(command) && !IsSetValueTemplate(command));

        /// <summary>The placeholder name the kit's own set-value templates use (<c>set Type.Member {v}</c>).</summary>
        public const string SetValueSlotName = "v";

        private static readonly System.Text.RegularExpressions.Regex SetValueTemplateRe = new(
            @"\Aset [A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+ \{([a-z][A-Za-z0-9]*)\}\z",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>
        /// Fix 4 (2026-09-28) — THE ONE TEMPLATE SHAPE THAT MAY BE TICKED: <c>set Type.Member {v}</c>. Exactly the verb
        /// <c>set</c>, one space, a literal dotted target (letters, digits and <c>_</c>, at least one dot — no brace, quote
        /// or <c>[key]</c>), one space, and ONE whole placeholder as the value, with nothing after it. Ticked, it lets
        /// through <c>set Type.Member &lt;value&gt;</c> for any one value the binder's value class allows
        /// (<see cref="ShotBinding.MatchesTemplate"/>: letters, digits, <c>.</c>, <c>_</c>, <c>-</c>) — the member is
        /// fixed and was shown to the person; the VALUE is what the cloud chooses. This REOPENS learn-and-drive v3 P2's
        /// "only fixed commands are ticked" for this one shape and no other (the r1 revision of the six-fixes plan, D4):
        /// a dev UI's setter takes any value by design, and the website words the row as "sets any value — a wider grant
        /// than a fixed cheat". Every other template (a placeholder in the target, a second value, another verb) is still
        /// refused. The website's twin is <c>isSetValueTemplate</c> in <c>shots-lint.ts</c>; both are held to
        /// <c>lever-shapes.cases.json</c>. Invariant 182.
        /// </summary>
        public static bool IsSetValueTemplate(string? command) => command != null && SetValueTemplateRe.IsMatch(command);

        /// <summary>The words the website shows on a set-value row (the web's <c>SETS_ANY_VALUE</c>) — one sentence.</summary>
        public const string SetsAnyValueWords = "sets any value — a wider grant than a fixed cheat";

        /// <summary>The line the Nova Capture window draws under a set-value template's row (the Fix 4 audit, M2), else null.</summary>
        public static string? SetValueGrantLine(string? command) =>
            IsSetValueTemplate(command)
                ? $"{SetsAnyValueWords}: ticked, it lets the website set {SecondWordOf(command)} to any one value it chooses"
                : null;

        /// <summary>The placeholder name of a set-value template (<c>v</c> in <c>set Player.coins {v}</c>), else null.</summary>
        public static string? SetValueSlot(string? command)
        {
            if (command == null) return null;
            var m = SetValueTemplateRe.Match(command);
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>
        /// Learn-and-drive v3 P2 (§2.6, §3.3, §4) — ONLY FIXED COMMANDS ARE TICKED. A command holding a
        /// <c>{placeholder}</c> is a template, and a ticked template let through any delivered command of its shape:
        /// the cloud chose the arguments of a command a person had approved in the abstract — <c>tap-at {x} {y}</c>,
        /// ticked once, pressed anywhere on the screen. "A person ticked it" is the only trust line, so a delivered
        /// command runs only when it is EXACTLY an entry of levers.json. Since P2 a template is not literal enough
        /// (<see cref="NotLiteralEnough"/>): the window will not tick one, the covering rule reads an entry with a
        /// placeholder as covering nothing — not even its own text — and the website refuses to send one
        /// (<c>lever-shapes.cases.json</c>, <c>deliveries.cases.json</c>). The template matcher below is kept, dormant:
        /// with no usable template, <see cref="CoveringApproval"/> is the exact pass alone.
        /// </summary>
        public static bool IsTemplate(string? command) => command != null && ShotBinding.HasPlaceholder(command);

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
                    // v3 P2 (§3.3): a DECLARED placeholder is refused too. Only fixed commands are ticked (IsTemplate), so a
                    // delivered template is a lever no tick can ever pass — refused here, by name, as the site refuses it.
                    foreach (var text in texts)
                        if (IsTemplate(text))
                            return $"shot {name} asks for '{text}', a template — this kit runs only FIXED commands a person " +
                                   "ticked, so no tick could ever pass it; write each value out — nothing was written";
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
            if (IsTemplate(command) && !IsSetValueTemplate(command))
                return FixedCommandsOnlyReason;
            // Kit 0.14.3 (KIT-2, invariant 192): a call/set spelled in the runtime's or the engine's namespaces — the pure,
            // shared half of the reflection scope (runner-scope.cases.json; the website's placeholderFirst says the same).
            // The resolved half (a bare `Process.Start`) is asked where a person ticks — SetApproved, AllowNeeded, the row's
            // note — not here: TickList refuses a whole file on this answer, and the website cannot resolve a type.
            if (ReflectionScope.NameRefusal(command) is { } outside)
                return outside;
            return null;
        }

        /// <summary>
        /// Kit 0.14.3 (KIT-2) — why a person may not tick this command HERE, on this machine: <see cref="NotTickableReason"/>,
        /// then the reflection scope resolved against this editor's types (<see cref="ReflectionScope.ResolvedRefusal"/>).
        /// Editor main thread only.
        /// </summary>
        public static string? NotTickableHere(string? command) =>
            NotTickableReason(command) ?? ReflectionScope.ResolvedRefusal(command);

        /// <summary>Why a template cannot be ticked (<see cref="IsTemplate"/>) — named once, for the window and the tests.</summary>
        public const string FixedCommandsOnlyReason =
            "this command holds a {placeholder}, so it is a template the website would fill in. Only FIXED commands " +
            "are ticked: a delivered command runs only when it is exactly a command you ticked, arguments and all. It " +
            "cannot be approved here; ask for each value as its own command (SelectHero Knight, SelectHero Mage).";
    }
}
