using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Kit 0.13.5 — THE NEAR-NAME RULE: when a shot's step names something that is not on screen, which on-screen names
    /// are near enough to say "did you mean …?".
    ///
    /// ONE rule, two readers: this class and the website's <c>apps/api/src/game-capture/near-name.ts</c>, held together by
    /// <c>Editor/Tests/Fixtures/near-name.cases.json</c> (both suites run it; its <c>about</c> states the rule in full). In
    /// short: names split into lower-case word tokens (at <c>_</c>, <c>-</c>, spaces, camelCase and letter/digit steps;
    /// <c>btn</c> → <c>button</c>); tokens pair one-to-one, exact first, then by PREFIX (the shorter at least 3 characters,
    /// never digits); score = matched / union; a name is offered only at score ≥ 0.5 AND with at least one matched pair
    /// that is not a role word (<c>tab</c>, <c>button</c>, <c>panel</c>, …) — a shared "tab" alone is never enough — and never
    /// one that sounds like money, deleting, resetting or the account (<see cref="PressGuard.RiskyRoot"/>) or brings in a
    /// spend noun the wanted name did not have (<see cref="Unsafe"/>).
    ///
    /// WHERE IT RUNS: the website is the one that offers the fix — it grades the shot it SENT against the names the
    /// facts carry (invariant 100), so an editor on an older kit gets it too. Here it only adds a LOG line to a failed
    /// press (<see cref="LogLine"/>) — the director's own sentence (its <c>reason</c>) is never changed. Pure: no Unity
    /// API, never throws.
    /// </summary>
    public static class NearName
    {
        public const int MinPrefix = 3;
        /// <summary>Offered at matched / union ≥ 1/2 — compared as integers (2 × matched ≥ union).</summary>
        public const double Threshold = 0.5;
        public const int Max = 3;

        public static readonly IReadOnlyDictionary<string, string> Synonyms = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["btn"] = "button", ["bttn"] = "button", ["btns"] = "buttons",
        };

        public static readonly IReadOnlyCollection<string> RoleTokens = new HashSet<string>(StringComparer.Ordinal)
        {
            "tab", "tabs", "button", "buttons", "toggle", "toggles", "panel", "panels", "screen", "screens", "popup", "popups",
            "window", "windows", "menu", "menus", "view", "views", "page", "pages", "ui", "icon", "icons", "bg",
        };

        /// <summary>Rule (4b): a near name that INTRODUCES one of these (a word starting with it that no word of the wanted name
        /// starts with) spends something the wanted press did not — never offered.</summary>
        public static readonly IReadOnlyList<string> SpendNouns = new[]
        {
            "gem", "coin", "diamond", "gold", "cash", "currency", "crystal", "topaz", "ruby", "credit", "premium", "wallet",
        };

        /// <summary>Rule (4b) — would pressing this near name, instead of the wanted one, spend or destroy something? A
        /// <see cref="PressGuard.RiskyRoot"/> word (the fence the kit enforces at every gated press) in its selector or
        /// label, or a spend noun it introduces. Held to the website's <c>unsafeNearName</c> by near-name.cases.json.</summary>
        public static bool Unsafe(string wanted, Row row)
        {
            var text = row.Selector + " " + (row.Label ?? "");
            if (PressGuard.RiskyRoot(text) != null) return true;
            var had = PressGuard.Words(wanted ?? "").ToList();
            return PressGuard.Words(text).Any(w => SpendNouns.Any(n =>
                w.StartsWith(n, StringComparison.Ordinal) && !had.Any(h => h.StartsWith(n, StringComparison.Ordinal))));
        }

        /// <summary><see cref="ReflectionCheatBridge.NotReachedMarker"/>, spelled once.</summary>
        public const string NotReached = ReflectionCheatBridge.NotReachedMarker;

        private static readonly Regex IndexSuffix = new(@" #(\d+) \(unstable — index order is not guaranteed\)$");

        private enum Cls { Upper, Lower, Digit, Other }

        private static Cls ClassOf(char c) =>
            char.IsDigit(c) ? Cls.Digit : char.IsUpper(c) ? Cls.Upper : char.IsLetter(c) ? Cls.Lower : Cls.Other;

        /// <summary>A name's word tokens, in order, repeats dropped (the fixture's rule (1)).</summary>
        public static List<string> Tokens(string? name)
        {
            var output = new List<string>();
            if (string.IsNullOrEmpty(name)) return output;
            var cur = new StringBuilder();
            Cls? prev = null;
            void Flush()
            {
                if (cur.Length > 0)
                {
                    var t = cur.ToString();
                    if (Synonyms.TryGetValue(t, out var mapped)) t = mapped;
                    if (!output.Contains(t)) output.Add(t);
                }
                cur.Clear();
            }
            for (var i = 0; i < name!.Length; i++)
            {
                var cls = ClassOf(name[i]);
                if (cls == Cls.Other) { Flush(); prev = null; continue; }
                if (cur.Length > 0 && prev != null)
                {
                    var next = i + 1 < name.Length ? ClassOf(name[i + 1]) : Cls.Other;
                    var boundary = (cls == Cls.Digit) != (prev == Cls.Digit)
                                   || (prev == Cls.Lower && cls == Cls.Upper)
                                   || (prev == Cls.Upper && cls == Cls.Upper && next == Cls.Lower);
                    if (boundary) Flush();
                }
                cur.Append(char.ToLowerInvariant(name[i]));
                prev = cls;
            }
            Flush();
            return output;
        }

        private static bool AllDigits(string t) => t.Length > 0 && t.All(char.IsDigit);

        private static bool PrefixPair(string x, string y)
        {
            var (s, l) = x.Length <= y.Length ? (x, y) : (y, x);
            return s != l && s.Length >= MinPrefix && !AllDigits(s) && !AllDigits(l) && l.StartsWith(s, StringComparison.Ordinal);
        }

        private static (int Matched, int Union, bool Content) Pair(IReadOnlyList<string> a, IReadOnlyList<string> b)
        {
            var usedA = new bool[a.Count];
            var usedB = new bool[b.Count];
            var matched = 0;
            var content = false;
            var passes = new Func<string, string, bool>[] { (x, y) => x == y, PrefixPair };
            foreach (var pass in passes)
                for (var i = 0; i < a.Count; i++)
                {
                    if (usedA[i]) continue;
                    for (var j = 0; j < b.Count; j++)
                    {
                        if (usedB[j] || !pass(a[i], b[j])) continue;
                        usedA[i] = usedB[j] = true;
                        matched++;
                        if (!RoleTokens.Contains(a[i]) && !RoleTokens.Contains(b[j])) content = true;
                        break;
                    }
                }
            return (matched, a.Count + b.Count - matched, content);
        }

        /// <summary>How near <paramref name="candidate"/> is to <paramref name="wanted"/>, and whether it is OFFERED.</summary>
        public static (int Matched, int Union, double Score, bool Offered) Match(string wanted, string candidate)
        {
            var p = Pair(Tokens(wanted), Tokens(candidate));
            var score = p.Union == 0 ? 0 : (double)p.Matched / p.Union;
            return (p.Matched, p.Union, score, p.Content && p.Matched > 0 && 2 * p.Matched >= p.Union);
        }

        /// <summary>One ui-dump row, read (or a bare name).</summary>
        public sealed class Row
        {
            public string Selector = "";
            public string Name = "";
            public string? Label;
            /// <summary>The Selectable's type, <c>candidate</c> for a <c>[candidate: …]</c> row, null for a bare name.</summary>
            public string? Role;
            public bool Locked;
            public bool Reaches = true;
            public bool Indexed;
        }

        /// <summary>A row as <see cref="ReflectionCheatBridge.OnScreenRows"/> prints it: <c>Selector[ #i (unstable …)]
        /// [Type|candidate: A+B][ [locked]][  "label"][  (not what this name reaches)]</c>. The type bracket is the FIRST
        /// <c> [</c> of the row.</summary>
        public static Row ParseRow(string row)
        {
            var r = new Row();
            var s = row ?? "";
            if (s.EndsWith(NotReached, StringComparison.Ordinal))
            {
                r.Reaches = false;
                s = s.Substring(0, s.Length - NotReached.Length);
            }
            var sel = s;
            string? label = null;
            var b = s.IndexOf(" [", StringComparison.Ordinal);
            var close = b >= 0 ? s.IndexOf(']', b) : -1;
            if (b >= 0 && close > b)
            {
                var type = s.Substring(b + 2, close - b - 2);
                r.Role = type.StartsWith("candidate:", StringComparison.Ordinal) ? "candidate" : type;
                sel = s.Substring(0, b);
                var rest = s.Substring(close + 1);
                if (rest.StartsWith(" [locked]", StringComparison.Ordinal))
                {
                    r.Locked = true;
                    rest = rest.Substring(" [locked]".Length);
                }
                if (rest.StartsWith("  \"", StringComparison.Ordinal) && rest.EndsWith("\"", StringComparison.Ordinal) && rest.Length >= 4)
                    label = rest.Substring(3, rest.Length - 4);
            }
            var ix = IndexSuffix.Match(sel);
            if (ix.Success)
            {
                r.Indexed = true;
                sel = sel.Substring(0, ix.Index);
            }
            sel = sel.Trim();
            var (name, selLabel) = UguiDriver.ParseSelector(sel);
            r.Selector = sel;
            r.Name = name;
            r.Label = label ?? selLabel;
            return r;
        }

        public sealed class Suggestion
        {
            public string Selector = "";
            /// <summary>Rounded to 3 places.</summary>
            public double Score;
            public bool Locked;
        }

        /// <summary>The answer: <c>present</c>, <c>locked</c> (only locked rows of that name are on screen) or
        /// <c>absent</c> with at most <see cref="Max"/> suggestions, best first — never an <see cref="Unsafe"/> one, which is
        /// only counted. <paramref name="clickable"/>: a pressed name (a real Selectable ranks before a candidate or a bare
        /// row).</summary>
        public static (string State, List<Suggestion> Suggestions, int Unsafe) Suggest(string wanted, IEnumerable<Row> rows, bool clickable)
        {
            var (tName, tLabel) = UguiDriver.ParseSelector(wanted ?? "");
            var list = (rows ?? Array.Empty<Row>()).Where(r => r != null).ToList();
            var same = list.Where(r => string.Equals(r.Name, tName, StringComparison.OrdinalIgnoreCase)
                                       && (tLabel == null || (r.Label != null && UguiDriver.LabelMatches(r.Label, tLabel)))).ToList();
            if (same.Count > 0)
                return (same.All(r => r.Locked) ? "locked" : "present", new List<Suggestion>(), 0);

            var wantedTokens = Tokens(tName);
            bool LabelHit(Row r) =>
                r.Label != null && (tLabel != null ? UguiDriver.LabelMatches(r.Label, tLabel) : Pair(wantedTokens, Tokens(r.Label)).Content);

            var scored = new List<(Row R, int M, int U, int Lock, int Role, int Label)>();
            var unsafeRows = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in list)
            {
                if (!r.Reaches || r.Indexed || string.IsNullOrEmpty(r.Name)) continue;
                var m = Match(tName, r.Name);
                if (!m.Offered) continue;
                if (Unsafe(wanted ?? "", r)) { unsafeRows.Add(r.Selector); continue; }
                scored.Add((r, m.Matched, m.Union, r.Locked ? 1 : 0,
                    clickable && !(r.Role != null && r.Role != "candidate") ? 1 : 0, LabelHit(r) ? 0 : 1));
            }
            scored.Sort((x, y) =>
            {
                var c = (y.M * x.U).CompareTo(x.M * y.U);
                if (c != 0) return c;
                if ((c = x.Lock.CompareTo(y.Lock)) != 0) return c;
                if ((c = x.Role.CompareTo(y.Role)) != 0) return c;
                if ((c = x.Label.CompareTo(y.Label)) != 0) return c;
                return string.CompareOrdinal(x.R.Selector, y.R.Selector);
            });
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var output = new List<Suggestion>();
            foreach (var s in scored)
            {
                if (!seen.Add(s.R.Selector)) continue;
                output.Add(new Suggestion
                {
                    Selector = s.R.Selector,
                    Score = Math.Round((double)s.M / s.U, 3, MidpointRounding.AwayFromZero),
                    Locked = s.R.Locked,
                });
                if (output.Count == Max) break;
            }
            return ("absent", output, unsafeRows.Count);
        }

        /// <summary>
        /// The director's log line for a failed press whose name is not among <paramref name="onScreenRows"/>: the near
        /// names, or null when the name IS on screen, when nothing is near, or when there are no rows. A LOG line only —
        /// the failure's sentence is unchanged (the website reads that verbatim).
        /// </summary>
        public static string? LogLine(string wanted, IReadOnlyList<string>? onScreenRows)
        {
            if (string.IsNullOrEmpty(wanted) || onScreenRows == null || onScreenRows.Count == 0) return null;
            var (state, suggestions, _) = Suggest(wanted, onScreenRows.Select(ParseRow), clickable: true);
            if (state != "absent" || suggestions.Count == 0) return null;
            return $"  near names on screen for '{wanted}': " + string.Join(", ",
                suggestions.Select(s => $"'{s.Selector}' ({s.Score.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}{(s.Locked ? ", locked" : "")})"));
        }
    }
}
