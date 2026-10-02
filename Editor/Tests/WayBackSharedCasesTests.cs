using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// Kit 0.14.0 (invariant 179, box half; the fresh audit's M3) — WHICH RECIPE IS THE GAME'S WAY BACK TO THE LOBBY, one
    /// set of cases for both sides: <c>recipe.cases.json</c>'s <c>wayBack</c>. The box decides what it sends as the claim's
    /// <c>back</c> with <c>qualifiesAsWayBack</c> (<c>apps/renderer/src/game-ads/learn/start-claim.ts</c>, whose spec runs
    /// the same cases); here each case is built into the one-recipe document the box sends, and <see cref="BackToLobby.Read"/>
    /// must hand out a recipe exactly when the case says the kit plays it (<c>kitPlays</c>, default <c>qualifies</c>). A
    /// drift is a way back the box sends and the kit drops (or the reverse) — found here, not on a studio's machine.
    /// </summary>
    public class WayBackSharedCasesTests
    {
        private static StartClaim ClaimOf(JObject recipe)
        {
            var doc = new JObject
            {
                ["schemaVersion"] = 1,
                ["gameId"] = "g",
                ["updatedAt"] = "t",
                ["recipes"] = new JArray(recipe),
            }.ToString(Formatting.None);
            return new StartClaim(doc, SyncNova.Sha256OfText(doc), new[] { recipe["id"]!.Value<string>()! }, Array.Empty<string>());
        }

        [Test]
        public void EveryWayBackCaseInTheSharedFixture_IsPlayedByTheKitExactlyWhenItSays()
        {
            var wayBack = (JObject)RecipeSharedCasesTests.Fixture()["wayBack"]!;
            var cases = ((JArray)wayBack["cases"]!).Cast<JObject>().ToList();
            Assert.GreaterOrEqual(cases.Count, 15, "the way-back cases shrank");
            var misses = new List<string>();
            foreach (var c in cases)
            {
                var name = c["name"]!.Value<string>();
                var recipe = (JObject)wayBack["base"]!.DeepClone();
                foreach (var p in ((JObject)c["over"]!).Properties())
                    if (p.Value.Type == JTokenType.Null) recipe.Remove(p.Name);
                    else recipe[p.Name] = p.Value.DeepClone();
                var expected = (c["kitPlays"] ?? c["qualifies"])!.Value<bool>();
                var read = BackToLobby.Read(ClaimOf(recipe));
                var plays = read.Recipe != null;
                if (plays != expected)
                    misses.Add($"{name}: the kit {(plays ? "plays it" : "drops it (" + read.Problem + ")")}, the case says it {(expected ? "plays" : "drops")} it");
            }
            Assert.IsEmpty(misses, string.Join("\n", misses));
        }

        [Test]
        public void TheFixtureCoversBothAnswers_AndTheOnePlaceTheBoxIsStricter()
        {
            var cases = ((JArray)((JObject)RecipeSharedCasesTests.Fixture()["wayBack"]!)["cases"]!).Cast<JObject>().ToList();
            Assert.IsTrue(cases.Any(c => c["qualifies"]!.Value<bool>()), "no case the box sends");
            Assert.IsTrue(cases.Any(c => !c["qualifies"]!.Value<bool>() && c["kitPlays"] == null), "no case both sides drop");
            // the ONLY disagreement allowed is the box refusing what the kit would play — never the reverse
            Assert.IsFalse(cases.Any(c => c["qualifies"]!.Value<bool>() && c["kitPlays"]?.Value<bool>() == false),
                "a case the box sends and the kit drops");
        }
    }
}
