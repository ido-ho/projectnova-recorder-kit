using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Learn-and-drive v3 P3 (§3.3 step 2) — A CHEAT'S CHECK VALUE: what to read before and after one run of it, chosen
    /// at TICK TIME from its kind, by the SENDER (the website — invariant 100: the evidence is chosen before the run,
    /// never taken from whatever happened to change). Exactly one of:
    /// <list type="bullet">
    /// <item><c>get</c> — a dotted path into the game's data (<c>Save.Player.Levels.Count</c>) and what it should do
    /// (<c>increase</c> / <c>decrease</c> / <c>change</c>). Reading it runs getters, so it is itself a lever:
    /// <c>get &lt;path&gt;</c>, ticked with the cheat (<see cref="Lever"/>);</item>
    /// <item><c>text</c> — a label that should be on screen after the run and not before ("Victory");</item>
    /// <item><c>screen</c> — a question a person (or a screen check) answers about the screen ("is there one more
    /// sword?") — the kit reads the labels before and after as evidence, and proves nothing by itself. Both read the
    /// labels with <see cref="UiText"/>, whose lever is <c>ui-text</c> — ticked with the cheat, exactly as the
    /// <c>ui-text</c> verb is gated (one rule: a label read runs the game's text getters);</item>
    /// <item><c>snapshot</c> — a root in the player's data whose numbers are ALL read before and after: FINDING the value
    /// to watch, never proving it (<see cref="SnapshotRead"/>). Its lever is <c>snapshot &lt;root&gt;</c>.</item>
    /// </list>
    /// ONE reader, used by the tick list (<see cref="TickList"/>) and by the proof job's claim, held to the website's by
    /// <c>Editor/Tests/Fixtures/tick-list.cases.json</c>.
    /// </summary>
    public sealed class CheatCheck
    {
        public const int MaxPathLength = 200;
        public const int MaxTextLength = 120;
        public static readonly IReadOnlyList<string> Expects = new[] { "increase", "decrease", "change" };
        /// <summary>Kit 0.13.4: the expectation that names a VALUE (<see cref="Value"/>) — a set-to cheat ("Give me 10k
        /// topaz" answered 10000 however many times it ran) is proven by the value it BECOMES, never by a direction.</summary>
        public const string Becomes = "becomes";
        /// <summary>A <see cref="Becomes"/> value is finite and at most this either way (the website's CHECK_MAX_VALUE).</summary>
        public const double MaxValue = 1e15;
        /// <summary>Kit 0.13.4: two numbers are the SAME value when equal or within this fraction of the larger magnitude
        /// (a float read back as 0.30000000000000004 is 0.3) — relative, so zero is only zero. The website's
        /// <c>sameValue</c>, held together by <c>Editor/Tests/Fixtures/proof-becomes.cases.json</c>.</summary>
        public const double RelTolerance = 1e-6;

        public static bool SameValue(double a, double b) =>
            a == b || Math.Abs(a - b) <= RelTolerance * Math.Max(Math.Abs(a), Math.Abs(b));

        public string? Get { get; private set; }
        public string? Expect { get; private set; }
        public string? Text { get; private set; }
        public string? Screen { get; private set; }
        public string? Snapshot { get; private set; }
        /// <summary>Kit 0.13.2: the one path under <see cref="Snapshot"/> this check is graded by (with <see cref="Expect"/>),
        /// named before the run; its read is the snapshot's own lever — no `get` tick.</summary>
        public string? Watch { get; private set; }
        /// <summary>Kit 0.13.4: what the value should BE after the cheat, when <see cref="Expect"/> is <see cref="Becomes"/>.</summary>
        public double? Value { get; private set; }

        /// <summary>
        /// The lever the check's read needs — never null: a <c>get</c>, a snapshot and a label read (<c>ui-text</c>) all
        /// run the game's code (getters; a TMP or Text subclass's <c>text</c>), so each is ticked with the cheat. The label
        /// read is the <c>ui-text</c> verb's own lever, gated the same way (the P3 review, M4: the proof read labels
        /// ungated while the verb was gated — two rules for one read).
        /// </summary>
        public string Lever => Get != null ? GetLever(Get) : Snapshot != null ? SnapshotLever(Snapshot) : UiText.Verb;

        public static string GetLever(string path) => "get " + path;
        public static string SnapshotLever(string root) => SnapshotRead.Verb + " " + root;

        /// <summary>Null when <paramref name="t"/> is not a check; <paramref name="why"/> says why (null for a missing one).</summary>
        public static CheatCheck? FromJson(JToken? t, out string? why)
        {
            why = null;
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t is not JObject o) { why = "a check is an object"; return null; }
            foreach (var p in o.Properties())
                if (p.Name != "get" && p.Name != "expect" && p.Name != "text" && p.Name != "screen" && p.Name != "snapshot" && p.Name != "watch" && p.Name != "value")
                {
                    why = $"a check has the key '{p.Name}', which this kit does not know";
                    return null;
                }
            var kinds = new[] { "get", "text", "screen", "snapshot" }.Where(k => o[k] != null).ToList();
            if (kinds.Count != 1) { why = "a check says exactly one of get, text, screen or snapshot"; return null; }
            var c = new CheatCheck();
            var kind = kinds[0];
            if (o[kind]!.Type != JTokenType.String) { why = $"a check's {kind} is a string"; return null; }
            var value = o[kind]!.Value<string>() ?? "";
            if (kind == "get" || kind == "snapshot")
            {
                if (!DataPath.IsPath(value))
                {
                    why = $"a check's {kind} is a data path (Type.member, then .member or [key]), at most {MaxPathLength} characters";
                    return null;
                }
            }
            else if (value.Trim().Length == 0 || value.Length > MaxTextLength || Levers.UnsendableCharacter(value) != null)
            {
                why = $"a check's {kind} is 1–{MaxTextLength} plain characters";
                return null;
            }
            // kit 0.13.2: a snapshot may WATCH one path under its root, named before the run — graded from that run's reads
            if (o["watch"] != null)
            {
                if (kind != "snapshot") { why = "only a snapshot check watches a path"; return null; }
                var watch = o["watch"]!.Type == JTokenType.String ? o["watch"]!.Value<string>() : null;
                if (watch == null || !DataPath.IsPath(watch) || !(watch.StartsWith(value + ".", StringComparison.Ordinal) || watch.StartsWith(value + "[", StringComparison.Ordinal)))
                {
                    why = "a check's watch is a data path under the snapshot's root";
                    return null;
                }
                if (o["expect"] == null) { why = "a watched snapshot says what it expects (increase, decrease, change, or becomes a value)"; return null; }
                c.Watch = watch;
            }
            var expectText = o["expect"]?.Type == JTokenType.String ? o["expect"]!.Value<string>() : null;
            if (o["value"] != null && expectText != Becomes) { why = $"only a check that expects \"{Becomes}\" names a value"; return null; }
            if (o["expect"] != null)
            {
                if (kind != "get" && c.Watch == null) { why = "only a get check or a watched snapshot says what it expects"; return null; }
                if (expectText == Becomes)
                {
                    // kit 0.13.4: the value it becomes, chosen at tick time — a JSON number, finite, bounded
                    var vt = o["value"];
                    if (vt == null) { why = $"a check that expects \"{Becomes}\" names the value it becomes"; return null; }
                    if (vt.Type != JTokenType.Integer && vt.Type != JTokenType.Float) { why = $"a check's value is a number, at most {MaxValue} either way"; return null; }
                    double n;
                    try { n = vt.Value<double>(); }
                    catch (Exception) { why = $"a check's value is a number, at most {MaxValue} either way"; return null; }
                    if (double.IsNaN(n) || double.IsInfinity(n) || Math.Abs(n) > MaxValue) { why = $"a check's value is a number, at most {MaxValue} either way"; return null; }
                    c.Value = n;
                }
                else if (expectText == null || !Expects.Contains(expectText))
                {
                    why = $"a check's expect is one of {string.Join(", ", Expects)} or {Becomes}";
                    return null;
                }
                c.Expect = expectText;
            }
            else if (kind == "get") { why = "a get check says what it expects (increase, decrease, change, or becomes a value)"; return null; }
            switch (kind)
            {
                case "get": c.Get = value; break;
                case "text": c.Text = value; break;
                case "screen": c.Screen = value; break;
                default: c.Snapshot = value; break;
            }
            return c;
        }

        public JObject ToJson()
        {
            var o = new JObject();
            if (Get != null) { o["get"] = Get; o["expect"] = Expect; if (Value != null) o["value"] = ValueToken(Value.Value); }
            if (Text != null) o["text"] = Text;
            if (Screen != null) o["screen"] = Screen;
            if (Snapshot != null)
            {
                o["snapshot"] = Snapshot;
                if (Watch != null) { o["watch"] = Watch; o["expect"] = Expect; if (Value != null) o["value"] = ValueToken(Value.Value); }
            }
            return o;
        }

        /// <summary>A whole value is written as an integer (10000, never 10000.0) — the check echoed in the facts reads as
        /// the website sent it.</summary>
        private static JToken ValueToken(double v) =>
            Math.Abs(v) < 9e15 && Math.Floor(v) == v ? new JValue((long)v) : new JValue(v);

        private string Should => Expect == Becomes && Value != null ? $"become {Value.Value.ToString(CultureInfo.InvariantCulture)}" : Expect ?? "";

        public string Describe() =>
            Get != null ? $"get {Get} should {Should}" :
            Text != null ? $"\"{Text}\" on screen after" :
            Screen != null ? $"a person looks: {Screen}" :
            Watch != null ? $"{Watch} should {Should} (read in the snapshot of {Snapshot})" : $"snapshot of {Snapshot}";
    }

    /// <summary>
    /// v3 P3 (§3.3 step 2, the P0 audit's runtime label) — THE PLAIN-TEXT UI READ: every active label on screen
    /// (uGUI <c>Text</c> and TextMeshPro, reached reflectively — the kit compiles without TMP), not only the buttons
    /// <c>ui-dump</c> lists. It reads what <c>ui-dump</c> already reads (a label's text getter), nothing more. Rows are
    /// <c>ObjectName: "text"</c>, sorted, capped.
    /// </summary>
    public static class UiText
    {
        public const string Verb = "ui-text";
        public const int MaxRows = 300;
        public const int MaxChars = 120;

        public static List<string> Rows()
        {
            var rows = new SortedSet<string>(StringComparer.Ordinal);
            void Add(Component c, string? text)
            {
                if (c == null || !c.gameObject.activeInHierarchy || string.IsNullOrWhiteSpace(text)) return;
                var t = text!.Trim().Replace("\n", " ").Replace("\r", " ");
                if (t.Length > MaxChars) t = t.Substring(0, MaxChars) + "…";
                rows.Add($"{c.gameObject.name}: \"{t}\"");
            }
            foreach (var text in UnityEngine.Object.FindObjectsByType<UnityEngine.UI.Text>(FindObjectsSortMode.None))
                Add(text, text.text);
            var tmp = GameReflection.FindType("TMPro.TMP_Text") ?? GameReflection.FindType("TMP_Text");
            if (tmp != null)
            {
                var prop = tmp.GetProperty("text");
                foreach (var o in UnityEngine.Object.FindObjectsByType(tmp, FindObjectsSortMode.None))
                    if (o is Component c)
                    {
                        string? s = null;
                        try { s = prop?.GetValue(c) as string; }
                        catch (Exception) { }
                        Add(c, s);
                    }
            }
            return rows.Take(MaxRows).ToList();
        }

        /// <summary>Is <paramref name="label"/> in these rows' texts (case-insensitive, as a person reads a screen)?</summary>
        public static bool Shows(IEnumerable<string> rows, string label) =>
            rows.Any(r => r.IndexOf(label, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    /// <summary>
    /// v3 P3 (§3.3, "finding the value to watch"), rebuilt in kit 0.13.2 — THE SNAPSHOT: every number under one root of
    /// the player's data, read before and after one run; the difference PROPOSES a check value ("Currencies[topaz].Amount
    /// 50 → 10050"), it never proves one: an unrelated change (a timer, a popup's counter) would pass, so a proposal is
    /// proven only by a SECOND run that watches that one value (invariant 100). Its lever is <c>snapshot &lt;root&gt;</c>.
    ///
    /// WHAT IT READS — and only this:
    /// <list type="bullet">
    /// <item>the root, by <see cref="GameReflection.TryGetPath"/> (the one read that may run a getter — the root the
    /// person ticked);</item>
    /// <item>under it, FIELDS only — never a property getter, so the walk runs none of the game's code. An
    /// auto-property's value is its compiler-made backing field, read directly and named by the property
    /// (<c>&lt;Level&gt;k__BackingField</c> → <c>Level</c>);</item>
    /// <item>numbers, booleans (0/1) and enums (their number) as leaves — strings and everything else are not sent;</item>
    /// <item>the runtime's OWN collections (<c>System.*</c>: lists, arrays, dictionaries, sets): each one's
    /// <c>.Count</c>; a dictionary's entries by key (<c>[key]</c>, keys sorted, the first <see cref="MaxEntries"/>); a
    /// list's or array's items by index — all of them up to <see cref="MaxEntries"/>, else the last
    /// <see cref="TailEntries"/>. A game-defined collection is never enumerated (its enumerator is game code): its
    /// fields are walked like any object's.</item>
    /// </list>
    /// NEVER: a <c>UnityEngine.Object</c> (a scene object, an asset), a delegate, a pointer, a type from the runtime,
    /// Unity or Newtonsoft other than those collections, an object already walked (a cycle is walked once).
    ///
    /// BOUNDED: <see cref="MaxDepth"/> steps below the root, <see cref="MaxNodes"/> objects, <see cref="MaxLeaves"/>
    /// values, and <see cref="BudgetMs"/> of wall time — whichever comes first stops the walk and says so
    /// (<see cref="Result.Cut"/>), and a cut read's diff compares only values present in both reads (a cut at another
    /// place would invent appeared / vanished values). Every leaf's path is one a <c>get</c> can read back
    /// (<see cref="DataPath"/>); a key or a path the grammar cannot write is skipped and counted
    /// (<see cref="Result.Unaddressable"/>). It never throws: a field that throws is skipped, anything else ends the read
    /// with an error.
    /// </summary>
    public static class SnapshotRead
    {
        public const string Verb = "snapshot";
        public const int MaxDepth = 8;
        public const int MaxNodes = 20000;
        public const int MaxLeaves = 4000;
        public const int BudgetMs = 250;
        public const int MaxEntries = 64;
        public const int TailEntries = 8;
        /// <summary>The most keys of one dictionary taken (in its own order) before sorting — the audit's M2 bound.</summary>
        public const int MaxKeysScanned = 256;
        public const int MaxChanged = 50;

        /// <summary>For the tests: a shorter time budget (a cut is reachable without a huge fixture).</summary>
        internal static int? BudgetMsForTests;

        public sealed class Result
        {
            public SortedDictionary<string, double> Leaves { get; } = new(StringComparer.Ordinal);
            /// <summary>Null, or which bound stopped the walk ("its time budget of 250 ms" …).</summary>
            public string? Cut { get; set; }
            /// <summary>Values or entries skipped because their path cannot be written in the grammar (a key with a
            /// space, a path past the length cap).</summary>
            public int Unaddressable { get; set; }
            public int Nodes { get; set; }
            /// <summary>Objects not read at all because they hold a credential-named field (<see cref="HoldsACredential"/>).</summary>
            public int Credentials { get; set; }
            /// <summary>Runtime collections not read because they wrap a collection of the game's own code.</summary>
            public int GameCollections { get; set; }
            /// <summary>Null, or why this read's hashed keys used a session-only salt (<see cref="DataKeys.SaltNote"/>).</summary>
            public string? SaltNote { get; set; }
            /// <summary>The watched value, when the check named one (<see cref="TryReadWatched"/>); null with <see cref="WatchedError"/>.</summary>
            public double? Watched { get; set; }
            public string? WatchedError { get; set; }
            /// <summary>Kit 0.13.4: the watched path names an ENTRY or ITEM the data does not hold (a key not in its
            /// dictionary, an index past its list's end) — a state, not a failed read: a cheat may add it.</summary>
            public bool WatchedAbsent { get; set; }
        }

        /// <summary>The leaves under <paramref name="root"/>, or null with the error.</summary>
        public static SortedDictionary<string, double>? Read(string root, out string? error) => ReadFull(root, out error)?.Leaves;

        public static Result? ReadFull(string root, out string? error, string? watch = null)
        {
            try
            {
                if (!GameReflection.TryGetPath(root, out var value, out var err))
                {
                    error = err;
                    return null;
                }
                if (value == null)
                {
                    error = $"{root} is null — is the game in the right state (logged in, its data loaded)?";
                    return null;
                }
                error = null;
                var result = new Result();
                var walker = new Walker(result, BudgetMsForTests ?? BudgetMs);
                try { walker.Walk(root, value, 0); }
                finally { walker.Done(); }
                result.SaltNote = DataKeys.SaltNote;
                if (watch != null)
                {
                    if (result.Leaves.TryGetValue(watch, out var seen)) result.Watched = seen;
                    else if (TryReadWatched(value, root, watch, out var w, out var why, out var absent)) result.Watched = w;
                    else if (absent) result.WatchedAbsent = true;
                    else result.WatchedError = why;
                }
                return result;
            }
            catch (Exception e)
            {
                var inner = e is TargetInvocationException { InnerException: { } ie } ? ie : e;
                error = $"the snapshot of {root} stopped: {inner.GetType().Name}: {inner.Message}";
                return null;
            }
        }

        private sealed class Walker
        {
            private readonly Result _r;
            private readonly int _budgetMs;
            private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
            private readonly HashSet<object> _seen = new(SameObject.Instance);

            public Walker(Result r, int budgetMs) { _r = r; _budgetMs = budgetMs; }

            /// <summary>One keyed hasher for the whole read (the salt read once), made on the first key that needs one.</summary>
            private DataKeys.Hasher? _hasher;
            internal DataKeys.Hasher Hasher => _hasher ??= DataKeys.NewHasher();
            internal void Done() => _hasher?.Dispose();

            private bool Stopped()
            {
                if (_r.Cut != null) return true;
                if (_r.Leaves.Count >= MaxLeaves) _r.Cut = $"its cap of {MaxLeaves} values";
                else if (_r.Nodes >= MaxNodes) _r.Cut = $"its cap of {MaxNodes} objects";
                else if (_clock.ElapsedMilliseconds > _budgetMs) _r.Cut = $"its time budget of {_budgetMs} ms";
                return _r.Cut != null;
            }

            private void Leaf(string path, double n)
            {
                if (!DataPath.IsPath(path)) { _r.Unaddressable++; return; }
                if (double.IsNaN(n) || double.IsInfinity(n)) return;
                _r.Leaves[path] = n;
            }

            public void Walk(string path, object? v, int depth)
            {
                if (v == null || Stopped()) return;
                if (Number(v) is { } n) { Leaf(path, n); return; }
                if (v is string || v is UnityEngine.Object || v is Delegate || v is Type || v is MemberInfo) return;
                var t = v.GetType();
                if (t.IsPrimitive || t.IsPointer || depth >= MaxDepth) return;
                if (IsRuntimeType(t))
                {
                    // a wrapper over the game's own collection, or a collection of credential holders: nothing at all
                    if (ItemsHoldACredential(t)) { _r.Credentials++; return; }
                    if (WrapsGameCollection(v)) { _r.GameCollections++; return; }
                    if (v is System.Collections.ICollection) Collection(path, v, depth);
                    // a runtime set (HashSet<T>) is no non-generic ICollection: its count only, read off the runtime's own
                    // Count property — its items are never opened
                    else if (RuntimeCount(v) is { } c) Leaf(path + ".Count", c);
                    return;
                }
                if (HoldsACredential(t)) { _r.Credentials++; return; }
                if (!t.IsValueType && !_seen.Add(v)) return;
                _r.Nodes++;
                foreach (var (name, field) in DataFields(t))
                {
                    if (Stopped()) return;
                    object? child;
                    try { child = field.GetValue(v); }
                    catch (Exception) { continue; }
                    Walk(path + "." + name, child, depth + 1);
                }
            }

            private void Collection(string path, object v, int depth)
            {
                if (!_seen.Add(v)) return;
                _r.Nodes++;
                Leaf(path + ".Count", ((System.Collections.ICollection)v).Count);
                if (v is System.Collections.IDictionary d)
                {
                    // BOUNDED WHILE ENUMERATING (the fresh audit, M2): the budget is asked on every entry, at most
                    // MaxKeysScanned keys are taken (the dictionary's own order), and only those are sorted
                    var entries = new List<(string Key, object? Value)>();
                    foreach (System.Collections.DictionaryEntry e in d)
                    {
                        if (Stopped() || entries.Count >= MaxKeysScanned) break;
                        var key = DataKeys.PathKey(e.Key, Hasher);
                        if (key == null) { _r.Unaddressable++; continue; }
                        entries.Add((key, e.Value));
                    }
                    foreach (var (key, value) in entries.OrderBy(x => x.Key, StringComparer.Ordinal).Take(MaxEntries))
                    {
                        if (Stopped()) return;
                        Walk($"{path}[{key}]", value, depth + 1);
                    }
                }
                else if (v is System.Collections.IList l)
                {
                    var count = l.Count;
                    for (var i = count <= MaxEntries ? 0 : count - TailEntries; i < count; i++)
                    {
                        if (Stopped()) return;
                        object? item;
                        try { item = l[i]; }
                        catch (Exception) { continue; }
                        Walk($"{path}[{i.ToString(CultureInfo.InvariantCulture)}]", item, depth + 1);
                    }
                }
            }
        }

        /// <summary>A dictionary key as a sent path writes it — the key, or its salted short hash when it looks like an
        /// identifier or the grammar cannot write it (<see cref="DataKeys.PathKey"/>) — or null for a key type with no form.</summary>
        internal static string? KeyText(object? key) => DataKeys.PathKey(key);

        /// <summary>
        /// THE WATCHED VALUE, read by FIELDS from the root's value (never a getter), for a check that names it before the
        /// run: each name step is a data field (<see cref="DataFields"/>), each <c>[key]</c> an entry or item of a runtime
        /// collection (<see cref="GameReflection.TryIndex"/>, a hashed key included), <c>.Count</c> a runtime collection's
        /// count. So it is read whatever bound stopped the walk, and never through an object holding a credential.
        /// </summary>
        internal static bool TryReadWatched(object rootValue, string root, string watch, out double? value, out string why) =>
            TryReadWatched(rootValue, root, watch, out value, out why, out _);

        /// <summary>…and <paramref name="absent"/>: the read stopped at an entry or item the data does not hold (kit
        /// 0.13.4) — a whole dictionary enumerated without the key, or an index past a list's end. A bound hit, a key the
        /// grammar cannot convert, a missing FIELD or anything else is a failed read, never "absent".</summary>
        internal static bool TryReadWatched(object rootValue, string root, string watch, out double? value, out string why, out bool absent)
        {
            value = null;
            why = "";
            absent = false;
            try
            {
                if (!DataPath.TryParse(watch, out var steps, out why) || !DataPath.TryParse(root, out var rootSteps, out why)) return false;
                object? cur = rootValue;
                var walked = root;
                for (var i = rootSteps.Count; i < steps.Count; i++)
                {
                    var step = steps[i];
                    if (cur == null) { why = $"{walked} is null"; return false; }
                    var t = cur.GetType();
                    if (step.IsIndex)
                    {
                        if (!IsRuntimeType(t) || !(cur is System.Collections.IDictionary || cur is System.Collections.IList))
                        {
                            why = $"{walked} is a {t.Name}, not a dictionary, list or array";
                            return false;
                        }
                        if (ItemsHoldACredential(t)) { why = $"{walked}: a collection of credential holders is never read"; return false; }
                        var container = cur;
                        if (!GameReflection.TryIndex(cur, step.Text, out cur, out var iw))
                        {
                            why = $"{walked}[{step.Text}]: {iw}";
                            absent = IsAbsentEntry(container, step.Text);
                            return false;
                        }
                        walked += $"[{step.Text}]";
                        continue;
                    }
                    walked += "." + step.Text;
                    if (IsRuntimeType(t))
                    {
                        if (step.Text != "Count") { why = $"{walked}: a runtime value is read only by its Count"; return false; }
                        if (WrapsGameCollection(cur) || ItemsHoldACredential(t)) { why = $"{walked}: a collection the kit never opens"; return false; }
                        cur = cur is System.Collections.ICollection col ? col.Count : RuntimeCount(cur);
                        continue;
                    }
                    if (HoldsACredential(t)) { why = $"{walked}: an object holding a credential is never read"; return false; }
                    var field = DataFields(t).FirstOrDefault(f => f.Name == step.Text).Field;
                    if (field == null) { why = $"{t.FullName} has no data field '{step.Text}'"; return false; }
                    cur = field.GetValue(cur);
                }
                value = cur == null ? null : Number(cur);
                if (value == null) { why = $"{watch} is {(cur == null ? "null" : "not a number")}"; return false; }
                return true;
            }
            catch (Exception e)
            {
                var inner = e is TargetInvocationException { InnerException: { } ie } ? ie : e;
                why = $"{inner.GetType().Name}: {inner.Message}";
                return false;
            }
        }

        /// <summary>Kit 0.13.4: is <paramref name="key"/> NOT an entry of this runtime collection — checked by enumeration
        /// and bounded exactly as <see cref="GameReflection.TryIndex"/> is (a bound hit is "cannot say", so false)?</summary>
        private static bool IsAbsentEntry(object? container, string key)
        {
            try
            {
                switch (container)
                {
                    case System.Collections.IList l:
                        return int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var ix) && ix >= l.Count;
                    case System.Collections.IDictionary d:
                    {
                        if (d.Count > DataKeys.MaxHashedScan) return false;
                        var hashed = DataKeys.IsHashed(key);
                        using var hasher = hashed ? DataKeys.NewHasher() : null;
                        var clock = System.Diagnostics.Stopwatch.StartNew();
                        foreach (System.Collections.DictionaryEntry e in d)
                        {
                            if (clock.ElapsedMilliseconds > DataKeys.LookupBudgetMs) return false;
                            var raw = DataKeys.RawText(e.Key);
                            if (raw == null) return false; // a key with no text form: cannot say it is not this one
                            if (hashed ? hasher!.Hash(raw) == key : raw == key) return false;
                        }
                        return true;
                    }
                    default:
                        return false;
                }
            }
            catch (Exception) { return false; }
        }

        /// <summary>Is this a type from the runtime, Unity or a serializer — never walked by its fields (only a runtime
        /// collection is opened, by <see cref="Walker"/>)?</summary>
        internal static bool IsRuntimeType(Type t)
        {
            if (t.IsArray) return true;
            var ns = t.Namespace ?? "";
            // a whole namespace segment, never a prefix of a name: a game's `Monopoly.Save` or `UnityTanks.Models` is the
            // game's own data, not the runtime's
            foreach (var root in RuntimeNamespaces)
                if (ns == root || ns.StartsWith(root + ".", StringComparison.Ordinal))
                    return true;
            return false;
        }

        private static readonly string[] RuntimeNamespaces =
            { "System", "Unity", "UnityEngine", "UnityEditor", "Newtonsoft", "Mono", "Microsoft" };

        private static readonly Dictionary<Type, List<(string, FieldInfo)>> FieldsByType = new();

        /// <summary>The count of a runtime collection that is no non-generic <c>ICollection</c> (a <c>HashSet&lt;T&gt;</c>),
        /// read off its public <c>Count</c> property — the runtime's code, never the game's — or null.</summary>
        internal static int? RuntimeCount(object v)
        {
            try
            {
                var t = v.GetType();
                if (!t.GetInterfaces().Any(i => i.IsGenericType && (i.GetGenericTypeDefinition() == typeof(ICollection<>)
                        || i.GetGenericTypeDefinition() == typeof(IReadOnlyCollection<>))))
                    return null;
                return t.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance)?.GetValue(v) is int c ? c : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// A field's name NAMES A CREDENTIAL when one of its WORDS (split at case changes, digits and underscores — so
        /// <c>Pinned</c> is not a PIN) is one of <see cref="CredentialWords"/>, or its letters joined hold one of
        /// <see cref="CredentialJoined"/> (<c>SessionKey</c>, <c>api_key</c>). An object holding such a field is not read
        /// AT ALL — not the field (a string is never sent anyway), not its counts, not its other numbers.
        /// </summary>
        private static readonly string[] CredentialWords =
            { "token", "tokens", "pin", "otp", "jwt", "cookie", "cookies", "secret", "secrets", "password", "passwords", "passwd", "pwd", "passcode", "credential", "credentials" };
        private static readonly string[] CredentialJoined =
            { "apikey", "privatekey", "authkey", "sessionkey", "secretkey", "accesstoken", "refreshtoken", "idtoken", "authtoken" };
        private static readonly Dictionary<Type, bool> CredentialByType = new();

        internal static bool NamesACredential(string fieldName)
        {
            var name = MemberNameOf(fieldName) ?? fieldName;
            var words = System.Text.RegularExpressions.Regex.Split(
                    System.Text.RegularExpressions.Regex.Replace(name, "([a-z0-9])([A-Z])", "$1 $2"), "[^A-Za-z]+")
                .Where(w => w.Length > 0).Select(w => w.ToLowerInvariant()).ToList();
            if (words.Any(w => CredentialWords.Contains(w))) return true;
            var joined = string.Concat(words);
            return CredentialJoined.Any(joined.Contains);
        }

        /// <summary>Does this type (or a base) declare a field whose name names a credential (<see cref="NamesACredential"/>)?</summary>
        internal static bool HoldsACredential(Type t)
        {
            lock (CredentialByType)
            {
                if (CredentialByType.TryGetValue(t, out var known)) return known;
                // a credential is a VALUE (a string, characters, bytes, a number such as a PIN) — a field named `Session`
                // that holds a model is that model, judged by its own fields
                var holds = FieldsInChain(t).Any(f => CanHoldASecret(f.FieldType) && NamesACredential(f.Name));
                CredentialByType[t] = holds;
                return holds;
            }
        }

        private static bool CanHoldASecret(Type t)
        {
            var u = Nullable.GetUnderlyingType(t) ?? t;
            return u == typeof(string) || u == typeof(char[]) || u == typeof(byte[]) || u.IsPrimitive || u == typeof(decimal)
                   || u.FullName == "System.Security.SecureString";
        }

        /// <summary>Does a collection of this type hold credential holders (a generic argument or an array's element that
        /// <see cref="HoldsACredential"/>)? Such a collection sends NOTHING — not its count, not its keys.</summary>
        internal static bool ItemsHoldACredential(Type t)
        {
            var parts = t.IsArray ? new[] { t.GetElementType()! } : t.IsGenericType ? t.GetGenericArguments() : Type.EmptyTypes;
            return parts.Any(p => !p.IsPrimitive && p != typeof(string) && !IsRuntimeType(p) && HoldsACredential(p));
        }

        /// <summary>
        /// A RUNTIME COLLECTION THAT WRAPS THE GAME'S OWN (the second fresh check, M2): a <c>ReadOnlyCollection&lt;T&gt;</c>,
        /// <c>ReadOnlyDictionary</c> or <c>Collection&lt;T&gt;</c> over a game-defined list or dictionary passes the
        /// runtime-type rule, yet its Count, indexer and enumerator call the game's code. So a runtime collection is
        /// trusted only when every collection its FIELDS hold (field reads — no code) is the runtime's too, recursively;
        /// otherwise it is not counted, indexed or opened.
        /// </summary>
        internal static bool WrapsGameCollection(object v) => WrapsGame(v, new HashSet<object>(SameObject.Instance), 0);

        private static bool WrapsGame(object v, HashSet<object> seen, int depth)
        {
            var t = v.GetType();
            if (t.IsArray || !IsRuntimeType(t) || depth > 4 || !seen.Add(v)) return false;
            foreach (var f in FieldsInChain(t))
            {
                if (f.FieldType.IsPrimitive || f.FieldType == typeof(string) || f.FieldType.IsPointer) continue;
                object? inner;
                try { inner = f.GetValue(v); }
                catch (Exception) { continue; }
                if (inner is not IEnumerable || inner is string) continue;
                if (!IsRuntimeType(inner.GetType())) return true;
                if (WrapsGame(inner, seen, depth + 1)) return true;
            }
            return false;
        }

        /// <summary>Every instance field of a type and of each base class (DeclaredOnly per level, so a base's PRIVATE fields
        /// and auto-property backing fields are seen — the fresh audit, M3), most-derived first.</summary>
        internal static IEnumerable<FieldInfo> FieldsInChain(Type t)
        {
            const BindingFlags declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (var at = t; at != null && at != typeof(object) && at != typeof(ValueType); at = at.BaseType)
                foreach (var f in at.GetFields(declared))
                    yield return f;
        }

        /// <summary>A type's instance fields as a snapshot names them — its own and every base class's (a hidden base field:
        /// the most-derived one, once) — sorted, an auto-property's backing field named by its property, other compiler-made
        /// fields and fields holding a Unity object or a delegate left out.</summary>
        internal static List<(string Name, FieldInfo Field)> DataFields(Type t)
        {
            lock (FieldsByType)
            {
                if (FieldsByType.TryGetValue(t, out var cached)) return cached;
                var list = new List<(string, FieldInfo)>();
                foreach (var f in FieldsInChain(t))
                {
                    var name = MemberNameOf(f.Name);
                    if (name == null) continue;
                    var ft = f.FieldType;
                    if (typeof(UnityEngine.Object).IsAssignableFrom(ft) || typeof(Delegate).IsAssignableFrom(ft) || ft.IsPointer) continue;
                    if (list.Any(x => x.Item1 == name)) continue;
                    list.Add((name, f));
                }
                list.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
                FieldsByType[t] = list;
                return list;
            }
        }

        /// <summary>A field's name as a path step: itself, an auto-property's name for its backing field, else null.</summary>
        internal static string? MemberNameOf(string fieldName)
        {
            const string backing = ">k__BackingField";
            if (fieldName.StartsWith("<", StringComparison.Ordinal) && fieldName.EndsWith(backing, StringComparison.Ordinal))
            {
                var name = fieldName.Substring(1, fieldName.Length - 1 - backing.Length);
                return DataPath.IsName(name) ? name : null;
            }
            return DataPath.IsName(fieldName) ? fieldName : null;
        }

        internal static double? Number(object v) => v switch
        {
            bool b => b ? 1 : 0,
            Enum e => Convert.ToDouble(e, CultureInfo.InvariantCulture),
            byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal
                => Convert.ToDouble(v, CultureInfo.InvariantCulture),
            _ => null,
        };

        /// <summary>Object identity for the walk (a cycle in the player's data is walked once).</summary>
        private sealed class SameObject : IEqualityComparer<object>
        {
            public static readonly SameObject Instance = new();
            public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }

        /// <summary>
        /// The leaves that changed, RANKED so the likely one survives the cap: a value present in both reads first (a
        /// number that moved — what every check watches), then the biggest move, then by path; a value that appeared or
        /// vanished after those. When either read was CUT (<paramref name="anyCut"/>), only values present in both are
        /// compared. The whole list — the caller caps it (<see cref="MaxChanged"/>) and says how many there were.
        /// </summary>
        public static List<(string Path, double? Before, double? After)> Diff(
            IReadOnlyDictionary<string, double> before, IReadOnlyDictionary<string, double> after, bool anyCut = false)
        {
            var changed = new List<(string Path, double? Before, double? After)>();
            foreach (var path in before.Keys.Union(after.Keys))
            {
                double? b = before.TryGetValue(path, out var x) ? x : null;
                double? a = after.TryGetValue(path, out var y) ? y : null;
                if (anyCut && (b == null || a == null)) continue;
                if (b != a) changed.Add((path, b, a));
            }
            return changed
                .OrderBy(c => c.Before != null && c.After != null ? 0 : 1)
                .ThenByDescending(c => c.Before != null && c.After != null ? Math.Abs(c.After!.Value - c.Before!.Value) : 0)
                .ThenBy(c => c.Path, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary>What the <c>cheat-proof</c> claim carries: the ticked cheat, its check, the settle wait. The server's
    /// snapshot of the tick (invariant 100); the kit only asks its own gate whether both are ticked here.</summary>
    public sealed class CheatProofRequest
    {
        /// <summary>The longest the proof waits for the cheat's effect (kit 0.13.4: 12, the website's default — Rogue
        /// Legend's server-backed cheats answered after 3 s). The proof polls, so a quick effect ends the wait early.</summary>
        public const double DefaultSettleSec = 12;
        public const double MaxSettleSec = 60;

        public string Command { get; }
        public CheatCheck Check { get; }
        public double SettleSec { get; }

        public CheatProofRequest(string command, CheatCheck check, double settleSec)
        {
            Command = command;
            Check = check;
            SettleSec = Math.Clamp(settleSec, 0, MaxSettleSec);
        }

        public const string MissingReason =
            "this cheat proof arrived without its cheat and check (no complete `cheatProof` block on the claim) — nothing was run";

        /// <summary>The <c>cheatProof</c> block of a claim reply, or null.</summary>
        public static CheatProofRequest? FromClaim(string claimBody)
        {
            try { return FromJson(NovaJson.ParseObject(claimBody)["cheatProof"]); }
            catch (Exception) { return null; }
        }

        public static CheatProofRequest? FromJson(JToken? t)
        {
            if (t is not JObject o) return null;
            var command = o["command"]?.Type == JTokenType.String ? o["command"]!.Value<string>() : null;
            if (string.IsNullOrWhiteSpace(command) || command!.Length > Levers.MaxLeverLength) return null;
            var check = CheatCheck.FromJson(o["check"], out _);
            if (check == null) return null;
            var settle = o["settleSec"] is { } s && (s.Type == JTokenType.Integer || s.Type == JTokenType.Float)
                ? s.Value<double>() : DefaultSettleSec;
            return new CheatProofRequest(command, check, settle);
        }

        public JObject ToJson() => new() { ["command"] = Command, ["check"] = Check.ToJson(), ["settleSec"] = SettleSec };
    }

    /// <summary>
    /// v3 P3 (§3.3 step 3) — THE PROOF RUN: the ticked cheat run ONCE, its check read before and after it, the after read
    /// taken once the settle wait is over (an effect that comes back late from the game's server). FACTS only: the
    /// website decides "proven" — only when the value moved the way the check said, never on the call's own "ok"
    /// (invariant 50) — and "no change yet" after the wait is "not yet proven", never "broken".
    ///
    /// Everything that touches the game goes through the kit job's gate (<see cref="KitJobRun.Gate"/>): the cheat, the
    /// <c>get</c>, the snapshot, and the label read (<see cref="UiText"/>, lever <c>ui-text</c>). A before-read that failed
    /// or was refused STOPS the proof before the cheat: with nothing to grade against, running it would only change the
    /// studio's game for no answer.
    /// </summary>
    public sealed class CheatProofRun
    {
        public sealed class Reading
        {
            public string? Value { get; set; }
            public double? Number { get; set; }
            public List<string>? Labels { get; set; }
            public SortedDictionary<string, double>? Leaves { get; set; }
            /// <summary>A snapshot's read only: which bound stopped it, or null (<see cref="SnapshotRead.Result.Cut"/>).</summary>
            public string? Cut { get; set; }
            /// <summary>A snapshot's read only: values skipped because their path cannot be written.</summary>
            public int Unaddressable { get; set; }
            /// <summary>A watched snapshot's read only: the watched path's value.</summary>
            public double? Watched { get; set; }
            /// <summary>Kit 0.13.4: the watched path names an entry the data does not hold (<see cref="SnapshotRead.Result.WatchedAbsent"/>).</summary>
            public bool WatchedAbsent { get; set; }
            public string? Error { get; set; }
            /// <summary>The read was refused by the gate (a tick taken away) — not a failed read of the game.</summary>
            internal bool Refused { get; set; }

            public JObject ToJson()
            {
                var o = new JObject { ["error"] = Error == null ? JValue.CreateNull() : Error };
                if (Value != null || Number != null)
                {
                    o["value"] = Value == null ? JValue.CreateNull() : Value;
                    o["number"] = Number == null ? JValue.CreateNull() : Number.Value;
                }
                if (Labels != null) o["labels"] = new JArray(Labels);
                if (Leaves != null)
                {
                    o["leaves"] = Leaves.Count;
                    o["cut"] = Cut == null ? JValue.CreateNull() : Cut;
                    o["unaddressable"] = Unaddressable;
                }
                return o;
            }
        }

        public CheatProofRequest Request { get; }
        public string? LeverRefused { get; private set; }
        public bool Ran { get; private set; }
        public bool RunOk { get; private set; }
        public Reading? Before { get; private set; }
        public Reading? After { get; private set; }
        public double SettledSec { get; private set; }
        /// <summary>Kit 0.13.4: how many after-reads the poll took.</summary>
        public int Reads { get; private set; }
        /// <summary>Kit 0.13.4: why the poll stopped — <c>moved</c> (the check is met), <c>appeared</c> (the label is on
        /// screen), <c>stable</c> (a screen check's labels changed and held for one more read), <c>refused</c> (a read was
        /// refused mid-wait) or <c>timeout</c> (the settle wait ran out; then one last read is taken).</summary>
        public string StoppedBy { get; private set; } = "timeout";
        public List<string> Log { get; } = new();

        /// <summary>Kit 0.13.4: the after-reads are taken this often until the check is met or the wait runs out.</summary>
        public const double PollEverySec = 1.0;

        public CheatProofRun(CheatProofRequest request) => Request = request;

        /// <summary>Not run: a lever of it was refused before Play Mode (<see cref="LeverRefusalNow"/>).</summary>
        public void RefuseBeforeRunning(string refusal) => LeverRefused = refusal;

        /// <summary>
        /// Would the gate let this proof run NOW — the cheat and its check's lever, both? Asked before Play Mode, with the
        /// gate's own function and a kit bridge that runs NOTHING; a refused one is remembered by the gate, so it is a row
        /// to tick. Null = both pass; otherwise the first refusal's sentence.
        /// </summary>
        public static string? LeverRefusalNow(string projectRoot, CheatProofRequest request)
        {
            var log = new List<string>();
            var gate = KitJobRun.Gate(new KitFunctionBridge(_ => true), projectRoot, log.Add);
            foreach (var lever in new[] { request.Check.Lever, request.Command })
                if (lever != null && !gate.Run(lever))
                    return log.LastOrDefault() ?? Levers.NotApprovedLog(lever);
            return null;
        }

        /// <summary>The whole proof, as the agent pumps it in Play Mode, pure over <paramref name="now"/>.</summary>
        public static IEnumerable Run(CheatProofRun run, string projectRoot, ICheatBridge gameCheats, Func<double> now)
        {
            var refused = LeverRefusalNow(projectRoot, run.Request);
            if (refused != null)
            {
                run.LeverRefused = refused;
                yield break;
            }
            run.Before = Read(run, projectRoot);
            if (run.Before.Error != null)
            {
                // nothing to grade against: the cheat is NOT run, and the game is left as it was
                run.Log.Add($"the cheat was not run: the check could not be read before it ({run.Before.Error})");
                yield break;
            }
            yield return null;
            var cheat = KitJobRun.Gate(gameCheats, projectRoot, run.Log.Add);
            run.Ran = true;
            try
            {
                run.RunOk = cheat.Run(run.Request.Command);
            }
            catch (Exception e)
            {
                run.RunOk = false;
                run.Log.Add($"the cheat threw: {e.GetType().Name}: {e.Message}");
            }
            if (cheat.LastRunRefused)
            {
                // ticked a moment ago, refused now (a tick taken away): nothing ran
                run.Ran = false;
                run.LeverRefused = run.Log.LastOrDefault();
                yield break;
            }
            // kit 0.13.4 — POLL, not one fixed wait (Rogue Legend 2026-09-27: "Give me 10k topaz" and "Reset user"
            // answered from the game's server after the old 3 s read). Every PollEverySec until the check is met (or a
            // snapshot's non-noise change held), else to the end of the settle wait. Each read is the same gated,
            // bounded, field-only read as before; nothing here runs the game's code.
            var started = now();
            Reading? prev = null, last = null;
            var nextRead = started + PollEverySec;
            run.StoppedBy = "timeout";
            while (now() - started < run.Request.SettleSec)
            {
                if (now() >= nextRead)
                {
                    prev = last;
                    last = Read(run, projectRoot);
                    run.Reads++;
                    nextRead = now() + PollEverySec;
                    var why = last.Refused ? "refused" : Settled(run.Request.Check, run.Before, prev, last);
                    if (why != null)
                    {
                        run.StoppedBy = why;
                        break;
                    }
                }
                yield return null;
            }
            run.SettledSec = now() - started;
            if (run.StoppedBy == "timeout")
            {
                last = Read(run, projectRoot);
                run.Reads++;
            }
            run.After = last;
        }

        /// <summary>
        /// Kit 0.13.4 — IS THE WAIT OVER after this read? Null = read again. A <c>get</c> or a watched snapshot: the value
        /// did what the check expects (<c>moved</c>; a watched ENTRY the data did not hold before counts when it now
        /// meets the expectation — above zero for <c>increase</c>, present for <c>change</c>, the value for
        /// <c>becomes</c>). A text check: the label is on screen now and was not before (<c>appeared</c>). A screen
        /// check: its labels changed and held (<c>stable</c>). A PLAIN snapshot never ends early (the fresh audit, M1): it
        /// is a one-off discovery run, and a local change that held would end it before a server's later answer ("Reset
        /// user") — it waits the whole settle time, then reads once more. A read that failed never ends the wait. Pure.
        /// </summary>
        internal static string? Settled(CheatCheck check, Reading? before, Reading? prev, Reading last)
        {
            if (before == null || last.Error != null) return null;
            if (check.Get != null) return Met(check, before.Number, last.Number, before.Value, last.Value, false) ? "moved" : null;
            if (check.Watch != null) return Met(check, before.Watched, last.Watched, null, null, before.WatchedAbsent) ? "moved" : null;
            if (check.Text != null)
                return !UiText.Shows(before.Labels ?? new List<string>(), check.Text) && UiText.Shows(last.Labels ?? new List<string>(), check.Text) ? "appeared" : null;
            if (check.Screen != null)
            {
                if (prev == null || prev.Error != null) return null;
                var b = before.Labels ?? new List<string>();
                var p = prev.Labels ?? new List<string>();
                return !p.SequenceEqual(b) && p.SequenceEqual(last.Labels ?? new List<string>()) ? "stable" : null;
            }
            return null;
        }

        private static bool Met(CheatCheck check, double? b, double? a, string? bText, string? aText, bool absentBefore)
        {
            if (check.Expect == CheatCheck.Becomes) return a != null && check.Value != null && CheatCheck.SameValue(a.Value, check.Value.Value);
            if (absentBefore && b == null)
                return a != null && check.Expect switch { "increase" => a > 0, "change" => true, _ => false };
            if (b == null || a == null) return check.Expect == "change" && bText != aText;
            return check.Expect switch
            {
                "increase" => a > b,
                "decrease" => a < b,
                _ => a != b,
            };
        }

        private static Reading Read(CheatProofRun run, string projectRoot)
        {
            var check = run.Request.Check;
            var reading = new Reading();
            var gate = KitJobRun.Gate(new KitFunctionBridge(_ =>
            {
                if (check.Text != null || check.Screen != null)
                {
                    try { reading.Labels = UiText.Rows(); }
                    catch (Exception e) { reading.Error = $"the labels on screen could not be read: {e.Message}"; return false; }
                    return true;
                }
                if (check.Get != null)
                {
                    if (!GameReflection.TryGetPath(check.Get, out var v, out var err)) { reading.Error = err; return false; }
                    reading.Value = GameReflection.Render(v, 200);
                    reading.Number = v == null ? null : SnapshotRead.Number(v);
                    return true;
                }
                var snap = SnapshotRead.ReadFull(check.Snapshot!, out var serr, check.Watch);
                reading.Error = serr;
                if (snap == null) return false;
                reading.Leaves = snap.Leaves;
                reading.Cut = snap.Cut;
                reading.Unaddressable = snap.Unaddressable;
                if (check.Watch != null)
                {
                    // the value the check named: unreadable → nothing to grade (before the cheat: the cheat is not run)
                    reading.Watched = snap.Watched;
                    reading.WatchedAbsent = snap.WatchedAbsent;
                    if (snap.Watched == null && !snap.WatchedAbsent)
                    {
                        reading.Error = $"the watched value {check.Watch} could not be read in the snapshot of {check.Snapshot}: {snap.WatchedError}";
                        return false;
                    }
                }
                return true;
            }), projectRoot, run.Log.Add);
            try
            {
                if (!gate.Run(check.Lever) && gate.LastRunRefused)
                {
                    reading.Error = run.Log.LastOrDefault() ?? Levers.NotApprovedLog(check.Lever);
                    reading.Refused = true;
                }
            }
            catch (Exception e)
            {
                reading.Error = $"{e.GetType().Name}: {e.Message}";
            }
            return reading;
        }

        public string ToFactsJson(string kitVersion)
        {
            var facts = new JObject
            {
                ["kitVersion"] = kitVersion,
                ["command"] = Request.Command,
                ["check"] = Request.Check.ToJson(),
                ["leverRefused"] = LeverRefused == null ? JValue.CreateNull() : LeverRefused,
                ["ran"] = Ran,
                ["runOk"] = RunOk,
                // the time it ACTUALLY waited (kit 0.13.4: the poll may end the wait early), the reads it took and why
                ["settleSec"] = Math.Round(SettledSec, 2),
                ["reads"] = Reads,
                ["stoppedBy"] = StoppedBy,
                ["before"] = Before == null ? JValue.CreateNull() : Before.ToJson(),
                ["after"] = After == null ? JValue.CreateNull() : After.ToJson(),
                ["log"] = new JArray(Log.Take(50)),
            };
            if (Before?.Leaves != null && After?.Leaves != null)
            {
                // ranked (a value that moved first, the biggest move first), then capped — the count says how many there were
                var diff = SnapshotRead.Diff(Before.Leaves, After.Leaves, Before.Cut != null || After.Cut != null);
                facts["changed"] = new JArray(diff.Take(SnapshotRead.MaxChanged).Select(c => new JObject
                {
                    ["path"] = c.Path,
                    ["before"] = c.Before == null ? JValue.CreateNull() : c.Before.Value,
                    ["after"] = c.After == null ? JValue.CreateNull() : c.After.Value,
                }));
                facts["changedTotal"] = diff.Count;
            }
            // the salt behind this read's hashed keys was not the project's stored one (unreadable, or not writable): said
            var usesHashes = Request.Check.Snapshot != null || (Request.Check.Get?.Contains("[#") ?? false);
            if (usesHashes && DataKeys.SaltNote is { } saltNote) facts["hashNote"] = saltNote;
            // kit 0.13.2: the path the check named, reported on its own — the 50-value cap never drops it
            if (Request.Check.Watch != null && Before != null)
                facts["watched"] = new JObject
                {
                    ["path"] = Request.Check.Watch,
                    ["before"] = Before.Watched == null ? JValue.CreateNull() : Before.Watched.Value,
                    ["after"] = After?.Watched == null ? JValue.CreateNull() : After.Watched.Value,
                    // kit 0.13.4: a null that is an entry the data does not hold — a state the website grades
                    ["absentBefore"] = Before.WatchedAbsent,
                    ["absentAfter"] = After?.WatchedAbsent ?? false,
                };
            return facts.ToString(Formatting.None);
        }
    }
}
