using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// v6.1 §1 B, riding learn-and-drive v3 P2 — the kit changes nothing in a studio's game: takes land under
    /// <c>Library/</c>, every <c>[InitializeOnLoad]</c> class is inert in batch mode, and the kit compiles with no
    /// TextMeshPro reference at all.
    /// </summary>
    public class KitHygieneTests
    {
        /// <summary>The package's own folder (as the shared-fixture tests find it).</summary>
        internal static string PackageRoot()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(Levers).Assembly);
            if (info != null && !string.IsNullOrEmpty(info.resolvedPath) && Directory.Exists(info.resolvedPath))
                return info.resolvedPath;
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            for (var i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
            {
                var guess = Path.Combine(dir.FullName, "com.projectnova.recorder-kit");
                if (File.Exists(Path.Combine(guess, "package.json"))) return guess;
            }
            Assert.Fail("the kit package folder was not found");
            return "";
        }

        private static string[] KitSources() =>
            Directory.GetFiles(Path.Combine(PackageRoot(), "Editor"), "*.cs", SearchOption.AllDirectories)
                .Where(p => !p.Replace('\\', '/').Contains("/Editor/Tests/"))
                .ToArray();

        // ---- recordings under Library/ -------------------------------------------------------------------------------

        [Test]
        public void TakesLandUnderLibrary_NotInAFolderAtTheProjectRoot()
        {
            Assert.AreEqual("Library/Nova/Recordings", RecorderPaths.OutputDir);
            Assert.AreEqual(RecorderPaths.OutputDir, new ScreenCaptureDriver().OutputDir);
#if NOVA_UNITY_RECORDER_TESTS
            Assert.AreEqual(RecorderPaths.OutputDir, new UnityRecorderDriver().OutputDir);
            var (_, movie) = UnityRecorderDriver.BuildSettings("clip");
            Assert.AreEqual(RecorderPaths.OutputDir, movie.FileNameGenerator.Leaf, "Unity Recorder writes where the kit reads");
            Assert.AreEqual(UnityEditor.Recorder.OutputPath.Root.Project, movie.FileNameGenerator.Root);
#endif
            // no driver source spells the old folder any more
            foreach (var file in KitSources().Where(p => p.Contains("Recording")))
                StringAssert.DoesNotContain("\"Recordings\"", File.ReadAllText(file).Replace(
                    "LegacyOutputDir = \"Recordings\"", ""), file);
        }

        // ---- inert in batch mode --------------------------------------------------------------------------------------

        [Test]
        public void TheThreeBootsThatActedInBatchModeNowDoNot()
        {
            // True in an interactive editor, false in batch mode (the kit's own suite runs headless in batch mode).
#if NOVA_UNITY_RECORDER_TESTS
            Assert.AreEqual(!Application.isBatchMode, UnityRecorderDriverBoot.Booted, "UnityRecorderDriverBoot");
#endif
            Assert.AreEqual(!Application.isBatchMode, PlayConsoleWatch.Booted, "PlayConsoleWatch");
            Assert.AreEqual(!Application.isBatchMode, GameReflectionWarmup.Booted, "GameReflectionWarmup");
            if (Application.isBatchMode)
                Assert.IsNull(RecorderDrivers.Registered, "a batch-mode editor registered the Unity Recorder driver");
        }

        private static readonly Regex InitializeOnLoad = new(@"^\s*\[(UnityEditor\.)?InitializeOnLoad\]", RegexOptions.Multiline);
        private static readonly Regex StaticCtor = new(@"static\s+(\w+)\s*\(\s*\)\s*\{", RegexOptions.Multiline);

        [Test]
        public void EveryInitializeOnLoadClassReturnsFirstInBatchMode()
        {
            var checkedClasses = 0;
            foreach (var file in KitSources())
            {
                var text = File.ReadAllText(file);
                foreach (Match attr in InitializeOnLoad.Matches(text))
                {
                    var ctor = StaticCtor.Match(text, attr.Index);
                    Assert.IsTrue(ctor.Success, $"{file}: an [InitializeOnLoad] class with no static constructor");
                    // the first statement of the static constructor is the batch-mode return
                    var body = text.Substring(ctor.Index + ctor.Length).TrimStart();
                    StringAssert.IsMatch(@"^if\s*\(\s*(UnityEngine\.)?Application\.isBatchMode\s*\)\s*(\r?\n\s*)?return\s*;", body,
                        $"{file}: static {ctor.Groups[1].Value}() does not return first in batch mode");
                    checkedClasses++;
                }
            }
            Assert.GreaterOrEqual(checkedClasses, 6, "the scan found fewer [InitializeOnLoad] classes than the kit has");
        }

        // ---- no TextMeshPro -------------------------------------------------------------------------------------------

        [Test]
        public void TheKitNamesNoTextMeshProAssembly_AndNoSourceUsesTMPro()
        {
            foreach (var asmdef in Directory.GetFiles(Path.Combine(PackageRoot(), "Editor"), "*.asmdef", SearchOption.AllDirectories))
            {
                var references = (JObject.Parse(File.ReadAllText(asmdef))["references"] as JArray)?.Select(t => t.ToString()) ?? Enumerable.Empty<string>();
                CollectionAssert.DoesNotContain(references.ToList(), "Unity.TextMeshPro", asmdef);
            }
            var usingTmp = new Regex(@"^\s*using\s+TMPro\s*;", RegexOptions.Multiline);
            foreach (var file in KitSources())
                Assert.IsFalse(usingTmp.IsMatch(File.ReadAllText(file)), $"{file}: `using TMPro` breaks the kit in a project without TextMeshPro");
        }
    }
}
