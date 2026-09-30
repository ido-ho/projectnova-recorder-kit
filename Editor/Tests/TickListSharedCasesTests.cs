using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// Learn-and-drive v3 P2 — THE TICK-LIST INPUT, one fixture for both sides:
    /// <c>Editor/Tests/Fixtures/tick-list.cases.json</c>, run here against <see cref="TickList.Refusal"/> and
    /// <see cref="CheatRisk.Why"/>, and by the website's <c>apps/renderer/src/game-ads/learn/tick-list.spec.ts</c> against
    /// its <c>tickListRefusal</c> and <c>tickListRisk</c>. A file one side reads and the other refuses is a send that fails
    /// on the studio's machine; a command one side calls risky and the other does not is a row that lies (invariant 99).
    /// </summary>
    public class TickListSharedCasesTests
    {
        private const string Relative = "Editor/Tests/Fixtures/tick-list.cases.json";

        private static string? FixturePath()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(Levers).Assembly);
            if (info != null && !string.IsNullOrEmpty(info.resolvedPath))
            {
                var resolved = Path.Combine(info.resolvedPath, Relative);
                if (File.Exists(resolved)) return resolved;
            }
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            for (var i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
            {
                var guess = Path.Combine(dir.FullName, "com.projectnova.recorder-kit", Relative);
                if (File.Exists(guess)) return guess;
            }
            return null;
        }

        /// <summary>The case's file text, built exactly as the website's runner builds it.</summary>
        private static string TextOf(JObject c)
        {
            if (c["text"] is { Type: JTokenType.String } text) return text.Value<string>()!;
            if (c["generate"] is JObject generate)
            {
                var n = generate["count"]!.Value<int>();
                return new JObject
                {
                    ["$schemaVersion"] = 1,
                    ["candidates"] = new JArray(Enumerable.Range(0, n).Select(i => (object)new JObject { ["command"] = $"raw cheat{i}" }).ToArray()),
                }.ToString(Formatting.None);
            }
            if (c["commandLength"] is { } length)
                return new JObject
                {
                    ["$schemaVersion"] = 1,
                    ["candidates"] = new JArray(new JObject { ["command"] = "raw " + new string('x', length.Value<int>() - 4) }),
                }.ToString(Formatting.None);
            return c["doc"]!.ToString(Formatting.None);
        }

        [Test]
        public void EveryTickListInTheSharedFixtureIsReadOrRefusedAsItSays_AndItsRiskyCommandsAreTheSame()
        {
            var path = FixturePath();
            // FAIL, never skip: a missing shared fixture means the two sides are no longer held to one rule.
            Assert.IsNotNull(path, "the shared tick-list fixture was not found (" + Relative + ")");
            var root = NovaJson.ParseObject(File.ReadAllText(path!));
            Assert.AreEqual(Levers.MaxLevers, root["maxCandidates"]!.Value<int>(), "the candidate cap");
            Assert.AreEqual(Levers.MaxLeverLength, root["maxCommandLength"]!.Value<int>(), "the command length cap");
            var cases = ((JArray)root["cases"]!).Cast<JObject>().ToList();
            Assert.GreaterOrEqual(cases.Count(c => c["refused"]!.Value<bool>()), 25, "the refusals shrank");
            Assert.GreaterOrEqual(cases.Count(c => !c["refused"]!.Value<bool>()), 10, "the controls shrank");

            var failures = new List<string>();
            foreach (var c in cases)
            {
                var name = c["name"]!.Value<string>();
                var refused = c["refused"]!.Value<bool>();
                var refusal = TickList.Refusal(TextOf(c), out var candidates, out var needed);
                if ((refusal != null) != refused)
                {
                    failures.Add($"'{name}': expected {(refused ? "refused" : "read")}, the kit says {refusal ?? "read"}");
                    continue;
                }
                if (refused)
                {
                    if (candidates.Count != 0) failures.Add($"'{name}': refused, yet {candidates.Count} candidates came back");
                    continue;
                }
                var risky = candidates.Select(x => x.Command).Where(cmd => CheatRisk.Why(cmd, candidates) != null)
                    .OrderBy(s => s, System.StringComparer.Ordinal).ToList();
                var want = (c["risky"] as JArray ?? new JArray()).Select(t => t.Value<string>()!)
                    .OrderBy(s => s, System.StringComparer.Ordinal).ToList();
                if (!risky.SequenceEqual(want))
                    failures.Add($"'{name}': risky [{string.Join(" | ", risky)}], the fixture says [{string.Join(" | ", want)}]");
                // invariant 190: the needed set, in file order, and which of it the same risk rule holds
                var neededWant = (c["needed"] as JArray ?? new JArray()).Select(t => t.Value<string>()!).ToList();
                if (!needed.Select(x => x.Command).SequenceEqual(neededWant))
                    failures.Add($"'{name}': needed [{string.Join(" | ", needed.Select(x => x.Command))}], the fixture says [{string.Join(" | ", neededWant)}]");
                var neededRisky = needed.Select(x => x.Command).Where(cmd => CheatRisk.Why(cmd, candidates) != null)
                    .OrderBy(s => s, System.StringComparer.Ordinal).ToList();
                var neededRiskyWant = (c["neededRisky"] as JArray ?? new JArray()).Select(t => t.Value<string>()!)
                    .OrderBy(s => s, System.StringComparer.Ordinal).ToList();
                if (!neededRisky.SequenceEqual(neededRiskyWant))
                    failures.Add($"'{name}': needed risky [{string.Join(" | ", neededRisky)}], the fixture says [{string.Join(" | ", neededRiskyWant)}]");
            }
            Assert.IsEmpty(failures, $"{failures.Count} shared tick-list verdicts disagree with this kit:\n" + string.Join("\n", failures));
        }

        /// <summary>
        /// The P3 review, M5 — what the server hands a kit before 0.11.0 (the list without `check`, `tickListForKit`)
        /// must still be a list: every case this kit READS, stripped of `check`, is still read, and keeps only the keys
        /// a 0.10.x reader knows (`keysKnownTo0_10`). And this kit knows exactly those keys plus `check` — a new key
        /// here is one the server must learn to strip for older kits.
        /// </summary>
        [Test]
        public void EveryReadListWithoutItsChecksIsStillRead_AndKeepsOnlyTheKeysA010KitKnows()
        {
            var path = FixturePath();
            Assert.IsNotNull(path, "the shared tick-list fixture was not found (" + Relative + ")");
            var root = NovaJson.ParseObject(File.ReadAllText(path!));
            var known = ((JArray)root["keysKnownTo0_10"]!).Select(t => t.Value<string>()!).ToList();
            CollectionAssert.AreEquivalent(new[] { "command", "kind", "risk", "source" }, known, "the 0.10 key set is v0.10.0's TickList.cs:130");
            // this kit knows those keys and `check`: a candidate carrying all five is read
            var all = new JObject { ["$schemaVersion"] = 1, ["candidates"] = new JArray(new JObject
            {
                ["command"] = "raw A", ["kind"] = "level", ["risk"] = new JArray(), ["source"] = "kit",
                ["check"] = new JObject { ["text"] = "Victory" },
            }) };
            Assert.IsNull(TickList.Refusal(all.ToString(Formatting.None), out _));
            var failures = new List<string>();
            var withCheck = 0;
            foreach (var c in ((JArray)root["cases"]!).Cast<JObject>())
            {
                if (c["refused"]!.Value<bool>() || c["doc"] is not JObject doc) continue;
                var stripped = (JObject)doc.DeepClone();
                foreach (var cand in (stripped["candidates"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    if (cand.Remove("check")) withCheck++;
                    foreach (var p in cand.Properties())
                        if (!known.Contains(p.Name)) failures.Add($"'{c["name"]}': a 0.10 kit would refuse the key '{p.Name}'");
                }
                if (TickList.Refusal(stripped.ToString(Formatting.None), out _) is { } why)
                    failures.Add($"'{c["name"]}': without its checks the list is refused: {why}");
                // invariant 190: what a kit before 0.14.2 is handed — the list without `needed` — is still read
                if (stripped.Remove("needed") && TickList.Refusal(stripped.ToString(Formatting.None), out _) is { } whyNeeded)
                    failures.Add($"'{c["name"]}': without its needed set the list is refused: {whyNeeded}");
            }
            Assert.GreaterOrEqual(withCheck, 3, "the fixture's check cases shrank");
            Assert.IsEmpty(failures, string.Join("\n", failures));
        }
    }
}
