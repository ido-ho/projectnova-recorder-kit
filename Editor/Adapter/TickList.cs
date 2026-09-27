using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Learn-and-drive v3 P2 (§3.3 step 1) — THE TICK-LIST INPUT: <c>Library/Nova/tick-list.json</c>, the cheat
    /// candidates the website found (READ, and later the deep cheat search), delivered so the Nova Capture window can
    /// list them — each with its kind and risk flags — for a PERSON to tick.
    ///
    /// A DELIVERED FILE CAN ONLY PROPOSE. Nothing here writes <c>levers.json</c>, and nothing reads this file to
    /// decide whether a command may run: the gate reads the ticks (<see cref="Levers.Approved"/>) and nothing else. A
    /// candidate shows as an unticked row until the person ticks it (<see cref="Levers.Rows"/>); what it says can only
    /// RAISE a command's risk (<see cref="CheatRisk.Why"/>), never lower it. It does not make the lever gate live and is
    /// not in <c>synced.json</c>'s history: it runs nothing.
    ///
    /// Written by the <c>sync-nova</c> job when the send carries one (<see cref="SyncNova.Run"/>), checked first by
    /// <see cref="Refusal"/>; the website builds and checks it with the same rules
    /// (<c>apps/renderer/src/game-ads/learn/tick-list.ts</c>), both held to <c>Editor/Tests/Fixtures/tick-list.cases.json</c>.
    ///
    /// NOTHING HERE THROWS. A missing file is no candidates; a file this kit cannot read is no candidates and a reason.
    /// </summary>
    public static class TickList
    {
        public const string FileName = "tick-list.json";
        public const int SchemaVersion = 1;

        /// <summary>The most bytes a tick list may be: 300 candidates of a 256-character command with their labels fit
        /// with room to spare.</summary>
        public const int MaxBytes = 128 * 1024;

        /// <summary>The longest <c>source</c> label.</summary>
        public const int MaxSourceLength = 64;

        /// <summary>The kinds a candidate may carry — v3 §3.3 step 4, the website's <c>CHEAT_KINDS</c>.</summary>
        public static readonly IReadOnlyList<string> Kinds = new[]
            { "give", "level", "win-lose", "reset", "unlock", "time", "purchase", "language", "other" };

        public sealed class Candidate
        {
            public string Command { get; set; } = "";
            public string? Kind { get; set; }
            public IReadOnlyList<string> Risk { get; set; } = Array.Empty<string>();
            public string? Source { get; set; }
            /// <summary>v3 P3 (§3.3 step 2): the check value the website chose at tick time from the cheat's kind, or
            /// null. Shown beside the row; its read (<see cref="CheatCheck.Lever"/> — a get, a snapshot or <c>ui-text</c>), ticking the row ticks
            /// that read too (<see cref="Levers.CompanionTicks"/>). It is never run from here.</summary>
            public CheatCheck? Check { get; set; }
        }

        public static string FilePath(string projectRoot) =>
            Path.Combine(RelayPaths.NovaDir(projectRoot), FileName);

        /// <summary>For the tests: how many times the file has been read in this editor session (the gate reads it once per
        /// attempt, not once per write — <c>LeverGateBridge.HoldLiveness</c>). Counting only.</summary>
        internal static int ReadCallsForTests => System.Threading.Volatile.Read(ref _readCalls);
        private static int _readCalls;

        /// <summary>The candidates on disk, in file order. Missing → none. Never throws.</summary>
        public static IReadOnlyList<Candidate> Read(string projectRoot) => Read(projectRoot, out _);

        /// <summary><see cref="Read(string)"/>, also saying why an existing file was not read (null otherwise).</summary>
        public static IReadOnlyList<Candidate> Read(string projectRoot, out string? unreadable)
        {
            System.Threading.Interlocked.Increment(ref _readCalls);
            unreadable = null;
            try
            {
                var path = FilePath(projectRoot);
                if (!File.Exists(path)) return Array.Empty<Candidate>();
                var bytes = File.ReadAllBytes(path);
                if (bytes.Length > MaxBytes)
                {
                    unreadable = $"{FileName} is {bytes.Length} bytes, more than {MaxBytes}";
                    return Array.Empty<Candidate>();
                }
                unreadable = Refusal(System.Text.Encoding.UTF8.GetString(bytes), out var candidates);
                return unreadable == null ? candidates : Array.Empty<Candidate>();
            }
            catch (Exception e)
            {
                unreadable = e.Message;
                return Array.Empty<Candidate>();
            }
        }

        /// <summary>
        /// THE ONE CHECK of a tick list — for a delivery (before anything is written) and for the file on disk. Null =
        /// it may be read, and <paramref name="candidates"/> holds it; otherwise the first reason, one sentence, and no
        /// candidates at all (a whole file or nothing, like levers.json). Refused: not a JSON object; a
        /// <c>$schemaVersion</c> other than 1; a key other than <c>$schemaVersion</c> / <c>candidates</c>; no
        /// <c>candidates</c> list; more than <see cref="Levers.MaxLevers"/> candidates; a candidate that is not an
        /// object or has another key than <c>command</c> / <c>kind</c> / <c>risk</c> / <c>source</c>; a command that is not a
        /// non-empty string, is longer than <see cref="Levers.MaxLeverLength"/>, or that the window could not tick
        /// (<see cref="Levers.NotTickableReason"/> — a template among them: only fixed commands are proposed); a command
        /// listed twice; a kind not in <see cref="Kinds"/>; a risk that is not a list of <see cref="CheatRisk.RiskLabels"/>;
        /// a source that is not a string of 1–<see cref="MaxSourceLength"/> characters with nothing unsendable in it.
        /// </summary>
        public static string? Refusal(string? text, out List<Candidate> candidates)
        {
            candidates = new List<Candidate>();
            var found = new List<Candidate>();
            JObject root;
            try
            {
                if (string.IsNullOrWhiteSpace(text)) return $"{FileName} is empty";
                if (text![0] == '\uFEFF') return $"{FileName} starts with a byte-order mark";
                root = NovaJson.ParseObject(text);
            }
            catch (Exception e)
            {
                return $"{FileName} is not a JSON object ({e.Message})";
            }

            foreach (var p in root.Properties())
                if (p.Name != "$schemaVersion" && p.Name != "candidates")
                    return $"{FileName} has the key '{p.Name}', which this kit does not know";
            var version = root["$schemaVersion"];
            if (version == null || version.Type != JTokenType.Integer || !NovaJson.TryNumber(version, out var v) || v != SchemaVersion)
                return $"{FileName}: $schemaVersion is not {SchemaVersion}";
            if (root["candidates"] is not JArray list) return $"{FileName}: 'candidates' is not a list";
            if (list.Count > Levers.MaxLevers)
                return $"{FileName} proposes {list.Count} commands, and this kit lists at most {Levers.MaxLevers}";

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < list.Count; i++)
            {
                var at = $"{FileName}: candidates[{i}]";
                if (list[i] is not JObject o) return $"{at} is not an object";
                foreach (var p in o.Properties())
                    if (p.Name != "command" && p.Name != "kind" && p.Name != "risk" && p.Name != "source" && p.Name != "check")
                        return $"{at} has the key '{p.Name}', which this kit does not know";
                if (o["command"] is not { Type: JTokenType.String } c || c.Value<string>() is not { Length: > 0 } command)
                    return $"{at}.command is not a non-empty string";
                if (command.Length > Levers.MaxLeverLength)
                    return $"{at}.command is {command.Length} characters long, and this kit takes at most {Levers.MaxLeverLength}";
                if (Levers.NotTickableReason(command) is { } why)
                    return $"{at}.command '{command}' cannot be ticked: {why}";
                if (!seen.Add(command)) return $"{at}.command '{command}' is listed twice";

                string? kind = null;
                if (o["kind"] is { } k)
                {
                    if (k.Type != JTokenType.String || !Kinds.Contains(k.Value<string>()))
                        return $"{at}.kind is not one of {string.Join(", ", Kinds)}";
                    kind = k.Value<string>();
                }
                var risk = new List<string>();
                if (o["risk"] is { } r)
                {
                    if (r is not JArray labels) return $"{at}.risk is not a list";
                    foreach (var label in labels)
                    {
                        if (label.Type != JTokenType.String || !CheatRisk.RiskLabels.Contains(label.Value<string>()))
                            return $"{at}.risk holds something other than {string.Join(", ", CheatRisk.RiskLabels)}";
                        if (!risk.Contains(label.Value<string>()!)) risk.Add(label.Value<string>()!);
                    }
                }
                string? source = null;
                if (o["source"] is { } s)
                {
                    if (s.Type != JTokenType.String || s.Value<string>() is not { Length: > 0 } src
                        || src.Length > MaxSourceLength || Levers.UnsendableCharacter(src) != null)
                        return $"{at}.source is not a string of 1–{MaxSourceLength} plain characters";
                    source = src;
                }
                CheatCheck? check = null;
                if (o["check"] is { } ck)
                {
                    check = CheatCheck.FromJson(ck, out var checkWhy);
                    if (check == null) return $"{at}.check is not a check: {checkWhy ?? "null"}";
                    if (check.Lever is { } checkLever && Levers.NotTickableReason(checkLever) is { } leverWhy)
                        return $"{at}.check's read '{checkLever}' cannot be ticked: {leverWhy}";
                }
                found.Add(new Candidate { Command = command, Kind = kind, Risk = risk, Source = source, Check = check });
            }
            candidates = found;
            return null;
        }
    }
}
