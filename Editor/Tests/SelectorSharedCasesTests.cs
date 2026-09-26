using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// v3 P0 — THE SELECTOR SEAM (invariant 101). <c>Editor/Tests/Fixtures/selector.cases.json</c> says
    /// how a <c>Name@Label</c> press name splits and when a label matches. The website's shots gate
    /// approves presses by the same two rules (renderer <c>lane-shots.ts</c> <c>parseSelector</c> /
    /// <c>labelMatches</c>) and its suite runs the same file — so a press the gate approves is a press
    /// this driver can find. The file is DATA: a disagreement is a finding for both sides.
    /// </summary>
    public class SelectorSharedCasesTests
    {
        private const string FixtureRelative = "Editor/Tests/Fixtures/selector.cases.json";

        private static string? FixturePath()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(UguiDriver).Assembly);
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
            // FAIL, never skip: a missing shared fixture means the gate and the driver are no longer held together
            Assert.IsNotNull(path, "the shared selector fixture was not found (" + FixtureRelative + ")");
            return NovaJson.ParseObject(File.ReadAllText(path!));
        }

        [Test]
        public void EverySelectorSplitsAsTheSharedFixtureSays()
        {
            var cases = Load()["parse"] as JArray;
            Assert.IsNotNull(cases);
            Assert.GreaterOrEqual(cases!.Count, 5);
            var failures = new List<string>();
            foreach (var t in cases)
            {
                var sel = t["selector"]!.Value<string>()!;
                var wantName = t["name"]!.Value<string>()!;
                var wantLabel = t["label"]!.Type == JTokenType.Null ? null : t["label"]!.Value<string>();
                var (name, label) = UguiDriver.ParseSelector(sel);
                if (name != wantName || label != wantLabel)
                    failures.Add($"'{sel}': expected ({wantName}, {wantLabel ?? "null"}), got ({name}, {label ?? "null"})");
            }
            Assert.IsEmpty(failures, string.Join("\n", failures));
        }

        [Test]
        public void EveryLabelMatchesAsTheSharedFixtureSays()
        {
            var cases = Load()["label"] as JArray;
            Assert.IsNotNull(cases);
            Assert.GreaterOrEqual(cases!.Count, 5);
            var failures = new List<string>();
            foreach (var t in cases)
            {
                var label = t["label"]!.Value<string>()!;
                var wanted = t["wanted"]!.Value<string>()!;
                var want = t["match"]!.Value<bool>();
                var got = UguiDriver.LabelMatches(label, wanted);
                if (got != want)
                    failures.Add($"label '{label}' / wanted '{wanted}': expected {want}, got {got}");
            }
            Assert.IsEmpty(failures, string.Join("\n", failures));
        }
    }
}
