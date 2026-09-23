using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// Second audit, M9 — THE SHARED ADAPTER-LEVER SEAM (invariant 101).
    ///
    /// <c>Editor/Tests/Fixtures/adapter-levers.cases.json</c> holds one verdict per
    /// <c>adapter.json</c>: the levers that document asks a person to tick. BOTH sides of the wire
    /// are run against it — this test through the kit's real
    /// <see cref="Levers.NeededFromAdapterJson"/>, and the hosted lint's own suite through the
    /// TypeScript mirror. They have to agree character for character: the website sends the list it
    /// computed, the kit answers with the list IT computed, and a send whose two lists differ is
    /// refused at `done` ("DIFFERENT lever lists") — a studio would see a send that can never
    /// finish and nothing on their disk to explain it.
    ///
    /// Compared as ORDINALLY SORTED SETS, because the order a window lists ticks in is not a
    /// contract and the membership is.
    ///
    /// The file is DATA, not a copy of either implementation: if the kit's real behaviour ever
    /// disagrees with a case, that is a finding to take to both sides, never a number to edit here.
    /// </summary>
    public class AdapterLeversSharedCasesTests
    {
        private const string FixtureRelative = "Editor/Tests/Fixtures/adapter-levers.cases.json";
        private const int MinimumCases = 10;

        /// <summary>The package's own folder — through the package manager when the kit is installed
        /// as a package (the test host's case), with a walk up from the working directory as the
        /// fallback for a project that embedded the sources.</summary>
        private static string? FixturePath(string relative = FixtureRelative)
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(Levers).Assembly);
            if (info != null && !string.IsNullOrEmpty(info.resolvedPath))
            {
                var resolved = Path.Combine(info.resolvedPath, relative);
                if (File.Exists(resolved)) return resolved;
            }
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            for (var i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
            {
                var guess = Path.Combine(dir.FullName, "com.projectnova.recorder-kit", relative);
                if (File.Exists(guess)) return guess;
            }
            return null;
        }

        [Test]
        public void EveryAdapterInTheSharedFixtureNeedsExactlyTheLeversItSays()
        {
            var path = FixturePath();
            // FAIL, never skip: a missing shared fixture means the two sides are no longer held to
            // the same lever list, and a skipped test reads as a pass on every dashboard there is.
            Assert.IsNotNull(path,
                "the shared adapter-lever fixture was not found (" + FixtureRelative + ") — the kit " +
                "and the hosted lint are no longer tested against the same cases");

            var root = NovaJson.ParseObject(File.ReadAllText(path!));
            var cases = root["cases"] as JArray;
            Assert.IsNotNull(cases, "the fixture has no 'cases' array");
            Assert.GreaterOrEqual(cases!.Count, MinimumCases,
                "the shared fixture has shrunk below the rules it is supposed to pin");

            var failures = new List<string>();
            foreach (var t in cases)
            {
                var c = (JObject)t;
                var name = c["name"]?.Value<string>() ?? "(unnamed)";
                Assert.IsNotNull(c["adapter"], name + ": the case carries no 'adapter' document");
                Assert.IsNotNull(c["levers"] as JArray, name + ": the case carries no 'levers' array");

                // The adapter reaches the kit as the TEXT of a file, which is what a sync delivers.
                var adapterText = c["adapter"]!.ToString(Formatting.None);

                var expected = (c["levers"] as JArray)!.Select(x => x.Value<string>() ?? "")
                    .OrderBy(x => x, System.StringComparer.Ordinal).ToArray();
                var got = Levers.NeededFromAdapterJson(adapterText)
                    .OrderBy(x => x, System.StringComparer.Ordinal).ToArray();

                if (!expected.SequenceEqual(got, System.StringComparer.Ordinal))
                    failures.Add($"{name}: expected [{string.Join(", ", expected)}], " +
                                 $"got [{string.Join(", ", got)}]");
            }

            Assert.IsEmpty(failures,
                $"{failures.Count} of the {cases.Count} shared adapter-lever cases disagree with " +
                "this kit:\n" + string.Join("\n", failures));
        }

        private const string ShapesRelative = "Editor/Tests/Fixtures/lever-shapes.cases.json";

        /// <summary>
        /// The fourth audit (2026-09-21) — WHICH LEVER SHAPES CAN BE TICKED, one file for both sides.
        /// <c>Editor/Tests/Fixtures/lever-shapes.cases.json</c> says, per command, whether the kit
        /// refuses to tick it (and ignores it in levers.json) and the website refuses to send it:
        /// adjacent placeholders, a target placeholder glued to letters, a placeholder or quote in
        /// camera-pose's Type slot, a quoted target, a padded one. The hosted lint's suite runs the
        /// same file. Before this there was no shared case list for SHAPES, only for lever LISTS, so a
        /// rule could hold on one side and not the other with both suites green.
        ///
        /// It also holds the kit's two readings of one rule to each other: for a shot's COMMANDS,
        /// <see cref="Levers.NotTickableReason"/> (what the window shows, and what filters the window's
        /// templates) and <see cref="Levers.NotLiteralEnough"/> (what the gate skips) must refuse exactly
        /// the same shapes — except a text whose verb is `hide-overlay` or `camera-spec`
        /// (<see cref="Levers.NeverATemplate"/>; five such shapes since the seventh audit): it is never a template, so
        /// one ticked by hand approves its own exact text and no binding of itself, and NotLiteralEnough is not its
        /// rule.
        /// </summary>
        [Test]
        public void EveryLeverShapeInTheSharedFixtureIsRefusedOrTickableAsItSays()
        {
            var path = FixturePath(ShapesRelative);
            Assert.IsNotNull(path, "the shared lever-shape fixture was not found (" + ShapesRelative + ")");
            var cases = NovaJson.ParseObject(File.ReadAllText(path!))["cases"] as JArray;
            Assert.IsNotNull(cases, "the fixture has no 'cases' array");
            Assert.GreaterOrEqual(cases!.Count(c => c["refused"]!.Value<bool>()), 20, "the refusals shrank");
            Assert.GreaterOrEqual(cases!.Count(c => !c["refused"]!.Value<bool>()), 10, "the controls shrank");

            var failures = new List<string>();
            foreach (var t in cases!)
            {
                var command = t["command"]!.Value<string>()!;
                var refused = t["refused"]!.Value<bool>();
                var reason = Levers.NotTickableReason(command);
                if ((reason != null) != refused)
                    failures.Add($"'{command}': expected {(refused ? "refused" : "tickable")}, NotTickableReason says " +
                                 (reason ?? "null"));
                if (Levers.NeverATemplate(command))
                {
                    // never a template: the gate reads one in levers.json by its exact text, not by NotLiteralEnough
                    if (!Levers.Allows(new[] { command }, command))
                        failures.Add($"'{command}': ticked by hand, it does not approve its own exact text");
                    if (ShotBinding.HasPlaceholder(command)
                        && Levers.Allows(new[] { command }, ShotBinding.WithEveryPlaceholderAs(command, "a")))
                        failures.Add($"'{command}': ticked by hand, it approves a binding of itself, as a template");
                    continue;
                }
                if (Levers.NotLiteralEnough(command) != refused)
                    failures.Add($"'{command}': NotLiteralEnough says {Levers.NotLiteralEnough(command)} — the gate " +
                                 "and the window read this shape differently");
            }
            Assert.IsEmpty(failures, $"{failures.Count} shared lever-shape verdicts disagree with this kit:\n" +
                                     string.Join("\n", failures));
        }

        /// <summary>
        /// The levers of a whole delivery are the shots' levers and the adapter's, together — the
        /// list <c>sync-nova</c> reports and the window shows. Pinned here because the two halves
        /// are computed by two functions and only their union is what anyone ticks.
        /// </summary>
        [Test]
        public void TheDeliverysLeversAreTheShotsAndTheAdapterTogether()
        {
            const string shots = @"{ ""$schemaVersion"": 1, ""shots"": [
                { ""name"": ""a"", ""setup"": [""set Coins 5""],
                  ""steps"": [ { ""kind"": ""timeScale"", ""factor"": 0.5 } ],
                  ""settle"": { ""kind"": ""present"", ""name"": ""X"" } } ] }";
            const string adapter = @"{ ""gameId"": ""g"", ""overlayTypeNames"": [""Hud""],
                ""ready"": { ""mute"": ""call Audio.Mute"" },
                ""camera"": { ""viewType"": ""Rig"" } }";

            CollectionAssert.AreEqual(
                new[]
                {
                    "call Audio.Mute", "camera-spec Rig SetPosition SetRotation SetFov",
                    "hide-overlay Hud", "set Coins 5", "timeScale",
                },
                Levers.NeededFrom(shots, adapter).ToArray());
        }

        private const string DeliveriesRelative = "Editor/Tests/Fixtures/deliveries.cases.json";

        private static JObject OneSecondWait() => new() { ["kind"] = "wait", ["seconds"] = 1 };

        /// <summary>The fixture's generated document: one shot 'a' (the thirteenth audit's 'setupRepeat', 'stepsCount' and
        /// 'allParts').</summary>
        private static JObject OneShotA(JArray? setup, JArray steps, JObject settle)
        {
            var shot = new JObject { ["name"] = "a" };
            if (setup != null) shot["setup"] = setup;
            shot["steps"] = steps;
            shot["settle"] = settle;
            return new JObject { ["$schemaVersion"] = 1, ["shots"] = new JArray(shot) };
        }

        /// <summary>
        /// The ninth audit (2026-09-22) — WHICH DELIVERIES ARE REFUSED BEFORE ANYTHING IS WRITTEN, one file for both sides.
        /// <c>Editor/Tests/Fixtures/deliveries.cases.json</c> says, per pair of files, whether this kit's
        /// <see cref="SyncNova.Run"/> refuses it and the website's send check refuses to send it: more levers than the cap,
        /// counted over both files; a placeholder nothing binds (a shot's undeclared parameter, anything in adapter.json);
        /// a lever holding a line break or another character the window cannot show as it runs; and (the tenth audit) a
        /// file the kit cannot read as its readers will — led by U+FEFF, not a JSON object, a ready write that is not a
        /// string. The cap itself is in the file, and this kit's <see cref="Levers.MaxLevers"/> must be it.
        /// </summary>
        [Test]
        public void EveryDeliveryInTheSharedFixtureIsRefusedOrWrittenAsItSays()
        {
            var path = FixturePath(DeliveriesRelative);
            Assert.IsNotNull(path, "the shared delivery fixture was not found (" + DeliveriesRelative + ")");
            var root = NovaJson.ParseObject(File.ReadAllText(path!));
            Assert.AreEqual(root["maxLevers"]!.Value<int>(), Levers.MaxLevers, "the two sides refuse above different numbers");
            Assert.AreEqual(root["maxLeverLength"]!.Value<int>(), Levers.MaxLeverLength, "the two sides take levers of different lengths");
            Assert.AreEqual(root["maxNesting"]!.Value<int>(), NovaJson.MaxNesting, "the two sides read different nestings");
            // the twelfth audit: the floor under every number of seconds, and the longest shot name
            Assert.AreEqual(root["minSeconds"]!.Value<double>(), JsonShotLoader.MinSeconds, "the two sides take different numbers of seconds");
            Assert.AreEqual(root["maxShotName"]!.Value<int>(), JsonShotLoader.MaxShotName, "the two sides take different shot names");
            // the thirteenth audit: the caps on one shot's setup and steps and on one condition's parts, and the ceiling on seconds
            Assert.AreEqual(root["maxSetup"]!.Value<int>(), JsonShotLoader.MaxSetup, "the two sides take different setup lists");
            Assert.AreEqual(root["maxSteps"]!.Value<int>(), JsonShotLoader.MaxSteps, "the two sides take different step lists");
            Assert.AreEqual(root["maxConditionParts"]!.Value<int>(), JsonShotLoader.MaxConditionParts, "the two sides take different conditions");
            Assert.AreEqual(root["maxSeconds"]!.Value<double>(), JsonShotLoader.MaxSeconds, "the two sides take different numbers of seconds");
            var cases = root["cases"] as JArray;
            Assert.IsNotNull(cases, "the fixture has no 'cases' array");
            Assert.GreaterOrEqual(cases!.Count(c => c["refused"]!.Value<bool>()), 44, "the refusals shrank");
            Assert.GreaterOrEqual(cases!.Count(c => !c["refused"]!.Value<bool>()), 14, "the controls shrank");

            var failures = new List<string>();
            foreach (var t in cases!)
            {
                var name = t["name"]!.Value<string>()!;
                var refused = t["refused"]!.Value<bool>();
                JObject shots;
                if (t["setupCount"] is { } count)
                    shots = new JObject
                    {
                        ["$schemaVersion"] = 1,
                        ["shots"] = new JArray(new JObject
                        {
                            ["name"] = "a",
                            ["setup"] = new JArray(Enumerable.Range(0, count.Value<int>()).Select(i => (object)("raw lever" + i)).ToArray()),
                            ["steps"] = new JArray(new JObject { ["kind"] = "wait", ["seconds"] = 1 }),
                            ["settle"] = new JObject { ["kind"] = "present", ["name"] = "X" },
                        }),
                    };
                // the thirteenth audit: 'setupRepeat', 'stepsCount' and 'allParts' are built, as the site's runner builds them
                else if (t["setupRepeat"] is JObject repeat)
                    shots = OneShotA(new JArray(Enumerable.Repeat((object)repeat["command"]!.Value<string>()!, repeat["count"]!.Value<int>()).ToArray()),
                        new JArray(OneSecondWait()), new JObject { ["kind"] = "present", ["name"] = "X" });
                else if (t["stepsCount"] is { } stepsCount)
                    shots = OneShotA(null, new JArray(Enumerable.Range(0, stepsCount.Value<int>()).Select(_ => (object)OneSecondWait()).ToArray()),
                        new JObject { ["kind"] = "present", ["name"] = "X" });
                else if (t["allParts"] is { } allParts)
                    shots = OneShotA(null, new JArray(OneSecondWait()), new JObject
                    {
                        ["kind"] = "all",
                        ["parts"] = new JArray(Enumerable.Range(0, allParts.Value<int>())
                            .Select(i => (object)new JObject { ["kind"] = "absent", ["name"] = "N" + i }).ToArray()),
                    });
                else
                    shots = t["shots"] as JObject ?? NovaJson.ParseObject(
                        "{ \"$schemaVersion\": 1, \"shots\": [ { \"name\": \"a\", \"steps\": [ { \"kind\": \"wait\", \"seconds\": 1 } ], " +
                        "\"settle\": { \"kind\": \"present\", \"name\": \"X\" } } ] }");
                var adapter = (t["adapter"] as JObject)?.DeepClone() as JObject ?? new JObject();
                adapter["gameId"] = "g";
                // the tenth audit: 'shotsText' is sent verbatim, and 'bom' puts U+FEFF in front of the files it names
                var shotsText = t["shotsText"]?.Value<string>() ?? shots.ToString();
                var adapterText = adapter.ToString();
                // the eleventh audit: 'schemaVersionDepth' and 'noteDepth' are built as TEXT — the fixture itself is read by
                // the kit's capped reader, and a JToken that deep would render indented, by the megabyte
                if (t["schemaVersionDepth"] is { } versionDepth)
                    shotsText = "{ \"$schemaVersion\": " + new string('[', versionDepth.Value<int>()) + "1" +
                                new string(']', versionDepth.Value<int>()) + ", \"shots\": [] }";
                string WithNote(string text, JToken? depth) => depth == null
                    ? text
                    : "{ \"note\": " + new string('[', depth.Value<int>()) + new string(']', depth.Value<int>()) + ", " +
                      text.Substring(text.IndexOf('{') + 1).TrimStart();
                shotsText = WithNote(shotsText, t["noteDepth"]?["shots"]);
                adapterText = WithNote(adapterText, t["noteDepth"]?["adapter"]);
                var bom = (t["bom"] as JArray)?.Select(b => b.Value<string>()).ToList() ?? new List<string?>();
                if (bom.Contains("shots")) shotsText = "\uFEFF" + shotsText;
                if (bom.Contains("adapter")) adapterText = "\uFEFF" + adapterText;

                var project = Path.Combine(Path.GetTempPath(), "deliveries-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(project);
                try
                {
                    var result = SyncNova.Run(project, new SyncNovaFiles(shotsText, adapterText,
                        SyncNova.Sha256OfText(shotsText), SyncNova.Sha256OfText(adapterText)), "run-1", "g");
                    if ((result.Refusal != null) != refused)
                        failures.Add($"{name}: expected {(refused ? "refused" : "written")}, SyncNova says " +
                                     (result.Refusal ?? "nothing (written)"));
                    if (result.Refusal != null && Directory.Exists(RelayPaths.NovaDir(project)))
                        failures.Add($"{name}: refused, but Library/Nova was written");
                    // THE POSITIVE CONTROL (the eleventh audit, ruling 1): the delivery check is the loader, so whatever it
                    // writes, the read-back through the real loader finds no error in
                    if (result.Refusal == null && result.ShotLoadErrors.Count > 0)
                        failures.Add($"{name}: written, and the loader then refused it: {string.Join(" | ", result.ShotLoadErrors.Select(e => e.Length > 120 ? e.Substring(0, 120) + "…" : e))}");
                }
                finally
                {
                    try { Directory.Delete(project, true); } catch (IOException) { }
                }
            }
            Assert.IsEmpty(failures, $"{failures.Count} of the {cases.Count} shared delivery cases disagree with this kit:\n" +
                                     string.Join("\n", failures));
        }
    }
}
