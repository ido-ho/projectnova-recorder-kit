using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// Kit 0.13.5 — THE NEAR-NAME RULE (invariant 101). <c>Editor/Tests/Fixtures/near-name.cases.json</c> says how a name
    /// splits into tokens, how two names score, how a ui-dump row reads, and what is offered for a name that is not on
    /// screen. The website's <c>apps/api/src/game-capture/near-name.ts</c> runs the same file — so the kit's log line and
    /// the website's "Did you mean" cannot disagree. The file is DATA: a disagreement is a finding for both sides.
    /// </summary>
    public class NearNameSharedCasesTests
    {
        private const string FixtureRelative = "Editor/Tests/Fixtures/near-name.cases.json";

        private static string? FixturePath()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(NearName).Assembly);
            if (info != null && !string.IsNullOrEmpty(info.resolvedPath))
            {
                var resolved = Path.Combine(info.resolvedPath, FixtureRelative);
                if (File.Exists(resolved)) return resolved;
            }
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            for (var i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
            {
                var guess = Path.Combine(dir.FullName, "com.projectnova.recorder-kit", FixtureRelative);
                if (File.Exists(guess)) return guess;
            }
            return null;
        }

        private static JObject Load()
        {
            var path = FixturePath();
            // FAIL, never skip: a missing shared fixture means the two readers are no longer held together
            Assert.IsNotNull(path, "the shared near-name fixture was not found (" + FixtureRelative + ")");
            return NovaJson.ParseObject(File.ReadAllText(path!));
        }

        private static string? Str(JToken? t) => t == null || t.Type == JTokenType.Null ? null : t.Value<string>();

        [Test]
        public void TheConstantsAreTheFixtures()
        {
            var rule = (JObject)Load()["rule"]!;
            Assert.AreEqual(rule["minPrefix"]!.Value<int>(), NearName.MinPrefix);
            Assert.AreEqual(rule["threshold"]!.Value<double>(), NearName.Threshold);
            Assert.AreEqual(rule["max"]!.Value<int>(), NearName.Max);
            var syn = ((JObject)rule["synonyms"]!).Properties().ToDictionary(p => p.Name, p => p.Value.Value<string>()!);
            CollectionAssert.AreEquivalent(syn, NearName.Synonyms);
            CollectionAssert.AreEquivalent(rule["roleTokens"]!.Values<string>().ToList(), NearName.RoleTokens);
            CollectionAssert.AreEqual(rule["spendNouns"]!.Values<string>().ToList(), NearName.SpendNouns);
        }

        [Test]
        public void EveryNameTokenisesAsTheSharedFixtureSays()
        {
            var failures = new List<string>();
            foreach (var t in (JArray)Load()["tokens"]!)
            {
                var name = t["name"]!.Value<string>()!;
                var want = t["tokens"]!.Values<string>().ToList();
                var got = NearName.Tokens(name);
                if (!want.SequenceEqual(got))
                    failures.Add($"'{name}': expected [{string.Join(", ", want)}], got [{string.Join(", ", got)}]");
            }
            Assert.IsEmpty(failures, string.Join("\n", failures));
        }

        [Test]
        public void EveryPairScoresAsTheSharedFixtureSays()
        {
            var cases = (JArray)Load()["score"]!;
            Assert.GreaterOrEqual(cases.Count, 10);
            var failures = new List<string>();
            foreach (var t in cases)
            {
                var a = t["a"]!.Value<string>()!;
                var b = t["b"]!.Value<string>()!;
                var m = NearName.Match(a, b);
                var score = Math.Round(m.Score, 3, MidpointRounding.AwayFromZero);
                if (m.Matched != t["matched"]!.Value<int>() || m.Union != t["union"]!.Value<int>()
                    || Math.Abs(score - t["score"]!.Value<double>()) > 1e-9 || m.Offered != t["offered"]!.Value<bool>())
                    failures.Add($"'{a}' / '{b}': expected {t["matched"]}/{t["union"]} {t["score"]} offered={t["offered"]}, " +
                                 $"got {m.Matched}/{m.Union} {score} offered={m.Offered}");
            }
            Assert.IsEmpty(failures, string.Join("\n", failures));
        }

        [Test]
        public void EveryRowReadsAsTheSharedFixtureSays()
        {
            var failures = new List<string>();
            foreach (var t in (JArray)Load()["rows"]!)
            {
                var row = t["row"]!.Value<string>()!;
                var r = NearName.ParseRow(row);
                var want = $"{t["selector"]}|{t["name"]}|{Str(t["label"]) ?? "null"}|{Str(t["role"]) ?? "null"}|" +
                           $"{t["locked"]!.Value<bool>()}|{t["reaches"]!.Value<bool>()}|{t["indexed"]!.Value<bool>()}";
                var got = $"{r.Selector}|{r.Name}|{r.Label ?? "null"}|{r.Role ?? "null"}|{r.Locked}|{r.Reaches}|{r.Indexed}";
                if (want != got) failures.Add($"'{row}': expected {want}, got {got}");
            }
            Assert.IsEmpty(failures, string.Join("\n", failures));
        }

        [Test]
        public void EverySuggestionIsTheSharedFixtures()
        {
            var cases = (JArray)Load()["suggest"]!;
            Assert.GreaterOrEqual(cases.Count, 10);
            var failures = new List<string>();
            foreach (var t in cases)
            {
                var target = t["target"]!.Value<string>()!;
                var rows = t["onScreen"]!.Values<string>().Select(r => NearName.ParseRow(r!));
                var (state, suggestions, unsafeCount) = NearName.Suggest(target, rows, t["wants"]!.Value<string>() == "clickable");
                var want = t["state"]!.Value<string>() + ": " + string.Join(", ", ((JArray)t["suggestions"]!).Select(s =>
                    $"{s["selector"]} {s["score"]!.Value<double>().ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}" +
                    $"{(s["locked"]?.Value<bool>() == true ? " locked" : "")}")) + $" | unsafe {t["unsafe"]!.Value<int>()}";
                var got = state + ": " + string.Join(", ", suggestions.Select(s =>
                    $"{s.Selector} {s.Score.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}{(s.Locked ? " locked" : "")}")) + $" | unsafe {unsafeCount}";
                if (want != got) failures.Add($"'{target}' ({Str(t["about"]) ?? ""}): expected {want}, got {got}");
            }
            Assert.IsEmpty(failures, string.Join("\n", failures));
        }

        [Test]
        public void TheLogLine_NamesTheNearOnes_AndSaysNothingWhenPresentOrFar()
        {
            var rl = new[] { "Adventure_Tab", "Equip_Tab", "Pet_Tab", "Shop_Tab", "Talents_Tab" };
            Assert.AreEqual("  near names on screen for 'Tab_Equipment': 'Equip_Tab' (1)", NearName.LogLine("Tab_Equipment", rl));
            Assert.IsNull(NearName.LogLine("Tab_Heroes", rl), "nothing near: no line (a role word alone is never near)");
            Assert.IsNull(NearName.LogLine("Pet_Tab", rl), "on screen: no line");
            Assert.IsNull(NearName.LogLine("Tab_Equipment", Array.Empty<string>()));
            Assert.IsNull(NearName.LogLine("Save", new[] { "DeleteSave [Button]" }), "an unsafe near name is never named as a fix");
            Assert.AreEqual("  near names on screen for 'Close_Btn': 'Close_Button' (1, locked)",
                NearName.LogLine("Close_Btn", new[] { "Close_Button [Button] [locked]" }));
        }
    }
}
