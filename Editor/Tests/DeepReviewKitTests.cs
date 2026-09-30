using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>A game type for the scope tests: lives in the test assembly, which the one-time setup names runnable.</summary>
    public static class ScopeFixtureGame
    {
        public static int Gold;
        public static int AddGold(int n) => Gold += n;
    }

    internal static class SharedFixture
    {
        /// <summary>A shared cases file under Editor/Tests/Fixtures — FAIL, never skip, when it is missing.</summary>
        internal static JObject Load(string file)
        {
            var relative = "Editor/Tests/Fixtures/" + file;
            string? path = null;
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ReflectionScope).Assembly);
            if (info != null && !string.IsNullOrEmpty(info.resolvedPath) && File.Exists(Path.Combine(info.resolvedPath, relative)))
                path = Path.Combine(info.resolvedPath, relative);
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            for (var i = 0; path == null && i < 6 && dir != null; i++, dir = dir.Parent)
            {
                var guess = Path.Combine(dir.FullName, "com.projectnova.recorder-kit", relative);
                if (File.Exists(guess)) path = guess;
            }
            Assert.IsNotNull(path, "the shared fixture was not found (" + relative + ")");
            return NovaJson.ParseObject(File.ReadAllText(path!));
        }
    }

    /// <summary>KIT-2 (invariant 192) — a call or a set reaches only the game's own code, by any door.</summary>
    public class ReflectionScopeTests
    {
        [Test]
        public void TheNameRuleIsTheSharedFixtures()
        {
            var f = SharedFixture.Load("runner-scope.cases.json");
            CollectionAssert.AreEqual(f["blockedNamespaces"]!.Values<string>().ToList(), ReflectionScope.BlockedNamespaces);
            CollectionAssert.AreEqual(f["engineMembers"]!.Values<string>().ToList(), ReflectionScope.EngineMembers);
            var failures = new List<string>();
            var cases = (JArray)f["cases"]!;
            Assert.GreaterOrEqual(cases.Count, 15);
            foreach (var c in cases)
            {
                var command = c["command"]!.Value<string>()!;
                var tickable = c["tickable"]!.Value<bool>();
                // by NAME: the rule TickList refuses a whole file on, and the website runs too
                var why = Levers.NotTickableReason(command);
                if ((why == null) != tickable) failures.Add($"'{command}': expected tickable={tickable}, NotTickableReason says {why ?? "null"}");
            }
            Assert.IsEmpty(failures, string.Join("\n", failures));
        }

        [Test]
        public void TheResolvedHalfRefusesABareRuntimeNameAndAllowsTheEngineMembers()
        {
            StringAssert.StartsWith(ReflectionScope.RefusalStart, ReflectionScope.ResolvedRefusal("call Process.Start /bin/sh"),
                "a bare Process resolves to System.Diagnostics.Process");
            StringAssert.StartsWith(ReflectionScope.RefusalStart, ReflectionScope.ResolvedRefusal("call File.WriteAllText /tmp/x y"));
            StringAssert.StartsWith(ReflectionScope.RefusalStart, ReflectionScope.ResolvedRefusal("set Application.targetFrameRate 5"));
            Assert.IsNull(ReflectionScope.ResolvedRefusal("set Time.timeScale 0"), "UnityEngine.Time.timeScale is an engine member");
            Assert.IsNull(ReflectionScope.ResolvedRefusal("set AudioListener.volume 0"));
            Assert.IsNull(ReflectionScope.ResolvedRefusal($"call {typeof(ScopeFixtureGame).FullName}.AddGold 5"), "the game's own type");
            Assert.IsNull(ReflectionScope.ResolvedRefusal("call NoSuchTypeAnywhere_zz.Go"), "nothing resolves: nothing would run");
        }

        /// <summary>Through the executor itself — the door every command ends at. Before 0.14.3 this call returned true.</summary>
        [Test]
        public void TryCallAndTrySetPathRefuseTheRuntimeAndTheEngine()
        {
            var target = Path.Combine(Path.GetTempPath(), "nova-scope-" + Guid.NewGuid().ToString("N") + ".txt");
            Assert.IsFalse(GameReflection.TryCall(typeof(File), "WriteAllText", new[] { target, "pwned" }, out _, out var error));
            Assert.IsFalse(File.Exists(target), "System.IO.File.WriteAllText ran");
            StringAssert.Contains("the game's own code", error);
            Assert.IsFalse(GameReflection.TryCall(typeof(UnityEditor.EditorApplication), "Beep", Array.Empty<string>(), out _, out _));
            Assert.IsFalse(GameReflection.TryCallGeneric(typeof(Enumerable), "Empty", new[] { typeof(int) }, Array.Empty<object?>(), out _, out _));
            var was = UnityEngine.Application.runInBackground;
            Assert.IsFalse(GameReflection.TrySetPath("UnityEngine.Application.runInBackground", was ? "true" : "false", out var setError));
            StringAssert.Contains("the game's own code", setError);
            // CONTROLS: the game's own type runs; an engine member a shot may write is written
            ScopeFixtureGame.Gold = 0;
            Assert.IsTrue(GameReflection.TryCall(typeof(ScopeFixtureGame), "AddGold", new[] { "5" }, out _, out var ok1), ok1);
            Assert.AreEqual(5, ScopeFixtureGame.Gold);
            var volume = UnityEngine.AudioListener.volume;
            try
            {
                Assert.IsTrue(GameReflection.TrySetPath("UnityEngine.AudioListener.volume", volume.ToString(System.Globalization.CultureInfo.InvariantCulture), out var ok2), ok2);
            }
            finally { UnityEngine.AudioListener.volume = volume; }
        }

        /// <summary>The identity control: without the tests' seam, the test assembly is NOT the game — the rule refuses it.</summary>
        [Test]
        public void TheTestAssemblyItselfIsRefusedWithoutTheSeam()
        {
            var asm = typeof(ScopeFixtureGame).Assembly;
            Assert.IsTrue(ReflectionScope.AlsoRunnableForTests.Remove(asm), "control: the one-time setup named it");
            try
            {
                Assert.IsFalse(GameReflection.TryCall(typeof(ScopeFixtureGame), "AddGold", new[] { "1" }, out _, out var error));
                StringAssert.Contains("ProjectNova.RecorderKit.Editor.Tests", error);
            }
            finally { ReflectionScope.AlsoRunnableForTests.Add(asm); }
        }

        /// <summary>The window's one writer never ticks a command whose type resolves outside the game — before 0.14.3 it did.</summary>
        [Test]
        public void TheWindowWillNotTickARuntimeCommand()
        {
            var root = Path.Combine(Path.GetTempPath(), "nova-scope-root-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RelayPaths.NovaDir(root));
            try
            {
                StringAssert.StartsWith(ReflectionScope.RefusalStart, Levers.SetApproved(root, "call Process.Start /bin/sh", true));
                StringAssert.StartsWith(ReflectionScope.RefusalStart, Levers.SetApproved(root, "call System.Diagnostics.Process.Start /bin/sh", true));
                CollectionAssert.IsEmpty(Levers.Approved(root));
                Assert.IsNull(Levers.SetApproved(root, $"call {typeof(ScopeFixtureGame).FullName}.AddGold 5", true), "control");
            }
            finally { Directory.Delete(root, true); }
        }

        /// <summary>MED-1 (kit 0.14.3) — a player-assembly read that throws once is not remembered as "no game assemblies".</summary>
        [Test]
        public void APlayerAssemblyReadThatThrowsIsNotCached_TheNextAskReadsAgain_AndItWarnsOnce()
        {
            var answers = 0;
            ReflectionScope.ForgetPlayerAssembliesForTests();
            ReflectionScope.PlayerAssemblyNamesForTests = () =>
            {
                answers++;
                if (answers <= 2) throw new InvalidOperationException("mid-compile");
                return new[] { "Game.Runtime" };
            };
            try
            {
                LogAssert.Expect(UnityEngine.LogType.Warning, new System.Text.RegularExpressions.Regex("could not be read \\(mid-compile\\)"));
                Assert.IsFalse(ReflectionScope.IsPlayerAssemblyForTests("Game.Runtime"), "fail closed while it cannot be read");
                Assert.IsFalse(ReflectionScope.IsPlayerAssemblyForTests("Game.Runtime"), "still closed — and no second warning");
                Assert.IsTrue(ReflectionScope.IsPlayerAssemblyForTests("Game.Runtime"), "read again once it can be — no domain reload needed");
                Assert.AreEqual(3, answers);
                Assert.IsTrue(ReflectionScope.IsPlayerAssemblyForTests("Game.Runtime"));
                Assert.AreEqual(3, answers, "a list that was read IS cached");
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                ReflectionScope.PlayerAssemblyNamesForTests = null;
                ReflectionScope.ForgetPlayerAssembliesForTests();
            }
        }
    }

    /// <summary>KIT-12 — a run id becomes a folder on this disk and a URL segment: refused unless it is a plain token.</summary>
    public class RunIdTests
    {
        [Test]
        public void AJobWhoseRunIdCouldWalkIsRefusedWhereItEnters()
        {
            foreach (var bad in new[] { "../../../Assets", "/Users/x", "..", "a/b", "a\\b", "run.1", "rün", "a b", new string('a', 65) })
            {
                var job = CaptureJob.FromJson(new JObject { ["runId"] = bad, ["kind"] = "teach" }, out var error);
                Assert.IsNull(job, $"'{bad}' was taken as a run id");
                StringAssert.Contains("not a run id this kit takes", error, bad);
            }
            var claim = CaptureJob.ParseClaim("{\"job\":{\"runId\":\"../../Assets\",\"kind\":\"teach\"}}", out var claimError);
            Assert.IsNull(claim);
            Assert.IsNotNull(claimError);
            // CONTROL: the API's cuids and the tests' ids
            foreach (var good in new[] { "cmu8u9tma000b1t4nv5rqwbmo", "run-1", "run_42", "r1" })
                Assert.IsNotNull(CaptureJob.FromJson(new JObject { ["runId"] = good, ["kind"] = "teach" }, out _), good);
            Assert.AreEqual("Assets", CapturePaths.SafeSegment("../../Assets"));
        }

        [Test]
        public void APlantedProgressFileWithAWalkingRunIdIsNotAJob()
        {
            var file = Path.Combine(Path.GetTempPath(), "nova-progress-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var job = CaptureJob.FromJson(new JObject { ["runId"] = "run-1", ["kind"] = "teach" }, out _)!;
                var text = CaptureProgress.Start(job).ToJson().Replace("\"run-1\"", "\"../../Assets\"");
                File.WriteAllText(file, text);
                Assert.IsNull(CaptureProgress.Load(file));
            }
            finally { File.Delete(file); }
        }
    }

    /// <summary>KIT-11 — the claim is read by the kit's one reader: a date-shaped shot name stays the name.</summary>
    public class ClaimDatesTests
    {
        [Test]
        public void AClaimsDateShapedShotNameStaysAString()
        {
            var claim = "{\"job\":{\"runId\":\"r1\",\"kind\":\"capture\",\"workspaceId\":\"ws1\"," +
                        "\"items\":[{\"shot\":\"2026-09-21\",\"take\":1}]},\"leaseUntil\":\"2026-09-29T10:00:00Z\"}";
            var job = CaptureJob.ParseClaim(claim, out var error);
            Assert.IsNull(error);
            Assert.AreEqual("2026-09-21", job!.Items.Single().Shot);
            var list = CaptureJob.ParseList("{\"jobs\":[{\"runId\":\"r1\",\"kind\":\"capture\",\"items\":[{\"shot\":\"2026-09-21T10:00:00Z\",\"take\":1}]}]}", out var listError);
            Assert.IsNull(listError);
            Assert.AreEqual("2026-09-21T10:00:00Z", list.Single().Items.Single().Shot);
            Assert.AreEqual("2026-09-21", RelayEnvelope.ParseCommand("c1", "{\"action\":\"run-shot\",\"args\":{\"name\":\"2026-09-21\"}}")!.Args["name"]!.Value<string>());
        }
    }

    /// <summary>KIT-13 — a shot name is a file name: the site's rule, in the kit's loader.</summary>
    public class ShotNameTests
    {
        private static string Doc(string name) =>
            "{\"$schemaVersion\":1,\"shots\":[{\"name\":" + new JValue(name).ToString(Newtonsoft.Json.Formatting.None) +
            ",\"steps\":[{\"kind\":\"wait\",\"seconds\":1}],\"settle\":{\"kind\":\"present\",\"name\":\"X\"}}]}";

        [Test]
        public void AShotNameThatWalksOrSplitsIsRefusedAtLoad_NeverThrown()
        {
            foreach (var bad in new[] { "../../../../Users/x/Desktop", "a/b", "a\\b", "a b", ".hidden", "-x", "a\"b", "é" })
            {
                ShotLoadResult? r = null;
                Assert.DoesNotThrow(() => r = JsonShotLoader.LoadFrom(Doc(bad)), bad);
                CollectionAssert.IsEmpty(r!.Shots, $"'{bad}' loaded");
                Assert.AreEqual($"shots[0]: {JsonShotLoader.UnsafeShotNameWords(bad)}", r.Errors.Single(), bad);
            }
            foreach (var good in new[] { "board_bigwin", "board-news-inbox", "lobby.shop-2", "2026-09-21", "A" })
                Assert.AreEqual(good, JsonShotLoader.LoadFrom(Doc(good)).Shots.Single().Name, good);
        }

        [Test]
        public void TheFallbackRecorderWillNotStartOnAnUnsafeName()
        {
            var d = new ScreenCaptureDriver();
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex("refusing to record"));
            d.Start("../../x");
            Assert.IsFalse(d.IsRecording);
        }
    }

    /// <summary>KIT-5 — the studio key goes only over https (or to this machine).</summary>
    public class StudioUrlTests
    {
        [Test]
        public void TheKeyGoesOnlyOverHttpsOrToThisMachine()
        {
            Assert.IsNull(StudioUrl.KeyRefusal("https://admiral-ads.fly.dev"));
            Assert.IsNull(StudioUrl.KeyRefusal("https://admiral-ads.fly.dev/studio/me"));
            Assert.IsNull(StudioUrl.KeyRefusal("http://localhost:3011/studio/me"));
            Assert.IsNull(StudioUrl.KeyRefusal("http://127.0.0.1:3011/studio/me"));
            Assert.IsNull(StudioUrl.KeyRefusal("http://[::1]:3011/studio/me"));
            foreach (var bad in new[] { "http://admiral-ads.fly.dev/studio/me", "http://192.168.1.4/studio/me", "ftp://x/y", "admiral-ads.fly.dev", "", "http://localhost.evil.com/x" })
                Assert.IsNotNull(StudioUrl.KeyRefusal(bad), bad);
        }

        /// <summary>Through the transport: an http URL answers at once, as a transport failure, and nothing is sent.
        /// Before 0.14.3 the request went out (IsDone false, the key on it).</summary>
        [Test]
        public void AnHttpCallCarryingTheKeyIsNeverSent()
        {
            var http = new UnityStudioHttp();
            using (var r = http.Get("http://nova.invalid/studio/me", "sk_test"))
            {
                Assert.IsTrue(r.IsDone, "the request was sent");
                Assert.AreEqual(0, r.Status);
                StringAssert.Contains("https://", r.TransportError);
            }
            using (var r = http.PostJson("http://nova.invalid/studio/capture-jobs/r1/claim", "sk_test", "{}"))
                Assert.IsTrue(r.IsDone && r.TransportError != null);
        }
    }

    /// <summary>KIT-6 / KIT-14 — the kinds that need a bound project, shared with the API.</summary>
    public class JobKindsSharedCasesTests
    {
        [Test]
        public void TheKindsAreTheSharedFixtures()
        {
            var f = SharedFixture.Load("job-kinds.cases.json");
            var kinds = f["kinds"]!.Values<string>().ToList();
            foreach (var k in kinds) Assert.IsTrue(CaptureJob.IsHandled(k!), $"the API hands out '{k}' and this kit has no handler");
            foreach (var k in new[] { "capture ", "Capture", "frobnicate", "" }) Assert.IsFalse(CaptureJob.IsHandled(k), k);
            CollectionAssert.AreEquivalent(f["kindsNeedingBinding"]!.Values<string>().ToList(), WorkspaceGuard.KindsNeedingBinding);
        }

        [Test]
        public void AnUnboundProjectRefusesEveryKindThatNeedsTheBinding()
        {
            foreach (var kind in WorkspaceGuard.KindsNeedingBinding)
            {
                StringAssert.Contains("bound to its workspace", WorkspaceGuard.KindRefusal(kind, "ws1", null), kind);
                StringAssert.Contains("names no workspace", WorkspaceGuard.KindRefusal(kind, "", "ws1"), kind);
                StringAssert.Contains("different workspace", WorkspaceGuard.KindRefusal(kind, "ws2", "ws1"), kind);
                Assert.IsNull(WorkspaceGuard.KindRefusal(kind, "ws1", "ws1"), kind);
            }
            // CONTROL: the kinds that predate the binding stay account-wide, as the API hands them
            foreach (var kind in new[] { CaptureJob.CaptureKind, CaptureJob.SelfTestKind })
            {
                Assert.IsNull(WorkspaceGuard.KindRefusal(kind, "ws1", null), kind);
                Assert.IsNull(WorkspaceGuard.KindRefusal(kind, null, "ws1"), kind);
                Assert.IsNotNull(WorkspaceGuard.KindRefusal(kind, "ws2", "ws1"), kind);
            }
        }
    }

    /// <summary>KIT-7 — an unreadable progress file is a lost job, set aside, never silently overwritten.</summary>
    public class ProgressQuarantineTests
    {
        private string _dir = "";

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "nova-quarantine-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown() => Directory.Delete(_dir, true);

        [Test]
        public void AnUnreadableProgressFileIsSetAside_WithItsPinNamed()
        {
            var file = Path.Combine(_dir, "capture-status.json");
            File.WriteAllText(file, "{\"runId\":\"r1\",\"items\":[{\"shot\":7}],\"playModeStartSceneSet\": true");
            var now = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
            var aside = CaptureProgress.QuarantineUnreadable(file, now, out var pinned);
            Assert.IsNotNull(aside);
            Assert.IsFalse(File.Exists(file), "the lost job's file is still where the next claim would write over it");
            Assert.IsTrue(File.Exists(aside!), "nothing is deleted — it is kept for a person to read");
            StringAssert.StartsWith(file + ".corrupt-20260929T120000", aside);
            Assert.IsTrue(pinned, "its text said the kit had pinned the start scene");

            File.WriteAllText(file, "not json at all");
            Assert.IsNotNull(CaptureProgress.QuarantineUnreadable(file, now.AddSeconds(1), out var pinned2));
            Assert.IsFalse(pinned2, "no pin in its text: the studio's own start scene is never cleared blindly");
        }

        [Test]
        public void AReadableOrAbsentProgressFileIsLeftAlone()
        {
            var file = Path.Combine(_dir, "capture-status.json");
            Assert.IsNull(CaptureProgress.QuarantineUnreadable(file, DateTime.UtcNow, out _), "absent");
            var job = CaptureJob.FromJson(new JObject { ["runId"] = "r1", ["kind"] = "capture", ["items"] = new JArray(new JObject { ["shot"] = "s", ["take"] = 1 }) }, out _)!;
            CaptureProgress.Start(job).Save(file);
            Assert.IsNull(CaptureProgress.QuarantineUnreadable(file, DateTime.UtcNow, out _));
            Assert.IsTrue(File.Exists(file));
        }
    }

    /// <summary>KIT-16 — a recording item that aborts every time is failed at the cap, and the job moves past it.</summary>
    public class CaptureAbortBoundTests
    {
        [Test]
        public void AnItemThatAbortsEveryTimeIsFailedAtTheCap_AndTheCountSurvivesAReload()
        {
            var file = Path.Combine(Path.GetTempPath(), "nova-aborts-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var items = new JArray(new JObject { ["shot"] = "a", ["take"] = 1 }, new JObject { ["shot"] = "b", ["take"] = 1 });
                var job = CaptureJob.FromJson(new JObject { ["runId"] = "r1", ["kind"] = "capture", ["items"] = items }, out _)!;
                var p = CaptureProgress.Start(job);
                for (var i = 1; i < 3; i++)
                {
                    Assert.IsFalse(p.NoteCaptureAbort("File.ReadAllBytes threw", 3), $"abort {i}");
                    p.Save(file);
                    p = CaptureProgress.Load(file)!; // each abort is followed by a reload-surviving retry
                }
                Assert.AreEqual(0, p.NextIndex);
                Assert.IsTrue(p.NoteCaptureAbort("File.ReadAllBytes threw", 3), "the third abort fails the item");
                Assert.AreEqual(1, p.NextIndex, "the job moved past it");
                Assert.AreEqual(1, p.Failed);
                StringAssert.Contains("aborted on this take 3 times — File.ReadAllBytes threw", p.DoneReportJson());
                // the next item starts its own count
                Assert.IsFalse(p.NoteCaptureAbort("x", 3));
                Assert.AreEqual(1, p.ItemAborts);
            }
            finally { File.Delete(file); }
        }
    }

    /// <summary>KIT-15 — the static cheat search runs off the editor's main thread.</summary>
    public class CheatSearchThreadTests
    {
        [Test]
        public void TheStaticSearchRunsOnAWorker_AndFindsWhatTheInlineOneFinds()
        {
            var fixtures = typeof(ScopeFixtureGame).Assembly;
            var inline = CheatStaticSearch.Run(new[] { fixtures });
            var task = CheatStaticSearch.Start(new[] { fixtures });
            Assert.IsTrue(task.Wait(TimeSpan.FromSeconds(60)), "the worker never finished");
            Assert.AreNotEqual(Environment.CurrentManagedThreadId, CheatStaticSearch.LastRunThreadForTests,
                "the search ran on the editor's thread");
            CollectionAssert.AreEqual(inline.Scan.Findings.Select(f => f.Command).ToList(),
                task.Result.Scan.Findings.Select(f => f.Command).ToList());
        }
    }

    /// <summary>KIT-14 — CheatRisk's lists are the shared press-risk fixture's.</summary>
    public class CheatRiskSharedCasesTests
    {
        [Test]
        public void TheCheatRootsAndRiskyKindsAreTheFixtures()
        {
            var f = SharedFixture.Load("press-risk.cases.json");
            CollectionAssert.AreEqual(f["cheatRoots"]!.Values<string>().ToList(), CheatRisk.CheatRoots);
            CollectionAssert.AreEqual(f["riskyKinds"]!.Values<string>().ToList(), CheatRisk.RiskyKinds);
        }
    }

    /// <summary>KIT-14 — RelayPaths is the relay-paths fixture's, the one the website's relay-protocol.spec.ts reads.</summary>
    public class RelayPathsSharedCasesTests
    {
        [Test]
        public void EveryPathIsTheFixtures()
        {
            var f = SharedFixture.Load("relay-paths.cases.json");
            var of = new Dictionary<string, Func<string, string>>
            {
                ["root"] = RelayPaths.Root,
                ["commands"] = RelayPaths.Commands,
                ["results"] = RelayPaths.Results,
                ["processed"] = RelayPaths.Processed,
                ["screenshots"] = RelayPaths.Screenshots,
                ["visionRequests"] = RelayPaths.VisionRequests,
                ["visionResponses"] = RelayPaths.VisionResponses,
                ["statusFile"] = RelayPaths.StatusFile,
                ["command(c1)"] = r => RelayPaths.Command(r, "c1"),
                ["result(c1)"] = r => RelayPaths.Result(r, "c1"),
            };
            var cases = (JArray)f["cases"]!;
            Assert.GreaterOrEqual(cases.Count, 3);
            var failures = new List<string>();
            foreach (var c in cases)
            {
                var project = Path.Combine(Path.GetTempPath(), "nova-relay-paths-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(project);
                try
                {
                    var at = c["statusAt"]?.Type == JTokenType.String ? c["statusAt"]!.Value<string>()! : "";
                    foreach (var rel in at.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var full = Path.Combine(project, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                        File.WriteAllText(full, "{}");
                    }
                    foreach (var p in ((JObject)c["paths"]!).Properties())
                    {
                        Assert.IsTrue(of.ContainsKey(p.Name), $"the fixture names '{p.Name}', which this side has no path for");
                        var got = Path.GetRelativePath(project, of[p.Name](project)).Replace(Path.DirectorySeparatorChar, '/');
                        if (got != p.Value.Value<string>()) failures.Add($"{c["name"]}: {p.Name} = {got}, the fixture says {p.Value}");
                    }
                }
                finally { Directory.Delete(project, true); }
            }
            Assert.IsEmpty(failures, string.Join("\n", failures));
        }
    }

    /// <summary>GAP-KIT-3 + GAP-KIT-4 (kit 0.14.3) — the kit's own pictures never land in a pre-0.3 project's project-root
    /// AdRelay/ (the studio's repo tree), leftovers there are swept, and a screen check's .jpg copy is swept too.</summary>
    public class KitWorkDirTests
    {
        private string _root = "";

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "nova-workdir-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_root, true); } catch (Exception) { }
        }

        [Test]
        public void ALegacyRelayProjectsFramesLiveUnderLibrary_AndLeftoversInTheRepoTreeAreSwept()
        {
            var legacy = Path.Combine(_root, "AdRelay");
            Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, "status.json"), "{}");
            Assert.AreEqual(legacy, RelayPaths.Root(_root), "control: the relay's own traffic still follows the legacy root");

            var library = Path.Combine(_root, "Library", "AdRelay");
            Assert.AreEqual(library, Path.GetDirectoryName(ProbeRun.FramePath(_root, "run-1")), "a try's frame");
            Assert.AreEqual(library, RelayPaths.KitWorkDir(_root), "the screen check's and the self-test's frame folder (the agent passes it)");
            Assert.AreEqual(Path.Combine(library, NoRecordingDriver.DirName), new NoRecordingDriver(_root).OutputDir);
            StringAssert.StartsWith(library, CapturePaths.ExportRoot(_root));

            // what an earlier kit left in the repo tree: the kit's own frame names only
            var leftovers = new[] { "probe-frame-old.png", "probe-frame-old-vision-q.jpg", "self-test-frame-old.png" }
                .Select(n => Path.Combine(legacy, n)).ToArray();
            var studios = new[] { Path.Combine(legacy, "status.json"), Path.Combine(legacy, "notes.png") };
            foreach (var f in leftovers.Concat(studios)) File.WriteAllBytes(f, new byte[] { 1 });
            // and in Library: a screen check's .jpg copy an interrupted run left (GAP-KIT-4)
            Directory.CreateDirectory(library);
            var jpg = Path.Combine(library, "probe-frame-run-2-vision-q.jpg");
            File.WriteAllBytes(jpg, new byte[] { 1 });

            var gone = ProbeRun.SweepStrayFrames(_root, null);
            foreach (var f in leftovers) Assert.IsFalse(File.Exists(f), f);
            Assert.IsFalse(File.Exists(jpg), "the .jpg copy is swept like its .png");
            foreach (var f in studios) Assert.IsTrue(File.Exists(f), "never anything else: " + f);
            Assert.AreEqual(4, gone.Count);
            // nothing there at all: nothing thrown
            CollectionAssert.IsEmpty(ProbeRun.SweepStrayFrames(Path.Combine(_root, "nowhere"), null));
        }
    }

    /// <summary>The 0.14.3 gap review's cheap LOWs that have a behaviour to hold.</summary>
    public class DeepReviewGapLowTests
    {
        /// <summary>GAP-KIT-10 — the window redraws by itself at most four times a second, not on every editor update.</summary>
        [Test]
        public void TheWindowRedrawsOnATimer_NotOnEveryEditorUpdate()
        {
            var last = double.MinValue;
            var redraws = 0;
            for (var t = 100.0; t < 101.0; t += 0.01) // 100 editor updates in one second
                if (NovaCaptureWindow.RepaintDue(t, ref last)) redraws++;
            Assert.AreEqual(4, redraws, "every 0.25 s");
            Assert.IsTrue(NovaCaptureWindow.RepaintDue(5.0, ref last), "a clock that went back redraws at once");
        }

        /// <summary>GAP-KIT-19 — a terminal's teach-stop keeps the newest presses files, not every one ever written.</summary>
        [Test]
        public void TheRelaysPressesFilesAreKeptToTheNewest_AndNothingElseIsTouched()
        {
            var dir = Path.Combine(Path.GetTempPath(), "nova-presses-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                for (var i = 0; i < 25; i++) File.WriteAllText(Path.Combine(dir, $"presses-20260929-10{i:00}00.json"), "{}");
                File.WriteAllText(Path.Combine(dir, "recipe.json"), "{}");
                Assert.AreEqual(5, TeachRecorder.PrunePresses(dir, TeachRecorder.KeepPresses));
                var left = Directory.GetFiles(dir, "presses-*.json").Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList();
                Assert.AreEqual(20, left.Count);
                Assert.AreEqual("presses-20260929-100500.json", left.First(), "the five oldest went");
                Assert.AreEqual("presses-20260929-102400.json", left.Last());
                Assert.IsTrue(File.Exists(Path.Combine(dir, "recipe.json")), "only presses-*.json");
                Assert.AreEqual(0, TeachRecorder.PrunePresses(Path.Combine(dir, "nowhere"), 20), "a folder that is not there: nothing thrown");
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
