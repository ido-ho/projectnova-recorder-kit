using System.IO;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// Spec §8.10 / plan item 1 — the Doctor says when a game never got past its boot scene, and
    /// why. The shape pinned is Rogue Legend's on 2026-09-23: still on `Loading.unity` after the wait,
    /// a 404 from its own login server in the console, and a build with more scenes to go to.
    /// </summary>
    public class SelfTestStuckTests
    {
        private static JObject Facts(string? active, int? errors, int scenes, string? first = null) =>
            SelfTestFacts.Build("0.9.0", "6000.0.63f1", 4.2, 162, 26.3f, 1f, true, true, true, "UnityRecorderDriver",
                null, false, "Assets/Scenes/New/Loading.unity", 0, null, true, null,
                activeScene: active, consoleErrors: errors, firstConsoleError: first,
                gitBranch: "nova-first-time-test", gitCommit: new string('a', 40), enabledSceneCount: scenes);

        [Test]
        public void StillOnBoot_WithErrors_AndSomewhereToGo_IsStuck()
        {
            var f = Facts("Assets/Scenes/New/Loading.unity", 12, 7, "Exception in sending request https://…/v0595_login_dev/login: HTTP/1.1 404 Not Found");
            Assert.IsTrue(f["stuckOnBootScene"]!.Value<bool>());
            Assert.AreEqual(12, f["consoleErrors"]!.Value<int>());
            StringAssert.Contains("404", f["firstConsoleError"]!.Value<string>());
            Assert.AreEqual("nova-first-time-test", f["gitBranch"]!.Value<string>());
        }

        [Test]
        public void TheSameScreen_ButAHealthyGame_IsNot()
        {
            Assert.IsFalse(Facts("Assets/Scenes/New/Loading.unity", 0, 7)["stuckOnBootScene"]!.Value<bool>(), "a slow boot with a clean console is not a fault");
            Assert.IsFalse(Facts("Assets/Scenes/New/Loading.unity", 5, 1)["stuckOnBootScene"]!.Value<bool>(), "a one-scene game stays on its only scene");
            Assert.IsFalse(Facts("Assets/Scenes/New/Lobby.unity", 5, 7)["stuckOnBootScene"]!.Value<bool>(), "it moved on");
        }

        [Test]
        public void WhenTheKitCannotTell_TheFactIsNull_NotFalse()
        {
            var f = Facts(null, null, 7);
            Assert.AreEqual(JTokenType.Null, f["stuckOnBootScene"]!.Type);
            Assert.AreEqual(JTokenType.Null, f["consoleErrors"]!.Type);
            foreach (var k in new[] { "activeScene", "stuckOnBootScene", "consoleErrors", "firstConsoleError", "gitBranch", "gitCommit" })
                Assert.IsTrue(f.ContainsKey(k), k + " is always present");
        }

        [Test]
        public void TheConsoleWatch_CountsErrorsOnly_SkipsTheKitsOwn_AndScrubsPaths()
        {
            PlayConsoleWatch.Reset();
            PlayConsoleWatch.OnLog("fine", "", LogType.Log);
            PlayConsoleWatch.OnLog("careful", "", LogType.Warning);
            PlayConsoleWatch.OnLog("[RecorderKit] our own line", "", LogType.Error);
            var project = Path.GetDirectoryName(Application.dataPath)!;
            PlayConsoleWatch.OnLog("could not load " + project + "/Assets/x.asset\nsecond line", "", LogType.Error);
            PlayConsoleWatch.OnLog("boom", "", LogType.Exception);
            PlayConsoleWatch.OnLog("login failed https://api.example.com/v1/login?token=abc123&uid=9 for " + new string('f', 40), "", LogType.Error);
            Assert.AreEqual(3, PlayConsoleWatch.Errors);
            Assert.AreEqual("could not load <project>/Assets/x.asset second line", PlayConsoleWatch.First);
            Assert.AreEqual("login failed https://api.example.com/v1/login?… for <redacted>",
                PlayConsoleWatch.Scrub("login failed https://api.example.com/v1/login?token=abc123&uid=9 for " + new string('f', 40)),
                "a URL keeps host + path, never its query; a long opaque run is redacted");
            PlayConsoleWatch.Reset();
            Assert.AreEqual(0, PlayConsoleWatch.Errors);
        }

        [Test]
        public void AnEditorScriptsError_IsNotTheGamesError()
        {
            PlayConsoleWatch.Reset();
            // RL's real line, from Assets/Editor/Localization/CjkFontBootstrap.cs via an editor delayCall
            PlayConsoleWatch.OnLog("[CjkFontBootstrap] source font missing: Assets/Fonts/CJK/NotoSansJP-Bold.otf",
                "UnityEngine.Debug:LogError (object)\nAssets.Editor.Localization.CjkFontBootstrap:Execute () (at Assets/Editor/Localization/CjkFontBootstrap.cs:78)\nUnityEditor.EditorApplication:Internal_CallDelayFunctions ()", LogType.Error);
            Assert.AreEqual(0, PlayConsoleWatch.Errors);
            // RL's real GAME error, from runtime code
            PlayConsoleWatch.OnLog("Exception in sending request https://x/v0595_login_dev/login: HTTP/1.1 404 Not Found",
                "UnityEngine.Debug:LogError (object)\nPLogger:LogError (string) (at Assets/Scripts/Utility/PLogger.cs:85)", LogType.Error);
            Assert.AreEqual(1, PlayConsoleWatch.Errors);
            StringAssert.Contains("404", PlayConsoleWatch.First);
            PlayConsoleWatch.Reset();
        }

        [Test]
        public void LeftBootAfterSec_IsAlwaysPresent()
        {
            var f = SelfTestFacts.Build("0.9.1", "6000.0.63f1", 4.2, 162, 26.3f, 1f, true, true, true, "UnityRecorderDriver",
                null, false, "Assets/Scenes/New/Loading.unity", 0, null, true, null,
                activeScene: "Assets/Scenes/New/Lobby.unity", consoleErrors: 0, enabledSceneCount: 7, leftBootAfterSec: 14.2);
            Assert.AreEqual(14.2, f["leftBootAfterSec"]!.Value<double>(), 0.001);
            Assert.IsFalse(f["stuckOnBootScene"]!.Value<bool>());
        }

        [Test]
        public void GitHead_ReadsABranch_ALooseAndAPackedRef_ADetachedHead_AndAWorktreeFile()
        {
            var root = Path.Combine(Path.GetTempPath(), "novakit-git-" + Path.GetRandomFileName());
            var sha1 = new string('1', 40); var sha2 = new string('2', 40);
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "repo", ".git", "refs", "heads"));
                File.WriteAllText(Path.Combine(root, "repo", ".git", "HEAD"), "ref: refs/heads/main\n");
                File.WriteAllText(Path.Combine(root, "repo", ".git", "refs", "heads", "main"), sha1 + "\n");
                Assert.AreEqual(("main", sha1), GitHead.Read(Path.Combine(root, "repo")));

                File.WriteAllText(Path.Combine(root, "repo", ".git", "HEAD"), "ref: refs/heads/packed\n");
                File.WriteAllText(Path.Combine(root, "repo", ".git", "packed-refs"), "# pack-refs\n" + sha2 + " refs/heads/packed\n");
                Assert.AreEqual(("packed", sha2), GitHead.Read(Path.Combine(root, "repo")));

                File.WriteAllText(Path.Combine(root, "repo", ".git", "HEAD"), sha1 + "\n");
                Assert.AreEqual(((string?)null, sha1), GitHead.Read(Path.Combine(root, "repo")));

                Directory.CreateDirectory(Path.Combine(root, "wt"));
                File.WriteAllText(Path.Combine(root, "wt", ".git"), "gitdir: " + Path.Combine(root, "repo", ".git") + "\n");
                Assert.AreEqual(((string?)null, sha1), GitHead.Read(Path.Combine(root, "wt")));

                Assert.AreEqual(((string?)null, (string?)null), GitHead.Read(Path.Combine(root, "nothing-here")));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }
}
