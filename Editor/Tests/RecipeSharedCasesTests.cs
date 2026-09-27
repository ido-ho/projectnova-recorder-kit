using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// v3 P4 — THE RECIPE RULES, one fixture for both sides: <c>Editor/Tests/Fixtures/recipe.cases.json</c>, run here
    /// against <see cref="TeachAnalysis.Analyse"/> (and, from the upload commit, <see cref="RecipeFile"/>), and by the
    /// website's <c>apps/renderer/src/game-ads/learn/recipes.spec.ts</c> against its <c>analyseTeach</c>. A teach the kit
    /// shows as confirmed and the server stores as unconfirmed is the drift this fixture exists to stop (invariant 99).
    /// </summary>
    public class RecipeSharedCasesTests
    {
        internal const string Relative = "Editor/Tests/Fixtures/recipe.cases.json";

        internal static string? FixturePath()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(TeachAnalysis).Assembly);
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

        internal static JObject Fixture()
        {
            var path = FixturePath();
            // FAIL, never skip: a missing shared fixture means the two sides are no longer held to one rule.
            Assert.IsNotNull(path, "the shared recipe fixture was not found (" + Relative + ")");
            return NovaJson.ParseObject(File.ReadAllText(path!));
        }

        private static List<string> Strings(JToken? t) => t is JArray a ? a.Select(x => x.Value<string>()!).ToList() : new List<string>();

        [Test]
        public void EveryAnalysisCaseInTheSharedFixture_SplitsArrivesAndConfirmsAsItSays()
        {
            var cases = ((JArray)Fixture()["analysis"]!).Cast<JObject>().ToList();
            Assert.GreaterOrEqual(cases.Count, 15, "the analysis cases shrank");
            // every case is run and every miss is listed — one red line per rule a change broke, not just the first
            var misses = new List<string>();
            foreach (var c in cases)
            {
                var name = c["name"]!.Value<string>();
                try
                {
                    var r = TeachAnalysis.Analyse((JArray)c["presses"]!, Strings(c["authored"]),
                        c["replay"] as JObject, c["declaredStart"] as JObject);
                    var e = (JObject)c["expect"]!;
                    CollectionAssert.AreEqual(e["goal"]!.Values<int>().ToList(), r.Goal, "goal presses");
                    CollectionAssert.AreEqual(Strings(e["dismiss"]), r.Dismiss, "dismiss");
                    CollectionAssert.AreEqual(Strings(e["teachArrival"]), r.TeachArrival, "teach arrival");
                    CollectionAssert.AreEqual(Strings(e["arrival"]), r.Arrival, "arrival");
                    Assert.AreEqual(e["confirmed"]!.Value<bool>(), r.Confirmed, "confirmed (" + r.Why + ")");
                    if (e["why"]!.Type == JTokenType.Null) Assert.IsNull(r.Why, "why");
                    else StringAssert.Contains(e["why"]!.Value<string>(), r.Why, "why");
                    if (e["replayBlocked"] != null)
                        Assert.AreEqual(e["replayBlocked"]!.Value<bool>(), r.ReplayBlocked != null, "replay blocked");
                    if (e["steps"] is JArray steps)
                        Assert.IsTrue(JToken.DeepEquals(steps, r.Steps), "steps " + r.Steps.ToString(Newtonsoft.Json.Formatting.None));
                }
                catch (AssertionException ex)
                {
                    misses.Add("[" + name + "] " + ex.Message.Trim());
                }
            }
            if (misses.Count > 0) Assert.Fail(misses.Count + " case(s) missed:\n" + string.Join("\n", misses));
        }

        [Test]
        public void EveryRecipeBookDocumentInTheSharedFixture_IsReadOrRefusedWhole_AsTheServersSchemaDoes()
        {
            var docs = ((JArray)Fixture()["documents"]!).Cast<JObject>().ToList();
            Assert.GreaterOrEqual(docs.Count(d => d["ok"]!.Value<bool>()), 5, "the readable documents shrank");
            Assert.GreaterOrEqual(docs.Count(d => !d["ok"]!.Value<bool>()), 15, "the refusals shrank");
            var misses = new List<string>();
            foreach (var d in docs)
            {
                var name = d["name"]!.Value<string>();
                // through the TEXT, as the kit reads a file: the kit's own parser, then the reader
                var recipes = RecipeFile.Read(d["doc"]!.ToString(Newtonsoft.Json.Formatting.None), out var error);
                var ok = d["ok"]!.Value<bool>();
                if (ok && recipes == null) misses.Add($"[{name}] refused: {error}");
                if (!ok && recipes != null) misses.Add($"[{name}] read, but the server refuses it");
                if (!ok && recipes == null && string.IsNullOrEmpty(error)) misses.Add($"[{name}] refused without a reason");
            }
            if (misses.Count > 0) Assert.Fail(misses.Count + " document(s) missed:\n" + string.Join("\n", misses));
        }

        [Test]
        public void TheRecipeReader_NeverThrows_AndReadsWhatItNeeds()
        {
            foreach (var junk in new[] { null, "", "not json", "[]", "{\"schemaVersion\":1}", new string('[', 5000) })
            {
                Assert.DoesNotThrow(() => RecipeFile.Read(junk, out _));
                Assert.IsNull(RecipeFile.Read(junk, out var why));
                Assert.IsNotNull(why);
            }
            var whole = (JObject)((JArray)Fixture()["documents"]!)[0]!["doc"]!;
            var r = RecipeFile.Read(whole.ToString(), out _)!.Single();
            Assert.AreEqual("r0123456789abcdef", r.Id);
            Assert.AreEqual(TeachAnalysis.StartLobby, r.StartKind);
            Assert.AreEqual("3f2c1ab", r.GameCommit);
            Assert.AreEqual("0.12.0", r.KitVersion);
            Assert.AreEqual(7, r.Steps.Count);
            CollectionAssert.AreEqual(new[] { "Close", "Later" }, r.Dismiss);
            Assert.IsTrue(r.Confirmed);
        }

        [Test]
        public void TheFixtureNamesEveryRuleItHolds()
        {
            var names = ((JArray)Fixture()["analysis"]!).Select(c => c["name"]!.Value<string>()!).ToList();
            foreach (var rule in new[] { "dismiss", "navigation", "tab", "baseline", "clone", "ONE observation", "unstable", "invoke-button", "gesture", "chain", "cheat" })
                Assert.IsTrue(names.Any(n => n.Contains(rule)), "no case holds the rule about " + rule);
        }
    }
}
