using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// THE PRESS-RISK WORDS (invariant 101). <c>Editor/Tests/Fixtures/press-risk.cases.json</c> holds the ONE list of
    /// word roots the kit enforces at every gated press (<see cref="PressGuard"/>) — and the website's copies (the tick
    /// list's risk, the reader's DESTRUCTIVE? mark, the near-name rule's safety) run the same file, so no copy drifts.
    /// </summary>
    public class PressGuardSharedCasesTests
    {
        private const string FixtureRelative = "Editor/Tests/Fixtures/press-risk.cases.json";

        private static JObject Load()
        {
            string? path = null;
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(PressGuard).Assembly);
            if (info != null && !string.IsNullOrEmpty(info.resolvedPath) && File.Exists(Path.Combine(info.resolvedPath, FixtureRelative)))
                path = Path.Combine(info.resolvedPath, FixtureRelative);
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            for (var i = 0; path == null && i < 6 && dir != null; i++, dir = dir.Parent)
            {
                var guess = Path.Combine(dir.FullName, "com.projectnova.recorder-kit", FixtureRelative);
                if (File.Exists(guess)) path = guess;
            }
            // FAIL, never skip: a missing shared fixture means the copies are no longer held together
            Assert.IsNotNull(path, "the shared press-risk fixture was not found (" + FixtureRelative + ")");
            return NovaJson.ParseObject(File.ReadAllText(path!));
        }

        [Test]
        public void TheRootsAreTheFixtures()
        {
            var f = Load();
            CollectionAssert.AreEqual(f["destructiveRoots"]!.Values<string>().ToList(), PressGuard.DestructiveRoots);
            CollectionAssert.AreEqual(f["moneyAndAccountRoots"]!.Values<string>().ToList(), PressGuard.MoneyAndAccountRoots);
        }

        [Test]
        public void EveryTargetIsRiskyAsTheSharedFixtureSays()
        {
            var cases = (JArray)Load()["cases"]!;
            Assert.GreaterOrEqual(cases.Count, 20);
            var failures = new List<string>();
            foreach (var c in cases)
            {
                var text = c["text"]!.Value<string>()!;
                var want = c["root"]!.Type == JTokenType.Null ? null : c["root"]!.Value<string>();
                var got = PressGuard.RiskyRoot(text);
                if (got != want) failures.Add($"'{text}': expected {want ?? "null"}, got {got ?? "null"}");
            }
            Assert.IsEmpty(failures, string.Join("\n", failures));
        }
    }
}
