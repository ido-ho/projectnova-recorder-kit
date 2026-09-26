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
        private static readonly Regex PathRe = new("^[A-Za-z_][A-Za-z0-9_]*(\\.[A-Za-z_][A-Za-z0-9_]*)+$");

        public string? Get { get; private set; }
        public string? Expect { get; private set; }
        public string? Text { get; private set; }
        public string? Screen { get; private set; }
        public string? Snapshot { get; private set; }

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
                if (p.Name != "get" && p.Name != "expect" && p.Name != "text" && p.Name != "screen" && p.Name != "snapshot")
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
                if (value.Length > MaxPathLength || !PathRe.IsMatch(value))
                {
                    why = $"a check's {kind} is a dotted path of names (Type.member…), at most {MaxPathLength} characters";
                    return null;
                }
            }
            else if (value.Trim().Length == 0 || value.Length > MaxTextLength || Levers.UnsendableCharacter(value) != null)
            {
                why = $"a check's {kind} is 1–{MaxTextLength} plain characters";
                return null;
            }
            if (o["expect"] != null)
            {
                if (kind != "get") { why = "only a get check says what it expects"; return null; }
                if (o["expect"]!.Type != JTokenType.String || !Expects.Contains(o["expect"]!.Value<string>()))
                {
                    why = $"a check's expect is one of {string.Join(", ", Expects)}";
                    return null;
                }
                c.Expect = o["expect"]!.Value<string>();
            }
            else if (kind == "get") { why = "a get check says what it expects (increase, decrease or change)"; return null; }
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
            if (Get != null) { o["get"] = Get; o["expect"] = Expect; }
            if (Text != null) o["text"] = Text;
            if (Screen != null) o["screen"] = Screen;
            if (Snapshot != null) o["snapshot"] = Snapshot;
            return o;
        }

        public string Describe() =>
            Get != null ? $"get {Get} should {Expect}" :
            Text != null ? $"\"{Text}\" on screen after" :
            Screen != null ? $"a person looks: {Screen}" : $"snapshot of {Snapshot}";
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
    /// v3 P3 (§3.3, "finding the value to watch") — THE SNAPSHOT: every number under one root of the player's data —
    /// numeric fields and properties, booleans, enums, and each collection's <c>Count</c> — read to a bounded depth.
    /// Before and after one run, the difference PROPOSES a check value ("Inventory.Count 12 → 13"); it never proves one:
    /// an unrelated change (a timer, a popup's counter) would pass, so a proposal is proven only by a SECOND run that
    /// watches that one value (invariant 100). Reading runs the data's getters: the lever is <c>snapshot &lt;root&gt;</c>.
    /// Each leaf's path is one a <c>get</c> can read back.
    /// </summary>
    public static class SnapshotRead
    {
        public const string Verb = "snapshot";
        public const int MaxDepth = 4;
        public const int MaxLeaves = 400;
        public const int MaxChanged = 50;

        public static SortedDictionary<string, double>? Read(string root, out string? error)
        {
            if (!GameReflection.TryGetPath(root, out var value, out var err))
            {
                error = err;
                return null;
            }
            error = null;
            var leaves = new SortedDictionary<string, double>(StringComparer.Ordinal);
            var seen = new HashSet<object>(SameObject.Instance);
            Walk(root, value, 0, leaves, seen);
            return leaves;
        }

        private static void Walk(string path, object? v, int depth, SortedDictionary<string, double> leaves, HashSet<object> seen)
        {
            if (v == null || leaves.Count >= MaxLeaves) return;
            if (Number(v) is { } n) { leaves[path] = n; return; }
            if (v is string) return;
            if (v is ICollection col) { leaves[path + ".Count"] = col.Count; return; }
            if (depth >= MaxDepth || v is UnityEngine.Object || !seen.Add(v)) return;
            var t = v.GetType();
            if (t.IsPrimitive || t.IsPointer) return;
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var f in t.GetFields(inst).OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                if (f.Name.IndexOf('<') >= 0 || typeof(UnityEngine.Object).IsAssignableFrom(f.FieldType)) continue;
                object? child;
                try { child = f.GetValue(v); }
                catch (Exception) { continue; }
                Walk(path + "." + f.Name, child, depth + 1, leaves, seen);
            }
            foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public).OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                if (!p.CanRead || p.GetIndexParameters().Length > 0 || typeof(UnityEngine.Object).IsAssignableFrom(p.PropertyType)) continue;
                if (leaves.ContainsKey(path + "." + p.Name)) continue;
                object? child;
                try { child = p.GetValue(v); }
                catch (Exception) { continue; }
                Walk(path + "." + p.Name, child, depth + 1, leaves, seen);
            }
        }

        internal static double? Number(object v) => v switch
        {
            bool b => b ? 1 : 0,
            Enum e => Convert.ToDouble(e, CultureInfo.InvariantCulture),
            byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal
                => Convert.ToDouble(v, CultureInfo.InvariantCulture),
            _ => null,
        };

        /// <summary>The leaves that changed (or appeared / disappeared), sorted by path, capped.</summary>
        /// <summary>Object identity for the walk (a cycle in the player's data is walked once).</summary>
        private sealed class SameObject : IEqualityComparer<object>
        {
            public static readonly SameObject Instance = new();
            public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }

        public static List<(string Path, double? Before, double? After)> Diff(
            IReadOnlyDictionary<string, double> before, IReadOnlyDictionary<string, double> after)
        {
            var changed = new List<(string, double?, double?)>();
            foreach (var path in before.Keys.Union(after.Keys).OrderBy(k => k, StringComparer.Ordinal))
            {
                double? b = before.TryGetValue(path, out var x) ? x : null;
                double? a = after.TryGetValue(path, out var y) ? y : null;
                if (b != a) changed.Add((path, b, a));
                if (changed.Count >= MaxChanged) break;
            }
            return changed;
        }
    }

    /// <summary>What the <c>cheat-proof</c> claim carries: the ticked cheat, its check, the settle wait. The server's
    /// snapshot of the tick (invariant 100); the kit only asks its own gate whether both are ticked here.</summary>
    public sealed class CheatProofRequest
    {
        public const double DefaultSettleSec = 3;
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
            public string? Error { get; set; }

            public JObject ToJson()
            {
                var o = new JObject { ["error"] = Error == null ? JValue.CreateNull() : Error };
                if (Value != null || Number != null)
                {
                    o["value"] = Value == null ? JValue.CreateNull() : Value;
                    o["number"] = Number == null ? JValue.CreateNull() : Number.Value;
                }
                if (Labels != null) o["labels"] = new JArray(Labels);
                if (Leaves != null) o["leaves"] = Leaves.Count;
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
        public List<string> Log { get; } = new();

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
            var started = now();
            while (now() - started < run.Request.SettleSec) yield return null;
            run.SettledSec = now() - started;
            run.After = Read(run, projectRoot);
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
                reading.Leaves = SnapshotRead.Read(check.Snapshot!, out var serr);
                reading.Error = serr;
                return reading.Leaves != null;
            }), projectRoot, run.Log.Add);
            try
            {
                if (!gate.Run(check.Lever) && gate.LastRunRefused)
                    reading.Error = run.Log.LastOrDefault() ?? Levers.NotApprovedLog(check.Lever);
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
                ["settleSec"] = Math.Round(SettledSec, 2),
                ["before"] = Before == null ? JValue.CreateNull() : Before.ToJson(),
                ["after"] = After == null ? JValue.CreateNull() : After.ToJson(),
                ["log"] = new JArray(Log.Take(50)),
            };
            if (Before?.Leaves != null && After?.Leaves != null)
                facts["changed"] = new JArray(SnapshotRead.Diff(Before.Leaves, After.Leaves).Select(c => new JObject
                {
                    ["path"] = c.Path,
                    ["before"] = c.Before == null ? JValue.CreateNull() : c.Before.Value,
                    ["after"] = c.After == null ? JValue.CreateNull() : c.After.Value,
                }));
            return facts.ToString(Formatting.None);
        }
    }
}
