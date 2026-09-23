using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Binds `{name}` placeholders in a shot's command strings to values supplied per run.
    ///
    /// Every failure is a HARD error rather than a best-effort substitution, because the one
    /// unacceptable outcome for a parameterized shot is silently filming the wrong content while
    /// every automated signal stays green.
    ///
    /// Pure and static so it unit-tests without Unity, a game, or a director — apart from the one
    /// warning line a timed-out template match leaves in the console (<see cref="MatchTimeout"/>).
    /// </summary>
    public static class ShotBinding
    {
        /// <summary>Parameter NAMES: lowerCamelCase, the same class <see cref="PlaceholderPattern"/> reads between
        /// braces. <c>\A…\z</c>, as for <see cref="ValuePattern"/> (sixth audit).</summary>
        private static readonly Regex NamePattern = new(@"\A[a-z][A-Za-z0-9]*\z", RegexOptions.Compiled);

        /// <summary>The VALUE class: letters, digits, '.', '_' and '-', at least one. ONE string, and the binder
        /// (<see cref="ValuePattern"/>), the gate's match (<see cref="MatchesTemplate"/>) and the separator rule
        /// (<see cref="ValueCharsOnly"/>) are each built from it, so they cannot come to disagree about what a
        /// value may contain.</summary>
        private const string ValueBody = "[A-Za-z0-9._-]+";

        /// <summary>
        /// Parameter VALUES. ReflectionCheatBridge.Run splits commands on spaces, so forbidding
        /// whitespace stops a value injecting a second command, and forbidding '@' stops it forging
        /// a label selector ("Button@Label").
        ///
        /// <c>\A…\z</c>, not <c>^…$</c> (sixth audit, S1): in .NET <c>$</c> also matches BEFORE a final "\n", so
        /// the binder took "Knight\n" and wrote <c>SelectHero Knight\n</c>, which the gate's <c>\z</c> refused
        /// even with the template ticked — and the window listed that binding of the files' own template as a
        /// command no file asks for. The binder now refuses such a value by name, as it refuses a space.
        /// </summary>
        private static readonly Regex ValuePattern = new(@"\A" + ValueBody + @"\z", RegexOptions.Compiled);

        /// <summary>A `{name}` whose contents are a VALID parameter name. `{"k":1}` is not one.</summary>
        private static readonly Regex PlaceholderPattern =
            new(@"\{([a-z][A-Za-z0-9]*)\}", RegexOptions.Compiled);

        public static bool ValidateName(string name) => name != null && NamePattern.IsMatch(name);
        public static bool ValidateValue(string value) => value != null && ValuePattern.IsMatch(value);

        /// <summary>
        /// Slice A2′ — would <paramref name="candidate"/> be a legal BINDING of
        /// <paramref name="template"/>? A template with no placeholder matches only itself; each
        /// `{name}` matches exactly what <see cref="ValidateValue"/> allows a bound value to be (one
        /// <see cref="ValueBody"/>, and since the sixth audit both read it between <c>\A</c> and <c>\z</c>),
        /// and everything around them is literal. A name used TWICE matches the same value twice
        /// (fifth audit, M4): <see cref="TryApply"/> binds every occurrence of a name to one value, so
        /// ticked <c>set Player.{a} {a}</c> covers <c>set Player.coins coins</c> and not
        /// <c>set Player.coins 999</c> — the first occurrence is a named group, each repeat a
        /// backreference to it.
        ///
        /// Lives HERE, beside the two patterns it is made of, because the lever gate asks the same
        /// question `TryApply` answers ("is this the shape the author approved?") and a second copy
        /// of the grammar would drift: approving `SelectHero {hero}` must never come to mean
        /// approving `SelectHero Knight; DoSomethingElse`.
        /// </summary>
        public static bool MatchesTemplate(string? template, string? candidate) =>
            TryMatchTemplate(template, candidate, out _);

        /// <summary><see cref="MatchesTemplate"/>, also saying whether the match ran out of time
        /// (<see cref="MatchTimeout"/>), which it answers as no match. <c>Levers.Rows</c> uses it to stop
        /// matching a template for the rest of one call once it has run out of time
        /// (<c>RowsWithFiftyTickedLiteralsAndOneSlowTemplateTakeWellUnderASecond</c>).</summary>
        internal static bool TryMatchTemplate(string? template, string? candidate, out bool timedOut)
        {
            timedOut = false;
            if (template == null || candidate == null) return false;
            if (!HasPlaceholder(template))
                return string.Equals(template, candidate, StringComparison.Ordinal);
            // A TIME LIMIT, read as NO MATCH (fourth audit, M3). One value class per placeholder and
            // nothing else between two of them makes the match try every way of splitting the text —
            // ×6.4 per placeholder, 7.5 s at eleven — and the window runs this on every repaint. Such
            // templates cannot be ticked (Levers.NotLiteralEnough, via HasAdjacentPlaceholders), and
            // since the fifth audit neither can `{a}.{b}`, `{a}_{b}` or `{a}-{b}`: the value class
            // holds `.`, `_` and `-`, so those are as ambiguous as `{a}{b}`. The limit stays as a belt
            // for a text that is matched without being ticked (a hand-edited entry, a refused
            // command). Fail closed: a match that ran out of time approves nothing and folds nothing.
            try
            {
                return Regex.IsMatch(candidate, TemplatePattern(template), RegexOptions.None, MatchTimeout);
            }
            catch (RegexMatchTimeoutException)
            {
                timedOut = true;
                LogTimeoutOnce(template);
                return false;
            }
        }

        /// <summary>The pattern a template's bindings match: <c>\A</c>, each literal piece escaped, each first use of a
        /// name one <see cref="ValueBody"/> group and each repeat a backreference to it, <c>\z</c>. ONE builder, for
        /// <see cref="TryMatchTemplate"/> and for the <see cref="TemplateMatcher"/> one <c>Levers.Rows</c> call keeps.</summary>
        private static string TemplatePattern(string template)
        {
            var pattern = new StringBuilder(@"\A");
            var last = 0;
            var named = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in PlaceholderPattern.Matches(template))
            {
                pattern.Append(Regex.Escape(template.Substring(last, m.Index - last)));
                var name = m.Groups[1].Value;
                pattern.Append(named.Add(name) ? "(?<" + name + ">" + ValueBody + ")" : @"\k<" + name + ">");
                last = m.Index + m.Length;
            }
            pattern.Append(Regex.Escape(template.Substring(last)));
            // `\A`/`\z`, never `^`/`$`: in .NET `$` also matches BEFORE a trailing newline, so an
            // approved `SelectHero {hero}` would match "SelectHero Knight\n…" — and a command is
            // split on whitespace by the bridge underneath.
            pattern.Append(@"\z");
            return pattern.ToString();
        }

        /// <summary>
        /// Ninth audit, S1 — ONE TEMPLATE'S MATCH, BUILT ONCE, for the length of one <c>Levers.Rows</c> call. The same
        /// answer as <see cref="TryMatchTemplate"/> (the same <see cref="TemplatePattern"/>, the same time limit, the same
        /// once-only log line), with the regex kept instead of rebuilt — the static cache holds 15 patterns, and a window
        /// over hundreds of templates missed it on nearly every match — and a PREFILTER in front of it: a text that does
        /// not begin with the template's literal text before its first placeholder (<see cref="Prefix"/>) cannot match,
        /// because that text is the first thing the pattern reads after <c>\A</c>. A necessary condition, so no answer
        /// changes (<c>RowsAreByteForByteTheSameWithTheMemoOff</c>).
        /// </summary>
        internal sealed class TemplateMatcher
        {
            private readonly string _template;
            private readonly Regex? _regex;

            /// <summary>The template's literal text before its first placeholder — the whole text when it has none.</summary>
            public string Prefix { get; }

            public TemplateMatcher(string template)
            {
                _template = template;
                Prefix = LiteralPieces(template)[0];
                if (HasPlaceholder(template)) _regex = new Regex(TemplatePattern(template), RegexOptions.None, MatchTimeout);
            }

            public bool TryMatch(string candidate, out bool timedOut)
            {
                timedOut = false;
                if (!candidate.StartsWith(Prefix, StringComparison.Ordinal)) return false;
                if (_regex == null) return string.Equals(_template, candidate, StringComparison.Ordinal);
                try
                {
                    return _regex.IsMatch(candidate);
                }
                catch (RegexMatchTimeoutException)
                {
                    timedOut = true;
                    LogTimeoutOnce(_template);
                    return false;
                }
            }
        }

        /// <summary>How long one template match may run before it is read as no match.</summary>
        public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

        private static void LogTimeoutOnce(string template)
        {
            if (System.Threading.Interlocked.Exchange(ref _timeoutLogged, 1) != 0) return;
            var line = $"[RecorderKit] a lever template match timed out after {MatchTimeout.TotalMilliseconds:0} ms " +
                       $"and was read as NO match (fail closed): '{template}'. Said once per editor session.";
            if (_timeoutLog != null) _timeoutLog(line);
            else UnityEngine.Debug.LogWarning(line);
        }

        /// <summary>
        /// Fourth audit, S1 — DO TWO TEMPLATES HAVE THE SAME SHAPE? True when they are equal after
        /// every placeholder in each is replaced by ONE fixed token: <c>SelectHero {x}</c> and
        /// <c>SelectHero {hero}</c> are the same approval with a parameter renamed, and a re-draft
        /// that renames one must not leave the needed row unticked while the ticked one runs every
        /// binding. The token is one no text can spell: the literal pieces BETWEEN placeholders are
        /// compared, so a literal <c>{}</c> or <c>\0</c> in one can never stand in for a placeholder
        /// in the other. A text with no placeholder has one piece; the caller still refuses to let a
        /// LITERAL cover a template in so many words (<c>Levers.CoveringApproval</c>).
        ///
        /// WHICH PLACEHOLDERS SHARE A NAME is part of the shape (fifth audit, M4): a repeated name is
        /// one value (<see cref="MatchesTemplate"/>), so <c>set Player.{a} {a}</c> approves fewer
        /// commands than <c>set Player.{s} {v}</c> and is not the same shape.
        /// </summary>
        public static bool SameShape(string? a, string? b)
        {
            if (a == null || b == null) return false;
            return LiteralPieces(a).SequenceEqual(LiteralPieces(b), StringComparer.Ordinal)
                   && RepeatPattern(a).SequenceEqual(RepeatPattern(b));
        }

        /// <summary>Ninth audit, S1 — <see cref="SameShape"/> as one string: two texts have the same shape exactly when their
        /// keys are equal. The same two lists SameShape compares, each literal piece written with its length in front (so
        /// no piece's text can run into the next), then the repeat pattern. <c>Levers.Rows</c> finds the first ticked
        /// template of a needed template's shape by it, instead of comparing every pair.</summary>
        internal static string ShapeKey(string text)
        {
            var key = new StringBuilder();
            foreach (var piece in LiteralPieces(text)) key.Append(piece.Length).Append(':').Append(piece);
            key.Append('|');
            foreach (var i in RepeatPattern(text)) key.Append(i).Append(',');
            return key.ToString();
        }

        /// <summary>For each placeholder in order, the index of its NAME among the distinct names met
        /// so far: <c>{a} {a}</c> is [0, 0], <c>{s} {v}</c> is [0, 1], <c>{x} {y} {x}</c> is [0, 1, 0].</summary>
        private static List<int> RepeatPattern(string text)
        {
            var names = new List<string>();
            var pattern = new List<int>();
            foreach (Match m in PlaceholderPattern.Matches(text))
            {
                var name = m.Groups[1].Value;
                var i = names.IndexOf(name);
                if (i < 0)
                {
                    i = names.Count;
                    names.Add(name);
                }
                pattern.Add(i);
            }
            return pattern;
        }

        private static List<string> LiteralPieces(string text)
        {
            var pieces = new List<string>();
            var last = 0;
            foreach (Match m in PlaceholderPattern.Matches(text))
            {
                pieces.Add(text.Substring(last, m.Index - last));
                last = m.Index + m.Length;
            }
            pieces.Add(text.Substring(last));
            return pieces;
        }

        /// <summary>
        /// Are two placeholders ADJACENT anywhere in <paramref name="text"/> — is there nothing between
        /// two of them that a VALUE cannot hold? <c>{a}{b}</c> (fourth audit, M3), and since the fifth
        /// audit also <c>{a}.{b}</c>, <c>{a}_{b}</c> and <c>{a}-{b}</c>: a value may hold letters,
        /// digits, <c>.</c>, <c>_</c> and <c>-</c>, so <c>Knight-1</c> against <c>{x}-{y}</c> reads as
        /// x=Knight, y=1 or as x=Kni, y=ght-1. A command that fits such a template cannot be read back
        /// into one value per placeholder — nothing says where one ends and the next begins — so the
        /// gate refuses to tick one (slowness was only the symptom). A space between them, or any
        /// other character outside the value class (<c>{a} {b}</c>, <c>{a}:{b}</c>), is a separator.
        /// </summary>
        public static bool HasAdjacentPlaceholders(string? text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            var end = -1;
            foreach (Match m in PlaceholderPattern.Matches(text))
            {
                if (end >= 0 && ValueCharsOnly.IsMatch(text!.Substring(end, m.Index - end))) return true;
                end = m.Index + m.Length;
            }
            return false;
        }

        /// <summary>True when <paramref name="text"/> is exactly one placeholder and nothing else.</summary>
        public static bool IsOnePlaceholder(string? text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            var m = PlaceholderPattern.Match(text);
            return m.Success && m.Index == 0 && m.Length == text!.Length;
        }

        /// <summary><paramref name="template"/> with every placeholder replaced by
        /// <paramref name="value"/> — one concrete command of that shape, to name as an example.</summary>
        public static string WithEveryPlaceholderAs(string template, string value) =>
            PlaceholderPattern.Replace(template ?? "", value.Replace("$", "$$"));

        /// <summary>Where a timed-out template match is reported, once. Null = Unity's console.</summary>
        private static Action<string>? _timeoutLog;
        private static int _timeoutLogged;

        /// <summary>For the tests: route the once-only timeout line to <paramref name="sink"/> (null =
        /// the console again) and forget that it was ever written.</summary>
        internal static void ResetTimeoutLogForTests(Action<string>? sink)
        {
            _timeoutLog = sink;
            System.Threading.Interlocked.Exchange(ref _timeoutLogged, 0);
        }

        /// <summary>Nothing, or only characters a VALUE may hold — built from <see cref="ValueBody"/>, so
        /// the separator rule (<see cref="HasAdjacentPlaceholders"/>) and the binder cannot disagree.</summary>
        private static readonly Regex ValueCharsOnly = new(@"\A(?:" + ValueBody + @")?\z", RegexOptions.Compiled);

        /// <summary>
        /// Sixth audit, M5 — in a text WITH a placeholder, is a <c>{</c> or <c>}</c> left once every placeholder
        /// is taken out? <c>SelectHero {{h}}</c> bound to <c>knight</c> is <c>SelectHero {knight}</c>, which reads
        /// as a placeholder itself (<see cref="HasPlaceholder"/>). A text with no placeholder is not asked about:
        /// it is a literal, and a literal may hold braces.
        /// </summary>
        public static bool HasBraceOutsideAPlaceholder(string? text) =>
            !string.IsNullOrEmpty(text) && HasPlaceholder(text!)
            && PlaceholderPattern.Replace(text!, "").IndexOfAny(Braces) >= 0;

        private static readonly char[] Braces = { '{', '}' };

        /// <summary>The name of every placeholder in <paramref name="text"/>, in order (ninth audit, M1:
        /// <c>Levers.UnboundPlaceholder</c> asks which of them a shot declares).</summary>
        internal static IEnumerable<string> PlaceholderNames(string? text) =>
            string.IsNullOrEmpty(text)
                ? Enumerable.Empty<string>()
                : PlaceholderPattern.Matches(text).Cast<Match>().Select(m => m.Groups[1].Value);

        /// <summary>True when the text contains at least one placeholder. Drives the zero-param lint.</summary>
        public static bool HasPlaceholder(string text) =>
            !string.IsNullOrEmpty(text) && PlaceholderPattern.IsMatch(text);

        public static bool TryApply(string text, IReadOnlyList<string> declared,
            IReadOnlyDictionary<string, string> bindings, out string result, out string error) =>
            TryApply(text, declared, bindings, true, out result, out error);

        /// <summary>
        /// <paramref name="isLever"/>: is <paramref name="text"/> a LEVER — a shot's setup entry, or a <c>cheat</c> /
        /// <c>cheatUntil</c> command, the texts that reach the gate? The brace guard below applies to those only (ninth
        /// audit, M2): a click or hold NAME is looked up among the game's objects and never matched against a ticked
        /// template, so <c>Slot{0}_{i}</c> binds to <c>Slot{0}_3</c>, as the site (which checks braces in levers only)
        /// sends it.
        /// </summary>
        public static bool TryApply(string text, IReadOnlyList<string> declared,
            IReadOnlyDictionary<string, string> bindings, bool isLever, out string result, out string error)
        {
            result = text ?? "";
            error = "";
            if (string.IsNullOrEmpty(text)) return true;

            // A binding the shot never declared is a script typo that must surface even when
            // `validate` was skipped.
            foreach (var key in bindings.Keys)
            {
                if (!declared.Contains(key))
                {
                    error = $"binding '{key}' is not declared by this shot " +
                            $"(declared: {(declared.Count == 0 ? "none" : string.Join(", ", declared))})";
                    return false;
                }
            }

            // Seventh audit, S1 + M1: the gate reads a text whose verb is hide-overlay or camera-spec as never a template
            // (Levers.NeverATemplate, called here, not copied), so the binder writes no command from one
            // (TheBinderNeverWritesAHideOverlayOrCameraSpecCommandFromAPlaceholder).
            if (HasPlaceholder(text!) && Levers.NeverATemplate(text))
            {
                error = $"'{text}' holds a placeholder, and a '{text!.Substring(0, text.IndexOf(' '))}' command takes " +
                        "none: its words name a C# type or method";
                return false;
            }

            // Eighth audit, M1: a '{' or '}' outside a placeholder — the window's brace rule (HasBraceOutsideAPlaceholder),
            // called here, not copied. h=knight of `SelectHero {{h}}` wrote `SelectHero {knight}`, which reads as a template,
            // and the gate covers a template by a ticked template of the same shape, so a tick of `SelectHero {k}` ran it
            // (TheBinderWritesNoCommandFromATextWithABraceOutsideAPlaceholder).
            if (isLever && HasBraceOutsideAPlaceholder(text))
            {
                error = $"the lever '{text}' holds a '{{' or '}}' that is not part of a placeholder, so a bound value would " +
                        "make a command that reads as a placeholder itself";
                return false;
            }

            var failure = "";
            result = PlaceholderPattern.Replace(text, m =>
            {
                var name = m.Groups[1].Value;
                if (!declared.Contains(name))
                {
                    if (failure.Length == 0)
                        failure = $"placeholder '{{{name}}}' is not declared by this shot " +
                                  $"(declared: {(declared.Count == 0 ? "none" : string.Join(", ", declared))})";
                    return m.Value;
                }
                if (!bindings.TryGetValue(name, out var value))
                {
                    if (failure.Length == 0)
                        failure = $"placeholder '{{{name}}}' has no value bound for this run";
                    return m.Value;
                }
                if (!ValidateValue(value))
                {
                    if (failure.Length == 0)
                        failure = $"value '{value}' for '{name}' is not allowed " +
                                  "(letters, digits, dot, underscore, hyphen only — no spaces or '@')";
                    return m.Value;
                }
                return value;
            });

            if (failure.Length > 0)
            {
                error = failure;
                result = text ?? "";
                return false;
            }
            return true;
        }
    }
}
